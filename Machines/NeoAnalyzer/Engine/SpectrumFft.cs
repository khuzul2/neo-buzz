#nullable disable
// Neo Analyzer (docs/specs/machines.md §5): the FFT the spectrum runs on the audio thread. The
// same transform as the analysis library's (NeoBuzz.Analysis Fft: forward, unscaled, radix 2,
// in place), equal to it within rounding, but laid out for speed: each stage's twiddles are
// contiguous, the first two stages need no multiplications, and the butterflies of the later
// stages run on SIMD vectors. No allocation after construction.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Analyzer
{
    internal sealed class SpectrumFft
    {
        readonly int size;
        readonly int[] swaps;          // pairs (i, j), i < j, of the bit-reversal permutation
        readonly double[] twr, twi;    // stage with half-length h: twiddles e^(−iπk/h) at h − 1 … 2h − 2

        public SpectrumFft(int size)
        {
            if (size < 8 || (size & (size - 1)) != 0) throw new ArgumentException("A power of two, at least 8.", nameof(size));
            this.size = size;
            int bits = 0;
            while ((1 << bits) < size) bits++;
            int count = 0;
            var pairs = new int[size];
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
                if (r > i)
                {
                    pairs[count++] = i;
                    pairs[count++] = r;
                }
            }
            swaps = pairs.AsSpan(0, count).ToArray();
            twr = new double[size];
            twi = new double[size];
            for (int h = 1; h < size; h <<= 1)
                for (int k = 0; k < h; k++)
                {
                    twr[h - 1 + k] = Math.Cos(-Math.PI * k / h);
                    twi[h - 1 + k] = Math.Sin(-Math.PI * k / h);
                }
        }

        public int Size => size;

        /// <summary>The forward transform in place (as NeoBuzz.Analysis Fft.Transform with inverse false).</summary>
        // Fully optimised at its first compile (the warm-up's), so the audio thread never compiles an on-stack replacement of its loops (AudioWarmUp).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Forward(double[] re, double[] im)
        {
            var s = swaps;
            for (int p = 0; p < s.Length; p += 2)
            {
                int i = s[p], j = s[p + 1];
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
            // Length 2: w = 1.
            for (int a = 0; a < size; a += 2)
            {
                int b = a + 1;
                double tr = re[b], ti = im[b];
                re[b] = re[a] - tr;
                im[b] = im[a] - ti;
                re[a] += tr;
                im[a] += ti;
            }
            // Length 4: w = 1, −i.
            for (int a = 0; a < size; a += 4)
            {
                int b = a + 2;
                double tr = re[b], ti = im[b];
                re[b] = re[a] - tr;
                im[b] = im[a] - ti;
                re[a] += tr;
                im[a] += ti;
                a++;
                b++;
                tr = im[b];
                ti = -re[b];
                re[b] = re[a] - tr;
                im[b] = im[a] - ti;
                re[a] += tr;
                im[a] += ti;
                a--;
            }
            int v = Vector<double>.Count;
            for (int half = 4; half < size; half <<= 1)
            {
                int len = half << 1, tw = half - 1;
                bool simd = Vector.IsHardwareAccelerated && half % v == 0;
                for (int start = 0; start < size; start += len)
                {
                    int k = 0;
                    if (simd)
                    {
                        for (; k < half; k += v)
                        {
                            int a = start + k, b = a + half;
                            var wr = new Vector<double>(twr, tw + k);
                            var wi = new Vector<double>(twi, tw + k);
                            var br = new Vector<double>(re, b);
                            var bi = new Vector<double>(im, b);
                            var ar = new Vector<double>(re, a);
                            var ai = new Vector<double>(im, a);
                            var tr = br * wr - bi * wi;
                            var ti = br * wi + bi * wr;
                            (ar - tr).CopyTo(re, b);
                            (ai - ti).CopyTo(im, b);
                            (ar + tr).CopyTo(re, a);
                            (ai + ti).CopyTo(im, a);
                        }
                    }
                    for (; k < half; k++)
                    {
                        int a = start + k, b = a + half;
                        double wr = twr[tw + k], wi = twi[tw + k];
                        double tr = re[b] * wr - im[b] * wi;
                        double ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }
    }
}
