// Neo Tune's objective quality measures (docs/specs/neo-tune.md section 12). Written after the listening verdict on NT1 ("really low quality"): NT1's own evaluation
// measured only the pitch error and a click proxy, and missed the vibrato it cut, the target flips, the jitter and the sub-harmonics it added, the lost voicing and
// the bursts at note changes. Pure (NeoBuzz.Dsp only): linked into the pitch-eval tool and into NeoBuzz.MachineTests, so the gates and the VocalSet figures are the same code.
// The pitch is the shared tracker's (Voice range, Quality); "input" is the unprocessed signal and "output" the corrected one with the latency taken out.

using NeoBuzz.Dsp;

namespace NeoBuzz.Quality
{
    /// <summary>The tracker's estimates over a whole signal, time-aligned to the middle of the audio each one analysed (monotonic in time here).</summary>
    internal sealed class QTrack
    {
        public double[] T, Cents;     // cents are MIDI x 100 (A4 = 6900); only meaningful where Voiced
        public float[] F0, Rms;
        public bool[] Voiced;
        public int Count;
        public double Hop;

        public static QTrack Run(float[] x, int rate, PitchRange range = PitchRange.Voice, PitchQuality q = PitchQuality.Quality)
        {
            var tr = new PitchTracker(rate, range, q);
            int cap = x.Length / tr.Hop + 4;
            var t = new QTrack { T = new double[cap], Cents = new double[cap], F0 = new float[cap], Rms = new float[cap], Voiced = new bool[cap], Hop = tr.Hop / (double)rate };
            var buf = new PitchEstimate[tr.MaxFrames(256)];
            for (int pos = 0; pos < x.Length; pos += 256)
            {
                int n = Math.Min(256, x.Length - pos);
                int got = tr.Process(x, pos, n, buf, 0);
                for (int i = 0; i < got; i++)
                {
                    var e = buf[i];
                    int k = t.Count++;
                    double period = e.RawF0 > 0 ? rate / e.RawF0 : tr.TauMax / 2.0;
                    t.T[k] = (e.EndSample - (tr.Window + period) / 2) / rate;
                    t.F0[k] = (float)e.F0; t.Rms[k] = e.Rms; t.Voiced[k] = e.Voiced && e.F0 > 0;
                    t.Cents[k] = t.Voiced[k] ? (69 + 12 * Math.Log2(e.F0 / 440.0)) * 100 : 0;
                }
            }
            for (int k = 1; k < t.Count; k++) if (t.T[k] < t.T[k - 1]) t.T[k] = t.T[k - 1];
            return t;
        }

        public const float Active = 0.00316f;

        /// <summary>The estimate nearest <paramref name="time"/> (-1 for none).</summary>
        public int At(double time)
        {
            if (Count == 0) return -1;
            int lo = 0, hi = Count - 1;
            while (lo < hi) { int m = (lo + hi) / 2; if (T[m] < time) lo = m + 1; else hi = m; }
            if (lo > 0 && time - T[lo - 1] < T[lo] - time) lo--;
            return Math.Abs(T[lo] - time) < 0.02 ? lo : -1;
        }

        public bool VoicedAt(double time) { int i = At(time); return i >= 0 && Voiced[i] && Rms[i] > Active; }

        /// <summary>Voiced, active stretches of at least <paramref name="minSeconds"/> (gaps up to 40 ms allowed): index ranges.</summary>
        public List<(int From, int To)> Runs(double minSeconds)
        {
            var r = new List<(int, int)>();
            int start = -1, last = -1;
            for (int i = 0; i < Count; i++)
            {
                bool v = Voiced[i] && Rms[i] > Active;
                if (v) { if (start < 0) start = i; last = i; }
                else if (start >= 0 && T[i] - T[last] > 0.04) { if (T[last] - T[start] >= minSeconds) r.Add((start, last)); start = -1; }
            }
            if (start >= 0 && T[last] - T[start] >= minSeconds) r.Add((start, last));
            return r;
        }
    }

    internal static class TuneQuality
    {
        // ------------------------------------------------------------------ filters

        internal sealed class Biquad
        {
            double b0, b1, b2, a1, a2, z1, z2;
            public static Biquad Low(double fs, double fc, double q = 0.7071) => Make(fs, fc, q, false);
            public static Biquad High(double fs, double fc, double q = 0.7071) => Make(fs, fc, q, true);
            static Biquad Make(double fs, double fc, double q, bool high)
            {
                double w = 2 * Math.PI * fc / fs, c = Math.Cos(w), s = Math.Sin(w), al = s / (2 * q), a0 = 1 + al;
                var f = new Biquad();
                if (high) { f.b0 = (1 + c) / 2 / a0; f.b1 = -(1 + c) / a0; f.b2 = (1 + c) / 2 / a0; }
                else { f.b0 = (1 - c) / 2 / a0; f.b1 = (1 - c) / a0; f.b2 = (1 - c) / 2 / a0; }
                f.a1 = -2 * c / a0; f.a2 = (1 - al) / a0;
                return f;
            }
            public double Run(double x) { double y = b0 * x + z1; z1 = b1 * x - a1 * y + z2; z2 = b2 * x - a2 * y; return y; }
        }

        // ------------------------------------------------------------------ vibrato

        /// <summary>The 4-8 Hz extent of the pitch (cents; twice the RMS times the square root of 2 = peak to peak of a sine): the median over the voiced runs of at least 0.6 s.</summary>
        public static double VibratoExtent(QTrack t, double minRun = 0.6) => Runs(t, minRun, out _, out _);

        static double Runs(QTrack t, double minRun, out int runs, out double seconds)
        {
            var list = new List<double>();
            runs = 0; seconds = 0;
            foreach (var (a, b) in t.Runs(minRun))
            {
                const double Fs = 200;
                double t0 = t.T[a], t1 = t.T[b];
                int n = (int)((t1 - t0) * Fs);
                if (n < 80) continue;
                var hp1 = Biquad.High(Fs, 4.0); var hp2 = Biquad.High(Fs, 4.0); var lp1 = Biquad.Low(Fs, 8.0); var lp2 = Biquad.Low(Fs, 8.0);
                double acc = 0, dc = 0; int cnt = 0;
                bool first = true;
                int j = a;
                for (int i = 0; i < n; i++)
                {
                    double tt = t0 + i / Fs;
                    while (j + 1 < b && t.T[j + 1] <= tt) j++;
                    // linear interpolation between voiced neighbours
                    int k = j; while (k <= b && !t.Voiced[k]) k++;
                    int p = j; while (p >= a && !t.Voiced[p]) p--;
                    double c = p < a ? t.Cents[Math.Min(k, b)] : k > b ? t.Cents[p] : p == k ? t.Cents[p] : t.Cents[p] + (t.Cents[k] - t.Cents[p]) * Math.Clamp((tt - t.T[p]) / Math.Max(1e-9, t.T[k] - t.T[p]), 0, 1);
                    if (first) { dc = c; first = false; }
                    double y = lp2.Run(lp1.Run(hp2.Run(hp1.Run(c - dc))));
                    if (i > (int)(0.5 * Fs)) { acc += y * y; cnt++; }
                }
                if (cnt > 40) { list.Add(2 * Math.Sqrt(2) * Math.Sqrt(acc / cnt)); seconds += cnt / Fs; }
            }
            runs = list.Count;
            return list.Count == 0 ? double.NaN : Median(list);
        }

        public static double Median(List<double> v) { if (v.Count == 0) return double.NaN; var a = v.OrderBy(x => x).ToList(); return a[a.Count / 2]; }

        // ------------------------------------------------------------------ jitter and shimmer

        /// <summary>Per-cycle jitter (mean absolute difference of successive periods over the mean period, %) and shimmer (the same for the peak amplitudes), medians over the runs of at least 0.5 s.</summary>
        public static (double Jitter, double Shimmer) JitterShimmer(float[] s, int rate, QTrack t)
        {
            var js = new List<double>(); var ss = new List<double>();
            var lp = new[] { Biquad.Low(rate, 900), Biquad.Low(rate, 900) };
            var l = new float[s.Length];
            for (int i = 0; i < s.Length; i++) l[i] = (float)lp[1].Run(lp[0].Run(s[i]));
            foreach (var (a, b) in t.Runs(0.5))
            {
                var f = new List<double>();
                for (int i = a; i <= b; i++) if (t.Voiced[i]) f.Add(t.F0[i]);
                if (f.Count < 20) continue;
                double T = rate / Median(f);
                int st = (int)(t.T[a] * rate), en = Math.Min(s.Length - 1, (int)(t.T[b] * rate));
                var pk = new List<int>();
                int dist = (int)(0.7 * T);
                for (int i = st + 1; i < en - 1; i++)
                {
                    if (!(l[i] > l[i - 1] && l[i] >= l[i + 1] && l[i] > 0)) continue;
                    if (pk.Count > 0 && i - pk[^1] < dist) { if (l[i] > l[pk[^1]]) pk[^1] = i; continue; }
                    pk.Add(i);
                }
                var P = new List<double>(); var A = new List<double>();
                for (int i = 1; i < pk.Count; i++)
                {
                    double p = pk[i] - pk[i - 1];
                    if (p > 0.7 * T && p < 1.3 * T) { P.Add(p); A.Add(l[pk[i]]); }
                }
                if (P.Count < 12) continue;
                double dp = 0, da = 0;
                for (int i = 1; i < P.Count; i++) { dp += Math.Abs(P[i] - P[i - 1]); da += Math.Abs(A[i] - A[i - 1]); }
                js.Add(dp / (P.Count - 1) / P.Average() * 100);
                ss.Add(da / (A.Count - 1) / A.Average() * 100);
            }
            return (Median(js), Median(ss));
        }

        // ------------------------------------------------------------------ spectra

        const int N = 4096, Hop = 1024;

        sealed class Spectra
        {
            public readonly ComplexFft Fft = new ComplexFft(N);
            public readonly float[] Win = new float[N], Re = new float[N], Im = new float[N];
            public Spectra() { for (int i = 0; i < N; i++) Win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)); }
            public void Power(float[] s, int at, double[] p)
            {
                for (int i = 0; i < N; i++) { int k = at + i; Re[i] = k >= 0 && k < s.Length ? s[k] * Win[i] : 0f; Im[i] = 0f; }
                Fft.Transform(Re, Im, false);
                for (int k = 0; k <= N / 2; k++) p[k] = Re[k] * Re[k] + Im[k] * Im[k];
            }
        }

        public static readonly double[] BandCentres = { 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000 };

        /// <summary>The long-term average spectrum difference (output minus input, dB) per third octave over the frames where the input is voiced, and the level of the band above 5 kHz in each (dB).</summary>
        public static (double[] Diff, double HighIn, double HighOut) Ltas(float[] x, float[] y, int rate, QTrack ti)
        {
            var sp = new Spectra();
            var px = new double[N / 2 + 1]; var py = new double[N / 2 + 1];
            var ax = new double[BandCentres.Length]; var ay = new double[BandCentres.Length];
            double hx = 0, hy = 0; int frames = 0;
            int hlo = (int)(5000.0 * N / rate), hhi = Math.Min(N / 2, (int)(16000.0 * N / rate));
            for (int at = 0; at + N <= Math.Min(x.Length, y.Length); at += Hop)
            {
                if (!ti.VoicedAt((at + N / 2.0) / rate)) continue;
                sp.Power(x, at, px); sp.Power(y, at, py);
                for (int b = 0; b < BandCentres.Length; b++)
                {
                    double lo = BandCentres[b] / Math.Pow(2, 1.0 / 6), hi = BandCentres[b] * Math.Pow(2, 1.0 / 6);
                    int k0 = Math.Max(1, (int)Math.Ceiling(lo * N / rate)), k1 = Math.Min(N / 2, (int)Math.Floor(hi * N / rate));
                    for (int k = k0; k <= k1; k++) { ax[b] += px[k]; ay[b] += py[k]; }
                }
                for (int k = hlo; k < hhi; k++) { hx += px[k]; hy += py[k]; }
                frames++;
            }
            var d = new double[BandCentres.Length];
            for (int b = 0; b < d.Length; b++) d[b] = 10 * Math.Log10((ay[b] + 1e-20) / (ax[b] + 1e-20));
            return (d, 10 * Math.Log10(hx / Math.Max(1, frames) + 1e-20), 10 * Math.Log10(hy / Math.Max(1, frames) + 1e-20));
        }

        /// <summary>The energy at f0/2 and 3f0/2 (+-12 % of f0/2) against the energy at f0 (+-12 %), dB, the median over the voiced frames of the run (f0 from the track of the same signal).</summary>
        public static double Subharmonic(float[] s, int rate, QTrack t)
        {
            var sp = new Spectra();
            var p = new double[N / 2 + 1];
            var vals = new List<double>();
            for (int at = 0; at + N <= s.Length; at += Hop)
            {
                int i = t.At((at + N / 2.0) / rate);
                if (i < 0 || !t.Voiced[i] || t.Rms[i] <= QTrack.Active) continue;
                double f0 = t.F0[i];
                if (f0 < 70 || f0 > 700) continue;
                sp.Power(s, at, p);
                double E(double f, double w) { int k0 = Math.Max(1, (int)Math.Floor((f - w) * N / rate)), k1 = Math.Min(N / 2, (int)Math.Ceiling((f + w) * N / rate)); double e = 0; for (int k = k0; k <= k1; k++) e += p[k]; return e; }
                double e0 = E(f0, 0.12 * f0), es = E(f0 / 2, 0.06 * f0) + E(1.5 * f0, 0.06 * f0);
                if (e0 > 1e-12) vals.Add(10 * Math.Log10(es / e0 + 1e-12));
            }
            return Median(vals);
        }

        // ------------------------------------------------------------------ bursts at note changes

        /// <summary>The note sequence: the median of the voiced pitches of the 150 ms before each estimate (a median keeps a jump a jump and, over at least half a vibrato period, a vibrato centred) rounded to the semitone,
        /// a new note after it has been 80 cents past the old one's middle for 100 ms; and the times (s) of the changes.</summary>
        public static List<double> NoteChanges(QTrack t, out List<int> notes)
        {
            var times = new List<double>(); notes = new List<int>();
            var buf = new List<double>();
            foreach (var (a, b) in t.Runs(0.15))
            {
                int cur = int.MinValue, pend = int.MinValue; double pendT = 0;
                for (int i = a; i <= b; i++)
                {
                    if (!t.Voiced[i]) continue;
                    buf.Clear();
                    for (int k = i; k >= a && t.T[i] - t.T[k] < 0.15; k--) if (t.Voiced[k]) buf.Add(t.Cents[k]);
                    buf.Sort();
                    double c = buf[buf.Count / 2] / 100.0;
                    int near = (int)Math.Round(c);
                    if (cur == int.MinValue) { cur = near; notes.Add(cur); pend = near; continue; }
                    if (near == cur || Math.Abs(c - cur) <= 0.5 + 0.3) { pend = cur; continue; }
                    if (near != pend) { pend = near; pendT = t.T[i]; }
                    else if (t.T[i] - pendT >= 0.1) { cur = near; notes.Add(cur); times.Add(pendT); }
                }
            }
            return times;
        }

        /// <summary>The expression inside one note: twice the square root of 2 times the standard deviation (cents) of the voiced pitch between two times (the peak to peak of a sine).</summary>
        public static double NoteSpread(QTrack t, double from, double to)
        {
            var v = new List<double>();
            for (int i = 0; i < t.Count; i++) if (t.Voiced[i] && t.T[i] >= from && t.T[i] < to) v.Add(t.Cents[i]);
            if (v.Count < 10) return double.NaN;
            double m = v.Average();
            return 2 * Math.Sqrt(2) * Math.Sqrt(v.Select(q => (q - m) * (q - m)).Average());
        }

        /// <summary>Broadband bursts: hops (256 samples) in which the 1-10 kHz energy rises by 10 dB over the mean of the four hops before it, counted where the signal is active; and how many of them fall within 40 ms of a note change.</summary>
        public static (int Total, int AtChanges, double Seconds) Bursts(float[] s, int rate, QTrack t, List<double> changes)
        {
            const int M = 512, H = 256;
            var fft = new ComplexFft(M);
            var win = new float[M]; for (int i = 0; i < M; i++) win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / M));
            var re = new float[M]; var im = new float[M];
            int k0 = (int)(1000.0 * M / rate), k1 = Math.Min(M / 2, (int)(10000.0 * M / rate));
            var hist = new double[4]; int hn = 0;
            int total = 0, near = 0; double secs = 0;
            for (int at = 0; at + M <= s.Length; at += H)
            {
                double rms = 0; for (int i = 0; i < M; i++) { re[i] = s[at + i] * win[i]; im[i] = 0; rms += s[at + i] * s[at + i]; }
                rms = Math.Sqrt(rms / M);
                fft.Transform(re, im, false);
                double e = 0; for (int k = k0; k < k1; k++) e += re[k] * re[k] + im[k] * im[k];
                double db = 10 * Math.Log10(e + 1e-10);
                if (hn >= 4 && rms > QTrack.Active)
                {
                    double prev = (hist[0] + hist[1] + hist[2] + hist[3]) / 4;
                    secs += H / (double)rate;
                    if (db - prev > 10)
                    {
                        total++;
                        double tm = (at + M / 2.0) / rate;
                        if (changes.Any(c => Math.Abs(c - tm) < 0.04)) near++;
                    }
                }
                hist[hn & 3] = db; hn++;
            }
            return (total, near, secs);
        }

        // ------------------------------------------------------------------ notes

        /// <summary>Count of note changes in a sequence.</summary>
        public static int Changes(List<int> notes) => Math.Max(0, notes.Count - 1);

        /// <summary>The centre error: the median over the voiced runs of the distance (cents) of the 200 ms mean of the pitch from the nearest semitone (a hard tune on the chromatic scale leaves 0; the input as sung leaves its own).</summary>
        public static double CentreError(QTrack t)
        {
            var v = new List<double>();
            foreach (var (a, b) in t.Runs(0.3))
            {
                for (int i = a; i <= b; i += 4)
                {
                    double sum = 0; int n = 0;
                    for (int k = i; k >= a && t.T[i] - t.T[k] < 0.2; k--) if (t.Voiced[k]) { sum += t.Cents[k]; n++; }
                    if (n < 10) continue;
                    double c = sum / n;
                    v.Add(Math.Abs(c - Math.Round(c / 100.0) * 100.0));
                }
            }
            return Median(v);
        }

        /// <summary>The share of the input's voiced, active estimates that are voiced in the output.</summary>
        public static double VoicedKept(QTrack ti, QTrack to)
        {
            int n = 0, k = 0;
            for (int i = 0; i < ti.Count; i++)
            {
                if (!ti.Voiced[i] || ti.Rms[i] <= QTrack.Active) continue;
                n++;
                if (to.VoicedAt(ti.T[i])) k++;
            }
            return n == 0 ? double.NaN : k / (double)n;
        }
    }
}
