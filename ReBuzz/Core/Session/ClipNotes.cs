#nullable disable
// Session View clip notes (0.7.1, fork addition): the notes of a clip's MIDI column as an
// editor sees them, and the edits a piano roll makes. See docs/specs/session-view.md, 0.7.1.
//
// No dependency on the rest of ReBuzz, so ReBuzz.SessionTests links this file. Times are
// pattern time, 1/240 tick (BuzzGUI's PatternEvent.TimeBase).

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>A note of a clip: where it starts and how long it lasts (1/240 tick), its pitch, velocity and channel.</summary>
    internal readonly record struct ClipNote(int Time, int Duration, int Note, int Velocity, int Channel = 0)
    {
        public int End => Time + Duration;

        /// <summary>The MIDI column event it is: its note-on as the value.</summary>
        public (int Time, int Value, int Duration) ToEvent() =>
            (Time, 0x90 | (Channel & 0x0F) | ((Note & 0x7F) << 8) | ((Velocity & 0x7F) << 16), Duration);

        /// <summary>The note a MIDI column event is, if it is a note-on.</summary>
        public static bool TryFrom((int Time, int Value, int Duration) e, out ClipNote note)
        {
            int status = e.Value & 0xFF;
            int velocity = (e.Value >> 16) & 0x7F;
            if ((status & 0xF0) != 0x90 || velocity == 0)
            {
                note = default;
                return false;
            }
            // The event's own duration, so the note turns back into the very event (editors remove it by value).
            note = new ClipNote(e.Time, e.Duration, (e.Value >> 8) & 0x7F, velocity, status & 0x0F);
            return true;
        }
    }

    /// <summary>
    /// The edits a piano roll makes, pure: each takes notes and returns the notes as they become,
    /// kept inside the clip (0 to its length) and the MIDI range.
    /// </summary>
    internal static class ClipNotes
    {
        public const int TimeBase = 240;
        public const int MinDuration = 1;

        /// <summary>The notes among MIDI column events, in time order.</summary>
        public static List<ClipNote> From(IEnumerable<(int Time, int Value, int Duration)> events)
        {
            var notes = new List<ClipNote>();
            foreach (var e in events)
                if (ClipNote.TryFrom(e, out var n)) notes.Add(n);
            notes.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Note.CompareTo(b.Note));
            return notes;
        }

        /// <summary><paramref name="time"/> to the nearest grid line (a grid of 1 or less: unchanged).</summary>
        public static int Snap(int time, int grid) =>
            grid <= 1 ? time : (int)(Math.Round(time / (double)grid, MidpointRounding.AwayFromZero) * grid);

        /// <summary><paramref name="time"/> to the grid line at or before it.</summary>
        public static int SnapDown(int time, int grid) =>
            grid <= 1 ? time : (int)Math.Floor(time / (double)grid) * grid;

        /// <summary>A note as drawn: at <paramref name="time"/> (snapped down to the grid), one grid step long (or <paramref name="duration"/>).</summary>
        public static ClipNote Draw(int time, int note, int velocity, int grid, int clipLength, int duration = 0)
        {
            int start = Math.Clamp(SnapDown(time, grid), 0, Math.Max(0, clipLength - 1));
            int length = duration > 0 ? duration : Math.Max(MinDuration, grid > 1 ? grid : TimeBase);
            return new ClipNote(start, Math.Clamp(length, MinDuration, clipLength - start), Math.Clamp(note, 0, 127), Math.Clamp(velocity, 1, 127));
        }

        /// <summary>
        /// Notes moved by <paramref name="deltaTime"/> and <paramref name="deltaNote"/>, as one group:
        /// with <paramref name="grid"/>, the first one lands on a grid line and the rest keep their
        /// distance to it; the group stays in the clip and in the MIDI range.
        /// </summary>
        public static List<ClipNote> Move(IReadOnlyList<ClipNote> notes, int deltaTime, int deltaNote, int grid, int clipLength)
        {
            if (notes.Count == 0) return new List<ClipNote>();
            int first = notes.Min(n => n.Time);
            int last = notes.Max(n => n.Time);
            int low = notes.Min(n => n.Note);
            int high = notes.Max(n => n.Note);
            int target = grid > 1 ? Snap(first + deltaTime, grid) : first + deltaTime;
            int dt = Math.Clamp(target - first, -first, Math.Max(0, clipLength - 1 - last));
            int dn = Math.Clamp(deltaNote, -low, 127 - high);
            return notes.Select(n => n with
            {
                Time = n.Time + dt,
                Note = n.Note + dn,
                Duration = Math.Clamp(n.Duration, MinDuration, Math.Max(MinDuration, clipLength - (n.Time + dt)))
            }).ToList();
        }

        /// <summary>Notes made longer or shorter by <paramref name="deltaDuration"/> (with a grid, their ends land on grid lines), never shorter than one step nor past the clip.</summary>
        public static List<ClipNote> Resize(IReadOnlyList<ClipNote> notes, int deltaDuration, int grid, int clipLength)
        {
            return notes.Select(n =>
            {
                int end = n.End + deltaDuration;
                if (grid > 1) end = Math.Max(Snap(end, grid), n.Time + Math.Min(grid, Math.Max(MinDuration, n.Duration)));
                return n with { Duration = Math.Clamp(end - n.Time, MinDuration, Math.Max(MinDuration, clipLength - n.Time)) };
            }).ToList();
        }

        /// <summary>Notes with their velocity changed by <paramref name="delta"/> (1-127).</summary>
        public static List<ClipNote> Velocity(IReadOnlyList<ClipNote> notes, int delta) =>
            notes.Select(n => n with { Velocity = Math.Clamp(n.Velocity + delta, 1, 127) }).ToList();

        /// <summary>
        /// Notes quantized (Live's): their starts move <paramref name="strength"/> (0-1) of the
        /// way to the nearest grid line; with <paramref name="ends"/>, their ends too. A note
        /// keeps its length otherwise, and stays in the clip.
        /// </summary>
        public static List<ClipNote> Quantize(IReadOnlyList<ClipNote> notes, int grid, double strength, bool ends, int clipLength)
        {
            strength = Math.Clamp(strength, 0, 1);
            return notes.Select(n =>
            {
                int time = n.Time + (int)Math.Round((Snap(n.Time, grid) - n.Time) * strength);
                time = Math.Clamp(time, 0, Math.Max(0, clipLength - 1));
                int end = time + n.Duration;
                if (ends)
                {
                    int snapped = Snap(n.End, grid);
                    end = n.End + (int)Math.Round((snapped - n.End) * strength);
                    if (end <= time) end = time + Math.Max(MinDuration, grid);     // a short note keeps a step
                }
                return n with { Time = time, Duration = Math.Clamp(end - time, MinDuration, Math.Max(MinDuration, clipLength - time)) };
            }).ToList();
        }

        /// <summary>The note under a point (time, pitch), the latest-starting first; -1 if none.</summary>
        public static int HitTest(IReadOnlyList<ClipNote> notes, int time, int note)
        {
            int found = -1;
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                if (n.Note == note && time >= n.Time && time < n.Time + Math.Max(MinDuration, n.Duration) && (found < 0 || n.Time >= notes[found].Time))
                    found = i;
            }
            return found;
        }

        /// <summary>Notes inside a box (times and pitches inclusive of the box's edges).</summary>
        public static List<int> InBox(IReadOnlyList<ClipNote> notes, int fromTime, int toTime, int lowNote, int highNote)
        {
            if (toTime < fromTime) (fromTime, toTime) = (toTime, fromTime);
            if (highNote < lowNote) (lowNote, highNote) = (highNote, lowNote);
            var found = new List<int>();
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                if (n.Note >= lowNote && n.Note <= highNote && n.Time <= toTime && n.End >= fromTime)
                    found.Add(i);
            }
            return found;
        }

        /// <summary>A note's name as Preferences name notes (BuzzGUI.Common.NoteNames; by default C3 is middle C, 60).</summary>
        public static string Name(int note) => BuzzGUI.Common.NoteNames.Name(note);
    }
}
