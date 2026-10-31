using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Clips;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Audio tracks, engine side (docs/specs/session-view.md 1.0): the Session Clip Player
    /// machine's work. Each block, the machine's patterns that play (session clips, and the
    /// arrangement's) render their audio clips at the beat they are at, and its input is heard
    /// when it should be (Monitor). Audio thread; no allocation.
    /// </summary>
    internal static class SessionClipEngine
    {
        /// <summary>One clip player's state, made with the machine.</summary>
        internal sealed class PlayerState
        {
            public readonly ClipMixer Mixer = new ClipMixer();
        }

        /// <summary>Hands ReBuzz's side to the machine DLL (once; every ReBuzz instance uses the same).</summary>
        internal static void Install()
        {
            SessionClipHost.CreateState = () => new PlayerState();
            SessionClipHost.Render = Render;
        }

        static bool Render(IMachine machine, object state, Sample[] output, Sample[] input, int n, WorkModes mode, int monitor)
        {
            var player = (PlayerState)state;
            var buzz = (machine as MachineCore)?.Graph?.Buzz as ReBuzzCore;
            var mixer = player.Mixer;
            mixer.Begin(n);
            if (buzz != null && buzz.Playing)
            {
                var mi = ReBuzzCore.masterInfo;
                int samplesPerTick = mi.SamplesPerTick > 0 ? mi.SamplesPerTick : 1;
                int ticksPerBeat = mi.TicksPerBeat > 0 ? mi.TicksPerBeat : 4;
                double beatsPerSample = 1.0 / ((double)samplesPerTick * ticksPerBeat);
                double inTick = mi.PosInTick / (double)samplesPerTick;
                var positioned = buzz.SongCore.PositionedPatterns;
                for (int i = 0; i < positioned.Count; i++)
                {
                    var pattern = positioned[i];
                    var clip = pattern.SessionAudio;
                    if (clip == null || pattern.Machine != machine) continue;
                    int position = pattern.PlayPosition;
                    if (position == int.MinValue) continue;
                    // The play position is the block's start in 1/TimeBase ticks; the tick exactly.
                    int tick = position >= 0 ? position / PatternEvent.TimeBase : -((-position + PatternEvent.TimeBase - 1) / PatternEvent.TimeBase);
                    mixer.Play(clip, (tick + inTick) / ticksPerBeat, beatsPerSample, mi.SamplesPerSec, n);
                }
            }
            bool sound = mixer.End(n);

            bool hear = (mode & WorkModes.WM_READ) != 0 &&
                        (monitor == 1 || (monitor == 0 && buzz != null && buzz.SessionCore.IsArmedMachine(machine)));
            if (!sound && !hear) return false;
            var left = mixer.Left;
            var right = mixer.Right;
            for (int i = 0; i < n; i++)
            {
                float l = left[i] * 32768f, r = right[i] * 32768f;
                if (hear)
                {
                    l += input[i].L;
                    r += input[i].R;
                }
                output[i].L = l;
                output[i].R = r;
            }
            return true;
        }
    }
}
