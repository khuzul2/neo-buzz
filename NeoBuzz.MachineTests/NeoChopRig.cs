using NeoBuzz.Chop;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>What the Neo Chop tests and the listening pack share: a song position that follows a plan (tempo changes and loop jumps), the engine rendered block by block, and test signals made in code.</summary>
    internal static class ChopRig
    {
        public const int Rate = Measure.Rate;

        /// <summary>From sample <see cref="Start"/> the song is at beat <see cref="Beat"/> and a beat takes <see cref="Spb"/> samples (a tempo change or a loop jump starts a new one).</summary>
        public struct Seg
        {
            public int Start;
            public double Beat;
            public double Spb;
            public bool Playing;
            public Seg(int start, double beat, double spb, bool playing = true) { Start = start; Beat = beat; Spb = spb; Playing = playing; }
        }

        /// <summary>The samples of a beat as ReBuzz has them: a whole number of samples per tick, four ticks to the beat.</summary>
        public static double Spb(double bpm, int ticksPerBeat = 4) => Math.Round(60.0 * Rate / (bpm * ticksPerBeat)) * ticksPerBeat;

        public static Seg[] Constant(double bpm, double startBeat = 0) => new[] { new Seg(0, startBeat, Spb(bpm)) };

        public static void Phys(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        public static void Choice(ParamValues v, int p, string name)
        {
            int i = Array.IndexOf(Params.Table[p].Names, name);
            if (i < 0) throw new ArgumentException($"{Params.Table[p].Name}: no choice {name}");
            v.Set(p, Params.Table[p].Min + i);
        }

        public static void Switch(ParamValues v, int p, bool on) => v.Set(p, on ? 1 : 0);

        /// <summary>Every mode off, the gate and the pan off: a clean start for a test to switch on what it measures.</summary>
        public static ParamValues Fresh(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            foreach (int p in new[] { P.StutterOn, P.RepeatOn, P.RetrigOn, P.TapeOn, P.GateOn, P.PanOn }) v.Set(p, 0);
            setup?.Invoke(v);
            return v;
        }

        public static ChopEngine Engine(ParamValues v, int rate = Rate, int logSize = 0)
        {
            var e = new ChopEngine(v);
            e.SetSampleRate(rate);
            if (logSize > 0) e.Log = new ChopEvent[logSize];
            return e;
        }

        /// <summary>Renders <paramref name="l"/> and <paramref name="r"/> through the engine in blocks of <paramref name="block"/> along the plan. Notes: (sample, on, key) for the next block that holds them.</summary>
        public static (float[] L, float[] R) Render(ChopEngine e, float[] l, float[] r, Seg[] plan, int block, IList<(int Sample, bool On, int Key)> notes = null, Action<int> atBlock = null)
        {
            int length = l.Length;
            var ol = new float[length];
            var or = new float[length];
            var bl = new float[ChopEngine.MaxBlock];
            var br = new float[ChopEngine.MaxBlock];
            int segIndex = 0;
            for (int pos = 0; pos < length; pos += block)
            {
                int n = Math.Min(block, length - pos);
                while (segIndex + 1 < plan.Length && plan[segIndex + 1].Start <= pos) segIndex++;
                var s = plan[segIndex];
                atBlock?.Invoke(pos);
                e.SetTransport(s.Playing, s.Beat + (pos - s.Start) / s.Spb, s.Spb);
                if (notes != null)
                    foreach (var nt in notes.OrderBy(x => x.Sample))
                        if (nt.Sample >= pos && nt.Sample < pos + n) e.QueueNote(nt.Sample - pos, nt.On, nt.Key);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        public static (float[] L, float[] R) Render(ParamValues v, float[] l, float[] r, Seg[] plan, int block = 64)
        {
            var e = Engine(v);
            return Render(e, l, r, plan, block);
        }

        // ------------------------------------------------------------------------------------------------ signals

        /// <summary>A sine, 0.5 peak.</summary>
        public static float[] Sine(int length, double hz = 220, double amplitude = 0.5)
        {
            var x = new float[length];
            for (int i = 0; i < length; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
            return x;
        }

        /// <summary>Each sample is its own position, scaled so that a float holds it exactly: what the output reads shows where it read from.</summary>
        public static float[] Ramp(int length)
        {
            var x = new float[length];
            for (int i = 0; i < length; i++) x[i] = i / 8388608f;
            return x;
        }

        public static int FromRamp(float x) => (int)Math.Round(x * 8388608.0);

        /// <summary>
        /// A drum loop in code at <paramref name="bpm"/>: a kick (a sine falling from 120 to 45 Hz), a snare (noise and a 190 Hz tone) on 2 and 4, and
        /// closed and open hats on the eighths and sixteenths, every sound with a 1 ms attack, about -1 dBFS at the loudest.
        /// </summary>
        public static (float[] L, float[] R) Drums(int length, double bpm = 120)
        {
            double spb = Spb(bpm);
            var l = new float[length];
            var r = new float[length];
            var rng = new Rng(77);
            int steps = (int)(length / (spb / 4)) + 1;
            for (int s = 0; s < steps; s++)
            {
                int at = (int)Math.Round(s * spb / 4);
                int pos = s % 16;
                if (pos == 0 || pos == 6 || pos == 8 || pos == 11) Kick(l, r, at, pos == 0 ? 0.9f : 0.75f);
                if (pos == 4 || pos == 12) Snare(l, r, at, 0.7f, ref rng);
                if (pos % 2 == 0) Hat(l, r, at, pos % 4 == 0 ? 0.25f : 0.15f, pos == 14 ? 0.12 : 0.03, ref rng);
                else if (pos % 4 == 3) Hat(l, r, at, 0.1f, 0.02, ref rng);
            }
            float peak = 0;
            for (int i = 0; i < length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            if (peak > 0)
            {
                float g = 0.89f / peak;
                for (int i = 0; i < length; i++) { l[i] *= g; r[i] *= g; }
            }
            return (l, r);
        }

        static void Kick(float[] l, float[] r, int at, float level)
        {
            int n = (int)(0.35 * Rate);
            double ph = 0;
            for (int i = 0; i < n && at + i < l.Length; i++)
            {
                double t = (double)i / Rate;
                double f = 45 + 75 * Math.Exp(-t * 28);
                ph += 2 * Math.PI * f / Rate;
                double env = Math.Min(1.0, i / (0.001 * Rate)) * Math.Exp(-t * 11);
                float y = (float)(level * env * Math.Sin(ph));
                l[at + i] += y;
                r[at + i] += y;
            }
        }

        static void Snare(float[] l, float[] r, int at, float level, ref Rng rng)
        {
            int n = (int)(0.25 * Rate);
            double lp = 0;
            for (int i = 0; i < n && at + i < l.Length; i++)
            {
                double t = (double)i / Rate;
                double env = Math.Min(1.0, i / (0.001 * Rate)) * Math.Exp(-t * 22);
                double noise = rng.NextBipolar();
                lp += (noise - lp) * 0.6;
                double y = level * env * (0.7 * (noise - 0.3 * lp) + 0.5 * Math.Sin(2 * Math.PI * 190 * t));
                l[at + i] += (float)y;
                r[at + i] += (float)(y * 0.96);
            }
        }

        static void Hat(float[] l, float[] r, int at, float level, double decay, ref Rng rng)
        {
            int n = (int)((decay * 6 + 0.01) * Rate);
            float prev = 0;
            for (int i = 0; i < n && at + i < l.Length; i++)
            {
                double t = (double)i / Rate;
                double env = Math.Min(1.0, i / (0.001 * Rate)) * Math.Exp(-t / decay);
                float x = rng.NextBipolar();
                float hp = x - prev;          // a first difference: bright noise
                prev = x;
                float y = (float)(level * env * hp);
                l[at + i] += y;
                r[at + i] += y * 0.9f;
            }
        }

        // ------------------------------------------------------------------------------------------------ measures

        /// <summary>The largest |second difference| of <paramref name="x"/> from sample <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
        public static double MaxSecond(float[] x, int from, int to)
        {
            double m = 0;
            from = Math.Max(1, from);
            to = Math.Min(x.Length - 1, to);
            for (int i = from; i < to; i++) m = Math.Max(m, Math.Abs(x[i + 1] - 2.0 * x[i] + x[i - 1]));
            return m;
        }

        /// <summary>
        /// How much sharper the output is at a boundary than around it: the largest |second difference| within +-3 ms of <paramref name="at"/>
        /// over the largest between 3 and 30 ms on either side. At most 1.5 is the gate.
        /// </summary>
        public static double BoundaryRatio(float[] x, int at, out double near, out double outside)
        {
            int a = (int)(0.003 * Rate), b = (int)(0.030 * Rate);
            near = MaxSecond(x, at - a, at + a + 1);
            outside = Math.Max(MaxSecond(x, at - b, at - a), MaxSecond(x, at + a + 1, at + b + 1));
            return near / Math.Max(outside, 1e-9);
        }

        /// <summary>Clicks the detector finds in <paramref name="y"/> that it does not find within 2 ms of one in <paramref name="x"/> (the input's own).</summary>
        public static List<int> NewClicks(float[] x, float[] y)
        {
            var own = ClickDetector.Find(x);
            var found = ClickDetector.Find(y);
            return found.Where(c => !own.Any(o => Math.Abs(o - c) <= 96)).ToList();
        }
    }
}
