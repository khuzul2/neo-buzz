using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.Sampler;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Sampler (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class SamplerTests
    {
        const int Rate = Measure.Rate;

        [OneTimeSetUp]
        public void BuildTheBuiltIns() => BuiltIns.BuildNow();

        internal static void Set(ParamValues v, int p, double value) => v.Set(p, NeoBuzz.Sampler.Params.Table[p].Raw(value));

        internal static int Z(int zone, int field) => SamplerEngine.ZoneParam(zone, field);

        /// <summary>An engine at <paramref name="rate"/> with the defaults (zone 1: slot 1 across the keyboard), velocity not touching the level.</summary>
        internal static (SamplerEngine Engine, ParamValues Values, SampleBank Bank) Make(Action<ParamValues> setup = null, int rate = Rate)
        {
            var v = new ParamValues(NeoBuzz.Sampler.Params.Table);
            v.Set(P.VelAmp, 0);
            setup?.Invoke(v);
            var bank = new SampleBank();
            var e = new SamplerEngine(v, bank);
            e.SetSampleRate(rate);
            return (e, v, bank);
        }

        internal static SampleSlot Slot(string name, params SampleLayer[] layers) => new SampleSlot(name, layers);

        internal static SampleLayer Mono(float[] x, int root = 60, int rate = Rate, int loopStart = 0, int loopEnd = 0, WaveLoop loop = WaveLoop.None) =>
            SampleLayer.Build("test", x, null, rate, root, loopStart, loopEnd, loop);

        /// <summary>A sine of <paramref name="hz"/> for <paramref name="frames"/> samples.</summary>
        internal static float[] SineWave(double hz, int frames, double amplitude = 0.5, int rate = Rate)
        {
            var x = new float[frames];
            for (int i = 0; i < frames; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
            return x;
        }

        /// <summary>
        /// Renders <paramref name="length"/> samples in blocks of <paramref name="block"/>, split at the
        /// events' samples so each lands exactly there whatever the block size. Left and right.
        /// </summary>
        internal static (float[] L, float[] R) Run(SamplerEngine e, int length, int block = SamplerEngine.MaxBlock, params (int At, Action Do)[] events)
        {
            var l = new float[length];
            var r = new float[length];
            var bl = new float[SamplerEngine.MaxBlock];
            var br = new float[SamplerEngine.MaxBlock];
            var queue = events.OrderBy(x => x.At).ToList();
            int k = 0;
            for (int pos = 0; pos < length;)
            {
                while (k < queue.Count && queue[k].At <= pos) queue[k++].Do();
                int n = Math.Min(block, length - pos);
                if (k < queue.Count) n = Math.Min(n, queue[k].At - pos);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, l, pos, n);
                Array.Copy(br, 0, r, pos, n);
                pos += n;
            }
            return (l, r);
        }

        internal static float[] RunL(SamplerEngine e, int length, int block = SamplerEngine.MaxBlock, params (int At, Action Do)[] events) => Run(e, length, block, events).L;

        static double MaxStep(float[] x, int from, int to)
        {
            double m = 0;
            for (int i = Math.Max(1, from); i < to; i++) m = Math.Max(m, Math.Abs(x[i] - x[i - 1]));
            return m;
        }

        /// <summary>Frequency by zero crossings (rising, interpolated) over the window: exact to far better than a cent.</summary>
        static double Frequency(float[] x, int from, int to, int rate = Rate)
        {
            double first = -1, last = -1;
            int count = 0;
            for (int i = from + 1; i < to; i++)
            {
                if (x[i - 1] < 0 && x[i] >= 0)
                {
                    double t = i - 1 + x[i - 1] / (x[i - 1] - x[i]);
                    if (first < 0) first = t;
                    last = t;
                    count++;
                }
            }
            return (count - 1) * rate / (last - first);
        }

        // ------------------------------------------------------------------ playback

        [Test]
        public void AWaveAtItsRootAndTheSongsRatePlaysAsItIs()
        {
            var x = Measure.PinkNoise(Rate / 2, 0.2, 77);
            var (e, v, bank) = Make();
            bank.Set(1, Slot("noise", Mono(x, root: 60)));
            e.NoteOn(0, 60, 1f);
            var y = RunL(e, Rate / 2 + 1000);
            // The amp envelope's shortest attack (0.5 ms) first; then the samples themselves, at unity.
            double worst = 0;
            for (int i = 48; i < x.Length - 200; i++) worst = Math.Max(worst, Math.Abs(y[i] - x[i]));
            Measure.Report($"Neo Sampler: a wave at its root and rate against the wave itself, after the 0.5 ms attack: largest difference {worst:E2}");
            Assert.That(worst, Is.LessThan(1e-6));
            Assert.That(y.Skip(x.Length + 1).All(s => s == 0), Is.True, "silent after the wave's end");
        }

        [Test]
        public void PitchIsAccurateAcrossTheKeyboard()
        {
            foreach (int rate in new[] { 48000, 44100 })
            {
                double worst = 0;
                int worstNote = 0;
                for (int note = 21; note <= 108; note += 3)
                {
                    var (e, v, _) = Make(v => v.Set(Z(0, SamplerEngine.FWave), 216), rate);   // Built-in Sine: A4, a loop of exact cycles
                    e.NoteOn(0, note, 1f);
                    var y = RunL(e, rate, events: Array.Empty<(int, Action)>());
                    double f = Frequency(y, rate / 10, rate, rate);
                    double expect = 440 * Math.Pow(2, (note - 69) / 12.0);
                    double cents = Measure.Cents(f, expect);
                    if (Math.Abs(cents) > Math.Abs(worst)) { worst = cents; worstNote = note; }
                }
                Measure.Report($"Neo Sampler pitch at {rate} Hz, A0 … C8 every 3 semitones: worst {worst:+0.0000;-0.0000} cent (MIDI {worstNote})");
                Assert.That(Math.Abs(worst), Is.LessThan(0.05));
            }
        }

        [Test]
        public void BuiltInsAreMadeAndFinite()
        {
            for (int n = 1; n <= BuiltIns.Count; n++)
            {
                var slot = BuiltIns.Get(n);
                Assert.That(slot, Is.Not.Null, BuiltIns.Names[n - 1]);
                foreach (var l in slot.Layers)
                {
                    Assert.That(l.Length, Is.GreaterThan(1000));
                    Assert.That(l.Left[0].All(float.IsFinite), Is.True);
                    Assert.That(l.Peak, Is.InRange(0.3f, 1f), $"{BuiltIns.Names[n - 1]} peak");
                }
                TestContext.Out.WriteLine($"{200 + n} {BuiltIns.Names[n - 1]}: {slot.Layers.Length} layer(s), {slot.Layers[0].Length / (double)BuiltIns.Rate:F2} s, root {SamplerText.Note(slot.Layers[0].Root)}, loop {slot.Layers[0].Loop}");
            }
        }

        // ------------------------------------------------------------------ interpolation

        [Test]
        public void PitchingUpAndDownKeepsAliasesUnderMinus90Db()
        {
            // The built-in saw: every harmonic up to 21.6 kHz at full strength (1/n), the worst case
            // for folding; played from an octave down to four octaves up (levels 0 … 3), 48 kHz.
            double worst = 0;
            string at = "";
            foreach (int off in new[] { -12, -5, 0, 1, 5, 7, 12, 17, 19, 24, 31, 36, 43, 48 })
            {
                var (e, _, _) = Make(v => v.Set(Z(0, SamplerEngine.FWave), 217));
                e.NoteOn(0, 48 + off, 1f);
                var y = RunL(e, 8192 + Measure.N);
                double f0 = 30.0 * Rate / 11008 * Math.Pow(2, off / 12.0);
                double floor = PolyTests.AliasFloorWindowed(y, 8192, Measure.N, f0);
                TestContext.Out.WriteLine($"saw {off:+0;-0} st ({f0:F0} Hz, level {e.Voice(0).Level}, step {e.Voice(0).Step:F3}): {floor:F1} dB");
                if (floor > worst || at.Length == 0) { worst = floor; at = $"{off:+0;-0} st"; }
            }
            Measure.Report($"Neo Sampler, the band-limited saw (harmonics to 21.6 kHz) from −12 to +48 st: alias floor at most {worst:F1} dB ({at}; the measurement's floor is about −92 dB)");
            Assert.That(worst, Is.LessThan(-90));
        }

        [Test]
        public void TheKernelIsFlatInItsPassbandAndRejectsImages()
        {
            // Kernel 0 (steps up to 1) as the continuous filter its phases sample: its response, by
            // DTFT over all of them, in cycles per wave sample.
            var k = Interpolation.Kernel(0);
            double Response(double f)
            {
                double re = 0, im = 0;
                for (int p = 0; p < Interpolation.Phases; p++)
                    for (int j = 0; j < Interpolation.Taps; j++)
                    {
                        double t = j - (Interpolation.Half - 1) - p / (double)Interpolation.Phases;
                        double w = k[p * Interpolation.Taps + j];
                        re += w * Math.Cos(2 * Math.PI * f * t);
                        im -= w * Math.Sin(2 * Math.PI * f * t);
                    }
                return 10 * Math.Log10((re * re + im * im) / ((double)Interpolation.Phases * Interpolation.Phases));
            }
            double worstPass = Enumerable.Range(0, 76).Select(i => Math.Abs(Response(i * 0.005))).Max();
            double worstStop = Enumerable.Range(0, 120).Select(i => Response(0.55 + i * 0.0205)).Max();
            Measure.Report($"Neo Sampler's kernel (32 taps, Kaiser β 9): within {worstPass:F3} dB up to 0.375 of the wave's rate (18 kHz at 48 kHz); images from 0.55 to 3 times the rate at most {worstStop:F1} dB");
            Assert.That(worstPass, Is.LessThan(0.5));
            Assert.That(worstStop, Is.LessThan(-80));
        }

        // ------------------------------------------------------------------ loops

        [Test]
        public void TheWavesOwnLoopHasNoSeamAtAnyPitch()
        {
            // The built-in sine: a forward loop of exactly 110 cycles. Through its playback copy the
            // loop runs on without a seam, so the output is a pure sine at every pitch and level.
            double worst = -999;
            foreach (int note in new[] { 45, 57, 69, 76, 81, 88, 93 })
            {
                var (e, _, _) = Make(v => v.Set(Z(0, SamplerEngine.FWave), 216));
                e.NoteOn(0, note, 1f);
                var y = RunL(e, 4 * Rate);   // many passes of the loop (0.25 s at A4)
                double f0 = 440 * Math.Pow(2, (note - 69) / 12.0);
                double floor = PolyTests.AliasFloorWindowed(y, 4 * Rate - Measure.N, Measure.N, f0);
                worst = Math.Max(worst, floor);
                TestContext.Out.WriteLine($"MIDI {note} (level {e.Voice(0).Level}): everything but the sine {floor:F1} dB");
            }
            Measure.Report($"Neo Sampler, the wave's own loop (110 cycles) from A2 to A6: everything but the sine at most {worst:F1} dB");
            Assert.That(worst, Is.LessThan(-90));
        }

        [Test]
        public void AZoneLoopCrossfadesItsSeam()
        {
            // A sine whose loop doesn't hold whole cycles: without a crossfade the seam steps; with 10 ms it doesn't.
            var x = SineWave(441, Rate, 0.5);
            double own = MaxStep(x, 1, x.Length);
            double Seam(double xfadeSeconds)
            {
                var (e, v, bank) = Make(v =>
                {
                    v.Set(Z(0, SamplerEngine.FLoop), 2);                            // Forward
                    Set(v, Z(0, SamplerEngine.FLoopS), 25.0);
                    Set(v, Z(0, SamplerEngine.FLoopE), 77.7);
                    Set(v, Z(0, SamplerEngine.FXfade), xfadeSeconds);
                });
                bank.Set(1, Slot("sine", Mono(x)));
                e.NoteOn(0, 60, 1f);
                var y = RunL(e, 3 * Rate);
                return MaxStep(y, 100, y.Length);
            }
            double hard = Seam(0), soft = Seam(0.01);
            Measure.Report($"Neo Sampler, a forward zone loop off the sine's cycles: largest step {hard / own:F2}× the sine's own without a crossfade, {soft / own:F3}× with 10 ms");
            Assert.That(hard, Is.GreaterThan(3 * own), "the test hears the seam");
            Assert.That(soft, Is.LessThan(1.05 * own));
        }

        [Test]
        public void PingPongAndReversedLoopsStayContinuous()
        {
            var x = SineWave(441, Rate, 0.5);
            double own = MaxStep(x, 1, x.Length);
            foreach (var (loop, reverse) in new[] { (3, false), (3, true), (2, true) })
            {
                var (e, v, bank) = Make(v =>
                {
                    v.Set(Z(0, SamplerEngine.FLoop), loop);
                    Set(v, Z(0, SamplerEngine.FLoopS), 25.0);
                    Set(v, Z(0, SamplerEngine.FLoopE), 77.7);
                    Set(v, Z(0, SamplerEngine.FXfade), 0.01);
                    v.Set(Z(0, SamplerEngine.FRev), reverse ? 1 : 0);
                });
                bank.Set(1, Slot("sine", Mono(x)));
                e.NoteOn(0, 60, 1f);
                var y = RunL(e, 3 * Rate);
                double step = MaxStep(y, 100, y.Length);
                Measure.Report($"Neo Sampler, {(loop == 3 ? "ping-pong" : "forward")} loop{(reverse ? ", reversed" : "")}: largest step {step / own:F3}× the sine's own");
                Assert.That(step, Is.LessThan(1.05 * own));
                Assert.That(Measure.Rms(y, 2 * Rate, Rate), Is.GreaterThan(0.3), "still looping after 2 s");
            }
        }

        [Test]
        public void ReversedPlaysTheRegionBackwards()
        {
            var x = Measure.PinkNoise(Rate / 4, 0.2, 9);
            var (e, v, bank) = Make(v => { v.Set(Z(0, SamplerEngine.FRev), 1); v.Set(Z(0, SamplerEngine.FTrig), 1); });
            bank.Set(1, Slot("noise", Mono(x)));
            e.NoteOn(0, 60, 1f);
            var y = RunL(e, Rate / 2);
            double worst = 0;
            for (int i = 48; i < x.Length - 200; i++) worst = Math.Max(worst, Math.Abs(y[i] - x[x.Length - 1 - i]));
            Assert.That(worst, Is.LessThan(1e-6));
            Assert.That(y.Skip(x.Length).All(s => s == 0), Is.True);
        }

        // ------------------------------------------------------------------ envelopes, voices

        [Test]
        public void EnvelopeTimesAreAsSet()
        {
            // The looped sine held 0.6 s, then released: attack 100 ms to full, release 300 ms to −60 dB.
            var (e, v, _) = Make(v =>
            {
                v.Set(Z(0, SamplerEngine.FWave), 216);
                Set(v, P.AmpA, 0.1);
                Set(v, P.AmpR, 0.3);
            });
            int off = (int)(0.6 * Rate);
            var y = RunL(e, 2 * Rate, events: new (int, Action)[] { (0, () => e.NoteOn(0, 69, 1f)), (off, () => e.NoteOff(0)) });
            // Envelope: the peak of each 1.1 ms (half a cycle at 440 Hz) window.
            double Env(int at) => Measure.Peak(y, at - 26, 53);
            double full = Env(off - 1000);
            int reach = Enumerable.Range(30, Rate).First(i => Env(i) >= 0.995 * full);
            int sixty = Enumerable.Range(off, Rate).First(i => Env(i) <= full * 1e-3);
            double attack = reach / (double)Rate, release = (sixty - off) / (double)Rate;
            Measure.Report($"Neo Sampler envelopes: attack {attack * 1000:F1} ms for 100 ms, release to −60 dB {release * 1000:F1} ms for 300 ms");
            Assert.That(attack, Is.EqualTo(0.1).Within(0.005));
            Assert.That(release, Is.EqualTo(0.3).Within(0.015));
        }

        [Test]
        public void NoteStartsAndEndsDoNotClick()
        {
            // Starting mid-wave and ending at 60 %, one-shot: the shortest attack (0.5 ms) and the
            // end's 2 ms fade. From a zero crossing every step is within the sine's own; from its peak
            // (the worst case) the attack's first steps are a little steeper than the sine's.
            var x = SineWave(441, Rate, 0.8);
            double own = MaxStep(x, 1, x.Length);
            double Steps(double startPercent, out int last)
            {
                var (e, v, bank) = Make(v =>
                {
                    Set(v, Z(0, SamplerEngine.FStart), startPercent);
                    Set(v, Z(0, SamplerEngine.FEnd), 60.0);
                    v.Set(Z(0, SamplerEngine.FTrig), 1);
                });
                bank.Set(1, Slot("sine", Mono(x)));
                e.NoteOn(0, 60, 1f);
                var y = RunL(e, Rate);
                last = Array.FindLastIndex(y, s => s != 0);
                return MaxStep(y, 0, y.Length);
            }
            // 441 Hz: 25 % of a second is 110¼ cycles (a peak); 25.17 % is near a zero crossing.
            double peak = Steps(25.0, out int lastPeak), zero = Steps(100.0 * Math.Round(0.25 * 441) / 441 / 1.0, out _);
            double region = (0.6 - 0.25) * Rate;
            Measure.Report($"Neo Sampler, notes starting and ending mid-wave: largest step from a zero crossing {zero / own:F3}×, from the peak {peak / own:F3}× the sine's own (the 0.5 ms attack); the last sound {lastPeak - region:+0;-0} samples from the region's end");
            Assert.That(zero, Is.LessThan(1.02 * own));
            Assert.That(peak, Is.LessThan(1.25 * own));
            Assert.That(lastPeak, Is.InRange(region - 2, region));
        }

        [Test]
        public void ChokeAndStealFadeWithoutClicks()
        {
            var x = SineWave(441, Rate, 0.8);
            double own = MaxStep(x, 1, x.Length);
            // Choke: zones 1 and 2 in group 1 (a closed hat cuts an open one).
            {
                var (e, v, bank) = Make(v =>
                {
                    v.Set(Z(0, SamplerEngine.FKeyHi), 60);
                    v.Set(Z(0, SamplerEngine.FChoke), 1);
                    v.Set(Z(0, SamplerEngine.FLoop), 2);
                    v.Set(Z(1, SamplerEngine.FWave), 2);
                    v.Set(Z(1, SamplerEngine.FKeyLo), 61);
                    v.Set(Z(1, SamplerEngine.FChoke), 1);
                    v.Set(Z(1, SamplerEngine.FLevel), 0);                         // silent: what's left is the choked note
                });
                bank.Set(1, Slot("sine", Mono(x)));
                bank.Set(2, Slot("sine", Mono(x)));
                int at = Rate / 4;
                var y = RunL(e, Rate / 2, events: new (int, Action)[] { (0, () => e.NoteOn(0, 60, 1f)), (at, () => e.NoteOn(1, 62, 1f)) });
                double step = MaxStep(y, at - 10, y.Length);
                int quiet = Array.FindLastIndex(y, s => s != 0) - at;
                Measure.Report($"Neo Sampler choke: largest step {step / own:F3}× the sine's own, silent {quiet / (double)Rate * 1000:F2} ms after the choking note");
                Assert.That(step, Is.LessThan(1.05 * own));
                Assert.That(quiet, Is.LessThanOrEqualTo((int)(SamplerEngine.ChokeSeconds * Rate) + 1));
            }
            // Steal: polyphony 1, a second note takes the voice after a 3 ms fade.
            {
                var (e, v, bank) = Make(v =>
                {
                    v.Set(P.Polyphony, 1);
                    v.Set(Z(0, SamplerEngine.FLoop), 2);
                });
                bank.Set(1, Slot("sine", Mono(x)));
                int at = Rate / 4;
                var y = RunL(e, Rate / 2, events: new (int, Action)[] { (0, () => e.NoteOn(0, 60, 1f)), (at, () => e.NoteOn(1, 67, 1f)) });
                double step = MaxStep(y, at - 10, at + 1000);
                double ownHigh = own * Math.Pow(2, 7 / 12.0);
                Measure.Report($"Neo Sampler steal: largest step {step / own:F3}× the old note's own (the new note's is {ownHigh / own:F2}×)");
                Assert.That(step, Is.LessThan(1.05 * ownHigh));
                Assert.That(e.ActiveVoices, Is.EqualTo(1));
                Assert.That(Frequency(y, at + 1000, y.Length), Is.EqualTo(441 * Math.Pow(2, 7 / 12.0)).Within(0.5));
            }
        }

        [Test]
        public void VoicesStealTheQuietestReleasedFirstAndKeepThePolyphony()
        {
            var (e, v, bank) = Make(v => { v.Set(P.Polyphony, 4); v.Set(Z(0, SamplerEngine.FLoop), 2); Set(v, P.AmpR, 2.0); });
            bank.Set(1, Slot("sine", Mono(SineWave(441, Rate))));
            for (int k = 0; k < 4; k++) e.NoteOn(k, 60 + k, 1f);
            RunL(e, 4800);
            e.NoteOff(2);                                   // released: the first to go
            RunL(e, 4800);
            e.NoteOn(9, 72, 1f);
            RunL(e, 4800);
            var keys = Enumerable.Range(0, SamplerEngine.VoiceCount).Select(i => e.Voice(i)).Where(x => x.IsActive).Select(x => x.Key).OrderBy(k => k).ToArray();
            Assert.That(keys, Is.EqualTo(new[] { 0, 1, 3, 9 }));
        }

        [Test]
        public void VelocityLayersSwitchOrCrossfadeAtEqualPower()
        {
            // Zone 1: velocities 1–64 (a sine on FFT bin 601, 440 Hz), zone 2: 65–127 (bin 903, 661 Hz); each a wave loop of exact cycles.
            void Layers(ParamValues v, int crossfade)
            {
                v.Set(Z(0, SamplerEngine.FVelHi), 64);
                v.Set(Z(1, SamplerEngine.FWave), 2);
                v.Set(Z(1, SamplerEngine.FVelLo), 65);
                v.Set(P.VelXfade, crossfade);
            }
            (double Soft, double Hard) Play(int crossfade, int velocity)
            {
                var (e, v, bank) = Make(v => Layers(v, crossfade));
                bank.Set(1, Slot("soft", Mono(SineWave(Measure.BinFrequency(601), 2 * Measure.N), loopStart: Measure.N, loopEnd: 2 * Measure.N, loop: WaveLoop.Forward)));
                bank.Set(2, Slot("hard", Mono(SineWave(Measure.BinFrequency(903), 2 * Measure.N), loopStart: Measure.N, loopEnd: 2 * Measure.N, loop: WaveLoop.Forward)));
                e.NoteOn(0, 60, velocity / 127f);
                var y = RunL(e, Measure.N + 4800);
                var mag = Measure.Spectrum(y, 4800);
                return (mag[601], mag[903]);
            }
            var soft = Play(0, 40);
            var hard = Play(0, 100);
            Assert.That(Measure.Db(soft.Hard / soft.Soft), Is.LessThan(-80), "velocity 40: the soft layer alone");
            Assert.That(Measure.Db(hard.Soft / hard.Hard), Is.LessThan(-80), "velocity 100: the hard layer alone");
            double worst = 0;
            var full = Play(0, 40).Soft;
            for (int vel = 50; vel <= 80; vel += 3)
            {
                var (s, h) = Play(32, vel);
                double power = (s * s + h * h) / (full * full);
                worst = Math.Max(worst, Math.Abs(10 * Math.Log10(power)));
            }
            var mid = Play(32, 64);
            Measure.Report($"Neo Sampler velocity crossfade over 32 steps: the two layers' power within {worst:F3} dB of one layer's across velocities 50–80; at 64 they are {Measure.Db(mid.Soft / mid.Hard):+0.00;-0.00} dB apart");
            Assert.That(worst, Is.LessThan(0.05));
        }

        [Test]
        public void ALayersRootPicksItsKeysAndLayersOfOneRootAlternate()
        {
            // A slot of layers at C2, C4 and C4 again (two hits): E3 plays C2's; C4 and up alternate the two at C4.
            var (e, v, bank) = Make(v => v.Set(Z(0, SamplerEngine.FTrig), 1));
            var c2 = Mono(SineWave(100, Rate / 4), root: 36);
            var c4a = Mono(SineWave(300, Rate / 4), root: 60);
            var c4b = Mono(SineWave(500, Rate / 4), root: 60);
            bank.Set(1, Slot("multi", c2, c4a, c4b));
            e.NoteOn(0, 52, 1f);
            Assert.That(e.Voice(0).Layer, Is.SameAs(c2));
            var picked = new List<SampleLayer>();
            for (int k = 0; k < 4; k++)
            {
                e.NoteOn(1, 62, 1f);
                picked.Add(Enumerable.Range(0, SamplerEngine.VoiceCount).Select(i => e.Voice(i)).Where(x => x.Key == 1).OrderBy(x => x.StartTime).Last().Layer);
                RunL(e, 256);
            }
            Assert.That(picked[0], Is.Not.SameAs(picked[1]));
            Assert.That(picked[0], Is.SameAs(picked[2]));
            Assert.That(picked.Distinct().Count(), Is.EqualTo(2));
            Assert.That(picked.All(l => l.Root == 60), Is.True);
        }

        [Test]
        public void KitModePutsZoneNOnKeyBasePlusNOneShotAtItsOwnPitchWithItsPanAndChoke()
        {
            var x = SineWave(441, Rate / 2, 0.5);
            var (e, v, bank) = Make(v =>
            {
                v.Set(P.Mode, 1);
                v.Set(Z(2, SamplerEngine.FWave), 1);                           // zone 3 → D2 (38)
                v.Set(Z(2, SamplerEngine.FPan), 0);                             // hard left
                v.Set(Z(2, SamplerEngine.FTune), 48 + 7);                       // a fifth up
            });
            bank.Set(1, Slot("sine", Mono(x, root: 72)));
            e.NoteOn(0, 37, 1f);
            Assert.That(e.ActiveVoices, Is.EqualTo(0), "zone 2 is off: nothing");
            var (l, r) = Run(e, Rate, events: new (int, Action)[] { (0, () => e.NoteOn(0, 38, 1f)), (2400, () => e.NoteOff(0)) });
            Assert.That(Frequency(l, 2400, 20000), Is.EqualTo(441 * Math.Pow(2, 7 / 12.0)).Within(0.2), "its own pitch, tuned, whatever the key");
            Assert.That(Measure.Peak(r, 0, r.Length), Is.EqualTo(0), "panned hard left");
            int last = Array.FindLastIndex(l, s => s != 0);
            Assert.That(last, Is.GreaterThan(x.Length / Math.Pow(2, 7 / 12.0) - 200), "one-shot: plays to its end through the note-off");
        }

        [Test]
        public void AMissingWavePlaysSilenceAndAReplacedOneKeepsPlaying()
        {
            // Empty slot, a built-in number with nothing there, a slot emptied while its note plays.
            var (e, v, bank) = Make(v =>
            {
                v.Set(Z(1, SamplerEngine.FWave), 7);
                v.Set(Z(2, SamplerEngine.FWave), 254);
            });
            Assert.DoesNotThrow(() => e.NoteOn(0, 60, 1f));
            var y = RunL(e, 4800);
            Assert.That(y.All(s => s == 0), Is.True);
            bank.Set(1, Slot("sine", Mono(SineWave(441, Rate, 0.5))));
            e.NoteOn(1, 60, 1f);
            RunL(e, 4800);
            bank.Set(1, null);
            var z = RunL(e, 4800);
            Assert.That(Measure.Rms(z, 0, z.Length), Is.GreaterThan(0.3), "the voice keeps the layer it started with");
            e.NoteOn(2, 60, 1f);
            Assert.That(e.ActiveVoices, Is.EqualTo(1), "a new note of the emptied slot: nothing more");
        }

        [Test]
        public void PitchBendBendsByItsRange()
        {
            var (e, v, _) = Make(v => { v.Set(Z(0, SamplerEngine.FWave), 216); v.Set(P.BendRange, 7); });
            e.NoteOn(0, 69, 1f);
            e.PitchBend(1f);
            var y = RunL(e, Rate);
            Assert.That(Measure.Cents(Frequency(y, Rate / 10, Rate), 440), Is.EqualTo(700).Within(0.05));
        }

        [Test]
        public void VelocitySetsLevelAndFilter()
        {
            double Level(int vel, Action<ParamValues> setup)
            {
                var (e, v, _) = Make(v => { v.Set(Z(0, SamplerEngine.FWave), 217); setup(v); });
                e.NoteOn(0, 48, vel / 127f);
                var y = RunL(e, Rate / 2);
                return Measure.Rms(y, Rate / 4, Rate / 4);
            }
            double loud = Level(127, v => v.Set(P.VelAmp, 1000)), half = Level(64, v => v.Set(P.VelAmp, 1000));
            Assert.That(Measure.Db(half / loud), Is.EqualTo(40 * Math.Log10(64 / 127.0)).Within(0.05), "Vel → amp 100 %: velocity squared");
            double open = Level(127, v => { v.Set(P.FType, 1); Set(v, P.FCutoff, 1000); v.Set(P.FVel, 1000); });
            double closed = Level(20, v => { v.Set(P.FType, 1); Set(v, P.FCutoff, 1000); v.Set(P.FVel, 1000); });
            Assert.That(closed, Is.LessThan(open * 0.7), "softer notes close the filter");
        }

        // ------------------------------------------------------------------ robustness

        [Test]
        public void NonFiniteSamplesAndExtremeSettingsStayFiniteAndBounded()
        {
            var bad = SineWave(441, Rate / 2);
            for (int i = 0; i < bad.Length; i += 97) bad[i] = i % 3 == 0 ? float.NaN : i % 3 == 1 ? float.PositiveInfinity : float.NegativeInfinity;
            var rng = new Random(5);
            var (e, v, bank) = Make();
            bank.Set(1, Slot("bad", Mono(bad)));
            bank.Set(2, Slot("stereo", SampleLayer.Build("s", SineWave(300, Rate), SineWave(301, Rate), 44100, 50, 100, 40000, WaveLoop.PingPong)));
            double peak = 0;
            for (int round = 0; round < 30; round++)
            {
                foreach (var d in NeoBuzz.Sampler.Params.Table)
                {
                    int raw = rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) };
                    if (d.Index >= P.Z1Wave && (d.Index - P.Z1Wave) % SamplerEngine.ZoneStride == SamplerEngine.FWave) raw = rng.Next(4) switch { 0 => 1, 1 => 2, 2 => 200 + rng.Next(1, 27), _ => rng.Next(0, 255) };
                    v.Set(d.Index, raw);
                }
                v.Set(P.Volume, 600);
                for (int k = 0; k < 6; k++) e.NoteOn(k, rng.Next(0, 128), (float)rng.NextDouble());
                e.PitchBend((float)(rng.NextDouble() * 2 - 1));
                var (l, r) = Run(e, 9600);
                Assert.That(l.All(float.IsFinite) && r.All(float.IsFinite), Is.True, $"round {round}");
                peak = Math.Max(peak, Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length)));
                if (round % 5 == 4) e.AllNotesOff(round % 10 == 9);
            }
            Measure.Report($"Neo Sampler, every parameter at random ends and NaN and ±∞ in a wave, six notes a round: finite, peak {Measure.Db(peak):+0.0;-0.0} dBFS");
            Assert.That(Measure.Db(peak), Is.LessThan(48));
        }

        [Test]
        public void SilenceAfterResonanceHasNoSubnormalsAndGoesIdle()
        {
            var (e, v, _) = Make(v =>
            {
                v.Set(Z(0, SamplerEngine.FWave), 226);                          // looped noise
                v.Set(P.FType, 1);
                Set(v, P.FCutoff, 200.0);
                v.Set(P.FReso, 950);
                Set(v, P.AmpR, 0.2);
            });
            var y = RunL(e, 3 * Rate, events: new (int, Action)[] { (0, () => e.NoteOn(0, 60, 1f)), (Rate, () => e.NoteOff(0)) });
            int subnormal = y.Count(s => s != 0 && Math.Abs(s) < 1.18e-38f);
            Assert.That(subnormal, Is.EqualTo(0));
            Assert.That(e.IsActive, Is.False, "idle after the release");
            var bl = new float[256];
            Assert.That(e.Process(bl, bl, 256), Is.False, "and says so");
        }

        [Test]
        public void BlockSizesGiveTheSameOutput()
        {
            (float[] L, float[] R) Play(int block)
            {
                var (e, v, bank) = Make(v =>
                {
                    v.Set(Z(0, SamplerEngine.FWave), 221);                       // stereo strings, its wave loop
                    v.Set(Z(0, SamplerEngine.FKeyHi), 59);
                    v.Set(Z(1, SamplerEngine.FWave), 1);
                    v.Set(Z(1, SamplerEngine.FKeyLo), 60);
                    v.Set(Z(1, SamplerEngine.FLoop), 2);
                    Set(v, Z(1, SamplerEngine.FLoopS), 30.0);
                    Set(v, Z(1, SamplerEngine.FLoopE), 70.0);
                    v.Set(Z(1, SamplerEngine.FChoke), 2);
                    v.Set(P.FType, 2);
                    v.Set(P.FReso, 500);
                    Set(v, P.FEnv, 0.5);
                    v.Set(P.Polyphony, 3);
                    v.Set(P.VelAmp, 700);
                });
                bank.Set(1, Slot("noise", Mono(Measure.PinkNoise(Rate, 0.2, 3), root: 64, rate: 44100)));
                return Run(e, 2 * Rate, block,
                    (0, () => e.NoteOn(0, 48, 0.8f)),
                    (1000, () => e.NoteOn(1, 55, 0.5f)),
                    (7777, () => e.NoteOn(2, 62, 1f)),
                    (9001, () => e.PitchBend(0.3f)),
                    (12000, () => { e.NoteOn(3, 66, 0.7f); Set(v, Z(1, SamplerEngine.FPan), -40.0); }),
                    (20000, () => e.NoteOn(4, 52, 0.9f)),
                    (30011, () => { e.NoteOff(0); Set(v, P.FCutoff, 900.0); }),
                    (50000, () => e.NoteOff(2)),
                    (70000, () => e.AllNotesOff(false)));
            }
            var a = Play(256);
            var b = Play(117);
            var c = Play(16);
            Assert.That(b.L, Is.EqualTo(a.L));
            Assert.That(b.R, Is.EqualTo(a.R));
            Assert.That(c.L, Is.EqualTo(a.L));
            Assert.That(Measure.Rms(a.L, 0, a.L.Length), Is.GreaterThan(0.01));
        }

        [Test]
        public void TheAudioPathAllocatesNothing()
        {
            var (e, v, bank) = Make(v =>
            {
                v.Set(Z(0, SamplerEngine.FWave), 221);
                v.Set(Z(1, SamplerEngine.FWave), 1);
                v.Set(Z(1, SamplerEngine.FChoke), 1);
                v.Set(Z(1, SamplerEngine.FLoop), 3);
                v.Set(Z(2, SamplerEngine.FWave), 203);
                v.Set(Z(2, SamplerEngine.FChoke), 1);
                v.Set(P.FType, 1);
                v.Set(P.Polyphony, 8);
            });
            bank.Set(1, Slot("sine", Mono(SineWave(441, Rate))));
            var l = new float[SamplerEngine.MaxBlock];
            var r = new float[SamplerEngine.MaxBlock];
            int n = 0;
            void Block(int b)
            {
                if (b % 7 == 0) e.NoteOn(b % 16, 36 + (b * 5) % 60, 0.3f + (b % 5) * 0.15f);
                if (b % 11 == 0) e.NoteOff((b + 3) % 16);
                if (b % 13 == 0) v.Set(Z(0, SamplerEngine.FTune), 40 + b % 17);
                if (b % 17 == 0) e.PitchBend((b % 9) / 4.5f - 1);
                e.Process(l, r, SamplerEngine.MaxBlock);
                n++;
            }
            for (int b = 0; b < 200; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, Block);
            Measure.Report($"Neo Sampler: {bytes} bytes allocated over 2000 blocks (notes, steals, chokes, tune and bend moving)");
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void ThirtyTwoVoicesRunFasterThanRealTime()
        {
            foreach (var (wave, filter, label) in new[] { (221, 1, "stereo strings, ladder LP24"), (219, 0, "mono E-piano, no filter") })
            {
                var (e, v, _) = Make(v =>
                {
                    v.Set(Z(0, SamplerEngine.FWave), wave);
                    v.Set(Z(0, SamplerEngine.FLoop), 2);
                    v.Set(Z(0, SamplerEngine.FLoopS), 30000);
                    v.Set(Z(0, SamplerEngine.FLoopE), 50000);
                    v.Set(P.FType, filter);
                    v.Set(P.FCutoff, 700);
                    v.Set(P.Volume, 300);
                });
                for (int k = 0; k < 32; k++) e.NoteOn(k, 36 + 2 * k + k % 3, 1f);
                RunL(e, Rate);
                Assert.That(e.ActiveVoices, Is.EqualTo(32));
                var sw = Stopwatch.StartNew();
                RunL(e, 10 * Rate);
                double factor = 10 / sw.Elapsed.TotalSeconds;
                Measure.Report($"Neo Sampler, 32 voices ({label}, every pitch 1–4 octaves around the roots): {factor:F1}× real time");
                Assert.That(factor, Is.GreaterThan(3));
            }
        }
    }
}
