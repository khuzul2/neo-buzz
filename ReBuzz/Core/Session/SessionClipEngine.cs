using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Clips;
using System;
using System.Runtime.CompilerServices;
using System.Threading;

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
            /// <summary>The block's audio from a source other than the wired input (a device's channels, a track's tap), in Buzz's sample scale.</summary>
            public readonly Sample[] Source = new Sample[Math.Max(ClipMixer.DefaultMaxBlock, ReBuzz.Audio.EngineInput.MaxBlock)];
            float peakLeft, peakRight;

            /// <summary>Audio thread: what this block's input reached (Buzz's sample scale, 32768 is full scale).</summary>
            public void NotePeak(float left, float right)
            {
                AtomicMax(ref peakLeft, left);
                AtomicMax(ref peakRight, right);
            }

            /// <summary>The loudest input sample of each side since the last call (any thread), and forgets it.</summary>
            public (float Left, float Right) TakePeak() => (Interlocked.Exchange(ref peakLeft, 0f), Interlocked.Exchange(ref peakRight, 0f));

            static void AtomicMax(ref float target, float value)
            {
                float seen = Volatile.Read(ref target);
                while (value > seen)
                {
                    float was = Interlocked.CompareExchange(ref target, value, seen);
                    if (was == seen) return;
                    seen = was;
                }
            }
        }

        static readonly ConditionalWeakTable<IMachine, PlayerState> players = new ConditionalWeakTable<IMachine, PlayerState>();

        /// <summary>The clip player state of a Neo Audio, or null before it first worked.</summary>
        internal static PlayerState PlayerOf(IMachine machine) => machine != null && players.TryGetValue(machine, out var p) ? p : null;

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
            var recording = buzz.SessionCore.ArrangementAudio;
            if (!buzz.Playing) return;
            int block = ReBuzzCore.GlobalState.AudioFrame;
            // NA4b: an arrangement stream of a track that didn't work this block (muted) records silence, so it keeps its time.
            for (int i = 0; i < recording.Length; i++)
            {
                var take = recording[i];
                if (take.Started && !take.Capture.IsDone && take.LastBlock != block)
                {
                    take.Capture.WriteSilence(frames);
                    take.LastBlock = block;
                }
            }
            if (captures.Length == 0) return;
            var mi = ReBuzzCore.masterInfo;
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
            if (!players.TryGetValue(machine, out _)) players.Add(machine, player);
            var mixer = player.Mixer;
            bool sound;
            int frames = mixer.Begin(n);          // never more than it was made for: a longer block's end is silent, nothing is allocated
            // docs/specs/neo-audio.md "Inputs and arm": where this track's audio comes from (the wired input, an input device, another track, nothing).
            var audio = Source(buzz, machine, player, input, n, mode, out bool hasInput);
            if (buzz != null && buzz.Playing)
            {
                var mi = ReBuzzCore.masterInfo;
                // NA4b: an arrangement recording of this track writes what it hears (the first block at its start tick begins the stream).
                var recording = buzz.SessionCore.ArrangementAudio;
                for (int r = 0; r < recording.Length; r++)
                {
                    var take = recording[r];
                    if (!ReferenceEquals(take.Machine, machine) || take.Capture.IsDone) continue;
                    if (take.Begin(buzz.SongCore.PlayPosition, mi.PosInTick, mi.SamplesPerTick))
                    {
                        Record(player, take.Capture, audio, n, hasInput);
                        take.LastBlock = ReBuzzCore.GlobalState.AudioFrame;
                    }
                }
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
                        Record(player, capture, audio, n, hasInput);
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

            bool hear = hasInput &&
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
                if (hear && i < audio.Length)
                {
                    l += audio[i].L;
                    r += audio[i].R;
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

        /// <summary>
        /// The block's input of a Neo Audio: the wired input (as before), nothing, channels of the input device (a mono one centred to both
        /// sides) or the tap of a track. <paramref name="hasInput"/> is false when there is none: nothing is heard and takes record silence.
        /// The input meter takes its peak here.
        /// </summary>
        static Sample[] Source(ReBuzzCore buzz, IMachine machine, PlayerState player, Sample[] input, int n, WorkModes mode, out bool hasInput)
        {
            var route = buzz?.SessionCore.RouteOf(machine);
            var from = route?.Input.Audio ?? AudioFrom.Wired;
            Sample[] audio = input;
            hasInput = false;
            switch (from.Kind)
            {
                case AudioFromKind.Wired:
                    hasInput = (mode & WorkModes.WM_READ) != 0;
                    break;
                case AudioFromKind.Mono:
                case AudioFromKind.Stereo:
                    {
                        var device = buzz?.EngineInput;
                        if (device == null || !device.IsOpen || n > player.Source.Length) break;
                        var buffer = player.Source;
                        var left = device.Channel(from.Channel);
                        var right = from.Kind == AudioFromKind.Stereo ? device.Channel(from.Channel + 1) : left;
                        int m = Math.Min(n, device.BlockFrames);
                        for (int i = 0; i < m; i++)
                        {
                            buffer[i].L = left[i] * 32768f;
                            buffer[i].R = right[i] * 32768f;
                        }
                        for (int i = m; i < n; i++) buffer[i] = default;
                        audio = buffer;
                        hasInput = true;
                        break;
                    }
                case AudioFromKind.Track:
                    if (route.Tap != null && n <= player.Source.Length)
                    {
                        route.Tap.Read(player.Source, n);
                        audio = player.Source;
                        hasInput = true;
                    }
                    break;
            }
            if (hasInput)
            {
                float l = 0, r = 0;
                for (int i = 0; i < n && i < audio.Length; i++)
                {
                    float a = audio[i].L < 0 ? -audio[i].L : audio[i].L, b = audio[i].R < 0 ? -audio[i].R : audio[i].R;
                    if (a > l) l = a;
                    if (b > r) r = b;
                }
                if (l > 0 || r > 0) player.NotePeak(l, r);
            }
            return audio;
        }

        /// <summary>A take of the track's input records this block (silence when nothing comes in).</summary>
        internal static void Record(PlayerState player, AudioCapture capture, Sample[] input, int n, bool hasInput)
        {
            if (!hasInput)
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
