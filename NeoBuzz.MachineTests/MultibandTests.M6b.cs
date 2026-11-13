using NeoBuzz.Dsp;
using NeoBuzz.Multiband;
using BP = NeoBuzz.Multiband.MultibandEngine.BandParam;
using XP = NeoBuzz.Multiband.MultibandEngine.ExtraParam;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Multiband's M6b extensions (docs/specs/mastering-tools.md §5.14): lookahead, the Dynamic phase, upward compression, the RMS detector and the external sidechain.</summary>
    public partial class MultibandTests
    {
        /// <summary>The bands these tests use: below 400 Hz, to 2.5 kHz, to 8 kHz, above (a sine at 100 Hz, 1 kHz, 5 kHz or 12 kHz is in band 1, 2, 3 or 4).</summary>
        static readonly Action<ParamValues> Layout = Crossovers(400, 2500, 8000);

        static int X(int band, XP what) => MultibandEngine.ExtParam(band, what);

        /// <summary>Like <see cref="Run"/>, with the sidechain on input 2 (null: nothing connected).</summary>
        static (float[] L, float[] R) RunKey(MultibandEngine e, float[] l, float[] r, float[] kl, float[] kr, int block = MultibandEngine.MaxBlock, Action<int> before = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[MultibandEngine.MaxBlock];
            var br = new float[MultibandEngine.MaxBlock];
            var cl = new float[MultibandEngine.MaxBlock];
            var cr = new float[MultibandEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                before?.Invoke(pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                if (kl != null) Array.Copy(kl, pos, cl, 0, n);
                if (kr != null) Array.Copy(kr, pos, cr, 0, n);
                e.Process(bl, br, kl != null ? cl : null, kr != null ? cr : null, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        /// <summary>The level (dB) of a steady sine through the engine, measured over its last half second.</summary>
        static double SineLevel(Action<ParamValues> setup, double hz, double dbfs, PhaseMode phase, float[] key = null, double seconds = 2)
        {
            var (e, _) = Make(Both(Layout, setup), phase: phase);
            int n = (int)(seconds * Rate);
            var x = Measure.Sine(hz, n, DspMath.DbToGain(dbfs));
            var (l, _) = RunKey(e, x, x, key, key);
            return Db(Measure.Rms(l, n - Rate / 2, Rate / 2) / Measure.Rms(x, n - Rate / 2, Rate / 2));
        }

        // ------------------------------------------------------------------ lookahead

        [TestCase(0, 48000, 4607)]
        [TestCase(1, 48000, 0)]
        [TestCase(2, 48000, 0)]
        [TestCase(0, 44100, 4607)]
        [TestCase(1, 96000, 0)]
        public void TheLookaheadJoinsTheLatencyAndIsReported(int phaseMode, int rate, int baseLatency)
        {
            var phase = (PhaseMode)phaseMode;
            foreach (double ms in new[] { 0.0, 0.5, 3.0, 10.0 })
            {
                var (e, _) = Make(v => Set(v, P.Lookahead, ms), rate, phase);
                int look = (int)Math.Round(ms * 0.001 * rate);
                Assert.That(e.Latency, Is.EqualTo(baseLatency + look), $"{ms} ms at {rate}");
                // The readings report it too, once a step has been taken.
                var x = Noise(rate / 2, 0.1, 3);
                Run(e, x, x);
                Assert.That(e.Readings()["latency_samples"], Is.EqualTo(baseLatency + look));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void TheLookaheadDelaysTheWholeSignalByExactlyItsSamples(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            // With every band at 1:1 the output is the dry: with a lookahead it is the same samples later, bit for bit.
            var x = Noise(2 * Rate, 0.2, 5);
            var y = Noise(2 * Rate, 0.2, 6);
            var (e0, _) = Make(Unity, phase: phase);
            var (e1, _) = Make(Both(Unity, v => Set(v, P.Lookahead, 5)), phase: phase);
            var a = Run(e0, x, y);
            var b = Run(e1, x, y);
            int look = e1.Lookahead;
            Assert.That(look, Is.EqualTo((int)Math.Round(0.005 * Rate)));
            for (int i = 0; i + look < x.Length; i++)
            {
                if (Bits(b.L[i + look]) != Bits(a.L[i]) || Bits(b.R[i + look]) != Bits(a.R[i]))
                {
                    Assert.Fail($"{phase}: sample {i} is not the lookahead-free sample {look} earlier");
                }
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void TheDetectorSeesTheOnsetBeforeItArrivesAtTheOutput(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            // A 1 kHz burst at −6 dBFS after a second of silence, band 2 at ∞:1 from −30 dB with a 3 ms attack: without a lookahead the
            // first two milliseconds at the output are nearly untouched; with 10 ms the gain is down before the burst arrives.
            Action<ParamValues> band2 = v =>
            {
                Layout(v);
                Unity(v);
                Set(v, B(1, BP.Threshold), -30);
                v.Set(B(1, BP.Ratio), Params.Table[B(1, BP.Ratio)].Max);
                Set(v, B(1, BP.Knee), 0);
                Set(v, B(1, BP.Attack), 0.003);
                Set(v, B(1, BP.Release), 0.5);
            };
            var x = new float[3 * Rate];
            var burst = Measure.Sine(1000, Rate, DspMath.DbToGain(-6));
            Array.Copy(burst, 0, x, Rate, Rate / 2);
            (double First, double Steady) Peaks(double lookMs)
            {
                var (e, _) = Make(Both(band2, v => Set(v, P.Lookahead, lookMs)), phase: phase);
                var (l, _) = Run(e, x, x);
                int at = Rate + e.Latency;
                return (Measure.Peak(l, at, Rate / 500), Measure.Peak(l, at + Rate / 4, Rate / 100));
            }
            var plain = Peaks(0);
            var ahead = Peaks(10);
            Measure.Report($"Neo Multiband {phase}: the burst's first 2 ms peak at {Db(plain.First):F1} dBFS without lookahead, {Db(ahead.First):F1} dBFS with 10 ms (the burst later: {Db(ahead.Steady):F1} dBFS)");
            Assert.That(Db(plain.First), Is.GreaterThan(Db(plain.Steady) + 6), "without lookahead the onset gets through");
            // Dynamic phase adds the band to the input, and the band's onset lags the input's: the first ms isn't cancelled as in the others.
            if (phase == PhaseMode.Dynamic) Assert.That(Db(ahead.First), Is.LessThanOrEqualTo(Db(plain.First)));
            else Assert.That(Db(ahead.First), Is.LessThan(Db(plain.First) - 6), "with it the gain is down at the onset");
        }

        [TestCase(0)]
        [TestCase(1)]
        public void ALookaheadChangeFadesAndTheLatencyFollowsAtTheSwitch(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            var x = Measure.Sine(1000, 3 * Rate, 0.5);
            var (e, v) = Make(Unity, phase: phase);
            int baseLatency = e.Latency;
            var seen = new List<int>();
            var (l, _) = Run(e, x, x, before: pos =>
            {
                if (pos == 29952) Set(v, P.Lookahead, 8);
                if (pos == 89856) Set(v, P.Lookahead, 2);
                if (pos == 119808) Set(v, P.Lookahead, 0);
                seen.Add(e.Latency);
            });
            Assert.That(seen.Distinct().Select(s => s - baseLatency), Is.EquivalentTo(new[] { 0, 384, 96 }), "each setting's latency, nothing between");
            Assert.That(e.Latency, Is.EqualTo(baseLatency));
            double step = Math.Abs(0.5 * 2 * Math.PI * 1000 / Rate);
            Assert.That(MaxStep(l, 2 * baseLatency + 4800), Is.LessThan(step * 1.2), "no click: fades, never a jump");
            Assert.That(l.Skip(Rate * 5 / 2).All(float.IsFinite), Is.True);
            Assert.That(Measure.Rms(l, Rate * 51 / 20 + baseLatency, Rate / 5), Is.EqualTo(Measure.Rms(x, 0, Rate / 5)).Within(1e-4), "and back to the signal");
        }

        [Test]
        public void ThePartialSwitchKeepsTheLinearPathsAudioInFlight()
        {
            // Only the lookahead changes: no reset of the linear path, so the signal is back after the lookahead's own samples and the
            // fades, not the 96 ms the phase switch holds.
            var x = Measure.Sine(1000, 3 * Rate, 0.5);
            var (e, v) = Make(Unity, phase: PhaseMode.Linear);
            var (l, _) = Run(e, x, x, before: pos => { if (pos == Rate) Set(v, P.Lookahead, 4); });
            int silent = 0;
            for (int i = Rate; i < 2 * Rate; i++) { if (Math.Abs(l[i]) < 1e-4) silent++; }
            Measure.Report($"Neo Multiband: a lookahead change in linear phase leaves {silent} samples under −80 dBFS in a 1 kHz sine's second");
            Assert.That(silent, Is.LessThan(Rate / 10), "well under the 4607-sample hold of a phase switch plus its fades");
        }

        // ------------------------------------------------------------------ dynamic phase

        [Test]
        public void DynamicPhaseAtUnityIsTheInputBitForBitWithNoLatency()
        {
            var x = Noise(Rate, 0.3, 7);
            var y = Noise(Rate, 0.3, 8);
            var (e, _) = Make(Unity, phase: PhaseMode.Dynamic);
            Assert.That(e.Latency, Is.EqualTo(0));
            var (l, r) = Run(e, x, y);
            for (int i = 0; i < x.Length; i++)
            {
                Assert.That(Bits(l[i]), Is.EqualTo(Bits(x[i])), $"left {i}");
                Assert.That(Bits(r[i]), Is.EqualTo(Bits(y[i])), $"right {i}");
            }
        }

        [Test]
        public void DynamicPhaseChangesOnlyWhereABandChanges()
        {
            // Band 3 (2.5 … 8 kHz) cut 12 dB by its makeup: a 100 Hz sine, far from it, comes out as it went in (the difference under
            // −45 dB), where minimum phase has the crossovers' allpass shift; a 5 kHz sine in the band is cut 12 dB.
            Action<ParamValues> cut = v => { Layout(v); Unity(v); Set(v, B(2, BP.Makeup), -12); };
            double Residual(PhaseMode phase)
            {
                var (e, _) = Make(cut, phase: phase);
                var x = Measure.Sine(100, 2 * Rate, 0.5);
                var (l, _) = Run(e, x, x);
                var d = new float[x.Length];
                for (int i = 0; i < d.Length; i++) d[i] = l[i] - x[i];
                return Db(Measure.Rms(d, Rate, Rate / 2) / Measure.Rms(x, Rate, Rate / 2));
            }
            double dynamic = Residual(PhaseMode.Dynamic), minimum = Residual(PhaseMode.Minimum);
            double inBand = SineLevel(cut, 5000, -12, PhaseMode.Dynamic);
            Measure.Report($"Neo Multiband Dynamic: a 100 Hz sine differs from the input by {dynamic:F1} dB (minimum phase {minimum:F1} dB); a 5 kHz sine in the cut band {inBand:F2} dB");
            Assert.That(dynamic, Is.LessThan(-45));
            Assert.That(minimum, Is.GreaterThan(-12));
            Assert.That(inBand, Is.InRange(-12.5, -6), "cut, by less than the setting: the band is not in phase with the input");
        }

        [Test]
        public void DynamicPhaseCompressesWithinMinimumPhasesDepth()
        {
            // Band 2 at ∞:1 from −20 dB on a 1 kHz sine at −6 dBFS: minimum phase takes the band's whole change, Dynamic the part of it that is in phase with the input.
            var setup = OneBand(1, -20, double.PositiveInfinity, release: 0.5);
            double minimum = SineLevel(setup, 1000, -6, PhaseMode.Minimum), dynamic = SineLevel(setup, 1000, -6, PhaseMode.Dynamic);
            Measure.Report($"Neo Multiband band 2 ∞:1 from −20 dB, a 1 kHz sine at −6 dBFS: minimum phase {minimum:F2} dB, dynamic {dynamic:F2} dB");
            Assert.That(dynamic, Is.LessThan(-5), "compresses");
            Assert.That(dynamic, Is.GreaterThanOrEqualTo(minimum - 0.5), "by no more than minimum phase does: a band's change reaches the output through its phase against the input");
        }

        [Test]
        public void DynamicPhaseSoloHearsTheBandAlone()
        {
            Action<ParamValues> solo = v => { Unity(v); v.Set(B(1, BP.Solo), 1); };
            double inBand = SineLevel(solo, 1000, -12, PhaseMode.Dynamic);
            double below = SineLevel(solo, 100, -12, PhaseMode.Dynamic);
            Measure.Report($"Neo Multiband Dynamic, band 2 soloed: 1 kHz {inBand:F2} dB, 100 Hz {below:F1} dB");
            Assert.That(inBand, Is.EqualTo(0).Within(1.0));
            Assert.That(below, Is.LessThan(-40));
        }

        // ------------------------------------------------------------------ upward compression

        static Action<ParamValues> Upward(int b, double threshold, double ratio, double knee = 0, double range = 24, double attack = 0.001, double release = 0.2) => v =>
        {
            Unity(v);
            Set(v, B(b, BP.Threshold), threshold);
            Set(v, B(b, BP.Knee), knee);
            Set(v, B(b, BP.Range), range);
            Set(v, B(b, BP.Attack), attack);
            Set(v, B(b, BP.Release), release);
            Set(v, X(b, XP.UpRatio), ratio);
        };

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void UpwardCompressionRaisesWhatIsUnderTheThresholdAsTheCurve(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            // Band 1 from −30 dB at 4:1: a 100 Hz sine at −50 dBFS gains (−30 + 50)(1 − 1/4) = 15 dB; at −40 dBFS 7.5 dB; above the
            // threshold nothing; under −80 dBFS nothing is lifted (the floor).
            var setup = Upward(0, -30, 4);
            double a = SineLevel(setup, 100, -50, phase), b = SineLevel(setup, 100, -40, phase), c = SineLevel(setup, 100, -20, phase), d = SineLevel(setup, 100, -88, phase);
            Measure.Report($"Neo Multiband {phase} upward 4:1 from −30 dB: −50 dBFS {a:+0.00;-0.00} dB (15), −40 dBFS {b:+0.00;-0.00} dB (7.5), −20 dBFS {c:+0.00;-0.00} dB (0), −88 dBFS {d:+0.00;-0.00} dB (0, the floor)");
            Assert.That(a, Is.EqualTo(15).Within(0.6));
            Assert.That(b, Is.EqualTo(7.5).Within(0.6));
            Assert.That(c, Is.EqualTo(0).Within(0.05));
            Assert.That(d, Is.EqualTo(0).Within(0.05));
        }

        [Test]
        public void UpwardCompressionIsCappedByRangeAndSoftenedByTheKnee()
        {
            double capped = SineLevel(Upward(0, -30, 8, range: 6), 100, -55, PhaseMode.Minimum);
            double hard = SineLevel(Upward(0, -30, 4, knee: 0), 100, -32, PhaseMode.Minimum);
            double soft = SineLevel(Upward(0, -30, 4, knee: 12), 100, -32, PhaseMode.Minimum);
            // At −32 dB (2 dB under) a 12 dB knee has raised (2 + 6)² × 0.75 / 24 = 2 dB, a hard knee 1.5 dB.
            Measure.Report($"Neo Multiband upward: range 6 dB caps the lift at {capped:F2} dB; 2 dB under the threshold the hard knee lifts {hard:F2} dB, the 12 dB knee {soft:F2} dB");
            Assert.That(capped, Is.EqualTo(6).Within(0.2));
            Assert.That(hard, Is.InRange(1.3, 2.0), "about 0.75 × 2 dB (a peak detector reads a sine's troughs low, so a little more)");
            Assert.That(soft, Is.GreaterThan(hard + 0.2), "the knee starts the lift above the threshold's line");
        }

        [Test]
        public void UpwardCompressionLetsGoFastWhenTheLevelRisesAndRisesAtTheReleaseWhenItFalls()
        {
            // 100 Hz at −50 dBFS for 1 s (lifted ~15 dB), then −10 dBFS: the lift is gone within 5 attack times (a 2 ms attack).
            var (e, _) = Make(Both(Layout, Upward(0, -30, 4, attack: 0.002, release: 0.2)), phase: PhaseMode.Minimum);
            var x = Measure.Sine(100, 3 * Rate, 1);
            for (int i = 0; i < x.Length; i++) x[i] *= (float)DspMath.DbToGain(i < Rate * 3 / 2 ? -50 : -10);
            var (l, _) = Run(e, x, x);
            double after = Db(Measure.Rms(l, Rate * 3 / 2 + Rate / 20, Rate / 20) / Measure.Rms(x, Rate * 3 / 2 + Rate / 20, Rate / 20));
            double before = Db(Measure.Rms(l, Rate, Rate / 4) / Measure.Rms(x, Rate, Rate / 4));
            Measure.Report($"Neo Multiband upward: lift {before:F1} dB, {after:F2} dB 50 ms after the level rises 40 dB");
            Assert.That(before, Is.GreaterThan(12));
            Assert.That(Math.Abs(after), Is.LessThan(0.5));
        }

        [Test]
        public void UpwardCompressionLeavesNoGainAfterSilenceSoTheMachineRests()
        {
            var (e, _) = Make(Both(Layout, Upward(0, -30, 8)));
            var noise = Noise(Rate, 0.01, 17);
            Run(e, noise, noise);
            Run(e, new float[10 * Rate], new float[10 * Rate]);
            Assert.That(e.Idle, Is.True);
        }

        // ------------------------------------------------------------------ RMS detector

        [TestCase(0)]
        [TestCase(1)]
        public void TheRmsDetectorReadsASineOnThePeakScale(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            // A 1 kHz sine at −12 dBFS in band 2: both detectors read it at −12 dBFS (the RMS on the peak scale).
            double Level(bool rms)
            {
                var (e, _) = Make(Both(Layout, v => v.Set(P.Detector, rms ? 1 : 0)), phase: phase);
                var x = Measure.Sine(1000, 2 * Rate, DspMath.DbToGain(-12));
                for (int i = 0; i < Rate / 5; i++) x[i] *= (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / (Rate / 5)));   // no overshoot at the onset
                Run(e, x, x);
                return e.Readings()["b2_level_db"];
            }
            double peak = Level(false), mean = Level(true);
            Measure.Report($"Neo Multiband {phase}: a 1 kHz sine at −12 dBFS reads {peak:F2} dBFS with the peak detector, {mean:F2} dBFS with RMS");
            Assert.That(peak, Is.EqualTo(-12.44).Within(0.1), "−12 dBFS less the band's −0.44 dB at 1 kHz");
            Assert.That(mean, Is.EqualTo(peak).Within(0.3));
        }

        [Test]
        public void TheRmsDetectorIgnoresAClickThePeakDetectorSees()
        {
            var x = new float[2 * Rate];
            x[Rate] = 0.9f;
            double Level(bool rms)
            {
                var (e, _) = Make(v => v.Set(P.Detector, rms ? 1 : 0), phase: PhaseMode.Minimum);
                Run(e, x, x);
                return e.Readings()["b4_level_db"];
            }
            double peak = Level(false), mean = Level(true);
            Measure.Report($"Neo Multiband: a single-sample click at 0.9 reads {peak:F1} dBFS on band 4 with the peak detector, {mean:F1} dBFS with RMS");
            Assert.That(mean, Is.LessThan(peak - 8));
        }

        // ------------------------------------------------------------------ external sidechain

        [TestCase(1)]
        [TestCase(2)]
        public void ASidechainThatIsTheInputIsTheInternalDetectorBitForBit(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            var x = Noise(2 * Rate, 0.3, 21);
            var y = Noise(2 * Rate, 0.3, 22);
            Action<ParamValues> comp = v => { for (int b = 0; b < 4; b++) { Unity(v); } Set(v, B(0, BP.Threshold), -30); Set(v, B(0, BP.Ratio), 6); Set(v, B(2, BP.Threshold), -34); Set(v, B(2, BP.Ratio), 4); };
            var (e0, _) = Make(comp, phase: phase);
            var (e1, _) = Make(Both(comp, v => { v.Set(X(0, XP.Key), 1); v.Set(X(2, XP.Key), 1); }), phase: phase);
            var a = RunKey(e0, x, y, null, null);
            var b = RunKey(e1, x, y, x, y);
            Assert.That(Enumerable.Range(0, x.Length).Count(i => Bits(a.L[i]) != Bits(b.L[i]) || Bits(a.R[i]) != Bits(b.R[i])), Is.EqualTo(0), "the same bits");
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void AKeyBurstCompressesTheSignalWhileItLastsAndLinesUpWithIt(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            // The signal: a steady 1 kHz sine at −20 dBFS (band 2). The key: the same tone at −6 dBFS for 0.3 s from 1.0 s. Band 2 listens
            // to the key at ∞:1 from −20 dB (a 14 dB cut). The cut starts where the burst reaches the output, the latency later:
            // in linear phase that is 4607 samples, which the key's own bands are held back by.
            var (e, _) = Make(Both(Layout, OneBand(1, -20, double.PositiveInfinity, release: 0.02, attack: 0.0001), v => v.Set(X(1, XP.Key), 1)), phase: phase);
            int n = 3 * Rate;
            var x = Measure.Sine(1000, n, DspMath.DbToGain(-20));
            var key = new float[n];
            Array.Copy(Measure.Sine(1000, Rate * 3 / 10, DspMath.DbToGain(-6)), 0, key, Rate, Rate * 3 / 10);
            var (l, _) = RunKey(e, x, x, key, key);
            int latency = e.Latency;
            double Level(int from, int length) => Db(Measure.Rms(l, from, length) / Measure.Rms(x, 0, length));
            double before = Level(Rate / 2 + latency, Rate / 4), during = Level(Rate + latency + Rate / 20, Rate / 5), after = Level(Rate * 3 / 2 + latency, Rate / 4);
            // Where the output's 10 ms envelope first falls 6 dB under the steady level.
            int w = Rate / 1000, start = -1, end = -1;
            double steady = Measure.Rms(x, 0, Rate / 10);
            for (int i = Rate / 2 + latency; i + w < n; i += w / 4)
            {
                bool low = Measure.Rms(l, i, w) < steady * 0.5;
                if (low && start < 0) start = i;
                if (!low && start >= 0 && end < 0) { end = i; break; }
            }
            Measure.Report($"Neo Multiband {phase} external key: before {before:F2} dB, during the burst {during:F1} dB, after {after:F2} dB; the cut runs {start - latency - Rate} … {end - latency - (Rate + Rate * 3 / 10)} samples from the burst's start … end (latency {latency})");
            Assert.That(Math.Abs(before), Is.LessThan(0.3), "no key: no cut");
            if (phase == PhaseMode.Dynamic) Assert.That(during, Is.InRange(-16, -5));
            else Assert.That(during, Is.InRange(-16, -9));
            Assert.That(Math.Abs(after), Is.LessThan(0.3), "released");
            Assert.That(start - latency - Rate, Is.InRange(-48, 48), "the cut starts with the burst, at the output");
            Assert.That(end - latency - (Rate + Rate * 3 / 10), Is.InRange(-48, 1500), "and ends with it (the release)");
        }

        [Test]
        public void ABandOnTheExternalKeyIgnoresItsOwnLevelAndOthersKeepTheirs()
        {
            // Band 2 external with no key connected hears silence: nothing is cut however loud the signal; band 1 on its own signal does.
            Action<ParamValues> setup = v =>
            {
                Unity(v);
                for (int b = 0; b < 2; b++)
                {
                    Set(v, B(b, BP.Threshold), -30);
                    v.Set(B(b, BP.Ratio), Params.Table[B(b, BP.Ratio)].Max);
                }
                v.Set(X(1, XP.Key), 1);
            };
            double band1 = SineLevel(setup, 100, -6, PhaseMode.Minimum);
            double band2 = SineLevel(setup, 1000, -6, PhaseMode.Minimum);
            Measure.Report($"Neo Multiband band 2 on a missing key: a 1 kHz sine at −6 dBFS {band2:F2} dB; band 1 on its own signal, 100 Hz: {band1:F1} dB");
            Assert.That(Math.Abs(band2), Is.LessThan(0.05));
            Assert.That(band1, Is.LessThan(-18));
        }

        [Test]
        public void TheKeyHearsOnlyItsBand()
        {
            // Band 2 external, keyed by 100 Hz (band 1's range) at −6 dBFS: it is outside band 2, so band 2 isn't cut; keyed by 1 kHz it is.
            Action<ParamValues> setup = v =>
            {
                Unity(v);
                Set(v, B(1, BP.Threshold), -30);
                v.Set(B(1, BP.Ratio), Params.Table[B(1, BP.Ratio)].Max);
                v.Set(X(1, XP.Key), 1);
            };
            double wrong = SineLevel(setup, 1000, -12, PhaseMode.Minimum, key: Measure.Sine(100, 2 * Rate, DspMath.DbToGain(-6)));
            double right = SineLevel(setup, 1000, -12, PhaseMode.Minimum, key: Measure.Sine(1000, 2 * Rate, DspMath.DbToGain(-6)));
            Measure.Report($"Neo Multiband band 2 keyed by 100 Hz: {wrong:F2} dB; keyed by 1 kHz: {right:F1} dB");
            Assert.That(Math.Abs(wrong), Is.LessThan(0.5));
            Assert.That(right, Is.LessThan(-15));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void NewOptionsKeepTheOutputIndependentOfHowTheHostSplitsIt(int startMode)
        {
            var start = (PhaseMode)startMode;
            // Blocks of 256, 128 and 117 with the key, lookahead, RMS and upward compression switching meanwhile.
            var x = Noise(4 * Rate, 0.2, 31);
            var y = Noise(4 * Rate, 0.2, 32);
            var k = Noise(4 * Rate, 0.3, 33);
            (float[] L, float[] R) Render(int block)
            {
                var (e, v) = Make(Both(Unity, w => { Set(w, B(0, BP.Threshold), -30); Set(w, B(0, BP.Ratio), 4); Set(w, B(1, BP.Threshold), -30); Set(w, B(1, BP.Ratio), 3); }), phase: start);
                return RunKey(e, x, y, k, k, block, pos =>
                {
                    if (pos == 29952) v.Set(X(1, XP.Key), 1);
                    if (pos == 59904) Set(v, P.Lookahead, 4);
                    if (pos == 89856) v.Set(P.Detector, 1);
                    if (pos == 119808) Set(v, X(0, XP.UpRatio), 3);
                    if (pos == 149760) v.Set(P.Phase, (int)start == 0 ? 2 : 0);
                    if (pos == 179712) { Set(v, P.Lookahead, 0); v.Set(X(0, XP.Key), 1); }
                });
            }
            var a = Render(256);
            foreach (int block in new[] { 128, 117 })
            {
                var b = Render(block);
                for (int i = 0; i < x.Length; i++)
                {
                    Assert.That(Bits(b.L[i]), Is.EqualTo(Bits(a.L[i])), $"blocks of {block}, left {i}");
                    Assert.That(Bits(b.R[i]), Is.EqualTo(Bits(a.R[i])), $"blocks of {block}, right {i}");
                }
            }
        }

        [Test]
        public void NanAndInfinityOnTheSidechainAreSilence()
        {
            var (e, _) = Make(v => { v.Set(X(0, XP.Key), 1); v.Set(X(1, XP.Key), 1); Set(v, B(0, BP.Threshold), -40); Set(v, X(0, XP.UpRatio), 4); }, phase: PhaseMode.Linear);
            var x = Noise(2 * Rate, 0.1, 13);
            var k = Noise(2 * Rate, 0.1, 14);
            for (int i = Rate / 2; i < Rate / 2 + 100; i++) k[i] = float.NaN;
            for (int i = Rate; i < Rate + 100; i++) k[i] = (i & 1) == 0 ? float.PositiveInfinity : float.NegativeInfinity;
            var (l, r) = RunKey(e, x, x, k, k);
            Assert.That(e.HoldsNonFinite(), Is.False);
            Assert.That(l.Concat(r).All(float.IsFinite), Is.True);
        }

        [Test]
        public void TheNewOptionsAllocateNothing()
        {
            // 2000 blocks with the key, lookahead, detector, upward ratio, Phase (all three), Bands and solos moving: no allocation.
            var (e, v) = Make(null);
            var l = new float[MultibandEngine.MaxBlock];
            var r = new float[MultibandEngine.MaxBlock];
            var k = new float[MultibandEngine.MaxBlock];
            var rng = new Random(2);
            void Block(int b)
            {
                for (int i = 0; i < l.Length; i++)
                {
                    l[i] = r[i] = (float)Math.Sin(i * 0.05 + b) * 0.3f;
                    k[i] = (float)Math.Sin(i * 0.11 + b) * 0.4f;
                }
                if (b % 3 == 0) v.Set(P.Xo1 + rng.Next(3), rng.Next(10001));
                if (b % 40 == 5) v.Set(X(rng.Next(4), XP.Key), rng.Next(2));
                if (b % 40 == 6) v.Set(X(rng.Next(4), XP.UpRatio), rng.Next(1001));
                if (b % 90 == 7) v.Set(P.Lookahead, rng.Next(1001));
                if (b % 60 == 8) v.Set(P.Detector, rng.Next(2));
                if (b % 50 == 9) v.Set(P.Bands, rng.Next(2));
                if (b % 120 == 11) v.Set(P.Phase, rng.Next(3));
                if (b % 30 == 13) v.Set(B(rng.Next(4), BP.Solo), rng.Next(2));
                e.Process(l, r, b % 7 == 0 ? null : k, b % 7 == 0 ? null : k, l.Length);
            }
            for (int b = 0; b < 300; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, Block);
            Measure.Report($"Neo Multiband M6b: {bytes} bytes allocated over 2000 blocks with the key, lookahead, detector, upward ratio, Phase, Bands and solos moving");
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void CpuOfTheNewOptionsIsSmall()
        {
            // Stereo at 48 kHz on noise (0.5 s untimed, then 2 s): lookahead 5 ms, the RMS detector with upward compression, the external
            // sidechain in linear and in minimum phase, the Dynamic phase.
            var x = Noise(3 * Rate, 0.2, 4);
            var k = Noise(3 * Rate, 0.2, 5);
            const int Warm = Rate / 2;
            Action<ParamValues> keyed = v => { for (int b = 0; b < 4; b++) v.Set(X(b, XP.Key), 1); };
            var cases = new (string Name, Action<ParamValues> Setup, PhaseMode Phase, double Floor)[]
            {
                ("linear, lookahead 5 ms", v => Set(v, P.Lookahead, 5), PhaseMode.Linear, Throughput.Floor(x64: 46, arm64: 30)),
                ("minimum, RMS and upward on every band", v => { v.Set(P.Detector, 1); for (int b = 0; b < 4; b++) Set(v, X(b, XP.UpRatio), 3); }, PhaseMode.Minimum, Throughput.Floor(x64: 50, arm64: 38)),
                ("linear, external sidechain on 4 bands", keyed, PhaseMode.Linear, Throughput.Floor(x64: 38, arm64: 26)),
                ("minimum, external sidechain on 4 bands", keyed, PhaseMode.Minimum, Throughput.Floor(x64: 40, arm64: 30)),
                ("dynamic", null, PhaseMode.Dynamic, Throughput.Floor(x64: 54, arm64: 40)),
            };
            foreach (var c in cases)
            {
                Throughput.Guard($"Neo Multiband CPU ({c.Name})", c.Floor, 2, Rate, MultibandEngine.MaxBlock, () =>
                {
                    var (e, _) = Make(c.Setup, phase: c.Phase);
                    var bl = new float[MultibandEngine.MaxBlock];
                    var br = new float[MultibandEngine.MaxBlock];
                    var kl = new float[MultibandEngine.MaxBlock];
                    void Block(int pos, int n)
                    {
                        Array.Copy(x, pos, bl, 0, n);
                        Array.Copy(x, pos, br, 0, n);
                        Array.Copy(k, pos, kl, 0, n);
                        e.Process(bl, br, kl, kl, n);
                    }
                    for (int pos = 0; pos < Warm; pos += MultibandEngine.MaxBlock) Block(pos, MultibandEngine.MaxBlock);
                    return Throughput.InBlocks(MultibandEngine.MaxBlock, (pos, n) => Block(Warm + pos, n));
                }, Measure.Report);
            }
        }
    }
}
