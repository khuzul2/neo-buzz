// Convert to MIDI evaluation (docs/specs/pitch.md section 9.6). Runs the converters on real recordings and writes a Markdown report.
//
//   ConvertEval [--mdb DIR] [--mdb-limit N] [--gmd DIR] [--maestro DIR --reference reference.json] [--out report.md]
//
//  mdb      MDB-stem-synth (the folder with audio_stems and annotation_stems): note F-measure of the melody converter (onset +-50 ms, pitch +-50 cents),
//           the reference notes cut from the annotated f0, and frame accuracy.
//  gmd      Groove MIDI Dataset (fetch_data.py): onset F-measure (+-50 ms) of the drum converter, per class and all.
//  maestro  MAESTRO excerpts (fetch_data.py): note F-measure of the harmony converter (onset +-50 ms, pitch exact; and with the offset within
//           max(50 ms, 20 % of the length)), and the agreement with the Python reference (basic_pitch_reference.py, JSON of its notes per file).

using System.Globalization;
using System.Text;
using System.Text.Json;
using ReBuzz.Core.AudioLibrary.AudioToMidi;

namespace NeoBuzz.ConvertEval
{
    internal sealed class Counts
    {
        public long Tp, Fp, Fn;
        public void Add(Counts o) { Tp += o.Tp; Fp += o.Fp; Fn += o.Fn; }
        public double P => Tp + Fp == 0 ? double.NaN : (double)Tp / (Tp + Fp);
        public double R => Tp + Fn == 0 ? double.NaN : (double)Tp / (Tp + Fn);
        public double F => 2.0 * Tp + Fp + Fn == 0 ? double.NaN : 2.0 * Tp / (2.0 * Tp + Fp + Fn);
        public string Row(string name) => $"| {name} | {Tp + Fn} | {Tp + Fp} | {Pc(P)} | {Pc(R)} | {Pc(F)} |";
        public static string Pc(double v) => double.IsNaN(v) ? "-" : (100 * v).ToString("0.0", CultureInfo.InvariantCulture) + " %";
    }

    internal sealed record Ref(double On, double Off, double Pitch, string Class = "");

    internal static class Program
    {
        /// <summary>Greedy one-to-one matching in time order: a detection is right when it is within <paramref name="tol"/> seconds of a free reference of the same class that <paramref name="ok"/> accepts.</summary>
        internal static Counts Match(IReadOnlyList<ConvertedNote> found, IReadOnlyList<Ref> refs, double tol, Func<ConvertedNote, Ref, bool> ok, Func<ConvertedNote, string> classOf = null)
        {
            var used = new bool[refs.Count];
            var c = new Counts();
            foreach (var n in found.OrderBy(n => n.Start))
            {
                int best = -1; double bd = double.MaxValue;
                for (int i = 0; i < refs.Count; i++)
                {
                    if (used[i]) continue;
                    double d = Math.Abs(refs[i].On - n.Start);
                    if (d <= tol && d < bd && ok(n, refs[i]) && (classOf == null || classOf(n) == refs[i].Class)) { bd = d; best = i; }
                }
                if (best >= 0) { used[best] = true; c.Tp++; } else c.Fp++;
            }
            c.Fn = used.Count(u => !u);
            return c;
        }

        static int Main(string[] args)
        {
            string mdb = null, gmd = null, maestro = null, reference = null, outPath = "convert-eval-report.md", gmdDump = null, featDump = null, synthDir = null;
            double candThr = 2.0, candMs = 20;
            int mdbLimit = 0;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + a);
                switch (a)
                {
                    case "--mdb": mdb = Next(); break;
                    case "--mdb-limit": mdbLimit = int.Parse(Next()); break;
                    case "--gmd": gmd = Next(); break;
                    case "--gmd-dump": gmdDump = Next(); break;
                    case "--synth": synthDir = Next(); break;
                    case "--feat-dump": featDump = Next(); break;
                    case "--cand-threshold": candThr = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--cand-ms": candMs = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--maestro": maestro = Next(); break;
                    case "--reference": reference = Next(); break;
                    case "--out": outPath = Next(); break;
                    default: Console.Error.WriteLine("unknown argument " + a); return 2;
                }
            }
            if (synthDir != null) { SynthKit.Write(synthDir); return 0; }
            if (featDump != null) { FeatureDump.Run(gmd, featDump, candThr, candMs); return 0; }
            var md = new StringBuilder();
            md.AppendLine("# Convert to MIDI evaluation on real recordings");
            md.AppendLine();
            md.AppendLine($"Run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC on {Environment.ProcessorCount} cores, .NET {Environment.Version}. The converters are `ReBuzz/Core/AudioLibrary/AudioToMidi` as the app runs them (defaults of the menu). The data are fetched on the runner and are not kept.");
            md.AppendLine();
            if (mdb != null) Melody.Run(mdb, mdbLimit, md);
            if (gmd != null) Drums.Run(gmd, md, gmdDump);
            if (maestro != null) Harmony.Run(maestro, reference, md);
            md.AppendLine("## Data and licences");
            md.AppendLine();
            md.AppendLine("- **MDB-stem-synth** (https://zenodo.org/records/1481172), Salamon, Bittner, Bonada, Bosch, Gomez, Bello: CC BY-NC 4.0. Measurement only.");
            md.AppendLine("- **Groove MIDI Dataset** (https://magenta.tensorflow.org/datasets/groove), Gillick, Roberts, Engel, Eck, Bamman: CC BY 4.0. The recordings are of an electronic kit (Roland TD-11), so the drum figures are for electronic drum sounds.");
            md.AppendLine("- **MAESTRO v3.0.0** (https://magenta.tensorflow.org/datasets/maestro), Hawthorne et al.: CC BY-NC-SA 4.0. Measurement only; 60-second excerpts.");
            md.AppendLine("- **Basic Pitch** (https://github.com/spotify/basic-pitch), Spotify: Apache-2.0; the model file and the Python package that is the reference.");
            File.WriteAllText(outPath, md.ToString());
            Console.WriteLine(md.ToString());
            return 0;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- melody

    internal static class Melody
    {
        /// <summary>The reference notes of an annotated f0 track: runs of voiced frames that stay within 0.8 semitone of the run's median pitch (cut when three frames in a row leave it, or the voice stops); runs under 50 ms are dropped.</summary>
        internal static List<Ref> NotesOf(List<double> t, List<double> f0)
        {
            var notes = new List<Ref>();
            int n = f0.Count;
            double Midi(int k) => 69 + 12 * Math.Log2(f0[k] / 440);
            int i = 0;
            while (i < n)
            {
                if (f0[i] <= 0) { i++; continue; }
                int start = i, end = i;                       // end: the last frame in the note
                var pitches = new List<double> { Midi(i) };
                int away = 0;
                double median = pitches[0];
                for (int k = i + 1; k < n && f0[k] > 0; k++)
                {
                    double m = Midi(k);
                    if (Math.Abs(m - median) > 0.8)
                    {
                        if (++away >= 3) break;
                        continue;
                    }
                    away = 0;
                    pitches.Add(m);
                    end = k;
                    var sorted = pitches.OrderBy(v => v).ToList();
                    median = sorted[sorted.Count / 2];
                }
                if (t[end] - t[start] >= 0.05) notes.Add(new Ref(t[start], t[end], median));
                i = end + 1;
            }
            return notes;
        }

        public static void Run(string dir, int limit, StringBuilder md)
        {
            string audio = Path.Combine(dir, "audio_stems"), ann = Path.Combine(dir, "annotation_stems");
            if (!Directory.Exists(audio))
            {
                var nested = Directory.GetDirectories(dir).FirstOrDefault(d => Directory.Exists(Path.Combine(d, "audio_stems")));
                if (nested != null) { audio = Path.Combine(nested, "audio_stems"); ann = Path.Combine(nested, "annotation_stems"); }
            }
            var files = Directory.GetFiles(audio, "*.wav").Where(f => !Path.GetFileName(f).StartsWith("._")).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (limit > 0 && files.Count > limit) files = Enumerable.Range(0, limit).Select(i => files[(int)((long)i * files.Count / limit)]).ToList();
            Console.WriteLine($"MDB-stem-synth: {files.Count} stems");
            var total = new Counts();
            var perStem = new Counts[files.Count];
            long frames = 0, frameHit = 0, voicedRef = 0, voicedHitBoth = 0;
            double audioSeconds = 0, work = 0;
            int done = 0;
            Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, i =>
            {
                var (x, rate) = NeoBuzz.PitchEval.Wav.Read(files[i]);
                var t = new List<double>(); var f0 = new List<double>();
                foreach (var line in File.ReadLines(Path.Combine(ann, Path.GetFileName(files[i]).Replace(".wav", ".csv"))))
                {
                    var p = line.Split(',');
                    if (p.Length < 2) continue;
                    t.Add(double.Parse(p[0], CultureInfo.InvariantCulture)); f0.Add(double.Parse(p[1], CultureInfo.InvariantCulture));
                }
                var refs = NotesOf(t, f0);
                var r = AudioConverter.Run(x, rate, new ConvertOptions { Mode = ConvertMode.Melody });
                var c = Program.Match(r.Notes, refs, 0.05, (n, rf) => Math.Abs(n.Pitch - rf.Pitch) <= 0.5);
                // Frame accuracy: of the frames the annotation calls voiced, how many have a converted note within 50 cents; and the notes over unvoiced frames.
                long fr = 0, hit = 0, vr = 0, vb = 0;
                var notes = r.Notes.OrderBy(n => n.Start).ToList();
                int ni = 0;
                for (int k = 0; k < t.Count; k++)
                {
                    if (f0[k] <= 0) continue;
                    vr++;
                    while (ni < notes.Count && notes[ni].End < t[k]) ni++;
                    if (ni < notes.Count && notes[ni].Start <= t[k])
                    {
                        vb++;
                        if (Math.Abs(notes[ni].Pitch - (69 + 12 * Math.Log2(f0[k] / 440))) <= 0.5) hit++;
                    }
                    fr++;
                }
                lock (total)
                {
                    total.Add(c); perStem[i] = c;
                    frames += fr; frameHit += hit; voicedRef += vr; voicedHitBoth += vb;
                    audioSeconds += x.Length / (double)rate; work += r.WorkSeconds;
                    if (++done % 20 == 0) Console.WriteLine($"  {done}/{files.Count}");
                }
            });
            md.AppendLine("## Melody: MDB-stem-synth");
            md.AppendLine();
            md.AppendLine($"{files.Count} resynthesised solo stems ({audioSeconds / 60:0} minutes of audio, converted in {work:0} s of work: {audioSeconds / Math.Max(work, 1e-9):0}x real time per core). The reference notes are cut from the annotated f0 (runs that stay within 0.8 semitone of their median, at least 50 ms); a note is right when its onset is within 50 ms of a reference's and its pitch within 50 cents of that note's median f0.");
            md.AppendLine();
            md.AppendLine("| | reference | found | precision | recall | F-measure |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|");
            md.AppendLine(total.Row("all stems"));
            md.AppendLine();
            var fs = perStem.Where(c => c != null && !double.IsNaN(c.F)).Select(c => c.F).OrderBy(v => v).ToList();
            md.AppendLine($"Per stem F-measure: median {Counts.Pc(fs[fs.Count / 2])}, 10th percentile {Counts.Pc(fs[fs.Count / 10])}, 90th percentile {Counts.Pc(fs[fs.Count * 9 / 10])}.");
            md.AppendLine();
            md.AppendLine($"Frame level: of the annotated voiced frames, {Counts.Pc((double)voicedHitBoth / Math.Max(1, voicedRef))} are inside a converted note and {Counts.Pc((double)frameHit / Math.Max(1, voicedRef))} are inside one within 50 cents.");
            md.AppendLine();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- drums

    internal static class Drums
    {
        internal static string GroupOf(int p) => Group(p);
        static string Group(int p) => p switch
        {
            36 => "kick", 38 or 37 or 40 => "snare", 42 or 44 or 22 => "closed hat", 46 or 26 => "open hat",
            41 or 43 or 45 or 47 or 48 or 50 or 58 => "tom", 49 or 55 or 57 or 51 or 53 or 59 or 52 => "cymbal", _ => "other",
        };

        static string GroupOf(ConvertedNote n) => n.Label switch
        {
            DrumConverter.Kick => "kick", DrumConverter.Snare => "snare", DrumConverter.ClosedHat => "closed hat", DrumConverter.OpenHat => "open hat",
            DrumConverter.Crash => "cymbal", _ => "tom",
        };

        public static void Run(string dir, StringBuilder md, string dump = null)
        {
            var files = Directory.GetFiles(dir, "*.wav").OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (dump != null) Directory.CreateDirectory(dump);
            var confusion = new Confusion();
            Console.WriteLine($"GMD: {files.Count} recordings");
            var classes = new[] { "kick", "snare", "closed hat", "open hat", "tom", "cymbal" };
            var per = classes.ToDictionary(c => c, _ => new Counts());
            var all = new Counts();
            var onsets = new Counts();
            double audioSeconds = 0, work = 0;
            foreach (var f in files)
            {
                var (x, rate) = NeoBuzz.PitchEval.Wav.Read(f);
                var gt = JsonSerializer.Deserialize<double[][]>(File.ReadAllText(Path.ChangeExtension(f, ".json"))).Where(g => g[2] >= 20).ToList();
                var refs = gt.Select(g => new Ref(g[0], g[0], g[1], Group((int)g[1]))).Where(r => r.Class != "other").ToList();
                var r = AudioConverter.Run(x, rate, new ConvertOptions { Mode = ConvertMode.Drums });
                audioSeconds += x.Length / (double)rate; work += r.WorkSeconds;
                // Per class: the notes of that class against the reference hits of it.
                foreach (var cls in classes)
                {
                    var c = Program.Match(r.Notes.Where(n => GroupOf(n) == cls).ToList(), refs.Where(q => q.Class == cls).ToList(), 0.05, (_, _) => true);
                    per[cls].Add(c);
                    all.Add(c);
                }
                confusion.Add(r.Notes.Select(n => (n.Start, GroupOf(n))).ToList(), gt.Select(g => (g[0], Group((int)g[1]))).ToList());
                if (dump != null)
                    File.WriteAllText(Path.Combine(dump, Path.GetFileNameWithoutExtension(f) + ".out.json"), JsonSerializer.Serialize(new
                    {
                        truth = gt.Select(g => new object[] { g[0], Group((int)g[1]), (int)g[1] }),
                        output = r.Notes.Select(n => new object[] { Math.Round(n.Start, 4), GroupOf(n), n.Pitch }),
                        seconds = x.Length / (double)rate,
                    }));
                // Onsets alone: the times of the hits, whatever the class (a kick with a hat is one onset).
                var foundTimes = r.Notes.GroupBy(n => Math.Round(n.Start, 3)).Select(g => new ConvertedNote { Start = g.Key }).ToList();
                var refTimes = refs.GroupBy(q => Math.Round(q.On / 0.02) * 0.02).Select(g => new Ref(g.Min(q => q.On), g.Min(q => q.On), 0)).ToList();
                onsets.Add(Program.Match(foundTimes, refTimes, 0.05, (_, _) => true));
            }
            md.AppendLine("## Drums: Groove MIDI Dataset");
            md.AppendLine();
            md.AppendLine($"{files.Count} recordings of the test split ({audioSeconds / 60:0.#} minutes, an electronic kit), against the MIDI the drummer played (hits of velocity 20 and up; toms and cymbals are grouped, as the classes are). A hit is right when its class matches and its time is within 50 ms. Converted in {work:0.#} s of work.");
            md.AppendLine();
            md.AppendLine("| class | reference | found | precision | recall | F-measure |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (var cls in classes) md.AppendLine(per[cls].Row(cls));
            md.AppendLine(all.Row("all classes"));
            md.AppendLine(onsets.Row("onsets only (class ignored)"));
            md.AppendLine();
            md.AppendLine($"Speed: {audioSeconds / Math.Max(1e-9, work):0}x real time on one core.");
            md.AppendLine();
            confusion.Write(md);
        }
    }

    /// <summary>Development tool: synthetic drum loops (the unit tests' kit, ConvertSignals.Hit) with exact hit lists, in the layout of the GMD folders, so that the classifier also knows the synthetic kit of the tests (training data only; no real audio).</summary>
    internal static class SynthKit
    {
        public static void Write(string dir)
        {
            Directory.CreateDirectory(dir);
            var rnd = new Random(11);
            var notes = new Dictionary<string, int> { ["kick"] = 36, ["snare"] = 38, ["hat"] = 42, ["open"] = 46 };
            for (int n = 0; n < 120; n++)
            {
                double bpm = 70 + rnd.Next(90), step = 60.0 / bpm / 2;          // 8th notes
                int steps = 16;
                double seconds = 0.4 + steps * step + 1.0;
                var x = new float[(int)(ConvertSignalsRate * seconds)];
                var truth = new List<double[]>();
                for (int i = 0; i < steps; i++)
                {
                    double at = 0.3 + i * step;
                    double Lv() => 0.15 + 0.85 * rnd.NextDouble();
                    void Add(string d) { double lv = Lv(); ReBuzz.SessionTests.Convert.ConvertSignals.Hit(x, d, at, lv); truth.Add(new double[] { Math.Round(at, 4), notes[d], 100 }); }
                    bool kick = i % 4 == 0 || rnd.NextDouble() < 0.15, snare = i % 4 == 2 || rnd.NextDouble() < 0.08;
                    if (kick) Add("kick");
                    if (snare) Add("snare");
                    // Hats mostly between the other drums (as the tests' loop has them), sometimes with them.
                    double r = rnd.NextDouble(), together = kick || snare ? 0.1 : 1.0;
                    if (r < 0.7 * together) Add("hat"); else if (!kick && !snare && r < 0.8) Add("open");
                }
                string name = Path.Combine(dir, $"synth{n:00}");
                WriteWav(name + ".wav", x, ConvertSignalsRate);
                File.WriteAllText(name + ".json", JsonSerializer.Serialize(truth));
            }
        }
        const int ConvertSignalsRate = ReBuzz.SessionTests.Convert.ConvertSignals.Rate;
        static void WriteWav(string path, float[] x, int rate)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write("RIFF"u8.ToArray()); w.Write(36 + x.Length * 2); w.Write("WAVEfmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8.ToArray()); w.Write(x.Length * 2);
            foreach (var v in x) w.Write((short)Math.Clamp(v * 32767, -32768, 32767));
        }
    }

    /// <summary>Development tool: candidates and their features, with the class labels of the nearest truth hits (for training the drum classifier offline).</summary>
    internal static class FeatureDump
    {
        static readonly string[] Cls = { "kick", "snare", "closed hat", "open hat", "tom", "cymbal" };
        public static void Run(string dir, string outDir, double thr, double ms)
        {
            Directory.CreateDirectory(outDir);
            var recall = new long[Cls.Length]; var total = new long[Cls.Length]; long ncand = 0, nlab = 0;
            foreach (var f in Directory.GetFiles(dir, "*.wav").OrderBy(f => f, StringComparer.Ordinal))
            {
                var (x, rate) = NeoBuzz.PitchEval.Wav.Read(f);
                if (rate > 48000) { x = ReBuzz.Core.AudioLibrary.Resampler.Resample(x, rate, 48000); rate = 48000; }
                var gt = JsonSerializer.Deserialize<double[][]>(File.ReadAllText(Path.ChangeExtension(f, ".json"))).Where(g => g[2] >= 20).ToList();
                var cands = DrumConverter.Candidates(x, rate, thr, ms);
                var fe = new DrumFeatures(rate);
                var sb = new StringBuilder();
                var hit = new bool[gt.Count];
                var labels = new int[cands.Count, Cls.Length];
                // Each truth hit labels the one candidate nearest to it (within 50 ms): a ring that fires a second candidate is not a second hit.
                for (int g = 0; g < gt.Count; g++)
                {
                    int ci = Array.IndexOf(Cls, DrumsGroup((int)gt[g][1])); if (ci < 0) continue;
                    int best = -1; double bd = 0.05;
                    for (int i = 0; i < cands.Count; i++)
                    {
                        double d = Math.Abs(cands[i].Sample / (double)rate - gt[g][0]);
                        if (d <= bd) { bd = d; best = i; }
                    }
                    if (best >= 0) { labels[best, ci] = 1; hit[g] = true; }
                }
                for (int i = 0; i < cands.Count; i++)
                {
                    double t = cands[i].Sample / (double)rate;
                    double prev = i > 0 ? t - cands[i - 1].Sample / (double)rate : 9, next = i + 1 < cands.Count ? cands[i + 1].Sample / (double)rate - t : 9;
                    var lab = new int[Cls.Length];
                    for (int c = 0; c < Cls.Length; c++) lab[c] = labels[i, c];
                    var v = fe.Extract(x, cands[i].Sample, prev, next);
                    sb.Append(t.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',').Append(string.Join("", lab));
                    foreach (var q in v) sb.Append(',').Append(q.ToString("0.##", CultureInfo.InvariantCulture));
                    sb.AppendLine();
                    ncand++; if (lab.Any(l => l > 0)) nlab++;
                }
                for (int g = 0; g < gt.Count; g++)
                {
                    int ci = Array.IndexOf(Cls, DrumsGroup((int)gt[g][1])); if (ci < 0) continue;
                    total[ci]++; if (hit[g]) recall[ci]++;
                }
                File.WriteAllText(Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + ".feat.csv"), sb.ToString());
                File.WriteAllText(Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + ".truth.json"), JsonSerializer.Serialize(gt.Select(g => new object[] { g[0], DrumsGroup((int)g[1]) })));
            }
            Console.WriteLine($"candidates {ncand}, labelled {nlab}; candidate recall within 50 ms: " + string.Join(", ", Cls.Select((c, i) => $"{c} {100.0 * recall[i] / Math.Max(1, total[i]):0.0} % of {total[i]}")));
        }
        static string DrumsGroup(int p) => Drums.GroupOf(p);
    }

    /// <summary>Truth class against output class (hits within 50 ms), plus missed and extra. Same-class matches first (nearest in time, one to one), then the rest of the truth hits against the rest of the outputs.</summary>
    internal sealed class Confusion
    {
        static readonly string[] Cls = { "kick", "snare", "closed hat", "open hat", "tom", "cymbal", "other" };
        readonly long[,] m = new long[Cls.Length, Cls.Length + 1];   // [truth, output or missed]
        readonly long[] extra = new long[Cls.Length];

        public void Add(List<(double T, string C)> output, List<(double T, string C)> truth)
        {
            int ci(string c) => Array.IndexOf(Cls, c);
            var uo = new bool[output.Count]; var ut = new bool[truth.Count];
            for (int pass = 0; pass < 2; pass++)
            {
                var pairs = new List<(double d, int ti, int oi)>();
                for (int i = 0; i < truth.Count; i++)
                    for (int j = 0; j < output.Count; j++)
                    {
                        double d = Math.Abs(truth[i].T - output[j].T);
                        if (d <= 0.05 && (pass == 1 || truth[i].C == output[j].C)) pairs.Add((d, i, j));
                    }
                foreach (var p in pairs.OrderBy(p => p.d))
                {
                    if (ut[p.ti] || uo[p.oi]) continue;
                    ut[p.ti] = uo[p.oi] = true;
                    m[ci(truth[p.ti].C), ci(output[p.oi].C)]++;
                }
            }
            for (int i = 0; i < truth.Count; i++) if (!ut[i]) m[ci(truth[i].C), Cls.Length]++;
            for (int j = 0; j < output.Count; j++) if (!uo[j]) extra[ci(output[j].C)]++;
        }

        public void Write(StringBuilder md)
        {
            md.AppendLine("Confusion matrix: rows are the drummer's hits by class, columns what the converter wrote for them (within 50 ms; same class first, then any); `missed` is a truth hit with no output near it, and the last row is outputs with no truth hit near them.");
            md.AppendLine();
            md.AppendLine("| truth \\ output | " + string.Join(" | ", Cls) + " | missed |");
            md.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---:|", Cls.Length + 1)));
            for (int t = 0; t < Cls.Length; t++)
            {
                long tot = 0; for (int o = 0; o <= Cls.Length; o++) tot += m[t, o];
                if (tot == 0) continue;
                md.AppendLine($"| {Cls[t]} ({tot}) | " + string.Join(" | ", Enumerable.Range(0, Cls.Length + 1).Select(o => m[t, o].ToString())) + " |");
            }
            md.AppendLine("| extra | " + string.Join(" | ", extra.Select(v => v.ToString())) + " | |");
            md.AppendLine();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- harmony

    internal static class Harmony
    {
        public static void Run(string dir, string referenceJson, StringBuilder md)
        {
            var files = Directory.GetFiles(dir, "*.wav").OrderBy(f => f, StringComparer.Ordinal).ToList();
            Console.WriteLine($"MAESTRO: {files.Count} excerpts");
            JsonElement reference = default;
            bool haveRef = referenceJson != null && File.Exists(referenceJson);
            if (haveRef) reference = JsonDocument.Parse(File.ReadAllText(referenceJson)).RootElement;
            var onset = new Counts(); var withOffset = new Counts(); var refOnset = new Counts(); var agree30 = new Counts(); var agreeExact = new Counts();
            double audioSeconds = 0, work = 0;
            long cs = 0, py = 0;
            using var model = new BasicPitchModel();
            foreach (var f in files)
            {
                var (x, rate) = NeoBuzz.PitchEval.Wav.Read(f);
                var gt = JsonSerializer.Deserialize<double[][]>(File.ReadAllText(Path.ChangeExtension(f, ".json"))).Select(g => new Ref(g[0], g[1], g[2])).ToList();
                var r = AudioConverter.Run(x, rate, new ConvertOptions { Mode = ConvertMode.Harmony }, null, default, model);
                audioSeconds += x.Length / (double)rate; work += r.WorkSeconds;
                onset.Add(Program.Match(r.Notes, gt, 0.05, (n, q) => n.Pitch == (int)q.Pitch));
                withOffset.Add(Program.Match(r.Notes, gt, 0.05, (n, q) => n.Pitch == (int)q.Pitch && Math.Abs(n.End - q.Off) <= Math.Max(0.05, 0.2 * (q.Off - q.On))));
                if (haveRef && reference.TryGetProperty(Path.GetFileName(f), out var list))
                {
                    var pyNotes = list.EnumerateArray().Select(e => new ConvertedNote { Start = e[0].GetDouble(), End = e[1].GetDouble(), Pitch = e[2].GetInt32() }).ToList();
                    py += pyNotes.Count; cs += r.Notes.Count;
                    refOnset.Add(Program.Match(pyNotes, gt, 0.05, (n, q) => n.Pitch == (int)q.Pitch));
                    var pyRefs = pyNotes.Select(n => new Ref(n.Start, n.End, n.Pitch)).ToList();
                    agree30.Add(Program.Match(r.Notes, pyRefs, 0.03, (n, q) => n.Pitch == (int)q.Pitch));
                    agreeExact.Add(Program.Match(r.Notes, pyRefs, 0.012, (n, q) => n.Pitch == (int)q.Pitch && Math.Abs(n.End - q.Off) <= 0.03));
                }
            }
            md.AppendLine("## Harmony: MAESTRO");
            md.AppendLine();
            md.AppendLine($"{files.Count} excerpts of 60 s of the test split, piano ({audioSeconds / 60:0} minutes; converted in {work:0} s of work), against the Disklavier's MIDI. Note F-measure as mir_eval's: onset within 50 ms and the same MIDI note; with offset: also the end within 50 ms or 20 % of the note's length.");
            md.AppendLine();
            md.AppendLine("| | reference | found | precision | recall | F-measure |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|");
            md.AppendLine(onset.Row("this port, onset"));
            md.AppendLine(withOffset.Row("this port, onset and offset"));
            if (haveRef) md.AppendLine(refOnset.Row("Python basic_pitch, onset (same files)"));
            md.AppendLine();
            if (haveRef)
            {
                md.AppendLine("Agreement of this port with the Python package (`basic_pitch.inference.predict`, the same model file, the upstream defaults), note against note:");
                md.AppendLine();
                md.AppendLine("| | Python notes | port notes | precision | recall | F-measure |");
                md.AppendLine("|---|---:|---:|---:|---:|---:|");
                md.AppendLine(agree30.Row("same pitch, onset within 30 ms"));
                md.AppendLine(agreeExact.Row("same pitch, onset within 12 ms, offset within 30 ms"));
                md.AppendLine();
                md.AppendLine($"{cs} notes from the port, {py} from the package. The package's time stamps are stepped (its frame-to-time table is a little off the exact time, by up to about 10 ms); the port uses the exact time.");
                md.AppendLine();
            }
        }
    }
}
