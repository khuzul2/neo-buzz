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
            return (m, AcidGate.Analyse(m, ev, AcidGate.IsDark(name)));
        }

        static string Row(string label, List<AcidGate.Row> rows) =>
            $"| {label} | {AcidGate.WorstRatio(rows):F1}x | {AcidGate.Worst2d(rows):F4} | {rows.Count(x => x.Bad)} | {(rows.Any(x => x.Bad) ? AcidGate.Flags(rows) : "-")} |";

        const string Readme = """
            # Neo Poly acid listening pack 3 (AC3, local only, no audio is committed)

            What this is: the acid voice after a second look at the whole engine. Pack 2 still clicked, and the first AC3 build hid the clicks
            behind a dark filter (1.6 kHz) instead of fixing them. The cause is now found and fixed at its source: an accented note asked the
            filter for five to seven octaves of sweep in under five milliseconds (the envelope's attack and the accent's depth both arrive at
            once, and the cutoff took a note from 300 Hz to 10 kHz and more), and a resonant filter swept that fast pops at the note's start
            whatever its ceiling. Now the cutoff may rise only at a set rate (200 octaves a second at the default Soft start for the first 10 ms of
            a note, up to three times that after it), and falls six times as fast as it rises. Around it, as before: a soft ceiling on the cutoff
            (`Filter range`, 10 kHz as shipped), a soft limit on the total modulation, a capped accent sweep (`Sweep limit`), the acid ladder
            and diode at twice the sample rate (`Filter quality`: Eco 1x, High 2x, Ultra 4x), a smooth start and end of every note
            (`Soft start`, 3.5 ms; it also sets how fast the cutoff may rise). The filter lags the oscillators by 15 samples (0.31 ms) at High.
            Because the sweep is slower, the accent's bite now comes a little later and the presets' accents were retuned for it
            (longer accent decays, deeper accents on Plucky, Squelch, Deep and Sub): the accent is brighter than a plain note again, by 1.15 to 3 times in spectral centroid, and 3.4 to 4.7 dB louder.

            Four folders (24 bit, 48 kHz, mono):

            - `blind/` : six items, each `NN_A.wav` and `NN_B.wav`: Diode 18 against the 24 dB ladder on the pack line (two bars),
              Diode 18 in slot A in exactly three of the six (a seeded shuffle, never three in a row), the same loudness in both.
              Which is which: `KEY-do-not-open-before-listening.txt`.
            - `named/` : by name, the presets as they ship (all at the default Filter range, 10 kHz). `Acid-X-diode18.wav`, `Acid-X-ladder24.wav` (the pack line),
              `Acid-X-busy-line.wav` (the busier line: sixteenths, eight slides, seven accents; the one to listen to for clicks),
              and `Acid-X-range-max-busy-line.wav`: the same busy line with Filter range at its maximum (18 kHz) and nothing else changed.
              It is brighter; the gate still finds no boundary spike there (one note in Screamer has a burst above 6 kHz).
            - `before-after/` : per preset, the busy line from the pack-2 build (`Acid-X-1-before.wav`) and from this build (`Acid-X-2-after.wav`),
              the before brought to the after's loudness (the gain is in `before-after/GAINS.txt`). Listen for the clicks you named.
            - `measurements.md` : the numbers: per render the worst boundary ratio and the steps the gate flags; per step, before and after.

            Gate (replaced; the absolute 0.010 / 0.004 limits are gone): at every note start and note end the largest second difference of the raw
            signal within 5 ms is at most 2x the larger interior peak of the notes around it (10 to 55 ms after, 55 to 8 ms before; never under 1e-4), plus no burst above 6 kHz at
            a note start (more than 6 dB over the note's 30 to 80 ms; floors -90 dBFS on a dark note, -75 dBFS on a bright one). No oscillator-edge exclusion.
            Checked at Filter range 1.6, 3, 6, 10 (default) and 18 kHz, on both filters, at blocks 16, 64 and 256. The pack-2 renders fail it at the steps you named.

            What I would like to hear back: per preset, clicks or none; whether Squelch bites and Screamer screams at the default; the
            range-max renders against the defaults; and which of Eco, High and Ultra you hear a difference between (High is the default).
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
            var md = new StringBuilder("# Neo Poly acid pack 3: measurements\n\nGate numbers per render: the worst note boundary's second difference (within 5 ms) over the larger interior peak around it (limit 2.0x), the largest second difference anywhere (full scale = 1, for scale), the steps flagged (r = boundary ratio, b = burst above 6 kHz).\n\n");
            md.Append("| render | worst boundary ratio | largest 2nd diff | steps flagged | which |\n|---|---|---|---|---|\n");
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
                    var oldRows = AcidGate.Analyse(old, Sequence(Busy, 2), AcidGate.IsDark(name));
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
                    var oldRows = AcidGate.Analyse(old, Sequence(Busy, 2), AcidGate.IsDark(name));
                    var (_, newRows) = RenderMono(name, -1, Busy);
                    md.Append($"### {name}\n\n| step | before ratio | before 6k burst dB | after ratio | after 6k burst dB |\n|---|---|---|---|---|\n");
                    for (int i = 0; i < oldRows.Count; i++)
                        md.Append($"| {i + 1} | {oldRows[i].Ratio:F1}x | {oldRows[i].BroadbandDb:F0} | {newRows[i].Ratio:F1}x | {newRows[i].BroadbandDb:F0} |\n");
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
