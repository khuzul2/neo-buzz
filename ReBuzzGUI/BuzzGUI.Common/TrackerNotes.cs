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
    /// One row of one track's lanes: a note (<see cref="Midi"/> 0-127) or a note-off (-1), its
    /// velocity (0: none), and its delay and cut in 1/<see cref="TrackerNotes.Fraction"/> of the
    /// row (0: none).
    /// </summary>
    public readonly record struct TrackerRow(int Track, int Row, int Midi, int Velocity, int Delay, int Cut);

    public static class TrackerNotes
    {
        /// <summary>Delays and cuts count in 1/95 of a row (the Modern Pattern Editor's 0x5f).</summary>
        public const int Fraction = 95;

        static int ToFraction(int offset, int rowLength) =>
            Math.Clamp((int)Math.Round(offset * (double)Fraction / rowLength), 0, Fraction);

        static int FromFraction(int fraction, int rowLength) =>
            (int)Math.Round(fraction * (double)rowLength / Fraction);

        /// <summary>
        /// Notes laid out on tracker lanes. Each note goes to the first track free from its row
        /// on (a track is free once its previous note has ended by the row's start); a note ends
        /// with a cut when it ends in its own row, with nothing when the track's next note starts
        /// by then, else with a note-off (delayed into its row). Notes needing more than
        /// <paramref name="maxTracks"/> tracks, or starting past the pattern, are left out.
        /// </summary>
        public static List<TrackerRow> ToRows(IEnumerable<TrackerNote> notes, int rowLength, int length, int maxTracks, out int tracks, out int dropped)
        {
            rowLength = Math.Max(1, rowLength);
            var ordered = notes.Where(n => n.Time >= 0).OrderBy(n => n.Time).ThenBy(n => n.Midi).ToList();
            var ends = new List<int>();                       // per track: when its last note ends
            var lanes = new List<List<TrackerNote>>();
            dropped = 0;
            foreach (var n in ordered)
            {
                if (n.Time >= length) { dropped++; continue; }
                int rowStart = n.Time / rowLength * rowLength;
                int t = ends.FindIndex(e => e <= rowStart);
                if (t < 0)
                {
                    if (ends.Count >= maxTracks) { dropped++; continue; }
                    t = ends.Count;
                    ends.Add(0);
                    lanes.Add(new List<TrackerNote>());
                }
                ends[t] = n.Time + Math.Max(1, n.Duration);
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
                    int row = n.Time / rowLength;
                    int delay = ToFraction(n.Time - row * rowLength, rowLength);
                    int end = Math.Min(n.Time + Math.Max(1, n.Duration), length);
                    int endRow = end / rowLength;
                    int nextRow = i + 1 < lane.Count ? lane[i + 1].Time / rowLength : int.MaxValue;
                    int cut = 0;
                    if (endRow == row)
                        cut = Math.Max(delay + 1, ToFraction(end - row * rowLength, rowLength));
                    rows.Add(new TrackerRow(t, row, Math.Clamp(n.Midi, 0, 127), Math.Clamp(n.Velocity, 1, 127), delay, Math.Min(cut, Fraction)));
                    if (cut > 0 || nextRow <= endRow || end >= length) continue;
                    rows.Add(new TrackerRow(t, endRow, -1, 0, ToFraction(end - endRow * rowLength, rowLength), 0));
                }
            }
            return rows.OrderBy(r => r.Track).ThenBy(r => r.Row).ToList();
        }

        /// <summary>
        /// The notes tracker lanes play: a note starts at its row plus its delay and lasts until
        /// its cut, or the track's next note or note-off (plus its delay), or the pattern's end.
        /// </summary>
        public static List<TrackerNote> FromRows(IEnumerable<TrackerRow> rows, int rowLength, int length, int defaultVelocity = 100)
        {
            rowLength = Math.Max(1, rowLength);
            var notes = new List<TrackerNote>();
            foreach (var lane in rows.GroupBy(r => r.Track))
            {
                var ordered = lane.OrderBy(r => r.Row).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var r = ordered[i];
                    if (r.Midi < 0) continue;
                    int start = r.Row * rowLength + FromFraction(r.Delay, rowLength);
                    int end;
                    if (r.Cut > 0)
                        end = r.Row * rowLength + FromFraction(r.Cut, rowLength);
                    else if (i + 1 < ordered.Count)
                        end = ordered[i + 1].Row * rowLength + FromFraction(ordered[i + 1].Delay, rowLength);
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
    }
}
