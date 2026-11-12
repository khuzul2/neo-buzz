using NeoBuzz.Dsp;
using NeoBuzz.Tape;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Tape (docs/specs/mastering-tools.md §5.11): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class TapeTests
    {
        const int Rate = Measure.Rate;
        static readonly string[] Speeds = { "7.5 ips", "15 ips", "30 ips" };

        static (TapeEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new TapeEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (float[] L, float[] R) Run(TapeEngine e, float[] l, float[] r, int block = TapeEngine.MaxBlock)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[TapeEngine.MaxBlock];
            var br = new float[TapeEngine.MaxBlock];
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

        /// <summary>No colour except what is asked for: a flat setup (no bump, loss, emphasis, electronics), so only the saturation acts.</summary>
        static Action<ParamValues> Clean(double drive = 0, bool high = true) => v =>
        {
            Set(v, P.Drive, drive);
            Set(v, P.Bump, 0);
            Set(v, P.Loss, 0);
            Set(v, P.Emphasis, 0);
            Set(v, P.Electronics, 0);
            v.Set(P.Quality, high ? 1 : 0);
        };

        /// <summary>The level of <paramref name="hz"/> in the last <paramref name="n"/> samples (a projection), amplitude.</summary>
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

        // ---- neutral and latency -------------------------------------------------------------------

        [TestCase(1, 0, 0)]
        [TestCase(0, 0, 0)]
        [TestCase(1, 10, 5)]
        [TestCase(0, 10, 0)]
        public void MixZeroGivesTheInputDelayedBitForBit(int high, int wow, int flutter)
        {
            var l = Measure.PinkNoise(Rate * 2, 0.1, 3);
            var r = Measure.PinkNoise(Rate * 2, 0.1, 4);
            var (e, _) = Make(v => { Set(v, P.Mix, 0); v.Set(P.Quality, high); Set(v, P.Wow, wow); Set(v, P.Flutter, flutter); Set(v, P.Drive, 12); });
            var (ol, or) = Run(e, l, r);
            int la = e.Latency;
            for (int i = la; i < l.Length; i++)
                if (ol[i] != l[i - la] || or[i] != r[i - la]) Assert.Fail($"differs at {i}");
            TestContext.Out.WriteLine($"{(high == 1 ? "High" : "Live")}, wow {wow} ct, flutter {flutter} ct: latency {la} samples");
        }

        [Test]
        public void TheLatencyIsReportedAndMeasured()
        {
            foreach (var (high, wow, expected) in new[] { (true, 0.0, TapeEngine.OversampleLatency(true)), (false, 0.0, 0), (true, 0.1, TapeEngine.OversampleLatency(true) + 250), (false, 0.1, 250) })
            {
                var (e, _) = Make(v => { Clean(0, high)(v); Set(v, P.Wow, wow); Set(v, P.WowRate, 2); Set(v, P.Drive, 0); });   // a wow of 0.1 ct at 2 Hz moves the read a fifth of a sample
                var impulse = new float[Rate];
                impulse[3000] = 1e-3f;                               // small: the linear response
                var (ol, _) = Run(e, impulse, new float[Rate]);
                int peak = 0;
                for (int i = 0; i < ol.Length; i++) if (Math.Abs(ol[i]) > Math.Abs(ol[peak])) peak = i;
                TestContext.Out.WriteLine($"{(high ? "High" : "Live")}, wow {wow}: reported {e.Latency}, impulse peak {peak - 3000}");
                Assert.That(e.Latency, Is.EqualTo(expected));
                Assert.That(peak - 3000, Is.EqualTo(e.Latency).Within(1), "the response's peak sits at the latency (the ADAA adds half a sample)");
            }
        }

        // ---- small-signal response -------------------------------------------------------------------

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void TheSmallSignalResponseIsTheDesignedOneAtEverySpeed(int speed)
        {
            double worst = 0, worstHz = 0;
            double[] hz = { 31.5, 50, 80, 125, 200, 315, 500, 800, 1250, 2000, 3150, 5000, 6300, 8000, 10000, 12500, 16000 };
            foreach (double f in hz)
            {
                var (e, _) = Make(v => { v.Set(P.Speed, speed); Set(v, P.Drive, 12); Set(v, P.Bump, 2); Set(v, P.Loss, 4); Set(v, P.Emphasis, 6); Set(v, P.Electronics, 0); Set(v, P.Bias, 0); });
                int n = Rate * 3;
                var tone = Measure.Sine(f, n, 0.001);
                var (ol, _) = Run(e, tone, (float[])tone.Clone());
                int len = (int)(Math.Round(f * 1.0 / (Rate / (double)Rate)) > 0 ? Rate : Rate);
                double got = Measure.Db(Amplitude(ol, f, Rate * 2, len) / 0.001);
                double want = TapeEngine.DesignDb(speed, 2, 4, f, Rate);
                if (Math.Abs(got - want) > worst) { worst = Math.Abs(got - want); worstHz = f; }
            }
            Measure.Report($"Neo Tape {Speeds[speed]}: small-signal response within {worst:0.000} dB of the design from 31.5 Hz to 16 kHz (worst at {worstHz} Hz)");
            Assert.That(worst, Is.LessThanOrEqualTo(0.1));
        }

        [Test]
        public void LiveSkipsTheDroopCompensationAndReportsIt()
        {
            double[] hz = { 1000, 5000, 10000, 16000 };
            var sb = new List<string>();
            foreach (double f in hz)
            {
                var (e, _) = Make(v => { Clean(12, false)(v); });
                var tone = Measure.Sine(f, Rate * 3, 0.001);
                var (ol, _) = Run(e, tone, (float[])tone.Clone());
                sb.Add($"{f / 1000:0.#} kHz {Measure.Db(Amplitude(ol, f, Rate * 2, Rate) / 0.001):+0.00;-0.00} dB");
            }
            Measure.Report("Neo Tape Live (no oversampling, ADAA at the base rate, flat settings): " + string.Join(", ", sb));
            Assert.Pass();
        }

        // ---- saturation, alias, bias, electronics ---------------------------------------------------

        [Test]
        public void TheHarmonicsGrowWithTheLevelAndTheSaturation()
        {
            const int bin = 1367;
            double prev = -200;
            foreach (double amp in new[] { 0.03, 0.1, 0.3, 0.7 })
            {
                var tone = Measure.Sine(Measure.BinFrequency(bin), Measure.N + 20000, amp);
                var (e, _) = Make(Clean(12));
                var (ol, _) = Run(e, tone, (float[])tone.Clone());
                var mag = Measure.Spectrum(ol, 12000);
                double thd = 20 * Math.Log10(Measure.Thd(mag, bin));
                Measure.Report($"Neo Tape THD at {Measure.Db(amp):0.0} dBFS (saturation 12 dB, 15 ips, flat): {thd:0.0} dB (H3 {Measure.HarmonicDb(mag, bin, 3):0.0} dB)");
                Assert.That(thd, Is.GreaterThan(prev), "more level, more distortion");
                prev = thd;
            }
        }

        static double AliasFloor(int speed, double drive, double emphasis, double bias, double electronics)
        {
            const int bin = 4233;   // 3.1 kHz, odd: aliases stay off the harmonic bins
            // 4 s of lead-in: the electronics' DC and the bias's decay through the 2 Hz DC blocker (80 ms) before the window.
            var tone = Measure.Sine(Measure.BinFrequency(bin), Measure.N + 200000, 0.5);
            var (e, _) = Make(v => { Clean(drive)(v); v.Set(P.Speed, speed); Set(v, P.Electronics, electronics); Set(v, P.Bias, bias); Set(v, P.Emphasis, emphasis); });
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            return Measure.AliasFloorDb(Measure.Spectrum(ol, 192000), bin);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void AliasingIsUnder90DbAtFourTimes(int speed)
        {
            double floor = AliasFloor(speed, 24, 6, 0.4, 0.5);
            Measure.Report($"Neo Tape {Speeds[speed]}: alias floor {floor:0.0} dB (3.1 kHz at 0.5, saturation 24 dB, emphasis 6 dB, bias +40 %, electronics 50 %)");
            Assert.That(floor, Is.LessThanOrEqualTo(-90));
        }

        [Test]
        public void AliasingAtTheExtremesIsReported()
        {
            // Saturation 24 dB with the emphasis at 12 dB drives a 3.1 kHz tone 30× into the curve (a square wave): first-order ADAA at 4× is not enough there.
            double hard = AliasFloor(1, 24, 12, 0, 0), mild = AliasFloor(1, 12, 6, 0, 0), flat = AliasFloor(1, 24, 0, 0, 0);
            Measure.Report($"Neo Tape alias floor: saturation 12 dB, emphasis 6 dB {mild:0.0} dB; saturation 24 dB, no emphasis {flat:0.0} dB; saturation 24 dB, emphasis 12 dB (30× into the curve) {hard:0.0} dB");
            Assert.That(mild, Is.LessThanOrEqualTo(-90));
            Assert.That(hard, Is.LessThan(-75), "known limit: the heaviest setting");
        }

        [Test]
        public void BiasAndElectronicsMakeEvenHarmonicsAndASymmetricTapeDoesNot()
        {
            const int bin = 1367;
            double H2(Action<ParamValues> s)
            {
                var tone = Measure.Sine(Measure.BinFrequency(bin), Measure.N + 20000, 0.3);
                var (e, _) = Make(v => { Clean(12)(v); s(v); });
                var (ol, _) = Run(e, tone, (float[])tone.Clone());
                return Measure.HarmonicDb(Measure.Spectrum(ol, 12000), bin, 2);
            }
            double plain = H2(v => { }), biased = H2(v => Set(v, P.Bias, 0.5)), electronics = H2(v => Set(v, P.Electronics, 1));
            Measure.Report($"Neo Tape H2 at 0.3, saturation 12 dB: plain {plain:0.0} dB, bias +50 % {biased:0.0} dB, electronics 100 % {electronics:0.0} dB");
            Assert.That(plain, Is.LessThan(-100), "tanh is odd");
            Assert.That(biased, Is.GreaterThan(plain + 40));
            Assert.That(electronics, Is.GreaterThan(plain + 40));
        }

        [Test]
        public void TheBiasLeavesNoDcAndTheSmallSignalGainAlone()
        {
            var tone = Measure.Sine(1000, Rate * 3, 0.001);
            var (e, _) = Make(v => { Clean(12)(v); Set(v, P.Bias, 0.8); });
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            double mean = 0;
            for (int i = Rate * 2; i < Rate * 3; i++) mean += ol[i];
            mean /= Rate;
            Assert.That(Math.Abs(mean), Is.LessThan(1e-6));
            Assert.That(Measure.Db(Amplitude(ol, 1000, Rate * 2, Rate) / 0.001), Is.EqualTo(0).Within(0.05));
        }

        // ---- hiss ---------------------------------------------------------------------------------------

        [TestCase(0, 3)]
        [TestCase(1, 0)]
        [TestCase(2, -3)]
        public void HissIsAtItsLevelPerSpeed(int speed, double offset)
        {
            var silent = new float[Rate * 3];
            var (e, _) = Make(v => { v.Set(P.Speed, speed); Set(v, P.Noise, -60); Set(v, P.Bump, 0); Set(v, P.Loss, 0); });
            var (ol, _) = Run(e, silent, new float[Rate * 3]);
            double got = Measure.Db(Measure.Rms(ol, Rate, Rate * 2));
            TestContext.Out.WriteLine($"{Speeds[speed]}: hiss {got:0.00} dBFS rms against {-60 + offset:0.0}");
            Assert.That(got, Is.EqualTo(-60 + offset).Within(0.3));
        }

        [Test]
        public void HissOffIsExactSilenceAndDeterministic()
        {
            var silent = new float[Rate];
            var (e, _) = Make(v => { Set(v, P.Drive, 12); });
            var (ol, _) = Run(e, silent, new float[Rate]);
            Assert.That(Measure.Peak(ol, 0, Rate), Is.EqualTo(0));
            var (e1, _) = Make(v => Set(v, P.Noise, -60));
            var (e2, _) = Make(v => Set(v, P.Noise, -60));
            Assert.That(Run(e1, silent, new float[Rate]).L, Is.EqualTo(Run(e2, silent, new float[Rate]).L));
        }

        // ---- wow and flutter ----------------------------------------------------------------------------

        /// <summary>The frequency of a 1 kHz tone over time from its zero crossings (linear interpolation), in cents against 1 kHz, one value per crossing.</summary>
        static (double[] T, double[] Cents) Pitch(float[] x, int from, int to, double hz)
        {
            var t = new List<double>();
            var c = new List<double>();
            double last = double.NaN;
            for (int i = from + 1; i < to; i++)
            {
                if (x[i - 1] < 0 && x[i] >= 0)
                {
                    double pos = i - 1 + -x[i - 1] / (double)(x[i] - x[i - 1]);
                    if (!double.IsNaN(last))
                    {
                        double f = Rate / (pos - last);
                        t.Add(pos / Rate);
                        c.Add(1200 * Math.Log2(f / hz));
                    }
                    last = pos;
                }
            }
            return (t.ToArray(), c.ToArray());
        }

        [TestCase(8.0, 0.8)]
        [TestCase(20.0, 0.8)]
        [TestCase(15.0, 1.5)]
        public void WowDepthInCentsIsTheSetting(double cents, double rate)
        {
            int n = Rate * 6;
            var tone = Measure.Sine(1000, n, 0.3);
            var (e, _) = Make(v => { Clean(0)(v); Set(v, P.Wow, cents); Set(v, P.WowRate, rate); });
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            var (t, c) = Pitch(ol, Rate, n - 1, 1000);
            // Smooth over 5 crossings (5 ms) against the interpolation noise, then take the extremes.
            double max = double.MinValue, min = double.MaxValue;
            for (int i = 2; i < c.Length - 2; i++)
            {
                double m = (c[i - 2] + c[i - 1] + c[i] + c[i + 1] + c[i + 2]) / 5;
                max = Math.Max(max, m); min = Math.Min(min, m);
            }
            double depth = (max - min) / 2;
            Measure.Report($"Neo Tape wow {cents} ct at {rate} Hz: measured peak deviation {depth:0.00} ct ({100 * (depth / cents - 1):+0.0;-0.0}%)");
            Assert.That(depth, Is.EqualTo(cents).Within(0.05 * cents + 0.2));
        }

        [Test]
        public void FlutterStaysWithinItsSettingAndWowAndFlutterOffAddNothing()
        {
            int n = Rate * 4;
            var tone = Measure.Sine(1000, n, 0.3);
            var (e, _) = Make(v => { Clean(0)(v); Set(v, P.Flutter, 10); });
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            var (_, c) = Pitch(ol, Rate, n - 1, 1000);
            double max = c.Max(), min = c.Min();
            Measure.Report($"Neo Tape flutter 10 ct: single-period deviation {min:0.0} … {max:0.0} ct");
            Assert.That(Math.Max(max, -min), Is.LessThan(10 * 1.4 + 2).And.GreaterThan(4), "single periods read a little over the smoothed peak");
            var (e0, _) = Make(v => Clean(0)(v));
            Assert.That(e0.Latency, Is.EqualTo(TapeEngine.OversampleLatency(true)));
        }

        // ---- blocks, delta, allocation, CPU --------------------------------------------------------------

        [Test]
        public void BlocksOfAnySizeGiveTheSameBitsAndRunsRepeat()
        {
            var l = Measure.PinkNoise(Rate * 2, 0.3, 11);
            var r = Measure.PinkNoise(Rate * 2, 0.3, 12);
            float[] first = null;
            Action<ParamValues> all = v => { Set(v, P.Drive, 14); Set(v, P.Bias, 0.3); Set(v, P.Electronics, 0.5); Set(v, P.Noise, -70); Set(v, P.Wow, 12); Set(v, P.Flutter, 6); };
            foreach (int block in new[] { 256, 117, 16 })
            {
                var (e, _) = Make(all);
                var (ol, _) = Run(e, l, r, block);
                if (first == null) first = ol;
                else Assert.That(ol, Is.EqualTo(first), $"block {block}");
            }
            var (e2, _) = Make(all);
            Assert.That(Run(e2, l, r).L, Is.EqualTo(first), "a second run");
        }

        [Test]
        public void DeltaIsWhatTheTapeAdds()
        {
            var tone = Measure.Sine(1000, Rate * 3, 0.5);
            var (e, _) = Make(v => { Clean(12)(v); v.Set(P.Delta, 1); });
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            var (e2, _) = Make(v => Clean(12)(v));
            var (wet, _) = Run(e2, tone, (float[])tone.Clone());
            int la = e.Latency;
            double worst = 0;
            for (int i = Rate * 2; i < Rate * 3; i++) worst = Math.Max(worst, Math.Abs(ol[i] - (wet[i] - tone[i - la])));
            Assert.That(worst, Is.LessThan(2e-6));
        }

        [Test]
        public void NothingIsAllocatedPerBlock()
        {
            var l = Measure.PinkNoise(256 * 8, 0.3, 5);
            var r = Measure.PinkNoise(256 * 8, 0.3, 6);
            var (e, v) = Make(x => { Set(x, P.Drive, 14); Set(x, P.Electronics, 0.4); Set(x, P.Noise, -70); Set(x, P.Wow, 10); Set(x, P.Flutter, 5); });
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                if (b % 30 == 0) Set(v, P.Drive, 6 + (b / 30 % 6) * 3);
                if (b % 50 == 0) v.Set(P.Speed, (b / 50) % 3);
                if (b % 90 == 0) Set(v, P.Bias, ((b / 90) % 3 - 1) * 0.4);
                if (b % 200 == 0) v.Set(P.Quality, (b / 200) % 2);
                if (b % 120 == 0) Set(v, P.Wow, (b / 120) % 2 == 0 ? 10 : 0);
                Array.Copy(l, (b % 8) * 256, bl, 0, 256);
                Array.Copy(r, (b % 8) * 256, br, 0, 256);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 60; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2400, b => Block(b + 60));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [TestCase(1, 32, 22)]
        [TestCase(0, 90, 62)]
        public void RunsFasterThanRealTime(int high, double x64, double arm64)
        {
            int n = Rate * 3;
            var l = Measure.PinkNoise(n, 0.3, 1);
            var r = Measure.PinkNoise(n, 0.3, 2);
            const int Warm = Rate;
            Throughput.Guard($"Neo Tape CPU ({(high == 1 ? "High, 4×" : "Live")}, wow and flutter, hiss, electronics)", Throughput.Floor(x64, arm64), 2,
                Rate, TapeEngine.MaxBlock, () =>
            {
                var (e, _) = Make(v => { v.Set(P.Quality, high); Set(v, P.Drive, 12); Set(v, P.Electronics, 0.3); Set(v, P.Noise, -70); Set(v, P.Wow, 8); Set(v, P.Flutter, 4); });
                var bl = new float[TapeEngine.MaxBlock];
                var br = new float[TapeEngine.MaxBlock];
                void Block(int pos, int len)
                {
                    Array.Copy(l, pos, bl, 0, len);
                    Array.Copy(r, pos, br, 0, len);
                    e.Process(bl, br, len);
                }
                for (int pos = 0; pos < Warm; pos += TapeEngine.MaxBlock) Block(pos, TapeEngine.MaxBlock);
                return Throughput.InBlocks(TapeEngine.MaxBlock, (pos, len) => Block(Warm + pos, len));
            }, Measure.Report);
        }
    }
}
