// Auto-warp evaluation (docs/specs/neo-audio.md, AW1 as built). Runs auto-warp and today's steady-tempo detection on recordings with known beats and
// writes what each found as JSON, for tools/autowarp/score.py:
//
//   AutoWarpEval --out results.json [--generated] [--dir DIR --set NAME [--limit N]] [--beats-per-bar 4]
//
//  generated  the lines of the repo (ReBuzz.SessionTests/AutoWarp/WarpSignals.cs): a steady tempo, a slow drift, a step and a ritardando, each as drums,
//             a bass and drums mix and a pad (weak onsets); two variants each (the first is the development half, the second is held out).
//  dir        NAME.wav and NAME.json ({"beats": [seconds], "downbeats": [seconds], "beats_per_bar": 4}) pairs, as tools/autowarp/fetch_data.py writes them;
//             the half of each is taken from its name (a hash), so a file is always in the same half.
//
// Per file: the beats and downbeats the markers give (the clip's beat 0, 1, 2, ... in the audio), those of today's detection (a straight grid from the
// start of the file at the tempo the import finds), and the grid error: how far each ground-truth beat lands from the nearest beat of the grid once the
// clip is warped, in milliseconds at 120 BPM (the song's tempo in the listening pack's example is another one; the error scales with it).

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NeoBuzz.PitchEval;
using ReBuzz.Core.Clips;
using ReBuzz.SessionTests.AutoWarp;


namespace NeoBuzz.AutoWarpEval
{
    internal static class Program
    {
        const double SongBpm = 120;
        internal static BeatModelNet Aw2Model;

        static int Main(string[] args)
        {
            string outPath = "autowarp-results.json", dir = null, set = null, modelPath = null;
            bool generated = false;
            int limit = int.MaxValue, bpb = 4;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + a);
                switch (a)
                {
                    case "--out": outPath = Next(); break;
                    case "--generated": generated = true; break;
                    case "--dir": dir = Next(); break;
                    case "--set": set = Next(); break;
                    case "--model": modelPath = Next(); break;
                    case "--parity": return Aw2Commands.Parity(Next());
                    case "--pulses": return Aw2Commands.Pulses();
                    case "--feat": return Aw2Commands.Feat(Next());
                    case "--gen-train": { string gd = Next(); return Aw2Commands.GenTrain(gd, int.Parse(Next(), CultureInfo.InvariantCulture)); }
                    case "--dsp-steady": BeatTracker.DspSteadyTolerance = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--tempo-prior": BeatModelTracker.TempoPriorSigma = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--dsp-phase": BeatTracker.DspPhaseRatio = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--dsp-agree": BeatTracker.DspAgreeTolerance = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--bench": return Aw2Commands.Bench(Next(), args.Length > i + 1 ? args[i + 1] : "600");
                    case "--limit": limit = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--beats-per-bar": bpb = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--pack": return Pack.Run(args, i + 1);
                    case "--pack2": return Pack2.Run(args, i + 1);
                    case "--pack3": return Pack3.Run(args, i + 1);
                    case "--pack5": return Pack5.Run(args, i + 1);
                    case "--aw3": return Aw3Commands.Run(args, i + 1);
                    case "--dump":
                        {
                            var l = Variant(Enum.Parse<WarpSignals.Tempo>(Next(), true), Enum.Parse<WarpSignals.Style>(Next(), true), int.Parse(Next()));
                            Pack.WriteWav(Next(), l.Audio, l.Rate);
                            return 0;
                        }
                    case "--debugfile":
                        {
                            string wav = Next();
                            var (x, rate) = Wav.Read(wav);
                            var src = new AudioSource(x, x, rate);
                            var (env, lowEnv, frate, period) = BeatTracker.Debug(src);
                            var bt = BeatTracker.Track(src);
                            Console.WriteLine($"pass-1 tempo (bpm) every 2 s: " + string.Join(" ", Enumerable.Range(0, (int)(env.Length / frate / 2)).Select(k => (60 * frate / period[Math.Min(period.Length - 1, (int)(k * 2 * frate))]).ToString("0.0"))));
                            Console.WriteLine($"final median {bt.MedianBpm:0.00}; beats {bt.Beats.Length}; length {src.Seconds:0.0} s");
                            Console.WriteLine("beats: " + string.Join(" ", bt.Beats.Select(b => (b / rate).ToString("0.00"))));
                            Console.WriteLine("env>1 peaks: " + string.Join(" ", Enumerable.Range(1, env.Length - 2).Where(q => env[q] > 1.5 && env[q] >= env[q - 1] && env[q] >= env[q + 1]).Select(q => (q / frate).ToString("0.00"))));
                            return 0;
                        }
                    case "--long":
                        {
                            var one = Variant(WarpSignals.Tempo.Drift, WarpSignals.Style.Mix, 0);
                            int reps = int.Parse(Next());
                            var x = new float[one.Audio.Length * reps];
                            for (int r = 0; r < reps; r++) Array.Copy(one.Audio, 0, x, r * one.Audio.Length, one.Audio.Length);
                            var src = new AudioSource(x, x, one.Rate);
                            var sw = Stopwatch.StartNew();
                            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
                            var res = AudioAnalysis.AutoWarp(src, 4);
                            Console.WriteLine($"{src.Seconds:0} s of audio: {sw.Elapsed.TotalMilliseconds:0} ms wall, {(Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds:0} ms cpu, markers {res.Markers.Count}, bpm {res.Bpm:0.00}");
                            return 0;
                        }
                    case "--fftcheck": FftCheck(); return 0;
                    case "--sine":
                        {
                            var x = new float[44100 * 3];
                            for (int q = 0; q < x.Length; q++) x[q] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * q / 44100.0));
                            BeatTracker.Debug(new AudioSource(x, x, 44100));
                            return 0;
                        }
                    case "--debug": return Debug(Next(), Next(), int.Parse(Next()));
                    default: throw new ArgumentException("unknown argument " + a);
                }
            }
            if (modelPath != null) { Aw2Model = Aw2Commands.LoadModel(modelPath); Console.WriteLine($"model {modelPath}: version {Aw2Model.Version}, d {Aw2Model.D}"); }
            BeatModel.Use(null);                       // "auto" is AW1's DSP tracker; "aw2" scopes the model in
            var items = new List<Dictionary<string, object>>();
            if (generated)
            {
                foreach (var tempo in Enum.GetValues<WarpSignals.Tempo>())
                    foreach (var style in Enum.GetValues<WarpSignals.Style>())
                        for (int variant = 0; variant < 2; variant++)
                        {
                            var line = Variant(tempo, style, variant);
                            items.Add(Score("generated", $"{tempo}-{style}-{variant}".ToLowerInvariant(), variant == 0 ? "dev" : "test", line.Audio, line.Rate, line.Beats, line.Downbeats, line.BeatsPerBar));
                        }
            }
            if (generated)
            {
                // Steady loops of whole bars (16 bars each) at four tempos: the import must keep today's tempo for them (the gate: within 0.1 BPM).
                foreach (var style in new[] { WarpSignals.Style.Drums, WarpSignals.Style.Mix })
                    foreach (double scale in new[] { 1.0, 0.92, 1.1, 0.8 })
                    {
                        var line = WarpSignals.Make(WarpSignals.Tempo.Steady, style, 16 * 4 * 60 / (120 * scale), seed: 3, scale: scale);
                        items.Add(Score("loops", $"loop-{style}-{120 * scale:0}".ToLowerInvariant(), "all", line.Audio, line.Rate, line.Beats, line.Downbeats, line.BeatsPerBar));
                    }
            }
            if (dir != null)
            {
                int n = 0;
                foreach (var wav in Directory.GetFiles(dir, "*.wav").OrderBy(f => f, StringComparer.Ordinal))
                {
                    if (n++ >= limit) break;
                    string json = Path.ChangeExtension(wav, ".json");
                    if (!File.Exists(json)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(json));
                    var root = doc.RootElement;
                    double[] beats = root.GetProperty("beats").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    double[] downs = root.GetProperty("downbeats").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    int perBar = root.TryGetProperty("beats_per_bar", out var pb) ? pb.GetInt32() : bpb;
                    var (x, rate) = Wav.Read(wav);
                    string name = Path.GetFileNameWithoutExtension(wav);
                    string half = root.TryGetProperty("split", out var sp) ? (sp.GetString() == "train" ? "dev" : sp.GetString() == "val" ? "val" : "test") : HalfOf(name);
                    items.Add(Score(set ?? "data", name, half, x, rate, beats, downs, perBar));
                }
            }
            File.WriteAllText(outPath, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = false }));
            Console.WriteLine($"{items.Count} files -> {outPath}");
            return 0;
        }

        static void FftCheck()
        {
            int N = 512;
            var fft = new Fft(N);
            var rnd = new Random(3);
            var xa = new double[N]; var xb = new double[N];
            for (int i = 0; i < N; i++) { xa[i] = rnd.NextDouble() - 0.5; xb[i] = Math.Sin(i * 0.3) + rnd.NextDouble() * 0.1; }
            var ra = (double[])xa.Clone(); var ia = new double[N];
            fft.Transform(ra, ia);
            var rb = (double[])xb.Clone(); var ib = new double[N];
            fft.Transform(rb, ib);
            var re = (double[])xa.Clone(); var im = (double[])xb.Clone();
            fft.Transform(re, im);
            for (int k = 1; k < 6; k++)
            {
                double pa = 0.25 * (Math.Pow(re[k] + re[N - k], 2) + Math.Pow(im[k] - im[N - k], 2));
                double pb = 0.25 * (Math.Pow(re[k] - re[N - k], 2) + Math.Pow(im[k] + im[N - k], 2));
                Console.WriteLine($"{k}: A {ra[k] * ra[k] + ia[k] * ia[k]:0.000} vs {pa:0.000}   B {rb[k] * rb[k] + ib[k] * ib[k]:0.000} vs {pb:0.000}");
            }
        }

        static int Debug(string tempo, string style, int variant)
        {
            var line = Variant(Enum.Parse<WarpSignals.Tempo>(tempo, true), Enum.Parse<WarpSignals.Style>(style, true), variant);
            var src = new AudioSource(line.Audio, line.Audio, line.Rate);
            var track = BeatTracker.Track(src);
            Console.WriteLine($"gt beats {line.Beats.Length}, tracked {track.Beats.Length}, median bpm {track.MedianBpm:0.00}, first downbeat gt {line.FirstDownbeat:0.000}");
            var (env, lowEnv, frate, period) = BeatTracker.Debug(src);
            Console.WriteLine($"frame rate {frate:0.00}; period at frame 100, 1000: {(period == null ? 0 : 60 * frate / period[100]):0.0}, {(period == null ? 0 : 60 * frate / period[1000]):0.0} bpm");
            var peaks = new List<(double t, double v)>();
            for (int i = 1; i < env.Length - 1; i++) if (env[i] > 1.0 && env[i] >= env[i - 1] && env[i] >= env[i + 1]) peaks.Add((i / frate, env[i]));
            Console.WriteLine("env peaks (<4 s): " + string.Join(" ", peaks.Where(p => p.t < 4).Select(p => $"{p.t:0.000}:{p.v:0.0}")));
            if (Environment.GetEnvironmentVariable("ENV") != null)
                for (int i = 0; i < Math.Min(env.Length, 120); i++) Console.WriteLine($"{i / frate:0.000} env {env[i],5:0.00} low {lowEnv[i],5:0.00} " + new string('#', (int)Math.Min(60, env[i] * 10)));
            int n = Math.Min(track.Beats.Length, 40);
            for (int i = 0; i < n; i++)
            {
                double t = track.Beats[i] / line.Rate;
                double nearest = line.Beats.OrderBy(b => Math.Abs(b - t)).First();
                Console.WriteLine($"{i,3} {t,8:0.000}  gt {nearest,8:0.000}  d {(t - nearest) * 1000,7:0.0} ms  low {track.Low[i]:0.00} full {track.Full[i]:0.00} chg {track.Change[i]:0.00}");
            }
            return 0;
        }

        /// <summary>The development half or the held-out one, from the name only (FNV-1a), so a file never changes sides.</summary>
        internal static string HalfOf(string name)
        {
            uint h = 2166136261;
            foreach (char c in name) { h ^= c; h *= 16777619; }
            return (h >> 7 & 1) == 0 ? "dev" : "test";
        }

        /// <summary>A generated line; the second variant (held out) has another tempo, another drift period and phase, other noise and a pickup and a silence at the start.</summary>
        internal static WarpLine Variant(WarpSignals.Tempo tempo, WarpSignals.Style style, int variant) =>
            variant == 0
                ? WarpSignals.Make(tempo, style, 64, seed: 1)
                : WarpSignals.Make(tempo, style, 64, seed: 2, lead: 1.3, pickup: 1, scale: 0.92, period: 52, phase: 1.1);

        static Dictionary<string, object> Score(string set, string name, string half, float[] audio, int rate, double[] gtBeats, double[] gtDown, int bpb)
        {
            var src = new AudioSource(audio, audio, rate);
            var sw = Stopwatch.StartNew();
            var auto = AudioAnalysis.AutoWarp(src, bpb);
            double ms = sw.Elapsed.TotalMilliseconds;
            var (todayBpm, _) = AudioAnalysis.Tempo(src, bpb, out bool todayFound);
            double seconds = src.Seconds;
            WarpMap autoMap = auto.Found && auto.Markers.Count >= 2
                ? new WarpMap(auto.Markers.Select(m => new WarpMarker(m.Sample, m.Beat)))
                : WarpMap.Straight(src.Length, seconds * (auto.Found ? auto.Bpm : todayBpm) / 60);
            var todayMap = WarpMap.Straight(src.Length, seconds * todayBpm / 60);
            Dictionary<string, object> aw2 = null;
            if (Aw2Model != null)
            {
                var src2 = new AudioSource(audio, audio, rate);
                BeatModel.Use(Aw2Model);
                var sw2 = Stopwatch.StartNew();
                var r2 = AudioAnalysis.AutoWarp(src2, bpb);
                double ms2 = sw2.Elapsed.TotalMilliseconds;
                BeatModel.Use(null);
                WarpMap map2 = r2.Found && r2.Markers.Count >= 2
                    ? new WarpMap(r2.Markers.Select(m => new WarpMarker(m.Sample, m.Beat)))
                    : WarpMap.Straight(src.Length, seconds * (r2.Found ? r2.Bpm : todayBpm) / 60);
                aw2 = Method(map2, rate, src.Length, bpb, gtBeats, r2.Found ? r2.Bpm : 0, new Dictionary<string, object> { ["found"] = r2.Found, ["steady"] = r2.Steady, ["loop"] = r2.Loop, ["from_model"] = r2.FromModel, ["markers"] = r2.Markers.Count, ["analysis_ms"] = Math.Round(ms2, 1) });
            }
            var item = new Dictionary<string, object>
            {
                ["set"] = set, ["name"] = name, ["half"] = half, ["bpb"] = bpb, ["seconds"] = Math.Round(seconds, 2), ["analysis_ms"] = Math.Round(ms, 1),
                ["gt_beats"] = Round(gtBeats), ["gt_downbeats"] = Round(gtDown),
                ["auto"] = Method(autoMap, rate, src.Length, bpb, gtBeats, auto.Found ? auto.Bpm : 0, new Dictionary<string, object> { ["found"] = auto.Found, ["steady"] = auto.Steady, ["loop"] = auto.Loop, ["markers"] = auto.Markers.Count }),
                ["today"] = Method(todayMap, rate, src.Length, bpb, gtBeats, todayBpm, new Dictionary<string, object> { ["found"] = todayFound }),
            };
            if (aw2 != null) item["aw2"] = aw2;
            return item;
        }

        static Dictionary<string, object> Method(WarpMap map, int rate, int length, int bpb, double[] gtBeats, double bpm, Dictionary<string, object> extra)
        {
            var beats = new List<double>();
            var downs = new List<double>();
            for (int k = (int)Math.Ceiling(map.BeatAt(0)); k < 1_000_000; k++)
            {
                double s = map.SampleAt(k);
                if (s >= length) break;
                if (s < 0) continue;
                beats.Add(s / rate);
                if (k % bpb == 0) downs.Add(s / rate);
            }
            var err = new List<double>();
            foreach (double t in gtBeats)
            {
                double s = t * rate;
                if (s < 0 || s >= length) continue;
                double b = map.BeatAt(s);
                err.Add(Math.Abs(b - Math.Round(b)) * 60000 / SongBpm);
            }
            extra["bpm"] = Math.Round(bpm, 3);
            extra["beats"] = Round(beats);
            extra["downbeats"] = Round(downs);
            extra["grid_ms"] = Round(err, 2);
            return extra;
        }

        static double[] Round(IEnumerable<double> x, int digits = 4) => x.Select(v => Math.Round(v, digits)).ToArray();
    }
}
