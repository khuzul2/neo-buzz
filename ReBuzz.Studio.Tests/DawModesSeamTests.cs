// Fork (docs/specs/daw-modes.md AR0): the engine seams the arrangement, piano roll and tracker
// views will use, on the studio's engine (Linux CI runs these): S1 the song meter, S2 clip ids on
// sequence events, S3 automation ramps, S4 note sources; and the exit check, a song with clips,
// loops, ramps and a meter that round-trips in .bmx and .bmxml with its core sections unchanged.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Headless.NUnit;
using BuzzGUI.Common;
using BuzzGUI.Common.Actions.SequenceActions;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Sequence;

namespace ReBuzz.Studio.Tests
{
    public class DawModesSeamTests
    {
        const int TB = PatternEvent.TimeBase;

        static string Work(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"rebuzz-daw-modes-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        /// <summary>Saves the song to a file and opens it again, as File ▸ Save as and Open do.</summary>
        static void SaveAndOpen(SessionRig rig, string path)
        {
            rig.Core.SaveSongCopy(path);
            rig.Core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            rig.Core.OpenSongFile(path);
            rig.Settle();
        }

        static SequenceCore Track(SessionRig rig, string machine) => rig.Core.SongCore.SequencesList.First(s => s.Machine.Name == machine);

        // ------------------------------------------------------------------ S1: the song meter

        [AvaloniaTest]
        public void TheSequenceEditorsBarsAreTheEnginesSongMeterAndSongFilesKeepThem([Values(".bmx", ".bmxml")] string extension)
        {
            using var rig = new SessionRig("Lead");
            var meter = rig.Core.SongMeter;
            Assert.That(SequenceSongState.For(rig.Core).TimeSignatures, Is.SameAs(meter), "the studio's sequence editor edits the engine's list");
            Assert.That(meter.IsDefault, Is.True, "a new song: Buzz's bars of 16 ticks");

            meter.Set(0, 12);
            meter.Set(48, 16);
            var bars = meter.Entries.ToDictionary(e => e.Key, e => e.Value);
            Assert.That(bars, Is.EqualTo(new Dictionary<int, int> { [0] = 12, [48] = 16 }));
            Assert.That(rig.Session.BeatsPerBar, Is.EqualTo(3), "without a layout of its own, the session's bar is the song's first bar (12 ticks at 4 a beat)");

            SaveAndOpen(rig, Work("meter" + extension));
            Assert.That(meter.Entries, Is.EqualTo(bars), "the bars come back with the song");
            Assert.That(rig.Session.BeatsPerBar, Is.EqualTo(3));

            rig.Core.Modified = false;
            rig.Core.ExecuteCommand(BuzzCommand.NewFile);
            rig.Settle();
            Assert.That(meter.IsDefault, Is.True, "a new song has the default bars");
            Assert.That(rig.Session.BeatsPerBar, Is.EqualTo(4));
        }

        [AvaloniaTest]
        public void ASavedSessionLayoutKeepsItsOwnBar()
        {
            using var rig = new SessionRig("Lead");
            rig.Layout.SetBeatsPerBar(5);
            rig.Core.SongMeter.Set(0, 12);
            Assert.That(rig.Session.BeatsPerBar, Is.EqualTo(5), "the session's own bar, set in the Session View, stays");
            SaveAndOpen(rig, Work("own-bar.bmx"));
            Assert.That(rig.Session.BeatsPerBar, Is.EqualTo(5));
            Assert.That(rig.Core.SongMeter.Entries[0], Is.EqualTo(12));
        }

        [AvaloniaTest]
        public void TheSessionBarFromTheMeterNeedsWholeBeats()
        {
            var meter = new SongMeter();
            Assert.That(meter.SessionBeatsPerBar(4), Is.EqualTo(4));
            meter.Set(0, 12);
            Assert.That(meter.SessionBeatsPerBar(4), Is.EqualTo(3));
            Assert.That(meter.SessionBeatsPerBar(8), Is.EqualTo(4), "12 ticks at 8 a beat isn't whole beats: as before");
            meter.Set(0, 16);
            Assert.That(meter.IsDefault, Is.True);
            Assert.That(meter.SessionBeatsPerBar(8), Is.EqualTo(4), "Buzz's untouched default keeps 4 beats at any ticks per beat");
            var copy = new SongMeter();
            copy.ReadBytes(new SongMeter(meter).ToBytes());
            Assert.That(copy.SameAs(meter), Is.True);
        }

        // ------------------------------------------------------------------ S2: clip ids on events

        [AvaloniaTest]
        public void ClipIdsAndLoopsSurviveEditsUndoAndSongFiles([Values(".bmx", ".bmxml")] string extension)
        {
            using var rig = new SessionRig("Lead");
            var lead = rig.Machine("Lead");
            var verse = rig.Pattern(lead, "Verse", 8, (0, 60), (4, 64));
            var track = Track(rig, "Lead");
            var clips = rig.Core.Clips;
            int id = clips.NewId();
            clips.SetLoop(id, new ClipLoop(2, 6, true));
            // A clip: from tick 1 to the loop end, then the loop twice, the last repeat cut at 16.
            track.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, verse) { Offset = 1, End = 6, Clip = id });
            track.SetEvent(5, new SequenceEvent(SequenceEventType.PlayPattern, verse) { Offset = 2, End = 6, Clip = id });
            track.SetEvent(9, new SequenceEvent(SequenceEventType.PlayPattern, verse) { Offset = 2, End = 6, Clip = id });
            track.SetEvent(12, new SequenceEvent(SequenceEventType.Break));
            track.SetEvent(16, new SequenceEvent(SequenceEventType.PlayPattern, verse));          // a plain event: a clip of its own

            var read = clips.Of(track);
            Assert.That(read.Select(c => (c.Id, c.Time, c.Length, c.Offset, c.Events)), Is.EqualTo(new[] { (id, 0, 12, 1, 3), (0, 16, 8, 0, 1) }));
            Assert.That(read[0].Loop, Is.EqualTo(new ClipLoop(2, 6, true)));
            Assert.That(new SequenceEvent(track.Events[5]).Clip, Is.EqualTo(id), "the copy constructor keeps it");

            // The editors' actions keep it: BuzzGUI.Common's clear and its undo, the studio's move and its clipboard.
            var clear = new ClearAction(track, 0, 16);
            clear.Do();
            Assert.That(track.Events.Keys, Is.EqualTo(new[] { 16 }));
            clear.Undo();
            Assert.That(track.Events.Where(e => e.Value.Type == SequenceEventType.PlayPattern && e.Key < 16).Select(e => e.Value.Clip), Is.All.EqualTo(id));
            var edits = new SequenceEditing(rig.Core.Song, rig.Core.SongCore.ActionStack, rig.Core.SongMeter, new SequenceClipboard());
            Assert.That(edits.MoveEvent(0, 16, 32, clone: false), Is.True);
            Assert.That(track.Events[32].Clip, Is.EqualTo(0));
            var board = new SequenceClipboard();
            board.Copy(rig.Core.Song, new SequenceRect(0, 0, 16, 1));
            board.Paste(rig.Core.Song, 48, 0);
            Assert.That(track.Events[53].Clip, Is.EqualTo(id), "paste keeps the clip (AR1 gives copies their own)");
            rig.Core.SongCore.ActionStack.Undo();
            Assert.That(track.Events.ContainsKey(16) && !track.Events.ContainsKey(32), Is.True, "undo of the move");

            SaveAndOpen(rig, Work("clips" + extension));
            track = Track(rig, "Lead");
            var e5 = track.Events[5];
            Assert.That((e5.Offset, e5.End, e5.Clip), Is.EqualTo((2, 6, id)));
            Assert.That(track.Events[16].Clip, Is.EqualTo(0));
            Assert.That(rig.Core.Clips.Of(track)[0].Loop, Is.EqualTo(new ClipLoop(2, 6, true)), "the loop comes back");
            Assert.That(rig.Core.Clips.NewId(), Is.GreaterThan(id), "new ids follow the song's");
        }

        [AvaloniaTest]
        public void PlainArrangementsStillSaveNoArrangementEvents()
        {
            using var rig = new SessionRig("Lead");
            var verse = rig.Pattern(rig.Machine("Lead"), "Verse", 8);
            Track(rig, "Lead").SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, verse));
            Assert.That(ArrangementEventStore.ToXml(rig.Core.SongCore.Sequences, rig.Core.Clips), Is.Null);
        }

        [AvaloniaTest]
        public void SceneToArrangementWritesEachTracksClipWithItsLoop()
        {
            using var rig = new SessionRig("Lead", "Bass");
            var a = rig.Pattern(rig.Machine("Lead"), "a", 8, (0, 60));
            var b = rig.Pattern(rig.Machine("Bass"), "b", 12, (0, 40));
            var layout = rig.Layout;
            layout.Ensure();
            var c = layout.ClipOf(a);
            c.Start = 2;
            c.LoopStart = 4;
            c.LoopEnd = 7;
            layout.SetClipSettings(a, c);

            SceneToArrangement.Insert(rig.Core, layout, 0, 0, 16, insert: false);

            var lead = Track(rig, "Lead");
            var bass = Track(rig, "Bass");
            var leadClips = rig.Core.Clips.Of(lead);
            Assert.That(leadClips.Count, Is.EqualTo(1), "the start and its repeats are one clip");
            Assert.That((leadClips[0].Time, leadClips[0].Length, leadClips[0].Offset, leadClips[0].Events), Is.EqualTo((0, 16, 2, 5)), "the start (2-7), then the loop (4-7) four times, the last cut at 16");
            Assert.That(leadClips[0].Loop, Is.EqualTo(new ClipLoop(4, 7, true)));
            var bassClips = rig.Core.Clips.Of(bass);
            Assert.That(bassClips.Count, Is.EqualTo(1));
            Assert.That(bassClips[0].Id, Is.Not.EqualTo(leadClips[0].Id), "a clip id per track");
            Assert.That(bassClips[0].Loop, Is.EqualTo(new ClipLoop(0, 0, true)), "the whole pattern loops");

            layout.Undo();
            Assert.That(lead.Events, Is.Empty);
        }

        [AvaloniaTest]
        public void RecordingWritesALaunchAndItsLoopWrapsAsOneClip()
        {
            using var rig = new SessionRig("Lead");
            var a = rig.Pattern(rig.Machine("Lead"), "a", 4, (0, 60));
            var track = Track(rig, "Lead");
            rig.Session.LaunchQuantization = LaunchQuantization.None;
            rig.Core.Looping = false;
            rig.Core.SongCore.SongEnd = 64;

            rig.Core.Recording = true;
            rig.Session.Launch(track, a);
            rig.RunTicks(14);                                // the launch and three wraps
            rig.Session.Launch(track, a);                    // launched again: a clip of its own
            rig.RunTicks(6);
            rig.Core.Recording = false;
            rig.RunTicks(2);
            rig.Core.Playing = false;
            rig.Settle();

            var played = track.Events.OrderBy(e => e.Key).Where(e => e.Value.Type == SequenceEventType.PlayPattern).ToList();
            Assert.That(played.Count, Is.GreaterThanOrEqualTo(5), string.Join(" ", track.Events.Select(e => $"{e.Key}:{e.Value.Type}/{e.Value.Clip}")));
            Assert.That(played.All(e => e.Value.Clip != 0), Is.True, "recorded starts carry clip ids");
            var clips = rig.Core.Clips.Of(track);
            Assert.That(clips.Count, Is.EqualTo(2), "the launch with its wraps, then the relaunch: " + string.Join(", ", clips.Select(c => $"{c.Id}@{c.Time}x{c.Events}")));
            Assert.That(clips[0].Events, Is.GreaterThanOrEqualTo(3));
            Assert.That(clips[0].Id, Is.Not.EqualTo(clips[1].Id));
            Assert.That(clips[0].Loop, Is.EqualTo(new ClipLoop(0, 0, true)), "the session clip's loop");
        }

        // ------------------------------------------------------------------ S3: automation ramps

        static (PatternCore Pattern, IParameter Parameter, IPatternColumn Column) RampSong(SessionRig rig, bool ramp, int at = 0)
        {
            var lead = rig.Machine("Lead");
            var p = rig.Pattern(lead, "Sweep", 16, (0, 48));
            var parameter = lead.ParameterGroups[1].Parameters.First(x => x.Flags.HasFlag(ParameterFlags.State) && x.MaxValue - x.MinValue >= 64);
            var column = SessionLayout.ColumnFor(p, new ColumnKey(1, parameter.IndexInGroup, 0), create: true);
            column.SetEvents(new[] { new PatternEvent(0, parameter.MinValue, ramp ? 8 * TB : 0), new PatternEvent(8 * TB, parameter.MinValue + 64, 0) }, true);
            Track(rig, "Lead").SetEvent(at, new SequenceEvent(SequenceEventType.PlayPattern, p));
            rig.Core.SongCore.SongEnd = at + 16;
            rig.Core.SongCore.LoopEnd = at + 16;
            return (p, parameter, column);
        }

        static List<int> ValuesPerTick(SessionRig rig, IParameter parameter, int ticks)
        {
            parameter.SetValue(1 << 16, parameter.MinValue);      // the column's first value, before it plays
            rig.Core.SongCore.PlayPosition = 0;
            rig.Core.Playing = true;
            var values = new List<int>();
            for (int t = 0; t < ticks; t++)
            {
                rig.RunTicks(1);
                values.Add(parameter.GetValue(0));
            }
            rig.Core.Playing = false;
            return values;
        }

        [AvaloniaTest]
        public void AnAutomationEventWithADurationRampsToTheNext()
        {
            using var rig = new SessionRig("Lead");
            var (_, parameter, column) = RampSong(rig, ramp: true);
            int lo = parameter.MinValue;
            Assert.That(((PatternColumnCore)column).HasRamps, Is.True);
            var values = ValuesPerTick(rig, parameter, 12);
            TestContext.Out.WriteLine("ramp: " + string.Join(" ", values));
            for (int t = 1; t < values.Count; t++)
                Assert.That(values[t], Is.GreaterThanOrEqualTo(values[t - 1]), "rises: " + string.Join(" ", values));
            Assert.That(values.Take(8).Distinct().Count(), Is.GreaterThanOrEqualTo(7), "a value on each tick, not one step: " + string.Join(" ", values));
            for (int t = 1; t < 8; t++)
                Assert.That(values[t - 1], Is.InRange(lo + 8 * (t - 2), lo + 8 * t), $"on the line at tick {t}: " + string.Join(" ", values));
            Assert.That(values.Skip(9), Is.All.EqualTo(lo + 64), "holds the next event's value after it");
        }

        [AvaloniaTest]
        public void AutomationWithoutDurationsStillStepsAsBefore()
        {
            using var rig = new SessionRig("Lead");
            var (_, parameter, column) = RampSong(rig, ramp: false);
            int lo = parameter.MinValue;
            Assert.That(((PatternColumnCore)column).HasRamps, Is.False);
            var values = ValuesPerTick(rig, parameter, 12);
            TestContext.Out.WriteLine("steps: " + string.Join(" ", values));
            Assert.That(values.Distinct().ToArray(), Is.EqualTo(new[] { lo, lo + 64 }), "two steps: " + string.Join(" ", values));
            Assert.That(PatternColumnCoreRamp(column, 4 * TB), Is.Null);
        }

        static int? PatternColumnCoreRamp(IPatternColumn column, int time) =>
            ((PatternColumnCore)column).TryRampValue(time, out int v) ? v : null;

        [AvaloniaTest]
        public void RampsAreExactOnTheLine()
        {
            using var rig = new SessionRig("Lead");
            var (_, parameter, column) = RampSong(rig, ramp: true);
            int lo = parameter.MinValue;
            Assert.That(PatternColumnCoreRamp(column, 0), Is.Null, "on the event itself the event plays");
            Assert.That(PatternColumnCoreRamp(column, 4 * TB), Is.EqualTo(lo + 32));
            Assert.That(PatternColumnCoreRamp(column, 2 * TB + TB / 2), Is.EqualTo(lo + 20));
            Assert.That(PatternColumnCoreRamp(column, 9 * TB), Is.Null, "after the last event: nothing to ramp to");
        }

        // Allocation: ReBuzzTests' DawModesSeamTests.PlayingRampsAllocatesNothing, in the release build CI tests
        // (a debug build boxes enum flags on the audio path, ramps or not).

        // ------------------------------------------------------------------ S4: note sources

        [AvaloniaTest]
        public void MidiColumnNotesEditOnTheSongsHistory()
        {
            using var rig = new SessionRig("Lead");
            var p = rig.Pattern(rig.Machine("Lead"), "Riff", 8, (0, 60));
            var source = new MidiColumnNotes(rig.Core, p);
            int changed = 0;
            source.Changed += () => changed++;
            Assert.That(source.Notes().Select(n => n.Note), Is.EqualTo(new[] { 60 }));
            Assert.That(source.WhyNotEditable, Is.Null);
            Assert.That(source.Length, Is.EqualTo(8 * TB));
            int sessionUndo = rig.Layout.UndoStack.Actions.Count();

            Assert.That(source.Edit(null, new[] { new ClipNote(2 * TB, TB, 67, 90) }), Is.Null);
            Assert.That(source.Notes().Select(n => n.Note), Is.EqualTo(new[] { 60, 67 }));
            Assert.That(rig.Layout.UndoStack.Actions.Count(), Is.EqualTo(sessionUndo + 1), "one step on the song's history, which is the session's too");
            source.Undo();
            Assert.That(source.Notes().Select(n => n.Note), Is.EqualTo(new[] { 60 }), "the song's undo takes it back");
            source.Redo();
            Assert.That(source.Notes().Select(n => n.Note), Is.EqualTo(new[] { 60, 67 }));
            Assert.That(changed, Is.EqualTo(3));

            rig.Machine("Lead").DeletePattern(p);
            Assert.That(source.IsLive, Is.False);
            Assert.That(source.Edit(null, new[] { new ClipNote(0, TB, 50, 90) }), Is.Not.Null);
        }

        [AvaloniaTest]
        public void SessionClipNotesAreWhatTheSessionsPianoRollEdits()
        {
            using var rig = new SessionRig("Lead");
            var p = rig.Pattern(rig.Machine("Lead"), "Riff", 8, (0, 60));
            var source = new SessionClipNotes(rig.Layout, p);
            Assert.That(source.Notes(), Is.EqualTo(rig.Layout.NotesOf(p)));
            int songUndo = rig.Core.SongCore.ActionStack.Actions.Count();
            Assert.That(source.Edit(null, new[] { new ClipNote(TB, TB, 62, 80) }), Is.Null);
            Assert.That(rig.Core.SongCore.ActionStack.Actions.Count(), Is.GreaterThan(songUndo), "a step of the one history, the song's and the session's");
            source.Undo();
            Assert.That(source.Notes().Count, Is.EqualTo(1));
            var roll = new Views.Session.PianoRoll(rig.Core, rig.Layout, Track(rig, "Lead"), p);
            Assert.That(roll.Source, Is.InstanceOf<SessionClipNotes>(), "the Session View's roll goes through the session's source");
            var other = new Views.Session.PianoRoll(rig.Core, new MidiColumnNotes(rig.Core, p), null);
            other.AddNote(4 * TB, 72);
            Assert.That(new MidiColumnNotes(rig.Core, p).Notes().Select(n => n.Note), Is.EqualTo(new[] { 60, 72 }), "a roll on another source edits it");
            rig.Core.SongCore.ActionStack.Undo();
            Assert.That(rig.Layout.NotesOf(p).Count, Is.EqualTo(1));
        }

        [AvaloniaTest]
        public void TrackerColumnNotesFollowTrackerRules()
        {
            using var rig = new SessionRig();
            var bass = rig.Add("Pedal SH101", "Bass");
            var p = rig.Pattern(bass, "Line", 16);
            var source = TrackerColumnNotes.For(rig.Core, p);
            Assert.That(source, Is.Not.Null, "Pedal SH101 has a Note track parameter");
            int tracks = Math.Max(1, bass.TrackCount);

            Assert.That(source.Edit(null, new[] { new ClipNote(0, 4 * TB, 36, 100), new ClipNote(4 * TB, 2 * TB, 43, 100) }), Is.Null);
            var notes = source.Notes();
            Assert.That(notes.Select(n => (n.Time, n.Duration, n.Note)), Is.EqualTo(new[] { (0, 4 * TB, 36), (4 * TB, 2 * TB, 43) }), "read back as written");
            var column = p.Columns.First(c => c.Parameter?.Type == ParameterType.Note && c.Track == 0);
            Assert.That(column.GetEvents(0, int.MaxValue).Select(e => (e.Time, e.Value)), Is.EqualTo(new[] { (0, BuzzNote.FromMIDINote(36)), (4 * TB, BuzzNote.FromMIDINote(43)), (6 * TB, BuzzNote.Off) }),
                "Buzz notes on the track, an off where the last ends");

            // A chord needs a track per note: more than the machine has is refused, and nothing changes.
            var chord = Enumerable.Range(0, tracks + 1).Select(i => new ClipNote(8 * TB, TB, 48 + 4 * i, 100)).ToArray();
            string why = source.Edit(null, chord);
            Assert.That(why, Does.Contain("track"), "refused, with the reason");
            Assert.That(source.Notes().Count, Is.EqualTo(2));
            // Notes named as Preferences name them (ux-overhaul.md §3.5): Buzz's B-9 is MIDI 119.
            why = source.Edit(null, new[] { new ClipNote(12 * TB, TB, 120, 100) });
            Assert.That(why, Does.StartWith($"{BuzzGUI.Common.NoteNames.Name(120)} is above").And.Contain($"({BuzzGUI.Common.NoteNames.Name(119)} is the highest a Buzz track takes)"));
            Assert.That(source.Notes().Count, Is.EqualTo(2));

            // A move keeps its track; undo is the song's.
            var moved = new ClipNote(TB, 3 * TB, 38, 100);
            Assert.That(source.Edit(new[] { notes[0] }, new[] { moved }), Is.Null);
            Assert.That(source.Notes().Select(n => n.Note), Is.EqualTo(new[] { 38, 43 }));
            source.Undo();
            Assert.That(source.Notes().Select(n => n.Note), Is.EqualTo(new[] { 36, 43 }));
        }

        [AvaloniaTest]
        public void EditorPreviewNotesReadAnEditorsExportAndCannotBeEdited()
        {
            // (time in 1/960 tick, MIDI message): C4 from 0 to tick 1, E4 from tick 2 on (no off).
            var pairs = new[] { 0, SessionRig.NoteOn(60, 100), 960, SessionRig.NoteOff(60), 1920, SessionRig.NoteOn(64, 80) };
            var notes = EditorPreviewNotes.FromEditorEvents(pairs, 4 * TB);
            Assert.That(notes, Is.EqualTo(new[] { new ClipNote(0, TB, 60, 100), new ClipNote(2 * TB, 2 * TB, 64, 80) }));

            using var rig = new SessionRig("Lead");
            var p = rig.Pattern(rig.Machine("Lead"), "Riff", 4);
            var source = new EditorPreviewNotes(rig.Core, p);
            Assert.That(source.WhyNotEditable, Is.Not.Null);
            Assert.That(source.Edit(null, new[] { new ClipNote(0, TB, 60, 100) }), Is.Not.Null);
        }

        // ------------------------------------------------------------------ the exit check (daw-modes.md §9, AR0)

        /// <summary>BMX sections by name (as the file spells them), each section's bytes.</summary>
        static Dictionary<string, byte[]> BmxSections(string path)
        {
            var data = File.ReadAllBytes(path);
            using var r = new BinaryReader(new MemoryStream(data));
            Assert.That(r.ReadUInt32(), Is.EqualTo(0x7A7A7542u), "a Buzz file");
            uint count = r.ReadUInt32();
            var sections = new Dictionary<string, byte[]>();
            for (uint i = 0; i < count; i++)
            {
                string name = Encoding.ASCII.GetString(r.ReadBytes(4));
                uint offset = r.ReadUInt32(), size = r.ReadUInt32();
                sections[name] = data.AsSpan((int)offset, (int)size).ToArray();
            }
            return sections;
        }

        const string SubSections = "BGUI";      // SectionType.IUGB as the file spells it: where the subsections are

        /// <summary>A BMXML file without its stamps (name, build, date) and, with <paramref name="core"/>, without its subsections.</summary>
        static string Bmxml(string path, bool core)
        {
            string text = File.ReadAllText(path);
            text = Regex.Replace(text, "<ReBuzzSong[^>]*>", m => Regex.Replace(m.Value, "\\s(Name|Build|BuildString)=\"[^\"]*\"", ""));
            if (core) text = Regex.Replace(text, "<SubSenctions>.*</SubSenctions>", "", RegexOptions.Singleline);
            return text;
        }

        static string Repo
        {
            get
            {
                string dir = AppContext.BaseDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "ReBuzz.sln"))) dir = Path.GetDirectoryName(dir);
                return dir;
            }
        }

        static (int Exit, string Output)? RoundTripTool(params string[] args)
        {
            if (Repo == null) return null;
            var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-I");
            start.ArgumentList.Add(Path.Combine(Repo, "tools", "song-roundtrip.py"));
            foreach (var a in args) start.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(start)!;
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, output.Trim());
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        [AvaloniaTest]
        public void ASongWithClipsLoopsRampsAndAMeterRoundTripsWithItsCoreSectionsUnchanged()
        {
            using var rig = new SessionRig("Lead", "Bass");
            var layout = rig.Layout;
            var a = rig.Pattern(rig.Machine("Lead"), "a", 8, (0, 60), (3, 63));
            var b = rig.Pattern(rig.Machine("Bass"), "b", 12, (0, 40));
            layout.Ensure();
            var settings = layout.ClipOf(a);
            settings.Start = 2;
            settings.LoopStart = 4;
            settings.LoopEnd = 7;
            layout.SetClipSettings(a, settings);
            SceneToArrangement.Insert(rig.Core, layout, 0, 0, 32, insert: false);       // clips and their loops
            RampSong(rig, ramp: true, at: 32);                                            // a ramp (Lead's "Sweep" from 32)
            rig.Core.SongMeter.Set(0, 12);                                                // a meter
            rig.Core.SongMeter.Set(24, 16);

            string Describe()
            {
                var lines = new List<string> { "meter " + string.Join(" ", rig.Core.SongMeter.Entries.Select(e => $"{e.Key}:{e.Value}")) };
                foreach (var s in rig.Core.SongCore.SequencesList)
                {
                    lines.Add($"{s.Machine.Name}: " + string.Join(" ", s.Events.OrderBy(e => e.Key).Select(e => $"{e.Key}:{e.Value.Type}:{e.Value.Pattern?.Name}:{e.Value.Offset}-{e.Value.End}:c{e.Value.Clip}")));
                    lines.Add($"{s.Machine.Name} clips: " + string.Join(" ", rig.Core.Clips.Of(s).Select(c => $"{c.Time}+{c.Length}x{c.Events}:{c.Loop}")));
                }
                foreach (var c in rig.Machine("Lead").Patterns.First(p => p.Name == "Sweep").Columns)
                    lines.Add($"Sweep {c.Type} {c.Parameter?.Name}: " + string.Join(" ", c.GetEvents(0, int.MaxValue).Select(e => $"{e.Time}={e.Value}/{e.Duration}")));
                return string.Join("\n", lines);
            }
            string song = Describe();
            Assert.That(song, Does.Contain(":c1").And.Contain("/" + 8 * TB), "the song has clips and a ramp:\n" + song);

            string original = Work("song.bmx");
            rig.Core.SaveSongCopy(original);
            var problems = new List<string>();
            foreach (var ext in new[] { ".bmx", ".bmxml" })
            {
                string fileA = Work("song.A" + ext), fileB = Work("song.B" + ext);
                SaveAndOpen(rig, original);
                rig.Core.SaveSongCopy(fileA);
                SaveAndOpen(rig, fileA);
                Assert.That(Describe(), Is.EqualTo(song), $"{ext}: everything comes back");
                rig.Core.SaveSongCopy(fileB);
                if (ext == ".bmx")
                    Assert.That(File.ReadAllBytes(fileB), Is.EqualTo(File.ReadAllBytes(fileA)), "bmx: A and B are the same bytes");
                else
                    Assert.That(Bmxml(fileB, core: false), Is.EqualTo(Bmxml(fileA, core: false)), "bmxml: A and B are the same but for the stamps");
                if (RoundTripTool("--compare", original, fileA, fileB) is (int exit, string output))
                {
                    TestContext.Out.WriteLine($"song-roundtrip.py --compare {ext}: {output}");
                    if (exit != 0) problems.Add($"{ext}: {output}");
                }
            }
            Assert.That(problems, Is.Empty, "tools/song-roundtrip.py --compare");

            // The same song without the fork's arrangement data (clip ids, their loops, the meter): only subsections differ.
            string plainBmx = Work("plain.bmx"), plainXml = Work("plain.bmxml"), forkXml = Work("song.A.bmxml");
            foreach (var s in rig.Core.SongCore.SequencesList)
                foreach (var e in s.Events.Values) e.Clip = 0;
            rig.Core.SongMeter.Reset();
            rig.Core.SaveSongCopy(plainBmx);
            rig.Core.SaveSongCopy(plainXml);
            var fork = BmxSections(Work("song.A.bmx"));
            var plain = BmxSections(plainBmx);
            Assert.That(fork.Keys.Where(k => k != SubSections), Is.EquivalentTo(plain.Keys.Where(k => k != SubSections)));
            foreach (var name in fork.Keys.Where(k => k != SubSections))
                Assert.That(fork[name], Is.EqualTo(plain[name]), $"bmx section {name}: the same bytes with and without the fork's data");
            Assert.That(fork[SubSections], Is.Not.EqualTo(plain[SubSections]), "the fork's data is in the subsections");
            Assert.That(Bmxml(forkXml, core: true), Is.EqualTo(Bmxml(plainXml, core: true)), "bmxml: the same but for the subsections");
        }
    }
}
