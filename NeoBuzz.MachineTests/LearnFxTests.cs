using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using ReBuzzTests.Automation;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Learn (docs/specs/neo-learn.md §9, L2b): the shared engine's chorus, grit, room and tone stages (Machines/DyeEssence/Engine/LearnFx.cs): out of the path at 0,
    /// finite after any input, no denormals in the tails, no allocation, and the settings an amount stands for.
    /// </summary>
    [TestFixture]
    public class LearnFxTests
    {
        const int Rate = 48000, Block = 256;

        public sealed class Fx
        {
            public string Name;
            public Action<float[], float[], int> Process;
            public Action<float> Amount;
            public Action Reset;
        }

        static IEnumerable<TestCaseData> Stages()
        {
            var chorus = new ChorusFx(Rate); var grit = new GritFx(Rate); var room = new RoomFx(Rate); var tilt = new TiltFx(Rate);
            yield return new TestCaseData(new Fx { Name = "Chorus", Process = chorus.Process, Amount = a => chorus.Amount = a, Reset = chorus.Reset }).SetName("Chorus");
            yield return new TestCaseData(new Fx { Name = "Grit", Process = grit.Process, Amount = a => grit.Amount = a, Reset = grit.Reset }).SetName("Grit");
            yield return new TestCaseData(new Fx { Name = "Room", Process = room.Process, Amount = a => room.Amount = a, Reset = room.Reset }).SetName("Room");
            yield return new TestCaseData(new Fx { Name = "Tone", Process = tilt.Process, Amount = a => tilt.Tone = a, Reset = tilt.Reset }).SetName("Tone");
        }

        static void Sine(float[] l, float[] r, int at, double hz, double amp)
        {
            for (int i = 0; i < l.Length; i++) l[i] = r[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * (at + i) / Rate));
        }

        [TestCaseSource(nameof(Stages))]
        public void AtZeroTheStageIsOutOfThePath(Fx fx)
        {
            fx.Amount(0);
            var l = new float[Block]; var r = new float[Block];
            for (int b = 0; b < 40; b++)
            {
                Sine(l, r, b * Block, 440, 0.5);
                var l0 = (float[])l.Clone(); var r0 = (float[])r.Clone();
                fx.Process(l, r, Block);
                Assert.That(l, Is.EqualTo(l0).Within(1e-6f), fx.Name);
                Assert.That(r, Is.EqualTo(r0).Within(1e-6f), fx.Name);
            }
        }

        [TestCaseSource(nameof(Stages))]
        public void AnyInputLeavesTheOutputFiniteAndTheStageRecovers(Fx fx)
        {
            fx.Amount(fx.Name == "Tone" ? 1f : 0.8f);
            var l = new float[Block]; var r = new float[Block];
            var bad = new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.NaN, float.NegativeInfinity };
            for (int b = 0; b < 5; b++)
            {
                for (int i = 0; i < Block; i++) l[i] = r[i] = bad[(i + b) % bad.Length];
                fx.Process(l, r, Block);
            }
            for (int b = 0; b < 400; b++)
            {
                Sine(l, r, b * Block, 330, 0.4);
                fx.Process(l, r, Block);
                for (int i = 0; i < Block; i++)
                    if (!float.IsFinite(l[i]) || !float.IsFinite(r[i]) || Math.Abs(l[i]) > 4 || Math.Abs(r[i]) > 4)
                        Assert.Fail($"{fx.Name}: block {b} sample {i}: {l[i]}, {r[i]} after non-finite input");
            }
        }

        [TestCaseSource(nameof(Stages))]
        public void TailsLeaveNoDenormals(Fx fx)
        {
            fx.Reset();
            fx.Amount(1f);
            var l = new float[Block]; var r = new float[Block];
            for (int b = 0; b < 60; b++)
            {
                Sine(l, r, b * Block, 220, 0.6);
                fx.Process(l, r, Block);
            }
            int subnormal = 0;
            for (int b = 0; b < 1500; b++)
            {
                Array.Clear(l); Array.Clear(r);
                fx.Process(l, r, Block);
                for (int i = 0; i < Block; i++)
                    if (l[i] != 0 && Math.Abs(l[i]) < 1.17549435e-38f || r[i] != 0 && Math.Abs(r[i]) < 1.17549435e-38f) subnormal++;
            }
            Assert.That(subnormal, Is.EqualTo(0), fx.Name);
        }

        [TestCaseSource(nameof(Stages))]
        public void TheStageAllocatesNothing(Fx fx)
        {
            fx.Amount(0.7f);
            var l = new float[Block]; var r = new float[Block];
            for (int b = 0; b < 200; b++) { Sine(l, r, b * Block, 300, 0.5); fx.Process(l, r, Block); }
            var (bytes, why) = AllocationCheck.Run(1500, b =>
            {
                if (b % 100 == 0) fx.Amount((b / 100 % 5) / 4f);
                Sine(l, r, b * Block, 300, 0.5);
                fx.Process(l, r, Block);
            });
            Assert.That(bytes, Is.EqualTo(0), fx.Name + ": " + why);
        }

        [Test]
        public void GritAddsQuantisationNoiseAndFoldsAHighToneDownByTheLowerRate()
        {
            // The grit's error (everything but the tone) and an alias: a 15 kHz tone against 55 % of 48 kHz (Nyquist 13.2 kHz) lands at 26.4 - 15 = 11.4 kHz.
            double Measure2(float amount, double hz, double aliasHz, out double err)
            {
                var grit = new GritFx(Rate) { Amount = amount };
                var l = new float[Block]; var r = new float[Block];
                var y = new float[Measure.N + 8 * Block];
                for (int b = 0; b * Block < y.Length; b++)
                {
                    Sine(l, r, b * Block, hz, 0.5);
                    grit.Process(l, r, Block);
                    Array.Copy(l, 0, y, b * Block, Block);
                }
                int at = 8 * Block;
                var mag = Measure.Spectrum(y, at);
                double tone = 0, rest = 0;
                for (int k = 1; k < mag.Length; k++)
                {
                    double f = Measure.BinFrequency(k);
                    if (Math.Abs(f - hz) < 2 * Measure.BinFrequency(1)) tone += mag[k] * mag[k]; else rest += mag[k] * mag[k];
                }
                err = 10 * Math.Log10(Math.Max(1e-30, rest / tone));
                double alias = 0;
                for (int k = 1; k < mag.Length; k++) if (Math.Abs(Measure.BinFrequency(k) - aliasHz) < 60) alias += mag[k] * mag[k];
                return 10 * Math.Log10(Math.Max(1e-30, alias / tone));
            }
            double Tone = 1365.0 * Rate / Measure.N;   // on a bin: no leakage
            double a0 = Measure2(0f, Tone, 11400, out double e0), a1 = Measure2(1f, Tone, 11400, out double e1);
            Measure2(0.4f, Tone, 11400, out double eMid);
            double hi0 = Measure2(0f, 15000, 11400, out _), hi1 = Measure2(1f, 15000, 11400, out _);
            GritFx.Settings(1f, Rate, out float bits, out double rateHz, out float wobble);
            Measure.Report($"Grit: {bits} bits, {rateHz:F0} Hz of {Rate}, wobble {wobble} at 100 %; error against a 997 Hz tone: 0 % {e0:F1} dB, 40 % {eMid:F1} dB, 100 % {e1:F1} dB; a 15 kHz tone's alias at 11.4 kHz: {hi0:F1} dB clean, {hi1:F1} dB");
            Assert.That(bits, Is.EqualTo(12f));
            Assert.That(rateHz / Rate, Is.EqualTo(0.55).Within(1e-9));
            Assert.That(eMid, Is.GreaterThan(e0 + 60), "the wow and the quantisation are there");
            Assert.That(e1, Is.GreaterThan(eMid), "and grow with the amount");
            Assert.That(hi1, Is.GreaterThan(hi0 + 25));
        }

        [Test]
        public void GritGoesFromCleanToTwelveBitsContinuously()
        {
            double prevBits = 99, prevRate = 2;
            for (int i = 0; i <= 100; i++)
            {
                GritFx.Settings(i / 100f, Rate, out float bits, out double rateHz, out float wobble);
                Assert.That(bits, Is.LessThanOrEqualTo(prevBits));
                Assert.That(rateHz / Rate, Is.LessThanOrEqualTo(prevRate + 1e-12));
                prevBits = bits; prevRate = rateHz / Rate;
            }
            GritFx.Settings(0, Rate, out float b0, out double r0, out float w0);
            Assert.That((b0, r0 / Rate, w0), Is.EqualTo((16f, 1.0, 0f)));
        }

        [Test]
        public void TheRoomsReflectionsAreNeoVerbsRoomPatternWithinThreeAndFortyMilliseconds()
        {
            var tl = new float[RoomFx.Taps]; var tr = new float[RoomFx.Taps]; var gl = new float[RoomFx.Taps]; var gr = new float[RoomFx.Taps];
            RoomFx.Pattern(1.0, Rate, tl, tr, gl, gr);
            var tl2 = new float[RoomFx.Taps]; var tr2 = new float[RoomFx.Taps]; var gl2 = new float[RoomFx.Taps]; var gr2 = new float[RoomFx.Taps];
            RoomFx.Pattern(1.0, Rate, tl2, tr2, gl2, gr2);
            Assert.That(tl2, Is.EqualTo(tl)); Assert.That(gr2, Is.EqualTo(gr));
            Assert.That(RoomFx.Seed, Is.EqualTo(0xBEEF00u + 1), "Neo Verb seeds its Room mode (1) with 0xBEEF00 + mode");
            for (int k = 0; k < RoomFx.Taps; k++)
            {
                Assert.That(tl[k] / Rate * 1000, Is.InRange(RoomFx.ErMinMs, RoomFx.ErMaxMs));
                Assert.That(tr[k] / Rate * 1000, Is.InRange(RoomFx.ErMinMs, RoomFx.ErMaxMs));
                Assert.That(Math.Abs(gl[k]), Is.LessThan(2));
            }
            var tl3 = new float[RoomFx.Taps];
            RoomFx.Pattern(2.0, Rate, tl3, tr2, gl2, gr2);
            Assert.That(tl3[5], Is.EqualTo(tl[5] * 2).Within(1e-3), "the size scales the times");
        }

        [Test]
        public void TheRoomHoldsATailAfterANoiseBurstAndItDecays()
        {
            var room = new RoomFx(Rate) { Amount = 1f };
            var noise = Measure.PinkNoise(Rate / 2, 0.1);
            var l = new float[Block]; var r = new float[Block];
            var y = new List<float>(); var dry = new List<float>();
            for (int b = 0; b < 300; b++)
            {
                for (int i = 0; i < Block; i++) l[i] = r[i] = b * Block + i < noise.Length ? noise[b * Block + i] : 0f;
                dry.AddRange(l);
                room.Process(l, r, Block);
                y.AddRange(l);
            }
            var a = y.ToArray();
            int end = noise.Length;
            double burst = Measure.Db(Measure.Rms(a, Rate / 4, 4800)), tail1 = Measure.Db(Measure.Rms(a, end + 2400, 2400)), tail2 = Measure.Db(Measure.Rms(a, end + Rate * 3 / 10, 2400));
            Measure.Report($"Room at 100 % on a pink noise burst: {burst:F1} dB during it, {tail1:F1} dB 50 ms after it ends, {tail2:F1} dB 300 ms after");
            Assert.That(tail1, Is.GreaterThan(burst - 25), "a tail follows");
            Assert.That(tail2, Is.LessThan(tail1 - 8), "it decays");
        }

        [Test]
        public void ToneFollowsItsDocumentedResponse()
        {
            foreach (float tone in new[] { -1f, -0.5f, 0.5f, 1f })
                foreach (double hz in new[] { 200.0, 1000.0, 4000.0, 9000.0 })
                {
                    var tilt = new TiltFx(Rate) { Tone = tone };
                    var l = new float[Block]; var r = new float[Block];
                    double rms = 0, n = 0;
                    for (int b = 0; b < 120; b++)
                    {
                        Sine(l, r, b * Block, hz, 0.1);
                        tilt.Process(l, r, Block);
                        if (b >= 60) { for (int i = 0; i < Block; i++) rms += l[i] * l[i]; n += Block; }
                    }
                    double measured = 10 * Math.Log10(rms / n / (0.1 * 0.1 / 2));
                    Assert.That(measured, Is.EqualTo(TiltFx.ResponseDb(tone, hz, Rate)).Within(0.6), $"tone {tone} at {hz} Hz");
                }
            Assert.That(TiltFx.ResponseDb(0, 3000, Rate), Is.EqualTo(0).Within(1e-6));
            Assert.That(TiltFx.ResponseDb(-1, 9000, Rate), Is.LessThan(-8));
            Assert.That(TiltFx.ResponseDb(1, 9000, Rate), Is.GreaterThan(6));
        }

        [Test]
        public void ChorusAndRoomStopAfterLongSilenceAndResumeCleanly()
        {
            var chorus = new ChorusFx(Rate) { Amount = 0.6f };
            var room = new RoomFx(Rate) { Amount = 0.6f };
            var l = new float[Block]; var r = new float[Block];
            for (int b = 0; b < 80; b++) { Sine(l, r, b * Block, 330, 0.3); chorus.Process(l, r, Block); Sine(l, r, b * Block, 330, 0.3); room.Process(l, r, Block); }
            for (int b = 0; b < 48000 * 10 / Block; b++)
            {
                Array.Clear(l); Array.Clear(r); chorus.Process(l, r, Block);
                Array.Clear(l); Array.Clear(r); room.Process(l, r, Block);
                if (b * Block > Rate * 9) Assert.That(l.Max(Math.Abs), Is.EqualTo(0f), "silent once stopped");
            }
            double level = 0;
            for (int b = 0; b < 200; b++)
            {
                Sine(l, r, b * Block, 330, 0.3); chorus.Process(l, r, Block);
                Sine(l, r, b * Block, 330, 0.3); room.Process(l, r, Block);
                foreach (float v in l) { Assert.That(float.IsFinite(v) && Math.Abs(v) < 3); }
                if (b >= 100) level += Measure.Rms(l, 0, Block);
            }
            Assert.That(level / 100, Is.InRange(0.1, 0.7), "the sound is back at about its old level");
        }

        [Test]
        public void TheChorusAddsNoNoiseWhenNothingPlays()
        {
            var chorus = new ChorusFx(Rate) { Amount = 1f };
            var l = new float[Block]; var r = new float[Block];
            for (int b = 0; b < 60; b++) { Sine(l, r, b * Block, 330, 0.3); chorus.Process(l, r, Block); }
            float peak = 0;
            for (int b = 0; b < 400; b++)
            {
                Array.Clear(l); Array.Clear(r);
                chorus.Process(l, r, Block);
                if (b >= 100) for (int i = 0; i < Block; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            }
            Assert.That(peak, Is.LessThan(1e-7f), "after the signal and the delay have gone, the chorus is silent");
        }

        [Test]
        public void TheChorusWidensAndLeavesTheLevelAlone()
        {
            var chorus = new ChorusFx(Rate) { Amount = 1f };
            var l = new float[Block]; var r = new float[Block];
            double dry = 0, wet = 0, diff = 0, n = 0;
            for (int b = 0; b < 300; b++)
            {
                Sine(l, r, b * Block, 330, 0.3);
                var d = (float[])l.Clone();
                chorus.Process(l, r, Block);
                if (b < 100) continue;
                for (int i = 0; i < Block; i++) { dry += d[i] * d[i]; wet += l[i] * l[i]; diff += (l[i] - r[i]) * (l[i] - r[i]); n++; }
            }
            Measure.Report($"Chorus at 100 %: level {10 * Math.Log10(wet / dry):F1} dB against the dry, side {10 * Math.Log10(diff / wet):F1} dB");
            Assert.That(10 * Math.Log10(wet / dry), Is.InRange(-3, 6));
            Assert.That(diff, Is.GreaterThan(0), "the two sides differ");
        }
    }
}
