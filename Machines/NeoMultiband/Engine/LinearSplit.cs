#nullable disable
// Neo Multiband (docs/specs/machines.md §5): the linear-phase crossovers. Three FIRs (bands 1, 2 and 3;
// two with three bands) of 2D + 1 taps on the shared library's partitioned convolution (NeoBuzz.Dsp
// PartitionedConvolver, SteppedFirDesign, LinearPhaseFilters: moved there from this file, the same
// arithmetic in the same order, docs/specs/mastering-tools.md §4.2): partitions of P samples, 16 of them,
// a complex FFT of 2P points carrying left + i·right. The last band is the delayed input minus the
// others, so the bands sum to it exactly, to float rounding, whatever the FIRs' truncation.
//  - Design: each FIR's zero-phase magnitude (MultibandDesign's Linkwitz–Riley shares) on a grid of 2N
//    points, made exactly symmetric and cut at ±D, in steps of bounded work the engine takes at fixed
//    samples of the partitions (DesignStep), so no audio block carries more than its share of a design
//    and the switch falls on the same sample however the host splits the audio.
//  - A new design starts at a partition boundary when the crossovers have moved, at most every
//    DesignEvery partitions; at the first partition boundary after its last step the bands crossfade
//    from the old FIRs to the new over that partition (both sets run): the bands still sum to the
//    delayed input throughout.
// No allocation after construction (one per sample rate).

using System;
using System.Runtime.CompilerServices;
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

        public readonly int P, K, N, D, M;
        readonly LinearPhaseFilters filters;
        readonly Target target;
        // The delayed input (the dry signal and the last band), a ring.
        readonly float[] ringL, ringR;
        readonly int ringMask;
        long written;

        /// <summary>The bands of the partition just processed, P samples each: [band][sample]; the dry signal (the input, D samples back).</summary>
        public readonly float[][] BandL = new float[MultibandDesign.MaxBands][], BandR = new float[MultibandDesign.MaxBands][];
        public readonly float[] DryL, DryR;

        /// <summary>The crossovers the running FIRs were designed for.</summary>
        public Xover Designed { get; private set; }

        /// <summary>Designs made since construction.</summary>
        public int Designs => filters.Design.Designs;

        /// <summary>Design steps taken since construction, the initial design's included (the tests count them).</summary>
        internal long Steps => filters.Design.Steps;

        /// <summary>
        /// Tests: each design whole, in the partition that starts it, crossfading at once (as before the steps): the reference
        /// the spread design is measured against.
        /// </summary>
        internal bool DesignAtOnce
        {
            get => filters.DesignAtOnce;
            set => filters.DesignAtOnce = value;
        }

        public LinearSplit(double sampleRate, in Xover initial)
        {
            P = MultibandDesign.PartitionLength(sampleRate);
            K = MultibandDesign.Partitions;
            N = K * P;
            D = MultibandDesign.HalfLength(sampleRate);
            M = 2 * N;
            target = new Target(this, sampleRate) { Want = initial };
            filters = new LinearPhaseFilters(P, K, Filters, D, target, DesignEvery);
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
            DryL = new float[P];
            DryR = new float[P];
        }

        /// <summary>Samples from an input to the bands' (and the dry signal's) output: D + P.</summary>
        public int Latency => D + P;

        public void Reset()
        {
            filters.Reset();
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
        }

        /// <summary>
        /// One partition: <paramref name="inL"/>, <paramref name="inR"/> hold its P input samples. Fills
        /// BandL/BandR (3 or 4 bands) and DryL/DryR, aligned: each the input D samples back, the bands
        /// filtered. <paramref name="third"/>: run band 3's FIR (four bands); without it band 3 is
        /// everything above Crossover 2 and band 4 is silent. A design whose steps are done takes over,
        /// crossfading over this partition; when <paramref name="target"/> differs from the design and
        /// the last design started long enough ago, a new one starts (its steps: DesignStep).
        /// </summary>
        // Fully optimised at its first compile (the warm-up's), so the audio thread never compiles an on-stack replacement of its loops (AudioWarmUp).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

            this.target.Want = target;
            int count = third ? 3 : 2;
            filters.Process(inL, inR, count, BandL, BandR);

            // The rest is the last band: with four bands band 4, with three band 3 (band 4 silent).
            var ll = BandL[count];
            var lr = BandR[count];
            for (int n = 0; n < P; n++)
            {
                float sl = DryL[n] - BandL[0][n] - BandL[1][n];
                float sr = DryR[n] - BandR[0][n] - BandR[1][n];
                if (count == 3)
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
        }

        /// <summary>
        /// One step of the design under way, if any: the engine calls it at every DesignStride-th sample of a partition but
        /// its first, so the steps fall on the same samples however the audio comes in blocks.
        /// </summary>
        public void DesignStep() => filters.DesignStep();

        /// <summary>FIR <paramref name="f"/>'s taps as now designed (tests).</summary>
        internal float[] Taps(int f) => filters.Design.Taps(f);

        public bool HoldsSubnormals() => filters.Convolver.HoldsSubnormals();

        public bool HoldsNonFinite()
        {
            if (filters.Convolver.HoldsNonFinite()) return true;
            foreach (var x in ringL) if (!float.IsFinite(x)) return true;
            foreach (var x in ringR) if (!float.IsFinite(x)) return true;
            return false;
        }

        /// <summary>
        /// The crossovers as the FIRs' design target: the Linkwitz–Riley shares of bands 1, 2 and 3 on the grid (bands 1
        /// and 2 in one transform, band 3 in another), from the crossovers latched when the design started.
        /// </summary>
        sealed class Target : IFirTarget
        {
            readonly LinearSplit split;
            readonly double sampleRate;
            Xover pending;
            double t1, t2, t3;

            public Target(LinearSplit split, double sampleRate)
            {
                this.split = split;
                this.sampleRate = sampleRate;
            }

            /// <summary>The crossovers asked for now.</summary>
            public Xover Want;

            public bool Moved => !Want.Equals(split.Designed);

            public void Latch() => pending = Want;

            public void TookOver() => split.Designed = pending;

            public void Begin()
            {
                t1 = MultibandDesign.Warp(pending.F1, sampleRate);
                t2 = MultibandDesign.Warp(pending.F2, sampleRate);
                t3 = MultibandDesign.Warp(pending.F3, sampleRate);
            }

            public void At(int k, double t, float[] magnitudes)
            {
                double l1 = MultibandDesign.Low(t, t1), h1 = MultibandDesign.High(t, t1);
                double l2 = MultibandDesign.Low(t, t2), h2 = MultibandDesign.High(t, t2);
                double l3 = MultibandDesign.Low(t, t3);
                magnitudes[0] = (float)l1;
                magnitudes[1] = (float)(h1 * l2);
                magnitudes[2] = (float)(h1 * h2 * l3);
            }
        }
    }
}
