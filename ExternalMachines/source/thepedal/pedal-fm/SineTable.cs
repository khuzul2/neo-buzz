using System;
using System.Runtime.CompilerServices;

namespace PedalFM
{
    /// <summary>
    /// Static sine LUT for FM operators. 8192 entries, linear interpolation.
    ///
    /// Indexed by a 32-bit unsigned phase accumulator: top 13 bits = table
    /// index, low 19 bits = interpolation fraction. Phase wrapping is free
    /// (just let the uint overflow).
    ///
    /// Static is safe here because the table is read-only after construction.
    /// The Core §22 rule is against mutable shared state, not shared LUTs.
    /// </summary>
    internal static class SineTable
    {
        public const int  SIZE       = 8192;
        public const int  BITS       = 13;             // log2(SIZE)
        public const int  FRAC_BITS  = 32 - BITS;      // 19
        public const uint FRAC_MASK  = (1u << FRAC_BITS) - 1u;
        public const float FRAC_SCALE = 1.0f / (float)(1u << FRAC_BITS);
        public const int  INDEX_MASK = SIZE - 1;

        // SIZE+1: extra slot duplicates index 0 so the linear interp at the
        // last index doesn't need a wrap-and-mask.
        static readonly float[] _table = BuildTable();

        static float[] BuildTable()
        {
            var t = new float[SIZE + 1];
            for (int i = 0; i < SIZE; i++)
                t[i] = MathF.Sin(2f * MathF.PI * i / SIZE);
            t[SIZE] = t[0];
            return t;
        }

        /// <summary>Lookup sin(2*pi*phase/2^32).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lookup(uint phase)
        {
            int   idx  = (int)(phase >> FRAC_BITS) & INDEX_MASK;
            float frac = (phase & FRAC_MASK) * FRAC_SCALE;
            float a    = _table[idx];
            float b    = _table[idx + 1];
            return a + (b - a) * frac;
        }
    }
}
