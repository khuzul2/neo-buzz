using NeoBuzz.Dsp;
using NeoBuzz.Gate;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Gate (docs/specs/mastering-tools.md §5.13): the state machine, the floor and the expander, the key, measured.</summary>
    [TestFixture]
    public class GateTests
    {
        const int Rate = Measure.Rate;

        static (GateEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new GateEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (float[] L, float[] R) Run(GateEngine e, float[] l, float[] r, int block = GateEngine.MaxBlock, float[] kl = null, float[] kr = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[GateEngine.MaxBlock];
            var br = new float[GateEngine.MaxBlock];
            var xl = new float[GateEngine.MaxBlock];
            var xr = new float[GateEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                if (kl != null) { Array.Copy(kl, pos, xl, 0, n); Array.Copy(kr, pos, xr, 0, n); }
                e.Process(bl, br, kl != null ? xl : null, kl != null ? xr : null, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        /// <summary>A 1 kHz tone of the given amplitude between samples <paramref name="from"/> and <paramref name="to"/>, silence elsewhere.</summary>
        static float[] Burst(int length, int from, int to, double amplitude, double hz = 1000)
        {
            var x = new float[length];
            for (int i = from; i < to; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
            return x;
        }

        static double Level(float[] x, int from, int n) => Measure.Db(Measure.Rms(x, from, n));

        static Action<ParamValues> Gate(double threshold = -30, double hysteresis = 6, double attackMs = 5, double holdMs = 100, double releaseMs = 200, double range = -90, double ratio = double.PositiveInfinity) => v =>
        {
            Set(v, P.Threshold, threshold);
            Set(v, P.Hysteresis, hysteresis);
            Set(v, P.Attack, attackMs * 0.001);
            Set(v, P.Hold, holdMs * 0.001);
            Set(v, P.Release, releaseMs * 0.001);
            Set(v, P.Range, range);
            Set(v, P.Ratio, ratio);
        };

        // ---- open, closed, range -------------------------------------------------------------------

        [Test]
        public void AnOpenGatePassesTheAudioBitForBitAndAClosedOneMutesIt()
        {
            int n = Rate * 2;
            var tone = Burst(n, 0, Rate, 0.3);           // 1 s of tone at −10.5 dBFS, then silence
            var (e, _) = Make(Gate(holdMs: 50, releaseMs: 50));
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            // After the 5 ms attack the gate is open: the audio passes exactly.
            for (int i = 2000; i < Rate - 10; i++)
                if (ol[i] != tone[i]) Assert.Fail($"differs while open at {i}");
            // 50 ms hold + 50 ms release after the end: closed, −90 dB.
            double closed = Measure.Peak(ol, Rate + 6000, Rate - 6000);
            TestContext.Out.WriteLine($"closed: {Measure.Db(closed)} dB");
            Assert.That(closed, Is.EqualTo(0));   // silent input stays silent: exactly 0
        }

        [TestCase(-90)]
        [TestCase(-40)]
        [TestCase(-12)]
        public void RangeIsTheClosedGatesGain(double range)
        {
            int n = Rate * 3;
            var quiet = Burst(n, 0, n, 0.002);           // −54 dBFS: under the threshold the whole time
            var (e, _) = Make(Gate(range: range));
            var (ol, _) = Run(e, quiet, (float[])quiet.Clone());
            double got = Level(ol, Rate, Rate) - Level(quiet, Rate, Rate);
            TestContext.Out.WriteLine($"Range {range} dB: closed gain {got:F2} dB");
            Assert.That(got, Is.EqualTo(Math.Max(range, -90 + 0.0)).Within(0.05));
        }

        [Test]
        public void HysteresisKeepsAnOpenGateOpenBetweenTheThresholds()
        {
            int n = Rate * 3;
            // −34 dBFS peak: between the close (−36) and open (−30) thresholds. First opened by a loud burst, then the quiet tone.
            var x = new float[n];
            var loud = Burst(n, 0, 12000, 0.3);
            var quiet = Burst(n, 12000, n, 0.02);
            for (int i = 0; i < n; i++) x[i] = loud[i] + quiet[i];
            var (e, _) = Make(Gate(hysteresis: 6, holdMs: 20));
            var (ol, _) = Run(e, x, (float[])x.Clone());
            Assert.That(Level(ol, Rate, Rate), Is.EqualTo(Level(x, Rate, Rate)).Within(0.01), "stays open");
            // The same quiet tone on its own never opens the gate.
            var (e2, _) = Make(Gate(hysteresis: 6, holdMs: 20));
            var (ol2, _) = Run(e2, quiet, (float[])quiet.Clone());
            Assert.That(Level(ol2, Rate, Rate), Is.LessThan(Level(quiet, Rate, Rate) - 80), "never opens");
        }

        [Test]
        public void HoldKeepsTheGateOpenAfterTheSignalFallsAndReleaseRampsItDown()
        {
            int n = Rate * 2;
            var tone = Burst(n, 0, 24000, 0.3);          // ends at 0.5 s
            var (e, _) = Make(Gate(holdMs: 200, releaseMs: 100));
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            // The level follower falls 6 dB under the threshold ~ 20 ms after the end; the output is the tone's tail only if it exists, so
            // use a signal that continues under the close threshold instead: a quiet tone after the loud one.
            var quiet = Burst(n, 24000, n, 0.004);       // −48 dBFS, under the close threshold
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = tone[i] + quiet[i];
            var (e2, _) = Make(Gate(holdMs: 200, releaseMs: 100));
            var (o2, _) = Run(e2, x, (float[])x.Clone());
            double Win(int from) => Level(o2, from, 480) - Level(quiet, 24000 + 4800, 480);
            // 0.5 s + 20 ms (detector) + hold 200 ms = 0.72 s, then a 100 ms ramp to nothing (to −90 dB at the end).
            Assert.That(Win(24000 + 4800), Is.EqualTo(0).Within(0.1), "held open 100 ms after the end");
            Assert.That(Win(24000 + 9000), Is.EqualTo(0).Within(0.1), "still held at 190 ms");
            double during = Win(Rate * 5 / 6 / 1 > 0 ? 24000 + 12000 + 2400 : 0);
            TestContext.Out.WriteLine($"mid-release: {during:F1} dB");
            Assert.That(during, Is.LessThan(-3).And.GreaterThan(-40), "mid-release");
            Assert.That(Win(24000 + 26000), Is.LessThan(-60), "closed after hold + release");
        }

        [Test]
        public void AttackIsALinearRampAndTheLookaheadLetsTheTransientThroughWhole()
        {
            int n = Rate;
            var x = Burst(n, 4800, n, 0.3);              // onset at 100 ms
            var (e, _) = Make(Gate(attackMs: 10, holdMs: 100, releaseMs: 100));
            var (ol, _) = Run(e, x, (float[])x.Clone());
            // The gate's gain after the onset: the audio's envelope over its input, per 1 ms window, rises linearly to 1.
            double G(int from) => Measure.Rms(ol, from, 48) / Math.Max(1e-9, Measure.Rms(x, from, 48));
            double g3 = G(4800 + 96), g6 = G(4800 + 216);   // centred 2.5 and 5 ms into the 10 ms ramp: 0.25 and 0.5
            TestContext.Out.WriteLine($"gain {g3:F2} at 2.5 ms, {g6:F2} at 5 ms into the 10 ms ramp");
            Assert.That(g3, Is.EqualTo(0.25).Within(0.04));
            Assert.That(g6, Is.EqualTo(0.5).Within(0.04));
            // With a 10 ms lookahead the whole onset is through at full gain.
            var (e2, _) = Make(v => { Gate(attackMs: 3, holdMs: 100, releaseMs: 100)(v); Set(v, P.Lookahead, 10); });
            var (o2, _) = Run(e2, x, (float[])x.Clone());
            int la = e2.Latency;
            Assert.That(la, Is.EqualTo((int)Math.Round(0.010 * Rate)));
            for (int i = 4800 + 20; i < 4800 + 2000; i++)
                if (o2[i + la] != x[i]) Assert.Fail($"the onset is shaped at {i}");
        }

        // ---- the expander --------------------------------------------------------------------------

        [TestCase(2.0)]
        [TestCase(4.0)]
        public void AnExpanderCutsEachDbUnderTheCloseThresholdByRatioMinusOne(double ratio)
        {
            int n = Rate * 4;
            // A tone 8, 14 and 20 dB under the close threshold (−36 dBFS): each dB under it is (ratio − 1) dB more cut. The peak follower
            // reads a sine about a dB low, so the cuts are checked by their slope (exact) and absolutely within 1.5 dB.
            double[] cut = new double[3];
            double[] under = { 8, 14, 20 };
            for (int k = 0; k < 3; k++)
            {
                var tone = Burst(n, 0, n, Math.Pow(10, (-36 - under[k]) / 20));
                var (e, _) = Make(Gate(threshold: -30, hysteresis: 6, ratio: ratio, releaseMs: 50));
                var (ol, _) = Run(e, tone, (float[])tone.Clone());
                cut[k] = Level(ol, Rate * 2, Rate) - Level(tone, Rate * 2, Rate);
                Assert.That(cut[k], Is.EqualTo(-under[k] * (ratio - 1)).Within(1.5 * (ratio - 1)), $"{under[k]} dB under");
            }
            TestContext.Out.WriteLine($"ratio {ratio}: cuts {cut[0]:F2}, {cut[1]:F2}, {cut[2]:F2} dB for 8, 14, 20 dB under the close threshold");
            Assert.That(cut[0] - cut[1], Is.EqualTo(6 * (ratio - 1)).Within(0.25), "the slope");
            Assert.That(cut[1] - cut[2], Is.EqualTo(6 * (ratio - 1)).Within(0.25), "the slope");
        }

        // ---- the key ---------------------------------------------------------------------------------

        [Test]
        public void TheKeyFilterKeepsRumbleFromOpeningTheGate()
        {
            int n = Rate * 2;
            var rumble = Burst(n, 0, n, 0.3, 60);
            var (e, _) = Make(v => { Gate()(v); Set(v, P.KeyLow, 500); });
            var (ol, _) = Run(e, rumble, (float[])rumble.Clone());
            Assert.That(Level(ol, Rate, Rate), Is.LessThan(Level(rumble, Rate, Rate) - 60), "60 Hz is filtered out of the key: stays shut");
            var (e2, _) = Make(Gate());
            var (o2, _) = Run(e2, rumble, (float[])rumble.Clone());
            Assert.That(Level(o2, Rate, Rate), Is.EqualTo(Level(rumble, Rate, Rate)).Within(0.01), "without the filter it opens");
            var hiss = Burst(n, 0, n, 0.3, 12000);
            var (e3, _) = Make(v => { Gate()(v); Set(v, P.KeyHigh, 4000); });
            var (o3, _) = Run(e3, hiss, (float[])hiss.Clone());
            Assert.That(Level(o3, Rate, Rate), Is.LessThan(Level(hiss, Rate, Rate) - 60), "12 kHz filtered out by the low-pass: stays shut");
        }

        [Test]
        public void TheExternalSidechainOpensTheGateForQuietAudio()
        {
            int n = Rate * 2;
            var audio = Burst(n, 0, n, 0.01, 440);       // −40 dBFS: shut on its own
            var key = Burst(n, 12000, 36000, 0.5, 100);  // loud, from 0.25 s to 0.75 s
            var silent = new float[n];
            var (e, _) = Make(v => { Gate(holdMs: 10, releaseMs: 10)(v); v.Set(P.Sidechain, 1); });
            var (ol, _) = Run(e, audio, (float[])audio.Clone(), GateEngine.MaxBlock, key, (float[])key.Clone());
            Assert.That(Level(ol, 18000, 12000), Is.EqualTo(Level(audio, 18000, 12000)).Within(0.01), "open while the key is loud");
            Assert.That(Level(ol, Rate + 20000, 12000), Is.LessThan(Level(audio, 18000, 12000) - 60), "shut when the key falls");
            var (e2, _) = Make(v => { Gate()(v); v.Set(P.Sidechain, 1); });
            var (o2, _) = Run(e2, audio, (float[])audio.Clone(), GateEngine.MaxBlock, silent, (float[])silent.Clone());
            Assert.That(Measure.Db(Measure.Rms(o2, Rate, Rate)), Is.LessThan(-85), "silent key: shut, though the audio is on the threshold's far side");
            _ = key;
        }

        [TestCase(0)]
        [TestCase(1)]
        public void BothDetectorsReadASineAtItsPeakLevel(int detection)
        {
            // Peak: the follower's reading of a sine. RMS: the power sum of both channels read on the peak scale (Pedal Gate's
            // sqrt((l² + r²) / N) is a sine's amplitude when both channels carry it): a −28 dBFS tone is under a −25 dB threshold, −21 over (the peak follower reads a 1 kHz sine about a dB low).
            int n = Rate * 2;
            foreach (var (amp, opens) in new[] { (-28.0, false), (-21.0, true) })
            {
                var tone = Burst(n, 0, n, Math.Pow(10, amp / 20));
                var (e, _) = Make(v => { Gate(threshold: -25)(v); v.Set(P.Detection, detection); });
                var (ol, _) = Run(e, tone, (float[])tone.Clone());
                double got = Level(ol, Rate, Rate) - Level(tone, Rate, Rate);
                Assert.That(got, opens ? Is.EqualTo(0).Within(0.01) : Is.LessThan(-60), $"{(detection == 0 ? "peak" : "RMS")} at {amp} dBFS");
            }
        }

        // ---- listen, delta, blocks, determinism ------------------------------------------------------

        [Test]
        public void ListenGivesTheFilteredKey()
        {
            int n = Rate;
            var tone = Burst(n, 0, n, 0.2, 100);
            var (e, _) = Make(v => { v.Set(P.Listen, 1); Set(v, P.KeyLow, 1000); });
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            Assert.That(Level(ol, Rate / 2, Rate / 4), Is.LessThan(Level(tone, Rate / 2, Rate / 4) - 25));
        }

        [Test]
        public void DeltaIsWhatTheGateTakesOut()
        {
            int n = Rate * 2;
            var quiet = Burst(n, 0, n, 0.002);
            var (e, _) = Make(v => { Gate()(v); v.Set(P.Delta, 1); });
            var (ol, _) = Run(e, quiet, (float[])quiet.Clone());
            Assert.That(Level(ol, Rate, Rate), Is.EqualTo(Level(quiet, Rate, Rate)).Within(0.01), "the closed gate took out all of it");
            var loud = Burst(n, 0, n, 0.3);
            var (e2, _) = Make(v => { Gate()(v); v.Set(P.Delta, 1); });
            var (o2, _) = Run(e2, loud, (float[])loud.Clone());
            Assert.That(Measure.Peak(o2, Rate, Rate), Is.EqualTo(0), "the open gate took out nothing");
        }

        [TestCase(0)]
        [TestCase(1)]
        public void BlocksOfAnySizeGiveTheSameBits(int detection)
        {
            int n = Rate * 3;
            var l = Measure.PinkNoise(n, 0.05, 21);
            var r = Measure.PinkNoise(n, 0.05, 22);
            for (int i = 0; i < n; i++) { float g = (i / 9600) % 3 == 0 ? 1f : 0.02f; l[i] *= g; r[i] *= g; }
            float[] first = null;
            foreach (int block in new[] { 256, 117, 16 })
            {
                var (e, _) = Make(v => { Gate(threshold: -30, ratio: 3, range: -40)(v); Set(v, P.Lookahead, 3); v.Set(P.Detection, detection); Set(v, P.KeyLow, 100); });
                var (ol, _) = Run(e, l, r, block);
                if (first == null) first = ol;
                else Assert.That(ol, Is.EqualTo(first), $"block {block}");
            }
        }

        [Test]
        public void TheLatencyIsTheLookahead()
        {
            foreach (double ms in new[] { 0.0, 1.0, 12.5, 25.0 })
            {
                var (e, _) = Make(v => { Gate(threshold: -80)(v); Set(v, P.Lookahead, ms); });   // −80: open at once for the impulse
                var impulse = new float[Rate];
                impulse[2000] = 0.5f;
                var (ol, _) = Run(e, impulse, new float[Rate]);
                int peak = 0;
                for (int i = 0; i < ol.Length; i++) if (Math.Abs(ol[i]) > Math.Abs(ol[peak])) peak = i;
                Assert.That(e.Latency, Is.EqualTo((int)Math.Round(ms * 0.001 * Rate)));
                Assert.That(peak, Is.EqualTo(2000 + e.Latency).Or.EqualTo(2000), $"{ms} ms");
            }
        }

        // ---- allocation, CPU -----------------------------------------------------------------------

        [Test]
        public void NothingIsAllocatedPerBlock()
        {
            var l = Measure.PinkNoise(256 * 8, 0.1, 5);
            var r = Measure.PinkNoise(256 * 8, 0.1, 6);
            for (int i = 0; i < l.Length; i++) if ((i / 512) % 2 == 1) { l[i] *= 0.01f; r[i] *= 0.01f; }
            var (e, v) = Make(x => { Gate(ratio: 4, range: -30)(x); Set(x, P.Lookahead, 2); });
            var bl = new float[256];
            var br = new float[256];
            var kl = new float[256];
            var kr = new float[256];
            void Block(int b)
            {
                if (b % 40 == 0) Set(v, P.Threshold, -35 + (b / 40 % 5) * 3);
                if (b % 100 == 0) { v.Set(P.Detection, (b / 100) % 2); v.Set(P.Sidechain, (b / 100) % 3 == 2 ? 1 : 0); }
                Array.Copy(l, (b % 8) * 256, bl, 0, 256);
                Array.Copy(r, (b % 8) * 256, br, 0, 256);
                Array.Copy(l, ((b + 3) % 8) * 256, kl, 0, 256);
                Array.Copy(r, ((b + 3) % 8) * 256, kr, 0, 256);
                e.Process(bl, br, kl, kr, 256);
            }
            for (int b = 0; b < 60; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 60));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [TestCase(0, 360, 250)]
        [TestCase(1, 360, 250)]
        public void RunsFasterThanRealTime(int detection, double x64, double arm64)
        {
            int n = Rate * 3;
            var l = Measure.PinkNoise(n, 0.1, 1);
            var r = Measure.PinkNoise(n, 0.1, 2);
            for (int i = 0; i < n; i++) if ((i / 9600) % 2 == 1) { l[i] *= 0.01f; r[i] *= 0.01f; }
            const int Warm = Rate;
            Throughput.Guard($"Neo Gate CPU ({(detection == 0 ? "peak" : "RMS")}, expander, lookahead, key filter)", Throughput.Floor(x64, arm64), 2,
                Rate, GateEngine.MaxBlock, () =>
            {
                var (e, _) = Make(v => { Gate(ratio: 4, range: -30)(v); Set(v, P.Lookahead, 2); v.Set(P.Detection, detection); Set(v, P.KeyLow, 100); });
                var bl = new float[GateEngine.MaxBlock];
                var br = new float[GateEngine.MaxBlock];
                void Block(int pos, int len)
                {
                    Array.Copy(l, pos, bl, 0, len);
                    Array.Copy(r, pos, br, 0, len);
                    e.Process(bl, br, null, null, len);
                }
                for (int pos = 0; pos < Warm; pos += GateEngine.MaxBlock) Block(pos, GateEngine.MaxBlock);
                return Throughput.InBlocks(GateEngine.MaxBlock, (pos, len) => Block(Warm + pos, len));
            }, Measure.Report);
        }
    }
}
