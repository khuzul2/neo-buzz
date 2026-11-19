// Neo Tune quality gates on real singing (docs/specs/neo-tune.md section 11): NT1 (the engine as it shipped, frozen in TuneNt1/) against NT2 (the
// machine's engine, linked) on the VocalSet subset, with the measurements NT1's own evaluation did not make (vibrato kept, jitter and shimmer,
// long-term spectrum, sub-harmonics, bursts, voicing, note sequence, null). NT1 is run with the three settings the first evaluation used
// (hard, natural, subtle), written as that evaluation wrote them (Humanize 40 and Amount 55 went through Raw() and were clamped to 100 %, which
// is what the machine did too); NT2 at Natural 0, 50 and 100. The listening excerpts (the same six files and offsets as NT1's pack) are written here.

using System.IO.Compression;
using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Quality;

namespace NeoBuzz.PitchEval
{
    internal static class TuneGates
    {
        internal sealed class Variant
        {
            public string Name, Key;
            public int Nt1;          // 0 = NT2; 1 hard, 2 natural, 3 subtle (NT1 as evaluated)
            public double Natural;
        }

        static readonly Variant[] Variants =
        {
            new() { Name = "NT1 hard", Key = "nt1-hard", Nt1 = 1 },
            new() { Name = "NT1 natural", Key = "nt1-natural", Nt1 = 2 },
            new() { Name = "NT1 subtle", Key = "nt1-subtle", Nt1 = 3 },
            new() { Name = "NT2 Natural 0", Key = "nt2-natural-000", Natural = 0.0 },
            new() { Name = "NT2 Natural 50", Key = "nt2-natural-050", Natural = 0.5 },
            new() { Name = "NT2 Natural 100", Key = "nt2-natural-100", Natural = 1.0 },
        };

        internal static float[] Render(float[] x, int rate, Variant v, int scale = 0)
        {
            if (v.Nt1 != 0) return RenderNt1(x, rate, v.Nt1);
            var pv = new ParamValues(NeoBuzz.Tune.Params.Table);
            pv.Set(NeoBuzz.Tune.P.Amount, NeoBuzz.Tune.Params.Table[NeoBuzz.Tune.P.Amount].Raw(100));
            NeoBuzz.Tune.TuneNatural.Apply(pv, v.Natural);
            pv.Set(NeoBuzz.Tune.P.Range, 1);
            pv.Set(NeoBuzz.Tune.P.Scale, scale);
            var e = new NeoBuzz.Tune.TuneEngine(pv);
            e.SetSampleRate(rate);
            return Blocks(x, e.Latency, (l, r, n) => e.Process(l, r, n));
        }

        static float[] RenderNt1(float[] x, int rate, int preset)
        {
            var pv = new ParamValues(NeoBuzz.TuneNt1.Params.Table);
            void Set(int p, double value) => pv.Set(p, NeoBuzz.TuneNt1.Params.Table[p].Raw(value));
            Set(NeoBuzz.TuneNt1.P.Amount, preset == 3 ? 55 : 100);
            Set(NeoBuzz.TuneNt1.P.Retune, preset == 1 ? 0 : preset == 2 ? 60 : 120);
            Set(NeoBuzz.TuneNt1.P.Humanize, preset == 1 ? 0 : preset == 2 ? 40 : 60);
            Set(NeoBuzz.TuneNt1.P.VibKeep, preset == 1 ? 0 : 100);
            pv.Set(NeoBuzz.TuneNt1.P.Range, 1);
            pv.Set(NeoBuzz.TuneNt1.P.Scale, 0);
            var e = new NeoBuzz.TuneNt1.TuneEngine(pv);
            e.SetSampleRate(rate);
            return Blocks(x, e.Latency, (l, r, n) => e.Process(l, r, n));
        }

        static float[] Blocks(float[] x, int latency, Action<float[], float[], int> process)
        {
            int total = x.Length + latency + 256;
            var o = new float[x.Length];
            var l = new float[256]; var r = new float[256];
            for (int pos = 0; pos < total; pos += 256)
            {
                int n = Math.Min(256, total - pos);
                for (int i = 0; i < n; i++) l[i] = r[i] = pos + i < x.Length ? x[pos + i] : 0f;
                process(l, r, n);
                for (int i = 0; i < n; i++) { int at = pos + i - latency; if (at >= 0 && at < o.Length) o[at] = l[i]; }
            }
            return o;
        }

        // ------------------------------------------------------------------------------------------------ one file

        sealed class M
        {
            public double Vib = double.NaN, Jit = double.NaN, Shim = double.NaN, Worst = double.NaN, High = double.NaN, Sub = double.NaN;
            public double Bursts = double.NaN, Kept = double.NaN, Changes = double.NaN, Centre = double.NaN, Null = double.NaN;
        }

        static M Measure(float[] x, float[] y, int rate, QTrack ti, bool inputItself)
        {
            var m = new M();
            var to = inputItself ? ti : QTrack.Run(y, rate);
            m.Vib = TuneQuality.VibratoExtent(to);
            var (j, s) = TuneQuality.JitterShimmer(y, rate, to);
            m.Jit = j; m.Shim = s;
            var changes = TuneQuality.NoteChanges(ti, out _);
            m.Bursts = TuneQuality.Bursts(y, rate, to, changes).Total;
            TuneQuality.NoteChanges(to, out var notes);
            m.Changes = TuneQuality.Changes(notes);
            m.Centre = TuneQuality.CentreError(to);
            if (!inputItself)
            {
                var (diff, hin, hout) = TuneQuality.Ltas(x, y, rate, ti);
                m.Worst = diff.Length == 0 ? double.NaN : diff.Select(Math.Abs).Max();
                m.High = hout - hin;
                m.Sub = TuneQuality.Subharmonic(y, rate, to) - TuneQuality.Subharmonic(x, rate, ti);
                m.Kept = TuneQuality.VoicedKept(ti, to);
                double e = 0, r = 0;
                for (int i = rate / 2; i < x.Length; i++) { double d = y[i] - x[i]; e += d * d; r += (double)x[i] * x[i]; }
                m.Null = r > 0 && e > 0 ? 10 * Math.Log10(r / e) : double.NaN;
            }
            return m;
        }

        static double Mean(IEnumerable<double> v) { var l = v.Where(d => !double.IsNaN(d) && !double.IsInfinity(d)).ToList(); return l.Count == 0 ? double.NaN : l.Average(); }

        // ------------------------------------------------------------------------------------------------ the run

        public static void Run(List<(ZipArchiveEntry E, string Group, string Singer, string Vowel)> selected, StringBuilder md)
        {
            // Long tones for the transparency numbers, vibrato arpeggios for expression and target flips, scales and excerpts for the rest.
            string[] groups = { "long tones, straight", "scales, straight", "arpeggios, straight", "arpeggios, vibrato", "excerpts, straight" };
            var files = selected.Where(t => groups.Contains(t.Group)).ToList();
            var rows = new Dictionary<(string Group, string Var), List<M>>();   // "input" is the unprocessed file
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int Batch = 16;
            for (int b = 0; b < files.Count; b += Batch)
            {
                var chunk = files.Skip(b).Take(Batch).ToList();
                var bytes = new byte[chunk.Count][];
                for (int i = 0; i < chunk.Count; i++)
                {
                    using var s = chunk[i].E.Open();
                    var ms = new MemoryStream((int)chunk[i].E.Length);
                    s.CopyTo(ms);
                    bytes[i] = ms.ToArray();
                }
                var res = new List<(string Var, M M)>[chunk.Count];
                Parallel.For(0, chunk.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, i =>
                {
                    var (x, rate) = Wav.Read(new MemoryStream(bytes[i]));
                    if (x.Length > rate * 12) Array.Resize(ref x, rate * 12);
                    var ti = QTrack.Run(x, rate);
                    var list = new List<(string, M)> { ("input", Measure(x, x, rate, ti, true)) };
                    foreach (var v in Variants) list.Add((v.Name, Measure(x, Render(x, rate, v), rate, ti, false)));
                    res[i] = list;
                });
                for (int i = 0; i < chunk.Count; i++)
                    foreach (var (v, m) in res[i])
                        foreach (var g in new[] { chunk[i].Group, "**all**" })
                        {
                            if (!rows.TryGetValue((g, v), out var l)) rows[(g, v)] = l = new List<M>();
                            l.Add(m);
                        }
                if ((b / Batch) % 4 == 0) Console.WriteLine($"  gates {Math.Min(b + Batch, files.Count)}/{files.Count} files, {sw.Elapsed.TotalSeconds:F0} s");
            }

            md.AppendLine("## Neo Tune quality gates on VocalSet: NT1 against NT2");
            md.AppendLine();
            md.AppendLine($"{files.Count} recordings (at most 12 s each), chromatic scale, Quality range, the engines as run in 256-sample blocks and aligned by their latency. NT1 is the engine as shipped (frozen copy) with the three settings of its own evaluation (hard, natural, subtle; its Humanize 40 and Amount 55 were clamped to 100 % by the evaluation and by the machine, so \"natural\" and \"subtle\" were Humanize 100 %, Amount 100 %); NT2 at Natural 0, 50 and 100 (the six fine controls set by the Natural map). Means over the files. **Vibrato**: 4-8 Hz extent of the pitch (cents peak to peak, runs of 0.6 s). **Jitter / shimmer**: per-cycle, medians over voiced runs (%). **LTAS**: the worst third-octave difference of the output against the input (125 Hz - 8 kHz, dB), and the change of the band above 5 kHz (dB). **Sub**: change of the sub-harmonic ratio (f0/2, 3f0/2) in dB. **Bursts**: broadband bursts in the output at the sung note changes. **Voiced kept**: share of voiced input frames still voiced. **Changes**: note changes per file in the output (the input's number is the sung one). **Centre**: median distance of the 200 ms mean pitch from the nearest semitone (ct). **Null**: aligned residual of the output against the input (dB below the signal; large means unchanged).");
            md.AppendLine();
            foreach (var g in groups.Append("**all**"))
            {
                if (!rows.ContainsKey((g, "input"))) continue;
                md.AppendLine($"### {g}");
                md.AppendLine();
                md.AppendLine("| Metric | input | " + string.Join(" | ", Variants.Select(v => v.Name)) + " |");
                md.AppendLine("|---|---:|" + string.Concat(Enumerable.Repeat("---:|", Variants.Length)));
                void Row(string name, Func<M, double> f, string fmt, bool input = true)
                {
                    var cells = new List<string> { input ? Fmt.F(Mean(rows[(g, "input")].Select(f)), fmt) : "-" };
                    foreach (var v in Variants) cells.Add(Fmt.F(Mean(rows[(g, v.Name)].Select(f)), fmt));
                    md.AppendLine($"| {name} | " + string.Join(" | ", cells) + " |");
                }
                Row("Vibrato extent (ct)", m => m.Vib, "F1");
                Row("Jitter (%)", m => m.Jit, "F2");
                Row("Shimmer (%)", m => m.Shim, "F2");
                Row("LTAS worst third-octave (dB)", m => m.Worst, "F2", false);
                Row("Band above 5 kHz (dB)", m => m.High, "F2", false);
                Row("Sub-harmonic change (dB)", m => m.Sub, "F2", false);
                Row("Bursts", m => m.Bursts, "F1");
                Row("Voiced kept (%)", m => 100 * m.Kept, "F1", false);
                Row("Note changes per file", m => m.Changes, "F1");
                Row("Centre error (ct)", m => m.Centre, "F1");
                Row("Aligned null (dB)", m => m.Null, "F1", false);
                md.AppendLine();
            }
        }


        // ------------------------------------------------------------------------------------------------ inside the notes, per excerpt

        /// <summary>Per excerpt and setting: the measures inside the notes (TuneNotes), as table rows; and, with <paramref name="csvDir"/>, the pitch tracks for plotting.</summary>
        public static void InNotes(string id, float[] seg, int rate, int nt1Old, StringBuilder md, string csvDir)
        {
            var ti = QTrack.Run(seg, rate);
            var sung = TuneNotes.Segments(ti);
            var viIn = new List<double>();
            double vibIn = TuneNotes.VibratoInside(ti, sung, viIn);
            double ceIn = TuneNotes.CentreError(ti, sung);
            var (flatIn, _) = TuneNotes.Flatness(ti, sung);
            var variants = new List<Variant> { new() { Name = "NT1 old", Key = "nt1", Nt1 = nt1Old } };
            foreach (var n in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }) variants.Add(new Variant { Name = $"NT2 N{n * 100:F0}", Key = $"n{n * 100:F0}", Natural = n });
            var tracks = new List<(string, QTrack)> { ("in", ti) };
            md.AppendLine($"| {id} | input | {sung.Count} / {TuneNotes.Long(sung).Count} | - | {Fmt.F(flatIn)} | - | {Fmt.F(ceIn)} | {Fmt.F(vibIn)} | - |");
            foreach (var v in variants)
            {
                var y = Render(seg, rate, v);
                var to = QTrack.Run(y, rate);
                tracks.Add((v.Key, to));
                var outSegs = TuneNotes.Segments(to);
                if (Environment.GetEnvironmentVariable("NOTES") == v.Key) Console.WriteLine(id + " sung " + string.Join(" ", TuneNotes.Long(sung)) + "\n   out " + string.Join(" ", TuneNotes.Long(outSegs)));
                var (flat, _) = TuneNotes.Flatness(to, outSegs);
                var delays = TuneNotes.StepDelays(ti, to, sung);
                double dmed = delays.Count == 0 ? double.NaN : delays.OrderBy(d => d).ToList()[delays.Count / 2] * 1000;
                double ce = TuneNotes.CentreError(to, sung);
                var viOut = new List<double>();
                double vib = TuneNotes.VibratoInside(to, sung, viOut);
                var ratios = new List<double>();
                for (int k = 0; k < viIn.Count && k < viOut.Count; k++) if (!double.IsNaN(viIn[k]) && !double.IsNaN(viOut[k]) && viIn[k] > 15) ratios.Add(viOut[k] / viIn[k]);
                double kept = ratios.Count == 0 ? double.NaN : 100 * ratios.OrderBy(r => r).ToList()[ratios.Count / 2];
                md.AppendLine($"| {id} | {v.Name} | {outSegs.Count} / {TuneNotes.Long(outSegs).Count} | {Fmt.F(dmed, "F0")} ({delays.Count}) | {Fmt.F(flat)} | {Fmt.F(Mean(delays.Select(d => d * 1000)), "F0")} | {Fmt.F(ce)} | {Fmt.F(vib)} | {Fmt.F(kept, "F0")} |");
            }
            if (csvDir != null)
            {
                Directory.CreateDirectory(csvDir);
                var sb = new StringBuilder("t");
                foreach (var (k, _) in tracks) sb.Append(',').Append(k);
                sb.AppendLine();
                for (double x = 0; x < seg.Length / (double)rate; x += 0.005)
                {
                    sb.Append(x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                    foreach (var (_, t) in tracks)
                    {
                        double c = double.NaN;
                        int i = t.At(x);
                        if (i >= 0 && t.Voiced[i]) c = t.Cents[i];
                        sb.Append(',').Append(double.IsNaN(c) ? "" : (c / 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    sb.AppendLine();
                }
                File.WriteAllText(Path.Combine(csvDir, id + ".csv"), sb.ToString());
                var sg = new StringBuilder("from,to,note\n");
                foreach (var s in sung) sg.AppendLine($"{s.From:F3},{s.To:F3},{s.Note}");
                File.WriteAllText(Path.Combine(csvDir, id + "-sung.csv"), sg.ToString());
            }
        }

        /// <summary>Local use: the originals of a listening pack (a folder of *-original.wav) measured inside the notes, the report to a file and the pitch tracks to a folder.</summary>
        public static void PackDir(string dir, string outMd, string csvDir)
        {
            string[] old = { "hard", "natural", "natural", "hard", "subtle", "natural" };
            var md = new StringBuilder();
            InNotesHeader(md);
            int n = 0;
            foreach (var f in Directory.GetFiles(dir, "*-original.wav").OrderBy(x => x))
            {
                var (x, rate) = Wav.Read(File.OpenRead(f));
                var id = Path.GetFileName(f).Replace("-original.wav", "");
                InNotes(id, x, rate, old[Math.Min(n, 5)] == "hard" ? 1 : old[Math.Min(n, 5)] == "natural" ? 2 : 3, md, csvDir);
                n++;
            }
            File.WriteAllText(outMd, md.ToString());
        }

        public static void InNotesHeader(StringBuilder md)
        {
            md.AppendLine("### Inside the notes, per excerpt");
            md.AppendLine();
            md.AppendLine("Notes are runs of one semitone (150 ms median, a new note after 80 cents past the middle for 100 ms). **notes**: the number found, and the number of notes of at least 250 ms (sung: the input's). **step delay** (ms): the output's crossing of the midpoint between two sung notes against the input's, median (steps measured). **flat** (ct): median over notes of the median distance of the pitch from the note's median (the first 30 ms after a step skipped, notes under 100 ms ignored). **step mean** (ms). **centre error** (ct): the note median from the nearest semitone, per sung note. **vibrato inside** (ct, 4-8 Hz peak to peak inside each sung note) and the share of it kept (median output over input per note).");
            md.AppendLine();
            md.AppendLine("| Excerpt | setting | notes | step delay | flat (ct) | step mean | centre error (ct) | vibrato inside (ct) | kept % |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        }

        // ------------------------------------------------------------------------------------------------ the excerpts

        public static void Excerpts(ZipArchive zip, List<(ZipArchiveEntry E, string Group, string Singer, string Vowel)> selected, string dir, StringBuilder md)
        {
            Directory.CreateDirectory(dir);
            // The six excerpts of NT1's pack: the same files and offsets (the first moment of singing, 8 s at most).
            var plan = new (string Group, string Voice)[]
            {
                ("scales, straight", "female"), ("scales, straight", "male"),
                ("arpeggios, vibrato", "female"), ("arpeggios, vibrato", "male"),
                ("excerpts, straight", "female"), ("excerpts, straight", "male"),
            };
            var readme = new StringBuilder();
            readme.AppendLine("# Neo Tune NT2: listening pack");
            readme.AppendLine();
            readme.AppendLine("Six excerpts of at most 8 seconds from VocalSet 1.1 (the same files and offsets as the NT1 pack), each as the original (`-original.wav`), NT1's output as it was (`-nt1-*.wav`, the setting NT1's pack used for that excerpt) and NT2 at Natural 0, 50 and 100 (`-nt2-natural-000/050/100.wav`). Mono, 16 bit, at the recording's rate, chromatic scale, Quality range, the machine's latency taken out, **every file scaled to the original's RMS** so the levels match. The `blind/` folder holds the same processed files under letters (A, B, C, ...) in a shuffled order per excerpt, the key is in `blind-key/KEY.md`, beside the blind folder (do not open it until you have listened).");
            readme.AppendLine();
            readme.AppendLine("Attribution (Creative Commons Attribution 4.0 International, https://creativecommons.org/licenses/by/4.0/): the recordings are from **VocalSet: A Singing Voice Dataset** by Julia Wilkins, Prem Seetharaman, Alison Wahl and Bryan Pardo (https://zenodo.org/records/1442513), CC BY 4.0. The originals are excerpts (shortened, converted to mono 16 bit); the other files are modified: processed with Neo Tune, part of NeoBuzz Live. No endorsement by the authors is implied.");
            readme.AppendLine();
            readme.AppendLine("| Excerpt | Singer | Technique | From (s) | Length (s) | NT1 setting in the old pack |");
            readme.AppendLine("|---|---|---|---:|---:|---|");
            var key = new StringBuilder("# Key to the blind folder (do not open before listening)\n\n| Excerpt | A | B | C | D | E |\n|---|---|---|---|---|---|\n");
            string[] oldSetting = { "hard", "natural", "natural", "hard", "subtle", "natural" };
            Directory.CreateDirectory(Path.Combine(dir, "blind")); Directory.CreateDirectory(Path.Combine(dir, "blind-key"));
            int n = 0;
            var rng = new Random(2026);
            var notesMd = new StringBuilder();
            InNotesHeader(notesMd);
            foreach (var (group, voice) in plan)
            {
                var pick = selected.FirstOrDefault(t => t.Group == group && t.Singer.StartsWith(voice) && !(voice == "male" && t.Singer.StartsWith("female")));
                if (pick.E == null) continue;
                using var s = pick.E.Open();
                var ms = new MemoryStream((int)pick.E.Length);
                s.CopyTo(ms);
                var (x, rate) = Wav.Read(new MemoryStream(ms.ToArray()));
                int start = 0, win = rate / 20;
                for (int i = 0; i + win < x.Length; i += win / 2)
                {
                    double e = 0; for (int k = 0; k < win; k++) e += x[i + k] * x[i + k];
                    if (Math.Sqrt(e / win) > 0.01) { start = Math.Max(0, i - win); break; }
                }
                int len = Math.Min(8 * rate, x.Length - start);
                var seg = new float[len];
                Array.Copy(x, start, seg, 0, len);
                double rms0 = Rms(seg);
                n++;
                string id = $"{n:00}-{pick.Singer}-{pick.Group.Replace(", ", "-").Replace(' ', '-')}";
                var outs = new List<(string Name, float[] Data)>();
                int nt1 = oldSetting[n - 1] == "hard" ? 1 : oldSetting[n - 1] == "natural" ? 2 : 3;
                outs.Add(($"nt1-{oldSetting[n - 1]}", Level(Render(seg, rate, Variants[nt1 - 1]), rms0)));
                foreach (var v in Variants.Where(v => v.Nt1 == 0)) outs.Add((v.Key, Level(Render(seg, rate, v), rms0)));
                InNotes(id, seg, rate, nt1, notesMd, Path.Combine(dir, "pitch-csv"));
                TuneEval.WriteWav(Path.Combine(dir, id + "-original.wav"), seg, rate);
                foreach (var (name, data) in outs) TuneEval.WriteWav(Path.Combine(dir, $"{id}-{name}.wav"), data, rate);
                // Blind copies: the original is not among them; letters in a shuffled order.
                var order = Enumerable.Range(0, outs.Count).OrderBy(_ => rng.Next()).ToList();
                var cells = new List<string>();
                for (int k = 0; k < order.Count; k++)
                {
                    char letter = (char)('A' + k);
                    TuneEval.WriteWav(Path.Combine(dir, "blind", $"{n:00}-{letter}.wav"), outs[order[k]].Data, rate);
                    cells.Add(outs[order[k]].Name);
                }
                TuneEval.WriteWav(Path.Combine(dir, "blind", $"{n:00}-original.wav"), seg, rate);
                key.AppendLine($"| {n:00} | {string.Join(" | ", cells)} |");
                readme.AppendLine($"| {id} | {pick.Singer} | {pick.Group} | {start / (double)rate:F1} | {len / (double)rate:F1} | {oldSetting[n - 1]} |");
            }
            File.WriteAllText(Path.Combine(dir, "README.md"), readme.ToString());
            File.WriteAllText(Path.Combine(dir, "blind-key", "KEY.md"), key.ToString());
            md.Append(notesMd);
            md.AppendLine();
            md.AppendLine("### Listening pack");
            md.AppendLine();
            md.AppendLine($"{n} excerpts (at most 8 s each; original, NT1's old output, NT2 at Natural 0, 50, 100; levels matched; a blind folder with a key) were written beside this report with the VocalSet attribution in their README (artifact `neo-tune-excerpts`).");
            md.AppendLine();
        }

        static double Rms(float[] x) { double e = 0; foreach (var f in x) e += f * f; return Math.Sqrt(e / Math.Max(1, x.Length)); }

        static float[] Level(float[] y, double rms)
        {
            double g = Rms(y) > 1e-9 ? rms / Rms(y) : 1.0;
            var o = new float[y.Length];
            for (int i = 0; i < y.Length; i++) o[i] = (float)Math.Clamp(y[i] * g, -0.98, 0.98);
            return o;
        }
    }
}
