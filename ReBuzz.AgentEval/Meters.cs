// Evaluation: our own meters on rendered files. Scores never trust a tool's own report of how
// something sounds: they read the WAV the render wrote and measure it with NeoBuzz.Analysis, the
// library the server and the analysis machines share (docs/specs/mcp-server.md §7).

using System;
using System.Collections.Generic;
using System.Linq;
using NeoBuzz.Analysis;
using ReBuzz.Agent.Audio;

namespace ReBuzz.AgentEval
{
    internal static class Meters
    {
        public static StereoAudio Read(string file) => Wav.Read(file);

        static float[] R(StereoAudio a) => a.Right != null && !ReferenceEquals(a.Right, a.Left) ? a.Right : null;

        public static LoudnessResult Loudness(StereoAudio a) => NeoBuzz.Analysis.Loudness.Measure(a.Left, R(a), a.Rate);

        public static SpectrumResult Spectrum(StereoAudio a) => NeoBuzz.Analysis.Spectrum.Measure(a.Left, R(a), a.Rate);

        public static StereoResult Stereo(StereoAudio a) => NeoBuzz.Analysis.Stereo.Measure(a.Left, R(a), a.Rate);

        public static DynamicsResult Dynamics(StereoAudio a) =>
            NeoBuzz.Analysis.Dynamics.Measure(a.Left, R(a), a.Rate, Loudness(a), new List<double>());

        public static float[] Mono(StereoAudio a) => NeoBuzz.Analysis.Dynamics.Mono(a.Left, R(a));

        public static List<double> Onsets(StereoAudio a) => NeoBuzz.Analysis.Tempo.Onsets(Mono(a), a.Rate);

        public static TempoResult Tempo(StereoAudio a) => NeoBuzz.Analysis.Tempo.Measure(Mono(a), a.Rate);

        public static KeyResult Key(StereoAudio a) => KeyFinder.Measure(a.Left, R(a), a.Rate);

        public static PitchTrack Pitch(StereoAudio a, double minHz = 30, double maxHz = 2000) => Yin.Track(Mono(a), a.Rate, minHz, maxHz);

        /// <summary>How much two tracks fight (masking, Bark bands, 85 ms frames): the worst band's severity and the sum over bands.</summary>
        public static (double Worst, double Sum, string Where) Masking(StereoAudio a, StereoAudio b)
        {
            var fa = BandFrames.Analyze("a", a.Left, R(a), a.Rate);
            var fb = BandFrames.Analyze("b", b.Left, R(b), b.Rate);
            var pair = NeoBuzz.Analysis.Masking.Pair(fa, fb);
            string where = string.Join(", ", pair.Bands.Take(3).Select(x => $"{x.LoHz:0}–{x.HiHz:0} Hz {x.Severity:0.00}"));
            return (pair.Severity, pair.Bands.Sum(x => x.Severity), where.Length == 0 ? "none" : where);
        }

        public static StereoAudio Slice(StereoAudio a, double from, double to) => AudioReport.Slice(a, from, to);

        public static double PeakDb(StereoAudio a)
        {
            float peak = 0;
            foreach (var x in a.Left) peak = Math.Max(peak, Math.Abs(x));
            if (R(a) is float[] r) foreach (var x in r) peak = Math.Max(peak, Math.Abs(x));
            return peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        }

        public static bool Silent(StereoAudio a) => PeakDb(a) < -90;

        /// <summary>RMS per window of both channels, dBFS (−∞ for digital silence).</summary>
        public static double[] EnvelopeDb(StereoAudio a, double windowSeconds = 0.01)
        {
            int w = Math.Max(1, (int)Math.Round(windowSeconds * a.Rate));
            var r = R(a);
            int n = a.Frames / w;
            var env = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                for (int j = i * w; j < (i + 1) * w; j++)
                {
                    double l = a.Left[j];
                    sum += l * l;
                    if (r != null) sum += r[j] * (double)r[j];
                }
                double ms = sum / (w * (r != null ? 2 : 1));
                env[i] = ms > 1e-20 ? 10 * Math.Log10(ms) : double.NegativeInfinity;
            }
            return env;
        }

        /// <summary>
        /// Level-matched distance between two spectra: the mean absolute difference of their third
        /// octaves, 50 Hz – 16 kHz, after removing the overall level difference, over the bands where
        /// the reference is within <paramref name="rangeDb"/> of its loudest band.
        /// </summary>
        public static (double Distance, double LevelDiff) SpectralDistance(SpectrumResult reference, SpectrumResult candidate, double rangeDb = 50)
        {
            var hz = reference.ThirdOctaveHz;
            double max = reference.ThirdOctaveDb.Where(d => !double.IsNegativeInfinity(d)).DefaultIfEmpty(double.NegativeInfinity).Max();
            var bands = Enumerable.Range(0, hz.Length).Where(i => hz[i] >= 50 && hz[i] <= 16000 && reference.ThirdOctaveDb[i] > max - rangeDb).ToList();
            if (bands.Count == 0) return (double.PositiveInfinity, double.NaN);
            double Clamp(double d) => double.IsNegativeInfinity(d) ? max - rangeDb - 20 : d;
            var diffs = bands.Select(i => Clamp(candidate.ThirdOctaveDb[i]) - Clamp(reference.ThirdOctaveDb[i])).ToList();
            double offset = Median(diffs);
            return (diffs.Average(d => Math.Abs(d - offset)), offset);
        }

        /// <summary>
        /// Level-matched distance between two loudness envelopes (10 ms RMS, dB): the mean absolute
        /// difference over the windows where either is within <paramref name="rangeDb"/> of the
        /// reference's peak, after removing the median level difference; the shorter is padded with silence.
        /// </summary>
        public static double EnvelopeDistance(StereoAudio reference, StereoAudio candidate, double rangeDb = 40)
        {
            var a = EnvelopeDb(reference);
            var b = EnvelopeDb(candidate);
            int n = Math.Max(a.Length, b.Length);
            double peak = a.Where(x => !double.IsNegativeInfinity(x)).DefaultIfEmpty(-200).Max();
            double floor = peak - rangeDb;
            double At(double[] e, int i) => i < e.Length && !double.IsNegativeInfinity(e[i]) ? Math.Max(e[i], floor) : floor;
            var idx = Enumerable.Range(0, n).Where(i => At(a, i) > floor || At(b, i) > floor).ToList();
            if (idx.Count == 0) return double.PositiveInfinity;
            double offset = Median(idx.Where(i => At(a, i) > floor && At(b, i) > floor).Select(i => At(b, i) - At(a, i)).DefaultIfEmpty(0).ToList());
            return idx.Average(i => Math.Abs(Math.Max(At(b, i) - offset, floor) - At(a, i)));
        }

        public static double Median(IReadOnlyList<double> xs)
        {
            if (xs.Count == 0) return double.NaN;
            var s = xs.OrderBy(x => x).ToList();
            return s.Count % 2 == 1 ? s[s.Count / 2] : 0.5 * (s[s.Count / 2 - 1] + s[s.Count / 2]);
        }

        /// <summary>A third octave's level above the mean of its neighbours two thirds away on each side, dB.</summary>
        public static double Prominence(SpectrumResult s, double hz)
        {
            int i = Array.IndexOf(s.ThirdOctaveHz, s.ThirdOctaveHz.OrderBy(h => Math.Abs(Math.Log(h / hz))).First());
            double At(int k) => k >= 0 && k < s.ThirdOctaveDb.Length && !double.IsNegativeInfinity(s.ThirdOctaveDb[k]) ? s.ThirdOctaveDb[k] : -150;
            double neighbours = 10 * Math.Log10((Math.Pow(10, At(i - 2) / 10) + Math.Pow(10, At(i + 2) / 10) + Math.Pow(10, At(i - 3) / 10) + Math.Pow(10, At(i + 3) / 10)) / 4);
            return At(i) - neighbours;
        }

        /// <summary>The longest stretch, in seconds, where the 50 ms RMS stays below <paramref name="floorDb"/> between the first and last sound.</summary>
        public static double LongestGap(StereoAudio a, double floorDb = -60, double windowSeconds = 0.05)
        {
            var env = EnvelopeDb(a, windowSeconds);
            int first = Array.FindIndex(env, x => x > floorDb), last = Array.FindLastIndex(env, x => x > floorDb);
            if (first < 0) return a.Seconds;
            int run = 0, longest = 0;
            for (int i = first; i <= last; i++)
            {
                run = env[i] > floorDb ? 0 : run + 1;
                longest = Math.Max(longest, run);
            }
            return longest * windowSeconds;
        }
    }
}
