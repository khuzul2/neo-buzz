using NeoBuzz.Chop;
using NeoBuzz.Dsp;
using static NeoBuzz.MachineTests.ChopRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Chop (docs/specs/neo-chop.md): the acceptance gates, measured, with signals made in code.</summary>
    [TestFixture]
    public class NeoChopTests
    {
        static readonly double[] Tempos = { 60, 120, 197 };
        static readonly int[] Blocks = { 16, 64, 256 };

        // ---- the modes the timing tests switch on
        static ParamValues StutterSetup() => Fresh(v =>
        {
            Switch(v, P.StutterOn, true);
            Choice(v, P.StutterEvery, "1 bar");
            Choice(v, P.Slice, "1/16");
            Choice(v, P.SliceFeel, "Straight");
            Choice(v, P.Repeats, "4");
            Phys(v, P.StutterPitch, 0);
            Phys(v, P.StutterDecay, 0);
            Phys(v, P.StutterFilter, 0);
        });

        static ParamValues RepeatSetup() => Fresh(v =>
        {
            Switch(v, P.RepeatOn, true);
            Choice(v, P.RepeatInterval, "1 bar");
            Phys(v, P.RepeatChance, 1.0);
            Choice(v, P.RepeatLength, "1/2");
            Choice(v, P.RepeatGrid, "1/8");
            Choice(v, P.RepeatFeel, "Straight");
            Phys(v, P.Variation, 0);
        });

        static ParamValues RetrigSetup(string back = "Beat") => Fresh(v =>
        {
            Switch(v, P.RetrigOn, true);
            Choice(v, P.RetrigEvery, "1 bar");
            Choice(v, P.RetrigLength, "1/2");
            Choice(v, P.RetrigGrid, "1/8");
            Choice(v, P.RetrigFeel, "Straight");
            Choice(v, P.RetrigBack, back);
        });

        static ParamValues GateSetup() => Fresh(v =>
        {
            Switch(v, P.GateOn, true);
            Choice(v, P.GateLock, "Song");
            Choice(v, P.GateRate, "1/16");
            Phys(v, P.GateAttack, 3);
            Phys(v, P.GateRelease, 30);
            Phys(v, P.GateDepth, 1.0);
        });

        static ParamValues TapeSetup() => Fresh(v =>
        {
            Switch(v, P.TapeOn, true);
            Choice(v, P.TapeEvery, "1 bar");
            Choice(v, P.StopSync, "1/4");
            Choice(v, P.StartSync, "1/8");
            Phys(v, P.TapeCurve, 0);
        });

        /// <summary>The samples a grid point of <paramref name="beat"/> is on in a plan: the first whole sample at or after it.</summary>
        static long ExpectedSample(Seg[] plan, double beat)
        {
            // The last segment that starts at or before the beat.
            int k = 0;
            for (int i = 0; i < plan.Length; i++) if (plan[i].Beat <= beat + 1e-9 && (i == 0 || plan[i].Start <= SampleOf(plan, i, beat))) k = i;
            return SampleOf(plan, k, beat);
        }

        static long SampleOf(Seg[] plan, int k, double beat) => plan[k].Start + (long)Math.Ceiling((beat - plan[k].Beat) * plan[k].Spb - 1e-6);

        // ================================================================================== timing

        [Test]
        public void TheDefaultsStutterAtTheEndOfTheBar()
        {
            var v = new ParamValues(Params.Table);
            Assert.That(v.On(P.StutterOn), Is.True, "a fresh instance does something at once");
            Assert.That(v.Choice(P.Repeats) + 1, Is.EqualTo(4));
            Assert.That(v.Value(P.Mix), Is.EqualTo(1.0));
            Assert.That(v.Value(P.Amount), Is.EqualTo(100.0).Within(0.1));
        }

        [Test, Combinatorial]
        public void EveryStutterSliceStartsWithinOneSampleOfItsGridTime([ValueSource(nameof(Tempos))] double bpm, [ValueSource(nameof(Blocks))] int block)
        {
            double spb = Spb(bpm);
            int length = (int)(spb * 4 * 6) + 5000;
            var x = Ramp(length);
            var e = Engine(StutterSetup(), logSize: 512);
            var plan = Constant(bpm);
            var (ol, _) = Render(e, x, (float[])x.Clone(), plan, block);
            var starts = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Where(ev => ev.Kind == ChopEventKind.StutterStart || ev.Kind == ChopEventKind.StutterRepeat).ToList();
            Assert.That(starts.Count(ev => ev.Kind == ChopEventKind.StutterStart), Is.EqualTo(6), "one stutter in each bar, ending on the bar lines 4, 8, ... 24");
            Assert.That(starts.Count, Is.EqualTo(6 * 4), "a start and three repeats in each window of four sixteenths");
            int s0 = (int)Math.Round(0.25 * spb);
            long windowStart = 0;
            foreach (var ev in starts)
            {
                Assert.That(Math.Abs(ev.Sample - ExpectedSample(plan, ev.Beat)), Is.LessThanOrEqualTo(1), $"{ev.Kind} {ev.Index} at beat {ev.Beat}");
                Assert.That(ev.Beat * 4, Is.EqualTo(Math.Round(ev.Beat * 4)).Within(1e-9), "on the sixteenth grid");
                if (ev.Kind == ChopEventKind.StutterStart) windowStart = ev.Sample;
                // The audio really starts there: the slice that played before it, from its first sample.
                Assert.That(FromRamp(ol[ev.Sample]), Is.EqualTo((int)(windowStart - s0)), $"{ev.Kind} {ev.Index}: the first sample of the slice");
                Assert.That(FromRamp(ol[ev.Sample - 3]), Is.Not.EqualTo((int)(windowStart - s0 - 3)), "and the sample before it is not");
            }
        }

        [Test, Combinatorial]
        public void EveryRepeatStartsWithinOneSampleOfItsGridTime([ValueSource(nameof(Tempos))] double bpm, [ValueSource(nameof(Blocks))] int block)
        {
            double spb = Spb(bpm);
            int length = (int)(spb * 4 * 6) + 5000;
            var x = Ramp(length);
            var e = Engine(RepeatSetup(), logSize: 512);
            var plan = Constant(bpm);
            var (ol, _) = Render(e, x, (float[])x.Clone(), plan, block);
            var evs = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Where(ev => ev.Kind == ChopEventKind.RepeatStart || ev.Kind == ChopEventKind.RepeatRepeat).ToList();
            Assert.That(evs.Count(ev => ev.Kind == ChopEventKind.RepeatStart), Is.EqualTo(6));
            Assert.That(evs.Count, Is.EqualTo(6 * 4), "a window of half a bar in eighths: a start and three repeats");
            int s0 = (int)Math.Round(0.5 * spb);
            long windowStart = 0;
            foreach (var ev in evs)
            {
                Assert.That(Math.Abs(ev.Sample - ExpectedSample(plan, ev.Beat)), Is.LessThanOrEqualTo(1), $"{ev.Kind} {ev.Index} at beat {ev.Beat}");
                Assert.That(ev.Beat * 2, Is.EqualTo(Math.Round(ev.Beat * 2)).Within(1e-9), "on the eighth grid");
                if (ev.Kind == ChopEventKind.RepeatStart) windowStart = ev.Sample;
                Assert.That(FromRamp(ol[ev.Sample]), Is.EqualTo((int)(windowStart - s0)), $"{ev.Kind} {ev.Index}: the first sample of the slice");
            }
        }

        [Test, Combinatorial]
        public void EveryRetriggerJumpStartsWithinOneSampleOfItsGridTime([ValueSource(nameof(Tempos))] double bpm, [ValueSource(nameof(Blocks))] int block, [Values("Beat", "Bar")] string back)
        {
            double spb = Spb(bpm);
            int length = (int)(spb * 4 * 6) + 5000;
            var x = Ramp(length);
            var e = Engine(RetrigSetup(back), logSize: 512);
            var plan = Constant(bpm);
            var (ol, _) = Render(e, x, (float[])x.Clone(), plan, block);
            var evs = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Where(ev => ev.Kind == ChopEventKind.RetrigStart || ev.Kind == ChopEventKind.RetrigStep).ToList();
            Assert.That(evs.Count(ev => ev.Kind == ChopEventKind.RetrigStart), Is.EqualTo(6));
            Assert.That(evs.Count, Is.EqualTo(6 * 4), "a window of half a bar in eighths: a start and three steps");
            double unit = back == "Beat" ? 1 : 4;
            foreach (var ev in evs)
            {
                long expected = ExpectedSample(plan, ev.Beat);
                Assert.That(Math.Abs(ev.Sample - expected), Is.LessThanOrEqualTo(1), $"{ev.Kind} {ev.Index} at beat {ev.Beat}");
                Assert.That(ev.Beat * 2, Is.EqualTo(Math.Round(ev.Beat * 2)).Within(1e-9), "on the eighth grid");
                // It jumped back to the start of the beat (or bar) it is in.
                long back0 = (long)Math.Round((ev.Beat - Math.Floor(ev.Beat / unit + 1e-9) * unit) * spb);
                Assert.That(FromRamp(ol[ev.Sample]), Is.EqualTo((int)(ev.Sample - back0)), $"{ev.Kind} {ev.Index}: reads the audio {back0} samples back");
            }
        }

        [Test, Combinatorial]
        public void EveryGateStepStartsWithinOneSampleOfItsGridTime([ValueSource(nameof(Tempos))] double bpm, [ValueSource(nameof(Blocks))] int block)
        {
            double spb = Spb(bpm);
            int length = (int)(spb * 4 * 2) + 5000;
            var e = Engine(GateSetup(), logSize: 2048);
            var plan = Constant(bpm);
            var x = Sine(length);
            Render(e, x, (float[])x.Clone(), plan, block);
            var evs = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Where(ev => ev.Kind == ChopEventKind.GateStep).ToList();
            Assert.That(evs.Count, Is.GreaterThanOrEqualTo(8 * 4 - 1));
            for (int i = 0; i < evs.Count; i++)
            {
                var ev = evs[i];
                Assert.That(Math.Abs(ev.Sample - ExpectedSample(plan, ev.Beat)), Is.LessThanOrEqualTo(1), $"step {i} at beat {ev.Beat}");
                Assert.That(ev.Beat * 4, Is.EqualTo(Math.Round(ev.Beat * 4)).Within(1e-9));
                if (i > 0) Assert.That(ev.Beat - evs[i - 1].Beat, Is.EqualTo(0.25).Within(1e-9), "every step");
                Assert.That(ev.Index, Is.EqualTo((int)(Math.Round(ev.Beat * 4) % 16)), "the step the song is in");
            }
        }

        [Test, Combinatorial]
        public void ATapeStopEndsOnTheBarAndTheStartBeginsThere([ValueSource(nameof(Tempos))] double bpm, [ValueSource(nameof(Blocks))] int block)
        {
            double spb = Spb(bpm);
            int length = (int)(spb * 12.5) + 8000;
            var e = Engine(TapeSetup(), logSize: 64);
            var plan = Constant(bpm);
            var x = Sine(length);
            var (ol, _) = Render(e, x, (float[])x.Clone(), plan, block);
            var stops = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Where(ev => ev.Kind == ChopEventKind.TapeStop).ToList();
            var starts = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Where(ev => ev.Kind == ChopEventKind.TapeStart).ToList();
            Assert.That(stops.Count, Is.EqualTo(3));
            Assert.That(starts.Count, Is.EqualTo(3));
            for (int k = 0; k < 3; k++)
            {
                double bar = 4.0 * (k + 1);
                Assert.That(Math.Abs(stops[k].Sample - ExpectedSample(plan, bar - 1.0)), Is.LessThanOrEqualTo(1), $"the stop {k}: a beat before the bar");
                Assert.That(Math.Abs(starts[k].Sample - ExpectedSample(plan, bar)), Is.LessThanOrEqualTo(1), $"the start {k}: on the bar");
                int sb = (int)starts[k].Sample;
                // Silent at the bar (the tape has stopped), back to sound a little later.
                Assert.That(Measure.Peak(ol, sb - 8, 8), Is.LessThan(0.02), "the tape is nearly silent as it stops");
                Assert.That(Measure.Peak(ol, sb + (int)(0.5 * spb) + 2000, 2000), Is.GreaterThan(0.3), "and it is back");
            }
        }

        [Test]
        public void ATempoChangeStaysOnTheGrid()
        {
            double spb120 = Spb(120), spb90 = Spb(90);
            int change = 256 * 64;                                   // a whole number of blocks of 16, 64 and 256
            var plan = new[] { new Seg(0, 0, spb120), new Seg(change, change / spb120, spb90) };
            int length = (int)(change + spb90 * 4 * 3);
            foreach (int block in Blocks)
            {
                var x = Ramp(length);
                var e = Engine(StutterSetup(), logSize: 512);
                var (ol, _) = Render(e, x, (float[])x.Clone(), plan, block);
                var evs = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Where(ev => ev.Kind == ChopEventKind.StutterStart || ev.Kind == ChopEventKind.StutterRepeat).ToList();
                Assert.That(evs.Count, Is.GreaterThanOrEqualTo(4), $"block {block}");
                bool across = false;
                foreach (var ev in evs)
                {
                    int k = ev.Sample >= change ? 1 : 0;
                    long expected = SampleOf(plan, k, ev.Beat);
                    // An event that the first tempo scheduled but the second one has moved is on the grid of the tempo it ends up in.
                    Assert.That(Math.Abs(ev.Sample - expected), Is.LessThanOrEqualTo(1), $"block {block}: {ev.Kind} {ev.Index} at beat {ev.Beat}");
                    Assert.That(ev.Beat * 4, Is.EqualTo(Math.Round(ev.Beat * 4)).Within(1e-9));
                    across |= k == 1;
                }
                Assert.That(across, Is.True, "events after the change were measured too");
            }
        }

        [Test]
        public void ALoopJumpStartsTheScheduleAgainFromTheNewPosition()
        {
            double spb = Spb(120);
            int jumpAt = 256 * 400;                                  // sample of the jump: song beat 4.27 jumps back to 0.5 (the stutter before it ended at beat 4)
            var plan = new[] { new Seg(0, 0, spb), new Seg(jumpAt, 0.5, spb) };
            int length = (int)(jumpAt + spb * 4 * 2);
            foreach (int block in Blocks)
            {
                var x = Ramp(length);
                var e = Engine(StutterSetup(), logSize: 512);
                var (ol, _) = Render(e, x, (float[])x.Clone(), plan, block);
                var evs = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Where(ev => ev.Kind == ChopEventKind.StutterStart || ev.Kind == ChopEventKind.StutterRepeat).ToList();
                var after = evs.Where(ev => ev.Sample >= jumpAt).ToList();
                Assert.That(after.Count, Is.GreaterThanOrEqualTo(4), $"block {block}: the stutter at the end of the bar after the jump");
                foreach (var ev in after)
                {
                    Assert.That(Math.Abs(ev.Sample - SampleOf(plan, 1, ev.Beat)), Is.LessThanOrEqualTo(1), $"block {block}: {ev.Kind} {ev.Index} at beat {ev.Beat}");
                    Assert.That(ev.Beat, Is.GreaterThanOrEqualTo(3.0 - 1e-9), "from the new position");
                }
                // Nothing that began before the jump goes on past it.
                Assert.That(evs.Where(ev => ev.Sample < jumpAt).Max(ev => ev.Sample), Is.LessThan(jumpAt));
            }
        }

        // ================================================================================== clean

        static IEnumerable<ParamValues> Cleans()
        {
            yield return StutterSetup();
            yield return Fresh(v => { Switch(v, P.StutterOn, true); Choice(v, P.StutterEvery, "2 beats"); Choice(v, P.Slice, "1/32"); Choice(v, P.Repeats, "8"); Phys(v, P.StutterPitch, 0); Phys(v, P.StutterDecay, 3); Phys(v, P.StutterFilter, 0); });
            yield return RepeatSetup();
            yield return RetrigSetup("Beat");
            yield return RetrigSetup("Bar");
            yield return GateSetup();
            yield return TapeSetup();
        }

        static string Describe(ParamValues v) =>
            string.Join(",", new[] { P.StutterOn, P.RepeatOn, P.RetrigOn, P.TapeOn, P.GateOn }.Zip(new[] { "stutter", "repeat", "retrig", "tape", "gate" }, (p, n) => v.On(p) ? n : null).Where(s => s != null)) + " " + v.Value(P.Slice) + "/" + v.Value(P.Repeats);

        [Test]
        public void EveryBoundaryIsCrossfadedOnASineAndOnADrumLoop([Values(120, 197)] double bpm)
        {
            double spb = Spb(bpm);
            int length = (int)(spb * 4 * 4) + 4000;
            var drums = DrumLoop(length, bpm);
            var sine = Sine(length);
            int config = 0;
            double worst = 0;
            foreach (var v in Cleans())
            {
                config++;
                foreach (var (name, input) in new[] { ("sine", (sine, sine)), ("drums", drums) })
                {
                    var e = Engine(v, logSize: 4096);
                    var (ol, or) = Render(e, input.Item1, input.Item2, Constant(bpm), 64);
                    var bounds = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Where(ev => ev.Kind != ChopEventKind.NoteStart).Select(ev => (int)ev.Sample).Where(b => b >= 4 * spb).Distinct().ToList();   // the first bar has no history for a jump back to read
                    Assert.That(bounds.Count, Is.GreaterThanOrEqualTo(3), $"{Describe(v)} on {name}: the boundaries exist");
                    foreach (int b in bounds)
                    {
                        double ratio = BoundaryRatio(ol, b, out double near, out double outside);
                        if (outside >= 1e-4) worst = Math.Max(worst, ratio);
                        // A boundary in silence, or in a quiet tail, has nothing to compare with: the size itself must be tiny.
                        if (outside < 1e-4) Assert.That(near, Is.LessThan(2e-3), $"{Describe(v)} on {name} at {b}: quiet around it");
                        else Assert.That(ratio, Is.LessThanOrEqualTo(1.5), $"{Describe(v)} on {name} at {b} (sample): |second difference| {near:G3} against {outside:G3} around it");
                    }
                    var clicks = NewClicks(input.Item1, ol).Where(c => c >= 4 * spb).ToList();
                    Assert.That(clicks.Count, Is.EqualTo(0), $"{Describe(v)} on {name}: the click detector finds {string.Join(",", clicks.Take(5))}");
                }
            }
            TestContext.Out.WriteLine($"{config} settings at {bpm} BPM: the worst boundary is {worst:0.00} times its surroundings");
        }

        // ================================================================================== Mix and Amount

        static ParamValues AllOn() => Fresh(v =>
        {
            Switch(v, P.StutterOn, true); Choice(v, P.StutterEvery, "1 beat"); Choice(v, P.Slice, "1/16"); Choice(v, P.Repeats, "2");
            Switch(v, P.RepeatOn, true); Choice(v, P.RepeatInterval, "1 bar"); Choice(v, P.RepeatLength, "1/2");
            Switch(v, P.RetrigOn, true); Choice(v, P.RetrigEvery, "1 bar");
            Switch(v, P.TapeOn, true); Choice(v, P.TapeEvery, "4 bars");
            Switch(v, P.GateOn, true); Switch(v, P.PanOn, true); Phys(v, P.PanDepth, 1.0);
        });

        [Test]
        public void AmountZeroPassesTheInputBitForBitWithEveryModeOn()
        {
            var (l, r) = DrumLoop(Rate * 6);
            var v = AllOn();
            Phys(v, P.Amount, 0);
            var (ol, or) = Render(Engine(v), l, r, Constant(120), 64);
            for (int i = 0; i < l.Length; i++)
                if (ol[i] != l[i] || or[i] != r[i]) Assert.Fail($"differs at {i}");
        }

        [Test]
        public void MixZeroPassesTheInputBitForBitWithEveryModeOn()
        {
            var (l, r) = DrumLoop(Rate * 6);
            var v = AllOn();
            Phys(v, P.Mix, 0);
            var (ol, or) = Render(Engine(v), l, r, Constant(120), 64);
            for (int i = 0; i < l.Length; i++)
                if (ol[i] != l[i] || or[i] != r[i]) Assert.Fail($"differs at {i}");
        }

        [Test]
        public void AmountAtOneHundredPercentIsTheSettingsAsTheyAreAndScalesTheRest()
        {
            var v = AllOn();
            Phys(v, P.StutterPitch, 5); Phys(v, P.StutterDecay, 4); Phys(v, P.StutterFilter, -1); Phys(v, P.RepeatChance, 0.8); Phys(v, P.GateDepth, 0.6); Phys(v, P.PanDepth, 0.5);
            var e = Engine(v);
            int[] ps = { P.StutterPitch, P.StutterDecay, P.StutterFilter, P.RepeatChance, P.GateDepth, P.PanDepth };
            var x = Sine(256);
            Render(e, x, (float[])x.Clone(), Constant(120), 256);
            foreach (int p in ps) Assert.That(e.Effective.Value(p), Is.EqualTo(v.Value(p)), $"{Params.Table[p].Name} at 100 %");
            foreach (var (amount, k) in new[] { (50.0, 0.5), (200.0, 2.0) })
            {
                Phys(v, P.Amount, amount);
                Render(e, x, (float[])x.Clone(), Constant(120), 256);
                foreach (int p in ps)
                {
                    double want = Math.Clamp(v.Value(p) * k, Math.Min(Params.Table[p].A, Params.Table[p].B), Math.Max(Params.Table[p].A, Params.Table[p].B));
                    if (Params.Table[p].Kind == MapKind.Percent) want = Math.Clamp(v.Value(p) * k, 0, 1);
                    Assert.That(e.Effective.Value(p), Is.EqualTo(want).Within(1e-9), $"{Params.Table[p].Name} at {amount} %");
                }
            }
        }

        [Test]
        public void AmountGlidingToZeroEndsExactlyAtTheInputAndMixStaysTheDryWetBlend()
        {
            var (l, r) = DrumLoop(Rate * 4);
            var v = Fresh(s => { Switch(s, P.GateOn, true); Phys(s, P.GateDepth, 1.0); });
            var e = Engine(v);
            var seg = Constant(120);
            var first = Render(e, l, r, seg, 64);
            Phys(v, P.Amount, 0);
            var second = Render(e, l, r, seg, 64);
            Assert.That(second.L.Skip(Rate).SequenceEqual(l.Skip(Rate)), Is.True, "Amount 0 after a second: the input exactly");
            Assert.That(first.L.SequenceEqual(l), Is.False, "and at 100 % the gate was cutting");
            // Mix 50 %: half the wet, half the dry.
            var v2 = Fresh(s => { Switch(s, P.GateOn, true); Phys(s, P.GateDepth, 1.0); Phys(s, P.Mix, 0.5); });
            var half = Render(Engine(v2), l, r, seg, 64);
            var full = Render(Engine(Fresh(s => { Switch(s, P.GateOn, true); Phys(s, P.GateDepth, 1.0); })), l, r, seg, 64);
            for (int i = Rate; i < l.Length; i += 97)
                Assert.That(half.L[i], Is.EqualTo(0.5f * l[i] + 0.5f * full.L[i]).Within(1e-5), $"at {i}");
        }

        // ================================================================================== notes

        [Test, Combinatorial]
        public void ANoteStartsTheStutterOnItsExactSample([Values(16, 64, 256)] int block, [Values(0, 1, 37, 130)] int into)
        {
            double spb = Spb(120);
            int length = (int)(spb * 6);
            int at = (int)(spb * 2) / block * block + into;           // a sample inside a block
            var v = Fresh(s =>
            {
                Switch(s, P.StutterOn, true); Choice(s, P.StutterTrig, "Note"); Choice(s, P.Slice, "1/16"); Choice(s, P.Repeats, "3");
                Phys(s, P.StutterPitch, 0); Phys(s, P.StutterDecay, 0); Phys(s, P.StutterFilter, 0);
            });
            var e = Engine(v, logSize: 64);
            var x = Ramp(length);
            var (ol, _) = Render(e, x, (float[])x.Clone(), Constant(120), block, new[] { (at, true, 60) });
            var start = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Single(ev => ev.Kind == ChopEventKind.StutterStart);
            Assert.That(start.Sample, Is.EqualTo(at), "the stutter starts on the note's own sample");
            Assert.That(Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Single(ev => ev.Kind == ChopEventKind.NoteStart).Sample, Is.EqualTo(at));
            int s0 = (int)Math.Round(0.25 * spb);
            int blend = (int)(0.004 * Rate) + 1;
            // Before the note: the input. A little after (the blend is over): the slice from the start.
            for (int i = at - 50; i < at; i++) Assert.That(ol[i], Is.EqualTo(x[i]), $"before the note, at {i}");
            Assert.That(FromRamp(ol[at + blend + 40]), Is.EqualTo(at - s0 + blend + 40), "the repeated slice, read from its first sample");
            Assert.That(ol[at], Is.EqualTo(x[at]), "the note's own sample is where the blend begins");
            // Three repeats, then back to the input.
            Assert.That(ol[at + 3 * s0 + blend + 40], Is.EqualTo(x[at + 3 * s0 + blend + 40]), "after its three repeats: the input");
        }

        [Test]
        public void ANoteOffEndsAHeldStutterAndTheTapeStopsOnANoteAndSpinsUpOnItsEnd()
        {
            double spb = Spb(120);
            int length = (int)(spb * 8);
            int on = (int)(spb * 2), off = (int)(spb * 4);
            var v = Fresh(s => { Switch(s, P.StutterOn, true); Choice(s, P.StutterTrig, "Note"); Choice(s, P.Slice, "1/16"); Choice(s, P.Repeats, "Hold"); Phys(s, P.StutterPitch, 0); Phys(s, P.StutterDecay, 0); Phys(s, P.StutterFilter, 0); });
            var x = Ramp(length);
            var (ol, _) = Render(Engine(v), x, (float[])x.Clone(), Constant(120), 64, new[] { (on, true, 48), (off, false, 48) });
            int s0 = (int)Math.Round(0.25 * spb);
            Assert.That(FromRamp(ol[on + 3000]), Is.EqualTo(on - s0 + 3000), "held: still repeating the first slice long after");
            Assert.That(FromRamp(ol[off - 10]) < on, Is.True, "held until the note ends: still in the held loop");
            int blend = (int)(0.004 * Rate) + 1;
            Assert.That(ol[off + blend + 5], Is.EqualTo(x[off + blend + 5]), "the note's end hands the sound back");

            var vt = Fresh(s => { Switch(s, P.TapeOn, true); Choice(s, P.TapeTrig, "Note"); Choice(s, P.StopSync, "1/4"); Choice(s, P.StartSync, "1/8"); });
            var xs = Sine(length);
            var e = Engine(vt, logSize: 64);
            var (tl, _) = Render(e, xs, (float[])xs.Clone(), Constant(120), 64, new[] { (on, true, 48), (off, false, 48) });
            var stop = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Single(ev => ev.Kind == ChopEventKind.TapeStop);
            var start = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Single(ev => ev.Kind == ChopEventKind.TapeStart);
            Assert.That(stop.Sample, Is.EqualTo(on));
            Assert.That(start.Sample, Is.EqualTo(off), "the spin-up begins on the note's end");
            Assert.That(Measure.Peak(tl, on + (int)spb + 500, (int)(spb * 0.9)), Is.LessThan(1e-3), "silent while the note is held");
            Assert.That(Measure.Peak(tl, off + (int)spb, 2000), Is.GreaterThan(0.3), "and back after the spin-up");
        }

        [Test]
        public void TheKeyOfANoteCanPickTheSlice()
        {
            double spb = Spb(120);
            int length = (int)(spb * 6);
            var v = Fresh(s => { Switch(s, P.RepeatOn, true); Choice(s, P.RepeatTrig, "Note"); Switch(s, P.NotePick, true); Choice(s, P.RepeatLength, "1/2"); });
            // Key 62 (D) is the third of the twelve: an eighth note. Key 60 (C) is a quarter.
            foreach (var (key, beats) in new[] { (62, 0.5), (60, 1.0), (66, 1.0 / 6) })
            {
                var e = Engine(v, logSize: 64);
                var x = Ramp(length);
                Render(e, x, (float[])x.Clone(), Constant(120), 64, new[] { ((int)(spb * 2), true, key) });
                var evs = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Where(ev => ev.Kind == ChopEventKind.RepeatStart || ev.Kind == ChopEventKind.RepeatRepeat).ToList();
                Assert.That(evs.Count, Is.GreaterThanOrEqualTo(2), $"key {key}");
                Assert.That(evs[1].Beat - evs[0].Beat, Is.EqualTo(beats).Within(1e-9), $"key {key}");
            }
        }

        [Test]
        public void NothingRunsWhileTheTransportIsStoppedExceptAFreeGateAndPan()
        {
            var (l, r) = DrumLoop(Rate * 3);
            var stopped = new[] { new Seg(0, 0, Spb(120), false) };
            var v = Fresh(s => { Switch(s, P.StutterOn, true); Switch(s, P.RepeatOn, true); Switch(s, P.RetrigOn, true); Switch(s, P.TapeOn, true); Choice(s, P.StutterTrig, "Both"); Switch(s, P.GateOn, true); Switch(s, P.PanOn, true); Choice(s, P.GateLock, "Song"); Choice(s, P.PanSync, "1 bar"); });
            var (ol, or) = Render(Engine(v), l, r, stopped, 64, new[] { (5000, true, 60) });
            Assert.That(ol.SequenceEqual(l) && or.SequenceEqual(r), Is.True, "synced modes and notes do nothing while stopped");
            var free = Fresh(s => { Switch(s, P.GateOn, true); Choice(s, P.GateLock, "Free"); Choice(s, P.GateRate, "1/16"); Phys(s, P.GateDepth, 1.0); Switch(s, P.PanOn, true); Choice(s, P.PanSync, "Free"); Phys(s, P.PanRate, 2.0); });
            var (fl, fr) = Render(Engine(free), l, r, stopped, 64);
            Assert.That(fl.SequenceEqual(l), Is.False, "a free gate runs while stopped");
            double diffPan = 0;
            for (int i = Rate; i < l.Length; i++) diffPan += Math.Abs(fl[i] - fr[i] - (l[i] - r[i]));
            Assert.That(diffPan, Is.GreaterThan(1), "and a free pan moves the balance");
        }

        // ================================================================================== the gate and the pan

        [Test]
        public void TheGateFollowsItsStepRowAtItsRate()
        {
            double spb = Spb(120);
            int length = (int)(spb * 10);
            var v = GateSetup();
            Choice(v, P.GateRate, "1/8");
            Phys(v, P.GateAccent, 6);
            // Steps: accent, on, off, off, ...: pattern of 16 eighths = 2 bars.
            for (int i = 0; i < 16; i++) v.Set(P.Step1 + i, 0);
            v.Set(P.Step1, 2); v.Set(P.Step1 + 1, 1);
            var x = Sine(length, 220, 0.25);
            var (ol, _) = Render(Engine(v), x, (float[])x.Clone(), Constant(120), 64);
            double eighth = spb / 2;
            double Level(int step) => Measure.Rms(ol, (int)(step * eighth + eighth * 0.4), (int)(eighth * 0.4)) / Measure.Rms(x, (int)(step * eighth + eighth * 0.4), (int)(eighth * 0.4));
            Assert.That(Measure.Db(Level(0)), Is.EqualTo(6.0).Within(0.2), "accent: +6 dB");
            Assert.That(Measure.Db(Level(1)), Is.EqualTo(0.0).Within(0.2), "on: unchanged");
            Assert.That(Level(2), Is.LessThan(0.003), "off at full depth: silence");
            Assert.That(Level(15), Is.LessThan(0.003));
            Assert.That(Measure.Db(Level(16)), Is.EqualTo(6.0).Within(0.2), "the row starts again after sixteen steps");
            // The depth is how far an off step closes.
            var half = GateSetup(); Choice(half, P.GateRate, "1/8"); Phys(half, P.GateDepth, 0.5);
            for (int i = 0; i < 16; i++) half.Set(P.Step1 + i, 0);
            var (hl, _) = Render(Engine(half), x, (float[])x.Clone(), Constant(120), 64);
            double hLevel = Measure.Rms(hl, (int)(2 * eighth + eighth * 0.4), (int)(eighth * 0.4)) / Measure.Rms(x, (int)(2 * eighth + eighth * 0.4), (int)(eighth * 0.4));
            Assert.That(hLevel, Is.EqualTo(0.5).Within(0.01), "depth 50 %: an off step is half level");
        }

        [Test]
        public void TheGatesEdgesTakeTheAttackAndTheReleaseAndNeverLessThanTwoMilliseconds()
        {
            double spb = Spb(120);
            var v = GateSetup();
            Choice(v, P.GateRate, "1/4");
            Phys(v, P.GateAttack, 2); Phys(v, P.GateRelease, 2);
            for (int i = 0; i < 16; i++) v.Set(P.Step1 + i, i % 2 == 0 ? 1 : 0);
            int length = (int)(spb * 4);
            var dc = new float[length]; Array.Fill(dc, 1f);
            var (ol, _) = Render(Engine(v), dc, (float[])dc.Clone(), Constant(120), 64);
            int step = (int)spb;
            // The off step starts at one beat: from 1 down to 0, over no less than 2 ms (96 samples).
            int a = Array.FindIndex(ol, step - 20, q => q < 0.999f);
            int b = Array.FindIndex(ol, a, q => q < 0.001f);
            Assert.That(a, Is.InRange(step - 1, step + 4), "it starts closing on the beat");
            Assert.That(b - a, Is.GreaterThanOrEqualTo(90).And.LessThanOrEqualTo(110), "over about 2 ms");
        }

        [Test]
        public void TheAutoPanSwingsTheBalanceOnItsShapeAtItsRate()
        {
            double spb = Spb(120);
            int length = (int)(spb * 8);
            var x = Sine(length, 220, 0.5);
            foreach (var shape in new[] { "Sine", "Triangle", "Square" })
            {
                var v = Fresh(s => { Switch(s, P.PanOn, true); Choice(s, P.PanSync, "1 bar"); Phys(s, P.PanDepth, 1.0); Choice(s, P.PanShape, shape); Phys(s, P.PanPhase, 0); });
                var (ol, or) = Render(Engine(v), x, (float[])x.Clone(), Constant(120), 64);
                // Phase 0 starts at the centre going right: after a quarter of the bar the left channel is shut and the right one is whole.
                int quarter = (int)(spb);
                double lq = Measure.Rms(ol, quarter - 600, 600) / Measure.Rms(x, quarter - 600, 600);
                double rq = Measure.Rms(or, quarter - 600, 600) / Measure.Rms(x, quarter - 600, 600);
                Assert.That(rq, Is.EqualTo(1.0).Within(0.02), $"{shape}: the right channel whole at a quarter");
                Assert.That(lq, Is.LessThan(shape == "Square" ? 0.05 : 0.2), $"{shape}: the left channel nearly shut at a quarter");
                double lc = Measure.Rms(ol, 2 * quarter - 200, 400) / Measure.Rms(x, 2 * quarter - 200, 400);
                double rc = Measure.Rms(or, 2 * quarter - 200, 400) / Measure.Rms(x, 2 * quarter - 200, 400);
                if (shape != "Square")
                {
                    Assert.That(lc, Is.EqualTo(1.0).Within(0.1), $"{shape}: the centre at half a bar");
                    Assert.That(rc, Is.EqualTo(1.0).Within(0.1));
                }
                // It comes round: one cycle later it is as it was.
                Assert.That(Measure.Rms(ol, quarter + 4 * quarter - 600, 600), Is.EqualTo(Measure.Rms(ol, quarter - 600, 600)).Within(0.01));
            }
            // The phase offset moves it by that much of a cycle.
            var v90 = Fresh(s => { Switch(s, P.PanOn, true); Choice(s, P.PanSync, "1 bar"); Phys(s, P.PanDepth, 1.0); Phys(s, P.PanPhase, 180); });
            var (zl, zr) = Render(Engine(v90), x, (float[])x.Clone(), Constant(120), 64);
            double zq = Measure.Rms(zr, (int)spb - 600, 600) / Measure.Rms(x, (int)spb - 600, 600);
            Assert.That(zq, Is.LessThan(0.2), "a half cycle later the right channel is the one that is shut");
        }

        // ================================================================================== the sound of the modes

        [Test]
        public void TheStutterRepeatsPitchesDecaysAndFiltersEachRepeat()
        {
            double spb = Spb(120);
            int length = (int)(spb * 6);
            // A sine (steady), so that the repeats show the pitch and the level.
            var x = Sine(length, 220, 0.4);
            var v = Fresh(s => { Switch(s, P.StutterOn, true); Choice(s, P.StutterEvery, "1 bar"); Choice(s, P.Slice, "1/4"); Choice(s, P.Repeats, "3"); Phys(s, P.StutterPitch, 12); Phys(s, P.StutterDecay, 6); Phys(s, P.StutterFilter, 0); });
            var (ol, _) = Render(Engine(v), x, (float[])x.Clone(), Constant(120), 64);
            int w = (int)(spb * 1);                 // the window of 3 beats starts at beat 1
            double Hz(int rep) => Measure.PeakFrequency(ol, (int)(spb * (1 + rep) + 3000), 16384, Rate);
            Assert.That(Hz(0), Is.EqualTo(220).Within(6), "repeat 0: unchanged");
            Assert.That(Hz(1), Is.EqualTo(440).Within(8), "repeat 1: an octave up");
            Assert.That(Hz(2), Is.EqualTo(880).Within(16), "repeat 2: two octaves up");
            double Rms(int rep) => Measure.Rms(ol, (int)(spb * (1 + rep) + 3000), 16384);
            Assert.That(Measure.Db(Rms(1) / Rms(0)), Is.EqualTo(-6.0).Within(0.6), "6 dB less each");
            Assert.That(Measure.Db(Rms(2) / Rms(1)), Is.EqualTo(-6.0).Within(0.6));
            // A low-pass that closes by an octave each repeat takes a 6 kHz tone down.
            var hi = Sine(length, 6000, 0.4);
            var vf = Fresh(s => { Switch(s, P.StutterOn, true); Choice(s, P.StutterEvery, "1 bar"); Choice(s, P.Slice, "1/4"); Choice(s, P.Repeats, "3"); Phys(s, P.StutterPitch, 0); Phys(s, P.StutterDecay, 0); Phys(s, P.StutterFilter, -2); });
            var (fl, _) = Render(Engine(vf), hi, (float[])hi.Clone(), Constant(120), 64);
            double F0 = Measure.Rms(fl, (int)(spb * 1 + 6000), 8000), F2 = Measure.Rms(fl, (int)(spb * 3 + 6000), 8000);
            Assert.That(Measure.Db(F2 / F0), Is.LessThan(-12.0), "the third repeat is filtered well below the first");
        }

        [Test]
        public void TheRepeatChanceDecidesWhichIntervalsRepeatAndTheSameWayEveryPlay()
        {
            double spb = Spb(120);
            int length = (int)(spb * 4 * 40);
            var x = Sine(length);
            var v = RepeatSetup();
            Phys(v, P.RepeatChance, 0.5);
            int Windows(ParamValues vv)
            {
                var e = Engine(vv, logSize: 4096);
                Render(e, x, (float[])x.Clone(), Constant(120), 256);
                return Enumerable.Range(0, e.LogCount).Count(i => e.Log[i].Kind == ChopEventKind.RepeatStart);
            }
            int a = Windows(v), b = Windows(v);
            Assert.That(a, Is.EqualTo(b), "the same way every play");
            Assert.That(a, Is.InRange(10, 30), "about half of the 40 bars");
            Phys(v, P.RepeatChance, 0.0);
            Assert.That(Windows(v), Is.EqualTo(0));
            Phys(v, P.RepeatChance, 1.0);
            Assert.That(Windows(v), Is.EqualTo(40));
        }

        [Test]
        public void TheRepeatVariationCutsSomeLoopsShorterAndKeepsTheirStartsOnTheGrid()
        {
            double spb = Spb(120);
            int length = (int)(spb * 4 * 12);
            var x = Ramp(length);
            var v = RepeatSetup();
            Choice(v, P.RepeatLength, "1 bar"); Choice(v, P.RepeatGrid, "1/4"); Phys(v, P.Variation, 0.8);
            var e = Engine(v, logSize: 4096);
            var plan = Constant(120);
            Render(e, x, (float[])x.Clone(), plan, 64);
            var evs = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).Where(ev => ev.Kind == ChopEventKind.RepeatStart || ev.Kind == ChopEventKind.RepeatRepeat).ToList();
            var lengths = new HashSet<double>();
            for (int i = 1; i < evs.Count; i++)
            {
                if (evs[i].Kind == ChopEventKind.RepeatStart) continue;
                lengths.Add(Math.Round(evs[i].Beat - evs[i - 1].Beat, 6));
                Assert.That(Math.Abs(evs[i].Sample - ExpectedSample(plan, evs[i].Beat)), Is.LessThanOrEqualTo(1));
            }
            Assert.That(lengths, Is.SupersetOf(new[] { 1.0, 0.5, 0.25 }), "some loops are whole, some half, some a quarter of the grid");
        }

        // ================================================================================== the audio thread

        [Test]
        public void NothingIsAllocatedPerBlockWithEveryModeBusy()
        {
            var v = AllOn();
            Choice(v, P.StutterTrig, "Both"); Choice(v, P.RepeatTrig, "Both"); Choice(v, P.RetrigTrig, "Both"); Choice(v, P.TapeTrig, "Both");
            Phys(v, P.StutterPitch, 3); Phys(v, P.StutterFilter, -0.5); Choice(v, P.PanShape, "Square");
            var e = Engine(v);
            var (dl, dr) = DrumLoop(Rate * 12);
            var l = new float[256]; var r = new float[256];
            double spb = Spb(120);
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                int pos = (b % 1500) * 256;
                Array.Copy(dl, pos, l, 0, 256);
                Array.Copy(dr, pos, r, 0, 256);
                e.SetTransport(true, (b * 256) / spb, spb);
                if (b % 37 == 3) e.QueueNote(17, true, 48 + b % 12);
                if (b % 37 == 20) e.QueueNote(100, false, 48 + (b - 17) % 12);
                // Parameters move under automation too.
                v.Set(P.GateAttack, 100 + b % 300);
                v.Set(P.StutterPitch, 100 + b % 40);
                e.Process(l, r, 256);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void NoDenormalsRingOutOfAnyStageAfterALoudBurst()
        {
            var v = AllOn();
            Phys(v, P.StutterFilter, -1); Choice(v, P.StutterTrig, "Both");
            var e = Engine(v);
            double spb = Spb(120);
            int total = Rate * 6;
            var x = new float[total];
            var rng = new Rng(5);
            for (int i = 0; i < Rate / 2; i++) x[i] = 0.8f * rng.NextBipolar();
            var (ol, or) = Render(e, x, (float[])x.Clone(), Constant(120), 256, new[] { (1000, true, 60), (20000, false, 60) });
            for (int i = 0; i < total; i++)
            {
                if (float.IsSubnormal(ol[i]) || float.IsSubnormal(or[i])) Assert.Fail($"a denormal at {i}");
                if (!float.IsFinite(ol[i]) || !float.IsFinite(or[i])) Assert.Fail($"not finite at {i}");
            }
            Assert.That(Measure.Peak(ol, Rate * 5, Rate), Is.LessThan(1e-9), "silent at the end");
        }

        [Test]
        public void TheSongsOwnTransportDoesNotMoveASetting([Values(16, 64, 256)] int block)
        {
            // The block size changes nothing: the same input and the same plan give the same output.
            var (l, r) = DrumLoop(Rate * 5);
            var v = StutterSetup();
            Switch(v, P.GateOn, true);
            var a = Render(Engine(v), l, r, Constant(120), 256);
            var b = Render(Engine(v), l, r, Constant(120), block);
            double worst = 0;
            for (int i = 0; i < l.Length; i++) worst = Math.Max(worst, Math.Abs(a.L[i] - b.L[i]));
            Assert.That(worst, Is.LessThan(1e-4), "the same sound at any block size (the smoothed gate depth may differ by a hair)");
        }

        // ================================================================================== state and presets

        [Test]
        public void EveryParameterAndTheStepRowSurviveASaveAndReopen()
        {
            var rng = new Rng(2026);
            var v = new ParamValues(Params.Table);
            for (int p = 0; p < Params.Table.Length; p++)
            {
                var d = Params.Table[p];
                v.Set(p, d.Min + (int)(rng.NextUnipolar() * (d.Max - d.Min + 1)) % (d.Max - d.Min + 1));
            }
            // Saved as the host saves: the raw values, in order, then read back into a new instance.
            var saved = Enumerable.Range(0, Params.Table.Length).Select(i => v[i]).ToArray();
            var back = new ParamValues(Params.Table);
            for (int i = 0; i < saved.Length; i++) back.Set(i, saved[i]);
            for (int i = 0; i < saved.Length; i++) Assert.That(back[i], Is.EqualTo(v[i]), Params.Table[i].Name);
            for (int s = 0; s < 16; s++) Assert.That(back[P.Step1 + s], Is.EqualTo(v[P.Step1 + s]), $"step {s + 1}");
            Assert.That(Params.Table.Length, Is.EqualTo(P.Count));
            // Each id is unique and each step is a three-way choice.
            Assert.That(Params.Table.Select(d => d.Id).Distinct().Count(), Is.EqualTo(Params.Table.Length));
            for (int s = 0; s < 16; s++) Assert.That(Params.Table[P.Step1 + s].Names, Is.EqualTo(new[] { "Off", "On", "Accent" }));
        }

        [Test]
        public void TheParametersAreInRealUnitsAndInTheirRanges()
        {
            var t = Params.Table;
            Assert.That(t[P.StutterPitch].Value(t[P.StutterPitch].Min), Is.EqualTo(-12));
            Assert.That(t[P.StutterPitch].Value(t[P.StutterPitch].Max), Is.EqualTo(12));
            Assert.That(t[P.StutterDecay].Unit, Is.EqualTo("dB"));
            Assert.That(t[P.StutterFilter].Unit, Is.EqualTo("oct"));
            Assert.That(t[P.Repeats].Names.Length, Is.EqualTo(17));
            Assert.That(t[P.Repeats].Names[16], Is.EqualTo("Hold"));
            Assert.That(t[P.Slice].Names, Is.EqualTo(new[] { "1/4", "1/8", "1/16", "1/32", "1/64" }));
            Assert.That(t[P.RepeatInterval].Names.Length, Is.EqualTo(8));
            Assert.That(t[P.RepeatLength].Names, Is.EqualTo(new[] { "1/16", "1/8", "1/4", "1/2", "1 bar", "2 bars", "4 bars" }));
            Assert.That(t[P.GateAttack].Unit, Is.EqualTo("ms"));
            Assert.That(t[P.Smoothness].Value(t[P.Smoothness].Min), Is.EqualTo(2.0).Within(1e-9));
            Assert.That(t[P.Smoothness].Value(t[P.Smoothness].Max), Is.EqualTo(20.0).Within(1e-9));
            Assert.That(t[P.Amount].Value(t[P.Amount].Max), Is.EqualTo(200.0));
            Assert.That(t[P.PanRate].Unit, Is.EqualTo("Hz"));
            Assert.That(t[P.StopTime].Unit, Is.EqualTo("ms"));
        }

        [Test]
        public void TheSmoothnessSetsTheLengthOfTheCrossfades()
        {
            // A step in a stutter's output (one sample of the slice start) is spread over the Smoothness: 2 ms and 20 ms.
            double spb = Spb(120);
            int length = (int)(spb * 4 * 2);
            var x = Sine(length);
            foreach (var (ms, expect) in new[] { (2.0, 96), (20.0, 960) })
            {
                var v = StutterSetup();
                Phys(v, P.Smoothness, ms);
                var e = Engine(v, logSize: 64);
                var (ol, _) = Render(e, x, (float[])x.Clone(), Constant(120), 64);
                var start = Enumerable.Range(0, e.LogCount).Select(i => e.Log[i]).First(ev => ev.Kind == ChopEventKind.StutterStart);
                int s = (int)start.Sample;
                int first = s - 1; while (first > s - 3000 && ol[first] != x[first]) first--;
                // The blend begins as many samples before the first slice sample as the Smoothness says.
                Assert.That(s - first, Is.EqualTo(expect).Within(4), $"{ms} ms: the blend lasts {expect} samples");
            }
        }

        // ================================================================================== the presets

        static List<(string Name, ParamValues V)> ChopPresets()
        {
            var list = new List<(string, ParamValues)>();
            foreach (var (file, machine, name, values) in PresetTests.AllPresets())
            {
                if (machine != "Neo Chop") continue;
                var v = new ParamValues(Params.Table);
                foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                list.Add((name, v));
            }
            return list;
        }

        static double DiffDb(float[] a, float[] b, int from)
        {
            double d = 0, s = 0;
            for (int i = from; i < a.Length; i++) { double e = a[i] - b[i]; d += e * e; s += (double)b[i] * b[i]; }
            return 10 * Math.Log10(Math.Max(d, 1e-20) / Math.Max(s, 1e-20));
        }

        [Test]
        public void EveryPresetIsClearlyAudibleDifferentFromTheOthersAndClickFreeOnFullScaleDrums()
        {
            var presets = ChopPresets();
            Assert.That(presets.Count, Is.EqualTo(8));
            double spb = Spb(120);
            int length = (int)(spb * 16);
            var (l, r) = DrumLoop(length, 120);
            var outs = new List<float[]>();
            foreach (var (name, v) in presets)
            {
                var (ol, _) = Render(Engine(v), l, r, Constant(120), 256);
                outs.Add(ol);
                double against = DiffDb(ol, l, (int)(spb * 4));
                var clicks = NewClicks(l, ol).Where(c => c >= 4 * spb).ToList();
                TestContext.Out.WriteLine($"{name}: differs from the dry by {against:0.0} dB (0 dB = as loud as the drums), {clicks.Count} clicks");
                Assert.That(against, Is.GreaterThan(-12.0), $"{name}: audible at first listen");
                Assert.That(clicks, Is.Empty, $"{name}: click detector");
            }
            double least = 99;
            for (int a = 0; a < outs.Count; a++)
                for (int b = a + 1; b < outs.Count; b++)
                {
                    double d = DiffDb(outs[a], outs[b], (int)(spb * 4));
                    least = Math.Min(least, d);
                    Assert.That(d, Is.GreaterThan(-12.0), $"{presets[a].Name} against {presets[b].Name}: clearly different");
                }
            TestContext.Out.WriteLine($"the two presets closest to each other differ by {least:0.0} dB");
        }
    }
}
