using System.Diagnostics;
using System.Security.Cryptography;
using NeoBuzz.Dsp;
using NeoBuzz.Eq;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo EQ v2 (docs/specs/mastering-tools.md §5.3): channels, shapes, dynamic bands, Delta, readings, measured.</summary>
    [TestFixture]
    public class EqV2Tests
    {
        const int Rate = Measure.Rate;

        static (EqEngine Engine, ParamValues Values) Make(Action<ParamValues>? setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            for (int b = 0; b < EqDesign.Bands; b++) v.Set(EqDesign.On(b), 0);
            setup?.Invoke(v);
            var e = new EqEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static void Shape(ParamValues v, int b, BandShape shape) => v.Set(EqDesign.ShapeOf(b), (int)shape);

        static void Channel(ParamValues v, int b, BandChannel c) => v.Set(EqDesign.Channel(b), (int)c);

        /// <summary>Band <paramref name="b"/> dynamic: down (or up), threshold, ratio (∞ allowed), knee, range, attack, release, detector.</summary>
        static void Dynamic(ParamValues v, int b, bool up = false, double threshold = -30, double ratio = 4, double knee = 0, double range = 24,
            double attack = 0.01, double release = 0.15, bool sidechain = false)
        {
            v.Set(EqDesign.Dynamic(b), up ? 2 : 1);
            Set(v, EqDesign.Threshold(b), threshold);
            if (double.IsPositiveInfinity(ratio)) v.Set(EqDesign.Ratio(b), Params.Table[EqDesign.Ratio(b)].Max);
            else Set(v, EqDesign.Ratio(b), ratio);
            Set(v, EqDesign.Knee(b), knee);
            Set(v, EqDesign.Range(b), range);
            Set(v, EqDesign.Attack(b), attack);
            Set(v, EqDesign.Release(b), release);
            v.Set(EqDesign.Detect(b), sidechain ? 1 : 0);
        }

        static (float[] L, float[] R) Run(EqEngine e, float[] l, float[] r, float[]? kl = null, float[]? kr = null, int block = EqEngine.MaxBlock, Action<int>? before = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[EqEngine.MaxBlock];
            var br = new float[EqEngine.MaxBlock];
            var bkl = new float[EqEngine.MaxBlock];
            var bkr = new float[EqEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += block)
            {
                before?.Invoke(pos);
                int n = Math.Min(block, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                if (kl != null)
                {
                    Array.Copy(kl, pos, bkl, 0, n);
                    Array.Copy(kr ?? kl, pos, bkr, 0, n);
                }
                e.Process(bl, br, kl != null ? bkl : null, kl != null ? bkr : null, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        /// <summary>The response of an impulse response at a frequency, dB (a direct DTFT).</summary>
        static double ResponseDb(float[] h, double hz, int rate = Rate)
        {
            double sr = 0, si = 0;
            for (int n = 0; n < h.Length; n++)
            {
                double th = 2 * Math.PI * hz * n / rate;
                sr += h[n] * Math.Cos(th);
                si -= h[n] * Math.Sin(th);
            }
            return 10 * Math.Log10(sr * sr + si * si + 1e-300);
        }

        static IEnumerable<double> LogFrequencies(double lo, double hi, int count)
        {
            for (int i = 0; i < count; i++) yield return lo * Math.Pow(hi / lo, i / (double)(count - 1));
        }

        // ---- R=D per channel and shape --------------------------------------------------------------------------

        internal static readonly (string Name, BandType Type, BandShape Shape, double Hz, double Db, double Q)[] ShapeCases =
        {
            ("bell", BandType.Bell, BandShape.Standard, 1000, 6, 1.4),
            ("proportional-Q bell +12 dB", BandType.Bell, BandShape.ProportionalQ, 800, 12, 1),
            ("proportional-Q bell −9 dB", BandType.Bell, BandShape.ProportionalQ, 3000, -9, 0.7),
            ("Baxandall low +8 dB", BandType.LowShelf, BandShape.Baxandall, 300, 8, 0.7),
            ("Baxandall high −6 dB", BandType.HighShelf, BandShape.Baxandall, 5000, -6, 0.7),
            ("Vintage low +6 dB", BandType.LowShelf, BandShape.Vintage, 100, 6, 0.7),
            ("Vintage high +5 dB", BandType.HighShelf, BandShape.Vintage, 10000, 5, 0.7),
            ("all-pass", BandType.Bell, BandShape.AllPass, 1000, 6, 2),
        };

        [Test]
        public void ResponseMatchesTheDesignPerChannelAndShape()
        {
            const int length = 1 << 15;
            double worst = 0;
            string where = "";
            var lines = new List<string>();
            foreach (var c in ShapeCases)
                foreach (BandChannel chan in Enum.GetValues<BandChannel>())
                {
                    var (e, v) = Make(p =>
                    {
                        EqTests.Band(p, 3, c.Type, c.Hz, c.Db, c.Q);
                        Shape(p, 3, c.Shape);
                        Channel(p, 3, chan);
                    });
                    // The channel the band works on gets the impulse its way: L alone, R alone, mono (L = R) for Mid, L = −R for Side.
                    var l = new float[length];
                    var r = new float[length];
                    switch (chan)
                    {
                        case BandChannel.Left: l[0] = 1; break;
                        case BandChannel.Right: r[0] = 1; break;
                        case BandChannel.Side: l[0] = 1; r[0] = -1; break;
                        default: l[0] = r[0] = 1; break;
                    }
                    var (yl, yr) = Run(e, l, r);
                    var h = chan == BandChannel.Right ? yr : yl;
                    var settings = new BandSettings { Type = c.Type, Hz = c.Hz, GainDb = c.Db, Q = c.Q, Shape = c.Shape };
                    double caseWorst = 0;
                    foreach (double hz in LogFrequencies(20, 20000, 90))
                    {
                        double want = 20 * Math.Log10(EqDesign.Magnitude(settings, hz, Rate));
                        double err = Math.Abs(ResponseDb(h, hz) - want);
                        if (err > caseWorst) caseWorst = err;
                        if (err > worst) { worst = err; where = $"{c.Name}, {chan}, {hz:F0} Hz"; }
                    }
                    lines.Add($"{c.Name} {chan} {caseWorst:F4}");
                }
            Measure.Report($"Neo EQ v2 R=D, {ShapeCases.Length} shapes × 5 channels (20 Hz – 20 kHz): worst {worst:F4} dB ({where})");
            Assert.That(worst, Is.LessThan(0.01), where);
        }

        static (float[] L, float[] R) PinkPair(int n = 24000)
        {
            var l = Measure.PinkNoise(n, 0.2, 71);
            var r = Measure.PinkNoise(n, 0.2, 72);
            return (l, r);
        }

        [Test]
        public void TheOtherChannelPassesUntouched()
        {
            // Left, Right, Mid and Side bands boosting 9 dB at 1 kHz: the channel (or half) they don't work on is the input's.
            var (l, r) = PinkPair();
            foreach (BandChannel chan in new[] { BandChannel.Left, BandChannel.Right, BandChannel.Mid, BandChannel.Side })
            {
                var (e, _) = Make(p => { EqTests.Band(p, 2, BandType.Bell, 1000, 9, 1); Channel(p, 2, chan); });
                var (yl, yr) = Run(e, l, r);
                switch (chan)
                {
                    case BandChannel.Left:
                        Assert.That(yr, Is.EqualTo(r), "Left: right untouched");
                        Assert.That(yl, Is.Not.EqualTo(l));
                        break;
                    case BandChannel.Right:
                        Assert.That(yl, Is.EqualTo(l), "Right: left untouched");
                        Assert.That(yr, Is.Not.EqualTo(r));
                        break;
                    default:
                        // The half not worked on: within the outputs' float rounding (each output rounded once from double).
                        double worst = 0, peak = 0;
                        for (int i = 0; i < l.Length; i++)
                        {
                            double untouched = chan == BandChannel.Mid ? (double)l[i] - r[i] : (double)l[i] + r[i];
                            double got = chan == BandChannel.Mid ? (double)yl[i] - yr[i] : (double)yl[i] + yr[i];
                            double ulps = (Ulp(yl[i]) + Ulp(yr[i])) / 2;
                            worst = Math.Max(worst, Math.Abs(got - untouched) / Math.Max(ulps, 1e-45));
                            peak = Math.Max(peak, Math.Abs(untouched));
                        }
                        Measure.Report($"Neo EQ v2 {chan} band +9 dB: {(chan == BandChannel.Mid ? "L−R" : "L+R")} within {worst:F2} of the outputs' half-ulps (float rounding)");
                        Assert.That(worst, Is.LessThanOrEqualTo(1.0));
                        break;
                }
            }
        }

        static double Ulp(float x)
        {
            float a = Math.Abs(x);
            return MathF.BitIncrement(a) - a;
        }

        [Test]
        public void ASideOrMidBandAtRestIsBitForBit()
        {
            // A 0 dB bell's section passes its input exactly; encoded and decoded around it, mid/side return the input bit for bit.
            var (l, r) = PinkPair();
            foreach (BandChannel chan in new[] { BandChannel.Mid, BandChannel.Side })
            {
                var (e, _) = Make(p => { EqTests.Band(p, 2, BandType.Bell, 1000, 0, 1); Channel(p, 2, chan); });
                var (yl, yr) = Run(e, l, r);
                Assert.That(yl, Is.EqualTo(l), chan.ToString());
                Assert.That(yr, Is.EqualTo(r), chan.ToString());
            }
            Measure.Report("Neo EQ v2 Mid and Side bands at 0 dB: L and R bit for bit");
        }

        // ---- the shapes ------------------------------------------------------------------------------------------

        [Test]
        public void TheShapesAreWhatTheySay()
        {
            double M(BandSettings s, double hz) => 20 * Math.Log10(EqDesign.Magnitude(s, hz, Rate));
            // Proportional Q: Q × 10^(|gain|/40), about twice for each 12 dB.
            var prop = new BandSettings { Type = BandType.Bell, Hz = 1000, GainDb = 12, Q = 1, Shape = BandShape.ProportionalQ };
            var std = new BandSettings { Type = BandType.Bell, Hz = 1000, GainDb = 12, Q = Math.Pow(10, 12 / 40.0) };
            double propWorst = LogFrequencies(20, 20000, 60).Max(hz => Math.Abs(M(prop, hz) - M(std, hz)));
            Assert.That(propWorst, Is.LessThan(1e-6), "proportional Q at +12 dB is Q × 10^(12/40)");
            // Baxandall: half the gain at the frequency, the full gain far below, 6 dB/oct between.
            var bax = new BandSettings { Type = BandType.LowShelf, Hz = 300, GainDb = 12, Q = 5, Shape = BandShape.Baxandall };
            double atF = M(bax, 300), far = M(bax, 20), top = M(bax, 18000);
            Assert.That(atF, Is.EqualTo(6).Within(0.05), "half the gain at its frequency");
            Assert.That(far, Is.EqualTo(12).Within(0.15), "the full gain two decades below");
            Assert.That(Math.Abs(top), Is.LessThan(0.05), "flat far above");
            // The steepest slope a first-order shelf has: under 6 dB/oct (a second-order shelf of the same gain is steeper).
            double steepest = 0, steepestStd = 0;
            var shelf = new BandSettings { Type = BandType.LowShelf, Hz = 300, GainDb = 12, Q = 0.7071 };
            for (double hz = 30; hz < 3000; hz *= 1.05)
            {
                steepest = Math.Max(steepest, M(bax, hz) - M(bax, hz * 2));
                steepestStd = Math.Max(steepestStd, M(shelf, hz) - M(shelf, hz * 2));
            }
            Assert.That(steepest, Is.LessThan(6.0));
            Assert.That(steepestStd, Is.GreaterThan(steepest));
            // Vintage: the shelf times a bell of half its gain the other way, an octave inside it, Q 1.
            var vin = new BandSettings { Type = BandType.LowShelf, Hz = 100, GainDb = 8, Q = 0.7071, Shape = BandShape.Vintage };
            var vinShelf = new BandSettings { Type = BandType.LowShelf, Hz = 100, GainDb = 8, Q = 0.7071 };
            var dip = new BandSettings { Type = BandType.Bell, Hz = 200, GainDb = -4, Q = 1 };
            double vinWorst = LogFrequencies(20, 20000, 60).Max(hz => Math.Abs(M(vin, hz) - M(vinShelf, hz) - M(dip, hz)));
            Assert.That(vinWorst, Is.LessThan(1e-9));
            double dipDepth = LogFrequencies(100, 1000, 200).Min(hz => M(vin, hz));
            // All-pass: magnitude 1 everywhere.
            var ap = new BandSettings { Type = BandType.HighCut, Hz = 1000, GainDb = 6, Q = 2, Shape = BandShape.AllPass };
            double apWorst = LogFrequencies(20, 20000, 60).Max(hz => Math.Abs(M(ap, hz)));
            Assert.That(apWorst, Is.LessThan(1e-9));
            Measure.Report($"Neo EQ v2 shapes: proportional Q +12 dB = Q × 1.995 within {propWorst:E1} dB; Baxandall low +12 dB at 300 Hz: {atF:F2} dB there, " +
                $"{far:F2} dB at 20 Hz, steepest {steepest:F2} dB/oct (the standard shelf's {steepestStd:F2}); Vintage low +8 dB at 100 Hz: the dip reaches " +
                $"{dipDepth:F2} dB above it; all-pass flat within {apWorst:E1} dB");
        }

        // ---- dynamics -------------------------------------------------------------------------------------------

        /// <summary>The steady gain change (dB) a sine at <paramref name="hz"/> and <paramref name="dbfs"/> peak gets through the engine.</summary>
        static double SteadyChange(Action<ParamValues> setup, double hz, double dbfs, bool viaSidechain = false, double keyDbfs = double.NaN)
        {
            var (e, _) = Make(setup);
            int n = Rate * 2;
            var x = Measure.Sine(hz, n, DspMath.DbToGain(dbfs));
            float[]? key = viaSidechain ? Measure.Sine(hz, n, DspMath.DbToGain(keyDbfs)) : null;
            var (y, _) = Run(e, x, x, key, key);
            int from = n - Rate / 2;
            return Measure.Db(Measure.Rms(y, from, Rate / 2) / Measure.Rms(x, from, Rate / 2));
        }

        [Test]
        public void TheStaticCurveIsTheGainComputers()
        {
            // A bell, a low shelf and a high shelf, dynamic down (and up), against GainComputer.Ff on the sine's peak level,
            // read as the band's design at the moved gain.
            var cases = new (string Name, BandType Type, double Hz, double TestHz, double Static, bool Up, double Ratio, double Knee, double Range)[]
            {
                ("bell down 4:1", BandType.Bell, 1000, 1000, 0, false, 4, 0, 24),
                ("bell down 2:1 knee 12", BandType.Bell, 2500, 2500, 3, false, 2, 12, 24),
                ("bell up 3:1 range 6", BandType.Bell, 500, 500, -4, true, 3, 6, 6),
                ("bell down ∞", BandType.Bell, 1000, 1000, 0, false, double.PositiveInfinity, 0, 10),
                ("low shelf down 4:1", BandType.LowShelf, 400, 60, 0, false, 4, 6, 24),
                ("high shelf down 3:1", BandType.HighShelf, 3000, 12000, 2, false, 3, 0, 24),
            };
            double worst = 0;
            string where = "";
            var gc = new List<string>();
            foreach (var c in cases)
                foreach (double level in new[] { -50.0, -36, -30, -26, -20, -14, -8 })
                {
                    const double threshold = -30;
                    double change = SteadyChange(p =>
                    {
                        EqTests.Band(p, 4, c.Type, c.Hz, c.Static, 1);
                        Dynamic(p, 4, c.Up, threshold, c.Ratio, c.Knee, c.Range, attack: 0.002, release: 0.3);
                    }, c.TestHz, level);
                    double t = Math.Min(GainComputer.Make(threshold, c.Ratio, c.Knee).Ff(level), c.Range);
                    var moved = new BandSettings { Type = c.Type, Hz = c.Hz, Q = 1, GainDb = c.Static + (c.Up ? t : -t) };
                    double want = 20 * Math.Log10(EqDesign.Magnitude(moved, c.TestHz, Rate));
                    double err = Math.Abs(change - want);
                    if (err > worst) { worst = err; where = $"{c.Name} at {level} dBFS: {change:F3} dB, the curve {want:F3} dB"; }
                }
            Measure.Report($"Neo EQ v2 dynamic static curves (6 bands × 7 levels, threshold −30 dB): within {worst:F3} dB of the gain computer ({where})");
            Assert.That(worst, Is.LessThan(0.25), where);
        }

        /// <summary>
        /// The engine's gain change over time (dB) for a sine at the band's centre: the output's envelope over the input's, both
        /// from the analytic signal made offline (an FFT, zero phase), so the measurement adds no delay of its own.
        /// </summary>
        static double[] ChangeOverTime(Action<ParamValues> setup, float[] x)
        {
            var (e, _) = Make(setup);
            var (y, _) = Run(e, x, x);
            var ey = Envelope(y);
            var ex = Envelope(x);
            var change = new double[x.Length];
            for (int i = 0; i < x.Length; i++) change[i] = 20 * Math.Log10((ey[i] + 1e-15) / (ex[i] + 1e-15));
            return change;
        }

        static double[] Envelope(float[] x)
        {
            int n = 1;
            while (n < x.Length) n <<= 1;
            var re = new double[n];
            var im = new double[n];
            for (int i = 0; i < x.Length; i++) re[i] = x[i];
            NeoBuzz.Dsp.Fft.Transform(re, im, false);
            for (int k = 1; k < n / 2; k++) { re[k] *= 2; im[k] *= 2; }
            for (int k = n / 2 + 1; k < n; k++) re[k] = im[k] = 0;
            NeoBuzz.Dsp.Fft.Transform(re, im, true);
            var env = new double[x.Length];
            for (int i = 0; i < x.Length; i++) env[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
            return env;
        }

        /// <summary>
        /// A 4 kHz sine at <paramref name="fromDb"/> dBFS, then at <paramref name="toDb"/> from <paramref name="step"/> (a 0.5 ms
        /// raised-cosine change: its sidebands stay within 2 kHz of the carrier, so the analytic envelope reads it).
        /// </summary>
        static float[] SteppedSine(int n, int step, double fromDb, double toDb)
        {
            var x = new float[n];
            double a = DspMath.DbToGain(fromDb), b = DspMath.DbToGain(toDb);
            for (int i = 0; i < n; i++)
            {
                double u = Math.Clamp((i - step) / (0.0005 * Rate) + 0.5, 0, 1);
                double g = a + (b - a) * (0.5 - 0.5 * Math.Cos(Math.PI * u));
                x[i] = (float)(g * Math.Sin(2 * Math.PI * TimingHz * i / Rate));
            }
            return x;
        }

        const double TimingHz = 4000;

        // The detector hears a band-pass (Q 1) and its envelope through the 90° pair, and the gain moves on the 16-sample grid:
        // about 0.5 ms in all at 4 kHz (a 5 ms attack reads 5.5 ms), so the attacks from 10 ms.
        [TestCase(0.01)]
        [TestCase(0.03)]
        [TestCase(0.1)]
        public void AttackReaches63PercentInItsTime(double attack)
        {
            // A 4 kHz bell (0 dB static), down 4:1 at −20 dB, hard knee: a 4 kHz sine stepping from −40 to −10 dBFS gets 7.5 dB.
            int step = Rate / 2, n = step + (int)(attack * 8 * Rate) + Rate / 10;
            var x = SteppedSine(n, step, -40, -10);
            var change = ChangeOverTime(p => { EqTests.Band(p, 4, BandType.Bell, TimingHz, 0, 1); Dynamic(p, 4, threshold: -20, ratio: 4, attack: attack, release: 1); }, x);
            int hit = -1;
            for (int i = step; i < n; i++)
                if (change[i] <= -0.632 * 7.5) { hit = i - step; break; }
            double t = hit / (double)Rate;
            double full = change[n - 1];
            Measure.Report($"Neo EQ v2 dynamic attack {attack * 1000} ms: {full:F2} dB in the end, 63 % after {t * 1000:F2} ms");
            Assert.That(full, Is.EqualTo(-7.5).Within(0.25));
            Assert.That(t / attack, Is.InRange(0.9, 1.1));
        }

        [TestCase(0.05)]
        [TestCase(0.3)]
        public void ReleaseRecovers63PercentInItsTime(double release)
        {
            int step = Rate / 2, n = step + (int)(release * 6 * Rate);
            var x = SteppedSine(n, step, -10, -40);
            var change = ChangeOverTime(p => { EqTests.Band(p, 4, BandType.Bell, TimingHz, 0, 1); Dynamic(p, 4, threshold: -20, ratio: 4, attack: 0.001, release: release); }, x);
            // From 3 ms after the step: until then the bell still rings with the louder sound (its time constant Q/(π f) is 0.3 ms).
            Assert.That(change[step + 144], Is.LessThan(-6.5), "still reduced 3 ms after the step");
            int hit = -1;
            for (int i = step + 144; i < n; i++)
                if (change[i] >= -0.368 * 7.5) { hit = i - step; break; }
            double t = hit / (double)Rate;
            Measure.Report($"Neo EQ v2 dynamic release {release * 1000} ms: back to 37 % after {t * 1000:F1} ms");
            Assert.That(t / release, Is.InRange(0.9, 1.1));
        }

        [Test]
        public void TheSidechainDrivesTheBand()
        {
            // Detector Sidechain: the main input's level doesn't move the band, the sidechain's does.
            void Setup(ParamValues p) { EqTests.Band(p, 4, BandType.Bell, 1000, 0, 1); Dynamic(p, 4, threshold: -30, ratio: 4, release: 0.3, sidechain: true); }
            double quietKey = SteadyChange(Setup, 1000, -6, viaSidechain: true, keyDbfs: -60);
            double loudKey = SteadyChange(Setup, 1000, -40, viaSidechain: true, keyDbfs: -10);
            double noKey = SteadyChange(Setup, 1000, -6);
            double want = -Math.Min(GainComputer.Make(-30, 4, 0).Ff(-10), 24);
            Measure.Report($"Neo EQ v2 sidechain: a −6 dBFS input with a −60 dBFS key {quietKey:F3} dB; a −40 dBFS input with a −10 dBFS key {loudKey:F3} dB (the curve {want:F2}); no key connected {noKey:F3} dB");
            Assert.That(Math.Abs(quietKey), Is.LessThan(0.01));
            Assert.That(Math.Abs(noKey), Is.LessThan(0.01));
            Assert.That(loudKey, Is.EqualTo(want).Within(0.25));
        }

        /// <summary>Energy above 2 kHz relative to a 220 Hz sine, dB (Hann-windowed spectrum of the last 2^16 samples).</summary>
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

        [TestCase(0.01, 0.1)]
        [TestCase(0.002, 0.02)]
        public void TheDynamicGainMovesWithoutZipper(double attack, double release)
        {
            // A 220 Hz sine through a 220 Hz bell (+6 dB static) moving down by up to 12 dB, driven by the sidechain: 220 Hz
            // switching between −50 and −6 dBFS every 50 ms: the gain swings all the time. Against the same engine designing
            // every sample (no grid, no ramps): the grid adds nothing above 2 kHz that the gain's own movement doesn't make.
            const int n = 1 << 17;
            var x = Measure.Sine(220, n, 0.5);
            var key = new float[n];
            for (int i = 0; i < n; i++) key[i] = (float)(DspMath.DbToGain((i / 2400) % 2 == 0 ? -50 : -6) * Math.Sin(2 * Math.PI * 220 * i / Rate));
            void Setup(ParamValues p) { EqTests.Band(p, 2, BandType.Bell, 220, 6, 1); Dynamic(p, 2, threshold: -40, ratio: 8, range: 12, attack: attack, release: release, sidechain: true); }
            var (e, _) = Make(Setup);
            var gains = new double[n / 256 + 1];
            var (y, _) = Run(e, x, x, key, key, before: pos => gains[pos / 256] = e.DynamicDb(2));
            var (r, _) = Make(Setup);
            r.Grid = 1;
            var (yr, _) = Run(r, x, x, key, key);
            double engine = ZipperDb(y), reference = ZipperDb(yr);
            double swing = gains.Max() - gains.Min();
            Measure.Report($"Neo EQ v2 dynamic gain swinging {swing:F1} dB every 50 ms (attack {attack * 1000} ms, release {release * 1000} ms, on 220 Hz): " +
                $"energy above 2 kHz {engine:F1} dB re the sine (designing every sample: {reference:F1} dB)");
            Assert.That(swing, Is.GreaterThan(8));
            Assert.That(engine, Is.LessThan(Math.Max(-90, reference + 1)));
        }

        // ---- the house rules ------------------------------------------------------------------------------------

        static void Everything(ParamValues p)
        {
            EqTests.Band(p, 0, BandType.LowCut, 120, slope: 1);
            Channel(p, 0, BandChannel.Side);
            EqTests.Band(p, 1, BandType.LowShelf, 120, 3, 0.7);
            Shape(p, 1, BandShape.Vintage);
            Dynamic(p, 1, threshold: -24, release: 0.2);
            EqTests.Band(p, 2, BandType.Bell, 300, -3, 1.4);
            Channel(p, 2, BandChannel.Mid);
            Dynamic(p, 2, up: true, threshold: -36, range: 6, sidechain: true);
            EqTests.Band(p, 3, BandType.Bell, 2500, 4, 2);
            Shape(p, 3, BandShape.ProportionalQ);
            Channel(p, 3, BandChannel.Left);
            EqTests.Band(p, 4, BandType.Bell, 6500, 0, 3);
            Channel(p, 4, BandChannel.Side);
            Dynamic(p, 4, threshold: -40, knee: 6, range: 9, attack: 0.001, release: 0.05);
            EqTests.Band(p, 5, BandType.HighShelf, 9000, -2, 0.7);
            Shape(p, 5, BandShape.Baxandall);
            Channel(p, 5, BandChannel.Right);
            EqTests.Band(p, 6, BandType.Bell, 700, 0, 1);
            Shape(p, 6, BandShape.AllPass);
            EqTests.Band(p, 7, BandType.HighShelf, 12000, 2, 0.7);
            Dynamic(p, 7, threshold: -45, ratio: double.PositiveInfinity, range: 4);
        }

        static string Hash(float[] l, float[] r)
        {
            var bytes = new byte[l.Length * 8];
            for (int i = 0; i < l.Length; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(8 * i), l[i]);
                BitConverter.TryWriteBytes(bytes.AsSpan(8 * i + 4), r[i]);
            }
            return Convert.ToHexString(SHA256.HashData(bytes))[..16];
        }

        [Test]
        public void TheOutputDoesntDependOnBlocks()
        {
            // Every band a v2 one (v1's bands flush their state per host block, as they always have).
            var (l, r) = LimiterTests.Drums(Rate * 2);
            var key = Measure.PinkNoise(Rate * 2, 0.3, 9);
            foreach (bool moves in new[] { false, true })
            {
                string? reference = null;
                foreach (int block in moves ? new[] { 256, 128, 16, 1 } : new[] { 256, 117, 16, 1, 200 })
                {
                    var (e, v) = Make(Everything);
                    var (yl, yr) = Run(e, l, r, key, key, block, pos =>
                    {
                        // Moves where every block size has a boundary.
                        if (!moves) return;
                        if (pos == 25600) Set(v, EqDesign.Gain(2), -6);
                        // (Not a switch of type, channel, shape or dynamic: those fade out, switch at the next host block, and
                        // fade in, as v1's type switches always have.)
                        if (pos == 51200) Set(v, EqDesign.Q(4), 1.2);
                        if (pos == 57600) Set(v, EqDesign.Range(7), 9);
                        if (pos == 64000) Set(v, EqDesign.Threshold(4), -30);
                    });
                    string h = Hash(yl, yr);
                    reference ??= h;
                    Assert.That(h, Is.EqualTo(reference), $"blocks of {block}{(moves ? ", with moves" : "")}");
                }
            }
            Measure.Report($"Neo EQ v2 (every channel, shape and dynamic kind, the sidechain): the same bits at blocks of 256, 117, 16, 1 and 200, and with moves at 256, 128, 16 and 1");
        }

        [Test]
        public void AllocatesNothingWithEverythingMoving()
        {
            var (e, v) = Make(Everything);
            var (l, r) = LimiterTests.Drums(256 * 2100);
            var key = Measure.PinkNoise(256 * 2100, 0.3, 9);
            var bl = new float[256];
            var br = new float[256];
            var kl = new float[256];
            void Block(int b)
            {
                Array.Copy(l, 256 * b, bl, 0, 256);
                Array.Copy(r, 256 * b, br, 0, 256);
                Array.Copy(key, 256 * b, kl, 0, 256);
                Set(v, EqDesign.Freq(2), 200 + (b % 50) * 4);
                if (b % 300 == 150) v.Set(EqDesign.Channel(3), (b / 300) % 5);
                if (b % 300 == 0) v.Set(P.Delta, (b / 300) % 2);
                e.Process(bl, br, kl, kl, 256);
            }
            for (int b = 0; b < 50; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 50));
            Assert.That(bytes, Is.EqualTo(0), why);
            Measure.Report("Neo EQ v2: 0 bytes allocated over 2000 blocks with every v2 feature on and moving (but the readings' dictionary, read off the audio thread)");
        }

        [Test]
        public void DeltaIsTheOutputMinusTheInput()
        {
            var (l, r) = PinkPair(Rate);
            var (eOff, _) = Make(Everything);
            var (eOn, vOn) = Make(p => { Everything(p); p.Set(P.Delta, 1); Set(p, P.Output, 0); });
            var key = Measure.PinkNoise(Rate, 0.3, 9);
            var (wl, wr) = Run(eOff, l, r, key, key);
            var (dl, dr) = Run(eOn, l, r, key, key);
            double worst = 0;
            for (int i = 0; i < l.Length; i++)
                worst = Math.Max(worst, Math.Max(Math.Abs(dl[i] - (wl[i] - l[i])), Math.Abs(dr[i] - (wr[i] - r[i]))));
            Measure.Report($"Neo EQ v2 Delta: the output minus the input within {worst:E1}");
            Assert.That(worst, Is.LessThan(1e-6));
        }

        [Test]
        public void ReadsTheGainMatchAndTheDynamicChanges()
        {
            var (e, _) = Make(p => { EqTests.Band(p, 2, BandType.HighShelf, 3000, 6, 0.7); EqTests.Band(p, 4, BandType.Bell, 1000, 0, 1); Dynamic(p, 4, threshold: -40, range: 6); });
            var (l, r) = PinkPair(Rate * 4);
            Run(e, l, r);
            var readings = e.Readings();
            Assert.That(readings.ContainsKey("gain_match_db"), Is.True);
            Assert.That(readings["gain_match_db"], Is.GreaterThan(1));
            Assert.That(readings["b5_dynamic_db"], Is.LessThan(-0.5));
            Assert.That(readings["b1_dynamic_db"], Is.EqualTo(0));
            Measure.Report($"Neo EQ v2 readings: gain_match_db {readings["gain_match_db"]:F2} dB (a +6 dB high shelf on pink noise), b5_dynamic_db {readings["b5_dynamic_db"]:F2} dB");
        }

        [Test]
        public void SwitchingChannelShapeOrDynamicFades()
        {
            // Each v2 switch on a live +12 dB band: the output's largest sample-to-sample step stays near a sine's own.
            var x = Measure.Sine(1000, Rate, 0.5);
            double sineStep = 0.5 * 2 * Math.PI * 1000 / Rate;
            foreach (var (name, move) in new (string, Action<ParamValues>)[]
            {
                ("channel", p => Channel(p, 2, BandChannel.Side)),
                ("shape", p => Shape(p, 2, BandShape.AllPass)),
                ("dynamic", p => Dynamic(p, 2, threshold: -60, range: 12)),
            })
            {
                var (e, v) = Make(p => EqTests.Band(p, 2, BandType.Bell, 1000, 12, 1));
                var (y, _) = Run(e, x, (float[])x.Clone(), before: pos => { if (pos == 256 * 80) move(v); });
                double biggest = 0;
                for (int i = 1; i < y.Length; i++) biggest = Math.Max(biggest, Math.Abs(y[i] - y[i - 1]));
                Assert.That(biggest, Is.LessThan(4.3 * sineStep), name);
            }
        }

        [Test]
        public void ExtremeSettingsStayFinite()
        {
            var (e, _) = Make(p =>
            {
                for (int b = 0; b < EqDesign.Bands; b++)
                {
                    EqTests.Band(p, b, b % 3 == 0 ? BandType.LowShelf : b % 3 == 1 ? BandType.Bell : BandType.HighShelf, 20 * Math.Pow(1000, b / 7.0), 24, 30);
                    Shape(p, b, (BandShape)(b % 4));
                    Channel(p, b, (BandChannel)(b % 5));
                    Dynamic(p, b, up: true, threshold: -60, ratio: double.PositiveInfinity, range: 24, attack: 0.00005, release: 0.005);
                }
            });
            var (l, r) = LimiterTests.Drums(Rate * 2);
            var (yl, yr) = Run(e, l, r);
            double peak = 0;
            for (int i = 0; i < yl.Length; i++)
            {
                Assert.That(float.IsFinite(yl[i]) && float.IsFinite(yr[i]), Is.True, $"sample {i}");
                peak = Math.Max(peak, Math.Max(Math.Abs(yl[i]), Math.Abs(yr[i])));
            }
            Measure.Report($"Neo EQ v2 extremes (every band +24 dB Q 30, dynamic up ∞:1 by 24 dB at −60 dB, 0.05 ms attack): finite, peak {Measure.Db(peak):F1} dBFS");
        }

        [Test]
        public void RunsFasterThanRealTimeWithEightDynamicBands()
        {
            var (e, _) = Make(p =>
            {
                for (int b = 0; b < EqDesign.Bands; b++)
                {
                    EqTests.Band(p, b, b == 0 ? BandType.LowShelf : b == 7 ? BandType.HighShelf : BandType.Bell, 40 * Math.Pow(400, b / 7.0), b % 2 == 0 ? 3 : -3, 1.5);
                    Dynamic(p, b, threshold: -36, range: 6, attack: 0.005, release: 0.1);
                }
            });
            var (l, r) = LimiterTests.Drums(Rate * 4);
            Run(e, l[..Rate], r[..Rate]);
            double speed = 0;
            for (int run = 0; run < 3; run++)
            {
                var clock = Stopwatch.StartNew();
                Run(e, l, r);
                speed = Math.Max(speed, 4 / clock.Elapsed.TotalSeconds);
            }
            Measure.Report($"Neo EQ v2 CPU, 8 dynamic bands (stereo, drums): {speed:F0}× real time (best of three 4 s runs)");
            Assert.That(speed, Is.GreaterThan(30));
        }
    }
}
