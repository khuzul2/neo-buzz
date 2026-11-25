// Fork (docs/specs/daw-modes.md EV1): clip envelopes. The Detail panel's lane edits the engine automation column of the clip shown there,
// for a Session clip and an Arrangement clip alike (they are one pattern); the clips of the Arrangement and the Session slots draw a
// read-only curve of the parameter their track chose. Headless, against the real studio: each gesture's value, undo and redo, the file
// formats, the overlap marking, the per-track choice, and the exit test: the lane's curve renders bit for bit as AR4's.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Detail;
using ReBuzz.Studio.Views.Sequence;
using static ReBuzz.Studio.Tests.AutomationLaneTests;

namespace ReBuzz.Studio.Tests
{
    public class ClipEnvelopeTests
    {
        const int TB = PatternEvent.TimeBase;

        static ReBuzzCore Core => AutomationLaneTests.Core;

        static RawInputModifiers Cmd => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        static ColumnKey KeyOf(IMachine machine, string name) => AutomationParams.List(machine).First(p => p.Name == name).Key;

        /// <summary>A clip of the lead machine, in a Session slot or placed in the arrangement, open in the Detail panel with the cutoff lane.</summary>
        sealed class EnvelopeClip
        {
            public Rig Rig;
            public IMachine Machine;
            public IPattern Pattern;
            public ColumnKey Key;
            public AutoParam Param;

            public EnvelopeStack Stack => PatternView.Page.Roll.Envelopes;
            public EnvelopeLane Lane => Stack.LaneFor(Key);

            public static EnvelopeClip Open(Rig rig, bool arrangement, string parameter = Cutoff)
            {
                var lead = rig.Lane("Lead");
                var machine = lead.Machine;
                IPattern pattern;
                if (arrangement)
                {
                    rig.DoubleClick(rig.At(lead, 0, 2));
                    pattern = machine.Patterns.Last();
                }
                else
                {
                    var layout = Core.SessionLayout;
                    layout.Ensure();
                    int track = Enumerable.Range(0, layout.TrackCount).First(t => layout.SequenceOf(t)?.Machine == machine);
                    pattern = layout.CreateClip(track, 0);
                }
                Assert.That(pattern, Is.Not.Null);
                var clip = new EnvelopeClip { Rig = rig, Machine = machine, Pattern = pattern, Key = KeyOf(machine, parameter) };
                clip.Param = AutomationParams.Find(machine, clip.Key);
                clip.Show();
                return clip;
            }

            public void Show()
            {
                rigDetail();
                Assert.That(EnvelopeOpener.Open(Pattern, Key), Is.True, "the window opens the clip's lane");
                Settle();
                Assert.That(Lane, Is.Not.Null, "the lane is there");
                Lane.Focus();
                Settle();
            }

            void rigDetail() { Rig.Window.Detail.Show(true); Settle(); }

            public Point P(double tick, double fraction)
            {
                Settle();
                var p = Lane.TranslatePoint(Lane.At((int)Math.Round(tick * TB), Param.ValueAt(fraction)), Rig.Window);
                Assert.That(p, Is.Not.Null);
                return p!.Value;
            }

            public Point PointP(int time)
            {
                Settle();
                return Lane.TranslatePoint(Lane.PointOf(time), Rig.Window)!.Value;
            }

            public string Points() => string.Join(" ", ClipEnvelopes.Read(Pattern, Key).Points.Select(p => $"{p.Time / TB}:{p.Value}{(p.Ramp ? "/" : "-")}"));

            public void Refresh() { Stack.Refresh(); Settle(); }

            /// <summary>Does a gesture, then checks it changed the points, is one step of the history, and comes back with redo.</summary>
            public void Step(string what, Action gesture, string expected = null)
            {
                string before = Points();
                gesture();
                Settle();
                string after = Points();
                Assert.That(after, Is.Not.EqualTo(before), what + ": it changed the clip");
                if (expected != null) Assert.That(after, Is.EqualTo(expected), what);
                Rig.Undo();
                Refresh();
                Assert.That(Points(), Is.EqualTo(before), what + ": one undo step brings it back");
                Rig.Redo();
                Refresh();
                Assert.That(Points(), Is.EqualTo(after), what + ": redo does it again");
                Lane.Focus();
                Settle();
            }
        }

        // ------------------------------------------------------------------ the gestures

        [AvaloniaTest]
        public void EveryGestureOfTheLaneIsOneStepOfTheHistory([Values(false, true)] bool arrangement)
        {
            using var rig = new Rig("Lead");
            var clip = EnvelopeClip.Open(rig, arrangement);
            Assert.That(clip.Points(), Is.Empty);
            Assert.That(clip.Stack.CountText, Is.Not.Null);

            // Click adds a point (a ramp), at the grid.
            clip.Step("add", () => rig.Click(clip.P(4, 0.2)));
            clip.Step("add another", () => rig.Click(clip.P(8, 0.8)));
            clip.Step("add a third", () => rig.Click(clip.P(12, 0.4)));
            var pts = ClipEnvelopes.Read(clip.Pattern, clip.Key).Points;
            Assert.That(pts.Select(p => p.Time / TB), Is.EqualTo(new[] { 4, 8, 12 }));
            Assert.That(pts.All(p => p.Ramp), Is.True, "a new point ramps to the next");
            rig.Window.MouseMove(clip.PointP(8 * TB));
            Settle();
            Assert.That(clip.Lane.TipText, Does.Contain("ramp").Or.Contain("step"), "the value tooltip says what the point is");

            // Drag moves a point, in time and value.
            int movedValue = 0;
            clip.Step("drag a point", () =>
            {
                rig.Drag(clip.PointP(8 * TB), clip.P(10, 0.5));
                movedValue = ClipEnvelopes.Read(clip.Pattern, clip.Key).Points.First(p => p.Time == 10 * TB).Value;
            });
            Assert.That(clip.Points(), Does.Contain("10:" + movedValue));
            Assert.That(clip.Points(), Does.Not.Contain(" 8:"));

            // A rubber band selects; dragging one of the selected moves them all, together.
            rig.Drag(clip.P(1, 0.97), clip.P(14, 0.02));
            Assert.That(clip.Lane.Selection.Count, Is.EqualTo(3), "the band took the three points");
            clip.Step("drag the selection", () => rig.Drag(clip.PointP(4 * TB), clip.P(6, 0.2)), null);
            Assert.That(clip.Points().Split(' ').Select(x => int.Parse(x.Split(':')[0])), Is.EqualTo(new[] { 6, 12, 14 }), "all three moved by two ticks");

            // R: ramps become steps, and back (an undo takes the selection with the points it named).
            rig.Key(PhysicalKey.A, Cmd);
            Assert.That(clip.Lane.Selection.Count, Is.EqualTo(3));
            clip.Step("R makes the selection steps", () => rig.Key(PhysicalKey.R));
            Assert.That(ClipEnvelopes.Read(clip.Pattern, clip.Key).Points.All(p => !p.Ramp), Is.True);
            clip.Step("R again makes ramps", () => rig.Key(PhysicalKey.R));
            Assert.That(ClipEnvelopes.Read(clip.Pattern, clip.Key).Points.All(p => p.Ramp), Is.True);

            // Shift/Ctrl-click adds to the selection; Ctrl/Cmd+A takes all.
            rig.Click(clip.PointP(6 * TB));
            Assert.That(clip.Lane.Selection.Count, Is.EqualTo(1));
            rig.Click(clip.PointP(12 * TB), mods: RawInputModifiers.Shift);
            Assert.That(clip.Lane.Selection.Count, Is.EqualTo(2), "shift-click adds");
            rig.Click(clip.PointP(12 * TB), mods: Cmd);
            Assert.That(clip.Lane.Selection.Count, Is.EqualTo(1), "ctrl/cmd-click toggles");
            rig.Key(PhysicalKey.A, Cmd);
            Assert.That(clip.Lane.Selection.Count, Is.EqualTo(3), "ctrl/cmd+A selects every point");

            // Copy and paste: the selection's points come back later in the clip.
            rig.Click(clip.PointP(6 * TB));
            rig.Key(PhysicalKey.C, Cmd);
            string beforePaste = clip.Points();
            clip.Step("paste", () => Assert.That(clip.Lane.PasteAt(8 * TB), Is.True));
            Assert.That(clip.Points(), Does.Contain("8:"), "the copied point arrived at the paste time");
            Assert.That(clip.Points().Split(' ').Length, Is.EqualTo(beforePaste.Split(' ').Length + 1));

            // Delete removes the selection; a double click removes one point.
            rig.Click(clip.PointP(8 * TB));
            clip.Step("Delete", () => rig.Key(PhysicalKey.Delete));
            clip.Step("double click", () => rig.DoubleClick(clip.PointP(12 * TB)));
            Assert.That(clip.Points(), Does.Not.Contain("12:"));

            // The clip is a clip in both places: the same pattern and the same events, whichever view holds it.
            var column = clip.Pattern.Columns.OfType<PatternColumnCore>().Single(c => c.EnginePlays);
            Assert.That(column.GetEvents(int.MinValue, int.MaxValue).Count(), Is.EqualTo(ClipEnvelopes.Read(clip.Pattern, clip.Key).Points.Count));
        }

        [AvaloniaTest]
        public void AGestureThatShiftHoldsMovesOffTheGrid()
        {
            using var rig = new Rig("Lead");
            var clip = EnvelopeClip.Open(rig, false);
            rig.Click(clip.P(4, 0.5));
            Assert.That(clip.Points(), Does.StartWith("4:"));
            var from = clip.PointP(4 * TB);
            var to = clip.P(5.4, 0.5);
            // Shift is held for the drag (a Shift press on a point would toggle it in the selection instead).
            rig.Window.MouseMove(from);
            rig.Window.MouseDown(from, MouseButton.Left);
            rig.Window.MouseMove(from + (to - from) * 0.5, RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
            rig.Window.MouseMove(to, RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
            rig.Window.MouseUp(to, MouseButton.Left, RawInputModifiers.Shift);
            Settle();
            var time = ClipEnvelopes.Read(clip.Pattern, clip.Key).Points.Single().Time;
            Assert.That(time % TB, Is.Not.EqualTo(0), "shift: no snap, between two ticks; time " + time + " selection " + clip.Lane.Selection.Count + " refusal " + clip.Lane.Refusal);
        }

        // ------------------------------------------------------------------ the files

        [AvaloniaTest]
        public void ACurveAndAPerTrackChoiceAreSavedAndReopened([Values(".nbl")] string extension, [Values(false, true)] bool arrangement)
        {
            using var rig = new Rig("Lead");
            var clip = EnvelopeClip.Open(rig, arrangement);
            rig.Click(clip.P(4, 0.3));
            rig.Click(clip.P(8, 0.9));
            rig.Key(PhysicalKey.R);
            string drawn = clip.Points();
            Assert.That(drawn.Split(' ').Length, Is.EqualTo(2));
            var voice = KeyOf(clip.Machine, "Voice mode");
            Core.Arrangement.SetEnvelopeChoice("Lead", voice);
            string name = clip.Pattern.Name;

            string path = Path.Combine(Path.GetTempPath(), $"rebuzz-ev1-{Environment.ProcessId}{extension}");
            try
            {
                Core.SaveSongCopy(path);
                Core.Modified = false;
                Core.OpenSongFile(path);
                Settle();
                var machine = Core.SongCore.MachinesList.Single(m => m.Name == "Lead");
                var pattern = machine.Patterns.Single(p => p.Name == name);
                string again = string.Join(" ", ClipEnvelopes.Read(pattern, clip.Key).Points.Select(p => $"{p.Time / TB}:{p.Value}{(p.Ramp ? "/" : "-")}"));
                Assert.That(again, Is.EqualTo(drawn), "the points come back");
                Assert.That(Core.Arrangement.EnvelopeChoice("Lead"), Is.EqualTo(voice), "the track's choice comes back with the song");
                Assert.That(ClipEnvelopes.ChoiceOf(Core, machine), Is.EqualTo(voice));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [AvaloniaTest]
        public void ASongWithNoChoiceWritesNothingForItAndTheDefaultIsTheFirstParameterWithData()
        {
            using var rig = new Rig("Lead");
            Assert.That(Core.Arrangement.EnvelopeChoice("Lead"), Is.Null);
            var xml = Core.Arrangement.ToXml();
            Assert.That(xml == null || !xml.ToString().Contains("Envelope"), "nothing differs from the default: nothing is saved");
            var clip = EnvelopeClip.Open(rig, false);
            Assert.That(ClipEnvelopes.DefaultKey(clip.Machine), Is.Null, "no data, no default");
            rig.Click(clip.P(4, 0.5));
            Assert.That(ClipEnvelopes.DefaultKey(clip.Machine), Is.EqualTo(clip.Key), "the one with data");
            Assert.That(ClipEnvelopes.ChoiceOf(Core, clip.Machine), Is.EqualTo(clip.Key));
        }

        // ------------------------------------------------------------------ the tracker's column

        [AvaloniaTest]
        public void ThePatternEditorsColumnShowsAsReadOnlyStepsAndTheOverlapIsMarked()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            var machine = lead.Machine;
            machine.CreatePattern("00", 16);
            var pattern = machine.Patterns.First(p => p.Name == "00");

            rig.Window.Detail.Show(true);
            Settle();
            PatternView.Edit(pattern);
            Settle();
            dynamic view = PatternView.Page.EditorControl;
            ((Avalonia.Controls.Control)view.TrackerGrid.Body).Focus();
            Settle();
            foreach (var (rowNumber, digit) in new[] { (4, PhysicalKey.Digit2), (8, PhysicalKey.Digit1) })
            {
                EditorNav.GoTo((object)view, "Voice mode", Settle);
                for (int i = 0; i < rowNumber; i++) rig.Key(PhysicalKey.ArrowDown);
                rig.Key(PhysicalKey.ArrowRight);
                rig.Key(digit);
            }
            Settle();

            var key = KeyOf(machine, "Voice mode");
            var clip = new EnvelopeClip { Rig = rig, Machine = machine, Pattern = pattern, Key = key, Param = AutomationParams.Find(machine, key) };
            clip.Show();
            var env = clip.Lane.Envelope;
            Assert.That(env.Tracker, Is.Not.Null, "the pattern editor's column is read");
            Assert.That(env.Tracker.Points.Select(p => (p.Time / TB, p.Value)), Is.EqualTo(new[] { (4, 2), (8, 1) }));
            Assert.That(env.Tracker.Editable, Is.False, "read only: the Tracker's to edit");
            Assert.That(env.Overlaps, Is.Empty, "one writer");

            // A point in the lane over the Tracker's steps: both set the parameter there, and the lane marks it.
            rig.Click(clip.P(3, 0.0));
            rig.Click(clip.P(12, 1.0));
            clip.Refresh();
            Assert.That(clip.Lane.Envelope.Overlaps, Is.Not.Empty, "two writers in one place are marked");
            Assert.That(clip.Lane.Envelope.Overlaps.Any(o => o.From <= 4 * TB && o.To > 4 * TB), Is.True, "over the first step");
            // The Tracker's column is untouched by the lane's edits.
            Assert.That(pattern.PatternEditorMachineEvents.First(c => c.Parameter.Name == "Voice mode").Events.Select(e => (e.Time / TB, e.Value)), Is.EqualTo(new[] { (4, 2), (8, 1) }));
        }

        // ------------------------------------------------------------------ the curves on the clips

        [AvaloniaTest]
        public void ArrangementClipsDrawTheTracksChosenCurveIncludingTheRepeatsAndADoubleClickOpensTheLane()
        {
            using var rig = new Rig("Lead");
            var lead = rig.Lane("Lead");
            var clip = EnvelopeClip.Open(rig, true);
            rig.Click(clip.P(2, 0.1));
            rig.Click(clip.P(10, 0.9));
            Assert.That(rig.Canvas.Envelopes, Is.False, "off until asked");

            // E (or the toggle) shows the curves.
            rig.Window.Detail.Close();
            Settle();
            rig.Click(rig.At(lead, 4, 0, 0.3));      // the clip: the canvas has the keys
            Assert.That(rig.Canvas.IsKeyboardFocusWithin || rig.View.IsKeyboardFocusWithin, Is.True);
            rig.Key(PhysicalKey.E);
            Assert.That(rig.Canvas.Envelopes, Is.True, "E toggles the curves");
            Assert.That(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(rig.View).OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Envelopes on clips").IsChecked, Is.True, "the toggle follows");
            var curve = rig.Canvas.CurveOfClip(clip.Pattern);
            Assert.That(curve, Is.Not.Null);
            Assert.That(curve!.Value.Key, Is.EqualTo(clip.Key), "the first parameter with data");
            Assert.That(curve.Value.Points.Count, Is.EqualTo(2));
            Assert.That(curve.Value.FromTracker, Is.False);

            // The track's header chooses another parameter: saved in the song's fork data.
            var voice = KeyOf(clip.Machine, "Voice mode");
            rig.Click(rig.Of(rig.View.Headers.Find<Avalonia.Controls.Button>("Clip curve of Lead")!));
            Assert.That(rig.View.Headers.LastCurvePicker, Is.Not.Null);
            Assert.That(rig.View.Headers.LastCurvePicker.Choose("Voice mode"), Is.True);
            Settle();
            Assert.That(Core.Arrangement.EnvelopeChoice("Lead"), Is.EqualTo(voice));
            Assert.That(rig.Canvas.CurveOfClip(clip.Pattern), Is.Null, "the clip holds none of it: nothing drawn");
            Core.Arrangement.SetEnvelopeChoice("Lead", clip.Key);
            Settle();
            Assert.That(rig.Canvas.CurveOfClip(clip.Pattern), Is.Not.Null);

            var item = rig.Canvas.ItemsOf(lead).First();
            Assert.That(item.Actual.Count, Is.GreaterThanOrEqualTo(1));
            Studio.Screenshot(rig.Window, "ev1-arrangement-curves");

            // A double click on the curve opens the clip in the Detail panel with its lane.
            rig.Window.Detail.Close();
            Settle();
            item = rig.Canvas.ItemsOf(lead).First();
            var r = rig.Canvas.ClipRect(item);
            var area = new Rect(r.X, r.Y + 19, item.Actual[0].Length * rig.Canvas.Geometry.TickWidth, r.Height - 22);
            double x = r.X + 6 * rig.Canvas.Geometry.TickWidth;
            int t = (int)Math.Round((x - r.X) / rig.Canvas.Geometry.TickWidth + item.Actual[0].Offset) * TB;
            int v = AutomationMath.ValueAt(clip.Lane.Envelope.Points, t) ?? 0;
            double y = area.Y + (1 - clip.Param.Fraction(v)) * area.Height;
            var window = rig.Canvas.TranslatePoint(new Point(x, y), rig.Window)!.Value;
            rig.DoubleClick(window);
            Assert.That(rig.Window.Detail.IsOpen, Is.True, "the Detail panel opened");
            Assert.That(PatternView.Page.Roll.Envelopes.LaneFor(clip.Key), Is.Not.Null, "with the lane of the curve's parameter");
            Assert.That(PatternView.Page.Pattern, Is.SameAs(clip.Pattern));
        }

        [AvaloniaTest]
        public void SessionSlotsDrawAMiniCurveAndTheTracksChoiceChangesIt()
        {
            using var rig = new Rig("Lead");
            var clip = EnvelopeClip.Open(rig, false);
            rig.Click(clip.P(2, 0.1));
            rig.Click(clip.P(10, 0.9));

            var tab = rig.Window.Tabs.Items.Cast<Avalonia.Controls.TabItem>().Single(t => (string)t.Header == "Session");
            tab.IsSelected = true;
            Settle();
            var session = (Views.Session.SessionView)tab.Content!;
            session.Rebuild();
            Settle();
            int track = Enumerable.Range(0, Core.SessionLayout.TrackCount).First(t => Core.SessionLayout.SequenceOf(t)?.Machine == clip.Machine);
            var cell = session.SlotAt(track, 0);
            Assert.That(cell.Curve, Is.Not.Null, "the slot draws its clip's envelope");
            Assert.That(cell.Curve.Key, Is.EqualTo(clip.Key));

            // The chip in the track's header picks the parameter.
            var header = session.Headers.First(h => h.Machine == clip.Machine);
            session.ChooseCurve(header);
            Settle();
            Assert.That(session.LastCurvePicker.Choose("Voice mode"), Is.True);
            Settle();
            Assert.That(Core.Arrangement.EnvelopeChoice("Lead"), Is.EqualTo(KeyOf(clip.Machine, "Voice mode")), "saved in the song");
            Settle();
            Assert.That(session.SlotAt(track, 0).Curve, Is.Null, "the clip has none of the chosen parameter");
            Core.Arrangement.SetEnvelopeChoice("Lead", clip.Key);
            Settle();
            Assert.That(session.SlotAt(track, 0).Curve, Is.Not.Null);
            Studio.Screenshot(rig.Window, "ev1-session-curves");
        }

        // ------------------------------------------------------------------ the exit test

        /// <summary>The first point of a curve, written the same way in both songs: AR4's lane adds to a curve a clip already has.</summary>
        static void Seed(EnvelopeClip clip)
        {
            Assert.That(new ClipEnvelopeEditor(Core).AddPoint(ClipEnvelopes.Read(clip.Pattern, clip.Key), 0, clip.Param.ValueAt(0.15)), Is.Null);
            clip.Refresh();
        }

        [AvaloniaTest]
        public void ACurveDrawnInTheDetailLaneRendersBitForBitAsTheSamePointsDrawnWithAR4sArrangementLane()
        {
            float[] byLane, byAr4;
            string drawn;
            System.Collections.Generic.List<AutoPoint> laneDrawn;

            // The same clip twice: notes, a looped bar, the cutoff drawn in the Detail lane.
            using (var rig = new Rig("Lead"))
            {
                var lead = rig.Lane("Lead");
                Core.Looping = false;
                var clip = EnvelopeClip.Open(rig, true);
                Notes(clip.Pattern, 16);
                Core.SongCore.SongEnd = 64;
                Seed(clip);
                rig.Click(clip.P(8, 0.85));
                rig.Click(clip.P(14, 0.4));
                drawn = clip.Points();
                laneDrawn = ClipEnvelopes.Read(clip.Pattern, clip.Key).Points.ToList();
                Assert.That(drawn.Split(' ').Length, Is.EqualTo(3));
                Core.SongCore.PlayPosition = 0;
                byLane = Render(64);
                Core.SongCore.PlayPosition = 0;
                Assert.That(Render(64), Is.EqualTo(byLane), "a render is repeatable");
                Assert.That(byLane.Any(x => x != 0), Is.True, "something sounds");
            }

            // AR4's edit action (the one its lane's mouse gestures call) writes the same points into the clip's column of a fresh song.
            using (var rig = new Rig("Lead"))
            {
                var lead = rig.Lane("Lead");
                Core.Looping = false;
                var clip = EnvelopeClip.Open(rig, true);
                Notes(clip.Pattern, 16);
                Core.SongCore.SongEnd = 64;
                rig.Window.Detail.Close();
                Settle();
                Seed(clip);
                foreach (var p in laneDrawn.Skip(1))
                {
                    var seg = AutomationReader.Read(Core, clip.Machine, clip.Key).Segments.Single(x => x.Source == AutoSource.ClipColumn);
                    Assert.That(rig.Canvas.Automation.AddPoint(seg, clip.Key, seg.ToSong(p.Time), p.Value, p.Ramp), Is.Null);
                }
                Assert.That(clip.Points(), Is.EqualTo(drawn), "the same points");
                Core.SongCore.PlayPosition = 0;
                byAr4 = Render(64);
            }
            Assert.That(byAr4, Is.EqualTo(byLane), "bit for bit the same sound");
        }

        [AvaloniaTest]
        public void ASessionClipDrawnInTheLaneHoldsTheSameEventsAsTheArrangementsAndTheLaneDoesNotTouchTheAudioPath()
        {
            using var rig = new Rig("Lead");
            var session = EnvelopeClip.Open(rig, false);
            rig.Click(session.P(2, 0.2));
            rig.Click(session.P(8, 0.9));
            var events = session.Pattern.Columns.OfType<PatternColumnCore>().Single(c => c.EnginePlays).GetEvents(int.MinValue, int.MaxValue).ToList();
            Assert.That(events.Select(e => e.Time), Is.EqualTo(new[] { 2 * TB, 8 * TB }));

            // The model is the AR4 one: the arrangement's reader finds the same curve in the clip once it is placed.
            var lead = rig.Lane("Lead");
            Assert.That(rig.Canvas.Editor.Place(lead, session.Pattern, 0), Is.Null);
            rig.Canvas.Changed();
            rig.Window.Detail.Close();
            Settle();
            rig.AddLane("Lead", Cutoff);
            var read = rig.Read("Lead", Cutoff);
            var seg = read.Segments.Single(s => s.Source == AutoSource.ClipColumn);
            Assert.That(string.Join(" ", seg.Points.Select(p => $"{p.Time / TB}:{p.Value}")), Is.EqualTo(string.Join(" ", ClipEnvelopes.Read(session.Pattern, session.Key).Points.Select(p => $"{p.Time / TB}:{p.Value}"))));
        }

        [AvaloniaTest]
        public void TheLaneHasItsHeaderItsHintAndStacksLanesThatCollapseAndAreRemoved()
        {
            using var rig = new Rig("Lead");
            var clip = EnvelopeClip.Open(rig, false);
            rig.Click(clip.P(4, 0.5));
            var stack = clip.Stack;
            Assert.That(stack.AddButton.Content?.ToString(), Does.Contain("Envelope"));
            Assert.That(stack.HintText, Does.Not.Contain("pattern"), "the word is clip");
            Assert.That(stack.Lanes.Count, Is.EqualTo(1), "the parameter that holds data shows by default");
            var second = KeyOf(clip.Machine, "Voice mode");
            stack.AddLane(second);
            Settle();
            Assert.That(stack.Lanes.Count, Is.EqualTo(2), "lanes stack");
            stack.ToggleCollapsed(second);
            Assert.That(stack.IsCollapsed(second), Is.True);
            stack.RemoveLane(second);
            Settle();
            Assert.That(stack.Lanes.Count, Is.EqualTo(1));
            Studio.Screenshot(rig.Window, "ev1-detail-lane");
        }
    }
}
