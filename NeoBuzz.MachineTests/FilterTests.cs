using System.Diagnostics;
using System.Numerics;
using NeoBuzz.Dsp;
using NeoBuzz.Filter;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Filter (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class FilterTests
    {
        const int Rate = Measure.Rate;

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (FilterEngine Engine, ParamValues Values) Make(Action<ParamValues> setup, int rate = Rate, double tempo = 120)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new FilterEngine(v) { Tempo = tempo };
            e.SetSampleRate(rate);
            return (e, v);
        }

        /// <summary>Runs a signal through in blocks; <paramref name="transport"/>: the song plays from its start at the tempo (ticks of whole samples, 4 a beat).</summary>
        static (float[] L, float[] R) Run(FilterEngine e, float[] l, float[] r, int block = FilterEngine.MaxBlock, Action<int> before = null, bool transport = false)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[FilterEngine.MaxBlock];
            var br = new float[FilterEngine.MaxBlock];
            int samplesPerTick = (int)(60.0 * e.SampleRate / (e.Tempo * 4));
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                before?.Invoke(pos);
                if (transport)
                    e.Transport = new FilterTransport { Playing = true, Tick = pos / samplesPerTick, PosInTick = pos % samplesPerTick, SamplesPerTick = samplesPerTick, TicksPerBeat = 4 };
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static float[] Noise(int length, double rms, uint seed) => Measure.PinkNoise(length, rms, seed);

        static double Db(double x) => Measure.Db(x);

        static Complex Dtft(float[] h, double hz, int rate)
        {
            double w = 2 * Math.PI * hz / rate, re = 0, im = 0;
            for (int k = 0; k < h.Length; k++)
            {
                re += h[k] * Math.Cos(w * k);
                im -= h[k] * Math.Sin(w * k);
            }
            return new Complex(re, im);
        }

        /// <summary>The engine's impulse response (a small impulse, so the filter is linear), scaled back to unit gain.</summary>
        static float[] Impulse(FilterEngine e, int length, int rate)
        {
            var x = new float[length];
            x[0] = 1e-3f;
            var (l, _) = Run(e, x, (float[])x.Clone());
            for (int i = 0; i < length; i++) l[i] *= 1000;
            return l;
        }

        static double[] LogHz(int points, double lo = 20, double hi = 20000) =>
            Enumerable.Range(0, points).Select(i => lo * Math.Pow(hi / lo, i / (points - 1.0))).ToArray();

        /// <summary>Where a magnitude function first falls to <paramref name="db"/> below <paramref name="refDb"/> between two frequencies (bisection in log frequency).</summary>
        static double Crossing(Func<double, double> dbAt, double lo, double hi, double level)
        {
            for (int i = 0; i < 60; i++)
            {
                double mid = Math.Sqrt(lo * hi);
                if ((dbAt(lo) - level) * (dbAt(mid) - level) <= 0) hi = mid;
                else lo = mid;
            }
            return Math.Sqrt(lo * hi);
        }

        static readonly (string Name, int Type, int Slope)[] Kinds =
        {
            ("Ladder 24", 0, 0), ("Ladder 12", 1, 0), ("SVF LP 12", 2, 0), ("SVF LP 24", 2, 1), ("SVF BP 12", 3, 0), ("SVF BP 24", 3, 1),
            ("SVF HP 12", 4, 0), ("SVF HP 24", 4, 1), ("SVF notch 12", 5, 0), ("SVF notch 24", 5, 1), ("SVF peak", 6, 0),
        };

        // ------------------------------------------------------------------ the response

        [TestCase(48000)]
        [TestCase(44100)]
        public void TheResponseIsTheDesign(int rate)
        {
            // Every type and slope at three settings (a low cutoff with a high Q, the middle, a high cutoff), small signals: the
            // engine's impulse response (DTFT at 160 frequencies, 20 Hz – 20 kHz) against FilterDesign's magnitude, the resampling
            // included, where the design is above −80 dB.
            var settings = new[] { (60.0, 12.0), (1000.0, 0.71), (8000.0, 3.0) };
            double worst = 0;
            string at = "";
            foreach (var (name, type, slope) in Kinds)
                foreach (var (hz, q) in settings)
                {
                    var (e, v) = Make(pv => { pv.Set(P.Type, type); pv.Set(P.Slope, slope); Set(pv, P.Cutoff, hz); Set(pv, P.Resonance, q); }, rate);
                    var s = FilterDesign.From(v, 120);
                    int length = 4 * rate;
                    var h = Impulse(e, length, rate);
                    foreach (double f in LogHz(160, 20, Math.Min(20000, 0.45 * rate)))
                    {
                        double design = FilterDesign.Db(s.Type, s.Steep, s.CutoffHz, s.Q, f, rate, e.Stages);
                        if (design < -80) continue;
                        double measured = Db(Dtft(h, f, rate).Magnitude);
                        double d = Math.Abs(measured - design);
                        Assert.That(d, Is.LessThan(0.02), $"{name}, {hz} Hz, Q {q}, at {f:F0} Hz ({rate} Hz)");
                        if (d > worst) { worst = d; at = $"{name} {hz} Hz Q {q} at {f:F0} Hz"; }
                    }
                }
            Measure.Report($"Neo Filter response = design ({rate} Hz): 11 types × 3 settings within {worst:F4} dB ({at})");
        }

        [Test]
        public void MinusThreeDbPointsAndSlopesMatchTheDesign()
        {
            // Q 0.71 at 1 kHz: each low-pass and high-pass's −3 dB point (from the measured response) where the design puts it
            // (±0.5 %); the slope two to three octaves past the cutoff 12 or 24 dB/oct within 5 %; the band-pass's two −3 dB points
            // round its peak and the notch's depth.
            var lines = new List<string>();
            foreach (var (name, type, slope) in Kinds)
            {
                var (e, v) = Make(pv => { pv.Set(P.Type, type); pv.Set(P.Slope, slope); Set(pv, P.Cutoff, 1000); Set(pv, P.Resonance, 0.71); });
                var s = FilterDesign.From(v, 120);
                var h = Impulse(e, 2 * Rate, Rate);
                double Meas(double f) => Db(Dtft(h, f, Rate).Magnitude);
                double Des(double f) => FilterDesign.Db(s.Type, s.Steep, s.CutoffHz, s.Q, f, Rate, e.Stages);
                if (type <= 2 || type == 4)
                {
                    bool high = type == 4;
                    double pass = high ? Meas(15000) : Meas(30);
                    double designPass = high ? Des(15000) : Des(30);
                    double m3 = high ? Crossing(Meas, 30, 15000, pass - 3) : Crossing(Meas, 30, 15000, pass - 3);
                    double d3 = Crossing(Des, 30, 15000, designPass - 3);
                    double f1 = high ? s.CutoffHz / 4 : s.CutoffHz * 4, f2 = high ? s.CutoffHz / 8 : s.CutoffHz * 8;
                    double measuredSlope = Math.Abs(Meas(f1) - Meas(f2));
                    double nominal = type == 1 || slope == 0 && type != 0 ? 12 : 24;
                    Assert.That(m3 / d3, Is.EqualTo(1).Within(0.005), $"{name}: −3 dB point");
                    Assert.That(measuredSlope, Is.EqualTo(nominal).Within(nominal * 0.05), $"{name}: the slope 2–3 octaves past the cutoff");
                    lines.Add($"{name} −3 dB at {m3:F1} Hz (design {d3:F1}), {measuredSlope:F2} dB/oct");
                }
                else if (type == 3)
                {
                    double peak = Meas(1000);
                    double lo = Crossing(Meas, 50, 1000, peak - 3), hi = Crossing(Meas, 1000, 15000, peak - 3);
                    double dlo = Crossing(Des, 50, 1000, Des(1000) - 3), dhi = Crossing(Des, 1000, 15000, Des(1000) - 3);
                    Assert.That(lo / dlo, Is.EqualTo(1).Within(0.005), $"{name}: lower −3 dB point");
                    Assert.That(hi / dhi, Is.EqualTo(1).Within(0.005), $"{name}: upper −3 dB point");
                    lines.Add($"{name} peak {peak:F2} dB, −3 dB at {lo:F0} and {hi:F0} Hz (design {dlo:F0}, {dhi:F0})");
                }
                else if (type == 5)
                {
                    double depth = Meas(1000);
                    Assert.That(depth, Is.LessThan(-60), $"{name}: the notch at its frequency");
                    lines.Add($"{name} {depth:F0} dB at 1 kHz");
                }
            }
            Measure.Report("Neo Filter −3 dB points and slopes (1 kHz, Q 0.71): " + string.Join("; ", lines));
        }

        [Test]
        public void TheResonancePeakIsTheDesigns()
        {
            // Q 2, 8 and 30 at 300 Hz and 3 kHz: the measured peak's height (over the response at DC, or at 20 kHz for the
            // high-pass, its level for the band-pass and peak) and frequency against the design's: within 0.05 dB and 0.5 %.
            var lines = new List<string>();
            double worstDb = 0, worstHz = 0;
            foreach (var (name, type, slope) in Kinds.Where(k => k.Type != 5))
                foreach (double q in new[] { 2.0, 8, 30 })
                    foreach (double hz in new[] { 300.0, 3000 })
                    {
                        var (e, v) = Make(pv => { pv.Set(P.Type, type); pv.Set(P.Slope, slope); Set(pv, P.Cutoff, hz); Set(pv, P.Resonance, q); });
                        var s = FilterDesign.From(v, 120);
                        var h = Impulse(e, 4 * Rate, Rate);
                        double Meas(double f) => Db(Dtft(h, f, Rate).Magnitude);
                        double Des(double f) => FilterDesign.Db(s.Type, s.Steep, s.CutoffHz, s.Q, f, Rate, e.Stages);
                        // The peak: a fine search round the cutoff.
                        (double F, double Db) Peak(Func<double, double> at)
                        {
                            double bestF = hz, best = double.MinValue;
                            for (int i = -400; i <= 400; i++)
                            {
                                double f = hz * Math.Pow(2, i / 400.0);
                                double d = at(f);
                                if (d > best) { best = d; bestF = f; }
                            }
                            return (bestF, best);
                        }
                        var mp = Peak(Meas);
                        var dp = Peak(Des);
                        Assert.That(mp.Db, Is.EqualTo(dp.Db).Within(0.05), $"{name}, Q {q}, {hz} Hz: the peak's height");
                        Assert.That(mp.F / dp.F, Is.EqualTo(1).Within(0.005), $"{name}, Q {q}, {hz} Hz: its frequency");
                        worstDb = Math.Max(worstDb, Math.Abs(mp.Db - dp.Db));
                        worstHz = Math.Max(worstHz, Math.Abs(mp.F / dp.F - 1));
                        if (hz == 3000) lines.Add($"{name} Q {q}: {mp.Db:+0.0;−0.0} dB at {mp.F:F0} Hz");
                    }
            Measure.Report($"Neo Filter resonance peaks within {worstDb:F4} dB and {worstHz * 100:F3} % of the design (Q 2, 8, 30 at 300 Hz and 3 kHz); at 3 kHz: " + string.Join(", ", lines));
        }

        // ------------------------------------------------------------------ self-oscillation and the extremes

        [Test]
        public void SelfOscillationIsBoundedAndSingsAtTheCutoff()
        {
            // Resonance at its top, a click, then nothing: every type rings on by itself at the cutoff (the ladder within 2 cents,
            // the SVF within 15) at a bounded level, from 50 Hz to 10 kHz.
            var lines = new List<string>();
            foreach (var (name, type, slope) in Kinds.Where(k => k.Slope == 0 && k.Type != 5))
            {
                double lo = double.MaxValue, hi = double.MinValue, worstCents = 0;
                foreach (double hz in new[] { 50.0, 440, 2000, 10000 })
                {
                    var (e, _) = Make(pv => { pv.Set(P.Type, type); Set(pv, P.Cutoff, hz); pv.Set(P.Resonance, Params.Table[P.Resonance].Max); });
                    int length = 4 * Rate;
                    var x = new float[length];
                    x[0] = 0.01f;
                    var (l, _) = Run(e, x, (float[])x.Clone());
                    double peak = Db(Measure.Peak(l, length - Rate, Rate));
                    double f = Measure.PeakFrequency(l, length - 32768, 32768);
                    double cents = Measure.Cents(f, hz);
                    Assert.That(peak, Is.InRange(-20.0, 0.0), $"{name} at {hz} Hz: it sings, bounded");
                    Assert.That(Math.Abs(cents), Is.LessThan(FilterDesign.IsLadder((FilterType)type) ? 2 : 15), $"{name} at {hz} Hz: at the cutoff");
                    lo = Math.Min(lo, peak);
                    hi = Math.Max(hi, peak);
                    worstCents = Math.Max(worstCents, Math.Abs(cents));
                }
                lines.Add($"{name} {lo:F1} … {hi:F1} dBFS, within {worstCents:F1} cents");
            }
            Measure.Report("Neo Filter self-oscillation (50 Hz – 10 kHz): " + string.Join("; ", lines));
        }

        [Test]
        public void EverySettingStaysBounded()
        {
            // Each type and slope at the top of Resonance and at Q 100 below it, with the most drive, the deepest and fastest LFO
            // and envelope sweeps, at the lowest and highest cutoff; and 40 random settings with every parameter at either end: 5 s
            // of loud pink noise, a sine at +20 dBFS at 1 kHz, then silence. Finite, and never more than 12 dB over the input's peak
            // (+20 dBFS: at Mix 0 % the input itself).
            var rng = new Random(5);
            double worst = double.MinValue, worstNoise = double.MinValue;
            string at = "", atNoise = "";
            var input = Noise(2 * Rate, 0.5, 3).Concat(Measure.Sine(1000, Rate, 10)).Concat(new float[2 * Rate]).ToArray();
            void Check(string label, Action<ParamValues> setup)
            {
                var (e, _) = Make(setup);
                var (l, r) = Run(e, input, input);
                Assert.That(l.Concat(r).All(float.IsFinite), Is.True, label);
                Assert.That(e.HoldsNonFinite(), Is.False, label);
                double peak = Db(Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length)));
                double noisePeak = Db(Math.Max(Measure.Peak(l, 0, 2 * Rate), Measure.Peak(r, 0, 2 * Rate)));
                Assert.That(peak, Is.LessThan(32), label);
                Assert.That(noisePeak, Is.LessThan(18), label + ": the noise (peaking near 0 dBFS)");
                if (peak > worst) { worst = peak; at = label; }
                if (noisePeak > worstNoise) { worstNoise = noisePeak; atNoise = label; }
            }
            foreach (var (name, type, slope) in Kinds)
                foreach (int res in new[] { Params.Table[P.Resonance].Max, Params.Table[P.Resonance].Max - 1 })
                    foreach (int cut in new[] { 0, Params.Table[P.Cutoff].Max })
                        Check($"{name}, resonance raw {res}, cutoff raw {cut}", v =>
                        {
                            v.Set(P.Type, type); v.Set(P.Slope, slope); v.Set(P.Resonance, res); v.Set(P.Cutoff, cut);
                            v.Set(P.Drive, Params.Table[P.Drive].Max); v.Set(P.LfoDepth, Params.Table[P.LfoDepth].Max); v.Set(P.LfoRate, Params.Table[P.LfoRate].Max);
                            v.Set(P.EnvAmount, cut == 0 ? Params.Table[P.EnvAmount].Max : 0); v.Set(P.EnvSens, Params.Table[P.EnvSens].Max); v.Set(P.Output, 240);
                        });
            for (int k = 0; k < 40; k++)
            {
                int seed = rng.Next();
                Check($"random ends {seed}", v =>
                {
                    var rr = new Random(seed);
                    foreach (var d in Params.Table) v.Set(d.Index, rr.Next(2) == 0 ? d.Min : d.Max);
                    v.Set(P.Output, 240);
                });
            }
            Measure.Report($"Neo Filter extremes (loud noise, a +20 dBFS sine, Output 0 dB): bounded, at most {worst:+0.0;−0.0} dBFS ({at}); noise alone at most {worstNoise:+0.0;−0.0} dBFS ({atNoise})");
        }

        [Test]
        public void NanAndInfinityInTheInputLeaveTheStatesClean()
        {
            var (e, _) = Make(v => { v.Set(P.Type, 3); Set(v, P.Resonance, 20); v.Set(P.Drive, 120); Set(v, P.EnvAmount, 3); });
            var x = Noise(2 * Rate, 0.1, 13);
            for (int i = Rate / 2; i < Rate / 2 + 100; i++) x[i] = float.NaN;
            for (int i = Rate; i < Rate + 100; i++) x[i] = (i & 1) == 0 ? float.PositiveInfinity : float.NegativeInfinity;
            var (l, r) = Run(e, x, x);
            Assert.That(e.HoldsNonFinite(), Is.False);
            for (int i = Rate + 2000; i < x.Length; i++) Assert.That(float.IsFinite(l[i]) && float.IsFinite(r[i]), Is.True, $"finite again ({i})");
        }

        // ------------------------------------------------------------------ the drive

        [TestCase(0, 0.25)]
        [TestCase(0, 1.0)]
        [TestCase(2, 0.25)]
        [TestCase(2, 1.0)]
        [TestCase(4, 1.0)]
        public void TheDriveMakesNoAliasesWorthMeasuring(int type, double amplitude)
        {
            // A 3.1 kHz sine (Neo Drive's test), the most drive (24 dB), the filter open (a low-pass at 20 kHz or a high-pass at 20 Hz,
            // Q 0.5): the loudest component that is no harmonic, 20 Hz – 20 kHz, against the fundamental.
            const int bin = 4231;
            var (e, _) = Make(v => { v.Set(P.Type, type); Set(v, P.Cutoff, type == 4 ? 20 : 20000); Set(v, P.Resonance, 0.5); v.Set(P.Drive, 240); });
            var x = Measure.Sine(Measure.BinFrequency(bin), 8192 + Measure.N, amplitude);
            var (l, _) = Run(e, x, (float[])x.Clone());
            var mag = Measure.Spectrum(l, 8192);
            double floor = Measure.AliasFloorDb(mag, bin);
            double thd = Measure.Thd(mag, bin);
            Measure.Report($"Neo Filter drive 24 dB, {(FilterType)type}, 3.1 kHz at {Db(amplitude):+0;−0} dBFS: alias floor {floor:F1} dB (THD {thd * 100:F1} %)");
            Assert.That(thd, Is.GreaterThan(0.05), "the drive distorts");
            Assert.That(floor, Is.LessThan(amplitude < 0.5 ? -120 : -75));
        }

        [Test]
        public void TheDriveLeavesSmallSignalsAndTheResponseAlone()
        {
            // At any drive a small signal sees the filter alone: the impulse response at 24 dB of drive equals the clean one.
            var (a, _) = Make(v => { v.Set(P.Type, 2); Set(v, P.Cutoff, 2000); Set(v, P.Resonance, 2); });
            var (b, _) = Make(v => { v.Set(P.Type, 2); Set(v, P.Cutoff, 2000); Set(v, P.Resonance, 2); v.Set(P.Drive, 240); });
            var x = new float[Rate];
            x[0] = 1e-5f;
            var (la, _) = Run(a, x, x);
            var (lb, _) = Run(b, x, x);
            double err = 0, peak = Measure.Peak(la, 0, la.Length);
            for (int i = 0; i < la.Length; i++) err = Math.Max(err, Math.Abs(la[i] - lb[i]));
            Assert.That(err / peak, Is.LessThan(1e-4));
        }

        // ------------------------------------------------------------------ the envelope follower

        [TestCase(0.001, 0.05)]
        [TestCase(0.01, 0.3)]
        [TestCase(0.1, 2.0)]
        public void TheEnvelopeFollowersAttackAndReleaseAreTheirTimeConstants(double attack, double release)
        {
            // A square wave at 0.5 (its magnitude constant) switched on and off: the follower reaches 63 % of the step after the
            // attack time and falls to 37 % after the release time (one-pole time constants), within a sample's worth; and the
            // cutoff, with the envelope full, sits Env amount octaves up.
            var (e, v) = Make(pv => { Set(pv, P.EnvAttack, attack); Set(pv, P.EnvRelease, release); Set(pv, P.EnvAmount, 2); Set(pv, P.EnvSens, 12); Set(pv, P.Cutoff, 500); });
            double ta = v.Value(P.EnvAttack), tr = v.Value(P.EnvRelease);
            int on = (int)(Math.Max(10 * ta, 0.2) * Rate), off = (int)(5 * tr * Rate) + Rate / 10;
            var x = new float[on + off];
            for (int i = 0; i < on; i++) x[i] = (i / 20) % 2 == 0 ? 0.5f : -0.5f;
            var env = new float[x.Length];
            var bl = new float[1];
            var br = new float[1];
            double cutoffFull = 0;
            for (int i = 0; i < x.Length; i++)
            {
                bl[0] = br[0] = x[i];
                e.Process(bl, br, 1);
                env[i] = e.Follower;
                if (i == on - 1) cutoffFull = e.Cutoff(0);
            }
            int rise = Array.FindIndex(env, a => a >= 0.5f * (1 - Math.Exp(-1)));
            int fall = Array.FindIndex(env, on, a => a <= env[on - 1] * Math.Exp(-1)) - on;
            Measure.Report($"Neo Filter envelope: attack {ta * 1000:F2} ms → 63 % after {rise * 1000.0 / Rate:F3} ms; release {tr * 1000:F1} ms → 37 % after {fall * 1000.0 / Rate:F2} ms; full envelope: cutoff {cutoffFull:F1} Hz (500 Hz + 2 octaves)");
            Assert.That(rise, Is.EqualTo(ta * Rate).Within(1.5), "attack");
            Assert.That(fall, Is.EqualTo(tr * Rate).Within(1.5), "release");
            Assert.That(cutoffFull / (v.Value(P.Cutoff) * 4), Is.EqualTo(1).Within(1e-3), "the full envelope moves the cutoff by its amount");
        }

        // ------------------------------------------------------------------ the LFO

        [Test]
        public void TheLfosRateAndSyncFollowTheTempo()
        {
            // Free: 10 s at 0.37 Hz is 3.7 cycles. Synced: 1/4 at 120 BPM is 2 Hz, 1 bar at 90 BPM 0.375 Hz, 1/8T at 150 BPM 7.5 Hz,
            // 1/16D at 174 BPM 7.733 Hz. Song: the phase is the song's position in cycles (beats over the note value) plus LFO phase.
            var (fe, fv) = Make(v => { v.Set(P.LfoRetrig, 0); Set(v, P.LfoRate, 0.37); });
            double rate = fv.Value(P.LfoRate);
            Run(fe, new float[10 * Rate], new float[10 * Rate]);
            double cycles = fe.LfoCycle + fe.LfoPhase;
            Assert.That(cycles, Is.EqualTo(rate * 10).Within(1e-6 + rate * 16.0 / Rate), "free: cycles over 10 s");
            var lines = new List<string> { $"free {rate:F4} Hz: {cycles:F6} cycles in 10 s" };
            foreach (var (sync, bpm, hz) in new[] { (9, 120.0, 2.0), (14, 90.0, 0.375), (5, 150.0, 7.5), (4, 174.0, 174.0 / 60 / 0.375) })
            {
                var (e, v) = Make(pv => { pv.Set(P.LfoRetrig, 0); pv.Set(P.LfoSync, sync); }, tempo: bpm);
                Assert.That(e.Setting.LfoHz, Is.EqualTo(hz).Within(1e-9), $"{Params.Table[P.LfoSync].Describe(sync)} at {bpm} BPM");
                Run(e, new float[4 * Rate], new float[4 * Rate]);
                double c = e.LfoCycle + e.LfoPhase;
                Assert.That(c, Is.EqualTo(hz * 4).Within(hz * 16.0 / Rate + 1e-6), $"{Params.Table[P.LfoSync].Describe(sync)} at {bpm} BPM: cycles in 4 s");
                lines.Add($"{Params.Table[P.LfoSync].Describe(sync)} at {bpm} BPM: {c / 4:F4} Hz");
            }
            // Song: 1 bar at 120 BPM from the song's start, LFO phase 90°: after 3.5 beats the phase is 0.875 + 0.25.
            var (se, sv) = Make(v => { v.Set(P.LfoRetrig, 1); v.Set(P.LfoSync, 14); v.Set(P.LfoPhase, 90); });
            int beats35 = (int)(3.5 * 60.0 / 120 * Rate);
            Run(se, new float[beats35 + 16], new float[beats35 + 16], transport: true);
            double songPhase = se.LfoPhase;
            Assert.That(songPhase, Is.EqualTo((0.875 + 0.25) % 1).Within(0.002), "song: the phase is the position in cycles");
            lines.Add($"song, 1 bar from LFO phase 90°: {songPhase:F4} cycles after 3.5 beats");
            Measure.Report("Neo Filter LFO: " + string.Join("; ", lines));
        }

        [Test]
        public void TheRightChannelsLfoRunsSpreadAhead()
        {
            // LFO spread 90°: the right channel's LFO a quarter cycle on from the left's; 0°: both alike; 180°: opposite (sine).
            foreach (int spread in new[] { 0, 90, 180 })
            {
                var (e, _) = Make(v => { v.Set(P.LfoRetrig, 0); v.Set(P.LfoSpread, spread); Set(v, P.LfoRate, 1); Set(v, P.LfoDepth, 1); });
                Run(e, new float[Rate / 3], new float[Rate / 3]);
                double p = e.LfoPhase;
                double expectL = Math.Sin(2 * Math.PI * p), expectR = Math.Sin(2 * Math.PI * (p + spread / 360.0));
                Assert.That(e.LfoValue(0), Is.EqualTo(expectL).Within(1e-5), $"spread {spread}: left");
                Assert.That(e.LfoValue(1), Is.EqualTo(expectR).Within(1e-5), $"spread {spread}: right");
            }
        }

        [Test]
        public void EnvRetrigRestartsTheLfoOnEachOnset()
        {
            // LFO retrig Env: each burst after silence restarts the LFO at LFO phase.
            var (e, _) = Make(v => { v.Set(P.LfoRetrig, 2); v.Set(P.LfoPhase, 45); Set(v, P.LfoRate, 0.3); Set(v, P.EnvSens, 12); Set(v, P.EnvAttack, 0.0005); Set(v, P.EnvRelease, 0.01); });
            var burst = Measure.Sine(500, Rate / 4, 0.5);
            var quiet = new float[Rate / 2];
            Run(e, quiet, quiet);
            long before = e.LfoCycle;
            Run(e, burst, burst);
            Assert.That(e.LfoCycle, Is.EqualTo(before + 1), "a new cycle started at the onset");
            Assert.That(e.LfoPhase, Is.EqualTo(0.125 + 0.3 * (Rate / 4 - 1) / (double)Rate).Within(0.003), "from LFO phase");
            Run(e, quiet, quiet);
            Run(e, burst, burst);
            Assert.That(e.LfoCycle, Is.EqualTo(before + 2), "and again at the next");
        }

        // ------------------------------------------------------------------ moves, blocks, mix

        [Test]
        public void CutoffAndResonanceMovesMakeNoZipperNoise()
        {
            // The SVF peak (unity on both sides) at Q 4, its cutoff stepped through 100 Hz – 5 kHz and its Q through 1–8, one value
            // per 256-sample block as automation arrives, on a 220 Hz sine: the energy above 2 kHz against the sine. The same moves
            // through a plain SVF set once per block measure what zipper noise is.
            var x = Measure.Sine(220, 4 * Rate, 0.1);
            var (e, v) = Make(pv => { pv.Set(P.Type, 6); Set(pv, P.Resonance, 4); });
            int blocks = 0;
            var (l, _) = Run(e, x, x, before: pos =>
            {
                double t = (blocks++ % 64) / 63.0;
                Set(v, P.Cutoff, 100 * Math.Pow(50, t));
                Set(v, P.Resonance, Math.Pow(8, 1 - t));
            });
            double ours = HighEnergyDb(l);
            // The reference: a TPT SVF peak at the base rate, its coefficients switched per block.
            var y = new float[x.Length];
            double ic1 = 0, ic2 = 0;
            for (int b = 0; b * 256 < x.Length; b++)
            {
                double t = (b % 64) / 63.0, hz = 100 * Math.Pow(50, t), q = Math.Pow(8, 1 - t), k = 1 / q;
                double g = Math.Tan(Math.PI * hz / Rate), a1 = 1 / (1 + g * (g + k)), a2 = g * a1, a3 = g * a2;
                for (int i = b * 256; i < Math.Min(x.Length, b * 256 + 256); i++)
                {
                    double v3 = x[i] - ic2, v1 = a1 * ic1 + a2 * v3, v2 = ic2 + a2 * ic1 + a3 * v3;
                    ic1 = 2 * v1 - ic1;
                    ic2 = 2 * v2 - ic2;
                    y[i] = (float)(x[i] + (2 - k) * v1);
                }
            }
            double switched = HighEnergyDb(y);
            Measure.Report($"Neo Filter moves (peak 100 Hz – 5 kHz, Q 8 – 1, a step per 256-sample block, 220 Hz sine): energy above 2 kHz {ours:F1} dB (switched per block: {switched:F1} dB)");
            Assert.That(switched, Is.GreaterThan(-75), "the reference hears zipper noise");
            Assert.That(ours, Is.LessThan(-90));
        }

        /// <summary>The energy above 2 kHz in the last 3 s against the whole signal's, dB (a Hann-windowed FFT).</summary>
        static double HighEnergyDb(float[] y)
        {
            int n = 131072;
            var w = new float[n];
            int from = y.Length - n;
            for (int i = 0; i < n; i++) w[i] = y[from + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Fft.Magnitudes(w, 0, n);
            double all = 0, high = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                double p = mag[k] * mag[k];
                all += p;
                if (k * (double)Rate / n > 2000) high += p;
            }
            return 10 * Math.Log10(high / all + 1e-30);
        }

        [Test]
        public void TheOutputDoesNotDependOnHowTheHostSplitsIt()
        {
            // The envelope, the LFO (following the song, and free) and the coefficients move on segments that run across blocks:
            // blocks of 256, 128, 117 and 16 give the same samples, with the cutoff, the type and the resonance changing meanwhile.
            foreach (int retrig in new[] { 0, 1, 2 })
            {
                Action<ParamValues> setup = v =>
                {
                    v.Set(P.Type, 0); Set(v, P.Resonance, 6); v.Set(P.Drive, 120); Set(v, P.EnvAmount, 2); Set(v, P.EnvSens, 12);
                    v.Set(P.LfoSync, 9); Set(v, P.LfoDepth, 1.5); v.Set(P.LfoSpread, 60); v.Set(P.LfoShape, 5); v.Set(P.LfoRetrig, retrig);
                };
                var x = Noise(Rate, 0.3, 3);
                var y = Noise(Rate, 0.3, 4);
                float[] Render(int block)
                {
                    var (e, v) = Make(setup);
                    var (l, r) = Run(e, x, y, block, pos =>
                    {
                        if (pos == 11776) Set(v, P.Cutoff, 300);
                        if (pos == 23296) v.Set(P.Type, 3);
                        if (pos == 34816) Set(v, P.Resonance, 30);
                    }, transport: true);
                    return l.Concat(r).ToArray();
                }
                var a = Render(256);
                Assert.That(Render(128), Is.EqualTo(a), $"retrig {retrig}: blocks of 128");
                Assert.That(Render(16), Is.EqualTo(a), $"retrig {retrig}: blocks of 16");
                // Blocks of 117 don't start on the changes' samples; steady, they give the same.
                float[] Steady(int block)
                {
                    var (e, _) = Make(setup);
                    var (l, r) = Run(e, x, y, block, transport: true);
                    return l.Concat(r).ToArray();
                }
                Assert.That(Steady(117), Is.EqualTo(Steady(256)), $"retrig {retrig}: blocks of 117");
            }
        }

        [Test]
        public void MixZeroPassesTheInputBitForBit()
        {
            // Every part working (drive, the envelope and the LFO sweeping a resonant filter): at Mix 0 % and Output 0 dB the input
            // comes out bit for bit, negative zeros and the smallest numbers included.
            var (e, v) = Make(pv => { pv.Set(P.Mix, 0); pv.Set(P.Drive, 240); Set(pv, P.Resonance, 20); Set(pv, P.EnvAmount, 3); Set(pv, P.LfoDepth, 2); Set(pv, P.LfoRate, 5); });
            var x = Noise(Rate, 0.3, 7);
            x[10] = -0f;
            x[11] = float.Epsilon;
            x[12] = -float.Epsilon;
            var (l, r) = Run(e, x, x, block: 117, before: pos => { if (pos > Rate / 2) v.Set(P.Type, 4); });
            for (int i = 0; i < x.Length; i++)
            {
                Assert.That(BitConverter.SingleToInt32Bits(l[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(x[i])), $"left {i}");
                Assert.That(BitConverter.SingleToInt32Bits(r[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(x[i])), $"right {i}");
            }
            Assert.That(e.Latency, Is.EqualTo(0));
        }

        [Test]
        public void ParameterJumpsAreSmooth()
        {
            // A 220 Hz sine at −12 dBFS through a low-pass at 2 kHz, Q 0.71: each parameter jumps between its ends every 4096 samples. The
            // largest step between samples stays within 1.5× the largest the steady settings at either end make on their own: no
            // clicks (the type and slope fade out, switch and fade back in).
            var problems = new List<string>();
            var lines = new List<string>();
            foreach (var d in Params.Table)
            {
                Action<ParamValues> baseSetup = v => { v.Set(P.Type, 2); Set(v, P.Cutoff, 2000); Set(v, P.Resonance, 0.71); };
                var x = Measure.Sine(220, 2 * Rate, 0.25);
                double Steady(int raw)
                {
                    var (e, _) = Make(v => { baseSetup(v); v.Set(d.Index, raw); });
                    var (l, r) = Run(e, x, x);
                    return Math.Max(MaxStep(l, Rate / 2), MaxStep(r, Rate / 2));
                }
                double reference = Math.Max(Steady(d.Min), Steady(d.Max));
                var (je, jv) = Make(baseSetup);
                var (jl, jr) = Run(je, x, x, before: pos => { if (pos >= Rate / 2 && pos % 4096 == 0) jv.Set(d.Index, (pos / 4096) % 2 == 0 ? d.Min : d.Max); });
                double jump = Math.Max(MaxStep(jl, Rate / 2), MaxStep(jr, Rate / 2));
                double ratio = jump / Math.Max(reference, 1e-9);
                lines.Add($"{d.Name} {ratio:F2}");
                // Resonance's top self-oscillates: a tone of its own, not a click.
                if (ratio > 1.5 && d.Index != P.Resonance) problems.Add($"{d.Name} jumps make steps {ratio:F2}× the steady signal's ({jump:F4} against {reference:F4})");
            }
            Measure.Report($"Neo Filter parameter jumps, largest step against the steady signal's: {string.Join(", ", lines)}");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        static double MaxStep(float[] y, int from)
        {
            double m = 0;
            for (int i = from + 1; i < y.Length; i++) m = Math.Max(m, Math.Abs(y[i] - y[i - 1]));
            return m;
        }

        // ------------------------------------------------------------------ hygiene

        [Test]
        public void SilenceAfterNoiseLeavesNoSubnormalsAndRunsAtFullSpeed()
        {
            // 2 s of noise through a resonant low-pass, then 20 s of silence: nothing subnormal in the states or the output, silence
            // costs no more than noise, and the machine goes idle once the tail is gone.
            foreach (int type in new[] { 0, 2, 3 })
            {
                var (e, _) = Make(v => { v.Set(P.Type, type); Set(v, P.Resonance, 20); Set(v, P.Cutoff, 60); v.Set(P.Slope, 1); });
                var noise = Noise(2 * Rate, 0.3, 9);
                var silent = new float[20 * Rate];
                var sw = Stopwatch.StartNew();
                Run(e, noise, noise);
                double noiseSeconds = sw.Elapsed.TotalSeconds / 2;
                sw.Restart();
                var (l, r) = Run(e, silent, silent);
                double silenceSeconds = sw.Elapsed.TotalSeconds / 20;
                Assert.That(e.HoldsSubnormals(), Is.False, "no subnormal state");
                Assert.That(l.Concat(r).Any(float.IsSubnormal), Is.False, "no subnormal output");
                Assert.That(e.Idle, Is.True, "idle after the tail");
                Measure.Report($"Neo Filter {(FilterType)type}, Q 20 at 60 Hz: silence after noise costs {silenceSeconds / noiseSeconds:F2}× noise; no subnormals; idle after the tail");
                Assert.That(silenceSeconds, Is.LessThan(1.5 * noiseSeconds + 0.002));
            }
        }

        [Test]
        public void TheAudioPathAllocatesNothing()
        {
            // 2000 blocks with the cutoff moving every block, the type, slope, sync and retrig switching, the envelope and the LFO working.
            var (e, v) = Make(pv => { pv.Set(P.Drive, 100); Set(pv, P.EnvAmount, 2); Set(pv, P.LfoDepth, 1); pv.Set(P.LfoShape, 6); });
            var l = new float[FilterEngine.MaxBlock];
            var r = new float[FilterEngine.MaxBlock];
            var rng = new Random(1);
            void Blocks(int count)
            {
                for (int b = 0; b < count; b++)
                {
                    for (int i = 0; i < l.Length; i++) l[i] = r[i] = (float)Math.Sin(i * 0.05 + b) * 0.3f;
                    v.Set(P.Cutoff, rng.Next(10001));
                    if (b % 50 == 7) v.Set(P.Type, rng.Next(7));
                    if (b % 60 == 3) v.Set(P.Slope, rng.Next(2));
                    if (b % 70 == 9) v.Set(P.LfoSync, rng.Next(19));
                    if (b % 80 == 11) v.Set(P.LfoRetrig, rng.Next(3));
                    if (b % 90 == 13) v.Set(P.Resonance, rng.Next(1001));
                    e.Transport = new FilterTransport { Playing = true, Tick = b, SamplesPerTick = 256, TicksPerBeat = 4 };
                    e.Process(l, r, l.Length);
                }
            }
            Blocks(200);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Blocks(2000);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Measure.Report($"Neo Filter: {bytes} bytes allocated over 2000 blocks with the cutoff moving every block and the type, slope, sync and retrig switching");
            Assert.That(bytes, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ presets, CPU

        [Test]
        public void EveryPresetLoadsAndStaysWithinLevelBounds()
        {
            // Each factory preset on pink noise at −18 dBFS RMS for 3 s: within ±12 dB of the input's level (a filter takes level
            // away by design: a band-pass keeps a few octaves of the noise), peaks under full scale, finite.
            var x = Noise(3 * Rate, DspMath.DbToGain(-18), 21);
            double inRms = Measure.Rms(x, Rate, 2 * Rate);
            int count = 0;
            foreach (var (_, machine, name, values) in PresetTests.AllPresets())
            {
                if (machine != "Neo Filter") continue;
                var (e, _) = Make(pv => { foreach (var kv in values) if (kv.Key >= 0) pv.Set(kv.Key, kv.Value); });
                var (l, r) = Run(e, x, x, transport: true);
                double rms = Math.Max(Measure.Rms(l, Rate, 2 * Rate), Measure.Rms(r, Rate, 2 * Rate));
                double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
                Measure.Report($"Neo Filter / {name}: {Db(rms / inRms):+0.0;−0.0} dB, peak {Db(peak):+0.0;−0.0} dBFS");
                Assert.That(double.IsFinite(peak), Is.True, name);
                Assert.That(Db(rms / inRms), Is.InRange(-12.0, 6.0), name);
                Assert.That(Db(peak), Is.LessThan(0), name);
                count++;
            }
            Assert.That(count, Is.InRange(12, 20));
        }

        [Test]
        public void CpuAtTheHeaviestSetting()
        {
            // Stereo at 48 kHz, every part working (drive, the envelope and a fast LFO moving the cutoff every segment, resonance),
            // each family at its heaviest: the ladder (its coefficients each sample) and the SVF at 24 dB/oct.
            foreach (var (name, type, slope) in new[] { ("Ladder 24", 0, 0), ("SVF LP 24", 2, 1) })
            {
                var (e, _) = Make(v => { v.Set(P.Type, type); v.Set(P.Slope, slope); v.Set(P.Drive, 240); Set(v, P.LfoDepth, 2); Set(v, P.LfoRate, 8); Set(v, P.EnvAmount, 3); Set(v, P.Resonance, 8); v.Set(P.Mix, 500); });
                var x = Noise(10 * Rate, 0.2, 4);
                Run(e, x, x);
                var sw = Stopwatch.StartNew();
                Run(e, x, x);
                double speed = 10 / sw.Elapsed.TotalSeconds;
                Measure.Report($"Neo Filter CPU ({name}, everything on): {speed:F0}× real time");
                Assert.That(speed, Is.GreaterThan(20));
            }
        }
    }
}
