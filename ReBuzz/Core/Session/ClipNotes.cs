#nullable disable
// Session View clip notes (0.7.1, fork addition): the notes of a clip's MIDI column as an
// editor sees them, and the edits a piano roll makes. See docs/specs/session-view.md, 0.7.1.
//
// No dependency on the rest of ReBuzz, so ReBuzz.SessionTests links this file. Times are
// clip time, 1/240 tick (BuzzGUI's ClipEvent.TimeBase).

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Where a note's extras live in its MIDI column event (the event's value is <c>0x90 | channel | note &lt;&lt; 8 | velocity &lt;&lt; 16</c>; the bits above
    /// the velocity were always 0): bit 23 is the note's Slide flag, bits 24-31 the fine start (FU2b). The engine takes both off before a machine
    /// is sent the note (<see cref="NoteBits.Strip"/>), so a machine that knows neither sees the plain MIDI message it always did.
    /// A fine start of 0 is an ordinary note (on its time unit, played by the block that holds the unit). Any other value (1 to 255) marks a note on an
    /// exact sample: it starts <c>(value - 1) / 254</c> of a unit after its time, and the engine ends a block on that sample so the note's first sample is
    /// the block's first, even when the note sits right on a unit.
    /// </summary>
    internal static class NoteBits
    {
        /// <summary>Bit 23 of a note-on's value: the note slides into its pitch (Neo Poly glides to it at its Slide time).</summary>
        public const int SlideBit = 1 << 23;

        /// <summary>Bits 24-31: 0 for every note a song had before; 1 to 255: the note is on an exact sample, <c>(value - 1) / 254</c> of a clip time unit (1/240 tick) after its time.</summary>
        public const int FineShift = 24;

        /// <summary>The fine value's steps from the unit to the next one (value 1 is the unit itself, 255 the next).</summary>
        public const int FineSteps = 254;

        /// <summary>The part of a clip time unit after the note's time that a fine value stands for (0 to 1); 0 for none.</summary>
        public static double FractionOf(int fine) => fine <= 0 ? 0 : Math.Min(fine - 1, FineSteps) / (double)FineSteps;

        /// <summary>The value without the slide flag and the fine start: what a machine is sent.</summary>
        public static int Strip(int value) => value & 0x7FFFFF;

        public static bool IsSlide(int value) => (value & SlideBit) != 0;

        public static int FineOf(int value) => (int)((uint)value >> FineShift);

        public static int With(int value, bool slide, int fine) =>
            (value & 0x7FFFFF) | (slide ? SlideBit : 0) | (int)((uint)Math.Clamp(fine, 0, 255) << FineShift);

        /// <summary>A start in clip time as a (whole units, fine) pair: the unit at or before it and the fine value (1 to 255) of the rest; the note is on an exact sample.</summary>
        public static (int Time, int Fine) Split(double units)
        {
            double floor = Math.Floor(units);
            return ((int)floor, 1 + (int)Math.Round((units - floor) * FineSteps));
        }
    }

    /// <summary>
    /// A note of a clip: where it starts and how long it lasts (1/240 tick), its pitch, velocity and channel. Notes of a machine that plays a sample per
    /// note (Neo Sampler's Tracker mode, TM1) carry that too: <see cref="Sample"/> (the number of the sample it plays; 0: none, the machine plays
    /// it as it plays any note), <see cref="Offset"/> (where in the sample it starts, in the sample's frames) and <see cref="Tune"/> (cents).
    /// Only the tracker's store keeps them (the MIDI column has no room for them). <see cref="Slide"/> (FU2b) marks a note that slides into its pitch
    /// (Neo Poly's acid slide); <see cref="Fine"/> (0: none) marks a note on an exact sample (Slice to new MIDI track) and the part of a clip time unit it starts after
    /// <see cref="Time"/> (<see cref="NoteBits.FractionOf"/>), instead of the 1/240 tick grid. Both are in the MIDI column event's value (<see cref="NoteBits"/>).
    /// </summary>
    internal readonly record struct ClipNote(int Time, int Duration, int Note, int Velocity, int Channel = 0, int Sample = 0, int Offset = 0, int Tune = 0, bool Slide = false, int Fine = 0)
    {
        public int End => Time + Duration;

        /// <summary>The MIDI column event it is: its note-on as the value.</summary>
        public (int Time, int Value, int Duration) ToEvent() =>
            (Time, NoteBits.With(0x90 | (Channel & 0x0F) | ((Note & 0x7F) << 8) | ((Velocity & 0x7F) << 16), Slide, Fine), Duration);

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
            note = new ClipNote(e.Time, e.Duration, (e.Value >> 8) & 0x7F, velocity, status & 0x0F, Slide: NoteBits.IsSlide(e.Value), Fine: NoteBits.FineOf(e.Value));
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
                return n with { Time = time, Fine = 0, Duration = Math.Clamp(end - time, MinDuration, Math.Max(MinDuration, clipLength - time)) };   // a quantized note is on the grid: no fine start
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
