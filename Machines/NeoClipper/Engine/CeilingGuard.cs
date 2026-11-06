#nullable disable
// Neo Clipper (docs/specs/mastering-tools.md §5.1): the ceiling guard. The oversampled curves never pass the
// ceiling, but the downsampling filter rings past it after a hard clip (by up to 2 dB on drums driven 18 dB):
// clamping those samples would alias as a 1× clipper does (the alias floor went from −94 to −42 dB). Instead a
// short lookahead gain (Neo Limiter's construction on sample peaks, linked): each sample's needed gain
// min(1, ceiling/|x|), its sliding minimum over the hold, averaged by two box filters whose lengths sum to the
// hold plus one, so every gain averaged is at most what the delayed sample needs: no sample leaves above the
// ceiling, and the gain moves smoothly (modulation, not aliasing). A last clamp catches float rounding. The hold
// is 5 ms: on a sine clipped 12 dB into a 3 dB knee at 4×, 0.5 ms left an alias floor of −64 dB, 1 ms −67, 2 ms
// −71, 5 ms −94 (the gain barely moves within a few cycles). The audio is delayed by the hold − 1 samples.
// Allocation-free after construction; double sums, recomputed every 256 samples of its own count (no drift, and
// the same bits whatever the blocks).

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Clipper
{
    internal sealed class CeilingGuard
    {
        public const double HoldSeconds = 0.005;

        readonly DelayLineF audioL, audioR, ceil;
        readonly double[] dqVal, box1, box2;
        readonly long[] dqIdx;
        readonly int mask;
        int hold, b1, b2, head, count;
        long index;
        double sum1, sum2;

        /// <param name="maxHold">The most hold any rate needs, samples.</param>
        public CeilingGuard(int maxHold)
        {
            int cap = 1;
            while (cap < maxHold + 4) cap <<= 1;
            mask = cap - 1;
            dqVal = new double[cap];
            dqIdx = new long[cap];
            box1 = new double[cap];
            box2 = new double[cap];
            audioL = new DelayLineF(maxHold + 2);
            audioR = new DelayLineF(maxHold + 2);
            ceil = new DelayLineF(maxHold + 2);
            SetHold(2);
        }

        /// <summary>The hold for a rate (0.5 ms, at least 2 samples).</summary>
        public static int HoldFor(double sampleRate) => Math.Max(2, (int)Math.Round(HoldSeconds * sampleRate));

        public void SetHold(int samples)
        {
            hold = Math.Max(2, samples);
            b1 = (hold + 1) / 2;
            b2 = hold - b1 + 1;
            Reset();
        }

        /// <summary>Samples of delay.</summary>
        public int Latency => hold - 1;

        public void Reset() => Reset(0);

        /// <summary>Empties it, the delayed ceiling at <paramref name="ceiling"/>.</summary>
        public void Reset(float ceiling)
        {
            audioL.Reset();
            audioR.Reset();
            ceil.Reset();
            for (int k = 0; k <= hold + 1; k++) ceil.Write(ceiling);
            Array.Fill(box1, 1.0);
            Array.Fill(box2, 1.0);
            head = count = 0;
            index = 0;
        }

        void Push(double v, long j)
        {
            while (count > 0 && dqVal[(head + count - 1) & mask] >= v) count--;
            int at = (head + count) & mask;
            dqVal[at] = v;
            dqIdx[at] = j;
            count++;
            while (dqIdx[head] <= j - hold)
            {
                head = (head + 1) & mask;
                count--;
            }
        }

        /// <summary>
        /// <paramref name="n"/> samples in place, each under its own ceiling <paramref name="c"/>[i]; the output (and
        /// <paramref name="c"/>, delayed with it) <see cref="Latency"/> samples later.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] left, float[] right, float[] c, int n)
        {
            double inv1 = 1.0 / b1, inv2 = 1.0 / b2;
            int delay = hold - 1;
            for (int i = 0; i < n; i++)
            {
                if ((index & 255) == 0)
                {
                    // The exact running sums, now and then (no drift).
                    double s1 = 0, s2 = 0;
                    int w = (int)(index - 1) & mask;
                    for (int k = 0; k < b1; k++) s1 += box1[(w - k) & mask];
                    for (int k = 0; k < b2; k++) s2 += box2[(w - k) & mask];
                    sum1 = s1;
                    sum2 = s2;
                }
                float x0 = left[i], x1 = right[i], ci = c[i];
                audioL.Write(x0);
                audioR.Write(x1);
                ceil.Write(ci);
                float a = MathF.Max(MathF.Abs(x0), MathF.Abs(x1));
                double need = a > ci ? ci / (double)a : 1.0;
                long j = index++;
                int slot = (int)j & mask;
                Push(need, j);
                double h = dqVal[head];
                sum1 += h - box1[(slot - b1) & mask];
                box1[slot] = h;
                double m1 = sum1 * inv1;
                sum2 += m1 - box2[(slot - b2) & mask];
                box2[slot] = m1;
                float g = (float)Math.Min(1.0, sum2 * inv2);
                float cd = ceil.Read(delay);
                float y0 = audioL.Read(delay) * g, y1 = audioR.Read(delay) * g;
                left[i] = MathF.Min(cd, MathF.Max(-cd, y0));
                right[i] = MathF.Min(cd, MathF.Max(-cd, y1));
                c[i] = cd;
            }
        }
    }
}
