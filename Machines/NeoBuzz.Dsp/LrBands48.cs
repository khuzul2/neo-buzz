#nullable disable
// Neo machines shared DSP (docs/specs/neo-split.md, SPL1): the 48 dB/oct sibling of LrBands. The same cascade (band 1 = L1,
// band 2 = H1·L2, band 3 = H1·H2·L3, band 4 = H1·H2·H3, the lower bands passing the higher crossovers' allpasses) with each
// crossover a Linkwitz–Riley pair of the 8th order: a 4th-order Butterworth low-pass (or high-pass) run twice, its two sections
// of Q 1.3066 and 0.5412 (their 1/Q are 2 sin(π/8) and 2 sin(3π/8)). With B the 4th-order Butterworth denominator,
// LP² + HP² = (1 + s⁸)/B(s)² = B(−s)/B(s): the sum of a pair is the 4th-order Butterworth allpass, so the bands sum to the allpasses
// in a row, flat in magnitude at 48 dB/oct, each band −6 dB at its crossover (Linkwitz and Riley, JAES 1976). Topology-preserving
// state-variable sections in double (seven per crossover and channel, the first one giving both the low and the high of its stage);
// the allpasses of the lower bands are two sections each. The crossovers glide (20 ms, one-pole in log frequency), their
// coefficients recomputed every 16 samples while they move. State under 1e-30 is flushed every 64 samples of the split's own
// count, so the output does not depend on how the host splits the audio. Allocation-free after construction; the portable maths.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class LrBands48
    {
        public const int MaxBands = 4;
        public const int Segment = 16;
        const double GlideSeconds = 0.02;
        const double KA = 0.7653668647301796;   // 1/Q of the first 4th-order Butterworth section: 2 sin(π/8)
        const double KB = 1.8477590650225735;   // 1/Q of the second: 2 sin(3π/8)
        const int PerCrossover = 7;
        const int PerChannel = 3 * PerCrossover + 6;     // three crossovers, then three allpasses of two sections

        struct Section
        {
            public double S1, S2;
        }

        // Per crossover (0 … 2) and section kind (0: KA, 1: KB): the coefficients.
        readonly double[] a1 = new double[6], a2 = new double[6], a3 = new double[6];
        readonly double[] logHz = new double[3], target = new double[3];
        readonly Section[] s = new Section[2 * PerChannel];
        readonly double sampleRate, glide;
        int segmentLeft;
        long count;

        public LrBands48(double sampleRate, double f1, double f2, double f3)
        {
            this.sampleRate = sampleRate;
            glide = 1 - PortableMath.Exp(-Segment / (GlideSeconds * sampleRate));
            SetTarget(f1, f2, f3);
            Settle();
        }

        /// <summary>The crossovers in Hz, each no lower than the one before.</summary>
        public void SetTarget(double f1, double f2, double f3)
        {
            target[0] = PortableMath.Log(f1);
            target[1] = PortableMath.Log(Math.Max(f1, f2));
            target[2] = PortableMath.Log(Math.Max(Math.Max(f1, f2), f3));
        }

        /// <summary>The crossovers at their targets now, no glide.</summary>
        public void Settle()
        {
            for (int i = 0; i < 3; i++)
            {
                logHz[i] = target[i];
                Coefficients(i);
            }
        }

        public void Reset()
        {
            Array.Clear(s);
            Settle();
            segmentLeft = 0;
            count = 0;
        }

        void Coefficients(int i)
        {
            double hz = Math.Clamp(PortableMath.Exp(logHz[i]), 10, 0.49 * sampleRate);
            double g = PortableMath.Tan(Math.PI * hz / sampleRate);
            for (int j = 0; j < 2; j++)
            {
                double k = j == 0 ? KA : KB;
                double b1 = 1.0 / (1.0 + g * (g + k));
                a1[2 * i + j] = b1;
                a2[2 * i + j] = g * b1;
                a3[2 * i + j] = g * a2[2 * i + j];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Tick(ref Section st, int c, double v0, out double v1, out double v2)
        {
            double v3 = v0 - st.S2;
            v1 = a1[c] * st.S1 + a2[c] * v3;
            v2 = st.S2 + a2[c] * st.S1 + a3[c] * v3;
            st.S1 = 2 * v1 - st.S1;
            st.S2 = 2 * v2 - st.S2;
        }

        // One crossover: stage 1 (KA) from the input gives the low and the high of that stage; stages 2, 3 and 4 (KB, KA, KB) run on each.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Split(int at, int i, double x, out double low, out double high)
        {
            int ca = 2 * i, cb = 2 * i + 1;
            Tick(ref s[at], ca, x, out double v1, out double v2);
            double lo = v2, hi = x - KA * v1 - v2;
            Tick(ref s[at + 1], cb, lo, out _, out lo);
            Tick(ref s[at + 4], cb, hi, out double w1, out double w2);
            hi = hi - KB * w1 - w2;
            Tick(ref s[at + 2], ca, lo, out _, out lo);
            Tick(ref s[at + 5], ca, hi, out w1, out w2);
            hi = hi - KA * w1 - w2;
            Tick(ref s[at + 3], cb, lo, out _, out lo);
            Tick(ref s[at + 6], cb, hi, out w1, out w2);
            low = lo;
            high = hi - KB * w1 - w2;
        }

        // The 4th-order Butterworth allpass of crossover i: two sections, x − 2 (1/Q) v1 each.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double Allpass(int at, int i, double x)
        {
            Tick(ref s[at], 2 * i, x, out double v1, out _);
            x -= 2 * KA * v1;
            Tick(ref s[at + 1], 2 * i + 1, x, out v1, out _);
            return x - 2 * KB * v1;
        }

        /// <summary>Splits <paramref name="n"/> samples of both channels into <paramref name="bands"/> (2 … 4) bands: outL[band][i], outR[band][i].</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] left, float[] right, int n, int bands, float[][] outL, float[][] outR)
        {
            for (int i = 0; i < n; i++)
            {
                if (segmentLeft == 0)
                {
                    segmentLeft = Segment;
                    for (int x = 0; x < 3; x++)
                    {
                        if (logHz[x] == target[x]) continue;
                        double d = target[x] - logHz[x];
                        logHz[x] = Math.Abs(d) < 1e-6 ? target[x] : logHz[x] + d * glide;
                        Coefficients(x);
                    }
                }
                segmentLeft--;
                if ((++count & 63) == 0)
                    for (int k = 0; k < s.Length; k++)
                    {
                        s[k].S1 = DspMath.Flush(s[k].S1);
                        s[k].S2 = DspMath.Flush(s[k].S2);
                    }
                for (int c = 0; c < 2; c++)
                {
                    int at = c * PerChannel;
                    int ap = at + 3 * PerCrossover;
                    double x = c == 0 ? left[i] : right[i];
                    var o = c == 0 ? outL : outR;
                    Split(at, 0, x, out double lo0, out double hi0);
                    if (bands == 2)
                    {
                        o[0][i] = (float)lo0;
                        o[1][i] = (float)hi0;
                        continue;
                    }
                    Split(at + PerCrossover, 1, hi0, out double lo1, out double hi1);
                    if (bands == 3)
                    {
                        o[0][i] = (float)Allpass(ap, 1, lo0);
                        o[1][i] = (float)lo1;
                        o[2][i] = (float)hi1;
                        continue;
                    }
                    Split(at + 2 * PerCrossover, 2, hi1, out double lo2, out double hi2);
                    o[0][i] = (float)Allpass(ap + 2, 2, Allpass(ap, 1, lo0));
                    o[1][i] = (float)Allpass(ap + 4, 2, lo1);
                    o[2][i] = (float)lo2;
                    o[3][i] = (float)hi2;
                }
            }
        }

        public bool HoldsSubnormals()
        {
            foreach (var st in s) if (double.IsSubnormal(st.S1) || double.IsSubnormal(st.S2)) return true;
            return false;
        }
    }
}
