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

        volatile SequenceCore[] armed = Array.Empty<SequenceCore>();
        volatile MachineCore[] armedMachines = Array.Empty<MachineCore>();

        /// <summary>Tracks armed for recording (not saved; a new song starts with none).</summary>
        internal IReadOnlyList<SequenceCore> ArmedTracks => armed;

        public bool IsArmed(ISequence track) => track is SequenceCore seq && Array.IndexOf(armed, seq) >= 0;

        /// <summary>UI thread: arms or disarms a track.</summary>
        internal void SetArmed(SequenceCore seq, bool on)
        {
            if (seq == null) return;
            var current = armed;
            if ((Array.IndexOf(current, seq) >= 0) == on) return;
            var next = on ? current.Append(seq).ToArray() : current.Where(s => s != seq).ToArray();
            armedMachines = next.Select(s => s.MachineCore).Where(m => m != null).Distinct().ToArray();
            armed = next;
            RaisePropertyChangedOnUi(nameof(ArmedTracks));
        }

        void DisarmAll()
        {
            armedMachines = Array.Empty<MachineCore>();
            armed = Array.Empty<SequenceCore>();
            recordWindows = Array.Empty<RecordWindow>();
            Array.Clear(heldNotes);
        }

        // ------------------------------------------------------------------ record windows

        /// <summary>
        /// A track records while one of these is open (immutable; replaced to change): into its
        /// take, for the take's first pass only; or, without a take (overdub), into whatever
        /// clip it plays. From and Until are heard session time, in 1/240 tick.
        /// </summary>
        internal sealed class RecordWindow
        {
            public RecordWindow(SequenceCore track, PatternCore take, int firstPassTicks, long from, long until)
            {
                Track = track;
                Take = take;
                FirstPassTicks = firstPassTicks;
                From = from;
                Until = until;
            }

            public SequenceCore Track { get; }
            public PatternCore Take { get; }
            public int FirstPassTicks { get; }
            public long From { get; }
            public long Until { get; }

            public RecordWindow ClosedAt(long until) => new RecordWindow(Track, Take, FirstPassTicks, From, until);
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
            int key = ((status & 0x0F) << 7) | ((data >> 8) & 0x7F);
            bool noteOn = kind == 0x90 && velocity > 0;
            bool noteOff = kind == 0x80 || (kind == 0x90 && velocity == 0);

            var machines = armedMachines;
            if (noteOff)
            {
                var pressed = Interlocked.Exchange(ref heldNotes[key], null);
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
                    if (noteOn) Volatile.Write(ref heldNotes[key], FocusRouted);
                    return false;
                }
                if (noteOn) Volatile.Write(ref heldNotes[key], machines);
            }

            for (int i = 0; i < machines.Length; i++)
            {
                var machine = machines[i];
                var dll = machine.DLL;
                if (dll == null || dll.IsMissing) continue;
                bool polac = dll.Name == "Polac VSTi 1.1" || dll.Name == "Polac VST 1.1";
                buzz.MachineManager.SendMidiInput(machine, data, polac);
            }

            var windows = recordWindows;
            if (windows.Length > 0 && !buzz.SongLoading)
                Stamp(data & 0xFFFFFF, windows);
            return true;
        }

        void Stamp(int data, RecordWindow[] windows)
        {
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
                    if (time < w.From || time >= w.Until) continue;
                    if (!scheduler.TryGetPlayingAt(w.Track, tick, out var clip, out int position, out int end, out long start)) continue;
                    if (w.Take != null && !ReferenceEquals(clip, w.Take)) continue;
                    if (w.FirstPassTicks > 0 && tick - start >= w.FirstPassTicks) continue;
                    var input = new TakeInput
                    {
                        Track = w.Track,
                        Clip = clip,
                        ClipStart = start,
                        Stamp = new TakeStamp(data, time, position * TakeTime.TimeBase + fraction, end * TakeTime.TimeBase)
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
