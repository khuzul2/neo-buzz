using BuzzGUI.Common;

namespace ReBuzz.SessionTests
{
    /// <summary>Notes as tracker rows and back (docs/specs/session-view.md 0.7.1), pure.</summary>
    [TestFixture]
    public class TrackerNotesTests
    {
        const int Row = 240;               // a row a tick (4 rows a beat)
        const int Length = 16 * Row;
        static readonly RowGrid Grid = RowGrid.Uniform(Row);

        static TrackerRow R(int track, int row, int midi, int velocity, int delay, int cut) =>
            new TrackerRow(track, row * Row, Row, midi, velocity, delay, cut);

        [Test]
        public void ANoteIsARowWithItsDelayAndAnOffWhereItEnds()
        {
            var rows = TrackerNotes.ToRows(new[] { new TrackerNote(2 * Row + 60, 3 * Row, 60, 90) }, Grid, Length, 8, out int tracks, out int dropped);
            Assert.That((tracks, dropped), Is.EqualTo((1, 0)));
            Assert.That(rows, Is.EqualTo(new[]
            {
                R(0, 2, 60, 90, 24, 0),                // 60/240 of a row = 23.75/95
                R(0, 5, -1, 0, 24, 0)                  // ends at 5 rows + 60
            }));
        }

        [Test]
        public void AShortNoteIsCutInItsRowAndTheNextNoteOnTheTrackEndsTheOneBefore()
        {
            var rows = TrackerNotes.ToRows(new[]
            {
                new TrackerNote(0, 120, 60, 100),                   // ends mid-row: a cut
                new TrackerNote(Row, 4 * Row, 62, 100),             // ends where the next starts: no off
                new TrackerNote(5 * Row, Row, 64, 100),
            }, Grid, Length, 8, out int tracks, out _);
            Assert.That(tracks, Is.EqualTo(1));
            Assert.That(rows, Is.EqualTo(new[]
            {
                R(0, 0, 60, 100, 0, 48),
                R(0, 1, 62, 100, 0, 0),
                R(0, 5, 64, 100, 0, 0),
                R(0, 6, -1, 0, 0, 0)
            }));
        }

        [Test]
        public void AChordTakesOneTrackANoteAndTooManyAreLeftOut()
        {
            var chord = new[] { new TrackerNote(0, Row, 60, 100), new TrackerNote(0, Row, 64, 100), new TrackerNote(0, Row, 67, 100) };
            var rows = TrackerNotes.ToRows(chord, Grid, Length, 8, out int tracks, out int dropped);
            Assert.That((tracks, dropped), Is.EqualTo((3, 0)));
            Assert.That(rows.Where(r => r.Midi >= 0).Select(r => (r.Track, r.Midi)), Is.EqualTo(new[] { (0, 60), (1, 64), (2, 67) }));
            TrackerNotes.ToRows(chord, Grid, Length, 2, out tracks, out dropped);
            Assert.That((tracks, dropped), Is.EqualTo((2, 1)));
        }

        [Test]
        public void RowsPlayBackTheNotesTheyWereMadeFrom()
        {
            var random = new Random(3);
            var notes = Enumerable.Range(0, 40).Select(_ =>
                new TrackerNote(random.Next(0, Length - Row), random.Next(10, 4 * Row), random.Next(36, 84), random.Next(1, 128))).ToList();
            var rows = TrackerNotes.ToRows(notes, Grid, Length, 64, out _, out int dropped);
            Assert.That(dropped, Is.EqualTo(0));
            var back = TrackerNotes.FromRows(rows, Length);
            Assert.That(back.Count, Is.EqualTo(notes.Count));
            var expected = notes.OrderBy(n => n.Time).ThenBy(n => n.Midi).ToList();
            for (int i = 0; i < back.Count; i++)
            {
                Assert.That(back[i].Midi, Is.EqualTo(expected[i].Midi), $"note {i}");
                Assert.That(back[i].Velocity, Is.EqualTo(expected[i].Velocity));
                Assert.That(Math.Abs(back[i].Time - expected[i].Time) <= 2, Is.True, $"start of {i} within a delay step");
                int end = Math.Min(expected[i].Time + expected[i].Duration, Length);
                Assert.That(Math.Abs(back[i].Time + back[i].Duration - end) <= 4, Is.True, $"end of {i}: {back[i].Time + back[i].Duration} vs {end}");
            }
        }

        [Test]
        public void ANoteEndingOnARowLineBeforeADelayedNoteGetsItsOff()
        {
            // The review's case: 0-480 and a note at 540 (row 2, delayed): the first must end at 480.
            var notes = new[] { new TrackerNote(0, 2 * Row, 60, 100), new TrackerNote(2 * Row + 60, Row, 62, 100) };
            var rows = TrackerNotes.ToRows(notes, Grid, Length, 8, out int tracks, out _);
            Assert.That(tracks, Is.EqualTo(2), "the delayed note can't share the row of the first one's off");
            var back = TrackerNotes.FromRows(rows, Length);
            Assert.That(back[0], Is.EqualTo(new TrackerNote(0, 2 * Row, 60, 100)));
            // Two notes in one row never share a track, even back to back.
            var tight = new[] { new TrackerNote(0, 60, 60, 100), new TrackerNote(60, 60, 62, 100) };
            TrackerNotes.ToRows(tight, Grid, Length, 8, out tracks, out _);
            Assert.That(tracks, Is.EqualTo(2));
        }

        [Test]
        public void NotesAboveTheEditorsRangeAreLeftOut()
        {
            TrackerNotes.ToRows(new[] { new TrackerNote(0, Row, 119, 100), new TrackerNote(Row, Row, 120, 100) }, Grid, Length, 8, out _, out int dropped, maxNote: 119);
            Assert.That(dropped, Is.EqualTo(1));
        }

        [Test]
        public void RowsPerBeatThatDontDivideTheBeatPlaceRowsAsTheEditorDoes()
        {
            var grid = RowGrid.Beats(new[] { 7 });
            for (int r = 0; r < 30; r++)
            {
                int beat = r / 7, inBeat = r % 7;
                int start = beat * 960 + 960 * inBeat / 7;             // the editor's row times
                Assert.That(grid.Start(r), Is.EqualTo(start), $"row {r}");
                Assert.That(grid.RowAt(start), Is.EqualTo(r));
                Assert.That(grid.RowAt(start + grid.Length(r) - 1), Is.EqualTo(r));
            }
            var mixed = RowGrid.Beats(new[] { 4, 3, 0 });          // a beat of no rows counts as one
            Assert.That(Enumerable.Range(0, 9).Select(mixed.Start), Is.EqualTo(new[] { 0, 240, 480, 720, 960, 1280, 1600, 1920, 2880 }));
            var notes = new[] { new TrackerNote(1000, 500, 60, 100), new TrackerNote(2000, 300, 64, 90) };
            var back = TrackerNotes.FromRows(TrackerNotes.ToRows(notes, grid, 8 * 960, 8, out _, out _), 8 * 960);
            for (int i = 0; i < 2; i++)
            {
                Assert.That(Math.Abs(back[i].Time - notes[i].Time) <= 1, Is.True, $"start {i}");
                Assert.That(Math.Abs(back[i].Duration - notes[i].Duration) <= 2, Is.True, $"length {i}");
            }
        }

        [Test]
        public void NotesComeOffAndBackOneForOne()
        {
            var a = new TrackerNote(0, 10, 60, 100);
            var b = new TrackerNote(5, 10, 62, 100);
            var all = TrackerNotes.Pack(new[] { a, b, a });
            Assert.That(TrackerNotes.Unpack(TrackerNotes.Without(all, TrackerNotes.Pack(new[] { a }))), Is.EqualTo(new[] { b, a }));
            Assert.That(TrackerNotes.Unpack(TrackerNotes.Added(TrackerNotes.Pack(new[] { a }), all)), Is.EqualTo(new[] { b, a }));
            Assert.That(TrackerNotes.Without(all, TrackerNotes.Pack(new[] { new TrackerNote(1, 1, 1, 1) })), Is.EqualTo(all), "missing notes are ignored");
        }

        [Test]
        public void NotesPackIntoTriplesAndBack()
        {
            var notes = new[] { new TrackerNote(10, 20, 60, 90), new TrackerNote(30, 40, 61, 127) };
            var data = TrackerNotes.Pack(notes);
            Assert.That(data.Length, Is.EqualTo(6));
            Assert.That(TrackerNotes.Unpack(data), Is.EqualTo(notes));
            Assert.That(TrackerNotes.Unpack(new[] { 0, 0x80 | (60 << 8), 5 }), Is.Empty, "not a note-on");
        }
    }
}
