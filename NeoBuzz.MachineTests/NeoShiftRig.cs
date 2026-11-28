using NeoBuzz.Dsp;
using NeoBuzz.Shift;

namespace NeoBuzz.MachineTests
{
    /// <summary>What the Neo Shift tests and the listening pack share: the engine rendered block by block, and test signals made in code.</summary>
    internal static class ShiftRig
    {
        public const int Rate = Measure.Rate;

        public static void Phys(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        public static void Choice(ParamValues v, int p, string name)
        {
            int i = Array.IndexOf(Params.Table[p].Names, name);
            if (i < 0) throw new ArgumentException($"{Params.Table[p].Name}: no choice {name}");
            v.Set(p, Params.Table[p].Min + i);
        }

        public static void Switch(ParamValues v, int p, bool on) => v.Set(p, on ? 1 : 0);

        /// <summary>The defaults (Pitch mode, Fast, an octave down, Mix and Amount 100 %) with <paramref name="setup"/> applied.</summary>
        public static ParamValues Fresh(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            return v;
        }

        /// <summary>Pitch mode with the given quality and shift, every other control at its default.</summary>
        public static ParamValues Pitch(string quality, double semitones, double cents = 0, int windowMs = 40) => Fresh(p =>
        {
            Choice(p, P.Mode, "Pitch");
            Choice(p, P.Quality, quality);
            Phys(p, P.Pitch, semitones);
            Phys(p, P.Fine, cents);
            Phys(p, P.Window, windowMs);
        });

        public static ParamValues FrequencyMode(double hz, double feedback = 0, double spread = 0) => Fresh(p =>
        {
            Choice(p, P.Mode, "Frequency");
            Phys(p, P.Shift, Math.Round(hz));
            Phys(p, P.ShiftFine, hz - Math.Round(hz));
            Phys(p, P.Feedback, feedback);
            Phys(p, P.Spread, spread);
        });

        public static ParamValues RingMode(double carrierHz, string shape = "Sine", double depth = 100) => Fresh(p =>
        {
            Choice(p, P.Mode, "Ring");
            Phys(p, P.Carrier, carrierHz);
            Choice(p, P.Shape, shape);
            Phys(p, P.Depth, depth);
        });

        public static ShiftEngine Engine(ParamValues v, int rate = Rate)
        {
            var e = new ShiftEngine(v);
            e.SetSampleRate(rate);
            return e;
        }

        /// <summary>
        /// Renders <paramref name="l"/> and <paramref name="r"/> through the engine in blocks of <paramref name="block"/>. <paramref name="atBlock"/> runs before
        /// each block with its first sample; <paramref name="notes"/> are (sample, on, key), heard by the block that holds them.
        /// </summary>
        public static (float[] L, float[] R) Render(ShiftEngine e, float[] l, float[] r, int block = 256, Action<int> atBlock = null,
            IList<(int Sample, bool On, int Key)> notes = null)
        {
            int length = l.Length;
            var ol = new float[length];
            var or = new float[length];
            var bl = new float[ShiftEngine.MaxBlock];
            var br = new float[ShiftEngine.MaxBlock];
            for (int pos = 0; pos < length; pos += block)
            {
                int n = Math.Min(block, length - pos);
                atBlock?.Invoke(pos);
                if (notes != null)
                    foreach (var nt in notes)
                        if (nt.Sample >= pos && nt.Sample < pos + n) { if (nt.On) e.NoteOn(nt.Key); else e.NoteOff(nt.Key); }
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        public static (float[] L, float[] R) Render(ParamValues v, float[] l, float[] r, int block = 256, Action<int> atBlock = null) => Render(Engine(v), l, r, block, atBlock);

        /// <summary>The preset tests' one second of noise through the engine (left channel).</summary>
        public static float[] PresetRender(ParamValues v, float[] x) => Render(Engine(v), x, (float[])x.Clone(), 256).L;

        /// <summary>The audio call of the modulation-cost guard.</summary>
        public static Action<float[], float[], int> CostCall(ParamValues v)
        {
            var e = Engine(v);
            return (l, r, n) => e.Process(l, r, n);
        }

        // ------------------------------------------------------------------------------------------------ signals

        public static float[] Sine(int length, double hz, double amplitude = 0.5, double phase = 0)
        {
            var x = new float[length];
            for (int i = 0; i < length; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate + phase));
            return x;
        }

        public static float[] Noise(int length, double amplitude = 0.3, int seed = 7)
        {
            var rng = new Rng((uint)seed);
            var x = new float[length];
            for (int i = 0; i < length; i++) x[i] = (float)(amplitude * rng.NextBipolar());
            return x;
        }

        public static float[] Sum(float[] a, float[] b)
        {
            var x = new float[a.Length];
            for (int i = 0; i < x.Length; i++) x[i] = a[i] + b[i];
            return x;
        }

        public static float[] Copy(float[] x) => (float[])x.Clone();

        /// <summary>The amplitude spectrum of <paramref name="n"/> samples from <paramref name="from"/>, Blackman-Harris-windowed (a full-scale sine reads 1).</summary>
        public static double[] Spectrum(float[] x, int from, int n)
        {
            var re = new double[n];
            var im = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                double w = 0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t);
                re[i] = x[from + i] * w;
                sum += w;
            }
            Fft.Transform(re, im, false);
            var m = new double[n / 2];
            for (int i = 0; i < m.Length; i++) m[i] = 2 * Math.Sqrt(re[i] * re[i] + im[i] * im[i]) / sum;
            return m;
        }

        /// <summary>The frequency of the strongest line (Hz) from the parabola through the log magnitudes of its bin and its neighbours.</summary>
        public static double PeakHz(double[] m, int n, double near = -1, int reach = 6)
        {
            int pk = 1;
            if (near > 0)
            {
                int c = (int)Math.Round(near * n / Rate);
                pk = c;
                for (int k = Math.Max(1, c - reach); k <= Math.Min(m.Length - 2, c + reach); k++) if (m[k] > m[pk]) pk = k;
            }
            else for (int k = 1; k < m.Length - 1; k++) if (m[k] > m[pk]) pk = k;
            double a0 = Math.Log(m[pk - 1]), a1 = Math.Log(m[pk]), a2 = Math.Log(m[pk + 1]);
            double d = 0.5 * (a0 - a2) / (a0 - 2 * a1 + a2);
            return (pk + d) * Rate / n;
        }

        public static double Cents(double hz, double wanted) => 1200 * Math.Log2(hz / wanted);

        public static double RmsDb(float[] x, int from, int to) => Measure.Db(Measure.Rms(x, from, to - from));

        /// <summary>The clicks in <paramref name="y"/> that the input <paramref name="x"/> (delayed by <paramref name="latency"/>) does not have.</summary>
        public static List<int> NewClicks(float[] x, float[] y, int latency)
        {
            var own = ClickDetector.Find(x).Select(c => c + latency).ToList();
            return ClickDetector.Find(y).Where(c => !own.Any(o => Math.Abs(o - c) <= 96)).ToList();
        }
    }
}
