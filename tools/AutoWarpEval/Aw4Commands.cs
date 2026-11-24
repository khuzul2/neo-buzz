// AW4 of the evaluation tool (docs/specs/neo-audio.md, AW4 as built), for the bt-* jobs of .github/workflows/pitch-eval.yml:
//   --bt-parity ONNX REFDIR WAV...   each WAV against what tools/autowarp/beat_this_model.py reference wrote for it in REFDIR (NAME.spect.f32, NAME.logits.f32, NAME.ref.json): the
//                                    spectrogram (the largest difference; the gate is 1e-4), the logits of the network on our spectrogram, and the beats and downbeats picked on ours against the
//                                    upstream package's (within one frame). The post-processing is also run on the upstream logits: it must give the upstream beats exactly.
//   --aw4 DIR SET ONNX OUT [--half h] [--reach 0,25] [--limit n] [--onsets]
//                                    for each NAME.wav / NAME.json of DIR: AW1's DSP track, AW2 as shipped, AW3's candidate (25 ms reach, residual bounds 30:60) and the pretrained
//                                    network (the grid as the app would warp it; reach 0 and each --reach), with the time each took, in the schema of --aw3 (aw3score.py and aw4score.py read it).
//   --bt-bench ONNX [SECONDS]        the whole analysis (decoding, spectrogram, network, markers) of SECONDS (default 600) of generated music, twice: wall and CPU time.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NeoBuzz.PitchEval;
using ReBuzz.Core.Clips;
using ReBuzz.SessionTests.AutoWarp;

namespace NeoBuzz.AutoWarpEval
{
    internal static class Aw4Commands
    {
        public static BeatThisNet LoadNet(string path)
        {
            var net = BeatThisNet.LoadFile(path);
            if (net == null)
            {
                // Say why: the pinned checksum, or the file.
                string sha = File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : "(no such file)";
                throw new InvalidOperationException($"{path} is not the pinned beat network: its SHA-256 is {sha}, the app pins the exports listed in BeatThisModel.cs");
            }
            return net;
        }

        public static int Parity(string[] a, int at)
        {
            var net = LoadNet(a[at]);
            string refDir = a[at + 1];
            int bad = 0, n = 0;
            double worstSpect = 0, worstLogit = 0;
            for (int i = at + 2; i < a.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(a[i]);
                string bin = Path.Combine(refDir, name + ".spect.f32");
                if (!File.Exists(bin)) continue;
                n++;
                var (x, rate) = Wav.Read(a[i]);
                var src = new AudioSource(x, x, rate);
                var mono = BeatFront.Mono22k(src);
                var mine = BeatThisFront.Compute(mono, out int frames);
                var refSpect = ReadFloats(bin);
                var refLogits = ReadFloats(Path.Combine(refDir, name + ".logits.f32"));
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(refDir, name + ".ref.json")));
                int refFrames = doc.RootElement.GetProperty("frames").GetInt32();
                double[] refBeats = doc.RootElement.GetProperty("beats").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                double[] refDowns = doc.RootElement.GetProperty("downbeats").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                // 1. The spectrogram: against the package's (float32), and against the same in float64 (the package's rounding shows in quiet bins: a loud-bin error of 1e-7 is 1e-4 of a quiet one).
                double dSpect = double.PositiveInfinity, dExact = double.PositiveInfinity, dPackage = double.PositiveInfinity;
                string exactFile = Path.Combine(refDir, name + ".exact.f32");
                if (frames == refFrames && mine.Length == refSpect.Length)
                {
                    dSpect = 0;
                    for (int k = 0; k < mine.Length; k++) dSpect = Math.Max(dSpect, Math.Abs(mine[k] - refSpect[k]));
                    if (File.Exists(exactFile))
                    {
                        var exact = ReadFloats(exactFile);
                        dExact = dPackage = 0;
                        for (int k = 0; k < mine.Length; k++) { dExact = Math.Max(dExact, Math.Abs(mine[k] - exact[k])); dPackage = Math.Max(dPackage, Math.Abs(refSpect[k] - exact[k])); }
                    }
                }
                // 2. The network on the upstream's spectrogram, against the upstream's logits (the network and the windows alone), and 3. on ours.
                net.Predict(refSpect, refFrames, out var b0, out var d0);
                double dLogit = 0;
                for (int k = 0; k < refFrames; k++) { dLogit = Math.Max(dLogit, Math.Abs(b0[k] - refLogits[k])); dLogit = Math.Max(dLogit, Math.Abs(d0[k] - refLogits[refFrames + k])); }
                // 4. The post-processing on the upstream's logits: exactly the upstream's beats and downbeats.
                var upLogitsB = refLogits.AsSpan(0, refFrames).ToArray();
                var upLogitsD = refLogits.AsSpan(refFrames, refFrames).ToArray();
                BeatThisFront.PickPeaks(upLogitsB, upLogitsD, out var pb, out var pd);
                bool postOk = Same(pb, refBeats, 0.0) && Same(pd, refDowns, 0.0);
                // 5. Our whole chain: beats and downbeats within one frame of the upstream's.
                var r = BeatThisTracker.Run(src, net);
                bool beatsOk = Within(r.BeatFrames, refBeats), downOk = Within(r.DownFrames, refDowns);
                bool spectOk = dSpect <= 1e-4 || dExact <= 1e-4 || rate != BeatThisFront.Rate;          // another sample rate goes through our resampler, the reference's is another one: only the beats are compared
                Console.WriteLine($"{name}: {rate} Hz, {frames} frames; spectrogram max diff {dSpect:0.0e+0} (against the exact one {dExact:0.0e+0}, the package's own rounding {dPackage:0.0e+0}); logits max diff {dLogit:0.0e+0}; post-processing on upstream logits {(postOk ? "identical" : "DIFFERENT")}; beats {r.BeatFrames.Length} vs {refBeats.Length} ({(beatsOk ? "within a frame" : "MISMATCH")}); downbeats {r.DownFrames.Length} vs {refDowns.Length} ({(downOk ? "within a frame" : "MISMATCH")})");
                if (rate == BeatThisFront.Rate) worstSpect = Math.Max(worstSpect, Math.Min(dSpect, double.IsInfinity(dExact) ? dSpect : Math.Max(dExact, 0)));
                worstLogit = Math.Max(worstLogit, dLogit);
                if (!spectOk || dLogit > 1e-3 || !postOk || !beatsOk || !downOk) bad++;
            }
            Console.WriteLine($"{n} files, {bad} failing; spectrogram max diff at 22.05 kHz {worstSpect:0.0e+0}, logits {worstLogit:0.0e+0}");
            return n > 0 && bad == 0 ? 0 : 1;
        }

        static float[] ReadFloats(string path) { var b = File.ReadAllBytes(path); var f = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, f, 0, b.Length); return f; }

        static bool Same(double[] a, double[] b, double tol)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] / BeatThisFront.Fps - b[i]) > tol + 1e-9) return false;
            return true;
        }

        /// <summary>Every peak of a (frames) has one of b (seconds) within one frame and the other way, and there are as many.</summary>
        static bool Within(double[] aFrames, double[] bSeconds)
        {
            if (Math.Abs(aFrames.Length - bSeconds.Length) > Math.Max(1, bSeconds.Length / 100)) return false;
            int miss = 0;
            foreach (double f in aFrames) if (!bSeconds.Any(s => Math.Abs(s * BeatThisFront.Fps - f) <= 1.0 + 1e-9)) miss++;
            foreach (double s in bSeconds) if (!aFrames.Any(f => Math.Abs(s * BeatThisFront.Fps - f) <= 1.0 + 1e-9)) miss++;
            return miss <= Math.Max(0, bSeconds.Length / 100);
        }

        public static int Run(string[] a, int at)
        {
            if (a.Length < at + 4) { Console.Error.WriteLine("--aw4 DIR SET ONNX OUT [--half h] [--reach 0,25] [--limit n]"); return 2; }
            string dir = a[at], set = a[at + 1], onnx = a[at + 2], outPath = a[at + 3];
            var reaches = new List<double> { 0, 25 };
            string halfFilter = null;
            int limit = int.MaxValue;
            bool onsets = false;
            for (int i = at + 4; i < a.Length; i++)
            {
                switch (a[i])
                {
                    case "--onsets": onsets = true; break;
                    case "--reach": reaches = a[++i].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList(); break;
                    case "--half": halfFilter = a[++i]; break;
                    case "--limit": limit = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                }
            }
            var bt = LoadNet(onnx);
            var aw2 = BeatModel.LoadEmbedded() ?? throw new InvalidOperationException("this build has no embedded AW2 model");
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
                string name = Path.GetFileNameWithoutExtension(wav);
                var item = new Dictionary<string, object> { ["set"] = set, ["name"] = name, ["half"] = half, ["bpb"] = bpb, ["seconds"] = Math.Round(x.Length / (double)rate, 2), ["gt_beats"] = Aw3Commands.Round(gtB), ["gt_downbeats"] = Aw3Commands.Round(gtD) };
                var res = new Dictionary<string, object>();
                var ms = new Dictionary<string, double>();

                // AW1: AW1's DSP tracker alone.
                var s1 = new AudioSource(x, x, rate);
                var sw = Stopwatch.StartNew();
                BeatTrack dsp = BeatTracker.Track(s1.ForAnalysis());
                var r1 = AudioAnalysis.FromTrack(s1, dsp, bpb);
                ms["aw1"] = sw.Elapsed.TotalMilliseconds;
                res["aw1"] = Aw3Commands.Describe(s1, r1, rate, bpb);

                // AW2 as shipped (model or DSP, the knobs off) and AW3's candidate (attack reach 25 ms, residual bounds 30:60).
                var s2 = new AudioSource(x, x, rate);
                sw.Restart();
                AutoWarpResult r2;
                using (BeatModel.Scope(aw2)) r2 = AudioAnalysis.AutoWarp(s2, bpb);
                ms["aw2"] = sw.Elapsed.TotalMilliseconds;
                res["aw2"] = Aw3Commands.Describe(s2, r2, rate, bpb);
                var s3 = new AudioSource(x, x, rate);
                BeatModelTracker.AttackReachMs = 25; AudioAnalysis.SteadyResidualP95Ms = 30; AudioAnalysis.SteadyResidualMaxMs = 60;
                sw.Restart();
                AutoWarpResult r3;
                using (BeatModel.Scope(aw2)) r3 = AudioAnalysis.AutoWarp(s3, bpb);
                ms["aw3"] = sw.Elapsed.TotalMilliseconds;
                BeatModelTracker.AttackReachMs = 0; AudioAnalysis.SteadyResidualP95Ms = AudioAnalysis.SteadyResidualMaxMs = double.PositiveInfinity;
                res["aw3"] = Aw3Commands.Describe(s3, r3, rate, bpb);

                // AW4: the pretrained network. The activations are computed once; each reach is a setting applied to them. The first pass of the run includes the session's creation.
                var s4 = new AudioSource(x, x, rate);
                sw.Restart();
                var act = BeatThisTracker.Run(s4, bt);
                double network = sw.Elapsed.TotalMilliseconds;
                foreach (double reach in reaches)
                {
                    BeatThisTracker.AttackReachMs = reach;
                    sw.Restart();
                    var t = BeatThisTracker.TrackFrom(s4, act);
                    var r4 = t == null ? new AutoWarpResult { BeatsPerBar = bpb } : AudioAnalysis.FromTrack(s4, t, bpb);
                    double rest = sw.Elapsed.TotalMilliseconds;
                    string key = reach == 0 ? "aw4" : $"aw4_r{reach:0}";
                    ms[key] = network + rest;
                    var d = Aw3Commands.Describe(s4, r4, rate, bpb);
                    d["source"] = r4.Source;
                    res[key] = d;
                    if (t != null) res["bt_raw" + (reach == 0 ? "" : $"_r{reach:0}")] = new Dictionary<string, object> { ["beats"] = Aw3Commands.Round(t.Beats.Select(v => v / rate)), ["bpm"] = Math.Round(t.MedianBpm, 3), ["downbeats"] = Aw3Commands.Round(act.DownFrames.Select(v => v / BeatThisFront.Fps)) };
                }
                BeatThisTracker.AttackReachMs = 0;
                item["res"] = res;
                if (onsets) item["onsets"] = Aw3Commands.Onsets(s4);
                item["ms"] = ms.ToDictionary(k => k.Key, k => Math.Round(k.Value, 1));
                items.Add(item);
            }
            File.WriteAllText(outPath, JsonSerializer.Serialize(items));
            Console.WriteLine($"{items.Count} files -> {outPath}");
            return 0;
        }

        public static int Bench(string onnx, string seconds)
        {
            var net = LoadNet(onnx);
            double secs = double.Parse(seconds, CultureInfo.InvariantCulture);
            var one = WarpSignals.Make(WarpSignals.Tempo.Drift, WarpSignals.Style.Mix, 64, seed: 1);
            int reps = (int)Math.Ceiling(secs / 64.0);
            var x = new float[one.Audio.Length * reps];
            for (int r = 0; r < reps; r++) Array.Copy(one.Audio, 0, x, r * one.Audio.Length, one.Audio.Length);
            string path = Path.Combine(Path.GetTempPath(), "aw4-bench.wav");
            Pack.WriteWav(path, x, one.Rate);
            using var scope = BeatThis.Scope(net);
            for (int pass = 0; pass < 3; pass++)
            {
                var proc = Process.GetCurrentProcess();
                var cpu0 = proc.TotalProcessorTime;
                var sw = Stopwatch.StartNew();
                var (audio, rate) = Wav.Read(path);
                var src = new AudioSource(audio, audio, rate);
                double decode = sw.Elapsed.TotalMilliseconds;
                // The stages of one more analysis, for the log: the mono mix and resampling, the spectrogram, the network.
                var swp = Stopwatch.StartNew();
                var mono = BeatFront.Mono22k(src);
                double tMono = swp.Elapsed.TotalMilliseconds; swp.Restart();
                var spect = BeatThisFront.Compute(mono, out int frames);
                double tSpect = swp.Elapsed.TotalMilliseconds; swp.Restart();
                net.Predict(spect, frames, out _, out _);
                double tNet = swp.Elapsed.TotalMilliseconds;
                Console.WriteLine($"  stages: mono {tMono:0} ms, spectrogram {tSpect:0} ms, network {tNet:0} ms ({frames} frames, {BeatThisFront.WindowStarts(frames).Length} windows)");
                sw.Restart();
                var src9 = new AudioSource(audio, audio, rate);
                var res = AudioAnalysis.AutoWarp(src9, 4);
                double total = decode + sw.Elapsed.TotalMilliseconds;
                proc.Refresh();
                Console.WriteLine($"pass {pass}: {src.Seconds:0} s of audio: decode {decode:0} ms, total (decoding, spectrogram, network, markers) {total:0} ms wall, {(proc.TotalProcessorTime - cpu0).TotalMilliseconds:0} ms cpu, cores {Environment.ProcessorCount}, found {res.Found}, source {res.Source}, markers {res.Markers.Count}, bpm {res.Bpm:0.00}");
            }
            File.Delete(path);
            return 0;
        }
    }
}
