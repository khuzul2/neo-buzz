using System.Diagnostics;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The shared blocks the mixing and mastering machines build on (docs/specs/mastering-tools.md §4.2): the 90° allpass
    /// pair, the transient/sustain split, the STFT and the partitioned convolution with its stepped design (beyond Neo
    /// Multiband's three FIRs, which MultibandTests pins).
    /// </summary>
    [TestFixture]
    public class MasteringDspTests
    {
        static readonly double[] Rates = { 44100, 48000, 96000 };

        // ── the 90° pair ────────────────────────────────────────────────────

        [Test]
        public void QuadraturePairIsNinetyDegreesApartFrom20HzTo20kHz()
        {
            foreach (double rate in Rates)
            {
                var c = QuadratureAllpass.Coefficients(rate);
                double worst = 0, worstHz = 0, magnitude = 0;
                for (int i = 0; i <= 600; i++)
                {
                    double hz = 20 * Math.Pow(1000, i / 600.0);
                    var (ar, ai, br, bi) = QuadratureAllpass.Response(c, hz, rate);
                    // a / b: its angle is the phase difference.
                    double qr = ar * br + ai * bi, qi = ai * br - ar * bi;
                    double deg = Math.Atan2(qi, qr) * 180 / Math.PI;
                    if (Math.Abs(deg - 90) > worst)
                    {
                        worst = Math.Abs(deg - 90);
                        worstHz = hz;
                    }
                    magnitude = Math.Max(magnitude, Math.Max(Math.Abs(Math.Sqrt(ar * ar + ai * ai) - 1), Math.Abs(Math.Sqrt(br * br + bi * bi) - 1)));
                }
                Measure.Report($"Quadrature pair at {rate / 1000} kHz: {c.Length} coefficients, 90° within ±{worst:F3}° from 20 Hz to 20 kHz (worst at {worstHz:F0} Hz), magnitude 1 within {magnitude:E1}");
                Assert.That(worst, Is.LessThan(0.2), $"{rate} Hz");
                Assert.That(magnitude, Is.LessThan(1e-9));
            }
        }

        [Test]
        public void QuadraturePairGivesAFlatEnvelopeOnSines()
        {
            const int rate = 48000;
            foreach (double hz in new[] { 25.0, 100, 1000, 10000, 19000 })
            {
                var pair = new QuadratureAllpass(rate);
                int n = rate;
                double lo = double.MaxValue, hi = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = 0.5 * Math.Sin(2 * Math.PI * hz * i / rate);
                    pair.Process(x, out double a, out double b);
                    if (i < n / 2) continue;
                    double e = Math.Sqrt(a * a + b * b);
                    lo = Math.Min(lo, e);
                    hi = Math.Max(hi, e);
                }
                double ripple = 20 * Math.Log10(hi / lo);
                Measure.Report($"Quadrature envelope of a {hz} Hz sine: ripple {ripple:F4} dB (level {20 * Math.Log10(hi / 0.5):F4} dB)");
                Assert.That(ripple, Is.LessThan(0.05), $"{hz} Hz");
            }
        }

        // ── the transient split ─────────────────────────────────────────────

        static float[] Hits(int rate, int length, double hz, double decayMs, int every)
        {
            var x = new float[length];
            for (int i = 0; i < length; i++)
            {
                int t = i % every;
                x[i] = (float)(0.5 * Math.Exp(-t / (decayMs / 1000 * rate)) * Math.Sin(2 * Math.PI * hz * t / rate));
            }
            return x;
        }

        [Test]
        public void TransientWeightIsZeroOnSteadySines()
        {
            const int rate = 48000;
            foreach (double hz in new[] { 25.0, 40, 100, 1000, 10000 })
            {
                var split = new TransientSplit(rate);
                var x = Measure.Sine(hz, rate, 0.5, rate);
                var w = new float[x.Length];
                split.Process(x, x, 0, x.Length, w);
                float most = 0;
                for (int i = rate * 3 / 10; i < x.Length; i++) most = Math.Max(most, w[i]);
                Measure.Report($"TransientSplit on a steady {hz} Hz sine after 300 ms: largest weight {most:G3}");
                Assert.That(most, Is.EqualTo(0f), $"{hz} Hz");
            }
        }

        [Test]
        public void TransientWeightMarksOnsetsAndNotDecays()
        {
            const int rate = 48000;
            // A synthetic drum: 200 Hz hits decaying with 60 ms, every 250 ms.
            int every = rate / 4;
            var x = Hits(rate, rate * 2, 200, 60, every);
            var split = new TransientSplit(rate);
            var w = new float[x.Length];
            split.Process(x, x, 0, x.Length, w);
            // The second hit on (the first starts from silence, so its pair has no history; the second is a fair onset).
            int at = every;
            double mean10 = 0, peak5 = 0;
            for (int i = 0; i < rate / 100; i++)
            {
                mean10 += w[at + i];
                if (i < rate / 200) peak5 = Math.Max(peak5, w[at + i]);
            }
            mean10 /= rate / 100;
            double tail = 0;
            for (int i = at + rate * 60 / 1000; i < at + every; i++) tail = Math.Max(tail, w[i]);
            string profile = string.Join(", ", new[] { 1, 2, 3, 5, 10, 20, 40 }.Select(ms => $"{ms} ms {w[at + ms * rate / 1000]:F2}"));
            Measure.Report($"TransientSplit on 200 Hz hits (60 ms decay): weight {profile}; peak in the first 5 ms {peak5:F3}, mean over 10 ms {mean10:F3}; largest from 60 ms on {tail:F3}");
            Assert.That(peak5, Is.GreaterThan(0.75));
            Assert.That(mean10, Is.GreaterThan(0.6));
            Assert.That(tail, Is.EqualTo(0));
        }

        [Test]
        public void TransientUnityGainIsExactlyOne()
        {
            foreach (float t in new[] { 0f, 1e-7f, 0.25f, 0.5f, 0.999f, 1f })
                Assert.That(TransientSplit.Gain(1, 1, t), Is.EqualTo(1.0), $"t = {t}");
            Assert.That(TransientSplit.Gain(2, 1, 1), Is.EqualTo(2.0));
            Assert.That(TransientSplit.Gain(2, 0.5, 0), Is.EqualTo(0.5));
        }

        [Test]
        public void TransientSplitDoesntDependOnBlocks()
        {
            const int rate = 44100;
            var x = Hits(rate, rate * 2, 90, 120, rate / 3);
            var noise = Measure.PinkNoise(x.Length, 0.05);
            var r = new float[x.Length];
            for (int i = 0; i < x.Length; i++) r[i] = 0.5f * x[i] + noise[i];
            float[] Run(int block)
            {
                var split = new TransientSplit(rate);
                var w = new float[x.Length];
                var part = new float[block];
                for (int i = 0; i < x.Length; i += block)
                {
                    int n = Math.Min(block, x.Length - i);
                    split.Process(x, r, i, n, part);
                    Array.Copy(part, 0, w, i, n);
                }
                return w;
            }
            var reference = Run(256);
            foreach (int block in new[] { 128, 117, 16 })
                Assert.That(Run(block), Is.EqualTo(reference), $"blocks of {block}");
        }

        // ── the STFT ────────────────────────────────────────────────────────

        sealed class Unity : ISpectralProcessor
        {
            public void Process(float[] re, float[] im, int n) { }
        }

        sealed class LeftOnly : ISpectralProcessor
        {
            readonly float[] lRe, lIm, rRe, rIm;
            readonly float gain;

            public LeftOnly(int n, float gain)
            {
                lRe = new float[n / 2 + 1];
                lIm = new float[n / 2 + 1];
                rRe = new float[n / 2 + 1];
                rIm = new float[n / 2 + 1];
                this.gain = gain;
            }

            public void Process(float[] re, float[] im, int n)
            {
                Stft.Unpack(re, im, n, lRe, lIm, rRe, rIm);
                for (int k = 0; k <= n / 2; k++)
                {
                    lRe[k] *= gain;
                    lIm[k] *= gain;
                }
                Stft.Pack(lRe, lIm, rRe, rIm, n, re, im);
            }
        }

        static (float[] L, float[] R) Run(Stft stft, float[] l, float[] r, int block, ISpectralProcessor p)
        {
            var ol = (float[])l.Clone();
            var or = (float[])r.Clone();
            for (int i = 0; i < ol.Length; i += block) stft.Process(ol, or, i, Math.Min(block, ol.Length - i), p);
            return (ol, or);
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

        [Test]
        public void StftReconstructsTheInputDelayed()
        {
            foreach (int size in new[] { 512, 2048, 4096 })
            {
                var l = Measure.PinkNoise(48000, 0.2, 7);
                var r = Measure.PinkNoise(48000, 0.2, 99);
                var stft = new Stft(size);
                var (ol, or) = Run(stft, l, r, 256, new Unity());
                double nl = NullDb(l, ol, stft.Latency), nr = NullDb(r, or, stft.Latency);
                Measure.Report($"Stft N = {size}: unit spectrum, the input {stft.Latency} samples back within {nl:F1} dB (left), {nr:F1} dB (right)");
                Assert.That(stft.Latency, Is.EqualTo(size - 1));
                Assert.That(nl, Is.LessThan(-120));
                Assert.That(nr, Is.LessThan(-120));
            }
        }

        [Test]
        public void StftSeparatesTheChannels()
        {
            const int size = 1024;
            var l = Measure.PinkNoise(24000, 0.2, 3);
            var r = Measure.PinkNoise(24000, 0.2, 4);
            var stft = new Stft(size);
            var (ol, or) = Run(stft, l, r, 200, new LeftOnly(size, 0.5f));
            var half = l.Select(v => v * 0.5f).ToArray();
            double nl = NullDb(half, ol, stft.Latency), nr = NullDb(r, or, stft.Latency);
            Measure.Report($"Stft: −6 dB on the left alone (Unpack, Pack): left within {nl:F1} dB of the input halved, right within {nr:F1} dB of the input");
            Assert.That(nl, Is.LessThan(-120));
            Assert.That(nr, Is.LessThan(-120));
        }

        [Test]
        public void StftDoesntDependOnBlocks()
        {
            var l = Measure.PinkNoise(20000, 0.2, 11);
            var r = Measure.Sine(441, 20000, 0.3);
            var gains = new float[1024 / 2 + 1];
            for (int k = 0; k < gains.Length; k++) gains[k] = 0.25f + k % 7 / 8f;
            var p = new Gains(gains);
            var (rl, rr) = Run(new Stft(1024), l, r, 256, p);
            foreach (int block in new[] { 128, 117, 16, 1 })
            {
                var (bl, br) = Run(new Stft(1024), l, r, block, p);
                Assert.That(bl, Is.EqualTo(rl), $"left, blocks of {block}");
                Assert.That(br, Is.EqualTo(rr), $"right, blocks of {block}");
            }
        }

        sealed class Gains(float[] gains) : ISpectralProcessor
        {
            public void Process(float[] re, float[] im, int n) => Stft.ApplyGains(re, im, n, gains);
        }

        // ── the convolution with any number of FIRs ─────────────────────────

        /// <summary>One or two FIRs (a low-pass and its complement) as zero-phase magnitudes on the grid.</summary>
        sealed class Shelf(int filters, double tc) : IFirTarget
        {
            public bool Moved => false;
            public void Latch() { }
            public void TookOver() { }
            public void Begin() { }

            public void At(int k, double t, float[] magnitudes)
            {
                // At Nyquist t is +∞: all high.
                double w = t / tc, w4 = w * w * w * w;
                magnitudes[0] = double.IsInfinity(w4) ? 0f : (float)(1 / (1 + w4));
                if (filters > 1) magnitudes[1] = double.IsInfinity(w4) ? 1f : (float)(w4 / (1 + w4));
            }
        }

        [Test]
        public void PartitionedConvolutionMatchesDirectConvolution()
        {
            const int p = 256, k = 8, d = k * p / 2 - 1;
            foreach (int filters in new[] { 1, 2 })
            {
                var bank = new LinearPhaseFilters(p, k, filters, d, new Shelf(filters, Math.Tan(Math.PI * 1000 / 48000)), 4);
                var l = Measure.PinkNoise(p * 40, 0.2, 5);
                var r = Measure.PinkNoise(p * 40, 0.2, 6);
                var outL = new float[filters][];
                var outR = new float[filters][];
                for (int f = 0; f < filters; f++)
                {
                    outL[f] = new float[p];
                    outR[f] = new float[p];
                }
                var yl = new float[filters][];
                var yr = new float[filters][];
                for (int f = 0; f < filters; f++)
                {
                    yl[f] = new float[l.Length];
                    yr[f] = new float[l.Length];
                }
                var inL = new float[p];
                var inR = new float[p];
                for (int b = 0; b < l.Length / p; b++)
                {
                    Array.Copy(l, b * p, inL, 0, p);
                    Array.Copy(r, b * p, inR, 0, p);
                    bank.Process(inL, inR, filters, outL, outR);
                    for (int f = 0; f < filters; f++)
                    {
                        Array.Copy(outL[f], 0, yl[f], b * p, p);
                        Array.Copy(outR[f], 0, yr[f], b * p, p);
                    }
                }
                double sumTaps = 0;
                for (int f = 0; f < filters; f++)
                {
                    // The taps as designed, scaled by 1/2P for the convolution's unscaled inverse transform: back to unit scale.
                    var taps = bank.Design.Taps(f).Select(t => (double)t * (2.0 * p)).ToArray();
                    sumTaps += taps.Sum();
                    double err = 0, sig = 0;
                    for (int n = taps.Length; n < l.Length; n++)
                    {
                        double el = 0, er = 0;
                        for (int j = 0; j < taps.Length; j++)
                        {
                            el += taps[j] * l[n - j];
                            er += taps[j] * r[n - j];
                        }
                        // Each partition's output is the FIR at its input's own samples (the taps hold the delay D).
                        double dl = yl[f][n] - el, dr = yr[f][n] - er;
                        err += dl * dl + dr * dr;
                        sig += el * el + er * er;
                    }
                    double db = 10 * Math.Log10(err / sig);
                    Measure.Report($"Partitioned convolution, {filters} FIR(s), FIR {f}: against direct convolution with its taps within {db:F1} dB");
                    Assert.That(db, Is.LessThan(-100), $"{filters} FIRs, FIR {f}");
                }
                if (filters == 2) Assert.That(sumTaps, Is.EqualTo(1).Within(1e-4), "the low and high shares sum to a delayed impulse");
            }
        }

        // ── the audio thread ────────────────────────────────────────────────

        [Test]
        public void TheBlocksAllocateNothing()
        {
            const int rate = 48000, block = 256;
            var split = new TransientSplit(rate);
            var stft = new Stft(2048);
            var unity = new Unity();
            var l = Measure.PinkNoise(block * 2100, 0.1, 21);
            var r = Measure.PinkNoise(block * 2100, 0.1, 22);
            var w = new float[block];
            void Block(int b)
            {
                split.Process(l, r, b * block, block, w);
                stft.Process(l, r, b * block, block, unity);
            }
            for (int b = 0; b < 50; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 50));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void StftCpu()
        {
            const int rate = 48000;
            var l = Measure.PinkNoise(rate * 10, 0.1, 31);
            var r = Measure.PinkNoise(rate * 10, 0.1, 32);
            var stft = new Stft(2048);
            var unity = new Unity();
            for (int i = 0; i < rate; i += 256) stft.Process(l, r, i, 256, unity);
            var clock = Stopwatch.StartNew();
            for (int i = rate; i < l.Length; i += 256) stft.Process(l, r, i, Math.Min(256, l.Length - i), unity);
            double factor = (l.Length - rate) / (double)rate / clock.Elapsed.TotalSeconds;
            Measure.Report($"Stft N = 2048, hop 512, stereo at 48 kHz (the transforms alone): {factor:F0}× real time");
            Assert.That(factor, Is.GreaterThan(50));
        }
    }
}
