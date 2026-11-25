using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.FileOps;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork addition (docs/specs/clips.md §5 and §10, wave CU2): the engine's side of the one store. A clip's content is its pattern's
    /// columns (the store); this reads them as lanes and writes lanes into them, and runs the converter (BuzzGUI.Common's ClipMigration)
    /// on a machine's data (a .nbl of format 1 or 2 holds its tracker data in the pattern editors' blobs, which open-time migration moves here).
    ///
    /// Columns the converter writes carry no <c>SessionAutomation</c> mark, so the engine does not play them (the editor still plays its own
    /// data): the parameter lanes change nothing that is heard. A Notes lane is the machine's MIDI column, which the engine plays whatever
    /// its metadata, so a song that has been migrated is not to be played until CU3 stops the editor playing the same notes.
    /// </summary>
    internal static class ClipStore
    {
        /// <summary>
        /// The host plays every lane from the engine (clips.md §5.6, CU3): the Studio and rebuzz-headless call this once, first.
        /// </summary>
        public static void UseDirectEngine()
        {
            PatternColumnCore.PlaysEveryLane = true;
        }

        /// <summary>Whether the song holds anything an older build would not play from the lanes: tracker lanes or notes (a native song then needs a reader of format 3).</summary>
        public static bool HoldsTrackerLanes(SongCore song)
        {
            foreach (var m in song.MachinesList)
                foreach (var p in m.PatternsList)
                    foreach (var c in p.ColumnsList.OfType<PatternColumnCore>())
                        if (c.Type == PatternColumnType.Parameter && !c.IsMarkedAutomation && c.GetEvents(int.MinValue, int.MaxValue).Any())
                            return true;
            return false;
        }

        // ------------------------------------------------------------------ machines

        /// <summary>What the converter needs to know of a machine.</summary>
        public static ClipMachine ModelOf(MachineCore machine)
        {
            var model = new ClipMachine { Name = machine.Name, TrackCount = machine.TrackCount };
            if (machine.ParameterGroupsList.Count > 1)
            {
                var globals = machine.ParameterGroupsList[1].ParametersList;
                model.GlobalCount = globals.Count;
                for (int i = 0; i < globals.Count; i++)
                    if (globals[i].Type == ParameterType.Note) model.NoteGlobal.Add(i);
            }
            if (machine.ParameterGroupsList.Count > 2)
            {
                var tracks = machine.ParameterGroupsList[2].ParametersList;
                model.TrackParamCount = tracks.Count;
                for (int i = 0; i < tracks.Count; i++)
                    if (tracks[i].Type == ParameterType.Note) model.NoteTrack.Add(i);
            }
            return model;
        }

        sealed class SongMachines : IClipMachines
        {
            readonly Dictionary<string, MachineCore> byName = new Dictionary<string, MachineCore>();
            readonly Dictionary<string, ClipMachine> models = new Dictionary<string, ClipMachine>();

            public SongMachines(IEnumerable<MachineCore> machines)
            {
                foreach (var m in machines)
                    byName[m.Name] = m;
            }

            public MachineCore Machine(string name) => name != null && byName.TryGetValue(name, out var m) ? m : null;

            public ClipMachine Find(string name)
            {
                if (name == null) return null;
                if (models.TryGetValue(name, out var model)) return model;
                var m = Machine(name);
                return m == null ? null : models[name] = ModelOf(m);
            }
        }

        static SongMachines MachinesOf(SongCore song) => new SongMachines(song.MachinesList);

        // ------------------------------------------------------------------ the store as lanes

        /// <summary>The lanes of a pattern's columns, with their metadata.</summary>
        public static ClipContent Read(PatternCore pattern)
        {
            var clip = new ClipContent { Name = pattern.Name, Length = pattern.Length };
            foreach (var c in pattern.ColumnsList)
            {
                var machine = c.Machine ?? pattern.Machine;
                var parameter = c.Parameter;
                bool midi = c.Type == PatternColumnType.MIDI || parameter == null || parameter.IndexInGroup < 0;
                var lane = new ClipLane
                {
                    Kind = midi ? ClipLaneKind.Notes : ClipLaneKind.Parameter,
                    Machine = machine?.Name,
                    Track = midi ? 0 : c.Track,
                    Events = c.GetEvents(int.MinValue, int.MaxValue).ToList(),
                    Meta = new Dictionary<string, string>(c.Metadata),
                };
                if (!midi)
                {
                    lane.Group = machine.ParameterGroups.IndexOf(parameter.Group);
                    lane.Index = parameter.IndexInGroup;
                }
                clip.Lanes.Add(lane);
            }
            var grid = clip.Lanes.Select(l => l.Grid).FirstOrDefault(g => g != null);
            if (grid != null && ClipGrid.TryParse(grid, out int rpb, out _)) clip.RowsPerBeat = rpb;
            return clip;
        }

        /// <summary>
        /// Puts a clip's lanes into the pattern's columns: a lane whose key has a column replaces that column's events, any other makes a column
        /// (metadata first, as the engine reads the session mark while it plays). A lane that cannot be put (a machine or parameter the song
        /// lacks, a track past the machine's) is a log line. Returns the number of lanes written.
        /// </summary>
        public static int Write(PatternCore pattern, ClipContent clip, SongCore song, ClipLog log, bool mergeNotes = false)
        {
            var machines = MachinesOf(song);
            var existing = Read(pattern);
            int written = 0;
            foreach (var lane in clip.Lanes)
            {
                var machine = machines.Machine(lane.Machine);
                string where = lane.Kind == ClipLaneKind.Notes ? "notes" : $"{lane.Group}.{lane.Index}.{lane.Track}";
                if (machine == null)
                {
                    log.Loss(ClipTags.MachineMissing, lane.Machine, clip.Name, where, lane.Events.Count, "writing the lane into the pattern");
                    continue;
                }
                IPatternColumn column = null;
                var key = lane.Key;
                for (int i = 0; i < existing.Lanes.Count && column == null; i++)
                    if (existing.Lanes[i].Key == key)
                        column = pattern.ColumnsList[i];
                if (column == null)
                {
                    column = Insert(pattern, machine, lane, out string problem);
                    if (column == null)
                    {
                        log.Loss(problem == "track" ? ClipTags.TrackDropped : ClipTags.ParamMissing, lane.Machine, clip.Name, where, lane.Events.Count, "writing the lane into the pattern");
                        continue;
                    }
                }
                else
                {
                    var old = column.GetEvents(int.MinValue, int.MaxValue).ToArray();
                    if (mergeNotes && lane.Kind == ClipLaneKind.Notes && old.Length > 0)
                    {
                        // An older build played the Notes lane (the song's) and the editor's own MIDI lanes together: both stay, once each.
                        var have = new HashSet<(int, int, int)>(old.Select(e => (e.Time, e.Value, e.Duration)));
                        var extra = lane.Events.Where(e => have.Add((e.Time, e.Value, e.Duration))).ToArray();
                        if (extra.Length > 0) column.SetEvents(extra, true);
                        foreach (var kv in lane.Meta) if (!column.Metadata.ContainsKey(kv.Key)) column.Metadata[kv.Key] = kv.Value;
                        written++;
                        continue;
                    }
                    if (old.Length > 0) column.SetEvents(old, false);
                    foreach (var kv in lane.Meta) column.Metadata[kv.Key] = kv.Value;
                }
                if (lane.Events.Count > 0) column.SetEvents(lane.Events.ToArray(), true);
                written++;
            }
            return written;
        }

        static IPatternColumn Insert(PatternCore pattern, MachineCore machine, ClipLane lane, out string problem)
        {
            problem = null;
            var metadata = new Dictionary<string, string>(lane.Meta);
            if (lane.Kind == ClipLaneKind.Notes)
            {
                int at = pattern.ColumnsList.Count;
                if (ReferenceEquals(machine, pattern.Machine))
                    pattern.InsertColumn(at, (IMachine)null);
                else
                    pattern.InsertColumn(at, ParameterCore.GetMidiParameter(machine, pattern.Dispatcher), 0, metadata);
                var made = pattern.ColumnsList[at];
                foreach (var kv in metadata) made.Metadata[kv.Key] = kv.Value;
                return made;
            }
            var groups = machine.ParameterGroupsList;
            if (lane.Group < 1 || lane.Group > 2 || lane.Group >= groups.Count || lane.Index < 0 || lane.Index >= groups[lane.Group].ParametersList.Count)
            {
                problem = "parameter";
                return null;
            }
            if (lane.Group == 2 && lane.Track >= machine.TrackCount || lane.Track < 0)
            {
                problem = "track";
                return null;
            }
            var parameter = groups[lane.Group].ParametersList[lane.Index];
            int index = pattern.ColumnsList.Count;
            pattern.InsertColumn(index, parameter, lane.Track, metadata);
            return pattern.ColumnsList[index];
        }

        // ------------------------------------------------------------------ migration and export

        /// <summary>The bytes of a machine's pattern editor data: its editor's, or the opaque data of a placeholder editor (PatternXP's format).</summary>
        public static byte[] EditorData(MachineCore machine)
        {
            // CU3: what the song file gave the editor (its stamp is the store's, a derived blob), else what the editor holds now.
            if (machine.EditorMachine != null) return machine.EditorMachine.LoadedEditorData ?? machine.PatternEditorData;
            return machine.PlaceholderEditor?.Data;
        }

        /// <summary>
        /// The clips of a machine as lanes, from what the song keeps outside the engine's columns: the built-in rows of its patterns, and its
        /// pattern editor's data (<paramref name="editorData"/> instead of the editor's own, for a test). Reads and writes nothing in the song. Every
        /// step is in <paramref name="log"/>.
        /// </summary>
        public static ClipMigration.Result Migrate(MachineCore machine, SongCore song, ClipLog log, byte[] editorData = null)
        {
            var machines = MachinesOf(song);
            var result = new ClipMigration.Result();
            var model = machines.Find(machine.Name);
            foreach (var pattern in machine.PatternsList)
            {
                if (pattern.BuiltInData == null) continue;
                ClipMigration.FromBuiltIn(ToRows(pattern, machine), model, pattern.Length, log, result);
            }
            var data = editorData ?? EditorData(machine);
            if (data != null && data.Length > 0)
            {
                var lengths = machine.PatternsList.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().Length);
                var fromBlob = ClipMigration.FromBlob(data, machines, name => lengths.TryGetValue(name, out int l) ? l : 0, log, machine.Name);
                result.Clips.AddRange(fromBlob.Clips);
                result.EventsIn += fromBlob.EventsIn;
                result.EventsKept += fromBlob.EventsKept;
                result.EventsMerged += fromBlob.EventsMerged;
                result.EventsDropped += fromBlob.EventsDropped;
            }
            return result;
        }

        /// <summary>
        /// The clips of a machine as lanes at open (clips.md §7.1): the built-in rows of its patterns (PATT, which the loaders also hand to the
        /// default editor as PatternXP data: the rows are the source, not the editor's copy of them) and, for a pattern with none, the editor's
        /// data. What the loaders dropped (the connections' amp and pan) is counted and logged. Reads and writes nothing in the song.
        /// </summary>
        public static ClipMigration.Result MigrateForOpen(MachineCore machine, SongCore song, ClipLog log, byte[] editorData = null)
        {
            var machines = MachinesOf(song);
            var result = new ClipMigration.Result();
            var model = machines.Find(machine.Name);
            var builtIn = new HashSet<string>();
            foreach (var pattern in machine.PatternsList)
            {
                if (pattern.BuiltInData == null) continue;
                builtIn.Add(pattern.Name);
                ClipMigration.FromBuiltIn(ToRows(pattern, machine), model, pattern.Length, log, result);
            }
            bool allBuiltIn = machine.PatternsList.Count > 0 && builtIn.Count == machine.PatternsList.Select(p => p.Name).Distinct().Count();
            var data = editorData ?? EditorData(machine);
            if (!allBuiltIn && data != null && data.Length > 0)
            {
                var lengths = machine.PatternsList.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().Length);
                var fromBlob = ClipMigration.FromBlob(data, machines, name => lengths.TryGetValue(name, out int l) ? l : 0, log, machine.Name);
                result.Clips.AddRange(fromBlob.Clips.Where(c => !builtIn.Contains(c.Name)));
                result.EventsIn += fromBlob.EventsIn;
                result.EventsKept += fromBlob.EventsKept;
                result.EventsMerged += fromBlob.EventsMerged;
                result.EventsDropped += fromBlob.EventsDropped;
            }
            return result;
        }

        /// <summary>A built-in pattern as the converter reads it: its rows, and how many cells of each connection's amp and pan hold a value.</summary>
        internal static BuiltInRows ToRows(PatternCore pattern, MachineCore machine)
        {
            var data = pattern.BuiltInData;
            var rows = new BuiltInRows
            {
                Name = pattern.Name,
                Machine = machine.Name,
                Rows = pattern.Length,
                Global = data.Global,
                Tracks = data.Tracks,
                GlobalNoValue = machine.ParameterGroupsList.Count > 1 ? machine.ParameterGroupsList[1].ParametersList.Select(p => p.NoValue).ToArray() : Array.Empty<int>(),
                TrackNoValue = machine.ParameterGroupsList.Count > 2 ? machine.ParameterGroupsList[2].ParametersList.Select(p => p.NoValue).ToArray() : Array.Empty<int>(),
            };
            var cells = data.InputCells();
            if (cells != null)
                foreach (var kv in cells) rows.InputCells[kv.Key] = kv.Value;
            return rows;
        }

        /// <summary>The machines of the song as the converter sees them, by name.</summary>
        public static IClipMachines Machines(SongCore song) => MachinesOf(song);
    }
}
