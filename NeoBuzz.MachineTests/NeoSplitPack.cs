using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Split;
using static NeoBuzz.MachineTests.SplitRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo Split listening pack (docs/specs/neo-split.md): one 15 s file per factory preset, 48 kHz, 16-bit, stereo, 120 BPM. Each file is the input for
    /// 3 s, then each used output soloed in turn (an equal share of the next 10 s), then the outputs summed for 2 s (it should sound like the input); the
    /// parts are joined by 20 ms crossfades. Bands and Mid / Side play a small mix (a Neo Drums loop, a Neo Poly bass, a pad widened to twice its side and a lead to
    /// the right), Transient / Body the drums and the pad. A README gives the time map, what to listen for and each part's peak and RMS. Local only, never
    /// committed: run it by hand with NEO_SPLIT_PACK set to the output folder.
    ///   NEO_SPLIT_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~NeoSplitPack"
    /// </summary>
    [TestFixture]
    public class NeoSplitPack
    {
        const int Length = 15 * Rate;
        const int Fade = Rate / 50;                  // 20 ms
        const int InputEnd = 3 * Rate;
        const int SoloEnd = 13 * Rate;

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
                while (dir != null && !File.Exists(Path.Combine(dir, "Machines", "NeoSplit", "Presets.txt"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) return map;
                string name = null;
                foreach (var line in File.ReadAllLines(Path.Combine(dir, "Machines", "NeoSplit", "Presets.txt")))
                {
                    if (line.StartsWith("preset \"")) name = line.Substring(8).TrimEnd('"');
                    else if (name != null && line.StartsWith("comment \"")) map[name] = line.Substring(9).TrimEnd('"');
                }
                return map;
            }
        }

        static string Slug(string s) =>
            new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

        static void Normalise((float[] L, float[] R) x, double dbfs)
        {
            double peak = 1e-9;
            for (int i = 0; i < x.L.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(x.L[i]), Math.Abs(x.R[i])));
            float g = (float)(Math.Pow(10, dbfs / 20) / peak);
            for (int i = 0; i < x.L.Length; i++) { x.L[i] *= g; x.R[i] *= g; }
        }

        static (double Peak, double Rms) Level(float[] l, float[] r, int from, int to)
        {
            double pk = 0, sq = 0;
            for (int i = from; i < to; i++)
            {
                pk = Math.Max(pk, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
                sq += 0.5 * ((double)l[i] * l[i] + (double)r[i] * r[i]);
            }
            return (20 * Math.Log10(pk + 1e-12), 10 * Math.Log10(sq / (to - from) + 1e-24));
        }

        static bool Smooth(int c, List<(string Name, float[] L, float[] R, int From, int To)> parts, List<int> joins)
        {
            var part = parts.FirstOrDefault(x => c >= x.From && c < x.To);
            if (part.L == null || part.Name == "input" || part.Name == "sum") return false;
            var input = parts[0];
            double prev = double.NaN, step = 0;
            for (int i = c - 3; i <= c + 3; i++)
            {
                if (Math.Abs(input.L[i]) < 0.002f) return false;
                double g = part.L[i] / input.L[i];
                if (!double.IsNaN(prev)) step = Math.Max(step, Math.Abs(g - prev));
                prev = g;
            }
            return step <= 0.05;
        }

        [Test, Explicit("writes the listening pack: set NEO_SPLIT_PACK to an output folder")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_SPLIT_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_SPLIT_PACK");
            Directory.CreateDirectory(dir);
            var mix = Mix(Length);
            var drumsPad = DrumsAndPad(Length);
            Normalise(mix, -3);
            Normalise(drumsPad, -3);

            var log = new StringBuilder();
            log.AppendLine("# Neo Split listening pack");
            log.AppendLine();
            log.AppendLine($"Sources, at {Bpm:0} BPM, {Length / Rate} s each, 48 kHz, at -3 dBFS peak before Neo Split:");
            log.AppendLine("- **mix** (Bands and Mid / Side): a Neo Drums loop, a Neo Poly bass, a Neo Poly pad widened to twice its side, a lead to the right;");
            log.AppendLine("- **drums and pad** (Transient / Body): the Neo Drums loop and the pad.");
            log.AppendLine();
            log.AppendLine("Every file: 16-bit stereo WAV with the machine at one factory preset and every output of it heard with its own gain at unity. Time map of every file (20 ms crossfades between the parts):");
            log.AppendLine($"- 0 to 3 s: the input (in Transient / Body, delayed by the machine's look-ahead so that it lines up with the outputs);");
            log.AppendLine("- 3 to 13 s: each used output soloed in turn, an equal share of the 10 s (two outputs: 5 s each; three: 3.33 s; four: 2.5 s), in the order Out 1, Out 2 ...;");
            log.AppendLine("- 13 to 15 s: the outputs summed: it should sound like the input.");
            log.AppendLine();
            log.AppendLine("Click detector (second difference against its local mean) on every file, against the clicks the input itself has: clicks within a crossfade (20 ms either side of a join) are counted apart from the rest.");
            log.AppendLine();
            log.AppendLine("| File | Preset | Source | What to listen for | Parts: peak / RMS (dBFS) | Clicks away from the joins | Clicks at the joins | Source's own |");
            log.AppendLine("|---|---|---|---|---|---|---|---|");
            int index = 0, clicksTotal = 0, joinTotal = 0, ownTotal = 0;
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Split"))
            {
                index++;
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var mode = (SplitEngine.Mode)v.Choice(P.Mode);
                var src = mode == SplitEngine.Mode.TransientBody ? drumsPad : mix;
                var e = Engine(v);
                var (ol, or) = Run(e, src.L, src.R);
                int lat = e.Latency;
                var names = SplitDesign.OutputNames(mode, (int)Math.Round(v.Value(P.Bands)));
                var used = Enumerable.Range(0, 4).Where(o => names[o].Length > 0).ToList();

                // Every part as a full-length signal, then the joined file: the part's own signal inside its window, a 20 ms crossfade at each join.
                var parts = new List<(string Name, float[] L, float[] R, int From, int To)>();
                var dl = new float[Length];
                var dr = new float[Length];
                for (int i = 0; i + lat < Length; i++) { dl[i + lat] = src.L[i]; dr[i + lat] = src.R[i]; }
                parts.Add(("input", dl, dr, 0, InputEnd));
                int share = (SoloEnd - InputEnd) / used.Count;
                for (int k = 0; k < used.Count; k++)
                    parts.Add((names[used[k]], ol[used[k]], or[used[k]], InputEnd + k * share, k == used.Count - 1 ? SoloEnd : InputEnd + (k + 1) * share));
                var sl = Sum(ol);
                var sr = Sum(or);
                parts.Add(("sum", sl, sr, SoloEnd, Length));

                var l = new float[Length];
                var r = new float[Length];
                var joins = new List<int>();
                for (int k = 0; k < parts.Count; k++)
                {
                    var (_, pl, pr, from, to) = parts[k];
                    if (k > 0) joins.Add(from);
                    for (int i = from; i < to; i++) { l[i] = pl[i]; r[i] = pr[i]; }
                }
                // Crossfade: around each join the two neighbouring parts, 20 ms, the first fading out over the join and the second in.
                for (int k = 1; k < parts.Count; k++)
                {
                    int j = parts[k].From;
                    var a = parts[k - 1];
                    var b = parts[k];
                    for (int i = j - Fade / 2; i < j + Fade / 2; i++)
                    {
                        float t = (i - (j - Fade / 2)) / (float)Fade;
                        l[i] = (1 - t) * a.L[i] + t * b.L[i];
                        r[i] = (1 - t) * a.R[i] + t * b.R[i];
                    }
                }
                var dryClicks = new HashSet<int>(ClickDetector.Find(dl).Concat(ClickDetector.Find(dr)));
                var found = ClickDetector.Find(l).Concat(ClickDetector.Find(r)).Where(c => !dryClicks.Any(x => Math.Abs(x - c) < 64)).ToList();
                int atJoins = found.Count(c => joins.Any(j => Math.Abs(c - j) <= Fade));
                // A click the detector finds in a solo part where the output is a smooth gain of the input (the ratio of the two moves by at most 0.05 a sample over 7 samples) is the
                // source's own (the detector's threshold follows the local level, so a quieter copy of a noisy hit can trip it); it is counted apart.
                int sourceOwn = found.Count(c => !joins.Any(j => Math.Abs(c - j) <= Fade) && Smooth(c, parts, joins));
                int away = found.Count - atJoins - sourceOwn;
                clicksTotal += away;
                joinTotal += atJoins;
                ownTotal += sourceOwn;

                string file = $"{index:00}-{Slug(p.Preset)}.wav";
                WriteWav(Path.Combine(dir, file), l, r);
                var levels = parts.Select(x => { var (pk, rms) = Level(x.L, x.R, x.From, x.To); return $"{x.Name} {pk:0.0} / {rms:0.0}"; });
                log.AppendLine($"| {file} | {p.Preset} | {(mode == SplitEngine.Mode.TransientBody ? "drums and pad" : "mix")} | {Comments.GetValueOrDefault(p.Preset, "")} | {string.Join("; ", levels)} | {away} | {atJoins} | {sourceOwn} |");
            }
            log.AppendLine();
            log.AppendLine($"Click detector, all files: {clicksTotal} clicks away from the joins, {joinTotal} at the joins (the crossfades), {ownTotal} the source's own (a smooth gain of the input).");
            File.WriteAllText(Path.Combine(dir, "README.md"), log.ToString());
            TestContext.Out.WriteLine(log.ToString());
            Assert.That(clicksTotal, Is.EqualTo(0));
        }
    }
}
