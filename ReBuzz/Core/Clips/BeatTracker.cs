#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW1): finds the beats of a piece of audio, following a tempo that changes, and the
// downbeat among them. Pure (ReBuzz.SessionTests links this file); run when a clip is made or auto-warped, never on the audio thread.
//
// Clean room, from the literature only:
//  - the onset strength: the positive change of a log-compressed band spectrum, with a max filter over the neighbouring bands of the
//    frame before it (the vibrato-proof flux of Boeck and Widmer, 2013), on the audio at a lower rate;
//  - the tempo: the autocorrelation of the onset strength in a sliding window, a comb over the beat and its multiples (Klapuri, Eronen,
//    Astola 2006; Ellis 2007), a Viterbi path over the windows that lets the tempo drift smoothly and jump at a cost (as the tempo
//    states of Krebs, Boeck and Widmer 2015, here with the beats separate);
//  - the beats: dynamic programming over the onset strength with a penalty on the log ratio of each interval to the local period (Ellis,
//    "Beat tracking by dynamic programming", 2007), run twice, the second time on the period read from the first pass's beats;
//  - the downbeat: the beat of each bar that is accented most in low-frequency onsets, in broadband onsets and in the change of the
//    beat-synchronous spectrum (Goto and Muraoka 1994; Davies and Plumbley 2006), taken as one phase of the beats per bar over the piece.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ReBuzz.Core.Clips
{
    /// <summary>The beats of an audio and what is known about each (what the downbeat is chosen from); made once per source.</summary>
    internal sealed class BeatTrack
    {
        public int Rate;
        public int Length;
        /// <summary>Sample positions of the beats, ascending (fractional).</summary>
        public double[] Beats = Array.Empty<double>();
        /// <summary>Per beat: low-frequency onset strength, broadband onset strength, and the spectrum's change into the beat (not yet normalised).</summary>
        public double[] Low = Array.Empty<double>(), Full = Array.Empty<double>(), Change = Array.Empty<double>();
        /// <summary>The tempo over the whole piece: the median beat interval, in BPM (0 when there are no beats).</summary>
        public double MedianBpm;
        public bool Found => Beats.Length >= 4;
        /// <summary>Whether the audio is short enough to be one clip's worth of loop (kept for callers that ask).</summary>
        public double Seconds => Length / (double)Math.Max(1, Rate);
    }

    /// <summary>
    /// A complex FFT of one power-of-two size for the onset strength's frames (two real frames at a time): contiguous twiddles per stage, the first
    /// stage with no multiplication, fully optimised from the first call (the tests run unoptimised code, and a long file is 30 000 of these).
    /// </summary>
    internal sealed class RadixFft
    {
        readonly int size;
        readonly int[] reverse;
        readonly double[] wr, wi;                    // stage with half-length h: its h twiddles at [h, 2h)

        public RadixFft(int size)
        {
            this.size = size;
            int bits = 0;
            while ((1 << bits) < size) bits++;
            reverse = new int[size];
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
                reverse[i] = r;
            }
            wr = new double[size];
            wi = new double[size];
            for (int h = 1; h < size; h <<= 1)
                for (int k = 0; k < h; k++)
                {
                    wr[h + k] = Math.Cos(-Math.PI * k / h);
                    wi[h + k] = Math.Sin(-Math.PI * k / h);
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Forward(double[] re, double[] im)
        {
            int n = size;
            var rev = reverse;
            for (int i = 0; i < n; i++)
            {
                int j = rev[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (int a = 0; a < n; a += 2)
            {
                double tr = re[a + 1], ti = im[a + 1];
                re[a + 1] = re[a] - tr; im[a + 1] = im[a] - ti;
                re[a] += tr; im[a] += ti;
            }
            for (int h = 2; h < n; h <<= 1)
            {
                for (int start = 0; start < n; start += 2 * h)
                {
                    for (int k = 0; k < h; k++)
                    {
                        double cr = wr[h + k], ci = wi[h + k];
                        int a = start + k, b = a + h;
                        double tr = re[b] * cr - im[b] * ci;
                        double ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
                }
            }
        }
    }

    internal static class BeatTracker
    {
        // ---- The front end -------------------------------------------------------------------
        const int Window = 512;                      // samples of the decimated audio: about 46 ms, so a low note's period is inside it (bass beats in a shorter one)
        const double HopSeconds = 0.0116;            // 11.6 ms between frames, as Ellis's
        const double TargetRate = 11025;             // the audio is decimated to about this
        const double MaxFreq = 8000;

        // ---- The tempo -----------------------------------------------------------------------
        const double MinBpm = 70, MaxBpm = 180, BpmRatio = 1.005;
        const double TempoWindowSeconds = 5.8;
        const double TempoStepSeconds = 1.0;
        const double PriorOctaves = 1.2;             // the width of the preference for 120 BPM, in octaves (one standard deviation)
        const double JumpPenalty = 8.0;              // the most a change of tempo between two windows costs
        const double DriftPenalty = 1500;            // the cost of a change per square of its log ratio, below that

        // ---- The beats -----------------------------------------------------------------------
        const double Tightness1 = 40, Tightness2 = 70;
        /// <summary>Onset strength below this (the sum of the rises of the log bands, with the audio at -20 dB RMS) is rounding: a steady tone has none.</summary>
        const double AbsoluteFloor = 0.5;
        /// <summary>The comb over the autocorrelation: the beat, two beats and a bar of four (not three: a pattern in dotted eighths repeats every three beats).</summary>
        static readonly int[] Harmonics = { 1, 2, 4 };
        static readonly double[] HarmonicWeights = { 1.0, 0.6, 0.5 };

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AudioSource, Lazy<BeatTrack>> cache = new();

        /// <summary>The beats of the audio, once per source (also for a streamed one: it reads the file's exact samples). Never on the audio thread.</summary>
        public static BeatTrack Of(AudioSource source)
        {
            var lazy = cache.GetValue(source, s => new Lazy<BeatTrack>(() => Track(s.ForAnalysis()), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }

        sealed class Frames
        {
            public int Count, Bands, Decimation, Hop;
            public double Fs, FrameRate;
            public double[] Flux, LowFlux;
            public float[] Log;                      // Count x Bands
        }

        /// <summary>How many times the tracker has run (the tests check that opening a song does not run it).</summary>
        internal static int Runs;

        public static BeatTrack Track(AudioSource src)
        {
            System.Threading.Interlocked.Increment(ref Runs);
            var track = new BeatTrack { Rate = src.SampleRate, Length = src.Length };
            var fr = Analyse(src);
            if (fr.Count < 64) return track;                           // under about 0.8 s
            var env = Standardise(fr.Flux, fr.FrameRate);
            var low = Standardise(fr.LowFlux, fr.FrameRate);
            var period1 = TempoCurve(env, fr.FrameRate);
            if (period1 == null) return track;

            int[] frames = Dp(env, period1, Tightness1);
            double[] pos = Refine(src, fr, frames, out _);
            double[] period2 = PeriodFromBeats(frames, pos, fr, period1);
            frames = Dp(env, period2, Tightness2);
            pos = Refine(src, fr, frames, out double[] attack);
            pos = Smooth(pos, attack);
            // Beats in the silence before the music and after it are no beats.
            int first = 0, last = frames.Length - 1;
            while (first <= last && !Active(env, frames[first])) first++;
            while (last >= first && !Active(env, frames[last])) last--;
            if (last - first + 1 < 4) return track;
            int n = last - first + 1;
            var beats = new double[n];
            var fi = new int[n];
            for (int i = 0; i < n; i++) { beats[i] = pos[first + i]; fi[i] = frames[first + i]; }
            track.Beats = beats;
            track.Low = new double[n];
            track.Full = new double[n];
            track.Change = new double[n];
            for (int i = 0; i < n; i++)
            {
                int a = Math.Max(0, fi[i] - 2), b = Math.Min(fr.Count - 1, fi[i] + 1);
                double lo = 0, fu = 0;
                for (int f = a; f <= b; f++) { lo = Math.Max(lo, low[f]); fu = Math.Max(fu, env[f]); }
                track.Low[i] = lo;
                track.Full[i] = fu;
            }
            // The beat-synchronous spectrum: the mean log spectrum from this beat to the next; its change from the beat before.
            int B = fr.Bands;
            var prev = new double[B];
            var cur = new double[B];
            for (int i = 0; i < n; i++)
            {
                int a = Math.Min(fr.Count - 1, fi[i] + 1);
                int b = i + 1 < n ? Math.Max(a, fi[i + 1]) : Math.Min(fr.Count - 1, a + 8);
                Array.Clear(cur);
                for (int f = a; f <= b; f++)
                    for (int k = 0; k < B; k++) cur[k] += fr.Log[f * B + k];
                double d = 0;
                for (int k = 0; k < B; k++)
                {
                    cur[k] /= (b - a + 1);
                    d += Math.Abs(cur[k] - prev[k]);
                }
                track.Change[i] = i == 0 ? 0 : d;
                Array.Copy(cur, prev, B);
            }
            var intervals = new List<double>(n);
            for (int i = 1; i < n; i++) intervals.Add(beats[i] - beats[i - 1]);
            intervals.Sort();
            double med = intervals[intervals.Count / 2];
            track.MedianBpm = med > 0 ? 60.0 * src.SampleRate / med : 0;
            return track;
        }

        /// <summary>The standardised onset strength and its frame rate (for the evaluation tool's debugging).</summary>
        internal static (double[] Env, double[] Low, double FrameRate, double[] Period) Debug(AudioSource src)
        {
            var fr = Analyse(src.ForAnalysis());
            var env = Standardise(fr.Flux, fr.FrameRate);
            if (Environment.GetEnvironmentVariable("RAW") != null) for (int i = 0; i < 30; i++) Console.WriteLine($"raw {i} flux {fr.Flux[i]:0.000} log0 {fr.Log[i * fr.Bands]:0.000} log5 {fr.Log[i * fr.Bands + 5]:0.000}");
            return (env, Standardise(fr.LowFlux, fr.FrameRate), fr.FrameRate, TempoCurve(env, fr.FrameRate));
        }

        static bool Active(double[] env, int f)
        {
            double m = 0;
            for (int k = Math.Max(0, f - 1); k <= Math.Min(env.Length - 1, f + 1); k++) m = Math.Max(m, env[k]);
            return m >= 0.5;
        }

        // ------------------------------------------------------------------ the onset strength

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static Frames Analyse(AudioSource src)
        {
            int rate = src.SampleRate;
            int D = Math.Max(1, (int)Math.Round(rate / TargetRate));
            double fs = rate / (double)D;
            int N = Window;
            int H = Math.Max(16, (int)Math.Round(HopSeconds * fs));
            long decLen = src.Length / D;
            int F = decLen < N ? 0 : (int)((decLen - N) / H) + 1;
            double binHz = fs / N;
            int kmax = Math.Min(N / 2 - 1, (int)(Math.Min(MaxFreq, 0.45 * fs) / binHz));
            // The bands: from 100 Hz up for the onset strength (the kick's body and a bass's notes beat against each other there, which is no onset);
            // the low ones (up to 200 Hz) are kept apart for the downbeat.
            int k0 = Math.Max(1, (int)Math.Ceiling(100 / binHz));
            var edges = new List<int> { k0 };
            const int Target = 24;
            for (int j = 1; j <= Target; j++)
            {
                int e = (int)Math.Round(k0 * Math.Pow((kmax + 1.0) / k0, j / (double)Target));
                if (j == Target) e = kmax + 1;
                if (e > edges[^1]) edges.Add(e);
            }
            int B = edges.Count - 1;
            int lowBands = 0;
            while (lowBands < B && edges[lowBands + 1] <= (int)Math.Ceiling(200 / binHz) + 1) lowBands++;
            lowBands = Math.Max(1, lowBands);
            // The level the analysis sees: the audio at -20 dB RMS whatever it was recorded at (a quiet recording has the same beats), unless it is silent.
            double rms = Rms(src);
            double gain = rms < 1e-6 ? 0 : Math.Min(1e4, 0.1 / rms);
            var fr = new Frames { Count = F, Bands = B, Decimation = D, Hop = H, Fs = fs, FrameRate = fs / H, Flux = new double[F], LowFlux = new double[F], Log = new float[(long)F * B] };
            if (F == 0) return fr;

            var fft = new RadixFft(N);
            var window = new double[N];
            for (int n = 0; n < N; n++) window[n] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / N);
            var re = new double[N];
            var im = new double[N];
            const int ChunkFrames = 128;
            int cap = N + H * (ChunkFrames - 1);
            var dec = new float[cap];
            var rawL = new float[cap * D];
            var rawR = src.IsMono ? null : new float[cap * D];
            var powA = new double[N / 2];
            var powB = new double[N / 2];
            double scale = 16.0 / ((double)N * N);
            for (int f0 = 0; f0 < F; f0 += ChunkFrames)
            {
                int nf = Math.Min(ChunkFrames, F - f0);
                long start = (long)f0 * H;                                   // the decimated index of dec[0]
                int need = N + H * (nf - 1);
                src.CopyExact(0, start * D, rawL.AsSpan(0, need * D));
                if (rawR != null) src.CopyExact(1, start * D, rawR.AsSpan(0, need * D));
                float inv = (float)(gain / D * (rawR != null ? 0.5 : 1.0));
                for (int i = 0; i < need; i++)
                {
                    float s = 0;
                    int o = i * D;
                    for (int d = 0; d < D; d++) s += rawL[o + d] + (rawR != null ? rawR[o + d] : 0f);
                    dec[i] = s * inv;
                }
                for (int j = 0; j < nf; j += 2)
                {
                    bool two = j + 1 < nf;
                    int a = j * H, b = (j + 1) * H;
                    for (int n = 0; n < N; n++)
                    {
                        re[n] = window[n] * dec[a + n];
                        im[n] = two ? window[n] * dec[b + n] : 0;
                    }
                    fft.Forward(re, im);
                    for (int k = 1; k <= kmax; k++)
                    {
                        double zr = re[k], zi = im[k], cr = re[N - k], ci = -im[N - k];
                        double sr = zr + cr, si = zi + ci, dr = zr - cr, di = zi - ci;
                        powA[k] = 0.25 * (sr * sr + si * si) * scale;
                        powB[k] = 0.25 * (dr * dr + di * di) * scale;
                    }
                    Bands(fr, f0 + j, powA, edges, B, lowBands);
                    if (two) Bands(fr, f0 + j + 1, powB, edges, B, lowBands);
                }
            }
            return fr;
        }

        /// <summary>The RMS of the audio (both channels' mean), read in blocks.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static double Rms(AudioSource src)
        {
            const int Block = 1 << 15;
            var l = new float[Block];
            var r = src.IsMono ? null : new float[Block];
            double sum = 0;
            for (long at = 0; at < src.Length; at += Block)
            {
                int n = (int)Math.Min(Block, src.Length - at);
                src.CopyExact(0, at, l.AsSpan(0, n));
                if (r != null) src.CopyExact(1, at, r.AsSpan(0, n));
                for (int i = 0; i < n; i++)
                {
                    double x = r == null ? l[i] : 0.5 * (l[i] + r[i]);
                    sum += x * x;
                }
            }
            return Math.Sqrt(sum / Math.Max(1, src.Length));
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Bands(Frames fr, int f, double[] pow, List<int> edges, int B, int lowBands)
        {
            int row = f * B;
            for (int b = 0; b < B; b++)
            {
                double e = 0;
                for (int k = edges[b]; k < edges[b + 1]; k++) e += pow[k];
                fr.Log[row + b] = (float)Math.Log(1 + 1000 * e);
            }
            if (f == 0) return;
            int prev = row - B;
            double flux = 0, low = 0;
            for (int b = 0; b < B; b++)
            {
                float p = fr.Log[prev + b];
                if (b > 0) p = Math.Max(p, fr.Log[prev + b - 1]);
                if (b + 1 < B) p = Math.Max(p, fr.Log[prev + b + 1]);
                double rise = fr.Log[row + b] - p;
                if (rise > 0)
                {
                    flux += rise;
                    if (b < lowBands) low += rise;
                }
            }
            fr.Flux[f] = flux;
            fr.LowFlux[f] = low;
        }

        /// <summary>
        /// The onset strength as the tracker reads it: what is above the local mean (half a second either way), over the local spread
        /// (five seconds either way, with a floor so silence stays silent).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static double[] Standardise(double[] x, double frameRate)
        {
            int n = x.Length;
            var mean = MovingAverage(x, (int)(0.4 * frameRate));
            var y = new double[n];
            double all = 0;
            for (int i = 0; i < n; i++) { y[i] = Math.Max(0, x[i] - mean[i]); all += y[i] * y[i]; }
            double global = Math.Sqrt(all / Math.Max(1, n));
            var sq = new double[n];
            for (int i = 0; i < n; i++) sq[i] = y[i] * y[i];
            var local = MovingAverage(sq, (int)(5 * frameRate));
            for (int i = 0; i < n; i++)
            {
                double s = Math.Sqrt(local[i]);
                y[i] /= Math.Max(Math.Max(s, 0.3 * global), AbsoluteFloor);
            }
            return y;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static double[] MovingAverage(double[] x, int half)
        {
            int n = x.Length;
            var cs = new double[n + 1];
            for (int i = 0; i < n; i++) cs[i + 1] = cs[i] + x[i];
            var y = new double[n];
            for (int i = 0; i < n; i++)
            {
                int a = Math.Max(0, i - half), b = Math.Min(n, i + half + 1);
                y[i] = (cs[b] - cs[a]) / (b - a);
            }
            return y;
        }

        // ------------------------------------------------------------------ the tempo

        /// <summary>The beat period in frames at every frame, or null when the audio has no periodicity.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static double[] TempoCurve(double[] env, double fr)
        {
            int n = env.Length;
            int K = (int)Math.Ceiling(Math.Log(MaxBpm / MinBpm) / Math.Log(BpmRatio)) + 1;
            var bpm = new double[K];
            for (int k = 0; k < K; k++) bpm[k] = MinBpm * Math.Pow(BpmRatio, k);
            int W = (int)Math.Round(TempoWindowSeconds * fr);
            int step = Math.Max(1, (int)Math.Round(TempoStepSeconds * fr));
            int size = 1;
            while (size < 2 * W) size <<= 1;
            var fft = new Fft(size);
            var re = new double[size];
            var im = new double[size];
            int windows = Math.Max(1, (n + step - 1) / step);
            var centre = new double[windows];
            var emit = new double[windows][];
            var prior = new double[K];
            for (int k = 0; k < K; k++) prior[k] = Math.Exp(-0.5 * Math.Pow(Math.Log(bpm[k] / 120) / Math.Log(2) / PriorOctaves, 2));
            int maxLag = (int)(0.6 * W);
            var r = new double[maxLag + 2];
            bool any = false;
            for (int w = 0; w < windows; w++)
            {
                int c = Math.Min(n - 1, w * step + step / 2);
                centre[w] = c;
                int a = c - W / 2;
                double mean = 0;
                int cnt = 0;
                for (int i = 0; i < W; i++)
                {
                    int t = a + i;
                    if (t >= 0 && t < n) { mean += env[t]; cnt++; }
                }
                var e = new double[K];
                emit[w] = e;
                if (cnt < 0.75 * W) continue;                // a window the audio does not fill says nothing
                mean /= cnt;
                Array.Clear(re); Array.Clear(im);
                for (int i = 0; i < W; i++)
                {
                    int t = a + i;
                    if (t >= 0 && t < n) re[i] = env[t] - mean;
                }
                fft.Transform(re, im);
                for (int i = 0; i < size; i++) { re[i] = re[i] * re[i] + im[i] * im[i]; im[i] = 0; }
                fft.Transform(re, im, true);
                double zero = re[0];
                if (zero <= 1e-9) continue;
                for (int l = 0; l <= maxLag + 1; l++) r[l] = re[l] / zero * W / Math.Max(1.0, W - l);
                double best = 0;
                for (int k = 0; k < K; k++)
                {
                    double lag = 60.0 / bpm[k] * fr;
                    double s = 0, ws = 0;
                    for (int q = 0; q < Harmonics.Length; q++)
                    {
                        int m = Harmonics[q];
                        double l = lag * m;
                        if (l >= maxLag) break;
                        int li = (int)l;
                        double fracPart = l - li;
                        s += HarmonicWeights[q] * (r[li] * (1 - fracPart) + r[li + 1] * fracPart);
                        ws += HarmonicWeights[q];
                    }
                    s = ws > 0 ? s / ws * prior[k] : 0;
                    e[k] = Math.Max(0, s);
                    if (e[k] > best) best = e[k];
                }
                if (best <= 0) { Array.Clear(e); continue; }
                double conf = Math.Min(1.0, best / 0.12);
                for (int k = 0; k < K; k++)
                {
                    double q = e[k] / best;
                    e[k] = conf * q * q * q;
                }
                if (conf > 0.3) any = true;
            }
            if (!any) return null;
            // Viterbi over the windows: a tempo may move a little between two, or jump at a cost.
            var trans = new double[K * K];
            for (int j = 0; j < K; j++)
                for (int k = 0; k < K; k++)
                {
                    double d = Math.Log(bpm[k] / bpm[j]);
                    trans[j * K + k] = Math.Min(DriftPenalty * d * d, JumpPenalty);
                }
            var score = new double[K];
            var next = new double[K];
            var back = new int[windows][];
            for (int k = 0; k < K; k++) score[k] = emit[0][k];
            back[0] = new int[K];
            for (int w = 1; w < windows; w++)
            {
                back[w] = new int[K];
                for (int k = 0; k < K; k++)
                {
                    double best = double.NegativeInfinity;
                    int arg = k;
                    for (int j = 0; j < K; j++)
                    {
                        double v = score[j] - trans[j * K + k];
                        if (v > best) { best = v; arg = j; }
                    }
                    next[k] = best + emit[w][k];
                    back[w][k] = arg;
                }
                (score, next) = (next, score);
            }
            int state = 0;
            for (int k = 1; k < K; k++) if (score[k] > score[state]) state = k;
            var path = new int[windows];
            for (int w = windows - 1; w >= 0; w--)
            {
                path[w] = state;
                state = back[w][state];
            }
            // Per frame, between the windows' centres, in the log of the period.
            var period = new double[n];
            for (int t = 0; t < n; t++)
            {
                double x = (t - step / 2.0) / step;
                int w0 = (int)Math.Floor(x);
                double q = x - w0;
                int a = Math.Clamp(w0, 0, windows - 1), b = Math.Clamp(w0 + 1, 0, windows - 1);
                double la = Math.Log(60.0 / bpm[path[a]] * fr), lb = Math.Log(60.0 / bpm[path[b]] * fr);
                period[t] = Math.Exp(la + (lb - la) * Math.Clamp(q, 0, 1));
            }
            return period;
        }

        // ------------------------------------------------------------------ the beats

        /// <summary>Ellis's dynamic programme: the frames of the beats, given the period at each frame.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int[] Dp(double[] o, double[] period, double alpha)
        {
            int n = o.Length;
            var C = new double[n];
            var P = new int[n];
            int maxP = (int)Math.Ceiling(1.7 * Max(period)) + 2;
            var ln = new double[maxP + 1];
            for (int p = 1; p <= maxP; p++) ln[p] = Math.Log(p);
            for (int t = 0; t < n; t++)
            {
                double tau = period[t];
                double lt = Math.Log(tau);
                int lo = Math.Max(1, (int)Math.Ceiling(0.55 * tau)), hi = (int)Math.Floor(1.7 * tau);
                double best = double.NegativeInfinity;
                int arg = -1;
                for (int p = lo; p <= hi && p <= t; p++)
                {
                    double d = ln[p] - lt;
                    double v = C[t - p] - alpha * d * d;
                    if (v > best) { best = v; arg = t - p; }
                }
                if (arg >= 0 && best > 0) { C[t] = o[t] + best; P[t] = arg; }
                else { C[t] = o[t]; P[t] = -1; }
            }
            // The last beat: the best frame in the last period and a half.
            double tauEnd = period[n - 1];
            int from = Math.Max(0, n - 1 - (int)(1.5 * tauEnd));
            int end = from;
            for (int t = from; t < n; t++) if (C[t] >= C[end]) end = t;
            var beats = new List<int>();
            for (int t = end; t >= 0; t = P[t])
            {
                beats.Add(t);
                if (P[t] < 0) break;
            }
            beats.Reverse();
            return beats.ToArray();
        }

        static double Max(double[] x)
        {
            double m = 0;
            foreach (var v in x) if (v > m) m = v;
            return m;
        }

        /// <summary>The period at each frame as the first pass's beats show it (their median interval over six), kept near the first pass's own.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static double[] PeriodFromBeats(int[] frames, double[] pos, Frames fr, double[] first)
        {
            int n = first.Length;
            int m = frames.Length;
            if (m < 8) return first;
            double unit = fr.Decimation * (double)fr.Hop;                 // source samples per frame
            var local = new double[m];
            var tmp = new double[6];
            for (int i = 0; i < m; i++)
            {
                int c = 0;
                for (int j = i - 3; j <= i + 2; j++)
                    if (j >= 0 && j + 1 < m) tmp[c++] = (pos[j + 1] - pos[j]) / unit;
                if (c == 0) { local[i] = first[frames[i]]; continue; }
                Array.Sort(tmp, 0, c);
                local[i] = c % 2 == 1 ? tmp[c / 2] : 0.5 * (tmp[c / 2 - 1] + tmp[c / 2]);
                double f1 = first[frames[i]];
                if (local[i] < 0.85 * f1 || local[i] > 1.15 * f1) local[i] = f1;      // not what the tempo curve says: not believed
            }
            var period = new double[n];
            int k = 0;
            for (int t = 0; t < n; t++)
            {
                while (k + 1 < m && frames[k + 1] <= t) k++;
                if (t <= frames[0]) period[t] = local[0];
                else if (k + 1 >= m) period[t] = local[m - 1];
                else
                {
                    double q = (t - frames[k]) / (double)Math.Max(1, frames[k + 1] - frames[k]);
                    period[t] = local[k] + (local[k + 1] - local[k]) * q;
                }
                if (t > frames[m - 1]) period[t] = local[m - 1];
            }
            return period;
        }

        // ------------------------------------------------------------------ where the beats sound

        /// <summary>
        /// A soft attack (a pad's swell, a bowed note) has no one place; a beat with a weak attack is put where its neighbours' line has it
        /// (a weighted line through the four beats either side, the sharp ones counting most), a sharp one stays where it sounds, and in
        /// between it is a mix.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static double[] Smooth(double[] pos, double[] attack)
        {
            int n = pos.Length;
            var result = (double[])pos.Clone();
            double soft = Math.Log(3), sharp = Math.Log(12);
            for (int i = 0; i < n; i++)
            {
                if (attack[i] >= sharp) continue;
                double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
                for (int j = Math.Max(0, i - 4); j <= Math.Min(n - 1, i + 4); j++)
                {
                    if (j == i) continue;                                 // the beat itself says nothing of where its neighbours' line puts it
                    double w = 0.1 + Math.Clamp(attack[j] / sharp, 0, 1);
                    double x = j - i;
                    sw += w; sx += w * x; sy += w * pos[j]; sxx += w * x * x; sxy += w * x * pos[j];
                }
                double den = sw * sxx - sx * sx;
                if (sw <= 0 || den <= 1e-9) continue;
                double slope = (sw * sxy - sx * sy) / den;
                double at = (sy - slope * sx) / sw;
                double t = Math.Clamp((attack[i] - soft) / (sharp - soft), 0, 1);
                result[i] = at + t * (pos[i] - at);
            }
            return result;
        }

        /// <summary>
        /// The beats' places in the audio, in samples: where the sound starts within 35 ms of the frame the programme found. The first difference
        /// of the audio (a bass's own pulses are no attack) is cut in blocks of 64 samples (1.5 ms); the attack is where the energy rises most
        /// in ratio from one block to the next, with a floor 30 dB under the loudest block of the window, so a soft attack is placed at its
        /// start (where the energy first leaves the floor) and a sharp one at the attack itself.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static double[] Refine(AudioSource src, Frames fr, int[] frames, out double[] strength)
        {
            const int Block = 64, Hop = 16;
            int rate = src.SampleRate;
            int reach = (int)(0.035 * rate);
            var pos = new double[frames.Length];
            strength = new double[frames.Length];
            int span = 2 * reach + 3 * Block;
            var l = new float[span];
            var r = src.IsMono ? null : new float[span];
            var d = new double[span];
            var cs = new double[span + 1];
            for (int i = 0; i < frames.Length; i++)
            {
                // The frame's centre is the middle of the window the flux saw; an attack is within the half window of it.
                long rough = (long)frames[i] * fr.Hop * fr.Decimation + (long)Window / 2 * fr.Decimation;
                long start = rough - reach - Block;
                src.CopyExact(0, start, l);
                if (r != null) src.CopyExact(1, start, r);
                double prev = 0;
                for (int k = 0; k < span; k++)
                {
                    double x = r == null ? l[k] : 0.5 * (l[k] + r[k]);
                    d[k] = x - 0.97 * prev;
                    prev = x;
                }
                for (int k = 0; k < span; k++) cs[k + 1] = cs[k] + d[k] * d[k];
                double loudest = 0;
                for (int k = Block; k + Block <= span; k += Hop) loudest = Math.Max(loudest, cs[k + Block] - cs[k]);
                double floor = 1e-3 * loudest + 1e-12;
                double best = double.MinValue;
                int at = reach + Block;
                for (int k = Block; k + Block <= span; k += Hop)
                {
                    double rise = Math.Log((cs[k + Block] - cs[k] + floor) / (cs[k] - cs[k - Block] + floor));
                    if (rise > best) { best = rise; at = k; }
                }
                // Steady sound (a pad without a swell) has no attack to find: the frame's own place then.
                if (best < Math.Log(3)) { at = reach + Block - Block / 2; best = 0; }
                strength[i] = best;
                pos[i] = start + at + 8;
            }
            return pos;
        }
    }
}
