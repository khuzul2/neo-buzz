// Synthetic sources for the pitch, onset, trigger and vowel tests (docs/specs/pitch.md §6): sines, band-limited saw and
// pulse trains, a glottal-pulse voice through vowel formant filters with jitter, shimmer, scoops and vibrato, noise,
// Karplus-Strong plucks with slides, and a beatbox kit. Deterministic (an xorshift generator with a seed), so the figures
// the tests print are the same every run. Tests may use the C# maths: only the machines' audio paths must not.

using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    internal sealed class SynthRng
    {
        ulong s;
        public SynthRng(ulong seed) { s = seed * 0x9E3779B97F4A7C15UL + 0x1234567UL; for (int i = 0; i < 4; i++) Next(); }
        public double Next()
        {
            s ^= s << 13; s ^= s >> 7; s ^= s << 17;
            return (s >> 11) * (1.0 / (1UL << 53));
        }
        public double Range(double lo, double hi) => lo + (hi - lo) * Next();
        public double Gauss()
        {
            double u = Math.Max(1e-12, Next()), v = Next();
            return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
        }
    }

    /// <summary>A signal and the truth about it: f0 (Hz, 0 = none) at any time.</summary>
    internal sealed class Source
    {
        public float[] X;
        public double Rate;
        public Func<double, double> F0;
        public string Name;
        public double Seconds => X.Length / Rate;
    }

    internal static class PitchSignals
    {
        public const double Rate = 48000;

        static float[] Zeros(double sr, double sec) => new float[(int)Math.Round(sr * sec)];

        public static float[] Noise(double sr, double sec, double amp, SynthRng rng)
        {
            var x = Zeros(sr, sec);
            for (int i = 0; i < x.Length; i++) x[i] = (float)(amp * rng.Gauss() * 0.5);
            return x;
        }

        public static float[] Pink(double sr, double sec, double amp, SynthRng rng)
        {
            var x = Zeros(sr, sec);
            double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double w = rng.Gauss();
                b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
                b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
                x[i] = (float)(amp * 0.25 * (b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362));
                b6 = w * 0.115926;
            }
            return x;
        }

        /// <summary>A harmonic series with the given amplitudes (index 0 = fundamental), phase continuous through a changing f0.</summary>
        public static Source Harmonic(double sr, Func<double, double> f0, double sec, Func<int, double> amp, string name, double level = 0.4)
        {
            var x = Zeros(sr, sec);
            double phase = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double t = i / sr, f = f0(t);
                phase += f / sr;
                phase -= Math.Floor(phase);
                double s = 0;
                int h = 1;
                while (f * h < 0.45 * sr) { s += amp(h) * Math.Sin(2 * Math.PI * h * phase); h++; if (h > 400) break; }
                x[i] = (float)(level * s);
            }
            Normalise(x, level);
            return new Source { X = x, Rate = sr, F0 = f0, Name = name };
        }

        static void Normalise(float[] x, double peak)
        {
            float m = 0;
            foreach (var v in x) m = Math.Max(m, Math.Abs(v));
            if (m > 0) for (int i = 0; i < x.Length; i++) x[i] = (float)(x[i] * peak / m);
        }

        public static Source Sine(double sr, double hz, double sec, double level = 0.5) =>
            Harmonic(sr, _ => hz, sec, h => h == 1 ? 1 : 0, $"sine {hz:F1} Hz", level);

        public static Source Saw(double sr, double hz, double sec, double level = 0.5) =>
            Harmonic(sr, _ => hz, sec, h => 1.0 / h, $"saw {hz:F1} Hz", level);

        /// <summary>A 10 % pulse train (band-limited): every harmonic about as strong.</summary>
        public static Source Pulse(double sr, double hz, double sec, double level = 0.5, double duty = 0.1) =>
            Harmonic(sr, _ => hz, sec, h => Math.Abs(Math.Sin(Math.PI * h * duty)) / h, $"pulse {hz:F1} Hz", level);

        // Average adult formants (Hz) and bandwidths: aa, oo, ee for a lower and a higher voice.
        public static readonly double[][] VowelsLow =
        {
            new[] { 730.0, 1090, 2440, 80, 90, 120 },
            new[] { 300.0, 870, 2240, 60, 90, 120 },
            new[] { 270.0, 2290, 3010, 60, 100, 120 },
        };
        public static readonly double[][] VowelsHigh =
        {
            new[] { 850.0, 1220, 2810, 90, 100, 130 },
            new[] { 370.0, 950, 2670, 70, 100, 130 },
            new[] { 310.0, 2790, 3310, 70, 110, 130 },
        };

        public sealed class VoiceOptions
        {
            public double Jitter = 0.006, Shimmer = 0.04, Aspiration = 0.01, VibratoCents = 0, VibratoHz = 5.5, VibratoDelay = 0.3;
            public double ScoopCents = 0, ScoopSeconds = 0.08;
            public double Level = 0.5;
            public double HighLift = 0;
            public double[] Formants = VowelsLow[0];
            public ulong Seed = 7;
            public double GlideTo = 0, GlideStart = 0.2, GlideSeconds = 0.3;
        }

        /// <summary>A glottal-pulse voice (Rosenberg C pulse, differentiated) through three formant resonators.</summary>
        public static Source Voice(double sr, double f0Hz, double sec, VoiceOptions o)
        {
            var rng = new SynthRng(o.Seed);
            var x = Zeros(sr, sec);
            Func<double, double> contour = t =>
            {
                double c = 0;
                if (o.ScoopCents != 0) c -= o.ScoopCents * Math.Exp(-t / o.ScoopSeconds * 3);
                if (o.VibratoCents != 0 && t > o.VibratoDelay) c += o.VibratoCents * Math.Sin(2 * Math.PI * o.VibratoHz * (t - o.VibratoDelay));
                double f = f0Hz * Math.Pow(2, c / 1200);
                if (o.GlideTo > 0)
                {
                    double u = Math.Clamp((t - o.GlideStart) / o.GlideSeconds, 0, 1);
                    f *= Math.Pow(o.GlideTo / f0Hz, u);
                }
                return f;
            };
            // Glottal source: phase advances by the contour; each period's length is scaled by (1 + jitter·N(0,1)).
            double ph = 0, scale = 1, amp = 1, prevG = 0;
            var g = new double[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                double f = contour(i / sr) * scale;
                ph += f / sr;
                if (ph >= 1)
                {
                    ph -= 1;
                    scale = 1 + o.Jitter * rng.Gauss();
                    amp = 1 + o.Shimmer * rng.Gauss();
                }
                const double tp = 0.4, tn = 0.16;
                double pulse = ph < tp ? 0.5 * (1 - Math.Cos(Math.PI * ph / tp))
                             : ph < tp + tn ? Math.Cos(Math.PI * (ph - tp) / (2 * tn)) : 0;
                double s = pulse * amp;
                g[i] = s - prevG;   // lip radiation: the derivative
                prevG = s;
            }
            double asp = o.Aspiration;
            for (int i = 0; i < g.Length; i++) g[i] += asp * rng.Gauss() * 0.05;
            // Real voices carry more high-frequency energy than a pulse with this tilt (measured on VocalSet vowels, docs/specs/pitch.md section 5): a first-order lift.
            if (o.HighLift != 0) { double p1 = 0; for (int i = 0; i < g.Length; i++) { double cur = g[i]; g[i] = cur - o.HighLift * p1; p1 = cur; } }
            // The formant cascade.
            double[] y = g;
            for (int k = 0; k < 3; k++)
            {
                double fr = o.Formants[k], bw = o.Formants[3 + k];
                double c = -Math.Exp(-2 * Math.PI * bw / sr), b = 2 * Math.Exp(-Math.PI * bw / sr) * Math.Cos(2 * Math.PI * fr / sr), a = 1 - b - c;
                var z = new double[y.Length];
                double y1 = 0, y2 = 0;
                for (int i = 0; i < y.Length; i++)
                {
                    double v = a * y[i] + b * y1 + c * y2;
                    z[i] = v; y2 = y1; y1 = v;
                }
                y = z;
            }
            // A short fade in and out so the voice starts and ends as a voice does.
            int fade = (int)(0.01 * sr);
            for (int i = 0; i < x.Length; i++)
            {
                double env = Math.Min(1.0, Math.Min(i, x.Length - 1 - i) / (double)fade);
                x[i] = (float)(y[i] * env);
            }
            Normalise(x, o.Level);
            return new Source { X = x, Rate = sr, F0 = contour, Name = $"voice {f0Hz:F0} Hz" };
        }

        /// <summary>
        /// A Karplus-Strong string: noise burst through a one-pole low-pass, then the loop (two-point average, <paramref name="decay"/>
        /// per pass). The delay can change over time (a slide); the pitch is sr / (delay + 0.5).
        /// </summary>
        public static Source Pluck(double sr, Func<double, double> hz, double sec, ulong seed, double decay = 0.997, double level = 0.5)
        {
            var rng = new SynthRng(seed);
            var x = Zeros(sr, sec);
            int cap = 1 << 16;
            var buf = new double[cap];
            double lp = 0;
            double l0 = sr / hz(0) - 0.5;
            int n0 = (int)Math.Ceiling(l0) + 2;
            for (int i = 0; i < n0; i++) { lp += 0.6 * (rng.Gauss() - lp); buf[i] = lp; }
            // Re-scale the burst to unit RMS.
            double e = 0; for (int i = 0; i < n0; i++) e += buf[i] * buf[i];
            double sc = 1 / Math.Sqrt(e / n0 + 1e-12);
            for (int i = 0; i < n0; i++) buf[i] *= sc;
            long w = n0;
            for (int i = 0; i < x.Length; i++)
            {
                double len = sr / hz(i / sr) - 0.5;
                double rp = w - len;
                long ri = (long)Math.Floor(rp);
                double fr = rp - ri;
                double a = buf[(ri) & (cap - 1)] * (1 - fr) + buf[(ri + 1) & (cap - 1)] * fr;
                double b = buf[(ri - 1) & (cap - 1)] * (1 - fr) + buf[(ri) & (cap - 1)] * fr;
                double v = 0.5 * (a + b) * decay;
                buf[w & (cap - 1)] = v;
                w++;
                x[i] = (float)v;
            }
            Normalise(x, level);
            var src = new Source { X = x, Rate = sr, F0 = hz, Name = "pluck" };
            return src;
        }

        public static float[] Silence(double sr, double sec) => Zeros(sr, sec);

        // ── beatbox kit ───────────────────────────────────────────────────

        public static readonly string[] KitNames = { "kick", "snare", "closed hat", "open hat", "k click", "tom", "clap", "rim" };

        internal static void Burst(float[] x, int at, double sr, SynthRng rng, double lo, double hi, double tau, double amp, int len)
        {
            // Band-limited noise: a high-pass and a low-pass one-pole pair.
            double a = Math.Exp(-2 * Math.PI * lo / sr), b = Math.Exp(-2 * Math.PI * hi / sr);
            double lpS = 0, hpS = 0;
            for (int i = 0; i < len && at + i < x.Length; i++)
            {
                double w = rng.Gauss();
                lpS = (1 - b) * w + b * lpS;
                hpS = a * hpS + (1 - a) * lpS;
                double bandp = lpS - hpS;
                x[at + i] += (float)(amp * bandp * Math.Exp(-i / (tau * sr)) * 3);
            }
        }

        internal static void Tone(float[] x, int at, double sr, double f0, double f1, double sweepTau, double decayTau, double amp, int len, SynthRng rng)
        {
            double ph = rng.Next();
            for (int i = 0; i < len && at + i < x.Length; i++)
            {
                double t = i / sr;
                double f = f1 + (f0 - f1) * Math.Exp(-t / sweepTau);
                ph += f / sr;
                x[at + i] += (float)(amp * Math.Sin(2 * Math.PI * ph) * Math.Exp(-t / decayTau));
            }
        }

        /// <summary>One hit of a class into <paramref name="x"/> at <paramref name="at"/>: the random variation is the hit's own (pitch, decay, level, noise).</summary>
        public static void Hit(float[] x, int at, double sr, int cls, SynthRng rng, double level = 0.6)
        {
            double v(double lo, double hi) => rng.Range(lo, hi);
            double amp = level * Math.Pow(10, v(-6, 0) / 20);
            switch (cls)
            {
                case 0: // kick
                    Tone(x, at, sr, v(140, 200), v(45, 62), 0.025, v(0.07, 0.11), amp, (int)(0.3 * sr), rng);
                    Burst(x, at, sr, rng, 200, 2000, 0.003, amp * 0.3, (int)(0.01 * sr));
                    break;
                case 1: // snare
                    Tone(x, at, sr, v(180, 230), v(170, 200), 0.01, 0.05, amp * 0.5, (int)(0.2 * sr), rng);
                    Burst(x, at, sr, rng, v(1200, 2000), v(7000, 10000), v(0.05, 0.1), amp * 0.9, (int)(0.3 * sr));
                    break;
                case 2: // closed hat
                    Burst(x, at, sr, rng, v(5500, 7500), v(14000, 18000), v(0.012, 0.03), amp * 0.8, (int)(0.15 * sr));
                    break;
                case 3: // open hat
                    Burst(x, at, sr, rng, v(5500, 7500), v(14000, 18000), v(0.12, 0.2), amp * 0.8, (int)(0.6 * sr));
                    break;
                case 4: // "k" click
                    Burst(x, at, sr, rng, v(1500, 2200), v(3200, 4500), v(0.004, 0.008), amp * 1.2, (int)(0.04 * sr));
                    break;
                case 5: // tom
                    Tone(x, at, sr, v(130, 160), v(90, 110), 0.04, v(0.12, 0.2), amp, (int)(0.5 * sr), rng);
                    break;
                case 6: // clap
                    for (int k = 0; k < 3; k++) Burst(x, at + (int)(k * v(0.007, 0.011) * sr), sr, rng, v(900, 1300), v(2800, 3800), 0.006, amp * 0.9, (int)(0.03 * sr));
                    Burst(x, at + (int)(0.025 * sr), sr, rng, 1000, 3500, v(0.04, 0.07), amp * 0.5, (int)(0.2 * sr));
                    break;
                default: // rim
                    Tone(x, at, sr, v(1500, 1700), v(1500, 1700), 1, 0.008, amp * 0.8, (int)(0.05 * sr), rng);
                    Tone(x, at, sr, v(750, 850), v(750, 850), 1, 0.01, amp * 0.5, (int)(0.05 * sr), rng);
                    Burst(x, at, sr, rng, 2000, 6000, 0.002, amp * 0.4, (int)(0.01 * sr));
                    break;
            }
        }

        /// <summary>A stream of hits: the classes in order <paramref name="order"/>, spaced between <paramref name="gapLo"/> and <paramref name="gapHi"/> seconds, over a faint room noise.</summary>
        public static (float[] X, List<(long Sample, int Class)> Truth) Kit(double sr, int[] order, double gapLo, double gapHi, ulong seed, double noiseDb = -60)
        {
            var rng = new SynthRng(seed);
            double t = 0.3;
            var at = new List<(long, int)>();
            foreach (int c in order) { at.Add(((long)(t * sr), c)); t += rng.Range(gapLo, gapHi); }
            var x = Zeros(sr, t + 0.8);
            double na = Math.Pow(10, noiseDb / 20);
            for (int i = 0; i < x.Length; i++) x[i] = (float)(na * rng.Gauss());
            foreach (var (s, c) in at) Hit(x, (int)s, sr, c, rng);
            return (x, at);
        }
    }
}
