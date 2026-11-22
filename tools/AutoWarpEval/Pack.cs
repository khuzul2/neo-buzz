using System.Text;
using NeoBuzz.PitchEval;
using ReBuzz.Core.Clips;
using ReBuzz.SessionTests.AutoWarp;

namespace NeoBuzz.AutoWarpEval
{
    internal static partial class Pack
    {
        public static void WriteWav(string path, float[] mono, int rate)
        {
            using var f = File.Create(path);
            using var w = new BinaryWriter(f);
            int bytes = mono.Length * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (var v in mono) w.Write((short)Math.Clamp((int)Math.Round(v * 32767.0), -32768, 32767));
        }

        const double SongBpm = 120;

        /// <summary>
        /// The listening pack: <c>--pack OUT [--gmd DIR]</c>. For two Groove MIDI Dataset performances (when DIR is given) and the generated drift and step lines
        /// (mix, second variant), the clip is played in a song at 120 BPM, on a click on the song's grid (a higher, louder tick on each bar line), once as today's
        /// steady detection warps it (one tempo from the file's start) and once as auto-warp does. The warp is a plain resample between the markers
        /// (so the pitch moves with the speed: it is for hearing the timing, not the quality of the stretch). Files are NN-name--method.wav.
        /// </summary>
        public static int Run(string[] args, int at)
        {
            string? outDir = null, gmd = null;
            for (int i = at; i < args.Length; i++)
            {
                if (args[i] == "--gmd" && i + 1 < args.Length) gmd = args[++i];
                else if (outDir == null) outDir = args[i];
            }
            if (outDir == null) { Console.Error.WriteLine("--pack OUT [--gmd DIR]"); return 2; }
            Directory.CreateDirectory(outDir);
            var items = new List<(string Name, float[] Audio, int Rate, int Bpb)>();
            if (gmd != null && Directory.Exists(gmd))
            {
                var files = Directory.GetFiles(gmd, "*.wav").OrderBy(f => f, StringComparer.Ordinal).ToList();
                foreach (int index in new[] { 0, files.Count / 2 }.Distinct())
                    if (index < files.Count)
                    {
                        var (x, rate) = Wav.Read(files[index]);
                        items.Add(("gmd-" + Path.GetFileNameWithoutExtension(files[index]), x, rate, 4));
                    }
            }
            foreach (var tempo in new[] { WarpSignals.Tempo.Drift, WarpSignals.Tempo.Step })
            {
                var line = Program.Variant(tempo, WarpSignals.Style.Mix, 1);
                items.Add(("generated-" + tempo.ToString().ToLowerInvariant(), line.Audio, line.Rate, line.BeatsPerBar));
            }
            var notes = new StringBuilder();
            notes.AppendLine("Auto-warp listening pack. Each clip is played in a song at 120 BPM against a click on the song's grid (a higher, louder tick on each bar line).");
            notes.AppendLine("--steady: today's steady-tempo detection (one tempo from the start of the file). --autowarp: the markers of auto-warp.");
            notes.AppendLine("The warp here is a plain resample between the markers, so the pitch moves with the speed; listen to the timing against the click, not to the quality.");
            notes.AppendLine("Groove MIDI Dataset (Magenta), CC BY 4.0; the generated lines are made by the tool.");
            int n = 0;
            foreach (var (name, audio, rate, bpb) in items)
            {
                n++;
                var src = new AudioSource(audio, audio, rate);
                var auto = AudioAnalysis.AutoWarp(src, bpb);
                var (todayBpm, _) = AudioAnalysis.Tempo(src, bpb, out _);
                double beats = src.Seconds;
                var todayMap = WarpMap.Straight(src.Length, src.Seconds * todayBpm / 60);
                var autoMap = auto.Found && auto.Markers.Count >= 2
                    ? new WarpMap(auto.Markers.Select(m => new WarpMarker(m.Sample, m.Beat)))
                    : WarpMap.Straight(src.Length, src.Seconds * (auto.Found ? auto.Bpm : todayBpm) / 60);
                string stem = $"{n:00}-{name}";
                WriteWav(Path.Combine(outDir, stem + "--steady.wav"), Render(audio, rate, todayMap, bpb, 0), rate);
                WriteWav(Path.Combine(outDir, stem + "--autowarp.wav"), Render(audio, rate, autoMap, bpb, auto.Found ? auto.FirstDownbeat : 0), rate);
                notes.AppendLine($"{stem}: steady {todayBpm:0.0} BPM; auto-warp {(auto.Found ? $"{auto.Bpm:0.0} BPM, {auto.Markers.Count} markers" : "no beats found")}.");
            }
            File.WriteAllText(Path.Combine(outDir, "README.txt"), notes.ToString());
            Console.WriteLine($"{items.Count} clips, {items.Count * 2} files -> {outDir}");
            return 0;
        }

        /// <summary>The clip at the song's tempo: output beat k plays the audio where the map has beat k; the click is on every beat of the song's grid.</summary>
        static float[] Render(float[] audio, int rate, WarpMap map, int bpb, double firstDownbeatSample)
        {
            double endBeat = map.BeatAt(audio.Length - 1);
            int beats = Math.Max(1, (int)Math.Floor(endBeat));
            int length = (int)(beats * 60.0 / SongBpm * rate);
            var o = new float[length];
            double perBeat = 60.0 / SongBpm * rate;
            for (int i = 0; i < length; i++)
            {
                double src = map.SampleAt(i / perBeat);
                if (src < 0 || src >= audio.Length - 1) continue;
                int j = (int)src; double f = src - j;
                o[i] = (float)(audio[j] * (1 - f) + audio[j + 1] * f) * 0.7f;
            }
            for (int k = 0; k <= beats; k++)
            {
                bool bar = k % bpb == 0;
                double hz = bar ? 1800 : 1200, gain = bar ? 0.5 : 0.3;
                int start = (int)(k * perBeat), len = (int)(0.03 * rate);
                for (int i = 0; i < len && start + i < length; i++)
                    o[start + i] += (float)(gain * Math.Sin(2 * Math.PI * hz * i / rate) * Math.Exp(-i / (0.006 * rate)));
            }
            return o;
        }
    }
}
