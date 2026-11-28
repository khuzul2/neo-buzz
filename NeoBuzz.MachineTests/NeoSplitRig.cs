using NeoBuzz.Dsp;
using NeoBuzz.Split;

namespace NeoBuzz.MachineTests
{
    /// <summary>The Neo Split tests' helpers (docs/specs/neo-split.md): values, engines, runs in blocks, and the sources made by our own machines.</summary>
    internal static class SplitRig
    {
        public const int Rate = Measure.Rate;
        public const int Block = SplitEngine.MaxBlock;
        public const double Bpm = 120;

        public static ParamValues Values(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            return v;
        }

        public static void Set(ParamValues v, int p, double physical) => v.Set(p, Params.Table[p].Raw(physical));

        /// <summary>Mode, band count (2 … 4) and slope (0: 24 dB/oct, 1: 48 dB/oct).</summary>
        public static void Choose(ParamValues v, SplitEngine.Mode mode, int bands = 4, int slope = 0)
        {
            v.Set(P.Mode, (int)mode);
            v.Set(P.Bands, bands - 2);
            v.Set(P.Slope, slope);
        }

        public static void Crossovers(ParamValues v, double f1, double f2, double f3)
        {
            Set(v, P.Xo1, f1);
            Set(v, P.Xo2, f2);
            Set(v, P.Xo3, f3);
        }

        public static SplitEngine Engine(ParamValues v, int rate = Rate)
        {
            var e = new SplitEngine(v);
            e.SetSampleRate(rate);
            return e;
        }

        /// <summary>The four outputs of a stereo run, in blocks of <paramref name="block"/> (the input is not touched).</summary>
        public static (float[][] L, float[][] R) Run(SplitEngine e, float[] l, float[] r, int block = Block, Action<int> atBlock = null)
        {
            var ol = new float[SplitEngine.Outputs][];
            var or = new float[SplitEngine.Outputs][];
            for (int o = 0; o < SplitEngine.Outputs; o++)
            {
                ol[o] = new float[l.Length];
                or[o] = new float[l.Length];
            }
            var bl = new float[Block];
            var br = new float[Block];
            for (int pos = 0, b = 0; pos < l.Length; pos += block, b++)
            {
                atBlock?.Invoke(b);
                int n = Math.Min(block, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                for (int o = 0; o < SplitEngine.Outputs; o++)
                {
                    Array.Copy(e.OutL[o], 0, ol[o], pos, n);
                    Array.Copy(e.OutR[o], 0, or[o], pos, n);
                }
            }
            return (ol, or);
        }

        /// <summary>The four outputs added, in a fixed order.</summary>
        public static float[] Sum(float[][] x)
        {
            var y = new float[x[0].Length];
            for (int i = 0; i < y.Length; i++)
            {
                float s = 0;
                for (int o = 0; o < x.Length; o++) s += x[o][i];
                y[i] = s;
            }
            return y;
        }

        public static double Rms(float[] x, int from = 0, int to = -1)
        {
            if (to < 0) to = x.Length;
            double s = 0;
            for (int i = from; i < to; i++) s += (double)x[i] * x[i];
            return Math.Sqrt(s / Math.Max(1, to - from));
        }

        static bool At(double sample, int pos) => (int)Math.Ceiling(sample) >= pos && (int)Math.Ceiling(sample) < pos + 256;

        static ParamValues PresetValues(ParamDef[] table, string machine, string preset)
        {
            var found = PresetTests.AllPresets().First(p => p.Machine == machine && p.Preset == preset);
            var v = new ParamValues(table);
            foreach (var kv in found.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        static (float[] L, float[] R) Render(Action<float[], float[], int> process, Action<int> atBlock, int length)
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

        /// <summary>The sample of each hit of the drum loop (a kick, a snare, a hat or an open hat starts there) and its key.</summary>
        public static List<(int Sample, int Key)> DrumHits(int bars)
        {
            double step = Rate * 60.0 / Bpm / 4;
            var hits = new List<(int, int)>();
            for (int s = 0; s < bars * 16; s++)
            {
                int b = s % 16, bar = s / 16;
                int at = (int)Math.Ceiling(s * step);
                if (b == 0 || b == 10 || (b == 7 && bar % 2 == 1)) hits.Add((at, 36));
                if (b == 4 || b == 12) hits.Add((at, 38));
                if (b % 2 == 0) hits.Add((at, 42));
                if (b == 14) hits.Add((at, 46));
            }
            return hits;
        }

        /// <summary>Neo Drums, "Classic — Punchy Kit": a loop of <paramref name="bars"/> bars (kick, snare, hats, an open hat) at 120 BPM, then nothing up to <paramref name="length"/>.</summary>
        public static (float[] L, float[] R) DrumLoop(int bars, int length)
        {
            var v = PresetValues(NeoBuzz.Drums.Params.Table, "Neo Drums", "Classic — Punchy Kit");
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Rate);
            var hits = DrumHits(bars);
            return Render((l, r, n) => e.Process(l, r, n), pos =>
            {
                foreach (var (at, key) in hits)
                {
                    if (!At(at, pos)) continue;
                    int track = key == 36 ? 0 : key == 38 ? 1 : key == 42 ? 2 : 3;
                    int s = (int)Math.Round(at / (Rate * 60.0 / Bpm / 4));
                    float vel = key == 36 ? (s % 16 == 0 ? 0.95f : 0.8f) : key == 38 ? 0.9f : key == 42 ? (s % 4 == 0 ? 0.8f : 0.5f) : 0.6f;
                    e.NoteOn(track, key, vel);
                }
            }, length);
        }

        /// <summary>Neo Poly as the preset <paramref name="preset"/> plays <paramref name="notes"/> (note numbers) held from 0 until <paramref name="holdSeconds"/>, for <paramref name="length"/> samples.</summary>
        public static (float[] L, float[] R) PadChord(string preset, int[] notes, double holdSeconds, int length)
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", preset);
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            int off = (int)(holdSeconds * Rate);
            return Render((l, r, n) => e.Process(l, r, n), pos =>
            {
                if (pos == 0) for (int k = 0; k < notes.Length; k++) e.NoteOn(k, notes[k], 0.8f);
                if (pos <= off && off < pos + 256) for (int k = 0; k < notes.Length; k++) e.NoteOff(k);
            }, length);
        }

        /// <summary>A bass line: Neo Poly's "Bass — Classic Sub" on A1, A1, C2, E2 (a note a beat), for <paramref name="length"/> samples.</summary>
        public static (float[] L, float[] R) Bass(int length)
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", "Bass — Classic Sub");
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            int[] notes = { 33, 33, 36, 40 };
            double beat = Rate * 60.0 / Bpm;
            return Render((l, r, n) => e.Process(l, r, n), pos =>
            {
                for (int s = 0; s < 64; s++)
                {
                    if (At(s * beat, pos)) e.NoteOn(0, notes[s % 4], 0.85f);
                    if (At(s * beat + beat * 0.85, pos)) e.NoteOff(0);
                }
            }, length);
        }

        /// <summary>Neo Poly "Pluck — Bright", an A minor figure of eighth notes: the lead.</summary>
        public static (float[] L, float[] R) Lead(int length)
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", "Pluck — Bright");
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            int[] notes = { 69, 72, 76, 79, 76, 72, 74, 77 };
            double step = Rate * 60.0 / Bpm / 2;
            return Render((l, r, n) => e.Process(l, r, n), pos =>
            {
                for (int s = 0; s < 128; s++)
                {
                    if (At(s * step, pos)) e.NoteOn(s % 4, notes[s % notes.Length], 0.8f);
                    if (At(s * step + step * 0.8, pos)) e.NoteOff(s % 4);
                }
            }, length);
        }

        /// <summary>The mix the Bands and Mid and Side packs play: drums, a bass, a pad widened to twice its side and a lead panned to the right.</summary>
        public static (float[] L, float[] R) Mix(int length)
        {
            var (dl, dr) = DrumLoop(length / (int)(Rate * 60.0 / Bpm * 4) + 1, length);
            var (bl, br) = Bass(length);
            var (pl, pr) = PadChord("Pad — Warm Strings", new[] { 57, 60, 64, 67 }, 14.5, length);
            var (ll, lr) = Lead(length);
            var l = new float[length];
            var r = new float[length];
            for (int i = 0; i < length; i++)
            {
                float m = 0.5f * (pl[i] + pr[i]), s = 0.5f * (pl[i] - pr[i]) * 2f;
                l[i] = 0.9f * dl[i] + 0.9f * bl[i] + 0.7f * (m + s) + 0.45f * ll[i];
                r[i] = 0.9f * dr[i] + 0.9f * br[i] + 0.7f * (m - s) + 0.9f * lr[i];
            }
            return (l, r);
        }

        /// <summary>The mix the Transient and Body pack plays: the drums and the pad.</summary>
        public static (float[] L, float[] R) DrumsAndPad(int length)
        {
            var (dl, dr) = DrumLoop(length / (int)(Rate * 60.0 / Bpm * 4) + 1, length);
            var (pl, pr) = PadChord("Pad — Warm Strings", new[] { 57, 60, 64, 67 }, 14.5, length);
            var l = new float[length];
            var r = new float[length];
            for (int i = 0; i < length; i++)
            {
                l[i] = 0.9f * dl[i] + 0.7f * pl[i];
                r[i] = 0.9f * dr[i] + 0.7f * pr[i];
            }
            return (l, r);
        }

        /// <summary>A preset's render for the preset tests: the four outputs added (the input again, in the same level), the left channel.</summary>
        public static float[] PresetRender(ParamValues v, float[] x)
        {
            var e = Engine(v);
            var (l, _) = Run(e, x, x);
            return Sum(l);
        }

        /// <summary>The drum loop's second difference against its local mean is the click detector's job: the count of clicks in a signal that the source itself did not have.</summary>
        public static int NewClicks(float[] processed, float[] dry)
        {
            var dryClicks = new HashSet<int>(ClickDetector.Find(dry));
            return ClickDetector.Find(processed).Count(c => !dryClicks.Any(x => Math.Abs(x - c) < 64));
        }
    }
}
