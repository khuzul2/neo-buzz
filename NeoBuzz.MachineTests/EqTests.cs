using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.Eq;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo EQ (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class EqTests
    {
        static (EqEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Measure.Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new EqEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        /// <summary>Sets band <paramref name="b"/> (0-based) on, with a type, frequency, gain, Q and slope.</summary>
        internal static void Band(ParamValues v, int b, BandType type, double hz, double db = 0, double q = 0.7071, int slope = 0)
        {
            v.Set(EqDesign.On(b), 1);
            v.Set(EqDesign.TypeOf(b), (int)type);
            Set(v, EqDesign.Freq(b), hz);
            Set(v, EqDesign.Gain(b), db);
            Set(v, EqDesign.Q(b), q);
            v.Set(EqDesign.Slope(b), slope);
        }

        static void AllOff(ParamValues v)
        {
            for (int b = 0; b < EqDesign.Bands; b++) v.Set(EqDesign.On(b), 0);
        }

        internal static (float[] L, float[] R) Run(EqEngine e, float[] l, float[] r, Action<int> beforeBlock = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[EqEngine.MaxBlock];
            var br = new float[EqEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += EqEngine.MaxBlock)
            {
                beforeBlock?.Invoke(pos);
                int n = Math.Min(EqEngine.MaxBlock, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        /// <summary>The engine's impulse response (left channel).</summary>
        static float[] ImpulseResponse(EqEngine e, int length)
        {
            var x = new float[length];
            x[0] = 1;
            return Run(e, x, x).L;
        }

        /// <summary>The response of an impulse response at any frequency, dB (a direct DTFT).</summary>
        static double ResponseDb(float[] h, double hz, int rate)
        {
            double th = 2 * Math.PI * hz / rate;
            double wr = Math.Cos(th), wi = -Math.Sin(th);
            double zr = 1, zi = 0, sr = 0, si = 0;
            for (int n = 0; n < h.Length; n++)
            {
                sr += h[n] * zr;
                si += h[n] * zi;
                double t = zr * wr - zi * wi;
                zi = zr * wi + zi * wr;
                zr = t;
                if ((n & 4095) == 4095)
                {
                    double m = 1 / Math.Sqrt(zr * zr + zi * zi);
                    zr *= m;
                    zi *= m;
                }
            }
            return 10 * Math.Log10(sr * sr + si * si + 1e-300);
        }

        static IEnumerable<double> LogFrequencies(double lo, double hi, int count)
        {
            for (int i = 0; i < count; i++) yield return lo * Math.Pow(hi / lo, i / (double)(count - 1));
        }

        internal static readonly (string Name, Action<ParamValues> Setup)[] Curves =
        {
            ("mix curve (8 bands)", v =>
            {
                Band(v, 0, BandType.LowCut, 40, slope: 1);
                Band(v, 1, BandType.LowShelf, 120, 2.5, 0.7071);
                Band(v, 2, BandType.Bell, 320, -3, 1.4);
                Band(v, 3, BandType.Bell, 800, -1.5, 2.5);
                Band(v, 4, BandType.Notch, 2400, 0, 8);
                Band(v, 5, BandType.Bell, 4500, 2, 0.9);
                Band(v, 6, BandType.HighShelf, 10000, 3, 0.7071);
                Band(v, 7, BandType.HighCut, 18000, slope: 0);
                Set(v, P.Output, -1.5);
            }),
            ("mastering curve", v =>
            {
                AllOff(v);
                Band(v, 0, BandType.LowCut, 25, slope: 2);
                Band(v, 1, BandType.LowShelf, 80, 1, 0.6);
                Band(v, 2, BandType.Bell, 250, -0.7, 0.8);
                Band(v, 5, BandType.Bell, 3000, 0.5, 0.7);
                Band(v, 6, BandType.HighShelf, 12000, 1.5, 0.7071);
            }),
            ("extremes (Q 30 at 20 Hz, ±24 dB, 48 dB/oct)", v =>
            {
                AllOff(v);
                Band(v, 0, BandType.LowCut, 30, q: 2, slope: 2);
                Band(v, 1, BandType.Bell, 20, -24, 30);
                Band(v, 2, BandType.Bell, 1000, 24, 4);
                Band(v, 3, BandType.LowShelf, 200, -24, 2);
                Band(v, 4, BandType.HighShelf, 6000, 24, 0.3);
                Band(v, 5, BandType.Bell, 15000, -24, 0.1);
                Band(v, 7, BandType.HighCut, 12000, q: 0.5, slope: 2);
            }),
        };

        [TestCase(48000)]
        [TestCase(44100)]
        public void ResponseMatchesTheDesign(int rate)
        {
            double worst = 0;
            string where = "";
            foreach (var (name, setup) in Curves)
            {
                var (e, v) = Make(setup, rate);
                var h = ImpulseResponse(e, 1 << 17);
                foreach (double f in LogFrequencies(20, 16000, 160))
                {
                    double design = EqDesign.ResponseDb(v, f, rate);
                    if (design < -100) continue;   // below what float audio can show
                    double err = Math.Abs(ResponseDb(h, f, rate) - design);
                    if (err > worst)
                    {
                        worst = err;
                        where = $"{name} at {f:F0} Hz";
                    }
                }
            }
            Measure.Report($"Neo EQ response = design at {rate} Hz: worst {worst:F5} dB ({where}), 20 Hz – 16 kHz");
            Assert.That(worst, Is.LessThan(0.1));
        }

        [Test]
        public void LabelsAreExact()
        {
            const int rate = Measure.Rate;
            double worstBell = 0;
            foreach (var (hz, db, q) in new[] { (320.0, -3.0, 1.4), (60.0, 6.0, 0.7), (5000.0, -9.5, 3.0), (12000.0, 4.0, 1.0) })
            {
                var (e, v) = Make(p => { AllOff(p); Band(p, 2, BandType.Bell, hz, db, q); }, rate);
                var h = ImpulseResponse(e, 1 << 16);
                double f0 = v.Value(EqDesign.Freq(2));
                worstBell = Math.Max(worstBell, Math.Abs(ResponseDb(h, f0, rate) - db));
            }
            var (ls, lv) = Make(p => { AllOff(p); Band(p, 1, BandType.LowShelf, 100, 6); }, rate);
            var lh = ImpulseResponse(ls, 1 << 16);
            double lf = lv.Value(EqDesign.Freq(1));
            double lowHalf = ResponseDb(lh, lf, rate), lowFull = ResponseDb(lh, lf / 16, rate);
            var (hs, hv) = Make(p => { AllOff(p); Band(p, 6, BandType.HighShelf, 1000, -6); }, rate);
            var hh = ImpulseResponse(hs, 1 << 16);
            double hf = hv.Value(EqDesign.Freq(6));
            double highHalf = ResponseDb(hh, hf, rate), highFull = ResponseDb(hh, hf * 16, rate);
            var (ns, nv) = Make(p => { AllOff(p); Band(p, 4, BandType.Notch, 2400, 0, 8); }, rate);
            double notch = ResponseDb(ImpulseResponse(ns, 1 << 16), nv.Value(EqDesign.Freq(4)), rate);
            Measure.Report($"Neo EQ labels: bells within {worstBell:F4} dB at their frequency; low shelf +6: {lowHalf:F3} dB at its frequency, {lowFull:F3} dB 4 octaves below; high shelf −6: {highHalf:F3} / {highFull:F3} dB; notch {notch:F0} dB deep");
            Assert.That(worstBell, Is.LessThan(0.01));
            Assert.That(lowHalf, Is.EqualTo(3).Within(0.01));
            Assert.That(highHalf, Is.EqualTo(-3).Within(0.01));
            Assert.That(Math.Abs(lowFull - 6), Is.LessThan(0.1));
            Assert.That(Math.Abs(highFull + 6), Is.LessThan(0.1));
            Assert.That(notch, Is.LessThan(-60));
        }

        [Test]
        public void CutsHaveTheirSlopes()
        {
            const int rate = Measure.Rate;
            var lines = new List<string>();
            foreach (var low in new[] { true, false })
                for (int slope = 0; slope < 3; slope++)
                {
                    double fc = 1000;
                    var (e, v) = Make(p => { AllOff(p); Band(p, 0, low ? BandType.LowCut : BandType.HighCut, fc, 0, 0.7071, slope); }, rate);
                    var h = ImpulseResponse(e, 1 << 16);
                    double f = v.Value(EqDesign.Freq(0));
                    double atFc = ResponseDb(h, f, rate);
                    // The second octave past the cutoff.
                    double a = ResponseDb(h, low ? f / 2 : f * 2, rate), b = ResponseDb(h, low ? f / 4 : f * 4, rate);
                    double perOctave = a - b, nominal = 12 << slope;
                    lines.Add($"{(low ? "low" : "high")} cut {nominal} dB/oct: {atFc:F3} dB at the cutoff, {perOctave:F2} dB/oct");
                    Assert.That(atFc, Is.EqualTo(-3.0103).Within(0.05), lines[^1]);
                    Assert.That(perOctave / nominal, Is.InRange(0.97, 1.03), lines[^1]);
                }
            Measure.Report("Neo EQ cuts (Q 0.71, 1 kHz; slope over the 2nd octave past the cutoff): " + string.Join("; ", lines));
        }

        /// <summary>Energy above 2 kHz relative to a 220 Hz sine, dB (Hann-windowed spectrum).</summary>
        static double ZipperDb(float[] y)
        {
            int n = 1 << 16;
            var w = new float[n];
            int from = y.Length - n;
            for (int i = 0; i < n; i++) w[i] = y[from + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Measure.Spectrum(w, 0, n);
            int k220 = (int)Math.Round(220.0 * n / Measure.Rate), k2k = (int)Math.Round(2000.0 * n / Measure.Rate);
            double sine = 0, high = 0;
            for (int k = k220 - 40; k <= k220 + 40; k++) sine += mag[k] * mag[k];
            for (int k = k2k; k < mag.Length; k++) high += mag[k] * mag[k];
            return 10 * Math.Log10(high / sine);
        }

        [Test]
        public void ParameterMovesMakeNoZipperNoise()
        {
            const int n = 1 << 16;
            var x = Measure.Sine(220, n, 0.5);
            int blocks = n / 256;
            // A +12 dB bell swept 100 Hz → 5 kHz over the first half, then its gain +12 → −12 dB.
            double Hz(int block) => block < blocks / 2 ? 100 * Math.Pow(50, block / (blocks / 2.0)) : 5000;
            double Db(int block) => block < blocks / 2 ? 12 : 12 - 24 * (block - blocks / 2) / (blocks / 2.0);

            var (e, v) = Make(p => { AllOff(p); Band(p, 2, BandType.Bell, 100, 12, 1); });
            var (y, _) = Run(e, x, x, pos =>
            {
                Set(v, EqDesign.Freq(2), Hz(pos / 256));
                Set(v, EqDesign.Gain(2), Db(pos / 256));
            });
            double engine = ZipperDb(y);

            // The same moves on a plain RBJ biquad whose coefficients change once per block.
            var bq = new Biquad();
            var z = new float[n];
            for (int pos = 0; pos < n; pos += 256)
            {
                bq.Set(BiquadType.Peak, Hz(pos / 256), Measure.Rate, 1, Db(pos / 256));
                for (int i = 0; i < 256; i++) z[pos + i] = bq.Process(x[pos + i]);
            }
            double stepped = ZipperDb(z);
            Measure.Report($"Neo EQ moves (bell swept 100 Hz → 5 kHz, gain ±12 dB, per block, on 220 Hz): energy above 2 kHz {engine:F1} dB re the sine (a biquad switched per block: {stepped:F1} dB)");
            Assert.That(engine, Is.LessThan(-90));
            Assert.That(stepped, Is.GreaterThan(-70), "the measurement hears zipper noise when there is some");
        }

        [Test]
        public void FlatPassesTheInput()
        {
            var l = Measure.PinkNoise(24000, 0.2, 21);
            var r = Measure.PinkNoise(24000, 0.2, 22);
            var (e, _) = Make();
            var (yl, yr) = Run(e, l, r);
            double worst = 0;
            for (int i = 0; i < l.Length; i++) worst = Math.Max(worst, Math.Max(Math.Abs(yl[i] - l[i]), Math.Abs(yr[i] - r[i])));
            var (eo, _) = Make(AllOff);
            var (ol, or) = Run(eo, l, r);
            Measure.Report($"Neo EQ flat: defaults (bands 2–7 on at 0 dB) differ from the input by {worst:E1}; all bands off: bit for bit");
            Assert.That(worst, Is.LessThan(1e-6));
            Assert.That(ol, Is.EqualTo(l));
            Assert.That(or, Is.EqualTo(r));
            Assert.That(e.Latency, Is.EqualTo(0));
        }

        [Test]
        public void SwitchingTypesFadesInsteadOfClicking()
        {
            var x = Measure.Sine(100, 48000, 0.5);
            var (e, v) = Make(p => { AllOff(p); Band(p, 2, BandType.Bell, 100, 12, 1); });
            var (y, _) = Run(e, x, x, pos =>
            {
                if (pos == 12032) v.Set(EqDesign.TypeOf(2), (int)BandType.HighCut);
                if (pos == 24064) v.Set(EqDesign.On(2), 0);
                if (pos == 36096) v.Set(EqDesign.On(2), 1);
            });
            // A 100 Hz sine moves at most 2π·100/48000 × amplitude per sample; a click is a far larger step.
            double biggest = 0;
            for (int i = 4800; i < y.Length; i++) biggest = Math.Max(biggest, Math.Abs(y[i] - y[i - 1]));
            double sineStep = 2 * Math.PI * 100 / 48000 * DspMath.DbToGain(12) * 0.5;
            Measure.Report($"Neo EQ type and on/off switches: largest step {biggest:F4} (the boosted sine's own: {sineStep:F4})");
            Assert.That(biggest, Is.LessThan(1.2 * sineStep));
        }

        [Test]
        public void RunsFasterThanRealTime()
        {
            // 50× is 2 % of a core. Timed against the reference workload in the same run (Throughput.cs): 1 s played untimed, then 2 s.
            var l = Measure.PinkNoise(Measure.Rate * 3, 0.2, 31);
            var r = Measure.PinkNoise(Measure.Rate * 3, 0.2, 32);
            const int Warm = Measure.Rate;
            Throughput.Guard("Neo EQ CPU (8 bands, 48 dB/oct cuts at both ends, stereo)", Throughput.Floor(x64: 170, arm64: 110), 2, Measure.Rate, EqEngine.MaxBlock, () =>
            {
                var (e, _) = Make(v =>
                {
                    Band(v, 0, BandType.LowCut, 30, slope: 2);
                    Band(v, 7, BandType.HighCut, 18000, slope: 2);
                    for (int b = 1; b < 7; b++) Set(v, EqDesign.Gain(b), b % 2 == 0 ? 3 : -3);
                });
                var bl = new float[EqEngine.MaxBlock];
                var br = new float[EqEngine.MaxBlock];
                void Block(int pos, int n)
                {
                    Array.Copy(l, pos, bl, 0, n);
                    Array.Copy(r, pos, br, 0, n);
                    e.Process(bl, br, n);
                }
                for (int pos = 0; pos < Warm; pos += EqEngine.MaxBlock) Block(pos, EqEngine.MaxBlock);
                return Throughput.InBlocks(EqEngine.MaxBlock, (pos, n) => Block(Warm + pos, n));
            }, Measure.Report);
        }

        [Test]
        public void ExtremeSettingsStayFinite()
        {
            var rng = new Random(13);
            var (e, v) = Make();
            var l = Measure.PinkNoise(12000, 0.3, 41);
            for (int round = 0; round < 40; round++)
            {
                foreach (var d in Params.Table) v.Set(d.Index, rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) });
                var (yl, yr) = Run(e, l, l);
                foreach (var s in yl.Concat(yr))
                {
                    Assert.That(float.IsFinite(s), Is.True);
                    // Eight +24 dB bands and +24 dB out can add a lot to resonant settings: bounded, not small.
                    Assert.That(Math.Abs(s), Is.LessThan(1e7));
                }
            }
        }
    }
}
