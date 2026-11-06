#nullable disable
// Neo machines shared DSP (docs/specs/mastering-tools.md §4.2): a short-time Fourier transform with
// weighted overlap-add (WOLA) for spectral machines (Neo Spectral, M7).
//  - Frames of N samples (a power of two) every hop H = N/4, √Hann analysis and synthesis windows (the
//    periodic Hann's square root: their product, the Hann, overlap-adds to 2 at a hop of N/4, so the
//    synthesis scale 1/(2N) with the unscaled inverse transform gives back the input).
//  - Stereo in one complex transform: left as the real part, right as the imaginary. A processor sees the
//    packed spectrum (ISpectralProcessor); a gain that is real and the same for bin k and N − k (ApplyGains)
//    acts on both channels at once; Unpack and Pack separate and join the channels' own spectra.
//  - Hops fall on fixed samples of the transform's own count (rings for the input and the overlap-add), so
//    the output doesn't depend on how the host splits the audio. A frame runs when its last sample has
//    come in, and the output is the input N − 1 samples back (the least a frame of N allows): Latency.
// Allocation-free after construction; the windows from the portable maths, the transforms ComplexFft's:
// the same bits on every platform.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>What a <see cref="Stft"/> does to each frame's spectrum.</summary>
    internal interface ISpectralProcessor
    {
        /// <summary>
        /// One frame's packed spectrum (left + i·right), <paramref name="n"/> bins, in place. Bin k holds frequency
        /// k·fs/n (k ≤ n/2) and −(n − k)·fs/n above.
        /// </summary>
        void Process(float[] re, float[] im, int n);
    }

    internal sealed class Stft
    {
        public readonly int N, Hop;
        readonly int inMask, outMask;
        readonly ComplexFft fft;
        readonly float[] window, synthesis;
        readonly float[] inL, inR, accL, accR, re, im;
        long count;

        /// <param name="size">The frame N: a power of two, at least 16; the hop is N/4.</param>
        public Stft(int size)
        {
            if (size < 16 || (size & (size - 1)) != 0) throw new ArgumentException("A power of two, at least 16.", nameof(size));
            N = size;
            Hop = size / 4;
            fft = new ComplexFft(N);
            window = new float[N];
            synthesis = new float[N];
            double scale = 1.0 / (2.0 * N);
            for (int i = 0; i < N; i++)
            {
                double w = Math.Sqrt(0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * i / N));
                window[i] = (float)w;
                synthesis[i] = (float)(w * scale);
            }
            inL = new float[N];
            inR = new float[N];
            inMask = N - 1;
            accL = new float[2 * N];
            accR = new float[2 * N];
            outMask = 2 * N - 1;
            re = new float[N];
            im = new float[N];
        }

        /// <summary>Samples from an input to the output: N − 1.</summary>
        public int Latency => N - 1;

        public void Reset()
        {
            Array.Clear(inL);
            Array.Clear(inR);
            Array.Clear(accL);
            Array.Clear(accR);
            count = 0;
        }

        /// <summary>
        /// <paramref name="n"/> samples of <paramref name="l"/> and <paramref name="r"/> from <paramref name="offset"/> through the
        /// transform and <paramref name="processor"/>, in place: the output is the processed input <see cref="Latency"/> samples back.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int offset, int n, ISpectralProcessor processor)
        {
            int done = 0;
            while (done < n)
            {
                // Up to the next hop boundary: the frame ending at sample t runs when (t + 1) is a multiple of the hop.
                int toHop = Hop - (int)(count % Hop);
                int run = Math.Min(n - done, toHop);
                for (int i = 0; i < run; i++)
                {
                    int w = (int)((count + i) & inMask);
                    inL[w] = l[offset + done + i];
                    inR[w] = r[offset + done + i];
                }
                long last = count + run - 1;
                if (run == toHop) Frame(last, processor);
                // Out: the time N − 1 back of each sample, cleared for its next use (2N later).
                for (int i = 0; i < run; i++)
                {
                    int o = (int)((count + i - (N - 1)) & outMask);
                    l[offset + done + i] = accL[o];
                    r[offset + done + i] = accR[o];
                    accL[o] = 0;
                    accR[o] = 0;
                }
                count += run;
                done += run;
            }
        }

        /// <summary>The frame of the N samples ending at <paramref name="last"/>: windowed, transformed, processed, back, windowed, added.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Frame(long last, ISpectralProcessor processor)
        {
            long first = last - (N - 1);
            for (int i = 0; i < N; i++)
            {
                int s = (int)((first + i) & inMask);
                re[i] = inL[s] * window[i];
                im[i] = inR[s] * window[i];
            }
            fft.Transform(re, im, false);
            processor?.Process(re, im, N);
            fft.Transform(re, im, true);
            for (int i = 0; i < N; i++)
            {
                int o = (int)((first + i) & outMask);
                accL[o] += re[i] * synthesis[i];
                accR[o] += im[i] * synthesis[i];
            }
        }

        /// <summary>
        /// A real gain per bin 0 … n/2 (<paramref name="gains"/>, n/2 + 1 values) on a packed spectrum: bin k and its mirror n − k
        /// get the same gain, so both channels are scaled alike.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ApplyGains(float[] re, float[] im, int n, float[] gains)
        {
            re[0] *= gains[0];
            im[0] *= gains[0];
            for (int k = 1; k < n / 2; k++)
            {
                float g = gains[k];
                re[k] *= g;
                im[k] *= g;
                re[n - k] *= g;
                im[n - k] *= g;
            }
            re[n / 2] *= gains[n / 2];
            im[n / 2] *= gains[n / 2];
        }

        /// <summary>
        /// The two channels' own spectra, bins 0 … n/2, from a packed one: Z[k] = L[k] + i·R[k], with L and R the spectra of
        /// real signals, so L[k] = (Z[k] + Z*[n − k])/2 and R[k] = (Z[k] − Z*[n − k])/(2i).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Unpack(float[] re, float[] im, int n, float[] lRe, float[] lIm, float[] rRe, float[] rIm)
        {
            for (int k = 0; k <= n / 2; k++)
            {
                int m = (n - k) & (n - 1);
                float zr = re[k], zi = im[k], cr = re[m], ci = im[m];
                lRe[k] = 0.5f * (zr + cr);
                lIm[k] = 0.5f * (zi - ci);
                rRe[k] = 0.5f * (zi + ci);
                rIm[k] = 0.5f * (cr - zr);
            }
        }

        /// <summary>The packed spectrum from the two channels' spectra, bins 0 … n/2 (the inverse of <see cref="Unpack"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Pack(float[] lRe, float[] lIm, float[] rRe, float[] rIm, int n, float[] re, float[] im)
        {
            for (int k = 0; k <= n / 2; k++)
            {
                // Z[k] = L[k] + i·R[k]; Z[n − k] = L*[k] + i·R*[k].
                re[k] = lRe[k] - rIm[k];
                im[k] = lIm[k] + rRe[k];
                if (k > 0 && k < n / 2)
                {
                    re[n - k] = lRe[k] + rIm[k];
                    im[n - k] = -lIm[k] + rRe[k];
                }
            }
        }
    }
}
