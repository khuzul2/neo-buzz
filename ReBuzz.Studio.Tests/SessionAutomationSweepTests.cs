// Fork (docs/specs/session-view.md 0.7, SA1, issue 173): a pass of recorded automation replaces what it sweeps over, as the
// Arrangement's recorder does. From a parameter's first move in a pass until the pass ends (the clip's end, or the recording's)
// the clip holds only that pass's points; parameters not moved keep theirs; notes still overdub; a pass is one undo step.
// On the real engine: the Session's recorder, the clip's automation columns, the song's history.

using Avalonia.Headless.NUnit;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Tests
{
    public class SessionAutomationSweepTests
    {
        const int Bar = SessionRig.Bar;
        const int TB = SessionRig.TB;

        sealed class Song : IDisposable
        {
            readonly IDisposable setup;
            public readonly SessionRig Rig;
            public readonly SessionLayout Layout;
            public readonly MachineCore Lead;
            public readonly ParameterCore P, Q;
            public readonly ISequence Track;
            public readonly int TrackIndex;
            public PatternCore Take;

            public Song()
            {
                setup = TrackInputEngineTests.Setup(out Rig);
                Layout = Rig.Layout;
                Layout.Ensure();
                Layout.SetCountInBars(0);
                Layout.SetRecordBars(1);
                Lead = Rig.Add("Neo Poly", "Lead");
                var wide = Lead.ParameterGroupsList[1].ParametersList.Cast<ParameterCore>()
                    .Where(p => p.MaxValue - p.MinValue >= 100 && p.MaxValue < 0xFFFE).ToList();
                P = wide[0];
                Q = wide[1];
                Layout.SetMachineArmed(Lead, true);
                TrackIndex = Layout.FirstTrackOf(Lead);
                Track = Layout.SequenceOf(TrackIndex)!;
            }

            public int Position => Rig.Session.GetClipPosition(Track);

            /// <summary>Renders ticks until the clip is at <paramref name="position"/> (ticks into it).</summary>
            public void RunTo(int position)
            {
                for (int guard = 0; Position != position; guard++)
                {
                    Assert.That(guard, Is.LessThan(4 * Bar), "the clip reaches position " + position);
                    Rig.RunTicks(1);
                }
            }

            public void Move(ParameterCore parameter, int value) => parameter.SetValue(0, value);

            /// <summary>Pass 1: a take of one bar, with a knob moved at each of these positions (value = Lo + position), then it plays on.</summary>
            public void RecordTakeWith(int[] pPositions, int[] qPositions, int note, int notePosition)
            {
                Take = (PatternCore)Layout.RecordTake(TrackIndex, 0)!;
                Assert.That(Take, Is.Not.Null);
                Rig.Core.Playing = true;
                var all = pPositions.Concat(qPositions).Concat(new[] { notePosition, notePosition + 1 }).Distinct().OrderBy(x => x).ToList();
                foreach (int at in all)
                {
                    RunTo(at);
                    if (pPositions.Contains(at)) Move(P, P.MinValue + at);
                    if (qPositions.Contains(at)) Move(Q, Q.MinValue + at);
                    if (at == notePosition) Rig.Core.SendMIDIInput(SessionRig.NoteOn(note, 100));
                    if (at == notePosition + 1) Rig.Core.SendMIDIInput(SessionRig.NoteOff(note));
                }
                RunTo(Bar - 1);
                Rig.RunTicks(3);                                  // past the wrap
                Layout.PollTakes();
                Assert.That(Layout.IsRecording(Track), Is.False, "the first pass is over");
            }

            public void Dispose()
            {
                setup.Dispose();
            }
        }

        static (int Time, int Value)[] Curve(IPattern clip, IParameter parameter) =>
            clip.Columns.Where(c => c.Type == PatternColumnType.Parameter && ReferenceEquals(c.Parameter, parameter))
                .SelectMany(c => c.GetEvents(int.MinValue, int.MaxValue)).OrderBy(e => e.Time).Select(e => (e.Time, e.Value)).ToArray();

        static (int Time, int Value)[] Points(ParameterCore p, params int[] positions) =>
            positions.Select(at => (at * TB, p.MinValue + at)).ToArray();

        [AvaloniaTest]
        public void ASecondPassOverTheSecondHalfReplacesTheSecondHalfAndLeavesTheFirstAndTheTailIsReplacedWhereTheClipWraps()
        {
            using var s = new Song();
            s.RecordTakeWith(pPositions: new[] { 1, 3, 5, 7, 9, 11, 13, 15 }, qPositions: new[] { 2, 12 }, note: 60, notePosition: 4);
            var first = Curve(s.Take, s.P);
            Assert.That(first, Is.EqualTo(Points(s.P, 1, 3, 5, 7, 9, 11, 13, 15)), "pass 1 wrote its curve");
            var q1 = Curve(s.Take, s.Q);
            Assert.That(q1.Length, Is.EqualTo(2));

            // Pass 2 (Session Record on a clip that plays): P moved over the second half only, at 10 and 12.
            s.Rig.Core.SongCore.ActionStack.Clear();
            s.Layout.SetSessionRecord(true);
            Assert.That(s.Layout.IsRecordingInto(s.Track, s.Take), Is.True, "overdubbing");
            s.RunTo(6);
            s.Rig.Core.SendMIDIInput(SessionRig.NoteOn(62, 100));
            s.RunTo(7);
            s.Rig.Core.SendMIDIInput(SessionRig.NoteOff(62));
            s.RunTo(10);
            s.Move(s.P, s.P.MinValue + 60);
            s.RunTo(12);
            s.Move(s.P, s.P.MinValue + 70);
            s.RunTo(Bar - 1);
            Assert.That(Curve(s.Take, s.P), Is.EqualTo(new[] { (1 * TB, s.P.MinValue + 1), (3 * TB, s.P.MinValue + 3), (5 * TB, s.P.MinValue + 5), (7 * TB, s.P.MinValue + 7), (9 * TB, s.P.MinValue + 9), (10 * TB, s.P.MinValue + 60), (12 * TB, s.P.MinValue + 70), (13 * TB, s.P.MinValue + 13), (15 * TB, s.P.MinValue + 15) }),
                "mid pass: what was swept, 10 to 12, is replaced (11 went); 13 and 15 wait for the pass to end");
            s.Rig.RunTicks(3);                                    // the clip wraps
            s.Layout.PollTakes();
            var second = Curve(s.Take, s.P);
            Assert.That(second, Is.EqualTo(new[] { (1 * TB, s.P.MinValue + 1), (3 * TB, s.P.MinValue + 3), (5 * TB, s.P.MinValue + 5), (7 * TB, s.P.MinValue + 7), (9 * TB, s.P.MinValue + 9), (10 * TB, s.P.MinValue + 60), (12 * TB, s.P.MinValue + 70) }),
                "the first half is pass 1's; from the first move on, only pass 2's: pass 1's points at 13 and 15 went when the pass ended");

            Assert.That(Curve(s.Take, s.Q), Is.EqualTo(q1), "a parameter not moved in pass 2 keeps all its points");
            s.Layout.SetSessionRecord(false);
            Assert.That(Curve(s.Take, s.Q), Is.EqualTo(q1));
            Assert.That(s.Layout.NotesOf(s.Take).Select(n => n.Note).OrderBy(n => n).ToArray(), Is.EqualTo(new[] { 60, 62 }), "notes of both passes are kept");

            // One undo step: pass 2's points go and pass 1's come back, exactly; redo does pass 2 again.
            var stack = s.Layout.UndoStack;
            Assert.That(stack.CanUndo, Is.True);
            stack.Undo();
            Assert.That(Curve(s.Take, s.P), Is.EqualTo(first), "undo restores pass 1's curve");
            Assert.That(Curve(s.Take, s.Q), Is.EqualTo(q1));
            Assert.That(s.Layout.NotesOf(s.Take).Select(n => n.Note).ToArray(), Is.EqualTo(new[] { 60 }), "and the notes of pass 2 go with it");
            stack.Redo();
            Assert.That(Curve(s.Take, s.P), Is.EqualTo(second), "redo reapplies it");
            Assert.That(s.Layout.NotesOf(s.Take).Select(n => n.Note).OrderBy(n => n).ToArray(), Is.EqualTo(new[] { 60, 62 }));
        }

        [AvaloniaTest]
        public void APassThatEndsWhenTheRecordingStopsReplacesUpToTheStopAndNoFurther()
        {
            using var s = new Song();
            s.RecordTakeWith(pPositions: new[] { 1, 3, 5, 7, 9, 11, 13, 15 }, qPositions: new int[0], note: 60, notePosition: 2);
            var first = Curve(s.Take, s.P);
            s.Rig.Core.SongCore.ActionStack.Clear();
            s.Layout.SetSessionRecord(true);
            s.RunTo(8);
            s.Move(s.P, s.P.MinValue + 80);
            s.RunTo(12);
            s.Layout.SetSessionRecord(false);                     // stops at 12
            Assert.That(Curve(s.Take, s.P), Is.EqualTo(new[] { (1 * TB, s.P.MinValue + 1), (3 * TB, s.P.MinValue + 3), (5 * TB, s.P.MinValue + 5), (7 * TB, s.P.MinValue + 7), (8 * TB, s.P.MinValue + 80), (13 * TB, s.P.MinValue + 13), (15 * TB, s.P.MinValue + 15) }),
                "9 and 11 were swept over and are gone; 13 and 15 are after the stop and stay");
            s.Layout.UndoStack.Undo();
            Assert.That(Curve(s.Take, s.P), Is.EqualTo(first), "undo restores the old points exactly");
        }

        [AvaloniaTest]
        public void ThePassAfterAWrapReplacesNothingUntilAMoveHappensInIt()
        {
            using var s = new Song();
            s.RecordTakeWith(pPositions: new[] { 2, 4, 6, 8, 10 }, qPositions: new int[0], note: 60, notePosition: 1);
            var first = Curve(s.Take, s.P);
            s.Rig.Core.SongCore.ActionStack.Clear();
            s.Layout.SetSessionRecord(true);
            s.RunTo(9);
            s.Move(s.P, s.P.MinValue + 90);                       // pass 2: from 9 on
            s.RunTo(Bar - 1);
            s.Rig.RunTicks(2);                                    // pass 3 begins: no move yet
            s.Layout.PollTakes();
            Assert.That(Curve(s.Take, s.P), Is.EqualTo(new[] { (2 * TB, s.P.MinValue + 2), (4 * TB, s.P.MinValue + 4), (6 * TB, s.P.MinValue + 6), (8 * TB, s.P.MinValue + 8), (9 * TB, s.P.MinValue + 90) }),
                "pass 3 has not moved the knob: nothing of the first half is touched");
            s.RunTo(5);
            s.Move(s.P, s.P.MinValue + 55);                       // pass 3: from 5 on
            s.RunTo(7);
            s.Layout.SetSessionRecord(false);
            Assert.That(Curve(s.Take, s.P), Is.EqualTo(new[] { (2 * TB, s.P.MinValue + 2), (4 * TB, s.P.MinValue + 4), (5 * TB, s.P.MinValue + 55), (8 * TB, s.P.MinValue + 8), (9 * TB, s.P.MinValue + 90) }),
                "pass 3 replaced 5 to 7 (6 went); 8 is after the stop, and pass 2's 9 is untouched");
            s.Layout.UndoStack.Undo();
            Assert.That(Curve(s.Take, s.P), Is.EqualTo(first), "the recording is one undo step: both passes go, pass 1's curve is back exactly");
        }
    }
}
