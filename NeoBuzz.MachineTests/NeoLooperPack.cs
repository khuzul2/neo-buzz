using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Looper;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo Looper listening pack (docs/specs/neo-looper.md): every factory preset plays the same performance on one source, a Neo Drums loop with a Neo Poly
    /// chord loop under it at 120 BPM (8 bars, 16 s): record a bar, play it, overdub, multiply, undo and redo, overdub over the longer loop, undo and redo, stop.
    /// Written as 16-bit 48 kHz WAV files with a README. Local only, never committed: run it by hand with NEO_LOOPER_PACK set to the output folder.
    ///   NEO_LOOPER_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~NeoLooperPack"
    /// </summary>
    [TestFixture]
    public class NeoLooperPack
    {
        const int Rate = LooperRig.Rate;
        const int Bar = 96000;
        const int Length = Bar * 8;

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
            while (dir != null && !File.Exists(Path.Combine(dir, "Machines", "NeoLooper", "Presets.txt"))) dir = Path.GetDirectoryName(dir);
            if (dir == null) return map;
            string name = null;
            foreach (var line in File.ReadAllLines(Path.Combine(dir, "Machines", "NeoLooper", "Presets.txt")))
            {
                if (line.StartsWith("preset \"")) name = line.Substring(8).TrimEnd('"');
                else if (name != null && line.StartsWith("comment \"")) map[name] = line.Substring(9).TrimEnd('"');
            }
            return map;
        }

        static string Slug(string s) =>
            new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

        /// <summary>Clicks the output has that the source has not (the source's own are removed by position).</summary>
        static List<int> NewClicks(float[] source, float[] output)
        {
            var own = ClickDetector.Find(source);
            return ClickDetector.Find(output).Where(c => !own.Any(o => Math.Abs(o - c) < 480)).ToList();
        }

        [Test, Explicit("writes the listening pack: set NEO_LOOPER_PACK to an output folder")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_LOOPER_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_LOOPER_PACK");
            Directory.CreateDirectory(dir);
            var (dl, dr) = LooperRig.DrumsSource(Length);
            var (cl, cr) = LooperRig.ChordSource(Length);
            var l = new float[Length];
            var r = new float[Length];
            for (int i = 0; i < Length; i++) { l[i] = dl[i] + 0.45f * cl[i]; r[i] = dr[i] + 0.45f * cr[i]; }
            double peak = 1e-9;
            for (int i = 0; i < Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            float g = (float)(Math.Pow(10, -3.0 / 20) / peak);              // -3 dBFS
            for (int i = 0; i < Length; i++) { l[i] *= g; r[i] *= g; }

            var log = new StringBuilder();
            log.AppendLine("# Neo Looper listening pack");
            log.AppendLine();
            log.AppendLine("Source: a four-bar drum loop (Neo Drums, \"Classic — Punchy Kit\") with a four-chord loop (Neo Poly, \"Pad — Warm Strings\") under it, 120 BPM in 4/4,");
            log.AppendLine($"played twice ({Length / Bar} bars, {Length / (double)Rate:0.0} s), at -3 dBFS peak before Neo Looper. Every file: 48 kHz, 16-bit, stereo WAV, one factory preset each.");
            log.AppendLine();
            log.AppendLine("Every file plays the same performance (a bar is 2.0 s; the presses are the ones a pad sends, just before the bar line; in a Sync preset each lands on the bar):");
            log.AppendLine();
            log.AppendLine("| Time | What happens |");
            log.AppendLine("|---|---|");
            foreach (var (t, what) in new[]
            {
                ("0.0 s", "Record: the first take starts (Free presets: at once)"),
                ("2.0 s", "Record: the take closes (one bar) and the loop plays"),
                ("4.0 s", "Record: overdub starts (the drums and chords are laid on top)"),
                ("6.0 s", "Record: overdub ends"),
                ("8.0 s", "Multiply (x2, or the preset's own factor): the loop is two bars, its content repeated"),
                ("8.4 s", "Undo: back to the one-bar loop"),
                ("9.0 s", "Redo: the two-bar loop again"),
                ("10.0 s", "Record: overdub over the longer loop"),
                ("12.0 s", "Record: overdub ends"),
                ("12.4 s", "Undo: the last pass is gone"),
                ("13.0 s", "Redo: the pass is back"),
                ("14.0 s", "Stop: the loop fades out (the input is heard to the end where Monitor is on)"),
            }) log.AppendLine($"| {t} | {what} |");
            log.AppendLine();
            log.AppendLine("The loop is the source played back by the looper, so what you hear is the same music a few bars later: in the overdubs it is doubled, in the multiply it is the same bar twice. A preset with the monitor off (Octave Down Wash) lets you hear only the loop.");
            log.AppendLine("Click detector (second difference against its local mean) on every file, left and right: clicks that the source itself does not have.");
            log.AppendLine();
            log.AppendLine("| File | Preset | What it sets | Peak | RMS | Clicks |");
            log.AppendLine("|---|---|---|---|---|---|");
            int index = 0, clicksTotal = 0;
            var comments = Comments();
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Looper"))
            {
                index++;
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var (ol, or) = LooperRig.Render(LooperRig.Engine(v), v, l, r, ChopRig.Constant(120), 256, LooperRig.Script(Bar));
                var clicks = NewClicks(l, ol).Concat(NewClicks(r, or)).ToList();
                clicksTotal += clicks.Count;
                double pk = 0, sum = 0;
                for (int i = 0; i < Length; i++)
                {
                    pk = Math.Max(pk, Math.Max(Math.Abs(ol[i]), Math.Abs(or[i])));
                    sum += (double)ol[i] * ol[i] + (double)or[i] * or[i];
                }
                double rms = Math.Sqrt(sum / (2.0 * Length));
                string file = $"{index:00}-{Slug(p.Preset)}.wav";
                WriteWav(Path.Combine(dir, file), ol, or);
                log.AppendLine($"| {file} | {p.Preset} | {comments.GetValueOrDefault(p.Preset, "")} | {20 * Math.Log10(pk):0.0} dBFS | {20 * Math.Log10(rms):0.0} dBFS | {clicks.Count} |");
            }
            log.AppendLine();
            log.AppendLine($"Click detector, all files: {clicksTotal} clicks.");
            File.WriteAllText(Path.Combine(dir, "README.md"), log.ToString());
            Assert.That(clicksTotal, Is.EqualTo(0));
        }
    }
}
