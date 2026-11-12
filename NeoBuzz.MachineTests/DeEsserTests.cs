using NeoBuzz.Dsp;
using NeoBuzz.DeEsser;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo De-esser (docs/specs/mastering-tools.md §5.12): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class DeEsserTests
    {
        const int Rate = Measure.Rate;
        static readonly string[] Actions = { "Split", "Split linear", "Shelf", "Bell" };

        static (DeEsserEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new DeEsserEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (float[] L, float[] R) Run(DeEsserEngine e, float[] l, float[] r, int block = DeEsserEngine.MaxBlock)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[DeEsserEngine.MaxBlock];
            var br = new float[DeEsserEngine.MaxBlock];
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

        /// <summary>A de-esser at 6.5 kHz, 1.5 octaves, ratio 4, up to 24 dB, absolute threshold unless told otherwise.</summary>
        static Action<ParamValues> Setup(int action, bool relative, double threshold, int lookahead = 0) => v =>
        {
            v.Set(P.Action, action);
            v.Set(P.Mode, relative ? 0 : 1);
            Set(v, P.Freq, 6500);
            Set(v, P.Width, 1.5);
            Set(v, P.Threshold, threshold);
            Set(v, P.Ratio, 4);
            Set(v, P.Range, 24);
            Set(v, P.Lookahead, lookahead);
        };

        static int LatencyOf(int action, int lookahead) => (int)Math.Round(lookahead * 0.001 * Rate) + (action == 1 ? DeEsserEngine.FirHalf : 0);

        static double Level(float[] x, int from, int n) => Measure.Db(Measure.Rms(x, from, n));

        // ---- nothing below the threshold, latency --------------------------------------------------

        [TestCase(0, 0)]
        [TestCase(0, 2)]
        [TestCase(1, 0)]
        [TestCase(1, 3)]
        [TestCase(2, 1)]
        [TestCase(3, 1)]
        public void NothingBelowTheThresholdGivesTheInputDelayedBitForBit(int action, int lookahead)
        {
            var l = Measure.PinkNoise(Rate * 2, 0.05, 3);
            var r = Measure.PinkNoise(Rate * 2, 0.05, 4);
            var (e, _) = Make(Setup(action, false, -10, lookahead));   // the noise peaks near −15 dBFS in the band at most
            var (ol, or) = Run(e, l, r);
            int la = LatencyOf(action, lookahead);
            Assert.That(e.Latency, Is.EqualTo(la));
            for (int i = la; i < l.Length; i++)
                if (ol[i] != l[i - la] || or[i] != r[i - la]) Assert.Fail($"differs at {i}");
            TestContext.Out.WriteLine($"{Actions[action]}, lookahead {lookahead} ms: latency {la} samples, bit for bit");
        }

        [Test]
        public void TheLatencyIsTheLookaheadPlusTheLinearBand()
        {
            foreach (int la in new[] { 0, 1, 5 })
                foreach (int action in new[] { 0, 1, 2, 3 })
                {
                    var (e, _) = Make(Setup(action, false, -10, la));
                    var impulse = new float[Rate];
                    impulse[2000] = 1;
                    var (ol, _) = Run(e, impulse, new float[Rate]);
                    int peak = 0;
                    for (int i = 0; i < ol.Length; i++) if (Math.Abs(ol[i]) > Math.Abs(ol[peak])) peak = i;
                    Assert.That(peak, Is.EqualTo(2000 + LatencyOf(action, la)), $"{Actions[action]} {la} ms");
                    Assert.That(e.Latency, Is.EqualTo(LatencyOf(action, la)));
                }
        }

        // ---- reduction against the design ----------------------------------------------------------

        /// <summary>A tone at the centre: the reduction is (level − threshold)·(1 − 1/ratio), capped at Max reduction.</summary>
        [TestCase(0, false, -30, 0.3, 14.25)]    // the tone's level is −10.46 dB; 19.54 over, ×0.75
        [TestCase(1, false, -30, 0.3, 14.25)]
        [TestCase(2, false, -30, 0.3, 14.25)]    // a shelf's midpoint is half the reduction (checked here; its plateau in ShelfActsAbove…)
        [TestCase(3, false, -30, 0.3, 14.25)]
        [TestCase(0, true, -20, 0.3, 15.0)]      // relative: the band's level is the whole signal's, 0 dB; 20 over, ×0.75
        [TestCase(1, true, -20, 0.3, 15.0)]
        [TestCase(3, true, -12, 0.1, 9.0)]
        public void TheReductionOfATonePlayedAtTheCentreMatchesTheDesign(int action, bool relative, double threshold, double amplitude, double expected)
        {
            double level = Measure.Db(amplitude);
            if (!relative) expected = (level - threshold) * 0.75;
            if (action == 2) expected /= 2;
            var tone = Measure.Sine(6500, Rate * 2, amplitude);
            var (e, _) = Make(Setup(action, relative, threshold));
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            int la = LatencyOf(action, 0);
            double got = Level(tone, Rate, Rate) - Level(ol, Rate, Rate);
            TestContext.Out.WriteLine($"{Actions[action]} {(relative ? "relative" : "absolute")}: reduction {got:F2} dB against the design {expected:F2} dB");
            Assert.That(got, Is.EqualTo(expected).Within(0.25), "reduction against the design");
            _ = la;
        }

        [Test]
        public void MaxReductionCapsTheReduction()
        {
            var tone = Measure.Sine(6500, Rate * 2, 0.5);
            var (e, _) = Make(v => { Setup(0, false, -50)(v); Set(v, P.Range, 6); });
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            double got = Level(tone, Rate, Rate) - Level(ol, Rate, Rate);
            Assert.That(got, Is.EqualTo(6).Within(0.3));
        }

        [Test]
        public void SplitLeavesTheRestOfTheSpectrumAlone()
        {
            // A strong sibilant tone and a low tone: the split cuts the tone in the band, not the one at 300 Hz.
            int n = Rate * 2;
            var low = Measure.Sine(300, n, 0.1);
            var high = Measure.Sine(6500, n, 0.3);
            var mix = new float[n];
            for (int i = 0; i < n; i++) mix[i] = low[i] + high[i];
            foreach (int action in new[] { 0, 1 })
            {
                var (e, _) = Make(Setup(action, false, -30));
                var (ol, _) = Run(e, mix, (float[])mix.Clone());
                int la = LatencyOf(action, 0);
                // What is left at 300 Hz: correlate against the tone (a projection), the same on both sides.
                double Proj(float[] x, double hz, int from, int len)
                {
                    double a = 0, b = 0;
                    for (int i = 0; i < len; i++)
                    {
                        double ph = 2 * Math.PI * hz * (from + i) / Rate;
                        a += x[from + i] * Math.Cos(ph);
                        b += x[from + i] * Math.Sin(ph);
                    }
                    return 2 * Math.Sqrt(a * a + b * b) / len;
                }
                double lowIn = Proj(mix, 300, Rate, Rate / 2), lowOut = Proj(ol, 300, Rate + la, Rate / 2);
                double hiIn = Proj(mix, 6500, Rate, Rate / 2), hiOut = Proj(ol, 6500, Rate + la, Rate / 2);
                TestContext.Out.WriteLine($"{Actions[action]}: 300 Hz {Measure.Db(lowOut / lowIn):F2} dB, 6.5 kHz {Measure.Db(hiOut / hiIn):F2} dB");
                Assert.That(Measure.Db(lowOut / lowIn), Is.EqualTo(0).Within(0.1));
                Assert.That(Measure.Db(hiOut / hiIn), Is.LessThan(-10));
            }
        }

        [Test]
        public void ShelfActsAboveTheFrequencyAndBellAroundIt()
        {
            double[] hz = { 800, 6500, 14000 };
            double[] gains = new double[3];
            foreach (int action in new[] { 2, 3 })
            {
                for (int k = 0; k < 3; k++)
                {
                    // A loud tone at the band's centre drives the reduction; a small probe tone is measured through the same state.
                    int n = Rate * 3;
                    var drive = Measure.Sine(6500, n, 0.3);
                    var probe = Measure.Sine(hz[k], n, 0.01);
                    var mix = new float[n];
                    for (int i = 0; i < n; i++) mix[i] = drive[i] + probe[i];
                    var (e, _) = Make(Setup(action, false, -30));
                    var (ol, _) = Run(e, mix, (float[])mix.Clone());
                    double Proj(float[] x, double f, int from, int len)
                    {
                        double a = 0, b = 0;
                        for (int i = 0; i < len; i++) { double ph = 2 * Math.PI * f * (from + i) / Rate; a += x[from + i] * Math.Cos(ph); b += x[from + i] * Math.Sin(ph); }
                        return 2 * Math.Sqrt(a * a + b * b) / len;
                    }
                    if (hz[k] == 6500) { gains[k] = Measure.Db(Proj(ol, 6500, Rate * 2, Rate) / Proj(mix, 6500, Rate * 2, Rate)); }
                    else gains[k] = Measure.Db(Proj(ol, hz[k], Rate * 2, Rate) / 0.01);
                }
                TestContext.Out.WriteLine($"{Actions[action]}: 800 Hz {gains[0]:F2}, 6.5 kHz {gains[1]:F2}, 14 kHz {gains[2]:F2} dB (the design at the centre -14.5)");
                Assert.That(gains[0], Is.EqualTo(0).Within(0.4), "far below the frequency");
                Assert.That(gains[1], Is.EqualTo(action == 3 ? -14.66 : -7.33).Within(0.8), "at the centre (a shelf's midpoint is half the reduction)");
                if (action == 2) Assert.That(gains[2], Is.EqualTo(-14.66).Within(1.0), "the shelf's plateau");
                if (action == 3) Assert.That(Math.Abs(gains[2]), Is.LessThan(3), "a bell leaves the far top");
                else Assert.That(gains[2], Is.LessThan(-10), "a shelf cuts the top");
            }
        }

        // ---- listen, delta, blocks, determinism ---------------------------------------------------

        [Test]
        public void ListenGivesTheBandAndNotTheRest()
        {
            int n = Rate;
            var l = Measure.Sine(6500, n, 0.2);
            var low = Measure.Sine(200, n, 0.2);
            var (e, _) = Make(v => { Setup(0, false, 0)(v); v.Set(P.Listen, 1); });
            var (ol, _) = Run(e, l, (float[])l.Clone());
            var (e2, _) = Make(v => { Setup(0, false, 0)(v); v.Set(P.Listen, 1); });
            var (ol2, _) = Run(e2, low, (float[])low.Clone());
            double inBand = Level(ol, Rate / 2, Rate / 4), outBand = Level(ol2, Rate / 2, Rate / 4);
            TestContext.Out.WriteLine($"Listen: 6.5 kHz {inBand:F1} dB (in {Level(l, Rate / 2, Rate / 4):F1}), 200 Hz {outBand:F1} dB");
            Assert.That(inBand, Is.EqualTo(Level(l, Rate / 2, Rate / 4)).Within(0.3));
            Assert.That(outBand, Is.LessThan(inBand - 40));
        }

        [Test]
        public void DeltaIsWhatTheDeEsserTakesOut()
        {
            var tone = Measure.Sine(6500, Rate * 2, 0.3);
            var (e, _) = Make(v => { Setup(0, false, -30)(v); v.Set(P.Delta, 1); });
            var (ol, _) = Run(e, tone, (float[])tone.Clone());
            // The reduction is 14.25 dB, so the delta of a tone is (1 − 0.1936) of it: −1.87 dB under the input.
            double got = Level(ol, Rate, Rate) - Level(tone, Rate, Rate);
            Assert.That(got, Is.EqualTo(20 * Math.Log10(1 - Math.Pow(10, -14.25 / 20))).Within(0.3));
            var quiet = Measure.PinkNoise(Rate, 0.01, 9);
            var (e2, _) = Make(v => { Setup(0, false, -10)(v); v.Set(P.Delta, 1); });
            var (od, _) = Run(e2, quiet, (float[])quiet.Clone());
            Assert.That(Measure.Peak(od, 2000, Rate - 2000), Is.EqualTo(0), "nothing reduced: the delta is silence");
        }

        [TestCase(1)]
        [TestCase(3)]
        public void BlocksOfAnySizeGiveTheSameBits(int action)
        {
            var l = Measure.PinkNoise(Rate * 2, 0.3, 11);
            var r = Measure.PinkNoise(Rate * 2, 0.3, 12);
            for (int i = 0; i < l.Length; i++) { double s = Math.Sin(2 * Math.PI * 7000 * i / Rate) * (i % 24000 < 8000 ? 0.4 : 0); l[i] += (float)s; r[i] += (float)s; }
            float[] first = null;
            foreach (int block in new[] { 256, 117, 16 })
            {
                var (e, _) = Make(Setup(action, true, -14, 2));
                var (ol, _) = Run(e, l, r, block);
                if (first == null) first = ol;
                else Assert.That(ol, Is.EqualTo(first), $"block {block}");
            }
            var (e2, _) = Make(Setup(action, true, -14, 2));
            Assert.That(Run(e2, l, r).L, Is.EqualTo(first), "a second run");
            Assert.That(Measure.Peak(first, 0, first.Length), Is.LessThan(2));
        }

        [Test]
        public void MovingTheBandWhileItReducesDoesNotClick()
        {
            // Split linear redesigns its FIR and crossfades: a sweep of the frequency under a steady tone stays smooth.
            int n = Rate * 3;
            var tone = Measure.Sine(6500, n, 0.3);
            var (e, v) = Make(Setup(1, false, -30));
            var ol = new float[n];
            var bl = new float[256];
            var br = new float[256];
            for (int pos = 0; pos + 256 <= n; pos += 256)
            {
                if (pos > Rate / 2 && pos % 2048 == 0) Set(v, P.Freq, 6000 + (pos / 2048 % 20) * 50);
                Array.Copy(tone, pos, bl, 0, 256);
                Array.Copy(tone, pos, br, 0, 256);
                e.Process(bl, br, 256);
                Array.Copy(bl, 0, ol, pos, 256);
            }
            double worst = 0;
            for (int i = Rate; i < n - 1; i++) worst = Math.Max(worst, Math.Abs(ol[i + 1] - ol[i]));
            TestContext.Out.WriteLine($"largest sample step {worst:F3} against the tone's own {0.3 * 2 * Math.PI * 6500 / Rate:F3}");
            Assert.That(worst, Is.LessThan(0.3 * 2 * Math.PI * 6500 / Rate * 1.5));
        }

        // ---- allocation, CPU -----------------------------------------------------------------------

        [Test]
        public void NothingIsAllocatedPerBlock()
        {
            var l = Measure.PinkNoise(256 * 4, 0.3, 5);
            var r = Measure.PinkNoise(256 * 4, 0.3, 6);
            var (e, v) = Make(Setup(1, true, -20, 2));
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                if (b % 50 == 0) Set(v, P.Freq, 5000 + (b / 50 % 10) * 300);
                if (b % 400 == 0) v.Set(P.Action, (b / 400) % 4);
                Array.Copy(l, (b % 4) * 256, bl, 0, 256);
                Array.Copy(r, (b % 4) * 256, br, 0, 256);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 60; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 60));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [TestCase(0, 190, 130)]
        [TestCase(1, 13, 9)]
        [TestCase(3, 220, 150)]
        public void RunsFasterThanRealTime(int action, double x64, double arm64)
        {
            int n = Rate * 3;
            var l = Measure.PinkNoise(n, 0.2, 1);
            var r = Measure.PinkNoise(n, 0.2, 2);
            for (int i = 0; i < n; i++) { float s = (float)(Math.Sin(2 * Math.PI * 7000 * i / Rate) * (i % 12000 < 4000 ? 0.4 : 0)); l[i] += s; r[i] += s; }
            const int Warm = Rate;
            Throughput.Guard($"Neo De-esser CPU ({Actions[action]}, reducing)", Throughput.Floor(x64, arm64), 2,
                Rate, DeEsserEngine.MaxBlock, () =>
            {
                var (e, _) = Make(Setup(action, true, -14, 2));
                var bl = new float[DeEsserEngine.MaxBlock];
                var br = new float[DeEsserEngine.MaxBlock];
                void Block(int pos, int len)
                {
                    Array.Copy(l, pos, bl, 0, len);
                    Array.Copy(r, pos, br, 0, len);
                    e.Process(bl, br, len);
                }
                for (int pos = 0; pos < Warm; pos += DeEsserEngine.MaxBlock) Block(pos, DeEsserEngine.MaxBlock);
                return Throughput.InBlocks(DeEsserEngine.MaxBlock, (pos, len) => Block(Warm + pos, len));
            }, Measure.Report);
        }
    }
}
