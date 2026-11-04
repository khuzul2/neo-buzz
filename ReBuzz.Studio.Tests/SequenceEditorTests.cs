// Fork (docs/specs/studio-mvp.md S4): the sequence editor, headless, in the main window's
// Sequence tab: tracks and patterns arranged with the mouse and keys, moved, resized, deleted,
// copied and pasted, loop markers, playback and its cursor, save and reopen, undo. Each step is
// checked against the engine's sequences, and the stages are saved as screenshots.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Sequence;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class SequenceEditorTests
    {
        const int TB = PatternEvent.TimeBase;

        static ReBuzzCore Core => Studio.Host.Core;
        static ISong Song => Core.Song;

        /// <summary>Control, or Command on a Mac.</summary>
        static RawInputModifiers Cmd => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        static TimeSpan menuDelay;

        // Submenus open on hover after a wall-clock delay; the tests click, so it is put off (as the machine view's tests do).
        [SetUp]
        public void PutOffMenuHoverDelays()
        {
            menuDelay = Avalonia.Controls.Platform.DefaultMenuInteractionHandler.MenuShowDelay;
            Avalonia.Controls.Platform.DefaultMenuInteractionHandler.MenuShowDelay = TimeSpan.FromHours(1);
        }

        [TearDown]
        public void RestoreMenuHoverDelays() => Avalonia.Controls.Platform.DefaultMenuInteractionHandler.MenuShowDelay = menuDelay;

        // ------------------------------------------------------------------ the rig

        sealed class Rig : IDisposable
        {
            public MainWindow Window { get; }
            public SequenceView View { get; }
            public SequenceCanvas Canvas => View.Canvas;

            public Rig()
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;     // never keep unsaved changes here
                NewSong();
                Window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                ToolTip.SetServiceEnabled(Window, false);
                Window.Show();
                var tab = Window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Sequence");
                tab.IsSelected = true;
                Settle();
                View = (SequenceView)tab.Content!;
                Canvas.AutoSelectPattern = true;
                Settle();
            }

            public static void NewSong()
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
            }

            /// <summary>A Neo Poly wired to the master, with patterns (name, length, notes at ticks).</summary>
            public static MachineCore Machine(string name, params (string Name, int Length, int[] Notes)[] patterns)
            {
                var song = Core.SongCore;
                song.CreateMachine("Neo Poly", null, name, null, null, null, -1, -0.3f + 0.2f * song.MachinesList.Count, 0.3f);
                var machine = song.MachinesList.Last(m => m.Name == name);
                var master = song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
                song.ConnectMachines(machine, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                foreach (var (pname, length, notes) in patterns)
                {
                    machine.CreatePattern(pname, length);
                    var p = machine.PatternsList.Single(x => x.Name == pname);
                    if (notes.Length > 0)
                        Core.SessionLayout.EditNotes(p, null, notes.Select((t, i) => new ClipNote(t * TB, TB * 2, 60 + 3 * (i % 5), 100)).ToArray());
                }
                return machine;
            }

            public static IPattern Pattern(string machine, string name) => Core.Song.Machines.First(m => m.Name == machine).Patterns.First(p => p.Name == name);

            /// <summary>A cell of the timeline in window pixels: tick, track, and where across it (pixels from its start).</summary>
            public Point At(int time, int row, double dx = 6, double fy = 0.6)
            {
                Settle();
                var geo = Canvas.Geometry;
                var p = Canvas.TranslatePoint(new Point(geo.XOf(time) + dx, geo.YOf(row) + fy * geo.TrackHeight), Window);
                Assert.That(p, Is.Not.Null);
                return p!.Value;
            }

            /// <summary>A point of the canvas (its own pixels) in window pixels.</summary>
            public Point OfCanvas(Point p) => Canvas.TranslatePoint(p, Window)!.Value;

            public void Click(Point p, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.MouseMove(p, mods);
                Window.MouseDown(p, button, mods);
                Window.MouseUp(p, button, mods);
                Settle();
            }

            public void DoubleClick(Point p)
            {
                MachineViewTests.DoubleClick(Window, p);
                Settle();
            }

            public void Drag(Point from, Point to, RawInputModifiers mods = RawInputModifiers.None, string screenshotOnTheWay = null)
            {
                Window.MouseMove(from, mods);
                Window.MouseDown(from, MouseButton.Left, mods);
                for (int i = 1; i <= 4; i++)
                    Window.MouseMove(from + (to - from) * (i / 4.0), mods | RawInputModifiers.LeftMouseButton);
                if (screenshotOnTheWay != null) Screenshot(screenshotOnTheWay);
                Window.MouseUp(to, MouseButton.Left, mods);
                Settle();
            }

            public void Key(PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.KeyPressQwerty(key, mods);
                Window.KeyReleaseQwerty(key, mods);
                Settle();
            }

            public void Type(string text)
            {
                foreach (char c in text) Window.KeyTextInput(c.ToString());
                Settle();
            }

            public void Undo() => Key(PhysicalKey.Z, Cmd);

            public void Redo()
            {
                if (OperatingSystem.IsMacOS()) Key(PhysicalKey.Z, Cmd | RawInputModifiers.Shift);
                else Key(PhysicalKey.Y, Cmd);
            }

            /// <summary>Clicks through the menu the view opened last (a right click or the + button).</summary>
            public void Menu(params string[] path)
            {
                var menu = View.OpenMenu;
                Assert.That(menu, Is.Not.Null, "a menu opened");
                Assert.That(menu!.IsOpen, Is.True);
                ItemsControl level = menu;
                for (int i = 0; i < path.Length; i++)
                {
                    var item = level.Items.OfType<MenuItem>().FirstOrDefault(x => x.Header as string == path[i]);
                    Assert.That(item, Is.Not.Null, $"the menu has {path[i]} (it has {string.Join(", ", level.Items.OfType<MenuItem>().Select(x => x.Header))})");
                    var top = TopLevel.GetTopLevel(item)!;
                    top.UpdateLayout();
                    Assert.That(item!.IsEffectivelyVisible && item.Bounds.Width > 0, Is.True, $"{path[i]} is showing");
                    var centre = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), top)!.Value;
                    top.MouseMove(centre);
                    top.MouseDown(centre, MouseButton.Left);
                    top.MouseUp(centre, MouseButton.Left);
                    Settle();
                    level = item;
                }
                Assert.That(menu.IsOpen, Is.False, "the menu closes once an item is chosen");
            }

            public string Screenshot(string name)
            {
                Settle();
                return Studio.Screenshot(Window, name);
            }

            public void Dispose()
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                Window.Close();
                Settle();
                Dialogs.Responder = null;
                Dialogs.TextResponder = null;
                SongFiles.Picker = null;
                NewSong();
            }
        }

        static void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>A track's events as text: "0:Hook 16:Riff 32:<break>".</summary>
        static string Events(int row) => string.Join(" ", Song.Sequences[row].Events.Select(e =>
            $"{e.Key}:{(e.Value.Type == SequenceEventType.PlayPattern ? e.Value.Pattern.Name : "<" + e.Value.Type.ToString().ToLowerInvariant() + ">")}"));

        static string Tracks => string.Join(", ", Song.Sequences.Select(s => s.Machine.Name));

        // ------------------------------------------------------------------ arranging

        [AvaloniaTest]
        public void ArrangeOnTwoTracksWithMouseAndKeysThenMoveResizeDeleteCopyPasteAndUndo()
        {
            using var rig = new Rig();
            Rig.Machine("Lead", ("Hook", 16, new[] { 0, 4, 8, 12 }), ("Riff", 32, new[] { 0, 2, 6, 10, 16, 20, 26 }));
            Rig.Machine("Bass", ("Line", 32, new[] { 0, 8, 16, 24 }));
            var canvas = rig.Canvas;
            Assert.That(Song.Sequences, Is.Empty, "a new song has no tracks");

            // Tracks: the + button's menu, a machine each.
            rig.Click(rig.View.AddTrackButton.TranslatePoint(new Point(10, 10), rig.Window)!.Value);
            rig.Menu("Lead");
            rig.Click(rig.View.AddTrackButton.TranslatePoint(new Point(10, 10), rig.Window)!.Value);
            rig.Menu("Bass");
            Assert.That(Tracks, Is.EqualTo("Lead, Bass"));
            rig.Screenshot("sequence-1-tracks");

            // Keys: on Lead at the start, a pattern's key places it and the cursor moves past it.
            rig.Click(rig.At(0, 0));
            Assert.That((canvas.CursorTime, canvas.CursorRow), Is.EqualTo((0, 0)));
            Assert.That(rig.View.Patterns.Select(p => $"{p.Char}. {p.Pattern.Name}"), Is.EqualTo(new[] { "0. Hook", "1. Riff" }), "the pattern list");
            rig.Type("0");
            Assert.That(canvas.CursorTime, Is.EqualTo(16), "past the pattern");
            rig.Type("1");
            Assert.That(Events(0), Is.EqualTo("0:Hook 16:Riff"));
            Assert.That(canvas.CursorTime, Is.EqualTo(48));

            // The mouse: Bass's second bar, then the pattern list's Line with a double click.
            rig.Click(rig.At(16, 1));
            Assert.That((canvas.CursorTime, canvas.CursorRow), Is.EqualTo((16, 1)));
            var list = rig.View.PatternList;
            var item = list.ContainerFromIndex(0)!;
            var itemAt = item.TranslatePoint(new Point(20, item.Bounds.Height / 2), rig.Window)!.Value;
            rig.DoubleClick(itemAt);
            Assert.That(Events(1), Is.EqualTo("16:Line"));
            Assert.That(canvas.CursorTime, Is.EqualTo(48), "past Line");

            // A double click on an empty cell makes a new pattern there.
            rig.DoubleClick(rig.At(64, 1));
            var made = Song.Sequences[1].Machine.Patterns.Single(p => p.Name == "00");
            Assert.That(made.Length, Is.EqualTo(16), "the default pattern length");
            Assert.That(Events(1), Is.EqualTo("16:Line 64:00"));
            rig.Screenshot("sequence-2-arranged");

            // Move: drag Hook from the start to bar 4; the ghost shows on the way.
            rig.Drag(rig.At(0, 0, dx: 12), rig.At(64, 0, dx: 12), screenshotOnTheWay: "sequence-3-moving");
            Assert.That(Events(0), Is.EqualTo("16:Riff 64:Hook"));
            Assert.That((canvas.CursorTime, canvas.CursorRow), Is.EqualTo((64, 0)), "the cursor follows the block");

            // Alt-drag copies: Riff again at bar 6.
            rig.Drag(rig.At(16, 0, dx: 12), rig.At(96, 0, dx: 12), RawInputModifiers.Alt);
            Assert.That(Events(0), Is.EqualTo("16:Riff 64:Hook 96:Riff"));
            rig.Screenshot("sequence-3-moved");

            // Resize: Line's right edge to bar 4 (its pattern gets 48 ticks); Hook's left edge back a bar (32 ticks, from 48).
            var geo = canvas.Geometry;
            rig.Drag(rig.At(48, 1, dx: -2), rig.At(62, 1, dx: 0));
            Assert.That(Rig.Pattern("Bass", "Line").Length, Is.EqualTo(48));
            Assert.That(Events(1), Is.EqualTo("16:Line 64:00"));
            rig.Drag(rig.At(64, 0, dx: 2), rig.At(48, 0, dx: 2));
            Assert.That(Events(0), Is.EqualTo("16:Riff 48:Hook 96:Riff"));
            Assert.That(Rig.Pattern("Lead", "Hook").Length, Is.EqualTo(32), "the end stays where it was");
            rig.Screenshot("sequence-4-resized");

            // Delete: a click selects the block, Delete clears it.
            rig.Click(rig.At(64, 1, dx: 12));
            Assert.That(canvas.Selection.NotEmpty, Is.True, "a click on a block selects it");
            rig.Key(PhysicalKey.Delete);
            Assert.That(Events(1), Is.EqualTo("16:Line"));
            rig.Screenshot("sequence-5-deleted");

            // Copy: a rubber band from an empty cell (Bass's first bar) over both tracks to bar 3; paste at bar 8.
            rig.Drag(rig.At(0, 1, dx: 4), rig.At(32, 0, dx: 20), screenshotOnTheWay: "sequence-6-selecting");
            var rect = canvas.Selection.Rect(canvas.TimeSignatures);
            Assert.That(rect, Is.EqualTo(new SequenceRect(0, 0, 48, 2)), "three bars of both tracks");
            rig.Key(PhysicalKey.C, Cmd);
            rig.Click(rig.At(128, 0));
            Assert.That(canvas.Selection.NotEmpty, Is.False, "a click on empty space drops the selection");
            rig.Key(PhysicalKey.V, Cmd);
            Assert.That(Events(0), Is.EqualTo("16:Riff 48:Hook 96:Riff 144:Riff"));
            Assert.That(Events(1), Is.EqualTo("16:Line 144:Line"));
            rig.Screenshot("sequence-6-pasted");

            // Cut the block under the cursor, paste it further on.
            rig.Click(rig.At(144, 1, dx: 12));
            rig.Key(PhysicalKey.X, Cmd);
            Assert.That(Events(1), Is.EqualTo("16:Line"));
            rig.Click(rig.At(192, 1));
            rig.Key(PhysicalKey.V, Cmd);
            Assert.That(Events(1), Is.EqualTo("16:Line 192:Line"), "the cut pastes elsewhere");

            // Undo, step by step, on the song's history (the shell's Ctrl/Command+Z).
            rig.Undo();
            Assert.That(Events(1), Is.EqualTo("16:Line"), "undo the paste");
            rig.Undo();
            Assert.That(Events(1), Is.EqualTo("16:Line 144:Line"), "undo the cut");
            rig.Undo();
            Assert.That(Events(0), Is.EqualTo("16:Riff 48:Hook 96:Riff"));
            Assert.That(Events(1), Is.EqualTo("16:Line"), "undo the paste of two tracks");
            rig.Undo();
            Assert.That(Events(1), Is.EqualTo("16:Line 64:00"), "undo the delete");
            rig.Undo();
            Assert.That(Events(0), Is.EqualTo("16:Riff 64:Hook 96:Riff"), "undo the left resize");
            Assert.That(Rig.Pattern("Lead", "Hook").Length, Is.EqualTo(16));
            rig.Undo();
            Assert.That(Rig.Pattern("Bass", "Line").Length, Is.EqualTo(32), "undo the right resize");
            rig.Undo();
            Assert.That(Events(0), Is.EqualTo("16:Riff 64:Hook"), "undo the copy-drag");
            rig.Undo();
            Assert.That(Events(0), Is.EqualTo("0:Hook 16:Riff"), "undo the move");
            rig.Redo();
            Assert.That(Events(0), Is.EqualTo("16:Riff 64:Hook"), "redo the move");
            rig.Screenshot("sequence-7-undone");
        }

        [AvaloniaTest]
        public void KeysPlaceBreaksMutesAndClearAndMoveTracks()
        {
            using var rig = new Rig();
            Rig.Machine("Lead", ("Hook", 16, new[] { 0, 8 }));
            Rig.Machine("Bass", ("Line", 16, new[] { 0 }));
            var canvas = rig.Canvas;
            Song.AddSequence(Song.Machines.First(m => m.Name == "Lead"), 0);
            Song.AddSequence(Song.Machines.First(m => m.Name == "Bass"), 1);
            Settle();

            rig.Click(rig.At(0, 0));
            rig.Type("0,0-0_");
            Assert.That(Events(0), Is.EqualTo("0:Hook 16:<break> 32:Hook 48:<mute> 64:Hook 80:<thru>"));
            rig.Key(PhysicalKey.Comma, RawInputModifiers.Shift);
            Assert.That(Events(0), Does.EndWith("96:<off>"), "Shift+, stops the track");
            rig.Click(rig.At(16, 0));
            rig.Type(".");
            Assert.That(Events(0), Is.EqualTo("0:Hook 32:Hook 48:<mute> 64:Hook 80:<thru> 96:<off>"), ". clears");
            rig.Screenshot("sequence-8-keys");

            // Insert and Delete move what follows by a bar (Buzz).
            rig.Key(PhysicalKey.Insert);
            Assert.That(Events(0), Is.EqualTo("0:Hook 48:Hook 64:<mute> 80:Hook 96:<thru> 112:<off>"));
            rig.Key(PhysicalKey.Delete);
            Assert.That(Events(0), Is.EqualTo("0:Hook 32:Hook 48:<mute> 64:Hook 80:<thru> 96:<off>"));

            // Arrows with Shift select; Escape drops it.
            rig.Key(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
            rig.Key(PhysicalKey.ArrowDown, RawInputModifiers.Shift);
            Assert.That(canvas.Selection.Rect(canvas.TimeSignatures), Is.EqualTo(new SequenceRect(32, 0, 32, 2)));
            rig.Key(PhysicalKey.Escape);
            Assert.That(canvas.Selection.NotEmpty, Is.False);

            // Ctrl/Command+Down moves the track down; undo puts it back.
            rig.Click(rig.At(0, 0));
            rig.Key(PhysicalKey.ArrowDown, Cmd);
            Assert.That(Tracks, Is.EqualTo("Bass, Lead"));
            Assert.That(canvas.CursorRow, Is.EqualTo(1), "the cursor stays on the track");
            rig.Undo();
            Assert.That(Tracks, Is.EqualTo("Lead, Bass"));

            // Mute, solo and disable in the header.
            rig.Click(rig.OfCanvas(canvas.HeaderButton(1, 0).Center));
            rig.Click(rig.OfCanvas(canvas.HeaderButton(0, 1).Center));
            rig.Click(rig.OfCanvas(canvas.HeaderButton(0, 2).Center));
            Assert.That(Song.Sequences[1].Machine.IsMuted, Is.True, "M mutes Bass");
            Assert.That(Song.Sequences[0].Machine.IsSoloed, Is.True, "S solos Lead");
            Assert.That(Song.Sequences[0].IsDisabled, Is.True, "D disables Lead's track");
            rig.Screenshot("sequence-9-mute-solo");
            Song.Sequences[0].Machine.IsSoloed = false;
            Song.Sequences[1].Machine.IsMuted = false;

            // The menu: Quick Create Patterns (two new patterns from the cursor), Rename Machine... (asked through the engine).
            rig.Click(rig.At(128, 1), MouseButton.Right);
            rig.Menu("Quick Create Patterns", "2");
            Assert.That(Events(1), Is.EqualTo("128:00 144:01"));
            Dialogs.TextResponder = (title, current) => current + " 2";
            rig.Click(rig.At(128, 1), MouseButton.Right);
            rig.Menu("Rename Machine...");
            Assert.That(Tracks, Is.EqualTo("Lead, Bass 2"));
            rig.Undo();
            Assert.That(Tracks, Is.EqualTo("Lead, Bass"), "the rename undoes");
        }

        [AvaloniaTest]
        public void ADoubleClickOrReturnChoosesThePatternForEditing()
        {
            using var rig = new Rig();
            Rig.Machine("Lead", ("Hook", 16, new[] { 0, 4 }), ("Riff", 16, new[] { 2 }));
            Song.AddSequence(Song.Machines.First(m => m.Name == "Lead"), 0);
            Settle();
            rig.Click(rig.At(0, 0));
            rig.Type("01");
            IPattern chosen = null;
            Action<IPattern> handler = p => chosen = p;
            SequenceTab.PatternChosen += handler;
            try
            {
                rig.DoubleClick(rig.At(16, 0, dx: 12));
                Assert.That(chosen?.Name, Is.EqualTo("Riff"), "the double-clicked block's pattern");
                Assert.That(Core.PatternEditorPattern, Is.SameAs(chosen), "the engine's pattern editor has it");
                rig.Key(PhysicalKey.ArrowLeft);
                Assert.That(Core.PatternEditorPattern.Name, Is.EqualTo("Hook"), "the cursor's pattern goes to the editor (AutoSelectPattern)");
                chosen = null;
                rig.Key(PhysicalKey.Enter);
                Assert.That(chosen?.Name, Is.EqualTo("Hook"), "Return chooses the cursor's pattern");
            }
            finally
            {
                SequenceTab.PatternChosen -= handler;
            }
        }

        [AvaloniaTest]
        public void TheShellsEditMenuCutsCopiesPastesAndUndoesHere()
        {
            using var rig = new Rig();
            Rig.Machine("Lead", ("Hook", 16, new[] { 0, 4 }));
            Song.AddSequence(Song.Machines.First(m => m.Name == "Lead"), 0);
            Settle();
            rig.Click(rig.At(0, 0));
            rig.Type("0");
            Assert.That(rig.Window.Tabs.SelectedContent, Is.InstanceOf<ReBuzz.Studio.Views.IStudioEditTarget>(), "the view takes the shell's edit commands");

            // Edit ▸ Copy on the block under the cursor, Edit ▸ Paste two bars on, Edit ▸ Cut, Edit ▸ Undo.
            void Edit(string header)
            {
                if (OperatingSystem.IsMacOS())
                {
                    // On a Mac the menus are the menu bar's (NativeMenu), not the window's: run the same model entry.
                    var entry = AppMenu.Find(rig.Window.MenuModel(mac: true), "Edit/" + header.Replace("_", ""));
                    Assert.That(entry, Is.Not.Null, $"the Edit menu has {header}");
                    Assert.That(entry!.CanRun?.Invoke() ?? true, Is.True, $"{header} is enabled");
                    entry.Run();
                }
                else
                {
                    var item = rig.Window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == header);
                    Assert.That(item.Command!.CanExecute(null), Is.True, $"{header} is enabled");
                    item.Command.Execute(null);
                }
                Settle();
            }
            rig.Click(rig.At(0, 0, dx: 12));
            Edit("_Copy");
            rig.Click(rig.At(32, 0));
            Edit("_Paste");
            Assert.That(Events(0), Is.EqualTo("0:Hook 32:Hook"));
            rig.Click(rig.At(0, 0, dx: 12));
            Edit("Cu_t");
            Assert.That(Events(0), Is.EqualTo("32:Hook"));
            Edit("_Undo");
            Assert.That(Events(0), Is.EqualTo("0:Hook 32:Hook"), "the song's history undoes the cut");
            Edit("_Undo");
            Assert.That(Events(0), Is.EqualTo("0:Hook"), "and the paste");
            Edit("_Redo");
            Assert.That(Events(0), Is.EqualTo("0:Hook 32:Hook"));

            // The window's keys reach the view when the focus is elsewhere (here the tab's header).
            var tab = (TabItem)rig.Window.Tabs.SelectedItem!;
            Assert.That(tab.Focus(), Is.True, "the tab header takes the focus");
            Settle();
            rig.Key(PhysicalKey.X, Cmd);
            Assert.That(Events(0), Is.EqualTo("32:Hook"), "Ctrl/Command+X from the window cuts the block under the cursor");
            rig.Undo();
            Assert.That(Events(0), Is.EqualTo("0:Hook 32:Hook"));

            // A focused text box takes the Edit menu's commands (the BPM box), not the view.
            var bpmBox = rig.Window.Transport.GetVisualDescendants().OfType<TextBox>().First();
            Assert.That(bpmBox.Focus(), Is.True);
            bpmBox.SelectAll();
            Settle();
            Edit("_Copy");
            var clipboard = TopLevel.GetTopLevel(rig.Window)!.Clipboard!;
            var text = Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard);
            for (var until = DateTime.UtcNow.AddSeconds(10); !text.IsCompleted && DateTime.UtcNow < until;)
                Dispatcher.UIThread.RunJobs();
            Assert.That(text.IsCompleted ? text.Result : null, Is.EqualTo(bpmBox.Text), "the BPM box's text is on the clipboard");
            Assert.That(Events(0), Is.EqualTo("0:Hook 32:Hook"), "the arrangement is untouched");
        }

        // ------------------------------------------------------------------ markers and playback

        [AvaloniaTest]
        public void LoopMarkersAndThePlayCursorFollowingPlayback()
        {
            using var rig = new Rig();
            Rig.Machine("Lead", ("Hook", 16, new[] { 0, 4, 8, 12 }));
            Rig.Machine("Bass", ("Line", 32, new[] { 0, 16 }));
            var canvas = rig.Canvas;
            Song.AddSequence(Song.Machines.First(m => m.Name == "Lead"), 0);
            Song.AddSequence(Song.Machines.First(m => m.Name == "Bass"), 1);
            Settle();
            rig.Click(rig.At(0, 0));
            rig.Type("0000");
            rig.Click(rig.At(0, 1));
            rig.Type("00");
            Assert.That(Events(0), Is.EqualTo("0:Hook 16:Hook 32:Hook 48:Hook"));
            Assert.That(Events(1), Is.EqualTo("0:Line 32:Line"));

            // Ctrl/Command+B: the loop starts at the cursor; +E: it ends at the cursor's bar end, again: the song does.
            rig.Click(rig.At(16, 0));
            rig.Key(PhysicalKey.B, Cmd);
            Assert.That(Song.LoopStart, Is.EqualTo(16));
            Assert.That((Song.LoopEnd, Song.SongEnd), Is.EqualTo((64, 64)), "placing patterns moved the ends");
            rig.Click(rig.At(112, 0));
            rig.Key(PhysicalKey.E, Cmd);
            Assert.That((Song.LoopEnd, Song.SongEnd), Is.EqualTo((128, 128)));
            rig.Key(PhysicalKey.ArrowLeft);
            rig.Key(PhysicalKey.ArrowLeft);
            rig.Key(PhysicalKey.E, Cmd);
            Assert.That((Song.LoopEnd, Song.SongEnd), Is.EqualTo((96, 128)), "the loop ends at the cursor's bar end");
            rig.Key(PhysicalKey.E, Cmd);
            Assert.That((Song.LoopEnd, Song.SongEnd), Is.EqualTo((96, 96)), "again there: the song ends there");

            // The menu: Set Start at bar 1 (right click there).
            rig.Click(rig.At(0, 0), MouseButton.Right);
            rig.Menu("Set Start");
            Assert.That(Song.LoopStart, Is.EqualTo(0));
            rig.Click(rig.At(16, 0), MouseButton.Right);
            rig.Menu("Set Start");
            Assert.That(Song.LoopStart, Is.EqualTo(16));
            rig.Undo();
            Assert.That(Song.LoopStart, Is.EqualTo(0), "markers undo");
            rig.Redo();
            Assert.That(Song.LoopStart, Is.EqualTo(16));
            rig.Screenshot("sequence-10-loop");

            // A click on the ruler moves the play position (to its bar).
            var ruler = rig.OfCanvas(new Point(canvas.Geometry.XOf(36), 10));
            rig.Click(ruler);
            Assert.That(Song.PlayPosition, Is.EqualTo(32));

            // Play: the engine's ticks move the position, and the marker follows.
            using var play = new Player();
            Core.Looping = true;
            Core.Playing = true;
            play.Ticks(8);
            int p1 = Song.PlayPosition;
            Assert.That(p1, Is.InRange(36, 40), "the position moved with the ticks");
            Assert.That(canvas.PlayMarkerX, Is.EqualTo(canvas.Geometry.XOf(p1)).Within(0.01), "the marker is there");
            rig.Screenshot("sequence-11-playing");
            play.Ticks(70);
            Assert.That(Song.PlayPosition, Is.LessThan(p1).And.AtLeast(16), "it looped back to the loop start");

            // Zoomed in, playback past the view scrolls it (Follow).
            canvas.SetZoom(SequenceGeometry.MaxTickWidth, null);
            canvas.ScrollTo(0, 0);
            Song.LoopEnd = 400;
            Song.PlayPosition = 300;
            play.Ticks(2);
            Assert.That(Song.PlayPosition, Is.InRange(300, 302));
            Settle();
            var x = canvas.PlayMarkerX;
            Assert.That(canvas.Geometry.ScrollX, Is.GreaterThan(0), "the view followed");
            Assert.That(x, Is.InRange(SequenceGeometry.HeaderWidth, canvas.Bounds.Width), "the play marker is in view");
            rig.Screenshot("sequence-12-following");
            Core.Playing = false;
            canvas.SetZoom(SequenceGeometry.DefaultTickWidth, null);
        }

        /// <summary>Renders whole ticks, as the audio thread would (a WorkManager, as ReBuzzTests and the Session View's tests do).</summary>
        internal sealed class Player : IDisposable
        {
            readonly WorkManager work = new WorkManager(Core, new WorkThreadEngine(1), 0, BuzzGUI.Common.Global.EngineSettings);
            readonly float[] buffer = new float[512];

            public void Ticks(int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    var mi = ReBuzzCore.masterInfo;
                    int frames = Math.Max(1, mi.SamplesPerTick - mi.PosInTick);
                    while (frames > 0)
                    {
                        int n = Math.Min(frames, buffer.Length / 2);
                        work.MainAudioFillBuffer(buffer, 0, n * 2);
                        frames -= n;
                    }
                }
                Settle();
            }

            public void Dispose() => Core.Playing = false;
        }

        // ------------------------------------------------------------------ the song file

        [AvaloniaTest]
        public void SaveAndReopenKeepTheArrangementColoursBarsAndZoom()
        {
            string dir = Path.Combine(Path.GetTempPath(), "rebuzz-studio-sequence-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "arranged.bmxml");
            using var rig = new Rig();
            Rig.Machine("Lead", ("Hook", 16, new[] { 0, 4, 8 }), ("Riff", 32, new[] { 0, 12 }));
            Rig.Machine("Bass", ("Line", 32, new[] { 0, 16 }));
            var canvas = rig.Canvas;
            Song.AddSequence(Song.Machines.First(m => m.Name == "Lead"), 0);
            Song.AddSequence(Song.Machines.First(m => m.Name == "Bass"), 1);
            Settle();
            rig.Click(rig.At(0, 0));
            rig.Type("0101");
            rig.Click(rig.At(16, 1));
            rig.Type("0,0");

            // Colour: Riff red, from the menu on its block.
            rig.Click(rig.At(16, 0, dx: 12), MouseButton.Right);
            rig.Menu("Color", "Red");
            int red = SequenceSongState.Palette.ToList().FindIndex(c => c.Name == "Red");
            var state = SequenceSongState.For(Core);
            Assert.That(state.ColourIndexOf(Rig.Pattern("Lead", "Riff")), Is.EqualTo(red));
            Assert.That(ReBuzz.Core.PatternColours.Current.TryGet(Rig.Pattern("Lead", "Riff"), out int engineColour) && engineColour == red, Is.True,
                "the engine (and the Session View) see the colour");

            // Bars of 8 from tick 96 on (Ctrl/Command+T), loop markers, zoom.
            rig.Click(rig.At(96, 0));
            Dialogs.TextResponder = (title, current) => "8";
            rig.Key(PhysicalKey.T, Cmd);
            Assert.That(canvas.TimeSignatures.Entries, Is.EqualTo(new System.Collections.Generic.Dictionary<int, int> { [0] = 16, [96] = 8 }));
            rig.Click(rig.At(16, 0, dx: 20));
            rig.Key(PhysicalKey.B, Cmd);
            rig.Click(rig.At(64, 0, dx: 20));
            rig.Key(PhysicalKey.E, Cmd);
            canvas.SetZoom(3, null);
            Assert.That(rig.View.ZoomSlider.Value, Is.EqualTo(3), "the slider follows");
            string lead = Events(0), bass = Events(1);
            Assert.That(lead, Is.EqualTo("0:Hook 16:Riff 48:Hook 64:Riff"));
            Assert.That(bass, Is.EqualTo("16:Line 48:<break> 64:Line"));
            rig.Screenshot("sequence-13-before-save");

            SongFiles.Picker = save => path;
            Assert.That(SongFiles.SaveAs().Result, Is.True);
            SongFiles.New().Wait();
            Settle();
            Assert.That(Song.Sequences, Is.Empty);
            Assert.That(canvas.TimeSignatures.Entries.Count, Is.EqualTo(1), "a new song has the default bars");
            canvas.SetZoom(SequenceGeometry.DefaultTickWidth, null);

            Assert.That(SongFiles.Open(path).Result, Is.True);
            Settle();
            Assert.That(Tracks, Is.EqualTo("Lead, Bass"));
            Assert.That(Events(0), Is.EqualTo(lead));
            Assert.That(Events(1), Is.EqualTo(bass));
            Assert.That((Song.LoopStart, Song.LoopEnd), Is.EqualTo((16, 80)));
            Assert.That(canvas.TimeSignatures.Entries, Is.EqualTo(new System.Collections.Generic.Dictionary<int, int> { [0] = 16, [96] = 8 }), "the bars");
            Assert.That(state.ColourIndexOf(Rig.Pattern("Lead", "Riff")), Is.EqualTo(red), "the colour");
            Assert.That(canvas.Geometry.TickWidth, Is.EqualTo(3), "the zoom");
            Assert.That(File.ReadAllText(path), Does.Contain(SequenceSongState.ClassicSection).And.Contain(SequenceSongState.HorizontalSection),
                "the Windows editors' subsections");
            rig.Screenshot("sequence-14-reopened");
            canvas.SetZoom(SequenceGeometry.DefaultTickWidth, null);
        }

        [AvaloniaTest]
        public void AnOldBuzzSongShowsItsArrangementAndSavesItBack()
        {
            string repo = AppContext.BaseDirectory;
            while (repo != null && !File.Exists(Path.Combine(repo, "ReBuzz.sln"))) repo = Path.GetDirectoryName(repo);
            string song = repo == null ? null : Path.Combine(repo, "ExternalMachines", "source", "buzztrax-buzzmachines", "FSM", "Infector", "FSM_Infector_Demo.bmx");
            if (song == null || !File.Exists(song)) Assert.Ignore("the demo song isn't in this checkout");
            using var rig = new Rig();
            Assert.That(SongFiles.Open(song).Result, Is.True);
            Settle();
            int rows = Song.Sequences.Count;
            Assert.That(rows, Is.GreaterThan(0), "the demo has tracks");
            var before = Enumerable.Range(0, rows).Select(Events).ToList();
            Assert.That(before.Any(e => e.Length > 0), Is.True, "and events");
            Assert.That(rig.Canvas.Rows, Is.EqualTo(rows));
            Assert.That(rig.Canvas.TimeSignatures.Entries, Is.EqualTo(new System.Collections.Generic.Dictionary<int, int> { [0] = 16 }), "no editor data: the default bars");
            rig.Screenshot("sequence-15-old-song");

            string copy = Path.Combine(Path.GetTempPath(), "rebuzz-studio-sequence-" + Environment.ProcessId, "infector.bmxml");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            SongFiles.Picker = save => copy;
            Assert.That(SongFiles.SaveAs().Result, Is.True);
            Assert.That(SongFiles.Open(copy).Result, Is.True);
            Settle();
            Assert.That(Enumerable.Range(0, Song.Sequences.Count).Select(Events), Is.EqualTo(before), "the arrangement round-trips");
        }

        // ------------------------------------------------------------------ pure logic

        [Test]
        public void TheBarListSnapsInsertsAndDeletesAsBuzzDoes()
        {
            var tsl = new ReBuzz.Core.SongMeter();
            Assert.That(tsl.Snap(37, int.MaxValue), Is.EqualTo(32));
            Assert.That(tsl.GetBarLengthAt(37), Is.EqualTo(16));
            tsl.Set(40, 8);
            Assert.That(tsl.Entries, Is.EqualTo(new System.Collections.Generic.Dictionary<int, int> { [0] = 16, [32] = 8 }), "a change mid-bar splits the bar it falls in");
            Assert.That(tsl.GetBars(56).ToArray(), Is.EqualTo(new[] { (0, 16), (16, 16), (32, 8), (40, 8), (48, 8) }));
            tsl.Insert(16, 16);
            Assert.That(tsl.Entries.Keys, Is.EqualTo(new[] { 0, 48 }));
            tsl.Delete(16, 16);
            Assert.That(tsl.Entries.Keys, Is.EqualTo(new[] { 0, 32 }));
            var copy = new ReBuzz.Core.SongMeter(tsl);
            var ms = new MemoryStream();
            copy.Write(new BinaryWriter(ms));
            ms.Position = 0;
            var read = new ReBuzz.Core.SongMeter();
            read.Read(new BinaryReader(ms));
            Assert.That(read.Entries, Is.EqualTo(tsl.Entries), "the file format round-trips");
        }

        [Test]
        public void ResizesSnapAsTheWpfEditorDoes()
        {
            Assert.That(SequenceGeometry.ResizeRightLength(16, 40, 16), Is.EqualTo(32), "the end goes to the next snap line");
            Assert.That(SequenceGeometry.ResizeRightLength(16, 48, 16), Is.EqualTo(32), "on a line it stays there");
            Assert.That(SequenceGeometry.ResizeRightLength(16, 3, 16), Is.EqualTo(16), "at least a snap");
            Assert.That(SequenceGeometry.ResizeLeft(64, 16, 47, 16), Is.EqualTo((48, 32)), "the start to the nearest line, the end stays");
            Assert.That(SequenceGeometry.ResizeLeft(64, 32, 90, 16), Is.EqualTo((80, 16)), "at most a snap before the end");
            var sel = new SequenceSelection();
            sel.Begin(40, 2);
            sel.Update(17, 0);
            Assert.That(sel.Rect(new ReBuzz.Core.SongMeter()), Is.EqualTo(new SequenceRect(16, 0, 32, 3)));
            var block = new Rect(100, 0, 64, 80);
            Assert.That(SequenceGeometry.PartAt(block, new Point(102, 10), true), Is.EqualTo(BlockPart.LeftEdge));
            Assert.That(SequenceGeometry.PartAt(block, new Point(160, 10), true), Is.EqualTo(BlockPart.RightEdge));
            Assert.That(SequenceGeometry.PartAt(block, new Point(130, 10), true), Is.EqualTo(BlockPart.Body));
            Assert.That(SequenceGeometry.PartAt(block, new Point(102, 10), false), Is.EqualTo(BlockPart.Body), "breaks don't resize");
        }
    }
}
