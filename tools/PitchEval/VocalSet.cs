// VocalSet (docs/specs/pitch.md §7.3): professional singers, 20 voices, on techniques and vowels. There is no f0 truth, so this
// measures what the tracker does on real technique: how much of the sung audio it calls voiced, how often the pitch jumps an
// octave from one estimate to the next, how steady a held note is, how Low latency agrees with Quality, and (the files are
// labelled by vowel) what the vowel estimator makes of a, i and u.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using NeoBuzz.Dsp;

namespace NeoBuzz.PitchEval
{
    internal static class VocalSet
    {
        // category/technique folders used, with a short label.
        static readonly (string Path, string Label)[] Groups =
        {
            ("long_tones/straight", "long tones, straight"),
            ("long_tones/messa", "long tones, messa di voce"),
            ("scales/straight", "scales, straight"),
            ("arpeggios/straight", "arpeggios, straight"),
            ("arpeggios/vibrato", "arpeggios, vibrato"),
            ("arpeggios/belt", "arpeggios, belt"),
            ("arpeggios/breathy", "arpeggios, breathy"),
            ("arpeggios/vocal_fry", "arpeggios, vocal fry"),
            ("excerpts/straight", "excerpts, straight"),
        };

        sealed class FileResult
        {
            public string Group, Voice, Singer, Vowel;
            public long Active, VoicedQ, VoicedL, Pairs, Agree, OctaveDisagree, ConsecQ, ConsecL, OctJumpQ, OctJumpL, BothVoiced;
            public List<double> RunStd = new();
            public double[] VowelMean = new double[3]; public int VowelFrames; public double Lm, Hm, Lh;
            public double Seconds;
        }

        public static void Run(string zipPath, int maxPerGroupPerSinger, StringBuilder md)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var rx = new Regex(@"^FULL/((?:fe)?male\d+)/(long_tones|scales|arpeggios|excerpts)/([a-z_]+)/([^/]+)\.wav$");
            var selected = new List<(ZipArchiveEntry E, string Group, string Singer, string Vowel)>();
            foreach (var grp in Groups)
            {
                foreach (var singerGroup in zip.Entries.Select(e => (E: e, M: rx.Match(e.FullName))).Where(t => t.M.Success && t.M.Groups[2].Value + "/" + t.M.Groups[3].Value == grp.Path).GroupBy(t => t.M.Groups[1].Value))
                {
                    var list = singerGroup.OrderBy(t => t.E.FullName, StringComparer.Ordinal).ToList();
                    // Vowels a, i and u first (the vowel check), then the rest.
                    string V(string n) { var p = Path.GetFileNameWithoutExtension(n).Split('_'); return p[^1].Length == 1 ? p[^1] : ""; }
                    var ordered = list.OrderBy(t => "aiu".Contains(V(t.E.Name)) && V(t.E.Name) != "" ? 0 : 1).ThenBy(t => t.E.FullName, StringComparer.Ordinal).Take(grp.Label == "long tones, straight" ? Math.Max(3, maxPerGroupPerSinger) : maxPerGroupPerSinger);
                    foreach (var t in ordered) selected.Add((t.E, grp.Label, singerGroup.Key, V(t.E.Name)));
                }
            }
            Console.WriteLine($"VocalSet: {selected.Count} files selected from {zip.Entries.Count} entries");
            var results = new List<FileResult>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int Batch = 24;
            for (int b = 0; b < selected.Count; b += Batch)
            {
                var chunk = selected.Skip(b).Take(Batch).ToList();
                var bytes = new byte[chunk.Count][];
                for (int i = 0; i < chunk.Count; i++)
                {
                    using var s = chunk[i].E.Open();
                    var ms = new MemoryStream((int)chunk[i].E.Length);
                    s.CopyTo(ms);
                    bytes[i] = ms.ToArray();
                }
                var res = new FileResult[chunk.Count];
                Parallel.For(0, chunk.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, i =>
                {
                    var (x, rate) = Wav.Read(new MemoryStream(bytes[i]));
                    res[i] = Analyse(x, rate, chunk[i].Group, chunk[i].Singer, chunk[i].Vowel);
                });
                results.AddRange(res);
                if ((b / Batch) % 5 == 0) Console.WriteLine($"  {results.Count}/{selected.Count} files, {sw.Elapsed.TotalSeconds:F0} s");
            }

            int nFemale = results.Where(r => r.Voice == "female").Select(r => r.Singer).Distinct().Count();
            int nMale = results.Where(r => r.Voice == "male").Select(r => r.Singer).Distinct().Count();
            md.AppendLine("## VocalSet: real singing, no f0 truth");
            md.AppendLine();
            md.AppendLine($"{results.Count} recordings ({results.Sum(r => r.Seconds) / 60:F0} minutes) of {results.Select(r => r.Singer).Distinct().Count()} singers ({nFemale} female, {nMale} male) from VocalSet 1.1 (the subset: up to {maxPerGroupPerSinger} files per singer and technique, vowels a, i, u first; the straight long tones all three vowels a, i, u). **Active** frames are those over -50 dBFS. **Voiced** is the share of active frames the tracker calls voiced (Voice range, Quality; the same for Low latency). **Octave jumps** are consecutive voiced estimates (2.7 ms apart, 1.35 ms for Low latency) that differ by 1000-1400 cents, per 1000 voiced estimates: in sung phrases of stepwise scales and arpeggios there are none of those from the singer, so each is an octave error. **Agreement** is Low latency within 50 cents of Quality where both are voiced; **octave** counts where they differ by an octave. **Held-note std** is the standard deviation (cents) of the pitch within a voiced run of at least 0.6 s, the first and last 0.15 s left out, median over the runs (long tones only: steady notes, vibrato and messa di voce excepted).");
            md.AppendLine();
            md.AppendLine("| Technique | files | active (s) | voiced (Q) | voiced (LL) | octave jumps /1000 (Q) | octave jumps /1000 (LL) | LL agrees with Q | LL octave-off | held-note std (ct) |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var g in results.GroupBy(r => r.Group).OrderBy(g => Array.FindIndex(Groups, x => x.Label == g.Key)))
                md.AppendLine(Row(g.Key, g));
            md.AppendLine(Row("**all**", results));
            md.AppendLine();
            md.AppendLine("By voice type:");
            md.AppendLine();
            md.AppendLine("| Voice | files | active (s) | voiced (Q) | voiced (LL) | octave jumps /1000 (Q) | octave jumps /1000 (LL) | LL agrees with Q | LL octave-off | held-note std (ct) |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var g in results.GroupBy(r => r.Voice).OrderBy(g => g.Key)) md.AppendLine(Row(g.Key, g));
            md.AppendLine();

            // Vowels.
            md.AppendLine("### Vowel estimator on labelled vowels");
            md.AppendLine();
            md.AppendLine("Each file's weights (aa, oo, ee) averaged over its voiced estimates, then the largest taken as the answer; the files are sung on one vowel each. The labels a, u, i are taken as aa, oo, ee (e and o sit between them and are left out).");
            md.AppendLine();
            md.AppendLine("| Sung | files | called aa | called oo | called ee | right | mean aa / oo / ee |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            int totalRight = 0, total = 0;
            foreach (var (v, idx, name) in new[] { ("a", 0, "a (aa)"), ("u", 1, "u (oo)"), ("i", 2, "i (ee)") })
            {
                var fs = results.Where(r => r.Vowel == v && r.VowelFrames > 20).ToList();
                var said = new int[3];
                foreach (var r in fs) { int arg = Array.IndexOf(r.VowelMean, r.VowelMean.Max()); said[arg]++; }
                totalRight += said[idx]; total += fs.Count;
                md.AppendLine($"| {name} | {fs.Count} | {said[0]} | {said[1]} | {said[2]} | {Fmt.P(fs.Count == 0 ? 0 : (double)said[idx] / fs.Count)} | {Fmt.F(Fmt.Mean(fs.Select(r => r.VowelMean[0])), "F2")} / {Fmt.F(Fmt.Mean(fs.Select(r => r.VowelMean[1])), "F2")} / {Fmt.F(Fmt.Mean(fs.Select(r => r.VowelMean[2])), "F2")} |");
            }
            md.AppendLine($"| **all three** | {total} | | | | {Fmt.P(total == 0 ? 0 : (double)totalRight / total)} | |");
            md.AppendLine();
            foreach (var voice in new[] { "female", "male" })
            {
                int r2 = 0, t2 = 0;
                foreach (var (v, idx) in new[] { ("a", 0), ("u", 1), ("i", 2) })
                    foreach (var r in results.Where(r => r.Vowel == v && r.Voice == voice && r.VowelFrames > 20)) { t2++; if (Array.IndexOf(r.VowelMean, r.VowelMean.Max()) == idx) r2++; }
                md.AppendLine($"- {voice}: {r2} of {t2} right ({Fmt.P(t2 == 0 ? 0 : (double)r2 / t2)}).");
            }
            md.AppendLine();
            HeldOutVowels(results, md);
        }

        /// <summary>
        /// The vowel estimator on singers it was not fitted on (docs/specs/voice-to-midi.md §9): PT1's constants were set on 24 files of eight
        /// of these singers, so its overall figure is not independent. Here every singer is a test singer in turn for a logistic model refitted on
        /// the others (leave-one-singer-out, on the three region differences), the figure for the PT1 weights is given per half (odd-numbered
        /// singers and even-numbered ones, one of which holds most of the fitting singers) and per singer, so a reader can leave out any set.
        /// </summary>
        static void HeldOutVowels(List<FileResult> results, StringBuilder md)
        {
            var files = results.Where(r => "aiu".Contains(r.Vowel) && r.Vowel != "" && r.VowelFrames > 20).ToList();
            if (files.Count < 30) return;
            int Label(FileResult r) => r.Vowel == "a" ? 0 : r.Vowel == "u" ? 1 : 2;
            int Pt1(FileResult r) => Array.IndexOf(r.VowelMean, r.VowelMean.Max());
            int Num(FileResult r) => int.Parse(new string(r.Singer.Where(char.IsDigit).ToArray()));
            md.AppendLine("#### Vowels on held-out singers");
            md.AppendLine();
            md.AppendLine("PT1's constants were set on 24 files of eight of these singers, so the figure above is partly in-sample. Two checks that do not depend on which eight: (1) the PT1 weights scored per half of the singers and per singer; (2) a model of the same three region differences (low minus mid, high minus mid, low minus high, in dB) **refitted without the test singer** (leave-one-singer-out, multinomial logistic, per-file means), so every singer is a held-out singer for it.");
            md.AppendLine();
            md.AppendLine("| Singers | files | PT1 weights right | refit, leave-one-singer-out, right |");
            md.AppendLine("|---|---:|---:|---:|");
            var loso = new Dictionary<FileResult, int>();
            foreach (var singer in files.Select(f => f.Singer).Distinct())
            {
                var train = files.Where(f => f.Singer != singer).ToList();
                var model = Logistic.Fit(train.Select(f => new[] { f.Lm, f.Hm, f.Lh }).ToList(), train.Select(Label).ToList(), 3);
                foreach (var f in files.Where(f => f.Singer == singer)) loso[f] = model.Predict(new[] { f.Lm, f.Hm, f.Lh });
            }
            void Line(string name, IEnumerable<FileResult> fs)
            {
                var l = fs.ToList();
                if (l.Count == 0) return;
                md.AppendLine($"| {name} | {l.Count} | {Fmt.P(l.Count(f => Pt1(f) == Label(f)) / (double)l.Count)} | {Fmt.P(l.Count(f => loso[f] == Label(f)) / (double)l.Count)} |");
            }
            Line("all", files);
            Line("odd-numbered singers", files.Where(f => Num(f) % 2 == 1));
            Line("even-numbered singers", files.Where(f => Num(f) % 2 == 0));
            Line("female", files.Where(f => f.Voice == "female"));
            Line("male", files.Where(f => f.Voice == "male"));
            foreach (var g in files.GroupBy(f => f.Vowel).OrderBy(g => g.Key)) Line("vowel " + g.Key + (g.Key == "a" ? " (aa)" : g.Key == "u" ? " (oo)" : " (ee)"), g);
            md.AppendLine();
            md.AppendLine("Per singer (PT1 weights / refit):");
            md.AppendLine();
            md.AppendLine("| Singer | files | PT1 | refit |");
            md.AppendLine("|---|---:|---:|---:|");
            foreach (var g in files.GroupBy(f => f.Singer).OrderBy(g => g.First().Voice).ThenBy(g => Num(g.First())))
                md.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(f => Pt1(f) == Label(f))} | {g.Count(f => loso[f] == Label(f))} |");
            md.AppendLine();
        }

        sealed class Logistic
        {
            double[,] w; int dim, classes; double[] mean, scale;
            public static Logistic Fit(List<double[]> x, List<int> y, int dim, int classes = 3)
            {
                var m = new Logistic { dim = dim, classes = classes, mean = new double[dim], scale = new double[dim], w = new double[classes, dim + 1] };
                for (int d = 0; d < dim; d++)
                {
                    m.mean[d] = x.Average(v => v[d]);
                    m.scale[d] = Math.Max(1e-6, Math.Sqrt(x.Average(v => (v[d] - m.mean[d]) * (v[d] - m.mean[d]))));
                }
                for (int it = 0; it < 400; it++)
                {
                    var g = new double[classes, dim + 1];
                    for (int i = 0; i < x.Count; i++)
                    {
                        var p = m.Probs(x[i]);
                        for (int c = 0; c < classes; c++)
                        {
                            double e = p[c] - (y[i] == c ? 1 : 0);
                            for (int d = 0; d < dim; d++) g[c, d] += e * (x[i][d] - m.mean[d]) / m.scale[d];
                            g[c, dim] += e;
                        }
                    }
                    for (int c = 0; c < classes; c++)
                        for (int d = 0; d <= dim; d++) m.w[c, d] -= 0.5 * (g[c, d] / x.Count + (d < dim ? 0.01 * m.w[c, d] : 0));
                }
                return m;
            }
            double[] Probs(double[] v)
            {
                var z = new double[classes];
                double max = double.MinValue;
                for (int c = 0; c < classes; c++)
                {
                    z[c] = w[c, dim];
                    for (int d = 0; d < dim; d++) z[c] += w[c, d] * (v[d] - mean[d]) / scale[d];
                    max = Math.Max(max, z[c]);
                }
                double sum = 0;
                for (int c = 0; c < classes; c++) { z[c] = Math.Exp(z[c] - max); sum += z[c]; }
                for (int c = 0; c < classes; c++) z[c] /= sum;
                return z;
            }
            public int Predict(double[] v) { var p = Probs(v); int b = 0; for (int c = 1; c < classes; c++) if (p[c] > p[b]) b = c; return b; }
        }

        static string Row(string name, IEnumerable<FileResult> rs)
        {
            var l = rs.ToList();
            long active = l.Sum(r => r.Active), vq = l.Sum(r => r.VoicedQ), vl = l.Sum(r => r.VoicedL);
            double hop = 0.0027;
            var std = l.SelectMany(r => r.RunStd).ToList();
            return $"| {name} | {l.Count} | {active * hop:F0} | {Fmt.P(vq / (double)Math.Max(1, active))} | {Fmt.P(vl / (double)Math.Max(1, active))} | {Fmt.F(1000.0 * l.Sum(r => r.OctJumpQ) / Math.Max(1, l.Sum(r => r.ConsecQ)), "F2")} | {Fmt.F(1000.0 * l.Sum(r => r.OctJumpL) / Math.Max(1, l.Sum(r => r.ConsecL)), "F2")} | {Fmt.P(l.Sum(r => r.Agree) / (double)Math.Max(1, l.Sum(r => r.BothVoiced)))} | {Fmt.P(l.Sum(r => r.OctaveDisagree) / (double)Math.Max(1, l.Sum(r => r.BothVoiced)))} | {(std.Count == 0 ? "" : Fmt.F(Fmt.Median(std)))} |";
        }

        static FileResult Analyse(float[] x, int rate, string group, string singer, string vowel)
        {
            var r = new FileResult { Group = group, Singer = singer, Voice = singer.StartsWith("f") ? "female" : "male", Vowel = vowel, Seconds = x.Length / (double)rate };
            var q = Track.Run(x, rate, PitchRange.Voice, PitchQuality.Quality);
            var l = Track.Run(x, rate, PitchRange.Voice, PitchQuality.LowLatency);
            const float ActiveRms = 0.00316f;
            for (int i = 0; i < q.Count; i++) if (q.Rms[i] > ActiveRms) { r.Active++; if (q.Voiced[i]) r.VoicedQ++; }
            for (int i = 0; i < l.Count; i++) if (l.Rms[i] > ActiveRms && l.Voiced[i]) r.VoicedL++;
            // Scale LL's voiced count to Q's hop so the shares compare.
            r.VoicedL = (long)(r.VoicedL * (double)l.Tracker.Hop / q.Tracker.Hop);
            Jumps(q, out r.ConsecQ, out r.OctJumpQ);
            Jumps(l, out r.ConsecL, out r.OctJumpL);
            // Agreement: for each Q estimate the nearest LL estimate.
            for (int i = 0; i < q.Count; i++)
            {
                if (!q.Voiced[i]) continue;
                int j = l.Nearest(q.T[i]);
                if (j < 0 || Math.Abs(l.T[j] - q.T[i]) > 0.004 || !l.Voiced[j]) continue;
                r.BothVoiced++;
                double cents = 1200 * Math.Log2(l.F0[j] / q.F0[i]);
                if (Math.Abs(cents) < 50) r.Agree++;
                else if (Math.Abs(cents - 1200 * Math.Round(cents / 1200)) < 50) r.OctaveDisagree++;
            }
            // Held notes: runs of voiced Q estimates.
            if (group.StartsWith("long tones, straight"))
            {
                int s = -1;
                for (int i = 0; i <= q.Count; i++)
                {
                    bool v = i < q.Count && q.Voiced[i];
                    if (v && s < 0) s = i;
                    if (!v && s >= 0)
                    {
                        if (q.T[i - 1] - q.T[s] >= 0.6) RunStd(q, s, i - 1, r);
                        s = -1;
                    }
                }
            }
            // The vowel estimator on the voiced estimates.
            if ("aiu".Contains(vowel) && vowel != "")
            {
                var ve = new VowelEstimator(rate);
                int block = 256, k = 0;
                double[] sum = new double[3]; int n = 0; double slm = 0, shm = 0, slh = 0;
                for (int pos = 0; pos + block <= x.Length; pos += block)
                {
                    ve.Push(x, pos, block);
                    double tNow = (pos + block) / (double)rate;
                    while (k + 1 < q.Count && q.End[k + 1] <= tNow) k++;
                    bool voiced = q.Count > 0 && q.Voiced[k] && q.Rms[k] > ActiveRms;
                    if ((pos / block) % 2 == 1)
                    {
                        var res = ve.Update(voiced);
                        if (voiced) { var rg = ve.Regions; slm += rg.Low - rg.Mid; shm += rg.High - rg.Mid; slh += rg.Low - rg.High; sum[0] += res.Aa; sum[1] += res.Oo; sum[2] += res.Ee; n++; }
                    }
                }
                if (n > 0) for (int c = 0; c < 3; c++) r.VowelMean[c] = sum[c] / n;
                if (Environment.GetEnvironmentVariable("PITCHEVAL_VDBG") != null && n > 0) Console.Error.WriteLine($"VDBG {singer} {vowel} lm {slm / n:F1} hm {shm / n:F1} lh {slh / n:F1} aa {sum[0] / n:F2} oo {sum[1] / n:F2} ee {sum[2] / n:F2}");
                r.VowelFrames = n;
                if (n > 0) { r.Lm = slm / n; r.Hm = shm / n; r.Lh = slh / n; }
            }
            return r;
        }

        static void RunStd(Track q, int from, int to, FileResult r)
        {
            double t0 = q.T[from] + 0.15, t1 = q.T[to] - 0.15;
            var c = new List<double>();
            for (int i = from; i <= to; i++) if (q.T[i] >= t0 && q.T[i] <= t1) c.Add(Math.Log2(q.F0[i]) * 1200);
            if (c.Count < 20) return;
            double med = c.OrderBy(v => v).ElementAt(c.Count / 2);
            double var2 = c.Sum(v => (v - med) * (v - med)) / c.Count;
            r.RunStd.Add(Math.Sqrt(var2));
        }

        static void Jumps(Track t, out long consec, out long octave)
        {
            consec = 0; octave = 0;
            for (int i = 1; i < t.Count; i++)
            {
                if (!t.Voiced[i] || !t.Voiced[i - 1]) continue;
                consec++;
                double d = Math.Abs(1200 * Math.Log2(t.F0[i] / t.F0[i - 1]));
                if (d >= 1000 && d <= 1400) octave++;
            }
        }
    }
}
