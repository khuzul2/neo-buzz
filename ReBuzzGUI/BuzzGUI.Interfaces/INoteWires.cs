// Fork (docs/specs/note-wires.md): note wires. A machine says whether it takes notes and whether it sends them (INoteCapable); the engine hands
// a target the events its note wires carried this block (INoteReceiver) before the target's Work, with the sample offsets they were made at;
// a source sends through its own machine (INoteEmitter, implemented by the core: get it with <c>host.Machine as INoteEmitter</c>).
using System;

namespace BuzzGUI.Interfaces
{
    public enum NoteMessageKind : byte { NoteOn, NoteOff, Controller, PitchBend, Pressure }

    /// <summary>One note-wire event. NoteOn: Data1 note, Data2 velocity. NoteOff: Data1 note. Controller: Data1 number, Data2 value. PitchBend: Data2 0 ... 16383 (8192 centre). Pressure: Data2 0 ... 127.</summary>
    public struct NoteMessage
    {
        /// <summary>Samples from the start of the block the event belongs to.</summary>
        public int Offset;
        public NoteMessageKind Kind;
        public byte Channel;
        public byte Data1;
        public int Data2;

        public static NoteMessage On(int offset, int channel, int note, int velocity) => new NoteMessage { Offset = offset, Kind = NoteMessageKind.NoteOn, Channel = (byte)(channel & 15), Data1 = (byte)(note & 127), Data2 = Math.Clamp(velocity, 1, 127) };
        public static NoteMessage Off(int offset, int channel, int note) => new NoteMessage { Offset = offset, Kind = NoteMessageKind.NoteOff, Channel = (byte)(channel & 15), Data1 = (byte)(note & 127) };
    }

    /// <summary>Marker with two answers: this machine's managed class takes notes (a note-in port) and/or sends them (a note-out port). Both may change while the machine runs (Neo Plug sends only when the plug-in makes MIDI).</summary>
    public interface INoteCapable
    {
        bool AcceptsNotes { get; }
        bool SendsNotes { get; }
    }

    /// <summary>A machine that takes notes: the engine calls this on the audio thread just before Work with every event its wires carried this block, sorted by offset. Don't keep the span.</summary>
    public interface INoteReceiver
    {
        void ReceiveNotes(ReadOnlySpan<NoteMessage> events);
    }

    /// <summary>Implemented by the machine core: sends an event down this machine's note wires, to arrive in the same block at the same offset. Audio thread, from Work; allocates nothing.</summary>
    public interface INoteEmitter
    {
        void EmitNote(in NoteMessage e);
    }

    /// <summary>
    /// Told of every note a machine is given, whatever its source (docs/specs/notes.md G1): the clip player, the piano roll and Session (their MIDI column),
    /// live MIDI and the keyboards, a note insert or note wire, another machine. Called on the MIDI or audio thread; must not allocate, lock or wait.
    /// </summary>
    public interface INoteTap
    {
        /// <summary>A note-on: the MIDI note and its velocity (1 ... 127).</summary>
        void TapNoteOn(int note, int velocity);

        void TapNoteOff(int note);
    }

    /// <summary>Implemented by the machine core: a modulator (Neo Envelope, Neo LFO) listens to the notes a machine plays through it, as it listens to its Note parameters.</summary>
    public interface INoteTapSource
    {
        void AddNoteTap(INoteTap tap);

        void RemoveNoteTap(INoteTap tap);
    }
}
