#nullable disable
// Neo machines' Buzz glue (docs/specs/machines.md §3): what the Neo synths share. Notes from
// tracks and MIDI are queued and applied at the start of each block; the transport stopping
// (or ReBuzz's Stop) releases every note, and Stop again once stopped silences everything (a
// panic for sounds that ring on after release, such as DX7 patches whose level 4 isn't 0).

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

        /// <summary>ReBuzz: stop (release everything at the next block; silence everything if already stopped).</summary>
        public void Stop() => stopRequested = true;

        /// <summary>
        /// ReBuzz's offline render (and nothing else) calls this under the audio lock before it runs any audio: everything the sound has
        /// built up (voice allocation and age, oscillator and LFO phases, the analog drift and the random streams, filter memory, queued
        /// notes) goes back to what a machine made a moment ago has, so two renders of one song are the same samples in one process,
        /// as they are in two (docs/specs/ux-overhaul.md P4a). The parameters, the machine's own data (a bank, a user table) stay.
        /// </summary>
        public void ResetForRender()
        {
            stopRequested = false;
            Notes.Clear();
            ResetTransport();
            Restart();
        }

        /// <summary>A new engine for the same parameters, and the sample rate asked for again at the next block (the machines that drift or
        /// keep a random stream override it; the others start every render the same already).</summary>
        internal virtual void Restart() { }

        /// <summary>At the start of each block: transport stop, then the queued notes and controllers.</summary>
        internal void BeginBlock(INoteSink sink)
        {
            bool edge = TransportStopped();
            bool stop = stopRequested;
            stopRequested = false;
            if (edge) sink.AllNotesOff(false);
            else if (stop) sink.AllNotesOff(StoppedForAWhile);
            Notes.Drain(sink);
        }
    }
}
