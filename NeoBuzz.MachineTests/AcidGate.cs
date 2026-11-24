using NeoBuzz.Dsp;
using static NeoBuzz.MachineTests.AcidLines;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The AC3 click gate (revised): it has no oscillator-edge exclusion, no isolation rule and no absolute limit. A bright saw is allowed to be bright;
    /// what is not allowed is a note boundary that is spikier than the notes around it:
    ///   1. at every note start and every note end, the largest |second difference| of the raw signal (x[n+1] - 2 x[n] + x[n-1], full scale = 1) within ±5 ms
    ///      is at most <see cref="MaxRatio"/> times the larger interior peak of the two notes around it (the same measure from 10 ms after the previous
    ///      boundary to 8 ms before this one, and from 10 ms after this one to 8 ms before the next; never under <see cref="Floor"/>);
    ///   2. the broadband check: the energy above 6 kHz in the first 10 ms of a note (when it is louder than the floor) at most <see cref="BroadbandDb"/> over the
    ///      same band in the note's 30 to 80 ms window.
    /// A step is the time from one note start to the next; its row holds the worst boundary in it (its own start and any note end).
    /// </summary>
    internal static class AcidGate
    {
        public const double MaxRatio = 2.0, Floor = 1e-4, BroadbandDb = 6, BroadbandFloorDb = -90, BrightFloorDb = -75;
        const int Rate = Measure.Rate;

        public sealed class Row
        {
            public int Step;
            /// <summary>The step's largest |2nd difference|, the largest within ±5 ms of its boundaries, the interior peak those were compared with, and the worst ratio.</summary>
            public double Max2d, Near2d, Interior, Ratio;
            public double BroadbandDb, BroadbandLevelDb;
            public bool Dark;
            public bool BadRatio => Ratio > MaxRatio;
            /// <summary>Over the window by more than 6 dB and above the floor. A dark bass (a preset whose filter sits under about 200 Hz: Sub, Deep) has nothing up there to mask a
            /// burst above 6 kHz: the floor is -90 dBFS (pack 2's Sub clicks were at -80 to -89). A bright preset's own upper mids mask the same burst up to -75 dBFS: its filter
            /// opens by design, and a note that opens is brighter at its start than 50 ms later.</summary>
            public bool BadBroadband => BroadbandDb > AcidGate.BroadbandDb && BroadbandLevelDb > (Dark ? BroadbandFloorDb : BrightFloorDb);
            public bool Bad => BadRatio || BadBroadband;
        }

        /// <summary>Note starts and note ends (samples) from an event list.</summary>
        public static (List<int> Ons, List<int> Offs) Boundaries(List<Ev> events) =>
            (events.Where(e => e.Note >= 0).Select(e => e.At).OrderBy(x => x).ToList(), events.Where(e => e.Note < 0).Select(e => e.At).OrderBy(x => x).ToList());

        public static float[] HighPass6k(float[] x) => HighPass(x, 6000);

        public static float[] HighPass(float[] x, double hz)
        {
            var a = new Biquad(); var b = new Biquad();
            a.Set(BiquadType.HighPass, hz, Rate, 0.5411961);      // a 4th-order Butterworth
            b.Set(BiquadType.HighPass, hz, Rate, 1.3065630);
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
        public static List<Row> Analyse(float[] x, List<int> ons, List<int> offs, bool dark = false)
        {
            var d = new float[x.Length];
            for (int i = 1; i < x.Length - 1; i++) d[i] = Math.Abs(x[i + 1] - 2 * x[i] + x[i - 1]);
            var hp = HighPass6k(x);
            int w = (int)(0.005 * Rate), lead = (int)(0.010 * Rate), tail = (int)(0.008 * Rate), span = (int)(0.055 * Rate);
            double Peak(int from, int to)
            {
                double m = 0;
                for (int i = Math.Max(1, from); i < Math.Min(x.Length - 1, to); i++) m = Math.Max(m, d[i]);
                return m;
            }
            (double Near, double Interior, double Ratio) Boundary(int at)
            {
                // The interior on each side is 45 ms long (10 to 55 ms after the boundary, 55 to 8 ms before it): longer than one period of the lowest bass note
                // (27.5 Hz: 36 ms), so the saw's own edge, which comes once per period, cannot hide from the window.
                double interior = Math.Max(Floor, Math.Max(Peak(at + lead, at + span), Peak(at - span, at - tail)));
                double near = Peak(at - w, at + w);
                return (near, interior, near / interior);
            }
            var rows = new List<Row>();
            for (int k = 0; k < ons.Count; k++)
            {
                int a = ons[k], b = k + 1 < ons.Count ? ons[k + 1] : x.Length;
                var r = new Row { Step = k + 1, Dark = dark };
                r.Max2d = Peak(a, Math.Min(b, x.Length));
                void Near(int at)
                {
                    var t = Boundary(at);
                    r.Near2d = Math.Max(r.Near2d, t.Near);
                    if (t.Ratio > r.Ratio) { r.Ratio = t.Ratio; r.Interior = t.Interior; }
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

        public static List<Row> Analyse(float[] x, List<Ev> events, bool dark = false)
        {
            var (ons, offs) = Boundaries(events);
            return Analyse(x, ons, offs, dark);
        }

        /// <summary>The presets whose filter sits under 200 Hz and keeps almost nothing above 1.5 kHz: the strict floor applies.</summary>
        public static bool IsDark(string preset) => preset.EndsWith("Sub") || preset.EndsWith("Deep");

        public static string Flags(List<Row> rows) =>
            string.Join(" ", rows.Where(r => r.Bad).Select(r => r.Step + (r.BadRatio ? "r" : "") + (r.BadBroadband ? "b" : "")));

        public static double WorstRatio(List<Row> rows) => rows.Count == 0 ? 0 : rows.Max(r => r.Ratio);

        public static double Worst2d(List<Row> rows) => rows.Count == 0 ? 0 : rows.Max(r => r.Max2d);

        /// <summary>The table as text: one line per step.</summary>
        public static string Table(List<Row> rows) =>
            string.Join("\n", rows.Select(r => $"{r.Step,3}  max2d {r.Max2d:F4}  near {r.Near2d:F4} vs {r.Interior:F4} = {r.Ratio,5:F1}x  hf10/hf30-80 {r.BroadbandDb,6:F1} dB at {r.BroadbandLevelDb,6:F1} dBFS{(r.Bad ? "  <--" : "")}"));
    }
}
