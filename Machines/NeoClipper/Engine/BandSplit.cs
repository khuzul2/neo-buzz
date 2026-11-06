#nullable disable
// Neo Clipper (docs/specs/mastering-tools.md §5.1): the zero-latency split into two or three bands, Neo
// Multiband's minimum-phase crossovers (MinimumSplit) cut to two: each crossover a Linkwitz–Riley pair (a
// Butterworth section whose low and high outputs each pass a second one, LP² and HP², summing to the
// section's allpass). Two bands: L1 and H1. Three: L1 through crossover 2's allpass, H1·L2 and H1·H2, so the
// bands share one phase and sum flat. Topology-preserving state-variable sections in double; the crossovers
// glide (20 ms, one-pole in log frequency), their coefficients recomputed every 16 samples while they move.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Clipper
{
    internal sealed class BandSplit
    {
        public const int Segment = 16;
        const double GlideSeconds = 0.02;
        const double K = 1.4142135623730951;   // 1/Q, Butterworth

        struct Section
        {
            public double S1, S2;
        }

        readonly double[] a1 = new double[2], a2 = new double[2], a3 = new double[2];
        readonly double[] logHz = new double[2], target = new double[2];
        // Per channel: crossover 1 (first section, low, high), crossover 2 (the same), crossover 2's allpass on band 1.
        readonly Section[] s = new Section[2 * 7];
        readonly double sampleRate, glide;
        int segmentLeft;
        long count;

        public BandSplit(double sampleRate, double f1, double f2)
        {
            this.sampleRate = sampleRate;
            glide = 1 - PortableMath.Exp(-Segment / (GlideSeconds * sampleRate));
            SetTarget(f1, f2);
            Settle();
        }

        public void SetTarget(double f1, double f2)
        {
            target[0] = PortableMath.Log(f1);
            target[1] = PortableMath.Log(Math.Max(f1, f2));
        }

        public void Settle()
        {
            for (int i = 0; i < 2; i++)
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
            double b1 = 1.0 / (1.0 + g * (g + K));
            a1[i] = b1;
            a2[i] = g * b1;
            a3[i] = g * a2[i];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Tick(ref Section st, int i, double v0, out double v1, out double v2)
        {
            double v3 = v0 - st.S2;
            v1 = a1[i] * st.S1 + a2[i] * v3;
            v2 = st.S2 + a2[i] * st.S1 + a3[i] * v3;
            st.S1 = 2 * v1 - st.S1;
            st.S2 = 2 * v2 - st.S2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Split(int at, int i, double x, out double low, out double high)
        {
            Tick(ref s[at], i, x, out double v1, out double v2);
            double hp = x - K * v1 - v2;
            Tick(ref s[at + 1], i, v2, out _, out low);
            Tick(ref s[at + 2], i, hp, out double w1, out double w2);
            high = hp - K * w1 - w2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double Allpass(ref Section st, int i, double x)
        {
            Tick(ref st, i, x, out double v1, out _);
            return x - 2 * K * v1;
        }

        /// <summary>Splits channel <paramref name="c"/>'s <paramref name="n"/> samples into <paramref name="bands"/> (2 or 3) bands.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] left, float[] right, int n, int bands, float[][] outL, float[][] outR)
        {
            for (int i = 0; i < n; i++)
            {
                if (segmentLeft == 0)
                {
                    segmentLeft = Segment;
                    for (int x = 0; x < 2; x++)
                    {
                        if (logHz[x] == target[x]) continue;
                        double d = target[x] - logHz[x];
                        logHz[x] = Math.Abs(d) < 1e-6 ? target[x] : logHz[x] + d * glide;
                        Coefficients(x);
                    }
                }
                segmentLeft--;
                // State under 1e-30 flushed every 64 samples of its own count: the same bits whatever the blocks.
                if ((++count & 63) == 0)
                    for (int k = 0; k < s.Length; k++)
                    {
                        s[k].S1 = DspMath.Flush(s[k].S1);
                        s[k].S2 = DspMath.Flush(s[k].S2);
                    }
                for (int c = 0; c < 2; c++)
                {
                    int at = c * 7;
                    double x = c == 0 ? left[i] : right[i];
                    var o = c == 0 ? outL : outR;
                    Split(at, 0, x, out double lo1, out double hi1);
                    if (bands == 2)
                    {
                        o[0][i] = (float)lo1;
                        o[1][i] = (float)hi1;
                    }
                    else
                    {
                        Split(at + 3, 1, hi1, out double lo2, out double hi2);
                        o[0][i] = (float)Allpass(ref s[at + 6], 1, lo1);
                        o[1][i] = (float)lo2;
                        o[2][i] = (float)hi2;
                    }
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
