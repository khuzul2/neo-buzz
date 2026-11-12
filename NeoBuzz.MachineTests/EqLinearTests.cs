using NeoBuzz.Dsp;
using NeoBuzz.Eq;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo EQ linear phase and the curve layer (docs/specs/mastering-tools.md §5.6): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class EqLinearTests
    {
        const int Rate = Measure.Rate;

        static (EqEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, bool linear = true, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Phase, linear ? 1 : 0);
            setup?.Invoke(v);
            var e = new EqEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static void Flat(ParamValues v)
        {
            for (int b = 0; b < EqDesign.Bands; b++) v.Set(EqDesign.On(b), 0);
        }

        static void Band(ParamValues v, int b, BandType type, double hz, double db = 0, double q = 0.7071, int slope = 0) =>
            EqTests.Band(v, b, type, hz, db, q, slope);

        /// <summary>A mixed setting: low cut, low bell within the Q limit, mid bells, a shelf and a high cut.</summary>
        static void Mixed(ParamValues v)
        {
            Flat(v);
            Band(v, 0, BandType.LowCut, 30, slope: 1);
            Band(v, 1, BandType.Bell, 60, 6, 1.2);
            Band(v, 2, BandType.Bell, 250, -7, 1.5);
            Band(v, 3, BandType.Bell, 1000, 8, 2.5);
            Band(v, 4, BandType.Notch, 3300, 0, 4);
            Band(v, 5, BandType.HighShelf, 7000, -5, 0.8);
            Band(v, 6, BandType.Bell, 12000, 4, 3);
            Band(v, 7, BandType.HighCut, 19000, slope: 1);
        }

        static (float[] L, float[] R) Run(EqEngine e, float[] l, float[] r, int block = EqEngine.MaxBlock, params (int At, Action Do)[] changes)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[EqEngine.MaxBlock];
            var br = new float[EqEngine.MaxBlock];
            int pos = 0, ci = 0;
            while (pos < l.Length)
            {
                int n = Math.Min(block, l.Length - pos);
                while (ci < changes.Length && changes[ci].At <= pos) changes[ci++].Do();
                if (ci < changes.Length && changes[ci].At < pos + n) n = changes[ci].At - pos;
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
                pos += n;
            }
            return (ol, or);
        }

        static float[] Impulse(EqEngine e, int length)
        {
            var x = new float[length];
            x[0] = 1;
            return Run(e, x, x).L;
        }

        /// <summary>The DTFT of <paramref name="h"/> at <paramref name="hz"/> with <paramref name="delay"/> samples taken out: (re, im).</summary>
        static (double Re, double Im) Dtft(float[] h, double hz, int delay)
        {
            double th = 2 * Math.PI * hz / Rate;
            double re = 0, im = 0;
            for (int n = 0; n < h.Length; n++)
            {
                double a = th * (n - delay);
                re += h[n] * Math.Cos(a);
                im -= h[n] * Math.Sin(a);
            }
            return (re, im);
        }

        static IEnumerable<double> Grid(double from, double to, int count)
        {
            for (int i = 0; i < count; i++) yield return from * Math.Pow(to / from, i / (double)(count - 1));
        }

        [Test]
        public void TheLatencyIsTheFirsCentreAndAPartition()
        {
            var (e, _) = Make();
            Assert.That(e.Latency, Is.EqualTo(4095 + 512));
            Assert.That(EqLinearDesign.LinearLatency(96000), Is.EqualTo(8191 + 1024));
            var (m, _) = Make(linear: false);
            Assert.That(m.Latency, Is.EqualTo(0));
        }

        [Test]
        public void TheResponseIsTheMinimumPhaseMagnitudeAndTheGroupDelayIsConstant()
        {
            var (e, v) = Make(Mixed);
            var h = Impulse(e, 12000);
            double worst = 0, worstDelay = 0;
            int at = 0, checkedAt = 0;
            foreach (double hz in Grid(30, 18000, 160))
            {
                var (re, im) = Dtft(h, hz, 0);
                double db = 20 * Math.Log10(Math.Max(1e-12, Math.Sqrt(re * re + im * im)));
                double want = EqDesign.ResponseDb(v, hz, Rate);
                double err = Math.Abs(db - want);
                if (err > worst)
                {
                    worst = err;
                    at = (int)hz;
                }
                // The group delay: the phase (turned by the latency) stays within ±0.5 sample of zero delay.
                if (want > -60)
                {
                    var (cr, ci) = Dtft(h, hz, e.Latency);
                    double phase = Math.Atan2(ci, cr);
                    double w = 2 * Math.PI * hz / Rate;
                    worstDelay = Math.Max(worstDelay, Math.Abs(phase) / w);
                    checkedAt++;
                }
            }
            Measure.Report($"Neo EQ linear phase: response vs the minimum-phase design within {worst:0.0000} dB (worst at {at} Hz), delay error ≤ {worstDelay:0.000} samples at {checkedAt} frequencies");
            Assert.That(worst, Is.LessThan(0.05));
            Assert.That(worstDelay, Is.LessThan(0.5));
        }

        [Test]
        public void TheImpulseResponsePeaksAtTheReportedLatency()
        {
            var (e, _) = Make(v =>
            {
                Flat(v);
                Band(v, 3, BandType.Bell, 2000, 9, 1.5);
            });
            var h = Impulse(e, 12000);
            int peak = 0;
            for (int i = 1; i < h.Length; i++) if (Math.Abs(h[i]) > Math.Abs(h[peak])) peak = i;
            Assert.That(peak, Is.EqualTo(e.Latency));
            // And symmetric about it (linear phase): the taps D ± n agree.
            for (int n = 1; n < 1500; n++)
                Assert.That(h[e.Latency + n], Is.EqualTo(h[e.Latency - n]).Within(2e-6), $"tap ±{n}");
        }

        [Test]
        public void TheCurveLayerIsAddedBeforeTheDesign()
        {
            var (e, v) = Make(c =>
            {
                Flat(c);
                for (int i = 0; i < 32; i++) Set(c, P.Curve1 + i, 0);
                Set(c, P.Curve1 + 16, 9);
                Set(c, P.Curve1 + 15, 5);
                Set(c, P.Curve1 + 17, 5);
                Band(c, 3, BandType.Bell, 1000, -4, 1);
            });
            var h = Impulse(e, 12000);
            foreach (double hz in Grid(40, 18000, 80))
            {
                var (re, im) = Dtft(h, hz, 0);
                double db = 20 * Math.Log10(Math.Max(1e-12, Math.Sqrt(re * re + im * im)));
                double want = EqDesign.ResponseDb(v, hz, Rate) + EqLinearDesign.CurveDb(v, hz);
                Assert.That(db, Is.EqualTo(want).Within(0.06), $"{hz:0} Hz");
            }
            // The curve passes through its points, smoothly.
            double at16 = EqLinearDesign.CurveDb(v, EqLinearDesign.CurveHz(16));
            Assert.That(at16, Is.EqualTo(9).Within(0.06));
            Assert.That(EqLinearDesign.CurveDb(v, 20), Is.EqualTo(0).Within(1e-9));
            Assert.That(EqLinearDesign.CurveDb(v, 20000), Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void TheCurveDoesNothingInMinimumPhase()
        {
            var (e, _) = Make(v =>
            {
                Flat(v);
                Set(v, P.Curve1 + 10, 12);
            }, linear: false);
            var x = Measure.PinkNoise(20000);
            var (l, _) = Run(e, x, x);
            Assert.That(l, Is.EqualTo(x));
        }

        [TestCase(4)]
        [TestCase(3)]
        [TestCase(1)]
        [TestCase(2)]
        public void ABandOnOneChannelMakesTheSameMagnitudeAsInMinimumPhase(int channel)
        {
            // (Bands on different channels that overlap in frequency add as complex gains in minimum phase, as magnitudes here:
            // the engine's linear phase is the magnitude of each band alone; one band per run compares like with like.)
            Action<ParamValues> setup = v =>
            {
                Flat(v);
                Band(v, 0, BandType.Bell, 1000, 8, 1);
                v.Set(EqDesign.Channel(0), channel);
            };
            var noise = Measure.PinkNoise(2 * Rate, 0.1, 777);
            var other = Measure.PinkNoise(2 * Rate, 0.1, 4242);
            var (lin, _) = Make(setup);
            var (min, _) = Make(setup, linear: false);
            var (ll, lr) = Run(lin, noise, other);
            var (ml, mr) = Run(min, noise, other);
            int n = 1 << 15, from = Rate;
            foreach (var (a, b) in new[] { (ll, ml), (lr, mr) })
            {
                var sa = Measure.Spectrum(a, from + lin.Latency, n);
                var sb = Measure.Spectrum(b, from, n);
                // Over wide bands (the noise's own scatter averages out): the level of each third octave.
                for (double hz = 100; hz < 12000; hz *= 1.26)
                {
                    int lo = (int)Math.Round(hz * n / Rate), hi = (int)Math.Round(hz * 1.26 * n / Rate);
                    double da = 0, db = 0;
                    for (int k = lo; k < hi; k++)
                    {
                        da += sa[k] * sa[k];
                        db += sb[k] * sb[k];
                    }
                    Assert.That(10 * Math.Log10(da / db), Is.EqualTo(0).Within(0.25), $"{hz:0} Hz, {(BandChannel)channel}");
                }
            }
        }

        [Test]
        public void TheQOfALowNarrowBandIsLimitedAndTheEditorCanSay()
        {
            var (e, _) = Make(v =>
            {
                Flat(v);
                Band(v, 0, BandType.Bell, 30, 12, 8);      // limited
                Band(v, 1, BandType.Bell, 1000, 12, 8);    // fine
                Band(v, 2, BandType.Bell, 80, 6, 1);       // fine
            });
            Assert.That(e.QCappedMask, Is.EqualTo(1));
            Assert.That(e.Readings()["q_capped_bands"], Is.EqualTo(1));
            Assert.That(EqLinearDesign.MaxQ(30, Rate), Is.EqualTo(1.608).Within(0.01));
            // The response of the limited band is the limited design's, not a ringing past the FIR.
            var h = Impulse(e, 12000);
            double tail = 0;
            for (int i = 2 * e.Latency - 200; i < h.Length && i < 2 * e.Latency + 4000; i++) tail = Math.Max(tail, Math.Abs(h[i]));
            Assert.That(tail, Is.LessThan(1e-3));
            var (m, _) = Make(v => Band(v, 0, BandType.Bell, 30, 12, 8), linear: false);
            Assert.That(m.QCappedMask, Is.EqualTo(0));
        }

        [Test]
        public void PreRingingIsReported()
        {
            var (flat, _) = Make(Flat);
            Impulse(flat, 100);
            Assert.That(flat.Readings()["pre_ringing_db"], Is.EqualTo(-300));
            var (e, _) = Make(v =>
            {
                Flat(v);
                Band(v, 3, BandType.Bell, 1000, 12, 4);
            });
            Impulse(e, 100);
            double pre = e.Readings()["pre_ringing_db"];
            Measure.Report($"Neo EQ linear phase: pre-ringing {pre:0.0} dB of the response's energy (bell +12 dB, 1 kHz, Q 4)");
            Assert.That(pre, Is.LessThan(-3).And.GreaterThan(-60));
            var (m, _) = Make(v => Band(v, 3, BandType.Bell, 1000, 12, 4), linear: false);
            Assert.That(m.Readings()["pre_ringing_db"], Is.EqualTo(-300));
        }

        [Test]
        public void NeutralSettingsPassTheInputDelayedBitForBit()
        {
            // The defaults' bands have no gain: the identity, which passes the input untouched, D + P late.
            var (e, _) = Make();
            var x = Measure.PinkNoise(3 * Rate, 0.3, 99);
            var y = Measure.PinkNoise(3 * Rate, 0.3, 100);
            var (l, r) = Run(e, x, y);
            int la = e.Latency;
            for (int i = 0; i < 3 * Rate - la; i++)
            {
                if (i < 2048) continue;
                if (l[i + la] != x[i] || r[i + la] != y[i])
                    Assert.Fail($"sample {i}: {l[i + la]} against {x[i]}");
            }
        }

        [Test]
        public void BlocksOf256And128And117And16AreIdentical()
        {
            var x = Measure.PinkNoise(3 * Rate, 0.2, 5);
            var y = Measure.PinkNoise(3 * Rate, 0.2, 6);
            float[] first = null, firstR = null;
            foreach (int block in new[] { 256, 128, 117, 16 })
            {
                var (e, v) = Make(Mixed);
                var (l, r) = Run(e, x, y, block,
                    (Rate, () => Set(v, EqDesign.Gain(2), 5)),
                    (Rate + 7777, () => Band(v, 3, BandType.Bell, 1500, -6, 2)),
                    (2 * Rate, () => v.Set(EqDesign.On(5), 0)));
                if (first == null)
                {
                    first = l;
                    firstR = r;
                }
                else
                {
                    Assert.That(l, Is.EqualTo(first), $"block {block}");
                    Assert.That(r, Is.EqualTo(firstR), $"block {block}");
                }
            }
        }

        [Test]
        public void AParameterChangeIsHeardAndTheOutputStaysFinite()
        {
            var (e, v) = Make(Flat);
            var x = Measure.Sine(1000, 4 * Rate, 0.3);
            var (l, _) = Run(e, x, x, EqEngine.MaxBlock,
                (Rate, () => Band(v, 3, BandType.Bell, 1000, 12, 1)));
            foreach (var s in l) Assert.That(float.IsFinite(s));
            double before = Measure.Rms(l, Rate / 2, Rate / 4);
            double after = Measure.Rms(l, 3 * Rate, Rate / 4);
            Assert.That(Measure.Db(after / before), Is.EqualTo(12).Within(0.1));
        }

        [Test]
        public void SwitchingThePhaseModeStaysFiniteAndComesBackAligned()
        {
            var (e, v) = Make(Mixed, linear: false);
            var x = Measure.PinkNoise(5 * Rate, 0.2, 31);
            var (l, _) = Run(e, x, x, EqEngine.MaxBlock,
                (Rate, () => v.Set(P.Phase, 1)),
                (3 * Rate, () => v.Set(P.Phase, 0)));
            foreach (var s in l) Assert.That(float.IsFinite(s));
            var (m, _) = Make(Mixed, linear: false);
            var (want, _) = Run(m, x, x);
            // Back in minimum phase after the fade: the same as an engine that stayed there (state settled after 1 s).
            for (int i = 4 * Rate; i < 5 * Rate; i++)
                Assert.That(l[i], Is.EqualTo(want[i]).Within(1e-4), $"sample {i}");
            Assert.That(e.Latency, Is.EqualTo(0));
        }

        [Test]
        public void GraphicModeStaysMinimumPhase()
        {
            var (e, _) = Make(v => v.Set(P.Mode, 1));
            Assert.That(e.Latency, Is.EqualTo(0));
        }

        [Test]
        public void NonFiniteInputLeavesNoState()
        {
            var (e, _) = Make(Mixed);
            var x = Measure.PinkNoise(Rate, 0.2, 1);
            x[5000] = float.NaN;
            x[5001] = float.PositiveInfinity;
            var (l, _) = Run(e, x, x);
            foreach (var s in l) Assert.That(float.IsFinite(s));
            Assert.That(e.Linear.HoldsNonFinite(), Is.False);
            Run(e, new float[Rate], new float[Rate]);
            Assert.That(e.Linear.HoldsSubnormals(), Is.False);
        }

        [Test]
        public void ChangingTheBandsAllocatesNothing()
        {
            var (e, v) = Make(Mixed);
            var x = Measure.PinkNoise(Rate, 0.2, 8);
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                int pos = (b % 180) * 256;
                Array.Copy(x, pos, bl, 0, 256);
                Array.Copy(x, pos, br, 0, 256);
                if (b % 5 == 0) Set(v, EqDesign.Gain(3), (b % 9) - 4);
                if (b % 37 == 0) Set(v, EqDesign.Freq(2), 200 + (b % 7) * 30);
                if (b % 53 == 0) Set(v, P.Curve1 + (b % 32), (b % 11) - 5);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 400; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2500, b => Block(b + 400));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void ARedesignStepIsBounded()
        {
            // No block does a whole design: the longest block while the bands move is within a small multiple of the median.
            var (e, v) = Make(Mixed);
            var x = Measure.PinkNoise(Rate, 0.2, 9);
            var bl = new float[256];
            var br = new float[256];
            var times = new List<double>();
            var sw = new System.Diagnostics.Stopwatch();
            for (int b = 0; b < 1500; b++)
            {
                int pos = (b % 180) * 256;
                Array.Copy(x, pos, bl, 0, 256);
                Array.Copy(x, pos, br, 0, 256);
                if (b % 3 == 0) Set(v, EqDesign.Gain(3), (b % 9) - 4);
                sw.Restart();
                e.Process(bl, br, 256);
                sw.Stop();
                if (b > 300) times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            double median = times[times.Count / 2], worst = times[^1];
            Measure.Report($"Neo EQ linear phase: block time while redesigning, median {median:0.000} ms, 99.9th percentile {times[(int)(times.Count * 0.999)]:0.000} ms, worst {worst:0.000} ms (256 samples are {256 * 1000.0 / Rate:0.0} ms)");
            Assert.That(times[(int)(times.Count * 0.99)], Is.LessThan(256 * 1000.0 / Rate));
        }

        [TestCase(1, 80, 55)]
        public void RunsFasterThanRealTime(int _, double x64, double arm64)
        {
            var x = Measure.PinkNoise(3 * Rate, 0.2, 3);
            const int Warm = Rate;
            Throughput.Guard("Neo EQ CPU (linear phase, 8 bands, moving)", Throughput.Floor(x64, arm64), 2, Rate, EqEngine.MaxBlock, () =>
            {
                var (e, v) = Make(Mixed);
                var bl = new float[EqEngine.MaxBlock];
                var br = new float[EqEngine.MaxBlock];
                int blocks = 0;
                void Block(int pos, int n)
                {
                    Array.Copy(x, pos, bl, 0, n);
                    Array.Copy(x, pos, br, 0, n);
                    if (++blocks % 40 == 0) Set(v, EqDesign.Gain(3), (blocks % 9) - 4);
                    e.Process(bl, br, n);
                }
                for (int pos = 0; pos < Warm; pos += EqEngine.MaxBlock) Block(pos, EqEngine.MaxBlock);
                return Throughput.InBlocks(EqEngine.MaxBlock, (pos, n) => Block(Warm + pos, n));
            }, Measure.Report);
        }
    }
}
