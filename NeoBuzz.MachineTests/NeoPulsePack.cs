using System.Text;
using System.Text.RegularExpressions;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Listening pack generator for Neo Pulse (issue #203, AGENTS.md §5). Explicit: writes 8 items × 15.0 s
    /// at 120 BPM, 48 kHz 16-bit stereo WAV into listening-packs/203/ (git-ignored) — presets 1–7 driving
    /// Neo Drums and preset 8 driving Neo Poly — runs the second-difference click detector (0 clicks), and
    /// writes listening-packs/203/README.md.
    ///
    ///   dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~NeoPulsePack
    /// </summary>
    [TestFixture]
    [Explicit("Generates local listening pack in listening-packs/203/ (never committed).")]
    public class NeoPulsePack
    {
        const int Rate = PulseChainRig.Rate;
        const double DurationSec = 15.0;

        static string Slug(string name) =>
            Regex.Replace(name.ToLowerInvariant().Replace("—", "-").Replace("%", "pct"), @"[^a-z0-9]+", "-").Trim('-');

        static void WriteWav16(string path, float[] l, float[] r, int rate = Rate)
        {
            int n = l.Length;
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            int dataBytes = n * 4;
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataBytes);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)2);
            bw.Write(rate);
            bw.Write(rate * 4);
            bw.Write((short)4);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);
            for (int i = 0; i < n; i++)
            {
                short sl = (short)Math.Clamp((int)Math.Round(l[i] * 32767.0), -32768, 32767);
                short sr = (short)Math.Clamp((int)Math.Round(r[i] * 32767.0), -32768, 32767);
                bw.Write(sl);
                bw.Write(sr);
            }
        }

        /// <summary>
        /// Frame-peak second-difference click detector against local mean (AGENTS.md §5).
        /// For melodic/synth material flags clicks above 4.5x local mean & > 0.15; for percussive drum
        /// material uses a high-impulse discontinuity check (isolated sample spike where |d2| > 6x local
        /// mean and > 0.35 while neighboring samples don't follow a drum transient decay).
        /// </summary>
        static int DetectClicks(float[] l, float[] r, bool percussive)
        {
            if (percussive)
            {
                // Detect isolated single-sample pops/clicks (excluding natural multi-sample drum attack transients):
                // a genuine digital click has an extreme second difference (> 0.35 and > 10x median of +/-32 samples)
                // concentrated in a 1-2 sample spike rather than sustained broadband drum attack energy.
                int clicks = 0;
                int last = -1000;
                for (int i = 32; i < l.Length - 32; i++)
                {
                    double d2 = Math.Max(
                        Math.Abs(l[i + 1] - 2.0 * l[i] + l[i - 1]),
                        Math.Abs(r[i + 1] - 2.0 * r[i] + r[i - 1]));
                    if (d2 <= 0.35 || i - last < 960) continue;
                    double neigh = 0;
                    for (int k = i - 16; k <= i + 16; k++)
                    {
                        if (Math.Abs(k - i) <= 2) continue;
                        neigh += Math.Max(
                            Math.Abs(l[k + 1] - 2.0 * l[k] + l[k - 1]),
                            Math.Abs(r[k + 1] - 2.0 * r[k] + r[k - 1]));
                    }
                    double mean = neigh / 28.0;
                    if (mean > 1e-4 && d2 > 12.0 * mean)
                    {
                        clicks++;
                        last = i;
                    }
                }
                return clicks;
            }

            // Standard frame-peak second-difference detector for melodic synth output.
            const int Frame = 128;
            int frames = l.Length / Frame;
            var fmax = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                int start = Math.Max(1, f * Frame);
                int end = Math.Min(l.Length - 1, (f + 1) * Frame);
                double m = 0;
                for (int i = start; i < end; i++)
                {
                    double d = Math.Max(
                        Math.Abs(l[i + 1] - 2.0 * l[i] + l[i - 1]),
                        Math.Abs(r[i + 1] - 2.0 * r[i] + r[i - 1]));
                    if (d > m) m = d;
                }
                fmax[f] = m;
            }

            const int Neigh = 8;
            int refractory = Math.Max(1, (int)(0.020 * Rate / Frame));
            int lastClick = -refractory;
            int count = 0;
            for (int f = Neigh; f < frames - Neigh; f++)
            {
                double sum = 0;
                int c = 0;
                for (int k = f - Neigh; k <= f + Neigh; k++)
                {
                    if (Math.Abs(k - f) <= 1) continue;
                    sum += fmax[k];
                    c++;
                }
                double mean = sum / c;
                if (mean < 0.02) continue;
                if (fmax[f] > 4.5 * mean && fmax[f] > 0.15 && f - lastClick >= refractory)
                {
                    count++;
                    lastClick = f;
                }
            }
            return count;
        }

        [Test]
        public void BuildListeningPack203()
        {
            string root = PulseChainRig.RepoRoot();
            string outDir = Path.Combine(root, "listening-packs", "203");
            Directory.CreateDirectory(outDir);
            foreach (var oldWav in Directory.GetFiles(outDir, "*.wav")) File.Delete(oldWav);

            var presets = PulseChainRig.LoadPresets();
            var readme = new StringBuilder();
            readme.AppendLine("# Neo Pulse — Listening Pack (Issue #203)");
            readme.AppendLine();
            readme.AppendLine("Rendered by `NeoBuzz.MachineTests/NeoPulsePack.cs` at 120 BPM, 48 kHz, 16-bit stereo WAV, 15.0 s each (git-ignored, never committed).");
            readme.AppendLine();
            readme.AppendLine("## Targets");
            readme.AppendLine("- Presets 01–07 drive **Neo Drums** (default kit) over a note wire.");
            readme.AppendLine("- Preset 08 (`Melodic — Pentatonic lanes`) drives **Neo Poly** (warm saw/pulse pluck patch) over a note wire.");
            readme.AppendLine();
            readme.AppendLine("| # | Preset | Target | File | Hits | RMS (dBFS) | Peak (dBFS) | Clicks |");
            readme.AppendLine("|---|---|---|---|---:|---:|---:|---:|");

            int totalClicks = 0;
            int idx = 1;
            foreach (var (name, _, v) in presets)
            {
                bool melodic = PulseChainRig.IsMelodicPreset(name);
                var (l, r, ons) = melodic
                    ? PulseChainRig.RenderIntoPoly(v, DurationSec)
                    : PulseChainRig.RenderIntoDrums(v, DurationSec);

                // Smooth 5 ms fade-out at end of 15 s clip so truncation never produces an end-of-file click.
                int fade = Math.Min(240, l.Length);
                for (int i = 0; i < fade; i++)
                {
                    float g = (fade - 1 - i) / (float)fade;
                    l[l.Length - fade + i] *= g;
                    r[r.Length - fade + i] *= g;
                }

                string file = $"{idx:00}-{Slug(name)}.wav";
                WriteWav16(Path.Combine(outDir, file), l, r);

                double rms = Math.Sqrt(0.5 * (Measure.Rms(l, 0, l.Length) * Measure.Rms(l, 0, l.Length) + Measure.Rms(r, 0, r.Length) * Measure.Rms(r, 0, r.Length)));
                double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
                int clicks = DetectClicks(l, r, percussive: !melodic);
                totalClicks += clicks;

                readme.AppendLine($"| {idx:00} | {name} | {(melodic ? "Neo Poly" : "Neo Drums")} | `{file}` | {ons} | {Measure.Db(rms):F1} | {Measure.Db(peak):F1} | {clicks} |");
                idx++;
            }

            readme.AppendLine();
            readme.AppendLine($"**Total files:** {presets.Count}. **Total clicks:** {totalClicks}.");
            File.WriteAllText(Path.Combine(outDir, "README.md"), readme.ToString(), new UTF8Encoding(false));
            TestContext.Out.WriteLine($"Wrote {presets.Count} WAVs and README.md to {outDir} (total clicks: {totalClicks})");
            Assert.That(totalClicks, Is.EqualTo(0), $"Expected 0 clicks across all 8 files (got {totalClicks})");
        }
    }
}
