// Fork (docs/specs/daw-modes.md S4, D3 A): the notes of a classic Buzz machine, one that takes
// notes in its Note track parameter rather than as MIDI. They live in the pattern's Note
// columns, a voice per track (here the engine-played columns the agent writes for such
// machines, mcp-server.md §12.2; the Modern Pattern Editor's own Note columns come with S7), and
// are edited with tracker rules: a note per track at a time, so a note that needs more tracks
// than the machine has is refused, with the reason. A note keeps its track while it moves; a new
// one takes the lowest free track. Velocity goes to the machine's velocity track parameter when
// it has one. Each edit is one undo step of the song's history.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Model;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Notes
{
    internal class TrackerColumnNotes : NoteSourceBase
    {
        protected readonly ReBuzzCore buzz;
        protected readonly IPattern pattern;
        protected readonly IParameter noteParameter;
        protected readonly IParameter velocityParameter;

        protected TrackerColumnNotes(ReBuzzCore buzz, IPattern pattern, IParameter note, IParameter velocity)
        {
            this.buzz = buzz;
            this.pattern = pattern;
            noteParameter = note;
            velocityParameter = velocity;
        }

        /// <summary>The pattern's tracker notes, or null when its machine has no Note track parameter.</summary>
        public static TrackerColumnNotes For(ReBuzzCore buzz, IPattern pattern)
        {
            var (note, velocity) = Parameters(pattern);
            return note == null ? null : new TrackerColumnNotes(buzz, pattern, note, velocity);
        }

        /// <summary>The Note track parameter and the velocity one (as the agent finds them), or nulls.</summary>
        protected static (IParameter Note, IParameter Velocity) Parameters(IPattern pattern)
        {
            var groups = pattern?.Machine?.ParameterGroups;
            if (groups == null || groups.Count < 3) return (null, null);
            var track = groups[2].Parameters;
            var note = track.FirstOrDefault(p => p.Type == ParameterType.Note);
            if (note == null) return (null, null);
            // As the agent finds it (MachineNotes.For): a track parameter with "vel" in its name.
            var velocity = track.FirstOrDefault(p => p.Type != ParameterType.Note && p.Name.Contains("vel", StringComparison.OrdinalIgnoreCase) && p.MaxValue > p.MinValue);
            return (note, velocity);
        }

        public override IPattern Pattern => pattern;

        /// <summary>The tracks the machine has now.</summary>
        protected int Tracks => Math.Max(1, pattern.Machine.TrackCount);

        protected int PatternTime => Math.Max(1, pattern.Length) * PatternEvent.TimeBase;

        (int Min, int Max)? VelocityRange => velocityParameter is { } v ? (v.MinValue, v.MaxValue) : null;

        protected ColumnKey NoteKey(int track) => new ColumnKey(2, noteParameter.IndexInGroup, track);

        ColumnKey VelocityKey(int track) => new ColumnKey(2, velocityParameter.IndexInGroup, track);

        protected virtual List<PatternEvent> Events(ColumnKey key) =>
            SessionLayout.ColumnFor(pattern, key, create: false)?.GetEvents(int.MinValue, int.MaxValue).OrderBy(e => e.Time).ToList() ?? new List<PatternEvent>();

        /// <summary>The tracks that have Note events: the machine's, and any beyond them still holding notes.</summary>
        protected virtual int TracksWithColumns()
        {
            int n = Tracks;
            foreach (var c in pattern.Columns)
                if (ReferenceEquals(c.Parameter, noteParameter)) n = Math.Max(n, c.Track + 1);
            return n;
        }

        /// <summary>A note as this store can keep it (the tracker's rows are on a grid); the engine's columns keep any time.</summary>
        protected virtual ClipNote Fit(ClipNote n) => n;

        /// <summary>The changes made, as one undo step of this store's history; null when done, else why not.</summary>
        protected virtual string Commit(List<ColumnEventsAction.Change> changes)
        {
            buzz.SongCore.ActionStack.Do(new ColumnEventsAction(buzz, pattern, changes));
            buzz.SetModifiedFlag();
            return null;
        }

        /// <summary>The notes with their tracks, in time order.</summary>
        List<(ClipNote Note, int Track)> Read()
        {
            var list = new List<(ClipNote, int)>();
            for (int t = 0, n = TracksWithColumns(); t < n; t++)
            {
                var notes = Events(NoteKey(t)).Select(e => (Track: t, e.Time, e.Value));
                Dictionary<(int, int), int> velocities = null;
                if (velocityParameter != null)
                {
                    velocities = new Dictionary<(int, int), int>();
                    foreach (var e in Events(VelocityKey(t))) velocities[(t, e.Time)] = e.Value;
                }
                foreach (var ne in TrackNotes.Read(notes, PatternTime, PatternEvent.TimeBase, velocities, VelocityRange))
                    list.Add((new ClipNote(ne.Time, ne.Duration, ne.Pitch, Math.Clamp(ne.Velocity, 1, 127)), t));
            }
            list.Sort((a, b) => a.Item1.Time != b.Item1.Time ? a.Item1.Time.CompareTo(b.Item1.Time) : a.Item1.Note.CompareTo(b.Item1.Note));
            return list;
        }

        public override List<ClipNote> Notes() => IsLive ? Read().Select(n => n.Note).ToList() : new List<ClipNote>();

        public override string WhyNotEditable =>
            !IsLive ? "The pattern was deleted."
            : buzz.SessionLayout?.IsRecordingClip(pattern) == true ? "Recording: edits wait until it stops."
            : null;

        protected override string Apply(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added)
        {
            var notes = Read();
            if (added.Count > 0) added = added.Select(Fit).ToList();
            // The removed notes' tracks: a note that moves (removed and added at the same place in the lists) keeps its track.
            var freed = new List<int>();
            foreach (var r in removed)
            {
                int i = notes.FindIndex(n => n.Note == r);
                freed.Add(i >= 0 ? notes[i].Track : -1);
                if (i >= 0) notes.RemoveAt(i);
            }
            int tracks = Tracks;
            bool Free(int track, ClipNote n) => !notes.Any(o => o.Track == track && o.Note.Time < n.End && n.Time < o.Note.End);
            for (int k = 0; k < added.Count; k++)
            {
                var n = added[k];
                if (TrackNotes.ToBuzz(n.Note) < 0)
                    return $"{ClipNotes.Name(n.Note)} is above what {pattern.Machine.Name} takes in its tracks ({BuzzGUI.Common.NoteNames.Name(119)} is the highest a Buzz track takes).";
                int track = removed.Count == added.Count && freed[k] >= 0 && freed[k] < tracks && Free(freed[k], n) ? freed[k] : -1;
                for (int t = 0; t < tracks && track < 0; t++)
                    if (Free(t, n)) track = t;
                if (track < 0)
                    return $"{pattern.Machine.Name} plays a note per track and has {tracks} track{(tracks == 1 ? "" : "s")}: " +
                           $"{ClipNotes.Name(n.Note)} at {n.Time / (double)PatternEvent.TimeBase:0.##} ticks needs one more at once.";
                notes.Add((n, track));
            }

            // Each track's Note events (and velocities) from its notes, changed where they differ.
            var all = notes.Select(n => new NoteEvent(n.Note.Time, n.Note.Duration, n.Note.Note, n.Note.Velocity)).ToList();
            var events = TrackNotes.Events(all, notes.Select(n => n.Track).ToArray(), PatternTime, PatternEvent.TimeBase, VelocityRange);
            var changes = new List<ColumnEventsAction.Change>();
            for (int t = 0, n = Math.Max(tracks, TracksWithColumns()); t < n; t++)
            {
                var noteOld = Events(NoteKey(t));
                var noteNew = events.Where(e => e.Track == t).Select(e => new PatternEvent(e.Time, e.Note, 0)).ToList();
                AddChange(changes, NoteKey(t), noteOld, noteNew);
                if (velocityParameter == null) continue;
                var velocityOld = Events(VelocityKey(t));
                var velocityNew = events.Where(e => e.Track == t && e.Velocity.HasValue).Select(e => new PatternEvent(e.Time, e.Velocity.Value, 0)).ToList();
                AddChange(changes, VelocityKey(t), velocityOld, velocityNew);
            }
            if (changes.Count == 0) return null;
            return Commit(changes);
        }

        static void AddChange(List<ColumnEventsAction.Change> changes, ColumnKey key, List<PatternEvent> before, List<PatternEvent> after)
        {
            var gone = before.Where(e => !after.Contains(e)).ToArray();
            var come = after.Where(e => !before.Contains(e)).ToArray();
            if (gone.Length > 0 || come.Length > 0) changes.Add(new ColumnEventsAction.Change(key, gone, come));
        }

        /// <summary>Plays the note on the machine's first track (its Note parameter), or lets it go.</summary>
        public override void Preview(int note, int velocity, bool on)
        {
            if (pattern?.Machine is not MachineCore machine || machine.DLL == null || machine.DLL.IsMissing || machine.DLL.IsCrashed || !machine.Ready) return;
            int value = on ? TrackNotes.ToBuzz(note) : TrackNotes.Off;
            if (value < 0) return;
            const int notRecorded = 1 << 16;
            if (on && velocityParameter != null && VelocityRange is (int lo, int hi))
                velocityParameter.SetValue(notRecorded, lo + (int)Math.Round((hi - lo) * Math.Clamp(velocity, 1, 127) / 127.0));
            noteParameter.SetValue(notRecorded, value);
        }

        protected override void DoUndo() => buzz.SongCore.ActionStack.Undo();

        protected override void DoRedo() => buzz.SongCore.ActionStack.Redo();
    }
}
