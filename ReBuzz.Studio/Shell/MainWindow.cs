// Fork (docs/specs/studio-mvp.md S0, S7): the studio's main window. Menus (the macOS menu bar on a
// Mac, the window's own menu elsewhere: AppMenu), transport bar, one tab per view (StudioViews),
// status line (audio, CPU, dropouts, the agent). Until the engine runs it shows its start-up steps.
// Its size and place are kept in the settings.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell.Preferences;
using ReBuzz.Studio.Views;
using BuzzCommand = BuzzGUI.Interfaces.BuzzCommand;

namespace ReBuzz.Studio.Shell
{
    internal sealed class MainWindow : Window
    {
        const string PlacementName = "MainWindow";

        readonly StudioHost host;
        readonly TextBlock startup = new TextBlock { Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly DockPanel root = new DockPanel();
        readonly TransportBar transport;
        readonly TextBlock status = new TextBlock { Margin = new Thickness(8, 3), FontSize = 12, Foreground = Brushes.Gray };
        readonly TabControl tabs = new TabControl();
        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        readonly Command undo, redo;
        readonly Menu menu;
        int ticks;
        BuzzPerformanceData lastPerformance;
        double? cpu;

        public MainWindow(StudioHost host)
        {
            this.host = host;
            Title = "ReBuzz";
            Width = 1280;
            Height = 820;
            MinWidth = 640;
            MinHeight = 400;
            WindowBounds.Restore(this, host.Registry, PlacementName);

            // The selected view first (IStudioEditTarget), then the song's undo history.
            // A focused text box first (FocusedText), then the selected view, then the song's history.
            undo = new Command(() => { if (FocusedText is { } t) t.Undo(); else if (Target?.Undo() != true) Stack?.Undo(); },
                               () => FocusedText != null || Target != null || Stack?.CanUndo == true);
            redo = new Command(() => { if (FocusedText is { } t) t.Redo(); else if (Target?.Redo() != true) Stack?.Redo(); },
                               () => FocusedText != null || Target != null || Stack?.CanRedo == true);
            transport = new TransportBar(host);

            // The macOS menu bar has the menus there; elsewhere they are the window's own.
            if (OperatingSystem.IsMacOS())
                NativeMenu.SetMenu(this, AppMenu.ToNativeMenu(MenuModel(mac: true)));
            else
            {
                menu = AppMenu.ToMenu(MenuModel(mac: false));
                DockPanel.SetDock(menu, Dock.Top);
                root.Children.Add(menu);
            }
            DockPanel.SetDock(transport, Dock.Top);
            var statusBar = new Border { Child = status, BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), BorderThickness = new Thickness(0, 1, 0, 0) };
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(transport);
            root.Children.Add(statusBar);
            root.Children.Add(startup);
            Content = root;
            transport.IsEnabled = false;

            AddKeys();
            AddHandler(KeyDownEvent, (s, e) => StudioKeyboard.Held = e.KeyModifiers, handledEventsToo: true);
            AddHandler(KeyUpEvent, (s, e) => StudioKeyboard.Held = e.KeyModifiers, handledEventsToo: true);
            AddHandler(PointerMovedEvent, (s, e) => StudioKeyboard.Held = e.KeyModifiers, handledEventsToo: true);
            // View keys after the views: F2 renames in the Session View, and picks the Pattern tab elsewhere.
            // Ctrl+Tab before focus navigation takes it (as ReBuzz.exe's PreviewKeyDown).
            AddHandler(KeyDownEvent, OnViewKey, RoutingStrategies.Bubble);
            AddHandler(KeyDownEvent, OnSessionKey, RoutingStrategies.Tunnel);

            refresh.Tick += (s, e) => Refresh();
            if (host.Core != null) OnStarted();
            else host.Started += OnStarted;
        }

        BuzzGUI.Common.ManagedActionStack Stack => host.Core?.SongCore?.ActionStack;

        /// <summary>The selected view, when it has edit commands of its own.</summary>
        IStudioEditTarget Target => (tabs.SelectedItem as TabItem)?.Content as IStudioEditTarget;

        /// <summary>
        /// The text box with the keyboard focus, if any: the Edit commands act on it first, as a Mac app's
        /// act on the first responder (the menu bar's key equivalents may reach the menu before the box).
        /// </summary>
        TextBox FocusedText => FocusManager?.GetFocusedElement() as TextBox;

        void Cut() { if (FocusedText is { } t) t.Cut(); else Target?.Cut(); }
        void Copy() { if (FocusedText is { } t) t.Copy(); else Target?.Copy(); }
        void Paste() { if (FocusedText is { } t) t.Paste(); else Target?.Paste(); }

        /// <summary>The views' tabs, once the engine runs.</summary>
        public TabControl Tabs => tabs;

        public TransportBar Transport => transport;

        /// <summary>The window's own menu (null on a Mac, where the menu bar has it).</summary>
        public Menu WindowMenu => menu;

        /// <summary>The status line's text now (for tests).</summary>
        public string StatusText => status.Text;

        /// <summary>A start-up step, shown until the views replace it.</summary>
        public void ShowStep(string step) => startup.Text = step;

        void OnStarted()
        {
            root.Children.Remove(startup);
            foreach (var view in StudioViews.All)
            {
                Avalonia.Controls.Control content;
                try
                {
                    content = view.Create(host);
                }
                catch (Exception e)
                {
                    host.Log($"The {view.Title} view failed: {e}");
                    content = new TextBlock { Text = $"{view.Title}: {e.Message}", Foreground = Brushes.IndianRed, Margin = new Thickness(12) };
                }
                tabs.Items.Add(new TabItem { Header = view.Title, Content = content });
            }
            tabs.SelectedIndex = 0;
            root.Children.Add(tabs);
            transport.IsEnabled = true;
            if (host.Agent != null) host.Agent.Changed += Refresh;
            refresh.Start();
            Refresh();
        }

        // ------------------------------------------------------------------ menus and keys

        /// <summary>Command on macOS, Control elsewhere.</summary>
        internal static KeyModifiers CommandModifiers => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        static KeyGesture Keys(Key key, KeyModifiers mods = KeyModifiers.None) => new KeyGesture(key, mods);

        static MenuEntry Item(string header, Action run, KeyGesture key = null, Func<bool> can = null, bool nativeKey = true) =>
            new MenuEntry { Header = header, Run = run, Gesture = key, CanRun = can, NativeKey = nativeKey };

        static MenuEntry Sub(string header, Func<IEnumerable<MenuEntry>> items) => new MenuEntry { Header = header, Items = items };

        /// <summary>
        /// The macOS app menu's own items: About and Preferences. Avalonia adds Services, Hide,
        /// Hide Others, Show All and Quit (Command+Q) after them.
        /// </summary>
        internal IEnumerable<MenuEntry> AppMenuModel() => new[]
        {
            Item("About ReBuzz", () => AboutWindow.Open()),
            MenuEntry.Separator,
            Item("Preferences…", ShowPreferences, Keys(Key.OemComma, KeyModifiers.Meta), () => host.Core != null),
        };

        /// <summary>
        /// The menus: File, Edit, Transport, View, Window, Help. On a Mac (<paramref name="mac"/>)
        /// About, Preferences and Quit are in the app menu instead.
        /// </summary>
        internal IEnumerable<MenuEntry> MenuModel(bool mac)
        {
            var cmd = mac ? KeyModifiers.Meta : KeyModifiers.Control;
            bool running() => host.Core != null;
            yield return Sub("_File", () => FileItems(mac, cmd, running));
            yield return Sub("_Edit", () => EditItems(mac, cmd));
            yield return Sub("_Transport", () => new[]
            {
                Item("_Play", () => transport.Play(), Keys(Key.F5), running),
                Item("_Stop", () => transport.Stop(), Keys(Key.F8), running),
                Item("_Record", () => transport.ToggleRecord(), Keys(Key.F7), running),
                MenuEntry.Separator,
                Item("Restart _audio", RestartAudio, Keys(Key.F12), running),
            });
            yield return Sub("_View", () => ViewItems(cmd));
            yield return Sub("_Window", () => new[]
            {
                Item("_Minimize", () => WindowState = WindowState.Minimized),
                Item("_Zoom", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
                MenuEntry.Separator,
                Item("_Agent connection…", ShowAgent, Keys(Key.A, cmd | KeyModifiers.Shift), running),
            });
            yield return Sub("_Help", () => HelpItems(mac));
        }

        IEnumerable<MenuEntry> FileItems(bool mac, KeyModifiers cmd, Func<bool> running)
        {
            yield return Item("_New", () => _ = SongFiles.New(), Keys(Key.N, cmd), running);
            yield return Item("_Open…", () => _ = SongFiles.Open(), Keys(Key.O, cmd), running);
            yield return Sub("Open _Recent", () => RecentFiles.Menu(host.Registry));
            yield return MenuEntry.Separator;
            yield return Item("_Save", () => _ = SongFiles.Save(), Keys(Key.S, cmd), running);
            yield return Item("Save _As…", () => _ = SongFiles.SaveAs(), Keys(Key.S, cmd | KeyModifiers.Shift), running);
            if (!mac)
            {
                yield return MenuEntry.Separator;
                yield return Item("E_xit", Close, Keys(Key.Q, cmd));
            }
        }

        IEnumerable<MenuEntry> EditItems(bool mac, KeyModifiers cmd)
        {
            yield return new MenuEntry { Header = "_Undo", Run = () => undo.Execute(null), CanRun = () => undo.CanExecute(null), Gesture = Keys(Key.Z, cmd) };
            yield return new MenuEntry { Header = "_Redo", Run = () => redo.Execute(null), CanRun = () => redo.CanExecute(null), Gesture = mac ? Keys(Key.Z, cmd | KeyModifiers.Shift) : Keys(Key.Y, cmd) };
            yield return MenuEntry.Separator;
            yield return Item("Cu_t", Cut, Keys(Key.X, cmd));
            yield return Item("_Copy", Copy, Keys(Key.C, cmd));
            yield return Item("_Paste", Paste, Keys(Key.V, cmd));
            if (!mac)
            {
                yield return MenuEntry.Separator;
                yield return Item("Pr_eferences…", ShowPreferences, Keys(Key.OemComma, cmd), () => host.Core != null);
            }
        }

        /// <summary>The views' tabs, with ReBuzz.exe's keys (F2 Pattern, F3 Machines, F4 Sequence, Ctrl+Tab Session).</summary>
        IEnumerable<MenuEntry> ViewItems(KeyModifiers cmd)
        {
            foreach (var view in StudioViews.All)
            {
                string title = view.Title;
                yield return new MenuEntry
                {
                    Header = "_" + title,
                    Run = () => ShowView(title),
                    CanRun = () => tabs.ItemCount > 0,
                    IsChecked = () => (tabs.SelectedItem as TabItem)?.Header as string == title,
                    Gesture = ViewKey(title),
                    NativeKey = false,          // the window's handler has them, after the views (F2 renames in the Session View)
                };
            }
        }

        static KeyGesture ViewKey(string title) => title switch
        {
            "Pattern" => new KeyGesture(Key.F2),
            "Machines" => new KeyGesture(Key.F3),
            "Sequence" => new KeyGesture(Key.F4),
            "Session" => new KeyGesture(Key.Tab, KeyModifiers.Control),
            _ => null,
        };

        IEnumerable<MenuEntry> HelpItems(bool mac)
        {
            yield return Item("_Keyboard shortcuts…", () => _ = Dialogs.Info(ShortcutsText(mac), "Keyboard shortcuts"));
            if (!mac)
            {
                yield return MenuEntry.Separator;
                yield return Item("_About ReBuzz", () => AboutWindow.Open());
            }
        }

        internal static string ShortcutsText(bool mac)
        {
            string c = mac ? "⌘" : "Ctrl+";
            string s = mac ? "⇧" : "Shift+";
            return $"{c}N / {c}O / {c}S / {s}{c}S\tnew, open, save, save as\n" +
                   $"{c}Z / {(mac ? $"{s}{c}Z" : $"{c}Y")}\tundo / redo (the selected view's first)\n" +
                   $"{c}X / {c}C / {c}V\tcut, copy, paste in the selected view\n" +
                   "F5 / F8 / F7\tplay / stop (twice: silence) / record\n" +
                   "F12\trestart audio\n" +
                   "F2 / F3 / F4\tPattern / Machines / Sequence\n" +
                   "Ctrl+Tab\tSession View (again: back to Sequence)\n" +
                   $"{c},\tPreferences\n" +
                   $"{s}{c}A\tagent connection (Preferences ▸ Agent)";
        }

        void AddKeys()
        {
            var cmd = CommandModifiers;
            void Bind(KeyGesture key, Action run) => KeyBindings.Add(new KeyBinding { Gesture = key, Command = new Command(run) });
            Bind(Keys(Key.N, cmd), () => _ = SongFiles.New());
            Bind(Keys(Key.O, cmd), () => _ = SongFiles.Open());
            Bind(Keys(Key.S, cmd), () => _ = SongFiles.Save());
            Bind(Keys(Key.S, cmd | KeyModifiers.Shift), () => _ = SongFiles.SaveAs());
            KeyBindings.Add(new KeyBinding { Gesture = Keys(Key.Z, cmd), Command = undo });
            KeyBindings.Add(new KeyBinding { Gesture = OperatingSystem.IsMacOS() ? Keys(Key.Z, cmd | KeyModifiers.Shift) : Keys(Key.Y, cmd), Command = redo });
            // The in-window menu only shows its gestures: these make them work. They act on a focused
            // text box first, then on the selected view (§3.10).
            Bind(Keys(Key.X, cmd), Cut);
            Bind(Keys(Key.C, cmd), Copy);
            Bind(Keys(Key.V, cmd), Paste);
            Bind(Keys(Key.F5), () => transport.Play());
            Bind(Keys(Key.F8), () => transport.Stop());
            Bind(Keys(Key.F7), () => transport.ToggleRecord());
            Bind(Keys(Key.F12), RestartAudio);
            Bind(Keys(Key.OemComma, cmd), ShowPreferences);
            Bind(Keys(Key.A, cmd | KeyModifiers.Shift), ShowAgent);
            if (!OperatingSystem.IsMacOS()) Bind(Keys(Key.Q, cmd), Close);
        }

        void OnViewKey(object sender, KeyEventArgs e)
        {
            if (e.Handled || tabs.ItemCount == 0 || e.KeyModifiers != KeyModifiers.None) return;
            string title = e.Key switch { Key.F2 => "Pattern", Key.F3 => "Machines", Key.F4 => "Sequence", _ => null };
            if (title == null) return;
            ShowView(title);
            e.Handled = true;
        }

        /// <summary>Ctrl+Tab: the Session View, and back to the arrangement, as Live's Session/Arrangement switch.</summary>
        void OnSessionKey(object sender, KeyEventArgs e)
        {
            if (tabs.ItemCount == 0 || e.KeyModifiers != KeyModifiers.Control || e.Key != Key.Tab) return;
            ShowView(SelectedTitle == "Session" ? "Sequence" : "Session");
            e.Handled = true;
        }

        string SelectedTitle => (tabs.SelectedItem as TabItem)?.Header as string;

        /// <summary>Selects a view's tab.</summary>
        public void ShowView(string title)
        {
            var tab = tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == title);
            if (tab != null) tabs.SelectedItem = tab;
        }

        void ShowPreferences() => PreferencesWindow.Open(host);

        void ShowAgent() => PreferencesWindow.Open(host, PreferencesWindow.AgentPageName);

        /// <summary>F12, as ReBuzz.exe's Reset Audio: the device opens again.</summary>
        void RestartAudio()
        {
            var core = host.Core;
            if (core?.AudioEngine?.SelectedOutDevice != null) AudioPage.Reopen(core, core.SelectedAudioDriver);
        }

        // ------------------------------------------------------------------ status

        void Refresh()
        {
            var core = host.Core;
            if (core == null) return;
            transport.Refresh();
            undo.Refresh();
            redo.Refresh();
            string name = core.SongCore.SongName == null ? "Untitled" : Path.GetFileName(core.SongCore.SongName);
            Title = $"{name}{(core.Modified ? " •" : "")} — ReBuzz";
            var device = core.AudioEngine?.SelectedOutDevice;
            if (ticks++ % 10 == 0) MeasureCpu(core);
            string audio = device != null
                ? $"{device.Name}, {core.SelectedAudioDriverSampleRate} Hz, {core.AudioEngine.CurrentDriver?.BufferSize} frames" + (cpu is double load ? $" · CPU {load:0}%" : "")
                : "no audio output";
            string agent = host.Agent?.Status;
            status.Text = $"{audio} · dropouts {ReBuzzCore.DeadlineMisses} · {core.SongCore.MachinesList.Count} machines" + (string.IsNullOrEmpty(agent) ? "" : " · " + agent);
        }

        /// <summary>
        /// The engine's load once a second: the audio engine's time over the wall clock's since the
        /// last snapshot, as ReBuzz.exe's CPU monitor reckons its total.
        /// </summary>
        void MeasureCpu(ReBuzzCore core)
        {
            if (core.AudioEngine?.SelectedOutDevice == null)
            {
                cpu = null;
                lastPerformance = null;
                return;
            }
            core.TakePerformanceDataSnapshot();
            var now = core.PerformanceData;
            if (lastPerformance != null && now.PerformanceCount > lastPerformance.PerformanceCount)
                cpu = Math.Clamp(100.0 * (now.EnginePerformanceCount - lastPerformance.EnginePerformanceCount) / (now.PerformanceCount - lastPerformance.PerformanceCount), 0, 999);
            lastPerformance = now;
        }

        /// <summary>The status line now, as the window's timer would (tests).</summary>
        internal void RefreshNow() => Refresh();

        /// <summary>The engine's load in percent (null without an audio device), for tests.</summary>
        internal double? CpuLoad => cpu;

        // ------------------------------------------------------------------ closing

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel) return;
            if (host.Core == null || !host.Core.Modified || quitting)
            {
                // Closing for real: its size and place while it still has them.
                WindowBounds.Save(this, host.Registry, PlacementName);
                return;
            }
            // Unsaved changes: ask first, then close for real.
            e.Cancel = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (await SongFiles.ConfirmDiscard())
                {
                    quitting = true;
                    Close();
                }
            });
        }

        bool quitting;

        protected override void OnClosed(EventArgs e)
        {
            refresh.Stop();
            if (host.Agent != null) host.Agent.Changed -= Refresh;
            base.OnClosed(e);
        }
    }
}
