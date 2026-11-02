#nullable disable
// Neo analysis library (docs/specs/mcp-server.md §7): the key. A chroma vector from the Welch
// spectrum's peaks, 55 Hz – 5 kHz, overtones left out (each peak to its nearest pitch class,
// weighted toward the class's centre; per octave and class the amplitude, so one loud bass octave
// doesn't decide alone), then Krumhansl–Schmuckler: the Pearson correlation with the
// Krumhansl–Kessler major and minor profiles in all 24 keys.

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Analysis
{
    internal sealed class KeyResult
    {
        /// <summary>Energy per pitch class (C = 0), the largest 1.</summary>
        public double[] Chroma;
        /// <summary>All 24 keys, best first.</summary>
        public (int Tonic, bool Minor, double Correlation)[] Ranking;
        public int Tonic => Ranking[0].Tonic;
        public bool Minor => Ranking[0].Minor;
        public double Correlation => Ranking[0].Correlation;
        public string Name => KeyFinder.Name(Ranking[0].Tonic, Ranking[0].Minor);
        public string RunnerUp => KeyFinder.Name(Ranking[1].Tonic, Ranking[1].Minor);
        public double RunnerUpCorrelation => Ranking[1].Correlation;
    }

    internal static class KeyFinder
    {
        public static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        public static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
        public static readonly string[] PitchClasses = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        static readonly string[] MajorNames = { "C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };
        static readonly string[] MinorNames = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "G#", "A", "Bb", "B" };

        /// <summary>"F# major", "Bb minor": the usual spelling of each key.</summary>
        public static string Name(int tonic, bool minor) => (minor ? MinorNames : MajorNames)[((tonic % 12) + 12) % 12] + (minor ? " minor" : " major");

        public static KeyResult Measure(float[] left, float[] right, int rate) => Find(Chroma(Welch.Measure(left, right, rate)));

        /// <summary>
        /// Pitch-class energy of the spectral peaks that are fundamentals: a peak within 30 cents
        /// of a whole multiple (2–12) of a stronger, lower peak is that note's overtone and is left
        /// out, so a bright C doesn't add G and E.
        /// </summary>
        public static double[] Chroma(Psd psd, double loHz = 55, double hiHz = 5000)
        {
            var p = psd.Mean;
            double df = psd.BinHz;
            hiHz = Math.Min(hiHz, psd.Nyquist);
            int octaves = 9;
            var cells = new double[octaves, 12];
            int k0 = Math.Max(2, (int)Math.Ceiling(loHz / df)), k1 = Math.Min(p.Length - 3, (int)Math.Floor(hiHz / df));
            double max = 0;
            for (int k = k0; k <= k1; k++) max = Math.Max(max, p[k]);
            // Peaks: local maxima over ±2 bins, within 60 dB of the loudest, not a window side lobe
            // (nothing 20 dB stronger within 6 bins).
            var candidates = new List<int>();
            for (int k = k0; k <= k1 && max > 0; k++)
                if (p[k] >= max * 1e-6 && p[k] > p[k - 1] && p[k] >= p[k + 1] && p[k] > p[k - 2] && p[k] >= p[k + 2]) candidates.Add(k);
            var peaks = new List<(double Hz, double Power)>();
            foreach (int k in candidates)
            {
                bool sideLobe = false;
                foreach (int j in candidates)
                    if (j != k && Math.Abs(j - k) <= 6 && p[j] > 100 * p[k]) { sideLobe = true; break; }
                if (sideLobe) continue;
                double a = Math.Log(Math.Max(p[k - 1], 1e-30)), b = Math.Log(p[k]), c = Math.Log(Math.Max(p[k + 1], 1e-30)), den = a - 2 * b + c;
                double off = den < 0 ? Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5) : 0;
                peaks.Add(((k + off) * df, p[k - 2] + p[k - 1] + p[k] + p[k + 1] + p[k + 2]));
            }
            var fundamentals = new List<(double Hz, double Power)>();
            foreach (var peak in peaks)
            {
                bool overtone = false;
                foreach (var f0 in fundamentals)
                {
                    double ratio = peak.Hz / f0.Hz;
                    int h = (int)Math.Round(ratio);
                    if (h < 2 || h > 12 || peak.Power > f0.Power) continue;
                    if (Math.Abs(1200 * Math.Log2(ratio / h)) < 30) { overtone = true; break; }
                }
                if (overtone) continue;
                fundamentals.Add(peak);
                double midi = 69 + 12 * Math.Log2(peak.Hz / 440);
                int note = (int)Math.Round(midi);
                double weight = Math.Cos(Math.PI * (midi - note));
                int octave = Math.Clamp(note / 12 - 1, 0, octaves - 1);
                cells[octave, ((note % 12) + 12) % 12] += weight * weight * peak.Power;
            }
            var chroma = new double[12];
            for (int o = 0; o < octaves; o++)
                for (int c = 0; c < 12; c++) chroma[c] += Math.Sqrt(cells[o, c]);
            double top = chroma.Max();
            if (top > 0) for (int c = 0; c < 12; c++) chroma[c] /= top;
            return chroma;
        }

        public static KeyResult Find(double[] chroma)
        {
            var ranking = new (int, bool, double)[24];
            for (int tonic = 0; tonic < 12; tonic++)
            {
                ranking[tonic] = (tonic, false, Correlate(chroma, MajorProfile, tonic));
                ranking[12 + tonic] = (tonic, true, Correlate(chroma, MinorProfile, tonic));
            }
            return new KeyResult { Chroma = chroma, Ranking = ranking.OrderByDescending(r => r.Item3).ToArray() };
        }

        /// <summary>Pearson correlation of the chroma with a profile rotated to <paramref name="tonic"/>.</summary>
        static double Correlate(double[] chroma, double[] profile, int tonic)
        {
            double mc = chroma.Average(), mp = profile.Average();
            double sxy = 0, sxx = 0, syy = 0;
            for (int c = 0; c < 12; c++)
            {
                double x = chroma[c] - mc, y = profile[((c - tonic) % 12 + 12) % 12] - mp;
                sxy += x * y;
                sxx += x * x;
                syy += y * y;
            }
            return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : 0;
        }
    }
}
