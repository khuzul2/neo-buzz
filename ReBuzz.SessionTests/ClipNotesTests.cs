using ReBuzz.Core.Session;

namespace ReBuzz.SessionTests
{
    /// <summary>The piano roll's edits (docs/specs/session-view.md 0.7.1), pure.</summary>
    [TestFixture]
    public class ClipNotesTests
    {
        const int TB = ClipNotes.TimeBase;
        const int Clip = 16 * TB;          // a one-bar clip at 4 ticks per beat

        static int On(int note, int velocity, int channel = 0) => 0x90 | channel | (note << 8) | (velocity << 16);

        [Test]
        public void NotesAreTheNoteOnsOfTheColumnInTimeOrder()
        {
            var notes = ClipNotes.From(new[]
            {
                (480, On(64, 90), 240),
                (0, On(60, 100, channel: 2), 120),
                (100, 0xB0 | (1 << 8) | (64 << 16), 0),            // a controller: not a note
                (200, On(61, 0), 10),                               // velocity 0: a release, not a note
            });
            Assert.That(notes, Is.EqualTo(new[] { new ClipNote(0, 120, 60, 100, 2), new ClipNote(480, 240, 64, 90) }));
            Assert.That(notes[0].ToEvent(), Is.EqualTo((0, On(60, 100, channel: 2), 120)), "and back");
        }

        [Test]
        public void SnappingRoundsToTheNearestLineAndDrawingToTheOneBefore()
        {
            Assert.That(ClipNotes.Snap(130, TB), Is.EqualTo(240));
            Assert.That(ClipNotes.Snap(110, TB), Is.EqualTo(0));
            Assert.That(ClipNotes.Snap(117, 1), Is.EqualTo(117), "no grid");
            Assert.That(ClipNotes.SnapDown(470, TB), Is.EqualTo(240));
            var drawn = ClipNotes.Draw(470, 60, 100, TB, Clip);
            Assert.That(drawn, Is.EqualTo(new ClipNote(240, TB, 60, 100)), "one grid step long");
            Assert.That(ClipNotes.Draw(Clip - 10, 200, 0, TB, Clip), Is.EqualTo(new ClipNote(Clip - TB, TB, 127, 1)), "inside the clip and the MIDI range");
        }

        [Test]
        public void MovingAGroupSnapsItsFirstNoteAndKeepsItsShape()
        {
            var group = new[] { new ClipNote(250, 100, 60, 100), new ClipNote(490, 100, 64, 100) };
            var moved = ClipNotes.Move(group, 300, 2, TB, Clip);
            Assert.That(moved, Is.EqualTo(new[] { new ClipNote(480, 100, 62, 100), new ClipNote(720, 100, 66, 100) }));
            var unsnapped = ClipNotes.Move(group, 7, 0, 1, Clip);
            Assert.That(unsnapped.Select(n => n.Time), Is.EqualTo(new[] { 257, 497 }));
        }

        [Test]
        public void AGroupMovedTooFarStaysInTheClipAndTheMidiRange()
        {
            var group = new[] { new ClipNote(240, 240, 120, 100), new ClipNote(Clip - 480, 240, 126, 100) };
            var moved = ClipNotes.Move(group, 10 * Clip, 20, 1, Clip);
            Assert.That(moved.Max(n => n.Time), Is.EqualTo(Clip - 1));
            Assert.That(moved.Max(n => n.Note), Is.EqualTo(127));
            Assert.That(moved[0].Note, Is.EqualTo(121), "the group keeps its intervals");
            Assert.That(moved.All(n => n.End <= Clip), Is.True, "cut at the clip's end");
            var back = ClipNotes.Move(group, -10 * Clip, -200, 1, Clip);
            Assert.That((back[0].Time, back[0].Note), Is.EqualTo((0, 0)));
        }

        [Test]
        public void ResizingEndsOnGridLinesAndNeverBelowAStep()
        {
            var notes = new[] { new ClipNote(240, 240, 60, 100) };
            Assert.That(ClipNotes.Resize(notes, 250, TB, Clip)[0].Duration, Is.EqualTo(480), "end on the line near 730");
            Assert.That(ClipNotes.Resize(notes, -1000, TB, Clip)[0].Duration, Is.EqualTo(TB), "at least one step");
            Assert.That(ClipNotes.Resize(notes, 10 * Clip, 1, Clip)[0].End, Is.EqualTo(Clip), "not past the clip");
            Assert.That(ClipNotes.Resize(notes, -1000, 1, Clip)[0].Duration, Is.EqualTo(ClipNotes.MinDuration));
        }

        [Test]
        public void QuantizingMovesStartsByItsStrengthAndEndsOnlyIfAsked()
        {
            var notes = new[] { new ClipNote(200, 100, 60, 100), new ClipNote(500, 260, 62, 100) };
            var full = ClipNotes.Quantize(notes, TB, 1.0, ends: false, Clip);
            Assert.That(full, Is.EqualTo(new[] { new ClipNote(240, 100, 60, 100), new ClipNote(480, 260, 62, 100) }));
            var half = ClipNotes.Quantize(notes, TB, 0.5, ends: false, Clip);
            Assert.That(half.Select(n => n.Time), Is.EqualTo(new[] { 220, 490 }));
            var withEnds = ClipNotes.Quantize(notes, TB, 1.0, ends: true, Clip);
            Assert.That(withEnds.Select(n => n.End), Is.EqualTo(new[] { 480, 720 }), "a short note keeps a step");
        }

        [Test]
        public void VelocityStaysInRange()
        {
            var notes = new[] { new ClipNote(0, 10, 60, 120), new ClipNote(0, 10, 61, 5) };
            Assert.That(ClipNotes.Velocity(notes, 20).Select(n => n.Velocity), Is.EqualTo(new[] { 127, 25 }));
            Assert.That(ClipNotes.Velocity(notes, -50).Select(n => n.Velocity), Is.EqualTo(new[] { 70, 1 }));
        }

        [Test]
        public void HitTestingAndBoxSelection()
        {
            var notes = new[] { new ClipNote(0, 480, 60, 100), new ClipNote(240, 480, 60, 100), new ClipNote(240, 240, 64, 100) };
            Assert.That(ClipNotes.HitTest(notes, 300, 60), Is.EqualTo(1), "the later one on top");
            Assert.That(ClipNotes.HitTest(notes, 100, 60), Is.EqualTo(0));
            Assert.That(ClipNotes.HitTest(notes, 100, 61), Is.EqualTo(-1));
            Assert.That(ClipNotes.HitTest(notes, 480, 64), Is.EqualTo(-1), "the end is outside");
            Assert.That(ClipNotes.InBox(notes, 500, 200, 65, 63), Is.EqualTo(new[] { 2 }), "a box drawn backwards");
            Assert.That(ClipNotes.InBox(notes, 200, 500, 61, 63), Is.Empty);
            Assert.That(ClipNotes.InBox(notes, 300, 600, 59, 65), Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void NotesAreNamedAsPreferencesSay()
        {
            // The default (BuzzGUI.Common.NoteNames.Default): C3 is middle C, 60.
            Assert.That(ClipNotes.Name(60), Is.EqualTo("C3"));
            Assert.That(ClipNotes.Name(0), Is.EqualTo("C-2"));
            Assert.That(ClipNotes.Name(127), Is.EqualTo("G8"));
            Assert.That(ClipNotes.Name(61), Is.EqualTo("C#3"));
            var naming = BuzzGUI.Common.NoteNames.Naming;
            try
            {
                BuzzGUI.Common.NoteNames.Naming = BuzzGUI.Common.NoteNaming.Scientific;
                Assert.That(ClipNotes.Name(60), Is.EqualTo("C4"));
                Assert.That(ClipNotes.Name(0), Is.EqualTo("C-1"));
                BuzzGUI.Common.NoteNames.Naming = BuzzGUI.Common.NoteNaming.Buzz;
                Assert.That(ClipNotes.Name(60), Is.EqualTo("C-5"), "Buzz's: three characters");
                Assert.That(ClipNotes.Name(61), Is.EqualTo("C#5"));
                Assert.That(ClipNotes.Name(0), Is.EqualTo("C-0"));
            }
            finally { BuzzGUI.Common.NoteNames.Naming = naming; }
        }
    }
}
