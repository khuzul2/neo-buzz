#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4; moved from Neo Multiband, docs/specs/mastering-tools.md
// §4.2): the complex FFT for the audio thread, in single precision: a radix-2 transform whose twiddles and
// bit reversal are made once, then no allocation. The butterflies of the stages from four points up run
// four or eight bins at once (Vector128 or Vector256, element by element: the same operations as one bin
// at a time, so the same bits on every CPU, docs/specs/machines.md §4); the twiddles come from the
// portable maths. TransformPart runs the same transform a bounded number of butterflies at a time, so a
// long one can be spread over audio blocks (SteppedFirDesign). Fft (Fft.cs) is the double-precision one
// for analysis and design off the audio thread.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace NeoBuzz.Dsp
{
    internal sealed class ComplexFft
    {
        readonly int[] swapA, swapB, rev;
        // Stage with half-length h (h = 1, 2, 4 … n/2) keeps its twiddles at [h, 2h): e^(−iπk/h), k < h.
        readonly float[] wr, wi, wiNeg;

        public ComplexFft(int size)
        {
            if (size < 8 || (size & (size - 1)) != 0) throw new ArgumentException("A power of two, at least 8.", nameof(size));
            Size = size;
            int bits = 0;
            while ((1 << bits) < size) bits++;
            int pairs = 0;
            rev = new int[size];
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
                rev[i] = r;
                if (r > i) pairs++;
            }
            swapA = new int[pairs];
            swapB = new int[pairs];
            for (int i = 0, p = 0; i < size; i++)
                if (rev[i] > i)
                {
                    swapA[p] = i;
                    swapB[p] = rev[i];
                    p++;
                }
            wr = new float[size];
            wi = new float[size];
            wiNeg = new float[size];
            for (int h = 1; h < size; h <<= 1)
                for (int k = 0; k < h; k++)
                {
                    double angle = -Math.PI * k / h;
                    wr[h + k] = (float)PortableMath.Cos(angle);
                    wi[h + k] = (float)PortableMath.Sin(angle);
                    wiNeg[h + k] = -wi[h + k];
                }
        }

        public int Size { get; }

        /// <summary>Where the bit reversal puts index <paramref name="i"/>: data written there needs no reversal (TransformPart from pass 1).</summary>
        public int Reversed(int i) => rev[i];

        /// <summary>In place, unscaled: the inverse is the conjugate transform (scale by 1/n yourself).</summary>
        // Fully optimised at its first compile (the warm-up's), so the audio thread never compiles an on-stack replacement of its loops (AudioWarmUp).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Transform(float[] re, float[] im, bool inverse)
        {
            int n = Size;
            for (int p = 0; p < swapA.Length; p++)
            {
                int a = swapA[p], b = swapB[p];
                (re[a], re[b]) = (re[b], re[a]);
                (im[a], im[b]) = (im[b], im[a]);
            }
            // Two-point butterflies (the twiddle is 1).
            for (int a = 0; a < n; a += 2)
            {
                float br = re[a + 1], bi = im[a + 1];
                re[a + 1] = re[a] - br;
                im[a + 1] = im[a] - bi;
                re[a] += br;
                im[a] += bi;
            }
            // Four-point: the twiddles are 1 and ∓i.
            for (int a = 0; a < n; a += 4)
            {
                float br = re[a + 2], bi = im[a + 2];
                re[a + 2] = re[a] - br;
                im[a + 2] = im[a] - bi;
                re[a] += br;
                im[a] += bi;
                // ×(−i) forward: (x + iy)(−i) = y − ix; inverse ×(+i): −y + ix.
                float cr = re[a + 3], ci = im[a + 3];
                float tr = inverse ? -ci : ci, ti = inverse ? cr : -cr;
                re[a + 3] = re[a + 1] - tr;
                im[a + 3] = im[a + 1] - ti;
                re[a + 1] += tr;
                im[a + 1] += ti;
            }
            var sin = inverse ? wiNeg : wi;
            for (int h = 4; h < n; h <<= 1)
                for (int start = 0; start < n; start += 2 * h)
                    Stage(re, im, start, h, sin, 0, h);
        }

        /// <summary>
        /// The transform <see cref="Transform"/> makes, run in parts: each call does about <paramref name="budget"/> units of
        /// work and returns true once the transform is complete. A unit is about one butterfly of the later stages (the
        /// vector loops), and the passes that cost more a butterfly count more (measured on x64): a swap of the bit
        /// reversal 3, a butterfly of the two- and four-point passes 2, of the stage of half-length 4 4 (a call for every
        /// four), of 8 2. The same operations on the same values in the same order within each pass (the butterflies of a
        /// pass touch separate pairs), so the same bits as one call to Transform, however the work is split. Start each
        /// transform with a new <paramref name="at"/> (Pass 1 when the data was written in bit-reversed order, see
        /// <see cref="Reversed"/>), and don't touch <paramref name="re"/>, <paramref name="im"/> between the parts.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool TransformPart(float[] re, float[] im, bool inverse, ref FftProgress at, int budget)
        {
            int n = Size, half = n / 2;
            budget = Math.Max(1, budget);
            while (budget > 0)
            {
                int from = at.At, end;
                if (at.Pass == 0)
                {
                    end = Math.Min(swapA.Length, from + Math.Max(1, budget / 3));
                    for (int p = from; p < end; p++)
                    {
                        int a = swapA[p], b = swapB[p];
                        (re[a], re[b]) = (re[b], re[a]);
                        (im[a], im[b]) = (im[b], im[a]);
                    }
                    budget -= 3 * (end - from);
                    if (end < swapA.Length) at.At = end;
                    else at = new FftProgress { Pass = 1 };
                }
                else if (at.Pass == 1)
                {
                    // Two-point butterflies: pair q is (2q, 2q + 1).
                    end = Math.Min(half, from + Math.Max(1, budget / 2));
                    for (int q = from; q < end; q++)
                    {
                        int a = 2 * q;
                        float br = re[a + 1], bi = im[a + 1];
                        re[a + 1] = re[a] - br;
                        im[a + 1] = im[a] - bi;
                        re[a] += br;
                        im[a] += bi;
                    }
                    budget -= 2 * (end - from);
                    if (end < half) at.At = end;
                    else at = new FftProgress { Pass = 2 };
                }
                else if (at.Pass == 2)
                {
                    // Four-point groups (two butterflies each): group q is 4q … 4q + 3.
                    int groups = n / 4;
                    end = Math.Min(groups, from + Math.Max(1, budget / 4));
                    for (int q = from; q < end; q++)
                    {
                        int a = 4 * q;
                        float br = re[a + 2], bi = im[a + 2];
                        re[a + 2] = re[a] - br;
                        im[a + 2] = im[a] - bi;
                        re[a] += br;
                        im[a] += bi;
                        float cr = re[a + 3], ci = im[a + 3];
                        float tr = inverse ? -ci : ci, ti = inverse ? cr : -cr;
                        re[a + 3] = re[a + 1] - tr;
                        im[a + 3] = im[a + 1] - ti;
                        re[a + 1] += tr;
                        im[a + 1] += ti;
                    }
                    budget -= 4 * (end - from);
                    if (end < groups) at.At = end;
                    else at = new FftProgress { Pass = 3 };
                }
                else
                {
                    // The stage of half-length h: butterfly b is k = b mod h of the group starting at (b / h) · 2h.
                    int h = 4 << (at.Pass - 3);
                    if (h >= n) return true;
                    int cost = h == 4 ? 4 : h == 8 ? 2 : 1;
                    var sin = inverse ? wiNeg : wi;
                    // Whole groups of eight butterflies (from is a multiple of 8 here), so the vector loops run as in Transform.
                    end = Math.Min(half, from + ((Math.Max(1, budget / cost) + 7) & ~7));
                    for (int b = from; b < end;)
                    {
                        int start = b / h * 2 * h, k = b % h, to = Math.Min(h, k + end - b);
                        Stage(re, im, start, h, sin, k, to);
                        b += to - k;
                    }
                    budget -= cost * (end - from);
                    if (end < half) at.At = end;
                    else at = new FftProgress { Pass = at.Pass + 1 };
                }
            }
            return at.Pass >= 3 && (4 << (at.Pass - 3)) >= n;
        }

        /// <summary>Butterflies <paramref name="from"/> … <paramref name="to"/> − 1 of the group at <paramref name="start"/> in the stage of half-length <paramref name="h"/> (whole vectors: multiples of 8 from h = 8 up).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Stage(float[] re, float[] im, int start, int h, float[] sin, int from, int to)
        {
            int k = from;
            if (Vector256.IsHardwareAccelerated && h >= 8)
            {
                for (; k < to; k += 8)
                {
                    int a = start + k, b = a + h;
                    var cr = Vector256.Create(wr, h + k);
                    var ci = Vector256.Create(sin, h + k);
                    var xr = Vector256.Create(re, b);
                    var xi = Vector256.Create(im, b);
                    var tr = xr * cr - xi * ci;
                    var ti = xr * ci + xi * cr;
                    var ar = Vector256.Create(re, a);
                    var ai = Vector256.Create(im, a);
                    (ar - tr).CopyTo(re, b);
                    (ai - ti).CopyTo(im, b);
                    (ar + tr).CopyTo(re, a);
                    (ai + ti).CopyTo(im, a);
                }
                return;
            }
            if (Vector128.IsHardwareAccelerated)
            {
                for (; k < to; k += 4)
                {
                    int a = start + k, b = a + h;
                    var cr = Vector128.Create(wr, h + k);
                    var ci = Vector128.Create(sin, h + k);
                    var xr = Vector128.Create(re, b);
                    var xi = Vector128.Create(im, b);
                    var tr = xr * cr - xi * ci;
                    var ti = xr * ci + xi * cr;
                    var ar = Vector128.Create(re, a);
                    var ai = Vector128.Create(im, a);
                    (ar - tr).CopyTo(re, b);
                    (ai - ti).CopyTo(im, b);
                    (ar + tr).CopyTo(re, a);
                    (ai + ti).CopyTo(im, a);
                }
                return;
            }
            for (; k < to; k++)
            {
                int a = start + k, b = a + h;
                float cr = wr[h + k], ci = sin[h + k];
                float xr = re[b], xi = im[b];
                float tr = xr * cr - xi * ci;
                float ti = xr * ci + xi * cr;
                float ar = re[a], ai = im[a];
                re[b] = ar - tr;
                im[b] = ai - ti;
                re[a] = ar + tr;
                im[a] = ai + ti;
            }
        }

        /// <summary>
        /// acc += x · h, complex, bin by bin (the convolution's multiply-accumulate). Each bin's sum
        /// runs in the order of the calls, whatever the vector width.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void MultiplyAdd(float[] accRe, float[] accIm, float[] xRe, float[] xIm, float[] hRe, float[] hIm, int n)
        {
            int k = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                for (; k + 8 <= n; k += 8)
                {
                    var xr = Vector256.Create(xRe, k);
                    var xi = Vector256.Create(xIm, k);
                    var hr = Vector256.Create(hRe, k);
                    var hi = Vector256.Create(hIm, k);
                    (Vector256.Create(accRe, k) + (xr * hr - xi * hi)).CopyTo(accRe, k);
                    (Vector256.Create(accIm, k) + (xr * hi + xi * hr)).CopyTo(accIm, k);
                }
            }
            else if (Vector128.IsHardwareAccelerated)
            {
                for (; k + 4 <= n; k += 4)
                {
                    var xr = Vector128.Create(xRe, k);
                    var xi = Vector128.Create(xIm, k);
                    var hr = Vector128.Create(hRe, k);
                    var hi = Vector128.Create(hIm, k);
                    (Vector128.Create(accRe, k) + (xr * hr - xi * hi)).CopyTo(accRe, k);
                    (Vector128.Create(accIm, k) + (xr * hi + xi * hr)).CopyTo(accIm, k);
                }
            }
            for (; k < n; k++)
            {
                float xr = xRe[k], xi = xIm[k], hr = hRe[k], hi = hIm[k];
                accRe[k] = accRe[k] + (xr * hr - xi * hi);
                accIm[k] = accIm[k] + (xr * hi + xi * hr);
            }
        }
    }

    /// <summary>How far a transform run in parts (<see cref="ComplexFft.TransformPart"/>) has got: its pass (the bit reversal, two-point, four-point, then each stage) and the place in it.</summary>
    internal struct FftProgress
    {
        public int Pass, At;
    }
}
