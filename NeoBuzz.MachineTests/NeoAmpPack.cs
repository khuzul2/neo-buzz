using System.Text;
using NeoBuzz.Amp;
using NeoBuzz.Dsp;
using static NeoBuzz.MachineTests.AmpRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo Amp listening pack (docs/specs/neo-amp.md §15): every factory preset on the source it suits (plucked chords, a single-note riff
    /// or a bass line, all made by the test from a plucked-string model: no third-party audio), 15 s, peaks about -12 dBFS before the amp, written
    /// as 16-bit 48 kHz WAV files with a README. Local only, never committed: run it by hand with NEO_AMP_PACK set to the output folder.
    ///   NEO_AMP_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~NeoAmpPack"
    /// </summary>
    [TestFixture]
    public class NeoAmpPack
    {
        const int Length = 15 * Rate;

        static void WriteWav(string path, float[] x)
        {
            using var f = File.Create(path);
            using var w = new BinaryWriter(f);
            int n = x.Length, bytes = n * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            for (int i = 0; i < n; i++) w.Write((short)Math.Clamp((int)Math.Round(x[i] * 32767.0), -32768, 32767));
        }

        static Dictionary<string, string> Comments
        {
            get
            {
                var map = new Dictionary<string, string>();
                string dir = TestContext.CurrentContext.TestDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "Machines", "NeoAmp", "Presets.txt"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) return map;
                string name = null;
                foreach (var line in File.ReadAllLines(Path.Combine(dir, "Machines", "NeoAmp", "Presets.txt")))
                {
                    if (line.StartsWith("preset \"")) name = line.Substring(8).TrimEnd('"');
                    else if (name != null && line.StartsWith("comment \"")) map[name] = line.Substring(9).TrimEnd('"');
                }
                return map;
            }
        }

        static string Slug(string s) =>
            new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

        [Test, Explicit("writes the listening pack: set NEO_AMP_PACK to an output folder")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_AMP_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_AMP_PACK");
            Directory.CreateDirectory(dir);
            var chords = AmpSources.Chords(15.0, 0.25);
            var riff = AmpSources.Riff(15.0, 0.25);
            var bass = AmpSources.Bass(15.0, 0.25);
            var sources = new Dictionary<string, float[]>
            {
                ["Clean — Glassy"] = chords, ["Clean — Warm Jazz"] = chords, ["Crunch — Edge of Breakup"] = chords, ["Crunch — Classic Rock"] = riff,
                ["Lead — Singing Sustain"] = riff, ["High Gain — Tight Rhythm"] = riff, ["Bass — Round Clean"] = bass, ["Bass — Growl"] = bass,
            };
            var names = new Dictionary<float[], string> { [chords] = "plucked chords (E, A, D, G)", [riff] = "a single-note riff on the low strings", [bass] = "a bass line" };

            var log = new StringBuilder();
            log.AppendLine("# Neo Amp listening pack");
            log.AppendLine();
            log.AppendLine("Sources, made by the test from a plucked-string model (a Karplus-Strong loop with a pickup filter; no third-party audio), 15 s each, **peak -12 dBFS** before the amp:");
            log.AppendLine("- **chords**: eight bars of E, A, D and G chords, strummed;");
            log.AppendLine("- **riff**: a single-note riff on the low strings, palm-muted eighths and a few long notes;");
            log.AppendLine("- **bass**: a bass line an octave and more lower.");
            log.AppendLine("Every file: 48 kHz, 16-bit, mono WAV (the amp and cabinet are mono), the machine at one factory preset with Quality 4x. Click detector (second difference against its local mean, `ClickDetector.Find`) on every file: the clicks of the dry source are not counted.");
            log.AppendLine();
            log.AppendLine("| File | Preset | Source | What to listen for | Peak | RMS | Clicks |");
            log.AppendLine("|---|---|---|---|---|---|---|");
            int index = 0, clicksTotal = 0;
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Amp"))
            {
                index++;
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var x = sources[p.Preset];
                var y = Run(Engine(v), x);
                var dry = new HashSet<int>(ClickDetector.Find(x));
                var clicks = ClickDetector.Find(y).Where(c => !dry.Any(d => Math.Abs(d - c) < 64)).ToList();
                clicksTotal += clicks.Count;
                double pk = 0, sq = 0;
                for (int i = 0; i < Length; i++) { pk = Math.Max(pk, Math.Abs(y[i])); sq += (double)y[i] * y[i]; }
                string file = $"{index:00}-{Slug(p.Preset)}.wav";
                WriteWav(Path.Combine(dir, file), y);
                log.AppendLine($"| {file} | {p.Preset} | {names[x]} | {Comments.GetValueOrDefault(p.Preset, "")} | {20 * Math.Log10(pk):0.0} dBFS | {10 * Math.Log10(sq / Length):0.0} dBFS | {clicks.Count}{(clicks.Count > 0 ? " at " + string.Join(", ", clicks.Take(6).Select(c => $"{c / (double)Rate:0.000} s")) : "")} |");
            }
            log.AppendLine();
            log.AppendLine($"Click detector, all files: {clicksTotal} new clicks.");
            File.WriteAllText(Path.Combine(dir, "README.md"), log.ToString());
            TestContext.Out.WriteLine(log.ToString());
            Assert.That(clicksTotal, Is.EqualTo(0));
        }
    }
}
