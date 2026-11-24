#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW4 as built): everything around the pretrained beat and downbeat network that is not the network:
// the front end exactly as the reference defines it, the way a long piece is cut into 30 s windows and put back together, and the peak picking.
// Reference: Foscarin, Schlueter, Widmer, "Beat This! Accurate Beat Tracking Without DBN Postprocessing", ISMIR 2024 (github.com/CPJKU/beat_this, MIT):
//   mono at 22.05 kHz; a 1024-sample periodic Hann window, hop 441 (50 frames a second, frame i centred on sample 441 i, the signal reflected at both ends);
//   the magnitude divided by sqrt(1024), the frame length; 128 triangles on the Slaney mel scale from 30 Hz to 11 kHz (no area normalisation); ln(1 + 1000 x).
//   Windows of 1500 frames with a border of 6 frames discarded on each side (the model was not trained on the edges), the last window moved back to end
//   at the end of the piece, the earlier window winning where two overlap; the peaks of each logit within +-3 frames that are above 0, neighbouring
//   peaks (at most 1 frame apart) averaged, and each downbeat moved to the nearest beat.
// Written from that description; no code of the reference is used. Pure (ReBuzz.SessionTests links this file; the network itself is BeatThisModel.cs).

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReBuzz.Core.Clips
{
    internal static class BeatThisFront
    {
        public const int Rate = 22050, FftSize = 1024, Hop = 441, Mels = 128;
        public const double Fps = 50.0;
        const double FMin = 30, FMax = 11000;
        public const int Chunk = 1500, Border = 6;

        static float[][] filterWeights;
        static int[] filterStart;
        static double[] window;
        static double windowNorm;
        static readonly object gate = new object();

        // The Slaney mel scale: linear below 1 kHz (200/3 Hz a mel), logarithmic above (a step of ln(6.4)/27 a mel).
        const double MinLogHz = 1000.0, FSp = 200.0 / 3.0;
        static readonly double MinLogMel = MinLogHz / FSp, LogStep = Math.Log(6.4) / 27.0;
        static double Mel(double hz) => hz >= MinLogHz ? MinLogMel + Math.Log(hz / MinLogHz) / LogStep : hz / FSp;
        static double Hz(double mel) => mel >= MinLogMel ? MinLogHz * Math.Exp(LogStep * (mel - MinLogMel)) : mel * FSp;

        static void Prepare()
        {
            lock (gate)
            {
                if (filterWeights != null) return;
                int bins = FftSize / 2 + 1;
                // The filterbank as the reference's library makes it: in float32, step by step (its mel points, their Hz, the slopes), so that its rounding is ours (it is up to 1e-4 of
                // the log-mel in the quietest bins, and the spectrogram is asked to be within 1e-4 of it).
                float[] Lin(float start, float end, int steps)
                {
                    var v = new float[steps];
                    float step = (end - start) / (steps - 1);                       // the step in float32, each point from the nearer end, summed wide and rounded once
                    for (int i = 0; i < steps; i++) v[i] = (float)(i < steps / 2 ? (double)start + (double)step * i : (double)end - (double)step * (steps - i - 1));
                    return v;
                }
                float[] all = Lin(0f, Rate / 2, bins);
                float[] mp = Lin((float)Mel(FMin), (float)Mel(FMax), Mels + 2);
                float fsp = (float)FSp, minLogHz = (float)MinLogHz, minLogMel = (float)MinLogMel, logStep = (float)LogStep;
                var f = new float[Mels + 2];
                for (int i = 0; i < f.Length; i++)
                    f[i] = mp[i] >= minLogMel ? minLogHz * MathF.Exp(logStep * (mp[i] - minLogMel)) : fsp * mp[i];
                var w = new float[Mels][];
                var st = new int[Mels];
                for (int b = 0; b < Mels; b++)
                {
                    int a = -1, z = -1;
                    var row = new float[bins];
                    float dDown = f[b + 1] - f[b], dUp = f[b + 2] - f[b + 1];
                    for (int k = 0; k < bins; k++)
                    {
                        float down = (-1.0f * (f[b] - all[k])) / dDown;              // rising slope
                        float up = (f[b + 2] - all[k]) / dUp;                        // falling slope
                        float v = MathF.Max(0f, MathF.Min(down, up));
                        row[k] = v;
                        if (v > 0) { if (a < 0) a = k; z = k; }
                    }
                    if (a < 0) { a = 0; z = 0; }
                    var r = new float[z - a + 1];
                    Array.Copy(row, a, r, 0, z - a + 1);
                    w[b] = r; st[b] = a;
                }
                window = new double[FftSize];
                for (int i = 0; i < FftSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize);
                windowNorm = 1.0 / Math.Sqrt(FftSize);                         // torchaudio's normalized = "frame_length"; "window" would be the window's L2 norm
                filterStart = st; filterWeights = w;
            }
        }

        /// <summary>The log-mel spectrogram of mono audio already at 22.05 kHz: frames x 128, frame-major, as the reference makes it (frames = samples / 441 + 1).</summary>
        public static float[] Compute(float[] x, out int frames)
        {
            Prepare();
            int n = x.Length;
            frames = n / Hop + 1;
            int nf = frames;
            var q = new float[(long)nf * Mels];
            int pairs = (nf + 1) / 2;
            const int chunk = 64;
            Parallel.For(0, (pairs + chunk - 1) / chunk, () => (new RadixFft(FftSize), new double[FftSize], new double[FftSize], new double[FftSize / 2 + 1], new double[FftSize / 2 + 1]), (c, st, local) =>
            {
                var (fft, re, im, pa, pb) = local;
                for (int pr = c * chunk; pr < Math.Min(pairs, (c + 1) * chunk); pr++)
                {
                    int fa = 2 * pr, fb = 2 * pr + 1;
                    bool two = fb < nf;
                    for (int i = 0; i < FftSize; i++)
                    {
                        re[i] = window[i] * x[Reflect((long)fa * Hop - FftSize / 2 + i, n)];
                        im[i] = two ? window[i] * x[Reflect((long)fb * Hop - FftSize / 2 + i, n)] : 0;
                    }
                    fft.Forward(re, im);
                    for (int k = 0; k <= FftSize / 2; k++)
                    {
                        int k2 = (FftSize - k) & (FftSize - 1);
                        double zr = re[k], zi = im[k], cr = re[k2], ci = -im[k2];
                        double sr = zr + cr, si = zi + ci, dr = zr - cr, di = zi - ci;
                        pa[k] = 0.5 * Math.Sqrt(sr * sr + si * si) * windowNorm;
                        pb[k] = 0.5 * Math.Sqrt(dr * dr + di * di) * windowNorm;
                    }
                    Bands(q, fa, pa);
                    if (two) Bands(q, fb, pb);
                }
                return local;
            }, _ => { });
            return q;
        }

        /// <summary>Reflect padding (no repeat of the edge sample), as torch does it; a signal shorter than the pad folds back and forth.</summary>
        static int Reflect(long i, int n)
        {
            if (n <= 1) return 0;
            long period = 2L * (n - 1);
            i %= period;
            if (i < 0) i += period;
            return (int)(i < n ? i : period - i);
        }

        static void Bands(float[] q, int frame, double[] mag)
        {
            long row = (long)frame * Mels;
            for (int b = 0; b < Mels; b++)
            {
                var w = filterWeights[b];
                int a = filterStart[b];
                double e = 0;
                for (int k = 0; k < w.Length; k++) e += w[k] * mag[a + k];
                q[row + b] = (float)Math.Log(1 + 1000 * e);
            }
        }

        /// <summary>The windows of a piece of <paramref name="frames"/> frames: the first frame of each (negative at the start: zeros are put before it). The reference's split_piece.</summary>
        public static int[] WindowStarts(int frames)
        {
            int step = Chunk - 2 * Border;
            var starts = new List<int>();
            for (int s = -Border; s < frames - Border; s += step) starts.Add(s);
            if (starts.Count == 0) starts.Add(-Border);
            if (frames > step) starts[starts.Count - 1] = frames - (Chunk - Border);          // the last window ends at the end of the piece
            return starts.ToArray();
        }

        /// <summary>The input of one window: its frames with zero frames where it reaches past the start (all of the border) or the end (at most the border).</summary>
        public static int FillWindow(float[] spect, int frames, int start, float[] into)
        {
            int a = Math.Max(start, 0), b = Math.Min(start + Chunk, frames);
            int left = Math.Max(0, -start), right = Math.Max(0, Math.Min(Border, start + Chunk - frames));
            int len = left + (b - a) + right;
            Array.Clear(into, 0, len * Mels);
            Array.Copy(spect, (long)a * Mels, into, (long)left * Mels, (long)(b - a) * Mels);
            return len;
        }

        /// <summary>Puts the windows' answers together (the reference's aggregate_prediction with keep_first): each window gives its frames from Border to the end less Border, an earlier window wins.</summary>
        public static float[] Aggregate(int frames, int[] starts, float[][] perWindow)
        {
            var full = new float[frames];
            for (int i = 0; i < frames; i++) full[i] = -1000f;
            for (int w = starts.Length - 1; w >= 0; w--)
            {
                var p = perWindow[w];
                int count = p.Length - 2 * Border;
                for (int j = 0; j < count; j++)
                {
                    int at = starts[w] + Border + j;
                    if (at >= 0 && at < frames) full[at] = p[Border + j];
                }
            }
            return full;
        }

        /// <summary>The beats and downbeats (frame positions, fractional where neighbouring peaks were averaged) of the logits: the reference's minimal post-processor.</summary>
        public static void PickPeaks(float[] beat, float[] down, out double[] beats, out double[] downbeats)
        {
            beats = Dedupe(Peaks(beat));
            var d = Dedupe(Peaks(down));
            var kept = new List<double>();
            if (beats.Length > 0)
            {
                // Each downbeat goes to the nearest beat, measured in seconds as the reference does (so that ties fall as they fall there), the first of equals.
                var bt = new double[beats.Length];
                for (int j = 0; j < bt.Length; j++) bt[j] = beats[j] / Fps;
                int last = -1;
                foreach (double frame in d)
                {
                    double t = frame / Fps;
                    int best = 0;
                    double bd = Math.Abs(bt[0] - t);
                    for (int j = 1; j < bt.Length; j++)
                    {
                        double dd = Math.Abs(bt[j] - t);
                        if (dd < bd) { bd = dd; best = j; }
                        else if (bt[j] > t) break;
                    }
                    if (best != last) { kept.Add(beats[best]); last = best; }
                }
            }
            else kept.AddRange(d);
            downbeats = kept.ToArray();
        }

        /// <summary>
        /// AW4b: the beats' positions to a fraction of a frame (20 ms): each position (a frame or the mean of neighbours) is moved to the top of the parabola through the logit at its
        /// nearest frame and the two beside it, by at most half a frame. The picked frames themselves (and so the parity with the reference) are not changed.
        /// </summary>
        public static double[] Refine(float[] logit, double[] frames)
        {
            var r = new double[frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                int c = (int)Math.Round(frames[i]);
                // the largest of the logit within a frame of the position: the parabola is taken around the top
                int top = Math.Max(0, Math.Min(logit.Length - 1, c));
                for (int u = Math.Max(0, c - 1); u <= Math.Min(logit.Length - 1, c + 1); u++) if (logit[u] > logit[top]) top = u;
                double d = 0;
                if (top > 0 && top < logit.Length - 1)
                {
                    double a = logit[top - 1], b = logit[top], g = logit[top + 1];
                    double den = a - 2 * b + g;
                    if (den < -1e-9) d = Math.Max(-0.5, Math.Min(0.5, 0.5 * (a - g) / den));
                }
                r[i] = frames[i] == c ? top + d : frames[i];           // a position that is the mean of neighbouring peaks (a plateau) stays
            }
            return r;
        }

        /// <summary>The frames whose logit is the largest within 3 frames either side and above 0 (a probability over one half).</summary>
        static List<int> Peaks(float[] x)
        {
            var list = new List<int>();
            for (int i = 0; i < x.Length; i++)
            {
                if (!(x[i] > 0)) continue;
                bool top = true;
                for (int j = Math.Max(0, i - 3); j <= Math.Min(x.Length - 1, i + 3) && top; j++) if (x[j] > x[i]) top = false;
                if (top) list.Add(i);
            }
            return list;
        }

        static double[] Dedupe(List<int> peaks)
        {
            var result = new List<double>();
            if (peaks.Count == 0) return result.ToArray();
            double p = peaks[0];
            int c = 1;
            for (int i = 1; i < peaks.Count; i++)
            {
                int p2 = peaks[i];
                if (p2 - p <= 1) { c++; p += (p2 - p) / c; }
                else { result.Add(p); p = p2; c = 1; }
            }
            result.Add(p);
            return result.ToArray();
        }
    }
}
