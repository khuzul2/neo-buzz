using NeoBuzz.Dsp;
using NeoBuzz.Imager;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Imager (docs/specs/mastering-tools.md §5.7): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class ImagerTests
    {
        const int Rate = Measure.Rate;

        static (ImagerEngine Engine, ParamValues Values) Make(Action<ParamValues>? setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new ImagerEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (float[] L, float[] R) Run(ImagerEngine e, float[] l, float[] r, int block = ImagerEngine.MaxBlock)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[ImagerEngine.MaxBlock];
            var br = new float[ImagerEngine.MaxBlock];
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

        /// <summary>Partly correlated pink noise: l = a + c, r = b + c with the shared part c at <paramref name="shared"/>.</summary>
        static (float[] L, float[] R) Stereo(int length, double shared = 0.6, uint seed = 7)
        {
            var a = Measure.PinkNoise(length, 0.1, seed);
            var b = Measure.PinkNoise(length, 0.1, seed + 1);
            var c = Measure.PinkNoise(length, 0.1, seed + 2);
            var l = new float[length];
            var r = new float[length];
            for (int i = 0; i < length; i++)
            {
                l[i] = (float)(a[i] + shared * c[i]);
                r[i] = (float)(b[i] + shared * c[i]);
            }
            return (l, r);
        }

        static void Settle(ImagerEngine e, float[] l, float[] r) => Run(e, l, r);

        // ---- neutral -----------------------------------------------------------------------------

        [TestCase(256)]
        [TestCase(117)]
        public void NeutralPassesTheAudioBitForBitWithNoLatency(int block)
        {
            var (l, r) = Stereo(Rate * 2);
            var (e, _) = Make();
            var (ol, or) = Run(e, l, r, block);
            Assert.That(ol, Is.EqualTo(l));
            Assert.That(or, Is.EqualTo(r));
            Assert.That(e.Latency, Is.EqualTo(0));
        }

        // ---- width -------------------------------------------------------------------------------

        [TestCase(0, 0.0)]
        [TestCase(0, 0.5)]
        [TestCase(1, 1.5)]
        [TestCase(2, 2.0)]
        [TestCase(3, 0.25)]
        [TestCase(3, 2.0)]
        public void WidthMovesTheSideAgainstTheMidExactlyPerBand(int band, double width)
        {
            var (l, r) = Stereo(Rate * 5);
            var (e0, _) = Make();
            Run(e0, l, r);
            // Neutral engine: bands unshaped, but the readings are taken at the output of the bands; force the bands on.
            var (eRef, _) = Make(v => Set(v, P.Width1 + (band + 1) % 4, 100.1));
            Run(eRef, l, r);
            double before = eRef.WidthDb(band);
            var (e, _) = Make(v => Set(v, P.Width1 + band, width * 100));
            Run(e, l, r);
            double expected = before + (width > 0 ? 20 * Math.Log10(width) : double.NegativeInfinity);
            double got = e.WidthDb(band);
            TestContext.Out.WriteLine($"band {band + 1} width {width * 100:0} %: side/mid {before:0.000} -> {got:0.000} dB (expected {(double.IsFinite(expected) ? expected : -100):0.000})");
            if (width == 0) Assert.That(got, Is.LessThan(-90));
            else Assert.That(got, Is.EqualTo(expected).Within(0.01));
        }

        // ---- Stereoize ---------------------------------------------------------------------------

        [Test]
        public void StereoizeLeavesTheMonoSumAlone()
        {
            var (l, r) = Stereo(Rate * 4, 0.9);
            // The same bands with the mid unchanged either way: one engine only widens a band, the other stereoizes all four.
            var (eA, _) = Make(v => Set(v, P.Width4, 1.01));
            var (eB, _) = Make(v => { for (int b = 0; b < 4; b++) Set(v, P.Stereo1 + b, 0.5); Set(v, P.Width4, 1.01); });
            var (al, ar) = Run(eA, l, r);
            var (bl, br) = Run(eB, l, r);
            double diff = 0, ref2 = 0;
            for (int i = Rate; i < al.Length; i++)
            {
                double ma = al[i] + (double)ar[i], mb = bl[i] + (double)br[i];
                // Width 101 % on band 4 moves only the side, so the mono sums have to be the same signal.
                diff += (ma - mb) * (ma - mb);
                ref2 += ma * ma;
            }
            double db = 10 * Math.Log10(diff / ref2 + 1e-30);
            TestContext.Out.WriteLine($"Stereoize 50 % in four bands: the mono sum differs from the unstereoized by {db:0.0} dB");
            Assert.That(db, Is.LessThan(-110));
            // And the side really is something new.
            double side = 0;
            for (int i = Rate; i < al.Length; i++) side += Math.Pow((bl[i] - br[i]) - (al[i] - ar[i]), 2);
            Assert.That(10 * Math.Log10(side / ref2), Is.GreaterThan(-20));
        }

        [Test]
        public void StereoizeOfAMonoSignalMakesItWideAndDecorrelated()
        {
            var m = Measure.PinkNoise(Rate * 5, 0.1, 3);
            var (e, _) = Make(v => { for (int b = 0; b < 4; b++) Set(v, P.Stereo1 + b, 1.0); });
            var (ol, or) = Run(e, m, (float[])m.Clone());
            double mm = 0, dd = 0, md = 0;
            for (int i = Rate; i < ol.Length; i++)
            {
                double mid = 0.5 * (ol[i] + or[i]), side = 0.5 * (ol[i] - or[i]);
                mm += mid * mid; dd += side * side; md += mid * side;
            }
            double corr = md / Math.Sqrt(mm * dd);
            double widthDb = 10 * Math.Log10(dd / mm);
            TestContext.Out.WriteLine($"Stereoize 100 % on mono noise: side/mid {widthDb:0.0} dB, mid-side correlation {corr:0.000}");
            Assert.That(widthDb, Is.GreaterThan(-6));
            Assert.That(Math.Abs(corr), Is.LessThan(0.15));
        }

        // ---- Recover sides -----------------------------------------------------------------------

        [Test]
        public void RecoverKeepsTheEnergyWhenNarrowingToMono()
        {
            var (l, r) = Stereo(Rate * 5, 0.5);
            double inE = 0;
            for (int i = Rate; i < l.Length; i++) inE += (double)l[i] * l[i] + (double)r[i] * r[i];
            double Total(bool recover)
            {
                var (e, _) = Make(v =>
                {
                    for (int b = 0; b < 4; b++) Set(v, P.Width1 + b, 0);
                    v.Set(P.Recover, recover ? 1 : 0);
                });
                var (ol, or) = Run(e, l, r);
                double o = 0;
                for (int i = Rate; i < ol.Length; i++) o += (double)ol[i] * ol[i] + (double)or[i] * or[i];
                return 10 * Math.Log10(o / inE);
            }
            double with = Total(true), without = Total(false);
            TestContext.Out.WriteLine($"narrowed to 0 %: energy {without:+0.00;-0.00} dB without Recover, {with:+0.00;-0.00} dB with it");
            Assert.That(Math.Abs(with), Is.LessThan(0.5));
            Assert.That(without, Is.LessThan(-1.0));
        }

        // ---- readings ----------------------------------------------------------------------------

        [Test]
        public void TheCorrelationReadingAgreesWithTheAnalysisLibrary()
        {
            // Band 1 only: noise under the first crossover.
            var (l, r) = Stereo(Rate * 5, 0.8, 21);
            var lp = l; var rp = r;
            var (e, _) = Make(v => { Set(v, P.Xo1, 20000); Set(v, P.Xo2, 20000); Set(v, P.Xo3, 20000); Set(v, P.Width1, 80); });
            var (ol, or) = Run(e, lp, rp);
            var tail = ol.Skip(Rate).ToArray();
            var tailR = or.Skip(Rate).ToArray();
            var m = NeoBuzz.Analysis.Stereo.Measure(tail, tailR, Rate);
            double reading = e.Correlation(0);
            TestContext.Out.WriteLine($"correlation: reading {reading:0.000}, library {m.Correlation:0.000}");
            Assert.That(reading, Is.EqualTo(m.Correlation).Within(0.03));
        }


        // ---- blocks, allocation, CPU -------------------------------------------------------------

        [Test]
        public void BlocksOfAnySizeGiveTheSameBits()
        {
            var (l, r) = Stereo(Rate * 2);
            float[] First = null!, FirstR = null!;
            foreach (int block in new[] { 256, 128, 117, 16, 1 })
            {
                var (e, _) = Make(v =>
                {
                    Set(v, P.Width3, 150); Set(v, P.Width1, 20); Set(v, P.Stereo4, 0.4); v.Set(P.Recover, 1);
                });
                var (ol, or) = Run(e, l, r, block);
                if (First == null) { First = ol; FirstR = or; }
                else
                {
                    Assert.That(ol, Is.EqualTo(First), $"block {block}");
                    Assert.That(or, Is.EqualTo(FirstR), $"block {block}");
                }
            }
        }

        [Test]
        public void NothingIsAllocatedPerBlock()
        {
            var (l, r) = Stereo(Rate * 5);
            var (e, v) = Make(c => { Set(c, P.Width2, 150); Set(c, P.Stereo3, 0.3); c.Set(P.Recover, 1); });
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                int pos = (b % 900) * 256;
                Array.Copy(l, pos, bl, 0, 256);
                Array.Copy(r, pos, br, 0, 256);
                if (b % 7 == 0) Set(v, P.Width1, 40 + (b % 13) * 10);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 60; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 60));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [TestCase(70, 45)]
        public void RunsFasterThanRealTime(double x64, double arm64)
        {
            var (l, r) = Stereo(Rate * 3);
            const int Warm = Rate;
            Throughput.Guard("Neo Imager CPU (four bands, Stereoize and Recover)", Throughput.Floor(x64, arm64), 2,
                Rate, ImagerEngine.MaxBlock, () =>
            {
                var (e, _) = Make(v => { Set(v, P.Width2, 150); Set(v, P.Width3, 20); Set(v, P.Stereo4, 0.4); v.Set(P.Recover, 1); });
                var bl = new float[ImagerEngine.MaxBlock];
                var br = new float[ImagerEngine.MaxBlock];
                void Block(int pos, int n)
                {
                    Array.Copy(l, pos, bl, 0, n);
                    Array.Copy(r, pos, br, 0, n);
                    e.Process(bl, br, n);
                }
                for (int pos = 0; pos < Warm; pos += ImagerEngine.MaxBlock) Block(pos, ImagerEngine.MaxBlock);
                return Throughput.InBlocks(ImagerEngine.MaxBlock, (pos, n) => Block(Warm + pos, n));
            }, Measure.Report);
        }
    }
}
