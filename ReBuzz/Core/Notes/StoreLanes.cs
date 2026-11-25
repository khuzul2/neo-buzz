// Fork (docs/specs/clips.md §5.1, §8, CU4): where one parameter and track of a clip is kept in the store. A clip can hold two lanes for
// the same parameter and track: the unmarked lane the tracker writes and the marked one a session take or the agent made (§5.5, AR4).
// Every reader of notes and automation sees all of them; a writer takes events out of whichever lane holds them and puts new ones in
// the lane that is there (the unmarked one first), or makes one: unmarked where the engine plays every lane (so the tracker shows it),
// marked where it still reads the mark (Classic's semantics and the engine's own tests).

using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Notes
{
    internal static class StoreLanes
    {
        public static bool IsMarked(IPatternColumn c) => c.Metadata.ContainsKey(PatternColumnCore.SessionAutomationKey);

        /// <summary>The parameter a key names in the clip's machine, or null.</summary>
        public static IParameter ParameterOf(IPattern clip, ColumnKey key)
        {
            var groups = clip?.Machine?.ParameterGroups;
            if (groups == null || key.Group < 0 || key.Group >= groups.Count || key.Index < 0 || key.Index >= groups[key.Group].Parameters.Count) return null;
            return groups[key.Group].Parameters[key.Index];
        }

        /// <summary>Every lane of the key: the unmarked one first, then the marked. The MIDI key is the Notes lane alone.</summary>
        public static List<IPatternColumn> All(IPattern clip, ColumnKey key)
        {
            var lanes = new List<IPatternColumn>();
            if (clip == null) return lanes;
            if (key.IsMidi)
            {
                if (SessionLayout.ColumnFor(clip, key, create: false) is { } midi) lanes.Add(midi);
                return lanes;
            }
            var parameter = ParameterOf(clip, key);
            if (parameter == null) return lanes;
            bool Same(IPatternColumn c) => c.Type == PatternColumnType.Parameter && ReferenceEquals(c.Parameter, parameter) && c.Track == key.Track;
            lanes.AddRange(clip.Columns.Where(c => Same(c) && !IsMarked(c)));
            lanes.AddRange(clip.Columns.Where(c => Same(c) && IsMarked(c)));
            return lanes;
        }

        /// <summary>The events of every lane of the key, in time order.</summary>
        public static List<PatternEvent> Events(IPattern clip, ColumnKey key)
        {
            var events = new List<PatternEvent>();
            foreach (var lane in All(clip, key)) events.AddRange(lane.GetEvents(int.MinValue, int.MaxValue));
            return events.OrderBy(e => e.Time).ToList();
        }

        /// <summary>The lane new events go to: the first there is, else (when asked) a new one. Null when there is none and none is made.</summary>
        public static IPatternColumn Home(IPattern clip, ColumnKey key, bool create)
        {
            if (All(clip, key).FirstOrDefault() is { } lane) return lane;
            if (!create) return null;
            if (key.IsMidi || !PatternColumnCore.PlaysEveryLane) return SessionLayout.ColumnFor(clip, key, create: true);
            var parameter = ParameterOf(clip, key);
            if (parameter == null || clip is not PatternCore core) return null;
            core.InsertColumn(clip.Columns.Count, parameter, key.Track);
            return All(clip, key).FirstOrDefault();
        }

        /// <summary>One change: <paramref name="take"/> go out of every lane that holds them, <paramref name="put"/> come into the home lane.</summary>
        public static void Change(IPattern clip, ColumnKey key, IReadOnlyCollection<PatternEvent> take, IReadOnlyCollection<PatternEvent> put)
        {
            if (take.Count > 0)
                foreach (var lane in All(clip, key))
                {
                    var held = lane.GetEvents(int.MinValue, int.MaxValue).ToHashSet();
                    var gone = take.Where(held.Contains).ToArray();
                    if (gone.Length > 0) lane.SetEvents(gone, false);
                }
            if (put.Count > 0 && Home(clip, key, create: true) is { } home) home.SetEvents(put, true);
        }
    }
}
