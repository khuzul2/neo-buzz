#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.13, P5b): what the Spectral engine's analysis gives the other two analytic engines.
//  * ImportedPartials: a frame or a range of a SpectralModel turned into the Harmonic engine's partial table (a level and a spacing for each row).
//  * ImportedModes: a short stretch of a SpectralModel (a sample read as an impulse response) turned into the Modal engine's modes
//    (frequency, decay and gain each).
// Both are worked out from the model and a few parameters by code with basic operations and the portable maths only (the same bits on every
// platform) and into arrays made once, so the audio thread can make them when a control moves without allocating. The model keeps no phases, so
// the tables have none either: a voice starts its partials and modes where the engine's own phase and random controls put them.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    /// <summary>A slot's imported partial table: rows sorted by their place in the spectrum, with the level and the pitch (a multiple of the reference pitch) of each.</summary>
    internal sealed class ImportedPartials
    {
        public const int Max = SpectralModel.MaxPartials;
        /// <summary>Frames read from a range (evenly spread; a single frame when the range is empty).</summary>
        public const int MaxFrames = 12;

        public readonly float[] Level = new float[HarmonicSets.Partials], Ratio = new float[HarmonicSets.Partials];
        /// <summary>Rows that sound (the rest are silent).</summary>
        public int Count;
        /// <summary>The reference pitch (Hz): the lowest strong partial of the loudest frame read. Row ratios are against it, so that it sounds the note played.</summary>
        public float Reference;

        // Scratch: candidates (a partial's mean power and frequency; one per partial per frame at most), and the slot-wise running sums.
        readonly float[] candP = new float[MaxFrames * Max + Max], candF = new float[MaxFrames * Max + Max];
        readonly float[] runP = new float[Max], runF = new float[Max];
        readonly int[] frames = new int[MaxFrames];
        int ncand;

        /// <summary>
        /// Fills the table from <paramref name="m"/>: <paramref name="from"/> (0 … 1) is where in the sample the frames start, <paramref name="span"/> (0 … 1)
        /// how much of it is read after that (0: one frame). A partial that sounds in only some of the frames is weighted by its share of them.
        /// </summary>
        public void Make(SpectralModel m, float from, float span)
        {
            Count = 0;
            Reference = 0f;
            Array.Clear(Level, 0, Level.Length);
            for (int j = 0; j < Ratio.Length; j++) Ratio[j] = j + 1;
            if (m == null || m.Frames <= 0) return;
            int last = m.Frames - 1;
            from = Math.Clamp(from, 0f, 1f);
            span = Math.Clamp(span, 0f, 1f);
            int f0 = (int)(from * last + 0.5f), f1 = (int)(Math.Min(1f, from + span) * last + 0.5f);
            int n = 1;
            frames[0] = f0;
            if (f1 > f0)
            {
                n = Math.Min(MaxFrames, f1 - f0 + 1);
                for (int i = 0; i < n; i++) frames[i] = n == 1 ? f0 : f0 + (int)((long)(f1 - f0) * i / (n - 1));
            }
            // The reference pitch: the lowest strong partial (within 14 dB of the strongest) of the loudest frame read. (The fundamental the analysis finds is not used: a
            // bar or a bell has none, and the search then returns a pitch far under the lowest partial. A tone whose fundamental is missing plays its first present partial.)
            double reference;
            {
                float best = 0f;
                int bf = -1;
                for (int i = 0; i < n; i++)
                {
                    float e = 0f;
                    for (int p = m.Start[frames[i]]; p < m.Start[frames[i] + 1]; p++) e += m.Amp[p] * m.Amp[p];
                    if (e > best) { best = e; bf = frames[i]; }
                }
                if (bf < 0) return;
                float peak = 0f;
                for (int p = m.Start[bf]; p < m.Start[bf + 1]; p++) peak = Math.Max(peak, m.Amp[p]);
                float low = float.MaxValue;
                for (int p = m.Start[bf]; p < m.Start[bf + 1]; p++)
                    if (m.Amp[p] >= 0.2f * peak && m.Freq[p] < low) low = m.Freq[p];
                if (low == float.MaxValue) return;
                reference = low;
            }
            Reference = (float)reference;
            // Candidates: each partial slot's run of near-equal frequencies (a slot is reused by another partial after a death) over the frames read, its mean power.
            ncand = 0;
            Array.Clear(runP, 0, Max);
            Array.Clear(runF, 0, Max);
            for (int i = 0; i < n; i++)
            {
                int f = frames[i];
                for (int p = m.Start[f]; p < m.Start[f + 1]; p++)
                {
                    int s = m.Slot[p];
                    float a2 = m.Amp[p] * m.Amp[p], hz = m.Freq[p];
                    if (a2 <= 0f || hz <= 0f) continue;
                    if (runP[s] > 0f && Math.Abs(runF[s] / runP[s] - hz) > 0.03f * hz) Flush(s, n);
                    runP[s] += a2;
                    runF[s] += a2 * hz;
                }
            }
            for (int s = 0; s < Max; s++) Flush(s, n);
            // The strongest rows, then in order of their place.
            int rows = Math.Min(Max, ncand);
            for (int r = 0; r < rows; r++)
            {
                int bi = r;
                for (int c = r + 1; c < ncand; c++) if (candP[c] > candP[bi]) bi = c;
                (candP[r], candP[bi]) = (candP[bi], candP[r]);
                (candF[r], candF[bi]) = (candF[bi], candF[r]);
            }
            for (int r = 1; r < rows; r++)
            {
                float p = candP[r], fq = candF[r];
                int c = r - 1;
                while (c >= 0 && candF[c] > fq) { candP[c + 1] = candP[c]; candF[c + 1] = candF[c]; c--; }
                candP[c + 1] = p; candF[c + 1] = fq;
            }
            for (int r = 0; r < rows; r++)
            {
                Level[r] = (float)Math.Sqrt(candP[r]);
                Ratio[r] = candF[r] / Reference;
            }
            Count = rows;
        }

        void Flush(int s, int n)
        {
            if (runP[s] > 0f)
            {
                candP[ncand] = runP[s] / n;
                candF[ncand] = runF[s] / runP[s];
                ncand++;
            }
            runP[s] = runF[s] = 0f;
        }
    }

    /// <summary>
    /// A slot's imported modes: a sample's analysed partials read as the ringing of a body struck once. Each mode has a frequency, a decay (its
    /// rate of fall, per second, of the amplitude: 6.908 / T60) and a gain (the amplitude it started at, at the start of the stretch). Sorted by
    /// frequency; <see cref="Rank"/> is each mode's place by gain (0 the strongest), so a smaller count keeps the strongest.
    /// </summary>
    internal sealed class ImportedModes
    {
        public const int Max = 64;
        /// <summary>A mode must be tracked over this many frames, so that its decay is a fit and not a guess.</summary>
        public const int MinFrames = 3;
        /// <summary>The slowest and the fastest decay kept: T60 of 20 s and 50 ms.</summary>
        public const double SigmaMin = 6.908 / 20.0, SigmaMax = 6.908 / 0.05;

        public readonly float[] Ratio = new float[Max], Sigma = new float[Max], Gain = new float[Max], Hz = new float[Max];
        public readonly int[] Rank = new int[Max];
        public int Count;
        /// <summary>The reference pitch (Hz): the lowest mode within 20 dB of the strongest. Ratios are against it, so that it sounds the note played.</summary>
        public float Reference;

        // Per model slot: the running run (frames, sums for the line fit of ln amplitude on time, the frequency).
        readonly int[] runN = new int[SpectralModel.MaxPartials], runLast = new int[SpectralModel.MaxPartials];
        readonly double[] runT = new double[SpectralModel.MaxPartials], runT2 = new double[SpectralModel.MaxPartials], runY = new double[SpectralModel.MaxPartials],
            runTY = new double[SpectralModel.MaxPartials], runWF = new double[SpectralModel.MaxPartials], runW = new double[SpectralModel.MaxPartials], runF = new double[SpectralModel.MaxPartials];
        readonly float[] cHz = new float[SpectralModel.MaxPartials * 40], cSig = new float[SpectralModel.MaxPartials * 40], cGain = new float[SpectralModel.MaxPartials * 40];
        int ncand;

        /// <summary>
        /// Fills the table from <paramref name="m"/>: the stretch of the sample that starts <paramref name="start"/> (0 … 1) of the way in and lasts
        /// <paramref name="seconds"/>. A partial track that lasts at least <see cref="MinFrames"/> frames is a mode: its frequency is the power-weighted
        /// mean, its decay the slope of a line through ln amplitude against time, its gain that line's amplitude at the start of the stretch.
        /// </summary>
        public void Make(SpectralModel m, float start, float seconds)
        {
            Count = 0;
            Reference = 0f;
            if (m == null || m.Frames <= 0) return;
            double fs = m.FrameSeconds;
            int f0 = (int)(Math.Clamp(start, 0f, 1f) * (m.Frames - 1) + 0.5f);
            // A frame centred nearer the sample's start than half its window has part of its window outside the sample (it reads low): the fit starts after those.
            int fa = Math.Max(f0, (m.Size / 2 + m.Hop - 1) / m.Hop);
            int f1 = Math.Min(m.Frames - 1, Math.Max(f0 + Math.Max(MinFrames, (int)(seconds / fs + 0.5)) - 1, fa + MinFrames - 1));
            double t0 = f0 * fs;
            ncand = 0;
            Array.Clear(runN, 0, runN.Length);
            Array.Clear(runLast, 0, runLast.Length);
            Array.Clear(runT, 0, runT.Length); Array.Clear(runT2, 0, runT2.Length); Array.Clear(runY, 0, runY.Length); Array.Clear(runTY, 0, runTY.Length);
            Array.Clear(runWF, 0, runWF.Length); Array.Clear(runW, 0, runW.Length); Array.Clear(runF, 0, runF.Length);
            for (int f = fa; f <= f1; f++)
            {
                for (int p = m.Start[f]; p < m.Start[f + 1]; p++)
                {
                    int s = m.Slot[p];
                    float amp = m.Amp[p], hz = m.Freq[p];
                    if (amp <= 0f || hz <= 0f) continue;
                    // A track continues only from the frame before and within 1.5 % of where it was.
                    if (runN[s] > 0 && (runLast[s] != f - 1 || Math.Abs(runWF[s] / runW[s] - hz) > 0.015f * hz)) Close(s);
                    double t = f * fs - t0, y = m.LnA[p], w = (double)amp * amp;
                    runN[s]++;
                    runLast[s] = f;
                    runT[s] += t; runT2[s] += t * t; runY[s] += y; runTY[s] += t * y;
                    runWF[s] += w * hz; runW[s] += w;
                }
                // Tracks that stopped.
                for (int s = 0; s < runN.Length; s++) if (runN[s] > 0 && runLast[s] < f) Close(s);
            }
            for (int s = 0; s < runN.Length; s++) Close(s);
            if (ncand == 0) return;
            // The strongest Max by gain, ranked, then in order of frequency.
            int rows = Math.Min(Max, ncand);
            for (int r = 0; r < rows; r++)
            {
                int bi = r;
                for (int c = r + 1; c < ncand; c++) if (cGain[c] > cGain[bi]) bi = c;
                Swap(r, bi);
            }
            float top = cGain[0];
            // Reference: the lowest mode within 20 dB of the strongest.
            float low = float.MaxValue;
            for (int r = 0; r < rows; r++) if (cGain[r] >= 0.1f * top && cHz[r] < low) low = cHz[r];
            Reference = low;
            for (int r = 0; r < rows; r++) Rank[r] = r;
            // Order by frequency, keeping each mode's rank with it.
            for (int r = 0; r < rows; r++) { Hz[r] = cHz[r]; Sigma[r] = cSig[r]; Gain[r] = cGain[r]; }
            for (int r = 1; r < rows; r++)
            {
                float hz = Hz[r], sg = Sigma[r], g = Gain[r];
                int rk = Rank[r], c = r - 1;
                while (c >= 0 && Hz[c] > hz) { Hz[c + 1] = Hz[c]; Sigma[c + 1] = Sigma[c]; Gain[c + 1] = Gain[c]; Rank[c + 1] = Rank[c]; c--; }
                Hz[c + 1] = hz; Sigma[c + 1] = sg; Gain[c + 1] = g; Rank[c + 1] = rk;
            }
            for (int r = 0; r < rows; r++) Ratio[r] = Hz[r] / Reference;
            Count = rows;
        }

        void Swap(int a, int b)
        {
            if (a == b) return;
            (cHz[a], cHz[b]) = (cHz[b], cHz[a]);
            (cSig[a], cSig[b]) = (cSig[b], cSig[a]);
            (cGain[a], cGain[b]) = (cGain[b], cGain[a]);
        }

        void Close(int s)
        {
            int n = runN[s];
            if (n >= MinFrames && ncand < cHz.Length)
            {
                double det = n * runT2[s] - runT[s] * runT[s];
                double slope = det > 1e-12 ? (n * runTY[s] - runT[s] * runY[s]) / det : 0.0;
                double icpt = (runY[s] - slope * runT[s]) / n;
                double sigma = Math.Clamp(-slope, SigmaMin, SigmaMax);
                cHz[ncand] = (float)(runWF[s] / runW[s]);
                cSig[ncand] = (float)sigma;
                cGain[ncand] = (float)PortableMath.Exp(icpt);
                ncand++;
            }
            runN[s] = 0; runT[s] = runT2[s] = runY[s] = runTY[s] = runWF[s] = runW[s] = 0.0;
        }
    }
}
