// Fork (docs/specs/mcp-server.md §12.2, §12.9, U3): the curated manifests. 25 machines curated, the
// rest drafts, and every machine in the package with a manifest; every curated manifest exact
// (mappings from evidence, no unit read from a description alone), its plain names unique, its roles
// from the vocabulary, its safe ranges inside the parameter ranges, its evidence in the repository;
// and a manual (rebuzz://machines/<slug>/manual) for every manifest, curated, draft or generated.
// U6 (§12.13): the Pedal effects and ReBuzz's own machines that had drafts have manifests generated from their
// source (tools/source_manifests.py), exact as the curated ones are; U7 (§12.14) the natives with sources that had drafts
// (tools/native_manifests.py, from their C++ declarations and DescribeValue).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReBuzz.Agent.Guides;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;

namespace ReBuzz.AgentTests
{
    [TestFixture]
    public class CuratedManifestTests
    {
        /// <summary>The 25 (mcp-server.md §12.9 says why these).</summary>
        static readonly string[] TheCurated =
        {
            "Pedal Juno106", "Pedal SH101", "Pedal FM", "Pedal M1", "FSM Infector", "FSM KickXP", "Oomek Aggressor 303", "Jeskola Bass 3", "Rout 909",
            "Jeskola Reverb", "Jeskola Delay", "Jeskola Cross Delay", "Jeskola Distortion", "Jeskola Filter", "Jeskola EQ-3", "Pedal Comp", "Pedal Plate",
            "Pedal EQ", "Pedal Filter", "Pedal Chorus", "Pedal Drum Matrix", "Oomek Exciter", "Oomek Masterizer", "FSM PanzerDelay", "Joachims Mars filter 2",
        };

        /// <summary>U6: the manifests generated from source (tools/source_manifests.py's list).</summary>
        static readonly string[] TheFromSource =
        {
            "Pedal Shaper", "Pedal Gate", "Pedal Gain", "Pedal Gain Multi", "Pedal MComp", "AudioBlock", "Connection Mixer Console", "EnvelopeBlock",
            "QuickNote", "ReBuzz Audio In", "ReBuzz Audio Out", "ReBuzz Groove", "ReBuzz Midi Output", "Session Clip Player", "ManagedMultiIOEffect",
            // U6 for the Pedal synths (§12.15): the Pedal machines that had stayed drafts
            "Pedal Tracker", "Pedal Chord", "Pedal LFO", "Pedal Peer Control",
            // U7: the natives (tools/native_manifests.py)
            "FSM WahMan", "FSM WahManPro", "FSM WahPro2", "FSM Chorus2", "FSM PhatMan", "Jeskola NiNjA dElaY", "Jeskola Noise Generator", "Jeskola Trilok",
        };

        internal static string Root()
        {
            string? env = Environment.GetEnvironmentVariable("NEO_BUZZ_ROOT");
            if (!string.IsNullOrEmpty(env)) return env;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "Machines", "Manifests")) && Directory.Exists(Path.Combine(dir.FullName, "ReBuzz.Agent")))
                    return dir.FullName;
            Assert.Inconclusive("repository root not found");
            return "";
        }

        /// <summary>Drafts and curated manifests (Machines/Manifests), by gear.</summary>
        internal static Dictionary<string, MachineManifest> Profiled() =>
            Directory.GetFiles(Path.Combine(Root(), "Machines", "Manifests"), "*" + MachineManifest.Suffix, SearchOption.AllDirectories)
                .Select(f => MachineManifest.Load(f)!).ToDictionary(m => m.Gear);

        /// <summary>The Neo machines' generated manifests (beside their sources), by gear.</summary>
        internal static Dictionary<string, MachineManifest> Generated() =>
            Directory.GetFiles(Path.Combine(Root(), "Machines"), "*" + MachineManifest.Suffix, SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/Machines/Manifests/") && !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/"))
                .Select(f => MachineManifest.Load(f)!).ToDictionary(m => m.Gear);

        internal static Dictionary<string, MachineManifest> Curated() => Profiled().Values.Where(m => m.IsCurated).ToDictionary(m => m.Gear);

        static ParamInfo Info(ManifestParameter p) => new()
        {
            Name = p.Name, Index = p.Index, Group = p.Group, Description = p.Description,
            Min = p.Min, Max = p.Max, Default = p.Default, Mapping = p.Mapping,
            Describe = r => MachineManual.Format(p, r),
        };

        [Test]
        public void EveryMachineInThePackageHasAManifest()
        {
            // The package's machines: what the profiler found in ReBuzz.exe's Gear on Windows and rebuzz-headless's on Linux
            // (Machines/Manifests/machine-profile.json, keyed by gear).
            var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "Machines", "Manifests", "machine-profile.json")))!["machines"]!.AsObject();
            var profiled = Profiled();
            var generated = Generated();
            var missing = new List<string>();
            foreach (var (gear, entry) in summary)
            {
                bool isGenerated = entry!["generated"]?.GetValue<bool>() == true;
                if (isGenerated ? !generated.ContainsKey(gear) : !profiled.ContainsKey(gear)) missing.Add(gear);
            }
            Assert.That(missing, Is.Empty, "machines in the package without a manifest");
            Assert.That(profiled.Keys.Concat(generated.Keys).Except(summary.Select(kv => kv.Key)), Is.Empty, "manifests of machines the package doesn't have");
            Assert.That(summary.Count, Is.GreaterThanOrEqualTo(108));

            Assert.That(Curated().Keys, Is.EquivalentTo(TheCurated), "25 curated");
            Assert.That(profiled.Values.Where(m => m.IsGeneratedFromSource).Select(m => m.Gear), Is.EquivalentTo(TheFromSource), "19 from source (U6, §12.15), 8 natives (U7)");
            Assert.That(profiled.Values.Where(m => !m.IsCurated && !m.IsGeneratedFromSource).All(m => m.IsDraft), Is.True, "the rest drafts");
            Assert.That(generated.Values.All(m => m.Quality == "generated"), Is.True);
        }

        [Test]
        public void EveryCuratedManifestIsExact()
        {
            string root = Root();
            var problems = new List<string>();
            foreach (var m in Curated().Values)
            {
                string g = m.Gear;
                if (m.Summary.Length < 40 || m.Summary.Contains("profiled draft")) problems.Add($"{g}: a summary that says what it's for");
                if (m.Type.Length == 0) problems.Add($"{g}: a type");
                if (m.Quirks.Count == 0) problems.Add($"{g}: quirks");
                if (m.Evidence.Count == 0) problems.Add($"{g}: evidence");
                if (m.Health == null) problems.Add($"{g}: the profile's health");
                // Evidence names files in the repository: each one is there.
                foreach (var e in m.Evidence)
                {
                    var path = Regex.Match(e, @"^((?:ExternalMachines|RunTime|ReBuzz|Machines|docs|tools)/[^—]+?)\s+—");
                    if (path.Success && !File.Exists(Path.Combine(root, path.Groups[1].Value)) && !Directory.Exists(Path.Combine(root, path.Groups[1].Value)))
                        problems.Add($"{g}: evidence {path.Groups[1].Value} isn't in the repository");
                }
                // Plain names name one parameter, global or track: param_set resolves them.
                var every = m.Parameters.Concat(m.TrackParameters).ToList();
                var plain = every.GroupBy(p => p.PlainName, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
                if (plain.Count > 0) problems.Add($"{g}: plain names used twice: {string.Join(", ", plain)}");
                foreach (var (list, where) in new[] { (m.Parameters, "parameter"), (m.TrackParameters, "track parameter") })
                {
                    foreach (var p in list)
                    {
                        string at = $"{g} {where} \"{p.Name}\"";
                        if (p.PlainName.Length == 0) problems.Add($"{at}: no plain name");
                        else if (every.Any(q => q != p && string.Equals(q.Name.Trim(), p.PlainName, StringComparison.OrdinalIgnoreCase)))
                            problems.Add($"{at}: its plain name \"{p.PlainName}\" is another parameter's name");
                        if (p.Role.Length > 0 && !ManifestRoles.IsKnown(p.Role)) problems.Add($"{at}: unknown role {p.Role}");
                        if (p.Description.Length == 0) problems.Add($"{at}: no description");
                        if (p.Confidence.Length > 0) problems.Add($"{at}: a draft's confidence ({p.Confidence}): curated mappings are exact");
                        if (p.Default < p.Min || p.Default > p.Max) problems.Add($"{at}: default {p.Default} outside {p.Min}…{p.Max}");
                        if (p.SafeRange is SafeRange s && (s.Min < p.Min || s.Max > p.Max || !s.Contains(p.Default) || s.Reason.Length == 0))
                            problems.Add($"{at}: safe range {s.Min}…{s.Max} must lie inside {p.Min}…{p.Max}, hold the default and say why");
                        problems.AddRange(MappingProblems(p).Select(x => $"{at}: {x}"));
                    }
                }
                // Safe ranges the profile measured stay (machine-safety.md §3.4): the curation keeps them, or tightens them.
                // U2 in depth (mcp-server.md §12.12) tightened Jeskola Delay's at +100 % (it rings) and gave Jeskola Filter's Resonance one.
                if (g == "Jeskola Delay" && m.FindTrack("Feedback")?.SafeRange is not { Min: 1, Max: 127 }) problems.Add($"{g}: U2's Feedback range (1…127) is gone");
                if (g == "Jeskola Filter" && m.Find("Resonance")?.SafeRange is not { Min: 0, Max: 118 }) problems.Add($"{g}: U2's Resonance range (0…118) is gone");
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [Test]
        public void EveryManifestFromSourceIsExact()
        {
            // U6 (mcp-server.md §12.13): from the machine's own declarations and the code that uses each value; no
            // draft's confidence or suggestions; the profile's health kept; every mapping as exact and usable as a curated one.
            string root = Root();
            var problems = new List<string>();
            foreach (var m in Profiled().Values.Where(m => m.IsGeneratedFromSource))
            {
                string g = m.Gear;
                if (m.Summary.Length < 40 || m.Summary.Contains("profiled draft")) problems.Add($"{g}: a summary that says what it is");
                if (m.Health == null) problems.Add($"{g}: the profile's health");
                var source = Regex.Match(m.Source, @"^generated by tools/(?:source|native)_manifests\.py from (\S+\.(?:cs|cpp)) ");
                if (!source.Success || !File.Exists(Path.Combine(root, source.Groups[1].Value))) problems.Add($"{g}: its source file ({m.Source})");
                foreach (var (list, where) in new[] { (m.Parameters, "parameter"), (m.TrackParameters, "track parameter") })
                    foreach (var p in list)
                    {
                        string at = $"{g} {where} \"{p.Name}\"";
                        if (p.Description.Length == 0) problems.Add($"{at}: no description");
                        if (p.Confidence.Length > 0) problems.Add($"{at}: a draft's confidence ({p.Confidence})");
                        if (p.SuggestedRole.Length > 0) problems.Add($"{at}: a draft's suggested role");
                        if (p.Default < p.Min || p.Default > p.Max) problems.Add($"{at}: default {p.Default} outside {p.Min}…{p.Max}");
                        problems.AddRange(MappingProblems(p).Select(x => $"{at}: {x}"));
                    }
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
            // What a manifest from source says, on one example: Pedal Shaper's Drive is dB, from its code ((Drive − 48) × 0.5 dB).
            var shaper = Profiled()["Pedal Shaper"].Find("Drive")!;
            Assert.That(MachineManual.Format(shaper, 48), Is.EqualTo("0.0 dB").Or.EqualTo("+0.0 dB"));
            Assert.That(MachineManual.Format(shaper, 96), Does.Contain("24"));
            Assert.That(shaper.OwnTexts, Is.False, "its own texts are bare numbers");
        }

        [Test]
        public void TextsThatNeedTheEnginesRateOrPictureASliderReadThroughTheirMappings()
        {
            // UI gallery review (win-912, win-913): Oomek Exciter's corners read "1 Hz" while its markers stood near 1.8 and
            // 3.7 kHz; Oomek Masterizer's crossovers read "1 Hz" and "0 Hz" and its Knee "-- 16 -- h a r d". The mappings
            // are right: they are the machines' own formulas at 44.1 kHz (DescribeValue in Exciter.cpp and Masterizer.cpp).
            // Their own texts divide by the engine's sample rate, which a native has only once the engine has run (0
            // before), and the knee's is a picture for Buzz's slider: these parameters read through their mappings.
            var curated = Curated();
            var exciter = curated["Oomek Exciter"];
            var masterizer = curated["Oomek Masterizer"];
            foreach (var (manifest, name, raw, text) in new[]
            {
                (exciter, "Pre Cut", 128, "3.74 kHz"), (exciter, "Post Cut", 60, "1.76 kHz"),
                (masterizer, "Lo | Mid Split", 17, "125 Hz"), (masterizer, "Mid | Hi Split", 18, "1.55 kHz"), (masterizer, "Knee", 16, "16"),
            })
            {
                var p = manifest.Find(name)!;
                Assert.That(p.OwnTexts, Is.False, $"{manifest.Gear} {name}: read through its mapping");
                Assert.That(p.Default, Is.EqualTo(raw));
                Assert.That(MachineManual.Format(p, raw), Is.EqualTo(text), $"{manifest.Gear} {name} at its default");
            }
            // The machines' formulas at 44.1 kHz, at the ends of their ranges.
            const double w = 6.28319 / 44100;
            Assert.That(exciter.Find("Pre Cut")!.Mapping.Value(239, 1, 239), Is.EqualTo(239 * 0.0041666666667 / w + 1).Within(0.5));
            Assert.That(exciter.Find("Post Cut")!.Mapping.Value(1, 1, 239), Is.EqualTo(1 * 0.0041666666667 / w + 1).Within(0.5));
            Assert.That(masterizer.Find("Lo | Mid Split")!.Mapping.Value(112, 2, 112), Is.EqualTo(112 / 960.0 / w + 1).Within(0.5));
            Assert.That(masterizer.Find("Mid | Hi Split")!.Mapping.Value(240, 0, 240), Is.EqualTo((240 + 35) / 240.0 / w).Within(0.5));
            Assert.That(curated["Rout 909"].Summary, Does.Not.Contain("repo"), "no developer notes in the summary the editor shows");
        }

        /// <summary>Why a curated parameter's mapping isn't exact and usable, if it isn't.</summary>
        static IEnumerable<string> MappingProblems(ManifestParameter p)
        {
            var m = p.Mapping;
            int count = p.Max - p.Min + 1;
            switch (m.Kind)
            {
                case MappingKind.Enum:
                    if (m.Names == null || m.Names.Count != count) yield return $"{m.Names?.Count ?? 0} choices for {count} values";
                    else if (m.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != m.Names.Count) yield return "choices repeat";
                    break;
                case MappingKind.Switch:
                    if (count != 2) yield return $"a switch over {count} values";
                    break;
                case MappingKind.Table:
                    var pts = m.Points!;
                    if (pts[0].Raw != p.Min || pts[^1].Raw != p.Max) yield return $"its table runs {pts[0].Raw}…{pts[^1].Raw}, not {p.Min}…{p.Max}";
                    for (int i = 1; i < pts.Count; i++)
                        if (pts[i].Raw <= pts[i - 1].Raw) yield return $"table raws not increasing at {pts[i].Raw}";
                    if (pts.Skip(1).Any(x => double.IsInfinity(x.Value))) yield return "an infinity past its first point";
                    break;
                case MappingKind.Exp or MappingKind.Time or MappingKind.Freq or MappingKind.Ratio:
                    if (!(m.A > 0 && m.B > 0)) yield return $"an exponential mapping needs positive ends ({m.A}, {m.B})";
                    break;
            }
            if (m.Kind is not (MappingKind.Enum or MappingKind.Switch or MappingKind.Table) && (!double.IsFinite(m.A) || !double.IsFinite(m.B)))
                yield return "infinite ends";
            if (m.Unit.Length > 0 && Quantity.DimensionOf(m.Unit) == null && m.Unit != ":1") yield return $"a unit param_set can't read: {m.Unit}";
            // Every value the manual prints at the ends and the default comes back through param_set as the same value.
            foreach (int raw in new[] { p.Min, p.Default, p.Max }.Distinct())
            {
                string shown = MachineManual.Format(p, raw);
                int back;
                string? why = null;
                try { back = ValueResolver.Resolve(Info(p), JsonValue.Create(shown), p.Default); }
                catch (ToolError e) { why = e.Message; back = raw; }
                if (why != null) yield return $"\"{shown}\" doesn't resolve: {why}";
                else if (MachineManual.Format(p, back) != shown) yield return $"\"{shown}\" comes back as \"{MachineManual.Format(p, back)}\"";
            }
        }

        [Test]
        public void EveryManifestHasAManual()
        {
            var problems = new List<string>();
            foreach (var m in Profiled().Values.Concat(Generated().Values))
            {
                string text = MachineManual.Build(m, null);
                if (!text.StartsWith($"# {m.Gear} manual\n")) problems.Add($"{m.Gear}: title");
                if (m.Summary.Length > 0 && !text.Contains(m.Summary)) problems.Add($"{m.Gear}: summary");
                if (!text.Contains("## Parameters")) problems.Add($"{m.Gear}: parameters");
                // Every parameter is a table row (generated manifests may fold repeated groups: GuideTests checks those).
                if (!m.IsDraft && !m.IsCurated && !m.IsGeneratedFromSource) continue;
                foreach (var p in m.Parameters.Concat(m.TrackParameters))
                    if (!text.Contains("| " + p.Name.Replace("|", "\\|") + " |") && !Folded(text, p.Group)) problems.Add($"{m.Gear}: {p.Name} not in a table");
                if (m.TrackParameters.Count > 0 && !text.Contains("### Track parameters")) problems.Add($"{m.Gear}: track parameters");
                if (m.Health is { Problems.Count: > 0 } && !text.Contains("## Health")) problems.Add($"{m.Gear}: health");
                if (m.Notes != null && m.Kind == "generator" && !text.Contains("## Notes")) problems.Add($"{m.Gear}: how it takes notes");
                if (m.IsCurated)
                {
                    if (!text.Contains("## Quirks")) problems.Add($"{m.Gear}: quirks");
                    if (!text.Contains("## Sources")) problems.Add($"{m.Gear}: sources");
                    foreach (var p in m.Parameters.Concat(m.TrackParameters))
                        if (!text.Contains("**" + p.PlainName + "**") && !Folded(text, p.Group)) problems.Add($"{m.Gear}: plain name of {p.Name}");
                    if (!text.Contains("exact units from a curated manifest")) problems.Add($"{m.Gear}: says it's curated");
                }
                else if (m.IsGeneratedFromSource)
                {
                    if (!text.Contains("generated from its source code") || !text.Contains("## Sources")) problems.Add($"{m.Gear}: says it's from its source");
                }
                else if (!text.Contains("profiled draft")) problems.Add($"{m.Gear}: says it's a draft");
                var resource = MachineManual.Resource(m);
                if (resource.Uri != MachineManual.UriFor(m.Gear) || resource.Read() != text) problems.Add($"{m.Gear}: resource");
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems));

            // What a curated manual says, on one example.
            var juno = MachineManual.Build(Curated()["Pedal Juno106"], new[] { "Brass 1" });
            Assert.That(juno, Does.Contain("| VCF Freq | 30 Hz … 16 kHz |"));
            Assert.That(juno, Does.Contain("**Filter cutoff** (`filter.cutoff`)"));
            Assert.That(juno, Does.Contain("- Plays the written pitch: A4 is 440 Hz."));
            Assert.That(juno, Does.Contain("ExternalMachines/source/thepedal/pedal-juno106/PedalJuno106.cs"));
            var infector = MachineManual.Build(Curated()["FSM Infector"], null);
            Assert.That(infector, Does.Contain("Plays 12 semitones low: `notes_write` adds +12 semitones"));
            var delay = MachineManual.Build(Curated()["Jeskola Delay"], null);
            Assert.That(delay, Does.Contain("## Safe ranges"));
            Assert.That(delay, Does.Contain("{\"param\": \"Length\", \"track\": 0, \"value\":"), "track parameters take their track");
        }

        /// <summary>A numbered group shown once in a family heading ("### Operator 1 … Operator 4": the manual folds groups that repeat).</summary>
        static bool Folded(string text, string group)
        {
            var m = Regex.Match(group, @"^(.*?)(\d+)$");
            if (!m.Success) return false;
            int n = int.Parse(m.Groups[2].Value);
            foreach (Match f in Regex.Matches(text, $@"^### {Regex.Escape(m.Groups[1].Value)}(\d+) … {Regex.Escape(m.Groups[1].Value)}(\d+)$", RegexOptions.Multiline))
                if (int.Parse(f.Groups[1].Value) < n && n <= int.Parse(f.Groups[2].Value)) return true;
            return false;
        }

        [Test]
        public void PlainNamesAndRolesSayWhatTheParameterIs()
        {
            var all = Curated();
            // A role per parameter where one fits; the cutoffs, envelopes, mixes and drives agents reach for have theirs.
            var roles = all.Values.SelectMany(m => m.Parameters.Concat(m.TrackParameters)).Where(p => p.Role.Length > 0).ToList();
            Assert.That(roles.Count, Is.GreaterThan(350));
            foreach (var (gear, name, role) in new[]
            {
                ("Pedal Juno106", "VCF Freq", "filter.cutoff"), ("Pedal SH101", "Release", "env.release"), ("Pedal Plate", "Mix", "mix.wet"),
                ("Jeskola Distortion", "Amount", "drive"), ("Pedal Comp", "Threshold", "dyn.threshold"), ("Jeskola Reverb", "Rev Time", "reverb.decay"),
            })
            {
                var p = all[gear].Find(name) ?? all[gear].FindTrack(name);
                Assert.That(p?.Role, Is.EqualTo(role), $"{gear} {name}");
            }
            // Machine texts that are wrong are overridden (Pedal Comp's Attack reads "0.1 ms" for 10 ms), and the manual says the real value.
            var attack = all["Pedal Comp"].Find("Attack")!;
            Assert.That(attack.OwnTexts, Is.False);
            Assert.That(MachineManual.Format(attack, 10), Is.EqualTo("10 ms"));
            Assert.That(MachineManual.Format(all["Pedal Juno106"].Find("Env A")!, 127), Is.EqualTo("4 s"));
            Assert.That(MachineManual.Format(all["Pedal EQ"].Find("LM Freq")!, 6), Is.EqualTo("400 Hz"), "Pedal EQ's frequency steps, from its source");
        }
    }
}
