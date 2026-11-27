#nullable disable
// Neo Keys (docs/specs/neo-keys.md): the three models' mode tables and the pickup curves. Clean room: everything here is written from the acoustics
// literature and our own algebra; no other product's code, tables or presets were used.
//   - Tine and Reed: a clamped-free (cantilever) bar. Its bending modes are f_n proportional to (beta_n L)^2 with beta_n L = 1.875104, 4.694091, 7.854757,
//     10.995541, so the ratios are 1 : 6.267 : 17.55 : 34.39 (Fletcher and Rossing, The Physics of Musical Instruments, 2nd ed., ch. 2). The mode
//     shape is phi_n(x) = cosh(b x) - cos(b x) - s_n (sinh(b x) - sin(b x)), s_n = (cosh bL + cos bL) / (sinh bL + sin bL). A hammer at x_h drives
//     mode n with phi_n(x_h) and a pickup at x_p reads it with phi_n(x_p); the displacement of an impulse-driven mode falls as 1/f_n, so the amplitude of
//     mode n at the pickup is phi_n(x_h) phi_n(x_p) / ratio_n, relative to the first.
//   - String: a stiff string, partial n at n f1 sqrt(1 + B n^2) (Fletcher 1964, JASA 36, 203; Fletcher and Rossing ch. 12), struck at the fraction
//     beta of its length: partial n is driven with sin(n pi beta) and read by a pickup at x with sin(n pi x); displacement falls as 1/n.
//   - The tone bar, the reed's body and the string's second polarisation are one extra mode on the note, a few cents off it, that rings two or three times
//     longer (the aftersound of a coupled pair; Fletcher and Rossing ch. 12 on the double decay of a string).
//   - The pickups: a magnetic pickup's coil voltage is the rate of change of the flux through it, and the flux through a coil falls with the gap
//     to the vibrating ferromagnetic part (Horton and Moore, Am. J. Phys. 77, 144, 2009), so the output is a velocity read through a curve of the
//     displacement, modelled here as flux(y) = y / (1 - a y) (a falls on 1/gap); an electrostatic pickup reads the reed through the gap capacitance, and
//     a reed between two plates (push-pull) gives an odd curve, y / (1 - (a y)^2).
// Pure and allocation-free after construction.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Keys
{
    internal enum KeysModel { Tine, Reed, String }

    /// <summary>One model's modes. Mode 0 is the slow partial (tone bar, body, second polarisation); modes 1 to Count - 1 are the main ones.</summary>
    internal sealed class KeysTable
    {
        public int Count;
        /// <summary>Frequency ratio to the note (mode 1 is the note itself).</summary>
        public readonly double[] Ratio = new double[KeysModels.MaxModes];
        /// <summary>The weight of each mode at the (first) pickup, mode 1 normalised to 1; mode 0 holds the slow partial's weight at full Bar and body.</summary>
        public readonly float[] WeightA = new float[KeysModels.MaxModes];
        /// <summary>The same at the second pickup (the String's bridge pickup; the other models have only one).</summary>
        public readonly float[] WeightB = new float[KeysModels.MaxModes];
        /// <summary>T60 of each mode as a multiple of Decay (1 for mode 1), and the same with the string muted.</summary>
        public readonly double[] T60 = new double[KeysModels.MaxModes];
        public readonly double[] T60Muted = new double[KeysModels.MaxModes];
    }

    internal static class KeysModels
    {
        /// <summary>The slow partial and up to 12 main modes.</summary>
        public const int MaxModes = 13;
        public const int StringPartials = 12;
        /// <summary>The slow partial's detune from the note, in cents (it beats against the main mode), by model.</summary>
        public static readonly double[] SlowCents = { 2.0, 0.0, 1.0 };
        /// <summary>The slow partial's T60 as a multiple of the main mode's.</summary>
        public static readonly double[] SlowT60 = { 2.6, 1.7, 2.8 };

        // Where the hammer strikes and the pickup reads, as fractions of the length.
        const double TineHammer = 0.16, TinePickup = 0.93, ReedHammer = 0.14, ReedPickup = 0.80;
        const double StringStrike = 0.12, NeckPickup = 0.26, BridgePickup = 0.075, Stiffness = 0.0004;
        static readonly double[] Beta = { 1.875104069, 4.694091133, 7.854757438, 10.99554073 };

        public static readonly KeysTable[] Tables = { BuildBeam(TineHammer, TinePickup, 0.9, 0), BuildBeam(ReedHammer, ReedPickup, 1.25, 1), BuildString() };

        /// <summary>The flux curve's largest a (the pickup at 100 %), per model: below the pole at 1 / y with room for the soft clip.</summary>
        public static readonly double[] MaxA = { 0.72, 0.80, 0.55 };
        /// <summary>The displacement sum is scaled into the pickup's curve by this (the tone bar and the tine add up to nearly 1.9 at full speed).</summary>
        public static readonly float[] PickupIn = { 0.6f, 0.95f, 0.6f };
        /// <summary>Each model's output trim, so that a chord of any model has about the level of the others (measured, docs/specs/neo-keys.md).</summary>
        public static readonly float[] ModelGain = { 1f, 1.35f, 0.33f };
        /// <summary>The hammer's amplitude is its speed to this power (1.25: velocity 127 over 30 is 15.7 dB at the note's own mode).</summary>
        public const double SpeedToLevel = 1.25;

        /// <summary>The mode shape of the n-th (0-based) clamped-free mode at x (0 clamp, 1 free end).</summary>
        public static double Shape(int n, double x)
        {
            double b = Beta[n], bx = b * x;
            double e1 = PortableMath.Exp(bx), e2 = PortableMath.Exp(-bx), eb = PortableMath.Exp(b), enb = PortableMath.Exp(-b);
            double sigma = (0.5 * (eb + enb) + PortableMath.Cos(b)) / (0.5 * (eb - enb) + PortableMath.Sin(b));
            return 0.5 * (e1 + e2) - PortableMath.Cos(bx) - sigma * (0.5 * (e1 - e2) - PortableMath.Sin(bx));
        }

        static KeysTable BuildBeam(double xh, double xp, double t60Exponent, int model)
        {
            var t = new KeysTable();
            t.Count = 5;
            double first = Shape(0, xh) * Shape(0, xp);
            for (int n = 0; n < 4; n++)
            {
                double ratio = (Beta[n] / Beta[0]) * (Beta[n] / Beta[0]);
                t.Ratio[n + 1] = ratio;
                t.WeightA[n + 1] = (float)(Shape(n, xh) * Shape(n, xp) / ratio / first);
                t.WeightB[n + 1] = t.WeightA[n + 1];
                t.T60[n + 1] = t.T60Muted[n + 1] = PortableMath.Pow(ratio, -t60Exponent);
            }
            t.Ratio[0] = PortableMath.Pow(2.0, SlowCents[model] / 1200.0);
            t.WeightA[0] = t.WeightB[0] = 0.9f;
            t.T60[0] = t.T60Muted[0] = SlowT60[model];
            return t;
        }

        static KeysTable BuildString()
        {
            var t = new KeysTable();
            t.Count = StringPartials + 1;
            double r1 = Math.Sqrt(1 + Stiffness);
            double a1 = PortableMath.Sin(Math.PI * StringStrike), wn1 = PortableMath.Sin(Math.PI * NeckPickup), wb1 = PortableMath.Sin(Math.PI * BridgePickup);
            const double kappa = 0.03, kappaMuted = 0.35;
            for (int n = 1; n <= StringPartials; n++)
            {
                double ratio = n * Math.Sqrt(1 + Stiffness * n * n) / r1;
                t.Ratio[n] = ratio;
                double drive = PortableMath.Sin(Math.PI * n * StringStrike) / n;
                t.WeightA[n] = (float)(drive * PortableMath.Sin(Math.PI * n * NeckPickup) / (a1 * wn1));
                t.WeightB[n] = (float)(drive * PortableMath.Sin(Math.PI * n * BridgePickup) / (a1 * wb1));
                t.T60[n] = (1 + kappa) / (1 + kappa * n * n);
                t.T60Muted[n] = 0.125 * (1 + kappaMuted) / (1 + kappaMuted * n * n);
            }
            t.Ratio[0] = PortableMath.Pow(2.0, SlowCents[2] / 1200.0);
            t.WeightA[0] = t.WeightB[0] = 0.9f;
            t.T60[0] = SlowT60[2];
            t.T60Muted[0] = 0.125 * SlowT60[2] * 0.5;
            return t;
        }

        /// <summary>The flux of a pickup at displacement <paramref name="y"/> (the soft-clipped displacement, around +-1) with the closeness <paramref name="a"/>.</summary>
        public static float Flux(KeysModel model, float y, float a)
        {
            float ys = 1.2f * DspMath.FastTanh(y * (1f / 1.2f));
            if (model == KeysModel.Reed)
            {
                float u = a * ys;
                return ys / (1f - u * u);
            }
            return ys / (1f - a * ys);
        }
    }
}
