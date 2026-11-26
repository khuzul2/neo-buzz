#nullable disable
// Neo Chop (docs/specs/neo-chop.md): the musical grid the engine cuts on, and the small pieces it is built from. Clean room: note values,
// a hash for the seeded chances and a state-variable filter, all textbook; no code or table from any other machine.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Chop
{
    /// <summary>The four modes that cut the audio in time, in the order of their priority (highest last): a higher one takes over from a lower one.</summary>
    internal enum ChopMode { Retrigger = 0, Repeat = 1, Stutter = 2, Tape = 3 }

    /// <summary>What the engine logs (when asked to) for the timing tests: the sample a thing started on, and the song position it was meant for.</summary>
    internal enum ChopEventKind : byte { StutterStart, StutterRepeat, RepeatStart, RepeatRepeat, RetrigStart, RetrigStep, GateStep, TapeStop, TapeStart, PanCycle, NoteStart }

    internal struct ChopEvent
    {
        public ChopEventKind Kind;
        /// <summary>The sample, counted from the engine's first (stream time).</summary>
        public long Sample;
        /// <summary>The grid position the event belongs to, in beats of the song (for a note start: the position of the sample).</summary>
        public double Beat;
        public int Index;
    }

    internal static class ChopGrid
    {
        public const int BeatsPerBar = 4;

        /// <summary>Slice and grid note values (1/4, 1/8, 1/16, 1/32, 1/64) in beats; a quarter note is a beat.</summary>
        public static readonly double[] SliceBeats = { 1.0, 0.5, 0.25, 0.125, 0.0625 };

        /// <summary>Straight, dotted, triplet.</summary>
        public static double Feel(int feel) => feel == 1 ? 1.5 : feel == 2 ? 2.0 / 3.0 : 1.0;

        /// <summary>The periods of "every" (1 beat, 2 beats, 1 bar, 2, 4, 8 bars) in beats.</summary>
        public static readonly double[] EveryBeats = { 1, 2, 4, 8, 16, 32 };

        /// <summary>Window lengths (1/16 of a bar, 1/8, 1/4, 1/2, 1, 2, 4 bars) in beats.</summary>
        public static readonly double[] LengthBeats = { 0.25, 0.5, 1, 2, 4, 8, 16 };

        /// <summary>The note values of a synced time (Free, 1/16, 1/8, 1/4, 1/2, 1 bar) in beats; Free is 0.</summary>
        public static readonly double[] TimeBeats = { 0, 0.25, 0.5, 1, 2, 4 };

        /// <summary>One pan cycle (Free, 1/16, 1/8, 1/4, 1/2, 1 bar, 2, 4, 8 bars) in beats; Free is 0.</summary>
        public static readonly double[] PanBeats = { 0, 0.25, 0.5, 1, 2, 4, 8, 16, 32 };

        /// <summary>
        /// What a key picks when "Note picks slice" is on: the twelve keys of an octave, C up to B, are 1/4, dotted 1/8, 1/8, triplet 1/8,
        /// dotted 1/16, 1/16, triplet 1/16, dotted 1/32, 1/32, triplet 1/32, 1/64 and triplet 1/64, in beats.
        /// </summary>
        public static readonly double[] KeySlice =
        {
            1.0, 0.75, 0.5, 1.0 / 3.0, 0.375, 0.25, 1.0 / 6.0, 0.1875, 0.125, 1.0 / 12.0, 0.0625, 1.0 / 24.0,
        };

        /// <summary>Uniform in [0, 1), the same for the same (seed, index, salt) on every platform: the seeded chances and variations.</summary>
        public static double Random01(uint seed, long index, int salt)
        {
            unchecked
            {
                ulong z = (ulong)index * 0x9E3779B97F4A7C15UL + (ulong)(salt + 1) * 0xBF58476D1CE4E5B9UL + seed;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                return (z >> 11) * (1.0 / 9007199254740992.0);
            }
        }
    }

    /// <summary>A two-pole state-variable filter (the trapezoidal form) for the stutter's filter per repeat; one channel.</summary>
    internal struct ChopSvf
    {
        double ic1, ic2;

        public void Reset() { ic1 = ic2 = 0; }

        /// <summary>One sample: <paramref name="g"/> = tan(pi fc / sr), <paramref name="k"/> = 1/Q; returns low-pass or high-pass.</summary>
        public float Process(float x, double g, double k, bool highPass)
        {
            double a1 = 1.0 / (1.0 + g * (g + k));
            double a2 = g * a1;
            double a3 = g * a2;
            double v3 = x - ic2;
            double v1 = a1 * ic1 + a2 * v3;
            double v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2 * v1 - ic1;
            ic2 = 2 * v2 - ic2;
            // Keep the state out of the denormal range.
            if (Math.Abs(ic1) < 1e-20) ic1 = 0;
            if (Math.Abs(ic2) < 1e-20) ic2 = 0;
            return (float)(highPass ? x - k * v1 - v2 : v2);
        }
    }
}
