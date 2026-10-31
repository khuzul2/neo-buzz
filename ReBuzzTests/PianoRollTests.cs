using BuzzGUI.Interfaces;
using ReBuzz.AppViews.Session;
using ReBuzz.Core.Session;
using System.Linq;
using System.Threading;
using static ReBuzzTests.HardeningTests063;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: the Session View's piano roll (docs/specs/session-view.md 0.7.1). Edits of
    /// a clip's notes are undo steps of the session; the control's commands edit, and it renders.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class PianoRollTests
    {
        const int Bar = 16;
        const int TB = PatternEvent.TimeBase;

        [Test]
        public void EditingNotesIsOneUndoStepEach()
        {
            using var rig = new KeysRig();
            var clip = rig.Clip("A", Bar, (0, 60));
            var layout = rig.Layout;
            layout.Ensure();
            var before = layout.NotesOf(clip);
            Assert.That(before.Single(), Is.EqualTo(new ClipNote(0, TB / 2, 60, 100)));

            var moved = ClipNotes.Move(before, 2 * TB, 7, TB, SessionLayout.ClipLengthOf(clip));
            layout.EditNotes(clip, before, moved);
            Assert.That(layout.NotesOf(clip).Single(), Is.EqualTo(new ClipNote(2 * TB, TB / 2, 67, 100)));
            layout.EditNotes(clip, null, new[] { new ClipNote(4 * TB, TB, 64, 80) });
            Assert.That(layout.NotesOf(clip).Count, Is.EqualTo(2));

            layout.Undo();
            Assert.That(layout.NotesOf(clip).Single().Note, Is.EqualTo(67), "the added note goes");
            layout.Undo();
            Assert.That(layout.NotesOf(clip).Single(), Is.EqualTo(before.Single()), "the move is taken back");
            layout.Redo();
            layout.Redo();
            Assert.That(layout.NotesOf(clip).Select(n => n.Note), Is.EqualTo(new[] { 67, 64 }));
        }

        [Test]
        public void DrawingIntoAClipWithoutAMidiColumnGivesItOneThatPlays()
        {
            using var rig = new KeysRig();
            rig.Keys.CreatePattern("Empty", Bar);
            var clip = rig.Keys.PatternsList.Single(p => p.Name == "Empty");
            var layout = rig.Layout;
            layout.Ensure();
            layout.EditNotes(clip, null, new[] { new ClipNote(TB, TB, 72, 110) });
            Assert.That(clip.Columns.Single().Type, Is.EqualTo(PatternColumnType.MIDI));

            rig.Session.Launch(layout.SequenceOf(0)!, clip);
            rig.PlayTo(3);
            Assert.That(rig.NoteOns.Select(n => n.Note), Is.EqualTo(new[] { 72 }), "the engine plays it");
        }

        [Test]
        public void QuantizingIsOneStep()
        {
            using var rig = new KeysRig();
            var clip = rig.Clip("A", Bar, (TB + 30, 60), (3 * TB - 40, 62));
            var layout = rig.Layout;
            layout.Ensure();
            layout.QuantizeNotes(clip, null, TB);
            Assert.That(layout.NotesOf(clip).Select(n => n.Time), Is.EqualTo(new[] { TB, 3 * TB }));
            layout.Undo();
            Assert.That(layout.NotesOf(clip).Select(n => n.Time), Is.EqualTo(new[] { TB + 30, 3 * TB - 40 }));
        }

        [Test]
        public void ThePianoRollsCommandsEditTheClipAndItRenders()
        {
            using var rig = new KeysRig();
            var clip = rig.Clip("Take", 2 * Bar, (0, 60), (TB * 4 + 50, 64), (TB * 9, 67));
            var layout = rig.Layout;
            layout.Ensure();
            var roll = new PianoRoll(rig.Core, layout, layout.SequenceOf(0)!, clip) { Grid = TB };
            Assert.That(roll.Notes.Count, Is.EqualTo(3));

            roll.AddNote(12 * TB + 100, 72);                     // snapped down to the grid, a step long
            Assert.That(layout.NotesOf(clip).Last(), Is.EqualTo(new ClipNote(12 * TB, TB, 72, 100)));
            Assert.That(roll.Selection.Count, Is.EqualTo(1), "the drawn note is selected");

            roll.SelectAll();
            roll.MoveSelected(0, 2);
            Assert.That(layout.NotesOf(clip).Select(n => n.Note), Is.EqualTo(new[] { 62, 66, 69, 74 }));
            Assert.That(roll.Selection.Count, Is.EqualTo(4), "still selected after the move");

            roll.ChangeVelocity(-20);
            Assert.That(layout.NotesOf(clip).All(n => n.Velocity == 80), Is.True);

            roll.Quantize();
            Assert.That(layout.NotesOf(clip).Select(n => n.Time), Is.EqualTo(new[] { 0, 4 * TB, 9 * TB, 12 * TB }));

            SessionViewTests.Screenshot(roll, "piano-roll", 900, 500);

            roll.DeleteSelected();
            Assert.That(layout.NotesOf(clip), Is.Empty);
            for (int i = 0; i < 5; i++) layout.Undo();
            Assert.That(layout.NotesOf(clip).Select(n => n.Note), Is.EqualTo(new[] { 60, 64, 67 }), "each edit was one undo step");
        }

        [Test]
        public void NotesMoveToThePatternViewAndBackAsOneUndoStepEach()
        {
            using var rig = new KeysRig();
            var clip = rig.Clip("A", Bar, (0, 60), (4 * TB, 64));
            var layout = rig.Layout;
            layout.Ensure();
            var track = layout.SequenceOf(0)!;
            var notes = layout.NotesOf(clip);
            Assert.That(layout.CanMoveNotes(clip), Is.True, "the (fake) Modern Pattern Editor takes notes");

            Assert.That(layout.MoveNotesToPatternView(track, clip), Is.Null);
            Assert.That(layout.NotesOf(clip), Is.Empty, "out of the clip's MIDI column");
            Assert.That(layout.PatternViewNotesOf(clip), Is.EqualTo(notes), "onto the note lanes");

            layout.Undo();
            Assert.That(layout.NotesOf(clip), Is.EqualTo(notes));
            Assert.That(layout.PatternViewNotesOf(clip), Is.Empty);
            layout.Redo();
            Assert.That(layout.PatternViewNotesOf(clip), Is.EqualTo(notes));
            Assert.That(layout.NotesOf(clip), Is.Empty);

            layout.EditNotes(clip, null, new[] { new ClipNote(8 * TB, TB, 67, 90) });
            Assert.That(layout.MoveNotesToPatternView(track, clip), Is.Null, "beside the notes already there");
            Assert.That(layout.PatternViewNotesOf(clip).Select(n => n.Note), Is.EqualTo(new[] { 60, 64, 67 }));

            Assert.That(layout.MoveNotesFromPatternView(track, clip), Is.Null);
            Assert.That(layout.PatternViewNotesOf(clip), Is.Empty);
            Assert.That(layout.NotesOf(clip).Select(n => n.Note), Is.EqualTo(new[] { 60, 64, 67 }), "back where the piano roll edits them");
            layout.Undo();
            Assert.That(layout.NotesOf(clip), Is.Empty);
            Assert.That(layout.PatternViewNotesOf(clip).Count, Is.EqualTo(3));
            Assert.That(layout.MoveNotesFromPatternView(track, clip), Is.Null);

            rig.Session.Launch(track, clip);
            rig.PlayTo(12);
            Assert.That(rig.NoteOns.Select(n => n.Note), Is.EqualTo(new[] { 60, 64, 67 }), "the engine plays them again");
        }

        [Test]
        public void NotesThatDontFitTheMachinesTracksStayInTheClip()
        {
            using var rig = new KeysRig();
            var chord = Enumerable.Range(0, 6).Select(i => (0, 60 + i)).ToArray();     // more voices than the fake editor's 4
            var clip = rig.Clip("Chord", Bar, chord);
            var layout = rig.Layout;
            layout.Ensure();
            int steps = layout.UndoStack.Actions.Count();

            var problem = layout.MoveNotesToPatternView(layout.SequenceOf(0)!, clip);
            Assert.That(problem, Does.Contain("tracks"));
            Assert.That(layout.NotesOf(clip).Count, Is.EqualTo(6), "nothing moved");
            Assert.That(layout.PatternViewNotesOf(clip), Is.Empty);
            Assert.That(layout.UndoStack.Actions.Count(), Is.EqualTo(steps), "no undo step");
            Assert.That(layout.MoveNotesFromPatternView(layout.SequenceOf(0)!, clip), Does.Contain("no notes"));
        }
    }
}
