#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.2): the per-note filters, run across notes. Every note of
// a patch runs the same filter model with its own cutoff and resonance, so eight notes are eight
// lanes of one filter: the ladder and the state-variable filter of NeoBuzz.Dsp (Filters.cs), with
// the same operations in the same order, per lane coefficients set each control tick, both channels
// in one loop. Buffers hold, per sample, one column per note (stride Notes); a group is the eight
// columns from 8·group.

using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    internal static class NoteFilters
    {
        /// <summary>Ladder coefficient rows (per note): G, k, gain compensation, c1 … c4, g⁴, 1/(1 + k g⁴).</summary>
        public const int LadderCoefs = 9;

        /// <summary>
        /// One note's ladder coefficients, as NeoBuzz.Dsp.Ladder.Set computes them: <paramref name="g"/> =
        /// tan(π f / fs), <paramref name="resonance"/> 0 … 1 (1 self-oscillates).
        /// </summary>
        public static void SetLadder(float[] coef, int notes, int note, float g, float resonance)
        {
            float G = g / (1f + g);
            float k = 4.2f * DspMath.Clamp(resonance, 0f, 1f);
            float gg = G, g2 = gg * gg, om = 1f - gg;
            float g4 = g2 * g2;
            coef[0 * notes + note] = G;
            coef[1 * notes + note] = k;
            coef[2 * notes + note] = 1f + 0.5f * k;
            coef[3 * notes + note] = g2 * gg * om;
            coef[4 * notes + note] = g2 * om;
            coef[5 * notes + note] = gg * om;
            coef[6 * notes + note] = om;
            coef[7 * notes + note] = g4;
            coef[8 * notes + note] = 1f / (1f + k * g4);
        }

        /// <summary>
        /// The ladder on a group, both channels, from <paramref name="inL"/>/<paramref name="inR"/> to
        /// <paramref name="outL"/>/<paramref name="outR"/>. State: four stages per channel (rows 0–3
        /// left, 4–7 right, stride <paramref name="notes"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Ladder<TL, TF, TU>(float[] inL, float[] inR, float[] outL, float[] outR, int notes, int group, int count,
            float[] coef, float[] state, float drive, bool lp12)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int c = 8 * group;
            TF G = TL.Load(coef, c), k = TL.Load(coef, notes + c), comp = TL.Load(coef, 2 * notes + c);
            TF c1 = TL.Load(coef, 3 * notes + c), c2 = TL.Load(coef, 4 * notes + c), c3 = TL.Load(coef, 5 * notes + c), c4 = TL.Load(coef, 6 * notes + c);
            TF g4 = TL.Load(coef, 7 * notes + c), invDen = TL.Load(coef, 8 * notes + c);
            TF l1 = TL.Load(state, c), l2 = TL.Load(state, notes + c), l3 = TL.Load(state, 2 * notes + c), l4 = TL.Load(state, 3 * notes + c);
            TF r1 = TL.Load(state, 4 * notes + c), r2 = TL.Load(state, 5 * notes + c), r3 = TL.Load(state, 6 * notes + c), r4 = TL.Load(state, 7 * notes + c);
            TF d = TL.Splat(drive > 1f ? drive * 0.5f : 0.5f);
            TF outScale = TL.Splat(drive > 1f ? 2f / drive : 2f);
            for (int t = 0; t < count; t++)
            {
                int o = t * notes + c;
                TF yl = Stage<TL, TF, TU>(TL.Load(inL, o), G, k, comp, c1, c2, c3, c4, g4, invDen, d, ref l1, ref l2, ref l3, ref l4, lp12);
                TF yr = Stage<TL, TF, TU>(TL.Load(inR, o), G, k, comp, c1, c2, c3, c4, g4, invDen, d, ref r1, ref r2, ref r3, ref r4, lp12);
                TL.Store(TL.Mul(yl, outScale), outL, o);
                TL.Store(TL.Mul(yr, outScale), outR, o);
            }
            TL.Store(l1, state, c); TL.Store(l2, state, notes + c); TL.Store(l3, state, 2 * notes + c); TL.Store(l4, state, 3 * notes + c);
            TL.Store(r1, state, 4 * notes + c); TL.Store(r2, state, 5 * notes + c); TL.Store(r3, state, 6 * notes + c); TL.Store(r4, state, 7 * notes + c);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Stage<TL, TF, TU>(TF x, TF G, TF k, TF comp, TF c1, TF c2, TF c3, TF c4, TF g4, TF invDen, TF d,
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
            TF lim = TL.Splat(4.97f), neg = TL.Splat(-4.97f);
            r = TL.Select(TL.Greater(x, lim), TL.Splat(1f), r);
            return TL.Select(TL.Greater(neg, x), TL.Splat(-1f), r);
        }

        /// <summary>State-variable filter coefficient rows (per note): a1, a2, a3, m0, m1, m2.</summary>
        public const int SvfCoefs = 6;

        /// <summary>One note's SVF coefficients, as NeoBuzz.Dsp.Svf.SetFast computes them.</summary>
        public static void SetSvf(float[] coef, int notes, int note, SvfMode mode, float g, float kDamp)
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
            coef[0 * notes + note] = aa1;
            coef[1 * notes + note] = g * aa1;
            coef[2 * notes + note] = g * g * aa1;
            coef[3 * notes + note] = m0;
            coef[4 * notes + note] = m1;
            coef[5 * notes + note] = m2;
        }

        /// <summary>
        /// The SVF on a group, both channels, its input crossfaded from <paramref name="aL"/> (0) to
        /// <paramref name="bL"/> (1) by <paramref name="mix"/>. State: ic1, ic2 left, then right.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Svf<TL, TF, TU>(float[] aL, float[] aR, float[] bL, float[] bR, float mix, float[] outL, float[] outR,
            int notes, int group, int count, float[] coef, float[] state)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int c = 8 * group;
            TF a1 = TL.Load(coef, c), a2 = TL.Load(coef, notes + c), a3 = TL.Load(coef, 2 * notes + c);
            TF m0 = TL.Load(coef, 3 * notes + c), m1 = TL.Load(coef, 4 * notes + c), m2 = TL.Load(coef, 5 * notes + c);
            TF l1 = TL.Load(state, c), l2 = TL.Load(state, notes + c), r1 = TL.Load(state, 2 * notes + c), r2 = TL.Load(state, 3 * notes + c);
            TF w = TL.Splat(mix), two = TL.Splat(2f);
            for (int t = 0; t < count; t++)
            {
                int o = t * notes + c;
                TF xa = TL.Load(aL, o);
                TF xl = TL.Add(xa, TL.Mul(TL.Sub(TL.Load(bL, o), xa), w));
                xa = TL.Load(aR, o);
                TF xr = TL.Add(xa, TL.Mul(TL.Sub(TL.Load(bR, o), xa), w));
                TL.Store(SvfStep<TL, TF, TU>(xl, a1, a2, a3, m0, m1, m2, two, ref l1, ref l2), outL, o);
                TL.Store(SvfStep<TL, TF, TU>(xr, a1, a2, a3, m0, m1, m2, two, ref r1, ref r2), outR, o);
            }
            TL.Store(l1, state, c); TL.Store(l2, state, notes + c); TL.Store(r1, state, 2 * notes + c); TL.Store(r2, state, 3 * notes + c);
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
        /// A group's notes into the output: per lane (f2 + <paramref name="f1Mix"/> · f1) × the note's
        /// gain (ramped: <paramref name="gain"/> at the note's column, its per-sample step at
        /// <paramref name="notes"/> + column), summed across lanes in the fixed order and added to
        /// <paramref name="outL"/>/<paramref name="outR"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void MixOut<TL, TF, TU>(float[] f1L, float[] f1R, float[] f2L, float[] f2R, float f1Mix, float[] gain,
            int notes, int group, int count, float[] outL, float[] outR, int outOffset)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int c = 8 * group;
            TF g = TL.Load(gain, c), dg = TL.Load(gain, notes + c), w = TL.Splat(f1Mix);
            for (int t = 0; t < count; t++)
            {
                int o = t * notes + c;
                TF yl = TL.Mul(TL.Add(TL.Load(f2L, o), TL.Mul(w, TL.Load(f1L, o))), g);
                TF yr = TL.Mul(TL.Add(TL.Load(f2R, o), TL.Mul(w, TL.Load(f1R, o))), g);
                outL[outOffset + t] += TL.Sum(yl);
                outR[outOffset + t] += TL.Sum(yr);
                g = TL.Add(g, dg);
            }
            TL.Store(g, gain, c);
        }
    }
}
