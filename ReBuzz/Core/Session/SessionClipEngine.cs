using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Clips;
using System;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Audio tracks, engine side (docs/specs/session-view.md 1.0): the Session Clip Player
    /// machine's work. Each block, the machine's patterns that play (session clips, and the
    /// arrangement's) render their audio clips at the beat they are at, a take records its input,
    /// and its input is heard when it should be (Monitor). Takes resampling the master record in
    /// the master tap (<see cref="TapMaster"/>). Audio thread; no allocation.
    /// </summary>
    internal static class SessionClipEngine
    {
        /// <summary>One clip player's state, made with the machine.</summary>
        internal sealed class PlayerState
        {
            public readonly ClipMixer Mixer = new ClipMixer();
            public readonly float[] InputLeft = new float[256], InputRight = new float[256];
        }

        /// <summary>A take recording the master output (resampling) while its pattern plays.</summary>
        internal sealed class MasterCapture
        {
            public MasterCapture(PatternCore take, AudioCapture capture)
            {
                Take = take;
                Capture = capture;
            }

            public PatternCore Take { get; }
            public AudioCapture Capture { get; }
        }

        /// <summary>
        /// Audio thread, after the master tap of each block: takes resampling the master record
        /// what was just mixed (before the metronome), while their pattern plays.
        /// </summary>
        internal static void TapMaster(ReBuzzCore buzz, float[] buffer, int offset, int frames)
        {
            var captures = buzz.SessionCore.MasterCaptures;
            if (captures.Length == 0 || !buzz.Playing) return;
            for (int i = 0; i < captures.Length; i++)
            {
                var c = captures[i];
                if (c.Take.PlayPosition != int.MinValue && !c.Capture.IsDone)
                    c.Capture.WriteInterleaved(buffer, offset, frames, 1f / 32768);
            }
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
                    var capture = clip.Capture;
                    if (capture != null && !capture.IsDone)
                        Record(player, capture, input, n, mode);
                    // The play position is the block's start in 1/TimeBase ticks; the tick exactly.
                    int tick = position >= 0 ? position / PatternEvent.TimeBase : -((-position + PatternEvent.TimeBase - 1) / PatternEvent.TimeBase);
                    mixer.Play(clip, (tick + inTick) / ticksPerBeat, beatsPerSample, mi.SamplesPerSec, n);
                }
            }
            bool sound = mixer.End(n);

            bool hear = (mode & WorkModes.WM_READ) != 0 &&
                        (monitor == 1 || (monitor == 0 && buzz != null && buzz.SessionCore.IsArmedMachine(machine)));
            if (!sound && !hear)
            {
                // ReBuzz works a managed effect in place: what came in goes on out unless the
                // output is written. Input not heard is written over with silence.
                if ((mode & WorkModes.WM_READ) == 0 || !HasSound(input, n)) return false;
                for (int i = 0; i < n; i++) output[i] = default;
                return true;
            }
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

        static bool HasSound(Sample[] samples, int n)
        {
            for (int i = 0; i < n; i++)
                if (samples[i].L != 0 || samples[i].R != 0) return true;
            return false;
        }

        /// <summary>A take of the track's input records this block (silence when nothing comes in).</summary>
        static void Record(PlayerState player, AudioCapture capture, Sample[] input, int n, WorkModes mode)
        {
            if ((mode & WorkModes.WM_READ) == 0)
            {
                capture.WriteSilence(n);
                return;
            }
            int count = Math.Min(n, player.InputLeft.Length);
            for (int i = 0; i < count; i++)
            {
                player.InputLeft[i] = input[i].L;
                player.InputRight[i] = input[i].R;
            }
            capture.Write(player.InputLeft, player.InputRight, count, 1f / 32768);
        }
    }
}
