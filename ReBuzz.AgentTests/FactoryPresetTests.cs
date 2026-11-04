// Fork (docs/specs/mcp-server.md §12.3, §12.11, U4): the curated machines' factory presets. Each source
// (Machines/Manifests/<kind>/<Gear>.presets.txt) compiles through its curated manifest with param_set's
// parser, inside the safe ranges, with names that say what they do ("Group — Name") and a comment each;
// the .prs.xml beside it is exactly what it compiles to (rebuild with REBUZZ_WRITE_PRESETS=1), and reads
// back as ReBuzz's preset serializer reads it. Whether each one sounds, at a consistent level and below
// −1 dBFS, is measured on the machines themselves: tools/preset-check.py (Linux and the Windows package).
using System.Text.RegularExpressions;
using ReBuzz.Agent.Model;

namespace ReBuzz.AgentTests
{
    [TestFixture]
    public class FactoryPresetTests
    {
        /// <summary>Every source beside a curated manifest, with its manifest and preset file.</summary>
        internal static IEnumerable<(string Source, MachineManifest Manifest, string Prs)> Sources()
        {
            var curated = CuratedManifestTests.Curated();
            foreach (var source in Directory.GetFiles(Path.Combine(CuratedManifestTests.Root(), "Machines", "Manifests"), "*" + FactoryPresets.SourceSuffix, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                string gear = Path.GetFileName(source)[..^FactoryPresets.SourceSuffix.Length];
                Assert.That(curated.ContainsKey(gear), Is.True, $"{source}: factory presets are for curated machines");
                yield return (source, curated[gear], Path.Combine(Path.GetDirectoryName(source)!, gear + FactoryPresets.FileSuffix));
            }
        }

        [Test]
        public void EverySourceCompilesAndItsPresetFileIsWhatItCompilesTo()
        {
            bool write = Environment.GetEnvironmentVariable("REBUZZ_WRITE_PRESETS") == "1";
            var stale = new List<string>();
            int count = 0;
            foreach (var (source, manifest, prs) in Sources())
            {
                var presets = FactoryPresets.Parse(File.ReadAllText(source), manifest, Path.GetFileName(source));
                string xml = FactoryPresets.ToXml(manifest, presets);
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(xml);   // the BOM is in the text
                if (write) File.WriteAllBytes(prs, bytes);
                if (!File.Exists(prs) || !File.ReadAllBytes(prs).AsSpan().SequenceEqual(bytes)) stale.Add(prs);
                count += presets.Count;
                TestContext.Out.WriteLine($"{manifest.Gear}: {presets.Count} presets");
            }
            Assert.That(stale, Is.Empty, "preset files that aren't what their sources compile to (REBUZZ_WRITE_PRESETS=1 rewrites them)");
            Assert.That(count, Is.GreaterThan(50));
        }

        [Test]
        public void PresetsAreNamedForWhatTheyDoAndSetValuesInsideTheirRanges()
        {
            var problems = new List<string>();
            foreach (var (source, manifest, prs) in Sources())
            {
                var presets = FactoryPresets.Parse(File.ReadAllText(source), manifest, Path.GetFileName(source));
                Assert.That(presets.Count, Is.InRange(3, 12), $"{manifest.Gear}: a handful of presets");
                foreach (var p in presets)
                {
                    // "Group — What it does", as the Neo machines' presets are named (the preset bars group by the prefix).
                    if (!Regex.IsMatch(p.Name, @"^[A-Z][\w\- ]+ — [A-Z0-9][^—]+$")) problems.Add($"{manifest.Gear}: \"{p.Name}\" isn't \"Group — Name\"");
                    if (p.Comment.Length < 12) problems.Add($"{manifest.Gear}: \"{p.Name}\" has no comment saying what it is");
                    foreach (var (entry, raw) in p.Values)
                    {
                        if (raw < entry.Min || raw > entry.Max) problems.Add($"{manifest.Gear}/{p.Name}: {entry.Name} = {raw} outside {entry.Min}…{entry.Max}");
                        if (entry.SafeRange != null && !entry.SafeRange.Contains(raw)) problems.Add($"{manifest.Gear}/{p.Name}: {entry.Name} outside its safe range");
                    }
                }
                // Every preset is its own sound.
                var sounds = presets.Select(p => string.Join(",", manifest.Parameters.Concat(manifest.TrackParameters).Select(e => p.Values.TryGetValue(e, out int v) ? v : e.Default))).ToList();
                if (sounds.Distinct().Count() != sounds.Count) problems.Add($"{manifest.Gear}: two presets are the same sound");
                // The file lists every parameter, in the machine's order, global then track (presets apply by position
                // where names repeat: FSM Infector's " - Decay").
                foreach (var (name, comment, values) in FactoryPresets.Read(File.ReadAllText(prs)))
                {
                    var expected = manifest.Parameters.Select(e => (e.Name, 1, e.Index)).Concat(manifest.TrackParameters.Select(e => (e.Name, 2, e.Index))).ToList();
                    if (!values.Select(v => (v.Name, v.Group, v.Index)).SequenceEqual(expected)) problems.Add($"{manifest.Gear}/{name}: not every parameter in order");
                }
            }
            Assert.That(problems, Is.Empty);
        }

        [Test]
        public void TheCompilerRefusesWhatParamSetRefuses()
        {
            var delay = CuratedManifestTests.Curated()["Jeskola Delay"];
            string Head = "machine \"Jeskola Delay\"\npreset \"Echo — Test\"\ncomment \"A test preset\"\n";
            Assert.That(() => FactoryPresets.Parse(Head + "Feedback = -100%\n", delay), Throws.InstanceOf<FormatException>().With.Message.Contains("safe range"),
                "Feedback at −100 % never decays (machine-safety.md §3.4)");
            Assert.That(() => FactoryPresets.Parse(Head + "Loudness = 3\n", delay), Throws.InstanceOf<FormatException>().With.Message.Contains("no parameter"));
            Assert.That(() => FactoryPresets.Parse(Head + "Feedback = 12 Hz\n", delay), Throws.InstanceOf<FormatException>().With.Message.Contains("presets:4"));
            Assert.That(() => FactoryPresets.Parse("machine \"Jeskola Reverb\"\n", delay), Throws.InstanceOf<FormatException>());
            Assert.That(() => FactoryPresets.Parse(Head, delay), Throws.InstanceOf<FormatException>().With.Message.Contains("sets nothing"));
            var ok = FactoryPresets.Parse(Head + "Feedback = 40%\nEcho level = 50%\n", delay);
            var feedback = delay.TrackParameters.Single(p => p.Name == "Feedback");
            Assert.That(ok.Single().Values[feedback], Is.EqualTo(feedback.Mapping.Raw(40, feedback.Min, feedback.Max)), "40 % as param_set reads it");
        }
    }
}
