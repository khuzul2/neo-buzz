// Fork (docs/specs/clips.md §10, wave CU2): the corpus's generated songs, in the process. tools/clip-null.py (CU1) builds one generated song a
// feature of a clip by driving an engine over MCP and putting the tracker's data into the song file; here the same features are built
// with the engine's own API and the editor's data as bytes (BuzzGUI.Common's ClipBlob), so that the round-trip tests run in the Studio
// test run on every platform with nothing but the repo. The names are the corpus's (clip-null.py list); a test checks that none is missing.
//
// A built song is a set of machines with patterns of the right lengths and, for each machine with tracker data, the blob its pattern editor
// would hold (the editor itself is not given it: the converter reads bytes). Where clip-null.py's songs are also at hand
// (CLIP_CORPUS_DIR), ClipRoundTripTests runs those files too.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Tests
{
    internal static class ClipCorpus
    {
        const int Beat = 4 * PatternEvent.TimeBase;
        const int Off = BuzzNote.Off;
        static readonly int[] Scale = { 0x41, 0x44, 0x47, 0x4A, 0x51, 0x4A, 0x47, 0x44 };

        /// <summary>The generated songs of tools/clip-null.py, by name.</summary>
        public static readonly string[] Names =
        {
            "gen/classic-tracks", "gen/tpb1", "gen/tpb4", "gen/tpb8", "gen/rows1", "gen/rows256", "gen/rpb6", "gen/subdivided", "gen/explicit-off",
            "gen/midi-lanes", "gen/midi-lanes-rpb4", "gen/other-machine", "gen/tracks8", "gen/track-change", "gen/session-loop", "gen/chord4",
            "gen/chord4-midi", "gen/pxp-blob",
        };

        /// <summary>A song built: the blob of each machine that has tracker data.</summary>
        public sealed class Song
        {
            public readonly Dictionary<string, byte[]> Blobs = new Dictionary<string, byte[]>();
        }

        static ReBuzzCore Core => Studio.Host.Core;

        static MachineCore Add(string dll, string name, int tracks = -1)
        {
            var song = Core.SongCore;
            song.CreateMachine(dll, null, name, null, null, null, tracks, -0.3f + 0.1f * song.MachinesList.Count, 0.2f);
            var machine = song.MachinesList.Last(m => m.Name == name);
            song.ConnectMachines(machine, song.MachinesList[0], new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            return machine;
        }

        static PatternCore Pattern(MachineCore machine, string name, int ticks)
        {
            machine.CreatePattern(name, ticks);
            return machine.PatternsList.Single(p => p.Name == name);
        }

        /// <summary>The index a column of the editor's data gives a parameter: the globals, then the track parameters.</summary>
        static int Param(MachineCore machine, string name)
        {
            var globals = machine.ParameterGroupsList[1].ParametersList;
            for (int i = 0; i < globals.Count; i++)
                if (globals[i].Name == name) return i;
            var tracks = machine.ParameterGroupsList[2].ParametersList;
            for (int i = 0; i < tracks.Count; i++)
                if (tracks[i].Name == name) return globals.Count + i;
            throw new InvalidOperationException($"{machine.Name} has no parameter {name}: {string.Join(", ", globals.Concat(tracks).Select(p => p.Name))}");
        }

        static List<int> RowTimes(IList<int> beatRows)
        {
            var times = new List<int>();
            for (int b = 0; b < beatRows.Count; b++)
                for (int k = 0; k < beatRows[b]; k++)
                    times.Add(b * Beat + k * Beat / beatRows[b]);
            return times;
        }

        static BlobColumn Col(string machine, int index, int track, IEnumerable<(int Time, int Value)> events, IList<int> beatRows) => new BlobColumn
        {
            Machine = machine,
            ParamIndex = index,
            Track = track,
            Events = events.OrderBy(e => e.Time).Select(e => new PatternEvent(e.Time, e.Value)).ToList(),
            BeatRows = beatRows?.ToList() ?? new List<int>(),
        };

        static BlobPattern Pat(string name, int ticks, int rpb, IEnumerable<BlobColumn> columns) =>
            new BlobPattern { Name = name, Beats = Math.Max(1, (ticks + 3) / 4), RowsPerBeat = rpb, Columns = columns.ToList() };

        /// <summary>The classic tracker columns of a Pedal Tracker (Note, Volume, Cmd, Arg, Cmd2, Arg2 a track): a note every few rows, Offs, volumes and commands between.</summary>
        static List<BlobColumn> TrackerColumns(MachineCore machine, int tracks, IList<int> beatRows, IList<int> noteRows, IList<int> offRows, bool cmd = true, bool volume = true)
        {
            int note = Param(machine, "Note"), vol = Param(machine, "Volume");
            var times = RowTimes(beatRows);
            var cols = new List<BlobColumn>();
            for (int t = 0; t < tracks; t++)
            {
                var notes = new List<(int, int)>(); var vols = new List<(int, int)>();
                var cmds = new List<(int, int)>(); var args = new List<(int, int)>(); var cmds2 = new List<(int, int)>(); var args2 = new List<(int, int)>();
                foreach (int k in noteRows.Where(k => k < times.Count))
                {
                    notes.Add((times[k], Scale[(k / 2 + t) % Scale.Length]));
                    vols.Add((times[k], 96 + (13 * (k + t)) % 100));
                }
                foreach (int k in offRows.Where(k => k < times.Count))
                    notes.Add((times[k], Off));
                if (cmd)
                    for (int j = 0; j < noteRows.Count; j++)
                        if (noteRows[j] < times.Count && j % 2 == 1)
                        {
                            int k = noteRows[j];
                            cmds.Add((times[k], 1 + (j + t) % 6));
                            args.Add((times[k], 8 + 4 * ((j + t) % 5)));
                            cmds2.Add((times[k], 2));
                            args2.Add((times[k], 3 + j % 4));
                        }
                cols.Add(Col(machine.Name, note, t, notes, beatRows));
                if (volume) cols.Add(Col(machine.Name, vol, t, vols, beatRows));
                if (cmd)
                {
                    cols.Add(Col(machine.Name, Param(machine, "Cmd"), t, cmds, beatRows));
                    cols.Add(Col(machine.Name, Param(machine, "Arg"), t, args, beatRows));
                    cols.Add(Col(machine.Name, Param(machine, "Cmd2"), t, cmds2, beatRows));
                    cols.Add(Col(machine.Name, Param(machine, "Arg2"), t, args2, beatRows));
                }
            }
            return cols;
        }

        static Song TrackerSong(int tpb = 4, int rows = 32, int tracks = 2, int rpb = 4, int[] beatRows = null, int[] noteRows = null, int[] offRows = null,
            (string Name, int Rows, int[] Notes, int[] Offs)[] patterns = null, bool cmd = true)
        {
            Core.BPM = 120;
            Core.TPB = tpb;
            var trk = Add("Pedal Tracker", "Trk", tracks);
            patterns ??= new[] { ("A", rows, noteRows ?? Enumerable.Range(0, rows).Where(k => k % 4 == 0).ToArray(), offRows ?? Array.Empty<int>()) };
            var blob = new List<BlobPattern>();
            foreach (var (name, n, notes, offs) in patterns)
            {
                Pattern(trk, name, n);
                int beats = Math.Max(1, (n + 3) / 4);
                var br = beatRows != null ? beatRows.ToList() : Enumerable.Repeat(rpb, beats).ToList();
                blob.Add(Pat(name, n, rpb, TrackerColumns(trk, tracks, br, notes, offs, cmd)));
            }
            var song = new Song();
            song.Blobs[trk.Name] = ClipBlob.WriteMpe(blob);
            return song;
        }

        static Song MidiLanes(int rpb, int rowsPerNote, int cutEvery)
        {
            Core.BPM = 120;
            var lead = Add("Neo Poly", "Lead");
            Pattern(lead, "A", 32);
            var beatRows = Enumerable.Repeat(rpb, 8).ToList();
            var times = RowTimes(beatRows);
            var cols = new List<BlobColumn>();
            for (int t = 0; t < 2; t++)
            {
                var notes = new List<(int, int)>(); var vels = new List<(int, int)>(); var delays = new List<(int, int)>(); var cuts = new List<(int, int)>();
                int j = 0;
                for (int k = 0; k < times.Count; k += rowsPerNote + t, j++)
                {
                    notes.Add((times[k], Scale[(j + 3 * t) % Scale.Length]));
                    vels.Add((times[k], 40 + (29 * (j + t)) % 87));
                    delays.Add((times[k], (7 * j + 11 * t) % 96));
                    if (j % cutEvery == cutEvery - 1) cuts.Add((times[k], 55 + (13 * j) % 41));
                }
                cols.Add(Col("Lead", ClipMigration.MidiNote, t, notes, beatRows));
                cols.Add(Col("Lead", ClipMigration.MidiVelocity, t, vels, beatRows));
                cols.Add(Col("Lead", ClipMigration.MidiNoteDelay, t, delays, beatRows));
                cols.Add(Col("Lead", ClipMigration.MidiNoteCut, t, cuts, beatRows));
            }
            var song = new Song();
            song.Blobs["Lead"] = ClipBlob.WriteMpe(new[] { Pat("A", 32, rpb, cols) });
            return song;
        }

        /// <summary>Builds the song in the open session (a new song is the caller's) and returns the blobs of its pattern editors.</summary>
        public static Song Build(string name)
        {
            switch (name)
            {
                case "gen/classic-tracks": return TrackerSong(tracks: 3, rows: 32);
                case "gen/tpb1": return TrackerSong(tpb: 1, rows: 32, tracks: 2);
                case "gen/tpb4": return TrackerSong(tpb: 4, rows: 32, tracks: 2);
                case "gen/tpb8": return TrackerSong(tpb: 8, rows: 32, tracks: 2);
                case "gen/rows1":
                    return TrackerSong(tracks: 2, patterns: new[] { ("A", 8, new[] { 0, 4 }, Array.Empty<int>()), ("One", 1, new[] { 0 }, Array.Empty<int>()), ("B", 8, new[] { 0, 2, 4, 6 }, Array.Empty<int>()) });
                case "gen/rows256": return TrackerSong(tracks: 2, rows: 256, noteRows: Enumerable.Range(0, 256).Where(k => k % 8 == 0).ToArray());
                case "gen/rpb6": return TrackerSong(tracks: 2, rows: 32, rpb: 6, noteRows: Enumerable.Range(0, 48).Where(k => k % 3 == 0).ToArray());
                case "gen/subdivided": return TrackerSong(tracks: 2, rows: 16, beatRows: new[] { 4, 3, 6, 4 }, noteRows: new[] { 0, 2, 4, 5, 7, 9, 10, 11, 13, 14, 16 });
                case "gen/explicit-off":
                    return TrackerSong(tracks: 2, patterns: new[] { ("A", 16, new[] { 0, 8 }, new[] { 4 }), ("B", 16, new[] { 4, 12 }, new[] { 0, 8 }) });
                case "gen/midi-lanes": return MidiLanes(2, 1, 2);
                case "gen/midi-lanes-rpb4": return MidiLanes(4, 3, 3);
                case "gen/other-machine": return OtherMachine();
                case "gen/tracks8": return TrackerSong(tracks: 8, rows: 32, noteRows: new[] { 0, 4, 8, 12, 16, 20, 24, 28 });
                case "gen/track-change": return TrackChange();
                case "gen/session-loop": return SessionLoop();
                case "gen/chord4": return TrackerSong(tracks: 4, rows: 16, noteRows: new[] { 0, 8 }, cmd: false);
                case "gen/chord4-midi": return Chord4Midi();
                case "gen/pxp-blob": return PxpBlob();
                default: throw new ArgumentException("not a generated song: " + name);
            }
        }

        static Song OtherMachine()
        {
            Core.BPM = 120;
            var lead = Add("Neo Poly", "Lead");
            var filt = Add("Neo Filter", "Filt");
            Pattern(lead, "A", 32);
            var beatRows = Enumerable.Repeat(4, 8).ToList();
            var times = RowTimes(beatRows);
            int cutoff = Param(filt, "Cutoff");
            var all = filt.ParameterGroupsList[1].ParametersList.Concat(filt.ParameterGroupsList[2].ParametersList).ToList();
            var p = all[cutoff];
            var notes = Enumerable.Range(0, 32).Where(k => k % 4 == 0).Select((k, j) => (times[k], Scale[j % Scale.Length])).ToList();
            var vels = Enumerable.Range(0, 32).Where(k => k % 4 == 0).Select(k => (times[k], 100)).ToList();
            var cuts = Enumerable.Range(0, 32).Where(k => k % 2 == 0).Select((k, j) => (times[k], p.MinValue + (p.MaxValue - p.MinValue) * (j % 8) / 8)).ToList();
            var cols = new List<BlobColumn>
            {
                Col("Lead", ClipMigration.MidiNote, 0, notes, beatRows), Col("Lead", ClipMigration.MidiVelocity, 0, vels, beatRows), Col("Filt", cutoff, 0, cuts, beatRows),
            };
            var song = new Song();
            song.Blobs["Lead"] = ClipBlob.WriteMpe(new[] { Pat("A", 32, 4, cols) });
            return song;
        }

        static Song TrackChange()
        {
            Core.BPM = 120;
            var trk = Add("Pedal Tracker", "Trk", 6);
            var a = Pattern(trk, "A", 32);
            var e = Pattern(trk, "E", 32);
            // Engine columns (the agent's notes) on six tracks, then the machine goes down to three.
            int note = trk.ParameterGroupsList[2].ParametersList.FindIndex(p => p.Name == "Note");
            for (int t = 0; t < 6; t++)
            {
                var column = SessionLayout.ColumnFor(e, new ColumnKey(2, note, t), create: true);
                column.SetEvents(new[] { new PatternEvent(t * 120, Scale[t]), new PatternEvent(720 + t * 12, Scale[(t + 2) % Scale.Length]) }, true);
            }
            trk.TrackCount = 3;
            var beatRows = Enumerable.Repeat(4, 8).ToList();
            var cols = TrackerColumns(trk, 6, beatRows, Enumerable.Range(0, 32).Where(k => k % 4 == 0).ToList(), Array.Empty<int>());
            var song = new Song();
            song.Blobs["Trk"] = ClipBlob.WriteMpe(new[] { Pat("A", 32, 4, cols), Pat("E", 32, 4, Array.Empty<BlobColumn>()) });
            return song;
        }

        static Song SessionLoop()
        {
            Core.BPM = 120;
            var lead = Add("Neo Poly", "Lead");
            var loop = Pattern(lead, "Loop", 16);
            var midi = SessionLayout.EnsureMidiColumn(loop);
            midi.SetEvents(new[]
            {
                new PatternEvent(0, ClipEvents.NoteOn(48, 100), 960), new PatternEvent(960, ClipEvents.NoteOn(55, 100), 480),
                new PatternEvent(1920, ClipEvents.NoteOn(64, 100), 960), new PatternEvent(2880, ClipEvents.NoteOn(57, 70), 720),
            }, true);
            var globals = lead.ParameterGroupsList[1].ParametersList;
            int cutoff = globals.FindIndex(p => p.Name == "F1 cutoff"), detune = globals.FindIndex(p => p.Name == "Uni detune");
            if (cutoff >= 0)
                SessionLayout.ColumnFor(loop, new ColumnKey(1, cutoff, 0), create: true).SetEvents(new[] { new PatternEvent(0, 20), new PatternEvent(480, 60) }, true);
            if (detune >= 0)
                SessionLayout.ColumnFor(loop, new ColumnKey(1, detune, 0), create: true).SetEvents(new[] { new PatternEvent(0, 0, 3840), new PatternEvent(3840, 80) }, true);   // a ramp
            return new Song();
        }

        static Song Chord4Midi()
        {
            Core.BPM = 120;
            var lead = Add("Neo Poly", "Lead");
            Pattern(lead, "A", 16);
            var beatRows = Enumerable.Repeat(2, 4).ToList();
            var times = RowTimes(beatRows);
            var cols = new List<BlobColumn>();
            int t = 0;
            foreach (int n in new[] { 0x41, 0x44, 0x47, 0x51 })
            {
                cols.Add(Col("Lead", ClipMigration.MidiNote, t, new[] { (times[0], n), (times[4], n + 2) }, beatRows));
                cols.Add(Col("Lead", ClipMigration.MidiVelocity, t, new[] { (times[0], 90 - 10 * t), (times[4], 100) }, beatRows));
                t++;
            }
            cols.Add(Col("Lead", ClipMigration.MidiNoteCut, 3, new[] { (times[0], 70) }, beatRows));
            var song = new Song();
            song.Blobs["Lead"] = ClipBlob.WriteMpe(new[] { Pat("A", 16, 2, cols) });
            return song;
        }

        static Song PxpBlob()
        {
            Core.BPM = 120;
            var trk = Add("Pedal Tracker", "Trk", 2);
            Pattern(trk, "A", 16);
            int note = Param(trk, "Note"), vol = Param(trk, "Volume");
            var cols = new List<BlobColumn>();
            for (int t = 0; t < 2; t++)
            {
                // PatternXP's version 3 holds rows: a row is 240 of pattern time at 4 rows a beat.
                cols.Add(Col("Trk", note, t, new[] { (0 + t, Scale[t]), (4 + t, Scale[t + 2]), (8, Off), (10, Scale[t + 4]) }.Select(e => (e.Item1 * 240, e.Item2)), null));
                cols.Add(Col("Trk", vol, t, new[] { (0 + t, 120), (4 + t, 160) }.Select(e => (e.Item1 * 240, e.Item2)), null));
            }
            foreach (var (index, track) in new[] { (-10, 0), (-11, 0), (-12, 0), (-110, 0), (-111, 0), (-112, 0), (-110, 1) })
                cols.Add(Col("Trk", index, track, new[] { (2 * 240, 3), (6 * 240, 1) }, null));
            var song = new Song();
            song.Blobs["Trk"] = ClipBlob.WritePxp(new[] { Pat("A", 16, 4, cols) }, out _);
            return song;
        }
    }
}
