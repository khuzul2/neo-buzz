#nullable disable
// Neo machines shared DSP (docs/specs/mastering-tools.md §4.2, M4): the microdynamics gain Neo Transient's Impact
// mode (per band) and Neo Low End's Contrast use. The microdynamics ratio of a signal is its short-term power over
// its long-term power (the level now against the level around it); one bipolar amount I (−1 … +1) moves that ratio
// in dB by the factor (1 + I): the gain in dB is I times the ratio in dB. I = +1 doubles it (the hits stand out
// twice as far), I = −1 takes it out (the signal follows its slow level), I = 0 leaves it.
//  - Two one-pole followers on the power of what they are given (a band's, linked over both channels, or one
//    channel's): fast and slow. The ratio r = (fast + ε) / (slow + ε), ε = 1e-12 (−120 dB, so silence gives 1).
//    g = r^(I/2), the exponent clamped so the gain stays within ±12 dB.
//  - Auto gain: the power in and the power out of the gain (before the compensation) in one-poles of 300 ms, and
//    the compensation √(in / out), within ±12 dB, so a band's energy stays what it was whatever I is. At I = 0 the
//    two are the same number and the compensation is exactly 1.
//  - Per sample, in double, with the portable maths; nothing is allocated after construction. State under 1e-30 is
//    flushed every 64 samples of the instance's own count, so the output doesn't depend on how the host splits the
//    audio.
//  - The followers' times are the caller's, set from a frequency (a band's centre) so a low band isn't rectified
//    into its own ripple: Times(f) gives the fast time 1 / f clamped to 2 … 20 ms, the slow twelve times that within 60 … 300 ms.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal struct MicroGain
    {
        const double Eps = 1e-12;
        const double MaxLn = 1.3815510557964274;    // ln(10^(12/20)): ±12 dB
        const double AutoSeconds = 0.3;

        double fast, slow, powerIn, powerOut;
        double cf, cs, ca;
        int count;

        /// <summary>A band's followers' times in seconds from its centre frequency, scaled by <paramref name="speed"/> (1 = as designed).</summary>
        public static void Times(double hz, double speed, out double fastSeconds, out double slowSeconds)
        {
            double f = Math.Clamp(1.0 / Math.Max(hz, 1), 0.002, 0.02);
            fastSeconds = f * speed;
            slowSeconds = Math.Clamp(12 * f, 0.06, 0.3) * speed;
        }

        /// <summary>The followers' times in seconds (the slow no shorter than the fast), at <paramref name="sampleRate"/>.</summary>
        public void Setup(double fastSeconds, double slowSeconds, double sampleRate)
        {
            cf = 1 - Detector.Coef(fastSeconds, sampleRate);
            cs = 1 - Detector.Coef(Math.Max(slowSeconds, fastSeconds), sampleRate);
            ca = 1 - Detector.Coef(AutoSeconds, sampleRate);
        }

        public void Reset()
        {
            fast = slow = powerIn = powerOut = 0;
            count = 0;
        }

        /// <summary>The followers now (power), for readings and tests.</summary>
        public double Fast => fast;
        public double Slow => slow;

        /// <summary>
        /// The gain (linear) for one sample whose power is <paramref name="power"/> (the square of its level: linked over the
        /// channels by the caller), the amount <paramref name="amount"/> (−1 … +1), and with auto gain on or off.
        /// The gain is to be applied to the samples the power came from.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Next(double power, double amount, bool auto)
        {
            fast += (power - fast) * cf;
            slow += (power - slow) * cs;
            if (++count == 64)
            {
                count = 0;
                fast = DspMath.Flush(fast);
                slow = DspMath.Flush(slow);
                powerIn = DspMath.Flush(powerIn);
                powerOut = DspMath.Flush(powerOut);
            }
            double g = 1;
            if (amount != 0)
            {
                double x = 0.5 * amount * PortableMath.Log((fast + Eps) / (slow + Eps));
                g = PortableMath.Exp(x > MaxLn ? MaxLn : x < -MaxLn ? -MaxLn : x);
            }
            if (!auto) return g;
            powerIn += (power - powerIn) * ca;
            powerOut += (power * g * g - powerOut) * ca;
            double comp = Math.Sqrt((powerIn + Eps) / (powerOut + Eps));
            return g * (comp > 3.981071705534972 ? 3.981071705534972 : comp < 0.251188643150958 ? 0.251188643150958 : comp);
        }
    }
}
