#nullable disable
// Neo analysis library (docs/specs/mcp-server.md §7): onsets and tempo. A port of the Session
// View's spectral-flux analysis (ReBuzz/Core/Clips/AudioAnalysis.cs, session-view.md 1.0) to
// plain sample arrays, without its loop-length assumption: onsets are spectral-flux peaks over a
// running median; the tempo is the autocorrelation of the flux over 60–200 BPM (beat and its
// multiples), refined by fitting a beat grid to the onsets that fall on it.

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Analysis
{
    internal sealed class TempoResult
    {
        /// <summary>Beats per minute; 0 when there is no pulse.</summary>
        public double Bpm;
        /// <summary>0 … 1: how much of the flux repeats at the beat (the Session View's measure).</summary>
        public double Confidence;
        /// <summary>Time of the first beat of the fitted grid, seconds (NaN without a grid).</summary>
        public double FirstBeat = double.NaN;
        /// <summary>True when a beat grid fitted the onsets and set the tempo.</summary>
        public bool Grid;
        /// <summary>Onsets on the fitted grid.</summary>
        public int OnGrid;
        public List<double> Onsets;
        public double OnsetsPerSecond;
    }

    internal static class Tempo
    {
        /// <summary>Spectral flux below this is no transient (log-magnitude rises summed over the bins).</summary>
        const double MinFlux = 1.0;

        /// <summary>The flux frame (1024 samples, hop 256 up to 50 kHz; doubled above).</summary>
        static (int Size, int Hop) Frame(int rate) => rate > 50000 ? (2048, 512) : (1024, 256);

        /// <summary>Spectral flux per hop: the summed rises of log magnitude, floored 60 dB under each frame's loudest bin.</summary>
        public static double[] Flux(float[] x, int rate)
        {
            var (size, hop) = Frame(rate);
            int frames = Math.Max(0, (x.Length - size) / hop + 1);
            var fft = new Fft(size);
            var re = new double[size];
            var im = new double[size];
            var window = Fft.Hann(size);
            var previous = new double[size / 2];
            var flux = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                int at = f * hop;
                for (int n = 0; n < size; n++)
                {
                    re[n] = x[at + n] * window[n];
                    im[n] = 0;
                }
                fft.Transform(re, im);
                double loudest = 0;
                for (int k = 1; k < size / 2; k++)
                {
                    re[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * (4.0 / size);
                    if (re[k] > loudest) loudest = re[k];
                }
                double floor = Math.Max(1e-5, loudest * 1e-3);
                double sum = 0;
                for (int k = 1; k < size / 2; k++)
                {
                    double mag = Math.Log(1 + 100 * Math.Max(re[k], floor));
                    double rise = mag - previous[k];
                    if (rise > 0) sum += rise;
                    previous[k] = mag;
                }
                flux[f] = f == 0 ? 0 : sum;
            }
            return flux;
        }

        /// <summary>
        /// Transients, in seconds: flux peaks above a running median, at least 50 ms apart, each
        /// placed on the steepest rise of the waveform's energy near it, 2 ms early.
        /// </summary>
        public static List<double> Onsets(float[] x, int rate) => Onsets(x, rate, Flux(x, rate));

        static List<double> Onsets(float[] x, int rate, double[] flux)
        {
            var (size, hop) = Frame(rate);
            var onsets = new List<double>();
            if (flux.Length < 3) return onsets;
            int minGap = Math.Max(1, (int)(0.05 * rate / hop));
            double mean = flux.Average();
            double floor = Math.Max(MinFlux, 0.1 * flux.Max());
            int lastPeak = -minGap;
            var around = new double[17];
            for (int i = 1; i < flux.Length - 1; i++)
            {
                if (flux[i] < flux[i - 1] || flux[i] < flux[i + 1]) continue;
                int from = Math.Max(0, i - 8), to = Math.Min(flux.Length - 1, i + 8), count = to - from + 1;
                Array.Copy(flux, from, around, 0, count);
                Array.Sort(around, 0, count);
                double threshold = around[count / 2] * 1.5 + mean * 0.5 + 1e-6;
                if (flux[i] <= threshold || flux[i] < floor || i - lastPeak < minGap) continue;
                lastPeak = i;
                onsets.Add(Refine(x, rate, (long)i * hop + size / 2, size) / rate);
            }
            return onsets;
        }

        /// <summary>The steepest rise of 64-sample energy within a frame of a rough position, 2 ms before it (samples).</summary>
        static double Refine(float[] x, int rate, long rough, int size)
        {
            const int Block = 64;
            long from = Math.Max(0, rough - size), to = Math.Min(x.Length - Block, rough + size / 2);
            double best = double.MinValue;
            long at = rough;
            for (long i = from; i <= to; i += 16)
            {
                double rise = Energy(x, i, Block) - Energy(x, i - Block, Block);
                if (rise > best) { best = rise; at = i; }
            }
            return Math.Max(0, at - 0.002 * rate);
        }

        static double Energy(float[] x, long at, int count)
        {
            double sum = 0;
            for (long i = Math.Max(0, at), end = Math.Min(x.Length, at + count); i < end; i++) sum += x[i] * x[i];
            return sum;
        }

        /// <summary>The tempo and onsets of mono audio.</summary>
        public static TempoResult Measure(float[] x, int rate, double minBpm = 60, double maxBpm = 200)
        {
            var flux = Flux(x, rate);
            var result = new TempoResult { Onsets = Onsets(x, rate, flux) };
            double seconds = x.Length / (double)rate;
            result.OnsetsPerSecond = seconds > 0 ? result.Onsets.Count / seconds : 0;
            double bpm = Autocorrelation(flux, rate, minBpm, maxBpm, out double confidence);
            result.Bpm = bpm;
            result.Confidence = confidence;
            if (bpm > 0) FitGrid(result, maxBpm);
            return result;
        }

        /// <summary>
        /// The flux's autocorrelation at the beat and its multiples (a bar's worth, weighted 1/m),
        /// over a 0.05-BPM grid with a mild preference for tempos near 120 (one octave either side
        /// costs a third), refined between grid points. The autocorrelation is summed once per
        /// whole lag and read between lags linearly.
        /// </summary>
        static double Autocorrelation(double[] flux, int rate, double minBpm, double maxBpm, out double confidence)
        {
            confidence = 0;
            if (flux.Length < 16) return 0;
            var (_, hop) = Frame(rate);
            var f = (double[])flux.Clone();
            double mean = f.Average();
            for (int i = 0; i < f.Length; i++) f[i] = Math.Max(0, f[i] - mean);
            double frameRate = rate / (double)hop;
            double zero = 0;
            for (int i = 0; i < f.Length; i++) zero += f[i] * f[i];
            if (zero <= 0) return 0;
            const double Step = 0.05;
            int maxLag = Math.Min(f.Length - 1, (int)Math.Ceiling(4 * 60 / minBpm * frameRate) + 2);
            var r = new double[maxLag + 1];
            for (int l = 0; l <= maxLag; l++)
            {
                double acc = 0;
                for (int i = 0; i + l < f.Length; i++) acc += f[i] * f[i + l];
                r[l] = acc;
            }
            int steps = (int)Math.Round((maxBpm - minBpm) / Step);
            var score = new double[steps + 1];
            var raw = new double[steps + 1];
            int best = -1;
            for (int s = 0; s <= steps; s++)
            {
                double bpm = minBpm + s * Step;
                double lag = 60 / bpm * frameRate;
                double sum = 0;
                for (int m = 1; m <= 4; m++)
                {
                    double l = lag * m;
                    int li = (int)l;
                    if (li + 1 > maxLag) break;
                    double frac = l - li;
                    sum += (r[li] * (1 - frac) + r[li + 1] * frac) / m;
                }
                raw[s] = sum;
                double octaves = Math.Log2(bpm / 120);
                score[s] = sum * Math.Exp(-0.5 * octaves * octaves / (1.1 * 1.1));
                if (best < 0 || score[s] > score[best]) best = s;
            }
            if (raw[best] <= 0) return 0;
            confidence = Math.Min(1, raw[best] / (zero * (1 + 0.5 + 1.0 / 3 + 0.25)));
            double offset = 0;
            if (best > 0 && best < steps)
            {
                double a = score[best - 1], b = score[best], c = score[best + 1], den = a - 2 * b + c;
                if (den < 0) offset = Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5);
            }
            return minBpm + (best + offset) * Step;
        }

        /// <summary>
        /// Fits t = first + n·period to the onsets within a tenth of a beat of the grid (least
        /// squares, twice). Accepted when four or more onsets sit on it and the fitted tempo is
        /// within 2% of the autocorrelation's.
        /// </summary>
        static void FitGrid(TempoResult r, double maxBpm)
        {
            var onsets = r.Onsets;
            if (onsets.Count < 4) return;
            double period = 60 / r.Bpm;
            double bestAnchor = onsets[0];
            int bestCount = -1;
            foreach (double anchor in onsets.Take(8))
            {
                int count = onsets.Count(t => Math.Abs(Residual(t, anchor, period)) < 0.1 * period);
                if (count > bestCount) { bestCount = count; bestAnchor = anchor; }
            }
            double first = bestAnchor, p = period;
            int used = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                double sn = 0, st = 0, snn = 0, snt = 0;
                used = 0;
                foreach (double t in onsets)
                {
                    double n = Math.Round((t - first) / p);
                    if (Math.Abs(t - first - n * p) >= 0.1 * p) continue;
                    sn += n; st += t; snn += n * n; snt += n * t;
                    used++;
                }
                double den = used * snn - sn * sn;
                if (used < 4 || den <= 0) return;
                double slope = (used * snt - sn * st) / den;
                first = (st - slope * sn) / used;
                p = slope;
            }
            if (p <= 0 || Math.Abs(60 / p - r.Bpm) > 0.02 * r.Bpm || 60 / p > maxBpm * 1.02) return;
            r.Bpm = 60 / p;
            r.Grid = true;
            r.OnGrid = used;
            r.FirstBeat = first - Math.Floor(first / p) * p;
        }

        static double Residual(double t, double anchor, double period)
        {
            double n = Math.Round((t - anchor) / period);
            return t - anchor - n * period;
        }
    }
}
