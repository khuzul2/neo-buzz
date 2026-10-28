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
    ///
    /// SessionCore reports from the audio thread (tick time) into a queue; the writes happen
    /// on the UI thread, since ISequence.SetEvent notifies the sequence editors.
    /// </summary>
    internal sealed class SessionRecorder
    {
        enum Kind { Start, Stop, Cut, Release, End }

        readonly record struct Entry(Kind Kind, SequenceCore Sequence, PatternCore Pattern, int Position);

        sealed class TrackPass
        {
            public int LastWrite;
            public KeyValuePair<int, SequenceEvent>[] Before;
            public bool Open = true;
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

        /// <summary>A clip started from its first tick (launch or loop restart) at a song position.</summary>
        internal void ClipStarted(SequenceCore seq, PatternCore pattern, int position) => Post(new Entry(Kind.Start, seq, pattern, position));

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
                track = new TrackPass { LastWrite = e.Position, Before = seq.Events.ToArray() };
                pass[seq] = track;
            }
            else if (!track.Open)
            {
                track.Open = true;
                track.LastWrite = e.Position;
            }

            if (e.Kind == Kind.Release)
            {
                ClearBetween(seq, track.LastWrite, e.Position);
                track.Open = false;
                return;
            }

            ClearBetween(seq, track.LastWrite, e.Position);
            if (e.Kind == Kind.Start && e.Pattern != null && e.Pattern.Machine == seq.Machine)
                seq.SetEvent(e.Position, new SequenceEvent(SequenceEventType.PlayPattern, e.Pattern));
            else if (e.Kind == Kind.Stop)
                seq.SetEvent(e.Position, new SequenceEvent(SequenceEventType.Off));
            else if (e.Kind == Kind.Cut)
                seq.SetEvent(e.Position, new SequenceEvent(SequenceEventType.Break));
            track.LastWrite = e.Position;
        }

        /// <summary>Removes events strictly between two positions (nothing if they wrapped).</summary>
        static void ClearBetween(SequenceCore seq, int after, int before)
        {
            if (before - after > 1 && seq.Events.Keys.Any(t => t > after && t < before))
                seq.Clear(after + 1, before - after - 1);
        }

        void FinishPass(int position)
        {
            if (pass == null) return;
            var written = pass;
            pass = null;

            var changes = new List<ArrangementSnapshotAction.SequenceChange>();
            foreach (var kv in written)
            {
                var seq = kv.Key;
                if (!buzz.SongCore.SequencesList.Contains(seq)) continue;
                if (kv.Value.Open)
                    ClearBetween(seq, kv.Value.LastWrite, position);
                changes.Add(new ArrangementSnapshotAction.SequenceChange(seq, kv.Value.Before, seq.Events.ToArray()));
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
    /// inserted into the arrangement): the events of the sequences it touched, and the song's
    /// loop and song end, before and after. The edit is already made when the action is
    /// pushed, so the first Do does nothing.
    /// </summary>
    internal sealed class ArrangementSnapshotAction : IAction
    {
        internal readonly record struct SequenceChange(ISequence Sequence, KeyValuePair<int, SequenceEvent>[] Before, KeyValuePair<int, SequenceEvent>[] After);

        readonly SongCore song;
        readonly List<SequenceChange> changes;
        readonly (int LoopEnd, int SongEnd) endsBefore;
        readonly (int LoopEnd, int SongEnd) endsAfter;
        bool skipDo = true;

        public ArrangementSnapshotAction(SongCore song, List<SequenceChange> changes, (int, int) endsBefore, (int, int) endsAfter)
        {
            this.song = song;
            this.changes = changes;
            this.endsBefore = endsBefore;
            this.endsAfter = endsAfter;
        }

        public void Do()
        {
            if (skipDo) { skipDo = false; return; }
            foreach (var c in changes) Restore(c.Sequence, c.After);
            SetEnds(endsAfter);
        }

        public void Undo()
        {
            foreach (var c in changes) Restore(c.Sequence, c.Before);
            SetEnds(endsBefore);
        }

        void SetEnds((int LoopEnd, int SongEnd) ends)
        {
            if (ends.LoopEnd <= 0) return;
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

        static void Restore(ISequence seq, KeyValuePair<int, SequenceEvent>[] events)
        {
            if (!seq.Machine?.Graph?.Buzz?.Song?.Sequences.Contains(seq) ?? true) return;
            if (seq.Events.Count > 0)
                seq.Clear(0, int.MaxValue);
            foreach (var e in events)
            {
                if (e.Value.Pattern != null && !seq.Machine.Patterns.Contains(e.Value.Pattern)) continue;
                seq.SetEvent(e.Key, new SequenceEvent(e.Value));
            }
        }
    }

    /// <summary>Scene to arrangement (0.3): a scene's clips written into the sequences.</summary>
    internal static class SceneToArrangement
    {
        /// <summary>
        /// Writes the scene's clips at <paramref name="position"/> (ticks) for
        /// <paramref name="length"/> ticks, repeating each clip and cutting the last repeat at
        /// the end. With <paramref name="insert"/>, every sequence's later events move back by
        /// the length first; otherwise the scene's tracks are overwritten in that range.
        /// One undo step in the Session View. Returns the number of tracks written.
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
                for (int t = position; t < end; t += pattern.Length)
                    seq.SetEvent(t, new SequenceEvent(SequenceEventType.PlayPattern, pattern));
                // Cut a last repeat that runs past the end, unless something already starts there.
                int lastStart = position + (length - 1) / pattern.Length * pattern.Length;
                if (lastStart + pattern.Length > end && !seq.Events.ContainsKey(end))
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

        /// <summary>Length of a scene in ticks: its longest clip, rounded up to whole bars.</summary>
        public static int SceneLength(SessionLayout layout, int scene, int ticksPerBar)
        {
            var doc = layout.Document;
            if (doc == null || ticksPerBar <= 0) return ticksPerBar;
            int longest = doc.SceneSlots(scene).Select(s => layout.PatternAt(s.Track, scene)?.Length ?? 0).DefaultIfEmpty(0).Max();
            int bars = Math.Max(1, (longest + ticksPerBar - 1) / ticksPerBar);
            return bars * ticksPerBar;
        }
    }
}
