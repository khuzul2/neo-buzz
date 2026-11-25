// The listening pack and the gates of AW5 (auto-warp that sticks to the grid): --pack5 OUT WARPTOOL LIST. LIST has "clip.wav<TAB>song BPM" per line (tools/aw2/aw4packlist.py ... r4:
// the eight clips of round 4). Each clip is cut as Pack4 cuts it (AW4_SECONDS from 10 s, 15 by default), analysed with AW2 as shipped (the beat model), and rendered twice at the song tempo
// against a click on the grid: as plain auto-warp (the beats) and as auto-warp to grid (the same, then a marker on every transient pulled to the 1/16 grid at 100 %, as the app does it:
// AudioAnalysis.GridOnsets and WarpMath.ToGrid). Files are NN-name--grid.wav and NN-name--beat.wav; tools/aw2/aw5shuffle.py makes the blind A/B pack.
// It also measures, per clip and over all: the grid fit (how far each marked transient, and each transient the detector found, lands from its grid line, in ms at the render tempo, from
// the warp map and again from the rendered audio's own onsets), the transients left alone, and the click detector (second difference against its local mean) on both renders.
using System.Globalization;
using NeoBuzz.PitchEval;
using ReBuzz.Core.Clips;

namespace NeoBuzz.AutoWarpEval
{
    internal static class Pack5
    {
        const double GridBeats = 0.25, Strength = 1, Reach = 0.4;

        public static int Run(string[] a, int at)
        {
            if (a.Length < at + 3) { Console.Error.WriteLine("--pack5 OUT WARPTOOL LIST"); return 2; }
            string outDir = a[at], warp = a[at + 1], list = a[at + 2];
            Directory.CreateDirectory(outDir);
            var aw2 = Aw2Commands.LoadModel("embedded");
            double seconds = double.Parse(Environment.GetEnvironmentVariable("AW4_SECONDS") ?? "15", CultureInfo.InvariantCulture);
            int sens = int.Parse(Environment.GetEnvironmentVariable("AW5_SENS") ?? "50", CultureInfo.InvariantCulture);
            var mapErr = new List<double>(); var allErr = new List<double>(); var renderErr = new List<double>(); var renderFar = 0; var renderAll = 0;
            int outOfReach = 0, gatedTotal = 0, madeTotal = 0, detectedTotal = 0, clicksGrid = 0, clicksBeat = 0;
            var rows = new List<string> { "clip\tsong_bpm\tbeats_found\tmarkers_beat\tmarkers_grid\traw_onsets\tgated\tmade\tout_of_reach\tmap_median_ms\tmap_p90_ms\trender_onsets\trender_median_ms\trender_p90_ms\tclicks_beat\tclicks_grid" };
            int n = 0;
            foreach (var line in File.ReadLines(list))
            {
                var p = line.Split('\t');
                if (p.Length < 2) continue;
                var (x, rate) = Wav.Read(p[0]);
                int s0 = Math.Min(x.Length / 4, 10 * rate), len = Math.Min(x.Length - s0, (int)(seconds * rate));
                var w = new float[len];
                Array.Copy(x, s0, w, 0, len);
                double song = double.Parse(p[1], CultureInfo.InvariantCulture);
                string name = Path.GetFileNameWithoutExtension(p[0]);
                var src = new AudioSource(w, w, rate);
                AutoWarpResult r;
                using (BeatModel.Scope(aw2)) r = AudioAnalysis.AutoWarp(src, 4);
                // What the app has after auto-warp: its markers, or the clip's own straight line (from the tempo found) when there are none.
                double clipBpm = r.Found ? r.Bpm : AudioAnalysis.Tempo(src, 4).Bpm;
                var beatMap = WarpMath.Effective(r.Markers, len, rate, clipBpm > 0 ? clipBpm : song);
                var onsets = AudioAnalysis.GridOnsets(src, sens, out int gated);
                var rep = new GridWarpReport { Gated = gated };
                var res = WarpMath.ToGrid(beatMap, onsets, GridBeats, Strength, Reach, 0, len, rep);
                var gridMap = res.Ok ? res.Markers : beatMap;
                // The fit from the map: each transient the detector passed, where it lands against its line (ms at the song's tempo).
                double msPerBeat = 60000.0 / song;
                var mine = new List<double>(); var every = new List<double>();
                foreach (double t in onsets)
                {
                    double beat = WarpMath.BeatAt(gridMap, t), d = Math.Abs(beat - Math.Round(beat / GridBeats) * GridBeats);
                    every.Add(d * msPerBeat);
                    if (Math.Abs(WarpMath.BeatAt(beatMap, t) - Math.Round(WarpMath.BeatAt(beatMap, t) / GridBeats) * GridBeats) <= Reach * GridBeats + 1e-9) mine.Add(d * msPerBeat);
                }
                mapErr.AddRange(mine); allErr.AddRange(every);
                outOfReach += rep.OutOfReach; gatedTotal += rep.Gated; madeTotal += rep.Made; detectedTotal += onsets.Count + rep.Gated;
                n++;
                var (yBeat, cBeat) = Render(w, rate, beatMap, song, warp);
                var (yGrid, cGrid) = Render(w, rate, gridMap, song, warp);
                Pack.WriteWav(Path.Combine(outDir, $"{n:00}-{name}--beat.wav"), cBeat, rate);
                Pack.WriteWav(Path.Combine(outDir, $"{n:00}-{name}--grid.wav"), cGrid, rate);
                int kb = Clicks(yBeat, rate), kg = Clicks(yGrid, rate);
                clicksBeat += kb; clicksGrid += kg;
                // The fit from the rendered audio: its onsets against the click's grid (the song's 1/16 lines), those within the reach of a line and those beyond it.
                var ro = AudioAnalysis.Onsets(new AudioSource(yGrid, yGrid, rate), sens);
                var rr = new List<double>();
                double stepMs = msPerBeat * GridBeats;
                foreach (double t in ro)
                {
                    double ms = t / rate * 1000, off = Math.Abs(ms - Math.Round(ms / stepMs) * stepMs);
                    renderAll++;
                    if (off <= Reach * stepMs) rr.Add(off); else renderFar++;
                }
                renderErr.AddRange(rr);
                rows.Add(string.Join("\t", name, song.ToString(CultureInfo.InvariantCulture), r.Found, beatMap.Count, gridMap.Count, onsets.Count + gated, gated, rep.Made, rep.OutOfReach,
                    F(Pct(mine, 0.5)), F(Pct(mine, 0.9)), ro.Count, F(Pct(rr, 0.5)), F(Pct(rr, 0.9)), kb, kg));
                Console.WriteLine($"{n:00}-{name}: {(r.Found ? $"{r.Bpm:0.0} BPM beats" : "no beats found (own tempo)")}; markers {beatMap.Count} -> {gridMap.Count}; transients {onsets.Count + gated} ({gated} quiet or sustained, {rep.OutOfReach} out of reach, {rep.Made} marked, {rep.Skipped} skipped); map fit median {Pct(mine, 0.5):0.00} ms p90 {Pct(mine, 0.9):0.00} ms; render fit median {Pct(rr, 0.5):0.00} ms p90 {Pct(rr, 0.9):0.00} ms; clicks beat {kb} grid {kg}");
            }
            File.WriteAllLines(Path.Combine(outDir, "aw5-measure.tsv"), rows);
            string summary =
                $"ALL: {n} clips, {detectedTotal} transients detected, {gatedTotal} in quiet or sustained passages, {outOfReach} beyond the reach (left alone), {madeTotal} marked.\n" +
                $"GRID FIT (map, marked transients within the reach): n={mapErr.Count} median {Pct(mapErr, 0.5):0.000} ms p90 {Pct(mapErr, 0.9):0.000} ms (gates: median <= 3, p90 <= 10)\n" +
                $"GRID FIT (map, every transient passing the gate, beyond-reach ones included): n={allErr.Count} median {Pct(allErr, 0.5):0.000} ms p90 {Pct(allErr, 0.9):0.000} ms\n" +
                $"GRID FIT (rendered audio's onsets within the reach of a 1/16 line): n={renderErr.Count} median {Pct(renderErr, 0.5):0.000} ms p90 {Pct(renderErr, 0.9):0.000} ms; {renderFar} of {renderAll} rendered onsets beyond the reach\n" +
                $"CLICKS (second difference against its local mean, warped audio without the click track): plain auto-warp {clicksBeat}, to grid {clicksGrid}, new {clicksGrid - clicksBeat}\n";
            Console.WriteLine(summary);
            File.WriteAllText(Path.Combine(outDir, "aw5-summary.txt"), summary);
            return 0;
        }

        static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

        static double Pct(List<double> v, double p)
        {
            if (v.Count == 0) return double.NaN;
            var s = v.OrderBy(t => t).ToArray();
            return s[Math.Min(s.Length - 1, (int)Math.Ceiling(p * s.Length) - 1)];
        }

        /// <summary>
        /// The clip played at the song's tempo: the audio from the first marker at or after beat 0 warped between the markers (Signalsmith Stretch, as the app's Pro mode), and the same
        /// with a click on the song's grid (a higher, louder tick on each bar line). Output time 0 is that marker's beat, so plain auto-warp (first marker at beat 0) is as Pack2 renders it.
        /// </summary>
        static (float[] Warped, float[] WithClick) Render(float[] w, int rate, List<(double Sample, double Beat)> m, double song, string warp)
        {
            double perBeat = 60.0 / song;
            int first = m.FindIndex(x => x.Beat >= -0.01);
            if (first < 0 || first + 1 >= m.Count) first = 0;
            var mk = m.Skip(first).ToList();
            double start = mk[0].Sample, beat0 = mk[0].Beat;
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
            string tmp = Path.Combine(Path.GetTempPath(), "aw5pack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            float[] y;
            try
            {
                File.WriteAllBytes(Path.Combine(tmp, "in.f32"), ToBytes(inp));
                File.WriteAllText(Path.Combine(tmp, "curve.txt"), curve.ToString());
                var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(warp, $"\"{Path.Combine(tmp, "in.f32")}\" \"{Path.Combine(tmp, "out.f32")}\" \"{Path.Combine(tmp, "curve.txt")}\" \"{Path.Combine(tmp, "map.txt")}\" {rate} 0") { UseShellExecute = false });
                ps!.WaitForExit();
                var b = File.ReadAllBytes(Path.Combine(tmp, "out.f32"));
                y = new float[b.Length / 4];
                Buffer.BlockCopy(b, 0, y, 0, b.Length);
            }
            finally { Directory.Delete(tmp, true); }
            var o = new float[y.Length];
            for (int i = 0; i < y.Length; i++) o[i] = y[i] * 0.7f;
            var warped = (float[])o.Clone();
            for (int k = (int)Math.Ceiling(beat0 - 1e-9); (k - beat0) * perBeat * rate < o.Length; k++)
            {
                bool bar = ((k % 4) + 4) % 4 == 0;
                double hz = bar ? 1800 : 1200, gain = bar ? 0.5 : 0.3;
                int st = (int)((k - beat0) * perBeat * rate), len = (int)(0.03 * rate);
                for (int i = 0; i < len && st + i < o.Length; i++)
                    if (st + i >= 0) o[st + i] += (float)(gain * Math.Sin(2 * Math.PI * hz * i / rate) * Math.Exp(-i / (0.006 * rate)));
            }
            return (warped, o);
        }

        /// <summary>
        /// The click detector of AGENTS.md section 5: the second difference of the signal against its local mean (the mean of its magnitude over 20 ms either side). A click is a sample
        /// whose second difference is over 40 times that mean and over 0.05 of full scale; events within 3 ms are one.
        /// </summary>
        internal static int Clicks(float[] x, int rate)
        {
            int n = x.Length;
            if (n < 8) return 0;
            var d = new double[n];
            for (int i = 1; i < n - 1; i++) d[i] = Math.Abs(x[i + 1] - 2.0 * x[i] + x[i - 1]);
            int half = Math.Max(1, rate / 50), merge = Math.Max(1, rate * 3 / 1000);
            var cum = new double[n + 1];
            for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + d[i];
            int count = 0; long last = -merge * 2;
            for (int i = 1; i < n - 1; i++)
            {
                if (d[i] < 0.05) continue;
                int lo = Math.Max(0, i - half), hi = Math.Min(n, i + half + 1);
                double mean = (cum[hi] - cum[lo]) / (hi - lo);
                if (d[i] > 40 * mean && i - last > merge) count++;
                if (d[i] > 40 * mean) last = i;
            }
            return count;
        }

        static byte[] ToBytes(float[] f) { var b = new byte[f.Length * 4]; Buffer.BlockCopy(f, 0, b, 0, b.Length); return b; }
    }
}
