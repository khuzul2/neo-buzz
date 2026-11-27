using System.Security.Cryptography;
using NeoBuzz.Dsp;
using NeoBuzz.Filter;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The vowel mode (EXT1) changes nothing for what existed: every Neo Filter factory preset of the time, the default patch, every type
    /// and slope, an automation lane on Type and a song saved before the new parameters render the same bits as on origin/main. The
    /// hashes in Golden/neo-filter-baseline.txt were written from origin/main before the vowel mode; no audio is stored.
    /// Regenerate only on purpose: NEO_WRITE_GOLDEN=&lt;file&gt; dotnet test --filter NeoFilterUnchanged.
    /// </summary>
    [TestFixture]
    public class NeoFilterUnchangedTests
    {
        static string GoldenFile => Path.Combine(TestContext.CurrentContext.TestDirectory, "Golden", "neo-filter-baseline.txt");

        /// <summary>Two seconds of stereo pink noise (a loud and a quiet half) through the engine, blocks of 100 samples, the song playing (so Song retrig runs).</summary>
        static string Render(ParamValues v, Action<ParamValues, int> automate = null)
        {
            var e = new FilterEngine(v) { Tempo = 120 };
            e.SetSampleRate(Measure.Rate);
            int total = 2 * Measure.Rate;
            var xl = Measure.PinkNoise(total, 0.2, 11);
            var xr = Measure.PinkNoise(total, 0.2, 29);
            for (int i = total / 2; i < total; i++) { xl[i] *= 0.1f; xr[i] *= 0.1f; }
            var l = new float[256];
            var r = new float[256];
            int samplesPerTick = (int)(60.0 * Measure.Rate / (120 * 4));
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int block = 0;
            for (int pos = 0; pos < total; pos += 100, block++)
            {
                int n = Math.Min(100, total - pos);
                automate?.Invoke(v, block);
                e.Transport = new FilterTransport { Playing = true, Tick = pos / samplesPerTick, PosInTick = pos % samplesPerTick, SamplesPerTick = samplesPerTick, TicksPerBeat = 4 };
                Array.Copy(xl, pos, l, 0, n);
                Array.Copy(xr, pos, r, 0, n);
                e.Process(l, r, n);
                sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(l.AsSpan(0, n)));
                sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(r.AsSpan(0, n)));
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        static ParamValues Preset(string name)
        {
            var found = PresetTests.AllPresets().First(p => p.Machine == "Neo Filter" && p.Preset == name);
            var v = new ParamValues(Params.Table);
            foreach (var kv in found.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        static void Phys(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static IEnumerable<(string Name, Action<ParamValues> Setup, Action<ParamValues, int> Automate)> Cases()
        {
            yield return ("default", v => { }, null);
            for (int type = 0; type <= 6; type++)
                for (int slope = 0; slope <= 1; slope++)
                {
                    int t = type, s = slope;
                    yield return ($"type-{t}-slope-{s}", v => { v.Set(P.Type, t); v.Set(P.Slope, s); Phys(v, P.Cutoff, 700); Phys(v, P.Resonance, 6); Phys(v, P.Drive, 9); }, null);
                }
            yield return ("env-lfo-retrig-env", v => { v.Set(P.Type, 3); v.Set(P.LfoRetrig, 2); Phys(v, P.LfoDepth, 2); v.Set(P.LfoSync, 9); Phys(v, P.EnvAmount, 3); }, null);
            yield return ("lfo-free-spread", v => { v.Set(P.Type, 0); v.Set(P.LfoRetrig, 0); v.Set(P.LfoShape, 6); Phys(v, P.LfoDepth, 2); Phys(v, P.LfoSpread, 90); }, null);
            yield return ("self-oscillation", v => { v.Set(P.Type, 2); v.Set(P.Resonance, Params.Table[P.Resonance].Max); }, null);
            yield return ("amount-mix", v => { v.Set(P.Type, 2); Phys(v, P.Amount, 60); Phys(v, P.Mix, 0.5); Phys(v, P.Drive, 12); }, null);
            // An automation lane on Type: it steps through the seven filters, every 800 samples.
            yield return ("automate-type", v => { Phys(v, P.Cutoff, 1500); Phys(v, P.Resonance, 3); }, (v, block) => v.Set(P.Type, block / 8 % 7));
            yield return ("automate-type-slope", v => { Phys(v, P.Cutoff, 900); }, (v, block) => { v.Set(P.Type, block / 5 % 7); v.Set(P.Slope, block / 11 % 2); });
        }

        static List<(string Name, string Hash)> Current()
        {
            var list = new List<(string, string)>();
            foreach (var (_, machine, name, values) in PresetTests.AllPresets())
            {
                if (machine != "Neo Filter" || name.StartsWith("Vowel")) continue;
                var v = new ParamValues(Params.Table);
                foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                list.Add(("preset " + name, Render(v)));
            }
            foreach (var (name, setup, automate) in Cases())
            {
                var v = new ParamValues(Params.Table);
                setup(v);
                list.Add(("case " + name, Render(v, automate)));
            }
            return list;
        }

        [Test]
        public void PresetsAndSettingsRenderBitForBitAsOnMain()
        {
            var now = Current();
            if (Environment.GetEnvironmentVariable("NEO_WRITE_GOLDEN") is { Length: > 0 } outPath)
            {
                File.WriteAllText(outPath, string.Join("\n", now.Select(x => x.Name + "\t" + x.Hash)) + "\n");
                Assert.Inconclusive("written to " + outPath);
            }
            var golden = File.ReadAllLines(GoldenFile).Where(s => s.Length > 0).Select(s => s.Split('\t')).ToDictionary(f => f[0], f => f[1]);
            var problems = new List<string>();
            foreach (var (name, hash) in now)
            {
                if (!golden.TryGetValue(name, out var want)) { problems.Add(name + " (not in the baseline)"); continue; }
                if (want != hash) problems.Add(name);
            }
            foreach (var name in golden.Keys)
                if (!now.Any(x => x.Name == name)) problems.Add(name + " (missing)");
            Assert.That(golden.Count, Is.GreaterThan(30));
            Assert.That(problems, Is.Empty, "renders that differ from origin/main:\n" + string.Join("\n", problems));
        }

        [Test]
        public void ASongSavedBeforeTheVowelModeLoadsAndRendersTheSame()
        {
            // The raw values a version 0 song holds (19 of them, Amount last), loaded through the machine's chain.
            var golden = File.ReadAllLines(GoldenFile).Where(s => s.Length > 0).Select(s => s.Split('\t')).ToDictionary(f => f[0], f => f[1]);
            foreach (var name in new[] { "Auto-Wah — Funky (Band-Pass)", "Sweep — Ladder Low-Pass (1 Bar)", "Peak — Resonant Peak Sweep (1 Bar)", "LFO — Sample and Hold (1/16)" })
            {
                var v0 = Preset(name);
                var saved = new int[19];
                for (int i = 0; i < 19; i++) saved[i] = v0[i];
                var now = FilterMigrations.Chain.MigrateGlobals(0, saved);
                Assert.That(now.Length, Is.EqualTo(Params.Table.Length));
                var v = new ParamValues(Params.Table);
                for (int i = 0; i < now.Length; i++) v.Set(i, now[i]);
                Assert.That(Render(v), Is.EqualTo(golden["preset " + name]), name);
                // A song from before Amount was appended (18 values) loads with Amount at 100 %.
                var shorter = new int[18];
                Array.Copy(saved, shorter, 18);
                var now18 = FilterMigrations.Chain.MigrateGlobals(0, shorter);
                var w = new ParamValues(Params.Table);
                for (int i = 0; i < now18.Length; i++) w.Set(i, now18[i]);
                Assert.That(Render(w), Is.EqualTo(golden["preset " + name]), name + " (18 values)");
            }
        }
    }
}
