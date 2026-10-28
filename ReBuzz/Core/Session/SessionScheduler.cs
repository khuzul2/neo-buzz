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

    /// <summary>How a clip is launched (0.5): its shape, legato, repeat rate, gain. Immutable.</summary>
    internal sealed class LaunchSpec
    {
        public static readonly LaunchSpec Looped = new LaunchSpec(ClipShape.Looped);
        public static readonly LaunchSpec OneShot = new LaunchSpec(ClipShape.OneShot);

        public LaunchSpec(ClipShape shape, bool legato = false, int repeatTicks = 0, double gain = 1.0)
        {
            Shape = shape ?? ClipShape.Looped;
            Legato = legato;
            RepeatTicks = Math.Max(0, repeatTicks);
            Gain = Math.Clamp(gain, 0, 1);
        }

        public static LaunchSpec For(bool loop) => loop ? Looped : OneShot;

        public ClipShape Shape { get; }
        /// <summary>Take over the play position of the clip playing on the track.</summary>
        public bool Legato { get; }
        /// <summary>Repeat mode: restart every this many ticks while held; 0 when not repeating.</summary>
        public int RepeatTicks { get; }
        /// <summary>Volume of the clip's machine (velocity), 0..1.</summary>
        public double Gain { get; }

        public LaunchSpec WithoutRepeat() => RepeatTicks == 0 ? this : new LaunchSpec(Shape, Legato, 0, Gain);
        public LaunchSpec WithShape(ClipShape shape) => new LaunchSpec(shape, Legato, RepeatTicks, Gain);
    }

    internal sealed class SessionScheduler<TTrack, TClip>
        where TTrack : class, ISessionTrack
        where TClip : class
    {
        sealed class Pending
        {
            public Pending(bool isStop, TClip clip, long at, LaunchSpec spec, bool releaseOverride, long origin = long.MinValue, int stopAfter = 0)
            {
                IsStop = isStop; Clip = clip; At = at; Spec = spec ?? LaunchSpec.Looped; ReleaseOverride = releaseOverride; Origin = origin; StopAfter = stopAfter;
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

            public Pending Shifted(long delta) =>
                new Pending(IsStop, Clip, At + delta, Spec, ReleaseOverride, Origin == long.MinValue ? long.MinValue : Origin + delta, StopAfter);

            public Pending With(LaunchSpec spec, int stopAfter) =>
                new Pending(IsStop, Clip, At, spec, ReleaseOverride, Origin, stopAfter);
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
            public double Gain = 1.0;
            public volatile Pending Next;
            public volatile bool Overridden;
        }

        const long NotStarted = long.MinValue;

        readonly Func<TClip, int> lengthOf;
        readonly List<TrackState> tracks = new List<TrackState>();
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
            LaunchAt(track, clip, at, OriginWithOffset(at, origin, startOffset), spec);
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

        public void LaunchAt(TTrack track, TClip clip, long at, long origin, LaunchSpec spec)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            var target = GetOrCreate(track);
            target.Next = new Pending(false, clip, at, spec, false, origin);
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
        /// tick (scene settings such as tempo take effect on it).
        /// </summary>
        public long LaunchScene(IEnumerable<(TTrack Track, TClip Clip, LaunchSpec Spec)> slots, int quantizeTicks, long heardClock)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            var (at, origin) = Plan(quantizeTicks, heardClock);
            foreach (var (track, clip, spec) in slots)
            {
                if (track != null && clip != null)
                    LaunchAt(track, clip, at, origin, spec);
            }
            return at;
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
            if (ReferenceEquals(st.Playing, clip)) st.Shape = shape;
            var next = st.Next;
            if (next != null && !next.IsStop && ReferenceEquals(next.Clip, clip))
                st.Next = next.With(next.Spec.WithShape(shape), next.StopAfter);
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
                    st.Playing = null;
                    st.StartClock = NotStarted;
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
                    st.RepeatFrom += delta;
                    var n = st.Next;
                    if (n != null)
                        st.Next = n.Shifted(delta);
                }
            }
            else
            {
                // Launches queued while stopped were stamped "0" (= at start).
                for (int i = 0; i < tracks.Count; i++)
                {
                    var st = tracks[i];
                    var n = st.Next;
                    if (n != null && n.At <= 0)
                        st.Next = n.Shifted(initialClock - n.At);
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
                st.Playing = null;
                st.StartClock = NotStarted;
                st.RepeatTicks = 0;
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
                if (playing != null && st.Shape.PositionAt(c - st.StartClock, Length(playing)) < 0)
                {
                    st.Playing = null;
                    st.StartClock = NotStarted;
                    st.RepeatTicks = 0;
                    changes.Add(new SessionChange<TTrack, TClip>(st.Track, playing, null, SessionChangeKind.Ended));
                    playing = null;
                }

                bool launchedNow = false;
                var next = st.Next;
                if (next != null && next.At <= c)
                {
                    st.Next = null;
                    if (next.IsStop)
                    {
                        if (playing != null)
                        {
                            st.Playing = null;
                            st.StartClock = NotStarted;
                            st.RepeatTicks = 0;
                            changes.Add(new SessionChange<TTrack, TClip>(st.Track, playing, null, SessionChangeKind.Stopped));
                        }
                        if (next.ReleaseOverride) st.Overridden = false;
                    }
                    else
                    {
                        var spec = next.Spec;
                        long launchOrigin = next.Origin == long.MinValue ? c : Math.Min(next.Origin, c);
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
                        st.RepeatFrom = launchOrigin;          // repeats stay on the grid after a late (catch-up) start
                        st.Gain = spec.Gain;
                        st.Overridden = true;
                        st.Playing = next.Clip;
                        launchedNow = true;
                        changes.Add(new SessionChange<TTrack, TClip>(st.Track, playing, next.Clip, SessionChangeKind.Launched));
                        if (next.StopAfter > 0)
                            st.Next = Pending.StopAt(c + next.StopAfter);     // gate released before the start
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
                for (int i = 0; i < tracks.Count; i++)
                {
                    var st = tracks[i];
                    if (ReferenceEquals(st.Track, launched.Track) || !ReferenceEquals(st.Playing, launched.Current)) continue;
                    st.Playing = null;
                    st.StartClock = NotStarted;
                    var next = st.Next;
                    if (next != null && next.IsStop) st.Next = null;
                    changes.Add(new SessionChange<TTrack, TClip>(st.Track, launched.Current, null, SessionChangeKind.Moved));
                }
            }
        }

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
