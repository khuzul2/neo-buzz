using NeoBuzz.Dsp;
using NeoBuzz.Looper;

namespace NeoBuzz.MachineTests
{
    /// <summary>What the Neo Looper tests and the listening pack share: a song position that follows a plan, triggers sent as the pulses a pad sends, the engine rendered block by block.</summary>
    internal static class LooperRig
    {
        public const int Rate = Measure.Rate;

        /// <summary>The pulse a pad or footswitch sends: the parameter goes to 1 in the block that holds <see cref="Sample"/>, and back to 0 in the next.</summary>
        public readonly struct Trig
        {
            public readonly int Sample;
            public readonly int Param;
            public Trig(int sample, int param) { Sample = sample; Param = param; }
        }

        public static void Phys(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        public static void Choice(ParamValues v, int p, string name)
        {
            int i = Array.IndexOf(Params.Table[p].Names, name);
            if (i < 0) throw new ArgumentException($"{Params.Table[p].Name}: no choice {name}");
            v.Set(p, Params.Table[p].Min + i);
        }

        public static void Switch(ParamValues v, int p, bool on) => v.Set(p, on ? 1 : 0);

        /// <summary>Small limits for the tests (15 s, two undo levels, so a pool is 23 MB), the rest at its defaults.</summary>
        public static ParamValues Fresh(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            Choice(v, P.MaxLoop, "15 s");
            Phys(v, P.UndoLevels, 2);
            setup?.Invoke(v);
            return v;
        }

        public static LooperEngine Engine(ParamValues v, int rate = Rate)
        {
            var e = new LooperEngine(v);
            e.SetSampleRate(rate);
            return e;
        }

        /// <summary>Renders the input through the engine along the plan, block by block, with the triggers pulsed. <paramref name="beatsPerBar"/> is the song's meter.</summary>
        public static (float[] L, float[] R) Render(LooperEngine e, ParamValues v, float[] l, float[] r, ChopRig.Seg[] plan, int block, IList<Trig> trigs, int beatsPerBar = 4, Action<int> afterBlock = null)
        {
            int length = l.Length;
            var ol = new float[length];
            var or = new float[length];
            var bl = new float[LooperEngine.MaxBlock];
            var br = new float[LooperEngine.MaxBlock];
            int segIndex = 0;
            var release = new List<int>();
            for (int pos = 0; pos < length; pos += block)
            {
                int n = Math.Min(block, length - pos);
                while (segIndex + 1 < plan.Length && plan[segIndex + 1].Start <= pos) segIndex++;
                var s = plan[segIndex];
                foreach (int p in release) v.Set(p, 0);
                release.Clear();
                if (trigs != null)
                    foreach (var t in trigs)
                        if (t.Sample >= pos && t.Sample < pos + n) { v.Set(t.Param, 1); release.Add(t.Param); }
                e.SetTransport(s.Playing, s.Beat + (pos - s.Start) / s.Spb, s.Spb, beatsPerBar);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
                afterBlock?.Invoke(pos);
            }
            return (ol, or);
        }

        public static float[] Constant(int length, float value)
        {
            var x = new float[length];
            Array.Fill(x, value);
            return x;
        }

        public static (float[] L, float[] R) Loop(LooperEngine e)
        {
            Assert.That(e.Snapshot(out var l, out var r, out int n, out _), Is.True, "the engine holds no loop");
            return (l, r);
        }

        /// <summary>Neo Drums: a four-bar pattern at 120 BPM (as the listening packs make it).</summary>
        public static (float[], float[]) DrumsSource(int length)
        {
            var v = PresetValues(NeoBuzz.Drums.Params.Table, "Neo Drums", "Classic — Punchy Kit");
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Rate);
            int step = (int)(ChopRig.Spb(120) / 4);
            return Run((l, r, n) => e.Process(l, r, n), length, pos =>
            {
                for (int s = 0; s * step < length; s++)
                {
                    int at = s * step;
                    if (at < pos || at >= pos + 256) continue;
                    int b = s % 16, bar = (s / 16) % 4;
                    if (b == 0 || b == 10 || b == 8 || (b == 13 && bar % 2 == 1)) e.NoteOn(0, 36, b == 0 ? 0.95f : 0.8f);
                    if (b == 4 || b == 12) e.NoteOn(1, 38, 0.9f);
                    if (b % 2 == 0) e.NoteOn(2, 42, b % 4 == 0 ? 0.8f : 0.5f);
                    if (b == 14) e.NoteOn(3, 46, 0.6f);
                }
            });
        }

        /// <summary>Neo Poly: Am, F, C, G, a bar each, with the "Pad — Warm Strings" preset.</summary>
        public static (float[], float[]) ChordSource(int length)
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", "Pad — Warm Strings");
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            int[][] chords = { new[] { 45, 52, 57, 60 }, new[] { 41, 48, 57, 60 }, new[] { 48, 52, 55, 60 }, new[] { 43, 50, 55, 59 } };
            int bar = (int)(ChopRig.Spb(120) * 4);
            return Run((l, r, n) => e.Process(l, r, n), length, pos =>
            {
                for (int c = 0; c * bar < length; c++)
                {
                    if (c * bar >= pos && c * bar < pos + 256)
                        for (int k = 0; k < 4; k++) e.NoteOn(k, chords[c % 4][k], 0.7f);
                    int off = c * bar + (int)(bar * 0.9);
                    if (off >= pos && off < pos + 256)
                        for (int k = 0; k < 4; k++) e.NoteOff(k);
                }
            });
        }

        public static ParamValues PresetValues(ParamDef[] table, string machine, string preset)
        {
            var found = PresetTests.AllPresets().First(p => p.Machine == machine && p.Preset == preset);
            var v = new ParamValues(table);
            foreach (var kv in found.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        static (float[] L, float[] R) Run(Action<float[], float[], int> process, int length, Action<int> atBlock)
        {
            var ol = new float[length];
            var or = new float[length];
            var l = new float[256];
            var r = new float[256];
            for (int pos = 0; pos < length; pos += 256)
            {
                atBlock(pos);
                int n = Math.Min(256, length - pos);
                Array.Clear(l, 0, 256);
                Array.Clear(r, 0, 256);
                process(l, r, n);
                Array.Copy(l, 0, ol, pos, n);
                Array.Copy(r, 0, or, pos, n);
            }
            return (ol, or);
        }
    }
}
