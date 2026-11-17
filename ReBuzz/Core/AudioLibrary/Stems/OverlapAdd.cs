#nullable disable
// Fork (docs/specs/stem-separation.md): cutting a long recording into the fixed segments a stem model takes and putting the answers
// together, as Demucs's apply_model does: a segment every (1 - overlap) of its length, each answer weighted by a triangle (rising, then
// falling, so the seams are cross-faded where two segments overlap), and every sample divided by the sum of the weights it received.
// The scratch (segment in, segment out, weights) is made once for a run; a segment costs no allocation. Pure: ReBuzz.SessionTests links this file.

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ReBuzz.Core.AudioLibrary.Stems
{
    /// <summary>What the chunker asks of a model: stereo in, <see cref="Stems"/> stereo signals out, for one segment.</summary>
    internal interface IChunkModel
    {
        /// <summary>The segment's length in samples (the model's rate).</summary>
        int Segment { get; }

        /// <summary>The number of stereo signals a segment gives.</summary>
        int Stems { get; }

        /// <summary>
        /// One segment. <paramref name="output"/> is laid out [stem][channel][sample] (Stems * 2 * Segment floats); every one of them is
        /// written. Throws <see cref="OperationCanceledException"/> when <paramref name="cancel"/> is cancelled.
        /// </summary>
        void Process(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> output, CancellationToken cancel);
    }

    internal static class OverlapAdd
    {
        /// <summary>The segments of a recording of <paramref name="length"/> samples: how many, and the stride between starts.</summary>
        public static (int Count, int Stride) Plan(int length, int segment, double overlap)
        {
            int stride = Math.Max(1, (int)((1 - Math.Clamp(overlap, 0, 0.9)) * segment));
            int count = 0;
            for (long offset = 0; offset < length; offset += stride) count++;
            return (count, stride);
        }

        /// <summary>The weight of sample <paramref name="i"/> of a segment (Demucs's triangle: 1 up to the middle, then down to 1, over the peak).</summary>
        public static float Weight(int i, int segment)
        {
            int rise = segment / 2;
            int peak = Math.Max(1, Math.Max(rise, segment - rise));
            int w = i < rise ? i + 1 : segment - i;
            return (float)w / peak;
        }

        /// <summary>
        /// Runs <paramref name="model"/> over the recording and gives its stems in <paramref name="results"/> ([stem * 2 + channel], each
        /// <paramref name="length"/> long and cleared by this call). <paramref name="progress"/> gets the fraction done after each segment.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Run(IChunkModel model, float[] left, float[] right, int length, double overlap, float[][] results,
            Action<double> progress, CancellationToken cancel)
        {
            int segment = model.Segment, stems = model.Stems;
            if (results.Length < stems * 2) throw new ArgumentException("A result for every stem and channel.", nameof(results));
            foreach (var r in results) Array.Clear(r, 0, Math.Min(length, r.Length));
            var (count, stride) = Plan(length, segment, overlap);
            var inL = new float[segment];
            var inR = new float[segment];
            var output = new float[stems * 2 * segment];
            var weight = new float[segment];
            for (int i = 0; i < segment; i++) weight[i] = Weight(i, segment);
            var sum = new float[length];
            for (int c = 0; c < count; c++)
            {
                cancel.ThrowIfCancellationRequested();
                long offset = (long)c * stride;
                int have = (int)Math.Min(segment, length - offset);
                Array.Clear(inL, 0, segment);
                Array.Clear(inR, 0, segment);
                Array.Copy(left, offset, inL, 0, have);
                Array.Copy(right, offset, inR, 0, have);
                model.Process(inL, inR, output, cancel);
                for (int s = 0; s < stems * 2; s++)
                {
                    var target = results[s];
                    int from = s * segment;
                    for (int i = 0; i < have; i++) target[offset + i] += output[from + i] * weight[i];
                }
                for (int i = 0; i < have; i++) sum[offset + i] += weight[i];
                progress?.Invoke((c + 1.0) / count);
            }
            for (int s = 0; s < stems * 2; s++)
            {
                var target = results[s];
                for (int i = 0; i < length; i++) target[i] = sum[i] > 0 ? target[i] / sum[i] : 0f;
            }
        }
    }
}
