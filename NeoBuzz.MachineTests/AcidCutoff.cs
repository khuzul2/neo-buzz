using NeoBuzz.Dsp;
using NeoBuzz.Poly;
using static NeoBuzz.MachineTests.AcidLines;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The cutoff-path gate (AC3 round 3). An acid voice's filter cutoff, in octaves, as the engine uses it (after the rate limit and the follower), recorded once per control
    /// block for every voice. Its curvature, the second derivative in octaves per second squared taken over <see cref="SpanMs"/> (1 ms), is what a resonant filter turns into a
    /// ping: a rate limit alone has a corner where the cutoff meets the decaying envelope and turns from rising to falling (a slope step of 350 to 465 octaves a second: 460000 oct/s² at
    /// 1 ms), a critically damped follower has none. The curvature is measured inside one note of one voice only: where a voice starts a new note (its age restarts) the cutoff is
    /// allowed to jump, the voice has just faded to silence.
    /// </summary>
    internal static class AcidCutoff
    {
        /// <summary>The bound, in octaves per second squared at 1 ms. The corner build measured 407000 to 527000 on the three clicky presets; the follower's worst is a third of this.</summary>
        public const double MaxCurvature = 150000;
        public const double SpanMs = 1.0;
        const int Rate = Measure.Rate;

        public sealed record Row(int Step, double Curvature, double AtMs, double PeakHz, double MaxHz = 0);

        /// <summary>Renders the events and returns the mono render and one row per note start: the worst curvature of the voice that starts it, from 2 ms after its start to the next note's start.</summary>
        public static (float[] Mono, List<Row> Rows) RenderTrace(Dictionary<int, int> preset, int filter, List<Ev> events, double seconds, Action<ParamValues> tweak = null, int block = 16)
        {
            var pts = new List<(int Pos, int Voice, float Age, float Oct, float Hz)>();
            var (l, r, _) = Render(preset, filter, events, seconds, tweak, block, (e, pos) =>
            {
                for (int i = 0; i < PolyEngine.VoiceCount; i++) { var v = e.Voice(i); if (v.IsActive) pts.Add((pos, i, v.NoteAgeNow, v.CutOctNow, v.Cut1Hz)); }
            });
            var (ons, _) = AcidGate.Boundaries(events);
            return (Mid(l, r), Curvature(pts, ons, block));
        }

        public static List<Row> Curvature(List<(int Pos, int Voice, float Age, float Oct, float Hz)> pts, List<int> ons, int block)
        {
            int k = Math.Max(1, (int)Math.Round(SpanMs * 0.001 * Rate / block));
            double dt = k * block / (double)Rate;
            var rows = new List<Row>();
            var byVoice = pts.GroupBy(p => p.Voice).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Pos).ToList());
            var worst = new double[ons.Count]; var at = new double[ons.Count]; var hz = new double[ons.Count]; var maxHz = new double[ons.Count];
            foreach (var list in byVoice.Values)
            {
                foreach (var q in list) { int s0 = ons.FindLastIndex(o => o <= q.Pos); if (s0 >= 0 && q.Pos - ons[s0] < 2400) maxHz[s0] = Math.Max(maxHz[s0], q.Hz); }
                // runs: a run ends where the voice's note age goes backwards (a new note) or the voice was idle
                int start = 0;
                for (int i = 1; i <= list.Count; i++)
                {
                    bool end = i == list.Count || list[i].Age < list[i - 1].Age || list[i].Pos - list[i - 1].Pos > block;
                    if (!end) continue;
                    for (int b = start + k; b + k < i; b++)
                    {
                        double dd = Math.Abs(list[b + k].Oct - 2.0 * list[b].Oct + list[b - k].Oct) / (dt * dt);
                        int pos = list[b].Pos;
                        int s = ons.FindLastIndex(o => o <= pos);
                        if (s < 0 || pos - ons[s] < 96) continue;       // the first 2 ms of a note: the cold start
                        if (dd > worst[s]) { worst[s] = dd; at[s] = (pos - ons[s]) / 48.0; hz[s] = list[b].Hz; }
                    }
                    start = i;
                }
            }
            for (int s = 0; s < ons.Count; s++) rows.Add(new Row(s + 1, worst[s], at[s], hz[s], maxHz[s]));
            return rows;
        }

        public static double Worst(List<Row> rows) => rows.Count == 0 ? 0 : rows.Max(r => r.Curvature);
        public static string Flags(List<Row> rows) => string.Join(" ", rows.Where(r => r.Curvature > MaxCurvature).Select(r => $"{r.Step}:{r.Curvature / 1000:F0}k@{r.AtMs:F0}ms"));
    }
}
