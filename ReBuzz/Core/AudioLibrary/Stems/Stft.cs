#nullable disable
// Fork (docs/specs/stem-separation.md): the short-time Fourier transform the stem models need, in our own code. The networks run as ONNX
// graphs without the transform (no complex numbers in them), so the host does what torch.stft and torch.istft do: a periodic Hann window,
// frames every <hop> samples, the one-sided spectrum, optionally scaled by 1/sqrt(size) ("normalized", as Demucs wants it), and the inverse
// by windowed overlap-add divided by the sum of the squared windows. Off the audio thread; allocates only on its first call of a size.
// Pure: ReBuzz.SessionTests links this file.

using System;
using System.Runtime.CompilerServices;
using ReBuzz.Core.Clips;

namespace ReBuzz.Core.AudioLibrary.Stems
{
    internal sealed class Stft
    {
        readonly Fft fft;
        readonly double[] window;
        readonly double[] re, im;
        readonly double forwardScale, inverseScale;
        float[] envelope = Array.Empty<float>();

        public Stft(int size, int hop, bool normalized)
        {
            Size = size;
            Hop = hop;
            Bins = size / 2 + 1;
            fft = new Fft(size);
            window = new double[size];
            for (int i = 0; i < size; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size);   // periodic, like torch.hann_window
            re = new double[size];
            im = new double[size];
            forwardScale = normalized ? 1.0 / Math.Sqrt(size) : 1.0;
            inverseScale = normalized ? Math.Sqrt(size) : 1.0;
        }

        public int Size { get; }
        public int Hop { get; }
        /// <summary>Size / 2 + 1.</summary>
        public int Bins { get; }

        /// <summary>The frames a signal of <paramref name="paddedLength"/> samples (already padded) has.</summary>
        public int Frames(int paddedLength) => paddedLength < Size ? 0 : (paddedLength - Size) / Hop + 1;

        /// <summary>
        /// Frames <paramref name="firstFrame"/> to firstFrame + frames - 1 of <paramref name="padded"/>, bins 0 to keepBins - 1, written as
        /// re[bin * frames + frame] and im[...].
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Analyze(ReadOnlySpan<float> padded, int firstFrame, int frames, int keepBins, Span<float> outRe, Span<float> outIm)
        {
            keepBins = Math.Min(keepBins, Bins);
            for (int f = 0; f < frames; f++)
            {
                int start = (firstFrame + f) * Hop;
                for (int i = 0; i < Size; i++)
                {
                    re[i] = (start + i < padded.Length ? padded[start + i] : 0f) * window[i];
                    im[i] = 0;
                }
                fft.Transform(re, im);
                for (int b = 0; b < keepBins; b++)
                {
                    outRe[b * frames + f] = (float)(re[b] * forwardScale);
                    outIm[b * frames + f] = (float)(im[b] * forwardScale);
                }
            }
        }

        /// <summary>
        /// The inverse: <paramref name="frames"/> frames of spectrum, frame f read from re[bin * frameStride + f + frameOffset] (frames
        /// outside 0 to frameStride - 1 and bins from inBins up to the Nyquist are 0) overlap-added into <paramref name="output"/> (Size + Hop * (frames - 1) samples, which it clears first) and divided by
        /// the sum of the squared windows. The caller trims the centre padding.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Synthesize(ReadOnlySpan<float> inRe, ReadOnlySpan<float> inIm, int frames, int inBins, int frameStride, int frameOffset, Span<float> output)
        {
            int length = Size + Hop * (frames - 1);
            if (output.Length < length) throw new ArgumentException("The output is too short.", nameof(output));
            if (envelope.Length < length) envelope = new float[length];
            output.Slice(0, length).Clear();
            Array.Clear(envelope, 0, length);
            inBins = Math.Min(inBins, Bins);
            int half = Size / 2;
            for (int f = 0; f < frames; f++)
            {
                for (int b = 0; b < Bins; b++)
                {
                    double r = 0, i = 0;
                    int src = f + frameOffset;
                    if (b < inBins && src >= 0 && src < frameStride)
                    {
                        int at = b * frameStride + src;
                        r = inRe[at] * inverseScale;
                        i = inIm[at] * inverseScale;
                    }
                    if (b == 0 || b == half) i = 0;                  // a real signal has no imaginary DC or Nyquist
                    re[b] = r;
                    im[b] = i;
                    if (b > 0 && b < half)
                    {
                        re[Size - b] = r;
                        im[Size - b] = -i;
                    }
                }
                fft.Transform(re, im, inverse: true);
                int start = f * Hop;
                for (int k = 0; k < Size; k++)
                {
                    output[start + k] += (float)(re[k] * window[k]);
                    envelope[start + k] += (float)(window[k] * window[k]);
                }
            }
            for (int k = 0; k < length; k++)
                if (envelope[k] > 1e-11f) output[k] /= envelope[k];
        }

        /// <summary>
        /// torch's reflect padding (and, past one reflection, the continued mirror): <paramref name="destination"/> holds left samples, the
        /// source, then right samples.
        /// </summary>
        public static void PadReflect(ReadOnlySpan<float> source, Span<float> destination, int left, int right)
        {
            int n = source.Length;
            int total = left + n + right;
            if (destination.Length < total) throw new ArgumentException("The destination is too short.", nameof(destination));
            if (n == 0) { destination.Slice(0, total).Clear(); return; }
            if (n == 1) { destination.Slice(0, total).Fill(source[0]); return; }
            int period = 2 * (n - 1);
            for (int i = 0; i < total; i++)
            {
                int j = i - left;
                int m = ((j % period) + period) % period;
                destination[i] = source[m < n ? m : period - m];
            }
        }
    }
}
