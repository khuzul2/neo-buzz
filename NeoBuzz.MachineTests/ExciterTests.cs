using NeoBuzz.Dsp;
using NeoBuzz.Exciter;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Exciter (docs/specs/mastering-tools.md §5.8): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class ExciterTests
    {
        const int Rate = Measure.Rate;
        static readonly string[] Modes = { "Tape", "Tube", "Transistor", "Diode", "Warm" };

        static (ExciterEngine Engine, ParamValues Values) Make(Action<ParamValues>? setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new ExciterEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (float[] L, float[] R) Run(ExciterEngine e, float[] l, float[] r, int block = ExciterEngine.MaxBlock)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[ExciterEngine.MaxBlock];
            var br = new float[ExciterEngine.MaxBlock];
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

        static (float[] L, float[] R) Noise(int length, uint seed = 5)
        {
            var a = Measure.PinkNoise(length, 0.1, seed);
            var b = Measure.PinkNoise(length, 0.1, seed + 1);
            return (a, b);
        }

        /// <summary>All the excitation in band 4 (crossovers at 100, 200, 300 Hz), the given mode, drive 24 dB.</summary>
        static Action<ParamValues> HighBand(int mode, double drive = 24, double amount = 100) => v =>
        {
            Set(v, P.Xo1, 100); Set(v, P.Xo2, 200); Set(v, P.Xo3, 300);
            v.Set(P.Mode, mode);
            Set(v, P.Drive, drive);
            Set(v, P.Amount4, amount);
        };

        static double[] ToneSpectrum(int mode, int bin, double amplitude, double drive, out float[] output)
        {
            var tone = Measure.Sine(Measure.BinFrequency(bin), Measure.N + 20000, amplitude);
            var (e, _) = Make(HighBand(mode, drive));
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            output = ol;
            return Measure.Spectrum(ol, 12000);
        }

        // ---- neutral and latency -----------------------------------------------------------------

        [TestCase(0, 0)]
        [TestCase(4, 1)]
        public void AmountZeroGivesTheInputDelayedBitForBit(int mode, int ms)
        {
            var (l, r) = Noise(Rate * 2);
            var (e, _) = Make(v => { v.Set(P.Mode, mode); v.Set(P.Stereo, ms); Set(v, P.Amount4, 0); });
            var (ol, or) = Run(e, l, r);
            int la = ExciterEngine.Latency;
            for (int i = la; i < l.Length; i++)
                if (ol[i] != l[i - la] || or[i] != r[i - la]) Assert.Fail($"differs at {i}");
            TestContext.Out.WriteLine($"latency {la} samples at {Rate} Hz");
        }

        [Test]
        public void TheLatencyIsTheSameAtEverySetting()
        {
            var (l, r) = Noise(Rate);
            var impulse = new float[Rate];
            impulse[1000] = 1;
            int first = -1;
            foreach (var mode in new[] { 0, 2, 4 })
                foreach (var amount in new[] { 0.0, 100.0 })
                {
                    var (e, _) = Make(v => { v.Set(P.Mode, mode); Set(v, P.Amount4, amount); Set(v, P.Drive, 0); Set(v, P.Xo1, 100); Set(v, P.Xo2, 200); Set(v, P.Xo3, 300); Set(v, P.Amount1, 0); });
                    var (ol, _) = Run(e, impulse, new float[Rate]);
                    int peak = 0;
                    for (int i = 0; i < ol.Length; i++) if (Math.Abs(ol[i]) > Math.Abs(ol[peak])) peak = i;
                    if (first < 0) first = peak;
                    Assert.That(peak, Is.EqualTo(1000 + ExciterEngine.Latency), $"{Modes[mode]} amount {amount}");
                }
        }

        // ---- alias and harmonics -----------------------------------------------------------------

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void AliasingIsUnder90DbAtFourTimes(int mode)
        {
            const int bin = 4233;   // 3.1 kHz, odd: aliases stay off the harmonic bins
            var mag = ToneSpectrum(mode, bin, 0.5, 24, out _);
            double floor = Measure.AliasFloorDb(mag, bin);
            TestContext.Out.WriteLine($"{Modes[mode]}: alias floor {floor:0.0} dB (3.1 kHz at 0.5, drive 24 dB, Amount 100 %)");
            Assert.That(floor, Is.LessThanOrEqualTo(-90));
        }

        [Test]
        public void WarmHasOnlyEvenHarmonics()
        {
            const int bin = 1367;   // about 1 kHz
            var mag = ToneSpectrum(4, bin, 0.5, 12, out _);
            double h2 = Measure.HarmonicDb(mag, bin, 2), h3 = Measure.HarmonicDb(mag, bin, 3), h4 = Measure.HarmonicDb(mag, bin, 4), h5 = Measure.HarmonicDb(mag, bin, 5), h7 = Measure.HarmonicDb(mag, bin, 7);
            TestContext.Out.WriteLine($"Warm: H2 {h2:0.0}, H3 {h3:0.0}, H4 {h4:0.0}, H5 {h5:0.0}, H7 {h7:0.0} dB");
            Assert.That(h2 - Math.Max(h3, Math.Max(h5, h7)), Is.GreaterThanOrEqualTo(40));
            Assert.That(h4 - Math.Max(h3, Math.Max(h5, h7)), Is.GreaterThanOrEqualTo(40));
        }

        [Test]
        public void TheHarmonicProfilePerModeIsReported()
        {
            const int bin = 1367;
            foreach (int mode in new[] { 0, 1, 2, 3, 4 })
            {
                var mag = ToneSpectrum(mode, bin, 0.5, 12, out _);
                double even = 0, odd = 0;
                for (int h = 2; h <= 9; h++)
                {
                    double a = mag[bin * h] / mag[bin];
                    if (h % 2 == 0) even += a * a; else odd += a * a;
                }
                TestContext.Out.WriteLine($"{Modes[mode]}: H2 {Measure.HarmonicDb(mag, bin, 2):0.0}, H3 {Measure.HarmonicDb(mag, bin, 3):0.0}, H5 {Measure.HarmonicDb(mag, bin, 5):0.0} dB; even/odd {10 * Math.Log10(even / odd):+0.0;-0.0} dB");
                if (mode == 0) Assert.That(10 * Math.Log10(even / odd), Is.LessThan(-20), "Tape is odd");
            }
        }

        // ---- isolation, stereo, delta, auto gain -------------------------------------------------

        [Test]
        public void ABandAtZeroLeavesTheInputAlone()
        {
            // A 60 Hz tone, all the excitation in band 4 (crossovers 200 Hz, 1 kHz, 5 kHz): the tone is far under it.
            var tone = Measure.Sine(Measure.BinFrequency(80), Measure.N + 20000, 0.5);
            var (e, _) = Make(v =>
            {
                Set(v, P.Xo1, 200); Set(v, P.Xo2, 1000); Set(v, P.Xo3, 5000);
                Set(v, P.Drive, 24); Set(v, P.Amount4, 100);
            });
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            int la = ExciterEngine.Latency;
            double err = 0, sig = 0;
            for (int i = 20000; i < ol.Length; i++)
            {
                double d = ol[i] - tone[i - la];
                err += d * d;
                sig += (double)tone[i - la] * tone[i - la];
            }
            double db = 10 * Math.Log10(err / sig + 1e-30);
            TestContext.Out.WriteLine($"60 Hz with only band 4 excited: {db:0.0} dB against the input ({Math.Sqrt(err / sig):0.0e+0})");
            Assert.That(Math.Sqrt(err / sig), Is.LessThan(1e-6));
        }

        [Test]
        public void MidSideKeepsTheMonoSumAndAddsHarmonicsToTheSide()
        {
            // Side-only material excited in M/S adds nothing to the mid: the mono sum stays the input's.
            var s = Measure.PinkNoise(Rate * 3, 0.1, 10);
            var l = (float[])s.Clone();
            var r = s.Select(x => -x).ToArray();
            var (e, _) = Make(v => { HighBand(0, 18, 100)(v); v.Set(P.Stereo, 1); });
            var (ol, or) = Run(e, l, r);
            int la = ExciterEngine.Latency;
            double mono = 0, side = 0, ref2 = 0;
            for (int i = Rate; i < l.Length; i++)
            {
                mono += Math.Pow(ol[i] + or[i], 2);
                side += Math.Pow((ol[i] - or[i]) - (l[i - la] - r[i - la]), 2);
                ref2 += Math.Pow(l[i - la] - r[i - la], 2);
            }
            TestContext.Out.WriteLine($"M/S, side-only input: mono sum {10 * Math.Log10(mono / ref2 + 1e-30):0.0} dB against the side; harmonics added to the side {10 * Math.Log10(side / ref2):0.0} dB");
            Assert.That(10 * Math.Log10(mono / ref2 + 1e-30), Is.LessThan(-100));
            Assert.That(side / ref2, Is.GreaterThan(1e-4));
        }

        [Test]
        public void DeltaIsTheOutputMinusTheInput()
        {
            var (l, r) = Noise(Rate * 3);
            var (eOn, _) = Make(v => { HighBand(0, 18, 100)(v); v.Set(P.Delta, 1); });
            var (eOff, _) = Make(v => HighBand(0, 18, 100)(v));
            var (dl, dr) = Run(eOn, l, r);
            var (ol, or) = Run(eOff, l, r);
            int la = ExciterEngine.Latency;
            double err = 0, ref2 = 0;
            for (int i = Rate; i < l.Length; i++)
            {
                double expected = ol[i] - l[i - la];
                err += Math.Pow(dl[i] - expected, 2);
                ref2 += expected * expected;
            }
            double db = 10 * Math.Log10(err / ref2 + 1e-30);
            TestContext.Out.WriteLine($"Delta against output − delayed input: {db:0.0} dB");
            Assert.That(db, Is.LessThan(-100));
        }

        [Test]
        public void AutoGainKeepsTheLoudness()
        {
            var (l, r) = Noise(Rate * 8);
            double Level(bool auto)
            {
                var (e, _) = Make(v => { HighBand(2, 24, 100)(v); Set(v, P.Xo1, 200); Set(v, P.Xo2, 1000); Set(v, P.Xo3, 2000); Set(v, P.Amount2, 100); v.Set(P.Auto, auto ? 1 : 0); });
                var (ol, or) = Run(e, l, r);
                double o = 0, i2 = 0;
                for (int i = Rate * 3; i < l.Length; i++) { o += (double)ol[i] * ol[i]; i2 += (double)l[i] * l[i]; }
                return 10 * Math.Log10(o / i2);
            }
            double off = Level(false), on = Level(true);
            TestContext.Out.WriteLine($"level against the input: auto off {off:+0.00;-0.00} dB, auto on {on:+0.00;-0.00} dB");
            Assert.That(Math.Abs(on), Is.LessThan(0.5));
        }

        // ---- blocks, allocation, CPU -------------------------------------------------------------

        [Test]
        public void BlocksOfAnySizeGiveTheSameBits()
        {
            var (l, r) = Noise(Rate * 2);
            float[] first = null!, firstR = null!;
            foreach (int block in new[] { 256, 128, 117, 16, 1 })
            {
                var (e, _) = Make(v => { HighBand(1, 20, 80)(v); Set(v, P.Amount2, 40); v.Set(P.Stereo, 1); v.Set(P.Auto, 1); });
                var (ol, or) = Run(e, l, r, block);
                if (first == null) { first = ol; firstR = or; }
                else
                {
                    Assert.That(ol, Is.EqualTo(first), $"block {block}");
                    Assert.That(or, Is.EqualTo(firstR), $"block {block}");
                }
            }
        }

        [Test]
        public void NothingIsAllocatedPerBlock()
        {
            var (l, r) = Noise(Rate * 5);
            var (e, v) = Make(c => { HighBand(4, 20, 80)(c); Set(c, P.Amount2, 40); c.Set(P.Auto, 1); });
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                int pos = (b % 900) * 256;
                Array.Copy(l, pos, bl, 0, 256);
                Array.Copy(r, pos, br, 0, 256);
                if (b % 7 == 0) { Set(v, P.Amount4, 20 + (b % 11) * 7); v.Set(P.Mode, b % 5); }
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 60; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 60));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [TestCase(0, 16, 10)]
        [TestCase(4, 32, 20)]
        public void RunsFasterThanRealTime(int mode, double x64, double arm64)
        {
            var (l, r) = Noise(Rate * 3);
            const int Warm = Rate;
            Throughput.Guard($"Neo Exciter CPU ({Modes[mode]}, four bands)", Throughput.Floor(x64, arm64), 2,
                Rate, ExciterEngine.MaxBlock, () =>
            {
                var (e, _) = Make(v => { v.Set(P.Mode, mode); Set(v, P.Amount1, 20); Set(v, P.Amount2, 30); Set(v, P.Amount3, 40); Set(v, P.Amount4, 50); });
                var bl = new float[ExciterEngine.MaxBlock];
                var br = new float[ExciterEngine.MaxBlock];
                void Block(int pos, int n)
                {
                    Array.Copy(l, pos, bl, 0, n);
                    Array.Copy(r, pos, br, 0, n);
                    e.Process(bl, br, n);
                }
                for (int pos = 0; pos < Warm; pos += ExciterEngine.MaxBlock) Block(pos, ExciterEngine.MaxBlock);
                return Throughput.InBlocks(ExciterEngine.MaxBlock, (pos, n) => Block(Warm + pos, n));
            }, Measure.Report);
        }
    }
}
