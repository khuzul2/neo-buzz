#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.3): the warps, per lane. Two in series per oscillator.
// Phase warps map the cycle's phase p ∈ [0, 1) through a curve before the table is read; PM adds
// another oscillator's output to the phase; FM moves the phase increment itself (linear, through
// zero); Window, AM and Ring shape the amplitude after the read. Every curve is built from IEEE
// basic operations (a rational bend, a parabolic sine), so it vectorises and gives the same bits
// everywhere. Speed is how much faster than the plain phase a warp can read through the cycle: the
// mip level allows for it, so the warped wave stays band-limited. Bend, Squeeze and Sync are edged:
// their read turns a corner (or jumps) at the cycle's wrap, which adds harmonics no mip level holds;
// High quality (4×) keeps those aliases further down (docs/specs/dye-essence.md §5.4). The others
// are smooth: Asym, Window sync, FM, PM, AM, Ring, and Mirror, which crossfades the plain read with
// a palindrome read (forward to the half and back) whose turns are smooth.

using NeoBuzz.Dsp;
using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.DyeEssence
{
    /// <summary>The warps, in the parameters' order (append only).</summary>
    internal enum Warp { None, BendPlus, BendMinus, Squeeze, Mirror, Asym, Sync, WindowSync, Fm, Pm, Am, Ring }

    internal static class Warps
    {
        /// <summary>
        /// Neo Learn's phase distortion (12): a warp the engine runs but Dye Essence's controls do not offer, so it is not a member of <see cref="Warp"/> (whose members
        /// Dye's tests and manifest enumerate: its edged warps are checked for their alias floors by name). A Learn machine turns it on through <c>OscSettings.PdOn</c>.
        /// </summary>
        public const Warp Pd = (Warp)12;

        /// <summary>FM's depth at full amount: the increment swings ±4 times its own (through zero).</summary>
        public const float FmDepth = 4f;
        /// <summary>PM's depth at full amount: the phase moves ±1 cycle for a full-scale modulator.</summary>
        public const float PmDepth = 1f;

        public static bool UsesModulator(Warp w) => w == Warp.Fm || w == Warp.Pm || w == Warp.Am || w == Warp.Ring;
        public static bool MapsPhase(Warp w) => w >= Warp.BendPlus && w <= Warp.WindowSync && w != Warp.Mirror || w == Pd;

        /// <summary>Bend's curve parameter: f(p) = p / (b + p (1 − b)); b = 2^(3a) for Bend+, its inverse for Bend−.</summary>
        public static float BendB(Warp w, float a)
        {
            float b = (float)NeoBuzz.Dsp.PortableMath.Exp2(3.0 * a);
            return w == Warp.BendMinus ? 1f / b : b;
        }

        /// <summary>
        /// How fast a warp at amount <paramref name="a"/> reads through the cycle against the plain
        /// phase. <paramref name="modRatio"/>: the modulator's frequency over the carrier's.
        /// </summary>
        public static float Speed(Warp w, float a, float modRatio)
        {
            switch (w)
            {
                case Warp.BendPlus:
                case Warp.BendMinus: { float b = BendB(w, a); return MathF.Max(b, 1f / b); }
                case Warp.Squeeze: return 1f / MathF.Max(0.04f, 1f - 0.96f * a);
                case Warp.Mirror: return a > 0 ? 1.6f : 1f;   // the palindrome read peaks at π/2 the plain speed
                case Warp.Asym: return 1f + 0.95f * a;
                case Warp.Sync:
                case Warp.WindowSync: return 1f + 7f * a;
                case Warp.Fm: return 1f + FmDepth * a + (a > 0 ? modRatio : 0f);   // Carson: the deviation plus the modulator
                case Warp.Pm: return 1f + 2f * MathF.PI * PmDepth * a * MathF.Max(1f, modRatio);
                case Warp.Am:
                case Warp.Ring: return a > 0 ? 1f + modRatio : 1f;
                case Pd: return 1f + (PdMaxSlope - 1f) * a;
                default: return 1f;
            }
        }

        /// <summary>
        /// Per-tick constants of a warp: k0 and k1 as each curve wants them (see <see cref="Apply"/>).
        /// </summary>
        public static void Constants(Warp w, float a, out float k0, out float k1)
        {
            k0 = k1 = 0;
            switch (w)
            {
                case Warp.BendPlus:
                case Warp.BendMinus: { float b = BendB(w, a); k0 = b; k1 = 1f - b; break; }
                case Warp.Squeeze: k0 = 1f / MathF.Max(0.04f, 1f - 0.96f * a); break;
                case Warp.Mirror: k0 = a; break;
                case Warp.Asym: k0 = 0.15f * a; break;
                case Warp.Sync:
                case Warp.WindowSync: k0 = 1f + 7f * a; break;
                case Warp.Pm: k0 = PmDepth * a; break;
                case Warp.Fm: k0 = FmDepth * a; break;
                case Warp.Am: k0 = a; k1 = 1f / (1f + a); break;
                case Warp.Ring: k0 = a; break;
                case Pd: k0 = a; break;   // k1 (the shape's row in the table) is set by the caller, which knows the shape
            }
        }

        /// <summary>A phase warp of p ∈ [0, 1); the result may need wrapping (Pm) but stays within [0, 1] otherwise.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TF Phase<TL, TF, TU>(Warp w, TF p, TF k0, TF k1, TF m)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            switch (w)
            {
                case Warp.BendPlus:
                case Warp.BendMinus:
                    return TL.Div(p, TL.Add(k0, TL.Mul(p, k1)));
                case Warp.Squeeze:
                {
                    TF q = TL.Mul(p, k0);
                    return TL.Select(TL.Greater(q, TL.Splat(0.99999994f)), TL.Splat(0.99999994f), q);
                }
                case Warp.Asym:
                    // p + 0.15 a sin(2π p), the sine parabolic and refined (shape, not a measurement).
                    return TL.Sub(p, TL.Mul(k0, SinPi<TL, TF, TU>(TL.Sub(TL.Mul(TL.Splat(2f), p), TL.Splat(1f)))));
                case Warp.Sync:
                case Warp.WindowSync:
                {
                    TF q = TL.Mul(p, k0);
                    return TL.Sub(q, TL.IntToFloat(TL.Truncate(q)));
                }
                case Warp.Pm:
                    return TL.Add(p, TL.Mul(k0, m));
                case Pd:
                {
                    // p + a (T(p) − p): T is the shape's phase-read geometry at full bend, in a 256-segment table (k1: its row's first index)
                    TF pos = TL.Mul(p, TL.Splat(256f));
                    TU i0 = TL.Truncate(pos);
                    TF x = TL.Sub(pos, TL.IntToFloat(i0));
                    TU idx = TL.AddU(i0, TL.Truncate(k1));
                    TF t0 = TL.Gather(PdTable, 0, idx), t1 = TL.Gather(PdTable, 0, TL.AddU(idx, TL.SplatU(1u)));
                    TF target = TL.Add(t0, TL.Mul(TL.Sub(t1, t0), x));
                    return TL.Add(p, TL.Mul(k0, TL.Sub(target, p)));
                }
                default:
                    return p;
            }
        }

        /// <summary>An amplitude warp of a read y (Window needs the plain phase p).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TF Amplitude<TL, TF, TU>(Warp w, TF y, TF p, TF k0, TF k1, TF m)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            switch (w)
            {
                case Warp.WindowSync:
                {
                    // sin²(π p): a Hann window over the cycle, so the synced restarts are silent.
                    TF s = SinPi<TL, TF, TU>(p);
                    return TL.Mul(y, TL.Mul(s, s));
                }
                case Warp.Am:
                    return TL.Mul(y, TL.Mul(TL.Add(TL.Splat(1f), TL.Mul(k0, m)), k1));
                case Warp.Ring:
                    return TL.Mul(y, TL.Add(TL.Sub(TL.Splat(1f), k0), TL.Mul(k0, m)));
                default:
                    return y;
            }
        }

        /// <summary>Neo Learn's phase distortion (L2b): the shapes (Saw, Square, Narrow Pulse, Double Sine, Saw-Pulse, Trapezoid), each a row of <see cref="PdRow"/> knots of the phase-read geometry T(p) at full bend.</summary>
        public const int PdShapes = 6, PdRow = 257;

        /// <summary>The steepest part of any shape's T (the phase runs this many times faster there): the mip level's guard.</summary>
        public const float PdMaxSlope = 16f;

        public static readonly float[] PdTable = BuildPd();

        static float[] BuildPd()
        {
            var t = new float[PdShapes * PdRow];
            for (int s = 0; s < PdShapes; s++)
                for (int i = 0; i < PdRow; i++) t[s * PdRow + i] = (float)PdTarget(s, i / 256.0);
            return t;
        }

        /// <summary>
        /// Shape <paramref name="shape"/>'s phase-read geometry at full bend, T(p) from 0 to 1 (Double Sine to 2): the cosine's phase runs where T says, so 0 % bend reads the
        /// wave evenly and 100 % reads part of the cycle much faster than the rest. Saw: half the cycle in the first 1/32 of the period; Square: a quarter cycle up, held, back down,
        /// held; Narrow Pulse: the whole cycle in the first tenth, then still; Double Sine: twice the cycle; Saw-Pulse: a saw's first half, a pulse's second; Trapezoid: the Square's
        /// edges stretched to a fifth of the period.
        /// </summary>
        public static double PdTarget(int shape, double p)
        {
            const double e = 8.0 / 256, wide = 52.0 / 256, q = 26.0 / 256;
            switch (shape)
            {
                case 0: return Saw(p, e);
                case 1: return Square(p, e);
                case 2: return Pulse(p, q);
                case 3: return 2 * p;
                case 4: return p < 0.5 ? 0.5 * Saw(2 * p, e) : 0.5 + 0.5 * Pulse(2 * p - 1, q);
                default: return Square(p, wide);
            }
        }

        static double Saw(double p, double m) => p < m ? 0.5 * p / m : 0.5 + 0.5 * (p - m) / (1 - m);

        static double Pulse(double p, double q) => p < q ? p / q : 1;

        static double Square(double p, double e)
        {
            if (p < e) return 0.25 * p / e;
            if (p < 0.5) return 0.25;
            if (p < 0.5 + e) return 0.25 + 0.5 * (p - 0.5) / e;
            if (p < 1 - e) return 0.75;
            return 0.75 + 0.25 * (p - (1 - e)) / e;
        }

        /// <summary>
        /// Neo Learn's wavefolder (L2a): <paramref name="stages"/> triangle folds in series, each of gain <paramref name="g"/> (1: the wave passes untouched
        /// while it stays inside ±1), the first after a DC offset <paramref name="bias"/> (the symmetry). One stage is y = 4 |frac(u/4 + 3/4) − 1/2| − 1 of
        /// u = g y: the identity inside ±1, then reflected at ±1, ±3, … So a wave that grows past the edge folds back instead of clipping, and every stage
        /// leaves the output in [−1, 1]. An input that is not finite becomes 0.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TF Fold<TL, TF, TU>(TF y, int stages, TF g, TF bias)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF zero = TL.Splat(0f), quarter = TL.Splat(0.25f), threeQuarter = TL.Splat(0.75f), half = TL.Splat(0.5f), four = TL.Splat(4f), one = TL.Splat(1f);
            TF lim = TL.Splat(64f), nlim = TL.Splat(-64f);
            y = TL.Add(y, bias);
            // NaN and infinities fail the comparison and become 0; the rest is kept in range for the truncation
            y = TL.Select(TL.Greater(TL.Splat(1e8f), TL.Mul(y, y)), y, zero);
            y = TL.Select(TL.Greater(y, lim), lim, TL.Select(TL.Greater(nlim, y), nlim, y));
            for (int s = 0; s < stages; s++)
            {
                TF v = Wrap<TL, TF, TU>(TL.Add(TL.Mul(TL.Mul(y, g), quarter), threeQuarter));
                TF d = TL.Sub(v, half);
                d = TL.Select(TL.Greater(zero, d), TL.Sub(zero, d), d);
                y = TL.Sub(TL.Mul(four, d), one);
            }
            return y;
        }

        /// <summary>
        /// The same fold with first-order antiderivative anti-aliasing at every stage (Neo Learn L2b): a stage does not read its input at one instant, it
        /// returns the average of the fold over the step the input just made (F(u) − F(u₀)) / (u − u₀), with F the fold's antiderivative (period 4:
        /// u²/2 inside ±1, 1 − (u − 2)²/2 from 1 to 3). A wave that folds many times in one sample therefore averages out instead of aliasing, and
        /// each stage hands the next a band-limited stream. <paramref name="prevU"/> and <paramref name="prevF"/> hold each stage's last input and F of it
        /// (stage s of the lanes at <paramref name="at"/> in rows s × <paramref name="total"/>). A step under 2e-3 uses the fold at the new input (the
        /// quotient would lose its digits; the two differ by under 1e-3). Inside ±1 a stage is the mean of two neighbouring inputs: half a sample of delay per stage, and a roll-off
        /// of under 1 dB at 10 kHz at 4x for six stages. Still bounded by [−1, 1] and finite.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static TF FoldAdaa<TL, TF, TU>(TF y, int stages, TF g, TF bias, float[] prevU, float[] prevF, int at, int total)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF zero = TL.Splat(0f), quarter = TL.Splat(0.25f), half = TL.Splat(0.5f), three = TL.Splat(3f), four = TL.Splat(4f), one = TL.Splat(1f);
            TF lim = TL.Splat(64f), nlim = TL.Splat(-64f), eps = TL.Splat(4e-6f);   // (2e-3)²
            y = TL.Add(y, bias);
            y = TL.Select(TL.Greater(TL.Splat(1e8f), TL.Mul(y, y)), y, zero);
            y = TL.Select(TL.Greater(y, lim), lim, TL.Select(TL.Greater(nlim, y), nlim, y));
            for (int s = 0; s < stages; s++)
            {
                TF u = TL.Mul(y, g);
                TF u0 = TL.Load(prevU, s * total + at), f0 = TL.Load(prevF, s * total + at);
                // p = the input's place in the fold's period of 4 (0 … 1); lower = the rising leg (the fold is a), else the falling leg (-a)
                TF p = Wrap<TL, TF, TU>(TL.Mul(TL.Add(u, one), quarter));
                TF lower = TL.Greater(half, p);
                TF a = TL.Sub(TL.Mul(four, p), TL.Select(lower, one, three));
                TF a2 = TL.Mul(half, TL.Mul(a, a));
                TF f1 = TL.Select(lower, a2, TL.Sub(one, a2));
                TF du = TL.Sub(u, u0);
                TF small = TL.Greater(eps, TL.Mul(du, du));
                TF quot = TL.Div(TL.Sub(f1, f0), TL.Select(small, one, du));
                TL.Store(u, prevU, s * total + at);
                TL.Store(f1, prevF, s * total + at);
                // a step too small for the quotient's digits takes the fold at the new input (within 1e-3 of the average)
                y = TL.Select(small, TL.Select(lower, a, TL.Sub(zero, a)), quot);
            }
            // rounding in the quotient can pass 1 by a hair
            y = TL.Select(TL.Greater(y, one), one, TL.Select(TL.Greater(TL.Splat(-1f), y), TL.Splat(-1f), y));
            return y;
        }

        /// <summary>
        /// Fold bandwidth: the folds' total gain times the lane's frequency (as a fraction of the oversampled rate) at which the spectrum a fold makes still
        /// fits under the oversampled Nyquist with room for its decay (measured: alias under −40 dB against the loudest harmonic).
        /// </summary>
        public const double FoldBandwidth = 0.12;

        /// <summary>
        /// The stage gain <paramref name="g"/> of a fold of <paramref name="stages"/> stages, lowered (all stages alike) where g^stages × the pitch
        /// (<paramref name="inc"/>, a phase step in units of 2^−32 cycles, FM's swing included) would pass <see cref="FoldBandwidth"/>. Never below 1, never above g.
        /// Continuous in both, so neither a drive sweep nor a glide jumps.
        /// </summary>
        public static float FoldLimit(float g, int stages, double inc)
        {
            if (stages <= 0 || g <= 1f) return 1f;
            double cap = FoldBandwidth * 4294967296.0 / Math.Max(1.0, inc);   // the total gain this pitch allows
            double total = 1.0;
            for (int s = 0; s < stages; s++) total *= g;
            if (total <= cap) return g;
            return (float)Math.Max(1.0, PortableMath.Pow(cap, 1.0 / stages));
        }

        /// <summary>How much faster than the plain wave a fold of <paramref name="stages"/> at stage gain <paramref name="g"/> reads through its table (the bandwidth it adds), for the mip level.</summary>
        public static float FoldSpeed(int stages, float g)
        {
            double total = 1.0;
            for (int s = 0; s < stages; s++) total *= Math.Max(1f, g);
            return (float)Math.Min(1.0 + 2.0 * (total - 1.0), 48.0);
        }

        /// <summary>
        /// sin(π x) for x ∈ [−1, 1]: the parabola 4x(1 − |x|) refined once (within 0.001): a shape,
        /// from basic operations only.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TF SinPi<TL, TF, TU>(TF x)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF ax = TL.Select(TL.Greater(TL.Splat(0f), x), TL.Sub(TL.Splat(0f), x), x);
            TF s = TL.Mul(TL.Mul(TL.Splat(4f), x), TL.Sub(TL.Splat(1f), ax));
            TF az = TL.Select(TL.Greater(TL.Splat(0f), s), TL.Sub(TL.Splat(0f), s), s);
            return TL.Add(TL.Mul(TL.Splat(0.225f), TL.Sub(TL.Mul(s, az), s)), s);
        }

        /// <summary>p − floor(p): the phase back into [0, 1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TF Wrap<TL, TF, TU>(TF p)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF t = TL.IntToFloat(TL.Truncate(p));
            TF f = TL.Sub(p, t);
            // Below zero, truncation went up: one more down. A tiny negative plus one rounds to 1: 0.
            f = TL.Select(TL.Greater(TL.Splat(0f), f), TL.Add(f, TL.Splat(1f)), f);
            return TL.Select(TL.Greater(f, TL.Splat(0.99999994f)), TL.Splat(0f), f);
        }

        /// <summary>
        /// A phase kept to [0, 1 − 2⁻²⁴] for a read (a curve may land on 1 by rounding); anything
        /// not above zero, NaN included, reads at 0, so a read never leaves its table.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TF Clamp<TL, TF, TU>(TF p)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF top = TL.Splat(0.99999994f);
            return TL.Select(TL.Greater(p, TL.Splat(0f)), TL.Select(TL.Greater(p, top), top, p), TL.Splat(0f));
        }
    }
}
