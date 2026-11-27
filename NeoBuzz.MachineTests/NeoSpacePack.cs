using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Space;
using static NeoBuzz.MachineTests.SpaceRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo Space listening pack (docs/specs/neo-space.md): every factory preset on the source it suits (a Neo Drums loop or a Neo Poly
    /// pluck phrase, three bars at 110 BPM) with the rest of 15 s left as a gap so that the tail is heard, written as 16-bit 48 kHz WAV
    /// files with a README. Local only, never committed: run it by hand with NEO_SPACE_PACK set to the output folder.
    ///   NEO_SPACE_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~NeoSpacePack"
    /// </summary>
    [TestFixture]
    public class NeoSpacePack
    {
        const double Bpm = 110;
        const int Length = 15 * Rate;
        const int Bars = 3;
        static readonly double Beat = Rate * 60.0 / Bpm;

        static ParamValues PresetValues(ParamDef[] table, string machine, string preset)
        {
            var found = PresetTests.AllPresets().First(p => p.Machine == machine && p.Preset == preset);
            var v = new ParamValues(table);
            foreach (var kv in found.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        static (float[] L, float[] R) RunSource(Action<float[], float[], int> process, Action<int> atBlock)
        {
            var ol = new float[Length];
            var or = new float[Length];
            var l = new float[256];
            var r = new float[256];
            for (int pos = 0; pos < Length; pos += 256)
            {
                atBlock(pos);
                int n = Math.Min(256, Length - pos);
                Array.Clear(l, 0, 256);
                Array.Clear(r, 0, 256);
                process(l, r, n);
                Array.Copy(l, 0, ol, pos, n);
                Array.Copy(r, 0, or, pos, n);
            }
            return (ol, or);
        }

        static bool At(double sample, int pos) => (int)Math.Ceiling(sample) >= pos && (int)Math.Ceiling(sample) < pos + 256;

        /// <summary>Neo Drums: three bars of a loop (kick, snare, hats, an open hat), then nothing.</summary>
        static (float[], float[]) Drums()
        {
            var v = PresetValues(NeoBuzz.Drums.Params.Table, "Neo Drums", "Classic — Punchy Kit");
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Rate);
            double step = Beat / 4;
            return RunSource((l, r, n) => e.Process(l, r, n), pos =>
            {
                for (int s = 0; s < Bars * 16; s++)
                {
                    if (!At(s * step, pos)) continue;
                    int b = s % 16, bar = s / 16;
                    if (b == 0 || b == 10 || (b == 7 && bar % 2 == 1)) e.NoteOn(0, 36, b == 0 ? 0.95f : 0.8f);
                    if (b == 4 || b == 12) e.NoteOn(1, 38, 0.9f);
                    if (b % 2 == 0) e.NoteOn(2, 42, b % 4 == 0 ? 0.8f : 0.5f);
                    if (b == 14) e.NoteOn(3, 46, 0.6f);
                }
            });
        }

        /// <summary>Neo Poly: a pluck phrase (an A minor figure, then a lift) over three bars, then nothing.</summary>
        static (float[], float[]) Plucks()
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", "Pluck — Bright");
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            int[] notes = { 57, 60, 64, 67, 64, 60, 62, 65, 69, 65, 62, 64, 60, 57, 55, 52, 57, 0, 0, 0, 64, 0, 0, 0, 60, 0, 0, 0, 57 };
            double step = Beat / 2;
            return RunSource((l, r, n) => e.Process(l, r, n), pos =>
            {
                for (int s = 0; s < notes.Length; s++)
                {
                    if (notes[s] == 0) continue;
                    if (At(s * step, pos)) e.NoteOn(s % 4, notes[s], 0.8f);
                    if (At(s * step + step * 0.8, pos)) e.NoteOff(s % 4);
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

        static Dictionary<string, string> Comments
        {
            get
            {
                var map = new Dictionary<string, string>();
                string dir = TestContext.CurrentContext.TestDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "Machines", "NeoSpace", "Presets.txt"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) return map;
                string name = null;
                foreach (var line in File.ReadAllLines(Path.Combine(dir, "Machines", "NeoSpace", "Presets.txt")))
                {
                    if (line.StartsWith("preset \"")) name = line.Substring(8).TrimEnd('"');
                    else if (name != null && line.StartsWith("comment \"")) map[name] = line.Substring(9).TrimEnd('"');
                }
                return map;
            }
        }

        static string Slug(string s) =>
            new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

        [Test, Explicit("writes the listening pack: set NEO_SPACE_PACK to an output folder")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_SPACE_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_SPACE_PACK");
            Directory.CreateDirectory(dir);
            var drums = Drums();
            var plucks = Plucks();
            // Both sources to -3 dBFS peak.
            foreach (var src in new[] { drums, plucks })
            {
                double peak = 1e-9;
                for (int i = 0; i < Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(src.Item1[i]), Math.Abs(src.Item2[i])));
                float g = (float)(Math.Pow(10, -3.0 / 20) / peak);
                for (int i = 0; i < Length; i++) { src.Item1[i] *= g; src.Item2[i] *= g; }
            }
            var onDrums = new HashSet<string> { "Drums — Drum Room", "Plate — Bright Plate", "Room — Ambience", "Effect — Reverse Swell" };

            var log = new StringBuilder();
            log.AppendLine("# Neo Space listening pack");
            log.AppendLine();
            log.AppendLine($"Sources, at {Bpm:0} BPM in 4/4, {Bars} bars each ({Bars * 4 * Beat / Rate:0.0} s) and then nothing, so that the tail is heard to its end in a file of {Length / Rate} s:");
            log.AppendLine("- **drums**: a Neo Drums loop (\"Classic — Punchy Kit\": kick, snare, closed and open hat);");
            log.AppendLine("- **plucks**: a Neo Poly pluck phrase (\"Pluck — Bright\", an A minor figure).");
            log.AppendLine("Both at -3 dBFS peak before Neo Space. Every file: 48 kHz, 16-bit, stereo WAV, the machine at one factory preset, the whole signal through it (as an insert, with the preset's Mix).");
            log.AppendLine("A tail longer than the file (the dark long tail) is cut by the end of the file. Click detector (second difference against its local mean) on every file: clicks the source itself does not have.");
            log.AppendLine();
            log.AppendLine("| File | Preset | Source | What to listen for | Reverb vs dry | Peak | RMS | Tail to -60 dB | Clicks |");
            log.AppendLine("|---|---|---|---|---|---|---|---|---|");
            int index = 0, clicksTotal = 0;
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Space"))
            {
                index++;
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                bool d = onDrums.Contains(p.Preset);
                var (l, r) = d ? drums : plucks;
                var e = Engine(v);
                var (ol, or) = Run(e, l, r);
                // Clicks the source does not have: in the processed file, clicks away from those of the dry file (by position).
                var dryClicks = new HashSet<int>(ClickDetector.Find(l).Concat(ClickDetector.Find(r)));
                var clicks = ClickDetector.Find(ol).Concat(ClickDetector.Find(or)).Where(c => !dryClicks.Any(x => Math.Abs(x - c) < 64)).ToList();
                clicksTotal += clicks.Count;
                double pk = 0, sq = 0, wet = 0, dry = 0;
                float mix = (float)v.Value(P.Mix);
                for (int i = 0; i < Length; i++)
                {
                    pk = Math.Max(pk, Math.Max(Math.Abs(ol[i]), Math.Abs(or[i])));
                    sq += 0.5 * ((double)ol[i] * ol[i] + (double)or[i] * or[i]);
                    double w = ol[i] - l[i] * (1 - mix);
                    wet += w * w;
                    dry += (double)l[i] * l[i];
                }
                // The tail: the last time the output is above -60 dBFS.
                int last = 0;
                for (int i = Length - 1; i >= 0; i--) if (Math.Abs(ol[i]) > 0.001f || Math.Abs(or[i]) > 0.001f) { last = i; break; }
                string file = $"{index:00}-{Slug(p.Preset)}.wav";
                WriteWav(Path.Combine(dir, file), ol, or);
                log.AppendLine($"| {file} | {p.Preset} | {(d ? "drums" : "plucks")} | {Comments.GetValueOrDefault(p.Preset, "")} | {10 * Math.Log10(wet / dry):0.0} dB | {20 * Math.Log10(pk):0.0} dBFS | {10 * Math.Log10(sq / Length):0.0} dBFS | {last / (double)Rate:0.0} s | {clicks.Count} |");
            }
            log.AppendLine();
            log.AppendLine($"Click detector, all files: {clicksTotal} new clicks.");
            File.WriteAllText(Path.Combine(dir, "README.md"), log.ToString());
            TestContext.Out.WriteLine(log.ToString());
            Assert.That(clicksTotal, Is.EqualTo(0));
        }
    }
}
