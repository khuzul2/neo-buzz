using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Patch;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Patch NP0, the runtime gate (docs/specs/neo-patch.md §5, docs/specs/neo-patch-np0.md): the compiler (order, cycles, Feedback's
    /// block delay, dead nodes), the nodes, per-voice processing across voices, the three lane implementations giving the same bits, a render
    /// pinned across platforms, no allocation on the audio thread, the NaN guard, the graph swap, and the CPU cost of a patch against the
    /// closest equivalent sound in Dye Essence (the go criterion: 20 nodes, 8 voices, at most 1.5 times Dye's cost).
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
            Measure.Report($"Neo Patch: the 20-node patch compiles to {r.Patch.Jobs.Length} jobs ({r.Patch.Jobs.Count(j => j is MixJob)} of them input mixes) and {r.Patch.Buffers} buffers");
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

        /// <summary>Loudest bin more than 10 bins from any harmonic of the fundamental (a Hann window's sidelobes are under -75 dB from 8 bins out), relative to the fundamental.</summary>
        static double AliasFloorDb(float[] x, double hz, int n = 65536)
        {
            var mag = Fft.Magnitudes(Windowed(x, 8192, n), 0, n);
            double bin = hz * n / Rate;
            int fund = (int)Math.Round(bin);
            double worst = 1e-30; int wk = 0;
            for (int k = 40; k < mag.Length - 1 && k * (double)Rate / n < 20000; k++)
            {
                double h = Math.Round(k / bin);
                if (h >= 1 && Math.Abs(k - h * bin) <= 10) continue;
                if (mag[k] > worst) { worst = mag[k]; wk = k; }
            }
            return 20 * Math.Log10(worst / mag[fund]);
        }

        [TestCase(0, 440.0)]
        [TestCase(1, 440.0)]
        [TestCase(2, 440.0)]
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

        [TestCase(0, 1250.0, 0)]
        [TestCase(1, 1250.0, 0)]
        [TestCase(0, 4400.0, 0)]
        [TestCase(0, 1250.0, 1)]
        [TestCase(1, 1250.0, 1)]
        [TestCase(0, 4400.0, 1)]
        [TestCase(2, 1250.0, 1)]
        public void OscillatorsAreBandLimited(int shape, double hz, int quality)
        {
            double note = 69 + 12 * Math.Log2(hz / 440);
            var g = Simple(shape, (float)(note - 69));
            g.Nodes.First(n => n.Kind == NodeKind.Osc).Set(NodeInfo.OscQuality, quality);
            var host = new PatchHost(g, 1);
            var (l, _) = host.Render(new[] { new NoteEvent(0, 69, 1f) }, 8192 + 65536 + 64);
            double floor = AliasFloorDb(l, hz);
            Measure.Report($"Neo Patch: Osc shape {shape} at {hz:F0} Hz, {(quality == 1 ? "four-point" : "two-point")} band limiting: aliasing floor {floor:F1} dB ({RuntimeInfo()})");
            Assert.That(floor, Is.LessThan(shape == 2 ? -65 : quality == 1 ? (hz > 3000 ? -38 : -45) : (hz > 3000 ? -25 : -35)), "dB below the fundamental, beyond ±10 bins of every harmonic");
        }

        [Test]
        public void VoicesAreIndependentAndSumInOrder()
        {
            // Two notes at once are the sum of each alone: per-voice nodes keep every voice's state to itself (no crosstalk across the lanes).
            var g = PatchTemplates.Core8();
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

        // ------------------------------------------------------------------ determinism

        const ulong PinnedBig20 = 0x9D77BCC6A1411EF0UL;

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

        [Test]
        public void ASwapCrossfadesAndDoesNotClick()
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

        // ------------------------------------------------------------------ CPU against Dye Essence

        static NoteBank DyeBank(LaneKind kind, int oversample, bool big)
        {
            var b = new NoteBank(kind);
            b.S.Oversample = oversample;
            b.SetSampleRate(Rate);
            var s = b.S;
            b.Analog.Basic = TableBank.GetNow(0);
            b.Analog.Pulse = TableBank.GetNow(1);
            foreach (var o in s.Osc) { o.Type = SlotType.Analog; o.Unison = 1; }
            s.Osc[0].Shape = AnalogShape.Saw; s.Osc[0].Warp1 = Warp.Fm; s.Osc[0].Amount1 = 0.4f;
            s.Osc[1].Shape = AnalogShape.Square; s.Osc[1].Transpose = 12; s.Osc[1].Level = 0.5f;
            s.Filter[0].Type = FilterType.Lp12; s.Filter[0].Cutoff = 1200; s.Filter[0].Reso = 0.2f; s.Filter[0].Env = 2; s.Filter[0].Key = 0;
            s.Amp.A = 0.005f; s.Amp.D = 0.3f; s.Amp.S = 1; s.Amp.R = 0.4f;
            s.Flt.A = 0.01f; s.Flt.D = 0.5f; s.Flt.S = 0.3f; s.Flt.R = 0.5f;
            if (!big)
            {
                s.Osc[0].Mod = ModSource.OscB;
                s.Osc[2].Type = SlotType.Off;
                s.Filter[1].Type = FilterType.Off;
                return b;
            }
            // Twenty nodes' worth: the FM modulator is its own silent sine slot, the detuned saw is a second unison voice, the sub, a
            // second filter, a free LFO and a modulation envelope on routes.
            s.Osc[0].Mod = ModSource.OscC; s.Osc[0].Unison = 2; s.Osc[0].Detune = 12; s.Osc[0].Spread = 0.3f;
            s.Osc[2].Shape = AnalogShape.Sine; s.Osc[2].Transpose = 12; s.Osc[2].Level = 0;
            s.Sub.Level = 0.4f; s.Sub.ToF2 = 1f; s.SubShape = SubShape.Sine;
            s.Filter[1].Type = FilterType.Lp12; s.Filter[1].Cutoff = 3000; s.Filter[1].Reso = 0.2f; s.Filter[1].Env = 1; s.Filter[1].Key = 0;
            s.Lfo[0].Rate = 8;
            s.ModEnv[0].A = 0.002f; s.ModEnv[0].D = 0.25f; s.ModEnv[0].S = 0.2f;
            s.Routes[0].Source = RouteSource.Lfo1; s.Routes[0].Dest = RouteDest.F1Cutoff; s.Routes[0].Amount = 0.15f;
            s.Routes[1].Source = RouteSource.ModEnv1; s.Routes[1].Dest = RouteDest.AWarp1; s.Routes[1].Amount = 0.4f;
            return b;
        }

        /// <summary>× real time (wall) of <paramref name="process"/> over <paramref name="seconds"/> of audio in 256-sample blocks, and the process's CPU time as the same figure.</summary>
        static (double Wall, double Cpu) Speed(Action<float[], float[], int> process, double seconds)
        {
            var l = new float[256];
            var r = new float[256];
            for (int i = 0; i < 40; i++) process(l, r, 256);
            int blocks = (int)(seconds * Rate / 256);
            var p = Process.GetCurrentProcess();
            var cpu0 = p.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < blocks; i++) process(l, r, 256);
            double wall = sw.Elapsed.TotalSeconds;
            p.Refresh();
            double cpu = (p.TotalProcessorTime - cpu0).TotalSeconds;
            double audio = blocks * 256.0 / Rate;
            return (audio / wall, audio / Math.Max(1e-9, cpu));
        }

        static Action<float[], float[], int> PatchProcess(PatchGraph g, LaneKind kind, int tick, int voices = 8)
        {
            var rt = new PatchRuntime(Rate, kind, tick) { Voices = voices, Volume = 0.25f };
            rt.SetGraph(g);
            for (int n = 0; n < voices; n++) rt.NoteOn(n, 36 + 3 * n, 0.4f + 0.6f * n / voices);
            return rt.Process;
        }

        static Action<float[], float[], int> DyeProcess(LaneKind kind, int oversample, bool big, int notes = 8)
        {
            var b = DyeBank(kind, oversample, big);
            for (int n = 0; n < notes; n++) b.NoteOn(n, 36 + 3 * n, 0.4f + 0.6f * n / notes);
            return (l, r, n) => b.Process(l, r, n);
        }

        /// <summary>Best (fastest) of <paramref name="reps"/> alternating runs of each: other load only ever adds time, and alternating keeps a drifting runner fair to both.</summary>
        static (double D, double P, double Dcpu, double Pcpu) Race(Func<Action<float[], float[], int>> dye, Func<Action<float[], float[], int>> patch, double seconds, int reps = 5)
        {
            double d = 0, p = 0, dc = 0, pc = 0;
            var da = dye();
            var pa = patch();
            for (int i = 0; i < reps; i++)
            {
                var (dw, dcpu) = Speed(da, seconds);
                var (pw, pcpu) = Speed(pa, seconds);
                d = Math.Max(d, dw); p = Math.Max(p, pw); dc = Math.Max(dc, dcpu); pc = Math.Max(pc, pcpu);
            }
            return (d, p, dc, pc);
        }

        static PatchGraph WithQuality(PatchGraph g, int quality)
        {
            foreach (var n in g.Nodes) if (n.Kind == NodeKind.Osc) n.Set(NodeInfo.OscQuality, quality);
            return g;
        }

        [Test]
        public void WhereTheTimeGoes()
        {
            // The 20-node patch's cost by control interval (the CV rate: 64 is once a block, 1 is audio rate) and by oscillator band limiting,
            // and each job's share of a block (Stopwatch ticks around every job, so the shares carry the timer's own cost: read them as relative).
            var kind = Avx2Lanes.Supported ? LaneKind.Avx2 : LaneKind.Pair;
            string info = $"{kind}, {RuntimeInfo()}";
            double Cost(PatchGraph g, int tick, int voices = 8) => Speed(PatchProcess(g, kind, tick, voices), 4).Cpu;
            foreach (int tick in new[] { 64, 32, 16, 8, 4, 2, 1 })
            {
                double best = 0;
                for (int i = 0; i < 3; i++) best = Math.Max(best, Cost(PatchTemplates.Big20(), tick));
                Measure.Report($"Neo Patch 20 nodes, 8 voices, control interval {tick} ({info}): {best:F1}× real time ({100 / best:F2} % of a core)");
            }
            foreach (int q in new[] { 0, 1 })
            {
                double best = 0;
                for (int i = 0; i < 3; i++) best = Math.Max(best, Cost(WithQuality(PatchTemplates.Big20(), q), 16));
                Measure.Report($"Neo Patch 20 nodes, 8 voices, {(q == 1 ? "four-point" : "two-point")} oscillators ({info}): {best:F1}× real time ({100 / best:F2} % of a core)");
            }
            foreach (int voices in new[] { 1, 4, 8, 16 })
            {
                double best = 0;
                for (int i = 0; i < 3; i++) best = Math.Max(best, Cost(PatchTemplates.Big20(), 16, voices));
                Measure.Report($"Neo Patch 20 nodes, {voices} voices, tick 16 ({info}): {best:F1}× real time ({100 / best:F2} % of a core)");
            }
            var rt = new PatchRuntime(Rate, kind, 16) { Voices = 8, Volume = 0.25f };
            var g20 = PatchTemplates.Big20();
            var compiled = rt.Compile(g20).Patch;
            rt.Publish(compiled);
            for (int n = 0; n < 8; n++) rt.NoteOn(n, 36 + 3 * n, 0.8f);
            var l = new float[64];
            var r = new float[64];
            for (int b = 0; b < 400; b++) rt.ProcessBlock(l, r);
            rt.Profile = new long[compiled.Jobs.Length];
            for (int b = 0; b < 4000; b++) rt.ProcessBlock(l, r);
            long total = rt.Profile.Sum();
            var rows = compiled.Jobs.Select((j, i) => (Name: j.GetType().Name.Replace("Job", "") + (j.NodeId > 0 ? "#" + j.NodeId : ""), Ticks: rt.Profile[i], Kind: j.GetType().Name.Replace("Job", "")));
            foreach (var grp in rows.GroupBy(x => x.Kind).OrderByDescending(x => x.Sum(y => y.Ticks)))
                Measure.Report($"Neo Patch 20 nodes profile: {grp.Key,-14} {100.0 * grp.Sum(x => x.Ticks) / total,5:F1} % of the jobs' time ({grp.Count()} jobs, {grp.Sum(x => x.Ticks) * 1e6 / Stopwatch.Frequency / 4000:F2} µs a block of {1e6 * 64 / Rate:F0} µs)");
        }

        [Test]
        public void TheTwentyNodePatchAgainstDyeEssence()
        {
            var kind = Avx2Lanes.Supported ? LaneKind.Avx2 : LaneKind.Pair;
            string info = $"{kind}, {RuntimeInfo()}";
            Console.WriteLine($"load {File.ReadAllText("/proc/loadavg").Trim()}");
            foreach (bool big in new[] { false, true })
            {
                string name = big ? "20 nodes" : "8 nodes";
                var g = big ? (Func<PatchGraph>)PatchTemplates.Big20 : PatchTemplates.Core8;
                foreach (int os in new[] { 1, 2 })
                {
                    var (d, p, dc, pc) = Race(() => DyeProcess(kind, os, big), () => PatchProcess(g(), kind, 16), 4);
                    Assert.That(d / p, Is.LessThan(1.5), $"the go criterion: {name} against Dye at {os}×");
                    Measure.Report($"Neo Patch {name}, 8 voices, tick 16 vs Dye Essence ({(big ? "3 osc + sub, 2 filters, LFO and mod env" : "2 osc FM, 1 SVF")}, {os}×) ({info}): Dye {d:F1}× real time (CPU {dc:F1}×), Neo Patch {p:F1}× (CPU {pc:F1}×): Neo Patch costs {d / p:F2}× Dye (CPU {dc / pc:F2}×)");
                }
            }
        }
    }
}
