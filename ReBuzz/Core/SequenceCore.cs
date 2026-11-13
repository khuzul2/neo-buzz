using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using System;
using System.ComponentModel;
using System.Linq;
using Wintellect.PowerCollections;

namespace ReBuzz.Core
{
    internal class SequenceCore : ISequence, ISessionTrack
    {
        // Session View engine state for this track (fork addition)
        object ISessionTrack.SessionState { get; set; }

        // Audio thread (SessionCore.ResolveGains, 0.5.1): what sets this track's volume now (a
        // session clip or an arrangement event), and when it started (a stamp; later is newer).
        internal object GainSource;
        internal long GainSourceKey;
        internal long GainStamp;

        // Follow actions (0.6): this track's column of slots from the layout's launch map, for the
        // scheduler (set on the UI thread, read on the audio thread).
        internal volatile object FollowColumn;

        // Session recording (0.7): the clip that last stopped here (or was replaced), and the
        // clock it stopped on (exclusive): written by the audio thread, read by the UI thread to
        // give a take recorded until stopped its length.
        internal long SessionStoppedAt = -1;
        internal volatile PatternCore SessionStoppedClip;

        /// <summary>
        /// Fork (0.6.1): set (under AudioLock) when the sequence is removed from the song. Removed
        /// sequences never come back (undo adds a new one), so the Session View rejects launches
        /// onto one that a stale reference still holds.
        /// </summary>
        internal volatile bool IsRemoved;

        private static IntPtr sequenceHandleCounter = 100;

        internal MachineCore MachineCore { get; private set; }
        public IMachine Machine { get => MachineCore; }

        OrderedDictionary<int, SequenceEvent> eventsList;
        public OrderedDictionary<int, SequenceEvent> EventsList
        {
            get => eventsList;
            set
            {
                eventsList = value;
                RebuildArranged();
            }
        }
        public ReadOnlyDictionary<int, SequenceEvent> Events { get => new ReadOnlyDictionary<int, SequenceEvent>(EventsList); }

        /// <summary>
        /// Fork (0.4.1): the events as sorted arrays, for the audio thread and PlayingPattern
        /// (no tree walk, no allocation). Replaced whenever events are added or removed, which
        /// happens under AudioLock; events' spans are shared with <see cref="EventsList"/>.
        /// </summary>
        internal sealed class ArrangedEvents
        {
            public static readonly ArrangedEvents Empty = new ArrangedEvents(Array.Empty<int>(), Array.Empty<SequenceEvent>());

            public ArrangedEvents(int[] times, SequenceEvent[] events)
            {
                Times = times;
                Events = events;
            }

            public int[] Times { get; }
            public SequenceEvent[] Events { get; }

            /// <summary>Index of the last event at or before <paramref name="time"/>, or -1.</summary>
            public int IndexAtOrBefore(int time)
            {
                int lo = 0, hi = Times.Length - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (Times[mid] <= time)
                    {
                        found = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }
                return found;
            }
        }

        volatile ArrangedEvents arranged = ArrangedEvents.Empty;
        internal ArrangedEvents Arranged => arranged;

        internal void RebuildArranged()
        {
            var list = eventsList;
            if (list == null || list.Count == 0)
            {
                arranged = ArrangedEvents.Empty;
                return;
            }
            var times = new int[list.Count];
            var events = new SequenceEvent[list.Count];
            int i = 0;
            foreach (var kv in list)
            {
                times[i] = kv.Key;
                events[i] = kv.Value;
                i++;
            }
            arranged = new ArrangedEvents(times, events);
        }

        public IPattern PlayingPattern
        {
            get
            {
                var buzz = MachineCore.Graph.Buzz as ReBuzzCore;

                if (buzz.SoloPattern != null && buzz.SoloPattern.Machine == Machine)
                    return buzz.SoloPattern;

                // Session View (fork): a launched clip wins, and a track taken over by the
                // session ignores its arrangement until "back to arrangement".
                var session = buzz.SessionCore;
                var clip = session.GetPlayingCore(this);
                if (clip != null && clip.PlayPosition != int.MinValue)
                    return clip;
                if (session.IsArrangementOverridden(this))
                    return null;

                // Fork (0.4.1): the pattern of this track's event at the song position, if it plays
                // here. Upstream returned the first event (in time) whose pattern had a position,
                // which could be a pattern another track of the same machine was playing.
                var events = arranged;
                int songPosition = buzz.SongCore.PlayPosition;
                int index = events.IndexAtOrBefore(songPosition);
                if (index < 0) return null;
                var e = events.Events[index];
                if (e.Type != SequenceEventType.PlayPattern || songPosition >= events.Times[index] + e.Span) return null;
                var pattern = e.Pattern as PatternCore;
                if (pattern == null || pattern.PlayPosition == int.MinValue) return null;
                if (pattern.SessionTrack != null && pattern.SessionTrack != this) return null;
                return pattern;
            }
        }

        // Returns ticks
        public int PlayingPatternPosition
        {
            get
            {
                var pattern = PlayingPattern;
                if (pattern != null)
                {
                    return pattern.PlayPosition / PatternEvent.TimeBase;
                }
                return -1;
            }
        }

        public IntPtr CSequence { get; private set; }


        bool isDisabled;
        public bool IsDisabled
        {
            get => isDisabled;
            set
            {
                isDisabled = value;
                if (isDisabled)
                {
                    // Maybe setting to send stop after disable?
                    //var buzz = Global.Buzz as ReBuzzCore;
                    //buzz.MachineManager.Stop(Machine as MachineCore);
                }
                PropertyChanged.Raise(this, "IsDisabled");
            }
        }

        public SequenceCore(MachineCore mc)
        {
            MachineCore = mc;
            CSequence = sequenceHandleCounter++;
            EventsList = new OrderedDictionary<int, SequenceEvent>();
        }

        public event Action<int> EventChanged;
        public event Action<int, int> SpanInserted;
        public event Action<int, int> SpanDeleted;
        public event Action<int, int> SpanCleared;
        public event PropertyChangedEventHandler PropertyChanged;

        // Fork (0.4.1): edits change the events under AudioLock and raise events after releasing
        // it, so sequence editors redraw without holding up the audio thread (the Session View
        // writes the arrangement while playing).

        public void Clear(int time, int span)
        {
            lock (ReBuzzCore.AudioLock)
            lock (eventsLock)              // fork (0.6.1): spans and the swap together
            {
                OrderedDictionary<int, SequenceEvent> od = new OrderedDictionary<int, SequenceEvent>();
                foreach (var kv in EventsList)
                {
                    if (kv.Key < time || kv.Key >= time + span)
                    {
                        od.Add(kv.Key, kv.Value);
                    }
                }

                UpdateSpans(od);
                EventsList = od;
            }
            PropertyChanged.Raise(this, "Events");
            SpanCleared?.Invoke(time, span);   // fork: ?. like Insert/Delete (no listener without a sequence editor)

            (Global.Buzz as ReBuzzCore).SetModifiedFlag();
        }

        public void Delete(int time, int span)
        {
            lock (ReBuzzCore.AudioLock)
            lock (eventsLock)              // fork (0.6.1): spans and the swap together
            {
                OrderedDictionary<int, SequenceEvent> od = new OrderedDictionary<int, SequenceEvent>();
                foreach (var kv in EventsList)
                {
                    int eventTime = kv.Key;
                    var e = kv.Value;
                    if (eventTime >= time && eventTime < time + span)
                    {
                        // Don't add events to updated sequence if between time and span (delete)
                    }
                    else if (eventTime >= time + span)
                    {
                        od.Add(eventTime - span, e);
                    }
                    else
                    {
                        od.Add(eventTime, e);
                    }
                }

                UpdateSpans(od);
                EventsList = od;
            }
            SpanDeleted?.Invoke(time, span);
            PropertyChanged.Raise(this, "Events");
            (Global.Buzz as ReBuzzCore).SetModifiedFlag();
        }

        public void Insert(int time, int span)
        {
            lock (ReBuzzCore.AudioLock)
            lock (eventsLock)              // fork (0.6.1): spans and the swap together
            {
                OrderedDictionary<int, SequenceEvent> od = new OrderedDictionary<int, SequenceEvent>();
                foreach (var kv in EventsList)
                {
                    int eventTime = kv.Key;
                    var e = kv.Value;
                    if (eventTime >= time)
                    {
                        od.Add(eventTime + span, e);
                    }
                    else
                    {
                        od.Add(eventTime, e);
                    }
                }

                UpdateSpans(od);
                EventsList = od;
            }
            SpanInserted?.Invoke(time, span);
            PropertyChanged.Raise(this, "Events");
            (Global.Buzz as ReBuzzCore).SetModifiedFlag();
        }

        /// <summary>
        /// Fork (0.6.1): guards in-place changes of <see cref="EventsList"/> against readers that
        /// can't take AudioLock (span refreshes from the IPC thread). Taken inside AudioLock, never
        /// while waiting for anything.
        /// </summary>
        readonly object eventsLock = new object();

        /// <summary>Fork (0.6.1): a copy of the events of a pattern (any thread).</summary>
        internal System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, SequenceEvent>> EventsOfPattern(IPattern pattern)
        {
            lock (eventsLock)
                return EventsList.Where(e => e.Value.Pattern == pattern).ToList();
        }

        public void SetEvent(int time, SequenceEvent e)
        {
            bool changed = false;
            lock (ReBuzzCore.AudioLock)
            lock (eventsLock)
            {
                if (e == null)
                {
                    if (EventsList.ContainsKey(time))
                    {
                        EventsList.Remove(time);
                        UpdateSpans(EventsList);
                        RebuildArranged();
                        changed = true;
                    }
                }
                else
                {
                    if (e.Type == SequenceEventType.PlayPattern && e.Pattern != null)
                        e.Span = e.PlayedLength;    // fork (0.5.1): the part of the pattern the event plays

                    this.EventsList[time] = e;
                    UpdateSpans(EventsList);
                    RebuildArranged();
                    changed = true;
                }
            }
            if (changed)
            {
                PropertyChanged.Raise(this, "Events");
                EventChanged?.Invoke(time);
            }
            (Global.Buzz as ReBuzzCore).SetModifiedFlag();
        }

        /// <summary>Fork: removes every event of a pattern (it was deleted); call under AudioLock. Returns whether any was removed.</summary>
        internal bool RemoveEventsOf(IPattern pattern)
        {
            var times = new System.Collections.Generic.List<int>();
            lock (eventsLock)
            {
                foreach (var kv in EventsList)
                    if (kv.Value.Pattern == pattern)
                        times.Add(kv.Key);
                foreach (int t in times)
                    EventsList.Remove(t);
            }
            if (times.Count > 0)
                RebuildArranged();
            return times.Count > 0;
        }

        /// <summary>
        /// Fork (0.5.1): recomputes spans after a pattern's length or an event's offset or end
        /// changed, and tells the sequence editors. Only the spans (ints the audio thread reads)
        /// change, so no AudioLock: native pattern editors change lengths from the IPC thread.
        /// </summary>
        internal void RefreshSpans()
        {
            lock (eventsLock)
                UpdateSpans(EventsList);
            PropertyChanged.Raise(this, "Events");
        }

        static void UpdateSpans(OrderedDictionary<int, SequenceEvent> list)
        {
            var events = list.ToArray();
            for (int i = 0; i < events.Length; i++)
            {
                int time = events[i].Key;
                var se = events[i].Value;
                if (se.Type == SequenceEventType.PlayPattern)
                {
                    se.Span = se.PlayedLength;      // fork (0.5.1): from its offset to its end
                    if ((i < events.Length - 1) && (time + se.Span > events[i + 1].Key))
                    {
                        events[i].Value.Span = events[i + 1].Key - time;
                    }
                }
            }
        }

        // Fork: live triggering goes through the Session View engine, which quantizes it, keeps
        // the pattern's phase, and overrides the arrangement (ReBuzz.Core.Session.SessionCore).
        public void TriggerEvent(int time, SequenceEvent e, bool loop)
        {
            (MachineCore.Graph.Buzz as ReBuzzCore)?.SessionCore.TriggerFromSequence(this, time, e, loop);
        }

        internal void SequenceEventChanged(int time, SequenceEvent e)
        {
            PropertyChanged.Raise(this, "Events");
            EventChanged?.Invoke(time);
        }

        internal void Release()
        {
            lock (eventsLock)
                EventsList.Clear();
            RebuildArranged();
            PropertyChanged.Raise(this, "Events");
            PropertyChanged = null;
            EventChanged = null;
            SpanInserted = null;
            SpanDeleted = null;
            SpanCleared = null;
        }

        /// <summary>
        /// Fork (history-snapshots.md HS3): the audio thread puts a live restore's events in place at the boundary: the dictionary and
        /// the sorted arrays were built beforehand on the UI thread, so this is two reference swaps under the events lock and
        /// allocates nothing. Returns the old dictionary (for the UI thread to drop). Listeners are told afterwards by <see cref="InvokeEvents"/>.
        /// </summary>
        internal OrderedDictionary<int, SequenceEvent> SwapEvents(OrderedDictionary<int, SequenceEvent> events, ArrangedEvents arrangedEvents)
        {
            lock (eventsLock)
            {
                var old = eventsList;
                eventsList = events;
                arranged = arrangedEvents;
                return old;
            }
        }

        /// <summary>Fork (HS3): the sorted arrays for a dictionary, built the way <see cref="RebuildArranged"/> builds them.</summary>
        internal static ArrangedEvents Arrange(OrderedDictionary<int, SequenceEvent> list)
        {
            if (list == null || list.Count == 0) return ArrangedEvents.Empty;
            var times = new int[list.Count];
            var events = new SequenceEvent[list.Count];
            int i = 0;
            foreach (var kv in list) { times[i] = kv.Key; events[i] = kv.Value; i++; }
            return new ArrangedEvents(times, events);
        }

        internal void InvokeEvents()
        {
            PropertyChanged.Raise(this, "Events");
        }
    }
}
