using NeoBuzz.Dsp;
using NeoBuzz.Poly;

namespace NeoBuzz.MachineTests
{
    /// <summary>Test lines for Neo Poly rendered as a sequencer would play them (AC2): steps of 16ths at 130 BPM, notes that slide overlap the next.</summary>
    internal static class AcidLines
    {
        const int Rate = Measure.Rate;

        public const double StepSeconds = 60.0 / 130 / 4;
        public static double Step => StepSeconds * Rate;

        public readonly record struct Note(int Pitch, bool Accent, bool Slide);

        /// <summary>The AC1 listening-pack line: 16 steps; accents on 1, 4, 7, 9, 12, 15; slides on 3, 6, 11, 15 (1-based).</summary>
        public static readonly Note[] Pack =
        {
            new(33, true, false), new(33, false, false), new(45, false, true), new(43, true, false), new(36, false, false), new(33, false, true), new(48, true, false), new(31, false, false),
            new(33, true, false), new(45, false, false), new(33, false, true), new(36, true, false), new(38, false, false), new(33, false, false), new(43, true, true), new(33, false, false),
        };

        /// <summary>A busier line for the listening pack: 16ths with eight slides and seven accents.</summary>
        public static readonly Note[] Busy =
        {
            new(33, true, true), new(45, false, false), new(33, false, true), new(36, true, false), new(48, false, true), new(43, false, true), new(33, true, false), new(31, false, false),
            new(33, true, true), new(40, false, false), new(45, true, true), new(33, false, false), new(36, true, true), new(38, false, false), new(43, true, true), new(33, false, false),
        };

        public static Note[] AllSlid(Note[] line) => line.Select(n => n with { Slide = true }).ToArray();
        public static Note[] AllAccented(Note[] line) => line.Select(n => n with { Accent = true }).ToArray();

        /// <summary>One event: sample, key, note (negative: note off), velocity 0 … 1.</summary>
        public readonly record struct Ev(int At, int Key, int Note, float Vel);

        /// <summary>The line played <paramref name="bars"/> times: a note on its step, off after 0.55 of a step (1.2 when it slides), keys alternating so a slide overlaps the next.</summary>
        public static List<Ev> Sequence(Note[] line, int bars, double start = 0, float plain = 90 / 127f)
        {
            var events = new List<Ev>();
            for (int bar = 0; bar < bars; bar++)
                for (int i = 0; i < line.Length; i++)
                {
                    int n = bar * line.Length + i;
                    var note = line[i];
                    int key = n % 2;
                    int on = (int)(start + n * Step);
                    int off = on + (int)(Step * (note.Slide ? 1.2 : 0.55));
                    events.Add(new Ev(on, key, note.Pitch, note.Accent ? 1f : plain));
                    events.Add(new Ev(off, key, -1, 0));
                }
            return events;
        }

        /// <summary>The first-note line: notes after silence, accented and plain, 0.6 s on, with a second of silence between.</summary>
        public static List<Ev> AfterSilence()
        {
            var events = new List<Ev>();
            int[] pitches = { 33, 36, 45, 31 };
            bool[] accent = { true, false, true, false };
            for (int i = 0; i < pitches.Length; i++)
            {
                int on = Rate / 4 + i * (int)(1.5 * Rate);
                events.Add(new Ev(on, 0, pitches[i], accent[i] ? 1f : 90 / 127f));
                events.Add(new Ev(on + (int)(0.6 * Rate), 0, -1, 0));
            }
            return events;
        }

        /// <summary>A chord line: four chords of 3 to 4 notes, each held 0.5 s, 0.6 s apart.</summary>
        public static List<Ev> Chords()
        {
            var events = new List<Ev>();
            int[][] chords = { new[] { 48, 52, 55, 59 }, new[] { 45, 48, 52 }, new[] { 53, 57, 60, 64 }, new[] { 43, 47, 50, 55 } };
            for (int c = 0; c < chords.Length; c++)
            {
                int on = Rate / 10 + c * (int)(0.6 * Rate);
                for (int k = 0; k < chords[c].Length; k++)
                {
                    events.Add(new Ev(on + k * 24, c * 8 + k, chords[c][k], 100 / 127f));
                    events.Add(new Ev(on + (int)(0.5 * Rate), c * 8 + k, -1, 0));
                }
            }
            return events;
        }

        /// <summary>A mono line, no accents: 16 steps of 16ths with the pack's slides as overlaps, every note at velocity 100.</summary>
        public static List<Ev> MonoLine() => Sequence(Pack.Select(n => n with { Accent = false }).ToArray(), 2, Rate / 10, 100 / 127f);

        /// <summary>Renders events through an engine set as <paramref name="preset"/> says (a filter type ≥ 0 replaces its F1 type; <paramref name="tweak"/> runs last), stereo.</summary>
        public static (float[] L, float[] R, PolyEngine E) Render(Dictionary<int, int> preset, int filterType, List<Ev> events, double seconds, Action<ParamValues> tweak = null, int block = 16, Action<PolyEngine, int> afterBlock = null)
        {
            var v = new ParamValues(Params.Table);
            foreach (var kv in preset) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            if (filterType >= 0) v.Set(P.F1Type, filterType);
            tweak?.Invoke(v);
            var e = new PolyEngine(v);
            e.SetSampleRate(Rate);
            events = events.OrderBy(x => x.At).ThenBy(x => x.Note < 0 ? 0 : 1).ToList();
            int total = (int)(seconds * Rate);
            var yl = new float[total];
            var yr = new float[total];
            var l = new float[PolyEngine.MaxBlock];
            var r = new float[PolyEngine.MaxBlock];
            int ev = 0;
            for (int pos = 0; pos < total; pos += block)
            {
                while (ev < events.Count && events[ev].At <= pos)
                {
                    var x = events[ev++];
                    if (x.Note < 0) e.NoteOff(x.Key); else e.NoteOn(x.Key, x.Note, x.Vel);
                }
                int n = Math.Min(block, total - pos);
                e.Process(l, r, n);
                afterBlock?.Invoke(e, pos);
                Array.Copy(l, 0, yl, pos, n);
                Array.Copy(r, 0, yr, pos, n);
            }
            return (yl, yr, e);
        }

        /// <summary>The same notes with filter 1 wide open (a state-variable low-pass at 20 kHz, no resonance, envelope, key, velocity or drive) and no filter 2: the oscillators' own edges.</summary>
        public static void WideOpen(ParamValues v)
        {
            v.Set(P.F1Type, 2);
            v.Set(P.F1Cutoff, 1000);
            v.Set(P.F1Reso, 0);
            v.Set(P.F1Env, 500);
            v.Set(P.F1Key, 0);
            v.Set(P.F1Vel, 0);
            v.Set(P.F1Drive, 0);
            v.Set(P.F2Type, 0);
        }

        /// <summary>The clicks in a render, with the oscillator's own edges (found in the same notes with the filter wide open) not counted; plain: the count without that.</summary>
        public static (List<int> Clicks, List<int> Plain) Clicks(Dictionary<int, int> preset, int filterType, List<Ev> events, double seconds, float[] l, float[] r, Action<ParamValues> tweak = null, int block = 16, double floor = ClickDetector.Floor)
        {
            var (rl, rr, _) = Render(preset, filterType, events, seconds, v => { tweak?.Invoke(v); WideOpen(v); }, block);
            var el = ClickDetector.OscillatorEdges(rl);
            var er = ClickDetector.OscillatorEdges(rr);
            var edges = new bool[el.Length];
            for (int i = 0; i < edges.Length; i++) edges[i] = el[i] || er[i];
            return (ClickDetector.Find(l, r, -1, floor, edges), ClickDetector.Find(l, r, -1, floor));
        }

        public static float[] Mid(float[] l, float[] r) => l.Select((x, i) => 0.5f * (x + r[i])).ToArray();

        public static double LineSeconds(int bars, int steps = 16) => bars * steps * StepSeconds + 0.5;
    }
}
