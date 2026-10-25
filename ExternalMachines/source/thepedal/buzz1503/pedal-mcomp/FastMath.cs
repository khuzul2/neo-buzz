// FastMath.cs - IEEE 754 bit-manipulation fast log/exp for per-sample hot paths.
//
// Corrected versions (PedalComp notes section 5, correction of 2026-10-09).
// The coefficients originally copied from Pedal Comp were not exact at the
// ends of their reduction interval: LinToDb jumped by 1.76 dB just below every
// power of two (every 6 dB of level) and DbToLin stepped by 0.048 dB at every
// integer boundary. In a compressor the gain follows LinToDb(envelope)
// directly, so each jump was an audible tick as the envelope crossed -6, -12,
// -18 dBFS and so on. These cubics are exact at both ends (p(0), p(1)), so the
// results are continuous.

namespace PedalMComp
{
    internal static class FastMath
    {
        // 20 * Log10(lin). Accuracy about 0.01 dB, continuous across powers of two.
        // Returns -120 dB for inputs at or below 1e-9 (silence floor).
        public static float LinToDb(float lin)
        {
            if (lin <= 1e-9f) return -120f;
            int   bits = FloatBits.ToInt(lin);
            float exp  = (bits >> 23) - 127f;
            float mant = FloatBits.ToFloat((bits & 0x007FFFFF) | 0x3F800000) - 1f;
            // log2(1+m) on [0,1): p(0)=0, p(1)=1
            float log2 = exp + mant * (1.420865f + mant * (-0.577251f + mant * 0.156386f));
            return log2 * 6.02059f;     // log2 -> dB: x (20 / log2(10))
        }

        // Pow(10, db / 20). Accuracy about 0.002 dB for db in [-120, +24],
        // continuous at integer boundaries.
        public static float DbToLin(float db)
        {
            float x  = db * 0.16609640f;   // db x log2(10) / 20
            float xi = MathF.Floor(x);
            float xf = x - xi;
            // 2^xf on [0,1): p(0)=1, p(1)=2
            float p  = 1f + xf * (0.695928f + xf * (0.224946f + xf * 0.079125f));
            int   e  = MathEx.Clamp((int)xi + 127, 1, 254);
            return FloatBits.ToFloat(e << 23) * p;
        }
    }
}
