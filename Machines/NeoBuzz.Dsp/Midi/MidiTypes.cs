#nullable disable
// Shared by Neo Voice to MIDI and Neo Audio to MIDI (docs/specs/audio-to-midi.md §2): the MIDI an engine makes, as events with a sample offset in
// the block, and the buffer they are collected in. No host types: the machine turns the events into messages (NeoBuzz.Machine.MidiRouter).

using System;

namespace NeoBuzz.Dsp
{
    internal enum MidiKind : byte { NoteOn, NoteOff, Controller, PitchBend }
    internal enum MidiStream : byte { Pitch, Chord, Trigger, Expression }

    /// <summary>One MIDI event. NoteOn: Data1 note, Data2 velocity. NoteOff: Data1 note. Controller: Data1 number, Data2 value. PitchBend: Data2 0 ... 16383 (8192 centre).</summary>
    internal struct MidiEvent
    {
        public int Offset;
        public MidiKind Kind;
        public MidiStream Stream;
        public byte Channel;
        public byte Data1;
        public int Data2;
    }

    /// <summary>The events of one block, in the order they were made until <see cref="SortByOffset"/>. Full: further events are dropped.</summary>
    internal sealed class MidiBlock
    {
        public const int Capacity = 128;
        public readonly MidiEvent[] Events = new MidiEvent[Capacity];
        public int Count;
        public long EventsSent;
        long blockStart;
        int blockLen = 1;

        /// <summary>Starts a block of <paramref name="length"/> samples that begins at stream position <paramref name="start"/>.</summary>
        public void Begin(long start, int length) { Count = 0; blockStart = start; blockLen = length; }

        /// <summary>Adds an event at stream position <paramref name="at"/> (its offset in the block is clamped to the block).</summary>
        public void Emit(MidiKind kind, MidiStream stream, int channel, int d1, int d2, long at)
        {
            if (Count >= Events.Length) return;
            int off = (int)Math.Clamp(at - blockStart, 0, Math.Max(0, blockLen - 1));
            ref var e = ref Events[Count++];
            e.Offset = off; e.Kind = kind; e.Stream = stream; e.Channel = (byte)(channel & 15); e.Data1 = (byte)Math.Clamp(d1, 0, 127); e.Data2 = d2;
            EventsSent++;
        }

        /// <summary>Stable insertion sort by offset (a few events).</summary>
        public void SortByOffset()
        {
            for (int i = 1; i < Count; i++)
            {
                var t = Events[i]; int j = i - 1;
                while (j >= 0 && Events[j].Offset > t.Offset) { Events[j + 1] = Events[j]; j--; }
                Events[j + 1] = t;
            }
        }
    }
}
