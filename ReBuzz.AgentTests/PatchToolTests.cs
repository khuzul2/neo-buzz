using System.Text.Json.Nodes;
using ReBuzz.Agent;
using ReBuzz.Agent.App;
using ReBuzz.Agent.Guides;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.AgentTests
{
    /// <summary>
    /// The patch_* tools (docs/specs/neo-patch.md §4.6, NP3), over a fake of what the engine gives them: schemas, annotations and examples, how each call reaches the
    /// machine (the operation, the arguments without the machine's name, whether it changes the song), one undo step for each editing call and none for a read or a
    /// refusal, and the machine's errors reaching the agent in its own words. What the machine does to a patch is tested in NeoBuzz.MachineTests/NeoPatchAgentApiTests.cs.
    /// </summary>
    [TestFixture]
    public class PatchToolTests
    {
        /// <summary>A Neo Patch machine named Lead with a patch kept as a list of node names, and an undo stack with one step for each editing call.</summary>
        sealed class FakePatch : IPatchServices
        {
            public readonly List<(string Tool, bool Changes, string? Machine, string Op, JsonObject Args)> Calls = new();
            public List<string> Nodes = new() { "Keys 1" };
            public readonly Stack<List<string>> Undo = new();
            public readonly List<(string? Machine, string Name, string? Comment, bool Overwrite)> Saved = new();

            public Task<JsonObject> Run(string tool, bool changes, string? machine, string op, JsonObject args, CancellationToken ct)
            {
                Calls.Add((tool, changes, machine, op, args));
                if (machine != null && machine != "Lead") throw new ToolError($"{machine} is not a Neo Patch machine (it is Neo Poly). The Neo Patch machines in this song: Lead.");
                int steps = Undo.Count;
                if (changes) Undo.Push(new List<string>(Nodes));
                try
                {
                    var r = Do(op, args);
                    r["machine"] = "Lead";
                    return Task.FromResult(r);
                }
                catch (ToolError)
                {
                    while (Undo.Count > steps) Undo.Pop();   // a refusal changes nothing and makes no step
                    throw;
                }
            }

            JsonObject Do(string op, JsonObject a)
            {
                switch (op)
                {
                    case "describe":
                        return new JsonObject
                        {
                            ["name"] = "Init", ["node_count"] = Nodes.Count, ["node_cap"] = 128, ["wire_count"] = 0,
                            ["nodes"] = new JsonArray(Nodes.Select((n, i) => (JsonNode)new JsonObject { ["id"] = i + 1, ["kind"] = n.Split(' ')[0] }).ToArray()),
                            ["wires"] = new JsonArray(), ["macros"] = new JsonArray(), ["cpu"] = new JsonObject { ["percent"] = 0.4 }, ["unheard"] = new JsonArray(),
                        };
                    case "add_node":
                        if (a["kind"]!.ToString() == "Oscilator") throw new ToolError("There is no node kind \"Oscilator\". Did you mean Osc? The kinds are Keys, Osc, …");
                        Nodes.Add(a["kind"] + " " + (Nodes.Count + 1));
                        return new JsonObject { ["id"] = Nodes.Count, ["kind"] = a["kind"]!.ToString(), ["controls"] = new JsonArray(), ["node_count"] = Nodes.Count };
                    case "connect":
                        if ((int)a["from_node"]! == (int)a["to_node"]!) throw new ToolError("Osc 2 cannot feed itself. Send the wire through a Feedback node, which hands the signal back one block later.");
                        return new JsonObject { ["connected"] = $"node {a["from_node"]} to node {a["to_node"]}", ["amount"] = a["amount"]?.DeepClone() ?? 1, ["wire_count"] = 1 };
                    case "disconnect":
                        return new JsonObject { ["removed"] = 1, ["wire_count"] = 0 };
                    case "set":
                        return new JsonObject { ["set"] = new JsonArray(new JsonObject { ["node"] = 2, ["kind"] = "Filter", ["control"] = "Cutoff", ["value"] = 800, ["shown"] = "800 Hz", ["was"] = "2 kHz" }) };
                    case "remove":
                        Nodes.RemoveAt(Nodes.Count - 1);
                        return new JsonObject { ["removed"] = new JsonArray("Osc 2"), ["wires_removed"] = 1, ["node_count"] = Nodes.Count };
                    case "macro_map":
                        return new JsonObject { ["macro"] = a["macro"]!.DeepClone(), ["mapped"] = "Filter 4 · Cutoff", ["label"] = a["label"]?.ToString() ?? "Cutoff" };
                    case "load_template":
                        Nodes = new List<string> { "Keys 1", "Osc 2", "Envelope 3", "VCA 4", "Out 5" };
                        return new JsonObject { ["template"] = a["template"]!.DeepClone(), ["title"] = "Start", ["summary"] = "A basic voice.", ["node_count"] = 5, ["wire_count"] = 2, ["note"] = "Three wires to make." };
                    default:
                        throw new ToolError("no such operation " + op);
                }
            }

            public Task<JsonObject> SavePreset(string? machine, string name, string? comment, bool overwrite, CancellationToken ct)
            {
                Saved.Add((machine, name, comment, overwrite));
                if (name == "Taken" && !overwrite) throw new ToolError("Neo Patch already has a preset \"Taken\". Pass overwrite=true to replace it, or choose another name.");
                return Task.FromResult(new JsonObject { ["machine"] = "Lead", ["preset"] = name, ["file"] = "Neo Patch.prs.xml" });
            }

            public void UndoStep() { Nodes = Undo.Pop(); }
        }

        static (McpServer Server, FakePatch Patch) Make()
        {
            var patch = new FakePatch();
            var tools = new ToolRegistry();
            PatchTools.Register(tools, patch);
            return (new McpServer(new ServerInfo { Name = "t", Title = "t", Version = "1" }, tools), patch);
        }

        static (bool Error, string Text, JsonObject Data) Call(McpServer s, string tool, JsonObject? args = null)
        {
            var r = TestTools.CallTool(s, tool, args);
            var def = s.Tools.Find(tool)!;
            if (!r.Error && def.Output != null)
            {
                var problems = new List<string>();
                Schema.Validate(def.Output, r.Data, "", problems);
                Assert.That(problems, Is.Empty, $"{tool}'s result doesn't match its output schema");
            }
            return r;
        }

        static JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;

        static readonly string[] ToolNames =
        {
            "patch_describe", "patch_add_node", "patch_connect", "patch_disconnect", "patch_set", "patch_remove", "patch_macro_map", "patch_load_template", "patch_save_preset",
        };

        // ---- schemas -----------------------------------------------------------------------------

        [Test]
        public void EveryToolHasADescriptionSchemasAnAnnotationAndAnExample()
        {
            var (server, _) = Make();
            Assert.That(server.Tools.All.Select(t => t.Name), Is.EquivalentTo(ToolNames));
            foreach (var name in ToolNames)
            {
                var t = server.Tools.Find(name)!;
                Assert.That(t.Description.Length, Is.GreaterThan(80), name);
                Assert.That(t.Description, Does.Contain("Example:"), name + " has an example");
                Assert.That(t.Input["type"]!.GetValue<string>(), Is.EqualTo("object"), name);
                Assert.That(t.Output, Is.Not.Null, name);
                Assert.That(t.ToJson()["annotations"]!["title"]!.GetValue<string>(), Is.Not.Empty, name);
                foreach (var (prop, schema) in t.Input["properties"]!.AsObject())
                    Assert.That(schema!["description"]?.GetValue<string>() ?? "", Is.Not.Empty, $"{name}.{prop} is described");
                Assert.That(t.Input["properties"]!.AsObject().ContainsKey("machine"), Is.True, name + " takes the machine");
                Assert.That(ToolReference.AreaOf(name), Is.EqualTo("Neo Patch"), name);
            }
            // The example in each description is a call the tool accepts: its argument names are inputs.
            foreach (var name in ToolNames)
            {
                var t = server.Tools.Find(name)!;
                var example = System.Text.RegularExpressions.Regex.Match(t.Description, @"Example: " + name + @"\((.*)\)\.?$");
                Assert.That(example.Success, Is.True, name + ": the example is a call of this tool");
                foreach (System.Text.RegularExpressions.Match arg in System.Text.RegularExpressions.Regex.Matches(example.Groups[1].Value, @"(?<![\w""])([a-z_]+)=(?=[\w""{\[-])"))
                    Assert.That(t.Input["properties"]!.AsObject().ContainsKey(arg.Groups[1].Value), Is.True, $"{name}: example argument {arg.Groups[1].Value}");
            }
        }

        [Test]
        public void AnnotationsSayWhichToolsReadAndWhichRemove()
        {
            var (server, _) = Make();
            bool Ann(string tool, string hint) => server.Tools.Find(tool)!.ToJson()["annotations"]![hint]!.GetValue<bool>();
            Assert.That(Ann("patch_describe", "readOnlyHint"), Is.True);
            foreach (var edit in ToolNames.Skip(1)) Assert.That(Ann(edit, "readOnlyHint"), Is.False, edit);
            Assert.That(Ann("patch_remove", "destructiveHint"), Is.True);
            foreach (var plain in new[] { "patch_add_node", "patch_connect", "patch_set", "patch_load_template" }) Assert.That(Ann(plain, "destructiveHint"), Is.False, plain);
        }

        [Test]
        public void TheTemplateNamesAreTheLessonsInOrder()
        {
            var (server, _) = Make();
            var schema = server.Tools.Find("patch_load_template")!.Input["properties"]!["template"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            Assert.That(schema, Is.EqualTo(PatchTools.Templates));
            Assert.That(schema, Is.EqualTo(new[] { "start", "subtractive", "fm-2op", "fm-4op", "am-ring", "wavefolder", "sync-lead", "plucked-string", "vector-crossfade", "sample-loop" }));
            foreach (var name in schema) Assert.That(server.Tools.Find("patch_load_template")!.Description, Does.Contain(name));
        }

        // ---- calls -------------------------------------------------------------------------------

        [Test]
        public void EachCallReachesTheMachineAsItsOperationWithoutTheMachinesName()
        {
            var (server, p) = Make();
            var cases = new (string Tool, string Args, string Op, bool Changes)[]
            {
                ("patch_describe", "{\"machine\":\"Lead\",\"catalog\":true}", "describe", false),
                ("patch_add_node", "{\"kind\":\"Filter\",\"machine\":\"Lead\",\"controls\":{\"Cutoff\":\"1.5 kHz\",\"Mode\":\"ladder 24\"},\"global\":false}", "add_node", true),
                ("patch_connect", "{\"from_node\":1,\"to_node\":2,\"to_port\":\"Pitch\",\"from_port\":0,\"amount\":-0.5}", "connect", true),
                ("patch_disconnect", "{\"from_node\":1,\"to_node\":2,\"to_port\":0}", "disconnect", true),
                ("patch_set", "{\"node\":2,\"control\":\"Cutoff\",\"value\":\"800 Hz\"}", "set", true),
                ("patch_set", "{\"changes\":[{\"node\":2,\"control\":1,\"value\":900},{\"node\":3,\"control\":\"Attack\",\"value\":\"20 ms\"}]}", "set", true),
                ("patch_remove", "{\"nodes\":[2,3]}", "remove", true),
                ("patch_macro_map", "{\"macro\":3,\"node\":4,\"control\":\"Cutoff\",\"low\":\"200 Hz\",\"high\":8000,\"label\":\"Bright\"}", "macro_map", true),
                ("patch_load_template", "{\"template\":\"fm-2op\"}", "load_template", true),
            };
            foreach (var (tool, args, op, changes) in cases)
            {
                p.Calls.Clear();
                var r = Call(server, tool, J(args));
                Assert.That(r.Error, Is.False, $"{tool} {args}: {r.Text}");
                Assert.That(r.Text, Is.Not.Empty);
                var call = p.Calls.Single();
                Assert.That((call.Tool, call.Op, call.Changes), Is.EqualTo((tool, op, changes)), tool);
                Assert.That(call.Args.ContainsKey("machine"), Is.False, tool + ": the machine's name is not an argument of the operation");
                var sent = J(args); sent.Remove("machine");
                Assert.That(call.Args.ToJsonString(), Is.EqualTo(sent.ToJsonString()), tool);
            }
            p.Calls.Clear();
            Call(server, "patch_describe", J("{\"machine\":\"Lead\"}"));
            Assert.That(p.Calls.Single().Machine, Is.EqualTo("Lead"));
            p.Calls.Clear();
            Call(server, "patch_describe");
            Assert.That(p.Calls.Single().Machine, Is.Null, "no machine: the only Neo Patch in the song");
        }

        [Test]
        public void EachEditingCallIsOneUndoStepAndReadsAndRefusalsMakeNone()
        {
            var (server, p) = Make();
            Call(server, "patch_describe");
            Call(server, "patch_describe", J("{\"catalog\":true}"));
            Assert.That(p.Undo.Count, Is.EqualTo(0), "reading is no step");
            Call(server, "patch_add_node", J("{\"kind\":\"Osc\"}"));
            Call(server, "patch_add_node", J("{\"kind\":\"VCA\"}"));
            Call(server, "patch_connect", J("{\"from_node\":2,\"to_node\":3,\"to_port\":\"In\"}"));
            Assert.That(p.Undo.Count, Is.EqualTo(3), "one step for each call");
            var refused = Call(server, "patch_add_node", J("{\"kind\":\"Oscilator\"}"));
            Assert.That(refused.Error, Is.True);
            Assert.That(p.Undo.Count, Is.EqualTo(3), "a refusal makes no step");
            Assert.That(p.Nodes, Is.EqualTo(new[] { "Keys 1", "Osc 2", "VCA 3" }));
            p.UndoStep(); p.UndoStep();
            Assert.That(p.Nodes, Is.EqualTo(new[] { "Keys 1", "Osc 2" }), "undo takes the last two calls back, the add of the VCA and the wire's no-op");
            p.UndoStep();
            Assert.That(p.Nodes, Is.EqualTo(new[] { "Keys 1" }));
            Call(server, "patch_load_template", J("{\"template\":\"start\"}"));
            Assert.That(p.Nodes.Count, Is.EqualTo(5));
            p.UndoStep();
            Assert.That(p.Nodes, Is.EqualTo(new[] { "Keys 1" }), "a template is one step too");
        }

        [Test]
        public void TheMachinesRefusalsReachTheAgentInTheMachinesOwnWords()
        {
            var (server, _) = Make();
            var loop = Call(server, "patch_connect", J("{\"from_node\":2,\"to_node\":2,\"to_port\":\"FM\"}"));
            Assert.That(loop.Error, Is.True);
            Assert.That(loop.Text, Does.Contain("cannot feed itself").And.Contain("Feedback node"));
            var kind = Call(server, "patch_add_node", J("{\"kind\":\"Oscilator\"}"));
            Assert.That(kind.Text, Does.Contain("Did you mean Osc"));
            var wrong = Call(server, "patch_describe", J("{\"machine\":\"Bass\"}"));
            Assert.That(wrong.Error, Is.True);
            Assert.That(wrong.Text, Does.Contain("is not a Neo Patch machine").And.Contain("Lead"));
        }

        [Test]
        public void ArgumentsAreCheckedBeforeTheyReachTheMachine()
        {
            var (server, p) = Make();
            foreach (var (tool, args, expect) in new[]
            {
                ("patch_connect", "{\"from_node\":1,\"to_node\":2,\"to_port\":0,\"amount\":40}", "amount"),
                ("patch_connect", "{\"from_node\":1,\"to_node\":2}", "to_port"),
                ("patch_connect", "{\"from_node\":0,\"to_node\":2,\"to_port\":0}", "from_node"),
                ("patch_macro_map", "{\"macro\":17,\"node\":1,\"control\":0}", "macro"),
                ("patch_macro_map", "{\"node\":1,\"control\":0}", "macro"),
                ("patch_load_template", "{\"template\":\"supersaw\"}", "template"),
                ("patch_load_template", "{}", "template"),
                ("patch_add_node", "{}", "kind"),
                ("patch_set", "{\"node\":2,\"control\":\"Cutoff\",\"value\":true}", "value"),
                ("patch_set", "{\"changes\":[]}", "changes"),
                ("patch_describe", "{\"catalog\":\"yes\"}", "catalog"),
            })
            {
                var r = Call(server, tool, J(args));
                Assert.That(r.Error, Is.True, tool + " " + args);
                Assert.That(r.Text, Does.Contain(expect), tool + " " + args);
            }
            Assert.That(p.Calls, Is.Empty, "none of them reached the machine");
            // The tools' own checks for what the schema cannot say.
            foreach (var (tool, args, expect) in new[]
            {
                ("patch_disconnect", "{}", "from_node"),
                ("patch_disconnect", "{\"all\":true}", "node"),
                ("patch_set", "{}", "changes"),
                ("patch_remove", "{}", "nodes"),
            })
            {
                var r = Call(server, tool, J(args));
                Assert.That(r.Error, Is.True, tool + " " + args);
                Assert.That(r.Text, Does.Contain(expect), tool + " " + args);
            }
            Assert.That(p.Calls, Is.Empty);
        }

        [Test]
        public void SavingAPresetPassesTheNameCommentAndOverwrite()
        {
            var (server, p) = Make();
            var r = Call(server, "patch_save_preset", J("{\"name\":\"Lead — Agent Fold\",\"comment\":\"A folded sine\",\"machine\":\"Lead\"}"));
            Assert.That(r.Error, Is.False, r.Text);
            Assert.That(p.Saved.Single(), Is.EqualTo(((string?)"Lead", "Lead — Agent Fold", (string?)"A folded sine", false)));
            Assert.That(r.Text, Does.Contain("Lead — Agent Fold"));
            var again = Call(server, "patch_save_preset", J("{\"name\":\"Taken\"}"));
            Assert.That(again.Error, Is.True);
            Assert.That(again.Text, Does.Contain("overwrite=true"));
            Assert.That(Call(server, "patch_save_preset", J("{\"name\":\"Taken\",\"overwrite\":true}")).Error, Is.False);
            Assert.That(Call(server, "patch_save_preset", J("{\"name\":\"  \"}")).Error, Is.True, "a blank name is refused");
            Assert.That(p.Calls, Is.Empty, "saving a preset is not an edit call");
        }

        [Test]
        public void DescribeSaysWhatIsInThePatchAndWhatToDoWhenItIsEmpty()
        {
            var (server, p) = Make();
            var r = Call(server, "patch_describe");
            Assert.That(r.Text, Does.Contain("Lead").And.Contain("1 of 128 nodes").And.Contain("Keys 1"));
            p.Nodes.Clear();
            Assert.That(Call(server, "patch_describe").Text, Does.Contain("The patch is empty").And.Contain("patch_load_template"));
        }
    }
}
