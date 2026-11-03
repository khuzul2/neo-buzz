using BuzzGUI.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Arrangement record (fork addition, docs/specs/session-view.md 0.3): while ReBuzz records,
    /// session clip starts (launches and loop restarts) and stops are written into the
    /// sequences at the song position where they happened, replacing what the arrangement had
    /// on those tracks in between. One recording pass is one undo step in the Session View.
    /// Since 0.5.1 a start carries the part of the pattern played (from a start or loop marker,
    /// or where legato picked it up, to the clip's end marker) and its launch velocity, so the
    /// arrangement plays back what was heard (<see cref="SequenceEvent.Offset"/>, End, Gain).
    /// Since daw-modes.md S2 a launch and the loop wraps after it are one arrangement clip
    /// (<see cref="SequenceEvent.Clip"/>), with the session clip's loop as its loop.
    ///
    /// SessionCore reports from the audio thread (tick time) into a queue; the writes happen
    /// on the UI thread, since ISequence.SetEvent notifies the sequence editors.
    /// </summary>
    internal sealed class SessionRecorder
    {
        enum Kind { Start, Stop, Cut, Release, End }

        readonly record struct Entry(Kind Kind, SequenceCore Sequence, PatternCore Pattern, int Position, int Offset = 0, int End = 0, float Gain = 1f, bool Wrap = false);

        sealed class TrackPass
        {
            public int LastWrite;
            public bool Open = true;
            /// <summary>daw-modes.md S2: the clip the track's last start began, which its loop wraps continue (0: none).</summary>
            public int Clip;
            public PatternCore ClipPattern;
            /// <summary>
            /// 0.6.2: every time the pass wrote to, with what was there before its first write and
            /// what it wrote last. Undo takes back exactly these, so edits made in the Sequence
            /// View during the pass (on these tracks too) stay.
            /// </summary>
            public readonly Dictionary<int, (SequenceEvent Before, SequenceEvent After)> Touched = new Dictionary<int, (SequenceEvent, SequenceEvent)>();
        }

        readonly ReBuzzCore buzz;
        readonly SessionLayout layout;
        readonly ConcurrentQueue<Entry> queue = new ConcurrentQueue<Entry>();
        int drainPosted;

        // UI thread: the pass being recorded.
        Dictionary<SequenceCore, TrackPass> pass;
        int passLoopEnd = -1;
        int passSongEnd = -1;

        /// <summary>A pass finished and was added to the undo history.</summary>
        public event Action PassRecorded;

        internal SessionRecorder(ReBuzzCore buzz, SessionLayout layout)
        {
            this.buzz = buzz;
            this.layout = layout;
        }

        /// <summary>Whether a pass is open (something was written since recording started).</summary>
        public bool IsRecordingPass => pass != null;

        // ------------------------------------------------------------------ any thread

        /// <summary>
        /// A clip started (launch, loop restart, Repeat restart) at a song position, playing its
        /// pattern from <paramref name="offset"/> to <paramref name="end"/> (0: the pattern's end)
        /// at <paramref name="gain"/>. <paramref name="wrap"/>: a loop restart of the clip that
        /// started last on the track, written as the same arrangement clip (daw-modes.md S2).
        /// </summary>
        internal void ClipStarted(SequenceCore seq, PatternCore pattern, int position, int offset = 0, int end = 0, float gain = 1f, bool wrap = false) =>
            Post(new Entry(Kind.Start, seq, pattern, position, offset, end, gain, wrap));

        /// <summary>A clip was stopped (the track stays with the session).</summary>
        internal void ClipStopped(SequenceCore seq, int position) => Post(new Entry(Kind.Stop, seq, null, position));

        /// <summary>
        /// A clip's pattern moved to another track of the same machine (0.4.1): the pattern ends
        /// here with a Break, not an Off, which would stop the machine that now plays it there.
        /// </summary>
        internal void ClipCut(SequenceCore seq, int position) => Post(new Entry(Kind.Cut, seq, null, position));

        /// <summary>A track went back to the arrangement.</summary>
        internal void TrackReleased(SequenceCore seq, int position) => Post(new Entry(Kind.Release, seq, null, position));

        /// <summary>Recording or the transport stopped.</summary>
        internal void EndPass(int position) => Post(new Entry(Kind.End, null, null, position));

        void Post(Entry e)
        {
            queue.Enqueue(e);
            if (Interlocked.Exchange(ref drainPosted, 1) == 0)
                buzz.dispatcher.BeginInvoke(Drain);
        }

        // ------------------------------------------------------------------ UI thread

        /// <summary>New song: drop whatever was pending.</summary>
        internal void Reset()
        {
            while (queue.TryDequeue(out _)) { }
            pass = null;
        }

        void Drain()
        {
            Volatile.Write(ref drainPosted, 0);
            while (queue.TryDequeue(out var e))
            {
                try
                {
                    Apply(e);
                }
                catch (Exception ex)
                {
                    buzz.DCWriteLine("Session arrangement record: " + ex.Message, DCLogLevel.Warning);
                }
            }
        }

        void Apply(Entry e)
        {
            if (e.Kind == Kind.End)
            {
                FinishPass(e.Position);
                return;
            }

            var seq = e.Sequence;
            if (seq == null || !buzz.SongCore.SequencesList.Contains(seq)) return;

            if (pass == null)
            {
                pass = new Dictionary<SequenceCore, TrackPass>();
                passLoopEnd = buzz.SongCore.LoopEnd;
                passSongEnd = buzz.SongCore.SongEnd;
            }
            if (!pass.TryGetValue(seq, out var track))
            {
                track = new TrackPass { LastWrite = e.Position };
                pass[seq] = track;
            }
            else if (!track.Open)
            {
                track.Open = true;
                track.LastWrite = e.Position;
            }

            if (e.Kind == Kind.Release)
            {
                ClearBetween(seq, track, track.LastWrite, e.Position);
                track.Open = false;
                track.Clip = 0;
                return;
            }

            ClearBetween(seq, track, track.LastWrite, e.Position);
            // 0.6.1: never a deleted pattern (posted just before the delete, or from stale state):
            // the song would save it as an invalid pattern index.
            if (e.Kind == Kind.Start && e.Pattern != null && e.Pattern.Machine == seq.Machine && !e.Pattern.IsDeleted)
                Write(seq, track, e.Position, new SequenceEvent(SequenceEventType.PlayPattern, e.Pattern)
                {
                    Offset = Math.Max(0, e.Offset),
                    End = e.End > 0 && e.End < e.Pattern.Length ? e.End : 0,
                    Gain = Math.Clamp(e.Gain, 0f, 1f),
                    Clip = ClipFor(track, e)
                });
            else if (e.Kind == Kind.Stop)
                Write(seq, track, e.Position, new SequenceEvent(SequenceEventType.Off));
            else if (e.Kind == Kind.Cut)
                Write(seq, track, e.Position, new SequenceEvent(SequenceEventType.Break));
            if (e.Kind != Kind.Start) track.Clip = 0;
            track.LastWrite = e.Position;
        }

        /// <summary>
        /// daw-modes.md S2: the arrangement clip a start belongs to. A loop wrap continues the
        /// track's clip; a launch begins a new one, with the session clip's loop as its loop.
        /// </summary>
        int ClipFor(TrackPass track, Entry e)
        {
            if (e.Wrap && track.Clip != 0 && ReferenceEquals(track.ClipPattern, e.Pattern))
                return track.Clip;
            var clips = buzz.Clips;
            int id = clips.NewId();
            clips.SetLoop(id, SceneToArrangement.LoopOf(layout, e.Pattern));
            track.Clip = id;
            track.ClipPattern = e.Pattern;
            return id;
        }

        /// <summary>Writes (or with null removes) an event, remembering it for undo (0.6.2).</summary>
        static void Write(SequenceCore seq, TrackPass track, int time, SequenceEvent e)
        {
            var before = seq.Events.TryGetValue(time, out var was) ? new SequenceEvent(was) : null;
            track.Touched[time] = track.Touched.TryGetValue(time, out var t)
                ? (t.Before, e == null ? null : new SequenceEvent(e))
                : (before, e == null ? null : new SequenceEvent(e));
            if (e == null)
                seq.Clear(time, 1);
            else
                seq.SetEvent(time, e);
        }

        /// <summary>Removes events strictly between two positions (nothing if they wrapped).</summary>
        static void ClearBetween(SequenceCore seq, TrackPass track, int after, int before)
        {
            if (before - after <= 1) return;
            foreach (int t in seq.Events.Keys.Where(t => t > after && t < before).ToList())
                Write(seq, track, t, null);
        }

        void FinishPass(int position)
        {
            if (pass == null) return;
            var written = pass;
            pass = null;

            var changes = new List<ArrangementSnapshotAction.EventChange>();
            foreach (var kv in written)
            {
                var seq = kv.Key;
                if (!buzz.SongCore.SequencesList.Contains(seq)) continue;
                if (kv.Value.Open)
                    ClearBetween(seq, kv.Value, kv.Value.LastWrite, position);
                foreach (var t in kv.Value.Touched)
                    if (!ArrangementSnapshotAction.Same(t.Value.Before, t.Value.After))
                        changes.Add(new ArrangementSnapshotAction.EventChange(seq, t.Key, t.Value.Before, t.Value.After));
            }
            if (changes.Count == 0) return;

            var song = buzz.SongCore;
            layout.UndoStack.Do(new ArrangementSnapshotAction(song, changes,
                (passLoopEnd, passSongEnd), (song.LoopEnd, song.SongEnd)));
            buzz.SetModifiedFlag();
            PassRecorded?.Invoke();
        }
    }

    /// <summary>
    /// Undo step for an arrangement edit made from the session (a recording pass, a scene
    /// inserted into the arrangement): the events it changed, one by one (0.6.1; 0.6.2: a
    /// recording pass lists exactly the events it wrote). Undo puts back what was there where
    /// the edit's event is still as it left it, so edits made since, in the Sequence View or
    /// anywhere, stay; redo likewise. The song's loop and song end go back only if nothing
    /// changed them since. The edit is already made when the action is pushed, so the first Do
    /// does nothing.
    /// </summary>
    internal sealed class ArrangementSnapshotAction : IAction
    {
        internal readonly record struct SequenceChange(ISequence Sequence, KeyValuePair<int, SequenceEvent>[] Before, KeyValuePair<int, SequenceEvent>[] After);

        /// <summary>One event the edit changed: what was there (null: nothing) and what it put there (null: removed).</summary>
        internal readonly record struct EventChange(ISequence Sequence, int Time, SequenceEvent Before, SequenceEvent After);

        readonly SongCore song;
        readonly List<EventChange> changes;
        readonly (int LoopEnd, int SongEnd) endsBefore;
        readonly (int LoopEnd, int SongEnd) endsAfter;
        bool skipDo = true;

        /// <summary>From the events the edit changed, one by one (a recording pass, 0.6.2).</summary>
        public ArrangementSnapshotAction(SongCore song, List<EventChange> changes, (int, int) endsBefore, (int, int) endsAfter)
        {
            this.song = song;
            this.changes = changes;
            this.endsBefore = endsBefore;
            this.endsAfter = endsAfter;
        }

        /// <summary>From tracks before and after the edit (a scene inserted): the differences.</summary>
        public ArrangementSnapshotAction(SongCore song, List<SequenceChange> tracks, (int, int) endsBefore, (int, int) endsAfter)
            : this(song, Differences(tracks), endsBefore, endsAfter)
        {
        }

        static List<EventChange> Differences(List<SequenceChange> tracks)
        {
            var list = new List<EventChange>();
            foreach (var c in tracks)
            {
                var before = c.Before.ToDictionary(e => e.Key, e => e.Value);
                var after = c.After.ToDictionary(e => e.Key, e => e.Value);
                foreach (int time in before.Keys.Union(after.Keys))
                {
                    before.TryGetValue(time, out var b);
                    after.TryGetValue(time, out var a);
                    if (!Same(b, a))
                        list.Add(new EventChange(c.Sequence, time, b == null ? null : new SequenceEvent(b), a == null ? null : new SequenceEvent(a)));
                }
            }
            return list;
        }

        public void Do()
        {
            if (skipDo) { skipDo = false; return; }
            foreach (var c in changes) Put(c.Sequence, c.Time, expected: c.Before, value: c.After);
            SetEnds(endsBefore, endsAfter);
        }

        public void Undo()
        {
            for (int i = changes.Count - 1; i >= 0; i--)
                Put(changes[i].Sequence, changes[i].Time, expected: changes[i].After, value: changes[i].Before);
            SetEnds(endsAfter, endsBefore);
        }

        /// <summary>Two events (or nothing) are the same.</summary>
        internal static bool Same(SequenceEvent a, SequenceEvent b) =>
            a == null || b == null ? a == null && b == null :
            a.Type == b.Type && ReferenceEquals(a.Pattern, b.Pattern) && a.Offset == b.Offset && a.End == b.End && a.Gain == b.Gain && a.Clip == b.Clip;

        /// <summary>Sets an event (null: removes it) if what is there is still as expected: edits made since stay.</summary>
        static void Put(ISequence seq, int time, SequenceEvent expected, SequenceEvent value)
        {
            if (!seq.Machine?.Graph?.Buzz?.Song?.Sequences.Contains(seq) ?? true) return;
            seq.Events.TryGetValue(time, out var now);
            if (!Same(now, expected)) return;
            if (value == null)
            {
                if (now != null) seq.Clear(time, 1);
                return;
            }
            if (value.Pattern != null && !seq.Machine.Patterns.Contains(value.Pattern)) return;
            seq.SetEvent(time, new SequenceEvent(value));
        }

        /// <summary>Sets the loop and song end, unless something else changed them since.</summary>
        void SetEnds((int LoopEnd, int SongEnd) expected, (int LoopEnd, int SongEnd) ends)
        {
            if (ends.LoopEnd <= 0) return;
            if (song.LoopEnd != expected.LoopEnd || song.SongEnd != expected.SongEnd) return;
            if (ends.SongEnd >= song.SongEnd)
            {
                song.SongEnd = ends.SongEnd;
                song.LoopEnd = ends.LoopEnd;
            }
            else
            {
                song.LoopEnd = ends.LoopEnd;
                song.SongEnd = ends.SongEnd;
            }
        }
    }

    /// <summary>Scene to arrangement (0.3): a scene's clips written into the sequences.</summary>
    internal static class SceneToArrangement
    {
        /// <summary>
        /// daw-modes.md S2: the loop an arrangement clip of the pattern gets from its session
        /// clip settings (its loop region, whether it loops).
        /// </summary>
        internal static ClipLoop LoopOf(SessionLayout layout, IPattern pattern)
        {
            var c = layout.ClipOf(pattern);
            var (_, loopStart, regionEnd) = new ClipShape(c.Start, c.LoopStart, c.LoopEnd, c.Loop).Resolve(pattern.Length);
            return new ClipLoop(loopStart, regionEnd < pattern.Length ? regionEnd : 0, c.Loop);
        }

        /// <summary>
        /// Writes the scene's clips at <paramref name="position"/> (ticks) for
        /// <paramref name="length"/> ticks, as launching the scene would play them (0.5.1: from
        /// each clip's start marker, then its loop region over and over, or once for a clip
        /// without loop), cutting the last repeat at the end. Each is one arrangement clip
        /// (daw-modes.md S2), with the session clip's loop as its loop. With <paramref name="insert"/>,
        /// every sequence's later events move back by the length first; otherwise the scene's
        /// tracks are overwritten in that range. One undo step in the Session View. Returns the
        /// number of tracks written.
        /// </summary>
        public static int Insert(ReBuzzCore buzz, SessionLayout layout, int scene, int position, int length, bool insert)
        {
            var doc = layout.Document;
            if (doc == null || position < 0 || length <= 0) return 0;
            var song = buzz.SongCore;
            var clips = doc.SceneSlots(scene)
                .Select(s => (Sequence: layout.SequenceOf(s.Track), Pattern: layout.PatternAt(s.Track, scene)))
                .Where(c => c.Sequence != null && c.Pattern != null && c.Pattern.Length > 0)
                .ToList();
            if (clips.Count == 0 && !insert) return 0;

            var touched = insert ? song.Sequences.ToList() : clips.Select(c => c.Sequence).Distinct().ToList();
            var before = touched.ToDictionary(s => s, s => s.Events.ToArray());
            var endsBefore = (song.LoopEnd, song.SongEnd);
            int end = position + length;

            if (insert)
            {
                foreach (var seq in touched)
                    seq.Insert(position, length);
            }
            else
            {
                foreach (var seq in touched)
                    seq.Clear(position, length);
            }

            foreach (var (seq, pattern) in clips)
            {
                var c = layout.ClipOf(pattern);
                var (start, loopStart, regionEnd) = new ClipShape(c.Start, c.LoopStart, c.LoopEnd, c.Loop).Resolve(pattern.Length);
                int endMark = regionEnd < pattern.Length ? regionEnd : 0;
                int id = buzz.Clips.NewId();
                buzz.Clips.SetLoop(id, new ClipLoop(loopStart, endMark, c.Loop));
                int t = position, offset = start, lastEnd = position;
                while (t < end)
                {
                    seq.SetEvent(t, new SequenceEvent(SequenceEventType.PlayPattern, pattern) { Offset = offset, End = endMark, Clip = id });
                    lastEnd = t + (regionEnd - offset);
                    if (!c.Loop) break;
                    t = lastEnd;
                    offset = loopStart;
                }
                // Cut a last repeat that runs past the end, unless something already starts there.
                if (lastEnd > end && !seq.Events.ContainsKey(end))
                    seq.SetEvent(end, new SequenceEvent(SequenceEventType.Break));
            }

            // Keep the song (and a loop over the whole song) long enough.
            int songEnd = song.SongEnd;
            int loopEnd = song.LoopEnd;
            bool loopIsSong = loopEnd == songEnd;
            if (insert && position < songEnd) songEnd += length;
            else if (end > songEnd) songEnd = end;
            if (insert && position < loopEnd && !loopIsSong) loopEnd += length;
            if (loopIsSong) loopEnd = songEnd;
            song.SongEnd = songEnd;
            song.LoopEnd = loopEnd;

            var changes = touched.Select(s => new ArrangementSnapshotAction.SequenceChange(s, before[s], s.Events.ToArray())).ToList();
            layout.UndoStack.Do(new ArrangementSnapshotAction(song, changes, endsBefore, (song.LoopEnd, song.SongEnd)));
            buzz.SetModifiedFlag();
            return clips.Count;
        }

        /// <summary>
        /// Length of a scene in ticks: its longest clip (0.5.1: the part a launch plays first,
        /// from its start marker to its end marker), rounded up to whole bars.
        /// </summary>
        public static int SceneLength(SessionLayout layout, int scene, int ticksPerBar)
        {
            var doc = layout.Document;
            if (doc == null || ticksPerBar <= 0) return ticksPerBar;
            int longest = doc.SceneSlots(scene)
                .Select(s => layout.PatternAt(s.Track, scene))
                .Where(p => p != null)
                .Select(p =>
                {
                    var c = layout.ClipOf(p);
                    var (start, _, regionEnd) = new ClipShape(c.Start, c.LoopStart, c.LoopEnd, c.Loop).Resolve(p.Length);
                    return regionEnd - start;
                })
                .DefaultIfEmpty(0).Max();
            int bars = Math.Max(1, (longest + ticksPerBar - 1) / ticksPerBar);
            return bars * ticksPerBar;
        }
    }
}
