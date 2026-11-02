using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Rig = ReBuzzTests.AgentServerTests.Rig;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/mcp-server.md §6, A5): the guides as ReBuzz serves them. Prompts,
    /// one manual resource per installed gear with a manifest (its parameters and factory presets),
    /// and guide(machine=…) answering with the same manual.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class AgentA5Tests
    {
        static JsonObject Request(Rig rig, string method, JsonObject p = null)
        {
            var r = rig.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 500, ["method"] = method, ["params"] = p ?? new JsonObject() });
            Assert.That(r["error"], Is.Null, $"{method}: {r["error"]?.ToJsonString()}");
            return r["result"]!.AsObject();
        }

        [Test]
        public void PromptsAndMachineManualsAreServed()
        {
            using var rig = new Rig(NeoMachineTests.Poly);

            var prompts = Request(rig, "prompts/list")["prompts"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList();
            Assert.That(prompts, Is.SupersetOf(new[] { "compose", "design-sound", "mix", "master", "critique" }));
            var compose = Request(rig, "prompts/get", new JsonObject { ["name"] = "compose", ["arguments"] = new JsonObject { ["genre"] = "synthwave" } });
            Assert.That(compose["messages"]!.AsArray().Count, Is.GreaterThan(0));

            var resources = Request(rig, "resources/list")["resources"]!.AsArray().Select(r => r!["uri"]!.GetValue<string>()).ToList();
            Assert.That(resources, Does.Contain("rebuzz://machines/neo-poly/manual"), "the installed Neo Poly has its manual");
            Assert.That(resources, Does.Contain("rebuzz://guides/workflow"));
            var manual = Request(rig, "resources/read", new JsonObject { ["uri"] = "rebuzz://machines/neo-poly/manual" })["contents"]![0]!["text"]!.GetValue<string>();
            Assert.That(manual, Does.Contain("F1 cutoff"), "the parameter table, from the manifest");
            Assert.That(manual, Does.Contain("Pad — Warm Strings"), "the factory presets, from the .prs.xml beside the DLL");

            var (error, text, data) = rig.Call("guide", new JsonObject { ["machine"] = "Neo Poly" });
            Assert.That(error, Is.False, text);
            Assert.That(data["markdown"]!.GetValue<string>(), Is.EqualTo(manual), "guide(machine) and the resource are the same manual");
        }
    }
}
