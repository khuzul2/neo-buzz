// Fork (docs/specs/clips.md §5.6, CU1): the timing probe's machine. It plays nothing: it puts what it was told
// on its output, so a test reads from the audio where a value arrived, to the sample. Left is the Level parameter
// (a classic state parameter, taken at the tick), right is the MIDI note it holds (note-on velocity > 0 sets it,
// a note-off clears it) plus 128 times the Note track parameter's value (a classic stateless one; Off clears it).
// Every value the machine takes holds until the next, and the output changes in the first Work after it came in:
// where the value lands is the first sample of that block.
// Built into the tests' Gear/Generators as "Clip Probe.NET.dll" (ClipProbe.csproj); not shipped.

using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace ReBuzz.Studio.Tests.TestMachines
{
    [MachineDecl(Name = "Clip Probe", ShortName = "Probe", Author = "neo-buzz", MaxTracks = 1, InputCount = 0, OutputCount = 1)]
    public class ClipProbeMachine : IBuzzMachine
    {
        int level, midi, note, ons;

        public ClipProbeMachine(IBuzzMachineHost host)
        {
        }

        [ParameterDecl(Name = "Level", Description = "Left output: the value, held", MinValue = 0, MaxValue = 1000, DefValue = 0)]
        public int Level
        {
            get => level;
            set => level = value;
        }

        [ParameterDecl(Name = "Note", IsStateless = true, Description = "A note, held in the right output (times 128) until an off")]
        public void SetNote(Note value, int track)
        {
            if (value.Value == 0) return;
            note = value.Value == Note.Off ? 0 : value.Value;
        }

        public void MidiNote(int channel, int value, int velocity)
        {
            midi = velocity > 0 ? value : 0;
            if (velocity > 0) ons++;
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            float l = level, r = midi + 128 * note + 100000f * ons;      // the note-ons it took, times 100000: a test counts the times a note sounded
            for (int i = 0; i < n; i++) output[i] = new Sample(l, r);
            return true;
        }

        public void Stop()
        {
            level = 0;
            midi = 0;
            note = 0;
            ons = 0;
        }
    }
}
