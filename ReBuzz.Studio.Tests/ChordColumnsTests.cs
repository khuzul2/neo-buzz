// Fork (docs/specs/notes.md section 9, CU6b): chords in the tracker, headless. A clip's tracker shows as many note+velocity column pairs as the
// user wants for its Notes lane: added and removed with Alt++ and Alt+- (and the grid's menu), saved with the clip in the .nbl, one undo step each,
// the cursor moves across them, a chord typed across two pairs is two notes of the MIDI column (the real note path), the agent reads and writes
// them, and a clip that never had a count shows exactly as before. Neo Sampler's Tracker mode takes 32 notes at once (MaxTracks 32), and says 32.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Common.Actions.ClipActions;
using BuzzGUI.Interfaces;
using NeoBuzz.TestSupport;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class ChordColumnsTests
    {
        const int TB = ClipEvent.TimeBase;
        const int Row = TB;

        sealed class Setup
        {
            public MainWindow Window;
            public MachineCore Machine;
            public IClip Clip;
            public dynamic View;
            public dynamic Vm => View.Editor.SelectedMachine.SelectedClip;
        }

        static ReBuzzCore Core => Studio.Host.Core;

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        static RawInputModifiers Cmd => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        static Setup Open(string name, int ticks = 16)
        {
            ValueDisplay.Base = ValueBase.Hex;
            Core.SongCore.CreateMachine("Neo Poly", null, name, null, null, null, -1, 0.3f, 0.3f);
            var machine = Core.SongCore.MachinesList.First(m => m.Name == name);
            machine.CreateClip("00", ticks);
            var clip = machine.Clips.First(p => p.Name == "00");
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Pump();
            window.Detail.Show(true);
            ClipView.Edit(clip);
            Pump();
            dynamic view = ClipView.Page.EditorControl;
            Assert.That((object)view, Is.Not.Null);
            ((Control)view.TrackerGrid.Body).Focus();
            Pump();
            return new Setup { Window = window, Machine = machine, Clip = clip, View = view };
        }

        static void Close(Setup s)
        {
            ValueDisplay.Base = ValueDisplay.Default;
            Core.Modified = false;
            s.Window.Close();
            Pump();
            Core.SongCore.DeleteMachines(new[] { s.Machine });
        }

        static void Key(Setup s, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            s.Window.KeyPressQwerty(key, modifiers);
            s.Window.KeyReleaseQwerty(key, modifiers);
            Pump();
        }

        static void Take(IClip clip, params LaneEvent[] events) =>
            SessionLayout.EnsureMidiColumn(clip).SetEvents(events.Select(e => new ClipEvent(e.Time, e.Value, e.Duration)).ToArray(), true);

        static LaneEvent Note(int time, int duration, int midi, int velocity = 100) =>
            new LaneEvent(time, 0x90 | (midi << 8) | (velocity << 16), duration);

        static List<LaneEvent> Column(IClip clip) =>
            (SessionLayout.EnsureMidiColumn(clip)?.GetEvents(int.MinValue, int.MaxValue) ?? Enumerable.Empty<ClipEvent>())
                .Select(e => new LaneEvent(e.Time, e.Value, e.Duration)).OrderBy(e => e.Time).ThenBy(e => e.Value).ToList();

        static void At(Setup s, int column, int row, int set = 0)
        {
            dynamic d = s.Vm.CursorPosition;
            s.View.Editor.Control.MoveCursor(d.SetColumnSet(set).SetColumn(column).SetBeat(row / 4).SetRowInBeat(row % 4).SetIndex(0));
            Pump();
        }

        static int Steps => Core.SongCore.ActionStack.Actions.Count();

        static LaneEvent[] ATwoNoteChord() => new[] { Note(0, 4 * Row, 60), Note(0, 4 * Row, 64), Note(8 * Row, 2 * Row, 67) };

        [AvaloniaTest]
        public void AClipWithNoCountShowsAsItAlwaysDidAndAltPlusAndMinusAddAndRemoveAPair()
        {
            var s = Open("CU6b Keys");
            try
            {
                Take(s.Clip, ATwoNoteChord());
                Pump();
                Assert.That(NoteColumnsAction.StoredOf(s.Clip), Is.Null, "an old clip has no count");
                int voices = 2, sets = s.Vm.ColumnSets.Count;
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(voices + 1), "the voices the notes need and a free one, as before");

                int steps = Steps;
                Key(s, PhysicalKey.Equal, RawInputModifiers.Alt);
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(voices + 2), "Alt++ adds a pair");
                Assert.That(s.Vm.ColumnSets.Count, Is.EqualTo(sets + 1), "a set of Note, Vel, Dly and Cut more");
                Assert.That(NoteColumnsAction.StoredOf(s.Clip), Is.EqualTo(voices + 2), "saved with the clip");
                Assert.That(Steps, Is.EqualTo(steps + 1), "one step of the song's history");
                Assert.That(Column(s.Clip), Is.EqualTo(Column(s.Clip).OrderBy(e => e.Time).ThenBy(e => e.Value).ToList()));
                Assert.That(Column(s.Clip).Count, Is.EqualTo(3), "the notes are not touched");

                Key(s, PhysicalKey.Equal, RawInputModifiers.Alt);
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(voices + 3));
                Key(s, PhysicalKey.Minus, RawInputModifiers.Alt);
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(voices + 2), "Alt+- takes the last pair off");
                Assert.That(NoteColumnsAction.StoredOf(s.Clip), Is.EqualTo(voices + 2));

                // Undo and redo are the song's own.
                Key(s, PhysicalKey.Z, Cmd);
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(voices + 3), "undo of the removal");
                Key(s, PhysicalKey.Z, Cmd);
                Key(s, PhysicalKey.Z, Cmd);
                Assert.That(NoteColumnsAction.StoredOf(s.Clip), Is.Null, "undone to the clip that had none");
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(voices + 1));
                Assert.That(s.Vm.ColumnSets.Count, Is.EqualTo(sets));
                if (OperatingSystem.IsMacOS()) Key(s, PhysicalKey.Z, Cmd | RawInputModifiers.Shift); else Key(s, PhysicalKey.Y, Cmd);
                Assert.That(NoteColumnsAction.StoredOf(s.Clip), Is.EqualTo(voices + 2), "and redone");
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(voices + 2));
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void TheMenuCommandsDoTheSameAndARemovalNeverHidesANote()
        {
            var s = Open("CU6b Menu");
            try
            {
                Take(s.Clip, ATwoNoteChord());
                Pump();
                dynamic control = s.View.Editor.Control;
                Assert.That((bool)control.AddNoteColumnCommand.CanExecute(null), Is.True);
                control.AddNoteColumnCommand.Execute(null);
                control.AddNoteColumnCommand.Execute(null);
                Pump();
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(5));
                control.RemoveNoteColumnCommand.Execute(null);
                control.RemoveNoteColumnCommand.Execute(null);
                Pump();
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(3));
                control.RemoveNoteColumnCommand.Execute(null);
                Pump();
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(2), "down to the two the chord needs");
                Assert.That((string)s.Vm.RemoveNoteColumn(), Does.Contain("would not be shown"), "refused while it would hide a note, with the reason");
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(2));
                Assert.That(Column(s.Clip).Count, Is.EqualTo(3));
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void AChordTypedAcrossTwoPairsIsTwoNotesOfTheMidiColumnAndTheCursorMovesAcross()
        {
            var s = Open("CU6b Chord");
            try
            {
                s.Vm.ShowNoteLanes = true;
                Pump();
                Key(s, PhysicalKey.Equal, RawInputModifiers.Alt);
                Assert.That((int)s.Vm.NoteColumnCount, Is.GreaterThanOrEqualTo(2));
                At(s, 0, 2, set: 0);
                Key(s, PhysicalKey.Z);                                  // the first note of the chord, pair 1
                At(s, 0, 2, set: 1);
                Key(s, PhysicalKey.C);                                  // the next one, pair 2, at the same row
                var column = Column(s.Clip);
                Assert.That(column.Count, Is.EqualTo(2), "two notes");
                Assert.That(column.Select(n => n.Time).Distinct().ToList(), Is.EqualTo(new[] { 2 * Row }), "at one time");
                Assert.That(column.Select(n => n.Midi).Distinct().Count(), Is.EqualTo(2), "of two pitches");

                // They are the notes the machine plays: the note source of the clip (the MIDI route's) holds them.
                var notes = SongNotes.For(Core, s.Clip).Notes();
                Assert.That(notes.Count, Is.EqualTo(2));
                Assert.That(SongNotes.For(Core, s.Clip), Is.InstanceOf<MidiColumnNotes>(), "the real note path");

                // And they sit on pair 1 and pair 2 of the lanes.
                var lanes = new NoteLanes(column, RowGrid.Beats(new[] { 4 }), 16 * TB);
                Assert.That(lanes.Voices, Is.EqualTo(2));

                // The cursor moves across the pairs: Right from the last lane of pair 1 lands on the Note lane of pair 2.
                At(s, 3, 2, set: 0);
                Key(s, PhysicalKey.ArrowRight);                         // the Cut lane has two digits: the second one first
                Key(s, PhysicalKey.ArrowRight);
                dynamic d = s.Vm.CursorPosition;
                Assert.That(((int)d.ColumnSet, (int)d.Column), Is.EqualTo((1, 0)), "Right crosses to the next pair");
                Key(s, PhysicalKey.ArrowLeft);
                d = s.Vm.CursorPosition;
                Assert.That(((int)d.ColumnSet, (int)d.Column), Is.EqualTo((0, 3)), "and back");
                At(s, 3, 2, set: 0);
                Key(s, PhysicalKey.Tab);
                d = s.Vm.CursorPosition;
                Assert.That((int)d.ColumnSet, Is.EqualTo(1), "Tab too");
            }
            finally { Close(s); }
        }

        [AvaloniaTest]
        public void TheColumnsAreSavedWithTheClipAndComeBackInTheNbl()
        {
            var s = Open("CU6b Saved");
            string dir = Path.Combine(Path.GetTempPath(), "rebuzz-studio-cu6b-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            try
            {
                Take(s.Clip, ATwoNoteChord());
                Pump();
                for (int i = 0; i < 3; i++) Key(s, PhysicalKey.Equal, RawInputModifiers.Alt);
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(6));
                var notes = Column(s.Clip);
                string path = Path.Combine(dir, "chords.nbl");
                SongFiles.Picker = save => path;
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Assert.That(SongFiles.SaveAs().Result, Is.True);
                Assert.That(SongFiles.Open(path).Result, Is.True);
                Pump();
                var machine = Core.SongCore.MachinesList.First(m => m.Name == "CU6b Saved");
                s.Machine = machine;
                s.Clip = machine.Clips.First();
                Assert.That(NoteColumnsAction.StoredOf(s.Clip), Is.EqualTo(6), "the count is in the clip");
                Assert.That(Column(s.Clip), Is.EqualTo(notes), "the notes as they were");
                ClipView.Edit(s.Clip);
                Pump();
                s.View = ClipView.Page.EditorControl;
                Assert.That((int)s.Vm.NoteColumnCount, Is.EqualTo(6), "reopened, the tracker shows six pairs");
            }
            finally
            {
                SongFiles.Picker = null;
                Dialogs.Responder = null;
                Close(s);
                SongFiles.New().Wait();
            }
        }

        // ------------------------------------------------------------------ the agent

        static JsonObject Tool(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()["result"]!.AsObject();
        }

        static JsonObject Chord(int pitch, double start, double length = 0.5) => new JsonObject { ["pitch"] = pitch, ["start"] = start, ["length"] = length, ["velocity"] = 100 };

        [AvaloniaTest]
        public void TheAgentReadsAndSetsTheColumnsOfAClip()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var machine = rig.Add("Neo Poly", "Pad");
            var clip = rig.Clip(machine, "Chords", 16);

            var written = Tool("notes_write", new JsonObject
            {
                ["machine"] = "Pad", ["clip"] = "Chords",
                ["notes"] = new JsonArray { Chord(60, 0), Chord(64, 0), Chord(67, 0), Chord(72, 2) },
                ["columns"] = 5,
            });
            Assert.That(written["isError"]?.GetValue<bool>() == true, Is.False, written.ToJsonString());
            var cols = written["structuredContent"]!["note_columns"]!.AsObject();
            Assert.That((int)cols["shown"]!, Is.EqualTo(5));
            Assert.That((int)cols["stored"]!, Is.EqualTo(5));
            Assert.That((int)cols["needed"]!, Is.EqualTo(3));
            Assert.That(NoteColumnsAction.StoredOf(clip), Is.EqualTo(5), "saved with the clip");

            var read = Tool("notes_read", new JsonObject { ["machine"] = "Pad", ["clip"] = "Chords" });
            var notes = read["structuredContent"]!["notes"]!.AsArray().Select(n => n!.AsObject()).OrderBy(n => (double)n["start"]!).ThenBy(n => (int)n["pitch"]!).ToList();
            Assert.That(notes.Take(3).Select(n => (int)n["column"]!), Is.EqualTo(new[] { 1, 2, 3 }), "a chord sits across three pairs");
            Assert.That((int)notes[3]["column"]!, Is.EqualTo(1), "a lone note on the first free pair");
            Assert.That((int)read["structuredContent"]!["note_columns"]!["shown"]!, Is.EqualTo(5));
            // What notes_read gives, column and all, goes back into notes_write unchanged (the agent evaluation's round trip).
            var back = Tool("notes_write", new JsonObject { ["machine"] = "Pad", ["clip"] = "Chords", ["mode"] = "replace", ["notes"] = read["structuredContent"]!["notes"]!.DeepClone() });
            Assert.That(back["isError"]?.GetValue<bool>() == true, Is.False, back.ToJsonString());
            Assert.That(SongNotes.For(core, clip).Notes().Count(), Is.EqualTo(4));
            var got = Tool("clip_get", new JsonObject { ["machine"] = "Pad", ["clip"] = "Chords" });
            Assert.That((int)got["structuredContent"]!["note_columns"]!["shown"]!, Is.EqualTo(5));

            // Fewer than the notes need is refused, in words, and nothing changes.
            var refused = Tool("notes_write", new JsonObject { ["machine"] = "Pad", ["clip"] = "Chords", ["notes"] = new JsonArray(), ["columns"] = 2 });
            Assert.That(refused.ToJsonString(), Does.Contain("would not be shown"));
            Assert.That(NoteColumnsAction.StoredOf(clip), Is.EqualTo(5));
            var tooMany = Tool("notes_write", new JsonObject { ["machine"] = "Pad", ["clip"] = "Chords", ["notes"] = new JsonArray(), ["columns"] = 33 });
            Assert.That(tooMany.ToJsonString(), Does.Contain("32"));

            // Undo takes the count away again (one step of the song's history), and the notes with the writes before it.
            for (int i = 0; i < 4 && NoteColumnsAction.StoredOf(clip) != null; i++) core.SongCore.ActionStack.Undo();
            Assert.That(NoteColumnsAction.StoredOf(clip), Is.Null, "undone to the clip that had none");
            core.SongCore.ActionStack.Redo();
            Assert.That(NoteColumnsAction.StoredOf(clip) == null || NoteColumnsAction.StoredOf(clip) == 5, Is.True);
        }

        // ------------------------------------------------------------------ MaxTracks 32

        static MachineCore TrackerSampler(SessionRig rig, string name)
        {
            var m = rig.Add("Neo Sampler", name);
            m.ParameterGroups[1].Parameters.First(p => p.Name == "Mode").SetValue(0, 2);
            return m;
        }

        static ClipNote[] Chord32(int size) => Enumerable.Range(0, size).Select(i => new ClipNote(0, 2 * ClipNotes.TimeBase, 36 + i, 100)).ToArray();

        [AvaloniaTest]
        public void NeoSamplerInTrackerModeTakesThirtyTwoNotesAtOnceAndSaysThirtyTwoWhenThereAreMore()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var machine = TrackerSampler(rig, "Keys32");
            Assert.That(machine.DLL.Info.MaxTracks, Is.EqualTo(32), "the track-note limit is 32");
            var clip = rig.Clip(machine, "Wide", 16);

            // The agent's way: the tracks grow to what the chord needs.
            var ok = Tool("notes_write", new JsonObject
            {
                ["machine"] = "Keys32", ["clip"] = "Wide",
                ["notes"] = new JsonArray(Enumerable.Range(0, 32).Select(i => (JsonNode)Chord(36 + i, 0, 1)).ToArray()),
            });
            Assert.That(ok["isError"]?.GetValue<bool>() == true, Is.False, ok.ToJsonString());
            Assert.That(machine.TrackCount, Is.EqualTo(32), "raised to the 32 the chord needs");
            Assert.That(SongNotes.For(core, clip).Notes().Count(), Is.EqualTo(32));

            var more = Tool("notes_write", new JsonObject
            {
                ["machine"] = "Keys32", ["clip"] = "Wide", ["mode"] = "replace",
                ["notes"] = new JsonArray(Enumerable.Range(0, 33).Select(i => (JsonNode)Chord(36 + i, 0, 1)).ToArray()),
            });
            Assert.That(more["isError"]?.GetValue<bool>(), Is.True);
            Assert.That(more.ToJsonString(), Does.Contain("at most 32 tracks"), "the refusal names the new limit");

            // The tracker's own edit (the user's way): 32 fit, the 33rd is refused and says 32.
            var clip2 = rig.Clip(machine, "Typed", 16);
            var source = SongNotes.For(core, clip2);
            Assert.That(source.Edit(null, Chord32(32)), Is.Null);
            Assert.That(SongNotes.For(core, clip2).Notes().Count(), Is.EqualTo(32));
            var refused = source.Edit(null, new[] { new ClipNote(0, ClipNotes.TimeBase, 80, 100) });
            Assert.That(refused, Does.Contain("32"));
            Assert.That(refused, Does.Contain("is the most"));
            Assert.That(SongNotes.For(core, clip2).Notes().Count(), Is.EqualTo(32), "nothing changed");
        }

        [AvaloniaTest]
        public void ASongSavedWithSixteenTracksKeepsItsTracksAndItsNotes()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var machine = TrackerSampler(rig, "Old16");
            core.SongCore.ActionStack.Do(new BuzzGUI.Common.Actions.MachineActions.SetTrackCountAction(machine, 16));
            var clip = rig.Clip(machine, "Riff", 16);
            Assert.That(SongNotes.For(core, clip).Edit(null, Chord32(16)), Is.Null);
            Assert.That(machine.TrackCount, Is.EqualTo(16));
            var before = SongNotes.For(core, clip).Notes().OrderBy(n => n.Note).ToArray();
            string dir = TestTemp.Path($"cu6b-tracks-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "sixteen.nbl");
            core.SaveSongCopy(path);
            core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(core.TryOpenSongFile(path), Is.True);
            Dialogs.Responder = null;
            var reopened = core.SongCore.MachinesList.Single(m => m.Name == "Old16");
            Assert.That(reopened.TrackCount, Is.EqualTo(16), "the tracks it was saved with");
            var again = SongNotes.For(core, reopened.ClipsList.Single(p => p.Name == "Riff")).Notes().OrderBy(n => n.Note).ToArray();
            Assert.That(again, Is.EqualTo(before), "every note as it was");
        }
    }
}
