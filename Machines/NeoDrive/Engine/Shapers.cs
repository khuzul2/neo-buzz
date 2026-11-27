#nullable disable
// Neo Drive (docs/specs/machines.md §5): the transfer curves and their antiderivatives, for
// first-order antiderivative anti-aliasing (ADAA): y[n] = (F(x[n]) − F(x[n−1])) / (x[n] − x[n−1]).
// Every curve has slope 1 at 0, so the drive sets the small-signal gain.

using System;
using NeoBuzz.Dsp;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Drive
{
    internal enum DriveMode { Tape, Tube, Transistor, Diode, Fold, Overdrive, Fuzz, HardClip, Rectify, Transformer }

    /// <summary>A memoryless curve f with its antiderivative F (F(0) = 0).</summary>
    internal struct Shaper
    {
        public DriveMode Mode;
        /// <summary>
        /// Transistor: blend towards a hard clip. Fold: blend towards the sine fold (0 … 1). Hard clip: the knee's half width (0 hard …
        /// 1 soft). Rectify: 0 = half-wave … 1 = full-wave.
        /// </summary>
        public double Hardness;
        /// <summary>Diode, Tube, Overdrive and Fuzz: the negative side's saturation level (≤ 1: asymmetric).</summary>
        public double NegLevel;

        public static double LogCosh(double x)
        {
            double a = Math.Abs(x);
            // ln cosh x = |x| + ln(1 + e^(−2|x|)) − ln 2, stable for large |x|.
            return a + PortableMath.Log(1 + PortableMath.Exp(-2 * a)) - 0.6931471805599453;
        }

        static double Asinh(double x) => PortableMath.Asinh(x);

        /// <summary>∫ asinh = x·asinh(x) − √(x² + 1) + 1.</summary>
        static double AsinhIntegral(double x) => x * PortableMath.Asinh(x) - Math.Sqrt(x * x + 1) + 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double F(double x)
        {
            switch (Mode)
            {
                case DriveMode.Tape:
                    return PortableMath.Tanh(x);
                case DriveMode.Tube:
                {
                    // Asymmetric: the negative side saturates at NegLevel (softer, later).
                    if (x >= 0) return PortableMath.Tanh(x);
                    double a = NegLevel;
                    return a * PortableMath.Tanh(x / a);
                }
                case DriveMode.Transistor:
                {
                    // Towards a cubic clip: flat at ±1 from |x| = 1.5, smooth (C1) at the knee.
                    double h = Hardness;
                    double u = x / 1.5;
                    double clip = u >= 1 ? 1 : u <= -1 ? -1 : 1.5 * u - 0.5 * u * u * u;
                    return (1 - h) * PortableMath.Tanh(x) + h * clip;
                }
                case DriveMode.Diode:
                {
                    // A diode clipper's soft-logarithmic curve (asinh), squeezed on the negative
                    // side for asymmetry. Smooth through 0 (both sides have zero curvature there).
                    if (x >= 0) return Asinh(x);
                    double a = NegLevel;
                    return a * Asinh(x / a);
                }
                case DriveMode.Fold:
                {
                    double m = Hardness;
                    return (1 - m) * PortableMath.Tanh(x) + m * PortableMath.Sin(x);
                }
                case DriveMode.Overdrive:
                {
                    // Algebraic soft clip x / √(1 + x²) (slow approach to the rail), squeezed on the negative side.
                    if (x >= 0) return x / Math.Sqrt(1 + x * x);
                    double a = NegLevel;
                    double u = x / a;
                    return a * (u / Math.Sqrt(1 + u * u));
                }
                case DriveMode.Fuzz:
                    // A hard cubic clip, level 1 above and NegLevel below the centre.
                    return x >= 0 ? CubicClip(x, 1) : -CubicClip(-x, NegLevel);
                case DriveMode.HardClip:
                {
                    double w = KneeWidth(Hardness);
                    double a = Math.Abs(x);
                    double y = a <= 1 - w ? a : a < 1 + w ? a - (a - (1 - w)) * (a - (1 - w)) / (4 * w) : 1;
                    return x < 0 ? -y : y;
                }
                case DriveMode.Rectify:
                {
                    // tanh, then a blend of the half-wave (the positive part) and the full-wave (the magnitude) rectified result.
                    double g = PortableMath.Tanh(x);
                    return 0.5 * (1 - Hardness) * g + 0.5 * (1 + Hardness) * Math.Abs(g);
                }
                default:   // Transformer: the core is a tanh (the frequency dependence is in the engine)
                    return PortableMath.Tanh(x);
            }
        }

        /// <summary>Slope 1 at 0 and level <paramref name="level"/> from |x| = 1.5 · level: C1, hard-kneed.</summary>
        static double CubicClip(double x, double level)
        {
            double k = 1.5 * level;
            double u = x / k;
            return u >= 1 ? level : level * (1.5 * u - 0.5 * u * u * u);
        }

        /// <summary>∫₀ˣ CubicClip for x ≥ 0.</summary>
        static double CubicClipIntegral(double x, double level)
        {
            double k = 1.5 * level;
            double u = x / k;
            return u >= 1 ? level * (x - 0.375 * k) : level * k * (0.75 * u * u - 0.125 * u * u * u * u);
        }

        /// <summary>The hard clip's half knee width for a Character 0 … 1 (a hair above 0 keeps the maths finite).</summary>
        public static double KneeWidth(double character) => 0.002 + 0.998 * character;

        /// <summary>Antiderivative of <see cref="F"/>, zero at 0.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double AntiDerivative(double x)
        {
            switch (Mode)
            {
                case DriveMode.Tape:
                    return LogCosh(x);
                case DriveMode.Tube:
                {
                    if (x >= 0) return LogCosh(x);
                    double a = NegLevel;
                    return a * a * LogCosh(x / a);
                }
                case DriveMode.Transistor:
                {
                    double h = Hardness;
                    double u = x / 1.5;
                    double au = Math.Abs(u);
                    double clipInt = au <= 1 ? 1.5 * (0.75 * u * u - 0.125 * u * u * u * u) : Math.Abs(x) - 0.5625;
                    return (1 - h) * LogCosh(x) + h * clipInt;
                }
                case DriveMode.Diode:
                {
                    if (x >= 0) return AsinhIntegral(x);
                    double a = NegLevel;
                    return a * a * AsinhIntegral(x / a);
                }
                case DriveMode.Fold:
                {
                    double m = Hardness;
                    return (1 - m) * LogCosh(x) + m * (1 - PortableMath.Cos(x));
                }
                case DriveMode.Overdrive:
                {
                    if (x >= 0) return Math.Sqrt(1 + x * x) - 1;
                    double a = NegLevel;
                    double u = x / a;
                    return a * a * (Math.Sqrt(1 + u * u) - 1);
                }
                case DriveMode.Fuzz:
                    return x >= 0 ? CubicClipIntegral(x, 1) : CubicClipIntegral(-x, NegLevel);
                case DriveMode.HardClip:
                {
                    double w = KneeWidth(Hardness);
                    double a = Math.Abs(x);
                    double h = 1 - w;
                    if (a <= h) return 0.5 * a * a;
                    if (a < 1 + w)
                    {
                        double t = a - h;
                        return 0.5 * h * h + h * t + 0.5 * t * t - t * t * t / (12 * w);
                    }
                    return 0.5 * h * h + 2 * w - (2.0 / 3.0) * w * w + (a - (1 + w));
                }
                case DriveMode.Rectify:
                {
                    // ∫ tanh = ln cosh; ∫ |tanh| = sign(x) · ln cosh.
                    double l = LogCosh(x);
                    return 0.5 * (1 - Hardness) * l + 0.5 * (1 + Hardness) * (x < 0 ? -l : l);
                }
                default:   // Transformer
                    return LogCosh(x);
            }
        }
    }

    /// <summary>First-order ADAA around a <see cref="Shaper"/>, with a bias offset.</summary>
    internal struct AdaaShaper
    {
        double x1, f1;   // previous input and its antiderivative value
        bool primed;

        /// <summary>
        /// One sample of f(x + bias) − f(bias), anti-aliased. The bias offset is removed so that
        /// silence stays silence.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Process(ref Shaper s, double x, double bias, double fBias)
        {
            double u = x + bias;
            double F = s.AntiDerivative(u);
            double y;
            if (!primed)
            {
                primed = true;
                y = s.F(u);
            }
            else
            {
                double d = u - x1;
                if (Math.Abs(d) > 1e-6)
                    y = (F - f1) / d;
                else
                    y = s.F(0.5 * (u + x1));
            }
            x1 = u;
            f1 = F;
            return y - fBias;
        }

        public void Reset() { primed = false; x1 = f1 = 0; }
    }
}
