#nullable disable
// Neo machines' Buzz glue (docs/specs/machines.md §3): what the Neo synths share. Notes from
// tracks and MIDI are queued and applied at the start of each block; the transport stopping
// (or ReBuzz's Stop) releases every note.

using Buzz.MachineInterface;
using NeoBuzz.Dsp;

namespace NeoBuzz.Machine
{
    public abstract class NeoSynthBase : NeoMachineBase
    {
        volatile bool stopRequested;

        internal NeoSynthBase(IBuzzMachineHost host, ParamDef[] table, int tracks) : base(host, table)
        {
            Notes = new NoteInput(tracks);
        }

        /// <summary>Pattern and MIDI notes waiting for the next block (generated track setters write here).</summary>
        internal NoteInput Notes { get; }

        /// <summary>ReBuzz: a MIDI note for this machine (velocity 0 releases).</summary>
        public void MidiNote(int channel, int value, int velocity) => Notes.MidiNote(channel, value, velocity);

        /// <summary>ReBuzz: a MIDI controller, pitch bend (0xE0/0xFF) or channel pressure (0xD0/0xFC).</summary>
        public void MidiControlChange(int ctrl, int channel, int value) => Notes.MidiControl(ctrl, channel, value);

        /// <summary>ReBuzz: stop (release everything at the next block).</summary>
        public void Stop() => stopRequested = true;

        /// <summary>At the start of each block: transport stop, then the queued notes and controllers.</summary>
        internal void BeginBlock(INoteSink sink)
        {
            if (TransportStopped() || stopRequested)
            {
                stopRequested = false;
                sink.AllNotesOff(false);
            }
            Notes.Drain(sink);
        }
    }
}
