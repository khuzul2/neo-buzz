#nullable disable
// Fork (docs/specs/neo-audio.md, NA1): the import's sample-rate converter, our own code: a windowed sinc (Kaiser window, beta 9, 32 zero
// crossings each side at the lower of the two rates, so more than 90 dB of the band above the lower Nyquist is rejected), the kernel
// tabulated at 1/512 of a sample and interpolated. Run once at import, off the audio thread. Pure: ReBuzz.SessionTests links this file.

using System;
using System.Runtime.CompilerServices;

namespace ReBuzz.Core.AudioLibrary
{
    internal static class Resampler
    {
        public const int ZeroCrossings = 32;
        const int Steps = 512;
        const double Beta = 9.0;

        /// <summary>The audio at <paramref name="to"/> Hz (the same array when the rates agree). Samples outside the audio are 0.</summary>
        public static float[] Resample(float[] x, int from, int to)
        {
            if (x == null) return Array.Empty<float>();
            if (from <= 0 || to <= 0 || from == to || x.Length == 0) return x;
            double ratio = (double)to / from;
            int length = (int)Math.Max(1, Math.Round(x.Length * ratio));
            double cutoff = Math.Min(1.0, ratio);                      // of the source's Nyquist
            double half = ZeroCrossings / cutoff;                      // the kernel's half width, in source samples
            var table = Kernel(cutoff, half, out int entries);
            var y = new float[length];
            Run(x, y, ratio, half, table, entries);
            return y;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Run(float[] x, float[] y, double ratio, double half, float[] table, int entries)
        {
            int n = x.Length;
            double scale = Steps;
            for (int o = 0; o < y.Length; o++)
            {
                double t = o / ratio;
                int first = (int)Math.Ceiling(t - half), last = (int)Math.Floor(t + half);
                if (first < 0) first = 0;
                if (last > n - 1) last = n - 1;
                double sum = 0;
                for (int k = first; k <= last; k++)
                {
                    double p = Math.Abs(k - t) * scale;
                    int i = (int)p;
                    if (i + 1 >= entries) continue;
                    double f = p - i;
                    sum += x[k] * (table[i] + (table[i + 1] - table[i]) * f);
                }
                y[o] = (float)sum;
            }
        }

        /// <summary>h(d) = cutoff * sinc(cutoff * d) * kaiser(d / half), for d from 0 to half in steps of 1/Steps.</summary>
        static float[] Kernel(double cutoff, double half, out int entries)
        {
            entries = (int)Math.Ceiling(half * Steps) + 2;
            var h = new float[entries];
            double i0Beta = Bessel0(Beta);
            for (int i = 0; i < entries; i++)
            {
                double d = i / (double)Steps;
                if (d > half) { h[i] = 0; continue; }
                double u = d / half;
                double window = Bessel0(Beta * Math.Sqrt(Math.Max(0, 1 - u * u))) / i0Beta;
                double a = Math.PI * cutoff * d;
                double sinc = d == 0 ? 1.0 : Math.Sin(a) / a;
                h[i] = (float)(cutoff * sinc * window);
            }
            return h;
        }

        static double Bessel0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int k = 1; k < 60; k++)
            {
                term *= q / ((double)k * k);
                sum += term;
                if (term < 1e-16 * sum) break;
            }
            return sum;
        }
    }
}
