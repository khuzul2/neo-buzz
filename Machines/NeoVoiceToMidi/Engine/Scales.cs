#nullable disable
// Neo Voice to MIDI (docs/specs/voice-to-midi.md §2.2): scales as 12-bit masks (bit n = n semitones above the root, or above C for an
// absolute mask), the nearest allowed note, the key finder (Krumhansl-Schmuckler profiles on the notes sung so far) and a small key parser
// for the song's key text ("A minor", "F#m", "Bb major"). Pure; no allocation after the tables are built.

using System;

namespace NeoBuzz.VoiceToMidi
{
    internal static class Scales
    {
        /// <summary>The Scale parameter's choices, in order (the last is Custom: the mask parameter).</summary>
        public static readonly string[] Names =
        {
            "Major", "Minor", "Dorian", "Phrygian", "Lydian", "Mixolydian", "Locrian", "Harmonic minor", "Melodic minor",
            "Major pentatonic", "Minor pentatonic", "Blues", "Whole tone", "Custom",
        };
        public const int Custom = 13;
        public const int All = 0xFFF;
        public static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>Each scale's notes above its root, as a mask.</summary>
        public static readonly int[] Relative =
        {
            Mask(0, 2, 4, 5, 7, 9, 11), Mask(0, 2, 3, 5, 7, 8, 10), Mask(0, 2, 3, 5, 7, 9, 10), Mask(0, 1, 3, 5, 7, 8, 10), Mask(0, 2, 4, 6, 7, 9, 11),
            Mask(0, 2, 4, 5, 7, 9, 10), Mask(0, 1, 3, 5, 6, 8, 10), Mask(0, 2, 3, 5, 7, 8, 11), Mask(0, 2, 3, 5, 7, 9, 11),
            Mask(0, 2, 4, 7, 9), Mask(0, 3, 5, 7, 10), Mask(0, 3, 5, 6, 7, 10), Mask(0, 2, 4, 6, 8, 10), 0,
        };

        static int Mask(params int[] notes) { int m = 0; foreach (int n in notes) m |= 1 << n; return m; }

        public static int Rotate(int mask, int by)
        {
            by = ((by % 12) + 12) % 12;
            return ((mask << by) | (mask >> (12 - by))) & All;
        }

        /// <summary>The notes of a key as an absolute mask (bit 0 = C). A Custom scale takes <paramref name="custom"/> as it is (absolute).</summary>
        public static int KeyMask(int root, int scale, int custom)
        {
            if (scale < 0 || scale >= Relative.Length) return All;
            if (scale == Custom) return custom & All;
            return Rotate(Relative[scale], root);
        }

        public static int Count(int mask) { int n = 0; for (int i = 0; i < 12; i++) if ((mask >> i & 1) != 0) n++; return n; }

        /// <summary>The nearest allowed note (a MIDI number) to <paramref name="midi"/>; <paramref name="mask"/> is absolute. An empty mask allows everything.</summary>
        public static int Nearest(double midi, int mask)
        {
            if ((mask & All) == 0) mask = All;
            int centre = (int)Math.Round(midi);
            int best = centre;
            double bestD = double.MaxValue;
            for (int d = -7; d <= 7; d++)
            {
                int n = centre + d;
                if (n < 0 || n > 127) continue;
                if ((mask >> (((n % 12) + 12) % 12) & 1) == 0) continue;
                double dist = Math.Abs(midi - n);
                if (dist < bestD) { bestD = dist; best = n; }
            }
            return best;
        }

        public static string Name(int midi) => NoteNames[((midi % 12) + 12) % 12] + (midi / 12 - 1);

        /// <summary>"A minor", "F#m", "Bb major", "D dorian", "C": the root and the scale index (Scales.Names), or false.</summary>
        public static bool TryParseKey(string text, out int root, out int scale)
        {
            root = 0; scale = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            int pc;
            switch (char.ToUpperInvariant(s[0]))
            {
                case 'C': pc = 0; break; case 'D': pc = 2; break; case 'E': pc = 4; break; case 'F': pc = 5; break;
                case 'G': pc = 7; break; case 'A': pc = 9; break; case 'B': pc = 11; break;
                default: return false;
            }
            int i = 1;
            while (i < s.Length && (s[i] == '#' || s[i] == 'b' || s[i] == '♯' || s[i] == '♭'))
            {
                // "Bb" is B flat, but "b" alone after a space is not an accidental; "Abm" reads A flat minor.
                pc += (s[i] == '#' || s[i] == '♯') ? 1 : -1;
                i++;
            }
            root = ((pc % 12) + 12) % 12;
            string rest = s.Substring(i).Trim().ToLowerInvariant();
            if (rest.Length == 0 || rest == "major" || rest == "maj" || rest == "ionian") { scale = 0; return true; }
            if (rest == "m" || rest == "min" || rest == "minor" || rest == "aeolian") { scale = 1; return true; }
            for (int k = 0; k < Names.Length - 1; k++)
                if (rest == Names[k].ToLowerInvariant()) { scale = k; return true; }
            return false;
        }
    }

    /// <summary>The key of what was sung: the pitch classes' durations against the Krumhansl-Kessler major and minor profiles.</summary>
    internal sealed class KeyFinder
    {
        static readonly double[] Major = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        static readonly double[] Minor = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
        readonly double[] hist = new double[12];
        double total;
        int root = -1, scale;
        double margin;

        /// <summary>Seconds of singing needed before a key is suggested.</summary>
        public double MinSeconds = 2.0;
        public int Root => root;
        /// <summary>Scales.Names index: 0 major, 1 minor.</summary>
        public int Scale => scale;
        public bool Known => root >= 0;
        public double Seconds => total;

        public void Reset() { Array.Clear(hist, 0, 12); total = 0; root = -1; margin = 0; }

        public void Add(int pitchClass, double seconds)
        {
            hist[((pitchClass % 12) + 12) % 12] += seconds;
            total += seconds;
        }

        /// <summary>Re-reads the key from the histogram (cheap; call about once a second).</summary>
        public void Update()
        {
            if (total < MinSeconds) return;
            double best = double.MinValue, second = double.MinValue;
            int bRoot = 0, bScale = 0;
            for (int r = 0; r < 12; r++)
                for (int m = 0; m < 2; m++)
                {
                    double c = Correlate(r, m == 0 ? Major : Minor);
                    if (c > best) { second = best; best = c; bRoot = r; bScale = m; }
                    else if (c > second) second = c;
                }
            // A new key replaces the one in use only when it is clearly better (the relative minor scores close to its major).
            if (root >= 0 && (bRoot != root || bScale != scale))
            {
                double cur = Correlate(root, scale == 0 ? Major : Minor);
                if (best - cur < 0.04) return;
            }
            root = bRoot; scale = bScale; margin = best - second;
        }

        double Correlate(int r, double[] profile)
        {
            double mx = 0, my = 0;
            for (int i = 0; i < 12; i++) { mx += hist[(i + r) % 12]; my += profile[i]; }
            mx /= 12; my /= 12;
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < 12; i++)
            {
                double x = hist[(i + r) % 12] - mx, y = profile[i] - my;
                sxy += x * y; sxx += x * x; syy += y * y;
            }
            return sxx <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
        }

        public string Text() => root < 0 ? "" : Scales.NoteNames[root] + (scale == 0 ? " major" : " minor");
    }
}
