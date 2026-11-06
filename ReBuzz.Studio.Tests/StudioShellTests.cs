// Fork (docs/specs/studio-mvp.md S7): the rest of the shell, headless: recent files in File ▸ Open
// Recent, an autosave offered and restored at start-up, the menus (the macOS menu bar's layout
// built here too), the shortcuts, the window's size and place, About, and the Session View's Sync
// and Controllers menus.

using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Common;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Preferences;

namespace ReBuzz.Studio.Tests
{
    public class StudioShellTests
    {
        static StudioHost Host => Studio.Host;
        static ReBuzzCore Core => Host.Core;

        /// <summary>Command on macOS (the Mac CI job runs these too), Control elsewhere.</summary>
        static RawInputModifiers Cmd => System.OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        static string SongFolder
        {
            get
            {
                string dir = Path.Combine(Path.GetTempPath(), "rebuzz-studio-shell-" + System.Environment.ProcessId);
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        static void Settle(Window window = null)
        {
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        static MainWindow Open()
        {
            var window = new MainWindow(Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle(window);
            return window;
        }

        static void NewSong()
        {
            Core.Modified = false;
            SongFiles.New().Wait();
            Settle();
            Core.Modified = false;
        }

        // Not a [TearDown]: each test runs it when it ends, failed or not (Studio.AfterTest).
        public void Reset()
        {
            SongFiles.Picker = null;
            Dialogs.Responder = null;
            PreferencesWindow.Current?.Close();
            Core.Modified = false;
            Settle();
        }

        // ------------------------------------------------------------------ recent files

        [AvaloniaTest]
        public void SavedAndOpenedSongsAppearInOpenRecent()
        {
            using var restore = Studio.AfterTest(Reset);
            RecentFiles.Clear(Host.Registry);
            var window = Open();
            try
            {
                string a = Path.Combine(SongFolder, "first song.bmxml"), b = Path.Combine(SongFolder, "second_song.bmxml");
                NewSong();
                SongFiles.Picker = save => a;
                Assert.That(SongFiles.SaveAs().Result, Is.True);
                NewSong();
                SongFiles.Picker = save => b;
                Assert.That(SongFiles.SaveAs().Result, Is.True);
                Assert.That(RecentFiles.List(Host.Registry), Is.EqualTo(new[] { b, a }), "newest first");

                // The model both menus are made from, then the window's own menu (on a Mac: the menu bar).
                var recent = AppMenu.Find(window.MenuModel(mac: false), "File/Open Recent").Items().ToList();
                Assert.That(recent[0].Text, Is.EqualTo($"second_song.bmxml — {SongFolder}"), "an underscore is shown as one");
                Assert.That(recent[1].Text, Is.EqualTo($"first song.bmxml — {SongFolder}"));
                Assert.That(recent.Last().Text, Is.EqualTo("Clear Menu"));
                if (window.WindowMenu != null)
                {
                    var file = window.WindowMenu.Items.OfType<MenuItem>().First(i => (string)i.Tag == "File");
                    file.IsSubMenuOpen = true;          // built as it opens
                    Settle(window);
                    var openRecent = file.Items.OfType<MenuItem>().Single(i => (string)i.Tag == "Open Recent");
                    Assert.That(openRecent.Items.OfType<MenuItem>().Select(i => (string)i.Tag).Take(2), Is.EqualTo(new[] { recent[0].Text, recent[1].Text }));
                    Studio.Screenshot(window, "studio-file-menu");
                    file.IsSubMenuOpen = false;
                }

                // Picked: it opens, and goes to the top.
                recent[1].Run();
                Settle(window);
                Assert.That(Core.SongCore.SongName, Is.EqualTo(a));
                Assert.That(RecentFiles.List(Host.Registry), Is.EqualTo(new[] { a, b }));

                var clear = AppMenu.Find(window.MenuModel(mac: false), "File/Open Recent/Clear Menu");
                clear.Run();
                var empty = AppMenu.Find(window.MenuModel(mac: false), "File/Open Recent").Items().ToList();
                Assert.That(empty[0].Text, Is.EqualTo("No recent songs"));
                Assert.That(empty[0].CanRun(), Is.False);
            }
            finally
            {
                NewSong();
                window.Close();
            }
        }

        // ------------------------------------------------------------------ autosave

        [AvaloniaTest]
        public void AnAutosaveLeftOverIsOfferedAndRestored()
        {
            using var restore = Studio.AfterTest(Reset);
            var window = Open();
            try
            {
                string song = Path.Combine(SongFolder, "crashed song.bmxml");
                NewSong();
                SongFiles.Picker = save => song;
                Core.BPM = 121;
                Assert.That(SongFiles.SaveAs().Result, Is.True);

                // Unsaved changes, autosaved, then ReBuzz "crashes": the autosave stays behind.
                Core.SongCore.CreateMachine("Neo Poly", null, "After Crash", null, null, null, -1, 0.3f, 0.1f);
                Core.BPM = 133;
                Core.Modified = true;
                Assert.That(Core.Autosave.SaveNow(System.DateTime.Now), Is.True);
                string autosave = Core.Autosave.Current;
                byte[] data = File.ReadAllBytes(autosave);
                string owner = File.ReadAllText(autosave + ".txt");
                NewSong();                                          // (a new song discards its autosave)
                File.WriteAllBytes(autosave, data);
                File.WriteAllText(autosave + ".txt", owner);
                Assert.That(SongAutosave.FindLeftovers().Count, Is.EqualTo(1));

                // Cancel keeps it for next time.
                Dialogs.Responder = (m, c, b) => UserAnswer.Cancel;
                Assert.That(Autosave.OfferRecovery(Core).Result, Is.False);
                Assert.That(SongAutosave.FindLeftovers().Count, Is.EqualTo(1));

                // Yes opens it as the song it belongs to, with unsaved changes.
                string asked = null;
                Dialogs.Responder = (m, c, b) =>
                {
                    Assert.That(c, Is.EqualTo("Recover unsaved changes"), "the only question");
                    Assert.That(b, Is.EqualTo(UserButtons.YesNoCancel));
                    asked = m;
                    return UserAnswer.Yes;
                };
                Assert.That(Autosave.OfferRecovery(Core).Result, Is.True);
                Settle(window);
                Assert.That(asked, Does.StartWith("NeoBuzz Live didn't close normally").And.Contain("'crashed song.bmxml'"));
                Assert.That(Core.SongCore.SongName, Is.EqualTo(song));
                Assert.That(Core.SongCore.MachinesList.Select(m => m.Name), Does.Contain("After Crash"));
                Assert.That(Core.BPM, Is.EqualTo(133));
                Assert.That(Core.Modified, Is.True, "save to keep them");
                Assert.That(SongAutosave.FindLeftovers(), Is.Empty, "the autosave is used up");
                window.RefreshNow();
                Studio.Screenshot(window, "studio-autosave-recovered");

                // Discard… (No) deletes it once confirmed.
                Assert.That(Core.Autosave.SaveNow(System.DateTime.Now), Is.True);
                data = File.ReadAllBytes(Core.Autosave.Current);
                autosave = Core.Autosave.Current;
                NewSong();
                File.WriteAllBytes(autosave, data);
                File.WriteAllText(autosave + ".txt", song);
                var questions = new System.Collections.Generic.List<string>();
                Dialogs.Responder = (m, c, b) => { questions.Add(c); return UserAnswer.No; };
                Assert.That(Autosave.OfferRecovery(Core).Result, Is.False);
                Assert.That(questions, Is.EqualTo(new[] { "Recover unsaved changes", "Discard unsaved changes" }), "asked again before it deletes");
                Assert.That(SongAutosave.FindLeftovers().Count, Is.EqualTo(1), "Keep keeps it");
                questions.Clear();
                Dialogs.Responder = (m, c, b) => { questions.Add(c); return questions.Count == 1 ? UserAnswer.No : UserAnswer.Yes; };
                Assert.That(Autosave.OfferRecovery(Core).Result, Is.False);
                Assert.That(SongAutosave.FindLeftovers(), Is.Empty, "Discard deletes it");
            }
            finally
            {
                foreach (var leftover in SongAutosave.FindLeftovers()) SongAutosave.Delete(leftover.Path);
                NewSong();
                window.Close();
            }
        }

        // ------------------------------------------------------------------ menus and keys

        /// <summary>
        /// Fork (rename): the app is NeoBuzz Live: the window's title, the application's name (the macOS app menu's
        /// About, Hide and Quit items and, unbundled, the menu bar take it), the About items, and the questions' caption.
        /// </summary>
        [AvaloniaTest]
        public void TheWindowAndTheMenusSayNeoBuzzLive()
        {
            using var restore = Studio.AfterTest(Reset);
            Assert.That(Application.Current!.Name, Is.EqualTo("NeoBuzz Live"), "the macOS app menu: About, Hide and Quit NeoBuzz Live");
            var window = Open();
            try
            {
                NewSong();
                window.RefreshNow();
                Assert.That(window.Title, Is.EqualTo("Untitled — NeoBuzz Live"));
                Core.Modified = true;
                window.RefreshNow();
                Assert.That(window.Title, Is.EqualTo("Untitled • — NeoBuzz Live"), "modified");

                var appMenu = AppMenu.ToNativeMenu(window.AppMenuModel());
                Assert.That(appMenu.Items.OfType<NativeMenuItem>().First().Header, Is.EqualTo("About NeoBuzz Live"), "the macOS app menu");
                Assert.That(AppMenu.Find(window.MenuModel(mac: false), "Help/About NeoBuzz Live"), Is.Not.Null, "Help elsewhere");
                Assert.That(window.MenuModel(mac: false).SelectMany(e => e.IsSeparator ? Enumerable.Empty<MenuEntry>() : e.Items?.Invoke() ?? Enumerable.Empty<MenuEntry>())
                                .Select(e => e.Text).Where(t => t != null && t.Contains("ReBuzz")), Is.Empty, "no menu item says ReBuzz");
                Assert.That(MainWindow.ShortcutsText(mac: true) + MainWindow.ShortcutsText(mac: false), Does.Not.Contain("ReBuzz"), "the shortcut list");

                // Closing a changed song asks, under the app's name.
                string caption = null;
                Dialogs.Responder = (m, c, b) => { caption = c; return UserAnswer.Cancel; };
                Assert.That(SongFiles.ConfirmDiscard().Result, Is.False);
                Assert.That(caption, Is.EqualTo("NeoBuzz Live"));
            }
            finally
            {
                Dialogs.Responder = null;
                NewSong();
                window.Close();
            }
        }

        [AvaloniaTest]
        public void TheMacMenuBarHasTheAppMenuAndTheStudiosMenus()
        {
            using var restore = Studio.AfterTest(Reset);
            var window = Open();
            try
            {
                // Fork (ui-gallery.md): the app menu whole, as Mac apps have it (Avalonia's standard items left out: their
                // Quit had no name and Hide Others was on ⌥⌘Q).
                var app = window.AppMenuModel().Where(e => !e.IsSeparator).ToList();
                Assert.That(app.Select(e => e.Text), Is.EqualTo(new[] { "About NeoBuzz Live", "Preferences…", "Services", "Hide NeoBuzz Live", "Hide Others", "Show All", "Quit NeoBuzz Live" }));
                Assert.That(app.Select(e => e.Gesture), Is.EqualTo(new[] { null, new KeyGesture(Key.OemComma, KeyModifiers.Meta), null,
                    new KeyGesture(Key.H, KeyModifiers.Meta), new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt), null, new KeyGesture(Key.Q, KeyModifiers.Meta) }),
                    "⌘, ⌘H ⌥⌘H ⌘Q");
                Assert.That(app.Single(e => e.Text == "Services").IsServices, Is.True, "the system's Services submenu");
                var early = AppMenu.ToNativeMenu(MainWindow.AppMenuModel(null)).Items.OfType<NativeMenuItem>().Single(i => i.Header == "Preferences…");
                Assert.That(early.IsEnabled, Is.True, "Preferences… on though the menu is made before the engine runs (it was greyed out on the Mac)");
                Assert.That(window.AppMenuModel().Select(e => e.IsSeparator), Is.EqualTo(new[] { false, true, false, true, false, true, false, false, false, true, false }), "grouped as macOS groups them");

                var mac = window.MenuModel(mac: true).ToList();
                Assert.That(mac.Select(e => e.Text), Is.EqualTo(new[] { "File", "Edit", "Transport", "View", "Window", "Help" }));
                Assert.That(AppMenu.Find(mac, "File/Exit"), Is.Null, "Quit is in the app menu");
                Assert.That(AppMenu.Find(mac, "Edit/Preferences…"), Is.Null);
                Assert.That(AppMenu.Find(mac, "Edit/Redo").Gesture, Is.EqualTo(new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift)));
                Assert.That(AppMenu.Find(mac, "File/Save As…").Gesture, Is.EqualTo(new KeyGesture(Key.S, KeyModifiers.Meta | KeyModifiers.Shift)));
                Assert.That(AppMenu.Find(mac, "View/Session"), Is.Not.Null);
                Assert.That(AppMenu.Find(mac, "Window/Agent connection…").Gesture, Is.EqualTo(new KeyGesture(Key.A, KeyModifiers.Meta | KeyModifiers.Shift)));

                // As the menu bar gets them (NativeMenu): headers without access keys, key equivalents, submenus.
                var bar = AppMenu.ToNativeMenu(mac);
                Assert.That(bar.Items.OfType<NativeMenuItem>().Select(i => i.Header), Is.EqualTo(new[] { "File", "Edit", "Transport", "View", "Window", "Help" }));
                var fileMenu = bar.Items.OfType<NativeMenuItem>().First().Menu;
                Assert.That(fileMenu.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).Select(i => i.Header), Is.EqualTo(new[] { "New", "Open…", "Open Recent", "Save", "Save As…" }));
                Assert.That(fileMenu.Items[3], Is.InstanceOf<NativeMenuItemSeparator>());
                Assert.That(fileMenu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Open Recent").Menu, Is.Not.Null);
                var viewMenu = bar.Items.OfType<NativeMenuItem>().Single(i => i.Header == "View").Menu;
                Assert.That(viewMenu.Items.OfType<NativeMenuItem>().Select(i => i.Header), Is.EqualTo(new[] { "Machines", "Sequence", "Session", "Pattern", "Song Notes" }));
                Assert.That(viewMenu.Items.OfType<NativeMenuItem>().Select(i => i.Gesture), Is.EqualTo(new[] { new KeyGesture(Key.F3), new KeyGesture(Key.F4),
                    new KeyGesture(Key.Tab, KeyModifiers.Control), new KeyGesture(Key.F2), new KeyGesture(Key.F10) }), "the view keys, key equivalents too (ui-gallery.md)");
                var windowMenu = bar.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Window").Menu;
                Assert.That(windowMenu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Minimize").Gesture, Is.EqualTo(new KeyGesture(Key.M, KeyModifiers.Meta)), "⌘M");
                var appMenu = AppMenu.ToNativeMenu(window.AppMenuModel());
                var preferences = appMenu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Preferences…");
                Assert.That(preferences.Gesture, Is.EqualTo(new KeyGesture(Key.OemComma, KeyModifiers.Meta)));
                preferences.Command.Execute(null);
                Assert.That(PreferencesWindow.Current, Is.Not.Null, "the app menu's Preferences… opens them");
                PreferencesWindow.Current.Close();

                // Elsewhere: the window's own menu, with Preferences in Edit and About in Help.
                var other = window.MenuModel(mac: false).ToList();
                Assert.That(AppMenu.Find(other, "Edit/Preferences…").Gesture, Is.EqualTo(new KeyGesture(Key.OemComma, KeyModifiers.Control)));
                Assert.That(AppMenu.Find(other, "Help/About NeoBuzz Live"), Is.Not.Null);
                Assert.That(AppMenu.Find(other, "File/Exit"), Is.Not.Null);
                if (!System.OperatingSystem.IsMacOS())
                    Assert.That(window.WindowMenu.Items.OfType<MenuItem>().Select(i => (string)i.Tag), Is.EqualTo(new[] { "File", "Edit", "Transport", "View", "Window", "Help" }));
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaTest]
        public void ShortcutsOpenPreferencesTheAgentPageAndTheViews()
        {
            using var restore = Studio.AfterTest(Reset);
            var window = Open();
            try
            {
                window.KeyPressQwerty(PhysicalKey.Comma, Cmd);
                Settle(window);
                Assert.That(PreferencesWindow.Current, Is.Not.Null, "Command+, opens Preferences");
                PreferencesWindow.Current.Close();
                Settle();

                window.KeyPressQwerty(PhysicalKey.A, Cmd | RawInputModifiers.Shift);
                Settle(window);
                Assert.That(PreferencesWindow.Current?.Selected?.Title, Is.EqualTo("Agent"), "Shift+Command+A: the agent's connection");
                PreferencesWindow.Current.Close();
                Settle();

                string Selected() => (string)((TabItem)window.Tabs.SelectedItem!).Header!;
                window.Focus();
                window.KeyPressQwerty(PhysicalKey.F4, RawInputModifiers.None);
                Assert.That(Selected(), Is.EqualTo("Sequence"));
                window.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);
                Assert.That(Selected(), Is.EqualTo("Pattern"));
                window.KeyPressQwerty(PhysicalKey.F3, RawInputModifiers.None);
                Assert.That(Selected(), Is.EqualTo("Machines"));
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
                Assert.That(Selected(), Is.EqualTo("Session"), "Ctrl+Tab: the Session View");
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
                Assert.That(Selected(), Is.EqualTo("Sequence"), "and back to the arrangement");

                // The View menu's check follows.
                var view = AppMenu.Find(window.MenuModel(mac: false), "View").Items().ToList();
                Assert.That(view.Single(e => e.IsChecked()).Text, Is.EqualTo("Sequence"));
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaTest]
        public void TheWindowKeepsItsSizeAndPlace()
        {
            using var restore = Studio.AfterTest(Reset);
            var registry = Host.Registry;
            var window = new MainWindow(Host) { Width = 1000, Height = 700 };
            window.Show();
            window.Position = new PixelPoint(140, 90);
            Settle(window);
            var placed = window.Position;
            window.Close();
            Settle();
            try
            {
                Assert.That(registry.Read("MainWindowWidth", 0.0, "Studio"), Is.EqualTo(1000));
                Assert.That(registry.Read("MainWindowHeight", 0.0, "Studio"), Is.EqualTo(700));
                Assert.That((registry.Read("MainWindowX", -1, "Studio"), registry.Read("MainWindowY", -1, "Studio")), Is.EqualTo((placed.X, placed.Y)));
                var again = new MainWindow(Host);
                Assert.That((again.Width, again.Height), Is.EqualTo((1000.0, 700.0)), "the same size");
                Assert.That(again.WindowStartupLocation, Is.EqualTo(WindowStartupLocation.Manual), "at its place, not centred");
                again.Show();
                Settle(again);
                Assert.That(again.Position, Is.EqualTo(placed));
                again.WindowState = WindowState.Maximized;
                again.Close();
                Assert.That(registry.Read("MainWindowMaximized", false, "Studio"), Is.True);
                Assert.That(registry.Read("MainWindowWidth", 0.0, "Studio"), Is.EqualTo(1000), "maximized keeps the size it had");
                var third = new MainWindow(Host);
                Assert.That(third.WindowState, Is.EqualTo(WindowState.Maximized));
            }
            finally
            {
                foreach (var key in new[] { "Width", "Height", "X", "Y", "Maximized" }) registry.DeleteCurrentUserValue("MainWindow" + key, "Studio");
            }
        }

        [AvaloniaTest]
        public void TheWindowFitsTheScreensVisibleArea()
        {
            // Fork (ui-gallery.md): the default 1280×820 window ran off a 1024×768 screen. On opening, a window is moved and
            // shrunk into its screen's visible area (the Mac's without the menu bar and the Dock), title bar included.
            using var restore = Studio.AfterTest(Reset);
            var area = new Rect(0, 25, 1024, 680);                  // a 1024×768 Mac: the menu bar above, the Dock below
            Assert.That(WindowBounds.Fit(new Rect(128, 50, 1280, 848), area), Is.EqualTo(area), "the default frame on a small screen");
            Assert.That(WindowBounds.Fit(new Rect(100, 50, 600, 400), area), Is.EqualTo(new Rect(100, 50, 600, 400)), "a window that fits stays");
            Assert.That(WindowBounds.Fit(new Rect(900, 500, 600, 400), area), Is.EqualTo(new Rect(424, 305, 600, 400)), "one that hangs off is moved in");
            Assert.That(WindowBounds.Fit(new Rect(-50, 0, 600, 400), area), Is.EqualTo(new Rect(0, 25, 600, 400)), "or up and left");

            // The main window, kept at a size larger than the (headless, 1920×1280) screen.
            var registry = Host.Registry;
            registry.Write("MainWindowWidth", 5000.0, "Studio");
            registry.Write("MainWindowHeight", 4000.0, "Studio");
            registry.Write("MainWindowX", 300, "Studio");
            registry.Write("MainWindowY", 200, "Studio");
            MainWindow window = null;
            try
            {
                window = new MainWindow(Host);
                window.Show();
                Settle(window);
                var screen = window.Screens.Primary!.WorkingArea;
                Assert.That((window.Width, window.Height), Is.EqualTo(((double)screen.Width, (double)screen.Height)), "as large as the screen, no larger");
                Assert.That(window.Position, Is.EqualTo(new PixelPoint(screen.X, screen.Y)), "on it");
                window.Close();
                Settle();
                Assert.That(registry.Read("MainWindowWidth", 0.0, "Studio"), Is.EqualTo(screen.Width), "the size it has is the one kept");
            }
            finally
            {
                window?.Close();
                foreach (var key in new[] { "Width", "Height", "X", "Y", "Maximized" }) registry.DeleteCurrentUserValue("MainWindow" + key, "Studio");
            }
        }

        [AvaloniaTest]
        public void AboutShowsTheBuildAndTheCredits()
        {
            using var restore = Studio.AfterTest(Reset);
            var about = AboutWindow.Open();
            Settle(about);
            Assert.That(about.Title, Is.EqualTo("About NeoBuzz Live"));
            Assert.That(about.Text, Does.StartWith("NeoBuzz Live").And.Contain("Oskari Tammelin"));
            // What it is based on: ReBuzz, its authors and link, its licence.
            Assert.That(about.Text, Does.Contain("Based on ReBuzz\nby WDE and the ReBuzz contributors\ngithub.com/wasteddesign/ReBuzz\n"));
            Assert.That(about.Text, Does.Contain(" WDE, MIT licence\n"));
            if (!string.IsNullOrWhiteSpace(BuildInfo.Number)) Assert.That(about.Text, Does.Contain("(build " + BuildInfo.Number.Trim() + ")"));
            Assert.That(AboutWindow.Open(), Is.SameAs(about), "one at a time");
            Studio.Screenshot(about, "studio-about");
            about.Close();
        }

        [AvaloniaTest]
        public void TheStatusLineShowsTheAgentAndNoLoadWithoutAudio()
        {
            using var restore = Studio.AfterTest(Reset);
            var window = Open();
            try
            {
                window.RefreshNow();
                Assert.That(window.StatusText, Does.StartWith("no audio output").And.Not.Contain("CPU"));
                Assert.That(window.CpuLoad, Is.Null);
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ the Session View's Sync and Controllers menus

        [AvaloniaTest]
        public void TheSessionViewsSyncAndControllersMenus()
        {
            using var restore = Studio.AfterTest(Reset);
            var sync = Core.SessionSync;
            using var rig = new SessionRig("Lead");
            var view = rig.Show();
            try
            {
                rig.Click(view.SettingsButton);
                var menu = view.LastMenu;
                Assert.That(menu.Items.OfType<MenuItem>().Select(i => i.Tag as string), Does.Contain("Sync").And.Contain("Controllers"));
                var controllers = menu.Items.OfType<MenuItem>().Single(i => (string)i.Tag == "Controllers");
                Assert.That(controllers.Items.OfType<MenuItem>().Any(), Is.True, "a controller, or none found");
                rig.Screenshot("session-sync-menu");
                rig.Menu("Sync/Follow MIDI clock in");
                Assert.That(sync.FollowMidiClock, Is.True);

                rig.Click(view.SettingsButton);
                var syncItem = view.LastMenu.Items.OfType<MenuItem>().Single(i => (string)i.Tag == "Sync");
                Assert.That((string)syncItem.Header, Does.StartWith("Sync (").IgnoreCase, "the menu shows what syncs");
                Assert.That(syncItem.Items.OfType<MenuItem>().Single(i => (string)i.Tag == "Follow MIDI clock in").IsChecked, Is.True);
                rig.Menu("Sync/Follow MIDI clock in");
                Assert.That(sync.FollowMidiClock, Is.False);
            }
            finally
            {
                sync.SetFollowMidiClock(false);
            }
        }

        [AvaloniaTest]
        public void OnAMacEveryWindowHasTheMenuBarsMenus()
        {
            // Fork (ui-gallery.md): macOS shows the key window's menus. Preferences, About and the other windows had none,
            // so while one was the key window the menu bar showed the app menu alone.
            using var restore = Studio.AfterTest(Reset);
            bool mac = App.MacMenus;
            App.MacMenus = true;
            MainWindow window = null;
            try
            {
                window = Open();
                Assert.That(NativeMenu.GetMenu(window), Is.Not.Null, "the main window's own");
                var prefs = PreferencesWindow.Open(Host);
                Settle(prefs);
                var menu = NativeMenu.GetMenu(prefs);
                Assert.That(menu, Is.Not.Null, "Preferences has the menus");
                Assert.That(menu.Items.OfType<NativeMenuItem>().Select(i => i.Header), Is.EqualTo(new[] { "File", "Edit", "Transport", "View", "Window", "Help" }));
                prefs.Close();
                var about = AboutWindow.Open();
                Settle(about);
                Assert.That(NativeMenu.GetMenu(about)?.Items.Count, Is.EqualTo(6), "About too");
                about.Close();

                var own = new NativeMenu();
                var other = new Window();
                NativeMenu.SetMenu(other, own);
                other.Show();
                Assert.That(NativeMenu.GetMenu(other), Is.SameAs(own), "a window with menus of its own keeps them");
                other.Close();
                if (System.OperatingSystem.IsMacOS())
                    Assert.That(NativeMenu.GetMenu(Application.Current!)?.Items.OfType<NativeMenuItem>().First().Header, Is.EqualTo("About NeoBuzz Live"),
                        "the app menu, set on the application as it starts (before Avalonia's exporter made its own)");
            }
            finally
            {
                App.MacMenus = mac;
                window?.Close();
            }
        }

        [AvaloniaTest]
        public void PreferencesDrawsBeforeItAsksForTheDevices()
        {
            // Fork (ui-gallery.md): 2 s after ⌘, the real Mac's Preferences was still blank. The page's state (PortAudio's
            // devices, CoreMIDI's) comes once the window has drawn.
            using var restore = Studio.AfterTest(Reset);
            var window = Open();
            int asked = 0;
            bool shown = false;
            AudioPage.OutputSource = () =>
            {
                asked++;
                shown = PreferencesWindow.Current?.IsVisible == true;
                return new[] { "PortAudio: default", "PortAudio: Test Speakers (Core Audio)" };
            };
            try
            {
                var prefs = PreferencesWindow.Open(Host, PreferencesWindow.AudioPageName);
                Assert.That(prefs.IsVisible, Is.True);
                Assert.That(prefs.Selected?.Title, Is.EqualTo(PreferencesWindow.AudioPageName), "on its page");
                Assert.That(asked, Is.EqualTo(0), "the devices aren't asked for before the window shows");
                var audio = prefs.Page<AudioPage>();
                Assert.That(audio.LimiterBox.IsChecked, Is.True, "the rest of the page is there as it shows (no limiter shown off)");
                Assert.That(audio.SaysPortAudioIsMissing, Is.False, "nor PortAudio missing before it has been asked");
                Settle(prefs);
                Assert.That(asked, Is.EqualTo(1), "then once");
                Assert.That(shown, Is.True, "with the window shown");
                Assert.That(prefs.Page<AudioPage>().OutputBox.ItemCount, Is.GreaterThanOrEqualTo(2), "and the page filled");
            }
            finally
            {
                AudioPage.OutputSource = null;
                PreferencesWindow.Current?.Close();
                window.Close();
            }
        }

        [AvaloniaTest]
        public void TheViewItemsKeyEquivalentsDoWhatTheirKeysDoAndMinimizeIsTheKeyWindows()
        {
            // Fork (ui-gallery.md): the macOS menu bar's View items have their keys as key equivalents. The menu bar takes
            // them before the window, so an item chosen by its key does what the key does in the window (Ctrl+Tab goes back
            // from the Session View; F2 renames a clip there), and a click chooses the view. Minimize (⌘M) minimizes the
            // key window.
            using var restore = Studio.AfterTest(Reset);
            var window = Open();
            try
            {
                string Shown() => (window.Tabs.SelectedItem as TabItem)?.Header as string;
                var menus = window.MenuModel(mac: true).ToList();
                var session = AppMenu.Find(menus, "View/Session");
                window.ShowView("Session");
                MacApp.KeyDownSource = () => Key.Tab;               // chosen by Ctrl+Tab
                session.Run();
                Assert.That(Shown(), Is.EqualTo("Sequence"), "Ctrl+Tab from the Session View goes back, as in the window");
                MacApp.KeyDownSource = () => null;                  // a click
                session.Run();
                session.Run();
                Assert.That(Shown(), Is.EqualTo("Session"), "a click chooses the Session View");
                MacApp.KeyDownSource = () => Key.F3;
                AppMenu.Find(menus, "View/Machines").Run();
                Assert.That(Shown(), Is.EqualTo("Machines"), "F3, which nothing in the Session View takes: the Machine View");

                var prefs = PreferencesWindow.Open(Host);
                Settle(prefs);
                prefs.Activate();
                Settle(prefs);
                Assert.That(prefs.IsActive, Is.True, "Preferences is the key window");
                AppMenu.Find(menus, "Window/Minimize").Run();
                Assert.That(prefs.WindowState, Is.EqualTo(WindowState.Minimized), "the key window minimized");
                Assert.That(window.WindowState, Is.Not.EqualTo(WindowState.Minimized), "not the main window");
                prefs.Close();

                // The Machine View's mute: M on a Mac (⌘M minimizes there), Ctrl+M elsewhere.
                var machines = window.Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<ReBuzz.Studio.Views.Machines.MachineView>().First();
                bool macMute = machines.Canvas.KeyBindings.Any(b => b.Gesture.Equals(new KeyGesture(Key.M)));
                bool ctrlMute = machines.KeyBindings.Any(b => b.Gesture.Equals(new KeyGesture(Key.M, KeyModifiers.Control)));
                Assert.That((macMute, ctrlMute), Is.EqualTo(System.OperatingSystem.IsMacOS() ? (true, false) : (false, true)));
            }
            finally
            {
                MacApp.KeyDownSource = null;
                PreferencesWindow.Current?.Close();
                window.Close();
            }
        }
    }
}
