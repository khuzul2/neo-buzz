using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Session recording, engine side (docs/specs/session-view.md 0.7).
    ///
    /// MIDI thread (<see cref="OnMidiInput"/>, from ReBuzzCore.RouteMidiInput): armed tracks
    /// take the keyboard. Their machines play what comes in (each MIDI device calls from its own
    /// thread), and while a track records, each message is stamped with when it was heard and
    /// where the track's clip was then, under the gate, and queued for the UI thread. A release
    /// goes where its press went (0.6.3), whatever was armed or disarmed in between.
    ///
    /// Audio thread: the metronome (<see cref="MixMetronome"/>), and the count-in of a take
    /// started from a stopped transport (<see cref="LaunchTake"/>).
    ///
    /// UI thread (<see cref="SessionLayout"/>): arms tracks, opens and closes record windows,
    /// drains the queue into takes.
    /// </summary>
    internal sealed partial class SessionCore
    {
        // ------------------------------------------------------------------ arm

        /// <summary>What is armed, published whole (immutable) for the MIDI thread.</summary>
        sealed class ArmState
        {
            public static readonly ArmState None = new ArmState(Array.Empty<SequenceCore>(), Array.Empty<CaptureRing>());

            public ArmState(SequenceCore[] tracks, CaptureRing[] rings)
            {
                Tracks = tracks;
                Rings = rings;
                Machines = tracks.Select(s => s.MachineCore).Where(m => m != null).Distinct().ToArray();
            }

            public readonly SequenceCore[] Tracks;
            public readonly CaptureRing[] Rings;     // one per track: what it was played lately (Capture MIDI)
            public readonly MachineCore[] Machines;
        }

        volatile ArmState arm = ArmState.None;

        /// <summary>Tracks armed for recording (not saved; a new song starts with none).</summary>
        internal IReadOnlyList<SequenceCore> ArmedTracks => arm.Tracks;

        public bool IsArmed(ISequence track) => track is SequenceCore seq && Array.IndexOf(arm.Tracks, seq) >= 0;

        /// <summary>UI thread: arms or disarms a track. An armed track keeps what it is played lately, for Capture MIDI.</summary>
        internal void SetArmed(SequenceCore seq, bool on)
        {
            if (seq == null) return;
            var current = arm;
            int at = Array.IndexOf(current.Tracks, seq);
            if ((at >= 0) == on) return;
            arm = on
                ? new ArmState(current.Tracks.Append(seq).ToArray(), current.Rings.Append(new CaptureRing()).ToArray())
                : new ArmState(current.Tracks.Where((_, i) => i != at).ToArray(), current.Rings.Where((_, i) => i != at).ToArray());
            RaisePropertyChangedOnUi(nameof(ArmedTracks));
        }

        /// <summary>A track's sequence was removed (any thread): it is armed no more, so no input goes to its machine.</summary>
        void Disarm(SequenceCore seq)
        {
            var current = arm;
            int at = Array.IndexOf(current.Tracks, seq);
            if (at < 0) return;
            arm = new ArmState(current.Tracks.Where((_, i) => i != at).ToArray(), current.Rings.Where((_, i) => i != at).ToArray());
            RaisePropertyChangedOnUi(nameof(ArmedTracks));
        }

        void DisarmAll()
        {
            arm = ArmState.None;
            recordWindows = Array.Empty<RecordWindow>();
            Array.Clear(heldNotes);
            Array.Clear(heldPedals);
        }

        // ------------------------------------------------------------------ capture (Capture MIDI)

        /// <summary>The last messages an armed track was played, with when they were heard (1/240 tick). Thread-safe.</summary>
        internal sealed class CaptureRing
        {
            public const int Size = 4096;
            readonly int[] data = new int[Size];
            readonly long[] time = new long[Size];
            readonly object sync = new object();
            int start, count;

            public void Add(int message, long heard)
            {
                lock (sync)
                {
                    if (count == Size)
                    {
                        start = (start + 1) % Size;
                        count--;
                    }
                    int at = (start + count) % Size;
                    data[at] = message;
                    time[at] = heard;
                    count++;
                }
            }

            public void Clear()
            {
                lock (sync)
                {
                    start = 0;
                    count = 0;
                }
            }

            /// <summary>Takes out what was heard from <paramref name="from"/> on, oldest first, and forgets everything.</summary>
            public List<(long Time, int Data)> TakeSince(long from)
            {
                var list = new List<(long, int)>();
                lock (sync)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int at = (start + i) % Size;
                        if (time[at] >= from) list.Add((time[at], data[at]));
                    }
                    start = 0;
                    count = 0;
                }
                return list;
            }
        }

        /// <summary>
        /// Audio thread, the transport starting or jumping: what armed tracks were played
        /// before is on another timeline, not something to capture.
        /// </summary>
        void ForgetCaptured()
        {
            var rings = arm.Rings;
            for (int i = 0; i < rings.Length; i++)
                rings[i].Clear();
        }

        /// <summary>Audio thread: a clip stopped on a track at a clock (exclusive end).</summary>
        static void NoteStopped(SequenceCore seq, PatternCore clip, long clock)
        {
            Volatile.Write(ref seq.SessionStoppedAt, clock);
            seq.SessionStoppedClip = clip;
        }

        /// <summary>UI thread: what an armed track was played since <paramref name="from"/> (heard time), taken out of its ring.</summary>
        internal List<(long Time, int Data)> TakeCaptured(SequenceCore seq, long from)
        {
            var state = arm;
            int at = Array.IndexOf(state.Tracks, seq);
            return at < 0 ? new List<(long, int)>() : state.Rings[at].TakeSince(from);
        }

        /// <summary>
        /// UI thread (Capture MIDI): launches a clip on the next tick in phase with a grid point
        /// already passed: it plays from where it would be had it started at <paramref name="originClock"/>.
        /// </summary>
        internal void LaunchInPhase(SequenceCore seq, PatternCore clip, long originClock)
        {
            if (seq == null || clip == null || clip.IsDeleted || seq.IsRemoved) return;
            lock (gate)
            {
                long c = scheduler.Clock;
                if (c < 0) return;
                scheduler.LaunchAt(seq, clip, c + 1, Math.Min(originClock, c + 1), LaunchSpec.Looped, Math.Min(originClock, c + 1));
            }
            RaiseTrackStateChanged(seq);
        }

        /// <summary>The session's bar grid starts here (a scene's time signature change restarts it).</summary>
        internal long BarGridOrigin => scheduler.GridOrigin;

        // ------------------------------------------------------------------ record windows

        /// <summary>
        /// A track records while one of these is open (immutable; replaced to change): into its
        /// take, for the take's first pass only; or, without a take (overdub), into whatever
        /// clip it plays. From and Until are heard session time, in 1/240 tick.
        /// </summary>
        internal sealed class RecordWindow
        {
            public RecordWindow(SequenceCore track, PatternCore take, int firstPassTicks, long from, long until, bool thenOverdub = false)
            {
                Track = track;
                Take = take;
                FirstPassTicks = firstPassTicks;
                From = from;
                Until = until;
                ThenOverdub = thenOverdub;
            }

            public SequenceCore Track { get; }
            public PatternCore Take { get; }
            public int FirstPassTicks { get; }
            public long From { get; }
            public long Until { get; }
            /// <summary>Session Record is on: after its first pass the take goes on recording, as an overdub.</summary>
            public bool ThenOverdub { get; }
        }

        volatile RecordWindow[] recordWindows = Array.Empty<RecordWindow>();

        internal RecordWindow[] RecordWindows => recordWindows;

        /// <summary>UI thread: the windows from now on.</summary>
        internal void SetRecordWindows(RecordWindow[] windows) => recordWindows = windows ?? Array.Empty<RecordWindow>();

        // ------------------------------------------------------------------ input

        /// <summary>One stamped message of a recording track, for the UI thread.</summary>
        internal struct TakeInput
        {
            public SequenceCore Track;
            public PatternCore Clip;
            public long ClipStart;
            public TakeStamp Stamp;
            /// <summary>Automation: the parameter moved (the stamp's data is its value); null for MIDI.</summary>
            public ParameterCore Parameter;
            public int ParameterTrack;
            /// <summary>A take's input after its first pass (Session Record on): an overdub.</summary>
            public bool PastFirstPass;
        }

        const int RingSize = 4096;
        readonly TakeInput[] ring = new TakeInput[RingSize];
        int ringStart, ringCount;
        readonly object ringLock = new object();     // a leaf: taken under the gate, never the other way
        int droppedInputs;
        int drainPosted;
        Action drain;

        /// <summary>UI thread: stamped input is waiting (<see cref="TakeInputs"/>).</summary>
        internal event Action TakeInputArrived;

        /// <summary>Messages lost because the queue was full (the UI thread stalled for thousands of notes).</summary>
        internal int DroppedInputs => Volatile.Read(ref droppedInputs);

        // What each held note's press went to: the armed machines then, or the MIDI focus
        // routing (an empty array). A release goes the same way (0.6.3).
        static readonly MachineCore[] FocusRouted = Array.Empty<MachineCore>();
        readonly MachineCore[][] heldNotes = new MachineCore[16 * 128][];
        readonly MachineCore[][] heldPedals = new MachineCore[16][];

        /// <summary>
        /// MIDI thread: plays and records the input of armed tracks. True when it went to armed
        /// tracks' machines; false leaves it to the usual routing (MIDI focus, channels).
        /// </summary>
        internal bool OnMidiInput(int data)
        {
            int status = data & 0xFF;
            if (status >= 0xF0) return false;            // system messages go where they always did
            int kind = status & 0xF0;
            int velocity = (data >> 16) & 0x7F;
            int channel = status & 0x0F;
            int key = (channel << 7) | ((data >> 8) & 0x7F);
            // The sustain pedal pairs like a note (down, up), per channel: its release must reach
            // the machines its press held, or their notes ring on.
            bool pedal = kind == 0xB0 && ((data >> 8) & 0x7F) == 64;
            bool noteOn = (kind == 0x90 && velocity > 0) || (pedal && velocity >= 64);
            bool noteOff = kind == 0x80 || (kind == 0x90 && velocity == 0) || (pedal && velocity < 64);
            ref MachineCore[] held = ref pedal ? ref heldPedals[channel] : ref heldNotes[key];

            var state = arm;
            var machines = state.Machines;
            if (noteOff)
            {
                var pressed = Interlocked.Exchange(ref held, null);
                if (pressed != null)
                {
                    if (pressed.Length == 0) return false;
                    machines = pressed;
                }
                else if (machines.Length == 0)
                {
                    return false;
                }
            }
            else
            {
                if (machines.Length == 0)
                {
                    if (noteOn) Volatile.Write(ref held, FocusRouted);
                    return false;
                }
                if (noteOn) Volatile.Write(ref held, machines);
            }

            for (int i = 0; i < machines.Length; i++)
            {
                var machine = machines[i];
                var dll = machine.DLL;
                if (dll == null || dll.IsMissing || dll.IsCrashed || !machine.Ready) continue;
                bool polac = dll.Name == "Polac VSTi 1.1" || dll.Name == "Polac VST 1.1";
                buzz.MachineManager.SendMidiInput(machine, data, polac);
            }

            bool kept = kind == 0x80 || kind == 0x90 || kind == 0xA0 || kind == 0xB0 || kind == 0xD0 || kind == 0xE0;
            if (kept && !buzz.SongLoading)
                Stamp(data & 0xFFFFFF, recordWindows, state);
            return true;
        }

        void Stamp(int data, RecordWindow[] windows, ArmState state)
        {
            bool queued = false;
            lock (gate)
            {
                double heard = HeardTicks();
                if (heard < 0) return;
                long time = (long)Math.Floor(heard * TakeTime.TimeBase);
                long tick = time / TakeTime.TimeBase;
                int fraction = (int)(time - tick * TakeTime.TimeBase);
                // Capture MIDI: every armed track keeps what it is played, recording or not.
                for (int i = 0; i < state.Rings.Length; i++)
                    state.Rings[i].Add(data, time);
                for (int i = 0; i < windows.Length; i++)
                {
                    var w = windows[i];
                    if (time < w.From || time >= w.Until) continue;
                    if (!scheduler.TryGetPlayingAt(w.Track, tick, out var clip, out int position, out int end, out long start)) continue;
                    if (w.Take != null && !ReferenceEquals(clip, w.Take)) continue;
                    bool past = w.FirstPassTicks > 0 && tick - start >= w.FirstPassTicks;
                    if (past && !w.ThenOverdub) continue;
                    var input = new TakeInput
                    {
                        Track = w.Track,
                        Clip = clip,
                        ClipStart = start,
                        Stamp = new TakeStamp(data, time, position * TakeTime.TimeBase + fraction, end * TakeTime.TimeBase),
                        PastFirstPass = past
                    };
                    lock (ringLock)
                    {
                        if (ringCount == RingSize)
                        {
                            droppedInputs++;
                            continue;
                        }
                        ring[(ringStart + ringCount) % RingSize] = input;
                        ringCount++;
                    }
                    queued = true;
                }
            }
            if (queued && Interlocked.Exchange(ref drainPosted, 1) == 0)
                buzz.dispatcher.BeginInvoke(drain ??= OnDrain);
        }

        /// <summary>
        /// A parameter was moved (ParameterCore.SetValue without its "don't record" flag), on the
        /// UI thread: a recording track of its machine records it into the clip it records, as
        /// automation.
        /// </summary>
        internal void OnParameterChange(ParameterCore parameter, int track, int value)
        {
            var windows = recordWindows;
            if (windows.Length == 0 || parameter?.Group?.Machine is not MachineCore machine) return;
            // Moves made in ReBuzz (parameter windows, MIDI controller mappings) come on the UI
            // thread. Machines move parameters themselves from the audio and IPC threads (a
            // peer controller, an LFO): that's not a performance to record.
            if (ReBuzzCore.AudioLock.IsHeldByCurrentThread || !buzz.dispatcher.CheckAccess() || buzz.SongLoading) return;
            bool queued = false;
            lock (gate)
            {
                double heard = HeardTicks();
                if (heard < 0) return;
                long time = (long)Math.Floor(heard * TakeTime.TimeBase);
                long tick = time / TakeTime.TimeBase;
                int fraction = (int)(time - tick * TakeTime.TimeBase);
                for (int i = 0; i < windows.Length; i++)
                {
                    var w = windows[i];
                    if (!ReferenceEquals(w.Track.MachineCore, machine) || time < w.From || time >= w.Until) continue;
                    if (!scheduler.TryGetPlayingAt(w.Track, tick, out var clip, out int position, out int end, out long start)) continue;
                    if (w.Take != null && !ReferenceEquals(clip, w.Take)) continue;
                    bool past = w.FirstPassTicks > 0 && tick - start >= w.FirstPassTicks;
                    if (past && !w.ThenOverdub) continue;
                    var input = new TakeInput
                    {
                        Track = w.Track,
                        Clip = clip,
                        ClipStart = start,
                        Stamp = new TakeStamp(value, time, position * TakeTime.TimeBase + fraction, end * TakeTime.TimeBase),
                        Parameter = parameter,
                        ParameterTrack = track,
                        PastFirstPass = past
                    };
                    lock (ringLock)
                    {
                        if (ringCount == RingSize)
                        {
                            droppedInputs++;
                            continue;
                        }
                        ring[(ringStart + ringCount) % RingSize] = input;
                        ringCount++;
                    }
                    queued = true;
                }
            }
            if (queued && Interlocked.Exchange(ref drainPosted, 1) == 0)
                buzz.dispatcher.BeginInvoke(drain ??= OnDrain);
        }

        void OnDrain()
        {
            Volatile.Write(ref drainPosted, 0);
            TakeInputArrived?.Invoke();
        }

        /// <summary>UI thread: moves the queued input into <paramref name="into"/>, oldest first.</summary>
        internal void TakeInputs(List<TakeInput> into)
        {
            lock (ringLock)
            {
                for (int i = 0; i < ringCount; i++)
                {
                    int at = (ringStart + i) % RingSize;
                    into.Add(ring[at]);
                    ring[at] = default;
                }
                ringStart = 0;
                ringCount = 0;
            }
        }

        // ------------------------------------------------------------------ takes

        int pendingCountIn;             // ticks; under the gate, while stopped
        long countInUntil = -1;         // clock the count-in ends on (the take's start); -1 none

        /// <summary>
        /// UI thread: launches a take, looped from its start: on the launch grid while the
        /// transport runs; from a stopped transport after <paramref name="countInTicks"/>, which
        /// the metronome counts in. Starts the transport if needed.
        /// </summary>
        internal void LaunchTake(SequenceCore seq, PatternCore take, int countInTicks)
        {
            if (seq == null || take == null || take.IsDeleted || seq.IsRemoved) return;
            lock (gate)
            {
                if (!scheduler.IsRunning)
                {
                    int ticks = Math.Max(0, countInTicks);
                    pendingCountIn = Math.Max(pendingCountIn, ticks);
                    scheduler.LaunchAt(seq, take, ticks, long.MinValue, LaunchSpec.Looped);
                }
                else
                {
                    scheduler.Launch(seq, take, QuantizeTicks(null), HeardClock(), LaunchSpec.Looped, 0);
                }
            }
            StartTransportIfStopped();
            RaiseTrackStateChanged(seq);
        }

        /// <summary>Audio thread, transport started: a pending count-in runs from the first tick.</summary>
        void StartCountIn(long firstTick)
        {
            if (pendingCountIn > 0)
                Volatile.Write(ref countInUntil, firstTick + pendingCountIn);
            pendingCountIn = 0;
        }

        void ResetCountIn()
        {
            pendingCountIn = 0;
            Volatile.Write(ref countInUntil, -1);
        }

        /// <summary>UI thread: heard session time now (1/240 tick), or -1 while stopped.</summary>
        internal long HeardTimeNow()
        {
            lock (gate)
            {
                double heard = HeardTicks();
                return heard < 0 ? -1 : (long)Math.Floor(heard * TakeTime.TimeBase);
            }
        }

        /// <summary>UI thread: the clock the clip counts from on the track, if it plays there now; else -1.</summary>
        internal long StartOf(SequenceCore seq, PatternCore clip)
        {
            lock (gate)
            {
                return ReferenceEquals(scheduler.GetPlaying(seq), clip) ? scheduler.GetStartClock(seq) : -1;
            }
        }

        /// <summary>UI thread: whether the clip plays or waits to on the track.</summary>
        internal bool IsPlayingOrQueued(SequenceCore seq, PatternCore clip)
        {
            lock (gate)
            {
                return ReferenceEquals(scheduler.GetPlaying(seq), clip) || ReferenceEquals(scheduler.GetQueued(seq), clip);
            }
        }

        // ------------------------------------------------------------------ metronome

        readonly MetronomeClick click = new MetronomeClick();
        long clickTick = long.MinValue;

        /// <summary>The metronome clicks while the session plays (the layout keeps it from the song's settings).</summary>
        internal volatile bool MetronomeOn;

        /// <summary>
        /// Audio thread (AudioLock held), after a block is rendered, before PosInTick moves past
        /// it: a click on each beat of the bar grid while the metronome is on or a count-in
        /// runs, mixed into the output only (not into the master tap, so not recorded).
        /// </summary>
        internal void MixMetronome(float[] buffer, int offset, int frames)
        {
            var mi = ReBuzzCore.masterInfo;
            if (mi.PosInTick == 0)
            {
                long c = scheduler.Clock;
                if (c >= 0 && c != clickTick)
                {
                    clickTick = c;
                    if (MetronomeOn || c < Volatile.Read(ref countInUntil))
                    {
                        int tpb = Math.Max(1, buzz.TPB);
                        long rel = c - scheduler.GridOrigin;
                        if (rel >= 0 && rel % tpb == 0)
                            click.Trigger((rel / tpb) % Math.Max(1, beatsPerBar) == 0, mi.SamplesPerSec);
                    }
                }
            }
            if (click.Remaining > 0)
                click.MixInto(buffer, offset, frames);
        }

        void StopMetronome()
        {
            click.Stop();
            clickTick = long.MinValue;
        }
    }
}
