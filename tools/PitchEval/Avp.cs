// Amateur Vocal Percussion (docs/specs/pitch.md §7.2): 9,780 kick, snare, closed- and open-hat imitations by 28 amateurs, with
// onset and class labels. Onset F-measure (50 ms window) and onset latency; then trigger accuracy: per participant, trained on
// the first 10 detected hits of each class and tested on the rest (4 classes; 3 as kick / snare / hat), on the same
// recordings, on the participant's improvisation, and trained on the personal recordings against the fixed ones.

using System.Globalization;
using System.Text;
using NeoBuzz.Dsp;

namespace NeoBuzz.PitchEval
{
    internal static class Avp
    {
        static readonly string[] ClassNames = { "kd", "sd", "hhc", "hho" };

        sealed class FileData
        {
            public string Participant, Modality, Kind;
            public float[] X; public int Rate;
            public List<(double T, int Cls)> Ann = new();
            public List<OnsetEvent> Found;
        }

        static int ClassOf(string label) => label switch { "kd" => 0, "sd" => 1, "hhc" => 2, "hho" => 3, _ => -1 };

        static List<OnsetEvent> Detect(float[] x, int rate, float threshold, double minInterval = -1)
        {
            var od = new OnsetDetector(rate) { Threshold = threshold };
            if (minInterval > 0) od.MinIntervalSeconds = minInterval;
            var buf = new OnsetEvent[od.MaxEvents(256)];
            var list = new List<OnsetEvent>();
            for (int pos = 0; pos + 256 <= x.Length; pos += 256)
            {
                int n = od.Process(x, pos, 256, buf, 0);
                for (int i = 0; i < n; i++) list.Add(buf[i]);
            }
            return list;
        }

        /// <summary>Greedy nearest match inside the window. Returns, per annotation, the index of its detection or −1.</summary>
        static int[] Match(List<OnsetEvent> found, List<(double T, int Cls)> ann, int rate, double window)
        {
            var match = new int[ann.Count];
            var used = new bool[found.Count];
            for (int a = 0; a < ann.Count; a++)
            {
                int best = -1; double bd = double.MaxValue;
                for (int i = 0; i < found.Count; i++)
                {
                    if (used[i]) continue;
                    double d = Math.Abs(found[i].Sample / (double)rate - ann[a].T);
                    if (d < bd) { bd = d; best = i; }
                }
                if (best >= 0 && bd <= window) { used[best] = true; match[a] = best; } else match[a] = -1;
            }
            return match;
        }

        public static void Run(string dir, int limitParticipants, StringBuilder md)
        {
            string root = Directory.Exists(Path.Combine(dir, "AVP_Dataset")) ? Path.Combine(dir, "AVP_Dataset") : dir;
            var files = new List<FileData>();
            foreach (var modality in new[] { "Fixed", "Personal" })
            {
                string md0 = Path.Combine(root, modality);
                if (!Directory.Exists(md0)) continue;
                var parts = Directory.GetDirectories(md0).OrderBy(d => int.Parse(Path.GetFileName(d).Split('_')[1])).ToList();
                if (limitParticipants > 0) parts = parts.Take(limitParticipants).ToList();
                foreach (var pd in parts)
                    foreach (var wav in Directory.GetFiles(pd, "*.wav").Where(f => !Path.GetFileName(f).StartsWith("._")))
                    {
                        string csv = Path.ChangeExtension(wav, ".csv");
                        if (!File.Exists(csv)) continue;
                        var fd = new FileData { Participant = Path.GetFileName(pd), Modality = modality, Kind = Path.GetFileNameWithoutExtension(wav).Split('_')[1] };
                        (fd.X, fd.Rate) = Wav.Read(wav);
                        foreach (var line in File.ReadLines(csv))
                        {
                            var p = line.Split(',');
                            if (p.Length < 2 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) continue;
                            fd.Ann.Add((t, ClassOf(p[1].Trim())));
                        }
                        files.Add(fd);
                    }
            }
            int total = files.Sum(f => f.Ann.Count);
            Console.WriteLine($"AVP: {files.Count} files, {total} annotated onsets");

            md.AppendLine("## Amateur Vocal Percussion: onsets");
            md.AppendLine();
            md.AppendLine($"{files.Count} recordings by {files.Select(f => f.Participant).Distinct().Count()} participants, {total} annotated hits (kick, snare, closed hat, open hat), a MacBook's built-in microphone. An onset counts as found if the detector reports one within 50 ms of the annotation (each detection matches one annotation). **Offset** is the detected position less the annotation (the annotators marked the start of the utterance, which is not always the first energy: a bias belongs to the labels as well as to the detector); **Report delay** is how long after its own position the detector announced it.");
            md.AppendLine();
            md.AppendLine("| Threshold | Min. interval (ms) | precision | recall | F-measure | offset median (ms) | offset p10 / p90 (ms) | report delay median (ms) |");
            md.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|");
            double defMin = new OnsetDetector(44100).MinIntervalSeconds;
            float defTh = new OnsetDetector(44100).Threshold;
            foreach (var (th, mi) in new[] { (defTh, 0.04), (defTh, 0.08), (defTh, 0.1), (defTh, 0.12), (2.5f, defMin), (6f, defMin) }.Distinct())
            {
                long tp = 0, fp = 0, fn = 0;
                var off = new List<double>(); var delay = new List<double>();
                var det = new List<OnsetEvent>[files.Count];
                Parallel.For(0, files.Count, i => det[i] = Detect(files[i].X, files[i].Rate, th, mi));
                for (int i = 0; i < files.Count; i++)
                {
                    var f = files[i];
                    if (th == defTh && Math.Abs(mi - defMin) < 1e-9) f.Found = det[i];
                    var m = Match(det[i], f.Ann, f.Rate, 0.05);
                    int hit = m.Count(v => v >= 0);
                    tp += hit; fn += f.Ann.Count - hit; fp += det[i].Count - hit;
                    for (int a = 0; a < m.Length; a++)
                        if (m[a] >= 0)
                        {
                            off.Add((det[i][m[a]].Sample / (double)f.Rate - f.Ann[a].T) * 1000);
                            delay.Add((det[i][m[a]].ReportedAt - det[i][m[a]].Sample) * 1000.0 / f.Rate);
                        }
                }
                double prec = tp / (double)Math.Max(1, tp + fp), rec = tp / (double)Math.Max(1, tp + fn);
                md.AppendLine($"| {th:F1}{(th == defTh && Math.Abs(mi - defMin) < 1e-9 ? " (default)" : "")} | {mi * 1000:F0} | {Fmt.P(prec)} | {Fmt.P(rec)} | {Fmt.P(2 * prec * rec / Math.Max(1e-9, prec + rec))} | {Fmt.F(Fmt.Median(off))} | {Fmt.F(Fmt.Pct(off, 0.1))} / {Fmt.F(Fmt.Pct(off, 0.9))} | {Fmt.F(Fmt.Median(delay))} |");
            }
            md.AppendLine();
            // Per class and per kind, at the default.
            md.AppendLine("Recall at the default threshold, by annotated class: " + string.Join(", ", Enumerable.Range(0, 4).Select(c =>
            {
                int n = 0, h = 0;
                foreach (var f in files)
                {
                    var m = Match(f.Found, f.Ann, f.Rate, 0.05);
                    for (int a = 0; a < m.Length; a++) if (f.Ann[a].Cls == c) { n++; if (m[a] >= 0) h++; }
                }
                return $"{ClassNames[c]} {Fmt.P(h / (double)Math.Max(1, n))} of {n}";
            })) + ".");
            md.AppendLine();

            Triggers(files, md);
        }

        /// <summary>For a look at one file: each detection against the annotations.</summary>
        public static void Debug(string wav, string csv)
        {
            var (x, rate) = Wav.Read(wav);
            var ann = File.ReadLines(csv).Select(l => l.Split(',')).Where(p => p.Length > 1).Select(p => (T: double.Parse(p[0], CultureInfo.InvariantCulture), L: p[1])).ToList();
            var found = Detect(x, rate, 4f);
            foreach (var f in found)
            {
                double t = f.Sample / (double)rate;
                var near = ann.OrderBy(a => Math.Abs(a.T - t)).First();
                Console.WriteLine($"det {t:F3} strength {f.Strength:F1} level {f.LevelDb:F0} dB   nearest annotation {near.T:F3} ({(t - near.T) * 1000:F0} ms) {near.L}");
            }
            Console.WriteLine($"{found.Count} detections, {ann.Count} annotations");
        }

        sealed class Hit { public int Cls; public float[] F; }

        /// <summary>The features at each matched detection, in time order, with the annotation's class; also how many annotations there were.</summary>
        static (List<Hit> Hits, int Annotated) Hits(FileData f, TriggerFeatures tf, float[] ring, int mask)
        {
            // The recording in the ring (a power of two long, zero after the end).
            Array.Clear(ring, 0, ring.Length);
            Array.Copy(f.X, ring, Math.Min(f.X.Length, ring.Length));
            var m = Match(f.Found, f.Ann, f.Rate, 0.05);
            var hits = new List<Hit>();
            for (int a = 0; a < m.Length; a++)
            {
                if (m[a] < 0 || f.Ann[a].Cls < 0) continue;
                var feat = new float[TriggerFeatures.Dimension];
                tf.Extract(ring, mask, f.Found[m[a]].Sample, feat);
                hits.Add(new Hit { Cls = f.Ann[a].Cls, F = feat });
            }
            return (hits, f.Ann.Count(a => a.Cls >= 0));
        }

        sealed class Tally
        {
            public long Annotated, Detected, Correct, Rejected;
            public List<double> PerParticipant = new();
            public double OverDetected => Detected == 0 ? double.NaN : (double)Correct / Detected;
            public double EndToEnd => Annotated == 0 ? double.NaN : (double)Correct / Annotated;
        }

        sealed class Data
        {
            public Dictionary<(string, string), (List<Hit>[] Class, int[] Ann, List<Hit> Improv, int ImprovAnn)> ByKey = new();
        }

        static Data Build(List<FileData> files, double length, List<string> parts)
        {
            int maxLen = files.Max(f => f.X.Length);
            int size = 1; while (size < maxLen + 8192) size <<= 1;
            var data = new Data();
            Parallel.ForEach(parts, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, part =>
            {
                var ring = new float[size]; int mask = size - 1;
                var tf = new TriggerFeatures(files[0].Rate, length);
                foreach (var mod in new[] { "Fixed", "Personal" })
                {
                    var cls = new List<Hit>[4]; var ann = new int[4];
                    List<Hit> imp = new(); int impAnn = 0;
                    bool any = false;
                    foreach (var f in files.Where(f => f.Participant == part && f.Modality == mod))
                    {
                        any = true;
                        var (hits, n) = Hits(f, tf, ring, mask);
                        if (f.Kind == "Improvisation") { imp = hits; impAnn = n; }
                        else
                        {
                            int c = f.Kind switch { "Kick" => 0, "Snare" => 1, "HHclosed" => 2, "HHopened" => 3, _ => -1 };
                            if (c >= 0) { cls[c] = hits; ann[c] = n; }
                        }
                    }
                    if (any && cls.All(c => c != null)) lock (data) data.ByKey[(part, mod)] = (cls, ann, imp, impAnn);
                }
            });
            return data;
        }

        static Tally Scenario(Data data, List<string> parts, string scenario, int classes)
        {
            var t = new Tally();
            foreach (var part in parts)
            {
                var mods = scenario == "cross" ? new[] { "Personal" } : new[] { "Personal", "Fixed" };
                foreach (var mod in mods)
                {
                    if (!data.ByKey.TryGetValue((part, mod), out var d)) continue;
                    var tr = new TriggerClassifier();
                    var testHits = new List<Hit>(); int testAnn = 0;
                    var trainOk = true;
                    for (int c = 0; c < 4; c++)
                    {
                        int take = Math.Min(10, d.Class[c].Count);
                        if (take < 5) trainOk = false;
                        for (int k = 0; k < take; k++) tr.Add(Map(c, classes), d.Class[c][k].F);
                    }
                    if (!trainOk || !tr.Train()) continue;
                    if (scenario == "same")
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            int take = Math.Min(10, d.Class[c].Count);
                            testHits.AddRange(d.Class[c].Skip(take));
                            testAnn += Math.Max(0, d.Ann[c] - take);
                        }
                    }
                    else if (scenario == "improv") { testHits.AddRange(d.Improv); testAnn += d.ImprovAnn; }
                    else
                    {
                        if (!data.ByKey.TryGetValue((part, "Fixed"), out var fx)) continue;
                        for (int c = 0; c < 4; c++) { testHits.AddRange(fx.Class[c]); testAnn += fx.Ann[c]; }
                    }
                    int right = 0, rej = 0;
                    foreach (var h in testHits)
                    {
                        var r = tr.Classify(h.F);
                        if (r.Class < 0) rej++;
                        else if (r.Class == Map(h.Cls, classes)) right++;
                    }
                    t.Annotated += testAnn; t.Detected += testHits.Count; t.Correct += right; t.Rejected += rej;
                    if (testAnn > 0) t.PerParticipant.Add((double)right / testAnn);
                }
            }
            return t;
        }

        static void Triggers(List<FileData> files, StringBuilder md)
        {
            var parts = files.Select(f => f.Participant).Distinct().OrderBy(p => int.Parse(p.Split('_')[1])).ToList();
            var data = Build(files, TriggerFeatures.DefaultLengthSeconds, parts);

            md.AppendLine("## Amateur Vocal Percussion: trigger accuracy");
            md.AppendLine();
            md.AppendLine("Per participant and per modality (Personal: each person's own sounds; Fixed: all say the same four), the classifier is trained on the first 10 detected hits of each class (in the class recordings, which hold only that class) and tested on the rest of those recordings, on the participant's improvisation (all four classes in a rhythm), and, trained on Personal, on all of Fixed's class recordings. Hits are the detector's (default threshold) matched to annotations within 50 ms; the features are taken at the detected position. **Of detected**: correct over the hits found; **End to end**: correct over all annotated hits (a missed onset is an error, and a hit the classifier rejects as none-of-these is an error). 4 classes: kick, snare, closed hat, open hat; 3 classes: the hats merged (20 hat examples).");
            md.AppendLine();
            md.AppendLine("| Test | Classes | Participants | Hits | Of detected | End to end | Rejected | Mean of participants (end to end) |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var scenario in new[] { "same", "improv", "cross" })
                foreach (int classes in new[] { 4, 3 })
                {
                    var t = Scenario(data, parts, scenario, classes);
                    string name = scenario switch { "same" => "rest of the class recordings", "improv" => "improvisation", _ => "Personal -> Fixed" };
                    md.AppendLine($"| {name} | {classes} | {t.PerParticipant.Count} | {t.Annotated} | {Fmt.P(t.OverDetected)} | {Fmt.P(t.EndToEnd)} | {Fmt.P(t.Detected == 0 ? 0 : (double)t.Rejected / t.Detected)} | {Fmt.P(Fmt.Mean(t.PerParticipant))} |");
                }
            md.AppendLine();
            var tf0 = new TriggerFeatures(files[0].Rate);
            md.AppendLine($"The classifier's own delay: it needs {TriggerFeatures.DefaultLengthSeconds * 1000:F0} ms of audio from 3 ms before the onset, so a hit is classified about {tf0.Length * 1000.0 / tf0.SampleRate - TriggerFeatures.LeadSeconds * 1000:F0} ms after it starts, on top of the detector's report delay above. A longer window sees more of what follows the burst (the vowel of \"boom\" and \"tss\") and costs that much more delay:");
            md.AppendLine();
            md.AppendLine("| Window (ms) | classified after (ms) | 4 classes, rest of the recordings (of detected / end to end) | 3 classes | 4 classes, improvisation |");
            md.AppendLine("|---:|---:|---:|---:|---:|");
            foreach (double len in new[] { 0.012, 0.018, 0.025, 0.035 })
            {
                var d2 = Math.Abs(len - TriggerFeatures.DefaultLengthSeconds) < 1e-9 ? data : Build(files, len, parts);
                var a4 = Scenario(d2, parts, "same", 4); var a3 = Scenario(d2, parts, "same", 3); var i4 = Scenario(d2, parts, "improv", 4);
                md.AppendLine($"| {len * 1000:F0}{(Math.Abs(len - TriggerFeatures.DefaultLengthSeconds) < 1e-9 ? " (default)" : "")} | {(len - TriggerFeatures.LeadSeconds) * 1000:F0} | {Fmt.P(a4.OverDetected)} / {Fmt.P(a4.EndToEnd)} | {Fmt.P(a3.OverDetected)} / {Fmt.P(a3.EndToEnd)} | {Fmt.P(i4.OverDetected)} / {Fmt.P(i4.EndToEnd)} |");
            }
            md.AppendLine();
        }

        static int Map(int c, int classes) => classes == 3 && c == 3 ? 2 : c;
    }
}
