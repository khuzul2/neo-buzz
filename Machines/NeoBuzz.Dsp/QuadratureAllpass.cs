#nullable disable
// Neo machines shared DSP (docs/specs/mastering-tools.md §4.2): a 90° phase-difference pair (a Hilbert
// transformer made of allpasses). The polyphase halfband's two allpass branches (IirHalfbandDesign, after
// L. de Soras' HIIR: sections (c + z⁻²)/(1 + c z⁻²), even coefficients on one branch, odd on the other
// behind a sample) differ in phase by 0 below a quarter of the rate and by π above; shifted up by a quarter
// of the rate (z → jz, so z⁻² → −z⁻²), the branches differ by 90° over the whole band but its ends. Both
// outputs are allpasses: their magnitudes are 1, so a = |x|·cos(φ), b = |x|·sin(φ) of the same phase φ,
// and √(a² + b²) is the signal's envelope without the ripple of a rectifier (TransientSplit), and b is the
// signal turned by 90° (Neo Imager's Recover Sides). Designed for 20 Hz up (the band's lower edge as the
// transition width), 60 dB of halfband attenuation: 90° within ±0.11° from 20 Hz to 20 kHz at 44.1 and
// 48 kHz with 12 coefficients, ±0.13° at 96 kHz with 13. Double precision: the lowest sections' poles sit
// close to the unit circle. Allocation-free after construction; IEEE basic operations only.
// M3: Block runs a stereo pair a block at a time, a section at a time over the block (each section's recursion
// reaches two samples back, so the samples overlap instead of waiting for the section before), the two
// channels in the lanes of a Vector128<double> and the two branches side by side: every value comes from the
// same operations on the same inputs as Process, so the same bits.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace NeoBuzz.Dsp
{
    internal sealed class QuadratureAllpass
    {
        /// <summary>The design's lower edge (Hz): 90° holds from here to as far below Nyquist.</summary>
        public const double LowEdgeHz = 20;

        /// <summary>The halfband attenuation designed for (dB): the phase difference's error is about 10^(−A/20)·2 radians.</summary>
        public const double AttenuationDb = 60;

        readonly double[] ca, cb;
        // Two samples of history per section: x[n−1], x[n−2], y[n−1], y[n−2], kept as pairs.
        readonly double[] xa1, xa2, ya1, ya2, xb1, xb2, yb1, yb2;
        double prev;

        public QuadratureAllpass(double sampleRate) : this(Coefficients(sampleRate))
        {
        }

        public QuadratureAllpass(double[] coefficients)
        {
            int n = coefficients.Length;
            ca = new double[(n + 1) / 2];
            cb = new double[n / 2];
            for (int i = 0; i < n; i++)
            {
                if (i % 2 == 0) ca[i / 2] = coefficients[i];
                else cb[i / 2] = coefficients[i];
            }
            xa1 = new double[ca.Length];
            xa2 = new double[ca.Length];
            ya1 = new double[ca.Length];
            ya2 = new double[ca.Length];
            xb1 = new double[cb.Length];
            xb2 = new double[cb.Length];
            yb1 = new double[cb.Length];
            yb2 = new double[cb.Length];
        }

        /// <summary>The halfband coefficients for a sample rate (portable maths: the same on every platform).</summary>
        public static double[] Coefficients(double sampleRate) =>
            new IirHalfbandDesign(AttenuationDb, Math.Min(0.24, LowEdgeHz / sampleRate)).Coefs;

        /// <summary>Allpass sections in the two branches (the order of the design).</summary>
        public int Sections => ca.Length + cb.Length;

        public void Reset()
        {
            Array.Clear(xa1);
            Array.Clear(xa2);
            Array.Clear(ya1);
            Array.Clear(ya2);
            Array.Clear(xb1);
            Array.Clear(xb2);
            Array.Clear(yb1);
            Array.Clear(yb2);
            prev = 0;
        }

        /// <summary>
        /// One sample: <paramref name="a"/> (the in-phase branch) and <paramref name="b"/> (the quadrature branch, 90° behind
        /// <paramref name="a"/> at positive frequencies), each an allpass of <paramref name="x"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(double x, out double a, out double b)
        {
            a = Chain(x, ca, xa1, xa2, ya1, ya2);
            b = Chain(prev, cb, xb1, xb2, yb1, yb2);
            prev = x;
        }

        // Sections (c − z⁻²)/(1 − c z⁻²): y[n] = c·(x[n] + y[n−2]) − x[n−2].
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double Chain(double s, double[] c, double[] x1, double[] x2, double[] y1, double[] y2)
        {
            for (int i = 0; i < c.Length; i++)
            {
                double y = c[i] * (s + y2[i]) - x2[i];
                x2[i] = x1[i];
                x1[i] = s;
                y2[i] = y1[i];
                y1[i] = y;
                s = y;
            }
            return s;
        }

        /// <summary>
        /// A stereo pair (<paramref name="left"/> and <paramref name="right"/>, the same design), <paramref name="n"/> samples:
        /// <paramref name="a"/> holds the inputs interleaved (left, right, left, …) and receives the in-phase branch's outputs;
        /// <paramref name="b"/> receives the quadrature branch's. The same bits as <see cref="Process"/> one sample at a time
        /// on each channel, with <see cref="Flush"/> after sample <paramref name="flushFirst"/> and every 64 samples after it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Block(QuadratureAllpass left, QuadratureAllpass right, double[] a, double[] b, int n, int flushFirst)
        {
            if (left.ca.Length != right.ca.Length || left.cb.Length != right.cb.Length) throw new ArgumentException("Two designs.");
            if (a.Length < 2 * n || b.Length < 2 * n) throw new ArgumentOutOfRangeException(nameof(n));
            // The quadrature branch's input: the input a sample later (its delay flushed where Flush would have).
            b[0] = left.prev;
            b[1] = right.prev;
            for (int j = 1; j < n; j++)
            {
                double pl = a[2 * j - 2], pr = a[2 * j - 1];
                if (Flushes(j - 1, flushFirst))
                {
                    pl = DspMath.Flush(pl);
                    pr = DspMath.Flush(pr);
                }
                b[2 * j] = pl;
                b[2 * j + 1] = pr;
            }
            if (n > 0)
            {
                double pl = a[2 * n - 2], pr = a[2 * n - 1];
                left.prev = Flushes(n - 1, flushFirst) ? DspMath.Flush(pl) : pl;
                right.prev = Flushes(n - 1, flushFirst) ? DspMath.Flush(pr) : pr;
            }
            int both = Math.Min(left.ca.Length, left.cb.Length);
            for (int i = 0; i < both; i++) Two(left, right, i, a, b, n, flushFirst);
            for (int i = both; i < left.ca.Length; i++) One(left.ca[i], left.xa1, left.xa2, left.ya1, left.ya2, right.xa1, right.xa2, right.ya1, right.ya2, i, a, n, flushFirst);
            for (int i = both; i < left.cb.Length; i++) One(left.cb[i], left.xb1, left.xb2, left.yb1, left.yb2, right.xb1, right.xb2, right.yb1, right.yb2, i, b, n, flushFirst);
        }

        static bool Flushes(int j, int first) => j >= first && ((j - first) & 63) == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<double> FlushV(Vector128<double> v) =>
            Vector128.ConditionalSelect(Vector128.GreaterThan(v, Vector128.Create(-1e-30)) & Vector128.LessThan(v, Vector128.Create(1e-30)), Vector128<double>.Zero, v);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<double> Pair(double[] l, double[] r, int i) => Vector128.Create(l[i], r[i]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Store(Vector128<double> v, double[] l, double[] r, int i)
        {
            l[i] = v.GetElement(0);
            r[i] = v.GetElement(1);
        }

        /// <summary>Section <paramref name="i"/> of both branches over the block.</summary>
        static void Two(QuadratureAllpass L, QuadratureAllpass R, int i, double[] a, double[] b, int n, int flushFirst)
        {
            var ca = Vector128.Create(L.ca[i]);
            var cb = Vector128.Create(L.cb[i]);
            Vector128<double> ax1 = Pair(L.xa1, R.xa1, i), ax2 = Pair(L.xa2, R.xa2, i), ay1 = Pair(L.ya1, R.ya1, i), ay2 = Pair(L.ya2, R.ya2, i);
            Vector128<double> bx1 = Pair(L.xb1, R.xb1, i), bx2 = Pair(L.xb2, R.xb2, i), by1 = Pair(L.yb1, R.yb1, i), by2 = Pair(L.yb2, R.yb2, i);
            ref double ra = ref MemoryMarshal.GetArrayDataReference(a);
            ref double rb = ref MemoryMarshal.GetArrayDataReference(b);
            int next = flushFirst;
            for (int j = 0; j < n; j++)
            {
                var sa = Vector128.LoadUnsafe(ref ra, (nuint)(2 * j));
                var sb = Vector128.LoadUnsafe(ref rb, (nuint)(2 * j));
                var ya = ca * (sa + ay2) - ax2;
                var yb = cb * (sb + by2) - bx2;
                ax2 = ax1;
                ax1 = sa;
                ay2 = ay1;
                ay1 = ya;
                bx2 = bx1;
                bx1 = sb;
                by2 = by1;
                by1 = yb;
                ya.StoreUnsafe(ref ra, (nuint)(2 * j));
                yb.StoreUnsafe(ref rb, (nuint)(2 * j));
                if (j == next)
                {
                    ax1 = FlushV(ax1); ax2 = FlushV(ax2); ay1 = FlushV(ay1); ay2 = FlushV(ay2);
                    bx1 = FlushV(bx1); bx2 = FlushV(bx2); by1 = FlushV(by1); by2 = FlushV(by2);
                    next += 64;
                }
            }
            Store(ax1, L.xa1, R.xa1, i); Store(ax2, L.xa2, R.xa2, i); Store(ay1, L.ya1, R.ya1, i); Store(ay2, L.ya2, R.ya2, i);
            Store(bx1, L.xb1, R.xb1, i); Store(bx2, L.xb2, R.xb2, i); Store(by1, L.yb1, R.yb1, i); Store(by2, L.yb2, R.yb2, i);
        }

        /// <summary>Section <paramref name="i"/> of one branch over the block (the longer branch's last).</summary>
        static void One(double c, double[] lx1, double[] lx2, double[] ly1, double[] ly2, double[] rx1, double[] rx2, double[] ry1, double[] ry2,
            int i, double[] buf, int n, int flushFirst)
        {
            var cv = Vector128.Create(c);
            Vector128<double> x1 = Pair(lx1, rx1, i), x2 = Pair(lx2, rx2, i), y1 = Pair(ly1, ry1, i), y2 = Pair(ly2, ry2, i);
            ref double rf = ref MemoryMarshal.GetArrayDataReference(buf);
            int next = flushFirst;
            for (int j = 0; j < n; j++)
            {
                var s = Vector128.LoadUnsafe(ref rf, (nuint)(2 * j));
                var y = cv * (s + y2) - x2;
                x2 = x1;
                x1 = s;
                y2 = y1;
                y1 = y;
                y.StoreUnsafe(ref rf, (nuint)(2 * j));
                if (j == next)
                {
                    x1 = FlushV(x1); x2 = FlushV(x2); y1 = FlushV(y1); y2 = FlushV(y2);
                    next += 64;
                }
            }
            Store(x1, lx1, rx1, i); Store(x2, lx2, rx2, i); Store(y1, ly1, ry1, i); Store(y2, ly2, ry2, i);
        }

        /// <summary>Flushes state under 1e-30 to zero (call on a grid of the caller's own samples, not per host block, so the output doesn't depend on the blocks).</summary>
        public void Flush()
        {
            FlushAll(xa1);
            FlushAll(xa2);
            FlushAll(ya1);
            FlushAll(ya2);
            FlushAll(xb1);
            FlushAll(xb2);
            FlushAll(yb1);
            FlushAll(yb2);
            prev = DspMath.Flush(prev);
        }

        static void FlushAll(double[] v)
        {
            for (int i = 0; i < v.Length; i++) v[i] = DspMath.Flush(v[i]);
        }

        /// <summary>The two branches' complex responses at <paramref name="hz"/> (tests): their phase difference is 90°.</summary>
        public static (double ReA, double ImA, double ReB, double ImB) Response(double[] coefficients, double hz, double sampleRate)
        {
            double w = 2 * Math.PI * hz / sampleRate;
            // z⁻¹ and z⁻² on the unit circle.
            double c1 = PortableMath.Cos(w), s1 = -PortableMath.Sin(w);
            double c2 = PortableMath.Cos(2 * w), s2 = -PortableMath.Sin(2 * w);
            double ar = 1, ai = 0, br = c1, bi = s1;
            for (int i = 0; i < coefficients.Length; i++)
            {
                double c = coefficients[i];
                // (c − z⁻²)/(1 − c z⁻²)
                double nr = c - c2, ni = -s2, dr = 1 - c * c2, di = -c * s2;
                double d = dr * dr + di * di;
                double hr = (nr * dr + ni * di) / d, hi = (ni * dr - nr * di) / d;
                if (i % 2 == 0)
                {
                    double t = ar * hr - ai * hi;
                    ai = ar * hi + ai * hr;
                    ar = t;
                }
                else
                {
                    double t = br * hr - bi * hi;
                    bi = br * hi + bi * hr;
                    br = t;
                }
            }
            return (ar, ai, br, bi);
        }
    }
}
