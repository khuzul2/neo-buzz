#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW2 as built): the beats of a piece of audio from the trained beat model. The model gives, 50 times a
// second, how likely a beat and a downbeat are, and a tempo distribution; this decodes them into beat positions with the machinery AW1 used on its own onset
// strength, widened to 55 to 215 BPM: a tempo curve (windowed autocorrelation of the beat activation, weighted by the model's tempo distribution, a Viterbi path
// over the windows) and Ellis's dynamic programme (2007) over the activation. The octave is what the model's activation fires on (it fires on beats, not on
// subdivisions); the bar phase is the one whose beats have the highest downbeat activation. Returns null when there is no model or it is not confident,
// and the caller then uses AW1's DSP tracker. Pure (ReBuzz.SessionTests links this file).

using System;
using System.Collections.Generic;

namespace ReBuzz.Core.Clips
{
    /// <summary>The beat model the app holds (none until the weights are shipped: AW2b), and the switch the tests use to take it away.</summary>
    internal static class BeatModel
    {
        static BeatModelNet embedded;
        static bool embeddedTried;
        [ThreadStatic] static BeatModelNet scoped;
        [ThreadStatic] static bool scopedSet;
        static volatile BeatModelNet global;
        static volatile bool globalSet;

        /// <summary>The model in use, or null (then AW1's DSP path is used).</summary>
        public static BeatModelNet Current
        {
            get
            {
                if (scopedSet) return scoped;
                if (globalSet) return global;
                if (!embeddedTried)
                {
                    embeddedTried = true;
                    try { var bytes = EmbeddedBytes(); embedded = bytes == null ? null : BeatModelNet.Load(bytes); } catch (Exception) { embedded = null; }
                }
                return embedded;
            }
        }

        /// <summary>The weights compiled into the app: none in the pilot (AW2a); the shipped model's generated data (BeatModelData.cs) replaces this.</summary>
        static byte[] EmbeddedBytes() => null;

        /// <summary>Uses this model (null: none, the DSP path) for every thread until changed (the evaluation tools).</summary>
        public static void Use(BeatModelNet net) { global = net; globalSet = true; }

        /// <summary>Uses this model (null: none) on this thread until disposed (the tests).</summary>
        public static IDisposable Scope(BeatModelNet net)
        {
            var prevSet = scopedSet; var prev = scoped;
            scoped = net; scopedSet = true;
            return new Restore(() => { scoped = prev; scopedSet = prevSet; });
        }

        sealed class Restore : IDisposable
        {
            readonly Action a; public Restore(Action a) { this.a = a; }
            public void Dispose() => a();
        }
    }

    internal static class BeatModelTracker
    {
        const double Fps = 50.0;
        const double MinBpm = 55, MaxBpm = 215, BpmRatio = 1.01;
        const double TempoWindowSeconds = 8, TempoStepSeconds = 1;
        const double JumpPenalty = 8.0, DriftPenalty = 1500;
        const double Alpha = 30, ScoreScale = 8;
        /// <summary>The median beat activation at the tracked beats below which the model is not believed.</summary>
        public const double MinConfidence = 0.30;

        /// <summary>Beat activations of the audio (and the other outputs), for the evaluation tools.</summary>
        internal sealed class Activations
        {
            public float[] Beat, Down;
            public float[][] TempoWindows;
            public int[] TempoCentres;
        }

        public static Activations Run(AudioSource src, BeatModelNet net)
        {
            var q = BeatFront.Compute(src, out int T);
            var act = new Activations { Beat = new float[T], Down = new float[T] };
            if (T < 64) return act;
            var logits = new float[2 * T];
            var feats = new float[(long)T * net.D];
            net.Forward(q, T, logits, feats);
            for (int t = 0; t < T; t++)
            {
                act.Beat[t] = Sigmoid(logits[2 * t]);
                act.Down[t] = Sigmoid(logits[2 * t + 1]);
            }
            const int Win = 1500, Stride = 750;
            var wins = new List<float[]>();
            var centres = new List<int>();
            for (int a = 0; ; a += Stride)
            {
                int b = Math.Min(T, a + Win);
                if (b - a < Math.Min(Win, 300) && a > 0) break;
                wins.Add(net.Tempo(feats, a, b));
                centres.Add((a + b) / 2);
                if (b >= T) break;
            }
            act.TempoWindows = wins.ToArray();
            act.TempoCentres = centres.ToArray();
            return act;
        }

        static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

        public static BeatTrack Track(AudioSource src, BeatModelNet net)
        {
            var act = Run(src, net);
            return Decode(act, src.SampleRate, src.Length, net.Version, net.TempoBins);
        }

        public static BeatTrack Decode(Activations act, int rate, int length, int version, int tempoBins)
        {
            int T = act.Beat.Length;
            if (T < 64) return null;
            var a = act.Beat;
            int K = (int)Math.Ceiling(Math.Log(MaxBpm / MinBpm) / Math.Log(BpmRatio)) + 1;
            var bpm = new double[K];
            for (int k = 0; k < K; k++) bpm[k] = MinBpm * Math.Pow(BpmRatio, k);
            var period = TempoCurve(act, bpm, tempoBins);
            if (period == null) return null;

            var o = new double[T];
            for (int t = 0; t < T; t++) o[t] = ScoreScale * a[t];
            int[] frames = Dp(o, period, Alpha);
            // Beats where the model hears nothing are no beats (the silence before and after the music).
            int first = 0, last = frames.Length - 1;
            while (first <= last && a[frames[first]] < 0.2f) first++;
            while (last >= first && a[frames[last]] < 0.2f) last--;
            int n = last - first + 1;
            if (n < 4) return null;
            var beats = new double[n];
            var down = new double[n];
            var conf = new double[n];
            double unit = Fps / 1.0;
            for (int i = 0; i < n; i++)
            {
                int t = frames[first + i];
                double d = 0;
                if (t > 0 && t < T - 1)
                {
                    double l = a[t - 1], c = a[t], r = a[t + 1];
                    double den = l - 2 * c + r;
                    if (den < -1e-6) d = Math.Clamp(0.5 * (l - r) / den, -0.5, 0.5);
                }
                beats[i] = (t + d) / unit * rate;
                double dm = 0;
                for (int u = Math.Max(0, t - 1); u <= Math.Min(T - 1, t + 1); u++) dm = Math.Max(dm, act.Down[u]);
                down[i] = dm;
                conf[i] = a[t];
            }
            var sorted = (double[])conf.Clone();
            Array.Sort(sorted);
            if (sorted[n / 2] < MinConfidence) return null;
            var track = new BeatTrack { Rate = rate, Length = length, Beats = beats, Down = down, FromModel = true, ModelVersion = version, Low = (double[])conf.Clone(), Full = (double[])conf.Clone(), Change = new double[n] };
            var intervals = new List<double>(n);
            for (int i = 1; i < n; i++) intervals.Add(beats[i] - beats[i - 1]);
            intervals.Sort();
            double med = intervals[intervals.Count / 2];
            track.MedianBpm = med > 0 ? 60.0 * rate / med : 0;
            return track;
        }

        // ------------------------------------------------------------------ the tempo

        /// <summary>The beat period in frames at every frame (from the activation's autocorrelation, the model's tempo distribution and a Viterbi path), or null for no pulse.</summary>
        static double[] TempoCurve(Activations act, double[] bpm, int tempoBins)
        {
            var env = act.Beat;
            int n = env.Length, K = bpm.Length;
            int W = (int)Math.Round(TempoWindowSeconds * Fps);
            int step = (int)Math.Round(TempoStepSeconds * Fps);
            int windows = Math.Max(1, (n + step - 1) / step);
            var emit = new double[windows][];
            int maxLag = (int)Math.Ceiling(60.0 / bpm[0] * Fps * 2) + 2;
            var r = new double[maxLag + 2];
            bool any = false;
            for (int w = 0; w < windows; w++)
            {
                var e = new double[K];
                emit[w] = e;
                int c = Math.Min(n - 1, w * step + step / 2);
                int lo = Math.Max(0, c - W / 2), hi = Math.Min(n, c + W / 2);
                if (hi - lo < 0.5 * W) continue;
                double mean = 0;
                for (int i = lo; i < hi; i++) mean += env[i];
                mean /= hi - lo;
                double zero = 0;
                for (int i = lo; i < hi; i++) zero += (env[i] - mean) * (env[i] - mean);
                if (zero <= 1e-6) continue;
                for (int l = 0; l <= maxLag + 1; l++)
                {
                    double s = 0;
                    for (int i = lo; i + l < hi; i++) s += (env[i] - mean) * (env[i + l] - mean);
                    r[l] = s / zero * (hi - lo) / Math.Max(1.0, hi - lo - l);
                }
                double[] model = ModelAt(act, c, tempoBins, bpm);
                double best = 0;
                for (int k = 0; k < K; k++)
                {
                    double lag = 60.0 / bpm[k] * Fps;
                    double s = 0, ws = 0;
                    for (int m = 1; m <= 2; m++)
                    {
                        double l = lag * m;
                        if (l >= maxLag) break;
                        int li = (int)l; double f = l - li;
                        double wgt = m == 1 ? 1.0 : 0.5;
                        s += wgt * (r[li] * (1 - f) + r[li + 1] * f);
                        ws += wgt;
                    }
                    s = ws > 0 ? s / ws : 0;
                    // A tempo whose half-period is itself a strong period is the slower octave of a faster pulse: the faster one is the beat.
                    double hl = lag * 0.5;
                    if (hl >= 3) { int hi2 = (int)hl; double hf = hl - hi2; s -= 0.35 * Math.Max(0, r[hi2] * (1 - hf) + r[hi2 + 1] * hf); }
                    e[k] = Math.Max(0, s) * (0.2 + 0.8 * model[k]);
                    if (e[k] > best) best = e[k];
                }
                if (best <= 0) { Array.Clear(e); continue; }
                double conf = Math.Min(1.0, best / 0.12);
                for (int k = 0; k < K; k++) { double qv = e[k] / best; e[k] = conf * qv * qv * qv; }
                if (conf > 0.3) any = true;
            }
            if (!any) return null;
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
                    double best = double.NegativeInfinity; int arg = k;
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
            for (int w = windows - 1; w >= 0; w--) { path[w] = state; state = back[w][state]; }
            var period = new double[n];
            for (int t = 0; t < n; t++)
            {
                double x = (t - step / 2.0) / step;
                int w0 = (int)Math.Floor(x);
                double qv = x - w0;
                int a = Math.Clamp(w0, 0, windows - 1), b = Math.Clamp(w0 + 1, 0, windows - 1);
                double la = Math.Log(60.0 / bpm[path[a]] * Fps), lb = Math.Log(60.0 / bpm[path[b]] * Fps);
                period[t] = Math.Exp(la + (lb - la) * Math.Clamp(qv, 0, 1));
            }
            return period;
        }

        /// <summary>The model's tempo distribution near frame c, on the grid of the tempo states, scaled to a peak of 1.</summary>
        static double[] ModelAt(Activations act, int c, int tempoBins, double[] bpm)
        {
            int K = bpm.Length;
            var res = new double[K];
            if (act.TempoWindows == null || act.TempoWindows.Length == 0) { Array.Fill(res, 1.0); return res; }
            int best = 0;
            for (int i = 1; i < act.TempoCentres.Length; i++) if (Math.Abs(act.TempoCentres[i] - c) < Math.Abs(act.TempoCentres[best] - c)) best = i;
            var p = act.TempoWindows[best];
            double max = 0;
            for (int k = 0; k < K; k++)
            {
                double pos = Math.Log(bpm[k] / MinBpm) / Math.Log(MaxBpm / MinBpm) * (tempoBins - 1);
                int i0 = Math.Clamp((int)Math.Floor(pos), 0, tempoBins - 1), i1 = Math.Min(tempoBins - 1, i0 + 1);
                double f = Math.Clamp(pos - i0, 0, 1);
                res[k] = p[i0] * (1 - f) + p[i1] * f;
                if (res[k] > max) max = res[k];
            }
            if (max <= 0) { Array.Fill(res, 1.0); return res; }
            for (int k = 0; k < K; k++) res[k] /= max;
            return res;
        }

        // ------------------------------------------------------------------ the beats

        /// <summary>Ellis's dynamic programme: the frames of the beats, given the period at each frame.</summary>
        static int[] Dp(double[] o, double[] period, double alpha)
        {
            int n = o.Length;
            var C = new double[n];
            var P = new int[n];
            double maxPeriod = 0;
            foreach (var v in period) if (v > maxPeriod) maxPeriod = v;
            int maxP = (int)Math.Ceiling(1.7 * maxPeriod) + 2;
            var ln = new double[maxP + 1];
            for (int p = 1; p <= maxP; p++) ln[p] = Math.Log(p);
            for (int t = 0; t < n; t++)
            {
                double tau = period[t];
                double lt = Math.Log(tau);
                int lo = Math.Max(1, (int)Math.Ceiling(0.55 * tau)), hi = (int)Math.Floor(1.7 * tau);
                double best = double.NegativeInfinity; int arg = -1;
                for (int p = lo; p <= hi && p <= t; p++)
                {
                    double d = ln[p] - lt;
                    double v = C[t - p] - alpha * d * d;
                    if (v > best) { best = v; arg = t - p; }
                }
                if (arg >= 0 && best > 0) { C[t] = o[t] + best; P[t] = arg; }
                else { C[t] = o[t]; P[t] = -1; }
            }
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
    }
}
