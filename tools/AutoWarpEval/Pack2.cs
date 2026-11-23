// The listening pack of AW2b: --pack2 OUT MODEL WARPTOOL LIST. LIST has "clip.wav<TAB>song BPM" per line. Each clip (a 40 s window) is analysed by AW1's DSP tracker and by
// the beat model (AW2c: only clips where the two differ, at most 2 per source set and MAX in all), played at its song tempo against a click on the song's grid (a higher, louder tick on each bar line), warped with Signalsmith Stretch (the library of the app's
// Pro warp: the pitch stays put) between the markers each found. Files are NN-name--aw1.wav and NN-name--aw2.wav.
using System.Diagnostics;
using System.Globalization;
using NeoBuzz.PitchEval;
using ReBuzz.Core.Clips;

namespace NeoBuzz.AutoWarpEval
{
    internal static class Pack2
    {
        public static int Run(string[] a, int at)
        {
            if (a.Length < at + 4) { Console.Error.WriteLine("--pack2 OUT MODEL WARPTOOL LIST"); return 2; }
            string outDir = a[at], model = a[at + 1], warp = a[at + 2], list = a[at + 3];
            Directory.CreateDirectory(outDir);
            var net = Aw2Commands.LoadModel(model);
            int max = a.Length > at + 4 ? int.Parse(a[at + 4], CultureInfo.InvariantCulture) : int.MaxValue;
            var perGroup = new Dictionary<string, int>();
            int n = 0;
            foreach (var line in File.ReadLines(list))
            {
                var p = line.Split('\t');
                if (p.Length < 2 || n >= max) continue;
                var (x, rate) = Wav.Read(p[0]);
                int s0 = Math.Min(x.Length / 4, 10 * rate), len = Math.Min(x.Length - s0, 40 * rate);
                var w = new float[len];
                Array.Copy(x, s0, w, 0, len);
                double song = double.Parse(p[1], CultureInfo.InvariantCulture);
                string name = Path.GetFileNameWithoutExtension(p[0]);
                string group = name.Split('_')[0];
                perGroup.TryGetValue(group, out int have);
                AutoWarpResult r1, r2;
                using (BeatModel.Scope(null)) r1 = AudioAnalysis.AutoWarp(new AudioSource(w, w, rate), 4);
                using (BeatModel.Scope(net)) r2 = AudioAnalysis.AutoWarp(new AudioSource(w, w, rate), 4);
                bool differ = r1.Found != r2.Found || (r1.Found && (r2.FromModel || Math.Abs(r1.Bpm - r2.Bpm) > 0.003 * r1.Bpm));
                if (Environment.GetEnvironmentVariable("AW2_PACK_ALL") == "1")
                {
                    // A fixed list of clips, whatever AW1 gives: only the model's rendering, as NN-name--model.wav
                    n++;
                    var oo = Render(w, rate, r2, song, warp);
                    Pack.WriteWav(Path.Combine(outDir, $"{n:00}-{name}--model.wav"), oo, rate);
                    Console.WriteLine($"{n:00}-{name}--model: {(r2.Found ? $"{r2.Bpm:0.0} BPM, {r2.Markers.Count} markers, {(r2.FromModel ? "model" : "AW1's DSP track kept")}" : "no beats found")}; song {song} BPM");
                    continue;
                }
                if (!differ || have >= 2) { Console.WriteLine($"skipped {name}: {(differ ? "enough of its set" : "AW2 gives the same as AW1")}"); continue; }
                perGroup[group] = have + 1;
                n++;
                foreach (var (which, r) in new[] { ("aw1", r1), ("aw2", r2) })
                {
                    var o = Render(w, rate, r, song, warp);
                    Pack.WriteWav(Path.Combine(outDir, $"{n:00}-{name}--{which}.wav"), o, rate);
                    Console.WriteLine($"{n:00}-{name}--{which}: {(r.Found ? $"{r.Bpm:0.0} BPM, {r.Markers.Count} markers" : "no beats found")}; song {song} BPM");
                }
            }
            return 0;
        }

        static float[] Render(float[] w, int rate, AutoWarpResult r, double song, string warp)
        {
            double perBeat = 60.0 / song;
            float[] y;
            if (!r.Found) y = w;
            else
            {
                // Segments: input seconds from the first marker, and the stretch (output seconds per input second) that puts each marker's beat on the song's grid.
                var mk = r.Markers.Count >= 2 ? r.Markers : new List<(double Sample, double Beat)> { (r.FirstDownbeat, 0), (r.FirstDownbeat + rate * 60.0 / r.Bpm * 8, 8) };
                double start = mk[0].Sample;
                var curve = new System.Text.StringBuilder();
                for (int i = 0; i + 1 < mk.Count; i++)
                {
                    double tin = (mk[i + 1].Sample - mk[i].Sample) / rate, tout = (mk[i + 1].Beat - mk[i].Beat) * perBeat;
                    if (tin <= 0) continue;
                    double ratio = tout / tin;
                    curve.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{(mk[i].Sample - start) / rate:0.0000} {ratio:0.00000}"));
                    curve.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{(mk[i + 1].Sample - start) / rate - 0.001:0.0000} {ratio:0.00000}"));
                }
                int from = (int)Math.Max(0, start);
                var inp = new float[w.Length - from];
                Array.Copy(w, from, inp, 0, inp.Length);
                string tmp = Path.Combine(Path.GetTempPath(), "aw2pack-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);
                try
                {
                    File.WriteAllBytes(Path.Combine(tmp, "in.f32"), ToBytes(inp));
                    File.WriteAllText(Path.Combine(tmp, "curve.txt"), curve.ToString());
                    var ps = Process.Start(new ProcessStartInfo(warp, $"\"{Path.Combine(tmp, "in.f32")}\" \"{Path.Combine(tmp, "out.f32")}\" \"{Path.Combine(tmp, "curve.txt")}\" \"{Path.Combine(tmp, "map.txt")}\" {rate} 0") { UseShellExecute = false });
                    ps!.WaitForExit();
                    var b = File.ReadAllBytes(Path.Combine(tmp, "out.f32"));
                    y = new float[b.Length / 4];
                    Buffer.BlockCopy(b, 0, y, 0, b.Length);
                }
                finally { Directory.Delete(tmp, true); }
            }
            var o = new float[y.Length];
            for (int i = 0; i < y.Length; i++) o[i] = y[i] * 0.7f;
            for (int k = 0; k * perBeat * rate < o.Length; k++)
            {
                bool bar = k % 4 == 0;
                double hz = bar ? 1800 : 1200, gain = bar ? 0.5 : 0.3;
                int st = (int)(k * perBeat * rate), len = (int)(0.03 * rate);
                for (int i = 0; i < len && st + i < o.Length; i++)
                    o[st + i] += (float)(gain * Math.Sin(2 * Math.PI * hz * i / rate) * Math.Exp(-i / (0.006 * rate)));
            }
            return o;
        }

        static byte[] ToBytes(float[] f) { var b = new byte[f.Length * 4]; Buffer.BlockCopy(f, 0, b, 0, b.Length); return b; }
    }
}
