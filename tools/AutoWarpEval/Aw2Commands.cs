// AW2 helpers of the evaluation tool (docs/specs/neo-audio.md, AW2 as built):
//   --feat LIST        LIST has one "in.wav<TAB>out.u8" per line: the beat model's features (BeatFront, the app's own code) of each file.
//   --bench MODEL [S]  analyses S seconds (default 600) of generated music, read from a WAV file, with the model: decoding included, wall and CPU time.
using System.Diagnostics;
using NeoBuzz.PitchEval;
using ReBuzz.Core.Clips;
using ReBuzz.SessionTests.AutoWarp;

namespace NeoBuzz.AutoWarpEval
{
    internal static class ParityFile
    {
        public static (BeatModelNet, byte[], int, float[]) Read(string file)
        {
            var b = File.ReadAllBytes(file);
            int len = BitConverter.ToInt32(b, 0), T = BitConverter.ToInt32(b, 4);
            var data = b.AsSpan(8, len).ToArray();
            var q = b.AsSpan(8 + len, T * 128).ToArray();
            var exp = new float[2 * T];
            Buffer.BlockCopy(b, 8 + len + T * 128, exp, 0, exp.Length * 4);
            return (BeatModelNet.Load(data), q, T, exp);
        }
    }

    internal static class Aw2Commands
    {
        public static int Feat(string list)
        {
            int n = 0;
            foreach (var line in File.ReadLines(list))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                var (x, rate) = Wav.Read(parts[0]);
                var mono = rate == BeatFront.Rate ? x : BeatFront.Resample(x, rate, BeatFront.Rate);
                var q = BeatFront.Compute(mono, out int frames);
                File.WriteAllBytes(parts[1], q);
                n++;
            }
            Console.WriteLine($"{n} files");
            return 0;
        }

        public static int Parity(string file)
        {
            var (net, q, T, expected) = ParityFile.Read(file);
            var logits = new float[2 * T];
            net.Forward(q, T, logits, null);
            double worst = 0;
            for (int i = 0; i < logits.Length; i++) worst = Math.Max(worst, Math.Abs(logits[i] - expected[i]));
            Console.WriteLine($"parity: largest difference {worst:0.0e+0} over {logits.Length} logits");
            return worst < 1e-4 ? 0 : 1;
        }

        public static int Pulses()
        {
            foreach (double bpm in new[] { 62.0, 100, 128, 190 })
            {
                int T = 3000;
                var act = new BeatModelTracker.Activations { Beat = new float[T], Down = new float[T], TempoWindows = Array.Empty<float[]>(), TempoCentres = Array.Empty<int>() };
                double period = 60.0 / bpm * 50; int i = 0;
                for (double t = 25; t < T - 2; t += period, i++) { int f = (int)Math.Round(t); act.Beat[f] = 0.9f; act.Beat[f - 1] = 0.3f; act.Beat[f + 1] = 0.3f; if (i % 4 == 0) act.Down[f] = 0.8f; }
                var tr = BeatModelTracker.Decode(act, 22050, 22050 * 60, 5, 68);
                Console.WriteLine($"{bpm}: {(tr == null ? "null" : tr.MedianBpm.ToString("0.00") + " beats " + tr.Beats.Length)}");
            }
            return 0;
        }

        public static int Bench(string model, string seconds)
        {
            var net = BeatModelNet.Load(File.ReadAllBytes(model));
            double secs = double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture);
            var one = WarpSignals.Make(WarpSignals.Tempo.Drift, WarpSignals.Style.Mix, 64, seed: 1);
            int reps = (int)Math.Ceiling(secs / 64.0);
            var x = new float[one.Audio.Length * reps];
            for (int r = 0; r < reps; r++) Array.Copy(one.Audio, 0, x, r * one.Audio.Length, one.Audio.Length);
            string path = Path.Combine(Path.GetTempPath(), "aw2-bench.wav");
            Pack.WriteWav(path, x, one.Rate);
            BeatModel.Use(net);
            for (int pass = 0; pass < 2; pass++)
            {
                var proc = Process.GetCurrentProcess();
                var cpu0 = proc.TotalProcessorTime;
                var sw = Stopwatch.StartNew();
                var (audio, rate) = Wav.Read(path);                         // the decoding
                var src = new AudioSource(audio, audio, rate);
                double decode = sw.Elapsed.TotalMilliseconds;
                var swp = Stopwatch.StartNew();
                var q = BeatFront.Compute(src, out int frames);
                double front = swp.Elapsed.TotalMilliseconds; swp.Restart();
                var lg = new float[2 * frames]; var ft = new float[(long)frames * net.D];
                net.Forward(q, frames, lg, ft);
                double fwd = swp.Elapsed.TotalMilliseconds;
                Console.WriteLine($"  front end {front:0} ms, forward {fwd:0} ms ({frames} frames)");
                var res = AudioAnalysis.AutoWarp(src, 4);
                proc.Refresh();
                Console.WriteLine($"pass {pass}: {src.Seconds:0} s of audio, d={net.D}: decode {decode:0} ms, total {sw.Elapsed.TotalMilliseconds:0} ms wall, {(proc.TotalProcessorTime - cpu0).TotalMilliseconds:0} ms cpu, cores {Environment.ProcessorCount}, found {res.Found}, markers {res.Markers.Count}, bpm {res.Bpm:0.00}");
            }
            File.Delete(path);
            return 0;
        }
    }
}
