// The listening pack v2 of AW3: --pack3 OUT WARPTOOL LIST [MODEL]. LIST has "clip.wav<TAB>song BPM<TAB>fixed|cand<TAB>set" per line. A "fixed" clip is always rendered; a "cand" clip only when
// AW1's DSP track and the build's default path (the model, AW3's settings) differ, at most 2 per set. Files are NN-name--aw1.wav and NN-name--aw3.wav; tools/../aw3-shuffle-pack.py then
// makes the blind A/B pack. Each clip is a 40 s window, as Pack2 has it.
using System.Globalization;
using NeoBuzz.PitchEval;
using ReBuzz.Core.Clips;

namespace NeoBuzz.AutoWarpEval
{
    internal static class Pack3
    {
        public static int Run(string[] a, int at)
        {
            if (a.Length < at + 3) { Console.Error.WriteLine("--pack3 OUT WARPTOOL LIST [MODEL]"); return 2; }
            string outDir = a[at], warp = a[at + 1], list = a[at + 2];
            Directory.CreateDirectory(outDir);
            var net = Aw2Commands.LoadModel(a.Length > at + 3 ? a[at + 3] : "embedded");
            var perSet = new Dictionary<string, int>();
            int n = 0;
            foreach (var line in File.ReadLines(list))
            {
                var p = line.Split('\t');
                if (p.Length < 4) continue;
                var (x, rate) = Wav.Read(p[0]);
                int s0 = Math.Min(x.Length / 4, 10 * rate), len = Math.Min(x.Length - s0, 40 * rate);
                var w = new float[len];
                Array.Copy(x, s0, w, 0, len);
                double song = double.Parse(p[1], CultureInfo.InvariantCulture);
                string name = Path.GetFileNameWithoutExtension(p[0]);
                bool fixedItem = p[2] == "fixed";
                perSet.TryGetValue(p[3], out int have);
                AutoWarpResult r1, r2;
                using (BeatModel.Scope(null)) r1 = AudioAnalysis.AutoWarp(new AudioSource(w, w, rate), 4);
                AutoWarpResult rShipped;
                using (BeatModel.Scope(net)) rShipped = AudioAnalysis.AutoWarp(new AudioSource(w, w, rate), 4);          // AW2 as shipped: the knobs are off
                // AW3's candidate settings (the environment: AW3_REACH ms, AW3_P95 and AW3_MAX ms of the steady test's residuals), only for the AW3 render
                BeatModelTracker.AttackReachMs = double.Parse(Environment.GetEnvironmentVariable("AW3_REACH") ?? "0", CultureInfo.InvariantCulture);
                AudioAnalysis.SteadyResidualP95Ms = double.Parse(Environment.GetEnvironmentVariable("AW3_P95") ?? "Infinity", CultureInfo.InvariantCulture);
                AudioAnalysis.SteadyResidualMaxMs = double.Parse(Environment.GetEnvironmentVariable("AW3_MAX") ?? "Infinity", CultureInfo.InvariantCulture);
                using (BeatModel.Scope(net)) r2 = AudioAnalysis.AutoWarp(new AudioSource(w, w, rate), 4);
                BeatModelTracker.AttackReachMs = 0; AudioAnalysis.SteadyResidualP95Ms = AudioAnalysis.SteadyResidualMaxMs = double.PositiveInfinity;
                Console.WriteLine($"{name}: AW2 as shipped {(rShipped.Found ? $"{rShipped.Bpm:0.0} BPM, {rShipped.Markers.Count} markers, steady {rShipped.Steady}" : "no beats")}; AW1 {(r1.Found ? $"{r1.Bpm:0.0} BPM, {r1.Markers.Count} markers" : "no beats")}");
                bool differ = r1.Found != r2.Found || (r1.Found && (r2.FromModel || r1.Steady != r2.Steady || r1.Markers.Count != r2.Markers.Count || Math.Abs(r1.Bpm - r2.Bpm) > 0.003 * r1.Bpm));
                if (!fixedItem && (!differ || have >= 2)) { Console.WriteLine($"skipped {name}: {(differ ? "enough of its set" : "AW3 gives the same as AW1")}"); continue; }
                if (!fixedItem) perSet[p[3]] = have + 1;
                n++;
                foreach (var (which, r) in new[] { ("aw1", r1), ("aw3", r2) })
                {
                    Pack.WriteWav(Path.Combine(outDir, $"{n:00}-{name}--{which}.wav"), Pack2.Render(w, rate, r, song, warp), rate);
                    Console.WriteLine($"{n:00}-{name}--{which}: {(r.Found ? $"{r.Bpm:0.0} BPM, {r.Markers.Count} markers, steady {r.Steady}" : "no beats found")}; song {song} BPM; {(fixedItem ? "fixed (the AW2 blind test)" : "new")}");
                }
            }
            return 0;
        }
    }
}
