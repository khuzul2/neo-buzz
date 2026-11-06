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

using System;
using System.Runtime.CompilerServices;

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
