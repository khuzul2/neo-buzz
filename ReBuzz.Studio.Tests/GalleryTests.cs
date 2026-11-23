// Fork (docs/specs/ui-gallery.md): the studio's UI gallery for reviews. Every view, menu, context menu, window and
// dialog of the Mac app that can be reached headless, with the demo song (ReBuzz/AppViews/GallerySong.cs) loaded
// where content matters, drawn at 1440×900 logical points and scale 2 (a Retina Mac), as gallery/mac-NNN-*.png with
// gallery/index-mac.md (what each shows, how a user gets there, notes on the capture). Explicit: normal runs skip it.
//
//   dotnet test ReBuzz.Studio.Tests -c Release --filter TestCategory=Gallery     (GALLERY_DIR: where to; default
//   bin/…/screenshots/gallery)
//
// The window is drawn as macOS has it: no in-window menu (the Mac's menus are in the menu bar, which headless can't
// draw: the menu shots draw the same menu model as an in-window menu instead). Dialogs, which are windows of their
// own, are drawn over the main window, centred, as macOS shows a modal window. Each shot is taken whatever the others
// did: one that fails is listed (index-mac.md, the test's output) and the rest go on; the test then fails.

using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using ReBuzz.Core.AudioLibrary.AudioToMidi;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Keyboards;
using ReBuzz.Core.Session;
using ReBuzz.Gallery;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Preferences;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Sequence;
using ReBuzz.Studio.Views.Session;
using ReBuzzTests.Automation;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    [Category("Gallery")]
    [Explicit("The UI gallery for reviews (docs/specs/ui-gallery.md): dotnet test ReBuzz.Studio.Tests --filter TestCategory=Gallery")]
    public class GalleryTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        // ------------------------------------------------------------------ the gallery

        internal const double Width = 1440, Height = 900, Scale = 2;
        const int MaxBytes = 1_500_000;

        sealed record Row(string File, string What, string How, string Size, string Song, string Notes);

        static readonly List<Row> rows = new();
        static readonly List<string> failures = new();
        static bool installed;
        static readonly List<Window> opened = new();
        static System.Action<Window> onOpened;

        /// <summary>The file prefix and the index's platform: "mac" (the default), or GALLERY_PREFIX=win on the Windows runner (ux-overhaul.md P6c,
        /// the package workflow's studio-windows job: the same headless gallery drawn with Windows' fonts; the file names are win-NNN-*.png).</summary>
        static string Prefix => System.Environment.GetEnvironmentVariable("GALLERY_PREFIX") is { Length: > 0 } p ? p : "mac";
        static string PlatformName => Prefix == "mac" ? "Mac" : Prefix == "win" ? "Windows" : Prefix;

        static string Folder
        {
            get
            {
                string dir = System.Environment.GetEnvironmentVariable("GALLERY_DIR") ?? Path.Combine(Studio.ScreenshotFolder, "gallery");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>Every window drawn at scale 2 from when it opens; the newest window kept (dialogs).</summary>
        static void Install()
        {
            if (installed) return;
            installed = true;
            // Fork (ux-overhaul.md P4b): GALLERY_THEME=light draws every screen in the Light theme (the dark theme is the default).
            if (System.Environment.GetEnvironmentVariable("GALLERY_THEME") is { } theme && theme.Equals("light", System.StringComparison.OrdinalIgnoreCase))
                ReBuzz.Studio.Shell.Design.StudioTheme.Kind = ReBuzz.Studio.Shell.Design.AppearanceTheme.Light;
            Window.WindowOpenedEvent.AddClassHandler<Window>((w, e) =>
            {
                opened.RemoveAll(x => !x.IsVisible);
                opened.Add(w);
                w.SetRenderScaling(Scale);
                onOpened?.Invoke(w);
            }, RoutingStrategies.Direct, handledEventsToo: true);
        }

        static void Settle()
        {
            for (int i = 0; i < 2; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Lets the views' timers run (meters, play cursors, the status line) for about <paramref name="ms"/>.</summary>
        static void Wait(int ms)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Thread.Sleep(15);
                Settle();
            }
        }

        /// <summary>Run just before a frame is captured: a live input keeps feeding so its meter is lit in the shot.</summary>
        static System.Action LiveBeforeCapture;

        static Bitmap Frame(TopLevel top)
        {
            foreach (var v in top.GetVisualDescendants()) v.InvalidateVisual();
            Settle();
            LiveBeforeCapture?.Invoke();
            var frame = top.CaptureRenderedFrame();
            Assert.That(frame, Is.Not.Null, "the window drew");
            return frame!;
        }

        static string SizeText(TopLevel top, Bitmap frame) =>
            $"{top.ClientSize.Width:0}×{top.ClientSize.Height:0} pt, scale {top.RenderScaling:0.#} ({frame.PixelSize.Width}×{frame.PixelSize.Height} px)";

        static void Save(Bitmap frame, string file)
        {
            string path = Path.Combine(Folder, file);
            frame.Save(path);
            if (new FileInfo(path).Length > MaxBytes)
            {
                // Over the size a review page takes well: the same picture at three quarters (never seen at 2880×1800).
                var smaller = frame.CreateScaledBitmap(new PixelSize(frame.PixelSize.Width * 3 / 4, frame.PixelSize.Height * 3 / 4), BitmapInterpolationMode.HighQuality);
                smaller.Save(path);
            }
        }

        static void Record(string file, string what, string how, string size, string song, string notes)
        {
            rows.RemoveAll(r => r.File == file);
            rows.Add(new Row(file, what, how, size, song, notes));
            WriteIndex();
        }

        static void WriteIndex()
        {
            static string Cell(string s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ");
            var sb = new StringBuilder();
            sb.AppendLine("| App | File | What it shows | How a user reaches it | Window size and scale | Song | Notes |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in rows.OrderBy(r => r.File, System.StringComparer.Ordinal))
                sb.AppendLine($"| {PlatformName} ({Product.Name}, headless) | {Cell(r.File)} | {Cell(r.What)} | {Cell(r.How)} | {Cell(r.Size)} | {Cell(r.Song)} | {Cell(r.Notes)} |");
            if (failures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Not captured:");
                foreach (var f in failures) sb.AppendLine("- " + f);
            }
            File.WriteAllText(Path.Combine(Folder, $"index-{Prefix}.md"), sb.ToString());
        }

        const string Headless = "Headless (Avalonia.Headless, Skia): the window's content only, no macOS title bar; text in the headless fallback font, greyscale-antialiased.";

        /// <summary>A shot of a window: mac-<paramref name="id"/>.png.</summary>
        /// <summary>Preferences ▸ Plug-ins over a plug-in library written for the picture (a data folder of its own; nothing is scanned).</summary>
        static void PluginsGallery(PreferencesWindow prefs, string id, bool withBlocked, string what, string how)
        {
            string data = TestTemp.Path("gallery-plugin-data-" + Environment.ProcessId);
            string vst3 = Path.Combine(TestTemp.Path("gallery-plugins-" + Environment.ProcessId), "VST3");
            string clap = Path.Combine(TestTemp.Path("gallery-plugins-" + Environment.ProcessId), "CLAP");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(vst3);
            Directory.CreateDirectory(clap);
            string[] keep = { "NEOBUZZ_PLUGIN_DIR", "NEOBUZZ_PLUGIN_STANDARD", "NEOBUZZ_PLUGIN_PATH" };
            var old = keep.Select(Environment.GetEnvironmentVariable).ToArray();
            try
            {
                Environment.SetEnvironmentVariable("NEOBUZZ_PLUGIN_DIR", data);
                Environment.SetEnvironmentVariable("NEOBUZZ_PLUGIN_STANDARD", "0");
                Environment.SetEnvironmentVariable("NEOBUZZ_PLUGIN_PATH", vst3);
                File.WriteAllLines(Path.Combine(data, "folders.txt"), new[] { clap });
                string Entry(string format, string name, string vendor, bool instrument, string module) =>
                    $"{{\"Format\":\"{format}\",\"Path\":{System.Text.Json.JsonSerializer.Serialize(module)},\"Id\":\"{name}\",\"Name\":\"{name}\",\"Vendor\":\"{vendor}\",\"Instrument\":{(instrument ? "true" : "false")},\"Display\":\"{vendor}/{name} ({format.ToUpperInvariant()})\",\"Problem\":\"\"}}";
                string m1 = Path.Combine(vst3, "Wavetable Synth.vst3"), m2 = Path.Combine(vst3, "Plate Reverb.vst3"), m3 = Path.Combine(clap, "Tape Delay.clap");
                string modules = string.Join(",", new[]
                {
                    $"{{\"Path\":{System.Text.Json.JsonSerializer.Serialize(m1)},\"Size\":1,\"Modified\":1,\"Classes\":[{Entry("vst3", "Wavetable Synth", "Example Audio", true, m1)}]}}",
                    $"{{\"Path\":{System.Text.Json.JsonSerializer.Serialize(m2)},\"Size\":1,\"Modified\":1,\"Classes\":[{Entry("vst3", "Plate Reverb", "Example Audio", false, m2)}]}}",
                    $"{{\"Path\":{System.Text.Json.JsonSerializer.Serialize(m3)},\"Size\":1,\"Modified\":1,\"Classes\":[{Entry("clap", "Tape Delay", "Example Audio", false, m3)}]}}",
                });
                string blocked = withBlocked
                    ? $"{{\"Path\":{System.Text.Json.JsonSerializer.Serialize(Path.Combine(vst3, "Unstable Chorus.vst3"))},\"Size\":1,\"Modified\":1,\"Reason\":\"crashed while scanning (exit code 139)\",\"When\":\"\"}}," +
                      $"{{\"Path\":{System.Text.Json.JsonSerializer.Serialize(Path.Combine(clap, "Needs A Licence.clap"))},\"Size\":1,\"Modified\":1,\"Reason\":\"does not load: the licence file is missing\",\"When\":\"\"}}"
                    : "";
                File.WriteAllText(Path.Combine(data, "library.json"), $"{{\"Format\":1,\"Modules\":[{modules}],\"Blocked\":[{blocked}]}}");
                NeoBuzz.Plug.PlugLibrary.Reload();
                prefs.ShowPage("Plug-ins");
                prefs.Page<PluginsPage>().Show();
                Settle();
                Shot(prefs, id, what, how, notes: "The library and its folders are written for the picture; the plug-ins are not real ones.");
            }
            finally
            {
                for (int i = 0; i < keep.Length; i++) Environment.SetEnvironmentVariable(keep[i], old[i]);
                NeoBuzz.Plug.PlugLibrary.Reload();
                prefs.Page<PluginsPage>().Show();
            }
        }

        static void Shot(TopLevel top, string id, string what, string how, string song = GallerySong.Title, string notes = null)
        {
            string file = $"{Prefix}-{id}.png";
            var frame = Frame(top);
            Save(frame, file);
            Record(file, what, how, SizeText(top, frame), song, notes ?? "");
        }

        /// <summary>A dialog (a window of its own) drawn over the main window, centred, with a shadow, as macOS shows it.</summary>
        static void DialogShot(Window main, Window dialog, string id, string what, string how, string song = GallerySong.Title, string notes = null)
        {
            string file = $"{Prefix}-{id}.png";
            var back = Frame(main);
            var front = Frame(dialog);
            var size = back.PixelSize;
            using var target = new RenderTargetBitmap(size, new Vector(96, 96));
            using (var dc = target.CreateDrawingContext())
            {
                // Source rectangles in pixels: the frames are 192 dpi, and DrawImage's default source (Size, in points) would take a quarter.
                dc.DrawImage(back, new Rect(0, 0, size.Width, size.Height), new Rect(0, 0, size.Width, size.Height));
                double x = System.Math.Round((size.Width - front.PixelSize.Width) / 2.0), y = System.Math.Round((size.Height - front.PixelSize.Height) / 3.0);
                var r = new Rect(x, y, front.PixelSize.Width, front.PixelSize.Height);
                for (int i = 1; i <= 12; i++)
                    dc.DrawRectangle(new SolidColorBrush(Colors.Black, 0.035), null, r.Inflate(i * 2).Translate(new Vector(0, i)), 12 + i, 12 + i);
                dc.DrawImage(front, new Rect(0, 0, front.PixelSize.Width, front.PixelSize.Height), r);
                dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)), 2), r);
            }
            Save(target, file);
            Record(file, what, how, $"{SizeText(main, back)}; dialog {SizeText(dialog, front)}", song,
                (notes == null ? "" : notes + " ") + "The dialog is its own window: drawn here over the main window, centred, with a shadow (the harness's composite; macOS adds its title bar).");
        }

        /// <summary>A sentence on the machine keyboard under a machine's window, where it has one (a machine that plays notes).</summary>
        static string KeyboardUnder(MachineCore m)
        {
            var route = KeyboardRoute.For(m);
            if (!route.Shows) return "";
            return route.Input == KeyboardInput.Midi
                ? " Under it, the machine keyboard: it plays the machine by MIDI, as a MIDI keyboard does."
                : " Under it, the machine keyboard: it plays the machine's Note track parameter, a note per track.";
        }

        /// <summary>Takes a shot, or lists why not and goes on.</summary>
        static void Try(string id, System.Action shot)
        {
            try
            {
                shot();
            }
            catch (System.Exception e)
            {
                string why = $"{Prefix}-{id}: {e.GetType().Name}: {e.Message.Split('\n')[0]}";
                failures.Add(why);
                TestContext.Out.WriteLine("[gallery] not captured: " + why + "\n" + e.StackTrace);
                WriteIndex();
            }
            finally
            {
                foreach (var w in Popups().ToList()) w.Close();
                CloseMenus();
                ResetViewState();
                Settle();
            }
        }

        /// <summary>
        /// What a shot typed or opened must not show in the next (mac-341 showed the device view's search still holding "cutoff"): the device
        /// view's search and the browser's search are emptied after every shot, whether it was taken or failed.
        /// </summary>
        static void ResetViewState()
        {
            foreach (var w in mains.OfType<MainWindow>().ToList())
            {
                try
                {
                    if (w.Detail?.DevicePage is ReBuzz.Studio.Views.Device.DeviceDock { Page: { } page } && page.Parameters.Query.Length > 0) page.Parameters.Query = "";
                    if (w.Browser?.Search is { } search && (search.Text ?? "").Length > 0) search.Text = "";
                }
                catch (System.Exception e)
                {
                    TestContext.Out.WriteLine("[gallery] view state not reset: " + e.Message);
                }
            }
        }

        /// <summary>Runs shots with the song's clip and track colours at "none" (what P3h gives the machine's identity colour), then puts them back.</summary>
        static void WithMachineColours(System.Action shots)
        {
            var layout = Core.SessionLayout;
            var doc = layout.Ensure();
            var patterns = Core.SongCore.Machines.SelectMany(m => m.Patterns).ToList();
            var clips = patterns.ToDictionary(p => p, p => ReBuzz.Core.Session.PatternColors.Get(p));
            var tracks = Enumerable.Range(0, doc.Tracks.Count).Select(t => doc.Tracks[t].ColorIndex).ToList();
            foreach (var p in patterns) layout.SetClipColor(p, -1);
            for (int t = 0; t < tracks.Count; t++) layout.SetTrackColor(t, -1);
            Settle();
            try
            {
                shots();
            }
            finally
            {
                foreach (var (p, c) in clips) layout.SetClipColor(p, c);
                for (int t = 0; t < tracks.Count; t++) layout.SetTrackColor(t, tracks[t]);
                Settle();
            }
        }

        static List<Window> mains = new();

        /// <summary>Dialogs and tool windows a shot left open (not main windows or Preferences).</summary>
        static IEnumerable<Window> Popups() => opened.Where(w => w.IsVisible && w is not MainWindow && w is not PreferencesWindow).ToList();

        /// <summary>
        /// A dialog that <paramref name="trigger"/> opens, drawn over <paramref name="main"/> as soon as it opens and then
        /// closed (Cancel): from inside the dialog's own modal frame where it has one (the engine's questions wait in one).
        /// </summary>
        static void DialogOnOpen(Window main, System.Action trigger, string id, string what, string how, string song = GallerySong.Title, string notes = null)
        {
            System.Exception error = null;
            bool done = false;
            onOpened = w =>
            {
                onOpened = null;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        CloseMenus();           // the menu that opened it has gone by the time a user sees the dialog
                        Settle();
                        DialogShot(main, w, id, what, how, song, notes);
                    }
                    catch (System.Exception e)
                    {
                        error = e;
                    }
                    finally
                    {
                        w.Close();
                        done = true;
                    }
                }, DispatcherPriority.Background);
            };
            try
            {
                trigger();
                for (int i = 0; i < 40 && !done; i++) Settle();
            }
            finally
            {
                onOpened = null;
            }
            if (error != null) throw error;
            if (!done) throw new AssertionException("the dialog didn't open");
        }

        static readonly List<ContextMenu> menus = new();

        static void CloseMenus()
        {
            foreach (var m in menus) m.Close();
            menus.Clear();
            foreach (var w in mains) foreach (var mi in w.GetVisualDescendants().OfType<MenuItem>().Where(i => i.IsSubMenuOpen)) mi.Close();
        }

        static int failuresBefore;

        /// <summary>At a test's start: its failures are the ones listed after this.</summary>
        static void BeforeAll() => failuresBefore = failures.Count;

        static void AfterAll()
        {
            WriteIndex();
            TestContext.Out.WriteLine($"[gallery] {rows.Count} shots in {Folder}");
            var mine = failures.Skip(failuresBefore).ToList();
            if (mine.Count > 0) Assert.Fail($"{mine.Count} shot(s) not captured:\n" + string.Join("\n", mine));
        }

        // ------------------------------------------------------------------ the main window and the song

        static WorkManager work;
        readonly float[] buffer = new float[512];

        /// <summary>Renders whole ticks offline (no audio device here), as the audio thread would.</summary>
        void RunTicks(int ticks)
        {
            work ??= new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            for (int i = 0; i < ticks; i++)
            {
                var mi = ReBuzzCore.masterInfo;
                int frames = System.Math.Max(1, mi.SamplesPerTick - mi.PosInTick);
                while (frames > 0)
                {
                    int n = System.Math.Min(frames, buffer.Length / 2);
                    lock (ReBuzzCore.AudioLock) work.MainAudioFillBuffer(buffer, 0, n * 2);
                    frames -= n;
                }
            }
            Dispatcher.UIThread.RunJobs();
        }

        static string SongFolder
        {
            get
            {
                string dir = TestTemp.Path("rebuzz-gallery-" + System.Environment.ProcessId);
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>A task's result, the dispatcher running meanwhile (a blocking wait could deadlock on the UI thread); up to a minute.</summary>
        static T Await<T>(System.Threading.Tasks.Task<T> task)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 60)
            {
                Thread.Sleep(10);
                Settle();
            }
            Assert.That(task.IsCompleted, Is.True, "it finished within a minute");
            return task.Result;
        }

        static void NewSong()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.ExecuteCommand(BuzzCommand.Stop);
            Core.Recording = false;
            Core.Modified = false;
            Core.ExecuteCommand(BuzzCommand.NewFile);
            Settle();
            Dialogs.Responder = null;
        }

        /// <summary>
        /// The demo song, built in a new song and saved as "Night Drive.bmxml" (the title names it). Not opened again: the
        /// notes moved onto the note lanes show in the Pattern View as they are written (they showed only once the song was
        /// opened again before; ui-gallery.md).
        /// </summary>
        static GallerySong.Song LoadDemo()
        {
            NewSong();
            var built = GallerySong.Build(Core);
            foreach (var p in built.Problems) TestContext.Out.WriteLine("[gallery] demo song: " + p);
            string path = Path.Combine(SongFolder, GallerySong.Title + ".bmxml");
            SongFiles.Picker = _ => path;
            try
            {
                Assert.That(Await(SongFiles.SaveAs()), Is.True, "the demo song saved");
            }
            finally
            {
                SongFiles.Picker = null;
            }
            Core.Modified = false;
            File.Copy(path, Path.Combine(Folder, GallerySong.Title + ".bmxml"), true);      // beside the pictures (the Mac job opens it in the app)
            Settle();
            return GallerySong.Find(Core);
        }

        /// <summary>The main window at 1440×900, scale 2, as on a Mac: no in-window menu (the menu bar has it).</summary>
        static MainWindow Open(string view = "Machines")
        {
            Install();
            var window = new MainWindow(Studio.Host) { Width = Width, Height = Height };
            window.Show();
            window.SetRenderScaling(Scale);
            if (window.WindowMenu != null) window.WindowMenu.IsVisible = false;
            mains.Add(window);
            Settle();
            window.ShowView(view);
            Settle();
            window.RefreshNow();
            return window;
        }

        static void Close(MainWindow window)
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            foreach (var w in Popups().ToList()) w.Close();
            PreferencesWindow.Current?.Close();
            window.Close();
            mains.Remove(window);
            Settle();
            Dialogs.Responder = null;
            Dialogs.TextResponder = null;
        }

        /// <summary>Whether the machine's DLL has a GUI factory, which is what an editor window is made from.</summary>
        static bool HasEditorWindow(string machine)
        {
            try { return Core.MachineDLLs.TryGetValue(machine, out var dll) && dll?.GUIFactory != null; }
            catch { return false; }
        }

        static T Tab<T>(MainWindow window, string title) where T : class =>
            window.Tabs.Items.OfType<TabItem>().First(t => (string)t.Header == title).Content as T;

        static void Click(TopLevel top, Point p, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
        {
            top.MouseMove(p, mods);
            top.MouseDown(p, button, mods);
            top.MouseUp(p, button, mods);
            Settle();
        }

        static Point Centre(Control c, TopLevel top, double fx = 0.5, double fy = 0.5)
        {
            top.UpdateLayout();
            return c.TranslatePoint(new Point(c.Bounds.Width * fx, c.Bounds.Height * fy), top)!.Value;
        }

        static void Key(TopLevel top, PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
        {
            top.KeyPressQwerty(key, mods);
            top.KeyReleaseQwerty(key, mods);
            Settle();
        }

        /// <summary>Hovers a menu item (its submenu opens), as the pointer would.</summary>
        static void Hover(MenuItem item)
        {
            var top = TopLevel.GetTopLevel(item)!;
            top.UpdateLayout();
            var p = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), top)!.Value;
            top.MouseMove(p);
            Settle();
            if (item.ItemCount > 0 && !item.IsSubMenuOpen) item.IsSubMenuOpen = true;
            Settle();
        }

        static MenuItem ItemOf(ItemsControl menu, string header) =>
            menu.Items.OfType<MenuItem>().FirstOrDefault(i => (i.Tag as string ?? i.Header as string) == header || i.Header as string == header)
            ?? throw new AssertionException($"no item {header} (there are {string.Join(", ", menu.Items.OfType<MenuItem>().Select(i => i.Header))})");

        static Window WaitForWindow(System.Func<Window, bool> which)
        {
            for (int i = 0; i < 20; i++)
            {
                Settle();
                var w = opened.LastOrDefault(x => x.IsVisible && which(x));
                if (w != null) return w;
            }
            throw new AssertionException("the window didn't open");
        }

        /// <summary>The pattern grid focused and its cursor on the first note lane (Tab), which scrolls the lanes into view.</summary>
        static void ToNotes(MainWindow window)
        {
            dynamic view = PatternView.Page.EditorControl;
            ((Control)view.TrackerGrid.Body).Focus();
            Settle();
            Key(window, PhysicalKey.Tab);
            Wait(100);
        }

        // ------------------------------------------------------------------ 0xx: start, views, transport

        [AvaloniaTest, Order(1)]
        public void Views()
        {
            BeforeAll();
            using var restore = Studio.AfterTest(() => { SessionCore.Timestamp = System.Diagnostics.Stopwatch.GetTimestamp; Core.ExecuteCommand(BuzzCommand.Stop); Core.Recording = false; });
            NewSong();
            var window = Open();
            try
            {
                Try("010-start-empty", () => Shot(window, "010-start-empty", "The main window on a new, empty song: transport bar, view tabs (Machines selected), the Master alone, status line.",
                    $"Start {Product.Name} (or File ▸ New, ⌘N).", song: "none (new song)", notes: Headless));
                Try("011-start-sequence-empty", () => { window.ShowView("Sequence"); Shot(window, "011-start-sequence-empty", "The Sequence tab on a new song: no tracks yet.", "F4, or the Sequence tab.", song: "none (new song)"); });
                Try("012-start-session-empty", () => { window.ShowView("Session"); Shot(window, "012-start-session-empty", "The Session tab on a new song: an empty launch grid with its toolbar and mixer.", "Ctrl+Tab, or the Session tab.", song: "none (new song)"); });
                Try("013-start-pattern-empty", () => { window.ShowView("Pattern"); Shot(window, "013-start-pattern-empty", "The Tracker docked in the detail panel on a new song: no machine with patterns yet (P3a).", "Cmd+4, Alt+Cmd+D, or the Tracker button under the view.", song: "none (new song)"); });
                Try("015-start-tracker-maximised", () => { window.Detail.Show(true); Shot(window, "015-start-tracker-maximised", "The Tracker maximised over the window (P3a).", "F2, Shift+Cmd+F, or the button in the detail bar.", song: "none (new song)"); });
                Try("017-start-machine-starters", () => { window.ShowView("Machines"); Shot(window, "017-start-machine-starters", "The Machine View on a new song (P3d): the starters Neo Poly, Neo Drums and Neo Wave one click each, and the hint to double-click or press the Add Machine key to search.", "Open a new song.", song: "none (new song)"); });
                Try("016-start-browser", () => { window.Detail.Close(); window.ShowView("Machines"); window.Browser.Open(); Shot(window, "016-start-browser", "The browser on the left (P3a): a search box over the installed machines, the patterns of the selection first.", "Alt+Cmd+B, or the rail's button.", song: "none (new song)"); window.Browser.Close(); });
                Try("014-start-notes-empty", () => { window.ShowView("Song Notes"); Shot(window, "014-start-notes-empty", "The Song Notes tab on a new song: the empty notes editor and its toolbar.", "F10, or the Song Notes tab.", song: "none (new song)"); });

                // P3i: every view of an empty song shows a next step; then the views of a song with a track and nothing in it.
                Try("018-start-piano-roll-empty", () => { window.Detail.ShowPage(true, false); Shot(window, "018-start-piano-roll-empty", "The piano roll on a new song (P3i): no machine to edit, so the starters and Add machine… are the next step, as in the Tracker.", "Alt+Cmd+R.", song: "none (new song)"); });
                Try("019-start-device-empty", () => { ReBuzz.Studio.Views.StudioSelection.For(Core).Clear(window); window.Detail.ShowDevice(); Shot(window, "019-start-device-empty", "The device view on a new song (P3i): no machine selected; the starters and Add machine….", "Alt+Cmd+5.", song: "none (new song)"); });
                Try("029-start-history-empty", () => { window.Detail.Close(); window.Inspector.Open(); window.Inspector.ShowTab(ReBuzz.Studio.Views.Inspector.InspectorPanel.HistoryTabName); Shot(window, "029-start-history-empty", "The inspector's History on a new song (P3i): nothing done yet, and the first step to make.", "Alt+Cmd+I, then History.", song: "none (new song)"); window.Inspector.ShowTab(ReBuzz.Studio.Views.Inspector.InspectorPanel.SelectionTab); });
                Try("034-start-sequence-nothing-arranged", () =>
                {
                    // A starter comes with its first pattern, placed on its track; this shot (and 044 and 046) show the states their names say, so
                    // the pattern goes (undoable: it is the "nothing arranged", "no clips" and "no patterns" a person reaches by deleting it).
                    var poly = ReBuzz.Studio.Shell.EmptyStates.AddStarter(Core, "Neo Poly");
                    Settle();
                    poly.DeletePattern(poly.Patterns[0]);
                    Settle();
                    window.ShowView("Sequence");
                    window.Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<ReBuzz.Studio.Views.Sequence.SequenceView>().First().RefreshEmpty();
                    Settle();
                    Shot(window, "034-start-sequence-nothing-arranged", "The Sequence tab after the Neo Poly starter and its pattern deleted (P3i): it has its track and nothing placed on it; the step is a new pattern on the track.", "Click the Neo Poly starter, then the Sequence tab.", song: "new song with Neo Poly");
                });
                Try("044-start-session-no-clips", () => { window.ShowView("Session"); Shot(window, "044-start-session-no-clips", "The Session tab after the Neo Poly starter (P3i): a track and no clip; the step is Create a clip, or a double click on a slot.", "Click the Neo Poly starter, then the Session tab.", song: "new song with Neo Poly"); });
                Try("046-start-tracker-no-patterns", () => { window.Detail.ShowPage(false, false); Shot(window, "046-start-tracker-no-patterns", "The Tracker on a machine with no patterns (P3i): New pattern is the next step.", "Select a machine that has no pattern, then the Tracker.", song: "new song with Neo Poly"); });
            }
            finally
            {
                Close(window);
            }

            var demo = LoadDemo();
            window = Open();
            SessionCore.Timestamp = () => 1_000_000;
            try
            {
                var machines = Tab<MachineView>(window, "Machines");
                Try("020-machines", () => Shot(window, "020-machines", "Machines: the demo song's machines and wires (Neo generators on the left, effects, Master on the right), at zoom 0.",
                    "F3, or the Machines tab.", notes: Headless));
                Try("021-machines-selected", () =>
                {
                    Click(window, machines.PointOf(demo.Lead, window) + new Vector(14, 9));
                    Shot(window, "021-machines-selected", "Machines with Lead selected (a click on its box).", "Click a machine.");
                });
                Try("022-machines-hover", () =>
                {
                    ToolTip.SetServiceEnabled(window, true);
                    window.MouseMove(machines.PointOf(demo.Delay, window) + new Vector(3, 2));
                    Wait(100);
                    window.MouseMove(machines.PointOf(demo.Delay, window));
                    Wait(1200);
                    Shot(window, "022-machines-hover", "Machines with the pointer resting on Delay: its hover state and tooltip.", "Point at a machine.");
                    ToolTip.SetServiceEnabled(window, false);
                });
                Try("025-machines-port-dots", () =>
                {
                    var port = machines.Canvas.OutPortOf(demo.Lead);
                    window.MouseMove(machines.FromCanvas(port, window));
                    Settle();
                    Shot(window, "025-machines-port-dots", "Machines with the pointer on Lead's output dot (P3d): its output and input dots shown, the dot enlarged, the tooltip names the port.", "Point at a machine's edge.");
                    window.MouseMove(new Point(20, 860));
                    Settle();
                });
                Try("026-machines-browser-popup", () =>
                {
                    MachineViewTests.DoubleClick(window, machines.PointOf(0.55, 0.75, window));
                    machines.Popup.Search.Text = "warm";
                    Settle();
                    Shot(window, "026-machines-browser-popup", "The machine browser on the canvas (P3d): opened by a double click on empty canvas, searching \"warm\": results with brand and tags, use_for, a star for favourites, the category filter.", "Double-click the canvas, or Shift+Cmd+N.");
                    machines.Popup.Hide();
                    Settle();
                });
                Try("027-device-docked", () =>
                {
                    ReBuzz.Studio.Views.StudioSelection.For(Core).SelectMachine(demo.Lead, window);
                    window.Detail.ShowDevice();
                    Settle();
                    Shot(window, "027-device-docked", "Detail > Device (P3e): the selected generator's parameters, grouped from its manifest, the macro strip over the list, sliders filled from zero with units, segmented choices and toggles, search and Editor | Parameters in the bar, the piano keyboard under it.", "Select a machine, Alt+Cmd+5, or the Device button under the view.");
                });
                Try("028-device-search-explain", () =>
                {
                    var view = ((ReBuzz.Studio.Views.Device.DeviceDock)window.Detail.DevicePage).Page!.Parameters;
                    view.Query = "feedback";
                    Settle();
                    var first = view.Layout.All.First(p => p.FullName.Contains("feedback", System.StringComparison.OrdinalIgnoreCase));
                    view.List.SetFocus(first);
                    Settle();
                    Shot(window, "028-device-search-explain", "The device view searching \"feedback\" (Cmd+F): the groups with a match, the focused parameter's name and value under the list with its description (How and Sound where the manifest has them).", "Cmd+F in the Device page, then click a parameter.");
                    view.Query = "";
                    window.Detail.Close();
                    Settle();
                });
                Try("029b-master-device-docked", () =>
                {
                    ReBuzz.Studio.Views.StudioSelection.For(Core).SelectMachine(demo.Master, window);
                    window.Detail.ShowDevice();
                    Settle();
                    Shot(window, "029b-master-device-docked", "Detail > Device with the Master selected: what the Master shows today.", "Select the Master; Detail > Device.");
                    window.Detail.Close();
                    Settle();
                });
                Try("029c-master-floating", () =>
                {
                    var page = new ReBuzz.Studio.Views.Device.DevicePage(demo.Master, floating: true);
                    page.SetEditor(false, remember: false);
                    var w = new Window { Width = 900, Height = 620, Content = page };
                    w.Show();
                    Settle();
                    Shot(w, "029c-master-floating", "The Master's parameters floating: what a double click or Parameters... opens today.", "Double-click the Master, or right-click it > Parameters...");
                    w.Close();
                });
                Try("023-machines-amp", () =>
                {
                    var wire = demo.Delay.Outputs.First(o => o.Destination == demo.Verb);
                    Click(window, machines.ArrowOf(wire, window), MouseButton.Right);
                    Assert.That(machines.Amp.IsVisible, Is.True, "the amp control opened");
                    Shot(window, "023-machines-amp", "Machines: the amp and pan control of the Delay → Verb wire (the send).", "Right-click a wire's arrow.");
                    window.MouseMove(new Point(20, 860));
                    Settle();
                });
                Try("029-machines-modulation", () =>
                {
                    // neo-patch.md §3 (phase NM): two modulators on the demo song: an LFO on the lead and an envelope on the delay, each a dashed wire
                    // in the Control identity colour; the pointer rests on the LFO's wire for its label. Then the lead's device view with the ring.
                    var song = Core.SongCore;
                    var added = new List<MachineCore>();
                    try
                    {
                        song.CreateMachine("Neo LFO", null, "Wobble", null, null, null, -1, -0.62f, -0.6f);
                        song.CreateMachine("Neo Envelope", null, "Pluck", null, null, null, -1, 0.2f, -0.72f);
                        added.AddRange(song.MachinesList.Where(m => m.Name is "Wobble" or "Pluck"));
                        IParameter Slider(MachineCore m, string name) => m.ParameterGroups[1].Parameters.FirstOrDefault(p => p.Name == name)
                            ?? m.ParameterGroups[1].Parameters.First(p => p.Type != ParameterType.Note && p.MaxValue - p.MinValue >= 100);
                        var lead = Slider((MachineCore)demo.Lead, "F1 cutoff");
                        var fb = Slider((MachineCore)demo.Delay, "Feedback");
                        var lfo = ModulationBoard.Of(added.First(m => m.Name == "Wobble"));
                        var env = ModulationBoard.Of(added.First(m => m.Name == "Pluck"));
                        lfo.SetTarget(-1, demo.Lead, lead, 0, new ModulationInfo { Depth = 40, Bipolar = true }, out _);
                        env.SetTarget(-1, demo.Delay, fb, 0, new ModulationInfo { Depth = 30, Bipolar = false }, out _);
                        for (int i = 0; i < 60 && !(lfo.Targets.Count == 1 && lfo.Targets[0].State == ModulationState.Active && env.Targets[0].State == ModulationState.Active); i++)
                        {
                            Wait(100);
                            Settle();
                        }
                        window.ShowView("Machines");
                        Settle();
                        var link = machines.Canvas.ModLinks.First(l => l.Source.Name == "Wobble");
                        window.MouseMove(machines.FromCanvas(machines.Canvas.ModMidOf(link), window) + new Vector(2, 2));
                        Wait(100);
                        window.MouseMove(machines.FromCanvas(machines.Canvas.ModMidOf(link), window));
                        Settle();
                        Shot(window, "029-machines-modulation", "Machines with two modulators (Neo LFO \"Wobble\" on Lead's cutoff, Neo Envelope \"Pluck\" on Delay's feedback): a dashed wire in the Control identity colour from each modulator to the machine it moves (no arrow: it is not a signal), the label of the one under the pointer.",
                            "Add a Neo LFO, Envelope, Step or Macro and pick a target in its editor (or the agent's modulate); point at a dashed wire for its parameters.");
                        window.MouseMove(new Point(5, 5));

                        ReBuzz.Studio.Views.StudioSelection.For(Core).SelectMachine(demo.Lead, window);
                        window.Detail.ShowDevice();
                        Settle();
                        var view = ((ReBuzz.Studio.Views.Device.DeviceDock)window.Detail.DevicePage).Page!.Parameters;
                        var row = view.Layout.All.First(p => p.Parameter == lead);
                        view.List.Reveal(row);
                        view.List.SetFocus(row);
                        Wait(300);
                        Settle();
                        Shot(window, "029-device-modulation-ring", "The device view of Lead with its cutoff modulated: a ring in the Control colour over the slider shows where the modulation reaches, a tick the value you set and a dot the value now.",
                            "Modulate a parameter, then select its machine.");
                        window.Detail.Close();
                        Settle();
                    }
                    finally
                    {
                        foreach (var m in added)
                        {
                            Core.UiHost.ReleaseMachine(m);
                            song.DeleteMachines(new[] { m });
                        }
                        Settle();
                    }
                });
                Try("024-machines-zoomed", () =>
                {
                    machines.ZoomSlider.Value = 3;
                    Settle();
                    Shot(window, "024-machines-zoomed", "Machines zoomed in (zoom slider at 3).", "⌘ + scroll wheel, a pinch, or the zoom slider (top right).");
                    machines.ZoomSlider.Value = 0;
                    Settle();
                });
                Try("035-machines-tracks", () =>
                {
                    // MT1 (ux-overhaul.md §11): the Tracks mode on the demo song: the chip bar under the top bar, boxes and wires in their track's
                    // colour, a split stripe on a machine that several tracks feed; then one track lit (the rest at about 35 %) with its card; then many chips.
                    ReBuzz.Studio.Views.Machines.MachineTracksPreference.On = true;
                    try
                    {
                        Settle();
                        Shot(window, "035-machines-tracks", "Machines with Tracks on (MT1): a chip for each track, group and return under the top bar, every box and wire in its track's colour, a split stripe on a machine several tracks feed, the boxes where they were.",
                            "Tracks, in the top bar of the Machines view.");
                        var chip = machines.Tracks.ChipOf(demo.Lead);
                        Assert.That(chip, Is.Not.Null, "a chip for Lead");
                        machines.Canvas.Pin(ReBuzz.Studio.Views.Machines.TrackTarget.Entry(demo.Lead));
                        Settle();
                        Shot(window, "035-machines-track-lit", "Machines with Lead's track lit (MT1): its path in full colour, everything else dimmed to about 35 %, the card under its first machine with the chain, mute, solo, arm and a meter.",
                            "Point at, focus or click a chip.");
                        machines.Canvas.Pin(null);
                        Settle();

                        var song = Core.SongCore;
                        var added = new List<MachineCore>();
                        try
                        {
                            var master = song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
                            for (int i = 0; i < 26; i++)
                            {
                                string name = "Track " + (i + 1);
                                song.CreateMachine("Neo Poly", null, name, null, null, null, -1, -0.95f + 0.07f * (i % 6), 0.55f + 0.1f * (i / 6));
                                var m = song.MachinesList.First(x => x.Name == name);
                                added.Add(m);
                                song.ConnectMachines(m, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                            }
                            Settle();
                            Shot(window, "035-machines-many-chips", "Machines with Tracks on and many chips (MT1): 26 more generators into the Master (30 chips in all); the bar stops at two rows and the rest is behind a +N chip that opens a list with a filter; every chip is reachable with Tab and the arrow keys.",
                                "Tracks, with many generators in the song.");
                        }
                        finally
                        {
                            foreach (var m in added)
                            {
                                Core.UiHost.ReleaseMachine(m);
                                song.DeleteMachines(new[] { m });
                            }
                            Settle();
                        }
                    }
                    finally
                    {
                        ReBuzz.Studio.Views.Machines.MachineTracksPreference.On = false;
                        Settle();
                    }
                });

                Try("036-midi-mapping", () =>
                {
                    // MM1 (docs/specs/midi-mapping.md): map mode with badges and a control listening, the popover, the Inspector's track page, the top-bar track selector.
                    var mm = Core.MidiMapping;
                    ReBuzz.Midi.MidiEngine.SimulatedInputs = new[] { "Faders", "Pads" };
                    try
                    {
                        var ps = demo.Lead.ParameterGroupsList[1].Parameters.Where(x => x.Flags.HasFlag(BuzzGUI.Interfaces.ParameterFlags.State)).Take(6).ToList();
                        StudioSelection.For(Core).SelectMachine(demo.Lead, null);
                        mm.SetKnobGroup("Faders", Enumerable.Range(21, 8).Select(n => new ReBuzz.Midi.Mapping.MapSource("Faders", ReBuzz.Midi.Mapping.MapKind.ControlChange, 0, n)));
                        var trackId = mm.SelectedTrackId;
                        mm.SetSlot(trackId, 0, 0, ReBuzz.Midi.Mapping.MapTarget.Parameter(demo.Lead.Name, ps[0].Name), new ReBuzz.Midi.Mapping.MapDrive());
                        mm.SetSlot(trackId, 0, 1, ReBuzz.Midi.Mapping.MapTarget.Parameter(demo.Lead.Name, ps[1].Name), new ReBuzz.Midi.Mapping.MapDrive { Takeover = ReBuzz.Midi.Mapping.Takeover.Jump });
                        mm.SetSlot(trackId, 0, 2, ReBuzz.Midi.Mapping.MapTarget.Parameter(demo.Lead.Name, ps[2].Name), new ReBuzz.Midi.Mapping.MapDrive());
                        mm.MapControl(new ReBuzz.Midi.Mapping.MapSource("Pads", ReBuzz.Midi.Mapping.MapKind.ControlChange, 0, 74), ReBuzz.Midi.Mapping.KnobEncoding.Absolute,
                            ReBuzz.Midi.Mapping.MapTarget.Parameter(demo.Lead.Name, ps[3].Name), ReBuzz.Core.MapScope.Global);
                        mm.MapControl(new ReBuzz.Midi.Mapping.MapSource("Pads", ReBuzz.Midi.Mapping.MapKind.Note, 9, 36), ReBuzz.Midi.Mapping.KnobEncoding.Absolute,
                            ReBuzz.Midi.Mapping.MapTarget.Command(ReBuzz.Midi.Mapping.MapCommand.Play), ReBuzz.Core.MapScope.Global);
                        window.Detail.ShowDevice();
                        window.Transport.Refresh();
                        Settle();
                        window.ToggleMapMode();
                        Settle();
                        window.MapLayer.Rescan();
                        window.MapLayer.ArmTarget(ReBuzz.Midi.Mapping.MapTarget.Parameter(demo.Lead.Name, ps[4].Name));
                        Settle();
                        window.MapLayer.Rescan();
                        Shot(window, "036-midi-map-mode", "MIDI map mode (MM1): the banner under the top bar, every mappable control outlined, badges on the mapped ones (K1 to K3 from the Faders group, a pad's CC), one control listening, the track selector and the devices that follow it.",
                            "Edit, Map MIDI controls (Ctrl+Alt+K).");
                        // The Device panel scrolled so a card is half out of view: its outline stops at the panel's edge and none reaches the Inspector.
                        var devicePage = MidiMappingUiTests.DeviceViewOf(window);
                        var cardsScroller = devicePage?.Cards.Scroller;
                        Assert.That(cardsScroller != null && MidiMappingUiTests.ScrollHalfACardOut(window, cardsScroller), Is.True, "the Device panel scrolls so a card is cut by its edge");
                        window.MapLayer.Rescan();
                        Shot(window, "036-midi-map-mode-scrolled", "MIDI map mode with the Device panel scrolled (MM1b): a card half out of view is outlined as far as it is in view, the outline stops at the panel's edge and none spills over the Inspector.",
                            "Edit, Map MIDI controls (Ctrl+Alt+K), then scroll the Device panel.");
                        cardsScroller.Offset = default;
                        Settle();
                        window.MapLayer.Rescan();
                        window.MapLayer.Mode.Exit();
                        window.MapLayer.OpenForTarget(ReBuzz.Midi.Mapping.MapTarget.Parameter(demo.Lead.Name, ps[3].Name));
                        Settle();
                        Shot(window, "036-midi-popover", "The MIDI popover (MM1): scope (This track or Global), what it is mapped to, Listen and Unmap, the range, Invert and the takeover mode.",
                            "The MIDI button of a control, or a badge's Edit.");
                        window.MapLayer.Close();
                        Settle();
                        window.Transport.Refresh();
                        Shot(window, "036-midi-top-bar", "The top bar's track selector with two devices following (MM1b): ◀ Lead ▶ and a muted \"2 devices\" chip (their names are its tooltip); the bar stays one row, with Lock, Keys and the device picker after it.",
                            "The top bar, after the position and the minutes.");
                        window.Inspector.MidiPage.Refresh();
                        Shot(window, "036-midi-track-page", "The Inspector's MIDI page for Lead's track (MM1): slots laid out as the knobs, the parameter and its value, a grey ring where the generator's pin answers, the controllers and their keys role.",
                            "Select a track; the Inspector's Selection tab.");
                    }
                    finally
                    {
                        window.MapLayer.Mode.Exit();
                        mm.CancelListening();
                        mm.Edit((c, sg) => { c.Global.Clear(); c.Groups.Clear(); c.NoKeys.Clear(); sg.Tracks.Clear(); sg.Global = null; });
                        ReBuzz.Midi.MidiEngine.SimulatedInputs = null;
                        Settle();
                    }
                });

                Try("030-sequence", () =>
                {
                    window.ShowView("Sequence");
                    Shot(window, "030-sequence", "Sequence: the 16-bar arrangement of the demo song, one track a generator, coloured pattern boxes, the pattern list on the right.", "F4, or the Sequence tab.");
                });
                Try("031-sequence-cursor", () =>
                {
                    var seq = Tab<SequenceView>(window, "Arrangement");
                    var geo = seq.Canvas.Geometry;
                    Click(window, seq.Canvas.TranslatePoint(new Point(geo.XOf(8 * 16) + 6, geo.YOf(2) + geo.TrackHeight * 0.6), window)!.Value);
                    Shot(window, "031-sequence-cursor", "Sequence with the cursor on Lead's Hook at bar 9 (selected pattern, the pattern list follows).", "Click a pattern box.");
                });
                Try("032-sequence-arrangement", () =>
                {
                    var seq = Tab<SequenceView>(window, "Arrangement");
                    seq.SetArrangementMode(true);
                    Shot(window, "032-sequence-arrangement", "Sequence in Arrangement mode (daw-modes.md AR1): the Master's tempo and markers on top, a track for each machine with its lanes, clips with their previews, loop repeats and shared badges, the meter grid.", "The Sequence tab, then Arrange.");
                    seq.SetArrangementMode(false);
                });
                Try("033-arrangement-piano-roll", () =>
                {
                    var seq = Tab<SequenceView>(window, "Arrangement");
                    seq.SetArrangementMode(true);
                    var hook = Studio.Host.Core.Song.Machines.First(m => m.Name == "Lead").Patterns.First(p => p.Name == "Hook");
                    seq.OpenPianoRoll(hook);
                    Shot(window, "033-arrangement-piano-roll", "A clip opened in the Arrangement mode (daw-modes.md AR2): its notes in the piano roll under the timeline, with the velocity lane (or a CC or pitch bend lane), key and scale, fold, step input and the ghosts of other clips.", "Double-click a clip, or P.");
                    seq.RollPanel.Close();
                    seq.SetArrangementMode(false);
                });

                Try("040-session", () =>
                {
                    window.ShowView("Session");
                    Shot(window, "040-session", "Session (stopped): the launch grid of the demo song's scenes (Intro, Verse, Drop, Break, Fill), clip colours, the Verb return, clip panel and mixer.", "Ctrl+Tab, or the Session tab.", notes: Headless);
                });
                var session = Tab<SessionView>(window, "Session");
                Try("041-session-queued", () =>
                {
                    Click(window, Centre(session.Scenes[2], window));
                    Shot(window, "041-session-queued", "Session: the Drop scene launched, its clips queued for the next bar (blinking state, drawn mid-blink).", "Click a scene's ▶ (or press its number key).");
                });
                Try("042-session-playing", () =>
                {
                    RunTicks(20);
                    session.RefreshState();
                    session.RefreshMeters();
                    Shot(window, "042-session-playing", "Session playing: the Drop scene's clips playing (each keeps its colour; its ▶ on a dark chip, its outline and progress bar in the play green), the clock running, meters moving.", "After a launch, at the bar line; the transport runs.");
                });
                Try("340-session-no-transport", () =>
                    Shot(window, "340-session-no-transport", "Session (P3g): its toolbar has no transport of its own any more: Stop all, Overdub, Capture, Back to arrangement, Launch, Follow, the nudge and Scenes; the one transport is the top bar above (metronome, time signature, tempo, Tap, the clock).", "Ctrl+Tab, or the Session tab."));
                Try("347-export-audio-sheet", () =>
                {
                    var sheet = new ReBuzz.Studio.Shell.ExportSheet(Core);
                    sheet.Show();
                    Settle();
                    sheet.PathBox.Text = "/Users/you/Music/Gallery demo.wav";
                    sheet.Bits.SelectedIndex = 0;
                    sheet.Rate.SelectedIndex = 2;
                    sheet.Stems.SelectedIndex = 1;
                    Settle();
                    try
                    {
                        DialogShot(window, sheet, "347-export-audio-sheet", "Export Audio (P3j): the range (song or loop), the bit depth (16, 24, 32 float), dither (on for 16 bit; Neo Dither's), the sample rate, normalise (off), stems, and the file; Export and Cancel. It runs the agent's export_audio, so the file is the one the tool writes.", "File ▸ Export Audio… (Shift+Cmd+R).", notes: Headless);
                    }
                    finally
                    {
                        sheet.Close();
                    }
                });
                Try("348-export-audio-progress", () =>
                {
                    string file = TestTemp.Path("gallery-export-" + System.Environment.ProcessId + ".wav");
                    var sheet = new ReBuzz.Studio.Shell.ExportSheet(Core);
                    sheet.Show();
                    Settle();
                    sheet.PathBox.Text = file;
                    bool shot = false;
                    ReBuzz.Studio.Shell.ExportSheet.OnProgress = seconds =>
                    {
                        if (shot) return;
                        shot = true;
                        sheet.PathBox.Text = "/Users/you/Music/Gallery demo.wav";
                        DialogShot(window, sheet, "348-export-audio-progress", "Export Audio while it renders (P3j): the sheet's settings locked, a progress bar and the seconds rendered; Cancel stops it and leaves no file.", "Export in the sheet.", notes: Headless);
                        sheet.CancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    };
                    try
                    {
                        var run = sheet.Run();
                        while (!run.IsCompleted) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(2); }
                    }
                    finally
                    {
                        ReBuzz.Studio.Shell.ExportSheet.OnProgress = null;
                        sheet.Close();
                        if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
                    }
                });
                Try("345-session-machine-colours", () => WithMachineColours(() =>
                {
                    window.ShowView("Session");
                    Shot(window, "345-session-machine-colours", "The Session View with no colour picked on any clip or track (P3h): each clip and track takes its machine's identity colour (Synth pink, Drums amber, Delay and reverb violet, Dynamics teal, EQ sky); a colour the user picks wins.", "Right-click a clip ▸ Colour ▸ Machine's colour.");
                }));
                Try("346-sequence-machine-colours", () => WithMachineColours(() =>
                {
                    window.ShowView("Sequence");
                    Shot(window, "346-sequence-machine-colours", "The Arrangement with no colour picked on any pattern (P3h): the blocks and the track headers' left strips take the machine's identity colour.", "Right-click a block ▸ Color ▸ Default.");
                    window.ShowView("Session");
                }));
                Try("341-machines-meters-playing", () =>
                {
                    // A machine alone is selected here (a clip chosen in the Session View stays selected with its machine across views, and
                    // that selection has the inspector's clip card: mac-341 showed it on Lead).
                    ReBuzz.Studio.Views.StudioSelection.For(Core).Set(demo.Lead, null, window);
                    window.ShowView("Machines");
                    var canvas = Tab<MachineView>(window, "Machines").Canvas;
                    for (int i = 0; i < 4; i++)
                    {
                        RunTicks(4);
                        canvas.TickMeters(1.0 / MachineCanvas.MeterRate);
                    }
                    Shot(window, "341-machines-meters-playing", "Machines while the Drop scene plays (P3g): each box and wire brightens with the level through it, from the engine's own peaks (a strip along the foot of a box, green and yellow from -6 dB; a wire turns green and thickens); the top bar's clock runs.", "Play a song with the Machine View open.", notes: Headless);
                    window.ShowView("Session");
                    Settle();
                });
                Try("342-top-bar-master-meter", () =>
                {
                    var meter = window.Transport.Meter;
                    Core.ClearMasterOver();
                    var loud = new float[1024];
                    System.Array.Fill(loud, 1.8f);
                    Core.SafeMaster.Process(loud, 2, 44100);
                    meter.Refresh();
                    System.Threading.Thread.Sleep(400);                 // the bars fall at 60 dB a second
                    var soft = new float[1024];
                    System.Array.Fill(soft, 0.3f);
                    Core.SafeMaster.Process(soft, 2, 44100);
                    meter.Refresh();
                    window.RefreshNow();
                    Shot(window, "342-top-bar-master-meter", "The top bar (P3g): metronome, time signature, the tempo (drag it, or double-click to type), Tap, the clock as bar.beat.tick and minutes and seconds, and the master meter: two bars on a -60 to 0 dB scale and the clip light, latched red with the highest over until it is clicked (the test feeds it a signal of 1.8, +5.1 dB, and then one at -10 dB to show the latch; the demo song itself peaks well under 0 dB, UxP3hTests).", "The bar above every view.");
                    Core.ClearMasterOver();
                    meter.Refresh();
                    Core.SafeMaster.Reset();
                });
                Try("043-session-clip-panel", () =>
                {
                    Click(window, Centre(session.SlotAt(2, 2), window, 0.6));
                    Shot(window, "043-session-clip-panel", "Session: Lead's Hook clip selected, its properties in the clip panel (launch mode, quantization, legato, loop region, follow action, scene tempo).", "Click a clip (not its ▶).");
                });
                Try("044-session-mixer-hidden", () =>
                {
                    Click(window, Centre(session.MixerButton, window));
                    Shot(window, "044-session-mixer-hidden", "Session with the mixer hidden (the grid gets the room).", "The toolbar's Mixer button (Ctrl+Alt+M).");
                    Click(window, Centre(session.MixerButton, window));
                });
                Try("045-session-stopped-all", () =>
                {
                    Click(window, Centre(session.StopAllButton, window));
                    RunTicks(20);
                    Core.ExecuteCommand(BuzzCommand.Stop);
                    Settle();
                    session.RefreshState();
                    Shot(window, "045-session-stopped-all", "Session after Stop all: clips stopped, the transport stopped.", "The toolbar's ■ Stop all, then Space.");
                });
                Try("046-session-recording", () =>
                {
                    Core.Playing = true;
                    RunTicks(3);
                    int padTrack = session.Headers.ToList().FindIndex(h => h.Machine == demo.Pad);
                    Click(window, Centre(session.Headers[padTrack].Arm, window));
                    session.RefreshState();
                    var empty = session.SlotAt(padTrack, 3);
                    Click(window, Centre(empty.RecordButton, window));
                    RunTicks(24);
                    Core.SendMIDIInput(SessionRig.NoteOn(60, 99));
                    RunTicks(4);
                    session.RefreshState();
                    Shot(window, "046-session-recording", "Session recording: Pad's track armed (●), a take recording into its empty Break slot while the transport runs.", "Arm a track (its header's ●), then click an empty slot's ● (or the toolbar's ● Session record).");
                    Core.SendMIDIInput(SessionRig.NoteOff(60));
                    Core.ExecuteCommand(BuzzCommand.Stop);
                    RunTicks(2);
                    Click(window, Centre(session.Headers[padTrack].Arm, window));
                    Core.SessionLayout.Undo();
                    Settle();
                });
                Try("047-session-piano-roll", () =>
                {
                    var piano = PianoRollWindow.Show(window, Core, Core.SessionLayout, Core.SessionLayout.SequenceOf(session.Headers.ToList().FindIndex(h => h.Machine == demo.Lead)), demo.Hook);
                    Settle();
                    Shot(piano, "047-session-piano-roll", "The piano roll of Lead's Hook clip (a window of its own): notes, velocities, the grid.", "⌘ + double-click a clip in the Session View.");
                    piano.Close();
                });

                Core.BPM = 124;                 // the Drop scene's tempo, put back
                Core.SongCore.PlayPosition = 0;
                Try("050-pattern", () =>
                {
                    window.Detail.Show(true);       // the Tracker over the window, as the tab was
                    PatternView.Edit(demo.LineA);
                    window.ShowView("Pattern");
                    Wait(200);
                    ToNotes(window);
                    Shot(window, "050-pattern", "Pattern: Bass's Line A in the Modern Pattern Editor (machine and pattern choosers, toolbar, the global columns, the notes on the MIDI note lanes on the right).",
                        "F2 (the Tracker over the window), or double-click a pattern in the Sequence View.");
                });
                Try("051-pattern-drums", () =>
                {
                    PatternView.Edit(demo.Beat);
                    Wait(200);
                    ToNotes(window);
                    Shot(window, "051-pattern-drums", "Pattern: Drums' Beat (kick, snare, clap and hats on Neo Drums' MIDI note lanes).", "Choose Drums and Beat in the Pattern tab's choosers.");
                });
                Try("052-pattern-selection", () =>
                {
                    PatternView.Edit(demo.Chords);
                    Wait(200);
                    ToNotes(window);
                    for (int i = 0; i < 6; i++) Key(window, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
                    for (int i = 0; i < 2; i++) Key(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
                    Shot(window, "052-pattern-selection", "Pattern: Pad's Chords with a block selected (Shift + arrows).", "Shift + arrow keys (or a drag) in the pattern grid.");
                });
                Try("053-pattern-long-scrolled", () =>
                {
                    PatternView.Edit(demo.Arp);
                    Wait(200);
                    ToNotes(window);
                    for (int i = 0; i < 4; i++) Key(window, PhysicalKey.PageDown);
                    Wait(150);
                    Shot(window, "053-pattern-long-scrolled", "Pattern: Lead's 8-bar Arp (128 rows), scrolled down with the cursor past the middle.", "Page Down in a long pattern.");
                });
                Try("054-pattern-notes-lanes", () =>
                {
                    // A take as a session records it: notes in the pattern's MIDI column, seen in the tracker as lanes (daw-modes.md AR3).
                    var lead = (MachineCore)Studio.Host.Core.Song.Machines.First(m => m.Name == "Lead");
                    lead.CreatePattern("Take", 16);
                    var take = lead.Patterns.First(p => p.Name == "Take");
                    PatternEvent N(int row, int rows, int midi, int vel, int shift = 0) => new PatternEvent(row * 240 + shift, 0x90 | (midi << 8) | (vel << 16), rows * 240);
                    ReBuzz.Core.Session.SessionLayout.EnsureMidiColumn(take).SetEvents(new[]
                    {
                        N(0, 2, 60, 100), N(0, 4, 64, 90, 0), N(2, 2, 67, 100, 61), N(4, 4, 62, 80), N(4, 4, 65, 70), N(4, 4, 69, 60), N(8, 3, 60, 110), N(10, 6, 55, 95), N(12, 2, 72, 127, 100),
                    }, true);
                    try
                    {
                        PatternView.Edit(take);
                        Wait(200);
                        dynamic v = PatternView.Page.EditorControl;
                        ((Control)v.TrackerGrid.Body).Focus();
                        Settle();
                        Shot(window, "054-pattern-notes-lanes", "Pattern: a take recorded in the session shows in the tracker at once (daw-modes.md AR3): the pattern's MIDI column as Notes lanes, a voice for each overlapping note with its note, velocity, delay and cut, edited with the keys as a step of the song's history.", "Record a take in the Session View, or draw notes in a piano roll, then open the pattern in the Tracker.");
                    }
                    finally
                    {
                        var other = lead.Patterns.FirstOrDefault(p => p != take);
                        if (other != null) { PatternView.Edit(other); Wait(100); }
                        lead.DeletePattern(take);
                        Settle();
                    }
                });

                Control Grid() => (Control)((dynamic)PatternView.Page.EditorControl).TrackerGrid.Body;
                ContextMenu GridMenu()
                {
                    // In the grid's view (the body is as wide as all its columns, most of them scrolled away).
                    var body = Grid();
                    var view = (Control)((dynamic)PatternView.Page.EditorControl).TrackerGrid;
                    Click(window, Centre(view, window, 0.45, 0.4), MouseButton.Right);
                    var menu = body.ContextMenu;
                    Assert.That(menu, Is.Not.Null, "the grid has a menu");
                    if (!menu!.IsOpen) menu.Open(body);
                    Settle();
                    Assert.That(menu.IsOpen, Is.True, "the grid's menu opened");
                    menus.Add(menu!);
                    return menu!;
                }
                Try("054-pattern-menu", () =>
                {
                    GridMenu();
                    Shot(window, "054-pattern-menu", "Pattern: the grid's context menu (Edit ▸, Select All, Unselect, Pattern Properties…).", "Right-click in the pattern grid.");
                });
                Try("055-pattern-menu-edit", () =>
                {
                    Hover(ItemOf(GridMenu(), "Edit"));
                    Shot(window, "055-pattern-menu-edit", "Pattern: the grid menu's Edit ▸ (increase, interpolate, randomize, rotate, write state, undo, clipboard).", "Right-click in the pattern grid ▸ Edit.");
                });
                Try("056-pattern-properties", () =>
                {
                    var item = GridMenu().Items.OfType<MenuItem>().First(i => (i.Header as string)?.StartsWith("Pattern Properties") == true);
                    DialogOnOpen(window, () => Click(window, Centre(item, window)), "056-pattern-properties", "Pattern Properties: name, length, rows per beat, the columns shown (the MIDI lanes among them).",
                        "Right-click in the pattern grid ▸ Pattern Properties… (Ctrl+Backspace).");
                });
                Try("057-pattern-help", () =>
                {
                    var help = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => b.Content as string == "Help");
                    Click(window, Centre(help, window));
                    Wait(150);
                    Shot(window, "057-pattern-help", "Pattern: the editor's help panel (its keys).", "The pattern toolbar's Help button (F1).");
                    Click(window, Centre(help, window));
                });

                // The piano roll in the detail panel (ux-overhaul.md P3b): the same pattern as the Tracker above, as a note grid.
                Try("058-piano-roll", () =>
                {
                    window.Detail.ShowPage(true, true);
                    PatternView.Edit(demo.LineA);
                    Wait(250);
                    var roll = PatternView.Page.Roll;
                    roll.Poll();
                    roll.Roll?.SelectAll();
                    Wait(100);
                    Shot(window, "058-piano-roll", "Piano roll: Bass's Line A, the same pattern as the Tracker (the notes of its MIDI lanes), maximised over the window, all notes selected.",
                        "The Piano roll button in the detail bar (Alt+Cmd+4), after choosing a pattern.");
                    window.Detail.SetPage(false);
                });

                Try("060-song-notes", () =>
                {
                    window.Detail.Restore();
                    window.ShowView("Song Notes");
                    Shot(window, "060-song-notes", "Song Notes: the demo song's notes (Markdown: headings, a list, bold), with the formatting toolbar.", "F10, or the Song Notes tab.");
                });

                // The transport in its three states, in the Sequence View (its play cursor shows where the song is).
                Try("070-transport-stopped", () =>
                {
                    Core.ExecuteCommand(BuzzCommand.Stop);
                    Core.SongCore.PlayPosition = 0;
                    window.ShowView("Sequence");
                    window.RefreshNow();
                    Shot(window, "070-transport-stopped", "The transport bar stopped (▶ ■ ● Loop, BPM, position 1.1.1, the master meter), Sequence View.", "Space or F8 stops.");
                });
                Try("071-transport-playing", () =>
                {
                    Core.SongCore.PlayPosition = 8 * 16;
                    Core.Playing = true;
                    RunTicks(22);
                    Wait(300);
                    window.RefreshNow();
                    Shot(window, "071-transport-playing", "The transport playing from bar 9: ▶ lit in the play green, the position counting, the meter showing the level, the play cursor in the Sequence View.", "Space or F5 plays.");
                });
                Try("072-transport-recording", () =>
                {
                    Core.Recording = true;
                    RunTicks(4);
                    Wait(200);
                    window.RefreshNow();
                    Shot(window, "072-transport-recording", "The transport recording while it plays: ● lit in the record red.", "F7 (or the ● button) while playing.");
                    Core.Recording = false;
                    Core.ExecuteCommand(BuzzCommand.Stop);
                    RunTicks(2);
                    Settle();
                });
            }
            finally
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 1xx: the menus

        /// <summary>The Mac's menus as an in-window menu: the app menu (ReBuzz) and the menu bar's File … Help, from the same model.</summary>
        static Menu MacMenuBar(MainWindow window)
        {
            var model = new[] { new MenuEntry { Header = Avalonia.Application.Current?.Name ?? Product.Name, Items = () => window.AppMenuModel() } }.Concat(window.MenuModel(mac: true));
            var bar = AppMenu.ToMenu(model);
            var dock = (DockPanel)window.Transport.Parent!;
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Insert(0, bar);
            Settle();
            return bar;
        }

        [AvaloniaTest, Order(2)]
        public void Menus()
        {
            BeforeAll();
            LoadDemo();
            var window = Open("Machines");
            try
            {
                var bar = MacMenuBar(window);
                const string Bar = "macOS shows these menus in its menu bar, which headless can't draw: here the same menu model (AppMenu, mac: true) is drawn as an in-window Avalonia menu at the top of the window. Shortcuts show as Avalonia writes them off a Mac.";
                var items = bar.Items.OfType<MenuItem>().ToList();
                string[] ids = { "100-menu-app", "101-menu-file", "102-menu-edit", "103-menu-transport", "104-menu-view", "105-menu-window", "106-menu-help" };
                string[] whats =
                {
                    $"The app menu ({Product.Name}) as the model has it, whole: About {Product.Name}, Preferences… (⌘,), Services (the system fills it), Hide {Product.Name} (⌘H), Hide Others (⌥⌘H), Show All, Quit {Product.Name} (⌘Q). The real Mac's is mac-real-100.",
                    "File: New, Open…, Open Recent ▸, Save, Save As… (no Exit: Quit is in the app menu).",
                    "Edit: Undo, Redo, Cut, Copy, Paste (Preferences is in the app menu).",
                    "Transport: Play or Stop (Space), Play, Stop, Record, Restart audio.",
                    "View: the five views, the shown one checked, with their keys.",
                    "Window: Minimize, Zoom, Agent connection….",
                    "Help: Keyboard shortcuts… (About is in the app menu).",
                };
                for (int i = 0; i < items.Count && i < ids.Length; i++)
                {
                    int k = i;
                    Try(ids[k], () =>
                    {
                        items[k].IsSubMenuOpen = true;
                        Settle();
                        Shot(window, ids[k], whats[k], $"The menu bar's {items[k].Tag} menu.", notes: Bar);
                        items[k].IsSubMenuOpen = false;
                        Settle();
                    });
                }
                Try("107-menu-file-recent", () =>
                {
                    var file = items[1];
                    file.IsSubMenuOpen = true;
                    Settle();
                    Hover(ItemOf(file, "Open Recent"));
                    Shot(window, "107-menu-file-recent", "File ▸ Open Recent: the recent songs (Night Drive.bmxml), Clear Menu.", "File ▸ Open Recent.", notes: Bar);
                    file.IsSubMenuOpen = false;
                });
                ((DockPanel)bar.Parent!).Children.Remove(bar);
            }
            finally
            {
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 2xx: context menus

        [AvaloniaTest, Order(3)]
        public void ContextMenus()
        {
            BeforeAll();
            var demo = LoadDemo();
            var window = Open("Machines");
            try
            {
                var view = Tab<MachineView>(window, "Machines");
                void MachineMenu(string id, Point at, string what, string how, params string[] path)
                {
                    Try(id, () =>
                    {
                        Click(window, at, MouseButton.Right);
                        var menu = view.Canvas.OpenMenu;
                        Assert.That(menu?.IsOpen, Is.True, "a right click opens a menu");
                        menus.Add(menu!);
                        ItemsControl level = menu!;
                        foreach (var p in path)
                        {
                            var item = ItemOf(level, p);
                            Hover(item);
                            level = item;
                        }
                        Shot(window, id, what, how);
                    });
                }
                MachineMenu("200-machines-menu-canvas", view.PointOf(0.1, 0.85, window), "Machines: the menu on empty space (New ▸, Unmute all, Snap, Select All, Deselect…).", "Right-click empty space in the Machine View.");
                MachineMenu("201-machines-menu-new-generators", view.PointOf(0.1, 0.85, window), "Machines: New ▸ Generators, the installed generators (the machine browser): Neo and Pedal machines.", "Right-click empty space ▸ New ▸ Generators.", "New", "Generators");
                MachineMenu("202-machines-menu-new-effects", view.PointOf(0.1, 0.85, window), "Machines: New ▸ Effects, the installed effects.", "Right-click empty space ▸ New ▸ Effects.", "New", "Effects");
                MachineMenu("203-machines-menu-generator", view.PointOf(demo.Bass, window), "Machines: a generator's menu (Bass, Neo Poly): Mute, Solo, Parameters…, Editor…, MIDI input, Replace, Rename…, Delete.", "Right-click a generator.");
                MachineMenu("204-machines-menu-effect", view.PointOf(demo.Delay, window), "Machines: an effect's menu (Delay, Neo Delay): Mute, Bypass, Parameters…, Editor…, Rename…, Delete.", "Right-click an effect.");
                MachineMenu("205-machines-menu-master", view.PointOf(demo.Master, window), "Machines: the Master's menu (Parameters…, clear the clip indicator).", "Right-click the Master.");
                // P6c: a machine's menu with Attributes, Cut / Copy / Paste, Search Online; groups folded, moved and renamed.
                MachineMenu("206-machines-menu-group", view.PointOf(demo.Bass, window), "Machines: a generator's menu in P6c: Attributes, Cut / Copy / Paste, Search Online and the machine's own commands after Delete; the Group submenu with New Group From Selection.", "Right-click a generator ▸ Group.", "Group");
                Try("207-machines-group-folded", () =>
                {
                    var group = ReBuzz.Studio.Views.Machines.MachineEdits.NewGroup(Core, new[] { demo.Bass, demo.Delay });
                    Assert.That(group, Is.Not.Null);
                    try
                    {
                        view.Canvas.Redraw();
                        Settle();
                        Shot(window, "207-machines-group-open", "Machines: a group of two machines drawn as a dashed frame with its name on a tab; the tab drags the group, a double click on it folds it.", "Right-click a machine ▸ Group ▸ New Group From Selection.");
                        ReBuzz.Studio.Views.Machines.MachineEdits.Fold(Core, group);
                        view.Canvas.Redraw();
                        Settle();
                        Shot(window, "208-machines-group-folded", "Machines: the same group folded to one box with its name and how many machines it holds; the wires to the machines in it end on the box. A double click opens it; right click: Rename, Unfold, Delete Group.", "Double-click the group's tab.");
                        Click(window, view.FromCanvas(view.Canvas.FoldedBoxOf(group).Center, window), MouseButton.Right);
                        menus.Add(view.Canvas.OpenMenu!);
                        Shot(window, "209-machines-group-menu", "Machines: a folded group's menu.", "Right-click the folded group.");
                    }
                    finally
                    {
                        CloseMenus();
                        ReBuzz.Studio.Views.Machines.MachineEdits.DeleteGroup(Core, group);
                        view.Canvas.Redraw();
                        Settle();
                    }
                });

                // The Sequence View's menus.
                window.ShowView("Sequence");
                Settle();
                var seq = Tab<SequenceView>(window, "Arrangement");
                Try("210-sequence-menu", () =>
                {
                    var geo = seq.Canvas.Geometry;
                    var p = seq.Canvas.TranslatePoint(new Point(geo.XOf(4 * 16) + 6, geo.YOf(1) + geo.TrackHeight * 0.6), window)!.Value;
                    Click(window, p);
                    Click(window, p, MouseButton.Right);
                    Assert.That(seq.OpenMenu?.IsOpen, Is.True);
                    menus.Add(seq.OpenMenu!);
                    Shot(window, "210-sequence-menu", "Sequence: the context menu on a pattern (Add Track ▸, Delete/Move Track, Set Start/End, Time Signature…, Insert/Delete, Color ▸, Rename Machine…, Quick Create Patterns ▸, Cut/Copy/Paste).", "Right-click in the Sequence View (or Ctrl+Enter).");
                });
                Try("211-sequence-menu-colour", () =>
                {
                    var geo = seq.Canvas.Geometry;
                    var p = seq.Canvas.TranslatePoint(new Point(geo.XOf(4 * 16) + 6, geo.YOf(1) + geo.TrackHeight * 0.6), window)!.Value;
                    Click(window, p, MouseButton.Right);
                    menus.Add(seq.OpenMenu!);
                    Hover(ItemOf(seq.OpenMenu!, "Color"));
                    Shot(window, "211-sequence-menu-colour", "Sequence: Color ▸, the pattern colours.", "Right-click a pattern ▸ Color.");
                });
                Try("212-sequence-add-track", () =>
                {
                    Click(window, Centre(seq.AddTrackButton, window));
                    Assert.That(seq.OpenMenu?.IsOpen, Is.True);
                    menus.Add(seq.OpenMenu!);
                    Shot(window, "212-sequence-add-track", "Sequence: the + button's menu, a track for one of the song's machines.", "The + button above the track list.");
                });

                // The Session View's menus.
                window.ShowView("Session");
                Settle();
                var session = Tab<SessionView>(window, "Session");
                void SessionMenu(string id, Control target, double fx, string what, string how, params string[] path)
                {
                    Try(id, () =>
                    {
                        Click(window, Centre(target, window, fx), MouseButton.Right);
                        var menu = session.LastMenu;
                        Assert.That(menu?.IsOpen, Is.True, "a menu opened");
                        menus.Add(menu!);
                        ItemsControl level = menu!;
                        foreach (var p in path)
                        {
                            var item = ItemOf(level, p);
                            Hover(item);
                            level = item;
                        }
                        Shot(window, id, what, how);
                    });
                }
                SessionMenu("220-session-menu-clip", session.SlotAt(1, 1), 0.6, "Session: a clip's menu (Launch, Stop, Edit notes…, Rename…, Duplicate, Delete, Colour ▸, Assign pattern ▸, MIDI learn…, Pattern View moves).", "Right-click a clip.");
                SessionMenu("221-session-menu-clip-colour", session.SlotAt(1, 1), 0.6, "Session: a clip's Colour ▸ palette.", "Right-click a clip ▸ Colour.", "Colour");
                SessionMenu("222-session-menu-empty-slot", session.SlotAt(1, 3), 0.6, "Session: an empty slot's menu (Create clip, Assign pattern ▸, MIDI learn…).", "Right-click an empty slot.");
                SessionMenu("223-session-menu-scene", session.Scenes[2], 0.5, "Session: a scene's menu (Launch, Rename…, Tempo…, Time signature…, Colour ▸, Insert/Duplicate/Delete scene, Insert into arrangement…, Follow action).", "Right-click a scene's button.");
                SessionMenu("224-session-menu-track", session.Headers[0], 0.5, "Session: a track header's menu (colour, rename, arm, stop, remove the track…).", "Right-click a track's header.");
                Try("225-session-menu-settings", () =>
                {
                    Click(window, Centre(session.SettingsButton, window));
                    menus.Add(session.LastMenu!);
                    Shot(window, "225-session-menu-settings", "Session: the ⚙ menu (new clip and record length, count-in, sync, controllers, return tracks, cue output, MIDI learn, keyboard shortcuts).", "The toolbar's ⚙ button.");
                });
                Try("226-session-menu-settings-returns", () =>
                {
                    Click(window, Centre(session.SettingsButton, window));
                    menus.Add(session.LastMenu!);
                    Hover(ItemOf(session.LastMenu!, "Return tracks"));
                    Shot(window, "226-session-menu-settings-returns", "Session: ⚙ ▸ Return tracks (Verb is a return).", "⚙ ▸ Return tracks.");
                });
                Try("227-session-menu-add-track", () =>
                {
                    var add = session.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "FlatButton" && c.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "+ Track"));
                    Click(window, Centre(add, window));
                    menus.Add(session.LastMenu!);
                    Shot(window, "227-session-menu-add-track", "Session: + Track, a track for a machine without one.", "The toolbar's + Track button.");
                });
            }
            finally
            {
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 3xx: windows and dialogs

        [AvaloniaTest, Order(4)]
        public void WindowsAndDialogs()
        {
            BeforeAll();
            using var restore = Studio.AfterTest(() =>
            {
                AudioPage.OutputSource = null;
                AudioPage.InputSource = null;
                AudioPage.ReopenHook = null;
                Dialogs.Responder = null;
                Dialogs.TextResponder = null;
                ArrangementInsertDialog.Responder = null;
            });
            var demo = LoadDemo();
            var window = Open("Machines");
            try
            {
                // Preferences, every page.
                AudioPage.OutputSource = () => new[] { "PortAudio: MacBook Pro Speakers (Core Audio)", "PortAudio: External Headphones (Core Audio)", "PortAudio: Studio Display Speakers (Core Audio)" };
                AudioPage.InputSource = () => new[] { "PortAudio: MacBook Pro Microphone (Core Audio)" };
                AudioPage.ReopenHook = _ => { };
                const string Devices = "Device lists injected (headless has no audio devices; on a Mac PortAudio lists CoreAudio's).";
                var prefs = PreferencesWindow.Open(Studio.Host, PreferencesWindow.AudioPageName);
                Settle();
                string[] pages = prefs.Pages.Select(p => p.Title).ToArray();
                int n = 0;
                foreach (var page in pages)
                {
                    string id = $"{300 + n * 2:000}-prefs-{page.ToLowerInvariant()}";
                    int k = n++;
                    Try(id, () =>
                    {
                        prefs.ShowPage(page);
                        Settle();
                        Shot(prefs, id, $"Preferences ▸ {page} (a window of its own; changes apply as they are made).",
                            page == "Agent" ? $"{Product.Name} ▸ Preferences… (⌘,) ▸ Agent; or Window ▸ Agent connection… (⇧⌘A)." : $"{Product.Name} ▸ Preferences… (⌘,) ▸ {page}.",
                            notes: page == "Audio" ? Devices : null);
                        var scroll = prefs.Selected as ScrollViewer;
                        if (scroll != null && scroll.Extent.Height > scroll.Viewport.Height + 4)
                        {
                            scroll.Offset = new Vector(0, scroll.Extent.Height);
                            Settle();
                            string more = $"{300 + k * 2 + 1:000}-prefs-{page.ToLowerInvariant()}-scrolled";
                            Shot(prefs, more, $"Preferences ▸ {page}, scrolled to its end.", $"{Product.Name} ▸ Preferences… ▸ {page}, scrolled.", notes: page == "Audio" ? Devices : null);
                            scroll.Offset = default;
                        }
                    });
                }
                Try("320-prefs-agent-on", () =>
                {
                    prefs.ShowPage("Agent");
                    var agent = prefs.Page<AgentPage>();
                    agent.AllowBox.IsChecked = true;
                    Wait(300);
                    Shot(prefs, "320-prefs-agent-on", "Preferences ▸ Agent with agents allowed: the listening state and the `claude mcp add` line with its token, Copy and New token.",
                        "Window ▸ Agent connection… (⇧⌘A), then Allow agents.", notes: "The token is this test run's throwaway one.");
                    agent.AllowBox.IsChecked = false;
                    Settle();
                });
                Try("321-prefs-safety-question", () =>
                {
                    prefs.ShowPage("Safety");
                    var audio = prefs.Page<SafetyPage>();
                    DialogOnOpen(prefs, () => audio.LimiterBox.IsChecked = false, "321-prefs-safety-question", "The safety question before the limiter goes off (Preferences ▸ Safety): Turn Off or Keep On, Keep On on Return and Escape.",
                        "Preferences ▸ Safety: untick Safety limiter.", notes: Devices);
                    Settle();
                });
                Try("322-prefs-restart-note", () =>
                {
                    // An agent sets the render threads (a restart-required setting): the open window shows it at once, with the line under the pages.
                    prefs.ShowPage("Audio");
                    var threads = prefs.Model.Entry("audio.AudioThreads");
                    string was = ReBuzz.Agent.App.SettingsService.Get(threads);
                    ReBuzz.Agent.App.SettingsService.Apply(prefs.Model.App, threads, was == "2" ? "3" : "2", ReBuzz.Agent.App.SettingsService.AgentSource);
                    Settle();
                    Shot(prefs, "322-prefs-restart-note", "Preferences ▸ Audio after an agent changed the render threads (P3f): the page shows the new value at once, and a line under the pages says that the change waits for a restart. The change is in settings_history as Agent.",
                        "Ask the agent to change a setting (settings_set) while Preferences is open.", notes: Devices);
                    ReBuzz.Agent.App.SettingsService.Apply(prefs.Model.App, threads, was, ReBuzz.Agent.App.SettingsService.AgentSource);
                    Settle();
                });
                Try("326-prefs-audio-streaming", () =>
                {
                    // NA1b: the Disk streaming section (the last on the Audio page), with a cache of two files made for the picture in a folder of its own.
                    string folder = AudioCache.DataFolder;
                    string temp = Path.Combine(Path.GetTempPath(), "neobuzz-gallery-cache-" + Environment.ProcessId);
                    try
                    {
                        Directory.CreateDirectory(temp);
                        AudioCache.DataFolder = temp;
                        File.WriteAllBytes(Path.Combine(temp, "0123456789abcdef.nbdc"), new byte[5 * 1024 * 1024]);
                        File.WriteAllBytes(Path.Combine(temp, "fedcba9876543210.nbdc"), new byte[3 * 1024 * 1024]);
                        prefs.ShowPage("Audio");
                        prefs.Page<AudioPage>().ShowCache();
                        Settle();
                        if (prefs.Selected is ScrollViewer scroll)
                        {
                            scroll.Offset = new Vector(0, scroll.Extent.Height);
                            Settle();
                        }
                        Shot(prefs, "326-prefs-audio-streaming", "Preferences ▸ Audio ▸ Disk streaming (NA1b): the audio cache's size and files with Clear, the length from which audio streams, and the cache size limit.",
                            $"{Product.Name} ▸ Preferences… ▸ Audio, scrolled to Disk streaming.", notes: "The two cache files are made for the picture (8 MB of nothing) in a folder of their own; the device lists are as in the other Audio shots. " + Devices);
                    }
                    finally
                    {
                        AudioCache.DataFolder = folder;
                        try { Directory.Delete(temp, true); } catch (IOException) { }
                        prefs.Page<AudioPage>().ShowCache();
                        Settle();
                    }
                });
                Try("323-prefs-restore-question", () =>
                {
                    prefs.ShowPage("Advanced");
                    DialogOnOpen(prefs, () => _ = prefs.RestoreDefaults(), "323-prefs-restore-question", "Restore Defaults on the Advanced page (P3f): it asks first, since the engine flags change how audio is made; Cancel on Return and Escape.",
                        "Preferences ▸ Advanced ▸ Restore Defaults.");
                    Settle();
                });
                // H1b (plugin-host.md): Preferences ▸ Plug-ins, with a library written for the picture: no plug-in of anyone's is scanned.
                Try("324-prefs-plugins", () => PluginsGallery(prefs, "324-prefs-plugins", withBlocked: false,
                    "Preferences ▸ Plug-ins (H1b): the folders the plug-in host scans (standard ones, and the ones added here with Remove), Rescan and Rescan everything, and what was found.",
                    "Preferences ▸ Plug-ins."));
                Try("325-prefs-plugins-blocked", () => PluginsGallery(prefs, "325-prefs-plugins-blocked", withBlocked: true,
                    "Preferences ▸ Plug-ins with two blocked plug-ins: each with the reason the scan gave (a crash, a missing dependency) and its Unblock button; Unblock all below.",
                    "Preferences ▸ Plug-ins, after a scan met plug-ins that crashed or would not load."));
                prefs.Close();
                Settle();

                Try("330-about", () => DialogOnOpen(window, () => AboutWindow.Open(), "330-about", $"About {Product.Name}: the build and the credits.", $"{Product.Name} ▸ About {Product.Name}."));
                Try("331-keyboard-shortcuts", () => DialogOnOpen(window, () => ReBuzz.Studio.Shell.Commands.ShortcutsWindow.Open(window),
                    "331-keyboard-shortcuts", "Help ▸ Keyboard Shortcuts: one searchable list from the keymap the menus are made from (ux-overhaul.md §4): the commands by menu, then each view's own keys.",
                    "Help ▸ Keyboard Shortcuts… (⌘/)."));
                Try("339-keyboard-shortcuts-search", () => DialogOnOpen(window, () => ReBuzz.Studio.Shell.Commands.ShortcutsWindow.Open(window).Search = "delete",
                    "339-keyboard-shortcuts-search", "Keyboard Shortcuts searched for \"delete\": every key that deletes, in every view.", "Type in the window's search box."));
                // P6b (ux-overhaul.md §9): ReBuzz.exe's tool windows, in the studio.
                Try("530-cpu-monitor", () => DialogOnOpen(window, () => ReBuzz.Studio.Shell.Tools.CpuMonitorWindow.Open(Studio.Host, window),
                    "530-cpu-monitor", "View ▸ CPU Monitor: each machine's share of the audio engine's time as a bar and a percentage, its cycles per sample and the longest it held the engine's lock; the engine's total and the overhead no machine accounts for.",
                    "View ▸ CPU Monitor."));
                Try("531-signal-analysis", () => DialogOnOpen(window, () =>
                {
                    var master = Studio.Host.Core.SongCore.Machines.First(m => m.DLL.Info.Type == BuzzGUI.Interfaces.MachineType.Master);
                    var analysis = ReBuzz.Studio.Shell.Tools.SignalAnalysisWindow.Open(Studio.Host, master, window);
                    int rate = System.Math.Max(1, Studio.Host.Core.SelectedAudioDriverSampleRate);
                    var tone = new float[8192];
                    for (int i = 0; i < tone.Length / 2; i++)
                    {
                        double t = i / (double)rate;
                        tone[2 * i] = (float)(12000 * System.Math.Sin(2 * System.Math.PI * 220 * t) + 6000 * System.Math.Sin(2 * System.Math.PI * 1320 * t) + 2000 * System.Math.Sin(2 * System.Math.PI * 5280 * t));
                        tone[2 * i + 1] = (float)(9000 * System.Math.Sin(2 * System.Math.PI * 330 * t) + 3000 * System.Math.Sin(2 * System.Math.PI * 2640 * t));
                    }
                    analysis.Tap.Add(tone, true);
                    analysis.Tick();
                }, "531-signal-analysis", "Signal Analysis of the Master (a machine's menu ▸ Signal Analysis...): the spectrum on a log frequency axis with a window to choose and a peak line that falls slowly, the scope under it. Here fed a test tone.",
                    "Right click a machine ▸ Signal Analysis...."));
                Try("532-hard-disk-recorder", () => DialogOnOpen(window, () => ReBuzz.Studio.Shell.Tools.HardDiskRecorderWindow.Open(Studio.Host, window),
                    "532-hard-disk-recorder", "View ▸ Hard Disk Recorder: records the Master to a wave file as it plays (Record), or exactly over the song's loop (Record loop); the file is named after the song and numbered, the bit depth is a choice.",
                    "View ▸ Hard Disk Recorder."));
                Try("533-debug-console", () => DialogOnOpen(window, () =>
                {
                    Studio.Host.Core.DCWriteLine("Song loaded: " + GallerySong.Title);
                    Studio.Host.Core.DCWriteLine("Machine Neo Delay: latency 0 samples", BuzzGUI.Interfaces.DCLogLevel.Information);
                    Studio.Host.Core.DCWriteLine("Audio: the output was reopened at 48000 Hz", BuzzGUI.Interfaces.DCLogLevel.Warning);
                    ReBuzz.Studio.Shell.Tools.DebugConsoleWindow.Open(Studio.Host, window);
                }, "533-debug-console", "View ▸ Debug Console: the engine's log, newest at the bottom, with a level filter, a text filter, Clear, Copy and Follow.",
                    "View ▸ Debug Console."));
                Try("534-midi-bindings", () => DialogOnOpen(window, () =>
                {
                    var backend = new ReBuzz.Agent.MidiBackend(Studio.Host.Core);
                    var machine = Studio.Host.Core.SongCore.MachinesList.FirstOrDefault(m => m.DLL.Info.Type == BuzzGUI.Interfaces.MachineType.Generator);
                    var parameter = machine?.ParameterGroups.SelectMany(g => g.Parameters).FirstOrDefault(p => p.Type != BuzzGUI.Interfaces.ParameterType.Note && p.Type != BuzzGUI.Interfaces.ParameterType.Internal && p.Group.Type == BuzzGUI.Interfaces.ParameterGroupType.Global);
                    if (machine != null && parameter != null) backend.Bind(machine.Name, parameter.Name, 0, 1, 74);
                    ReBuzz.Studio.Shell.Tools.MidiBindingsWindow.Open(Studio.Host, window);
                }, "534-midi-bindings", "View ▸ MIDI Bindings: every parameter a MIDI controller drives, by machine, parameter and track, with its channel and controller, a filter and a Clear button per row.",
                    "View ▸ MIDI Bindings."));
                // P6c (ux-overhaul.md §9): the on-screen piano keyboard.
                Try("537-piano-keyboard", () => DialogOnOpen(window, () =>
                {
                    Studio.Host.Core.MIDIFocusMachine = Studio.Host.Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == BuzzGUI.Interfaces.MachineType.Generator);
                    ReBuzz.Studio.Shell.Tools.PianoKeyboardWindow.Open(Studio.Host, window);
                }, "537-piano-keyboard", "View ▸ Piano Keyboard (or the Keys button in the MIDI bar): the keyboard every machine window has under its content, here playing the machine that has the MIDI focus.",
                    "View ▸ Piano Keyboard."));
                Try("535-latency", () => DialogOnOpen(window, () =>
                    _ = ReBuzz.Studio.Shell.Tools.LatencyDialog.Ask(Studio.Host.Core.SongCore.MachinesList.First(m => m.DLL.Info.Type != BuzzGUI.Interfaces.MachineType.Master)),
                    "535-latency", "Latency... on a machine's menu: the latency the machine reports and a switch to override it with a number of samples for the delay compensation.",
                    "Right click a machine ▸ Latency...."));
                Try("536-song-properties", () => DialogOnOpen(window, () => _ = ReBuzz.Studio.Shell.Tools.SongPropertiesDialog.Ask(Studio.Host.Core),
                    "536-song-properties", "Edit ▸ Song Properties...: tempo, ticks per beat and playback speed of the song, checked as they are typed.",
                    "Edit ▸ Song Properties..."));
                Try("332-save-changes", () =>
                {
                    Core.SetModifiedFlag();
                    DialogOnOpen(window, () => _ = SongFiles.ConfirmDiscard(), "332-save-changes", "Save changes? before a new song, opening another or closing: Don't Save, Cancel, Save (Return; macOS's order, the default rightmost).",
                        "File ▸ New / Open… / close the window with unsaved changes.");
                    Settle();
                    Core.Modified = false;
                });
                Try("333-rename-machine", () =>
                {
                    var view = Tab<MachineView>(window, "Machines");
                    Click(window, view.PointOf(demo.Lead, window), MouseButton.Right);
                    menus.Add(view.Canvas.OpenMenu!);
                    var rename = ItemOf(view.Canvas.OpenMenu!, "Rename...");
                    DialogOnOpen(window, () => Click(window, Centre(rename, window)), "333-rename-machine", "Rename a machine (the studio's text prompt).", "Right-click a machine ▸ Rename….");
                });
                Try("334-session-insert-arrangement", () =>
                {
                    window.ShowView("Session");
                    Settle();
                    var session = Tab<SessionView>(window, "Session");
                    Click(window, Centre(session.Scenes[1], window), MouseButton.Right);
                    menus.Add(session.LastMenu!);
                    DialogOnOpen(window, () => Assert.That(session.ClickMenuItem("Insert into arrangement…"), Is.True), "334-session-insert-arrangement",
                        "Session: Insert into arrangement (the scene's clips written into the Sequence View at a bar, for some bars).", "Right-click a scene ▸ Insert into arrangement….");
                });
                Try("335-session-scene-tempo", () =>
                {
                    var session = Tab<SessionView>(window, "Session");
                    Click(window, Centre(session.Scenes[2], window), MouseButton.Right);
                    menus.Add(session.LastMenu!);
                    DialogOnOpen(window, () => Assert.That(session.ClickMenuItem("Tempo…"), Is.True), "335-session-scene-tempo", "Session: a scene's tempo prompt.", "Right-click a scene ▸ Tempo….");
                });
                Try("336-sequence-time-signature", () =>
                {
                    window.ShowView("Sequence");
                    Settle();
                    var seq = Tab<SequenceView>(window, "Arrangement");
                    var geo = seq.Canvas.Geometry;
                    Click(window, seq.Canvas.TranslatePoint(new Point(geo.XOf(2 * 16) + 6, geo.YOf(1) + geo.TrackHeight * 0.6), window)!.Value, MouseButton.Right);
                    menus.Add(seq.OpenMenu!);
                    var item = ItemOf(seq.OpenMenu!, "Set Time Signature...");
                    DialogOnOpen(window, () => Click(window, Centre(item, window)), "336-sequence-time-signature", "Sequence: Set Time Signature (beats/note, as 4/4, or ticks; checked as it is typed).", "T in the Sequence View, or its menu ▸ Set Time Signature….");
                    window.ShowView("Machines");
                });
                Try("337-autosave-recovery", () =>
                {
                    // As after a crash: the song's autosave left behind, then offered at start-up (Autosave.OfferRecovery).
                    Core.SetModifiedFlag();
                    Assert.That(Core.Autosave.SaveNow(System.DateTime.Now), Is.True);
                    try
                    {
                        DialogOnOpen(window, () => _ = Autosave.OfferRecovery(Core), "337-autosave-recovery", $"At start-up after {Product.Name} didn't close normally: recover the autosaved changes? Discard… (asks again), Decide Later (Escape), Restore (Return).",
                            $"Start {Product.Name} after a crash, with unsaved changes autosaved.");
                    }
                    finally
                    {
                        Settle();
                        foreach (var leftover in SongAutosave.FindLeftovers()) SongAutosave.Delete(leftover.Path);
                        Core.Modified = false;
                    }
                });
                Try("338-error-open", () =>
                {
                    string broken = Path.Combine(SongFolder, "Broken song.bmx");
                    File.WriteAllBytes(broken, Encoding.ASCII.GetBytes("Buzz\0not a song at all"));
                    DialogOnOpen(window, () => _ = SongFiles.Open(broken), "338-error-open", "An error: a file that isn't a song, opened. What happened in words; the exception under Details (closed).", "File ▸ Open… a damaged file.");
                });
            }
            finally
            {
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 4xx: machine windows

        [AvaloniaTest, Order(5)]
        public void MachineWindows()
        {
            BeforeAll();
            var demo = LoadDemo();
            var window = Open("Machines");
            var made = new List<MachineCore>();
            // The machine windows on the Mac the gallery draws (1440 × 900 pt, less its 25 pt menu bar): the headless
            // platform's screen is 1920 × 1280, and it cuts windows off at 1080. They keep inside the work area and scroll.
            var workArea = MachineWindowFit.WorkArea;
            MachineWindowFit.WorkArea = _ => new Size(Width, Height - 25);
            try
            {
                MachineCore Add(string gear, string name)
                {
                    Core.SongCore.CreateMachine(gear, null, name, null, null, null, -1, 0.5f, 0.8f);
                    var m = Core.SongCore.MachinesList.Last(x => x.Name == name);
                    made.Add(m);
                    return m;
                }

                // Every Neo machine's editor, as a double click opens it.
                var neo = Core.MachineDLLs.Values.Where(d => d?.Info != null && d.Name.StartsWith("Neo ") && !d.Name.StartsWith("Neo Learn ") && !d.Name.StartsWith("Neo Plug") && d.Name != "Neo Patch" && d.Name != "Neo Tune" && d.Info.Type != MachineType.Master)
                    .Select(d => d.Name).OrderBy(x => x, System.StringComparer.Ordinal).ToList();
                // Dye Essence, a Neo-family machine with its own name (docs/specs/dye-essence.md), after them (the others keep their numbers).
                if (Core.MachineDLLs.ContainsKey("Dye Essence")) neo.Add("Dye Essence");
                // Neo Learn Subtractive (docs/specs/neo-learn.md), after Dye Essence for the same reason.
                foreach (var learn in new[] { "Neo Learn Subtractive", "Neo Learn FM", "Neo Learn Wavetable", "Neo Learn Additive", "Neo Learn Granular", "Neo Learn Physical", "Neo Learn String", "Neo Learn Formant", "Neo Learn Wavefolder", "Neo Learn Phase Distortion", "Neo Learn Vector", "Neo Learn Sampler", "Neo Learn Spectral", "Neo Patch" })
                    if (Core.MachineDLLs.ContainsKey(learn)) neo.Add(learn);
                // Neo Tune (docs/specs/neo-tune.md), after them for the same reason (Neo Utility, Neo Verb and Neo Wave keep their numbers).
                if (Core.MachineDLLs.ContainsKey("Neo Tune")) neo.Add("Neo Tune");
                int i = 0;
                foreach (var gear in neo)
                {
                    string id = $"{400 + i++:000}-editor-{gear.ToLowerInvariant().Replace(' ', '-')}";
                    // A machine with no editor window of its own (Neo Audio: its settings are the Audio page and the Device panel) has no shot here; its number stays free.
                    if (!HasEditorWindow(gear)) continue;
                    Try(id, () =>
                    {
                        var m = demo.Generators.Concat(new[] { demo.Drive, demo.Comp, demo.Delay, demo.Verb, demo.Eq, demo.Limiter }).FirstOrDefault(x => x?.DLL.Name == gear) ?? Add(gear, gear.Replace("Neo ", ""));
                        Core.UiHost.OpenGui(m, null);
                        var editor = StudioMachineUiHost.GuiWindowOf(m);
                        Assert.That(editor, Is.Not.Null, "its GUI opened");
                        var busy = m.ManagedMachine?.GetType().GetProperty("Busy");
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        while (busy?.GetValue(m.ManagedMachine) is true && clock.ElapsedMilliseconds < 5000) Thread.Sleep(20);
                        Wait(200);
                        Shot(editor!, id, $"{gear}'s editor ({m.Name}), as a double click opens it: a window of its own." + KeyboardUnder(m), "Double-click the machine in the Machine View.");
                        Core.UiHost.CloseWindows(m);
                    });
                }

                // Neo Tune with a voice sung through it (docs/specs/neo-tune.md §6): the live pitch display (the voice in grey, the corrected voice in
                // the accent colour, the scale's notes lit on the piano roll) and the scale keyboard after a click turned the scale into Custom.
                if (Core.MachineDLLs.ContainsKey("Neo Tune"))
                {
                    void Sing(MachineCore m, double seconds, double flat)
                    {
                        var instance = m.ManagedMachine;
                        var work = instance.GetType().GetMethod("Work", new[] { typeof(Buzz.MachineInterface.Sample[]), typeof(Buzz.MachineInterface.Sample[]), typeof(int), typeof(Buzz.MachineInterface.WorkModes) })!;
                        int rate = ReBuzzCore.masterInfo.SamplesPerSec;
                        var input = new Buzz.MachineInterface.Sample[256];
                        var output = new Buzz.MachineInterface.Sample[256];
                        int[] steps = { 0, 2, 4, 7, 9, 7, 4, 2 };
                        double phase = 0;
                        long n = 0;
                        for (int block = 0; block < seconds * rate / 256; block++)
                        {
                            for (int k = 0; k < 256; k++, n++)
                            {
                                double t = n / (double)rate;
                                double midi = 57 + steps[(int)(t / 0.6) % steps.Length] - flat + 0.25 * System.Math.Sin(2 * System.Math.PI * 5.5 * t) * System.Math.Min(1, (t % 0.6) / 0.3);
                                phase += 2 * System.Math.PI * 440.0 * System.Math.Pow(2, (midi - 69) / 12) / rate;
                                double v = 0;
                                for (int h = 1; h <= 12; h++) v += System.Math.Sin(h * phase) / (h * h * 0.6 + 1);
                                input[k] = new Buzz.MachineInterface.Sample((float)(v * 7000), (float)(v * 7000));
                            }
                            lock (ReBuzzCore.AudioLock) work.Invoke(instance, new object[] { output, input, 256, Buzz.MachineInterface.WorkModes.WM_READWRITE });
                        }
                    }
                    Try("580-neo-tune-live", () =>
                    {
                        var m = Add("Neo Tune", "Tune");
                        Core.UiHost.OpenGui(m, null);
                        var editor = StudioMachineUiHost.GuiWindowOf(m);
                        Assert.That(editor, Is.Not.Null, "its GUI opened");
                        Sing(m, 5.5, 0.3);
                        Dispatcher.UIThread.RunJobs();
                        Wait(200);
                        Shot(editor!, "580-neo-tune-live", "Neo Tune with a slightly flat voice sung through it (C major, Retune speed 25 ms): the live display shows the voice (grey) and the corrected voice (amber) over the last seconds on a piano roll with the scale's notes lit, the target note and the correction in cents at the top right, and the scale keyboard under it (the root marked).", "Double-click the machine; play a voice through it.");
                        Core.UiHost.CloseWindows(m);
                    });
                    Try("581-neo-tune-custom-scale", () =>
                    {
                        var m = Core.SongCore.MachinesList.First(x => x.Name == "Tune");
                        // The parameters (the editor shows them) and the machine's own properties (the engine takes them at once: no tick runs here).
                        void Set(string name, string property, int raw)
                        {
                            m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == name).SetValue(0, raw);
                            m.ManagedMachine.GetType().GetProperty(property)!.SetValue(m.ManagedMachine, raw);
                        }
                        Set("Scale", "Scale", 12);                      // Custom
                        Set("Custom notes", "Notes", 0b010010101001);   // C minor pentatonic: C, E flat, F, G, B flat
                        Set("Retune speed", "Retune", 0);
                        Set("Keep vibrato", "VibKeep", 0);
                        Core.UiHost.OpenGui(m, null);
                        var editor = StudioMachineUiHost.GuiWindowOf(m);
                        Assert.That(editor, Is.Not.Null, "its GUI opened");
                        Sing(m, 5.5, 0.0);
                        Dispatcher.UIThread.RunJobs();
                        Wait(200);
                        Shot(editor!, "581-neo-tune-custom-scale", "Neo Tune on a Custom scale (the notes clicked on the keyboard) with Retune speed 0 and Keep vibrato 0 (hard): the voice is held to the lit notes only, the stepped corrected line against the sung one.", "Click the keys of the scale keyboard to enable or mute notes; set Retune speed and Keep vibrato to 0.");
                        Core.UiHost.CloseWindows(m);
                    });
                }

                // Neo Patch's editor (docs/specs/neo-patch.md §4.5): the empty patch, the 20-node patch, a selected node with the side panel, the node menu, a refused loop.
                if (Core.MachineDLLs.ContainsKey("Neo Patch"))
                {
                    void PatchShot(string id, string kind, string what, string how)
                    {
                        Try(id, () =>
                        {
                            var m = Add("Neo Patch", "Patch " + kind);
                            var editor = NeoPatchStudioTests.GalleryShot(kind, m);
                            Wait(200);
                            Shot(editor, id, what, how);
                            Core.UiHost.CloseWindows(m);
                        });
                    }
                    PatchShot("480-neo-patch-empty", "empty", "Neo Patch's editor on an empty patch: what to do next in three steps, and a button that makes a basic voice.", "Double-click a Neo Patch machine; Select all and Delete (or open a new patch).");
                    PatchShot("481-neo-patch-twenty-nodes", "big20", "Neo Patch's editor on the 20-node patch: typed ports, coloured wires, per-voice nodes with a stacked edge, the macro strip below.", "Double-click a Neo Patch machine and load a patch; Fit shows all of it.");
                    PatchShot("482-neo-patch-selected-node", "selected", "A selected node: its controls in real units in the side panel, with Explain How and Sound under them.", "Click a node.");
                    PatchShot("483-neo-patch-node-menu", "menu", "The node menu: right-click the canvas, type to search.", "Right-click an empty part of the canvas.");
                    PatchShot("484-neo-patch-refused-loop", "cycle", "A wire that would make a loop is refused: the banner names the loop and says to use a Feedback node; the loop is outlined in red.", "Drag a wire from an output back to an input upstream of it.");
                }

                // P5a (ux-overhaul.md): the pages and the open panels of the three editors that gained them, in the states the default shots don't show.
                void P5aShot(string id, string gear, System.Action<Window, MachineCore> arrange, string what, string how)
                {
                    Try(id, () =>
                    {
                        var m = demo.Generators.FirstOrDefault(x => x?.DLL.Name == gear) ?? Add(gear, gear.Replace("Neo ", ""));
                        Core.UiHost.OpenGui(m, null);
                        var editor = StudioMachineUiHost.GuiWindowOf(m);
                        Assert.That(editor, Is.Not.Null, "its GUI opened");
                        arrange(editor!, m);
                        Dispatcher.UIThread.RunJobs();
                        Wait(200);
                        Shot(editor!, id, what + KeyboardUnder(m), how);
                        // The page shown last comes back in the next editor of the machine: put Synth back for the later shots.
                        foreach (var pages in editor!.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == "Pages"))
                            pages.GetType().GetProperty("Selected")!.SetValue(pages, 0);
                        Core.UiHost.CloseWindows(m);
                    });
                }
                // P5b (ux-overhaul.md): drag-to-modulate and the Samples view.
                Try("493-modulation-ring-and-drop-targets", () =>
                {
                    var lfo = Add("Neo LFO", "LFO");
                    var filter = Add("Neo Filter", "Filter");
                    var mod = ModulationBoard.Of(lfo)!;
                    Assert.That(ReBuzz.Agent.ModulationEdits.Add(Core, mod, filter, filter.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == "Cutoff"), 0, 60), Is.Null);
                    Core.UiHost.OpenGui(filter, null);
                    var editor = StudioMachineUiHost.GuiWindowOf(filter)!;
                    ModulationBoard.BeginDrag(mod);          // a drag in progress: every knob that takes a modulator is outlined
                    Dispatcher.UIThread.RunJobs();
                    Wait(200);
                    Shot(editor, "493-modulation-ring-and-drop-targets", "Neo Filter's editor while a modulator is dragged (P5b): every knob is outlined as a drop target, and Cutoff carries the ring of the modulation already on it (the arc is where it reaches, the tick the value you set).", "Press a modulator's output handle (in its editor, its device view or on its box on the canvas) and drag it over a knob.");
                    ModulationBoard.EndDrag(null);
                    Core.UiHost.CloseWindows(filter);
                });
                Try("494-modulation-matrix", () =>
                {
                    var lfo = Core.SongCore.MachinesList.First(m => m.Name == "LFO");
                    var filter = Core.SongCore.MachinesList.First(m => m.Name == "Filter");
                    var mod = ModulationBoard.Of(lfo)!;
                    ReBuzz.Agent.ModulationEdits.Add(Core, mod, filter, filter.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == "Resonance"), 0, -35);
                    var w = ReBuzz.Studio.Views.Modulation.ModulationMatrixWindow.Open(mod);
                    Settle();
                    w.Choose("LFO", "Filter", "Cutoff");
                    Settle();
                    Shot(w, "494-modulation-matrix", "The modulation matrix (P5b): every modulation in the song as a row (source, machine, parameter, depth, polarity, Remove) and, above, + Add with a source, a machine and a parameter chosen from lists.", "View ▸ Modulation Matrix.");
                    w.Close();
                    foreach (var m in new[] { lfo, filter }) Core.UiHost.ReleaseMachine(m);
                    Core.SongCore.DeleteMachines(new[] { lfo, filter });
                });
                Try("495-samples-view", () =>
                {
                    string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gallery-samples-" + System.Guid.NewGuid().ToString("N"));
                    System.IO.Directory.CreateDirectory(dir);
                    try
                    {
                        string[] names = { "Kick", "Snare", "Pad A3" };
                        for (int n = 0; n < names.Length; n++)
                        {
                            const int rate = 22050;
                            int frames = rate * (n + 1) / 2;
                            var data = new float[frames * 2];
                            for (int i = 0; i < frames; i++) data[2 * i] = data[2 * i + 1] = (float)(0.7 * System.Math.Sin(i * 2 * System.Math.PI * (110 + 55 * n) / rate) * System.Math.Exp(-3.0 * i / frames));
                            string path = System.IO.Path.Combine(dir, names[n] + ".wav");
                            ReBuzz.Agent.Audio.Wav.WriteStereoFloat(path, data, frames, rate);
                            Assert.That(ReBuzz.Agent.WaveEdits.Load(Core, path, n == 2 ? 9 : n, null, true, out _), Is.Null);
                        }
                        ReBuzz.Agent.WaveEdits.SetLoop(Core, 2, true, 2000, 9000);
                        var w = ReBuzz.Studio.Views.Samples.SamplesWindow.Open();
                        Settle();
                        w.Selected = 2;
                        Settle();
                        w.Selected = 9;
                        Settle();
                        Shot(w, "495-samples-view", "The Samples view (P5b): the song's wavetable with slots in hex as Buzz shows them (01 … C8) and their names; the chosen wave's name, length, rate, root note, loop points, waveform with the loop shaded, and Replace, Clear, Play.", "View ▸ Samples; drag an audio file into the window to load it.");
                        w.Close();
                        for (int n = 0; n < 10; n++) if (Core.SongCore.Wavetable.Waves[n] != null) Core.SongCore.Wavetable.LoadWave(n, null, null, false);
                    }
                    finally { try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { } }
                });
                Control EditorPart(Window w, string type, string name) =>
                    w.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == type && Avalonia.Automation.AutomationProperties.GetName(c) == name);
                void ClickIn(Window w, Control c, double x, double y)
                {
                    var p = c.TranslatePoint(new Point(x, y), w)!.Value;
                    w.MouseDown(p, MouseButton.Left);
                    w.MouseUp(p, MouseButton.Left);
                }
                P5aShot("490-editor-neo-poly-modulation", "Neo Poly", (w, m) =>
                {
                    var tabs = EditorPart(w, "PageTabs", "Page tabs");
                    ClickIn(w, tabs, 120, 12);
                }, "Neo Poly's editor on its Modulation page (P5a): the mod envelope, the two LFOs and the modulation matrix; Synth is the other page.", "Double-click Neo Poly, then the Modulation tab (or Right on the tabs).");
                P5aShot("491-editor-neo-fm6-operator-4", "Neo FM6", (w, m) =>
                {
                    var tiles = EditorPart(w, "InteractiveGraph", "Operator overview");
                    ClickIn(w, tiles, 3 * 196 + 98, 32);
                }, "Neo FM6's editor with operator 4 open (P5a): the six operators as tiles (role in the algorithm, frequency, level now), one open below.", "Double-click Neo FM6, then a tile (or the digits 1 to 6).");
                P5aShot("492-editor-neo-drums-snare", "Neo Drums", (w, m) =>
                {
                    var pads = w.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "InteractiveGraph" && System.Math.Abs(c.Bounds.Width - 480) < 1);
                    ClickIn(w, pads, 6 + 1 * (((480 - 12 - 5 * 6) / 6.0) + 6) + 10, 20);
                }, "Neo Drums' editor with the snare's pad selected (P5a): its panel alone, the mix strip with every pad's level and pan, the pad lit by the hit it played.", "Double-click Neo Drums, then click the snare's pad.");

                // The generic parameter window: a Neo machine, a Pedal synth, a Pedal effect.
                void Parameters(string id, MachineCore m, string what, string how)
                {
                    Try(id, () =>
                    {
                        Core.UiHost.OpenParameterWindow(m, null);
                        var w = StudioMachineUiHost.ParameterWindowOf(m);
                        Assert.That(w, Is.Not.Null, "the parameter window opened");
                        Settle();
                        Shot(w!, id, what + KeyboardUnder(m), how);
                        Core.UiHost.CloseWindows(m);
                    });
                }
                Parameters("450-parameters-neo-poly", demo.Bass, "The parameter window of Bass (Neo Poly): every parameter as a slider with its value, presets, track choice.", "Right-click a machine ▸ Parameters….");
                string synth = new[] { "Pedal SH101", "Pedal Juno106", "Pedal M1", "Pedal FM" }.FirstOrDefault(Core.MachineDLLs.ContainsKey);
                string effect = new[] { "Pedal Comp", "Pedal Plate", "Pedal Chorus" }.FirstOrDefault(Core.MachineDLLs.ContainsKey);
                if (synth != null) Parameters("451-parameters-pedal-synth", Add(synth, synth.Replace("Pedal ", "")), $"The parameter window of a Pedal synth ({synth}): the generic view (Pedal machines have no editor of their own here).", "Double-click a Pedal machine, or its menu ▸ Parameters….");
                else failures.Add("mac-451: no Pedal synth in Gear");
                if (effect != null) Parameters("452-parameters-pedal-effect", Add(effect, effect.Replace("Pedal ", "")), $"The parameter window of a Pedal effect ({effect}).", "Double-click a Pedal machine, or its menu ▸ Parameters….");
                else failures.Add("mac-452: no Pedal effect in Gear");
                Try("453-parameters-menu", () =>
                {
                    Core.UiHost.OpenParameterWindow(demo.Bass, null);
                    var w = StudioMachineUiHost.ParameterWindowOf(demo.Bass)!;
                    Settle();
                    var knob = w.GetVisualDescendants().OfType<ReBuzz.Studio.Views.Device.DeviceCell>().First().Face;
                    Click(w, DeviceTestKit.Centre(w, knob), MouseButton.Right);
                    Shot(w, "453-parameters-menu", "A parameter's menu in the Device window (right-click on a knob).", "Right-click a knob in the Device window.");
                    Core.UiHost.CloseWindows(demo.Bass);
                });

                // The Device panel (ux-overhaul.md, the parameter panel): cards of knobs docked (one row, scrolling sideways) and floating
                // (wrapping), for four machines, and the Inspector with the macros and the pinned parameters.
                foreach (var (gear, slug) in new[] { ("Neo Filter", "neo-filter"), ("Neo Poly", "neo-poly"), ("Neo Drums", "neo-drums"), ("Dye Essence", "dye-essence"), ("Neo EQ", "neo-eq"), ("Pedal Gain Multi", "pedal-gain-multi") })
                {
                    Try("454-device-docked-" + slug, () =>
                    {
                        var m = Add(gear, "Gallery " + gear);
                        var page = new ReBuzz.Studio.Views.Device.DevicePage(m, floating: false);
                        page.SetEditor(false, remember: false);
                        var w = new Window { Width = 1100, Height = 330, Content = page };
                        w.Show();
                        Settle();
                        Shot(w, "454-device-docked-" + slug, $"The Device panel docked for {gear}: its sections as cards of knobs in one row that scrolls sideways (every card whole above the bar, the track table cut to the panel), a chip per section, search, Changed only, Knobs | List; the keys toggle (note icon) sits by Presets.", "Select the machine; Detail ▸ Device.");
                        w.Close();
                    });
                    Try("455-device-floating-" + slug, () =>
                    {
                        var m = Core.SongCore.MachinesList.First(x => x.Name == "Gallery " + gear);
                        var page = new ReBuzz.Studio.Views.Device.DevicePage(m, floating: true);
                        page.SetEditor(false, remember: false);
                        var w = new Window { Width = gear == "Dye Essence" ? 1440 : 900, Height = gear == "Dye Essence" ? 820 : 620, Content = page };
                        w.Show();
                        Settle();
                        Shot(w, "455-device-floating-" + slug, $"The Device panel floating for {gear}: cards in columns, each to the shortest column (no gaps under short cards), choices on top of each card, one colour, sentence-case labels, a \"+N\" menu for the chips that don't fit.", "Right-click a machine ▸ Parameters…, or Float in the Device panel.");
                        w.Close();
                    });
                }
                Try("456-device-list-mode", () =>
                {
                    var m = Core.SongCore.MachinesList.First(x => x.Name == "Gallery Neo Poly");
                    var page = new ReBuzz.Studio.Views.Device.DevicePage(m, floating: true);
                    page.SetEditor(false, remember: false);
                    page.Parameters.SetMode(ReBuzz.Studio.Views.Device.DeviceView.DeviceMode.List, remember: false);
                    var w = new Window { Width = 760, Height = 560, Content = page };
                    w.Show();
                    Settle();
                    Shot(w, "456-device-list-mode", "The Device panel in List mode: rows in columns with group headers, filled bars with the value inside.", "Device panel ▸ List.");
                    w.Close();
                });
                Try("458-device-knob-menu", () =>
                {
                    var m = Core.SongCore.MachinesList.First(x => x.Name == "Gallery Dye Essence");
                    var page = new ReBuzz.Studio.Views.Device.DevicePage(m, floating: true);
                    page.SetEditor(false, remember: false);
                    var w = new Window { Width = 900, Height = 700, Content = page };
                    w.Show();
                    Settle();
                    var cell = page.Parameters.Cards.Shown.SelectMany(s => s.Card.Knobs).Take(2).Select(p => page.Parameters.Cards.Cell(p)!).Last();
                    Click(w, DeviceTestKit.Centre(w, cell.Face), MouseButton.Right);
                    Shot(w, "458-device-knob-menu", "A knob's menu: Reset, Type a Value, Pin to Inspector, Automate (adds the parameter's arrangement lane), Modulate (a modulator or the matrix), Lock and Hide (ticks), MIDI binding. Keyboard Sends MIDI Notes is now in the bar and the Presets menu. The List rows have the same menu.", "Right-click a knob or a row in the Device panel.");
                    w.Close();
                });
                Try("459-device-edit-features", () =>
                {
                    var m = Core.SongCore.MachinesList.First(x => x.Name == "Gallery Dye Essence");
                    string hiddenFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gallery-hidden-" + System.Guid.NewGuid().ToString("N") + ".xml");
                    ReBuzz.Studio.Parameters.ParameterVisibility.FilePath = hiddenFile;
                    ReBuzz.Studio.Parameters.ParameterVisibility.Reset();
                    var page = new ReBuzz.Studio.Views.Device.DevicePage(m, floating: true);
                    page.SetEditor(false, remember: false);
                    var w = new Window { Width = 1200, Height = 760, Content = page };
                    w.Show();
                    Settle();
                    try
                    {
                        var view = page.Parameters;
                        var knobs = view.Cards.Shown.SelectMany(s => s.Card.Knobs).ToList();
                        foreach (var p in knobs.Take(3)) ReBuzz.Studio.Views.Device.DeviceMenus.SetLocked(m, p, true);
                        foreach (var p in knobs.Skip(3).Take(2)) ReBuzz.Studio.Parameters.ParameterVisibility.SetVisible(p.Parameter, false);
                        view.ShowHidden = true;
                        Settle();
                        ReBuzz.Studio.Views.Device.DeviceFunctions.OpenMorph(m, page.PresetsButton, null);
                        Settle();
                        Shot(w, "459-device-edit-features", "The old Edit mode's features in the Device panel: locked parameters carry a padlock (left out of Randomize and Morph), hidden ones are dimmed while Show hidden is on, and the Presets menu's Morph flyout (preset A, preset B, a slider; one undo step).", "Right-click a knob ▸ Lock or Hide; Show hidden in the bar; Presets ▸ Morph Between A and B.");
                    }
                    finally
                    {
                        w.Close();
                        Core.PinStore!.Clear();
                        ReBuzz.Studio.Parameters.ParameterVisibility.FilePath = null;
                        ReBuzz.Studio.Parameters.ParameterVisibility.Reset();
                        try { System.IO.File.Delete(hiddenFile); } catch (System.IO.IOException) { }
                    }
                });
                Try("457-inspector-macros-and-pins", () =>
                {
                    var m = Core.SongCore.MachinesList.First(x => x.Name == "Gallery Neo Poly");
                    var layout = ReBuzz.Studio.Views.Device.DeviceLayout.Build(m);
                    foreach (var p in layout.All.Where(p => p.CanPin).Take(4)) Core.PinStore.Set(m.Name, p.Parameter.Name, true);
                    var device = new ReBuzz.Studio.Views.Inspector.InspectorDevice(Core);
                    var w = new Window { Width = 320, Height = 420, Content = new Border { Padding = new Thickness(12), Background = ReBuzz.Studio.Shell.Design.StudioTokens.SurfacePanelBrush, Child = device } };
                    w.Show();
                    device.Show(m);
                    Settle();
                    Shot(w, "457-inspector-macros-and-pins", "The Inspector's machine card: the eight macros, then the parameters pinned to it (pin from a knob's right-click menu).", "Select a machine; the Inspector's Selection tab.");
                    w.Close();
                    Core.PinStore.Clear();
                });

                // The machine keyboard (docs/specs/studio-mvp.md §3.12): playing, and hidden.
                Try("470-keyboard-playing", () =>
                {
                    Core.UiHost.OpenGui(demo.Bass, null);
                    var w = StudioMachineUiHost.GuiWindowOf(demo.Bass)!;
                    Wait(200);
                    var kb = StudioMachineUiHost.KeyboardOf(w);
                    Assert.That(kb, Is.Not.Null, "Bass's editor has the keyboard");
                    var key = kb!.Keys.ElementOf(69);
                    Assert.That(key, Is.Not.Null, "A4 is on the keyboard");
                    var at = key!.TranslatePoint(new Point(key.Bounds.Width / 2, key.Bounds.Height * 0.85), w)!.Value;
                    w.MouseMove(at);
                    w.MouseDown(at, MouseButton.Left);
                    foreach (var note in new[] { 48, 55, 64 }) Core.MachineManager.SendMidiInput(demo.Bass, 0x90 | (note << 8) | (72 << 16), false);
                    try
                    {
                        for (int i = 0; i < 4; i++) kb.Poll();
                        Wait(100);
                        Shot(w, "470-keyboard-playing", "Bass's editor (Neo Poly) with its keyboard playing: A4 held with the pointer low on the key (loud: the readout names the note and its velocity, the fill shows it), and C3, G3 and E4 lit from a MIDI keyboard. Keys light whatever plays them (a pointer, MIDI, the machine's own patterns).",
                            "Click and hold a key under a machine's window; drag across the keys to glide. Lower on a key plays louder.");
                    }
                    finally
                    {
                        w.MouseUp(at, MouseButton.Left);
                        foreach (var note in new[] { 48, 55, 64 }) Core.MachineManager.SendMidiInput(demo.Bass, 0x80 | (note << 8), false);
                        Settle();
                        Core.UiHost.CloseWindows(demo.Bass);
                    }
                });
                Try("471-keyboard-hidden", () =>
                {
                    Core.UiHost.OpenParameterWindow(demo.Bass, null);
                    var w = StudioMachineUiHost.ParameterWindowOf(demo.Bass)!;
                    Settle();
                    var kb = StudioMachineUiHost.KeyboardOf(w)!;
                    kb.SetShown(false);                    // not remembered: the gallery leaves the machine type's choice alone
                    try
                    {
                        Settle();
                        Shot(w, "471-keyboard-hidden", "Bass's parameter window with its keyboard hidden: the strip stays, its ▸ Keyboard button shows the keys again (the window shrinks and grows by them; the choice is kept per machine type).",
                            $"The ▾ Keyboard button over the keys, or {(System.OperatingSystem.IsMacOS() ? "⌘K" : "Ctrl+K")} in the window. Preferences ▸ General ▸ Machine Keyboard sets the default.");
                    }
                    finally
                    {
                        kb.SetShown(true);
                        Core.UiHost.CloseWindows(demo.Bass);
                    }
                });
                if (synth != null)
                {
                    Try("460-manifest-editor-pedal-synth", () =>
                    {
                        var m = made.First(x => x.DLL.Name == synth);
                        Assert.That(Core.UiHost.OpenEditor(m), Is.True, "Editor... opened");
                        var w = StudioMachineUiHost.EditorWindowOf(m);
                        Assert.That(w, Is.Not.Null);
                        Wait(200);
                        Shot(w!, "460-manifest-editor-pedal-synth", $"{synth}'s manifest editor (Editor…): its curated face, knobs in sections, the preset bar.", "Right-click a machine ▸ Editor… (or a double click, where Double-click Opens Editor is on).");
                        Core.UiHost.CloseWindows(m);
                    });
                }
            }
            finally
            {
                MachineWindowFit.WorkArea = workArea;
                foreach (var m in made) Core.UiHost.ReleaseMachine(m);
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 5xx: old songs, natives, safety, many machines

        // ------------------------------------------------------------------ 36x: Neo Audio clips in the Arrangement

        /// <summary>Two seconds of a drum loop (a kick on every beat of 120 BPM, a hat between), as a float WAV the song's rate: something with a shape to draw.</summary>
        static string DrumLoop(string name, int rate = 44100)
        {
            string path = Path.Combine(SongFolder, name);
            int n = rate * 4;
            var inter = new float[n * 2];
            var rnd = new System.Random(7);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)rate, beat = t * 2, inBeat = (beat - System.Math.Floor(beat)) * 0.5;
                double kick = System.Math.Sin(2 * System.Math.PI * (55 + 90 * System.Math.Exp(-inBeat * 30)) * inBeat) * System.Math.Exp(-inBeat * 9) * 0.9;
                double off = ((beat + 0.5) % 1) * 0.5;
                double hat = (rnd.NextDouble() * 2 - 1) * System.Math.Exp(-off * 60) * 0.25;
                inter[2 * i] = inter[2 * i + 1] = (float)(kick + hat);
            }
            using var f = libsndfile.SoundFile.Create(path, rate, 2, libsndfile.Format.SF_FORMAT_WAV | libsndfile.Format.SF_FORMAT_FLOAT);
            f.WriteFloat(inter, 0, n);
            return path;
        }

        [AvaloniaTest, Order(7)]
        public void AudioArrangement()
        {
            BeforeAll();
            LoadDemo();
            var window = Open();
            try
            {
                Try("036-arrangement-audio-clips", () =>
                {
                    var layout = Core.SessionLayout;
                    layout.Ensure();
                    var drums = layout.NewAudioTrack("Drum Loop");
                    var vox = layout.NewAudioTrack("Chops");
                    var laneD = Core.Song.Sequences.First(s => s.Machine == drums);
                    var laneV = Core.Song.Sequences.First(s => s.Machine == vox);
                    var editor = new ClipEditor(Core);
                    string file = DrumLoop("drum-loop.wav");
                    IPattern Import(IMachine m)
                    {
                        var pattern = layout.ImportAudio(layout.FirstTrackOf(m), 0, file, out string error);
                        Assert.That(pattern, Is.Not.Null, error);
                        return pattern;
                    }
                    ClipItem At(ISequence lane, int time) => editor.Read(lane).First(i => i.Time == time);
                    // Drums: a loop with fades, a second one crossfaded under it, the last turned down.
                    var first = Import(drums);
                    Assert.That(editor.Place(laneD, first, 16 * 4), Is.Null);
                    int end = At(laneD, 16 * 4).End;
                    Assert.That(editor.Fade(At(laneD, 16 * 4), 12, 24), Is.Null);
                    Assert.That(editor.Place(laneD, Import(drums), end - 12), Is.Null);        // overlaps the first: a crossfade
                    var third = At(laneD, end - 12);
                    Assert.That(editor.Place(laneD, Import(drums), third.End + 16), Is.Null);
                    Assert.That(editor.SetGain(At(laneD, third.End + 16), -9), Is.Null);
                    // Chops: one stretched to twice its length, one backwards.
                    Assert.That(editor.Place(laneV, Import(vox), 16 * 4 + 8), Is.Null);
                    var chop = At(laneV, 16 * 4 + 8);
                    Assert.That(editor.Stretch(chop, chop.Time + (chop.End - chop.Time) * 2), Is.Null);
                    chop = At(laneV, 16 * 4 + 8);
                    Assert.That(editor.Place(laneV, Import(vox), chop.End + 16), Is.Null);
                    Assert.That(editor.Reverse(At(laneV, chop.End + 16)), Is.Null);
                    Settle();
                    var seq = Tab<SequenceView>(window, "Arrangement");
                    window.ShowView("Sequence");
                    seq.SetArrangementMode(true);
                    Settle();
                    Click(window, seq.Arrangement.TranslatePoint(new Point(seq.Arrangement.Geometry.XOf(16 * 4 + 20), seq.Arrangement.YOf(seq.Arrangement.RowOf(laneD)) + 28), window)!.Value);
                    Shot(window, "036-arrangement-audio-clips", "Neo Audio clips in the Arrangement (neo-audio.md, NA2): waveforms drawn along the beats from a peak cache, warp markers, fade corners and the gain handle on the selected clip, a crossfade where two clips overlap, a clip stretched with Alt+drag of its right edge, a reversed clip (the \"rev\" badge), a turned-down clip.", "Drag a WAV onto the Arrangement, or audio_import; the handles on a selected clip.");
                    seq.SetArrangementMode(false);
                });
            }
            finally
            {
                Close(window);
            }
            AfterAll();
        }

        [AvaloniaTest, Order(7)]
        public void AudioPages()
        {
            BeforeAll();
            LoadDemo();
            var window = Open();
            try
            {
                var layout = Core.SessionLayout;
                layout.Ensure();
                var drums = layout.NewAudioTrack("Drum Loop");
                var vox = layout.NewAudioTrack("Chops");
                string file = DrumLoop("page-loop.wav");
                IPattern Import(IMachine m, int scene)
                {
                    var pattern = layout.ImportAudio(layout.FirstTrackOf(m), scene, file, out string error);
                    Assert.That(pattern, Is.Not.Null, error);
                    return pattern;
                }
                var inSession = Import(drums, 0);
                Import(drums, 1);
                var chops = Import(vox, 0);
                Import(vox, 2);
                var arranged = Import(drums, 2);
                layout.SetSlot(layout.FirstTrackOf(drums), 2, null);
                var laneV = Core.Song.Sequences.First(q => q.Machine == drums);   // the Arrangement's clip is on the visible Drum Loop lane
                var editor = new ClipEditor(Core);
                Assert.That(editor.Place(laneV, arranged, 16 * 4), Is.Null);
                var asset = layout.ViewOf(inSession).Asset;
                for (int i = 0; i < 300 && asset.Peaks == null; i++) { asset.PeaksOrBuild(); System.Threading.Thread.Sleep(10); Dispatcher.UIThread.RunJobs(); }
                Settle();

                Try("039-session-audio-slots", () =>
                {
                    window.ShowView("Session");
                    Settle();
                    Shot(window, "039-session-audio-slots", "The Session grid with audio clips (neo-audio.md, NA3): each slot of an audio clip draws a mini waveform from the peak cache through its warp, scaled by its gain, with the playing position over it.", "Drop or import audio into a slot.");
                });
                // Fork (docs/specs/pitch.md section 9): Convert to MIDI, from the clip's menu.
                Try("048-convert-menu", () =>
                {
                    var session = Tab<SessionView>(window, "Session");
                    window.ShowView("Session");
                    Settle();
                    Click(window, Centre(session.SlotAt(layout.FirstTrackOf(drums), 0), window, 0.6, 0.5), MouseButton.Right);
                    var menu = session.LastMenu;
                    Assert.That(menu?.IsOpen, Is.True, "a right click on an audio clip opens its menu");
                    menus.Add(menu!);
                    Hover(ItemOf(menu!, "Convert to MIDI"));
                    Shot(window, "048-convert-menu", "Session: an audio clip's menu with Convert to MIDI ▸ Melody…, Harmony…, Drums… (pitch.md section 9); the Arrangement's clip menu and the Audio page's MIDI button offer the same three.", "Right-click an audio clip ▸ Convert to MIDI.");
                    CloseMenus();
                });
                Try("049-convert-dialog", () =>
                {
                    var source = layout.ConvertSourceOfClip(inSession, out string error);
                    Assert.That(source, Is.Not.Null, error);
                    foreach (var (id, mode, what) in new[]
                    {
                        ("049-convert-dialog-melody", ConvertMode.Melody, "Convert to MIDI ▸ Melody…: what to listen for (any melody instrument, voice, guitar or bass), pitch bends, and the quantize grid and strength; Convert starts the job on a worker."),
                        ("049-convert-dialog-drums", ConvertMode.Drums, "Convert to MIDI ▸ Drums…: the sensitivity and the quantize grid and strength; the result is a new Neo Drums track with the hits as General MIDI notes."),
                    })
                    {
                        var dialog = new ReBuzz.Studio.Shell.ConvertFlow.ConvertWindow(layout, source, mode);
                        dialog.Show();
                        Settle();
                        try { DialogShot(window, dialog, id, what, "Right-click an audio clip ▸ Convert to MIDI ▸ " + mode + "…."); }
                        finally { dialog.Close(); }
                    }
                });
                Try("049-convert-progress", () =>
                {
                    var source = layout.ConvertSourceOfClip(inSession, out string error);
                    Assert.That(source, Is.Not.Null, error);
                    var dialog = new ReBuzz.Studio.Shell.ConvertFlow.ConvertWindow(layout, source, ConvertMode.Harmony);
                    dialog.Show();
                    dialog.Bar.IsVisible = true;
                    dialog.Bar.Value = 0.4;
                    dialog.GoButton.IsEnabled = false;
                    Settle();
                    try { DialogShot(window, dialog, "049-convert-progress", "Convert to MIDI while the job runs (here: Harmony, 40 %): the options locked, a progress bar; Cancel stops it and changes nothing.", "Convert in the dialog."); }
                    finally { dialog.Close(); }
                });
                Try("038-session-audio-page", () =>
                {
                    var session = Tab<SessionView>(window, "Session");
                    window.ShowView("Session");
                    Click(window, Centre(session.SlotAt(layout.FirstTrackOf(drums), 0), window, 0.6, 0.5));
                    window.Detail.ShowAudio();
                    Settle();
                    Shot(window, "038-session-audio-page", "The Audio page of the clip panel for a Session clip (NA3): the waveform with its loop and region handles, warp mode, tempo with halve, double and detect, transpose, detune, gain, reverse and loop, the start and loop region in bars, beats and ticks.", "Select an audio clip in the Session, then the Audio button of the detail panel, or double-click the clip.");
                });
                Try("037-arrangement-audio-page", () =>
                {
                    var seq = Tab<SequenceView>(window, "Arrangement");
                    window.ShowView("Sequence");
                    seq.SetArrangementMode(true);
                    Settle();
                    var item = editor.Read(laneV).First();
                    var r = seq.Arrangement.ClipRect(item);
                    Click(window, seq.Arrangement.TranslatePoint(new Point(r.X + r.Width * 0.3, r.Y + r.Height * 0.8), window)!.Value);
                    window.Detail.ShowAudio();
                    Settle();
                    Shot(window, "037-arrangement-audio-page", "The same Audio page for a clip selected in the Arrangement (NA3): the fades are here, and the clip's own loop and region are the arranged clip's.", "Select an audio clip in the Arrangement; the Audio page follows the selection.");
                    seq.SetArrangementMode(false);
                });
                Try("461-warp-marker-editing", () =>
                {
                    var session = Tab<SequenceView>(window, "Arrangement");
                    window.ShowView("Session");
                    var sv = Tab<SessionView>(window, "Session");
                    Click(window, Centre(sv.SlotAt(layout.FirstTrackOf(drums), 0), window, 0.6, 0.5));
                    window.Detail.ShowAudio();
                    Settle();
                    var page = (ReBuzz.Studio.Views.Audio.AudioClipPage)window.Detail.AudioPage;
                    page.Refresh(force: true);
                    Settle();
                    var wave = page.Wave;
                    wave.SetZoom(40);
                    Settle();
                    Point At(double beat) => Centre(wave, window, 0, 0.5) + new Vector(wave.X(beat) - wave.Bounds.Width / 2 * 0, 0);
                    void Double(Point p) { MachineViewTests.DoubleClick(window, p); Settle(); }
                    Double(At(1.1));
                    Double(At(3.2));
                    Double(At(5.0));
                    var marks = layout.AudioOf(inSession).Markers.OrderBy(m => m.Beat).ToList();
                    if (marks.Count > 3)
                    {
                        // Drag the second marker a beat on, as a user warps it.
                        var from = At(marks[2].Beat);
                        window.MouseMove(from); window.MouseDown(from, MouseButton.Left);
                        for (int i = 1; i <= 4; i++) window.MouseMove(from + new Vector(wave.PixelsPerBeat * 0.25 * i, 0), RawInputModifiers.LeftMouseButton);
                        window.MouseUp(from + new Vector(wave.PixelsPerBeat, 0), MouseButton.Left);
                        Settle();
                    }
                    Shot(window, "461-warp-marker-editing", "Editing warp markers on the Audio page (NA3): double-click adds a marker pinned to a transient when one is near, a drag warps the audio between markers and snaps to the beat grid (Shift is free), double-click or Del removes one.", "Double-click the waveform; drag a marker; Del.");
                });
                Try("462-warp-ghost-marker", () =>
                {
                    CloseMenus();
                    var page = (ReBuzz.Studio.Views.Audio.AudioClipPage)window.Detail.AudioPage;
                    var wave = page.Wave;
                    Point Mid(double x) => Centre(wave, window, 0, 0.5) + new Vector(x - wave.Bounds.Width / 2 * 0, 0);
                    var marked = Enumerable.Range(0, wave.Markers.Count).Select(k => wave.MarkerX(k)).ToList();
                    var clear = layout.TransientsOf(inSession).Select(t => wave.TransientX(t)).Where(x => x > 30 && x < wave.Bounds.Width - 30 && marked.All(m => Math.Abs(m - x) > 40)).ToList();
                    window.MouseMove(Mid(clear.Count > 0 ? clear[clear.Count / 2] : 200));
                    Settle();
                    Shot(window, "462-warp-ghost-marker", "Warp markers (WM1): hovering a transient shows a ghost marker; dragging it makes the marker and warps the audio in one undo step. The line under the tools names what a drag will do.", "Hover a transient on the waveform, then drag.");
                });
                Try("463-warp-selection-segment-tempo", () =>
                {
                    var page = (ReBuzz.Studio.Views.Audio.AudioClipPage)window.Detail.AudioPage;
                    var wave = page.Wave;
                    Point Mid(double x) => Centre(wave, window, 0, 0.5) + new Vector(x, 0);
                    int n = wave.Markers.Count;
                    Click(window, Mid(wave.MarkerX(Math.Min(1, n - 1))));
                    if (n > 3) Click(window, Mid(wave.MarkerX(2)), MouseButton.Left, RawInputModifiers.Shift);
                    Settle();
                    Shot(window, "463-warp-selection-segment-tempo", "Warp markers (WM1): a click selects a marker, Shift or " + (OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl") + "+click adds more (a drag on the waveform selects a time range); the tempo of the segments before and after it is shown, editable, with halve and double.", "Click a marker, Shift+click another.");
                });
                Try("464-warp-context-menu", () =>
                {
                    var page = (ReBuzz.Studio.Views.Audio.AudioClipPage)window.Detail.AudioPage;
                    var wave = page.Wave;
                    Click(window, Centre(wave, window, 0, 0.6) + new Vector(wave.X(6.4), 0), MouseButton.Right);
                    menus.Add(wave.LastMenu!);
                    Settle();
                    Shot(window, "464-warp-context-menu", "Warp markers (WM1): the waveform's menu (Set bar 1 here, Warp from here (auto-warp from that moment: it follows the tempo changes, AW1), Warp from here, straight (one tempo), Warp as a loop of N bars, markers at the transients or on every bar, Delete, Remove all, Quantize), each one undo step with its key beside it.", "Right-click the waveform.");
                    CloseMenus();
                });
            }
            finally
            {
                Close(window);
            }
            AfterAll();
        }

        [AvaloniaTest, Order(6)]
        public void SongsAndStates()
        {
            BeforeAll();
            using var restore = Studio.AfterTest(() =>
            {
                MachineGuard.Enabled = true;
                Core.SafeMaster.Reset();
            });
            string repo = System.AppContext.BaseDirectory;
            while (repo != null && !File.Exists(Path.Combine(repo, "ReBuzz.sln"))) repo = Path.GetDirectoryName(repo);
            NewSong();
            var window = Open("Machines");
            try
            {
                string old = repo == null ? null : Path.Combine(repo, "ExternalMachines", "source", "buzztrax-buzzmachines", "FSM", "Infector", "FSM_Infector_Demo.bmx");
                if (old != null && File.Exists(old))
                {
                    const string Song = "FSM_Infector_Demo.bmx (a Windows Buzz song)";
                    const string Natives = "Native (Windows) machines don't load on the Mac: they open as placeholders that keep their data.";
                    Dialogs.Responder = (m, c, b) => UserAnswer.No;
                    try
                    {
                        Assert.That(Await(SongFiles.Open(old)), Is.True, "the old song opened");
                    }
                    finally
                    {
                        Dialogs.Responder = null;
                    }
                    Settle();
                    window.RefreshNow();
                    Settle();
                    Try("500-old-song-machines", () => Shot(window, "500-old-song-machines", "An old Windows song with native machines: each a placeholder box marked Windows only, and the problem bar saying so in plain words (the song plays without them and saves them back unchanged), with Show Them.", "File ▸ Open… a .bmx made on Windows.", Song, Natives));
                    Try("501-old-song-sequence", () => { window.ShowView("Sequence"); Shot(window, "501-old-song-sequence", "The old song's arrangement.", "F4.", Song, Natives); });
                    Try("502-old-song-placeholder-parameters", () =>
                    {
                        var native = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type != MachineType.Master);
                        Core.UiHost.OpenParameterWindow(native, null);
                        var w = StudioMachineUiHost.ParameterWindowOf(native);
                        Assert.That(w, Is.Not.Null);
                        Settle();
                        Shot(w!, "502-old-song-placeholder-parameters", $"The parameter window of a placeholder ({native.Name}, {native.DLL.Name}).", "Right-click a placeholder ▸ Parameters….", Song, Natives);
                        Core.UiHost.CloseWindows(native);
                    });
                    window.ShowView("Machines");
                    NewSong();
                }
                else failures.Add("mac-500..502: the old demo song isn't in this checkout");

                // Many machines: every Neo and Pedal machine in one song.
                Try("510-machines-many", () =>
                {
                    NewSong();
                    var gear = Core.MachineDLLs.Values.Where(d => d?.Info != null && d.Info.Type != MachineType.Master && !d.Name.StartsWith("Neo Plug") && (d.Name.StartsWith("Neo ") || d.Name == "Dye Essence" || d.Name.StartsWith("Pedal ")))
                        .Select(d => d.Name).OrderBy(x => x).ToList();
                    int cols = 6;
                    for (int k = 0; k < gear.Count; k++)
                    {
                        float x = -0.9f + 1.6f * (k % cols) / (cols - 1), y = -0.85f + 1.6f * (k / cols) / System.Math.Max(1, (gear.Count - 1) / cols);
                        // As right-click ▸ New makes them (a spot taken moves to free space).
                        var instrument = Core.Instruments.First(i => i.MachineDLL?.Name == gear[k]);
                        var made = ReBuzz.Studio.Views.Machines.MachineEdits.Create(Core, instrument, x, y);
                        if (made is MachineCore mc) mc.Name = gear[k].Replace("Neo ", "").Replace("Pedal ", "P ");
                    }
                    Settle();
                    window.ShowView("Machines");
                    Shot(window, "510-machines-many", $"Machines with many machines: every Neo and Pedal machine ({gear.Count}) in one song.", "Add machines (right-click ▸ New).", song: "a song of every Neo and Pedal machine");
                });

                Try("343-machines-many-meters", () =>
                {
                    var canvas = Tab<MachineView>(window, "Machines").Canvas;
                    var master = Core.SongCore.Machines.First(m => m.DLL.Info.Type == MachineType.Master);
                    foreach (var m in Core.SongCore.Machines.ToList())
                        if (m != master) MachineEdits.Connect(Core, m, master);          // every machine into the Master, as a big project has them
                    Settle();
                    var wires = canvas.Wires.ToList();
                    int n = 0;
                    var level = new Dictionary<IMachineConnection, float>();
                    foreach (var c in wires) level[c] = 32768f * (float)System.Math.Pow(10, -((n++ * 7) % 13) * 4.0 / 20);     // 0 to -48 dB across the wires
                    canvas.PeakSource = c => level.TryGetValue(c, out float v) ? (v, v * 0.8f) : (0, 0);
                    canvas.TickMeters(1);
                    canvas.SetZoom(-2);                  // the whole song in the view (P SH101 was cut off by the foot of it)
                    Settle();
                    Shot(window, "343-machines-many-meters", $"The same song of every Neo and Pedal machine ({wires.Count} wires) with a level on every wire (P3g; the levels are set by the test: no sound is made here): the meters stay readable and the wires you can't see from far away show where the sound goes.", "Play a big song with the Machine View open.", song: "a song of every Neo and Pedal machine", notes: Headless);
                    canvas.SetZoom(0);
                    canvas.PeakSource = ReBuzz.Core.Session.SessionMixer.PeakOf;
                    canvas.TickMeters(10);
                });

                // Safety: a machine that runs away, the guard's state.
                if (Core.MachineDLLs.ContainsKey("Guard Test"))
                {
                    Try("520-safety-diverged", () =>
                    {
                        NewSong();
                        MachineGuard.Enabled = true;
                        var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
                        Core.SongCore.CreateMachine("Guard Test", null, "Runaway", null, null, null, -1, -0.3f, -0.3f);
                        var m = Core.SongCore.MachinesList.Last(x => x.Name == "Runaway");
                        Core.SongCore.ConnectMachines(m, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                        m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == "Mode").SetValue(0, 1);
                        for (int b = 0; b < 40; b++) lock (ReBuzzCore.AudioLock) { work ??= new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings); work.MainAudioFillBuffer(buffer, 0, 512); }
                        Settle();
                        window.RefreshNow();
                        var view = Tab<MachineView>(window, "Machines");
                        window.MouseMove(view.PointOf(m, window));
                        Settle();
                        Shot(window, "520-safety-diverged", "Safety: a machine whose signal ran away (the guard tripped): drawn red and muted; the problem bar says why in plain words, with Reset and Unmute and Parameters….", "Happens when a machine goes wild (NaN, infinity, over +60 dBFS).",
                            song: "a test song (one misbehaving machine)", notes: "Guard Test is the tests' own machine, built to diverge.");
                        Click(window, view.PointOf(m, window), MouseButton.Right);
                        menus.Add(view.Canvas.OpenMenu!);
                        Shot(window, "521-safety-diverged-menu", "Safety: the diverged machine's menu, Reset and Unmute first.", "Right-click the red machine.", song: "a test song (one misbehaving machine)");
                    });
                }
                else failures.Add("mac-520: the Guard Test machine isn't in the tests' Gear");
            }
            finally
            {
                NewSong();
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 560-564: track inputs and arm (neo-audio.md "Inputs and arm")

        [AvaloniaTest, Order(8)]
        public void TrackInputs()
        {
            BeforeAll();
            var demo = LoadDemo();
            var window = Open();
            var work = new WorkManager(Core, new WorkThreadEngine(1), 0, BuzzGUI.Common.Global.EngineSettings);
            var buffer = new float[512];
            long fed = 0;
            // The input device is simulated: four inputs with a driver's names, a voice on the first.
            void Feed(int frames)
            {
                for (int done = 0; done < frames; done += 256)
                {
                    var data = new float[256 * 4];
                    for (int i = 0; i < 256; i++)
                    {
                        double t = (fed + i) / 44100.0;
                        double swell = 0.35 + 0.25 * System.Math.Sin(t * 2.3);
                        data[i * 4] = (float)(swell * System.Math.Sin(2 * System.Math.PI * 196 * t));
                    }
                    fed += 256;
                    Core.EngineInput.Push(data, 256, 4);
                    work.MainAudioFillBuffer(buffer, 0, 512);
                }
                System.Threading.Thread.Sleep(25);
                foreach (var v in window.GetVisualDescendants().OfType<ReBuzz.Studio.Views.Session.TrackInputView>()) v.Poll();
            }
            LiveBeforeCapture = () => { ReBuzz.Studio.Views.Session.TrackInputView.PausePolling = false; Feed(2048); ReBuzz.Studio.Views.Session.TrackInputView.PausePolling = true; };
            try
            {
                Core.EngineInput.Configure(4, new[] { "Mic 1", "Mic 2", "Line 3", "Line 4" }, 44100, 128);
                var layout = Core.SessionLayout;
                layout.Ensure();
                var voice = (MachineCore)layout.NewAudioTrack("Voice");
                layout.SetAudioFrom(voice, AudioFrom.Mono(0));
                layout.SetMonitor(voice, 1);
                layout.SetMidiFrom(demo.Lead, MidiFromKind.Device, "USB Keyboard");
                layout.SetMachineArmed(voice, true);
                layout.SetMachineArmed(demo.Lead, true);
                Settle();
                Try("560-machine-arm-dot", () =>
                {
                    var view = Tab<MachineView>(window, "Machines");
                    window.ShowView("Machines");
                    Settle();
                    window.RefreshNow();
                    Shot(window, "560-machine-arm-dot", "Machine View: a dot at the foot of every machine that can be armed (Neo Audio and generators): red on Voice and Lead (armed), grey on the others. Click it to arm; right-click it for the Input menu.", "The dot on a machine's box.");
                    var dot = view.FromCanvas(MachineGeometry.ArmDot(view.Canvas.BoxOf(voice)).Center, window);
                    Click(window, dot, MouseButton.Right);
                    var menu = view.Canvas.OpenMenu;
                    Assert.That(menu?.IsOpen, Is.True, "a right click on the dot opens the Input menu");
                    var audio = menu.Items.OfType<MenuItem>().First(i => (string)i.Header == "Audio From");
                    audio.IsSubMenuOpen = true;
                    Settle();
                    Shot(window, "561-machine-input-menu", "The Input menu of Voice's dot: Arm, Audio From (None, the device's inputs by the driver's names, In 1/2 pairs, the other tracks, Wired inputs), MIDI From, Channel, Monitor.", "Right-click the dot.");
                    menu.Close();
                    Settle();
                });
                Try("562-session-io-row", () =>
                {
                    var session = Tab<SessionView>(window, "Session");
                    window.ShowView("Session");
                    layout.SetShowMixer(true);
                    layout.SetShowIO(true);
                    Settle();
                    window.RefreshNow();
                    session.RefreshState();
                    Feed(11025);
                    System.Threading.Thread.Sleep(80);
                    session.RefreshState();
                    Settle();
                    Feed(2048);
                    Shot(window, "562-session-io-row", "Session: the I/O row under the mixer strips (the toolbar's I/O button): Audio From, MIDI From, the arm dot and the MIDI channel, Monitor (In, Auto, Off) and the input meter, live while the track is armed or monitored (Voice listens to Mic 1 and its meter moves; Lead takes MIDI from one device).", "The toolbar's I/O button.");
                });
                Try("563-arrangement-header-inputs", () =>
                {
                    var seq = Tab<SequenceView>(window, "Arrangement");
                    window.ShowView("Sequence");
                    seq.SetArrangementMode(true);
                    Settle();
                    Feed(4096);
                    Shot(window, "563-arrangement-header-inputs", "Arrangement: an expanded track header of a machine that can be armed has its input chooser (Audio From for an audio track, MIDI From for a generator) and the input meter under the buttons; the R arms, as every other dot does.", "The track header.");
                    seq.SetArrangementMode(false);
                });
                Try("564-inspector-input", () =>
                {
                    window.ShowView("Machines");
                    window.Inspector.Open();
                    window.Inspector.ShowTab(ReBuzz.Studio.Views.Inspector.InspectorPanel.SelectionTab);
                    StudioSelection.For(Core).Set(voice, null, window);
                    Settle();
                    window.RefreshNow();
                    Feed(4096);
                    Shot(window, "564-inspector-input", "The Inspector's Input section for the selected machine (Voice): the arm dot, Audio From, MIDI From and channel, Monitor and the input meter.", "Select a track's machine; the Inspector's Selection tab.");
                });
                Try("570-voice-to-midi-editor", () =>
                {
                    Core.SongCore.CreateMachine("Neo Voice to MIDI", null, "Voice MIDI", null, null, null, -1, 0.5f, 0.8f);
                    var vm = Core.SongCore.MachinesList.First(m => m.Name == "Voice MIDI");
                    var inst = vm.ManagedMachine!;
                    inst.GetType().GetMethod("SetTarget")!.Invoke(inst, new object[] { demo.Lead.Name });
                    vm.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == "Chords").SetValue(0, 1);
                    // A sung phrase: A3, C4, E4 (the machine's Work, as the host calls it), so that the pitch line, the key and the chord show.
                    var work = inst.GetType().GetMethod("Work")!;
                    var input = new Buzz.MachineInterface.Sample[256];
                    var output = new Buzz.MachineInterface.Sample[256];
                    long frame = 0;
                    foreach (double hz in new[] { 220.0, 261.63, 329.63 })
                        for (int b = 0; b < 44100 * 0.5 / 256; b++)
                        {
                            for (int k = 0; k < 256; k++, frame++)
                            {
                                double t = frame / 44100.0, v = 0;
                                for (int h = 1; h <= 8; h++) v += System.Math.Sin(2 * System.Math.PI * hz * h * t) / h;
                                input[k].L = input[k].R = (float)(v * 0.25 * 32768);
                            }
                            work.Invoke(inst, new object[] { output, input, 256, Buzz.MachineInterface.WorkModes.WM_READWRITE });
                        }
                    Core.UiHost.OpenGui(vm, null);
                    var w = StudioMachineUiHost.GuiWindowOf(vm);
                    Assert.That(w, Is.Not.Null, "Neo Voice to MIDI's editor opened");
                    Wait(200);
                    Shot(w!, "570-voice-to-midi-editor", "Neo Voice to MIDI's editor after a sung phrase (A, C, E): the pitch line on the piano roll with the key's notes lit, the chord under it, the key and scale keyboard, the pitch knobs, the eight sound pads with the Train panel, the chords and the expression lanes, and where the MIDI goes.", "Open the machine's window.");
                    Core.UiHost.CloseWindows(vm);
                });
                Try("571-session-midi-from-machine", () =>
                {
                    layout.SetMidiFrom(demo.Lead, MidiFromKind.Machine, "Voice MIDI");
                    var session = Tab<SessionView>(window, "Session");
                    window.ShowView("Session");
                    layout.SetShowMixer(true);
                    layout.SetShowIO(true);
                    Settle();
                    window.RefreshNow();
                    session.RefreshState();
                    Settle();
                    Shot(window, "571-session-midi-from-machine", "Session: the I/O row with Lead's MIDI From set to a machine (Machine: Voice MIDI, Neo Voice to MIDI): an armed synth track records what the voice plays.", "Session > I/O row > MIDI From > Machine: Voice MIDI.");
                });
                Try("574-audio-to-midi-melody", () =>
                {
                    Core.SongCore.CreateMachine("Neo Audio to MIDI", null, "Audio MIDI", null, null, null, -1, 0.5f, 0.8f);
                    var am = Core.SongCore.MachinesList.First(m => m.Name == "Audio MIDI");
                    var inst = am.ManagedMachine!;
                    inst.GetType().GetMethod("SetTarget")!.Invoke(inst, new object[] { demo.Lead.Name });
                    // A plucked phrase (E2 G2 A2 B2 D3 A2 as decaying harmonic tones), through the machine's Work as the host calls it.
                    var work = inst.GetType().GetMethod("Work")!;
                    var input = new Buzz.MachineInterface.Sample[256];
                    var output = new Buzz.MachineInterface.Sample[256];
                    long frame = 0;
                    foreach (double hz in new[] { 82.41, 98.0, 110.0, 123.47, 146.83, 110.0 })
                        for (int b = 0; b < 44100 * 0.45 / 256; b++)
                        {
                            for (int k = 0; k < 256; k++, frame++)
                            {
                                double t = (b * 256 + k) / 44100.0, v = 0;
                                for (int h = 1; h <= 6; h++) v += System.Math.Sin(2 * System.Math.PI * hz * h * t) / h * System.Math.Exp(-t * (2.5 + h));
                                input[k].L = input[k].R = (float)(v * 0.5 * 32768);
                            }
                            work.Invoke(inst, new object[] { output, input, 256, Buzz.MachineInterface.WorkModes.WM_READWRITE });
                        }
                    Core.UiHost.OpenGui(am, null);
                    var w = StudioMachineUiHost.GuiWindowOf(am);
                    Assert.That(w, Is.Not.Null, "Neo Audio to MIDI's editor opened");
                    Wait(200);
                    Shot(w!, "574-audio-to-midi-melody", "Neo Audio to MIDI's editor in Melody mode after a plucked bass phrase (E, G, A, B, D, A): the input's pitch as a line on the piano roll and the notes sent as bars, the key and scale keyboard, the melody knobs, the drum pads and the MIDI-to buttons.", "Add Neo Audio to MIDI (an effect); open its editor.");
                    Core.UiHost.CloseWindows(am);
                });
                Try("575-audio-to-midi-drums", () =>
                {
                    var am = Core.SongCore.MachinesList.First(m => m.Name == "Audio MIDI");
                    var inst = am.ManagedMachine!;
                    am.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == "Mode").SetValue(0, 1);
                    var work = inst.GetType().GetMethod("Work")!;
                    var input = new Buzz.MachineInterface.Sample[256];
                    var output = new Buzz.MachineInterface.Sample[256];
                    var rng = new System.Random(5);
                    // Two bars of a beat at 120 bpm: kick, hat, snare, hat (a kick as a falling sine, a snare as noise over a shell tone, a hat as high noise).
                    int total = (int)(44100 * 2.0), hit = 0;
                    var pattern = new (double T, int Cls)[] { (0.0, 0), (0.25, 2), (0.5, 1), (0.75, 2), (1.0, 0), (1.25, 2), (1.5, 1), (1.75, 2), (1.875, 2) };
                    var signal = new float[total + 44100];
                    foreach (var (t0, cls) in pattern)
                    {
                        int at = (int)(t0 * 44100) + 4410;
                        for (int i = 0; i < 9000 && at + i < signal.Length; i++)
                        {
                            double t = i / 44100.0, v = 0;
                            if (cls == 0) v = System.Math.Sin(2 * System.Math.PI * (50 + 110 * System.Math.Exp(-t / 0.025)) * t) * System.Math.Exp(-t / 0.09) * 0.8;
                            else if (cls == 1) v = (System.Math.Sin(2 * System.Math.PI * 190 * t) * 0.6 + (rng.NextDouble() * 2 - 1) * 0.5) * System.Math.Exp(-t / 0.06);
                            else v = (rng.NextDouble() * 2 - 1) * System.Math.Exp(-t / 0.02) * 0.35;
                            signal[at + i] += (float)v;
                        }
                        hit++;
                    }
                    for (int pos = 0; pos + 256 <= signal.Length; pos += 256)
                    {
                        for (int k = 0; k < 256; k++) input[k].L = input[k].R = signal[pos + k] * 32768f * 0.8f;
                        work.Invoke(inst, new object[] { output, input, 256, Buzz.MachineInterface.WorkModes.WM_READWRITE });
                    }
                    Core.UiHost.OpenGui(am, null);
                    var w = StudioMachineUiHost.GuiWindowOf(am);
                    Assert.That(w, Is.Not.Null, "Neo Audio to MIDI's editor opened in Drums mode");
                    Wait(200);
                    Shot(w!, "575-audio-to-midi-drums", "Neo Audio to MIDI's editor in Drums mode after two bars of a beat (kick, hat, snare, hat): a lane for each drum with the hits it played, the kick, snare and hat pads with the Calibrate panel and each drum's note, velocity, sensitivity and interval.", "Set Mode to Drums; open the editor; Calibrate takes five hits of the chosen drum.");
                    Core.UiHost.CloseWindows(am);
                });
            }
            finally
            {
                LiveBeforeCapture = null;
                ReBuzz.Studio.Views.Session.TrackInputView.PausePolling = false;
                Core.EngineInput.Close();
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 350-352: clip envelopes (daw-modes.md EV1)

        [AvaloniaTest, Order(10)]
        public void ClipEnvelopes()
        {
            BeforeAll();
            var demo = LoadDemo();
            var window = Open();
            try
            {
                var lead = (MachineCore)Core.Song.Machines.First(m => m.Name == "Bass");
                var hook = lead.Patterns.First(p => p.Name == "Line A");
                var param = AutomationParams.List(lead).First(p => p.Name == "F1 cutoff");
                var env = ReBuzz.Core.ClipEnvelopes.Read(hook, param.Key);
                int span = param.Max - param.Min;
                int V(double f) => param.Min + (int)(span * f);
                var editor = new ReBuzz.Core.ClipEnvelopeEditor(Core);
                // A cutoff that opens over the first bar, closes in steps, and opens again.
                Assert.That(editor.Replace(env, new[]
                {
                    new AutoPoint(0, V(0.15), true), new AutoPoint(10 * 240, V(0.85), true), new AutoPoint(16 * 240, V(0.35), false),
                    new AutoPoint(22 * 240, V(0.6), false), new AutoPoint(28 * 240, V(0.2), true),
                }), Is.Null);
                Settle();

                Try("350-clip-envelope-lane", () =>
                {
                    window.Detail.ShowPage(true, true);
                    Settle();
                    Assert.That(ReBuzz.Studio.Views.Detail.EnvelopeOpener.Open(hook, param.Key), Is.True);
                    Wait(250);
                    var lane = PatternView.Page.Roll.Envelopes.LaneFor(param.Key);
                    Assert.That(lane, Is.Not.Null);
                    lane.SelectTimes(new[] { 16 * 240, 22 * 240 });
                    Settle();
                    // The pointer rests on the third point: its value tooltip.
                    window.MouseMove(lane.TranslatePoint(lane.PointOf(10 * 240), window)!.Value);
                    Wait(100);
                    Shot(window, "350-clip-envelope-lane", "The Detail panel's envelope lane (EV1) under the piano roll of Bass's Line A clip: the Envelopes header (the parameter chooser, + Envelope, Ramp and Step for the selection, the count), the F1 cutoff curve on the piano roll's own time axis (ramps are circles, steps squares), two points selected, and the value tooltip on the point under the pointer.",
                        "Double-click a curve on a clip; or Detail > Piano roll, then + Envelope.");
                });

                Try("351-arrangement-envelope-curves", () =>
                {
                    window.Detail.Close();
                    var seq = Tab<SequenceView>(window, "Arrangement");
                    window.ShowView("Sequence");
                    seq.SetArrangementMode(true);
                    seq.Arrangement.Envelopes = true;
                    Settle();
                    Shot(window, "351-arrangement-envelope-curves", "The Arrangement with the Envelopes toggle on (EV1, key E): each clip of a track draws the parameter its header chose (the ∿ button; here Bass's F1 cutoff, the first parameter with data), read only, the repeats of a looped clip fainter.",
                        "The Arrangement, then the Envelopes button in the right panel (E).");
                    seq.Arrangement.Envelopes = false;
                    seq.SetArrangementMode(false);
                });

                Try("352-session-envelope-curves", () =>
                {
                    window.ShowView("Session");
                    var session = Tab<SessionView>(window, "Session");
                    session.Rebuild();
                    Settle();
                    Shot(window, "352-session-envelope-curves", "The Session View (EV1): a slot whose clip holds an envelope draws it as a mini curve along its bottom; the ∿ chip in the track header chooses the parameter.",
                        "Draw an envelope in the Detail panel's lane, then look at the Session View.");
                });
            }
            finally
            {
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 590-593: audio takes and comping (neo-audio.md "Takes and comping")

        [AvaloniaTest, Order(9)]
        public void AudioTakes()
        {
            BeforeAll();
            var demo = LoadDemo();
            var window = Open();
            var work = new WorkManager(Core, new WorkThreadEngine(1), 0, BuzzGUI.Common.Global.EngineSettings);
            var buffer = new float[512];
            long fed = 0;
            string takes = Path.Combine(SongFolder, "takes");
            ReBuzz.Core.Clips.TakeFile.Directory = takes;
            Core.EngineInput.Configure(2, new[] { "In 1", "In 2" }, 44100, 128);
            void Feed(int frames)
            {
                for (int done = 0; done < frames; done += 256)
                {
                    var data = new float[256 * 2];
                    for (int i = 0; i < 256; i++)
                    {
                        double t = (fed + i) / 44100.0;
                        double swell = 0.45 + 0.35 * System.Math.Sin(t * 3.1);
                        data[i * 2] = data[i * 2 + 1] = (float)(swell * System.Math.Sin(2 * System.Math.PI * 220 * t));
                    }
                    fed += 256;
                    Core.EngineInput.Push(data, 256, 2);
                    work.MainAudioFillBuffer(buffer, 0, 512);
                }
                Core.SessionLayout.PollTakes();
                System.Threading.Thread.Sleep(30);
            }
            try
            {
                var layout = Core.SessionLayout;
                layout.Ensure();
                Core.OutputLatencySamples = 0;
                Core.BPM = 120;
                var voice = (MachineCore)layout.NewAudioTrack("Voice");
                layout.SetAudioFrom(voice, AudioFrom.Stereo(0));
                layout.SetMachineArmed(voice, true);
                Settle();
                Try("590-session-recording-slot", () =>
                {
                    var session = Tab<SessionView>(window, "Session");
                    window.ShowView("Session");
                    layout.SetRecordBars(2);
                    var take = layout.RecordTake(layout.FirstTrackOf(voice), 0);
                    Assert.That(take, Is.Not.Null);
                    Core.Playing = true;
                    for (int i = 0; i < 60; i++) Feed(2048);
                    Settle();
                    window.RefreshNow();
                    session.RefreshState();
                    // PL1: the slot shows the take's name (not "∿ 00"), and its waveform is in the lower half.
                    var recSlot = session.SlotAt(layout.FirstTrackOf(voice), 0);
                    Assert.That(recSlot.LabelText, Is.EqualTo("Voice take 1"), "the recording slot names the take");
                    Assert.That(recSlot.LiveWave?.LowerHalf, Is.True, "the growing waveform is in the lower half");
                    Shot(window, "590-session-recording-slot", "Session: an armed audio track records into its slot (the record button or the slot's record): the slot is red with the record dot and the waveform grows as the take is written to a temp file, so a long take never sits in memory. It becomes a normal, unwarped clip with the tempo it was played at.", "Arm the track, then the record button.");
                    for (int i = 0; i < 200 && layout.IsRecordingClip(take); i++) Feed(4096);
                    Core.Playing = false;
                    Feed(2048);
                });
                Try("591-arrangement-recording-takes", () =>
                {
                    var seq = Tab<SequenceView>(window, "Arrangement");
                    window.ShowView("Sequence");
                    seq.SetArrangementMode(true);
                    Core.SongCore.SongEnd = 64;
                    Core.SongCore.LoopStart = 0;
                    Core.SongCore.LoopEnd = 64;
                    Core.SongCore.PlayPosition = 0;
                    var rec = Core.Arrangement.Takes;
                    rec.SetArmed(voice, true);
                    Assert.That(rec.Start(true), Is.Null);
                    var entry = Core.SessionCore.ArrangementAudio.Single();
                    for (int guard = 0; entry.MarkCount < 1 && guard < 800; guard++) Feed(2048);
                    for (int i = 0; i < 90; i++) Feed(2048);
                    Settle();
                    window.RefreshNow();
                    Shot(window, "591-arrangement-recording-takes", "Arrangement: loop recording an armed audio track. The second pass is drawn growing in red over the loop; each finished pass is already a take.", "Arrange mode, arm the track, loop on, record.");
                    for (int guard = 0; entry.MarkCount < 3 && guard < 800; guard++) Feed(2048);
                    Assert.That(rec.Stop(), Is.Null);
                    Core.Playing = false;
                    Settle();
                    window.RefreshNow();
                    Shot(window, "592-arrangement-take-lanes", "Arrangement: the three takes under the track, the newest on the track itself (it plays). The track header's take button toggles the lanes, deletes the unused takes and flattens the comp.", "The take button in the track header.");
                });
                Try("593-arrangement-comp", () =>
                {
                    var editor = new ClipEditor(Core);
                    Assert.That(editor.CompRanges(voice, new[] { (1, 0, 32), (3, 32, 64) }), Is.Null);
                    Settle();
                    window.RefreshNow();
                    Shot(window, "593-arrangement-comp", "Arrangement: a comp of take 1 for the first half of the loop and take 3 for the second, joined by a short crossfade at the cut. Non-destructive: the takes stay on their lanes and Undo brings the old comp back.", "Shift-drag a range on a take lane, or the takes tool.");
                    Tab<SequenceView>(window, "Arrangement").SetArrangementMode(false);
                });
            }
            finally
            {
                Core.EngineInput.Close();
                Close(window);
            }
            AfterAll();
        }
    }
}
