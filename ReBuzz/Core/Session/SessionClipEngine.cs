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
    /// and its input is heard when it should be (Monitor). After the mix (<see cref="AfterMix"/>),
    /// takes resampling the master record it. Audio thread; no allocation.
    /// </summary>
    internal static class SessionClipEngine
    {
        /// <summary>One clip player's state, made with the machine.</summary>
        internal sealed class PlayerState
        {
            public readonly ClipMixer Mixer = new ClipMixer();
            public readonly float[] InputLeft = new float[ClipMixer.DefaultMaxBlock], InputRight = new float[ClipMixer.DefaultMaxBlock];
        }

        /// <summary>
        /// A take recording while its pattern plays: the master output (resampling) or its
        /// track's input (which the player writes in its Work).
        /// </summary>
        internal sealed class TakeCapture
        {
            public TakeCapture(PatternCore take, AudioCapture capture, bool master)
            {
                Take = take;
                Capture = capture;
                Master = master;
            }

            public PatternCore Take { get; }
            public AudioCapture Capture { get; }
            public bool Master { get; }
        }

        /// <summary>The beat of a pattern playing in this block, at its first sample.</summary>
        static double BeatOf(PatternCore pattern, MasterInfo mi)
        {
            int samplesPerTick = mi.SamplesPerTick > 0 ? mi.SamplesPerTick : 1;
            int ticksPerBeat = mi.TicksPerBeat > 0 ? mi.TicksPerBeat : 4;
            int position = pattern.PlayPosition;
            // The play position is the block's start in 1/TimeBase ticks; the tick exactly.
            int tick = position >= 0 ? position / PatternEvent.TimeBase : -((-position + PatternEvent.TimeBase - 1) / PatternEvent.TimeBase);
            return (tick + mi.PosInTick / (double)samplesPerTick) / ticksPerBeat;
        }

        /// <summary>
        /// Audio thread, after the master tap of each block: takes resampling the master record
        /// what was just mixed (before the metronome), and takes of a track's input whose machine
        /// didn't work this block (muted) record silence, so they keep their time.
        /// </summary>
        internal static void AfterMix(ReBuzzCore buzz, float[] buffer, int offset, int frames)
        {
            var captures = buzz.SessionCore.TakeCaptures;
            if (captures.Length == 0 || !buzz.Playing) return;
            var mi = ReBuzzCore.masterInfo;
            int block = ReBuzzCore.GlobalState.AudioFrame;
            for (int i = 0; i < captures.Length; i++)
            {
                var c = captures[i];
                var capture = c.Capture;
                if (c.Take.PlayPosition == int.MinValue || capture.IsDone) continue;
                if (c.Master)
                {
                    capture.StartAt(BeatOf(c.Take, mi));
                    capture.WriteInterleaved(buffer, offset, frames, 1f / 32768);
                }
                else if (capture.LastBlock != block)
                {
                    capture.StartAt(BeatOf(c.Take, mi));
                    capture.WriteSilence(frames);
                    capture.LastBlock = block;
                }
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
            bool sound;
            int frames = mixer.Begin(n);          // never more than it was made for: a longer block's end is silent, nothing is allocated
            if (buzz != null && buzz.Playing)
            {
                var mi = ReBuzzCore.masterInfo;
                int samplesPerTick = mi.SamplesPerTick > 0 ? mi.SamplesPerTick : 1;
                int ticksPerBeat = mi.TicksPerBeat > 0 ? mi.TicksPerBeat : 4;
                double beatsPerSample = 1.0 / ((double)samplesPerTick * ticksPerBeat);
                var positioned = buzz.SongCore.PositionedPatterns;
                for (int i = 0; i < positioned.Count; i++)
                {
                    var pattern = positioned[i];
                    var clip = pattern.SessionAudio;
                    if (clip == null || pattern.Machine != machine || pattern.PlayPosition == int.MinValue) continue;
                    double beat = BeatOf(pattern, mi);
                    var capture = clip.Capture;
                    if (capture != null && !capture.IsDone && capture.LastBlock != ReBuzzCore.GlobalState.AudioFrame)
                    {
                        capture.StartAt(beat);
                        Record(player, capture, input, n, mode);
                        capture.LastBlock = ReBuzzCore.GlobalState.AudioFrame;
                    }
                    // An arrangement clip with fades knows where it is in its clip (WorkManager.UpdatePatternPositions).
                    double clipPos = double.NaN, clipLeft = double.NaN;
                    if (pattern.ArrangementSinceClipStart != int.MinValue)
                    {
                        double inTick = mi.PosInTick / (double)samplesPerTick;
                        clipPos = (pattern.ArrangementSinceClipStart + inTick) / ticksPerBeat;
                        clipLeft = (pattern.ArrangementUntilClipEnd - inTick) / ticksPerBeat;
                    }
                    mixer.Play(clip, beat, beatsPerSample, mi.SamplesPerSec, frames, clipPos, clipLeft);
                }
                sound = mixer.End(frames, beatsPerSample, mi.SamplesPerSec);
            }
            else
                sound = mixer.End(frames);

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
                float l = i < frames ? Clean(left[i] * 32768f) : 0f, r = i < frames ? Clean(right[i] * 32768f) : 0f;
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

        /// <summary>What the clips made, as the master may have it: no NaN or infinity (they would stay in every filter and delay after it), no subnormals.</summary>
        static float Clean(float x) => !float.IsFinite(x) || Math.Abs(x) < 1e-12f ? 0f : x;

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
            // In pieces of what the buffers hold, so a block longer than that records whole and keeps its time.
            for (int at = 0; at < n; at += player.InputLeft.Length)
            {
                int count = Math.Min(n - at, player.InputLeft.Length);
                for (int i = 0; i < count; i++)
                {
                    player.InputLeft[i] = input[at + i].L;
                    player.InputRight[i] = input[at + i].R;
                }
                capture.Write(player.InputLeft, player.InputRight, count, 1f / 32768);
            }
        }
    }
}
