// A stand-in for the host, for Neo Arp's tests (docs/specs/neo-arp.md §9): ticks of whole samples (the carry of the fraction as the engine's work loop does),
// blocks that never cross a tick, the transport, tempo changes at ticks and jumps in the song position. It feeds the engine the way the machine does and
// logs everything the engine sends with the sample it falls on and the song position (in beats) it falls at.
using NeoBuzz.Arp;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    internal sealed class ArpRig
    {
        internal struct Rec
        {
            public long At;               // absolute sample
            public ArpKind Kind;
            public int Note, Vel, Channel;
            public double Beat;           // the song position it falls at
            public bool Playing;
        }

        public readonly ParamValues V = new ParamValues(Params.Table);
        public readonly ArpEngine E;
        public int Rate = 48000, Tpb = 4, Block = 64;
        /// <summary>The tempo of a tick (read when the tick starts).</summary>
        public Func<long, double> Bpm = _ => 120.0;
        public bool Playing = true;
        public long Tick;
        public long Abs;
        public readonly List<Rec> Log = new();
        public readonly Dictionary<long, (long Start, int Length)> TickInfo = new();
        /// <summary>Called before every block (to change parameters the way automation does).</summary>
        public Action BeforeBlock;
        /// <summary>The number of events the engine sent in each block with the transport stopped (first block, then the rest).</summary>
        public int SentWhileStopped, OnsWhileStopped;

        readonly List<(long At, ArpEvent Event)> inputs = new();
        int pos, spt;
        double rem;
        bool prevPlaying = true;

        public ArpRig(int rate = 48000)
        {
            Rate = rate;
            E = new ArpEngine(V);
            E.SetSampleRate(rate);
        }

        public static string Name(int p) => Params.Table[p].Name;

        /// <summary>Sets a parameter by its physical value (the table's mapping; an enum by its index).</summary>
        public ArpRig Set(int p, double value)
        {
            V.Set(p, Params.Table[p].Raw(value));
            return this;
        }

        public ArpRig SetRaw(int p, int raw)
        {
            V.Set(p, raw);
            return this;
        }

        public ArpRig Row(string kinds, int[] transposes = null)
        {
            // "O" off, "N" on, "T" tie, "A" accent; one letter a step.
            Set(P.StepLen, kinds.Length);
            for (int i = 0; i < Math.Min(16, kinds.Length); i++)
                Set(P.S1 + i, "ONTA".IndexOf(kinds[i]));
            if (transposes != null)
                for (int i = 0; i < Math.Min(16, transposes.Length); i++) Set(P.X1 + i, transposes[i]);
            return this;
        }

        public void NoteOn(long at, int note, int vel = 100, int channel = 0) => inputs.Add((at, ArpEvent.On(0, channel, note, vel)));
        public void NoteOff(long at, int note, int channel = 0) => inputs.Add((at, ArpEvent.Off(0, channel, note)));
        public void Input(long at, ArpEvent e) => inputs.Add((at, e));

        /// <summary>The song jumps to this tick at the next tick boundary the rig reaches.</summary>
        public void Jump(long tick)
        {
            Tick = tick;
            pos = 0;
        }

        /// <summary>The song position of an absolute sample while no jump has happened: a beat to the sample the host puts it at.</summary>
        public double BeatToAbs(double beat)
        {
            long tick = (long)Math.Floor(beat * Tpb + 1e-9);
            var info = TickInfo[tick];
            return info.Start + (beat * Tpb - tick) * info.Length;
        }

        public void Run(long samples) => RunCore(Abs + samples, long.MaxValue);

        /// <summary>Runs until the song position reaches this tick.</summary>
        public void RunTo(long tick) => RunCore(long.MaxValue, tick);

        void RunCore(long end, long endTick)
        {
            inputs.Sort((a, b) => a.At.CompareTo(b.At));
            while (Abs < end && Tick < endTick)
            {
                if (pos == 0)
                {
                    double exact = 60.0 * Rate / (Bpm(Tick) * Tpb) + rem;
                    spt = Math.Max(1, (int)Math.Floor(exact));
                    rem = Math.Clamp(exact - spt, 0, 0.999999);
                    if (Playing) TickInfo[Tick] = (Abs, spt);
                }
                int n = (int)Math.Min(Math.Min(Block, spt - pos), end - Abs);
                BeforeBlock?.Invoke();
                var evs = new List<ArpEvent>();
                int taken = 0;
                while (taken < inputs.Count && inputs[taken].At < Abs + n)
                {
                    var e = inputs[taken].Event;
                    e.Offset = (int)Math.Max(0, inputs[taken].At - Abs);
                    evs.Add(e);
                    taken++;
                }
                inputs.RemoveRange(0, taken);
                var block = new ArpBlock { Samples = n, Playing = Playing, Beat = (Tick + pos / (double)spt) / Tpb, SamplesPerBeat = (double)spt * Tpb, BeatsPerBar = 4 };
                E.Process(in block, evs.ToArray());
                for (int i = 0; i < E.OutCount; i++)
                {
                    var o = E.Out[i];
                    Assert.That(o.Offset, Is.InRange(0, n - 1), "an event's offset lies inside its block");
                    Log.Add(new Rec { At = Abs + o.Offset, Kind = o.Kind, Note = o.Data1, Vel = o.Data2, Channel = o.Channel, Beat = (Tick + (pos + o.Offset) / (double)spt) / Tpb, Playing = Playing });
                    if (!Playing)
                    {
                        // Only the stop edge's note-offs may come while the transport is stopped.
                        SentWhileStopped++;
                        if (o.Kind == ArpKind.NoteOn) OnsWhileStopped++;
                        Assert.That(prevPlaying && o.Kind == ArpKind.NoteOff, Is.True, $"sent while stopped: {o.Kind} {o.Data1} at {Abs + o.Offset}");
                    }
                }
                prevPlaying = Playing;
                Abs += n;
                if (Playing)
                {
                    pos += n;
                    if (pos >= spt) { pos = 0; Tick++; }
                }
                else
                {
                    pos += n;
                    if (pos >= spt) pos = 0;
                }
            }
        }

        public IEnumerable<Rec> Ons => Log.Where(r => r.Kind == ArpKind.NoteOn);

        /// <summary>The notes played, in order.</summary>
        public int[] OnNotes() => Ons.Select(r => r.Note).ToArray();

        /// <summary>Every note-on matched by exactly one note-off after it, nothing left sounding: returns the problems.</summary>
        public static List<string> Balance(IEnumerable<Rec> log)
        {
            var problems = new List<string>();
            var on = new Dictionary<(int, int), long>();
            foreach (var r in log)
            {
                var key = (r.Channel, r.Note);
                if (r.Kind == ArpKind.NoteOn)
                {
                    if (on.ContainsKey(key)) problems.Add($"note {r.Note} on at {r.At} while already on since {on[key]}");
                    on[key] = r.At;
                }
                else if (r.Kind == ArpKind.NoteOff)
                {
                    if (!on.Remove(key)) problems.Add($"note {r.Note} off at {r.At} without an on");
                }
            }
            foreach (var kv in on) problems.Add($"note {kv.Key.Item2} on at {kv.Value} never released");
            return problems;
        }
    }
}
