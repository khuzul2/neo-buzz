#nullable disable
// Neo Limiter (docs/specs/machines.md §5): input gain → 4× true-peak detector → the reduction each
// sample needs to stay under the ceiling → stereo link → hold over the lookahead → release (set,
// or program-dependent) → two box filters over the lookahead → gain on the audio, delayed so the
// gain reaches the needed value exactly when the peak arrives. All gain maths in dB of reduction
// (≥ 0). Pure: tested by NeoBuzz.MachineTests.

using System;
using System.Runtime.Intrinsics;
using NeoBuzz.Dsp;

namespace NeoBuzz.Limiter
{
    /// <summary>
    /// The 4× oversampled peak of a stream, sample by sample, with the BS.1770 interpolator that
    /// NeoBuzz.Analysis measures true peak with (windowed sinc, 24 taps per phase, Kaiser β 8), so
    /// the limiter holds exactly what the meter reads. Each call takes the newest sample and returns
    /// the true peak of the sample <see cref="Delay"/> samples ago: the larger of that sample and
    /// the interpolated points on both sides of it.
    /// </summary>
    internal sealed class TruePeakInterpolator
    {
        public const int Phases = 4, Taps = 24;
        /// <summary>Samples between the input and the peak returned for it.</summary>
        public const int Delay = 12;

        // Kernel[p − 1][q] multiplies x[t − 23 + q] for the point at (t − 11) − p/4.
        static readonly float[][] Kernel = Build();
        readonly float[] ring = new float[64];
        int pos;
        float prevRight;

        static float[][] Build()
        {
            const double beta = 8.0;
            static double I0(double x)
            {
                double s = 1, t = 1;
                for (int k = 1; k < 30; k++) { t *= x / (2 * k); s += t * t; }
                return s;
            }
            var kernel = new float[Phases - 1][];
            for (int p = 1; p < Phases; p++)
            {
                var h = new float[Taps];
                for (int k = 0; k < Taps; k++)
                {
                    // As NeoBuzz.Analysis.Loudness: tap k of phase p sits at (k − Taps/2 + 1) − p/Phases.
                    double t = (k - Taps / 2 + 1) - p / (double)Phases;
                    double sinc = Math.Abs(t) < 1e-12 ? 1 : PortableMath.Sin(Math.PI * t) / (Math.PI * t);
                    double pos = (t + Taps / 2.0) / Taps;
                    double w = Math.Abs(pos - 0.5) <= 0.5 ? I0(beta * Math.Sqrt(1 - PortableMath.Pow(2 * pos - 1, 2))) / I0(beta) : 0;
                    // Tap k multiplies x[i − (k − 11)] with i = t − 11: x[t − k], which is ring offset q = 23 − k.
                    h[Taps - 1 - k] = (float)(sinc * w);
                }
                kernel[p - 1] = h;
            }
            return kernel;
        }

        public void Reset()
        {
            Array.Clear(ring, 0, ring.Length);
            pos = 0;
            prevRight = 0;
        }

        /// <summary>Takes x[t]; returns the true peak at t − <see cref="Delay"/>.</summary>
        public float Process(float x)
        {
            pos = (pos + 1) & 31;
            ring[pos] = x;
            ring[pos + 32] = x;
            int s = (pos - 23) & 31;
            // The points between x[t − 12] and x[t − 11]: right of the sample returned, left of the next.
            // Summed in one order on every CPU (docs/specs/machines.md §4, portable maths): eight lanes,
            // lane j taking taps j, j + 8 and j + 16, then lane j + lane j + 4, then (0 + 1) + (2 + 3).
            // One 8-wide vector where the CPU has it (x64), two 4-wide halves elsewhere (arm64): the
            // same additions, so the same bits. (Vector<float> was 8 lanes on x64 and 4 on arm64.)
            float right = 0;
            for (int p = 0; p < Phases - 1; p++)
            {
                var k = Kernel[p];
                Vector128<float> lo, hi;
                if (Vector256.IsHardwareAccelerated)
                {
                    var sum = Vector256<float>.Zero;
                    for (int q = 0; q < Taps; q += 8) sum += Vector256.Create(ring, s + q) * Vector256.Create(k, q);
                    lo = sum.GetLower();
                    hi = sum.GetUpper();
                }
                else
                {
                    lo = hi = Vector128<float>.Zero;
                    for (int q = 0; q < Taps; q += 8)
                    {
                        lo += Vector128.Create(ring, s + q) * Vector128.Create(k, q);
                        hi += Vector128.Create(ring, s + q + 4) * Vector128.Create(k, q + 4);
                    }
                }
                var four = lo + hi;
                float acc = MathF.Abs((four.GetElement(0) + four.GetElement(1)) + (four.GetElement(2) + four.GetElement(3)));
                if (acc > right) right = acc;
            }
            float centre = MathF.Abs(ring[s + 11]);
            float tp = MathF.Max(centre, MathF.Max(prevRight, right));
            prevRight = right;
            return tp;
        }
    }

    internal sealed class LimiterEngine
    {
        public const int MaxBlock = 256;
        public const double MaxLookaheadSeconds = 0.005;
        const double GlideSeconds = 0.02;
        const double Ln10Over20 = 2.302585092994046 / 20;
        /// <summary>Editor history: one entry per this many seconds.</summary>
        public const double HistoryStep = 0.01;
        public const int HistoryLength = 400;

        readonly ParamValues values;
        float sampleRate = 48000;

        sealed class Channel
        {
            public readonly TruePeakInterpolator Detector = new TruePeakInterpolator();
            public DelayLineF Audio;
            // The detected true peaks, kept until their samples come out (the output meter: peak × gain).
            public float[] Peaks;
            // Sliding maximum of the needed reduction over the hold (a monotonic deque on rings).
            public double[] DqVal;
            public long[] DqIdx;
            public int DqHead, DqCount;
            // Release state.
            public double Gr, GrAvg;
            // Box filters: rings of past values and their running sums.
            public double[] Box1, Box2;
            public double Sum1, Sum2;

            public void Allocate(int cap, int delay)
            {
                Audio = new DelayLineF(delay + 1);
                Peaks = new float[cap];
                DqVal = new double[cap];
                DqIdx = new long[cap];
                Box1 = new double[cap];
                Box2 = new double[cap];
                Reset();
            }

            public void Reset()
            {
                Detector.Reset();
                Audio?.Reset();
                if (DqVal != null)
                {
                    Array.Clear(Box1, 0, Box1.Length);
                    Array.Clear(Box2, 0, Box2.Length);
                    Array.Clear(Peaks, 0, Peaks.Length);
                }
                DqHead = DqCount = 0;
                Gr = GrAvg = Sum1 = Sum2 = 0;
            }
        }

        readonly Channel[] ch = { new Channel(), new Channel() };
        int mask;
        long index;
        int hold, box1, box2;
        bool auto;
        double link;
        double relA, fastA, avgA;
        /// <summary>Auto release: an isolated peak recovers this many times faster than Release.</summary>
        public const double AutoFast = 5;
        const double AverageSeconds = 0.3;
        LinearRamp inGain, ceiling;

        // Meters and history (written on the audio thread, read by the editor).
        float grNow;
        readonly float[] histIn = new float[HistoryLength], histOut = new float[HistoryLength], histGr = new float[HistoryLength];
        volatile int histPos;
        int chunk, chunkLeft;
        float chunkIn, chunkOut, chunkGr;

        public LimiterEngine(ParamValues values)
        {
            this.values = values;
            SetSampleRate(48000);
        }

        public readonly LevelMeter InMeter = new LevelMeter();
        /// <summary>The output's true peak (4×: each sample's detected true peak times the gain it got), held and falling.</summary>
        public readonly LevelMeter OutMeter = new LevelMeter();

        /// <summary>Gain reduction now, dB (≥ 0), for the editor.</summary>
        public float GainReductionDb => grNow;

        /// <summary>Samples the audio is delayed by: the lookahead and the true-peak interpolator.</summary>
        public int Latency => hold + TruePeakInterpolator.Delay - 1;

        /// <summary>The hold (lookahead) in samples.</summary>
        public int LookaheadSamples => hold;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            int maxHold = (int)Math.Ceiling(MaxLookaheadSeconds * sr) + 2;
            int cap = 1;
            while (cap < maxHold + 2) cap <<= 1;
            mask = cap - 1;
            foreach (var c in ch) c.Allocate(cap, maxHold + TruePeakInterpolator.Delay + 1);
            index = 0;
            inGain.Setup(GlideSeconds, sr);
            ceiling.Setup(GlideSeconds, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            chunk = Math.Max(1, (int)Math.Round(HistoryStep * sr));
            chunkLeft = chunk;
            Update(true);
        }

        public void Reset()
        {
            foreach (var c in ch) c.Reset();
            index = 0;
            grNow = 0;
        }

        static double Coef(double seconds, double sr) => seconds <= 0 ? 0 : PortableMath.Exp(-1.0 / (seconds * sr));

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            float g = (float)DspMath.DbToGain(values.Value(P.InputGain));
            float c = (float)DspMath.DbToGain(values.Value(P.Ceiling));
            if (force)
            {
                inGain.Reset(g);
                ceiling.Reset(c);
            }
            else
            {
                inGain.SetTarget(g);
                ceiling.SetTarget(c);
            }
            double release = values.Value(P.Release);
            auto = values.On(P.AutoRelease);
            relA = Coef(release, sampleRate);
            // Auto: the release slides from five times faster than Release (an isolated peak) to the
            // Release time (sustained limiting), by how much of the reduction is sustained: its
            // average over AverageSeconds against its value now.
            fastA = Coef(Math.Max(0.001, release / AutoFast), sampleRate);
            avgA = Coef(AverageSeconds, sampleRate);
            link = values.Value(P.Link);
            int maxHold = (int)Math.Ceiling(MaxLookaheadSeconds * sampleRate);
            hold = Math.Clamp((int)Math.Round(values.Value(P.Lookahead) * 0.001 * sampleRate), 2, maxHold);
            box1 = (hold + 1) / 2;
            box2 = hold - box1 + 1;
            values.Clean();
        }

        bool wasShared;

        /// <summary>
        /// One channel's gain chain for detected sample <paramref name="j"/>: hold over the
        /// lookahead, release, then two box filters (the weighted average of the last `hold`
        /// reductions, each at least what the delayed sample needs). Returns the reduction (dB ≥ 0)
        /// for the sample leaving now.
        /// </summary>
        double Chain(Channel c, double need, long j, int slot, double inv1, double inv2)
        {
            Push(c, need, j);
            double h = c.DqVal[c.DqHead];
            double r;
            if (!auto)
            {
                r = c.Gr = Math.Max(h, c.Gr * relA);
            }
            else
            {
                double sustained = c.Gr > 1e-9 ? Math.Min(1, c.GrAvg / c.Gr) : 1;
                r = c.Gr = Math.Max(h, c.Gr * (fastA + (relA - fastA) * sustained));
                c.GrAvg += (r - c.GrAvg) * (1 - avgA);
            }
            c.Sum1 += r - c.Box1[(slot - box1) & mask];
            c.Box1[slot] = r;
            double b1 = c.Sum1 * inv1;
            c.Sum2 += b1 - c.Box2[(slot - box2) & mask];
            c.Box2[slot] = b1;
            return Math.Max(0, c.Sum2 * inv2);
        }

        static void CopyChain(Channel from, Channel to)
        {
            Array.Copy(from.DqVal, to.DqVal, from.DqVal.Length);
            Array.Copy(from.DqIdx, to.DqIdx, from.DqIdx.Length);
            Array.Copy(from.Box1, to.Box1, from.Box1.Length);
            Array.Copy(from.Box2, to.Box2, from.Box2.Length);
            to.DqHead = from.DqHead;
            to.DqCount = from.DqCount;
            to.Gr = from.Gr;
            to.GrAvg = from.GrAvg;
        }

        void Push(Channel c, double v, long j)
        {
            while (c.DqCount > 0 && c.DqVal[(c.DqHead + c.DqCount - 1) & mask] <= v) c.DqCount--;
            int at = (c.DqHead + c.DqCount) & mask;
            c.DqVal[at] = v;
            c.DqIdx[at] = j;
            c.DqCount++;
            while (c.DqIdx[c.DqHead] <= j - hold)
            {
                c.DqHead = (c.DqHead + 1) & mask;
                c.DqCount--;
            }
        }

        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            int delay = Latency;
            bool shared = link >= 1;
            // Leaving full link, the right channel's chain carries on from the shared one.
            if (!shared && wasShared) CopyChain(ch[0], ch[1]);
            wasShared = shared;
            // Exact running sums at each block start (no drift).
            foreach (var c in ch)
            {
                double s1 = 0, s2 = 0;
                int w = (int)(index - 1) & mask;
                for (int k = 0; k < box1; k++) s1 += c.Box1[(w - k) & mask];
                for (int k = 0; k < box2; k++) s2 += c.Box2[(w - k) & mask];
                c.Sum1 = s1;
                c.Sum2 = s2;
            }
            var c0 = ch[0];
            var c1 = ch[1];
            float blockGr = 0, blockIn = 0;
            double inv1 = 1.0 / box1, inv2 = 1.0 / box2;
            for (int i = 0; i < n; i++)
            {
                float g = inGain.Next();
                float ceil = ceiling.Next();
                float x0 = left[i] * g, x1 = right[i] * g;
                c0.Audio.Write(x0);
                c1.Audio.Write(x1);
                float a = MathF.Max(MathF.Abs(x0), MathF.Abs(x1));
                if (a > blockIn) blockIn = a;
                if (a > chunkIn) chunkIn = a;

                // The reduction the detected sample needs (dB ≥ 0), then linked.
                float tp0 = c0.Detector.Process(x0), tp1 = c1.Detector.Process(x1);
                double n0 = tp0 > ceil ? DspMath.DbPerNeper * PortableMath.Log(tp0 / ceil) : 0;
                double n1 = tp1 > ceil ? DspMath.DbPerNeper * PortableMath.Log(tp1 / ceil) : 0;
                if (link > 0)
                {
                    double m = Math.Max(n0, n1);
                    n0 += link * (m - n0);
                    n1 += link * (m - n1);
                }

                long j = index++;
                int slot = (int)j & mask;
                int outSlot = (slot - hold + 1) & mask;
                c0.Peaks[slot] = tp0;
                c1.Peaks[slot] = tp1;
                // Fully linked, both channels need the same: one gain chain serves both.
                double r0 = Chain(c0, n0, j, slot, inv1, inv2);
                double r1 = shared ? r0 : Chain(c1, n1, j, slot, inv1, inv2);
                float gain0 = r0 > 0 ? PortableMathF.Exp((float)(-r0 * Ln10Over20)) : 1f;
                float gain1 = shared ? gain0 : r1 > 0 ? PortableMathF.Exp((float)(-r1 * Ln10Over20)) : 1f;
                left[i] = c0.Audio.Read(delay) * gain0;
                right[i] = c1.Audio.Read(delay) * gain1;
                float gr = (float)Math.Max(r0, r1);
                // The samples leaving now were detected hold − 1 samples ago: their true peak × their gain.
                float outPeak = MathF.Max(c0.Peaks[outSlot] * gain0, c1.Peaks[outSlot] * gain1);
                if (gr > blockGr) blockGr = gr;
                chunkOut = MathF.Max(chunkOut, outPeak);
                chunkGr = MathF.Max(chunkGr, gr);
                if (--chunkLeft <= 0)
                {
                    int p = (histPos + 1) % HistoryLength;
                    histIn[p] = chunkIn;
                    histOut[p] = chunkOut;
                    histGr[p] = chunkGr;
                    histPos = p;
                    OutMeter.FeedValue(chunkOut, chunk);
                    chunkIn = chunkOut = chunkGr = 0;
                    chunkLeft = chunk;
                }
            }
            foreach (var c in ch)
            {
                c.Gr = DspMath.Flush(c.Gr);
                c.GrAvg = DspMath.Flush(c.GrAvg);
            }
            grNow = blockGr;
            InMeter.FeedValue(blockIn, n);
        }

        /// <summary>
        /// The last <paramref name="count"/> history entries, oldest first (input peak, output true
        /// peak, gain reduction dB; one per <see cref="HistoryStep"/>), for the editor.
        /// </summary>
        public void History(float[] input, float[] output, float[] reduction, int count)
        {
            int end = histPos;
            count = Math.Min(count, HistoryLength);
            for (int i = 0; i < count; i++)
            {
                int p = ((end - count + 1 + i) % HistoryLength + HistoryLength) % HistoryLength;
                input[i] = histIn[p];
                output[i] = histOut[p];
                reduction[i] = histGr[p];
            }
        }
    }
}
