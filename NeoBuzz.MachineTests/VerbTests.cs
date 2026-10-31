using NeoBuzz.Dsp;
using NeoBuzz.Verb;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Verb (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class VerbTests
    {
        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (VerbEngine Engine, ParamValues Values) Make(Action<ParamValues> setup)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Mix, 1000);
            setup?.Invoke(v);
            var e = new VerbEngine(v);
            e.SetSampleRate(Measure.Rate);
            return (e, v);
        }

        static (float[] L, float[] R) Run(VerbEngine e, float[] l, float[] r)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[VerbEngine.MaxBlock];
            var br = new float[VerbEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += VerbEngine.MaxBlock)
            {
                int n = Math.Min(VerbEngine.MaxBlock, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static (float[] L, float[] R) Impulse(VerbEngine e, int length)
        {
            var x = new float[length];
            x[0] = 1;
            return Run(e, x, x);
        }

        /// <summary>T60 from an impulse response in the 1 kHz octave: Schroeder integration, T30 fit.</summary>
        static double T60At1k(float[] ir)
        {
            var bp1 = new Biquad();
            var bp2 = new Biquad();
            bp1.Set(BiquadType.BandPass, 1000, Measure.Rate, 1.41);
            bp2.Set(BiquadType.BandPass, 1000, Measure.Rate, 1.41);
            var e = new double[ir.Length];
            for (int i = 0; i < ir.Length; i++)
            {
                double y = bp2.Process(bp1.Process(ir[i]));
                e[i] = y * y;
            }
            var edc = new double[ir.Length];
            double acc = 0;
            for (int i = ir.Length - 1; i >= 0; i--)
            {
                acc += e[i];
                edc[i] = acc;
            }
            // Linear fit of 10·log10(EDC/EDC0) between −5 and −35 dB.
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            int count = 0;
            for (int i = 0; i < ir.Length; i++)
            {
                double db = 10 * Math.Log10(edc[i] / edc[0] + 1e-300);
                if (db > -5) continue;
                if (db < -35) break;
                double t = i / (double)Measure.Rate;
                sx += t; sy += db; sxx += t * t; sxy += t * db;
                count++;
            }
            double slope = (count * sxy - sx * sy) / (count * sxx - sx * sx);
            return -60 / slope;
        }

        [TestCase(0.5)]
        [TestCase(2.0)]
        [TestCase(5.0)]
        [TestCase(10.0)]
        public void DecayTimeIsExactAt1k(double t60)
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Decay, t60);
                Set(v, P.LowDecay, 1.0);
                v.Set(P.Damping, 1000);
                v.Set(P.Early, 0);
                v.Set(P.HighCut, 1000);
                v.Set(P.LowCut, 0);
            });
            int length = (int)((t60 * 0.75 + 0.5) * Measure.Rate);
            var (l, _) = Impulse(e, length);
            double measured = T60At1k(l);
            TestContext.Out.WriteLine($"T60 {t60} s: measured {measured:F3} s ({(measured / t60 - 1) * 100:+0.0;−0.0} %)");
            Assert.That(measured / t60, Is.InRange(0.9, 1.1));
        }

        static double Kurtosis(float[] x, int start, int n)
        {
            double m = 0;
            for (int i = 0; i < n; i++) m += x[start + i];
            m /= n;
            double m2 = 0, m4 = 0;
            for (int i = 0; i < n; i++)
            {
                double d = x[start + i] - m;
                m2 += d * d;
                m4 += d * d * d * d;
            }
            m2 /= n;
            m4 /= n;
            return m4 / (m2 * m2);
        }

        [TestCase((int)VerbMode.Hall, 0.100)]
        [TestCase((int)VerbMode.Room, 0.050)]
        [TestCase((int)VerbMode.Chamber, 0.080)]
        [TestCase((int)VerbMode.Plate, 0.050)]
        public void TailIsDenseSoon(int mode, double at)
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, mode);
                v.Set(P.Early, 0);
                v.Set(P.PreDelay, 0);
                Set(v, P.Decay, 3.0);
            });
            var (l, _) = Impulse(e, (int)(0.4 * Measure.Rate));
            int start = (int)(at * Measure.Rate);
            double k = Kurtosis(l, start, 960);
            TestContext.Out.WriteLine($"{(VerbMode)mode}: kurtosis {k:F2} at {at * 1000} ms");
            Assert.That(k, Is.LessThan(4));
        }

        [Test]
        public void TailIsDecorrelated()
        {
            var (e, _) = Make(v => Set(v, P.Decay, 3.0));
            var (l, r) = Impulse(e, Measure.Rate);
            double sl = 0, sr = 0, slr = 0;
            for (int i = (int)(0.2 * Measure.Rate); i < Measure.Rate; i++)
            {
                sl += l[i] * (double)l[i];
                sr += r[i] * (double)r[i];
                slr += l[i] * (double)r[i];
            }
            double rho = slr / Math.Sqrt(sl * sr);
            TestContext.Out.WriteLine($"correlation {rho:F3}");
            Assert.That(Math.Abs(rho), Is.LessThan(0.3));
        }

        [Test]
        public void FreezeHoldsTheTail()
        {
            var (e, v) = Make(null);
            var x = Measure.PinkNoise(Measure.Rate, 0.2);
            Run(e, x, x);
            v.Set(P.Freeze, 1);
            var silence = new float[11 * Measure.Rate];
            var (l, _) = Run(e, silence, silence);
            double a = Measure.Rms(l, (int)(0.5 * Measure.Rate), Measure.Rate);
            double b = Measure.Rms(l, 10 * Measure.Rate, Measure.Rate);
            TestContext.Out.WriteLine($"frozen: {Measure.Db(a):F2} dB → {Measure.Db(b):F2} dB after 10 s");
            Assert.That(Math.Abs(Measure.Db(b / a)), Is.LessThan(1.0));
        }

        [Test]
        public void MixZeroIsTheInput()
        {
            var (e, _) = Make(v => v.Set(P.Mix, 0));
            var x = Measure.PinkNoise(8192, 0.3);
            var (y, _) = Run(e, x, x);
            for (int i = 0; i < x.Length; i++) Assert.That(y[i], Is.EqualTo(x[i]));
        }

        [Test]
        public void ExtremeSettingsStayBoundedAndFinite()
        {
            var rng = new Random(9);
            var (e, v) = Make(null);
            var x = Measure.PinkNoise(3 * Measure.Rate, 0.5);
            for (int round = 0; round < 20; round++)
            {
                foreach (var d in Params.Table) v.Set(d.Index, rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) });
                var (y, _) = Run(e, x, x);
                double peak = Measure.Peak(y, 0, y.Length);
                Assert.That(double.IsFinite(peak), Is.True, $"round {round}");
                Assert.That(peak, Is.LessThan(100), $"round {round}");
            }
        }

        [Test]
        public void WetLevelIsSensible()
        {
            var (e, _) = Make(null);
            var x = Measure.PinkNoise(4 * Measure.Rate, 0.1);
            var (y, _) = Run(e, x, x);
            double ratio = Measure.Db(Measure.Rms(y, 2 * Measure.Rate, 2 * Measure.Rate) / 0.1);
            TestContext.Out.WriteLine($"wet level vs dry: {ratio:F1} dB");
            Assert.That(ratio, Is.InRange(-12.0, 3.0));
        }
    }
}
