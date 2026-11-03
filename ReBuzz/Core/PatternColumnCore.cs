using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;

namespace ReBuzz.Core
{
    internal class PatternColumnCore : IPatternColumn
    {
        public PatternColumnType Type { get; }

        public IPattern Pattern { get; }

        public IMachine Machine
        {
            get;
            set;
        }

        public IParameter Parameter { get; }

        public int Track { get; set; }

        readonly List<PatternEvent> patternEvents = new List<PatternEvent>();

        readonly Dictionary<int, int> beatSubdivision;
        public ReadOnlyDictionary<int, int> BeatSubdivision { get => IDictionaryExtensions.AsReadOnly(beatSubdivision); }

        readonly Dictionary<string, string> metadata = new Dictionary<string, string>();
        public IDictionary<string, string> Metadata { get => metadata; }

        /// <summary>
        /// Fork (0.7): metadata of a parameter column recorded by the Session View (automation).
        /// The engine plays such columns itself; other parameter columns belong to pattern
        /// editors, which play their own data.
        /// </summary>
        internal const string SessionAutomationKey = "SessionAutomation";

        /// <summary>Fork (0.7): a parameter column the engine plays (session automation).</summary>
        internal bool EnginePlays => Type == PatternColumnType.Parameter && metadata.ContainsKey(SessionAutomationKey);

        /// <summary>
        /// Fork (docs/specs/daw-modes.md S3): whether an event has a Duration above 0, which in a
        /// column the engine plays means a ramp: the value goes in a straight line from that event
        /// to the next one (WorkManager sets it on each tick and subtick where it changed).
        /// Columns without any play as steps, as before.
        /// </summary>
        internal bool HasRamps => hasRamps;
        volatile bool hasRamps;

        public event Action<IEnumerable<PatternEvent>, bool> EventsChanged;
        public event Action<int> BeatSubdivisionChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        public PatternColumnCore(PatternColumnType type, IPattern pattern, IMachine machine, IParameter parameter, int track, Dictionary<int, int> beatSubdivisions, Dictionary<string, string> metadatas)
        {
            Type = type;
            Pattern = pattern;
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

        PatternEvent[] emptyPatternEventList = Array.Empty<PatternEvent>();
        Lock patternEventsLock = new Lock();
        // Returns the events from PatternColumn object. Not from pattern editor machine.
        // Returns a fresh snapshot array (callers may enumerate it multiple times and
        // across threads - e.g. the BMX save path Count()s then foreach()es it). All
        // reads of the shared event list happen under the lock.
        public IEnumerable<PatternEvent> GetEvents(int tbegin, int tend)
        {
            lock (patternEventsLock)
            {
                if (patternEvents.Count == 0)
                    return emptyPatternEventList;

                var list = new List<PatternEvent>();
                for (int i = 0; i < patternEvents.Count; i++)
                {
                    var pe = patternEvents[i];
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
        public void GetEventsInto(int tbegin, int tend, List<PatternEvent> dest)
        {
            dest.Clear();
            lock (patternEventsLock)
            {
                int count = patternEvents.Count;
                for (int i = 0; i < count; i++)
                {
                    var pe = patternEvents[i];
                    if (pe.Time >= tbegin && pe.Time < tend)
                        dest.Add(pe);
                }
            }
        }

        public void SetBeatSubdivision(int beatindex, int subdiv)
        {
            beatSubdivision[beatindex] = subdiv;
            BeatSubdivisionChanged?.Invoke(beatindex);
        }

        public void SetEvents(IEnumerable<PatternEvent> patternEvents, bool set)
        {
            // Fork (0.6.3): one batch under one lock, so the audio thread sees all of it or none.
            var batch = patternEvents as ICollection<PatternEvent> ?? patternEvents.ToList();
            lock (patternEventsLock)
            {
                foreach (var pe in batch)
                {
                    if (set)
                        this.patternEvents.Add(pe);
                    else
                        this.patternEvents.Remove(pe);
                }
                // Fork (daw-modes.md S3).
                bool ramps = false;
                for (int i = 0; i < this.patternEvents.Count && !ramps; i++)
                    ramps = this.patternEvents[i].Duration > 0;
                hasRamps = ramps;
            }
            EventsChanged?.Invoke(batch, set);
        }

        /// <summary>
        /// Fork (daw-modes.md S3): where a ramp is at <paramref name="time"/> (pattern time): when
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
            lock (patternEventsLock)
            {
                bool before = false, after = false;
                int t0 = 0, v0 = 0, d0 = 0, t1 = 0, v1 = 0;
                for (int i = 0; i < patternEvents.Count; i++)
                {
                    var pe = patternEvents[i];
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
