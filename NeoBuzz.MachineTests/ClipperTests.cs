using System.Diagnostics;
using NeoBuzz.Clipper;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Clipper (docs/specs/mastering-tools.md §5.1): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class ClipperTests
    {
        static (ClipperEngine Engine, ParamValues Values) Make(Action<ParamValues>? setup = null, int rate = Measure.Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new ClipperEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (float[] L, float[] R) Run(ClipperEngine e, float[] l, float[] r, int block = ClipperEngine.MaxBlock, Action<int>? before = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[ClipperEngine.MaxBlock];
            var br = new float[ClipperEngine.MaxBlock];
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

        static float[] Run(ClipperEngine e, float[] x) => Run(e, x, (float[])x.Clone()).L;

        const int Warm = 8192;
        static readonly string[] Shapes = { "Hard", "Soft", "Tanh", "Knee" };

        // ---- the curves -------------------------------------------------------------------------

        [Test]
        public void TheCurvesAreTheirIntegralsDerivativesAndNeverPassTheCeiling()
        {
            foreach (var shape in Enum.GetValues<ClipShape>())
                foreach (double knee in new[] { 0.0, 1, 3, 6, 12 })
                {
                    var c = ClipCurve.Make(shape, knee);
                    double worstSlope = 0;
                    for (double u = -4; u <= 4; u += 0.0137)
                    {
                        double f = c.F(u);
                        Assert.That(Math.Abs(f), Is.LessThanOrEqualTo(1), $"{shape} {knee} dB at {u}");
                        Assert.That(c.F(-u), Is.EqualTo(-f).Within(1e-15), "odd");
                        const double h = 1e-6;
                        double d = (c.Integral(u + h) - c.Integral(u - h)) / (2 * h);
                        worstSlope = Math.Max(worstSlope, Math.Abs(d - f));
                    }
                    Assert.That(worstSlope, Is.LessThan(1e-6), $"{shape} {knee} dB: F' = f");
                    Assert.That(c.Integral(0), Is.EqualTo(0));
                    Assert.That((c.F(1e-4) - c.F(-1e-4)) / 2e-4, Is.EqualTo(1).Within(1e-6), "slope 1 at 0");
                }
            var k = ClipCurve.Make(ClipShape.Knee, 6);
            double t = Math.Pow(10, -6 / 20.0);
            Assert.That(k.F(t * 0.999), Is.EqualTo(t * 0.999).Within(1e-15), "straight under the knee");
            Assert.That(k.F(2 - t), Is.EqualTo(1).Within(1e-12), "the ceiling at 2 − T");
            Assert.That(ClipCurve.Make(ClipShape.Knee, 0).Shape, Is.EqualTo(ClipShape.Hard));
        }

        // ---- the ceiling --------------------------------------------------------------------------

        [TestCase(0, 1)]
        [TestCase(0, 4)]
        [TestCase(1, 4)]
        [TestCase(2, 4)]
        [TestCase(3, 8)]
        [TestCase(3, 2)]
        public void NoSampleLeavesAboveTheCeiling(int shape, int factor)
        {
            double worst = double.NegativeInfinity;
            foreach (double ceilingDb in new[] { -0.3, -6.0 })
                foreach (int bands in new[] { 1, 3 })
                    foreach (int ms in new[] { 0, 1 })
                    {
                        var (e, _) = Make(v =>
                        {
                            v.Set(P.Shape, shape);
                            v.Set(P.Oversample, (int)Math.Log2(factor));
                            Set(v, P.Drive, 18);
                            Set(v, P.Ceiling, ceilingDb);
                            v.Set(P.Bands, bands - 1);
                            v.Set(P.Stereo, ms);
                        });
                        var (l, r) = LimiterTests.Drums(Measure.Rate * 2);
                        var (ol, or) = Run(e, l, r);
                        // The ceiling as the engine has it (the parameter's value in float).
                        float c = (float)DspMath.DbToGain(Params.Table[P.Ceiling].Value(Params.Table[P.Ceiling].Raw(ceilingDb)));
                        double peak = Math.Max(Measure.Peak(ol, 0, ol.Length), Measure.Peak(or, 0, or.Length));
                        Assert.That(peak, Is.LessThanOrEqualTo(c), $"{Shapes[shape]} {factor}× ceiling {ceilingDb} dB, {bands} band(s), {(ms == 1 ? "M/S" : "L/R")}");
                        worst = Math.Max(worst, Measure.Db(peak) - ceilingDb);
                    }
            Measure.Report($"Neo Clipper {Shapes[shape]} {factor}×, +18 dB of drive on drums: the highest sample {worst:F4} dB against the ceiling (≤ 0)");
        }

        [Test]
        public void TheTruePeakOvershootIsReported()
        {
            // A clipper doesn't promise true peak (the guide puts Neo Limiter after it): how far over its ceiling it goes, per shape.
            var lines = new List<string>();
            foreach (int shape in new[] { 0, 1, 2, 3 })
                foreach (int factor in new[] { 1, 4 })
                {
                    var (e, _) = Make(v =>
                    {
                        v.Set(P.Shape, shape);
                        v.Set(P.Oversample, (int)Math.Log2(factor));
                        Set(v, P.Drive, 12);
                        Set(v, P.Ceiling, -1);
                    });
                    var (l, r) = LimiterTests.Drums(Measure.Rate * 2);
                    var (ol, _) = Run(e, l, r);
                    double tp = 20 * Math.Log10(NeoBuzz.Analysis.Loudness.TruePeak(ol)) + 1;
                    lines.Add($"{Shapes[shape]} {factor}× {tp:+0.00;-0.00} dB");
                    Assert.That(tp, Is.LessThan(3), "bounded");
                }
            Measure.Report("Neo Clipper true peak over a −1 dB ceiling, drums +12 dB: " + string.Join(", ", lines));
        }

        // ---- aliasing and distortion --------------------------------------------------------------

        [TestCase(3, 4, -90.0)]
        [TestCase(3, 8, -95.0)]
        [TestCase(3, 2, -70.0)]
        [TestCase(3, 1, -35.0)]
        [TestCase(1, 4, -95.0)]
        [TestCase(2, 4, -95.0)]
        [TestCase(0, 4, -70.0)]
        [TestCase(0, 8, -80.0)]
        public void AliasFloor(int shape, int factor, double limit)
        {
            // 3.1 kHz at +12 dB into the curve (the Knee shape at its default 3 dB knee).
            const int bin = 4231;   // 3098.9 Hz
            var (e, _) = Make(v =>
            {
                v.Set(P.Shape, shape);
                v.Set(P.Oversample, (int)Math.Log2(factor));
                Set(v, P.Drive, 12);
                Set(v, P.Ceiling, 0);
            });
            var y = Run(e, Measure.Sine(Measure.BinFrequency(bin), Warm + Measure.N, 1.0));
            var mag = Measure.Spectrum(y, Warm);
            double floor = Measure.AliasFloorDb(mag, bin);
            Measure.Report($"Neo Clipper {Shapes[shape]} {factor}×, a full-scale 3.1 kHz sine +12 dB into the ceiling: alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(limit));
        }

        [Test]
        public void DistortionAgainstTheKnee()
        {
            const int bin = 1365;   // 999.7 Hz
            var lines = new List<string>();
            foreach (double knee in new[] { 0.0, 3, 6, 12 })
                foreach (double over in new[] { -1.0, 3, 6 })
                {
                    var (e, _) = Make(v =>
                    {
                        v.Set(P.Shape, 3);
                        Set(v, P.Knee, knee);
                        Set(v, P.Ceiling, 0);
                        Set(v, P.Drive, over);
                    });
                    var y = Run(e, Measure.Sine(Measure.BinFrequency(bin), Warm + Measure.N, 1.0));
                    var mag = Measure.Spectrum(y, Warm);
                    lines.Add($"knee {knee} dB at {over:+0;-0} dB: {Measure.Db(Measure.Thd(mag, bin)):F1} dB");
                }
            Measure.Report("Neo Clipper THD (1 kHz, harmonics to 20 kHz), the Knee shape: " + string.Join("; ", lines));

            // Under the knee: straight, so THD+N at the floor.
            var (lin, _) = Make(v =>
            {
                v.Set(P.Shape, 3);
                Set(v, P.Knee, 6);
                Set(v, P.Ceiling, 0);
                Set(v, P.Drive, -8);
            });
            var yl = Run(lin, Measure.Sine(Measure.BinFrequency(bin), Warm + Measure.N, 1.0));
            var ml = Measure.Spectrum(yl, Warm);
            double noise = 0, fund = ml[bin];
            for (int k = 20; k < ml.Length; k++) if (Math.Abs(k - bin) > 3 && Measure.BinFrequency(k) <= 20000) noise += ml[k] * ml[k];
            double thdn = Measure.Db(Math.Sqrt(noise) / fund);
            Measure.Report($"Neo Clipper under the knee (6 dB knee, 8 dB under the ceiling): THD+N {thdn:F1} dB");
            Assert.That(thdn, Is.LessThan(-100));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(8)]
        public void TheResponseUnderTheKneeIsReported(int factor)
        {
            // ADAA's two-sample mean at the oversampled rate: its cost at 20 kHz.
            var (e, _) = Make(v =>
            {
                v.Set(P.Shape, 3);
                Set(v, P.Knee, 12);
                Set(v, P.Drive, -12);
                Set(v, P.Output, 12);
                v.Set(P.Oversample, (int)Math.Log2(factor));
            });
            const int bin = 27307;   // 20.0 kHz
            var y = Run(e, Measure.Sine(Measure.BinFrequency(bin), Warm + Measure.N, 0.5));
            double db = Measure.Db(Measure.Spectrum(y, Warm)[bin] / 0.5);
            Measure.Report($"Neo Clipper {factor}× under the knee: {db:F2} dB at 20 kHz");
            Assert.That(db, Is.GreaterThan(factor == 1 ? -0.05 : factor == 2 ? -2.5 : -0.6));
        }

        // ---- the house rules ------------------------------------------------------------------------

        [Test]
        public void MixZeroIsTheInputDelayedBitForBit()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mix, 0);
                Set(v, P.Drive, 24);
                v.Set(P.Bands, 2);
            });
            var (l, r) = LimiterTests.Drums(Measure.Rate);
            var (ol, or) = Run(e, l, r);
            int L = ClipperEngine.LatencyAt(Measure.Rate);
            for (int i = L; i < l.Length; i++)
            {
                Assert.That(ol[i], Is.EqualTo(l[i - L]), $"left at {i}");
                Assert.That(or[i], Is.EqualTo(r[i - L]), $"right at {i}");
            }
        }

        [Test]
        public void TheLatencyIsTheSameAtEverySetting()
        {
            int expected = ClipperEngine.LatencyAt(Measure.Rate);
            // With bands the crossovers' phase moves an impulse's peak (as an analog crossover's); one band shows the delay itself.
            foreach (int factor in new[] { 0, 1, 2, 3 })
                foreach (int bands in new[] { 0 })
                    foreach (int ms in new[] { 0, 1 })
                    {
                        var (e, _) = Make(v =>
                        {
                            v.Set(P.Oversample, factor);
                            v.Set(P.Bands, bands);
                            v.Set(P.Stereo, ms);
                            Set(v, P.Drive, -12);
                        });
                        var x = new float[2048];
                        x[300] = 0.25f;
                        var y = Run(e, x);
                        int peak = 0;
                        for (int i = 0; i < y.Length; i++) if (Math.Abs(y[i]) > Math.Abs(y[peak])) peak = i;
                        Assert.That(peak - 300, Is.EqualTo(expected), $"{1 << factor}×, {bands + 1} band(s), {(ms == 1 ? "M/S" : "L/R")}");
                    }
            Measure.Report($"Neo Clipper latency: {expected} samples at every setting ({expected * 1000.0 / Measure.Rate:F2} ms at 48 kHz)");
        }

        [Test]
        public void BandsSumBackFlatUnderTheKnee()
        {
            // Three bands below the ceiling: the Linkwitz–Riley bands sum to their allpasses, so the level is flat.
            var (e, _) = Make(v =>
            {
                v.Set(P.Bands, 2);
                Set(v, P.Drive, -12);
                Set(v, P.Output, 12);
            });
            var lines = new List<string>();
            double worst = 0;
            foreach (double hz in new[] { 40.0, 120, 500, 2000, 6000, 15000 })
            {
                e.Reset();
                int bin = (int)Math.Round(hz * Measure.N / Measure.Rate) | 1;
                var y = Run(e, Measure.Sine(Measure.BinFrequency(bin), Warm + Measure.N, 0.5));
                double db = Measure.Db(Measure.Spectrum(y, Warm)[bin] / 0.5);
                worst = Math.Max(worst, Math.Abs(db));
                lines.Add($"{hz} Hz {db:F3}");
            }
            Measure.Report("Neo Clipper three bands under the knee: " + string.Join(", ", lines) + " dB");
            Assert.That(worst, Is.LessThan(0.7), "flat but for the ADAA's mean at 4×");
        }

        [Test]
        public void BlocksDontMatter()
        {
            var (l, r) = LimiterTests.Drums(Measure.Rate);
            float[]? reference = null;
            foreach (int block in new[] { 256, 128, 117, 16 })
            {
                var (e, _) = Make(v =>
                {
                    Set(v, P.Drive, 9);
                    v.Set(P.Bands, 2);
                    v.Set(P.Stereo, 1);
                    v.Set(P.Mix, 800);
                });
                var (ol, _) = Run(e, l, r, block);
                if (reference == null) reference = ol;
                else Assert.That(ol, Is.EqualTo(reference), $"blocks of {block}");
            }
        }

        [Test]
        public void MovesDontStep()
        {
            // Drive, ceiling and band drives stepped every block on a 220 Hz sine: the change in each sample stays near the sine's own.
            var (e, v) = Make(v0 => Set(v0, P.Drive, 0));
            var x = Measure.Sine(220, Measure.Rate, 0.5);
            int k = 0;
            var (y, _) = Run(e, x, (float[])x.Clone(), 256, pos =>
            {
                Set(v, P.Drive, k % 2 == 0 ? 6 : -6);
                Set(v, P.Ceiling, k % 3 == 0 ? -3 : -0.3);
                k++;
            });
            double own = 0, step = 0;
            for (int i = 1; i < x.Length; i++) own = Math.Max(own, Math.Abs(x[i] - x[i - 1]));
            for (int i = ClipperEngine.LatencyAt(Measure.Rate) + 1; i < y.Length; i++) step = Math.Max(step, Math.Abs(y[i] - y[i - 1]));
            Measure.Report($"Neo Clipper, drive ±6 dB and ceiling stepped every block: largest step {step / own:F2}× the sine's own at +6 dB");
            Assert.That(step, Is.LessThan(2.2 * own));
        }

        [Test]
        public void DeltaIsWhatTheClipperTakesAway()
        {
            var (l, r) = LimiterTests.Drums(Measure.Rate);
            var (wet, _) = Make(v => Set(v, P.Drive, 12));
            var (on, _) = Make(v =>
            {
                Set(v, P.Drive, 12);
                v.Set(P.Delta, 1);
            });
            var (yw, _) = Run(wet, l, r);
            var (yd, _) = Run(on, l, r);
            int L = ClipperEngine.LatencyAt(Measure.Rate);
            double worst = 0;
            for (int i = L; i < l.Length; i++) worst = Math.Max(worst, Math.Abs(yd[i] - (yw[i] - l[i - L])));
            Measure.Report($"Neo Clipper Delta: the output minus the input within {worst:E1}");
            Assert.That(worst, Is.LessThan(1e-6));
        }

        [Test]
        public void GainMatchReadsTheLoudnessTheClipperAdds()
        {
            var (l, r) = LimiterTests.Drums(Measure.Rate * 5);
            var (e, _) = Make(v => Set(v, P.Drive, 9));
            var (ol, or) = Run(e, l, r);
            var a = NeoBuzz.Analysis.Loudness.Measure(l.Skip(Measure.Rate * 2).ToArray(), r.Skip(Measure.Rate * 2).ToArray(), Measure.Rate).Integrated;
            var b = NeoBuzz.Analysis.Loudness.Measure(ol.Skip(Measure.Rate * 2).ToArray(), or.Skip(Measure.Rate * 2).ToArray(), Measure.Rate).Integrated;
            var readings = e.Readings();
            double match = readings["gain_match_db"];
            Measure.Report($"Neo Clipper +9 dB drive on drums: gain_match_db {match:F2} (integrated difference {b - a:F2} LU); clip_db {readings["clip_db"]}, clipped {readings["clipped_percent"]} %");
            Assert.That(match, Is.EqualTo(b - a).Within(0.5));
            Assert.That(readings["clip_db"], Is.GreaterThan(3));
            Assert.That(NeoBuzz.Analysis.GainMatch.Match(0, match, -24, 24), Is.EqualTo(-Math.Round(match, 1)).Within(1e-9), "Match takes it back");
        }

        [Test]
        public void SilenceAfterSoundLeavesNoSubnormalsAndNaNLeavesNoState()
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Drive, 12);
                v.Set(P.Bands, 2);
                v.Set(P.Stereo, 1);
            });
            var (l, r) = LimiterTests.Drums(Measure.Rate);
            Run(e, l, r);
            var silence = new float[Measure.Rate];
            var (sl, _) = Run(e, silence, silence);
            Assert.That(sl.Skip(ClipperEngine.LatencyAt(Measure.Rate) + 4096).All(s => s == 0), Is.True, "silence out after the tail");
            Assert.That(sl.All(s => !float.IsSubnormal(s)), Is.True);
        }

        [Test]
        public void TheBlocksAllocateNothing()
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Drive, 12);
                v.Set(P.Bands, 2);
                v.Set(P.Stereo, 1);
                v.Set(P.Delta, 0);
            });
            var (l, r) = LimiterTests.Drums(256 * 2100);
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                Array.Copy(l, b * 256, bl, 0, 256);
                Array.Copy(r, b * 256, br, 0, 256);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 50; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 50));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        // The card asked 100× at 4×; the shared 4× oversampler alone (up and down, two channels) runs at about 84× on
        // the 2-core test machine, so the floors are set as the other oversampled machines' (shared runners vary).
        [TestCase(1, 4, 0, 20)]
        [TestCase(1, 1, 0, 100)]
        [TestCase(1, 8, 0, 10)]
        [TestCase(3, 4, 1, 5)]
        public void RunsFasterThanRealTime(int bands, int factor, int ms, double target)
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Drive, 9);
                v.Set(P.Bands, bands - 1);
                v.Set(P.Oversample, (int)Math.Log2(factor));
                v.Set(P.Stereo, ms);
            });
            var (l, r) = LimiterTests.Drums(Measure.Rate * 10);
            Run(e, l.Take(Measure.Rate).ToArray(), r.Take(Measure.Rate).ToArray());
            var clock = Stopwatch.StartNew();
            Run(e, l, r);
            double factorRt = 10 / clock.Elapsed.TotalSeconds;
            Measure.Report($"Neo Clipper {bands} band(s), {factor}×, {(ms == 1 ? "M/S" : "L/R")}: {factorRt:F0}× real time");
            Assert.That(factorRt, Is.GreaterThan(target));
        }
    }
}
