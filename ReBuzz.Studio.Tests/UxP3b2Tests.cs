// Fork (docs/specs/ux-overhaul.md P3b, second step: decisions 45-49): what the docked piano roll has around its grid: the bar and
// beat ruler (the song's bar length and ticks a beat), the playhead that follows the pattern, the loop brace that is the song's own
// loop, the velocity lane (one undo step a drag, relative for several notes) and cut, copy and paste of notes.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Detail;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class UxP3b2Tests
    {
        const int TB = PatternEvent.TimeBase;

        /// <summary>The pattern placed on its machine's track at a song tick (the loop brace needs it in the song).</summary>
        static void Place(UxP3bTests.Rig r, int time)
        {
            var seq = r.Song.Core.Song.Sequences.First(s => s.Machine == r.Lead);
            seq.SetEvent(time, new SequenceEvent(SequenceEventType.PlayPattern, r.Pattern));
        }

        static Point InWindow(UxP3bTests.Rig r, Control c, Point p)
        {
            UxP3bTests.Pump();
            r.Window.UpdateLayout();
            var q = c.TranslatePoint(p, r.Window);
            Assert.That(q, Is.Not.Null);
            return q!.Value;
        }

        // ------------------------------------------------------------------ the ruler

        [Test]
        public void TheRulerLinesAreBarsAndBeatsFromEachBarsStart()
        {
            var lines = RollRuler.Lines(0, 32, 4, 16, 24);
            Assert.That(lines.Where(l => l.Kind == RulerMark.Bar).Select(l => (l.Tick, l.Label)), Is.EqualTo(new[] { (0, "1"), (16, "2"), (32, "3") }));
            Assert.That(lines.Where(l => l.Kind == RulerMark.Beat && l.Tick < 16).Select(l => l.Tick), Is.EqualTo(new[] { 4, 8, 12 }));
            Assert.That(lines.First(l => l.Tick == 4).Label, Is.EqualTo("1.2"), "beats are 96 px apart: named");

            // A 3/4 bar of 12 ticks.
            var waltz = RollRuler.Lines(0, 36, 4, 12, 24);
            Assert.That(waltz.Where(l => l.Kind == RulerMark.Bar).Select(l => l.Tick), Is.EqualTo(new[] { 0, 12, 24, 36 }));
            Assert.That(waltz.Where(l => l.Kind == RulerMark.Beat && l.Tick < 12).Select(l => l.Tick), Is.EqualTo(new[] { 4, 8 }));

            // A bar that isn't a whole number of beats (10 ticks, 4 a beat): the next bar starts on its own line.
            var odd = RollRuler.Lines(0, 20, 4, 10, 24);
            Assert.That(odd.Where(l => l.Kind == RulerMark.Bar).Select(l => l.Tick), Is.EqualTo(new[] { 0, 10, 20 }));
            Assert.That(odd.Where(l => l.Kind == RulerMark.Beat && l.Tick is > 0 and < 10).Select(l => l.Tick), Is.EqualTo(new[] { 4, 8 }));

            // Zoomed out: labels thin out (44 px a label) and beats go.
            var far = RollRuler.Lines(0, 160, 4, 16, 1);
            Assert.That(far.Where(l => l.Kind == RulerMark.Beat), Is.Empty, "beats 4 px apart are not drawn");
            Assert.That(far.Where(l => l.Kind == RulerMark.Bar && l.Label.Length > 0).Select(l => l.Label), Is.EqualTo(new[] { "1", "4", "7", "10" }), "16 px a bar: a label every third");
        }

        [AvaloniaTest]
        public void TheRulerFollowsTheSongsTimeSignatureAndTicksPerBeat()
        {
            using var r = new UxP3bTests.Rig(48);
            int tpb = r.Song.Core.TPB;
            try
            {
                r.ShowRoll();
                Place(r, 0);
                var roll = r.Roll;
                roll.RefreshBars();
                Assert.That(roll.Ruler.BarTicks, Is.EqualTo(16), "Buzz's bar");
                Assert.That(roll.Ruler.Tpb, Is.EqualTo(tpb));
                Assert.That(r.Notes.BarTime, Is.EqualTo(16 * TB));
                Assert.That(roll.Ruler.LengthTicks, Is.EqualTo(48));

                r.Song.Core.SongMeter.Set(0, 12);                      // 3 beats of 4 ticks
                roll.RefreshBars();
                Assert.That(roll.Ruler.BarTicks, Is.EqualTo(12), "the song's list");
                Assert.That(r.Notes.BarTime, Is.EqualTo(12 * TB), "the roll's bar lines and its Bar grid too");
                r.Roll.GridBox.SelectedIndex = 2;
                UxP3bTests.Pump();
                Assert.That(r.Notes.Grid, Is.EqualTo(12 * TB), "Grid: bar is the song's bar");

                r.Song.Core.TPB = 6;
                roll.RefreshBars();
                Assert.That(roll.Ruler.Tpb, Is.EqualTo(6), "ticks a beat");
                var bars = RollRuler.Lines(0, 48, roll.Ruler.Tpb, roll.Ruler.BarTicks, 24);
                Assert.That(bars.Where(l => l.Kind == RulerMark.Beat && l.Tick < 12).Select(l => l.Tick), Is.EqualTo(new[] { 6 }), "two beats in a bar of 12 at 6 a beat");

                r.Window.UpdateLayout();
                UxP3bTests.Pump();
                Assert.That(roll.Ruler.Bounds.Height, Is.EqualTo(RollRuler.BarHeight), "the ruler is above the grid");
                Assert.That(roll.Scroll.TranslatePoint(new Point(0, 0), roll.Ruler)!.Value.Y, Is.GreaterThanOrEqualTo(RollRuler.BarHeight - 1), "the grid starts under the ruler");
            }
            finally
            {
                r.Song.Core.TPB = tpb;
                r.Song.Core.SongMeter.Reset();
            }
        }

        // ------------------------------------------------------------------ the playhead

        [AvaloniaTest]
        public void ThePlayheadFollowsThePatternsPlayPositionAndTheViewerWithIt()
        {
            using var r = new UxP3bTests.Rig(128);
            r.ShowRoll();
            var roll = r.Roll;
            var pattern = (PatternCore)r.Pattern;
            r.Window.UpdateLayout();
            UxP3bTests.Pump();
            try
            {
                roll.UpdatePlayhead();
                Assert.That(r.Notes.PlayheadTime, Is.EqualTo(-1), "not playing: no playhead");
                Assert.That(roll.Playhead.LineX, Is.LessThan(0));

                pattern.PlayPosition = 3 * TB;
                roll.UpdatePlayhead();
                Assert.That(r.Notes.PlayheadTime, Is.EqualTo(3 * TB));
                Assert.That(roll.Ruler.Playhead, Is.EqualTo(3 * TB));
                Assert.That(roll.Playhead.LineX, Is.EqualTo(r.Notes.X(3 * TB) - roll.Scroll.Offset.X).Within(0.01), "over the viewer, at the note-grid's x");

                // Far to the right: Follow scrolls to it.
                pattern.PlayPosition = 120 * TB;
                roll.UpdatePlayhead();
                Assert.That(roll.Scroll.Offset.X, Is.GreaterThan(0), "the viewer followed");
                double x = roll.Playhead.LineX;
                Assert.That(x, Is.InRange(PianoRoll.KeysWidth, roll.Scroll.Viewport.Width), "and the playhead is in view");

                // Follow off: the viewer stays.
                roll.FollowBox.IsChecked = false;
                roll.Scroll.Offset = new Vector(0, roll.Scroll.Offset.Y);
                UxP3bTests.Pump();
                pattern.PlayPosition = 121 * TB;
                roll.UpdatePlayhead();
                Assert.That(roll.Scroll.Offset.X, Is.EqualTo(0).Within(0.01), "Follow off");

                pattern.PlayPosition = int.MinValue;
                roll.UpdatePlayhead();
                Assert.That(roll.Playhead.LineX, Is.LessThan(0), "stopped: gone");
            }
            finally
            {
                pattern.PlayPosition = int.MinValue;
            }
        }

        // ------------------------------------------------------------------ the loop brace

        [AvaloniaTest]
        public void DraggingOnTheRulerSetsTheSongsLoopAsOneUndoStep()
        {
            using var r = new UxP3bTests.Rig(16);
            r.ShowRoll();
            var roll = r.Roll;
            var core = r.Song.Core.SongCore;
            r.Window.UpdateLayout();
            UxP3bTests.Pump();

            // The pattern isn't in the song yet: nothing to set.
            roll.RefreshBars();
            Assert.That(roll.Ruler.CanSetLoop, Is.False, "no place in the song, no loop to set");

            Place(r, 16);
            roll.RefreshBars();
            Assert.That(roll.Ruler.CanSetLoop, Is.True);
            int oldStart = core.LoopStart, oldEnd = core.LoopEnd;

            Point Tick(int tick) => InWindow(r, roll.Ruler, new Point(r.Notes.X(tick * TB) - roll.Scroll.Offset.X, 20));
            UxP3bTests.Drag(r, Tick(4), Tick(12));
            Assert.That((core.LoopStart, core.LoopEnd), Is.EqualTo((16 + 4, 16 + 12)), "the pattern's ticks 4-12, where it lies in the song, snapped to beats");
            Assert.That((roll.Ruler.LoopStartTick, roll.Ruler.LoopEndTick), Is.EqualTo((4, 12)), "the brace shows it");

            // One undo step, in the song's history; the roll's Undo takes it first.
            Assert.That(roll.Ruler.LoopOn, Is.EqualTo(r.Song.Core.Looping));
            Assert.That(r.Page.Undo(), Is.True);
            Assert.That((core.LoopStart, core.LoopEnd), Is.EqualTo((oldStart, oldEnd)), "both ends in one step");
            Assert.That(r.Page.Redo(), Is.True);
            Assert.That((core.LoopStart, core.LoopEnd), Is.EqualTo((20, 28)));

            // Drag the brace's right edge to tick 8.
            UxP3bTests.Drag(r, Tick(12), Tick(8));
            Assert.That((core.LoopStart, core.LoopEnd), Is.EqualTo((20, 24)), "the end moved, the start stayed");
            core.ActionStack.Undo();
            Assert.That((core.LoopStart, core.LoopEnd), Is.EqualTo((20, 28)), "one step for that too");
        }

        [AvaloniaTest]
        public void ALoopGestureIsNotTheNotesUndoAndANotesEditEndsItsClaim()
        {
            using var r = new UxP3bTests.Rig(16);
            r.ShowRoll();
            Place(r, 0);
            r.Roll.RefreshBars();
            var core = r.Song.Core.SongCore;
            r.Notes.AddNote(TB, 60);                          // an edit first (the notes' history)
            r.Window.UpdateLayout();
            UxP3bTests.Pump();
            Point Tick(int tick) => InWindow(r, r.Roll.Ruler, new Point(r.Notes.X(tick * TB) - r.Roll.Scroll.Offset.X, 20));
            UxP3bTests.Drag(r, Tick(4), Tick(8));
            Assert.That(core.LoopEnd, Is.EqualTo(8));
            Assert.That(r.Page.Undo(), Is.True);              // the loop (the last gesture)
            Assert.That(r.Notes.Notes.Count, Is.EqualTo(1), "the note stays");
            Assert.That(r.Page.Undo(), Is.True);              // now the note
            Assert.That(r.Column(), Is.Empty);
        }

        // ------------------------------------------------------------------ the velocity lane

        [AvaloniaTest]
        public void DraggingABarSetsThatNotesVelocityAsOneUndoStep()
        {
            using var r = new UxP3bTests.Rig(16);
            r.ShowRoll();
            r.Notes.AddNote(2 * TB, 60);
            r.Notes.AddNote(6 * TB, 64);
            var lane = r.Roll.Velocities;
            Assert.That(lane, Is.Not.Null);
            r.Window.UpdateLayout();
            UxP3bTests.Pump();
            int undos = r.Undos;
            var note = r.Notes.Notes.First(n => n.Note == 64);
            var top = InWindow(r, lane, lane.BarPoint(note));
            var to = new Point(top.X, InWindow(r, lane, new Point(0, lane.YOfVelocity(40))).Y);
            UxP3bTests.Drag(r, top, to);

            var after = r.Notes.Notes.First(n => n.Note == 64);
            Assert.That(after.Velocity, Is.EqualTo(40).Within(2), "the pointer's height, as the velocity");
            Assert.That(r.Notes.Notes.First(n => n.Note == 60).Velocity, Is.EqualTo(100), "the other note untouched");
            Assert.That(r.Undos, Is.EqualTo(undos + 1), "one step for the whole drag");
            Assert.That(r.Page.Undo(), Is.True);
            Assert.That(r.Notes.Notes.First(n => n.Note == 64).Velocity, Is.EqualTo(100), "undone");
            Assert.That(r.Page.Redo(), Is.True);
            Assert.That(r.Notes.Notes.First(n => n.Note == 64).Velocity, Is.EqualTo(after.Velocity));
        }

        [AvaloniaTest]
        public void WithSeveralNotesSelectedTheDragChangesThemByTheSameAmount()
        {
            using var r = new UxP3bTests.Rig(16);
            r.ShowRoll();
            r.Notes.AddNote(2 * TB, 60);
            r.Notes.AddNote(6 * TB, 64);
            // Different velocities to begin with: 100 and then 60 for the second.
            r.Notes.SelectOnly(new[] { 1 });
            r.Notes.ChangeVelocity(-40);
            r.Notes.SelectAll();
            var lane = r.Roll.Velocities;
            r.Window.UpdateLayout();
            UxP3bTests.Pump();
            var before = r.Notes.Notes.Select(n => n.Velocity).ToArray();
            Assert.That(before.OrderBy(v => v), Is.EqualTo(new[] { 60, 100 }));
            int undos = r.Undos;
            var hit = r.Notes.Notes.First(n => n.Velocity == 60);
            var down = InWindow(r, lane, lane.BarPoint(hit));
            var up = new Point(down.X, down.Y - 10);                                   // up by 10 px
            UxP3bTests.Drag(r, down, up);
            int delta = lane.VelocityOfY(lane.YOfVelocity(60) - 10) - 60;
            Assert.That(delta, Is.GreaterThan(0));
            var now = r.Notes.Notes.ToDictionary(n => n.Note, n => n.Velocity);
            Assert.That(now[64], Is.EqualTo(60 + delta).Within(1), "the one dragged");
            Assert.That(now[60], Is.EqualTo(Math.Min(127, 100 + delta)).Within(1), "the other by the same amount, relative");
            Assert.That(r.Undos, Is.EqualTo(undos + 1), "still one step");
            Assert.That(r.Notes.Selection.Count, Is.EqualTo(2), "both stay selected");
        }

        // ------------------------------------------------------------------ cut, copy, paste

        [AvaloniaTest]
        public void CopyPasteAndCutKeepPitchAndSpacingAndEachIsOneUndoStep()
        {
            using var r = new UxP3bTests.Rig(16);
            PianoRoll.ClearClipboard();
            try
            {
                r.ShowRoll();
                r.Notes.AddNote(2 * TB, 60);
                r.Notes.AddNote(4 * TB, 67);
                r.Notes.SelectAll();
                int undos = r.Undos;

                Assert.That(r.Page.Copy(), Is.True, "Edit > Copy goes to the roll");
                Assert.That(r.Undos, Is.EqualTo(undos), "copying is no edit");
                Assert.That(PianoRoll.Clipboard.Select(n => (n.Time, n.Note)), Is.EqualTo(new[] { (0, 60), (2 * TB, 67) }), "relative to the first");

                Assert.That(r.Notes.Paste(8 * TB), Is.True);
                Assert.That(r.Notes.Notes.Select(n => (n.Time, n.Note)).OrderBy(n => n.Time),
                    Is.EqualTo(new[] { (2 * TB, 60), (4 * TB, 67), (8 * TB, 60), (10 * TB, 67) }));
                Assert.That(r.Notes.Selection.Count, Is.EqualTo(2), "the pasted notes are selected");
                Assert.That(r.Undos, Is.EqualTo(undos + 1), "one step");
                r.Page.Undo();
                Assert.That(r.Notes.Notes.Count, Is.EqualTo(2), "undone: only the paste");

                // Cut: the notes go, one step; they paste back.
                r.Notes.SelectAll();
                Assert.That(r.Page.Cut(), Is.True);
                Assert.That(r.Notes.Notes, Is.Empty);
                Assert.That(r.Undos, Is.EqualTo(undos + 1), "one step");
                r.Notes.InsertTime = 4 * TB;
                Assert.That(r.Page.Paste(), Is.True);
                Assert.That(r.Notes.Notes.Select(n => (n.Time, n.Note)).OrderBy(n => n.Time), Is.EqualTo(new[] { (4 * TB, 60), (6 * TB, 67) }), "at the last click");

                // The keys: ⌘C / ⌘V (Ctrl off a Mac).
                Assert.That(r.Notes.HandleKey(Key.C, KeyModifiers.Control), Is.True);
                r.Notes.InsertTime = 10 * TB;
                Assert.That(r.Notes.HandleKey(Key.V, KeyModifiers.Control), Is.True);
                Assert.That(r.Notes.Notes.Count, Is.EqualTo(4));
            }
            finally
            {
                PianoRoll.ClearClipboard();
            }
        }

        [AvaloniaTest]
        public void PasteGoesToThePlayheadWhilePlayingAndPastesNothingFromAnEmptyClipboard()
        {
            using var r = new UxP3bTests.Rig(16);
            var pattern = (PatternCore)r.Pattern;
            PianoRoll.ClearClipboard();
            try
            {
                r.ShowRoll();
                Assert.That(r.Notes.Paste(), Is.False, "nothing copied, nothing pasted");
                r.Notes.AddNote(TB, 60);
                r.Notes.SelectAll();
                r.Notes.CopySelected();
                pattern.PlayPosition = 8 * TB + 30;
                r.Roll.UpdatePlayhead();
                Assert.That(r.Notes.PasteTime(), Is.EqualTo(8 * TB), "the playhead, on the grid");
                r.Notes.Paste();
                Assert.That(r.Notes.Notes.Select(n => n.Time), Is.EquivalentTo(new[] { TB, 8 * TB }));

                // A note past the pattern's end is dropped, one that runs over is cut to fit.
                r.Notes.SelectAll();
                r.Notes.CopySelected();
                Assert.That(r.Notes.Paste(15 * TB), Is.True);
                Assert.That(r.Notes.Notes.All(n => n.End <= 16 * TB), Is.True);
            }
            finally
            {
                pattern.PlayPosition = int.MinValue;
                PianoRoll.ClearClipboard();
            }
        }

        // ------------------------------------------------------------------ the song, contrast

        [AvaloniaTest]
        public void LookingAtTheRulerThePlayheadAndTheLaneChangesNoByteOfTheSong()
        {
            using var r = new UxP3bTests.Rig(16);
            string dir = Path.Combine(Path.GetTempPath(), $"rebuzz-p3b2-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            var pattern = (PatternCore)r.Pattern;
            try
            {
                string a = Path.Combine(dir, "a.bmx"), b = Path.Combine(dir, "b.bmx");
                r.Song.Core.SaveSongCopy(a);
                r.ShowRoll();
                pattern.PlayPosition = 5 * TB;
                r.Roll.UpdatePlayhead();
                pattern.PlayPosition = int.MinValue;
                r.Roll.UpdatePlayhead();
                r.Roll.RefreshBars();
                r.Notes.HandleKey(Key.C, KeyModifiers.Control);       // copying: no edit
                r.Window.UpdateLayout();
                UxP3bTests.Pump();
                r.Song.Core.SaveSongCopy(b);
                Assert.That(File.ReadAllBytes(b), Is.EqualTo(File.ReadAllBytes(a)));
            }
            finally
            {
                pattern.PlayPosition = int.MinValue;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void TheRulersAndLanesTextMeetsTheContrastFloorOnTheTokens()
        {
            Assert.That(StudioTokens.Contrast(StudioTokens.TextPrimary, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(4.5), "bar numbers on the ruler");
            Assert.That(StudioTokens.Contrast(StudioTokens.TextSecondary, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(4.5), "beat numbers, the Bar and Velocity labels");
            Assert.That(StudioTokens.Contrast(StudioTokens.TextPrimary, StudioTokens.SurfaceView), Is.GreaterThanOrEqualTo(4.5), "the lane's value");
            Assert.That(StudioTokens.Contrast(StudioTokens.TextSecondary, StudioTokens.SurfaceView), Is.GreaterThanOrEqualTo(4.5), "the Follow button's text and the summary");
            Assert.That(StudioTokens.Contrast(StudioTokens.Play, Avalonia.Media.Colors.Black), Is.GreaterThanOrEqualTo(3), "the playhead against its dark edge");
            Assert.That(StudioTokens.Contrast(StudioTokens.Accent, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(3), "the brace on the ruler");
        }
    }
}
