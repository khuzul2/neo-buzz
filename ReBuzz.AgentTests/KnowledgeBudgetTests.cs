// Fork (docs/specs/agent-knowledge.md, K0): what an agent reads is measured and held to a budget, so the ladder of progressive
// disclosure stays a ladder: server instructions, then a guide, then a machine page, then the full manual on request. Size is
// counted in tokens, estimated as characters / 4. The machine page template (§4) is a failure for every page except those of the
// machines in KnowledgePending (K1 still to do: a warning); docs/agent-coverage.md is generated (tools/agent-coverage.py) and a stale committed copy fails here.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ReBuzz.Agent.Guides;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.AgentTests
{
    [TestFixture]
    public class KnowledgeBudgetTests
    {
        public const int InstructionsBudget = 1000, GuideBudget = 4000, PageBudget = 2500;

        static int Tokens(string text) => (text.Length + 3) / 4;

        [Test]
        public void TheServerInstructionsAreWithinBudget()
        {
            int tokens = Tokens(GuideLibrary.ServerInstructions);
            TestContext.Out.WriteLine($"server instructions: {tokens} tokens (budget {InstructionsBudget})");
            Assert.That(tokens, Is.LessThanOrEqualTo(InstructionsBudget));
        }

        [Test]
        public void EveryGuideIsWithinBudget()
        {
            var over = new List<string>();
            foreach (var topic in GuideLibrary.TopicNames)
            {
                int tokens = Tokens(GuideLibrary.Get(topic)!);
                TestContext.Out.WriteLine($"guide {topic}: {tokens} tokens (budget {GuideBudget})");
                if (tokens > GuideBudget) over.Add($"{topic}: {tokens} tokens");
            }
            Assert.That(over, Is.Empty, $"over {GuideBudget} tokens: split the guide by topic (every fact stays, in a guide of its own)\n" + string.Join("\n", over));
        }

        [Test]
        public void EveryMachinePageIsWithinBudget()
        {
            var over = new List<string>();
            foreach (var gear in MachineManual.CuratedGear)
            {
                int tokens = Tokens(MachineManual.Curated(gear)!);
                TestContext.Out.WriteLine($"page {gear}: {tokens} tokens (budget {PageBudget})");
                if (tokens > PageBudget) over.Add($"{gear}: {tokens} tokens");
            }
            Assert.That(over, Is.Empty, $"over {PageBudget} tokens: move detail to a guide or the generated manual\n" + string.Join("\n", over));
        }

        [Test]
        public void TheGeneratedResourcesAreMeasured()
        {
            // Not budgeted (they grow with the machines and tools) but reported, so a jump shows.
            var manifests = CatalogTests.AllManifests().Values.ToList();
            int catalog = Tokens(MachineCatalog.Build(MachineCatalog.Items(manifests)));
            var tools = new ToolRegistry();
            GuideLibrary.Register(tools);
            MachineCatalog.Register(tools, () => manifests);
            int reference = Tokens(ToolReference.Build(tools.All));
            TestContext.Out.WriteLine($"catalog: {catalog} tokens for {manifests.Count} machines; tool reference: {reference} tokens for {tools.All.Count} tools here");
            Assert.That(catalog, Is.LessThan(8000));
        }

        // ---- the machine page template (§4) ---------------------------------------------------------

        static readonly string[] Sections = { "How it sounds", "Use it for", "Key controls", "Starting points", "Pitfalls", "Pairs well with", "Successor", "Checklist" };
        static readonly string[] OptionalSections = { "Successor" };

        /// <summary>Whether a checklist item names a tool (a call such as render(...) or a backticked tool name) that the guides know.</summary>
        static bool NamesATool(string item) =>
            Regex.Matches(item, @"(?<![\w./-])([a-z][a-z0-9_]*)\(|`([a-z][a-z0-9_]*)`")
                .Any(m => GuideTests.KnownTools.ContainsKey(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));

        /// <summary>What is wrong with a page against the template: missing or misordered sections, a Key controls list outside 5 to 8, a Checklist with no items.</summary>
        internal static List<string> TemplateProblems(string page, int controlCount = int.MaxValue)
        {
            var problems = new List<string>();
            var headings = Regex.Matches(page, @"(?m)^## (.+?)\s*$").Select(m => m.Groups[1].Value).ToList();
            int last = -1;
            foreach (var s in Sections)
            {
                int at = headings.FindIndex(h => string.Equals(h, s, StringComparison.OrdinalIgnoreCase));
                if (at < 0) { if (!OptionalSections.Contains(s)) problems.Add($"no \"## {s}\" section"); continue; }
                if (at < last) problems.Add($"\"## {s}\" comes out of order");
                last = Math.Max(last, at);
            }
            string Section(string name)
            {
                var m = Regex.Match(page, @"(?ms)^## " + Regex.Escape(name) + @"\s*$(.*?)(?=^## |\z)", RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value : "";
            }
            string controls = Section("Key controls");
            if (controls.Length > 0)
            {
                int rows = Regex.Matches(controls, @"(?m)^\s*(?:-|\*|\d+\.)\s+\S").Count;
                if (rows == 0) rows = Math.Max(0, Regex.Matches(controls, @"(?m)^\|.+\|\s*$").Count - 2);
                // 5 to 8; a machine with fewer controls than that lists them all (Pedal Gain has three parameters).
                int least = Math.Min(5, controlCount);
                if (rows < least || rows > 8) problems.Add($"\"Key controls\" lists {rows} controls ({(least < 5 ? $"all {least} of the machine's, at most 8" : "5 to 8")})");
            }
            string checklist = Section("Checklist");
            if (checklist.Length > 0)
            {
                var items = Regex.Matches(checklist, @"(?m)^- \[ \] (.*)$").Select(m => m.Groups[1].Value).ToList();
                if (items.Count == 0) problems.Add("\"Checklist\" has no \"- [ ]\" items");
                foreach (var item in items)
                    if (!NamesATool(item)) problems.Add($"Checklist item names no tool: \"{(item.Length > 50 ? item[..50] + "..." : item)}\"");
            }
            return problems;
        }

        [Test]
        public void ThePagesAgainstTheTemplate()
        {
            var failures = new List<string>();
            var pending = new List<string>();
            int conforming = 0;
            foreach (var gear in MachineManual.CuratedGear)
            {
                var manifest = CuratedManifestTests.Profiled().GetValueOrDefault(gear);
                var problems = TemplateProblems(MachineManual.Curated(gear)!, manifest == null ? int.MaxValue : manifest.Parameters.Count + manifest.TrackParameters.Count);
                if (problems.Count == 0) conforming++;
                else (KnowledgePending.Gear.Contains(gear) ? pending : failures).Add($"{gear}: {string.Join("; ", problems)}");
            }
            TestContext.Out.WriteLine($"{conforming} of {MachineManual.CuratedGear.Count} pages follow the template");
            Assert.That(failures, Is.Empty, "pages that don't follow the page template (agent-knowledge.md §4):\n" + string.Join("\n", failures));
            Assert.That(KnowledgePending.Gear.Where(g => !MachineManual.CuratedGear.Contains(g)), Is.Empty, "KnowledgePending names a machine with no page");
            if (pending.Count > 0)
                Assert.Warn($"{pending.Count} pages still to bring to the template (K1; KnowledgePending):\n" + string.Join("\n", pending));
        }

        [Test]
        public void TheTemplateCheckFindsWhatIsWrong()
        {
            string good = "# Neo X\n\n## How it sounds\nSmooth.\n\n## Use it for\nBuses.\n\n## Key controls\n- A: 1 dB\n- B: 2 dB\n- C: 3 dB\n- D: 4 dB\n- E: 5 dB\n\n## Starting points\n- Glue: A 1 dB\n\n## Pitfalls\nLevel.\n\n## Pairs well with\nNeo Verb.\n\n## Successor\nPedal X.\n\n## Checklist\n- [ ] It sounds: `machine_readings(machine=\"X\")` reads under 0 dB.\n";
            Assert.That(TemplateProblems(good), Is.Empty);
            Assert.That(TemplateProblems(good.Replace("## Successor\nPedal X.\n\n", "")), Is.Empty, "Successor is optional");
            Assert.That(TemplateProblems(good.Replace("## Pitfalls\nLevel.\n\n", "")), Has.Some.Contain("no \"## Pitfalls\""));
            Assert.That(TemplateProblems(good.Replace("- E: 5 dB\n", "")), Has.Some.Contain("lists 4 controls"));
            Assert.That(TemplateProblems(good.Replace("- [ ] It sounds", "It sounds")), Has.Some.Contain("no \"- [ ]\""));
            Assert.That(TemplateProblems(good.Replace("`machine_readings(machine=\"X\")`", "the meter")), Has.Some.Contain("names no tool"));
            Assert.That(TemplateProblems("## Use it for\nBuses.\n\n## How it sounds\nSmooth.\n"), Has.Some.Contain("out of order"));
            string table = good.Replace("- A: 1 dB\n- B: 2 dB\n- C: 3 dB\n- D: 4 dB\n- E: 5 dB\n", "| Param | Value |\n|---|---|\n| A | 1 |\n| B | 2 |\n| C | 3 |\n| D | 4 |\n| E | 5 |\n");
            Assert.That(TemplateProblems(table), Is.Empty, "a table of controls counts its rows");
            Assert.That(TemplateProblems(good.Replace("- D: 4 dB\n- E: 5 dB\n", ""), 3), Is.Empty, "a machine with three controls lists its three");
            Assert.That(TemplateProblems(good.Replace("- D: 4 dB\n- E: 5 dB\n", ""), 5), Has.Some.Contain("lists 3 controls"), "but a machine with five lists five");
        }

        // ---- the coverage report --------------------------------------------------------------------

        [Test]
        public void TheCommittedCoverageReportIsCurrent()
        {
            string root = CuratedManifestTests.Root();
            string? python = null;
            foreach (var candidate in new[] { "python3", "python" })
            {
                try
                {
                    using var probe = Process.Start(new ProcessStartInfo(candidate, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                    probe!.WaitForExit(10000);
                    if (probe.ExitCode == 0) { python = candidate; break; }
                }
                catch (Exception) { }
            }
            if (python == null) { Assert.Inconclusive("python is not available to run tools/agent-coverage.py --check"); return; }
            var info = new ProcessStartInfo(python, "\"" + Path.Combine(root, "tools", "agent-coverage.py") + "\" --check")
            {
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            using var p = Process.Start(info)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.That(p.ExitCode, Is.EqualTo(0), output + "\nrun: python3 tools/agent-coverage.py");
        }

        [Test]
        public void TheCoverageReportNamesEveryMachineAndTool()
        {
            string report = File.ReadAllText(Path.Combine(CuratedManifestTests.Root(), "docs", "agent-coverage.md"));
            foreach (var gear in CatalogTests.AllManifests().Keys) Assert.That(report, Does.Contain($"| {gear} |"), gear);
            foreach (var tool in GuideTests.KnownTools.Keys) Assert.That(report, Does.Contain($"| {tool} |"), tool);
            var listed = Regex.Matches(report, @"(?m)^\| ([a-z][a-z0-9_]*) \| \d+ \| \d+ \| (?:yes|no) \|$").Select(m => m.Groups[1].Value).ToList();
            Assert.That(listed, Is.EquivalentTo(GuideTests.KnownTools.Keys), "the tools the report finds in the source are the tools the guide checks know (GuideTests.KnownTools)");
        }
    }
}
