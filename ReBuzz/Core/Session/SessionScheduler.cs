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
        Halted
    }

    internal readonly record struct SessionChange<TTrack, TClip>(
        TTrack Track, TClip Previous, TClip Current, SessionChangeKind Kind);

    internal sealed class SessionScheduler<TTrack, TClip>
        where TTrack : class, ISessionTrack
        where TClip : class
    {
        sealed class Pending
        {
            public Pending(bool isStop, TClip clip, long at, bool loop, bool releaseOverride)
            {
                IsStop = isStop; Clip = clip; At = at; Loop = loop; ReleaseOverride = releaseOverride;
            }
            public readonly bool IsStop;
            public readonly TClip Clip;
            public readonly long At;
            public readonly bool Loop;
            public readonly bool ReleaseOverride;
        }

        sealed class TrackState
        {
            public TrackState(SessionScheduler<TTrack, TClip> owner, TTrack track) { Owner = owner; Track = track; }
            public readonly SessionScheduler<TTrack, TClip> Owner;
            public readonly TTrack Track;
            public volatile TClip Playing;
            public long StartClock = -1;
            public volatile bool Loop;
            public volatile Pending Next;
            public volatile bool Overridden;
        }

        readonly Func<TClip, int> lengthOf;
        readonly List<TrackState> tracks = new List<TrackState>();
        readonly List<SessionChange<TTrack, TClip>> changes = new List<SessionChange<TTrack, TClip>>();
        long clock = -1;

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
        public long NextQuantizedClock(int quantizeTicks)
        {
            long c = Clock;
            if (c < 0) return 0;
            if (quantizeTicks <= 0) return c + 1;
            return (c / quantizeTicks + 1) * quantizeTicks;
        }

        long ImmediateClock()
        {
            long c = Clock;
            return c < 0 ? 0 : c + 1;
        }

        public void Launch(TTrack track, TClip clip, int quantizeTicks, bool loop)
        {
            LaunchAt(track, clip, NextQuantizedClock(quantizeTicks), loop);
        }

        /// <summary>Queues a launch at an absolute clock value (replacing any queued action).</summary>
        public void LaunchAt(TTrack track, TClip clip, long at, bool loop)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            GetOrCreate(track).Next = new Pending(false, clip, at, loop, false);
        }

        /// <summary>Queues launches that all start on the same tick.</summary>
        public void LaunchScene(IEnumerable<(TTrack Track, TClip Clip)> slots, int quantizeTicks, bool loop)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            long at = NextQuantizedClock(quantizeTicks);
            foreach (var (track, clip) in slots)
            {
                if (track != null && clip != null)
                    LaunchAt(track, clip, at, loop);
            }
        }

        /// <summary>Queues a stop. With nothing playing, cancels a queued launch instead.</summary>
        public void Stop(TTrack track, int quantizeTicks, bool immediate)
        {
            var st = Find(track);
            if (st == null) return;
            if (st.Playing == null || !IsRunning)
            {
                st.Next = null;
                return;
            }
            long at = immediate ? ImmediateClock() : NextQuantizedClock(quantizeTicks);
            st.Next = new Pending(true, null, at, false, false);
        }

        public void StopAll(int quantizeTicks, bool immediate)
        {
            for (int i = 0; i < tracks.Count; i++)
                Stop(tracks[i].Track, quantizeTicks, immediate);
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
                    st.Next = new Pending(true, null, at, false, true);
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
                    st.StartClock = -1;
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
                // Restarted while running: shift everything so clips keep their phase.
                long delta = initialClock - c;
                for (int i = 0; i < tracks.Count; i++)
                {
                    var st = tracks[i];
                    if (st.StartClock >= 0) st.StartClock += delta;
                    var n = st.Next;
                    if (n != null)
                        st.Next = new Pending(n.IsStop, n.Clip, n.At + delta, n.Loop, n.ReleaseOverride);
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
                        st.Next = new Pending(n.IsStop, n.Clip, initialClock, n.Loop, n.ReleaseOverride);
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
            for (int i = 0; i < tracks.Count; i++)
            {
                var st = tracks[i];
                var playing = st.Playing;
                st.Next = null;
                st.Playing = null;
                st.StartClock = -1;
                st.Overridden = false;
                if (playing != null)
                    changes.Add(new SessionChange<TTrack, TClip>(st.Track, playing, null, SessionChangeKind.Halted));
            }
        }

        void ProcessDue()
        {
            long c = Clock;
            for (int i = 0; i < tracks.Count; i++)
            {
                var st = tracks[i];

                var playing = st.Playing;
                if (playing != null && !st.Loop && c - st.StartClock >= Length(playing))
                {
                    st.Playing = null;
                    st.StartClock = -1;
                    changes.Add(new SessionChange<TTrack, TClip>(st.Track, playing, null, SessionChangeKind.Ended));
                    playing = null;
                }

                var next = st.Next;
                if (next == null || next.At > c) continue;
                st.Next = null;

                if (next.IsStop)
                {
                    if (playing != null)
                    {
                        st.Playing = null;
                        st.StartClock = -1;
                        changes.Add(new SessionChange<TTrack, TClip>(st.Track, playing, null, SessionChangeKind.Stopped));
                    }
                    if (next.ReleaseOverride) st.Overridden = false;
                }
                else
                {
                    st.StartClock = c;
                    st.Loop = next.Loop;
                    st.Overridden = true;
                    st.Playing = next.Clip;
                    changes.Add(new SessionChange<TTrack, TClip>(st.Track, playing, next.Clip, SessionChangeKind.Launched));
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
            return st != null && st.Playing != null && IsRunning ? st.StartClock : -1;
        }

        /// <summary>Position of the playing clip in ticks, or -1.</summary>
        public int GetPosition(TTrack track)
        {
            var st = Find(track);
            if (st == null) return -1;
            var playing = st.Playing;
            long c = Clock;
            long start = st.StartClock;
            if (playing == null || c < 0 || start < 0) return -1;
            long elapsed = c - start;
            if (elapsed < 0) return -1;
            int len = Length(playing);
            if (st.Loop) return (int)(elapsed % len);
            return elapsed < len ? (int)elapsed : -1;
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
