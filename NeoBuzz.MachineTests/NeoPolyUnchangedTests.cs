using System.Security.Cryptography;
using NeoBuzz.Dsp;
using NeoBuzz.Poly;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The acid extension (AC1) changes nothing for what existed: every Neo Poly factory preset of the time and a set of
    /// settings that take the engine's other paths (legato, glide, every filter type, steals) render the same bits as on
    /// origin/main. The hashes in Golden/neo-poly-baseline.txt were written from origin/main before the extension; no audio is stored.
    /// Regenerate only on purpose: NEO_WRITE_GOLDEN=&lt;file&gt; dotnet test --filter NeoPolyUnchanged.
    /// </summary>
    [TestFixture]
    public class NeoPolyUnchangedTests
    {
        static string GoldenFile => Path.Combine(TestContext.CurrentContext.TestDirectory, "Golden", "neo-poly-baseline.txt");

        /// <summary>A short song played straight into the engine: overlaps, velocities either side of 100/127, a steal.</summary>
        static string Render(ParamValues v)
        {
            var e = new PolyEngine(v);
            e.SetSampleRate(Measure.Rate);
            var l = new float[256];
            var r = new float[256];
            var events = new (int At, int Key, int Note, float Vel)[]
            {
                (0, 0, 36, 1f), (3072, 1, 40, 0.5f), (6144, 0, -1, 0), (9216, 0, 43, 100 / 127f), (12288, 1, -1, 0),
                (15360, 0, -1, 0), (18432, 2, 48, 0.9f), (18432, 3, 55, 0.4f), (21504, 4, 60, 0.7f), (24576, 5, 62, 0.8f),
                (27648, 2, -1, 0), (27648, 3, -1, 0), (30720, 4, -1, 0), (30720, 5, -1, 0),
            };
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int total = 64000, ev = 0;
            for (int pos = 0; pos < total; pos += 256)
            {
                while (ev < events.Length && events[ev].At <= pos)
                {
                    var x = events[ev++];
                    if (x.Note < 0) e.NoteOff(x.Key); else e.NoteOn(x.Key, x.Note, x.Vel);
                }
                e.Process(l, r, 256);
                sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(l.AsSpan()));
                sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(r.AsSpan()));
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        /// <summary>The settings cases: name → what to set on a fresh table.</summary>
        static IEnumerable<(string Name, Action<ParamValues> Setup)> Cases()
        {
            yield return ("default", v => { });
            for (int f = 0; f <= 5; f++)
            {
                int type = f;
                yield return ($"filter-{f}", v => { v.Set(P.F1Type, type); v.Set(P.F1Reso, 800); v.Set(P.F1Cutoff, 500); });
            }
            yield return ("legato-glide-legato", v => { v.Set(P.VoiceMode, 2); v.Set(P.GlideMode, 2); v.Set(P.Glide, 300); });
            yield return ("legato-glide-always", v => { v.Set(P.VoiceMode, 2); v.Set(P.GlideMode, 1); v.Set(P.Glide, 500); });
            yield return ("mono-glide-legato", v => { v.Set(P.VoiceMode, 1); v.Set(P.GlideMode, 2); v.Set(P.Glide, 300); });
            yield return ("poly-glide-legato", v => { v.Set(P.GlideMode, 2); v.Set(P.Glide, 400); });
            yield return ("legato-no-glide", v => { v.Set(P.VoiceMode, 2); v.Set(P.GlideMode, 0); });
            yield return ("steal", v => { v.Set(P.Polyphony, 2); v.Set(P.Unison, 2); });
            yield return ("velocity", v => { v.Set(P.F1Vel, 1000); v.Set(P.VelAmp, 1000); v.Set(P.F1Env, 900); });
        }

        static List<(string Name, string Hash)> Current()
        {
            var list = new List<(string, string)>();
            foreach (var (_, machine, name, values) in PresetTests.AllPresets())
            {
                if (machine != "Neo Poly") continue;
                var v = new ParamValues(Params.Table);
                foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                list.Add(("preset " + name, Render(v)));
            }
            foreach (var (name, setup) in Cases())
            {
                var v = new ParamValues(Params.Table);
                setup(v);
                list.Add(("case " + name, Render(v)));
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
                if (!golden.TryGetValue(name, out var want)) continue;   // a preset or case added after the baseline
                if (want != hash) problems.Add(name);
            }
            foreach (var name in golden.Keys)
                if (!now.Any(x => x.Name == name)) problems.Add(name + " (missing)");
            Assert.That(golden.Count, Is.GreaterThan(30));
            Assert.That(problems, Is.Empty, "renders that differ from origin/main:\n" + string.Join("\n", problems));
        }
    }
}
