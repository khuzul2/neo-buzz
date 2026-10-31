using BuzzGUI.Common;

namespace ReBuzz.SessionTests
{
    /// <summary>Notes as tracker rows and back (docs/specs/session-view.md 0.7.1), pure.</summary>
    [TestFixture]
    public class TrackerNotesTests
    {
        const int Row = 240;               // a row a tick (4 rows a beat)
        const int Length = 16 * Row;

        [Test]
        public void ANoteIsARowWithItsDelayAndAnOffWhereItEnds()
        {
            var rows = TrackerNotes.ToRows(new[] { new TrackerNote(2 * Row + 60, 3 * Row, 60, 90) }, Row, Length, 8, out int tracks, out int dropped);
            Assert.That((tracks, dropped), Is.EqualTo((1, 0)));
            Assert.That(rows, Is.EqualTo(new[]
            {
                new TrackerRow(0, 2, 60, 90, 24, 0),                // 60/240 of a row = 23.75/95
                new TrackerRow(0, 5, -1, 0, 24, 0)                  // ends at 5 rows + 60
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
            }, Row, Length, 8, out int tracks, out _);
            Assert.That(tracks, Is.EqualTo(1));
            Assert.That(rows, Is.EqualTo(new[]
            {
                new TrackerRow(0, 0, 60, 100, 0, 48),
                new TrackerRow(0, 1, 62, 100, 0, 0),
                new TrackerRow(0, 5, 64, 100, 0, 0),
                new TrackerRow(0, 6, -1, 0, 0, 0)
            }));
        }

        [Test]
        public void AChordTakesOneTrackANoteAndTooManyAreLeftOut()
        {
            var chord = new[] { new TrackerNote(0, Row, 60, 100), new TrackerNote(0, Row, 64, 100), new TrackerNote(0, Row, 67, 100) };
            var rows = TrackerNotes.ToRows(chord, Row, Length, 8, out int tracks, out int dropped);
            Assert.That((tracks, dropped), Is.EqualTo((3, 0)));
            Assert.That(rows.Where(r => r.Midi >= 0).Select(r => (r.Track, r.Midi)), Is.EqualTo(new[] { (0, 60), (1, 64), (2, 67) }));
            TrackerNotes.ToRows(chord, Row, Length, 2, out tracks, out dropped);
            Assert.That((tracks, dropped), Is.EqualTo((2, 1)));
        }

        [Test]
        public void RowsPlayBackTheNotesTheyWereMadeFrom()
        {
            var random = new Random(3);
            var notes = Enumerable.Range(0, 40).Select(_ =>
                new TrackerNote(random.Next(0, Length - Row), random.Next(10, 4 * Row), random.Next(36, 84), random.Next(1, 128))).ToList();
            var rows = TrackerNotes.ToRows(notes, Row, Length, 64, out _, out int dropped);
            Assert.That(dropped, Is.EqualTo(0));
            var back = TrackerNotes.FromRows(rows, Row, Length);
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
