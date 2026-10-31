#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): halfband filters for 2× resampling.
//  - FirHalfband: linear phase (Kaiser-windowed sinc), integer latency: for effects that mix dry
//    and wet signals (the dry path is delayed to match).
//  - IirHalfband: polyphase allpass pair (design after L. de Soras' HIIR), minimal latency and
//    cost: for synths, where phase doesn't matter.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>Coefficients of a linear-phase halfband lowpass (cutoff at a quarter of the high rate).</summary>
    internal sealed class FirHalfbandDesign
    {
        /// <param name="halfTaps">Nonzero taps on each side of the centre (K); the filter has 4K − 1 taps.</param>
        /// <param name="attenuationDb">Kaiser window design attenuation.</param>
        public FirHalfbandDesign(int halfTaps, double attenuationDb)
        {
            K = Math.Max(1, halfTaps);
            int n = 4 * K - 1;
            int centre = 2 * K - 1;
            double beta = attenuationDb > 50 ? 0.1102 * (attenuationDb - 8.7)
                : attenuationDb >= 21 ? 0.5842 * Math.Pow(attenuationDb - 21, 0.4) + 0.07886 * (attenuationDb - 21) : 0;
            double i0b = BesselI0(beta);
            Taps = new double[n];
            for (int i = 0; i < n; i++)
            {
                int m = i - centre;
                double sinc = m == 0 ? 0.5 : Math.Sin(Math.PI * m / 2) / (Math.PI * m);
                double r = (double)m / centre;
                double w = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
                Taps[i] = m != 0 && m % 2 == 0 ? 0 : sinc * w;
            }
            // Unity DC gain.
            double sum = 0;
            foreach (var t in Taps) sum += t;
            for (int i = 0; i < n; i++) Taps[i] /= sum;
            Odd = new float[K];
            // Nonzero taps besides the centre sit at odd distances from it: centre ± (2j+1).
            for (int j = 0; j < K; j++) Odd[j] = (float)Taps[centre - (2 * j + 1)];
            Centre = (float)Taps[centre];
        }

        public int K { get; }
        public double[] Taps { get; }
        /// <summary>Tap at distance 2j+1 from the centre (symmetric).</summary>
        public float[] Odd { get; }
        public float Centre { get; }

        /// <summary>Group delay at the high rate: the centre index.</summary>
        public int CentreIndex => 2 * K - 1;

        static double BesselI0(double x)
        {
            double sum = 1, term = 1, half = x / 2;
            for (int k = 1; k < 64; k++)
            {
                term *= half / k;
                double t2 = term * term;
                sum += t2;
                if (t2 < sum * 1e-17) break;
            }
            return sum;
        }

        /// <summary>Magnitude response at a frequency given as a fraction of the high rate (0 … 0.5).</summary>
        public double Magnitude(double f)
        {
            double re = 0, im = 0;
            for (int i = 0; i < Taps.Length; i++)
            {
                re += Taps[i] * Math.Cos(2 * Math.PI * f * i);
                im -= Taps[i] * Math.Sin(2 * Math.PI * f * i);
            }
            return Math.Sqrt(re * re + im * im);
        }
    }

    /// <summary>
    /// 2× up and down with one linear-phase halfband. Up: one input sample in, two out. Down: two
    /// in, one out. Latency (base rate) of an up followed by a down: <see cref="RoundTripLatency"/>.
    /// </summary>
    internal sealed class FirHalfband
    {
        readonly FirHalfbandDesign d;
        // Up: history of base-rate inputs. Down: history of the even and odd high-rate phases.
        readonly float[] upHist, evenHist, oddHist;
        int upPos, downPos;
        readonly int len;

        public FirHalfband(FirHalfbandDesign design)
        {
            d = design;
            len = 2 * d.K;
            upHist = new float[2 * len];
            evenHist = new float[2 * len];
            oddHist = new float[2 * len];
        }

        /// <summary>Base-rate samples an up followed by a down delays by (the high-rate centre index, both filters).</summary>
        public int RoundTripLatency => d.CentreIndex;

        /// <summary>Base-rate latency of the upsampler alone, in high-rate samples.</summary>
        public int CentreIndex => d.CentreIndex;

        public void Reset()
        {
            Array.Clear(upHist, 0, upHist.Length);
            Array.Clear(evenHist, 0, evenHist.Length);
            Array.Clear(oddHist, 0, oddHist.Length);
            upPos = downPos = 0;
        }

        /// <summary>One base-rate sample in, two high-rate samples out.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Up(float x, out float y0, out float y1)
        {
            // Zero-stuffed input u[2n] = x[n], u[2n+1] = 0, filtered with gain 2.
            // Output at high-rate index 2n: the taps at even distance from the centre multiply
            // nonzero inputs only through the centre (distance 0) when the centre index is odd,
            // so y[2n] uses the odd-distance taps and y[2n+1] is the delayed sample.
            upPos = (upPos == 0 ? len : upPos) - 1;
            upHist[upPos] = x;
            upHist[upPos + len] = x;
            // History: h[0] newest. Centre at high-rate delay 2K−1: base delay K − 1/2 → the
            // odd-distance taps 2j+1 around it fall on base delays K−1−j and K+j.
            float acc = 0;
            var odd = d.Odd;
            int K = d.K;
            for (int j = 0; j < K; j++)
                acc += odd[j] * (upHist[upPos + K - 1 - j] + upHist[upPos + K + j]);
            y0 = 2f * acc;
            y1 = 2f * d.Centre * upHist[upPos + K - 1];
        }

        /// <summary>Two high-rate samples in (the earlier first), one base-rate sample out.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Down(float x0, float x1)
        {
            downPos = (downPos == 0 ? len : downPos) - 1;
            evenHist[downPos] = x0;
            evenHist[downPos + len] = x0;
            oddHist[downPos] = x1;
            oddHist[downPos + len] = x1;
            // Output aligned with the even sample x0 (high-rate index 2N): the centre (odd index
            // 2K−1) falls on the odd phase at base delay K, the odd-distance taps on the even
            // phase at delays K−1−j and K+j. Up then down delays by exactly 2K−1 base samples.
            var odd = d.Odd;
            int K = d.K;
            float acc = d.Centre * oddHist[downPos + K];
            for (int j = 0; j < K; j++)
                acc += odd[j] * (evenHist[downPos + K - 1 - j] + evenHist[downPos + K + j]);
            return acc;
        }
    }

    /// <summary>
    /// Polyphase IIR halfband (two allpass chains), designed for a transition band and stopband
    /// attenuation. Cheap and nearly free of latency; not linear phase.
    /// </summary>
    internal sealed class IirHalfbandDesign
    {
        public IirHalfbandDesign(double attenuationDb, double transition)
        {
            Coefs = ComputeCoefs(attenuationDb, transition);
        }

        public double[] Coefs { get; }

        static void TransitionParam(double transition, out double k, out double q)
        {
            k = Math.Tan((1 - transition * 2) * Math.PI / 4);
            k *= k;
            double kksqrt = Math.Pow(1 - k * k, 0.25);
            double e = 0.5 * (1 - kksqrt) / (1 + kksqrt);
            double e2 = e * e;
            double e4 = e2 * e2;
            q = e * (1 + e4 * (2 + e4 * (15 + 150 * e4)));
        }

        static double[] ComputeCoefs(double attenuation, double transition)
        {
            TransitionParam(transition, out double k, out double q);
            double attnP2 = Math.Pow(10.0, -attenuation / 10);
            double a = attnP2 / (1 - attnP2);
            int order = (int)Math.Ceiling(Math.Log(a * a / 16) / Math.Log(q));
            if ((order & 1) == 0) order++;
            if (order == 1) order = 3;
            int n = (order - 1) / 2;
            var c = new double[n];
            for (int i = 0; i < n; i++)
            {
                int ci = i + 1;
                double num = AccNum(q, order, ci) * Math.Pow(q, 0.25);
                double den = AccDen(q, order, ci) + 0.5;
                double ww = num / den;
                double wwsq = ww * ww;
                double x = Math.Sqrt((1 - wwsq * k) * (1 - wwsq / k)) / (1 + wwsq);
                c[i] = (1 - x) / (1 + x);
            }
            return c;
        }

        static double AccNum(double q, int order, int c)
        {
            double acc = 0, term;
            int i = 0, j = 1;
            do
            {
                term = Math.Pow(q, i * (i + 1)) * Math.Sin((i * 2 + 1) * c * Math.PI / order) * j;
                acc += term;
                j = -j;
                i++;
            } while (Math.Abs(term) > 1e-100 && i < 100);
            return acc;
        }

        static double AccDen(double q, int order, int c)
        {
            double acc = 0, term;
            int i = 1, j = -1;
            do
            {
                term = Math.Pow(q, i * i) * Math.Cos(i * 2 * c * Math.PI / order) * j;
                acc += term;
                j = -j;
                i++;
            } while (Math.Abs(term) > 1e-100 && i < 100);
            return acc;
        }
    }

    /// <summary>2× IIR halfband resampler (one instance per channel per direction).</summary>
    internal sealed class IirHalfband
    {
        readonly float[] c, x, y;
        readonly int n;

        public IirHalfband(IirHalfbandDesign design)
        {
            n = design.Coefs.Length;
            c = new float[n];
            for (int i = 0; i < n; i++) c[i] = (float)design.Coefs[i];
            x = new float[n];
            y = new float[n];
        }

        public void Reset()
        {
            Array.Clear(x, 0, n);
            Array.Clear(y, 0, n);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Stages(ref float s0, ref float s1)
        {
            int i = 0;
            for (; i + 1 < n; i += 2)
            {
                float t0 = (s0 - y[i]) * c[i] + x[i];
                x[i] = s0; y[i] = t0; s0 = t0;
                float t1 = (s1 - y[i + 1]) * c[i + 1] + x[i + 1];
                x[i + 1] = s1; y[i + 1] = t1; s1 = t1;
            }
            if (i < n)
            {
                float t0 = (s0 - y[i]) * c[i] + x[i];
                x[i] = s0; y[i] = t0; s0 = t0;
            }
        }

        /// <summary>Two high-rate samples in (the earlier first), one out.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Down(float in0, float in1)
        {
            float s0 = in1, s1 = in0;
            Stages(ref s0, ref s1);
            return 0.5f * (s0 + s1);
        }

        /// <summary>One sample in, two high-rate samples out.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Up(float input, out float out0, out float out1)
        {
            float s0 = input, s1 = input;
            Stages(ref s0, ref s1);
            out0 = s0;
            out1 = s1;
        }

        /// <summary>Flushes denormal state (call once per block).</summary>
        public void Flush()
        {
            for (int i = 0; i < n; i++)
            {
                x[i] = DspMath.Flush(x[i]);
                y[i] = DspMath.Flush(y[i]);
            }
        }
    }
}
