#nullable disable
// Neo analysis library (docs/specs/mcp-server.md §7): loudness and peaks, the numbers mixing and
// mastering are judged by. ITU-R BS.1770 K-weighting with gated integrated loudness, momentary
// (400 ms) and short-term (3 s) maxima, EBU Tech 3342 loudness range, 4× oversampled true peak.
// Pure C#, shared by the agent server and the analysis machines; checked against the EBU
// Tech 3341/3342 test signals (generated in the tests).

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Analysis
{
    internal sealed class LoudnessResult
    {
        /// <summary>Gated integrated loudness, LUFS (−∞ when everything is below the gate).</summary>
        public double Integrated;
        public double ShortTermMax;
        public double MomentaryMax;
        /// <summary>Loudness range (EBU Tech 3342), LU.</summary>
        public double Range;
        public double TruePeakDb;
        public double SamplePeakDb;
        /// <summary>Samples at or beyond full scale.</summary>
        public int Clipped;
        public double Seconds;
        /// <summary>Short-term loudness every 100 ms (for plots and "loudest section").</summary>
        public double[] ShortTerm;
        public double[] Momentary;
    }

    internal static class Loudness
    {
        const double Offset = -0.691;

        /// <summary>The K-weighting filter (BS.1770 stage 1 shelf + stage 2 high-pass) for a sample rate.</summary>
        internal struct KWeighting
        {
            double b0, b1, b2, a1, a2, c0, c1, c2, d1, d2;
            double x1, x2, y1, y2, u1, u2, v1, v2;

            public static KWeighting For(double rate)
            {
                var k = new KWeighting();
                // Stage 1: high shelf (+4 dB above ~1.7 kHz), coefficients for any rate (as libebur128).
                double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
                double kk = Math.Tan(Math.PI * f0 / rate);
                double vh = Math.Pow(10, g / 20), vb = Math.Pow(vh, 0.4996667741545416);
                double a0 = 1 + kk / q + kk * kk;
                k.b0 = (vh + vb * kk / q + kk * kk) / a0;
                k.b1 = 2 * (kk * kk - vh) / a0;
                k.b2 = (vh - vb * kk / q + kk * kk) / a0;
                k.a1 = 2 * (kk * kk - 1) / a0;
                k.a2 = (1 - kk / q + kk * kk) / a0;
                // Stage 2: RLB high-pass (~38 Hz).
                f0 = 38.13547087602444;
                q = 0.5003270373238773;
                kk = Math.Tan(Math.PI * f0 / rate);
                double a0b = 1 + kk / q + kk * kk;
                k.c0 = 1; k.c1 = -2; k.c2 = 1;
                k.d1 = 2 * (kk * kk - 1) / a0b;
                k.d2 = (1 - kk / q + kk * kk) / a0b;
                return k;
            }

            public double Process(double x)
            {
                double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                double z = c0 * y + c1 * u1 + c2 * u2 - d1 * v1 - d2 * v2;
                u2 = u1; u1 = y; v2 = v1; v1 = z;
                return z;
            }

            /// <summary>
            /// Filters <paramref name="n"/> samples as <see cref="Process"/> does, one by one, and
            /// returns the sum of the outputs' squares (for streaming meters: the state stays in registers).
            /// </summary>
            public double SumOfSquares(float[] x, int offset, int n)
            {
                double sb0 = b0, sb1 = b1, sb2 = b2, sa1 = a1, sa2 = a2, sc0 = c0, sc1 = c1, sc2 = c2, sd1 = d1, sd2 = d2;
                double sx1 = x1, sx2 = x2, sy1 = y1, sy2 = y2, su1 = u1, su2 = u2, sv1 = v1, sv2 = v2;
                double sum = 0;
                for (int i = offset, end = offset + n; i < end; i++)
                {
                    double xi = x[i];
                    double y = sb0 * xi + sb1 * sx1 + sb2 * sx2 - sa1 * sy1 - sa2 * sy2;
                    sx2 = sx1; sx1 = xi; sy2 = sy1; sy1 = y;
                    double z = sc0 * y + sc1 * su1 + sc2 * su2 - sd1 * sv1 - sd2 * sv2;
                    su2 = su1; su1 = y; sv2 = sv1; sv1 = z;
                    sum += z * z;
                }
                x1 = sx1; x2 = sx2; y1 = sy1; y2 = sy2; u1 = su1; u2 = su2; v1 = sv1; v2 = sv2;
                return sum;
            }
        }

        internal static double Lufs(double meanSquare) => meanSquare <= 0 ? double.NegativeInfinity : Offset + 10 * Math.Log10(meanSquare);

        /// <summary>Measures a stereo (or mono: <paramref name="right"/> null) signal at <paramref name="rate"/> Hz, ±1 full scale.</summary>
        public static LoudnessResult Measure(float[] left, float[] right, int rate)
        {
            int n = left.Length;
            var kl = KWeighting.For(rate);
            var kr = KWeighting.For(rate);
            int hop = (int)Math.Round(rate * 0.1);
            int blocks = n / hop;
            // Energy (sum over channels of mean square) per 100 ms step.
            var step = new double[blocks];
            for (int b = 0; b < blocks; b++)
            {
                double sum = 0;
                for (int i = b * hop, end = i + hop; i < end; i++)
                {
                    double l = kl.Process(left[i]);
                    sum += l * l;
                    if (right != null)
                    {
                        double r = kr.Process(right[i]);
                        sum += r * r;
                    }
                }
                step[b] = sum / hop;
            }
            // Mono counts as both channels (BS.1770 weights are per channel; a mono file played on two speakers).
            if (right == null) for (int b = 0; b < blocks; b++) step[b] *= 2;

            var momentary = Windows(step, 4);
            var shortTerm = Windows(step, 30);
            var result = new LoudnessResult
            {
                Seconds = n / (double)rate,
                Momentary = momentary.Select(Lufs).ToArray(),
                ShortTerm = shortTerm.Select(Lufs).ToArray(),
            };
            result.MomentaryMax = result.Momentary.Length > 0 ? result.Momentary.Max() : double.NegativeInfinity;
            result.ShortTermMax = result.ShortTerm.Length > 0 ? result.ShortTerm.Max() : double.NegativeInfinity;
            result.Integrated = Gated(momentary, -10);
            result.Range = Range(shortTerm);

            double peak = 0;
            int clipped = 0;
            foreach (var ch in right == null ? new[] { left } : new[] { left, right })
                foreach (float x in ch)
                {
                    double a = Math.Abs(x);
                    if (a > peak) peak = a;
                    if (a >= 0.99999) clipped++;
                }
            result.SamplePeakDb = Db(peak);
            result.Clipped = clipped;
            result.TruePeakDb = Db(Math.Max(peak, Math.Max(TruePeak(left), right == null ? 0 : TruePeak(right))));
            return result;
        }

        static double Db(double a) => a <= 0 ? double.NegativeInfinity : 20 * Math.Log10(a);

        /// <summary>Mean energy over sliding windows of <paramref name="length"/> 100 ms steps, every step.</summary>
        static double[] Windows(double[] step, int length)
        {
            if (step.Length < length) return Array.Empty<double>();
            var w = new double[step.Length - length + 1];
            double sum = 0;
            for (int i = 0; i < length; i++) sum += step[i];
            w[0] = sum / length;
            for (int i = length; i < step.Length; i++)
            {
                sum += step[i] - step[i - length];
                w[i - length + 1] = Math.Max(0, sum / length);
            }
            return w;
        }

        /// <summary>BS.1770 gating: blocks above −70 LUFS, then above (their mean − <paramref name="relative"/> LU).</summary>
        static double Gated(double[] blocks, double relative)
        {
            var above = blocks.Where(e => Lufs(e) > -70).ToList();
            if (above.Count == 0) return double.NegativeInfinity;
            double gate = Lufs(above.Average()) + relative;
            var kept = above.Where(e => Lufs(e) > gate).ToList();
            return kept.Count == 0 ? double.NegativeInfinity : Lufs(kept.Average());
        }

        /// <summary>EBU Tech 3342: short-term values above −70 LUFS and above (mean − 20 LU); 95th − 10th percentile.</summary>
        static double Range(double[] shortTerm)
        {
            var above = shortTerm.Where(e => Lufs(e) > -70).ToList();
            if (above.Count == 0) return 0;
            double gate = Lufs(above.Average()) - 20;
            var values = above.Select(Lufs).Where(l => l > gate).OrderBy(l => l).ToArray();
            if (values.Length == 0) return 0;
            return Percentile(values, 0.95) - Percentile(values, 0.10);
        }

        static double Percentile(double[] sorted, double p)
        {
            double pos = p * (sorted.Length - 1);
            int i = (int)Math.Floor(pos);
            double f = pos - i;
            return i + 1 < sorted.Length ? sorted[i] + f * (sorted[i + 1] - sorted[i]) : sorted[i];
        }

        // True peak: 4× oversampling with a windowed-sinc interpolator (24 taps per phase, Kaiser).
        // The streaming meter (LoudnessMeter.cs: TruePeakMeter) runs the same coefficients.
        internal const int Phases = 4, Taps = 24;
        internal static readonly double[][] Interpolator = BuildInterpolator();

        static double[][] BuildInterpolator()
        {
            var h = new double[Phases][];
            double beta = 8.0;
            double I0(double x)
            {
                double s = 1, t = 1;
                for (int k = 1; k < 30; k++) { t *= x / (2 * k); s += t * t; }
                return s;
            }
            int len = Phases * Taps;
            for (int p = 0; p < Phases; p++)
            {
                h[p] = new double[Taps];
                for (int k = 0; k < Taps; k++)
                {
                    // Tap k of phase p sits at offset (k − Taps/2 + 1) − p/Phases input samples.
                    double t = (k - Taps / 2 + 1) - p / (double)Phases;
                    double sinc = Math.Abs(t) < 1e-12 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
                    double pos = (t + Taps / 2.0) / Taps;   // 0 … 1 across the filter
                    double w = Math.Abs(pos - 0.5) <= 0.5 ? I0(beta * Math.Sqrt(1 - Math.Pow(2 * pos - 1, 2))) / I0(beta) : 0;
                    h[p][k] = sinc * w;
                }
            }
            return h;
        }

        /// <summary>The largest absolute value of the 4× oversampled signal (linear).</summary>
        public static double TruePeak(float[] x)
        {
            double peak = 0;
            int n = x.Length;
            foreach (float v in x) peak = Math.Max(peak, Math.Abs(v));
            for (int i = 0; i < n; i++)
            {
                // Between samples a band-limited signal overshoots its neighbours by far less than
                // 2× (6 dB): skip stretches that can't beat the peak found so far.
                double near = Math.Abs(x[i]);
                if (i > 0) near = Math.Max(near, Math.Abs(x[i - 1]));
                if (i + 1 < n) near = Math.Max(near, Math.Abs(x[i + 1]));
                if (i > 1) near = Math.Max(near, Math.Abs(x[i - 2]));
                if (2 * near < peak) continue;
                for (int p = 1; p < Phases; p++)
                {
                    var h = Interpolator[p];
                    double s = 0;
                    for (int k = 0; k < Taps; k++)
                    {
                        int j = i - (k - Taps / 2 + 1);
                        if (j >= 0 && j < n) s += x[j] * h[k];
                    }
                    if (Math.Abs(s) > peak) peak = Math.Abs(s);
                }
            }
            return peak;
        }
    }
}
