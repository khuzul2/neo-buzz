// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): the Session View's audio track.
//
// A Session Clip Player plays the audio clips (patterns with audio, made by dropping a WAV,
// AIFF or FLAC file on a slot) its track launches, warped to the song's tempo, and records what
// comes in (connect ReBuzz Audio In, or any machine, to it) into a slot while its track is armed.
// The work is ReBuzz's (BuzzGUI.Common.SessionClipHost): this machine only hands it the block.

using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace NeoBuzz.SessionClipPlayer
{
    [MachineDecl(Name = SessionClipHost.MachineName, ShortName = "Clips", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public class SessionClipPlayerMachine : IBuzzMachine
    {
        readonly IBuzzMachineHost host;
        readonly object? state;

        public SessionClipPlayerMachine(IBuzzMachineHost host)
        {
            this.host = host;
            state = SessionClipHost.CreateState?.Invoke();
        }

        /// <summary>What comes in is heard: auto (while the track is armed), in (always) or off (never).</summary>
        [ParameterDecl(Name = "Monitor", MinValue = 0, MaxValue = 2, DefValue = 0, ValueDescriptions = new[] { "auto", "in", "off" },
            Description = "Hear the input: auto (while armed), in (always) or off")]
        public int Monitor { get; set; }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            var render = SessionClipHost.Render;
            if (render != null && state != null)
                return render(host.Machine, state, output, input, n, mode, Monitor);
            // Not in ReBuzz (or not yet set up): the input goes through.
            if ((mode & WorkModes.WM_READ) == 0) return false;
            for (int i = 0; i < n; i++) output[i] = input[i];
            return true;
        }
    }
}
