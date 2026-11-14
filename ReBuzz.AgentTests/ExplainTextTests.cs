// Fork (docs/specs/agent-knowledge.md, K1): every parameter of every Neo machine (and Dye Essence) carries Explain text, a How
// sentence (how the control changes the sound) and a Sound sentence (what you will hear), so the agent (machine_describe), the
// editor's Explain strip and the studio inspector can explain any control. KnowledgePending lists the machines still to do:
// they warn, everything else fails.
using ReBuzz.Agent.Model;

namespace ReBuzz.AgentTests
{
    [TestFixture]
    public class ExplainTextTests
    {
        static bool InScope(string gear) => gear.StartsWith("Neo ", StringComparison.OrdinalIgnoreCase) || gear.Equals("Dye Essence", StringComparison.OrdinalIgnoreCase);

        /// <summary>The parameters without How or Sound, by machine, for every Neo machine and Dye Essence.</summary>
        internal static SortedDictionary<string, List<string>> Missing(IEnumerable<MachineManifest> manifests)
        {
            var missing = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in manifests.Where(m => InScope(m.Gear)))
            {
                var list = m.Parameters.Where(p => p.How.Trim().Length == 0 || p.Sound.Trim().Length == 0).Select(p => p.Id).ToList();
                if (list.Count > 0) missing[m.Gear] = list;
            }
            return missing;
        }

        [Test]
        public void EveryNeoParameterHasHowAndSoundExceptThePendingMachines()
        {
            var manifests = CuratedManifestTests.Generated().Values.ToList();
            Assert.That(manifests.Count(m => InScope(m.Gear)), Is.GreaterThanOrEqualTo(29), "the Neo family and Dye Essence are found");
            var missing = Missing(manifests);
            var failures = missing.Where(kv => !KnowledgePending.Gear.Contains(kv.Key)).ToList();
            var pending = missing.Where(kv => KnowledgePending.Gear.Contains(kv.Key)).ToList();

            Assert.That(failures, Is.Empty, "parameters without How or Sound (edit Params.def and run tools/gen-machine-params.py):\n"
                + string.Join("\n", failures.Select(kv => $"{kv.Key}: {kv.Value.Count} of {manifests.First(m => m.Gear == kv.Key).Parameters.Count}: {string.Join(", ", kv.Value.Take(6))}...")));

            var finished = KnowledgePending.Gear.Where(g => manifests.Any(m => m.Gear == g) && !missing.ContainsKey(g)).ToList();
            Assert.That(finished, Is.Empty, "these machines now have all their Explain text: remove them from KnowledgePending.Gear (and check their pages)\n" + string.Join(", ", finished));
            Assert.That(KnowledgePending.Gear.Where(g => manifests.All(m => m.Gear != g)), Is.Empty, "KnowledgePending names a machine that has no generated manifest");

            if (pending.Count > 0)
                Assert.Warn("Explain text still to write (K1): " + string.Join("; ", pending.Select(kv => $"{kv.Key} ({kv.Value.Count} parameters)")));
        }

        [Test]
        public void ExplainSentencesAreOneSentenceOfPlainText()
        {
            var problems = new List<string>();
            foreach (var m in CuratedManifestTests.Generated().Values.Where(m => InScope(m.Gear) && !KnowledgePending.Gear.Contains(m.Gear)))
                foreach (var p in m.Parameters)
                    foreach (var (what, text) in new[] { ("how", p.How), ("sound", p.Sound) })
                    {
                        string t = text.Trim();
                        if (t.Length < 12) problems.Add($"{m.Gear}/{p.Id} {what}: too short");
                        else if (t.Length > 260) problems.Add($"{m.Gear}/{p.Id} {what}: {t.Length} characters (one sentence)");
                        else if (!(t.EndsWith('.') || t.EndsWith(')'))) problems.Add($"{m.Gear}/{p.Id} {what}: does not end in a full stop");
                    }
            Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(30)));
        }

        [Test]
        public void EveryParameterOfAFinishedFamilyHasHowAndSound()
        {
            // K2: every packaged machine but the Neo ones and Dye Essence (KnowledgeFamilies), global and track parameters alike.
            var problems = new List<string>();
            int machines = 0;
            foreach (var m in CuratedManifestTests.Profiled().Values.Where(m => KnowledgeFamilies.Covers(m.Gear)))
            {
                machines++;
                foreach (var p in m.Parameters.Concat(m.TrackParameters))
                    foreach (var (what, text) in new[] { ("how", p.How), ("sound", p.Sound) })
                    {
                        string t = text.Trim();
                        if (t.Length < 12) problems.Add($"{m.Gear}/{p.Id} {what}: missing or too short");
                        else if (t.Length > 260) problems.Add($"{m.Gear}/{p.Id} {what}: {t.Length} characters (one sentence)");
                        else if (!(t.EndsWith('.') || t.EndsWith(')'))) problems.Add($"{m.Gear}/{p.Id} {what}: does not end in a full stop");
                    }
            }
            Assert.That(machines, Is.GreaterThanOrEqualTo(98));
            Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(30)));
        }

        [Test]
        public void TheMissingCheckFindsAGap()
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse("{\"format\":\"rebuzz-machine-manifest/1\",\"gear\":\"Neo X\",\"kind\":\"effect\",\"parameters\":[{\"index\":0,\"id\":\"a\",\"name\":\"A\",\"min\":0,\"max\":1,\"default\":0,\"kind\":\"linear\",\"how\":\"Does a thing.\",\"sound\":\"\"}]}")!.ToJsonString();
            var path = Path.Combine(Path.GetTempPath(), "explaintext-" + Guid.NewGuid().ToString("N") + MachineManifest.Suffix);
            try
            {
                File.WriteAllText(path, json);
                var m = MachineManifest.Load(path)!;
                Assert.That(Missing(new[] { m }), Does.ContainKey("Neo X"));
            }
            finally { File.Delete(path); }
        }
    }
}
