using System.Text;
using NeoBuzz.Chop;
using NeoBuzz.Dsp;
using static NeoBuzz.MachineTests.ChopRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo Chop listening pack (docs/specs/neo-chop.md): every factory preset on one four-bar drum loop (Neo Drums) with a chord
    /// loop (Neo Poly) under it at 120 BPM, played twice (8 bars, 16 s), written as 16-bit 48 kHz WAV files with a README. Local only,
    /// never committed: run it by hand with NEO_CHOP_PACK set to the output folder.
    ///   NEO_CHOP_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~NeoChopPack"
    /// </summary>
    [TestFixture]
    public class NeoChopPack
    {
        static ParamValues PresetValues(ParamDef[] table, string machine, string preset)
        {
            var found = PresetTests.AllPresets().First(p => p.Machine == machine && p.Preset == preset);
            var v = new ParamValues(table);
            foreach (var kv in found.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        static (float[] L, float[] R) Run(Action<float[], float[], int> process, int length, Action<int> atBlock)
        {
            var ol = new float[length];
            var or = new float[length];
            var l = new float[256];
            var r = new float[256];
            for (int pos = 0; pos < length; pos += 256)
            {
                atBlock(pos);
                int n = Math.Min(256, length - pos);
                Array.Clear(l, 0, 256);
                Array.Clear(r, 0, 256);
                process(l, r, n);
                Array.Copy(l, 0, ol, pos, n);
                Array.Copy(r, 0, or, pos, n);
            }
            return (ol, or);
        }

        const int Bars = 8;
        static readonly int StepSamples = (int)(Spb(120) / 4);          // a sixteenth
        static readonly int Length = (int)(Spb(120) * 4 * Bars);

        /// <summary>Neo Drums: a four-bar pattern played twice.</summary>
        static (float[], float[]) DrumsSource()
        {
            var v = PresetValues(NeoBuzz.Drums.Params.Table, "Neo Drums", "909 — Classic");
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Rate);
            return Run((l, r, n) => e.Process(l, r, n), Length, pos =>
            {
                for (int s = 0; s < Bars * 16; s++)
                {
                    int at = s * StepSamples;
                    if (at < pos || at >= pos + 256) continue;
                    int b = s % 16, bar = (s / 16) % 4;
                    if (b == 0 || b == 10 || b == 8 || (b == 13 && bar % 2 == 1)) e.NoteOn(0, 36, b == 0 ? 0.95f : 0.8f);
                    if (b == 4 || b == 12) e.NoteOn(1, 38, 0.9f);
                    if (b % 2 == 0) e.NoteOn(2, 42, b % 4 == 0 ? 0.8f : 0.5f);
                    if (b == 14) e.NoteOn(3, 46, 0.6f);
                }
            });
        }

        /// <summary>Neo Poly: Am, F, C, G, a bar each, played twice.</summary>
        static (float[], float[]) ChordSource()
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", "Pad — Warm Strings");
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            int[][] chords = { new[] { 45, 52, 57, 60 }, new[] { 41, 48, 57, 60 }, new[] { 48, 52, 55, 60 }, new[] { 43, 50, 55, 59 } };
            int bar = (int)(Spb(120) * 4);
            return Run((l, r, n) => e.Process(l, r, n), Length, pos =>
            {
                for (int c = 0; c < Bars; c++)
                {
                    if (c * bar >= pos && c * bar < pos + 256)
                        for (int k = 0; k < 4; k++) e.NoteOn(k, chords[c % 4][k], 0.7f);
                    int off = c * bar + (int)(bar * 0.9);
                    if (off >= pos && off < pos + 256)
                        for (int k = 0; k < 4; k++) e.NoteOff(k);
                }
            });
        }

        static void WriteWav(string path, float[] l, float[] r)
        {
            using var f = File.Create(path);
            using var w = new BinaryWriter(f);
            int n = l.Length, bytes = n * 4;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            for (int i = 0; i < n; i++)
            {
                w.Write((short)Math.Clamp((int)Math.Round(l[i] * 32767.0), -32768, 32767));
                w.Write((short)Math.Clamp((int)Math.Round(r[i] * 32767.0), -32768, 32767));
            }
        }

        /// <summary>Each preset's comment from Presets.txt (what the preset does), found by walking up from the test folder.</summary>
        static Dictionary<string, string> Comments
        {
            get
            {
                var map = new Dictionary<string, string>();
                string dir = TestContext.CurrentContext.TestDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "Machines", "NeoChop", "Presets.txt"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) return map;
                string name = null;
                foreach (var line in File.ReadAllLines(Path.Combine(dir, "Machines", "NeoChop", "Presets.txt")))
                {
                    if (line.StartsWith("preset \"")) name = line.Substring(8).TrimEnd('"');
                    else if (name != null && line.StartsWith("comment \"")) map[name] = line.Substring(9).TrimEnd('"');
                }
                return map;
            }
        }

        static string Slug(string s) =>
            new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

        [Test, Explicit("writes the listening pack: set NEO_CHOP_PACK to an output folder")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_CHOP_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_CHOP_PACK");
            Directory.CreateDirectory(dir);
            var (dl, dr) = DrumsSource();
            var (cl, cr) = ChordSource();
            var l = new float[Length];
            var r = new float[Length];
            for (int i = 0; i < Length; i++) { l[i] = dl[i] + 0.45f * cl[i]; r[i] = dr[i] + 0.45f * cr[i]; }
            double peak = 1e-9;
            for (int i = 0; i < Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            float g = (float)(Math.Pow(10, -3.0 / 20) / peak);              // -3 dBFS, so that a gate accent has room
            for (int i = 0; i < Length; i++) { l[i] *= g; r[i] *= g; }

            var log = new StringBuilder();
            log.AppendLine("# Neo Chop listening pack");
            log.AppendLine();
            log.AppendLine("Source: a four-bar drum loop (Neo Drums, \"909 — Classic\") with a four-chord loop (Neo Poly, \"Pad — Warm Strings\") under it, 120 BPM in 4/4,");
            log.AppendLine($"played twice ({Bars} bars, {Length / (double)Rate:0.0} s), at -3 dBFS peak before Neo Chop. Every file: 48 kHz, 16-bit, stereo WAV. The song runs from beat 0;");
            log.AppendLine("the first bar has no history to cut from, so the cuts start in bar 2 (a note or loop that starts mid-song is the same).");
            log.AppendLine();
            log.AppendLine("Each file is the machine at one factory preset. Click detector (second difference against its local mean) on every file: clicks the source itself does not have.");
            log.AppendLine();
            log.AppendLine("| File | Preset | What to listen for | Differs from dry | Peak | Clicks |");
            log.AppendLine("|---|---|---|---|---|---|");
            int index = 0, clicksTotal = 0;
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Chop"))
            {
                index++;
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var (ol, or) = Render(Engine(v), l, r, Constant(120), 256);
                var clicks = NewClicks(l, ol).Union(NewClicks(r, or)).Where(c => c >= 4 * Spb(120)).ToList();
                clicksTotal += clicks.Count;
                double pk = 0;
                for (int i = 0; i < Length; i++) pk = Math.Max(pk, Math.Max(Math.Abs(ol[i]), Math.Abs(or[i])));
                double d = 0, s = 0;
                for (int i = (int)(Spb(120) * 4); i < Length; i++) { double e = ol[i] - l[i]; d += e * e; s += (double)l[i] * l[i]; }
                string file = $"{index:00}-{Slug(p.Preset)}.wav";
                WriteWav(Path.Combine(dir, file), ol, or);
                log.AppendLine($"| {file} | {p.Preset} | {Comments.GetValueOrDefault(p.Preset, "")} | {10 * Math.Log10(d / s):0.0} dB | {20 * Math.Log10(pk):0.0} dBFS | {clicks.Count} |");
            }
            log.AppendLine();
            log.AppendLine($"Click detector, all files: {clicksTotal} clicks.");
            File.WriteAllText(Path.Combine(dir, "README.md"), log.ToString());
            Assert.That(clicksTotal, Is.EqualTo(0));
        }
    }
}
