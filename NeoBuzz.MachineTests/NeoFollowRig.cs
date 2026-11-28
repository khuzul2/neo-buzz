using NeoBuzz.Drums;
using NeoBuzz.Dsp;
using NeoBuzz.FollowMachine;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Follow's test rig (docs/specs/neo-follow.md): an engine with settings in real units, signals with known truth, and a stand-in for a target parameter.</summary>
    internal static class NeoFollowRig
    {
        public const int Rate = Measure.Rate;

        /// <summary>Sets a parameter by its real value (dB, ms, a fraction, a choice index).</summary>
        public static void Set(ParamValues v, int p, double value) => v.Set(p, NeoBuzz.FollowMachine.Params.Table[p].Raw(value));

        public static (FollowEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Rate)
        {
            var v = new ParamValues(NeoBuzz.FollowMachine.Params.Table);
            // A clean slate for measuring: no smoothing, full depth, unipolar, no bias.
            setup?.Invoke(v);
            var e = new FollowEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        /// <summary>The engine's output after each block of <paramref name="block"/> samples, with its Raw and Level and the like read by <paramref name="read"/>.</summary>
        public static float[] Run(FollowEngine e, float[] x, int block, Func<FollowEngine, double> read = null)
        {
            int blocks = (x.Length + block - 1) / block;
            var y = new float[blocks];
            for (int b = 0; b < blocks; b++)
            {
                int pos = b * block, n = Math.Min(block, x.Length - pos);
                var seg = new float[block];
                Array.Copy(x, pos, seg, 0, n);
                e.Process(seg, seg, null, null, n);
                y[b] = read == null ? e.Output : (float)read(e);
            }
            return y;
        }

        /// <summary>The Process loop without allocating a segment per block (the audio-thread test).</summary>
        public static void Feed(FollowEngine e, float[] x, int pos, int n) => e.Process(Seg(x, pos, n), Seg(x, pos, n), null, null, n);

        static readonly float[] scratch = new float[FollowEngine.MaxBlock];

        static float[] Seg(float[] x, int pos, int n)
        {
            Array.Copy(x, pos, scratch, 0, n);
            return scratch;
        }

        /// <summary>A sine whose amplitude steps from <paramref name="lo"/> to <paramref name="hi"/> at <paramref name="stepAt"/> seconds and back at <paramref name="backAt"/>.</summary>
        public static float[] Steps(double hz, double lo, double hi, double stepAt, double backAt, double seconds, int rate = Rate)
        {
            int n = (int)(seconds * rate);
            var x = new float[n];
            double ph = 0, w = 2 * Math.PI * hz / rate;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                double a = t >= stepAt && t < backAt ? hi : lo;
                x[i] = (float)(a * Math.Sin(ph));
                ph += w;
            }
            return x;
        }

        /// <summary>A sine gliding exponentially (a straight line in notes) from <paramref name="note0"/> to <paramref name="note1"/> over <paramref name="seconds"/> after <paramref name="lead"/> seconds on the first note; the note of each sample is returned too.</summary>
        public static (float[] X, double[] Note) Glide(double note0, double note1, double lead, double seconds, double tail, double amplitude = 0.3, int rate = Rate)
        {
            int n = (int)((lead + seconds + tail) * rate);
            var x = new float[n];
            var note = new double[n];
            double ph = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate - lead;
                double nt = t <= 0 ? note0 : t >= seconds ? note1 : note0 + (note1 - note0) * t / seconds;
                note[i] = nt;
                double hz = 440.0 * Math.Pow(2.0, (nt - 69.0) / 12.0);
                ph += 2 * Math.PI * hz / rate;
                x[i] = (float)(amplitude * Math.Sin(ph));
            }
            return (x, note);
        }

        /// <summary>
        /// A drum loop made by Neo Drums itself (kick, snare, closed and open hat, a clap, toms) on 16ths at <paramref name="bpm"/>, mono (L + R) / 2, with the
        /// time of each hit; hits of different drums within 20 ms of each other are one onset (the truth lists each time once).
        /// </summary>
        public static (float[] X, List<long> Onsets) DrumLoop(int bars, double bpm = 120, int rate = Rate, bool kicksOnly = false)
        {
            var v = new ParamValues(NeoBuzz.Drums.Params.Table);
            v.Set(NeoBuzz.Drums.P.VelLevel, 0);
            v.Set(NeoBuzz.Drums.P.VelTone, 0);
            var d = new DrumsEngine(v);
            d.SetSampleRate(rate);
            double step = 60.0 / bpm / 4.0;
            // 16 steps a bar: voice, velocity.
            string[] kick = { "x", ".", ".", ".", ".", ".", "x", ".", "x", ".", ".", ".", ".", ".", "x", "." };
            string[] snare = { ".", ".", ".", ".", "x", ".", ".", ".", ".", ".", ".", ".", "x", ".", ".", "." };
            string[] hat = { "x", ".", "x", ".", "x", ".", "x", ".", "x", ".", "x", ".", "x", ".", "x", "o" };
            string[] tom = { ".", ".", ".", ".", ".", ".", ".", ".", ".", ".", ".", ".", ".", ".", ".", "." };
            var hits = new List<(long At, int Voice, float Vel)>();
            long start = (long)(0.25 * rate);
            for (int bar = 0; bar < bars; bar++)
                for (int s = 0; s < 16; s++)
                {
                    long at = start + (long)Math.Round((bar * 16 + s) * step * rate);
                    if (kick[s] == "x") hits.Add((at, 0, 1f));
                    if (kicksOnly) continue;
                    if (snare[s] == "x") hits.Add((at, 1, 0.9f));
                    if (hat[s] == "x") hits.Add((at, 4, 0.7f));
                    if (hat[s] == "o") hits.Add((at, 5, 0.7f));
                    if (bar % 4 == 3 && s == 13) hits.Add((at, 6, 0.9f));
                }
            long length = start + (long)Math.Round(bars * 16 * step * rate) + rate;
            var (l, r) = DrumsTests.Run(d, (int)length, DrumsEngine.MaxBlock, hits.Select(h => (h.At > int.MaxValue ? int.MaxValue : (int)h.At, (Action)(() => d.Hit(h.Voice, h.Vel)))).ToArray());
            var x = new float[l.Length];
            for (int i = 0; i < x.Length; i++) x[i] = 0.5f * (l[i] + r[i]);
            var onsets = new List<long>();
            foreach (var h in hits.OrderBy(h => h.At))
                if (onsets.Count == 0 || h.At - onsets[onsets.Count - 1] > 0.02 * rate) onsets.Add(h.At);
            return (x, onsets);
        }

        /// <summary>
        /// A lead line: a note every 0.25 s walking up and down a scale from C3 to C5 (a saw-like tone: a sine with its second and third harmonic) with a soft attack and
        /// a gap between notes. The truth is the note of each sample (0 in the gaps).
        /// </summary>
        public static (float[] X, double[] Note) Melody(double seconds, int rate = Rate)
        {
            int[] steps = { 0, 2, 4, 5, 7, 9, 11, 12, 14, 16, 17, 19, 21, 23, 24, 23, 21, 19, 17, 16, 14, 12, 11, 9, 7, 5, 4, 2 };
            int n = (int)(seconds * rate);
            var x = new float[n];
            var note = new double[n];
            double ph = 0;
            int len = rate / 4;
            for (int i = 0; i < n; i++)
            {
                int k = i / len, at = i % len;
                double nt = 48 + steps[k % steps.Length];
                double env = at < rate / 100 ? at / (rate / 100.0) : at > len - rate / 50 ? Math.Max(0, (len - at) / (rate / 50.0)) : 1.0;
                double hz = 440.0 * Math.Pow(2.0, (nt - 69.0) / 12.0);
                ph += 2 * Math.PI * hz / rate;
                if (at == 0) ph = 0;
                x[i] = (float)(0.3 * env * (Math.Sin(ph) + 0.4 * Math.Sin(2 * ph) + 0.2 * Math.Sin(3 * ph)));
                note[i] = env > 0 ? nt : 0;
            }
            return (x, note);
        }

        /// <summary>A stand-in for a target parameter (what the host's parameter is to the bank).</summary>
        internal sealed class Port : IModPort
        {
            public int Min { get; set; } = 0;
            public int Max { get; set; } = 1000;
            public int SafeMin { get; set; } = 0;
            public int SafeMax { get; set; } = 1000;
            public int SpanMax { get; set; } = 1000;
            public int Value;
            public int Puts;
            public int Get() => Value;
            public void Put(int raw) { Value = raw; Puts++; }
        }
    }
}
