using System.Globalization;
using NeoBuzz.Dsp;

namespace NeoBuzz.PitchEval
{
    internal sealed class Config
    {
        public string Name;
        public PitchRange Range;
        public PitchQuality Quality;
        public Config(string name, PitchRange r, PitchQuality q) { Name = name; Range = r; Quality = q; }
        public static readonly Config[] All =
        {
            new("Voice, Quality", PitchRange.Voice, PitchQuality.Quality),
            new("Voice, Low latency", PitchRange.Voice, PitchQuality.LowLatency),
            new("Guitar", PitchRange.Guitar, PitchQuality.Quality),
            new("Bass", PitchRange.Bass, PitchQuality.Quality),
            new("Full", PitchRange.Full, PitchQuality.Quality),
        };
    }

    /// <summary>A tracker's output over a whole file, time-aligned to the middle of the audio each estimate analysed.</summary>
    internal sealed class Track
    {
        /// <summary>The audio's middle time of each estimate (not monotonic: it moves with the period), and the time the estimate was made (monotonic).</summary>
        public double[] T, End;
        int[] order;
        double[] sortedT;
        public float[] Raw, F0, Rms, Conf;
        public bool[] Voiced;
        public int Count;
        public PitchTracker Tracker;

        public static Track Run(float[] x, int rate, PitchRange range, PitchQuality q, int block = 256)
        {
            var tr = new PitchTracker(rate, range, q);
            int cap = x.Length / tr.Hop + 4;
            var t = new Track { T = new double[cap], End = new double[cap], Raw = new float[cap], F0 = new float[cap], Rms = new float[cap], Conf = new float[cap], Voiced = new bool[cap], Tracker = tr };
            var buf = new PitchEstimate[tr.MaxFrames(block)];
            for (int pos = 0; pos < x.Length; pos += block)
            {
                int n = Math.Min(block, x.Length - pos);
                int got = tr.Process(x, pos, n, buf, 0);
                for (int i = 0; i < got; i++)
                {
                    var e = buf[i];
                    double period = e.RawF0 > 0 ? rate / e.RawF0 : tr.TauMax / 2.0;
                    int k = t.Count++;
                    t.T[k] = (e.EndSample - (tr.Window + period) / 2) / rate;
                    t.End[k] = e.EndSample / (double)rate;
                    t.Raw[k] = (float)e.RawF0; t.F0[k] = (float)e.F0; t.Rms[k] = e.Rms; t.Conf[k] = e.Confidence; t.Voiced[k] = e.Voiced;
                }
            }
            t.order = Enumerable.Range(0, t.Count).OrderBy(i => t.T[i]).ToArray();
            t.sortedT = t.order.Select(i => t.T[i]).ToArray();
            return t;
        }

        /// <summary>The estimate whose middle time is nearest <paramref name="time"/> (−1 for none).</summary>
        public int Nearest(double time)
        {
            if (Count == 0) return -1;
            int lo = Array.BinarySearch(sortedT, time);
            if (lo < 0) lo = ~lo;
            int best = Math.Min(lo, Count - 1);
            if (lo > 0 && (best == Count - 1 && lo >= Count || Math.Abs(sortedT[lo - 1] - time) <= Math.Abs(sortedT[best] - time))) best = lo - 1;
            return order[best];
        }
    }

    internal static class Fmt
    {
        public static string P(double v) => (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
        public static string F(double v, string f = "F1") => v.ToString(f, CultureInfo.InvariantCulture);
        public static double Median(List<double> v) { if (v.Count == 0) return double.NaN; var a = v.OrderBy(x => x).ToList(); return a[a.Count / 2]; }
        public static double Pct(List<double> v, double p) { if (v.Count == 0) return double.NaN; var a = v.OrderBy(x => x).ToList(); return a[Math.Min(a.Count - 1, (int)(a.Count * p))]; }
        public static double Mean(IEnumerable<double> v) { var l = v.ToList(); return l.Count == 0 ? double.NaN : l.Average(); }
    }
}
