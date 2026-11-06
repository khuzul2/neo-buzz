#nullable disable
// Session View engine core (fork addition). See docs/specs/session-view.md, section 4.1.
//
// Pure scheduling logic with no dependency on the rest of ReBuzz, so it can be unit tested
// on its own (ReBuzz.SessionTests links this file).
//
// Threading: requests (Launch*, Stop*, BackToArrangement, Forget*, Reset), engine calls
// (Start, Advance, Halt) and the list-walking properties (HasActivity, IsAnyOverridden) must
// be made under one external lock (SessionCore's gate). Per-track reads (GetPlaying,
// GetPosition, GetState, ...) are lock-free: they go through ISessionTrack.SessionState and
// tolerate running concurrently with the engine.

using System;
using System.Collections.Generic;
using System.Threading;

namespace ReBuzz.Core.Session
{
    /// <summary>A track keeps a slot for its scheduler state so reads need no shared dictionary.</summary>
    internal interface ISessionTrack
    {
        object SessionState { get; set; }
    }

    internal enum SessionChangeKind
    {
        /// <summary>A clip started (new clip, switch, or relaunch).</summary>
        Launched,
        /// <summary>A stop request took effect.</summary>
        Stopped,
        /// <summary>A one-shot clip reached its end.</summary>
        Ended,
        /// <summary>The transport stopped while the clip was playing.</summary>
        Halted,
        /// <summary>
        /// The clip started on another track (a clip plays in one place, 0.4.1), so it stopped
        /// here; the machine keeps playing it there, so nothing is silenced.
        /// </summary>
        Moved
    }

    internal readonly record struct SessionChange<TTrack, TClip>(
        TTrack Track, TClip Previous, TClip Current, SessionChangeKind Kind);

    /// <summary>
    /// Where a clip plays within its pattern (0.5, Live's clip markers): it starts at
    /// <see cref="Start"/>, plays to the end of the loop region, then (with <see cref="Loop"/>)
    /// repeats the region <see cref="LoopStart"/>..<see cref="LoopEnd"/>; without loop it ends
    /// there. LoopEnd 0 means the clip's end. Immutable.
    /// </summary>
    internal sealed class ClipShape
    {
        public static readonly ClipShape Looped = new ClipShape(0, 0, 0, true);
        public static readonly ClipShape OneShot = new ClipShape(0, 0, 0, false);

        public ClipShape(int start, int loopStart, int loopEnd, bool loop)
        {
            Start = Math.Max(0, start);
            LoopStart = Math.Max(0, loopStart);
            LoopEnd = Math.Max(0, loopEnd);
            Loop = loop;
        }

        public int Start { get; }
        public int LoopStart { get; }
        public int LoopEnd { get; }
        public bool Loop { get; }

        /// <summary>The markers for a clip of <paramref name="length"/> ticks: start, loop start, end (exclusive).</summary>
        public (int Start, int LoopStart, int End) Resolve(int length)
        {
            int end = LoopEnd > 0 ? Math.Min(LoopEnd, length) : length;
            if (end < 1) end = 1;
            return (Math.Min(Start, end - 1), Math.Min(LoopStart, end - 1), end);
        }

        /// <summary>Position after <paramref name="elapsed"/> ticks, or -1 once a one-shot clip has ended.</summary>
        public int PositionAt(long elapsed, int length)
        {
            if (elapsed < 0) return -1;
            var (start, loopStart, end) = Resolve(length);
            long p = start + elapsed;
            if (p < end) return (int)p;
            if (!Loop) return -1;
            return (int)(loopStart + (p - end) % (end - loopStart));
        }

        /// <summary>Ticks since the clip last entered: from its start, or from its loop start after a wrap.</summary>
        public long CycleElapsed(long elapsed, int length)
        {
            var (start, loopStart, end) = Resolve(length);
            long first = end - start;
            if (elapsed < first || !Loop) return elapsed;
            return (elapsed - first) % (end - loopStart);
        }

        /// <summary>
        /// Elapsed ticks at which this clip is where another clip was (legato): the position is
        /// wrapped into this clip's loop if it's past the end; 0 if the clip never gets there.
        /// </summary>
        public long LegatoElapsed(int position, int length)
        {
            var (start, loopStart, end) = Resolve(length);
            if (position >= end)
            {
                if (!Loop) return 0;
                position = loopStart + (position - loopStart) % (end - loopStart);
            }
            if (position >= start && position < end) return position - start;
            if (Loop && position >= loopStart) return (end - start) + (position - loopStart);
            return 0;
        }
    }

    /// <summary>Live's follow actions (0.6). Values match BuzzGUI.Interfaces.ClipFollowAction.</summary>
    internal enum FollowAction { None, Stop, PlayAgain, Previous, Next, First, Last, Any, Other, Jump }

    /// <summary>
    /// A clip's follow actions (0.6, Live's): when its follow time comes, action A happens with
    /// <see cref="ChanceA"/> percent, else action B. Linked: the time is the end of the clip (after
    /// <see cref="Multiplier"/> passes of a looping clip); unlinked: <see cref="TimeTicks"/> after
    /// it starts. Jump targets are slot indexes (scene rows) in the track. Immutable.
    /// </summary>
    internal sealed class FollowSpec
    {
        public FollowSpec(FollowAction a, FollowAction b, int chanceA, bool linked, int multiplier, int timeTicks, int jumpA = -1, int jumpB = -1)
        {
            A = a;
            B = b;
            ChanceA = Math.Clamp(chanceA, 0, 100);
            Linked = linked;
            Multiplier = Math.Max(1, multiplier);
            TimeTicks = Math.Max(1, timeTicks);
            JumpA = jumpA;
            JumpB = jumpB;
        }

        public FollowAction A { get; }
        public FollowAction B { get; }
        public int ChanceA { get; }
        public bool Linked { get; }
        public int Multiplier { get; }
        public int TimeTicks { get; }
        public int JumpA { get; }
        public int JumpB { get; }

        /// <summary>The action for a roll of 0..99, and its jump target.</summary>
        public (FollowAction Action, int Jump) Pick(int roll) => roll < ChanceA ? (A, JumpA) : (B, JumpB);
    }

    /// <summary>How a clip is launched (0.5): its shape, legato, repeat rate, gain; and (0.6) its follow actions and slot. Immutable.</summary>
    internal sealed class LaunchSpec
    {
        public static readonly LaunchSpec Looped = new LaunchSpec(ClipShape.Looped);
        public static readonly LaunchSpec OneShot = new LaunchSpec(ClipShape.OneShot);

        public LaunchSpec(ClipShape shape, bool legato = false, int repeatTicks = 0, double gain = 1.0, FollowSpec follow = null, int slot = -1)
        {
            Shape = shape ?? ClipShape.Looped;
            Legato = legato;
            RepeatTicks = Math.Max(0, repeatTicks);
            Gain = Math.Clamp(gain, 0, 1);
            Follow = follow;
            Slot = slot;
        }

        public static LaunchSpec For(bool loop) => loop ? Looped : OneShot;

        public ClipShape Shape { get; }
        /// <summary>Take over the play position of the clip playing on the track.</summary>
        public bool Legato { get; }
        /// <summary>Repeat mode: restart every this many ticks while held; 0 when not repeating.</summary>
        public int RepeatTicks { get; }
        /// <summary>Volume of the clip's machine (velocity), 0..1.</summary>
        public double Gain { get; }
        /// <summary>Follow actions (0.6), or null.</summary>
        public FollowSpec Follow { get; }
        /// <summary>The slot (scene row) the clip is launched from, or -1: where follow actions find its group.</summary>
        public int Slot { get; }

        public LaunchSpec WithoutRepeat() => RepeatTicks == 0 ? this : new LaunchSpec(Shape, Legato, 0, Gain, Follow, Slot);
        public LaunchSpec WithShape(ClipShape shape) => new LaunchSpec(shape, Legato, RepeatTicks, Gain, Follow, Slot);
    }

    internal sealed class SessionScheduler<TTrack, TClip>
        where TTrack : class, ISessionTrack
        where TClip : class
    {
        sealed class Pending
        {
            public Pending(bool isStop, TClip clip, long at, LaunchSpec spec, bool releaseOverride, long origin = long.MinValue, int stopAfter = 0, long heard = long.MinValue)
            {
                IsStop = isStop; Clip = clip; At = at; Spec = spec ?? LaunchSpec.Looped; ReleaseOverride = releaseOverride; Origin = origin; StopAfter = stopAfter; Heard = heard;
            }

            public static Pending StopAt(long at, bool releaseOverride = false) => new Pending(true, null, at, null, releaseOverride);

            public readonly bool IsStop;
            public readonly TClip Clip;
            public readonly long At;
            public readonly LaunchSpec Spec;
            public readonly bool ReleaseOverride;
            /// <summary>
            /// Clock value the clip counts its position from (position = clock - Origin), or
            /// long.MinValue for <see cref="At"/>: earlier than At starts the clip partway through
            /// (catch-up after a late request, or an explicit start offset).
            /// </summary>
            public readonly long Origin;
            /// <summary>Gate released before the clip started: it stops this many ticks after starting.</summary>
            public readonly int StopAfter;
            /// <summary>
            /// The grid point the listener hears the clip start on (0.6.1), or long.MinValue for
            /// the same as <see cref="Origin"/> (or <see cref="At"/>). It differs from Origin when
            /// the clip starts in step from an earlier phase (a start offset, an undone launch):
            /// follow actions, repeats and recording count from here, the position from Origin.
            /// </summary>
            public readonly long Heard;

            static long Shift(long value, long delta) => value == long.MinValue ? long.MinValue : value + delta;

            public Pending Shifted(long delta) =>
                new Pending(IsStop, Clip, At + delta, Spec, ReleaseOverride, Shift(Origin, delta), StopAfter, Shift(Heard, delta));

            public Pending With(LaunchSpec spec, int stopAfter) =>
                new Pending(IsStop, Clip, At, spec, ReleaseOverride, Origin, stopAfter, Heard);
        }

        /// <summary>
        /// A slot of a track's column, for follow actions (0.6): its clip, how it launches, and its
        /// own launch quantization (a code for <see cref="QuantizeTicksOf"/>; -1 for None or
        /// Global, which follow actions don't wait for). Null entries in a column are empty slots.
        /// </summary>
        public sealed class Slot
        {
            public Slot(TClip clip, LaunchSpec spec, int quantization = -1)
            {
                Clip = clip;
                Spec = spec ?? LaunchSpec.Looped;
                Quantization = quantization;
            }

            public TClip Clip { get; }
            public LaunchSpec Spec { get; }
            public int Quantization { get; }
        }

        /// <summary>Snapshot of a track before a launch, for undoing it (0.6).</summary>
        sealed class Memento
        {
            public TClip Playing;
            public long StartClock;
            public LaunchSpec Spec;
            public Pending Next;
        }

        sealed class TrackState
        {
            public TrackState(SessionScheduler<TTrack, TClip> owner, TTrack track) { Owner = owner; Track = track; }
            public readonly SessionScheduler<TTrack, TClip> Owner;
            public readonly TTrack Track;
            public volatile TClip Playing;
            /// <summary>Clock the playing clip counts from (can be negative with a start offset); NotStarted when none.</summary>
            public long StartClock = NotStarted;
            public volatile ClipShape Shape = ClipShape.Looped;
            public volatile int RepeatTicks;
            public long RepeatFrom;
            /// <summary>Clock of the tick the playing clip last (re)started on, and how late that was (catch-up ticks).</summary>
            public long LaunchedAt = NotStarted;
            public long LaunchLate;
            public double Gain = 1.0;
            // Follow actions (0.6): of the playing clip, when they're due, and its slot.
            public FollowSpec Follow;
            public long FollowAt = NotStarted;
            public int Slot = -1;
            public volatile Pending Next;
            public volatile bool Overridden;
        }

        const long NotStarted = long.MinValue;

        readonly Func<TClip, int> lengthOf;
        readonly List<TrackState> tracks = new List<TrackState>();
        Random random = new Random(1);

        /// <summary>The slots of a track's column, for follow actions (0.6); null: no column.</summary>
        public Func<TTrack, Slot[]> ColumnOf { get; set; }

        /// <summary>Ticks of a slot's quantization code (see <see cref="Slot.Quantization"/>).</summary>
        public Func<int, int> QuantizeTicksOf { get; set; }

        /// <summary>Follow actions happen (Live's global switch). Off, a due follow action is skipped until the next one.</summary>
        public volatile bool FollowActionsEnabled = true;

        /// <summary>
        /// Whether a clip or track can still play (0.6.1): false once its pattern was deleted or
        /// its sequence removed. Launches of a dead clip or onto a dead track are ignored, so
        /// stale references (a pad's map, a follow column, the undo history) can't bring them back.
        /// </summary>
        public Func<TClip, bool> IsLiveClip { get; set; }
        public Func<TTrack, bool> IsLiveTrack { get; set; }

        bool IsLive(TTrack track, TClip clip) =>
            (IsLiveClip == null || IsLiveClip(clip)) && (IsLiveTrack == null || IsLiveTrack(track));

        /// <summary>Seeds the random choices of follow actions (reproducible runs and tests).</summary>
        public void Seed(int seed) => random = new Random(seed);

        /// <summary>A number 0..max-1 from the follow actions' generator (scene follow actions use it too).</summary>
        public int NextRandom(int max) => random.Next(max);
        readonly List<SessionChange<TTrack, TClip>> changes = new List<SessionChange<TTrack, TClip>>();
        long clock = -1;
        long gridOrigin;

        /// <summary>
        /// Clock value the quantization grid counts from (0.5): bars and beats are multiples of
        /// their length after it. 0 (bars line up with the song) until a scene changes the beats
        /// per bar, which restarts the bar count there.
        /// </summary>
        public long GridOrigin => Volatile.Read(ref gridOrigin);

        public void SetGridOrigin(long origin) => Volatile.Write(ref gridOrigin, origin);

        /// <param name="lengthOf">Clip length in ticks; values below 1 count as 1.</param>
        public SessionScheduler(Func<TClip, int> lengthOf)
        {
            this.lengthOf = lengthOf ?? throw new ArgumentNullException(nameof(lengthOf));
        }

        /// <summary>Session ticks; -1 while the transport is stopped.</summary>
        public long Clock => Volatile.Read(ref clock);

        public bool IsRunning => Clock >= 0;

        /// <summary>Changes made by the last Start/Advance/Halt call. Owned by the engine thread.</summary>
        public IReadOnlyList<SessionChange<TTrack, TClip>> Changes => changes;

        /// <summary>True if any track plays or waits for a launch or stop.</summary>
        public bool HasActivity
        {
            get
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    var st = tracks[i];
                    if (st.Playing != null || st.Next != null) return true;
                }
                return false;
            }
        }

        // ------------------------------------------------------------------ requests

        /// <summary>
        /// First clock value strictly after the current tick on the quantization grid, or the
        /// next tick if <paramref name="quantizeTicks"/> is 0. Returns 0 ("when the transport
        /// starts") while stopped.
        /// </summary>
        public long NextQuantizedClock(int quantizeTicks) => Plan(quantizeTicks, Clock).At;

        long ImmediateClock()
        {
            long c = Clock;
            return c < 0 ? 0 : c + 1;
        }

        /// <summary>
        /// When a request made while the listener hears <paramref name="heardClock"/> takes effect.
        /// The grid point is the first one after what is heard, not after what the engine has
        /// rendered (which runs ahead by the output latency). If that point has been rendered
        /// already, the request is late: it takes effect on the next tick, and a launch counts
        /// its position from the grid point (<paramref name="origin"/>), so the clip plays in step
        /// and only what was already rendered is skipped. Quantization None never catches up: it
        /// means "start from the top, now". <paramref name="heardClock"/> is clamped to the
        /// rendered clock; pass <see cref="Clock"/> for no compensation.
        /// </summary>
        public (long At, long Origin) Plan(int quantizeTicks, long heardClock)
        {
            long c = Clock;
            if (c < 0) return (0, long.MinValue);
            if (quantizeTicks <= 0) return (c + 1, long.MinValue);
            long heard = Math.Min(heardClock, c);
            if (heard < 0) heard = 0;
            long origin = GridOrigin;
            long rel = heard - origin;
            long steps = rel >= 0 ? rel / quantizeTicks : -((-rel + quantizeTicks - 1) / quantizeTicks);
            long boundary = origin + (steps + 1) * quantizeTicks;
            return boundary > c ? (boundary, long.MinValue) : (c + 1, boundary);
        }

        public void Launch(TTrack track, TClip clip, int quantizeTicks, bool loop) =>
            Launch(track, clip, quantizeTicks, Clock, LaunchSpec.For(loop), 0);

        public void Launch(TTrack track, TClip clip, int quantizeTicks, bool loop, long heardClock, int startOffset) =>
            Launch(track, clip, quantizeTicks, heardClock, LaunchSpec.For(loop), startOffset);

        /// <summary>
        /// Queues a launch on the quantization grid (see <see cref="Plan"/>), starting the clip
        /// <paramref name="startOffset"/> ticks in.
        /// </summary>
        public void Launch(TTrack track, TClip clip, int quantizeTicks, long heardClock, LaunchSpec spec, int startOffset)
        {
            var (at, origin) = Plan(quantizeTicks, heardClock);
            long heard = startOffset > 0 ? (origin == long.MinValue ? at : origin) : long.MinValue;
            LaunchAt(track, clip, at, OriginWithOffset(at, origin, startOffset), spec, heard);
        }

        static long OriginWithOffset(long at, long origin, int startOffset)
        {
            if (startOffset <= 0) return origin;
            return (origin == long.MinValue ? at : origin) - startOffset;
        }

        /// <summary>
        /// Queues a launch at an absolute clock value (replacing any queued action). With an
        /// <paramref name="origin"/> before <paramref name="at"/> the clip starts partway through.
        /// A clip plays in one place at a time (Buzz patterns have one play position): a launch
        /// of the same clip queued on another track is dropped, and a track playing it hands it
        /// over when it starts here (<see cref="SessionChangeKind.Moved"/>, without a stop).
        /// </summary>
        public void LaunchAt(TTrack track, TClip clip, long at, bool loop, long origin = long.MinValue) =>
            LaunchAt(track, clip, at, origin, LaunchSpec.For(loop));

        public void LaunchAt(TTrack track, TClip clip, long at, long origin, LaunchSpec spec) =>
            LaunchAt(track, clip, at, origin, spec, long.MinValue);

        /// <summary>As above; <paramref name="heard"/> is the grid point it is heard to start on, when that isn't <paramref name="origin"/>.</summary>
        public void LaunchAt(TTrack track, TClip clip, long at, long origin, LaunchSpec spec, long heard)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (!IsLive(track, clip)) return;
            var target = GetOrCreate(track);
            target.Next = new Pending(false, clip, at, spec, false, origin, 0, heard);
            for (int i = 0; i < tracks.Count; i++)
            {
                var st = tracks[i];
                if (ReferenceEquals(st, target)) continue;
                var next = st.Next;
                if (next != null && !next.IsStop && ReferenceEquals(next.Clip, clip))
                    st.Next = null;
            }
        }

        /// <summary>Queues launches that all start on the same tick.</summary>
        public void LaunchScene(IEnumerable<(TTrack Track, TClip Clip)> slots, int quantizeTicks, bool loop) =>
            LaunchScene(slots, quantizeTicks, loop, Clock);

        public void LaunchScene(IEnumerable<(TTrack Track, TClip Clip)> slots, int quantizeTicks, bool loop, long heardClock)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            var spec = LaunchSpec.For(loop);
            LaunchScene(System.Linq.Enumerable.Select(slots, s => (s.Track, s.Clip, spec)), quantizeTicks, heardClock);
        }

        /// <summary>
        /// Queues launches that all start on the same tick, each with its own spec. Returns that
        /// tick (scene settings such as tempo take effect on it), and the grid point it is heard
        /// on: the same tick, or for a late launch the earlier grid point the clips start in
        /// step with (a time signature change restarts the bar grid there, 0.6.1).
        /// </summary>
        public (long At, long Grid) LaunchScene(IEnumerable<(TTrack Track, TClip Clip, LaunchSpec Spec)> slots, int quantizeTicks, long heardClock)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            var (at, origin) = Plan(quantizeTicks, heardClock);
            foreach (var (track, clip, spec) in slots)
            {
                if (track != null && clip != null)
                    LaunchAt(track, clip, at, origin, spec);
            }
            return (at, origin == long.MinValue ? at : origin);
        }

        /// <summary>
        /// Release of a clip's launch control (0.5). With <paramref name="stop"/> (Gate): a clip
        /// that plays stops on the grid; one still waiting to start stops a quantum after it
        /// starts. Without (Repeat): the clip stops repeating and plays on.
        /// </summary>
        public void Release(TTrack track, TClip clip, int quantizeTicks, long heardClock, bool stop)
        {
            var st = Find(track);
            if (st == null || clip == null) return;
            var next = st.Next;
            bool queued = next != null && !next.IsStop && ReferenceEquals(next.Clip, clip);
            if (!stop)
            {
                if (queued) st.Next = next.With(next.Spec.WithoutRepeat(), next.StopAfter);
                if (ReferenceEquals(st.Playing, clip)) st.RepeatTicks = 0;
                return;
            }
            if (queued)
            {
                st.Next = next.With(next.Spec.WithoutRepeat(), Math.Max(1, quantizeTicks));
                return;
            }
            // Another clip waiting to replace this one keeps its launch (the release doesn't cancel it).
            if (ReferenceEquals(st.Playing, clip) && IsRunning && (next == null || next.IsStop))
            {
                st.RepeatTicks = 0;
                long at = Plan(quantizeTicks, heardClock).At;
                if (next == null || at < next.At)
                    st.Next = Pending.StopAt(at);
            }
        }

        /// <summary>Changes the shape of a clip that plays or waits on the track (its markers were edited).</summary>
        public void Reshape(TTrack track, TClip clip, ClipShape shape)
        {
            var st = Find(track);
            if (st == null || clip == null || shape == null) return;
            if (ReferenceEquals(st.Playing, clip))
            {
                st.Shape = shape;
                Refollow(st);
            }
            var next = st.Next;
            if (next != null && !next.IsStop && ReferenceEquals(next.Clip, clip))
                st.Next = next.With(next.Spec.WithShape(shape), next.StopAfter);
        }

        /// <summary>
        /// The clip's length changed (0.6.1): a linked follow action moves with its end, as for
        /// a marker change.
        /// </summary>
        public void ClipLengthChanged(TClip clip)
        {
            for (int i = 0; i < tracks.Count; i++)
                if (ReferenceEquals(tracks[i].Playing, clip))
                    Refollow(tracks[i]);
        }

        /// <summary>
        /// A playing clip's shape or length changed: its linked follow action is due when the
        /// new shape says (counting from its launch), or at the end of the current pass if that
        /// is already behind; a clip that no longer loops follows where it ends.
        /// </summary>
        void Refollow(TrackState st)
        {
            if (st.Follow == null || !st.Follow.Linked || st.FollowAt == NotStarted || st.Playing == null || !IsRunning) return;
            long c = Clock;
            long heardStart = st.LaunchedAt == NotStarted ? c : st.LaunchedAt - st.LaunchLate;
            long due = FollowDue(st, heardStart);
            if (due > c)
            {
                st.FollowAt = due;
                return;
            }
            int length = Length(st.Playing);
            var (_, _, end) = st.Shape.Resolve(length);
            int position = st.Shape.PositionAt(c - st.StartClock, length);
            st.FollowAt = position < 0 ? c + 1 : c + (end - position);
        }

        /// <summary>Queues a stop. With nothing playing, cancels a queued launch instead.</summary>
        public void Stop(TTrack track, int quantizeTicks, bool immediate) => Stop(track, quantizeTicks, immediate, Clock);

        /// <summary>Queues a stop on the grid after what is heard (a late stop happens on the next tick).</summary>
        public void Stop(TTrack track, int quantizeTicks, bool immediate, long heardClock)
        {
            var st = Find(track);
            if (st == null) return;
            if (st.Playing == null || !IsRunning)
            {
                st.Next = null;
                return;
            }
            long at = immediate ? ImmediateClock() : Plan(quantizeTicks, heardClock).At;
            st.RepeatTicks = 0;
            st.Next = Pending.StopAt(at);
        }

        /// <summary>
        /// Stops the track at <paramref name="at"/> (0.6: a scene launch stopping the tracks it
        /// has no clip for). With nothing playing, cancels a queued launch.
        /// </summary>
        public void StopAt(TTrack track, long at)
        {
            var st = Find(track);
            if (st == null) return;
            if (st.Playing == null || !IsRunning)
            {
                st.Next = null;
                return;
            }
            st.RepeatTicks = 0;
            st.Next = Pending.StopAt(at);
        }

        public void StopAll(int quantizeTicks, bool immediate) => StopAll(quantizeTicks, immediate, Clock);

        public void StopAll(int quantizeTicks, bool immediate, long heardClock)
        {
            for (int i = 0; i < tracks.Count; i++)
                Stop(tracks[i].Track, quantizeTicks, immediate, heardClock);
        }

        /// <summary>Stops all clips on the next tick and gives every track back to the arrangement.</summary>
        public void BackToArrangement()
        {
            long at = ImmediateClock();
            for (int i = 0; i < tracks.Count; i++)
            {
                var st = tracks[i];
                if (st.Playing != null && IsRunning)
                {
                    st.Next = Pending.StopAt(at, releaseOverride: true);
                }
                else
                {
                    st.Next = null;
                    st.Overridden = false;
                }
            }
        }

        /// <summary>Drops a track (its sequence was removed). Returns the clip it was playing.</summary>
        public TClip Forget(TTrack track)
        {
            var st = Find(track);
            if (st == null) return null;
            var playing = st.Playing;
            tracks.Remove(st);
            track.SessionState = null;
            return playing;
        }

        /// <summary>
        /// Removes a clip everywhere (its pattern was deleted). Tracks that were playing it are
        /// added to <paramref name="stoppedTracks"/>.
        /// </summary>
        public void ForgetClip(TClip clip, List<TTrack> stoppedTracks)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                var st = tracks[i];
                var next = st.Next;
                if (next != null && !next.IsStop && ReferenceEquals(next.Clip, clip))
                    st.Next = null;
                if (ReferenceEquals(st.Playing, clip))
                {
                    Clear(st);
                    stoppedTracks?.Add(st.Track);
                }
            }
        }

        /// <summary>Forgets everything (new song / song loaded).</summary>
        public void Reset()
        {
            for (int i = 0; i < tracks.Count; i++)
                tracks[i].Track.SessionState = null;
            tracks.Clear();
            changes.Clear();
            Volatile.Write(ref clock, -1);
        }

        // ------------------------------------------------------------------ engine

        /// <summary>
        /// Transport started. The clock starts at <paramref name="initialClock"/> (the song
        /// position, so the launch grid lines up with the arrangement's bars), and launches
        /// queued while stopped take effect on this tick.
        /// </summary>
        public void Start(long initialClock)
        {
            if (initialClock < 0) initialClock = 0;
            changes.Clear();
            long c = Clock;
            if (c >= 0)
            {
                // Restarted while running: shift everything so clips keep their phase. A bar grid
                // restarted by a time signature change shifts with them (origin 0 stays with the
                // song's bars).
                long delta = initialClock - c;
                long origin = GridOrigin;
                if (origin != 0)
                    SetGridOrigin(origin + delta);
                for (int i = 0; i < tracks.Count; i++)
                {
                    var st = tracks[i];
                    if (st.StartClock != NotStarted) st.StartClock += delta;
                    if (st.LaunchedAt != NotStarted) st.LaunchedAt += delta;
                    if (st.FollowAt != NotStarted) st.FollowAt += delta;
                    st.RepeatFrom += delta;
                    var n = st.Next;
                    if (n != null)
                        st.Next = n.Shifted(delta);
                }
            }
            else
            {
                // Launches queued while stopped count from the start: "0" is the first tick,
                // "n" (a take after a count-in, 0.7) n ticks later.
                for (int i = 0; i < tracks.Count; i++)
                {
                    var st = tracks[i];
                    var n = st.Next;
                    if (n != null)
                        st.Next = n.Shifted(initialClock - Math.Min(0, n.At));
                }
            }
            Volatile.Write(ref clock, initialClock);
            ProcessDue();
        }

        /// <summary>Called once per tick while the transport runs.</summary>
        public void Advance()
        {
            changes.Clear();
            long c = Clock;
            if (c < 0) return;
            Volatile.Write(ref clock, c + 1);
            ProcessDue();
        }

        /// <summary>Transport stopped: every clip stops, queued actions and overrides are dropped.</summary>
        public void Halt()
        {
            changes.Clear();
            Volatile.Write(ref clock, -1);
            Volatile.Write(ref gridOrigin, 0);
            for (int i = 0; i < tracks.Count; i++)
            {
                var st = tracks[i];
                var playing = st.Playing;
                st.Next = null;
                Clear(st);
                st.LaunchedAt = NotStarted;
                st.Overridden = false;
                if (playing != null)
                    changes.Add(new SessionChange<TTrack, TClip>(st.Track, playing, null, SessionChangeKind.Halted));
            }
        }

        void ProcessDue()
        {
            long c = Clock;
            int firstChange = changes.Count;
            for (int i = 0; i < tracks.Count; i++)
            {
                var st = tracks[i];
                var playing = st.Playing;
                var next = st.Next;
                bool requestDue = next != null && next.At <= c;

                // Follow action (0.6). A request queued on the track (a launch, a stop, Stop All, a
                // Gate release) replaces it, now or later (0.6.1): the follow action waits a period.
                if (playing != null && st.FollowAt != NotStarted && c >= st.FollowAt)
                {
                    if (next == null)
                    {
                        FollowDueNow(st, c, playing);
                        next = st.Next;
                        requestDue = next != null && next.At <= c;
                    }
                    else
                    {
                        long period = FollowPeriod(st);
                        st.FollowAt = period > 0 ? c + period : NotStarted;
                    }
                }

                // A clip without loop ends, unless something replaces or stops it on this very
                // tick: then it's a switch (no note-offs between the two) or a stop.
                if (playing != null && !requestDue && st.Shape.PositionAt(c - st.StartClock, Length(playing)) < 0)
                {
                    Clear(st);
                    changes.Add(new SessionChange<TTrack, TClip>(st.Track, playing, null, SessionChangeKind.Ended));
                    playing = null;
                }

                bool launchedNow = false;
                if (requestDue)
                {
                    st.Next = null;
                    if (next.IsStop)
                    {
                        if (playing != null)
                        {
                            Clear(st);
                            changes.Add(new SessionChange<TTrack, TClip>(st.Track, playing, null, SessionChangeKind.Stopped));
                        }
                        if (next.ReleaseOverride) st.Overridden = false;
                    }
                    else
                    {
                        var spec = next.Spec;
                        long launchOrigin = next.Origin == long.MinValue ? c : Math.Min(next.Origin, c);
                        // Where it is heard to start: follow actions, repeats and recording count from here (0.6.1).
                        long heardStart = next.Heard == long.MinValue ? launchOrigin : Math.Min(next.Heard, c);
                        long origin = launchOrigin;
                        if (spec.Legato && playing != null)
                        {
                            // Legato: take over the position the previous clip has reached now.
                            int previousPosition = st.Shape.PositionAt(c - st.StartClock, Length(playing));
                            if (previousPosition >= 0)
                                origin = c - spec.Shape.LegatoElapsed(previousPosition, Length(next.Clip));
                        }
                        st.StartClock = origin;
                        st.Shape = spec.Shape;
                        st.RepeatTicks = spec.RepeatTicks;
                        st.RepeatFrom = heardStart;            // repeats stay on the grid after a late (catch-up) start
                        st.LaunchedAt = c;
                        st.LaunchLate = c - heardStart;
                        st.Gain = spec.Gain;
                        st.Overridden = true;
                        st.Playing = next.Clip;
                        st.Follow = spec.Follow;
                        st.Slot = spec.Slot;
                        st.FollowAt = FollowDue(st, heardStart);
                        launchedNow = true;
                        changes.Add(new SessionChange<TTrack, TClip>(st.Track, playing, next.Clip, SessionChangeKind.Launched));
                        if (next.StopAfter > 0)
                            st.Next = Pending.StopAt(Math.Max(c + 1, heardStart + next.StopAfter));     // gate released before the start: a step from its grid point
                    }
                }

                // Repeat mode: while held, the clip restarts every RepeatTicks from where it was launched.
                var current = st.Playing;
                int repeat = st.RepeatTicks;
                if (current != null && !launchedNow && repeat > 0)
                {
                    long since = c - st.RepeatFrom;
                    if (since > 0 && since % repeat == 0)
                    {
                        st.StartClock = c;
                        st.LaunchedAt = c;
                        st.LaunchLate = 0;
                        st.FollowAt = FollowDue(st, c);
                        changes.Add(new SessionChange<TTrack, TClip>(st.Track, current, current, SessionChangeKind.Launched));
                    }
                }
            }

            // A clip plays in one place: a clip that started here stops on any other track still
            // playing it (its stop could have been replaced by a later request on that track).
            int launchedEnd = changes.Count;
            for (int k = firstChange; k < launchedEnd; k++)
            {
                var launched = changes[k];
                if (launched.Kind != SessionChangeKind.Launched) continue;
                // Launched on two tracks on the same tick: the first keeps it (0.6.1), not neither.
                if (!ReferenceEquals(Find(launched.Track)?.Playing, launched.Current)) continue;
                for (int i = 0; i < tracks.Count; i++)
                {
                    var st = tracks[i];
                    if (ReferenceEquals(st.Track, launched.Track) || !ReferenceEquals(st.Playing, launched.Current)) continue;
                    Clear(st);
                    var next = st.Next;
                    if (next != null && next.IsStop) st.Next = null;
                    changes.Add(new SessionChange<TTrack, TClip>(st.Track, launched.Current, null, SessionChangeKind.Moved));
                }
            }
        }

        /// <summary>The track plays nothing any more (its queued request stays).</summary>
        static void Clear(TrackState st)
        {
            st.Playing = null;
            st.StartClock = NotStarted;
            st.RepeatTicks = 0;
            st.Follow = null;
            st.FollowAt = NotStarted;
        }

        // ------------------------------------------------------------------ follow actions (0.6)

        /// <summary>
        /// When the playing clip's follow action is due, counting from <paramref name="origin"/>
        /// (where playback started as heard): unlinked, its follow time later; linked, when it
        /// reaches its end (from where it started: its start marker, or where legato picked it
        /// up), plus Multiplier-1 passes of its loop region. NotStarted without follow actions.
        /// </summary>
        long FollowDue(TrackState st, long origin)
        {
            var f = st.Follow;
            var playing = st.Playing;
            if (f == null || playing == null) return NotStarted;
            if (!f.Linked) return origin + f.TimeTicks;
            int length = Length(playing);
            var shape = st.Shape;
            var (start, loopStart, end) = shape.Resolve(length);
            long elapsed = origin - st.StartClock;
            int position = elapsed >= 0 ? shape.PositionAt(elapsed, length) : start;
            if (position < 0) position = start;
            long due = origin + (end - position);
            if (shape.Loop) due += (long)(f.Multiplier - 1) * (end - loopStart);
            return due;
        }

        /// <summary>How long until a skipped follow action comes round again (0: never: the clip ends).</summary>
        long FollowPeriod(TrackState st)
        {
            var f = st.Follow;
            if (f == null || st.Playing == null) return 0;
            if (!f.Linked) return f.TimeTicks;
            if (!st.Shape.Loop) return 0;
            var (_, loopStart, end) = st.Shape.Resolve(Length(st.Playing));
            return (long)f.Multiplier * (end - loopStart);
        }

        /// <summary>
        /// A follow action is due on this tick: picks A or B and sets the track's request (a
        /// launch or stop due now, or a launch at the target's own quantization). With follow
        /// actions switched off, it waits for the next one instead.
        /// </summary>
        void FollowDueNow(TrackState st, long c, TClip playing)
        {
            var f = st.Follow;
            if (f == null) { st.FollowAt = NotStarted; return; }
            if (!FollowActionsEnabled)
            {
                long period = FollowPeriod(st);
                st.FollowAt = period > 0 ? c + period : NotStarted;
                return;
            }
            st.FollowAt = NotStarted;            // once per launch; the launch it makes sets the next
            var (action, jump) = f.Pick(random.Next(100));
            if (action == FollowAction.None)
            {
                st.Follow = null;                // Live: no other action on this clip until it is launched again
                return;
            }
            if (action == FollowAction.Stop)
            {
                st.Next = Pending.StopAt(c);
                return;
            }

            var column = ColumnOf?.Invoke(st.Track);
            int current = CurrentSlot(column, st.Slot, playing);
            int target = -1;
            if (action == FollowAction.Jump)
            {
                target = jump;
            }
            else if (action == FollowAction.PlayAgain)
            {
                if (current < 0)
                {
                    // Not in the layout (any more): relaunch it as it was.
                    LaunchAt(st.Track, playing, c, long.MinValue, new LaunchSpec(st.Shape, false, 0, st.Gain, f, st.Slot));
                    return;
                }
                target = current;
            }
            else if (current >= 0)
            {
                // The group: the run of filled slots around the current one.
                int lo = current, hi = current;
                while (lo > 0 && column[lo - 1] != null) lo--;
                while (hi < column.Length - 1 && column[hi + 1] != null) hi++;
                int count = hi - lo + 1;
                switch (action)
                {
                    case FollowAction.Previous: target = current > lo ? current - 1 : hi; break;
                    case FollowAction.Next: target = current < hi ? current + 1 : lo; break;
                    case FollowAction.First: target = lo; break;
                    case FollowAction.Last: target = hi; break;
                    case FollowAction.Any: target = lo + random.Next(count); break;
                    case FollowAction.Other:
                        if (count == 1) { target = current; break; }
                        target = lo + random.Next(count - 1);
                        if (target >= current) target++;
                        break;
                }
            }

            if (column == null || target < 0 || target >= column.Length || column[target] == null)
            {
                if (action == FollowAction.Jump) st.Next = Pending.StopAt(c);   // jumping to an empty slot stops, as its stop button would
                return;
            }
            var slot = column[target];
            int q = slot.Quantization < 0 ? 0 : QuantizeTicksOf?.Invoke(slot.Quantization) ?? 0;
            LaunchAt(st.Track, slot.Clip, OnGridFrom(c, q), long.MinValue, slot.Spec);
        }

        /// <summary><paramref name="c"/> if it is on the grid of <paramref name="q"/> ticks (or q is 0), else the next grid point.</summary>
        long OnGridFrom(long c, int q)
        {
            if (q <= 0) return c;
            long rel = c - GridOrigin;
            long r = ((rel % q) + q) % q;
            return r == 0 ? c : c + (q - r);
        }

        static int CurrentSlot(Slot[] column, int slot, TClip playing)
        {
            if (column == null) return -1;
            if (slot >= 0 && slot < column.Length && column[slot] != null && ReferenceEquals(column[slot].Clip, playing))
                return slot;
            for (int i = 0; i < column.Length; i++)
                if (column[i] != null && ReferenceEquals(column[i].Clip, playing))
                    return i;
            return -1;
        }

        /// <summary>When the track's follow action is due (session clock), or -1.</summary>
        public long GetFollowAt(TTrack track)
        {
            var st = Find(track);
            return st == null || st.Playing == null || st.FollowAt == NotStarted ? -1 : st.FollowAt;
        }

        // ------------------------------------------------------------------ launch undo (0.6)

        /// <summary>What the track plays and has queued now, to undo a launch made after (call under the lock).</summary>
        public object Capture(TTrack track)
        {
            var st = Find(track);
            if (st == null) return new Memento();
            return new Memento
            {
                Playing = IsRunning ? st.Playing : null,
                StartClock = st.StartClock,
                Spec = new LaunchSpec(st.Shape, false, 0, st.Gain, st.Follow, st.Slot),
                Next = st.Next
            };
        }

        /// <summary>
        /// Undoes the launch of <paramref name="launched"/> on the track, back to what
        /// <see cref="Capture"/> saw. Still waiting: cancelled (what was queued before comes back).
        /// Started: on the grid, the clip that played before comes back in step, as if it had never
        /// stopped (or the track stops, if nothing played). If the track has moved on to another
        /// clip since, nothing changes.
        /// </summary>
        public void Revert(TTrack track, object memento, TClip launched, int quantizeTicks, long heardClock)
        {
            var st = Find(track);
            if (st == null) return;
            var m = memento as Memento ?? new Memento();
            var next = st.Next;
            if (launched == null)
            {
                // Undoing a stop (a scene that stops other tracks): cancelled, or the clip comes back in step.
                if (next != null && next.IsStop) { RestoreRequest(st, m.Next, quantizeTicks, heardClock); return; }
                if (st.Playing != null || m.Playing == null || !IsRunning) return;
                Resume(st, m, quantizeTicks, heardClock);
                return;
            }
            if (next != null && !next.IsStop && ReferenceEquals(next.Clip, launched))
            {
                RestoreRequest(st, m.Next, quantizeTicks, heardClock);
                return;
            }
            if (!ReferenceEquals(st.Playing, launched) || !IsRunning) return;
            if (m.Playing == null)
            {
                Stop(track, quantizeTicks, false, heardClock);
                return;
            }
            Resume(st, m, quantizeTicks, heardClock);
        }

        /// <summary>
        /// The clip a memento saw playing comes back on the next grid point, in step as if it had
        /// never stopped; it is heard to start on that grid point (0.6.1), so its follow action,
        /// repeats and the recording count from there.
        /// </summary>
        void Resume(TrackState st, Memento m, int quantizeTicks, long heardClock)
        {
            var (at, boundary) = Plan(quantizeTicks, heardClock);
            long heard = boundary == long.MinValue ? at : boundary;
            long phase = m.StartClock == NotStarted ? long.MinValue : m.StartClock;
            LaunchAt(st.Track, m.Playing, at, phase, m.Spec, phase == long.MinValue ? long.MinValue : heard);
        }

        /// <summary>
        /// Puts back a request a memento saw queued. One whose time has passed is planned again
        /// on the grid (0.6.1): a launch in step with when it was due, a stop on the next grid
        /// point; so nothing starts off the grid or from its top by surprise.
        /// </summary>
        void RestoreRequest(TrackState st, Pending p, int quantizeTicks, long heardClock)
        {
            if (p == null || !IsRunning || p.At > Clock)
            {
                if (p != null && !p.IsStop && !IsLive(st.Track, p.Clip)) p = null;
                st.Next = p;
                return;
            }
            var (at, boundary) = Plan(quantizeTicks, heardClock);
            long heard = boundary == long.MinValue ? at : boundary;
            if (p.IsStop)
            {
                st.Next = st.Playing != null ? Pending.StopAt(at, p.ReleaseOverride) : null;
                return;
            }
            long phase = p.Origin == long.MinValue ? p.At : p.Origin;
            LaunchAt(st.Track, p.Clip, at, phase, p.Spec, heard);
            var queued = st.Next;
            if (p.StopAfter > 0 && queued != null && !queued.IsStop && ReferenceEquals(queued.Clip, p.Clip))
                st.Next = queued.With(queued.Spec, p.StopAfter);     // a Gate released before it started still stops after a step
        }

        /// <summary>Whether a memento refers to the clip (0.6.1: a deleted pattern leaves the undo history).</summary>
        public bool Refers(object memento, TClip clip) =>
            memento is Memento m && clip != null &&
            (ReferenceEquals(m.Playing, clip) || (m.Next != null && !m.Next.IsStop && ReferenceEquals(m.Next.Clip, clip)));

        // ------------------------------------------------------------------ reads (lock-free)

        public TClip GetPlaying(TTrack track)
        {
            if (!IsRunning) return null;
            return Find(track)?.Playing;
        }

        public TClip GetQueued(TTrack track)
        {
            var next = Find(track)?.Next;
            return next != null && !next.IsStop ? next.Clip : null;
        }

        public bool IsStopQueued(TTrack track)
        {
            var next = Find(track)?.Next;
            return next != null && next.IsStop;
        }

        /// <summary>The slot (scene row) the playing clip was launched from, or -1 (none, or not known).</summary>
        public int GetPlayingSlot(TTrack track)
        {
            var st = Find(track);
            return st != null && IsRunning && st.Playing != null ? st.Slot : -1;
        }

        /// <summary>The slot (scene row) the waiting launch comes from, or -1 (none, a stop, or not known).</summary>
        public int GetQueuedSlot(TTrack track)
        {
            var next = Find(track)?.Next;
            return next != null && !next.IsStop ? next.Spec.Slot : -1;
        }

        public long GetStartClock(TTrack track)
        {
            var st = Find(track);
            if (st == null || st.Playing == null || !IsRunning) return -1;
            long start = st.StartClock;
            return start == NotStarted ? -1 : start;
        }

        /// <summary>Position of the playing clip in ticks, or -1.</summary>
        public int GetPosition(TTrack track)
        {
            var st = Find(track);
            if (st == null) return -1;
            var playing = st.Playing;
            long c = Clock;
            long start = st.StartClock;
            if (playing == null || c < 0 || start == NotStarted) return -1;
            return st.Shape.PositionAt(c - start, Length(playing));
        }

        /// <summary>
        /// Ticks since the playing clip last entered (launch, restart or loop wrap), or -1. 0
        /// means it (re)started on this tick: the arrangement recorder writes it there.
        /// </summary>
        public long GetCycleElapsed(TTrack track)
        {
            var st = Find(track);
            if (st == null) return -1;
            var playing = st.Playing;
            long c = Clock;
            long start = st.StartClock;
            if (playing == null || c < 0 || start == NotStarted || c < start) return -1;
            return st.Shape.CycleElapsed(c - start, Length(playing));
        }

        /// <summary>
        /// Clock of the tick the track's clip last launched or restarted on (Repeat), and how many
        /// ticks after its grid point that was (a late, catch-up launch); (-1, 0) when none plays.
        /// </summary>
        public (long At, long Late) GetLaunch(TTrack track)
        {
            var st = Find(track);
            if (st == null || st.Playing == null || st.LaunchedAt == NotStarted) return (-1, 0);
            return (st.LaunchedAt, st.LaunchLate);
        }

        /// <summary>
        /// For recording (0.5.1): where the track's clip is in its pattern at
        /// <paramref name="clock"/> (its start marker if that is before it started), and the end
        /// of its region (exclusive). (0, 0) when none plays.
        /// </summary>
        public (int Offset, int End) GetRegionAt(TTrack track, long clock)
        {
            var st = Find(track);
            var playing = st?.Playing;
            if (playing == null) return (0, 0);
            int length = Length(playing);
            var shape = st.Shape;
            var (start, _, end) = shape.Resolve(length);
            long elapsed = clock - st.StartClock;
            int position = elapsed >= 0 ? shape.PositionAt(elapsed, length) : start;
            return (position < 0 ? start : position, end);
        }

        /// <summary>
        /// For recording (0.7): the clip the track plays at <paramref name="atClock"/> (now, or a
        /// little earlier as heard), where it is in its pattern then (ticks), the end of its region
        /// (exclusive) and the clock it counts from. False when none plays, or the clip (re)started
        /// after that moment. Call under the gate.
        /// </summary>
        public bool TryGetPlayingAt(TTrack track, long atClock, out TClip clip, out int position, out int end, out long startClock)
        {
            clip = null;
            position = -1;
            end = 0;
            startClock = -1;
            var st = Find(track);
            var playing = st?.Playing;
            if (playing == null || Clock < 0) return false;
            long start = st.StartClock;
            if (start == NotStarted || atClock < start) return false;
            int length = Length(playing);
            var shape = st.Shape;
            int p = shape.PositionAt(atClock - start, length);
            if (p < 0) return false;
            clip = playing;
            position = p;
            end = shape.Resolve(length).End;
            startClock = start;
            return true;
        }

        /// <summary>Gain of the clip playing on the track (1 when none).</summary>
        public double GetGain(TTrack track)
        {
            var st = Find(track);
            return st != null && st.Playing != null ? st.Gain : 1.0;
        }

        /// <summary>Shape of the clip playing on the track, or null.</summary>
        public ClipShape GetShape(TTrack track)
        {
            var st = Find(track);
            return st != null && st.Playing != null ? st.Shape : null;
        }

        /// <summary>Whether the track's clip is repeating (Repeat mode, held).</summary>
        public bool IsRepeating(TTrack track)
        {
            var st = Find(track);
            return st != null && st.Playing != null && st.RepeatTicks > 0;
        }

        public bool IsOverridden(TTrack track)
        {
            var st = Find(track);
            return st != null && st.Overridden;
        }

        public bool IsAnyOverridden
        {
            get
            {
                for (int i = 0; i < tracks.Count; i++)
                    if (tracks[i].Overridden) return true;
                return false;
            }
        }

        /// <summary>
        /// Playing, Queued (launch or relaunch pending), Stopping (stop pending) or Stopped.
        /// Returns the ISession.ClipState values as ints so this file stays free of ReBuzz types:
        /// 0 Stopped, 1 Queued, 2 Playing, 3 Stopping.
        /// </summary>
        public int GetState(TTrack track, TClip clip)
        {
            var st = Find(track);
            if (st == null || clip == null) return 0;
            var next = st.Next;
            var playing = IsRunning ? st.Playing : null;
            if (ReferenceEquals(playing, clip))
            {
                if (next == null) return 2;
                if (next.IsStop) return 3;
                return ReferenceEquals(next.Clip, clip) ? 1 : 2;
            }
            if (next != null && !next.IsStop && ReferenceEquals(next.Clip, clip)) return 1;
            return 0;
        }

        // ------------------------------------------------------------------ helpers

        int Length(TClip clip) => Math.Max(1, lengthOf(clip));

        TrackState Find(TTrack track)
        {
            if (track == null) return null;
            var st = track.SessionState as TrackState;
            return st != null && ReferenceEquals(st.Owner, this) ? st : null;
        }

        TrackState GetOrCreate(TTrack track)
        {
            var st = Find(track);
            if (st != null) return st;
            st = new TrackState(this, track);
            tracks.Add(st);
            track.SessionState = st;
            return st;
        }
    }
}
