#nullable disable
// Fork (docs/specs/mcp-server.md §12.2, U1): notes for machines that take them only in their Note
// track parameter, as trackers do (the Pedal synths, many native machines), not as MIDI. Notes are
// given voices (one track each), and become per-track Note events (Buzz notes: octave << 4 | 1…12,
// 255 = off) in the pattern's engine-played parameter columns, and are read back from them. Also
// the pitch offset a profiled manifest records, so the written pitch is the pitch heard. Pure.

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Agent.Model
{
    public static class TrackNotes
    {
        public const int Off = 255;

        /// <summary>The Buzz note for a MIDI pitch (60 → C-5, as ReBuzz's own BuzzNote), or -1 outside 0…119.</summary>
        public static int ToBuzz(int midi) => midi < 0 || midi > 119 ? -1 : Pitch.ToBuzz(midi);

        /// <summary>The MIDI pitch of a Buzz note, or -1 when it isn't one (0 = no value, 255 = off).</summary>
        public static int FromBuzz(int buzz) => buzz >= Off || (buzz >> 4) > 9 ? -1 : Pitch.FromBuzz(buzz);

        /// <summary>
        /// Voices for notes: each note goes to the lowest track free at its start (a track is free
        /// once its last note has ended). Returns each note's track, in the notes' order, and how many tracks it took.
        /// </summary>
        public static (int[] Tracks, int Needed) Allocate(IReadOnlyList<NoteEvent> notes)
        {
            var order = Enumerable.Range(0, notes.Count).OrderBy(i => notes[i].Time).ThenBy(i => notes[i].Pitch).ToList();
            var tracks = new int[notes.Count];
            var busyUntil = new List<int>();
            foreach (int i in order)
            {
                int t = busyUntil.FindIndex(end => end <= notes[i].Time);
                if (t < 0) { t = busyUntil.Count; busyUntil.Add(0); }
                busyUntil[t] = notes[i].End;
                tracks[i] = t;
            }
            return (tracks, busyUntil.Count);
        }

        /// <summary>
        /// The Note column events of each track: a note-on at each start, an off at each end unless
        /// the track's next note starts there. A note that runs to the pattern's end is let go on its
        /// last tick (an event at the end itself would never play). Velocity, when the machine has a
        /// velocity track parameter (<paramref name="velocity"/> is its raw range), goes beside each note-on.
        /// </summary>
        public static List<(int Track, int Time, int Note, int? Velocity)> Events(IReadOnlyList<NoteEvent> notes, int[] tracks, int patternTime, int timePerTick,
            (int Min, int Max)? velocity = null)
        {
            var events = new List<(int Track, int Time, int Note, int? Velocity)>();
            foreach (var group in Enumerable.Range(0, notes.Count).GroupBy(i => tracks[i]))
            {
                var list = group.OrderBy(i => notes[i].Time).ToList();
                for (int k = 0; k < list.Count; k++)
                {
                    var n = notes[list[k]];
                    int? vel = velocity is (int lo, int hi) ? lo + (int)Math.Round((hi - lo) * n.Velocity / 127.0) : null;
                    events.Add((group.Key, n.Time, ToBuzz(n.Pitch), vel));
                    int end = Math.Min(n.End, patternTime - timePerTick);
                    bool next = k + 1 < list.Count && notes[list[k + 1]].Time <= n.End;
                    if (!next && end > n.Time) events.Add((group.Key, end, Off, null));
                }
            }
            return events.OrderBy(e => e.Track).ThenBy(e => e.Time).ToList();
        }

        /// <summary>
        /// Notes from per-track Note events (time, Buzz note): each note-on lasts to the track's next
        /// event (or the pattern's end). Velocities by (track, time), when the machine has them.
        /// </summary>
        public static List<NoteEvent> Read(IEnumerable<(int Track, int Time, int Value)> noteEvents, int patternTime, int timePerTick,
            IReadOnlyDictionary<(int Track, int Time), int> velocities = null, (int Min, int Max)? velocity = null)
        {
            var list = new List<NoteEvent>();
            foreach (var track in noteEvents.GroupBy(e => e.Track))
            {
                var events = track.OrderBy(e => e.Time).ToList();
                for (int i = 0; i < events.Count; i++)
                {
                    int pitch = FromBuzz(events[i].Value);
                    if (pitch < 0) continue;
                    int end = i + 1 < events.Count ? events[i + 1].Time : patternTime;
                    // Let go on its last tick (Events): read as running to the end.
                    if (i + 1 < events.Count && events[i + 1].Value == Off && end == patternTime - timePerTick) end = patternTime;
                    int vel = NoteList.DefaultVelocity;
                    if (velocities != null && velocity is (int lo, int hi) && hi > lo && velocities.TryGetValue((track.Key, events[i].Time), out int raw))
                        vel = Math.Clamp((int)Math.Round((raw - lo) * 127.0 / (hi - lo)), 1, 127);
                    list.Add(new NoteEvent(events[i].Time, Math.Max(1, end - events[i].Time), pitch, vel));
                }
            }
            list.Sort(NoteList.Order);
            return list;
        }

        /// <summary>
        /// A written pitch as the machine gets it: plus the manifest's offset. Throws when that leaves
        /// the range it takes (0–127 for MIDI, 0–119 for Buzz notes).
        /// </summary>
        public static int Sent(int written, int offset, int highest, string machine)
        {
            int sent = written + offset;
            if (sent < 0 || sent > highest)
                throw new Mcp.ToolError($"{Pitch.Name(written)} is out of {machine}'s range" +
                                        (offset != 0 ? $" with its pitch offset ({offset:+0;-0} semitones, from its manifest, so the written pitch plays)" : "") +
                                        $": it takes notes {Pitch.Name(Math.Max(0, -offset))}–{Pitch.Name(Math.Min(127, highest - offset))}.");
            return sent;
        }
    }
}
