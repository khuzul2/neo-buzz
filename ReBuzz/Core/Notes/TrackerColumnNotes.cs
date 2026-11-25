// Fork (docs/specs/daw-modes.md S4, D3 A; clips.md §5.3, CU4): the notes of a machine that takes
// notes in its Note track parameter rather than as MIDI. They live in the clip's Note and velocity
// lanes, a voice per track (every lane of the parameter and track, whoever wrote it: the tracker's, a
// take's, the agent's; StoreLanes), and are edited with tracker rules: a note per track at a time, so
// a note that needs more tracks than the machine has is refused, with the reason. A note keeps its
// track while it moves; a new one takes the lowest free track. Velocity goes to the machine's velocity
// track parameter when it has one. Each edit is one undo step of the song's history. The agent asks
// for more tracks when a chord needs them (AllowMoreTracks): the notes are then given tracks again
// as a whole and the machine's track count is raised in the same step.
//
// A machine that plays a sample per note (INoteSampleProvider: Neo Sampler in Tracker mode, TM1) has more per note: its Sample, Offset (low and high
// part) and Tune track parameters, found by name beside the note's. They are written and read at the note's own time on its track, so a note's
// sample, offset and tune are in the clip like its pitch and velocity, in the same undo step, and a note that moves takes them with it.

using System;
using System.Collections.Generic;
using System.Linq;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Model;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Notes
{
    internal sealed class TrackerColumnNotes : NoteSourceBase
    {
        readonly ReBuzzCore buzz;
        readonly IClip clip;
        readonly IParameter noteParameter;
        readonly IParameter velocityParameter;
        readonly IParameter sampleParameter, offsetParameter, offsetHiParameter, tuneParameter;
        List<string> raised;

        TrackerColumnNotes(ReBuzzCore buzz, IClip clip, IParameter note, IParameter velocity)
        {
            this.buzz = buzz;
            this.clip = clip;
            noteParameter = note;
            velocityParameter = velocity;
            if (clip.Machine is MachineCore { ManagedMachine: INoteSampleProvider { PlaysSamplePerNote: true } } && clip.Machine.ParameterGroups.Count > 2)
            {
                var track = clip.Machine.ParameterGroups[2].Parameters;
                IParameter Find(string name) => track.FirstOrDefault(p => p.Name == name);
                var sample = Find("Sample");
                var lo = Find("Offset");
                var hi = Find("Offset hi");
                var tune = Find("Tune");
                if (sample != null && lo != null && hi != null && tune != null)
                {
                    sampleParameter = sample;
                    offsetParameter = lo;
                    offsetHiParameter = hi;
                    tuneParameter = tune;
                }
            }
        }

        /// <summary>Whether the notes carry a sample, an offset and a tune (the machine plays a sample per note, and has the parameters for it).</summary>
        public bool PerNoteSamples => sampleParameter != null;

        /// <summary>The Offset parameter's step: frames = Offset + this × Offset hi (the machine's own constant).</summary>
        public const int OffsetStep = 32768;

        /// <summary>The Tune parameter's value for the note's own pitch (cents are the value less this).</summary>
        public const int TuneCentre = 2400;

        /// <summary>The clip's tracker notes, or null when its machine has no Note track parameter.</summary>
        public static TrackerColumnNotes For(ReBuzzCore buzz, IClip clip)
        {
            var (note, velocity) = Parameters(clip);
            return note == null ? null : new TrackerColumnNotes(buzz, clip, note, velocity);
        }

        /// <summary>The Note track parameter and the velocity one (as the agent finds them), or nulls.</summary>
        static (IParameter Note, IParameter Velocity) Parameters(IClip clip)
        {
            var groups = clip?.Machine?.ParameterGroups;
            if (groups == null || groups.Count < 3) return (null, null);
            var track = groups[2].Parameters;
            var note = track.FirstOrDefault(p => p.Type == ParameterType.Note);
            if (note == null) return (null, null);
            // As the agent finds it (MachineNotes.For): a track parameter with "vel" in its name.
            var velocity = track.FirstOrDefault(p => p.Type != ParameterType.Note && p.Name.Contains("vel", StringComparison.OrdinalIgnoreCase) && p.MaxValue > p.MinValue);
            return (note, velocity);
        }

        public override IClip Clip => clip;

        /// <summary>The tracks the machine has now.</summary>
        int Tracks => Math.Max(1, clip.Machine.TrackCount);

        int ClipTime => Math.Max(1, clip.Length) * ClipEvent.TimeBase;

        (int Min, int Max)? VelocityRange => velocityParameter is { } v ? (v.MinValue, v.MaxValue) : null;

        ColumnKey NoteKey(int track) => new ColumnKey(2, noteParameter.IndexInGroup, track);

        ColumnKey VelocityKey(int track) => new ColumnKey(2, velocityParameter.IndexInGroup, track);

        static ColumnKey KeyOf(IParameter p, int track) => new ColumnKey(2, p.IndexInGroup, track);

        /// <summary>The value of each of a track's events of a parameter, by time.</summary>
        Dictionary<int, int> ByTime(IParameter p, int track)
        {
            var d = new Dictionary<int, int>();
            if (p != null) foreach (var e in Events(KeyOf(p, track))) d[e.Time] = e.Value;
            return d;
        }

        static List<ClipEvent> EventsOf(IClip clip, ColumnKey key) => StoreLanes.Events(clip, key);

        List<ClipEvent> Events(ColumnKey key) => EventsOf(clip, key);

        /// <summary>The agent's way: a chord that needs more tracks than the machine has raises its track count (up to what it can have), and the problems list says so.</summary>
        public TrackerColumnNotes AllowMoreTracks(List<string> problems)
        {
            raised = problems;
            return this;
        }

        /// <summary>The length of a row in clip time (1/240 tick), from the lane's Grid; a tick when it has none.</summary>
        public override int RowTime
        {
            get
            {
                foreach (var lane in StoreLanes.All(clip, NoteKey(0)))
                    if (lane.Metadata.TryGetValue(BuzzGUI.Common.ClipMeta.Grid, out var grid) && BuzzGUI.Common.ClipGrid.TryParse(grid, out int rpb, out _) && rpb > 0)
                        return Math.Max(1, ClipEvent.TimeBase * 4 / rpb);
                return ClipNotes.TimeBase;
            }
        }

        /// <summary>The tracks that have Note events: the machine's, and any beyond them still holding notes.</summary>
        int TracksWithColumns()
        {
            int n = Tracks;
            foreach (var c in clip.Columns)
                if (ReferenceEquals(c.Parameter, noteParameter)) n = Math.Max(n, c.Track + 1);
            return n;
        }

        /// <summary>The changes made, as one undo step of the song's history; null when done, else why not.</summary>
        string Commit(List<ColumnEventsAction.Change> changes)
        {
            buzz.SongCore.ActionStack.Do(new ColumnEventsAction(buzz, clip, changes));
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
                Dictionary<int, int> samples = null, offsets = null, offsetsHi = null, tunes = null;
                if (PerNoteSamples)
                {
                    samples = ByTime(sampleParameter, t);
                    offsets = ByTime(offsetParameter, t);
                    offsetsHi = ByTime(offsetHiParameter, t);
                    tunes = ByTime(tuneParameter, t);
                }
                foreach (var ne in TrackNotes.Read(notes, ClipTime, ClipEvent.TimeBase, velocities, VelocityRange))
                {
                    var clip = new ClipNote(ne.Time, ne.Duration, ne.Pitch, Math.Clamp(ne.Velocity, 1, 127));
                    if (samples != null && samples.TryGetValue(ne.Time, out int sample) && sample > 0)
                    {
                        offsets.TryGetValue(ne.Time, out int lo);
                        offsetsHi.TryGetValue(ne.Time, out int hi);
                        clip = clip with
                        {
                            Sample = sample,
                            Offset = Math.Max(0, lo) + OffsetStep * Math.Max(0, hi),
                            Tune = tunes.TryGetValue(ne.Time, out int tune) ? tune - TuneCentre : 0,
                        };
                    }
                    list.Add((clip, t));
                }
            }
            list.Sort((a, b) => a.Item1.Time != b.Item1.Time ? a.Item1.Time.CompareTo(b.Item1.Time) : a.Item1.Note.CompareTo(b.Item1.Note));
            return list;
        }

        public override List<ClipNote> Notes() => IsLive ? Read().Select(n => n.Note).ToList() : new List<ClipNote>();

        public override string WhyNotEditable =>
            !IsLive ? "The clip was deleted."
            : buzz.SessionLayout?.IsRecordingClip(clip) == true ? "Recording: edits wait until it stops."
            : null;

        protected override string Apply(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added)
        {
            var notes = Read();
            // The removed notes' tracks: a note that moves (removed and added at the same place in the lists) keeps its track.
            var freed = new List<int>();
            foreach (var r in removed)
            {
                int i = notes.FindIndex(n => n.Note == r);
                freed.Add(i >= 0 ? notes[i].Track : -1);
                if (i >= 0) notes.RemoveAt(i);
            }
            int tracks = Tracks;
            foreach (var n in added)
                if (TrackNotes.ToBuzz(n.Note) < 0)
                    return $"{ClipNotes.Name(n.Note)} is above what {clip.Machine.Name} takes in its tracks ({BuzzGUI.Common.NoteNames.Name(119)} is the highest a Buzz track takes).";
            if (raised != null)
            {
                // The agent's way: all the notes get tracks again, and the machine gets the tracks they need.
                foreach (var n in added) notes.Add((n, -1));
                var sorted = notes.Select(n => n.Note).OrderBy(n => new NoteEvent(n.Time, n.Duration, n.Note, n.Velocity), Comparer<NoteEvent>.Create(NoteList.Order)).ToList();
                var all = sorted.Select(n => new NoteEvent(n.Time, n.Duration, n.Note, n.Velocity)).ToList();
                var (given, needed) = TrackNotes.Allocate(all);
                var machine = (MachineCore)clip.Machine;
                int most = Math.Max(machine.DLL.Info.MinTracks, machine.DLL.Info.MaxTracks);
                if (needed > most)
                    return $"{machine.Name} ({machine.DLL.Name}) plays a note per track and has at most {most} tracks, but these notes need {needed} at once. " +
                           "Write fewer notes at once (shorter notes, or a chord split across two machines).";
                if (needed > machine.TrackCount)
                {
                    raised.Add($"{machine.Name} plays a note per track: raised its tracks from {machine.TrackCount} to {needed} for the chords.");
                    buzz.SongCore.ActionStack.Do(new BuzzGUI.Common.Actions.MachineActions.SetTrackCountAction(machine, needed));
                    tracks = machine.TrackCount;
                }
                notes = sorted.Select((n, i) => (n, given[i])).ToList();
                tracks = Math.Max(tracks, needed);
            }
            else
            {
                bool Free(int track, ClipNote n) => !notes.Any(o => o.Track == track && o.Note.Time < n.End && n.Time < o.Note.End);
                for (int k = 0; k < added.Count; k++)
                {
                    var n = added[k];
                    int track = removed.Count == added.Count && freed[k] >= 0 && freed[k] < tracks && Free(freed[k], n) ? freed[k] : -1;
                    for (int t = 0; t < tracks && track < 0; t++)
                        if (Free(t, n)) track = t;
                    if (track < 0)
                        return $"{clip.Machine.Name} plays a note per track and has {tracks} track{(tracks == 1 ? "" : "s")}: " +
                               $"{ClipNotes.Name(n.Note)} at {n.Time / (double)ClipEvent.TimeBase:0.##} ticks needs one more at once.";
                    notes.Add((n, track));
                }
            }

            // Each track's Note events (and velocities) from its notes, changed where they differ.
            var list = notes.Select(n => new NoteEvent(n.Note.Time, n.Note.Duration, n.Note.Note, n.Note.Velocity)).ToList();
            var events = TrackNotes.Events(list, notes.Select(n => n.Track).ToArray(), ClipTime, ClipEvent.TimeBase, VelocityRange);
            var changes = new List<ColumnEventsAction.Change>();
            for (int t = 0, n = Math.Max(tracks, TracksWithColumns()); t < n; t++)
            {
                var noteOld = Events(NoteKey(t));
                var noteNew = events.Where(e => e.Track == t).Select(e => new ClipEvent(e.Time, e.Note, 0)).ToList();
                AddChange(changes, NoteKey(t), noteOld, noteNew);
                if (velocityParameter == null) continue;
                var velocityOld = Events(VelocityKey(t));
                var velocityNew = events.Where(e => e.Track == t && e.Velocity.HasValue).Select(e => new ClipEvent(e.Time, e.Velocity.Value, 0)).ToList();
                AddChange(changes, VelocityKey(t), velocityOld, velocityNew);
            }
            if (PerNoteSamples)
                for (int t = 0, n = Math.Max(tracks, TracksWithColumns()); t < n; t++)
                {
                    var mine = notes.Where(x => x.Track == t).Select(x => x.Note).ToList();
                    AddChange(changes, KeyOf(sampleParameter, t), Events(KeyOf(sampleParameter, t)), mine.Where(x => x.Sample > 0).Select(x => new ClipEvent(x.Time, Math.Min(x.Sample, sampleParameter.MaxValue), 0)).ToList());
                    // The offset and tune cells are written only where they differ from the default, as a tracker writes them.
                    AddChange(changes, KeyOf(offsetParameter, t), Events(KeyOf(offsetParameter, t)),
                        mine.Where(x => x.Sample > 0 && x.Offset % OffsetStep > 0).Select(x => new ClipEvent(x.Time, x.Offset % OffsetStep, 0)).ToList());
                    AddChange(changes, KeyOf(offsetHiParameter, t), Events(KeyOf(offsetHiParameter, t)),
                        mine.Where(x => x.Sample > 0 && x.Offset >= OffsetStep).Select(x => new ClipEvent(x.Time, Math.Min(x.Offset / OffsetStep, offsetHiParameter.MaxValue), 0)).ToList());
                    AddChange(changes, KeyOf(tuneParameter, t), Events(KeyOf(tuneParameter, t)),
                        mine.Where(x => x.Sample > 0 && x.Tune != 0).Select(x => new ClipEvent(x.Time, Math.Clamp(TuneCentre + x.Tune, tuneParameter.MinValue, tuneParameter.MaxValue), 0)).ToList());
                }
            if (changes.Count == 0) return null;
            return Commit(changes);
        }

        static void AddChange(List<ColumnEventsAction.Change> changes, ColumnKey key, List<ClipEvent> before, List<ClipEvent> after)
        {
            var gone = before.Where(e => !after.Contains(e)).ToArray();
            var come = after.Where(e => !before.Contains(e)).ToArray();
            if (gone.Length > 0 || come.Length > 0) changes.Add(new ColumnEventsAction.Change(key, gone, come));
        }

        /// <summary>Plays the note on the machine's first track (its Note parameter), or lets it go.</summary>
        public override void Preview(int note, int velocity, bool on)
        {
            if (clip?.Machine is not MachineCore machine || machine.DLL == null || machine.DLL.IsMissing || machine.DLL.IsCrashed || !machine.Ready) return;
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
