using NeoBuzz.Dsp;
using NeoBuzz.Prism;
using static NeoBuzz.MachineTests.PrismRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Prism (docs/specs/neo-prism.md): the acceptance gates, measured, with signals made in code.</summary>
    [TestFixture]
    public class NeoPrismTests
    {
        static float[] Silence(int n) => new float[n];

        // ================================================================================== transparency, Mix and Amount, latency

        [Test]
        public void WithEverySectionOffTheOutputIsTheInputDelayedByTheLatencyWithinMinus90Db([Values(1024, 2048, 4096, 8192)] int size)
        {
            var v = Fresh(p => Size(p, size));
            var e = Engine(v);
            int n = Rate * 3;
            var rng = new Rng(11);
            var l = new float[n]; var r = new float[n];
            var s = Sine(n, 1000, 0.4);
            for (int i = 0; i < n; i++) { l[i] = s[i] + 0.1f * rng.NextBipolar(); r[i] = 0.7f * s[i] - 0.1f * rng.NextBipolar(); }
            var (ol, or) = Render(e, l, r);
            int lat = e.Latency;
            Assert.That(lat, Is.EqualTo(size - 1));
            double err = 0, sig = 0;
            for (int i = lat; i < n; i++)
            {
                double a = ol[i] - l[i - lat], b = or[i] - r[i - lat];
                err += a * a + b * b;
                sig += (double)l[i - lat] * l[i - lat] + (double)r[i - lat] * r[i - lat];
            }
            double db = 10 * Math.Log10(err / sig);
            TestContext.Out.WriteLine($"{size}: reconstruction error {db:0.0} dB");
            Assert.That(db, Is.LessThan(-90));
        }

        [Test]
        public void TheReportedLatencyIsTheMeasuredDelayToTheSampleAtEveryMixAndAmount([Values(1024, 2048, 4096, 8192)] int size, [Values(100, 50, 0)] int mix, [Values(100, 30, 0)] int amount)
        {
            var v = Fresh(p => { Size(p, size); Phys(p, P.Mix, mix); Phys(p, P.Amount, amount); });
            var e = Engine(v);
            int n = Rate;
            var l = new float[n]; var r = new float[n];
            l[3000] = 0.9f; r[3000] = -0.6f;
            var (ol, or) = Render(e, l, r);
            int at = 0;
            for (int i = 0; i < n; i++) if (Math.Abs(ol[i]) > Math.Abs(ol[at])) at = i;
            Assert.That(e.Latency, Is.EqualTo(size - 1));
            Assert.That(at, Is.EqualTo(3000 + size - 1), "the impulse comes out exactly the reported latency late");
            Assert.That(ol[at], Is.EqualTo(0.9f).Within(1e-3));
            for (int i = 0; i < at - 2; i++) Assert.That(Math.Abs(ol[i]), Is.LessThan(2e-5), $"nothing before it (sample {i})");
        }

        [Test]
        public void AmountZeroPassesTheInputBitForBitDelayedByTheLatencyWithEverySectionOn([Values(1024, 4096, 8192)] int size)
        {
            var v = AllOn(size);
            Phys(v, P.Amount, 0);
            var e = Engine(v);
            var (l, r) = Music(Rate * 6);
            var (ol, or) = Render(e, l, r);
            int lat = e.Latency;
            for (int i = 0; i < lat; i++) Assert.That(ol[i], Is.EqualTo(0f), $"sample {i}");
            for (int i = lat; i < l.Length; i++)
            {
                if (ol[i] != l[i - lat] || or[i] != r[i - lat]) Assert.Fail($"sample {i}: {ol[i]} against {l[i - lat]}");
            }
        }

        [Test]
        public void MixZeroPassesTheInputBitForBitDelayedByTheLatencyWithEverySectionOn([Values(1024, 4096, 8192)] int size)
        {
            var v = AllOn(size);
            Phys(v, P.Mix, 0);
            var e = Engine(v);
            var (l, r) = Music(Rate * 6);
            var (ol, or) = Render(e, l, r);
            int lat = e.Latency;
            for (int i = lat; i < l.Length; i++)
            {
                if (ol[i] != l[i - lat] || or[i] != r[i - lat]) Assert.Fail($"sample {i}: {ol[i]} against {l[i - lat]}");
            }
        }

        [Test]
        public void AmountAtOneHundredPercentIsTheSettingsAsTheyAreAndScalesTheRest()
        {
            var v = AllOn();
            Phys(v, P.Semi, 5); Phys(v, P.Cents, 20); Phys(v, P.ScrAmount, 40); Phys(v, P.BlurTime, 600); Phys(v, P.Smear, 3);
            Phys(v, P.DelLow, 40); Phys(v, P.DelHigh, 250); Phys(v, P.DelFeedback, 50);
            var e = Engine(v);
            var x = Silence(256);
            var y = Silence(256);
            int[] scaled = { P.Semi, P.Cents, P.ScrAmount, P.BlurTime, P.Smear, P.DelLow, P.DelHigh, P.DelFeedback };
            e.Process(x, y, 256);
            foreach (int p in scaled) Assert.That(e.Effective.Value(p), Is.EqualTo(v.Value(p)).Within(1e-12), Params.Table[p].Name + " at 100 %");
            Phys(v, P.Amount, 200);
            e.Process(x, y, 256);
            Assert.That(e.Effective.Value(P.Semi), Is.EqualTo(10).Within(1e-9));
            Assert.That(e.Effective.Value(P.Cents), Is.EqualTo(40).Within(1e-9));
            Assert.That(e.Effective.Value(P.BlurTime), Is.EqualTo(1200).Within(5));
            Assert.That(e.Effective.Value(P.DelHigh), Is.EqualTo(500).Within(3));
            Assert.That(e.Effective.Value(P.DelFeedback), Is.EqualTo(99).Within(1e-9), "kept inside the parameter's range");
            Phys(v, P.Amount, 50);
            e.Process(x, y, 256);
            Assert.That(e.Effective.Value(P.Semi), Is.EqualTo(2.5).Within(1e-9));
            Assert.That(e.Effective.Value(P.DelLow), Is.EqualTo(20).Within(1));
        }

        [Test]
        public void AmountHalfMovesAnOctaveUpToATritone()
        {
            var v = Fresh(p => { Switch(p, P.PitchOn, true); Phys(p, P.Semi, 12); Phys(p, P.Amount, 50); });
            var x = Sine(Rate * 4, 440);
            var (l, _) = Render(Engine(v), x, x);
            double f = Frequency(l, Rate * 2, Rate * 4 - 2000);
            double c = Cents(f, 440 * Math.Pow(2, 0.5));
            TestContext.Out.WriteLine($"Amount 50 %, +12 st: {f:0.00} Hz ({c:0.00} cents from +6 st)");
            Assert.That(Math.Abs(c), Is.LessThan(2));
        }

        // ================================================================================== pitch

        [Test]
        public void ASineShiftedSevenUpAndTwelveDownReadsWithinTwoCentsAtEverySize([Values(1024, 2048, 4096, 8192)] int size, [Values(7, -12)] int semitones)
        {
            var v = Fresh(p => { Size(p, size); Switch(p, P.PitchOn, true); Phys(p, P.Semi, semitones); });
            var x = Sine(Rate * 4, 440);
            var (l, _) = Render(Engine(v), x, x);
            double f = Frequency(l, Rate * 2, Rate * 4 - 2000);
            double c = Cents(f, 440 * Math.Pow(2, semitones / 12.0));
            TestContext.Out.WriteLine($"{size}, {semitones:+0;-0} st: {f:0.000} Hz, {c:0.000} cents; level {RmsDb(l, Rate * 2, Rate * 4):0.0} dB (input {RmsDb(x, Rate * 2, Rate * 4):0.0})");
            Assert.That(Math.Abs(c), Is.LessThan(2));
            Assert.That(RmsDb(l, Rate * 2, Rate * 4), Is.EqualTo(RmsDb(x, Rate * 2, Rate * 4)).Within(0.5), "the level survives");
        }

        [Test]
        public void ACentsAddUpToTheSemitones()
        {
            var v = Fresh(p => { Switch(p, P.PitchOn, true); Phys(p, P.Semi, 3); Phys(p, P.Cents, -40); });
            var x = Sine(Rate * 4, 440);
            var (l, _) = Render(Engine(v), x, x);
            double c = Cents(Frequency(l, Rate * 2, Rate * 4 - 2000), 440 * Math.Pow(2, 2.6 / 12));
            Assert.That(Math.Abs(c), Is.LessThan(2));
        }

        [Test]
        public void ASustainedChordShiftedAnOctaveKeepsEveryPartialWithinTwoCentsAndAddsNoPeakAboveMinus40Db([Values(4096, 8192)] int size, [Values(12, 7, -12)] int semitones)
        {
            var v = Fresh(p => { Size(p, size); Switch(p, P.PitchOn, true); Phys(p, P.Semi, semitones); });
            int n = Rate * 5;
            var x = ChordSignal(n, Triad, 0.2);
            var (l, _) = Render(Engine(v), x, x);
            int N = 1 << 17;
            var m = HannSpectrum(l, n - N - 100, N);
            double max = m.Max();
            var want = Triad.Select(f => f * Math.Pow(2, semitones / 12.0)).ToArray();
            foreach (var w in want)
            {
                double f = PeakFrequency(m, (int)Math.Round(w * N / Rate), N);
                double c = Cents(f, w);
                TestContext.Out.WriteLine($"{size}, {semitones:+0;-0} st: partial {w:0.0} Hz reads {c:0.00} cents off");
                Assert.That(Math.Abs(c), Is.LessThan(2), $"partial {w:0.0} Hz");
            }
            double worst = -200;
            for (int k = 3; k < m.Length - 3; k++)
            {
                if (m[k] < m[k - 1] || m[k] <= m[k + 1]) continue;
                if (want.Any(w => Math.Abs(k - w * N / Rate) < 8)) continue;
                worst = Math.Max(worst, Measure.Db(m[k] / max));
            }
            TestContext.Out.WriteLine($"{size}, {semitones:+0;-0} st: the strongest new peak is {worst:0.0} dB below the loudest partial");
            Assert.That(worst, Is.LessThan(-40), "no new peak above -40 dB");
        }

        [Test]
        public void FormantKeepHoldsTheSpectralEnvelopeWhileThePartialsMove()
        {
            // A voice-like source: harmonics of 150 Hz under a bump at 1 kHz. Shifted up a fifth, the bump moves along unless the formants are kept.
            int n = Rate * 4;
            var x = new float[n];
            for (int h = 1; h * 150.0 < 9000; h++)
            {
                double a = 0.08 * Math.Exp(-0.5 * Math.Pow((h * 150.0 - 1000) / 350.0, 2));
                var s = Sine(n, h * 150.0, a);
                for (int i = 0; i < n; i++) x[i] += s[i];
            }
            double Centroid(float[] y)
            {
                int N = 1 << 16;
                var m = HannSpectrum(y, n - N - 100, N);
                double num = 0, den = 0;
                for (int k = (int)(200.0 * N / Rate); k < (int)(5000.0 * N / Rate); k++) { double f = k * (double)Rate / N; num += f * m[k] * m[k]; den += m[k] * m[k]; }
                return num / den;
            }
            double dry = Centroid(x);
            double Shifted(bool keep)
            {
                var v = Fresh(p => { Switch(p, P.PitchOn, true); Phys(p, P.Semi, 7); Switch(p, P.FormantKeep, keep); });
                return Centroid(Render(Engine(v), x, x).L);
            }
            double moved = Shifted(false), kept = Shifted(true);
            TestContext.Out.WriteLine($"envelope centroid: dry {dry:0} Hz, shifted {moved:0} Hz, shifted with formants kept {kept:0} Hz");
            Assert.That(moved / dry, Is.GreaterThan(1.3), "the envelope moves with the partials");
            Assert.That(kept / dry, Is.InRange(0.85, 1.15), "the envelope stays");
        }

        // ================================================================================== freeze

        [Test]
        public void AFrozenSineKeepsItsLevelWithinHalfADecibelOverThirtySecondsWithNoBeating([Values(1024, 4096, 8192)] int size)
        {
            var v = Fresh(p => Size(p, size));
            var e = Engine(v);
            int n = Rate * 34;
            var x = new float[n];
            var s = Sine(Rate * 2, 440, 0.5);
            Array.Copy(s, x, s.Length);
            var (l, _) = Render(e, x, x, 256, pos => { if (pos >= Rate * 3 / 2) v.Set(P.FreezeOn, 1); });
            double inDb = RmsDb(x, 0, Rate);
            double lo = 999, hi = -999, slo = 999, shi = -999;
            for (int t = Rate * 4; t + Rate <= n; t += Rate) { double d = RmsDb(l, t, t + Rate); lo = Math.Min(lo, d); hi = Math.Max(hi, d); }
            for (int t = Rate * 4; t + 960 <= n; t += 960) { double d = RmsDb(l, t, t + 960); slo = Math.Min(slo, d); shi = Math.Max(shi, d); }
            TestContext.Out.WriteLine($"{size}: input {inDb:0.00} dB; frozen 1 s levels {lo:0.00} … {hi:0.00} dB; 20 ms levels {slo:0.00} … {shi:0.00} dB");
            Assert.That(hi - lo, Is.LessThan(0.5), "level over 30 s");
            Assert.That(shi - slo, Is.LessThan(0.5), "no beating");
            Assert.That(lo, Is.EqualTo(inDb).Within(0.5), "the level of the sine that was frozen");
            double f = Frequency(l, Rate * 30, Rate * 33);
            Assert.That(Math.Abs(Cents(f, 440)), Is.LessThan(2), "and its pitch");
        }

        [Test]
        public void AHeldNoteFreezesTheSoundAndItsEndLetsGo()
        {
            var v = Fresh(p => Choice(p, P.FreezeTrig, "Note"));
            var e = Engine(v);
            int n = Rate * 6;
            var x = new float[n];
            var s = Sine(Rate, 440, 0.4);
            Array.Copy(s, x, s.Length);                                  // a second of tone, then silence
            var (l, _) = Render(e, x, x, 256, null, 120, 0, true, new[] { (Rate / 2, true, 60), (Rate * 4, false, 60) });
            Assert.That(RmsDb(l, Rate * 2, Rate * 3), Is.EqualTo(RmsDb(x, 0, Rate / 2 )).Within(1.0), "held while the key is down");
            Assert.That(RmsDb(l, Rate * 5, Rate * 6), Is.LessThan(-90), "gone after the key is released");
        }

        [Test]
        public void ASyncedFreezeCatchesTheSoundAtTheStartOfEveryPeriodAndHoldsItForItsLength()
        {
            var v = Fresh(p => { Choice(p, P.FreezeTrig, "Synced"); Choice(p, P.FreezeEvery, "1 bar"); Choice(p, P.FreezeLen, "1/2"); });
            var e = Engine(v);
            double spb = Spb(120);                                       // 120 BPM: a beat is half a second
            int n = (int)(spb * 16);
            var x = new float[n];
            var s = Sine((int)(spb * 4.2), 440, 0.4);                  // tone through bar 1 and a bit
            Array.Copy(s, x, s.Length);
            var (l, _) = Render(e, x, x, 256, null, 120, 0.0);
            int lat = e.Latency;
            // The catch at beat 4 holds 2 beats (to beat 6): the tone that ended at 4.2 is still there at 5, and gone by beat 7.
            double at5 = RmsDb(l, (int)(spb * 5.0) + lat - 4800, (int)(spb * 5.0) + lat);
            double at7 = RmsDb(l, (int)(spb * 7.0) + lat, (int)(spb * 7.5) + lat);
            TestContext.Out.WriteLine($"beat 5: {at5:0.0} dB, beat 7: {at7:0.0} dB (input {RmsDb(x, 0, Rate):0.0})");
            Assert.That(at5, Is.EqualTo(RmsDb(x, 0, Rate)).Within(1.5));
            Assert.That(at7, Is.LessThan(-60));
        }

        [Test]
        public void HoldBlendSetsHowMuchOfTheNewSoundGetsThroughTheFreeze([Values(0, 50, 100)] int hold)
        {
            var v = Fresh(p => Phys(p, P.Hold, hold / 100.0));
            var e = Engine(v);
            int n = Rate * 6;
            var x = Sine(n, 440, 0.3);                                   // 440 Hz is caught, then 660 Hz plays on top from 2 s
            var b = Sine(n, 660, 0.3);
            for (int i = Rate * 2; i < n; i++) x[i] += b[i];
            var (l, _) = Render(e, x, x, 256, pos => { if (pos >= Rate && pos < Rate + 256) v.Set(P.FreezeOn, 1); });
            var m = HannSpectrum(l, Rate * 5 - 40000 + 0, 1 << 15);
            int N = 1 << 15;
            double a440 = m[(int)Math.Round(440.0 * N / Rate)], a660 = m[(int)Math.Round(660.0 * N / Rate)];
            TestContext.Out.WriteLine($"Hold {hold} %: 440 Hz {Measure.Db(a440 / 0.3):0.0} dB, 660 Hz {Measure.Db(a660 / 0.3):0.0} dB");
            Assert.That(a440 / 0.3, Is.GreaterThan(0.25), "the first tone is there");
            double live = a660 / 0.3;
            double wanted = 1 - hold / 100.0;
            Assert.That(live, Is.EqualTo(wanted).Within(0.12), "the share of the new tone");
        }

        // ================================================================================== blur

        [Test]
        public void BlurLetsASoundDieAwayOverItsTimeAndSmearSpreadsItAcrossBins()
        {
            int n = Rate * 5;
            var rng = new Rng(3);
            var x = new float[n];
            for (int i = 0; i < Rate / 5; i++) x[i] = 0.5f * rng.NextBipolar();      // a burst of noise at the start
            double TailDb(ParamValues v, int e0, int e1)
            {
                var e = Engine(v);
                var (l, _) = Render(e, x, x);
                return RmsDb(l, e.Latency + e0, e.Latency + e1);
            }
            var off = Fresh();
            var on = Fresh(p => { Switch(p, P.BlurOn, true); Phys(p, P.BlurTime, 1000); });
            double dry = TailDb(off, Rate * 3 / 4, Rate);
            double blurred = TailDb(on, Rate * 3 / 4, Rate);
            double later = TailDb(on, Rate * 5 / 2, Rate * 3);
            TestContext.Out.WriteLine($"0.75 s after the burst: {dry:0.0} dB without, {blurred:0.0} dB with a 1 s blur; at 2.5 s {later:0.0} dB");
            Assert.That(blurred - dry, Is.GreaterThan(40), "a long tail");
            Assert.That(later, Is.LessThan(blurred - 8), "that dies away");

            // Smear: a sine's neighbours rise.
            double Skirt(int smear)
            {
                var v = Fresh(p => { Switch(p, P.BlurOn, true); Phys(p, P.BlurTime, 0); p.Set(P.Smear, smear); });
                var s = Sine(Rate * 4, 1000.5, 0.5);
                var (l, _) = Render(Engine(v), s, s);
                int N = 1 << 15;
                var m = HannSpectrum(l, Rate * 3, N);
                int k0 = (int)Math.Round(1000.5 * N / Rate);
                double side = 0;
                for (int k = k0 + 60; k < k0 + 110; k++) side = Math.Max(side, m[k]);      // ~90 Hz away, at 4096 about 8 bins
                return Measure.Db(side / m[k0]);
            }
            double plain = Skirt(0), smeared = Skirt(12);
            TestContext.Out.WriteLine($"skirt 90 Hz from a sine: {plain:0.0} dB with Smear 0, {smeared:0.0} dB with Smear 12");
            Assert.That(smeared, Is.GreaterThan(plain + 15));
        }

        // ================================================================================== scramble

        static float[] ScrambleRender(int seed, double amount, int width, string sync, float[] x, double rate = 5)
        {
            var v = Fresh(p =>
            {
                Switch(p, P.ScrOn, true); Phys(p, P.ScrAmount, amount); Phys(p, P.ScrWidth, width); Choice(p, P.ScrSync, sync);
                Phys(p, P.ScrRate, rate); Phys(p, P.ScrSeed, seed);
            });
            return Render(Engine(v), x, x).L;
        }

        [Test]
        public void ScrambleIsSeededKeepsToItsBandsAndAmountZeroLeavesTheSoundAlone()
        {
            int n = Rate * 6;
            var x = ChordSignal(n, new[] { 300.0, 700.0, 1500.0 }, 0.2);
            var a = ScrambleRender(5, 100, 16, "Free", x);
            var b = ScrambleRender(5, 100, 16, "Free", x);
            var c = ScrambleRender(6, 100, 16, "Free", x);
            for (int i = 0; i < n; i++) if (a[i] != b[i]) Assert.Fail($"the same seed differs at {i}");
            double diff = 0;
            for (int i = Rate * 2; i < n; i++) diff += Math.Abs(a[i] - c[i]);
            Assert.That(diff, Is.GreaterThan(1.0), "another seed, another sequence");
            // Amount 0: the sound as it was.
            var z = ScrambleRender(5, 0, 16, "Free", x);
            double err = 0, sig = 0;
            int lat = 4095;
            for (int i = lat + Rate; i < n; i++) { err += Math.Pow(z[i] - x[i - lat], 2); sig += Math.Pow(x[i - lat], 2); }
            Assert.That(10 * Math.Log10(err / sig), Is.LessThan(-90));
            // A sine moves only inside its band: width 16 bins at 4096 is 188 Hz wide; it stays inside the band that holds it (plus the lobe).
            int bandBin = (int)(2000.0 / (Rate / 4096.0));
            int b0 = 1 + (bandBin - 1) / 16 * 16;
            double lo = b0 * (Rate / 4096.0) - 3 * (Rate / 4096.0), hi = (b0 + 16) * (Rate / 4096.0) + 3 * (Rate / 4096.0);
            var s = Sine(n, 2000.0, 0.4);
            var o = ScrambleRender(9, 100, 16, "Free", s, 2);
            foreach (int at in new[] { Rate * 2, Rate * 3, Rate * 4 })
            {
                var m = HannSpectrum(o, at, 1 << 14);
                int pk = 0;
                for (int k = 1; k < m.Length; k++) if (m[k] > m[pk]) pk = k;
                double f = pk * (double)Rate / (1 << 14);
                Assert.That(f, Is.InRange(lo, hi), $"the sine stays in its band at {at / (double)Rate:0} s");
            }
        }

        [Test]
        public void ASyncedScrambleChangesItsShuffleOnTheBeatGrid()
        {
            // A 2 kHz sine under width-16 bands, shuffled on every quarter note at 120 BPM: the pitch it lands on is steady within a beat
            // and changes at the beat lines.
            int n = Rate * 8;
            var s = Sine(n, 2000.0, 0.4);
            var v = Fresh(p => { Switch(p, P.ScrOn, true); Phys(p, P.ScrAmount, 100); Phys(p, P.ScrWidth, 16); Choice(p, P.ScrSync, "1/4"); Phys(p, P.ScrSeed, 4); });
            var e = Engine(v);
            var (l, _) = Render(e, s, s);
            int lat = e.Latency;
            double spb = Spb(120);
            var freqs = new List<double>();
            for (int beat = 2; beat < 14; beat++)
            {
                int from = (int)(beat * spb) + lat + 6000, to = (int)((beat + 1) * spb) + lat - 4000;
                var m = HannSpectrum(l, from, 1 << 13);
                int pk = 0;
                for (int k = 1; k < m.Length; k++) if (m[k] > m[pk]) pk = k;
                freqs.Add(pk * (double)Rate / (1 << 13));
            }
            TestContext.Out.WriteLine("pitch per beat: " + string.Join(", ", freqs.Select(f => f.ToString("0"))));
            Assert.That(freqs.Distinct().Count(), Is.GreaterThanOrEqualTo(3), "the shuffle changes from beat to beat");
        }

        // ================================================================================== spectral delay

        [Test]
        public void AClicksEnergyInABinArrivesWithinOneHopOfThatBinsDelayAtEverySize([Values(1024, 4096, 8192)] int size)
        {
            var v = Fresh(p => { Size(p, size); Switch(p, P.DelayOn, true); Phys(p, P.DelLow, 50); Phys(p, P.DelHigh, 400); Phys(p, P.DelFeedback, 0); });
            var e = Engine(v);
            int n = Rate * 2;
            var x = new float[n];
            x[24000] = 0.9f;
            var (l, _) = Render(e, x, x);
            int hop = size / 4;
            foreach (double f0 in new[] { 500.0, 2000.0, 8000.0 })
            {
                double w = 2 * Math.PI * f0 / Rate, rr = 0.995, y1 = 0, y2 = 0, best = 0, acc = 0;
                int at = 0;
                var env = new double[n];
                double a1 = 2 * rr * Math.Cos(w), a2 = -rr * rr;
                for (int i = 0; i < n; i++) { double y = l[i] + a1 * y1 + a2 * y2; y2 = y1; y1 = y; env[i] = y * y; }
                for (int i = 0; i < n; i++)
                {
                    acc += env[i];
                    if (i >= 48) acc -= env[i - 48];
                    if (acc > best) { best = acc; at = i; }
                }
                double pos = Math.Clamp(Math.Log2(f0 / 20) / Math.Log2(1000), 0, 1);
                double expect = 24000 + e.Latency + (0.05 + 0.35 * pos) * Rate;
                TestContext.Out.WriteLine($"{size}, {f0} Hz: arrives {(at - expect) / hop:0.00} hops from its time (hop {hop})");
                Assert.That(Math.Abs(at - expect), Is.LessThanOrEqualTo(hop), $"{f0} Hz");
            }
        }

        [Test]
        public void ADelayInSyncRoundsItsTimesToTheSongsNoteLength()
        {
            var v = Fresh(p => { Switch(p, P.DelayOn, true); Phys(p, P.DelLow, 280); Phys(p, P.DelHigh, 280); Phys(p, P.DelFeedback, 0); Choice(p, P.DelSync, "1/8"); });
            var e = Engine(v);
            int n = Rate * 2;
            var x = new float[n];
            x[24000] = 0.9f;
            var (l, _) = Render(e, x, x);
            int at = 0;
            for (int i = 0; i < n; i++) if (Math.Abs(l[i]) > Math.Abs(l[at])) at = i;
            // 1/8 note = half a beat = 0.25 s at 120 BPM; 280 ms rounds to 250 ms (the grid unit)
            Assert.That(at - 24000 - e.Latency, Is.EqualTo((int)(0.25 * Rate)).Within(1024));
        }

        [Test]
        public void NinetyFivePercentFeedbackStaysBoundedForAMinuteAndDiesAway([Values(1024, 4096, 8192)] int size)
        {
            var v = Fresh(p => { Size(p, size); Switch(p, P.DelayOn, true); Phys(p, P.DelLow, 80); Phys(p, P.DelHigh, 80); Phys(p, P.DelFeedback, 95); Phys(p, P.DelDamping, 0); });
            var e = Engine(v);
            int n = Rate * 60;
            var rng = new Rng(1);
            var bl = new float[256]; var br = new float[256];
            double mx = 0, late = 0, loud = 0;
            for (int p = 0; p < n; p += 256)
            {
                for (int i = 0; i < 256; i++) { bl[i] = p < Rate * 20 ? 0.25f * rng.NextBipolar() : 0f; br[i] = bl[i]; }     // 20 s of noise, then silence
                e.SetTransport(true, p / Spb(120), Spb(120));
                e.Process(bl, br, 256);
                for (int i = 0; i < 256; i++)
                {
                    float a = Math.Max(Math.Abs(bl[i]), Math.Abs(br[i]));
                    if (!float.IsFinite(a)) Assert.Fail($"not finite at {p + i}");
                    mx = Math.Max(mx, a);
                    if (p > n - Rate) late = Math.Max(late, a);
                    if (p > Rate * 10 && p < Rate * 20) loud = Math.Max(loud, a);
                }
            }
            TestContext.Out.WriteLine($"{size}: peak {Measure.Db(mx):0.0} dBFS over 60 s; 10 - 20 s {Measure.Db(loud):0.0}; last second {Measure.Db(late):0.0}");
            Assert.That(Measure.Db(mx), Is.LessThan(12), "bounded");
            Assert.That(late, Is.LessThan(loud * 0.05), "and it dies away once the input stops");
        }

        [Test]
        public void DampingMakesEveryRepeatDarker()
        {
            double Ratio(double damping)
            {
                var v = Fresh(p => { Switch(p, P.DelayOn, true); Phys(p, P.DelLow, 100); Phys(p, P.DelHigh, 100); Phys(p, P.DelFeedback, 80); Phys(p, P.DelDamping, damping); });
                var e = Engine(v);
                int n = Rate * 3;
                var x = new float[n];
                var rng = new Rng(8);
                for (int i = 0; i < 2400; i++) x[i] = 0.5f * rng.NextBipolar();
                var (l, _) = Render(e, x, x);
                // The fourth repeat (0.4 s after the first), high band against low band, by one-pole splits.
                int from = e.Latency + (int)(0.4 * Rate), to = from + 4800;
                double lo = 0, hi = 0, st = 0;
                for (int i = from; i < to; i++) { st += 0.05 * (l[i] - st); lo += st * st; hi += (l[i] - st) * (l[i] - st); }
                return 10 * Math.Log10(hi / Math.Max(lo, 1e-20));
            }
            double plain = Ratio(0), damped = Ratio(100);
            TestContext.Out.WriteLine($"high against low in the fourth repeat: {plain:0.0} dB undamped, {damped:0.0} dB damped");
            Assert.That(damped, Is.LessThan(plain - 6));
        }

        // ================================================================================== clean

        static void Switches(string name, out Action<ParamValues, bool> apply)
        {
            switch (name)
            {
                case "pitch": apply = (p, on) => { Switch(p, P.PitchOn, on); Phys(p, P.Semi, 7); }; break;
                case "pitch formant": apply = (p, on) => { Switch(p, P.PitchOn, on); Phys(p, P.Semi, -5); Switch(p, P.FormantKeep, true); }; break;
                case "scramble": apply = (p, on) => { Switch(p, P.ScrOn, on); Phys(p, P.ScrAmount, 60); Phys(p, P.ScrRate, 8); }; break;
                case "freeze": apply = (p, on) => Switch(p, P.FreezeOn, on); break;
                case "blur": apply = (p, on) => { Switch(p, P.BlurOn, on); Phys(p, P.BlurTime, 1500); p.Set(P.Smear, 6); }; break;
                case "delay": apply = (p, on) => { Switch(p, P.DelayOn, on); Phys(p, P.DelLow, 60); Phys(p, P.DelHigh, 300); Phys(p, P.DelFeedback, 50); }; break;
                case "fft 4096 to 1024": apply = (p, on) => Size(p, on ? 1024 : 4096); break;
                case "fft 2048 to 8192": apply = (p, on) => Size(p, on ? 8192 : 2048); break;
                case "fft 8192 to 2048": apply = (p, on) => Size(p, on ? 2048 : 8192); break;
                default: throw new ArgumentException(name);
            }
        }

        [TestCase("pitch"), TestCase("pitch formant"), TestCase("scramble"), TestCase("freeze"), TestCase("blur"), TestCase("delay")]
        [TestCase("fft 4096 to 1024"), TestCase("fft 2048 to 8192"), TestCase("fft 8192 to 2048")]
        public void SwitchingASectionOnAndOffOrChangingTheSizeClicksNotOnAChord(string name)
        {
            Switches(name, out var apply);
            var v = Fresh();
            apply(v, false);
            var e = Engine(v);
            int n = Rate * 9;
            var x = ChordSignal(n, Triad, 0.15);
            int on = Rate * 3, off = Rate * 6;
            var (l, _) = Render(e, x, x, 256, pos =>
            {
                if (pos >= on && pos < on + 256) apply(v, true);
                if (pos >= off && pos < off + 256) apply(v, false);
            });
            ClickCheck(name, l, x, new[] { on, off }, 4095);
        }

        static void ClickCheck(string name, float[] y, float[] x, int[] switches, int latency)
        {
            int n = y.Length;
            double local = Math.Max(Math.Max(MaxSecond(y, switches[0] - Rate / 2, switches[0]), MaxSecond(y, n - Rate, n)),
                MaxSecond(y, switches[^1] - Rate / 2, switches[^1]));   // the steady level in each state
            var clicks = ClickDetector.Find(y).Where(c => c > 8191 + 200).ToList();   // the chord itself starts at sample 0
            TestContext.Out.WriteLine($"{name}: local level {local:0.00000}; clicks {clicks.Count}");
            Assert.That(clicks, Is.Empty, "click detector");
            foreach (int sw in switches)
            {
                // The switch reaches the output a frame later; the section's weight ramps over 30 ms and the frame spreads it further.
                double around = MaxSecond(y, sw + latency - 144, sw + latency + 144);
                double whole = MaxSecond(y, sw, sw + latency + Rate / 2);
                TestContext.Out.WriteLine($"  switch at {sw}: +-3 ms {around / local:0.00}x, the whole transition {whole / local:0.00}x the local level");
                Assert.That(around, Is.LessThanOrEqualTo(1.5 * local), "+-3 ms of the switch");
                Assert.That(whole, Is.LessThanOrEqualTo(1.5 * local), "the whole transition");
            }
        }

        [Test]
        public void AFreezeOnAndOffAndASizeChangeAreClickFreeOnFullScaleMusic()
        {
            var (l, r) = Music(Rate * 16);
            var v = Fresh();
            var e = Engine(v);
            var (ol, or) = Render(e, l, r, 256, pos =>
            {
                if (pos >= Rate * 3 && pos < Rate * 3 + 256) v.Set(P.FreezeOn, 1);
                if (pos >= Rate * 6 && pos < Rate * 6 + 256) v.Set(P.FreezeOn, 0);
                if (pos >= Rate * 9 && pos < Rate * 9 + 256) Size(v, 1024);
                if (pos >= Rate * 12 && pos < Rate * 12 + 256) Size(v, 8192);
            });
            var clicks = NewClicks(l, ol, 4095).Union(NewClicks(r, or, 4095)).ToList();
            TestContext.Out.WriteLine($"{clicks.Count} new clicks");
            Assert.That(clicks, Is.Empty);
        }

        // ================================================================================== the audio thread

        [Test]
        public void NothingIsAllocatedPerBlockAtEverySizeWithEverySectionBusy([Values(1024, 2048, 4096, 8192)] int size)
        {
            var v = AllOn(size);
            Choice(v, P.FreezeTrig, "Note");
            var e = Engine(v);
            var (dl, dr) = Music(Rate * 12);
            var l = new float[256]; var r = new float[256];
            double spb = Spb(120);
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                int pos = (b % 2000) * 256;
                Array.Copy(dl, pos, l, 0, 256);
                Array.Copy(dr, pos, r, 0, 256);
                e.SetTransport(true, b * 256 / spb, spb);
                if (b % 211 == 5) e.NoteOn(60);
                if (b % 211 == 150) e.NoteOff(60);
                // Parameters move under automation, and the size changes now and then (the other chain is built already).
                v.Set(P.Semi, 20 + b % 12);
                v.Set(P.BlurTime, 300 + b % 200);
                v.Set(P.DelLow, 100 + b % 100);
                v.Set(P.ScrAmount, 300 + b % 300);
                if (b % 700 == 699) Size(v, Sizes[(b / 700 + 1) % 4]);
                e.Process(l, r, 256);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void NoDenormalsRingOutOfAnyStageAfterALoudBurstAndTheOutputIsFinite()
        {
            var v = AllOn();
            Phys(v, P.Hold, 100); Phys(v, P.DelFeedback, 90); Phys(v, P.BlurTime, 1500);
            var e = Engine(v);
            int total = Rate * 40;
            var x = new float[total];
            var rng = new Rng(5);
            for (int i = 0; i < Rate / 2; i++) x[i] = 0.8f * rng.NextBipolar();
            var (ol, or) = Render(e, x, (float[])x.Clone(), 256, pos => { if (pos >= Rate) Switch(v, P.FreezeOn, false); });
            for (int i = 0; i < total; i++)
            {
                if (float.IsSubnormal(ol[i]) || float.IsSubnormal(or[i])) Assert.Fail($"a denormal at {i}");
                if (!float.IsFinite(ol[i]) || !float.IsFinite(or[i])) Assert.Fail($"not finite at {i}");
            }
            Assert.That(Measure.Peak(ol, Rate * 38, Rate), Is.LessThan(1e-6), "quiet at the end");
        }

        [Test]
        public void TheCpuAtFourThousandAndNinetySixIsStated()
        {
            var v = AllOn(4096);
            var e = Engine(v);
            var (dl, dr) = Music(Rate * 10);
            var l = new float[256]; var r = new float[256];
            double spb = Spb(120);
            // Warm up, then time ten seconds of audio in 256-sample blocks.
            for (int pass = 0; pass < 2; pass++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int pos = 0; pos + 256 <= Rate * 10; pos += 256)
                {
                    Array.Copy(dl, pos, l, 0, 256); Array.Copy(dr, pos, r, 0, 256);
                    e.SetTransport(true, pos / spb, spb);
                    e.Process(l, r, 256);
                }
                sw.Stop();
                double load = sw.Elapsed.TotalSeconds / 10.0;
                TestContext.Out.WriteLine($"pass {pass}: every section on at 4096 takes {load * 100:0.0} % of one core's real time");
                if (pass == 1) Assert.That(load, Is.LessThan(0.5));
            }
        }

        // ================================================================================== state and presets

        [Test]
        public void EveryParameterSurvivesASaveAndReopen()
        {
            var rng = new Rng(2026);
            var v = new ParamValues(Params.Table);
            for (int p = 0; p < Params.Table.Length; p++)
            {
                var d = Params.Table[p];
                v.Set(p, d.Min + (int)(rng.NextUnipolar() * (d.Max - d.Min + 1)) % (d.Max - d.Min + 1));
            }
            var saved = Enumerable.Range(0, Params.Table.Length).Select(i => v[i]).ToArray();
            var back = new ParamValues(Params.Table);
            for (int i = 0; i < saved.Length; i++) back.Set(i, saved[i]);
            for (int i = 0; i < saved.Length; i++) Assert.That(back[i], Is.EqualTo(v[i]), Params.Table[i].Name);
            Assert.That(Params.Table.Length, Is.EqualTo(P.Count));
            Assert.That(Params.Table.Select(d => d.Id).Distinct().Count(), Is.EqualTo(Params.Table.Length));
            // A reopened machine sounds the same: the same values through two engines give the same bits.
            var x = ChordSignal(Rate * 2, Triad, 0.2);
            var a = Render(Engine(v), x, x).L;
            var b = Render(Engine(back), x, x).L;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) Assert.Fail($"differs at {i}");
        }

        [Test]
        public void TheParametersAreInRealUnitsAndInTheirRanges()
        {
            var t = Params.Table;
            Assert.That(t[P.Semi].Value(t[P.Semi].Min), Is.EqualTo(-24));
            Assert.That(t[P.Semi].Value(t[P.Semi].Max), Is.EqualTo(24));
            Assert.That(t[P.Semi].Unit, Is.EqualTo("st"));
            Assert.That(t[P.Cents].Value(t[P.Cents].Min), Is.EqualTo(-100));
            Assert.That(t[P.Cents].Value(t[P.Cents].Max), Is.EqualTo(100));
            Assert.That(t[P.BlurTime].Value(t[P.BlurTime].Min), Is.EqualTo(0).Within(1e-9));
            Assert.That(t[P.BlurTime].Value(t[P.BlurTime].Max), Is.EqualTo(4000).Within(1e-9));
            Assert.That(t[P.BlurTime].Unit, Is.EqualTo("ms"));
            Assert.That(t[P.Smear].Value(t[P.Smear].Max), Is.EqualTo(24));
            Assert.That(t[P.DelLow].Value(t[P.DelLow].Max), Is.EqualTo(2000).Within(1e-9));
            Assert.That(t[P.DelHigh].Value(t[P.DelHigh].Max), Is.EqualTo(2000).Within(1e-9));
            Assert.That(t[P.DelFeedback].Value(t[P.DelFeedback].Max), Is.LessThan(100), "feedback stays below 100 %");
            Assert.That(t[P.ScrRate].Unit, Is.EqualTo("Hz"));
            Assert.That(t[P.FftSize].Names, Is.EqualTo(new[] { "1024", "2048", "4096", "8192" }));
            Assert.That(t[P.Amount].Value(t[P.Amount].Max), Is.EqualTo(200.0));
            Assert.That(t[P.Mix].Value(t[P.Mix].Default), Is.EqualTo(1.0));
            Assert.That(t[P.FftSize].Value(t[P.FftSize].Default), Is.EqualTo(2), "4096 at first");
        }

        /// <summary>The preset tests' values for every Neo Prism preset, by name.</summary>
        internal static List<(string Name, ParamValues Values)> PrismPresets()
        {
            var list = new List<(string, ParamValues)>();
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Prism"))
            {
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                list.Add((p.Preset, v));
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
        public void EveryPresetIsClearlyAudibleDifferentFromTheOthersAndClickFreeOnFullScaleMusic()
        {
            var presets = PrismPresets();
            Assert.That(presets.Count, Is.EqualTo(8));
            double spb = Spb(120);
            int length = (int)(spb * 16);
            var (l, r) = Music(length);
            var outs = new List<float[]>();
            int lat = 4095;
            var dryAligned = new float[length];
            for (int i = lat; i < length; i++) dryAligned[i] = l[i - lat];
            foreach (var (name, v) in presets)
            {
                var (ol, or) = Render(Engine(v), l, r);
                outs.Add(ol);
                double against = DiffDb(ol, dryAligned, (int)(spb * 4));
                var clicks = NewClicks(l, ol, lat).Where(c => c >= 4 * spb).ToList();
                TestContext.Out.WriteLine($"{name}: differs from the dry by {against:0.0} dB (0 dB = as loud as the music), {clicks.Count} clicks");
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

        [Test]
        public void ChangingFromOnePresetToAnotherWhileItPlaysDoesNotClick()
        {
            var presets = PrismPresets();
            var (l, r) = Music(Rate * 20);
            var v = new ParamValues(Params.Table);
            for (int p = 0; p < Params.Table.Length; p++) v.Set(p, presets[0].Values[p]);
            var e = Engine(v);
            int next = 0;
            var (ol, or) = Render(e, l, r, 256, pos =>
            {
                int slot = pos / (Rate * 2);
                if (slot >= 1 && slot > next && pos % (Rate * 2) < 256)
                {
                    next = slot;
                    var to = presets[slot % presets.Count].Values;
                    for (int p = 0; p < Params.Table.Length; p++) v.Set(p, to[p]);
                }
            });
            var clicks = NewClicks(l, ol, 4095).Union(NewClicks(r, or, 4095)).ToList();
            TestContext.Out.WriteLine($"{next} preset changes, {clicks.Count} new clicks");
            Assert.That(clicks, Is.Empty);
        }
    }
}
