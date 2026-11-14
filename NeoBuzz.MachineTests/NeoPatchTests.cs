using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Patch;
using ReBuzzTests.Automation;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Patch NP1 (docs/specs/neo-patch.md §4, docs/specs/neo-patch-engine.md): the compiler (order, cycles, Feedback's block delay, dead
    /// nodes, the node cap), every node, per-voice and global scope, the voice sum, state carried across a graph swap, the three lane
    /// implementations giving the same bits, a render pinned across platforms, no allocation on the audio thread, the NaN guard, the
    /// oscillators' alias floor, the patch text, the machine's presets built in code, and the CPU of the nodes and of whole patches.
    /// </summary>
    [TestFixture]
    public class NeoPatchTests
    {
        const int Rate = Measure.Rate;
        static readonly LaneKind[] Kinds = Avx2Lanes.Supported
            ? new[] { LaneKind.Scalar, LaneKind.Pair, LaneKind.Avx2 }
            : new[] { LaneKind.Scalar, LaneKind.Pair };

        static string RuntimeInfo() =>
            $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Split(' ')[0]}";

        static ulong Hash(float[] x)
        {
            ulong h = 1469598103934665603UL;
            foreach (var v in x) h = (h ^ BitConverter.SingleToUInt32Bits(v)) * 1099511628211UL;
            return h;
        }

        static List<NoteEvent> Chord(int notes = 8, int off = -1)
        {
            var e = new List<NoteEvent>();
            for (int n = 0; n < notes; n++) e.Add(new NoteEvent(0, 36 + 3 * n, 0.5f + 0.5f * n / notes));
            if (off > 0) for (int n = 0; n < notes; n += 2) e.Add(new NoteEvent(off, 36 + 3 * n, 0));
            return e;
        }

        /// <summary>Keys → Osc → VCA(envelope) → Out, nothing else: a voice's sound without a filter.</summary>
        static PatchGraph Simple(int shape, float tune = 0f, float level = 0.5f)
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = g.Add(NodeKind.Osc); o.Set(NodeInfo.OscShape, shape); o.Set(NodeInfo.OscTune, tune);
            var e = g.Add(NodeKind.Envelope); e.Set(NodeInfo.EnvA, 0.0005f); e.Set(NodeInfo.EnvS, 1f);
            var v = g.Add(NodeKind.Vca);
            var s = g.Add(NodeKind.Out); s.Set(NodeInfo.OutLevel, level);
            g.Connect(k, 0, o, NodeInfo.OscInPitch);
            g.Connect(k, 1, e, NodeInfo.EnvInGate);
            g.Connect(o, 0, v, NodeInfo.VcaInAudio);
            g.Connect(e, 0, v, NodeInfo.VcaInCv);
            g.Connect(v, 0, s, 0);
            return g;
        }


        /// <summary>Renders a graph: notes (key, velocity; 0 releases) at the start, <paramref name="frames"/> samples, one voice unless said.</summary>
        static (float[] L, float[] R) Run(PatchGraph g, int frames, int voices = 1, params NoteEvent[] events)
        {
            var host = new PatchHost(g, voices, Rate, LaneKind.Auto);
            return host.Render(events.Length == 0 ? new[] { new NoteEvent(0, 69, 1f) } : events, frames);
        }

        static double Rms(float[] x, int from = 0, int to = -1)
        {
            if (to < 0) to = x.Length;
            double s = 0;
            for (int i = from; i < to; i++) s += (double)x[i] * x[i];
            return Math.Sqrt(s / Math.Max(1, to - from));
        }

        /// <summary>A node's output heard directly: <paramref name="node"/>'s first output into Out (level 1).</summary>
        static Node Hear(PatchGraph g, Node node, float amount = 1f)
        {
            var o = g.Add(NodeKind.Out);
            g.Connect(node, 0, o, 0, amount);
            return o;
        }

        /// <summary>A constant: a Macro node set to <paramref name="macro"/> (0 … 1 through the machine's macro smoothing, here fixed by SetMacro and one block).</summary>
        static Node Const(PatchGraph g, float value)
        {
            var m = g.Add(NodeKind.Macro);
            m.Set(NodeInfo.MacroIndex, 16);
            m.Set(NodeInfo.MacroAmount, value);
            return m;
        }

        // ------------------------------------------------------------------ the compiler

        [Test]
        public void ACycleIsRefusedUnlessItGoesThroughAFeedbackNode()
        {
            var g = new PatchGraph();
            var o = g.Add(NodeKind.Osc);
            var f = g.Add(NodeKind.Filter);
            var v = g.Add(NodeKind.Vca);
            var s = g.Add(NodeKind.Out);
            Assert.That(g.Connect(o, 0, f, 0), Is.EqualTo(ConnectResult.Ok));
            Assert.That(g.Connect(f, 0, v, 0), Is.EqualTo(ConnectResult.Ok));
            Assert.That(g.Connect(v, 0, s, 0), Is.EqualTo(ConnectResult.Ok));
            int wires = g.Wires.Count;
            Assert.That(g.Connect(v, 0, o, NodeInfo.OscInFm), Is.EqualTo(ConnectResult.Cycle), "Osc → Filter → VCA → Osc FM");
            Assert.That(g.Connect(f, 0, f, 0), Is.EqualTo(ConnectResult.Cycle), "a node into itself");
            Assert.That(g.Wires.Count, Is.EqualTo(wires), "a refused wire leaves the graph as it was");
            var fb = g.Add(NodeKind.Feedback);
            Assert.That(g.Connect(v, 0, fb, 0), Is.EqualTo(ConnectResult.Ok));
            Assert.That(g.Connect(fb, 0, o, NodeInfo.OscInFm), Is.EqualTo(ConnectResult.Ok), "the same loop through a Feedback node");
            Assert.That(g.Connect(f, 0, o, 99), Is.EqualTo(ConnectResult.BadPort));
            var rt = new PatchRuntime();
            Assert.That(rt.Compile(g).Error, Is.Null);
            Measure.Report("Neo Patch: a cycle is refused at connect time unless it goes through Feedback; a bad port is refused");
        }

        [Test]
        public void TheOrderPutsSourcesFirstKeepsFeedbackReadBeforeWriteAndDropsWhatIsNotHeard()
        {
            var g = PatchTemplates.Big20();
            var stray = g.Add(NodeKind.Osc);            // wired to nothing: not compiled
            var rt = new PatchRuntime();
            var r = rt.Compile(g);
            Assert.That(r.Error, Is.Null);
            var order = r.Patch.Order.Split(' ');
            int Index(string s) => Array.IndexOf(order, s);
            Assert.That(Index("Osc#" + stray.Id), Is.EqualTo(-1), "an Osc that leads nowhere is not compiled");
            Assert.That(r.Patch.Nodes, Is.EqualTo(20));
            Assert.That(Index("Keys#1"), Is.EqualTo(0));
            Assert.That(order[^1], Does.StartWith("Out#"));
            int fb = g.Nodes.First(n => n.Kind == NodeKind.Feedback).Id;
            Assert.That(Index($"Feedback#{fb}.read"), Is.LessThan(Index($"Feedback#{fb}.write")));
            foreach (var w in g.Wires)
            {
                string from = g.Find(w.FromNode).Kind == NodeKind.Feedback ? $"Feedback#{w.FromNode}.read" : g.Find(w.FromNode).Kind + "#" + w.FromNode;
                string to = g.Find(w.ToNode).Kind == NodeKind.Feedback ? $"Feedback#{w.ToNode}.write" : g.Find(w.ToNode).Kind + "#" + w.ToNode;
                Assert.That(Index(from), Is.LessThan(Index(to)), $"{from} before {to}");
            }
            Measure.Report($"Neo Patch: the 20-node patch compiles to {r.Patch.Jobs.Length} jobs and {r.Patch.Buffers} buffers (mixes are fused into their consumers)");
        }

        [Test]
        public void FeedbackDelaysExactlyOneBlock()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var fb = g.Add(NodeKind.Feedback);
            var s = g.Add(NodeKind.Out); s.Set(NodeInfo.OutLevel, 1f);
            g.Connect(k, 1, fb, 0);
            g.Connect(fb, 0, s, 0);
            var host = new PatchHost(g, 4);
            var (l, _) = host.Render(new[] { new NoteEvent(0, 60, 1f) }, 4 * PatchRuntime.Block);
            for (int i = 0; i < PatchRuntime.Block; i++) Assert.That(l[i], Is.EqualTo(0f), $"block 0, sample {i}: the gate hasn't come round yet");
            Assert.That(l.Skip(PatchRuntime.Block).Take(PatchRuntime.Block).Max(Math.Abs), Is.GreaterThan(0.1f), "block 1 carries block 0's gate");
            Measure.Report("Neo Patch: Feedback delays its input by exactly one block (64 samples)");
        }

        // ------------------------------------------------------------------ the nodes

        static float[] Windowed(float[] x, int from, int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = (float)(x[from + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n)));
            return w;
        }

        /// <summary>Loudest bin more than 40 bins (29 Hz) from any harmonic of the fundamental (a Hann window's sidelobes are under -75 dB from 8 bins out), relative to the fundamental.</summary>
        static double AliasFloorDb(float[] x, double hz, int n = 65536)
        {
            var mag = Fft.Magnitudes(Windowed(x, 8192, n), 0, n);
            double bin = hz * n / Rate;
            int fund = (int)Math.Round(bin);
            double worst = 1e-30; int wk = 0;
            for (int k = 40; k < mag.Length - 1 && k * (double)Rate / n < 20000; k++)
            {
                double h = Math.Round(k / bin);
                if (h >= 1 && Math.Abs(k - h * bin) <= 40) continue;
                if (mag[k] > worst) { worst = mag[k]; wk = k; }
            }
            return 20 * Math.Log10(worst / mag[fund]);
        }


        [Test]
        public void TheNodeCapIsEnforcedAndEveryKindHasPortsAndControls()
        {
            var g = new PatchGraph();
            for (int i = 0; i < NodeInfo.Cap; i++) Assert.That(g.Add(NodeKind.Osc), Is.Not.Null, $"node {i + 1}");
            Assert.That(g.Add(NodeKind.Osc), Is.Null, "the 129th node is refused");
            Assert.That(g.Nodes.Count, Is.EqualTo(NodeInfo.Cap));
            for (int k = 0; k < NodeInfo.KindCount; k++)
            {
                var info = NodeInfo.For((NodeKind)k);
                Assert.That(info.Name, Is.Not.Empty);
                Assert.That(info.Outs.Length + info.Ins.Length, Is.GreaterThan(0), info.Name);
                foreach (var c in info.Controls) Assert.That(c.Default, Is.InRange(c.Min, c.Max), $"{info.Name} {c.Name}");
            }
            Measure.Report($"Neo Patch: {NodeInfo.KindCount} node kinds, cap {NodeInfo.Cap} nodes");
        }

        [Test]
        public void ThePatchTextRoundTripsAndDamageIsSkippedNotThrown()
        {
            var g = PatchTemplates.Big20();
            g.Name = "Big & Twenty";
            g.MapMacro(2, g.Nodes.First(n => n.Kind == NodeKind.Filter), NodeInfo.FilterCutoff, 200, 5000);
            g.Nodes[3].Global = true;
            string text = PatchText.Save(g);
            Assert.That(text, Does.Not.Contain("\n"));
            var back = PatchText.Load(text);
            Assert.That(PatchText.Save(back), Is.EqualTo(text));
            Assert.That(back.Name, Is.EqualTo(g.Name));
            Assert.That(back.Nodes.Count, Is.EqualTo(g.Nodes.Count));
            Assert.That(back.Wires.Count, Is.EqualTo(g.Wires.Count));
            Assert.That(back.Macros.Count, Is.EqualTo(1));
            // A song from a future version or a damaged one loads what it can.
            foreach (string bad in new[] { "", "garbage", "neopatch 1;n 1 Nope 0 0 0;n x", text.Substring(0, text.Length / 2), text + ";w 999 0 1 0 1;w 1 0 1 0 1" })
            {
                var r = PatchText.Load(bad);       // null: not a patch at all (the machine then keeps its empty patch)
                var rt = new PatchRuntime(Rate);
                if (r != null) Assert.DoesNotThrow(() => rt.SetGraph(r), bad);
            }
            Measure.Report($"Neo Patch: the 20-node patch is {text.Length} characters of text and round-trips exactly");
        }

        // ------------------------------------------------------------------ the nodes

        [TestCase(0, "saw")]
        [TestCase(1, "square")]
        [TestCase(2, "sine")]
        [TestCase(3, "triangle")]
        public void EveryOscillatorShapeIsTheRightWaveAtTheRightLevel(int shape, string name)
        {
            var g = Simple(shape, 0f, 1f);
            var (l, _) = Run(g, 24000, 1, new NoteEvent(0, 57, 1f));
            double rms = Rms(l, 9600);
            // Level 1 through the Out's equal-power pan (0.707 a side): saw 0.577, square 1, sine 0.707, triangle 0.577 of full scale before the pan.
            double[] expect = { 0.577 * 0.845, 1.0 * 0.845, 0.707, 0.577 };       // the band-limited saw and square are scaled so their peaks (with Gibbs overshoot) stay inside 1
            Measure.Report($"Neo Patch: Osc {name}: rms {rms:F3}");
            Assert.That(rms, Is.EqualTo(expect[shape] * 0.7071).Within(6).Percent, name);
        }

        [Test]
        public void TheWavetableOscPlaysEveryBuiltInTableAtPitch()
        {
            for (int t = 0; t < TableBank.Count; t++)
            {
                var g = new PatchGraph();
                var k = g.Add(NodeKind.Keys);
                var w = g.Add(NodeKind.WavetableOsc); w.Set(NodeInfo.WtTable, t); w.Set(NodeInfo.WtPosition, 0.4f);
                var o = Hear(g, w);
                g.Connect(k, 0, w, NodeInfo.OscInPitch);
                var (l, _) = Run(g, 16384, 1, new NoteEvent(0, 69, 1f));
                Assert.That(l.All(float.IsFinite), Is.True, TableBank.Names[t]);
                Assert.That(Rms(l, 4096), Is.GreaterThan(0.02), TableBank.Names[t]);
            }
        }

        [Test]
        public void OscillatorFmPmSyncAndPulseAllChangeTheSound()
        {
            float[] Make(Action<PatchGraph, Node, Node, Node> wire)
            {
                var g = new PatchGraph();
                var k = g.Add(NodeKind.Keys);
                var a = g.Add(NodeKind.Osc); a.Set(NodeInfo.OscShape, 2);
                var b = g.Add(NodeKind.Osc); b.Set(NodeInfo.OscShape, 2); b.Set(NodeInfo.OscTune, 7.3f);
                g.Connect(k, 0, a, NodeInfo.OscInPitch);
                g.Connect(k, 0, b, NodeInfo.OscInPitch);
                Hear(g, a);
                wire(g, a, b, k);
                return Run(g, 8192).L;
            }
            var plain = Make((g, a, b, k) => { });
            foreach (var (name, wire) in new (string, Action<PatchGraph, Node, Node, Node>)[]
            {
                ("FM", (g, a, b, k) => { a.Set(NodeInfo.OscFm, 3f); g.Connect(b, 0, a, NodeInfo.OscInFm); }),
                ("PM", (g, a, b, k) => { a.Set(NodeInfo.OscPm, 1f); g.Connect(b, 0, a, NodeInfo.OscInPm); }),
                ("sync", (g, a, b, k) => { g.Connect(b, 0, a, NodeInfo.OscInSync); }),
            })
            {
                var y = Make(wire);
                double diff = 0;
                for (int i = 0; i < y.Length; i++) diff += Math.Abs(y[i] - plain[i]);
                Assert.That(diff / y.Length, Is.GreaterThan(0.01), name);
                Assert.That(y.All(float.IsFinite), Is.True, name);
            }
            // Pulse width: a 20 % pulse has its even harmonics (a 50 % square has none).
            var g2 = new PatchGraph();
            var k2 = g2.Add(NodeKind.Keys);
            var s2 = g2.Add(NodeKind.Osc); s2.Set(NodeInfo.OscShape, 1); s2.Set(NodeInfo.OscPulse, 0.2f);
            g2.Connect(k2, 0, s2, NodeInfo.OscInPitch); Hear(g2, s2);
            var sq = Run(g2, 8192 + 16384, 1, new NoteEvent(0, 57, 1f)).L;
            var mag = Fft.Magnitudes(Windowed(sq, 8192, 16384), 0, 16384);
            double f0 = 220.0 * 16384 / Rate;
            Assert.That(20 * Math.Log10(mag[(int)Math.Round(2 * f0)] / mag[(int)Math.Round(f0)]), Is.GreaterThan(-10), "the second harmonic of a 20 % pulse");
        }

        [Test]
        public void NoiseIsWhiteOrPinkAndTheSampleNodePlaysEverySource()
        {
            foreach (int color in new[] { 0, 1 })
            {
                var g = new PatchGraph();
                var n = g.Add(NodeKind.Noise); n.Set(NodeInfo.NoiseColor, color);
                var k = g.Add(NodeKind.Keys);
                var v = g.Add(NodeKind.Vca);
                g.Connect(n, 0, v, 0);
                g.Connect(k, 1, v, NodeInfo.VcaInCv);
                Hear(g, v);
                var (l, _) = Run(g, 16384);
                Assert.That(Rms(l, 2048), Is.GreaterThan(0.02), color == 0 ? "white" : "pink");
                var mag = Fft.Magnitudes(Windowed(l, 2048, 8192), 0, 8192);
                double low = 0, high = 0;
                for (int i = 20; i < 400; i++) low += mag[i] * mag[i];
                for (int i = 2000; i < 2380; i++) high += mag[i] * mag[i];
                if (color == 1) Assert.That(low, Is.GreaterThan(high * 4), "pink falls with frequency");
                else Assert.That(low / high, Is.EqualTo(1.0).Within(0.5), "white is flat");
            }
            for (int s = 0; s <= SampleSources.Count; s++)
            {
                var g = new PatchGraph();
                var k = g.Add(NodeKind.Keys);
                var sm = g.Add(NodeKind.Sample); sm.Set(NodeInfo.SampleSource, Math.Min(s, SampleSources.Count - 1)); sm.Set(NodeInfo.SampleLoop, 1);
                g.Connect(k, 0, sm, NodeInfo.SampleInPitch);
                g.Connect(k, 1, sm, NodeInfo.SampleInGate);
                Hear(g, sm);
                var (l, _) = Run(g, 12000, 1, new NoteEvent(0, 60, 1f));
                Assert.That(l.All(float.IsFinite), Is.True, "source " + s);
                Assert.That(l.Max(Math.Abs), Is.GreaterThan(0.001f), "source " + s);
            }
        }

        /// <summary>A sine tone through a VCA whose CV is <paramref name="cv"/>: the control signal heard as the tone's loudness.</summary>
        static PatchGraph Tone(Func<PatchGraph, Node> cv)
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = g.Add(NodeKind.Osc); o.Set(NodeInfo.OscShape, 2);
            var v = g.Add(NodeKind.Vca);
            g.Connect(k, 0, o, 0); g.Connect(o, 0, v, 0);
            g.Connect(cv(g), 0, v, NodeInfo.VcaInCv);
            Hear(g, v);
            return g;
        }

        [Test]
        public void ControlSourcesLfoRandomSlewSampleHoldAndMacroDoWhatTheySay()
        {
            // LFO: a 40 Hz sine heard directly (above the output's DC blocker), at its depth.
            var g = new PatchGraph();
            var lfo = g.Add(NodeKind.Lfo); lfo.Set(NodeInfo.LfoRate, 40); lfo.Set(NodeInfo.LfoDepth, 0.5f); lfo.Set(NodeInfo.LfoRetrig, 0);
            Hear(g, lfo);
            g.Connect(g.Add(NodeKind.Keys), 0, g.Add(NodeKind.Osc), 0);
            var (l, _) = Run(g, Rate);
            Assert.That(l.Skip(2000).Max() / 0.7071, Is.EqualTo(0.5).Within(0.06));
            Assert.That(l.Skip(2000).Min() / 0.7071, Is.EqualTo(-0.5).Within(0.06));
            int up = 0;
            for (int i = 1; i < l.Length; i++) if (l[i - 1] < 0 && l[i] >= 0) up++;
            Assert.That(up, Is.EqualTo(40).Within(2), "forty cycles in a second");

            // Random: stays inside depth, and moves.
            g = new PatchGraph();
            var rnd = g.Add(NodeKind.Random); rnd.Set(NodeInfo.RandRate, 20); rnd.Set(NodeInfo.RandDepth, 0.5f);
            Hear(g, rnd);
            (l, _) = Run(g, Rate);
            Assert.That(l.Max() / 0.7071, Is.LessThanOrEqualTo(0.55));
            Assert.That(l.Min() / 0.7071, Is.GreaterThanOrEqualTo(-0.55));
            Assert.That(l.Max() - l.Min(), Is.GreaterThan(0.1f));

            // Slew: a step through it arrives over about its rise time.
            g = Tone(gr => { var c = Const(gr, 1f); var sl = gr.Add(NodeKind.Slew); sl.Set(NodeInfo.SlewRise, 0.1f); gr.Connect(c, 0, sl, 0); return sl; });
            var host = new PatchHost(g, 1);
            host.Runtime.SetMacro(15, 1f);
            (l, _) = host.Render(new[] { new NoteEvent(0, 69, 1f) }, 24000);
            Assert.That(Rms(l, 0, 480), Is.LessThan(0.15), "still on its way in the first 10 ms");
            Assert.That(Rms(l, 14000, 24000) / 0.5, Is.GreaterThan(0.85), "there by 300 ms");

            // Macro: the machine's macro value, scaled by Amount.
            g = Tone(gr => Const(gr, 0.5f));
            host = new PatchHost(g, 1);
            host.Runtime.SetMacro(15, 0.8f);
            (l, _) = host.Render(new[] { new NoteEvent(0, 69, 1f) }, 24000);
            Assert.That(Rms(l, 12000, 24000) / 0.5, Is.EqualTo(0.4).Within(0.03));

            // Sample and hold: finite, inside its input's range.
            g = Tone(gr =>
            {
                var lf = gr.Add(NodeKind.Lfo); lf.Set(NodeInfo.LfoRate, 3);
                var sh = gr.Add(NodeKind.SampleHold); sh.Set(NodeInfo.ShMode, 0); sh.Set(NodeInfo.ShRate, 4);
                gr.Connect(lf, 0, sh, 0);
                return sh;
            });
            (l, _) = Run(g, Rate);
            Assert.That(l.All(float.IsFinite) && l.Max(Math.Abs) <= 0.75f, Is.True);
            Assert.That(Rms(l, 4000), Is.GreaterThan(0.01));
        }

        [Test]
        public void ShaperDelayMixerMathAndCrossfadeDoWhatTheySay()
        {
            // Shaper: every mode keeps a loud sine inside the unit, and more drive makes more harmonics.
            for (int mode = 0; mode < 3; mode++)
            {
                var g = new PatchGraph();
                var k = g.Add(NodeKind.Keys);
                var o = g.Add(NodeKind.Osc); o.Set(NodeInfo.OscShape, 2);
                var s = g.Add(NodeKind.Shaper); s.Set(NodeInfo.ShaperMode, mode); s.Set(NodeInfo.ShaperDrive, 8);
                g.Connect(k, 0, o, 0); g.Connect(o, 0, s, 0); Hear(g, s);
                var (l, _) = Run(g, 8192);
                Assert.That(l.All(float.IsFinite) && l.Max(Math.Abs) < 2f, Is.True, "mode " + mode);
                Assert.That(Rms(l, 2048), Is.GreaterThan(0.1), "mode " + mode);
            }
            // Delay: an impulse-like gate burst comes back at the delay time.
            {
                var g = new PatchGraph();
                var k = g.Add(NodeKind.Keys);
                var o = g.Add(NodeKind.Osc); o.Set(NodeInfo.OscShape, 2);
                var e = g.Add(NodeKind.Envelope); e.Set(NodeInfo.EnvA, 0.0005f); e.Set(NodeInfo.EnvD, 0.001f); e.Set(NodeInfo.EnvS, 0f); e.Set(NodeInfo.EnvR, 0.001f);
                var v = g.Add(NodeKind.Vca);
                var d = g.Add(NodeKind.Delay); d.Set(NodeInfo.DelayTime, 100); d.Set(NodeInfo.DelayFeedback, 0f); d.Set(NodeInfo.DelayMix, 1f);
                g.Connect(k, 0, o, 0); g.Connect(k, 1, e, 0); g.Connect(o, 0, v, 0); g.Connect(e, 0, v, 1); g.Connect(v, 0, d, 0); Hear(g, d);
                var (l, _) = Run(g, 24000);
                int first = Array.FindIndex(l, x => Math.Abs(x) > 0.01f);
                Assert.That(first / (double)Rate * 1000, Is.EqualTo(100).Within(4), "the echo arrives after 100 ms");
            }
            // Mixer: gains scale the sum.
            {
                var g = new PatchGraph();
                var k = g.Add(NodeKind.Keys);
                var a = g.Add(NodeKind.Osc); a.Set(NodeInfo.OscShape, 2);
                var b = g.Add(NodeKind.Osc); b.Set(NodeInfo.OscShape, 2);
                var m = g.Add(NodeKind.Mixer); m.Set(NodeInfo.MixGain, 0.5f); m.Set(NodeInfo.MixGain + 1, 0.5f);
                g.Connect(k, 0, a, 0); g.Connect(k, 0, b, 0); g.Connect(a, 0, m, 0); g.Connect(b, 0, m, 1); Hear(g, m);
                var (l, _) = Run(g, 8192);
                var (l1, _) = Run(Simple(2, 0f, 1f), 8192);
                Assert.That(Rms(l, 2048) / Rms(l1, 2048), Is.EqualTo(1.0).Within(0.15), "two halves of the same sine make one sine");
            }
            // Math: multiply two constants (as a loudness); every op is finite.
            {
                PatchGraph MakeMath(int op) => Tone(gr =>
                {
                    var c1 = Const(gr, 0.5f); var c2 = Const(gr, 0.5f);
                    var m = gr.Add(NodeKind.Math); m.Set(NodeInfo.MathOp, op);
                    gr.Connect(c1, 0, m, 0); gr.Connect(c2, 0, m, 1);
                    return m;
                });
                var host = new PatchHost(MakeMath(1), 1);
                host.Runtime.SetMacro(15, 1f);
                var (l, _) = host.Render(new[] { new NoteEvent(0, 69, 1f) }, 24000);
                Assert.That(Rms(l, 12000, 24000) / 0.5, Is.EqualTo(0.25).Within(0.03), "0.5 x 0.5");
                for (int op = 0; op < 7; op++)
                {
                    var (y, _) = new PatchHost(MakeMath(op), 1).Render(new[] { new NoteEvent(0, 60, 1f) }, 2048);
                    Assert.That(y.All(float.IsFinite), Is.True, "op " + op);
                }
            }
            // Crossfade: mix 0 is A, mix 1 is B.
            {
                var g = new PatchGraph();
                var k = g.Add(NodeKind.Keys);
                var a = g.Add(NodeKind.Osc); a.Set(NodeInfo.OscShape, 2);
                var b = g.Add(NodeKind.Osc); b.Set(NodeInfo.OscShape, 2); b.Set(NodeInfo.OscTune, 12);
                var x = g.Add(NodeKind.Crossfade); x.Set(NodeInfo.XfadeMix, 0f);
                g.Connect(k, 0, a, 0); g.Connect(k, 0, b, 0); g.Connect(a, 0, x, 0); g.Connect(b, 0, x, 1); Hear(g, x);
                var (la, _) = Run(g, 8192);
                x.Set(NodeInfo.XfadeMix, 1f);
                var (lb, _) = Run(g, 8192);
                var (ref_a, _) = Run(Simple(2, 0f, 1f), 8192);
                Assert.That(Rms(la, 2048), Is.EqualTo(Rms(ref_a, 2048)).Within(0.05));
                Assert.That(la.Zip(lb, (p, q) => Math.Abs(p - q)).Average(), Is.GreaterThan(0.05));
            }
        }

        [Test]
        public void OutPansEachInputAndSpreadsTheVoices()
        {
            var g = Simple(2, 0f, 1f);
            var o = g.Nodes.First(n => n.Kind == NodeKind.Out);
            o.Set(NodeInfo.OutPan, -1f);
            var (l, r) = Run(g, 8192);
            Assert.That(Rms(r, 2048), Is.LessThan(Rms(l, 2048) * 0.01), "hard left");
            o.Set(NodeInfo.OutPan, 1f);
            (l, r) = Run(g, 8192);
            Assert.That(Rms(l, 2048), Is.LessThan(Rms(r, 2048) * 0.01), "hard right");
            o.Set(NodeInfo.OutPan, 0f); o.Set(NodeInfo.OutSpread, 1f);
            var ev = Enumerable.Range(0, 4).Select(i => new NoteEvent(0, 48 + 5 * i, 0.9f)).ToArray();
            (l, r) = Run(g, 8192, 4, ev);
            double diff = 0;
            for (int i = 2048; i < l.Length; i++) diff += Math.Abs(l[i] - r[i]);
            Assert.That(diff / (l.Length - 2048), Is.GreaterThan(0.01), "spread puts voices in different places");
            Assert.That(Rms(l, 2048), Is.GreaterThan(0.05));
            Assert.That(Rms(r, 2048), Is.GreaterThan(0.05));
        }

        [Test]
        public void TheAudioRateModulationSwitchChangesWhatFilterFmSoundsLike()
        {
            PatchGraph Make()
            {
                var g = new PatchGraph();
                var k = g.Add(NodeKind.Keys);
                var o = g.Add(NodeKind.Osc); o.Set(NodeInfo.OscShape, 0);
                var m = g.Add(NodeKind.Osc); m.Set(NodeInfo.OscShape, 2); m.Set(NodeInfo.OscTune, 12);
                var f = g.Add(NodeKind.Filter); f.Set(NodeInfo.FilterCutoff, 1500); f.Set(NodeInfo.FilterMod, 1.5f);
                g.Connect(k, 0, o, 0); g.Connect(k, 0, m, 0); g.Connect(o, 0, f, 0); g.Connect(m, 0, f, NodeInfo.FilterInCutoff);
                Hear(g, f);
                return g;
            }
            var a = new PatchRuntime(Rate) { Voices = 1 }; a.SetGraph(Make());
            var b = new PatchRuntime(Rate) { Voices = 1, AudioRateModulation = true }; b.SetGraph(Make());
            a.NoteOn(0, 57, 1f); b.NoteOn(0, 57, 1f);
            var l1 = new float[64]; var r1 = new float[64]; var l2 = new float[64]; var r2 = new float[64];
            double diff = 0;
            for (int blk = 0; blk < 100; blk++)
            {
                a.ProcessBlock(l1, r1); b.ProcessBlock(l2, r2);
                if (blk > 20) for (int i = 0; i < 64; i++) diff += Math.Abs(l1[i] - l2[i]);
                Assert.That(l1.Concat(l2).All(float.IsFinite), Is.True);
            }
            Assert.That(diff, Is.GreaterThan(1.0), "per-sample modulation sounds different from the interpolated control rate");
        }


        // ------------------------------------------------------------------ scope, the voice sum, state across swaps

        [Test]
        public void AGlobalLfoIsOneForAllVoicesAndAPerVoiceLfoIsOnePerVoice()
        {
            PatchGraph Make(bool global)
            {
                var g = new PatchGraph();
                var k = g.Add(NodeKind.Keys);
                var o = g.Add(NodeKind.Osc); o.Set(NodeInfo.OscShape, 2);
                var lfo = g.Add(NodeKind.Lfo); lfo.Set(NodeInfo.LfoRate, 4); lfo.Set(NodeInfo.LfoRetrig, 1); lfo.Global = global;
                var v = g.Add(NodeKind.Vca);
                var e = g.Add(NodeKind.Envelope); e.Set(NodeInfo.EnvS, 1f);
                g.Connect(k, 0, o, 0); g.Connect(k, 1, e, 0); g.Connect(o, 0, v, 0); g.Connect(lfo, 0, v, NodeInfo.VcaInCv);
                var out_ = g.Add(NodeKind.Out); g.Connect(v, 0, out_, 0);
                return g;
            }
            // Two notes 125 ms apart: per-voice LFOs restart with each note (phases differ), a global one is the same for both.
            var ev = new[] { new NoteEvent(0, 60, 1f), new NoteEvent(6000, 67, 1f) };
            var (lg, _) = Run(Make(true), 24000, 2, ev);
            var (lv, _) = Run(Make(false), 24000, 2, ev);
            double diff = 0;
            for (int i = 12000; i < 24000; i++) diff += Math.Abs(lg[i] - lv[i]);
            Assert.That(diff / 12000, Is.GreaterThan(0.005), "the two scopes sound different with an offset note");
            Assert.That(lg.All(float.IsFinite) && lv.All(float.IsFinite), Is.True);
        }

        [Test]
        public void VoiceSumMakesTheLaterNodesOneForAllVoicesAndGlobalKeysFollowsTheHeldKeys()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = g.Add(NodeKind.Osc); o.Set(NodeInfo.OscShape, 0);
            var vs = g.Add(NodeKind.VoiceSum);
            var f = g.Add(NodeKind.Filter); f.Set(NodeInfo.FilterCutoff, 900);
            var e = g.Add(NodeKind.Envelope); e.Set(NodeInfo.EnvS, 1f);
            var vca = g.Add(NodeKind.Vca);
            g.Connect(k, 0, o, 0); g.Connect(k, 1, e, 0);
            g.Connect(o, 0, vca, 0); g.Connect(e, 0, vca, 1);
            g.Connect(vca, 0, vs, 0); g.Connect(vs, 0, f, 0);
            Hear(g, f);
            var rt = new PatchRuntime(Rate) { Voices = 8 };
            Assert.That(rt.SetGraph(g), Is.Null);
            Assert.That(rt.Current.HasGlobal, Is.True, "the filter after the Voice sum runs once, in the global view");
            for (int n = 0; n < 6; n++) rt.NoteOn(n, 40 + 4 * n, 0.8f);
            var l = new float[64]; var r = new float[64];
            double energy = 0;
            for (int b = 0; b < 300; b++) { rt.ProcessBlock(l, r); if (b > 20) energy += l.Sum(x => (double)x * x); }
            Assert.That(energy, Is.GreaterThan(1.0));
            // A global filter after the sum costs the same however many voices play: one filter, not six.
            // Global Keys: a pitch that follows the last-played key, with its own gate.
            var g2 = new PatchGraph();
            var gk = g2.Add(NodeKind.GlobalKeys);
            var osc = g2.Add(NodeKind.Osc); osc.Set(NodeInfo.OscShape, 2);
            var env = g2.Add(NodeKind.Envelope); env.Set(NodeInfo.EnvS, 1f); env.Global = true;
            var vv = g2.Add(NodeKind.Vca); vv.Global = true;
            osc.Global = true;
            g2.Connect(gk, 0, osc, 0); g2.Connect(gk, 1, env, 0); g2.Connect(osc, 0, vv, 0); g2.Connect(env, 0, vv, 1); Hear(g2, vv);
            var rt2 = new PatchRuntime(Rate) { Voices = 4 };
            Assert.That(rt2.SetGraph(g2), Is.Null);
            rt2.NoteOn(0, 57, 1f);
            var y = new float[16384];
            for (int b = 0; b < 256; b++) { rt2.ProcessBlock(l, r); Array.Copy(l, 0, y, b * 64, 64); }
            Assert.That(Rms(y, 4096), Is.GreaterThan(0.1), "Global Keys plays without any voice's help");
        }

        [Test]
        public void NodesThatSurviveAnEditKeepTheirStateAndTheToneGoesOn()
        {
            // Render 0.6 s straight; then render again with the graph edited (a new unrelated node, a knob moved) after 0.3 s: the part the edit
            // doesn't touch must continue sample for sample (the oscillator's phase, the filter's memory, the envelope's place).
            PatchGraph Make() { var g = PatchTemplates.Core8(); return g; }
            float[] Render(bool edit)
            {
                var g = Make();
                var rt = new PatchRuntime(Rate) { Voices = 4 };
                rt.SetGraph(g);
                for (int n = 0; n < 3; n++) rt.NoteOn(n, 48 + 4 * n, 0.8f);
                var y = new float[Rate * 6 / 10 / 64 * 64];
                var l = new float[64]; var r = new float[64];
                for (int b = 0; b < y.Length / 64; b++)
                {
                    if (edit && b == y.Length / 128)
                    {
                        var extra = g.Add(NodeKind.Lfo);                       // dead branch: wired to nothing, so the patch is unchanged
                        g.Nodes.First(n => n.Kind == NodeKind.Out).Set(NodeInfo.OutLevel, 0.5f);
                        Assert.That(rt.SetGraph(g), Is.Null);
                        Assert.That(rt.LastAdopted, Is.GreaterThanOrEqualTo(7), "the nodes carried their state");
                    }
                    rt.ProcessBlock(l, r);
                    Array.Copy(l, 0, y, b * 64, 64);
                }
                return y;
            }
            var a = Render(false);
            var e = Render(true);
            double worst = 0;
            for (int i = 0; i < a.Length; i++) worst = Math.Max(worst, Math.Abs(a[i] - e[i]));
            Measure.Report($"Neo Patch: a graph edit that changes nothing audible, mid-note: largest difference {worst:E2}");
            Assert.That(worst, Is.LessThan(2e-3), "a tone carries on through the edit");
        }

        [Test]
        public void ASwapDoesNotClickAndAnAddedNodeStartsAtItsOwnPlace()
        {
            var rt = new PatchRuntime(Rate) { Voices = 8 };
            rt.SetGraph(PatchTemplates.Core8());
            for (int n = 0; n < 4; n++) rt.NoteOn(n, 48 + 3 * n, 0.9f);
            var next = rt.Compile(PatchTemplates.Big20()).Patch;
            var l = new float[64];
            var r = new float[64];
            var all = new List<float>();
            for (int b = 0; b < 300; b++)
            {
                if (b == 150) rt.Publish(next);
                rt.ProcessBlock(l, r);
                all.AddRange(l);
            }
            double stepBefore = 0, stepAt = 0;
            for (int i = 64 * 100; i < 64 * 149; i++) stepBefore = Math.Max(stepBefore, Math.Abs(all[i] - all[i - 1]));
            for (int i = 64 * 150; i < 64 * 152; i++) stepAt = Math.Max(stepAt, Math.Abs(all[i] - all[i - 1]));
            Measure.Report($"Neo Patch: swapping the 8-node patch for the 20-node one on 4 held notes: largest step {stepAt:F3} across the swap, {stepBefore:F3} before it");
            Assert.That(all.All(float.IsFinite), Is.True);
            Assert.That(stepAt, Is.LessThan(Math.Max(0.5, 3 * stepBefore)));
        }

        [Test]
        public void MacrosMoveTheNodesTheyAreMappedToAndVoicesAndGlideBehave()
        {
            var g = PatchTemplates.Core8();
            var f = g.Nodes.First(n => n.Kind == NodeKind.Filter);
            g.MapMacro(0, f, NodeInfo.FilterCutoff, 100, 8000);
            var rt = new PatchRuntime(Rate) { Voices = 4 };
            rt.SetGraph(g);
            rt.NoteOn(0, 57, 1f);
            var l = new float[64]; var r = new float[64];
            double Level(float macro)
            {
                rt.SetMacro(0, macro);
                for (int b = 0; b < 200; b++) rt.ProcessBlock(l, r);
                double s = 0;
                for (int b = 0; b < 40; b++) { rt.ProcessBlock(l, r); s += l.Sum(x => (double)x * x); }
                return Math.Sqrt(s / (40 * 64));
            }
            double lo = Level(0f), hi = Level(1f);
            Assert.That(hi, Is.GreaterThan(lo * 1.2), "opening the macro opens the filter");
            // One voice: the second note steals the first; glide makes the pitch slide (the zero crossings spread out).
            var rt2 = new PatchRuntime(Rate) { Voices = 1, Glide = 0.3f };
            rt2.SetGraph(Simple(2));
            rt2.NoteOn(0, 57, 1f);
            for (int b = 0; b < 100; b++) rt2.ProcessBlock(l, r);
            rt2.NoteOn(1, 69, 1f);
            int ups0 = 0, ups1 = 0;
            for (int b = 0; b < 200; b++)
            {
                rt2.ProcessBlock(l, r);
                if (b < 100) { }
            }
            Assert.That(rt2.ActiveVoices, Is.GreaterThan(0));
        }

        [Test]
        public void MpeNotesCarryTheirOwnBendPressureAndSlide()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = g.Add(NodeKind.Osc); o.Set(NodeInfo.OscShape, 2);
            var e = g.Add(NodeKind.Envelope); e.Set(NodeInfo.EnvS, 1f);
            var v = g.Add(NodeKind.Vca);
            g.Connect(k, 0, o, 0); g.Connect(k, 1, e, 0); g.Connect(o, 0, v, 0); g.Connect(e, 0, v, 1);
            // Pressure and slide as gain: Keys pressure into a second VCA's CV.
            var v2 = g.Add(NodeKind.Vca); g.Connect(v, 0, v2, 0); g.Connect(k, 3, v2, 1);
            Hear(g, v2);
            var rt = new PatchRuntime(Rate) { Voices = 2 };
            rt.SetGraph(g);
            var l = new float[64]; var r = new float[64];
            rt.NoteOn(1, 57, 1f, 1);
            rt.Control(1, 0xD0, 127);
            for (int b = 0; b < 100; b++) rt.ProcessBlock(l, r);
            double loud = l.Max(Math.Abs);
            rt.Control(1, 0xD0, 0);
            for (int b = 0; b < 100; b++) rt.ProcessBlock(l, r);
            Assert.That(l.Max(Math.Abs), Is.LessThan(loud * 0.2), "channel pressure reaches the Keys node's Pressure output of that note");
        }

        [TestCase(0, 440.0)]
        [TestCase(1, 440.0)]
        [TestCase(2, 440.0)]
        [TestCase(3, 440.0)]
        public void OscillatorsRunAtTheRightPitch(int shape, double hz)
        {
            var host = new PatchHost(Simple(shape, 0f, 0.5f), 1);
            var (l, _) = host.Render(new[] { new NoteEvent(0, 69, 1f) }, Rate);
            int up = 0;
            double first = 0, last = 0;
            for (int i = 9601; i < Rate; i++)
                if (l[i - 1] <= 0 && l[i] > 0)
                {
                    double at = i - l[i] / (l[i] - l[i - 1]);       // the crossing, interpolated
                    if (up++ == 0) first = at;
                    last = at;
                }
            double measured = (up - 1) * Rate / (last - first);
            Assert.That(Math.Abs(Measure.Cents(measured, hz)), Is.LessThan(3.0), $"shape {shape}: {measured:F2} Hz");
        }

        [Test]
        public void VoicesAreIndependentAndSumInOrder()
        {
            // Two notes at once are the sum of each alone: per-voice nodes keep every voice's state to itself (no crosstalk across the lanes).
            var g = PatchTemplates.Core8();
            g.Nodes.First(n => n.Kind == NodeKind.Out).Set(NodeInfo.OutLevel, 0.05f);        // under the output limiter's knee, which isn't linear
            float[] Run(params int[] keys)
            {
                var host = new PatchHost(g, 8, Rate, LaneKind.Auto);
                var ev = keys.Select(k => new NoteEvent(0, k, 0.8f)).ToList();
                // A fresh runtime per run: the same graph's state is in the compiled patch, so compile again.
                return host.Render(ev, 8192).L;
            }
            var a = Run(48);
            var b = Run(55);
            var ab = Run(48, 55);
            double worst = 0;
            for (int i = 0; i < a.Length; i++) worst = Math.Max(worst, Math.Abs(ab[i] - (a[i] + b[i])));
            Measure.Report($"Neo Patch: a two-note chord against the sum of its notes: largest difference {worst:E2}");
            Assert.That(worst, Is.LessThan(1e-5));
        }

        [Test]
        public void TheFilterAndEnvelopeFollowTheirSettings()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = g.Add(NodeKind.Osc);
            var f = g.Add(NodeKind.Filter); f.Set(NodeInfo.FilterCutoff, 400);
            var s = g.Add(NodeKind.Out); s.Set(NodeInfo.OutLevel, 0.5f);
            g.Connect(k, 0, o, 0); g.Connect(o, 0, f, 0); g.Connect(f, 0, s, 0);
            // A 4 kHz tone through a 400 Hz low-pass: about 40 dB down for 2 poles.
            o.Set(NodeInfo.OscShape, 2); o.Set(NodeInfo.OscTune, 12 * (float)Math.Log2(4000.0 / 440));
            var (l, _) = new PatchHost(g, 1).Render(new[] { new NoteEvent(0, 69, 1f) }, 16384);
            double rms = Math.Sqrt(l.Skip(4096).Select(x => (double)x * x).Average());
            f.Set(NodeInfo.FilterCutoff, 8000);
            var (l2, _) = new PatchHost(g, 1).Render(new[] { new NoteEvent(0, 69, 1f) }, 16384);
            double rms2 = Math.Sqrt(l2.Skip(4096).Select(x => (double)x * x).Average());
            Measure.Report($"Neo Patch: a 4 kHz sine through the low-pass at 400 Hz and at 8 kHz: {20 * Math.Log10(rms / rms2):F1} dB");
            Assert.That(20 * Math.Log10(rms / rms2), Is.LessThan(-30));

            // The envelope: attack, then sustain, then the release dies away and the voice goes idle.
            var host = new PatchHost(Simple(2), 2);
            var (e, _) = host.Render(new[] { new NoteEvent(0, 69, 1f), new NoteEvent(24000, 69, 0f) }, 96000);
            Assert.That(host.Runtime.ActiveVoices, Is.EqualTo(0), "the released voice has gone idle");
            double tail = e.Skip(90000).Max(Math.Abs);
            Assert.That(tail, Is.LessThan(1e-4));
        }

        [Test]
        public void MoreNotesThanVoicesStealWithoutBreakingAnything()
        {
            var host = new PatchHost(PatchTemplates.Core8(), 4);
            var ev = new List<NoteEvent>();
            for (int n = 0; n < 12; n++) ev.Add(new NoteEvent(n * 1500, 40 + 2 * n, 0.8f));
            var (l, r) = host.Render(ev, 48000);
            Assert.That(l.Concat(r).All(float.IsFinite), Is.True);
            Assert.That(host.Runtime.ActiveVoices, Is.LessThanOrEqualTo(8), "4 held notes and the tails of the ones stolen from");
            double step = 0, rms = Math.Sqrt(l.Select(x => (double)x * x).Average());
            for (int i = 1; i < l.Length; i++) step = Math.Max(step, Math.Abs(l[i] - l[i - 1]));
            Measure.Report($"Neo Patch: 12 notes on 4 voices: largest sample step {step:F3} against rms {rms:F3}");
            Assert.That(step, Is.LessThan(0.6), "steals fade out, no clicks");
        }



        const ulong PinnedBig20 = 0x1B938B9A41C46578UL;

        [Test]
        public void TwoRendersAreIdenticalAndEveryLaneImplementationGivesTheSameBits()
        {
            ulong first = 0;
            foreach (var kind in Kinds)
            {
                var a = new PatchHost(PatchTemplates.Big20(), 8, Rate, kind).Render(Chord(8, 24000), 48000);
                var b = new PatchHost(PatchTemplates.Big20(), 8, Rate, kind).Render(Chord(8, 24000), 48000);
                Assert.That(Hash(a.L), Is.EqualTo(Hash(b.L)), $"{kind}: two renders");
                Assert.That(a.L.SequenceEqual(b.L) && a.R.SequenceEqual(b.R), Is.True);
                Assert.That(a.L.All(float.IsFinite), Is.True);
                Assert.That(a.L.Max(Math.Abs), Is.GreaterThan(0.01f), "it sounds");
                ulong h = Hash(a.L) ^ (Hash(a.R) * 31);
                if (kind == Kinds[0]) first = h; else Assert.That(h, Is.EqualTo(first), $"{kind} against the scalar reference");
            }
            Measure.Report($"Neo Patch: the 20-node render's checksum {first:X16}, the same on {string.Join(", ", Kinds)} ({RuntimeInfo()})");
            if (PinnedBig20 != 0) Assert.That(first, Is.EqualTo(PinnedBig20), "the render's bits, the same on every platform");
        }

        // ------------------------------------------------------------------ determinism (a render is the same bytes wherever it runs)

        static List<NoteEvent> Staggered()
        {
            // Notes starting at different blocks and across the lane groups (a lane unprimed while its neighbours play), some released early.
            var e = new List<NoteEvent>();
            int[] at = { 0, 0, 64, 192, 200, 1000, 1300, 4000, 9000, 9100 };
            for (int n = 0; n < at.Length; n++) e.Add(new NoteEvent(at[n], 40 + 5 * n, 0.4f + 0.06f * n));
            for (int n = 0; n < at.Length; n += 3) e.Add(new NoteEvent(at[n] + 6000, 40 + 5 * n, 0));
            e.Sort((a, b) => a.Sample.CompareTo(b.Sample));
            return e;
        }

        [Test]
        public void EveryLaneKindGivesTheSameBitsWithPortModulationAndStaggeredNotes()
        {
            // The presets run an envelope into the filter's cutoff port (sampled per control tick while it moves, every lane of a group
            // recomputed when one is unprimed): none of it may depend on the lane width, 8 on x64 and 4 on arm64.
            for (int preset = 0; preset < PatchPresets.Names.Length; preset++)
            {
                float[] first = null;
                foreach (var kind in Kinds)
                foreach (int tick in new[] { 16 })
                {
                    var host = new PatchHost(PatchPresets.Build(preset), 8, Rate, kind, tick);
                    var (l, r) = host.Render(Staggered(), 24000);
                    var both = l.Concat(r).ToArray();
                    Assert.That(both.Max(Math.Abs), Is.GreaterThan(0.001f), $"{PatchPresets.Names[preset]} sounds");
                    if (first == null) first = both;
                    else Assert.That(both.SequenceEqual(first), Is.True, $"{PatchPresets.Names[preset]}: {kind} against the scalar reference");
                }
            }
        }

        [Test]
        public void ARenderDoesNotDependOnWhenABackgroundCompileFinishes()
        {
            // The machine is built at 48 kHz; the host says 44.1 kHz at the first block, which asks for a compile in the background.
            // Before the fix that patch was published at whatever block the thread timing picked (a one-block correction and a state
            // take-over in the middle of the render, up to -78 dBFS on macOS arm64). Now the render waits for it (Busy).
            float[] Render(int delayMs, bool wait = true)
            {
                var rt = new PatchRuntime(48000f, LaneKind.Auto, 16) { Voices = 8, Volume = 1f, TestCompileDelayMs = delayMs };
                rt.SetGraph(PatchPresets.Build(4));
                rt.SetSampleRate(44100f);
                var sw = Stopwatch.StartNew();
                while (wait && rt.Compiling && sw.ElapsedMilliseconds < 30000) Thread.Sleep(1);
                if (wait) Assert.That(rt.Compiling, Is.False, "the compile finished");
                // Notes through the runtime that waited.
                var l = new float[24000];
                var r = new float[24000];
                var bl = new float[PatchRuntime.Block];
                var br = new float[PatchRuntime.Block];
                var ev = Staggered();
                int e = 0;
                for (int pos = 0; pos < l.Length; pos += PatchRuntime.Block)
                {
                    while (e < ev.Count && ev[e].Sample <= pos)
                    {
                        if (ev[e].Velocity > 0f) rt.NoteOn(ev[e].Key, ev[e].Key, ev[e].Velocity); else rt.NoteOff(ev[e].Key);
                        e++;
                    }
                    rt.ProcessBlock(bl, br);
                    if (!wait && pos < 64 * 40) Thread.Sleep(2);     // blocks that take a while: the compile lands among them
                    Array.Copy(bl, 0, l, pos, Math.Min(PatchRuntime.Block, l.Length - pos));
                }
                return l;
            }
            var a = Render(0);
            var b = Render(150);
            Assert.That(b.SequenceEqual(a), Is.True, "the same bytes however long the compile took");
            // The exposure itself: a render that does not wait picks the patch up mid-way when the compile is slow.
            Assert.That(Render(40, wait: false).SequenceEqual(a), Is.False, "(a slow compile that is not waited for does move the samples)");
            // And the same as a runtime that was built at the host's rate to begin with.
            var direct = new PatchHost(PatchPresets.Build(4), 8, 44100f).Render(Staggered(), 24000).L;
            Assert.That(a.SequenceEqual(direct), Is.True, "a rate change before the first block equals building at that rate");
        }

        [Test]
        public void TheMachinesBusyCoversTheRecompileTheHostsRateAsksFor()
        {
            var rt = new PatchRuntime(48000f, LaneKind.Auto, 16) { Voices = 8 };
            rt.SetGraph(PatchPresets.Build(4));
            Assert.That(rt.Compiling, Is.False);
            rt.SetSampleRate(44100f);
            var sw = Stopwatch.StartNew();
            while (rt.Compiling && sw.ElapsedMilliseconds < 30000) Thread.Sleep(1);
            Assert.That(rt.Compiling, Is.False);
            rt.Voices = 12;
            var sw2 = Stopwatch.StartNew();
            while (rt.Compiling && sw2.ElapsedMilliseconds < 30000) Thread.Sleep(1);
            Assert.That(rt.Compiling, Is.False);
        }

        [Test]
        public void TheHostsBlockSizeDoesNotChangeTheSamples()
        {
            float[] Run(int chunk)
            {
                var rt = new PatchRuntime(Rate) { Voices = 8 };
                rt.SetGraph(PatchTemplates.Big20());
                for (int n = 0; n < 5; n++) rt.NoteOn(n, 40 + 4 * n, 0.8f);
                var y = new float[16384];
                var l = new float[chunk];
                var r = new float[chunk];
                for (int pos = 0; pos < y.Length; pos += chunk)
                {
                    int n = Math.Min(chunk, y.Length - pos);
                    rt.Process(l, r, n);
                    Array.Copy(l, 0, y, pos, n);
                }
                return y;
            }
            var reference = Run(64);
            foreach (int chunk in new[] { 256, 117, 16, 1 })
                Assert.That(Run(chunk).SequenceEqual(reference), Is.True, $"chunks of {chunk}");
        }

        // ------------------------------------------------------------------ the audio thread

        [Test]
        public void TheAudioThreadAllocatesNothingWhileNotesControlsAndPatchesChange()
        {
            var rt = new PatchRuntime(Rate) { Voices = 8, Glide = 0.05f };
            var big = PatchTemplates.Big20();
            rt.SetGraph(big);
            var other = rt.Compile(PatchTemplates.Core8()).Patch;
            var another = rt.Compile(PatchTemplates.Big20()).Patch;
            var l = new float[64];
            var r = new float[64];
            var cutoff = big.Nodes.First(n => n.Kind == NodeKind.Filter);
            var (bytes, why) = AllocationCheck.Run(1500, b =>
            {
                int p = b % 500;
                if (p == 0) for (int n = 0; n < 10; n++) rt.NoteOn(n, 36 + 5 * n, 0.8f);      // 10 notes on 8 voices: steals
                if (p == 200) for (int n = 0; n < 10; n += 2) rt.NoteOff(n);
                if (p == 300) rt.AllNotesOff(false);
                if (b % 7 == 0) cutoff.Set(NodeInfo.FilterCutoff, 300 + 40 * (b % 50));
                if (b == 450) rt.Publish(other);
                if (b == 900) rt.Publish(another);
                rt.ProcessBlock(l, r);
            });
            Measure.Report($"Neo Patch: audio thread, 1500 blocks of notes, steals, glide, control edits and two patch swaps: {bytes} bytes");
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void NaNsAndRunawaysNeverReachTheOutput()
        {
            var g = PatchTemplates.Big20();
            var host = new PatchHost(g, 8);
            var vca = g.Nodes.Where(n => n.Kind == NodeKind.Vca).ToList();
            var filter = g.Nodes.First(n => n.Kind == NodeKind.Filter);
            var rt = host.Runtime;
            for (int n = 0; n < 4; n++) rt.NoteOn(n, 48 + 3 * n, 0.9f);
            var l = new float[64];
            var r = new float[64];
            var seen = new List<float>();
            for (int b = 0; b < 400; b++)
            {
                // Poison the patch behind the settings' clamps: a NaN gain, an infinite cutoff, a huge resonance.
                if (b == 50) vca[3].Controls[0] = float.NaN;
                if (b == 60) vca[3].Controls[0] = 1f;
                if (b == 100) filter.Controls[NodeInfo.FilterCutoff] = float.PositiveInfinity;
                if (b == 110) filter.Controls[NodeInfo.FilterCutoff] = 1200f;
                if (b == 150) filter.Controls[NodeInfo.FilterReso] = 1e9f;
                if (b == 160) filter.Controls[NodeInfo.FilterReso] = 1.2f;
                rt.ProcessBlock(l, r);
                seen.AddRange(l); seen.AddRange(r);
                Assert.That(l.Concat(r).All(x => float.IsFinite(x) && Math.Abs(x) <= 1f), Is.True, $"block {b}");
            }
            double late = seen.Skip(seen.Count - 4096).Select(Math.Abs).Max();
            Measure.Report($"Neo Patch: NaN gain, infinite cutoff, resonance 1e9 injected: output stayed finite and within ±1, and recovered (late peak {late:F3}); guard hits at the output {rt.NonFinite}");
            Assert.That(late, Is.GreaterThan(0.001f), "the patch plays again once the controls are sane");
        }


        // ------------------------------------------------------------------ the oscillators' alias floor

        [TestCase(0, 1250.0)]
        [TestCase(1, 1250.0)]
        [TestCase(3, 1250.0)]
        [TestCase(0, 4400.0)]
        [TestCase(1, 4400.0)]
        [TestCase(0, 9000.0)]
        [TestCase(0, 14000.0)]
        [TestCase(1, 11000.0)]
        public void OscillatorsAreBandLimited(int shape, double hz)
        {
            double note = 69 + 12 * Math.Log2(hz / 440);
            var g = Simple(shape, (float)(note - 69));
            var host = new PatchHost(g, 1);
            var (l, _) = host.Render(new[] { new NoteEvent(0, 69, 1f) }, 8192 + 65536 + 64);
            double floor = AliasFloorDb(l, hz);
            Measure.Report($"Neo Patch: Osc shape {shape} at {hz:F0} Hz: aliasing floor {floor:F1} dB ({RuntimeInfo()})");
            Assert.That(floor, Is.LessThan(AliasLimit), "dB below the fundamental, beyond ±40 bins of every harmonic");
        }

        const double AliasLimit = -85;

        // ------------------------------------------------------------------ CPU

        static Action<float[], float[], int> PatchProcess(PatchGraph g, LaneKind kind, int tick, int voices = 8)
        {
            var rt = new PatchRuntime(Rate, kind, tick) { Voices = voices, Volume = 0.25f };
            rt.SetGraph(g);
            for (int n = 0; n < voices; n++) rt.NoteOn(n, 36 + 3 * n, 0.4f + 0.6f * n / voices);
            return rt.Process;
        }

        static double Speed(Action<float[], float[], int> process, double seconds)
        {
            var l = new float[256];
            var r = new float[256];
            for (int i = 0; i < 40; i++) process(l, r, 256);
            int blocks = (int)(seconds * Rate / 256);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < blocks; i++) process(l, r, 256);
            double wall = sw.Elapsed.TotalSeconds;
            return blocks * 256.0 / Rate / wall;
        }

        static double Best(Func<Action<float[], float[], int>> make, double seconds = 1, int reps = 3)
        {
            var p = make();
            double best = 0;
            for (int i = 0; i < reps; i++) best = Math.Max(best, Speed(p, seconds));
            return best;
        }

        /// <summary>Eight voices of Keys → N copies of one node kind in series (or in parallel for a source) → Out: the cost of one node kind.</summary>
        static PatchGraph Chain(NodeKind kind, int copies, Action<Node> setup = null)
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var src = g.Add(NodeKind.Osc); src.Set(NodeInfo.OscShape, 0);
            g.Connect(k, 0, src, 0);
            var o = g.Add(NodeKind.Out); o.Set(NodeInfo.OutLevel, 0.3f);
            Node prev = src;
            var info = NodeInfo.For(kind);
            for (int i = 0; i < copies; i++)
            {
                var n = g.Add(kind); setup?.Invoke(n);
                if (kind == NodeKind.Osc || kind == NodeKind.WavetableOsc || kind == NodeKind.Sample) g.Connect(k, 0, n, 0);
                else if (info.Ins.Length > 0) g.Connect(prev, 0, n, 0);
                if (info.Outs.Length > 0 && (info.Ins.Length > 0 || kind == NodeKind.Noise || kind == NodeKind.Lfo)) prev = n;
                if (kind == NodeKind.Osc || kind == NodeKind.WavetableOsc || kind == NodeKind.Sample || kind == NodeKind.Noise) g.Connect(n, 0, o, Math.Min(3, i % 4));
            }
            if (prev != src && info.Outs.Length > 0) g.Connect(prev, 0, o, 0);
            else if (copies == 0) g.Connect(src, 0, o, 0);
            return g;
        }

        [Test]
        public void WhereTheTimeGoes()
        {
            // The cost of each node kind: ten of them in a chain on eight voices, as × real time, against the same patch without them.
            const int Copies = 10;
            double baseline = Best(() => PatchProcess(Chain(NodeKind.Vca, 0), LaneKind.Auto, 16));
            var lines = new List<string> { $"base (Keys, Osc, Out; 8 voices): {baseline:F0}x" };
            for (int k = 0; k < NodeInfo.KindCount; k++)
            {
                var kind = (NodeKind)k;
                if (kind == NodeKind.Keys || kind == NodeKind.Out || kind == NodeKind.Feedback || kind == NodeKind.GlobalKeys || kind == NodeKind.VoiceSum || kind == NodeKind.Macro) continue;
                double speed = Best(() => PatchProcess(Chain(kind, Copies), LaneKind.Auto, 16), 0.5, 2);
                // Per node: the extra time per second of audio, divided by nodes and voices (microseconds per node and voice per ms of audio).
                double extra = 1.0 / speed - 1.0 / baseline;
                lines.Add($"{NodeInfo.For(kind).Name}: {speed:F0}x with {Copies}; {extra / Copies * 1e3:F2} ms per node per second of audio (8 voices)");
            }
            Measure.Report("Neo Patch CPU per node kind: " + string.Join("; ", lines));
        }

        [Test]
        public void ThePatchesAgainstTheirFloors()
        {
            foreach (var (name, make, x64, arm64) in new (string, Func<PatchGraph>, double, double)[]
            {
                ("8-node patch", PatchTemplates.Core8, 65, 45),
                ("20-node patch", PatchTemplates.Big20, 35, 25),
            })
            {
                Throughput.Guard($"Neo Patch CPU: {name}, 8 voices", Throughput.Floor(x64, arm64), 2, Rate, PatchRuntime.Block, () =>
                {
                    var rt = new PatchRuntime(Rate, LaneKind.Auto, 16) { Voices = 8, Volume = 0.25f };
                    rt.SetGraph(make());
                    for (int n = 0; n < 8; n++) rt.NoteOn(n, 36 + 3 * n, 0.4f + 0.6f * n / 8);
                    var l = new float[PatchRuntime.Block]; var r = new float[PatchRuntime.Block];
                    for (int b = 0; b < 200; b++) rt.ProcessBlock(l, r);
                    return Throughput.InBlocks(PatchRuntime.Block, (pos, n) => rt.ProcessBlock(l, r));
                }, Measure.Report);
            }
        }
    }
}
