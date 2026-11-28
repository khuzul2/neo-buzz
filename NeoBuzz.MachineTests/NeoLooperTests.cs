using System.Xml.Serialization;
using NeoBuzz.Dsp;
using NeoBuzz.Looper;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Looper (docs/specs/neo-looper.md): the acceptance gates, measured, with signals made in code.</summary>
    [TestFixture]
    public class NeoLooperTests
    {
        const int Rate = LooperRig.Rate;
        static ChopRig.Seg[] Plan(double bpm, double beat = 0) => ChopRig.Constant(bpm, beat);
        static int Bar(double bpm, int beats = 4) => (int)(ChopRig.Spb(bpm) * beats);

        static LooperRig.Trig T(int sample, int param) => new LooperRig.Trig(sample, param);

        // ---------------------------------------------------------------- cycle and length

        [Test]
        public void SyncStartsAndStopsLandOnTheBarSample()
        {
            // 113 BPM: a bar is 101,952 samples, which is not a block multiple, so the landing sample is inside a block.
            int bar = Bar(113);
            Assert.That(bar % 256, Is.Not.EqualTo(0));
            var v = LooperRig.Fresh();
            var e = LooperRig.Engine(v);
            int len = bar * 4;
            var x = ChopRig.Ramp(len);
            var trigs = new[] { T(5000, P.Record), T(bar + 4000, P.Record) };    // press in bar 1: records from bar 2 and closes at bar 3... one bar long
            var (l, _) = LooperRig.Render(e, v, x, (float[])x.Clone(), Plan(113), 256, trigs);
            Assert.That(e.State, Is.EqualTo(LooperState.Playing));
            Assert.That(e.LoopFrames, Is.EqualTo(bar), "one whole bar");
            var (loop, _) = LooperRig.Loop(e);
            int F = e.CrossfadeFrames;
            // The take begins on the bar line: frame F of the loop is the input F samples after sample `bar`.
            for (int j = F; j < F + 64; j++) Assert.That(ChopRig.FromRamp(loop[j]), Is.EqualTo(bar + j), $"frame {j}");
        }

        [Test]
        public void SyncPlaysTheLoopFromTheLandingSample()
        {
            int bar = Bar(113);
            var v = LooperRig.Fresh(x => LooperRig.Switch(x, P.Monitor, false));
            var e = LooperRig.Engine(v);
            int len = bar * 4;
            var x0 = ChopRig.Ramp(len);
            var (l, _) = LooperRig.Render(e, v, x0, (float[])x0.Clone(), Plan(113), 256, new[] { T(5000, P.Record), T(bar + 4000, P.Record) });
            int F = e.CrossfadeFrames;
            // With the monitor off only the loop is heard: from the second bar line it plays frame 0, 1, 2 ... of the loop (fading in over F samples).
            for (int j = F; j < F + 200; j++) Assert.That(ChopRig.FromRamp(l[2 * bar + j]), Is.EqualTo(bar + j), $"sample {j}");
            Assert.That(l[2 * bar - 1], Is.EqualTo(0f), "silent before the bar line (monitor off)");
        }

        [Test]
        public void FreeTakeSetsTheLengthExactly()
        {
            var v = LooperRig.Fresh(x => { LooperRig.Choice(x, P.Sync, "Free"); LooperRig.Switch(x, P.Monitor, false); });
            var e = LooperRig.Engine(v);
            int len = 20000;
            var x0 = ChopRig.Ramp(len);
            // In Free a trigger lands on the first sample of its block: 1000 is in the block that starts at 768, 8200 in the one at 8192.
            var (l, _) = LooperRig.Render(e, v, x0, (float[])x0.Clone(), Plan(120), 256, new[] { T(1000, P.Record), T(8200, P.Record) });
            Assert.That(e.LoopFrames, Is.EqualTo(8192 - 768));
            var (loop, _) = LooperRig.Loop(e);
            int F = e.CrossfadeFrames;
            for (int j = F; j < F + 32; j++) Assert.That(ChopRig.FromRamp(loop[j]), Is.EqualTo(768 + j));
            for (int j = F; j < F + 100; j++) Assert.That(ChopRig.FromRamp(l[8192 + j]), Is.EqualTo(768 + j), "playing from the closing block's first sample");
        }

        [Test]
        public void ATwoBarTakeAt120BpmIsFourSecondsAndMultiplyRepeatsIt()
        {
            int bar = Bar(120);
            Assert.That(bar, Is.EqualTo(96000));
            var v = LooperRig.Fresh(x => LooperRig.Choice(x, P.MultiplyBy, "x3"));
            var e = LooperRig.Engine(v);
            var (dl, dr) = ChopRig.DrumLoop(bar * 12);
            // Press in bar 1 (records from bar 2), press in bar 3 (closes at bar 4: 2 bars), Multiply in bar 5.
            var trigs = new[] { T(1000, P.Record), T(bar * 2 + 500, P.Record), T(bar * 4 + 500, P.Multiply) };
            LooperRig.Render(e, v, dl, dr, Plan(120), 256, trigs, afterBlock: null);
            Assert.That(e.LoopFrames, Is.EqualTo(bar * 2 * 3), "x3");
            Assert.That(e.LoopFrames / (double)Rate, Is.EqualTo(12.0).Within(1e-12));
            var (m, _) = LooperRig.Loop(e);
            // The take before the multiply: record the same again without it.
            var v2 = LooperRig.Fresh();
            var e2 = LooperRig.Engine(v2);
            LooperRig.Render(e2, v2, dl, dr, Plan(120), 256, new[] { T(1000, P.Record), T(bar * 2 + 500, P.Record) });
            Assert.That(e2.LoopFrames / (double)Rate, Is.EqualTo(4.0).Within(1e-12), "a 2-bar take at 120 BPM is 4.000 s");
            var (take, _) = LooperRig.Loop(e2);
            for (int i = 0; i < m.Length; i++)
                if (BitConverter.SingleToInt32Bits(m[i]) != BitConverter.SingleToInt32Bits(take[i % take.Length])) Assert.Fail($"frame {i}");
        }

        [Test]
        public void ABarIsTheSongsBar()
        {
            // A 3/4 song: a bar is three beats, so a take from one bar line to the next is 72,000 samples at 120 BPM, and starts land on 3-beat lines.
            var v = LooperRig.Fresh();
            var e = LooperRig.Engine(v);
            int len = 72000 * 4;
            var x = ChopRig.Ramp(len);
            LooperRig.Render(e, v, x, (float[])x.Clone(), Plan(120), 256, new[] { T(1000, P.Record), T(72000 + 1000, P.Record) }, beatsPerBar: 3);
            Assert.That(e.LoopFrames, Is.EqualTo(72000));
            var (loop, _) = LooperRig.Loop(e);
            int F = e.CrossfadeFrames;
            for (int j = F; j < F + 32; j++) Assert.That(ChopRig.FromRamp(loop[j]), Is.EqualTo(72000 + j), "the take began on the 3-beat line");
        }

        [Test]
        public void ASeekKeepsTheLoopInTimeWithTheSong()
        {
            int bar = Bar(120);
            var v = LooperRig.Fresh(x => LooperRig.Switch(x, P.Monitor, false));
            var e = LooperRig.Engine(v);
            int len = bar * 9;
            var x = ChopRig.Ramp(len);
            // The take is bars 2 and 3 (2 bars, 8 beats); it plays from bar 4 (beat 12). At sample bar*6 the song jumps 1.5 bars (6 beats) forward.
            int jump = bar * 6;
            var plan = new[] { new ChopRig.Seg(0, 0, ChopRig.Spb(120)), new ChopRig.Seg(jump, 24 + 6, ChopRig.Spb(120)) };
            LooperRig.Render(e, v, x, (float[])x.Clone(), plan, 256, new[] { T(1000, P.Record), T(bar * 2 + 500, P.Record) });
            var (loop, _) = LooperRig.Loop(e);
            // Re-render, now capturing the output.
            var v2 = LooperRig.Fresh(q => LooperRig.Switch(q, P.Monitor, false));
            var e2 = LooperRig.Engine(v2);
            var (o, _) = LooperRig.Render(e2, v2, x, (float[])x.Clone(), plan, 256, new[] { T(1000, P.Record), T(bar * 2 + 500, P.Record) });
            int F = e2.CrossfadeFrames;
            // Before the jump the loop plays its frame (t - 3 bars) mod 192000 (the take closed at bar 3 = beat 12, the loop head there).
            // After it the song is 6 beats ahead of where its samples are, so the loop is 6 beats (144,000 frames) ahead as well.
            int n = loop.Length;
            int ahead = 6 * 24000;
            for (int j = F + 8; j < F + 400; j++)
            {
                int t = jump + j;
                int want = ((t - bar * 3) + ahead) % n;
                if (BitConverter.SingleToInt32Bits(o[t]) != BitConverter.SingleToInt32Bits(loop[want])) Assert.Fail($"after the jump, sample {j}: {o[t]:R} but loop frame {want} is {loop[want]:R}");
            }
        }

        // ---------------------------------------------------------------- overdub and undo

        static (float[] Before, float[] After, float[] Undone, float[] Redone, int Start, int Count) OverdubRun(float fb)
        {
            var v = LooperRig.Fresh(x => { LooperRig.Choice(x, P.Sync, "Free"); LooperRig.Phys(x, P.Feedback, fb); });
            var e = LooperRig.Engine(v);
            int N = 47872;
            int len = 96000 + N * 3;
            var inL = ChopRig.Ramp(len);
            for (int i = 0; i < len; i++) if (i >= 4096 + N) inL[i] = 0.25f;           // the overdub input is a constant
            var inR = (float[])inL.Clone();
            var trigs = new List<LooperRig.Trig> { T(4096, P.Record), T(4096 + N, P.Record) };    // the take: 48,000 frames... (to the block start)
            int a = 4096 + N + 5 * 256, b = a + 60 * 256;
            trigs.Add(T(a, P.Record));                // overdub in
            trigs.Add(T(b, P.Record));                // overdub out
            int u = b + 1024, r = u + 1024;
            trigs.Add(T(u, P.Undo));
            trigs.Add(T(r, P.Redo));
            LooperEngineProbe probe = new LooperEngineProbe();
            float[] before = null, after = null, undone = null, redone = null;
            int start = -1;
            LooperRig.Render(e, v, inL, inR, ChopRig.Constant(120), 256, trigs, afterBlock: pos =>
            {
                if (pos == a - 256 + 0 && before == null) { LooperRig.Loop(e).L.CopyTo(before = new float[e.LoopFrames], 0); }
                if (pos == a) start = e.Position;
                if (pos == u - 256 + 0) after = LooperRig.Loop(e).L;
                if (pos == r - 256 + 0) undone = LooperRig.Loop(e).L;
            });
            redone = LooperRig.Loop(e).L;
            return (before, after, undone, redone, start, (b - a));
        }

        sealed class LooperEngineProbe { }

        [TestCase(1f)]
        [TestCase(0.6f)]
        public void OverdubUndoRedoAreSampleExact(float fb)
        {
            var (before, after, undone, redone, start, count) = OverdubRun(fb);
            Assert.That(before, Is.Not.Null);
            int N = before.Length;
            // Wait: `start` is read after the block that holds the trigger, so find the pass's first frame from the data instead.
            int first = -1;
            for (int i = 0; i < N && first < 0; i++) if (BitConverter.SingleToInt32Bits(after[i]) != BitConverter.SingleToInt32Bits(before[i])) first = i;
            Assert.That(first, Is.GreaterThanOrEqualTo(0), "the pass changed the loop");
            float og = fb;
            int steady = 0;
            for (int i = 0; i < N; i++)
            {
                float old = before[i];
                bool same = BitConverter.SingleToInt32Bits(after[i]) == BitConverter.SingleToInt32Bits(old);
                float expect = old * og + 0.25f;
                bool sum = BitConverter.SingleToInt32Bits(after[i]) == BitConverter.SingleToInt32Bits(expect);
                if (!same && !sum) continue;   // inside a punch ramp
                if (sum && !same) steady++;
            }
            Assert.That(steady, Is.GreaterThan(count - 2 * 600), "most of the pass is exactly old x feedback + input");
            // No frame outside the pass changed: every frame is either untouched, exactly old x feedback + input, or inside one of the two ramps.
            int ramped = 0;
            for (int i = 0; i < N; i++)
            {
                bool same = BitConverter.SingleToInt32Bits(after[i]) == BitConverter.SingleToInt32Bits(before[i]);
                bool sum = BitConverter.SingleToInt32Bits(after[i]) == BitConverter.SingleToInt32Bits(before[i] * og + 0.25f);
                if (!same && !sum) ramped++;
            }
            Assert.That(ramped, Is.LessThanOrEqualTo(2 * 600));
            for (int i = 0; i < N; i++)
            {
                Assert.That(BitConverter.SingleToInt32Bits(undone[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(before[i])), $"undo, frame {i}");
                Assert.That(BitConverter.SingleToInt32Bits(redone[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(after[i])), $"redo, frame {i}");
            }
        }

        // ---------------------------------------------------------------- clicks

        static (float[] L, float[] R)[] sources;
        static (float[] L, float[] R)[] Sources
        {
            get
            {
                if (sources == null)
                {
                    int len = 96000 * 8;
                    sources = new[] { LooperRig.DrumsSource(len), LooperRig.ChordSource(len) };
                }
                return sources;
            }
        }

        [TestCase(0, "Neo Drums loop")]
        [TestCase(1, "Neo Poly pad")]
        public void NoClickAtTheLoopPointOrAnyPunchInOrOut(int source, string what)
        {
            var (sl, sr) = Sources[source];
            foreach (var (name, script) in new[] { ("the performance", LooperRig.Script()), ("every edit", LooperRig.EditScript()) })
                foreach (bool monitor in new[] { false, true })
                {
                    var v = LooperRig.Fresh(x => LooperRig.Switch(x, P.Monitor, monitor));
                    var e = LooperRig.Engine(v);
                    var (ol, or) = LooperRig.Render(e, v, sl, sr, Plan(120), 256, script);
                    var dry = ClickDetector.Find(sl);
                    var clicks = ClickDetector.Find(ol);
                    var clicksR = ClickDetector.Find(or);
                    Assert.That(clicks.Count - dry.Count, Is.LessThanOrEqualTo(0), $"{what}, {name}, monitor {monitor}: clicks at {string.Join(",", clicks)} (the source alone has {dry.Count})");
                    Assert.That(clicksR.Count, Is.LessThanOrEqualTo(clicks.Count + dry.Count + 1), $"{what}, {name}, monitor {monitor}: right");
                }
        }

        // ---------------------------------------------------------------- amount, mix, memory

        [TestCase(P.Amount)]
        [TestCase(P.Mix)]
        public void ZeroIsTheInputBitForBitWithALoopPlaying(int which)
        {
            var v = LooperRig.Fresh(x => LooperRig.Choice(x, P.Sync, "Free"));
            var e = LooperRig.Engine(v);
            var (dl, dr) = ChopRig.DrumLoop(Rate * 4);
            var trigs = new[] { T(1000, P.Record), T(24000, P.Record) };
            LooperRig.Render(e, v, dl, dr, Plan(120), 256, trigs);
            Assert.That(e.State, Is.EqualTo(LooperState.Playing));
            LooperRig.Phys(v, which, 0);
            var (ol, or) = LooperRig.Render(e, v, dl, dr, Plan(120), 256, null);
            // The stage glides to its target over 20 ms; after that the output is the input.
            for (int i = 4096; i < dl.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(ol[i]) != BitConverter.SingleToInt32Bits(dl[i])) Assert.Fail($"left {i}: {ol[i]:R} against {dl[i]:R}");
                if (BitConverter.SingleToInt32Bits(or[i]) != BitConverter.SingleToInt32Bits(dr[i])) Assert.Fail($"right {i}");
            }
        }

        [Test]
        public void NothingIsAllocatedWhileRecordingAndOverdubbing()
        {
            var v = LooperRig.Fresh(x => LooperRig.Choice(x, P.Sync, "Free"));
            var e = LooperRig.Engine(v);
            var (dl, dr) = ChopRig.DrumLoop(Rate * 12);
            var l = new float[256];
            var r = new float[256];
            double spb = ChopRig.Spb(120), beat = 0;
            // 2000 blocks: record (to block 400), play, overdub (410 to 700), undo, redo, multiply, stop, play, clear, undo, with the parameters moving.
            var pulses = new Dictionary<int, int>
            {
                [10] = P.Record, [400] = P.Record, [410] = P.Record, [700] = P.Record, [720] = P.Undo, [740] = P.Redo, [760] = P.Multiply,
                [900] = P.Record, [1100] = P.Record, [1300] = P.Stop, [1400] = P.Play, [1600] = P.Clear, [1700] = P.Undo,
            };
            int last = -1;
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                if (last >= 0) v.Set(last, 0);
                last = pulses.TryGetValue(b, out int p) ? p : -1;
                if (last >= 0) v.Set(last, 1);
                v.Set(P.Feedback, 500 + (b * 37) % 501);
                v.Set(P.Crossfade, 100 + (b * 53) % 800);
                v.Set(P.Mix, 600 + (b * 29) % 401);
                v.Set(P.Amount, 800 + (b * 83) % 1201);
                v.Set(P.Reverse, b / 300 % 2);
                v.Set(P.HalfSpeed, b / 450 % 2);
                int pos = (b % 1400) * 256;
                Array.Copy(dl, pos, l, 0, 256);
                Array.Copy(dr, pos, r, 0, 256);
                e.SetTransport(true, beat, spb, 4);
                e.Process(l, r, 256);
                beat += 256 / spb;
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void TheLoopFeedbackTailHasNoDenormals()
        {
            var v = LooperRig.Fresh(x => { LooperRig.Choice(x, P.Sync, "Free"); LooperRig.Phys(x, P.Feedback, 0.3); });
            var e = LooperRig.Engine(v);
            var x0 = LooperRig.Constant(Rate * 8, 0.5f);
            var trigs = new List<LooperRig.Trig> { T(1000, P.Record), T(12000, P.Record), T(13000, P.Record), T(Rate * 7, P.Record) };
            LooperRig.Render(e, v, x0, (float[])x0.Clone(), Plan(120), 256, trigs);
            var (loop, _) = LooperRig.Loop(e);
            foreach (var s in loop) if (s != 0f && Math.Abs(s) < 1.17549435E-38f) Assert.Fail("a denormal in the loop");
        }

        // ---------------------------------------------------------------- saving

        [Test]
        public void TheLoopRoundTripsBitExactThroughTheSongsStateAndPlaysTheSame()
        {
            var v = LooperRig.Fresh(x => LooperRig.Choice(x, P.Sync, "Free"));
            var e = LooperRig.Engine(v);
            var (dl, dr) = ChopRig.DrumLoop(Rate * 3);
            var plan = Plan(120);
            LooperRig.Render(e, v, dl, dr, plan, 256, new[] { T(1000, P.Record), T(60000, P.Record), T(70000, P.Stop) });
            Assert.That(e.State, Is.EqualTo(LooperState.Stopped));
            var (loop, loopR) = LooperRig.Loop(e);
            e.Snapshot(out _, out _, out int n, out double beats);

            // The state, as the host saves it: the machine's XML.
            var saved = LooperCodec.Encode(loop, loopR, n, Rate, beats);
            var ser = new XmlSerializer(typeof(LooperSaved));
            string xml;
            using (var w = new StringWriter()) { ser.Serialize(w, saved); xml = w.ToString(); }
            LooperSaved back;
            using (var rd = new StringReader(xml)) back = (LooperSaved)ser.Deserialize(rd);
            Assert.That(LooperCodec.TryDecode(back, out var l2, out var r2), Is.True);
            Assert.That(l2.Length, Is.EqualTo(n));
            for (int i = 0; i < n; i++)
            {
                Assert.That(BitConverter.SingleToInt32Bits(l2[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(loop[i])));
                Assert.That(BitConverter.SingleToInt32Bits(r2[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(loopR[i])));
            }

            // A song that was reopened plays the same: a second machine loads the state and both play from the same point.
            var vb = LooperRig.Fresh(x => LooperRig.Choice(x, P.Sync, "Free"));
            var eb = LooperRig.Engine(vb);
            eb.Load(l2, r2, back.Frames, back.LoopBeats);
            Assert.That(eb.State, Is.EqualTo(LooperState.Stopped));
            var silence = new float[Rate * 2];
            var (oa, _) = LooperRig.Render(e, v, silence, (float[])silence.Clone(), plan, 256, new[] { T(256, P.Play) });
            var (ob, _) = LooperRig.Render(eb, vb, silence, (float[])silence.Clone(), plan, 256, new[] { T(256, P.Play) });
            for (int i = 0; i < oa.Length; i++)
                if (BitConverter.SingleToInt32Bits(oa[i]) != BitConverter.SingleToInt32Bits(ob[i])) Assert.Fail($"sample {i}");
            Assert.That(oa.Max(Math.Abs), Is.GreaterThan(0.01f), "the loop is audible");
        }

        [TestCase(0, "Neo Drums loop")]
        [TestCase(1, "Neo Poly pad")]
        public void TheLoopCostsAboutAThirdOfAMegabyteASecondInTheSong(int source, string what)
        {
            var (l, r) = Sources[source];
            int n = Rate * 4;                                       // a 4 s loop: 2 bars at 120 BPM
            var saved = LooperCodec.Encode(l.AsSpan(Rate, n).ToArray(), r.AsSpan(Rate, n).ToArray(), n, Rate, 8);
            var ser = new XmlSerializer(typeof(LooperSaved));
            using var w = new StringWriter();
            ser.Serialize(w, saved);
            double mb = w.ToString().Length / 1e6;
            TestContext.Out.WriteLine($"{what}: a 4 s stereo loop is {mb:0.00} MB of machine state ({mb / 4:0.00} MB a second; raw float32 is {n * 8 / 1e6:0.00} MB)");
            Assert.That(mb / 4, Is.LessThan(0.5), what);
        }

        [Test]
        public void ADamagedLoopLoadsAsNoLoop()
        {
            var x = ChopRig.Ramp(5000);
            var saved = LooperCodec.Encode(x, x, 5000, Rate, 0);
            saved.Hash = new string('0', 64);
            Assert.That(LooperCodec.TryDecode(saved, out _, out _), Is.False);
            saved = LooperCodec.Encode(x, x, 5000, Rate, 0);
            saved.Data = saved.Data.Substring(0, saved.Data.Length / 2);
            Assert.That(LooperCodec.TryDecode(saved, out _, out _), Is.False);
            Assert.That(LooperCodec.TryDecode(new LooperSaved(), out _, out _), Is.False);
        }

        [Test]
        public void MemoryFollowsTheLimitsAndStaysUnderTheCap()
        {
            foreach (int choice in new[] { 0, 1, 2, 3 })
                foreach (int levels in new[] { 1, 4, 8 })
                {
                    LooperEngine.Plan(Rate, choice, levels, out int frames, out int inForce);
                    long bytes = (long)(inForce + 2) * frames * 8 + frames;
                    Assert.That(bytes, Is.LessThanOrEqualTo(LooperEngine.MemoryCap), $"{choice} {levels}");
                    Assert.That(inForce, Is.GreaterThanOrEqualTo(1));
                    Assert.That(inForce, Is.LessThanOrEqualTo(levels));
                }
            LooperEngine.Plan(Rate, 1, 4, out int f30, out int l30);
            Assert.That(f30, Is.EqualTo(30 * Rate));
            Assert.That(l30, Is.EqualTo(4));
            Assert.That((l30 + 2) * f30 * 8L / 1e6, Is.EqualTo(69.1).Within(0.1), "the default pool is 69 MB at 48 kHz");
        }
    }
}
