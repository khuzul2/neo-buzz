using NeoBuzz.Dsp;
using NeoBuzz.Spectral;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Spectral (docs/specs/mastering-tools.md §5.16): the gate's criteria as built (null, suppression of a +12 dB Q 20
    /// resonance on pink noise, the change to plain pink, pre-echo, time-domain aliasing, spectral flux, latency, CPU), and the
    /// machine's modes, lift, region, mix and delta.
    /// </summary>
    [TestFixture]
    public class SpectralTests
    {
        const int Rate = Measure.Rate;

        static (SpectralEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new SpectralEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        /// <summary>Suppress at the given depth and knee, everything else at its default.</summary>
        static Action<ParamValues> Suppress(double depth, double knee = 4) => v => { Set(v, P.Depth, depth); Set(v, P.Knee, knee); };

        static (float[] L, float[] R) Run(SpectralEngine e, float[] l, float[] r, int block = SpectralEngine.MaxBlock)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[SpectralEngine.MaxBlock];
            var br = new float[SpectralEngine.MaxBlock];
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

        /// <summary>An RBJ peaking filter (gain in dB, Q) over x.</summary>
        static float[] Peaking(float[] x, double hz, double db, double q, double rate)
        {
            double A = Math.Pow(10, db / 40), w = 2 * Math.PI * hz / rate, al = Math.Sin(w) / (2 * q), c = Math.Cos(w);
            double b0 = 1 + al * A, b1 = -2 * c, b2 = 1 - al * A, a0 = 1 + al / A, a1 = -2 * c, a2 = 1 - al / A;
            var y = new float[x.Length];
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double v = (b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = (float)v;
            }
            return y;
        }

        /// <summary>Welch power spectrum (Hann, 50 % overlap, nfft points) of x from start.</summary>
        static double[] Welch(float[] x, int start, int nfft = 8192)
        {
            var p = new double[nfft / 2 + 1];
            var re = new double[nfft];
            var im = new double[nfft];
            int frames = 0;
            for (int s = start; s + nfft <= x.Length; s += nfft / 2)
            {
                for (int i = 0; i < nfft; i++) { re[i] = x[s + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / nfft)); im[i] = 0; }
                NeoBuzz.Dsp.Fft.Transform(re, im, false);
                for (int k = 0; k <= nfft / 2; k++) p[k] += re[k] * re[k] + im[k] * im[k];
                frames++;
            }
            for (int k = 0; k < p.Length; k++) p[k] /= frames;
            return p;
        }

        static double BandDb(double[] a, double[] b, double lo, double hi, double rate, int nfft = 8192)
        {
            double sa = 0, sb = 0;
            for (int k = 1; k < a.Length; k++)
            {
                double f = k * rate / nfft;
                if (f < lo || f >= hi) continue;
                sa += a[k]; sb += b[k];
            }
            return 10 * Math.Log10(sa / sb);
        }

        static double NullDb(float[] input, float[] output, int latency)
        {
            double err = 0, sig = 0;
            for (int i = latency + 1; i < output.Length; i++)
            {
                double d = output[i] - input[i - latency];
                err += d * d;
                sig += (double)input[i - latency] * input[i - latency];
            }
            return 10 * Math.Log10(err / sig);
        }

        sealed class FixedMask : NeoBuzz.Dsp.ISpectralProcessor
        {
            readonly float[] g;
            public FixedMask(float[] g) { this.g = g; }
            public void Process(float[] re, float[] im, int n) => NeoBuzz.Dsp.Stft.ApplyGains(re, im, n, g);
        }

        /// <summary>A fixed mask on white noise against the ideal linear filter of the same mask: the time-domain aliasing.</summary>
        [Test]
        public void TimeDomainAliasingOfAFixedMask()
        {
            const int Rate = 48000, N = 2048;
            var gains = new float[N / 2 + 1];
            for (int k = 0; k < gains.Length; k++)
            {
                double f = k * (double)Rate / N, x = (f - 3000) / 60.0;
                gains[k] = (float)Math.Pow(10, -12.0 * Math.Exp(-x * x) / 20.0);
            }
            var x0 = Measure.PinkNoise(30000, 0.1, 41);
            var y = (float[])x0.Clone();
            var yr = (float[])x0.Clone();
            var stft = new NeoBuzz.Dsp.Stft(N);
            stft.Process(y, yr, 0, y.Length, new FixedMask(gains));
            // The ideal: the zero-phase kernel of the same gains, N taps centred on 0 (N/2 each side).
            var re = new double[N];
            var im = new double[N];
            for (int k = 0; k <= N / 2; k++) re[k] = gains[k];
            for (int k = 1; k < N / 2; k++) re[N - k] = gains[k];
            NeoBuzz.Dsp.Fft.Transform(re, im, true);
            var h = new double[N];
            for (int i = 0; i < N; i++) h[i] = re[i];
            double err = 0, sig = 0;
            for (int t = 3 * N; t < x0.Length - N; t++)
            {
                double ideal = 0;
                for (int m = -N / 2 + 1; m <= N / 2; m++) ideal += h[(m + N) % N] * x0[t - m];
                double got = y[t + N - 1];
                err += (got - ideal) * (got - ideal);
                sig += ideal * ideal;
            }
            Measure.Report($"Spectral prototype Multiply: time-domain aliasing, a fixed -12 dB notch (Gaussian, 60 Hz) at 3 kHz on pink against the ideal linear filter of {N} taps: error {10 * Math.Log10(err / sig):F1} dB of the signal");
        }


        // ── null ──────────────────────────────────────────────────────────────

        [Test]
        public void UnitMaskNullsAndMixZeroIsBitForBit()
        {
            var x = Measure.PinkNoise(Rate * 3, 0.1, 5);
            var y = Measure.PinkNoise(Rate * 3, 0.1, 6);
            var (e, _) = Make(Suppress(0));
            var (ol, or) = Run(e, x, y);
            double n = Math.Max(NullDb(x, ol, e.Latency), NullDb(y, or, e.Latency));
            // A resonant signal at full depth with Mix 0 returns the delayed input exactly.
            var res = Peaking(x, 3000, 12, 20, Rate);
            var (e0, _) = Make(v => { Suppress(1)(v); Set(v, P.Mix, 0); });
            var (zl, zr) = Run(e0, res, y, 117);
            int bad = 0;
            for (int i = e0.Latency; i < zl.Length; i++) if (zl[i] != res[i - e0.Latency] || zr[i] != y[i - e0.Latency]) bad++;
            Measure.Report($"Neo Spectral: unit mask (Depth 0) null {n:F1} dB, latency {e.Latency} samples ({e.Latency * 1000.0 / Rate:F1} ms); Mix 0 differs from the delayed input in {bad} samples");
            Assert.That(n, Is.LessThan(-120));
            Assert.That(bad, Is.Zero);
            Assert.That(e.Latency, Is.EqualTo(2047));
        }

        [TestCase(44100, 2047)]
        [TestCase(96000, 4095)]
        public void LatencyIsTheFrame(int rate, int latency)
        {
            var (e, _) = Make(Suppress(0), rate);
            Assert.That(e.Latency, Is.EqualTo(latency));
            Assert.That(e.Readings()["latency_samples"], Is.EqualTo(latency));
        }

        // ── suppression and plain pink ───────────────────────────────────────

        [TestCase(44100)]
        [TestCase(48000)]
        [TestCase(96000)]
        public void SuppressesAResonanceAndLeavesPlainPinkAlone(int rate)
        {
            int warm = rate * 4, seconds = 24;
            var pink = Measure.PinkNoise(rate * seconds, 0.1, 21);
            var res = Peaking(pink, 3000, 12, 20, rate);
            int nfft = rate > 66000 ? 16384 : 8192;
            var pr = Welch(pink, warm, nfft);
            var rr = Welch(res, warm, nfft);
            var (e, _) = Make(Suppress(1), rate);
            var (o, _) = Run(e, res, res);
            var po = Welch(o, warm + e.Latency, nfft);
            double Peak(double[] a, double[] b)
            {
                double best = -99;
                for (int k = 3; k < a.Length - 3; k++)
                {
                    double f = k * (double)rate / nfft;
                    if (f < 2850 || f > 3150) continue;
                    best = Math.Max(best, 10 * Math.Log10((a[k - 1] + a[k] + a[k + 1]) / (b[k - 1] + b[k] + b[k + 1])));
                }
                return best;
            }
            double before = Peak(rr, pr), after = Peak(po, pr);
            double band = BandDb(po, pr, 2850, 3150, rate, nfft);
            double worstOff = 0;
            for (double c = 100; c < 16000; c *= Math.Pow(2, 1 / 3.0))
            {
                double lo = c / Math.Pow(2, 1 / 6.0), hi = c * Math.Pow(2, 1 / 6.0);
                if (hi > 2700 && lo < 3300) continue;
                worstOff = Math.Max(worstOff, Math.Abs(BandDb(po, pr, lo, hi, rate, nfft)));
            }
            double Worst(float[] y, int lat)
            {
                var py = Welch(y, warm + lat, nfft);
                double w = 0;
                for (double c = 125; c < 16000; c *= Math.Pow(2, 1 / 3.0))
                {
                    double d = BandDb(py, pr, c / Math.Pow(2, 1 / 6.0), c * Math.Pow(2, 1 / 6.0), rate, nfft);
                    if (Math.Abs(d) > Math.Abs(w)) w = d;
                }
                return w;
            }
            var (d0, _) = Make(null, rate);
            var (pd, _) = Run(d0, pink, pink);
            var (d1, _) = Make(Suppress(1), rate);
            var (pm, _) = Run(d1, pink, pink);
            double pinkDefault = Worst(pd, d0.Latency), pinkMax = Worst(pm, d1.Latency);
            Measure.Report($"Neo Spectral at {rate / 1000.0:0.#} kHz: +12 dB Q 20 at 3 kHz on pink, Depth 100 %: peak over the envelope {before:F2} dB -> {after:F2} dB (band 2.85-3.15 kHz {band:F2} dB; worst third octave elsewhere {worstOff:F2} dB); plain pink, worst third octave: defaults {pinkDefault:F2} dB, Depth 100 % {pinkMax:F2} dB");
            Assert.That(after, Is.LessThan(1.0), "suppression");
            Assert.That(Math.Abs(pinkDefault), Is.LessThan(0.5), "plain pink at the defaults");
        }

        // ── artefacts ─────────────────────────────────────────────────────────

        [Test]
        public void PreEchoBeforeAClick()
        {
            const int Seconds = 8;
            var pink = Measure.PinkNoise(Rate * Seconds, 0.1, 31);
            var res = Peaking(pink, 3000, 12, 20, Rate);
            const int T0 = Rate * 6;
            var click = (float[])res.Clone();
            click[T0] += 1f;
            var (e1, _) = Make(Suppress(1));
            var (e2, _) = Make(Suppress(1));
            var (a, _) = Run(e1, click, click);
            var (b, _) = Run(e2, res, res);
            int lat = e1.Latency, N = 2048;
            double peakOut = 0;
            for (int i = T0 + lat - 8; i <= T0 + lat + 8; i++) peakOut = Math.Max(peakOut, Math.Abs(a[i] - b[i]));
            double maxPre = 0, en = 0, enClick = 0;
            for (int i = T0 + lat - N; i < T0 + lat - 16; i++) { double d = a[i] - b[i]; maxPre = Math.Max(maxPre, Math.Abs(d)); en += d * d; }
            for (int i = T0 + lat - 16; i <= T0 + lat + 16; i++) { double d = a[i] - b[i]; enClick += d * d; }
            var plain = (float[])pink.Clone();
            plain[T0] += 1f;
            var (e3, _) = Make(Suppress(1));
            var (e4, _) = Make(Suppress(1));
            var (c, _) = Run(e3, plain, plain);
            var (d0, _) = Run(e4, pink, pink);
            double pk2 = 0, mp2 = 0;
            for (int i = T0 + lat - 8; i <= T0 + lat + 8; i++) pk2 = Math.Max(pk2, Math.Abs(c[i] - d0[i]));
            for (int i = T0 + lat - N; i < T0 + lat - 16; i++) mp2 = Math.Max(mp2, Math.Abs(c[i] - d0[i]));
            Measure.Report($"Neo Spectral: a click (amplitude 1, pink at 0.1 rms) through a +12 dB Q 20 resonance at Depth 100 %: the output change in the {N} samples before the click is {20 * Math.Log10(maxPre / peakOut):F1} dB at its worst sample and {10 * Math.Log10(en / enClick):F1} dB in energy against the click's own (the worst sample is {20 * Math.Log10(maxPre / 0.1):F1} dB against the noise's rms); on plain pink {20 * Math.Log10(mp2 / pk2):F1} dB");
            Assert.That(20 * Math.Log10(maxPre / peakOut), Is.LessThan(-25), "pre-echo against the click");
        }

        /// <summary>Frame-to-frame change of the short-time spectrum of plain pink noise, in against out: musical noise shows as flux added.</summary>
        [TestCase(0.5)]
        [TestCase(1.0)]
        public void SpectralFluxOnNoise(double depth)
        {
            const int Nf = 2048, Hop = 512;
            var pink = Measure.PinkNoise(Rate * 12, 0.1, 51);
            var (e, _) = Make(Suppress(depth));
            var (o, _) = Run(e, pink, pink);
            double Flux(float[] x, int start)
            {
                var prev = new double[Nf / 2 + 1];
                var re = new double[Nf];
                var im = new double[Nf];
                double num = 0, den = 0;
                bool have = false;
                for (int s = start; s + Nf <= x.Length; s += Hop)
                {
                    for (int i = 0; i < Nf; i++) { re[i] = x[s + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Nf)); im[i] = 0; }
                    NeoBuzz.Dsp.Fft.Transform(re, im, false);
                    for (int k = 4; k < Nf / 2; k++)
                    {
                        double m = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                        if (have) { num += (m - prev[k]) * (m - prev[k]); den += prev[k] * prev[k]; }
                        prev[k] = m;
                    }
                    have = true;
                }
                return num / den;
            }
            int warm = Rate * 2;
            double ratio = 10 * Math.Log10(Flux(o, warm + e.Latency) / Flux(pink, warm));
            var (seen, cut) = e.Cells;
            Measure.Report($"Neo Spectral: plain pink, Depth {depth * 100:0} %: spectral flux out/in {ratio:F2} dB; {100.0 * cut / seen:F2} % of mask cells cut by more than 1 dB");
            Assert.That(ratio, Is.LessThan(0.5), "musical noise would add flux");
        }

        // ── modes, lift, region, mix, delta ─────────────────────────────────

        /// <summary>The slope of a signal's third-octave power from 200 Hz to 8 kHz, dB per octave (a least-squares line).</summary>
        static double Slope(float[] x, int start)
        {
            var p = Welch(x, start);
            var xs = new List<double>();
            var ys = new List<double>();
            for (double c = 200; c < 8000; c *= Math.Pow(2, 1 / 3.0))
            {
                double lo = c / Math.Pow(2, 1 / 6.0), hi = c * Math.Pow(2, 1 / 6.0), s = 0;
                for (int k = 1; k < p.Length; k++) { double f = k * (double)Rate / 8192; if (f >= lo && f < hi) s += p[k]; }
                xs.Add(Math.Log2(c)); ys.Add(10 * Math.Log10(s));
            }
            double mx = xs.Average(), my = ys.Average(), num = 0, den = 0;
            for (int i = 0; i < xs.Count; i++) { num += (xs[i] - mx) * (ys[i] - my); den += (xs[i] - mx) * (xs[i] - mx); }
            // The band's width doubles every octave: 3.01 dB per octave is the band's own, so a white signal reads +3.
            return num / den - 3.0103;
        }

        [Test]
        public void BalanceMovesTheSpectrumTowardPinkAndTiltMovesTheTarget()
        {
            var white = Measure.PinkNoise(Rate * 20, 0.1, 81);
            // A brighter-than-pink signal: pink through a +3 dB/oct tilt (white noise's slope is 0 dB/oct of power per band: slope +0).
            var rng = new Rng(82);
            var bright = new float[Rate * 20];
            for (int i = 0; i < bright.Length; i++) bright[i] = 0.1f * rng.NextBipolar();
            int warm = Rate * 6;
            double before = Slope(bright, warm);
            Action<ParamValues> bal(double tilt) => v => { Set(v, P.Mode, 1); Set(v, P.Depth, 1); Set(v, P.Lift, 12); Set(v, P.Tilt, tilt); Set(v, P.Release, 0.05); };
            var (e0, _) = Make(bal(0));
            var (o0, _) = Run(e0, bright, bright);
            var (e1, _) = Make(bal(3));
            var (o1, _) = Run(e1, bright, bright);
            double flat = Slope(o0, warm + e0.Latency), tilted = Slope(o1, warm + e1.Latency);
            Measure.Report($"Neo Spectral Balance: white noise (slope {before:F2} dB/oct) -> {flat:F2} dB/oct (target -3), with Tilt +3 dB/oct {tilted:F2} dB/oct (target above 300 Hz -3 + 3 = 0)");
            Assert.That(Math.Abs(flat + 3), Is.LessThan(1.0), "toward pink");
            Assert.That(tilted - flat, Is.GreaterThan(1.5), "the tilt raises the highs");
        }

        [Test]
        public void StabilizePullsASuddenStepTowardTheLongTermAverageAndSuppressDoesNot()
        {
            int n = Rate * 12;
            var pink = Measure.PinkNoise(n, 0.05, 91);
            var step = (float[])pink.Clone();
            for (int i = Rate * 8; i < Rate * 9; i++) step[i] *= 3.16f;   // +10 dB for a second
            double Lvl(float[] x, int a, int len) => Measure.Db(Measure.Rms(x, a, len));
            double gainOf(int mode)
            {
                var (e, _) = Make(v => { Set(v, P.Mode, mode); Set(v, P.Depth, 1); Set(v, P.Knee, 2); });
                var (o, _) = Run(e, step, step);
                int lat = e.Latency;
                return Lvl(o, Rate * 8 + lat + Rate / 4, Rate / 2) - Lvl(step, Rate * 8 + Rate / 4, Rate / 2);
            }
            double stab = gainOf(2), supp = gainOf(0);
            Measure.Report($"Neo Spectral: a +10 dB step of pink noise for a second: the level change at the step's middle is {stab:F1} dB with Stabilize, {supp:F1} dB with Suppress");
            Assert.That(stab, Is.LessThan(-2), "Stabilize");
            Assert.That(supp, Is.GreaterThan(-1), "Suppress leaves broadband level alone");
        }

        [Test]
        public void LiftFillsADipOnlyWhenAsked()
        {
            int n = Rate * 20, warm = Rate * 4;
            var pink = Measure.PinkNoise(n, 0.1, 101);
            var dip = Peaking(pink, 2000, -12, 5, Rate);
            var pr = Welch(pink, warm);
            double At(float[] x, int lat) => BandDb(Welch(x, warm + lat), pr, 1900, 2100, Rate);
            var (e0, _) = Make(Suppress(1));
            var (o0, _) = Run(e0, dip, dip);
            var (e1, _) = Make(v => { Suppress(1)(v); Set(v, P.Lift, 12); });
            var (o1, _) = Run(e1, dip, dip);
            double none = At(o0, e0.Latency), lifted = At(o1, e1.Latency), raw = At(dip, 0);
            Measure.Report($"Neo Spectral: a -12 dB Q 5 dip at 2 kHz on pink reads {raw:F1} dB; Lift 0 {none:F1} dB, Lift 12 dB {lifted:F1} dB");
            Assert.That(none - raw, Is.LessThan(0.7), "cut only");
            Assert.That(lifted - raw, Is.GreaterThan(2), "filled");
        }

        [Test]
        public void RegionKeepsTheMachineOutOfTheRest()
        {
            int n = Rate * 16, warm = Rate * 4;
            var pink = Measure.PinkNoise(n, 0.1, 111);
            var res = Peaking(pink, 3000, 12, 20, Rate);
            var pr = Welch(pink, warm);
            double Peak(float[] x, int lat)
            {
                var p = Welch(x, warm + lat);
                double best = -99;
                for (int k = 3; k < p.Length - 3; k++)
                {
                    double f = k * (double)Rate / 8192;
                    if (f < 2850 || f > 3150) continue;
                    best = Math.Max(best, 10 * Math.Log10((p[k - 1] + p[k] + p[k + 1]) / (pr[k - 1] + pr[k] + pr[k + 1])));
                }
                return best;
            }
            var (a, _) = Make(v => { Suppress(1)(v); Set(v, P.Low, 4000); });
            var (oa, _) = Run(a, res, res);
            var (b, _) = Make(v => { Suppress(1)(v); Set(v, P.High, 2500); });
            var (ob, _) = Run(b, res, res);
            var (c, _) = Make(v => { Suppress(1)(v); Set(v, P.Low, 2500); Set(v, P.High, 3500); });
            var (oc, _) = Run(c, res, res);
            double pa = Peak(oa, a.Latency), pb = Peak(ob, b.Latency), pc = Peak(oc, c.Latency);
            Measure.Report($"Neo Spectral: the resonance's peak over the envelope, 11.9 dB untreated: Low limit 4 kHz {pa:F1} dB, High limit 2.5 kHz {pb:F1} dB, region 2.5 - 3.5 kHz {pc:F1} dB");
            Assert.That(pa, Is.GreaterThan(9));
            Assert.That(pb, Is.GreaterThan(9));
            Assert.That(pc, Is.LessThan(1.5));
        }

        [Test]
        public void MixAndDeltaAndTheReadings()
        {
            var pink = Measure.PinkNoise(Rate * 8, 0.1, 121);
            var res = Peaking(pink, 3000, 12, 20, Rate);
            var (e, _) = Make(Suppress(1));
            var (wet, _) = Run(e, res, res);
            var (h, _) = Make(v => { Suppress(1)(v); Set(v, P.Mix, 0.5); });
            var (half, _) = Run(h, res, res);
            var (d, _) = Make(v => { Suppress(1)(v); Set(v, P.Delta, 1); });
            var (delta, _) = Run(d, res, res);
            int lat = e.Latency;
            double worstHalf = 0, worstDelta = 0;
            for (int i = Rate * 5; i < Rate * 8; i++)
            {
                double dry = res[i - lat];
                worstHalf = Math.Max(worstHalf, Math.Abs(half[i] - 0.5 * (dry + wet[i])));
                worstDelta = Math.Max(worstDelta, Math.Abs(delta[i] - (wet[i] - dry)));
            }
            var r = e.Readings();
            Measure.Report($"Neo Spectral: Mix 50 % differs from the mean of dry and wet by {worstHalf:E1}, Delta from wet - dry by {worstDelta:E1}; readings: reduction {r["reduction_db"]} dB, mean {r["mean_reduction_db"]} dB, gain match {(r.TryGetValue("gain_match_db", out var gm) ? gm : double.NaN)} dB");
            Assert.That(worstHalf, Is.LessThan(1e-5));
            Assert.That(worstDelta, Is.LessThan(1e-5));
            Assert.That(r["reduction_db"], Is.GreaterThan(5));
            Assert.That(r["mean_reduction_db"], Is.GreaterThan(0));
            Assert.That(e.Curve.Min(), Is.LessThan(-5), "the editor's curve shows the cut");
        }

        // ── hygiene and CPU ─────────────────────────────────────────────────

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void BlocksOfAnySizeGiveTheSameBits(int mode)
        {
            var l = Peaking(Measure.PinkNoise(Rate * 2, 0.2, 71), 3000, 12, 20, Rate);
            var r = Measure.PinkNoise(Rate * 2, 0.2, 72);
            float[] first = null;
            foreach (int block in new[] { 256, 117, 16 })
            {
                var (e, _) = Make(v => { Set(v, P.Mode, mode); Set(v, P.Depth, 0.8); Set(v, P.Mix, 0.7); Set(v, P.Lift, 6); });
                var (ol, _) = Run(e, l, r, block);
                if (first == null) first = ol;
                else Assert.That(ol, Is.EqualTo(first), $"block {block}");
            }
            var (e2, _) = Make(v => { Set(v, P.Mode, mode); Set(v, P.Depth, 0.8); Set(v, P.Mix, 0.7); Set(v, P.Lift, 6); });
            Assert.That(Run(e2, l, r).L, Is.EqualTo(first), "a second run");
            Assert.That(Measure.Peak(first, 0, first.Length), Is.LessThan(2));
        }

        [Test]
        public void NothingIsAllocatedPerBlock()
        {
            var l = Peaking(Measure.PinkNoise(256 * 4, 0.2, 5), 3000, 12, 20, Rate);
            var r = Measure.PinkNoise(256 * 4, 0.2, 6);
            var (e, v) = Make(Suppress(1));
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                if (b % 50 == 0)
                {
                    int s = b / 50;
                    Set(v, P.Mode, s % 3); Set(v, P.Depth, 0.3 + (s % 7) * 0.1); Set(v, P.Mix, s % 5 == 0 ? 0.5 : 1);
                    Set(v, P.Lift, (s % 4) * 3); Set(v, P.Low, 100 + 50 * (s % 5)); Set(v, P.Smoothing, s % 7); Set(v, P.Delta, s % 6 == 5 ? 1 : 0);
                }
                Array.Copy(l, (b % 4) * 256, bl, 0, 256);
                Array.Copy(r, (b % 4) * 256, br, 0, 256);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 60; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 60));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [TestCase(44100, 0, 0)]
        [TestCase(48000, 0, 0)]
        [TestCase(96000, 0, 0)]
        [TestCase(48000, 1, 0)]
        [TestCase(48000, 2, 0)]
        public void RunsFasterThanRealTime(int rate, int mode, int unused)
        {
            int n = rate * 3;
            var l = Peaking(Measure.PinkNoise(n, 0.1, 61), 3000, 12, 20, rate);
            var r = Peaking(Measure.PinkNoise(n, 0.1, 62), 3000, 12, 20, rate);
            const int Block = SpectralEngine.MaxBlock;
            int warm = rate;
            string[] names = { "Suppress", "Balance", "Stabilize" };
            double x64 = 20, arm64 = 12;   // placeholder floors until the runners' figures are in
            Throughput.Guard($"Neo Spectral CPU ({names[mode]}, {rate / 1000.0:0.#} kHz, resonant pink, Depth 100 %)", Throughput.Floor(x64, arm64), 2, rate, Block, () =>
            {
                var (e, _) = Make(v => { Suppress(1)(v); Set(v, P.Mode, mode); Set(v, P.Lift, 6); }, rate);
                var bl = new float[Block];
                var br = new float[Block];
                void B(int pos, int len)
                {
                    pos %= n - Block;
                    Array.Copy(l, pos, bl, 0, len);
                    Array.Copy(r, pos, br, 0, len);
                    e.Process(bl, br, len);
                }
                for (int pos = 0; pos < warm; pos += Block) B(pos, Block);
                return Throughput.InBlocks(Block, (pos, len) => B(warm + pos, len));
            }, Measure.Report);
        }
    }
}
