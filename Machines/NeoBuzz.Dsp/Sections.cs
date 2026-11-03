#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4, §5 mix and master batch): a second-order
// section in double precision for equalisers and crossovers, Butterworth cascades, and a linear
// ramp for gains that must land exactly on their value.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>
    /// One second-order section as a topology-preserving (TPT) state-variable filter in double
    /// precision, after A. Simper: output = M0·input + M1·band + M2·low, the bilinear transform of
    /// H(s) = (M0 s² + (M0 K + M1) s + M0 + M2) / (s² + K s + 1), with s normalised to the
    /// prewarped frequency G = tan(π f / fs). Exact at low frequencies and well behaved when its
    /// coefficients change every sample, so parameter moves glide without zipper noise.
    /// </summary>
    internal struct SvfSection
    {
        double ic1, ic2;
        double a1, a2, a3;

        public double G, K, M0, M1, M2;

        /// <summary>Prewarped frequency for <paramref name="hz"/> (clamped below Nyquist).</summary>
        public static double Prewarp(double hz, double sampleRate) =>
            PortableMath.Tan(Math.PI * Math.Clamp(hz, 0.01, sampleRate * 0.4999) / sampleRate);

        public void Set(double g, double k, double m0, double m1, double m2)
        {
            G = g;
            K = k;
            M0 = m0;
            M1 = m1;
            M2 = m2;
            a1 = 1.0 / (1.0 + g * (g + k));
            a2 = g * a1;
            a3 = g * a2;
        }

        /// <summary>Passes the input unchanged (keeps the state).</summary>
        public void SetIdentity() => Set(0.5, 1, 1, 0, 0);

        public void SetLowPass(double g, double q) => Set(g, 1 / q, 0, 0, 1);

        public void SetHighPass(double g, double q) => Set(g, 1 / q, 1, -1 / q, -1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Process(double v0)
        {
            double v3 = v0 - ic2;
            double v1 = a1 * ic1 + a2 * v3;
            double v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2 * v1 - ic1;
            ic2 = 2 * v2 - ic2;
            return M0 * v0 + M1 * v1 + M2 * v2;
        }

        public void Reset() { ic1 = ic2 = 0; }

        /// <summary>Flushes denormal state (call once per block).</summary>
        public void Flush()
        {
            ic1 = DspMath.Flush(ic1);
            ic2 = DspMath.Flush(ic2);
        }

        /// <summary>The section's exact magnitude response at <paramref name="hz"/> (editors and tests).</summary>
        public double Magnitude(double hz, double sampleRate) => Magnitude(G, K, M0, M1, M2, hz, sampleRate);

        public static double Magnitude(double g, double k, double m0, double m1, double m2, double hz, double sampleRate)
        {
            // On the unit circle the bilinear transform's s is j·tan(π f / fs) / g.
            double f = Math.Clamp(hz, 0, sampleRate * 0.5);
            double w = f >= sampleRate * 0.5 ? 1e12 : PortableMath.Tan(Math.PI * f / sampleRate) / g;
            double w2 = w * w;
            double n2 = m0, n1 = m0 * k + m1, n0 = m0 + m2;
            double nr = n0 - n2 * w2, ni = n1 * w;
            double dr = 1 - w2, di = k * w;
            return Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
        }
    }

    /// <summary>Butterworth cascades of second-order sections (12, 24 and 48 dB/oct).</summary>
    internal static class Butterworth
    {
        static readonly double[][] Qs =
        {
            SectionQs(2),
            SectionQs(4),
            SectionQs(8),
        };

        /// <summary>The section Qs of an even-order Butterworth filter, lowest first: 1 / (2 cos θ), θ = (2i − 1)π / 2n.</summary>
        public static double[] SectionQs(int order)
        {
            var q = new double[order / 2];
            for (int i = 1; i <= order / 2; i++) q[i - 1] = 1 / (2 * PortableMath.Cos((2 * i - 1) * Math.PI / (2 * order)));
            Array.Sort(q);
            return q;
        }

        /// <summary>Section Qs for 12, 24 or 48 dB/oct (slope index 0, 1, 2).</summary>
        public static double[] ForSlope(int slope) => Qs[Math.Clamp(slope, 0, 2)];
    }

    /// <summary>
    /// Linear-ramp parameter smoother: moves to a new target in a set time and lands on it
    /// exactly, so a gain of 1 or −1 ends bit-exact (a one-pole smoother stops a few ulps short).
    /// </summary>
    internal struct LinearRamp
    {
        float step;
        int left, length;

        public float Value;
        public float Target;

        public void Setup(double seconds, double sampleRate) => length = Math.Max(1, (int)Math.Round(seconds * sampleRate));

        public void Reset(float v)
        {
            Value = Target = v;
            left = 0;
        }

        public void SetTarget(float target)
        {
            if (target == Target) return;
            Target = target;
            int n = Math.Max(1, length);
            left = n;
            step = (target - Value) / n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Next()
        {
            if (left > 0)
            {
                if (--left == 0) Value = Target;
                else Value += step;
            }
            return Value;
        }

        public bool Settled => left == 0;

        /// <summary>Settled at <paramref name="v"/>.</summary>
        public bool At(float v) => left == 0 && Value == v;
    }
}
