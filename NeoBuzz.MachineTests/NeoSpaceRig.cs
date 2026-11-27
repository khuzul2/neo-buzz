using NeoBuzz.Dsp;
using NeoBuzz.Space;

namespace NeoBuzz.MachineTests
{
    /// <summary>The Neo Space tests' helpers (docs/specs/neo-space.md): values, engines whose response is ready, a stand-in for the song's waves, signals.</summary>
    internal static class SpaceRig
    {
        public const int Rate = Measure.Rate;
        public const int Block = SpaceEngine.MaxBlock;

        /// <summary>A song's waves: slot → clip; a slot in <see cref="Loading"/> is still being read.</summary>
        public sealed class FakeWaves : ISpaceWaves
        {
            public readonly Dictionary<int, SpaceClip> Clips = new();
            public readonly HashSet<int> Loading = new();
            public int Wanted;
            int versions;

            public SpaceWaveState State(int slot) => Clips.ContainsKey(slot) ? SpaceWaveState.Ready : Loading.Contains(slot) ? SpaceWaveState.Loading : SpaceWaveState.Missing;

            public SpaceClip Get(int slot) => Clips.TryGetValue(slot, out var c) ? c : null;

            public void Want(int slot) => Wanted = slot;

            public void Put(int slot, string name, float[] left, float[] right, int rate = Rate) => Clips[slot] = new SpaceClip(name, left, right, rate, ++versions);
        }

        public static ParamValues Values(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            return v;
        }

        public static void Set(ParamValues v, int p, double physical) => v.Set(p, Params.Table[p].Raw(physical));

        public static void Room(ParamValues v, string name)
        {
            int i = Array.IndexOf(Params.Table[P.Room].Names, name);
            Assert.That(i, Is.GreaterThanOrEqualTo(0), "room " + name);
            v.Set(P.Room, i);
        }

        /// <summary>Slot 1 … 200.</summary>
        public static void WaveSlot(ParamValues v, int slot) => v.Set(P.Wave, slot - 1);

        /// <summary>Mix 100 %, Amount 100 %, Output 0 dB: the output is the convolution alone.</summary>
        public static void Wet(ParamValues v)
        {
            v.Set(P.Mix, 1000);
            v.Set(P.Amount, 1000);
            v.Set(P.Output, 500);
        }

        public static SpaceEngine Engine(ParamValues v, ISpaceWaves waves = null, int rate = Rate)
        {
            var e = new SpaceEngine(v, waves);
            e.SetSampleRate(rate);
            e.BuildNow();
            return e;
        }

        /// <summary>Stereo in, stereo out, in blocks (the input is not touched).</summary>
        public static (float[] L, float[] R) Run(SpaceEngine e, float[] l, float[] r, int block = Block, Action<int> atBlock = null)
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

        public static float[] Noise(int n, uint seed, float amp = 0.5f)
        {
            var x = new float[n];
            uint s = seed;
            for (int i = 0; i < n; i++)
            {
                s = s * 1664525u + 1013904223u;
                x[i] = ((s >> 8) / 16777216f * 2 - 1) * amp;
            }
            return x;
        }

        /// <summary>Clicks of different heights at the given sample positions.</summary>
        public static float[] Clicks(int n, params int[] at)
        {
            var x = new float[n];
            for (int k = 0; k < at.Length; k++) x[at[k]] = (k % 3 == 0 ? 0.9f : k % 3 == 1 ? -0.6f : 0.35f);
            return x;
        }

        /// <summary>The direct convolution, in double, of the first <paramref name="n"/> samples of the output.</summary>
        public static double[] Direct(float[] x, float[] h, int n)
        {
            var y = new double[n];
            for (int i = 0; i < n; i++)
            {
                double acc = 0;
                int kmax = Math.Min(i, h.Length - 1);
                for (int k = 0; k <= kmax; k++) acc += h[k] * (double)x[i - k];
                y[i] = acc;
            }
            return y;
        }

        /// <summary>The direct convolution of a sparse input (a few non-zero samples): exact and quick.</summary>
        public static double[] DirectSparse(float[] x, float[] h, int n)
        {
            var y = new double[n];
            for (int p = 0; p < x.Length && p < n; p++)
            {
                if (x[p] == 0) continue;
                for (int k = 0; k < h.Length && p + k < n; k++) y[p + k] += h[k] * (double)x[p];
            }
            return y;
        }

        /// <summary>The error against a reference, in dB of the reference's energy.</summary>
        public static double ErrorDb(float[] got, double[] want)
        {
            double err = 0, sig = 0;
            for (int i = 0; i < want.Length; i++)
            {
                double d = got[i] - want[i];
                err += d * d;
                sig += want[i] * want[i];
            }
            return 10 * Math.Log10(err / sig + 1e-30);
        }

        /// <summary>The Schroeder decay (the time the backwards-integrated energy takes to fall 30 dB, doubled) of a response in the octave at <paramref name="hz"/> (0: all).</summary>
        public static double Rt60(float[] ir, double hz, int rate = Rate)
        {
            var x = new double[ir.Length];
            if (hz > 0)
            {
                var a = new Biquad(); var b = new Biquad();
                a.Set(BiquadType.BandPass, hz, rate, 1.41);
                b.Set(BiquadType.BandPass, hz, rate, 1.41);
                for (int i = 0; i < ir.Length; i++) x[i] = b.Process(a.Process(ir[i]));
            }
            else
                for (int i = 0; i < ir.Length; i++) x[i] = ir[i];
            double total = 0;
            for (int i = 0; i < x.Length; i++) total += x[i] * x[i];
            double acc = total;
            for (int i = 0; i < x.Length; i++)
            {
                acc -= x[i] * x[i];
                if (10 * Math.Log10(Math.Max(1e-30, acc) / total) < -30) return 2.0 * i / rate;
            }
            return 2.0 * x.Length / rate;
        }

        public static double Energy(float[] x, int from = 0, int to = -1)
        {
            if (to < 0 || to > x.Length) to = x.Length;
            double e = 0;
            for (int i = from; i < to; i++) e += x[i] * (double)x[i];
            return e;
        }

        /// <summary>A decaying noise burst as a wave: stereo when <paramref name="stereo"/>.</summary>
        public static (float[] L, float[] R) Burst(double seconds, double rt60, bool stereo, int rate = Rate, uint seed = 77)
        {
            int n = (int)(seconds * rate);
            var l = Noise(n, seed, 1f);
            var r = stereo ? Noise(n, seed + 1, 1f) : null;
            for (int i = 0; i < n; i++)
            {
                float g = (float)Math.Exp(-6.9 * i / (rt60 * rate));
                l[i] *= g;
                if (r != null) r[i] *= g;
            }
            return (l, r);
        }

        // ---- the machine-wide tests' hooks (preset renders, modulation cost)

        /// <summary>A preset played on <paramref name="x"/>: the response is made first (the machine's Busy does that), one engine for the whole render.</summary>
        public static float[] PresetRender(ParamValues v, float[] x)
        {
            var e = Engine(v);
            return Run(e, x, (float[])x.Clone()).L;
        }

        /// <summary>The audio call of the modulation-cost guard.</summary>
        public static Action<float[], float[], int> CostCall(ParamValues v)
        {
            var e = Engine(v);
            return (l, r, n) => e.Process(l, r, n);
        }
    }
}
