using System;
using System.Collections.Generic;
using System.Linq;
// The artefact detector (docs/specs/neo-tune.md section 12.1). It compares the output of a corrector with its input (latency-aligned, level-matched) and finds the clicks and
// glitches a listener hears at note transitions, which NT2's own measure (the 99th percentile of 2 ms level jumps) did not see.
//
// A voiced sound is nearly periodic: with the local period T (tracked on the signal itself, 2 ms hop, normalised autocorrelation, refined to a fraction of a sample) the
// aperiodic residual q(t) = x(t) - (x(t - T) + x(t + T)) / 2 of the high-passed (300 Hz) signal is small, whatever the pitch, and a splice that does not
// join the same phase of the cycle, a period cut where the marks are wrong, a chatter of the pitch, shows as a burst in it. The residual envelope (1 ms RMS) of the
// output is compared with the largest residual envelope of the INPUT within +-8 ms (the input's own consonants, onsets and glides are in there, so they do not count): a hit is
// a place where the output's residual is more than K (2.2) times the input's and at least 25 % of the local signal (-12 dB): an audible click and not a rounding.
// Hits within 10 ms are one event. The same detector run on the input against itself, or a copy of it (aligned, level-matched, delayed by a fraction of a sample), finds none.

namespace NeoBuzz.PitchEval
{
    internal static class TuneDetector
    {
        internal sealed class Hit
        {
            public double Time;          // seconds
            public double Excess;        // output residual over the input's (ratio)
            public double LevelDb;       // the residual relative to the local signal, dB
            public string Cause = "";    // filled in by the cause attribution when the engine's event log is at hand
            public bool Step;            // an intended pitch step (see TuneArtefacts.Classify): not counted as an artefact
            public double StepSemis;     // the size of the shift step around it, semitones
        }

        public const double DefaultK = 2.2, RelLevel = 0.25;

        /// <summary>The aperiodic residual of x, same length (0 where the signal is not periodic).</summary>
        public static float[] Residual(float[] x, int rate)
        {
            int n = x.Length;
            // 300 Hz high-pass, 2nd order Butterworth (bilinear)
            var xh = new float[n];
            {
                double w0 = Math.Tan(Math.PI * 300.0 / rate), k = Math.Sqrt(2.0) * w0, w2 = w0 * w0, a0 = 1 + k + w2;
                double b0 = 1 / a0, b1 = -2 / a0, b2 = 1 / a0, a1 = 2 * (w2 - 1) / a0, a2 = (1 - k + w2) / a0;
                double z1 = 0, z2 = 0;
                for (int i = 0; i < n; i++)
                {
                    double v = x[i];
                    double y = b0 * v + z1;
                    z1 = b1 * v - a1 * y + z2;
                    z2 = b2 * v - a2 * y;
                    xh[i] = (float)y;
                }
            }
            // decimated by 4 for the period search
            int dec = 4, nd = n / dec;
            var xd = new float[nd];
            for (int i = 0; i < nd; i++) { float s = 0; for (int k = 0; k < dec; k++) s += xh[i * dec + k]; xd[i] = s * 0.25f; }
            int hop = Math.Max(1, (int)(0.002 * rate)), win = (int)(0.030 * rate) / dec;
            int lmin = Math.Max(4, (int)(rate / 900.0) / dec), lmax = (int)(rate / 70.0) / dec;
            var q = new float[n];
            var r = new double[lmax + 2];
            for (int a = 0; a + hop <= n; a += hop)
            {
                int c = a / dec;
                int s0 = c - win / 2, s1 = s0 + win;
                if (s0 < 0 || s1 + lmax + 2 >= nd) continue;
                double e0 = 0;
                for (int i = s0; i < s1; i++) e0 += (double)xd[i] * xd[i];
                if (e0 < 1e-7) continue;
                double best = -1; int bl = 0;
                for (int l = lmin; l <= lmax + 1; l++)
                {
                    double cc = 0, e1 = 0;
                    for (int i = s0; i < s1; i++) { cc += (double)xd[i] * xd[i + l]; e1 += (double)xd[i + l] * xd[i + l]; }
                    r[l - lmin] = cc / Math.Sqrt(e0 * e1 + 1e-18);
                    if (r[l - lmin] > best) { best = r[l - lmin]; bl = l; }
                }
                if (best < 0.5) continue;
                int pick = bl;
                for (int l = lmin + 1; l <= lmax; l++)
                    if (r[l - lmin] >= 0.92 * best && r[l - lmin] >= r[l - lmin - 1] && r[l - lmin] >= r[l - lmin + 1]) { pick = l; break; }
                // refine at the full rate around 4 * pick
                int t0 = pick * dec;
                int fs0 = a - win * dec / 2, fs1 = fs0 + win * dec;
                if (fs0 < t0 + 8 || fs1 + t0 + 8 >= n) continue;
                double bestFull = -1; int bt = t0;
                for (int t = t0 - 5; t <= t0 + 5; t++)
                {
                    double cc = 0, e1 = 0, ee0 = 0;
                    for (int i = fs0; i < fs1; i += 2) { cc += (double)xh[i] * xh[i + t]; e1 += (double)xh[i + t] * xh[i + t]; ee0 += (double)xh[i] * xh[i]; }
                    double v = cc / Math.Sqrt(ee0 * e1 + 1e-18);
                    if (v > bestFull) { bestFull = v; bt = t; }
                }
                // the fractional period that makes the residual smallest over this hop, among the period found, twice it and half of it (a voice with a strong second harmonic
                // is tracked now at T and now at T/2: the choice must not decide whether a splice is seen)
                double bestE = double.MaxValue, bestT = bt;
                for (int mult = 0; mult < 3; mult++)
                {
                    int bb = mult == 0 ? bt : mult == 1 ? 2 * bt : bt / 2;
                    if (bb < 8 || bb > rate / 55 || a < bb + 2 || a + hop + bb + 2 >= n) continue;
                    for (int k = -6; k <= 6; k++)
                    {
                        double T = bb + k * 0.25;
                        double ee = 0;
                        for (int i = a; i < a + hop; i++) { double d = xh[i] - 0.5 * (At(xh, i - T) + At(xh, i + T)); ee += d * d; }
                        if (ee < bestE) { bestE = ee; bestT = T; }
                    }
                }
                for (int i = a; i < a + hop; i++) q[i] = (float)(xh[i] - 0.5 * (At(xh, i - bestT) + At(xh, i + bestT)));
            }
            return q;
        }

        static double At(float[] x, double p)
        {
            int i = (int)Math.Floor(p);
            double f = p - i;
            if (i < 0 || i + 1 >= x.Length) return 0;
            return x[i] * (1 - f) + x[i + 1] * f;
        }

        /// <summary>1 ms RMS envelope.</summary>
        public static float[] Envelope(float[] q, int rate, double ms = 1.0)
        {
            int w = Math.Max(1, (int)(rate * ms / 1000));
            var e = new float[q.Length];
            double s = 0;
            var sq = new double[q.Length];
            for (int i = 0; i < q.Length; i++) sq[i] = (double)q[i] * q[i];
            var cs = new double[q.Length + 1];
            for (int i = 0; i < q.Length; i++) cs[i + 1] = cs[i] + sq[i];
            for (int i = 0; i < q.Length; i++)
            {
                int a = Math.Max(0, i - w / 2), b = Math.Min(q.Length, i + w / 2 + 1);
                e[i] = (float)Math.Sqrt(Math.Max(0, cs[b] - cs[a]) / (b - a));
            }
            return e;
        }

        /// <summary>Running maximum over +-half samples (a monotone deque).</summary>
        static float[] MaxFilter(float[] x, int half)
        {
            int n = x.Length; var o = new float[n]; var dq = new int[n]; int h = 0, t = 0;
            for (int i = 0; i < n + half; i++)
            {
                if (i < n) { while (t > h && x[dq[t - 1]] <= x[i]) t--; dq[t++] = i; }
                int c = i - half;
                if (c < 0) continue;
                while (dq[h] < c - half) h++;
                o[c] = x[dq[h]];
            }
            return o;
        }

        public static List<Hit> Detect(float[] input, float[] output, int rate, double k = DefaultK, float[] inResidual = null)
        {
            int n = Math.Min(input.Length, output.Length);
            var xi = input.Length == n ? input : input[..n]; var xo = output.Length == n ? output : output[..n];
            var qi = inResidual ?? Residual(xi, rate); var qo = Residual(xo, rate);
            var ei = Envelope(qi, rate); var eo = Envelope(qo, rate);
            var mi = MaxFilter(ei, (int)(0.008 * rate));
            // local signal level of the output, 10 ms RMS
            var lv = Envelope(xo, rate, 10.0);
            var hits = new List<Hit>();
            int merge = (int)(0.010 * rate);
            int lastI = -merge * 2;
            for (int i = 0; i < n; i++)
            {
                double ex = eo[i] / (mi[i] + 1e-5);
                if (ex > k && eo[i] > RelLevel * lv[i] + 1e-4)
                {
                    double lvl = 20 * Math.Log10((eo[i] + 1e-9) / (lv[i] + 1e-9));
                    if (hits.Count > 0 && i - lastI < merge)
                    {
                        var h = hits[^1];
                        if (ex > h.Excess) { h.Excess = ex; h.Time = i / (double)rate; h.LevelDb = lvl; }
                    }
                    else hits.Add(new Hit { Time = i / (double)rate, Excess = ex, LevelDb = lvl });
                    lastI = i;
                }
            }
            return hits;
        }

        /// <summary>The output scaled to the input's RMS (the level matching of the listening pack).</summary>
        public static float[] LevelMatch(float[] input, float[] output)
        {
            double a = 0, b = 0; int n = Math.Min(input.Length, output.Length);
            for (int i = 0; i < n; i++) { a += (double)input[i] * input[i]; b += (double)output[i] * output[i]; }
            double g = b > 1e-12 ? Math.Sqrt(a / b) : 1.0;
            var o = new float[output.Length];
            for (int i = 0; i < o.Length; i++) o[i] = (float)(output[i] * g);
            return o;
        }
    }
}
