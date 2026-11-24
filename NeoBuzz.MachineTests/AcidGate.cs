using NeoBuzz.Dsp;
using static NeoBuzz.MachineTests.AcidLines;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The AC3 click gate. It has no oscillator-edge exclusion and no isolation rule (AC2's detector had both, and found 0 clicks where the
    /// listener heard many). What it measures is what an ear catches in a dry mono bass line:
    ///   1. the largest |second difference| of the raw signal (x[n+1] - 2 x[n] + x[n-1], full scale = 1) in each step: at most <see cref="MaxEverywhere"/>;
    ///   2. the same within ±5 ms of every note start and every note end: at most <see cref="MaxAtBoundary"/>;
    ///   3. the broadband check: the energy above 6 kHz in the first 10 ms of a note (when it is louder than <see cref="BroadbandFloorDb"/>)
    ///      at most <see cref="BroadbandDb"/> over the same band in the note's 30 to 80 ms window.
    /// A step is the time from one note start to the next. The second difference is a high-pass whose gain grows with the square of the
    /// frequency, so a click on a dark bass sound (where nothing else is up there) is large in it, and so is a saw edge through a wide-open filter.
    /// </summary>
    internal static class AcidGate
    {
        public const double MaxEverywhere = 0.010, MaxAtBoundary = 0.004, BroadbandDb = 6, BroadbandFloorDb = -90;
        const int Rate = Measure.Rate;

        public sealed class Row
        {
            public int Step;
            public double Max2d, Near2d, BroadbandDb, BroadbandLevelDb;
            public bool Bad2d => Max2d > MaxEverywhere;
            public bool BadNear => Near2d > MaxAtBoundary;
            public bool BadBroadband => BroadbandDb > AcidGate.BroadbandDb && BroadbandLevelDb > BroadbandFloorDb;
            public bool Bad => Bad2d || BadNear || BadBroadband;
        }

        /// <summary>Note starts and note ends (samples) from an event list.</summary>
        public static (List<int> Ons, List<int> Offs) Boundaries(List<Ev> events) =>
            (events.Where(e => e.Note >= 0).Select(e => e.At).OrderBy(x => x).ToList(), events.Where(e => e.Note < 0).Select(e => e.At).OrderBy(x => x).ToList());

        public static float[] HighPass6k(float[] x)
        {
            var a = new Biquad(); var b = new Biquad();
            a.Set(BiquadType.HighPass, 6000, Rate, 0.5411961);      // a 4th-order Butterworth
            b.Set(BiquadType.HighPass, 6000, Rate, 1.3065630);
            var y = new float[x.Length];
            for (int i = 0; i < x.Length; i++) y[i] = b.Process(a.Process(x[i]));
            return y;
        }

        static double Power(float[] h, int from, int to)
        {
            from = Math.Max(0, from); to = Math.Min(h.Length, to);
            if (to <= from) return 1e-14;
            double s = 0;
            for (int i = from; i < to; i++) s += (double)h[i] * h[i];
            return s / (to - from) + 1e-14;
        }

        /// <summary>The per-step table for a mono render and the note starts and ends it was played with.</summary>
        public static List<Row> Analyse(float[] x, List<int> ons, List<int> offs)
        {
            var d = new float[x.Length];
            for (int i = 1; i < x.Length - 1; i++) d[i] = Math.Abs(x[i + 1] - 2 * x[i] + x[i - 1]);
            var hp = HighPass6k(x);
            int w = (int)(0.005 * Rate);
            var rows = new List<Row>();
            for (int k = 0; k < ons.Count; k++)
            {
                int a = ons[k], b = k + 1 < ons.Count ? ons[k + 1] : x.Length;
                var r = new Row { Step = k + 1 };
                for (int i = a; i < Math.Min(b, x.Length); i++) r.Max2d = Math.Max(r.Max2d, d[i]);
                void Near(int at)
                {
                    for (int i = Math.Max(1, at - w); i < Math.Min(x.Length - 1, at + w); i++) r.Near2d = Math.Max(r.Near2d, d[i]);
                }
                Near(ons[k]);
                foreach (int off in offs) if (off >= a && off < b) Near(off);
                if (a + 3840 <= x.Length)
                {
                    double e1 = Power(hp, a, a + 480), e2 = Power(hp, a + 1440, a + 3840);
                    r.BroadbandDb = 10 * Math.Log10(e1 / e2);
                    r.BroadbandLevelDb = 10 * Math.Log10(e1);
                }
                else r.BroadbandDb = -999;
                rows.Add(r);
            }
            return rows;
        }

        public static List<Row> Analyse(float[] x, List<Ev> events)
        {
            var (ons, offs) = Boundaries(events);
            return Analyse(x, ons, offs);
        }

        public static string Flags(List<Row> rows) =>
            string.Join(" ", rows.Where(r => r.Bad).Select(r => r.Step + (r.Bad2d ? "a" : "") + (r.BadNear ? "n" : "") + (r.BadBroadband ? "b" : "")));

        public static double Worst2d(List<Row> rows) => rows.Count == 0 ? 0 : rows.Max(r => r.Max2d);

        /// <summary>The table as text: one line per step.</summary>
        public static string Table(List<Row> rows) =>
            string.Join("\n", rows.Select(r => $"{r.Step,3}  max2d {r.Max2d:F4}  near {r.Near2d:F4}  hf10/hf30-80 {r.BroadbandDb,6:F1} dB at {r.BroadbandLevelDb,6:F1} dBFS{(r.Bad ? "  <--" : "")}"));
    }
}
