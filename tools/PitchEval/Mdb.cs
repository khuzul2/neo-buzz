// MDB-stem-synth (docs/specs/pitch.md §7.1): 230 resynthesised solo stems with an exact f0 every 128/44100 s. Raw pitch accuracy
// at 50 cents, raw chroma accuracy, voicing recall and false alarm, overall accuracy (mir_eval.melody's definitions), per stem
// and overall, for each range and for Low latency against Quality.

using System.Globalization;
using System.Text;

namespace NeoBuzz.PitchEval
{
    internal sealed class MelodyAcc
    {
        public long RefVoiced, RefUnvoiced, VoicedHit, FalseAlarm, Rpa, Rca, Octave, Correct, Oa;
        public void Add(MelodyAcc o)
        {
            RefVoiced += o.RefVoiced; RefUnvoiced += o.RefUnvoiced; VoicedHit += o.VoicedHit; FalseAlarm += o.FalseAlarm;
            Rpa += o.Rpa; Rca += o.Rca; Octave += o.Octave; Correct += o.Correct; Oa += o.Oa;
        }
        public double RpaP => RefVoiced == 0 ? double.NaN : (double)Rpa / RefVoiced;
        public double RcaP => RefVoiced == 0 ? double.NaN : (double)Rca / RefVoiced;
        public double VrP => RefVoiced == 0 ? double.NaN : (double)VoicedHit / RefVoiced;
        public double VfaP => RefUnvoiced == 0 ? double.NaN : (double)FalseAlarm / RefUnvoiced;
        public double OaP => RefVoiced + RefUnvoiced == 0 ? double.NaN : (double)Oa / (RefVoiced + RefUnvoiced);
        public double OctP => RefVoiced == 0 ? double.NaN : (double)Octave / RefVoiced;
        /// <summary>Of the frames the tracker called voiced where the reference is voiced: how many within 50 cents.</summary>
        public double VoicedPitchP => VoicedHit == 0 ? double.NaN : (double)Correct / VoicedHit;
    }

    internal static class Mdb
    {
        public static void Run(string dir, int limit, StringBuilder md, StringBuilder csv)
        {
            string audio = Path.Combine(dir, "audio_stems"), ann = Path.Combine(dir, "annotation_stems");
            if (!Directory.Exists(audio))
            {
                var nested = Directory.GetDirectories(dir).FirstOrDefault(d => Directory.Exists(Path.Combine(d, "audio_stems")));
                if (nested != null) { audio = Path.Combine(nested, "audio_stems"); ann = Path.Combine(nested, "annotation_stems"); }
            }
            var files = Directory.GetFiles(audio, "*.wav").Where(f => !Path.GetFileName(f).StartsWith("._")).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (limit > 0 && files.Count > limit)
            {
                // An even spread, so a limited run still has voices and instruments.
                files = Enumerable.Range(0, limit).Select(i => files[(int)((long)i * files.Count / limit)]).ToList();
            }
            Console.WriteLine($"MDB-stem-synth: {files.Count} stems");
            var cfgs = Config.All;
            var perStem = new MelodyAcc[files.Count][];
            var medians = new double[files.Count];
            var names = files.Select(f => Path.GetFileName(f).Replace(".RESYN.wav", "")).ToArray();
            double seconds = 0;
            int done = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, i =>
            {
                var (x, rate) = Wav.Read(files[i]);
                string csvPath = Path.Combine(ann, Path.GetFileName(files[i]).Replace(".wav", ".csv"));
                var refT = new List<double>(); var refF = new List<double>();
                foreach (var line in File.ReadLines(csvPath))
                {
                    var p = line.Split(',');
                    if (p.Length < 2) continue;
                    refT.Add(double.Parse(p[0], CultureInfo.InvariantCulture)); refF.Add(double.Parse(p[1], CultureInfo.InvariantCulture));
                }
                var voicedRef = refF.Where(f => f > 0).OrderBy(f => f).ToList();
                medians[i] = voicedRef.Count > 0 ? voicedRef[voicedRef.Count / 2] : 0;
                var acc = new MelodyAcc[cfgs.Length];
                for (int c = 0; c < cfgs.Length; c++)
                {
                    var tr = Track.Run(x, rate, cfgs[c].Range, cfgs[c].Quality);
                    acc[c] = Score(tr, refT, refF);
                }
                perStem[i] = acc;
                lock (names)
                {
                    seconds += x.Length / (double)rate;
                    done++;
                    if (done % 20 == 0) Console.WriteLine($"  {done}/{files.Count} stems, {sw.Elapsed.TotalSeconds:F0} s");
                }
            });
            double wall = sw.Elapsed.TotalSeconds;

            md.AppendLine("## MDB-stem-synth: melody extraction (f0 every 2.9 ms)");
            md.AppendLine();
            md.AppendLine($"{files.Count} stems, {seconds / 60:F0} minutes of audio, evaluated in {wall:F0} s on the runner ({seconds / Math.Max(wall, 1e-9) * 1:F0} x real time over all ranges together). Frames are scored at the reference's 2.9 ms grid against the tracker's nearest estimate (each time-aligned to the middle of the audio it analysed: the newest window and the same one a period earlier), mir_eval.melody's definitions: **RPA** raw pitch accuracy (reference voiced, the tracker's best candidate within 50 cents, voiced or not), **RCA** the same with octaves folded, **VR** voicing recall, **VFA** voicing false alarm, **OA** overall accuracy (voicing right, and for voiced frames pitch within 50 cents and called voiced), **Oct** octave errors (reference voiced, right chroma, wrong octave) as a share of reference-voiced frames. 'In range' is the stems whose median pitch lies inside the range's own limits.");
            md.AppendLine();
            md.AppendLine("| Range | stems | RPA | RCA | VR | VFA | OA | Oct | mean of stems' RPA |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            for (int c = 0; c < cfgs.Length; c++)
            {
                var all = new MelodyAcc();
                for (int i = 0; i < files.Count; i++) all.Add(perStem[i][c]);
                md.AppendLine($"| {cfgs[c].Name}, all stems | {files.Count} | {Fmt.P(all.RpaP)} | {Fmt.P(all.RcaP)} | {Fmt.P(all.VrP)} | {Fmt.P(all.VfaP)} | {Fmt.P(all.OaP)} | {Fmt.P(all.OctP)} | {Fmt.P(Fmt.Mean(Enumerable.Range(0, files.Count).Select(i => perStem[i][c].RpaP).Where(v => !double.IsNaN(v))))} |");
                var tmp = new PitchTrackerInfo(cfgs[c]);
                var inRange = Enumerable.Range(0, files.Count).Where(i => medians[i] >= tmp.Lo * 1.05 && medians[i] <= tmp.Hi * 0.95).ToList();
                var ir = new MelodyAcc();
                foreach (int i in inRange) ir.Add(perStem[i][c]);
                md.AppendLine($"| {cfgs[c].Name}, in range ({tmp.Lo:F0}-{tmp.Hi:F0} Hz) | {inRange.Count} | {Fmt.P(ir.RpaP)} | {Fmt.P(ir.RcaP)} | {Fmt.P(ir.VrP)} | {Fmt.P(ir.VfaP)} | {Fmt.P(ir.OaP)} | {Fmt.P(ir.OctP)} | {Fmt.P(Fmt.Mean(inRange.Select(i => perStem[i][c].RpaP).Where(v => !double.IsNaN(v))))} |");
            }
            md.AppendLine();

            // Low latency against Quality, on the stems both cover.
            {
                var q = new MelodyAcc(); var l = new MelodyAcc();
                var tmp = new PitchTrackerInfo(cfgs[1]);
                foreach (int i in Enumerable.Range(0, files.Count).Where(i => medians[i] >= tmp.Lo * 1.05 && medians[i] <= tmp.Hi * 0.95)) { q.Add(perStem[i][0]); l.Add(perStem[i][1]); }
                md.AppendLine($"Low latency against Quality on the stems in the Low latency range: RPA {Fmt.P(l.RpaP)} against {Fmt.P(q.RpaP)}, octave errors {Fmt.P(l.OctP)} against {Fmt.P(q.OctP)}, VFA {Fmt.P(l.VfaP)} against {Fmt.P(q.VfaP)}.");
                md.AppendLine();
            }

            md.AppendLine("<details><summary>Per stem (median reference pitch, then RPA / OA per range)</summary>");
            md.AppendLine();
            md.AppendLine("| Stem | median Hz | " + string.Join(" | ", cfgs.Select(c => c.Name + " RPA / OA")) + " |");
            md.AppendLine("|---|---:|" + string.Join("|", cfgs.Select(_ => "---:")) + "|");
            csv.AppendLine("stem,median_hz,config,rpa,rca,vr,vfa,oa,octave");
            for (int i = 0; i < files.Count; i++)
            {
                md.AppendLine($"| {names[i]} | {medians[i]:F0} | " + string.Join(" | ", Enumerable.Range(0, cfgs.Length).Select(c => $"{Fmt.P(perStem[i][c].RpaP)} / {Fmt.P(perStem[i][c].OaP)}")) + " |");
                for (int c = 0; c < cfgs.Length; c++)
                {
                    var a = perStem[i][c];
                    csv.AppendLine(string.Join(",", names[i], medians[i].ToString("F1", CultureInfo.InvariantCulture), "\"" + cfgs[c].Name + "\"", F(a.RpaP), F(a.RcaP), F(a.VrP), F(a.VfaP), F(a.OaP), F(a.OctP)));
                }
            }
            md.AppendLine();
            md.AppendLine("</details>");
            md.AppendLine();
        }

        static string F(double v) => double.IsNaN(v) ? "" : v.ToString("F4", CultureInfo.InvariantCulture);

        sealed class PitchTrackerInfo
        {
            public readonly double Lo, Hi;
            public PitchTrackerInfo(Config c) { var t = new NeoBuzz.Dsp.PitchTracker(44100, c.Range, c.Quality); Lo = t.MinHz; Hi = t.MaxHz; }
        }

        static MelodyAcc Score(Track tr, List<double> refT, List<double> refF)
        {
            var a = new MelodyAcc();
            for (int k = 0; k < refT.Count; k++)
            {
                double t = refT[k];
                int j = tr.Nearest(t);
                bool have = j >= 0 && Math.Abs(tr.T[j] - t) < 0.02;
                bool estVoiced = have && tr.Voiced[j];
                double raw = have ? tr.Raw[j] : 0;
                if (refF[k] > 0)
                {
                    a.RefVoiced++;
                    if (estVoiced) a.VoicedHit++;
                    if (raw > 0)
                    {
                        double cents = 1200 * Math.Log2(raw / refF[k]);
                        double folded = cents - 1200 * Math.Round(cents / 1200);
                        bool exact = Math.Abs(cents) < 50;
                        if (exact) a.Rpa++;
                        if (Math.Abs(folded) < 50) a.Rca++;
                        if (!exact && Math.Abs(folded) < 50) a.Octave++;
                        if (estVoiced && exact) { a.Correct++; a.Oa++; }
                    }
                }
                else
                {
                    a.RefUnvoiced++;
                    if (estVoiced) a.FalseAlarm++; else a.Oa++;
                }
            }
            return a;
        }
    }
}
