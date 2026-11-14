// Fork (docs/specs/daw-modes.md AR4, §3.4, §4.3): automation lanes of the arrangement, as the views and
// the agent read and edit them. A lane is one state parameter of a machine (a global one, or one track of a
// track parameter). What writes that parameter along the timeline is read as segments:
//   - automation clips: patterns on an automation-role lane (a sequence of the machine) whose engine
//     automation column holds the parameter;
//   - clip automation: the same engine columns in the patterns of the machine's other lanes, stitched along
//     the timeline wherever those patterns play (each repeat of a looped clip shows again);
//   - tracker columns: the pattern editor's parameter columns, as steps; read only.
// Points are the column's events: a point whose Duration is above 0 is a ramp (a straight line to the next
// point, S3), any other is a step. Editing is a ColumnEventsAction on the song's history, so undo and redo are
// the song's. Overlaps (two sources writing one parameter in one place) are found, never hidden.
// Nothing here runs on the audio thread; the engine plays the columns as it did since S3.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;

namespace ReBuzz.Core
{
    /// <summary>A state parameter an automation lane can show: the machine, which parameter (and track), its range.</summary>
    internal sealed record AutoParam(IMachine Machine, ColumnKey Key, IParameter Parameter, string Name)
    {
        public int Min => Parameter.MinValue;
        public int Max => Parameter.MaxValue;
        public string Describe(int value) => Parameter.DescribeValue(value);

        /// <summary>Where a value sits in the range, 0 (lowest) to 1.</summary>
        public double Fraction(int value) => Max > Min ? Math.Clamp((value - Min) / (double)(Max - Min), 0, 1) : 0;

        public int ValueAt(double fraction) => (int)Math.Round(Min + Math.Clamp(fraction, 0, 1) * (Max - Min));
    }

    /// <summary>The parameters of a machine automation can move: its state parameters (a track parameter once for each track).</summary>
    internal static class AutomationParams
    {
        public static List<AutoParam> List(IMachine machine)
        {
            var list = new List<AutoParam>();
            if (machine == null) return list;
            var groups = machine.ParameterGroups;
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                if (group.Type == ParameterGroupType.Input) continue;
                int tracks = group.Type == ParameterGroupType.Track ? Math.Max(0, group.TrackCount) : 1;
                for (int t = 0; t < tracks; t++)
                    for (int i = 0; i < group.Parameters.Count; i++)
                    {
                        var p = group.Parameters[i];
                        if (!p.Flags.HasFlag(ParameterFlags.State) || p.Type is ParameterType.Note or ParameterType.Internal || p.MaxValue <= p.MinValue) continue;
                        list.Add(new AutoParam(machine, new ColumnKey(g, i, t), p, group.Type == ParameterGroupType.Track ? $"{p.Name} (track {t + 1})" : p.Name));
                    }
            }
            return list;
        }

        public static AutoParam Find(IMachine machine, ColumnKey key)
        {
            if (machine == null) return null;
            var groups = machine.ParameterGroups;
            if (key.Group < 0 || key.Group >= groups.Count || key.Index < 0 || key.Index >= groups[key.Group].Parameters.Count) return null;
            var group = groups[key.Group];
            if (group.Type == ParameterGroupType.Input || key.Track < 0 || (group.Type == ParameterGroupType.Track ? key.Track >= group.TrackCount : key.Track != 0)) return null;
            var p = group.Parameters[key.Index];
            if (!p.Flags.HasFlag(ParameterFlags.State) || p.MaxValue <= p.MinValue) return null;
            return new AutoParam(machine, key, p, group.Type == ParameterGroupType.Track ? $"{p.Name} (track {key.Track + 1})" : p.Name);
        }
    }

    internal enum AutoSource { AutomationClip, ClipColumn, Tracker }

    /// <summary>One point of a curve: a time in pattern time (1/240 tick), a value, and whether the line goes on to the next point (a ramp) or holds (a step).</summary>
    internal readonly record struct AutoPoint(int Time, int Value, bool Ramp);

    /// <summary>
    /// One writer of a parameter along the timeline: a clip's column as one placement plays it. Times of the
    /// points are the pattern's (1/240 tick); <see cref="Start"/> and <see cref="End"/> are the placement's
    /// in song time (1/240 tick); <see cref="PatternFrom"/> is the pattern time that plays at Start.
    /// </summary>
    internal sealed class AutoSegment
    {
        public AutoSource Source { get; init; }
        public ISequence Sequence { get; init; }
        public int EventTime { get; init; }
        public IPattern Pattern { get; init; }
        public int Start { get; init; }
        public int End { get; init; }
        public int PatternFrom { get; init; }
        public IReadOnlyList<AutoPoint> Points { get; init; } = Array.Empty<AutoPoint>();
        /// <summary>Whether the points can be edited here: engine columns of the parameter's own machine (tracker columns are the pattern editor's).</summary>
        public bool Editable { get; init; }

        public int PatternTo => PatternFrom + (End - Start);

        /// <summary>A pattern time in song time.</summary>
        public int ToSong(int patternTime) => Start + (patternTime - PatternFrom);

        public int ToPattern(int songTime) => PatternFrom + (songTime - Start);

        public bool Plays(int patternTime) => patternTime >= PatternFrom && patternTime < PatternTo;

        public bool Covers(int songTime) => songTime >= Start && songTime < End;

        public string Describe => Source switch
        {
            AutoSource.AutomationClip => $"automation clip {Pattern?.Name}",
            AutoSource.ClipColumn => $"clip {Pattern?.Name}",
            _ => $"tracker, pattern {Pattern?.Name}",
        };

        /// <summary>The value this writer gives at a song time: the last point at or before it, a ramp followed along its line; null before the first point or outside the placement.</summary>
        public int? ValueAt(int songTime) => Covers(songTime) ? AutomationMath.ValueAt(Points, ToPattern(songTime)) : null;

        /// <summary>The song times (1/240 tick) this writer sets the parameter in: each step for a tick, each ramp over its run.</summary>
        public List<(int From, int To)> Writes()
        {
            var list = new List<(int, int)>();
            for (int i = 0; i < Points.Count; i++)
            {
                var p = Points[i];
                if (!Plays(p.Time)) continue;
                int from = ToSong(p.Time), to = from + PatternEvent.TimeBase;
                if (p.Ramp && i + 1 < Points.Count) to = Math.Min(End, Math.Max(to, ToSong(Points[i + 1].Time)));
                list.Add((from, Math.Min(End, to)));
            }
            return list;
        }
    }

    /// <summary>Two writers of one parameter in one place: where (song time, 1/240 tick) and who.</summary>
    internal sealed record AutoOverlap(int From, int To, AutoSegment A, AutoSegment B);

    /// <summary>A lane read: what writes it along the timeline and where those writers overlap.</summary>
    internal sealed class AutoLane
    {
        public AutoParam Param { get; init; }
        public List<AutoSegment> Segments { get; init; } = new();
        public List<AutoOverlap> Overlaps { get; init; } = new();
    }

    internal static class AutomationMath
    {
        /// <summary>The value on the line from (t0, v0) to (t1, v1) at t, rounded to the nearest whole value, halves away from zero: as the engine rounds a ramp (PatternColumnCore.TryRampValue).</summary>
        public static int Ramp(int t0, int v0, int t1, int v1, int t)
        {
            long span = (long)t1 - t0;
            long rise = (long)(v1 - v0) * (t - t0);
            return v0 + (int)(rise >= 0 ? (rise + span / 2) / span : -((-rise + span / 2) / span));
        }

        /// <summary>The value of a curve at a pattern time, as the engine sets it: null before its first point.</summary>
        public static int? ValueAt(IReadOnlyList<AutoPoint> points, int time)
        {
            int at = -1;
            for (int i = 0; i < points.Count; i++)
                if (points[i].Time <= time) at = i;
                else break;
            if (at < 0) return null;
            var p = points[at];
            if (p.Ramp && p.Time != time && at + 1 < points.Count && points[at + 1].Time > p.Time)
                return Ramp(p.Time, p.Value, points[at + 1].Time, points[at + 1].Value, time);
            return p.Value;
        }

        /// <summary>A column's events as points, in time order; of two at one time the later in the column's order is the one that sets the value.</summary>
        public static List<AutoPoint> PointsOf(IEnumerable<PatternEvent> events, bool ramps, int noValue = int.MinValue)
        {
            var byTime = new SortedDictionary<int, AutoPoint>();
            foreach (var e in events)
            {
                if (e.Value == noValue) continue;
                byTime[e.Time] = new AutoPoint(e.Time, e.Value, ramps && e.Duration > 0);
            }
            return byTime.Values.ToList();
        }
    }

    /// <summary>Reads the lanes of a song.</summary>
    internal static class AutomationReader
    {
        public static AutoLane Read(ReBuzzCore core, IMachine machine, ColumnKey key) => ReadAll(core, new[] { (machine, key) })[0];

        public static List<AutoLane> ReadAll(ReBuzzCore core, IEnumerable<(IMachine Machine, ColumnKey Key)> lanes)
        {
            var result = new List<AutoLane>();
            var song = core.Song;
            var sequences = song.Sequences.ToList();
            var tracker = new Dictionary<IPattern, List<(int Group, int Index, IParameter Parameter, int Order, PatternEvent[] Events)>>();
            foreach (var (machine, key) in lanes)
            {
                var param = AutomationParams.Find(machine, key);
                var lane = new AutoLane { Param = param };
                result.Add(lane);
                if (param == null) continue;
                foreach (var seq in sequences)
                {
                    var role = core.Arrangement?.RoleOf(seq) ?? LaneRole.Main;
                    foreach (var kv in seq.Events.OrderBy(e => e.Key))
                    {
                        var e = kv.Value;
                        if (e.Type != SequenceEventType.PlayPattern || e.Pattern == null) continue;
                        int span = Math.Max(1, Math.Min(Math.Max(1, e.Span), Math.Max(1, e.PlayedLength)));
                        int start = kv.Key * PatternEvent.TimeBase, end = start + span * PatternEvent.TimeBase;
                        int from = e.PlayedOffset * PatternEvent.TimeBase;
                        bool found = false;
                        foreach (var column in e.Pattern.Columns)
                        {
                            if (column.Type != PatternColumnType.Parameter || column.Track != key.Track || !ReferenceEquals(column.Parameter, param.Parameter)) continue;
                            if (column is not PatternColumnCore engine || !engine.EnginePlays) continue;
                            var events = column.GetEvents(int.MinValue, int.MaxValue);
                            if (!events.Any()) continue;
                            found = true;
                            lane.Segments.Add(new AutoSegment
                            {
                                Source = role == LaneRole.Automation ? AutoSource.AutomationClip : AutoSource.ClipColumn,
                                Sequence = seq, EventTime = kv.Key, Pattern = e.Pattern, Start = start, End = end, PatternFrom = from,
                                Points = AutomationMath.PointsOf(events, true),
                                Editable = ReferenceEquals(e.Pattern.Machine, machine),
                            });
                        }
                        // An automation clip with no point yet is still a place a point can be put (recording and the mouse add the first).
                        if (!found && role == LaneRole.Automation && ReferenceEquals(e.Pattern.Machine, machine))
                            lane.Segments.Add(new AutoSegment { Source = AutoSource.AutomationClip, Sequence = seq, EventTime = kv.Key, Pattern = e.Pattern, Start = start, End = end, PatternFrom = from, Editable = true });
                        if (role == LaneRole.Automation || !ReferenceEquals(seq.Machine, machine)) continue;
                        foreach (var t in TrackerColumns(tracker, e.Pattern))
                        {
                            if (!Same(machine, t, key) || t.Events.All(x => x.Value == param.Parameter.NoValue)) continue;
                            lane.Segments.Add(new AutoSegment
                            {
                                Source = AutoSource.Tracker, Sequence = seq, EventTime = kv.Key, Pattern = e.Pattern, Start = start, End = end, PatternFrom = from,
                                Points = AutomationMath.PointsOf(t.Events, false, param.Parameter.NoValue), Editable = false,
                            });
                        }
                    }
                }
                lane.Overlaps.AddRange(OverlapsOf(lane.Segments));
            }
            return result;
        }

        static bool Same(IMachine machine, (int Group, int Index, IParameter Parameter, int Order, PatternEvent[] Events) t, ColumnKey key) =>
            t.Group == key.Group && t.Index == key.Index && t.Order == key.Track;

        /// <summary>The pattern editor's parameter columns: which parameter (group, index) and, for a track parameter, which track (the columns of one parameter come in track order).</summary>
        static List<(int Group, int Index, IParameter Parameter, int Order, PatternEvent[] Events)> TrackerColumns(
            Dictionary<IPattern, List<(int, int, IParameter, int, PatternEvent[])>> cache, IPattern pattern)
        {
            if (cache.TryGetValue(pattern, out var known)) return known;
            var list = new List<(int, int, IParameter, int, PatternEvent[])>();
            try
            {
                var counts = new Dictionary<(int, int), int>();
                var groups = pattern.Machine?.ParameterGroups;
                foreach (var c in pattern.PatternEditorMachineEvents ?? Enumerable.Empty<IPatternEditorColumn>())
                {
                    var p = c.Parameter;
                    if (p?.Group == null || groups == null) continue;
                    int g = -1;
                    for (int i = 0; i < groups.Count && g < 0; i++)
                        if (ReferenceEquals(groups[i], p.Group)) g = i;
                    if (g < 0)
                        for (int i = 0; i < groups.Count && g < 0; i++)
                            if (groups[i].Type == p.Group.Type) g = i;
                    if (g < 0) continue;
                    var k = (g, p.IndexInGroup);
                    counts.TryGetValue(k, out int order);
                    counts[k] = order + 1;
                    list.Add((g, p.IndexInGroup, p, order, c.Events.ToArray()));
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NullReferenceException || ex is KeyNotFoundException)
            {
                // A pattern editor that can't be read shows nothing.
            }
            cache[pattern] = list;
            return list;
        }

        /// <summary>Where two writers of one parameter set it in the same place (steps: a tick; ramps: their whole run).</summary>
        public static List<AutoOverlap> OverlapsOf(IReadOnlyList<AutoSegment> segments)
        {
            var found = new List<AutoOverlap>();
            var writes = segments.Select(s => s.Writes()).ToList();
            for (int a = 0; a < segments.Count; a++)
                for (int b = a + 1; b < segments.Count; b++)
                {
                    if (segments[a].End <= segments[b].Start || segments[b].End <= segments[a].Start) continue;
                    int from = int.MinValue, to = int.MinValue;
                    foreach (var (a0, a1) in writes[a])
                        foreach (var (b0, b1) in writes[b])
                        {
                            int f = Math.Max(a0, b0), t = Math.Min(a1, b1);
                            if (f >= t) continue;
                            if (from != int.MinValue && f <= to) { to = Math.Max(to, t); continue; }
                            if (from != int.MinValue) found.Add(new AutoOverlap(from, to, segments[a], segments[b]));
                            from = f; to = t;
                        }
                    if (from != int.MinValue) found.Add(new AutoOverlap(from, to, segments[a], segments[b]));
                }
            return found.OrderBy(o => o.From).ToList();
        }
    }

    /// <summary>An automation lane (a sequence with the automation role) added to a machine, on the song's history.</summary>
    [HistoryName("Add automation lane")]
    internal sealed class AddAutomationLaneAction : IAction
    {
        readonly ReBuzzCore core;
        readonly string machine;
        int index = -1;

        public AddAutomationLaneAction(ReBuzzCore core, IMachine machine)
        {
            this.core = core;
            this.machine = machine.Name;
        }

        public ISequence Lane { get; private set; }

        public void Do()
        {
            var m = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == machine);
            if (m == null) return;
            index = core.SongCore.Sequences.Count;
            core.SongCore.AddSequence(m, index);
            Lane = core.SongCore.Sequences[index];
            core.Arrangement?.SetRole(Lane, LaneRole.Automation);
        }

        public void Undo()
        {
            if (index < 0 || index >= core.SongCore.Sequences.Count) return;
            core.SongCore.RemoveSequence(core.SongCore.Sequences[index]);
            Lane = null;
        }
    }

    /// <summary>Edits of automation: points of a lane's clips, and clips drawn for it. Every edit is one step of the song's history; each returns null when made, else why not, in words.</summary>
    internal sealed class AutomationEditor
    {
        readonly ReBuzzCore core;
        readonly ClipEditor clips;

        public AutomationEditor(ReBuzzCore core, ClipEditor clips)
        {
            this.core = core;
            this.clips = clips;
        }

        /// <summary>The 1/240-tick grid points live on.</summary>
        public const int Unit = PatternEvent.TimeBase;

        static PatternEvent Event(AutoPoint p, IReadOnlyList<AutoPoint> all)
        {
            int duration = 0;
            if (p.Ramp)
            {
                var next = all.FirstOrDefault(q => q.Time > p.Time);
                duration = next.Time > p.Time ? next.Time - p.Time : 1;
            }
            return new PatternEvent(p.Time, p.Value, duration);
        }

        /// <summary>The column's events now, in time order.</summary>
        static PatternEvent[] Current(IPattern pattern, ColumnKey key)
        {
            var column = SessionLayout.ColumnFor(pattern, key, create: false);
            return column == null ? Array.Empty<PatternEvent>() : column.GetEvents(int.MinValue, int.MaxValue).OrderBy(e => e.Time).ToArray();
        }

        static string Check(AutoSegment seg)
        {
            if (seg == null) return "There is nothing to edit here.";
            if (!seg.Editable) return seg.Source == AutoSource.Tracker ? "These steps are the tracker's: edit them in the Pattern tab." : "This curve can't be edited from here.";
            return null;
        }

        void Do(IPattern pattern, ColumnKey key, PatternEvent[] removed, PatternEvent[] added)
        {
            if (removed.Length == 0 && added.Length == 0) return;
            core.SongCore.ActionStack.Do(new ColumnEventsAction(core, pattern, new[] { new ColumnEventsAction.Change(key, removed, added) }));
            core.SetModifiedFlag();
        }

        /// <summary>Writes a point at a song time (a new one, or the one already there with a new value). Times are kept inside what the placement plays.</summary>
        public string AddPoint(AutoSegment seg, ColumnKey key, int songTime, int value, bool ramp = true)
        {
            if (Check(seg) is { } why) return why;
            var param = AutomationParams.Find(seg.Pattern.Machine, key);
            if (param == null) return "That parameter can't be automated.";
            int t = Math.Clamp(seg.ToPattern(songTime), seg.PatternFrom, Math.Min(seg.PatternTo, seg.Pattern.Length * Unit) - 1);
            if (t < 0 || t >= seg.Pattern.Length * Unit) return "That is outside the pattern.";
            value = Math.Clamp(value, param.Min, param.Max);
            var now = Current(seg.Pattern, key);
            var points = AutomationMath.PointsOf(now, true).Where(p => p.Time != t).ToList();
            var point = new AutoPoint(t, value, ramp);
            points.Add(point);
            points.Sort((a, b) => a.Time.CompareTo(b.Time));
            Replace(seg.Pattern, key, now, points);
            return null;
        }

        /// <summary>Moves point <paramref name="index"/> (of the placement's points, in time order) to a song time and value, between its neighbours.</summary>
        public string MovePoint(AutoSegment seg, ColumnKey key, int index, int songTime, int value)
        {
            if (Check(seg) is { } why) return why;
            var now = Current(seg.Pattern, key);
            var points = AutomationMath.PointsOf(now, true);
            if (index < 0 || index >= points.Count) return "That point is gone.";
            var param = AutomationParams.Find(seg.Pattern.Machine, key);
            if (param == null) return "That parameter can't be automated.";
            int lo = index > 0 ? points[index - 1].Time + 1 : 0;
            int hi = index + 1 < points.Count ? points[index + 1].Time - 1 : seg.Pattern.Length * Unit - 1;
            int t = Math.Clamp(seg.ToPattern(songTime), lo, Math.Max(lo, hi));
            points[index] = points[index] with { Time = t, Value = Math.Clamp(value, param.Min, param.Max) };
            Replace(seg.Pattern, key, now, points);
            return null;
        }

        public string DeletePoint(AutoSegment seg, ColumnKey key, int index)
        {
            if (Check(seg) is { } why) return why;
            var now = Current(seg.Pattern, key);
            var points = AutomationMath.PointsOf(now, true);
            if (index < 0 || index >= points.Count) return "That point is gone.";
            points.RemoveAt(index);
            Replace(seg.Pattern, key, now, points);
            return null;
        }

        /// <summary>Makes the line after a point a ramp (straight to the next point) or a step (it holds).</summary>
        public string SetRamp(AutoSegment seg, ColumnKey key, int index, bool ramp)
        {
            if (Check(seg) is { } why) return why;
            var now = Current(seg.Pattern, key);
            var points = AutomationMath.PointsOf(now, true);
            if (index < 0 || index >= points.Count) return "That point is gone.";
            points[index] = points[index] with { Ramp = ramp };
            Replace(seg.Pattern, key, now, points);
            return null;
        }

        /// <summary>Changes a placement's points with <paramref name="change"/> (it gets the points in pattern time and returns the ones to keep): one history step.</summary>
        public string Edit(AutoSegment seg, ColumnKey key, Func<List<AutoPoint>, List<AutoPoint>> change)
        {
            if (Check(seg) is { } why) return why;
            var now = Current(seg.Pattern, key);
            var points = change(AutomationMath.PointsOf(now, true)).OrderBy(p => p.Time).ToList();
            Replace(seg.Pattern, key, now, points);
            return null;
        }

        /// <summary>Puts the points in the column: only the events that differ are taken out and put in.</summary>
        void Replace(IPattern pattern, ColumnKey key, PatternEvent[] now, List<AutoPoint> points)
        {
            var want = points.Select(p => Event(p, points)).ToList();
            var removed = new List<PatternEvent>();
            var added = new List<PatternEvent>();
            var left = want.ToList();
            foreach (var e in now)
            {
                int at = left.FindIndex(w => w.Time == e.Time && w.Value == e.Value && (w.Duration > 0) == (e.Duration > 0));
                if (at >= 0) left.RemoveAt(at);
                else removed.Add(e);
            }
            added.AddRange(left);
            Do(pattern, key, removed.ToArray(), added.ToArray());
        }

        /// <summary>
        /// The lane (a sequence of the machine with the automation role) with room for a clip from <paramref name="start"/> to
        /// <paramref name="end"/> (song ticks): the first that is free there. Null if there is none; <see cref="Lane"/> adds one.
        /// </summary>
        public ISequence FreeLane(IMachine machine, int start, int end)
        {
            foreach (var seq in core.Song.Sequences.Where(s => ReferenceEquals(s.Machine, machine) && core.Arrangement?.RoleOf(s) == LaneRole.Automation))
                if (!clips.Read(seq).Any(i => i.Time < end && i.End > start)) return seq;
            return null;
        }

        /// <summary>
        /// Draws an automation clip for a parameter: a pattern of <paramref name="length"/> ticks on an automation lane (one is added
        /// when none is free), with a point at its start, and another at its end if the value differs (a ramp between them).
        /// One step of the history. <paramref name="made"/> is the clip's pattern.
        /// </summary>
        public string DrawClip(IMachine machine, ColumnKey key, int start, int length, int startValue, int endValue, out IPattern made, bool withPoints = true)
        {
            made = null;
            var param = AutomationParams.Find(machine, key);
            if (param == null) return "That parameter can't be automated.";
            start = Math.Max(0, start);
            length = Math.Max(1, length);
            string why = null;
            IPattern pattern = null;
            var stack = core.SongCore.ActionStack;
            stack.BeginActionGroup();
            try
            {
                stack.Label("Draw automation");
                var lane = FreeLane(machine, start, start + length);
                if (lane == null)
                {
                    var add = new AddAutomationLaneAction(core, machine);
                    stack.Do(add);
                    lane = add.Lane;
                }
                if (lane == null) { why = "There is no lane to draw on."; return why; }
                why = clips.Draw(lane, start, length, out pattern);
                if (pattern == null) return why ?? "The clip couldn't be made.";
                startValue = Math.Clamp(startValue, param.Min, param.Max);
                endValue = Math.Clamp(endValue, param.Min, param.Max);
                int last = pattern.Length * Unit - 1;
                var points = new List<AutoPoint> { new AutoPoint(0, startValue, endValue != startValue && last > 0) };
                if (endValue != startValue && last > 0) points.Add(new AutoPoint(last, endValue, false));
                if (withPoints) Do(pattern, key, Array.Empty<PatternEvent>(), points.Select(p => Event(p, points)).ToArray());
            }
            finally
            {
                stack.EndActionGroup();
            }
            made = pattern;
            return why;
        }
    }
}
