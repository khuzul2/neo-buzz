#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.1): the unison wavetable kernel. One oscillator of one
// note renders its unison voices as lanes, eight at a time: 32-bit phases (exact, and the wrap is
// free), one mip level and one frame pair per control tick shared by every lane, each lane reading
// four points of both frames, morphing between the frames, then a 4-point cubic (Catmull-Rom) within
// the frame, as NeoBuzz.Dsp.Wavetable reads. Each lane has its own gain and pan, ramped across the
// tick; the lanes add, lane by lane, into the note's accumulators (left lanes then right lanes, per
// sample), which the note sums across lanes in one fixed order afterwards.

using System.Runtime.CompilerServices;

namespace NeoBuzz.DyeEssence
{
    internal static class UnisonKernel
    {
        /// <summary>Accumulator layout: per sample, 8 left lanes then 8 right lanes.</summary>
        public const int AccStride = 16;

        const float FracScale = 1f / 16777216f;   // 2^-24

        /// <summary>
        /// Renders <paramref name="count"/> samples of eight lanes reading frames <paramref name="offA"/>
        /// and <paramref name="offB"/> (offsets of the same level in <see cref="TableArena.Data"/>),
        /// <paramref name="ff"/> of the way from A to B. Lane state at <paramref name="lane"/>: phases and
        /// increments in <paramref name="phase"/>/<paramref name="inc"/>, gains in <paramref name="gain"/>
        /// (left at <paramref name="gainAt"/>, right at + 16, their per-sample steps at + 32 and + 48;
        /// the gains are left where the ramp ends).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Morph<TL, TF, TU>(float[] data, int offA, int offB, float ff, int log2Len,
            uint[] phase, uint[] inc, int lane, float[] gain, int gainAt, float[] acc, int count)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TU ph = TL.LoadU(phase, lane);
            TU dph = TL.LoadU(inc, lane);
            TF gl = TL.Load(gain, gainAt), gr = TL.Load(gain, gainAt + 16);
            TF dl = TL.Load(gain, gainAt + 32), dr = TL.Load(gain, gainAt + 48);
            TF morph = TL.Splat(ff), half = TL.Splat(0.5f), oneHalf = TL.Splat(1.5f), two = TL.Splat(2f), twoHalf = TL.Splat(2.5f);
            TF fracScale = TL.Splat(FracScale);
            int shift = 32 - log2Len;
            ref float a = ref data[offA];
            ref float b = ref data[offB];
            for (int t = 0; t < count; t++)
            {
                TU idx = TL.ShiftRight(ph, shift);
                TF x = TL.Mul(TL.ToFloat(TL.ShiftRight(TL.ShiftLeft(ph, log2Len), 8)), fracScale);
                TL.Read4(ref a, idx, out TF a0, out TF a1, out TF a2, out TF a3);
                TL.Read4(ref b, idx, out TF b0, out TF b1, out TF b2, out TF b3);
                TF y0 = TL.Add(a0, TL.Mul(TL.Sub(b0, a0), morph));
                TF y1 = TL.Add(a1, TL.Mul(TL.Sub(b1, a1), morph));
                TF y2 = TL.Add(a2, TL.Mul(TL.Sub(b2, a2), morph));
                TF y3 = TL.Add(a3, TL.Mul(TL.Sub(b3, a3), morph));
                TF y = Cubic<TL, TF, TU>(y0, y1, y2, y3, x, half, oneHalf, two, twoHalf);
                int o = t * AccStride;
                TL.Store(TL.Add(TL.Load(acc, o), TL.Mul(y, gl)), acc, o);
                TL.Store(TL.Add(TL.Load(acc, o + 8), TL.Mul(y, gr)), acc, o + 8);
                gl = TL.Add(gl, dl);
                gr = TL.Add(gr, dr);
                ph = TL.AddU(ph, dph);
            }
            TL.StoreU(ph, phase, lane);
            TL.Store(gl, gain, gainAt);
            TL.Store(gr, gain, gainAt + 16);
        }

        /// <summary>As <see cref="Morph"/> on one frame (the position on a frame, or a one-frame table): half the reads.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Single<TL, TF, TU>(float[] data, int offA, int log2Len,
            uint[] phase, uint[] inc, int lane, float[] gain, int gainAt, float[] acc, int count)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TU ph = TL.LoadU(phase, lane);
            TU dph = TL.LoadU(inc, lane);
            TF gl = TL.Load(gain, gainAt), gr = TL.Load(gain, gainAt + 16);
            TF dl = TL.Load(gain, gainAt + 32), dr = TL.Load(gain, gainAt + 48);
            TF half = TL.Splat(0.5f), oneHalf = TL.Splat(1.5f), two = TL.Splat(2f), twoHalf = TL.Splat(2.5f);
            TF fracScale = TL.Splat(FracScale);
            int shift = 32 - log2Len;
            ref float a = ref data[offA];
            for (int t = 0; t < count; t++)
            {
                TU idx = TL.ShiftRight(ph, shift);
                TF x = TL.Mul(TL.ToFloat(TL.ShiftRight(TL.ShiftLeft(ph, log2Len), 8)), fracScale);
                TL.Read4(ref a, idx, out TF y0, out TF y1, out TF y2, out TF y3);
                TF y = Cubic<TL, TF, TU>(y0, y1, y2, y3, x, half, oneHalf, two, twoHalf);
                int o = t * AccStride;
                TL.Store(TL.Add(TL.Load(acc, o), TL.Mul(y, gl)), acc, o);
                TL.Store(TL.Add(TL.Load(acc, o + 8), TL.Mul(y, gr)), acc, o + 8);
                gl = TL.Add(gl, dl);
                gr = TL.Add(gr, dr);
                ph = TL.AddU(ph, dph);
            }
            TL.StoreU(ph, phase, lane);
            TL.Store(gl, gain, gainAt);
            TL.Store(gr, gain, gainAt + 16);
        }

        /// <summary>Catmull-Rom through y1 … y2 at x ∈ [0, 1), in NeoBuzz.Dsp.Wavetable's order of operations.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TF Cubic<TL, TF, TU>(TF y0, TF y1, TF y2, TF y3, TF x, TF half, TF oneHalf, TF two, TF twoHalf)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            TF c1 = TL.Mul(half, TL.Sub(y2, y0));
            TF c2 = TL.Sub(TL.Add(TL.Sub(y0, TL.Mul(twoHalf, y1)), TL.Mul(two, y2)), TL.Mul(half, y3));
            TF c3 = TL.Add(TL.Mul(half, TL.Sub(y3, y0)), TL.Mul(oneHalf, TL.Sub(y1, y2)));
            return TL.Add(TL.Mul(TL.Add(TL.Mul(TL.Add(TL.Mul(c3, x), c2), x), c1), x), y1);
        }

        /// <summary>
        /// Sums a note's accumulators across lanes, in the fixed order of <see cref="ILanes{TF,TU}.Sum"/>,
        /// into column <paramref name="note"/> of the filters' inputs (stride <paramref name="notes"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void SumLanes<TL, TF, TU>(float[] acc, float[] outL, float[] outR, int note, int notes, int count)
            where TL : struct, ILanes<TF, TU> where TF : struct where TU : struct
        {
            for (int t = 0; t < count; t++)
            {
                int o = t * AccStride;
                outL[t * notes + note] = TL.Sum(TL.Load(acc, o));
                outR[t * notes + note] = TL.Sum(TL.Load(acc, o + 8));
            }
        }
    }
}
