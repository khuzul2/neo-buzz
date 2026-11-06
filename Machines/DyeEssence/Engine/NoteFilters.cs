#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.2): the per-note filters, run across notes. Every note of
// a patch runs the same model with its own cutoff and resonance, so eight notes are eight lanes of
// one filter: NeoBuzz.Dsp's ladder and state-variable filter (Filters.cs), the same operations in the
// same order, with each coefficient ramping sample by sample from one control tick's value to the
// next (no zipper as a cutoff moves); the SVF as one section or two alike (12 or 24 dB/oct); and a
// comb, positive or negative feedback, read between samples. Both channels in one loop. Buffers hold,
// per sample, one column per note (stride Notes); a group is the eight columns from 8·group.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    /// <summary>The filter types, in the parameters' order (append only).</summary>
    internal enum FilterType { Off, Ladder24, Ladder12, Lp12, Lp24, Bp12, Bp24, Hp12, Hp24, Notch, Peak, CombPlus, CombMinus }

    internal static class NoteFilters
    {
        public static bool IsLadder(FilterType t) => t == FilterType.Ladder24 || t == FilterType.Ladder12;
        public static bool IsComb(FilterType t) => t == FilterType.CombPlus || t == FilterType.CombMinus;
        public static bool IsSvf(FilterType t) => t >= FilterType.Lp12 && t <= FilterType.Peak;
        public static bool IsSteep(FilterType t) => t == FilterType.Lp24 || t == FilterType.Bp24 || t == FilterType.Hp24;

        public static SvfMode SvfModeOf(FilterType t) => t switch
        {
            FilterType.Lp12 or FilterType.Lp24 => SvfMode.LowPass,
            FilterType.Bp12 or FilterType.Bp24 => SvfMode.BandPass,
            FilterType.Hp12 or FilterType.Hp24 => SvfMode.HighPass,
            FilterType.Notch => SvfMode.Notch,
            _ => SvfMode.Peak,
        };

        /// <summary>Coefficient rows: the ladder's G, k, gain compensation, c1 … c4, g⁴, 1/(1 + k g⁴); the SVF's a1, a2, a3, m0, m1, m2; the comb's delay, feedback, output gain.</summary>
        public const int Rows = 9;

        /// <summary>A note's ladder coefficients, as NeoBuzz.Dsp.Ladder.Set computes them (g = tan(π f / fs), resonance 0 … 1).</summary>
        public static void Ladder(float[] c, int notes, int note, float g, float resonance)
        {
            float G = g / (1f + g);
            float k = 4.2f * DspMath.Clamp(resonance, 0f, 1f);
            float gg = G, g2 = gg * gg, om = 1f - gg;
            float g4 = g2 * g2;
            c[0 * notes + note] = G;
            c[1 * notes + note] = k;
            c[2 * notes + note] = 1f + 0.5f * k;
            c[3 * notes + note] = g2 * gg * om;
            c[4 * notes + note] = g2 * om;
            c[5 * notes + note] = gg * om;
            c[6 * notes + note] = om;
            c[7 * notes + note] = g4;
            c[8 * notes + note] = 1f / (1f + k * g4);
        }

        /// <summary>A note's SVF coefficients, as NeoBuzz.Dsp.Svf.SetFast computes them (kDamp = 1/Q).</summary>
        public static void Svf(float[] c, int notes, int note, SvfMode mode, float g, float kDamp)
        {
            float aa1 = 1f / (1f + g * (g + kDamp));
            float m0, m1, m2;
            switch (mode)
            {
                case SvfMode.LowPass: m0 = 0; m1 = 0; m2 = 1; break;
                case SvfMode.BandPass: m0 = 0; m1 = kDamp; m2 = 0; break;
                case SvfMode.HighPass: m0 = 1; m1 = -kDamp; m2 = -1; break;
                case SvfMode.Notch: m0 = 1; m1 = -kDamp; m2 = 0; break;
                case SvfMode.Peak: m0 = 1; m1 = -kDamp; m2 = -2; break;
                default: m0 = 1; m1 = -2 * kDamp; m2 = 0; break;
            }
            c[0 * notes + note] = aa1;
            c[1 * notes + note] = g * aa1;
            c[2 * notes + note] = g * g * aa1;
            c[3 * notes + note] = m0;
            c[4 * notes + note] = m1;
            c[5 * notes + note] = m2;
            for (int r = 6; r < Rows; r++) c[r * notes + note] = 0;
        }

        /// <summary>The comb's delay length in samples.</summary>
        public const int CombLength = 8192;
        public const int CombMask = CombLength - 1;

        /// <summary>
        /// A note's comb: a delay of one period of <paramref name="hz"/> (2 … length − 4 samples) and
        /// feedback ±0.99 · resonance; the output scaled by √(1 − fb²), so white noise keeps its power.
        /// </summary>
        public static void Comb(float[] c, int notes, int note, float hz, float rate, float resonance, bool negative)
        {
            float d = DspMath.Clamp(rate / MathF.Max(1f, hz), 2f, CombLength - 4);
            float fb = 0.99f * DspMath.Clamp(resonance, 0f, 1f);
            c[0 * notes + note] = d;
            c[1 * notes + note] = negative ? -fb : fb;
            c[2 * notes + note] = MathF.Sqrt(1f - fb * fb);
            for (int r = 3; r < Rows; r++) c[r * notes + note] = 0;
        }

        /// <summary>
        /// The ladder on a group, both channels, from <paramref name="aL"/> + <paramref name="mix"/> ·
        /// <paramref name="bL"/> (and right) to <paramref name="outL"/>/<paramref name="outR"/>, its
        /// coefficients <paramref name="cur"/> ramping by <paramref name="step"/> per sample. State: four
        /// stages per channel (rows 0–3 left, 4–7 right).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void RunLadder<TL, TF, TU>(float[] aL, float[] aR, float[] bL, float[] bR, float mix, float[] outL, float[] outR,
            int notes, int group, int count, float[] cur, float[] step, float[] state, float drive, bool lp12)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int c = 8 * group;
            TF G = TL.Load(cur, c), k = TL.Load(cur, notes + c), comp = TL.Load(cur, 2 * notes + c);
            TF c1 = TL.Load(cur, 3 * notes + c), c2 = TL.Load(cur, 4 * notes + c), c3 = TL.Load(cur, 5 * notes + c), c4 = TL.Load(cur, 6 * notes + c);
            TF g4 = TL.Load(cur, 7 * notes + c), invDen = TL.Load(cur, 8 * notes + c);
            TF dG = TL.Load(step, c), dk = TL.Load(step, notes + c), dcomp = TL.Load(step, 2 * notes + c);
            TF dc1 = TL.Load(step, 3 * notes + c), dc2 = TL.Load(step, 4 * notes + c), dc3 = TL.Load(step, 5 * notes + c), dc4 = TL.Load(step, 6 * notes + c);
            TF dg4 = TL.Load(step, 7 * notes + c), dinv = TL.Load(step, 8 * notes + c);
            TF l1 = TL.Load(state, c), l2 = TL.Load(state, notes + c), l3 = TL.Load(state, 2 * notes + c), l4 = TL.Load(state, 3 * notes + c);
            TF r1 = TL.Load(state, 4 * notes + c), r2 = TL.Load(state, 5 * notes + c), r3 = TL.Load(state, 6 * notes + c), r4 = TL.Load(state, 7 * notes + c);
            TF d = TL.Splat(drive > 1f ? drive * 0.5f : 0.5f);
            TF outScale = TL.Splat(drive > 1f ? 2f / drive : 2f);
            TF w = TL.Splat(mix);
            bool hasB = bL != null;
            for (int t = 0; t < count; t++)
            {
                int o = t * notes + c;
                TF xl = TL.Load(aL, o), xr = TL.Load(aR, o);
                if (hasB)
                {
                    xl = TL.Add(xl, TL.Mul(TL.Load(bL, o), w));
                    xr = TL.Add(xr, TL.Mul(TL.Load(bR, o), w));
                }
                TF yl = LadderStep<TL, TF, TU>(xl, G, k, comp, c1, c2, c3, c4, g4, invDen, d, ref l1, ref l2, ref l3, ref l4, lp12);
                TF yr = LadderStep<TL, TF, TU>(xr, G, k, comp, c1, c2, c3, c4, g4, invDen, d, ref r1, ref r2, ref r3, ref r4, lp12);
                TL.Store(TL.Mul(yl, outScale), outL, o);
                TL.Store(TL.Mul(yr, outScale), outR, o);
                G = TL.Add(G, dG); k = TL.Add(k, dk); comp = TL.Add(comp, dcomp);
                c1 = TL.Add(c1, dc1); c2 = TL.Add(c2, dc2); c3 = TL.Add(c3, dc3); c4 = TL.Add(c4, dc4);
                g4 = TL.Add(g4, dg4); invDen = TL.Add(invDen, dinv);
            }
            // The coefficients where the ramp got to: a tick split across blocks goes on from here.
            TL.Store(G, cur, c); TL.Store(k, cur, notes + c); TL.Store(comp, cur, 2 * notes + c);
            TL.Store(c1, cur, 3 * notes + c); TL.Store(c2, cur, 4 * notes + c); TL.Store(c3, cur, 5 * notes + c); TL.Store(c4, cur, 6 * notes + c);
            TL.Store(g4, cur, 7 * notes + c); TL.Store(invDen, cur, 8 * notes + c);
            TL.Store(l1, state, c); TL.Store(l2, state, notes + c); TL.Store(l3, state, 2 * notes + c); TL.Store(l4, state, 3 * notes + c);
            TL.Store(r1, state, 4 * notes + c); TL.Store(r2, state, 5 * notes + c); TL.Store(r3, state, 6 * notes + c); TL.Store(r4, state, 7 * notes + c);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF LadderStep<TL, TF, TU>(TF x, TF G, TF k, TF comp, TF c1, TF c2, TF c3, TF c4, TF g4, TF invDen, TF d,
            ref TF s1, ref TF s2, ref TF s3, ref TF s4, bool lp12)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF sigma = TL.Add(TL.Add(TL.Add(TL.Mul(c1, s1), TL.Mul(c2, s2)), TL.Mul(c3, s3)), TL.Mul(c4, s4));
            TF xin = TL.Mul(TL.Mul(x, comp), d);
            TF y4est = TL.Mul(TL.Add(TL.Mul(g4, xin), sigma), invDen);
            TF u = Tanh<TL, TF, TU>(TL.Sub(xin, TL.Mul(k, y4est)));
            TF v = TL.Mul(TL.Sub(u, s1), G); TF y1 = TL.Add(v, s1); s1 = TL.Add(y1, v);
            v = TL.Mul(TL.Sub(y1, s2), G); TF y2 = TL.Add(v, s2); s2 = TL.Add(y2, v);
            v = TL.Mul(TL.Sub(y2, s3), G); TF y3 = TL.Add(v, s3); s3 = TL.Add(y3, v);
            v = TL.Mul(TL.Sub(y3, s4), G); TF y4 = TL.Add(v, s4); s4 = TL.Add(y4, v);
            return lp12 ? y2 : y4;
        }

        /// <summary>DspMath.FastTanh per lane: the same rational, ±1 beyond ±4.97.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TF Tanh<TL, TF, TU>(TF x)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF x2 = TL.Mul(x, x);
            TF a = TL.Mul(x, TL.Add(TL.Splat(135135f), TL.Mul(x2, TL.Add(TL.Splat(17325f), TL.Mul(x2, TL.Add(TL.Splat(378f), x2))))));
            TF b = TL.Add(TL.Splat(135135f), TL.Mul(x2, TL.Add(TL.Splat(62370f), TL.Mul(x2, TL.Add(TL.Splat(3150f), TL.Mul(x2, TL.Splat(28f)))))));
            TF r = TL.Div(a, b);
            r = TL.Select(TL.Greater(x, TL.Splat(4.97f)), TL.Splat(1f), r);
            return TL.Select(TL.Greater(TL.Splat(-4.97f), x), TL.Splat(-1f), r);
        }

        /// <summary>
        /// The SVF on a group, both channels, one section or two alike (<paramref name="steep"/>): the
        /// input <paramref name="aL"/> + <paramref name="mix"/> · <paramref name="bL"/> (filter 2 hears its
        /// own sources and some of filter 1), pre-driven through tanh when <paramref name="drive"/> &gt; 1.
        /// State: ic1, ic2 per section and channel (rows 0–3 the first section, 4–7 the second).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void RunSvf<TL, TF, TU>(float[] aL, float[] aR, float[] bL, float[] bR, float mix, float[] outL, float[] outR,
            int notes, int group, int count, float[] cur, float[] step, float[] state, float drive, bool steep)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int c = 8 * group;
            TF a1 = TL.Load(cur, c), a2 = TL.Load(cur, notes + c), a3 = TL.Load(cur, 2 * notes + c);
            TF m0 = TL.Load(cur, 3 * notes + c), m1 = TL.Load(cur, 4 * notes + c), m2 = TL.Load(cur, 5 * notes + c);
            TF da1 = TL.Load(step, c), da2 = TL.Load(step, notes + c), da3 = TL.Load(step, 2 * notes + c);
            TF dm0 = TL.Load(step, 3 * notes + c), dm1 = TL.Load(step, 4 * notes + c), dm2 = TL.Load(step, 5 * notes + c);
            TF l1 = TL.Load(state, c), l2 = TL.Load(state, notes + c), r1 = TL.Load(state, 2 * notes + c), r2 = TL.Load(state, 3 * notes + c);
            TF l3 = TL.Load(state, 4 * notes + c), l4 = TL.Load(state, 5 * notes + c), r3 = TL.Load(state, 6 * notes + c), r4 = TL.Load(state, 7 * notes + c);
            TF w = TL.Splat(mix), two = TL.Splat(2f);
            bool driven = drive > 1.01f;
            TF dv = TL.Splat(drive), dvComp = TL.Splat(driven ? 1f / MathF.Sqrt(drive) : 1f);
            bool hasB = bL != null;
            for (int t = 0; t < count; t++)
            {
                int o = t * notes + c;
                TF xl = TL.Load(aL, o), xr = TL.Load(aR, o);
                if (hasB)
                {
                    xl = TL.Add(xl, TL.Mul(TL.Load(bL, o), w));
                    xr = TL.Add(xr, TL.Mul(TL.Load(bR, o), w));
                }
                if (driven)
                {
                    xl = TL.Mul(Tanh<TL, TF, TU>(TL.Mul(xl, dv)), dvComp);
                    xr = TL.Mul(Tanh<TL, TF, TU>(TL.Mul(xr, dv)), dvComp);
                }
                TF yl = SvfStep<TL, TF, TU>(xl, a1, a2, a3, m0, m1, m2, two, ref l1, ref l2);
                TF yr = SvfStep<TL, TF, TU>(xr, a1, a2, a3, m0, m1, m2, two, ref r1, ref r2);
                if (steep)
                {
                    yl = SvfStep<TL, TF, TU>(yl, a1, a2, a3, m0, m1, m2, two, ref l3, ref l4);
                    yr = SvfStep<TL, TF, TU>(yr, a1, a2, a3, m0, m1, m2, two, ref r3, ref r4);
                }
                TL.Store(yl, outL, o);
                TL.Store(yr, outR, o);
                a1 = TL.Add(a1, da1); a2 = TL.Add(a2, da2); a3 = TL.Add(a3, da3);
                m0 = TL.Add(m0, dm0); m1 = TL.Add(m1, dm1); m2 = TL.Add(m2, dm2);
            }
            TL.Store(a1, cur, c); TL.Store(a2, cur, notes + c); TL.Store(a3, cur, 2 * notes + c);
            TL.Store(m0, cur, 3 * notes + c); TL.Store(m1, cur, 4 * notes + c); TL.Store(m2, cur, 5 * notes + c);
            TL.Store(l1, state, c); TL.Store(l2, state, notes + c); TL.Store(r1, state, 2 * notes + c); TL.Store(r2, state, 3 * notes + c);
            TL.Store(l3, state, 4 * notes + c); TL.Store(l4, state, 5 * notes + c); TL.Store(r3, state, 6 * notes + c); TL.Store(r4, state, 7 * notes + c);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF SvfStep<TL, TF, TU>(TF v0, TF a1, TF a2, TF a3, TF m0, TF m1, TF m2, TF two, ref TF ic1, ref TF ic2)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF v3 = TL.Sub(v0, ic2);
            TF v1 = TL.Add(TL.Mul(a1, ic1), TL.Mul(a2, v3));
            TF v2 = TL.Add(TL.Add(ic2, TL.Mul(a2, ic1)), TL.Mul(a3, v3));
            ic1 = TL.Sub(TL.Mul(two, v1), ic1);
            ic2 = TL.Sub(TL.Mul(two, v2), ic2);
            return TL.Add(TL.Add(TL.Mul(m0, v0), TL.Mul(m1, v1)), TL.Mul(m2, v2));
        }

        /// <summary>
        /// The comb on a group, both channels: y = x + fb · y[n − d], d between samples (4-point cubic),
        /// the output y · gain. The delay lines: note n's at n · <see cref="CombLength"/> in
        /// <paramref name="lineL"/> and <paramref name="lineR"/>, written at <paramref name="write"/>
        /// (the position of this block's first sample, the same for every note).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void RunComb<TL, TF, TU>(float[] aL, float[] aR, float[] bL, float[] bR, float mix, float[] outL, float[] outR,
            int notes, int group, int count, float[] cur, float[] step, float[] lineL, float[] lineR, int write, float drive, float[] scratch)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int c = 8 * group;
            TF delay = TL.Load(cur, c), fb = TL.Load(cur, notes + c), gain = TL.Load(cur, 2 * notes + c);
            TF dd = TL.Load(step, c), dfb = TL.Load(step, notes + c), dgain = TL.Load(step, 2 * notes + c);
            TF w = TL.Splat(mix);
            TF half = TL.Splat(0.5f), oneHalf = TL.Splat(1.5f), two = TL.Splat(2f), twoHalf = TL.Splat(2.5f);
            bool driven = drive > 1.01f;
            TF dv = TL.Splat(drive), dvComp = TL.Splat(driven ? 1f / MathF.Sqrt(drive) : 1f);
            bool hasB = bL != null;
            for (int t = 0; t < count; t++)
            {
                int o = t * notes + c;
                TF xl = TL.Load(aL, o), xr = TL.Load(aR, o);
                if (hasB)
                {
                    xl = TL.Add(xl, TL.Mul(TL.Load(bL, o), w));
                    xr = TL.Add(xr, TL.Mul(TL.Load(bR, o), w));
                }
                if (driven)
                {
                    xl = TL.Mul(Tanh<TL, TF, TU>(TL.Mul(xl, dv)), dvComp);
                    xr = TL.Mul(Tanh<TL, TF, TU>(TL.Mul(xr, dv)), dvComp);
                }
                // Each lane's delay: its whole part and fraction (scalar, so the same everywhere), then
                // four points round it from each channel's line, the newest first.
                int wp = (write + t) & CombMask;
                TL.Store(delay, scratch, 0);
                for (int l = 0; l < 8; l++)
                {
                    float dl = scratch[l];
                    int di = (int)dl;
                    scratch[8 + l] = dl - di;
                    int baseAt = (c + l) * CombLength;
                    scratch[16 + l] = lineL[baseAt + ((wp - di + 1) & CombMask)];
                    scratch[24 + l] = lineL[baseAt + ((wp - di) & CombMask)];
                    scratch[32 + l] = lineL[baseAt + ((wp - di - 1) & CombMask)];
                    scratch[40 + l] = lineL[baseAt + ((wp - di - 2) & CombMask)];
                    scratch[48 + l] = lineR[baseAt + ((wp - di + 1) & CombMask)];
                    scratch[56 + l] = lineR[baseAt + ((wp - di) & CombMask)];
                    scratch[64 + l] = lineR[baseAt + ((wp - di - 1) & CombMask)];
                    scratch[72 + l] = lineR[baseAt + ((wp - di - 2) & CombMask)];
                }
                TF frac = TL.Load(scratch, 8);
                TF el = UnisonKernel.Cubic<TL, TF, TU>(TL.Load(scratch, 16), TL.Load(scratch, 24), TL.Load(scratch, 32), TL.Load(scratch, 40), frac, half, oneHalf, two, twoHalf);
                TF er = UnisonKernel.Cubic<TL, TF, TU>(TL.Load(scratch, 48), TL.Load(scratch, 56), TL.Load(scratch, 64), TL.Load(scratch, 72), frac, half, oneHalf, two, twoHalf);
                TF yl = TL.Add(xl, TL.Mul(fb, el));
                TF yr = TL.Add(xr, TL.Mul(fb, er));
                TL.Store(yl, scratch, 80);
                TL.Store(yr, scratch, 88);
                for (int l = 0; l < 8; l++)
                {
                    int at = (c + l) * CombLength + wp;
                    // Held to ±1000 and flushed: no setting runs away or leaves subnormals behind.
                    lineL[at] = DspMath.Flush(DspMath.Clamp(scratch[80 + l], -1000f, 1000f));
                    lineR[at] = DspMath.Flush(DspMath.Clamp(scratch[88 + l], -1000f, 1000f));
                }
                TL.Store(TL.Mul(yl, gain), outL, o);
                TL.Store(TL.Mul(yr, gain), outR, o);
                delay = TL.Add(delay, dd); fb = TL.Add(fb, dfb); gain = TL.Add(gain, dgain);
            }
            TL.Store(delay, cur, c); TL.Store(fb, cur, notes + c); TL.Store(gain, cur, 2 * notes + c);
        }


        /// <summary>A filter that passes its input (Off), filter 2's input mixed as the SVF's is.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void RunOff<TL, TF, TU>(float[] aL, float[] aR, float[] bL, float[] bR, float mix, float[] outL, float[] outR,
            int notes, int group, int count)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int c = 8 * group;
            TF w = TL.Splat(mix);
            bool hasB = bL != null;
            for (int t = 0; t < count; t++)
            {
                int o = t * notes + c;
                TF xl = TL.Load(aL, o), xr = TL.Load(aR, o);
                if (hasB)
                {
                    xl = TL.Add(xl, TL.Mul(TL.Load(bL, o), w));
                    xr = TL.Add(xr, TL.Mul(TL.Load(bR, o), w));
                }
                TL.Store(xl, outL, o);
                TL.Store(xr, outR, o);
            }
        }

        /// <summary>
        /// A group's notes into the output: per lane (f2 + <paramref name="f1Mix"/> · f1) × the note's
        /// gain (ramped: <paramref name="gain"/> at the note's column, its per-sample step at
        /// <paramref name="notes"/> + column), summed across lanes (eight samples at a time, each sum in
        /// the fixed order) and added to <paramref name="outL"/>/<paramref name="outR"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void MixOut<TL, TF, TU>(float[] f1L, float[] f1R, float[] f2L, float[] f2R, float f1Mix, float[] gain,
            int notes, int group, int count, float[] outL, float[] outR, int outOffset, float[] scratch)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int c = 8 * group;
            TF g = TL.Load(gain, c), dg = TL.Load(gain, notes + c), w = TL.Splat(f1Mix);
            int t = 0;
            for (; t + 8 <= count; t += 8)
            {
                // The gains of the eight samples, one step a sample, as the loop below adds them.
                TF g0 = g, g1 = TL.Add(g0, dg), g2 = TL.Add(g1, dg), g3 = TL.Add(g2, dg);
                TF g4 = TL.Add(g3, dg), g5 = TL.Add(g4, dg), g6 = TL.Add(g5, dg), g7 = TL.Add(g6, dg);
                TL.Store(TL.Sum8(Y<TL, TF, TU>(f1L, f2L, t, notes, c, w, g0), Y<TL, TF, TU>(f1L, f2L, t + 1, notes, c, w, g1),
                    Y<TL, TF, TU>(f1L, f2L, t + 2, notes, c, w, g2), Y<TL, TF, TU>(f1L, f2L, t + 3, notes, c, w, g3),
                    Y<TL, TF, TU>(f1L, f2L, t + 4, notes, c, w, g4), Y<TL, TF, TU>(f1L, f2L, t + 5, notes, c, w, g5),
                    Y<TL, TF, TU>(f1L, f2L, t + 6, notes, c, w, g6), Y<TL, TF, TU>(f1L, f2L, t + 7, notes, c, w, g7)), scratch, 0);
                TL.Store(TL.Sum8(Y<TL, TF, TU>(f1R, f2R, t, notes, c, w, g0), Y<TL, TF, TU>(f1R, f2R, t + 1, notes, c, w, g1),
                    Y<TL, TF, TU>(f1R, f2R, t + 2, notes, c, w, g2), Y<TL, TF, TU>(f1R, f2R, t + 3, notes, c, w, g3),
                    Y<TL, TF, TU>(f1R, f2R, t + 4, notes, c, w, g4), Y<TL, TF, TU>(f1R, f2R, t + 5, notes, c, w, g5),
                    Y<TL, TF, TU>(f1R, f2R, t + 6, notes, c, w, g6), Y<TL, TF, TU>(f1R, f2R, t + 7, notes, c, w, g7)), scratch, 8);
                for (int j = 0; j < 8; j++)
                {
                    outL[outOffset + t + j] += scratch[j];
                    outR[outOffset + t + j] += scratch[8 + j];
                }
                g = TL.Add(g7, dg);
            }
            for (; t < count; t++)
            {
                TF yl = Y<TL, TF, TU>(f1L, f2L, t, notes, c, w, g);
                TF yr = Y<TL, TF, TU>(f1R, f2R, t, notes, c, w, g);
                outL[outOffset + t] += TL.Sum(yl);
                outR[outOffset + t] += TL.Sum(yr);
                g = TL.Add(g, dg);
            }
            TL.Store(g, gain, c);
        }

        /// <summary>Sample t of a group: (f2 + w · f1) × g.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Y<TL, TF, TU>(float[] f1, float[] f2, int t, int notes, int c, TF w, TF g)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int o = t * notes + c;
            return TL.Mul(TL.Add(TL.Load(f2, o), TL.Mul(w, TL.Load(f1, o))), g);
        }

    }
}
