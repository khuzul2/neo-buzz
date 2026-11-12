using NeoBuzz.Dsp;
using NeoBuzz.Transient;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Transient (docs/specs/mastering-tools.md §5.4): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class TransientTests
    {
        const int Rate = Measure.Rate;

        static (TransientEngine Engine, ParamValues Values) Make(Action<ParamValues>? setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new TransientEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (float[] L, float[] R) Run(TransientEngine e, float[] l, float[] r, int block = TransientEngine.MaxBlock, Action<int>? before = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[TransientEngine.MaxBlock];
            var br = new float[TransientEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                before?.Invoke(pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        const double HitEvery = 0.5;

        /// <summary>
        /// A synthetic drum: every 0.5 s a kick (an 80 Hz sine falling to 50 Hz, e^(−t/80 ms)) and, half a beat on, a snare
        /// (noise, e^(−t/40 ms)), exponential bursts at known samples; <paramref name="hits"/> returns each onset.
        /// </summary>
        static (float[] L, float[] R) Drum(int seconds, out List<(int At, bool Kick)> hits, uint seed = 99)
        {
            int n = seconds * Rate;
            var l = new float[n];
            var r = new float[n];
            hits = new();
            uint s = seed;
            double Noise() { s = s * 1664525u + 1013904223u; return (s >> 8) / (double)(1 << 24) * 2 - 1; }
            for (double t0 = 0.1; t0 < seconds - 0.4; t0 += HitEvery)
            {
                int at = (int)(t0 * Rate);
                hits.Add((at, true));
                double ph = 0;
                for (int i = 0; i < 0.4 * Rate && at + i < n; i++)
                {
                    double t = i / (double)Rate;
                    ph += 2 * Math.PI * (50 + 30 * Math.Exp(-t / 0.015)) / Rate;
                    float v = (float)(0.7 * Math.Sin(ph) * Math.Exp(-t / 0.08));
                    l[at + i] += v;
                    r[at + i] += v;
                }
                int sn = at + (int)(0.25 * Rate);
                hits.Add((sn, false));
                for (int i = 0; i < 0.2 * Rate && sn + i < n; i++)
                {
                    double t = i / (double)Rate;
                    l[sn + i] += (float)(0.5 * Noise() * Math.Exp(-t / 0.04));
                    r[sn + i] += (float)(0.5 * Noise() * Math.Exp(-t / 0.04));
                }
            }
            return (l, r);
        }

        static double PeakDb(float[] x, int from, int n) => Measure.Db(Measure.Peak(x, from, n));

        // ---- unity ------------------------------------------------------------------------------

        [TestCase(0, 0)]
        [TestCase(30, 0)]
        [TestCase(30, 1)]
        [TestCase(50, 1)]
        public void UnityPassesTheAudioBitForBitDelayedByTheLookahead(double lookaheadMs, int midSide)
        {
            var (l, r) = Drum(3, out _);
            var (e, _) = Make(v =>
            {
                Set(v, P.Lookahead, lookaheadMs / 10);
                v.Set(P.Stereo, midSide);
            });
            var (ol, or) = Run(e, l, r);
            int la = e.Latency;
            Assert.That(la, Is.EqualTo(TransientEngine.LookaheadSamples(lookaheadMs / 10, Rate)));
            for (int i = la; i < l.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(ol[i]) != BitConverter.SingleToInt32Bits(l[i - la]) ||
                    BitConverter.SingleToInt32Bits(or[i]) != BitConverter.SingleToInt32Bits(r[i - la]))
                    Assert.Fail($"sample {i} differs (lookahead {lookaheadMs / 10} ms, mid/side {midSide})");
            }
        }

        // ---- attack and sustain -----------------------------------------------------------------

        /// <summary>The first 5 ms peak of every hit of one kind (after the first two: the followers settled), output over input, dB.</summary>
        static double HitChange(float[] inL, float[] outL, List<(int At, bool Kick)> hits, bool kick, int latency, int window = 5)
        {
            var d = new List<double>();
            int w = window * Rate / 1000;
            foreach (var h in hits.Where(h => h.Kick == kick).Skip(2))
                d.Add(PeakDb(outL, h.At + latency, w) - PeakDb(inL, h.At, w));
            return d.Average();
        }

        [TestCase(12.0)]
        [TestCase(6.0)]
        [TestCase(3.0)]
        [TestCase(-6.0)]
        [TestCase(-12.0)]
        public void AttackMovesTheFirstFiveMillisecondsPeakByTheGain(double db)
        {
            var (l, r) = Drum(6, out var hits);
            var (e, _) = Make(v => Set(v, P.Attack, db));
            var (ol, _) = Run(e, l, r);
            double kick = HitChange(l, ol, hits, true, e.Latency), snare = HitChange(l, ol, hits, false, e.Latency);
            Measure.Report($"Neo Transient Attack {db:+0.0;-0.0} dB (3 ms lookahead): the first 5 ms peak of the kick {kick:+0.00;-0.00} dB, of the snare {snare:+0.00;-0.00} dB");
            Assert.That(kick, Is.EqualTo(db).Within(0.5));
            Assert.That(snare, Is.EqualTo(db).Within(0.5));
        }

        [Test]
        public void WithoutLookaheadTheFirstMillisecondGetsLessOfTheAttackGain()
        {
            var (l, r) = Drum(6, out var hits);
            var (e, _) = Make(v => { Set(v, P.Attack, 12); Set(v, P.Lookahead, 0); });
            var (ol, _) = Run(e, l, r);
            Assert.That(e.Latency, Is.EqualTo(0));
            double kick = HitChange(l, ol, hits, true, 0), snare = HitChange(l, ol, hits, false, 0);
            Measure.Report($"Neo Transient Attack +12 dB with no lookahead: the first 5 ms peak of the kick {kick:+0.00} dB, of the snare {snare:+0.00} dB (reported)");
            Assert.That(kick, Is.GreaterThan(3).And.LessThanOrEqualTo(12.5));
        }

        [TestCase(100.0)]
        [TestCase(1000.0)]
        [TestCase(8000.0)]
        public void ASteadySineIsUntouchedByAttack(double hz)
        {
            var x = Measure.Sine(hz, Rate * 2, 0.5);
            var (e, _) = Make(v => Set(v, P.Attack, 12));
            var (o, _) = Run(e, x, (float[])x.Clone());
            int la = e.Latency;
            int from = Rate, n = Rate / 2;
            double change = Measure.Db(Measure.Rms(o, from, n) / Measure.Rms(x, from - la, n));
            Measure.Report($"Neo Transient steady {hz:F0} Hz sine, Attack +12 dB: {change:+0.0000;-0.0000} dB");
            Assert.That(Math.Abs(change), Is.LessThan(0.01));
        }

        [Test]
        public void SustainActsOnASteadySineAndOnTheTails()
        {
            var x = Measure.Sine(220, Rate * 2, 0.5);
            var (e, _) = Make(v => Set(v, P.Sustain, -6));
            var (o, _) = Run(e, x, (float[])x.Clone());
            double change = Measure.Db(Measure.Rms(o, Rate, Rate / 2) / Measure.Rms(x, Rate, Rate / 2));
            Assert.That(change, Is.EqualTo(-6.0).Within(0.01), "a steady sound is all sustain");

            var (l, r) = Drum(6, out var hits);
            var (e2, _) = Make(v => Set(v, P.Sustain, -9));
            var (ol, _) = Run(e2, l, r);
            // The kick's tail, 60–100 ms after its onset.
            var tails = hits.Where(h => h.Kick).Skip(2).Select(h => PeakDb(ol, h.At + e2.Latency + 60 * Rate / 1000, 40 * Rate / 1000) - PeakDb(l, h.At + 60 * Rate / 1000, 40 * Rate / 1000)).Average();
            Measure.Report($"Neo Transient Sustain −9 dB: a steady 220 Hz sine {change:F3} dB; the kick's tail (60–100 ms) {tails:F2} dB");
            Assert.That(tails, Is.EqualTo(-9).Within(0.75));
        }

        [Test]
        public void AttackAndSustainTogetherKeepTheirOwnRegions()
        {
            var (l, r) = Drum(6, out var hits);
            var (e, _) = Make(v => { Set(v, P.Attack, 6); Set(v, P.Sustain, -6); });
            var (ol, _) = Run(e, l, r);
            double peak = HitChange(l, ol, hits, true, e.Latency);
            double tail = hits.Where(h => h.Kick).Skip(2).Select(h => PeakDb(ol, h.At + e.Latency + 80 * Rate / 1000, 40 * Rate / 1000) - PeakDb(l, h.At + 80 * Rate / 1000, 40 * Rate / 1000)).Average();
            Measure.Report($"Neo Transient Attack +6 / Sustain −6 dB: kick peak {peak:+0.00} dB, tail (80–120 ms) {tail:+0.00;-0.00} dB");
            Assert.That(peak, Is.EqualTo(6).Within(0.5));
            Assert.That(tail, Is.EqualTo(-6).Within(1.0));
        }

        [Test]
        public void MidSideShapesEachOfMidAndSideOnItsOwn()
        {
            // A hit on the side alone (opposite polarities) over a steady mid: Attack +12 raises the side's peak, not the mid's bed.
            var (dl, _) = Drum(6, out var hits);
            var l = new float[dl.Length];
            var r = new float[dl.Length];
            var bed = Measure.Sine(300, dl.Length, 0.2);
            for (int i = 0; i < l.Length; i++)
            {
                l[i] = bed[i] + dl[i];
                r[i] = bed[i] - dl[i];
            }
            var (e, _) = Make(v => { Set(v, P.Attack, 12); v.Set(P.Stereo, 1); });
            var (ol, or) = Run(e, l, r);
            var mid = new float[l.Length];
            var side = new float[l.Length];
            for (int i = 0; i < l.Length; i++) { mid[i] = (ol[i] + or[i]) / 2; side[i] = (ol[i] - or[i]) / 2; }
            int la = e.Latency;
            double side5 = hits.Where(h => h.Kick).Skip(2).Select(h => PeakDb(side, h.At + la, 240) - PeakDb(dl, h.At, 240)).Average();
            double bedChange = Measure.Db(Measure.Rms(mid, Rate * 5, Rate / 2) / Measure.Rms(bed, Rate * 5 - la, Rate / 2));
            Measure.Report($"Neo Transient mid/side: side-only kick's first 5 ms peak {side5:+0.00} dB with Attack +12; the steady mid {bedChange:+0.000} dB");
            Assert.That(side5, Is.EqualTo(12).Within(0.6));
            Assert.That(Math.Abs(bedChange), Is.LessThan(0.05));
        }

        // ---- blocks, silence, zipper, delta, mix --------------------------------------------------

        [TestCase(0)]
        [TestCase(1)]
        public void BlocksOf256And128And117And16GiveTheSameSamples(int impact)
        {
            var (l, r) = Drum(3, out _);
            float[]? first = null;
            foreach (int block in new[] { 256, 128, 117, 16, 1 })
            {
                var (e, vals) = Make(v =>
                {
                    Set(v, P.Attack, 8);
                    Set(v, P.Sustain, -4);
                    v.Set(P.Stereo, 1);
                    v.Set(P.Mode, impact);
                    v.Set(P.Impact1, 800);
                    v.Set(P.Impact3, 150);
                    Set(v, P.Mix, 0.8);
                });
                var (ol, _) = Run(e, l, r, block, pos => { if (pos == 29952) Set(vals!, P.Attack, -3); });
                if (first == null) first = ol;
                else for (int i = 0; i < ol.Length; i++)
                        if (BitConverter.SingleToInt32Bits(ol[i]) != BitConverter.SingleToInt32Bits(first[i])) Assert.Fail($"block {block}: sample {i} differs (impact mode {impact})");
            }
        }

        static double ZipperDb(float[] y)
        {
            int n = 1 << 16;
            var w = new float[n];
            int from = y.Length - n;
            for (int i = 0; i < n; i++) w[i] = y[from + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Measure.Spectrum(w, 0, n);
            int k220 = (int)Math.Round(220.0 * n / Rate), k2k = (int)Math.Round(2000.0 * n / Rate);
            double sine = 0, high = 0;
            for (int k = k220 - 40; k <= k220 + 40; k++) sine += mag[k] * mag[k];
            for (int k = k2k; k < mag.Length; k++) high += mag[k] * mag[k];
            return 10 * Math.Log10(high / sine);
        }

        [Test]
        public void ParametersSteppingEveryBlockMakeNoZipper()
        {
            var x = Measure.Sine(220, Rate * 4, 0.5);
            var (e, v) = Make();
            var rng = new Random(5);
            var (o, _) = Run(e, x, (float[])x.Clone(), 256, pos =>
            {
                Set(v, P.Sustain, (rng.NextDouble() * 2 - 1) * 12);
                Set(v, P.Attack, (rng.NextDouble() * 2 - 1) * 12);
                Set(v, P.Mix, 0.5 + 0.5 * rng.NextDouble());
                Set(v, P.Output, (rng.NextDouble() * 2 - 1) * 6);
            });
            double z = ZipperDb(o);
            Measure.Report($"Neo Transient zipper: Attack, Sustain, Mix and Output stepped every 256-sample block on a 220 Hz sine: {z:F1} dB above 2 kHz");
            Assert.That(z, Is.LessThanOrEqualTo(-90));
        }

        [Test]
        public void DeltaIsTheOutputMinusTheDelayedInput()
        {
            var (l, r) = Drum(3, out _);
            var (e, _) = Make(v => { Set(v, P.Attack, 9); Set(v, P.Sustain, -5); v.Set(P.Delta, 1); });
            var (ol, _) = Run(e, l, r);
            var (e2, _) = Make(v => { Set(v, P.Attack, 9); Set(v, P.Sustain, -5); });
            var (wl, _) = Run(e2, l, r);
            int la = e.Latency;
            double worst = 0;
            for (int i = Rate; i < l.Length; i++) worst = Math.Max(worst, Math.Abs(ol[i] - (wl[i] - l[i - la])));
            Assert.That(worst, Is.LessThan(1e-6));
        }

        [Test]
        public void MixBlendsTheDelayedDryAndTheShapedSignal()
        {
            var (l, r) = Drum(4, out _);
            var (e1, _) = Make(v => Set(v, P.Attack, 10));
            var (full, _) = Run(e1, l, r);
            var (e2, _) = Make(v => { Set(v, P.Attack, 10); Set(v, P.Mix, 0.4); });
            var (part, _) = Run(e2, l, r);
            int la = e1.Latency;
            double worst = 0;
            for (int i = Rate; i < l.Length; i++)
            {
                double want = l[i - la] + 0.4 * (full[i] - l[i - la]);
                worst = Math.Max(worst, Math.Abs(part[i] - want));
            }
            Assert.That(worst, Is.LessThan(1e-6));
        }

        [Test]
        public void SilenceLeavesNothingBehind()
        {
            var (l, r) = Drum(2, out _);
            var (e, _) = Make(v => { Set(v, P.Attack, 12); Set(v, P.Sustain, 12); v.Set(P.Mode, 1); v.Set(P.Impact2, 1000); });
            Run(e, l, r);
            var z = new float[Rate];
            var (ol, or) = Run(e, z, (float[])z.Clone());
            for (int i = 4096; i < ol.Length; i++)
            {
                Assert.That(float.IsFinite(ol[i]) && !float.IsSubnormal(ol[i]), $"sample {i}");
                Assert.That(or[i], Is.EqualTo(0f).Within(1e-12), $"sample {i}");
            }
        }

        // ---- Impact --------------------------------------------------------------------------------

        [Test]
        public void ImpactAtRestPassesTheDelayedAudioBitForBit()
        {
            var (l, r) = Drum(3, out _);
            foreach (int ms in new[] { 0, 1 })
            {
                var (e, _) = Make(v => { v.Set(P.Mode, 1); v.Set(P.Stereo, ms); });
                var (ol, or) = Run(e, l, r);
                int la = e.Latency;
                for (int i = Rate; i < l.Length; i++)
                    if (BitConverter.SingleToInt32Bits(ol[i]) != BitConverter.SingleToInt32Bits(l[i - la])) Assert.Fail($"sample {i} (mid/side {ms})");
            }
        }

        [Test]
        public void TheFourBandsSumFlat()
        {
            foreach (var rate in new[] { 44100, 48000, 96000 })
            {
                var bands = new LrBands(rate, 120, 1000, 6000);
                int n = 1 << 15;
                var imp = new float[n];
                imp[0] = 1;
                var bl = new float[4][];
                var br = new float[4][];
                for (int b = 0; b < 4; b++) { bl[b] = new float[n]; br[b] = new float[n]; }
                bands.Process(imp, (float[])imp.Clone(), n, 4, bl, br);
                var sum = new float[n];
                for (int b = 0; b < 4; b++) for (int i = 0; i < n; i++) sum[i] += bl[b][i];
                var mag = Fft.Magnitudes(sum, 0, n);
                double lo = 1e9, hi = 0;
                for (int k = (int)(20.0 * n / rate); k < (int)(0.45 * n); k++) { lo = Math.Min(lo, mag[k]); hi = Math.Max(hi, mag[k]); }
                Measure.Report($"Neo Transient bands at {rate} Hz: the four bands sum flat within {Measure.Db(hi / lo):F4} dB (20 Hz – 0.45 fs)");
                Assert.That(Measure.Db(hi / lo), Is.LessThan(0.01));
            }
        }

        /// <summary>40 ms bursts of a tone every 250 ms, 20 dB over a bed of the same tone.</summary>
        static float[] Bursts(double hz, int seconds)
        {
            var x = new float[seconds * Rate];
            for (int i = 0; i < x.Length; i++)
            {
                double t = i / (double)Rate;
                double env = (t % 0.25) < 0.04 ? 1 : 0.1;
                x[i] = (float)(0.4 * env * Math.Sin(2 * Math.PI * hz * t));
            }
            return x;
        }

        [TestCase(1.0)]
        [TestCase(0.5)]
        [TestCase(-0.5)]
        [TestCase(-1.0)]
        public void ImpactMovesABandsMicrodynamicsAndKeepsItsEnergy(double impact)
        {
            // 1.5 kHz is in the third band (1 kHz – 6 kHz).
            var x = Bursts(1500, 6);
            var (e, _) = Make(v => { v.Set(P.Mode, 1); v.Set(P.Impact3, (int)Math.Round(500 + 500 * impact)); });
            var (o, _) = Run(e, x, (float[])x.Clone());
            int la = e.Latency;
            double In(int a, int len) => Measure.Rms(x, a, len);
            double Out(int a, int len) => Measure.Rms(o, a + la, len);
            double burstIn = 0, burstOut = 0, bedIn = 0, bedOut = 0;
            int cycles = 0;
            for (double t = 2.0; t < 5.5; t += 0.25)
            {
                int a = (int)(t * Rate);
                burstIn += In(a + 240, 480); burstOut += Out(a + 240, 480);     // 5–15 ms into the burst
                bedIn += In(a + 6000, 2400); bedOut += Out(a + 6000, 2400);     // the bed, 125 – 175 ms on
                cycles++;
            }
            double ratioIn = Measure.Db(burstIn / bedIn), ratioOut = Measure.Db(burstOut / bedOut);
            double energyIn = Measure.Rms(x, 2 * Rate, 3 * Rate), energyOut = Measure.Rms(o, 2 * Rate + la, 3 * Rate);
            Measure.Report($"Neo Transient Impact {impact * 100:+0;-0}% on 40 ms bursts 20 dB over a bed: the burst-to-bed ratio {ratioIn:F2} → {ratioOut:F2} dB; energy {Measure.Db(energyOut / energyIn):+0.00;-0.00} dB");
            Assert.That(Math.Sign(ratioOut - ratioIn), Is.EqualTo(Math.Sign(impact)));
            Assert.That(Math.Abs(Measure.Db(energyOut / energyIn)), Is.LessThan(1.0), "auto gain keeps the band's energy");
            // The knob moves the ratio by (1 + I) within the detector's blur: +100 % roughly doubles it, −100 % takes most of it out.
            Assert.That(Math.Abs(ratioOut - ratioIn), Is.GreaterThan(1.5 * Math.Abs(impact)), "the ratio moves with the knob");
        }

        [Test]
        public void ImpactOnOneBandLeavesTheOthersAlone()
        {
            var x = Measure.Sine(150, Rate * 3, 0.4);       // the second band (120 Hz – 1 kHz)
            var (e, _) = Make(v => { v.Set(P.Mode, 1); v.Set(P.Impact4, 1000); v.Set(P.Impact1, 0); });
            var (o, _) = Run(e, x, (float[])x.Clone());
            double d = Measure.Db(Measure.Rms(o, 2 * Rate, Rate / 2) / Measure.Rms(x, 2 * Rate - e.Latency, Rate / 2));
            Assert.That(Math.Abs(d), Is.LessThan(0.05));
        }

        [Test]
        public void SwitchingModeAndImpactMakesNoClick()
        {
            var x = Measure.Sine(220, Rate * 4, 0.5);
            var (e, v) = Make(c => c.Set(P.Impact2, 900));
            var (o, _) = Run(e, x, (float[])x.Clone(), 256, pos =>
            {
                if (pos == Rate) v.Set(P.Mode, 1);
                if (pos == 2 * Rate) v.Set(P.Impact2, 500);
                if (pos == 3 * Rate) v.Set(P.Mode, 0);
            });
            double own = 2 * Math.PI * 220 / Rate * 0.5;
            double worst = 0;
            for (int i = 8192; i < o.Length; i++) worst = Math.Max(worst, Math.Abs(o[i] - o[i - 1]));
            Measure.Report($"Neo Transient mode and Impact switches on a 220 Hz sine: the largest step {worst / own:F2}× the sine's own");
            Assert.That(worst, Is.LessThan(1.6 * own));
        }

        [Test]
        public void ReadingsReportTheGainsAndTheLatency()
        {
            var (l, r) = Drum(5, out _);
            var (e, _) = Make(v => { Set(v, P.Attack, 9); Set(v, P.Sustain, -6); });
            Run(e, l, r);
            var rd = e.Readings();
            Measure.Report($"Neo Transient readings: gain max {rd["gain_max_db"]:F2} dB, min {rd["gain_min_db"]:F2} dB, onsets {rd["transient_percent"]:F1} %, latency {rd["latency_samples"]}");
            Assert.That(rd["gain_max_db"], Is.EqualTo(9).Within(0.2));
            Assert.That(rd["gain_min_db"], Is.EqualTo(-6).Within(0.2));
            Assert.That(rd["transient_percent"], Is.GreaterThan(0.5).And.LessThan(30));
            Assert.That(rd["latency_samples"], Is.EqualTo(TransientEngine.LookaheadSamples(3, Rate)));
            Assert.That(rd.ContainsKey("gain_match_db"));
        }

        // ---- allocation and CPU -------------------------------------------------------------------

        [TestCase(0)]
        [TestCase(1)]
        public void NothingIsAllocatedPerBlock(int impact)
        {
            var (l, r) = Drum(5, out _);
            var (e, v) = Make(c =>
            {
                Set(c, P.Attack, 6);
                Set(c, P.Sustain, -3);
                c.Set(P.Stereo, 1);
                c.Set(P.Mode, impact);
                c.Set(P.Impact1, 800);
                c.Set(P.Impact4, 200);
            });
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                int pos = (b % 900) * 256;
                Array.Copy(l, pos, bl, 0, 256);
                Array.Copy(r, pos, br, 0, 256);
                if (b % 7 == 0) Set(v, P.Attack, (b % 13) - 6);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 60; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 60));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        // Throughput guards (Throughput.cs; machines.md §4) against the reference workload in the same run. Figures at the
        // reference's speed (Attack/Sustain L/R and M/S, Impact L/R and M/S): the 2-core Xeon 559, 313, 70, 66. Floors per
        // architecture: x64 and arm64.
        [TestCase(0, 0, 250, 150)]
        [TestCase(0, 1, 140, 90)]
        [TestCase(1, 0, 30, 20)]
        [TestCase(1, 1, 28, 18)]
        public void RunsFasterThanRealTime(int impact, int ms, double x64, double arm64)
        {
            var (l, r) = LimiterTests.Drums(Rate * 3);
            const int Warm = Rate;
            Throughput.Guard($"Neo Transient CPU ({(impact == 1 ? "Impact" : "Attack/Sustain")}, {(ms == 1 ? "M/S" : "L/R")}, drums)", Throughput.Floor(x64, arm64), 2,
                Rate, TransientEngine.MaxBlock, () =>
            {
                var (e, _) = Make(v =>
                {
                    Set(v, P.Attack, 6);
                    Set(v, P.Sustain, -3);
                    v.Set(P.Mode, impact);
                    v.Set(P.Stereo, ms);
                    v.Set(P.Impact1, 800);
                    v.Set(P.Impact2, 200);
                    v.Set(P.Impact3, 800);
                    v.Set(P.Impact4, 300);
                });
                var bl = new float[TransientEngine.MaxBlock];
                var br = new float[TransientEngine.MaxBlock];
                void Block(int pos, int n)
                {
                    Array.Copy(l, pos, bl, 0, n);
                    Array.Copy(r, pos, br, 0, n);
                    e.Process(bl, br, n);
                }
                for (int pos = 0; pos < Warm; pos += TransientEngine.MaxBlock) Block(pos, TransientEngine.MaxBlock);
                return Throughput.InBlocks(TransientEngine.MaxBlock, (pos, n) => Block(Warm + pos, n));
            }, Measure.Report);
        }
    }
}
