// Neo Tune measured inside the notes (docs/specs/neo-tune.md section 11.3). The lead's review of the NT2 listening pack found that whole-file numbers
// hid a hard effect that did not flatten a vibrato, and steps that came late. These measures segment a signal into notes and look inside each:
//  - Segments: runs of the same semitone (the median of 150 ms, a new note after 80 cents past the middle for 100 ms), the notes of a sung phrase or of the output;
//  - flatness: per output note, the median distance of the pitch from the note's own median (the first 30 ms after a step are skipped, notes under 100 ms ignored);
//  - step delay: for each step between two sung notes, when the output crosses the midpoint between them against when the input does;
//  - centre error: the note's median pitch from the nearest semitone, before and after;
//  - vibrato inside the notes: the 4-8 Hz extent of the pitch within each sung note, output over input.
// Pure (NeoBuzz.Dsp only), linked into the pitch-eval tool and the machine tests.

namespace NeoBuzz.Quality
{
    internal static class TuneNotes
    {
        internal readonly record struct Seg(double From, double To, int Note);

        /// <summary>The notes of a track (see the header). Segment boundaries are the times the new note began.</summary>
        public static List<Seg> Segments(QTrack t)
        {
            var segs = new List<Seg>();
            var buf = new List<double>();
            foreach (var (a, b) in t.Runs(0.15))
            {
                int cur = int.MinValue, pend = int.MinValue; double pendT = 0, start = t.T[a];
                for (int i = a; i <= b; i++)
                {
                    if (!t.Voiced[i]) continue;
                    buf.Clear();
                    for (int k = i; k >= a && t.T[i] - t.T[k] < 0.15; k--) if (t.Voiced[k]) buf.Add(t.Cents[k]);
                    buf.Sort();
                    double c = buf[buf.Count / 2] / 100.0;
                    int near = (int)Math.Round(c);
                    if (cur == int.MinValue) { cur = near; pend = near; continue; }
                    if (near == cur || Math.Abs(c - cur) <= 0.8) { pend = cur; continue; }
                    if (near != pend) { pend = near; pendT = t.T[i]; }
                    else if (t.T[i] - pendT >= 0.1) { segs.Add(new Seg(start, pendT, cur)); start = pendT; cur = near; }
                }
                if (cur != int.MinValue) segs.Add(new Seg(start, t.T[b], cur));
            }
            return segs;
        }

        /// <summary>The notes that last at least <paramref name="minSeconds"/>, a neighbour of the same note merged: the sequence a listener hears as the notes.</summary>
        public static List<int> Long(List<Seg> segs, double minSeconds = 0.25)
        {
            var l = new List<int>();
            foreach (var s in segs) if (s.To - s.From >= minSeconds && (l.Count == 0 || l[^1] != s.Note)) l.Add(s.Note);
            return l;
        }

        static List<double> Frames(QTrack t, double from, double to, double skip, List<double> times = null)
        {
            var v = new List<double>();
            for (int i = 0; i < t.Count; i++)
                if (t.Voiced[i] && t.T[i] >= from + skip && t.T[i] < to) { v.Add(t.Cents[i]); times?.Add(t.T[i]); }
            return v;
        }

        static double Med(List<double> v) { if (v.Count == 0) return double.NaN; var a = v.OrderBy(x => x).ToList(); return a[a.Count / 2]; }

        /// <summary>The flatness of the notes of a track: the median over notes of the median absolute distance (cents) of the pitch from the note's median.</summary>
        public static (double Flat, int Notes) Flatness(QTrack t, List<Seg> segs)
        {
            var per = new List<double>();
            foreach (var s in segs)
            {
                if (s.To - s.From - 0.03 < 0.1) continue;
                var f = Frames(t, s.From, s.To, 0.03);
                if (f.Count < 10) continue;
                double m = Med(f);
                per.Add(Med(f.Select(c => Math.Abs(c - m)).ToList()));
            }
            return (Med(per), per.Count);
        }

        /// <summary>The pitch (cents) of a track at a time, a 40 ms median of the voiced estimates around it (NaN if none).</summary>
        static double At(QTrack t, double time)
        {
            var v = new List<double>();
            for (int i = 0; i < t.Count; i++) if (t.Voiced[i] && Math.Abs(t.T[i] - time) <= 0.02) v.Add(t.Cents[i]);
            return v.Count < 2 ? double.NaN : Med(v);
        }

        /// <summary>For each step between two sung notes (both at least 150 ms), the time the output's pitch crosses the midpoint between the two notes minus the time the input's does (s).</summary>
        public static List<double> StepDelays(QTrack ti, QTrack to, List<Seg> sung)
        {
            var d = new List<double>();
            for (int k = 0; k + 1 < sung.Count; k++)
            {
                var a = sung[k]; var b = sung[k + 1];
                if (Math.Abs(a.To - b.From) > 0.05 || a.To - a.From < 0.15 || b.To - b.From < 0.15) continue;
                double ma = Med(Frames(ti, a.From, a.To, 0.03)), mb = Med(Frames(ti, b.From, b.To, 0.03));
                if (double.IsNaN(ma) || double.IsNaN(mb) || Math.Abs(mb - ma) < 150) continue;
                double mid = (ma + mb) / 2; bool up = mb > ma;
                double tin = Cross(ti, mid, up, a.To - 0.15, b.From + 0.4);
                if (double.IsNaN(tin)) continue;
                double tout = Cross(to, mid, up, tin - 0.15, tin + 0.6);
                if (!double.IsNaN(tout)) d.Add(tout - tin);
            }
            return d;
        }

        static double Cross(QTrack t, double mid, bool up, double from, double to)
        {
            for (double x = from; x < to; x += 0.005)
            {
                double c = At(t, x);
                if (double.IsNaN(c)) continue;
                if (up ? c >= mid : c <= mid) return x;
            }
            return double.NaN;
        }

        /// <summary>The median over the sung notes of the distance (cents) of the note's median pitch from the nearest semitone, for a track.</summary>
        public static double CentreError(QTrack t, List<Seg> sung)
        {
            var e = new List<double>();
            foreach (var s in sung)
            {
                if (s.To - s.From - 0.03 < 0.2) continue;
                double m = Med(Frames(t, s.From, s.To, 0.03));
                if (!double.IsNaN(m)) e.Add(Math.Abs(m - Math.Round(m / 100.0) * 100.0));
            }
            return Med(e);
        }

        /// <summary>The 4-8 Hz extent (cents, peak to peak of a sine) of the pitch inside each sung note of at least 0.4 s; the medians for a track and the median of output over input per note.</summary>
        public static double VibratoInside(QTrack t, List<Seg> sung, List<double> perNote = null)
        {
            var list = new List<double>();
            foreach (var s in sung)
            {
                if (s.To - s.From < 0.4) continue;
                double segMed = Med(Frames(t, s.From, s.To, 0.03));
                const double Fs = 200;
                int n = (int)((s.To - s.From) * Fs);
                var hp1 = TuneQuality.Biquad.High(Fs, 4.0); var hp2 = TuneQuality.Biquad.High(Fs, 4.0); var lp1 = TuneQuality.Biquad.Low(Fs, 8.0); var lp2 = TuneQuality.Biquad.Low(Fs, 8.0);
                double acc = 0, dc = double.NaN, last = double.NaN; int cnt = 0;
                for (int i = 0; i < n; i++)
                {
                    double c = At(t, s.From + i / Fs);
                    if (!double.IsNaN(c) && Math.Abs(c - segMed) > 500) c = double.NaN;      // a tracker error (an octave jump) is not vibrato
                    if (double.IsNaN(c)) c = last;
                    if (double.IsNaN(c)) continue;
                    last = c;
                    if (double.IsNaN(dc)) dc = c;
                    double y = lp2.Run(lp1.Run(hp2.Run(hp1.Run(c - dc))));
                    if (i > (int)(0.25 * Fs)) { acc += y * y; cnt++; }
                }
                list.Add(cnt > 20 ? 2 * Math.Sqrt(2) * Math.Sqrt(acc / cnt) : double.NaN);
            }
            perNote?.AddRange(list);
            return Med(list.Where(x => !double.IsNaN(x)).ToList());
        }
    }
}
