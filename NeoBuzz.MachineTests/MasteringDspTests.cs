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
            foreach (int block in new[] { 128, 117, 16, 1, 1000 })
                Assert.That(Run(block), Is.EqualTo(reference), $"blocks of {block}");
        }

        [TestCase(48000)]
        [TestCase(96000)]
        public void TransientSplitsBlocksAreItsOneSampleAtATimeBitForBit(int rate)
        {
            // M3 runs the 90° pair a section at a time over a block; against the pair one sample at a time and the followers
            // as documented, with stretches of signal near 1e-35 so the flush every 64 samples matters.
            var x = Hits(rate, rate, 90, 120, rate / 5);
            var noise = Measure.PinkNoise(x.Length, 0.05);
            var r = new float[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                r[i] = 0.5f * x[i] + noise[i];
                if (i % 20000 < 3000)
                {
                    x[i] *= 1e-34f;
                    r[i] = i % 7 == 0 ? 1e-35f : 0f;
                }
            }
            var split = new TransientSplit(rate);
            var got = new float[x.Length];
            var part = new float[300];
            var rng = new Random(3);
            for (int i = 0; i < x.Length;)
            {
                int n = Math.Min(1 + rng.Next(300), x.Length - i);
                split.Process(x, r, i, n, part);
                Array.Copy(part, 0, got, i, n);
                i += n;
            }
            var pl = new QuadratureAllpass(rate);
            var pr = new QuadratureAllpass(rate);
            double fa = Detector.Coef(0.0003, rate), fr = Detector.Coef(0.020, rate), sa = Detector.Coef(0.020, rate), sr = Detector.Coef(0.060, rate);
            double deadband = DspMath.DbToGain(0.5), f = 0, s = 0;
            int flushed = 0;
            for (int i = 0; i < x.Length; i++)
            {
                pl.Process(x[i], out double la, out double lb);
                pr.Process(r[i], out double ra, out double rb);
                double level = Math.Sqrt(Math.Max(la * la + lb * lb, ra * ra + rb * rb));
                f = level + (f - level) * (level > f ? fa : fr);
                s = level + (s - level) * (level > s ? sa : sr);
                double t = f > 1e-12 ? 1 - deadband * s / f : 0;
                float want = t > 0 ? (float)t : 0f;
                Assert.That(BitConverter.SingleToInt32Bits(got[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(want)), $"sample {i}");
                if (((i + 1) & 63) == 0)
                {
                    if (f != DspMath.Flush(f) || s != DspMath.Flush(s)) flushed++;
                    f = DspMath.Flush(f);
                    s = DspMath.Flush(s);
                    pl.Flush();
                    pr.Flush();
                }
            }
            Assert.That(flushed, Is.GreaterThan(0), "the flush mattered");
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

        // ── the faster oversampler (M3) ────────────────────────────────────

        [TestCase(2)]
        [TestCase(4)]
        [TestCase(8)]
        public void TheFastOversamplerIsFirOversamplersFiltersFaster(int factor)
        {
            // The same designs and latency; the sums in another order, so within float rounding of FirOversampler, not its bits.
            var slow = new FirOversampler(256);
            var fast = new FastOversampler(256);
            slow.SetFactor(factor);
            fast.SetFactor(factor);
            Assert.That(fast.Latency, Is.EqualTo(slow.Latency));
            var x = Measure.PinkNoise(256 * 200, 0.3, 41);
            var a = new float[x.Length];
            var b = new float[x.Length];
            var block = new float[256];
            double worst = 0, peak = 0;
            for (int at = 0; at < x.Length; at += 256)
            {
                Array.Copy(x, at, block, 0, 256);
                var hs = slow.Up(block, 0, 256);
                var hf = fast.Up(block, 0, 256);
                for (int i = 0; i < 256 * factor; i++)
                {
                    worst = Math.Max(worst, Math.Abs(hs[i] - hf[i]));
                    // A curve in between, as a clipper's: both see the same.
                    hs[i] = MathF.Max(-0.2f, MathF.Min(0.2f, hs[i]));
                    hf[i] = MathF.Max(-0.2f, MathF.Min(0.2f, hf[i]));
                }
                slow.Down(a, at, 256);
                fast.Down(b, at, 256);
            }
            for (int i = 0; i < x.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
                peak = Math.Max(peak, Math.Abs(a[i]));
            }
            // Speed: up and down, two channels' worth, 10 s at 48 kHz.
            var clock = Stopwatch.StartNew();
            for (int k = 0; k < Measure.Rate * 10 / 256 * 2; k++) { fast.Up(block, 0, 256); fast.Down(block, 0, 256); }
            double fastX = 10 / clock.Elapsed.TotalSeconds;
            clock.Restart();
            for (int k = 0; k < Measure.Rate * 10 / 256 * 2; k++) { slow.Up(block, 0, 256); slow.Down(block, 0, 256); }
            double slowX = 10 / clock.Elapsed.TotalSeconds;
            Measure.Report($"FastOversampler {factor}×: within {Measure.Db(worst / peak):F0} dB of FirOversampler; up and down for two channels {fastX:F0}× real time (FirOversampler {slowX:F0}×)");
            Assert.That(worst / peak, Is.LessThan(1e-5));
            Assert.That(fastX, Is.GreaterThan(slowX));
        }

        // ── Delta and gain match (the house conventions, §2) ───────────────

        /// <summary>A stand-in machine: the input delayed by <paramref name="latency"/> and scaled by <paramref name="gain"/>.</summary>
        static float[] Delayed(float[] x, int latency, float gain)
        {
            var y = new float[x.Length];
            for (int i = latency; i < x.Length; i++) y[i] = gain * x[i - latency];
            return y;
        }

        static (float[] L, float[] R) DeltaRun(float[] l, float[] r, float[] wl, float[] wr, int latency, int block, Func<int, bool?> switchAt)
        {
            var delta = new DeltaListen(1024, 48000) { Latency = latency };
            var ol = (float[])wl.Clone();
            var or = (float[])wr.Clone();
            var bl = new float[block];
            var br = new float[block];
            var dl = new float[block];
            var dr = new float[block];
            for (int at = 0; at < l.Length; at += block)
            {
                int n = Math.Min(block, l.Length - at);
                if (switchAt(at) is bool on) delta.On = on;
                Array.Copy(l, at, dl, 0, n);
                Array.Copy(r, at, dr, 0, n);
                Array.Copy(ol, at, bl, 0, n);
                Array.Copy(or, at, br, 0, n);
                delta.Process(dl, dr, bl, br, n);
                Array.Copy(bl, 0, ol, at, n);
                Array.Copy(br, 0, or, at, n);
            }
            return (ol, or);
        }

        [Test]
        public void DeltaIsWetMinusTheDelayedDryAndOffIsBitForBit()
        {
            const int rate = 48000, latency = 300;
            var l = Measure.PinkNoise(rate, 0.2, 31);
            var r = Measure.PinkNoise(rate, 0.2, 32);
            var wl = Delayed(l, latency, 0.5f);
            var wr = Delayed(r, latency, 0.5f);
            // Off: the wet signal untouched.
            var (offL, offR) = DeltaRun(l, r, wl, wr, latency, 256, _ => null);
            Assert.That(offL, Is.EqualTo(wl));
            Assert.That(offR, Is.EqualTo(wr));
            // On from the start (so no glide inside the signal): wet − dry delayed = −0.5 × the delayed dry.
            var (onL, onR) = DeltaRun(l, r, wl, wr, latency, 256, at => at == 0 ? true : null);
            int glide = (int)Math.Round(DeltaListen.SwitchSeconds * rate);
            double worst = 0;
            for (int i = glide; i < l.Length; i++)
                worst = Math.Max(worst, Math.Max(Math.Abs(onL[i] - (wl[i] - (i >= latency ? l[i - latency] : 0))), Math.Abs(onR[i] - (wr[i] - (i >= latency ? r[i - latency] : 0)))));
            Assert.That(worst, Is.EqualTo(0), "after the glide, exactly wet − dry");
            // A machine that changes nothing: Delta is silence, exactly.
            var (nullL, _) = DeltaRun(l, r, Delayed(l, latency, 1), Delayed(r, latency, 1), latency, 256, at => at == 0 ? true : null);
            Assert.That(nullL.Skip(glide).Max(Math.Abs), Is.EqualTo(0), "an unchanged signal's delta is exactly 0");
            Measure.Report($"Delta: off passes the wet signal bit for bit; on, wet − dry exactly after the {DeltaListen.SwitchSeconds * 1000:0} ms glide, at {latency} samples of latency");
        }

        [Test]
        public void DeltaGlidesAndDoesntDependOnBlocks()
        {
            const int rate = 48000, latency = 77;
            var l = Measure.PinkNoise(rate / 2, 0.2, 33);
            var r = Measure.PinkNoise(rate / 2, 0.2, 34);
            var wl = Delayed(l, latency, 0.7f);
            var wr = Delayed(r, latency, 0.7f);
            // Switch on at sample 4096 and off at 12000, whatever the blocks (the switches land on block starts for each size).
            float[] reference = null;
            foreach (int block in new[] { 1, 16, 117, 256, 1024 })
            {
                var (ol, _) = DeltaRun(l, r, wl, wr, latency, block, at => at <= 4096 && at + block > 4096 ? true : at <= 12000 && at + block > 12000 ? false : null);
                if (block == 1) reference = ol;
                else if (4096 % block == 0 && 12000 % block == 0) Assert.That(ol, Is.EqualTo(reference), $"blocks of {block}");
            }
            // The glide: linear over 10 ms, from wet to wet − dry, no step bigger than the signal's own.
            int glide = (int)Math.Round(DeltaListen.SwitchSeconds * rate);
            double step = 0, own = 0;
            for (int i = 4097; i < 4096 + glide; i++)
            {
                step = Math.Max(step, Math.Abs(reference[i] - reference[i - 1]));
                own = Math.Max(own, Math.Max(Math.Abs(wl[i] - wl[i - 1]), Math.Abs((wl[i] - l[i - latency]) - (wl[i - 1] - l[i - 1 - latency]))));
            }
            Assert.That(step, Is.LessThanOrEqualTo(own * 1.05 + 1e-6), "the glide adds no step");
            Assert.That(reference.Skip(12000 + glide).ToArray(), Is.EqualTo(wl.Skip(12000 + glide).ToArray()), "off again: the wet signal bit for bit");
        }

        [Test]
        public void KWeightingsFourStreamsAreFourOneAtATimeBitForBit()
        {
            // GainMatch's stereo path (M3): four filters side by side against each alone, across blocks, with silence (the 1e-15 snap).
            var rng = new Random(5);
            var x = new float[4][];
            for (int c = 0; c < 4; c++)
            {
                x[c] = Measure.PinkNoise(20000, 0.2, (uint)(11 + c));
                for (int i = 8000; i < 12000; i++) x[c][i] = 0;
            }
            var one = new NeoBuzz.Analysis.Loudness.KWeighting[4];
            var four = new NeoBuzz.Analysis.Loudness.KWeighting[4];
            for (int c = 0; c < 4; c++) one[c] = four[c] = NeoBuzz.Analysis.Loudness.KWeighting.For(44100 + 1000 * c);
            for (int at = 0; at < 20000;)
            {
                int n = Math.Min(1 + rng.Next(700), 20000 - at);
                NeoBuzz.Analysis.Loudness.KWeighting.SumOfSquares4(ref four[0], ref four[1], ref four[2], ref four[3], x[0], x[1], x[2], x[3], at, n,
                    out double s0, out double s1, out double s2, out double s3);
                var got = new[] { s0, s1, s2, s3 };
                for (int c = 0; c < 4; c++)
                    Assert.That(BitConverter.DoubleToInt64Bits(got[c]), Is.EqualTo(BitConverter.DoubleToInt64Bits(one[c].SumOfSquares(x[c], at, n))), $"stream {c} at {at}");
                at += n;
            }
        }

        [Test]
        public void GainMatchReadsOutputMinusInputShortTermLoudness()
        {
            const int rate = 48000, block = 256;
            var l = Measure.PinkNoise(rate * 4, 0.1, 35);
            var r = Measure.PinkNoise(rate * 4, 0.1, 36);
            var match = new NeoBuzz.Analysis.GainMatch(rate);
            var ol = l.Select(v => v * 0.5f).ToArray();
            var or = r.Select(v => v * 0.5f).ToArray();
            double before = double.NaN;
            for (int at = 0; at < l.Length; at += block)
            {
                match.Process(l, r, ol, or, at, Math.Min(block, l.Length - at));
                if (at + block <= rate * 29 / 10) before = match.Db;
            }
            Assert.That(before, Is.NaN, "unknown before 3 s");
            Assert.That(match.Db, Is.EqualTo(20 * Math.Log10(0.5)).Within(1e-6), "half the level reads −6.02 dB");
            Assert.That(match.InputLufs - match.OutputLufs, Is.EqualTo(-match.Db).Within(1e-12));
            Assert.That(NeoBuzz.Analysis.GainMatch.Match(0, match.Db, -24, 24), Is.EqualTo(6.0), "Match sets Output +6.0 dB");
            Assert.That(NeoBuzz.Analysis.GainMatch.Match(20, match.Db, -24, 24), Is.EqualTo(24), "within Output's range");
            Assert.That(NeoBuzz.Analysis.GainMatch.Match(-1.5, double.NaN, -24, 24), Is.EqualTo(-1.5), "unknown: unchanged");

            // A silent input never suggests a jump.
            var silent = new NeoBuzz.Analysis.GainMatch(rate);
            var zero = new float[rate * 4];
            for (int at = 0; at < zero.Length; at += block) silent.Process(zero, zero, l, r, at, block);
            Assert.That(silent.Db, Is.NaN);
            Measure.Report($"Gain match: output − input short-term loudness (K-weighted, 3 s), {match.Db:F4} dB for a −6.0206 dB change; unknown for the first 3 s and on silence");
        }

        // ── the audio thread ────────────────────────────────────────────────

        [Test]
        public void TheBlocksAllocateNothing()
        {
            const int rate = 48000, block = 256;
            var split = new TransientSplit(rate);
            var stft = new Stft(2048);
            var unity = new Unity();
            var delta = new DeltaListen(4096, rate) { Latency = 1000, On = true };
            var match = new NeoBuzz.Analysis.GainMatch(rate);
            var wl = new float[block];
            var wr = new float[block];
            var dl = new float[block];
            var dr = new float[block];
            var l = Measure.PinkNoise(block * 2100, 0.1, 21);
            var r = Measure.PinkNoise(block * 2100, 0.1, 22);
            var w = new float[block];
            void Block(int b)
            {
                split.Process(l, r, b * block, block, w);
                stft.Process(l, r, b * block, block, unity);
                Array.Copy(l, b * block, dl, 0, block);
                Array.Copy(r, b * block, dr, 0, block);
                Array.Copy(dl, wl, block);
                Array.Copy(dr, wr, block);
                delta.Process(dl, dr, wl, wr, block);
                match.Process(dl, dr, wl, wr, 0, block);
            }
            for (int b = 0; b < 50; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 50));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void StftCpu()
        {
            // The transforms alone (N = 2048, hop 512, stereo at 48 kHz): ≥ 50× real time as it was asserted from one stretch of
            // wall clock; now a throughput guard (Throughput.cs; machines.md §4): timed against the reference workload in the
            // same run, 1 s played untimed, then 3 s.
            const int rate = 48000;
            var l = Measure.PinkNoise(rate * 4, 0.1, 31);
            var r = Measure.PinkNoise(rate * 4, 0.1, 32);
            Throughput.Guard("Stft CPU (N = 2048, hop 512, stereo)", Throughput.Floor(x64: 150, arm64: 60), 3, rate, 256, () =>
            {
                var stft = new Stft(2048);
                var unity = new Unity();
                for (int i = 0; i < rate; i += 256) stft.Process(l, r, i, 256, unity);
                return Throughput.InBlocks(256, (pos, n) => stft.Process(l, r, rate + pos, n, unity));
            }, Measure.Report);
        }
    }
}
