// Fork (docs/specs/mcp-server.md §12.2, U3): every machine in the package has its manifest generated beside its source, and a manual
// (rebuzz://machines/<slug>/manual) built from it. The shared helpers of the knowledge tests live here.
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


        [Test]
        public void EveryMachineInThePackageHasAManifest()
        {
            // The package's machines (Machines/Manifests/machine-profile.json, keyed by gear): each has the manifest generated beside its source.
            var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "Machines", "Manifests", "machine-profile.json")))!["machines"]!.AsObject();
            var generated = Generated();
            var missing = summary.Select(kv => kv.Key).Where(g => !generated.ContainsKey(g)).ToList();
            Assert.That(missing, Is.Empty, "machines in the package without a manifest");
            Assert.That(generated.Keys.Except(summary.Select(kv => kv.Key)), Is.Empty, "manifests of machines the package doesn't have");
            Assert.That(Profiled(), Is.Empty, "every manifest is generated beside its machine: Machines/Manifests holds only the profile and the catalogue");
            Assert.That(summary.Count, Is.GreaterThanOrEqualTo(51));
            Assert.That(generated.Values.All(m => m.Quality == "generated"), Is.True);
        }

        [Test]
        public void EveryManifestHasAManual()
        {
            var problems = new List<string>();
            foreach (var m in Generated().Values)
            {
                string text = MachineManual.Build(m, null);
                if (!text.StartsWith($"# {m.Gear} manual\n")) problems.Add($"{m.Gear}: title");
                if (m.Summary.Length > 0 && !text.Contains(m.Summary)) problems.Add($"{m.Gear}: summary");
                if (!text.Contains("## Parameters")) problems.Add($"{m.Gear}: parameters");
                var resource = MachineManual.Resource(m);
                if (resource.Uri != MachineManual.UriFor(m.Gear) || resource.Read() != text) problems.Add($"{m.Gear}: resource");
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }
    }
}
