// Fork (docs/specs/daw-modes.md AR6): the tracker's vertical orientation (Buzz's default sequence view: time down the page, tracks as
// columns), headless, in the main window's Sequence tab. Each item of the parity checklist (§8.7) with the WPF vertical editor
// (ModernSequenceEditor) is one step here, checked against the engine's sequences; double clicks go through MachineViewTests.DoubleClick.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Sequence;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class VerticalSequenceTests
    {
        const int TB = ClipEvent.TimeBase;

        static ReBuzzCore Core => Studio.Host.Core;
        static ISong Song => Core.Song;

        static RawInputModifiers Cmd => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        static void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>The remembered orientation, as the view reads and writes it.</summary>
        static bool StoredVertical
        {
            get => Studio.Host.Registry.Read("SequenceVertical", false, "Studio");
            set => Studio.Host.Registry.Write("SequenceVertical", value, "Studio");
        }

        sealed class Rig : IDisposable
        {
            public MainWindow Window { get; }
            public SequenceView View { get; }
            public SequenceCanvas Canvas => View.Canvas;

            public Rig(bool vertical = true)
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                StoredVertical = vertical;
                Core.ExecuteCommand(BuzzCommand.Stop);
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
                Window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                ToolTip.SetServiceEnabled(Window, false);
                Window.Show();
                var tab = Window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Arrangement");
                tab.IsSelected = true;
                Settle();
                View = (SequenceView)tab.Content!;
                Canvas.AutoSelectClip = true;
                Settle();
            }

            public static MachineCore Machine(string name, params (string Name, int Length, int[] Notes)[] clips)
            {
                var song = Core.SongCore;
                song.CreateMachine("Neo Poly", null, name, null, null, null, -1, -0.3f + 0.2f * song.MachinesList.Count, 0.3f);
                var machine = song.MachinesList.Last(m => m.Name == name);
                var master = song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
                song.ConnectMachines(machine, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                foreach (var (pname, length, notes) in clips)
                {
                    machine.CreateClip(pname, length);
                    var p = machine.ClipsList.Single(x => x.Name == pname);
                    if (notes.Length > 0)
                        Core.SessionLayout.EditNotes(p, null, notes.Select((t, i) => new ClipNote(t * TB, TB * 2, 60 + 3 * (i % 5), 100)).ToArray());
                }
                return machine;
            }

            /// <summary>Two tracks, Lead and Bass, each with clips.</summary>
            public static void TwoTracks()
            {
                Machine("Lead", ("Hook", 16, new[] { 0, 4, 8, 12 }), ("Riff", 32, new[] { 0, 2, 6, 10, 16, 20, 26 }));
                Machine("Bass", ("Line", 32, new[] { 0, 8, 16, 24 }));
                Song.AddSequence(Song.Machines.First(m => m.Name == "Lead"), 0);
                Song.AddSequence(Song.Machines.First(m => m.Name == "Bass"), 1);
                Settle();
            }

            public static IClip Clip(string machine, string name) => Core.Song.Machines.First(m => m.Name == machine).Clips.First(p => p.Name == name);

            /// <summary>A cell in window pixels: tick, track, how far along the cell (pixels down from its start), and where across it (a fraction of the column).</summary>
            public Point At(int time, int row, double along = 6, double across = 0.6)
            {
                Settle();
                var geo = Canvas.Geometry;
                Assert.That(geo.Vertical, Is.True);
                // A cell below the window's bottom (or above its headers) is scrolled into view first, as a person would.
                double y = geo.TimePos(time) + along;
                if (y > Canvas.Bounds.Height - 60 || y < geo.LeadY + 4)
                {
                    Canvas.ScrollTo(geo.ScrollX, Math.Max(0, geo.ScrollY + y - 200));
                    Settle();
                }
                var p = Canvas.TranslatePoint(new Point(geo.RowPos(row) + across * geo.TrackWidth, geo.TimePos(time) + along), Window);
                Assert.That(p, Is.Not.Null);
                return p!.Value;
            }

            public Point OfCanvas(Point p) => Canvas.TranslatePoint(p, Window)!.Value;

            public Point Of(Control c)
            {
                Settle();
                return c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), Window)!.Value;
            }

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

            public void Drag(Point from, Point to, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.MouseMove(from, mods);
                Window.MouseDown(from, MouseButton.Left, mods);
                for (int i = 1; i <= 4; i++)
                    Window.MouseMove(from + (to - from) * (i / 4.0), mods | RawInputModifiers.LeftMouseButton);
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

            public IReadOnlyList<string> MenuItems()
            {
                var menu = View.OpenMenu;
                Assert.That(menu?.IsOpen, Is.True, "a menu opened");
                return menu!.Items.OfType<MenuItem>().Select(x => x.Header as string).ToList();
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
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
            }
        }

        static string Events(int row) => string.Join(" ", Song.Sequences[row].Events.Select(e =>
            $"{e.Key}:{(e.Value.Type == SequenceEventType.PlayClip ? e.Value.Clip.Name : "<" + e.Value.Type.ToString().ToLowerInvariant() + ">")}"));

        static string Tracks => string.Join(", ", Song.Sequences.Select(s => s.Machine.Name));

        // ------------------------------------------------------------------ items 1-3: the toggle, the layout, the ruler and the headers

        [AvaloniaTest]
        public void TheToggleTurnsTheTrackerOverTimeRunsDownTheTracksAreColumnsAndTheChoiceIsRemembered()
        {
            bool saved = StoredVertical;
            using var restore = Studio.AfterTest(() => StoredVertical = saved);
            Rig rig = new Rig(vertical: false);
            try
            {
                Rig.TwoTracks();
                Assert.That(rig.View.IsVertical, Is.False, "the horizontal is the default");
                Assert.That(rig.View.VerticalButton.IsChecked, Is.Not.True);

                rig.Click(rig.Of(rig.View.VerticalButton));
                Assert.That(rig.View.IsVertical, Is.True);
                Assert.That(StoredVertical, Is.True, "remembered per user");
                var canvas = rig.Canvas;
                var geo = canvas.Geometry;

                // Layout: the tracks are columns from the ruler's edge; time runs down from the headers.
                rig.Click(rig.At(0, 0));
                rig.Type("01");
                rig.Click(rig.At(0, 1));
                rig.Type("0");
                Assert.That(Events(0), Is.EqualTo("0:Hook 16:Riff"));
                var hook = Song.Sequences[0].Events.First();
                var block = geo.BlockRect(hook.Key, 0, hook.Value);
                Assert.That(block.X, Is.EqualTo(SequenceGeometry.VerticalRulerWidth), "the first column starts at the ruler's edge");
                Assert.That(block.Y, Is.EqualTo(SequenceGeometry.VerticalHeaderHeight), "time 0 is under the headers");
                Assert.That(block.Width, Is.EqualTo(geo.TrackWidth));
                Assert.That(block.Height, Is.EqualTo(16 * geo.PxPerTick), "a 16-tick clip is 16 ticks tall");
                var riff = Song.Sequences[0].Events.Last();
                Assert.That(geo.BlockRect(riff.Key, 0, riff.Value).Y, Is.EqualTo(block.Bottom), "the next bar is below");
                Assert.That(geo.BlockRect(0, 1, Song.Sequences[1].Events.First().Value).X, Is.EqualTo(block.Right), "the next track is to the right");
                rig.Screenshot("ar6-vertical-tracks");

                // A block under the pointer is selected by a click; the ruler on the left sets the play position (to its bar).
                rig.Click(rig.At(16, 0, along: 20));
                Assert.That(canvas.Selection.Rect(canvas.TimeSignatures), Is.EqualTo(new SequenceRect(16, 0, 32, 1)));
                rig.Click(rig.OfCanvas(new Point(10, geo.TimePos(36))));
                Assert.That(Song.PlayPosition, Is.EqualTo(32), "the ruler click snaps to the bar");
                Assert.That(canvas.TipAt(new Point(10, geo.TimePos(36))), Does.StartWith("Bar 3"), "the ruler's tip");

                // The headers: a click chooses the track, M, S and D are the machine's and the track's switches.
                rig.Click(rig.OfCanvas(new Point(geo.RowPos(1) + 30, 10)));
                Assert.That(canvas.CursorRow, Is.EqualTo(1), "a click on a header chooses its track");
                rig.Click(rig.OfCanvas(canvas.HeaderButton(1, 0).Center));
                rig.Click(rig.OfCanvas(canvas.HeaderButton(0, 1).Center));
                rig.Click(rig.OfCanvas(canvas.HeaderButton(0, 2).Center));
                Assert.That(Song.Sequences[1].Machine.IsMuted, Is.True, "M mutes Bass");
                Assert.That(Song.Sequences[0].Machine.IsSoloed, Is.True, "S solos Lead");
                Assert.That(Song.Sequences[0].IsDisabled, Is.True, "D disables Lead's track");
                Assert.That(canvas.TipAt(canvas.HeaderButton(0, 0).Center), Does.StartWith("Mute"));
                rig.Screenshot("ar6-vertical-headers");
                Song.Sequences[0].Machine.IsSoloed = false;
                Song.Sequences[0].IsDisabled = false;
                Song.Sequences[1].Machine.IsMuted = false;

                // A new window opens in the remembered orientation, and the toggle turns it back.
                rig.Dispose();
                rig = new Rig(vertical: StoredVertical);
                Assert.That(rig.View.IsVertical, Is.True, "remembered");
                Assert.That(rig.View.VerticalButton.IsChecked, Is.True);
                rig.Click(rig.Of(rig.View.VerticalButton));
                Assert.That(rig.View.IsVertical, Is.False);
                Assert.That(StoredVertical, Is.False);
                Assert.That(rig.Canvas.Geometry.LeadX, Is.EqualTo(SequenceGeometry.HeaderWidth), "the horizontal's layout is back");
            }
            finally
            {
                rig.Dispose();
            }
        }

        // ------------------------------------------------------------------ items 4-7: arranging with the mouse

        [AvaloniaTest]
        public void ArrangeInColumnsWithMouseAndKeysThenMoveResizeDeleteCopyPasteAndUndo()
        {
            bool saved = StoredVertical;
            using var restore = Studio.AfterTest(() => StoredVertical = saved);
            using var rig = new Rig();
            Rig.TwoTracks();
            var canvas = rig.Canvas;

            // Keys: a clip's key places it and the cursor moves down past it.
            rig.Click(rig.At(0, 0));
            Assert.That((canvas.CursorTime, canvas.CursorRow), Is.EqualTo((0, 0)));
            rig.Type("0");
            Assert.That(canvas.CursorTime, Is.EqualTo(16), "past the clip");
            rig.Type("1");
            Assert.That(Events(0), Is.EqualTo("0:Hook 16:Riff"));
            Assert.That(canvas.CursorTime, Is.EqualTo(48));

            // The mouse: Bass's second bar, then the clip list's Line with a double click.
            rig.Click(rig.At(16, 1));
            Assert.That((canvas.CursorTime, canvas.CursorRow), Is.EqualTo((16, 1)));
            var item = rig.View.ClipList.ContainerFromIndex(0)!;
            rig.DoubleClick(item.TranslatePoint(new Point(20, item.Bounds.Height / 2), rig.Window)!.Value);
            Assert.That(Events(1), Is.EqualTo("16:Line"));
            Assert.That(canvas.CursorTime, Is.EqualTo(48), "past Line");

            // A double click on an empty cell makes a new clip there; on a block it chooses the clip for editing.
            rig.DoubleClick(rig.At(64, 1));
            Assert.That(Rig.Clip("Bass", "00").Length, Is.EqualTo(16), "the default clip length");
            Assert.That(Events(1), Is.EqualTo("16:Line 64:00"));
            IClip chosen = null;
            Action<IClip> handler = p => chosen = p;
            SequenceTab.ClipChosen += handler;
            try
            {
                rig.DoubleClick(rig.At(16, 0, along: 12));
                Assert.That(chosen?.Name, Is.EqualTo("Riff"), "the double-clicked block's clip");
                Assert.That(Core.ClipEditorClip, Is.SameAs(chosen), "the engine's clip editor has it");
            }
            finally
            {
                SequenceTab.ClipChosen -= handler;
            }
            rig.Screenshot("ar6-vertical-arranged");

            // Move: drag Hook from the start down to bar 4; the cursor follows the block.
            rig.Drag(rig.At(0, 0, along: 20), rig.At(64, 0, along: 20));
            Assert.That(Events(0), Is.EqualTo("16:Riff 64:Hook"));
            Assert.That((canvas.CursorTime, canvas.CursorRow), Is.EqualTo((64, 0)));

            // Alt-drag copies: Riff again at bar 6.
            rig.Drag(rig.At(16, 0, along: 20), rig.At(96, 0, along: 20), RawInputModifiers.Alt);
            Assert.That(Events(0), Is.EqualTo("16:Riff 64:Hook 96:Riff"));

            // Resize: Line's bottom edge to bar 4 (its clip gets 48 ticks); Hook's top edge back a bar (32 ticks, from 48).
            rig.Drag(rig.At(48, 1, along: -2), rig.At(62, 1, along: 0));
            Assert.That(Rig.Clip("Bass", "Line").Length, Is.EqualTo(48));
            rig.Drag(rig.At(64, 0, along: 2), rig.At(48, 0, along: 2));
            Assert.That(Events(0), Is.EqualTo("16:Riff 48:Hook 96:Riff"));
            Assert.That(Rig.Clip("Lead", "Hook").Length, Is.EqualTo(32), "the end stays where it was");

            // Delete: a click selects the block, Delete clears it.
            rig.Click(rig.At(64, 1, along: 12));
            Assert.That(canvas.Selection.NotEmpty, Is.True, "a click on a block selects it");
            rig.Key(PhysicalKey.Delete);
            Assert.That(Events(1), Is.EqualTo("16:Line"));

            // Copy: a rubber band from an empty cell (Bass's first bar) across both columns down to bar 3; paste at bar 8.
            rig.Drag(rig.At(0, 1, along: 4), rig.At(32, 0, along: 20));
            Assert.That(canvas.Selection.Rect(canvas.TimeSignatures), Is.EqualTo(new SequenceRect(0, 0, 48, 2)), "three bars of both tracks");
            rig.Screenshot("ar6-vertical-selecting");
            rig.Key(PhysicalKey.C, Cmd);
            rig.Click(rig.At(128, 0));
            Assert.That(canvas.Selection.NotEmpty, Is.False, "a click on empty space drops the selection");
            rig.Key(PhysicalKey.V, Cmd);
            Assert.That(Events(0), Is.EqualTo("16:Riff 48:Hook 96:Riff 144:Riff"));
            Assert.That(Events(1), Is.EqualTo("16:Line 144:Line"));

            // Cut the block under the cursor, paste it further on.
            rig.Click(rig.At(144, 1, along: 12));
            rig.Key(PhysicalKey.X, Cmd);
            Assert.That(Events(1), Is.EqualTo("16:Line"));
            rig.Click(rig.At(192, 1));
            rig.Key(PhysicalKey.V, Cmd);
            Assert.That(Events(1), Is.EqualTo("16:Line 192:Line"), "the cut pastes elsewhere");

            // Undo, step by step.
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
            Assert.That(Events(0), Is.EqualTo("16:Riff 64:Hook 96:Riff"), "undo the top-edge resize");
            rig.Undo();
            Assert.That(Rig.Clip("Bass", "Line").Length, Is.EqualTo(32), "undo the bottom-edge resize");
            rig.Undo();
            rig.Undo();
            Assert.That(Events(0), Is.EqualTo("0:Hook 16:Riff"), "undo the copy and the move");
            rig.Redo();
            Assert.That(Events(0), Is.EqualTo("16:Riff 64:Hook"), "redo the move");
        }

        // ------------------------------------------------------------------ items 8-10: the keys and the menu

        [AvaloniaTest]
        public void TheArrowKeysGoDownThroughTimeAndAcrossTheTracksAndTheOtherKeysMeanWhatTheyDoInWpf()
        {
            bool saved = StoredVertical;
            using var restore = Studio.AfterTest(() => StoredVertical = saved);
            using var rig = new Rig();
            Rig.TwoTracks();
            var canvas = rig.Canvas;
            rig.Click(rig.At(0, 0));
            rig.Type("0,0-0_");
            Assert.That(Events(0), Is.EqualTo("0:Hook 16:<break> 32:Hook 48:<mute> 64:Hook 80:<thru>"));
            rig.Key(PhysicalKey.Comma, RawInputModifiers.Shift);
            Assert.That(Events(0), Does.EndWith("96:<off>"), "Shift+, stops the track");

            // Down and Up are bars of time, Right and Left are tracks.
            rig.Click(rig.At(0, 0));
            rig.Key(PhysicalKey.ArrowDown);
            Assert.That((canvas.CursorTime, canvas.CursorRow), Is.EqualTo((16, 0)));
            rig.Key(PhysicalKey.ArrowRight);
            Assert.That((canvas.CursorTime, canvas.CursorRow), Is.EqualTo((16, 1)));
            rig.Key(PhysicalKey.ArrowUp);
            rig.Key(PhysicalKey.ArrowLeft);
            Assert.That((canvas.CursorTime, canvas.CursorRow), Is.EqualTo((0, 0)));
            rig.Key(PhysicalKey.PageDown);
            Assert.That(canvas.CursorTime, Is.EqualTo(16 * 16), "Page Down: 16 bars");
            rig.Key(PhysicalKey.PageUp);
            Assert.That(canvas.CursorTime, Is.EqualTo(0));

            // Home: the start of time, then the first track; End: the last cell of the song, then the last track.
            rig.Click(rig.At(32, 1));
            rig.Key(PhysicalKey.Home);
            Assert.That((canvas.CursorTime, canvas.CursorRow), Is.EqualTo((0, 1)), "Home goes to the top first");
            rig.Key(PhysicalKey.Home);
            Assert.That((canvas.CursorTime, canvas.CursorRow), Is.EqualTo((0, 0)), "then to the first track");
            rig.Key(PhysicalKey.End);
            Assert.That(canvas.CursorTime, Is.EqualTo(Song.SongEnd - 16), "End goes to the song's last bar");
            rig.Key(PhysicalKey.End);
            Assert.That(canvas.CursorRow, Is.EqualTo(1), "then to the last track");

            // Shift selects.
            rig.Click(rig.At(32, 0));
            rig.Key(PhysicalKey.ArrowDown, RawInputModifiers.Shift);
            rig.Key(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
            Assert.That(canvas.Selection.Rect(canvas.TimeSignatures), Is.EqualTo(new SequenceRect(32, 0, 32, 2)));
            rig.Key(PhysicalKey.Escape);
            Assert.That(canvas.Selection.NotEmpty, Is.False);

            // Insert and Delete move what follows by a bar; Back clears the bar above the cursor and moves up.
            rig.Click(rig.At(16, 0));
            rig.Key(PhysicalKey.Escape);        // (a click on a block selects it, and Delete would clear the selection)
            rig.Key(PhysicalKey.Delete);
            Assert.That(Events(0), Is.EqualTo("0:Hook 16:Hook 32:<mute> 48:Hook 64:<thru> 80:<off>"), "Delete takes the bar and moves the rest up");
            rig.Key(PhysicalKey.Insert);
            Assert.That(Events(0), Is.EqualTo("0:Hook 32:Hook 48:<mute> 64:Hook 80:<thru> 96:<off>"));
            rig.Click(rig.At(48, 0));
            rig.Key(PhysicalKey.Escape);
            rig.Key(PhysicalKey.Backspace);
            Assert.That(Events(0), Is.EqualTo("0:Hook 48:<mute> 64:Hook 80:<thru> 96:<off>"), "Back clears the bar above the cursor");
            Assert.That(canvas.CursorTime, Is.EqualTo(32));
            rig.Undo();
            rig.Undo();
            rig.Undo();

            // Ctrl+Right moves the track one place to the right (WPF's Move Track Right); undo puts it back.
            rig.Click(rig.At(0, 0));
            rig.Key(PhysicalKey.ArrowRight, Cmd);
            Assert.That(Tracks, Is.EqualTo("Bass, Lead"));
            Assert.That(canvas.CursorRow, Is.EqualTo(1), "the cursor stays on the track");
            rig.Key(PhysicalKey.ArrowLeft, Cmd);
            Assert.That(Tracks, Is.EqualTo("Lead, Bass"), "and back to the left");

            // The context menu says left and right.
            rig.Click(rig.At(0, 1), MouseButton.Right);
            var items = rig.MenuItems();
            Assert.That(items, Does.Contain("Move Track Left").And.Contain("Move Track Right").And.Not.Contain("Move Track Up"));
            rig.View.OpenMenu!.Close();
            Settle();

            // Quick Create Clips: two new clips from the cursor, down the column.
            rig.Click(rig.At(128, 1), MouseButton.Right);
            var quick = rig.View.OpenMenu!.Items.OfType<MenuItem>().First(x => x.Header as string == "Quick Create Clips");
            var two = quick.Items.OfType<MenuItem>().First(x => x.Header as string == "2");
            two.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Settle();
            Assert.That(Events(1), Is.EqualTo("128:00 144:01"));
            rig.Screenshot("ar6-vertical-keys");
        }

        // ------------------------------------------------------------------ items 11-13: loop markers, playback, scrolling, zoom

        [AvaloniaTest]
        public void LoopMarkersTheRulerTheFollowingPlayheadTheWheelAndTheZoomWorkDownThePage()
        {
            bool saved = StoredVertical;
            using var restore = Studio.AfterTest(() => StoredVertical = saved);
            using var rig = new Rig();
            Rig.TwoTracks();
            var canvas = rig.Canvas;
            var geo = canvas.Geometry;
            rig.Click(rig.At(0, 0));
            rig.Type("0000");
            Assert.That(Events(0), Is.EqualTo("0:Hook 16:Hook 32:Hook 48:Hook"));

            // Ctrl+B: the loop starts at the cursor; Ctrl+E: it ends at the cursor's bar end.
            rig.Click(rig.At(16, 0));
            rig.Key(PhysicalKey.B, Cmd);
            Assert.That(Song.LoopStart, Is.EqualTo(16));
            rig.Click(rig.At(32, 0));
            rig.Key(PhysicalKey.E, Cmd);
            Assert.That(Song.LoopEnd, Is.EqualTo(48), "the loop ends at the cursor's bar end");
            rig.Screenshot("ar6-vertical-loop");

            // Playing: the position follows the ticks and the playhead is a line across the columns.
            using var play = new SequenceEditorTests.Player();
            Core.Looping = true;
            try
            {
                Song.PlayPosition = 16;
                Core.Playing = true;
                play.Ticks(8);
                int p1 = Song.PlayPosition;
                Assert.That(p1, Is.InRange(20, 28), "the position moved with the ticks");
                Assert.That(canvas.PlayMarkerPos, Is.EqualTo(geo.TimePos(p1)).Within(0.01), "the marker is at that height");
                rig.Screenshot("ar6-vertical-playing");

                // Zoomed in, playback past the view scrolls it down (Follow).
                Core.Playing = false;
                canvas.SetZoom(SequenceGeometry.MaxTickWidth, null);
                canvas.ScrollTo(0, 0);
                Song.LoopStart = 0;
                Song.LoopEnd = 400;
                Song.PlayPosition = 300;
                Core.Playing = true;
                play.Ticks(2);
                Settle();
                Assert.That(geo.ScrollY, Is.GreaterThan(0), "the view followed down the page");
                Assert.That(geo.ScrollX, Is.EqualTo(0), "and not sideways");
                Assert.That(canvas.PlayMarkerPos, Is.InRange(geo.LeadY, canvas.Bounds.Height), "the play marker is in view");
            }
            finally
            {
                Core.Playing = false;
                Core.Looping = false;
            }
            canvas.SetZoom(SequenceGeometry.DefaultTickWidth, null);

            // The wheel scrolls down the page; Shift turns it sideways; Ctrl+wheel zooms around the pointer.
            Song.SongEnd = 2000;
            canvas.ScrollTo(0, 0);
            Settle();
            var at = rig.OfCanvas(new Point(300, 300));
            rig.Window.MouseWheel(at, new Vector(0, -2));
            Settle();
            Assert.That(geo.ScrollY, Is.GreaterThan(0), "the wheel scrolled the time");
            Assert.That(rig.View.PageScrollBar.Maximum, Is.GreaterThan(0), "the page scroll bar has a range");
            double before = geo.ScrollY;
            int tickUnder = geo.TimeAtPoint(new Point(300, 300));
            rig.Window.MouseWheel(at, new Vector(0, 1), Cmd);
            Settle();
            Assert.That(geo.TickWidth, Is.GreaterThan(SequenceGeometry.DefaultTickWidth), "Ctrl+wheel zoomed in");
            Assert.That(geo.TimeAtPoint(new Point(300, 300)), Is.EqualTo(tickUnder).Within(1), "around the pointer");
            Assert.That(geo.ScrollY, Is.Not.EqualTo(before));
            canvas.SetZoom(SequenceGeometry.DefaultTickWidth, null);
        }

        // ------------------------------------------------------------------ item 14: the kit

        [AvaloniaTest]
        public void TheControlsAreOnTheKitsStyleHaveNamesDrawInLightAndDarkLeaveTheArrangementAloneAndLetGoWhenTheWindowCloses()
        {
            var kind = StudioTheme.Kind;
            bool saved = StoredVertical;
            using var restore = Studio.AfterTest(() => { StudioTheme.Kind = kind; StudioTheme.Refresh(); StoredVertical = saved; });
            var rig = new Rig();
            bool disposed = false;
            try
            {
                Rig.TwoTracks();
                rig.Click(rig.At(0, 0));
                rig.Type("0");
                rig.Click(rig.At(0, 1));
                rig.Type("0");
                Settle();

                var problems = new List<string>();
                var all = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(rig.View).OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0).ToList();
                foreach (var c in all)
                {
                    if (c.GetType() != typeof(Button) && c.GetType() != typeof(ToggleButton)) continue;
                    var b = (TemplatedControl)c;
                    string label = Avalonia.Automation.AutomationProperties.GetName(b);
                    if (string.IsNullOrEmpty(label)) { problems.Add("a button without a name: " + ((ContentControl)b).Content); continue; }
                    bool icon = b.Classes.Contains("icon");
                    var padding = icon ? StudioStyles.IconPadding : StudioStyles.ButtonPadding;
                    double font = icon ? StudioStyles.IconFontSize : StudioTokens.FontBody;
                    if (b.Padding != padding) problems.Add($"padding {b.Padding}: {label}");
                    if (b.FontSize != font) problems.Add($"font {b.FontSize}: {label}");
                    if (b.Bounds.Height < StudioStyles.ControlHeight - 0.5) problems.Add($"{b.Bounds.Height:0} high: {label}");
                    if (b.CornerRadius != new CornerRadius(StudioStyles.CornerRadius)) problems.Add($"radius: {label}");
                }
                Assert.That(problems, Is.Empty);
                Assert.That(all.Any(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Vertical tracks"), Is.True, "the toggle is named");
                Assert.That(all.Any(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Add track"), Is.True, "the corner's controls moved to the right panel and are still there");
                Assert.That(rig.View.AddTrackButton.TranslatePoint(new Point(), rig.View.ClipList)?.X, Is.Not.Null);

                foreach (var theme in new[] { AppearanceTheme.Light, AppearanceTheme.Dark })
                {
                    StudioTheme.Kind = theme;
                    StudioTheme.Refresh();
                    Settle();
                    rig.Screenshot("ar6-vertical-" + theme);
                }

                // The arrangement is horizontal whatever the tracker's orientation: its corner is back, the toggle is not offered.
                rig.Click(rig.Of(rig.View.ArrangementModeButton));
                Assert.That(rig.View.IsArrangementMode, Is.True);
                Assert.That(rig.View.VerticalButton.IsEffectivelyVisible, Is.False, "no orientation in the arrangement");
                Assert.That(rig.View.AddTrackButton.IsEffectivelyVisible, Is.True, "the arrangement keeps its corner");
                rig.Click(rig.Of(rig.View.TrackerModeButton));
                Assert.That(rig.View.IsVertical, Is.True, "back to the tracker: still vertical");
                Assert.That(rig.View.VerticalButton.IsEffectivelyVisible, Is.True);

                Assert.That(Listeners(Core.SongCore, "SequenceAdded"), Is.GreaterThan(0), "the view listens while shown");
                int open = Listeners(Core.SongCore, "SequenceAdded");
                rig.Dispose();
                disposed = true;
                Assert.That(Listeners(Core.SongCore, "SequenceAdded"), Is.LessThan(open), "the closed window let go");
            }
            finally
            {
                if (!disposed) rig.Dispose();
            }
        }

        /// <summary>The Tracker and Arrange mode buttons were fixed 64 px wide in a 120 px panel and both labels were clipped: each now gets
        /// at least the width its label and padding ask for, in both themes, in the tracker's orientations and the arrangement.</summary>
        [AvaloniaTest]
        public void TheModeButtonsAreWideEnoughForTheirLabelsInBothThemes()
        {
            var kind = StudioTheme.Kind;
            try
            {
                foreach (var theme in new[] { AppearanceTheme.Light, AppearanceTheme.Dark })
                foreach (bool vertical in new[] { true, false })
                {
                    StudioTheme.Kind = theme;
                    StudioTheme.Refresh();
                    using var rig = new Rig(vertical);
                    Settle();
                    foreach (var button in new[] { rig.View.TrackerModeButton, rig.View.ArrangementModeButton })
                    {
                        var content = (string)button.Content!;
                        Assert.That(button.IsEffectivelyVisible, Is.True, content);
                        double arranged = button.Bounds.Width;
                        button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        double wanted = button.DesiredSize.Width;
                        Assert.That(wanted, Is.GreaterThan(20), content + " has a measured label");
                        Assert.That(arranged, Is.GreaterThanOrEqualTo(wanted - 0.01), $"{content} ({theme}, vertical {vertical}) gets {arranged} px but its label wants {wanted}");
                        var inPanel = button.TranslatePoint(new Point(arranged, 0), rig.View)!.Value.X;
                        Assert.That(inPanel, Is.LessThanOrEqualTo(rig.View.Bounds.Width + 0.01), content + " lies inside the view");
                    }
                    Studio.Screenshot(rig.Window, $"sequence-mode-buttons-{theme}-{(vertical ? "vertical" : "horizontal")}");
                    rig.Click(rig.Of(rig.View.ArrangementModeButton));
                    Settle();
                    Assert.That(rig.View.ArrangementModeButton.Bounds.Width, Is.GreaterThanOrEqualTo(rig.View.ArrangementModeButton.DesiredSize.Width - 0.01), "also in the arrangement");
                    Assert.That(rig.View.TrackerModeButton.Bounds.Width, Is.GreaterThanOrEqualTo(rig.View.TrackerModeButton.DesiredSize.Width - 0.01), "also in the arrangement");
                }
            }
            finally
            {
                StudioTheme.Kind = kind;
                StudioTheme.Refresh();
                Settle();
            }
        }

        static int Listeners(object owner, string eventName)
        {
            var field = owner.GetType().GetField(eventName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (field?.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
        }
    }
}
