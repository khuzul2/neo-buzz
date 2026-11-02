#nullable disable
// Neo analysis library (docs/specs/mcp-server.md §7): dynamics. Crest factor (sample peak over
// RMS of the louder channel), PLR (true peak minus integrated loudness, as EBU R128 s1 reads it),
// an RMS envelope in short windows with its spread, and transient density (onsets per second).

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Analysis
{
    internal sealed class DynamicsResult
    {
        /// <summary>Sample peak of either channel, dBFS.</summary>
        public double PeakDb;
        /// <summary>RMS of the channels together (mean of their mean squares), dBFS: a full-scale sine reads −3 dB.</summary>
        public double RmsDb;
        /// <summary>Peak over RMS of the louder channel, dB: a sine 3.0, a square 0, white noise about 12.</summary>
        public double CrestDb;
        public double TruePeakDb;
        public double IntegratedLufs;
        /// <summary>True peak minus integrated loudness, dB (NaN when the integrated loudness is −∞).</summary>
        public double PlrDb;
        public double WindowSeconds;
        /// <summary>RMS per window (channels together), dBFS.</summary>
        public double[] Envelope;
        /// <summary>10th, 50th and 95th percentile of the envelope's windows above −70 dBFS (−∞ when none are).</summary>
        public double EnvelopeP10, EnvelopeP50, EnvelopeP95;
        public List<double> Onsets;
        public double TransientsPerSecond;
    }

    internal static class Dynamics
    {
        public static DynamicsResult Measure(float[] left, float[] right, int rate, LoudnessResult loudness = null, List<double> onsets = null, double window = 0.05)
        {
            bool stereo = right != null && !ReferenceEquals(right, left);
            loudness ??= Loudness.Measure(left, right, rate);
            int n = left.Length;
            double peakL = 0, peakR = 0, sumL = 0, sumR = 0;
            for (int i = 0; i < n; i++)
            {
                double l = left[i];
                sumL += l * l;
                if (Math.Abs(l) > peakL) peakL = Math.Abs(l);
                if (stereo)
                {
                    double r = right[i];
                    sumR += r * r;
                    if (Math.Abs(r) > peakR) peakR = Math.Abs(r);
                }
            }
            if (!stereo) { sumR = sumL; peakR = peakL; }
            var result = new DynamicsResult
            {
                PeakDb = Db(Math.Max(peakL, peakR)),
                RmsDb = n > 0 ? Spectrum.Db(0.5 * (sumL + sumR) / n) : double.NegativeInfinity,
                TruePeakDb = loudness.TruePeakDb,
                IntegratedLufs = loudness.Integrated,
                WindowSeconds = window,
            };
            bool leftLouder = sumL >= sumR;
            double peak = leftLouder ? peakL : peakR, ms = (leftLouder ? sumL : sumR) / Math.Max(1, n);
            result.CrestDb = ms > 0 && peak > 0 ? 20 * Math.Log10(peak) - 10 * Math.Log10(ms) : 0;
            result.PlrDb = double.IsInfinity(loudness.Integrated) || double.IsInfinity(loudness.TruePeakDb)
                ? double.NaN : loudness.TruePeakDb - loudness.Integrated;

            int size = Math.Max(1, (int)Math.Round(window * rate));
            int blocks = n / size;
            result.Envelope = new double[blocks];
            for (int b = 0; b < blocks; b++)
            {
                double s = 0;
                for (int i = b * size, end = i + size; i < end; i++)
                {
                    s += left[i] * left[i];
                    if (stereo) s += right[i] * right[i];
                }
                result.Envelope[b] = Spectrum.Db(s / (stereo ? 2.0 * size : size));
            }
            var loud = result.Envelope.Where(d => d > -70).OrderBy(d => d).ToArray();
            result.EnvelopeP10 = Percentile(loud, 0.10);
            result.EnvelopeP50 = Percentile(loud, 0.50);
            result.EnvelopeP95 = Percentile(loud, 0.95);

            result.Onsets = onsets ?? Tempo.Onsets(Mono(left, right), rate);
            double seconds = n / (double)rate;
            result.TransientsPerSecond = seconds > 0 ? result.Onsets.Count / seconds : 0;
            return result;
        }

        /// <summary>(L + R) / 2, or the one channel.</summary>
        public static float[] Mono(float[] left, float[] right)
        {
            if (right == null || ReferenceEquals(left, right)) return left;
            var m = new float[left.Length];
            for (int i = 0; i < m.Length; i++) m[i] = 0.5f * (left[i] + right[i]);
            return m;
        }

        static double Db(double a) => a > 0 ? 20 * Math.Log10(a) : double.NegativeInfinity;

        static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return double.NegativeInfinity;
            double pos = p * (sorted.Length - 1);
            int i = (int)Math.Floor(pos);
            double f = pos - i;
            return i + 1 < sorted.Length ? sorted[i] + f * (sorted[i + 1] - sorted[i]) : sorted[i];
        }
    }
}
