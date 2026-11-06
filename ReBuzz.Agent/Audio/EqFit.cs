#nullable disable
// Agent server (docs/specs/mastering-tools.md §6): eq_fit, a few Neo EQ bands that follow a target curve,
// what Ozone's Match EQ does with 8,000 bands done with a handful of editable ones. The model is Neo EQ's
// own design (NeoBuzz.Eq.EqDesign, linked from the machine: the digital response it runs, at the song's
// rate), the bands' dB adding up. Bands come one at a time: each new one starts where the weighted
// residual is largest (a bell with that gain and a Q from the residual's width there, or a shelf when
// the residual leans at an end), is fitted alone, and then every band is refined together by damped
// Gauss–Newton (Levenberg–Marquardt) steps on frequency (log), gain and Q (log), within bounds and with
// a small penalty on gain, so overlapping bands don't fight. It stops at the band count, or once the
// residual's RMS is under the tolerance or no longer falls. A cut or a notch is never placed: a match
// moves bells and shelves.

using System;
using System.Collections.Generic;
using System.Linq;
using NeoBuzz.Eq;

namespace ReBuzz.Agent.Audio
{
    internal sealed class EqFitResult
    {
        public List<BandSettings> Bands = new();
        public double[] Hz, TargetDb, FittedDb;
        /// <summary>Weighted RMS of the target alone (no bands) and of what the bands leave, dB.</summary>
        public double RmsBeforeDb, RmsAfterDb;
        /// <summary>The largest |residual| where the weight is full, dB.</summary>
        public double WorstDb;
        public double WorstHz;
    }

    internal static class EqFit
    {
        public const double MinHz = 20, MaxHz = 20000, MinQ = 0.3, MaxQ = 6, ShelfQ = 0.71;

        /// <summary>
        /// Fits up to <paramref name="maxBands"/> bands to <paramref name="targetDb"/> at <paramref name="hz"/>, each gain within
        /// ±<paramref name="maxGainDb"/>; <paramref name="weight"/> per point (null: full from 40 Hz to 12 kHz, half outside).
        /// </summary>
        public static EqFitResult Fit(double[] hz, double[] targetDb, double sampleRate, int maxBands = 5, double maxGainDb = 6,
                                      double[] weight = null, double toleranceDb = 0.2)
        {
            int n = hz.Length;
            weight ??= hz.Select(f => f >= 40 && f <= 12000 ? 1.0 : 0.5).ToArray();
            for (int i = 0; i < n; i++) if (!double.IsFinite(targetDb[i])) weight[i] = 0;
            var target = targetDb.Select(d => double.IsFinite(d) ? d : 0).ToArray();
            double top = Math.Min(MaxHz, 0.45 * sampleRate);
            var bands = new List<BandSettings>();
            var r = new EqFitResult { Hz = hz, TargetDb = target, RmsBeforeDb = Rms(target, weight) };
            double last = r.RmsBeforeDb;
            while (bands.Count < maxBands && last > toleranceDb)
            {
                var residual = Residual(hz, target, bands, sampleRate);
                BandSettings best = default;
                double bestSse = double.MaxValue;
                foreach (var candidate in Candidates(hz, residual, weight, maxGainDb, top))
                {
                    var trial = new List<BandSettings>(bands) { candidate };
                    Refine(hz, target, weight, trial, sampleRate, maxGainDb, top, only: trial.Count - 1, iterations: 12);
                    double sse = Sse(hz, target, weight, trial, sampleRate, maxGainDb);
                    if (sse < bestSse)
                    {
                        bestSse = sse;
                        best = trial[^1];
                    }
                }
                if (bestSse == double.MaxValue) break;
                var next = new List<BandSettings>(bands) { best };
                Refine(hz, target, weight, next, sampleRate, maxGainDb, top, only: -1, iterations: 30);
                double rms = Rms(Residual(hz, target, next, sampleRate), weight);
                if (rms > last - 0.02) break;          // a band that doesn't help isn't kept
                bands = next;
                last = rms;
            }
            bands.Sort((a, b) => a.Hz.CompareTo(b.Hz));
            r.Bands = bands;
            r.FittedDb = Model(hz, bands, sampleRate);
            var left = Residual(hz, target, bands, sampleRate);
            r.RmsAfterDb = Rms(left, weight);
            for (int i = 0; i < n; i++)
                if (weight[i] >= 1 && Math.Abs(left[i]) > Math.Abs(r.WorstDb))
                {
                    r.WorstDb = left[i];
                    r.WorstHz = hz[i];
                }
            return r;
        }

        static double Rms(double[] x, double[] w)
        {
            double s = 0, ws = 0;
            for (int i = 0; i < x.Length; i++)
            {
                s += w[i] * x[i] * x[i];
                ws += w[i];
            }
            return ws > 0 ? Math.Sqrt(s / ws) : 0;
        }

        static double BandDb(in BandSettings b, double hz, double rate) => 20 * Math.Log10(Math.Max(1e-12, EqDesign.Magnitude(b, hz, rate)));

        public static double[] Model(double[] hz, List<BandSettings> bands, double rate)
        {
            var y = new double[hz.Length];
            foreach (var b in bands)
                for (int i = 0; i < hz.Length; i++) y[i] += BandDb(b, hz[i], rate);
            return y;
        }

        static double[] Residual(double[] hz, double[] target, List<BandSettings> bands, double rate)
        {
            var m = Model(hz, bands, rate);
            return target.Select((t, i) => t - m[i]).ToArray();
        }

        const double GainPenalty = 0.002;

        static double Sse(double[] hz, double[] target, double[] w, List<BandSettings> bands, double rate, double maxGain)
        {
            var e = Residual(hz, target, bands, rate);
            double s = 0;
            for (int i = 0; i < e.Length; i++) s += w[i] * e[i] * e[i];
            foreach (var b in bands) s += GainPenalty * b.GainDb * b.GainDb;
            return s;
        }

        /// <summary>Where a new band could start: a bell at the largest weighted residual, and shelves when an end leans.</summary>
        static IEnumerable<BandSettings> Candidates(double[] hz, double[] residual, double[] w, double maxGain, double top)
        {
            int peak = 0;
            for (int i = 0; i < hz.Length; i++)
                if (w[i] * Math.Abs(residual[i]) > w[peak] * Math.Abs(residual[peak])) peak = i;
            double g = residual[peak];
            // The width of the lobe around the peak: where the residual keeps its sign and half its size.
            int lo = peak, hi = peak;
            while (lo > 0 && Math.Sign(residual[lo - 1]) == Math.Sign(g) && Math.Abs(residual[lo - 1]) >= Math.Abs(g) / 2) lo--;
            while (hi < hz.Length - 1 && Math.Sign(residual[hi + 1]) == Math.Sign(g) && Math.Abs(residual[hi + 1]) >= Math.Abs(g) / 2) hi++;
            double octaves = Math.Max(0.3, Math.Log2(hz[hi] / hz[lo]) + 1.0 / 6);
            double bw = Math.Pow(2, octaves);
            double q = Math.Clamp(Math.Sqrt(bw) / (bw - 1), MinQ, MaxQ);
            yield return new BandSettings { Type = BandType.Bell, Hz = Math.Clamp(hz[peak], MinHz, top), GainDb = Math.Clamp(g, -maxGain, maxGain), Q = q };
            double Mean(Func<double, bool> where) =>
                Enumerable.Range(0, hz.Length).Where(i => where(hz[i]) && w[i] > 0).Select(i => residual[i]).DefaultIfEmpty(0).Average();
            double lowLean = Mean(f => f < 120), highLean = Mean(f => f > 6000);
            if (Math.Abs(lowLean) > 0.3) yield return new BandSettings { Type = BandType.LowShelf, Hz = 100, GainDb = Math.Clamp(lowLean, -maxGain, maxGain), Q = ShelfQ };
            if (Math.Abs(highLean) > 0.3) yield return new BandSettings { Type = BandType.HighShelf, Hz = Math.Min(8000, top), GainDb = Math.Clamp(highLean, -maxGain, maxGain), Q = ShelfQ };
        }

        /// <summary>A band's parameters as the solver moves them: log2 frequency, gain, log2 Q (shelves keep their Q).</summary>
        static int Params(in BandSettings b) => b.Type == BandType.Bell ? 3 : 2;

        static double Get(in BandSettings b, int k) => k == 0 ? Math.Log2(b.Hz) : k == 1 ? b.GainDb : Math.Log2(b.Q);

        static BandSettings With(BandSettings b, int k, double v, double maxGain, double top)
        {
            if (k == 0) b.Hz = Math.Clamp(Math.Pow(2, v), MinHz, top);
            else if (k == 1) b.GainDb = Math.Clamp(v, -maxGain, maxGain);
            else b.Q = Math.Clamp(Math.Pow(2, v), MinQ, MaxQ);
            return b;
        }

        /// <summary>Levenberg–Marquardt on band <paramref name="only"/> (−1: all), the others fixed.</summary>
        static void Refine(double[] hz, double[] target, double[] w, List<BandSettings> bands, double rate, double maxGain, double top, int only, int iterations)
        {
            int n = hz.Length;
            var index = new List<(int Band, int K)>();
            for (int b = 0; b < bands.Count; b++)
                if (only < 0 || b == only)
                    for (int k = 0; k < Params(bands[b]); k++) index.Add((b, k));
            int m = index.Count;
            if (m == 0) return;
            double mu = 1e-2;
            double sse = Sse(hz, target, w, bands, rate, maxGain);
            var contribution = bands.Select(b => hz.Select(f => BandDb(b, f, rate)).ToArray()).ToList();
            for (int it = 0; it < iterations; it++)
            {
                // The residual and the Jacobian of the model (each parameter moves only its own band's curve).
                var model = new double[n];
                foreach (var c in contribution) for (int i = 0; i < n; i++) model[i] += c[i];
                var e = target.Select((t, i) => t - model[i]).ToArray();
                var jac = new double[m][];
                for (int p = 0; p < m; p++)
                {
                    var (b, k) = index[p];
                    double h = k == 1 ? 1e-3 : 1e-4, v = Get(bands[b], k);
                    var plus = With(bands[b], k, v + h, maxGain, top);
                    var minus = With(bands[b], k, v - h, maxGain, top);
                    double span = Get(plus, k) - Get(minus, k);
                    jac[p] = new double[n];
                    if (span == 0) continue;            // at a bound
                    for (int i = 0; i < n; i++) jac[p][i] = (BandDb(plus, hz[i], rate) - BandDb(minus, hz[i], rate)) / span;
                }
                // (JᵀWJ + μ diag + penalty) Δ = JᵀW e − penalty·g.
                var a = new double[m, m];
                var g = new double[m];
                for (int p = 0; p < m; p++)
                {
                    for (int q = p; q < m; q++)
                    {
                        double s = 0;
                        for (int i = 0; i < n; i++) s += w[i] * jac[p][i] * jac[q][i];
                        a[p, q] = a[q, p] = s;
                    }
                    double r = 0;
                    for (int i = 0; i < n; i++) r += w[i] * jac[p][i] * e[i];
                    var (bb, kk) = index[p];
                    if (kk == 1)
                    {
                        a[p, p] += GainPenalty;
                        r -= GainPenalty * bands[bb].GainDb;
                    }
                    g[p] = r;
                }
                bool improved = false;
                for (int attempt = 0; attempt < 8 && !improved; attempt++)
                {
                    var damped = (double[,])a.Clone();
                    for (int p = 0; p < m; p++) damped[p, p] += mu * Math.Max(1e-9, a[p, p]);
                    var step = Solve(damped, g);
                    if (step == null) { mu *= 10; continue; }
                    var trial = new List<BandSettings>(bands);
                    for (int p = 0; p < m; p++)
                    {
                        var (b, k) = index[p];
                        trial[b] = With(trial[b], k, Get(trial[b], k) + step[p], maxGain, top);
                    }
                    double s = Sse(hz, target, w, trial, rate, maxGain);
                    if (s < sse)
                    {
                        bool done = sse - s < 1e-7 * Math.Max(1, sse);
                        for (int b = 0; b < bands.Count; b++)
                            if (!bands[b].Equals(trial[b])) contribution[b] = hz.Select(f => BandDb(trial[b], f, rate)).ToArray();
                        bands.Clear();
                        bands.AddRange(trial);
                        sse = s;
                        mu = Math.Max(1e-7, mu / 3);
                        improved = true;
                        if (done) return;
                    }
                    else mu *= 4;
                }
                if (!improved) return;
            }
        }

        /// <summary>Gaussian elimination with partial pivoting; null when singular.</summary>
        static double[] Solve(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = (double[,])a.Clone();
            var x = (double[])b.Clone();
            for (int c = 0; c < n; c++)
            {
                int p = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
                if (Math.Abs(m[p, c]) < 1e-15) return null;
                if (p != c)
                {
                    for (int k = 0; k < n; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]);
                    (x[c], x[p]) = (x[p], x[c]);
                }
                for (int r = c + 1; r < n; r++)
                {
                    double f = m[r, c] / m[c, c];
                    if (f == 0) continue;
                    for (int k = c; k < n; k++) m[r, k] -= f * m[c, k];
                    x[r] -= f * x[c];
                }
            }
            for (int c = n - 1; c >= 0; c--)
            {
                double s = x[c];
                for (int k = c + 1; k < n; k++) s -= m[c, k] * x[k];
                x[c] = s / m[c, c];
            }
            return x;
        }
    }
}
