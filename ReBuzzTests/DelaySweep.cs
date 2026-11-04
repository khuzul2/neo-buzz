using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/mcp-server.md §12.10, U5): how a closed modulated delay (a flanger or chorus) sweeps, measured
    /// from its input and output. Over short windows of the output, the lag where the input correlates best is the delay
    /// then (refined between samples); over a render, those delays give the sweep's shortest and longest delay, its period,
    /// how long it lingers at the bottom (a sine: about a fifth of the time in the lowest tenth; a triangle: a tenth) and
    /// the wet's gain against the input. Feed it the wet signal only (no dry), one voice and no feedback.
    /// </summary>
    internal static class DelaySweep
    {
        public sealed record Result(double LoMs, double HiMs, double PeriodS, double BottomShare, double Gain, double Correlation, double SecondPeak, int Windows)
        {
            static readonly CultureInfo C = CultureInfo.InvariantCulture;

            public override string ToString() => Windows == 0 ? "no signal" :
                string.Format(C, "{0:0.000}–{1:0.000} ms, period {2:0.00} s, bottom {3:0.00}, gain {4:0.000}, r {5:0.00}, 2nd {6:0.00}",
                    LoMs, HiMs, PeriodS, BottomShare, Gain, Correlation, SecondPeak);
        }

        /// <param name="reference">The input (mono).</param>
        /// <param name="output">The output (one channel).</param>
        /// <param name="minLag">Lags searched, samples (above 0 when a dry signal or a short delay must be skipped).</param>
        public static Result Measure(float[] reference, float[] output, int rate, int minLag, int maxLag, double fromSeconds = 0.5, int window = 1024, double hopSeconds = 0.05)
        {
            int n = Math.Min(reference.Length, output.Length);
            var energy = new double[n + 1];
            for (int i = 0; i < n; i++) energy[i + 1] = energy[i] + (double)reference[i] * reference[i];
            int hop = Math.Max(1, (int)(hopSeconds * rate));
            var times = new List<double>();
            var delays = new List<double>();
            var gains = new List<double>();
            var corrs = new List<double>();
            var seconds = new List<double>();
            var c = new double[maxLag - minLag + 1];
            for (int t = Math.Max(maxLag + 1, (int)(fromSeconds * rate)); t + window < n; t += hop)
            {
                double eo = 0;
                for (int i = 0; i < window; i++) eo += (double)output[t + i] * output[t + i];
                if (eo < 1e-6 * window) continue;
                int best = -1;
                double bestNorm = 0;
                for (int lag = minLag; lag <= maxLag; lag++)
                {
                    double s = 0;
                    int o = t - lag;
                    for (int i = 0; i < window; i++) s += (double)output[t + i] * reference[o + i];
                    c[lag - minLag] = s;
                    double er = energy[o + window] - energy[o];
                    double norm = er > 0 ? s / Math.Sqrt(eo * er) : 0;
                    if (norm > bestNorm) { bestNorm = norm; best = lag; }
                }
                if (best < 0 || bestNorm < 0.3) continue;
                double frac = 0;
                if (best > minLag && best < maxLag)
                {
                    double a = c[best - minLag - 1], b = c[best - minLag], d = c[best - minLag + 1];
                    double den = a - 2 * b + d;
                    if (den < 0) frac = Math.Clamp(0.5 * (a - d) / den, -0.5, 0.5);
                }
                double second = 0;
                for (int lag = minLag; lag <= maxLag; lag++)
                {
                    if (Math.Abs(lag - best) <= 20) continue;
                    int o = t - lag;
                    double er = energy[o + window] - energy[o];
                    double norm = er > 0 ? c[lag - minLag] / Math.Sqrt(eo * er) : 0;
                    if (norm > second) second = norm;
                }
                int ob = t - best;
                double eb = energy[ob + window] - energy[ob];
                times.Add(t / (double)rate);
                delays.Add((best + frac) * 1000.0 / rate);
                gains.Add(eb > 0 ? c[best - minLag] / eb : 0);
                corrs.Add(bestNorm);
                seconds.Add(second / bestNorm);
            }
            if (delays.Count == 0) return new Result(0, 0, 0, 0, 0, 0, 0, 0);
            var sorted = delays.OrderBy(x => x).ToList();
            double lo = sorted[(int)(0.01 * (sorted.Count - 1))], hi = sorted[(int)Math.Ceiling(0.99 * (sorted.Count - 1))];
            double mid = (lo + hi) / 2, range = hi - lo;
            var ups = new List<double>();
            for (int i = 1; i < delays.Count; i++)
                if (delays[i - 1] < mid && delays[i] >= mid && range > 0.02) ups.Add(times[i]);
            double period = ups.Count >= 2 ? (ups[^1] - ups[0]) / (ups.Count - 1) : 0;
            double bottom = range > 0.02 ? delays.Count(d => d < lo + 0.1 * range) / (double)delays.Count : 0;
            return new Result(lo, hi, period, bottom, Median(gains), Median(corrs), Median(seconds), delays.Count);
        }

        static double Median(List<double> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s.Count == 0 ? 0 : s[s.Count / 2];
        }

        /// <summary>The correlation of two channels (1: the same signal).</summary>
        public static double Correlation(float[] a, float[] b)
        {
            double ab = 0, aa = 0, bb = 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) { ab += (double)a[i] * b[i]; aa += (double)a[i] * a[i]; bb += (double)b[i] * b[i]; }
            return aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : 0;
        }

        /// <summary>The RMS ratio of two signals in dB.</summary>
        public static double GainDb(float[] reference, float[] output, int from)
        {
            double r = 0, o = 0;
            for (int i = from; i < Math.Min(reference.Length, output.Length); i++) { r += (double)reference[i] * reference[i]; o += (double)output[i] * output[i]; }
            return r > 0 && o > 0 ? 10 * Math.Log10(o / r) : double.NegativeInfinity;
        }
    }
}
