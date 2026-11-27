// Fork (FU2b, docs/specs/clips.md "The Slide flag"): the per-note Slide flag of a clip note, headless. It is set in the piano roll (a per-note toggle) and in the tracker
// (Ctrl+L in the Notes lanes), saved in the .nbl with the note, one undo step an edit, read and written by the agent's clip tools, and read at the machine: Neo Poly
// glides into a flagged note at its Slide time (a rendered difference), any other machine plays it as it plays every note, and a clip without flags is as it was.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Session;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class NoteSlideFlagTests
    {
        const int TB = ClipNotes.TimeBase;

        static JsonObject Tool(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(180);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()["result"]!.AsObject();
        }

        static JsonObject Ok(JsonObject r)
        {
            Assert.That(r["isError"]?.GetValue<bool>() ?? false, Is.False, r.ToJsonString());
            return r["structuredContent"]!.AsObject();
        }

        static void Set(IMachine m, string name, int value) => m.ParameterGroups[1].Parameters.First(p => p.Name == name).SetValue(0, value);

        /// <summary>A line of two notes a beat apart (C2 then G2), the second one carrying <paramref name="slide"/>; on the arrangement for a render.</summary>
        static void Line(string machine, bool slide)
        {
            Ok(Tool("clip_create", new JsonObject { ["machine"] = machine, ["name"] = "Line", ["length_beats"] = 4 }));
            Ok(Tool("notes_write", new JsonObject
            {
                ["machine"] = machine, ["clip"] = "Line",
                ["notes"] = new JsonArray
                {
                    new JsonObject { ["pitch"] = 36, ["start"] = 0, ["length"] = 0.5, ["velocity"] = 100 },
                    new JsonObject { ["pitch"] = 43, ["start"] = 1, ["length"] = 0.5, ["velocity"] = 100, ["slide"] = slide },
                },
            }));
            Ok(Tool("arrangement_clip", new JsonObject { ["op"] = "add", ["machine"] = machine, ["clip"] = "Line", ["at"] = 0, ["length_beats"] = 4 }));
        }

        static float[] Render(string machine, string name)
        {
            var r = Ok(Tool("render", new JsonObject { ["from"] = 0, ["to"] = 4, ["tail_seconds"] = 0, ["solo"] = new JsonArray(machine), ["name"] = name }));
            return Wav.Read((string)r["file"]!).Left;
        }

        [AvaloniaTest]
        public void TheAgentWritesAndReadsTheFlagAndTheClipToolsGiveItBack()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var machine = rig.Add("Neo Poly", "Lead");
            Line("Lead", true);
            var read = Ok(Tool("notes_read", new JsonObject { ["machine"] = "Lead", ["clip"] = "Line" }));
            var notes = read["notes"]!.AsArray().Select(n => n!.AsObject()).OrderBy(n => (double)n["start"]!).ToList();
            Assert.That(notes.Select(n => n["slide"]?.GetValue<bool>()), Is.EqualTo(new bool?[] { null, true }), "only the flagged note says so");
            var got = Ok(Tool("clip_get", new JsonObject { ["machine"] = "Lead", ["clip"] = "Line" }));
            Assert.That(got["notes"]!.AsArray().Count(n => n!["slide"]?.GetValue<bool>() == true), Is.EqualTo(1), "clip_get too");

            // A flag that is not a boolean is refused in words.
            var refused = Tool("notes_write", new JsonObject
            {
                ["machine"] = "Lead", ["clip"] = "Line", ["mode"] = "replace",
                ["notes"] = new JsonArray { new JsonObject { ["pitch"] = 40, ["start"] = 0, ["length"] = 1, ["slide"] = "yes" } },
            });
            Assert.That(refused["isError"]?.GetValue<bool>(), Is.True);
            Assert.That(refused.ToJsonString(), Does.Contain("slide"));

            // Replacing the notes with the same ones keeps it (the removed notes match what is stored); one undo brings the first line back.
            int undos = core.SongCore.ActionStack.Actions.Count();
            Ok(Tool("notes_write", new JsonObject
            {
                ["machine"] = "Lead", ["clip"] = "Line", ["mode"] = "replace",
                ["notes"] = new JsonArray { new JsonObject { ["pitch"] = 36, ["start"] = 0, ["length"] = 0.5, ["velocity"] = 80 }, new JsonObject { ["pitch"] = 43, ["start"] = 1, ["length"] = 0.5, ["slide"] = true } },
            }));
            Assert.That(core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(undos + 1), "one step");
            var clip = machine.ClipsList.Single(p => p.Name == "Line");
            Assert.That(core.SessionLayout.NotesOf(clip).Select(n => (n.Velocity, n.Slide)), Is.EqualTo(new[] { (80, false), (100, true) }), "the flag is written with the note and kept by a replace");
            core.SongCore.ActionStack.Undo();
            Assert.That(core.SessionLayout.NotesOf(clip).Select(n => (n.Velocity, n.Slide)), Is.EqualTo(new[] { (100, false), (100, true) }), "undo goes back to the first line, with its flag");
        }

        [AvaloniaTest]
        public void ThePianoRollTogglesTheFlagOfTheSelectedNotesOneUndoStepAndItIsSavedWithTheSong()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var machine = rig.Add("Neo Poly", "Lead");
            var clip = rig.Clip(machine, "Line", 16, (0, 36), (4, 43), (8, 40));
            var source = SongNotes.For(core, clip);
            var roll = new PianoRoll(core, source, null) { Grid = TB };
            Assert.That(roll.CanSlide, Is.True, "a MIDI column's notes take the flag");
            Assert.That(roll.ToggleSlide(), Is.False, "nothing selected: nothing to do");
            roll.SelectOnly(new[] { 1 });
            int undos = core.SongCore.ActionStack.Actions.Count();
            Assert.That(roll.ToggleSlide(), Is.True);
            Assert.That(source.Notes().Select(n => n.Slide), Is.EqualTo(new[] { false, true, false }), "the second note slides");
            Assert.That(core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(undos + 1), "one step of the song's history");
            Assert.That(source.Notes().Select(n => (n.Time, n.Note, n.Velocity)), Is.EqualTo(new[] { (0, 36, 100), (4 * TB, 43, 100), (8 * TB, 40, 100) }), "nothing else changed");
            Assert.That(roll.HandleKey(Avalonia.Input.Key.L, Avalonia.Input.KeyModifiers.None), Is.True, "the L key toggles it back");
            Assert.That(source.Notes().Any(n => n.Slide), Is.False);
            roll.HandleKey(Avalonia.Input.Key.L, Avalonia.Input.KeyModifiers.None);
            core.SongCore.ActionStack.Undo();
            Assert.That(source.Notes().Any(n => n.Slide), Is.False, "undo");
            core.SongCore.ActionStack.Redo();
            Assert.That(source.Notes().Count(n => n.Slide), Is.EqualTo(1), "redo");

            string path = Path.Combine(Path.GetTempPath(), $"rebuzz-fu2b-slide-{Environment.ProcessId}.nbl");
            try
            {
                core.SaveSongCopy(path);
                core.Modified = false;
                core.ExecuteCommand(BuzzCommand.NewFile);
                core.OpenSongFile(path);
                var again = core.SongCore.MachinesList.Single(m => m.Name == "Lead").ClipsList.Single(p => p.Name == "Line");
                Assert.That(core.SessionLayout.NotesOf(again).Select(n => n.Slide), Is.EqualTo(new[] { false, true, false }), "the flag comes back with the song");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [AvaloniaTest]
        public void TheTrackersNotesLaneSetsAndClearsTheFlagWithoutTouchingTheNote()
        {
            var events = new[] { new LaneEvent(0, 0x90 | (36 << 8) | (100 << 16), TB), new LaneEvent(4 * TB, 0x90 | (43 << 8) | (90 << 16), TB) };
            var lanes = new NoteLanes(events, RowGrid.Uniform(TB), 16 * TB);
            Assert.That(lanes.TryCell(0, 4, out var cell), Is.True);
            Assert.That(lanes.IsSlide(cell), Is.False);
            var edit = lanes.SetSlide(0, 4, true);
            Assert.That(edit.IsEmpty, Is.False);
            var after = NoteLanes.Apply(events, edit);
            var note = after.Single(e => e.Time == 4 * TB);
            Assert.That((note.Value & NoteLanes.SlideBit) != 0, Is.True);
            Assert.That(note.Midi, Is.EqualTo(43));
            Assert.That(note.Velocity, Is.EqualTo(90), "the velocity is where it was");
            Assert.That(note.Duration, Is.EqualTo(TB));
            Assert.That(new NoteLanes(after, RowGrid.Uniform(TB), 16 * TB).TryCell(0, 4, out var again) && new NoteLanes(after, RowGrid.Uniform(TB), 16 * TB).IsSlide(again), Is.True);
            Assert.That(lanes.SetSlide(0, 4, false).IsEmpty, Is.True, "clearing a flag that is not set does nothing");
            Assert.That(lanes.SetSlide(0, 2, true).IsEmpty, Is.True, "no note in that row");
        }

        [AvaloniaTest]
        public void NeoPolyGlidesIntoAFlaggedNoteAtItsSlideTimeAndOtherMachinesAndUnflaggedClipsAreAsTheyWere()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            Ok(Tool("song_set", new JsonObject { ["tempo_bpm"] = 120 }));
            var poly = rig.Add("Neo Poly", "Lead");
            Set(poly, "Slide", 1);
            Set(poly, "Voice mode", 2);
            Set(poly, "Drift", 0);
            Line("Lead", true);
            var flagged = Render("Lead", "slide-flagged");

            // The same notes without the flag.
            Ok(Tool("notes_write", new JsonObject
            {
                ["machine"] = "Lead", ["clip"] = "Line", ["mode"] = "replace",
                ["notes"] = new JsonArray { new JsonObject { ["pitch"] = 36, ["start"] = 0, ["length"] = 0.5 }, new JsonObject { ["pitch"] = 43, ["start"] = 1, ["length"] = 0.5 } },
            }));
            var plain = Render("Lead", "slide-plain");
            Assert.That(flagged.Length, Is.EqualTo(plain.Length));
            int rate = 44100;
            int second = (int)(rate * 0.5);                      // beat 1 at 120 BPM
            Assert.That(Enumerable.Range(0, second).All(i => flagged[i] == plain[i]), Is.True, "everything before the flagged note is the same bits");
            double diff = 0;
            for (int i = second; i < Math.Min(flagged.Length, second + rate / 10); i++) diff += Math.Abs(flagged[i] - plain[i]);
            Assert.That(diff, Is.GreaterThan(1.0), "the flagged note glides in: its first 100 ms differ from the plain note's");

            // With the Slide switch off the flag says nothing.
            Set(poly, "Slide", 0);
            Ok(Tool("notes_write", new JsonObject
            {
                ["machine"] = "Lead", ["clip"] = "Line", ["mode"] = "replace",
                ["notes"] = new JsonArray { new JsonObject { ["pitch"] = 36, ["start"] = 0, ["length"] = 0.5 }, new JsonObject { ["pitch"] = 43, ["start"] = 1, ["length"] = 0.5, ["slide"] = true } },
            }));
            var off = Render("Lead", "slide-off");
            Set(poly, "Slide", 0);
            Ok(Tool("notes_write", new JsonObject
            {
                ["machine"] = "Lead", ["clip"] = "Line", ["mode"] = "replace",
                ["notes"] = new JsonArray { new JsonObject { ["pitch"] = 36, ["start"] = 0, ["length"] = 0.5 }, new JsonObject { ["pitch"] = 43, ["start"] = 1, ["length"] = 0.5 } },
            }));
            var offPlain = Render("Lead", "slide-off-plain");
            Assert.That(off.SequenceEqual(offPlain), Is.True, "Slide off: a flagged note renders bit for bit as a plain one");

            // Another machine takes the flagged note as it takes every note.
            rig.Add("Neo Drums", "Kit");
            Line("Kit", true);
            var kitFlagged = Render("Kit", "kit-flagged");
            Ok(Tool("notes_write", new JsonObject
            {
                ["machine"] = "Kit", ["clip"] = "Line", ["mode"] = "replace",
                ["notes"] = new JsonArray { new JsonObject { ["pitch"] = 36, ["start"] = 0, ["length"] = 0.5 }, new JsonObject { ["pitch"] = 43, ["start"] = 1, ["length"] = 0.5 } },
            }));
            var kitPlain = Render("Kit", "kit-plain");
            Assert.That(kitFlagged.Any(x => Math.Abs(x) > 1e-4f), Is.True, "the kit sounds");
            Assert.That(kitFlagged.SequenceEqual(kitPlain), Is.True, "a machine without slides ignores the flag: the same bits");
        }
    }
}
