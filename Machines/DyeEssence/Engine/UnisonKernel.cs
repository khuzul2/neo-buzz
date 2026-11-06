#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.1): the oscillator kernels. A note's oscillators render
// as lanes in groups of eight: an oscillator's unison voices are lanes, and lanes of oscillators
// with no warp share groups whatever their tables, levels and frames (a patch of three single
// voices is one group, not three). Per lane: a 32-bit phase (exact, the wrap free), the read index
// and its fraction by shifts at the lane's own mip level, four points of each frame of the pair as
// one 16-byte load, the morph between them, then the 4-point cubic in NeoBuzz.Dsp.Wavetable's order.
// A warped oscillator has groups of its own: its phase goes through the two warps in float (24-bit),
// with another oscillator's lane, the sub or the noise as the modulator for FM, PM, AM and Ring.
// Each lane's gains (to filter 1 and filter 2, left and right) ramp across the tick into the note's
// accumulators; the note sums them across lanes afterwards, eight samples at a time.

using System.Runtime.CompilerServices;

namespace NeoBuzz.DyeEssence
{
    /// <summary>Every lane of every note (structure of arrays; a note's lanes from note × MaxLanes).</summary>
    internal sealed class LaneState
    {
        public const int GainRows = 8;   // to F1 left, right; to F2 left, right; then their per-sample steps

        public readonly int Total;
        public readonly uint[] Phase, Inc, ShiftR, ShiftL, ModSlot;
        public readonly float[] FrameMix;
        public readonly int[] OffsetA, OffsetB;
        public readonly float[][] Data;
        public readonly float[] Gain;

        public LaneState(int total)
        {
            Total = total;
            Phase = new uint[total];
            Inc = new uint[total];
            ShiftR = new uint[total];
            ShiftL = new uint[total];
            ModSlot = new uint[total];
            FrameMix = new float[total];
            OffsetA = new int[total];
            OffsetB = new int[total];
            Data = new float[total][];
            Gain = new float[GainRows * total];
        }
    }

    /// <summary>A warped group's settings for one tick: its two warps and their constants.</summary>
    internal struct WarpSet
    {
        public Warp W1, W2;
        public float K10, K11, K20, K21;
        public float Fm;                 // FM depth (both warps' FM amounts)
        public bool UsesMod, MapsPhase, Amplitude;
        /// <summary>Mirror: a second read, forward to the half and back with smooth turns, crossfaded in by <see cref="MirrorMix"/>.</summary>
        public bool Mirror;
        public float MirrorMix;
        /// <summary>After the first warp, after the second: back into [0, 1) (PM leaves it), else held to it.</summary>
        public bool Wrap1, Wrap2;
        public int Log2Len;
        /// <summary>The modulator: 0 another oscillator's lanes (ModSlot), 1 the sub, 2 the noise.</summary>
        public int ModKind;
    }

    internal static class UnisonKernel
    {
        /// <summary>Accumulator layout: per sample, 8 lanes each of F1 left, F1 right, F2 left, F2 right.</summary>
        public const int AccStride = 32;

        const float FracScale = 1f / 16777216f;   // 2^-24

        static void Refs(LaneState s, int at, bool b, ref LaneRefs r)
        {
            var off = b ? s.OffsetB : s.OffsetA;
            for (int l = 0; l < 8; l++) r.Set(l, s.Data[at + l], off[at + l]);
        }

        /// <summary>
        /// Eight plain lanes from <paramref name="at"/> in <paramref name="s"/>, <paramref name="count"/>
        /// samples into <paramref name="acc"/>: each lane at its own level (ShiftR = 32 − log2 length,
        /// ShiftL = log2 length), frame pair and mix. <paramref name="morph"/>: some lane reads two
        /// frames; <paramref name="toF2"/>: some lane sends to filter 2; <paramref name="mod"/>: the
        /// lanes' outputs are kept (from <paramref name="modAt"/>, stride <paramref name="modStride"/>)
        /// for oscillators they modulate.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Plain<TL, TF, TU>(LaneState s, int at, float[] acc, int count, bool morph, bool toF2,
            float[] mod, int modAt, int modStride)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var ra = new LaneRefs();
            var rb = new LaneRefs();
            Refs(s, at, false, ref ra);
            Refs(s, at, morph, ref rb);
            int total = s.Total;
            var g = s.Gain;
            TU ph = TL.LoadU(s.Phase, at), dph = TL.LoadU(s.Inc, at);
            TU shr = TL.LoadU(s.ShiftR, at), shl = TL.LoadU(s.ShiftL, at);
            TF ff = TL.Load(s.FrameMix, at);
            TF g1l = TL.Load(g, at), g1r = TL.Load(g, total + at), g2l = TL.Load(g, 2 * total + at), g2r = TL.Load(g, 3 * total + at);
            TF d1l = TL.Load(g, 4 * total + at), d1r = TL.Load(g, 5 * total + at), d2l = TL.Load(g, 6 * total + at), d2r = TL.Load(g, 7 * total + at);
            TF half = TL.Splat(0.5f), oneHalf = TL.Splat(1.5f), two = TL.Splat(2f), twoHalf = TL.Splat(2.5f);
            TF fracScale = TL.Splat(FracScale);
            for (int t = 0; t < count; t++)
            {
                TU idx = TL.ShiftRightVar(ph, shr);
                TF x = TL.Mul(TL.ToFloat(TL.ShiftRight(TL.ShiftLeftVar(ph, shl), 8)), fracScale);
                TL.Read4(in ra, idx, out TF y0, out TF y1, out TF y2, out TF y3);
                if (morph)
                {
                    TL.Read4(in rb, idx, out TF b0, out TF b1, out TF b2, out TF b3);
                    y0 = TL.Add(y0, TL.Mul(TL.Sub(b0, y0), ff));
                    y1 = TL.Add(y1, TL.Mul(TL.Sub(b1, y1), ff));
                    y2 = TL.Add(y2, TL.Mul(TL.Sub(b2, y2), ff));
                    y3 = TL.Add(y3, TL.Mul(TL.Sub(b3, y3), ff));
                }
                TF y = Cubic<TL, TF, TU>(y0, y1, y2, y3, x, half, oneHalf, two, twoHalf);
                if (mod != null) TL.Store(y, mod, modAt + t * modStride);
                int o = t * AccStride;
                TL.Store(TL.Add(TL.Load(acc, o), TL.Mul(y, g1l)), acc, o);
                TL.Store(TL.Add(TL.Load(acc, o + 8), TL.Mul(y, g1r)), acc, o + 8);
                g1l = TL.Add(g1l, d1l);
                g1r = TL.Add(g1r, d1r);
                if (toF2)
                {
                    TL.Store(TL.Add(TL.Load(acc, o + 16), TL.Mul(y, g2l)), acc, o + 16);
                    TL.Store(TL.Add(TL.Load(acc, o + 24), TL.Mul(y, g2r)), acc, o + 24);
                    g2l = TL.Add(g2l, d2l);
                    g2r = TL.Add(g2r, d2r);
                }
                ph = TL.AddU(ph, dph);
            }
            TL.StoreU(ph, s.Phase, at);
            TL.Store(g1l, g, at);
            TL.Store(g1r, g, total + at);
            if (toF2)
            {
                TL.Store(g2l, g, 2 * total + at);
                TL.Store(g2r, g, 3 * total + at);
            }
        }

        /// <summary>
        /// Eight lanes of one warped oscillator (one level for all, <see cref="WarpSet.Log2Len"/>): the
        /// phase through the warps in float, then the read. Its modulator per sample: another
        /// oscillator's lanes from <paramref name="mod"/> (each lane's ModSlot, stride
        /// <paramref name="modStride"/>), or the note's sub or noise from <paramref name="modNote"/>
        /// (stride <paramref name="noteStride"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Warped<TL, TF, TU>(LaneState s, int at, float[] acc, int count, bool morph, bool toF2,
            in WarpSet w, float[] mod, int modAt, int modStride, float[] modNote, int noteAt, int noteStride, float[] store, int storeAt)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var ra = new LaneRefs();
            var rb = new LaneRefs();
            Refs(s, at, false, ref ra);
            Refs(s, at, morph, ref rb);
            int total = s.Total;
            var g = s.Gain;
            TU ph = TL.LoadU(s.Phase, at), inc = TL.LoadU(s.Inc, at);
            TU modSlot = TL.LoadU(s.ModSlot, at);
            TF incF = TL.ToFloat(TL.ShiftRight(inc, 8));   // the increment in units of 2^8, exact
            TF ff = TL.Load(s.FrameMix, at);
            TF g1l = TL.Load(g, at), g1r = TL.Load(g, total + at), g2l = TL.Load(g, 2 * total + at), g2r = TL.Load(g, 3 * total + at);
            TF d1l = TL.Load(g, 4 * total + at), d1r = TL.Load(g, 5 * total + at), d2l = TL.Load(g, 6 * total + at), d2r = TL.Load(g, 7 * total + at);
            TF half = TL.Splat(0.5f), oneHalf = TL.Splat(1.5f), two = TL.Splat(2f), twoHalf = TL.Splat(2.5f);
            TF fracScale = TL.Splat(FracScale);
            TF len = TL.Splat(1 << w.Log2Len);
            TF k10 = TL.Splat(w.K10), k11 = TL.Splat(w.K11), k20 = TL.Splat(w.K20), k21 = TL.Splat(w.K21);
            TF fm = TL.Splat(w.Fm), fmLimit = TL.Splat(8e6f), fmFloor = TL.Splat(-8e6f), zero = TL.Splat(0f);
            TF mirror = TL.Splat(w.MirrorMix);
            for (int t = 0; t < count; t++)
            {
                TF m = zero;
                if (w.UsesMod)
                    m = w.ModKind == 0 ? TL.Gather(mod, modAt + t * modStride, modSlot) : TL.Splat(modNote[noteAt + t * noteStride]);
                TF p = TL.Mul(TL.ToFloat(TL.ShiftRight(ph, 8)), fracScale);
                TF q = p;
                if (w.MapsPhase)
                {
                    // Wrapped straight after PM, so the second warp always sees a phase in [0, 1).
                    q = Warps.Phase<TL, TF, TU>(w.W1, q, k10, k11, m);
                    if (w.Wrap1) q = Warps.Wrap<TL, TF, TU>(q);
                    q = Warps.Phase<TL, TF, TU>(w.W2, q, k20, k21, m);
                    q = w.Wrap2 ? Warps.Wrap<TL, TF, TU>(q) : Warps.Clamp<TL, TF, TU>(q);
                }
                TF pos = TL.Mul(q, len);
                TU idx = TL.Truncate(pos);
                TF x = TL.Sub(pos, TL.IntToFloat(idx));
                TL.Read4(in ra, idx, out TF y0, out TF y1, out TF y2, out TF y3);
                if (morph)
                {
                    TL.Read4(in rb, idx, out TF b0, out TF b1, out TF b2, out TF b3);
                    y0 = TL.Add(y0, TL.Mul(TL.Sub(b0, y0), ff));
                    y1 = TL.Add(y1, TL.Mul(TL.Sub(b1, y1), ff));
                    y2 = TL.Add(y2, TL.Mul(TL.Sub(b2, y2), ff));
                    y3 = TL.Add(y3, TL.Mul(TL.Sub(b3, y3), ff));
                }
                TF y = Cubic<TL, TF, TU>(y0, y1, y2, y3, x, half, oneHalf, two, twoHalf);
                if (w.Mirror)
                {
                    // The cycle read as a palindrome, forward to its half and back, turning smoothly
                    // (sin²(π q) / 2: no corner at the turns or the wrap), crossfaded in.
                    TF sq = Warps.SinPi<TL, TF, TU>(q);
                    TF pos2 = TL.Mul(Warps.Clamp<TL, TF, TU>(TL.Mul(TL.Mul(sq, sq), half)), len);
                    TU idx2 = TL.Truncate(pos2);
                    TF x2 = TL.Sub(pos2, TL.IntToFloat(idx2));
                    TL.Read4(in ra, idx2, out TF z0, out TF z1, out TF z2, out TF z3);
                    if (morph)
                    {
                        TL.Read4(in rb, idx2, out TF b0, out TF b1, out TF b2, out TF b3);
                        z0 = TL.Add(z0, TL.Mul(TL.Sub(b0, z0), ff));
                        z1 = TL.Add(z1, TL.Mul(TL.Sub(b1, z1), ff));
                        z2 = TL.Add(z2, TL.Mul(TL.Sub(b2, z2), ff));
                        z3 = TL.Add(z3, TL.Mul(TL.Sub(b3, z3), ff));
                    }
                    TF yb = Cubic<TL, TF, TU>(z0, z1, z2, z3, x2, half, oneHalf, two, twoHalf);
                    y = TL.Add(y, TL.Mul(TL.Sub(yb, y), mirror));
                }
                if (w.Amplitude)
                {
                    y = Warps.Amplitude<TL, TF, TU>(w.W1, y, p, k10, k11, m);
                    y = Warps.Amplitude<TL, TF, TU>(w.W2, y, p, k20, k21, m);
                }
                if (store != null) TL.Store(y, store, storeAt + t * modStride);
                int o = t * AccStride;
                TL.Store(TL.Add(TL.Load(acc, o), TL.Mul(y, g1l)), acc, o);
                TL.Store(TL.Add(TL.Load(acc, o + 8), TL.Mul(y, g1r)), acc, o + 8);
                g1l = TL.Add(g1l, d1l);
                g1r = TL.Add(g1r, d1r);
                if (toF2)
                {
                    TL.Store(TL.Add(TL.Load(acc, o + 16), TL.Mul(y, g2l)), acc, o + 16);
                    TL.Store(TL.Add(TL.Load(acc, o + 24), TL.Mul(y, g2r)), acc, o + 24);
                    g2l = TL.Add(g2l, d2l);
                    g2r = TL.Add(g2r, d2r);
                }
                if (w.Fm != 0)
                {
                    // Linear FM through zero: the increment swings by inc · depth · m (in units of 2^8,
                    // kept well inside 32 bits).
                    TF dev = TL.Mul(TL.Mul(incF, fm), m);
                    dev = TL.Select(TL.Greater(dev, fmLimit), fmLimit, TL.Select(TL.Greater(fmFloor, dev), fmFloor, dev));
                    ph = TL.AddU(TL.AddU(ph, inc), TL.ShiftLeft(TL.Truncate(dev), 8));
                }
                else ph = TL.AddU(ph, inc);
            }
            TL.StoreU(ph, s.Phase, at);
            TL.Store(g1l, g, at);
            TL.Store(g1r, g, total + at);
            if (toF2)
            {
                TL.Store(g2l, g, 2 * total + at);
                TL.Store(g2r, g, 3 * total + at);
            }
        }

        /// <summary>
        /// Eight notes' single oscillators (the sub), notes as lanes: lanes <paramref name="at"/> … +7 of
        /// <paramref name="s"/>, each at its own level, to <paramref name="outBuf"/> at
        /// t · <paramref name="notes"/> + <paramref name="at"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Across<TL, TF, TU>(LaneState s, int at, float[] outBuf, int notes, int count)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            var ra = new LaneRefs();
            Refs(s, at, false, ref ra);
            TU ph = TL.LoadU(s.Phase, at), dph = TL.LoadU(s.Inc, at);
            TU shr = TL.LoadU(s.ShiftR, at), shl = TL.LoadU(s.ShiftL, at);
            TF half = TL.Splat(0.5f), oneHalf = TL.Splat(1.5f), two = TL.Splat(2f), twoHalf = TL.Splat(2.5f);
            TF fracScale = TL.Splat(FracScale);
            for (int t = 0; t < count; t++)
            {
                TU idx = TL.ShiftRightVar(ph, shr);
                TF x = TL.Mul(TL.ToFloat(TL.ShiftRight(TL.ShiftLeftVar(ph, shl), 8)), fracScale);
                TL.Read4(in ra, idx, out TF y0, out TF y1, out TF y2, out TF y3);
                TL.Store(Cubic<TL, TF, TU>(y0, y1, y2, y3, x, half, oneHalf, two, twoHalf), outBuf, t * notes + at);
                ph = TL.AddU(ph, dph);
            }
            TL.StoreU(ph, s.Phase, at);
        }

        /// <summary>
        /// Eight notes' noise, notes as lanes: xorshift32 per lane, uniform in [−1, 1), through a
        /// one-pole lowpass (coefficient <paramref name="a"/>; 1: white) and scaled by
        /// <paramref name="comp"/>, to <paramref name="outBuf"/> at t · <paramref name="notes"/> + <paramref name="at"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Noise<TL, TF, TU>(uint[] state, float[] lp, int at, float a, float comp, float[] outBuf, int notes, int count)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TU x = TL.LoadU(state, at);
            TF y = TL.Load(lp, at);
            TF ca = TL.Splat(a), cc = TL.Splat(comp), scale = TL.Splat(1f / 8388608f), one = TL.Splat(1f);
            for (int t = 0; t < count; t++)
            {
                x = TL.Xor(x, TL.ShiftLeft(x, 13));
                x = TL.Xor(x, TL.ShiftRight(x, 17));
                x = TL.Xor(x, TL.ShiftLeft(x, 5));
                TF u = TL.Sub(TL.Mul(TL.ToFloat(TL.ShiftRight(x, 8)), scale), one);
                y = TL.Add(y, TL.Mul(ca, TL.Sub(u, y)));
                TL.Store(TL.Mul(y, cc), outBuf, t * notes + at);
            }
            TL.StoreU(x, state, at);
            TL.Store(y, lp, at);
        }

        /// <summary>
        /// A per-note source (the sub, the noise) into the filters' inputs for a group of eight notes:
        /// each note's gains (rows of <paramref name="notes"/>: F1 left, right, F2 left, right, then their
        /// per-sample steps) ramped across the tick; F2's only when <paramref name="toF2"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Send<TL, TF, TU>(float[] src, float[] gain, int group, int notes, int count, bool toF2,
            float[] in1L, float[] in1R, float[] in2L, float[] in2R)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int c = 8 * group;
            TF g1l = TL.Load(gain, c), g1r = TL.Load(gain, notes + c), g2l = TL.Load(gain, 2 * notes + c), g2r = TL.Load(gain, 3 * notes + c);
            TF d1l = TL.Load(gain, 4 * notes + c), d1r = TL.Load(gain, 5 * notes + c), d2l = TL.Load(gain, 6 * notes + c), d2r = TL.Load(gain, 7 * notes + c);
            for (int t = 0; t < count; t++)
            {
                int o = t * notes + c;
                TF y = TL.Load(src, o);
                TL.Store(TL.Add(TL.Load(in1L, o), TL.Mul(y, g1l)), in1L, o);
                TL.Store(TL.Add(TL.Load(in1R, o), TL.Mul(y, g1r)), in1R, o);
                g1l = TL.Add(g1l, d1l);
                g1r = TL.Add(g1r, d1r);
                if (toF2)
                {
                    TL.Store(TL.Add(TL.Load(in2L, o), TL.Mul(y, g2l)), in2L, o);
                    TL.Store(TL.Add(TL.Load(in2R, o), TL.Mul(y, g2r)), in2R, o);
                    g2l = TL.Add(g2l, d2l);
                    g2r = TL.Add(g2r, d2r);
                }
            }
            TL.Store(g1l, gain, c);
            TL.Store(g1r, gain, notes + c);
            if (toF2)
            {
                TL.Store(g2l, gain, 2 * notes + c);
                TL.Store(g2r, gain, 3 * notes + c);
            }
        }

        /// <summary>Catmull-Rom through y1 … y2 at x ∈ [0, 1), in NeoBuzz.Dsp.Wavetable's order of operations.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TF Cubic<TL, TF, TU>(TF y0, TF y1, TF y2, TF y3, TF x, TF half, TF oneHalf, TF two, TF twoHalf)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF c1 = TL.Mul(half, TL.Sub(y2, y0));
            TF c2 = TL.Sub(TL.Add(TL.Sub(y0, TL.Mul(twoHalf, y1)), TL.Mul(two, y2)), TL.Mul(half, y3));
            TF c3 = TL.Add(TL.Mul(half, TL.Sub(y3, y0)), TL.Mul(oneHalf, TL.Sub(y1, y2)));
            return TL.Add(TL.Mul(TL.Add(TL.Mul(TL.Add(TL.Mul(c3, x), c2), x), c1), x), y1);
        }

        /// <summary>
        /// Sums a note's accumulators across lanes (each sum in <see cref="ILanes{TF,TU}.Sum"/>'s order),
        /// eight samples at a time, into column <paramref name="note"/> of the filters' inputs (stride
        /// <paramref name="notes"/>): F1 left and right, and F2's when <paramref name="toF2"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void SumLanes<TL, TF, TU>(float[] acc, float[] in1L, float[] in1R, float[] in2L, float[] in2R, bool toF2,
            int note, int notes, int count, float[] scratch)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            int streams = toF2 ? 4 : 2;
            for (int s = 0; s < streams; s++)
            {
                var dest = s == 0 ? in1L : s == 1 ? in1R : s == 2 ? in2L : in2R;
                int t = 0;
                for (; t + 8 <= count; t += 8)
                {
                    int o = t * AccStride + 8 * s;
                    TF sums = TL.Sum8(TL.Load(acc, o), TL.Load(acc, o + AccStride), TL.Load(acc, o + 2 * AccStride), TL.Load(acc, o + 3 * AccStride),
                        TL.Load(acc, o + 4 * AccStride), TL.Load(acc, o + 5 * AccStride), TL.Load(acc, o + 6 * AccStride), TL.Load(acc, o + 7 * AccStride));
                    TL.Store(sums, scratch, 0);
                    for (int j = 0; j < 8; j++) dest[(t + j) * notes + note] = scratch[j];
                }
                for (; t < count; t++)
                    dest[t * notes + note] = TL.Sum(TL.Load(acc, t * AccStride + 8 * s));
            }
        }
    }
}
