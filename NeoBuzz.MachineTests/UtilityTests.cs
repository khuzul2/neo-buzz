using NeoBuzz.Dsp;
using NeoBuzz.Utility;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Utility (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class UtilityTests
    {
        static (UtilityEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Measure.Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new UtilityEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        internal static (float[] L, float[] R) Run(UtilityEngine e, float[] l, float[] r)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[UtilityEngine.MaxBlock];
            var br = new float[UtilityEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += UtilityEngine.MaxBlock)
            {
                int n = Math.Min(UtilityEngine.MaxBlock, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static float[] Noise(int n, uint seed, double rms = 0.25)
        {
            var rng = new Rng(seed);
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = (float)(rms * 1.7320508 * rng.NextBipolar());
            return x;
        }

        /// <summary>Amplitude of the <paramref name="hz"/> component over a window holding whole cycles (lock-in).</summary>
        internal static double Amplitude(float[] y, double hz, int from, int count, int rate = Measure.Rate)
        {
            double s = 0, c = 0;
            for (int i = 0; i < count; i++)
            {
                double ph = 2 * Math.PI * hz * (from + i) / rate;
                s += y[from + i] * Math.Sin(ph);
                c += y[from + i] * Math.Cos(ph);
            }
            return 2 * Math.Sqrt(s * s + c * c) / count;
        }

        [Test]
        public void DefaultsPassTheInputBitForBit()
        {
            var (e, _) = Make();
            var l = Noise(48000, 1);
            var r = Noise(48000, 2);
            var (yl, yr) = Run(e, l, r);
            Assert.That(yl, Is.EqualTo(l), "left");
            Assert.That(yr, Is.EqualTo(r), "right");
            Assert.That(e.Latency, Is.EqualTo(0));
            Measure.Report("Neo Utility default: bit for bit, latency 0");
        }

        [Test]
        public void GainIsExact()
        {
            double worst = 0;
            foreach (double db in new[] { -60, -24.3, -6, -0.1, 0.1, 6.5, 24 })
            {
                var (e, v) = Make();
                Set(v, P.Gain, db);
                var l = Noise(9600, 3);
                var r = Noise(9600, 4);
                var (yl, yr) = Run(e, l, r);
                for (int i = 4800; i < l.Length; i++)
                {
                    if (Math.Abs(l[i]) < 1e-3 || Math.Abs(r[i]) < 1e-3) continue;
                    worst = Math.Max(worst, Math.Abs(Measure.Db(yl[i] / l[i]) - db));
                    worst = Math.Max(worst, Math.Abs(Measure.Db(yr[i] / r[i]) - db));
                    Assert.That(Math.Sign(yl[i]), Is.EqualTo(Math.Sign(l[i])));
                }
            }
            var (m, mv) = Make();
            mv.Set(P.Gain, 0);
            Assert.That(Params.Table[P.Gain].Describe(0), Is.EqualTo("−∞ dB"));
            var (sl, sr) = Run(m, Noise(4800, 5), Noise(4800, 6));
            Assert.That(sl.Skip(2400).All(x => x == 0) && sr.Skip(2400).All(x => x == 0), Is.True, "−∞ is silence");
            Measure.Report($"Neo Utility gain: worst error {worst:F5} dB (−60 … +24 dB); −∞ silent");
            Assert.That(worst, Is.LessThan(0.001));
        }

        [Test]
        public void PanLawsAreExact()
        {
            double worst = 0;
            foreach (var law in new[] { PanLaw.Balance, PanLaw.MonoPan })
                foreach (double pan in new[] { -1, -0.5, -0.3, 0, 0.25, 0.8, 1 })
                {
                    var (e, v) = Make(p =>
                    {
                        p.Set(P.PanLaw, (int)law);
                        Set(p, P.Pan, pan);
                    });
                    var l = Noise(9600, 7);
                    var r = Noise(9600, 8);
                    var (yl, yr) = Run(e, l, r);
                    double pv = Params.Table[P.Pan].Value(v[P.Pan]);
                    double el, er;
                    if (law == PanLaw.Balance)
                    {
                        el = pv > 0 ? 1 - pv : 1;
                        er = pv < 0 ? 1 + pv : 1;
                    }
                    else
                    {
                        el = Math.Cos((pv + 1) * Math.PI / 4);
                        er = Math.Sin((pv + 1) * Math.PI / 4);
                    }
                    for (int i = 4800; i < l.Length; i++)
                    {
                        double il = law == PanLaw.Balance ? l[i] : 0.5 * (l[i] + r[i]);
                        double ir = law == PanLaw.Balance ? r[i] : 0.5 * (l[i] + r[i]);
                        if (Math.Abs(il) < 1e-3 || Math.Abs(ir) < 1e-3) continue;
                        if (el < 1e-9) Assert.That(yl[i], Is.EqualTo(0f), $"{law} {pan}: left silent");
                        else worst = Math.Max(worst, Math.Abs(Measure.Db(yl[i] / il) - Measure.Db(el)));
                        if (er < 1e-9) Assert.That(yr[i], Is.EqualTo(0f), $"{law} {pan}: right silent");
                        else worst = Math.Max(worst, Math.Abs(Measure.Db(yr[i] / ir) - Measure.Db(er)));
                    }
                }
            Measure.Report($"Neo Utility pan: worst error {worst:F5} dB (balance and mono pan laws)");
            Assert.That(worst, Is.LessThan(0.001));
            Assert.That(Measure.Db(Math.Cos(Math.PI / 4)), Is.EqualTo(-3.0103).Within(1e-4), "mono pan: −3 dB at centre");
        }

        [Test]
        public void WidthScalesTheSideExactly()
        {
            double worst = 0;
            var l = Noise(9600, 9);
            var r = Noise(9600, 10);
            foreach (double w in new[] { 0, 25, 50, 100, 150, 200 })
            {
                var (e, _) = Make(p => Set(p, P.Width, w));
                var (yl, yr) = Run(e, l, r);
                for (int i = 2400; i < l.Length; i++)
                {
                    if (w == 0) Assert.That(yl[i], Is.EqualTo(yr[i]), "0 %: mono");
                    if (w == 100) Assert.That(yl[i] == l[i] && yr[i] == r[i], Is.True, "100 %: the input");
                    double m = 0.5 * (l[i] + r[i]), s = 0.5 * (l[i] - r[i]);
                    double mo = 0.5 * (yl[i] + yr[i]), so = 0.5 * (yl[i] - yr[i]);
                    Assert.That(mo, Is.EqualTo(m).Within(1e-6), "mid unchanged");
                    if (w > 0 && Math.Abs(s) > 1e-2) worst = Math.Max(worst, Math.Abs(Measure.Db(so / s) - Measure.Db(w / 100)));
                }
            }
            Measure.Report($"Neo Utility width: side/mid within {worst:F5} dB of the setting; 0 % mono, 100 % bit for bit");
            Assert.That(worst, Is.LessThan(0.01));
        }

        [Test]
        public void BassMonoRemovesTheLowSide()
        {
            const double fc = 120;
            Assert.That(Params.Table[P.BassFreq].Describe(Params.Table[P.BassFreq].Raw(fc)), Is.EqualTo("120 Hz"));
            double Side(double hz)
            {
                var (e, _) = Make(p =>
                {
                    p.Set(P.BassMono, 1);
                    Set(p, P.BassFreq, fc);
                });
                var x = Measure.Sine(hz, 96000, 0.5);
                var neg = x.Select(s => -s).ToArray();
                var (yl, yr) = Run(e, x, neg);
                var side = new float[yl.Length];
                for (int i = 0; i < side.Length; i++) side[i] = 0.5f * (yl[i] - yr[i]);
                return Measure.Db(Amplitude(side, hz, 48000, 48000) / 0.5);
            }
            double atFc = Side(fc), below = Side(fc / 4), above = Side(fc * 4);
            Measure.Report($"Neo Utility bass mono {fc} Hz: side {atFc:F3} dB at the frequency, {below:F1} dB two octaves below, {above:F4} dB two octaves above");
            Assert.That(atFc, Is.EqualTo(-3.0103).Within(0.1));
            Assert.That(below, Is.LessThan(-45));
            Assert.That(Math.Abs(above), Is.LessThan(0.1));
            Assert.That(UtilityEngine.BassMonoResponse(fc, fc, Measure.Rate), Is.EqualTo(Math.Sqrt(0.5)).Within(1e-9), "design: −3 dB at the frequency");

            // The mid passes untouched.
            var (em, _) = Make(p => { p.Set(P.BassMono, 1); Set(p, P.BassFreq, fc); });
            var l = Noise(48000, 11);
            var r = Noise(48000, 12);
            var (ml, mr) = Run(em, l, r);
            double worst = 0;
            for (int i = 0; i < l.Length; i++) worst = Math.Max(worst, Math.Abs(0.5 * (ml[i] + mr[i]) - 0.5 * (l[i] + r[i])));
            Assert.That(worst, Is.LessThan(1e-6), "mid untouched");
        }

        [Test]
        public void InvertAndSwapAreExact()
        {
            var l = Noise(4800, 13);
            var r = Noise(4800, 14);
            var (ei, _) = Make(p => { p.Set(P.InvertL, 1); });
            var (il, ir) = Run(ei, l, r);
            var (es, _) = Make(p => { p.Set(P.Swap, 1); });
            var (sl, sr) = Run(es, l, r);
            var (eb, _) = Make(p => { p.Set(P.Swap, 1); p.Set(P.InvertR, 1); });
            var (bl, br) = Run(eb, l, r);
            for (int i = 0; i < l.Length; i++)
            {
                Assert.That(il[i] == -l[i] && ir[i] == r[i], Is.True, $"invert L at {i}");
                Assert.That(sl[i] == r[i] && sr[i] == l[i], Is.True, $"swap at {i}");
                // Polarity applies to the inputs, then the swap: the inverted right input lands on the left.
                Assert.That(bl[i] == -r[i] && br[i] == l[i], Is.True, $"invert R, then swap, at {i}");
            }
            Measure.Report("Neo Utility invert and swap: exact, sample for sample");
        }

        [Test]
        public void DcRemovalRemovesDcAndKeeps20Hz()
        {
            var (e, _) = Make(p => p.Set(P.DcRemove, 1));
            var dc = new float[48000];
            Array.Fill(dc, 0.5f);
            var (y, _) = Run(e, dc, dc);
            double residual = Measure.Peak(y, 47000, 1000);
            int below = Array.FindLastIndex(y, s => Math.Abs(s) >= 0.5 * 1e-5) + 1;

            var (e2, _) = Make(p => p.Set(P.DcRemove, 1));
            var x = Measure.Sine(20, 96000, 0.5);
            var (y2, _) = Run(e2, x, x);
            double pass = Measure.Db(Amplitude(y2, 20, 48000, 48000) / 0.5);
            Measure.Report($"Neo Utility DC removal: offset below −100 dB after {below * 1000.0 / Measure.Rate:F0} ms (residual {Measure.Db(residual / 0.5):F0} dB at 1 s); 20 Hz {pass:F4} dB");
            Assert.That(below, Is.LessThan(48000), "below −100 dB within 1 s");
            Assert.That(Math.Abs(pass), Is.LessThan(0.05));
        }

        [Test]
        public void MovesGlideWithoutSteps()
        {
            var (e, v) = Make();
            var dc = new float[94 * 256];
            Array.Fill(dc, 0.5f);
            var bl = new float[256];
            var br = new float[256];
            double biggest = 0;
            float last = 0.5f;
            for (int pos = 0; pos < dc.Length; pos += 256)
            {
                if (pos == 2560) Set(v, P.Gain, -24);
                if (pos == 9984) { v.Set(P.InvertL, 1); }
                if (pos == 14848) { Set(v, P.Width, 0); }
                Array.Copy(dc, pos, bl, 0, 256);
                Array.Copy(dc, pos, br, 0, 256);
                e.Process(bl, br, 256);
                for (int i = 0; i < 256; i++)
                {
                    biggest = Math.Max(biggest, Math.Abs(bl[i] - last));
                    last = bl[i];
                }
            }
            // A 20 ms ramp over 0.5 → 0.031 moves 0.49 in 960 samples; the 5 ms polarity ramp 0.06 in 240.
            Measure.Report($"Neo Utility moves: largest step per sample {biggest:E2} (gain −24 dB in 20 ms, polarity in 5 ms)");
            Assert.That(biggest, Is.LessThan(0.001));
        }

        [Test]
        public void ExtremeSettingsStayFinite()
        {
            var rng = new Random(3);
            var (e, v) = Make();
            var l = Noise(12000, 15, 0.7);
            var r = Noise(12000, 16, 0.7);
            for (int round = 0; round < 40; round++)
            {
                foreach (var d in Params.Table) v.Set(d.Index, rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) });
                var (yl, yr) = Run(e, l, r);
                foreach (var s in yl.Concat(yr))
                {
                    Assert.That(float.IsFinite(s), Is.True);
                    Assert.That(Math.Abs(s), Is.LessThan(100));
                }
            }
        }
    }
}
