#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4; first user: Dye Essence's convolution reverb, docs/specs/dye-essence.md §4.10):
// a zero-latency, non-uniformly partitioned FFT convolution of a stereo signal with a stereo impulse response (left with the
// left response, right with the right: a mono response is both).
//
//  - Taps 0 … 63 run in the time domain, sample by sample (the head: no latency).
//  - Four stages of FFT partitions take the rest, each a uniform partitioned convolution (overlap-add, 2B-point complex
//    transforms that carry the left channel as the real part and the right as the imaginary one, split into the two
//    channels' spectra bins 0 … B): B = 64 (taps 64 … 255), 256 (256 … 1023), 1024 (1024 … 4095) and 4096 (4096 … the end).
//    Every stage starts at its own partition size (S = B), so the partition 0 of a block is the one that needs only
//    blocks that are complete: a stage's output block is ready when its input block has ended, whatever the host's block size.
//  - The stages' block grids are offset from each other (0, 128, 512, 2048 samples) so the transforms of the stages never
//    fall on the same audio sample: no block carries the work of all four.
//  - The sums are IEEE basic operations in a fixed order (the head's eight accumulators, the spectra's per-bin sums in
//    partition order), so the same bits on every CPU.
// A convolver is built once for a response (everything it needs is allocated then, off the audio thread: ConvolverPlan) and
// the audio thread only runs it: no allocation. Several are built over a machine's life; the machine crossfades between them.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class StereoConvolver
    {
        public const int Head = 64;
        public static readonly int[] StageSize = { 64, 256, 1024, 4096 };
        public static readonly int[] StagePhase = { 0, 128, 512, 2048 };

        sealed class Stage
        {
            public int B, K, Phase;
            public ComplexFft Fft;
            // The responses' partition spectra (bins 0 … B, scaled by 1/(2B)): [partition].
            public float[][] HlRe, HlIm, HrRe, HrIm;
            // The frequency-domain delay line of the inputs: [slot].
            public float[][] XlRe, XlIm, XrRe, XrIm;
            public float[] Zr, Zi, AlRe, AlIm, ArRe, ArIm;
            // The current output block and the previous one's second half.
            public float[] OutL, OutR, TailL, TailR;
            public int Slot;
        }

        const int Ring = 8192;

        readonly Stage[] stages;
        // The head: the first Head taps of each channel, reversed (so the dot product walks forwards).
        readonly float[] headL = new float[Head], headR = new float[Head];
        // The input history, written twice (at p and p + Ring) so a window is always contiguous.
        readonly float[] histL = new float[2 * Ring], histR = new float[2 * Ring];
        int pos;
        long t;

        /// <summary>The taps this convolver was built for (per channel).</summary>
        public int Length { get; }

        /// <summary>Memory the convolver holds, in bytes (about).</summary>
        public long Bytes { get; }

        /// <summary>
        /// Builds a convolver for the responses (any thread but the audio thread: it allocates and transforms). A null right
        /// response is the left one.
        /// </summary>
        public StereoConvolver(float[] left, float[] right)
        {
            right ??= left;
            int n = Math.Min(left.Length, right.Length);
            Length = n;
            for (int i = 0; i < Head; i++)
            {
                headL[Head - 1 - i] = i < n ? left[i] : 0;
                headR[Head - 1 - i] = i < n ? right[i] : 0;
            }
            var list = new System.Collections.Generic.List<Stage>();
            long bytes = (headL.Length + headR.Length + histL.Length + histR.Length) * 4L;
            int start = Head;
            for (int s = 0; s < StageSize.Length && start < n; s++)
            {
                int b = StageSize[s];
                int remaining = n - start;
                int k = s == StageSize.Length - 1 ? (remaining + b - 1) / b : Math.Min(3, (remaining + b - 1) / b);
                var st = new Stage { B = b, K = k, Phase = StagePhase[s], Fft = new ComplexFft(2 * b) };
                int bins = b + 1;
                st.HlRe = new float[k][]; st.HlIm = new float[k][]; st.HrRe = new float[k][]; st.HrIm = new float[k][];
                st.XlRe = new float[k][]; st.XlIm = new float[k][]; st.XrRe = new float[k][]; st.XrIm = new float[k][];
                st.Zr = new float[2 * b]; st.Zi = new float[2 * b];
                st.AlRe = new float[bins]; st.AlIm = new float[bins]; st.ArRe = new float[bins]; st.ArIm = new float[bins];
                st.OutL = new float[b]; st.OutR = new float[b]; st.TailL = new float[b]; st.TailR = new float[b];
                float scale = 1f / (2 * b);
                for (int j = 0; j < k; j++)
                {
                    st.XlRe[j] = new float[bins]; st.XlIm[j] = new float[bins]; st.XrRe[j] = new float[bins]; st.XrIm[j] = new float[bins];
                    st.HlRe[j] = new float[bins]; st.HlIm[j] = new float[bins]; st.HrRe[j] = new float[bins]; st.HrIm[j] = new float[bins];
                    Spectrum(st, left, right, start + j * b, scale, j);
                }
                bytes += (long)k * bins * 8 * 4 + (long)(2 * b) * 2 * 4;
                list.Add(st);
                start += k * b;
            }
            stages = list.ToArray();
            Bytes = bytes;
        }

        /// <summary>The partition's two channels' spectra (taps from <paramref name="from"/>, B of them), scaled, bins 0 … B.</summary>
        static void Spectrum(Stage st, float[] left, float[] right, int from, float scale, int j)
        {
            int b = st.B, n2 = 2 * b;
            // Both responses are real: left as the real part, right as the imaginary, then split.
            Array.Clear(st.Zr); Array.Clear(st.Zi);
            for (int i = 0; i < b; i++)
            {
                int at = from + i;
                st.Zr[i] = at < left.Length ? left[at] : 0;
                st.Zi[i] = at < right.Length ? right[at] : 0;
            }
            st.Fft.Transform(st.Zr, st.Zi, false);
            for (int k = 0; k <= b; k++)
            {
                int m = (n2 - k) & (n2 - 1);
                float zr = st.Zr[k], zi = st.Zi[k], mr = st.Zr[m], mi = st.Zi[m];
                st.HlRe[j][k] = 0.5f * (zr + mr) * scale;
                st.HlIm[j][k] = 0.5f * (zi - mi) * scale;
                st.HrRe[j][k] = 0.5f * (zi + mi) * scale;
                st.HrIm[j][k] = 0.5f * (mr - zr) * scale;
            }
        }

        public void Reset()
        {
            Array.Clear(histL); Array.Clear(histR);
            pos = 0;
            t = 0;
            foreach (var st in stages)
            {
                for (int j = 0; j < st.K; j++)
                {
                    Array.Clear(st.XlRe[j]); Array.Clear(st.XlIm[j]); Array.Clear(st.XrRe[j]); Array.Clear(st.XrIm[j]);
                }
                Array.Clear(st.OutL); Array.Clear(st.OutR); Array.Clear(st.TailL); Array.Clear(st.TailR);
                st.Slot = 0;
            }
        }

        /// <summary>The convolution of a block, in place (any length; no allocation).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if ((t & (Head - 1)) == 0) Boundaries();
                float xl = l[i], xr = r[i];
                histL[pos] = histL[pos + Ring] = xl;
                histR[pos] = histR[pos + Ring] = xr;
                int w = pos + Ring - (Head - 1);
                float yl = Dot(headL, histL, w), yr = Dot(headR, histR, w);
                foreach (var st in stages)
                {
                    if (t < st.Phase) continue;
                    int idx = (int)((t - st.Phase) % st.B);
                    yl += st.OutL[idx];
                    yr += st.OutR[idx];
                }
                l[i] = yl;
                r[i] = yr;
                pos = (pos + 1) & (Ring - 1);
                t++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        static float Dot(float[] h, float[] x, int w)
        {
            float a0 = 0, a1 = 0, a2 = 0, a3 = 0, a4 = 0, a5 = 0, a6 = 0, a7 = 0;
            for (int j = 0; j < Head; j += 8)
            {
                a0 += h[j] * x[w + j];
                a1 += h[j + 1] * x[w + j + 1];
                a2 += h[j + 2] * x[w + j + 2];
                a3 += h[j + 3] * x[w + j + 3];
                a4 += h[j + 4] * x[w + j + 4];
                a5 += h[j + 5] * x[w + j + 5];
                a6 += h[j + 6] * x[w + j + 6];
                a7 += h[j + 7] * x[w + j + 7];
            }
            return ((a0 + a4) + (a1 + a5)) + ((a2 + a6) + (a3 + a7));
        }

        /// <summary>At a block boundary of the head's grid: every stage whose own block ends now runs its transforms.</summary>
        void Boundaries()
        {
            foreach (var st in stages)
                if (t >= st.Phase && (t - st.Phase) % st.B == 0) Block(st);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Block(Stage st)
        {
            int b = st.B, n2 = 2 * b, k = st.K;
            // The input block that has just ended: samples t - B … t - 1.
            int from = (int)(t - b);
            for (int i = 0; i < b; i++)
            {
                int at = (from + i) & (Ring - 1);
                st.Zr[i] = histL[at];
                st.Zi[i] = histR[at];
            }
            // Before time zero the history holds zeros (the transform of a block that is not there yet).
            for (int i = b; i < n2; i++) { st.Zr[i] = 0; st.Zi[i] = 0; }
            st.Fft.Transform(st.Zr, st.Zi, false);
            int slot = st.Slot;
            var xlRe = st.XlRe[slot]; var xlIm = st.XlIm[slot]; var xrRe = st.XrRe[slot]; var xrIm = st.XrIm[slot];
            for (int q = 0; q <= b; q++)
            {
                int m = (n2 - q) & (n2 - 1);
                float zr = st.Zr[q], zi = st.Zi[q], mr = st.Zr[m], mi = st.Zi[m];
                xlRe[q] = 0.5f * (zr + mr);
                xlIm[q] = 0.5f * (zi - mi);
                xrRe[q] = 0.5f * (zi + mi);
                xrIm[q] = 0.5f * (mr - zr);
            }
            Array.Clear(st.AlRe); Array.Clear(st.AlIm); Array.Clear(st.ArRe); Array.Clear(st.ArIm);
            int bins = b + 1;
            for (int j = 0; j < k; j++)
            {
                int s = slot - j;
                if (s < 0) s += k;
                ComplexFft.MultiplyAdd(st.AlRe, st.AlIm, st.XlRe[s], st.XlIm[s], st.HlRe[j], st.HlIm[j], bins);
                ComplexFft.MultiplyAdd(st.ArRe, st.ArIm, st.XrRe[s], st.XrIm[s], st.HrRe[j], st.HrIm[j], bins);
            }
            // One inverse transform for both channels: W = A_L + i·A_R, with each channel's upper bins the conjugates of the lower.
            for (int q = 0; q <= b; q++)
            {
                st.Zr[q] = st.AlRe[q] - st.ArIm[q];
                st.Zi[q] = st.AlIm[q] + st.ArRe[q];
            }
            for (int q = b + 1; q < n2; q++)
            {
                int m = n2 - q;
                st.Zr[q] = st.AlRe[m] + st.ArIm[m];
                st.Zi[q] = st.ArRe[m] - st.AlIm[m];
            }
            st.Fft.Transform(st.Zr, st.Zi, true);
            for (int i = 0; i < b; i++)
            {
                st.OutL[i] = st.Zr[i] + st.TailL[i];
                st.OutR[i] = st.Zi[i] + st.TailR[i];
                st.TailL[i] = st.Zr[b + i];
                st.TailR[i] = st.Zi[b + i];
            }
            st.Slot = slot + 1 == k ? 0 : slot + 1;
        }
    }
}
