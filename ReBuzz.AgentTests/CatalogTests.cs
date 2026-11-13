// Fork (docs/specs/agent-knowledge.md, K0): the machine catalogue. Every machine in the package has a category, tags from the
// vocabulary and a use_for line in its manifest; rebuzz://catalog lists them by category; machine_find ranks machines for an
// intent; rebuzz://reference/tools is generated from the tools; and the three are the same single source the generators write.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReBuzz.Agent.Guides;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;

namespace ReBuzz.AgentTests
{
    [TestFixture]
    public class CatalogTests
    {
        static Dictionary<string, MachineManifest>? all;

        /// <summary>Every machine's manifest: the Neo machines' generated ones beside their sources, the rest in Machines/Manifests.</summary>
        internal static Dictionary<string, MachineManifest> AllManifests() =>
            all ??= CuratedManifestTests.Profiled().Concat(CuratedManifestTests.Generated()).ToDictionary(kv => kv.Key, kv => kv.Value);

        static McpServer Server(IEnumerable<MachineManifest>? manifests = null)
        {
            var list = (manifests ?? AllManifests().Values).ToList();
            var tools = new ToolRegistry();
            GuideLibrary.Register(tools);
            MachineCatalog.Register(tools, () => list);
            return new McpServer(GuideLibrary.AppServer("test"), tools, GuideLibrary.Prompts(),
                GuideLibrary.Resources().Concat(new[] { MachineCatalog.Resource(() => list), ToolReference.Resource(tools) }));
        }

        static Task<JsonObject?> Send(McpServer s, McpSession session, JsonObject m) => s.HandleAsync(m, session, _ => Task.CompletedTask, CancellationToken.None);

        static List<string> Find(JsonObject args, out JsonObject data)
        {
            var server = Server();
            var (error, text, structured) = TestTools.CallTool(server, "machine_find", args);
            Assert.That(error, Is.False, text);
            var problems = new List<string>();
            Schema.Validate(server.Tools.Find("machine_find")!.Output!, structured, "", problems);
            Assert.That(problems, Is.Empty, "the result matches the declared output schema");
            data = structured;
            return structured["matches"]!.AsArray().Select(m => m!["machine"]!.GetValue<string>()).ToList();
        }

        static List<string> Find(string intent, int limit = 8) => Find(new JsonObject { ["intent"] = intent, ["limit"] = limit }, out _);

        // ---- the fields -----------------------------------------------------------------------------

        [Test]
        public void EveryMachineInTheProfileHasACategoryTagsAndUseFor()
        {
            var profile = JsonNode.Parse(File.ReadAllText(Path.Combine(CuratedManifestTests.Root(), "Machines", "Manifests", "machine-profile.json")))!["machines"]!.AsObject();
            var manifests = AllManifests();
            var problems = new List<string>();
            foreach (var gear in profile.Select(kv => kv.Key))
            {
                if (!manifests.TryGetValue(gear, out var m)) { problems.Add($"{gear}: no manifest"); continue; }
                if (!MachineCatalog.Categories.Contains(m.Category)) problems.Add($"{gear}: category \"{m.Category}\" is not one of {string.Join(", ", MachineCatalog.Categories)}");
                if (m.Tags.Count == 0) problems.Add($"{gear}: no tags");
                if (m.Tags.Count > 8) problems.Add($"{gear}: {m.Tags.Count} tags (a handful says more than a list)");
                foreach (var t in m.Tags.Where(t => !MachineCatalog.Tags.ContainsKey(t))) problems.Add($"{gear}: tag \"{t}\" is not in the vocabulary");
                if (m.Tags.Distinct().Count() != m.Tags.Count) problems.Add($"{gear}: a tag twice");
                if (string.IsNullOrWhiteSpace(m.UseFor) || m.UseFor.Contains('\n') || m.UseFor.Length > 200) problems.Add($"{gear}: use_for is empty, more than one line or over 200 characters");
                var file = MachineCatalog.Entry(gear);
                if (file == null) problems.Add($"{gear}: not in machine-catalog.json");
                else if (file.Value.Category != m.Category || !file.Value.Tags.SequenceEqual(m.Tags) || file.Value.UseFor != m.UseFor)
                    problems.Add($"{gear}: differs from machine-catalog.json (python3 tools/machine_catalog.py --write)");
            }
            Assert.That(profile.Count, Is.EqualTo(MachineCatalog.GearInCatalogFile.Count()), "the catalogue names exactly the machines in the profile");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
            TestContext.Out.WriteLine(string.Join(", ", manifests.Values.GroupBy(m => m.Category).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        }

        [Test]
        public void TheVocabularyIsSmallAndEveryTagIsUsed()
        {
            var used = AllManifests().Values.SelectMany(m => m.Tags).ToHashSet();
            Assert.That(MachineCatalog.Tags.Keys.Where(t => !used.Contains(t)), Is.Empty, "a tag no machine has");
            Assert.That(MachineCatalog.Tags.Count, Is.LessThanOrEqualTo(80));
            Assert.That(MachineCatalog.Categories, Is.EqualTo(new[] { "Synths", "Drums", "Samplers", "Dynamics", "EQ & Filter", "Delay & Reverb", "Modulation", "Distortion", "Utility", "Control" }));
            foreach (var t in MachineCatalog.Tags.Keys) Assert.That(t, Does.Match("^[a-z][a-z-]*$"), "tags are lower-case words");
        }

        [Test]
        public void TheLexiconOnlyNamesTagsAndCategoriesThatExist()
        {
            // Every plain word machine_find understands leads to a real tag: asking for it finds something.
            foreach (var word in new[] { "glue", "harsh", "wobble", "slapback", "tempo", "dub", "crunch", "lofi", "ambient", "voice", "spectrum", "resample", "cohesive", "bleed" })
                Assert.That(Find(word, 3), Is.Not.Empty, word);
        }

        // ---- the catalog resource -------------------------------------------------------------------

        [Test]
        public async Task TheCatalogResourceListsEveryMachineByCategory()
        {
            var server = Server();
            var listed = await Send(server, new McpSession(), TestTools.Request(1, "resources/list"));
            Assert.That(listed!["result"]!["resources"]!.AsArray().Select(r => r!["uri"]!.GetValue<string>()), Does.Contain("rebuzz://catalog").And.Contain("rebuzz://reference/tools"));
            var read = await Send(server, new McpSession(), TestTools.Request(2, "resources/read", new JsonObject { ["uri"] = "rebuzz://catalog" }));
            string text = read!["result"]!["contents"]![0]!["text"]!.GetValue<string>();
            foreach (var m in AllManifests().Values) Assert.That(text, Does.Contain($"**{m.Gear}** ({m.Quality}"), m.Gear);
            int last = -1;
            foreach (var c in MachineCatalog.Categories)
            {
                int at = text.IndexOf("\n## " + c + " (", StringComparison.Ordinal);
                Assert.That(at, Is.GreaterThan(last), $"the category {c} comes in the browser's order");
                last = at;
            }
            Assert.That(text, Does.Contain(MachineCatalog.Entry("Neo De-esser")!.Value.UseFor));
            Assert.That(text, Does.Contain($"{AllManifests().Count} machines in 10 categories"));
            TestContext.Out.WriteLine($"catalog {text.Length / 4} tokens");
        }

        // ---- machine_find ---------------------------------------------------------------------------

        [Test]
        public void TypicalIntentsFindTheMachinesAPersonWouldPick()
        {
            var warmBass = Find("warm bass synth");
            Assert.That(warmBass[0], Is.EqualTo("Neo Poly"));
            Assert.That(warmBass.Take(5), Has.All.Matches<string>(g => AllManifests()[g].Category == "Synths"), "synths first");

            var harsh = Find("tame harsh vocals");
            Assert.That(harsh[0], Is.EqualTo("Neo De-esser"));
            Assert.That(harsh.Take(5), Does.Contain("Neo EQ").Or.Contain("Neo Comp"));

            var glue = Find("glue the mix");
            Assert.That(glue.Take(3), Does.Contain("Neo Comp"));
            Assert.That(glue.Take(6), Does.Contain("Neo Multiband"));

            Assert.That(Find("tempo-synced delay")[0], Is.EqualTo("Neo Delay"));
            Assert.That(Find("big plate reverb for vocals").Take(3), Does.Contain("Pedal Plate").Or.Contain("Neo Verb"));
            Assert.That(Find("punchy kick drum").Take(4), Does.Contain("Neo Drums"));
            Assert.That(Find("limit the master for loudness")[0], Is.EqualTo("Neo Limiter"));
            Assert.That(Find("widen the stereo image").Take(3), Does.Contain("Neo Imager"));
            Assert.That(Find("acid bass line").Take(3), Does.Contain("Oomek Aggressor 303").Or.Contain("Jeskola Bass 3"));
            Assert.That(Find("see the spectrum").Take(3), Does.Contain("Neo Analyzer"));
        }

        [Test]
        public void FiltersCutTheListAndCombineWithAnIntent()
        {
            var drums = Find(new JsonObject { ["category"] = "Drums" }, out _);
            Assert.That(drums, Is.EquivalentTo(AllManifests().Values.Where(m => m.Category == "Drums").Select(m => m.Gear)));
            var tagged = Find(new JsonObject { ["tag"] = "reverb" }, out _);
            Assert.That(tagged, Has.All.Matches<string>(g => AllManifests()[g].Tags.Contains("reverb")));
            var effects = Find(new JsonObject { ["intent"] = "warm", ["kind"] = "effect", ["limit"] = 30 }, out _);
            Assert.That(effects, Has.All.Matches<string>(g => AllManifests()[g].Kind == "effect"));
            Assert.That(effects, Does.Contain("Neo Tape"));
            var cutoff = Find(new JsonObject { ["role"] = "filter.cutoff", ["limit"] = 30 }, out var data);
            Assert.That(cutoff, Does.Contain("Pedal Filter").And.Contain("FSM Infector"), "the machines whose manifests give roles");
            Assert.That(data["matches"]![0]!["reason"]!.GetValue<string>(), Does.Contain("filter.cutoff"));
            Assert.That(Find(new JsonObject { ["category"] = "EQ & Filter", ["intent"] = "filter" }, out _), Has.All.Matches<string>(g => AllManifests()[g].Category == "EQ & Filter"));
        }

        [Test]
        public void EachMatchSaysWhyAndPointsAtItsManual()
        {
            Find(new JsonObject { ["intent"] = "warm bass synth", ["limit"] = 3 }, out var data);
            var first = data["matches"]![0]!.AsObject();
            Assert.That(first["reason"]!.GetValue<string>(), Does.Contain("warm").And.Contain("bass").And.Contain("Synths"));
            Assert.That(first["manual"]!.GetValue<string>(), Is.EqualTo("rebuzz://machines/neo-poly/manual"));
            Assert.That(first["page"]!.GetValue<bool>(), Is.True);
            Assert.That(data["matches"]!.AsArray().Count, Is.EqualTo(3));
            Assert.That(data["considered"]!.GetValue<int>(), Is.EqualTo(AllManifests().Count));
        }

        [Test]
        public void MachineFindIsReadOnlyHasASchemaAndAsksForSomething()
        {
            var server = Server();
            var tool = server.Tools.Find("machine_find")!;
            Assert.That(tool.Annotations.ReadOnly, Is.True);
            Assert.That(tool.Annotations.Destructive, Is.False);
            Assert.That(tool.Output, Is.Not.Null);
            Assert.That(tool.Description, Does.Contain("Example:"));
            Assert.That(tool.Input["properties"]!["category"]!["enum"]!.AsArray().Count, Is.EqualTo(10));
            Assert.That(tool.Input["properties"]!["tag"]!["enum"]!.AsArray().Count, Is.EqualTo(MachineCatalog.Tags.Count));
            var (error, text, _) = TestTools.CallTool(server, "machine_find", new JsonObject());
            Assert.That(error, Is.True);
            Assert.That(text, Does.Contain("Give intent"));
            var (bad, badText, _) = TestTools.CallTool(server, "machine_find", new JsonObject { ["category"] = "Lasers" });
            Assert.That(bad, Is.True, badText);
            Assert.That(Find("zzzz qqqq"), Is.Empty);
        }

        // ---- the tool reference ---------------------------------------------------------------------

        [Test]
        public async Task TheToolReferenceIsGeneratedFromTheRegisteredTools()
        {
            var server = Server();
            var read = await Send(server, new McpSession(), TestTools.Request(1, "resources/read", new JsonObject { ["uri"] = ToolReference.Uri }));
            string text = read!["result"]!["contents"]![0]!["text"]!.GetValue<string>();
            foreach (var tool in server.Tools.All)
            {
                Assert.That(text, Does.Contain($"**{tool.Name}** ("), tool.Name);
                foreach (var (name, _) in tool.Input["properties"]!.AsObject())
                    Assert.That(text, Does.Contain($"  - {name}"), $"{tool.Name}.{name}");
            }
            Assert.That(text, Does.Contain($"{server.Tools.All.Count} tools in"));
            Assert.That(text, Does.Contain("**machine_find** (RI)"));
            Assert.That(text, Does.Contain("## Machines and wiring"));
            // A tool added later shows up with no edit to the reference.
            server.Tools.Add(new ToolDef { Name = "zzz_new", Title = "New", Description = "Does a new thing. More.", Input = Schema.Object(null, Schema.Opt("depth_db", Schema.Number("How deep, in dB", -60, 0))), Annotations = ToolAnnotations.Delete, Handler = _ => Task.FromResult(ToolResult.Ok("")) });
            var again = (await Send(server, new McpSession(), TestTools.Request(2, "resources/read", new JsonObject { ["uri"] = ToolReference.Uri })))!["result"]!["contents"]![0]!["text"]!.GetValue<string>();
            Assert.That(again, Does.Contain("**zzz_new** (WD) Does a new thing."));
            Assert.That(again, Does.Contain("depth_db: number -60…0 — How deep, in dB"));
            Assert.That(again, Does.Contain("## Other"));
        }

        [Test]
        public void EveryToolTheGuidesKnowBelongsToAnArea()
        {
            var other = GuideTests.KnownTools.Keys.Where(t => ToolReference.AreaOf(t) == "Other").ToList();
            Assert.That(other, Is.Empty, "name these in ToolReference's areas: " + string.Join(", ", other));
        }

        [Test]
        public void TheInstructionsAndTheWorkflowGuideMentionTheCatalogAndTheToolReference()
        {
            Assert.That(GuideLibrary.ServerInstructions, Does.Contain("machine_find"));
            Assert.That(GuideLibrary.ServerInstructions, Does.Contain("rebuzz://catalog"));
            Assert.That(GuideLibrary.ServerInstructions, Does.Contain("rebuzz://reference/tools"));
            string workflow = GuideLibrary.Get("workflow")!;
            Assert.That(workflow, Does.Contain("rebuzz://reference/tools"));
            Assert.That(workflow, Does.Contain("machine_find"));
            Assert.That(workflow, Does.Contain("rebuzz://catalog"));
        }
    }
}
