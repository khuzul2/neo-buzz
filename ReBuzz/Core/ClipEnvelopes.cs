// Fork (docs/specs/daw-modes.md EV1, §3.4, §8.5): the envelopes of one clip. A clip's envelope is one state parameter of the clip's
// machine, kept as an engine automation column of the clip's clip: the store the arrangement lanes (AR4), the Session View's
// recording and the agent use. Here it is read and edited in the clip's own time (the Detail panel's lane, the curves drawn on the
// clips, the agent's clip_envelope), with AR4's reader and edit actions: a clip shown in the Session and the same clip placed in the
// arrangement hold the same points, play the same sound, and a point edited in either place is a step of the song's history.
//
// The clip's clip editor columns (the Tracker's) show read only, as AR4 shows them, and are marked where they overlap the clip's
// own points (AutomationReader.OverlapsOf). Nothing here runs on the audio thread.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core
{
    /// <summary>One parameter of one clip as the lane shows it: the engine column's points (editable) and the Tracker's steps (read only).</summary>
    internal sealed class ClipEnvelope
    {
        public IClip Clip { get; init; }
        public AutoParam Param { get; init; }
        public ColumnKey Key { get; init; }

        /// <summary>The clip's engine column, as one placement from its start to its end; its points are in the clip's time (1/240 tick).</summary>
        public AutoSegment Engine { get; init; }

        /// <summary>The clip editor's parameter column as steps (read only), or null when it holds none.</summary>
        public AutoSegment Tracker { get; init; }

        /// <summary>Where the engine column and the Tracker's column set the parameter in one place (clip time).</summary>
        public IReadOnlyList<AutoOverlap> Overlaps { get; init; } = Array.Empty<AutoOverlap>();

        public IReadOnlyList<AutoPoint> Points => Engine.Points;

        /// <summary>The clip's length in 1/240 ticks.</summary>
        public int Length => Engine.End;

        public bool HasData => Engine.Points.Count > 0 || Tracker != null;

        /// <summary>
        /// T2: this envelope is the tracker's own lane (the unmarked one a parameter column shows, read with its ramps and edited in place):
        /// <see cref="Engine"/> holds its points, <see cref="Tracker"/> is null, and an edit goes to that lane, never to the marked one.
        /// </summary>
        public bool OwnLane { get; init; }
    }

    /// <summary>Reads the envelopes of a clip, and finds which parameters hold data.</summary>
    internal static class ClipEnvelopes
    {
        public const int Unit = ClipEvent.TimeBase;

        /// <summary>A clip's length in 1/240 ticks (at least one tick).</summary>
        public static int LengthOf(IClip clip) => Math.Max(1, clip?.Length ?? 1) * Unit;

        static AutoSegment Segment(IClip clip, AutoSource source, IReadOnlyList<AutoPoint> points, bool editable) => new AutoSegment
        {
            Source = source, Clip = clip, Start = 0, End = LengthOf(clip), ClipFrom = 0, Points = points, Editable = editable,
        };

        /// <summary>One parameter of a clip, read now; null when the clip's machine has no such state parameter.</summary>
        public static ClipEnvelope Read(IClip clip, ColumnKey key, Dictionary<IClip, List<(int, int, IParameter, int, ClipEvent[])>> cache = null)
        {
            var machine = clip?.Machine;
            var param = AutomationParams.Find(machine, key);
            if (param == null) return null;
            var points = new List<AutoPoint>();
            foreach (var column in AutomationReader.EngineColumns(clip, param.Parameter, key.Track))
                points = AutomationMath.PointsOf(column.GetEvents(int.MinValue, int.MaxValue), true);
            var engine = Segment(clip, AutoSource.ClipColumn, points, true);
            AutoSegment tracker = null;
            cache ??= new Dictionary<IClip, List<(int, int, IParameter, int, ClipEvent[])>>();
            foreach (var t in AutomationReader.TrackerColumns(cache, clip))
            {
                if (!AutomationReader.Same(machine, t, key) || t.Events.All(x => x.Value == param.Parameter.NoValue)) continue;
                tracker = Segment(clip, AutoSource.Tracker, AutomationMath.PointsOf(t.Events, ClipColumnCore.PlaysEveryLane, param.Parameter.NoValue), false);
                break;
            }
            var overlaps = tracker != null && points.Count > 0 ? AutomationReader.OverlapsOf(new[] { engine, tracker }) : new List<AutoOverlap>();
            return new ClipEnvelope { Clip = clip, Param = param, Key = key, Engine = engine, Tracker = tracker, Overlaps = overlaps };
        }

        /// <summary>
        /// T2: the tracker's own lane of one parameter of a clip, read now (the points of the unmarked lane its column shows, a Duration above 0 a
        /// ramp, as the engine plays it); null when the clip's machine has no such state parameter. No lane yet reads as no points.
        /// </summary>
        public static ClipEnvelope ReadOwn(IClip clip, ColumnKey key)
        {
            var param = AutomationParams.Find(clip?.Machine, key);
            if (param == null) return null;
            var lane = Notes.StoreLanes.OwnLane(clip, key, create: false);
            var points = AutomationMath.PointsOf(lane?.GetEvents(int.MinValue, int.MaxValue) ?? Array.Empty<ClipEvent>(), true, param.Parameter.NoValue);
            return new ClipEnvelope { Clip = clip, Param = param, Key = key, Engine = Segment(clip, AutoSource.Tracker, points, true), OwnLane = true };
        }

        /// <summary>The parameters that hold data in a clip: its engine columns first, then the clip editor's, each in the machine's parameter order.</summary>
        public static List<ColumnKey> KeysWithData(IClip clip)
        {
            var found = new List<ColumnKey>();
            var machine = clip?.Machine;
            if (machine == null) return found;
            var groups = machine.ParameterGroups;
            foreach (var column in clip.Columns)
            {
                if (column.Type != ClipColumnType.Parameter || column.Parameter?.Group == null || column is not ClipColumnCore { IsMarkedAutomation: true }) continue;
                int g = IndexOfGroup(groups, column.Parameter.Group);
                if (g < 0 || !column.GetEvents(int.MinValue, int.MaxValue).Any()) continue;
                var key = new ColumnKey(g, column.Parameter.IndexInGroup, column.Track);
                if (AutomationParams.Find(machine, key) != null && !found.Contains(key)) found.Add(key);
            }
            found.Sort(Compare);
            var tracker = new List<ColumnKey>();
            var cache = new Dictionary<IClip, List<(int, int, IParameter, int, ClipEvent[])>>();
            foreach (var t in AutomationReader.TrackerColumns(cache, clip))
            {
                var key = new ColumnKey(t.Group, t.Index, t.Order);
                if (found.Contains(key) || tracker.Contains(key) || AutomationParams.Find(machine, key) is not { } p || t.Events.All(x => x.Value == p.Parameter.NoValue)) continue;
                tracker.Add(key);
            }
            tracker.Sort(Compare);
            found.AddRange(tracker);
            return found;
        }

        static int IndexOfGroup(IReadOnlyList<IParameterGroup> groups, IParameterGroup group)
        {
            for (int i = 0; i < groups.Count; i++)
                if (ReferenceEquals(groups[i], group)) return i;
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].Type == group.Type) return i;
            return -1;
        }

        static int Compare(ColumnKey a, ColumnKey b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Track.CompareTo(b.Track);

        /// <summary>The parameter a track shows on its clips by default: the first (in the machine's parameter order) that holds data in any of its clips; null if none does.</summary>
        public static ColumnKey? DefaultKey(IMachine machine)
        {
            ColumnKey? best = null;
            if (machine == null) return null;
            foreach (var clip in machine.Clips)
                foreach (var key in KeysWithData(clip))
                    if (best == null || Compare(key, best.Value) < 0) best = key;
            return best;
        }

        /// <summary>The parameter a track's clips draw: the choice saved with the song, else the default.</summary>
        public static ColumnKey? ChoiceOf(ReBuzzCore core, IMachine machine)
        {
            if (machine == null) return null;
            if (core.Arrangement?.EnvelopeChoice(machine.Name) is { } chosen && AutomationParams.Find(machine, chosen) != null) return chosen;
            return DefaultKey(machine);
        }

        /// <summary>The curve a clip draws for a parameter: its engine points, else the Tracker's steps (marked); empty when it holds none.</summary>
        public static (IReadOnlyList<AutoPoint> Points, bool FromTracker) CurveOf(IClip clip, ColumnKey key)
        {
            var e = Read(clip, key);
            if (e == null) return (Array.Empty<AutoPoint>(), false);
            if (e.Engine.Points.Count > 0) return (e.Engine.Points, false);
            return e.Tracker != null ? (e.Tracker.Points, true) : (Array.Empty<AutoPoint>(), false);
        }
    }

    /// <summary>
    /// The edits of a clip's envelope, each one step of the song's history (AutomationEditor.Edit: a ColumnEventsAction that takes out and
    /// puts in only the events that change). Each returns null when made, else why not, in words. Times are in the clip's time (1/240 tick).
    /// </summary>
    internal sealed class ClipEnvelopeEditor
    {
        readonly AutomationEditor editor;
        readonly AutomationEditor own;

        public ClipEnvelopeEditor(ReBuzzCore core)
        {
            var clips = new ClipEditor(core);
            editor = new AutomationEditor(core, clips);
            own = new AutomationEditor(core, clips, own: true);
        }

        public ClipEnvelopeEditor(AutomationEditor editor) => this.editor = editor;

        static int Last(ClipEnvelope e) => e.Length - 1;

        /// <summary>Changes the clip's points with <paramref name="change"/>; the points it returns are kept inside the clip.</summary>
        public string Edit(ClipEnvelope e, Func<List<AutoPoint>, List<AutoPoint>> change) =>
            (e.OwnLane ? own ?? throw new InvalidOperationException("This editor has no tracker lane.") : editor).Edit(e.Engine, e.Key, old =>
            {
                var result = change(old);
                return result.Select(p => p with { Time = Math.Clamp(p.Time, 0, Last(e)), Value = Math.Clamp(p.Value, e.Param.Min, e.Param.Max) })
                    .GroupBy(p => p.Time).Select(g => g.Last()).OrderBy(p => p.Time).ToList();
            });

        /// <summary>A point at a time of the clip (a point already there takes the new value).</summary>
        public string AddPoint(ClipEnvelope e, int time, int value, bool ramp = true) =>
            Edit(e, pts => pts.Where(p => p.Time != Math.Clamp(time, 0, Last(e))).Append(new AutoPoint(time, value, ramp)).ToList());

        /// <summary>The points at these times moved by a time and a value; a point that lands on another one's time takes its place.</summary>
        public string Move(ClipEnvelope e, IReadOnlyCollection<int> times, int deltaTime, int deltaValue)
        {
            if (times.Count == 0 || deltaTime == 0 && deltaValue == 0) return null;
            var set = times.ToHashSet();
            if (!set.Overlaps(e.Points.Select(p => p.Time))) return "That point is gone.";
            // The shift is kept so that no selected point leaves the clip.
            int lo = set.Min(), hi = set.Max();
            deltaTime = Math.Clamp(deltaTime, -lo, Last(e) - hi);
            return Edit(e, pts =>
            {
                var moved = pts.Where(p => set.Contains(p.Time)).Select(p => p with { Time = p.Time + deltaTime, Value = Math.Clamp(p.Value + deltaValue, e.Param.Min, e.Param.Max) }).ToList();
                var landed = moved.Select(p => p.Time).ToHashSet();
                return pts.Where(p => !set.Contains(p.Time) && !landed.Contains(p.Time)).Concat(moved).ToList();
            });
        }

        public string Delete(ClipEnvelope e, IReadOnlyCollection<int> times)
        {
            if (times.Count == 0) return null;
            var set = times.ToHashSet();
            if (!set.Overlaps(e.Points.Select(p => p.Time))) return "That point is gone.";
            return Edit(e, pts => pts.Where(p => !set.Contains(p.Time)).ToList());
        }

        /// <summary>The line after each of these points a ramp (straight to the next point) or a step (it holds).</summary>
        public string SetRamp(ClipEnvelope e, IReadOnlyCollection<int> times, bool ramp)
        {
            if (times.Count == 0) return null;
            var set = times.ToHashSet();
            if (!set.Overlaps(e.Points.Select(p => p.Time))) return "That point is gone.";
            return Edit(e, pts => pts.Select(p => set.Contains(p.Time) ? p with { Ramp = ramp } : p).ToList());
        }

        /// <summary>Puts points in the clip, each at <paramref name="at"/> plus its time; a point on another's time takes its place.</summary>
        public string Paste(ClipEnvelope e, IReadOnlyList<AutoPoint> points, int at)
        {
            if (points.Count == 0) return null;
            int shift = Math.Clamp(at, 0, Last(e)) - points.Min(p => p.Time);
            var landed = points.Select(p => p.Time + shift).Where(t => t >= 0 && t <= Last(e)).ToHashSet();
            if (landed.Count == 0) return "That is outside the clip.";
            return Edit(e, pts => pts.Where(p => !landed.Contains(p.Time)).Concat(points.Select(p => p with { Time = p.Time + shift }).Where(p => p.Time <= Last(e))).ToList());
        }

        /// <summary>The whole envelope replaced by these points (the agent's write with replace, and clear).</summary>
        public string Replace(ClipEnvelope e, IReadOnlyList<AutoPoint> points) => Edit(e, _ => points.ToList());
    }
}
