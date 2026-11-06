// Fork (docs/specs/ui-gallery.md): `NeoBuzzLive.exe --ui-shots <folder>`, the Windows app's half of the UI gallery for
// reviews. The app starts as it always does (main window, Gear with the natives, themes), loads the demo song both galleries
// share (GallerySong.cs), and draws its windows to <folder>/win-NNN-*.png at 1440×900: the main window in every view,
// the main menu's menus, the Machine View's context menus, both shipped themes, Preferences and Settings (every page),
// About, the agent connection, rename, the parameter windows of native and Pedal machines, a Neo synth's editor and the
// machine keyboard under machine windows, the tool windows. Then
// index-windows.md (what each shows, how a user gets there) and done.txt, and the app ends. ReBuzzTests'
// UiGalleryTests runs it on the package run; the pictures go to the ci-artifacts branch with the others.
//
// The window's client area is drawn (RenderTargetBitmap, 96 dpi): no Windows title bar. Menus and context menus are
// popups (windows of their own): drawn over it where Windows put them. Message boxes are captured to the log
// (NonModal), never shown: nobody answers them here.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.AppViews;
using ReBuzz.Common;
using ReBuzz.Core;
using ReBuzz.Gallery;

namespace ReBuzz
{
    public partial class MainWindow
    {
        internal const string UiShotsSwitch = "--ui-shots";

        /// <summary>The folder after --ui-shots on the command line, or null.</summary>
        internal static string UiShotsFolder
        {
            get
            {
                var args = Environment.GetCommandLineArgs();
                int i = Array.FindIndex(args, a => string.Equals(a, UiShotsSwitch, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
        }

        internal static bool UiShotsRequested => UiShotsFolder != null;

        const double ShotWidth = 1440, ShotHeight = 900;

        sealed class ShotRow
        {
            public string File, What, How, Size, Song, Notes;
        }

        readonly List<ShotRow> shotRows = new List<ShotRow>();
        readonly List<string> shotFailures = new List<string>();
        readonly List<string> shotLog = new List<string>();
        string shotFolder;

        /// <summary>Starts the gallery once the window has loaded, when the command line asks for it.</summary>
        void StartUiShots()
        {
            string folder = UiShotsFolder;
            if (folder == null) return;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _ = RunUiShots(Path.GetFullPath(folder))));
        }

        void ShotLog(string line)
        {
            shotLog.Add($"{DateTime.Now:HH:mm:ss.f} {line}");
            try { File.WriteAllLines(Path.Combine(shotFolder, "ui-shots.log"), shotLog); } catch (Exception) { }
        }

        static async Task Idle(int ms = 400)
        {
            await Task.Delay(ms);
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        // ------------------------------------------------------------------ drawing

        // Windows clamps a window to its screen's size (WM_GETMINMAXINFO): the runner's screen may be smaller than 1440×900.
        [StructLayout(LayoutKind.Sequential)]
        struct ShotPoint { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct ShotMinMaxInfo { public ShotPoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

        static IntPtr LargeWindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                var info = Marshal.PtrToStructure<ShotMinMaxInfo>(lParam);
                info.MaxTrackSize.X = Math.Max(info.MaxTrackSize.X, 4000);
                info.MaxTrackSize.Y = Math.Max(info.MaxTrackSize.Y, 3000);
                info.MaxSize.X = Math.Max(info.MaxSize.X, 4000);
                info.MaxSize.Y = Math.Max(info.MaxSize.Y, 3000);
                Marshal.StructureToPtr(info, lParam, true);
                // Not handled: the window's own handler reads the enlarged size next (it keeps it as its largest).
            }
            return IntPtr.Zero;
        }

        /// <summary>The window's drawn root (its template's, with the theme's background), else its content.</summary>
        static FrameworkElement RootOf(Window w) =>
            (VisualTreeHelper.GetChildrenCount(w) > 0 ? VisualTreeHelper.GetChild(w, 0) as FrameworkElement : null) ?? w.Content as FrameworkElement;

        /// <summary>The open popups (menus, context menus, drop-downs) over <paramref name="root"/>: their root and offset in it.</summary>
        static List<(FrameworkElement Root, Point At)> PopupsOver(FrameworkElement root)
        {
            var found = new List<(FrameworkElement, Point)>();
            if (PresentationSource.FromVisual(root) == null) return found;
            var origin = root.PointToScreen(new Point(0, 0));
            double scale = VisualTreeHelper.GetDpi(root).DpiScaleX;
            foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>())
            {
                if (source.RootVisual is not FrameworkElement popup || popup.GetType().Name != "PopupRoot" || !popup.IsVisible || popup.ActualWidth < 1) continue;
                if (popup.Dispatcher != root.Dispatcher) continue;
                if (Descendants<ToolTip>(popup).Any()) continue;          // a tooltip under wherever the runner's pointer is
                var at = popup.PointToScreen(new Point(0, 0));
                found.Add((popup, new Point((at.X - origin.X) / scale, (at.Y - origin.Y) / scale)));
            }
            return found;
        }

        static VisualBrush BrushOf(FrameworkElement e) => new VisualBrush(e)
        {
            Viewbox = new Rect(0, 0, e.ActualWidth, e.ActualHeight),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };

        /// <summary>A window drawn as PNG bytes (its popups over it), on its own thread; and its size.</summary>
        static (byte[] Png, int Width, int Height, string Size) Draw(Window w, bool popups = true)
        {
            w.UpdateLayout();
            var root = RootOf(w);
            root.UpdateLayout();
            double width = Math.Max(1, root.ActualWidth), height = Math.Max(1, root.ActualHeight);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(w.Background ?? SystemColors.WindowBrush, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(BrushOf(root), null, new Rect(0, 0, width, height));
                if (popups)
                {
                    foreach (var (popup, at) in PopupsOver(root))
                    {
                        if (at.X >= width || at.Y >= height || at.X + popup.ActualWidth <= 0 || at.Y + popup.ActualHeight <= 0) continue;
                        dc.DrawRectangle(BrushOf(popup), null, new Rect(at.X, at.Y, popup.ActualWidth, popup.ActualHeight));
                    }
                }
            }
            int pw = (int)Math.Ceiling(width), ph = (int)Math.Ceiling(height);
            var bitmap = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return (stream.ToArray(), pw, ph, $"window {w.ActualWidth:0}×{w.ActualHeight:0}, client {pw}×{ph} px, 96 dpi (100 %)");
        }

        /// <summary>A shot of a window: win-<paramref name="id"/>.png and its row in the index.</summary>
        async Task Shot(Window w, string id, string what, string how, string song = GallerySong.Title, string notes = "", int settle = 400)
        {
            string file = $"win-{id}.png";
            try
            {
                await Idle(settle);
                var (png, pw, ph, size) = w.Dispatcher == Dispatcher ? Draw(w) : w.Dispatcher.Invoke(() => Draw(w, popups: false));
                File.WriteAllBytes(Path.Combine(shotFolder, file), png);
                shotRows.RemoveAll(r => r.File == file);
                shotRows.Add(new ShotRow { File = file, What = what, How = how, Size = size, Song = song, Notes = notes });
                ShotLog($"{file}: {pw}×{ph}, {png.Length / 1024} KB");
            }
            catch (Exception e)
            {
                shotFailures.Add($"{file}: {e.GetType().Name}: {e.Message}");
                ShotLog($"{file} failed: {e}");
            }
        }

        /// <summary>A sentence on the machine keyboard under a machine's window, where it has one (a machine that plays notes).</summary>
        static string KeyboardUnder(MachineCore m)
        {
            var route = ReBuzz.Core.Keyboards.KeyboardRoute.For(m);
            if (!route.Shows) return "";
            return route.Input == ReBuzz.Core.Keyboards.KeyboardInput.Midi
                ? " Under it, the machine keyboard: it plays the machine by MIDI, as a MIDI keyboard does."
                : " Under it, the machine keyboard: it plays the machine's Note track parameter, a note per track.";
        }

        /// <summary>Runs a step whose shots may fail on their own: a failure is listed and the gallery goes on.</summary>
        async Task Step(string name, Func<Task> step)
        {
            try
            {
                await step();
            }
            catch (Exception e)
            {
                string at = string.Join(" < ", (e.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("at ")).Take(3));
                shotFailures.Add($"{name}: {e.GetType().Name}: {e.Message} ({at})");
                ShotLog($"{name} failed: {e}");
            }
        }

        [DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        void WriteShotIndex()
        {
            static string Cell(string s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
            var sb = new StringBuilder();
            sb.AppendLine("| App | File | What it shows | How a user reaches it | Window size and scale | Song | Notes |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in shotRows.OrderBy(r => r.File, StringComparer.Ordinal))
                sb.AppendLine($"| Windows ({Product.WindowsExe}, WPF) | {Cell(r.File)} | {Cell(r.What)} | {Cell(r.How)} | {Cell(r.Size)} | {Cell(r.Song)} | {Cell(r.Notes)} |");
            if (shotFailures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Not captured:");
                foreach (var f in shotFailures) sb.AppendLine("- " + f);
            }
            File.WriteAllText(Path.Combine(shotFolder, "index-windows.md"), sb.ToString());
        }

        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) yield return t;
                foreach (var d in Descendants<T>(child)) yield return d;
            }
        }

        /// <summary>Renders the engine offline when no audio device runs it (the runner has none), as the audio thread would.</summary>
        void RunEngine(int ticks)
        {
            if (Buzz.AudioEngine?.SelectedOutDevice != null) return;
            shotWork ??= new ReBuzz.Audio.WorkManager(Buzz, new ReBuzz.Audio.WorkThreadEngine(1), 0, Global.EngineSettings);
            var buffer = new float[512];
            for (int i = 0; i < ticks; i++)
            {
                var mi = ReBuzzCore.masterInfo;
                int frames = Math.Max(1, mi.SamplesPerTick - mi.PosInTick);
                while (frames > 0)
                {
                    int n = Math.Min(frames, 256);
                    lock (ReBuzzCore.AudioLock) shotWork.MainAudioFillBuffer(buffer, 0, n * 2);
                    frames -= n;
                }
            }
        }

        ReBuzz.Audio.WorkManager shotWork;

        /// <summary>Scrolls a view's scrollable areas to their right end (and <paramref name="down"/> of the way down).</summary>
        static void ScrollRight(FrameworkElement view, double down = 0)
        {
            if (view == null) return;
            view.UpdateLayout();
            foreach (var scroll in Descendants<ScrollViewer>(view))
            {
                if (scroll.ScrollableWidth > 0) scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth);
                if (down > 0 && scroll.ScrollableHeight > 0) scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * down);
            }
            foreach (var bar in Descendants<ScrollBar>(view).Where(b => b.Orientation == Orientation.Horizontal && b.Maximum > b.Minimum))
                bar.Value = bar.Maximum;
            if (down > 0)
                foreach (var bar in Descendants<ScrollBar>(view).Where(b => b.Orientation == Orientation.Vertical && b.Maximum > b.Minimum))
                    bar.Value = bar.Minimum + (bar.Maximum - bar.Minimum) * down;
            view.UpdateLayout();
        }

        async Task CloseMenus()
        {
            foreach (var menu in Descendants<MenuItem>(this).Where(m => m.IsSubmenuOpen).ToList()) menu.IsSubmenuOpen = false;
            if (MachineView?.ContextMenu != null) MachineView.ContextMenu.IsOpen = false;
            foreach (var mc in MachineView?.Machines ?? Enumerable.Empty<BuzzGUI.MachineView.MachineControl>())
                if (mc.ContextMenu != null) mc.ContextMenu.IsOpen = false;
            await Idle(200);
        }

        // ------------------------------------------------------------------ the gallery

        async Task RunUiShots(string folder)
        {
            shotFolder = folder;
            Directory.CreateDirectory(folder);
            ShotLog($"ui shots: screen {SystemParameters.PrimaryScreenWidth}×{SystemParameters.PrimaryScreenHeight}, {Buzz.MachineDLLs.Count} machines in Gear, audio {(Buzz.AudioEngine?.SelectedOutDevice?.Name ?? "none")}");
            try
            {
                await RunGallery();
            }
            catch (Exception e)
            {
                shotFailures.Add("the gallery stopped: " + e.Message);
                ShotLog("the gallery stopped: " + e);
            }
            finally
            {
                try
                {
                    Buzz.SelectedTheme = "<default>";
                }
                catch (Exception) { }
                WriteShotIndex();
                ShotLog($"done: {shotRows.Count} shots, {shotFailures.Count} not captured");
                File.WriteAllText(Path.Combine(folder, "done.txt"), $"{shotRows.Count} shots, {shotFailures.Count} not captured");
                Buzz.Modified = false;
                Process.GetCurrentProcess().Kill();
            }
        }

        async Task RunGallery()
        {
            // The window at 1440×900, at the screen's corner, however small the screen.
            WindowState = WindowState.Normal;
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(LargeWindowHook);
            Left = 0;
            Top = 0;
            Width = ShotWidth;
            Height = ShotHeight;
            await Idle(300);
            Width = ShotWidth;
            Height = ShotHeight;
            // The runner's pointer away from the window (else a tooltip waits under it).
            SetCursorPos((int)SystemParameters.PrimaryScreenWidth - 1, (int)SystemParameters.PrimaryScreenHeight - 1);
            Buzz.ActiveView = BuzzView.MachineView;
            await Idle(1500);
            ShotLog($"window {ActualWidth}×{ActualHeight}");

            const string Empty = "none (new song)";
            await Shot(this, "010-start-empty", "The main window on a new song: menu, toolbar, Machine View with the Master alone, status bar.", $"Start {Product.Name} (or File ▸ New, Ctrl+N).", Empty,
                "The window's client area (no Windows title bar).");

            // The demo song, saved (the title names it). Not opened again: the notes moved onto the note lanes show in the
            // Pattern View as they are written (they showed only once the song was opened again before).
            var built = GallerySong.Build(Buzz);
            foreach (var p in built.Problems) ShotLog("demo song: " + p);
            string songFolder = Path.Combine(Path.GetTempPath(), "rebuzz-ui-shots-" + Environment.ProcessId);
            Directory.CreateDirectory(songFolder);
            string songPath = Path.Combine(songFolder, GallerySong.Title + ".bmxml");
            Buzz.SaveSongFile(songPath);
            Buzz.SongCore.SongName = songPath;      // the song's file now, as Save As makes it (a named path doesn't)
            Buzz.Modified = false;
            await Idle(2000);
            var song = GallerySong.Find(Buzz);
            ShotLog($"demo song: {Buzz.SongCore.MachinesList.Count} machines, saved as {Buzz.SongCore.SongName}");

            // Every view.
            await Step("views", async () =>
            {
                Buzz.ActiveView = BuzzView.MachineView;
                await Shot(this, "020-machines", "Machine View: the demo song's machines and wires (Neo generators and effects, FSM Infector and Jeskola Delay natives).", "F3, or the toolbar.", settle: 800);
                var lead = MachineView.GetMachineControl(song.Lead);
                if (lead != null)
                {
                    MachineView.SetSelection(lead, false);
                    await Shot(this, "021-machines-selected", "Machine View with Lead selected.", "Click a machine.");
                }

                Buzz.SetPatternEditorPattern(song.LineA);
                Buzz.ActivatePatternEditor();
                await Shot(this, "030-pattern", "Pattern View: Bass's Line A in the Modern Pattern Editor.", "F2, or double-click a pattern in the Sequence View.", settle: 1200);
                ScrollRight(EditorView);
                await Shot(this, "032-pattern-lanes", "Pattern View: Bass's Line A scrolled right, to its MIDI note lanes (the notes, velocities, delays).", "F2, then scroll right (or Tab to the tracks).", settle: 500);
                Buzz.SetPatternEditorPattern(song.Arp);
                Buzz.ActivatePatternEditor();
                await Idle(1000);
                ScrollRight(EditorView, 0.5);
                await Shot(this, "033-pattern-long-scrolled", "Pattern View: Lead's 8-bar Arp (128 rows) scrolled to its note lanes and halfway down.", "Scroll a long pattern.", settle: 500);
                Buzz.SetPatternEditorPattern(song.Beat);
                Buzz.ActivatePatternEditor();
                await Shot(this, "031-pattern-drums", "Pattern View: Drums' Beat.", "F2, then choose the machine and pattern.", settle: 1000);

                Buzz.ActiveView = BuzzView.SequenceView;
                await Shot(this, "040-sequence", "Sequence View: the 16-bar arrangement, one track a generator.", "F4.", settle: 800);

                Buzz.ActiveView = BuzzView.WaveTableView;
                await Shot(this, "050-wavetable", "Wavetable View with its file browser panel (no waves loaded in the demo song).", "F9.", settle: 1000);

                Buzz.ActiveView = BuzzView.SongInfoView;
                await Shot(this, "060-song-info", "Song Notes (F10): the demo song's notes (Markdown) in the editor and as they read, with the formatting toolbar; the Mac app's Song Notes view. Buzz's old info text comes in as an imported note.", "F10.", settle: 800);

                Buzz.ActiveView = BuzzView.SessionView;
                await Shot(this, "070-session", "Session View docked in the main window (stopped): the demo song's scenes, clips, colours, mixer.", "Ctrl+Tab, or View ▸ Session View.", settle: 1000);
                Buzz.SessionLayout.LaunchScene(2);
                Buzz.Playing = true;
                await Idle(300);
                RunEngine(20);
                await Shot(this, "071-session-playing", "Session View playing: the Drop scene launched and playing.", "Click a scene's ▶ (or its number key).",
                    notes: Buzz.AudioEngine?.SelectedOutDevice == null ? "The runner has no audio device: the engine was run offline for the picture." : "", settle: 800);
                Buzz.Playing = false;
                RunEngine(2);
                Buzz.Playing = false;
                await Idle(300);
                Buzz.SongCore.PlayPosition = 0;
                Buzz.BPM = 124;                 // the Drop scene's tempo, put back
                Buzz.ActiveView = BuzzView.MachineView;
                await Idle(500);
            });

            // The main menu's menus.
            await Step("main menu", async () =>
            {
                var menu = Descendants<Menu>(ToolBarControl).FirstOrDefault();
                if (menu == null) throw new InvalidOperationException("no main menu in the toolbar");
                for (int i = 0; i < menu.Items.Count; i++)
                {
                    if (menu.ItemContainerGenerator.ContainerFromIndex(i) is not MenuItem item) continue;
                    string header = (item.Header ?? (menu.Items[i] as MenuItemVM)?.Text ?? $"menu {i}").ToString().Replace("_", "");
                    if (menu.Items[i] is MenuItemVM vm && vm.Text != null) header = vm.Text.Replace("_", "");
                    item.IsSubmenuOpen = true;
                    await Shot(this, $"{100 + i:000}-menu-{header.ToLowerInvariant()}", $"The main menu's {header} menu.", $"{header} in the menu bar (Alt).", notes: "The menu is a popup, drawn over the window where Windows put it.");
                    item.IsSubmenuOpen = false;
                    await Idle(200);
                }
            });

            // The Machine View's context menus.
            await Step("machine menus", async () =>
            {
                Buzz.ActiveView = BuzzView.MachineView;
                await Idle(400);
                async Task MachineMenu(IMachine m, string id, string what)
                {
                    var control = MachineView.GetMachineControl(m);
                    if (control == null) { shotFailures.Add($"win-{id}: {m?.Name} has no control"); return; }
                    control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonDownEvent });
                    var menu = control.ContextMenu;
                    if (menu == null) { shotFailures.Add($"win-{id}: no context menu"); return; }
                    menu.PlacementTarget = control;
                    menu.Placement = PlacementMode.Relative;
                    menu.HorizontalOffset = control.ActualWidth / 2;
                    menu.VerticalOffset = control.ActualHeight / 2;
                    menu.IsOpen = true;
                    await Shot(this, id, what, "Right-click the machine.", notes: "The menu is a popup, drawn over the window where Windows put it.");
                    menu.IsOpen = false;
                    await Idle(200);
                }
                await MachineMenu(song.Bass, "200-machines-menu-generator", "Machine View: a generator's context menu (Bass, Neo Poly).");
                await MachineMenu(song.Drive, "201-machines-menu-effect", "Machine View: an effect's context menu (Drive, Neo Drive).");
                if (song.NativeGenerator != null) await MachineMenu(song.NativeGenerator, "202-machines-menu-native", $"Machine View: a native machine's context menu ({song.NativeGenerator.Name}, {song.NativeGenerator.DLL.Name}).");
                await MachineMenu(song.Master, "203-machines-menu-master", "Machine View: the Master's context menu.");
                if (MachineView.ContextMenu != null)
                {
                    var canvas = MachineView.ContextMenu;
                    canvas.PlacementTarget = MachineView;
                    canvas.Placement = PlacementMode.Relative;
                    canvas.HorizontalOffset = 300;
                    canvas.VerticalOffset = 620;
                    canvas.IsOpen = true;
                    await Shot(this, "204-machines-menu-canvas", "Machine View: the menu on empty space (New ▸ the machines by kind, and the view's commands).", "Right-click empty space in the Machine View.",
                        notes: "The menu is a popup, drawn over the window where Windows put it.");
                    var newItem = Descendants<MenuItem>(canvas).FirstOrDefault();
                    if (newItem != null && newItem.HasItems)
                    {
                        newItem.IsSubmenuOpen = true;
                        await Idle(300);
                        var generators = Descendants<MenuItem>(newItem).FirstOrDefault();
                        if (generators == null)
                        {
                            foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>())
                                if (source.RootVisual is FrameworkElement f && f.GetType().Name == "PopupRoot" && f.IsVisible)
                                    generators ??= Descendants<MenuItem>(f).FirstOrDefault(x => x != newItem && x.HasItems);
                        }
                        if (generators != null) generators.IsSubmenuOpen = true;
                        await Shot(this, "205-machines-menu-new", "Machine View: New ▸ and its first kind's machines (the machine browser).", "Right-click empty space ▸ New.",
                            notes: "The menus are popups, drawn over the window where Windows put them.");
                    }
                    canvas.IsOpen = false;
                    await Idle(200);
                }
                await CloseMenus();
            });

            // Preferences, every page.
            await Step("preferences", async () =>
            {
                var prefs = new PreferencesWindow(Buzz, registryEx) { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
                prefs.Resources.MergedDictionaries.Add(UiUtils.GetUserControlXAML<ResourceDictionary>("MachineView\\MVResources.xaml", buzzPath));
                prefs.Show();
                try
                {
                    int i = 0;
                    foreach (var tab in prefs.tc.Items.OfType<TabItem>().ToList())
                    {
                        prefs.tc.SelectedItem = tab;
                        string header = tab.Header?.ToString() ?? $"page {i}";
                        string slug = new string(header.ToLowerInvariant().Replace(' ', '-').Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
                        await Shot(prefs, $"{400 + i++:000}-preferences-{slug}", $"Preferences ▸ {header} (OK applies, Cancel drops).", $"View ▸ Preferences… ▸ {header}.", settle: 600);
                    }
                }
                finally
                {
                    prefs.Close();
                }
            });

            // Settings (BuzzGUI's), every page, Agent among them (its Port a text box: a free value has no options).
            await Step("settings", async () =>
            {
                Buzz.ActiveView = BuzzView.MachineView;
                await Idle(500);
                SettingsWindow.Show(MachineView, null);
                await Idle(600);
                var settings = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? throw new InvalidOperationException("Settings didn't open");
                try
                {
                    var tabs = Descendants<TabControl>(settings).FirstOrDefault() ?? throw new InvalidOperationException("no tabs");
                    int i = 0;
                    foreach (var tab in tabs.Items.OfType<TabItem>().ToList())
                    {
                        tabs.SelectedItem = tab;
                        string header = tab.Header?.ToString() ?? $"page {i}";
                        string slug = new string(header.ToLowerInvariant().Replace(' ', '-').Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
                        await Shot(settings, $"{430 + i++:000}-settings-{slug}", $"Settings ▸ {header} (a drop-down per setting, its default in bold; a text box for a free value such as Agent's Port).", $"View ▸ Settings… ▸ {header}.", settle: 500);
                    }
                }
                finally
                {
                    settings.Close();
                }
            });

            // About, the agent connection, rename.
            await Step("about", async () =>
            {
                var about = new AboutWindow(Buzz, "(build " + Buzz.BuildNumber + ")") { Owner = this };
                var shell = UiUtils.GetUserControlXAML<Window>("ParameterWindowShell.xaml", buzzPath);
                about.Resources.MergedDictionaries.Add(shell.Resources);
                about.Show();
                await Shot(about, "500-about", $"About {Product.Name}: the build and the credits.", $"Help ▸ About {Product.Name}….", settle: 800);
                about.Close();
            });
            await Step("agent connection", async () =>
            {
                studioAgent?.ShowConnection();
                await Idle(500);
                var agent = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "Agent connection") ?? throw new InvalidOperationException("the agent connection didn't open");
                await Shot(agent, "501-agent-connection", "The agent connection window: how to connect Claude Code (agents off here: Settings ▸ Agent ▸ AllowAgents turns them on).", "Ctrl+Shift+A.");
                agent.Close();
            });
            await Step("rename", async () =>
            {
                var rename = new RenameMachineWindow("Rename Machine", song.Lead.Name, false) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                rename.Show();
                await Shot(rename, "502-rename-machine", "Rename a machine.", "Right-click a machine ▸ Rename… (or F2 on it).");
                rename.Close();
            });

            // Parameter windows: a native generator, a native effect, a Pedal machine (full builds), a Neo machine.
            await Step("parameter windows", async () =>
            {
                async Task Parameters(MachineCore m, string id, string what)
                {
                    if (m == null) return;
                    Buzz.UiHost.OpenParameterWindow(m, null);
                    await Idle(600);
                    var w = WpfMachineUiHost.ParameterWindowOf(m);
                    if (w == null) { shotFailures.Add($"win-{id}: no parameter window"); return; }
                    await Shot(w, id, what + KeyboardUnder(m), "Right-click a machine ▸ Parameters… (or double-click one without a GUI).");
                    Buzz.UiHost.CloseWindows(m);
                    await Idle(200);
                }
                await Parameters(song.NativeGenerator, "600-parameters-native-generator", $"The parameter window of a native generator ({song.NativeGenerator?.DLL.Name}).");
                await Parameters(song.NativeEffect, "601-parameters-native-effect", $"The parameter window of a native effect ({song.NativeEffect?.DLL.Name}).");
                await Parameters(song.Bass, "602-parameters-neo-poly", "The parameter window of Bass (Neo Poly).");
                string pedal = new[] { "Pedal SH101", "Pedal Juno106", "Pedal Comp", "Pedal Plate" }.FirstOrDefault(Buzz.MachineDLLs.ContainsKey);
                if (pedal != null)
                {
                    Buzz.SongCore.CreateMachine(pedal, null, pedal.Replace("Pedal ", ""), null, null, null, -1, 0.5f, 0.8f);
                    var m = Buzz.SongCore.MachinesList.Last(x => x.DLL.Name == pedal);
                    await Parameters(m, "603-parameters-pedal", $"The parameter window of a Pedal machine ({pedal}).");
                }
                else shotFailures.Add("win-603-parameters-pedal: no Pedal machine in Gear (a quick package leaves them out)");
            });

            // A Neo synth's editor and the machine keyboard under machine windows (docs/specs/studio-mvp.md §3.12).
            await Step("machine keyboard", async () =>
            {
                async Task<(Window, NeoBuzz.Editor.MachineKeyboard)> Gui(MachineCore m)
                {
                    Buzz.UiHost.OpenGui(m, null);
                    await Idle(800);
                    // As the app shows it: inside the screen's work area (MachineWindowFit), a taller editor scrolled above the keys.
                    var w = (Window)WpfMachineUiHost.GuiWindowOf(m) ?? throw new InvalidOperationException($"{m.Name}'s editor didn't open");
                    return (w, WpfMachineUiHost.KeyboardOf(w) ?? throw new InvalidOperationException($"no keyboard under {m.Name}'s editor"));
                }

                var (bass, kb) = await Gui(song.Bass);
                await Shot(bass, "610-editor-neo-poly", "Bass's editor (Neo Poly), as a double click opens it: a window of its own, the machine keyboard under it (it plays Bass by MIDI, as a MIDI keyboard does).",
                    "Double-click the machine in the Machine View.");
                // Playing: A4 held with the pointer low on its key (loud), and a chord from a MIDI keyboard lit.
                var chord = new[] { 48, 55, 64 };
                try
                {
                    var rect = kb.Keys.Layout.Rect(69);
                    kb.Keys.Down(rect.X + rect.Width / 2, rect.Y + rect.Height * 0.85);
                    foreach (var note in chord) Buzz.MachineManager.SendMidiInput(song.Bass, 0x90 | (note << 8) | (72 << 16), false);
                    for (int i = 0; i < 4; i++) kb.Poll();
                    await Shot(bass, "611-keyboard-playing", "Bass's editor with its keyboard playing: A4 held with the pointer low on the key (loud: the readout names the note and its velocity, the fill shows it), and C3, G3 and E4 lit from a MIDI keyboard. Keys light whatever plays them (a pointer, MIDI, the machine's own patterns).",
                        "Click and hold a key under a machine's window; drag across the keys to glide. Lower on a key plays louder.", settle: 200);
                }
                finally
                {
                    kb.Keys.Up();
                    foreach (var note in chord) Buzz.MachineManager.SendMidiInput(song.Bass, 0x80 | (note << 8), false);
                    Buzz.UiHost.CloseWindows(song.Bass);
                }
                await Idle(200);

                var (drums, _) = await Gui(song.Drums);
                await Shot(drums, "612-editor-neo-drums", "Drums' editor (Neo Drums) and its keyboard: a drum machine's keys are wider and named for their sounds, those without one dimmed.",
                    "Double-click the machine in the Machine View.");
                Buzz.UiHost.CloseWindows(song.Drums);
                await Idle(200);
            });

            // The tool windows, each on its own.
            await Step("cpu monitor", async () =>
            {
                Buzz.ActiveView = BuzzView.MachineView;
                Buzz.IsCPUMonitorWindowVisible = true;
                await Shot(this, "700-cpu-monitor", "Machine View with the CPU monitor on.", "View ▸ CPU Monitor.", settle: 800);
                Buzz.IsCPUMonitorWindowVisible = false;
            });
            await Step("hard disk recorder", async () =>
            {
                Buzz.IsHardDiskRecorderWindowVisible = true;
                await Idle(600);
                if (hDRecorderWindow == null) throw new InvalidOperationException("the Hard Disk Recorder didn't open");
                await Shot(hDRecorderWindow, "701-hard-disk-recorder", "The Hard Disk Recorder window.", "View ▸ Hard Disk Recorder.",
                    notes: Buzz.AudioEngine?.SelectedOutDevice == null ? "The runner has no audio device: the window says there's nothing to record and waits for one." : "");
                Buzz.IsHardDiskRecorderWindowVisible = false;
            });
            await Step("piano keyboard", async () =>
            {
                Buzz.IsPianoKeyboardVisible = true;
                await Idle(600);
                if (keyboardWindow == null) throw new InvalidOperationException("the keyboard didn't open");
                await Shot(keyboardWindow, "702-piano-keyboard", "The piano keyboard window.", "The toolbar's keyboard button.");
                Buzz.IsPianoKeyboardVisible = false;
            });
            await Step("session window", async () =>
            {
                ToggleSessionWindow();
                await Idle(800);
                if (sessionWindow == null) throw new InvalidOperationException("the Session window didn't open");
                await Shot(sessionWindow, "703-session-window", "The Session View in a window of its own.", "View ▸ Session View in a Window.");
                sessionWindow.Hide();
            });
            await Step("debug console", async () =>
            {
                debugWindow.Show();
                await Idle(600);
                await Shot(debugWindow, "704-debug-console", "The debug console.", "View ▸ Debug Console.", notes: "Its own UI thread: drawn there (no popups).");
                debugWindow.Dispatcher.Invoke(() => debugWindow.Hide());
            });

            // The shipped themes, switched with the demo song open (it threw in the Machine View, made again, before:
            // ui-gallery.md).
            await Step("themes", async () =>
            {
                foreach (var m in Buzz.SongCore.MachinesList.Where(m => !m.Ready && !m.Hidden))
                    ShotLog($"themes: {m.Name} ({m.DLL?.Name}) isn't ready: the Machine View leaves it and its wires out");
                foreach (var theme in Buzz.Themes.Where(t => t != "<default>" && t != "Default").ToList())
                {
                    string slug = new string(theme.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                    Buzz.SelectedTheme = theme;
                    await Idle(1500);
                    Buzz.ActiveView = BuzzView.MachineView;
                    await Shot(this, $"300-theme-{slug}-machines", $"The {theme} theme: Machine View and its toolbar.", $"Theme ▸ {theme}.", settle: 1000);
                    Buzz.ActiveView = BuzzView.SequenceView;
                    await Shot(this, $"301-theme-{slug}-sequence", $"The {theme} theme: Sequence View.", $"Theme ▸ {theme}, then F4.", settle: 800);
                    Buzz.SetPatternEditorPattern(song.LineA);
                    Buzz.ActivatePatternEditor();
                    await Idle(800);
                    ScrollRight(EditorView);
                    await Shot(this, $"302-theme-{slug}-pattern", $"The {theme} theme: Pattern View (Bass's Line A, scrolled to its note lanes).", $"Theme ▸ {theme}, then F2.", settle: 600);
                    Buzz.ActiveView = BuzzView.SessionView;
                    await Shot(this, $"303-theme-{slug}-session", $"The {theme} theme: Session View (its clip colours are the theme's pattern colours).", $"Theme ▸ {theme}, then Ctrl+Tab.", settle: 1000);
                    Buzz.ActiveView = BuzzView.WaveTableView;
                    await Shot(this, $"304-theme-{slug}-wavetable", $"The {theme} theme: Wavetable View with the file browser.", $"Theme ▸ {theme}, then F9.", settle: 1000);
                }
                Buzz.SelectedTheme = "<default>";
                await Idle(1500);
                Buzz.ActiveView = BuzzView.MachineView;
                await Idle(500);
            });
        }
    }
}
