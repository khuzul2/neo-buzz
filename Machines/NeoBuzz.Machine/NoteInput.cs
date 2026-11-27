#nullable disable
// Neo machines' Buzz glue (docs/specs/machines.md §3): notes from pattern tracks and MIDI, queued
// where they arrive (ReBuzz's tick, the MIDI thread) and applied by the synth at its next block.

using System;
using System.Threading;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoBuzz.Machine
{
    internal sealed class NoteInput
    {
        public const int MidiKeyBase = 1000;
        /// <summary>The key a sample preview (the picker's hover) plays on, apart from the tracks' (0 … 15) and the MIDI notes' (1000 …).</summary>
        public const int PreviewKey = 2000;
        /// <summary>The pitch a preview plays at: Buzz C-4, the tracker convention's own speed (Neo Sampler's TrackerRoot).</summary>
        public const int PreviewNote = 48;

        // What a track note can carry besides its pitch and velocity (a sample machine's Tracker mode): set by the machine's track parameters.
        public const int ExtraSample = 0, ExtraOffset = 1, ExtraOffsetHi = 2, ExtraTune = 3, ExtraCount = 4;
        /// <summary>The Tune parameter's value for the note's own pitch (cents are value − this).</summary>
        public const int TuneCentre = 2400;
        public const int OffsetStep = 32768;
        const int DefaultVelocity = 100;

        // Track notes: written by setters during ReBuzz's tick, read at the next Work (same thread
        // in practice; the lock covers the rare call from elsewhere).
        readonly byte[] pendingNote;
        readonly int[] pendingVelocity;
        readonly bool[] hasNote;
        readonly int[] pendingExtra;          // [track * ExtraCount + kind]; -1: not set on this row
        bool anyTrack;
        readonly object trackLock = new object();

        // MIDI events: (kind, a, b) ring buffer, multiple producers under a lock, one consumer.
        // 4096 events; the last Reserve slots are for note-offs only, so a flood of note-ons can never leave a note stuck (G5, notes.md). Drops are counted.
        const int RingSize = 4096, Reserve = 256;
        readonly int[] ringKind = new int[RingSize], ringA = new int[RingSize], ringB = new int[RingSize], ringC = new int[RingSize];
        int ringWrite, ringRead;
        readonly object midiLock = new object();

        const int KindNoteOn = 1, KindNoteOff = 2, KindControl = 3, KindSample = 4;

        public NoteInput(int tracks)
        {
            Tracks = Math.Max(1, tracks);
            pendingNote = new byte[Tracks];
            pendingVelocity = new int[Tracks];
            hasNote = new bool[Tracks];
            pendingExtra = new int[Tracks * ExtraCount];
            for (int t = 0; t < Tracks; t++) pendingVelocity[t] = -1;
            Array.Fill(pendingExtra, -1);
        }

        public int Tracks { get; }

        long dropped;
        /// <summary>MIDI events dropped because the queue was full (a note-off only when it was completely full), for tests and diagnosis.</summary>
        public long Dropped => Interlocked.Read(ref dropped);

        /// <summary>A Buzz note on a track (setter). 255 is note off.</summary>
        public void SetNote(int track, byte value)
        {
            if ((uint)track >= (uint)Tracks || value == 0) return;
            lock (trackLock)
            {
                pendingNote[track] = value;
                hasNote[track] = true;
                anyTrack = true;
            }
        }

        /// <summary>The velocity for the note on the same row (setter).</summary>
        public void SetVelocity(int track, int value)
        {
            if ((uint)track >= (uint)Tracks) return;
            lock (trackLock) pendingVelocity[track] = Math.Clamp(value, 1, 127);
        }

        /// <summary>A value a track parameter besides Note and Velocity gave this row (a sample number, an offset, a tune).</summary>
        public void SetExtra(int track, int kind, int value)
        {
            if ((uint)track >= (uint)Tracks || (uint)kind >= ExtraCount) return;
            lock (trackLock) pendingExtra[track * ExtraCount + kind] = value;
        }

        /// <summary>Starts (or ends) a sample at its own speed, for the picker's hover (any thread; applied at the next block).</summary>
        public void PreviewSample(int sample, bool on) => Push(KindSample, sample, on ? 1 : 0, 0);

        /// <summary>MIDI note on/off from ReBuzz (MIDI thread). Velocity 0 is note off.</summary>
        public void MidiNote(int channel, int note, int velocity)
        {
            if (note < 0 || note > 127) return;
            Push(velocity > 0 ? KindNoteOn : KindNoteOff, note, velocity, channel);
        }

        /// <summary>
        /// MIDI controllers from ReBuzz (MIDI thread): CC numbers, plus pitch bend (0xE0 or 0xFF,
        /// 14-bit value) and channel pressure (0xD0 or 0xFC).
        /// </summary>
        public void MidiControl(int ctrl, int channel, int value)
        {
            Push(KindControl, ctrl, value, channel);
        }

        void Push(int kind, int a, int b, int channel)
        {
            lock (midiLock)
            {
                int next = (ringWrite + 1) & (RingSize - 1);
                int used = (ringWrite - Volatile.Read(ref ringRead)) & (RingSize - 1);
                bool off = kind == KindNoteOff;
                if (next == Volatile.Read(ref ringRead) || (!off && used >= RingSize - 1 - Reserve)) { dropped++; return; }   // full (or full but for the note-offs' room): drop
                ringKind[ringWrite] = kind;
                ringA[ringWrite] = a;
                ringB[ringWrite] = b;
                ringC[ringWrite] = channel;
                Volatile.Write(ref ringWrite, next);
            }
        }

        /// <summary>Events a note wire carried this block (audio thread, just before Work): queued as the MIDI events are.</summary>
        public void Receive(ReadOnlySpan<NoteMessage> events)
        {
            for (int i = 0; i < events.Length; i++)
            {
                ref readonly var e = ref events[i];
                switch (e.Kind)
                {
                    case NoteMessageKind.NoteOn: MidiNote(e.Channel, e.Data1, Math.Max(1, e.Data2)); break;
                    case NoteMessageKind.NoteOff: MidiNote(e.Channel, e.Data1, 0); break;
                    case NoteMessageKind.Controller: MidiControl(e.Data1, e.Channel, e.Data2); break;
                    case NoteMessageKind.PitchBend: MidiControl(0xE0, e.Channel, e.Data2); break;
                    case NoteMessageKind.Pressure: MidiControl(0xD0, e.Channel, e.Data2); break;
                }
            }
        }

        /// <summary>Converts a Buzz note byte to a MIDI note (A-5 = 69), or −1.</summary>
        public static int BuzzToMidi(byte value)
        {
            int semi = (value & 15) - 1;
            if (semi < 0 || semi > 11) return -1;
            int midi = (value >> 4) * 12 + semi;
            return midi >= 0 && midi <= 127 ? midi : -1;
        }

        /// <summary>Forgets everything queued (a render starts from nothing).</summary>
        public void Clear()
        {
            lock (trackLock)
            {
                anyTrack = false;
                for (int t = 0; t < Tracks; t++) { hasNote[t] = false; pendingVelocity[t] = -1; }
                Array.Fill(pendingExtra, -1);
            }
            lock (midiLock) Volatile.Write(ref ringRead, Volatile.Read(ref ringWrite));
        }

        /// <summary>Audio thread, at the start of a block: applies everything queued.</summary>
        public void Drain(INoteSink sink)
        {
            if (anyTrack)
            {
                lock (trackLock)
                {
                    anyTrack = false;
                    for (int t = 0; t < Tracks; t++)
                    {
                        int x = t * ExtraCount;
                        if (!hasNote[t])
                        {
                            pendingVelocity[t] = -1;
                            for (int k = 0; k < ExtraCount; k++) pendingExtra[x + k] = -1;
                            continue;
                        }
                        hasNote[t] = false;
                        byte b = pendingNote[t];
                        int vel = pendingVelocity[t] > 0 ? pendingVelocity[t] : DefaultVelocity;
                        pendingVelocity[t] = -1;
                        if (b == 255)
                        {
                            for (int k = 0; k < ExtraCount; k++) pendingExtra[x + k] = -1;
                            sink.NoteOff(t);
                            continue;
                        }
                        int sample = pendingExtra[x + ExtraSample];
                        int offset = Math.Max(0, pendingExtra[x + ExtraOffset]) + OffsetStep * Math.Max(0, pendingExtra[x + ExtraOffsetHi]);
                        int tune = pendingExtra[x + ExtraTune] < 0 ? 0 : pendingExtra[x + ExtraTune] - TuneCentre;
                        for (int k = 0; k < ExtraCount; k++) pendingExtra[x + k] = -1;
                        int midi = BuzzToMidi(b);
                        if (midi < 0) continue;
                        if (sample > 0 && sink is ITrackNoteSink withSample) withSample.NoteOnSample(t, midi, vel / 127f, sample, offset, tune);
                        else sink.NoteOn(t, midi, vel / 127f);
                    }
                }
            }
            int read = ringRead;
            int write = Volatile.Read(ref ringWrite);
            while (read != write)
            {
                int kind = ringKind[read], a = ringA[read], b = ringB[read], c = ringC[read];
                read = (read + 1) & (RingSize - 1);
                var ch = sink as IChannelNoteSink;
                switch (kind)
                {
                    case KindNoteOn:
                        if (ch != null) ch.NoteOn(MidiKeyBase + a, a, Math.Clamp(b, 1, 127) / 127f, Math.Clamp(c, 0, 15));
                        else sink.NoteOn(MidiKeyBase + a, a, Math.Clamp(b, 1, 127) / 127f);
                        break;
                    case KindNoteOff:
                        if (ch != null) ch.NoteOff(MidiKeyBase + a, a, Math.Clamp(c, 0, 15));
                        else sink.NoteOff(MidiKeyBase + a);
                        break;
                    case KindSample:
                        if (b > 0 && sink is ITrackNoteSink previewing) previewing.NoteOnSample(PreviewKey, PreviewNote, 1f, a, 0, 0);
                        else sink.NoteOff(PreviewKey);
                        break;
                    case KindControl:
                        if (ch == null || !ch.Control(Math.Clamp(c, 0, 15), a, b)) Control(sink, a, b);
                        break;
                }
            }
            Volatile.Write(ref ringRead, read);
        }

        static void Control(INoteSink sink, int ctrl, int value)
        {
            switch (ctrl)
            {
                case 1: sink.ModWheel(Math.Clamp(value, 0, 127) / 127f); break;
                case 64: sink.Sustain(value >= 64); break;
                case 120:   // all sound off
                    sink.AllNotesOff(true); break;
                case 123:   // all notes off
                    sink.AllNotesOff(false); break;
                case 0xE0:
                case 0xFF:
                    sink.PitchBend(Math.Clamp((value - 8192) / 8192f, -1f, 1f)); break;
                case 0xD0:
                case 0xFC:
                    sink.Aftertouch(Math.Clamp(value, 0, 127) / 127f); break;
            }
        }
    }
}
