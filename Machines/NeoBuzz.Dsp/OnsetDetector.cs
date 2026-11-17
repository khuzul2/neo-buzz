#nullable disable
// Neo machines shared DSP (docs/specs/pitch.md §3): the real-time onset detector Neo Voice to MIDI's triggers and Neo Audio
// to MIDI's notes share.
//  - Detection functions per hop (a quarter of a 10.7 ms frame): the spectral flux of the square-root magnitudes with a
//    max filter over the neighbouring bins of the previous frame (the SuperFlux idea: a vibrato or a slide doesn't count as
//    rising), taken over the whole spectrum and over three bands (60-300 Hz, 0.3-3 kHz, 3-16 kHz: a kick under the tail of an
//    open hat is a rise of the low band only), the energy rise in dB over two hops, and the same rise of the signal low-passed
//    at 150 Hz (a kick's fundamental lies under the first bin of a 10.7 ms frame). Each has an adaptive threshold: the
//    mean of its last 0.3 s plus Threshold deviations, and at least MinRatio times the mean, so a loud passage doesn't fire on
//    its own noise and a quiet one still does.
//  - A level gate (nothing fires below it) and a minimum interval between onsets.
//  - A hit is confirmed one hop after the first crossing (the strength is the highest value of the two hops), then the
//    onset's sample position is refined within the frames that heard it: the half-millisecond block with the steepest
//    energy rise, and in it the first sample over 30 % of the block's peak.
// Allocation-free after construction; the portable maths only; the transforms ComplexFft's.

using System;

namespace NeoBuzz.Dsp
{
    internal struct OnsetEvent
    {
        /// <summary>The stream position of the onset (samples since the start), refined to the sample.</summary>
        public long Sample;
        /// <summary>The detection function over its threshold at the strongest hop: 1 = just fired; about 3 a firm hit, 10 or more a hard one.</summary>
        public float Strength;
        /// <summary>The loudest hop's RMS in the 12 ms after the onset, dBFS: velocity from it follows the player's dynamics.</summary>
        public float LevelDb;
        /// <summary>The stream position when the onset was reported (the detection delay is this minus <see cref="Sample"/>).</summary>
        public long ReportedAt;
    }

    internal sealed class OnsetDetector
    {
        public readonly double SampleRate;
        public readonly int FrameSize, Hop;

        /// <summary>Higher fires less: the multiple of the deviation above the mean. 1 = sensitive, 3 = default, 6 = only hard hits.</summary>
        public float Threshold = 4f;
        /// <summary>A function fires only above this multiple of its own recent mean (noise-like input rarely peaks over 2×).</summary>
        public float MinRatio = 2.2f;
        /// <summary>The low band's Threshold is this many times higher.</summary>
        public float LowBandFactor = 1.5f;
        /// <summary>The least time between two onsets, seconds.</summary>
        public double MinIntervalSeconds = 0.04;
        /// <summary>A hop quieter than this (RMS, linear) can't fire. −54 dBFS.</summary>
        public float Gate = 0.002f;
        /// <summary>The energy rise over two hops that fires on its own, dB.</summary>
        public float RiseDb = 9f;
        /// <summary>The energy rise of the part under 150 Hz that fires on its own, dB.</summary>
        public float LowRiseDb = 14f;

        const int History = 112;           // hops of threshold memory
        const int Confirm = 1;             // hops a crossing waits to take the strongest value
        readonly ComplexFft fft;
        readonly float[] window, re, im, prev, prevMax, ring;
        readonly int ringMask, bins, firstBin;
        const int Functions = 4;           // all, low, mid, high
        readonly float[][] fluxHist = { new float[History], new float[History], new float[History], new float[History] };
        readonly double[] fluxSum = new double[Functions], fluxSq = new double[Functions];
        readonly float[] flux = new float[Functions];
        readonly int[] bandEdge = new int[4];
        readonly float[] dbHist = new float[4], lowHist = new float[6];
        Biquad lowPass;
        double lowSq;
        int histPos, histCount;
        long total, lastOnset = long.MinValue / 2;
        int sinceHop;
        bool havePrev;
        int pendingHops;
        float pendingFlux, pendingRise, pendingLevel;
        long pendingFirstEnd;

        public OnsetDetector(double sampleRate)
        {
            if (sampleRate < 8000 || sampleRate > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            SampleRate = sampleRate;
            int n = 64;
            while (n < 0.0107 * sampleRate) n <<= 1;
            FrameSize = n;
            Hop = n / 4;
            fft = new ComplexFft(n);
            window = new float[n];
            for (int i = 0; i < n; i++) window[i] = (float)(0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * i / n));
            re = new float[n]; im = new float[n];
            bins = n / 2;
            firstBin = Math.Max(1, (int)(60.0 * n / sampleRate));
            prev = new float[bins + 2]; prevMax = new float[bins + 2];
            int top = Math.Min(bins, (int)(16000.0 * n / sampleRate));
            bandEdge[0] = firstBin;
            bandEdge[1] = Math.Max(firstBin + 1, (int)(300.0 * n / sampleRate));
            bandEdge[2] = Math.Max(bandEdge[1] + 1, (int)(3000.0 * n / sampleRate));
            bandEdge[3] = top + 1;
            int r = 1;
            while (r < n * 4) r <<= 1;
            ring = new float[r];
            ringMask = r - 1;
            lowPass.Set(BiquadType.LowPass, 150, sampleRate, 0.7071067811865476);
        }

        /// <summary>The most onsets <see cref="Process"/> can report for <paramref name="count"/> samples.</summary>
        public int MaxEvents(int count) => count / Hop + 2;

        public void Reset()
        {
            Array.Clear(ring, 0, ring.Length);
            Array.Clear(prev, 0, prev.Length);
            Array.Clear(prevMax, 0, prevMax.Length);
            for (int f = 0; f < Functions; f++) Array.Clear(fluxHist[f], 0, History);
            Array.Clear(fluxSum, 0, Functions); Array.Clear(fluxSq, 0, Functions);
            Array.Clear(dbHist, 0, dbHist.Length);
            Array.Clear(lowHist, 0, lowHist.Length);
            lowPass.Reset(); lowSq = 0;
            histPos = histCount = 0; total = 0; sinceHop = 0; havePrev = false; pendingHops = 0;
            lastOnset = long.MinValue / 2;
        }

        /// <summary>Feeds mono audio; writes the onsets reported in it to <paramref name="events"/> (at least <see cref="MaxEvents"/> long from the offset) and returns how many.</summary>
        public int Process(float[] x, int offset, int count, OnsetEvent[] events, int eventOffset)
        {
            if (events.Length - eventOffset < MaxEvents(count)) throw new ArgumentException("events is shorter than MaxEvents(count)", nameof(events));
            int produced = 0;
            for (int i = 0; i < count; i++)
            {
                ring[(int)(total & ringMask)] = x[offset + i];
                float lp = lowPass.Process(x[offset + i]);
                lowSq += (double)lp * lp;
                total++;
                if (++sinceHop >= Hop)
                {
                    sinceHop = 0;
                    if (total >= FrameSize && HopStep(out OnsetEvent ev)) events[eventOffset + produced++] = ev;
                }
            }
            return produced;
        }

        bool HopStep(out OnsetEvent ev)
        {
            ev = default;
            int n = FrameSize;
            long start = total - n;
            double sq = 0;
            for (int i = 0; i < n; i++)
            {
                float v = ring[(int)((start + i) & ringMask)];
                re[i] = v * window[i];
                im[i] = 0;
                if (i >= n - Hop) sq += (double)v * v;
            }
            float rmsHop = (float)Math.Sqrt(sq / Hop);
            fft.Transform(re, im, false);
            // The flux: rising square-root magnitudes against the max of the previous frame's three neighbouring bins, over each band.
            float scale = 2f / n;
            int top = bandEdge[3] - 1;
            for (int f = 0; f < Functions; f++) flux[f] = 0;
            for (int band = 0; band < 3; band++)
            {
                float acc = 0;
                for (int k = bandEdge[band]; k < bandEdge[band + 1]; k++)
                {
                    float m = (float)Math.Sqrt(Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * scale);
                    float d = m - prevMax[k];
                    if (d > 0) acc += d;
                    prev[k] = m;
                }
                flux[1 + band] = acc;
                flux[0] += acc;
            }
            for (int k = firstBin; k <= top; k++)
            {
                float a = prev[k - 1], b = prev[k], c = prev[k + 1];
                prevMax[k] = Math.Max(a, Math.Max(b, c));
            }
            if (!havePrev) { havePrev = true; for (int f = 0; f < Functions; f++) flux[f] = 0; }

            // The energy: dB of the hop, the rise over two hops.
            float db = rmsHop > 1e-7f ? (float)(20.0 * PortableMath.Log10(rmsHop)) : -140f;
            float rise = db - dbHist[1];
            dbHist[3] = dbHist[2]; dbHist[2] = dbHist[1]; dbHist[1] = dbHist[0]; dbHist[0] = db;

            float lowRms = (float)Math.Sqrt(lowSq / Hop);
            lowSq = 0;
            float lowDb = lowRms > 1e-7f ? (float)(20.0 * PortableMath.Log10(lowRms)) : -140f;
            // The low part's rise: this hop's level over the mean level of the six hops before the last one (a hop of 150 Hz noise
            // holds under one cycle, so one hop's level is too uneven to compare with a single other).
            float lowBase = 0;
            for (int h = 1; h < 6; h++) lowBase += lowHist[h];
            lowBase /= 5f;
            float lowRise = lowRms >= Gate * 0.7f ? lowDb - lowBase : 0f;
            for (int h = 5; h > 0; h--) lowHist[h] = lowHist[h - 1];
            lowHist[0] = lowDb;

            // The adaptive threshold of each function, from the hops before this one; the strongest ratio counts.
            float fluxRatio = 0;
            for (int f = 0; f < Functions; f++)
            {
                double mean = histCount > 0 ? fluxSum[f] / histCount : 0;
                double variance = histCount > 1 ? Math.Max(0, fluxSq[f] / histCount - mean * mean) : 0;
                // The low band has a handful of bins, so its flux is the most uneven in steady noise: it asks for more.
                float k = f == 1 ? Threshold * LowBandFactor : Threshold;
                float thr = Math.Max((float)(mean + k * Math.Sqrt(variance)), (float)(MinRatio * mean)) + 0.003f;
                fluxRatio = Math.Max(fluxRatio, flux[f] / thr);
                if (histCount == History) { float old = fluxHist[f][histPos]; fluxSum[f] -= old; fluxSq[f] -= (double)old * old; }
                fluxHist[f][histPos] = flux[f];
                fluxSum[f] += flux[f]; fluxSq[f] += (double)flux[f] * flux[f];
            }
            if (histCount < History) histCount++;
            if (++histPos == History) histPos = 0;

            bool loud = rmsHop >= Gate;
            float riseRatio = Math.Max(rise / RiseDb, lowRise / LowRiseDb);
            // A spectral rise with the level falling is a sound ending (a voice's last cycles), not one starting.
            bool crossing = loud && ((fluxRatio > 1f && rise > 0f) || riseRatio > 1f);
            long interval = (long)(MinIntervalSeconds * SampleRate);

            if (pendingHops > 0)
            {
                pendingFlux = Math.Max(pendingFlux, fluxRatio);
                pendingRise = Math.Max(pendingRise, riseRatio);
                pendingLevel = Math.Max(pendingLevel, db);
                if (--pendingHops == 0) return Resolve(out ev);
                return false;
            }
            if (crossing && total - Hop - lastOnset >= interval)
            {
                pendingFlux = fluxRatio; pendingRise = riseRatio; pendingLevel = db;
                pendingFirstEnd = total;
                pendingHops = Confirm;
                return false;
            }
            return false;
        }

        bool Resolve(out OnsetEvent ev)
        {
            ev = default;
            // The onset lies in the frames from the one before the first crossing to now.
            long to = total;
            long from = pendingFirstEnd - FrameSize - Hop;
            if (from < 0) from = 0;
            int block = Math.Max(8, (int)(0.0005 * SampleRate));
            double prevE = -1, bestRatio = 0;
            long bestStart = from;
            double floorE = 1e-9 * block;
            for (long b = from; b + block <= to; b += block)
            {
                double e = 0;
                for (int i = 0; i < block; i++) { float v = ring[(int)((b + i) & ringMask)]; e += (double)v * v; }
                if (prevE >= 0)
                {
                    double ratio = (e + floorE) / (prevE + floorE);
                    // Weight by the energy gained so a rise out of digital silence doesn't beat a real hit.
                    double score = ratio > 1 ? (e - prevE) * (1 - 1 / ratio) : 0;
                    if (score > bestRatio) { bestRatio = score; bestStart = b; }
                }
                prevE = e;
            }
            // In the best block the first sample over 30 % of its peak.
            float peak = 0;
            for (int i = 0; i < block; i++) peak = Math.Max(peak, Math.Abs(ring[(int)((bestStart + i) & ringMask)]));
            long at = bestStart;
            for (int i = 0; i < block; i++)
                if (Math.Abs(ring[(int)((bestStart + i) & ringMask)]) >= 0.3f * peak) { at = bestStart + i; break; }
            if (at < lastOnset + (long)(MinIntervalSeconds * SampleRate) * 0.5) return false;
            lastOnset = at;
            ev.Sample = at;
            ev.Strength = Math.Max(pendingFlux, pendingRise);
            ev.LevelDb = pendingLevel;
            ev.ReportedAt = total;
            return true;
        }
    }
}
