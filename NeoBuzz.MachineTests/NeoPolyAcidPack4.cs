using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Poly;
using static NeoBuzz.MachineTests.AcidLines;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Listening pack 4 (AC3 round 3), local only: NEO_ACID_PACK4=&lt;dir&gt; [NEO_ACID_PACK3_DIR=&lt;the round-2 pack, for the before/after pairs and the before columns&gt;]
    /// dotnet test --filter WritesListeningPack4.
    /// </summary>
    [TestFixture]
    public class NeoPolyAcidPack4
    {
        static readonly Dictionary<string, Dictionary<int, int>> Presets = PresetTests.AllPresets().Where(p => p.Machine == "Neo Poly").ToDictionary(p => p.Preset, p => p.Values);
        static readonly string[] Acid = Presets.Keys.Where(k => k.StartsWith("Acid")).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        static string Safe(string name) => name.Replace(" — ", " ").Replace(' ', '-');

        static (float[] Mono, List<AcidGate.Row> Rows, List<AcidCutoff.Row> Cut) RenderMono(string name, int filter, Note[] line, Action<ParamValues> tweak = null)
        {
            var ev = Sequence(line, 2);
            var (m, cut) = AcidCutoff.RenderTrace(Presets[name], filter, ev, LineSeconds(2), tweak);
            return (m, AcidGate.Analyse(m, ev, AcidGate.IsDark(name)), cut);
        }

        /// <summary>The audio measure: the largest |2nd difference| (full scale = 1) 8 to 60 ms into each note, per step: the step, the value and the time after the note start.</summary>
        internal static List<(int Step, double Peak, double AtMs)> InsidePeaks(float[] m, List<Ev> ev)
        {
            var (ons, _) = AcidGate.Boundaries(ev);
            var res = new List<(int, double, double)>();
            for (int s = 0; s < ons.Count; s++)
            {
                int a = ons[s] + 8 * 48, b = Math.Min(ons[s] + 60 * 48, Math.Min(s + 1 < ons.Count ? ons[s + 1] : int.MaxValue, m.Length - 2));
                double pk = 0; int at = a;
                for (int i = a; i < b; i++) { double d = Math.Abs(m[i + 1] - 2.0 * m[i] + m[i - 1]); if (d > pk) { pk = d; at = i; } }
                res.Add((s + 1, pk, (at - ons[s]) / 48.0));
            }
            return res;
        }

        static string Top(List<(int Step, double Peak, double AtMs)> peaks, int n = 3) =>
            string.Join(", ", peaks.OrderByDescending(p => p.Peak).Take(n).Select(p => $"s{p.Step} {p.Peak:F4}@{p.AtMs:F0}ms"));

        static string Row(string label, List<AcidGate.Row> rows, List<AcidCutoff.Row> cut) =>
            $"| {label} | {AcidGate.WorstRatio(rows):F1}x | {rows.Count(x => x.Bad)} | {AcidCutoff.Worst(cut) / 1000:F0}k | {cut.Count(x => x.Curvature > AcidCutoff.MaxCurvature)} |";

        /// <summary>What the user heard of the round-2 blind pack (pack 3), by preset, and the cutoff curvature the round-2 build measured on the same pack line with this helper (follower off).</summary>
        static readonly (string Preset, string Heard, double CornerCurvature)[] Heard =
        {
            ("Acid — Plucky", "clicks 'half way and at the very end' (steps 15 and 31); 'still too aggressive'", 543000),
            ("Acid — Squelch", "clicks at the beginning, mid way and at the end (steps 1, 15, 31); busy line kept", 463000),
            ("Acid — Deep", "click heard (steps 25, 15, 1); 'still clicky'", 424000),
            ("Acid — Sub", "no click; kept", 341000),
            ("Acid — Screamer", "no click; kept", 498000),
            ("Acid — Rubber", "no click, 'very fast slaps' (Diode preferred); kept", 357000),
        };

        const string Readme = """
            # Neo Poly acid listening pack 4 (AC3 round 3, local only, no audio is committed)

            What this is: the acid voice after the clicks you heard INSIDE the notes (20 to 35 ms after a note's start: Plucky steps 15 and 31,
            Squelch 1, 15 and 31, Deep 25, 15 and 1). Round 2 limited how fast the filter cutoff may rise, and that left a corner in the cutoff: it
            climbed at the limited rate until it met the decaying envelope, then turned at once to falling, and a resonant filter pings at a corner.
            Now the limited path runs through a critically damped follower (two equal one-poles of 0.4 x Soft start, 1.4 ms at the default), so the
            cutoff's slope is continuous, and the fall is limited to twice the rise (it was six times). The cutoff's curvature (octaves per second
            squared, over 1 ms) went from 340000 to 540000 on every preset to 75000 to 105000 on the pack line.
            Plucky is less aggressive: F1 reso 70 to 60, Accent 90 to 72 %, Accent sweep 2.0 to 1.5 octaves. Nothing else in any preset changed.

            Honest limit, because you should know what to listen for: what is left in the audio at those instants is the saw oscillator's own edge. A
            saw wraps once per period; when the cutoff is at its peak (4 to 8 kHz, 20 to 25 ms after an accent) the wrap that happens to land there is a
            sharper edge than the muffled wraps before it. Taking the resonance away (reso 0) does not remove it. If you still hear a tick on the first
            accent, that is the edge, and the fix would be a slower or lower accent peak (less bite), or phase control of the oscillator, not a smoother
            cutoff. Tell me which you want.

            Four folders (24 bit, 48 kHz, mono):

            - `blind/` : six items, each `NN_A.wav` and `NN_B.wav`: Diode 18 against the 24 dB ladder on the pack line (two bars), Diode 18 in slot A in exactly
              three of the six (a seeded shuffle, a new one), the same loudness in both. Which is which: `KEY-do-not-open-before-listening.txt`.
            - `named/` : by name, the presets as they ship (default Filter range 10 kHz). `Acid-X-diode18.wav`, `Acid-X-ladder24.wav` (the pack line),
              `Acid-X-busy-line.wav`, and `Acid-X-range-max-busy-line.wav` (Filter range 18 kHz, nothing else changed).
            - `before-after/` : per preset, the busy line from pack 3 (`Acid-X-1-before.wav`) and from this build (`Acid-X-2-after.wav`), the before brought to
              the after's loudness (the gain is in `before-after/GAINS.txt`).
            - `measurements.md` : the numbers, with what you heard next to them.

            The gates: (1) the cutoff path, at every Filter range (1.6, 3, 6, 10 and 18 kHz), both filters, six presets, pack and busy lines: the curvature of the cutoff
            is at most 150000 oct/s^2 over 1 ms. It does not depend on the preset, so the round-2 build fails it on every preset, including the three you heard as clean (their
            corner was there too; a dark filter, a saturated sound or a percussive one covers it). (2) The boundary gate of round 2 (boundary peak at most 2.0 x the interior peak,
            no burst above 6 kHz), unchanged. (3) The audio measure in the table, for you to read, not a limit: no audio-only limit separated what you heard as clicks from what you heard as clean,
            because a saw edge is as sharp in Screamer as in Squelch.
            """;

        [Test]
        public void WritesListeningPack4()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_ACID_PACK4");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("local only: set NEO_ACID_PACK4 to a folder");
            string before = Environment.GetEnvironmentVariable("NEO_ACID_PACK3_DIR");
            foreach (var sub in new[] { "blind", "named", "before-after" }) Directory.CreateDirectory(Path.Combine(dir, sub));
            var rng = new Random(20261126);
            var order = Enumerable.Range(0, Acid.Length).OrderBy(_ => rng.Next()).ToArray();
            HashSet<int> Draw() => Enumerable.Range(0, Acid.Length).OrderBy(_ => rng.Next()).Take(Acid.Length / 2).ToHashSet();
            var diodeFirst = Draw();
            while (diodeFirst.Max() - diodeFirst.Min() == diodeFirst.Count - 1) diodeFirst = Draw();
            var key = new StringBuilder("item\tpreset\tslot A\tslot B\tgain applied to the ladder render (dB)\tgate-flagged steps (diode, ladder)\n");
            var md = new StringBuilder("# Neo Poly acid pack 4: measurements\n\n");
            var table = new StringBuilder();
            table.Append("## What you heard, next to the numbers (pack line, Diode 18)\n\n");
            table.Append("Cutoff curvature in oct/s^2 over 1 ms (bound 150000): the worst step. Audio: the three largest |2nd difference| 8 to 60 ms into a note (full scale = 1). Before = the pack-3 build (pack-3 `named/` renders when NEO_ACID_PACK3_DIR is set; curvature measured with the follower off).\n\n");
            table.Append("| preset | what you heard (pack 3) | cutoff curvature before | after | audio before: three largest | audio after: three largest |\n|---|---|---|---|---|---|\n");
            md.Append("Per render: the worst boundary ratio (limit 2.0x), the steps the boundary gate flags, the worst cutoff curvature (limit 150000 oct/s^2) and the steps over it.\n\n");
            md.Append("| render | worst boundary ratio | steps flagged | worst cutoff curvature | steps over |\n|---|---|---|---|---|\n");
            int flaggedTotal = 0, curvTotal = 0;
            var gains = new List<double>();
            for (int item = 0; item < Acid.Length; item++)
            {
                string name = Acid[order[item]];
                var (diode, rd, cd) = RenderMono(name, 6, Pack);
                var (ladder, rl, cl) = RenderMono(name, 0, Pack);
                double gain = Measure.Rms(diode, 0, diode.Length) / Math.Max(1e-9, Measure.Rms(ladder, 0, ladder.Length));
                var matched = ladder.Select(x => (float)(x * gain)).ToArray();
                gains.Add(Measure.Db(gain));
                double peak = Math.Max(Measure.Peak(diode, 0, diode.Length), Measure.Peak(matched, 0, matched.Length));
                float trim = peak > 0.89 ? (float)(0.89 / peak) : 1f;
                bool diodeInA = diodeFirst.Contains(item);
                string n = (item + 1).ToString("00");
                NeoPolyClickTests.WriteWav24(Path.Combine(dir, "blind", $"{n}_A.wav"), (diodeInA ? diode : matched).Select(x => x * trim).ToArray());
                NeoPolyClickTests.WriteWav24(Path.Combine(dir, "blind", $"{n}_B.wav"), (diodeInA ? matched : diode).Select(x => x * trim).ToArray());
                key.Append($"{n}\t{name}\t{(diodeInA ? "Diode 18" : "Ladder 24")}\t{(diodeInA ? "Ladder 24" : "Diode 18")}\t{Measure.Db(gain):F2}\t{rd.Count(x => x.Bad)},{rl.Count(x => x.Bad)}\n");
                string safe = Safe(name);
                NeoPolyClickTests.WriteWav24(Path.Combine(dir, "named", $"{safe}-diode18.wav"), diode);
                NeoPolyClickTests.WriteWav24(Path.Combine(dir, "named", $"{safe}-ladder24.wav"), ladder);
                var (busy, rb, cb) = RenderMono(name, -1, Busy);
                NeoPolyClickTests.WriteWav24(Path.Combine(dir, "named", $"{safe}-busy-line.wav"), busy);
                var (wide, rw, cw) = RenderMono(name, -1, Busy, v => v.Set(P.AcidRange, Params.Table[P.AcidRange].Max));
                NeoPolyClickTests.WriteWav24(Path.Combine(dir, "named", $"{safe}-range-max-busy-line.wav"), wide);
                flaggedTotal += rd.Count(x => x.Bad) + rl.Count(x => x.Bad) + rb.Count(x => x.Bad);
                curvTotal += new[] { cd, cl, cb, cw }.Sum(c => c.Count(x => x.Curvature > AcidCutoff.MaxCurvature));
                md.Append(Row($"{name} diode18 pack line", rd, cd)).Append('\n').Append(Row($"{name} ladder24 pack line", rl, cl)).Append('\n')
                  .Append(Row($"{name} busy line", rb, cb)).Append('\n').Append(Row($"{name} busy line, Filter range 18 kHz", rw, cw)).Append('\n');
                if (before != null)
                {
                    var old = NeoPolyAcidGateTests.ReadWav24(Path.Combine(before, "named", $"{safe}-busy-line.wav"));
                    double g = Measure.Rms(busy, 0, busy.Length) / Math.Max(1e-9, Measure.Rms(old, 0, old.Length));
                    var oldMatched = old.Select(x => (float)(x * g)).ToArray();
                    float t2 = (float)Math.Min(1.0, 0.89 / Math.Max(Measure.Peak(busy, 0, busy.Length), Measure.Peak(oldMatched, 0, oldMatched.Length)));
                    NeoPolyClickTests.WriteWav24(Path.Combine(dir, "before-after", $"{safe}-1-before.wav"), oldMatched.Select(x => x * t2).ToArray());
                    NeoPolyClickTests.WriteWav24(Path.Combine(dir, "before-after", $"{safe}-2-after.wav"), busy.Select(x => x * t2).ToArray());
                    File.AppendAllText(Path.Combine(dir, "before-after", "GAINS.txt"), $"{name}: before x {Measure.Db(g):F2} dB, both x {Measure.Db(t2):F2} dB\n");
                }
            }
            foreach (var (preset, heard, corner) in Heard)
            {
                var (m, _, cut) = RenderMono(preset, 6, Pack);
                string beforeAudio = "(set NEO_ACID_PACK3_DIR)";
                if (before != null)
                {
                    var old = NeoPolyAcidGateTests.ReadWav24(Path.Combine(before, "named", $"{Safe(preset)}-diode18.wav"));
                    beforeAudio = Top(InsidePeaks(old, Sequence(Pack, 2)));
                }
                table.Append($"| {preset} | {heard} | {corner / 1000:F0}k | {AcidCutoff.Worst(cut) / 1000:F0}k | {beforeAudio} | {Top(InsidePeaks(m, Sequence(Pack, 2)))} |\n");
            }
            File.WriteAllText(Path.Combine(dir, "KEY-do-not-open-before-listening.txt"), key.ToString());
            File.WriteAllText(Path.Combine(dir, "README.md"), Readme);
            File.WriteAllText(Path.Combine(dir, "measurements.md"), table.ToString() + "\n" + md.ToString());
            Assert.That(diodeFirst.Count, Is.EqualTo(3));
            Assert.That(flaggedTotal, Is.EqualTo(0), "the boundary gate finds nothing in the pack as shipped");
            Assert.That(curvTotal, Is.EqualTo(0), "the cutoff path has no corner in the pack as shipped");
        }
    }
}
