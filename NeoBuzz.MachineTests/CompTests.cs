using NeoBuzz.Comp;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Comp (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class CompTests
    {
        static (CompEngine Engine, ParamValues Values) Make(Action<ParamValues> setup)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Knee, 0);
            v.Set(P.Ratio, Params.Table[P.Ratio].Raw(4));
            v.Set(P.Threshold, Params.Table[P.Threshold].Raw(-20));
            setup?.Invoke(v);
            var e = new CompEngine(v);
            e.SetSampleRate(Measure.Rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (float[] L, float[] R) Run(CompEngine e, float[] l, float[] r, float[] keyL = null, float[] keyR = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[CompEngine.MaxBlock];
            var br = new float[CompEngine.MaxBlock];
            var kl = keyL != null ? new float[CompEngine.MaxBlock] : null;
            var kr = keyR != null ? new float[CompEngine.MaxBlock] : null;
            for (int pos = 0; pos < l.Length; pos += CompEngine.MaxBlock)
            {
                int n = Math.Min(CompEngine.MaxBlock, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                if (kl != null) Array.Copy(keyL, pos, kl, 0, n);
                if (kr != null) Array.Copy(keyR, pos, kr, 0, n);
                e.Process(bl, br, kl, kr, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static float[] Constant(double db, int n)
        {
            var x = new float[n];
            float a = (float)DspMath.DbToGain(db);
            for (int i = 0; i < n; i++) x[i] = a;
            return x;
        }

        static double Ideal(double inDb, double threshold, double ratio) =>
            inDb <= threshold ? inDb : threshold + (inDb - threshold) / ratio;

        [TestCase((int)CompMode.Clean)]
        [TestCase((int)CompMode.Fet)]
        [TestCase((int)CompMode.Glue)]
        public void StaticCurveMatchesTheIdeal(int modeIndex)
        {
            double worst = 0;
            for (double inDb = -60; inDb <= 0; inDb += 6)
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.Mode, modeIndex);
                    Set(v, P.Attack, 0.0005);
                    Set(v, P.Release, 1.0);
                });
                // A steady level: DC for the peak detectors, a 1 kHz sine for the RMS one (Glue).
                float[] x = (CompMode)modeIndex == CompMode.Glue
                    ? Measure.Sine(1000, 48000, DspMath.DbToGain(inDb))
                    : Constant(inDb, 48000);
                var (y, _) = Run(e, x, x);
                double outDb = (CompMode)modeIndex == CompMode.Glue
                    ? Measure.Db(Measure.Peak(y, 40000, 4800))
                    : Measure.Db(Math.Abs(y[47999]));
                double err = outDb - Ideal(inDb, -20, 4);
                worst = Math.Max(worst, Math.Abs(err));
                TestContext.Out.WriteLine($"{(CompMode)modeIndex} in {inDb} dB → {outDb:F2} dB (ideal {Ideal(inDb, -20, 4):F2})");
            }
            Assert.That(worst, Is.LessThan(0.25));
        }

        [TestCase(0.001)]
        [TestCase(0.01)]
        [TestCase(0.1)]
        public void AttackReaches63PercentInItsTime(double attack)
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Attack, attack);
                Set(v, P.Release, 1.0);
            });
            int n = (int)(attack * 10 * Measure.Rate) + 4800;
            var x = Constant(-40, n);
            for (int i = 2400; i < n; i++) x[i] = (float)DspMath.DbToGain(-10);
            var (y, _) = Run(e, x, x);
            double full = 7.5;   // (−10 − (−20)) · (1 − 1/4)
            int hit = -1;
            for (int i = 2400; i < n; i++)
            {
                double gr = -Measure.Db(y[i] / x[i]);
                if (gr >= 0.632 * full) { hit = i - 2400; break; }
            }
            double t = hit / (double)Measure.Rate;
            TestContext.Out.WriteLine($"attack {attack * 1000} ms: 63 % after {t * 1000:F3} ms");
            Assert.That(t / attack, Is.InRange(0.9, 1.1));
        }

        [TestCase(0.05)]
        [TestCase(0.3)]
        [TestCase(2.0)]
        public void ReleaseRecovers63PercentInItsTime(double release)
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Attack, 0.0005);
                Set(v, P.Release, release);
            });
            int n = 4800 + (int)(release * 5 * Measure.Rate);
            var x = Constant(-10, n);
            for (int i = 4800; i < n; i++) x[i] = (float)DspMath.DbToGain(-40);
            var (y, _) = Run(e, x, x);
            double full = 7.5;
            int hit = -1;
            for (int i = 4800; i < n; i++)
            {
                double gr = -Measure.Db(y[i] / x[i]);
                if (gr <= 0.368 * full) { hit = i - 4800; break; }
            }
            double t = hit / (double)Measure.Rate;
            TestContext.Out.WriteLine($"release {release * 1000} ms: back to 37 % after {t * 1000:F1} ms");
            Assert.That(t / release, Is.InRange(0.9, 1.1));
        }

        [Test]
        public void BelowThresholdIsTransparent()
        {
            const int bin = 1365;
            var (e, _) = Make(v => v.Set(P.Mode, (int)CompMode.Clean));
            var x = Measure.Sine(Measure.BinFrequency(bin), 8192 + Measure.N, DspMath.DbToGain(-22));
            var (y, _) = Run(e, x, x);
            var mag = Measure.Spectrum(y, 8192);
            double thd = Measure.Db(Measure.Thd(mag, bin));
            double gain = Measure.Db(mag[bin] / DspMath.DbToGain(-22));
            TestContext.Out.WriteLine($"THD {thd:F1} dB, gain {gain:F4} dB");
            Assert.That(thd, Is.LessThan(-100));
            Assert.That(Math.Abs(gain), Is.LessThan(0.01));
        }

        [Test]
        public void FullStereoLinkGivesBothChannelsTheSameGain()
        {
            var (e, _) = Make(v => v.Set(P.Link, 1000));
            var l = Measure.Sine(500, 24000, 0.9);
            var r = Measure.Sine(700, 24000, 0.05);
            var (yl, yr) = Run(e, l, r);
            for (int i = 0; i < l.Length; i += 37)
            {
                if (Math.Abs(l[i]) < 0.05 || Math.Abs(r[i]) < 0.005) continue;
                Assert.That(yl[i] / l[i], Is.EqualTo(yr[i] / r[i]).Within(1e-4), $"sample {i}");
            }
        }

        [Test]
        public void MixZeroIsTheInputDelayedByTheLookahead()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mix, 0);
                Set(v, P.Lookahead, 2);
                Set(v, P.Threshold, -50);
                v.Set(P.Character, 1000);
            });
            var x = Measure.PinkNoise(8192, 0.3);
            var (y, _) = Run(e, x, x);
            int L = e.Latency;
            Assert.That(L, Is.EqualTo(96));
            double worst = 0;
            for (int i = L; i < x.Length; i++) worst = Math.Max(worst, Math.Abs(y[i] - x[i - L]));
            Assert.That(worst, Is.LessThan(1e-7));
        }

        [Test]
        public void LookaheadCatchesThePeak()
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Lookahead, 5);
                v.Set(P.Ratio, 1000);   // ∞
                Set(v, P.Attack, 0.00005);
                Set(v, P.Release, 0.2);
            });
            var x = Constant(-40, 9600);
            for (int i = 4800; i < 9600; i++) x[i] = 1f;
            var (y, _) = Run(e, x, x);
            double peak = Measure.Db(Measure.Peak(y, 4800, 4800));
            TestContext.Out.WriteLine($"peak {peak:F2} dB with threshold −20");
            Assert.That(peak, Is.LessThan(-19));
        }

        [Test]
        public void ExternalSidechainDucks()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Sidechain, 1);
                Set(v, P.Attack, 0.001);
            });
            var x = Measure.Sine(220, 24000, 0.3);
            var key = new float[24000];
            for (int i = 12000; i < 24000; i++) key[i] = 0.9f;
            var (y, _) = Run(e, x, x, key, key);
            double before = Measure.Rms(y, 6000, 4800), during = Measure.Rms(y, 18000, 4800);
            TestContext.Out.WriteLine($"before {Measure.Db(before):F1} dB, ducked {Measure.Db(during):F1} dB");
            Assert.That(Measure.Db(before / Measure.Rms(x, 6000, 4800)), Is.InRange(-0.01, 0.01), "no key, no reduction");
            Assert.That(Measure.Db(during / before), Is.LessThan(-10));
        }

        [Test]
        public void OptoReleasesInTwoStages()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, (int)CompMode.Opto);
                Set(v, P.Release, 2.0);
                Set(v, P.Attack, 0.01);
            });
            var x = Constant(-5, 48000 * 3);
            for (int i = 48000; i < x.Length; i++) x[i] = (float)DspMath.DbToGain(-50);
            var (y, _) = Run(e, x, x);
            double gr0 = -Measure.Db(y[47999] / x[47999]);
            double gr100ms = -Measure.Db(y[48000 + 4800] / x[48000 + 4800]);
            double gr1s = -Measure.Db(y[48000 + 48000] / x[48000 + 48000]);
            TestContext.Out.WriteLine($"GR {gr0:F1} dB → {gr100ms:F1} dB after 100 ms → {gr1s:F1} dB after 1 s");
            Assert.That(gr100ms, Is.LessThan(0.65 * gr0), "fast first stage");
            Assert.That(gr1s, Is.GreaterThan(0.1 * gr0), "slow second stage");
        }

        [Test]
        public void ExtremeSettingsStayBoundedAndFinite()
        {
            var rng = new Random(5);
            var (e, v) = Make(null);
            var x = Measure.PinkNoise(24000, 0.7);
            for (int round = 0; round < 30; round++)
            {
                foreach (var d in Params.Table) v.Set(d.Index, rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) });
                var (y, _) = Run(e, x, x, x, x);
                foreach (var s in y)
                {
                    Assert.That(float.IsFinite(s), Is.True);
                    // Makeup, auto makeup and output can add 72 dB to quiet settings: bounded, not small.
                    Assert.That(Math.Abs(s), Is.LessThan(5000));
                }
            }
        }
    }
}
