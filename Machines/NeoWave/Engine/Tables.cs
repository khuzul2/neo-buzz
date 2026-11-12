#nullable disable
// Neo Wave (docs/specs/machines.md §5): the built-in wavetables, generated (no binary assets), 64
// frames each, built once per process on a background thread when first wanted.

using System;
using System.Threading;
using System.Threading.Tasks;
using NeoBuzz.Dsp;

namespace NeoBuzz.Wave
{
    internal static class Tables
    {
        public const int Frames = 64;
        public static readonly string[] Names = { "Basic", "PWM", "Harmonics", "Formants", "Sync sweep", "FM sweep", "Organ", "Resonant", "Digital", "Grit" };
        static readonly Wavetable[] built = new Wavetable[Names.Length];
        static readonly int[] requested = new int[Names.Length];

        /// <summary>
        /// The table if built; otherwise null, and a build is started. Called by every voice's block (the sub oscillator's
        /// table): the build's lambda is in a method of its own, since a lambda that captures a parameter has C# make its
        /// closure as the method starts, whether or not it runs (24 bytes a call, a voice's every block: Neo Wave's first
        /// play allocated about 220 KB on the audio thread).
        /// </summary>
        public static Wavetable Get(int index)
        {
            if (index < 0 || index >= Names.Length) return null;
            var t = Volatile.Read(ref built[index]);
            if (t != null) return t;
            if (Interlocked.Exchange(ref requested[index], 1) == 0) Build(index);
            return null;
        }

        static void Build(int index) => Task.Run(() => Volatile.Write(ref built[index], Make(index)));

        /// <summary>
        /// Whether every built-in table is built (asking for those nobody has asked for). Any thread.
        /// What an offline render waits for: then whatever table the parameters choose, once the
        /// engine applies them, is there from the first block.
        /// </summary>
        public static bool AllBuilt()
        {
            bool all = true;
            for (int i = 0; i < Names.Length; i++)
                if (Get(i) == null) all = false;
            return all;
        }

        /// <summary>Builds now (tests, editors); returns the table.</summary>
        public static Wavetable GetNow(int index)
        {
            var t = Volatile.Read(ref built[index]);
            if (t != null) return t;
            t = Make(index);
            Volatile.Write(ref built[index], t);
            Interlocked.Exchange(ref requested[index], 1);
            return t;
        }

        static double T(int frame) => frame / (double)(Frames - 1);

        // Complex harmonic (sine and cosine parts) of the four basic shapes.
        static (double A, double B) Shape(int shape, int h)
        {
            switch (shape)
            {
                case 0: return h == 1 ? (1, 0) : (0, 0);
                case 1: return h % 2 == 1 ? (8 / (Math.PI * Math.PI * h * h) * ((h / 2) % 2 == 0 ? 1 : -1), 0) : (0, 0);
                case 2: return (-2 / (Math.PI * h), 0);
                default: return h % 2 == 1 ? (4 / (Math.PI * h), 0) : (0, 0);
            }
        }

        static (double Amp, double Phase) Polar(double a, double b) => (Math.Sqrt(a * a + b * b), PortableMath.Atan2(b, a));

        static Wavetable Make(int index)
        {
            string name = Names[index];
            switch (index)
            {
                case 0:   // sine → triangle → saw → square
                    return Wavetable.FromHarmonics(name, Frames, (f, h) =>
                    {
                        double s = T(f) * 3;
                        int k = Math.Min(2, (int)s);
                        double u = s - k;
                        var (a0, b0) = Shape(k, h);
                        var (a1, b1) = Shape(k + 1, h);
                        return Polar(a0 + (a1 - a0) * u, b0 + (b1 - b0) * u);
                    });
                case 1:   // pulse width 50 % → 3 %
                    return Wavetable.FromHarmonics(name, Frames, (f, h) =>
                    {
                        double w = 0.5 - 0.47 * T(f);
                        double a = 2 * (1 - PortableMath.Cos(2 * Math.PI * h * w)) / (Math.PI * h);
                        double b = 2 * PortableMath.Sin(2 * Math.PI * h * w) / (Math.PI * h);
                        return Polar(a, b);
                    });
                case 2:   // more and more saw harmonics
                    return Wavetable.FromHarmonics(name, Frames, (f, h) =>
                    {
                        double top = 1 + 255 * T(f) * T(f);
                        double taper = h <= top ? 1 : Math.Max(0, 1 - (h - top) / 2);
                        return (taper / h, 0);
                    });
                case 3:   // vowels A → E → I → O → U
                {
                    double[][] vowels =
                    {
                        new[] { 800.0, 1150, 2900 }, new[] { 400.0, 1700, 2600 }, new[] { 300.0, 2300, 3000 },
                        new[] { 450.0, 800, 2830 }, new[] { 325.0, 700, 2530 },
                    };
                    return Wavetable.FromHarmonics(name, Frames, (f, h) =>
                    {
                        double s = T(f) * 4;
                        int k = Math.Min(3, (int)s);
                        double u = s - k;
                        double hz = h * 130.0;
                        double g = 0;
                        for (int i = 0; i < 3; i++)
                        {
                            double fc = vowels[k][i] + (vowels[k + 1][i] - vowels[k][i]) * u;
                            double bw = 60 + 40 * i;
                            g += (i == 0 ? 1 : 0.6 / i) / (1 + PortableMath.Pow((hz - fc) / bw, 2));
                        }
                        return (g / Math.Sqrt(h), 0);
                    }, 160);
                }
                case 4:   // hard-synced saw, ratio 1 → 8
                    return Wavetable.FromFunction(name, Frames, (f, t) =>
                    {
                        double k = 1 + 7 * T(f);
                        double p = k * t;
                        return 2 * (p - Math.Floor(p)) - 1;
                    });
                case 5:   // FM 1:1, index 0 → 6
                    return Wavetable.FromFunction(name, Frames, (f, t) =>
                        PortableMath.Sin(2 * Math.PI * t + 6 * T(f) * PortableMath.Sin(2 * Math.PI * t)));
                case 6:   // organ registrations (8', 4', 2⅔', 2', 1⅗', 1⅓', 1')
                {
                    int[] harmonic = { 1, 2, 3, 4, 5, 6, 8 };
                    double[][] regs =
                    {
                        new[] { 1.0, 0, 0, 0, 0, 0, 0 }, new[] { 1.0, 0.8, 0.6, 0, 0, 0, 0 }, new[] { 1.0, 1, 1, 0.8, 0, 0, 0 },
                        new[] { 1.0, 0.5, 0.8, 0.6, 0.5, 0.4, 0.3 }, new[] { 0.6, 0.6, 0.8, 1, 0.8, 0.8, 1 },
                    };
                    return Wavetable.FromHarmonics(name, Frames, (f, h) =>
                    {
                        int idx = Array.IndexOf(harmonic, h);
                        if (idx < 0) return (0, 0);
                        double s = T(f) * 4;
                        int k = Math.Min(3, (int)s);
                        double u = s - k;
                        return (regs[k][idx] + (regs[k + 1][idx] - regs[k][idx]) * u, 0);
                    }, 8);
                }
                case 7:   // saw through a resonant low-pass sweeping up
                    return Wavetable.FromHarmonics(name, Frames, (f, h) =>
                    {
                        double c = PortableMath.Pow(2, 8 * T(f));   // cutoff in harmonics, 1 → 256
                        double r = h / c;
                        double mag = 1 / Math.Sqrt((1 - r * r) * (1 - r * r) + (r / 5) * (r / 5));
                        return (mag / h, 0);
                    });
                case 8:   // stepped saw: 4 steps → 256
                    return Wavetable.FromFunction(name, Frames, (f, t) =>
                    {
                        double steps = Math.Round(PortableMath.Pow(2, 2 + 6 * T(f)));
                        double v = 2 * t - 1;
                        return Math.Round(v * steps / 2) / (steps / 2);
                    });
                default:  // seeded spectra morphing
                {
                    var spectra = new (double A, double P)[5][];
                    var rng = new Rng(0x6A11);
                    for (int s = 0; s < 5; s++)
                    {
                        spectra[s] = new (double, double)[257];
                        for (int h = 1; h <= 256; h++)
                            spectra[s][h] = ((0.3 + rng.NextUnipolar()) / PortableMath.Pow(h, 0.9), 2 * Math.PI * rng.NextUnipolar());
                    }
                    return Wavetable.FromHarmonics(name, Frames, (f, h) =>
                    {
                        if (h > 256) return (0, 0);
                        double s = T(f) * 4;
                        int k = Math.Min(3, (int)s);
                        double u = s - k;
                        var (a0, p0) = spectra[k][h];
                        var (a1, p1) = spectra[k + 1][h];
                        double re = a0 * PortableMath.Cos(p0) * (1 - u) + a1 * PortableMath.Cos(p1) * u;
                        double im = a0 * PortableMath.Sin(p0) * (1 - u) + a1 * PortableMath.Sin(p1) * u;
                        return (Math.Sqrt(re * re + im * im), PortableMath.Atan2(im, re));
                    }, 256);
                }
            }
        }
    }
}
