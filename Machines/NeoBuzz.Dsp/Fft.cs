#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): in-place radix-2 complex FFT, for building
// wavetable mipmaps and for analysis in tests and editors. Not for the audio thread.

using System;

namespace NeoBuzz.Dsp
{
    internal static class Fft
    {
        /// <summary>In-place FFT of a power-of-two length; <paramref name="inverse"/> scales by 1/n.</summary>
        public static void Transform(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            if (n != im.Length || (n & (n - 1)) != 0) throw new ArgumentException("power-of-two length expected");
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                double wr = PortableMath.Cos(ang), wi = PortableMath.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    int half = len >> 1;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = a + half;
                        double tr = re[b] * cr - im[b] * ci;
                        double ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
            if (inverse)
            {
                double s = 1.0 / n;
                for (int i = 0; i < n; i++)
                {
                    re[i] *= s;
                    im[i] *= s;
                }
            }
        }

        /// <summary>Magnitude spectrum (bins 0 … n/2) of a real signal, rectangular window, amplitude-normalised (a full-scale sine reads 1).</summary>
        public static double[] Magnitudes(float[] x, int offset, int n)
        {
            var re = new double[n];
            var im = new double[n];
            for (int i = 0; i < n; i++) re[i] = x[offset + i];
            Transform(re, im, false);
            var mag = new double[n / 2 + 1];
            for (int k = 0; k <= n / 2; k++) mag[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * (k == 0 || k == n / 2 ? 1.0 : 2.0) / n;
            return mag;
        }
    }
}
