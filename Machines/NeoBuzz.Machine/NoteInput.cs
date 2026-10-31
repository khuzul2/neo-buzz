#nullable disable
// Neo machines' Buzz glue (docs/specs/machines.md §3): notes from pattern tracks and MIDI, queued
// where they arrive (ReBuzz's tick, the MIDI thread) and applied by the synth at its next block.

using System;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Machine
{
    internal sealed class NoteInput
    {
        public const int MidiKeyBase = 1000;
        const int DefaultVelocity = 100;

        // Track notes: written by setters during ReBuzz's tick, read at the next Work (same thread
        // in practice; the lock covers the rare call from elsewhere).
        readonly byte[] pendingNote;
        readonly int[] pendingVelocity;
        readonly bool[] hasNote;
        bool anyTrack;
        readonly object trackLock = new object();

        // MIDI events: (kind, a, b) ring buffer, multiple producers under a lock, one consumer.
        const int RingSize = 1024;
        readonly int[] ringKind = new int[RingSize], ringA = new int[RingSize], ringB = new int[RingSize];
        int ringWrite, ringRead;
        readonly object midiLock = new object();

        const int KindNoteOn = 1, KindNoteOff = 2, KindControl = 3;

        public NoteInput(int tracks)
        {
            Tracks = Math.Max(1, tracks);
            pendingNote = new byte[Tracks];
            pendingVelocity = new int[Tracks];
            hasNote = new bool[Tracks];
            for (int t = 0; t < Tracks; t++) pendingVelocity[t] = -1;
        }

        public int Tracks { get; }

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

        /// <summary>MIDI note on/off from ReBuzz (MIDI thread). Velocity 0 is note off.</summary>
        public void MidiNote(int channel, int note, int velocity)
        {
            if (note < 0 || note > 127) return;
            Push(velocity > 0 ? KindNoteOn : KindNoteOff, note, velocity);
        }

        /// <summary>
        /// MIDI controllers from ReBuzz (MIDI thread): CC numbers, plus pitch bend (0xE0 or 0xFF,
        /// 14-bit value) and channel pressure (0xD0 or 0xFC).
        /// </summary>
        public void MidiControl(int ctrl, int channel, int value)
        {
            Push(KindControl, ctrl, value);
        }

        void Push(int kind, int a, int b)
        {
            lock (midiLock)
            {
                int next = (ringWrite + 1) & (RingSize - 1);
                if (next == Volatile.Read(ref ringRead)) return;   // full: drop
                ringKind[ringWrite] = kind;
                ringA[ringWrite] = a;
                ringB[ringWrite] = b;
                Volatile.Write(ref ringWrite, next);
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
                        if (!hasNote[t])
                        {
                            pendingVelocity[t] = -1;
                            continue;
                        }
                        hasNote[t] = false;
                        byte b = pendingNote[t];
                        int vel = pendingVelocity[t] > 0 ? pendingVelocity[t] : DefaultVelocity;
                        pendingVelocity[t] = -1;
                        if (b == 255)
                        {
                            sink.NoteOff(t);
                            continue;
                        }
                        int midi = BuzzToMidi(b);
                        if (midi >= 0) sink.NoteOn(t, midi, vel / 127f);
                    }
                }
            }
            int read = ringRead;
            int write = Volatile.Read(ref ringWrite);
            while (read != write)
            {
                int kind = ringKind[read], a = ringA[read], b = ringB[read];
                read = (read + 1) & (RingSize - 1);
                switch (kind)
                {
                    case KindNoteOn: sink.NoteOn(MidiKeyBase + a, a, Math.Clamp(b, 1, 127) / 127f); break;
                    case KindNoteOff: sink.NoteOff(MidiKeyBase + a); break;
                    case KindControl: Control(sink, a, b); break;
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
