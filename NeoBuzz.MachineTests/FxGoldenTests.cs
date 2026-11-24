using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Mix and Amount on every Neo effect (docs/specs/machines.md, "Mix and Amount"): with Mix and Amount at their defaults an effect renders
    /// the bits it rendered before the two controls existed. The hashes in Goldens/fx-defaults.txt were made on origin/main (before the
    /// controls) from a drum loop and a chord through every effect at its defaults and at each of its factory presets; this test renders the
    /// same now. NEO_WRITE_FX_GOLDEN=1 rewrites the file (only ever from a build that has no new parameters).
    /// </summary>
    [TestFixture]
    public class FxGoldenTests
    {
        static string GoldenFile([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Goldens", "fx-defaults.txt");

        /// <summary>The effects that gained Mix and Amount (Neo Plug is hosted, a machine of its own).</summary>
        internal static readonly string[] Effects =
        {
            "Neo Clipper", "Neo Comp", "Neo De-esser", "Neo Delay", "Neo Dither", "Neo Drive", "Neo EQ", "Neo Ensemble", "Neo Exciter", "Neo Filter",
            "Neo Gate", "Neo Imager", "Neo Limiter", "Neo Low End", "Neo Multiband", "Neo Spectral", "Neo Strip", "Neo Tape", "Neo Transient",
            "Neo Tune", "Neo Verb", "Neo Utility",
        };

        /// <summary>A drum loop: kick, snare and hats at 120 bpm, 2 s, peak about −3 dBFS.</summary>
        internal static float[] DrumLoop(int rate = Measure.Rate, double seconds = 2)
        {
            var y = new float[(int)(rate * seconds)];
            uint seed = 12345;
            double Noise() { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / (double)(1 << 24) * 2 - 1; }
            void Hit(double at, Func<double, double> voice, double length)
            {
                int s = (int)(at * rate), n = (int)(length * rate);
                for (int i = 0; i < n && s + i < y.Length; i++) y[s + i] += (float)voice(i / (double)rate);
            }
            for (double b = 0; b < seconds; b += 0.5)
            {
                Hit(b, t => 0.7 * Math.Sin(2 * Math.PI * (45 * t + 90 * (1 - Math.Exp(-t * 30)) / 30)) * Math.Exp(-t * 14), 0.4);
                Hit(b + 0.25, t => 0.3 * Noise() * Math.Exp(-t * 40), 0.08);
            }
            for (double b = 0.25; b < seconds; b += 0.5)
                Hit(b, t => 0.45 * Noise() * Math.Exp(-t * 18) * 0.5 + 0.35 * Math.Sin(2 * Math.PI * 190 * t) * Math.Exp(-t * 20), 0.2);
            return y;
        }

        /// <summary>A chord (C3 E3 G3 B3 with a few harmonics), 2 s, held, peak about −6 dBFS.</summary>
        internal static float[] Chord(int rate = Measure.Rate, double seconds = 2)
        {
            var y = new float[(int)(rate * seconds)];
            double[] notes = { 130.81, 164.81, 196.00, 246.94 };
            for (int i = 0; i < y.Length; i++)
            {
                double t = i / (double)rate, v = 0;
                foreach (double f in notes)
                    for (int h = 1; h <= 4; h++) v += Math.Sin(2 * Math.PI * f * h * t) / (h * 4.0);
                y[i] = (float)(0.35 * v * Math.Min(1, t * 50) * Math.Min(1, (seconds - t) * 20));
            }
            return y;
        }

        static string Hash(float[] y)
        {
            var bytes = new byte[y.Length * 4];
            Buffer.BlockCopy(y, 0, bytes, 0, bytes.Length);
            return Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        }

        static IEnumerable<string> Lines()
        {
            var drums = DrumLoop();
            var chord = Chord();
            var presets = PresetTests.AllPresets().Where(p => Effects.Contains(p.Machine)).ToList();
            foreach (string name in Effects)
            {
                var (table, render) = PresetTests.Machines[name];
                var defaults = new ParamValues(table);
                yield return $"{name}\t(defaults)\t{Hash(render(defaults, drums))}\t{Hash(render(new ParamValues(table), chord))}";
                foreach (var (file, machine, preset, values) in presets.Where(p => p.Machine == name))
                {
                    ParamValues Make()
                    {
                        var v = new ParamValues(table);
                        foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                        return v;
                    }
                    yield return $"{name}\t{preset}\t{Hash(render(Make(), drums))}\t{Hash(render(Make(), chord))}";
                }
            }
        }

        [Test]
        public void EveryEffectAtDefaultMixAndAmountRendersWhatItRenderedBefore()
        {
            var now = Lines().ToList();
            if (Environment.GetEnvironmentVariable("NEO_WRITE_FX_GOLDEN") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GoldenFile())!);
                File.WriteAllText(GoldenFile(), "# Neo effects at their defaults and every factory preset, SHA-256 (first 16 hex) of the float32 render of a drum loop and of a chord (FxGoldenTests).\n# effect<TAB>preset<TAB>drums<TAB>chord. Made on origin/main before Mix and Amount existed.\n" + string.Join("\n", now) + "\n");
                Assert.Inconclusive("written");
            }
            var golden = File.ReadAllLines(GoldenFile()).Where(l => l.Length > 0 && l[0] != '#').ToList();
            var problems = new List<string>();
            var byKey = golden.ToDictionary(l => string.Join('\t', l.Split('\t').Take(2)));
            foreach (var line in now)
            {
                string key = string.Join('\t', line.Split('\t').Take(2));
                if (!byKey.TryGetValue(key, out var old)) { problems.Add(key.Replace('\t', '/') + ": not in the golden file"); continue; }
                if (old != line) problems.Add(key.Replace('\t', '/') + ": the render changed (" + old.Split('\t')[2] + "/" + old.Split('\t')[3] + " -> " + line.Split('\t')[2] + "/" + line.Split('\t')[3] + ")");
            }
            foreach (string key in byKey.Keys)
                if (!now.Any(l => l.StartsWith(key + "\t"))) problems.Add(key.Replace('\t', '/') + ": in the golden file, not rendered");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
            TestContext.Out.WriteLine($"{now.Count} renders compared");
        }
    }
}
