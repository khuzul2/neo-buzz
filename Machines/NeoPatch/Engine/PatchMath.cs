#nullable disable
// Neo Patch NP1 (docs/specs/neo-patch-engine.md): the scalar maths the control interval needs, cheaper than PortableMath's
// double versions (NP0: 2^x and tan were a third of a 20-node patch's time). Plain IEEE operations only (no fused
// multiply-add, no platform intrinsics), so every platform gives the same bits.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Patch
{
    internal static class PatchMath
    {
        /// <summary>2^x for |x| below 126: the fraction by an 8-term series (relative error about 1e-7), the integer part into the exponent.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Exp2(float x)
        {
            if (!(x > -126f)) x = -126f;       // also NaN
            if (x > 126f) x = 126f;
            float fl = MathF.Floor(x);
            float f = x - fl;
            float p = 1f + f * (0.69314718f + f * (0.24022651f + f * (0.05550411f + f * (0.00961813f + f * (0.00133336f + f * (0.00015404f + f * (0.0000152527f + f * 0.00000132155f)))))));
            return p * BitConverter.Int32BitsToSingle(((int)fl + 127) << 23);
        }

        /// <summary>sin(π y) for |y| ≤ ½ (error 6e-8).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SinPi(float y)
        {
            float y2 = y * y;
            float s = -0.0073704309f;
            s = 0.082145886f + y2 * s;
            s = -0.59926453f + y2 * s;
            s = 2.5501640f + y2 * s;
            s = -5.1677128f + y2 * s;
            s = 3.14159265f + y2 * s;
            return y * s;
        }

        /// <summary>tan(π y) for 0 ≤ y &lt; ½ as sin over cos, both from <see cref="SinPi"/> (y is a cutoff as a fraction of the sample rate).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float TanPi(float y)
        {
            if (y > 0.45f) y = 0.45f;
            if (y < 1e-6f) y = 1e-6f;
            return SinPi(y) / SinPi(0.5f - y);
        }

        /// <summary>A value that is finite and under 1e9 in size stays; anything else (NaN, infinities, runaways) becomes 0; denormals flush.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float CleanState(float x)
        {
            float a = Math.Abs(x);
            return a < 1e-18f || !(a < 1e9f) ? 0f : x;
        }

        /// <summary>The Ladder's loop gain for a Q (Neo Filter's FilterDesign.LadderK), 0 at Q 0.5.</summary>
        public static float LadderK(float q)
        {
            float r = MathF.Sqrt(Math.Max(0f, 4f * q * q - 1f));
            float u = r / (1f + r);
            return 4f * u * u * u * u;
        }
    }

    /// <summary>A smoothed knob: the value at the previous block's end, so a block ramps from it to the knob's new value (a macro, an edit, a pattern never steps).</summary>
    internal struct Smooth
    {
        float prev;
        bool primed;

        /// <summary>The ramp's start; <paramref name="step"/> is the change per sample across a block.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Begin(float now, out float step)
        {
            if (!primed) { prev = now; primed = true; }
            float start = prev;
            step = (now - start) * (1f / PatchContext.Block);
            prev = now;
            return start;
        }

        public void Adopt(in Smooth old) { prev = old.prev; primed = old.primed; }
    }
}
