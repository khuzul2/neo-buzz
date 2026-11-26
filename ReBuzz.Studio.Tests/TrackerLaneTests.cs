// Fork (docs/specs/clips.md T2): the tracker's parameter lanes. A toggle in each parameter column's header opens a vertical envelope strip beside
// the column, at the tracker's row scale, which draws and edits the same lane of the clip store as the column (and, read only, as the Detail
// panel's envelope lane shows). Headless, against the real studio: the toggle, the drawing, each gesture as one step of the song's history with
// the lane, the column cells and the Detail panel's model agreeing, the saved view, and what a frame allocates.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Detail;
using ReBuzz.Studio.Views.Session;
using ReBuzz.Studio.Shell;
using static ReBuzz.Studio.Tests.AutomationLaneTests;

namespace ReBuzz.Studio.Tests
{
    public class TrackerLaneTests
    {
        const int TB = ClipEvent.TimeBase;
        const string Voice = "Voice mode";

        static ReBuzzCore Core => AutomationLaneTests.Core;

        static ColumnKey KeyOf(IMachine machine, string name) => AutomationParams.List(machine).First(p => p.Name == name).Key;

        /// <summary>Lead's clip "00" open in the Tracker (maximised over the window) with the named columns shown.</summary>
        sealed class Open
        {
            public Rig Rig;
            public IMachine Machine;
            public IClip Clip;
            public dynamic View;
            public dynamic Grid => View.TrackerGrid;

            public static Open Tracker(Rig rig, params string[] columns)
            {
                var machine = rig.Lane("Lead").Machine;
                machine.CreateClip("00", 16);
                var clip = machine.Clips.First(p => p.Name == "00");
                rig.Window.Detail.Show(true);
                Settle();
                ClipView.Edit(clip);
                Settle();
                var o = new Open { Rig = rig, Machine = machine, Clip = clip, View = ClipView.Page.EditorControl };
                foreach (var name in columns) o.View.ShowAutomation(name);
                Settle();
                return o;
            }

            public (int Set, int Column) Find(string label)
            {
                dynamic vm = View.Editor.SelectedMachine.SelectedClip;
                for (int i = 0; i < ((System.Collections.ICollection)vm.ColumnSets).Count; i++)
                    for (int j = 0; j < ((System.Collections.ICollection)vm.ColumnSets[i].Columns).Count; j++)
                        if ((string)vm.ColumnSets[i].Columns[j].Label == label) return (i, j);
                var all = new System.Collections.Generic.List<string>();
                for (int i = 0; i < ((System.Collections.ICollection)vm.ColumnSets).Count; i++)
                    for (int j = 0; j < ((System.Collections.ICollection)vm.ColumnSets[i].Columns).Count; j++) all.Add((string)vm.ColumnSets[i].Columns[j].Label);
                Assert.Fail("no column " + label + " among " + string.Join("|", all) + " of " + (string)View.Editor.SelectedMachine.Machine.Name);
                return (0, 0);
            }

            public Point ToggleAt((int Set, int Column) c)
            {
                Settle();
                var rect = (Rect)Grid.LaneToggleRect(c.Set, c.Column);
                var p = ((Avalonia.Controls.Control)Grid.Header).TranslatePoint(rect.Center, Rig.Window);
                Assert.That(p, Is.Not.Null);
                return p!.Value;
            }

            public void Toggle(string label) => Rig.Click(ToggleAt(Find(label)));

            public TrackerLane Lane(string label)
            {
                var (s, c) = Find(label);
                return (TrackerLane)Grid.LaneOf(s, c);
            }

            public Point P(TrackerLane lane, double row, double fraction)
            {
                Settle();
                var p = lane.TranslatePoint(lane.At((int)Math.Round(row * TB), lane.Param.ValueAt(fraction)), Rig.Window);
                Assert.That(p, Is.Not.Null);
                return p!.Value;
            }

            public Point PointP(TrackerLane lane, int time)
            {
                Settle();
                return lane.TranslatePoint(lane.PointOf(time), Rig.Window)!.Value;
            }
        }

        static string Events(IClip clip, ColumnKey key) =>
            string.Join(" ", StoreLanes.OwnLane(clip, key, false)?.GetEvents(int.MinValue, int.MaxValue).OrderBy(e => e.Time).Select(e => $"{e.Time}:{e.Value}:{e.Duration}") ?? Array.Empty<string>());

        static string LaneMeta(IClip clip, ColumnKey key) =>
            StoreLanes.OwnLane(clip, key, false) is { } l && l.Metadata.TryGetValue(ClipMeta.Lane, out var v) ? v : null;

        static string Pts(System.Collections.Generic.IEnumerable<AutoPoint> points) => string.Join(" ", points.Select(p => $"{p.Time}:{p.Value}{(p.Ramp ? "/" : "-")}"));

        /// <summary>The column's cells: the (time, value) of every row that holds a value, read from the grid's own column.</summary>
        static string Cells(Open o, (int Set, int Column) c)
        {
            dynamic column = o.View.Editor.SelectedMachine.SelectedClip.ColumnSets[c.Set].Columns[c.Column];
            var list = new System.Collections.Generic.List<string>();
            for (int beat = 0; beat < 4; beat++)
            {
                object b = column.FetchBeat(beat);       // the editor's own (internal) beat type: read by reflection
                var rows = (System.Collections.IList)Prop(b, "Rows");
                foreach (var row in rows)
                    if (Prop(row, "Type").ToString() != "NoValue") list.Add($"{(int)Prop(row, "Time") + beat * 4 * TB}:{(int)Prop(row, "Value")}");
            }
            return string.Join(" ", list);
        }

        static object Prop(object o, string name) =>
            o.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(o)
            ?? o.GetType().GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(o);

        static string Timed(System.Collections.Generic.IEnumerable<AutoPoint> points) => string.Join(" ", points.Select(p => $"{p.Time}:{p.Value}"));

        // ------------------------------------------------------------------ the toggle

        [AvaloniaTest]
        public void ATogglePerParameterColumnOpensAndClosesAStripBesideTheColumn()
        {
            using var rig = new Rig("Lead");
            var o = Open.Tracker(rig, Voice, Cutoff);
            Assert.That((bool)o.Grid.HasLaneToggles, Is.True, "the header has its row of toggles");
            Assert.That((int)o.Grid.LaneControls.Count, Is.EqualTo(0), "every lane starts closed");
            var voice = o.Find(Voice);
            var cutoff = o.Find(Cutoff);
            double nextX = o.Grid.ColumnX(cutoff.Set, cutoff.Column);

            o.Toggle(Voice);
            Assert.That((bool)o.Grid.IsLaneOpen(voice.Set, voice.Column), Is.True, "a click on the toggle opens the lane");
            var lane = o.Lane(Voice);
            Assert.That(lane, Is.Not.Null);
            Rect strip = o.Grid.LaneStrip(voice.Set, voice.Column);
            Assert.That(strip.Width, Is.InRange(48.0, 64.0), "a narrow strip: about 48 to 64 px at 100 %");
            Assert.That(lane.Bounds.Width, Is.EqualTo(strip.Width).Within(0.5));
            double shifted = o.Grid.ColumnX(cutoff.Set, cutoff.Column);
            if (cutoff.Set == voice.Set) Assert.That(shifted - nextX, Is.EqualTo(strip.Width).Within(0.5), "the columns after it move over by the strip");
            Assert.That((bool)o.Grid.IsLaneOpen(cutoff.Set, cutoff.Column), Is.False, "the other column's lane stays closed");

            // The strip scrolls with the rows: it is in the grid's content, level with the first row.
            var body = (Avalonia.Controls.Control)o.Grid.Body;
            Assert.That(lane.TranslatePoint(new Point(0, 0), rig.Window)!.Value.Y, Is.EqualTo(body.TranslatePoint(new Point(0, 0), rig.Window)!.Value.Y).Within(0.5));

            o.Toggle(Voice);
            Assert.That((bool)o.Grid.IsLaneOpen(voice.Set, voice.Column), Is.False, "a second click closes it");
            Assert.That(o.Grid.LaneOf(voice.Set, voice.Column), Is.Null);
            Assert.That((double)o.Grid.ColumnX(cutoff.Set, cutoff.Column), Is.EqualTo(nextX).Within(0.5), "the column alone, as it was");
        }

        [AvaloniaTest]
        public void NoteColumnsGetNoToggle()
        {
            using var rig = new Rig("Lead");
            var o = Open.Tracker(rig, Voice);
            dynamic vm = o.View.Editor.SelectedMachine.SelectedClip;
            bool sawNote = false;
            for (int i = 0; i < ((System.Collections.ICollection)vm.ColumnSets).Count; i++)
                for (int j = 0; j < ((System.Collections.ICollection)vm.ColumnSets[i].Columns).Count; j++)
                    if (vm.ColumnSets[i].Columns[j].Type.ToString() == "Note")
                    {
                        sawNote = true;
                        Assert.That((bool)o.Grid.HasLaneToggle(i, j), Is.False, "a note column has no lane");
                    }
            Assert.That(sawNote, Is.True, "the clip has a note column");
            var voice = o.Find(Voice);
            Assert.That((bool)o.Grid.HasLaneToggle(voice.Set, voice.Column), Is.True, "a state parameter has one");
        }

        // ------------------------------------------------------------------ the lane draws the column's own data

        /// <summary>What the engine plays at a time from raw events (the oracle): the last event at or before it; a ramp followed along its line to the next.</summary>
        static int? Expected(ClipEvent[] events, int time)
        {
            var sorted = events.OrderBy(e => e.Time).ToArray();
            int at = -1;
            for (int i = 0; i < sorted.Length; i++) if (sorted[i].Time <= time) at = i;
            if (at < 0) return null;
            var e = sorted[at];
            if (e.Duration > 0 && e.Time != time && at + 1 < sorted.Length)
            {
                var n = sorted[at + 1];
                double v = e.Value + (n.Value - e.Value) * (time - e.Time) / (double)(n.Time - e.Time);
                return (int)Math.Round(v, MidpointRounding.AwayFromZero);
            }
            return e.Value;
        }

        [AvaloniaTest]
        public void ALaneWithStepsAndRampsWrittenThroughTheStoreShowsTheRightValueAtEachRow()
        {
            using var rig = new Rig("Lead");
            var o = Open.Tracker(rig, Cutoff);
            var key = KeyOf(o.Machine, Cutoff);
            var param = AutomationParams.Find(o.Machine, key);
            int lo = param.Min, hi = param.Max, span = hi - lo;
            // Steps at rows 2 and 11, a ramp from row 4 to row 8 and on to a step at 8, a ramp from 12 to 15.
            var written = new[]
            {
                new ClipEvent(2 * TB, lo + span / 4, 0),
                new ClipEvent(4 * TB, lo + span / 10, 4 * TB),
                new ClipEvent(8 * TB, lo + span * 9 / 10, 0),
                new ClipEvent(11 * TB, lo + span / 2, 0),
                new ClipEvent(12 * TB, lo + span / 5, 3 * TB),
                new ClipEvent(15 * TB, lo + span * 4 / 5, 0),
            };
            var home = StoreLanes.OwnLane(o.Clip, key, create: true);
            Assert.That(home, Is.Not.Null);
            home.SetEvents(written, true);
            o.Grid.SetLane(o.Find(Cutoff).Set, o.Find(Cutoff).Column, true);
            Settle();
            var lane = o.Lane(Cutoff);
            Assert.That(lane, Is.Not.Null);
            lane.Reload(force: true);

            Assert.That(Pts(lane.Envelope.Points), Is.EqualTo(Pts(written.Select(e => new AutoPoint(e.Time, e.Value, e.Duration > 0)))), "the lane reads the column's own events, ramps and steps");

            // Row by row: the model's value is the oracle's, and the engine's own ramp reading agrees on a ramp.
            var column = (ClipColumnCore)home;
            for (int row = 0; row < 16; row++)
            {
                int time = row * TB;
                var want = Expected(written, time);
                var got = AutomationMath.ValueAt(lane.Envelope.Points, time);
                Assert.That(got, Is.EqualTo(want), $"row {row}");
                if (column.TryRampValue(time, out int played)) Assert.That(played, Is.EqualTo(want), $"row {row}: the engine plays the same ramp");
            }

            // Where the strip is: a point of the lane is level with its row in the grid, and across by its value.
            var c = o.Find(Cutoff);
            foreach (var e in written)
            {
                int row = e.Time / TB;
                var at = lane.At(e.Time, e.Value);
                double rowH = ((Point)o.Grid.FieldCenter(c.Set, c.Column, (row + 1) / 4, (row + 1) % 4)).Y - ((Point)o.Grid.FieldCenter(c.Set, c.Column, row / 4, row % 4)).Y;
                double top = ((Point)o.Grid.FieldCenter(c.Set, c.Column, row / 4, row % 4)).Y - Math.Abs(rowH) / 2;
                if (row < 15) Assert.That(at.Y, Is.EqualTo(top).Within(1.0), $"row {row} is where the grid has it");
                Assert.That(at.X, Is.InRange(0.0, lane.Bounds.Width), "inside the strip");
            }
            Assert.That(lane.At(0, lo).X, Is.LessThan(lane.At(0, hi).X), "values run left to right from the minimum to the maximum");

            // Drawn: at each point's place the frame holds the curve's colour, and a value away from it does not.
            var fill = ((ISolidColorBrush)PianoRoll.NoteFill).Color;
            Assert.That(rig.Window.CaptureRenderedFrame(), Is.Not.Null);
            foreach (var e in written)
            {
                var onPoint = lane.TranslatePoint(lane.At(e.Time, e.Value), rig.Window)!.Value;
                var inGrid = lane.TranslatePoint(lane.At(e.Time, e.Value), (Avalonia.Visual)o.Grid)!.Value;
                if (inGrid.Y < 6 || inGrid.Y > ((Avalonia.Controls.Control)o.Grid).Bounds.Height - 6) continue;       // scrolled out of the view
                var off = lane.TranslatePoint(lane.At(e.Time, e.Value > lo + span / 2 ? lo : hi), rig.Window)!.Value;
                var px = PixelAt(rig, onPoint);
                Assert.That(Math.Min(Distance(px, fill), Distance(px, Colors.White)), Is.LessThan(40), $"the point at row {e.Time / TB} is drawn (a ramp point filled, a step point white)");
                if (Math.Abs(off.X - onPoint.X) > 14) Assert.That(Distance(PixelAt(rig, off), fill), Is.GreaterThan(60), $"and nothing is at the other end of row {e.Time / TB}");
            }
            // A ramp's middle: the line passes through the value the engine plays between its points.
            var mid = lane.TranslatePoint(lane.At(6 * TB, Expected(written, 6 * TB)!.Value), rig.Window)!.Value;
            Assert.That(NearestDistance(rig, mid, fill), Is.LessThan(50), "the ramp's line goes through the row between its points");
        }

        static Color PixelOf(Avalonia.Media.Imaging.WriteableBitmap bitmap, Point at)
        {
            using var fb = bitmap.Lock();
            bool rgba = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888;
            int p = System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address + (int)at.Y * fb.RowBytes + (int)at.X * 4);
            return rgba ? Color.FromRgb((byte)(p & 0xFF), (byte)((p >> 8) & 0xFF), (byte)((p >> 16) & 0xFF)) : Color.FromRgb((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF));
        }

        static Color PixelAt(Rig rig, Point at)
        {
            foreach (var v in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(rig.Window)) v.InvalidateVisual();
            Settle();
            using var frame = rig.Window.CaptureRenderedFrame();
            return PixelOf(frame!, at);
        }

        static int NearestDistance(Rig rig, Point at, Color want)
        {
            foreach (var v in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(rig.Window)) v.InvalidateVisual();
            Settle();
            using var frame = rig.Window.CaptureRenderedFrame();
            int best = int.MaxValue;
            for (int dx = -4; dx <= 4; dx++)
                best = Math.Min(best, Distance(PixelOf(frame!, new Point(at.X + dx, at.Y)), want));
            return best;
        }

        static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        // ------------------------------------------------------------------ one curve: the lane, the column and the Detail panel

        [AvaloniaTest]
        public void EveryGestureOfTheLaneIsOneStepOfTheHistoryAndTheColumnAndTheDetailPanelAgree()
        {
            using var rig = new Rig("Lead");
            var o = Open.Tracker(rig, Cutoff);
            var key = KeyOf(o.Machine, Cutoff);
            var c = o.Find(Cutoff);
            o.Toggle(Cutoff);
            var lane = o.Lane(Cutoff);
            Assert.That(lane, Is.Not.Null);
            lane.Focus();
            Settle();

            void Agree(string what)
            {
                Settle();
                lane.Reload(force: true);
                string model = Pts(lane.Envelope.Points);
                var detail = ClipEnvelopes.Read(o.Clip, key);
                string seen = detail.Tracker == null ? "" : Pts(detail.Tracker.Points);
                Assert.That(seen, Is.EqualTo(model), what + ": the Detail panel's envelope model shows the lane's curve");
                Assert.That(Cells(o, c), Is.EqualTo(Timed(lane.Envelope.Points.Where(p => p.Time % TB == 0))), what + ": the column's cells hold the lane's values on its rows (an off-row point is the lane's own)");
                var raw = StoreLanes.OwnLane(o.Clip, key, false)?.GetEvents(int.MinValue, int.MaxValue).OrderBy(e => e.Time).ToArray() ?? Array.Empty<ClipEvent>();
                Assert.That(Pts(raw.Select(e => new AutoPoint(e.Time, e.Value, e.Duration > 0))), Is.EqualTo(model), what + ": and so is the store");
            }

            void Step(string what, Action gesture)
            {
                string before = Events(o.Clip, key);
                gesture();
                Settle();
                string after = Events(o.Clip, key);
                Assert.That(after, Is.Not.EqualTo(before), what + ": it changed the clip");
                Agree(what);
                rig.Undo();
                Settle();
                Assert.That(Events(o.Clip, key), Is.EqualTo(before), what + ": one undo step brings it back, bit for bit");
                Agree(what + " undone");
                rig.Redo();
                Settle();
                Assert.That(Events(o.Clip, key), Is.EqualTo(after), what + ": redo does it again");
                Agree(what + " redone");
                lane.Focus();
                Settle();
            }

            // Click adds a point (a ramp), on a row.
            Step("add", () => rig.Click(o.P(lane, 4, 0.2)));
            Step("add another", () => rig.Click(o.P(lane, 8, 0.8)));
            Step("add a third", () => rig.Click(o.P(lane, 12, 0.4)));
            Assert.That(lane.Envelope.Points.Select(p => p.Time / TB), Is.EqualTo(new[] { 4, 8, 12 }));
            Assert.That(lane.Envelope.Points.All(p => p.Ramp), Is.True, "a new point ramps to the next");
            Assert.That(lane.Envelope.Points.All(p => p.Time % TB == 0), Is.True, "on rows");

            // Drag a point: it snaps to the rows.
            string beforeDrag = Pts(lane.Envelope.Points);
            Step("drag", () => rig.Drag(o.PointP(lane, 8 * TB), o.P(lane, 10.1, 0.5)));
            Assert.That(lane.Envelope.Points.Select(p => p.Time / TB), Is.EqualTo(new[] { 4, 10, 12 }), "to the row nearest the pointer");
            Assert.That(lane.Envelope.Points.All(p => p.Time % TB == 0), Is.True);

            // Alt: no snap.
            Step("alt-click", () => rig.Click(o.P(lane, 6.4, 0.6), mods: RawInputModifiers.Alt));
            Assert.That(lane.Envelope.Points.Any(p => p.Time % TB != 0), Is.True, "Alt puts it between rows");

            // R makes the selection steps, then ramps again.
            lane.SelectTimes(lane.Envelope.Points.Select(p => p.Time));
            Step("R to steps", () => rig.Key(PhysicalKey.R));
            Assert.That(lane.Envelope.Points.All(p => !p.Ramp), Is.True, "R: steps");
            Assert.That(StoreLanes.OwnLane(o.Clip, key, false).GetEvents(int.MinValue, int.MaxValue).All(e => e.Duration == 0), Is.True, "a step has no duration in the store");
            lane.SelectTimes(lane.Envelope.Points.Select(p => p.Time));
            Step("R to ramps", () => rig.Key(PhysicalKey.R));
            Assert.That(lane.Envelope.Points.Take(lane.Envelope.Points.Count - 1).All(p => p.Ramp), Is.True, "R again: ramps");

            // Delete removes the selection; a double click removes a point.
            lane.SelectTimes(new[] { 4 * TB });
            Step("delete", () => rig.Key(PhysicalKey.Delete));
            Assert.That(lane.Envelope.Points.Any(p => p.Time == 4 * TB), Is.False);
            Step("double click", () => rig.DoubleClick(o.PointP(lane, 12 * TB)));
            Assert.That(lane.Envelope.Points.Any(p => p.Time == 12 * TB), Is.False);
        }

        [AvaloniaTest]
        public void AValueTypedInTheColumnIsAPointInTheLaneAndInTheDetailPanel()
        {
            using var rig = new Rig("Lead");
            var o = Open.Tracker(rig, Voice);
            var key = KeyOf(o.Machine, Voice);
            var c = o.Find(Voice);
            o.Toggle(Voice);
            var lane = o.Lane(Voice);
            Assert.That(lane.Envelope.Points, Is.Empty);

            ((Avalonia.Controls.Control)o.Grid.Body).Focus();
            Settle();
            EditorNav.GoTo((object)o.View, Voice, Settle);
            for (int i = 0; i < 4; i++) rig.Key(PhysicalKey.ArrowDown);
            rig.Key(PhysicalKey.ArrowRight);
            rig.Key(PhysicalKey.Digit2);
            Settle();

            Assert.That(Timed(lane.Envelope.Points), Is.EqualTo($"{4 * TB}:2"), "the lane shows what was typed, at once");
            Assert.That(Timed(ClipEnvelopes.Read(o.Clip, key).Tracker.Points), Is.EqualTo($"{4 * TB}:2"), "and so does the Detail panel's model");
            Assert.That(Cells(o, c), Is.EqualTo($"{4 * TB}:2"));
            Assert.That(TrackerLane.Focused, Is.Null, "the grid has the keyboard, not the lane");
            rig.Undo();
            Settle();
            Assert.That(lane.Envelope.Points, Is.Empty, "the editor's own undo takes it out of the lane too");
            Assert.That(ClipEnvelopes.Read(o.Clip, key).Tracker, Is.Null);
        }

        [AvaloniaTest]
        public void TheDetailPanelsEnvelopeStillWritesItsOwnLaneAndTheTrackersLaneIsLeftAlone()
        {
            using var rig = new Rig("Lead");
            var o = Open.Tracker(rig, Cutoff);
            var key = KeyOf(o.Machine, Cutoff);
            o.Toggle(Cutoff);
            var lane = o.Lane(Cutoff);
            rig.Click(o.P(lane, 4, 0.5));
            string own = Events(o.Clip, key);
            // EV1's editor on the same clip writes the marked lane; the tracker's lane keeps its events.
            var envelope = ClipEnvelopes.Read(o.Clip, key);
            new ClipEnvelopeEditor(Core).AddPoint(envelope, 8 * TB, envelope.Param.Min);
            Settle();
            Assert.That(Events(o.Clip, key), Is.EqualTo(own), "the tracker's lane is the column's own; EV1's points are the marked lane's");
            lane.Reload(force: true);
            Assert.That(lane.Envelope.Points.Count, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------ saved with the song

        [AvaloniaTest]
        public void WhichLanesAreOpenIsSavedWithTheSongAndASongFromBeforeOpensWithEveryLaneClosed()
        {
            using var rig = new Rig("Lead");
            var o = Open.Tracker(rig, Voice, Cutoff);
            o.Toggle(Voice);
            o.Toggle(Cutoff);
            var voiceKey = KeyOf(o.Machine, Voice);
            var cutoffKey = KeyOf(o.Machine, Cutoff);
            var cutoff = o.Lane(Cutoff);
            rig.Click(o.P(cutoff, 4, 0.7));
            o.Toggle(Cutoff);       // closed again: only Voice mode stays open
            Assert.That(StoreLanes.OwnLane(o.Clip, voiceKey, false).Metadata[ClipMeta.Lane], Is.EqualTo("1"));
            Assert.That(StoreLanes.OwnLane(o.Clip, cutoffKey, false).Metadata.ContainsKey(ClipMeta.Lane), Is.False);
            string path = Path.Combine(Path.GetTempPath(), "tracker-lanes-" + Guid.NewGuid().ToString("N") + ".nbl");
            try
            {
                Core.SaveSongCopy(path);
                rig.Dispose();
                AudioHygieneTests.NewSong();
                Dialogs.Responder = (m, cc, b) => UserAnswer.No;
                try { Assert.That(Core.TryOpenSongFile(path), Is.True, "reopened"); }
                finally { Dialogs.Responder = null; }
                var machine = Core.SongCore.MachinesList.First(m => m.Name == "Lead");
                var clip = machine.Clips.First(p => p.Name == "00");
                Assert.That(LaneMeta(clip, voiceKey), Is.EqualTo("1"), "the open lane survived");
                Assert.That(StoreLanes.OwnLane(clip, cutoffKey, false)?.Metadata.ContainsKey(ClipMeta.Lane) ?? false, Is.False, "the closed one stayed closed");
                Assert.That(Events(clip, cutoffKey), Is.Not.Empty, "and its points are there");

                var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                window.Show();
                window.Detail.Show(true);
                Settle();
                ClipView.Edit(clip);
                Settle();
                dynamic view = ClipView.Page.EditorControl;
                var v = new Open { Rig = null, Machine = machine, Clip = clip, View = view };
                var (s, col) = v.Find(Voice);
                Assert.That((int)v.Grid.LaneControls.Count, Is.EqualTo(1), $"one lane is open in the tracker (open {v.Grid.IsLaneOpen(s, col)}, toggle {v.Grid.HasLaneToggle(s, col)}, provider {ClipLaneHost.Provider != null})");
                Assert.That((bool)v.Grid.IsLaneOpen(s, col), Is.True, "Voice mode's, shown although its column holds no value");
                window.Close();
            }
            finally { File.Delete(path); }
        }

        [AvaloniaTest]
        public void SongsSavedBeforeLanesExistOpenWithEveryLaneClosed()
        {
            string template = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Other", "Templates", "Songs", "Bass House Template.nbl");
            if (!File.Exists(template)) template = new[] { "Other/Templates/Songs/Bass House Template.nbl" }.Select(p => Path.GetFullPath(Path.Combine(FindRoot(), p))).First();
            Assert.That(File.Exists(template), Is.True, template);
            AudioHygieneTests.NewSong();
            Dialogs.Responder = (m, cc, b) => UserAnswer.No;
            try { Assert.That(Core.TryOpenSongFile(template), Is.True, "the template (saved before T2) opens"); }
            finally { Dialogs.Responder = null; }
            int lanes = 0, open = 0;
            foreach (var machine in Core.SongCore.MachinesList)
                foreach (var clip in machine.Clips)
                    foreach (var column in clip.Columns)
                    {
                        lanes++;
                        if (column.Metadata.ContainsKey(ClipMeta.Lane)) open++;
                    }
            Assert.That(lanes, Is.GreaterThan(0), "the song has lanes");
            Assert.That(open, Is.EqualTo(0), "none of them has a lane open");
        }

        static string FindRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ReBuzz.sln"))) dir = dir.Parent;
            return dir?.FullName ?? ".";
        }

        // ------------------------------------------------------------------ the draw path

        [AvaloniaTest]
        public void ScrollingAndThePlayPositionRedrawTheLaneWithoutMakingAnythingNew()
        {
            using var rig = new Rig("Lead");
            var o = Open.Tracker(rig, Cutoff);
            var key = KeyOf(o.Machine, Cutoff);
            var param = AutomationParams.Find(o.Machine, key);
            StoreLanes.OwnLane(o.Clip, key, create: true).SetEvents(new[]
            {
                new ClipEvent(2 * TB, param.Min, 4 * TB), new ClipEvent(6 * TB, param.Max, 0), new ClipEvent(10 * TB, param.Min + 1, 2 * TB), new ClipEvent(13 * TB, param.Max - 1, 0),
            }, true);
            var c = o.Find(Cutoff);
            o.Grid.SetLane(c.Set, c.Column, true);
            Settle();
            var lane = o.Lane(Cutoff);
            lane.Reload(force: true);
            lane.Prepare();
            int builds = lane.Builds;

            // The geometry is built once and kept while only the view moves.
            for (int i = 0; i < 30; i++)
            {
                o.Grid.ScrollBeatsTo(i % 2 == 0 ? 0.0 : 40.0);
                Settle();
            }
            Assert.That(lane.Builds, Is.EqualTo(builds), "scrolling made no new geometry");

            // What the prepare step allocates at steady state: nothing.
            lane.Prepare();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) lane.Prepare();
            long used = GC.GetAllocatedBytesForCurrentThread() - before;
            TestContext.Out.WriteLine($"Prepare x1000 at steady state: {used} bytes");
            Assert.That(used, Is.EqualTo(0), "no allocation per frame in the lane's own drawing code when nothing changed");
        }
    }
}
