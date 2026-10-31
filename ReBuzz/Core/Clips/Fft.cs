#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): an in-place radix-2 FFT for the
// phase vocoder and the analysis. Pure (ReBuzz.SessionTests links this file).

using System;

namespace ReBuzz.Core.Clips
{
    /// <summary>A complex FFT of one power-of-two size: twiddles and bit reversal made once, no allocation after.</summary>
    internal sealed class Fft
    {
        readonly int size;
        readonly int[] reverse;
        readonly double[] cos, sin;

        public Fft(int size)
        {
            if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentException("A power of two.", nameof(size));
            this.size = size;
            int bits = 0;
            while ((1 << bits) < size) bits++;
            reverse = new int[size];
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
                reverse[i] = r;
            }
            cos = new double[size / 2];
            sin = new double[size / 2];
            for (int i = 0; i < size / 2; i++)
            {
                cos[i] = Math.Cos(-2 * Math.PI * i / size);
                sin[i] = Math.Sin(-2 * Math.PI * i / size);
            }
        }

        public int Size => size;

        /// <summary>Forward (or, with <paramref name="inverse"/>, inverse and scaled by 1/size) in place.</summary>
        public void Transform(double[] re, double[] im, bool inverse = false)
        {
            for (int i = 0; i < size; i++)
            {
                int j = reverse[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            double sign = inverse ? -1 : 1;
            for (int len = 2; len <= size; len <<= 1)
            {
                int half = len >> 1, step = size / len;
                for (int start = 0; start < size; start += len)
                {
                    for (int k = 0; k < half; k++)
                    {
                        double wr = cos[k * step], wi = sign * sin[k * step];
                        int a = start + k, b = a + half;
                        double tr = re[b] * wr - im[b] * wi;
                        double ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
            if (inverse)
            {
                double scale = 1.0 / size;
                for (int i = 0; i < size; i++)
                {
                    re[i] *= scale;
                    im[i] *= scale;
                }
            }
        }
    }
}
