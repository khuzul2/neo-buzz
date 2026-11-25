#nullable disable
// Fork addition (neo-buzz, docs/specs/clips.md §7.1, wave CU2): the converter that turns what a song keeps of a clip outside the
// engine's columns into lanes: a built-in pattern's rows (PATT), PatternXP's data (versions 1 to 3) and the Modern Pattern Editor's
// (version 1). It is pure: bytes and rows in, lanes and a log out, nothing is written anywhere and the original file is never
// touched. CU2 builds it and nothing calls it on open (CU3 does).
//
// What happens to each source column:
//  - a parameter column (a machine's global or track parameter) becomes a Parameter lane with the same events, at the same times;
//  - the editor's MIDI lanes (Note, Velocity, Delay and Cut of each track) become the machine's Notes lane (TrackerNotes.FromRows, as the
//    editor's own ExportClipNotes reads them): a note with its time (row + delay), length (cut, the next note or off, or the clip's end)
//    and velocity; an off with no note before it in the clip becomes an explicit note-off event (clips.md §5.4);
//  - a Pitch Wheel or CC lane on a track with a note lane is dropped (the editor throws on them there), on a track without one it becomes
//    controller events of the Notes lane;
//  - PatternXP's own columns (trigger, effect, effect data), a machine or parameter the song lacks, a track the machine no longer has,
//    a built-in pattern's input columns and data in a format we cannot read are dropped.
// Every step writes a log entry with a cause tag (ClipTags) and the number of events it covers. The events of the source are counted
// in, kept, merged (into notes) or dropped, so none goes missing without a line.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;

namespace BuzzGUI.Common
{
    /// <summary>A built-in pattern's contents (Buzz's own pattern editor): rows of parameter values, for one machine (clips.md §2.1, S6).</summary>
    public sealed class BuiltInRows
    {
        public string Name;
        public string Machine;
        public int Rows;
        public int[][] Global = Array.Empty<int[]>();               // [row][parameter]
        public int[][][] Tracks = Array.Empty<int[][]>();           // [track][row][parameter]
        public int[] GlobalNoValue = Array.Empty<int>();            // the value that means "nothing" for each global parameter
        public int[] TrackNoValue = Array.Empty<int>();
        /// <summary>The cells of the connections' amp and pan that hold a value, by the name of the source machine (not converted: the editor drops them too).</summary>
        public Dictionary<string, int> InputCells = new Dictionary<string, int>();
    }

    public static class ClipMigration
    {
        public sealed class Result
        {
            public List<ClipContent> Clips = new List<ClipContent>();
            /// <summary>The events of the source: in = kept + merged + dropped.</summary>
            public int EventsIn, EventsKept, EventsMerged, EventsDropped;
            public bool Conserved => EventsIn == EventsKept + EventsMerged + EventsDropped;
        }

        /// <summary>The editor's internal parameters (PatternEditorUtils.InternalParameter).</summary>
        public const int MidiNote = -128, MidiVelocity = -129, MidiNoteDelay = -130, MidiNoteCut = -131, MidiPitchWheel = -132, MidiCC = -133;

        static bool IsSp(int index) => index == -10 || index == -11 || index == -12 || index == -110 || index == -111 || index == -112;

        static bool IsMidiLane(int index) => index <= MidiNote && index >= MidiCC;

        /// <summary>The default velocity of a note whose row has none (MPEInternalParameter.DefaultMidiVolume).</summary>
        public const int DefaultVelocity = 100;

        /// <summary>
        /// A Note value as the editor keeps it (MPEPatternColumn.SetEvents): one it cannot read is taken as a MIDI note number.
        /// </summary>
        public static int FixNote(int value)
        {
            if (value == BuzzNote.Off) return value;
            int o = value >> 4;
            if ((value & 15) < 1 || (value & 15) > 12 || o < BuzzNote.MinOctave || o > BuzzNote.MaxOctave)
            {
                value = Math.Max(0, Math.Min(value, 119));
                return BuzzNote.FromMIDINote(value);
            }
            return value;
        }

        // ------------------------------------------------------------------ built-in patterns (PATT)

        /// <summary>
        /// A built-in pattern's rows as lanes: a lane for each parameter with a value, an event for each row that has one at row × 240
        /// (4 rows a beat), as BuiltInPattern.ToPXP and the editor read them. The connections' amp and pan are dropped (one log line each source).
        /// </summary>
        public static ClipContent FromBuiltIn(BuiltInRows builtIn, ClipMachine machine, int lengthTicks, ClipLog log, Result result = null)
        {
            var clip = new ClipContent { Name = builtIn.Name, Length = lengthTicks > 0 ? lengthTicks : Math.Max(1, builtIn.Rows), RowsPerBeat = 4 };
            result ??= new Result();
            var lanes = new Dictionary<string, ClipLane>();
            int shifted = 0;

            void Column(int group, int index, int track, int[][] rows, int column, int noValue)
            {
                var events = new List<ClipEvent>();
                bool note = machine.IsNote(group, index);
                int fixedNotes = 0;
                for (int i = 0; i < rows.Length; i++)
                    if (column < rows[i].Length && rows[i][column] != noValue)
                    {
                        int v = rows[i][column];
                        if (note) { int f = FixNote(v); if (f != v) fixedNotes++; v = f; }
                        events.Add(new ClipEvent(i * ClipEvent.TimeBase, v));
                    }
                result.EventsIn += events.Count;
                if (events.Count == 0) return;
                if (group == 2 && track >= machine.TrackCount)
                {
                    result.EventsDropped += events.Count;
                    log.Loss(ClipTags.TrackDropped, machine.Name, builtIn.Name, $"{group}.{index}.{track}", events.Count, $"the machine has {machine.TrackCount} tracks");
                    return;
                }
                var lane = new ClipLane { Kind = ClipLaneKind.Parameter, Machine = machine.Name, Group = group, Index = index, Track = track, Grid = ClipGrid.Format(4, null) };
                lane.Events = events;
                lanes[lane.Key] = lane;
                result.EventsKept += events.Count;
                shifted += events.Count;
                log.Change(ClipTags.NoteFixed, machine.Name, builtIn.Name, $"{group}.{index}.{track}", fixedNotes);
            }

            for (int j = 0; j < machine.GlobalCount; j++)
                Column(1, j, 0, builtIn.Global, j, j < builtIn.GlobalNoValue.Length ? builtIn.GlobalNoValue[j] : 0);
            for (int t = 0; t < builtIn.Tracks.Length; t++)
                for (int j = 0; j < machine.TrackParamCount; j++)
                    Column(2, j, t, builtIn.Tracks[t], j, j < builtIn.TrackNoValue.Length ? builtIn.TrackNoValue[j] : 0);

            foreach (var kv in builtIn.InputCells)
            {
                result.EventsIn += kv.Value;
                result.EventsDropped += kv.Value;
                log.Loss(ClipTags.InputColumn, machine.Name, builtIn.Name, "input " + kv.Key, kv.Value, "a connection's amp and pan");
            }
            log.Change(ClipTags.BlockShift, machine.Name, builtIn.Name, "parameters", shifted);
            clip.Lanes.AddRange(lanes.Values);
            result.Clips.Add(clip);
            return clip;
        }

        // ------------------------------------------------------------------ the editor's data (PXP v1-3, MPE v1)

        /// <summary>
        /// The patterns of an editor's data blob as clips. <paramref name="lengthOf"/> gives a pattern's length in ticks (0 or less: not in the song).
        /// The note that rings at the end of a pattern on each voice is carried to the next pattern of the blob (in the order it holds them),
        /// so that an off at the start of the next one names it.
        /// </summary>
        public static Result FromBlob(byte[] blob, IClipMachines machines, Func<string, int> lengthOf, ClipLog log, string owner = null)
        {
            var result = new Result();
            var data = ClipBlob.Parse(blob);
            if (data.Problem != null && data.Clips.Count == 0 && data.Format == BlobFormat.None)
            {
                // Data of another editor, or of a version we do not read: dropped, one line (the editor clears it today).
                result.EventsIn = 0;
                log.Loss(ClipTags.ForeignDropped, owner, null, null, 1, $"{data.Problem}; {blob?.Length ?? 0} bytes");
                return result;
            }
            if (data.Problem != null)
                log.Loss(ClipTags.ForeignDropped, owner, null, null, 1, $"the data ends early: {data.Problem}");

            var ring = new Dictionary<(string, int), int>();
            foreach (var p in data.Clips)
                result.Clips.Add(Clip(p, data.Format, machines, lengthOf, log, result, ring));
            return result;
        }

        sealed class MidiTrack
        {
            public BlobColumn Note, Velocity, Delay, Cut;
            public readonly List<BlobColumn> Wheel = new List<BlobColumn>(), Cc = new List<BlobColumn>();
        }

        static ClipContent Clip(BlobClip p, BlobFormat format, IClipMachines machines, Func<string, int> lengthOf, ClipLog log, Result result, Dictionary<(string, int), int> ring)
        {
            int length = lengthOf?.Invoke(p.Name) ?? 0;
            bool inSong = length > 0;
            if (!inSong)
                length = p.Beats > 0 ? p.Beats * 4 : Math.Max(1, p.Columns.SelectMany(c => c.Events).Select(e => e.Time / ClipEvent.TimeBase + 1).DefaultIfEmpty(1).Max());
            var clip = new ClipContent { Name = p.Name, Length = length, RowsPerBeat = Math.Max(1, p.RowsPerBeat) };
            var lanes = new Dictionary<string, ClipLane>();
            var midi = new Dictionary<(string Machine, int Track), MidiTrack>();
            var machinesOfClip = new List<string>();
            int beats = clip.Beats;

            foreach (var col in p.Columns)
            {
                int n = col.Events.Count;
                result.EventsIn += n;
                if (inSong)
                {
                    // The editor drops the events past the end of the pattern as it loads them (MPEPatternColumn.SetEvents).
                    int limit = length * ClipEvent.TimeBase;
                    int past = col.Events.Count(e => e.Time >= limit);
                    if (past > 0)
                    {
                        col.Events = col.Events.Where(e => e.Time < limit).ToList();
                        result.EventsDropped += past;
                        log.Loss(ClipTags.EventPastEnd, col.Machine, p.Name, $"{col.ParamIndex}.{col.Track}", past, "after the end of the pattern");
                        n -= past;
                    }
                }
                string lane = $"{col.ParamIndex}.{col.Track}";
                var machine = machines.Find(col.Machine);
                void Drop(string tag, string detail)
                {
                    result.EventsDropped += n;
                    log.Loss(tag, col.Machine, p.Name, lane, n, detail);
                }
                if (machine == null) { Drop(ClipTags.MachineMissing, "the song has no machine of that name"); continue; }
                if (!machinesOfClip.Contains(machine.Name)) machinesOfClip.Add(machine.Name);
                if (IsSp(col.ParamIndex)) { Drop(ClipTags.PxpSpDropped, "PatternXP's trigger, effect or effect data column"); continue; }
                if (IsMidiLane(col.ParamIndex))
                {
                    var key = (machine.Name, col.Track);
                    if (!midi.TryGetValue(key, out var mt)) midi[key] = mt = new MidiTrack();
                    switch (col.ParamIndex)
                    {
                        case MidiNote: mt.Note ??= col; if (mt.Note != col) Merge(mt.Note, col); break;
                        case MidiVelocity: mt.Velocity ??= col; if (mt.Velocity != col) Merge(mt.Velocity, col); break;
                        case MidiNoteDelay: mt.Delay ??= col; if (mt.Delay != col) Merge(mt.Delay, col); break;
                        case MidiNoteCut: mt.Cut ??= col; if (mt.Cut != col) Merge(mt.Cut, col); break;
                        case MidiPitchWheel: mt.Wheel.Add(col); break;
                        default: mt.Cc.Add(col); break;
                    }
                    continue;
                }
                if (col.ParamIndex < 0) { Drop(ClipTags.ForeignDropped, "an internal parameter of an editor we do not know"); continue; }

                // A machine's parameter: globals first (on track 0), then the track parameters (PatternEditorUtils.GetParameter).
                int group, index;
                if (col.ParamIndex < machine.GlobalCount && col.Track == 0) { group = 1; index = col.ParamIndex; }
                else
                {
                    group = 2;
                    index = col.ParamIndex - machine.GlobalCount;
                    if (index < 0 || index >= machine.TrackParamCount) { Drop(ClipTags.ParamMissing, $"the machine has {machine.GlobalCount} global and {machine.TrackParamCount} track parameters"); continue; }
                }
                if (group == 2 && col.Track >= machine.TrackCount || col.Track < 0) { Drop(ClipTags.TrackDropped, $"the machine has {machine.TrackCount} tracks"); continue; }

                var events = new List<ClipEvent>(n);
                bool note = machine.IsNote(group, index);
                int fixedNotes = 0;
                foreach (var e in col.Events)
                {
                    int v = e.Value;
                    if (note) { int f = FixNote(v); if (f != v) fixedNotes++; v = f; }
                    events.Add(new ClipEvent(e.Time, v));
                }
                var l = new ClipLane { Kind = ClipLaneKind.Parameter, Machine = machine.Name, Group = group, Index = index, Track = col.Track };
                l.Grid = GridOf(col, p, beats);
                if (!lanes.TryGetValue(l.Key, out var existing)) lanes[l.Key] = existing = l;
                existing.Events.AddRange(events);
                result.EventsKept += events.Count;
                log.Change(ClipTags.NoteFixed, machine.Name, p.Name, lane, fixedNotes);
            }

            // Counted: the parameter lanes' events, per machine, for the change that moving them to the engine makes.
            foreach (var g in lanes.Values.Where(l => l.Kind == ClipLaneKind.Parameter).GroupBy(l => l.Machine))
            {
                int all = g.Sum(l => l.Events.Count);
                int off = g.Sum(l => l.Events.Count(e => e.Time % ClipEvent.TimeBase != 0));
                log.Change(ClipTags.BlockShift, g.Key, p.Name, "parameters", all);
                log.Change(ClipTags.Subtick, g.Key, p.Name, "parameters", off);
            }

            foreach (var m in machinesOfClip)
                NotesLane(m, p, clip, lanes, midi.Where(kv => kv.Key.Machine == m).ToList(), beats, log, result, ring);

            clip.Lanes.AddRange(lanes.Values);
            return clip;

            void Merge(BlobColumn into, BlobColumn extra) => into.Events.AddRange(extra.Events);
        }

        static string GridOf(BlobColumn col, BlobClip p, int beats)
        {
            if (col.BeatRows.Count == 0) return ClipGrid.Format(Math.Max(1, p.RowsPerBeat), null);
            var rows = new List<int>(beats);
            for (int b = 0; b < beats; b++) rows.Add(b < col.BeatRows.Count ? col.BeatRows[b] : Math.Max(1, p.RowsPerBeat));
            return ClipGrid.Format(Math.Max(1, p.RowsPerBeat), rows);
        }

        static int ValueAt(BlobColumn column, int time)
        {
            if (column == null) return 0;
            foreach (var e in column.Events)
                if (e.Time >= time && e.Time < time + 1)
                    return e.Value;
            return 0;
        }

        static void NotesLane(string machine, BlobClip p, ClipContent clip, Dictionary<string, ClipLane> lanes,
            List<KeyValuePair<(string Machine, int Track), MidiTrack>> tracks, int beats, ClipLog log, Result result, Dictionary<(string, int), int> ring)
        {
            if (tracks.Count == 0) return;
            int lengthTime = clip.Length * ClipEvent.TimeBase;
            var rows = new List<TrackerRow>();
            var controllers = new List<ClipEvent>();
            string grid = null;
            int delays = 0, cuts = 0;

            foreach (var kv in tracks.OrderBy(k => k.Key.Track))
            {
                int track = kv.Key.Track;
                var mt = kv.Value;
                string lane = $"midi.{track}";
                int Count(BlobColumn c) => c?.Events.Count ?? 0;
                if (mt.Note == null)
                {
                    // Velocity, delay and cut of no note do nothing in the editor; its pitch wheel and CC lanes become controller events.
                    foreach (var c in new[] { mt.Velocity, mt.Delay, mt.Cut })
                        if (c != null) { result.EventsDropped += c.Events.Count; log.Loss(ClipTags.MidiLaneOrphan, machine, p.Name, $"{c.ParamIndex}.{track}", c.Events.Count, "no note lane on the track"); }
                }
                else
                {
                    var beatRows = new List<int>(beats);
                    for (int b = 0; b < beats; b++) beatRows.Add(b < mt.Note.BeatRows.Count ? mt.Note.BeatRows[b] : Math.Max(1, p.RowsPerBeat));
                    grid ??= mt.Note.BeatRows.Count == 0 ? ClipGrid.Format(Math.Max(1, p.RowsPerBeat), null) : ClipGrid.Format(Math.Max(1, p.RowsPerBeat), beatRows);
                    var rowGrid = RowGrid.Beats(beatRows);
                    result.EventsMerged += Count(mt.Velocity) + Count(mt.Delay) + Count(mt.Cut);
                    foreach (var e in mt.Note.Events)
                    {
                        int midi = -1;
                        if (e.Value != BuzzNote.Off)
                        {
                            try { midi = BuzzNote.ToMIDINote(e.Value); }
                            catch (ArgumentOutOfRangeException)
                            {
                                result.EventsDropped++;
                                log.Loss(ClipTags.NoteInvalid, machine, p.Name, lane, 1, $"note value {e.Value}");
                                continue;
                            }
                        }
                        int row = rowGrid.RowAt(e.Time);
                        result.EventsMerged++;
                        int delay = ValueAt(mt.Delay, e.Time), cut = ValueAt(mt.Cut, e.Time);
                        rows.Add(new TrackerRow(track, rowGrid.Start(row), rowGrid.Length(row), midi, ValueAt(mt.Velocity, e.Time), delay, cut));
                        if (midi >= 0 && delay > 0) delays++;
                        if (midi >= 0 && cut > 0) cuts++;
                    }
                    // The pitch wheel and CC lanes of a track with a note lane: the editor throws on them while it renders.
                    foreach (var c in mt.Wheel.Concat(mt.Cc))
                    {
                        result.EventsDropped += c.Events.Count;
                        log.Loss(ClipTags.CcLaneDropped, machine, p.Name, $"{c.ParamIndex}.{track}", c.Events.Count, "on a track with a note lane");
                    }
                    continue;
                }
                foreach (var c in mt.Wheel)
                    foreach (var e in c.Events)
                    {
                        controllers.Add(new ClipEvent(e.Time, ClipEvents.PitchWheel(Math.Clamp(e.Value, 0, 0x3FFF)), 0));
                        result.EventsMerged++;
                    }
                foreach (var c in mt.Cc)
                    foreach (var e in c.Events)
                    {
                        controllers.Add(new ClipEvent(e.Time, ClipEvents.ControlChange((e.Value >> 8) & 0x7F, e.Value & 0x7F), 0));
                        result.EventsMerged++;
                    }
            }
            log.Change(ClipTags.CcLaneConverted, machine, p.Name, "controllers", controllers.Count, "a pitch wheel or CC lane on a track with no note lane");

            var notes = TrackerNotes.FromRows(rows, lengthTime, DefaultVelocity);
            int skipped = rows.Count(r => r.Midi >= 0) - notes.Count;       // a note whose delay puts its start past the end of the clip
            if (skipped > 0)
            {
                result.EventsMerged -= skipped;
                result.EventsDropped += skipped;
                log.Loss(ClipTags.EventPastEnd, machine, p.Name, "notes", skipped, "a note delayed past the end of the pattern");
            }
            var events = new List<ClipEvent>();
            foreach (var n in notes)
                events.Add(new ClipEvent(n.Time, ClipEvents.NoteOn(n.Midi, n.Velocity), n.Duration));

            // An off with no note before it on its voice in this clip (a note rings across clips): an explicit note-off event, duration 0.
            int explicitOffs = 0, unknownOffs = 0;
            foreach (var lane in rows.GroupBy(r => r.Track))
            {
                var ordered = lane.OrderBy(r => r.Time).ToList();
                bool seenNote = false;
                foreach (var r in ordered)
                {
                    if (r.Midi >= 0) { seenNote = true; continue; }
                    if (seenNote) continue;
                    ring.TryGetValue((machine, lane.Key), out int ringing);
                    bool known = ring.ContainsKey((machine, lane.Key));
                    int time = r.Time + TrackerNotes.FromFraction(r.Delay, Math.Max(1, r.Length));
                    if (time >= lengthTime) continue;
                    events.Add(ClipEvents.Off(time, known ? ringing : 0));
                    explicitOffs++;
                    if (!known) unknownOffs++;
                    ring.Remove((machine, lane.Key));
                    seenNote = true;                                // later offs of the voice end nothing either, but one off is the event
                }
            }
            // What rings at the end of this clip, for the next clip's first off: a voice's last row, if it is a note with no cut.
            foreach (var lane in rows.GroupBy(r => r.Track))
            {
                var last = lane.OrderBy(r => r.Time).Last();
                if (last.Midi >= 0 && last.Cut == 0) ring[(machine, lane.Key)] = last.Midi;
                else ring.Remove((machine, lane.Key));
            }

            events.AddRange(controllers);
            if (events.Count == 0) return;
            var notesLane = new ClipLane { Kind = ClipLaneKind.Notes, Machine = machine, Track = 0, Events = events.OrderBy(e => e.Time).ToList(), Grid = grid ?? ClipGrid.Format(Math.Max(1, p.RowsPerBeat), null) };
            lanes[notesLane.Key] = notesLane;
            log.Change(ClipTags.MidiTick, machine, p.Name, "notes", notes.Count, "the engine plays a lane note at its row, the editor one tick later");
            log.Change(ClipTags.MidiDelay, machine, p.Name, "notes", delays);
            log.Change(ClipTags.MidiCut, machine, p.Name, "notes", cuts);
            log.Info(ClipTags.OffExplicit, machine, p.Name, "notes", explicitOffs, "an off with no note before it in the clip");
            log.Change(ClipTags.OffNoteUnknown, machine, p.Name, "notes", unknownOffs, "no ringing note before the clip: note 0");
        }
    }
}
