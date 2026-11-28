#nullable disable
using NeoBuzz.Dsp;
using NeoBuzz.Notes;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Notes' test harness (docs/specs/neo-notes.md): the engine, its parameters and a clock. Events are given at absolute samples, blocks of <see cref="B"/> samples
    /// are run, what was sent is logged at absolute samples. Also checks, block by block, what the wire needs: offsets in order, at most 1024 events.
    /// </summary>
    internal sealed class NotesRig
    {
        public readonly ParamValues V = new ParamValues(Params.Table);
        public readonly NotesEngine E;
        public readonly int B;
        public long Pos;
        public readonly List<(long T, NotesEvent Ev)> Sent = new();
        public int MaxPerBlock;
        public int OrderViolations;
        readonly List<(long T, NotesEvent Ev)> queue = new();
        int head;

        public NotesRig(int block = 64, Action<ParamValues> setup = null, double samplesPerBeat = 28800)
        {
            B = block;
            setup?.Invoke(V);
            E = new NotesEngine(V);
            E.SetSampleRate(48000);
            E.SamplesPerBeat = samplesPerBeat;
        }

        public void Raw(long t, NotesEvent e)
        {
            int i = queue.Count;
            while (i > head && queue[i - 1].T > t) i--;       // stable: after the events already given for this sample
            queue.Insert(i, (t, e));
        }

        public void On(long t, int note, int vel = 100, int ch = 0) => Raw(t, NotesEvent.NoteOn(0, ch, note, vel));
        public void Off(long t, int note, int ch = 0) => Raw(t, NotesEvent.NoteOff(0, ch, note));

        /// <summary>A note: on at <paramref name="t"/>, off <paramref name="len"/> samples later.</summary>
        public void Note(long t, long len, int note, int vel = 100, int ch = 0) { On(t, note, vel, ch); Off(t + len, note, ch); }

        /// <summary>Runs whole blocks until the clock has passed <paramref name="until"/>.</summary>
        public void Run(long until)
        {
            while (Pos < until)
            {
                long end = Pos + B;
                while (head < queue.Count && queue[head].T < end)
                {
                    var (t, e) = queue[head++];
                    e.Offset = (int)Math.Max(0, t - Pos);
                    E.Input(e);
                }
                if (head > 4096) { queue.RemoveRange(0, head); head = 0; }
                var span = E.Process(B);
                MaxPerBlock = Math.Max(MaxPerBlock, span.Length);
                int last = 0;
                foreach (var o in span)
                {
                    if (o.Offset < last) OrderViolations++;
                    last = o.Offset;
                    Sent.Add((Pos + o.Offset, o));
                }
                Pos = end;
            }
        }

        public IEnumerable<(long T, int Note, int Vel)> Ons => Sent.Where(s => s.Ev.Kind == NotesEvent.On).Select(s => (s.T, (int)s.Ev.Data1, s.Ev.Data2));
        public IEnumerable<(long T, int Note)> Offs => Sent.Where(s => s.Ev.Kind == NotesEvent.Off).Select(s => (s.T, (int)s.Ev.Data1));
    }

    /// <summary>The phrase the tests and the listening pack play: two bars of 4/4 (eight beats), single notes and chords, varied velocities and lengths.</summary>
    internal static class NotesPhrase
    {
        public const int Rate = 48000;
        public const double Bpm = 100;
        public const double SamplesPerBeat = Rate * 60.0 / Bpm;       // 28800
        public const double Beats = 8;

        /// <summary>(beat, length in beats, notes, velocity).</summary>
        public static readonly (double Beat, double Len, int[] Notes, int Vel)[] Items =
        {
            (0.0, 0.5, new[] { 60 }, 100),
            (0.5, 0.5, new[] { 64 }, 70),
            (1.0, 1.0, new[] { 67 }, 110),
            (2.0, 1.5, new[] { 60, 64, 67 }, 90),
            (3.5, 0.25, new[] { 69 }, 60),
            (3.75, 0.25, new[] { 71 }, 120),
            (4.0, 2.0, new[] { 53, 57, 60 }, 100),
            (5.0, 0.5, new[] { 72 }, 80),
            (5.5, 0.25, new[] { 74 }, 40),
            (6.0, 1.0, new[] { 55, 59, 62, 65 }, 110),
            (7.0, 0.5, new[] { 76 }, 95),
            (7.5, 0.25, new[] { 75 }, 85),
            (7.75, 0.25, new[] { 74 }, 100),
        };

        /// <summary>The phrase as note events at absolute samples, starting at sample <paramref name="start"/>.</summary>
        public static List<(long T, bool On, int Note, int Vel)> Events(long start = 0)
        {
            var list = new List<(long, bool, int, int)>();
            foreach (var (beat, len, notes, vel) in Items)
                foreach (int n in notes)
                {
                    list.Add((start + (long)Math.Round(beat * SamplesPerBeat), true, n, vel));
                    list.Add((start + (long)Math.Round((beat + len) * SamplesPerBeat), false, n, 0));
                }
            return list.OrderBy(e => e.Item1).ToList();
        }

        public static void Feed(NotesRig rig, long start = 0)
        {
            foreach (var (t, on, note, vel) in Events(start)) { if (on) rig.On(t, note, vel); else rig.Off(t, note); }
        }
    }
}
