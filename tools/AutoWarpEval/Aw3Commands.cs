// AW3 sweep of the evaluation tool (docs/specs/neo-audio.md, AW3 as built):
//   --aw3 DIR SET MODEL OUT [--reach 0,25,35] [--resid inf:inf,25:50] [--onsets]
// For each NAME.wav / NAME.json of DIR (as fetch.py and prep.py write them): AW1's DSP track and the model's activations are computed once, then every setting
// (the model beats' attack reach in ms, and the residual bounds of the steady test, p95:max in ms) is applied to them and the grid as the app would warp it is written
// (beats, downbeats, tempo, markers). With --onsets: the strong onsets of the shared detector (NeoBuzz.Dsp.OnsetDetector) and the model's beat-activation peak at each annotated beat.
using System.Globalization;
using System.Text.Json;
using NeoBuzz.Dsp;
using NeoBuzz.PitchEval;
using ReBuzz.Core.Clips;

namespace NeoBuzz.AutoWarpEval
{
    internal static class Aw3Commands
    {
        public static int Run(string[] a, int at)
        {
            if (a.Length < at + 4) { Console.Error.WriteLine("--aw3 DIR SET MODEL OUT [--reach ..] [--resid ..] [--onsets]"); return 2; }
            string dir = a[at], set = a[at + 1], modelPath = a[at + 2], outPath = a[at + 3];
            var reaches = new List<double> { 0 };
            var resid = new List<(double p95, double max)> { (double.PositiveInfinity, double.PositiveInfinity) };
            bool onsets = false;
            int limit = int.MaxValue;
            string halfFilter = null;
            for (int i = at + 4; i < a.Length; i++)
            {
                switch (a[i])
                {
                    case "--reach": reaches = a[++i].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList(); break;
                    case "--resid": resid = a[++i].Split(',').Select(x => { var p = x.Split(':'); return (Num(p[0]), Num(p[1])); }).ToList(); break;
                    case "--onsets": onsets = true; break;
                    case "--half": halfFilter = a[++i]; break;
                    case "--limit": limit = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                }
            }
            var net = Aw2Commands.LoadModel(modelPath);
            var items = new List<Dictionary<string, object>>();
            int n = 0;
            foreach (var wav in Directory.GetFiles(dir, "*.wav").OrderBy(f => f, StringComparer.Ordinal))
            {
                string json = Path.ChangeExtension(wav, ".json");
                if (!File.Exists(json)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var root = doc.RootElement;
                string sp = root.TryGetProperty("split", out var s0) ? s0.GetString() : "";
                string half = sp == "train" ? "dev" : sp == "val" ? "val" : "test";
                if (halfFilter != null && !halfFilter.Split(',').Contains(half)) continue;
                if (n++ >= limit) break;
                double[] gtB = root.GetProperty("beats").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                double[] gtD = root.GetProperty("downbeats").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                int bpb = root.TryGetProperty("beats_per_bar", out var pb) ? pb.GetInt32() : 4;
                var (x, rate) = Wav.Read(wav);
                var src = new AudioSource(x, x, rate);
                string name = Path.GetFileNameWithoutExtension(wav);
                var item = new Dictionary<string, object> { ["set"] = set, ["name"] = name, ["half"] = half, ["bpb"] = bpb, ["seconds"] = Math.Round(src.Seconds, 2), ["gt_beats"] = Round(gtB), ["gt_downbeats"] = Round(gtD) };
                var res = new Dictionary<string, object>();
                var dsp = BeatTracker.Track(src.ForAnalysis());
                res["aw1"] = Describe(src, AudioAnalysis.FromTrack(src, dsp, bpb), rate, bpb);
                var act = BeatModelTracker.Run(src, net);
                foreach (double reach in reaches)
                {
                    BeatModelTracker.AttackReachMs = reach;
                    var t = BeatModelTracker.TrackFrom(src, act, net);
                    var final = t == null ? dsp : BeatTracker.PreferDsp(dsp, t) ? dsp : t;
                    foreach (var (p95, max) in resid)
                    {
                        AudioAnalysis.SteadyResidualP95Ms = p95; AudioAnalysis.SteadyResidualMaxMs = max;
                        res[$"r{reach:0}_p{Fmt(p95)}_m{Fmt(max)}"] = Describe(src, AudioAnalysis.FromTrack(src, final, bpb), rate, bpb);
                    }
                    // The raw track of the model (before the DSP-or-model choice): where its beats are against the annotations.
                    if (t != null && onsets) res[$"model_r{reach:0}"] = new Dictionary<string, object> { ["beats"] = Round(t.Beats.Select(v => v / rate)), ["bpm"] = Math.Round(t.MedianBpm, 3) };
                }
                AudioAnalysis.SteadyResidualP95Ms = AudioAnalysis.SteadyResidualMaxMs = double.PositiveInfinity;
                item["res"] = res;
                if (onsets)
                {
                    item["onsets"] = Onsets(src);
                    item["peak_off_ms"] = PeakOffsets(act.Beat, gtB);
                }
                items.Add(item);
            }
            File.WriteAllText(outPath, JsonSerializer.Serialize(items));
            Console.WriteLine($"{items.Count} files -> {outPath}");
            return 0;
        }

        static double Num(string s) => s == "inf" ? double.PositiveInfinity : double.Parse(s, CultureInfo.InvariantCulture);
        static string Fmt(double v) => double.IsInfinity(v) ? "inf" : v.ToString("0", CultureInfo.InvariantCulture);

        static Dictionary<string, object> Describe(AudioSource src, AutoWarpResult r, int rate, int bpb)
        {
            var beats = new List<double>();
            var downs = new List<double>();
            if (r.Found)
            {
                double seconds = src.Seconds;
                WarpMap map = r.Markers.Count >= 2 ? new WarpMap(r.Markers.Select(m => new WarpMarker(m.Sample, m.Beat))) : WarpMap.Straight(src.Length, seconds * r.Bpm / 60);
                for (int k = (int)Math.Ceiling(map.BeatAt(0)); k < 1_000_000; k++)
                {
                    double s = map.SampleAt(k);
                    if (s >= src.Length) break;
                    if (s < 0) continue;
                    beats.Add(s / rate);
                    if (k % bpb == 0) downs.Add(s / rate);
                }
            }
            return new Dictionary<string, object> { ["found"] = r.Found, ["bpm"] = Math.Round(r.Bpm, 3), ["markers"] = r.Markers.Count, ["steady"] = r.Steady, ["loop"] = r.Loop, ["from_model"] = r.FromModel, ["beats"] = Round(beats), ["downbeats"] = Round(downs) };
        }

        /// <summary>Strong onsets of the shared detector (time in s, strength), strength 2 and over.</summary>
        static double[][] Onsets(AudioSource src)
        {
            var mono = BeatFront.Mono22k(src);
            var det = new OnsetDetector(BeatFront.Rate) { MinIntervalSeconds = 0.04 };
            var list = new List<double[]>();
            int block = 1 << 16;
            var buf = new OnsetEvent[det.MaxEvents(block) + 4];
            for (int pos = 0; pos < mono.Length; pos += block)
            {
                int c = Math.Min(block, mono.Length - pos);
                int got = det.Process(mono, pos, c, buf, 0);
                for (int i = 0; i < got; i++) if (buf[i].Strength >= 2) list.Add(new[] { Math.Round(buf[i].Sample / (double)BeatFront.Rate, 4), Math.Round(buf[i].Strength, 1) });
            }
            return list.ToArray();
        }

        /// <summary>Where the model's beat activation peaks within 3 frames of each annotated beat (parabolic), minus the annotation, ms: the model's own bias before any decoding.</summary>
        static double[] PeakOffsets(float[] act, double[] gt)
        {
            var o = new List<double>();
            foreach (double g in gt)
            {
                int f = (int)Math.Round(g * 50);
                if (f < 4 || f > act.Length - 5) continue;
                int best = f;
                for (int u = f - 3; u <= f + 3; u++) if (act[u] > act[best]) best = u;
                if (act[best] < 0.3f) continue;
                double l = act[best - 1], c = act[best], r = act[best + 1], den = l - 2 * c + r, d = den < -1e-6 ? Math.Clamp(0.5 * (l - r) / den, -0.5, 0.5) : 0;
                o.Add(Math.Round(((best + d) / 50.0 - g) * 1000, 1));
            }
            return o.ToArray();
        }

        static double[] Round(IEnumerable<double> v) => v.Select(q => Math.Round(q, 4)).ToArray();
    }
}
