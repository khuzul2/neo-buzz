using NeoBuzz.Dsp;
using NeoBuzz.LoFi;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Lo-Fi (docs/specs/neo-lofi.md): the acceptance gates, measured, with signals made in code.</summary>
    [TestFixture]
    public class LoFiTests
    {
        const int Rate = Measure.Rate;

        static (LoFiEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new LoFiEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        /// <summary>Every stage off: the machine's neutral state.</summary>
        static Action<ParamValues> AllOff(Action<ParamValues> then = null) => v =>
        {
            Set(v, P.Bits, 24);
            v.Set(P.Rate, Params.Table[P.Rate].Max);
            then?.Invoke(v);
        };

        static (float[] L, float[] R) Run(LoFiEngine e, float[] l, float[] r, int block = LoFiEngine.MaxBlock)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[LoFiEngine.MaxBlock];
            var br = new float[LoFiEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static float[] Mono(LoFiEngine e, float[] x) => Run(e, x, (float[])x.Clone()).L;

        static double Amplitude(float[] x, double hz, int from, int n)
        {
            double a = 0, b = 0;
            for (int i = 0; i < n; i++)
            {
                double ph = 2 * Math.PI * hz * (from + i) / Rate;
                a += x[from + i] * Math.Cos(ph);
                b += x[from + i] * Math.Sin(ph);
            }
            return 2 * Math.Sqrt(a * a + b * b) / n;
        }

        static double RmsDb(float[] x, int from, int n) => 20 * Math.Log10(Math.Max(1e-12, Measure.Rms(x, from, n)));

        // ---- bypass and latency ------------------------------------------------------------------------

        [TestCase(256)]
        [TestCase(100)]
        [TestCase(37)]
        public void AllStagesOffPassTheInputBitForBit(int block)
        {
            var l = Measure.PinkNoise(Rate * 2, 0.2, 3);
            var r = Measure.PinkNoise(Rate * 2, 0.2, 4);
            var (e, _) = Make(AllOff());
            var (ol, or) = Run(e, l, r, block);
            for (int i = 0; i < l.Length; i++)
                if (ol[i] != l[i] || or[i] != r[i]) Assert.Fail($"differs at {i}");
            Assert.That(e.Idle, Is.True);
        }

        [Test]
        public void MixZeroPassesTheInputBitForBitWithEveryStageOn()
        {
            var l = Measure.PinkNoise(Rate * 2, 0.2, 5);
            var r = Measure.PinkNoise(Rate * 2, 0.2, 6);
            var (e, _) = Make(v =>
            {
                Set(v, P.Bits, 5); Set(v, P.Rate, 6000); v.Set(P.AntiAlias, 1); v.Set(P.Dither, 1); Set(v, P.Jitter, 0.5);
                Set(v, P.Hiss, -40); Set(v, P.Crackle, 30); Set(v, P.Wow, 30); Set(v, P.Flutter, 20); Set(v, P.Drift, 10); Set(v, P.Tone, -0.5);
                Set(v, P.Mix, 0);
            });
            var (ol, or) = Run(e, l, r);
            // The mix glides from its stored value only when it changes: here it starts at 0.
            for (int i = 0; i < l.Length; i++)
                if (ol[i] != l[i] || or[i] != r[i]) Assert.Fail($"differs at {i}");
        }

        [Test]
        public void MixGlidingToZeroEndsExactlyAtTheInput()
        {
            var l = Measure.PinkNoise(Rate * 2, 0.2, 7);
            var (e, v) = Make(x => { Set(x, P.Bits, 6); Set(x, P.Hiss, -50); });
            var bl = new float[256];
            var br = new float[256];
            var o = new float[l.Length];
            for (int pos = 0; pos < l.Length; pos += 256)
            {
                if (pos == 256 * 10) Set(v, P.Mix, 0);
                Array.Copy(l, pos, bl, 0, 256);
                Array.Copy(l, pos, br, 0, 256);
                e.Process(bl, br, 256);
                Array.Copy(bl, 0, o, pos, 256);
            }
            for (int i = Rate; i < l.Length; i++)
                if (o[i] != l[i]) Assert.Fail($"differs at {i}");
        }

        [Test]
        public void TheLatencyIsZeroChecksWithAnImpulse()
        {
            Assert.That(LoFiEngine.Latency, Is.EqualTo(0));
            foreach (var (name, setup) in new (string, Action<ParamValues>)[]
            {
                ("all off", AllOff()),
                ("bits only", AllOff(v => Set(v, P.Bits, 8))),
                ("rate 24 kHz", AllOff(v => Set(v, P.Rate, 24000))),
                ("wow 20 ct", AllOff(v => Set(v, P.Wow, 20))),
                ("everything but Tone", AllOff(v => { Set(v, P.Bits, 10); Set(v, P.Wow, 10); Set(v, P.Flutter, 5); Set(v, P.Drift, 5); })),
            })
            {
                var x = new float[4800];
                x[100] = 0.5f;
                var (e, _) = Make(setup);
                var y = Mono(e, x);
                int peak = 0;
                for (int i = 1; i < y.Length; i++) if (Math.Abs(y[i]) > Math.Abs(y[peak])) peak = i;
                Assert.That(peak, Is.EqualTo(100), name);
                Assert.That(y[100], Is.GreaterThan(0.45f).And.LessThan(0.55f), name);
                for (int i = 0; i < 100; i++) Assert.That(y[i], Is.EqualTo(0f), $"{name}: nothing before the impulse (at {i})");
            }
        }

        [Test]
        public void TheOutputBlockSizeDoesNotChangeASteadySetting()
        {
            var l = Measure.PinkNoise(Rate, 0.2, 8);
            Action<ParamValues> all = v =>
            {
                Set(v, P.Bits, 7.5); Set(v, P.Rate, 9000); v.Set(P.AntiAlias, 1); Set(v, P.Jitter, 0.3); v.Set(P.Dither, 1);
                Set(v, P.Hiss, -50); v.Set(P.HissFollow, 1); Set(v, P.Crackle, 20); Set(v, P.Wow, 10); Set(v, P.Flutter, 5); Set(v, P.Drift, 4); Set(v, P.Tone, 0.4);
            };
            var (e1, _) = Make(all);
            var (e2, _) = Make(all);
            var a = Mono(e1, l);
            var b = Run(e2, l, (float[])l.Clone(), 37).L;
            double worst = 0;
            for (int i = 0; i < l.Length; i++) worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
            Assert.That(worst, Is.LessThan(1e-6));
        }

        // ---- bits --------------------------------------------------------------------------------------

        static float[] FullScaleRamp(int n)
        {
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = (float)(-1.0 + 2.0 * i / (n - 1));
            return x;
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(8)]
        [TestCase(12)]
        public void AnIntegerDepthTakesExactlyTwoToTheNLevels(int bits)
        {
            foreach (int dither in new[] { 0, 1 })
            {
                var (e, _) = Make(v => { AllOff()(v); Set(v, P.Bits, bits); v.Set(P.Dither, dither); });
                var y = Mono(e, FullScaleRamp(1 << 18));
                var levels = new SortedSet<float>(y);
                Assert.That(levels.Count, Is.EqualTo(1 << bits), $"{bits} bits, dither {dither}");
                double step = Math.Pow(2, 1 - bits);
                foreach (float lv in levels)
                    Assert.That(Math.Abs(lv / step - Math.Round(lv / step)), Is.LessThan(1e-6), $"{lv} is on the grid");
                Assert.That(levels.Min, Is.EqualTo(-1f));
                Assert.That(levels.Max, Is.EqualTo((float)(1 - step)).Within(1e-6));
            }
        }

        [Test]
        public void AFractionalDepthSetsTheStepSize()
        {
            // 4.5 bits: a step of 2^(1-4.5) = 0.0884, about 22.6 levels over the full range.
            var (e, _) = Make(v => { AllOff()(v); Set(v, P.Bits, 4.5); });
            var y = Mono(e, FullScaleRamp(1 << 18));
            var levels = new SortedSet<float>(y).ToArray();
            double step = Math.Pow(2, 1 - 4.5);
            Assert.That(levels.Length, Is.InRange(22, 25));
            var diffs = new List<double>();
            for (int i = 1; i < levels.Length - 1; i++) diffs.Add(levels[i] - levels[i - 1]);
            Assert.That(diffs.Min(), Is.EqualTo(step).Within(2e-4));
            Assert.That(diffs.Max(), Is.EqualTo(step).Within(2e-4));
        }

        [Test]
        public void DitherTurnsTheErrorIntoNoiseThatKeepsAQuietToneAlive()
        {
            // A sine of a quarter of a step: without dither it quantises to silence; with TPDF dither it survives, on average.
            var x = Measure.Sine(1000, Rate * 2, 0.25 * Math.Pow(2, 1 - 6));
            var (off, _) = Make(v => { AllOff()(v); Set(v, P.Bits, 6); });
            var (on, _) = Make(v => { AllOff()(v); Set(v, P.Bits, 6); v.Set(P.Dither, 1); });
            double a0 = Amplitude(Mono(off, x), 1000, 0, Rate);
            double a1 = Amplitude(Mono(on, x), 1000, 0, Rate);
            Measure.Report($"Neo Lo-Fi 6 bits, a tone at a quarter of a step: without dither {a0:0.00000}, with {a1:0.00000} (the input {0.25 * Math.Pow(2, -5):0.00000})");
            Assert.That(a0, Is.EqualTo(0).Within(1e-9));
            Assert.That(a1, Is.EqualTo(0.25 * Math.Pow(2, -5)).Within(0.25 * 0.2 * Math.Pow(2, -5)));
        }

        // ---- rate --------------------------------------------------------------------------------------

        [Test]
        public void TheHoldTakesASampleEveryPeriod()
        {
            var x = SlowRamp(Rate);
            var (e, _) = Make(v => { AllOff()(v); Set(v, P.Rate, 6000); });
            var y = Mono(e, x);
            int changes = 0;
            for (int i = 1; i < y.Length; i++) if (y[i] != y[i - 1]) changes++;
            Assert.That(changes, Is.EqualTo(Rate / 8).Within(Rate / 8 * 0.01 + 2), "a hold of 8 samples (the rate is set in 0.7 % steps)");
            int run = 1, longest = 0;
            for (int i = 1; i < y.Length; i++) { if (y[i] == y[i - 1]) run++; else { longest = Math.Max(longest, run); run = 1; } }
            Assert.That(longest, Is.InRange(8, 9), "held for a period");
            // The held value is the input at the moment of the tick.
            int tick = -1;
            for (int i = 1000; i < 1100; i++) if (y[i] != y[i - 1]) { tick = i; break; }
            Assert.That(y[tick], Is.EqualTo(x[tick]).Within(1e-6));
        }

        /// <summary>A slow rising ramp: every hold shows as a change of the output, and only a hold does.</summary>
        static float[] SlowRamp(int n)
        {
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = i * 3.5e-6f;
            return x;
        }

        [TestCase(200.0)]
        [TestCase(1000.0)]
        [TestCase(12345.0)]
        public void TheHoldRateIsTheSettingEvenWhenItIsNotAnIntegerPeriod(double hz)
        {
            var x = SlowRamp(Rate * 4);
            var (e, _) = Make(v => { AllOff()(v); Set(v, P.Rate, hz); });
            var y = Mono(e, x);
            int changes = 0;
            for (int i = Rate / 2; i < y.Length; i++) if (y[i] != y[i - 1]) changes++;
            double measured = changes / 3.5;
            Assert.That(measured, Is.EqualTo(hz).Within(0.03 * hz + 2), $"{hz} Hz");
        }

        [Test]
        public void JitterSpreadsTheHoldTimesAroundTheSameRate()
        {
            var x = SlowRamp(Rate * 4);
            var (clean, _) = Make(v => { AllOff()(v); Set(v, P.Rate, 4000); });
            var (shaky, _) = Make(v => { AllOff()(v); Set(v, P.Rate, 4000); Set(v, P.Jitter, 1); });
            double Sd(float[] y, out double mean)
            {
                var runs = new List<int>();
                int last = 0;
                for (int i = 1; i < y.Length; i++) if (y[i] != y[i - 1]) { runs.Add(i - last); last = i; }
                double m = runs.Average();
                mean = m;
                return Math.Sqrt(runs.Select(r => (r - m) * (r - m)).Average());
            }
            double sd0 = Sd(Mono(clean, x), out double m0);
            double sd1 = Sd(Mono(shaky, x), out double m1);
            Assert.That(m0, Is.EqualTo(12).Within(0.05));
            Assert.That(sd0, Is.LessThan(0.5), "a period of 12.02 samples: runs of 12 and 13");
            Assert.That(m1, Is.EqualTo(12).Within(0.4));
            Assert.That(sd1, Is.GreaterThan(0.8));
        }

        [Test]
        public void WithoutAntiAliasAToneAboveTheNewNyquistAliasesAndWithItTheAliasIsSixtyDecibelsLower()
        {
            // A hold of 8 kHz (new Nyquist 4 kHz); a tone at 6 kHz (1.5 times the Nyquist) folds to 8 - 6 = 2 kHz.
            var x = Measure.Sine(6000, Rate * 2, 0.5);
            var (off, _) = Make(v => { AllOff()(v); Set(v, P.Rate, 8000); });
            var (on, _) = Make(v => { AllOff()(v); Set(v, P.Rate, 8000); v.Set(P.AntiAlias, 1); });
            var yOff = Mono(off, x);
            var yOn = Mono(on, x);
            double aOff = Amplitude(yOff, 2000, Rate / 2, Rate);
            double aOn = Amplitude(yOn, 2000, Rate / 2, Rate);
            double drop = Measure.Db(aOff / Math.Max(1e-12, aOn));
            Measure.Report($"Neo Lo-Fi alias at 2 kHz (a 6 kHz tone held at 8 kHz): anti-alias off {Measure.Db(aOff):0.0} dB, on {Measure.Db(aOn):0.0} dB: {drop:0.0} dB lower");
            Assert.That(Measure.Db(aOff), Is.GreaterThan(-60), "the alias is measurable");
            Assert.That(drop, Is.GreaterThanOrEqualTo(60));
        }

        [Test]
        public void WithAntiAliasEverythingAboveOneAndAHalfTimesTheNewNyquistLeavesNoEnergyBelowIt()
        {
            // Noise made of nothing below 6 kHz, held at 8 kHz: what lands below the new Nyquist is all alias.
            var white = new float[Rate * 3];
            var rng = new Rng(77);
            for (int i = 0; i < white.Length; i++) white[i] = 0.3f * rng.NextBipolar();
            var hp = new Biquad[8];
            for (int k = 0; k < hp.Length; k++) hp[k].Set(BiquadType.HighPass, 6500, Rate, 0.7071);
            for (int i = 0; i < white.Length; i++)
            {
                float s = white[i];
                for (int k = 0; k < hp.Length; k++) s = hp[k].Process(s);
                white[i] = s;
            }
            var (off, _) = Make(v => { AllOff()(v); Set(v, P.Rate, 8000); });
            var (on, _) = Make(v => { AllOff()(v); Set(v, P.Rate, 8000); v.Set(P.AntiAlias, 1); });
            double Band(float[] y)
            {
                var mag = Measure.Spectrum(y, Rate, Measure.N);
                double sum = 0;
                for (int k = (int)(100.0 * Measure.N / Rate); k < (int)(3600.0 * Measure.N / Rate); k++) sum += mag[k] * mag[k];
                return 10 * Math.Log10(Math.Max(1e-30, sum));
            }
            double eOff = Band(Mono(off, white)), eOn = Band(Mono(on, white));
            Measure.Report($"Neo Lo-Fi alias band 100 Hz - 3.6 kHz for noise above 6.5 kHz held at 8 kHz: off {eOff:0.0} dB, on {eOn:0.0} dB: {eOff - eOn:0.0} dB lower");
            Assert.That(eOff - eOn, Is.GreaterThanOrEqualTo(60));
        }

        [Test]
        public void TheAntiAliasFilterPassesTheBandBelowItsCornerFlat()
        {
            var x = Measure.Sine(1000, Rate * 2, 0.5);
            var (on, _) = Make(v => { AllOff()(v); Set(v, P.Rate, 8000); v.Set(P.AntiAlias, 1); });
            double a = Amplitude(Mono(on, x), 1000, Rate / 2, Rate);
            // The hold's own sinc droop at 1 kHz of an 8 kHz hold is -0.1 dB; the filter adds nothing measurable.
            Assert.That(Measure.Db(a / 0.5), Is.EqualTo(-0.1).Within(0.4));
        }

        [Test]
        public void ARateAtOrAboveTheHostsIsOffAndPassesTheInput()
        {
            var l = Measure.PinkNoise(Rate, 0.2, 9);
            foreach (double hz in new[] { 96000.0, 192000 })
            {
                var (e, _) = Make(v => { AllOff()(v); Set(v, P.Rate, hz); });
                var y = Mono(e, l);
                for (int i = 0; i < l.Length; i++) if (y[i] != l[i]) Assert.Fail($"{hz} Hz differs at {i}");
            }
        }

        // ---- noise -------------------------------------------------------------------------------------

        [TestCase(-80.0)]
        [TestCase(-60.0)]
        [TestCase(-40.0)]
        [TestCase(-20.0)]
        public void TheHissSitsWithinOneDecibelOfItsSetting(double db)
        {
            // Over silence, after the crusher (at 8 bits, so the crusher cannot be what makes it) and through a flat tone control.
            var (e, _) = Make(AllOff(v => { Set(v, P.Hiss, db); Set(v, P.Bits, 24); }));
            var y = Mono(e, new float[Rate * 3]);
            double measured = RmsDb(y, Rate, Rate * 2);
            Measure.Report($"Neo Lo-Fi hiss set {db} dB: measured {measured:0.00} dB");
            Assert.That(measured, Is.EqualTo(db).Within(1.0));
            var (e2, _) = Make(v => { Set(v, P.Hiss, db); Set(v, P.Bits, 8); });
            var y2 = Mono(e2, new float[Rate * 3]);
            Assert.That(RmsDb(y2, Rate, Rate * 2), Is.EqualTo(db).Within(1.0), "after the crusher: it is not crushed");
        }

        [Test]
        public void TheFollowingHissIsSilentOverSilenceAndAtItsSettingOverALoudInput()
        {
            var (e, _) = Make(AllOff(v => { Set(v, P.Hiss, -40); v.Set(P.HissFollow, 1); }));
            var silent = Mono(e, new float[Rate]);
            Assert.That(silent.All(s => s == 0f), Is.True, "gated: nothing plays, nothing hisses");
            var tone = Measure.Sine(220, Rate * 3, 0.5);
            var (e2, _) = Make(AllOff(v => { Set(v, P.Hiss, -40); v.Set(P.HissFollow, 1); }));
            var y = Mono(e2, tone);
            var diff = new float[y.Length];
            for (int i = 0; i < y.Length; i++) diff[i] = y[i] - tone[i];
            Assert.That(RmsDb(diff, Rate, Rate * 2), Is.EqualTo(-40).Within(1.0));
            // A quieter input (-30 dBFS peak) hisses 10 dB less than at -20 dBFS and above.
            var quiet = Measure.Sine(220, Rate * 3, 0.0316);
            var (e3, _) = Make(AllOff(v => { Set(v, P.Hiss, -40); v.Set(P.HissFollow, 1); }));
            var q = Mono(e3, quiet);
            for (int i = 0; i < q.Length; i++) q[i] -= quiet[i];
            Assert.That(RmsDb(q, Rate, Rate * 2), Is.LessThan(-47).And.GreaterThan(-60), "follows the level of the input");
        }

        /// <summary>The pops in a crackle-only render: rising crossings of a threshold, re-armed once the pop has fallen below half of it.</summary>
        static int CountPops(float[] y, double threshold)
        {
            int count = 0;
            bool armed = true;
            foreach (float s in y)
            {
                double a = Math.Abs(s);
                if (armed && a > threshold) { count++; armed = false; }
                else if (!armed && a < threshold / 2) armed = true;
            }
            return count;
        }

        [TestCase(5.0)]
        [TestCase(20.0)]
        [TestCase(60.0)]
        public void TheCrackleCountPerSecondIsWithinTwentyPercentOfTheSetting(double perSecond)
        {
            const double levelDb = -20;
            var (e, _) = Make(AllOff(v => { Set(v, P.Crackle, perSecond); Set(v, P.CrackleLevel, levelDb); }));
            var y = Mono(e, new float[Rate * 20]);
            double level = Math.Pow(10, levelDb / 20);
            double perSec = CountPops(y, 0.25 * level) / 20.0;
            Measure.Report($"Neo Lo-Fi crackle set {perSecond}/s: counted {perSec:0.00}/s");
            Assert.That(perSec, Is.EqualTo(perSecond).Within(0.2 * perSecond));
            Assert.That(Measure.Peak(y, 0, y.Length), Is.LessThanOrEqualTo(level * 1.5).And.GreaterThan(level * 0.6));
        }

        [Test]
        public void ACrackleOfZeroIsSilent()
        {
            var (e, _) = Make(AllOff(v => Set(v, P.CrackleLevel, -6)));
            Assert.That(Mono(e, new float[Rate]).All(s => s == 0f), Is.True);
        }

        // ---- motion ------------------------------------------------------------------------------------

        /// <summary>Pitch deviation in cents of a tone through the engine, by the shared pitch tracker (one estimate per hop).</summary>
        static double[] Deviation(Action<ParamValues> setup, double hz, double seconds, double skip)
        {
            int n = (int)(seconds * Rate);
            var x = Measure.Sine(hz, n, 0.3);
            var (e, _) = Make(AllOff(setup));
            var y = Mono(e, x);
            var tracker = new PitchTracker(Rate, PitchRange.Voice);
            var frames = new PitchEstimate[tracker.MaxFrames(n)];
            int count = tracker.Process(y, 0, n, frames, 0);
            var cents = new List<double>();
            for (int i = 0; i < count; i++)
                if (frames[i].Voiced && frames[i].EndSample > (long)(skip * Rate)) cents.Add(1200 * Math.Log2(frames[i].F0 / hz));
            return cents.ToArray();
        }

        [TestCase(20.0, 1.0)]
        [TestCase(50.0, 0.5)]
        [TestCase(30.0, 2.0)]
        [TestCase(12.0, 3.0)]
        public void TheWowDepthMeasuredWithThePitchTrackerIsTheSettingWithinFifteenPercent(double cents, double hz)
        {
            var dev = Deviation(v => { Set(v, P.Wow, cents); Set(v, P.WowRate, hz); }, 330, 8, 1.5);
            Assert.That(dev.Length, Is.GreaterThan(500));
            double mean = dev.Average();
            double depth = (dev.Max() - dev.Min()) / 2;
            Measure.Report($"Neo Lo-Fi wow {cents} ct at {hz} Hz: measured peak deviation {depth:0.00} ct ({100 * (depth / cents - 1):+0.0;-0.0}%), mean {mean:0.00} ct");
            Assert.That(depth, Is.EqualTo(cents).Within(0.15 * cents));
        }

        [Test]
        public void WithMotionOffThePitchDoesNotMove()
        {
            var dev = Deviation(v => { Set(v, P.Bits, 24); }, 330, 3, 0.5);
            Assert.That(dev.Max() - dev.Min(), Is.LessThan(0.5));
        }

        [Test]
        public void FlutterAndDriftStayWithinTheirSettings()
        {
            var fl = Deviation(v => Set(v, P.Flutter, 30), 330, 6, 1);
            double flutter = Math.Max(Math.Abs(fl.Max()), Math.Abs(fl.Min()));
            var dr = Deviation(v => Set(v, P.Drift, 20), 330, 40, 1);
            double drift = Math.Max(Math.Abs(dr.Max()), Math.Abs(dr.Min()));
            Measure.Report($"Neo Lo-Fi flutter set 30 ct: tracker sees up to {flutter:0.0} ct (it smooths the fast part); drift set 20 ct: up to {drift:0.0} ct");
            Assert.That(flutter, Is.LessThanOrEqualTo(30 * 1.15).And.GreaterThan(5));
            Assert.That(drift, Is.LessThanOrEqualTo(20 * 1.15).And.GreaterThan(6));
        }

        // ---- tone, output ------------------------------------------------------------------------------

        [Test]
        public void TheToneTiltsDarkerToTheLeftAndBrighterToTheRight()
        {
            double Gain(double tone, double hz)
            {
                var (e, _) = Make(AllOff(v => Set(v, P.Tone, tone)));
                var y = Mono(e, Measure.Sine(hz, Rate * 2, 0.3));
                return Measure.Db(Amplitude(y, hz, Rate, Rate) / 0.3);
            }
            Assert.That(Gain(0, 8000), Is.EqualTo(0).Within(0.01));
            Assert.That(Gain(-1, 8000), Is.LessThan(-30));
            Assert.That(Gain(-1, 200), Is.EqualTo(0).Within(0.5));
            Assert.That(Gain(1, 10000), Is.EqualTo(9).Within(1.0));
            Assert.That(Gain(1, 200), Is.EqualTo(0).Within(0.5));
        }

        [Test]
        public void OutputIsAGainInDecibels()
        {
            var (e, _) = Make(AllOff(v => Set(v, P.Output, -12)));
            var y = Mono(e, Measure.Sine(1000, Rate * 2, 0.5));
            Assert.That(Measure.Db(Amplitude(y, 1000, Rate, Rate) / 0.5), Is.EqualTo(-12).Within(0.01));
        }

        // ---- hygiene -----------------------------------------------------------------------------------

        [Test]
        public void NothingIsAllocatedPerBlock()
        {
            var l = Measure.PinkNoise(256 * 8, 0.3, 5);
            var r = Measure.PinkNoise(256 * 8, 0.3, 6);
            var (e, v) = Make(x => { Set(x, P.Bits, 7); Set(x, P.Rate, 9000); x.Set(P.AntiAlias, 1); Set(x, P.Hiss, -50); Set(x, P.Crackle, 20); Set(x, P.Wow, 10); Set(x, P.Flutter, 5); Set(x, P.Drift, 5); Set(x, P.Tone, -0.3); });
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                if (b % 20 == 0) Set(v, P.Bits, 3 + (b / 20 % 8) * 3);
                if (b % 25 == 0) Set(v, P.Rate, 400 + (b / 25 % 6) * 3000);
                if (b % 45 == 0) v.Set(P.AntiAlias, (b / 45) % 2);
                if (b % 55 == 0) v.Set(P.Dither, (b / 55) % 2);
                if (b % 33 == 0) Set(v, P.Jitter, (b / 33 % 3) * 0.4);
                if (b % 40 == 0) Set(v, P.Hiss, -70 + (b / 40 % 4) * 10);
                if (b % 70 == 0) v.Set(P.HissFollow, (b / 70) % 2);
                if (b % 50 == 0) Set(v, P.Crackle, (b / 50 % 4) * 20);
                if (b % 60 == 0) Set(v, P.Wow, (b / 60) % 2 == 0 ? 20 : 0);
                if (b % 65 == 0) Set(v, P.Flutter, (b / 65) % 2 == 0 ? 10 : 0);
                if (b % 75 == 0) Set(v, P.Drift, (b / 75) % 2 == 0 ? 8 : 0);
                if (b % 35 == 0) Set(v, P.Tone, ((b / 35) % 5 - 2) * 0.4);
                if (b % 90 == 0) Set(v, P.Mix, (b / 90) % 2 == 0 ? 1 : 0.4);
                Array.Copy(l, (b % 8) * 256, bl, 0, 256);
                Array.Copy(r, (b % 8) * 256, br, 0, 256);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 60; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2400, b => Block(b + 60));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void NoDenormalsRingOutOfAnyStageAfterALoudBurst()
        {
            var x = new float[Rate * 10];
            var burst = Measure.PinkNoise(Rate / 2, 0.8, 11);
            Array.Copy(burst, x, burst.Length);
            var (e, _) = Make(v => { Set(v, P.Bits, 9); Set(v, P.Rate, 5000); v.Set(P.AntiAlias, 1); Set(v, P.Wow, 25); Set(v, P.Flutter, 10); Set(v, P.Drift, 6); Set(v, P.Tone, -0.8); });
            var y = Mono(e, x);
            int sub = 0;
            for (int i = 0; i < y.Length; i++) if (y[i] != 0f && Math.Abs(y[i]) < 1.1754944e-38f) sub++;
            Assert.That(sub, Is.EqualTo(0));
            var (e2, _) = Make(v => { Set(v, P.Bits, 24); Set(v, P.Tone, 0.9); Set(v, P.Hiss, -60); v.Set(P.HissFollow, 1); });
            var y2 = Mono(e2, x);
            sub = 0;
            for (int i = 0; i < y2.Length; i++) if (y2[i] != 0f && Math.Abs(y2[i]) < 1.1754944e-38f) sub++;
            Assert.That(sub, Is.EqualTo(0));
            Assert.That(y2.Skip(Rate * 8).All(s => s == 0f), Is.True, "after the burst, the gated hiss and the filters have fallen silent");
        }

        /// <summary>Energy above 2 kHz relative to a 220 Hz sine, dB (Hann-windowed spectrum of the last 65536 samples).</summary>
        static double ZipperDb(float[] y)
        {
            int n = Measure.N;
            var w = new float[n];
            int from = y.Length - n;
            for (int i = 0; i < n; i++) w[i] = y[from + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Measure.Spectrum(w, 0, n);
            int k220 = (int)Math.Round(220.0 * n / Rate), k2k = (int)Math.Round(2000.0 * n / Rate);
            double sine = 0, high = 0;
            for (int k = k220 - 40; k <= k220 + 40; k++) sine += mag[k] * mag[k];
            for (int k = k2k; k < mag.Length; k++) high += mag[k] * mag[k];
            return 10 * Math.Log10(Math.Max(1e-30, high / sine));
        }

        /// <summary>A 220 Hz sine through the machine while one parameter is set to a new value in a triangle every block (256 samples): what lands above 2 kHz is zipper.</summary>
        static double SweepZipperDb(int param, double lo, double hi, Action<ParamValues> setup = null)
        {
            const int n = Rate * 4;
            var x = Measure.Sine(220, n, 0.25);
            var (e, v) = Make(AllOff(setup));
            var y = new float[n];
            var bl = new float[256];
            var br = new float[256];
            for (int pos = 0; pos < n; pos += 256)
            {
                // A triangle, 0.9 s per cycle, one value per block.
                double ph = (pos / (double)Rate / 0.9) % 1.0;
                double tri = ph < 0.5 ? 2 * ph : 2 - 2 * ph;
                Set(v, param, lo + (hi - lo) * tri);
                Array.Copy(x, pos, bl, 0, 256);
                Array.Copy(x, pos, br, 0, 256);
                e.Process(bl, br, 256);
                Array.Copy(bl, 0, y, pos, 256);
            }
            return ZipperDb(y);
        }

        [TestCase("Output", P.Output, -12.0, 12.0)]
        [TestCase("Mix", P.Mix, 0.0, 1.0)]
        [TestCase("Tone", P.Tone, -1.0, 1.0)]
        [TestCase("Wow", P.Wow, 0.0, 40.0)]
        [TestCase("Wow rate", P.WowRate, 0.4, 5.0)]
        [TestCase("Flutter", P.Flutter, 0.0, 40.0)]
        [TestCase("Drift", P.Drift, 0.0, 20.0)]
        public void AParameterSweepUnderAutomationMakesNoZipperAboveMinusSixtyDecibels(string name, int param, double lo, double hi)
        {
            // Mix is swept against a wet signal that differs from the dry (a slow wow); the others have nothing else running.
            Action<ParamValues> setup = v =>
            {
                if (param == P.Mix) Set(v, P.Wow, 10);
                if (param == P.WowRate) Set(v, P.Wow, 25);
            };
            double zipper = SweepZipperDb(param, lo, hi, setup);
            Measure.Report($"Neo Lo-Fi {name} swept {lo} to {hi} per block: energy above 2 kHz {zipper:0.0} dB re the sine");
            Assert.That(zipper, Is.LessThan(-60));
        }

        [Test]
        public void SteppedControlsGlideInsteadOfJumping()
        {
            // Bits and Rate set to the other end of their range every 16 samples: what the crusher uses moves in small steps.
            var (e, v) = Make(AllOff());
            var l = new float[16];
            var r = new float[16];
            float lastBits = 24, lastLog = (float)Math.Log2(Rate);
            double worstBits = 0, worstLog = 0;
            for (int b = 0; b < 4000; b++)
            {
                Set(v, P.Bits, (b / 40) % 2 == 0 ? 3 : 20);
                Set(v, P.Rate, (b / 55) % 2 == 0 ? 500 : 20000);
                e.Process(l, r, 16);
                worstBits = Math.Max(worstBits, Math.Abs(e.CurrentBits - lastBits));
                worstLog = Math.Max(worstLog, Math.Abs(Math.Log2(e.CurrentRate) - lastLog));
                lastBits = e.CurrentBits;
                lastLog = (float)Math.Log2(e.CurrentRate);
            }
            Measure.Report($"Neo Lo-Fi largest change per 16 samples: {worstBits:0.000} bits, {worstLog:0.000} octaves");
            Assert.That(worstBits, Is.LessThan(0.5));
            Assert.That(worstLog, Is.LessThan(0.15));
        }
    }
}
