// Fork (docs/specs/note-wires.md §8): a note machine for the tests; nothing ships. It takes notes and sends notes: every event it receives goes out
// transposed by Up semitones at the same offset, and, when Delay is not 0, once more Delay samples later (across block edges). Up 0 and Delay 0 is a pass-through.
// EmitAt makes it a source: the next Work sends one event at the offset given. Everything it receives is logged for the tests (block, offset, kind, note).
// Built into the tests' Gear/Generators as "Note Echo.NET.dll" (NoteEcho.csproj); it makes no sound.

using System;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace ReBuzz.Studio.Tests.TestMachines
{
    [MachineDecl(Name = "Note Echo", ShortName = "NEcho", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 1)]
    public class NoteEchoMachine : IBuzzMachine, INoteCapable, INoteReceiver
    {
        const int Cap = 8192;
        readonly IBuzzMachineHost host;
        INoteEmitter emitter;

        // Received, for the tests (preallocated: the audio thread allocates nothing).
        public readonly int[] LogBlock = new int[Cap], LogOffset = new int[Cap], LogKind = new int[Cap], LogNote = new int[Cap], LogBlockLength = new int[Cap];
        public int LogCount;
        public int Block;                         // Work calls so far
        int blockLength;

        readonly NoteMessage[] pending = new NoteMessage[Cap];     // to send: offsets are from the start of the next Work
        int pendingCount;
        readonly NoteMessage[] incoming = new NoteMessage[Cap];
        int incomingCount;
        readonly object gate = new object();

        public NoteEchoMachine(IBuzzMachineHost host) { this.host = host; }

        [ParameterDecl(Name = "Up", MinValue = 0, MaxValue = 24, DefValue = 0, Description = "Semitones added to every note")]
        public int Up { get; set; }

        [ParameterDecl(Name = "Delay", MinValue = 0, MaxValue = 4096, DefValue = 0, Description = "Samples of the second copy (0: none)")]
        public int Delay { get; set; }

        bool INoteCapable.AcceptsNotes => true;
        bool INoteCapable.SendsNotes => true;

        void INoteReceiver.ReceiveNotes(ReadOnlySpan<NoteMessage> events)
        {
            lock (gate)
                for (int i = 0; i < events.Length; i++)
                {
                    var e = events[i];
                    if (LogCount < Cap)
                    {
                        LogBlock[LogCount] = Block; LogOffset[LogCount] = e.Offset; LogKind[LogCount] = (int)e.Kind; LogNote[LogCount] = e.Data1; LogBlockLength[LogCount] = blockLength;
                        LogCount++;
                    }
                    if (incomingCount < Cap) incoming[incomingCount++] = e;
                }
        }

        /// <summary>Tests: sends one event in the next Work, at <paramref name="offset"/>.</summary>
        public void EmitAt(int offset, int note, bool on)
        {
            lock (gate)
                if (pendingCount < Cap)
                    pending[pendingCount++] = on ? NoteMessage.On(offset, 0, note, 100) : NoteMessage.Off(offset, 0, note);
        }

        public void ClearLog() { lock (gate) LogCount = 0; }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            emitter ??= host.Machine as INoteEmitter;
            blockLength = n;
            Block++;
            lock (gate)
            {
                // Events carried from earlier blocks (offsets past the end of their block) and the scripted ones.
                int keep = 0;
                for (int i = 0; i < pendingCount; i++)
                {
                    var e = pending[i];
                    if (e.Offset < n) emitter?.EmitNote(e);
                    else { e.Offset -= n; pending[keep++] = e; }
                }
                pendingCount = keep;
                // What came in goes out transposed, at the same offset; a second copy Delay samples later, carried over block edges.
                for (int i = 0; i < incomingCount; i++)
                {
                    var e = incoming[i];
                    if (e.Kind == NoteMessageKind.NoteOn || e.Kind == NoteMessageKind.NoteOff) e.Data1 = (byte)Math.Min(127, e.Data1 + Up);
                    Send(e);
                    if (Delay > 0) { var d = e; d.Offset += Delay; Send(d); }
                }
                incomingCount = 0;
            }
            Array.Clear(output, 0, n);
            return false;
        }

        void Send(NoteMessage e)
        {
            if (e.Offset < blockLength) emitter?.EmitNote(e);
            else
            {
                e.Offset -= blockLength;
                if (pendingCount < Cap) pending[pendingCount++] = e;
            }
        }
    }
}
