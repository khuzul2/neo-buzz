using System.Text.Json.Nodes;
using NeoBuzz.Dsp;
using NeoBuzz.Patch;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Patch NP3 (docs/specs/neo-patch.md §4.6): what the patch_* tools do to a patch (PatchAgentApi): describe, add, connect, disconnect, set, remove, macro
    /// mapping, templates and rename. Each edit is one step of the history it shares with the inner canvas's model, a refusal changes nothing, and the errors say
    /// what to do. (The tools' own schemas and wiring are tested in ReBuzz.AgentTests/PatchToolTests.cs.)
    /// </summary>
    [TestFixture]
    public class NeoPatchAgentApiTests
    {
        sealed class Steps : IPatchHistory
        {
            public readonly List<(string Name, string Before, string After)> List = new();
            public void Record(string name, string before, string after) => List.Add((name, before, after));
        }

        sealed class Rig
        {
            public readonly PatchEngine Engine;
            public readonly Steps History = new();
            public readonly PatchAgentApi Api;
            public Rig(PatchGraph start = null)
            {
                Engine = new PatchEngine(new ParamValues(Params.Table), Measure.Rate);
                Engine.SetGraph(start ?? new PatchGraph());
                Api = new PatchAgentApi(Engine, History);
            }
            public JsonObject Call(string op, string json = "{}") => (JsonObject)JsonNode.Parse(Api.Invoke(op, json));
            public JsonObject Ok(string op, string json = "{}")
            {
                var r = Call(op, json);
                Assert.That(r["error"], Is.Null, $"{op} {json}: {r["error"]}");
                return r;
            }
            public string Error(string op, string json)
            {
                string before = Engine.Text();
                int steps = History.List.Count;
                var r = Call(op, json);
                Assert.That(r["error"], Is.Not.Null, $"{op} {json} should be refused");
                Assert.That(Engine.Text(), Is.EqualTo(before), $"{op} {json}: a refused call changes nothing");
                Assert.That(History.List.Count, Is.EqualTo(steps), $"{op} {json}: and makes no undo step");
                return (string)r["error"];
            }
            public int Add(string kind, string extra = "") => (int)Ok("add_node", "{\"kind\":\"" + kind + "\"" + extra + "}")["id"];
            public void Undo()
            {
                var s = History.List[^1];
                History.List.RemoveAt(History.List.Count - 1);
                Engine.SetPatch(s.Before);
            }
        }

        // ------------------------------------------------------------------ describe

        [Test]
        public void DescribeListsNodesPortsWiresMacrosAndTheCpu()
        {
            var rig = new Rig(PatchPresets.Build(0));
            var d = rig.Ok("describe");
            Assert.That((string)d["name"], Is.EqualTo("Init — Saw"));
            Assert.That((int)d["node_count"], Is.EqualTo(7));
            Assert.That((int)d["node_cap"], Is.EqualTo(128));
            var nodes = d["nodes"].AsArray();
            Assert.That(nodes.Select(n => (string)n["kind"]), Is.EqualTo(new[] { "Keys", "Osc", "Envelope", "Filter", "Envelope", "VCA", "Out" }));
            var filter = nodes[3];
            Assert.That(filter["controls"].AsArray().Select(c => (string)c["name"]), Is.EqualTo(new[] { "Mode", "Cutoff", "Resonance", "Cutoff mod" }));
            Assert.That((string)filter["controls"][1]["unit"], Is.EqualTo("Hz"));
            Assert.That(filter["controls"][0]["choices"].AsArray().Select(c => (string)c), Does.Contain("ladder 24"));
            Assert.That(filter["inputs"].AsArray().Select(p => (string)p["name"]), Is.EqualTo(new[] { "In", "Cutoff", "Resonance" }));
            Assert.That((string)nodes[1]["scope"], Is.EqualTo("per voice"));
            Assert.That((string)nodes[6]["scope"], Is.EqualTo("global"));
            Assert.That(d["wires"].AsArray().Count, Is.EqualTo(8));
            Assert.That((string)d["wires"][0]["text"], Does.Contain("Keys 1 Pitch to Osc 2 Pitch"));
            var macros = d["macros"].AsArray();
            Assert.That(macros.Count, Is.EqualTo(2));
            Assert.That((int)macros[0]["macro"], Is.EqualTo(1));
            Assert.That((string)macros[0]["mappings"][0]["control"], Is.EqualTo("Cutoff"));
            Assert.That(d["cpu"]["percent"], Is.Not.Null);
            Assert.That(d["catalog"], Is.Null);
            Assert.That(rig.Ok("describe", "{\"catalog\":true}")["catalog"].AsArray().Count, Is.EqualTo(NodeInfo.KindCount));
        }

        [Test]
        public void DescribeNamesTheNodesThatLeadNowhere()
        {
            var rig = new Rig();
            int osc = rig.Add("osc");
            var d = rig.Ok("describe");
            Assert.That(d["unheard"].AsArray().Select(x => (int)x), Is.EqualTo(new[] { osc }));
            Assert.That((bool)d["nodes"][0]["heard"], Is.False);
        }

        // ------------------------------------------------------------------ add_node

        [Test]
        public void AddNodeTakesTheKindByAnyNameAndSetsControlsInRealUnits()
        {
            var rig = new Rig();
            foreach (var (name, kind) in new[] { ("Osc", "Osc"), ("osc", "Osc"), ("wavetable osc", "Wavetable Osc"), ("Wavetable_Osc", "Wavetable Osc"), ("sample & hold", "Sample & Hold"), ("VCA", "VCA"), ("voice sum", "Voice sum"), ("global keys", "Global Keys"), ("lfo", "LFO") })
                Assert.That((string)rig.Ok("add_node", $"{{\"kind\":\"{name}\"}}")["kind"], Is.EqualTo(kind), name);
            Assert.That(rig.History.List.Count, Is.EqualTo(9), "one step for each node");

            var f = rig.Ok("add_node", "{\"kind\":\"Filter\",\"controls\":{\"Mode\":\"ladder 24\",\"Cutoff\":\"1.5 kHz\",\"Resonance\":4,\"Cutoff mod\":2},\"x\":400,\"y\":96}");
            var node = rig.Engine.Graph.Find((int)f["id"]);
            Assert.That(node.Controls[NodeInfo.FilterMode], Is.EqualTo(4f));
            Assert.That(node.Controls[NodeInfo.FilterCutoff], Is.EqualTo(1500f));
            Assert.That(node.Controls[NodeInfo.FilterReso], Is.EqualTo(4f));
            Assert.That((node.X, node.Y), Is.EqualTo((400f, 96f)));
            Assert.That(rig.History.List.Count, Is.EqualTo(10), "the node and its controls are one step");
            Assert.That(rig.History.List[^1].Name, Is.EqualTo("Add Filter"));

            var l = rig.Ok("add_node", "{\"kind\":\"Delay\",\"global\":true,\"controls\":{\"Time\":\"0.25 s\"}}");
            Assert.That(rig.Engine.Graph.Find((int)l["id"]).Global, Is.True);
            Assert.That(rig.Engine.Graph.Find((int)l["id"]).Controls[NodeInfo.DelayTime], Is.EqualTo(250f).Within(1e-3), "seconds into milliseconds");
        }

        [Test]
        public void AddNodeRefusalsSayWhatIsValid()
        {
            var rig = new Rig();
            Assert.That(rig.Error("add_node", "{\"kind\":\"Oscilator\"}"), Does.Contain("There is no node kind").And.Contain("Osc").And.Contain("Wavetable Osc"));
            Assert.That(rig.Error("add_node", "{}"), Does.Contain("Give the node's kind"));
            Assert.That(rig.Error("add_node", "{\"kind\":\"Filter\",\"controls\":{\"Cutoff\":50000}}"), Does.Contain("Filter 0 Cutoff runs from 20 Hz to 20 kHz").And.Contain("outside"));
            Assert.That(rig.Error("add_node", "{\"kind\":\"Filter\",\"controls\":{\"Cutt\":500}}"), Does.Contain("has no control").And.Contain("Cutoff"));
            Assert.That(rig.Error("add_node", "{\"kind\":\"Filter\",\"controls\":{\"Mode\":\"lowpass\"}}"), Does.Contain("is one of").And.Contain("ladder 24"));
            Assert.That(rig.Error("add_node", "{\"kind\":\"Keys\",\"global\":true}"), Does.Contain("always per voice"));
            Assert.That(rig.Error("add_node", "{\"kind\":\"Filter\",\"controls\":{\"Cutoff\":\"3 parsecs\"}}"), Does.Contain("does not fit"));
        }

        [Test]
        public void AddNodeStopsAtTheNodeCap()
        {
            var g = new PatchGraph();
            for (int i = 0; i < NodeInfo.Cap; i++) g.Add(NodeKind.Math);
            var rig = new Rig(g);
            Assert.That(rig.Error("add_node", "{\"kind\":\"Osc\"}"), Does.Contain("128-node limit").And.Contain("patch_remove"));
        }

        // ------------------------------------------------------------------ connect and disconnect

        [Test]
        public void ConnectTakesPortsByNameOrNumberAndAnAmount()
        {
            var rig = new Rig();
            int k = rig.Add("Keys"), o = rig.Add("Osc"), v = rig.Add("VCA"), s = rig.Add("Out");
            Assert.That((string)rig.Ok("connect", $"{{\"from_node\":{k},\"from_port\":\"Pitch\",\"to_node\":{o},\"to_port\":\"pitch\"}}")["connected"], Is.EqualTo($"Keys {k} Pitch to Osc {o} Pitch"));
            rig.Ok("connect", $"{{\"from_node\":{o},\"to_node\":{v},\"to_port\":0}}");
            var third = rig.Ok("connect", $"{{\"from_node\":{v},\"to_node\":{s},\"to_port\":\"In 1\",\"amount\":0.5}}");
            Assert.That((double)third["amount"], Is.EqualTo(0.5));
            Assert.That(rig.Engine.Graph.Wires.Count, Is.EqualTo(3));
            Assert.That(rig.Engine.Graph.Wires.Last().Amount, Is.EqualTo(0.5f));
            Assert.That(rig.History.List.Select(h => h.Name).Skip(4), Is.EqualTo(new[] { "Connect Keys to Osc", "Connect Osc to VCA", "Connect VCA to Out" }));

            // The same wire again changes nothing; with an amount it changes the amount, as one step.
            int steps = rig.History.List.Count;
            Assert.That((bool)rig.Ok("connect", $"{{\"from_node\":{v},\"to_node\":{s},\"to_port\":0,\"amount\":0.5}}")["already_connected"], Is.True);
            Assert.That(rig.History.List.Count, Is.EqualTo(steps));
            rig.Ok("connect", $"{{\"from_node\":{v},\"to_node\":{s},\"to_port\":0,\"amount\":0.25}}");
            Assert.That(rig.Engine.Graph.Wires.Last().Amount, Is.EqualTo(0.25f));
            Assert.That(rig.History.List.Count, Is.EqualTo(steps + 1));
        }

        [Test]
        public void ARefusedLoopNamesTheLoopAndSuggestsAFeedbackNode()
        {
            var rig = new Rig();
            int o = rig.Add("Osc"), v = rig.Add("VCA");
            rig.Ok("connect", $"{{\"from_node\":{o},\"to_node\":{v},\"to_port\":\"In\"}}");
            string e = rig.Error("connect", $"{{\"from_node\":{v},\"to_node\":{o},\"to_port\":\"FM\"}}");
            Assert.That(e, Does.Contain($"Osc {o}").And.Contain($"VCA {v}").And.Contain("loop").And.Contain("Feedback node"));
            Assert.That(rig.Error("connect", $"{{\"from_node\":{o},\"to_node\":{o},\"to_port\":\"FM\"}}"), Does.Contain("cannot feed itself").And.Contain("Feedback"));
            // Through a Feedback node the same loop is allowed.
            int fb = rig.Add("Feedback");
            rig.Ok("connect", $"{{\"from_node\":{v},\"to_node\":{fb},\"to_port\":0}}");
            rig.Ok("connect", $"{{\"from_node\":{fb},\"to_node\":{o},\"to_port\":\"FM\"}}");
        }

        [Test]
        public void ConnectRefusalsListThePortsAndTheNodes()
        {
            var rig = new Rig();
            int o = rig.Add("Osc"), v = rig.Add("VCA");
            Assert.That(rig.Error("connect", $"{{\"from_node\":{o},\"to_node\":{v},\"to_port\":\"Gain\"}}"), Does.Contain("has no input port \"Gain\"").And.Contain("0 In, 1 CV"));
            Assert.That(rig.Error("connect", $"{{\"from_node\":{o},\"to_node\":{v},\"to_port\":7}}"), Does.Contain("no input port 7"));
            Assert.That(rig.Error("connect", $"{{\"from_node\":{o},\"from_port\":3,\"to_node\":{v},\"to_port\":0}}"), Does.Contain("no output port 3").And.Contain("0 Out"));
            Assert.That(rig.Error("connect", $"{{\"from_node\":99,\"to_node\":{v},\"to_port\":0}}"), Does.Contain("There is no node 99").And.Contain($"Osc {o}"));
            Assert.That(rig.Error("connect", $"{{\"from_node\":{v},\"to_node\":{o},\"to_port\":0,\"amount\":40}}"), Does.Contain("-16 to 16"));
            Assert.That(rig.Error("connect", $"{{\"from_node\":{v},\"to_port\":0}}"), Does.Contain("to_node"));
            int k = rig.Add("Keys");
            Assert.That(rig.Error("connect", $"{{\"from_node\":{v},\"to_node\":{k},\"to_port\":0}}"), Does.Contain("has no input ports"));
        }

        [Test]
        public void DisconnectRemovesOneWireOrAllOfANode()
        {
            var rig = new Rig(PatchPresets.Build(0));
            int steps = rig.History.List.Count;
            var d = rig.Ok("disconnect", "{\"from_node\":1,\"from_port\":0,\"to_node\":2,\"to_port\":0}");
            Assert.That((int)d["removed"], Is.EqualTo(1));
            Assert.That(rig.Engine.Graph.Wires.Count, Is.EqualTo(7));
            Assert.That(rig.History.List.Count, Is.EqualTo(steps + 1));
            Assert.That(rig.Error("disconnect", "{\"from_node\":1,\"from_port\":0,\"to_node\":2,\"to_port\":0}"), Does.Contain("There is no wire").And.Contain("Keys 1 Pitch to Osc 2 Pitch").Or.Contain("not wired together"));
            var all = rig.Ok("disconnect", "{\"node\":4,\"all\":true}");
            Assert.That((int)all["removed"], Is.EqualTo(3), "the filter's in, cutoff and out");
            Assert.That(rig.Error("disconnect", "{\"node\":4,\"all\":true}"), Does.Contain("has no wires"));
        }

        // ------------------------------------------------------------------ set, remove, macros, templates, rename

        [Test]
        public void SetTakesRealUnitsAndMakesOneStepForOneOrManyControls()
        {
            var rig = new Rig(PatchPresets.Build(0));
            int steps = rig.History.List.Count;
            var r = rig.Ok("set", "{\"node\":4,\"control\":\"Cutoff\",\"value\":\"800 Hz\"}");
            Assert.That(rig.Engine.Graph.Find(4).Controls[NodeInfo.FilterCutoff], Is.EqualTo(800f));
            Assert.That((string)r["set"][0]["shown"], Is.EqualTo("800 Hz"));
            Assert.That((string)r["set"][0]["note"], Does.Contain("macro"), "the cutoff is mapped to a macro");
            rig.Ok("set", "{\"changes\":[{\"node\":4,\"control\":1,\"value\":1200},{\"node\":4,\"control\":\"Mode\",\"value\":\"ladder 12\"},{\"node\":2,\"control\":\"Shape\",\"value\":\"square\"},{\"node\":3,\"control\":\"Attack\",\"value\":\"20 ms\"}]}");
            var g = rig.Engine.Graph;
            Assert.That(g.Find(4).Controls[NodeInfo.FilterCutoff], Is.EqualTo(1200f));
            Assert.That(g.Find(4).Controls[NodeInfo.FilterMode], Is.EqualTo(5f));
            Assert.That(g.Find(2).Controls[NodeInfo.OscShape], Is.EqualTo(1f));
            Assert.That(g.Find(3).Controls[NodeInfo.EnvA], Is.EqualTo(0.02f).Within(1e-6));
            Assert.That(rig.History.List.Count, Is.EqualTo(steps + 2), "one step for each call, however many controls");
            rig.Ok("set", "{\"node\":2,\"global\":true}");
            Assert.That(rig.Engine.Graph.Find(2).Global, Is.True);
        }

        [Test]
        public void ASetWithOneBadChangeChangesNothing()
        {
            var rig = new Rig(PatchPresets.Build(0));
            string e = rig.Error("set", "{\"changes\":[{\"node\":4,\"control\":\"Resonance\",\"value\":3},{\"node\":4,\"control\":\"Cutoff\",\"value\":1}]}");
            Assert.That(e, Does.Contain("Filter 4 Cutoff runs from 20 Hz"));
            Assert.That(rig.Error("set", "{\"node\":4,\"control\":\"Cutoff\"}"), Does.Contain("Give the value"));
            Assert.That(rig.Error("set", "{\"node\":4,\"control\":\"Volume\",\"value\":1}"), Does.Contain("has no control \"Volume\"").And.Contain("Cutoff mod"));
            Assert.That(rig.Error("set", "{\"node\":1,\"control\":0,\"value\":1}"), Does.Contain("has no controls"));
            Assert.That(rig.Error("set", "{\"node\":\"x\",\"control\":0,\"value\":1}"), Does.Contain("node id"));
            Assert.That(rig.Error("set", "{\"changes\":[]}"), Does.Contain("empty"));
            Assert.That(rig.Error("set", "{\"node\":7,\"global\":false}"), Does.Contain("always global"));
        }

        [Test]
        public void RemoveTakesTheWiresAndTheMappingsWithIt()
        {
            var rig = new Rig(PatchPresets.Build(0));
            var r = rig.Ok("remove", "{\"node\":4}");
            Assert.That((string)r["removed"][0], Is.EqualTo("Filter 4"));
            Assert.That((int)r["wires_removed"], Is.EqualTo(3));
            Assert.That(rig.Engine.Graph.Nodes.Count, Is.EqualTo(6));
            Assert.That(rig.Engine.Graph.Macros.Any(m => m.NodeId == 4), Is.False);
            Assert.That(rig.History.List.Count, Is.EqualTo(1));
            rig.Ok("remove", "{\"nodes\":[2,3]}");
            Assert.That(rig.History.List.Count, Is.EqualTo(2), "several nodes, one step");
            Assert.That(rig.Error("remove", "{\"node\":4}"), Does.Contain("There is no node 4"));
        }

        [Test]
        public void MacroMapSweepsAControlBetweenTwoValuesAndNamesTheMacro()
        {
            var rig = new Rig(PatchPresets.Build(0));
            int steps = rig.History.List.Count;
            var m = rig.Ok("macro_map", "{\"macro\":3,\"node\":4,\"control\":\"Resonance\",\"low\":0.7,\"high\":6,\"label\":\"Bite\"}");
            Assert.That((string)m["label"], Is.EqualTo("Bite"));
            var map = rig.Engine.Graph.Macros.Single(x => x.NodeId == 4 && x.Control == NodeInfo.FilterReso);
            Assert.That((map.Macro, map.Min, map.Max), Is.EqualTo((2, 0.7f, 6f)));
            Assert.That(rig.History.List.Count, Is.EqualTo(steps + 1));
            // Moving the cutoff's mapping to another macro takes it off macro 1.
            rig.Ok("macro_map", "{\"macro\":5,\"node\":4,\"control\":\"Cutoff\"}");
            Assert.That(rig.Engine.Graph.Macros.Single(x => x.NodeId == 4 && x.Control == NodeInfo.FilterCutoff).Macro, Is.EqualTo(4));
            rig.Ok("macro_map", "{\"macro\":5,\"label\":\"Brightness\"}");
            Assert.That(rig.Engine.Graph.MacroLabels[4], Is.EqualTo("Brightness"));
            rig.Ok("macro_map", "{\"macro\":3,\"node\":4,\"control\":\"Resonance\",\"unmap\":true}");
            Assert.That(rig.Engine.Graph.Macros.Any(x => x.Control == NodeInfo.FilterReso), Is.False);
            Assert.That(rig.Error("macro_map", "{\"macro\":3,\"node\":4,\"control\":\"Resonance\",\"unmap\":true}"), Does.Contain("is not mapped"));
            Assert.That(rig.Error("macro_map", "{\"macro\":17,\"node\":4,\"control\":\"Cutoff\"}"), Does.Contain("1 to 16"));
            Assert.That(rig.Error("macro_map", "{\"macro\":1,\"node\":4,\"control\":\"Mode\"}"), Does.Contain("picks one of"));
            Assert.That(rig.Error("macro_map", "{\"macro\":1}"), Does.Contain("Give node and control"));
        }

        [Test]
        public void EveryTemplateLoadsByNameAsOneStepAndUndoPutsTheOldPatchBack()
        {
            var rig = new Rig(PatchPresets.Build(0));
            string original = rig.Engine.Text();
            for (int i = 0; i < PatchLessons.Count; i++)
            {
                int steps = rig.History.List.Count;
                var r = rig.Ok("load_template", $"{{\"template\":\"{(i % 2 == 0 ? PatchLessons.Slugs[i] : PatchLessons.Titles[i].ToLowerInvariant())}\"}}");
                Assert.That((string)r["template"], Is.EqualTo(PatchLessons.Slugs[i]));
                Assert.That(rig.History.List.Count, Is.EqualTo(steps + 1), PatchLessons.Slugs[i]);
                Assert.That(rig.Engine.Graph.Name, Is.EqualTo(PatchLessons.Titles[i]));
                Assert.That((int)r["node_count"], Is.EqualTo(rig.Engine.Graph.Nodes.Count));
            }
            for (int i = 0; i < PatchLessons.Count; i++) rig.Undo();
            Assert.That(rig.Engine.Text(), Is.EqualTo(original));
            Assert.That((string)rig.Ok("load_template", "{\"template\":\"start\"}")["note"], Does.Contain("three wires"));
            string e = rig.Error("load_template", "{\"template\":\"supersaw\"}");
            Assert.That(e, Does.Contain("There is no template").And.Contain("subtractive").And.Contain("fm-4op").And.Contain("sample-loop"));
            Assert.That(rig.Error("load_template", "{}"), Does.Contain("Give the template's name"));
        }

        [Test]
        public void RenameIsOneStepAndNeedsAName()
        {
            var rig = new Rig();
            Assert.That((string)rig.Ok("rename", "{\"name\":\"My lead\"}")["name"], Is.EqualTo("My lead"));
            Assert.That(rig.History.List.Count, Is.EqualTo(1));
            Assert.That(rig.Error("rename", "{\"name\":\"  \"}"), Does.Contain("Give the patch's name"));
            Assert.That(rig.Error("explode", "{}"), Does.Contain("no operation").And.Contain("describe"));
            Assert.That(rig.Api.Invoke("describe", "[1]"), Does.Contain("JSON object"));
        }

        // ------------------------------------------------------------------ history shared with the canvas

        [Test]
        public void AgentEditsAndCanvasEditsShareOneHistoryAndUndoInOrder()
        {
            var rig = new Rig();
            var canvas = new PatchEditModel(rig.Engine, rig.History);   // the inner canvas's model, on the same engine and history
            int k = rig.Add("Keys");
            var osc = canvas.AddNode(NodeKind.Osc, 208, 0);             // a person adds a node
            rig.Ok("connect", $"{{\"from_node\":{k},\"to_node\":{osc.Id},\"to_port\":\"Pitch\"}}");   // the agent wires it
            canvas.SetControl(osc.Id, NodeInfo.OscTune, 7f);           // the person tunes it
            rig.Ok("set", $"{{\"node\":{osc.Id},\"control\":\"Shape\",\"value\":\"sine\"}}");
            Assert.That(rig.History.List.Select(h => h.Name), Is.EqualTo(new[] { "Add Keys", "Add Osc", "Connect Keys to Osc", "Osc Tune", "Osc Shape" }));
            // Every step's before is the previous step's after: the stack is one line.
            for (int i = 1; i < rig.History.List.Count; i++) Assert.That(rig.History.List[i].Before, Is.EqualTo(rig.History.List[i - 1].After), rig.History.List[i].Name);
            rig.Undo(); rig.Undo();
            Assert.That(rig.Engine.Graph.Find(osc.Id).Controls[NodeInfo.OscTune], Is.EqualTo(0f));
            Assert.That(rig.Engine.Graph.Wires.Count, Is.EqualTo(1));
            rig.Undo();
            Assert.That(rig.Engine.Graph.Wires.Count, Is.EqualTo(0));
        }

        [Test]
        public void ADescribeReadsAndNeverMakesAStep()
        {
            var rig = new Rig(PatchPresets.Build(2));
            rig.Ok("describe");
            rig.Ok("describe", "{\"catalog\":true}");
            Assert.That(rig.History.List.Count, Is.EqualTo(0));
            Assert.That(rig.Api.Edited, Is.False);
            rig.Ok("set", "{\"node\":1,\"control\":0,\"value\":1}".Replace("\"node\":1", "\"node\":2").Replace("\"control\":0", "\"control\":\"Tune\"").Replace("\"value\":1", "\"value\":3"));
            Assert.That(rig.Api.Edited, Is.True);
        }

        [Test]
        public void ThePatchStillSoundsAfterAgentEdits()
        {
            // Build the empty patch's sound with the tools alone: three wires, the lesson's steps.
            var rig = new Rig();
            rig.Ok("load_template", "{\"template\":\"start\"}");
            foreach (var (from, fp, to, tp) in PatchLessons.StartSteps)
            {
                int a = rig.Engine.Graph.Nodes.First(n => n.Kind == from).Id, b = rig.Engine.Graph.Nodes.First(n => n.Kind == to).Id;
                rig.Ok("connect", $"{{\"from_node\":{a},\"from_port\":{fp},\"to_node\":{b},\"to_port\":{tp}}}");
            }
            var y = PresetTests.RenderSynth(rig.Engine, (en, l, r, n) => ((PatchEngine)en).Process(l, r, n), Measure.Rate);
            double loudest = 0;
            for (int at = 0; at + 2400 <= y.Length * 2 / 3; at += 1200) loudest = Math.Max(loudest, Measure.Rms(y, at, 2400));
            Assert.That(Measure.Db(loudest), Is.EqualTo(-22.0).Within(0.3));
        }
    }
}
