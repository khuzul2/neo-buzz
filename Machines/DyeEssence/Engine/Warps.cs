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

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.DyeEssence
{
    /// <summary>The warps, in the parameters' order (append only).</summary>
    internal enum Warp { None, BendPlus, BendMinus, Squeeze, Mirror, Asym, Sync, WindowSync, Fm, Pm, Am, Ring }

    internal static class Warps
    {
        /// <summary>FM's depth at full amount: the increment swings ±4 times its own (through zero).</summary>
        public const float FmDepth = 4f;
        /// <summary>PM's depth at full amount: the phase moves ±1 cycle for a full-scale modulator.</summary>
        public const float PmDepth = 1f;

        public static bool UsesModulator(Warp w) => w == Warp.Fm || w == Warp.Pm || w == Warp.Am || w == Warp.Ring;
        public static bool MapsPhase(Warp w) => w >= Warp.BendPlus && w <= Warp.WindowSync && w != Warp.Mirror;

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
