#nullable disable
// Neo Low End (docs/specs/mastering-tools.md §5.5): the linear-phase split of the low band, Neo Multiband's linear
// crossover (LinearSplit) cut to one FIR. One FIR of 2D + 1 taps (D samples each side of the centre) on the shared
// library's partitioned convolution (PartitionedConvolver, SteppedFirDesign, LinearPhaseFilters), partitions of P
// samples, 16 of them, a complex FFT of 2P points carrying left + i·right. Its zero-phase magnitude is a Linkwitz–
// Riley 24 low-pass (1 / (1 + w⁴), w the warped frequency over the crossover's), designed on a grid of 2N points
// and cut at ±D, in steps of bounded work taken at fixed samples of each partition (DesignStep), so the switch
// falls on the same sample however the host splits the audio; a new design takes over at a partition boundary,
// crossfading the FIR's output over that partition. The input delayed by D (the dry signal) comes out beside it, so
// the high band is the dry signal minus the low band: the two sum to it, to float rounding, whatever the FIR's
// truncation, and what is above the crossover is the delayed input untouched.
// No allocation after construction (one per sample rate).

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.LowEnd
{
    internal static class LowEndDesign
    {
        public const double MinHz = 20, MaxHz = 300;

        /// <summary>The linear-phase FIR's partitions of the convolution.</summary>
        public const int Partitions = 16;

        /// <summary>1 up to 50 kHz, 2 up to 100 kHz, then 4: the FIR keeps its length in time.</summary>
        public static int Scale(double sampleRate) => sampleRate <= 50000 ? 1 : sampleRate <= 100000 ? 2 : 4;

        /// <summary>The convolution's partition (and block) length, samples.</summary>
        public static int PartitionLength(double sampleRate) => 512 * Scale(sampleRate);

        /// <summary>D: the FIR's taps each side of the centre.</summary>
        public static int HalfLength(double sampleRate) => Partitions * PartitionLength(sampleRate) / 2 - 1;

        /// <summary>Linear phase's latency: the FIR's centre and one partition of buffering.</summary>
        public static int LinearLatency(double sampleRate) => HalfLength(sampleRate) + PartitionLength(sampleRate);

        /// <summary>The most latency at any supported rate (384 kHz).</summary>
        public static int MaxLatency => LinearLatency(384000);

        /// <summary>The bilinear warp, tan(πf/fs), below Nyquist.</summary>
        public static double Warp(double hz, double sampleRate) =>
            PortableMath.Tan(Math.PI * Math.Clamp(hz, 0.0, 0.49999 * sampleRate) / sampleRate);

        /// <summary>The low share of the crossover at warped frequency <paramref name="tc"/>, for warped frequency <paramref name="t"/>: 1/(1 + (t/tc)⁴).</summary>
        public static double Low(double t, double tc)
        {
            double r = t / tc;
            r *= r;
            r *= r;
            return 1 / (1 + r);
        }

        /// <summary>The low band's magnitude (0 … 1) at <paramref name="hz"/> for a crossover at <paramref name="crossover"/>, in either phase mode.</summary>
        public static double Band(double hz, double crossover, double sampleRate)
        {
            double t = hz >= 0.5 * sampleRate ? double.PositiveInfinity : Warp(hz, sampleRate);
            return Low(t, Warp(crossover, sampleRate));
        }
    }

    internal sealed class LowLinear
    {
        /// <summary>A redesign at most every this many partitions while the crossover moves.</summary>
        public const int DesignEvery = 4;

        /// <summary>Samples between two steps of a design (the engine takes one at every DesignStride-th sample of a partition but its first).</summary>
        public const int DesignStride = 16;

        public readonly int P, K, N, D;
        readonly LinearPhaseFilters filters;
        readonly Target target;
        readonly float[] ringL, ringR;
        readonly int ringMask;
        readonly float[][] firL = { null }, firR = { null };
        long written;

        /// <summary>The low band of the partition just processed (P samples) and the dry signal, the input D samples back.</summary>
        public readonly float[] LowL, LowR, DryL, DryR;

        /// <summary>The crossover (Hz) the running FIR was designed for.</summary>
        public double Designed { get; private set; }

        public LowLinear(double sampleRate, double initial)
        {
            P = LowEndDesign.PartitionLength(sampleRate);
            K = LowEndDesign.Partitions;
            N = K * P;
            D = LowEndDesign.HalfLength(sampleRate);
            target = new Target(this, sampleRate) { Want = initial };
            filters = new LinearPhaseFilters(P, K, 1, D, target, DesignEvery);
            int ring = 1;
            while (ring < D + 2 * P) ring <<= 1;
            ringL = new float[ring];
            ringR = new float[ring];
            ringMask = ring - 1;
            LowL = new float[P];
            LowR = new float[P];
            DryL = new float[P];
            DryR = new float[P];
            firL[0] = LowL;
            firR[0] = LowR;
        }

        /// <summary>Samples from an input to the low band's (and the dry signal's) output: D + P.</summary>
        public int Latency => D + P;

        public void Reset()
        {
            filters.Reset();
            Array.Clear(ringL);
            Array.Clear(ringR);
            Array.Clear(LowL);
            Array.Clear(LowR);
            Array.Clear(DryL);
            Array.Clear(DryR);
            written = 0;
        }

        /// <summary>One partition: <paramref name="inL"/>, <paramref name="inR"/> hold its P input samples; fills LowL/LowR and DryL/DryR, aligned.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] inL, float[] inR, double crossover)
        {
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
            target.Want = crossover;
            filters.Process(inL, inR, 1, firL, firR);
        }

        /// <summary>One step of the design under way, if any, at the DesignStride-th sample of a partition.</summary>
        public void DesignStep() => filters.DesignStep();

        /// <summary>The FIR's taps as now designed (tests).</summary>
        internal float[] Taps() => filters.Design.Taps(0);

        public bool HoldsSubnormals() => filters.Convolver.HoldsSubnormals();

        public bool HoldsNonFinite()
        {
            if (filters.Convolver.HoldsNonFinite()) return true;
            foreach (var x in ringL) if (!float.IsFinite(x)) return true;
            foreach (var x in ringR) if (!float.IsFinite(x)) return true;
            return false;
        }

        sealed class Target : IFirTarget
        {
            readonly LowLinear split;
            readonly double sampleRate;
            double pending, t1;

            public Target(LowLinear split, double sampleRate)
            {
                this.split = split;
                this.sampleRate = sampleRate;
            }

            public double Want;

            public bool Moved => Want != split.Designed;

            public void Latch() => pending = Want;

            public void TookOver() => split.Designed = pending;

            public void Begin() => t1 = LowEndDesign.Warp(pending, sampleRate);

            public void At(int k, double t, float[] magnitudes) => magnitudes[0] = (float)LowEndDesign.Low(t, t1);
        }
    }
}
