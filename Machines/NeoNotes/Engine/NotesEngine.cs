#nullable disable
// Neo Notes (docs/specs/neo-notes.md): the note engine. Notes in (sorted by sample offset within the block), changed notes out at their own sample offsets.
// Time is counted in samples from the start (a long), so a delay, an echo or a fixed length carries across blocks and lands on the exact sample.
//
//  * A note on starts a SOURCE. Everything about it is decided at once, from the parameters as they are then: the pitch (Transpose, Scale, Spread), the
//    velocity, the random delay, the length rule and the echo. The pitch actually sent is kept with the source, so a parameter changed while the note is held
//    never changes what its note off does.
//  * Everything that is sent goes through ONE timeline (a heap ordered by sample, then by the order it was queued): note on, note off, the next repeat of an
//    echo, and the controllers that pass through. So order is kept, a note off never overtakes its note on, and a block that has too much to send (the wire
//    holds 1024 events a block) leaves the rest on the timeline for the next block instead of dropping it.
//  * A pitch is counted per channel, as Neo Chord counts it: a voice that starts adds one, one that ends takes one off, and the note off goes out when the count
//    becomes 0. So overlapping copies of a pitch (an echo over its own note) never stick. Unlike Neo Chord, a voice that starts on a pitch that is already
//    sounding strikes it again (a note off and a note on on the same sample): an echo repeat or a late-humanized note must be heard, not swallowed.
//  * Every note on that goes out has its note off queued with it (Fixed length, the maximum of Clamp, every echo repeat) or when its source ends (As played,
//    Clamp): causal, never earlier than the note on. Transport stop, the Stop command and controllers 123 and 120 release everything and clear the timeline.
//  * The queues keep a reserve for note offs (the note queue reserve of docs/specs/notes.md G5): a flood of note ons is refused first, counted, never a note off.
// No allocation after the constructor; no locks (the audio thread owns it; the machine hands it events just before Work).

using System;
using NeoBuzz.Chord;
using NeoBuzz.Dsp;

namespace NeoBuzz.Notes
{
    /// <summary>A note event as the engine sees it (the machine converts to and from the host's NoteMessage). Kind is the host's: 0 on, 1 off, 2 controller, 3 pitch bend, 4 pressure.</summary>
    internal struct NotesEvent
    {
        public int Offset;
        public byte Kind;
        public byte Channel;
        public byte Data1;
        public int Data2;

        public const byte On = 0, Off = 1;

        public static NotesEvent NoteOn(int offset, int channel, int note, int velocity) => new NotesEvent { Offset = offset, Kind = On, Channel = (byte)(channel & 15), Data1 = (byte)(note & 127), Data2 = Math.Clamp(velocity, 1, 127) };
        public static NotesEvent NoteOff(int offset, int channel, int note) => new NotesEvent { Offset = offset, Kind = Off, Channel = (byte)(channel & 15), Data1 = (byte)(note & 127) };
    }

    internal sealed class NotesEngine
    {
        public const int MaxSources = 2048;      // every key on every channel can be held at once
        /// <summary>The timeline: its last <see cref="QueueReserve"/> places are for the note offs a source needs when it ends.</summary>
        public const int QueueCapacity = 32768, QueueReserve = 4096;
        /// <summary>Events waiting for the next block: the last <see cref="InputReserve"/> places are for note offs only.</summary>
        public const int InputCapacity = 4096, InputReserve = 256;
        /// <summary>The most a block sends (a note wire holds 1024 events a block); the rest waits for the next block.</summary>
        public const int WireLimit = 1024;
        const int OutputCapacity = 4096;

        // The beats in each Rate choice (1/1 … 1/64), and the Feel factors (Straight, Dotted, Triplet).
        public static readonly double[] RateBeats = { 4, 2, 1, 0.5, 0.25, 0.125, 0.0625 };
        public static readonly double[] FeelFactor = { 1, 1.5, 2.0 / 3.0 };

        const byte TOnMain = 0, TOffMain = 1, TOnVoice = 2, TOffVoice = 3, TRepeat = 4, TPass = 5;
        const long Unknown = long.MinValue;

        struct Timed
        {
            public long Time, Seq;
            public int Source, Arg;
            public byte Type, Channel, Pitch, Vel;
        }

        sealed class Source
        {
            public int Slot;
            public bool Held;
            public byte Channel, Note;
            public int Outstanding;          // events on the timeline that name this source
            /// <summary>0 waiting to start, 1 sounding, 2 over.</summary>
            public byte MainState;
            public byte MainPitch, MainVel;
            public long Start, T0, PlannedEnd;
            public long Delay, MinSamples, MaxSamples;
            public int LenMode;
            // The echo, as it was when the note began.
            public int Repeats, NextRepeat, Step;
            public double EchoSamples, EchoVel, Drop, EchoLength;
            public bool HumOn;
            public double HumSamples, HumVel;
        }

        readonly ParamValues v;
        readonly Source[] sources = new Source[MaxSources];
        readonly short[] slotOf = new short[16 * 128];
        readonly int[] free = new int[MaxSources];
        int freeCount;

        readonly Timed[] heap = new Timed[QueueCapacity];
        int qCount;
        long qSeq;

        readonly int[] refs = new int[16 * 128];          // started voices per channel and pitch
        readonly int[] pitchRefs = new int[128];          // the same over all channels, for the editor
        readonly bool[] inKey = new bool[16 * 128];        // keys held on the input, for the editor
        readonly int[] inPitch = new int[128];

        readonly NotesEvent[] input = new NotesEvent[InputCapacity];
        int inputCount;
        readonly NotesEvent[] output = new NotesEvent[OutputCapacity];
        int outputCount;

        long now;                  // the first sample of the block being processed
        int blockSamples;
        int rate = 48000;
        double samplesPerBeat = 24000;
        bool panic, panicResets;
        uint rng;
        int seededWith = -1;
        int spreadStep;

        public NotesEngine(ParamValues values)
        {
            v = values;
            for (int i = 0; i < MaxSources; i++) { sources[i] = new Source { Slot = i }; free[i] = MaxSources - 1 - i; }
            freeCount = MaxSources;
            Array.Fill(slotOf, (short)-1);
            Seed();
        }

        public void SetSampleRate(int sampleRate) => rate = Math.Max(8000, sampleRate);

        /// <summary>Samples in a beat at the song's tempo (the machine sets it every block; 120 beats a minute until it does).</summary>
        public double SamplesPerBeat
        {
            get => samplesPerBeat;
            set => samplesPerBeat = value > 1 ? value : rate * 0.5;
        }

        /// <summary>Events that did not fit (input, timeline, sources): 0 in any sane use. Never a note off.</summary>
        public long Dropped { get; private set; }

        /// <summary>Notes on that went out and notes off that went out (all channels), for the tests and the editor.</summary>
        public long SentOn { get; private set; }
        public long SentOff { get; private set; }

        /// <summary>The sample position of the start of the next block.</summary>
        public long Position => now;

        /// <summary>Events on the timeline (waiting to be sent), for the tests.</summary>
        public int Pending => qCount;

        /// <summary>The most events the timeline held at once, for the load test.</summary>
        public int PeakPending { get; private set; }

        /// <summary>Note ons that are sounding (counted per channel and pitch).</summary>
        public int SoundingPitches
        {
            get
            {
                int n = 0;
                for (int i = 0; i < refs.Length; i++) if (refs[i] > 0) n++;
                return n;
            }
        }

        /// <summary>Sources in use (held, or still echoing): 0 when everything has ended.</summary>
        public int ActiveSources => MaxSources - freeCount;

        public bool IsSounding(int pitch) => (uint)pitch < 128 && pitchRefs[pitch] > 0;
        public bool IsHeld(int pitch) => (uint)pitch < 128 && inPitch[pitch] > 0;

        // ---------------------------------------------------------------------------------------------------------------- input

        /// <summary>Queues an event for the next <see cref="Process"/> (the offsets of one block are in order, or are put in order there). A note off always finds room.</summary>
        public void Input(in NotesEvent e)
        {
            if (inputCount >= InputCapacity || (e.Kind != NotesEvent.Off && inputCount >= InputCapacity - InputReserve)) { Dropped++; return; }
            input[inputCount++] = e;
        }

        /// <summary>Releases everything at the start of the next block (the transport stopped, the song stopped, panic) and starts the Spread pattern again.</summary>
        public void Panic()
        {
            panic = true;
            panicResets = true;
        }

        /// <summary>Forgets everything without sending anything (a render starts from nothing), and starts the random stream and the Spread pattern again.</summary>
        public void Reset()
        {
            for (int i = 0; i < MaxSources; i++) { sources[i].Held = false; free[i] = MaxSources - 1 - i; }
            freeCount = MaxSources;
            Array.Fill(slotOf, (short)-1);
            Array.Clear(refs);
            Array.Clear(pitchRefs);
            Array.Clear(inKey);
            Array.Clear(inPitch);
            qCount = 0;
            inputCount = 0;
            outputCount = 0;
            now = 0;
            panic = false;
            spreadStep = 0;
            seededWith = -1;
            Seed();
        }

        // ---------------------------------------------------------------------------------------------------------------- the block

        /// <summary>
        /// Handles the queued events of a block of <paramref name="n"/> samples and what falls due on the timeline in it; the events to send are in the returned span
        /// (valid until the next call), in order of offset.
        /// </summary>
        public ReadOnlySpan<NotesEvent> Process(int n)
        {
            outputCount = 0;
            blockSamples = n;
            SortInput();
            long end = now + n;
            if (panic)
            {
                panic = false;
                bool reset = panicResets;
                panicResets = false;
                PanicAll(now, reset);
            }
            for (int i = 0; i < inputCount; i++)
            {
                ref var e = ref input[i];
                long t = now + Math.Clamp(e.Offset, 0, Math.Max(0, n - 1));
                Flush(t);
                switch (e.Kind)
                {
                    case NotesEvent.On: Begin(e.Channel, e.Data1, e.Data2, t); break;
                    case NotesEvent.Off: End(e.Channel, e.Data1, t); break;
                    default:
                        if (e.Kind == 2 && (e.Data1 == 123 || e.Data1 == 120)) PanicAll(t, false);      // all notes off, all sound off
                        PushPass(e.Kind, e.Channel, e.Data1, e.Data2, t);
                        break;
                }
                Flush(t);
            }
            inputCount = 0;
            Flush(end - 1);
            now = end;
            return new ReadOnlySpan<NotesEvent>(output, 0, outputCount);
        }

        void SortInput()
        {
            for (int i = 1; i < inputCount; i++)
                if (input[i].Offset < input[i - 1].Offset)
                {
                    // Stable insertion sort (the rare case: legacy MIDI events at offset 0 arrive after the wires').
                    for (int a = 1; a < inputCount; a++)
                    {
                        var x = input[a];
                        int b = a - 1;
                        while (b >= 0 && input[b].Offset > x.Offset) { input[b + 1] = input[b]; b--; }
                        input[b + 1] = x;
                    }
                    return;
                }
        }

        // ---------------------------------------------------------------------------------------------------------------- the timeline

        static bool Less(in Timed a, in Timed b) => a.Time < b.Time || (a.Time == b.Time && a.Seq < b.Seq);

        void Push(Timed e)
        {
            if (qCount >= QueueCapacity) { Dropped++; return; }       // cannot happen: every push above checked its room, and the note offs have the reserve
            e.Seq = qSeq++;
            int i = qCount++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (!Less(e, heap[p])) break;
                heap[i] = heap[p];
                i = p;
            }
            heap[i] = e;
            if (qCount > PeakPending) PeakPending = qCount;
        }

        Timed Pop()
        {
            var top = heap[0];
            var last = heap[--qCount];
            if (qCount == 0) return top;
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= qCount) break;
                int r = l + 1;
                int m = r < qCount && Less(heap[r], heap[l]) ? r : l;
                if (!Less(heap[m], last)) break;
                heap[i] = heap[m];
                i = m;
            }
            heap[i] = last;
            return top;
        }

        /// <summary>Whether a timeline place is free: <paramref name="reserve"/> is true for a note off that must not be refused.</summary>
        bool Room(int places, bool reserve) => qCount + places <= (reserve ? QueueCapacity : QueueCapacity - QueueReserve);

        void PushPass(byte kind, int channel, int data1, int data2, long t)
        {
            if (!Room(1, false)) { Dropped++; return; }
            Push(new Timed { Time = t, Type = TPass, Channel = (byte)channel, Pitch = (byte)data1, Vel = kind, Arg = data2 });
        }

        /// <summary>Sends what is due on the timeline up to and including sample <paramref name="limit"/>, earliest first, as far as the block has room.</summary>
        void Flush(long limit)
        {
            while (qCount > 0 && heap[0].Time <= limit)
            {
                if (outputCount + 2 > WireLimit) return;       // the rest waits for the next block (a note struck again sends two events)
                var e = Pop();
                Execute(in e);
            }
        }

        void Execute(in Timed e)
        {
            long t = e.Time;
            switch (e.Type)
            {
                case TOnMain:
                {
                    var s = sources[e.Source];
                    if (s.MainState == 0) { s.MainState = 1; Sound(s.Channel, s.MainPitch, s.MainVel, t); }
                    Done(s);
                    break;
                }
                case TOffMain:
                {
                    var s = sources[e.Source];
                    if (s.MainState == 1) Release(s.Channel, s.MainPitch, t);
                    if (s.MainState != 2) { s.MainState = 2; if (s.PlannedEnd == Unknown || t < s.PlannedEnd) s.PlannedEnd = t; }
                    Done(s);
                    break;
                }
                case TOnVoice: Sound(e.Channel, e.Pitch, e.Vel, t); break;
                case TOffVoice: Release(e.Channel, e.Pitch, t); break;
                case TRepeat:
                {
                    var s = sources[e.Source];
                    Repeat(s, e.Source, t);
                    Done(s);
                    break;
                }
                case TPass: Emit(e.Vel, e.Channel, e.Pitch, e.Arg, t); break;
            }
        }

        void Done(Source s)
        {
            s.Outstanding--;
            TryFree(s);
        }

        void TryFree(Source s)
        {
            if (s.Held || s.Outstanding > 0) return;
            free[freeCount++] = s.Slot;
        }

        void Sound(int channel, int pitch, int velocity, long t)
        {
            int idx = channel * 128 + pitch;
            if (refs[idx]++ > 0) { Emit(NotesEvent.Off, channel, pitch, 0, t); SentOff++; }      // already sounding (an echo over its note, a late note on over an early off): struck again
            Emit(NotesEvent.On, channel, pitch, velocity, t);
            SentOn++;
            pitchRefs[pitch]++;
        }

        void Release(int channel, int pitch, long t)
        {
            int idx = channel * 128 + pitch;
            if (refs[idx] <= 0) return;
            if (--refs[idx] == 0) { Emit(NotesEvent.Off, channel, pitch, 0, t); SentOff++; }
            pitchRefs[pitch]--;
        }

        void Emit(byte kind, int channel, int data1, int data2, long t)
        {
            if (outputCount >= OutputCapacity) { Dropped++; return; }
            ref var o = ref output[outputCount++];
            long off = t - now;
            o.Offset = (int)Math.Clamp(off, 0, Math.Max(0, blockSamples - 1));
            o.Kind = kind;
            o.Channel = (byte)channel;
            o.Data1 = (byte)data1;
            o.Data2 = data2;
        }

        // ---------------------------------------------------------------------------------------------------------------- panic

        /// <summary>Every sounding pitch ends at <paramref name="t"/>, the timeline is cleared and every source forgotten.</summary>
        void PanicAll(long t, bool resetPattern)
        {
            for (int i = 0; i < refs.Length; i++)
                if (refs[i] > 0)
                {
                    Emit(NotesEvent.Off, i >> 7, i & 127, 0, t);
                    SentOff++;
                    refs[i] = 0;
                }
            Array.Clear(pitchRefs);
            Array.Clear(inKey);
            Array.Clear(inPitch);
            qCount = 0;
            for (int i = 0; i < MaxSources; i++) { sources[i].Held = false; sources[i].Outstanding = 0; free[i] = MaxSources - 1 - i; }
            freeCount = MaxSources;
            Array.Fill(slotOf, (short)-1);
            if (resetPattern) spreadStep = 0;
        }

        // ---------------------------------------------------------------------------------------------------------------- sources

        void End(int channel, int note, long t)
        {
            int key = channel * 128 + note;
            if (inKey[key]) { inKey[key] = false; inPitch[note]--; }
            int slot = slotOf[key];
            if (slot < 0) return;
            var s = sources[slot];
            s.Held = false;
            slotOf[key] = -1;
            if (s.MainState != 2 && s.LenMode != 1)
            {
                // As played: the note off follows the source's, as late as the note on was. Clamp: not before the minimum, and not after the maximum.
                long e = t + s.Delay;
                if (s.LenMode == 2) e = Math.Min(Math.Max(e, s.Start + s.MinSamples), s.Start + s.MaxSamples);
                if (e < s.Start) e = s.Start;
                s.PlannedEnd = e;
                s.Outstanding++;
                Push(new Timed { Time = e, Type = TOffMain, Source = slot });
            }
            TryFree(s);
        }

        void Begin(int channel, int note, int velocity, long t)
        {
            int key = channel * 128 + note;
            if (slotOf[key] >= 0 || inKey[key]) End(channel, note, t);     // the same key again: the old note ends first
            inKey[key] = true;
            inPitch[note]++;

            if (seed() != seededWith) { seededWith = seed(); Seed(); }
            // Four draws a note, always: switching a section on or off never moves the others' random numbers.
            uint rSpread = Next(), rVel = Next(), rTime = Next(), rHumVel = Next();

            // ---- pitch: Transpose, Scale, Spread (a note pushed outside 0 … 127 is dropped, with its note off)
            int pitch = note;
            if (On(P.TransOn))
            {
                pitch += (int)v.Value(P.Trans);
                if ((uint)pitch > 127) return;
            }
            if (On(P.ScaleOn))
            {
                pitch = Snap(pitch, v.Choice(P.Key), v.Choice(P.Scale), v.Choice(P.ScaleMode));
                if (pitch < 0) return;
            }
            if (On(P.SpreadOn))
            {
                int range = (int)v.Value(P.SpreadRange);
                int shift = 0;
                if (range > 0)
                {
                    int step = spreadStep++;
                    switch (v.Choice(P.SpreadPat))
                    {
                        case 0: shift = step % (range + 1); break;
                        case 1: shift = -(step % (range + 1)); break;
                        case 2: { int m = step % (2 * range); shift = m <= range ? m : 2 * range - m; break; }
                        default: shift = (int)(rSpread % (uint)(2 * range + 1)) - range; break;
                    }
                }
                pitch += 12 * shift;
                if ((uint)pitch > 127) return;
            }

            // ---- velocity
            double vel = Math.Clamp(velocity, 1, 127);
            if (On(P.VelOn))
            {
                if (v.Value(P.VelFixedOn) > 0) vel = v.Value(P.VelFixed);
                else
                {
                    double lo = v.Value(P.OutMin), hi = Math.Max(lo, v.Value(P.OutMax));
                    double y0 = (vel - 1) / 126.0;
                    double y = y0 + v.Value(P.VelCurve) * y0 * (1 - y0);      // 0 and 1 stay put; minus bows it down (soft), plus up (hard)
                    vel = lo + y * (hi - lo);
                }
                double rnd = v.Value(P.VelRandom) * 0.01;
                if (rnd > 0) vel *= 1 + (rVel * (2.0 / 4294967296.0) - 1) * rnd;
                vel = Math.Clamp(Math.Round(vel), 1, 127);
            }
            double velPre = vel;

            // ---- humanize: late, never early; the same lateness ends the note, so its length is kept
            bool hum = On(P.HumOn);
            double humSamples = hum ? v.Value(P.HumTime) * 0.001 * rate : 0;
            double humVel = hum ? v.Value(P.HumVel) * 0.01 : 0;
            long delay = hum ? (long)(rTime * (1.0 / 4294967296.0) * humSamples) : 0;
            if (hum && humVel > 0) vel = Math.Clamp(Math.Round(vel * (1 + (rHumVel * (2.0 / 4294967296.0) - 1) * humVel)), 1, 127);

            // ---- length
            int lenMode = 0;
            long fixedSamples = 0, minS = 0, maxS = 0;
            if (On(P.LenOn))
            {
                lenMode = v.Choice(P.LenMode);
                if (lenMode == 1) fixedSamples = Math.Max(1, (long)Math.Round(v.Choice(P.LenSync) == 0 ? Beats(v.Choice(P.LenRate), v.Choice(P.LenFeel)) * samplesPerBeat : v.Value(P.LenMs) * 0.001 * rate));
                else if (lenMode == 2)
                {
                    minS = (long)Math.Round(v.Value(P.ClampMin) * 0.001 * rate);
                    maxS = Math.Max(minS + 1, (long)Math.Round(v.Value(P.ClampMax) * 0.001 * rate));
                }
            }

            // ---- echo
            int repeats = 0;
            double echoSamples = 0;
            if (On(P.EchoOn))
            {
                repeats = (int)v.Value(P.Repeats);
                echoSamples = Math.Max(1.0, v.Choice(P.EchoSync) == 0 ? Beats(v.Choice(P.EchoRate), v.Choice(P.EchoFeel)) * samplesPerBeat : v.Value(P.EchoMs) * 0.001 * rate);
            }

            if (freeCount == 0 || !Room(4, false)) { Dropped++; return; }
            int slot = free[--freeCount];
            var src = sources[slot];
            src.Held = true;
            src.Channel = (byte)channel;
            src.Note = (byte)note;
            src.Outstanding = 0;
            src.MainState = 0;
            src.MainPitch = (byte)pitch;
            src.MainVel = (byte)vel;
            src.T0 = t;
            src.Delay = delay;
            src.Start = t + delay;
            src.PlannedEnd = Unknown;
            src.LenMode = lenMode;
            src.MinSamples = minS;
            src.MaxSamples = maxS;
            src.Repeats = repeats;
            src.NextRepeat = 1;
            src.EchoSamples = echoSamples;
            src.EchoVel = velPre;
            src.Drop = v.Value(P.EchoDrop) * 0.01;
            src.Step = (int)v.Value(P.EchoStep);
            src.EchoLength = v.Value(P.EchoLen) * 0.01;
            src.HumOn = hum;
            src.HumSamples = humSamples;
            src.HumVel = humVel;
            slotOf[key] = (short)slot;

            src.Outstanding++;
            Push(new Timed { Time = src.Start, Type = TOnMain, Source = slot });
            if (lenMode == 1)
            {
                src.PlannedEnd = src.Start + fixedSamples;
                src.Outstanding++;
                Push(new Timed { Time = src.PlannedEnd, Type = TOffMain, Source = slot });
            }
            else if (lenMode == 2)
            {
                src.Outstanding++;
                Push(new Timed { Time = src.Start + maxS, Type = TOffMain, Source = slot });
            }
            if (repeats > 0)
            {
                src.Outstanding++;
                Push(new Timed { Time = t + (long)Math.Round(echoSamples), Type = TRepeat, Source = slot });
            }
        }

        /// <summary>Repeat number <c>NextRepeat</c> of an echo, at its own time <paramref name="tNominal"/> (the grid the note began on; the humanize delay is added to it).</summary>
        void Repeat(Source s, int slot, long tNominal)
        {
            int k = s.NextRepeat;
            int pitch = s.MainPitch + k * s.Step;
            s.EchoVel *= 1 - s.Drop;
            if ((uint)pitch > 127 || Math.Round(s.EchoVel) < 1) return;        // the chain ends: it only goes further away
            if (!Room(3, false)) { Dropped++; return; }

            uint rTime = Next(), rVel = Next();
            long delay = s.HumOn ? (long)(rTime * (1.0 / 4294967296.0) * s.HumSamples) : 0;
            double vel = s.EchoVel;
            if (s.HumOn && s.HumVel > 0) vel *= 1 + (rVel * (2.0 / 4294967296.0) - 1) * s.HumVel;
            int velocity = (int)Math.Clamp(Math.Round(vel), 1, 127);

            // The repeat lasts a share of the note's length, and never longer than the time between repeats. A note still held when the repeat starts is at least that long.
            double span = s.EchoSamples;
            if (s.PlannedEnd != Unknown) span = Math.Min(span, Math.Max(0, s.PlannedEnd - s.Start));
            long len = Math.Max(1, (long)Math.Round(s.EchoLength * span));
            long start = tNominal + delay;
            Push(new Timed { Time = start, Type = TOnVoice, Channel = s.Channel, Pitch = (byte)pitch, Vel = (byte)velocity });
            Push(new Timed { Time = start + len, Type = TOffVoice, Channel = s.Channel, Pitch = (byte)pitch });
            if (k < s.Repeats)
            {
                s.NextRepeat = k + 1;
                s.Outstanding++;
                Push(new Timed { Time = s.T0 + (long)Math.Round((k + 1) * s.EchoSamples), Type = TRepeat, Source = slot });
            }
        }

        // ---------------------------------------------------------------------------------------------------------------- the pitch rules

        bool On(int p) => v.Value(p) > 0;

        int seed() => (int)v.Value(P.Seed);

        /// <summary>The length of a Rate and Feel choice in beats.</summary>
        public static double Beats(int rate, int feel) => RateBeats[Math.Clamp(rate, 0, RateBeats.Length - 1)] * FeelFactor[Math.Clamp(feel, 0, FeelFactor.Length - 1)];

        /// <summary>The twelve notes of a key and scale as a bit mask (bit n is the note n semitones above C). Scale 0 is Chromatic, 1 … 9 are Neo Chord's nine scales.</summary>
        public static int ScaleMask(int key, int scale)
        {
            if (scale <= 0) return 0xFFF;
            var steps = ChordTheory.Scales[Math.Clamp(scale - 1, 0, ChordTheory.Scales.Length - 1)];
            int mask = 0;
            for (int i = 0; i < steps.Length; i++) mask |= 1 << ((steps[i] + key) % 12);
            return mask;
        }

        /// <summary>
        /// Moves <paramref name="pitch"/> into the scale: itself when it is in, else (mode 0) the nearest scale note, a tie going down, (1) the next one up, (2) the next one down,
        /// or (3) nothing. Returns -1 for a note that is dropped (Filter, or no scale note on the keyboard that way).
        /// </summary>
        public static int Snap(int pitch, int key, int scale, int mode)
        {
            int mask = ScaleMask(key, scale);
            if ((mask >> (pitch % 12) & 1) != 0) return pitch;
            switch (mode)
            {
                case 1:
                    for (int d = 1; d <= 12; d++) { int q = pitch + d; if (q > 127) return -1; if ((mask >> (q % 12) & 1) != 0) return q; }
                    return -1;
                case 2:
                    for (int d = 1; d <= 12; d++) { int q = pitch - d; if (q < 0) return -1; if ((mask >> (q % 12) & 1) != 0) return q; }
                    return -1;
                case 0:
                    for (int d = 1; d <= 12; d++)
                    {
                        int lo = pitch - d, hi = pitch + d;
                        if (lo >= 0 && (mask >> (lo % 12) & 1) != 0) return lo;
                        if (hi <= 127 && (mask >> (hi % 12) & 1) != 0) return hi;
                    }
                    return -1;
                default: return -1;
            }
        }

        // ---------------------------------------------------------------------------------------------------------------- random (xorshift32)

        void Seed()
        {
            int s = seed();
            seededWith = s;
            rng = 0x9E3779B9u * (uint)(s + 1) + 0x7F4A7C15u;
            if (rng == 0) rng = 1;
            for (int i = 0; i < 4; i++) Next();
        }

        uint Next()
        {
            uint x = rng;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return rng = x;
        }
    }
}
