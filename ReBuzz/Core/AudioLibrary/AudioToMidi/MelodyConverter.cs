#nullable disable
// Fork (docs/specs/pitch.md section 9.1): a monophonic melody from audio, offline. The shared pitch tracker (PT1, Quality) runs over the
// whole file; its curve is median-filtered, short dropouts between voiced frames that agree are filled, and the voiced runs are cut into
// notes: a new semitone once the pitch has left the old one by more than the hysteresis for long enough (vibrato under it stays one
// note), a re-articulation of one pitch where the level falls and rises again. Short notes go; velocity follows the level; a slide can
// become pitch bends. Times are seconds in the audio. Allocation is not a concern here (it runs on a worker, once).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NeoBuzz.Dsp;

namespace ReBuzz.Core.AudioLibrary.AudioToMidi
{
    internal static class MelodyConverter
    {
        /// <summary>The tracker's frame series for a whole recording.</summary>
        internal sealed class Frames
        {
            public int Count;
            public double[] Time;           // seconds, the middle of the audio each estimate analysed
            public double[] End;            // seconds, the newest sample the estimate heard
            public double[] Midi;           // fractional MIDI pitch, NaN when not voiced
            public double[] Db;             // level of the newest window, dBFS
            public double WindowSeconds;
            public double Hop;              // seconds
        }

        public static PitchRange RangeOf(string name) => (name ?? "full").Trim().ToLowerInvariant() switch
        {
            "voice" or "vocal" or "vocals" or "singing" => PitchRange.Voice,
            "guitar" => PitchRange.Guitar,
            "bass" => PitchRange.Bass,
            _ => PitchRange.Full,
        };

        /// <summary>Runs the tracker over the audio.</summary>
        public static Frames Track(float[] x, int rate, PitchRange range, Action<double> progress, CancellationToken cancel)
        {
            var tr = new PitchTracker(rate, range, PitchQuality.Quality);
            int block = 4096;
            var buf = new PitchEstimate[tr.MaxFrames(block)];
            int cap = x.Length / tr.Hop + 4;
            var f = new Frames
            {
                Time = new double[cap], End = new double[cap], Midi = new double[cap], Db = new double[cap],
                WindowSeconds = tr.Window / (double)rate, Hop = tr.Hop / (double)rate,
            };
            double lastT = double.NegativeInfinity;
            for (int pos = 0; pos < x.Length; pos += block)
            {
                cancel.ThrowIfCancellationRequested();
                int n = Math.Min(block, x.Length - pos);
                int got = tr.Process(x, pos, n, buf, 0);
                for (int i = 0; i < got; i++)
                {
                    var e = buf[i];
                    double period = e.RawF0 > 0 ? rate / e.RawF0 : tr.TauMax / 2.0;
                    int k = f.Count++;
                    double t = (e.EndSample - (tr.Window + period) / 2) / rate;
                    if (t <= lastT) t = lastT + 1e-6;                                  // the middle moves with the period: keep the series in order
                    lastT = t;
                    f.Time[k] = t;
                    f.End[k] = e.EndSample / (double)rate;
                    f.Midi[k] = e.Voiced ? e.Midi : double.NaN;
                    f.Db[k] = e.Rms > 1e-7f ? 20 * Math.Log10(e.Rms) : -140;
                }
                progress?.Invoke(Math.Min(1.0, (pos + n) / (double)Math.Max(1, x.Length)) * 0.9);
            }
            return f;
        }

        public static List<ConvertedNote> Convert(float[] x, int rate, ConvertOptions o, Action<double> progress, CancellationToken cancel)
        {
            if (rate > 48000)
            {
                x = Resampler.Resample(x, rate, 48000);
                rate = 48000;
            }
            var frames = Track(x, rate, RangeOf(o.Range), progress, cancel);
            var notes = Notes(frames, o);
            progress?.Invoke(1);
            return notes;
        }

        // ------------------------------------------------------------------ frames to notes

        public static List<ConvertedNote> Notes(Frames f, ConvertOptions o)
        {
            int n = f.Count;
            var notes = new List<ConvertedNote>();
            if (n < 4) return notes;
            double hop = f.Hop;
            var m = (double[])f.Midi.Clone();

            // 1. A median filter over the voiced frames: octave glitches and single wild frames go.
            int half = Math.Max(0, (int)Math.Round(o.SmoothMs / 1000.0 / hop / 2));
            if (half > 0)
            {
                var sm = (double[])m.Clone();
                var win = new List<double>(2 * half + 1);
                for (int k = 0; k < n; k++)
                {
                    if (double.IsNaN(m[k])) continue;
                    win.Clear();
                    for (int j = Math.Max(0, k - half); j <= Math.Min(n - 1, k + half); j++)
                        if (!double.IsNaN(m[j])) win.Add(m[j]);
                    win.Sort();
                    sm[k] = win[win.Count / 2];
                }
                m = sm;
            }

            // 2. Dropouts shorter than 35 ms between frames within a semitone are the same note: filled.
            int gap = Math.Max(1, (int)Math.Round(0.035 / hop));
            var db = (double[])f.Db.Clone();
            for (int k = 1; k < n; k++)
            {
                if (!double.IsNaN(m[k]) || double.IsNaN(m[k - 1])) continue;
                int j = k;
                while (j < n && double.IsNaN(m[j])) j++;
                if (j >= n || j - k > gap) { k = j; continue; }
                if (Math.Abs(m[j] - m[k - 1]) < 1.0)
                    for (int q = k; q < j; q++)
                        m[q] = m[k - 1] + (m[j] - m[k - 1]) * (q - k + 1) / (j - k + 1);
                k = j;
            }

            // 3. The voiced runs.
            int minStable = Math.Max(3, (int)Math.Round(0.04 / hop));
            double tol = o.PitchBends ? Math.Max(o.Hysteresis, 1.6) : o.Hysteresis;
            var segs = new List<Seg>();
            for (int a = 0; a < n; a++)
            {
                if (double.IsNaN(m[a])) continue;
                int b = a;
                while (b + 1 < n && !double.IsNaN(m[b + 1])) b++;
                SplitRun(m, db, a, b, hop, tol, minStable, o.PitchBends, segs);
                a = b;
            }

            // 4. Notes: timing, length, velocity, bends.
            double w = f.WindowSeconds;
            var levels = new List<double>();
            foreach (var s in segs)
            {
                double start, end;
                bool runStart = s.RunStart;
                double periodSec = 1.0 / Math.Max(30.0, Hz(m, s.A));
                if (runStart) start = f.End[s.A] - (0.006 + 2 * Math.Min(periodSec, 0.02));
                else start = f.Time[s.A];
                end = s.RunEnd ? f.End[s.B] - w + 0.010 : f.Time[s.B] + hop / 2;
                if (end - start < o.MinNoteMs / 1000.0) continue;
                var note = new ConvertedNote { Start = Math.Max(0, start), End = end, Pitch = s.Pitch };
                double peak = double.NegativeInfinity;
                int lim = Math.Min(s.B, s.A + Math.Max(3, (int)Math.Round(0.08 / hop)));
                for (int k = s.A; k <= lim; k++) peak = Math.Max(peak, db[k]);
                note.Velocity = 0;
                levels.Add(peak);
                s.Level = peak;
                s.Note = note;
                if (o.PitchBends) note.Bends = Bends(m, f, s, hop);
                notes.Add(note);
            }
            if (notes.Count > 0)
            {
                var sorted = levels.OrderBy(v => v).ToList();
                double reference = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))];
                foreach (var s in segs)
                    if (s.Note != null) s.Note.Velocity = VelocityOf(s.Level, reference);
            }
            // Never overlapping: a note ends where the next begins.
            notes.Sort((p, q) => p.Start.CompareTo(q.Start));
            for (int i = 0; i + 1 < notes.Count; i++)
                if (notes[i].End > notes[i + 1].Start) notes[i].End = Math.Max(notes[i].Start + 0.01, notes[i + 1].Start);
            return notes;
        }

        /// <summary>Velocity from a level: 127 at <paramref name="reference"/> (dBFS), 30 at 30 dB under, never below 20.</summary>
        public static int VelocityOf(double level, double reference) =>
            (int)Math.Clamp(Math.Round(30 + 97 * Math.Clamp((level - (reference - 30)) / 30, 0, 1)), 20, 127);

        static double Hz(double[] m, int k) => 440.0 * Math.Pow(2, (m[k] - 69) / 12.0);

        sealed class Seg
        {
            public int A, B, Pitch;
            public bool RunStart, RunEnd;
            public double Level;
            public ConvertedNote Note;
        }

        static void SplitRun(double[] m, double[] db, int a, int b, double hop, double tol, int minStable, bool bends, List<Seg> into)
        {
            int len = b - a + 1;
            // Quantised with hysteresis: the semitone changes only when the pitch is more than tol from it.
            var q = new int[len];
            double first = Median(m, a, Math.Min(b, a + 4));
            int s = (int)Math.Round(first);
            for (int k = 0; k < len; k++)
            {
                if (Math.Abs(m[a + k] - s) > tol) s = (int)Math.Round(m[a + k]);
                q[k] = s;
            }
            // Runs of one semitone.
            var runs = new List<(int A, int B, int Q)>();
            for (int k = 0; k < len; k++)
            {
                if (runs.Count > 0 && runs[^1].Q == q[k]) runs[^1] = (runs[^1].A, k, q[k]);
                else runs.Add((k, k, q[k]));
            }
            // Short runs (a glide through, a wobble) join the neighbour that is nearer in pitch; then equal neighbours join.
            bool changed = true;
            while (changed && runs.Count > 1)
            {
                changed = false;
                int shortest = -1;
                for (int i = 0; i < runs.Count; i++)
                    if (runs[i].B - runs[i].A + 1 < minStable && (shortest < 0 || runs[i].B - runs[i].A < runs[shortest].B - runs[shortest].A)) shortest = i;
                if (shortest < 0) break;
                var r = runs[shortest];
                double med = Median(m, a + r.A, a + r.B);
                int target;
                if (shortest == 0) target = 1;
                else if (shortest == runs.Count - 1) target = shortest - 1;
                else target = Math.Abs(runs[shortest - 1].Q - med) <= Math.Abs(runs[shortest + 1].Q - med) ? shortest - 1 : shortest + 1;
                var t = runs[target];
                runs[target] = (Math.Min(t.A, r.A), Math.Max(t.B, r.B), t.Q);
                runs.RemoveAt(shortest);
                for (int i = runs.Count - 2; i >= 0; i--)
                    if (runs[i].Q == runs[i + 1].Q) { runs[i] = (runs[i].A, runs[i + 1].B, runs[i].Q); runs.RemoveAt(i + 1); }
                changed = true;
            }
            // A re-articulation inside a run: the level fell by 6 dB under its peak and came back up by 6 dB.
            var cut = new List<(int A, int B)>();
            foreach (var r in runs)
            {
                int from = r.A;
                double peak = db[a + r.A], valley = peak;
                int valleyAt = r.A;
                for (int k = r.A + 1; k <= r.B; k++)
                {
                    double d = (db[a + k - 1] + db[a + k] + db[a + Math.Min(r.B, k + 1)]) / 3;
                    if (d > peak) { peak = d; valley = d; valleyAt = k; continue; }
                    if (d < valley) { valley = d; valleyAt = k; }
                    if (d - valley >= 6 && peak - valley >= 6 && valleyAt - from >= minStable && r.B - valleyAt >= minStable)
                    {
                        cut.Add((from, valleyAt - 1));
                        from = valleyAt;
                        peak = d; valley = d; valleyAt = k;
                    }
                }
                cut.Add((from, r.B));
            }
            for (int i = 0; i < cut.Count; i++)
            {
                var c = cut[i];
                int pitchFrames = Math.Max(5, (c.B - c.A + 1) / 3);
                double pm = bends
                    ? Median(m, a + Math.Min(c.B, c.A + 2), Math.Min(a + c.B, a + c.A + 2 + pitchFrames))
                    : Median(m, a + c.A, a + c.B);
                into.Add(new Seg { A = a + c.A, B = a + c.B, Pitch = (int)Math.Clamp(Math.Round(pm), 0, 127), RunStart = i == 0, RunEnd = i == cut.Count - 1 });
            }
        }

        static double Median(double[] m, int a, int b)
        {
            var v = new List<double>(b - a + 1);
            for (int k = a; k <= b; k++) if (!double.IsNaN(m[k])) v.Add(m[k]);
            if (v.Count == 0) return 0;
            v.Sort();
            return v[v.Count / 2];
        }

        /// <summary>The note's pitch against its semitone, every 10 ms or more apart, each when it moved 6 cents from the last (smoothed).</summary>
        static List<(double Time, double Cents)> Bends(double[] m, Frames f, Seg s, double hop)
        {
            var list = new List<(double, double)>();
            double lastCents = 0, lastTime = double.NegativeInfinity;
            for (int k = s.A; k <= s.B; k++)
            {
                double sum = 0; int c = 0;
                for (int j = Math.Max(s.A, k - 2); j <= Math.Min(s.B, k + 2); j++) { sum += m[j]; c++; }
                double cents = Math.Clamp((sum / c - s.Pitch) * 100, -200, 200);
                double t = f.Time[k];
                if (t - lastTime < 0.010) continue;
                if (list.Count == 0 || Math.Abs(cents - lastCents) >= 6)
                {
                    list.Add((t, cents));
                    lastCents = cents; lastTime = t;
                }
            }
            // Nothing worth a lane: a note that stays in tune has no bends.
            if (list.Count == 0 || list.All(p => Math.Abs(p.Item2) < 12)) return null;
            return list;
        }
    }
}
