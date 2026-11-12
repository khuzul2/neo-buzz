using NeoBuzz.Dsp;
using NeoBuzz.Dither;
using Dsp = NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Dither (docs/specs/mastering-tools.md §5.9): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class DitherTests
    {
        const int Rate = Measure.Rate;

        static (DitherEngine Engine, ParamValues Values) Make(int bits, int dither, int shaping, bool blank = true)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Bits, Params.Table[P.Bits].Raw(bits));
            v.Set(P.Dither, dither);
            v.Set(P.Shaping, shaping);
            v.Set(P.Blank, blank ? 1 : 0);
            var e = new DitherEngine(v);
            e.SetSampleRate(Rate);
            return (e, v);
        }

        static (float[] L, float[] R) Run(DitherEngine e, float[] l, float[] r, int block = DitherEngine.MaxBlock)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[DitherEngine.MaxBlock];
            var br = new float[DitherEngine.MaxBlock];
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

        // ---- grid --------------------------------------------------------------------------------

        [TestCase(8)]
        [TestCase(16)]
        [TestCase(20)]
        [TestCase(24)]
        public void EverySampleIsOnTheGrid(int bits)
        {
            var x = Measure.PinkNoise(Rate * 2, 0.3, 11);
            for (int i = 0; i < 2000; i++) x[i * 7] = (i % 2 == 0) ? 1.0f : -1.0f;   // full scale and beyond the top code
            double scale = Math.Pow(2, bits - 1);
            foreach (int shaping in new[] { 0, 1, 2 })
            {
                var (e, _) = Make(bits, 1, shaping);
                var (ol, or) = Run(e, x, (float[])x.Clone());
                foreach (var ch in new[] { ol, or })
                    for (int i = 0; i < ch.Length; i++)
                    {
                        double k = ch[i] * scale;
                        if (k != Math.Round(k) || k < -scale || k > scale - 1) Assert.Fail($"{bits} bits shaping {shaping}: sample {i} = {ch[i]} (code {k}) is off the grid");
                    }
            }
        }

        // ---- linearity ---------------------------------------------------------------------------

        [Test]
        public void AQuietSineHasNoHarmonicsAboveTheNoise()
        {
            const int bin = 1367;   // about 1 kHz, coherent
            var tone = Measure.Sine(Measure.BinFrequency(bin), Measure.N + 5000, Math.Pow(10, -90.0 / 20));
            double Worst(int dither, out double floorDb)
            {
                var (e, _) = Make(16, dither, 0);
                var (ol, _) = Run(e, tone, (float[])tone.Clone());
                var mag = Measure.Spectrum(ol, 5000);
                // the noise level around the harmonics: the mean power of the bins that aren't harmonics, 20 Hz - 20 kHz
                double sum = 0; int count = 0;
                for (int k = (int)(20.0 * Measure.N / Rate); k < 20000.0 * Measure.N / Rate; k++)
                    if (k % bin != 0 && Math.Abs(k - bin) > 3) { sum += mag[k] * mag[k]; count++; }
                double noise = Math.Sqrt(sum / count);
                floorDb = 20 * Math.Log10(noise / mag[bin]);
                double worst = 0;
                for (int h = 2; h <= 14 && bin * h < mag.Length; h++) worst = Math.Max(worst, mag[bin * h]);
                return 20 * Math.Log10(worst / noise);
            }
            double withD = Worst(1, out double floorD), without = Worst(0, out _);
            TestContext.Out.WriteLine($"-90 dBFS 1 kHz at 16 bits: strongest harmonic {withD:+0.0;-0.0} dB over the noise with TPDF ({floorD:0.0} dB re the tone per bin), {without:+0.0;-0.0} dB without dither");
            Assert.That(withD, Is.LessThan(10));
            Assert.That(without, Is.GreaterThan(15), "the test would not see a distortion");
        }

        // ---- noise -------------------------------------------------------------------------------

        [TestCase(16)]
        [TestCase(20)]
        public void TpdfNoisePowerIsAQuarterOfAnLsbSquared(int bits)
        {
            var x = Measure.PinkNoise(Rate * 6, 0.1, 3);
            var (e, _) = Make(bits, 1, 0);
            var (ol, _) = Run(e, x, (float[])x.Clone());
            double lsb = 1.0 / Math.Pow(2, bits - 1), p = 0;
            int n = 0;
            for (int i = Rate; i < x.Length; i++) { double d = (ol[i] - (double)x[i]) / lsb; p += d * d; n++; }
            p /= n;
            double db = 10 * Math.Log10(p / 0.25);
            TestContext.Out.WriteLine($"{bits} bits: noise power {p:0.0000} LSB² ({db:+0.00;-0.00} dB against 1/4)");
            Assert.That(Math.Abs(db), Is.LessThan(0.2));
        }

        [TestCase(1)]
        [TestCase(2)]
        public void ShapedNoiseFollowsTheDesign(int shaping)
        {
            var x = Measure.PinkNoise(Rate * 20, 0.1, 5);
            var (e, _) = Make(16, 1, shaping);
            var (ol, _) = Run(e, x, (float[])x.Clone());
            double lsb = 1.0 / 32768;
            const int N = 8192;
            var avg = new double[N / 2];
            int frames = 0;
            var buf = new float[N];
            for (int pos = Rate; pos + N <= x.Length; pos += N)
            {
                for (int i = 0; i < N; i++) buf[i] = (float)((ol[pos + i] - (double)x[pos + i]) / lsb);
                var m = Fft.Magnitudes(buf, 0, N);   // amplitude, a full-scale sine reads 1 (rectangular window)
                for (int k = 1; k < N / 2; k++) avg[k] += m[k] * m[k];
                frames++;
            }
            var b = Dsp.Dither.Shapes[shaping];
            double worst = 0;
            // octave-ish bands, each at least 64 bins
            double[] edges = { 100, 200, 400, 800, 1600, 3200, 6400, 12800, 20000 };
            for (int band = 0; band + 1 < edges.Length; band++)
            {
                int lo = (int)(edges[band] * N / Rate), hi = (int)(edges[band + 1] * N / Rate);
                double meas = 0, des = 0;
                for (int k = lo; k < hi; k++)
                {
                    meas += avg[k] / frames;
                    double w = 2 * Math.PI * k / N, re = 1, im = 0;
                    for (int j = 0; j < b.Length; j++) { re += b[j] * Math.Cos(w * (j + 1)); im -= b[j] * Math.Sin(w * (j + 1)); }
                    des += re * re + im * im;
                }
                double diff = 10 * Math.Log10(meas / des);
                // meas/des is a constant (the white noise's bin power); compare the spread
                if (band == 0) lastRef = diff;
                worst = Math.Max(worst, Math.Abs(diff - lastRef));
                TestContext.Out.WriteLine($"shaping {shaping}: {edges[band]:0}-{edges[band + 1]:0} Hz: measured against design {diff - lastRef:+0.00;-0.00} dB");
            }
            Assert.That(worst, Is.LessThan(1.0));
        }

        double lastRef;

        // ---- silence, determinism, blocks --------------------------------------------------------

        [Test]
        public void ItBlanksAfterSeventyMillisecondsTimesTenOfSilence()
        {
            var x = new float[Rate * 3];
            var s = Measure.PinkNoise(Rate, 0.2, 2);
            Array.Copy(s, x, Rate);
            var (e, _) = Make(16, 1, 0);
            var (ol, or) = Run(e, x, (float[])x.Clone());
            int blankAt = Rate + (int)Math.Round(0.7 * Rate);
            bool noiseBefore = false;
            for (int i = Rate + 100; i < blankAt - 100; i++) if (ol[i] != 0) noiseBefore = true;
            Assert.That(noiseBefore, Is.True, "dither on the silence until 0.7 s");
            for (int i = blankAt; i < x.Length; i++) if (ol[i] != 0 || or[i] != 0) Assert.Fail($"not silent at {i}");
            // the sound comes back at its first sample
            var y = new float[Rate];
            y[500] = 0.25f;
            var (o2, _) = Run(e, y, (float[])y.Clone());
            Assert.That(o2[500], Is.Not.EqualTo(0f));
        }

        [Test]
        public void RendersRepeatBitForBit()
        {
            var x = Measure.PinkNoise(Rate, 0.1, 8);
            var (e, _) = Make(16, 1, 2);
            var (a, _) = Run(e, x, (float[])x.Clone());
            e.Reset();
            var (b, _) = Run(e, x, (float[])x.Clone(), 117);
            Assert.That(b, Is.EqualTo(a));
        }

        [Test]
        public void NothingIsAllocatedPerBlock()
        {
            var x = Measure.PinkNoise(Rate * 5, 0.1, 4);
            var (e, v) = Make(16, 1, 2);
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                int pos = (b % 900) * 256;
                Array.Copy(x, pos, bl, 0, 256);
                Array.Copy(x, pos, br, 0, 256);
                if (b % 7 == 0) v.Set(P.Shaping, b % 3);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 60; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 60));
            Assert.That(bytes, Is.EqualTo(0), why);
        }
    }
}
