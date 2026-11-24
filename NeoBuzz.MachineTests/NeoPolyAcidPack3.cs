using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Poly;
using static NeoBuzz.MachineTests.AcidLines;

namespace NeoBuzz.MachineTests
{
    /// <summary>Listening pack 3 (AC3), local only: NEO_ACID_PACK3=&lt;dir&gt; [NEO_ACID_PACK2_DIR=&lt;the AC2 pack, for the before/after pairs&gt;] dotnet test --filter WritesListeningPack3.</summary>
    [TestFixture]
    public class NeoPolyAcidPack3
    {
        static readonly Dictionary<string, Dictionary<int, int>> Presets = PresetTests.AllPresets().Where(p => p.Machine == "Neo Poly").ToDictionary(p => p.Preset, p => p.Values);
        static readonly string[] Acid = Presets.Keys.Where(k => k.StartsWith("Acid")).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        static string Safe(string name) => name.Replace(" — ", " ").Replace(' ', '-');

        static (float[] Mono, List<AcidGate.Row> Rows) RenderMono(string name, int filter, Note[] line, Action<ParamValues> tweak = null)
        {
            var ev = Sequence(line, 2);
            var (l, r, _) = Render(Presets[name], filter, ev, LineSeconds(2), tweak);
            var m = Mid(l, r);
            return (m, AcidGate.Analyse(m, ev));
        }

        static string Row(string label, List<AcidGate.Row> rows) =>
            $"| {label} | {AcidGate.Worst2d(rows):F4} | {rows.Max(x => x.Near2d):F4} | {rows.Count(x => x.Bad)} | {(rows.Any(x => x.Bad) ? AcidGate.Flags(rows) : "-")} |";

        const string Readme = """
            # Neo Poly acid listening pack 3 (AC3, local only, no audio is committed)

            What this is: the acid voice after a review of the whole engine, not another patch on it. Pack 2 still clicked: the
            accent opened the filter to the top of the band (23 kHz) on every accented note, so every saw edge went through a wide-open
            resonant filter, and note starts were short and hard. What changed in the voice: the filter cutoff approaches a soft
            ceiling (the new `Filter range`, 1.6 kHz as shipped) and the total modulation is soft-limited; the accent sweep is
            capped (`Sweep limit`); the acid ladder and diode run at twice the sample rate between linear-phase half-band filters
            (`Filter quality`: Eco 1x, High 2x, Ultra 4x); the amp goes through a smooth onset and release (`Soft start`, 3.5 ms; a
            raised-cosine start from silence, a raised-cosine fade when a voice is stolen, a release of at least 5 ms). The filter
            now lags the oscillators by 15 samples (0.31 ms) at High.

            Four folders (24 bit, 48 kHz, mono):

            - `blind/` : six items, each `NN_A.wav` and `NN_B.wav`: Diode 18 against the 24 dB ladder on the pack line (two bars),
              Diode 18 in slot A in exactly three of the six (a seeded shuffle, never three in a row), the same loudness in both.
              Which is which: `KEY-do-not-open-before-listening.txt`.
            - `named/` : by name, the presets as they ship. `Acid-X-diode18.wav`, `Acid-X-ladder24.wav` (the pack line), `Acid-X-busy-line.wav`
              (the busier line: sixteenths, eight slides, seven accents; this is the one to listen to for clicks),
              and `Acid-X-range-max-busy-line.wav`: the same busy line with Filter range at its maximum (18 kHz) and nothing else changed, so you can hear
              what the limit does. It is meant to be brighter and rougher; clicks and buzz may come back there.
            - `before-after/` : per preset, the busy line from the pack-2 build (`Acid-X-1-before.wav`) and from this build (`Acid-X-2-after.wav`),
              the before brought to the after's loudness (the gain is in `before-after/GAINS.txt`). Listen for the clicks you named.
            - `measurements.md` : the numbers: the largest second difference per preset, before and after, and the steps the gate flags.

            Gate: the largest second difference of the raw signal in every step at most 0.010 (full scale), at most 0.004 within 5 ms of a note
            start or end, and no burst above 6 kHz at a note start (above 6 dB over the note's 30 to 80 ms; floors -90 dBFS on a dark
            note, -75 dBFS on a bright one). No oscillator-edge exclusion. The pack-2 renders fail it at the steps you named.

            What I would like to hear back: per preset, clicks or none; whether Squelch still bites and Screamer still screams at 1.6 kHz
            (if not, say which of Filter range, Sweep limit or Soft start you would raise, and I will move the defaults); and which of Eco, High
            and Ultra you hear a difference between (all three are in the pack only through `Filter quality`; High is the default).
            """;

        [Test]
        public void WritesListeningPack3()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_ACID_PACK3");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("local only: set NEO_ACID_PACK3 to a folder");
            string before = Environment.GetEnvironmentVariable("NEO_ACID_PACK2_DIR");
            foreach (var sub in new[] { "blind", "named", "before-after" }) Directory.CreateDirectory(Path.Combine(dir, sub));
            var rng = new Random(20261125);
            var order = Enumerable.Range(0, Acid.Length).OrderBy(_ => rng.Next()).ToArray();
            HashSet<int> Draw() => Enumerable.Range(0, Acid.Length).OrderBy(_ => rng.Next()).Take(Acid.Length / 2).ToHashSet();
            var diodeFirst = Draw();
            while (diodeFirst.Max() - diodeFirst.Min() == diodeFirst.Count - 1) diodeFirst = Draw();
            var key = new StringBuilder("item\tpreset\tslot A\tslot B\tgain applied to the ladder render (dB)\tgate-flagged steps (diode, ladder)\n");
            var md = new StringBuilder("# Neo Poly acid pack 3: measurements\n\nGate numbers per render (second difference of the raw mono signal; full scale = 1; limits 0.010 anywhere, 0.004 within 5 ms of a note start or end).\n\n");
            md.Append("| render | worst 2nd diff | worst near a note start/end | steps flagged | which |\n|---|---|---|---|---|\n");
            int flaggedTotal = 0;
            var gains = new List<double>();
            for (int item = 0; item < Acid.Length; item++)
            {
                string name = Acid[order[item]];
                var (diode, rd) = RenderMono(name, 6, Pack);
                var (ladder, rl) = RenderMono(name, 0, Pack);
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
                var (busy, rb) = RenderMono(name, -1, Busy);
                NeoPolyClickTests.WriteWav24(Path.Combine(dir, "named", $"{safe}-busy-line.wav"), busy);
                var (wide, rw) = RenderMono(name, -1, Busy, v => v.Set(P.AcidRange, Params.Table[P.AcidRange].Max));
                NeoPolyClickTests.WriteWav24(Path.Combine(dir, "named", $"{safe}-range-max-busy-line.wav"), wide);
                flaggedTotal += rd.Count(x => x.Bad) + rl.Count(x => x.Bad) + rb.Count(x => x.Bad);
                md.Append(Row($"{name} diode18 pack line", rd)).Append('\n').Append(Row($"{name} ladder24 pack line", rl)).Append('\n').Append(Row($"{name} busy line", rb)).Append('\n').Append(Row($"{name} busy line, Filter range 18 kHz", rw)).Append('\n');
                if (before != null)
                {
                    var old = NeoPolyAcidGateTests.ReadWav24(Path.Combine(before, "named", $"{safe}-busy-line.wav"));
                    var oldRows = AcidGate.Analyse(old, Sequence(Busy, 2));
                    double g = Measure.Rms(busy, 0, busy.Length) / Math.Max(1e-9, Measure.Rms(old, 0, old.Length));
                    var oldMatched = old.Select(x => (float)(x * g)).ToArray();
                    float t2 = (float)Math.Min(1.0, 0.89 / Math.Max(Measure.Peak(busy, 0, busy.Length), Measure.Peak(oldMatched, 0, oldMatched.Length)));
                    NeoPolyClickTests.WriteWav24(Path.Combine(dir, "before-after", $"{safe}-1-before.wav"), oldMatched.Select(x => x * t2).ToArray());
                    NeoPolyClickTests.WriteWav24(Path.Combine(dir, "before-after", $"{safe}-2-after.wav"), busy.Select(x => x * t2).ToArray());
                    File.AppendAllText(Path.Combine(dir, "before-after", "GAINS.txt"), $"{name}: before x {Measure.Db(g):F2} dB, both x {Measure.Db(t2):F2} dB\n");
                    md.Append(Row($"{name} busy line, pack 2 (before)", oldRows)).Append('\n');
                }
            }
            if (before != null)
            {
                md.Append("\n## Per step, busy line, before (pack 2) and after (this build): largest second difference in the step, and near a note start/end\n\n");
                foreach (var name in Acid)
                {
                    var old = NeoPolyAcidGateTests.ReadWav24(Path.Combine(before, "named", $"{Safe(name)}-busy-line.wav"));
                    var oldRows = AcidGate.Analyse(old, Sequence(Busy, 2));
                    var (_, newRows) = RenderMono(name, -1, Busy);
                    md.Append($"### {name}\n\n| step | before max | before near | before 6k burst dB | after max | after near | after 6k burst dB |\n|---|---|---|---|---|---|---|\n");
                    for (int i = 0; i < oldRows.Count; i++)
                        md.Append($"| {i + 1} | {oldRows[i].Max2d:F4} | {oldRows[i].Near2d:F4} | {oldRows[i].BroadbandDb:F0} | {newRows[i].Max2d:F4} | {newRows[i].Near2d:F4} | {newRows[i].BroadbandDb:F0} |\n");
                    md.Append('\n');
                }
            }
            File.WriteAllText(Path.Combine(dir, "KEY-do-not-open-before-listening.txt"), key.ToString());
            File.WriteAllText(Path.Combine(dir, "README.md"), Readme);
            File.WriteAllText(Path.Combine(dir, "measurements.md"), md.ToString());
            Assert.That(diodeFirst.Count, Is.EqualTo(3));
            Assert.That(flaggedTotal, Is.EqualTo(0), "the gate finds nothing in the pack as shipped");
        }
    }
}
