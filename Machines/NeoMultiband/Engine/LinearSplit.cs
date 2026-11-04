#nullable disable
// Neo Multiband (docs/specs/machines.md §5): the linear-phase crossovers. Three FIRs (bands 1, 2 and 3;
// two with three bands) of 2D + 1 taps run as a uniformly partitioned convolution (overlap-save):
// partitions of P samples, a complex FFT of 2P points that carries the left channel as its real part
// and the right as its imaginary (each FIR is real, so one transform serves both), 16 partitions in
// a frequency-domain delay line. The last band is the delayed input minus the others, so the bands
// sum to it exactly, to float rounding, whatever the FIRs' truncation.
//  - Design (MultibandDesign): each FIR's zero-phase magnitude sampled on a grid of 2N points, an
//    inverse FFT, the taps made exactly symmetric and cut at ±D (no window: the impulse responses
//    decay exponentially, and plain truncation of the frequency-sampled response is the most
//    accurate cut), then each partition's spectrum. Two FIRs share each transform (one as the real
//    part, the other as the imaginary).
//  - A new design starts at a partition boundary when the crossovers have moved, at most every
//    DesignEvery partitions, and runs in steps of bounded work (a part of the grid, of a transform,
//    of the taps or of the partition spectra: under 10 µs each on the test runner) that the engine
//    takes at fixed samples of the partitions after, one every DesignStride samples (DesignStep).
//    So no audio block carries more than its share of a design (a whole one took about 1 ms, in
//    the block that held the partition boundary), and the switch falls on the same sample however
//    the host splits the audio and however fast the machine runs: renders stay deterministic. The
//    current FIRs play meanwhile; at the first partition boundary after the last step the bands
//    crossfade from the old FIRs to the new over that partition (both sets run): the bands still
//    sum to the delayed input throughout. The steps do the one-shot design's arithmetic, in its
//    order, so the FIRs are the same bits as when the whole design ran at once.
// No allocation after construction (one per sample rate).

using System;
using System.Collections.Generic;
using NeoBuzz.Dsp;

namespace NeoBuzz.Multiband
{
    internal sealed class LinearSplit
    {
        /// <summary>A redesign at most every this many partitions (2048 samples at 48 kHz: 43 ms) while the crossovers move.</summary>
        public const int DesignEvery = 4;
        public const int Filters = 3;

        /// <summary>
        /// Samples between two steps of a design: the engine takes one at every DesignStride-th sample of a partition but
        /// its first, where the partition itself runs (31 a partition at 44.1 and 48 kHz).
        /// </summary>
        public const int DesignStride = 16;

        // The work of one step: grid points, transform units (TransformPart: about a butterfly each; one partition's
        // transform at 44.1 and 48 kHz), tap pairs, taps scanned. A design is 88 steps at 44.1 and 48 kHz (done within the
        // 3 partitions after it starts, which hold 93), 181 at 88.2 and 96 kHz (within 3) and 401 at 192 kHz (within 4).
        const int GridStep = 512, FftStep = 10240, TapStep = 2048, SkipStep = 8192;

        public readonly int P, K, N, D, M;
        readonly double sampleRate;
        readonly ComplexFft block, design;

        // The frequency-domain delay line: the last K blocks' spectra (left + i·right).
        readonly float[][] xRe, xIm;
        int current;
        // Each FIR's partition spectra, two sets (now and next), scaled for the unscaled inverse transform.
        readonly float[][][] hRe = new float[2][][], hIm = new float[2][][];
        int set;
        readonly int[] firstPartition = new int[Filters * 2], lastPartition = new int[Filters * 2];
        readonly float[] accRe, accIm, prevL, prevR;
        // The design's buffers.
        readonly double[] tan;
        readonly float[] gridRe, gridIm, gridRe2, gridIm2, segRe, segIm;
        readonly float[][] taps;
        readonly float[] peak = new float[Filters];
        // The design under way: its crossovers, the set it fills, its steps (made once: stepping allocates nothing).
        readonly IEnumerator<bool> designer;
        Xover pending;
        int pendingSet;
        bool designing, designed;
        FftProgress fftAt;
        // The delayed input (the dry signal and the last band), a ring.
        readonly float[] ringL, ringR;
        readonly int ringMask;
        long written;
        int cooldown;

        /// <summary>The bands of the partition just processed, P samples each: [band][sample]; the dry signal (the input, D samples back).</summary>
        public readonly float[][] BandL = new float[MultibandDesign.MaxBands][], BandR = new float[MultibandDesign.MaxBands][];
        public readonly float[] DryL, DryR;
        readonly float[][] oldL = new float[Filters][], oldR = new float[Filters][];

        /// <summary>The crossovers the running FIRs were designed for.</summary>
        public Xover Designed { get; private set; }

        /// <summary>Designs made since construction (the tests count them).</summary>
        public int Designs { get; private set; }

        /// <summary>Design steps taken since construction, the initial design's included (the tests count them).</summary>
        internal long Steps { get; private set; }

        /// <summary>A design is under way (its steps still to come, or done and waiting for the next partition).</summary>
        public bool Designing => designing;

        /// <summary>
        /// Tests: each design whole, in the partition that starts it, crossfading at once (as before the steps): the reference
        /// the spread design is measured against.
        /// </summary>
        internal bool DesignAtOnce;

        public LinearSplit(double sampleRate, in Xover initial)
        {
            this.sampleRate = sampleRate;
            P = MultibandDesign.PartitionLength(sampleRate);
            K = MultibandDesign.Partitions;
            N = K * P;
            D = MultibandDesign.HalfLength(sampleRate);
            M = 2 * N;
            block = new ComplexFft(2 * P);
            design = new ComplexFft(M);
            xRe = new float[K][];
            xIm = new float[K][];
            for (int j = 0; j < K; j++)
            {
                xRe[j] = new float[2 * P];
                xIm[j] = new float[2 * P];
            }
            for (int s = 0; s < 2; s++)
            {
                hRe[s] = new float[Filters * K][];
                hIm[s] = new float[Filters * K][];
                for (int i = 0; i < Filters * K; i++)
                {
                    hRe[s][i] = new float[2 * P];
                    hIm[s][i] = new float[2 * P];
                }
            }
            accRe = new float[2 * P];
            accIm = new float[2 * P];
            prevL = new float[P];
            prevR = new float[P];
            tan = new double[M / 2 + 1];
            for (int k = 0; k < M / 2; k++) tan[k] = PortableMath.Tan(Math.PI * k / M);
            tan[M / 2] = double.PositiveInfinity;
            gridRe = new float[M];
            gridIm = new float[M];
            gridRe2 = new float[M];
            gridIm2 = new float[M];
            segRe = new float[2 * P];
            segIm = new float[2 * P];
            taps = new float[Filters][];
            for (int f = 0; f < Filters; f++) taps[f] = new float[N];
            int ring = 1;
            while (ring < D + 2 * P) ring <<= 1;
            ringL = new float[ring];
            ringR = new float[ring];
            ringMask = ring - 1;
            for (int b = 0; b < MultibandDesign.MaxBands; b++)
            {
                BandL[b] = new float[P];
                BandR[b] = new float[P];
            }
            for (int f = 0; f < Filters; f++)
            {
                oldL[f] = new float[P];
                oldR[f] = new float[P];
            }
            DryL = new float[P];
            DryR = new float[P];
            designer = Designer();
            set = 1;
            Start(initial);
            while (!designed) Step();
            set = 0;
            Designed = initial;
            designing = designed = false;
            cooldown = 0;
        }

        /// <summary>Samples from an input to the bands' (and the dry signal's) output: D + P.</summary>
        public int Latency => D + P;

        public void Reset()
        {
            for (int j = 0; j < K; j++)
            {
                Array.Clear(xRe[j]);
                Array.Clear(xIm[j]);
            }
            Array.Clear(prevL);
            Array.Clear(prevR);
            Array.Clear(ringL);
            Array.Clear(ringR);
            for (int b = 0; b < MultibandDesign.MaxBands; b++)
            {
                Array.Clear(BandL[b]);
                Array.Clear(BandR[b]);
            }
            Array.Clear(DryL);
            Array.Clear(DryR);
            written = 0;
            current = 0;
        }

        /// <summary>
        /// One partition: <paramref name="inL"/>, <paramref name="inR"/> hold its P input samples. Fills
        /// BandL/BandR (3 or 4 bands) and DryL/DryR, aligned: each the input D samples back, the bands
        /// filtered. <paramref name="third"/>: run band 3's FIR (four bands); without it band 3 is
        /// everything above Crossover 2 and band 4 is silent. A design whose steps are done takes over,
        /// crossfading over this partition; when <paramref name="target"/> differs from the design and
        /// the last design started long enough ago, a new one starts (its steps: DesignStep).
        /// </summary>
        public void Process(float[] inL, float[] inR, bool third, in Xover target)
        {
            // The dry ring.
            for (int n = 0; n < P; n++)
            {
                int w = (int)((written + n) & ringMask);
                ringL[w] = inL[n];
                ringR[w] = inR[n];
            }
            written += P;
            long from = written - P - D;
            for (int n = 0; n < P; n++)
            {
                long t = from + n;
                int r = (int)(t & ringMask);
                DryL[n] = t >= 0 ? ringL[r] : 0;
                DryR[n] = t >= 0 ? ringR[r] : 0;
            }

            // The block's spectrum: the previous partition and this one (overlap-save).
            var bre = xRe[current];
            var bim = xIm[current];
            for (int n = 0; n < P; n++)
            {
                bre[n] = prevL[n];
                bim[n] = prevR[n];
                bre[P + n] = prevL[n] = inL[n];
                bim[P + n] = prevR[n] = inR[n];
            }
            block.Transform(bre, bim, false);

            // The design whose last step ran during the last partition takes over; then, when the crossovers have moved and
            // the last design started DesignEvery partitions ago or more, a new one starts (at once: designed and taken now).
            if (cooldown > 0) cooldown--;
            bool redesign = designed;
            if (redesign)
            {
                Designed = pending;
                designing = designed = false;
            }
            bool start = !designing && cooldown == 0 && !target.Equals(Designed);
            if (start && DesignAtOnce)
            {
                Start(target);
                while (!designed) Step();
                Designed = pending;
                designing = designed = false;
                redesign = true;
                start = false;
            }

            int filters = third ? 3 : 2;
            if (redesign)
            {
                for (int f = 0; f < filters; f++) Convolve(set, f, oldL[f], oldR[f]);
                set = 1 - set;
            }
            for (int f = 0; f < filters; f++) Convolve(set, f, BandL[f], BandR[f]);
            if (redesign)
            {
                // From the old FIRs to the new over this partition, in a straight line.
                float step = 1f / P;
                for (int f = 0; f < filters; f++)
                {
                    var nl = BandL[f];
                    var nr = BandR[f];
                    var ol = oldL[f];
                    var or = oldR[f];
                    for (int n = 0; n < P; n++)
                    {
                        float a = (n + 1) * step;
                        nl[n] = ol[n] + (nl[n] - ol[n]) * a;
                        nr[n] = or[n] + (nr[n] - or[n]) * a;
                    }
                }
            }
            // The rest is the last band: with four bands band 4, with three band 3 (band 4 silent).
            int last = filters;
            var ll = BandL[last];
            var lr = BandR[last];
            for (int n = 0; n < P; n++)
            {
                float sl = DryL[n] - BandL[0][n] - BandL[1][n];
                float sr = DryR[n] - BandR[0][n] - BandR[1][n];
                if (filters == 3)
                {
                    sl -= BandL[2][n];
                    sr -= BandR[2][n];
                }
                ll[n] = sl;
                lr[n] = sr;
            }
            if (!third)
            {
                Array.Clear(BandL[3]);
                Array.Clear(BandR[3]);
            }
            current = (current + 1) % K;
            // Into the set not playing now (after a switch, the one just left).
            if (start) Start(target);
        }

        /// <summary>
        /// One step of the design under way, if any: the engine calls it at every DesignStride-th sample of a partition but
        /// its first, so the steps fall on the same samples however the audio comes in blocks.
        /// </summary>
        public void DesignStep()
        {
            if (designing && !designed) Step();
        }

        void Start(in Xover x)
        {
            pending = x;
            pendingSet = 1 - set;
            designing = true;
            designed = false;
            cooldown = DesignEvery;
        }

        void Step()
        {
            designer.MoveNext();
            Steps++;
        }

        /// <summary>One FIR over the delay line: Σ X[now − j] · H[j], then the inverse transform; its last P samples.</summary>
        void Convolve(int s, int f, float[] outL, float[] outR)
        {
            Array.Clear(accRe);
            Array.Clear(accIm);
            int first = firstPartition[s * Filters + f], end = lastPartition[s * Filters + f];
            int bins = 2 * P;
            for (int j = first; j <= end; j++)
            {
                int slot = (current - j + K) % K;
                ComplexFft.MultiplyAdd(accRe, accIm, xRe[slot], xIm[slot], hRe[s][f * K + j], hIm[s][f * K + j], bins);
            }
            block.Transform(accRe, accIm, true);
            for (int n = 0; n < P; n++)
            {
                outL[n] = accRe[P + n];
                outR[n] = accIm[P + n];
            }
        }

        /// <summary>
        /// The design of <c>pending</c>'s three FIRs into set <c>pendingSet</c>, one bounded step per MoveNext (made once; it
        /// waits between designs). The one-shot design's arithmetic in its order: the zero-phase magnitudes on the grid
        /// (bands 1 and 2 in one transform, band 3 in another), the inverse transforms, the taps, each partition's spectrum,
        /// the partitions to skip.
        /// </summary>
        IEnumerator<bool> Designer()
        {
            for (;;)
            {
                if (!designing || designed)
                {
                    yield return false;
                    continue;
                }
                int s = pendingSet;
                double t1 = MultibandDesign.Warp(pending.F1, sampleRate), t2 = MultibandDesign.Warp(pending.F2, sampleRate), t3 = MultibandDesign.Warp(pending.F3, sampleRate);
                for (int k = 0; k <= M / 2;)
                {
                    int end = k + GridStep >= M / 2 ? M / 2 + 1 : k + GridStep;
                    Grid(k, end, t1, t2, t3);
                    k = end;
                    yield return true;
                }
                // The grid is written in bit-reversed order: the transforms start after their bit reversal (pass 1).
                bool done;
                fftAt = new FftProgress { Pass = 1 };
                do
                {
                    done = design.TransformPart(gridRe, gridIm, true, ref fftAt, FftStep);
                    yield return true;
                } while (!done);
                fftAt = new FftProgress { Pass = 1 };
                do
                {
                    done = design.TransformPart(gridRe2, gridIm2, true, ref fftAt, FftStep);
                    yield return true;
                } while (!done);
                for (int f = 0; f < Filters; f++)
                {
                    peak[f] = 0;
                    for (int n = 0; n <= D; n += TapStep)
                    {
                        Taps(f, n, Math.Min(n + TapStep, D + 1));
                        yield return true;
                    }
                }
                for (int j = 0; j < K; j++)
                {
                    // Bands 1 and 2 together (real and imaginary parts), then band 3.
                    for (int n = 0; n < P; n++)
                    {
                        segRe[n] = taps[0][j * P + n];
                        segIm[n] = taps[1][j * P + n];
                    }
                    Array.Clear(segRe, P, P);
                    Array.Clear(segIm, P, P);
                    fftAt = default;
                    do
                    {
                        done = block.TransformPart(segRe, segIm, false, ref fftAt, FftStep);
                        yield return true;
                    } while (!done);
                    SplitPair(s, j);
                    for (int n = 0; n < P; n++)
                    {
                        segRe[n] = taps[2][j * P + n];
                        segIm[n] = 0;
                    }
                    Array.Clear(segRe, P, P);
                    Array.Clear(segIm, P, P);
                    fftAt = default;
                    do
                    {
                        done = block.TransformPart(segRe, segIm, false, ref fftAt, FftStep);
                        yield return true;
                    } while (!done);
                    Array.Copy(segRe, hRe[s][2 * K + j], 2 * P);
                    Array.Copy(segIm, hIm[s][2 * K + j], 2 * P);
                }
                // The outer partitions, where every tap is under 1e-9 of the FIR's largest (the tails, −180 dB), are skipped.
                int span = Math.Max(1, SkipStep / P);
                for (int f = 0; f < Filters; f++)
                {
                    int first = K, last = -1;
                    for (int j = 0; j < K; j += span)
                    {
                        // Each scan its own step but the first, which ends the spectra's last.
                        if (f > 0 || j > 0) yield return true;
                        Skip(f, j, Math.Min(K, j + span), ref first, ref last);
                    }
                    firstPartition[s * Filters + f] = first;
                    lastPartition[s * Filters + f] = last;
                }
                Designs++;
                designed = true;
                yield return true;
            }
        }

        /// <summary>
        /// Grid points <paramref name="from"/> … <paramref name="to"/> − 1 of the zero-phase magnitudes (real and even), and
        /// their mirrors, each where the transform's bit reversal would move it (the values are only moved, so the bits match).
        /// </summary>
        void Grid(int from, int to, double t1, double t2, double t3)
        {
            for (int k = from; k < to; k++)
            {
                double t = tan[k];
                double l1 = MultibandDesign.Low(t, t1), h1 = MultibandDesign.High(t, t1);
                double l2 = MultibandDesign.Low(t, t2), h2 = MultibandDesign.High(t, t2);
                double l3 = MultibandDesign.Low(t, t3);
                float a = (float)l1, b = (float)(h1 * l2), c = (float)(h1 * h2 * l3);
                int r = design.Reversed(k);
                gridRe[r] = a;
                gridIm[r] = b;
                gridRe2[r] = c;
                gridIm2[r] = 0;
                if (k >= 1 && k < M / 2)
                {
                    int q = design.Reversed(M - k);
                    gridRe[q] = a;
                    gridIm[q] = b;
                    gridRe2[q] = c;
                    gridIm2[q] = 0;
                }
            }
        }

        /// <summary>
        /// FIR <paramref name="f"/>'s taps D ± n for n in <paramref name="from"/> … <paramref name="to"/> − 1: centred at D,
        /// exactly symmetric, scaled for both inverse transforms (1/M here, 1/2P in Convolve); and its largest. The last tap
        /// (2D + 1 = N − 1) stays 0.
        /// </summary>
        void Taps(int f, int from, int to)
        {
            float scale = (float)(1.0 / M / (2.0 * P));
            var src = f == 0 ? gridRe : f == 1 ? gridIm : gridRe2;
            var tap = taps[f];
            float most = peak[f];
            for (int n = from; n < to; n++)
            {
                if (n == 0)
                {
                    tap[D] = src[0] * scale;
                    most = Math.Max(most, Math.Abs(tap[D]));
                    continue;
                }
                float v = 0.5f * (src[n] + src[M - n]) * scale;
                tap[D + n] = v;
                tap[D - n] = v;
                most = Math.Max(most, Math.Abs(v));
            }
            peak[f] = most;
        }

        /// <summary>Partition <paramref name="j"/>'s spectrum of FIRs 1 and 2 (the real and imaginary parts of segRe/segIm's transform) into set <paramref name="s"/>.</summary>
        void SplitPair(int s, int j)
        {
            var aRe = hRe[s][0 * K + j];
            var aIm = hIm[s][0 * K + j];
            var bRe = hRe[s][1 * K + j];
            var bIm = hIm[s][1 * K + j];
            int bins = 2 * P;
            for (int k = 0; k < bins; k++)
            {
                int m = (bins - k) & (bins - 1);
                float zr = segRe[k], zi = segIm[k], cr = segRe[m], ci = segIm[m];
                // Z[k] = A[k] + i·B[k], with A and B the spectra of real signals.
                aRe[k] = 0.5f * (zr + cr);
                aIm[k] = 0.5f * (zi - ci);
                bRe[k] = 0.5f * (zi + ci);
                bIm[k] = 0.5f * (cr - zr);
            }
        }

        /// <summary>Partitions <paramref name="from"/> … <paramref name="to"/> − 1 of FIR <paramref name="f"/>: the first and last with a tap above its floor.</summary>
        void Skip(int f, int from, int to, ref int first, ref int last)
        {
            float floor = peak[f] * 1e-9f;
            var tap = taps[f];
            for (int j = from; j < to; j++)
            {
                bool any = false;
                for (int n = 0; n < P && !any; n++) any = Math.Abs(tap[j * P + n]) > floor;
                if (!any) continue;
                if (j < first) first = j;
                last = j;
            }
        }

        /// <summary>FIR <paramref name="f"/>'s taps as now designed (tests).</summary>
        internal float[] Taps(int f) => taps[f];

        public bool HoldsSubnormals()
        {
            for (int j = 0; j < K; j++)
                for (int k = 0; k < 2 * P; k++)
                    if (float.IsSubnormal(xRe[j][k]) || float.IsSubnormal(xIm[j][k])) return true;
            foreach (var x in prevL) if (float.IsSubnormal(x)) return true;
            foreach (var x in prevR) if (float.IsSubnormal(x)) return true;
            return false;
        }

        public bool HoldsNonFinite()
        {
            for (int j = 0; j < K; j++)
                for (int k = 0; k < 2 * P; k++)
                    if (!float.IsFinite(xRe[j][k]) || !float.IsFinite(xIm[j][k])) return true;
            foreach (var x in ringL) if (!float.IsFinite(x)) return true;
            foreach (var x in ringR) if (!float.IsFinite(x)) return true;
            return false;
        }
    }
}
