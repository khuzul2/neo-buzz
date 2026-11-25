using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;

namespace ReBuzz.Core
{
    internal class ClipColumnCore : IClipColumn
    {
        public ClipColumnType Type { get; }

        public IClip Clip { get; }

        public IMachine Machine
        {
            get;
            set;
        }

        public IParameter Parameter { get; }

        public int Track { get; set; }

        List<ClipEvent> clipEvents = new List<ClipEvent>();   // fork (history-snapshots.md HS3): swapped whole by a live restore (SwapEvents)

        readonly Dictionary<int, int> beatSubdivision;
        public ReadOnlyDictionary<int, int> BeatSubdivision { get => IDictionaryExtensions.AsReadOnly(beatSubdivision); }

        readonly Dictionary<string, string> metadata = new Dictionary<string, string>();
        public IDictionary<string, string> Metadata { get => metadata; }

        /// <summary>
        /// Fork (0.7): metadata of a parameter column recorded by the Session View (automation).
        /// The engine plays such columns itself; other parameter columns belong to clip
        /// editors, which play their own data.
        /// </summary>
        internal const string SessionAutomationKey = "SessionAutomation";

        /// <summary>
        /// Fork (docs/specs/clips.md §5.6, CU3): the host plays every parameter lane of a clip from the engine, marked or not (one store,
        /// one engine). The Studio and rebuzz-headless set it when they start (EngineHost.PrepareProcess); Classic's WPF app does not, and
        /// there the clip editor still plays its own data and the engine only the marked columns, as before.
        /// </summary>
        internal static volatile bool PlaysEveryLane;

        /// <summary>
        /// Fork (0.7, CU3): a parameter column the engine plays: a marked one (session automation), and with <see cref="PlaysEveryLane"/> every
        /// one (the mark only says whether an older build may play it).
        /// </summary>
        internal bool EnginePlays => Type == ClipColumnType.Parameter && (PlaysEveryLane || metadata.ContainsKey(SessionAutomationKey));

        /// <summary>The mark of session automation, for the derived blob and the old builds: a column the engine played before CU3.</summary>
        internal bool IsMarkedAutomation => Type == ClipColumnType.Parameter && metadata.ContainsKey(SessionAutomationKey);

        /// <summary>
        /// Fork (docs/specs/daw-modes.md S3): whether an event has a Duration above 0, which in a
        /// column the engine plays means a ramp: the value goes in a straight line from that event
        /// to the next one (WorkManager sets it on each tick and subtick where it changed).
        /// Columns without any play as steps, as before.
        /// </summary>
        internal bool HasRamps => hasRamps;
        volatile bool hasRamps;

        public event Action<IEnumerable<ClipEvent>, bool> EventsChanged;
        public event Action<int> BeatSubdivisionChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        public ClipColumnCore(ClipColumnType type, IClip clip, IMachine machine, IParameter parameter, int track, Dictionary<int, int> beatSubdivisions, Dictionary<string, string> metadatas)
        {
            Type = type;
            Clip = clip;
            Machine = machine;
            Parameter = parameter;
            Track = track;
            beatSubdivision = beatSubdivisions;
            if (beatSubdivision == null)
                beatSubdivision = new Dictionary<int, int>();
            metadata = metadatas;
            if (metadata == null)
                metadata = new Dictionary<string, string>();
        }

        ClipEvent[] emptyClipEventList = Array.Empty<ClipEvent>();
        Lock clipEventsLock = new Lock();
        // Returns the events from ClipColumn object. Not from clip editor machine.
        // Returns a fresh snapshot array (callers may enumerate it multiple times and
        // across threads - e.g. the BMX save path Count()s then foreach()es it). All
        // reads of the shared event list happen under the lock.
        public IEnumerable<ClipEvent> GetEvents(int tbegin, int tend)
        {
            lock (clipEventsLock)
            {
                if (clipEvents.Count == 0)
                    return emptyClipEventList;

                var list = new List<ClipEvent>();
                for (int i = 0; i < clipEvents.Count; i++)
                {
                    var pe = clipEvents[i];
                    if (pe.Time >= tbegin && pe.Time < tend)
                        list.Add(pe);
                }
                return list.ToArray();
            }
        }

        // Zero-alloc variant for the audio play path: fills a caller-owned buffer
        // instead of allocating a snapshot array. `dest` MUST be owned exclusively by
        // the caller (the audio thread passes its own reusable list); the old shared
        // scratch field this replaces was itself a cross-thread race (its Clear() ran
        // outside the lock). The lock guards the shared source list only.
        public void GetEventsInto(int tbegin, int tend, List<ClipEvent> dest)
        {
            dest.Clear();
            lock (clipEventsLock)
            {
                int count = clipEvents.Count;
                for (int i = 0; i < count; i++)
                {
                    var pe = clipEvents[i];
                    if (pe.Time >= tbegin && pe.Time < tend)
                        dest.Add(pe);
                }
            }
        }

        /// <summary>
        /// Fork (history-snapshots.md HS3): a live restore's events for the column, built beforehand on the UI thread. The audio thread
        /// swaps the list under the events lock (a reference swap, no allocation) and gets the old list back to hand to the UI thread
        /// (nothing is raised here). The list must not be touched by the caller afterwards.
        /// </summary>
        internal List<ClipEvent> SwapEvents(List<ClipEvent> events, bool ramps)
        {
            lock (clipEventsLock)
            {
                var old = clipEvents;
                clipEvents = events;
                hasRamps = ramps;
                return old;
            }
        }

        /// <summary>Fork (HS3): a copy of the events, for a live restore's plan (any thread).</summary>
        internal List<ClipEvent> CopyEvents()
        {
            lock (clipEventsLock)
                return new List<ClipEvent>(clipEvents);
        }

        public void SetBeatSubdivision(int beatindex, int subdiv)
        {
            beatSubdivision[beatindex] = subdiv;
            BeatSubdivisionChanged?.Invoke(beatindex);
        }

        public void SetEvents(IEnumerable<ClipEvent> clipEvents, bool set)
        {
            // Fork (0.6.3): one batch under one lock, so the audio thread sees all of it or none.
            var batch = clipEvents as ICollection<ClipEvent> ?? clipEvents.ToList();
            lock (clipEventsLock)
            {
                foreach (var pe in batch)
                {
                    if (set)
                        this.clipEvents.Add(pe);
                    else
                        this.clipEvents.Remove(pe);
                }
                // Fork (daw-modes.md S3).
                bool ramps = false;
                for (int i = 0; i < this.clipEvents.Count && !ramps; i++)
                    ramps = this.clipEvents[i].Duration > 0;
                hasRamps = ramps;
            }
            EventsChanged?.Invoke(batch, set);
        }

        /// <summary>
        /// Fork (daw-modes.md S3): where a ramp is at <paramref name="time"/> (clip time): when
        /// the last event before it has a Duration above 0 and another event follows, the value on
        /// the straight line between the two, rounded to the nearest whole value. False otherwise:
        /// no ramp there (steps), or an event exactly at <paramref name="time"/>, which plays itself.
        /// Audio thread: allocates nothing.
        /// </summary>
        internal bool TryRampValue(int time, out int value)
        {
            value = 0;
            if (!hasRamps)
                return false;
            lock (clipEventsLock)
            {
                bool before = false, after = false;
                int t0 = 0, v0 = 0, d0 = 0, t1 = 0, v1 = 0;
                for (int i = 0; i < clipEvents.Count; i++)
                {
                    var pe = clipEvents[i];
                    if (pe.Time <= time)
                    {
                        // The last at that time (in list order) is the one that set the value.
                        if (!before || pe.Time >= t0)
                        {
                            t0 = pe.Time;
                            v0 = pe.Value;
                            d0 = pe.Duration;
                            before = true;
                        }
                    }
                    else if (!after || pe.Time < t1)
                    {
                        t1 = pe.Time;
                        v1 = pe.Value;
                        after = true;
                    }
                }
                if (!before || !after || d0 <= 0 || t0 == time)
                    return false;
                long span = (long)t1 - t0;
                long rise = (long)(v1 - v0) * (time - t0);
                value = v0 + (int)(rise >= 0 ? (rise + span / 2) / span : -((-rise + span / 2) / span));
                return true;
            }
        }
    }
}
