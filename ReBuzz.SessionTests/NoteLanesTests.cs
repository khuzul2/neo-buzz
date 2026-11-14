using BuzzGUI.Common;

namespace ReBuzz.SessionTests
{
    /// <summary>The MIDI column's notes as tracker lanes and the edits of those lanes (docs/specs/daw-modes.md AR3), pure.</summary>
    [TestFixture]
    public class NoteLanesTests
    {
        const int Beat = 960;                       // 4 ticks of 240
        const int Beats = 64;
        const int Length = Beats * Beat;
        static readonly RowGrid Grid = RowGrid.Beats(Enumerable.Repeat(4, Beats).ToList());
        const int Row = Beat / 4;

        static LaneEvent Note(int time, int duration, int midi, int velocity = 100, int channel = 0) =>
            new LaneEvent(time, 0x90 | channel | (midi << 8) | (velocity << 16), duration);

        static NoteLanes Lanes(IEnumerable<LaneEvent> events) => new NoteLanes(events, Grid, Length);

        [Test]
        public void ATakeShowsAsCellsOnVoices()
        {
            var events = new[] { Note(0, Row, 60), Note(2 * Row + 60, 3 * Row, 64, 90), Note(2 * Row, 4 * Row, 67) };
            var lanes = Lanes(events);
            Assert.That(lanes.Voices, Is.EqualTo(2));
            Assert.That(lanes.TryCell(0, 0, out var c0) && c0.Midi == 60, Is.True);
            Assert.That(lanes.TryCell(0, 2, out var c2) && c2.Midi == 67 && c2.Source == 2, Is.True);
            Assert.That(lanes.TryCell(1, 2, out var c1) && c1.Midi == 64 && c1.Delay == 24 && c1.Velocity == 90, Is.True);
            Assert.That(lanes.TryCell(1, 5, out var off) && off.IsOff && off.Ends == 1, Is.True, "the off that ends the note");
        }

        [Test]
        public void ANoteTypedInAnEmptyCellAddsThatNoteAndNothingElse()
        {
            var events = new[] { Note(0, 4 * Row, 60), Note(8 * Row, Row, 62), Note(100, 30, 70, 50, 3) };
            var edit = Lanes(events).SetNote(0, 5, 65);
            Assert.That(edit.Removed, Is.Empty);
            Assert.That(edit.Added, Is.EqualTo(new[] { Note(5 * Row, 3 * Row, 65) }), "to the voice's next note");
        }

        [Test]
        public void ANoteTypedWhileAnotherRingsCutsTheOneBefore()
        {
            var events = new[] { Note(0, 10 * Row, 60) };
            var edit = Lanes(events).SetNote(0, 4, 65);
            Assert.That(edit.Removed, Is.EqualTo(new[] { events[0] }));
            Assert.That(edit.Added, Is.EqualTo(new[] { Note(0, 4 * Row, 60), Note(4 * Row, Beats * Beat - 4 * Row, 65) }));
        }

        [Test]
        public void ChangingTheNoteOfACellKeepsItsTimeLengthVelocityAndChannel()
        {
            var events = new[] { Note(7, 333, 60, 77, 5), Note(2 * Row, Row, 62) };
            var edit = Lanes(events).SetNote(0, 0, 61);
            Assert.That(edit.Removed, Is.EqualTo(new[] { events[0] }));
            Assert.That(edit.Added, Is.EqualTo(new[] { Note(7, 333, 61, 77, 5) }));
        }

        [Test]
        public void VelocityDelayAndCutChangeThatNoteOnly()
        {
            var events = new[] { Note(Row, 2 * Row, 60), Note(3 * Row, Row, 62) };
            var lanes = Lanes(events);
            Assert.That(lanes.SetVelocity(0, 1, 33).Added, Is.EqualTo(new[] { Note(Row, 2 * Row, 60, 33) }));
            // A delay of 1/95 of a row (rounded) moves the start and keeps the end.
            var delay = lanes.SetDelay(0, 1, 48).Added.Single();
            Assert.That((delay.Time, delay.End), Is.EqualTo((Row + TrackerNotes.FromFraction(48, Row), Row + 2 * Row)));
            // A cut moves the end only.
            var cut = lanes.SetCut(0, 1, 48).Added.Single();
            Assert.That((cut.Time, cut.End), Is.EqualTo((Row, Row + TrackerNotes.FromFraction(48, Row))));
            Assert.That(lanes.SetVelocity(0, 1, 33).Removed, Is.EqualTo(new[] { events[0] }));
        }

        [Test]
        public void TypingTheValueAColumnAlreadyShowsChangesNothing()
        {
            var events = new[] { Note(Row + 61, 2 * Row, 60, 100), Note(5 * Row + 3, 17, 62) };
            var lanes = Lanes(events);
            Assert.That(lanes.TryCell(0, 1, out var c), Is.True);
            Assert.That(lanes.SetDelay(0, 1, c.Delay).IsEmpty, Is.True);
            Assert.That(lanes.SetNote(0, 1, 60).IsEmpty, Is.True);
            Assert.That(lanes.SetVelocity(0, 1, 100).IsEmpty, Is.True);
            Assert.That(lanes.SetCut(0, 1, c.Cut).IsEmpty, Is.True);
        }

        [Test]
        public void AnOffEndsTheRingingNoteAndClearingItLetsTheNoteRingOn()
        {
            var events = new[] { Note(0, 10 * Row, 60), Note(20 * Row, Row, 62) };
            var lanes = Lanes(events);
            var off = lanes.SetOff(0, 4);
            Assert.That(off.Added, Is.EqualTo(new[] { Note(0, 4 * Row, 60) }));
            var after = Lanes(NoteLanes.Apply(events, off));
            Assert.That(after.TryCell(0, 4, out var cell) && cell.IsOff, Is.True);
            var back = after.Clear(0, 4);
            Assert.That(back.Added, Is.EqualTo(new[] { Note(0, 20 * Row, 60) }), "to the voice's next note");
        }

        [Test]
        public void ClearingANoteRemovesIt()
        {
            var events = new[] { Note(0, Row, 60), Note(2 * Row, Row, 62) };
            var edit = Lanes(events).Clear(0, 2);
            Assert.That(edit.Removed, Is.EqualTo(new[] { events[1] }));
            Assert.That(edit.Added, Is.Empty);
        }

        [Test]
        public void ThingsTheLanesDontShowStayInTheColumn()
        {
            var cc = new LaneEvent(10, 0xB0 | (1 << 8) | (64 << 16), 0);
            var past = Note(Length + 5, 10, 60);
            var high = Note(0, 10, 125);
            var lanes = Lanes(new[] { cc, past, high, Note(0, Row, 60) });
            Assert.That((lanes.NoteCount, lanes.Hidden, lanes.Voices), Is.EqualTo((3, 2, 1)));
            Assert.That(lanes.SetVelocity(0, 0, 50).Removed, Is.EqualTo(new[] { Note(0, Row, 60) }));
        }

        [Test]
        public void AThousandNotesThroughRandomTrackerEditsKeepTheUntouchedOnesExact()
        {
            foreach (int seed in new[] { 1, 2, 3 })
            {
                var rng = new Random(seed);
                var events = new List<LaneEvent>();
                for (int i = 0; i < 1000; i++)
                    events.Add(Note(rng.Next(0, Length - 1), rng.Next(1, 3 * Beat), rng.Next(24, 108), rng.Next(1, 128), rng.Next(0, 3)));
                for (int i = 0; i < 20; i++) events.Add(new LaneEvent(rng.Next(0, Length - 1), 0xB0 | (rng.Next(0, 120) << 8) | (rng.Next(0, 128) << 16), 0));
                var original = events.ToList();
                var touched = new List<LaneEvent>();         // events an edit took away (the originals it changed)
                int edits = 0;
                for (int n = 0; n < 300; n++)
                {
                    var lanes = Lanes(events);
                    var cells = lanes.Cells;
                    int voice, row;
                    if (rng.Next(4) == 0 || cells.Count == 0) { voice = rng.Next(0, lanes.Voices + 1); row = rng.Next(0, Beats * 4); }
                    else { var c = cells[rng.Next(cells.Count)]; voice = c.Voice; row = c.Row; }
                    NoteLaneEdit edit = rng.Next(8) switch
                    {
                        0 => lanes.SetNote(voice, row, rng.Next(0, 120)),
                        1 => lanes.SetOff(voice, row),
                        2 => lanes.Clear(voice, row),
                        3 => lanes.SetVelocity(voice, row, rng.Next(0, 128)),
                        4 => lanes.SetDelay(voice, row, rng.Next(0, 96)),
                        5 => lanes.SetCut(voice, row, rng.Next(0, 96)),
                        6 => lanes.Transpose(voice, row, rng.Next(-12, 13)),
                        _ => lanes.SetNote(voice, row, rng.Next(0, 120), rng.Next(1, 128)),
                    };
                    if (edit.Refused != null || edit.IsEmpty) continue;
                    edits++;
                    foreach (var r in edit.Removed) Assert.That(events, Does.Contain(r), "an edit only takes away events there are");
                    // At most the cell's note and the note it cuts or lets ring.
                    Assert.That(edit.Removed.Count, Is.LessThanOrEqualTo(1));
                    Assert.That(edit.Added.Count, Is.LessThanOrEqualTo(2));
                    touched.AddRange(edit.Removed);
                    var next = NoteLanes.Apply(events, edit);
                    Assert.That(next.Count, Is.EqualTo(events.Count - edit.Removed.Count + edit.Added.Count));
                    events = next;
                }
                Assert.That(edits, Is.GreaterThan(150), "the run did edit");
                // Every original event no edit took away is still there, exactly: nothing was rounded or moved.
                var left = events.ToList();
                foreach (var e in original.Except(touched.Distinct()))
                {
                    int expected = original.Count(o => o == e) - touched.Count(t => t == e);
                    Assert.That(left.Count(o => o == e), Is.GreaterThanOrEqualTo(Math.Max(0, expected)), $"seed {seed}: {e}");
                }
                // And the controller events never moved.
                Assert.That(events.Where(e => !e.IsNote).OrderBy(e => e.Time), Is.EqualTo(original.Where(e => !e.IsNote).OrderBy(e => e.Time)));
            }
        }

        [Test]
        public void ProjectingAndReadingBackWritesNothing()
        {
            var rng = new Random(9);
            var events = Enumerable.Range(0, 1000).Select(_ => Note(rng.Next(0, Length - 1), rng.Next(1, 2 * Beat), rng.Next(24, 108), rng.Next(1, 128))).ToList();
            var lanes = Lanes(events);
            foreach (var c in lanes.Cells.Where(c => !c.IsOff))
            {
                // Typing what a cell shows is what the user does when they pass over it; none of it may touch the note.
                Assert.That(lanes.SetNote(c.Voice, c.Row, c.Midi).IsEmpty, Is.True);
                Assert.That(lanes.SetVelocity(c.Voice, c.Row, c.Velocity).IsEmpty, Is.True);
                Assert.That(lanes.SetDelay(c.Voice, c.Row, c.Delay).IsEmpty, Is.True);
                Assert.That(lanes.SetCut(c.Voice, c.Row, c.Cut).IsEmpty, Is.True);
            }
        }
    }
}
