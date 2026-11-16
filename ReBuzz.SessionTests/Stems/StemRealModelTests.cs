// Fork (docs/specs/stem-separation.md): our own transform, chunker and ONNX Runtime passes against the real models. Skipped unless the
// stem-models workflow (or a person) points NEOBUZZ_STEM_MODELS at the exported models and NEOBUZZ_STEM_REFERENCE at the folder
// tools/stems/validate.py --reference wrote: per model, the test clip and PyTorch's stems for it. It separates the clip with the C# code
// and compares each stem to PyTorch's (max abs difference, SDR), writing the figures to NEOBUZZ_STEM_REPORT (JSON) when that is set.
using System.Diagnostics;
using System.Text.Json;
using ReBuzz.Core.AudioLibrary.Stems;

namespace ReBuzz.SessionTests.Stems;

[TestFixture]
public class StemRealModelTests
{
    static float[] ReadF32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var f = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
        return f;
    }

    [Test]
    public void The_C_sharp_pipeline_matches_PyTorch_on_the_real_models()
    {
        string? models = Environment.GetEnvironmentVariable("NEOBUZZ_STEM_MODELS");
        string? reference = Environment.GetEnvironmentVariable("NEOBUZZ_STEM_REFERENCE");
        if (string.IsNullOrEmpty(models) || string.IsNullOrEmpty(reference) || !Directory.Exists(reference))
            Assert.Ignore("NEOBUZZ_STEM_MODELS and NEOBUZZ_STEM_REFERENCE are not set (the real models are not in the repository).");
        var report = new List<object>();
        var failures = new List<string>();
        foreach (var dir in Directory.GetDirectories(reference!).OrderBy(d => d))
        {
            string id = Path.GetFileName(dir);
            var manifest = StemModelManifest.Load(Path.Combine(models!, id, StemModelManifest.FileName), out string? problem);
            Assert.That(manifest, Is.Not.Null, $"{id}: {problem}");
            var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "reference.json"))).RootElement;
            int n = meta.GetProperty("samples").GetInt32();
            var clip = ReadF32(Path.Combine(dir, "clip.f32"));
            var l = clip.AsSpan(0, n).ToArray();
            var r = clip.AsSpan(n, n).ToArray();
            var clock = Stopwatch.StartNew();
            var res = StemSeparator.Separate(manifest!, l, r, manifest!.SampleRate, null, null, null, CancellationToken.None);
            double seconds = clock.Elapsed.TotalSeconds;
            var per = new Dictionary<string, object>();
            foreach (var stem in StemSeparator.AllStems)
            {
                var want = ReadF32(Path.Combine(dir, stem + ".f32"));
                double err = 0, energy = 1e-12, worst = 0;
                for (int ch = 0; ch < 2; ch++)
                {
                    var got = ch == 0 ? res.Stems[stem].Left : res.Stems[stem].Right;
                    for (int i = 0; i < n; i++)
                    {
                        double w = want[ch * n + i], d = w - got[i];
                        energy += w * w;
                        err += d * d;
                        worst = Math.Max(worst, Math.Abs(d));
                    }
                }
                double sdr = 10 * Math.Log10(energy / (err + 1e-12));
                per[stem] = new { maxAbsDiff = worst, sdrDb = sdr };
                if (sdr < 35) failures.Add($"{id} {stem}: SDR against PyTorch {sdr:F1} dB");
            }
            report.Add(new { id, clipSeconds = n / (double)manifest.SampleRate, seconds, secondsPerMinute = seconds * 60 / (n / (double)manifest.SampleRate), perStem = per });
        }
        string? outPath = Environment.GetEnvironmentVariable("NEOBUZZ_STEM_REPORT");
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrEmpty(outPath)) File.WriteAllText(outPath, json);
        TestContext.Out.WriteLine(json);
        Assert.That(failures, Is.Empty);
    }
}
