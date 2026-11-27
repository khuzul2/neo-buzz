using NeoBuzz.Dsp;
using NeoBuzz.Pulse;
using NeoBuzz.TestSupport;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Pulse (docs/specs/neo-pulse.md): headless unit tests for every gate in issue #203.</summary>
    [TestFixture]
    public class NeoPulseTests
    {
        const int Rate = 48000;

        static ParamValues DefaultValues(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            // Silence default lanes so unit tests configure only the lanes they test.
            for (int l = 0; l < PulseEngine.LaneCount; l++)
                v.Set(PulseEngine.LaneParam(l, PulseEngine.FPulses), 0);
            setup?.Invoke(v);
            return v;
        }

        static string MaskToBits(uint mask, int steps)
        {
            var chars = new char[steps];
            for (int i = 0; i < steps; i++)
                chars[i] = ((mask >> i) & 1u) != 0 ? '1' : '0';
            return new string(chars);
        }

        readonly record struct Rec(long Sample, PulseKind Kind, int Channel, int Note, int Vel);

        static List<Rec> RunBlocks(PulseEngine engine, double bpm, int blockSize, int totalSamples,
            double startBeat = 0.0, Func<int, double, (bool Playing, double? JumpBeat, double? NewBpm)> hook = null)
        {
            var log = new List<Rec>();
            double beat = startBeat;
            double curBpm = bpm;
            for (int pos = 0; pos < totalSamples; pos += blockSize)
            {
                int n = Math.Min(blockSize, totalSamples - pos);
                bool playing = true;
                if (hook != null)
                {
                    var (p, jb, nb) = hook(pos, beat);
                    playing = p;
                    if (jb.HasValue) beat = jb.Value;
                    if (nb.HasValue) curBpm = nb.Value;
                }
                double spb = Rate * 60.0 / curBpm;
                var block = new PulseBlock
                {
                    Samples = n,
                    Playing = playing,
                    Beat = beat,
                    SamplesPerBeat = spb,
                    BeatsPerBar = 4.0,
                };
                engine.Process(in block);
                for (int i = 0; i < engine.OutCount; i++)
                {
                    var e = engine.Out[i];
                    log.Add(new Rec(pos + e.Offset, e.Kind, e.Channel, e.Data1, e.Data2));
                }
                if (playing) beat += n / spb;
            }
            return log;
        }

        // ------------------------------------------------------------------ 1. Euclidean rhythms, rotation & accents

        [TestCase(3, 8, "10010010")]
        [TestCase(5, 8, "10110110")]
        [TestCase(7, 12, "101101011010")]
        [TestCase(5, 16, "1001001001001000")]
        [TestCase(9, 16, "1011010101101010")]
        [TestCase(13, 24, "101101010101011010101010")]
        public void EuclideanPublishedRhythmsMatchToussaint2005(int pulses, int steps, string expected)
        {
            uint mask = PulseEuclidean.Compute(pulses, steps);
            string actual = MaskToBits(mask, steps);
            Measure.Report($"Neo Pulse E({pulses},{steps}): {actual}");
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void RotationShiftsRhythmExactAndAccentsLandOnAccentRing()
        {
            // Verify every rotation 0..steps-1 shifts E(5,16) and E(7,12) cleanly.
            foreach (var (k, n) in new[] { (5, 16), (7, 12), (3, 8), (13, 24), (17, 32) })
            {
                uint baseMask = PulseEuclidean.Compute(k, n);
                for (int r = 0; r < n; r++)
                {
                    uint rotMask = PulseEuclidean.Rotate(baseMask, n, r);
                    for (int s = 0; s < n; s++)
                    {
                        bool got = ((rotMask >> s) & 1u) != 0;
                        bool want = ((baseMask >> ((s - r + n) % n)) & 1u) != 0;
                        Assert.That(got, Is.EqualTo(want), $"E({k},{n}) rot={r} step={s}");
                    }
                }
            }

            // Verify accents land where the second Euclidean ring says with exact AccentVelocity vs Velocity.
            var v = DefaultValues(p =>
            {
                p.Set(P.Rate, 4); // 1/16
                p.Set(P.L1Steps, 12);
                p.Set(P.L1Pulses, 7);
                p.Set(P.L1Rot, 2);
                p.Set(P.L1AccPulses, 3);
                p.Set(P.L1AccRot, 1);
                p.Set(P.L1Note, 56);
                p.Set(P.L1Vel, 70);
                p.Set(P.L1AccVel, 125);
                p.Set(P.L1Gate, 50);
            });
            var eng = new PulseEngine(v);
            eng.SetSampleRate(Rate);

            // 12 sixteenth-steps = 3 beats = 1.5 s at 120 BPM.
            var events = RunBlocks(eng, 120.0, 64, (int)(1.5 * Rate));
            var ons = events.Where(e => e.Kind == PulseKind.NoteOn).ToList();
            uint hitMask = PulseEuclidean.Rhythm(7, 12, 2);
            uint accMask = PulseEuclidean.Rhythm(3, 12, 1);

            int expectedHits = 0;
            for (int s = 0; s < 12; s++) if (((hitMask >> s) & 1u) != 0) expectedHits++;
            Assert.That(ons.Count, Is.EqualTo(expectedHits), "exact hit count over one 12-step cycle");

            double samplesPerStep = Rate * 60.0 / 120.0 * 0.25; // 6000 samples
            int idx = 0;
            for (int s = 0; s < 12; s++)
            {
                if (((hitMask >> s) & 1u) == 0) continue;
                var ev = ons[idx++];
                bool wantAcc = ((accMask >> s) & 1u) != 0;
                Assert.That(ev.Sample, Is.EqualTo((long)Math.Round(s * samplesPerStep)).Within(1));
                Assert.That(ev.Vel, Is.EqualTo(wantAcc ? 125 : 70), $"step {s} accent={wantAcc}");
            }
        }

        // ------------------------------------------------------------------ 2. Chance, fill & deterministic seed

        [TestCase(25)]
        [TestCase(50)]
        [TestCase(75)]
        public void ChanceOverTenThousandBarsWithinOnePercentAndSeedRepeatable(int chancePct)
        {
            // Lane 1: E(8,16) = 8 hits/bar -> 80,000 opportunities over 10,000 bars.
            var v = DefaultValues(p =>
            {
                p.Set(P.Rate, 4); // 1/16
                p.Set(P.Seed, 4242);
                p.Set(P.L1Steps, 16);
                p.Set(P.L1Pulses, 8);
                p.Set(P.L1Chance, chancePct);
                p.Set(P.L1Note, 36);
                p.Set(P.L1Gate, 50);
            });

            // Simulate 10,000 bars using 1-beat blocks (spb = 24000, 4 blocks/bar = 40,000 blocks).
            int RunCount(ParamValues pv, out List<(long BeatStep, int Vel)> first200)
            {
                var e = new PulseEngine(pv);
                e.SetSampleRate(Rate);
                first200 = new List<(long, int)>();
                int hits = 0;
                const int spb = 24000; // 120 BPM
                for (long b = 0; b < 40000; b++)
                {
                    var blk = new PulseBlock { Samples = spb, Playing = true, Beat = b, SamplesPerBeat = spb, BeatsPerBar = 4.0 };
                    e.Process(in blk);
                    for (int i = 0; i < e.OutCount; i++)
                    {
                        if (e.Out[i].Kind == PulseKind.NoteOn)
                        {
                            hits++;
                            if (first200.Count < 200) first200.Add((b * spb + e.Out[i].Offset, e.Out[i].Data2));
                        }
                    }
                }
                return hits;
            }

            int hitsA = RunCount(v, out var seqA);
            int hitsB = RunCount(v, out var seqB);
            double ratePct = 100.0 * hitsA / 80000.0;
            Measure.Report($"Neo Pulse Chance={chancePct}% over 10,000 bars: {ratePct:F3}% ({hitsA}/80000), repeatable={hitsA == hitsB}");

            Assert.That(ratePct, Is.EqualTo(chancePct).Within(1.0), "observed hit rate within 1 % of Chance");
            Assert.That(hitsB, Is.EqualTo(hitsA), "same Seed produces identical hit count");
            Assert.That(seqB, Is.EqualTo(seqA), "same Seed produces identical event timing & velocity");
        }

        [Test]
        public void FillOverTenThousandBarsWithinOnePercentMutedSilentAndFreeSeedVaries()
        {
            // Lane 1: E(0,16) unmuted (16 empty steps/bar -> 160,000 opportunities over 10,000 bars), Fill = 30 %.
            // Lane 2: E(0,16) muted -> must emit 0 hits even with Fill = 30 %.
            var v = DefaultValues(p =>
            {
                p.Set(P.Rate, 4);
                p.Set(P.Fill, 30);
                p.Set(P.Seed, 777);
                p.Set(P.L1Steps, 16);
                p.Set(P.L1Pulses, 0);
                p.Set(P.L1Note, 38);
                p.Set(P.L2Steps, 16);
                p.Set(P.L2Pulses, 0);
                p.Set(P.L2Note, 99);
                p.Set(P.L2Mute, 1);
            });

            var e = new PulseEngine(v);
            e.SetSampleRate(Rate);
            const int spb = 24000;
            int l1Hits = 0, l2Hits = 0;
            for (long b = 0; b < 40000; b++)
            {
                var blk = new PulseBlock { Samples = spb, Playing = true, Beat = b, SamplesPerBeat = spb, BeatsPerBar = 4.0 };
                e.Process(in blk);
                for (int i = 0; i < e.OutCount; i++)
                {
                    if (e.Out[i].Kind != PulseKind.NoteOn) continue;
                    if (e.Out[i].Data1 == 38) l1Hits++;
                    else if (e.Out[i].Data1 == 99) l2Hits++;
                }
            }
            double fillPct = 100.0 * l1Hits / 160000.0;
            Measure.Report($"Neo Pulse Fill=30% over 10,000 bars: {fillPct:F3}% ({l1Hits}/160000), muted lane hits={l2Hits}");
            Assert.That(fillPct, Is.EqualTo(30.0).Within(1.0), "Fill hit rate within 1 % over 10,000 bars");
            Assert.That(l2Hits, Is.EqualTo(0), "muted lane emits zero fill hits");

            // Seed = 0 (Free) varies across runs.
            var vf = DefaultValues(p =>
            {
                p.Set(P.Rate, 4);
                p.Set(P.Seed, 0);
                p.Set(P.L1Steps, 16);
                p.Set(P.L1Pulses, 16);
                p.Set(P.L1Chance, 50);
                p.Set(P.L1Note, 36);
            });
            var e1 = new PulseEngine(vf); e1.SetSampleRate(Rate);
            var e2 = new PulseEngine(vf); e2.SetSampleRate(Rate);
            var r1 = RunBlocks(e1, 120.0, 256, Rate * 4).Where(x => x.Kind == PulseKind.NoteOn).Select(x => x.Sample).ToList();
            var r2 = RunBlocks(e2, 120.0, 256, Rate * 4).Where(x => x.Kind == PulseKind.NoteOn).Select(x => x.Sample).ToList();
            Assert.That(r2, Is.Not.EqualTo(r1), "Seed = 0 (Free) produces different hits across instances");
        }

        // ------------------------------------------------------------------ 3. Timing across tempos, block sizes, swing, ratchets, dividers, tempo change & loop jump

        [TestCase(60.0, 16)]
        [TestCase(60.0, 64)]
        [TestCase(60.0, 256)]
        [TestCase(120.0, 16)]
        [TestCase(120.0, 64)]
        [TestCase(120.0, 256)]
        [TestCase(197.0, 16)]
        [TestCase(197.0, 64)]
        [TestCase(197.0, 256)]
        public void TimingStaysWithinOneSampleAcrossTemposAndBlockSizes(double bpm, int blockSize)
        {
            // E(5,16) at 1/16, Gate 60 % -> check every NoteOn and NoteOff within <= 1 sample of exact grid time.
            var v = DefaultValues(p =>
            {
                p.Set(P.Rate, 4); // 1/16
                p.Set(P.L1Steps, 16);
                p.Set(P.L1Pulses, 5);
                p.Set(P.L1Note, 36);
                p.Set(P.L1Gate, 60);
            });
            var e = new PulseEngine(v);
            e.SetSampleRate(Rate);

            double spb = Rate * 60.0 / bpm;
            int totalSamples = (int)Math.Ceiling(8.0 * spb); // 2 bars
            var evs = RunBlocks(e, bpm, blockSize, totalSamples);
            uint mask = PulseEuclidean.Compute(5, 16);
            double stepBeats = 0.25;

            var ons = evs.Where(x => x.Kind == PulseKind.NoteOn).ToList();
            var offs = evs.Where(x => x.Kind == PulseKind.NoteOff).ToList();
            int hitIdx = 0;
            double maxErr = 0;
            for (int s = 0; s < 32; s++)
            {
                if (((mask >> (s % 16)) & 1u) == 0) continue;
                double expectedOn = s * stepBeats * spb;
                double expectedOff = (s * stepBeats + stepBeats * 0.60) * spb;
                if (expectedOn >= totalSamples - 0.5) break;

                double onErr = Math.Abs(ons[hitIdx].Sample - expectedOn);
                maxErr = Math.Max(maxErr, onErr);
                Assert.That(onErr, Is.LessThanOrEqualTo(1.0), $"BPM={bpm} blk={blockSize} step={s} NoteOn err={onErr:F3}");

                if (expectedOff < totalSamples - 0.5 && hitIdx < offs.Count)
                {
                    double offErr = Math.Abs(offs[hitIdx].Sample - expectedOff);
                    maxErr = Math.Max(maxErr, offErr);
                    Assert.That(offErr, Is.LessThanOrEqualTo(1.0), $"BPM={bpm} blk={blockSize} step={s} NoteOff err={offErr:F3}");
                }
                hitIdx++;
            }
            Assert.That(hitIdx, Is.GreaterThanOrEqualTo(9));
            Measure.Report($"Neo Pulse timing BPM={bpm} block={blockSize}: max error {maxErr:F3} samples");
        }

        [Test]
        public void SwingRatchetsDividersTempoChangeAndLoopJumpStayOnGrid()
        {
            // 1. Swung odd steps with Gate = 100 %: odd step slot is (1 - 0.50) * step, NoteOff before same-sample NoteOn.
            var vSwing = DefaultValues(p =>
            {
                p.Set(P.Rate, 4); // 1/16 = 0.25 beats
                p.Set(P.Swing, 50); // odd step delayed by +0.125 beats, odd slot = 0.125 beats
                p.Set(P.L1Steps, 4);
                p.Set(P.L1Pulses, 4);
                p.Set(P.L1Note, 42);
                p.Set(P.L1Gate, 100);
            });
            var eSwing = new PulseEngine(vSwing);
            eSwing.SetSampleRate(Rate);
            double spb120 = Rate * 0.5; // 24000
            var swingEvs = RunBlocks(eSwing, 120.0, 64, (int)(2.0 * spb120));
            var swingOns = swingEvs.Where(x => x.Kind == PulseKind.NoteOn).ToList();
            for (int s = 0; s < 8; s++)
            {
                double expected = PulseEngine.StepBeat(s, 0.25, 0.50) * spb120;
                Assert.That(Math.Abs(swingOns[s].Sample - expected), Is.LessThanOrEqualTo(1.0), $"swing step {s}");
            }
            // Verify NoteOff strictly precedes same-sample NoteOn at every step boundary.
            int active = 0;
            foreach (var ev in swingEvs)
            {
                if (ev.Kind == PulseKind.NoteOn) { Assert.That(active, Is.EqualTo(0), "NoteOff must precede NoteOn at Gate=100%"); active++; }
                else if (ev.Kind == PulseKind.NoteOff) { active--; Assert.That(active, Is.EqualTo(0)); }
            }

            // 2. Ratchet 4x + Divider 2x at 197 BPM in 64-sample blocks.
            var vRat = DefaultValues(p =>
            {
                p.Set(P.Rate, 4); // 1/16 = 0.25 beats
                p.Set(P.L1Steps, 4);
                p.Set(P.L1Pulses, 1); // fires every 4 divided steps = every 2 beats
                p.Set(P.L1Div, 1);    // 2x -> lane step = 0.5 beats
                p.Set(P.L1Ratchet, 4);
                p.Set(P.L1RatchetCh, 100);
                p.Set(P.L1Gate, 50);
                p.Set(P.L1Note, 38);
            });
            var eRat = new PulseEngine(vRat);
            eRat.SetSampleRate(Rate);
            double spb197 = Rate * 60.0 / 197.0;
            var ratEvs = RunBlocks(eRat, 197.0, 64, (int)(2.0 * spb197));
            var ratOns = ratEvs.Where(x => x.Kind == PulseKind.NoteOn).ToList();
            Assert.That(ratOns.Count, Is.EqualTo(4), "4x ratchet emits 4 sub-hits inside one 2x-divided step");
            for (int k = 0; k < 4; k++)
            {
                double expected = (k * (0.5 / 4.0)) * spb197;
                Assert.That(Math.Abs(ratOns[k].Sample - expected), Is.LessThanOrEqualTo(1.0), $"ratchet sub-hit {k}");
            }

            // 3. Tempo change mid-song (120 BPM -> 173 BPM at beat 2.0) and loop jump (beat 4.0 -> 0.0).
            var vJump = DefaultValues(p =>
            {
                p.Set(P.Rate, 3); // 1/8 = 0.5 beats
                p.Set(P.L1Steps, 4);
                p.Set(P.L1Pulses, 4);
                p.Set(P.L1Note, 60);
                p.Set(P.L1Gate, 50);
            });
            var eJump = new PulseEngine(vJump);
            eJump.SetSampleRate(Rate);
            bool didTempo = false, didLoop = false;
            var beatAtOn = new List<double>();
            double beat = 0.0, curBpm = 120.0;
            for (int blkIdx = 0; blkIdx < 1600; blkIdx++)
            {
                if (!didTempo && beat >= 2.0) { curBpm = 173.0; didTempo = true; }
                if (!didLoop && beat >= 4.0) { beat = 0.0; didLoop = true; }
                double spb = Rate * 60.0 / curBpm;
                var blk = new PulseBlock { Samples = 64, Playing = true, Beat = beat, SamplesPerBeat = spb, BeatsPerBar = 4.0 };
                eJump.Process(in blk);
                for (int i = 0; i < eJump.OutCount; i++)
                {
                    if (eJump.Out[i].Kind == PulseKind.NoteOn)
                    {
                        double evBeat = beat + eJump.Out[i].Offset / spb;
                        beatAtOn.Add(evBeat);
                        double gridErrBeats = Math.Abs(evBeat / 0.5 - Math.Round(evBeat / 0.5)) * 0.5;
                        Assert.That(gridErrBeats * spb, Is.LessThanOrEqualTo(1.0), $"tempo/loop event at beat {evBeat:F4}");
                    }
                }
                beat += 64.0 / spb;
            }
            Assert.That(didTempo && didLoop, Is.True);
            Assert.That(beatAtOn.Count, Is.GreaterThanOrEqualTo(10));
        }

        // ------------------------------------------------------------------ 4. 10,000-event random stress: zero stuck notes & nothing while stopped

        [Test]
        public void TenThousandEventRandomStressLeavesZeroStuckNotesAndNothingWhileStopped()
        {
            var v = DefaultValues(p =>
            {
                p.Set(P.Rate, 5); // 1/32 for dense events
                p.Set(P.Swing, 35);
                p.Set(P.Fill, 20);
                p.Set(P.HumTime, 12);
                p.Set(P.HumVel, 20);
                p.Set(P.Seed, 999);
                for (int l = 0; l < PulseEngine.LaneCount; l++)
                {
                    int b = PulseEngine.LaneBase + l * PulseEngine.LaneStride;
                    p.Set(b + PulseEngine.FSteps, 8 + l);
                    p.Set(b + PulseEngine.FPulses, 4 + (l % 4));
                    p.Set(b + PulseEngine.FNote, 36 + (l % 3)); // intentional cross-lane shared notes!
                    p.Set(b + PulseEngine.FCh, 1 + (l % 2));    // intentional cross-lane shared channels!
                    p.Set(b + PulseEngine.FGate, 95);           // high gate + humanize overlap!
                    p.Set(b + PulseEngine.FRatchet, 1 + (l % 4));
                    p.Set(b + PulseEngine.FRatchetCh, 60);
                }
            });

            var e = new PulseEngine(v);
            e.SetSampleRate(Rate);

            var rng = new Random(20310000);
            var open = new bool[16 * 128];
            int openCount = 0;
            int totalOns = 0, totalOffs = 0;
            bool playing = true;
            double beat = 0.0;
            double bpm = 135.0;

            for (int iter = 0; iter < 10000; iter++)
            {
                // Apply random mutations (mutes, rhythm edits, note/channel edits, stop/start, loop jumps, panics).
                int action = rng.Next(20);
                int lane = rng.Next(PulseEngine.LaneCount);
                int b = PulseEngine.LaneBase + lane * PulseEngine.LaneStride;
                switch (action)
                {
                    case 0:
                    case 1: v.Set(b + PulseEngine.FMute, rng.Next(4) == 0 ? 1 : 0); break;
                    case 2:
                    case 3: v.Set(b + PulseEngine.FSteps, rng.Next(2, 25)); break;
                    case 4:
                    case 5: v.Set(b + PulseEngine.FPulses, rng.Next(1, 20)); break;
                    case 6:
                    case 7: v.Set(b + PulseEngine.FRot, rng.Next(0, 16)); break;
                    case 8:
                    case 9: v.Set(b + PulseEngine.FNote, 36 + rng.Next(4)); break;
                    case 10:
                    case 11: v.Set(b + PulseEngine.FCh, 1 + rng.Next(2)); break;
                    case 12:
                    case 13: v.Set(b + PulseEngine.FDiv, rng.Next(3)); break;
                    case 14: v.Set(P.Rate, rng.Next(4, 7)); break; // 1/16, 1/32, 1/64
                    case 15: playing = !playing; break;
                    case 16: beat = rng.NextDouble() * 16.0; break; // loop / seek jump
                    case 17: e.RequestPanic(); break;
                    case 18:
                    case 19: bpm = 80.0 + rng.NextDouble() * 120.0; break;
                }

                for (int sub = 0; sub < 6; sub++)
                {
                    int blockSize = ((iter + sub) % 3) switch { 0 => 64, 1 => 128, _ => 256 };
                    double spb = Rate * 60.0 / bpm;
                    bool wasStoppedBefore = !playing && openCount == 0;
                    var blk = new PulseBlock { Samples = blockSize, Playing = playing, Beat = beat, SamplesPerBeat = spb, BeatsPerBar = 4.0 };
                    e.Process(in blk);

                    if (wasStoppedBefore)
                        Assert.That(e.OutCount, Is.EqualTo(0), $"iter {iter}/{sub}: nothing may be sent while already stopped");

                    for (int i = 0; i < e.OutCount; i++)
                    {
                        var ev = e.Out[i];
                        int key = ((int)ev.Channel << 7) | ev.Data1;
                        if (ev.Kind == PulseKind.NoteOn)
                        {
                            Assert.That(playing, Is.True, $"iter {iter}/{sub}: NoteOn while stopped");
                            Assert.That(open[key], Is.False, $"iter {iter}/{sub}: double NoteOn without NoteOff on ch={ev.Channel} note={ev.Data1}");
                            open[key] = true;
                            openCount++;
                            totalOns++;
                        }
                        else if (ev.Kind == PulseKind.NoteOff)
                        {
                            Assert.That(open[key], Is.True, $"iter {iter}/{sub}: unmatched NoteOff on ch={ev.Channel} note={ev.Data1}");
                            open[key] = false;
                            openCount--;
                            totalOffs++;
                        }
                    }

                    if (!playing)
                        Assert.That(openCount, Is.EqualTo(0), $"iter {iter}/{sub}: every note must be released on transport stop");

                    if (playing) beat += blockSize / spb;
                }
            }

            // Final stop must release all remaining notes; subsequent stopped blocks must emit nothing.
            var stopBlk = new PulseBlock { Samples = 64, Playing = false, Beat = beat, SamplesPerBeat = 24000, BeatsPerBar = 4.0 };
            e.Process(in stopBlk);
            for (int i = 0; i < e.OutCount; i++)
            {
                var ev = e.Out[i];
                Assert.That(ev.Kind, Is.EqualTo(PulseKind.NoteOff));
                int key = ((int)ev.Channel << 7) | ev.Data1;
                Assert.That(open[key], Is.True);
                open[key] = false;
                openCount--;
                totalOffs++;
            }
            Assert.That(openCount, Is.EqualTo(0), "zero stuck notes after final stop");
            Assert.That(totalOffs, Is.EqualTo(totalOns), "every NoteOn matched by exactly one NoteOff");
            Assert.That(totalOns, Is.GreaterThan(5000), "stress test exercised substantial note traffic (>= 10,000 NoteOn+NoteOff events)");

            // Extra stopped blocks must produce 0 events.
            for (int k = 0; k < 20; k++)
            {
                e.Process(in stopBlk);
                Assert.That(e.OutCount, Is.EqualTo(0), "stopped block emits nothing");
            }
            Measure.Report($"Neo Pulse 10,000-event stress: {totalOns} NoteOns matched by {totalOffs} NoteOffs ({totalOns + totalOffs} total events), 0 stuck, 0 while stopped");
        }

        // ------------------------------------------------------------------ 5. Audio thread: 0 bytes allocated over 2,000 blocks

        [Test]
        public void AudioThreadAllocatesZeroBytesOverTwoThousandBlocks()
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Rate, 5);
            v.Set(P.Swing, 25);
            v.Set(P.Fill, 25);
            v.Set(P.HumTime, 10);
            v.Set(P.HumVel, 15);
            for (int l = 0; l < PulseEngine.LaneCount; l++)
            {
                int b = PulseEngine.LaneBase + l * PulseEngine.LaneStride;
                v.Set(b + PulseEngine.FPulses, 5 + (l % 4));
                v.Set(b + PulseEngine.FRatchet, 1 + (l % 4));
            }
            var e = new PulseEngine(v);
            e.SetSampleRate(Rate);

            double beat = 0;
            const double spb = 24000;
            // Warm up once before measuring allocations.
            for (int i = 0; i < 16; i++)
            {
                var blk = new PulseBlock { Samples = 64, Playing = true, Beat = beat, SamplesPerBeat = spb, BeatsPerBar = 4.0 };
                e.Process(in blk);
                beat += 64.0 / spb;
            }

            var (bytes, why) = AllocationCheck.Run(2000, i =>
            {
                v.Set(P.Swing, (i * 3) % 75);
                v.Set(P.L1Rot, i % 16);
                var blk = new PulseBlock { Samples = 64, Playing = true, Beat = beat, SamplesPerBeat = spb, BeatsPerBar = 4.0 };
                e.Process(in blk);
                beat += 64.0 / spb;
            });
            Measure.Report($"Neo Pulse audio-thread allocation (2000 blocks, 8 lanes active): {bytes} bytes ({why})");
            Assert.That(bytes, Is.EqualTo(0), $"zero bytes allocated per block ({why})");
        }

        // ------------------------------------------------------------------ 6. State round-trip

        [Test]
        public void StateMigrationsRoundTrip()
        {
            var chain = new StateMigrations(Params.Table);
            Assert.That(chain.Validate(), Is.Empty);
            Assert.That(chain.Current, Is.EqualTo(0));
            Assert.That(chain.Keys.Count, Is.EqualTo(Params.Table.Length));

            var saved = new int[Params.Table.Length];
            var keyed = new Dictionary<string, int>();
            for (int i = 0; i < Params.Table.Length; i++)
            {
                int val = Params.Table[i].Min + (Params.Table[i].Max - Params.Table[i].Min) / 2;
                saved[i] = val;
                keyed[Params.Table[i].Id] = val;
            }
            Assert.That(chain.MigrateGlobals(0, saved), Is.EqualTo(saved));
            var round = chain.Migrate(0, keyed);
            foreach (var kv in keyed)
                Assert.That(round[kv.Key], Is.EqualTo(kv.Value));
        }
    }
}
