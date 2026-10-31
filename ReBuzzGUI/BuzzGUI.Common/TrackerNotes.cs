#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 0.7.1): notes as tracker rows and back.
//
// A tracker plays MIDI notes from lanes, one set per track: a note (or a note-off) on a row,
// with a velocity, a delay into the row and a cut (both in 1/95 of the row, 0 for none), as
// the Modern Pattern Editor's MIDI Note, Velocity, Note Delay and Note Cut columns do. The
// Session View moves a clip's recorded notes into those lanes and back with this. Pure: no
// dependency on anything else, so ReBuzz.SessionTests links this file.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BuzzGUI.Common
{
    /// <summary>A note: start and length in pattern time (1/240 tick), MIDI note number and velocity.</summary>
    public readonly record struct TrackerNote(int Time, int Duration, int Midi, int Velocity);

    /// <summary>
    /// One row of one track's lanes: where the row starts and how long it is (pattern time), a
    /// note (<see cref="Midi"/> 0-127) or a note-off (-1), its velocity (0: none), and its delay
    /// and cut in 1/<see cref="TrackerNotes.Fraction"/> of the row (0: none).
    /// </summary>
    public readonly record struct TrackerRow(int Track, int Time, int Length, int Midi, int Velocity, int Delay, int Cut);

    /// <summary>
    /// Where a pattern's rows are: every row the same length, or a number of rows in each beat
    /// (the Modern Pattern Editor's rows per beat, which needn't divide the beat evenly: a row
    /// starts at beat + beat length × row / rows, rounded down, as the editor places them).
    /// </summary>
    public sealed class RowGrid
    {
        /// <summary>A beat in pattern time: 4 ticks of 240.</summary>
        public const int BeatLength = 4 * 240;

        readonly int uniform;
        readonly int[] perBeat;
        readonly int[] firstRow;          // the first row of each listed beat, then the row after them

        RowGrid(int uniform, int[] perBeat)
        {
            this.uniform = uniform;
            this.perBeat = perBeat;
            if (perBeat == null) return;
            firstRow = new int[perBeat.Length + 1];
            for (int b = 0; b < perBeat.Length; b++) firstRow[b + 1] = firstRow[b] + perBeat[b];
        }

        /// <summary>Rows of <paramref name="rowLength"/> each.</summary>
        public static RowGrid Uniform(int rowLength) => new RowGrid(Math.Max(1, rowLength), null);

        /// <summary><paramref name="rowsPerBeat"/> rows in each beat (the last count goes on for the beats after).</summary>
        public static RowGrid Beats(IReadOnlyList<int> rowsPerBeat)
        {
            var rows = (rowsPerBeat ?? Array.Empty<int>()).Select(r => Math.Clamp(r, 1, BeatLength)).ToArray();
            return new RowGrid(0, rows.Length == 0 ? new[] { 4 } : rows);
        }

        int RowsIn(int beat) => perBeat[Math.Min(beat, perBeat.Length - 1)];

        int FirstRowOf(int beat) => beat < perBeat.Length
            ? firstRow[beat]
            : firstRow[perBeat.Length] + (beat - perBeat.Length) * perBeat[perBeat.Length - 1];

        /// <summary>The row a time is in.</summary>
        public int RowAt(int time)
        {
            time = Math.Max(0, time);
            if (perBeat == null) return time / uniform;
            int beat = time / BeatLength;
            int rows = RowsIn(beat);
            int offset = time - beat * BeatLength;
            // The last row starting at or before the time (rows start at BeatLength * row / rows, rounded down).
            int inBeat = Math.Min(rows - 1, offset * rows / BeatLength);
            while (inBeat + 1 < rows && BeatLength * (inBeat + 1) / rows <= offset) inBeat++;
            while (inBeat > 0 && BeatLength * inBeat / rows > offset) inBeat--;
            return FirstRowOf(beat) + inBeat;
        }

        /// <summary>Where a row starts.</summary>
        public int Start(int row)
        {
            row = Math.Max(0, row);
            if (perBeat == null) return row * uniform;
            int beat;
            if (row < firstRow[perBeat.Length])
            {
                beat = Array.BinarySearch(firstRow, row);
                if (beat < 0) beat = ~beat - 1;
                while (beat + 1 < firstRow.Length && firstRow[beat + 1] <= row) beat++;   // skip beats of no rows
            }
            else
                beat = perBeat.Length + (row - firstRow[perBeat.Length]) / perBeat[perBeat.Length - 1];
            int rows = RowsIn(beat);
            return beat * BeatLength + BeatLength * (row - FirstRowOf(beat)) / rows;
        }

        /// <summary>How long a row is.</summary>
        public int Length(int row) => Math.Max(1, Start(row + 1) - Start(row));
    }

    public static class TrackerNotes
    {
        /// <summary>Delays and cuts count in 1/95 of a row (the Modern Pattern Editor's 0x5f).</summary>
        public const int Fraction = 95;

        static int ToFraction(int offset, int rowLength) =>
            Math.Clamp((int)Math.Round(offset * (double)Fraction / rowLength), 0, Fraction);

        static int FromFraction(int fraction, int rowLength) =>
            (int)Math.Round(fraction * (double)rowLength / Fraction);

        /// <summary>
        /// Notes laid out on tracker lanes. Each note goes to the first track free for it: one
        /// whose last note ended before the note's row, or exactly where the note starts (a
        /// note-on ends the note before it on its track). A note ends with a cut when it ends in
        /// its own row, with nothing when its track's next note starts right then, else with a
        /// note-off (delayed into its row). Notes needing more than <paramref name="maxTracks"/>
        /// tracks, above <paramref name="maxNote"/>, or starting past the pattern, are left out.
        /// </summary>
        public static List<TrackerRow> ToRows(IEnumerable<TrackerNote> notes, RowGrid grid, int length, int maxTracks, out int tracks, out int dropped, int maxNote = 127)
        {
            var ordered = notes.Where(n => n.Time >= 0).OrderBy(n => n.Time).ThenBy(n => n.Midi).ToList();
            var ends = new List<int>();                       // per track: when its last note ends
            var lastRows = new List<int>();                   // and where its row starts
            var lanes = new List<List<TrackerNote>>();
            dropped = 0;
            foreach (var n in ordered)
            {
                if (n.Time >= length || n.Midi < 0 || n.Midi > maxNote) { dropped++; continue; }
                int rowStart = grid.Start(grid.RowAt(n.Time));
                int t = -1;
                for (int i = 0; i < ends.Count && t < 0; i++)
                    if (ends[i] < rowStart || (ends[i] == n.Time && lastRows[i] < rowStart)) t = i;
                if (t < 0)
                {
                    if (ends.Count >= maxTracks) { dropped++; continue; }
                    t = ends.Count;
                    ends.Add(0);
                    lastRows.Add(0);
                    lanes.Add(new List<TrackerNote>());
                }
                ends[t] = n.Time + Math.Max(1, n.Duration);
                lastRows[t] = rowStart;
                lanes[t].Add(n);
            }
            tracks = lanes.Count;

            var rows = new List<TrackerRow>();
            for (int t = 0; t < lanes.Count; t++)
            {
                var lane = lanes[t];
                for (int i = 0; i < lane.Count; i++)
                {
                    var n = lane[i];
                    int row = grid.RowAt(n.Time);
                    int start = grid.Start(row), rowLength = grid.Length(row);
                    int delay = ToFraction(n.Time - start, rowLength);
                    int end = Math.Min(n.Time + Math.Max(1, n.Duration), length);
                    int endRow = grid.RowAt(end);
                    int cut = 0;
                    if (endRow == row)
                        cut = Math.Min(Fraction, Math.Max(delay + 1, ToFraction(end - start, rowLength)));
                    rows.Add(new TrackerRow(t, start, rowLength, Math.Clamp(n.Midi, 0, 127), Math.Clamp(n.Velocity, 1, 127), delay, cut));
                    bool endedByNext = i + 1 < lane.Count && lane[i + 1].Time == end;
                    if (cut > 0 || endedByNext || end >= length) continue;
                    int offStart = grid.Start(endRow), offLength = grid.Length(endRow);
                    rows.Add(new TrackerRow(t, offStart, offLength, -1, 0, ToFraction(end - offStart, offLength), 0));
                }
            }
            return rows.OrderBy(r => r.Track).ThenBy(r => r.Time).ToList();
        }

        /// <summary>
        /// The notes tracker lanes play: a note starts at its row plus its delay and lasts until
        /// its cut, or the track's next note or note-off (plus its delay), or the pattern's end.
        /// </summary>
        public static List<TrackerNote> FromRows(IEnumerable<TrackerRow> rows, int length, int defaultVelocity = 100)
        {
            var notes = new List<TrackerNote>();
            foreach (var lane in rows.GroupBy(r => r.Track))
            {
                var ordered = lane.OrderBy(r => r.Time).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var r = ordered[i];
                    if (r.Midi < 0) continue;
                    int rowLength = Math.Max(1, r.Length);
                    int start = r.Time + FromFraction(r.Delay, rowLength);
                    int end;
                    if (r.Cut > 0)
                        end = r.Time + FromFraction(r.Cut, rowLength);
                    else if (i + 1 < ordered.Count)
                        end = ordered[i + 1].Time + FromFraction(ordered[i + 1].Delay, Math.Max(1, ordered[i + 1].Length));
                    else
                        end = length;
                    if (start >= length) continue;
                    end = Math.Min(end, length);
                    notes.Add(new TrackerNote(start, Math.Max(1, end - start), r.Midi, r.Velocity > 0 ? r.Velocity : defaultVelocity));
                }
            }
            return notes.OrderBy(n => n.Time).ThenBy(n => n.Midi).ToList();
        }

        /// <summary>Notes as the flat int triples (time, note-on message, duration) the editors exchange.</summary>
        public static int[] Pack(IEnumerable<TrackerNote> notes)
        {
            var list = new List<int>();
            foreach (var n in notes)
            {
                list.Add(n.Time);
                list.Add(0x90 | ((n.Midi & 0x7F) << 8) | ((Math.Clamp(n.Velocity, 1, 127) & 0x7F) << 16));
                list.Add(n.Duration);
            }
            return list.ToArray();
        }

        /// <summary>The notes of int triples (time, note-on message, duration); anything else is skipped.</summary>
        public static List<TrackerNote> Unpack(int[] data)
        {
            var notes = new List<TrackerNote>();
            if (data == null) return notes;
            for (int i = 0; i + 2 < data.Length; i += 3)
            {
                int value = data[i + 1];
                int velocity = (value >> 16) & 0x7F;
                if ((value & 0xF0) != 0x90 || velocity == 0) continue;
                notes.Add(new TrackerNote(data[i], Math.Max(1, data[i + 2]), (value >> 8) & 0x7F, velocity));
            }
            return notes;
        }

        /// <summary>
        /// <paramref name="from"/> less one of each note in <paramref name="taken"/> (exact
        /// matches; notes not there are ignored), as triples.
        /// </summary>
        public static int[] Without(int[] from, int[] taken)
        {
            var left = Unpack(from);
            foreach (var n in Unpack(taken))
            {
                int i = left.IndexOf(n);
                if (i >= 0) left.RemoveAt(i);
            }
            return Pack(left);
        }

        /// <summary>The notes of <paramref name="after"/> that <paramref name="before"/> lacks (one for one), as triples.</summary>
        public static int[] Added(int[] before, int[] after) => Without(after, before);
    }
}
