// The listening pack of AW4 (round 3): --pack4 OUT WARPTOOL LIST ONNX. LIST has "clip.wav<TAB>song BPM" per line (the 16 clips of round 2, tools/aw2/aw4packlist.py). Each clip is cut,
// analysed and rendered exactly as Pack3 does (a 40 s window from 10 s, played at the song tempo against a click on the grid with a higher, louder tick on each bar line, warped with
// Signalsmith Stretch between the markers): once with the build's new default (the pretrained network through AudioAnalysis.AutoWarp, as the app runs it) and once with AW2 as shipped.
// Files are NN-name--aw4.wav and NN-name--aw2.wav; tools/aw2/aw4shuffle.py makes the blind A/B pack of them.
using System.Globalization;
using NeoBuzz.PitchEval;
using ReBuzz.Core.Clips;

namespace NeoBuzz.AutoWarpEval
{
    internal static class Pack4
    {
        public static int Run(string[] a, int at)
        {
            if (a.Length < at + 4) { Console.Error.WriteLine("--pack4 OUT WARPTOOL LIST ONNX"); return 2; }
            string outDir = a[at], warp = a[at + 1], list = a[at + 2];
            Directory.CreateDirectory(outDir);
            var bt = Aw4Commands.LoadNet(a[at + 3]);
            var aw2 = Aw2Commands.LoadModel("embedded");
            BeatThisTracker.AttackReachMs = double.Parse(Environment.GetEnvironmentVariable("AW4_REACH") ?? "0", CultureInfo.InvariantCulture);
            int n = 0;
            foreach (var line in File.ReadLines(list))
            {
                var p = line.Split('\t');
                if (p.Length < 2) continue;
                var (x, rate) = Wav.Read(p[0]);
                int s0 = Math.Min(x.Length / 4, 10 * rate), len = Math.Min(x.Length - s0, 40 * rate);
                var w = new float[len];
                Array.Copy(x, s0, w, 0, len);
                double song = double.Parse(p[1], CultureInfo.InvariantCulture);
                string name = Path.GetFileNameWithoutExtension(p[0]);
                AutoWarpResult rNew, rOld;
                using (BeatThis.Scope(bt)) using (BeatModel.Scope(aw2)) rNew = AudioAnalysis.AutoWarp(new AudioSource(w, w, rate), 4);
                using (BeatThis.Scope(null)) using (BeatModel.Scope(aw2)) rOld = AudioAnalysis.AutoWarp(new AudioSource(w, w, rate), 4);          // AW2 as shipped
                n++;
                foreach (var (which, r) in new[] { ("aw4", rNew), ("aw2", rOld) })
                {
                    Pack.WriteWav(Path.Combine(outDir, $"{n:00}-{name}--{which}.wav"), Pack2.Render(w, rate, r, song, warp), rate);
                    Console.WriteLine($"{n:00}-{name}--{which}: {(r.Found ? $"{r.Bpm:0.0} BPM, {r.Markers.Count} markers, steady {r.Steady}, source {r.Source}" : "no beats found")}; song {song} BPM");
                }
            }
            return 0;
        }
    }
}
