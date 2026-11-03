#nullable disable
// Neo Drive (docs/specs/machines.md §5): the transfer curves and their antiderivatives, for
// first-order antiderivative anti-aliasing (ADAA): y[n] = (F(x[n]) − F(x[n−1])) / (x[n] − x[n−1]).
// Every curve has slope 1 at 0, so the drive sets the small-signal gain.

using System;
using NeoBuzz.Dsp;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Drive
{
    internal enum DriveMode { Tape, Tube, Transistor, Diode, Fold }

    /// <summary>A memoryless curve f with its antiderivative F (F(0) = 0).</summary>
    internal struct Shaper
    {
        public DriveMode Mode;
        /// <summary>Transistor: blend towards a hard clip. Fold: blend towards the sine fold (0 … 1).</summary>
        public double Hardness;
        /// <summary>Diode and Tube: the negative side's saturation level (≤ 1: asymmetric).</summary>
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
                default:
                {
                    double m = Hardness;
                    return (1 - m) * PortableMath.Tanh(x) + m * PortableMath.Sin(x);
                }
            }
        }

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
                default:
                {
                    double m = Hardness;
                    return (1 - m) * LogCosh(x) + m * (1 - PortableMath.Cos(x));
                }
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
