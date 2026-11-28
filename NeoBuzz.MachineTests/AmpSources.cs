using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Amp's test sources (docs/specs/neo-amp.md §11): a plucked string made here, no third-party audio. A Karplus-Strong loop (Karplus and Strong,
    /// "Digital synthesis of plucked-string and drum timbres", Computer Music Journal 7(2), 1983) with a first-order all-pass in the loop tuned so
    /// that the loop's phase delay at the fundamental is exactly the period (Jaffe and Smith, "Extensions of the Karplus-Strong plucked-string
    /// algorithm", Computer Music Journal 7(2), 1983), then a pickup: a comb for the pickup's place on the string and a one-pole low-pass.
    /// </summary>
    internal static class AmpSources
    {
        public const int Rate = 48000;

        /// <summary>A plucked string at <paramref name="hz"/>, <paramref name="seconds"/> long, peak <paramref name="peak"/>, through a pickup.</summary>
        public static float[] Pluck(double hz, double seconds, double peak = 0.25, uint seed = 1, double t60 = 2.0, double pickupPos = 0.2, double rate = Rate)
        {
            int total = (int)(seconds * rate);
            double period = rate / hz;
            // The loop: delay N, the averaging low-pass (0.5 + 0.5 z^-1, phase delay depends on the frequency), the tuning all-pass (η + z^-1) / (1 + η z^-1).
            // Find η so that the loop's phase delay at hz equals the period.
            int n = (int)Math.Floor(period - 1.5);
            if (n < 2) n = 2;
            double w = 2 * Math.PI * hz / rate;
            double Delay(double eta)
            {
                // Phase of 0.5 (1 + e^-jw)
                double aRe = 0.5 * (1 + Math.Cos(w)), aIm = -0.5 * Math.Sin(w);
                double phA = Math.Atan2(aIm, aRe);
                // all-pass (η + e^-jw) / (1 + η e^-jw)
                double nRe = eta + Math.Cos(w), nIm = -Math.Sin(w), dRe = 1 + eta * Math.Cos(w), dIm = -eta * Math.Sin(w);
                double phP = Math.Atan2(nIm, nRe) - Math.Atan2(dIm, dRe);
                double ph = phA + phP - n * w;   // total phase of the loop (negative)
                return -ph / w;
            }
            double lo = -0.95, hi = 0.95;
            // Delay(η) falls as η rises: bisect.
            for (int it = 0; it < 80; it++)
            {
                double mid = 0.5 * (lo + hi);
                if (Delay(mid) > period) lo = mid; else hi = mid;
            }
            double eta = 0.5 * (lo + hi);
            // Loop gain from the T60 at the fundamental.
            double g = Math.Pow(10, -3.0 * period / (t60 * rate));
            var line = new double[n + 2];
            var rng = new uint[] { seed * 2654435761u + 12345u };
            for (int i = 0; i < line.Length; i++)
            {
                rng[0] = rng[0] * 1664525u + 1013904223u;
                line[i] = ((rng[0] >> 8) / 16777216.0 * 2 - 1);
            }
            var s = new double[total];
            int p = 0;
            double xPrev = 0, apIn = 0, apOut = 0;
            for (int i = 0; i < total; i++)
            {
                double outv = line[p];
                s[i] = outv;
                // low-pass (average of this and the previous sample out of the line)
                double lp = 0.5 * (outv + xPrev);
                xPrev = outv;
                // all-pass
                double ap = eta * lp + apIn - eta * apOut;
                apIn = lp; apOut = ap;
                line[p] = g * ap;
                p++; if (p >= n) p = 0;
            }
            // The pickup: a comb for the place (a fraction of the string), then a one-pole low-pass.
            int k = Math.Max(1, (int)Math.Round(pickupPos * period));
            var y = new float[total];
            double lpState = 0, lpA = 1 - Math.Exp(-2 * Math.PI * 5000 / rate);
            double max = 0;
            for (int i = 0; i < total; i++)
            {
                double c = s[i] - (i >= k ? s[i - k] : 0);
                lpState += lpA * (c - lpState);
                y[i] = (float)lpState;
                max = Math.Max(max, Math.Abs(lpState));
            }
            // Peak over the first 100 ms is the level asked for.
            double pk = 0;
            for (int i = 0; i < Math.Min(total, (int)(0.1 * rate)); i++) pk = Math.Max(pk, Math.Abs(y[i]));
            if (pk < 1e-9) pk = max;
            float scale = (float)(peak / Math.Max(pk, 1e-9));
            for (int i = 0; i < total; i++) y[i] *= scale;
            return y;
        }

        /// <summary>Adds a note into a mix at a sample position.</summary>
        public static void Mix(float[] dst, float[] note, int at, float gain = 1f)
        {
            for (int i = 0; i < note.Length && at + i < dst.Length; i++) dst[at + i] += note[i] * gain;
        }

        /// <summary>The source each factory preset is judged on (the listening pack's own): chords, a riff or a bass line, peaks at -12 dBFS.</summary>
        public static float[] ForPreset(string preset, double seconds)
        {
            if (preset.StartsWith("Bass")) return Bass(seconds);
            if (preset.StartsWith("Clean") || preset == "Crunch — Edge of Breakup") return Chords(seconds);
            return Riff(seconds);
        }

        /// <summary>The loudest 400 ms RMS of a render, in dBFS (windows every 50 ms).</summary>
        public static double Loudest400Db(float[] y)
        {
            int win = (int)(0.4 * Rate), hop = (int)(0.05 * Rate);
            double best = 0;
            for (int at = 0; at + win <= y.Length; at += hop)
            {
                double a = 0;
                for (int i = at; i < at + win; i++) a += y[i] * (double)y[i];
                best = Math.Max(best, a / win);
            }
            return 10 * Math.Log10(best + 1e-24);
        }

        static double Midi(int m) => 440.0 * Math.Pow(2.0, (m - 69) / 12.0);

        /// <summary>A guitar line of chords (open power chords and triads with a short strum) of the given length; peaks about −12 dBFS.</summary>
        public static float[] Chords(double seconds, double peak = 0.25)
        {
            int total = (int)(seconds * Rate);
            var x = new float[total];
            int[][] chords =
            {
                new[] { 40, 47, 52, 56, 59, 64 },   // E major
                new[] { 45, 52, 57, 61, 64 },       // A major
                new[] { 38, 45, 50, 54 },           // D major
                new[] { 43, 47, 50, 55, 59, 67 },   // G major
            };
            double bar = seconds / 8.0;
            for (int b = 0; b < 8; b++)
            {
                var ch = chords[b % chords.Length];
                for (int s = 0; s < ch.Length; s++)
                {
                    double at = b * bar + s * 0.012;
                    var n = Pluck(Midi(ch[s]), bar * 1.1 + 0.4, 0.5, (uint)(b * 10 + s + 3), 1.6, 0.18 + 0.02 * s);
                    Mix(x, n, (int)(at * Rate), 1f);
                }
            }
            Normalise(x, peak);
            return x;
        }

        /// <summary>A single-note riff (palm-muted eighths and a few long notes) on the low strings; peaks about −12 dBFS.</summary>
        public static float[] Riff(double seconds, double peak = 0.25)
        {
            int total = (int)(seconds * Rate);
            var x = new float[total];
            int[] notes = { 40, 40, 43, 40, 45, 40, 47, 46, 40, 40, 43, 45, 47, 45, 43, 40 };
            double step = seconds / (notes.Length * 2);
            for (int i = 0; i < notes.Length * 2; i++)
            {
                int m = notes[i % notes.Length] + (i >= notes.Length ? 12 : 0);
                var n = Pluck(Midi(m), 0.6, 0.5, (uint)(100 + i), i % 4 == 0 ? 1.8 : 0.7, 0.16);
                Mix(x, n, (int)(i * step * Rate));
            }
            Normalise(x, peak);
            return x;
        }

        /// <summary>A bass line from the same model, an octave and more lower; peaks about −12 dBFS.</summary>
        public static float[] Bass(double seconds, double peak = 0.25)
        {
            int total = (int)(seconds * Rate);
            var x = new float[total];
            int[] notes = { 28, 28, 35, 28, 33, 28, 31, 30, 28, 28, 35, 33, 31, 33, 35, 28 };
            double step = seconds / (notes.Length);
            for (int i = 0; i < notes.Length; i++)
            {
                var n = Pluck(Midi(notes[i]), step * 1.4, 0.5, (uint)(200 + i), 1.5, 0.22, Rate);
                Mix(x, n, (int)(i * step * Rate));
            }
            Normalise(x, peak);
            return x;
        }

        static void Normalise(float[] x, double peak)
        {
            double pk = 0;
            foreach (float v in x) pk = Math.Max(pk, Math.Abs(v));
            if (pk < 1e-9) return;
            float g = (float)(peak / pk);
            for (int i = 0; i < x.Length; i++) x[i] *= g;
        }
    }
}
