using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Shift;
using static NeoBuzz.MachineTests.ShiftRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo Shift listening pack (docs/specs/neo-shift.md): every factory preset on a lead phrase (Neo Poly), a drum loop (Neo Drums) or a held
    /// chord pad (Neo Poly), 15 s at 120 BPM, the first 4 s dry and then through the preset (a 40 ms crossfade at 4 s), written as 16-bit 48 kHz WAV
    /// files with a README. The engine's latency is taken out (a host compensates it). Local only, never committed: run it by hand with
    /// NEO_SHIFT_PACK set to the output folder.
    ///   NEO_SHIFT_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~NeoShiftPack"
    /// </summary>
    [TestFixture]
    public class NeoShiftPack
    {
        const int Seconds = 15, DrySeconds = 4;
        static readonly int Length = Rate * Seconds;
        static readonly int Beat = Rate / 2;         // 120 BPM

        static ParamValues PresetValues(ParamDef[] table, string machine, string preset)
        {
            var found = PresetTests.AllPresets().First(p => p.Machine == machine && p.Preset == preset);
            var v = new ParamValues(table);
            foreach (var kv in found.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        static (float[] L, float[] R) Run(Action<float[], float[], int> process, Action<int> atBlock)
        {
            var ol = new float[Length]; var or = new float[Length];
            var l = new float[256]; var r = new float[256];
            for (int pos = 0; pos < Length; pos += 256)
            {
                atBlock(pos);
                int n = Math.Min(256, Length - pos);
                Array.Clear(l, 0, 256); Array.Clear(r, 0, 256);
                process(l, r, n);
                Array.Copy(l, 0, ol, pos, n); Array.Copy(r, 0, or, pos, n);
            }
            return (ol, or);
        }

        // A lead phrase: eighth notes (A minor), one voice. Returns the notes as (sample, on, key) too, for Follow notes.
        static readonly int[] Phrase = { 69, 72, 76, 74, 72, 69, 67, 69, 72, 71, 69, 64, 67, 69, 72, 76 };

        static (float[], float[], List<(int, bool, int)>) LeadSource()
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", "Lead — Smooth Saw");
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            var notes = new List<(int, bool, int)>();
            int step = Beat / 2;
            for (int s = 0; s * step < Length; s++)
            {
                if (s % 8 == 7) continue;
                notes.Add((s * step, true, Phrase[s % Phrase.Length]));
                notes.Add((s * step + (int)(step * 0.85), false, Phrase[s % Phrase.Length]));
            }
            var (l, r) = Run((a, b, n) => e.Process(a, b, n), pos =>
            {
                foreach (var (at, on, key) in notes)
                    if (at >= pos && at < pos + 256) { if (on) e.NoteOn(0, key, 0.85f); else e.NoteOff(0); }
            });
            return (l, r, notes);
        }

        static (float[], float[]) DrumSource()
        {
            var v = PresetValues(NeoBuzz.Drums.Params.Table, "Neo Drums", "Classic — Punchy Kit");
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Rate);
            int sixteenth = Beat / 4;
            return Run((a, b, n) => e.Process(a, b, n), pos =>
            {
                for (int s = 0; s * sixteenth < Length; s++)
                {
                    int at = s * sixteenth;
                    if (at < pos || at >= pos + 256) continue;
                    int k = s % 16;
                    if (k == 0 || k == 8 || k == 10) e.NoteOn(0, 36, k == 0 ? 0.95f : 0.8f);
                    if (k == 4 || k == 12) e.NoteOn(1, 38, 0.9f);
                    if (k % 2 == 0) e.NoteOn(2, 42, k % 4 == 0 ? 0.8f : 0.5f);
                    if (k == 14) e.NoteOn(3, 46, 0.6f);
                }
            });
        }

        static (float[], float[]) PadSource()
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", "Pad — Warm Strings");
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            int[][] chords = { new[] { 45, 52, 57, 60 }, new[] { 41, 48, 57, 60 }, new[] { 48, 52, 55, 60 }, new[] { 43, 50, 55, 59 } };
            int bar = Beat * 4;
            return Run((a, b, n) => e.Process(a, b, n), pos =>
            {
                for (int c = 0; c * bar < Length; c++)
                {
                    if (c * bar >= pos && c * bar < pos + 256) for (int k = 0; k < 4; k++) e.NoteOn(k, chords[c % 4][k], 0.7f);
                    int off = c * bar + (int)(bar * 0.92);
                    if (off >= pos && off < pos + 256) for (int k = 0; k < 4; k++) e.NoteOff(k);
                }
            });
        }

        static void Normalise(float[] l, float[] r, double peakDb)
        {
            double peak = 1e-9;
            for (int i = 0; i < l.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            float g = (float)(Math.Pow(10, peakDb / 20) / peak);
            for (int i = 0; i < l.Length; i++) { l[i] *= g; r[i] *= g; }
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

        static Dictionary<string, string> Comments()
        {
            var map = new Dictionary<string, string>();
            string dir = TestContext.CurrentContext.TestDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Machines", "NeoShift", "Presets.txt"))) dir = Path.GetDirectoryName(dir);
            if (dir == null) return map;
            string name = null;
            foreach (var line in File.ReadAllLines(Path.Combine(dir, "Machines", "NeoShift", "Presets.txt")))
            {
                if (line.StartsWith("preset \"")) name = line.Substring(8).TrimEnd('"');
                else if (name != null && line.StartsWith("comment \"")) map[name] = line.Substring(9).TrimEnd('"');
            }
            return map;
        }

        static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

        static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-12));

        [Test, Explicit("writes the listening pack: set NEO_SHIFT_PACK to an output folder")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_SHIFT_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_SHIFT_PACK");
            Directory.CreateDirectory(dir);
            var (leadL, leadR, notes) = LeadSource();
            var (drumL, drumR) = DrumSource();
            var (padL, padR) = PadSource();
            Normalise(leadL, leadR, -3); Normalise(drumL, drumR, -3); Normalise(padL, padR, -3);
            var sources = new Dictionary<string, (float[] L, float[] R, string Name)>
            {
                ["lead"] = (leadL, leadR, "a lead phrase (Neo Poly, \"Lead — Smooth Saw\", A minor eighths)"),
                ["drums"] = (drumL, drumR, "a drum loop (Neo Drums, \"Classic — Punchy Kit\")"),
                ["pad"] = (padL, padR, "a held chord pad (Neo Poly, \"Pad — Warm Strings\", Am F C G)"),
            };
            var plan = new (string Preset, string Source)[]
            {
                ("Pitch — Octave Down", "lead"), ("Pitch — Fifth Up", "lead"), ("Pitch — Wide Double", "pad"),
                ("Frequency — Rising Barber Pole", "drums"), ("Frequency — Gentle Wide Shift", "pad"),
                ("Ring — Metal Bell", "lead"), ("Ring — Slow Throb", "pad"), ("Ring — Note Ring", "lead"),
            };
            var comments = Comments();
            var log = new StringBuilder();
            log.AppendLine("# Neo Shift listening pack");
            log.AppendLine();
            log.AppendLine($"Three sources at 120 BPM, {Seconds} s each, at -3 dBFS peak before Neo Shift. Every file: 48 kHz, 16-bit, stereo WAV. The first {DrySeconds} s are the dry source, then the preset (a 40 ms crossfade at {DrySeconds} s), and the last 150 ms fade out. Neo Shift's latency is taken out of the wet part, as a host does. \"Ring — Note Ring\" has Follow notes on and is sent the lead's notes.");
            log.AppendLine();
            log.AppendLine("Click detector (second difference against its local mean) on every file: 0 clicks unless the source has them.");
            log.AppendLine();
            log.AppendLine("| File | Preset | Source | What to listen for | Peak | RMS | Clicks |");
            log.AppendLine("|---|---|---|---|---|---|---|");
            int index = 0, clicksTotal = 0;
            foreach (var (preset, src) in plan)
            {
                index++;
                var (sl, sr, srcName) = sources[src];
                var v = PresetValues(Params.Table, "Neo Shift", preset);
                var e = Engine(v);
                var (wl, wr) = Render(e, sl, sr, 256, null, src == "lead" && preset.Contains("Note Ring") ? notes : null);
                int lat = e.Latency, xf = (int)(0.04 * Rate), t0 = DrySeconds * Rate;
                var ol = new float[Length]; var or = new float[Length];
                for (int i = 0; i < Length; i++)
                {
                    float dl = sl[i], dr = sr[i];
                    float xl = i + lat < Length ? wl[i + lat] : 0, xr = i + lat < Length ? wr[i + lat] : 0;
                    float w = i < t0 ? 0f : i >= t0 + xf ? 1f : (float)(0.5 - 0.5 * Math.Cos(Math.PI * (i - t0) / xf));
                    ol[i] = dl * (1 - w) + xl * w; or[i] = dr * (1 - w) + xr * w;
                }
                // The last 150 ms fade out (the wet part ends a latency early).
                for (int i = Length - Rate * 15 / 100; i < Length; i++) { float g = (float)(Length - 1 - i) / (Rate * 15 / 100); ol[i] *= g; or[i] *= g; }
                // The wet part only decides the numbers; the first seconds are the source.
                double pk = 0, sq = 0;
                for (int i = t0; i < Length; i++) { pk = Math.Max(pk, Math.Max(Math.Abs(ol[i]), Math.Abs(or[i]))); sq += (double)ol[i] * ol[i] + (double)or[i] * or[i]; }
                double rms = Math.Sqrt(sq / (2.0 * (Length - t0)));
                int clicks = ClickDetector.Find(ol).Count + ClickDetector.Find(or).Count;
                int srcClicks = ClickDetector.Find(sl).Count + ClickDetector.Find(sr).Count;
                clicksTotal += Math.Max(0, clicks - srcClicks);
                string file = $"{index:00}-{Slug(preset)}__{src}.wav";
                WriteWav(Path.Combine(dir, file), ol, or);
                comments.TryGetValue(preset, out string what);
                log.AppendLine($"| {file} | {preset} | {srcName} | {what} | {Db(pk):0.0} dBFS | {Db(rms):0.0} dBFS | {clicks} (source {srcClicks}) |");
                if (clicks > 0) TestContext.Out.WriteLine($"  clicks at {string.Join(", ", ClickDetector.Find(ol).Select(c => $"L{c} ({c / (double)Rate:0.000} s)").Concat(ClickDetector.Find(or).Select(c => $"R{c} ({c / (double)Rate:0.000} s)")))}");
                TestContext.Out.WriteLine($"{file}: peak {Db(pk):0.0} dBFS, rms {Db(rms):0.0} dBFS, clicks {clicks} (source {srcClicks})");
            }
            log.AppendLine();
            log.AppendLine($"New clicks over the source, all files: {clicksTotal}.");
            File.WriteAllText(Path.Combine(dir, "README.md"), log.ToString());
            Assert.That(clicksTotal, Is.EqualTo(0));
        }
    }
}
