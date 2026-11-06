#nullable disable
// Neo Clipper (docs/specs/mastering-tools.md §5.1): the curves, normalised to a ceiling of 1, each with its
// antiderivative for first-order antiderivative anti-aliasing (ADAA): y[n] = (F(u[n]) − F(u[n−1])) / (u[n] − u[n−1]),
// the mean of the curve between two samples. Every curve is odd, has slope 1 at 0 and never passes ±1, so neither
// does the mean. In double, with the portable maths (the same bits everywhere).
//  - Hard: u, flat at ±1.
//  - Soft: u − 4u³/27 up to |u| = 1.5, where it reaches 1 with slope 0 (a cubic that bends from 0).
//  - Tanh: tanh u, 1 its asymptote.
//  - Knee: straight up to T = 10^(−knee/20), then a quadratic bend, u − (u − T)²/(4(1 − T)), reaching 1 with slope 0
//    at 2 − T; flat beyond. Knee 0 dB is Hard.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Clipper
{
    internal enum ClipShape { Hard, Soft, Tanh, Knee }

    internal struct ClipCurve
    {
        public ClipShape Shape;
        /// <summary>Knee: where the bend starts (0 … 1, 1 is Hard).</summary>
        public double T;
        /// <summary>Knee: 1/(4(1 − T)), and F at the end of the bend.</summary>
        double q, endF;

        public static ClipCurve Make(ClipShape shape, double kneeDb)
        {
            var c = new ClipCurve { Shape = shape, T = 1 };
            if (shape == ClipShape.Knee)
            {
                c.T = Math.Min(1, PortableMath.Pow(10, -Math.Max(0, kneeDb) / 20));
                if (c.T >= 1 - 1e-9)
                {
                    c.Shape = ClipShape.Hard;
                    c.T = 1;
                }
                else
                {
                    c.q = 1 / (4 * (1 - c.T));
                    double e = 2 - c.T;
                    c.endF = e * e / 2 - (1 - c.T) * (1 - c.T) * 2 / 3;
                }
            }
            return c;
        }

        /// <summary>|u| above this, the curve has left the straight line by more than 1 % (the "clipped" share).</summary>
        public double BendsAbove => Shape switch
        {
            ClipShape.Hard => 1,
            ClipShape.Knee => T,
            ClipShape.Soft => 0.2598,     // 4u²/27 = 1 %
            _ => 0.1736,                  // tanh(u)/u = 0.99
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double F(double u)
        {
            double a = Math.Abs(u);
            double y;
            switch (Shape)
            {
                case ClipShape.Hard:
                    y = a < 1 ? a : 1;
                    break;
                case ClipShape.Soft:
                    y = a < 1.5 ? a - a * a * a * (4.0 / 27) : 1;
                    break;
                case ClipShape.Tanh:
                    return PortableMath.Tanh(u);
                default:
                    if (a <= T) y = a;
                    else if (a < 2 - T) { double d = a - T; y = a - d * d * q; }
                    else y = 1;
                    break;
            }
            return u < 0 ? -y : y;
        }

        /// <summary>The antiderivative, zero at 0 (even).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Integral(double u)
        {
            double a = Math.Abs(u);
            switch (Shape)
            {
                case ClipShape.Hard:
                    return a < 1 ? a * a / 2 : a - 0.5;
                case ClipShape.Soft:
                    return a < 1.5 ? a * a / 2 - a * a * a * a / 27 : a - 0.5625;
                case ClipShape.Tanh:
                    // ln cosh u = |u| + ln(1 + e^(−2|u|)) − ln 2, stable for large |u|.
                    return a + PortableMath.Log(1 + PortableMath.Exp(-2 * a)) - 0.6931471805599453;
                default:
                    if (a <= T) return a * a / 2;
                    if (a < 2 - T)
                    {
                        double d = a - T;
                        return a * a / 2 - d * d * d * q / 3;
                    }
                    return endF + (a - (2 - T));
            }
        }
    }

    /// <summary>First-order ADAA around a <see cref="ClipCurve"/>, one stream.</summary>
    internal struct ClipAdaa
    {
        double u1, f1;
        bool primed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Process(in ClipCurve c, double u)
        {
            double F = c.Integral(u);
            double y;
            double d = u - u1;
            if (!primed)
            {
                primed = true;
                y = c.F(u);
            }
            else if (Math.Abs(d) > 1e-5)
                y = (F - f1) / d;
            else
                y = c.F(0.5 * (u + u1));
            u1 = u;
            f1 = F;
            return y;
        }

        public void Reset()
        {
            primed = false;
            u1 = f1 = 0;
        }
    }
}
