#nullable disable
// Neo machines shared DSP (docs/specs/mastering-tools.md §4.2, M4): the zero-latency split into two, three or four
// bands that Neo Transient's Impact mode and Neo Low End's minimum-phase option use. It is Neo Clipper's BandSplit
// and Neo Multiband's MinimumSplit generalised: each crossover a Linkwitz–Riley pair (a Butterworth section whose low
// and high outputs each pass a second one, LP² and HP², summing to the section's allpass), the bands a cascade
// (band 1 = L1, band 2 = H1·L2, band 3 = H1·H2·L3, band 4 = H1·H2·H3) and the lower bands passing the higher
// crossovers' allpasses, so every band shares one phase and the bands sum to the allpasses in a row: flat.
// Topology-preserving state-variable sections in double; the crossovers glide (20 ms, one-pole in log frequency),
// their coefficients recomputed every 16 samples while they move. State under 1e-30 is flushed every 64 samples of
// the split's own count, so the output doesn't depend on how the host splits the audio. Allocation-free after
// construction; the portable maths, the same bits on every platform.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class LrBands
    {
        public const int MaxBands = 4;
        public const int Segment = 16;
        const double GlideSeconds = 0.02;
        const double K = 1.4142135623730951;   // 1/Q, Butterworth
        const int PerChannel = 12;              // 3 crossovers × 3 sections, then three allpasses

        struct Section
        {
            public double S1, S2;
        }

        readonly double[] a1 = new double[3], a2 = new double[3], a3 = new double[3];
        readonly double[] logHz = new double[3], target = new double[3];
        readonly Section[] s = new Section[2 * PerChannel];
        readonly double sampleRate, glide;
        int segmentLeft;
        long count;

        public LrBands(double sampleRate, double f1, double f2, double f3)
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
        double Allpass(int at, int i, double x)
        {
            Tick(ref s[at], i, x, out double v1, out _);
            return x - 2 * K * v1;
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
                    double x = c == 0 ? left[i] : right[i];
                    var o = c == 0 ? outL : outR;
                    Split(at, 0, x, out double lo0, out double hi0);
                    if (bands == 2)
                    {
                        o[0][i] = (float)lo0;
                        o[1][i] = (float)hi0;
                        continue;
                    }
                    Split(at + 3, 1, hi0, out double lo1, out double hi1);
                    if (bands == 3)
                    {
                        o[0][i] = (float)Allpass(at + 9, 1, lo0);
                        o[1][i] = (float)lo1;
                        o[2][i] = (float)hi1;
                        continue;
                    }
                    Split(at + 6, 2, hi1, out double lo2, out double hi2);
                    o[0][i] = (float)Allpass(at + 10, 2, Allpass(at + 9, 1, lo0));
                    o[1][i] = (float)Allpass(at + 11, 2, lo1);
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
