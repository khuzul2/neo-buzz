using NeoBuzz.Spectral;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The M7 gate (docs/specs/mastering-tools.md §5.16): a prototype resonance suppressor measured against the gate's
    /// criteria: null, suppression of a +12 dB Q 20 resonance on pink noise, the change to plain pink noise, pre-echo,
    /// time-domain aliasing, spectral flux, latency and CPU, for both ways of applying the mask (Multiply, Fir).
    /// </summary>
    [TestFixture]
    public class SpectralPrototypeTests
    {
        static readonly string[] Modes = { "Multiply", "Fir" };

        static SpectralEngine Make(int mode, double rate = 48000, float depth = 0.5f, float thr = 3.5f, int fir = 256)
        {
            var e = new SpectralEngine(rate, mode, fir) { Depth = depth, ThresholdDb = thr };
            e.Configure();
            return e;
        }

        static (float[], float[]) Run(SpectralEngine e, float[] l, float[] r, int block = 512)
        {
            var ol = (float[])l.Clone();
            var or = (float[])r.Clone();
            var bl = new float[block];
            var br = new float[block];
            for (int i = 0; i < ol.Length; i += block)
            {
                int n = Math.Min(block, ol.Length - i);
                Array.Copy(ol, i, bl, 0, n); Array.Copy(or, i, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, i, n); Array.Copy(br, 0, or, i, n);
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

        static float[] Stereo(float[] x) => (float[])x.Clone();

        // ── null ──────────────────────────────────────────────────────────────

        [TestCase(0)]
        [TestCase(1)]
        public void UnitMaskNullsAndMixZeroIsBitForBit(int mode)
        {
            const int Rate = 48000;
            var x = Measure.PinkNoise(Rate * 3, 0.1, 5);
            var y = Measure.PinkNoise(Rate * 3, 0.1, 6);
            var e = Make(mode, Rate, depth: 0f);
            var (ol, or) = Run(e, x, y);
            double n = Math.Max(NullDb(x, ol, e.Latency), NullDb(y, or, e.Latency));
            // A resonant signal at full depth with Mix 0 returns the delayed input exactly.
            var res = Peaking(x, 3000, 12, 20, Rate);
            var e0 = Make(mode, Rate, depth: 1f, thr: 3.5f);
            e0.Mix = 0f;
            var (zl, zr) = Run(e0, res, y, 300);
            int bad = 0;
            for (int i = e0.Latency; i < zl.Length; i++) if (zl[i] != res[i - e0.Latency] || zr[i] != y[i - e0.Latency]) bad++;
            Measure.Report($"Spectral prototype {Modes[mode]}: unit mask (Depth 0) null {n:F1} dB, latency {e.Latency} samples ({e.Latency * 1000.0 / Rate:F1} ms); Mix 0 differs from the delayed input in {bad} samples");
            Assert.That(n, Is.LessThan(-120));
            Assert.That(bad, Is.Zero);
        }

        // ── suppression and plain pink ───────────────────────────────────────

        [TestCase(0, 256)]
        [TestCase(1, 256)]
        [TestCase(1, 512)]
        public void SuppressesAResonanceAndLeavesPlainPinkAlone(int mode, int fir)
        {
            const int Rate = 48000, Seconds = 24, Warm = Rate * 4;
            var pink = Measure.PinkNoise(Rate * Seconds, 0.1, 21);
            var res = Peaking(pink, 3000, 12, 20, Rate);
            var pr = Welch(pink, Warm);
            var rr = Welch(res, Warm);
            // Maximum depth for the resonance.
            var e = Make(mode, Rate, depth: 1f, thr: 3.5f, fir: fir);
            var (o, _) = Run(e, res, res);
            var po = Welch(o, Warm + e.Latency);
            // The resonance's peak over the unresonant pink (3-bin smoothed) before and after.
            double Peak(double[] a, double[] b)
            {
                double best = -99;
                for (int k = 3; k < a.Length - 3; k++)
                {
                    double f = k * Rate / 8192.0;
                    if (f < 2850 || f > 3150) continue;
                    double x = 10 * Math.Log10((a[k - 1] + a[k] + a[k + 1]) / (b[k - 1] + b[k] + b[k + 1]));
                    best = Math.Max(best, x);
                }
                return best;
            }
            double before = Peak(rr, pr), after = Peak(po, pr);
            double band = BandDb(po, pr, 2850, 3150, Rate);
            // Off the resonance: the third-octave bands of the output against the unresonant pink.
            double worstOff = 0;
            for (double c = 100; c < 16000; c *= Math.Pow(2, 1 / 3.0))
            {
                double lo = c / Math.Pow(2, 1 / 6.0), hi = c * Math.Pow(2, 1 / 6.0);
                if (hi > 2700 && lo < 3300) continue;
                worstOff = Math.Max(worstOff, Math.Abs(BandDb(po, pr, lo, hi, Rate)));
            }
            // Plain pink at the default depth, and at the maximum.
            var d0 = Make(mode, Rate, fir: fir);
            var (pd, _) = Run(d0, pink, pink);
            var d1 = Make(mode, Rate, depth: 1f, thr: 3.5f, fir: fir);
            var (pm, _) = Run(d1, pink, pink);
            double Worst(float[] y, int lat)
            {
                var py = Welch(y, Warm + lat);
                var pi = Welch(pink, Warm);
                double w = 0, wf = 0;
                for (double c = 50; c < 16000; c *= Math.Pow(2, 1 / 3.0))
                {
                    double d = BandDb(py, pi, c / Math.Pow(2, 1 / 6.0), c * Math.Pow(2, 1 / 6.0), Rate);
                    if (Math.Abs(d) > Math.Abs(w)) { w = d; wf = c; }
                }
                return w * 1000 + wf * 1e-9; // not used: see below
            }
            double WorstDb(float[] y, int lat)
            {
                var py = Welch(y, Warm + lat);
                var pi = Welch(pink, Warm);
                double w = 0;
                for (double c = 50; c < 16000; c *= Math.Pow(2, 1 / 3.0))
                {
                    double d = BandDb(py, pi, c / Math.Pow(2, 1 / 6.0), c * Math.Pow(2, 1 / 6.0), Rate);
                    if (Math.Abs(d) > Math.Abs(w)) w = d;
                }
                return w;
            }
            double pinkDefault = WorstDb(pd, d0.Latency), pinkMax = WorstDb(pm, d1.Latency);
            Measure.Report($"Spectral prototype {Modes[mode]}{(mode == 1 ? " " + fir : "")}: +12 dB Q 20 at 3 kHz on pink: peak over the envelope {before:F2} dB -> {after:F2} dB (band 2.85-3.15 kHz {band:F2} dB; worst third octave elsewhere {worstOff:F2} dB); plain pink, worst third octave: default depth 0.5 {pinkDefault:F2} dB, depth 1 {pinkMax:F2} dB");
            Assert.That(after, Is.LessThan(1.0), "suppression");
            Assert.That(Math.Abs(pinkDefault), Is.LessThan(0.5), "plain pink");
        }

        // ── artefacts ─────────────────────────────────────────────────────────

        [TestCase(0, 256)]
        [TestCase(1, 256)]
        [TestCase(1, 512)]
        public void PreEchoBeforeAClick(int mode, int fir)
        {
            const int Rate = 48000, Seconds = 8;
            var pink = Measure.PinkNoise(Rate * Seconds, 0.1, 31);
            var res = Peaking(pink, 3000, 12, 20, Rate);
            const int T0 = Rate * 6;
            var click = (float[])res.Clone();
            click[T0] += 1f;
            double worstDb = 0, energyDb = 0, peakOut = 0;
            var e1 = Make(mode, Rate, depth: 1f, fir: fir);
            var e2 = Make(mode, Rate, depth: 1f, fir: fir);
            var (a, _) = Run(e1, click, click);
            var (b, _) = Run(e2, res, res);
            int lat = e1.Latency, N = e1.N;
            for (int i = T0 + lat - 8; i <= T0 + lat + 8; i++) peakOut = Math.Max(peakOut, Math.Abs(a[i] - b[i]));
            double maxPre = 0, en = 0, enClick = 0;
            for (int i = T0 + lat - N; i < T0 + lat - 16; i++) { double d = a[i] - b[i]; maxPre = Math.Max(maxPre, Math.Abs(d)); en += d * d; }
            for (int i = T0 + lat - 16; i <= T0 + lat + 16; i++) { double d = a[i] - b[i]; enClick += d * d; }
            worstDb = 20 * Math.Log10(maxPre / peakOut);
            energyDb = 10 * Math.Log10(en / enClick);
            // The same measure on a click through plain pink (nothing to suppress): the floor of the method.
            var plain = (float[])pink.Clone();
            plain[T0] += 1f;
            var e3 = Make(mode, Rate, depth: 1f, fir: fir);
            var e4 = Make(mode, Rate, depth: 1f, fir: fir);
            var (c, _) = Run(e3, plain, plain);
            var (d0, _) = Run(e4, pink, pink);
            double pk2 = 0, mp2 = 0;
            for (int i = T0 + lat - 8; i <= T0 + lat + 8; i++) pk2 = Math.Max(pk2, Math.Abs(c[i] - d0[i]));
            for (int i = T0 + lat - N; i < T0 + lat - 16; i++) mp2 = Math.Max(mp2, Math.Abs(c[i] - d0[i]));
            Measure.Report($"Spectral prototype {Modes[mode]}{(mode == 1 ? " " + fir : "")}: click (amplitude 1, pink at 0.1 rms) through a +12 dB Q 20 resonance at depth 1: the output change in the {N} samples before the click is {worstDb:F1} dB at its worst sample and {energyDb:F1} dB in energy against the click's own; on plain pink {20 * Math.Log10(mp2 / pk2):F1} dB");
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

        sealed class FixedMask : NeoBuzz.Dsp.ISpectralProcessor
        {
            readonly float[] g;
            public FixedMask(float[] g) { this.g = g; }
            public void Process(float[] re, float[] im, int n) => NeoBuzz.Dsp.Stft.ApplyGains(re, im, n, g);
        }

        /// <summary>Frame-to-frame change of the short-time spectrum of plain pink noise, in against out: musical noise shows as flux added.</summary>
        [TestCase(0, 0.5f)]
        [TestCase(0, 1f)]
        [TestCase(1, 0.5f)]
        public void SpectralFluxOnNoise(int mode, float depth)
        {
            const int Rate = 48000, Nf = 2048, Hop = 512;
            var pink = Measure.PinkNoise(Rate * 12, 0.1, 51);
            var e = Make(mode, Rate, depth: depth);
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
            double fin = Flux(pink, warm), fout = Flux(o, warm + e.Latency);
            Measure.Report($"Spectral prototype {Modes[mode]}: plain pink, depth {depth}: spectral flux out/in {10 * Math.Log10(fout / fin):F2} dB; {100.0 * e.CellsCut / e.CellsSeen:F2} % of mask cells cut by more than 1 dB");
        }

        // ── CPU ──────────────────────────────────────────────────────────────

        [TestCase(0, 256, 44100)]
        [TestCase(0, 256, 48000)]
        [TestCase(1, 256, 44100)]
        [TestCase(1, 256, 48000)]
        [TestCase(1, 512, 48000)]
        public void CpuOnAResonantSignal(int mode, int fir, int rate)
        {
            int n = rate * 3;
            var pink = Measure.PinkNoise(n, 0.1, 61);
            var l = Peaking(pink, 3000, 12, 20, rate);
            var r = Peaking(Measure.PinkNoise(n, 0.1, 62), 3000, 12, 20, rate);
            const int Block = 512;
            int warm = rate;
            var res = Throughput.Measure($"Spectral prototype {Modes[mode]}{(mode == 1 ? " " + fir : "")} {rate / 1000.0:0.#} kHz, resonant pink, Depth 1", 2, rate, Block, () =>
            {
                var e = Make(mode, rate, depth: 1f, fir: fir);
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
            });
            Measure.Report(res.ToString());
        }

        // ── hygiene ──────────────────────────────────────────────────────────

        [TestCase(0)]
        [TestCase(1)]
        public void BlocksOfAnySizeGiveTheSameBits(int mode)
        {
            const int Rate = 48000;
            var l = Peaking(Measure.PinkNoise(Rate * 2, 0.2, 71), 3000, 12, 20, Rate);
            var r = Measure.PinkNoise(Rate * 2, 0.2, 72);
            float[] first = null;
            foreach (int block in new[] { 512, 117, 16, 5000 })
            {
                var e = Make(mode, Rate, depth: 0.8f);
                e.Mix = 0.7f;
                var (ol, _) = Run(e, l, r, block);
                if (first == null) first = ol;
                else Assert.That(ol, Is.EqualTo(first), $"block {block}");
            }
            Assert.That(Measure.Peak(first, 0, first.Length), Is.LessThan(2));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void NothingIsAllocatedPerBlock(int mode)
        {
            const int Rate = 48000;
            var l = Peaking(Measure.PinkNoise(256 * 4, 0.2, 5), 3000, 12, 20, Rate);
            var r = Measure.PinkNoise(256 * 4, 0.2, 6);
            var e = Make(mode, Rate, depth: 1f);
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                if (b % 100 == 0) { e.Depth = 0.3f + (b / 100 % 7) * 0.1f; e.Mix = b % 300 == 0 ? 0.5f : 1f; e.Configure(); }
                Array.Copy(l, (b % 4) * 256, bl, 0, 256);
                Array.Copy(r, (b % 4) * 256, br, 0, 256);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 60; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 60));
            Assert.That(bytes, Is.EqualTo(0), why);
        }
    }
}
