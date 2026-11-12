#nullable disable
// Neo analysis library (docs/specs/mcp-server.md §7): loudness and peaks, the numbers mixing and
// mastering are judged by. ITU-R BS.1770 K-weighting with gated integrated loudness, momentary
// (400 ms) and short-term (3 s) maxima, EBU Tech 3342 loudness range, 4× oversampled true peak.
// Pure C#, shared by the agent server and the analysis machines; checked against the EBU
// Tech 3341/3342 test signals (generated in the tests).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

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

        /// <summary>
        /// The K-weighting filter (BS.1770 stage 1 shelf + stage 2 high-pass) for a sample rate.
        /// Each stage's output settles to exact zero below <see cref="Tiny"/> (docs/specs/mcp-server.md
        /// §12.13, U6): in silence, or on a constant input, the states otherwise decayed into denormal
        /// numbers and stayed there, and Neo Analyzer, which runs this filter on every block, spent
        /// 25 ms of work a second on silence against 5.5 on music. 1e-15 of full scale is −300 dB, far
        /// under any gate: the measures are as before (a step of silence reads −∞ a little sooner).
        /// </summary>
        internal struct KWeighting
        {
            const double Tiny = 1e-15;
            double b0, b1, b2, a1, a2, c0, c1, c2, d1, d2;
            double x1, x2, y1, y2, u1, u2, v1, v2;

            public static KWeighting For(double rate)
            {
                var k = new KWeighting();
                // Stage 1: high shelf (+4 dB above ~1.7 kHz), coefficients for any rate (as libebur128).
                double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
                double kk = NeoBuzz.Dsp.PortableMath.Tan(Math.PI * f0 / rate);
                double vh = NeoBuzz.Dsp.PortableMath.Pow(10, g / 20), vb = NeoBuzz.Dsp.PortableMath.Pow(vh, 0.4996667741545416);
                double a0 = 1 + kk / q + kk * kk;
                k.b0 = (vh + vb * kk / q + kk * kk) / a0;
                k.b1 = 2 * (kk * kk - vh) / a0;
                k.b2 = (vh - vb * kk / q + kk * kk) / a0;
                k.a1 = 2 * (kk * kk - 1) / a0;
                k.a2 = (1 - kk / q + kk * kk) / a0;
                // Stage 2: RLB high-pass (~38 Hz).
                f0 = 38.13547087602444;
                q = 0.5003270373238773;
                kk = NeoBuzz.Dsp.PortableMath.Tan(Math.PI * f0 / rate);
                double a0b = 1 + kk / q + kk * kk;
                k.c0 = 1; k.c1 = -2; k.c2 = 1;
                k.d1 = 2 * (kk * kk - 1) / a0b;
                k.d2 = (1 - kk / q + kk * kk) / a0b;
                return k;
            }

            public double Process(double x)
            {
                double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                if (Math.Abs(y) < Tiny) y = 0;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                double z = c0 * y + c1 * u1 + c2 * u2 - d1 * v1 - d2 * v2;
                if (Math.Abs(z) < Tiny) z = 0;
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
                    if (Math.Abs(y) < Tiny) y = 0;
                    sx2 = sx1; sx1 = xi; sy2 = sy1; sy1 = y;
                    double z = sc0 * y + sc1 * su1 + sc2 * su2 - sd1 * sv1 - sd2 * sv2;
                    if (Math.Abs(z) < Tiny) z = 0;
                    su2 = su1; su1 = y; sv2 = sv1; sv1 = z;
                    sum += z * z;
                }
                x1 = sx1; x2 = sx2; y1 = sy1; y2 = sy2; u1 = su1; u2 = su2; v1 = sv1; v2 = sv2;
                return sum;
            }

            /// <summary>
            /// <see cref="SumOfSquares"/> on four streams at once (each with its filter), in the lanes of two Vector128&lt;double&gt;:
            /// each lane the same operations in the same order as one stream alone, so the same bits (M3: GainMatch's
            /// four filters, a tenth of Neo Clipper's CPU one at a time).
            /// </summary>
            public static void SumOfSquares4(ref KWeighting k0, ref KWeighting k1, ref KWeighting k2, ref KWeighting k3,
                float[] x0, float[] x1, float[] x2, float[] x3, int offset, int n, out double s0, out double s1, out double s2, out double s3)
            {
                if (offset < 0 || n < 0 || offset + n > Math.Min(Math.Min(x0.Length, x1.Length), Math.Min(x2.Length, x3.Length)))
                    throw new ArgumentOutOfRangeException(nameof(n));
                Lanes(k0, k1, out var b0a, out var b1a, out var b2a, out var a1a, out var a2a, out var c0a, out var c1a, out var c2a, out var d1a, out var d2a);
                Lanes(k2, k3, out var b0b, out var b1b, out var b2b, out var a1b, out var a2b, out var c0b, out var c1b, out var c2b, out var d1b, out var d2b);
                var xa1 = V(k0.x1, k1.x1); var xa2 = V(k0.x2, k1.x2); var ya1 = V(k0.y1, k1.y1); var ya2 = V(k0.y2, k1.y2);
                var ua1 = V(k0.u1, k1.u1); var ua2 = V(k0.u2, k1.u2); var va1 = V(k0.v1, k1.v1); var va2 = V(k0.v2, k1.v2);
                var xb1 = V(k2.x1, k3.x1); var xb2 = V(k2.x2, k3.x2); var yb1 = V(k2.y1, k3.y1); var yb2 = V(k2.y2, k3.y2);
                var ub1 = V(k2.u1, k3.u1); var ub2 = V(k2.u2, k3.u2); var vb1 = V(k2.v1, k3.v1); var vb2 = V(k2.v2, k3.v2);
                var tiny = Vector128.Create(Tiny);
                var suma = Vector128<double>.Zero;
                var sumb = Vector128<double>.Zero;
                for (int i = offset, end = offset + n; i < end; i++)
                {
                    var xa = V(x0[i], x1[i]);
                    var xb = V(x2[i], x3[i]);
                    var ya = b0a * xa + b1a * xa1 + b2a * xa2 - a1a * ya1 - a2a * ya2;
                    var yb = b0b * xb + b1b * xb1 + b2b * xb2 - a1b * yb1 - a2b * yb2;
                    ya = Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(ya), tiny), Vector128<double>.Zero, ya);
                    yb = Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(yb), tiny), Vector128<double>.Zero, yb);
                    xa2 = xa1; xa1 = xa; ya2 = ya1; ya1 = ya;
                    xb2 = xb1; xb1 = xb; yb2 = yb1; yb1 = yb;
                    var za = c0a * ya + c1a * ua1 + c2a * ua2 - d1a * va1 - d2a * va2;
                    var zb = c0b * yb + c1b * ub1 + c2b * ub2 - d1b * vb1 - d2b * vb2;
                    za = Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(za), tiny), Vector128<double>.Zero, za);
                    zb = Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(zb), tiny), Vector128<double>.Zero, zb);
                    ua2 = ua1; ua1 = ya; va2 = va1; va1 = za;
                    ub2 = ub1; ub1 = yb; vb2 = vb1; vb1 = zb;
                    suma += za * za;
                    sumb += zb * zb;
                }
                Back(ref k0, ref k1, xa1, xa2, ya1, ya2, ua1, ua2, va1, va2);
                Back(ref k2, ref k3, xb1, xb2, yb1, yb2, ub1, ub2, vb1, vb2);
                s0 = suma.GetElement(0);
                s1 = suma.GetElement(1);
                s2 = sumb.GetElement(0);
                s3 = sumb.GetElement(1);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static Vector128<double> V(double a, double b) => Vector128.Create(a, b);

            static void Lanes(in KWeighting p, in KWeighting q, out Vector128<double> b0, out Vector128<double> b1, out Vector128<double> b2,
                out Vector128<double> a1, out Vector128<double> a2, out Vector128<double> c0, out Vector128<double> c1, out Vector128<double> c2,
                out Vector128<double> d1, out Vector128<double> d2)
            {
                b0 = V(p.b0, q.b0); b1 = V(p.b1, q.b1); b2 = V(p.b2, q.b2); a1 = V(p.a1, q.a1); a2 = V(p.a2, q.a2);
                c0 = V(p.c0, q.c0); c1 = V(p.c1, q.c1); c2 = V(p.c2, q.c2); d1 = V(p.d1, q.d1); d2 = V(p.d2, q.d2);
            }

            static void Back(ref KWeighting p, ref KWeighting q, Vector128<double> x1, Vector128<double> x2, Vector128<double> y1, Vector128<double> y2,
                Vector128<double> u1, Vector128<double> u2, Vector128<double> v1, Vector128<double> v2)
            {
                p.x1 = x1.GetElement(0); q.x1 = x1.GetElement(1);
                p.x2 = x2.GetElement(0); q.x2 = x2.GetElement(1);
                p.y1 = y1.GetElement(0); q.y1 = y1.GetElement(1);
                p.y2 = y2.GetElement(0); q.y2 = y2.GetElement(1);
                p.u1 = u1.GetElement(0); q.u1 = u1.GetElement(1);
                p.u2 = u2.GetElement(0); q.u2 = u2.GetElement(1);
                p.v1 = v1.GetElement(0); q.v1 = v1.GetElement(1);
                p.v2 = v2.GetElement(0); q.v2 = v2.GetElement(1);
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
