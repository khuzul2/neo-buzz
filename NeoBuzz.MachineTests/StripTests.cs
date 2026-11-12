using NeoBuzz.Dsp;
using NeoBuzz.Strip;
using SP = NeoBuzz.Strip.P;
using SParams = NeoBuzz.Strip.Params;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Strip (docs/specs/mastering-tools.md §5.10): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class StripTests
    {
        const int Rate = Measure.Rate;

        static (StripEngine Engine, ParamValues Values) Make(Action<ParamValues>? setup = null, int rate = Rate)
        {
            var v = new ParamValues(SParams.Table);
            setup?.Invoke(v);
            var e = new StripEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, v.Table[p].Raw(value));

        static (float[] L, float[] R) Run(StripEngine e, float[] l, float[] r, int block = StripEngine.MaxBlock, params (int At, Action Do)[] changes)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[StripEngine.MaxBlock];
            var br = new float[StripEngine.MaxBlock];
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

        static (float[] L, float[] R) Material(int length, uint seed = 11) => (Measure.PinkNoise(length, 0.2, seed), Measure.PinkNoise(length, 0.2, seed + 1));

        /// <summary>Runs a standalone engine of another machine over the same material.</summary>
        static (float[] L, float[] R) RunEngine(Action<float[], float[], int> process, float[] l, float[] r)
        {
            var ol = (float[])l.Clone();
            var or = (float[])r.Clone();
            var bl = new float[256];
            var br = new float[256];
            for (int pos = 0; pos < l.Length; pos += 256)
            {
                int n = Math.Min(256, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        [Test]
        public void EverythingOffPassesTheInputDelayedByTheLatencyBitForBit()
        {
            var (e, _) = Make();
            var (x, y) = Material(3 * Rate);
            var (l, r) = Run(e, x, y);
            int la = e.Latency;
            Assert.That(la, Is.GreaterThan(0));
            for (int i = la; i < x.Length; i++)
                if (l[i] != x[i - la] || r[i] != y[i - la]) Assert.Fail($"sample {i}: {l[i]} against {x[i - la]}");
        }

        [Test]
        public void TheLatencyIsTheSameWhicheverStagesAreOn()
        {
            int first = -1;
            foreach (int on in new[] { SP.GateOn, SP.EqOn, SP.CompOn, SP.DriveOn, SP.ClipOn, SP.LimitOn })
            {
                var (e, _) = Make(v => v.Set(on, 1));
                if (first < 0) first = e.Latency;
                Assert.That(e.Latency, Is.EqualTo(first));
            }
            var (all, _) = Make(v => { foreach (int on in new[] { SP.GateOn, SP.EqOn, SP.CompOn, SP.DriveOn, SP.ClipOn, SP.LimitOn }) v.Set(on, 1); });
            Assert.That(all.Latency, Is.EqualTo(first));
        }

        /// <summary>The strip with only one stage on, against that stage's machine alone, over the same material: sample for sample.</summary>
        void StageMatches(Action<ParamValues> strip, Func<(Action<float[], float[], int> Process, int Latency)> engine)
        {
            var (e, _) = Make(strip);
            var (x, y) = Material(3 * Rate);
            var (l, r) = Run(e, x, y);
            var (process, own) = engine();
            var (wl, wr) = RunEngine(process, x, y);
            int pad = e.Latency - own;
            for (int i = 0; i + pad < x.Length; i++)
                if (l[i + pad] != wl[i] || r[i + pad] != wr[i]) Assert.Fail($"sample {i}: {l[i + pad]} against {wl[i]}");
        }

        [Test]
        public void TheEqStageIsNeoEqWithTheSameBands()
        {
            StageMatches(v =>
            {
                v.Set(SP.EqOn, 1);
                v.Set(SP.LoCutOn, 1);
                Set(v, SP.LoCutFreq, 90);
                Set(v, SP.LoShelfGain, 3);
                Set(v, SP.LmFreq, 350);
                Set(v, SP.LmGain, -4);
                Set(v, SP.LmQ, 1.4);
                Set(v, SP.HmGain, 2.5);
                Set(v, SP.HiShelfGain, -2);
                v.Set(SP.HiCutOn, 1);
                Set(v, SP.HiCutFreq, 14000);
            }, () =>
            {
                var v = new ParamValues(NeoBuzz.Eq.Params.Table);
                for (int b = 0; b < 8; b++) v.Set(NeoBuzz.Eq.EqDesign.On(b), 0);
                void Band(int b, NeoBuzz.Eq.BandType t, double hz, double db, double q)
                {
                    v.Set(NeoBuzz.Eq.EqDesign.On(b), 1);
                    v.Set(NeoBuzz.Eq.EqDesign.TypeOf(b), (int)t);
                    v.Set(NeoBuzz.Eq.EqDesign.Freq(b), NeoBuzz.Eq.Params.Table[NeoBuzz.Eq.EqDesign.Freq(b)].Raw(hz));
                    v.Set(NeoBuzz.Eq.EqDesign.Gain(b), NeoBuzz.Eq.Params.Table[NeoBuzz.Eq.EqDesign.Gain(b)].Raw(db));
                    v.Set(NeoBuzz.Eq.EqDesign.Q(b), NeoBuzz.Eq.Params.Table[NeoBuzz.Eq.EqDesign.Q(b)].Raw(q));
                }
                // The strip's defaults: the cuts and shelves at Q 0.707, the shelves and the second bell where the strip puts them.
                double flatQ = 0.7071;
                Band(0, NeoBuzz.Eq.BandType.LowCut, 90, 0, flatQ);
                double D(int p) => SParams.Table[p].Value(SParams.Table[p].Default);
                Band(1, NeoBuzz.Eq.BandType.LowShelf, D(SP.LoShelfFreq), 3, flatQ);
                Band(2, NeoBuzz.Eq.BandType.Bell, 350, -4, 1.4);
                Band(3, NeoBuzz.Eq.BandType.Bell, D(SP.HmFreq), 2.5, D(SP.HmQ));
                Band(4, NeoBuzz.Eq.BandType.HighShelf, D(SP.HiShelfFreq), -2, flatQ);
                Band(5, NeoBuzz.Eq.BandType.HighCut, 14000, 0, flatQ);
                v.Set(NeoBuzz.Eq.EqDesign.Slope(0), 0);
                v.Set(NeoBuzz.Eq.EqDesign.Slope(5), 0);
                var eng = new NeoBuzz.Eq.EqEngine(v);
                eng.SetSampleRate(Rate);
                return ((l, r, n) => eng.Process(l, r, n), 0);
            });
        }

        [Test]
        public void TheCompressorStageIsNeoComp()
        {
            StageMatches(v =>
            {
                v.Set(SP.CompOn, 1);
                v.Set(SP.CompMode, 2);
                Set(v, SP.CompThresh, -24);
                Set(v, SP.CompRatio, 6);
                Set(v, SP.CompMakeup, 4);
            }, () =>
            {
                var v = new ParamValues(NeoBuzz.Comp.Params.Table);
                var t = NeoBuzz.Comp.Params.Table;
                v.Set(NeoBuzz.Comp.P.Mode, 2);
                v.Set(NeoBuzz.Comp.P.Threshold, t[NeoBuzz.Comp.P.Threshold].Raw(-24));
                v.Set(NeoBuzz.Comp.P.Ratio, t[NeoBuzz.Comp.P.Ratio].Raw(6));
                v.Set(NeoBuzz.Comp.P.Makeup, t[NeoBuzz.Comp.P.Makeup].Raw(4));
                var eng = new NeoBuzz.Comp.CompEngine(v);
                eng.SetSampleRate(Rate);
                return ((l, r, n) => eng.Process(l, r, null, null, n), eng.Latency);
            });
        }

        [Test]
        public void TheDriveStageIsNeoDrive()
        {
            StageMatches(v =>
            {
                v.Set(SP.DriveOn, 1);
                v.Set(SP.DriveMode, 3);
                Set(v, SP.DriveAmount, 14);
                Set(v, SP.DriveMix, 0.7);
            }, () =>
            {
                var v = new ParamValues(NeoBuzz.Drive.Params.Table);
                var t = NeoBuzz.Drive.Params.Table;
                v.Set(NeoBuzz.Drive.P.Mode, 3);
                v.Set(NeoBuzz.Drive.P.Drive, t[NeoBuzz.Drive.P.Drive].Raw(14));
                v.Set(NeoBuzz.Drive.P.Mix, t[NeoBuzz.Drive.P.Mix].Raw(0.7));
                var eng = new NeoBuzz.Drive.DriveEngine(v);
                eng.SetSampleRate(Rate);
                return ((l, r, n) => eng.Process(l, r, n), NeoBuzz.Drive.DriveEngine.Latency);
            });
        }

        [Test]
        public void TheClipperStageIsNeoClipperHard()
        {
            StageMatches(v =>
            {
                v.Set(SP.ClipOn, 1);
                Set(v, SP.Ceiling, -12);
            }, () =>
            {
                var v = new ParamValues(NeoBuzz.Clipper.Params.Table);
                var t = NeoBuzz.Clipper.Params.Table;
                v.Set(NeoBuzz.Clipper.P.Shape, 0);
                v.Set(NeoBuzz.Clipper.P.Ceiling, t[NeoBuzz.Clipper.P.Ceiling].Raw(-12));
                var eng = new NeoBuzz.Clipper.ClipperEngine(v);
                eng.SetSampleRate(Rate);
                return ((l, r, n) => eng.Process(l, r, n), eng.Latency);
            });
        }

        [Test]
        public void TheLimiterStageIsNeoLimiter()
        {
            StageMatches(v =>
            {
                v.Set(SP.LimitOn, 1);
                Set(v, SP.Ceiling, -9);
                Set(v, SP.LimRelease, 0.2);
            }, () =>
            {
                var v = new ParamValues(NeoBuzz.Limiter.Params.Table);
                var t = NeoBuzz.Limiter.Params.Table;
                v.Set(NeoBuzz.Limiter.P.Ceiling, t[NeoBuzz.Limiter.P.Ceiling].Raw(-9));
                v.Set(NeoBuzz.Limiter.P.Release, t[NeoBuzz.Limiter.P.Release].Raw(0.2));
                var eng = new NeoBuzz.Limiter.LimiterEngine(v);
                eng.SetSampleRate(Rate);
                return ((l, r, n) => eng.Process(l, r, n), eng.Latency);
            });
        }

        [Test]
        public void TheGateClosesUnderTheThresholdByTheRangeAndOpensAgain()
        {
            var (e, _) = Make(v =>
            {
                v.Set(SP.GateOn, 1);
                Set(v, SP.GateThresh, -40);
                Set(v, SP.GateRange, 30);
                Set(v, SP.GateAttack, 0.001);
                Set(v, SP.GateHold, 0.05);
                Set(v, SP.GateRelease, 0.05);
            });
            // 1 s of a 440 Hz tone at -20 dB, 1 s at -60 dB (under), 1 s at -20 dB again.
            int n = 3 * Rate;
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                double a = (i / Rate) == 1 ? 0.001 : 0.1;
                x[i] = (float)(a * Math.Sin(2 * Math.PI * 440 * i / Rate));
            }
            var (l, _) = Run(e, x, x);
            int la = e.Latency;
            double open1 = Measure.Rms(l, la + Rate / 2, Rate / 4), shut = Measure.Rms(l, la + Rate + Rate / 2, Rate / 4), open2 = Measure.Rms(l, la + 2 * Rate + Rate / 2, Rate / 4);
            Assert.That(Measure.Db(open1 / (0.1 / Math.Sqrt(2))), Is.EqualTo(0).Within(0.05));
            Assert.That(Measure.Db(shut / (0.001 / Math.Sqrt(2))), Is.EqualTo(-30).Within(0.5));
            Assert.That(Measure.Db(open2 / (0.1 / Math.Sqrt(2))), Is.EqualTo(0).Within(0.05));
            Assert.That(e.GateDb, Is.EqualTo(0).Within(0.1));
        }

        [Test]
        public void DeltaIsTheOutputMinusTheDelayedInput()
        {
            Action<ParamValues> setup = v =>
            {
                v.Set(SP.EqOn, 1);
                Set(v, SP.LmGain, 6);
                v.Set(SP.CompOn, 1);
                v.Set(SP.LimitOn, 1);
            };
            var (a, _) = Make(setup);
            var (b, _) = Make(v => { setup(v); v.Set(SP.Delta, 1); });
            var (x, y) = Material(2 * Rate);
            var (wl, _) = Run(a, x, y);
            var (dl, _) = Run(b, x, y);
            int la = a.Latency;
            double worst = 0;
            for (int i = Rate / 2; i < x.Length; i++) worst = Math.Max(worst, Math.Abs(dl[i] - (wl[i] - x[i - la])));
            Assert.That(worst, Is.LessThan(2e-6));
        }

        [Test]
        public void BlocksOf256And128And117And16AreIdentical()
        {
            var (x, y) = Material(3 * Rate, 3);
            float[]? first = null, firstR = null;
            foreach (int block in new[] { 256, 128, 117, 16 })
            {
                var (e, v) = Make(c =>
                {
                    foreach (int on in new[] { SP.GateOn, SP.EqOn, SP.CompOn, SP.DriveOn, SP.ClipOn, SP.LimitOn }) c.Set(on, 1);
                    Set(c, SP.GateThresh, -60);
                    Set(c, SP.LmGain, 3);
                    Set(c, SP.CompThresh, -22);
                    Set(c, SP.DriveAmount, 9);
                    Set(c, SP.Ceiling, -3);
                });
                var (l, r) = Run(e, x, y, block,
                    (Rate, () => Set(v, SP.LmGain, -4)),
                    (Rate + 4321, () => v.Set(SP.DriveOn, 0)),
                    (2 * Rate, () => Set(v, SP.CompThresh, -30)));
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
        public void SwitchingStagesStaysFiniteAndKeepsTheLatency()
        {
            var (e, v) = Make();
            var (x, y) = Material(4 * Rate, 21);
            var ons = new[] { SP.GateOn, SP.EqOn, SP.CompOn, SP.DriveOn, SP.ClipOn, SP.LimitOn };
            var changes = new List<(int, Action)>();
            for (int i = 0; i < 24; i++)
            {
                int k = i;
                changes.Add((Rate / 8 * (i + 1), () => v.Set(ons[k % 6], (k / 6) % 2 == 0 ? 1 : 0)));
            }
            var (l, r) = Run(e, x, y, StripEngine.MaxBlock, changes.ToArray());
            foreach (var s in l) Assert.That(float.IsFinite(s));
            foreach (var s in r) Assert.That(float.IsFinite(s));
            int la = e.Latency;
            // Everything off again at the end: the input, delayed by the latency, bit for bit.
            for (int i = 4 * Rate - Rate / 4; i < 4 * Rate; i++) Assert.That(l[i], Is.EqualTo(x[i - la]), $"sample {i}");
        }

        [Test]
        public void ReadingsReportTheStages()
        {
            var (e, v) = Make(c =>
            {
                c.Set(SP.CompOn, 1);
                Set(c, SP.CompThresh, -30);
                c.Set(SP.LimitOn, 1);
            });
            var x = Measure.Sine(1000, 2 * Rate, 0.7);
            Run(e, x, x);
            Assert.That(e.CompReductionDb, Is.GreaterThan(3));
            Assert.That(e.GateDb, Is.EqualTo(0).Within(0.01));
        }

        [Test]
        public void AllStagesWorkingAllocateNothing()
        {
            var (e, v) = Make(c =>
            {
                foreach (int on in new[] { SP.GateOn, SP.EqOn, SP.CompOn, SP.DriveOn, SP.ClipOn, SP.LimitOn }) c.Set(on, 1);
                Set(c, SP.LmGain, 3);
                Set(c, SP.CompThresh, -22);
                Set(c, SP.DriveAmount, 9);
                Set(c, SP.Ceiling, -3);
            });
            var x = Measure.PinkNoise(Rate, 0.3, 7);
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                int pos = (b % 180) * 256;
                Array.Copy(x, pos, bl, 0, 256);
                Array.Copy(x, pos, br, 0, 256);
                if (b % 7 == 0) Set(v, SP.LmGain, (b % 9) - 4);
                if (b % 61 == 0) Set(v, SP.CompThresh, -30 + (b % 5) * 3);
                if (b % 101 == 0) v.Set(SP.DriveOn, (b / 101) % 2);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 200; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 200));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [TestCase(1, 20, 12)]
        public void RunsFasterThanRealTime(int _, double x64, double arm64)
        {
            var (x, y) = Material(3 * Rate, 5);
            const int Warm = Rate;
            Throughput.Guard("Neo Strip CPU (all six stages on)", Throughput.Floor(x64, arm64), 2, Rate, StripEngine.MaxBlock, () =>
            {
                var (e, _) = Make(c =>
                {
                    foreach (int on in new[] { SP.GateOn, SP.EqOn, SP.CompOn, SP.DriveOn, SP.ClipOn, SP.LimitOn }) c.Set(on, 1);
                    Set(c, SP.LoShelfGain, 2);
                    Set(c, SP.LmGain, 3);
                    Set(c, SP.HmGain, -2);
                    Set(c, SP.HiShelfGain, 1);
                    c.Set(SP.LoCutOn, 1);
                    c.Set(SP.HiCutOn, 1);
                    Set(c, SP.CompThresh, -22);
                    Set(c, SP.DriveAmount, 9);
                    Set(c, SP.Ceiling, -3);
                });
                var bl = new float[StripEngine.MaxBlock];
                var br = new float[StripEngine.MaxBlock];
                void Block(int pos, int n)
                {
                    Array.Copy(x, pos, bl, 0, n);
                    Array.Copy(y, pos, br, 0, n);
                    e.Process(bl, br, n);
                }
                for (int pos = 0; pos < Warm; pos += StripEngine.MaxBlock) Block(pos, StripEngine.MaxBlock);
                return Throughput.InBlocks(StripEngine.MaxBlock, (pos, n) => Block(Warm + pos, n));
            }, Measure.Report);
        }
    }
}
