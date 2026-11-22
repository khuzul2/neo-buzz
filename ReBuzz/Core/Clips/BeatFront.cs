#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW2 as built): the one feature definition of the beat model, used by training (tools/Aw2Tool writes the
// features of the training audio) and by the app, so both see identical features. Mono audio at 22.05 kHz, scaled to -20 dB RMS, a 1024-point Hann STFT
// every 441 samples (50 frames a second, frame i centred on sample 441 i), 128 triangular mel bands (HTK scale, 30 Hz to 10 kHz), v = ln(1 + 1000 m / 256)
// of the magnitude m, stored as bytes: round(36 v), clipped to 0..255 (the model reads byte / 36). Pure (ReBuzz.SessionTests links this file).

using System;
using System.Threading.Tasks;

namespace ReBuzz.Core.Clips
{
    internal static class BeatFront
    {
        public const int Rate = 22050, FftSize = 1024, Hop = 441, Mels = 128;
        public const double ByteScale = 36.0;
        const double FMin = 30, FMax = 10000;
        const double TargetRms = 0.1;

        static float[][] filterWeights;
        static int[] filterStart;
        static readonly object gate = new object();

        static double Mel(double hz) => 2595.0 * Math.Log10(1 + hz / 700.0);
        static double Hz(double mel) => 700.0 * (Math.Pow(10, mel / 2595.0) - 1);

        static void Filters()
        {
            lock (gate)
            {
                if (filterWeights != null) return;
                int bins = FftSize / 2 + 1;
                double binHz = Rate / (double)FftSize;
                var edges = new double[Mels + 2];
                double m0 = Mel(FMin), m1 = Mel(FMax);
                for (int i = 0; i < edges.Length; i++) edges[i] = Hz(m0 + (m1 - m0) * i / (Mels + 1));
                var w = new float[Mels][];
                var st = new int[Mels];
                for (int b = 0; b < Mels; b++)
                {
                    double lo = edges[b], mid = edges[b + 1], hi = edges[b + 2];
                    int a = Math.Max(0, (int)Math.Floor(lo / binHz)), z = Math.Min(bins - 1, (int)Math.Ceiling(hi / binHz));
                    var row = new float[z - a + 1];
                    double sum = 0;
                    for (int k = a; k <= z; k++)
                    {
                        double f = k * binHz;
                        double v = f <= lo || f >= hi ? 0 : f < mid ? (f - lo) / (mid - lo) : (hi - f) / (hi - mid);
                        row[k - a] = (float)v; sum += v;
                    }
                    if (sum <= 0)
                    {
                        // A band narrower than a bin: the bin nearest its centre.
                        int k = (int)Math.Round(mid / binHz);
                        k = Math.Clamp(k, a, z);
                        row[k - a] = 1;
                    }
                    w[b] = row; st[b] = a;
                }
                filterStart = st; filterWeights = w;
            }
        }

        /// <summary>The features of a source: frames x 128 bytes (frame-major). The audio is read in blocks, mixed to mono and resampled to 22.05 kHz.</summary>
        public static byte[] Compute(AudioSource src, out int frames)
        {
            var mono = Mono22k(src);
            return Compute(mono, out frames);
        }

        /// <summary>The audio as mono at 22.05 kHz, as <see cref="Compute(float[],out int)"/> takes it.</summary>
        public static float[] Mono22k(AudioSource src)
        {
            src = src.ForAnalysis();
            int n = src.Length;
            var m = new float[n];
            const int Block = 1 << 16;
            var l = new float[Block];
            var r = src.IsMono ? null : new float[Block];
            for (int at = 0; at < n; at += Block)
            {
                int c = Math.Min(Block, n - at);
                src.CopyExact(0, at, l.AsSpan(0, c));
                if (r != null) src.CopyExact(1, at, r.AsSpan(0, c));
                for (int i = 0; i < c; i++) m[at + i] = r == null ? l[i] : 0.5f * (l[i] + r[i]);
            }
            return src.SampleRate == Rate ? m : Resample(m, src.SampleRate, Rate);
        }

        /// <summary>
        /// A windowed-sinc resampler (Blackman window, 16 zero crossings, cut-off at 0.97 of the lower Nyquist), polyphase: the filter for each output phase is
        /// tabulated (exactly when the rates reduce to at most 1024 phases, else on 1024 sub-sample phases), so a sample costs one dot product.
        /// </summary>
        public static float[] Resample(float[] x, int from, int to)
        {
            int n = (int)((long)x.Length * to / from);
            var y = new float[n];
            int g = Gcd(from, to);
            int num = to / g, den = from / g;                        // output sample o is at input position o * den / num
            bool exact = num <= 1024;
            int phases = exact ? num : 1024;
            double cut = Math.Min(1.0, to / (double)from) * 0.97;
            int half = (int)Math.Ceiling(16 / cut);
            int taps = 2 * half + 1;
            var table = new float[phases][];
            for (int p = 0; p < phases; p++)
            {
                double frac = exact ? (double)(p * (long)den % num) / num : p / (double)phases;     // the position's fraction
                var w = new float[taps];
                double sum = 0;
                for (int k = 0; k < taps; k++)
                {
                    double d = (k - half) - frac;                    // input sample (floor + k - half) relative to the position
                    double u = d * cut;
                    double sinc = Math.Abs(u) < 1e-9 ? 1.0 : Math.Sin(Math.PI * u) / (Math.PI * u);
                    double t = d / (half + 1);
                    double win = Math.Abs(t) >= 1 ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * t) + 0.08 * Math.Cos(2 * Math.PI * t);
                    w[k] = (float)(sinc * win * cut);
                    sum += w[k];
                }
                for (int k = 0; k < taps; k++) w[k] = (float)(w[k] / sum);       // unit gain at DC
                table[p] = w;
            }
            var padded = new float[x.Length + 2 * taps];
            Array.Copy(x, 0, padded, taps, x.Length);
            Parallel.For(0, (n + 8191) / 8192, blk =>
            {
                int a = blk * 8192, b = Math.Min(n, a + 8192);
                for (int o = a; o < b; o++)
                {
                    long pos = (long)o * den;
                    long ip = pos / num;
                    int phase = exact ? (int)(pos % num) : (int)((pos % num) * 1024 / num);
                    var w = table[phase];
                    int start = (int)ip - half + taps;
                    float acc = 0;
                    var xs = padded.AsSpan(start, taps);
                    if (System.Numerics.Vector.IsHardwareAccelerated)
                    {
                        var v = System.Numerics.Vector<float>.Zero;
                        int vc = System.Numerics.Vector<float>.Count, k = 0;
                        for (; k <= taps - vc; k += vc) v += new System.Numerics.Vector<float>(xs.Slice(k)) * new System.Numerics.Vector<float>(w.AsSpan(k));
                        acc = System.Numerics.Vector.Sum(v);
                        for (; k < taps; k++) acc += xs[k] * w[k];
                    }
                    else for (int k = 0; k < taps; k++) acc += xs[k] * w[k];
                    y[o] = acc;
                }
            });
            return y;
        }

        static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

        /// <summary>The features of mono audio already at 22.05 kHz.</summary>
        public static byte[] Compute(float[] x, out int frames)
        {
            Filters();
            int n = x.Length;
            frames = n / Hop + 1;
            int nf = frames;
            var q = new byte[(long)nf * Mels];
            double sum = 0;
            for (int i = 0; i < n; i++) sum += (double)x[i] * x[i];
            double rms = Math.Sqrt(sum / Math.Max(1, n));
            double gain = rms < 1e-6 ? 0 : TargetRms / rms;
            var window = new double[FftSize];
            for (int i = 0; i < FftSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize);
            int pairs = (nf + 1) / 2;
            int chunk = 64;
            Parallel.For(0, (pairs + chunk - 1) / chunk, () => (new RadixFft(FftSize), new double[FftSize], new double[FftSize], new double[FftSize / 2 + 1], new double[FftSize / 2 + 1]), (c, st, local) =>
            {
                var (fft, re, im, pa, pb) = local;
                for (int pr = c * chunk; pr < Math.Min(pairs, (c + 1) * chunk); pr++)
                {
                    int fa = 2 * pr, fb = 2 * pr + 1;
                    bool two = fb < nf;
                    for (int i = 0; i < FftSize; i++)
                    {
                        long ia = (long)fa * Hop - FftSize / 2 + i, ib = (long)fb * Hop - FftSize / 2 + i;
                        re[i] = ia >= 0 && ia < n ? window[i] * x[ia] * gain : 0;
                        im[i] = two && ib >= 0 && ib < n ? window[i] * x[ib] * gain : 0;
                    }
                    fft.Forward(re, im);
                    for (int k = 0; k <= FftSize / 2; k++)
                    {
                        int k2 = (FftSize - k) & (FftSize - 1);
                        double zr = re[k], zi = im[k], cr = re[k2], ci = -im[k2];
                        double sr = zr + cr, si = zi + ci, dr = zr - cr, di = zi - ci;
                        pa[k] = 0.5 * Math.Sqrt(sr * sr + si * si) / 256.0;
                        pb[k] = 0.5 * Math.Sqrt(dr * dr + di * di) / 256.0;
                    }
                    Bands(q, fa, pa);
                    if (two) Bands(q, fb, pb);
                }
                return local;
            }, _ => { });
            return q;
        }

        static void Bands(byte[] q, int frame, double[] mag)
        {
            long row = (long)frame * Mels;
            for (int b = 0; b < Mels; b++)
            {
                var w = filterWeights[b];
                int a = filterStart[b];
                double e = 0;
                for (int k = 0; k < w.Length; k++) e += w[k] * mag[a + k];
                double v = Math.Log(1 + 1000 * e);
                q[row + b] = (byte)Math.Clamp((int)Math.Round(v * ByteScale), 0, 255);
            }
        }
    }
}
