#nullable disable
// Neo Multiband (docs/specs/machines.md §5): the minimum-phase crossovers, zero latency. Each
// crossover is a Linkwitz–Riley split: a Butterworth section (Q 0.71) whose low and high outputs each
// pass a second one, so the low share is LP², the high HP², and LP² + HP² is the section's allpass.
// The bands are a cascade (band 1 = L1, band 2 = H1·L2, band 3 = H1·H2·L3, band 4 = H1·H2·H3), and the
// lower bands pass the higher crossovers' allpasses (band 1 those of 2 and 3, band 2 that of 3), so all
// four bands share one phase and sum to the three allpasses in a row: flat. Topology-preserving
// state-variable sections in double precision (NeoBuzz.Dsp SvfSection's update), one set per channel.
// The crossovers glide (a 20 ms one-pole in log frequency), their coefficients recomputed every 16
// samples while they move, in segments that run across blocks.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Multiband
{
    internal sealed class MinimumSplit
    {
        public const int Segment = 16;
        const double GlideSeconds = 0.02;
        const double K = 1.4142135623730951;   // 1/Q, Butterworth

        struct Section
        {
            public double S1, S2;
        }

        // Per crossover: g's coefficients.
        readonly double[] a1 = new double[3], a2 = new double[3], a3 = new double[3];
        readonly double[] logHz = new double[3], targetLogHz = new double[3];
        // Per channel: for each crossover the first section and the low and high second sections, then the allpasses
        // (band 1: at crossovers 2 and 3; band 2: at crossover 3).
        readonly Section[] s = new Section[2 * 12];
        readonly double sampleRate;
        readonly double glide;
        int segmentLeft;

        public MinimumSplit(double sampleRate, in Xover initial)
        {
            this.sampleRate = sampleRate;
            glide = 1 - PortableMath.Exp(-Segment / (GlideSeconds * sampleRate));
            for (int i = 0; i < 3; i++) logHz[i] = targetLogHz[i] = PortableMath.Log(initial[i]);
            for (int i = 0; i < 3; i++) Coefficients(i);
            Current = initial;
        }

        /// <summary>The crossovers the sections run at now (gliding to the target).</summary>
        public Xover Current { get; private set; }

        public void Reset()
        {
            Array.Clear(s);
            for (int i = 0; i < 3; i++) logHz[i] = targetLogHz[i];
            for (int i = 0; i < 3; i++) Coefficients(i);
            segmentLeft = 0;
        }

        public void SetTarget(in Xover x)
        {
            for (int i = 0; i < 3; i++) targetLogHz[i] = PortableMath.Log(x[i]);
        }

        /// <summary>Jumps to the target (a new song, a reset).</summary>
        public void Settle()
        {
            for (int i = 0; i < 3; i++) logHz[i] = targetLogHz[i];
            for (int i = 0; i < 3; i++) Coefficients(i);
        }

        void Coefficients(int i)
        {
            double g = MultibandDesign.Warp(PortableMath.Exp(logHz[i]), sampleRate);
            double b1 = 1.0 / (1.0 + g * (g + K));
            a1[i] = b1;
            a2[i] = g * b1;
            a3[i] = g * a2[i];
            var c = Current;
            if (i == 0) c.F1 = PortableMath.Exp(logHz[i]);
            else if (i == 1) c.F2 = PortableMath.Exp(logHz[i]);
            else c.F3 = PortableMath.Exp(logHz[i]);
            Current = c;
        }

        /// <summary>One sample through a section at crossover <paramref name="i"/>: its band (v1) and low (v2) outputs.</summary>
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
        double Lowpass(ref Section st, int i, double x)
        {
            Tick(ref st, i, x, out _, out double v2);
            return v2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double Highpass(ref Section st, int i, double x)
        {
            Tick(ref st, i, x, out double v1, out double v2);
            return x - K * v1 - v2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double Allpass(ref Section st, int i, double x)
        {
            Tick(ref st, i, x, out double v1, out _);
            return x - 2 * K * v1;
        }

        /// <summary>A Linkwitz–Riley split at crossover <paramref name="i"/>, sections from <paramref name="at"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Split(int at, int i, double x, out double low, out double high)
        {
            Tick(ref s[at], i, x, out double v1, out double v2);
            double hp = x - K * v1 - v2;
            low = Lowpass(ref s[at + 1], i, v2);
            high = Highpass(ref s[at + 2], i, hp);
        }

        /// <summary>
        /// Splits <paramref name="n"/> samples into four bands per channel: <paramref name="bandL"/>[b][i] and
        /// <paramref name="bandR"/>[b][i]. With three bands, bands 3 and 4 together are the third band.
        /// </summary>
        public void Process(float[] inL, float[] inR, int n, double[][] bandL, double[][] bandR)
        {
            for (int i = 0; i < n; i++)
            {
                if (segmentLeft == 0)
                {
                    segmentLeft = Segment;
                    for (int x = 0; x < 3; x++)
                    {
                        if (logHz[x] == targetLogHz[x]) continue;
                        double d = targetLogHz[x] - logHz[x];
                        logHz[x] = Math.Abs(d) < 1e-6 ? targetLogHz[x] : logHz[x] + d * glide;
                        Coefficients(x);
                    }
                }
                segmentLeft--;
                for (int c = 0; c < 2; c++)
                {
                    int at = c * 12;
                    double x = c == 0 ? inL[i] : inR[i];
                    Split(at + 0, 0, x, out double lo1, out double hi1);
                    Split(at + 3, 1, hi1, out double lo2, out double hi2);
                    Split(at + 6, 2, hi2, out double lo3, out double hi3);
                    double b1 = Allpass(ref s[at + 10], 2, Allpass(ref s[at + 9], 1, lo1));
                    double b2 = Allpass(ref s[at + 11], 2, lo2);
                    var bands = c == 0 ? bandL : bandR;
                    bands[0][i] = b1;
                    bands[1][i] = b2;
                    bands[2][i] = lo3;
                    bands[3][i] = hi3;
                }
            }
            for (int k = 0; k < s.Length; k++)
            {
                s[k].S1 = DspMath.Flush(s[k].S1);
                s[k].S2 = DspMath.Flush(s[k].S2);
            }
        }

        /// <summary>Every section's state is zero (after silence, once the tails are flushed).</summary>
        public bool AtRest
        {
            get
            {
                foreach (var st in s) if (st.S1 != 0 || st.S2 != 0) return false;
                return true;
            }
        }

        public bool HoldsSubnormals()
        {
            foreach (var st in s) if (double.IsSubnormal(st.S1) || double.IsSubnormal(st.S2)) return true;
            return false;
        }

        public bool HoldsNonFinite()
        {
            foreach (var st in s) if (!double.IsFinite(st.S1) || !double.IsFinite(st.S2)) return true;
            return false;
        }
    }
}
