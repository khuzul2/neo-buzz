#nullable disable
// Neo Voice to MIDI (docs/specs/voice-to-midi.md §5): diatonic chords for the sung note and suggestions that fit the melody.
//  - A key is the notes of its scale in order from the root. The chord on scale degree d stacks thirds in the scale: the notes d, d+2, d+4
//    (and d+6 for a seventh) of that order. Without a key (Lock off) or with a note outside it the chord is major on the note.
//  - Root: the sung note is the chord's root. Melody: among the chords of the key that contain the sung note, the one that is
//    most at home in the key (I, V, IV, vi, ii ...), a little more if it is the chord already playing.
//  - Voicing: the root in the octave of the Chord octave setting and the other notes stacked above it; First and Second inversion lift
//    the lowest notes an octave; Auto picks the inversion nearest the previous chord (smooth voice leading); Open drops the second
//    note from the top an octave; Wide doubles the root an octave down.
//  - Suggestions: the chords of the key ranked by how much of the recently sung notes (a decaying histogram of the last seconds) they
//    contain, root and fifth counting more than the third; the best three.
// Allocation-free (the names are made by ChordName, off the audio thread).

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.VoiceToMidi
{
    internal sealed class ChordBuilder
    {
        public const int MaxNotes = 6;
        // Quality codes: 0 major, 1 minor, 2 diminished, 3 augmented, 4 other; with a seventh +10: 10 dominant 7, 11 minor 7, 12 half-diminished,
        // 13 major 7, 14 minor-major 7, 15 diminished 7, 19 other.
        readonly int[] order = new int[12];
        int orderCount;
        double prevCentre = 55;

        /// <summary>The root of the chord built last (0 to 11) and its quality code.</summary>
        public int RootPc { get; private set; } = -1;
        public int Quality { get; private set; }
        int lastDegree = -1;
        int lastRoot = -1;

        public void Reset() { prevCentre = 55; RootPc = -1; lastDegree = -1; lastRoot = -1; }

        /// <summary>
        /// The chord for the sung note <paramref name="note"/> (a MIDI number). <paramref name="keyRoot"/> and <paramref name="keyMask"/> (absolute) say the key;
        /// a mask of every note means no key. Returns the number of notes written to <paramref name="notes"/> (at most <see cref="MaxNotes"/>).
        /// </summary>
        public int Build(int note, int keyRoot, int keyMask, bool melody, bool sevenths, int inversion, int spread, int octave, int[] notes)
        {
            int pc = ((note % 12) + 12) % 12;
            bool keyed = Scales.Count(keyMask) is >= 5 and < 12;
            orderCount = 0;
            if (keyed)
                for (int i = 0; i < 12; i++)
                {
                    int p = (keyRoot + i) % 12;
                    if ((keyMask >> p & 1) != 0) order[orderCount++] = p;
                }
            int rootPc = pc, degree = -1;
            if (keyed)
            {
                if (melody)
                {
                    double best = double.MinValue;
                    for (int d = 0; d < orderCount; d++)
                    {
                        int t3 = order[(d + 2) % orderCount], t5 = order[(d + 4) % orderCount], t7 = order[(d + 6) % orderCount];
                        int r = order[d];
                        bool has = pc == r || pc == t3 || pc == t5 || (sevenths && pc == t7);
                        if (!has) continue;
                        double w = DegreeWeight(d, orderCount) + (pc == r ? 0.15 : pc == t5 ? 0.05 : 0) + (r == lastRoot ? 0.25 : 0);
                        if (w > best) { best = w; degree = d; }
                    }
                    if (degree >= 0) rootPc = order[degree];
                }
                else
                {
                    for (int d = 0; d < orderCount; d++) if (order[d] == pc) { degree = d; break; }
                }
            }
            int third, fifth, seventh = -1;
            if (degree >= 0)
            {
                third = order[(degree + 2) % orderCount]; fifth = order[(degree + 4) % orderCount];
                if (sevenths) seventh = order[(degree + 6) % orderCount];
            }
            else
            {
                third = (rootPc + 4) % 12; fifth = (rootPc + 7) % 12;
                if (sevenths) seventh = (rootPc + 11) % 12;
            }
            int i3 = (third - rootPc + 12) % 12, i5 = (fifth - rootPc + 12) % 12, i7 = seventh < 0 ? -1 : (seventh - rootPc + 12) % 12;
            int q = i3 == 4 && i5 == 7 ? 0 : i3 == 3 && i5 == 7 ? 1 : i3 == 3 && i5 == 6 ? 2 : i3 == 4 && i5 == 8 ? 3 : 4;
            if (i7 >= 0) q = q == 0 ? (i7 == 10 ? 10 : i7 == 11 ? 13 : 19) : q == 1 ? (i7 == 10 ? 11 : i7 == 11 ? 14 : 19) : q == 2 ? (i7 == 10 ? 12 : i7 == 9 ? 15 : 19) : 19;
            RootPc = rootPc; Quality = q; lastDegree = degree; lastRoot = rootPc;

            int baseNote = 48 + 12 * octave;   // octave 0 puts the roots in C3 ... B3 (48 ... 59)
            int count = 0;
            int n0 = baseNote + ((rootPc - baseNote % 12) + 12) % 12;
            int[] tones = notes;   // MaxNotes long
            tones[count++] = n0;
            int prev = n0;
            Stack(third, ref prev, tones, ref count);
            Stack(fifth, ref prev, tones, ref count);
            if (seventh >= 0) Stack(seventh, ref prev, tones, ref count);
            int stack = count;
            // Inversion.
            int inv = inversion;
            if (inversion >= 3)
            {
                double bestD = double.MaxValue; inv = 0;
                for (int k = 0; k < Math.Min(stack, 3); k++)
                {
                    double centre = 0;
                    for (int i = 0; i < stack; i++) centre += i < k ? tones[i] + 12 : tones[i];
                    centre /= stack;
                    double d = Math.Abs(centre - prevCentre);
                    if (d < bestD - 1e-9) { bestD = d; inv = k; }
                }
            }
            for (int k = 0; k < Math.Min(inv, stack - 1); k++) tones[k] += 12;
            Array.Sort(tones, 0, stack);
            if (spread == 1 && stack >= 3) { tones[stack - 2] -= 12; Array.Sort(tones, 0, stack); }
            else if (spread == 2 && stack < MaxNotes) tones[count++] = tones[0] - 12;
            // Keep it in range (an octave at a time).
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                while (tones[i] > 127) tones[i] -= 12;
                while (tones[i] < 0) tones[i] += 12;
                if (i < stack) sum += tones[i];
            }
            prevCentre = sum / stack;
            Array.Sort(tones, 0, count);
            return count;
        }

        static void Stack(int pc, ref int prev, int[] tones, ref int count)
        {
            int n = prev + 1;
            while (((n % 12) + 12) % 12 != pc) n++;
            tones[count++] = n;
            prev = n;
        }

        static double DegreeWeight(int d, int n)
        {
            if (n == 7)
                switch (d) { case 0: return 1.0; case 1: return 0.6; case 2: return 0.45; case 3: return 0.85; case 4: return 0.9; case 5: return 0.7; default: return 0.2; }
            return d == 0 ? 1.0 : d == n / 2 || d == n - 2 ? 0.8 : 0.5;
        }

        /// <summary>"Am", "G7", "Bdim", "Fmaj7", "C" ... for a root and a quality code.</summary>
        public static string ChordName(int rootPc, int quality)
        {
            if (rootPc < 0) return "";
            string r = Scales.NoteNames[rootPc];
            switch (quality)
            {
                case 0: return r;
                case 1: return r + "m";
                case 2: return r + "dim";
                case 3: return r + "aug";
                case 10: return r + "7";
                case 11: return r + "m7";
                case 12: return r + "m7b5";
                case 13: return r + "maj7";
                case 14: return r + "mMaj7";
                case 15: return r + "dim7";
                default: return r + "?";
            }
        }
    }

    /// <summary>A decaying histogram of the notes sung lately and the chords of the key that fit it best.</summary>
    internal sealed class ChordSuggester
    {
        readonly double[] hist = new double[12];
        readonly int[] order = new int[12];
        public const int Top = 3;
        public readonly int[] RootPc = new int[Top];
        public readonly int[] Quality = new int[Top];
        public int Count { get; private set; }
        /// <summary>Seconds over which the histogram forgets (the weight halves in this time).</summary>
        public double HalfLife = 6.0;

        public void Reset() { Array.Clear(hist, 0, 12); Count = 0; }

        public void Add(int pitchClass, double seconds) => hist[((pitchClass % 12) + 12) % 12] += seconds;

        public void Decay(double seconds)
        {
            double f = PortableMath.Pow(0.5, seconds / HalfLife);
            for (int i = 0; i < 12; i++) hist[i] *= f;
        }

        public double Total { get { double t = 0; for (int i = 0; i < 12; i++) t += hist[i]; return t; } }

        /// <summary>Ranks the chords of the key (root and mask as for ChordBuilder). A key of fewer than five notes, or none, has no suggestions.</summary>
        public void Update(int keyRoot, int keyMask, bool sevenths)
        {
            Count = 0;
            int n = Scales.Count(keyMask);
            double total = Total;
            if (n < 5 || n >= 12 || total < 0.5) return;
            int oc = 0;
            for (int i = 0; i < 12; i++) { int p = (keyRoot + i) % 12; if ((keyMask >> p & 1) != 0) order[oc++] = p; }
            Span<double> score = stackalloc double[12];
            Span<int> quality = stackalloc int[12];
            for (int d = 0; d < oc; d++)
            {
                int r = order[d], t3 = order[(d + 2) % oc], t5 = order[(d + 4) % oc], t7 = order[(d + 6) % oc];
                double inside = hist[r] * 1.0 + hist[t5] * 0.8 + hist[t3] * 0.7 + (sevenths ? hist[t7] * 0.5 : 0);
                double outside = 0;
                for (int i = 0; i < 12; i++) if (i != r && i != t3 && i != t5 && !(sevenths && i == t7)) outside += hist[i] * 0.4;
                score[d] = (inside - outside) / total + 0.1 * (d == 0 ? 1 : d == 3 || d == 4 ? 0.7 : 0);
                int i3 = (t3 - r + 12) % 12, i5 = (t5 - r + 12) % 12;
                int q = i3 == 4 && i5 == 7 ? 0 : i3 == 3 && i5 == 7 ? 1 : i3 == 3 && i5 == 6 ? 2 : i3 == 4 && i5 == 8 ? 3 : 4;
                if (sevenths)
                {
                    int i7 = (t7 - r + 12) % 12;
                    q = q == 0 ? (i7 == 10 ? 10 : 13) : q == 1 ? (i7 == 10 ? 11 : 14) : q == 2 ? (i7 == 10 ? 12 : 15) : 19;
                }
                quality[d] = q;
            }
            for (int k = 0; k < Top; k++)
            {
                int best = -1; double bs = double.MinValue;
                for (int d = 0; d < oc; d++) if (score[d] > bs) { bs = score[d]; best = d; }
                if (best < 0 || bs <= 0.05) break;
                RootPc[Count] = order[best]; Quality[Count] = quality[best]; Count++;
                score[best] = double.MinValue;
            }
        }
    }
}
