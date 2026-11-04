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
//  - A new design runs at a partition boundary when the crossovers have moved, at most every
//    DesignEvery partitions, and the bands crossfade from the old FIRs to the new over that
//    partition (both sets run): the bands still sum to the delayed input throughout.
// No allocation after construction (one per sample rate).

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Multiband
{
    internal sealed class LinearSplit
    {
        /// <summary>A redesign at most every this many partitions (2048 samples at 48 kHz: 43 ms) while the crossovers move.</summary>
        public const int DesignEvery = 4;
        public const int Filters = 3;

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
            Design(initial, 0);
            set = 0;
            Designed = initial;
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
        /// everything above Crossover 2 and band 4 is silent. When <paramref name="target"/> differs
        /// from the design and the last design is long enough ago, designs again and crossfades.
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

            bool redesign = false;
            if (cooldown > 0) cooldown--;
            if (cooldown == 0 && !target.Equals(Designed))
            {
                Design(target, 1 - set);
                Designed = target;
                cooldown = DesignEvery;
                redesign = true;
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

        /// <summary>Designs the three FIRs for <paramref name="x"/> into set <paramref name="s"/>.</summary>
        void Design(in Xover x, int s)
        {
            Designs++;
            double t1 = MultibandDesign.Warp(x.F1, sampleRate), t2 = MultibandDesign.Warp(x.F2, sampleRate), t3 = MultibandDesign.Warp(x.F3, sampleRate);
            // The zero-phase magnitudes on the grid, real and even: bands 1 and 2 in one transform, band 3 in another.
            for (int k = 0; k <= M / 2; k++)
            {
                double t = tan[k];
                double l1 = MultibandDesign.Low(t, t1), h1 = MultibandDesign.High(t, t1);
                double l2 = MultibandDesign.Low(t, t2), h2 = MultibandDesign.High(t, t2);
                double l3 = MultibandDesign.Low(t, t3);
                gridRe[k] = (float)l1;
                gridIm[k] = (float)(h1 * l2);
                gridRe2[k] = (float)(h1 * h2 * l3);
                gridIm2[k] = 0;
            }
            for (int k = 1; k < M / 2; k++)
            {
                gridRe[M - k] = gridRe[k];
                gridIm[M - k] = gridIm[k];
                gridRe2[M - k] = gridRe2[k];
                gridIm2[M - k] = 0;
            }
            design.Transform(gridRe, gridIm, true);
            design.Transform(gridRe2, gridIm2, true);
            // Taps: centred at D, exactly symmetric, scaled for both inverse transforms (1/M here, 1/2P in Convolve).
            float scale = (float)(1.0 / M / (2.0 * P));
            for (int f = 0; f < Filters; f++)
            {
                var src = f == 0 ? gridRe : f == 1 ? gridIm : gridRe2;
                var tap = taps[f];
                Array.Clear(tap);
                tap[D] = src[0] * scale;
                for (int n = 1; n <= D; n++)
                {
                    float v = 0.5f * (src[n] + src[M - n]) * scale;
                    tap[D + n] = v;
                    tap[D - n] = v;
                }
            }
            // Partition spectra: bands 1 and 2 together (real and imaginary parts), then band 3.
            for (int j = 0; j < K; j++)
            {
                for (int n = 0; n < P; n++)
                {
                    segRe[n] = taps[0][j * P + n];
                    segIm[n] = taps[1][j * P + n];
                }
                Array.Clear(segRe, P, P);
                Array.Clear(segIm, P, P);
                block.Transform(segRe, segIm, false);
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
                for (int n = 0; n < P; n++)
                {
                    segRe[n] = taps[2][j * P + n];
                    segIm[n] = 0;
                }
                Array.Clear(segRe, P, P);
                Array.Clear(segIm, P, P);
                block.Transform(segRe, segIm, false);
                Array.Copy(segRe, hRe[s][2 * K + j], bins);
                Array.Copy(segIm, hIm[s][2 * K + j], bins);
            }
            // The outer partitions, where every tap is under 1e-9 of the FIR's largest (the tails, −180 dB), are skipped.
            for (int f = 0; f < Filters; f++)
            {
                float peak = 0;
                foreach (float t in taps[f]) peak = Math.Max(peak, Math.Abs(t));
                float floor = peak * 1e-9f;
                int first = K, last = -1;
                for (int j = 0; j < K; j++)
                {
                    bool any = false;
                    for (int n = 0; n < P && !any; n++) any = Math.Abs(taps[f][j * P + n]) > floor;
                    if (!any) continue;
                    if (j < first) first = j;
                    last = j;
                }
                firstPartition[s * Filters + f] = first;
                lastPartition[s * Filters + f] = last;
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
