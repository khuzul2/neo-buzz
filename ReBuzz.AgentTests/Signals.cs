namespace ReBuzz.AgentTests
{
    /// <summary>Test signals for the analysis library, generated here (docs/specs/mcp-server.md §7).</summary>
    static class Signals
    {
        public const int Rate = 48000;

        public static float[] Sine(double hz, double amplitude, double seconds, int rate = Rate, double phase = 0)
        {
            var x = new float[(int)(seconds * rate)];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate + phase));
            return x;
        }

        /// <summary>Gaussian white noise at an RMS level, seeded.</summary>
        public static float[] Noise(double rms, double seconds, int seed, int rate = Rate)
        {
            var random = new Random(seed);
            var x = new float[(int)(seconds * rate)];
            for (int i = 0; i < x.Length; i++)
            {
                double u = 1 - random.NextDouble(), v = random.NextDouble();
                x[i] = (float)(rms * Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v));
            }
            return x;
        }

        /// <summary>
        /// Periodic noise (a multisine with a line on every bin of a <paramref name="period"/>-sample
        /// period) whose power per bin falls as f^<paramref name="slope"/>: −1 is pink, 0 white.
        /// Each bin's phase is two bins below's ± 90° at random: noise-like in time, and the Hann
        /// window's cross terms between bins cancel, so a Welch average over whole half-periods
        /// reads the exact spectrum, with no estimation noise even three bins wide.
        /// </summary>
        public static float[] PeriodicNoise(double slope, double rms, double periods, int period = 32768, int seed = 5)
        {
            var random = new Random(seed);
            var re = new double[period];
            var im = new double[period];
            var phases = new double[period / 2];
            for (int k = 1; k < period / 2; k++)
            {
                phases[k] = k <= 2 ? 2 * Math.PI * random.NextDouble() : phases[k - 2] + (random.Next(2) == 0 ? Math.PI / 2 : -Math.PI / 2);
                double a = Math.Pow(k, slope / 2), phase = phases[k];
                re[k] = a * Math.Cos(phase);
                im[k] = a * Math.Sin(phase);
                re[period - k] = re[k];
                im[period - k] = -im[k];
            }
            new NeoBuzz.Analysis.Fft(period).Transform(re, im, inverse: true);
            double sum = 0;
            for (int i = 0; i < period; i++) sum += re[i] * re[i];
            double scale = rms / Math.Sqrt(sum / period);
            var x = new float[(int)(periods * period)];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(re[i % period] * scale);
            return x;
        }

        /// <summary>A band-limited sawtooth note with a short attack and release, as a subtractive synth plays it.</summary>
        public static void AddNote(float[] x, double midi, double start, double seconds, double amplitude = 0.3, double detuneCents = 0, int rate = Rate, int maxHarmonics = 40)
        {
            double hz = 440 * Math.Pow(2, (midi - 69 + detuneCents / 100) / 12);
            int harmonics = Math.Min(maxHarmonics, (int)(0.45 * rate / hz));
            int from = (int)(start * rate), count = (int)(seconds * rate);
            double attack = 0.005 * rate, release = 0.02 * rate;
            for (int i = 0; i < count && from + i < x.Length; i++)
            {
                double env = Math.Min(1, Math.Min(i / attack, (count - i) / release));
                double t = i / (double)rate, s = 0;
                for (int h = 1; h <= harmonics; h++) s += Math.Sin(2 * Math.PI * h * hz * t) / h;
                x[from + i] += (float)(amplitude * env * s * 2 / Math.PI);
            }
        }

        /// <summary>Clicks (a 5 ms decaying 1 kHz burst) on every beat at <paramref name="bpm"/>.</summary>
        public static float[] Clicks(double bpm, int beats, int rate = Rate, double offset = 0)
        {
            int perBeat = (int)Math.Round(rate * 60 / bpm);
            int first = (int)(offset * rate);
            var x = new float[first + perBeat * beats];
            for (int b = 0; b < beats; b++)
                for (int i = 0; i < rate / 50; i++)
                    x[first + b * perBeat + i] = (float)(0.8 * Math.Sin(2 * Math.PI * 1000 * i / rate) * Math.Exp(-i / (0.005 * rate)));
            return x;
        }

        /// <summary>A kick-like thump every <paramref name="every"/> seconds: 55 Hz with a falling pitch at the start and a 120 ms decay.</summary>
        public static float[] Kick(double seconds, double every, double amplitude)
        {
            var x = new float[(int)(seconds * Rate)];
            for (double t = 0; t < seconds - 1e-9; t += every)
            {
                int at = (int)(t * Rate);
                for (int i = 0; i < 0.25 * Rate && at + i < x.Length; i++)
                {
                    double time = i / (double)Rate, env = Math.Exp(-time / 0.12);
                    x[at + i] = (float)(amplitude * env * Math.Sin(2 * Math.PI * (55 * time + 1.8 * (1 - Math.Exp(-time / 0.03)))));
                }
            }
            return x;
        }

        public static float[] Mix(params float[][] parts)
        {
            var x = new float[parts.Max(p => p.Length)];
            foreach (var p in parts)
                for (int i = 0; i < p.Length; i++) x[i] += p[i];
            return x;
        }

        public static float[] Scale(float[] x, double gain) => x.Select(v => (float)(v * gain)).ToArray();

        /// <summary>Silence before and after: <paramref name="x"/> placed at <paramref name="at"/> seconds in <paramref name="seconds"/>.</summary>
        public static float[] Place(float[] x, double at, double seconds, int rate = Rate)
        {
            var y = new float[(int)(seconds * rate)];
            int from = (int)(at * rate);
            for (int i = 0; i < x.Length && from + i < y.Length; i++) y[from + i] = x[i];
            return y;
        }
    }
}
