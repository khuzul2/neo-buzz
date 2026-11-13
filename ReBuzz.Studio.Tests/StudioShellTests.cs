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
using Avalonia.VisualTree;
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

        // ------------------------------------------------------------------ the status line

        [AvaloniaTest]
        public void TheStatusLineCountsTheMachinesYouSee()
        {
            // Gallery mac-010 ("1 machines" for the Master alone) and mac-020 ("15 machines" for the
            // demo song's 10 and the Master, its 4 hidden pattern-editor machines counted).
            NewSong();
            var window = Open();
            try
            {
                window.RefreshNow();
                Assert.That(window.StatusText, Does.Contain(" · no machines"), "a new song: the Master alone");
                var song = Core.SongCore;
                song.CreateMachine("Neo Poly", null, "Lead", null, null, null, -1, 0.2f, 0.2f);
                window.RefreshNow();
                Assert.That(window.StatusText, Does.Contain(" · 1 machine").And.Not.Contain("1 machines"));
                song.CreateMachine("Neo Poly", null, "Bass", null, null, null, -1, -0.2f, 0.2f);
                song.CreateMachine("Neo Poly", null, "Editor", null, null, null, -1, -0.2f, -0.2f);
                song.MachinesList.Single(m => m.Name == "Editor").Hidden = true;      // as a song's pattern-editor machines are
                window.RefreshNow();
                Assert.That(window.StatusText, Does.Contain(" · 2 machines"), "not the hidden one, not the Master");
                Assert.That(song.MachinesList.Count, Is.EqualTo(4));
            }
            finally
            {
                NewSong();
                window.Close();
            }
        }

        // ------------------------------------------------------------------ a machine that stops working

        [AvaloniaTest]
        public void AMachineThatStopsWorkingIsToldByNameInTheStatusLine()
        {
            // Gallery win-704: a native machine's host stopped answering; only the debug console said so,
            // and its "Process hang" line named no machine.
            NewSong();
            var window = Open();
            var told = new System.Collections.Generic.List<string>();
            System.Action<MachineCore, string> handler = (m, reason) => told.Add($"{m.Name}: {reason}");
            Core.MachineCrashed += handler;
            try
            {
                Core.SongCore.CreateMachine("Neo Poly", null, "Delay", null, null, null, -1, 0.2f, 0.2f);
                var delay = Core.SongCore.MachinesList.Single(m => m.Name == "Delay");
                Core.ReportMachineCrashed(delay, "its host process didn't answer within 2000 ms");
                Core.ReportMachineCrashed(delay, "again");
                Settle(window);
                Assert.That(told, Is.EqualTo(new[] { "Delay: its host process didn't answer within 2000 ms" }), "told once, by name");
                window.RefreshNow();
                Assert.That(window.StatusText, Does.Contain(" · Stopped working: Delay"));
                NewSong();
                window.RefreshNow();
                Assert.That(window.StatusText, Does.Not.Contain("Stopped working"), "a new song starts clean");
            }
            finally
            {
                Core.MachineCrashed -= handler;
                NewSong();
                window.Close();
            }
        }

        // ------------------------------------------------------------------ a song that can't be opened

        [AvaloniaTest]
        public void ASongThatCantBeOpenedSaysSoWithTheExceptionUnderDetails()
        {
            // Gallery mac-338: the error said only "Unable to read beyond the end of the stream."
            NewSong();
            var window = Open();
            Window dialog = null;
            using var opened = Window.WindowOpenedEvent.AddClassHandler<Window>((w, e) => { if (w != window) dialog = w; });
            string broken = Path.Combine(SongFolder, "Broken song.bmx");
            File.WriteAllBytes(broken, System.Text.Encoding.ASCII.GetBytes("Buzz\0not a song at all"));
            try
            {
                var opening = SongFiles.Open(broken);
                for (int i = 0; i < 40 && dialog == null; i++) Settle(window);
                Assert.That(dialog, Is.Not.Null, "an error dialog");
                var text = dialog!.GetVisualDescendants().OfType<TextBlock>().Single(t => (t.Tag as string) == "message");
                Assert.That(text.Text, Is.EqualTo("\u201CBroken song.bmx\u201D couldn't be opened. The file is damaged or isn't a song this app can read."));
                var details = dialog.GetVisualDescendants().OfType<Expander>().Single();
                Assert.That((details.Header as string, details.IsExpanded), Is.EqualTo(("Details", false)), "the exception under Details, closed");
                details.IsExpanded = true;
                Settle(window);
                var box = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => (t.Tag as string) == "details");
                Assert.That(box.Text, Does.Contain("Exception: "), "the exception's type and message");
                Studio.Screenshot(dialog, "error-open-details");
                dialog.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == "OK").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                for (int i = 0; i < 20 && !opening.IsCompleted; i++) Settle(window);
                Assert.That(opening.IsCompleted && !opening.Result, Is.True, "it didn't open");

                // A song that needs a machine that isn't installed says so (the loader's own reason, kept).
                Assert.That(ReBuzzCore.OpenFailedMessage("/songs/Makk_M4.bmw", new System.Exception("To load this song, you need to have following machine in your Gear forlder: M4")),
                    Is.EqualTo("\u201CMakk_M4.bmw\u201D couldn't be opened: it uses M4, a machine that isn't installed."));
            }
            finally
            {
                NewSong();
                window.Close();
            }
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
                // Fork (ux-overhaul.md §4): ⇧⌘A is Deselect All (it was the agent's page, still in the Window menu).
                Assert.That(AppMenu.Find(mac, "Window/Agent Connection…").Gesture, Is.Null);
                Assert.That(AppMenu.Find(mac, "Edit/Deselect All").Gesture, Is.EqualTo(new KeyGesture(Key.A, KeyModifiers.Meta | KeyModifiers.Shift)));

                // As the menu bar gets them (NativeMenu): headers without access keys, key equivalents, submenus.
                var bar = AppMenu.ToNativeMenu(mac);
                Assert.That(bar.Items.OfType<NativeMenuItem>().Select(i => i.Header), Is.EqualTo(new[] { "File", "Edit", "Transport", "View", "Window", "Help" }));
                var fileMenu = bar.Items.OfType<NativeMenuItem>().First().Menu;
                Assert.That(fileMenu.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).Select(i => i.Header),
                    Is.EqualTo(new[] { "New", "Open…", "Open Recent", "Close Song", "Save", "Save As…", "Export Audio…", "Revert to Saved" }), "Close (⌘W) and Revert (UX review mac-real-101)");
                Assert.That(fileMenu.Items[3], Is.InstanceOf<NativeMenuItemSeparator>());
                Assert.That(fileMenu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Open Recent").Menu, Is.Not.Null);
                var viewMenu = bar.Items.OfType<NativeMenuItem>().Single(i => i.Header == "View").Menu;
                var views = viewMenu.Items.OfType<NativeMenuItem>().Take(5).ToList();
                Assert.That(views.Select(i => i.Header), Is.EqualTo(new[] { "Arrangement", "Session", "Machines", "Tracker", "Song Notes" }));
                Assert.That(views.Select(i => i.Gesture), Is.EqualTo(Enumerable.Range(0, 5).Select(i => new KeyGesture(Key.D1 + i, KeyModifiers.Meta))),
                    "⌘1–⌘5 in tab order (ux-overhaul.md §4); Buzz's F-keys stay the window's");
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

                AppMenu.Find(window.MenuModel(mac: false), "Window/Agent Connection…").Run();
                Settle(window);
                Assert.That(PreferencesWindow.Current?.Selected?.Title, Is.EqualTo("Agent"), "Window ▸ Agent Connection…: the agent's connection");
                PreferencesWindow.Current.Close();
                Settle();

                string Selected() => (string)((TabItem)window.Tabs.SelectedItem!).Header!;
                window.Focus();
                window.KeyPressQwerty(PhysicalKey.F4, RawInputModifiers.None);
                Assert.That(Selected(), Is.EqualTo("Arrangement"));
                window.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);
                Assert.That(window.Detail.IsMaximised, Is.True, "F2: the Tracker over the whole window (P3a)");
                Assert.That(Selected(), Is.EqualTo("Arrangement"), "the view behind it stays");
                window.KeyPressQwerty(PhysicalKey.F3, RawInputModifiers.None);
                Assert.That(Selected(), Is.EqualTo("Machines"));
                Assert.That(window.Detail.IsMaximised, Is.False, "a view's key brings the views back");
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
                Assert.That(Selected(), Is.EqualTo("Session"), "Ctrl+Tab: the Session View");
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
                Assert.That(Selected(), Is.EqualTo("Arrangement"), "and back to the arrangement");

                // ⌘1–⌘5 (Ctrl elsewhere), in tab order (ux-overhaul.md §4).
                string[] order = { "Arrangement", "Session", "Machines", "Pattern" };
                for (int i = 3; i >= 0; i--)
                {
                    window.KeyPressQwerty(PhysicalKey.Digit1 + i, Cmd);
                    if (order[i] == "Pattern") Assert.That(window.Detail.IsOpen, Is.True, "⌘4: the Tracker, in the detail panel (P3a)");
                    else Assert.That(Selected(), Is.EqualTo(order[i]), $"⌘{i + 1}");
                }

                // The View menu's check follows.
                var view = AppMenu.Find(window.MenuModel(mac: false), "View").Items().ToList();
                Assert.That(view.Take(5).Where(e => e.IsChecked()).Select(e => e.Text), Is.EquivalentTo(new[] { "Arrangement", "Tracker" }), "the view shown, and the Tracker open");

                window.KeyPressQwerty(PhysicalKey.Digit5, Cmd);
                Assert.That(window.Inspector.CurrentTab, Is.EqualTo("Song"), "⌘5: Song Notes, the inspector's Song tab (P3a)");
                Assert.That(Selected(), Is.EqualTo("Arrangement"), "no tab of its own: the view stays");
                view = AppMenu.Find(window.MenuModel(mac: false), "View").Items().ToList();
                Assert.That(view.Take(5).Where(e => e.IsChecked()).Select(e => e.Text), Is.EquivalentTo(new[] { "Arrangement", "Tracker", "Song Notes" }));
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
                // Fork (ux-overhaul.md P1): the audio-health badge says it, in red; the status line doesn't repeat it.
                Assert.That((window.Health.Shown, window.Health.Text), Is.EqualTo((AudioHealthBadge.State.NoOutput, "No audio output")));
                Assert.That(window.StatusText, Does.StartWith("dropouts ").And.Not.Contain("CPU").And.Not.Contain("no audio output"));
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
                prefs.Page<SafetyPage>().Show();
                Assert.That(prefs.Page<SafetyPage>().LimiterBox.IsChecked, Is.True, "the Safety page is there as it shows (no limiter shown off)");
                Assert.That(audio.SaysPortAudioIsMissing, Is.False, "nor PortAudio missing before it has been asked");
                Assert.That(audio.OutputBox.Items.OfType<string>(), Has.None.Contains("not connected"), "nor the device not connected");
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
        public void TheViewItemsChooseTheirViewsAndMinimizeIsTheKeyWindows()
        {
            // Fork (ux-overhaul.md §4): the View items' key equivalents are ⌘1–⌘5; Buzz's F-keys and Ctrl+Tab are the
            // window's, so F2 still renames a clip in the Session View and Ctrl+Tab still goes back from it. Minimize (⌘M)
            // minimizes the key window.
            using var restore = Studio.AfterTest(Reset);
            var window = Open();
            try
            {
                string Shown() => (window.Tabs.SelectedItem as TabItem)?.Header as string;
                var menus = window.MenuModel(mac: true).ToList();
                window.ShowView("Session");
                AppMenu.Find(menus, "View/Machines").Run();
                Assert.That(Shown(), Is.EqualTo("Machines"));
                AppMenu.Find(menus, "View/Session").Run();
                Assert.That(Shown(), Is.EqualTo("Session"), "an item chooses its view");
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
                Assert.That(Shown(), Is.EqualTo("Arrangement"), "Ctrl+Tab from the Session View goes back, as before");

                var prefs = PreferencesWindow.Open(Host);
                Settle(prefs);
                prefs.Activate();
                Settle(prefs);
                Assert.That(prefs.IsActive, Is.True, "Preferences is the key window");
                AppMenu.Find(menus, "Window/Minimize").Run();
                Assert.That(prefs.WindowState, Is.EqualTo(WindowState.Minimized), "the key window minimized");
                Assert.That(window.WindowState, Is.Not.EqualTo(WindowState.Minimized), "not the main window");
                prefs.Close();

                // The Machine View's mute: M in both apps (ux-overhaul.md §4; ⌘M minimizes on a Mac), and Ctrl+M too off a Mac.
                var machines = window.Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<ReBuzz.Studio.Views.Machines.MachineView>().First();
                bool mute = machines.Canvas.KeyBindings.Any(b => b.Gesture.Equals(new KeyGesture(Key.M)));
                bool ctrlMute = machines.KeyBindings.Any(b => b.Gesture.Equals(new KeyGesture(Key.M, KeyModifiers.Control)));
                Assert.That((mute, ctrlMute), Is.EqualTo((true, !System.OperatingSystem.IsMacOS())));
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
