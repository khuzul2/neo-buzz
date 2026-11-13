// Fork (docs/specs/studio-mvp.md S0, S7): the studio's main window. Menus (the macOS menu bar on a
// Mac, the window's own menu elsewhere: AppMenu), transport bar, one tab per view (StudioViews),
// status line (audio, CPU, dropouts, the agent). Until the engine runs it shows its start-up steps.
// Its size and place are kept in the settings.
// Fork (ux-overhaul.md P1): the menus and keys from one keymap; a problem bar under the transport says what's wrong in
// plain words with a button that fixes it; the status line starts with the audio's health.

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
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell.Commands;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Shell.Preferences;
using ReBuzz.Studio.Views;
using BuzzCommand = BuzzGUI.Interfaces.BuzzCommand;

namespace ReBuzz.Studio.Shell
{
    internal sealed class MainWindow : Window
    {
        const string PlacementName = "MainWindow";

        readonly StudioHost host;
        readonly TextBlock startup = new TextBlock { Foreground = StudioTokens.TextSecondaryBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly DockPanel root = new DockPanel();
        readonly TransportBar transport;
        readonly TextBlock status = new TextBlock { Margin = new Thickness(8, 3), FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center };
        readonly AudioHealthBadge health;
        readonly ProblemBar problems = new ProblemBar();
        readonly TabControl tabs = new TabControl();
        readonly Shell.ViewSwitcher switcher = new Shell.ViewSwitcher();
        readonly DockPanel viewHost = new DockPanel();
        readonly DockPanel body = new DockPanel();
        Views.Inspector.InspectorPanel inspector;
        Views.Browser.BrowserPanel browser;
        Views.Detail.DetailPanel detail;
        readonly Grid centre = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        readonly Command undo, redo;
        readonly Menu menu;
        int ticks;
        BuzzPerformanceData lastPerformance;
        double? cpu;

        public MainWindow(StudioHost host)
        {
            this.host = host;
            Title = Product.Name;
            Width = 1280;
            Height = 820;
            MinWidth = 640;
            MinHeight = 400;
            WindowBounds.Restore(this, host.Registry, PlacementName);
            WindowBounds.KeepOnScreen(this);

            // The selected view first (IStudioEditTarget), then the song's undo history.
            // A focused text box first (FocusedText), then the selected view, then the song's history.
            // Song Notes' editor keeps its edits on the song's history (machine-safety.md §3.5), so its
            // Undo and Redo are the song's.
            undo = new Command(() => { if (FocusedOwnUndo is { } t) t.Undo(); else if (Target?.Undo() != true) Stack?.Undo(); },
                               () => FocusedOwnUndo != null || Target != null || Stack?.CanUndo == true);
            redo = new Command(() => { if (FocusedOwnUndo is { } t) t.Redo(); else if (Target?.Redo() != true) Stack?.Redo(); },
                               () => FocusedOwnUndo != null || Target != null || Stack?.CanRedo == true);
            transport = new TransportBar(host);
            health = new AudioHealthBadge(() => host.Core, () => PreferencesWindow.Open(host, PreferencesWindow.AudioPageName));
            singleKeys = new (string, Action)[]
            {
                ("transport.record", () => transport.ToggleRecord()),
                ("transport.loop", () => transport.ToggleLoop()),
                ("transport.metronome", () => transport.ToggleMetronome()),
                ("transport.start", () => transport.GoToStart()),
            };

            // The macOS menu bar has the menus there; elsewhere they are the window's own.
            if (App.MacMenus)
                NativeMenu.SetMenu(this, AppMenu.ToNativeMenu(MenuModel(mac: true)));
            else
            {
                menu = AppMenu.ToMenu(MenuModel(mac: false));
                DockPanel.SetDock(menu, Dock.Top);
                root.Children.Add(menu);
            }
            DockPanel.SetDock(transport, Dock.Top);
            DockPanel.SetDock(problems, Dock.Top);
            var statusRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(health, Dock.Left);
            statusRow.Children.Add(health);
            statusRow.Children.Add(status);
            var statusBar = new Border { Child = statusRow, BorderBrush = StudioTokens.SurfaceBorderBrush, BorderThickness = new Thickness(0, 1, 0, 0) };
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(transport);
            root.Children.Add(problems);
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
            AddHandler(KeyDownEvent, OnSingleKey, RoutingStrategies.Bubble);
            AddHandler(KeyDownEvent, OnSessionKey, RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnNotesKey, RoutingStrategies.Tunnel);
            // Space: play and stop in every view, before a focused button takes it; not while typing.
            _ = new TransportKeys(this, () => tabs.ItemCount > 0 ? host.Core : null, () => detail?.IsOpen == true && InDetail(FocusManager?.GetFocusedElement()));

            Opened += (s, e) => open.Add(this);
            Closed += (s, e) => open.Remove(this);

            refresh.Tick += (s, e) => Refresh();
            if (host.Core != null) OnStarted();
            else host.Started += OnStarted;
        }

        static readonly List<MainWindow> open = new List<MainWindow>();

        /// <summary>The main windows open, oldest first (one in the app; tests open more).</summary>
        internal static IReadOnlyList<MainWindow> OpenWindows => open;

        BuzzGUI.Common.ManagedActionStack Stack => host.Core?.SongCore?.ActionStack;

        /// <summary>
        /// The selected view, when it has edit commands of its own and this is the key window: on a Mac every window has the
        /// menus (ui-gallery.md), and ⌘Z in a machine window undoes in the song, not in the Pattern View behind it.
        /// </summary>
        IStudioEditTarget Target
        {
            get
            {
                if (ActiveWindow != this) return null;
                // Song Notes in the inspector, when the keyboard is in it; else the selected view.
                if (inspector?.SongNotes is { } notes && inspector.IsOpen && FocusManager?.GetFocusedElement() is Visual focused && notes.IsVisualAncestorOf(focused))
                    return notes;
                // The Tracker in the detail panel, when the keyboard is in it.
                if (detail?.IsOpen == true && InDetail(FocusManager?.GetFocusedElement())) return detail.Page as IStudioEditTarget;
                return (tabs.SelectedItem as TabItem)?.Content as IStudioEditTarget;
            }
        }

        /// <summary>Whether this control is in the detail panel (the Tracker).</summary>
        bool InDetail(object element) => detail?.Page is Visual page && element is Visual v && (ReferenceEquals(page, v) || page.IsVisualAncestorOf(v));

        /// <summary>
        /// The text box with the keyboard focus, if any: the Edit commands act on it first, as a Mac app's
        /// act on the first responder (the menu bar's key equivalents may reach the menu before the box).
        /// </summary>
        TextBox FocusedText => FocusManager?.GetFocusedElement() as TextBox;

        /// <summary>The focused text box when it has an undo history of its own (not Song Notes' editor).</summary>
        TextBox FocusedOwnUndo => FocusedText is { } t && t is not Views.Notes.SongHistoryTextBox ? t : null;

        void Cut() { if (FocusedText is { } t) t.Cut(); else Target?.Cut(); }
        void Copy() { if (FocusedText is { } t) t.Copy(); else Target?.Copy(); }
        void Paste() { if (FocusedText is { } t) t.Paste(); else Target?.Paste(); }

        /// <summary>The views' tabs, once the engine runs.</summary>
        public TabControl Tabs => tabs;

        /// <summary>The inspector on the right (P3a), once the engine runs.</summary>
        internal Views.Inspector.InspectorPanel Inspector => inspector;
        internal Shell.ViewSwitcher Switcher => switcher;

        /// <summary>The browser on the left (P3a), once the engine runs.</summary>
        internal Views.Browser.BrowserPanel Browser => browser;

        /// <summary>The detail panel under the view (P3a): the Tracker.</summary>
        internal Views.Detail.DetailPanel Detail => detail;

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
            Views.Session.SessionView sessionView = null;
            Avalonia.Controls.Control tracker = null;
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
                    content = new TextBlock { Text = $"{view.Title}: {e.Message}", Foreground = StudioTokens.ErrorTextBrush, Margin = new Thickness(12) };
                }
                // Fork (ux-overhaul.md P3a): the Pattern editor is the detail panel's Tracker, not a tab.
                if (view.Title == "Pattern")
                {
                    tracker = content;
                    continue;
                }
                tabs.Items.Add(new TabItem { Header = view.Title, Content = content });
                switcher.Add(view.Title, ToolTipFor(view.Title), () => ShowView(view.Title));
                sessionView ??= content as Views.Session.SessionView;
            }
            ShowView(StudioViews.FirstShown);

            // Fork (ux-overhaul.md P3a): the inspector on the right in every view; Song Notes are its Song tab.
            Views.Notes.SongNotesView notes = null;
            try
            {
                notes = new Views.Notes.SongNotesView(host.Core);
            }
            catch (Exception e)
            {
                host.Log($"The Song Notes view failed: {e}");
            }
            inspector = new Views.Inspector.InspectorPanel(host.Core, host.Registry, ShowView, sessionView, notes);
            inspector.SessionShown = () => (tabs.SelectedItem as TabItem)?.Header as string == "Session";
            EmptyStates.AddMachine = AddMachine;          // the empty states' "Add machine…" (P3i)
            inspector.MachinesShown = () => (tabs.SelectedItem as TabItem)?.Header as string == "Machines";
            tabs.SelectionChanged += (s, e) => { if (e.Source == tabs) inspector?.Refresh(); };
            browser = new Views.Browser.BrowserPanel(host.Core, host.Registry, p => PatternView.Edit(p));
            detail = new Views.Detail.DetailPanel(host.Registry, tracker ?? new TextBlock { Text = "The Tracker failed to start.", Foreground = StudioTokens.ErrorTextBrush, Margin = new Thickness(12) },
                host.Core != null ? new Views.Device.DeviceDock(host.Core) : null);
            // Fork (ux-overhaul.md P3e): a double click on a machine with no editor of its own opens its device view here.
            Platform.StudioMachineUiHost.DeviceOpener = OpenDevice;
            if (tracker is PatternPage page)
            {
                page.Reveal = () => detail.Show(detail.IsMaximised);
                page.ShowSequence = () => ShowView("Arrangement");
            }
            // Fork (ux-overhaul.md decision 37): the segmented switcher is the view strip; the TabControl only hosts the
            // views (its own strip is hidden), so a narrow window no longer wraps it.
            tabs.TemplateApplied += (s, e) =>
            {
                if (e.NameScope.Find<Avalonia.Controls.Control>("PART_ItemsPresenter") is { } strip) strip.IsVisible = false;
            };
            tabs.SelectionChanged += (s, e) => switcher.Select(SelectedTitle);
            switcher.Select(SelectedTitle);
            DockPanel.SetDock(switcher, Dock.Top);
            viewHost.Children.Add(switcher);
            viewHost.Children.Add(tabs);
            Grid.SetRow(viewHost, 0);
            Grid.SetRow(detail, 1);
            centre.Children.Add(viewHost);
            centre.Children.Add(detail);
            body.Children.Add(browser);
            body.Children.Add(inspector);
            body.Children.Add(centre);
            root.Children.Add(body);
            detail.Changed += ApplyDetailLayout;
            browser.Changed += FitPanels;
            inspector.Changed += FitPanels;
            body.SizeChanged += (s, e) => FitPanels();
            ApplyDetailLayout();
            transport.IsEnabled = true;
            if (host.Agent != null) host.Agent.Changed += Refresh;
            refresh.Start();
            Refresh();
        }

        // ------------------------------------------------------------------ the panels

        /// <summary>The least width the main view keeps; the side panels give way to it in a narrow window.</summary>
        internal const double MinMainWidth = 360;

        /// <summary>The least height the main view keeps above the detail panel.</summary>
        internal const double MinMainHeight = 160;

        /// <summary>The detail panel docked under the view, or over all of it (maximised).</summary>
        void ApplyDetailLayout()
        {
            if (detail == null) return;
            bool max = detail.IsMaximised;
            centre.RowDefinitions[0].Height = max ? new GridLength(0) : GridLength.Star;
            centre.RowDefinitions[1].Height = max ? GridLength.Star : GridLength.Auto;
            viewHost.IsVisible = !max;
            if (!detail.IsOpen && host.Core != null) host.Core.EditContext = null;     // the tracker no longer has the keyboard
            FitPanels();
        }

        /// <summary>
        /// Keeps the main view usable: in a narrow window the inspector is narrower than it was set (not under its least width,
        /// then it is a rail), and the browser gets what is left (else a rail); the settings are kept. In a low window the
        /// detail panel is lower.
        /// </summary>
        void FitPanels()
        {
            if (browser == null || inspector == null) return;
            if (body.Bounds.Width > 0)
            {
                double room = body.Bounds.Width - MinMainWidth;
                inspector.Fit(inspector.IsOpen ? Math.Max(0, room - Views.Browser.BrowserPanel.RailWidth) : double.PositiveInfinity);
                browser.Fit(browser.IsOpen ? Math.Max(0, room - inspector.EffectiveWidth) : double.PositiveInfinity);
            }
            if (detail != null && body.Bounds.Height > 0)
                detail.MaxHeight = detail.IsMaximised ? double.PositiveInfinity : Math.Max(Views.Detail.DetailPanel.BarHeight, body.Bounds.Height - MinMainHeight);
        }

        // ------------------------------------------------------------------ menus and keys

        // Fork (ux-overhaul.md §4): the menus, the window's keys, the shortcuts window and the tooltips come from one keymap
        // (Commands/StudioKeymap): a key is written once, and ⌘D, ⌫, ⌘A, ⌘R mean the same in every view.

        /// <summary>Command on macOS, Control elsewhere.</summary>
        internal static KeyModifiers CommandModifiers => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        static KeyGesture Keys(Key key, KeyModifiers mods = KeyModifiers.None) => new KeyGesture(key, mods);

        static MenuEntry Item(string header, Action run, KeyGesture key = null, Func<bool> can = null, bool nativeKey = true) =>
            new MenuEntry { Header = header, Run = run, Gesture = key, CanRun = can, NativeKey = nativeKey };

        static MenuEntry Sub(string header, Func<IEnumerable<MenuEntry>> items) => new MenuEntry { Header = header, Items = items };

        /// <summary>
        /// A command of the keymap as a menu entry. On a Mac a key the menu bar mustn't take (Space, ⌫, the single-key
        /// transport keys) is written in the item's text instead: "Record (R)".
        /// </summary>
        static MenuEntry Command(string id, bool mac, Action run, Func<bool> can = null, Func<bool> isChecked = null)
        {
            var c = StudioKeymap.Get(id);
            var key = c.Gesture(mac);
            string header = c.Title;
            bool inText = mac && !c.Native && key != null;
            if (inText) header += " (" + KeyText.Of(key, true) + ")";
            return new MenuEntry { Header = header, Run = run, CanRun = can, IsChecked = isChecked, Gesture = key, NativeKey = c.Native, KeyInText = inText };
        }

        /// <summary>
        /// The macOS app menu, whole, as Mac apps have it: About, Preferences… (⌘,), Services, Hide NeoBuzz Live (⌘H),
        /// Hide Others (⌥⌘H), Show All, Quit NeoBuzz Live (⌘Q). Fork (ui-gallery.md): Avalonia's own standard items are
        /// left out (MacOSPlatformOptions.DisableDefaultApplicationMenuItems): their Quit has no name and their Hide
        /// Others is on ⌥⌘Q. Set on the application before Avalonia's exporter starts (App.Initialize), else Avalonia's
        /// "About Avalonia" menu stays.
        /// </summary>
        internal static IEnumerable<MenuEntry> AppMenuModel(StudioHost host) => new[]
        {
            Item("About " + Product.Name, () => AboutWindow.Open()),
            MenuEntry.Separator,
            // Always on: the menu is made before the engine runs, and Preferences opens only once it does.
            Item("Preferences…", () => PreferencesWindow.Open(host), Keys(Key.OemComma, KeyModifiers.Meta)),
            MenuEntry.Separator,
            new MenuEntry { Header = "Services", Items = () => Array.Empty<MenuEntry>(), IsServices = true },
            MenuEntry.Separator,
            Item("Hide " + Product.Name, MacApp.Hide, Keys(Key.H, KeyModifiers.Meta)),
            Item("Hide Others", MacApp.HideOthers, Keys(Key.H, KeyModifiers.Meta | KeyModifiers.Alt)),
            Item("Show All", MacApp.ShowAll),
            MenuEntry.Separator,
            Item("Quit " + Product.Name, Quit, Keys(Key.Q, KeyModifiers.Meta)),
        };

        internal IEnumerable<MenuEntry> AppMenuModel() => AppMenuModel(host);

        /// <summary>Quit: the windows close as they do when the app quits (a changed song asks first).</summary>
        static void Quit()
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.TryShutdown();
        }

        /// <summary>The window the keyboard is in (the key window on a Mac), else this one.</summary>
        Window ActiveWindow => App.Windows.LastOrDefault(w => w.IsActive && w.IsVisible) ?? this;

        /// <summary>
        /// The menus: File, Edit, Transport, View, Window, Help. On a Mac (<paramref name="mac"/>)
        /// About, Preferences and Quit are in the app menu instead.
        /// </summary>
        internal IEnumerable<MenuEntry> MenuModel(bool mac)
        {
            bool running() => host.Core != null;
            yield return Sub("_File", () => FileItems(mac, running));
            yield return Sub("_Edit", () => EditItems(mac));
            yield return Sub("_Transport", () => TransportItems(mac, running));
            yield return Sub("_View", () => ViewItems(mac));
            yield return Sub("_Window", () => WindowItems(mac, running));
            yield return Sub("_Help", () => HelpItems(mac));
        }

        IEnumerable<MenuEntry> FileItems(bool mac, Func<bool> running)
        {
            yield return Command("file.new", mac, () => _ = SongFiles.New(), running);
            yield return Command("file.open", mac, () => _ = SongFiles.Open(), running);
            yield return Sub("Open _Recent", () => RecentFiles.Menu(host.Registry));
            yield return MenuEntry.Separator;
            var close = Command("file.close", mac, CloseFromMenu);
            if (ActiveWindow == this) close.Header = "_Close Song";
            yield return close;
            yield return Command("file.save", mac, () => _ = SongFiles.Save(), running);
            yield return Command("file.saveas", mac, () => _ = SongFiles.SaveAs(), running);
            yield return Command("file.revert", mac, () => _ = RevertToSaved(), () => running() && host.Core.SongCore.SongName != null && host.Core.Modified);
            if (!mac)
            {
                yield return MenuEntry.Separator;
                yield return Command("file.exit", mac, Close);
            }
        }

        IEnumerable<MenuEntry> EditItems(bool mac)
        {
            yield return Command("edit.undo", mac, () => undo.Execute(null), () => undo.CanExecute(null));
            yield return Command("edit.redo", mac, () => redo.Execute(null), () => redo.CanExecute(null));
            yield return MenuEntry.Separator;
            yield return Command("edit.cut", mac, Cut);
            yield return Command("edit.copy", mac, Copy);
            yield return Command("edit.paste", mac, Paste);
            yield return Command("edit.duplicate", mac, Duplicate);
            yield return Command("edit.delete", mac, Delete);
            yield return MenuEntry.Separator;
            yield return Command("edit.selectall", mac, SelectAll);
            yield return Command("edit.deselect", mac, DeselectAll);
            yield return MenuEntry.Separator;
            yield return Command("edit.rename", mac, Rename);
            if (!mac)
            {
                yield return MenuEntry.Separator;
                yield return Command("edit.preferences", mac, ShowPreferences, () => host.Core != null);
            }
        }

        IEnumerable<MenuEntry> TransportItems(bool mac, Func<bool> running)
        {
            // Space is the window's (TransportKeys), not a menu bar key equivalent, which would take it from text boxes; the
            // single-key transport keys likewise (OnSingleKey): the Mac's menu bar shows them in the text.
            yield return Command("transport.playstop", mac, () => TransportCommands.TogglePlaying(host.Core), running);
            yield return Command("transport.play", mac, () => transport.Play(), running);
            yield return Command("transport.stop", mac, () => transport.Stop(), running);
            yield return Command("transport.record", mac, () => transport.ToggleRecord(), running, () => transport.Recording);
            yield return MenuEntry.Separator;
            yield return Command("transport.loop", mac, () => transport.ToggleLoop(), running, () => host.Core?.Looping == true);
            yield return Command("transport.metronome", mac, () => transport.ToggleMetronome(), running, () => transport.MetronomeOn);
            yield return Command("transport.start", mac, () => transport.GoToStart(), running);
            yield return MenuEntry.Separator;
            yield return Command("transport.restartaudio", mac, RestartAudio, running);
        }

        /// <summary>The keymap's command for a view's tab.</summary>
        static string ViewCommand(string title) => title switch
        {
            "Arrangement" => "view.sequence",
            "Session" => "view.session",
            "Machines" => "view.machines",
            "Pattern" => "view.pattern",
            _ => null,
        };

        /// <summary>
        /// The views' tabs, ⌘1–⌘5 in tab order (ux-overhaul.md §4) as the menu bar's key equivalents; ReBuzz.exe's keys
        /// (F2 Pattern, F3 Machines, F4 Sequence, Ctrl+Tab Session, F10 Song Notes) stay the window's, so F2 still renames a
        /// clip in the Session View and Ctrl+Tab still goes back from it.
        /// </summary>
        IEnumerable<MenuEntry> ViewItems(bool mac)
        {
            foreach (var view in StudioViews.All)
            {
                string title = view.Title;
                string id = ViewCommand(title);
                bool tracker = title == "Pattern";
                var entry = Command(id, mac, () => UnlessEditorKeeps(id, () => ShowView(title)), () => tabs.ItemCount > 0,
                    () => tracker ? detail?.IsOpen == true : SelectedTitle == title);
                yield return entry;
            }
            yield return Command("view.notes", mac, () => UnlessEditorKeeps("view.notes", ShowSongNotes), () => inspector != null,
                () => inspector is { IsOpen: true, CurrentTab: Views.Inspector.InspectorPanel.SongTab });
            yield return MenuEntry.Separator;
            yield return Command("view.browser", mac, () => browser?.Toggle(), () => browser != null, () => browser?.IsOpen == true);
            yield return Command("view.addmachine", mac, AddMachine, () => host.Core != null);
            yield return Command("view.inspector", mac, () => inspector?.Toggle(), () => inspector != null, () => inspector?.IsOpen == true);
            yield return Command("view.detail", mac, () => detail?.Toggle(), () => detail != null, () => detail?.IsOpen == true);
            yield return Command("view.pianoroll", mac, ShowPianoRoll, () => detail != null, () => detail?.IsOpen == true && detail.IsPianoRoll);
            yield return Command("view.maximise", mac, () => ToggleTrackerMaximised(), () => detail != null, () => detail?.IsMaximised == true);
            yield return Command("view.device", mac, ShowDevice, () => detail?.DevicePage != null, () => detail?.IsOpen == true && detail.IsDevice);
            yield return Command("device.find", mac, FindParameter, () => detail?.DevicePage != null);
            yield return Command("view.mixer", mac, ToggleSessionMixer, () => host.Core?.SessionLayout != null, () => host.Core?.SessionLayout?.Document?.ShowMixer == true);
        }

        IEnumerable<MenuEntry> WindowItems(bool mac, Func<bool> running)
        {
            // The key window's, as on a Mac: Preferences minimizes itself.
            yield return Command("window.minimize", mac, () => ActiveWindow.WindowState = WindowState.Minimized);
            yield return Command("window.zoom", mac, () => { var w = ActiveWindow; w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; });
            if (mac) yield return Command("window.front", mac, BringAllToFront);
            // The open windows (machine editors, the piano roll, Preferences…), so a window behind another is found again.
            var open = App.Windows.Where(w => w.IsVisible && !string.IsNullOrEmpty(w.Title)).ToList();
            if (open.Count > 0)
            {
                yield return MenuEntry.Separator;
                foreach (var w in open)
                {
                    var window = w;
                    yield return new MenuEntry { Header = window.Title.Replace("_", "__"), Run = () => window.Activate(), IsChecked = () => window.IsActive };
                }
            }
            yield return MenuEntry.Separator;
            yield return Command("window.agent", mac, ShowAgent, running);
        }

        IEnumerable<MenuEntry> HelpItems(bool mac)
        {
            yield return Command("help.shortcuts", mac, ShowShortcuts);
            if (!mac)
            {
                yield return MenuEntry.Separator;
                yield return Command("help.about", mac, () => AboutWindow.Open());
            }
        }

        /// <summary>The keyboard shortcuts as text, one per line (the keymap's, as the shortcuts window lists them).</summary>
        internal static string ShortcutsText(bool mac) =>
            string.Join("\n", ShortcutsWindow.AllRows(mac).Select(r => $"{r.Keys}\t{r.What} ({r.Section})"));

        void ShowShortcuts() => ShortcutsWindow.Open(this);

        void BringAllToFront()
        {
            if (OperatingSystem.IsMacOS()) MacApp.BringAllToFront();
            else foreach (var w in App.Windows.Where(w => w.IsVisible).ToList()) w.Activate();
        }

        /// <summary>The Session View's mixer, shown or hidden (⌥⌘M), from any view.</summary>
        void ToggleSessionMixer()
        {
            var layout = host.Core?.SessionLayout;
            if (layout == null) return;
            layout.SetShowMixer(!layout.Ensure().ShowMixer);
            ShowView("Session");
        }

        // ---- Edit: the focused text box first, then the selected view (IStudioEditTarget)

        bool forwardingKey;

        /// <summary>
        /// A command of the selected view. On a Mac the menu bar takes a key equivalent before the window: when the view
        /// doesn't take the command, the key goes to the focused control as if typed, so the tracker's own ⌘R (randomize)
        /// and ⌘W (rotate) still work. True when something took it.
        /// </summary>
        bool FromMenu(string id, Func<bool> command)
        {
            if (forwardingKey) return false;
            if (command()) return true;
            var key = StudioKeymap.Get(id).Gesture(true);
            if (key == null || MacApp.KeyThatChoseTheMenuItem() != key.Key) return false;
            forwardingKey = true;
            try
            {
                return SendKey(key);
            }
            finally
            {
                forwardingKey = false;
            }
        }

        /// <summary>
        /// Whether the tracker, focused, has this key itself (its ⌘R randomizes, ⌘W rotates, ⌘1–⌘9 set the beat's
        /// subdivision, ⌘I interpolates, ⌘T writes the machine's state; PatternEditorView.KeyRows): there the window leaves
        /// it to the editor, and the views are on their F-keys.
        /// </summary>
        internal bool EditorKeepsKey(KeyGesture key)
        {
            if (key == null || !key.KeyModifiers.HasFlag(CommandModifiers) || key.KeyModifiers.HasFlag(KeyModifiers.Alt) || detail?.IsOpen != true) return false;
            if (!(key.Key is Key.R or Key.W or Key.I or Key.T || (key.Key >= Key.D0 && key.Key <= Key.D9))) return false;
            return InDetail(FocusManager?.GetFocusedElement());
        }

        /// <summary>A command the tracker may have under the same key: chosen by that key while it has the focus, the key is its.</summary>
        void UnlessEditorKeeps(string id, Action run)
        {
            var key = StudioKeymap.Get(id).Gesture(OperatingSystem.IsMacOS());
            if (key != null && MacApp.KeyThatChoseTheMenuItem() == key.Key && EditorKeepsKey(key) && !forwardingKey)
            {
                forwardingKey = true;
                try
                {
                    SendKey(key);
                }
                finally
                {
                    forwardingKey = false;
                }
                return;
            }
            run();
        }

        void Duplicate() => FromMenu("edit.duplicate", () => Target?.Duplicate() == true);

        void Delete()
        {
            if (FocusedText is { IsReadOnly: false } t)
            {
                if (!string.IsNullOrEmpty(t.SelectedText)) t.SelectedText = "";
                return;
            }
            Target?.Delete();
        }

        void SelectAll()
        {
            if (FocusedText is { } t) t.SelectAll();
            else FromMenu("edit.selectall", () => Target?.SelectAll() == true);
        }

        void DeselectAll()
        {
            if (FocusedText is { } t) t.ClearSelection();
            else FromMenu("edit.deselect", () => Target?.DeselectAll() == true);
        }

        void Rename() => UnlessEditorKeeps("edit.rename", () => FromMenu("edit.rename", () => Target?.Rename() == true));

        /// <summary>Close (⌘W): the window in front; on the main window, the song (asking to save first), unless the view takes ⌘W.</summary>
        void CloseFromMenu() => UnlessEditorKeeps("file.close", CloseFrontOrSong);

        void CloseFrontOrSong()
        {
            var front = ActiveWindow;
            if (front != this)
            {
                front.Close();
                return;
            }
            if (host.Core != null) _ = SongFiles.New();
        }

        /// <summary>Revert to Saved: the saved song again, after asking (the changes since are lost).</summary>
        async System.Threading.Tasks.Task RevertToSaved()
        {
            var core = host.Core;
            string path = core?.SongCore.SongName;
            if (path == null || !core.Modified) return;
            var answer = await Dialogs.Ask($"Revert to the saved “{Path.GetFileNameWithoutExtension(path)}”? The changes made since it was saved are lost.",
                                           Product.Name, ReBuzz.Core.UserButtons.YesNo, DialogChoices.Revert);
            if (answer != ReBuzz.Core.UserAnswer.Yes) return;
            core.Modified = false;
            core.TryOpenSongFile(path);
        }

        /// <summary>A key, as the focused control (else the key window) gets it when typed. True when something took it.</summary>
        bool SendKey(KeyGesture key)
        {
            var window = ActiveWindow;
            var target = window.FocusManager?.GetFocusedElement() as Interactive ?? window;
            var e = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key.Key, KeyModifiers = key.KeyModifiers };
            target.RaiseEvent(e);
            return e.Handled;
        }

        void AddKeys()
        {
            // Every key of the keymap that has a modifier or is an F-key, as the window's: the in-window menu only shows its
            // keys, and the Mac's menu bar is skipped by headless tests. The views' own bindings and handlers see a key first.
            // A key the focused tracker has itself (⌘R, ⌘W, ⌘1–⌘9) is left to it: a binding that can't run lets the key on.
            void Bind(string id, Action run, Func<KeyGesture, bool> which = null)
            {
                foreach (var key in StudioKeymap.Get(id).Gestures())
                    if (which == null || which(key))
                        KeyBindings.Add(new KeyBinding { Gesture = key, Command = new Command(run, () => !EditorKeepsKey(key)) });
            }
            static bool Modified(KeyGesture k) => k.KeyModifiers != KeyModifiers.None;
            Bind("file.new", () => _ = SongFiles.New());
            Bind("file.open", () => _ = SongFiles.Open());
            Bind("file.close", CloseFromMenu);
            Bind("file.save", () => _ = SongFiles.Save());
            Bind("file.saveas", () => _ = SongFiles.SaveAs());
            foreach (var key in StudioKeymap.Get("edit.undo").Gestures()) KeyBindings.Add(new KeyBinding { Gesture = key, Command = undo });
            foreach (var key in StudioKeymap.Get("edit.redo").Gestures()) KeyBindings.Add(new KeyBinding { Gesture = key, Command = redo });
            // The in-window menu only shows its gestures: these make them work. They act on a focused
            // text box first, then on the selected view (§3.10).
            Bind("edit.cut", Cut);
            Bind("edit.copy", Copy);
            Bind("edit.paste", Paste);
            Bind("edit.duplicate", Duplicate);
            Bind("edit.selectall", SelectAll);
            Bind("edit.deselect", DeselectAll, Modified);
            Bind("edit.rename", Rename);
            Bind("transport.play", () => transport.Play());
            Bind("transport.stop", () => transport.Stop());
            Bind("transport.record", () => transport.ToggleRecord(), Modified2);
            Bind("transport.restartaudio", RestartAudio);
            foreach (var view in StudioViews.All)
            {
                string title = view.Title;
                Bind(ViewCommand(title), () => ShowView(title), k => k.KeyModifiers == CommandModifiers && k.Key >= Key.D1 && k.Key <= Key.D9);
            }
            Bind("view.notes", () => UnlessEditorKeeps("view.notes", ShowSongNotes), k => k.KeyModifiers == CommandModifiers && k.Key == Key.D5);
            Bind("view.pianoroll", ShowPianoRoll);
            Bind("view.maximise", () => ToggleTrackerMaximised(), k => k.Key == Key.F);
            Bind("view.browser", () => browser?.Toggle());
            Bind("view.addmachine", AddMachine);
            Bind("view.inspector", () => inspector?.Toggle());
            Bind("view.detail", () => detail?.Toggle());
            Bind("view.device", ShowDevice);
            Bind("device.find", FindParameter);
            Bind("view.mixer", ToggleSessionMixer);
            Bind("help.shortcuts", ShowShortcuts);
            KeyBindings.Add(new KeyBinding { Gesture = Keys(Key.OemComma, CommandModifiers), Command = new Command(ShowPreferences) });
            if (!OperatingSystem.IsMacOS()) Bind("file.exit", Close);
        }

        /// <summary>An F-key (F7: record), which the window binds; the single keys are OnSingleKey's.</summary>
        static bool Modified2(KeyGesture k) => k.KeyModifiers != KeyModifiers.None || (k.Key >= Key.F1 && k.Key <= Key.F24);

        /// <summary>
        /// The single-key transport keys (R record, C loop, K metronome, Home go to start; ux-overhaul.md §4), after the views
        /// (a bubble handler), in the views that don't type letters, never while typing in a box.
        /// </summary>
        void OnSingleKey(object sender, KeyEventArgs e)
        {
            if (e.Handled || tabs.ItemCount == 0 || e.KeyModifiers != KeyModifiers.None || !SingleKeysWork || TransportKeys.KeepsSpace(e.Source)) return;
            foreach (var (id, run) in singleKeys)
            {
                if (StudioKeymap.Get(id).Gestures().Any(g => g.Key == e.Key && g.KeyModifiers == KeyModifiers.None))
                {
                    run();
                    e.Handled = true;
                    return;
                }
            }
        }

        (string Id, Action Run)[] singleKeys;

        /// <summary>Whether the selected view leaves single letters to the transport: not the arrangement (pattern keys) or the tracker (notes).</summary>
        bool SingleKeysWork => SelectedTitle is "Machines" or "Session" && !InDetail(FocusManager?.GetFocusedElement());

        void OnViewKey(object sender, KeyEventArgs e)
        {
            if (e.Handled || tabs.ItemCount == 0 || e.KeyModifiers != KeyModifiers.None) return;
            if (e.Key == Key.F2)
            {
                // F2: the Tracker over the whole window, and back.
                ToggleTrackerMaximised();
                e.Handled = true;
                return;
            }
            string title = e.Key switch { Key.F3 => "Machines", Key.F4 => "Arrangement", _ => null };
            if (title == null) return;
            ShowView(title);
            e.Handled = true;
        }

        /// <summary>Ctrl+Tab: the Session View, and back to the arrangement, as Live's Session/Arrangement switch.</summary>
        void OnSessionKey(object sender, KeyEventArgs e)
        {
            if (tabs.ItemCount == 0 || e.KeyModifiers != KeyModifiers.Control || e.Key != Key.Tab) return;
            ShowView(SelectedTitle == "Session" && detail?.IsMaximised != true ? "Arrangement" : "Session");
            e.Handled = true;
        }

        /// <summary>
        /// F10: Song Notes, as Buzz's Info view (machine-safety.md §3.5). Before the window's menu,
        /// which takes F10 to open itself elsewhere.
        /// </summary>
        void OnNotesKey(object sender, KeyEventArgs e)
        {
            if (tabs.ItemCount == 0 || e.KeyModifiers != KeyModifiers.None || e.Key != Key.F10) return;
            ShowSongNotes();
            e.Handled = true;
        }

        /// <summary>Song Notes: the inspector's Song tab, open, with the editor focused.</summary>
        void ShowSongNotes() => inspector?.ShowTab(Views.Inspector.InspectorPanel.SongTab, focus: true);

        /// <summary>A view segment's tooltip: its name and its key.</summary>
        static string ToolTipFor(string title) => ViewCommand(title) is { } id ? $"{title} ({Commands.StudioKeymap.Get(id).KeyLabel()})" : title;

        string SelectedTitle => (tabs.SelectedItem as TabItem)?.Header as string;

        /// <summary>Selects a view's tab.</summary>
        public void ShowView(string title)
        {
            title = ViewNames.Canonical(title);
            if (title == Views.Notes.SongNotesTab.TabTitle)
            {
                ShowSongNotes();
                return;
            }
            if (title == "Pattern")
            {
                // The Tracker in the detail panel, docked (maximised stays so), with the keyboard in the editor.
                if (detail == null) return;
                detail.ShowPage(false, detail.IsMaximised);
                PatternView.Show();
                return;
            }
            var tab = tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == title);
            if (tab != null)
            {
                detail?.Restore();      // the views are behind the maximised Tracker
                tabs.SelectedItem = tab;
            }
        }

        /// <summary>⌥⌘4: the detail panel's piano roll, docked (maximised stays so), with the keyboard in it.</summary>
        /// <summary>The detail panel on its Device page, following the selected machine.</summary>
        internal void ShowDevice()
        {
            if (detail?.DevicePage is not Views.Device.DeviceDock dock) return;
            detail.ShowDevice(detail.IsMaximised);
            dock.Follow();
        }

        /// <summary>⌘F: the Device page's search box.</summary>
        internal void FindParameter()
        {
            ShowDevice();
            (detail?.DevicePage as Views.Device.DeviceDock)?.Page?.Parameters.FocusSearch();
        }

        /// <summary>A double click on a machine with no editor: selects it and shows its device (true when the panel took it).</summary>
        bool OpenDevice(IMachine machine)
        {
            if (detail?.DevicePage is not Views.Device.DeviceDock || host.Core == null) return false;
            StudioSelection.For(host.Core).SelectMachine(machine, this);
            ShowDevice();
            return true;
        }

        internal void ShowPianoRoll()
        {
            if (detail == null) return;
            detail.ShowPage(true, detail.IsMaximised);
            PatternView.Show();
        }

        /// <summary>F2 and ⇧⌘F: the Tracker over the whole window, and back to the panel.</summary>
        void ToggleTrackerMaximised()
        {
            if (detail == null) return;
            detail.ToggleMaximised();
            if (detail.IsMaximised) PatternView.Show();
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
            inspector?.Refresh();
            browser?.Refresh();
            undo.Refresh();
            redo.Refresh();
            string name = core.SongCore.SongName == null ? "Untitled" : Path.GetFileName(core.SongCore.SongName);
            Title = $"{name}{(core.Modified ? " •" : "")} — {Product.Name}";
            var device = core.AudioEngine?.SelectedOutDevice;
            if (ticks++ % 10 == 0) MeasureCpu(core);
            // Without an output the audio-health badge before it says so (it was said twice, ux-overhaul.md P1).
            string audio = device != null
                ? $"{device.Name}, {core.SelectedAudioDriverSampleRate} Hz, {core.AudioEngine.CurrentDriver?.BufferSize} frames" + (cpu is double load ? $" · CPU {load:0}%" : "") + " · "
                : "";
            string agent = host.Agent?.Status;
            status.Text = $"{audio}dropouts {ReBuzzCore.DeadlineMisses} · {MachineCountText(core)}" + SafetyText(core) + (string.IsNullOrEmpty(agent) ? "" : " · " + agent);
            health.Refresh();
            if (ticks % 5 == 1) RefreshProblems();
        }

        /// <summary>The problem bar now (the window's timer does it twice a second).</summary>
        internal void RefreshProblems()
        {
            var core = host.Core;
            if (core == null) return;
            problems.Show(ProblemBar.Find(core, ShowMachines, m => Views.Machines.MachineEdits.OpenParameters(m),
                m => Views.Machines.MachineEdits.Mute(core, new[] { m }, false),
                on => { core.SafeMaster.LimiterOn = on; core.SafeMaster.Save(host.Registry); }));
        }

        /// <summary>Add Machine… (decision 58): the Machine View, with the machine browser open on it, as a double click on empty canvas does.</summary>
        internal void AddMachine()
        {
            ShowView("Machines");
            tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<Views.Machines.MachineView>().FirstOrDefault()?.OpenBrowser();
        }

        /// <summary>The Machine View, with these machines selected.</summary>
        void ShowMachines(IReadOnlyList<MachineCore> machines)
        {
            ShowView("Machines");
            var view = tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<Views.Machines.MachineView>().FirstOrDefault();
            view?.Canvas.Select(machines);
        }

        /// <summary>The problem bar (tests).</summary>
        internal ProblemBar Problems => problems;

        /// <summary>The audio-health badge (tests).</summary>
        internal AudioHealthBadge Health => health;

        /// <summary>
        /// The song's machines as the user sees them in the Machine View: not the Master, not the
        /// hidden pattern-editor machines (the demo song said "15 machines" for 10, gallery mac-020),
        /// in the singular for one ("1 machines", mac-010).
        /// </summary>
        internal static string MachineCountText(ReBuzzCore core)
        {
            int count = core.Song.Machines.Count(m => m.DLL?.Info?.Type != MachineType.Master);
            return count == 0 ? "no machines" : count == 1 ? "1 machine" : $"{count} machines";
        }

        /// <summary>
        /// Fork (machine-safety.md §3.2): machines whose guard tripped ("Diverged: muted: Bass (NaN)"),
        /// and the guard when it's off; empty when all is well.
        /// </summary>
        internal static string SafetyText(ReBuzzCore core)
        {
            // Fork (ux-overhaul.md P1): plain words ("diverged" was the guard's jargon); the problem bar has the whole story.
            var diverged = core.SongCore.MachinesList.Where(m => m.IsDiverged).Select(m => $"{m.Name} ({m.DivergedReason})").ToList();
            string text = diverged.Count > 0 ? " · Muted to protect your ears: " + string.Join(", ", diverged) : "";
            // Fork: machines that stopped working (a host that hung or exited, gallery win-704), named.
            var stopped = core.SongCore.MachinesList.Where(core.IsReportedCrashed).Select(m => m.Name).ToList();
            if (stopped.Count > 0) text += " · Stopped working: " + string.Join(", ", stopped);
            if (!ReBuzz.Audio.MachineGuard.Enabled) text += " · machine guard off";
            return text;
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

        /// <summary>The status line and the problem bar now, as the window's timer would (tests).</summary>
        internal void RefreshNow()
        {
            Refresh();
            RefreshProblems();
        }

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
            inspector?.Dispose();
            browser?.Dispose();
            if (Platform.StudioMachineUiHost.DeviceOpener == (Func<IMachine, bool>)OpenDevice) Platform.StudioMachineUiHost.DeviceOpener = null;
            if (host.Agent != null) host.Agent.Changed -= Refresh;
            base.OnClosed(e);
        }
    }
}
