using NeoBuzz.Drive;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Drive (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class DriveTests
    {
        static (DriveEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new DriveEngine(v);
            e.SetSampleRate(Measure.Rate);
            return (e, v);
        }

        static int DriveRaw(double db) => Params.Table[P.Drive].Raw(db);

        /// <summary>Runs a mono signal through both channels; returns the left output.</summary>
        static float[] Run(DriveEngine e, float[] x)
        {
            var y = new float[x.Length];
            var l = new float[DriveEngine.MaxBlock];
            var r = new float[DriveEngine.MaxBlock];
            for (int pos = 0; pos < x.Length; pos += DriveEngine.MaxBlock)
            {
                int n = Math.Min(DriveEngine.MaxBlock, x.Length - pos);
                Array.Copy(x, pos, l, 0, n);
                Array.Copy(x, pos, r, 0, n);
                e.Process(l, r, n);
                Array.Copy(l, 0, y, pos, n);
                for (int i = 0; i < n; i++) Assert.That(l[i], Is.EqualTo(r[i]), "channels match");
            }
            return y;
        }

        const int Warm = 8192;

        [TestCase((int)DriveMode.Transistor, 2, -90.0)]
        [TestCase((int)DriveMode.Tape, 2, -90.0)]
        [TestCase((int)DriveMode.Diode, 2, -90.0)]
        [TestCase((int)DriveMode.Tube, 2, -90.0)]
        [TestCase((int)DriveMode.Fold, 2, -80.0)]
        [TestCase((int)DriveMode.Transistor, 1, -75.0)]
        [TestCase((int)DriveMode.Transistor, 3, -95.0)]
        public void AliasFloorAtHighDrive(int modeIndex, int quality, double limit)
        {
            var mode = (DriveMode)modeIndex;
            const int bin = 4231;   // 3098.9 Hz
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, (int)mode);
                v.Set(P.Drive, DriveRaw(30));
                v.Set(P.Quality, quality - 1);
            });
            var y = Run(e, Measure.Sine(Measure.BinFrequency(bin), Warm + Measure.N, 0.25));
            var mag = Measure.Spectrum(y, Warm);
            double floor = Measure.AliasFloorDb(mag, bin);
            TestContext.Out.WriteLine($"{mode} {1 << quality}×: alias floor {floor:F1} dB, THD {Measure.Thd(mag, bin) * 100:F1} %");
            Assert.That(floor, Is.LessThan(limit));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void MixZeroIsTheInputDelayedByTheLatency(int quality)
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mix, 0);
                v.Set(P.Drive, 1000);
                v.Set(P.Quality, quality);
            });
            var x = Measure.PinkNoise(8192, 0.2);
            var y = Run(e, x);
            int L = DriveEngine.Latency;
            double worst = 0;
            for (int i = L; i < x.Length; i++) worst = Math.Max(worst, Math.Abs(y[i] - x[i - L]));
            Assert.That(worst, Is.LessThan(1e-7));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void LatencyIsTheSameAtEveryQuality(int quality)
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Drive, 0);
                v.Set(P.AutoGain, 0);
                v.Set(P.Quality, quality);
            });
            var x = new float[1024];
            x[100] = 0.001f;
            var y = Run(e, x);
            int peak = 0;
            for (int i = 0; i < y.Length; i++) if (Math.Abs(y[i]) > Math.Abs(y[peak])) peak = i;
            Assert.That(peak - 100, Is.EqualTo(DriveEngine.Latency));
        }

        [TestCase((int)DriveMode.Tape)]
        [TestCase((int)DriveMode.Tube)]
        [TestCase((int)DriveMode.Transistor)]
        public void LowDistortionAtZeroDrive(int modeIndex)
        {
            var mode = (DriveMode)modeIndex;
            const int bin = 1365;   // ~1 kHz
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, (int)mode);
                v.Set(P.Drive, 0);
                v.Set(P.Bias, 500);
            });
            var y = Run(e, Measure.Sine(Measure.BinFrequency(bin), Warm + Measure.N, 0.25));
            var mag = Measure.Spectrum(y, Warm);
            double thd = Measure.Thd(mag, bin);
            TestContext.Out.WriteLine($"{mode}: THD {thd * 100:F3} %");
            // Tube is asymmetric by design: a little second harmonic even when clean.
            Assert.That(thd, Is.LessThan(mode == DriveMode.Tube ? 0.02 : 0.01));
        }

        [Test]
        public void TubeIsEvenOrder()
        {
            const int bin = 1365;
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, (int)DriveMode.Tube);
                v.Set(P.Drive, DriveRaw(12));
            });
            var y = Run(e, Measure.Sine(Measure.BinFrequency(bin), Warm + Measure.N, 0.25));
            var mag = Measure.Spectrum(y, Warm);
            double h2 = Measure.HarmonicDb(mag, bin, 2), h3 = Measure.HarmonicDb(mag, bin, 3);
            TestContext.Out.WriteLine($"Tube: H2 {h2:F1} dB, H3 {h3:F1} dB");
            Assert.That(h2, Is.AtLeast(h3));
        }

        [TestCase((int)DriveMode.Tape)]
        [TestCase((int)DriveMode.Tube)]
        [TestCase((int)DriveMode.Transistor)]
        [TestCase((int)DriveMode.Diode)]
        [TestCase((int)DriveMode.Fold)]
        public void AutoGainHoldsTheLevel(int modeIndex)
        {
            var mode = (DriveMode)modeIndex;
            var x = Measure.PinkNoise(48000, 0.125);
            double inRms = Measure.Rms(x, 24000, 24000);
            foreach (double db in new[] { 0.0, 12, 24, 36 })
            {
                if (mode == DriveMode.Fold && db > 24) continue;   // folded noise: band-limiting takes energy the estimate can't see
                var (e, _) = Make(v =>
                {
                    v.Set(P.Mode, (int)mode);
                    v.Set(P.Drive, DriveRaw(db));
                });
                var y = Run(e, x);
                double diff = Measure.Db(Measure.Rms(y, 24000, 24000) / inRms);
                TestContext.Out.WriteLine($"{mode} drive {db} dB: {diff:+0.00;−0.00} dB");
                Assert.That(Math.Abs(diff), Is.LessThan(1.5), $"{mode} at {db} dB");
            }
        }

        [Test]
        public void SilenceStaysSilent()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, (int)DriveMode.Tube);
                v.Set(P.Bias, 900);
                v.Set(P.Drive, 1000);
            });
            var y = Run(e, new float[4096]);
            Assert.That(Measure.Peak(y, 0, y.Length), Is.EqualTo(0));
        }

        [Test]
        public void ExtremeSettingsStayBoundedAndFinite()
        {
            var rng = new Random(3);
            var (e, v) = Make();
            var x = Measure.PinkNoise(48000, 0.7);
            for (int round = 0; round < 20; round++)
            {
                foreach (var d in Params.Table) v.Set(d.Index, rng.Next(2) == 0 ? d.Min : d.Max);
                var y = Run(e, x);
                foreach (var s in y)
                {
                    Assert.That(float.IsFinite(s), Is.True);
                    Assert.That(Math.Abs(s), Is.LessThan(64));
                }
            }
        }
    }
}
