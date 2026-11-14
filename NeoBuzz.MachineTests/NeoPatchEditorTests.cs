using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Patch;
using ReBuzzTests.Automation;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Patch NP2 (docs/specs/neo-patch.md §4.5): the inner canvas without a toolkit. The model (add, connect, refuse a cycle with the reason, disconnect, move, set a
    /// control, copy, paste, duplicate, macros, one history step per edit, undo and redo), the words (every node, control and port has them; every control has
    /// Explain How and Sound), the surface on the kit's recording canvas (what it draws, where a click lands, a wire dragged between dots, the menu with search,
    /// zoom and pan, the side panel, the macro strip, the Learn overlay, the CPU readout), the editor's saved state, and the audio thread through live edits.
    /// </summary>
    [TestFixture]
    public class NeoPatchEditorTests
    {
        sealed class Host : IPatchSurfaceHost
        {
            public readonly float[] Macros = new float[16];
            public int Gestures;
            public float Macro(int i) => Macros[i];
            public void SetMacro(int i, float v) => Macros[i] = v;
            public void BeginMacro() => Gestures++;
            public void EndMacro() { }
            public int Voices => 8;
        }

        sealed class Rig
        {
            public readonly PatchEngine Engine = new PatchEngine(new ParamValues(NeoBuzz.Patch.Params.Table));
            public readonly PatchEditModel Model;
            public readonly PatchSurface Surface;
            public readonly Host Host = new Host();
            public readonly RecordingCanvas Canvas = new RecordingCanvas();
            public long Clock = 1000;

            public Rig(PatchGraph g = null)
            {
                Engine.SetGraph(g ?? new PatchGraph());
                Model = new PatchEditModel(Engine);
                Model.Clock = () => Clock;
                Surface = new PatchSurface(Model, Host, Tokens.Identity(MachineCategory.Synth), () => Clock);
                Surface.TidyIfStacked();
            }

            public PatchGraph G => Engine.Graph;

            public void Draw()
            {
                Canvas.Clear();
                Surface.Draw(Canvas, new NeoRect(0, 0, PatchSurface.Width, PatchSurface.Height));
            }

            public List<string> Texts()
            {
                Draw();
                return Canvas.Of(NeoDrawKind.Text).Select(c => c.Text).ToList();
            }

            public void Click(double x, double y, bool fine = false, int clicks = 1)
            {
                Surface.Down(x, y, clicks, fine);
                Surface.Up();
            }

            public void ClickNode(Node n) => Click(Surface.NodeRect(n).X + 60, Surface.NodeRect(n).Y + 12);

            public void DragWire(Node from, int outPort, Node to, int inPort)
            {
                var a = Surface.PortPos(from, outPort, true);
                var b = Surface.PortPos(to, inPort, false);
                Surface.Down(a.X, a.Y, 1, false);
                Surface.Move((a.X + b.X) / 2, (a.Y + b.Y) / 2, false);
                Surface.Move(b.X, b.Y, false);
                Surface.Up();
            }

            public Node Add(NodeKind k, float x, float y) => Model.AddNode(k, x, y);
        }

        /// <summary>Keys, Osc, Envelope, VCA and Out wired as a basic voice.</summary>
        static Rig BasicVoice()
        {
            var rig = new Rig();
            rig.Surface.AddStarter();
            return rig;
        }

        // ------------------------------------------------------------------ the model

        [Test]
        public void AddConnectDisconnectUndoAndRedoAreOneStepEach()
        {
            var rig = new Rig();
            var m = rig.Model;
            var keys = m.AddNode(NodeKind.Keys, 0, 0);
            var osc = m.AddNode(NodeKind.Osc, 200, 0);
            var o = m.AddNode(NodeKind.Out, 400, 0);
            Assert.That(rig.G.Nodes.Count, Is.EqualTo(3));
            Assert.That(m.Connect(keys.Id, 0, osc.Id, NodeInfo.OscInPitch).Ok, Is.True);
            Assert.That(m.Connect(osc.Id, 0, o.Id, 0).Ok, Is.True);
            Assert.That(rig.G.Wires.Count, Is.EqualTo(2));
            Assert.That(m.Disconnect(new WireKey(keys.Id, 0, osc.Id, NodeInfo.OscInPitch)), Is.True);
            Assert.That(rig.G.Wires.Count, Is.EqualTo(1));
            // Six edits, six steps: three adds, two connects, one disconnect.
            for (int i = 0; i < 6; i++) Assert.That(m.Undo(), Is.True, "undo " + i);
            Assert.That(m.Undo(), Is.False);
            Assert.That(rig.G.Nodes.Count, Is.EqualTo(0));
            for (int i = 0; i < 6; i++) Assert.That(m.Redo(), Is.True, "redo " + i);
            Assert.That(rig.G.Nodes.Count, Is.EqualTo(3));
            Assert.That(rig.G.Wires.Count, Is.EqualTo(1));
            Measure.Report("Neo Patch editor: add, connect and disconnect are one step each, and undo and redo walk them");
        }

        [Test]
        public void AGestureIsOneStepAndMovingNeedsNoRecompile()
        {
            var rig = new Rig(PatchTemplates.Core8());
            var m = rig.Model;
            var compiled = rig.Engine.Runtime.Current;
            var ids = rig.G.Nodes.Take(3).Select(n => n.Id).ToArray();
            var start = ids.ToDictionary(id => id, id => (rig.G.Find(id).X, rig.G.Find(id).Y));
            m.BeginGesture("Move");
            for (int i = 1; i <= 10; i++) m.MoveNodes(ids, start, i * 8, i * 4);
            m.EndGesture();
            Assert.That(rig.G.Find(ids[0]).X, Is.EqualTo(80));
            Assert.That(rig.G.Find(ids[0]).Y, Is.EqualTo(40));
            Assert.That(rig.Engine.Runtime.Current, Is.SameAs(compiled), "a move does not compile");
            Assert.That(m.Undo(), Is.True);
            Assert.That(rig.G.Find(ids[0]).X, Is.EqualTo(0));
            Assert.That(m.Undo(), Is.False, "ten moves were one step");
        }

        [Test]
        public void ACycleIsRefusedWithItsReasonUnlessItGoesThroughFeedback()
        {
            var rig = new Rig();
            var m = rig.Model;
            var f = m.AddNode(NodeKind.Filter, 0, 0);
            var v = m.AddNode(NodeKind.Vca, 200, 0);
            var o = m.AddNode(NodeKind.Out, 400, 0);
            Assert.That(m.Connect(f.Id, 0, v.Id, NodeInfo.VcaInAudio).Ok, Is.True);
            Assert.That(m.Connect(v.Id, 0, o.Id, 0).Ok, Is.True);
            int steps = 0;
            while (m.Undo()) steps++;
            while (m.Redo()) { }
            var before = PatchText.Save(rig.G);
            var r = m.Connect(v.Id, 0, f.Id, NodeInfo.FilterInAudio);
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Message, Does.Contain("loop").And.Contain("Filter " + f.Id).And.Contain("VCA " + v.Id).And.Contain("Feedback"));
            Assert.That(PatchText.Save(rig.G), Is.EqualTo(before), "the patch is as it was");
            Assert.That(m.LastRefusal.Nodes, Is.EquivalentTo(new[] { f.Id, v.Id }));
            Assert.That(m.LastRefusal.Wires, Has.Length.EqualTo(1));
            Assert.That(m.CanUndo, Is.True);
            int after = 0;
            while (m.Undo()) after++;
            Assert.That(after, Is.EqualTo(steps), "a refusal is not a step");
            while (m.Redo()) { }
            // Itself.
            Assert.That(m.Connect(f.Id, 0, f.Id, NodeInfo.FilterInAudio).Message, Does.Contain("cannot feed itself"));
            // Through a Feedback node it is allowed.
            var fb = m.AddNode(NodeKind.Feedback, 200, 100);
            Assert.That(m.Connect(v.Id, 0, fb.Id, 0).Ok, Is.True);
            Assert.That(m.Connect(fb.Id, 0, f.Id, NodeInfo.FilterInAudio).Ok, Is.True, "the loop passes through Feedback");
            Measure.Report("Neo Patch editor: a loop without Feedback is refused with the loop named; through Feedback it connects");
        }

        [Test]
        public void ControlEditsAreInPlaceOneStepAndClamped()
        {
            var rig = new Rig(PatchTemplates.Core8());
            var m = rig.Model;
            var f = rig.G.Nodes.First(n => n.Kind == NodeKind.Filter);
            var compiled = rig.Engine.Runtime.Current;
            Assert.That(m.SetControl(f.Id, NodeInfo.FilterCutoff, 5000), Is.True);
            Assert.That(f.Controls[NodeInfo.FilterCutoff], Is.EqualTo(5000));
            Assert.That(rig.Engine.Runtime.Current, Is.SameAs(compiled), "a control is a float the audio thread reads: no compile");
            m.SetControl(f.Id, NodeInfo.FilterCutoff, 1e9f);
            Assert.That(f.Controls[NodeInfo.FilterCutoff], Is.EqualTo(20000));
            m.SetControl(f.Id, NodeInfo.FilterCutoff, float.NaN);
            Assert.That(f.Controls[NodeInfo.FilterCutoff], Is.EqualTo(2000), "a NaN is the default");
            Assert.That(m.SetControl(f.Id, NodeInfo.FilterCutoff, 2000), Is.False, "no change, no step");
            m.BeginGesture("drag");
            for (int i = 0; i < 20; i++) m.SetControl(f.Id, NodeInfo.FilterCutoff, 100 + i * 50);
            m.EndGesture();
            Assert.That(m.Undo(), Is.True);
            Assert.That(rig.G.Find(f.Id).Controls[NodeInfo.FilterCutoff], Is.EqualTo(2000), "the drag was one step");
            Assert.That(m.Undo(), Is.True);
            Assert.That(rig.G.Find(f.Id).Controls[NodeInfo.FilterCutoff], Is.EqualTo(20000));
        }

        [Test]
        public void CopyPasteAndDuplicateKeepTheWiresBetweenTheCopiedNodes()
        {
            var rig = BasicVoice();
            var m = rig.Model;
            var osc = rig.G.Nodes.First(n => n.Kind == NodeKind.Osc);
            var env = rig.G.Nodes.First(n => n.Kind == NodeKind.Envelope);
            osc.Set(NodeInfo.OscTune, 7);
            m.SelectMany(new[] { osc.Id, env.Id });
            Assert.That(m.Copy(), Is.True);
            int nodes = rig.G.Nodes.Count, wires = rig.G.Wires.Count;
            Assert.That(m.Paste(600, 304), Is.EqualTo(2));
            Assert.That(rig.G.Nodes.Count, Is.EqualTo(nodes + 2));
            Assert.That(rig.G.Wires.Count, Is.EqualTo(wires), "no wire was between the two copied nodes, so none came");
            var pasted = m.Selection.Select(rig.G.Find).ToList();
            Assert.That(pasted.Select(n => n.Kind), Is.EquivalentTo(new[] { NodeKind.Osc, NodeKind.Envelope }));
            Assert.That(pasted.First(n => n.Kind == NodeKind.Osc).Controls[NodeInfo.OscTune], Is.EqualTo(7));
            Assert.That(pasted.Min(n => n.X), Is.EqualTo(600));
            Assert.That(pasted.Min(n => n.Y), Is.EqualTo(304));
            Assert.That(pasted.Select(n => n.Id), Has.None.EqualTo(osc.Id).And.None.EqualTo(env.Id));
            Assert.That(m.Undo(), Is.True, "a paste is one step");
            Assert.That(rig.G.Nodes.Count, Is.EqualTo(nodes));
            // Wires between copied nodes come with them; duplicate does copy and paste.
            var keys = rig.G.Nodes.First(n => n.Kind == NodeKind.Keys);
            m.SelectMany(new[] { keys.Id, osc.Id });
            Assert.That(m.Duplicate(), Is.EqualTo(2));
            Assert.That(rig.G.Wires.Count, Is.EqualTo(wires + 1), "Keys to Osc was wired, so its copy is");
            Assert.That(PatchEditModel.ClipboardText, Does.StartWith("neopatch 1"));
            // Pasting into another patch.
            var other = new Rig();
            Assert.That(other.Model.Paste(0, 0), Is.EqualTo(2));
            Measure.Report("Neo Patch editor: copy, paste and duplicate keep the wires between the copied nodes, and paste into another patch");
        }

        [Test]
        public void PasteStopsAtTheNodeCap()
        {
            var rig = new Rig();
            var m = rig.Model;
            var first = m.AddNode(NodeKind.Osc, 0, 0);
            m.Copy();
            for (int i = 1; i < NodeInfo.Cap; i++) Assert.That(m.Paste(i * 8, 0), Is.EqualTo(1), "paste " + i);
            Assert.That(rig.G.Nodes.Count, Is.EqualTo(NodeInfo.Cap));
            Assert.That(m.Paste(0, 400), Is.EqualTo(0));
            Assert.That(m.AddNode(NodeKind.Osc, 0, 0), Is.Null);
            Assert.That(m.Notice, Does.Contain("limit"));
        }

        [Test]
        public void DeleteRemovesTheNodesTheirWiresAndMappingsInOneStep()
        {
            var rig = BasicVoice();
            var m = rig.Model;
            var env = rig.G.Nodes.First(n => n.Kind == NodeKind.Envelope);
            Assert.That(m.MapMacro(2, env.Id, NodeInfo.EnvR), Is.True);
            int wires = rig.G.Wires.Count;
            m.Select(env.Id);
            Assert.That(m.DeleteSelection(), Is.True);
            Assert.That(rig.G.Find(env.Id), Is.Null);
            Assert.That(rig.G.Wires.Count, Is.EqualTo(wires - 2));
            Assert.That(rig.G.Macros, Is.Empty);
            Assert.That(m.Undo(), Is.True);
            Assert.That(rig.G.Find(env.Id), Is.Not.Null);
            Assert.That(rig.G.Wires.Count, Is.EqualTo(wires));
            Assert.That(rig.G.Macros, Has.Count.EqualTo(1));
        }

        [Test]
        public void MacrosAreMappedRangedLabelledAndUnmapped()
        {
            var rig = new Rig(PatchTemplates.Core8());
            var m = rig.Model;
            var f = rig.G.Nodes.First(n => n.Kind == NodeKind.Filter);
            f.Set(NodeInfo.FilterCutoff, 1200);
            Assert.That(PatchEditModel.MacroName(rig.G, 4), Is.EqualTo("Macro 5"));
            Assert.That(m.MapMacro(4, f.Id, NodeInfo.FilterCutoff), Is.True);
            var map = m.MappingOf(f.Id, NodeInfo.FilterCutoff).Value;
            Assert.That(map.Macro, Is.EqualTo(4));
            Assert.That(map.Min, Is.EqualTo(1200).Within(0.5), "from where it is to the far end");
            Assert.That(map.Max, Is.EqualTo(20000));
            Assert.That(PatchEditModel.MacroName(rig.G, 4), Is.EqualTo("Cutoff"), "named by its first control");
            Assert.That(m.SetMapping(f.Id, NodeInfo.FilterCutoff, 300, 6000), Is.True);
            Assert.That(m.SetMacroLabel(4, "Brightness"), Is.True);
            Assert.That(PatchEditModel.MacroName(rig.G, 4), Is.EqualTo("Brightness"));
            var text = rig.Engine.Text();
            var back = PatchText.Load(text);
            Assert.That(back.MacroLabels[4], Is.EqualTo("Brightness"), "labels are saved with the patch");
            Assert.That(back.Macros.Single().Min, Is.EqualTo(300));
            // The runtime drives the control from the macro.
            rig.Engine.Runtime.SetMacro(4, 1f);
            var l = new float[64]; var r = new float[64];
            rig.Engine.Runtime.NoteOn(0, 60, 0.8f);
            for (int i = 0; i < 100; i++) rig.Engine.Runtime.ProcessBlock(l, r);
            Assert.That(rig.G.Find(f.Id).Controls[NodeInfo.FilterCutoff], Is.EqualTo(6000).Within(30));
            Assert.That(m.UnmapControl(f.Id, NodeInfo.FilterCutoff), Is.True);
            Assert.That(rig.G.Macros, Is.Empty);
            Assert.That(m.Undo(), Is.True);
            Assert.That(rig.G.Macros, Has.Count.EqualTo(1));
        }

        [Test]
        public void ControlFormattingUsesRealUnits()
        {
            var f = NodeInfo.For(NodeKind.Filter).Controls;
            Assert.That(ControlScale.Format(f[NodeInfo.FilterCutoff], 1200), Is.EqualTo("1.2 kHz"));
            Assert.That(ControlScale.Format(f[NodeInfo.FilterCutoff], 440), Is.EqualTo("440 Hz"));
            Assert.That(ControlScale.Format(f[NodeInfo.FilterMode], 4), Is.EqualTo("ladder 24"));
            var e = NodeInfo.For(NodeKind.Envelope).Controls;
            Assert.That(ControlScale.Format(e[NodeInfo.EnvA], 0.005f), Is.EqualTo("5 ms"));
            Assert.That(ControlScale.Format(e[NodeInfo.EnvR], 1.5f), Is.EqualTo("1.5 s"));
            var o = NodeInfo.For(NodeKind.Osc).Controls;
            Assert.That(ControlScale.Format(o[NodeInfo.OscTune], 7), Is.EqualTo("+7 st"));
            Assert.That(ControlScale.Format(o[NodeInfo.OscFine], -12), Is.EqualTo("-12 ct"));
            var d = NodeInfo.For(NodeKind.Delay).Controls;
            Assert.That(ControlScale.Format(d[NodeInfo.DelayTime], 250), Is.EqualTo("250 ms"));
            // Sliders: wide positive ranges are logarithmic, and a position maps back to the value.
            Assert.That(ControlScale.IsLog(f[NodeInfo.FilterCutoff]), Is.True);
            Assert.That(ControlScale.IsLog(f[NodeInfo.FilterReso]), Is.False);
            foreach (var kind in Enum.GetValues<NodeKind>())
                foreach (var c in NodeInfo.For(kind).Controls)
                    foreach (double t in new[] { 0.0, 0.3, 1.0 })
                    {
                        float v = ControlScale.FromUnit(c, t);
                        Assert.That(v, Is.InRange(c.Min, c.Max));
                        if (c.Choices == null) Assert.That(ControlScale.ToUnit(c, v), Is.EqualTo(t).Within(1e-4), c.Name);
                    }
        }

        [Test]
        public void ScopeIsTheCompilersAndAGlobalSwitchIsAnEdit()
        {
            var rig = BasicVoice();
            var m = rig.Model;
            var osc = rig.G.Nodes.First(n => n.Kind == NodeKind.Osc);
            var o = rig.G.Nodes.First(n => n.Kind == NodeKind.Out);
            Assert.That(m.IsGlobal(osc.Id), Is.False);
            Assert.That(m.IsGlobal(o.Id), Is.True);
            var lfo = m.AddNode(NodeKind.Lfo, 0, 400);
            Assert.That(m.IsHeard(lfo.Id), Is.False, "an LFO wired to nothing is not heard");
            Assert.That(m.SetGlobal(osc.Id, true), Is.True);
            Assert.That(m.IsGlobal(osc.Id), Is.True);
            Assert.That(m.SetGlobal(o.Id, false), Is.False, "Out is always global");
            Assert.That(m.SetGlobal(rig.G.Nodes.First(n => n.Kind == NodeKind.Keys).Id, true), Is.False, "Keys is always per voice");
        }

        // ------------------------------------------------------------------ the words

        [Test]
        public void EveryNodeControlAndPortHasItsWords()
        {
            int controls = 0, ports = 0;
            foreach (var kind in Enum.GetValues<NodeKind>())
            {
                var info = NodeInfo.For(kind);
                var doc = NodeHelp.For(kind);
                Assert.That(doc.Summary, Has.Length.GreaterThan(20), kind + " summary");
                Assert.That(doc.How, Has.Length.EqualTo(info.Controls.Length), kind + " How per control");
                Assert.That(doc.Sound, Has.Length.EqualTo(info.Controls.Length), kind + " Sound per control");
                Assert.That(doc.Ins, Has.Length.EqualTo(info.Ins.Length), kind + " words per input");
                Assert.That(doc.Outs, Has.Length.EqualTo(info.Outs.Length), kind + " words per output");
                for (int c = 0; c < info.Controls.Length; c++)
                {
                    Assert.That(doc.How[c], Has.Length.GreaterThan(20), $"{kind}.{info.Controls[c].Name} How");
                    Assert.That(doc.Sound[c], Has.Length.GreaterThan(20), $"{kind}.{info.Controls[c].Name} Sound");
                    Assert.That(doc.How[c], Does.EndWith("."), $"{kind}.{info.Controls[c].Name} How is a sentence");
                    Assert.That(doc.Sound[c], Does.EndWith("."), $"{kind}.{info.Controls[c].Name} Sound is a sentence");
                    controls++;
                }
                foreach (string s in doc.Ins.Concat(doc.Outs)) { Assert.That(s, Has.Length.GreaterThan(8), kind + " port words: " + s); ports++; }
            }
            foreach (PortType t in Enum.GetValues<PortType>()) Assert.That(NodeHelp.TypeWords(t), Does.StartWith(t + ":"));
            Measure.Report($"Neo Patch editor: {NodeInfo.KindCount} node kinds, {controls} controls with Explain How and Sound, {ports} ports with words");
        }

        [Test]
        public void TheWordsNameNoProduct()
        {
            // Clean room (neo-patch.md §2.9): none of the trademarked names the synth world is full of.
            string[] banned = { "moog", "roland", "korg", "yamaha", "arturia", "serum", "vital", "massive", "sylenth", "ableton", "buchla", "eurorack", "tb-303", "juno", "oberheim", "prophet", "minimoog", "dx7", "fl studio", "reaktor", "kontakt" };
            var all = new List<string>();
            foreach (var kind in Enum.GetValues<NodeKind>())
            {
                var doc = NodeHelp.For(kind);
                all.Add(NodeInfo.For(kind).Name);
                all.Add(doc.Summary);
                all.AddRange(doc.How); all.AddRange(doc.Sound); all.AddRange(doc.Ins); all.AddRange(doc.Outs);
            }
            foreach (string s in all) foreach (string b in banned) Assert.That(s.ToLowerInvariant(), Does.Not.Contain(b), s);
        }

        [Test]
        public void PortAndGroupColoursAreDistinctAndNeverAStateColour()
        {
            var ports = Enum.GetValues<PortType>().Select(PatchSurface.PortColour).ToArray();
            var groups = new[] { "Notes", "Sources", "Modulation", "Tone", "Plumbing" }.Select(PatchSurface.GroupColour).ToArray();
            foreach (var set in new[] { ports, groups })
                for (int i = 0; i < set.Length; i++)
                {
                    Assert.That(Tokens.IsReservedForState(set[i]), Is.False, set[i].ToString());
                    Assert.That(Tokens.ContrastOf(set[i], Tokens.SurfacePanel), Is.GreaterThan(3), "a control's part: 3:1 on the panel");
                    for (int j = i + 1; j < set.Length; j++) Assert.That(Tokens.DeltaE(set[i], set[j]), Is.GreaterThan(25), $"{i} and {j}");
                }
        }

        // ------------------------------------------------------------------ the surface

        [Test]
        public void AnEmptyPatchShowsTheThreeStepsAndOneClickMakesABasicVoice()
        {
            var rig = new Rig();
            var texts = rig.Texts();
            Assert.That(texts, Has.Some.EqualTo("An empty patch"));
            Assert.That(texts, Has.Some.StartsWith("1  Add Keys"));
            Assert.That(texts, Has.Some.EqualTo("Add a basic voice"));
            var s = rig.Surface.StarterRect;
            rig.Click(s.X + 10, s.Y + 10);
            Assert.That(rig.G.Nodes.Select(n => n.Kind), Is.EquivalentTo(new[] { NodeKind.Keys, NodeKind.Osc, NodeKind.Envelope, NodeKind.Vca, NodeKind.Out }));
            Assert.That(rig.G.Wires.Count, Is.EqualTo(5));
            Assert.That(rig.Model.Undo(), Is.True, "the starter is one step");
            Assert.That(rig.G.Nodes, Is.Empty);
            Assert.That(rig.Model.Undo(), Is.False);
            // And it makes sound: a note through the compiled patch.
            rig.Model.Redo();
            var l = new float[256]; var r = new float[256];
            rig.Engine.NoteOn(0, 60, 0.9f);
            float peak = 0;
            for (int b = 0; b < 40; b++) { rig.Engine.Process(l, r, 256); peak = Math.Max(peak, l.Concat(r).Max(Math.Abs)); }
            Assert.That(peak, Is.GreaterThan(0.01f), "the basic voice sounds");
            Measure.Report($"Neo Patch editor: the empty patch's basic voice (5 nodes, 5 wires) is one step and sounds (peak {peak:F2})");
        }

        [Test]
        public void TheBigPatchDrawsEveryNodeInsideTheCanvasAfterFit()
        {
            var g = PatchTemplates.Big20();
            int x = 0;
            foreach (var n in g.Nodes) { n.X = (x % 5) * 200; n.Y = (x / 5) * 150; x++; }
            var rig = new Rig(g);
            rig.Surface.Fit();
            rig.Draw();
            var canvas = rig.Surface.CanvasRect;
            foreach (var n in g.Nodes)
            {
                var r = rig.Surface.NodeRect(n);
                Assert.That(r.X, Is.GreaterThanOrEqualTo(canvas.X - 0.5), n.Info.Name);
                Assert.That(r.Right, Is.LessThanOrEqualTo(canvas.Right + 0.5));
                Assert.That(r.Y, Is.GreaterThanOrEqualTo(canvas.Y - 0.5));
                Assert.That(r.Bottom, Is.LessThanOrEqualTo(canvas.Bottom + 0.5));
            }
            var texts = rig.Texts();
            foreach (string name in new[] { "Keys", "Osc", "Envelope", "Filter", "VCA", "Out", "Feedback" }) Assert.That(texts, Has.Some.EqualTo(name), name);
            Assert.That(rig.Canvas.Unbalanced, Is.False);
            // The per-voice nodes carry the stacked edge: more rectangles than nodes and wires would explain; Out (global) has none.
            int perVoice = g.Nodes.Count(n => !rig.Model.IsGlobal(n.Id));
            Assert.That(perVoice, Is.EqualTo(g.Nodes.Count - 1), "everything but Out runs for each voice");
            Measure.Report($"Neo Patch editor: the 20-node patch fits the canvas at zoom {rig.Surface.Zoom:F2}; {perVoice} nodes drawn with a stacked edge");
        }

        [Test]
        public void ADragFromAnOutputDotToAnInputDotConnectsAndOneUndoRemovesIt()
        {
            var rig = new Rig();
            var keys = rig.Add(NodeKind.Keys, 0, 0);
            var osc = rig.Add(NodeKind.Osc, 256, 0);
            rig.Surface.Fit();
            Assert.That(rig.Surface.PortAt(rig.Surface.PortPos(keys, 0, true).X, rig.Surface.PortPos(keys, 0, true).Y)?.Port, Is.EqualTo(0));
            rig.DragWire(keys, 0, osc, NodeInfo.OscInPitch);
            Assert.That(rig.G.Wires.Select(WireKey.Of), Is.EquivalentTo(new[] { new WireKey(keys.Id, 0, osc.Id, NodeInfo.OscInPitch) }));
            Assert.That(rig.Model.SelectedWire, Is.Not.Null, "the new wire is selected");
            Assert.That(rig.Model.Undo(), Is.True);
            Assert.That(rig.G.Wires, Is.Empty);
            // From an input dot to an output dot works too.
            var a = rig.Surface.PortPos(osc, NodeInfo.OscInFm, false);
            var b = rig.Surface.PortPos(keys, 2, true);
            rig.Surface.Down(a.X, a.Y, 1, false);
            rig.Surface.Move(b.X, b.Y, false);
            rig.Surface.Up();
            Assert.That(rig.G.Wires.Select(WireKey.Of), Is.EquivalentTo(new[] { new WireKey(keys.Id, 2, osc.Id, NodeInfo.OscInFm) }));
            // Dropped on empty canvas: nothing.
            rig.Surface.Down(a.X, a.Y, 1, false);
            rig.Surface.Move(a.X - 40, a.Y + 200, false);
            rig.Surface.Up();
            Assert.That(rig.G.Wires.Count, Is.EqualTo(1));
        }

        [Test]
        public void ADraggedWireToACycleIsRefusedAndTheBannerSaysWhy()
        {
            var rig = new Rig();
            var f = rig.Add(NodeKind.Filter, 0, 0);
            var v = rig.Add(NodeKind.Vca, 256, 0);
            rig.Model.Connect(f.Id, 0, v.Id, NodeInfo.VcaInAudio);
            rig.Surface.Fit();
            var a = rig.Surface.PortPos(v, 0, true);
            var b = rig.Surface.PortPos(f, NodeInfo.FilterInAudio, false);
            rig.Surface.Down(a.X, a.Y, 1, false);
            rig.Surface.Move(b.X, b.Y, false);
            Assert.That(rig.Surface.HoverVerdict, Does.Contain("loop"), "while dragging over the dot, it already says why");
            var during = rig.Texts();
            Assert.That(during, Has.Some.Contain("would make a loop"));
            rig.Surface.Up();
            Assert.That(rig.G.Wires.Count, Is.EqualTo(1));
            var after = rig.Texts();
            Assert.That(after, Has.Some.Contain("Filter " + f.Id + " to VCA " + v.Id + " to Filter " + f.Id), "the banner names the loop");
            Assert.That(after, Has.Some.Contain("Feedback"));
            // It goes away.
            rig.Clock += PatchSurface.RefusalMs + 1;
            Assert.That(rig.Texts(), Has.None.Contain("would make a loop"));
        }

        [Test]
        public void TheRightClickMenuSearchesAndAddsANodeAtThePointer()
        {
            var rig = new Rig();
            var c = rig.Surface.CanvasRect;
            rig.Surface.RightDown(c.X + 300, c.Y + 200);
            Assert.That(rig.Surface.Menu, Is.Not.Null);
            Assert.That(rig.Surface.WantsText, Is.True);
            Assert.That(rig.Surface.Menu.Visible.Count(i => !i.Header), Is.EqualTo(NodeInfo.KindCount));
            rig.Surface.Text("fil");
            Assert.That(rig.Surface.Menu.Visible.Select(i => i.Label), Does.Contain("Filter"));
            Assert.That(rig.Surface.Menu.Visible.Count, Is.LessThan(8));
            rig.Surface.Text("ter ladder");
            Assert.That(rig.Surface.Menu.Visible.Select(i => i.Label), Is.EqualTo(new[] { "Filter" }), "the summary's words are searched too");
            var texts = rig.Texts();
            Assert.That(texts, Has.Some.EqualTo("Add a node"));
            Assert.That(texts, Has.Some.EqualTo("filter ladder"));
            rig.Surface.Key("Return", false, false);
            Assert.That(rig.Surface.Menu, Is.Null);
            var n = rig.G.Nodes.Single();
            Assert.That(n.Kind, Is.EqualTo(NodeKind.Filter));
            var at = rig.Surface.NodeRect(n);
            Assert.That(at.X, Is.EqualTo(c.X + 300).Within(8 * rig.Surface.Zoom));
            Assert.That(rig.Model.Selection, Is.EquivalentTo(new[] { n.Id }));
            // A name that matches comes before a description that does: "lfo" finds the LFO, not the node whose words mention it.
            rig.Surface.RightDown(c.X + 100, c.Y + 100);
            rig.Surface.Text("lfo");
            Assert.That(rig.Surface.Menu.Visible[0].Label, Is.EqualTo(NodeInfo.For(NodeKind.Lfo).Name));
            rig.Surface.Key("Escape", false, false);
            // Escape closes it; a click outside closes it; arrows move the highlight.
            rig.Surface.RightDown(c.X + 100, c.Y + 100);
            rig.Surface.Key("Down", false, false);
            rig.Surface.Key("Down", false, false);
            Assert.That(rig.Surface.Menu.Selected, Is.GreaterThan(1));
            rig.Surface.Key("Escape", false, false);
            Assert.That(rig.Surface.Menu, Is.Null);
            rig.Surface.RightDown(c.X + 100, c.Y + 100);
            rig.Surface.Down(c.X + 600, c.Y + 400, 1, false);
            Assert.That(rig.Surface.Menu, Is.Null);
            Assert.That(rig.G.Nodes.Count, Is.EqualTo(1), "the click that closed it added nothing");
        }

        [Test]
        public void AMenuRowPickedByTheMouseAddsThatNode()
        {
            var rig = new Rig();
            var c = rig.Surface.CanvasRect;
            rig.Surface.RightDown(c.X + 50, c.Y + 50);
            var menu = rig.Surface.Menu;
            int row = menu.Visible.FindIndex(i => i.Label == "Wavetable Osc");
            Assert.That(row, Is.GreaterThan(0));
            rig.Surface.Text("wavetable");
            row = menu.Visible.FindIndex(i => i.Label == "Wavetable Osc");
            var box = menu.Box(PatchSurface.Width, PatchSurface.Height);
            rig.Surface.Down(box.X + 40, box.Y + 26 + PatchMenu.SearchHeight + (row - menu.Scroll) * PatchMenu.RowHeight + 8, 1, false);
            Assert.That(rig.G.Nodes.Single().Kind, Is.EqualTo(NodeKind.WavetableOsc));
        }

        [Test]
        public void TheNodeMenuDuplicatesCopiesDeletesAndSwitchesScope()
        {
            var rig = BasicVoice();
            rig.Surface.Fit();
            var osc = rig.G.Nodes.First(n => n.Kind == NodeKind.Osc);
            var r = rig.Surface.NodeRect(osc);
            rig.Surface.RightDown(r.X + 40, r.Y + 10);
            Assert.That(rig.Surface.Menu.Visible.Select(i => i.Label), Is.EqualTo(new[] { "Duplicate", "Copy", "Delete", "Make global", "Disconnect all wires" }));
            Assert.That(rig.Surface.Menu.WantsText, Is.False);
            rig.Surface.Key("Down", false, false);
            rig.Surface.Key("Up", false, false);
            rig.Surface.Key("Return", false, false);       // Duplicate
            Assert.That(rig.G.Nodes.Count(n => n.Kind == NodeKind.Osc), Is.EqualTo(2));
            rig.Surface.Fit();
            osc = rig.G.Find(osc.Id);
            r = rig.Surface.NodeRect(osc);
            rig.Surface.RightDown(r.X + 40, r.Y + 10);
            var items = rig.Surface.Menu.Visible;
            rig.Surface.Down(rig.Surface.Menu.Box(PatchSurface.Width, PatchSurface.Height).X + 20, rig.Surface.Menu.Box(PatchSurface.Width, PatchSurface.Height).Y + 26 + 3 * PatchMenu.RowHeight + 8, 1, false);
            Assert.That(rig.G.Find(osc.Id).Global || rig.G.Nodes.Any(n => n.Kind == NodeKind.Osc && n.Global), Is.True, "Make global");
        }

        [Test]
        public void KeysDeleteCopyPasteDuplicateSelectAllAndArrows()
        {
            var rig = BasicVoice();
            var s = rig.Surface;
            s.Fit();
            Assert.That(s.Key("A", true, false), Is.True);
            Assert.That(rig.Model.Selection.Count, Is.EqualTo(5));
            Assert.That(s.Key("C", true, false), Is.True);
            Assert.That(s.Key("V", true, false), Is.True);
            Assert.That(rig.G.Nodes.Count, Is.EqualTo(10));
            Assert.That(rig.Model.Selection.Count, Is.EqualTo(5), "the pasted nodes are selected");
            var ids = rig.Model.Selection.ToArray();
            float x0 = rig.G.Find(ids[0]).X;
            s.Key("Right", false, false);
            Assert.That(rig.G.Find(ids[0]).X, Is.EqualTo(x0 + 8));
            s.Key("Down", false, true);
            Assert.That(rig.Model.Undo(), Is.True, "an arrow is a step");
            Assert.That(s.Key("Delete", false, false), Is.True);
            Assert.That(rig.G.Nodes.Count, Is.EqualTo(5));
            Assert.That(s.Key("A", true, false), Is.True);
            Assert.That(s.Key("D", true, false), Is.True);
            Assert.That(rig.G.Nodes.Count, Is.EqualTo(10));
            s.Key("Escape", false, false);
            Assert.That(rig.Model.Selection, Is.Empty);
        }

        [Test]
        public void ClickingSelectsDraggingMovesAndABoxSelects()
        {
            var rig = new Rig();
            var a = rig.Add(NodeKind.Osc, 0, 0);
            var b = rig.Add(NodeKind.Filter, 256, 0);
            var c = rig.Add(NodeKind.Out, 512, 0);
            rig.Surface.Fit();
            rig.ClickNode(a);
            Assert.That(rig.Model.Selection, Is.EquivalentTo(new[] { a.Id }));
            rig.ClickNode(b);
            Assert.That(rig.Model.Selection, Is.EquivalentTo(new[] { b.Id }));
            rig.Click(rig.Surface.NodeRect(c).X + 30, rig.Surface.NodeRect(c).Y + 10, fine: true);
            Assert.That(rig.Model.Selection, Is.EquivalentTo(new[] { b.Id, c.Id }), "Shift adds");
            // Drag the two.
            var r = rig.Surface.NodeRect(b);
            double z = rig.Surface.Zoom;
            float bx = b.X, cx = c.X;
            Node A() => rig.G.Find(a.Id); Node B() => rig.G.Find(b.Id); Node C() => rig.G.Find(c.Id);
            rig.Surface.Down(r.X + 30, r.Y + 10, 1, false);
            rig.Surface.Move(r.X + 30 + 64 * z, r.Y + 10 + 24 * z, false);
            rig.Surface.Up();
            Assert.That(B().X, Is.EqualTo(bx + 64).Within(8));
            Assert.That(C().X, Is.EqualTo(cx + 64).Within(8));
            Assert.That(A().X, Is.EqualTo(0), "the unselected one stays");
            Assert.That(rig.Model.Undo(), Is.True, "the drag is one step");
            Assert.That(B().X, Is.EqualTo(bx));
            // A box on empty canvas with Shift held.
            var canvas = rig.Surface.CanvasRect;
            rig.Surface.Down(canvas.X + 2, canvas.Y + canvas.Height - 6, 1, true);
            rig.Surface.Move(canvas.X + canvas.Width - 4, canvas.Y + 4, true);
            rig.Surface.Up();
            Assert.That(rig.Model.Selection.Count, Is.EqualTo(3));
            // A click on empty canvas clears; a drag on it pans.
            rig.Click(canvas.X + 4, canvas.Y + canvas.Height - 4);
            Assert.That(rig.Model.Selection, Is.Empty);
            double px = rig.Surface.PanX;
            rig.Surface.Down(canvas.X + 4, canvas.Y + canvas.Height - 4, 1, false);
            rig.Surface.Move(canvas.X + 54, canvas.Y + canvas.Height - 24, false);
            rig.Surface.Up();
            Assert.That(rig.Surface.PanX, Is.EqualTo(px + 50).Within(0.5));
        }

        [Test]
        public void TheWheelZoomsAroundThePointerAndTheViewIsSavedAsText()
        {
            var rig = new Rig();
            var n = rig.Add(NodeKind.Osc, 160, 120);
            var c = rig.Surface.CanvasRect;
            double px = c.X + 400, py = c.Y + 250;
            var before = rig.Surface.ToWorld(px, py);
            for (int i = 0; i < 5; i++) rig.Surface.Wheel(px, py, 120, false);
            Assert.That(rig.Surface.Zoom, Is.GreaterThan(1.5));
            var after = rig.Surface.ToWorld(px, py);
            Assert.That(after.X, Is.EqualTo(before.X).Within(1e-6));
            Assert.That(after.Y, Is.EqualTo(before.Y).Within(1e-6));
            for (int i = 0; i < 60; i++) rig.Surface.Wheel(px, py, -120, false);
            Assert.That(rig.Surface.Zoom, Is.EqualTo(PatchSurface.MinZoom));
            // The view text round-trips and refuses nonsense.
            rig.Surface.ZoomBy(2, px, py);
            string text = rig.Surface.ViewText;
            var other = new Rig();
            other.Surface.ViewText = text;
            Assert.That(other.Surface.Zoom, Is.EqualTo(rig.Surface.Zoom).Within(0.001));
            Assert.That(other.Surface.PanX, Is.EqualTo(rig.Surface.PanX).Within(0.1));
            other.Surface.ViewText = "nonsense";
            other.Surface.ViewText = "NaN 1 1";
            Assert.That(other.Surface.Zoom, Is.EqualTo(rig.Surface.Zoom).Within(0.001));
            // Shift and the wheel pans.
            double pan = rig.Surface.PanX;
            rig.Surface.Wheel(px, py, 120, true);
            Assert.That(rig.Surface.PanX, Is.Not.EqualTo(pan));
            Measure.Report("Neo Patch editor: wheel zoom keeps the point under the pointer; the view saves as text");
        }

        [Test]
        public void TheSidePanelEditsTheSelectedNodesControlsInRealUnitsWithExplain()
        {
            var rig = new Rig(PatchTemplates.Core8());
            var f = rig.G.Nodes.First(n => n.Kind == NodeKind.Filter);
            rig.Surface.Fit();
            rig.ClickNode(f);
            var texts = rig.Texts();
            Assert.That(texts, Has.Some.EqualTo("Filter"));
            Assert.That(texts, Has.Some.EqualTo("1.2 kHz"), "Cutoff in real units: " + string.Join("|", texts));
            Assert.That(texts, Has.Some.EqualTo("Q 1.2"));
            Assert.That(texts, Has.Some.StartsWith("Touch a control to see what it does"));
            var item = rig.Surface.PanelItems.First(i => i.Kind == PatchSurface.PK.Slider && i.Control == NodeInfo.FilterCutoff);
            // Hover: the Explain box shows How and Sound.
            rig.Surface.Move(item.Rect.X + 10, item.Rect.Y + 6, false);
            var hover = rig.Texts();
            var doc = NodeHelp.For(NodeKind.Filter);
            Assert.That(hover, Has.Some.EqualTo("Filter · Cutoff"));
            Assert.That(hover.Any(t => t.StartsWith("How " + doc.How[NodeInfo.FilterCutoff].Substring(0, 20))), Is.True);
            Assert.That(hover.Any(t => t.StartsWith("Sound " + doc.Sound[NodeInfo.FilterCutoff].Substring(0, 20))), Is.True);
            Assert.That(rig.Surface.ExplainNow().How, Is.EqualTo(doc.How[NodeInfo.FilterCutoff]));
            // Drag the slider to the middle: log scale, 20 Hz to 20 kHz, so about 632 Hz; one step.
            var compiled = rig.Engine.Runtime.Current;
            rig.Surface.Down(item.Rect.X + 2, item.Rect.Y + 6, 1, false);
            rig.Surface.Move(item.Rect.X + item.Rect.Width / 2, item.Rect.Y + 6, false);
            rig.Surface.Up();
            Assert.That(rig.G.Find(f.Id).Controls[NodeInfo.FilterCutoff], Is.EqualTo(632).Within(20));
            Assert.That(rig.Engine.Runtime.Current, Is.SameAs(compiled), "no compile for a slider");
            Assert.That(rig.Model.Undo(), Is.True);
            Assert.That(rig.G.Find(f.Id).Controls[NodeInfo.FilterCutoff], Is.EqualTo(1200));
            // A double-click puts it back at its default.
            rig.Click(item.Rect.X + 30, item.Rect.Y + 6, clicks: 2);
            Assert.That(rig.G.Find(f.Id).Controls[NodeInfo.FilterCutoff], Is.EqualTo(2000));
            // A choice: the mode, a dropdown of six.
            var mode = rig.Surface.PanelItems.First(i => i.Control == NodeInfo.FilterMode && i.Kind != PatchSurface.PK.Scope && i.Kind != PatchSurface.PK.MapChip);
            Assert.That(mode.Kind, Is.EqualTo(PatchSurface.PK.Dropdown));
            rig.Click(mode.Rect.X + 20, mode.Rect.Y + 6);
            Assert.That(rig.Surface.Menu.Visible.Select(i => i.Label), Is.EqualTo(new[] { "low-pass 12", "band-pass", "high-pass", "notch", "ladder 24", "ladder 12" }));
            rig.Surface.Key("Down", false, false); rig.Surface.Key("Down", false, false); rig.Surface.Key("Return", false, false);
            Assert.That(rig.G.Find(f.Id).Controls[NodeInfo.FilterMode], Is.EqualTo(2));
            // Few choices: chips.
            var osc = rig.G.Nodes.First(n => n.Kind == NodeKind.Osc);
            rig.ClickNode(osc);
            rig.Draw();
            var chips = rig.Surface.PanelItems.Where(i => i.Kind == PatchSurface.PK.Chip && i.Control == NodeInfo.OscShape).ToList();
            Assert.That(chips.Count, Is.EqualTo(4));
            rig.Click(chips[2].Rect.X + 4, chips[2].Rect.Y + 4);
            Assert.That(rig.G.Find(osc.Id).Controls[NodeInfo.OscShape], Is.EqualTo(2));
        }

        [Test]
        public void EveryControlOfEveryNodeShowsInThePanelWithItsExplain()
        {
            int shown = 0;
            foreach (var kind in Enum.GetValues<NodeKind>())
            {
                var rig = new Rig();
                var n = rig.Add(kind, 0, 0);
                var info = NodeInfo.For(kind);
                var seen = new HashSet<int>();
                // A long list scrolls above the Duplicate and Delete bar: wheel through it, checking each control as it comes into view.
                for (int page = 0; page < 12 && seen.Count < info.Controls.Length; page++)
                {
                    rig.Draw();
                    foreach (var it in rig.Surface.PanelItems.Where(i => i.Kind == PatchSurface.PK.Slider || i.Kind == PatchSurface.PK.Chip || i.Kind == PatchSurface.PK.Dropdown).GroupBy(i => i.Control).Select(g => g.First()))
                    {
                        Assert.That(it.Rect.Bottom, Is.LessThanOrEqualTo(rig.Surface.PanelRect.Bottom - 124 - 34 + 12), $"{kind} control {it.Control} is above the bar");
                        rig.Surface.Move(it.Rect.X + 4, it.Rect.Y + 4, false);
                        var texts = rig.Texts();
                        Assert.That(texts, Has.Some.EqualTo(info.Name + " · " + info.Controls[it.Control].Name), $"{kind}.{it.Control}");
                        Assert.That(texts.Any(t => t.StartsWith("How ")), Is.True);
                        Assert.That(texts.Any(t => t.StartsWith("Sound ")), Is.True);
                        if (seen.Add(it.Control)) shown++;
                    }
                    var p = rig.Surface.PanelRect;
                    rig.Surface.Wheel(p.X + 20, p.Y + 20, -120, false);
                }
                Assert.That(seen.Count, Is.EqualTo(info.Controls.Length), kind + ": every control can be reached");
                rig.Draw();
                Assert.That(rig.Surface.PanelItems.Any(i => i.Id == "duplicate") && rig.Surface.PanelItems.Any(i => i.Id == "delete"), Is.True, kind + ": the bar stays");
            }
            Measure.Report($"Neo Patch editor: all {shown} controls of all node kinds are reachable in the side panel, each with How and Sound");
        }

        [Test]
        public void TheMacroStripShowsSixteenMacrosAndMapsControlsThroughTheMChip()
        {
            var rig = new Rig(PatchTemplates.Core8());
            var f = rig.G.Nodes.First(n => n.Kind == NodeKind.Filter);
            rig.Surface.Fit();
            var texts = rig.Texts();
            for (int i = 1; i <= 16; i++) Assert.That(texts, Has.Some.EqualTo(i.ToString()), "macro " + i);
            Assert.That(texts.Count(t => t == "no mapping"), Is.EqualTo(16));
            // Map the cutoff to macro 3 through the chip beside it.
            rig.ClickNode(f);
            rig.Draw();
            var chip = rig.Surface.PanelItems.First(i => i.Kind == PatchSurface.PK.MapChip && i.Control == NodeInfo.FilterCutoff);
            rig.Click(chip.Rect.X + 4, chip.Rect.Y + 4);
            Assert.That(rig.Surface.Menu.Visible.Count, Is.EqualTo(16));
            var box = rig.Surface.Menu.Box(PatchSurface.Width, PatchSurface.Height);
            rig.Surface.Key("Down", false, false); rig.Surface.Key("Down", false, false);
            rig.Surface.Key("Return", false, false);
            Assert.That(rig.Model.MappingOf(f.Id, NodeInfo.FilterCutoff).Value.Macro, Is.EqualTo(2));
            texts = rig.Texts();
            Assert.That(texts, Has.Some.EqualTo("M3"));
            Assert.That(texts, Has.Some.EqualTo("1 control"));
            Assert.That(texts.Count(t => t == "Cutoff"), Is.GreaterThanOrEqualTo(1), "the macro is labelled by its control");
            // Dragging on the macro's bar sets its value (the machine's parameter), and selects the macro: the panel lists the mapping.
            var cell = rig.Surface.MacroCell(2);
            rig.Surface.Down(cell.X + 4, cell.Y + 26, 1, false);
            rig.Surface.Move(cell.X + cell.Width - 4, cell.Y + 26, false);
            rig.Surface.Up();
            Assert.That(rig.Host.Macros[2], Is.EqualTo(1f).Within(0.02f));
            Assert.That(rig.Host.Gestures, Is.EqualTo(1));
            Assert.That(rig.Model.SelectedMacro, Is.EqualTo(2));
            texts = rig.Texts();
            Assert.That(texts, Has.Some.EqualTo("Macro 3"));
            Assert.That(texts, Has.Some.EqualTo("Filter " + f.Id + " · Cutoff"));
            // Its range is edited by dragging the low end.
            var range = rig.Surface.PanelItems.First(i => i.Kind == PatchSurface.PK.MapRange);
            rig.Surface.Down(range.Rect.X + 2, range.Rect.Y + 6, 1, false);
            rig.Surface.Move(range.Rect.X + range.Rect.Width / 2, range.Rect.Y + 6, false);
            rig.Surface.Up();
            Assert.That(rig.Model.MappingOf(f.Id, NodeInfo.FilterCutoff).Value.Min, Is.GreaterThan(300));
            // The name is typed.
            rig.Surface.Down(cell.X + 4, cell.Y + 4, 1, false); rig.Surface.Up();
            var rename = rig.Surface.PanelItems.First(i => i.Id == "renamemacro");
            rig.Click(rename.Rect.X + 10, rename.Rect.Y + 6);
            Assert.That(rig.Surface.Menu.Prompt, Is.Not.Null);
            rig.Surface.Text("Bright");
            rig.Surface.Key("Return", false, false);
            Assert.That(rig.G.MacroLabels[2], Is.EqualTo("Bright"));
            Assert.That(rig.Texts(), Has.Some.EqualTo("Bright"));
            // Remove the mapping from the panel.
            var remove = rig.Surface.PanelItems.First(i => i.Kind == PatchSurface.PK.MapRemove);
            rig.Click(remove.Rect.X + 4, remove.Rect.Y + 4);
            Assert.That(rig.G.Macros, Is.Empty);
        }

        [Test]
        public void TheLearnOverlayNamesAWiresSignalOnHover()
        {
            var rig = BasicVoice();
            rig.Surface.Fit();
            var keys = rig.G.Nodes.First(n => n.Kind == NodeKind.Keys);
            var wire = rig.G.Wires.First(w => w.FromNode == keys.Id && w.FromPort == 1);   // Keys Gate to the Envelope
            var pts = rig.Surface.WireCurve(wire);
            var mid = pts[pts.Length / 2];
            rig.Surface.Move(mid.X, mid.Y, false);
            Assert.That(rig.Surface.HoverWire, Is.Not.Null);
            Assert.That(rig.Texts(), Has.None.StartsWith("Gate: "), "off: no callout");
            rig.Surface.Learn = true;
            var texts = rig.Texts();
            Assert.That(texts, Has.Some.Contain("Keys " + keys.Id + " · Gate"));
            Assert.That(texts, Has.Some.StartsWith("Gate: 1 while a key is held"));
            Assert.That(texts, Has.Some.Contain("Carries: 1 while the key is held"));
            Assert.That(texts, Has.Some.EqualTo("Gate"), "and every wire says its type at its middle");
            Assert.That(texts, Has.Some.EqualTo("Audio"));
            // The Learn button is in the toolbar and toggles it.
            rig.Surface.Learn = false;
            var learn = rig.Surface.Buttons().First(b => b.Id == "learn").Rect;
            rig.Click(learn.X + 4, learn.Y + 4);
            Assert.That(rig.Surface.Learn, Is.True);
            // A port too.
            var p = rig.Surface.PortPos(keys, 0, true);
            rig.Surface.Move(p.X, p.Y, false);
            Assert.That(rig.Texts(), Has.Some.StartsWith("Pitch: a note number in semitones"));
        }

        [Test]
        public void TheCpuReadoutAndTheNodeCountAreInTheToolbar()
        {
            var rig = BasicVoice();
            var l = new float[256]; var r = new float[256];
            rig.Engine.NoteOn(0, 60, 0.9f);
            for (int b = 0; b < 300; b++) rig.Engine.Process(l, r, 256);
            float cpu = rig.Engine.CpuPercent;
            Assert.That(cpu, Is.GreaterThan(0).And.LessThan(100));
            Assert.That(rig.Engine.Runtime.CpuPeak, Is.GreaterThanOrEqualTo(rig.Engine.Runtime.CpuLoad));
            var texts = rig.Texts();
            Assert.That(texts.Any(t => t.StartsWith("CPU ") && t.EndsWith(" %")), Is.True);
            Assert.That(texts, Has.Some.EqualTo("5/128 nodes"));
            Measure.Report($"Neo Patch editor: the CPU readout of the basic voice, one note: {cpu:F2} % ({RuntimeInfoText()})");
        }

        static string RuntimeInfoText() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

        [Test]
        public void AWireIsSelectedDeletedAndItsAmountEdited()
        {
            var rig = BasicVoice();
            rig.Surface.Fit();
            var wire = rig.G.Wires.First();
            var pts = rig.Surface.WireCurve(wire);
            var mid = pts[pts.Length / 2];
            rig.Click(mid.X, mid.Y);
            Assert.That(rig.Model.SelectedWire, Is.EqualTo(WireKey.Of(wire)));
            rig.Draw();
            var amount = rig.Surface.PanelItems.First(i => i.Kind == PatchSurface.PK.WireAmount);
            rig.Surface.Down(amount.Rect.X + amount.Rect.Width * 0.25 + 2, amount.Rect.Y + 6, 1, false);
            rig.Surface.Up();
            Assert.That(rig.G.Wires.First(w => WireKey.Of(w).Equals(WireKey.Of(wire))).Amount, Is.EqualTo(-1f).Within(0.1));
            Assert.That(rig.Texts(), Has.Some.Contain("×-0.9"));
            Assert.That(rig.Model.Undo(), Is.True);
            Assert.That(rig.G.Wires.First(w => WireKey.Of(w).Equals(WireKey.Of(wire))).Amount, Is.EqualTo(1f));
            Assert.That(rig.Surface.Key("Delete", false, false), Is.True);
            Assert.That(rig.G.Wires.Count, Is.EqualTo(4));
            // The wire's right-click menu.
            wire = rig.G.Wires.First();
            pts = rig.Surface.WireCurve(wire);
            mid = pts[pts.Length / 2];
            rig.Surface.RightDown(mid.X, mid.Y);
            Assert.That(rig.Surface.Menu.Visible.Select(i => i.Label), Does.Contain("Delete wire"));
        }

        [Test]
        public void TheSurfaceDrawsInBalancedClipsAtEveryZoomAndNeverThrows()
        {
            var rig = new Rig(PatchTemplates.Big20());
            foreach (double z in new[] { 0.25, 0.4, 0.6, 1.0, 2.5 })
            {
                rig.Surface.ViewText = $"{z} 10 10";
                rig.Draw();
                Assert.That(rig.Canvas.Unbalanced, Is.False, "zoom " + z);
                Assert.That(rig.Canvas.ClipDepth, Is.EqualTo(0));
            }
            // Pointer events before any draw, on the edges, and outside, are quiet.
            var fresh = new Rig(PatchTemplates.Core8());
            Assert.DoesNotThrow(() =>
            {
                fresh.Surface.Move(-5, -5, false);
                fresh.Surface.Down(PatchSurface.Width + 10, 5, 1, false);
                fresh.Surface.Up();
                fresh.Surface.RightDown(0, 0);
                fresh.Surface.Key("Z", true, false);
                fresh.Surface.Leave();
                fresh.Surface.Wheel(3, 3, 120, false);
                fresh.Surface.Text("x");
            });
        }

        // ------------------------------------------------------------------ the audio thread

        [Test]
        public void EditsFromTheEditorSwapInAtABlockBoundaryAndTheAudioThreadAllocatesNothing()
        {
            var rig = new Rig(PatchTemplates.Core8());
            var rt = rig.Engine.Runtime;
            rt.Voices = 8;
            var l = new float[64]; var r = new float[64];
            var model = rig.Model;
            var stop = false;
            int edits = 0;
            // The "UI thread": every kind of edit, as the editor makes them, while the audio thread runs blocks.
            var ui = new Thread(() =>
            {
                var rnd = new Random(5);
                while (!stop)
                {
                    var g = rig.G;
                    var f = g.Nodes.First(n => n.Kind == NodeKind.Filter);
                    var o = model.AddNode(NodeKind.Osc, rnd.Next(0, 400), rnd.Next(0, 400));
                    if (o != null)
                    {
                        var vca = rig.G.Nodes.First(n => n.Kind == NodeKind.Vca);
                        model.Connect(o.Id, 0, f.Id, NodeInfo.FilterInAudio);
                        model.SetControl(o.Id, NodeInfo.OscTune, rnd.Next(-12, 12));
                        model.MoveBy(8, 8);
                        model.Undo(); model.Undo(); model.Undo(); model.Undo();
                    }
                    model.SetControl(f.Id, NodeInfo.FilterCutoff, 300 + rnd.Next(0, 4000));
                    edits++;
                    Thread.Sleep(2);
                }
            });
            for (int n = 0; n < 4; n++) rt.NoteOn(n, 48 + 4 * n, 0.8f);
            ui.Start();
            // Rounds of blocks until the editing thread has made enough rounds (a fast machine gets through 2500 blocks in a few ms).
            long bytes = 0; string why = null;
            for (int round = 0; round < 400 && (edits < 8 || round < 2); round++)
            {
                var (b, w) = AllocationCheck.Run(2500, k => rt.ProcessBlock(l, r));
                bytes += b;
                if (b != 0) why = w;
                Thread.Sleep(1);
            }
            stop = true;
            ui.Join();
            Measure.Report($"Neo Patch editor: audio thread, blocks while the editor made {edits} rounds of add, connect, set, move and undo: {bytes} bytes");
            Assert.That(edits, Is.GreaterThan(5), "the editing thread ran");
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void AnEditUnderHeldNotesKeepsTheSoundGoing()
        {
            // Adding a node that is not wired to anything audible leaves the sound sample for sample (nodes with their ids keep their state).
            var rig = new Rig(PatchTemplates.Core8());
            var l = new float[64]; var r = new float[64];
            rig.Engine.Runtime.NoteOn(0, 60, 0.8f);
            for (int b = 0; b < 100; b++) rig.Engine.Runtime.ProcessBlock(l, r);
            var reference = new Rig(PatchTemplates.Core8());
            reference.Engine.Runtime.NoteOn(0, 60, 0.8f);
            for (int b = 0; b < 100; b++) reference.Engine.Runtime.ProcessBlock(l, r);
            rig.Model.AddNode(NodeKind.Lfo, 600, 600);
            double worst = 0;
            var l2 = new float[64]; var r2 = new float[64];
            for (int b = 0; b < 20; b++)
            {
                rig.Engine.Runtime.ProcessBlock(l, r);
                reference.Engine.Runtime.ProcessBlock(l2, r2);
                for (int i = 0; i < 64; i++) worst = Math.Max(worst, Math.Abs(l[i] - l2[i]));
            }
            Measure.Report($"Neo Patch editor: adding a node under a held note: largest difference {worst:E2}");
            Assert.That(worst, Is.LessThan(2e-3));
        }

        // ------------------------------------------------------------------ the saved state

        [Test]
        public void TheEditorStateSavesWithThePatchAndReloads()
        {
            var rig = BasicVoice();
            var m = rig.Model;
            var osc = rig.G.Nodes.First(n => n.Kind == NodeKind.Osc);
            m.MoveBy(0, 0);
            m.Select(osc.Id);
            m.MoveBy(40, 16);
            m.SetControl(osc.Id, NodeInfo.OscTune, 7);
            m.MapMacro(0, osc.Id, NodeInfo.OscFine);
            m.SetMacroLabel(0, "Detune");
            m.Rename("My lead");
            rig.Surface.ZoomBy(1.5, 400, 300);
            string text = rig.Engine.Text(), view = rig.Surface.ViewText;
            // A new engine and editor from the saved text and view.
            var again = new Rig();
            Assert.That(again.Engine.SetPatch(text), Is.Null);
            again.Surface.ViewText = view;
            var back = again.G.Nodes.First(n => n.Kind == NodeKind.Osc);
            Assert.That(back.X, Is.EqualTo(osc.X));
            Assert.That(back.Y, Is.EqualTo(osc.Y));
            Assert.That(back.Controls[NodeInfo.OscTune], Is.EqualTo(7));
            Assert.That(again.G.Name, Is.EqualTo("My lead"));
            Assert.That(again.G.MacroLabels[0], Is.EqualTo("Detune"));
            Assert.That(again.G.Macros.Single().Control, Is.EqualTo(NodeInfo.OscFine));
            Assert.That(again.G.Wires.Count, Is.EqualTo(5));
            Assert.That(again.Surface.Zoom, Is.EqualTo(rig.Surface.Zoom).Within(0.001));
            Assert.That(again.Surface.PanX, Is.EqualTo(rig.Surface.PanX).Within(0.1));
            Assert.That(again.Engine.Text(), Is.EqualTo(text), "text round-trips exactly");
            // An old song's text (no labels) loads; a text with a bad label record is skipped.
            Assert.That(PatchText.Load("neopatch 1;n 1 Osc 0 0 0;l 99 x;l x").Nodes.Count, Is.EqualTo(1));
        }

        [Test]
        public void ANodeStackPatchIsLaidOutInColumnsAndTidyIsOneStep()
        {
            var g = PatchTemplates.Big20();
            foreach (var n in g.Nodes) { n.X = 0; n.Y = 0; }
            var rig = new Rig(g);                       // the rig tidies a stacked patch as the editor does on open
            var nodes = rig.G.Nodes;
            for (int i = 0; i < nodes.Count; i++)
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    var a = rig.Surface.NodeRect(nodes[i]); var b = rig.Surface.NodeRect(nodes[j]);
                    Assert.That(a.Overlaps(b), Is.False, $"{nodes[i].Info.Name} {nodes[i].Id} and {nodes[j].Info.Name} {nodes[j].Id}");
                }
            Assert.That(rig.Model.CanUndo, Is.False, "opening a patch is not an edit");
            var keys = nodes.First(n => n.Kind == NodeKind.Keys);
            var o = nodes.First(n => n.Kind == NodeKind.Out);
            Assert.That(keys.X, Is.LessThan(o.X), "the signal flows left to right");
            rig.Model.MoveBy(0, 0);
            rig.Model.Select(keys.Id);
            rig.Model.MoveBy(400, 400);
            rig.Draw();
            var tidy = rig.Surface.Buttons().First(b => b.Id == "tidy").Rect;
            rig.Click(tidy.X + 4, tidy.Y + 4);
            Assert.That(rig.G.Find(keys.Id).X, Is.EqualTo(keys.X));
            Assert.That(rig.Model.Undo(), Is.True);
            Assert.That(rig.G.Find(keys.Id).X, Is.EqualTo(keys.X + 400), "Tidy was one step");
        }
    }
}
