using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>Measurement helpers: coherent spectra, alias floors, levels, decay times.</summary>
    internal static class Measure
    {
        public const int Rate = 48000;
        public const int N = 65536;

        /// <summary>
        /// A frequency that falls exactly on FFT bin <paramref name="bin"/> of an N-point
        /// transform at <see cref="Rate"/> (coherent: no window needed). Odd bins keep aliases
        /// off the harmonic bins.
        /// </summary>
        public static double BinFrequency(int bin, int n = N, int rate = Rate) => (double)bin * rate / n;

        /// <summary>Amplitude spectrum (a full-scale sine reads 1).</summary>
        public static double[] Spectrum(float[] x, int offset = 0, int n = N) => Fft.Magnitudes(x, offset, n);

        /// <summary>
        /// The loudest bin that is not a harmonic of <paramref name="fundamentalBin"/> (nor DC),
        /// between <paramref name="loHz"/> and <paramref name="hiHz"/>, in dB relative to the
        /// fundamental.
        /// </summary>
        public static double AliasFloorDb(double[] mag, int fundamentalBin, double loHz = 20, double hiHz = 20000, int n = N, int rate = Rate)
        {
            double fund = mag[fundamentalBin];
            int lo = Math.Max(1, (int)Math.Ceiling(loHz * n / rate));
            int hi = Math.Min(mag.Length - 1, (int)Math.Floor(hiHz * n / rate));
            double worst = 1e-30;
            for (int k = lo; k <= hi; k++)
            {
                if (k % fundamentalBin == 0) continue;
                if (mag[k] > worst) worst = mag[k];
            }
            return 20 * Math.Log10(worst / fund);
        }

        /// <summary>Level of harmonic <paramref name="h"/> relative to the fundamental, dB.</summary>
        public static double HarmonicDb(double[] mag, int fundamentalBin, int h) =>
            20 * Math.Log10(Math.Max(1e-30, mag[fundamentalBin * h]) / mag[fundamentalBin]);

        /// <summary>THD: harmonics 2 … up to 20 kHz relative to the fundamental, as a ratio.</summary>
        public static double Thd(double[] mag, int fundamentalBin, int n = N, int rate = Rate)
        {
            double sum = 0;
            for (int k = 2 * fundamentalBin; k < mag.Length && k * (double)rate / n <= 20000; k += fundamentalBin)
                sum += mag[k] * mag[k];
            return Math.Sqrt(sum) / mag[fundamentalBin];
        }

        public static float[] Sine(double hz, int length, double amplitude = 1, int rate = Rate)
        {
            var x = new float[length];
            for (int i = 0; i < length; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
            return x;
        }

        public static double Rms(float[] x, int offset, int n)
        {
            double s = 0;
            for (int i = 0; i < n; i++) s += (double)x[offset + i] * x[offset + i];
            return Math.Sqrt(s / Math.Max(1, n));
        }

        public static double Peak(float[] x, int offset, int n)
        {
            double p = 0;
            for (int i = 0; i < n; i++) p = Math.Max(p, Math.Abs(x[offset + i]));
            return p;
        }

        public static double Db(double ratio) => 20 * Math.Log10(Math.Max(1e-30, ratio));

        /// <summary>Frequency of the strongest bin (with parabolic interpolation), Hz.</summary>
        public static double PeakFrequency(float[] x, int offset, int n, int rate = Rate)
        {
            // Hann window for a clean peak.
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = x[offset + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Fft.Magnitudes(w, 0, n);
            int best = 1;
            for (int k = 2; k < mag.Length - 1; k++) if (mag[k] > mag[best]) best = k;
            double a = Math.Log(mag[best - 1] + 1e-30), b = Math.Log(mag[best] + 1e-30), c = Math.Log(mag[best + 1] + 1e-30);
            double delta = 0.5 * (a - c) / (a - 2 * b + c);
            return (best + delta) * rate / n;
        }

        /// <summary>Cents between two frequencies.</summary>
        public static double Cents(double f, double reference) => 1200 * Math.Log2(f / reference);

        /// <summary>
        /// Records a measured figure (for the spec): appended to machine-measurements.txt in
        /// MEASUREMENTS_DIR (CI shows it), else ignored.
        /// </summary>
        public static void Report(string what)
        {
            TestContext.Out.WriteLine(what);
            var dir = Environment.GetEnvironmentVariable("MEASUREMENTS_DIR");
            if (string.IsNullOrEmpty(dir)) return;
            lock (typeof(Measure))
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "machine-measurements.txt"), what + Environment.NewLine);
            }
        }

        /// <summary>Deterministic pink-ish test noise.</summary>
        public static float[] PinkNoise(int length, double rmsTarget = 0.1, uint seed = 12345)
        {
            var rng = new Rng(seed);
            var pink = new PinkNoise();
            var x = new float[length];
            for (int i = 0; i < length; i++) x[i] = pink.Next(rng.NextBipolar());
            double rms = Rms(x, 0, length);
            float g = (float)(rmsTarget / rms);
            for (int i = 0; i < length; i++) x[i] *= g;
            return x;
        }

        /// <summary>
        /// What a second of silence after noise costs against a second of noise (the hygiene tests' "runs at full speed"),
        /// timed as the throughput guards are (Throughput.CostGuard, machines.md §4) and asserted under
        /// <paramref name="limit"/>: each render starts with <paramref name="noise"/> played untimed through a machine made
        /// anew, then times the same noise again (the baseline) or <paramref name="silenceSeconds"/> of silence (the test).
        /// <paramref name="make"/> makes the machine, set up, and returns its Process(left, right, count) on blocks of
        /// <paramref name="block"/> samples. A flush that stopped working (feedback decaying into subnormal numbers) makes
        /// silence cost many times what noise does on x64.
        /// </summary>
        public static Throughput.Cost SilenceCost(string what, double limit, int block, Func<Action<float[], float[], int>> make, float[] noise, int silenceSeconds = 20)
        {
            var silent = new float[silenceSeconds * Rate];
            Func<Func<int, Action<int>>> Start(float[] timed) => () =>
            {
                var process = make();
                var bl = new float[block];
                var br = new float[block];
                void Block(float[] x, int pos, int n)
                {
                    int have = Math.Clamp(x.Length - pos, 0, n);
                    Array.Copy(x, pos, bl, 0, have);
                    Array.Copy(x, pos, br, 0, have);
                    Array.Clear(bl, have, n - have);
                    Array.Clear(br, have, n - have);
                    process(bl, br, n);
                }
                for (int pos = 0; pos < noise.Length; pos += block) Block(noise, pos, Math.Min(block, noise.Length - pos));
                return Throughput.InBlocks(block, (pos, n) => Block(timed, pos, n));
            };
            return Throughput.CostGuard(what, limit, Rate, block, noise.Length / (double)Rate, Start(noise), silenceSeconds, Start(silent), Report);
        }
    }
}
