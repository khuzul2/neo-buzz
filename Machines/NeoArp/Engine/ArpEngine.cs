#nullable disable
// Neo Arp's engine (docs/specs/neo-arp.md): an arpeggiator on note events. Pure C#: notes in (with the sample offsets they arrived at), notes out
// (the same), one block at a time; no allocation, no locks. The machine (NeoArp.cs) turns note-wire events into ArpEvents and back.
//
// Time. A step happens at a time in the engine's "domain": the song's position in beats while the Timing is Song tempo, a sample count while it is
// Free time. A block says where it starts in the song (beats), how many samples a beat is (constant inside a block: the host never lets a block
// cross a tick, and the tempo changes at ticks), and whether the transport plays. Step s of a pattern that started at T0 happens at
//     T0 + (s + (s odd ? swing : 0)) * stepLength
// and becomes the sample offset round((time - blockStart) / unitsPerSample). Notes, their offs, the humanized lateness and the bar lines are all
// scheduled in the same domain, so a tempo change, whatever it is, never moves a step off the grid and never leaves a note out of its length.
//
// Patterns are stateless functions of the step number s (and, for the Steps pattern, of the number of sounding steps before it), so a loop jump
// or a start in the middle of a bar finds the figure where the song position says it is. Only Random walk keeps a position (reset by a restart).

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Arp
{
    /// <summary>Same numbering as BuzzGUI.Interfaces.NoteMessageKind (the machine casts).</summary>
    internal enum ArpKind : byte { NoteOn, NoteOff, Controller, PitchBend, Pressure }

    /// <summary>One event in or out: Offset is samples from the start of the block.</summary>
    internal struct ArpEvent
    {
        public int Offset;
        public ArpKind Kind;
        public byte Channel;
        public byte Data1;
        public int Data2;

        public static ArpEvent On(int offset, int channel, int note, int velocity) => new ArpEvent { Offset = offset, Kind = ArpKind.NoteOn, Channel = (byte)(channel & 15), Data1 = (byte)(note & 127), Data2 = Math.Clamp(velocity, 1, 127) };
        public static ArpEvent Off(int offset, int channel, int note) => new ArpEvent { Offset = offset, Kind = ArpKind.NoteOff, Channel = (byte)(channel & 15), Data1 = (byte)(note & 127) };
    }

    /// <summary>What the host says about one block.</summary>
    internal struct ArpBlock
    {
        public int Samples;
        /// <summary>The transport plays. While it does not, nothing is sent.</summary>
        public bool Playing;
        /// <summary>The song position at the block's first sample, in beats (quarter notes).</summary>
        public double Beat;
        /// <summary>Samples in a beat at this block's tempo.</summary>
        public double SamplesPerBeat;
        /// <summary>Beats in a bar (4 unless the song says otherwise).</summary>
        public double BeatsPerBar;
    }

    internal sealed class ArpEngine
    {
        public const int OutputCapacity = 4096;
        const int Reserve = 300;                    // room kept for the offs of everything that can be sounding (128) and the passed-through events
        const int PendingCapacity = 512;
        public const int Steps = 16;

        public static readonly int PatternUp = 0, PatternDown = 1, PatternUpDown = 2, PatternUpDownTwice = 3, PatternDownUp = 4, PatternConverge = 5,
            PatternDiverge = 6, PatternAsPlayed = 7, PatternRandom = 8, PatternRandomWalk = 9, PatternChord = 10, PatternSteps = 11;

        public const int StepOff = 0, StepOn = 1, StepTie = 2, StepAccent = 3;

        /// <summary>Step lengths in beats for the Rate choices: 1/1 … 1/64.</summary>
        public static readonly double[] RateBeats = { 4, 2, 1, 0.5, 0.25, 0.125, 0.0625 };

        readonly ParamValues v;
        int rate = 48000;

        /// <summary>What the last <see cref="Process"/> sent: events in time order, offsets inside the block.</summary>
        public readonly ArpEvent[] Out = new ArpEvent[OutputCapacity];
        public int OutCount;

        // -------------------------------------------------------------------------------------------------------------- held notes
        readonly bool[] down = new bool[128];       // keys physically held
        readonly bool[] held = new bool[128];       // the notes the pattern plays (keys held, or latched)
        readonly long[] order = new long[128];
        readonly int[] heldVel = new int[128];
        readonly int[] heldCh = new int[128];
        int physCount, heldCount;
        long orderCounter;
        int heldVersion = 1, cacheVersion;
        bool lastLatch;

        // The held notes as lists, rebuilt when they change.
        readonly int[] sortedPitch = new int[128], sortedVel = new int[128], sortedCh = new int[128];
        readonly int[] arrival = new int[128];      // sorted indices in order of arrival
        readonly int[] cycle = new int[256];        // the pattern's cycle as sorted indices
        int sortedCount, cycleLength;
        int cachePattern = -1;
        readonly int[] passOct = new int[8];
        int passCount;
        int cacheOctaves = -1, cacheOctMode = -1;

        // -------------------------------------------------------------------------------------------------------------- sounding notes
        readonly bool[] actOn = new bool[128];
        readonly double[] actOff = new double[128];
        readonly int[] actCh = new int[128];
        readonly int[] actList = new int[128];
        int actCount;

        // Notes delayed by Humanize time.
        readonly double[] penTime = new double[PendingCapacity], penOff = new double[PendingCapacity];
        readonly int[] penNote = new int[PendingCapacity], penVel = new int[PendingCapacity], penCh = new int[PendingCapacity];
        int penCount;

        // -------------------------------------------------------------------------------------------------------------- the clock
        bool wasPlaying, lastSynced;
        double freeNow;
        double expectedBeat;
        double t0;
        long nextS;
        long nextBar;
        double stepLen, swing;
        double pos0, unit, beat0, spb, bpb;
        int n;
        Rng rng = new Rng(0x41525001u);
        int gridSig = int.MinValue;
        long walkGroup = -1;
        int walkPos;
        volatile bool panic;
        volatile int current = -1;

        // -------------------------------------------------------------------------------------------------------------- settings of this block
        int pattern, octaves, octMode, repeats, transpose, velMode, fixedVel, accentLevel, retrig, stepCount;
        double gate, humTimeSamples, humVel;
        bool synced, latch;

        public ArpEngine(ParamValues values)
        {
            v = values;
        }

        public void SetSampleRate(int hz) { rate = Math.Max(8000, hz); }

        /// <summary>The step (0 … 15) the Steps row is at while playing, or -1 while stopped.</summary>
        public int CurrentStep => current;

        /// <summary>The number of keys held (or latched).</summary>
        public int HeldCount => heldCount;

        /// <summary>The notes sounding now.</summary>
        public int ActiveCount => actCount;

        /// <summary>Asks for every sounding note to be released and every held note forgotten, at the start of the next block (the Panic command).</summary>
        public void RequestPanic() { panic = true; }

        /// <summary>Back to the state of a new machine (not on the audio thread).</summary>
        public void Reset()
        {
            Array.Clear(down); Array.Clear(held); Array.Clear(actOn);
            physCount = heldCount = actCount = penCount = 0;
            heldVersion++;
            wasPlaying = false;
            rng = new Rng(0x41525001u);
            current = -1;
            gridSig = int.MinValue;
        }

        // ================================================================================================================ one block

        public void Process(in ArpBlock block, ReadOnlySpan<ArpEvent> input)
        {
            OutCount = 0;
            n = block.Samples;
            if (n <= 0) return;
            ReadSettings();
            spb = block.SamplesPerBeat > 1 ? block.SamplesPerBeat : rate * 0.5;
            bpb = block.BeatsPerBar >= 1 ? block.BeatsPerBar : 4;
            beat0 = block.Beat;

            if (panic)
            {
                panic = false;
                DoPanic(0);
            }
            if (latch != lastLatch)
            {
                lastLatch = latch;
                if (!latch) ReleaseLatched(0);
            }

            if (!block.Playing)
            {
                if (wasPlaying)
                {
                    FlushActive(0);
                    penCount = 0;
                    wasPlaying = false;
                    current = -1;
                }
                // Keys are still followed (so a held chord plays when the transport starts), but nothing is sent.
                for (int i = 0; i < input.Length; i++) TrackOnly(input[i]);
                return;
            }

            unit = synced ? 1.0 / spb : 1.0;
            ComputeGrid();
            bool reacquire = false;
            if (!wasPlaying)
            {
                wasPlaying = true;
                freeNow = 0;
                rng = new Rng(0x41525001u);
                penCount = 0;
                reacquire = true;
            }
            else if (synced != lastSynced)
            {
                FlushActive(0);
                penCount = 0;
                freeNow = 0;
                reacquire = true;
            }
            else if (synced && Math.Abs(beat0 - expectedBeat) > 4.0 / spb)
            {
                FlushActive(0);                       // the song jumped (a loop, a locate): nothing may be left sounding
                penCount = 0;
                reacquire = true;
            }
            else if (gridSig != Signature())
            {
                reacquire = true;
            }
            lastSynced = synced;
            pos0 = synced ? beat0 : freeNow;
            if (reacquire) Reacquire();
            gridSig = Signature();

            Merge(input);

            freeNow += n;
            expectedBeat = beat0 + n / spb;
        }

        int Signature()
        {
            int h = synced ? 1 : 0;
            h = h * 31 + v.Choice(P.Rate);
            h = h * 31 + v.Choice(P.Feel);
            h = h * 8191 + (int)v.Value(P.FreeMs);
            h = h * 131 + (int)v.Value(P.Swing);
            h = h * 7 + retrig;
            return h;
        }

        void ReadSettings()
        {
            pattern = Math.Clamp(v.Choice(P.Pattern), 0, 11);
            octaves = Math.Clamp((int)v.Value(P.Octaves), 1, 4);
            octMode = Math.Clamp(v.Choice(P.OctMode), 0, 2);
            repeats = Math.Clamp((int)v.Value(P.Repeats), 1, 4);
            transpose = (int)v.Value(P.Transpose);
            velMode = Math.Clamp(v.Choice(P.VelMode), 0, 2);
            fixedVel = Math.Clamp((int)v.Value(P.FixedVel), 1, 127);
            accentLevel = Math.Clamp((int)v.Value(P.AccentLevel), 0, 100);
            retrig = Math.Clamp(v.Choice(P.Retrigger), 0, 2);
            stepCount = Math.Clamp((int)v.Value(P.StepLen), 1, Steps);
            gate = Math.Clamp(v.Value(P.Gate), 5, 200) / 100.0;
            humTimeSamples = Math.Clamp(v.Value(P.HumTime), 0, 20) * 0.001 * rate;
            humVel = Math.Clamp(v.Value(P.HumVel), 0, 30) / 100.0;
            synced = v.Choice(P.Sync) == 0;
            latch = v.Value(P.Latch) > 0;
        }

        void ComputeGrid()
        {
            if (synced)
            {
                double beats = RateBeats[Math.Clamp(v.Choice(P.Rate), 0, RateBeats.Length - 1)];
                int feel = v.Choice(P.Feel);
                stepLen = feel == 1 ? beats * 1.5 : feel == 2 ? beats * 2.0 / 3.0 : beats;
            }
            else stepLen = Math.Clamp(v.Value(P.FreeMs), 20, 2000) * 0.001 * rate;
            swing = Math.Clamp(v.Value(P.Swing), 0, 75) / 100.0;
        }

        /// <summary>The time of step <paramref name="s"/> (domain units).</summary>
        double StepTime(long s) => t0 + (s + ((s & 1) != 0 ? swing : 0.0)) * stepLen;

        /// <summary>The first point of the grid (multiples of the step length) at or after <paramref name="t"/>.</summary>
        double CeilToGrid(double t) => Math.Ceiling(t / stepLen - 1e-9) * stepLen;

        /// <summary>A start, a jump or a change of the grid: where the next step is.</summary>
        void Reacquire()
        {
            t0 = retrig == 0 ? 0 : CeilToGrid(pos0 - 0.5 * unit);
            nextS = FirstStepAtOrAfter(pos0 - 0.5 * unit);
            nextBar = (long)Math.Ceiling(beat0 / bpb - 1e-9);
            walkGroup = -1;
        }

        long FirstStepAtOrAfter(double t)
        {
            long s = Math.Max(0, (long)Math.Floor((t - t0) / stepLen) - 2);
            while (StepTime(s) < t) s++;
            return s;
        }

        /// <summary>The pattern starts over at the next step on the grid at or after the sample <paramref name="k"/> of this block.</summary>
        void RestartAt(int k)
        {
            t0 = CeilToGrid(pos0 + (k - 0.5) * unit);
            nextS = 0;
            walkGroup = -1;
        }

        // ================================================================================================================ the merge

        const int PrioOff = 0, PrioBar = 1, PrioInput = 2, PrioStep = 3, PrioPending = 4;

        static int Key(double x, int n) => x < n - 0.5 ? Math.Max(0, (int)Math.Floor(x + 0.5)) : int.MaxValue;

        void Merge(ReadOnlySpan<ArpEvent> input)
        {
            int ii = 0;
            while (true)
            {
                int kIn = ii < input.Length ? Math.Clamp(input[ii].Offset, 0, n - 1) : int.MaxValue;
                int kStep = Key((StepTime(nextS) - pos0) / unit, n);
                int kBar = retrig == 2 ? Key((nextBar * bpb - beat0) * spb, n) : int.MaxValue;
                int kOff = int.MaxValue, offAt = -1;
                for (int a = 0; a < actCount; a++)
                {
                    int p = actList[a];
                    int k = Key((actOff[p] - pos0) / unit, n);
                    if (k < kOff) { kOff = k; offAt = p; }
                }
                int kPen = int.MaxValue, penAt = -1;
                for (int i = 0; i < penCount; i++)
                {
                    int k = Key((penTime[i] - pos0) / unit, n);
                    if (k < kPen) { kPen = k; penAt = i; }
                }
                int best = kOff, prio = PrioOff;
                if (kBar < best) { best = kBar; prio = PrioBar; }
                if (kIn < best) { best = kIn; prio = PrioInput; }
                if (kStep < best) { best = kStep; prio = PrioStep; }
                if (kPen < best) { best = kPen; prio = PrioPending; }
                if (best == int.MaxValue) break;
                switch (prio)
                {
                    case PrioOff:
                        EmitOff(best, offAt);
                        break;
                    case PrioBar:
                        nextBar++;
                        RestartAt(best);
                        break;
                    case PrioInput:
                        OnInput(input[ii], best);
                        ii++;
                        break;
                    case PrioStep:
                    {
                        long s = nextS++;
                        PlayStep(s, best);
                        break;
                    }
                    default:
                        if (penAt >= 0)
                        {
                            EmitOn(best, penNote[penAt], penVel[penAt], penCh[penAt], penOff[penAt]);
                            penCount--;
                            penTime[penAt] = penTime[penCount]; penOff[penAt] = penOff[penCount];
                            penNote[penAt] = penNote[penCount]; penVel[penAt] = penVel[penCount]; penCh[penAt] = penCh[penCount];
                        }
                        break;
                }
            }
        }

        // ================================================================================================================ input

        void OnInput(in ArpEvent e, int k)
        {
            switch (e.Kind)
            {
                case ArpKind.NoteOn:
                    HandleOn(e.Data1, e.Data2, e.Channel, k);
                    break;
                case ArpKind.NoteOff:
                    HandleOff(e.Data1, k);
                    break;
                case ArpKind.Controller when e.Data1 == 123 || e.Data1 == 120:
                    DoPanic(k);
                    break;
                default:
                    if (OutCount < OutputCapacity - Reserve / 2)
                    {
                        var o = e;
                        o.Offset = k;
                        Out[OutCount++] = o;
                    }
                    break;
            }
        }

        /// <summary>Keeps the held notes while the transport is stopped.</summary>
        void TrackOnly(in ArpEvent e)
        {
            if (e.Kind == ArpKind.NoteOn) HandleOn(e.Data1, e.Data2, e.Channel, 0, restart: false);
            else if (e.Kind == ArpKind.NoteOff) HandleOff(e.Data1, 0);
            else if (e.Kind == ArpKind.Controller && (e.Data1 == 123 || e.Data1 == 120)) DoPanic(0);
        }

        void HandleOn(int note, int vel, int ch, int k, bool restart = true)
        {
            note &= 127;
            if (latch && physCount == 0 && heldCount > 0)
            {
                // A new chord replaces the latched one.
                Array.Clear(held);
                heldCount = 0;
            }
            if (!down[note]) { down[note] = true; physCount++; }
            if (!held[note]) { held[note] = true; heldCount++; order[note] = ++orderCounter; }
            heldVel[note] = Math.Clamp(vel, 1, 127);
            heldCh[note] = ch;
            heldVersion++;
            if (restart && retrig == 1) RestartAt(k);
        }

        void HandleOff(int note, int k)
        {
            note &= 127;
            if (down[note]) { down[note] = false; physCount--; }
            if (!latch && held[note])
            {
                held[note] = false;
                heldCount--;
                heldVersion++;
                if (heldCount == 0 && wasPlaying) FlushActive(k);
            }
        }

        /// <summary>Latch switched off: only the keys really held stay.</summary>
        void ReleaseLatched(int k)
        {
            for (int i = 0; i < 128; i++)
                if (held[i] && !down[i]) { held[i] = false; heldCount--; }
            heldVersion++;
            if (heldCount == 0 && wasPlaying) FlushActive(k);
        }

        void DoPanic(int k)
        {
            Array.Clear(down); Array.Clear(held);
            physCount = heldCount = 0;
            heldVersion++;
            FlushActive(k);
            penCount = 0;
        }

        // ================================================================================================================ output

        void EmitOn(int k, int note, int vel, int ch, double offTime)
        {
            if (actOn[note]) EmitOff(k, note);                  // the same key again while it sounds: off, then on
            if (OutCount >= OutputCapacity - Reserve) return;
            Out[OutCount++] = ArpEvent.On(k, ch, note, vel);
            actOn[note] = true;
            actOff[note] = offTime;
            actCh[note] = ch;
            actList[actCount++] = note;
        }

        void EmitOff(int k, int note)
        {
            if (!actOn[note]) return;
            Out[OutCount++] = ArpEvent.Off(k, actCh[note], note);
            actOn[note] = false;
            for (int a = 0; a < actCount; a++)
                if (actList[a] == note) { actList[a] = actList[--actCount]; break; }
        }

        /// <summary>A note-off for everything sounding, at sample <paramref name="k"/>.</summary>
        void FlushActive(int k)
        {
            while (actCount > 0) EmitOff(k, actList[actCount - 1]);
        }

        // ================================================================================================================ patterns

        /// <summary>The held notes sorted by pitch and by arrival, the octave passes and the pattern's cycle.</summary>
        void Rebuild()
        {
            if (cacheVersion != heldVersion)
            {
                int m = 0;
                for (int p = 0; p < 128; p++)
                    if (held[p]) { sortedPitch[m] = p; sortedVel[m] = heldVel[p]; sortedCh[m] = heldCh[p]; m++; }
                sortedCount = m;
                // Arrival order: insertion sort of the sorted indices by their order stamp.
                for (int i = 0; i < m; i++)
                {
                    int idx = i;
                    int j = i;
                    while (j > 0 && order[sortedPitch[arrival[j - 1]]] > order[sortedPitch[idx]]) { arrival[j] = arrival[j - 1]; j--; }
                    arrival[j] = idx;
                }
                cacheVersion = heldVersion;
                cachePattern = -1;
            }
            if (cacheOctaves != octaves || cacheOctMode != octMode)
            {
                cacheOctaves = octaves; cacheOctMode = octMode;
                int q = 0;
                if (octMode == 0) for (int o = 0; o < octaves; o++) passOct[q++] = o;
                else if (octMode == 1) for (int o = octaves - 1; o >= 0; o--) passOct[q++] = o;
                else
                {
                    for (int o = 0; o < octaves; o++) passOct[q++] = o;
                    for (int o = octaves - 2; o >= 1; o--) passOct[q++] = o;
                }
                passCount = q;
            }
            if (cachePattern != pattern)
            {
                cachePattern = pattern;
                BuildCycle();
            }
        }

        void BuildCycle()
        {
            int m = sortedCount, c = 0;
            if (m == 0) { cycleLength = 0; return; }
            if (pattern == PatternUp || pattern == PatternSteps) for (int i = 0; i < m; i++) cycle[c++] = i;
            else if (pattern == PatternDown) for (int i = m - 1; i >= 0; i--) cycle[c++] = i;
            else if (pattern == PatternUpDown)
            {
                for (int i = 0; i < m; i++) cycle[c++] = i;
                for (int i = m - 2; i >= 1; i--) cycle[c++] = i;
            }
            else if (pattern == PatternUpDownTwice)
            {
                for (int i = 0; i < m; i++) cycle[c++] = i;
                for (int i = m - 1; i >= 0; i--) cycle[c++] = i;
            }
            else if (pattern == PatternDownUp)
            {
                for (int i = m - 1; i >= 0; i--) cycle[c++] = i;
                for (int i = 1; i <= m - 2; i++) cycle[c++] = i;
            }
            else if (pattern == PatternConverge)
            {
                for (int d = 0; d < m; d++) cycle[c++] = (d & 1) == 0 ? d / 2 : m - 1 - d / 2;
            }
            else if (pattern == PatternDiverge)
            {
                int lo = (m - 1) / 2, hi = m / 2;
                for (int d = 0; d < m; d++)
                {
                    if (lo - d >= 0) cycle[c++] = lo - d;
                    if (hi + d < m && hi + d != lo - d) cycle[c++] = hi + d;
                    if (c >= m) break;
                }
            }
            else if (pattern == PatternAsPlayed) for (int i = 0; i < m; i++) cycle[c++] = arrival[i];
            else for (int i = 0; i < m; i++) cycle[c++] = i;     // the pool patterns and Chord use the sorted notes directly
            cycleLength = c;
        }

        // The notes of one step.
        readonly int[] stepPitch = new int[128], stepVel = new int[128], stepCh = new int[128];
        int stepNotes;

        static uint Hash(long g, uint seed)
        {
            ulong x = (ulong)g * 0x9E3779B97F4A7C15UL + seed;
            x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 27; x *= 0x94D049BB133111EBUL;
            x ^= x >> 31;
            return (uint)(x >> 16);
        }

        int StepKind(int i) => Math.Clamp(v.Choice(P.S1 + i), 0, 3);
        int StepTranspose(int i) => (int)v.Value(P.X1 + i);

        /// <summary>The number of sounding steps (On or Accent) before step <paramref name="s"/> of the row, counting from the start of the row's first run.</summary>
        long SoundingBefore(long s)
        {
            int len = stepCount;
            long cycles = s / len;
            int within = (int)(s % len);
            int per = 0, prefix = 0;
            for (int i = 0; i < len; i++)
            {
                int kd = StepKind(i);
                bool sounding = kd == StepOn || kd == StepAccent;
                if (i < within && sounding) prefix++;
                if (sounding) per++;
            }
            return cycles * per + prefix;
        }

        void AddNote(int basePitch, int octaveShift, int extra, int vel, int ch, bool accent)
        {
            int pitch = basePitch + 12 * octaveShift + transpose + extra;
            if (pitch < 0 || pitch > 127) return;
            int vv = velMode == 0 ? vel : fixedVel;
            if (velMode == 2 && accent) vv = fixedVel + (int)Math.Round((127 - fixedVel) * accentLevel / 100.0);
            stepPitch[stepNotes] = pitch;
            stepVel[stepNotes] = Math.Clamp(vv, 1, 127);
            stepCh[stepNotes] = ch;
            stepNotes++;
        }

        void PlayStep(long s, int k)
        {
            int len = stepCount;
            current = (int)(s % len);
            if (heldCount == 0) return;
            Rebuild();
            if (sortedCount == 0) return;
            int m = sortedCount, r = repeats;
            stepNotes = 0;
            int tie = 0;
            bool accent = false;
            int extra = 0;
            long jj = s;
            if (pattern == PatternSteps)
            {
                int i = current;
                int kd = StepKind(i);
                if (kd != StepOn && kd != StepAccent) return;
                accent = kd == StepAccent;
                extra = StepTranspose(i);
                jj = SoundingBefore(s);
                while (tie < len - 1 && StepKind((i + 1 + tie) % len) == StepTie) tie++;
            }
            if (pattern == PatternRandom)
            {
                int pool = m * octaves;
                int idx = (int)(Hash(s / r, 0x52414E44u) % (uint)pool);
                AddNote(sortedPitch[idx % m], idx / m, 0, sortedVel[idx % m], sortedCh[idx % m], false);
            }
            else if (pattern == PatternRandomWalk)
            {
                int pool = m * octaves;
                long g = s / r;
                if (walkGroup < 0) { walkGroup = g; walkPos = pool / 2; }
                else
                    while (walkGroup < g)
                    {
                        walkGroup++;
                        if (pool > 1)
                        {
                            int dir = (rng.NextUInt() & 0x10000) != 0 ? 1 : -1;
                            if (walkPos + dir < 0 || walkPos + dir > pool - 1) dir = -dir;
                            walkPos += dir;
                        }
                    }
                walkPos = Math.Clamp(walkPos, 0, pool - 1);
                AddNote(sortedPitch[walkPos % m], walkPos / m, 0, sortedVel[walkPos % m], sortedCh[walkPos % m], false);
            }
            else if (pattern == PatternChord)
            {
                long period = (long)passCount * r;
                int pass = (int)((jj % period) / r);
                for (int i = 0; i < m; i++) AddNote(sortedPitch[i], passOct[pass], 0, sortedVel[i], sortedCh[i], false);
            }
            else
            {
                int c = cycleLength;
                if (c == 0) return;
                long period = (long)c * passCount * r;
                long idx = jj % period;
                int pass = (int)(idx / ((long)c * r));
                int ci = (int)((idx % ((long)c * r)) / r);
                int si = cycle[ci];
                AddNote(sortedPitch[si], passOct[pass], extra, sortedVel[si], sortedCh[si], accent);
            }
            if (stepNotes == 0) return;

            double on = StepTime(s);
            double gridOff = StepTime(s + tie) + gate * stepLen;
            for (int j = 0; j < stepNotes; j++)
            {
                int vel = stepVel[j];
                double delay = 0;
                if (humTimeSamples > 0) delay = rng.NextUnipolar() * humTimeSamples * unit;
                if (humVel > 0) vel = Math.Clamp((int)Math.Round(vel * (1 + humVel * rng.NextBipolar())), 1, 127);
                if (delay <= 0.5 * unit) EmitOn(k, stepPitch[j], vel, stepCh[j], gridOff + delay);
                else if (penCount < PendingCapacity)
                {
                    penTime[penCount] = on + delay; penOff[penCount] = gridOff + delay;
                    penNote[penCount] = stepPitch[j]; penVel[penCount] = vel; penCh[penCount] = stepCh[j];
                    penCount++;
                }
            }
        }
    }
}
