// Fork (docs/specs/ux-overhaul.md P1): the UX overhaul's quick wins, headless: the state colour tokens and the contrast
// floor, the one keymap (menus, keys, the shortcuts window, tooltips), Duplicate, Delete, Deselect and Rename in every view,
// the single-key transport keys, the problem bar and the audio-health badge, and one time unit.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Commands;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Session;
using static ReBuzz.Studio.Tests.MachineViewTests;
using ReBuzzTests.Automation;

namespace ReBuzz.Studio.Tests
{
    public class UxQuickWinTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static readonly string[] Reserved = { "Red", "Orange", "Ochre", "Kelly Green" };

        // ------------------------------------------------------------------ tokens: state colours, contrast floor

        [Test]
        public void TextOnAnyPaletteColourReachesTheContrastFloor()
        {
            // UX review mac-040: "Hook" on green was 2.88:1, "Fill" on orange 2.96:1 with the near-white and near-black pair.
            foreach (var (name, argb) in SessionColourChoices.Defaults.Append(("Default clip", 0xFF3B5878u)))
            {
                var c = Color.FromUInt32(argb);
                double ratio = StudioTokens.Contrast(c, StudioTokens.TextOn(c));
                Assert.That(ratio, Is.GreaterThanOrEqualTo(4.5), $"{name}: {ratio:0.00}:1");
            }
            // And on every colour there is: white or black always reaches 4.58:1.
            for (int r = 0; r < 256; r += 15)
                for (int g = 0; g < 256; g += 15)
                    for (int b = 0; b < 256; b += 15)
                    {
                        var c = Color.FromRgb((byte)r, (byte)g, (byte)b);
                        Assert.That(StudioTokens.Contrast(c, StudioTokens.TextOn(c)), Is.GreaterThanOrEqualTo(4.58));
                    }
        }

        [Test]
        public void TheTokensMeetTheirMeasuredRatios()
        {
            // ux-overhaul.md §3.1's table, computed (WCAG 2.2 relative luminance).
            Assert.That(StudioTokens.Contrast(StudioTokens.TextPrimary, StudioTokens.SurfaceView), Is.GreaterThan(14.0));
            Assert.That(StudioTokens.Contrast(StudioTokens.TextSecondary, StudioTokens.SurfaceControl), Is.GreaterThan(5.2));
            Assert.That(StudioTokens.Contrast(StudioTokens.TextPlaceholder, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(4.6));
            Assert.That(StudioTokens.Contrast(Colors.White, StudioTokens.SelectionFill), Is.GreaterThan(6.4));
            foreach (var surface in new[] { StudioTokens.SurfaceBase, StudioTokens.SurfaceView, StudioTokens.SurfacePanel, StudioTokens.SurfaceControl, StudioTokens.SurfaceBorder })
                Assert.That(StudioTokens.Contrast(StudioTokens.Accent, surface), Is.GreaterThanOrEqualTo(3.0), "the accent is a UI part on every surface");
            foreach (var state in StudioTokens.StateColours)
                Assert.That(StudioTokens.Contrast(state, StudioTokens.SurfaceBase), Is.GreaterThanOrEqualTo(4.5), "a state glyph on its dark chip");
        }

        [AvaloniaTest]
        public void PlaceholdersAreDrawnWholeAtThePlaceholderToken()
        {
            // UX review mac-014: Fluent drew placeholders at half opacity over its own grey, 2.54:1 in Song Notes.
            var box = new TextBox { PlaceholderText = "The song's notes", Width = 300 };
            var window = new Window { Content = box, Width = 320, Height = 80, Background = StudioTokens.SurfaceViewBrush };
            window.Show();
            try
            {
                Settle();
                var placeholder = box.GetVisualDescendants().OfType<Avalonia.Controls.Control>().FirstOrDefault(c => c.Name == "PART_Placeholder");
                Assert.That(placeholder, Is.Not.Null, "the theme's placeholder part");
                Assert.That(placeholder!.Opacity, Is.EqualTo(1.0));
                Assert.That(((ISolidColorBrush)Application.Current!.Resources["TextControlPlaceholderForeground"]!).Color, Is.EqualTo(StudioTokens.TextPlaceholder));
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void StateColoursAreNeverOfferedAsClipColoursButAColourInUseStays()
        {
            var choices = SessionPalette.ColorChoices;
            var reserved = choices.Select((c, i) => (c, i)).Where(x => StudioTokens.IsReservedForState(x.c.Color)).Select(x => x.c.Name).ToList();
            Assert.That(reserved, Is.EquivalentTo(Reserved), "the default palette's entries within ΔE 20 of play, record or override");
            var offered = SessionPalette.OfferedColours().Select(o => o.Name).ToList();
            Assert.That(offered.Intersect(Reserved), Is.Empty);
            Assert.That(offered.Count, Is.EqualTo(choices.Length - Reserved.Length));
            int red = Array.FindIndex(choices, c => c.Name == "Red");
            Assert.That(SessionPalette.OfferedColours(red).Select(o => o.Index), Does.Contain(red), "a clip that is red already keeps it ticked");
            foreach (var o in SessionPalette.OfferedColours())
                foreach (var s in StudioTokens.StateColours)
                    Assert.That(StudioTokens.DeltaE(o.Color, s), Is.GreaterThanOrEqualTo(StudioTokens.ReservedDistance), o.Name);
        }

        // ------------------------------------------------------------------ the keymap

        [Test]
        public void KeysAreWrittenOnceAndReadForThePlatform()
        {
            Assert.That(KeyText.Parse("Shift+Cmd+A", mac: true), Is.EqualTo(new KeyGesture(Key.A, KeyModifiers.Shift | KeyModifiers.Meta)));
            Assert.That(KeyText.Parse("Shift+Cmd+A", mac: false), Is.EqualTo(new KeyGesture(Key.A, KeyModifiers.Shift | KeyModifiers.Control)));
            Assert.That(KeyText.Parse("Ctrl+Tab", mac: true), Is.EqualTo(new KeyGesture(Key.Tab, KeyModifiers.Control)), "Ctrl is the control key on a Mac too");
            Assert.That(KeyText.Of("Shift+Cmd+A", mac: true), Is.EqualTo("⇧⌘A"));
            Assert.That(KeyText.Of("Shift+Cmd+A", mac: false), Is.EqualTo("Ctrl+Shift+A"));
            Assert.That(KeyText.Of("Back|Delete", mac: true), Is.EqualTo("⌫ or ⌦"));
            Assert.That(KeyText.Of("R|F7", mac: false), Is.EqualTo("R or F7"));
            Assert.That(KeyText.Of("Alt+Cmd+M", mac: true), Is.EqualTo("⌥⌘M"));
            Assert.That(KeyText.Of("Cmd+1", mac: false), Is.EqualTo("Ctrl+1"));
        }

        [Test]
        public void NoTwoCommandsShareAKey()
        {
            foreach (bool mac in new[] { true, false })
            {
                var keys = StudioKeymap.Commands
                    .Where(c => c.Menu != "App" && !(mac && c.Id is "file.exit" or "edit.preferences"))
                    .SelectMany(c => c.Gestures(mac).Select(g => (c.Id, Key: KeyText.Of(g, mac))))
                    .GroupBy(x => x.Key).Where(g => g.Count() > 1).Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Id))}").ToList();
                Assert.That(keys, Is.Empty, mac ? "on a Mac" : "elsewhere");
            }
        }

        [AvaloniaTest]
        public void TheMenusAreMadeFromTheKeymap()
        {
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            try
            {
                foreach (bool mac in new[] { true, false })
                {
                    var menus = window.MenuModel(mac).ToList();
                    // One keymap (ux-overhaul.md §4): Duplicate ⌘D, Delete ⌫, Select All ⌘A, Deselect All ⇧⌘A, Rename ⌘R, ⌘1–⌘5.
                    Assert.That(AppMenu.Find(menus, "Edit/Duplicate").Gesture, Is.EqualTo(KeyText.Parse("Cmd+D", mac)));
                    Assert.That(AppMenu.Find(menus, "Edit/Select All").Gesture, Is.EqualTo(KeyText.Parse("Cmd+A", mac)));
                    Assert.That(AppMenu.Find(menus, "Edit/Deselect All").Gesture, Is.EqualTo(KeyText.Parse("Shift+Cmd+A", mac)));
                    Assert.That(AppMenu.Find(menus, "Edit/Rename…").Gesture, Is.EqualTo(KeyText.Parse("Cmd+R", mac)));
                    var delete = AppMenu.Find(menus, mac ? "Edit/Delete (⌫)" : "Edit/Delete");
                    Assert.That(delete, Is.Not.Null);
                    Assert.That(delete.NativeKey, Is.False, "the menu bar never takes ⌫ from a text box or a view");
                    Assert.That(AppMenu.Find(menus, mac ? "Transport/Record (R)" : "Transport/Record").Gesture, Is.EqualTo(new KeyGesture(Key.R)));
                    Assert.That(AppMenu.Find(menus, mac ? "Transport/Loop (C)" : "Transport/Loop"), Is.Not.Null);
                    Assert.That(AppMenu.Find(menus, mac ? "Transport/Metronome (K)" : "Transport/Metronome"), Is.Not.Null);
                    Assert.That(AppMenu.Find(menus, mac ? "Transport/Go to Start (Home)" : "Transport/Go to Start"), Is.Not.Null);
                    Assert.That(AppMenu.Find(menus, "Help/Keyboard Shortcuts…").Gesture, Is.EqualTo(KeyText.Parse("Cmd+Slash", mac)));
                    Assert.That(AppMenu.Find(menus, "File/Revert to Saved"), Is.Not.Null);
                    // Every gesture a menu shows is one of the keymap's, written once.
                    var shown = menus.SelectMany(m => m.Items()).Where(e => !e.IsSeparator && e.Gesture != null).ToList();
                    var all = StudioKeymap.Commands.SelectMany(c => c.Gestures(mac)).ToList();
                    Assert.That(shown.Where(e => !all.Contains(e.Gesture)).Select(e => e.Text), Is.Empty);
                }
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaTest]
        public void TheShortcutsWindowListsEveryKeyAndSearches()
        {
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle();
            try
            {
                window.KeyPressQwerty(PhysicalKey.Slash, Cmd);
                Settle();
                var shortcuts = ShortcutsWindow.Current;
                Assert.That(shortcuts, Is.Not.Null, "⌘/ opens it");
                var rows = shortcuts.Shown;
                foreach (var c in StudioKeymap.Commands.Where(c => c.Menu is not "App" && c.Id is not "help.about" and not "file.exit" and not "edit.preferences" and not "window.front" && c.KeyLabel().Length > 0))
                    Assert.That(rows.Any(r => r.Keys == c.KeyLabel() && r.What.StartsWith(c.Text.TrimEnd('…'))), Is.True, $"{c.Id} is listed with its keys");
                Assert.That(rows.Select(r => r.Section).Distinct(), Is.SupersetOf(new[] { "File", "Edit", "Transport", "View", "Machines", "Session", "Piano roll", "Pattern" }));

                shortcuts.Search = "delete";
                Settle();
                Assert.That(shortcuts.Shown.Count, Is.GreaterThan(4));
                Assert.That(shortcuts.Shown.All(r => (r.Section + r.Keys + r.What).Contains("delete", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(shortcuts.Shown.Select(r => r.Section).Distinct(), Is.SupersetOf(new[] { "Edit", "Machines", "Session" }), "every view's delete");
                shortcuts.Search = "duplicate session";
                Settle();
                Assert.That(shortcuts.Shown.Select(r => r.Section), Is.All.EqualTo("Session"), "every word must match");
                shortcuts.Search = "zzzz";
                Settle();
                Assert.That(shortcuts.Shown, Is.Empty);
                Studio.Screenshot(shortcuts, "ux-shortcuts");
            }
            finally
            {
                ShortcutsWindow.Current?.Close();
                window.Close();
            }
        }

        [AvaloniaTest]
        public void TheTrackerKeepsItsOwnCommandKeys()
        {
            // The tracker's ⌘R randomizes, ⌘W rotates, ⌘1–⌘9 set the beat's subdivision (PatternEditorView.KeyRows): while it
            // has the focus, the window's Rename, Close and view keys leave them to it.
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle();
            try
            {
                var cmd = MainWindow.CommandModifiers;
                window.ShowView("Pattern");
                Settle();
                var page = window.Detail.Page;
                var inside = page.GetVisualDescendants().OfType<InputElement>().First(c => c.Focusable && c.IsEffectivelyVisible);
                inside.Focus();
                Settle();
                foreach (var key in new[] { Key.R, Key.W, Key.D1, Key.D5 })
                {
                    var gesture = new KeyGesture(key, cmd);
                    Assert.That(window.EditorKeepsKey(gesture), Is.True, $"{key}");
                    var binding = window.KeyBindings.FirstOrDefault(b => b.Gesture.Equals(gesture));
                    Assert.That(binding, Is.Not.Null, $"the window binds {key}");
                    Assert.That(binding!.Command!.CanExecute(null), Is.False, $"and leaves {key} to the tracker");
                }
                Assert.That(window.EditorKeepsKey(new KeyGesture(Key.D, cmd)), Is.False, "⌘D isn't the tracker's");
                window.ShowView("Machines");
                Settle();
                ((ReBuzz.Studio.Views.Machines.MachineView)window.Tabs.SelectedContent!).Canvas.Focus();     // the keyboard is where the user clicked
                Settle();
                Assert.That(window.EditorKeepsKey(new KeyGesture(Key.R, cmd)), Is.False, "elsewhere ⌘R renames");
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ the transport

        [AvaloniaTest]
        public void PlayLightsGreenRecordRedAndTheTooltipsNameTheKeys()
        {
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle();
            var transport = window.Transport;
            try
            {
                // The lit looks are the state tokens, on these buttons only (Fluent's accent blue was both, gallery mac-071/072).
                Assert.That(((ISolidColorBrush)transport.PlayButton.Resources["AccentButtonBackground"]!).Color, Is.EqualTo(StudioTokens.Play));
                Assert.That(((ISolidColorBrush)transport.RecordButton.Resources["ToggleButtonBackgroundChecked"]!).Color, Is.EqualTo(StudioTokens.Record));
                Assert.That(((ISolidColorBrush)transport.LoopButton.Resources["ToggleButtonBackgroundChecked"]!).Color, Is.EqualTo(StudioTokens.TextPrimary), "loop lit plainly, no hue");
                Assert.That(ToolTip.GetTip(transport.RecordButton) as string, Does.StartWith(KeyText.IsMac ? "Record (R or F7)" : "Record (R or F7)"));
                Assert.That(ToolTip.GetTip(transport.LoopButton) as string, Does.StartWith("Loop (C)"));
                Assert.That(ToolTip.GetTip(transport.MetronomeButton) as string, Does.StartWith("Metronome (K)"));
                Assert.That(ToolTip.GetTip(transport.StartButton) as string, Does.StartWith("Go to Start (Home)"));

                // Record lights whenever anything records: the Session View's session record too.
                Core.SessionLayout.Ensure();
                Core.SessionLayout.SetSessionRecord(true, 0);
                transport.Refresh();
                Assert.That(transport.RecordButton.IsChecked, Is.True, "session record lights the transport's ●");
                Core.SessionLayout.SetSessionRecord(false, 0);
                transport.Refresh();
                Assert.That(transport.RecordButton.IsChecked, Is.False);

                // Go to start, and the metronome (the session's click) from the transport.
                Core.SongCore.PlayPosition = 64;
                transport.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.That(Core.SongCore.PlayPosition, Is.EqualTo(Core.Looping ? Core.SongCore.LoopStart : 0));
                bool click = transport.MetronomeOn;
                transport.ToggleMetronome();
                Assert.That(transport.MetronomeOn, Is.EqualTo(!click));
                transport.ToggleMetronome();
                Assert.That(transport.MetronomeOn, Is.EqualTo(click));
            }
            finally
            {
                Core.SessionLayout?.SetSessionRecord(false, 0);
                window.Close();
            }
        }

        [AvaloniaTest]
        public void SingleTransportKeysActWhereTheViewDoesntTypeLetters()
        {
            var (window, view) = OpenEmpty();
            bool looping = Core.Looping;
            try
            {
                view.Canvas.Focus();
                view.Canvas.Select(Core.SongCore.Machines.Take(1));       // P3d: with nothing selected the canvas's letters search the browser
                Settle();
                window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
                Assert.That(Core.Looping, Is.EqualTo(!looping), "C: loop, in the Machine View");
                window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
                Assert.That(Core.Looping, Is.EqualTo(looping));
                window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
                Assert.That(Core.Recording, Is.True, "R: record");
                window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
                Assert.That(Core.Recording, Is.False);
                bool click = window.Transport.MetronomeOn;
                window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.None);
                Assert.That(window.Transport.MetronomeOn, Is.EqualTo(!click), "K: the metronome");
                window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.None);
                Core.SongCore.PlayPosition = 32;
                window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
                Assert.That(Core.SongCore.PlayPosition, Is.EqualTo(looping ? Core.SongCore.LoopStart : 0), "Home: go to start");

                // The arrangement types pattern letters: R isn't record there.
                window.ShowView("Sequence");
                Settle();
                window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
                Assert.That(Core.Recording, Is.False, "R in the arrangement is the arrangement's");
                // A text box keeps its letters (Song Notes' editor).
                window.ShowView("Song Notes");
                Settle();
                var notes = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.AcceptsReturn);
                notes.Focus();
                Settle();
                window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
                Assert.That(Core.Recording, Is.False, "typing an r in the notes doesn't record");
            }
            finally
            {
                Core.Recording = false;
                Core.Looping = looping;
                Close(window);
            }
        }

        // ------------------------------------------------------------------ Edit: one keymap in every view

        [AvaloniaTest]
        public void DuplicateDeleteDeselectAndRenameInTheMachineView()
        {
            var (window, view) = OpenEmpty();
            try
            {
                Core.SongCore.CreateMachine("Neo Poly", null, "Bass", null, null, null, -1, -0.3f, 0f);
                Settle();
                var bass = Machine("Bass");
                view.Canvas.Select(new[] { bass });
                view.Canvas.Focus();
                Settle();

                window.KeyPressQwerty(PhysicalKey.D, Cmd);
                Settle();
                var copies = Core.SongCore.Machines.Where(m => m.DLL.Name == "Neo Poly").ToList();
                Assert.That(copies.Count, Is.EqualTo(2), "⌘D duplicated it (it deselected)");
                var copy = copies.Single(m => m != bass);
                Assert.That(view.Canvas.Selection, Is.EquivalentTo(new[] { copy }), "the copy is selected");
                Assert.That(copy.Position.Item1, Is.EqualTo(bass.Position.Item1 + MachineEdits.DuplicateOffset).Within(1e-4));
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(Core.SongCore.Machines.Count(m => m.DLL.Name == "Neo Poly"), Is.EqualTo(1), "one undo step");

                // Edit ▸ Duplicate from the menu does the same; ⇧⌘A deselects; ⌘R renames.
                view.Canvas.Select(new[] { bass });
                AppMenu.Find(window.MenuModel(mac: false), "Edit/Duplicate").Run();
                Settle();
                Assert.That(Core.SongCore.Machines.Count(m => m.DLL.Name == "Neo Poly"), Is.EqualTo(2));
                window.KeyPressQwerty(PhysicalKey.A, Cmd | RawInputModifiers.Shift);
                Assert.That(view.Canvas.Selection, Is.Empty, "⇧⌘A");
                view.Canvas.Select(new[] { bass });
                Dialogs.TextResponder = (title, current) => "Low Bass";
                window.KeyPressQwerty(PhysicalKey.R, Cmd);
                Settle();
                Assert.That(bass.Name, Is.EqualTo("Low Bass"), "⌘R renames the selected machine");

                // ⌫ deletes on every platform (it was Mac only).
                view.Canvas.Select(new[] { bass });
                view.Canvas.Focus();
                window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
                Settle();
                Assert.That(Machine("Low Bass"), Is.Null, "⌫ deleted it");

                // S solos, B bypasses.
                var poly = Core.SongCore.Machines.First(m => m.DLL.Name == "Neo Poly");
                view.Canvas.Select(new[] { poly });
                view.Canvas.Focus();
                window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.None);
                Assert.That(poly.IsSoloed, Is.True, "S solos a generator");
                window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.None);
                Assert.That(poly.IsSoloed, Is.False);
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void BackspaceInTheSessionViewRemovesTheClipAndShiftDeletesItsPattern()
        {
            using var rig = new SessionRig("Bass");
            var line = rig.Pattern(rig.Machine("Bass"), "Line", SessionRig.Bar, (0, 48));
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetSlot(0, 0, line);
            layout.SetSlot(0, 1, line);
            var view = rig.Show();
            rig.Key(PhysicalKey.ArrowDown);
            Assert.That(view.Selected, Is.EqualTo((0, 0)));
            rig.Key(PhysicalKey.Backspace);
            Assert.That(layout.PatternAt(0, 0), Is.Null, "⌫ removes the clip from its slot (it stopped the track)");
            Assert.That(rig.Machine("Bass").Patterns, Does.Contain(line), "its pattern stays");

            rig.Key(PhysicalKey.ArrowDown);
            Assert.That(view.Selected, Is.EqualTo((0, 1)));
            Dialogs.Responder = (m, c, b) => UserAnswer.Yes;          // it asks when the arrangement uses the pattern
            try
            {
                rig.Key(PhysicalKey.Backspace, RawInputModifiers.Shift);
                rig.Settle();
                Assert.That(rig.Machine("Bass").Patterns, Does.Not.Contain(line), "⇧⌫ deletes the pattern");
            }
            finally
            {
                Dialogs.Responder = null;
            }
        }

        [AvaloniaTest]
        public void TheSessionViewTakesTheEditMenusDuplicateDeleteAndRename()
        {
            using var rig = new SessionRig("Bass");
            var line = rig.Pattern(rig.Machine("Bass"), "Line", SessionRig.Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetSlot(0, 0, line);
            var view = rig.Show();
            rig.Key(PhysicalKey.ArrowDown);
            ReBuzz.Studio.Views.IStudioEditTarget target = view;
            Assert.That(target.Duplicate(), Is.True);
            Assert.That(layout.PatternAt(0, 1)?.Name, Is.EqualTo("Line 2"));
            Assert.That(target.Delete(), Is.True);
            Assert.That(layout.PatternAt(0, 1), Is.Null, "Delete removed the selected (copied) clip");
            Assert.That(target.Delete(), Is.False, "an empty slot: nothing to delete");
        }

        // ------------------------------------------------------------------ File, Window

        [AvaloniaTest]
        public void CloseClosesTheWindowInFrontOrTheSongAndRevertAsksFirst()
        {
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle();
            string path = TestTemp.Path($"ux-revert-{Environment.ProcessId}.bmxml");
            try
            {
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
                Core.SongCore.CreateMachine("Neo Poly", null, "Kept", null, null, null, -1, 0f, 0f);
                SongFiles.Picker = _ => path;
                Assert.That(SongFiles.SaveAs().Result, Is.True, "saved");
                SongFiles.Picker = null;
                Settle();
                Core.SongCore.CreateMachine("Neo Poly", null, "Unsaved", null, null, null, -1, 0.3f, 0f);
                Settle();
                Assert.That(Core.Modified, Is.True);

                // Revert: asks, Cancel is the default; Revert opens the saved song.
                string asked = null;
                Dialogs.Responder = (m, c, b) => { asked = m; return UserAnswer.No; };
                AppMenu.Find(window.MenuModel(mac: false), "File/Revert to Saved").Run();
                Settle();
                Assert.That(asked, Does.Contain("Revert to the saved"));
                Assert.That(Machine("Unsaved"), Is.Not.Null, "Cancel keeps the changes");
                Dialogs.Responder = (m, c, b) => UserAnswer.Yes;
                AppMenu.Find(window.MenuModel(mac: false), "File/Revert to Saved").Run();
                Settle();
                Assert.That((Machine("Kept") != null, Machine("Unsaved") == null), Is.EqualTo((true, true)), "the saved song again");

                // The Window menu lists the open windows; Close closes the one in front.
                var prefs = ReBuzz.Studio.Shell.Preferences.PreferencesWindow.Open(Studio.Host);
                Settle();
                Assert.That(AppMenu.Find(window.MenuModel(mac: false), "Window").Items().Select(e => e.Text), Does.Contain(prefs.Title));
                prefs.Activate();
                Settle();
                AppMenu.Find(window.MenuModel(mac: false), "File/Close Window").Run();
                Settle();
                Assert.That(prefs.IsVisible, Is.False, "Close closed Preferences, the window in front");
                window.Activate();
                Settle();
                Assert.That(AppMenu.Find(window.MenuModel(mac: false), "File/Close Song"), Is.Not.Null, "on the main window it closes the song");
                AppMenu.Find(window.MenuModel(mac: false), "File/Close Song").Run();
                Settle();
                Assert.That(Machine("Kept"), Is.Null, "an empty song");
            }
            finally
            {
                Dialogs.Responder = null;
                SongFiles.Picker = null;
                ReBuzz.Studio.Shell.Preferences.PreferencesWindow.Current?.Close();
                Core.Modified = false;
                window.Close();
                File.Delete(path);
            }
        }

        // ------------------------------------------------------------------ problems in plain words, audio health

        [AvaloniaTest]
        public void TheProblemBarSaysWhatsWrongWithAButtonThatFixesIt()
        {
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle();
            bool guard = ReBuzz.Audio.MachineGuard.Enabled;
            try
            {
                window.RefreshNow();
                Assert.That(window.Problems.IsVisible, Is.False, "nothing wrong, no bar");
                ReBuzz.Audio.MachineGuard.Enabled = false;
                window.RefreshNow();
                Settle();
                Assert.That(window.Problems.Text, Does.StartWith("The machine guard is off"));
                var turnOn = window.Problems.Buttons.Single();
                Assert.That(turnOn.Content, Is.EqualTo("Turn On"));
                turnOn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.That(ReBuzz.Audio.MachineGuard.Enabled, Is.True, "the button fixed it");
                window.RefreshNow();
                Assert.That(window.Problems.IsVisible, Is.False);

                // A song with machines this computer can't run: named, kept, a button to find them.
                var problems = ProblemBar.Find(Core, _ => { }, _ => { }, _ => { }, _ => { });
                Assert.That(problems, Is.Empty);
                Studio.Screenshot(window, "ux-problem-bar-none");
            }
            finally
            {
                ReBuzz.Audio.MachineGuard.Enabled = guard;
                window.Close();
            }
        }

        [AvaloniaTest]
        public void AHiddenProblemComesBackOnlyWhenItChanges()
        {
            var bar = new ProblemBar();
            var a = new StudioProblem("a", "A is wrong.", new (string, Action)[] { ("Fix", () => { }) }, true);
            var b = new StudioProblem("b", "B is wrong.", Array.Empty<(string, Action)>(), false);
            bar.Show(new[] { a, b });
            Assert.That((bar.IsVisible, bar.Shown?.Key), Is.EqualTo((true, "a")));
            var dismiss = (Button)((DockPanel)bar.Child!).Children.OfType<Button>().First(x => x.Content as string == "×");
            dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(bar.Shown?.Key, Is.EqualTo("b"), "× hides A; B shows");
            bar.Show(new[] { a, b });
            Assert.That(bar.Shown?.Key, Is.EqualTo("b"), "A stays hidden while it lasts");
            bar.Show(new[] { b });
            bar.Show(new[] { a, b });
            Assert.That(bar.Shown?.Key, Is.EqualTo("a"), "gone and back: shown again");
        }

        [AvaloniaTest]
        public void TheAudioBadgeIsRedWithoutOutputAndClicksToTheAudioSettings()
        {
            bool opened = false;
            var badge = new AudioHealthBadge(() => Core, () => opened = true);
            var window = new Window { Content = badge, Width = 300, Height = 60 };
            window.Show();
            try
            {
                badge.Refresh();
                if (Core.AudioEngine?.SelectedOutDevice == null)
                {
                    Assert.That(badge.Shown, Is.EqualTo(AudioHealthBadge.State.NoOutput));
                    Assert.That(badge.Text, Is.EqualTo("No audio output"));
                }
                var centre = badge.TranslatePoint(new Point(badge.Bounds.Width / 2, badge.Bounds.Height / 2), window)!.Value;
                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                Assert.That(opened, Is.True, "a click opens Preferences ▸ Audio");
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ one time unit

        [Test]
        public void TimeTextIsBarsAndBeatsFromOne()
        {
            Assert.That(TimeText.Position(0, 4, 4), Is.EqualTo("1.1.1"));
            Assert.That(TimeText.Position(128, 4, 4), Is.EqualTo("9.1.1"));
            Assert.That(TimeText.Position(31, 4, 4), Is.EqualTo("2.4.4"));
            Assert.That(TimeText.BarBeat(20, 4, 4), Is.EqualTo("2.2"));
            Assert.That(TimeText.Length(32, 4, 4), Is.EqualTo("2 bars"));
            Assert.That(TimeText.Length(24, 4, 4), Is.EqualTo("1 bar 2 beats"));
            Assert.That(TimeText.Length(4, 4, 4), Is.EqualTo("1 beat"));
            Assert.That(TimeText.Length(5, 4, 4), Is.EqualTo("1 beat 1 tick"));
            Assert.That(TimeText.Length(0, 4, 4), Is.EqualTo("0 ticks"));
            Assert.That(TimeText.TryParsePosition("2.1.1", 4, 4, out int t) && t == 16, Is.True);
            Assert.That(TimeText.TryParsePosition("1.3", 4, 4, out t) && t == 8, Is.True);
            Assert.That(TimeText.TryParsePosition("3", 4, 4, out t) && t == 32, Is.True);
            Assert.That(TimeText.TryParsePosition("16t", 4, 4, out t) && t == 16, Is.True, "ticks with a t");
            Assert.That(TimeText.TryParsePosition("0.1.1", 4, 4, out _), Is.False, "bars count from 1");
            Assert.That(TimeText.TryParsePosition("1.5", 4, 4, out _), Is.False, "no fifth beat in 4/4");
            Assert.That(TimeText.TryParsePosition("x", 4, 4, out _), Is.False);
            Assert.That(TimeText.Position(TimeText.TryParsePosition("7.2.3", 4, 3, out t) ? t : -1, 4, 3), Is.EqualTo("7.2.3"), "round trip in 3/4");
        }

        // ------------------------------------------------------------------ dialogs

        [AvaloniaTest]
        public void TheDefaultAnswerIsAccentedAndAnAnswerThatCantBeUndoneIsRed()
        {
            var window = Dialogs.Build("Turn the safety limiter off?", "Safety limiter", UserButtons.YesNo, DialogChoices.TurnOff, mac: true, _ => { });
            window.Show();
            try
            {
                var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
                var keep = buttons.Single(b => b.Content as string == "Keep On");
                var off = buttons.Single(b => b.Content as string == "Turn Off");
                Assert.That((keep.IsDefault, keep.Classes.Contains("accent")), Is.EqualTo((true, true)), "Return keeps it on, and shows it");
                Assert.That(off.Classes.Contains("accent"), Is.False);
                Assert.That((off.Foreground as ISolidColorBrush)?.Color, Is.EqualTo(StudioTokens.Record));
                Assert.That(buttons.IndexOf(keep), Is.GreaterThan(buttons.IndexOf(off)), "the default rightmost on a Mac");
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ the pattern editor's block selection

        [AvaloniaTest]
        public void ThePatternEditorsBlockSelectionIsTheAccent()
        {
            // UX review mac-052: the block selection was a 1.06:1 tint. The editor's view is in the editor machine's
            // assembly (Gear), so its theme is read by name.
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("WDE.ModernPatternEditor.AvaloniaView.PatternTheme", false)).FirstOrDefault(t => t != null);
            if (type == null) Assert.Ignore("the pattern editor's view isn't loaded in this run");
            var fill = (ISolidColorBrush)type.GetField("SelectionFill")!.GetValue(null)!;
            Assert.That(fill.Color, Is.EqualTo(Color.FromArgb(0x59, StudioTokens.Accent.R, StudioTokens.Accent.G, StudioTokens.Accent.B)));
            var stroke = (IPen)type.GetField("SelectionStroke")!.GetValue(null)!;
            Assert.That(((ISolidColorBrush)stroke.Brush!).Color, Is.EqualTo(StudioTokens.Accent));
            // Over the editor's background (#333) at 35 %, against it: visible.
            var bg = Color.FromRgb(0x33, 0x33, 0x33);
            byte Mix(byte f, byte b) => (byte)Math.Round(f * 0.35 + b * 0.65);
            var over = Color.FromRgb(Mix(StudioTokens.Accent.R, bg.R), Mix(StudioTokens.Accent.G, bg.G), Mix(StudioTokens.Accent.B, bg.B));
            Assert.That(StudioTokens.Contrast(StudioTokens.Accent, bg), Is.GreaterThanOrEqualTo(3.0), "the outline, 3:1");
            Assert.That(StudioTokens.Contrast(over, bg), Is.GreaterThan(1.4), "the fill");
        }

        // ------------------------------------------------------------------ note names (§3.5)

        [Test]
        public void NotesAreNamedOneWayInEachNaming()
        {
            // The coordinator's three namings: Buzz (C-5 = 60, three characters), C3 = 60 (the default) and scientific
            // (C4 = 60). The tracker keeps three characters in each; BuzzNote's own names (what Parse reads) don't change.
            const int C5 = (5 << 4) + 1, Cs5 = (5 << 4) + 2, C0 = 1, Cs0 = 2, B9 = (9 << 4) + 12;
            Assert.That(BuzzGUI.Common.NoteNames.Default, Is.EqualTo(BuzzGUI.Common.NoteNaming.C3));
            var cases = new[]
            {
                (BuzzGUI.Common.NoteNaming.Buzz, "C-5", "C#5", "C-0", "C#0", "B-9", "C-5", "C#5", "C-0", "G-10"),
                (BuzzGUI.Common.NoteNaming.C3, "C-3", "C#3", "C--2", "C#-2", "B-7", "C3", "C#3", "C-2", "G8"),
                (BuzzGUI.Common.NoteNaming.Scientific, "C-4", "C#4", "C--1", "C#-1", "B-8", "C4", "C#4", "C-1", "G9"),
            };
            foreach (var (n, c5, cs5, c0, cs0, b9, m60, m61, m0, m127) in cases)
            {
                Assert.That(BuzzGUI.Common.NoteNames.TryTracker(C5, n), Is.EqualTo(c5), $"{n}: Buzz's C-5 in the tracker");
                Assert.That(BuzzGUI.Common.NoteNames.TryTracker(Cs5, n), Is.EqualTo(cs5));
                Assert.That(BuzzGUI.Common.NoteNames.TryTracker(C0, n), Is.EqualTo(c0), $"{n}: the lowest");
                Assert.That(BuzzGUI.Common.NoteNames.TryTracker(Cs0, n), Is.EqualTo(cs0));
                Assert.That(BuzzGUI.Common.NoteNames.TryTracker(B9, n), Is.EqualTo(b9), $"{n}: the highest");
                Assert.That(BuzzGUI.Common.NoteNames.TryTracker(BuzzGUI.Common.BuzzNote.Off, n), Is.EqualTo("off"));
                Assert.That(BuzzGUI.Common.NoteNames.TryTracker(0x0D, n), Is.Null, "not a note");
                Assert.That(BuzzGUI.Common.NoteNames.Name(60, n), Is.EqualTo(m60), $"{n}: MIDI 60");
                Assert.That(BuzzGUI.Common.NoteNames.Name(61, n), Is.EqualTo(m61));
                Assert.That(BuzzGUI.Common.NoteNames.Name(0, n), Is.EqualTo(m0));
                Assert.That(BuzzGUI.Common.NoteNames.Name(127, n), Is.EqualTo(m127));
                Assert.That(BuzzGUI.Common.NoteNames.TryTracker(C5, n)!.Length, Is.EqualTo(3), "three characters");
            }
            Assert.That(BuzzGUI.Common.BuzzNote.TryToString(C5), Is.EqualTo("C-5"), "BuzzNote's names stay Buzz's");
            Assert.That(BuzzGUI.Common.BuzzNote.Parse("C-5"), Is.EqualTo(C5));
            Assert.That(BuzzGUI.Common.NoteNames.Octave(4, BuzzGUI.Common.NoteNaming.C3), Is.EqualTo(2), "the base octave 4 is 2 when C3 is 60");
            Assert.That(Enum.GetValues<BuzzGUI.Common.NoteNaming>().Select(BuzzGUI.Common.NoteNames.Label),
                Is.EqualTo(new[] { "Buzz: C-5 = MIDI 60", "C3 = MIDI 60", "Scientific: C4 = MIDI 60" }));
            // Drawing a grid of notes allocates nothing: the names are made once.
            Assert.That(ReferenceEquals(BuzzGUI.Common.NoteNames.TryTracker(C5, BuzzGUI.Common.NoteNaming.C3), BuzzGUI.Common.NoteNames.TryTracker(C5, BuzzGUI.Common.NoteNaming.C3)), Is.True);
        }
    }
}
