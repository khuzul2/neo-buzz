using BuzzGUI.Common;

namespace ReBuzz.SessionTests
{
    /// <summary>The tracker's note+velocity column pairs for chords (docs/specs/notes.md section 9, CU6b), pure: the count, what it shows and when a pair can go.</summary>
    [TestFixture]
    public class NoteColumnsTests
    {
        const int Beat = 960;
        const int Beats = 64;
        const int Row = Beat / 4;
        static readonly RowGrid Grid = RowGrid.Beats(Enumerable.Repeat(4, Beats).ToList());

        static LaneEvent Note(int time, int duration, int midi) => new LaneEvent(time, 0x90 | (midi << 8) | (100 << 16), duration);

        static NoteLanes Chord(int size) => new NoteLanes(Enumerable.Range(0, size).Select(i => Note(0, 4 * Row, 24 + 3 * i)), Grid, Beats * Beat);

        [Test]
        public void TheCountIsKeptInTheLanesMetadataAndReadBack()
        {
            var meta = new Dictionary<string, string>();
            Assert.That(NoteColumns.Stored(meta), Is.Null, "no count: the clip shows as it always did");
            NoteColumns.Store(meta, 5);
            Assert.That(meta[NoteColumns.Key], Is.EqualTo("5"));
            Assert.That(NoteColumns.Stored(meta), Is.EqualTo(5));
            NoteColumns.Store(meta, null);
            Assert.That(meta, Is.Empty, "taking it away leaves nothing behind");
            Assert.That(NoteColumns.Stored(null), Is.Null);
            meta[NoteColumns.Key] = "many";
            Assert.That(NoteColumns.Stored(meta), Is.Null, "something that is not a count is no count");
            meta[NoteColumns.Key] = "0";
            Assert.That(NoteColumns.Stored(meta), Is.Null);
            meta[NoteColumns.Key] = "99";
            Assert.That(NoteColumns.Stored(meta), Is.EqualTo(NoteColumns.Max), "never more than the most a machine plays");
            NoteColumns.Store(meta, 500);
            Assert.That(meta[NoteColumns.Key], Is.EqualTo(NoteColumns.Max.ToString()));
        }

        [Test]
        public void AClipWithNoCountShowsWhatItAlwaysShowed()
        {
            // The old rule: the voices the notes need and one free, nothing while the lanes are hidden (a note count of 32 at most).
            for (int need = 0; need <= 31; need++)
            {
                Assert.That(NoteColumns.Shown(null, need, true), Is.EqualTo(Math.Min(need + 1, 32)), $"{need} needed");
                Assert.That(NoteColumns.Shown(null, need, false), Is.EqualTo(0));
            }
            Assert.That(NoteColumns.Shown(null, 32, true), Is.EqualTo(32));
        }

        [Test]
        public void ACountNeverShowsFewerPairsThanTheNotesNeed()
        {
            Assert.That(NoteColumns.Shown(4, 2, true), Is.EqualTo(4));
            Assert.That(NoteColumns.Shown(2, 5, true), Is.EqualTo(5), "the notes are never hidden");
            Assert.That(NoteColumns.Shown(1, 0, false), Is.EqualTo(1), "a count asks for the lanes by itself");
        }

        [Test]
        public void PlusStopsAtTheMostAndMinusNeverHidesANote()
        {
            Assert.That(NoteColumns.Add(3, out int count), Is.Null);
            Assert.That(count, Is.EqualTo(4));
            Assert.That(NoteColumns.Add(NoteColumns.Max, out count), Does.Contain("32"));
            Assert.That(count, Is.EqualTo(NoteColumns.Max), "refused: unchanged");

            var chord = Chord(3);
            Assert.That(NoteColumns.Remove(5, chord, out count), Is.Null);
            Assert.That(count, Is.EqualTo(4));
            Assert.That(NoteColumns.Remove(4, chord, out count), Is.Null);
            Assert.That(count, Is.EqualTo(3), "down to the three the chord needs");
            var why = NoteColumns.Remove(3, chord, out count);
            Assert.That(why, Does.Contain("1 note would not be shown"));
            Assert.That(count, Is.EqualTo(3));
            Assert.That(NoteColumns.Remove(1, Chord(0), out count), Does.Contain("at least one"));
        }

        [Test]
        public void AChordIsOneNotePerPairAtTheSameTime()
        {
            var lanes = Chord(4);
            Assert.That(lanes.Voices, Is.EqualTo(4));
            for (int v = 0; v < 4; v++) Assert.That(lanes.TryCell(v, 0, out var c) && c.Midi == 24 + 3 * v, Is.True, $"pair {v + 1}");
            Assert.That(lanes.NotesBeyond(4), Is.EqualTo(0));
            Assert.That(lanes.NotesBeyond(3), Is.EqualTo(1));
            Assert.That(lanes.NotesBeyond(0), Is.EqualTo(4));
        }

        [Test]
        public void ThirtyTwoNotesAtOnceFillThirtyTwoPairs()
        {
            var lanes = Chord(32);
            Assert.That(lanes.Voices, Is.EqualTo(32));
            Assert.That(NoteColumns.Shown(null, lanes.Voices, true), Is.EqualTo(32));
            Assert.That(NoteColumns.Shown(32, lanes.Voices, true), Is.EqualTo(32));
        }
    }
}
