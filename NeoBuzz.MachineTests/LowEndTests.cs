using NeoBuzz.Dsp;
using NeoBuzz.LowEnd;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Low End (docs/specs/mastering-tools.md §5.5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class LowEndTests
    {
        const int Rate = Measure.Rate;

        static (LowEndEngine Engine, ParamValues Values) Make(Action<ParamValues>? setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new LowEndEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (float[] L, float[] R) Run(LowEndEngine e, float[] l, float[] r, int block = LowEndEngine.MaxBlock, Action<int>? before = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[LowEndEngine.MaxBlock];
            var br = new float[LowEndEngine.MaxBlock];
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

        static float[] Mix(params float[][] x)
        {
            var y = new float[x[0].Length];
            foreach (var s in x) for (int i = 0; i < y.Length; i++) y[i] += s[i];
            return y;
        }

        /// <summary>A kick every 0.5 s: a sine falling from 80 to 50 Hz, e^(−t/0.1 s), the first sample at full slope.</summary>
        static float[] Kicks(int seconds, out List<int> at, double amplitude = 0.7)
        {
            var x = new float[seconds * Rate];
            at = new();
            for (double t0 = 0.1; t0 < seconds - 0.5; t0 += 0.5)
            {
                int s = (int)(t0 * Rate);
                at.Add(s);
                double ph = 0;
                for (int i = 0; i < 0.4 * Rate; i++)
                {
                    double t = i / (double)Rate;
                    ph += 2 * Math.PI * (50 + 30 * Math.Exp(-t / 0.02)) / Rate;
                    x[s + i] += (float)(amplitude * Math.Sin(ph) * Math.Exp(-t / 0.1));
                }
            }
            return x;
        }

        static double PeakDb(float[] x, int from, int n) => Measure.Db(Measure.Peak(x, from, n));

        // ---- the split ---------------------------------------------------------------------------

        /// <summary>The DTFT of an impulse response at <paramref name="hz"/>, the response's origin at index <paramref name="origin"/> (its magnitude).</summary>
        static double Dtft(float[] h, int origin, double hz, int rate)
        {
            double w = 2 * Math.PI * hz / rate, re = 0, im = 0;
            for (int j = 0; j < h.Length; j++)
            {
                re += h[j] * Math.Cos(w * (j - origin));
                im -= h[j] * Math.Sin(w * (j - origin));
            }
            return Math.Sqrt(re * re + im * im);
        }

        static IEnumerable<double> LogFrequencies(int count, double from = 15, double to = 20000) =>
            Enumerable.Range(0, count).Select(k => from * Math.Pow(to / from, k / (count - 1.0)));

        [TestCase(44100)]
        [TestCase(48000)]
        [TestCase(96000)]
        public void TheMinimumPhaseBandsSumFlat(int rate)
        {
            foreach (double xo in new[] { 20.0, 60, 120, 300 })
            {
                var bands = new LrBands(rate, xo, xo, xo);
                int n = 1 << 15;
                var imp = new float[n];
                imp[0] = 1;
                var bl = new[] { new float[n], new float[n] };
                var br = new[] { new float[n], new float[n] };
                bands.Process(imp, (float[])imp.Clone(), n, 2, bl, br);
                var sum = new float[n];
                for (int i = 0; i < n; i++) sum[i] = bl[0][i] + bl[1][i];
                double flat = 0, worstDesign = 0;
                foreach (double hz in LogFrequencies(160, 10, 0.45 * rate))
                {
                    flat = Math.Max(flat, Math.Abs(Measure.Db(Dtft(sum, 0, hz, rate))));
                    double want = LowEndDesign.Band(hz, xo, rate);
                    if (want > 1e-3) worstDesign = Math.Max(worstDesign, Math.Abs(Measure.Db(Dtft(bl[0], 0, hz, rate) / want)));
                }
                Measure.Report($"Neo Low End minimum phase at {rate} Hz, crossover {xo:F0} Hz: the bands sum flat within {flat:F4} dB; the low band against the design within {worstDesign:F4} dB");
                Assert.That(flat, Is.LessThan(0.01));
                Assert.That(worstDesign, Is.LessThan(0.02));
            }
        }

        [TestCase(20.0)]
        [TestCase(60.0)]
        [TestCase(120.0)]
        [TestCase(300.0)]
        public void TheLinearPhaseFirMatchesTheDesignAndTheBandsSum(double xo)
        {
            var lin = new LowLinear(Rate, xo);
            int p = lin.P, parts = lin.K + 2;
            var low = new float[parts * p];
            var dry = new float[parts * p];
            var imp = new float[p];
            var zero = new float[p];
            imp[0] = 1;
            for (int j = 0; j < parts; j++)
            {
                lin.Process(j == 0 ? imp : zero, j == 0 ? imp : zero, xo);
                Array.Copy(lin.LowL, 0, low, j * p, p);
                Array.Copy(lin.DryL, 0, dry, j * p, p);
            }
            Assert.That(dry[lin.D], Is.EqualTo(1f), "the dry signal is the input D samples back");
            double worst = 0, worstFlat = 0;
            var high = new float[low.Length];
            for (int i = 0; i < low.Length; i++) high[i] = dry[i] - low[i];
            foreach (double hz in LogFrequencies(160))
            {
                double want = LowEndDesign.Band(hz, xo, Rate);
                if (want > 1e-3) worst = Math.Max(worst, Math.Abs(Measure.Db(Dtft(low, lin.D, hz, Rate) / want)));
                var sumMag = Dtft(Add(low, high), lin.D, hz, Rate);
                worstFlat = Math.Max(worstFlat, Math.Abs(Measure.Db(sumMag)));
            }
            Measure.Report($"Neo Low End linear phase FIR, crossover {xo:F0} Hz: response within {worst:F4} dB of the design where the band is above −60 dB; low + rest flat within {worstFlat:E1} dB");
            Assert.That(worst, Is.LessThan(xo < 40 ? 0.6 : 0.01));
            Assert.That(worstFlat, Is.LessThan(1e-4));
        }

        static float[] Add(float[] a, float[] b)
        {
            var y = new float[a.Length];
            for (int i = 0; i < y.Length; i++) y[i] = a[i] + b[i];
            return y;
        }

        // ---- at rest -------------------------------------------------------------------------------

        [TestCase(0)]
        [TestCase(1)]
        public void AtRestTheOutputIsTheInputBitForBitDelayedByTheLatency(int phase)
        {
            var kicks = Kicks(3, out _);
            var bed = Measure.Sine(1000, kicks.Length, 0.2);
            var x = Mix(kicks, bed);
            var y = new float[x.Length];
            for (int i = 0; i < y.Length; i++) y[i] = x[(i * 7 + 3) % x.Length] * 0.5f;
            var (e, _) = Make(v => v.Set(P.Phase, phase));
            var (ol, or) = Run(e, x, y);
            int la = e.Latency;
            Assert.That(la, Is.EqualTo(phase == 1 ? LowEndDesign.LinearLatency(Rate) : (int)Math.Round(0.005 * Rate)));
            for (int i = la; i < x.Length; i++)
                if (BitConverter.SingleToInt32Bits(ol[i]) != BitConverter.SingleToInt32Bits(x[i - la]) || BitConverter.SingleToInt32Bits(or[i]) != BitConverter.SingleToInt32Bits(y[i - la]))
                    Assert.Fail($"sample {i} differs (phase {phase})");
        }

        // ---- isolation -------------------------------------------------------------------------------

        [TestCase(0)]
        [TestCase(1)]
        public void OnlyTheLowBandChanges(int phase)
        {
            // Every control working hard on a steady tone at 20× the crossover and above: what leaks is the low-pass's skirt.
            const double xo = 120;
            var report = new List<string>();
            double worstAt1k = 0;
            foreach (double hz in new[] { 240.0, 480, 1000, 2000, 4000, 8000, 16000 })
            {
                var x = Measure.Sine(hz, Rate * 3, 0.3);
                var (e, _) = Make(v =>
                {
                    v.Set(P.Phase, phase);
                    Set(v, P.Xo, xo);
                    Set(v, P.Sustain, 12);
                    Set(v, P.Punch, 12);
                    v.Set(P.Contrast, 1000);
                    v.Set(P.Peak, 1);
                    Set(v, P.PeakLevel, -24);
                    v.Set(P.Mono, 1);
                });
                var (o, _) = Run(e, x, (float[])x.Clone());
                int la = e.Latency;
                // Linear phase: the waveform (the rest is the delayed input untouched). Minimum phase: the level (the two bands sum
                // to the input's allpass, so the phase of a tone above the crossover moves, its level doesn't).
                double worst = 0;
                if (phase == 1)
                    for (int i = 2 * Rate; i < 3 * Rate; i++) worst = Math.Max(worst, Math.Abs(o[i] - x[i - la]));
                else
                    worst = Math.Abs(Measure.Rms(o, 2 * Rate, Rate) / Measure.Rms(x, 2 * Rate, Rate) - 1);
                report.Add($"{hz:F0} Hz {worst:E1}");
                if (hz == 1000) worstAt1k = worst;
            }
            Measure.Report($"Neo Low End {(phase == 1 ? "linear" : "minimum")} phase, crossover {xo:F0} Hz, Sustain and Punch +12 dB, Contrast +100 %, Peak control −24 dB, mono: the change of a 0.3 tone above it ({(phase == 1 ? "waveform" : "level, relative")}): {string.Join(", ", report)}");
            Assert.That(worstAt1k, Is.LessThan(2e-3), "8× the crossover: the skirt of the Linkwitz–Riley 24 low-pass");
        }

        // ---- Punch and Sustain -------------------------------------------------------------------

        [TestCase(0, 6.0)]
        [TestCase(0, -6.0)]
        [TestCase(0, 12.0)]
        [TestCase(1, 6.0)]
        [TestCase(1, -6.0)]
        public void PunchMovesAKicksPeakByTheGain(int phase, double db)
        {
            var x = Kicks(6, out var at);
            var (e, _) = Make(v => { v.Set(P.Phase, phase); Set(v, P.Xo, 300); Set(v, P.Punch, db); });
            var (o, _) = Run(e, x, (float[])x.Clone());
            int la = e.Latency;
            // Each kick's first peak: the first 8 ms (the kick's first lobe peaks at 3 ms; the band's group delay moves it a little later).
            var d = at.Skip(2).Select(s => PeakDb(o, s + la, 384) - PeakDb(x, s, 384)).Average();
            Measure.Report($"Neo Low End Punch {db:+0.0;-0.0} dB, {(phase == 1 ? "linear" : "minimum")} phase: a kick's first peak {d:+0.00;-0.00} dB (crossover 300 Hz; the share of a sound above the crossover is untouched)");
            Assert.That(d, Is.EqualTo(db).Within(0.5));
        }

        [Test]
        public void SustainActsOnTheBodyAndTheCrestChangeIsReported()
        {
            var x = Kicks(6, out var at);
            var (e, _) = Make(v => { Set(v, P.Xo, 300); Set(v, P.Sustain, -9); Set(v, P.Punch, 3); });
            var (o, _) = Run(e, x, (float[])x.Clone());
            var tail = at.Skip(2).Select(s => PeakDb(o, s + 4800, 4800) - PeakDb(x, s + 4800, 4800)).Average();
            var rd = e.Readings();
            Measure.Report($"Neo Low End Punch +3 / Sustain −9 dB: a kick's tail (100–200 ms) {tail:+0.00;-0.00} dB; the low band's crest {rd["low_crest_in_db"]:F2} → {rd["low_crest_out_db"]:F2} dB");
            Assert.That(tail, Is.EqualTo(-9).Within(1.0));
            Assert.That(rd["low_crest_out_db"] - rd["low_crest_in_db"], Is.GreaterThan(3), "less sustain and more punch: a higher crest");
        }

        // ---- Contrast -----------------------------------------------------------------------------

        /// <summary>A 60 Hz sine, 0.5 for 200 ms every second over 0.1 (a loud part over a quiet one).</summary>
        static float[] LoudQuiet(int seconds)
        {
            var x = new float[seconds * Rate];
            for (int i = 0; i < x.Length; i++)
            {
                double t = i / (double)Rate;
                x[i] = (float)((t % 1.0 < 0.2 ? 0.5 : 0.1) * Math.Sin(2 * Math.PI * 60 * t));
            }
            return x;
        }

        [TestCase(1.0)]
        [TestCase(-1.0)]
        public void ContrastKeepsTheStrongestPartsAndPullsTheRestOrTheReverse(double amount)
        {
            var x = LoudQuiet(8);
            var (e, _) = Make(v => { Set(v, P.Xo, 150); v.Set(P.Contrast, (int)Math.Round(500 + 500 * amount)); });
            var (o, _) = Run(e, x, (float[])x.Clone());
            double loudIn = 0, loudOut = 0, quietIn = 0, quietOut = 0;
            for (int s = 3; s < 7; s++)
            {
                loudIn += Measure.Rms(x, s * Rate + 4800, 4800); loudOut += Measure.Rms(o, s * Rate + 4800, 4800);        // 100–200 ms into the loud part
                quietIn += Measure.Rms(x, s * Rate + 24000, 9600); quietOut += Measure.Rms(o, s * Rate + 24000, 9600);    // 500–700 ms: the quiet
            }
            double ratioIn = Measure.Db(loudIn / quietIn), ratioOut = Measure.Db(loudOut / quietOut);
            double energy = Measure.Db(Measure.Rms(o, 3 * Rate, 4 * Rate) / Measure.Rms(x, 3 * Rate, 4 * Rate));
            Measure.Report($"Neo Low End Contrast {amount * 100:+0;-0} %: loud-to-quiet {ratioIn:F2} → {ratioOut:F2} dB; energy {energy:+0.00;-0.00} dB");
            Assert.That(Math.Sign(ratioOut - ratioIn), Is.EqualTo(Math.Sign(amount)));
            Assert.That(Math.Abs(energy), Is.LessThan(1.5), "the band's energy is kept");
        }

        // ---- Balance ---------------------------------------------------------------------------------

        static double ToneDb(float[] y, double hz, int from, int n)
        {
            // The level of one tone: a one-bin DFT over whole cycles.
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double ph = 2 * Math.PI * hz * (from + i) / Rate;
                re += y[from + i] * Math.Cos(ph);
                im += y[from + i] * Math.Sin(ph);
            }
            return Measure.Db(2 * Math.Sqrt(re * re + im * im) / n);
        }

        [TestCase(6.0, 1.0, 6.0)]
        [TestCase(-6.0, 1.0, -6.0)]
        [TestCase(6.0, 0.5, 3.0)]
        public void BalanceMovesTheLowBandTowardTheTargetOverTheMid(double target, double amount, double expected)
        {
            // A 60 Hz and a 500 Hz tone at the same level: the ratio starts at 0 dB.
            var x = Mix(Measure.Sine(60, Rate * 10, 0.2), Measure.Sine(500, Rate * 10, 0.2));
            var (e, _) = Make(v => { Set(v, P.Xo, 150); Set(v, P.Target, target); Set(v, P.Balance, amount); });
            var (o, _) = Run(e, x, (float[])x.Clone());
            double low = ToneDb(o, 60, 9 * Rate, Rate) - ToneDb(x, 60, 9 * Rate, Rate);
            double mid = ToneDb(o, 500, 9 * Rate, Rate) - ToneDb(x, 500, 9 * Rate, Rate);
            Measure.Report($"Neo Low End Balance toward {target:+0;-0} dB at {amount * 100:F0} %: the low band {low:+0.00;-0.00} dB, the mid {mid:+0.000;-0.000} dB; reading {e.Readings()["balance_db"]:F2} dB");
            Assert.That(low, Is.EqualTo(expected).Within(0.5));
            Assert.That(Math.Abs(mid), Is.LessThan(0.1), "the mid tone is in the low-pass's skirt");
        }

        [Test]
        public void BalanceAtZeroLeavesTheLowBandAlone()
        {
            var x = Mix(Measure.Sine(60, Rate * 4, 0.2), Measure.Sine(500, Rate * 4, 0.05));
            var (e, _) = Make(v => { Set(v, P.Xo, 150); Set(v, P.Target, 12); Set(v, P.Punch, 0.1); });
            var (o, _) = Run(e, x, (float[])x.Clone());
            double low = ToneDb(o, 60, 3 * Rate, Rate) - ToneDb(x, 60, 3 * Rate, Rate);
            Assert.That(Math.Abs(low), Is.LessThan(0.2));
        }

        // ---- Peak control and mono --------------------------------------------------------------

        [Test]
        public void PeakControlHoldsOnlyTheLowBandUnderItsLevel()
        {
            var low = Measure.Sine(60, Rate * 3, 0.9);
            var (e, _) = Make(v => { Set(v, P.Xo, 300); v.Set(P.Peak, 1); Set(v, P.PeakLevel, -6); });
            var (o, _) = Run(e, low, (float[])low.Clone());
            double peak = Measure.Db(Measure.Peak(o, 2 * Rate, Rate));
            var mid = Measure.Sine(2000, Rate * 3, 0.3);
            var x = Mix(low, mid);
            var (e2, _) = Make(v => { Set(v, P.Xo, 300); v.Set(P.Peak, 1); Set(v, P.PeakLevel, -6); });
            var (o2, _) = Run(e2, x, (float[])x.Clone());
            double l2k = ToneDb(o2, 2000, 2 * Rate, Rate) - ToneDb(x, 2000, 2 * Rate, Rate);
            Measure.Report($"Neo Low End Peak control −6 dB on a 0.9 (−0.9 dBFS) 60 Hz tone: its peak {peak:F3} dBFS; a 2 kHz tone beside it {l2k:+0.0000;-0.0000} dB");
            Assert.That(peak, Is.LessThanOrEqualTo(-6.0 + 0.1), "a tone in the low-pass's skirt leaks a little to the rest");
            Assert.That(Math.Abs(l2k), Is.LessThan(0.01));
        }

        [Test]
        public void PeakControlBelowItsLevelIsTransparentAndItsAliasingIsLow()
        {
            var x = Measure.Sine(80, 1 << 17, 0.9);
            var (e, _) = Make(v => { Set(v, P.Xo, 300); v.Set(P.Peak, 1); Set(v, P.PeakLevel, -12); });
            var (o, _) = Run(e, x, (float[])x.Clone());
            int n = 1 << 16;
            int bin = (int)Math.Round(80.0 * n / Rate);
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = o[Rate / 2 + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Measure.Spectrum(w, 0, n);
            // The harmonics fall in the low band's skirt and are the clipper's own; what is not a harmonic is aliasing.
            double fund = mag[bin];
            double worstNon = 0;
            for (int k = bin * 2; k < mag.Length; k++)
            {
                double f = (double)k * Rate / n;
                double h = Math.Round(f / 80.0);
                if (Math.Abs(f - h * 80.0) < 40) continue;    // a bin near a harmonic
                if (h % 2 == 0) { worstNon = Math.Max(worstNon, mag[k]); continue; }
            }
            double thd = 0;
            for (int h = 3; h <= 15; h += 2) thd += Math.Pow(mag[(int)Math.Round(80.0 * h * n / Rate)], 2);
            Measure.Report($"Neo Low End Peak control, a 0.9 tone at 80 Hz into a −12 dB level: harmonics (3rd–15th) {Measure.Db(Math.Sqrt(thd) / fund):F1} dB, other content {Measure.Db(worstNon / fund):F1} dB");
            Assert.That(Measure.Db(worstNon / fund), Is.LessThan(-90));
        }

        [Test]
        public void MonoBelowAFrequencyRemovesTheSideOfTheLowBandAndNothingElse()
        {
            double Side(double hz, bool mono)
            {
                var m = Measure.Sine(hz, Rate * 3, 0.3);
                var l = m;
                var r = m.Select(s => -s).ToArray();
                var (e, _) = Make(v => { Set(v, P.Xo, 300); v.Set(P.Mono, mono ? 1 : 0); Set(v, P.MonoFreq, 100); v.Set(P.Punch, 121); });
                var (ol, or) = Run(e, l, r);
                double side = Measure.Rms(ol, 2 * Rate, Rate);
                return Measure.Db(side / Measure.Rms(l, 2 * Rate, Rate));
            }
            double s40 = Side(40, true), s100 = Side(100, true), s200 = Side(200, true), s1k = Side(1000, true);
            Measure.Report($"Neo Low End mono below 100 Hz (crossover 300 Hz), side-only tones: 40 Hz {s40:F1} dB, 100 Hz {s100:F1} dB, 200 Hz {s200:F1} dB, 1 kHz {s1k:F2} dB");
            Assert.That(s40, Is.LessThan(-30));
            Assert.That(s100, Is.EqualTo(-6).Within(1.0));
            Assert.That(s200, Is.GreaterThan(-2.5));
            Assert.That(Math.Abs(s1k), Is.LessThan(0.01));
        }

        // ---- blocks, silence, zipper, delta ---------------------------------------------------------

        [TestCase(0)]
        [TestCase(1)]
        public void BlocksOf256And128And117And16GiveTheSameSamples(int phase)
        {
            var x = Mix(Kicks(3, out _), Measure.Sine(1000, 3 * Rate, 0.2));
            float[]? first = null;
            foreach (int block in new[] { 256, 128, 117, 16, 1 })
            {
                var (e, vals) = Make(v =>
                {
                    v.Set(P.Phase, phase);
                    Set(v, P.Punch, 5);
                    Set(v, P.Sustain, -3);
                    v.Set(P.Contrast, 800);
                    Set(v, P.Balance, 0.5);
                    v.Set(P.Peak, 1);
                    Set(v, P.PeakLevel, -9);
                    v.Set(P.Mono, 1);
                });
                var (ol, _) = Run(e, x, (float[])x.Clone(), block, pos => { if (pos == 29952) Set(vals, P.Punch, -4); });
                if (first == null) first = ol;
                else for (int i = 0; i < ol.Length; i++)
                        if (BitConverter.SingleToInt32Bits(ol[i]) != BitConverter.SingleToInt32Bits(first[i])) Assert.Fail($"block {block}: sample {i} differs (phase {phase})");
            }
        }

        static double ZipperDb(float[] y)
        {
            int n = 1 << 16;
            var w = new float[n];
            int from = y.Length - n;
            for (int i = 0; i < n; i++) w[i] = y[from + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Measure.Spectrum(w, 0, n);
            int k220 = (int)Math.Round(220.0 * n / Rate), k2k = (int)Math.Round(2000.0 * n / Rate);
            double sine = 0, high = 0;
            for (int k = k220 - 40; k <= k220 + 40; k++) sine += mag[k] * mag[k];
            for (int k = k2k; k < mag.Length; k++) high += mag[k] * mag[k];
            return 10 * Math.Log10(high / sine);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void ParametersSteppingEveryBlockMakeNoZipper(int phase)
        {
            var x = Measure.Sine(220, Rate * 4, 0.5);
            var (e, v) = Make(c => { c.Set(P.Phase, phase); Set(c, P.Xo, 300); });
            var rng = new Random(5);
            var (o, _) = Run(e, x, (float[])x.Clone(), 256, pos =>
            {
                Set(v, P.Sustain, (rng.NextDouble() * 2 - 1) * 12);
                Set(v, P.Punch, (rng.NextDouble() * 2 - 1) * 12);
                v.Set(P.Contrast, 200 + rng.Next(600));
                Set(v, P.Output, (rng.NextDouble() * 2 - 1) * 6);
            });
            double z = ZipperDb(o);
            Measure.Report($"Neo Low End zipper ({(phase == 1 ? "linear" : "minimum")} phase): Punch, Sustain, Contrast and Output stepped every 256-sample block on a 220 Hz sine (crossover 300 Hz): {z:F1} dB above 2 kHz");
            Assert.That(z, Is.LessThanOrEqualTo(-90));
        }

        [Test]
        public void DeltaIsTheOutputMinusTheDelayedInput()
        {
            var x = Mix(Kicks(3, out _), Measure.Sine(1000, 3 * Rate, 0.2));
            foreach (int phase in new[] { 0, 1 })
            {
                Action<ParamValues> setup = v => { v.Set(P.Phase, phase); Set(v, P.Punch, 9); Set(v, P.Sustain, -5); };
                var (e, _) = Make(v => { setup(v); v.Set(P.Delta, 1); });
                var (ol, _) = Run(e, x, (float[])x.Clone());
                var (e2, _) = Make(setup);
                var (wl, _) = Run(e2, x, (float[])x.Clone());
                int la = e.Latency;
                double worst = 0;
                for (int i = Rate; i < x.Length; i++) worst = Math.Max(worst, Math.Abs(ol[i] - (wl[i] - x[i - la])));
                Assert.That(worst, Is.LessThan(1e-6), $"phase {phase}");
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void SilenceAndNaNLeaveNothingBehind(int phase)
        {
            var x = Mix(Kicks(2, out _), Measure.Sine(1000, 2 * Rate, 0.2));
            var (e, _) = Make(v => { v.Set(P.Phase, phase); Set(v, P.Punch, 12); Set(v, P.Sustain, 12); v.Set(P.Contrast, 1000); v.Set(P.Peak, 1); v.Set(P.Mono, 1); });
            Run(e, x, (float[])x.Clone());
            var bad = new float[4096];
            Array.Fill(bad, float.NaN);
            Run(e, bad, (float[])bad.Clone());
            var z = new float[Rate * 2];
            var (ol, or) = Run(e, z, (float[])z.Clone());
            for (int i = 20000; i < ol.Length; i++)
            {
                Assert.That(float.IsFinite(ol[i]) && !float.IsSubnormal(ol[i]), $"sample {i}");
                Assert.That(Math.Abs(or[i]), Is.LessThan(1e-9), $"sample {i}");
            }
        }

        [Test]
        public void SwitchingThePhaseModeStaysFiniteAndComesBackAligned()
        {
            var x = Measure.Sine(220, Rate * 4, 0.5);
            var (e, v) = Make();
            var (o, _) = Run(e, x, (float[])x.Clone(), 256, pos =>
            {
                if (pos == Rate) v.Set(P.Phase, 1);
                if (pos == 2 * Rate) v.Set(P.Phase, 0);
            });
            foreach (var s in o) Assert.That(float.IsFinite(s));
            Assert.That(o[3 * Rate + 1000], Is.EqualTo(x[3 * Rate + 1000 - e.Latency]));
            double worst = 0;
            for (int i = 2 * Rate + 2000; i < o.Length; i++) worst = Math.Max(worst, Math.Abs(o[i] - o[i - 1]));
            Assert.That(worst, Is.LessThan(1.1 * 2 * Math.PI * 220 / Rate * 0.5));
        }

        [Test]
        public void ReadingsReportTheLowBandAndTheLatency()
        {
            var x = Mix(Kicks(5, out _), Measure.Sine(1000, 5 * Rate, 0.1));
            var (e, _) = Make(v => { v.Set(P.Phase, 1); Set(v, P.Balance, 1); Set(v, P.Punch, 6); });
            Run(e, x, (float[])x.Clone());
            var rd = e.Readings();
            Measure.Report($"Neo Low End readings: {string.Join(", ", rd.Select(kv => $"{kv.Key} {kv.Value:F2}"))}");
            Assert.That(rd["latency_samples"], Is.EqualTo(LowEndDesign.LinearLatency(Rate)));
            foreach (var key in new[] { "low_crest_in_db", "low_crest_out_db", "balance_db", "balance_gain_db", "peak_clip_db", "gain_match_db" })
                Assert.That(rd.ContainsKey(key), key);
        }

        // ---- allocation and CPU -------------------------------------------------------------------

        [TestCase(0)]
        [TestCase(1)]
        public void NothingIsAllocatedPerBlock(int phase)
        {
            var x = Mix(Kicks(5, out _), Measure.Sine(1000, 5 * Rate, 0.2));
            var (e, v) = Make(c =>
            {
                c.Set(P.Phase, phase);
                Set(c, P.Punch, 4);
                Set(c, P.Sustain, -3);
                c.Set(P.Contrast, 700);
                Set(c, P.Balance, 0.6);
                c.Set(P.Peak, 1);
                Set(c, P.PeakLevel, -9);
                c.Set(P.Mono, 1);
            });
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                int pos = (b % 900) * 256;
                Array.Copy(x, pos, bl, 0, 256);
                Array.Copy(x, pos, br, 0, 256);
                if (b % 7 == 0) Set(v, P.Punch, (b % 13) - 6);
                if (b % 400 == 0) Set(v, P.Xo, 80 + (b % 5) * 20);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 60; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 60));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        // Throughput guards (Throughput.cs; machines.md §4) against the reference workload in the same run. Figures at the
        // reference's speed (minimum and linear phase, every control working): the 2-core Xeon 139, 116. Floors per
        // architecture: x64 and arm64.
        [TestCase(0, 70, 45)]
        [TestCase(1, 55, 35)]
        public void RunsFasterThanRealTime(int phase, double x64, double arm64)
        {
            var x = Mix(Kicks(3, out _), Measure.Sine(1000, 3 * Rate, 0.2));
            const int Warm = Rate;
            Throughput.Guard($"Neo Low End CPU ({(phase == 1 ? "linear" : "minimum")} phase, every control working)", Throughput.Floor(x64, arm64), 2,
                Rate, LowEndEngine.MaxBlock, () =>
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.Phase, phase);
                    Set(v, P.Punch, 4);
                    Set(v, P.Sustain, -3);
                    v.Set(P.Contrast, 700);
                    Set(v, P.Balance, 0.6);
                    v.Set(P.Peak, 1);
                    Set(v, P.PeakLevel, -9);
                    v.Set(P.Mono, 1);
                });
                var bl = new float[LowEndEngine.MaxBlock];
                var br = new float[LowEndEngine.MaxBlock];
                void Block(int pos, int n)
                {
                    Array.Copy(x, pos, bl, 0, n);
                    Array.Copy(x, pos, br, 0, n);
                    e.Process(bl, br, n);
                }
                for (int pos = 0; pos < Warm; pos += LowEndEngine.MaxBlock) Block(pos, LowEndEngine.MaxBlock);
                return Throughput.InBlocks(LowEndEngine.MaxBlock, (pos, n) => Block(Warm + pos, n));
            }, Measure.Report);
        }
    }
}
