#nullable disable
// Neo machines shared DSP (docs/specs/mastering-tools.md §5.1, §5.2): Neo Clipper's curves (and Neo Limiter v2's soft
// clip, the Knee curve), normalised to a ceiling of 1, each with its
// antiderivative for first-order antiderivative anti-aliasing (ADAA): y[n] = (F(u[n]) − F(u[n−1])) / (u[n] − u[n−1]),
// the mean of the curve between two samples. Every curve is odd, has slope 1 at 0 and never passes ±1, so neither
// does the mean. In double, with the portable maths (the same bits everywhere).
//  - Hard: u, flat at ±1.
//  - Soft: u − 4u³/27 up to |u| = 1.5, where it reaches 1 with slope 0 (a cubic that bends from 0).
//  - Tanh: tanh u, 1 its asymptote.
//  - Knee: straight up to T = 10^(−knee/20), then a quadratic bend, u − (u − T)²/(4(1 − T)), reaching 1 with slope 0
//    at 2 − T; flat beyond. Knee 0 dB is Hard.
// M3: Hard, Soft and Knee without branches (b = min(|u|, top), the bend from max(b − T, 0), the antiderivative's
// straight part a − b added), so a block runs four or eight samples at a time in vectors; one sample at a time runs
// the same operations in the same order, so both give the same bits (and the same bits on every platform). Tanh
// stays one sample at a time.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace NeoBuzz.Dsp
{
    internal enum ClipShape { Hard, Soft, Tanh, Knee }

    internal struct ClipCurve
    {
        public ClipShape Shape;
        /// <summary>Knee: where the bend starts (0 … 1, 1 is Hard).</summary>
        public double T;
        /// <summary>Where the curve reaches 1 (Hard 1, Soft 1.5, Knee 2 − T); the Knee's bend 1/(4(1 − T)) (0 for Hard) and its third.</summary>
        double top, q, q3;

        public static ClipCurve Make(ClipShape shape, double kneeDb)
        {
            var c = new ClipCurve { Shape = shape, T = 1, top = shape == ClipShape.Soft ? 1.5 : 1 };
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
                    c.q3 = c.q / 3;
                    c.top = 2 - c.T;
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

        const double Soft3 = 4.0 / 27, Soft4 = 1.0 / 27;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double F(double u)
        {
            if (Shape == ClipShape.Tanh) return PortableMath.Tanh(u);
            double a = Math.Abs(u);
            double b = Math.Min(a, top);
            double y;
            if (Shape == ClipShape.Soft) y = Math.Min(b - b * b * b * Soft3, 1.0);
            else
            {
                double d = Math.Max(b - T, 0.0);
                y = Math.Min(b - d * d * q, 1.0);
            }
            return u < 0 ? -y : y;
        }

        /// <summary>The antiderivative, zero at 0 (even).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Integral(double u)
        {
            double a = Math.Abs(u);
            // ln cosh u = |u| + ln(1 + e^(−2|u|)) − ln 2, stable for large |u|.
            if (Shape == ClipShape.Tanh) return a + PortableMath.Log(1 + PortableMath.Exp(-2 * a)) - 0.6931471805599453;
            double b = Math.Min(a, top);
            if (Shape == ClipShape.Soft) return (b * b * 0.5 - b * b * b * b * Soft4) + (a - b);
            double d = Math.Max(b - T, 0.0);
            return (b * b * 0.5 - d * d * d * q3) + (a - b);
        }

        /// <summary><see cref="F"/> on two values at once (not Tanh): the same operations, so the same bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Vector128<double> F(Vector128<double> u)
        {
            var a = Vector128.Abs(u);
            var b = Vector128.Min(a, Vector128.Create(top));
            var one = Vector128.Create(1.0);
            Vector128<double> y;
            if (Shape == ClipShape.Soft) y = Vector128.Min(b - b * b * b * Vector128.Create(Soft3), one);
            else
            {
                var d = Vector128.Max(b - Vector128.Create(T), Vector128<double>.Zero);
                y = Vector128.Min(b - d * d * Vector128.Create(q), one);
            }
            return Vector128.ConditionalSelect(Vector128.LessThan(u, Vector128<double>.Zero), -y, y);
        }

        /// <summary><see cref="F"/> on four values at once (not Tanh): the same operations, so the same bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Vector256<double> F(Vector256<double> u)
        {
            var a = Vector256.Abs(u);
            var b = Vector256.Min(a, Vector256.Create(top));
            var one = Vector256.Create(1.0);
            Vector256<double> y;
            if (Shape == ClipShape.Soft) y = Vector256.Min(b - b * b * b * Vector256.Create(Soft3), one);
            else
            {
                var d = Vector256.Max(b - Vector256.Create(T), Vector256<double>.Zero);
                y = Vector256.Min(b - d * d * Vector256.Create(q), one);
            }
            return Vector256.ConditionalSelect(Vector256.LessThan(u, Vector256<double>.Zero), -y, y);
        }

        /// <summary><see cref="Integral"/> on four values at once (not Tanh).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Vector256<double> Integral(Vector256<double> u)
        {
            var a = Vector256.Abs(u);
            var b = Vector256.Min(a, Vector256.Create(top));
            var half = Vector256.Create(0.5);
            if (Shape == ClipShape.Soft) return (b * b * half - b * b * b * b * Vector256.Create(Soft4)) + (a - b);
            var d = Vector256.Max(b - Vector256.Create(T), Vector256<double>.Zero);
            return (b * b * half - d * d * d * Vector256.Create(q3)) + (a - b);
        }

        /// <summary><see cref="Integral"/> on two values at once (not Tanh).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Vector128<double> Integral(Vector128<double> u)
        {
            var a = Vector128.Abs(u);
            var b = Vector128.Min(a, Vector128.Create(top));
            var half = Vector128.Create(0.5);
            if (Shape == ClipShape.Soft) return (b * b * half - b * b * b * b * Vector128.Create(Soft4)) + (a - b);
            var d = Vector128.Max(b - Vector128.Create(T), Vector128<double>.Zero);
            return (b * b * half - d * d * d * Vector128.Create(q3)) + (a - b);
        }
    }

    /// <summary>First-order ADAA around a <see cref="ClipCurve"/>, one stream.</summary>
    internal struct ClipAdaa
    {
        const double Close = 1e-5;
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
            else if (Math.Abs(d) > Close)
                y = (F - f1) / d;
            else
                y = c.F(0.5 * (u + u1));
            u1 = u;
            f1 = F;
            return y;
        }

        /// <summary>The scratch a block of <paramref name="n"/> samples needs (<see cref="Process(in ClipCurve, float[], int, double[], double[])"/>).</summary>
        public static int ScratchFor(int n) => n + 2;

        /// <summary>
        /// <paramref name="n"/> samples of <paramref name="x"/> in place, the same bits as <see cref="Process(in ClipCurve, double)"/> one
        /// at a time; Hard, Soft and Knee eight at a time in Vector256&lt;double&gt; where the machine has it, four in Vector128&lt;double&gt;
        /// (every lane the same operations: the same bits). <paramref name="us"/> and <paramref name="fs"/>:
        /// scratch of <see cref="ScratchFor"/>(n) (shared between streams processed one after the other).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(in ClipCurve c, float[] x, int n, double[] us, double[] fs)
        {
            int i = 0;
            if (n > 0 && !primed) x[i++] = (float)Process(in c, x[0]);
            if (c.Shape == ClipShape.Tanh || n - i < 4)
            {
                for (; i < n; i++) x[i] = (float)Process(in c, x[i]);
                return;
            }
            // us[k], fs[k]: u and F of sample i − 1 + k (us[0], fs[0] the previous one).
            int m = n - i;
            if (us.Length < m + 2 || fs.Length < m + 2 || x.Length < n) throw new ArgumentOutOfRangeException(nameof(us));
            us[0] = u1;
            fs[0] = f1;
            ref float rx = ref MemoryMarshal.GetArrayDataReference(x);
            ref double ru = ref MemoryMarshal.GetArrayDataReference(us);
            ref double rf = ref MemoryMarshal.GetArrayDataReference(fs);
            int k = 0;
            if (Vector256.IsHardwareAccelerated)
                for (; k + 8 <= m; k += 8)
                {
                    var v = Vector256.LoadUnsafe(ref rx, (nuint)(i + k));
                    var lo = Vector256.WidenLower(v);
                    var hi = Vector256.WidenUpper(v);
                    lo.StoreUnsafe(ref ru, (nuint)(k + 1));
                    hi.StoreUnsafe(ref ru, (nuint)(k + 5));
                    c.Integral(lo).StoreUnsafe(ref rf, (nuint)(k + 1));
                    c.Integral(hi).StoreUnsafe(ref rf, (nuint)(k + 5));
                }
            for (; k + 4 <= m; k += 4)
            {
                var v = Vector128.LoadUnsafe(ref rx, (nuint)(i + k));
                var lo = Vector128.WidenLower(v);
                var hi = Vector128.WidenUpper(v);
                lo.StoreUnsafe(ref ru, (nuint)(k + 1));
                hi.StoreUnsafe(ref ru, (nuint)(k + 3));
                c.Integral(lo).StoreUnsafe(ref rf, (nuint)(k + 1));
                c.Integral(hi).StoreUnsafe(ref rf, (nuint)(k + 3));
            }
            for (; k < m; k++)
            {
                us[k + 1] = x[i + k];
                fs[k + 1] = c.Integral(us[k + 1]);
            }
            k = 0;
            if (Vector256.IsHardwareAccelerated)
                for (; k + 8 <= m; k += 8)
                {
                    var lo = Mean4(in c, ref ru, ref rf, k);
                    var hi = Mean4(in c, ref ru, ref rf, k + 4);
                    Vector256.Narrow(lo, hi).StoreUnsafe(ref rx, (nuint)(i + k));
                }
            for (; k + 4 <= m; k += 4)
            {
                var lo = Mean(in c, ref ru, ref rf, k);
                var hi = Mean(in c, ref ru, ref rf, k + 2);
                Vector128.Narrow(lo, hi).StoreUnsafe(ref rx, (nuint)(i + k));
            }
            for (; k < m; k++)
            {
                double u = us[k + 1], up = us[k], d = u - up;
                x[i + k] = (float)(Math.Abs(d) > Close ? (fs[k + 1] - fs[k]) / d : c.F(0.5 * (u + up)));
            }
            u1 = us[m];
            f1 = fs[m];
        }

        /// <summary>Samples k and k + 1 of the block: the mean of the curve since the sample before, or the curve at the midpoint where they are close.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<double> Mean(in ClipCurve c, ref double ru, ref double rf, int k)
        {
            var u = Vector128.LoadUnsafe(ref ru, (nuint)(k + 1));
            var up = Vector128.LoadUnsafe(ref ru, (nuint)k);
            var d = u - up;
            var mean = (Vector128.LoadUnsafe(ref rf, (nuint)(k + 1)) - Vector128.LoadUnsafe(ref rf, (nuint)k)) / d;
            var far = Vector128.GreaterThan(Vector128.Abs(d), Vector128.Create(Close));
            if (far == Vector128<double>.AllBitsSet) return mean;
            return Vector128.ConditionalSelect(far, mean, c.F(Vector128.Create(0.5) * (u + up)));
        }

        /// <summary><see cref="Mean"/> on samples k … k + 3 (Vector256, where the machine has it: each lane the same operations).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector256<double> Mean4(in ClipCurve c, ref double ru, ref double rf, int k)
        {
            var u = Vector256.LoadUnsafe(ref ru, (nuint)(k + 1));
            var up = Vector256.LoadUnsafe(ref ru, (nuint)k);
            var d = u - up;
            var mean = (Vector256.LoadUnsafe(ref rf, (nuint)(k + 1)) - Vector256.LoadUnsafe(ref rf, (nuint)k)) / d;
            var far = Vector256.GreaterThan(Vector256.Abs(d), Vector256.Create(Close));
            if (far == Vector256<double>.AllBitsSet) return mean;
            return Vector256.ConditionalSelect(far, mean, c.F(Vector256.Create(0.5) * (u + up)));
        }

        public void Reset()
        {
            primed = false;
            u1 = f1 = 0;
        }
    }
}
