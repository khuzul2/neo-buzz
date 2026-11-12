#nullable disable
// Neo machines shared DSP (docs/specs/mastering-tools.md §4.2, M4): a parameter smoother for gains that must not
// zipper when automation steps them every block. A LinearRamp (lands exactly) followed by a one-pole (4 ms), so the
// trajectory has no corners (a straight ramp retargeted every block has a corner at each block and splatters
// 20 dB higher above 2 kHz). The one-pole's output snaps to the target once the ramp has landed and the two are
// within 1e-6 (relative), so a gain of exactly 1 ends exactly 1 (the unity machines pass bits through).
// Allocation-free; basic float and double operations only, the same bits on every platform.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal struct SmoothRamp
    {
        const double PoleSeconds = 0.004;
        LinearRamp ramp;
        double y, k;

        /// <summary>The ramp's length and the one-pole's pole for <paramref name="sampleRate"/>.</summary>
        public void Setup(double seconds, double sampleRate)
        {
            ramp.Setup(seconds, sampleRate);
            k = 1 - PortableMath.Exp(-1.0 / (PoleSeconds * sampleRate));
        }

        public void Reset(float v)
        {
            ramp.Reset(v);
            y = v;
        }

        public void SetTarget(float v) => ramp.SetTarget(v);

        public float Value => (float)y;
        public float Target => ramp.Target;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Next()
        {
            float x = ramp.Next();
            if (y == x) return x;
            if (ramp.Settled && Math.Abs(x - y) <= 1e-6 * Math.Max(1.0, Math.Abs(x)))
            {
                y = x;
                return x;
            }
            y += (x - y) * k;
            return (float)y;
        }

        /// <summary>Settled at <paramref name="v"/>, exactly.</summary>
        public bool At(float v) => ramp.At(v) && y == v;
    }
}
