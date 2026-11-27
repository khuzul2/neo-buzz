using NeoBuzz.Dsp;
using NeoBuzz.Prism;

namespace NeoBuzz.MachineTests
{
    /// <summary>What the Neo Prism tests and the listening pack share: the engine rendered block by block with the song running, and test signals made in code.</summary>
    internal static class PrismRig
    {
        public const int Rate = Measure.Rate;
        public static readonly int[] Sizes = { 1024, 2048, 4096, 8192 };

        public static void Phys(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        public static void Choice(ParamValues v, int p, string name)
        {
            int i = Array.IndexOf(Params.Table[p].Names, name);
            if (i < 0) throw new ArgumentException($"{Params.Table[p].Name}: no choice {name}");
            v.Set(p, Params.Table[p].Min + i);
        }

        public static void Switch(ParamValues v, int p, bool on) => v.Set(p, on ? 1 : 0);

        /// <summary>The defaults (every section off, FFT 4096, Mix and Amount 100 %) with <paramref name="setup"/> applied.</summary>
        public static ParamValues Fresh(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            return v;
        }

        public static void Size(ParamValues v, int size) => v.Set(P.FftSize, Array.IndexOf(Sizes, size));

        /// <summary>Every section on, with settings that all do something.</summary>
        public static ParamValues AllOn(int size = 4096)
        {
            var v = Fresh();
            Size(v, size);
            Switch(v, P.PitchOn, true); Phys(v, P.Semi, 5); Phys(v, P.Cents, 20); Switch(v, P.FormantKeep, true);
            Switch(v, P.ScrOn, true); Phys(v, P.ScrAmount, 40); Phys(v, P.ScrWidth, 6); Choice(v, P.ScrSync, "Free"); Phys(v, P.ScrRate, 7);
            Switch(v, P.FreezeOn, true); Phys(v, P.Hold, 50);
            Switch(v, P.BlurOn, true); Phys(v, P.BlurTime, 600); Phys(v, P.Smear, 3);
            Switch(v, P.DelayOn, true); Phys(v, P.DelLow, 40); Phys(v, P.DelHigh, 250); Phys(v, P.DelFeedback, 50); Phys(v, P.DelDamping, 30);
            return v;
        }

        public static PrismEngine Engine(ParamValues v, int rate = Rate)
        {
            var e = new PrismEngine(v);
            e.SetSampleRate(rate);
            return e;
        }

        public static double Spb(double bpm = 120) => Math.Round(60.0 * Rate / (bpm * 4)) * 4;

        /// <summary>
        /// Renders <paramref name="l"/> and <paramref name="r"/> through the engine in blocks of <paramref name="block"/>, the song running from beat
        /// <paramref name="startBeat"/> at <paramref name="bpm"/> (not playing: the engine keeps its own time). <paramref name="atBlock"/> runs before
        /// each block with its first sample; <paramref name="notes"/> are (sample, on, key), heard by the block that holds them.
        /// </summary>
        public static (float[] L, float[] R) Render(PrismEngine e, float[] l, float[] r, int block = 256, Action<int> atBlock = null, double bpm = 120,
            double startBeat = 0, bool playing = true, IList<(int Sample, bool On, int Key)> notes = null)
        {
            int length = l.Length;
            var ol = new float[length];
            var or = new float[length];
            var bl = new float[PrismEngine.MaxBlock];
            var br = new float[PrismEngine.MaxBlock];
            double spb = Spb(bpm);
            for (int pos = 0; pos < length; pos += block)
            {
                int n = Math.Min(block, length - pos);
                atBlock?.Invoke(pos);
                e.SetTransport(playing, startBeat + pos / spb, spb);
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

        /// <summary>The preset tests' one second of noise through the engine (left channel), the song running from beat 0.</summary>
        public static float[] PresetRender(ParamValues v, float[] x) => Render(Engine(v), x, (float[])x.Clone(), 256).L;

        /// <summary>The audio call of the modulation-cost guard: the song runs on from call to call.</summary>
        public static Action<float[], float[], int> CostCall(ParamValues v)
        {
            var e = Engine(v);
            double spb = Spb(120), beat = 0;
            return (l, r, n) =>
            {
                e.SetTransport(true, beat, spb);
                e.Process(l, r, n);
                beat += n / spb;
            };
        }

        // ------------------------------------------------------------------------------------------------ signals

        public static float[] Sine(int length, double hz, double amplitude = 0.5, double phase = 0)
        {
            var x = new float[length];
            for (int i = 0; i < length; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate + phase));
            return x;
        }

        public static float[] ChordSignal(int length, double[] hz, double amplitude)
        {
            var x = new float[length];
            foreach (var f in hz)
            {
                var s = Sine(length, f, amplitude);
                for (int i = 0; i < length; i++) x[i] += s[i];
            }
            return x;
        }

        public static readonly double[] Triad = { 261.63, 329.63, 392.0 };

        /// <summary>The frequency of a steady sine from its upward zero crossings between two samples (linear interpolation).</summary>
        public static double Frequency(float[] x, int from, int to)
        {
            double first = -1, last = -1;
            int count = 0;
            for (int i = from + 1; i < to; i++)
            {
                if (x[i - 1] < 0 && x[i] >= 0)
                {
                    double t = i - 1 + -x[i - 1] / (double)(x[i] - x[i - 1]);
                    if (first < 0) first = t;
                    last = t;
                    count++;
                }
            }
            return (count - 1) * Rate / (last - first);
        }

        public static double Cents(double hz, double wanted) => 1200 * Math.Log2(hz / wanted);

        /// <summary>The amplitude spectrum of <paramref name="n"/> samples from <paramref name="from"/>, Hann-windowed (a full-scale sine reads 1).</summary>
        public static double[] HannSpectrum(float[] x, int from, int n)
        {
            var re = new double[n];
            var im = new double[n];
            for (int i = 0; i < n; i++) re[i] = x[from + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            Fft.Transform(re, im, false);
            var m = new double[n / 2];
            for (int i = 0; i < m.Length; i++) m[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]) / n * 4;
            return m;
        }

        /// <summary>A peak's frequency from the parabola through the log magnitudes of its bin and its neighbours.</summary>
        public static double PeakFrequency(double[] m, int near, int n, int reach = 4)
        {
            int pk = near;
            for (int k = near - reach; k <= near + reach; k++) if (m[k] > m[pk]) pk = k;
            double a0 = Math.Log(m[pk - 1]), a1 = Math.Log(m[pk]), a2 = Math.Log(m[pk + 1]);
            double d = 0.5 * (a0 - a2) / (a0 - 2 * a1 + a2);
            return (pk + d) * Rate / n;
        }

        /// <summary>The largest |second difference| of <paramref name="x"/> from sample <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
        public static double MaxSecond(float[] x, int from, int to)
        {
            double m = 0;
            from = Math.Max(1, from);
            to = Math.Min(x.Length - 1, to);
            for (int i = from; i < to; i++) m = Math.Max(m, Math.Abs(x[i + 1] - 2.0 * x[i] + x[i - 1]));
            return m;
        }

        public static double RmsDb(float[] x, int from, int to) => Measure.Db(Measure.Rms(x, from, to - from));

        /// <summary>
        /// A drum loop in code at <paramref name="bpm"/> with a chord loop under it (a kick, a snare on 2 and 4, hats, and four chords of a saw-ish
        /// pad), about -3 dBFS at the loudest: full-scale material for the preset tests.
        /// </summary>
        public static (float[] L, float[] R) Music(int length, double bpm = 120)
        {
            var (l, r) = ChopRig.DrumLoop(length, bpm);
            double bar = Spb(bpm) * 4;
            double[][] chords = { new[] { 220.0, 261.63, 329.63 }, new[] { 174.61, 261.63, 349.23 }, new[] { 196.0, 261.63, 329.63 }, new[] { 196.0, 246.94, 293.66 } };
            for (int c = 0; c * bar < length; c++)
            {
                var ch = chords[c % 4];
                int from = (int)(c * bar), to = (int)Math.Min(length, (c + 1) * bar);
                foreach (var f in ch)
                    for (int i = from; i < to; i++)
                    {
                        double t = (double)(i - from) / Rate;
                        double env = Math.Min(1.0, t / 0.05) * Math.Min(1.0, (to - i) / (0.05 * Rate));
                        double ph = 2 * Math.PI * f * i / Rate;
                        float y = (float)(0.1 * env * (Math.Sin(ph) + 0.5 * Math.Sin(2 * ph) + 0.3 * Math.Sin(3 * ph)));
                        l[i] += y;
                        r[i] += y * 0.95f;
                    }
            }
            float peak = 0;
            for (int i = 0; i < length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            float g = (float)(Math.Pow(10, -3.0 / 20) / Math.Max(peak, 1e-9f));
            for (int i = 0; i < length; i++) { l[i] *= g; r[i] *= g; }
            return (l, r);
        }

        /// <summary>The clicks in <paramref name="y"/> that the input <paramref name="x"/> (delayed by <paramref name="latency"/>) does not have.</summary>
        public static List<int> NewClicks(float[] x, float[] y, int latency)
        {
            var own = ClickDetector.Find(x).Select(c => c + latency).ToList();
            return ClickDetector.Find(y).Where(c => !own.Any(o => Math.Abs(o - c) <= 96)).ToList();
        }
    }
}
