using System.Diagnostics;
using System.Numerics;
using NeoBuzz.Dsp;
using NeoBuzz.Multiband;
using BP = NeoBuzz.Multiband.MultibandEngine.BandParam;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Multiband (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class MultibandTests
    {
        const int Rate = Measure.Rate;

        static string MachinesDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Machines"));

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static int B(int band, BP what) => MultibandEngine.Param(band, what);

        /// <summary>Every band at 1:1 and 0 dB makeup: the crossovers alone.</summary>
        static void Unity(ParamValues v)
        {
            for (int b = 0; b < 4; b++) Set(v, B(b, BP.Ratio), 1);
        }

        static Action<ParamValues> Crossovers(double f1, double f2, double f3, int bands = 4) => v =>
        {
            Set(v, P.Xo1, f1);
            Set(v, P.Xo2, f2);
            Set(v, P.Xo3, f3);
            v.Set(P.Bands, bands == 4 ? 1 : 0);
        };

        /// <summary>Band <paramref name="b"/> compressing a steady level: threshold, ratio, knee 0, the given times, the others at 1:1.</summary>
        static Action<ParamValues> OneBand(int b, double threshold, double ratio, double knee = 0, double attack = 0.00005, double release = 1, double range = 24) => v =>
        {
            Unity(v);
            Set(v, B(b, BP.Threshold), threshold);
            if (double.IsPositiveInfinity(ratio)) v.Set(B(b, BP.Ratio), Params.Table[B(b, BP.Ratio)].Max);
            else Set(v, B(b, BP.Ratio), ratio);
            Set(v, B(b, BP.Knee), knee);
            Set(v, B(b, BP.Attack), attack);
            Set(v, B(b, BP.Release), release);
            Set(v, B(b, BP.Range), range);
        };

        static Action<ParamValues> Both(params Action<ParamValues>[] setups) => v => { foreach (var s in setups) s?.Invoke(v); };

        /// <summary>The crossovers the engine runs for a setup: the parameters' values (a frequency lands within 0.04 % of what was asked).</summary>
        static Xover XoverOf(Action<ParamValues> setup, int rate)
        {
            var v = new ParamValues(Params.Table);
            setup(v);
            return MultibandDesign.Effective(v, rate);
        }

        static (MultibandEngine Engine, ParamValues Values) Make(Action<ParamValues> setup, int rate = Rate, PhaseMode phase = PhaseMode.Linear)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Phase, (int)phase);
            setup?.Invoke(v);
            var e = new MultibandEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static (float[] L, float[] R) Run(MultibandEngine e, float[] l, float[] r, int block = MultibandEngine.MaxBlock, Action<int> before = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[MultibandEngine.MaxBlock];
            var br = new float[MultibandEngine.MaxBlock];
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

        static float[] Noise(int length, double rms, uint seed) => Measure.PinkNoise(length, rms, seed);

        static double Db(double x) => Measure.Db(x);

        static float[] Constant(double db, int n)
        {
            var x = new float[n];
            float a = (float)DspMath.DbToGain(db);
            for (int i = 0; i < n; i++) x[i] = a;
            return x;
        }

        /// <summary>A steady level that fades in over 0.2 s (a raised cosine), so the bands don't overshoot at its onset and the peak detector holds no overshoot.</summary>
        static float[] Steady(double db, int n)
        {
            var x = Constant(db, n);
            int fade = Rate / 5;
            for (int i = 0; i < Math.Min(n, fade); i++) x[i] *= (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / fade));
            return x;
        }

        static float[] Impulse(int length, int at = 0)
        {
            var x = new float[length];
            x[at] = 1;
            return x;
        }

        /// <summary>The DTFT of a stretch of an impulse response at <paramref name="hz"/>, its time origin at <paramref name="origin"/>.</summary>
        static Complex Dtft(float[] h, int from, int to, int origin, double hz, int rate)
        {
            double w = 2 * Math.PI * hz / rate, re = 0, im = 0;
            for (int k = Math.Max(0, from); k < Math.Min(h.Length, to); k++)
            {
                double a = w * (k - origin);
                re += h[k] * Math.Cos(a);
                im -= h[k] * Math.Sin(a);
            }
            return new Complex(re, im);
        }

        static double[] LogFrequencies(int count, double lo = 20, double hi = 20000)
        {
            var f = new double[count];
            for (int i = 0; i < count; i++) f[i] = lo * Math.Pow(hi / lo, i / (double)(count - 1));
            return f;
        }

        static int Bits(float x) => BitConverter.SingleToInt32Bits(x);

        static double MaxStep(float[] y, int from)
        {
            double m = 0;
            for (int i = Math.Max(1, from); i < y.Length; i++) m = Math.Max(m, Math.Abs(y[i] - y[i - 1]));
            return m;
        }

        static float[] Sines(int length, double amplitude, params double[] hz)
        {
            var x = new float[length];
            foreach (double f in hz)
                for (int i = 0; i < length; i++) x[i] += (float)(amplitude * Math.Sin(2 * Math.PI * f * i / Rate));
            return x;
        }

        internal static readonly (string Name, double F1, double F2, double F3, int Bands)[] CrossoverSets =
        {
            ("4 bands, 120 Hz / 1 kHz / 6 kHz (defaults)", 120, 1000, 6000, 4),
            ("4 bands, 20 / 40 / 80 Hz (the lowest)", 20, 40, 80, 4),
            ("4 bands, 5 / 10 / 20 kHz (the highest)", 5000, 10000, 20000, 4),
            ("4 bands, 1 / 1.1 / 1.2 kHz (close)", 1000, 1100, 1200, 4),
            ("3 bands, 200 Hz / 2.5 kHz", 200, 2500, 6000, 3),
        };

        // ------------------------------------------------------------------ unity and Mix 0 %

        [TestCase(0, 4, 0, 1.0)]
        [TestCase(0, 3, 0, 1.0)]
        [TestCase(0, 4, 1, 0.0)]
        [TestCase(0, 4, 1, 0.5)]
        public void UnityPassesTheInputDelayedByTheLatencyBitForBit(int phaseMode, int bands, int stereo, double link)
        {
            var phase = (PhaseMode)phaseMode;
            // Every band at 1:1 and 0 dB: the output is the input L samples later, bit for bit, negative zeros and the smallest
            // numbers included, whatever the crossovers, the band count and the detector (blocks of 117).
            var (e, _) = Make(Both(Unity, Crossovers(80, 700, 5000, bands), v => { v.Set(P.Stereo, stereo); Set(v, P.Link, link * 100); }), phase: phase);
            int L = e.Latency;
            var x = Noise(Rate, 0.3, 1);
            var y = Noise(Rate, 0.3, 2);
            x[5000] = -0f;
            x[5001] = float.Epsilon;
            y[5002] = -float.Epsilon;
            var (l, r) = Run(e, x, y, 117);
            for (int i = 0; i < x.Length; i++)
            {
                float ex = i >= L ? x[i - L] : 0, ey = i >= L ? y[i - L] : 0;
                Assert.That(Bits(l[i]), Is.EqualTo(Bits(ex)), $"left {i}");
                Assert.That(Bits(r[i]), Is.EqualTo(Bits(ey)), $"right {i}");
            }
            Assert.That(L, Is.EqualTo(4607));
        }

        [TestCase(44100, 4607)]
        [TestCase(48000, 4607)]
        [TestCase(88200, 9215)]
        [TestCase(96000, 9215)]
        [TestCase(192000, 18431)]
        public void TheLatencyIsTheFirsCentreAndOnePartition(int rate, int latency)
        {
            var (e, v) = Make(Unity, rate);
            Assert.That(e.Latency, Is.EqualTo(latency));
            Assert.That(MultibandDesign.LinearLatency(rate), Is.EqualTo(latency));
            // The latency doesn't move with any setting but Phase.
            v.Set(P.Bands, 0);
            Set(v, P.Xo1, 25);
            Set(v, P.Xo2, 18000);
            e.Process(new float[256], new float[256], 256);
            Assert.That(e.Latency, Is.EqualTo(latency));
            Measure.Report($"Neo Multiband latency at {rate} Hz: {latency} samples ({1000.0 * latency / rate:F1} ms), linear phase; 0 in minimum phase");
        }

        [TestCase(0)]
        [TestCase(1)]
        public void MixZeroIsTheDryBitForBit(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            // Every band compressing hard (−60 dB, ∞:1, makeup +12 dB) at Mix 0 %: the dry signal, bit for bit. In linear phase that is
            // the input delayed by the latency; in minimum phase the bands' sum at unity (the crossovers' allpasses).
            Action<ParamValues> hard = v =>
            {
                for (int b = 0; b < 4; b++)
                {
                    Set(v, B(b, BP.Threshold), -60);
                    v.Set(B(b, BP.Ratio), Params.Table[B(b, BP.Ratio)].Max);
                    Set(v, B(b, BP.Makeup), 12);
                }
                v.Set(P.Mix, 0);
            };
            var (e, _) = Make(hard, phase: phase);
            var (u, _) = Make(Unity, phase: phase);
            var x = Noise(Rate, 0.3, 3);
            var y = Noise(Rate, 0.3, 4);
            x[7000] = -0f;
            x[7001] = float.Epsilon;
            var (l, r) = Run(e, x, y, 117);
            var (ul, ur) = Run(u, x, y, 117);
            int L = e.Latency;
            for (int i = 0; i < x.Length; i++)
            {
                Assert.That(Bits(l[i]), Is.EqualTo(Bits(ul[i])), $"left {i}");
                Assert.That(Bits(r[i]), Is.EqualTo(Bits(ur[i])), $"right {i}");
                if (phase == PhaseMode.Linear) Assert.That(Bits(l[i]), Is.EqualTo(Bits(i >= L ? x[i - L] : 0f)), $"delayed input {i}");
            }
        }

        [Test]
        public void MinimumPhaseIsTheThreeAllpassesAndZeroLatency()
        {
            // Minimum phase, every band at unity: the input through the three crossovers' allpasses: flat, and the phase the
            // three second-order allpasses (Q 0.71) at the crossovers give. No latency.
            foreach (int rate in new[] { 44100, 48000 })
                foreach (var set in CrossoverSets)
                {
                    var (e, _) = Make(Both(Unity, Crossovers(set.F1, set.F2, set.F3, set.Bands)), rate, PhaseMode.Minimum);
                    Assert.That(e.Latency, Is.EqualTo(0));
                    var x = Impulse(1 << 16);
                    var (h, _) = Run(e, x, x);
                    var xo = XoverOf(Crossovers(set.F1, set.F2, set.F3, set.Bands), rate);
                    double worstDb = 0, worstPhase = 0;
                    foreach (double hz in LogFrequencies(160))
                    {
                        var got = Dtft(h, 0, h.Length, 0, hz, rate);
                        Complex want = 1;
                        double s = MultibandDesign.Warp(hz, rate);
                        for (int k = 0; k < 3; k++)
                        {
                            double w = s / MultibandDesign.Warp(xo[k], rate);
                            want *= new Complex(1 - w * w, -Math.Sqrt(2) * w) / new Complex(1 - w * w, Math.Sqrt(2) * w);
                        }
                        worstDb = Math.Max(worstDb, Math.Abs(Db(got.Magnitude)));
                        worstPhase = Math.Max(worstPhase, (got / want).Phase is var ph ? Math.Abs(ph) : 0);
                    }
                    Measure.Report($"Neo Multiband minimum phase at unity, {set.Name}, {rate} Hz: flat within {worstDb:F6} dB, the allpasses' phase within {worstPhase * 180 / Math.PI:F5}°");
                    Assert.That(worstDb, Is.LessThan(0.001), set.Name);
                    Assert.That(worstPhase, Is.LessThan(1e-4), set.Name);
                }
        }

        // ------------------------------------------------------------------ the crossovers

        /// <summary>Band <paramref name="band"/>'s impulse response, soloed at unity (Mix is ignored while a band is soloed).</summary>
        static float[] BandResponse(Action<ParamValues> setup, int band, int rate, PhaseMode phase, int length)
        {
            var (e, _) = Make(Both(Unity, setup, v => v.Set(B(band, BP.Solo), 1)), rate, phase);
            var x = Impulse(length);
            return Run(e, x, x).L;
        }

        [TestCase(0)]
        [TestCase(1)]
        public void EachBandIsItsDesignAndTheySumFlat(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            // Each band soloed: its response (DTFT of the impulse response, 160 frequencies, 20 Hz – 20 kHz) against
            // MultibandDesign.Band, where the band is above −40 dB; the four (or three) bands' responses added: flat.
            // Linear phase: zero phase about the latency, so the bands' magnitudes are compared as real numbers too.
            var lines = new List<string>();
            double worstAll = 0;
            foreach (int rate in new[] { 44100, 48000 })
                foreach (var set in CrossoverSets)
                {
                    int length = phase == PhaseMode.Linear ? 2 * MultibandDesign.LinearLatency(rate) + 16 : 1 << 16;
                    int L = phase == PhaseMode.Linear ? MultibandDesign.LinearLatency(rate) : 0;
                    int D = MultibandDesign.HalfLength(rate);
                    var xo = XoverOf(Crossovers(set.F1, set.F2, set.F3, set.Bands), rate);
                    var sum = new double[length];
                    double worstDb = 0, worstLin = 0;
                    for (int b = 0; b < set.Bands; b++)
                    {
                        var h = BandResponse(Crossovers(set.F1, set.F2, set.F3, set.Bands), b, rate, phase, length);
                        for (int i = 0; i < length; i++) sum[i] += h[i];
                        int from = phase == PhaseMode.Linear ? L - D : 0, to = phase == PhaseMode.Linear ? L + D + 1 : length;
                        foreach (double hz in LogFrequencies(160))
                        {
                            double want = MultibandDesign.Band(b, set.Bands, hz, xo, rate);
                            var got = Dtft(h, from, to, L, hz, rate);
                            double lin = phase == PhaseMode.Linear ? Math.Abs(got.Real - want) : Math.Abs(got.Magnitude - want);
                            worstLin = Math.Max(worstLin, lin);
                            if (want > 0.01) worstDb = Math.Max(worstDb, Math.Abs(Db(got.Magnitude / want)));
                        }
                    }
                    // The sum: the delayed impulse (linear), the allpasses (minimum; flat).
                    double sumErr = 0, sumFlat = 0;
                    if (phase == PhaseMode.Linear)
                        for (int i = 0; i < length; i++) sumErr = Math.Max(sumErr, Math.Abs(sum[i] - (i == L ? 1 : 0)));
                    var sumF = sum.Select(s => (float)s).ToArray();
                    foreach (double hz in LogFrequencies(160)) sumFlat = Math.Max(sumFlat, Math.Abs(Db(Dtft(sumF, 0, length, L, hz, rate).Magnitude)));
                    lines.Add($"{set.Name} {rate}: bands within {worstDb:F4} dB of the design (abs {worstLin:E1}), sum flat within {sumFlat:F6} dB" +
                              (phase == PhaseMode.Linear ? $", sum − delayed impulse {sumErr:E1}" : ""));
                    if (set.F1 >= 30) worstAll = Math.Max(worstAll, worstDb);
                    Assert.That(sumFlat, Is.LessThan(0.0001), set.Name);
                    if (phase == PhaseMode.Linear) Assert.That(sumErr, Is.LessThan(1e-6), set.Name);
                    Assert.That(worstDb, Is.LessThan(phase == PhaseMode.Minimum ? 0.001 : set.F1 >= 30 ? 0.001 : 0.15), set.Name);
                }
            Measure.Report($"Neo Multiband {phase} crossovers: " + string.Join("; ", lines));
        }

        [Test]
        public void EachBandIsMinusSixDbAtItsCrossovers()
        {
            // Linkwitz–Riley: the low and high shares are −6.02 dB at a crossover; band 1 at crossover 1, band 4 at crossover 3.
            var xo = MultibandDesign.Effective(120, 1000, 6000, Rate);
            Assert.That(Db(MultibandDesign.Band(0, 4, 120, xo, Rate)), Is.EqualTo(-6.0206).Within(0.01));
            Assert.That(Db(MultibandDesign.Band(3, 4, 6000, xo, Rate)), Is.EqualTo(-6.0206).Within(0.01));
            // 24 dB/oct: band 1 two octaves above crossover 1 (480 Hz) about −48 dB.
            Assert.That(Db(MultibandDesign.Band(0, 4, 480, xo, Rate)), Is.EqualTo(-48.2).Within(0.5));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void BandGainsShapeTheResponseAsDesignedInBothModes(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            // Fixed band gains (1:1, makeup +6, −6, +3 and −12 dB): the response (DTFT of the impulse response) is
            // Σ gain · |band| in both modes, the bands sharing one phase.
            double[] makeup = { 6, -6, 3, -12 };
            Action<ParamValues> gains = v => { Unity(v); for (int b = 0; b < 4; b++) Set(v, B(b, BP.Makeup), makeup[b]); };
            var (e, _) = Make(gains, phase: phase);
            int length = phase == PhaseMode.Linear ? 2 * e.Latency + 16 : 1 << 16;
            var x = Impulse(length);
            var (h, _) = Run(e, x, x);
            int L = e.Latency;
            var xo = XoverOf(gains, Rate);
            double worst = 0;
            foreach (double hz in LogFrequencies(160))
            {
                double want = MultibandDesign.Response(4, hz, xo, Rate, DspMath.DbToGain(6.0), DspMath.DbToGain(-6.0), DspMath.DbToGain(3.0), DspMath.DbToGain(-12.0));
                double got = Dtft(h, 0, length, L, hz, Rate).Magnitude;
                worst = Math.Max(worst, Math.Abs(Db(got / want)));
            }
            Measure.Report($"Neo Multiband {phase}: band gains +6/−6/+3/−12 dB, the response within {worst:F4} dB of Σ gain · |band|");
            Assert.That(worst, Is.LessThan(0.01));
        }

        [Test]
        public void TheBandsSumToTheDryWhileTheCrossoversMove()
        {
            // The linear split on its own: every partition, the crossovers somewhere new (a redesign starting every 4
            // partitions, its steps taken as the engine takes them, then crossfading): the bands always add up to the delayed input.
            var split = new LinearSplit(Rate, MultibandDesign.Effective(120, 1000, 6000, Rate));
            var rng = new Random(3);
            var x = Noise(64 * split.P, 0.3, 5);
            var y = Noise(64 * split.P, 0.3, 6);
            var inL = new float[split.P];
            var inR = new float[split.P];
            double worst = 0;
            int designs = split.Designs;
            for (int k = 0; k < 64; k++)
            {
                Array.Copy(x, k * split.P, inL, 0, split.P);
                Array.Copy(y, k * split.P, inR, 0, split.P);
                var target = MultibandDesign.Effective(20 * Math.Pow(1000, rng.NextDouble()), 20 * Math.Pow(1000, rng.NextDouble()), 20 * Math.Pow(1000, rng.NextDouble()), Rate);
                split.Process(inL, inR, k % 7 != 3, target);
                DesignSteps(split);
                for (int n = 0; n < split.P; n++)
                {
                    double l = 0, r = 0;
                    for (int b = 0; b < 4; b++)
                    {
                        l += split.BandL[b][n];
                        r += split.BandR[b][n];
                    }
                    worst = Math.Max(worst, Math.Max(Math.Abs(l - split.DryL[n]), Math.Abs(r - split.DryR[n])));
                }
            }
            Measure.Report($"Neo Multiband linear split, crossovers moving every partition: {split.Designs - designs} redesigns in 64 partitions, the bands' sum within {worst:E1} of the delayed input");
            Assert.That(split.Designs - designs, Is.EqualTo(16), "at most every 4th partition");
            Assert.That(worst, Is.LessThan(2e-7));
        }

        /// <summary>The design steps the engine takes during one partition: one every DesignStride samples but at its first.</summary>
        static void DesignSteps(LinearSplit split)
        {
            for (int q = LinearSplit.DesignStride; q < split.P; q += LinearSplit.DesignStride) split.DesignStep();
        }

        [Test]
        public void TransformInPartsIsTheWholeTransformBitForBit()
        {
            // The FFT the crossovers' design runs in steps: in parts of any size, from the bit reversal or from data written
            // already reversed, the same bits as one Transform, forward and inverse.
            var rng = new Random(7);
            foreach (int size in new[] { 8, 16, 64, 1024, 16384 })
            {
                var fft = new ComplexFft(size);
                var re = new float[size];
                var im = new float[size];
                for (int i = 0; i < size; i++)
                {
                    re[i] = (float)(rng.NextDouble() - 0.5);
                    im[i] = (float)(rng.NextDouble() - 0.5);
                }
                foreach (bool inverse in new[] { false, true })
                {
                    var wr = (float[])re.Clone();
                    var wi = (float[])im.Clone();
                    fft.Transform(wr, wi, inverse);
                    foreach (int budget in new[] { 1, 7, 8, 100, 1000, 10240, int.MaxValue / 8 })
                        foreach (bool reversed in new[] { false, true })
                        {
                            var pr = new float[size];
                            var pi = new float[size];
                            for (int i = 0; i < size; i++)
                            {
                                int at = reversed ? fft.Reversed(i) : i;
                                pr[at] = re[i];
                                pi[at] = im[i];
                            }
                            var progress = new FftProgress { Pass = reversed ? 1 : 0 };
                            int calls = 1;
                            while (!fft.TransformPart(pr, pi, inverse, ref progress, budget)) calls++;
                            for (int i = 0; i < size; i++)
                            {
                                Assert.That(Bits(pr[i]), Is.EqualTo(Bits(wr[i])), $"{size} points, budget {budget}, {(reversed ? "reversed" : "in order")}, re {i}");
                                Assert.That(Bits(pi[i]), Is.EqualTo(Bits(wi[i])), $"{size} points, budget {budget}, {(reversed ? "reversed" : "in order")}, im {i}");
                            }
                            if (budget >= 1 << 24) Assert.That(calls, Is.EqualTo(1), "a budget for the whole transform: one call");
                            if (budget == 1) Assert.That(calls, Is.GreaterThanOrEqualTo(size / 2), "the smallest budget: a call for each butterfly of the two-point pass at least");
                        }
                }
            }
        }

        [TestCase(44100)]
        [TestCase(48000)]
        [TestCase(96000)]
        [TestCase(192000)]
        public void TheSteppedDesignIsTheWholeDesignBitForBit(int rate)
        {
            // Two linear splits on the same noise, the crossovers jumping every 6 partitions through the test sets: one designs
            // in steps (as the engine runs it), the other whole at the partition that asks (as before). Once both have taken a
            // design and the crossfade is over, their bands are the same bits; the stepped one lands within DesignEvery
            // partitions (3 at 44.1 and 48 kHz) and no partition takes more than its 31, 63 or 127 steps.
            var sets = CrossoverSets.Select(c => MultibandDesign.Effective(c.F1, c.F2, c.F3, rate)).ToArray();
            var stepped = new LinearSplit(rate, sets[0]);
            var whole = new LinearSplit(rate, sets[0]) { DesignAtOnce = true };
            int p = stepped.P;
            var x = Noise(6 * sets.Length * p + 16 * p, 0.3, 17);
            var y = Noise(x.Length, 0.3, 18);
            var inL = new float[p];
            var inR = new float[p];
            int partitions = x.Length / p, compared = 0, slowest = 0, asked = -1;
            long mostSteps = 0;
            for (int k = 0; k < partitions; k++)
            {
                var target = sets[(k / 6 + 1) % sets.Length];
                Array.Copy(x, k * p, inL, 0, p);
                Array.Copy(y, k * p, inR, 0, p);
                whole.Process(inL, inR, true, target);
                stepped.Process(inL, inR, true, target);
                if (!stepped.Designed.Equals(target) && asked < 0) asked = k;
                if (stepped.Designed.Equals(target) && asked >= 0)
                {
                    slowest = Math.Max(slowest, k - asked);
                    asked = -1;
                }
                long before = stepped.Steps;
                DesignSteps(stepped);
                mostSteps = Math.Max(mostSteps, stepped.Steps - before);
                // Both on the same design for the whole partition: the last switch (whole: at 6m, stepped: at 6m + 3) behind.
                if (k % 6 == 5)
                {
                    for (int b = 0; b < 4; b++)
                        for (int n = 0; n < p; n++)
                        {
                            Assert.That(Bits(stepped.BandL[b][n]), Is.EqualTo(Bits(whole.BandL[b][n])), $"partition {k}, band {b + 1}, left {n}");
                            Assert.That(Bits(stepped.BandR[b][n]), Is.EqualTo(Bits(whole.BandR[b][n])), $"partition {k}, band {b + 1}, right {n}");
                        }
                    compared++;
                }
            }
            Measure.Report($"Neo Multiband at {rate} Hz: a design in steps lands {slowest} partitions after it starts ({slowest * p * 1000.0 / rate:F1} ms), at most {mostSteps} steps a partition; its bands bit for bit the whole design's ({compared} partitions compared)");
            Assert.That(slowest, Is.InRange(1, LinearSplit.DesignEvery));
            if (rate <= 48000) Assert.That(slowest, Is.EqualTo(3));
            Assert.That(mostSteps, Is.LessThanOrEqualTo(p / LinearSplit.DesignStride - 1));
            Assert.That(compared, Is.GreaterThan(sets.Length));
        }

        // ------------------------------------------------------------------ the dynamics

        [TestCase(0, 4.0, 0.0, -20.0, 24.0)]
        [TestCase(0, 4.0, 12.0, -20.0, 24.0)]
        [TestCase(0, 0.5, 0.0, -30.0, 12.0)]
        [TestCase(0, 0.7, 6.0, -40.0, 24.0)]
        [TestCase(0, double.PositiveInfinity, 0.0, -30.0, 9.0)]
        [TestCase(1, 4.0, 12.0, -20.0, 24.0)]
        [TestCase(1, 0.5, 6.0, -30.0, 12.0)]
        public void TheStaticCurveIsTheGainComputers(int phaseMode, double ratio, double knee, double threshold, double range)
        {
            var phase = (PhaseMode)phaseMode;
            // A steady level (DC, which is all in band 1) from −60 to 0 dBFS: the change the band makes is the shared gain
            // computer's (NeoBuzz.Dsp GainComputer, Neo Comp's), clamped to ±Range. Above 1:1 it compresses, below it expands.
            // The gain computer for the values the parameters hold (a ratio lands within 0.2 % of what was asked).
            var pv = new ParamValues(Params.Table);
            OneBand(0, threshold, ratio, knee, range: range)(pv);
            var gc = GainComputer.Make(pv.Value(B(0, BP.Threshold)), MultibandDesign.Ratio(pv.Value(B(0, BP.Ratio))), pv.Value(B(0, BP.Knee)));
            double worst = 0;
            for (double inDb = -60; inDb <= 0; inDb += 3)
            {
                var (e, _) = Make(OneBand(0, threshold, ratio, knee, range: range), phase: phase);
                var x = Steady(inDb, Rate);
                var (y, _) = Run(e, x, x);
                double change = inDb - Db(Math.Abs(y[^1]));
                double want = Math.Clamp(gc.Ff(inDb), -range, range);
                worst = Math.Max(worst, Math.Abs(change - want));
            }
            string r = double.IsPositiveInfinity(ratio) ? "∞" : ratio.ToString("0.0#");
            Measure.Report($"Neo Multiband {phase} static curve, {r}:1, knee {knee} dB, threshold {threshold} dB, range {range} dB: within {worst:F4} dB of the gain computer");
            Assert.That(worst, Is.LessThan(0.01));
        }

        [TestCase(0, 0.001)]
        [TestCase(0, 0.01)]
        [TestCase(0, 0.1)]
        [TestCase(1, 0.001)]
        [TestCase(1, 0.01)]
        public void AttackReaches63PercentInItsTime(int phaseMode, double attack)
        {
            var phase = (PhaseMode)phaseMode;
            // Band 1 spanning the whole audible range (crossover 1 at 20 kHz), a step from −40 to −10 dBFS at threshold −20,
            // 4:1: 7.5 dB of reduction, 63 % of it reached in the attack time (±10 %).
            // The change is read against the same input through the machine at unity (so the crossovers' own response cancels).
            var (e, _) = Make(Both(OneBand(0, -20, 4, attack: attack, release: 1), Crossovers(20000, 20000, 20000)), phase: phase);
            var (u, _) = Make(Both(Unity, Crossovers(20000, 20000, 20000)), phase: phase);
            int L = e.Latency, step = 4800, n = L + step + (int)(attack * 10 * Rate) + 4800;
            var x = Constant(-40, n);
            for (int i = step; i < n; i++) x[i] = (float)DspMath.DbToGain(-10);
            var (y, _) = Run(e, x, x);
            var (yu, _) = Run(u, x, x);
            int hit = -1;
            for (int i = step + L; i < n; i++)
                if (Db(yu[i] / y[i]) >= 0.632 * 7.5) { hit = i - step - L; break; }
            double t = hit / (double)Rate;
            Measure.Report($"Neo Multiband {phase} attack {attack * 1000} ms: 63 % after {t * 1000:F3} ms");
            Assert.That(t / attack, Is.InRange(0.9, 1.1));
        }

        [TestCase(0, 0.05)]
        [TestCase(0, 0.3)]
        [TestCase(1, 2.0)]
        public void ReleaseRecovers63PercentInItsTime(int phaseMode, double release)
        {
            var phase = (PhaseMode)phaseMode;
            var (e, _) = Make(Both(OneBand(0, -20, 4, attack: 0.0005, release: release), Crossovers(20000, 20000, 20000)), phase: phase);
            var (u, _) = Make(Both(Unity, Crossovers(20000, 20000, 20000)), phase: phase);
            int L = e.Latency, step = 9600, n = L + step + (int)(release * 5 * Rate);
            var x = Constant(-10, n);
            for (int i = step; i < n; i++) x[i] = (float)DspMath.DbToGain(-40);
            var (y, _) = Run(e, x, x);
            var (yu, _) = Run(u, x, x);
            int hit = -1;
            for (int i = step + L + 48; i < n; i++)
                if (Db(yu[i] / y[i]) <= 0.368 * 7.5) { hit = i - step - L; break; }
            double t = hit / (double)Rate;
            Measure.Report($"Neo Multiband {phase} release {release * 1000} ms: back to 37 % after {t * 1000:F1} ms");
            Assert.That(t / release, Is.InRange(0.9, 1.1));
        }

        [Test]
        public void UpwardExpansionAttacksAsItGrows()
        {
            // 0.5:1 above −30 dB: a step from −40 to −20 dBFS gets +10 dB, 63 % of it within the attack time.
            const double attack = 0.01;
            var (e, _) = Make(Both(OneBand(0, -30, 0.5, attack: attack, release: 1, range: 24), Crossovers(20000, 20000, 20000)), phase: PhaseMode.Minimum);
            var (u, _) = Make(Both(Unity, Crossovers(20000, 20000, 20000)), phase: PhaseMode.Minimum);
            int step = 4800, n = step + 4 * 4800;
            var x = Constant(-40, n);
            for (int i = step; i < n; i++) x[i] = (float)DspMath.DbToGain(-20);
            var (y, _) = Run(e, x, x);
            var (yu, _) = Run(u, x, x);
            int hit = -1;
            for (int i = step; i < n; i++)
                if (Db(y[i] / yu[i]) >= 0.632 * 10) { hit = i - step; break; }
            double full = Db(y[^1] / x[^1]);
            Measure.Report($"Neo Multiband upward expansion 0.5:1 at −30 dB: −20 dBFS raised {full:F3} dB, 63 % after {hit * 1000.0 / Rate:F2} ms (attack 10 ms)");
            Assert.That(full, Is.EqualTo(10).Within(0.01));
            Assert.That(hit / (attack * Rate), Is.InRange(0.9, 1.1));
        }

        [Test]
        public void TheStereoLinkAndMidSideActAsSet()
        {
            // DC in band 1 at −10 dB on the left and −40 dB on the right, threshold −20, 4:1: linked, both channels get the
            // left's 7.5 dB; unlinked, the left 7.5 and the right none. Mid/side, unlinked: a mid at −10 dB and a side at −40 dB:
            // the mid 7.5 dB down, the side untouched.
            double Change(float[] y, float[] x) => Db(Math.Abs(x[^1]) / Math.Abs(y[^1]));
            var hi = Steady(-10, Rate);
            var lo = Steady(-40, Rate);
            var (linked, _) = Make(OneBand(0, -20, 4), phase: PhaseMode.Minimum);
            var (ll, lr) = Run(linked, hi, lo);
            var (dual, dv) = Make(Both(OneBand(0, -20, 4), v => v.Set(P.Link, 0)), phase: PhaseMode.Minimum);
            var (dl, dr) = Run(dual, hi, lo);
            Assert.That(Change(ll, hi), Is.EqualTo(7.5).Within(0.01), "linked: left");
            Assert.That(Change(lr, lo), Is.EqualTo(7.5).Within(0.01), "linked: right gets the left's gain");
            Assert.That(Change(dl, hi), Is.EqualTo(7.5).Within(0.01), "unlinked: left");
            Assert.That(Change(dr, lo), Is.EqualTo(0).Within(0.01), "unlinked: right untouched");
            // Mid/side: L = m + s, R = m − s.
            float mid = (float)DspMath.DbToGain(-10), side = (float)DspMath.DbToGain(-40);
            var fadeIn = Steady(0, Rate);
            var l = fadeIn.Select(g => g * (mid + side)).ToArray();
            var r = fadeIn.Select(g => g * (mid - side)).ToArray();
            var (msu, _) = Make(Both(OneBand(0, -20, 4), v => { v.Set(P.Stereo, 1); v.Set(P.Link, 0); }), phase: PhaseMode.Minimum);
            var (ml, mr) = Run(msu, l, r);
            double outMid = 0.5 * (ml[^1] + mr[^1]), outSide = 0.5 * (ml[^1] - mr[^1]);
            Measure.Report($"Neo Multiband stereo: linked {Change(ll, hi):F3}/{Change(lr, lo):F3} dB, unlinked {Change(dl, hi):F3}/{Change(dr, lo):F3} dB, mid/side unlinked mid {Db(mid / outMid):F3} dB, side {Db(side / outSide):F3} dB");
            Assert.That(Db(mid / outMid), Is.EqualTo(7.5).Within(0.01), "the mid compressed");
            Assert.That(Db(side / outSide), Is.EqualTo(0).Within(0.01), "the side untouched");
        }

        [Test]
        public void SoloHearsTheBandAndBypassPassesItUntouched()
        {
            // Every band compressing hard and bypassed: the output is the dry signal, bit for bit. Band 2 soloed: the output is
            // band 2 alone, whatever Mix says.
            Action<ParamValues> hard = v =>
            {
                for (int b = 0; b < 4; b++)
                {
                    Set(v, B(b, BP.Threshold), -60);
                    Set(v, B(b, BP.Ratio), 8);
                    Set(v, B(b, BP.Makeup), 6);
                    v.Set(B(b, BP.Bypass), 1);
                }
            };
            var (e, _) = Make(hard);
            var x = Noise(Rate, 0.3, 8);
            var (l, _) = Run(e, x, x, 117);
            int L = e.Latency;
            for (int i = L; i < x.Length; i++) Assert.That(Bits(l[i]), Is.EqualTo(Bits(x[i - L])), $"bypassed {i}");

            var h = BandResponse(v => v.Set(P.Mix, 0), 1, Rate, PhaseMode.Linear, 2 * L + 16);
            var xo = XoverOf(_ => { }, Rate);
            double worst = 0;
            foreach (double hz in LogFrequencies(80))
            {
                double want = MultibandDesign.Band(1, 4, hz, xo, Rate);
                if (want > 0.01) worst = Math.Max(worst, Math.Abs(Db(Dtft(h, 0, h.Length, L, hz, Rate).Magnitude / want)));
            }
            Assert.That(worst, Is.LessThan(0.01), "band 2 soloed at Mix 0 %");
        }

        [Test]
        public void ReadingsReportEachBandsGainChangeAndLevel()
        {
            // DC at −10 dB (all in band 1), threshold −20, 4:1: band 1 reads 7.5 dB now and at most over the last 3 s, its level
            // −10 dBFS; the other bands nothing.
            var (e, _) = Make(OneBand(0, -20, 4));
            var x = Steady(-10, 2 * Rate);
            Run(e, x, x);
            var r = e.Readings();
            Measure.Report($"Neo Multiband readings: {string.Join(", ", r.Select(kv => $"{kv.Key} {kv.Value:0.##}"))}");
            Assert.That(r["b1_gr_db"], Is.EqualTo(7.5).Within(0.05));
            Assert.That(r["b1_gr_peak_db"], Is.EqualTo(7.5).Within(0.05));
            Assert.That(r["b1_level_db"], Is.EqualTo(-10).Within(0.05));
            Assert.That(r["b2_gr_db"], Is.EqualTo(0).Within(0.01));
            Assert.That(r["b4_level_db"], Is.LessThan(-100));
            Assert.That(r["bands"], Is.EqualTo(4));
            Assert.That(r["latency_samples"], Is.EqualTo(4607));
        }

        [Test]
        public void ReadingsHaveTheManifestsKeys()
        {
            // Four bands: every key the manifest declares (Params.def's readings); three bands: band 4's left out.
            string manifest = Path.Combine(MachinesDir(), "NeoMultiband", "Neo Multiband.machine.json");
            var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
            var keys = doc["readings"]!.AsArray().Select(r => r!["key"]!.GetValue<string>()).ToList();
            var (e, v) = Make(null);
            Run(e, Noise(Rate / 2, 0.2, 3), Noise(Rate / 2, 0.2, 4));
            Assert.That(e.Readings().Keys, Is.EquivalentTo(keys));
            v.Set(P.Bands, 0);
            Run(e, Noise(Rate / 2, 0.2, 3), Noise(Rate / 2, 0.2, 4));
            Assert.That(e.Readings().Keys, Is.EquivalentTo(keys.Where(k => !k.StartsWith("b4_"))));
            Assert.That(e.Readings()["bands"], Is.EqualTo(3));
        }

        // ------------------------------------------------------------------ the editor's handles (toolkit-free)

        sealed class FakeCrossovers : NeoBuzz.Editor.ICrossovers
        {
            public double[] Hz = { 120, 1000, 6000 };
            public int Bands { get; set; } = 4;
            public readonly List<int> Solos = new();
            public readonly List<(int Band, int Tenths)> Steps = new();
            double NeoBuzz.Editor.ICrossovers.Hz(int i) => Hz[i];
            public void Move(int i, double hz) => Hz[i] = hz;
            public void ToggleSolo(int band) => Solos.Add(band);
            public void StepThreshold(int band, int tenths) => Steps.Add((band, tenths));
        }

        [Test]
        public void TheCrossoverHandlesDragSelectSoloAndStep()
        {
            // The graph 600 × 262: dragging crossover 2's line moves it (Shift: a tenth as far), never within a sixth of an
            // octave of its neighbours; a press between crossovers selects that band, a double-click solos it, the wheel steps
            // its threshold. Three bands: two handles.
            var graph = new NeoBuzz.Editor.ResponseGraph(-30, 12, 6);
            graph.Layout(new NeoBuzz.Editor.NeoRect(0, 0, 600, 262));
            var xo = new FakeCrossovers();
            var input = new NeoBuzz.Editor.CrossoverHandleInput(graph, xo);
            double x2 = graph.X(1000), y = graph.Plot.Top + 100;
            Assert.That(input.CrossoverAt(x2 + 3, y), Is.EqualTo(1));
            Assert.That(input.Down(x2 + 3, y, 1, false), Is.True);
            Assert.That(input.Capturing, Is.True);
            input.Move(graph.X(2000) + 3, y, false);
            Assert.That(xo.Hz[1], Is.EqualTo(2000).Within(2000 * 0.01), "dragged to 2 kHz");
            input.Move(graph.X(50000), y, false);
            Assert.That(xo.Hz[1], Is.EqualTo(6000 / NeoBuzz.Editor.CrossoverHandleInput.MinRatio).Within(1e-6), "a sixth of an octave under crossover 3");
            input.Move(graph.X(10), y, false);
            Assert.That(xo.Hz[1], Is.EqualTo(120 * NeoBuzz.Editor.CrossoverHandleInput.MinRatio).Within(1e-6), "a sixth of an octave over crossover 1");
            input.Up();
            Assert.That(input.Capturing, Is.False);
            // Fine.
            xo.Hz[1] = 1000;
            input.Down(graph.X(1000), y, 1, true);
            input.Move(graph.X(1000) + 100, y, true);
            input.Up();
            Assert.That(xo.Hz[1], Is.EqualTo(graph.Hz(graph.X(1000) + 10)).Within(0.01), "Shift: a tenth as far");
            // Bands: select, solo, wheel.
            input.Down(graph.X(3000), y, 1, false);
            input.Up();
            Assert.That(input.Selected, Is.EqualTo(2));
            input.Down(graph.X(60), y, 2, false);
            Assert.That(input.Selected, Is.EqualTo(0));
            Assert.That(xo.Solos, Is.EqualTo(new[] { 0 }));
            input.Wheel(graph.X(10000), y, 120, false);
            input.Wheel(graph.X(10000), y, -240, true);
            Assert.That(xo.Steps, Is.EqualTo(new[] { (3, 10), (3, -2) }));
            // Three bands: crossover 3 is no handle.
            xo.Bands = 3;
            Assert.That(input.Count, Is.EqualTo(2));
            Assert.That(input.CrossoverAt(graph.X(6000), y), Is.EqualTo(-1));
            Assert.That(input.BandAt(graph.X(10000)), Is.EqualTo(2));
            var canvas = new NeoBuzz.Editor.RecordingCanvas();
            input.Draw(canvas, NeoBuzz.Editor.NeoColor.FromRgb(0xF2, 0xB1, 0x3C));
        }

        // ------------------------------------------------------------------ moves and switches

        [TestCase(0)]
        [TestCase(1)]
        public void CrossoverMovesAreSmooth(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            // A 220 Hz sine, band 1 compressing hard (−40 dB, 4:1, makeup +6 dB): crossover 1 jumps between 100 and 400 Hz,
            // across the sine, every 1024 samples, so the sine moves between band 1 and band 2. The largest step between
            // samples within 1.5× the steady signal's at either end: no clicks (linear: a redesign and crossfade; minimum: a glide).
            Action<ParamValues> setup = Both(OneBand(0, -40, 4, attack: 0.01, release: 0.2), v => Set(v, B(0, BP.Makeup), 6));
            var x = Measure.Sine(220, 3 * Rate, 0.25);
            double Steady(double hz)
            {
                var (s, _) = Make(Both(setup, v => Set(v, P.Xo1, hz)), phase: phase);
                return MaxStep(Run(s, x, x).L, Rate);
            }
            double reference = Math.Max(Steady(100), Steady(400));
            var (e, v) = Make(setup, phase: phase);
            int designs = e.Linear.Designs;
            var (l, _) = Run(e, x, x, before: pos => { if (pos >= Rate / 2 && pos % 1024 == 0) Set(v, P.Xo1, (pos / 1024) % 2 == 0 ? 100 : 400); });
            double ratio = MaxStep(l, Rate) / reference;
            Measure.Report($"Neo Multiband {phase}: crossover 1 jumping across a 220 Hz sine every 1024 samples, largest step {ratio:F2}× the steady signal's" +
                           (phase == PhaseMode.Linear ? $" ({e.Linear.Designs - designs} redesigns)" : ""));
            Assert.That(ratio, Is.LessThan(1.5));
        }

        [Test]
        public void ACrossoverSweepStaysBoundedPerBlock()
        {
            // Crossover 2 dragged (a parameter step every 64-sample block, 1 → 1.75 kHz and back) on noise, every band
            // compressing, linear phase, in blocks of 64: a design starting every 4th partition. Counted: no block takes more
            // than 4 design steps (one every 16 samples), and the sweep designs as often as with each design whole.
            // Timed against a calibration in the same run, so that a shared runner's noise can't decide it: not moving, the
            // sweep and each design whole run interleaved, 5 times each, every block timed. What a block does is fixed by
            // what it holds (where in the partition it falls, how many design steps it takes and from which step of the
            // design, whether a design takes over in it), so blocks are grouped by that and each group's time is its fastest
            // block over every run: tens to hundreds of the same work each, so preemption and slow spells (noise only adds
            // time) drop out where the slowest single block of a run couldn't shed them (run 41991632485: 498 µs against a
            // 393 µs limit, the next run 237). The slowest group of the sweep within 2× the slowest not moving (where a
            // partition's convolution runs), and under half the slowest with each design whole in one block, as before.
            const int Block = 64, Runs = 5, Skip = 100;
            var x = Noise(3 * Rate, 0.2, 31);
            var y = Noise(3 * Rate, 0.2, 32);
            var bl = new float[Block];
            var br = new float[Block];
            var fastest = new[] { new Dictionary<(int, long, long, bool), (double Us, int Count)>(), new(), new() };
            int designs = 0, wholeDesigns = 0;
            long most = 0, perDesign = 0;
            for (int run = 0; run < Runs; run++)
                for (int way = 0; way < 3; way++)
                {
                    bool sweep = way > 0;
                    var (e, v) = Make(null);
                    e.Linear.DesignAtOnce = way == 2;
                    perDesign = e.Linear.Steps;                 // the initial design's: every design takes as many
                    int raw = v[P.Xo2], first = e.Linear.Designs, perPartition = e.Linear.P / Block;
                    for (int b = 0; b < x.Length / Block; b++)
                    {
                        if (sweep)
                        {
                            int k = b % 1600;
                            v.Set(P.Xo2, raw + (k < 800 ? k : 1600 - k));
                        }
                        Array.Copy(x, b * Block, bl, 0, Block);
                        Array.Copy(y, b * Block, br, 0, Block);
                        long steps = e.Linear.Steps;
                        var designed = e.Linear.Designed;
                        long t0 = Stopwatch.GetTimestamp();
                        e.Process(bl, br, Block);
                        double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
                        long taken = e.Linear.Steps - steps;
                        if (way == 1) most = Math.Max(most, taken);
                        if (b < Skip) continue;
                        var group = (b % perPartition, taken, taken > 0 ? (steps - perDesign) % perDesign : -1, !e.Linear.Designed.Equals(designed));
                        fastest[way][group] = fastest[way].TryGetValue(group, out var f) ? (Math.Min(f.Us, us), f.Count + 1) : (us, 1);
                    }
                    if (way == 1) designs = e.Linear.Designs - first;
                    if (way == 2) wholeDesigns = e.Linear.Designs - first;
                }
            var slowest = fastest.Select(g => g.MaxBy(kv => kv.Value.Us)).ToArray();
            double still = slowest[0].Value.Us, swept = slowest[1].Value.Us, before = slowest[2].Value.Us;
            string Group(int way) => $"{fastest[way].Count} groups, the slowest the fastest of {slowest[way].Value.Count}";
            Measure.Report($"Neo Multiband: crossover 2 dragged in blocks of 64 at 48 kHz, the slowest block {swept:F0} µs ({designs} designs in steps of {perDesign}, " +
                           $"at most {most} steps a block; {Group(1)}) against {still:F0} µs not moving ({Group(0)}) and {before:F0} µs with each design whole in one block " +
                           $"({wholeDesigns} designs; {Group(2)})");
            Assert.That(designs, Is.GreaterThan(60), "the sweep designs again every 4th partition");
            Assert.That(designs, Is.InRange(wholeDesigns - 1, wholeDesigns), "as often as before");
            Assert.That(most, Is.LessThanOrEqualTo(Block / LinearSplit.DesignStride));
            Assert.That(swept, Is.LessThan(2 * still));
            Assert.That(swept, Is.LessThan(0.5 * before));
        }

        [Test]
        public void ASwitchInStepsIsAsSmoothAsAWholeDesigns()
        {
            // As CrossoverMovesAreSmooth (a 220 Hz sine, band 1 compressing hard, crossover 1 jumping across it), every 4096
            // samples so that every design lands: the largest step between samples with the designs in steps and with each
            // design whole at the partition that asks (as before), against the steady signal's. The same crossfade, later:
            // no worse than before (within 5 %), and within 1.5× the steady signal's.
            Action<ParamValues> setup = Both(OneBand(0, -40, 4, attack: 0.01, release: 0.2), v => Set(v, B(0, BP.Makeup), 6));
            var x = Measure.Sine(220, 4 * Rate, 0.25);
            double Steady(double hz)
            {
                var (s, _) = Make(Both(setup, v => Set(v, P.Xo1, hz)));
                return MaxStep(Run(s, x, x).L, Rate);
            }
            double reference = Math.Max(Steady(100), Steady(400));
            double Jumps(bool whole, out int designs)
            {
                var (e, v) = Make(setup);
                e.Linear.DesignAtOnce = whole;
                int first = e.Linear.Designs;
                var (l, _) = Run(e, x, x, before: pos => { if (pos >= Rate / 2 && pos % 4096 == 0) Set(v, P.Xo1, (pos / 4096) % 2 == 0 ? 100 : 400); });
                designs = e.Linear.Designs - first;
                return MaxStep(l, Rate) / reference;
            }
            double stepped = Jumps(false, out int steppedDesigns), whole = Jumps(true, out int wholeDesigns);
            Measure.Report($"Neo Multiband: crossover 1 jumping across a 220 Hz sine every 4096 samples, largest step {stepped:F3}× the steady signal's with the designs in steps ({steppedDesigns}), {whole:F3}× with each whole at once ({wholeDesigns})");
            Assert.That(steppedDesigns, Is.EqualTo(wholeDesigns));
            Assert.That(stepped, Is.LessThan(1.05 * whole));
            Assert.That(stepped, Is.LessThan(1.5));
        }

        [Test]
        public void RendersAreDeterministicWhileTheCrossoversSweep()
        {
            // Crossover 2 swept, a parameter step every 256 samples for 3 s, on noise: two renders in blocks of 64 and renders
            // in blocks of 128 and 256 are the same bits. The design's steps fall on fixed samples of the partitions and nothing
            // in the engine reads a clock, so neither the host's blocks nor the machine's speed moves a switch.
            var x = Noise(3 * Rate, 0.2, 41);
            var y = Noise(3 * Rate, 0.2, 42);
            int designs = 0;
            (float[] L, float[] R) Render(int block)
            {
                var (e, v) = Make(null);
                int raw = v[P.Xo2], first = e.Linear.Designs;
                var r = Run(e, x, y, block, pos => { if (pos % 256 == 0) v.Set(P.Xo2, raw + (pos / 256) % 400); });
                designs = e.Linear.Designs - first;
                return r;
            }
            var a = Render(64);
            Assert.That(designs, Is.GreaterThan(50));
            foreach (int block in new[] { 64, 128, 256 })
            {
                var b = Render(block);
                for (int i = 0; i < x.Length; i++)
                {
                    Assert.That(Bits(b.L[i]), Is.EqualTo(Bits(a.L[i])), $"blocks of {block}, left {i}");
                    Assert.That(Bits(b.R[i]), Is.EqualTo(Bits(a.R[i])), $"blocks of {block}, right {i}");
                }
            }
        }

        [Test]
        public void PhaseAndBandCountSwitchesAreSmoothAndTheLatencyFollows()
        {
            // A 220 Hz and a 3 kHz sine, the bands at their defaults (compressing): Phase switched every 0.5 s (a fade out, the
            // switch, a fade in once the new path carries sound), then Bands switched every 0.25 s. No step larger than the
            // steady signal's; the latency 4607 in linear phase, 0 in minimum.
            var x = Sines(4 * Rate, 0.2, 220, 3000);
            double reference = 0;
            foreach (var ph in new[] { PhaseMode.Linear, PhaseMode.Minimum })
                foreach (int bands in new[] { 3, 4 })
                {
                    var (s, _) = Make(v => v.Set(P.Bands, bands == 4 ? 1 : 0), phase: ph);
                    reference = Math.Max(reference, MaxStep(Run(s, x, x).L, Rate));
                }
            var (e, v) = Make(null);
            var latencies = new HashSet<int>();
            var (l, _) = Run(e, x, x, before: pos =>
            {
                if (pos < 2 * Rate && pos % 24064 == 0 && pos > 0) v.Set(P.Phase, 1 - v[P.Phase]);   // about every 0.5 s
                if (pos >= 2 * Rate && pos % 12032 == 0) v.Set(P.Bands, 1 - v[P.Bands]);
                latencies.Add(e.Latency);
            });
            double ratio = MaxStep(l, Rate / 4) / reference;
            Measure.Report($"Neo Multiband: Phase and Bands switched while playing, largest step {ratio:F2}× the steady signal's; latencies seen {string.Join(", ", latencies.OrderBy(t => t))}");
            Assert.That(ratio, Is.LessThan(1.2));
            Assert.That(latencies, Is.EquivalentTo(new[] { 0, 4607 }));
        }

        [Test]
        public void ParameterJumpsAreSmooth()
        {
            // Each parameter jumping between its ends every 2048 samples (after 1 s), the others at their defaults: band
            // parameters on a sine in that band (60 Hz, 400 Hz, 2.5 kHz, 10 kHz at −6 dBFS), the rest on 220 Hz and 2.5 kHz.
            // The largest step between samples within 1.5× the largest the steady settings at either end make on their own.
            var problems = new List<string>();
            var lines = new List<string>();
            double[] centre = { 60, 400, 2500, 10000 };
            foreach (var d in Params.Table)
            {
                int band = d.Index >= P.B1Threshold && d.Index <= P.B4Bypass ? (d.Index - P.B1Threshold) / 9 : -1;
                var x = band >= 0 ? Measure.Sine(centre[band], 3 * Rate, 0.5) : Sines(3 * Rate, 0.25, 220, 2500);
                double Steady(int raw)
                {
                    var (s, _) = Make(v => v.Set(d.Index, raw));
                    return MaxStep(Run(s, x, x).L, Rate);
                }
                double reference = Math.Max(Steady(d.Min), Steady(d.Max));
                var (e, jv) = Make(null);
                var (l, _) = Run(e, x, x, before: pos => { if (pos >= Rate && pos % 2048 == 0) jv.Set(d.Index, (pos / 2048) % 2 == 0 ? d.Min : d.Max); });
                double ratio = MaxStep(l, Rate) / reference;
                lines.Add($"{d.Name} {ratio:F2}");
                if (ratio > 1.5) problems.Add($"{d.Name} jumps make steps {ratio:F2}× the steady signal's");
            }
            Measure.Report($"Neo Multiband parameter jumps, largest step against the steady signal's: {string.Join(", ", lines)}");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [Test]
        public void TheOutputDoesNotDependOnHowTheHostSplitsIt()
        {
            // Blocks of 256, 128 and 117: the same samples, with the crossovers moving (redesigns), the band count, a solo and the
            // phase switched meanwhile (each change at a sample that starts a block in all three runs).
            Action<ParamValues> setup = v => { v.Set(P.Stereo, 1); v.Set(P.Link, 500); };
            var x = Noise(5 * Rate, 0.2, 11);
            var y = Noise(5 * Rate, 0.2, 12);
            (float[] L, float[] R) Render(int block)
            {
                var (e, v) = Make(setup);
                return Run(e, x, y, block, pos =>
                {
                    // Multiples of 29 952, which starts a block of 256, 128 and 117 samples alike.
                    if (pos == 29952) { Set(v, P.Xo1, 300); Set(v, P.Xo2, 3000); }
                    if (pos == 59904) v.Set(P.Bands, 0);
                    if (pos == 89856) v.Set(B(1, BP.Solo), 1);
                    if (pos == 119808) { v.Set(B(1, BP.Solo), 0); v.Set(P.Phase, 1); }
                    if (pos == 149760) { v.Set(P.Bands, 1); Set(v, P.Xo3, 9000); }
                    if (pos == 179712) v.Set(P.Phase, 0);
                });
            }
            var a = Render(256);
            foreach (int block in new[] { 128, 117 })
            {
                var b = Render(block);
                for (int i = 0; i < x.Length; i++)
                {
                    Assert.That(Bits(b.L[i]), Is.EqualTo(Bits(a.L[i])), $"blocks of {block}, left {i}");
                    Assert.That(Bits(b.R[i]), Is.EqualTo(Bits(a.R[i])), $"blocks of {block}, right {i}");
                }
            }
        }

        // ------------------------------------------------------------------ hygiene

        [TestCase(0)]
        [TestCase(1)]
        public void SilenceAfterNoiseLeavesNoSubnormalsAndRunsAtFullSpeed(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            // 2 s of noise with every band compressing, then 20 s of silence: nothing subnormal in the delay line, the sections,
            // the gains or the output; silence costs no more than noise; idle once everything has left.
            var (e, _) = Make(null, phase: phase);
            var noise = Noise(2 * Rate, 0.3, 9);
            var silent = new float[20 * Rate];
            var sw = Stopwatch.StartNew();
            Run(e, noise, noise);
            double noiseSeconds = sw.Elapsed.TotalSeconds / 2;
            sw.Restart();
            var (l, r) = Run(e, silent, silent);
            double silenceSeconds = sw.Elapsed.TotalSeconds / 20;
            Assert.That(e.HoldsSubnormals(), Is.False, "no subnormal state");
            Assert.That(l.Concat(r).Any(float.IsSubnormal), Is.False, "no subnormal output");
            Assert.That(e.Idle, Is.True, "idle after the tail");
            Measure.Report($"Neo Multiband {phase}: silence after noise costs {silenceSeconds / noiseSeconds:F2}× noise; no subnormals; idle after the tail");
            Assert.That(silenceSeconds, Is.LessThan(3 * noiseSeconds + 0.002));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void NanAndInfinityInTheInputAreSilence(int phaseMode)
        {
            var phase = (PhaseMode)phaseMode;
            // Bursts of NaN and ±infinity in noise: taken as silence, so no state becomes NaN and the output is always finite.
            var (e, _) = Make(null, phase: phase);
            var x = Noise(2 * Rate, 0.1, 13);
            for (int i = Rate / 2; i < Rate / 2 + 100; i++) x[i] = float.NaN;
            for (int i = Rate; i < Rate + 100; i++) x[i] = (i & 1) == 0 ? float.PositiveInfinity : float.NegativeInfinity;
            var (l, r) = Run(e, x, x);
            Assert.That(e.HoldsNonFinite(), Is.False);
            Assert.That(l.Concat(r).All(float.IsFinite), Is.True);
        }

        [Test]
        public void TheAudioPathAllocatesNothing()
        {
            // 2000 blocks with the crossovers moving (redesigns), Bands, Phase, Stereo and solos switching: no allocation.
            var (e, v) = Make(null);
            var l = new float[MultibandEngine.MaxBlock];
            var r = new float[MultibandEngine.MaxBlock];
            var rng = new Random(1);
            void Block(int b)
            {
                for (int i = 0; i < l.Length; i++) l[i] = r[i] = (float)Math.Sin(i * 0.05 + b) * 0.3f;
                if (b % 3 == 0) v.Set(P.Xo1 + rng.Next(3), rng.Next(10001));
                if (b % 50 == 7) v.Set(P.Bands, rng.Next(2));
                if (b % 400 == 9) v.Set(P.Phase, rng.Next(2));
                if (b % 70 == 11) v.Set(P.Stereo, rng.Next(2));
                if (b % 30 == 13) v.Set(B(rng.Next(4), BP.Solo), rng.Next(2));
                e.Process(l, r, l.Length);
            }
            for (int b = 0; b < 300; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, Block);
            Measure.Report($"Neo Multiband: {bytes} bytes allocated over 2000 blocks with the crossovers, Bands, Phase, Stereo and solos moving");
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        // ------------------------------------------------------------------ presets, CPU

        [Test]
        public void EveryPresetLoadsAndStaysWithinLevelBounds()
        {
            // Each factory preset on pink noise at −18 dBFS RMS for 3 s: within ±6 dB of the input's level, peaks under +3 dBFS.
            var x = Noise(3 * Rate, DspMath.DbToGain(-18), 21);
            double inRms = Measure.Rms(x, Rate, 2 * Rate);
            int count = 0;
            foreach (var (_, machine, name, values) in PresetTests.AllPresets())
            {
                if (machine != "Neo Multiband") continue;
                var (e, _) = Make(pv => { foreach (var kv in values) if (kv.Key >= 0) pv.Set(kv.Key, kv.Value); });
                var (l, r) = Run(e, x, x);
                double rms = Math.Max(Measure.Rms(l, Rate, 2 * Rate), Measure.Rms(r, Rate, 2 * Rate));
                double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
                Measure.Report($"Neo Multiband / {name}: {Db(rms / inRms):+0.0;−0.0;0.0} dB, peak {Db(peak):+0.0;−0.0} dBFS");
                Assert.That(double.IsFinite(peak), Is.True, name);
                Assert.That(Db(rms / inRms), Is.InRange(-6.0, 6.0), name);
                Assert.That(Db(peak), Is.LessThan(3), name);
                count++;
            }
            Assert.That(count, Is.InRange(12, 18));
        }

        [Test]
        public void CpuIsSmall()
        {
            // Stereo at 48 kHz on noise, every band compressing (the defaults); linear and minimum phase, four bands, and the
            // worst case: mid/side unlinked (two detectors a band), and crossovers moving every block in linear phase (a redesign
            // every 43 ms).
            var x = Noise(10 * Rate, 0.2, 4);
            var cases = new (string Name, Action<ParamValues> Setup, PhaseMode Phase, bool Move, double Floor)[]
            {
                ("linear, 4 bands", null, PhaseMode.Linear, false, 20),
                ("minimum, 4 bands", null, PhaseMode.Minimum, false, 20),
                ("linear, 3 bands", v => v.Set(P.Bands, 0), PhaseMode.Linear, false, 20),
                ("linear, mid/side unlinked", v => { v.Set(P.Stereo, 1); v.Set(P.Link, 0); }, PhaseMode.Linear, false, 12),
                ("linear, crossovers moving", null, PhaseMode.Linear, true, 10),
            };
            foreach (var c in cases)
            {
                var (e, v) = Make(c.Setup, phase: c.Phase);
                Action<int> move = c.Move ? pos => v.Set(P.Xo2, 4000 + (pos / 256) % 200) : null;
                Run(e, x, x, before: move);   // warm
                var sw = Stopwatch.StartNew();
                Run(e, x, x, before: move);
                double speed = 10 / sw.Elapsed.TotalSeconds;
                Measure.Report($"Neo Multiband CPU ({c.Name}): {speed:F0}× real time");
                Assert.That(speed, Is.GreaterThan(c.Floor), c.Name);
            }
        }
    }
}
