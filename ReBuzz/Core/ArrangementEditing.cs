// Fork (docs/specs/daw-modes.md AR1): arrangement clips as the views and the agent edit them. A
// clip is a run of PlayPattern events of one pattern on one sequence, joined by a clip id
// (ArrangementClips); here it is read as a ClipSpec (where it starts and how long it plays, the
// part of its pattern it starts with, its loop region, whether it loops) and written back as
// events: the first plays from the start, the next ones the loop region, the last is cut at the
// clip's end (Offset, End), so upstream ReBuzz plays the same events. Every edit is planned on
// copies of the touched sequences' events and applied as one ClipEditAction on the song's
// history; nothing here runs on the audio thread.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core
{
    /// <summary>A part of a clip's play: where it starts (song ticks), where in the pattern it starts, how long it plays.</summary>
    internal readonly record struct ClipSegment(int Time, int Offset, int Length);

    /// <summary>
    /// What a clip is, apart from its events: where it starts and how long it is (ticks), where in
    /// its pattern it starts, its loop region (LoopEnd 0 for the pattern's end, which is also where
    /// a clip without a loop ends), whether it loops, its volume. A clip that doesn't loop plays
    /// its region once; a looping one repeats the region (from LoopStart) up to Length.
    /// </summary>
    internal sealed record ClipSpec(int Id, IPattern Pattern, int Time, int Length, int Start, int LoopStart, int LoopEnd, bool Loop, double Gain)
    {
        public int PatternLength => Math.Max(1, Pattern.Length);
        public int RegionEnd => LoopEnd > 0 ? Math.Max(1, Math.Min(LoopEnd, PatternLength)) : PatternLength;
        public int End => Time + Length;
        public ClipLoop LoopRecord => new ClipLoop(LoopStart, LoopEnd, Loop);

        /// <summary>The most a clip without a loop can play: its region from its start.</summary>
        public int MaxLength => Loop ? int.MaxValue : Math.Max(1, RegionEnd - Math.Clamp(Start, 0, RegionEnd - 1));

        /// <summary>The parts it plays, in time order: what its events will be.</summary>
        public List<ClipSegment> Segments()
        {
            var list = new List<ClipSegment>();
            int regionEnd = RegionEnd;
            int offset = Math.Clamp(Start, 0, regionEnd - 1);
            int loopStart = Math.Clamp(LoopStart, 0, regionEnd - 1);
            int length = Math.Max(1, Loop ? Length : Math.Min(Length, regionEnd - offset));
            int t = Time, end = Time + length;
            while (t < end && list.Count < 8192)
            {
                int whole = regionEnd - offset;
                int len = Math.Min(whole, end - t);
                list.Add(new ClipSegment(t, offset, len));
                t += len;
                offset = loopStart;
                if (!Loop) break;
            }
            return list;
        }

        /// <summary>Where in the pattern the clip is at a song tick (null if it isn't playing then).</summary>
        public int? PatternPositionAt(int tick)
        {
            foreach (var s in Segments())
                if (tick >= s.Time && tick < s.Time + s.Length) return s.Offset + (tick - s.Time);
            return null;
        }

        /// <summary>The events that play it (the last cut with End where a part of the region plays).</summary>
        public List<(int Time, SequenceEvent Event)> Events(int id)
        {
            int plen = PatternLength;
            return Segments().Select(s =>
            {
                int played = s.Offset + s.Length;
                return (s.Time, new SequenceEvent(SequenceEventType.PlayPattern, Pattern) { Offset = s.Offset, End = played >= plen ? 0 : played, Gain = Gain, Clip = id });
            }).ToList();
        }

        /// <summary>The clip ends at <paramref name="tick"/> (it must start before it).</summary>
        public ClipSpec CutRight(int tick) => this with { Length = Math.Max(1, tick - Time) };

        /// <summary>The clip starts at <paramref name="tick"/>, what it plays then playing on (its end stays).</summary>
        public ClipSpec CutLeft(int tick)
        {
            int pos = PatternPositionAt(tick) ?? Math.Clamp(Start, 0, RegionEnd - 1);
            return this with { Time = tick, Length = Math.Max(1, End - tick), Start = pos };
        }
    }

    /// <summary>A clip in a sequence: its spec and the events that play it now.</summary>
    internal sealed class ClipItem
    {
        public ISequence Sequence { get; init; }
        public ClipSpec Spec { get; init; }
        public List<int> EventTimes { get; init; }
        /// <summary>What the events play now (their start, their part of the pattern, their span).</summary>
        public List<ClipSegment> Actual { get; init; }
        /// <summary>True for an event taken out of a run that isn't a clean loop: it is a clip of its own, as an event without an id is.</summary>
        public bool Single { get; init; }

        public int Time => Spec.Time;
        public int End => Spec.End;
        public IPattern Pattern => Spec.Pattern;

        // A clip is the same clip in a later reading while it starts with the same event of the same sequence.
        public override bool Equals(object obj) => obj is ClipItem o && ReferenceEquals(o.Sequence, Sequence) && o.EventTimes[0] == EventTimes[0];
        public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Sequence), EventTimes[0]);
    }

    /// <summary>The edits' own undo step: events set and removed on sequences, with the song's ends.</summary>
    [HistoryName("Edit clips")]
    internal sealed class ClipEditAction : IAction
    {
        internal readonly record struct Change(int Sequence, int Time, EventRef Before, EventRef After);

        readonly ISong song;
        readonly List<Change> changes;
        readonly (int LoopEnd, int SongEnd) before, after;

        public ClipEditAction(ISong song, List<Change> changes, (int, int) before, (int, int) after)
        {
            this.song = song;
            this.changes = changes;
            this.before = before;
            this.after = after;
        }

        public void Do() => Apply(true);

        public void Undo() => Apply(false);

        void Apply(bool forward)
        {
            var sequences = song.Sequences;
            foreach (var c in changes)
            {
                if (c.Sequence >= sequences.Count) continue;
                var seq = sequences[c.Sequence];
                if (seq.Events.ContainsKey(c.Time)) seq.Clear(c.Time, 1);
            }
            foreach (var c in changes)
            {
                if (c.Sequence >= sequences.Count) continue;
                (forward ? c.After : c.Before)?.Set(sequences[c.Sequence], c.Time);
            }
            var (loopEnd, songEnd) = forward ? after : before;
            if (loopEnd <= 0) return;
            if (songEnd >= song.SongEnd) { song.SongEnd = songEnd; song.LoopEnd = loopEnd; }
            else { song.LoopEnd = loopEnd; song.SongEnd = songEnd; }
        }
    }

    /// <summary>What a clip is copied as: its spec, taken apart from the song.</summary>
    internal sealed record ClipCopy(IPattern Pattern, int Length, int Start, int LoopStart, int LoopEnd, bool Loop, double Gain);

    /// <summary>
    /// Reads and edits the clips of a song. Edits return null when made, else why not, in words.
    /// Copies by the AR1 gestures get their own clip ids (daw-modes.md §8.1).
    /// </summary>
    internal sealed class ClipEditor
    {
        readonly ReBuzzCore core;

        public ClipEditor(ReBuzzCore core) => this.core = core;

        ISong Song => core.Song;
        ArrangementClips Clips => core.Clips;

        // ------------------------------------------------------------------ reading

        /// <summary>The clips of a sequence in time order; a run that isn't a clean loop is read event by event.</summary>
        public List<ClipItem> Read(ISequence seq)
        {
            var items = new List<ClipItem>();
            if (seq == null) return items;
            var events = seq.Events.OrderBy(e => e.Key).Select(e => (Time: e.Key, Event: e.Value)).ToList();
            foreach (var c in Clips.Of(seq))
            {
                var own = events.Where(e => e.Event.Type == SequenceEventType.PlayPattern && e.Event.Pattern != null && e.Time >= c.Time && e.Time < c.End).ToList();
                if (own.Count == 0) continue;
                var first = own[0].Event;
                var spec = new ClipSpec(c.Id, c.Pattern, c.Time, Math.Max(1, c.Length), first.PlayedOffset,
                    c.Loop?.Start ?? 0, c.Loop?.End ?? (first.End > 0 ? first.End : 0), c.Loop?.On == true, first.Gain);
                var actual = own.Select(e => new ClipSegment(e.Time, e.Event.PlayedOffset, Math.Max(1, e.Event.Span))).ToList();
                if (Matches(spec, actual, own.Select(e => e.Event.Gain)))
                {
                    items.Add(new ClipItem { Sequence = seq, Spec = spec, EventTimes = own.Select(e => e.Time).ToList(), Actual = actual });
                    continue;
                }
                foreach (var (time, e) in own)
                {
                    var one = new ClipSpec(0, e.Pattern, time, Math.Max(1, e.Span), e.PlayedOffset, 0, e.End > 0 ? e.End : 0, false, e.Gain);
                    items.Add(new ClipItem { Sequence = seq, Spec = one, EventTimes = new List<int> { time }, Actual = new List<ClipSegment> { new ClipSegment(time, e.PlayedOffset, Math.Max(1, e.Span)) }, Single = true });
                }
            }
            return items;
        }

        static bool Matches(ClipSpec spec, List<ClipSegment> actual, IEnumerable<double> gains)
        {
            var want = spec.Segments();
            if (want.Count != actual.Count) return false;
            for (int i = 0; i < want.Count; i++)
                if (want[i] != actual[i]) return false;
            return gains.All(g => g == spec.Gain);
        }

        /// <summary>Every place a pattern is used: its clips on the song's sequences and its slots in the session.</summary>
        public int Uses(IPattern pattern)
        {
            if (pattern?.Machine == null) return 0;
            int n = 0;
            foreach (var seq in Song.Sequences.Where(s => s.Machine == pattern.Machine))
                n += Read(seq).Count(i => ReferenceEquals(i.Pattern, pattern));
            var doc = core.SessionLayout?.Document;
            if (doc != null)
                foreach (var track in doc.Tracks)
                    n += track.Slots.Count(s => s.Value == pattern.Name && track.Machine == pattern.Machine.Name);
            return n;
        }

        public ClipCopy CopyOf(ClipItem item) =>
            new ClipCopy(item.Pattern, item.Spec.Length, item.Spec.Start, item.Spec.LoopStart, item.Spec.LoopEnd, item.Spec.Loop, item.Spec.Gain);

        // ------------------------------------------------------------------ planning

        /// <summary>The events of the touched sequences as the edit leaves them.</summary>
        sealed class Plan
        {
            readonly ClipEditor editor;
            public readonly Dictionary<ISequence, SortedDictionary<int, SequenceEvent>> Events = new();
            readonly Dictionary<ISequence, SortedDictionary<int, SequenceEvent>> original = new();
            readonly HashSet<ClipItem> removed = new();
            public int End;

            public Plan(ClipEditor editor) => this.editor = editor;

            SortedDictionary<int, SequenceEvent> Of(ISequence seq)
            {
                if (!Events.TryGetValue(seq, out var map))
                {
                    map = new SortedDictionary<int, SequenceEvent>();
                    var was = new SortedDictionary<int, SequenceEvent>();
                    foreach (var kv in seq.Events)
                    {
                        map[kv.Key] = new SequenceEvent(kv.Value);
                        was[kv.Key] = new SequenceEvent(kv.Value);
                    }
                    Events[seq] = map;
                    original[seq] = was;
                }
                return map;
            }

            public void Remove(ClipItem item)
            {
                if (!removed.Add(item)) return;
                var map = Of(item.Sequence);
                foreach (int t in item.EventTimes) map.Remove(t);
            }

            /// <summary>Writes a clip. It keeps its id while its loop is as the id says; otherwise (or when forced) it gets a new one.</summary>
            public ClipSpec Write(ISequence seq, ClipSpec spec, bool newId = false)
            {
                var map = Of(seq);
                int id = spec.Id;
                if (newId || id <= 0 || !editor.Clips.TryGetLoop(id, out var l) || l != spec.LoopRecord)
                {
                    id = editor.Clips.NewId();
                    editor.Clips.SetLoop(id, spec.LoopRecord);
                }
                spec = spec with { Id = id };
                foreach (var (time, e) in spec.Events(id)) map[time] = e;
                End = Math.Max(End, spec.Time + spec.Segments().Sum(s => s.Length));
                return spec;
            }

            /// <summary>Makes room: clips that cross the range are cut to what stays outside it, those inside go.</summary>
            public void Overwrite(ISequence seq, int from, int to)
            {
                if (to <= from) return;
                var map = Of(seq);
                foreach (var item in editor.Read(seq))
                {
                    if (removed.Contains(item) || item.End <= from || item.Time >= to) continue;
                    Remove(item);
                    if (item.Time < from) Write(seq, item.Spec.CutRight(from));
                    if (item.End > to) Write(seq, item.Spec.CutLeft(to), newId: item.Time < from);
                }
                foreach (int t in map.Keys.Where(t => t >= from && t < to && map[t].Type != SequenceEventType.PlayPattern).ToList()) map.Remove(t);
            }

            public ClipEditAction Build()
            {
                var changes = new List<ClipEditAction.Change>();
                var sequences = editor.Song.Sequences;
                foreach (var (seq, map) in Events)
                {
                    int index = sequences.IndexOf(seq);
                    var was = original[seq];
                    foreach (int t in was.Keys.Union(map.Keys).OrderBy(t => t))
                    {
                        was.TryGetValue(t, out var b);
                        map.TryGetValue(t, out var a);
                        if (Same(a, b)) continue;
                        changes.Add(new ClipEditAction.Change(index, t, b == null ? null : new EventRef(b), a == null ? null : new EventRef(a)));
                    }
                }
                var song = editor.Song;
                int loopEnd = song.LoopEnd, songEnd = song.SongEnd;
                int newEnd = editor.RoundUpToBar(End);
                bool loopIsSong = loopEnd == songEnd;
                int newSongEnd = Math.Max(songEnd, newEnd);
                return new ClipEditAction(song, changes, (loopEnd, songEnd), (loopIsSong ? newSongEnd : Math.Max(loopEnd, 0), newSongEnd));
            }

            static bool Same(SequenceEvent a, SequenceEvent b) => a == null || b == null ? a == null && b == null :
                a.Type == b.Type && ReferenceEquals(a.Pattern, b.Pattern) && a.Offset == b.Offset && a.End == b.End && a.Gain == b.Gain && a.Clip == b.Clip;
        }

        internal int RoundUpToBar(int tick)
        {
            var meter = core.SongMeter;
            int bar = 0;
            foreach (var (start, length) in meter.GetBars(tick + 2048))
            {
                bar = start + length;
                if (bar >= tick) break;
            }
            return Math.Max(bar, tick);
        }

        /// <summary>Applies a plan as one undo step (with what <paramref name="before"/> already did in the same group).</summary>
        void Commit(Plan plan)
        {
            var action = plan.Build();
            core.SongCore.ActionStack.Do(action);
            core.SetModifiedFlag();
        }

        void Group(Action body)
        {
            var stack = core.SongCore.ActionStack;
            stack.BeginActionGroup();
            try { body(); }
            finally { stack.EndActionGroup(); }
        }

        // ------------------------------------------------------------------ pattern copies

        /// <summary>An independent copy of a pattern (Make unique): a new pattern named after it, on the song's history. Null if it can't be made.</summary>
        IPattern ClonePattern(IPattern source)
        {
            var layout = core.SessionLayout;
            if (layout == null || source?.Machine == null) return null;
            string name = Session.SessionLayout.CopyName(source.Machine, source.Name);
            int color = Session.PatternColors.Get(source);
            var create = new Session.SessionCreatePatternAction(layout, source.Machine, name, source.Length, source);
            core.SongCore.ActionStack.Do(create);
            var copy = create.Created;
            if (copy != null && color >= 0) core.SongCore.ActionStack.Do(new Session.PatternColorAction(layout, copy, color));
            return copy;
        }

        static string Lane(ISequence lane, IPattern pattern) =>
            lane == null || pattern?.Machine == null || lane.Machine != pattern.Machine ? "A clip stays on its own machine's lanes." : null;

        // ------------------------------------------------------------------ the edits

        /// <summary>The clip a pattern first becomes in the arrangement: its loop from the session clip's settings (the pattern's markers and whether it loops).</summary>
        ClipSpec NewSpec(IPattern pattern, int time, int length)
        {
            var loop = core.SessionLayout?.Document != null ? Session.SceneToArrangement.LoopOf(core.SessionLayout, pattern) : new ClipLoop(0, 0, true);
            var c = core.SessionLayout?.Document != null ? core.SessionLayout.ClipOf(pattern) : null;
            var shape = c == null ? (Start: 0, LoopStart: 0, End: pattern.Length) : new Session.ClipShape(c.Start, c.LoopStart, c.LoopEnd, c.Loop).Resolve(pattern.Length);
            return new ClipSpec(0, pattern, time, Math.Max(1, length), shape.Item1, loop.Start, loop.End, loop.On, 1.0);
        }

        /// <summary>Draws a clip: a new pattern of the clip's length on the machine, and the clip over it. Returns the clip's start.</summary>
        public string Draw(ISequence lane, int time, int length, out IPattern made)
        {
            made = null;
            if (lane?.Machine == null) return "There's no lane here.";
            time = Math.Max(0, time);
            length = Math.Max(1, length);
            IPattern pattern = null;
            Group(() =>
            {
                string name = lane.Machine.GetNewPatternName();
                core.SongCore.ActionStack.Do(new BuzzGUI.Common.Actions.MachineActions.CreatePatternAction(lane.Machine, name, length));
                pattern = lane.Machine.Patterns.FirstOrDefault(p => p.Name == name);
                if (pattern == null) return;
                var plan = new Plan(this);
                // The clip ends where the next one starts, if that's sooner than the pattern's length.
                var next = Read(lane).Where(i => i.Time >= time).OrderBy(i => i.Time).FirstOrDefault();
                var spec = NewSpec(pattern, time, length);
                if (next != null && next.Time < time + length) spec = spec with { Length = Math.Max(1, next.Time - time) };
                plan.Overwrite(lane, time, time + spec.Length);
                plan.Write(lane, spec);
                Commit(plan);
            });
            made = pattern;
            return pattern == null ? "The pattern couldn't be made." : null;
        }

        /// <summary>Places an existing pattern as a clip (shared with its other places). It plays once over its length, or <paramref name="length"/>.</summary>
        public string Place(ISequence lane, IPattern pattern, int time, int length = 0)
        {
            if (Lane(lane, pattern) is { } why) return why;
            var spec = NewSpec(pattern, Math.Max(0, time), length > 0 ? length : Math.Max(1, pattern.Length));
            var plan = new Plan(this);
            plan.Overwrite(lane, spec.Time, spec.Time + Math.Min(spec.Length, spec.MaxLength));
            plan.Write(lane, spec, newId: true);
            Commit(plan);
            return null;
        }

        public enum CopyMode { Move, Independent, Linked }

        /// <summary>Moves a clip (to another lane of its machine, or in time), or puts a copy there: independent (a new pattern, "Bass 2") or linked (the same pattern).</summary>
        public string Move(ClipItem item, ISequence lane, int time, CopyMode mode)
        {
            if (Lane(lane, item.Pattern) is { } why) return why;
            time = Math.Max(0, time);
            if (mode == CopyMode.Move && lane == item.Sequence && time == item.Time) return null;
            string result = null;
            Group(() =>
            {
                var pattern = item.Pattern;
                if (mode == CopyMode.Independent)
                {
                    pattern = ClonePattern(item.Pattern);
                    if (pattern == null) { result = "The pattern couldn't be copied."; return; }
                }
                var plan = new Plan(this);
                var moved = item.Spec with { Pattern = pattern, Time = time };
                int span = moved.Segments().Sum(s => s.Length);
                if (mode == CopyMode.Move) plan.Remove(item);
                plan.Overwrite(lane, time, time + span);
                plan.Write(lane, moved, newId: mode != CopyMode.Move || item.Single);
                Commit(plan);
            });
            return result;
        }

        /// <summary>The clip starts at <paramref name="time"/> (its end stays): what plays there goes on playing.</summary>
        public string TrimLeft(ClipItem item, int time)
        {
            var s = item.Spec;
            time = Math.Min(time, s.End - 1);
            if (time < s.Time) time = Math.Max(time, s.Time - s.Start);        // not before the pattern's start
            time = Math.Max(0, time);
            if (time == s.Time) return null;
            var plan = new Plan(this);
            plan.Remove(item);
            if (time < s.Time) plan.Overwrite(item.Sequence, time, s.Time);
            plan.Write(item.Sequence, s.CutLeft(time) with { Start = Math.Clamp(s.Start + (time - s.Time), 0, s.RegionEnd - 1) });
            Commit(plan);
            return null;
        }

        /// <summary>The clip ends at <paramref name="end"/>: it trims, or with a loop repeats; without one it can't play past its region.</summary>
        public string TrimRight(ClipItem item, int end)
        {
            var s = item.Spec;
            int length = Math.Max(1, end - s.Time);
            if (!s.Loop) length = Math.Min(length, s.MaxLength);
            if (length == s.Length) return null;
            var plan = new Plan(this);
            plan.Remove(item);
            if (s.Time + length > s.End) plan.Overwrite(item.Sequence, s.End, s.Time + length);
            plan.Write(item.Sequence, s with { Length = length });
            Commit(plan);
            return null;
        }

        /// <summary>Cuts a clip in two at a tick.</summary>
        public string Split(ClipItem item, int at)
        {
            var s = item.Spec;
            if (at <= s.Time || at >= s.End) return "Split inside the clip.";
            var plan = new Plan(this);
            plan.Remove(item);
            plan.Write(item.Sequence, s.CutRight(at));
            plan.Write(item.Sequence, s.CutLeft(at), newId: true);
            Commit(plan);
            return null;
        }

        /// <summary>Joins a clip with the one that starts where it ends, if that plays on from it.</summary>
        public string Join(ClipItem a, ClipItem b)
        {
            if (a.Sequence != b.Sequence || a.End != b.Time || !ReferenceEquals(a.Pattern, b.Pattern)) return "Join clips of one pattern that touch.";
            var merged = a.Spec with { Length = a.Spec.Length + b.Spec.Length };
            if (a.Spec.Loop != b.Spec.Loop || a.Spec.LoopStart != b.Spec.LoopStart || a.Spec.LoopEnd != b.Spec.LoopEnd || a.Spec.Gain != b.Spec.Gain) return "These clips don't loop the same way.";
            // The same play, whichever way it is cut into events: a split in the middle of a repeat leaves two events where the whole has one.
            var want = Coalesce(merged.Segments());
            var have = Coalesce(a.Actual.Concat(b.Actual));
            if (want.Count != have.Count || want.Zip(have, (x, y) => x != y).Any(d => d)) return "The second clip doesn't play on from the first.";
            var plan = new Plan(this);
            plan.Remove(a);
            plan.Remove(b);
            plan.Write(a.Sequence, merged);
            Commit(plan);
            return null;
        }

        static List<ClipSegment> Coalesce(IEnumerable<ClipSegment> segments)
        {
            var list = new List<ClipSegment>();
            foreach (var s in segments)
            {
                if (list.Count > 0 && list[^1].Time + list[^1].Length == s.Time && list[^1].Offset + list[^1].Length == s.Offset)
                    list[^1] = list[^1] with { Length = list[^1].Length + s.Length };
                else list.Add(s);
            }
            return list;
        }

        /// <summary>Turns a clip's loop on or off (its region is kept for when it is on again).</summary>
        public string SetLoop(ClipItem item, bool on)
        {
            if (item.Spec.Loop == on) return null;
            var s = item.Spec with { Loop = on };
            if (!on) s = s with { Length = Math.Min(s.Length, s.MaxLength) };
            var plan = new Plan(this);
            plan.Remove(item);
            plan.Write(item.Sequence, s);
            Commit(plan);
            return null;
        }

        /// <summary>Gives the clip a pattern of its own (a copy of the one it shares).</summary>
        public string MakeUnique(ClipItem item)
        {
            string result = null;
            Group(() =>
            {
                var copy = ClonePattern(item.Pattern);
                if (copy == null) { result = "The pattern couldn't be copied."; return; }
                var plan = new Plan(this);
                plan.Remove(item);
                plan.Write(item.Sequence, item.Spec with { Pattern = copy });
                Commit(plan);
            });
            return result;
        }

        public string Delete(ClipItem item)
        {
            var plan = new Plan(this);
            plan.Remove(item);
            Commit(plan);
            return null;
        }

        /// <summary>
        /// Comping (daw-modes.md §4.4, AR5): the range of a take lane's clip goes on a main lane as an event of the take's pattern,
        /// trimmed to the range (<c>Offset</c>/<c>End</c>): no notes are copied. What the main lane held there is cut away.
        /// </summary>
        public string Comp(ISequence take, int from, int to, ISequence main)
        {
            if (take?.Machine == null || main?.Machine == null || take.Machine != main.Machine) return "Comp from a take lane to a lane of the same machine.";
            if (ReferenceEquals(take, main)) return "Comp from a take lane onto another lane.";
            var item = Read(take).FirstOrDefault(i => i.Time <= from && from < i.End);
            if (item == null) return "There's no take at the start of the range.";
            to = Math.Min(to, item.End);
            if (to <= from) return "Pick a range inside the take.";
            var spec = new ClipSpec(0, item.Pattern, from, to - from, item.Spec.PatternPositionAt(from) ?? 0, 0, 0, false, item.Spec.Gain);
            var plan = new Plan(this);
            plan.Overwrite(main, from, to);
            plan.Write(main, spec, newId: true);
            Commit(plan);
            return null;
        }

        /// <summary>A copy of a clip right after it, with a pattern of its own.</summary>
        public string Duplicate(ClipItem item) => Move(item, item.Sequence, item.End, CopyMode.Independent);

        /// <summary>Pastes a copied clip as an independent copy (a new pattern) on a lane of its machine at a time.</summary>
        public string Paste(ClipCopy copy, ISequence lane, int time)
        {
            if (Lane(lane, copy.Pattern) is { } why) return why;
            if (!lane.Machine.Patterns.Contains(copy.Pattern)) return "The pattern is gone.";
            string result = null;
            Group(() =>
            {
                var pattern = ClonePattern(copy.Pattern);
                if (pattern == null) { result = "The pattern couldn't be copied."; return; }
                var spec = new ClipSpec(0, pattern, Math.Max(0, time), copy.Length, copy.Start, copy.LoopStart, copy.LoopEnd, copy.Loop, copy.Gain);
                var plan = new Plan(this);
                plan.Overwrite(lane, spec.Time, spec.Time + spec.Segments().Sum(s => s.Length));
                plan.Write(lane, spec, newId: true);
                Commit(plan);
            });
            return result;
        }
    }
}
