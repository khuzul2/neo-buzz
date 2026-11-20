#nullable disable
// Fork (docs/specs/audio-to-midi.md, Convert to MIDI drums CD1): the features of one drum candidate. Offline, so the sound AFTER the onset is
// used as much as the sound before it: band energies (log spaced, 40 Hz to 18 kHz) in six windows from 45 ms before the onset to 300 ms after,
// in dB under the strongest band of the onset window, plus the onset's level and its distance to the neighbouring candidates. How a sound decays
// is what tells a closed hat from an open one, a hat from a cymbal, and a tom (a falling tone) from a snare.

using System;
using NeoBuzz.Dsp;

namespace ReBuzz.Core.AudioLibrary.AudioToMidi
{
    internal sealed class DrumFeatures
    {
        public const int Bands = 20;
        /// <summary>Window start and end, ms from the onset.</summary>
        public static readonly (double From, double To)[] Windows = { (-45, -3), (-3, 25), (25, 60), (60, 110), (110, 180), (180, 300) };
        /// <summary>Envelope points (ms after the onset) of three bands (40 to 150 Hz, 2 to 6 kHz, 6 to 16 kHz), in dB under the band's own early peak.</summary>
        public static readonly double[] EnvAt = { 5, 10, 20, 30, 45, 60, 80, 100, 130, 160, 200 };
        public const int Count = 6 * Bands + 4 + 3 * 11 + 6;
        public const double LowHz = 40, HighHz = 18000;
        /// <summary>Where the onset level (dB) and the log2 pitch of the low body (25 to 60 ms window) are in the vector.</summary>
        public const int LevelIndex = 6 * Bands, PeakHzIndex = 6 * Bands + 4 + 3 * 11 + 1;

        readonly int rate, n;
        readonly ComplexFft fft;
        readonly float[] re, im;
        readonly int[] lo = new int[Bands], hi = new int[Bands];
        readonly ComplexFft fftE = new ComplexFft(512);
        readonly float[] reE = new float[512], imE = new float[512];

        public DrumFeatures(int rate)
        {
            this.rate = rate;
            n = 8192;
            fft = new ComplexFft(n);
            re = new float[n]; im = new float[n];
            double top = Math.Min(HighHz, 0.45 * rate);
            for (int b = 0; b < Bands; b++)
            {
                double f0 = LowHz * Math.Pow(top / LowHz, b / (double)Bands), f1 = LowHz * Math.Pow(top / LowHz, (b + 1) / (double)Bands);
                lo[b] = Math.Max(1, (int)Math.Ceiling(f0 * n / rate));
                hi[b] = Math.Max(lo[b] + 1, (int)Math.Ceiling(f1 * n / rate));
            }
        }

        // 10 ms frames (Hann, 512-point FFT) centred at EnvAt; a frame that reaches into the next candidate repeats the last clean one.
        void Envelopes(float[] x, long sample, double nextGap, float[] f, int at)
        {
            int flen = (int)Math.Round(0.010 * rate);
            double[,] e = new double[3, EnvAt.Length + 1];
            double[] peak = { -200, -200, -200 };
            int[,] edge = new int[3, 2];
            double[][] bandHz = { new[] { 40.0, 150 }, new[] { 2000.0, 6000 }, new[] { 6000.0, Math.Min(16000, 0.45 * rate) } };
            for (int b = 0; b < 3; b++) { edge[b, 0] = Math.Max(1, (int)Math.Ceiling(bandHz[b][0] * 512 / rate)); edge[b, 1] = Math.Max(edge[b, 0] + 1, (int)Math.Ceiling(bandHz[b][1] * 512 / rate)); }
            // frame 0 is the onset frame (0 to 10 ms); then the EnvAt centres.
            for (int k = 0; k <= EnvAt.Length; k++)
            {
                double centre = k == 0 ? 5 : EnvAt[k - 1];
                long a = sample + (long)Math.Round((centre - 5) * 0.001 * rate);
                for (int i = 0; i < 512; i++)
                {
                    if (i < flen) { long p = a + i; float v = p >= 0 && p < x.Length ? x[p] : 0f; reE[i] = (float)(v * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / flen))); } else reE[i] = 0;
                    imE[i] = 0;
                }
                fftE.Transform(reE, imE, false);
                for (int b = 0; b < 3; b++)
                {
                    double s = 0;
                    for (int j = edge[b, 0]; j < edge[b, 1]; j++) s += (double)reE[j] * reE[j] + (double)imE[j] * imE[j];
                    e[b, k] = 10 * Math.Log10(s / (edge[b, 1] - edge[b, 0]) + 1e-12);
                    if (k <= 2) peak[b] = Math.Max(peak[b], e[b, k]);
                }
            }
            for (int b = 0; b < 3; b++)
            {
                double last = e[b, 1] - peak[b];
                for (int k = 0; k < EnvAt.Length; k++)
                {
                    bool clean = (EnvAt[k] + 5) * 0.001 <= nextGap - 0.003;
                    if (clean) last = e[b, k + 1] - peak[b];
                    f[at + b * EnvAt.Length + k] = (float)Math.Max(-90, last);
                }
            }
        }

        /// <param name="prevGap">Seconds to the candidate before (large when none), <paramref name="nextGap"/> to the one after.</param>
        public float[] Extract(float[] x, long sample, double prevGap, double nextGap)
        {
            var f = new float[Count];
            var db = new double[Windows.Length * Bands];
            double ref0 = double.NegativeInfinity, level = 0;
            var peakHz = new double[3]; var peakProm = new double[3];
            for (int w = 0; w < Windows.Length; w++)
            {
                long a = sample + (long)Math.Round(Windows[w].From * 0.001 * rate), b = sample + (long)Math.Round(Windows[w].To * 0.001 * rate);
                int len = (int)(b - a);
                double e = 0;
                for (int i = 0; i < n; i++)
                {
                    if (i < len)
                    {
                        long p = a + i;
                        float v = p >= 0 && p < x.Length ? x[p] : 0f;
                        double wv = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / len);
                        re[i] = (float)(v * wv); e += (double)v * v;
                    }
                    else re[i] = 0;
                    im[i] = 0;
                }
                fft.Transform(re, im, false);
                // Power per Hz of window (so windows of different length compare), per band mean.
                double scale = 1.0 / (len * 0.375);
                for (int k = 0; k < Bands; k++)
                {
                    double s = 0;
                    for (int j = lo[k]; j < hi[k] && j < n / 2; j++) s += (double)re[j] * re[j] + (double)im[j] * im[j];
                    db[w * Bands + k] = 10 * Math.Log10(s * scale / (hi[k] - lo[k]) + 1e-12);
                }
                if (w >= 1 && w <= 3)
                {
                    int k0 = (int)Math.Ceiling(50.0 * n / rate), k1 = (int)Math.Floor(500.0 * n / rate);
                    int bk = k0; double bp = -1, sum = 0;
                    for (int j = k0; j <= k1; j++) { double pw = (double)re[j] * re[j] + (double)im[j] * im[j]; sum += pw; if (pw > bp) { bp = pw; bk = j; } }
                    peakHz[w - 1] = Math.Log2(bk * (double)rate / n);
                    peakProm[w - 1] = 10 * Math.Log10(bp / (sum / (k1 - k0 + 1) + 1e-20) + 1e-9);
                }
                if (w == 1)
                {
                    for (int k = 0; k < Bands; k++) ref0 = Math.Max(ref0, db[w * Bands + k]);
                    level = 10 * Math.Log10(e / Math.Max(1, len) + 1e-12);
                }
            }
            for (int i = 0; i < db.Length; i++) f[i] = (float)Math.Max(-100, db[i] - ref0);
            f[Windows.Length * Bands] = (float)level;
            f[Windows.Length * Bands + 1] = (float)Math.Log10(Math.Min(2.0, prevGap) + 0.01);
            f[Windows.Length * Bands + 2] = (float)Math.Log10(Math.Min(2.0, nextGap) + 0.01);
            f[Windows.Length * Bands + 3] = (float)ref0;
            int at = Windows.Length * Bands + 4;
            Envelopes(x, sample, nextGap, f, at);
            at += 3 * EnvAt.Length;
            for (int i = 0; i < 3; i++) { f[at + i] = (float)peakHz[i]; f[at + 3 + i] = (float)peakProm[i]; }
            return f;
        }
    }
}
