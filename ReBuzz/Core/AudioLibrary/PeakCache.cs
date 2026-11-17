#nullable disable
// Fork (docs/specs/neo-audio.md, NA2): the min/max peaks of an asset's audio at several zoom levels, for drawing waveforms. Made off the UI thread
// and kept beside the asset in memory (it is never saved: it is made again from the audio). Pure: ReBuzz.SessionTests links this file.

using System;
using System.Threading;
using System.Threading.Tasks;
using ReBuzz.Core.Clips;

namespace ReBuzz.Core.AudioLibrary
{
    /// <summary>
    /// Peaks of one piece of audio: level 0 holds the lowest and highest sample (both channels) of each <see cref="BaseBlock"/> samples, every
    /// level above it the same of <see cref="Step"/> blocks of the one below. A drawing asks for a range of samples and gets its extremes from the level
    /// whose blocks are about a pixel wide, so a waveform costs a few reads per pixel at any zoom.
    /// </summary>
    internal sealed class PeakCache
    {
        public const int BaseBlock = 64;
        public const int Step = 4;
        const int MaxLevels = 9;

        readonly float[][] lows, highs;
        readonly AudioSource source;
        const int RangeBlock = 4096;
        readonly object rangeGate = new object();
        float[] rangeL, rangeR;
        long rangeAt = -1;
        int rangeCount;

        PeakCache(AudioSource source, float[][] lows, float[][] highs)
        {
            this.source = source;
            this.lows = lows;
            this.highs = highs;
        }

        public int Levels => lows.Length;

        /// <summary>Samples in one block of a level.</summary>
        public static long BlockOf(int level) => (long)BaseBlock * (long)Math.Pow(Step, level);

        public int BlocksAt(int level) => lows[level].Length;

        /// <summary>Makes the peaks of an audio (reads each sample once). Cancelled by the token between blocks.</summary>
        public static PeakCache Build(AudioSource source, CancellationToken cancel = default)
        {
            long n = source.Length;
            var lowList = new System.Collections.Generic.List<float[]>();
            var highList = new System.Collections.Generic.List<float[]>();
            int blocks = (int)((n + BaseBlock - 1) / BaseBlock);
            var lo = new float[blocks];
            var hi = new float[blocks];
            bool mono = source.IsMono;
            // Read in pieces (exact: from memory or the cache file), 4096 blocks at a time.
            const int PieceBlocks = 4096;
            var gl = new float[PieceBlocks * BaseBlock];
            var gr = mono ? gl : new float[PieceBlocks * BaseBlock];
            for (int b0 = 0; b0 < blocks; b0 += PieceBlocks)
            {
                cancel.ThrowIfCancellationRequested();
                long start = (long)b0 * BaseBlock;
                int got = (int)Math.Min(gl.Length, n - start);
                source.CopyExact(0, start, gl.AsSpan(0, got));
                if (!mono) source.CopyExact(1, start, gr.AsSpan(0, got));
                for (int b = b0; b < Math.Min(blocks, b0 + PieceBlocks); b++)
                {
                    int from = (b - b0) * BaseBlock, to = Math.Min(got, from + BaseBlock);
                    float mn = float.MaxValue, mx = float.MinValue;
                    for (int i = from; i < to; i++)
                    {
                        float l = gl[i];
                        if (l < mn) mn = l;
                        if (l > mx) mx = l;
                        if (!mono)
                        {
                            float r = gr[i];
                            if (r < mn) mn = r;
                            if (r > mx) mx = r;
                        }
                    }
                    lo[b] = mn;
                    hi[b] = mx;
                }
            }
            lowList.Add(lo);
            highList.Add(hi);
            while (lowList.Count < MaxLevels && lowList[^1].Length > 1)
            {
                var pl = lowList[^1];
                var ph = highList[^1];
                int count = (pl.Length + Step - 1) / Step;
                var nl = new float[count];
                var nh = new float[count];
                for (int b = 0; b < count; b++)
                {
                    float mn = float.MaxValue, mx = float.MinValue;
                    for (int k = b * Step; k < Math.Min(pl.Length, (b + 1) * Step); k++)
                    {
                        if (pl[k] < mn) mn = pl[k];
                        if (ph[k] > mx) mx = ph[k];
                    }
                    nl[b] = mn;
                    nh[b] = mx;
                }
                lowList.Add(nl);
                highList.Add(nh);
            }
            return new PeakCache(source, lowList.ToArray(), highList.ToArray());
        }

        /// <summary>
        /// The lowest and highest sample from <paramref name="from"/> to <paramref name="to"/> (sample positions, either order; outside the audio is 0).
        /// Short ranges read the samples; longer ones read the level whose blocks are not much wider than the range's quarter.
        /// </summary>
        public void Range(double from, double to, out float min, out float max)
        {
            if (to < from) (from, to) = (to, from);
            long n = source.Length;
            long a = (long)Math.Floor(from), b = (long)Math.Ceiling(to);
            min = float.MaxValue;
            max = float.MinValue;
            if (b < 0 || a >= n)
            {
                min = max = 0;
                return;
            }
            a = Math.Max(0, a);
            b = Math.Min(n - 1, Math.Max(a, b));
            if (a != (long)Math.Floor(from) || b != (long)Math.Ceiling(to))
            {
                // Partly outside the audio: it is silent there.
                if (min > 0) min = 0;
                if (max < 0) max = 0;
            }
            long span = b - a + 1;
            if (span <= BaseBlock * 2)
            {
                lock (rangeGate)
                {
                    // Exact reads, a few thousand samples at a time (a zoomed waveform asks for neighbours one after another).
                    if (rangeAt < 0 || a < rangeAt || b >= rangeAt + rangeCount)
                    {
                        rangeL ??= new float[RangeBlock];
                        rangeR ??= new float[RangeBlock];
                        rangeAt = Math.Max(0, a - 256);
                        rangeCount = (int)Math.Min(RangeBlock, n - rangeAt);
                        source.CopyExact(0, rangeAt, rangeL.AsSpan(0, rangeCount));
                        source.CopyExact(1, rangeAt, rangeR.AsSpan(0, rangeCount));
                    }
                    for (long i = a; i <= b; i++)
                    {
                        float l = rangeL[i - rangeAt], r = rangeR[i - rangeAt];
                        float lo = Math.Min(l, r), hi = Math.Max(l, r);
                        if (lo < min) min = lo;
                        if (hi > max) max = hi;
                    }
                }
                if (min > max) min = max = 0;
                return;
            }
            int level = 0;
            while (level + 1 < Levels && BlockOf(level + 1) * 2 <= span) level++;
            long block = BlockOf(level);
            long first = a / block, last = b / block;
            var l0 = lows[level];
            var h0 = highs[level];
            for (long k = first; k <= last && k < l0.Length; k++)
            {
                if (l0[k] < min) min = l0[k];
                if (h0[k] > max) max = h0[k];
            }
            if (min > max) min = max = 0;
        }

        /// <summary>Makes the peaks in the background; <paramref name="done"/> runs on a pool thread when they are ready (the view asks for a redraw there).</summary>
        public static Task BuildAsync(AudioSource source, Action<PeakCache> done) =>
            Task.Run(() =>
            {
                var cache = Build(source);
                done(cache);
            });
    }
}
