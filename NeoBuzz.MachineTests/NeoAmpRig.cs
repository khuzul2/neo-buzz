using NeoBuzz.Amp;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>The Neo Amp tests' helpers (docs/specs/neo-amp.md): values, engines whose cabinet is ready, a stand-in for the song's waves, signals.</summary>
    internal static class AmpRig
    {
        public const int Rate = Measure.Rate;
        public const int Block = AmpEngine.MaxBlock;

        /// <summary>A song's waves: slot → clip; a slot in <see cref="Loading"/> is still being read.</summary>
        public sealed class FakeWaves : IAmpWaves
        {
            public readonly Dictionary<int, AmpClip> Clips = new();
            public readonly HashSet<int> Loading = new();
            public int Wanted;
            int versions;

            public AmpWaveState State(int slot) => Clips.ContainsKey(slot) ? AmpWaveState.Ready : Loading.Contains(slot) ? AmpWaveState.Loading : AmpWaveState.Missing;

            public AmpClip Get(int slot) => Clips.TryGetValue(slot, out var c) ? c : null;

            public void Want(int slot) => Wanted = slot;

            public void Put(int slot, string name, float[] left, float[] right, int rate = Rate) => Clips[slot] = new AmpClip(name, left, right, rate, ++versions);
        }

        public static ParamValues Values(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            return v;
        }

        /// <summary>A parameter in its own unit (dB, %, Hz, ms).</summary>
        public static void Set(ParamValues v, int p, double physical) => v.Set(p, Params.Table[p].Raw(physical));

        /// <summary>A knob in per cent of its rotation.</summary>
        public static void Pct(ParamValues v, int p, double percent) => v.Set(p, (int)Math.Round(percent * 10));

        public static void Voicing(ParamValues v, int voicing) => v.Set(P.Voicing, voicing);

        public static void Cabinet(ParamValues v, int cab) => v.Set(P.Cabinet, cab);

        /// <summary>The song's wave slot 1 … 200.</summary>
        public static void WaveSlot(ParamValues v, int slot) => v.Set(P.CabWave, slot - 1);

        public static AmpEngine Engine(ParamValues v, IAmpWaves waves = null, int rate = Rate)
        {
            var e = new AmpEngine(v, waves);
            e.SetSampleRate(rate);
            e.BuildNow();
            return e;
        }

        /// <summary>Mono in (both sides), the left side out, in blocks (the input is not touched).</summary>
        public static float[] Run(AmpEngine e, float[] x, int block = Block, Action<int> atBlock = null)
        {
            var (l, _) = Run(e, x, (float[])x.Clone(), block, atBlock);
            return l;
        }

        public static (float[] L, float[] R) Run(AmpEngine e, float[] l, float[] r, int block = Block, Action<int> atBlock = null)
        {
            var ol = new float[l.Length];
            var or = new float[l.Length];
            var bl = new float[Block];
            var br = new float[Block];
            for (int pos = 0, b = 0; pos < l.Length; pos += block, b++)
            {
                atBlock?.Invoke(b);
                int n = Math.Min(block, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        public static float[] Sine(double hz, double amp, int n, int rate = Rate)
        {
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate));
            return x;
        }

        /// <summary>A decaying noise burst as a wave (a cabinet's impulse response for the tests).</summary>
        public static float[] Burst(double seconds, double rt60, uint seed = 77, int rate = Rate)
        {
            int n = (int)(seconds * rate);
            var x = new float[n];
            uint s = seed;
            for (int i = 0; i < n; i++)
            {
                s = s * 1664525u + 1013904223u;
                x[i] = ((s >> 8) / 16777216f * 2 - 1) * (float)Math.Exp(-6.9 * i / (rt60 * rate));
            }
            return x;
        }

        public static double Rms(float[] x, int from, int n)
        {
            double a = 0;
            for (int i = from; i < from + n; i++) a += x[i] * (double)x[i];
            return Math.Sqrt(a / n);
        }

        public static double Db(double lin) => 20 * Math.Log10(lin + 1e-12);

        // ---- the machine-wide tests' hooks (preset renders, modulation cost)

        /// <summary>A preset played on <paramref name="x"/> (a mono source on both sides), one engine for the whole render.</summary>
        public static float[] PresetRender(ParamValues v, float[] x) => Run(Engine(v), x);

        /// <summary>The audio call of the modulation-cost guard.</summary>
        public static Action<float[], float[], int> CostCall(ParamValues v)
        {
            var e = Engine(v);
            return (l, r, n) => e.Process(l, r, n);
        }
    }
}
