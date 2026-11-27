#nullable disable
// Neo Filter's vowel mode (EXT1, docs/specs/machines.md §5): a formant filter. The sound's spectrum is shaped by three resonances at
// the frequencies of a spoken vowel, as a vocal tract shapes the buzz of the vocal folds.
//
// Clean room: only published numbers and textbook filters.
//  - Formant frequencies: the averages of Peterson and Barney, "Control methods used in a study of the vowels", J. Acoust. Soc. Am.
//    24 (1952), 175-184, Table II (first three formants, F1-F3, of ten American English vowels spoken by 33 men, 28 women and 15
//    children; this mode uses the five vowels /ɑ ɛ i ɔ u/ as A, E, I, O, U, in the order the Vowel control morphs through them).
//  - Bandwidths: the typical values of a vocal tract, F1 60 Hz, F2 90 Hz, F3 150 Hz (Klatt, "Software for a cascade/parallel formant
//    synthesizer", J. Acoust. Soc. Am. 67 (1980), 971-995, Table I defaults); the same for every vowel and voice.
//  - Levels: the vocal tract is a cascade of resonances (Fant, Acoustic Theory of Speech Production, 1960), so a formant's height over
//    the others follows from the other resonances' skirts: the peak of formant i is Q_i times the product of the other resonators'
//    magnitudes at its frequency (second-order, unity at DC), lifted by 6 dB per octave above F1 (up to 3 times, +9.5 dB): speech spectra carry the voice
//    source's own fall, which a filter on an arbitrary signal does not have, and without the lift F2 and F3 are shoulders on F1's
//    skirt instead of peaks (our choice, measured by the formant test). The parallel form (Klatt 1980) sums band-passes with those peaks and
//    alternating signs, so adjacent formants add between their peaks instead of cancelling.
//  - Loudness: the whole is scaled so that a signal whose power density falls as 1/f^1.5 (between pink noise and a sawtooth, 100 Hz -
//    8 kHz) keeps its power through the filter, each band-pass counted as pi/(2Q) natural-log widths of its peak squared at its
//    frequency. A trim found by measurement closes the gap to pink noise and to a 110 Hz sawtooth.
// The three band-passes are the filter's SVF section (TPT, double precision), at four times the sample rate between the engine's
// halfbands, behind the drive. Frequencies move in the log domain, smoothed.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Filter
{
    internal static class VowelDesign
    {
        public const int Formants = 3, Vowels = 5, Voices = 3;

        public static readonly string[] VowelNames = { "A", "E", "I", "O", "U" };
        public static readonly string[] VoiceNames = { "Man", "Woman", "Child" };

        /// <summary>The cutoff (Hz) at which the table's frequencies stand as they are.</summary>
        public const double ReferenceHz = 2000;

        /// <summary>log2 of <see cref="ReferenceHz"/>.</summary>
        public const double ReferenceLog2 = 10.965784284662087;

        /// <summary>F1-F3 in Hz of /ɑ ɛ i ɔ u/ (A E I O U) for men, women and children: Peterson and Barney (1952), Table II averages.</summary>
        public static readonly double[,,] Table =
        {
            // Men
            {
                { 730, 1090, 2440 },   // A  /ɑ/
                { 530, 1840, 2480 },   // E  /ɛ/
                { 270, 2290, 3010 },   // I  /i/
                { 570, 840, 2410 },    // O  /ɔ/
                { 300, 870, 2240 },    // U  /u/
            },
            // Women
            {
                { 850, 1220, 2810 },
                { 610, 2330, 2990 },
                { 310, 2790, 3310 },
                { 590, 920, 2710 },
                { 370, 950, 2670 },
            },
            // Children
            {
                { 1030, 1370, 3170 },
                { 690, 2610, 3570 },
                { 370, 3200, 3730 },
                { 680, 1060, 3180 },
                { 430, 1150, 3140 },
            },
        };

        /// <summary>Bandwidths of F1-F3 in Hz (Klatt 1980).</summary>
        public static readonly double[] Bandwidth = { 60, 90, 150 };

        /// <summary>log2 of the table, so a morph is a straight line in the log domain.</summary>
        static readonly double[,,] LogTable = MakeLog();

        static double[,,] MakeLog()
        {
            var t = new double[Voices, Vowels, Formants];
            for (int v = 0; v < Voices; v++)
                for (int w = 0; w < Vowels; w++)
                    for (int f = 0; f < Formants; f++) t[v, w, f] = Math.Log2(Table[v, w, f]);
            return t;
        }

        /// <summary>The narrowest bandwidth scale (Resonance far up) and the widest (Resonance at its bottom): the table's bandwidths times this.</summary>
        public const double MinBandwidthScale = 0.08, MaxBandwidthScale = 1.5;

        /// <summary>The Q at which the table's bandwidths stand as they are (Resonance's flat 0.71).</summary>
        public const double ReferenceQ = 0.71;

        /// <summary>The power per natural-log frequency of a signal whose spectral density falls as f^-1.5 (between pink noise's f^-1 and a sawtooth's f^-2), integrated over 100 Hz - 8 kHz: 2 (100^-0.5 - 8000^-0.5).</summary>
        const double BandPower = 0.17764;

        /// <summary>The loudness trim found by measurement (the closed-form power is a little high: it ignores the formants' overlap).</summary>
        const double Trim = 1.462;   // +3.3 dB

        /// <summary>The smallest damping (1/Q) a formant runs with: Q 250.</summary>
        public const double MinDamping = 0.004;

        /// <summary>The bandwidth scale a Resonance (Q) asks for: higher Q, sharper formants.</summary>
        public static double BandwidthScale(double q) => Math.Clamp(ReferenceQ / q, MinBandwidthScale, MaxBandwidthScale);

        /// <summary>The cutoff's shift of the formants in octaves (2 kHz, the default Cutoff, is none), within ±2 octaves.</summary>
        public static double CutoffShift(double cutoffHz) => Math.Clamp(Math.Log2(cutoffHz / ReferenceHz), -2.0, 2.0);

        /// <summary>The vowel (0 … 1: A, E, I, O, U a quarter apart) and the voice (0 … 2, between two sets a blend) at which the formants stand, in Hz (before any shift), F1-F3.</summary>
        public static void Frequencies(double pos, double voice, double shiftOctaves, double[] f)
        {
            double x = Math.Clamp(pos, 0, 1) * (Vowels - 1);
            int i0 = Math.Min((int)x, Vowels - 2);
            double fx = x - i0;
            double vv = Math.Clamp(voice, 0, Voices - 1);
            int v0 = Math.Min((int)vv, Voices - 2);
            double fv = vv - v0;
            for (int k = 0; k < Formants; k++)
            {
                double a = LogTable[v0, i0, k] + (LogTable[v0, i0 + 1, k] - LogTable[v0, i0, k]) * fx;
                double b = LogTable[v0 + 1, i0, k] + (LogTable[v0 + 1, i0 + 1, k] - LogTable[v0 + 1, i0, k]) * fx;
                f[k] = PortableMath.Exp2(a + (b - a) * fv + shiftOctaves);
            }
        }

        /// <summary>
        /// The formants' signed peak gains for frequencies <paramref name="f"/> (already limited to what the engine reaches) and dampings
        /// <paramref name="k"/> (1/Q): Fant's cascade heights, alternating signs, scaled to keep pink noise's power.
        /// </summary>
        public static void Gains(double[] f, double[] k, double[] a)
        {
            double power = 0;
            for (int i = 0; i < Formants; i++)
            {
                double g = 1 / k[i];
                for (int j = 0; j < Formants; j++)
                {
                    if (j == i) continue;
                    double r = f[i] / f[j];
                    double d = 1 - r * r;
                    g /= Math.Sqrt(d * d + r * r * k[j] * k[j]);
                }
                g *= Math.Min(f[i] / f[0], 3.0);      // +6 dB/oct above F1, up to ×3 (+9.5 dB): the formants stand out of a signal that has no voice-source tilt
                a[i] = g;
                power += g * g * (Math.PI / 2) * k[i] / Math.Sqrt(f[i]);
            }
            double norm = Trim / Math.Sqrt(Math.Max(1e-12, power / BandPower));
            norm = Math.Min(norm, 8.0);
            a[0] *= norm;
            a[1] *= -norm;
            a[2] *= norm;
        }

        /// <summary>The full design: formant frequencies, dampings and signed gains for a vowel, voice, shift and bandwidth scale, at a sample rate.</summary>
        public static void Design(double pos, double voice, double shiftOctaves, double bwScale, double sampleRate, double[] f, double[] k, double[] a)
        {
            Frequencies(pos, voice, shiftOctaves, f);
            for (int i = 0; i < Formants; i++)
            {
                f[i] = FilterDesign.Reach(f[i], sampleRate);
                k[i] = Math.Max(MinDamping, Bandwidth[i] * bwScale / f[i]);
            }
            Gains(f, k, a);
        }

        /// <summary>The magnitude (linear) of the formants' sum at <paramref name="hz"/> (the analog prototypes at the engine's rate, as <see cref="FilterDesign.Core"/> has them).</summary>
        public static double Magnitude(double[] f, double[] k, double[] a, double hz, double sampleRate)
        {
            var sum = System.Numerics.Complex.Zero;
            for (int i = 0; i < Formants; i++)
            {
                double g = FilterDesign.Prewarp(f[i], sampleRate);
                double fq = Math.Clamp(hz, 0, FilterDesign.Oversampling * sampleRate * 0.5 * 0.999999);
                double w = PortableMath.Tan(Math.PI * fq / (FilterDesign.Oversampling * sampleRate)) / g;
                var s = new System.Numerics.Complex(0, w);
                sum += a[i] * k[i] * s / (s * s + k[i] * s + 1);
            }
            return sum.Magnitude;
        }

        /// <summary>The vowel's name for a position: the nearest one, and the neighbour it is blending towards with the share of it.</summary>
        public static string Describe(double pos)
        {
            double x = Math.Clamp(pos, 0, 1) * (Vowels - 1);
            int near = (int)Math.Round(x);
            double off = x - near;
            int other = off > 0 ? near + 1 : near - 1;
            if (other < 0 || other >= Vowels || Math.Abs(off) < 0.005) return VowelNames[near];
            return $"{VowelNames[near]} · {Math.Abs(off) * 100:0} % to {VowelNames[other]}";
        }
    }
}
