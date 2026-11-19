#nullable disable
// Neo Tune (docs/specs/neo-tune.md §3): the scales, as 12-bit masks of pitch classes (C = bit 0), and the key listener.
// Pure and allocation-free after construction; no libm.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Tune
{
    internal static class TuneScales
    {
        /// <summary>Scale 0 ... 11 as the Params.def lists them; 12 is Custom.</summary>
        public const int Custom = 12;

        public static readonly string[] Names =
        {
            "Chromatic", "Major", "Minor", "Dorian", "Phrygian", "Lydian", "Mixolydian", "Locrian",
            "Harmonic minor", "Minor pentatonic", "Major pentatonic", "Blues", "Custom",
        };

        /// <summary>The steps above the root, one array per scale 0 ... 11.</summary>
        public static readonly int[][] Steps =
        {
            new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },
            new[] { 0, 2, 4, 5, 7, 9, 11 },
            new[] { 0, 2, 3, 5, 7, 8, 10 },
            new[] { 0, 2, 3, 5, 7, 9, 10 },
            new[] { 0, 1, 3, 5, 7, 8, 10 },
            new[] { 0, 2, 4, 6, 7, 9, 11 },
            new[] { 0, 2, 4, 5, 7, 9, 10 },
            new[] { 0, 1, 3, 5, 6, 8, 10 },
            new[] { 0, 2, 3, 5, 7, 8, 11 },
            new[] { 0, 3, 5, 7, 10 },
            new[] { 0, 2, 4, 7, 9 },
            new[] { 0, 3, 5, 6, 7, 10 },
        };

        public static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>The scale's notes as a mask of pitch classes (bit n: note n, C = 0); Custom returns <paramref name="custom"/>.</summary>
        public static int Mask(int scale, int root, int custom)
        {
            if (scale < 0 || scale >= Steps.Length) return custom & 0xFFF;
            int m = 0;
            var s = Steps[scale];
            for (int i = 0; i < s.Length; i++) m |= 1 << ((root + s[i]) % 12);
            return m;
        }

        public static int Count(int mask)
        {
            int n = 0;
            for (int i = 0; i < 12; i++) n += (mask >> i) & 1;
            return n;
        }

        /// <summary>The note of the mask nearest to a fractional MIDI pitch, or −1 for an empty mask.</summary>
        public static int Nearest(double midi, int mask)
        {
            if (mask == 0) return -1;
            int best = -1;
            double bestD = 1e9;
            int centre = (int)Math.Round(midi);
            for (int n = centre - 7; n <= centre + 7; n++)
            {
                if (n < 0 || n > 127 || ((mask >> (n % 12)) & 1) == 0) continue;
                double d = Math.Abs(midi - n);
                if (d < bestD) { bestD = d; best = n; }
            }
            if (best >= 0) return best;
            // A single allowed note can lie further than 7 semitones away.
            for (int n = 0; n < 128; n++)
            {
                if (((mask >> (n % 12)) & 1) == 0) continue;
                double d = Math.Abs(midi - n);
                if (d < bestD) { bestD = d; best = n; }
            }
            return best;
        }
    }

    /// <summary>
    /// Listens for the key: a pitch-class histogram of the voiced pitch (weighted by confidence and time, forgetting over about
    /// 30 s), and the root that puts most of it on the scale's notes. The root changes only when another beats the current one by
    /// 10 %, so a passing note does not move it.
    /// </summary>
    internal sealed class KeyListener
    {
        readonly double[] hist = new double[12];
        int root = -1;
        double sinceUpdate;
        double total;

        public int Root => root;

        public void Reset()
        {
            Array.Clear(hist, 0, 12);
            root = -1;
            sinceUpdate = 0;
            total = 0;
        }

        public void Add(double midi, double confidence, double seconds)
        {
            double decay = PortableMath.Exp(-seconds / 30.0);
            for (int i = 0; i < 12; i++) hist[i] *= decay;
            int pc = ((int)Math.Round(midi) % 12 + 12) % 12;
            hist[pc] += confidence * seconds;
            total = total * decay + confidence * seconds;
            sinceUpdate += seconds;
        }

        /// <summary>The root for a scale (masks from <see cref="TuneScales.Steps"/>); -1 until about a second of voice has been heard.</summary>
        public int Estimate(int scale)
        {
            if (scale < 0 || scale >= TuneScales.Steps.Length || total < 1.0) return root;
            if (sinceUpdate < 0.2 && root >= 0) return root;
            sinceUpdate = 0;
            int best = 0;
            double bestScore = double.MinValue, current = double.MinValue;
            for (int r = 0; r < 12; r++)
            {
                int mask = TuneScales.Mask(scale, r, 0);
                double s = 0;
                for (int pc = 0; pc < 12; pc++) s += ((mask >> pc) & 1) != 0 ? hist[pc] : -0.6 * hist[pc];
                if (r == root) current = s;
                if (s > bestScore) { bestScore = s; best = r; }
            }
            if (root < 0 || bestScore > current + 0.1 * Math.Abs(current) + 1e-9) root = best;
            return root;
        }
    }
}
