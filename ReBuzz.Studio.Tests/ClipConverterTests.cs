// Fork (docs/specs/clips.md §5.2, §5.4, §7 and §10, wave CU2): the converter (ClipMigration), the blob codec (ClipBlob), the export (ClipExport) and
// the engine's store (ClipStore) on small cases made by hand, one rule each; ClipRoundTripTests runs them on the corpus. Pure cases need no engine.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.NUnit;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class ClipConverterTests
    {
        const int TB = PatternEvent.TimeBase, Beat = 4 * TB;
        const int Off = BuzzNote.Off;

        /// <summary>A machine with one global parameter (Level), the track parameters Note and Volume on 3 tracks, and a second machine "Other" with one global.</summary>
        static ClipMachineSet Machines()
        {
            var trk = new ClipMachine { Name = "Trk", GlobalCount = 1, TrackParamCount = 2, TrackCount = 3 };
            trk.NoteTrack.Add(0);
            var other = new ClipMachine { Name = "Other", GlobalCount = 1, TrackParamCount = 0, TrackCount = 1 };
            return new ClipMachineSet().Add(trk).Add(other);
        }

        static BlobColumn Col(string machine, int index, int track, (int Time, int Value)[] events, int[] beatRows = null) => new BlobColumn
        {
            Machine = machine, ParamIndex = index, Track = track, Events = events.Select(e => new PatternEvent(e.Time, e.Value)).ToList(), BeatRows = beatRows?.ToList() ?? new List<int>(),
        };

        static BlobPattern Pat(string name, int beats, int rpb, params BlobColumn[] columns) => new BlobPattern { Name = name, Beats = beats, RowsPerBeat = rpb, Columns = columns.ToList() };

        static ClipMigration.Result Migrate(byte[] blob, ClipLog log, int length = 8) => ClipMigration.FromBlob(blob, Machines(), _ => length, log, "Trk");

        static ClipMigration.Result Migrate(BlobPattern[] patterns, ClipLog log, int length = 8) => Migrate(ClipBlobWriter.WriteMpe(patterns), log, length);

        static ClipLane Lane(ClipMigration.Result r, string key, int clip = 0) => r.Clips[clip].Lanes.SingleOrDefault(l => l.Key == key);

        // ------------------------------------------------------------------ metadata and events

        [AvaloniaTest]
        public void TheGridTextSaysTheRowsOfEachBeat()
        {
            Assert.That(ClipGrid.Format(4, new[] { 4, 4, 4, 6, 4, 4, 4, 3 }), Is.EqualTo("rpb=4;sub=3:6,7:3"), "§5.2's example");
            Assert.That(ClipGrid.Format(4, null), Is.EqualTo("rpb=4"));
            Assert.That(ClipGrid.TryParse("rpb=4;sub=3:6,7:3", out int rpb, out var sub), Is.True);
            Assert.That((rpb, sub[3], sub[7], sub.Count), Is.EqualTo((4, 6, 3, 2)));
            Assert.That(ClipGrid.BeatRows("rpb=4;sub=3:6,7:3", 9, 4), Is.EqualTo(new[] { 4, 4, 4, 6, 4, 4, 4, 3, 4 }));
            Assert.That(ClipGrid.BeatRows(null, 3, 6), Is.EqualTo(new[] { 6, 6, 6 }), "no grid: the clip's rows per beat");
            foreach (var bad in new[] { "", "rpb=0", "rpb=x", "sub=3:6", "rpb=4;sub=3", "rpb=4;sub=a:6", "rpb=4;sub=-1:6" })
                Assert.That(ClipGrid.TryParse(bad, out _, out _), Is.False, bad);
        }

        [AvaloniaTest]
        public void AnExplicitNoteOffIsADurationZeroOffMessageAndANoteOnKeepsItsLength()
        {
            var off = ClipEvents.Off(480, 60);
            Assert.That((off.Time, off.Duration, off.Value & 0xF0, (off.Value >> 8) & 0x7F), Is.EqualTo((480, 0, 0x80, 60)));
            Assert.That(ClipEvents.IsExplicitOff(off), Is.True);
            var on = new PatternEvent(0, ClipEvents.NoteOn(60, 100), 240);
            Assert.That(ClipEvents.IsExplicitOff(on), Is.False);
            Assert.That(ClipEvents.IsNoteOn(on), Is.True);
            Assert.That(ClipEvents.NoteOff(60), Is.EqualTo(0x80 | (60 << 8)), "as the Session View's own note-off");
        }

        // ------------------------------------------------------------------ the blob codec

        [AvaloniaTest]
        public void TheModernEditorsDataReadsBackWhatItWasWrittenWith()
        {
            var p = Pat("A", 2, 4, Col("Trk", 0, 0, new[] { (0, 5), (240, 7) }, new[] { 4, 3 }), Col("Trk", 1, 2, new[] { (480, 0x41) }, new[] { 4, 3 }));
            var blob = ClipBlobWriter.WriteMpe(new[] { p, Pat("B", 1, 6) });
            var back = ClipBlob.Parse(blob);
            Assert.That(back.Problem, Is.Null);
            Assert.That(back.Format, Is.EqualTo(BlobFormat.Mpe1));
            Assert.That(back.Patterns.Select(x => (x.Name, x.Beats, x.RowsPerBeat, x.Columns.Count)), Is.EqualTo(new[] { ("A", 2, 4, 2), ("B", 1, 6, 0) }));
            var c = back.Patterns[0].Columns[0];
            Assert.That((c.Machine, c.ParamIndex, c.Track, c.BeatRows), Is.EqualTo(("Trk", 0, 0, new List<int> { 4, 3 })));
            Assert.That(c.Events.Select(e => (e.Time, e.Value)), Is.EqualTo(new[] { (0, 5), (240, 7) }));
        }

        [AvaloniaTest]
        public void PatternXPsDataOfEveryVersionReadsAsRowsAtThePatternsRowsPerBeat()
        {
            static byte[] Pxp(byte version, int rpb)
            {
                var d = new List<byte> { version };
                d.AddRange(BitConverter.GetBytes(1));
                d.AddRange(new byte[] { (byte)'A', 0 });
                if (version > 1) d.AddRange(BitConverter.GetBytes(rpb));
                d.AddRange(BitConverter.GetBytes(1));
                d.AddRange(new byte[] { (byte)'T', 0 });
                d.AddRange(BitConverter.GetBytes(1)); d.AddRange(BitConverter.GetBytes(0));
                if (version >= 3) d.Add(0);
                d.AddRange(BitConverter.GetBytes(2));
                foreach (var (row, value) in new[] { (0, 9), (5, 8) }) { d.AddRange(BitConverter.GetBytes(row)); d.AddRange(BitConverter.GetBytes(value)); }
                return d.ToArray();
            }
            foreach (var (version, rpb, format) in new[] { ((byte)1, 4, BlobFormat.Pxp1), ((byte)2, 6, BlobFormat.Pxp2), ((byte)3, 6, BlobFormat.Pxp3) })
            {
                var blob = ClipBlob.Parse(Pxp(version, rpb));
                Assert.That(blob.Problem, Is.Null, $"version {version}");
                Assert.That(blob.Format, Is.EqualTo(format));
                Assert.That(blob.Patterns[0].RowsPerBeat, Is.EqualTo(version == 1 ? 4 : rpb));
                var c = blob.Patterns[0].Columns[0];
                Assert.That((c.Machine, c.ParamIndex, c.Track), Is.EqualTo(("T", 1, 0)));
                Assert.That(c.Events.Select(e => e.Time), Is.EqualTo(new[] { 0, 5 * TB * 4 / (version == 1 ? 4 : rpb) }), "a row is a beat over the rows per beat: the editor's own rule");
            }
        }

        [AvaloniaTest]
        public void NoPrefixOfABlobAndNoJunkMakesTheReaderThrow()
        {
            var blob = ClipBlobWriter.WriteMpe(new[] { Pat("A", 2, 4, Col("Trk", 0, 0, new[] { (0, 5) }, new[] { 4, 4 })) });
            for (int n = 0; n <= blob.Length; n++)
                Assert.DoesNotThrow(() => ClipBlob.Parse(blob.Take(n).ToArray()), $"a blob cut at {n}");
            var rnd = new Random(7);
            for (int i = 0; i < 300; i++)
            {
                var junk = new byte[rnd.Next(1, 80)];
                rnd.NextBytes(junk);
                junk[0] = (byte)(i % 5 == 0 ? 255 : 1 + i % 3);
                if (junk[0] == 255 && junk.Length > 1) junk[1] = 1;
                Assert.DoesNotThrow(() => ClipBlob.Parse(junk));
            }
            Assert.That(ClipBlob.Parse(null).Patterns, Is.Empty);
            Assert.That(ClipBlob.Parse(new byte[0]).Patterns, Is.Empty);
            Assert.That(ClipBlob.Parse(new byte[] { 9, 9, 9 }).Problem, Is.Not.Null, "another editor's data says so");
        }

        // ------------------------------------------------------------------ migration

        [AvaloniaTest]
        public void ParameterColumnsBecomeLanesWithTheSameEventsAndTheirGrid()
        {
            var log = new ClipLog();
            var r = Migrate(new[] { Pat("A", 2, 4,
                Col("Trk", 0, 0, new[] { (0, 100), (121, 50) }, new[] { 4, 3 }),                 // Level, global, an event between ticks
                Col("Trk", 1, 1, new[] { (0, 0x41), (240, 0x44), (480, Off) }, new[] { 4, 3 }), // Note, track 1
                Col("Other", 0, 0, new[] { (240, 7) }, new[] { 4, 3 })) }, log);
            Assert.That(r.Conserved && r.EventsIn == 6 && r.EventsKept == 6, Is.True);
            var level = Lane(r, "Trk|1.0.0");
            Assert.That(level.Events.Select(e => (e.Time, e.Value, e.Duration)), Is.EqualTo(new[] { (0, 100, 0), (121, 50, 0) }));
            Assert.That(level.Grid, Is.EqualTo("rpb=4;sub=1:3"));
            Assert.That(level.Mute, Is.False);
            Assert.That(Lane(r, "Trk|2.0.1").Events.Select(e => e.Value), Is.EqualTo(new[] { 0x41, 0x44, Off }));
            Assert.That(Lane(r, "Other|1.0.0").Events.Count, Is.EqualTo(1), "a column of another machine is a lane of that machine");
            Assert.That(log.Entries.Where(e => e.Tag == ClipTags.BlockShift).Sum(e => e.Count), Is.EqualTo(6), "every parameter event moves from the editor's block to the engine's");
            Assert.That(log.Entries.Where(e => e.Tag == ClipTags.Subtick).Sum(e => e.Count), Is.EqualTo(1), "the event at 121 is between ticks");
        }

        [AvaloniaTest]
        public void ANoteValueTheEditorCouldNotReadIsReadAsAMidiNoteAsTheEditorDid()
        {
            var log = new ClipLog();
            var r = Migrate(new[] { Pat("A", 2, 4, Col("Trk", 1, 0, new[] { (0, 30), (240, 0x41), (480, 255) }, new[] { 4, 4 })) }, log);
            Assert.That(Lane(r, "Trk|2.0.0").Events.Select(e => e.Value), Is.EqualTo(new[] { BuzzNote.FromMIDINote(30), 0x41, Off }));
            Assert.That(log.Entries.Single(e => e.Tag == ClipTags.NoteFixed).Count, Is.EqualTo(1));
        }

        [AvaloniaTest]
        public void MidiLanesBecomeTheNotesLaneWithDelayCutAndVelocity()
        {
            // 2 rows a beat: a row is 480. Track 0: a note on row 0 with velocity 90, delay 48/95 of the row and a cut at 71/95; a note on row 1 with no velocity (100), ringing on.
            var rows = new[] { 2, 2, 2, 2 };
            var log = new ClipLog();
            var r = Migrate(new[] { Pat("A", 4, 2,
                Col("Trk", ClipMigration.MidiNote, 0, new[] { (0, 0x41), (480, 0x44) }, rows),
                Col("Trk", ClipMigration.MidiVelocity, 0, new[] { (0, 90) }, rows),
                Col("Trk", ClipMigration.MidiNoteDelay, 0, new[] { (0, 48) }, rows),
                Col("Trk", ClipMigration.MidiNoteCut, 0, new[] { (0, 71) }, rows)) }, log, length: 4);
            var notes = Lane(r, "Trk|notes");
            Assert.That(notes.Kind, Is.EqualTo(ClipLaneKind.Notes));
            int delay = TrackerNotes.FromFraction(48, 480), cut = TrackerNotes.FromFraction(71, 480);
            Assert.That(notes.Events.Select(e => (e.Time, e.Value, e.Duration)), Is.EqualTo(new[]
            {
                (delay, ClipEvents.NoteOn(48, 90), cut - delay),                                  // C-4 is MIDI 48
                (480, ClipEvents.NoteOn(51, 100), 4 * TB - 480),                                  // to the end of the clip
            }));
            Assert.That(r.Conserved, Is.True);
            Assert.That(r.EventsIn, Is.EqualTo(5));
            Assert.That(r.EventsMerged, Is.EqualTo(5), "the five lane events became two notes");
            var tags = log.ByTag();
            Assert.That(tags[ClipTags.MidiDelay].Events, Is.EqualTo(1));
            Assert.That(tags[ClipTags.MidiCut].Events, Is.EqualTo(1));
            Assert.That(tags[ClipTags.MidiTick].Events, Is.EqualTo(2));
            Assert.That(r.Clips[0].Lanes.Count(l => l.Kind == ClipLaneKind.Parameter), Is.EqualTo(0), "the MIDI lanes are not parameter lanes");
        }

        [AvaloniaTest]
        public void AnOffWithNoNoteBeforeItInTheClipIsAnExplicitNoteOffThatNamesTheNoteRingingFromTheClipBefore()
        {
            var rows = new[] { 4, 4 };
            var log = new ClipLog();
            var r = Migrate(new[]
            {
                Pat("A", 2, 4, Col("Trk", ClipMigration.MidiNote, 0, new[] { (0, 0x41), (480, 0x44) }, rows)),                            // ends ringing: D#... 0x44 is MIDI 51... on to the end
                Pat("B", 2, 4, Col("Trk", ClipMigration.MidiNote, 0, new[] { (240, Off), (480, 0x47) }, rows), Col("Trk", ClipMigration.MidiNote, 1, new[] { (240, Off) }, rows)),
            }, log);
            var b = Lane(r, "Trk|notes", 1);
            var offs = b.Events.Where(ClipEvents.IsExplicitOff).ToList();
            Assert.That(offs.Count, Is.EqualTo(2), "one off a voice that has no note before it in the clip");
            Assert.That(offs.Select(e => (e.Time, (e.Value >> 8) & 0x7F)), Is.EquivalentTo(new[] { (240, 51), (240, 0) }), "track 0's off names the note ringing from clip A; track 1 had none: note 0");
            Assert.That(b.Events.Count(ClipEvents.IsNoteOn), Is.EqualTo(1));
            Assert.That(log.ByTag()[ClipTags.OffExplicit].Events, Is.EqualTo(2));
            Assert.That(log.ByTag()[ClipTags.OffNoteUnknown].Events, Is.EqualTo(1));
            Assert.That(r.Conserved, Is.True);
        }

        [AvaloniaTest]
        public void AnOffAfterANoteInTheSameClipEndsTheNoteAndIsNoEvent()
        {
            var rows = new[] { 4 };
            var r = Migrate(new[] { Pat("A", 1, 4, Col("Trk", ClipMigration.MidiNote, 0, new[] { (0, 0x41), (480, Off) }, rows)) }, new ClipLog(), length: 4);
            var notes = Lane(r, "Trk|notes");
            Assert.That(notes.Events.Select(e => (e.Time, e.Duration)), Is.EqualTo(new[] { (0, 480) }));
            Assert.That(notes.Events.Any(ClipEvents.IsExplicitOff), Is.False);
        }

        [AvaloniaTest]
        public void WhatDoesNotConvertIsDroppedWithOneLogLineEachAndEveryEventIsAccountedFor()
        {
            var rows = new[] { 4, 4 };
            var log = new ClipLog();
            var r = Migrate(new[] { Pat("A", 2, 4,
                Col("Trk", 1, 0, new[] { (0, 0x41) }, rows),                                  // kept
                Col("Trk", -10, 0, new[] { (240, 3), (480, 1) }, rows),                       // PatternXP's global trigger
                Col("Trk", -111, 1, new[] { (240, 3) }, rows),                                // its track effect
                Col("Nobody", 0, 0, new[] { (0, 1), (240, 2), (480, 3) }, rows),              // a machine the song lacks
                Col("Trk", 9, 0, new[] { (0, 1) }, rows),                                     // a parameter the machine lacks
                Col("Trk", 1, 7, new[] { (0, 0x41), (240, 0x44) }, rows),                     // a track the machine lacks (it has 3)
                Col("Trk", -50, 0, new[] { (0, 1) }, rows),                                   // an editor's internal column we do not know
                Col("Trk", ClipMigration.MidiNote, 0, new[] { (0, 0x41) }, rows),
                Col("Trk", ClipMigration.MidiPitchWheel, 0, new[] { (0, 100), (240, 200) }, rows),    // on a track with a note lane: dropped
                Col("Trk", ClipMigration.MidiCC, 0, new[] { (0, 0x0105) }, rows),                     // ... too
                Col("Trk", ClipMigration.MidiPitchWheel, 1, new[] { (0, 0x2000) }, rows),             // on a track with none: controller events
                Col("Trk", ClipMigration.MidiCC, 1, new[] { (0, 0x0705) }, rows),
                Col("Trk", ClipMigration.MidiVelocity, 2, new[] { (0, 77) }, rows),                   // a velocity of no note
                Col("Trk", 1, 0, new[] { (2000, 0x41) }, rows)) }, log);               // past the end of the clip (8 ticks = 1920)
            var tags = log.ByTag();
            Assert.That(tags[ClipTags.PxpSpDropped], Is.EqualTo((2, 3)), "one line a column");
            Assert.That(tags[ClipTags.MachineMissing], Is.EqualTo((1, 3)));
            Assert.That(tags[ClipTags.ParamMissing], Is.EqualTo((1, 1)));
            Assert.That(tags[ClipTags.TrackDropped], Is.EqualTo((1, 2)));
            Assert.That(tags[ClipTags.ForeignDropped], Is.EqualTo((1, 1)));
            Assert.That(tags[ClipTags.CcLaneDropped], Is.EqualTo((2, 3)), "a pitch wheel and a CC lane on a track with a note lane");
            Assert.That(tags[ClipTags.MidiLaneOrphan], Is.EqualTo((1, 1)));
            Assert.That(tags[ClipTags.EventPastEnd], Is.EqualTo((1, 1)));
            Assert.That(tags[ClipTags.CcLaneConverted].Events, Is.EqualTo(2));
            Assert.That(log.Entries.Where(e => ClipTags.Losses.Contains(e.Tag)).All(e => e.Kind == ClipLogKind.Loss), Is.True);
            Assert.That(r.Conserved, Is.True, $"in {r.EventsIn} kept {r.EventsKept} merged {r.EventsMerged} dropped {r.EventsDropped}");
            Assert.That(r.EventsIn, Is.EqualTo(19));
            var notes = Lane(r, "Trk|notes");
            Assert.That(notes.Events.Select(e => e.Value & 0xF0), Is.EquivalentTo(new[] { 0x90, 0xE0, 0xB0 }), "a note, a pitch wheel and a control change");
            Assert.That(Lane(r, "Trk|2.0.0").Events.Count, Is.EqualTo(1), "the kept column");
        }

        [AvaloniaTest]
        public void DataOfAnotherEditorIsDroppedWithALineAndATruncatedBlobKeepsWhatItHas()
        {
            var log = new ClipLog();
            var r = Migrate(new byte[] { 9, 1, 2, 3 }, log);
            Assert.That(r.Clips, Is.Empty);
            Assert.That(log.Entries.Single().Tag, Is.EqualTo(ClipTags.ForeignDropped));

            var good = ClipBlobWriter.WriteMpe(new[] { Pat("A", 2, 4, Col("Trk", 1, 0, new[] { (0, 0x41) }, new[] { 4, 4 })), Pat("B", 2, 4, Col("Trk", 1, 0, new[] { (0, 0x44) }, new[] { 4, 4 })) });
            var log2 = new ClipLog();
            var r2 = Migrate(good.Take(good.Length - 9).ToArray(), log2);
            Assert.That(r2.Clips.Count, Is.EqualTo(1).Or.EqualTo(2));
            Assert.That(log2.Entries.Any(e => e.Tag == ClipTags.ForeignDropped), Is.True, "the data ends early, and it says so");
        }

        [AvaloniaTest]
        public void ABuiltInPatternsRowsBecomeLanesAtRowsTimes240AndItsInputColumnsAreDropped()
        {
            var trk = Machines().Find("Trk");
            var rows = new BuiltInRows
            {
                Name = "P", Machine = "Trk", Rows = 4,
                Global = new[] { new[] { 100 }, new[] { 255 }, new[] { 255 }, new[] { 30 } },
                Tracks = new[] { new[] { new[] { 0x41, 5 }, new[] { 0, 0 }, new[] { Off, 0 }, new[] { 0, 0 } }, new[] { new[] { 0x44, 0 }, new[] { 0, 0 }, new[] { 0, 0 }, new[] { 0, 0 } } },
                GlobalNoValue = new[] { 255 }, TrackNoValue = new[] { 0, 0 },
                InputCells = { ["Gen"] = 12 },
            };
            var log = new ClipLog();
            var result = new ClipMigration.Result();
            var clip = ClipMigration.FromBuiltIn(rows, trk, 4, log, result);
            Assert.That(clip.Lanes.Single(l => l.Key == "Trk|1.0.0").Events.Select(e => (e.Time, e.Value)), Is.EqualTo(new[] { (0, 100), (3 * TB, 30) }));
            Assert.That(clip.Lanes.Single(l => l.Key == "Trk|2.0.0").Events.Select(e => (e.Time, e.Value)), Is.EqualTo(new[] { (0, 0x41), (2 * TB, Off) }));
            Assert.That(clip.Lanes.Single(l => l.Key == "Trk|2.1.0").Events.Select(e => (e.Time, e.Value)), Is.EqualTo(new[] { (0, 5) }));
            Assert.That(clip.Lanes.Single(l => l.Key == "Trk|2.0.1").Events.Count, Is.EqualTo(1));
            Assert.That(clip.Lanes.All(l => l.Grid == "rpb=4"), Is.True);
            Assert.That(log.ByTag()[ClipTags.InputColumn], Is.EqualTo((1, 12)), "a connection's amp and pan are not converted, and the log says how many cells");
            Assert.That(result.Conserved && result.EventsIn == 6 + 12 && result.EventsDropped == 12, Is.True);
        }

        static MachineCore AddPoly()
        {
            var song = Studio.Host.Core.SongCore;
            song.CreateMachine("Neo Poly", null, "Lead", null, null, null, -1, 0.1f, 0.2f);
            return song.MachinesList.Last(m => m.Name == "Lead");
        }

        [AvaloniaTest]
        public void GridMuteAndAnExplicitNoteOffSurviveASaveInEveryFormat([Values(".nbl")] string extension)
        {
            var core = Studio.Host.Core;
            AudioHygieneTests.NewSong();
            var lead = AddPoly();
            lead.CreatePattern("A", 16);
            var pattern = lead.PatternsList.Single(p => p.Name == "A");
            var globals = lead.ParameterGroupsList[1].ParametersList;
            var notes = new ClipLane { Kind = ClipLaneKind.Notes, Machine = "Lead", Grid = "rpb=4;sub=1:3,2:6" };
            notes.Events.Add(new PatternEvent(0, ClipEvents.NoteOn(60, 100), 480));
            notes.Events.Add(ClipEvents.Off(1200, 62));
            var level = new ClipLane { Kind = ClipLaneKind.Parameter, Machine = "Lead", Group = 1, Index = 0, Track = 0, Grid = "rpb=4;sub=3:6,7:3", Mute = true };
            level.Events.Add(new PatternEvent(240, globals[0].MinValue + 1));
            var clip = new ClipContent { Name = "A", Length = 16, Lanes = { notes, level } };
            Assert.That(ClipStore.Write(pattern, clip, core.SongCore, new ClipLog()), Is.EqualTo(2));

            string path = TestTemp.Path($"clip-cu2-{Environment.ProcessId}{extension}");
            try
            {
                core.SaveSongCopy(path);
                core.Modified = false;
                core.OpenSongFile(path);
                var back = ClipStore.Read(core.SongCore.MachinesList.Single(m => m.Name == "Lead").PatternsList.Single(p => p.Name == "A"));
                var n = back.Lanes.Single(l => l.Kind == ClipLaneKind.Notes);
                Assert.That(n.Grid, Is.EqualTo("rpb=4;sub=1:3,2:6"));
                Assert.That(n.Events.Select(e => (e.Time, e.Value, e.Duration)), Is.EquivalentTo(new[] { (0, ClipEvents.NoteOn(60, 100), 480), (1200, ClipEvents.NoteOff(62), 0) }),
                    "the explicit off keeps its duration 0 and its message");
                var l = back.Lanes.Single(x => x.Kind == ClipLaneKind.Parameter);
                Assert.That((l.Grid, l.Mute, l.Events.Count), Is.EqualTo(("rpb=4;sub=3:6,7:3", true, 1)));
                Assert.That(back.Lanes.Count, Is.EqualTo(2), "nothing else appeared");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [AvaloniaTest]
        public void WritingLanesChangesNothingTheEngineHearsButTheNotesLaneAndMarksNoParameterColumn()
        {
            var core = Studio.Host.Core;
            AudioHygieneTests.NewSong();
            var lead = AddPoly();
            lead.CreatePattern("A", 16);
            var pattern = lead.PatternsList.Single(p => p.Name == "A");
            var level = new ClipLane { Kind = ClipLaneKind.Parameter, Machine = "Lead", Group = 1, Index = 0, Track = 0 };
            level.Events.Add(new PatternEvent(0, lead.ParameterGroupsList[1].ParametersList[0].MinValue));
            ClipStore.Write(pattern, new ClipContent { Name = "A", Length = 16, Lanes = { level } }, core.SongCore, new ClipLog());
            var column = pattern.ColumnsList.Single();
            Assert.That(column.Metadata.ContainsKey(PatternColumnCore.SessionAutomationKey), Is.False, "no session mark: the engine does not play a migrated parameter column (CU3 does)");
            Assert.That(ClipMeta.SessionAutomation, Is.EqualTo(PatternColumnCore.SessionAutomationKey));
            Assert.That(((PatternColumnCore)column).IsMarkedAutomation, Is.False);
        }
    }
}
