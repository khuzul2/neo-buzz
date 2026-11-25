using System.Security.Cryptography;
using NeoBuzz.Dsp;
using NeoBuzz.Sampler;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Sampler, a sample per note (docs/specs/machines.md, Neo Sampler Tracker mode; TM1).</summary>
    [TestFixture]
    public class SamplerTrackerTests
    {
        const int Rate = Measure.Rate;

        [OneTimeSetUp]
        public void BuildTheBuiltIns() => BuiltIns.BuildNow();

        static string Hash(float[] l, float[] r)
        {
            using var sha = SHA256.Create();
            var bytes = new byte[(l.Length + r.Length) * 4];
            Buffer.BlockCopy(l, 0, bytes, 0, l.Length * 4);
            Buffer.BlockCopy(r, 0, bytes, l.Length * 4, r.Length * 4);
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        /// <summary>The render the sampler made before Tracker mode existed: Instrument and Kit modes, every path of NoteOn.</summary>
        static string Render(int mode)
        {
            var (e, v, bank) = SamplerTests.Make(v =>
            {
                v.Set(P.Mode, mode);
                v.Set(SamplerTests.Z(0, SamplerEngine.FWave), 221);
                v.Set(SamplerTests.Z(0, SamplerEngine.FKeyHi), 59);
                v.Set(SamplerTests.Z(1, SamplerEngine.FWave), 1);
                v.Set(SamplerTests.Z(1, SamplerEngine.FKeyLo), 60);
                v.Set(SamplerTests.Z(1, SamplerEngine.FLoop), 2);
                SamplerTests.Set(v, SamplerTests.Z(1, SamplerEngine.FLoopS), 30.0);
                SamplerTests.Set(v, SamplerTests.Z(1, SamplerEngine.FLoopE), 70.0);
                v.Set(SamplerTests.Z(1, SamplerEngine.FChoke), 2);
                v.Set(P.FType, 2);
                v.Set(P.FReso, 500);
                v.Set(P.Polyphony, 3);
                v.Set(P.VelAmp, 700);
            });
            bank.Set(1, SamplerTests.Slot("noise", SamplerTests.Mono(Measure.PinkNoise(Rate, 0.2, 3), root: 64, rate: 44100)));
            var (l, r) = SamplerTests.Run(e, 2 * Rate, 64,
                (0, () => e.NoteOn(0, 36, 0.8f)),
                (1000, () => e.NoteOn(1, 37, 0.5f)),
                (7777, () => e.NoteOn(2, 62, 1f)),
                (9001, () => e.PitchBend(0.3f)),
                (12000, () => e.NoteOn(3, 66, 0.7f)),
                (20000, () => e.NoteOn(4, 52, 0.9f)),
                (30011, () => e.NoteOff(0)),
                (50000, () => e.NoteOff(2)),
                (70000, () => e.AllNotesOff(false)));
            return Hash(l, r);
        }

        [Test]
        public void InstrumentAndKitRenderAsBeforeTrackerMode()
        {
            Assert.That(Render(0), Is.EqualTo("ACEAC71C1538B82568952015CF6975605395ED59CC332D476811C1613491EF8F"), "Instrument mode");
            Assert.That(Render(1), Is.EqualTo("76A7C91F0DA1367667F718806FAC54E97291C3539602ADA530BB13873783B480"), "Kit mode");
        }

        [Test]
        public void TrackerModeWithoutSampleNumbersIsInstrumentMode()
        {
            Assert.That(Render(2), Is.EqualTo(Render(0)));
        }

        // ---------------------------------------------------------------- a sample per note

        /// <summary>A wave of seeded noise, 3000 frames: every frame different, so a shift of one frame shows.</summary>
        static float[] Noise(int seed, int frames = 3000)
        {
            var rng = new Random(seed);
            var x = new float[frames];
            for (int i = 0; i < frames; i++) x[i] = (float)(rng.NextDouble() - 0.5);
            return x;
        }

        static (SamplerEngine E, ParamValues V, SampleBank Bank) Tracker(Action<ParamValues> setup = null)
        {
            var (e, v, bank) = SamplerTests.Make(v =>
            {
                v.Set(P.Mode, SamplerEngine.ModeTracker);
                v.Set(P.Polyphony, 32);
                setup?.Invoke(v);
            });
            bank.Set(1, SamplerTests.Slot("a", SamplerTests.Mono(Noise(1), root: 60)));
            bank.Set(2, SamplerTests.Slot("b", SamplerTests.Mono(Noise(2), root: 72)));
            bank.Set(3, SamplerTests.Slot("c", SamplerTests.Mono(Noise(3), root: 36)));
            return (e, v, bank);
        }

        /// <summary>The render <paramref name="out"/> from <paramref name="at"/> is the wave from frame <paramref name="offset"/>, one gain throughout (after the attack).</summary>
        static void AssertPlays(float[] output, int at, float[] wave, int offset, string what, int from = 200, int count = 1500)
        {
            double g = output[at + from] / wave[offset + from];
            Assert.That(g, Is.GreaterThan(0.1).And.LessThan(2.0), what + ": gain");
            double worst = 0;
            for (int i = from; i < from + count; i++) worst = Math.Max(worst, Math.Abs(output[at + i] - g * wave[offset + i]));
            Assert.That(worst, Is.LessThan(1e-5), what + ": the wave from its frame " + offset);
        }

        [TestCase(16)]
        [TestCase(64)]
        [TestCase(256)]
        public void EachNotePlaysItsOwnSampleFromItsOffsetAtAnyBlockSize(int block)
        {
            var (e, v, bank) = Tracker();
            var waves = new[] { Noise(1), Noise(2), Noise(3) };
            const int a = 1000, b = 9000, c = 17000, d = 25000;
            var (l, r) = SamplerTests.Run(e, 32000, block,
                (a, () => e.NoteOnSample(0, 48, 1f, 1, 0, 0)),
                (b, () => e.NoteOnSample(1, 48, 1f, 2, 123, 0)),
                (c, () => e.NoteOnSample(2, 48, 1f, 3, 1, 0)),
                (d, () => e.NoteOnSample(3, 48, 1f, 1, 777, 0)));
            AssertPlays(l, a, waves[0], 0, "sample 1");
            AssertPlays(l, b, waves[1], 123, "sample 2 from 123");
            AssertPlays(l, c, waves[2], 1, "sample 3 from 1");
            AssertPlays(l, d, waves[0], 777, "sample 1 from 777");
            AssertPlays(r, b, waves[1], 123, "right channel");
            // Silence before the first note: nothing starts early.
            Assert.That(Measure.Rms(l, 0, a), Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void TheOutputIsTheSameAtEveryBlockSize()
        {
            (float[] L, float[] R) Play(int block)
            {
                var (e, v, bank) = Tracker();
                return SamplerTests.Run(e, 40000, block,
                    (0, () => e.NoteOnSample(0, 48, 0.8f, 1, 10, 0)),
                    (1001, () => e.NoteOnSample(1, 55, 0.5f, 2, 500, -37)),
                    (4000, () => e.NoteOn(2, 60, 0.9f)),
                    (7777, () => e.NoteOnSample(3, 40, 1f, 3, 0, 1234)),
                    (9001, () => e.NoteOff(1)),
                    (15000, () => e.NoteOnSample(0, 47, 1f, 2, 2999, 0)),
                    (30011, () => e.AllNotesOff(false)));
            }
            var a = Play(256);
            Assert.That(Play(64).L, Is.EqualTo(a.L));
            Assert.That(Play(16).R, Is.EqualTo(a.R));
            Assert.That(Measure.Rms(a.L, 0, a.L.Length), Is.GreaterThan(0.01));
        }

        [Test]
        public void TheKeyIsThePitchAndC4IsTheSamplesOwnSpeed()
        {
            // A 480 Hz sine that fills a second: at Buzz C-4 (MIDI 48) it is 480 Hz, an octave up 960, and the wave's own root (60 here) doesn't move it.
            var (e, v, bank) = Tracker();
            bank.Set(4, SamplerTests.Slot("sine", SamplerTests.Mono(SamplerTests.SineWave(480, Rate, 0.5), root: 60)));
            var (l, _) = SamplerTests.Run(e, 3 * Rate / 2, 64,
                (0, () => e.NoteOnSample(0, 48, 1f, 4, 0, 0)),
                (Rate, () => { e.AllNotesOff(true); }),
                (Rate + 2000, () => e.NoteOnSample(1, 60, 1f, 4, 0, 0)));
            Assert.That(Frequency(l, 1000, Rate / 2), Is.EqualTo(480).Within(0.01));
            Assert.That(Frequency(l, Rate + 3000, Rate + 8000), Is.EqualTo(960).Within(0.05));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(-1)]
        [TestCase(37)]
        [TestCase(-137)]
        [TestCase(99)]
        [TestCase(1200)]
        [TestCase(-2400)]
        [TestCase(2400)]
        public void TuneIsWithinACentOfTheRequest(int cents)
        {
            var (e, v, bank) = Tracker();
            bank.Set(4, SamplerTests.Slot("sine", SamplerTests.Mono(SamplerTests.SineWave(220, 2 * Rate, 0.5), root: 60)));
            var (l, _) = SamplerTests.Run(e, Rate, 64, (0, () => e.NoteOnSample(0, 48 + 7, 1f, 4, 0, cents)));
            double hz = Frequency(l, 3000, Rate / 2);
            double expected = 220 * Math.Pow(2, (7 + cents / 100.0) / 12);
            double error = 1200 * Math.Log2(hz / expected);
            Assert.That(Math.Abs(error), Is.LessThan(1.0), $"{hz:0.000} Hz against {expected:0.000} Hz: {error:0.0000} cents");
        }

        /// <summary>Frequency by rising zero crossings over the window.</summary>
        static double Frequency(float[] x, int from, int to)
        {
            double first = -1, last = -1;
            int count = 0;
            for (int i = from + 1; i < to; i++)
                if (x[i - 1] < 0 && x[i] >= 0)
                {
                    double t = i - 1 + x[i - 1] / (x[i - 1] - x[i]);
                    if (first < 0) first = t;
                    last = t;
                    count++;
                }
            return (count - 1) * Rate / (last - first);
        }

        [Test]
        public void ANoteWithoutASampleNumberPlaysAsAlwaysAndTheNumberIsIgnoredOutsideTrackerMode()
        {
            (float[] L, float[] R) Play(int mode, bool withSample)
            {
                var (e, v, bank) = Tracker(v => { v.Set(P.Mode, mode); v.Set(SamplerTests.Z(0, SamplerEngine.FWave), 1); });
                return SamplerTests.Run(e, 8000, 64, (0, () =>
                {
                    if (withSample) e.NoteOnSample(0, 52, 0.7f, 3, 400, 50);
                    else e.NoteOn(0, 52, 0.7f);
                }));
            }
            var plain = Play(2, false);
            Assert.That(Play(2, true).L, Is.Not.EqualTo(plain.L), "a sample number in Tracker mode plays that sample");
            Assert.That(Play(0, true).L, Is.EqualTo(Play(0, false).L), "Instrument mode ignores it");
            Assert.That(Play(1, true).L, Is.EqualTo(Play(1, false).L), "Kit mode ignores it");
            var zero = SamplerTests.Make(v => { v.Set(P.Mode, 2); v.Set(P.Polyphony, 32); });
            Assert.That(zero.Engine, Is.Not.Null);
        }

        [Test]
        public void AStolenVoiceStartsTheNewSampleFromItsOffsetAfterTheFade()
        {
            var (e, v, bank) = Tracker(v => v.Set(P.Polyphony, 1));
            var (l, _) = SamplerTests.Run(e, 12000, 16,
                (0, () => e.NoteOnSample(0, 48, 1f, 1, 0, 0)),
                (2000, () => e.NoteOnSample(1, 48, 1f, 2, 321, 0)));
            // The old note fades in 3 ms (144 frames), then the new one starts from its frame 321: find where it begins and check it frame for frame.
            var wave = Noise(2);
            int found = -1;
            for (int shift = 100; shift < 400 && found < 0; shift++)
            {
                double g = l[2000 + shift + 300] / wave[321 + 300];
                if (Math.Abs(g) < 0.1) continue;
                double worst = 0;
                for (int j = 300; j < 700; j++) worst = Math.Max(worst, Math.Abs(l[2000 + shift + j] - g * wave[321 + j]));
                if (worst < 1e-5) found = shift;
            }
            Assert.That(found, Is.InRange(144, 144 + 16), "the new note starts when the fade ends");
        }

        [Test]
        public void ThePathAllocatesNothingWithSixtyFourNotesAtOnce()
        {
            var (e, v, bank) = Tracker();
            var l = new float[SamplerEngine.MaxBlock];
            var r = new float[SamplerEngine.MaxBlock];
            void Block(int b)
            {
                // 64 note-ons over 16 blocks, more than the 32 voices: steals, restarts, offsets and tunes all moving.
                if (b % 16 < 16 && b % 4 == 0)
                    for (int k = 0; k < 16; k++) e.NoteOnSample(k, 40 + (b + k) % 24, 0.4f + 0.05f * (k % 8), 1 + (b + k) % 3, (b * 31 + k * 97) % 2500, ((b + k) % 41 - 20) * 5);
                if (b % 7 == 0) e.NoteOff((b + 3) % 16);
                e.Process(l, r, SamplerEngine.MaxBlock);
            }
            for (int b = 0; b < 200; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, Block);
            Measure.Report($"Neo Sampler, Tracker mode: {bytes} bytes allocated over 2000 blocks (64 notes a 4 blocks on 32 voices, samples, offsets and tunes changing)");
            Assert.That(bytes, Is.EqualTo(0), why);
        }
    }
}
