#nullable disable
// Neo analysis library (docs/specs/mcp-server.md §7): loudness and true peak as streams, for the
// analysis machines (Neo Analyzer, docs/specs/machines.md §5). The measures of Loudness.Measure,
// block by block on an audio thread: the same K-weighting, 100 ms steps, 400 ms momentary and 3 s
// short-term windows, BS.1770 gating for the integrated loudness, EBU Tech 3342 for the loudness
// range, and the same 4× interpolator for true peak. Nothing allocates after construction. The
// gated measures keep histograms of 0.01 LU bins that hold each bin's exact energy instead of
// every block, so over a whole signal they agree with the batch within thousandths of a LU.

using System;
using System.Numerics;

namespace NeoBuzz.Analysis
{
    /// <summary>
    /// Blocks' mean squares by loudness, for gating without keeping every block: 0.01 LU bins from
    /// −70 to +30 LUFS (louder blocks go in the top bin), each with its count and its blocks'
    /// summed energy, so means are exact and only the bin a gate falls in is approximate.
    /// </summary>
    internal sealed class LoudnessHistogram
    {
        public const double Lowest = -70, Highest = 30, BinLu = 0.01;
        const int Bins = (int)((Highest - Lowest) / BinLu);
        readonly int[] counts = new int[Bins];
        readonly double[] sums = new double[Bins];
        long count;
        double sum;

        /// <summary>Blocks above the absolute gate (−70 LUFS).</summary>
        public long Count => count;

        public void Clear()
        {
            Array.Clear(counts, 0, Bins);
            Array.Clear(sums, 0, Bins);
            count = 0;
            sum = 0;
        }

        /// <summary>Adds a block's mean square; blocks at or below −70 LUFS don't count (BS.1770's absolute gate).</summary>
        public void Add(double meanSquare)
        {
            double l = Loudness.Lufs(meanSquare);
            if (!(l > Lowest)) return;
            int i = (int)((l - Lowest) / BinLu);
            if (i >= Bins) i = Bins - 1;
            counts[i]++;
            sums[i] += meanSquare;
            count++;
            sum += meanSquare;
        }

        /// <summary>The first bin of blocks louder than <paramref name="gate"/>: the gate's own bin counts when its blocks' mean is above it.</summary>
        int FirstAbove(double gate)
        {
            int g = (int)Math.Floor((gate - Lowest) / BinLu);
            if (g < 0) return 0;
            if (g >= Bins) return Bins;
            return counts[g] > 0 && Loudness.Lufs(sums[g] / counts[g]) > gate ? g : g + 1;
        }

        /// <summary>BS.1770 gated loudness: the blocks louder than their mean + <paramref name="relative"/> LU (−10 for integrated loudness).</summary>
        public double Gated(double relative)
        {
            if (count == 0) return double.NegativeInfinity;
            double gate = Loudness.Lufs(sum / count) + relative;
            double s = 0;
            long c = 0;
            for (int i = FirstAbove(gate); i < Bins; i++)
            {
                if (counts[i] == 0) continue;
                s += sums[i];
                c += counts[i];
            }
            return c == 0 ? double.NegativeInfinity : Loudness.Lufs(s / c);
        }

        /// <summary>EBU Tech 3342 loudness range: 95th − 10th percentile of the blocks louder than their mean − 20 LU.</summary>
        public double Range()
        {
            if (count == 0) return 0;
            int first = FirstAbove(Loudness.Lufs(sum / count) - 20);
            long n = 0;
            for (int i = first; i < Bins; i++) n += counts[i];
            if (n == 0) return 0;
            return Percentile(first, n, 0.95) - Percentile(first, n, 0.10);
        }

        // As Loudness.Percentile: linear between the two ranks around p·(n − 1).
        double Percentile(int first, long n, double p)
        {
            double pos = p * (n - 1);
            long i = (long)Math.Floor(pos);
            double f = pos - i;
            double a = AtRank(first, i);
            return i + 1 < n ? a + f * (AtRank(first, i + 1) - a) : a;
        }

        /// <summary>The loudness of the block at <paramref name="rank"/> (0 = quietest from <paramref name="first"/>): its bin's mean.</summary>
        double AtRank(int first, long rank)
        {
            long seen = 0;
            for (int i = first; i < Bins; i++)
            {
                if (counts[i] == 0) continue;
                seen += counts[i];
                if (rank < seen) return Loudness.Lufs(sums[i] / counts[i]);
            }
            return double.NegativeInfinity;
        }
    }

    /// <summary>
    /// BS.1770 loudness of a stream: feed it blocks; every 100 ms step it updates the momentary
    /// (400 ms) and short-term (3 s) loudness, and the integrated loudness and loudness range since
    /// the last <see cref="ResetIntegrated"/> are read on demand. Not thread-safe: one thread feeds
    /// and reads (the analyzer publishes snapshots for other threads).
    /// </summary>
    internal sealed class LoudnessMeter
    {
        const int MomentarySteps = 4, ShortTermSteps = 30;
        Loudness.KWeighting kl, kr;
        readonly int hop;
        readonly double[] steps = new double[ShortTermSteps];
        readonly LoudnessHistogram momentaryBlocks = new LoudnessHistogram(), shortTermBlocks = new LoudnessHistogram();
        int inStep, stepPos;
        double stepSum;
        long total, sinceReset;

        public LoudnessMeter(int rate)
        {
            Rate = rate;
            kl = Loudness.KWeighting.For(rate);
            kr = Loudness.KWeighting.For(rate);
            hop = (int)Math.Round(rate * 0.1);
            ResetIntegrated();
        }

        public int Rate { get; }

        /// <summary>Samples per 100 ms step (as Loudness.Measure rounds it).</summary>
        public int StepSamples => hop;

        /// <summary>Samples until the current step completes.</summary>
        public int SamplesToStepEnd => hop - inStep;

        /// <summary>Steps completed since the meter started.</summary>
        public long Steps => total;

        /// <summary>Loudness of the last 400 ms, LUFS (−∞ until 400 ms have passed).</summary>
        public double Momentary { get; private set; }

        /// <summary>Loudness of the last 3 s, LUFS (−∞ until 3 s have passed).</summary>
        public double ShortTerm { get; private set; }

        /// <summary>The loudest momentary and short-term windows since the reset.</summary>
        public double MomentaryMax { get; private set; }
        public double ShortTermMax { get; private set; }

        /// <summary>Gated integrated loudness since the reset, LUFS (−∞ while every window is below the gate).</summary>
        public double Integrated => momentaryBlocks.Gated(-10);

        /// <summary>Loudness range since the reset, LU (0 until there are short-term windows).</summary>
        public double Range => shortTermBlocks.Range();

        /// <summary>Time measured since the reset, seconds (whole steps).</summary>
        public double Seconds => sinceReset * hop / (double)Rate;

        /// <summary>
        /// Starts the integrated loudness, loudness range and maxima again, as if measuring began
        /// now: windows that reach back before the reset don't count. Momentary and short-term go on.
        /// </summary>
        public void ResetIntegrated()
        {
            momentaryBlocks.Clear();
            shortTermBlocks.Clear();
            sinceReset = 0;
            MomentaryMax = double.NegativeInfinity;
            ShortTermMax = double.NegativeInfinity;
            if (total == 0)
            {
                Momentary = double.NegativeInfinity;
                ShortTerm = double.NegativeInfinity;
            }
        }

        /// <summary>
        /// Feeds <paramref name="n"/> samples (±1 full scale; <paramref name="right"/> null for
        /// mono, which counts as both channels as in Loudness.Measure). Returns the steps completed.
        /// </summary>
        public int Process(float[] left, float[] right, int offset, int n)
        {
            int done = 0;
            for (int i = offset, end = offset + n; i < end; i++)
            {
                double l = kl.Process(left[i]);
                stepSum += l * l;
                if (right != null)
                {
                    double r = kr.Process(right[i]);
                    stepSum += r * r;
                }
                if (++inStep == hop)
                {
                    CompleteStep(right == null);
                    done++;
                }
            }
            return done;
        }

        void CompleteStep(bool mono)
        {
            double e = stepSum / hop;
            if (mono) e *= 2;
            stepSum = 0;
            inStep = 0;
            steps[stepPos] = e;
            stepPos = stepPos + 1 == ShortTermSteps ? 0 : stepPos + 1;
            total++;
            sinceReset++;
            if (total >= MomentarySteps)
            {
                double m = Window(MomentarySteps);
                Momentary = Loudness.Lufs(m);
                if (sinceReset >= MomentarySteps)
                {
                    momentaryBlocks.Add(m);
                    if (Momentary > MomentaryMax) MomentaryMax = Momentary;
                }
            }
            if (total >= ShortTermSteps)
            {
                double s = Window(ShortTermSteps);
                ShortTerm = Loudness.Lufs(s);
                if (sinceReset >= ShortTermSteps)
                {
                    shortTermBlocks.Add(s);
                    if (ShortTerm > ShortTermMax) ShortTermMax = ShortTerm;
                }
            }
        }

        /// <summary>Mean energy of the last <paramref name="length"/> steps.</summary>
        double Window(int length)
        {
            double sum = 0;
            for (int k = 1; k <= length; k++)
            {
                int j = stepPos - k;
                if (j < 0) j += ShortTermSteps;
                sum += steps[j];
            }
            return Math.Max(0, sum / length);
        }
    }

    /// <summary>
    /// True peak of a stream (BS.1770 Annex 2): the largest of the samples and of the 4×
    /// interpolated points between them, with Loudness.TruePeak's interpolator and its shortcut
    /// (neighbourhoods that can't beat the peak are skipped). The points around a sample are known
    /// <see cref="Delay"/> samples after it arrives; <see cref="Flush"/> ends a signal as the
    /// batch does (silence after it).
    /// </summary>
    internal sealed class TruePeakMeter
    {
        const int Taps = Loudness.Taps;
        /// <summary>Samples of lookahead the interpolator needs.</summary>
        public const int Delay = Taps / 2 - 1;
        // Phases 1 … 3, each reversed so it runs over the history oldest first.
        static readonly double[] Coefficients = Build();
        readonly double[] history = new double[2 * Taps];   // each sample twice, so a window is contiguous
        int newest = Taps - 1;
        long seen;
        int fresh;
        double peak, windowPeak;

        static double[] Build()
        {
            var c = new double[(Loudness.Phases - 1) * Taps];
            for (int p = 1; p < Loudness.Phases; p++)
                for (int t = 0; t < Taps; t++)
                    c[(p - 1) * Taps + t] = Loudness.Interpolator[p][Taps - 1 - t];
            return c;
        }

        /// <summary>The true peak since the last <see cref="ResetPeak"/>, linear.</summary>
        public double Peak => Math.Max(peak, windowPeak);

        /// <summary>The true peak since the last call, linear (for meters that fall back).</summary>
        public double TakeWindowPeak()
        {
            double p = windowPeak;
            if (p > peak) peak = p;
            windowPeak = 0;
            return p;
        }

        /// <summary>
        /// Starts the peak again, as if measuring began now: until the interpolator's window lies
        /// wholly after the reset (23 samples), only the samples themselves count.
        /// </summary>
        public void ResetPeak()
        {
            peak = 0;
            windowPeak = 0;
            fresh = Taps - 1;
        }

        public void Process(float[] x, int offset, int n)
        {
            for (int i = offset, end = offset + n; i < end; i++) Push(x[i]);
        }

        /// <summary>Feeds the silence after a signal, so its last samples' neighbourhoods are measured.</summary>
        public void Flush()
        {
            for (int i = 0; i < Delay; i++) Push(0);
        }

        void Push(double v)
        {
            newest = newest + 1 == Taps ? 0 : newest + 1;
            history[newest] = v;
            history[newest + Taps] = v;
            double a = Math.Abs(v);
            if (a > windowPeak) windowPeak = a;
            if (++seen <= Delay) return;   // the centre sample would come before the signal
            if (fresh > 0)
            {
                fresh--;
                return;
            }
            // history[start + t] is x[m − 23 + t] for the newest sample m; the centre i = m − 11 is t = 12.
            int start = newest + 1;
            var h = history;
            double near = Math.Max(Math.Max(Math.Abs(h[start + 12]), Math.Abs(h[start + 11])),
                Math.Max(Math.Abs(h[start + 13]), Math.Abs(h[start + 10])));
            if (2 * near < windowPeak) return;   // as Loudness.TruePeak: can't beat the peak so far
            for (int p = 0; p < Loudness.Phases - 1; p++)
            {
                double s = Dot(h, start, p * Taps);
                double m = Math.Abs(s);
                if (m > windowPeak) windowPeak = m;
            }
        }

        static double Dot(double[] h, int start, int c0)
        {
            var c = Coefficients;
            if (Vector.IsHardwareAccelerated && Taps % Vector<double>.Count == 0)
            {
                var acc = Vector<double>.Zero;
                for (int t = 0; t < Taps; t += Vector<double>.Count)
                    acc += new Vector<double>(h, start + t) * new Vector<double>(c, c0 + t);
                return Vector.Dot(acc, Vector<double>.One);
            }
            double s = 0;
            for (int t = 0; t < Taps; t++) s += h[start + t] * c[c0 + t];
            return s;
        }
    }
}
