using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.Limiter;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Limiter (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class LimiterTests
    {
        static (LimiterEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new LimiterEngine(v);
            e.SetSampleRate(Measure.Rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (float[] L, float[] R) Run(LimiterEngine e, float[] l, float[] r)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[LimiterEngine.MaxBlock];
            var br = new float[LimiterEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += LimiterEngine.MaxBlock)
            {
                int n = Math.Min(LimiterEngine.MaxBlock, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static float[] Normalise(float[] x)
        {
            double p = Measure.Peak(x, 0, x.Length);
            return x.Select(s => (float)(s / p)).ToArray();
        }

        /// <summary>A naive (not band-limited) square wave: the worst case for inter-sample peaks.</summary>
        static float[] Square(double hz, int n, double phase = 0)
        {
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = (hz * i / Measure.Rate + phase) % 1.0 < 0.5 ? 1f : -1f;
            return x;
        }

        /// <summary>A drum loop at 120 BPM: kick, snare and hats, made here (no samples), peak 1.</summary>
        internal static (float[] L, float[] R) Drums(int n, uint seed = 77)
        {
            var l = new float[n];
            var r = new float[n];
            var rng = new Rng(seed);
            int beat = Measure.Rate / 2;
            for (int start = 0; start < n; start += beat / 2)
            {
                int step = start / (beat / 2);
                if (step % 2 == 0)
                {
                    // Kick: a falling sine with a click.
                    double ph = 0;
                    for (int i = 0; i < 12000 && start + i < n; i++)
                    {
                        double t = i / (double)Measure.Rate;
                        ph += 2 * Math.PI * (50 + 120 * Math.Exp(-t * 30)) / Measure.Rate;
                        float s = (float)(Math.Sin(ph) * Math.Exp(-t * 8));
                        l[start + i] += s;
                        r[start + i] += s;
                    }
                }
                if (step % 4 == 2)
                {
                    // Snare: noise and a 190 Hz body, a little to the left.
                    for (int i = 0; i < 9000 && start + i < n; i++)
                    {
                        double t = i / (double)Measure.Rate;
                        float s = (float)(0.7 * rng.NextBipolar() * Math.Exp(-t * 25) + 0.5 * Math.Sin(2 * Math.PI * 190 * t) * Math.Exp(-t * 20));
                        l[start + i] += 0.6f * s;
                        r[start + i] += 0.4f * s;
                    }
                }
                // Hats: short bright noise, a little to the right.
                float prev = 0;
                for (int i = 0; i < 2000 && start + i < n; i++)
                {
                    float w = rng.NextBipolar();
                    float s = (float)((w - prev) * 0.35 * Math.Exp(-i / 300.0));
                    prev = w;
                    l[start + i] += 0.4f * s;
                    r[start + i] += 0.6f * s;
                }
            }
            double peak = Math.Max(Measure.Peak(l, 0, n), Measure.Peak(r, 0, n));
            for (int i = 0; i < n; i++)
            {
                l[i] = (float)(l[i] / peak);
                r[i] = (float)(r[i] / peak);
            }
            return (l, r);
        }

        /// <summary>
        /// The true peak of a whole output, in dB. The input must end in silence longer than the
        /// latency: a signal cut off mid-flight would ring against the end of the array, which is
        /// the meter's artefact, not the output's.
        /// </summary>
        static double TruePeakDb(float[] l, float[] r) =>
            20 * Math.Log10(Math.Max(NeoBuzz.Analysis.Loudness.TruePeak(l), NeoBuzz.Analysis.Loudness.TruePeak(r)));

        static float[] Padded(float[] x, int tail = 1024) => x.Concat(new float[tail]).ToArray();

        [Test]
        public void TheDetectorReadsWhatTheMeterReads()
        {
            // The streaming interpolator finds the same 4× peak as NeoBuzz.Analysis.
            foreach (var x in new[] { Normalise(Measure.PinkNoise(20000, 0.2, 5)), Square(997, 20000), Measure.Sine(11025.7, 20000, 0.9) })
            {
                var d = new TruePeakInterpolator();
                double peak = 0;
                foreach (float s in x) peak = Math.Max(peak, d.Process(s));
                for (int i = 0; i < TruePeakInterpolator.Delay + 2; i++) peak = Math.Max(peak, d.Process(0));
                Assert.That(peak, Is.EqualTo(NeoBuzz.Analysis.Loudness.TruePeak(x)).Within(1e-5));
            }
        }

        [Test]
        public void TruePeakStaysUnderTheCeiling()
        {
            int n = Measure.Rate * 4;
            var drums = Drums(n);
            var pinkL = Normalise(Measure.PinkNoise(n, 0.2, 11));
            var pinkR = Normalise(Measure.PinkNoise(n, 0.2, 12));
            var signals = new (string Name, float[] L, float[] R)[]
            {
                ("square 997 Hz", Square(997, n), Square(997, n, 0.25)),
                ("square 5.5 kHz", Square(5512.5, n), Square(5512.5, n, 0.1)),
                ("drums", drums.L, drums.R),
                ("pink noise", pinkL, pinkR),
            };
            double worst = double.NegativeInfinity;
            string where = "";
            foreach (var (name, l, r) in signals)
                foreach (double ceiling in new[] { -1.0, -0.1 })
                    foreach (double lookahead in new[] { 0.5, 2.0 })
                        foreach (int auto in new[] { 0, 1 })
                        {
                            var (e, _) = Make(v =>
                            {
                                Set(v, P.InputGain, 12);
                                Set(v, P.Ceiling, ceiling);
                                Set(v, P.Lookahead, lookahead);
                                v.Set(P.AutoRelease, auto);
                                Set(v, P.Release, 0.05);
                            });
                            var (yl, yr) = Run(e, Padded(l), Padded(r));
                            double over = TruePeakDb(yl, yr) - ceiling;
                            TestContext.Out.WriteLine($"{name}, ceiling {ceiling} dBTP, lookahead {lookahead} ms, auto {auto}: true peak {over:+0.000;−0.000} dB re ceiling");
                            if (over > worst)
                            {
                                worst = over;
                                where = $"{name}, ceiling {ceiling}, lookahead {lookahead} ms";
                            }
                        }
            Measure.Report($"Neo Limiter true peak: worst {worst:+0.000;−0.000} dB re the ceiling ({where}); squares, drums, pink noise +12 dB");
            Assert.That(worst, Is.LessThanOrEqualTo(0.1));
        }

        [Test]
        public void BelowTheCeilingIsTransparent()
        {
            const int bin = 1365;
            var (e, _) = Make(v => Set(v, P.Ceiling, -1));
            var x = Measure.Sine(Measure.BinFrequency(bin), 8192 + Measure.N, DspMath.DbToGain(-7));
            var (y, _) = Run(e, x, x);
            int L = e.Latency;
            double worst = 0;
            for (int i = L; i < x.Length; i++) worst = Math.Max(worst, Math.Abs(y[i] - x[i - L]));
            var mag = Measure.Spectrum(y, 8192);
            double thdn = 0;
            for (int k = 2; k < mag.Length; k++) if (Math.Abs(k - bin) > 2) thdn += mag[k] * mag[k];
            double thdnDb = Measure.Db(Math.Sqrt(thdn) / mag[bin]);
            Measure.Report($"Neo Limiter below the ceiling: output = input delayed {L} samples (max difference {worst:E1}); THD+N {thdnDb:F1} dB");
            Assert.That(worst, Is.LessThan(1e-6));
            Assert.That(thdnDb, Is.LessThan(-100));
        }

        [TestCase(1)]
        [TestCase(0)]
        public void LimitingALowSineDistortsLittle(int auto)
        {
            const int bin = 137;   // ≈ 100.3 Hz
            var (e, _) = Make(v =>
            {
                Set(v, P.Ceiling, -6);
                v.Set(P.AutoRelease, auto);
                Set(v, P.Release, 0.1);
                Set(v, P.Lookahead, 2);
            });
            var x = Measure.Sine(Measure.BinFrequency(bin), 2 * Measure.Rate + Measure.N, 1.0);
            var (y, _) = Run(e, x, x);
            var mag = Measure.Spectrum(y, 2 * Measure.Rate);
            double thd = Measure.Thd(mag, bin) * 100;
            double level = Measure.Db(mag[bin]);
            Measure.Report($"Neo Limiter 100 Hz sine limited by 6 dB, {(auto == 1 ? "auto release" : "release 100 ms")}: THD {thd:F2} %, level {level:F2} dB");
            if (auto == 1) Assert.That(thd, Is.LessThanOrEqualTo(1.0));
        }

        [TestCase(0.01)]
        [TestCase(0.1)]
        [TestCase(0.5)]
        public void ReleaseRecoversInItsTime(double release)
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Ceiling, -12);
                v.Set(P.AutoRelease, 0);
                Set(v, P.Release, release);
                Set(v, P.Lookahead, 1);
            });
            int burst = 4800, n = burst + (int)(release * 6 * Measure.Rate) + 4800;
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = i < burst ? 1f : (float)DspMath.DbToGain(-40);
            var (y, _) = Run(e, x, x);
            int L = e.Latency;
            var gr = new double[n];
            for (int i = L; i < n; i++) gr[i] = -Measure.Db(y[i] / x[i - L]);
            int hold = burst / 2 + L;
            double top = gr[hold];
            int a = Array.FindIndex(gr, burst, g => g <= 0.8 * top);
            int b = Array.FindIndex(gr, burst, g => g <= 0.8 * top / Math.E);
            double tau = (b - a) / (double)Measure.Rate;
            Measure.Report($"Neo Limiter release {release * 1000} ms: time constant {tau * 1000:F2} ms (reduction {top:F1} dB)");
            Assert.That(top, Is.EqualTo(12).Within(0.05), "a 12 dB reduction");
            Assert.That(tau / release, Is.InRange(0.9, 1.1));
        }

        [Test]
        public void AutoReleaseRecoversPeaksFastAndSustainedLimitingSlowly()
        {
            double Recovery(int burst)
            {
                var (e, _) = Make(v =>
                {
                    Set(v, P.Ceiling, -12);
                    v.Set(P.AutoRelease, 1);
                    Set(v, P.Release, 0.1);
                });
                int n = burst + Measure.Rate;
                var x = new float[n];
                for (int i = 0; i < n; i++) x[i] = i < burst ? 1f : (float)DspMath.DbToGain(-40);
                var (y, _) = Run(e, x, x);
                int L = e.Latency;
                double top = -Measure.Db(y[burst - 1 + L - e.LookaheadSamples] / x[burst - 1 - e.LookaheadSamples]);
                int from = burst + L;
                int at = Array.FindIndex(y, from, s => -Measure.Db(s / DspMath.DbToGain(-40)) <= top / Math.E);
                return (at - from) / (double)Measure.Rate;
            }
            double peak = Recovery(240), sustained = Recovery(2 * Measure.Rate);
            Measure.Report($"Neo Limiter auto release (Release 100 ms): a 5 ms peak recovers 63 % in {peak * 1000:F0} ms, 2 s of limiting in {sustained * 1000:F0} ms");
            Assert.That(peak, Is.LessThan(0.05), "an isolated peak recovers fast");
            Assert.That(sustained, Is.InRange(0.08, 0.13), "sustained limiting recovers in about the Release time");
        }

        [Test]
        public void FullLinkGivesBothChannelsTheSameGain()
        {
            var (e, _) = Make(v => Set(v, P.InputGain, 12));
            var l = Measure.Sine(500, 24000, 0.9);
            var r = Measure.Sine(700, 24000, 0.05);
            var (yl, yr) = Run(e, l, r);
            int L = e.Latency;
            int checkedSamples = 0;
            for (int i = L; i < l.Length; i += 7)
            {
                float xl = l[i - L], xr = r[i - L];
                if (Math.Abs(xl) < 0.05 || Math.Abs(xr) < 0.005) continue;
                Assert.That(yl[i] / xl, Is.EqualTo(yr[i] / xr).Within(1e-4), $"sample {i}");
                checkedSamples++;
            }
            Assert.That(checkedSamples, Is.GreaterThan(100));
        }

        [Test]
        public void LatencyFollowsTheLookahead()
        {
            foreach (double ms in new[] { 0.5, 2.0, 5.0 })
            {
                var (e, _) = Make(v => Set(v, P.Lookahead, ms));
                var x = new float[2048];
                x[100] = 0.5f;
                var (y, _) = Run(e, x, x);
                int at = Array.FindIndex(y, s => s != 0);
                int expected = (int)Math.Round(ms * 0.001 * Measure.Rate) + TruePeakInterpolator.Delay - 1;
                Assert.That(e.Latency, Is.EqualTo(expected));
                Assert.That(at - 100, Is.EqualTo(e.Latency), $"lookahead {ms} ms: the impulse arrives after the reported latency");
                Assert.That(y[at], Is.EqualTo(0.5f));
            }
            Measure.Report($"Neo Limiter latency: lookahead + {TruePeakInterpolator.Delay - 1} samples (2 ms at 48 kHz: {(int)Math.Round(0.002 * Measure.Rate) + TruePeakInterpolator.Delay - 1})");
        }

        [Test]
        public void RunsFasterThanRealTime()
        {
            // 50× is 2 % of a core. Timed against the reference workload in the same run (Throughput.cs): 1 s played untimed, then 2 s.
            var (l, r) = Drums(Measure.Rate * 3);
            const int Warm = Measure.Rate;
            Throughput.Guard("Neo Limiter CPU (stereo, limiting drums)", Throughput.Floor(x64: 150, arm64: 110), 2, Measure.Rate, LimiterEngine.MaxBlock, () =>
            {
                var (e, _) = Make(v => Set(v, P.InputGain, 12));
                var bl = new float[LimiterEngine.MaxBlock];
                var br = new float[LimiterEngine.MaxBlock];
                void Block(int pos, int n)
                {
                    Array.Copy(l, pos, bl, 0, n);
                    Array.Copy(r, pos, br, 0, n);
                    e.Process(bl, br, n);
                }
                for (int pos = 0; pos < Warm; pos += LimiterEngine.MaxBlock) Block(pos, LimiterEngine.MaxBlock);
                return Throughput.InBlocks(LimiterEngine.MaxBlock, (pos, n) => Block(Warm + pos, n));
            }, Measure.Report);
        }

        [Test]
        public void ExtremeSettingsStayFinite()
        {
            var rng = new Random(9);
            var (e, v) = Make();
            var (l, r) = Drums(24000);
            for (int round = 0; round < 30; round++)
            {
                foreach (var d in Params.Table) v.Set(d.Index, rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) });
                var (yl, yr) = Run(e, l, r);
                foreach (var s in yl.Concat(yr))
                {
                    Assert.That(float.IsFinite(s), Is.True);
                    Assert.That(Math.Abs(s), Is.LessThan(20));
                }
            }
        }
    }
}
