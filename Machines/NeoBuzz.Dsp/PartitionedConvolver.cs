#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4; moved from Neo Multiband's LinearSplit,
// docs/specs/mastering-tools.md §4.2): linear-phase FIRs on the audio thread.
//  - PartitionedConvolver: a uniformly partitioned convolution (overlap-save) of stereo audio with F real
//    FIRs: partitions of P samples, a complex FFT of 2P points that carries the left channel as its real
//    part and the right as its imaginary (each FIR is real, so one transform serves both), K partitions in
//    a frequency-domain delay line, two sets of the FIRs' partition spectra (the one playing and the next).
//  - SteppedFirDesign: each FIR from its zero-phase magnitude sampled on a grid of 2N points (N = K·P), an
//    inverse FFT, the taps made exactly symmetric and cut at ±D (no window: for responses that decay
//    exponentially, plain truncation of the frequency-sampled response is the most accurate cut), then each
//    partition's spectrum and the partitions to skip (every tap under 1e-9 of the FIR's largest). Two FIRs
//    share each transform (one as the real part, the other as the imaginary). The design runs in steps of
//    bounded work (a part of the grid, of a transform, of the taps or of a partition's spectrum: under 10 µs
//    each on the test runner) that the machine takes at fixed samples of its partitions, so no audio block
//    carries more than its share of a design, and the switch falls on the same sample however the host
//    splits the audio and however fast the machine runs: renders stay deterministic. The steps do the
//    one-shot design's arithmetic in its order, so the FIRs are the same bits however the work is split.
//  - LinearPhaseFilters: the two together with the switching policy: a design starts at a partition
//    boundary when the target has moved, at most every designEvery partitions; at the first boundary after
//    its last step the outputs crossfade from the old FIRs to the new over that partition (both sets run).
// The magnitudes come from the machine (IFirMagnitudes: Neo Multiband's crossovers). No allocation after
// construction (one per sample rate).

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>The zero-phase magnitudes <see cref="SteppedFirDesign"/> designs FIRs from.</summary>
    internal interface IFirMagnitudes
    {
        /// <summary>A design starts: take the parameters it is for (called on its first step).</summary>
        void Begin();

        /// <summary>
        /// Every FIR's magnitude at grid point <paramref name="k"/> (0 … M/2 of a grid of M points), where
        /// <paramref name="t"/> = tan(πk/M) (+∞ at M/2): one value per FIR into <paramref name="magnitudes"/>.
        /// </summary>
        void At(int k, double t, float[] magnitudes);
    }

    /// <summary>What <see cref="LinearPhaseFilters"/> designs towards (the machine's parameters).</summary>
    internal interface IFirTarget : IFirMagnitudes
    {
        /// <summary>The target differs from what the playing FIRs were designed for.</summary>
        bool Moved { get; }

        /// <summary>A design starts for the target as it is now (latch it for <see cref="IFirMagnitudes.Begin"/>).</summary>
        void Latch();

        /// <summary>The design latched last now plays.</summary>
        void TookOver();
    }

    /// <summary>
    /// Uniformly partitioned overlap-save convolution of stereo audio (left + i·right) with <see cref="Filters"/>
    /// real FIRs of up to K·P taps, in two sets of partition spectra.
    /// </summary>
    internal sealed class PartitionedConvolver
    {
        public readonly int P, K, Filters;

        /// <summary>The 2P-point transform (shared with the design's partition spectra).</summary>
        public readonly ComplexFft Block;

        // The frequency-domain delay line: the last K blocks' spectra (left + i·right).
        readonly float[][] xRe, xIm;
        int current;
        // Each FIR's partition spectra, two sets, scaled for the unscaled inverse transform: [set][filter · K + partition].
        internal readonly float[][][] HRe = new float[2][][], HIm = new float[2][][];
        // Per set and FIR, the first and last partition with a tap above its floor.
        internal readonly int[] FirstPartition, LastPartition;
        readonly float[] accRe, accIm, prevL, prevR;

        public PartitionedConvolver(int partitionLength, int partitions, int filters)
        {
            P = partitionLength;
            K = partitions;
            Filters = filters;
            Block = new ComplexFft(2 * P);
            xRe = new float[K][];
            xIm = new float[K][];
            for (int j = 0; j < K; j++)
            {
                xRe[j] = new float[2 * P];
                xIm[j] = new float[2 * P];
            }
            for (int s = 0; s < 2; s++)
            {
                HRe[s] = new float[Filters * K][];
                HIm[s] = new float[Filters * K][];
                for (int i = 0; i < Filters * K; i++)
                {
                    HRe[s][i] = new float[2 * P];
                    HIm[s][i] = new float[2 * P];
                }
            }
            FirstPartition = new int[Filters * 2];
            LastPartition = new int[Filters * 2];
            accRe = new float[2 * P];
            accIm = new float[2 * P];
            prevL = new float[P];
            prevR = new float[P];
        }

        public void Reset()
        {
            for (int j = 0; j < K; j++)
            {
                Array.Clear(xRe[j]);
                Array.Clear(xIm[j]);
            }
            Array.Clear(prevL);
            Array.Clear(prevR);
            current = 0;
        }

        /// <summary>A partition of P input samples in: the block's spectrum (the previous partition and this one) into the delay line.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Input(float[] inL, float[] inR)
        {
            var bre = xRe[current];
            var bim = xIm[current];
            for (int n = 0; n < P; n++)
            {
                bre[n] = prevL[n];
                bim[n] = prevR[n];
                bre[P + n] = prevL[n] = inL[n];
                bim[P + n] = prevR[n] = inR[n];
            }
            Block.Transform(bre, bim, false);
        }

        /// <summary>FIR <paramref name="f"/> of set <paramref name="s"/> over the delay line: Σ X[now − j] · H[j], the inverse transform, its last P samples.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Convolve(int s, int f, float[] outL, float[] outR)
        {
            Array.Clear(accRe);
            Array.Clear(accIm);
            int first = FirstPartition[s * Filters + f], end = LastPartition[s * Filters + f];
            int bins = 2 * P;
            var hRe = HRe[s];
            var hIm = HIm[s];
            for (int j = first; j <= end; j++)
            {
                int slot = (current - j + K) % K;
                ComplexFft.MultiplyAdd(accRe, accIm, xRe[slot], xIm[slot], hRe[f * K + j], hIm[f * K + j], bins);
            }
            Block.Transform(accRe, accIm, true);
            for (int n = 0; n < P; n++)
            {
                outL[n] = accRe[P + n];
                outR[n] = accIm[P + n];
            }
        }

        /// <summary>The partition is done: the delay line moves on.</summary>
        public void Advance() => current = (current + 1) % K;

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
            return false;
        }
    }

    /// <summary>
    /// Linear-phase FIRs of 2D + 1 taps centred at D (the last of the K·P taps stays 0), from zero-phase magnitudes,
    /// into a <see cref="PartitionedConvolver"/>'s set, in bounded steps (<see cref="Step"/>).
    /// </summary>
    internal sealed class SteppedFirDesign
    {
        // The work of one step: grid points, transform units (TransformPart: about a butterfly each; one partition's
        // transform at P = 512), tap pairs, taps scanned. Neo Multiband's three FIRs at 44.1 and 48 kHz are 89 steps.
        const int GridStep = 512, FftStep = 10240, TapStep = 2048, SkipStep = 8192;

        public readonly int D, M;
        readonly PartitionedConvolver conv;
        readonly IFirMagnitudes magnitudes;
        readonly int P, K, N, filters, pairs;
        readonly ComplexFft design;
        readonly double[] tan;
        // The grid: a complex array per pair of FIRs (FIR 2p real, FIR 2p + 1 imaginary).
        readonly float[][] gridRe, gridIm;
        readonly float[] segRe, segIm, mags;
        readonly float[][] taps;
        readonly float[] peak;
        // The steps (made once: stepping allocates nothing).
        readonly IEnumerator<bool> designer;
        int set;
        bool designing, designed;
        FftProgress fftAt;

        public SteppedFirDesign(PartitionedConvolver convolver, int halfLength, IFirMagnitudes magnitudes)
        {
            conv = convolver;
            this.magnitudes = magnitudes;
            P = conv.P;
            K = conv.K;
            N = K * P;
            D = halfLength;
            M = 2 * N;
            filters = conv.Filters;
            pairs = (filters + 1) / 2;
            design = new ComplexFft(M);
            tan = new double[M / 2 + 1];
            for (int k = 0; k < M / 2; k++) tan[k] = PortableMath.Tan(Math.PI * k / M);
            tan[M / 2] = double.PositiveInfinity;
            gridRe = new float[pairs][];
            gridIm = new float[pairs][];
            for (int p = 0; p < pairs; p++)
            {
                gridRe[p] = new float[M];
                gridIm[p] = new float[M];
            }
            segRe = new float[2 * P];
            segIm = new float[2 * P];
            mags = new float[2 * pairs];
            taps = new float[filters][];
            for (int f = 0; f < filters; f++) taps[f] = new float[N];
            peak = new float[filters];
            designer = Designer();
        }

        /// <summary>A design is under way and its last step hasn't run.</summary>
        public bool Designing => designing && !designed;

        /// <summary>The design's last step has run (until the next <see cref="Start"/> or <see cref="Taken"/>).</summary>
        public bool Designed => designed;

        /// <summary>Designs finished since construction.</summary>
        public int Designs { get; private set; }

        /// <summary>Steps taken since construction.</summary>
        public long Steps { get; private set; }

        /// <summary>A design into <paramref name="intoSet"/> starts (its magnitudes' Begin runs at its first step).</summary>
        public void Start(int intoSet)
        {
            set = intoSet;
            designing = true;
            designed = false;
        }

        /// <summary>The finished design has been taken (it plays now).</summary>
        public void Taken() => designing = designed = false;

        /// <summary>One bounded step of the design under way (nothing when there is none).</summary>
        public void Step()
        {
            designer.MoveNext();
            Steps++;
        }

        /// <summary>FIR <paramref name="f"/>'s taps as last designed (tests).</summary>
        internal float[] Taps(int f) => taps[f];

        /// <summary>
        /// The design, one bounded step per MoveNext (made once; it waits between designs). The one-shot design's
        /// arithmetic in its order: the zero-phase magnitudes on the grid, the inverse transforms (a pair of FIRs
        /// each), the taps, each partition's spectrum (a pair at a time), the partitions to skip.
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
                int s = set;
                magnitudes.Begin();
                for (int k = 0; k <= M / 2;)
                {
                    int end = k + GridStep >= M / 2 ? M / 2 + 1 : k + GridStep;
                    Grid(k, end);
                    k = end;
                    yield return true;
                }
                // The grid is written in bit-reversed order: the transforms start after their bit reversal (pass 1).
                bool done;
                for (int p = 0; p < pairs; p++)
                {
                    fftAt = new FftProgress { Pass = 1 };
                    do
                    {
                        done = design.TransformPart(gridRe[p], gridIm[p], true, ref fftAt, FftStep);
                        yield return true;
                    } while (!done);
                }
                for (int f = 0; f < filters; f++)
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
                    // Two FIRs at a time (real and imaginary parts); an odd last one alone.
                    for (int p = 0; p < pairs; p++)
                    {
                        int a = 2 * p, b = a + 1 < filters ? a + 1 : -1;
                        Segment(j, a, b);
                        fftAt = default;
                        do
                        {
                            done = conv.Block.TransformPart(segRe, segIm, false, ref fftAt, FftStep);
                            yield return true;
                        } while (!done);
                        if (b >= 0)
                        {
                            SplitPair(s, j, a, b);
                        }
                        else
                        {
                            Array.Copy(segRe, conv.HRe[s][a * K + j], 2 * P);
                            Array.Copy(segIm, conv.HIm[s][a * K + j], 2 * P);
                        }
                    }
                }
                // The outer partitions, where every tap is under 1e-9 of the FIR's largest (the tails, −180 dB), are skipped.
                int span = Math.Max(1, SkipStep / P);
                for (int f = 0; f < filters; f++)
                {
                    int first = K, last = -1;
                    for (int j = 0; j < K; j += span)
                    {
                        // Each scan its own step but the first, which ends the spectra's last.
                        if (f > 0 || j > 0) yield return true;
                        Skip(f, j, Math.Min(K, j + span), ref first, ref last);
                    }
                    conv.FirstPartition[s * filters + f] = first;
                    conv.LastPartition[s * filters + f] = last;
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
        void Grid(int from, int to)
        {
            for (int k = from; k < to; k++)
            {
                magnitudes.At(k, tan[k], mags);
                int r = design.Reversed(k);
                int q = k >= 1 && k < M / 2 ? design.Reversed(M - k) : -1;
                for (int p = 0; p < pairs; p++)
                {
                    float a = mags[2 * p], b = 2 * p + 1 < filters ? mags[2 * p + 1] : 0;
                    gridRe[p][r] = a;
                    gridIm[p][r] = b;
                    if (q >= 0)
                    {
                        gridRe[p][q] = a;
                        gridIm[p][q] = b;
                    }
                }
            }
        }

        /// <summary>
        /// FIR <paramref name="f"/>'s taps D ± n for n in <paramref name="from"/> … <paramref name="to"/> − 1: centred at D,
        /// exactly symmetric, scaled for both inverse transforms (1/M here, 1/2P in the convolution); and its largest. The
        /// last tap (2D + 1 = N − 1) stays 0.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Taps(int f, int from, int to)
        {
            float scale = (float)(1.0 / M / (2.0 * P));
            var src = f % 2 == 0 ? gridRe[f / 2] : gridIm[f / 2];
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

        /// <summary>
        /// Partition <paramref name="j"/>'s taps into segRe (FIR <paramref name="re"/>) and segIm (FIR <paramref name="im"/>, or
        /// zeros for −1), zero-padded to 2P: a step's copy of P samples (1024 and more from 88.2 kHz) in a method of its own.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Segment(int j, int re, int im)
        {
            var a = taps[re];
            var b = im >= 0 ? taps[im] : null;
            for (int n = 0; n < P; n++)
            {
                segRe[n] = a[j * P + n];
                segIm[n] = b != null ? b[j * P + n] : 0;
            }
            Array.Clear(segRe, P, P);
            Array.Clear(segIm, P, P);
        }

        /// <summary>Partition <paramref name="j"/>'s spectra of FIRs <paramref name="fa"/> and <paramref name="fb"/> (the real and imaginary parts of segRe/segIm's transform) into set <paramref name="s"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void SplitPair(int s, int j, int fa, int fb)
        {
            var aRe = conv.HRe[s][fa * K + j];
            var aIm = conv.HIm[s][fa * K + j];
            var bRe = conv.HRe[s][fb * K + j];
            var bIm = conv.HIm[s][fb * K + j];
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
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
    }

    /// <summary>
    /// A bank of linear-phase FIRs redesigned while playing: <see cref="PartitionedConvolver"/> and <see cref="SteppedFirDesign"/>
    /// with the switching policy (a design at most every designEvery partitions while the target moves; the outputs
    /// crossfading from the old FIRs to the new over the partition where a design takes over).
    /// </summary>
    internal sealed class LinearPhaseFilters
    {
        public readonly PartitionedConvolver Convolver;
        public readonly SteppedFirDesign Design;
        readonly IFirTarget target;
        readonly int designEvery;
        readonly float[][] oldL, oldR;
        int set;
        int cooldown;

        /// <summary>
        /// Tests: each design whole, in the partition that starts it, crossfading at once (as before the steps): the reference
        /// the spread design is measured against.
        /// </summary>
        internal bool DesignAtOnce;

        /// <summary>
        /// <paramref name="filters"/> FIRs of 2·<paramref name="halfLength"/> + 1 taps in partitions of <paramref name="partitionLength"/>
        /// (<paramref name="partitions"/> of them), designed towards <paramref name="target"/>. The first design runs whole here.
        /// </summary>
        public LinearPhaseFilters(int partitionLength, int partitions, int filters, int halfLength, IFirTarget target, int designEvery)
        {
            this.target = target;
            this.designEvery = designEvery;
            Convolver = new PartitionedConvolver(partitionLength, partitions, filters);
            Design = new SteppedFirDesign(Convolver, halfLength, target);
            oldL = new float[filters][];
            oldR = new float[filters][];
            for (int f = 0; f < filters; f++)
            {
                oldL[f] = new float[partitionLength];
                oldR[f] = new float[partitionLength];
            }
            set = 1;
            Start();
            while (!Design.Designed) Design.Step();
            set = 0;
            target.TookOver();
            Design.Taken();
            cooldown = 0;
        }

        public int P => Convolver.P;
        public int K => Convolver.K;

        public void Reset() => Convolver.Reset();

        /// <summary>One step of the design under way, if any (call at fixed samples of each partition).</summary>
        public void DesignStep()
        {
            if (Design.Designing) Design.Step();
        }

        void Start()
        {
            target.Latch();
            Design.Start(1 - set);
            cooldown = designEvery;
        }

        /// <summary>
        /// One partition: <paramref name="inL"/>, <paramref name="inR"/> hold its P input samples; FIRs 0 … <paramref name="count"/> − 1
        /// into <paramref name="outL"/>[f], <paramref name="outR"/>[f]. A design whose steps are done takes over, crossfading
        /// over this partition; when the target has moved and the last design started long enough ago, a new one starts.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] inL, float[] inR, int count, float[][] outL, float[][] outR)
        {
            Convolver.Input(inL, inR);

            // The design whose last step ran during the last partition takes over; then, when the target has moved and the
            // last design started designEvery partitions ago or more, a new one starts (at once: designed and taken now).
            if (cooldown > 0) cooldown--;
            bool redesign = Design.Designed;
            if (redesign)
            {
                target.TookOver();
                Design.Taken();
            }
            bool start = !Design.Designing && cooldown == 0 && target.Moved;
            if (start && DesignAtOnce)
            {
                Start();
                while (!Design.Designed) Design.Step();
                target.TookOver();
                Design.Taken();
                redesign = true;
                start = false;
            }

            if (redesign)
            {
                for (int f = 0; f < count; f++) Convolver.Convolve(set, f, oldL[f], oldR[f]);
                set = 1 - set;
            }
            for (int f = 0; f < count; f++) Convolver.Convolve(set, f, outL[f], outR[f]);
            if (redesign)
            {
                // From the old FIRs to the new over this partition, in a straight line.
                int p = Convolver.P;
                float step = 1f / p;
                for (int f = 0; f < count; f++)
                {
                    var nl = outL[f];
                    var nr = outR[f];
                    var ol = oldL[f];
                    var or = oldR[f];
                    for (int n = 0; n < p; n++)
                    {
                        float a = (n + 1) * step;
                        nl[n] = ol[n] + (nl[n] - ol[n]) * a;
                        nr[n] = or[n] + (nr[n] - or[n]) * a;
                    }
                }
            }
            Convolver.Advance();
            // Into the set not playing now (after a switch, the one just left).
            if (start) Start();
        }
    }
}
