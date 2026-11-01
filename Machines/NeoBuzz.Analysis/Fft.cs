#nullable disable
// Neo analysis library (docs/specs/mcp-server.md §7): the FFT the spectral measures share. An
// in-place radix-2 complex transform with its twiddles and bit reversal made once, a periodic
// Hann window, and a short-time transform that hands each frame's power spectrum to a callback.

using System;

namespace NeoBuzz.Analysis
{
    /// <summary>A complex FFT of one power-of-two size; no allocation after construction.</summary>
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

        /// <summary>The smallest power of two of at least <paramref name="n"/>.</summary>
        public static int AtLeast(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        /// <summary>The power of two nearest <paramref name="n"/> on a log scale.</summary>
        public static int Nearest(double n)
        {
            int p = AtLeast((int)Math.Ceiling(Math.Max(1, n)));
            return p > 1 && n / (p / 2) < p / n ? p / 2 : p;
        }

        /// <summary>A periodic Hann window: its spectrum is exactly [−¼, ½, −¼] bins.</summary>
        public static double[] Hann(int n)
        {
            var w = new double[n];
            for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
            return w;
        }

        /// <summary>
        /// Separates the transform of <c>a + i·b</c> (two real signals in one FFT) into bin
        /// <paramref name="k"/> of each: A = (Z[k] + conj Z[−k]) / 2, B = (Z[k] − conj Z[−k]) / 2i.
        /// </summary>
        public static void Split(double[] re, double[] im, int k, out double ar, out double ai, out double br, out double bi)
        {
            int n = re.Length, nk = (n - k) & (n - 1);
            ar = 0.5 * (re[k] + re[nk]);
            ai = 0.5 * (im[k] - im[nk]);
            br = 0.5 * (im[k] + im[nk]);
            bi = 0.5 * (re[nk] - re[k]);
        }
    }

    /// <summary>
    /// Short-time power spectra: Hann frames of <c>size</c> every <c>hop</c> samples, the
    /// channels' mean power per bin (0 … size/2), scaled as <see cref="Welch"/> is, so a frame's
    /// bins sum to its mean square.
    /// </summary>
    internal static class Stft
    {
        /// <summary>The number of frames for <paramref name="n"/> samples (at least one when there is audio).</summary>
        public static int Frames(int n, int size, int hop) => n <= 0 ? 0 : n <= size ? 1 : (n - size) / hop + 1;

        public static void Run(float[] left, float[] right, int size, int hop, Action<int, double[]> frame)
        {
            int n = left.Length;
            bool stereo = right != null && !ReferenceEquals(right, left);
            var fft = new Fft(size);
            var w = Fft.Hann(size);
            double w2 = 0;
            foreach (double v in w) w2 += v * v;
            int half = size / 2;
            var re = new double[size];
            var im = new double[size];
            var power = new double[half + 1];
            int frames = Frames(n, size, hop);
            for (int f = 0; f < frames; f++)
            {
                int at = f * hop;
                for (int i = 0; i < size; i++)
                {
                    int j = at + i;
                    re[i] = j < n ? left[j] * w[i] : 0;
                    im[i] = stereo && j < n ? right[j] * w[i] : 0;
                }
                fft.Transform(re, im);
                for (int k = 0; k <= half; k++)
                {
                    double c = (k == 0 || k == half ? 1.0 : 2.0) / (size * w2);
                    if (stereo)
                    {
                        Fft.Split(re, im, k, out double lr, out double li, out double rr, out double ri);
                        power[k] = 0.5 * c * (lr * lr + li * li + rr * rr + ri * ri);
                    }
                    else power[k] = c * (re[k] * re[k] + im[k] * im[k]);
                }
                frame(f, power);
            }
        }
    }
}
