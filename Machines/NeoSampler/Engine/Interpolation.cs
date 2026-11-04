#nullable disable
// Neo Sampler (docs/specs/machines.md §5): reading a wave between its samples. A 32-tap windowed
// sinc (Kaiser, β 9), polyphase with 256 phases and straight lines between neighbouring phases.
// Its cutoff follows the step through the wave: at or below one sample per output sample (playing
// at or below the wave's own pitch and rate) it sits at 0.45 of the wave's rate; above, at the
// output's Nyquist times 0.9 … 0.85 (17 kernels, an octave in 16 steps, the margin growing with the
// step), so what would fold back over the output's Nyquist is filtered out instead. Steps beyond 2
// read an octave-down copy of the wave (SampleLayer's levels), where the step is between 1 and 2
// again. A step of exactly 1 from a whole sample reads the samples themselves (the voice decides:
// a wave at its root and the song's rate plays as it is). Built from IEEE basic operations and
// PortableMath, so the same bits everywhere (§4).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using NeoBuzz.Dsp;

namespace NeoBuzz.Sampler
{
    internal static class Interpolation
    {
        /// <summary>Taps of the kernel; it reads <see cref="Half"/> − 1 samples before a position and <see cref="Half"/> after.</summary>
        public const int Taps = 32;
        public const int Half = Taps / 2;
        public const int Phases = 256;
        /// <summary>Kernels: [0] for steps up to 1, then 16 an octave for steps up to 2.</summary>
        public const int Cutoffs = 17;
        /// <summary>The cutoff at a step of 1, as a share of the wave's Nyquist (0.45 of its rate); at a step of 2, of the output's.</summary>
        public const double Alpha = 0.9, AlphaAt2 = 0.85;
        public const double Beta = 9;

        /// <summary>[cutoff][phase · Taps + tap], phases 0 … Phases (the last for the straight line to the next).</summary>
        static readonly float[][] kernels = Build();

        public static float[] Kernel(int cutoff) => kernels[cutoff];

        /// <summary>The kernel for a step through the wave's level (1 … 2 above the first).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CutoffFor(double step)
        {
            if (step <= 1.0) return 0;
            int c = (int)Math.Ceiling((step - 1.0) * (Cutoffs - 1));
            return c < 1 ? 1 : c > Cutoffs - 1 ? Cutoffs - 1 : c;
        }

        /// <summary>The cutoff kernel <paramref name="c"/> is designed for, in cycles per wave sample.</summary>
        public static double CutoffOf(int c)
        {
            double r = 1.0 + c / (double)(Cutoffs - 1);
            return 0.5 * (Alpha + (AlphaAt2 - Alpha) * (r - 1.0)) / r;
        }

        static float[][] Build()
        {
            var all = new float[Cutoffs][];
            double i0b = BesselI0(Beta);
            var row = new double[Taps];
            for (int c = 0; c < Cutoffs; c++)
            {
                double fc = CutoffOf(c);
                var k = new float[(Phases + 1) * Taps];
                for (int p = 0; p <= Phases; p++)
                {
                    double frac = p / (double)Phases;
                    double sum = 0;
                    for (int j = 0; j < Taps; j++)
                    {
                        double t = j - (Half - 1) - frac;           // distance from the position to the tap
                        double x = t / Half;
                        double w = Math.Abs(x) >= 1 ? 0 : BesselI0(Beta * Math.Sqrt(1 - x * x)) / i0b;
                        double s = Math.Abs(t) < 1e-12 ? 2 * fc : PortableMath.Sin(2 * Math.PI * fc * t) / (Math.PI * t);
                        row[j] = s * w;
                        sum += row[j];
                    }
                    // Every phase passes DC at exactly 1: no ripple in the level as the position moves.
                    for (int j = 0; j < Taps; j++) k[p * Taps + j] = (float)(row[j] / sum);
                }
                all[c] = k;
            }
            return all;
        }

        /// <summary>The modified Bessel function I0, by its series (basic operations only).</summary>
        internal static double BesselI0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int k = 1; k < 64; k++)
            {
                term *= q / (k * (double)k);
                sum += term;
                if (term < sum * 1e-17) break;
            }
            return sum;
        }

        /// <summary>
        /// One channel at <paramref name="x"/> (in samples of <paramref name="data"/>, which holds
        /// <see cref="SampleLayer.Pad"/> samples of padding before sample 0). The caller keeps
        /// <paramref name="x"/> where the taps stay inside the array. Four taps at a time in
        /// 128-bit vectors (four lanes on x64 and arm64 alike), the lanes added in a fixed order.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Read(float[] data, double x, float[] kernel)
        {
            int i = (int)Math.Floor(x);
            double ph = (x - i) * Phases;
            int p = (int)ph;
            var t = Vector128.Create((float)(ph - p));
            int at = i - (Half - 1) + SampleLayer.Pad;
            ref float k0 = ref kernel[p * Taps];
            ref float k1 = ref kernel[p * Taps + Taps];
            ref float d = ref data[at];
            var acc = Vector128<float>.Zero;
            for (int j = 0; j < Taps; j += 4)
            {
                var a = Vector128.LoadUnsafe(ref k0, (nuint)j);
                var w = a + t * (Vector128.LoadUnsafe(ref k1, (nuint)j) - a);
                acc += Vector128.LoadUnsafe(ref d, (nuint)j) * w;
            }
            return ((acc.GetElement(0) + acc.GetElement(1)) + acc.GetElement(2)) + acc.GetElement(3);
        }

        /// <summary>Two channels at once (one kernel).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(float[] left, float[] right, double x, float[] kernel, out float l, out float r)
        {
            int i = (int)Math.Floor(x);
            double ph = (x - i) * Phases;
            int p = (int)ph;
            var t = Vector128.Create((float)(ph - p));
            int at = i - (Half - 1) + SampleLayer.Pad;
            ref float k0 = ref kernel[p * Taps];
            ref float k1 = ref kernel[p * Taps + Taps];
            ref float dl = ref left[at];
            ref float dr = ref right[at];
            var al = Vector128<float>.Zero;
            var ar = Vector128<float>.Zero;
            for (int j = 0; j < Taps; j += 4)
            {
                var a = Vector128.LoadUnsafe(ref k0, (nuint)j);
                var w = a + t * (Vector128.LoadUnsafe(ref k1, (nuint)j) - a);
                al += Vector128.LoadUnsafe(ref dl, (nuint)j) * w;
                ar += Vector128.LoadUnsafe(ref dr, (nuint)j) * w;
            }
            l = ((al.GetElement(0) + al.GetElement(1)) + al.GetElement(2)) + al.GetElement(3);
            r = ((ar.GetElement(0) + ar.GetElement(1)) + ar.GetElement(2)) + ar.GetElement(3);
        }
    }
}
