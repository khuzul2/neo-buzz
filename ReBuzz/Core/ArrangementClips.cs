// Fork (docs/specs/daw-modes.md S2): arrangement clips over Buzz's sequence events. A clip is a
// run of PlayPattern events of one pattern on one sequence, joined by a clip id
// (SequenceEvent.Clip): the first plays from the clip's start (its Offset), the next ones play
// the loop, the last one is cut at the clip's end. Loops are repeated events, so upstream ReBuzz
// plays them; it doesn't know the id and plays each event, cut at the next.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core
{
    /// <summary>
    /// A clip's loop: where its repeats start and end in its pattern (ticks; End 0 for the
    /// pattern's end), and whether it loops. Kept while it doesn't loop, for when it does again.
    /// </summary>
    internal readonly record struct ClipLoop(int Start, int End, bool On);

    /// <summary>
    /// One clip of a sequence, read from its events: its id (0: an event without one), its
    /// pattern, where it starts and how long it plays (ticks, cut at the next event), where its
    /// first event starts in the pattern, how many events it has, and its loop if it has one.
    /// </summary>
    internal readonly record struct ArrangementClip(int Id, IPattern Pattern, int Time, int Length, int Offset, int Events, ClipLoop? Loop)
    {
        public int End => Time + Length;
    }

    /// <summary>
    /// The song's clip ids and loops. ArrangementEventStore saves them in the song file. Ids
    /// aren't reused while the song is open, so an undo that brings events back never finds
    /// their id given to another clip.
    /// </summary>
    internal sealed class ArrangementClips
    {
        readonly Dictionary<int, ClipLoop> loops = new Dictionary<int, ClipLoop>();
        int next = 1;

        /// <summary>An id for a new clip.</summary>
        public int NewId() => next++;

        /// <summary>The loops by clip id (some of them may be of clips no longer in the song).</summary>
        public IReadOnlyDictionary<int, ClipLoop> Loops => loops;

        public bool TryGetLoop(int id, out ClipLoop loop) => loops.TryGetValue(id, out loop);

        public void SetLoop(int id, ClipLoop loop)
        {
            if (id <= 0) return;
            loops[id] = new ClipLoop(Math.Max(0, loop.Start), Math.Max(0, loop.End), loop.On);
        }

        /// <summary>A new song: no clips.</summary>
        public void Reset()
        {
            loops.Clear();
            next = 1;
        }

        /// <summary>After a song opened: new ids follow the highest one in it.</summary>
        public void ContinueAfter(IEnumerable<ISequence> sequences)
        {
            int max = 0;
            foreach (var seq in sequences)
                foreach (var e in seq.Events.Values)
                    max = Math.Max(max, e.Clip);
            next = Math.Max(next, max + 1);
        }

        /// <summary>
        /// The clips of a sequence, in time order. Consecutive PlayPattern events of one pattern
        /// with the same id, each playing on to where the next starts, are one clip; any other
        /// event, or a gap, ends it. An event without an id is a clip of its own.
        /// </summary>
        public List<ArrangementClip> Of(ISequence seq)
        {
            var clips = new List<ArrangementClip>();
            if (seq == null) return clips;
            ArrangementClip? open = null;
            int openEnd = 0;
            foreach (var kv in seq.Events.OrderBy(e => e.Key))
            {
                var e = kv.Value;
                int time = kv.Key;
                if (e.Type != SequenceEventType.PlayPattern || e.Pattern == null)
                {
                    if (open != null) clips.Add(open.Value);
                    open = null;
                    continue;
                }
                int span = Math.Max(0, e.Span);
                if (open is ArrangementClip c && e.Clip != 0 && c.Id == e.Clip && ReferenceEquals(c.Pattern, e.Pattern) && openEnd == time)
                {
                    open = c with { Length = time + span - c.Time, Events = c.Events + 1 };
                    openEnd = time + span;
                    continue;
                }
                if (open != null) clips.Add(open.Value);
                ClipLoop? loop = e.Clip != 0 && loops.TryGetValue(e.Clip, out var l) ? l : null;
                open = new ArrangementClip(e.Clip, e.Pattern, time, span, e.PlayedOffset, 1, loop);
                openEnd = time + span;
            }
            if (open != null) clips.Add(open.Value);
            return clips;
        }
    }
}
