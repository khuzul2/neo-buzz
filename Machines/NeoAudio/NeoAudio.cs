#nullable disable
// Neo Audio (docs/specs/neo-audio.md): the audio track. It was the Session Clip Player (docs/specs/session-view.md 1.0); songs that name
// that machine load as this one (ReBuzz/MachineManagement/MachineAliases.cs), and Params.def keeps its two parameters in their old order
// (state version 0, machines.md section 3.2). The work is ReBuzz's (BuzzGUI.Common.SessionClipHost, ReBuzz/Core/Session/SessionClipEngine.cs):
// this machine only hands it the block. Its clips are patterns of the machine; their audio lives in the song's audio store.
//
// It stays a managed effect by its Work signature (an audio track records what is connected to it, and songs saved with the old machine
// connect machines into it); the catalogue sorts it with the instruments (category Samplers).

using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.NeoAudio
{
    [MachineDecl(Name = SessionClipHost.MachineName, ShortName = "Audio", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoAudioMachine : NeoMachineBase, IBuzzMachine
    {
        readonly object state;

        public NeoAudioMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            state = SessionClipHost.CreateState?.Invoke();
        }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            var render = SessionClipHost.Render;
            if (render != null && state != null)
                return render(host.Machine, state, output, input, n, mode, Values[P.Monitor]);
            // Not in ReBuzz (or not yet set up): the input goes through.
            if ((mode & WorkModes.WM_READ) == 0) return false;
            for (int i = 0; i < n; i++) output[i] = input[i];
            return true;
        }
    }
}
