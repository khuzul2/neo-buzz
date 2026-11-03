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

        [TearDown]
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
                Assert.That(asked, Does.Contain("didn't close normally").And.Contain("'crashed song.bmxml'"));
                Assert.That(Core.SongCore.SongName, Is.EqualTo(song));
                Assert.That(Core.SongCore.MachinesList.Select(m => m.Name), Does.Contain("After Crash"));
                Assert.That(Core.BPM, Is.EqualTo(133));
                Assert.That(Core.Modified, Is.True, "save to keep them");
                Assert.That(SongAutosave.FindLeftovers(), Is.Empty, "the autosave is used up");
                window.RefreshNow();
                Studio.Screenshot(window, "studio-autosave-recovered");

                // No deletes it.
                Assert.That(Core.Autosave.SaveNow(System.DateTime.Now), Is.True);
                data = File.ReadAllBytes(Core.Autosave.Current);
                autosave = Core.Autosave.Current;
                NewSong();
                File.WriteAllBytes(autosave, data);
                File.WriteAllText(autosave + ".txt", song);
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Assert.That(Autosave.OfferRecovery(Core).Result, Is.False);
                Assert.That(SongAutosave.FindLeftovers(), Is.Empty);
            }
            finally
            {
                foreach (var leftover in SongAutosave.FindLeftovers()) SongAutosave.Delete(leftover.Path);
                NewSong();
                window.Close();
            }
        }

        // ------------------------------------------------------------------ menus and keys

        [AvaloniaTest]
        public void TheMacMenuBarHasTheAppMenuAndTheStudiosMenus()
        {
            var window = Open();
            try
            {
                var app = window.AppMenuModel().Where(e => !e.IsSeparator).ToList();
                Assert.That(app.Select(e => e.Text), Is.EqualTo(new[] { "About ReBuzz", "Preferences…" }));
                Assert.That(app[1].Gesture, Is.EqualTo(new KeyGesture(Key.OemComma, KeyModifiers.Meta)), "Command+,");

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
                Assert.That(viewMenu.Items.OfType<NativeMenuItem>().All(i => i.Gesture == null), "view keys stay with the window (F2 renames in the Session View)");
                var appMenu = AppMenu.ToNativeMenu(window.AppMenuModel());
                var preferences = appMenu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Preferences…");
                Assert.That(preferences.Gesture, Is.EqualTo(new KeyGesture(Key.OemComma, KeyModifiers.Meta)));
                preferences.Command.Execute(null);
                Assert.That(PreferencesWindow.Current, Is.Not.Null, "the app menu's Preferences… opens them");
                PreferencesWindow.Current.Close();

                // Elsewhere: the window's own menu, with Preferences in Edit and About in Help.
                var other = window.MenuModel(mac: false).ToList();
                Assert.That(AppMenu.Find(other, "Edit/Preferences…").Gesture, Is.EqualTo(new KeyGesture(Key.OemComma, KeyModifiers.Control)));
                Assert.That(AppMenu.Find(other, "Help/About ReBuzz"), Is.Not.Null);
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
        public void AboutShowsTheBuildAndTheCredits()
        {
            var about = AboutWindow.Open();
            Settle(about);
            Assert.That(about.Text, Does.StartWith("ReBuzz").And.Contain("Oskari Tammelin"));
            if (!string.IsNullOrWhiteSpace(BuildInfo.Number)) Assert.That(about.Text, Does.Contain("(build " + BuildInfo.Number.Trim() + ")"));
            Assert.That(AboutWindow.Open(), Is.SameAs(about), "one at a time");
            Studio.Screenshot(about, "studio-about");
            about.Close();
        }

        [AvaloniaTest]
        public void TheStatusLineShowsTheAgentAndNoLoadWithoutAudio()
        {
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
    }
}
