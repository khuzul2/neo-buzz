// Fork (docs/specs/note-wires.md §3): what the audio thread needs of a machine's note wires, built when the graph changes (under the audio lock) and swapped in
// whole: the queues of its incoming and outgoing wires, the machines that must run before it and after it, and a scratch array to merge into.
#nullable disable
using System;
using System.Collections.Generic;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core.NoteWires
{
    /// <summary>Events from the track input (clip notes, live MIDI, keys) for the first insert of a chain: written by the MIDI and tick threads, drained by the audio thread.</summary>
    internal sealed class NoteInputRing
    {
        readonly object gate = new object();
        readonly NoteMessage[] ring = new NoteMessage[NoteQueue.Capacity];
        int count;

        public void Push(in NoteMessage e)
        {
            lock (gate)
                if (count < ring.Length) ring[count++] = e;
        }

        public void DrainInto(NoteQueue q)
        {
            lock (gate)
            {
                for (int i = 0; i < count; i++) q.Add(ring[i]);
                count = 0;
            }
        }
    }

    internal sealed class NoteNode
    {
        public static readonly MachineCore[] None = Array.Empty<MachineCore>();

        public MachineCore Machine;
        /// <summary>Machines that must work before this one (note sources) and after it (note targets), for the work order.</summary>
        public MachineCore[] Sources = None, Targets = None;
        /// <summary>Incoming queues: the track input's first (when this is the first insert), then one per wire in creation order.</summary>
        public NoteQueue[] In = Array.Empty<NoteQueue>();
        public NoteQueue[] Out = Array.Empty<NoteQueue>();
        public NoteInputRing InputRing;
        public NoteQueue InputQueue;
        public NoteMessage[] Scratch = Array.Empty<NoteMessage>();
        public INoteReceiver Receiver;
        public long Delivered;

        /// <summary>Audio thread, just before Work: the events of this block, merged by offset, to the machine. Allocates nothing.</summary>
        public void Deliver(ReBuzzCore buzz)
        {
            var queues = In;
            if (queues.Length == 0) return;
            if (InputRing != null) InputRing.DrainInto(InputQueue);
            int n = NoteMerge.Merge(queues, queues.Length, Scratch);
            for (int i = 0; i < queues.Length; i++) queues[i].Clear();
            if (n == 0) return;
            Delivered += n;
            if (Receiver != null)
            {
                // Fork (notes.md G1): the modulators that listen to this machine hear what its wires gave it.
                for (int i = 0; i < n; i++)
                {
                    ref var e = ref Scratch[i];
                    if (e.Kind == NoteMessageKind.NoteOn) Machine.TapNote(e.Data1, Math.Max(1, e.Data2));
                    else if (e.Kind == NoteMessageKind.NoteOff) Machine.TapNote(e.Data1, 0);
                }
                Receiver.ReceiveNotes(new ReadOnlySpan<NoteMessage>(Scratch, 0, n));
                return;
            }
            // A target without the interface gets them the old way (no offsets).
            var mm = buzz.MachineManager;
            for (int i = 0; i < n; i++)
            {
                ref var e = ref Scratch[i];
                switch (e.Kind)
                {
                    case NoteMessageKind.NoteOn: mm.SendMIDINote(Machine, e.Channel, e.Data1, Math.Max(1, e.Data2)); break;
                    case NoteMessageKind.NoteOff: mm.SendMIDINote(Machine, e.Channel, e.Data1, 0); break;
                    case NoteMessageKind.Controller: mm.SendMIDIControlChange(Machine, e.Data1, e.Channel, e.Data2); break;
                    case NoteMessageKind.PitchBend: mm.SendMIDIControlChange(Machine, 0xE0, e.Channel, e.Data2); break;
                    case NoteMessageKind.Pressure: mm.SendMIDIControlChange(Machine, 0xD0, e.Channel, e.Data2); break;
                }
            }
        }
    }
}
