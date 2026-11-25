using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Lo-Fi on the shared Mix and Amount stage (docs/specs/machines.md, "Mix and Amount"): the user approved Lo-Fi's round-2
    /// sound, so moving it onto FxStage must not change a bit. Goldens/lofi-lf2.txt holds the renders of LF2's head (120d54b3,
    /// before the stage) of a drum loop and a chord through the defaults and each of the ten factory presets, each at Amount 100 %
    /// (as the presets stand), 50 % and 150 % (Lo-Fi's own scaling, which the stage leaves alone above 5 %). This test renders the
    /// same now. NEO_WRITE_LOFI_GOLDEN=1 rewrites the file (only ever from LF2's head).
    /// </summary>
    [TestFixture]
    public class LoFiGoldenTests
    {
        static string GoldenFile([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Goldens", "lofi-lf2.txt");

        static string Hash(float[] y)
        {
            var bytes = new byte[y.Length * 4];
            Buffer.BlockCopy(y, 0, bytes, 0, bytes.Length);
            return Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        }

        static IEnumerable<string> Lines()
        {
            var drums = FxGoldenTests.DrumLoop();
            var chord = FxGoldenTests.Chord();
            var (table, render) = PresetTests.Machines["Neo Lo-Fi"];
            int amount = table.First(d => d.Id == "Amount").Index;
            var presets = new List<(string Name, Dictionary<int, int> Values)> { ("(defaults)", new Dictionary<int, int>()) };
            presets.AddRange(PresetTests.AllPresets().Where(p => p.Machine == "Neo Lo-Fi").Select(p => (p.Preset, p.Values.Where(kv => kv.Key >= 0).ToDictionary(kv => kv.Key, kv => kv.Value))));
            foreach (var (name, values) in presets)
                foreach (double percent in new[] { 100.0, 50.0, 150.0 })
                {
                    ParamValues Make()
                    {
                        var v = new ParamValues(table);
                        foreach (var kv in values) v.Set(kv.Key, kv.Value);
                        if (percent != 100.0) v.Set(amount, table[amount].Raw(percent));
                        return v;
                    }
                    yield return $"Neo Lo-Fi\t{name}\t{percent:0}%\t{Hash(render(Make(), drums))}\t{Hash(render(Make(), chord))}";
                }
        }

        [Test]
        public void EveryLoFiPresetRendersWhatLf2Rendered()
        {
            var now = Lines().ToList();
            if (Environment.GetEnvironmentVariable("NEO_WRITE_LOFI_GOLDEN") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GoldenFile())!);
                File.WriteAllText(GoldenFile(), "# Neo Lo-Fi at its defaults and every factory preset, at Amount 100 %, 50 % and 150 %: SHA-256 (first 16 hex) of the float32 render of a drum loop and of a chord (LoFiGoldenTests).\n# machine<TAB>preset<TAB>amount<TAB>drums<TAB>chord. Made on machines/neo-lofi-lf1 at 120d54b3 (LF2, the sound the user approved), before Lo-Fi used FxStage.\n" + string.Join("\n", now) + "\n");
                Assert.Inconclusive("written");
            }
            var golden = File.ReadAllLines(GoldenFile()).Where(l => l.Length > 0 && l[0] != '#').ToList();
            Assert.That(golden.Count(l => l.Contains("\t100%\t")), Is.EqualTo(11), "the defaults and the ten presets");
            Assert.That(now, Is.EqualTo(golden), "Neo Lo-Fi renders what LF2 rendered");
            TestContext.Out.WriteLine($"{now.Count} renders compared");
        }
    }
}
