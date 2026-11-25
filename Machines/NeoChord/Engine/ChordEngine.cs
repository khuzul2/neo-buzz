#nullable disable
// Neo Chord (docs/specs/neo-chord.md): the note engine. Notes in (sorted by sample offset within the block), chords out at their own sample offsets.
// Time is counted in samples from the start (a long), so a strum's delays carry across blocks and land on the exact sample.
//
//  * A note on starts a SOURCE: the chord is built at once (ChordBuilder) from the parameters as they are then, the strum order and delays are fixed,
//    and the voices are stored with the source: the pitches actually sent, so a parameter changed while the note is held never changes what its off does.
//  * A voice with no delay starts at once; one with a delay waits (pending) and starts when its sample comes, in this block or a later one.
//  * A note off (or a new note on for the same key, or the transport stopping, or controller 123) ENDS the source: every started voice is released, every
//    voice not yet started is cancelled and is never sent.
//  * A pitch is counted per channel: a voice that starts adds one, one that ends takes one off, and the note on goes out when the count becomes 1 and the
//    note off when it becomes 0, so two chords that share a note keep it sounding until the last of them ends and every note on has exactly one note off.
//  * Notes that arrive with chord Off pass through the same way (one voice, the note, its velocity), so nothing is sent but what its source sent.
// No allocation after the constructor; no locks (the audio thread owns it; the machine hands it events just before Work).

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Chord
{
    /// <summary>A note event as the engine sees it (the machine converts to and from the host's NoteMessage). Kind is the host's: 0 on, 1 off, 2 controller, 3 pitch bend, 4 pressure.</summary>
    internal struct ChordEvent
    {
        public int Offset;
        public byte Kind;
        public byte Channel;
        public byte Data1;
        public int Data2;

        public const byte On = 0, Off = 1;

        public static ChordEvent NoteOn(int offset, int channel, int note, int velocity) => new ChordEvent { Offset = offset, Kind = On, Channel = (byte)(channel & 15), Data1 = (byte)(note & 127), Data2 = Math.Clamp(velocity, 1, 127) };
        public static ChordEvent NoteOff(int offset, int channel, int note) => new ChordEvent { Offset = offset, Kind = Off, Channel = (byte)(channel & 15), Data1 = (byte)(note & 127) };
    }

    internal sealed class ChordEngine
    {
        public const int MaxVoices = 8, MaxSources = 256, InputCapacity = 4096, OutputCapacity = 8192;

        sealed class Source
        {
            public bool Active;
            public byte Channel, Note;
            public int Count;
            public long Seq;
            public readonly long[] Start = new long[MaxVoices];
            public readonly byte[] Pitch = new byte[MaxVoices];
            public readonly byte[] Velocity = new byte[MaxVoices];
            /// <summary>0 waiting to start, 1 sounding, 2 over (ended or cancelled).</summary>
            public readonly byte[] State = new byte[MaxVoices];
        }

        /// <summary>For the tests: told of every voice that starts or ends (channel, the source's note, the pitch sent, started, the sample, the source's number).</summary>
        internal delegate void VoiceTrace(int channel, int source, int pitch, bool started, long t, long seq);
        internal VoiceTrace Voice;

        readonly ParamValues v;
        readonly Source[] sources = new Source[MaxSources];
        readonly short[] slotOf = new short[16 * 128];
        readonly int[] free = new int[MaxSources];
        int freeCount;
        readonly int[] active = new int[MaxSources];       // slot numbers in the order their notes began
        int activeCount;
        int pending;                                       // voices waiting to start

        readonly int[] refs = new int[16 * 128];           // started voices per channel and pitch
        readonly int[] pitchRefs = new int[128];           // the same over all channels, for the editor's keyboard

        readonly ChordEvent[] input = new ChordEvent[InputCapacity];
        int inputCount;
        readonly ChordEvent[] output = new ChordEvent[OutputCapacity];
        int outputCount;

        readonly int[] built = new int[ChordBuilder.MaxNotes + 1];
        readonly int[] order = new int[ChordBuilder.MaxNotes + 1];
        readonly int[] rank = new int[ChordBuilder.MaxNotes + 1];

        long now;                  // the first sample of the block being processed
        long seq;
        int rate = 48000;
        bool nextDown;             // Alternate: the next chord's direction
        bool panic;
        uint rng;
        int seededWith = -1;

        public ChordEngine(ParamValues values)
        {
            v = values;
            for (int i = 0; i < MaxSources; i++) { sources[i] = new Source(); free[i] = MaxSources - 1 - i; }
            freeCount = MaxSources;
            Array.Fill(slotOf, (short)-1);
            Seed();
        }

        public void SetSampleRate(int sampleRate) => rate = Math.Max(8000, sampleRate);

        /// <summary>Events that did not fit (input, sources, output): 0 in any sane use.</summary>
        public long Dropped { get; private set; }

        /// <summary>Notes on that went out and notes off that went out (all channels), for the tests and the editor.</summary>
        public long SentOn { get; private set; }
        public long SentOff { get; private set; }

        /// <summary>The notes of the last chord begun, as a bit mask (bit n of Low is note n, of High is note 64 + n): the editor's keyboard lights them.</summary>
        public ulong LastLow, LastHigh;
        /// <summary>The sample position of the start of the next block.</summary>
        public long Position => now;

        public bool IsSounding(int pitch) => (uint)pitch < 128 && pitchRefs[pitch] > 0;

        public int HeldSources => activeCount;

        // ---------------------------------------------------------------------------------------------------------------- input

        /// <summary>Queues an event for the next <see cref="Process"/> (the offsets of one block are in order, or are put in order there).</summary>
        public void Input(in ChordEvent e)
        {
            if (inputCount >= InputCapacity) { Dropped++; return; }
            input[inputCount++] = e;
        }

        /// <summary>Releases everything at the start of the next block (the transport stopped, the song stopped, panic).</summary>
        public void Panic() => panic = true;

        /// <summary>Forgets everything without sending anything (a render starts from nothing), and starts the random stream again.</summary>
        public void Reset()
        {
            for (int i = 0; i < MaxSources; i++) { sources[i].Active = false; free[i] = MaxSources - 1 - i; }
            freeCount = MaxSources;
            activeCount = 0;
            pending = 0;
            Array.Fill(slotOf, (short)-1);
            Array.Clear(refs);
            Array.Clear(pitchRefs);
            inputCount = 0;
            outputCount = 0;
            now = 0;
            nextDown = false;
            panic = false;
            LastLow = LastHigh = 0;
            seededWith = -1;
            Seed();
        }

        // ---------------------------------------------------------------------------------------------------------------- the block

        /// <summary>
        /// Handles the queued events of a block of <paramref name="n"/> samples and the voices that fall due in it; the events to send are in the returned span
        /// (valid until the next call), in order of offset.
        /// </summary>
        public ReadOnlySpan<ChordEvent> Process(int n)
        {
            outputCount = 0;
            SortInput();
            long end = now + n;
            if (panic)
            {
                panic = false;
                EndAll(now);
            }
            for (int i = 0; i < inputCount; i++)
            {
                ref var e = ref input[i];
                long t = now + Math.Clamp(e.Offset, 0, Math.Max(0, n - 1));
                FlushDue(t);
                switch (e.Kind)
                {
                    case ChordEvent.On: Begin(e.Channel, e.Data1, e.Data2, t); break;
                    case ChordEvent.Off: End(e.Channel, e.Data1, t); break;
                    default:
                        if (e.Kind == 2 && (e.Data1 == 123 || e.Data1 == 120)) EndAll(t);      // all notes off, all sound off
                        Emit(e.Kind, e.Channel, e.Data1, e.Data2, t);
                        break;
                }
            }
            inputCount = 0;
            FlushDue(end);
            now = end;
            return new ReadOnlySpan<ChordEvent>(output, 0, outputCount);
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

        /// <summary>Starts every waiting voice whose sample is before <paramref name="limit"/>, earliest first.</summary>
        void FlushDue(long limit)
        {
            while (pending > 0)
            {
                long best = long.MaxValue;
                int bs = -1, bv = -1;
                for (int a = 0; a < activeCount; a++)
                {
                    var s = sources[active[a]];
                    for (int k = 0; k < s.Count; k++)
                        if (s.State[k] == 0 && s.Start[k] < limit && s.Start[k] < best) { best = s.Start[k]; bs = active[a]; bv = k; }
                }
                if (bs < 0) return;
                var src = sources[bs];
                src.State[bv] = 1;
                pending--;
                Sound(src, bv, best);
            }
        }

        void Sound(Source s, int k, long t)
        {
            int idx = s.Channel * 128 + s.Pitch[k];
            if (refs[idx]++ == 0) { Emit(ChordEvent.On, s.Channel, s.Pitch[k], s.Velocity[k], t); SentOn++; }
            pitchRefs[s.Pitch[k]]++;
            Voice?.Invoke(s.Channel, s.Note, s.Pitch[k], true, t, s.Seq);
        }

        void Release(Source s, int k, long t)
        {
            int idx = s.Channel * 128 + s.Pitch[k];
            if (--refs[idx] == 0) { Emit(ChordEvent.Off, s.Channel, s.Pitch[k], 0, t); SentOff++; }
            pitchRefs[s.Pitch[k]]--;
            Voice?.Invoke(s.Channel, s.Note, s.Pitch[k], false, t, s.Seq);
        }

        void Emit(byte kind, int channel, int data1, int data2, long t)
        {
            if (outputCount >= OutputCapacity) { Dropped++; return; }
            ref var o = ref output[outputCount++];
            o.Offset = (int)(t - now);
            o.Kind = kind;
            o.Channel = (byte)channel;
            o.Data1 = (byte)data1;
            o.Data2 = data2;
        }

        // ---------------------------------------------------------------------------------------------------------------- sources

        void End(int channel, int note, long t)
        {
            int slot = slotOf[channel * 128 + note];
            if (slot < 0) return;
            var s = sources[slot];
            for (int k = 0; k < s.Count; k++)
            {
                if (s.State[k] == 1) Release(s, k, t);
                else if (s.State[k] == 0) pending--;
                s.State[k] = 2;
            }
            s.Active = false;
            slotOf[channel * 128 + note] = -1;
            int p = 0;
            while (active[p] != slot) p++;
            Array.Copy(active, p + 1, active, p, activeCount - p - 1);
            activeCount--;
            free[freeCount++] = slot;
        }

        void EndAll(long t)
        {
            while (activeCount > 0)
            {
                var s = sources[active[0]];
                End(s.Channel, s.Note, t);
            }
        }

        void Begin(int channel, int note, int velocity, long t)
        {
            if (slotOf[channel * 128 + note] >= 0) End(channel, note, t);     // the same key again: the old chord ends first
            if (freeCount == 0) { Dropped++; return; }
            ReadSettings(out var settings, out int strum, out double strumMs, out double curve, out double humMs, out double humVel, out int seed, out double top, out double bass, out double tilt);
            if (seed != seededWith) { seededWith = seed; Seed(); }

            int count = ChordBuilder.Build(settings, note, built, out _);
            if (count == 0) return;

            // The order the notes start in, and how far through the chord each one is.
            for (int i = 0; i < count; i++) order[i] = i;
            bool down = false;
            switch (strum)
            {
                case 1: break;
                case 2: down = true; break;
                case 3: down = nextDown; nextDown = !nextDown; break;
                case 4:
                    for (int i = count - 1; i > 0; i--) { int j = (int)(Next() % (uint)(i + 1)); (order[i], order[j]) = (order[j], order[i]); }
                    break;
            }
            for (int i = 0; i < count; i++)
            {
                if (strum != 4) { rank[i] = down ? count - 1 - i : i; continue; }
                for (int j = 0; j < count; j++) if (order[j] == i) rank[i] = j;
            }
            bool passThrough = settings.Chord == ChordKind.Off;      // Off: the note itself, on its own sample at its own velocity

            int slot = free[--freeCount];
            var src = sources[slot];
            src.Active = true;
            src.Channel = (byte)channel;
            src.Note = (byte)note;
            src.Count = count;
            src.Seq = seq++;
            slotOf[channel * 128 + note] = (short)slot;
            active[activeCount++] = slot;

            double strumSamples = strum == 0 ? 0 : strumMs * 0.001 * rate;
            ulong low = 0, high = 0;
            for (int i = 0; i < count; i++)
            {
                // Voice i in the chord (lowest first) starts rank[i]-th: its delay is the strum's curve at that point of the chord.
                double pos = count > 1 ? rank[i] / (double)(count - 1) : 0;
                long delay = strum == 0 ? 0 : (long)Math.Round(Curve(pos, curve) * strumSamples);
                uint r1 = Next(), r2 = Next();
                if (passThrough) delay = 0;
                else if (humMs > 0) delay += (long)(r1 * (1.0 / 4294967296.0) * humMs * 0.001 * rate);

                double p = count > 1 ? i / (double)(count - 1) : 0.5;
                double scale = 1 + tilt * (p - 0.5);
                if (count > 1 && i == count - 1) scale *= top;
                if (count > 1 && i == 0) scale *= bass;
                if (humVel > 0) scale *= 1 + (r2 * (2.0 / 4294967296.0) - 1) * humVel;
                int vel = passThrough ? Math.Clamp(velocity, 1, 127) : Math.Clamp((int)Math.Round(velocity * scale), 1, 127);

                src.Pitch[i] = (byte)built[i];
                src.Velocity[i] = (byte)vel;
                src.Start[i] = t + delay;
                src.State[i] = 0;
                pending++;
                if (built[i] < 64) low |= 1UL << built[i]; else high |= 1UL << (built[i] - 64);
            }
            LastLow = low;
            LastHigh = high;
            // The voices with no delay begin now (the ones before them in time were started by FlushDue already).
            for (int i = 0; i < count; i++)
                if (src.Start[i] <= t) { src.State[i] = 1; pending--; Sound(src, i, t); }
        }

        /// <summary>The share of the strum time that has passed when the note at <paramref name="pos"/> (0 first … 1 last) starts: even at 0, accelerating above, slowing below.</summary>
        public static double Curve(double pos, double curve)
        {
            if (curve == 0) return pos;
            // A blend of the straight line and the fourth power (no libm: the audio paths use plain arithmetic only): the share is x at 0 and x^4 at ±100 %.
            double a = Math.Abs(curve), x = curve > 0 ? 1 - pos : pos, x2 = x * x;
            double share = (1 - a) * x + a * x2 * x2;
            return curve > 0 ? 1 - share : share;
        }

        void ReadSettings(out ChordSettings s, out int strum, out double strumMs, out double curve, out double humMs, out double humVel, out int seed, out double top, out double bass, out double tilt)
        {
            s = new ChordSettings
            {
                Chord = v.Choice(P.Chord),
                Diatonic = v.Choice(P.Mode) == 1,
                Key = v.Choice(P.Key),
                Scale = v.Choice(P.Scale),
                Sevenths = v.Choice(P.DiaSize) == 1,
                Custom1 = Interval(P.Custom1), Custom2 = Interval(P.Custom2), Custom3 = Interval(P.Custom3), Custom4 = Interval(P.Custom4),
                Inversion = (int)v.Value(P.Inversion),
                Spread = v.Choice(P.Spread),
                Octave = (int)v.Value(P.Octave),
                Bass = v.Choice(P.Bass),
                Voices = (int)v.Value(P.Voices),
            };
            strum = v.Choice(P.Strum);
            strumMs = v.Value(P.StrumTime);
            curve = v.Value(P.StrumCurve);
            humMs = v.Value(P.HumTime);
            humVel = v.Value(P.HumVel) * 0.01;
            seed = (int)v.Value(P.Seed);
            top = v.Value(P.VelTop) * 0.01;
            bass = v.Value(P.VelBass) * 0.01;
            tilt = v.Value(P.VelSpread);
        }

        /// <summary>A Custom interval parameter: its value is the choice (0 Off, 1 … 49 for −24 … +24).</summary>
        int Interval(int index)
        {
            int c = v.Choice(index);
            return c == 0 ? ChordSettings.Off : c - 25;
        }

        // ---------------------------------------------------------------------------------------------------------------- random (xorshift32)

        void Seed()
        {
            int seed = (int)v.Value(P.Seed);
            seededWith = seed;
            rng = 0x9E3779B9u * (uint)(seed + 1) + 0x7F4A7C15u;
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
