// Fork (TM1, docs/specs/machines.md Neo Sampler Tracker mode): a sample per note in the clip store, headless. A Neo Sampler in Tracker mode keeps each
// note's sample, offset and tune beside its pitch and velocity (SongNotes, the tracker's store), one undo step an edit, saved in the .nbl and
// back; a sampler in another mode, and any song saved before, keep their notes as they were; the agent reads and writes them.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.FileOps.Nbl;
using ReBuzz.Studio.Shell;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class SamplerPerNoteTests
    {
        const int TB = ClipNotes.TimeBase;

        static string Work(string name)
        {
            string dir = TestTemp.Path($"sampler-per-note-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        static void Set(IMachine m, string name, int value) => m.ParameterGroups[1].Parameters.First(p => p.Name == name).SetValue(0, value);

        static MachineCore Sampler(SessionRig rig, string name, int mode)
        {
            var m = rig.Add("Neo Sampler", name);
            Set(m, "Mode", mode);
            return m;
        }

        static ClipNote[] Breaks() => new[]
        {
            new ClipNote(0, TB, 48, 100, 0, 1),
            new ClipNote(TB, TB, 48, 80, 0, 2, 0, 0),
            new ClipNote(2 * TB, 2 * TB, 46, 127, 0, 3, 123, 0),
            new ClipNote(4 * TB, TB, 48, 64, 0, 201, 40000, -37),      // an offset over the low parameter's 32,767: both parts
            new ClipNote(5 * TB, TB, 55, 90, 0, 250, 1, 1200),
            new ClipNote(6 * TB, TB, 48, 100),                         // no sample: the zones play it
        };

        static void AssertSame(System.Collections.Generic.IEnumerable<ClipNote> actual, ClipNote[] expected) =>
            Assert.That(actual.OrderBy(n => n.Time).ToArray(), Is.EqualTo(expected.OrderBy(n => n.Time).ToArray()));

        [AvaloniaTest]
        public void ANoteKeepsItsSampleOffsetAndTuneInTheClipOneUndoStepAnEditAndThroughASave()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var machine = Sampler(rig, "Breaks", 2);
            var clip = rig.Clip(machine, "Jungle", 16);
            var source = SongNotes.For(core, clip);
            Assert.That(source, Is.InstanceOf<TrackerColumnNotes>(), "Tracker mode keeps its notes in the tracks");
            Assert.That(((TrackerColumnNotes)source).PerNoteSamples, Is.True);

            int steps = core.SongCore.ActionStack.Actions.Count();
            Assert.That(source.Edit(null, Breaks()), Is.Null);
            Assert.That(core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "one undo step for the six notes");
            AssertSame(SongNotes.For(core, clip).Notes(), Breaks());

            // The values are the track parameters beside the note (what the machine plays, and the Tracker shows).
            var track = machine.ParameterGroups[2].Parameters;
            Assert.That(track.Select(p => p.Name), Is.EqualTo(new[] { "Note", "Velocity", "Sample", "Offset", "Offset hi", "Tune" }));
            var sampleColumn = clip.Columns.Single(c => c.Parameter?.Name == "Sample");
            Assert.That(sampleColumn.GetEvents(int.MinValue, int.MaxValue).Select(e => e.Value).OrderBy(v => v), Is.EqualTo(new[] { 1, 2, 3, 201, 250 }));

            // Editing one note's sample is one more step, and the others stay.
            var all = SongNotes.For(core, clip).Notes();
            var second = all.Single(n => n.Time == TB);
            Assert.That(SongNotes.For(core, clip).Edit(new[] { second }, new[] { second with { Sample = 9, Offset = 5, Tune = 50 } }), Is.Null);
            Assert.That(core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 2));
            core.SongCore.ActionStack.Undo();
            AssertSame(SongNotes.For(core, clip).Notes(), Breaks());
            core.SongCore.ActionStack.Redo();
            Assert.That(SongNotes.For(core, clip).Notes().Single(n => n.Time == TB).Sample, Is.EqualTo(9));
            core.SongCore.ActionStack.Undo();
            core.SongCore.ActionStack.Undo();
            Assert.That(SongNotes.For(core, clip).Notes(), Is.Empty, "undone to the empty clip");
            core.SongCore.ActionStack.Redo();
            AssertSame(SongNotes.For(core, clip).Notes(), Breaks());

            // A note that moves takes its values with it.
            var moved = ClipNotes.Move(new[] { Breaks()[3] }, 3 * TB, 0, TB, 16 * TB);
            Assert.That(SongNotes.For(core, clip).Edit(new[] { Breaks()[3] }, moved), Is.Null);
            var now = SongNotes.For(core, clip).Notes().Single(n => n.Time == 7 * TB);
            Assert.That((now.Sample, now.Offset, now.Tune, now.Velocity), Is.EqualTo((201, 40000, -37, 64)));
            core.SongCore.ActionStack.Undo();

            // Saved in the .nbl and back: every value.
            string path = Work("breaks.nbl");
            core.SaveSongCopy(path);
            core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(core.TryOpenSongFile(path), Is.True);
            Dialogs.Responder = null;
            var reopened = core.SongCore.MachinesList.Single(m => m.Name == "Breaks");
            Assert.That(reopened.ParameterGroups[1].Parameters.First(p => p.Name == "Mode").GetValue(0), Is.EqualTo(2), "Tracker mode is saved with the song");
            var again = SongNotes.For(core, reopened.ClipsList.Single(p => p.Name == "Jungle"));
            Assert.That(again, Is.InstanceOf<TrackerColumnNotes>());
            AssertSame(again.Notes(), Breaks());
        }

        [AvaloniaTest]
        public void AnotherModeAndAnOlderSongKeepTheirNotes()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var keys = Sampler(rig, "Keys", 0);
            var clip = rig.Clip(keys, "Riff", 16, (0, 48), (1, 50), (2, 52));
            var before = SongNotes.For(core, clip);
            Assert.That(before, Is.InstanceOf<MidiColumnNotes>(), "Instrument mode plays its MIDI column, as it always did");
            var notes = before.Notes();
            Assert.That(notes.Select(n => (n.Note, n.Sample, n.Offset, n.Tune)), Is.EqualTo(new[] { (48, 0, 0, 0), (50, 0, 0, 0), (52, 0, 0, 0) }));

            // The song as it was saved before the state had a version: the sampler's entry at version 0, its parameters by key.
            string current = Work("keys-v1.nbl"), old = Work("keys-v0.nbl");
            core.SaveSongCopy(current);
            using (var reader = new NblContainer.Reader(current))
            {
                var manifest = (JsonObject)JsonNode.Parse(reader.Manifest);
                var entry = manifest["machines"].AsArray().First(e => (string)e["name"] == "Keys");
                Assert.That((int)entry["stateVersion"], Is.EqualTo(1), "this build saves the sampler's state at version 1");
                entry["stateVersion"] = 0;
                var blobs = new NblBlobSet();
                foreach (var h in reader.Hashes) blobs.Add(reader.Get(h));
                NblContainer.WriteAtomic(old, System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()), blobs);
            }
            core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(core.TryOpenSongFile(old), Is.True);
            Dialogs.Responder = null;
            var reopened = core.SongCore.MachinesList.Single(m => m.Name == "Keys");
            Assert.That(reopened.ParameterGroups[1].Parameters.First(p => p.Name == "Mode").GetValue(0), Is.EqualTo(0));
            var source = SongNotes.For(core, reopened.ClipsList.Single(p => p.Name == "Riff"));
            Assert.That(source, Is.InstanceOf<MidiColumnNotes>());
            Assert.That(source.Notes(), Is.EqualTo(notes), "the notes are as they were");
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

        [AvaloniaTest]
        public void TheAgentReadsAndSetsASamplesOffsetAndTunePerNote()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var machine = Sampler(rig, "Breaks", 2);
            rig.Clip(machine, "Jungle", 16);
            var written = Tool("notes_write", new JsonObject
            {
                ["machine"] = "Breaks", ["clip"] = "Jungle",
                ["notes"] = new JsonArray
                {
                    new JsonObject { ["pitch"] = 48, ["start"] = 0, ["length"] = 0.25, ["velocity"] = 90, ["sample"] = 2 },
                    new JsonObject { ["pitch"] = 46, ["start"] = 0.25, ["length"] = 0.25, ["sample"] = 7, ["offset"] = 40000, ["tune"] = -37 },
                    new JsonObject { ["pitch"] = 48, ["start"] = 0.5, ["length"] = 0.25 },
                },
            });
            Assert.That(written["isError"]?.GetValue<bool>() == true, Is.False, written.ToJsonString());
            var read = Tool("notes_read", new JsonObject { ["machine"] = "Breaks", ["clip"] = "Jungle" });
            var notes = read["structuredContent"]!["notes"]!.AsArray().Select(n => n!.AsObject()).OrderBy(n => (double)n["start"]!).ToList();
            Assert.That(read["structuredContent"]!["input"]!.GetValue<string>(), Is.EqualTo("tracks"));
            Assert.That(notes.Select(n => (int?)n["sample"]), Is.EqualTo(new int?[] { 2, 7, null }));
            Assert.That(notes[0]["velocity"]!.GetValue<int>(), Is.EqualTo(90), "the volume is the velocity");
            Assert.That((int)notes[1]["offset"]!, Is.EqualTo(40000));
            Assert.That((int)notes[1]["tune"]!, Is.EqualTo(-37));
            Assert.That(notes[2]["offset"], Is.Null);

            // Instrument mode: a sample is refused, in words.
            Set(machine, "Mode", 0);
            var refused = Tool("notes_write", new JsonObject
            {
                ["machine"] = "Breaks", ["clip"] = "Jungle", ["mode"] = "replace",
                ["notes"] = new JsonArray { new JsonObject { ["pitch"] = 48, ["start"] = 0, ["length"] = 1, ["sample"] = 2 } },
            });
            Assert.That(refused["isError"]?.GetValue<bool>(), Is.True);
            Assert.That(refused.ToJsonString(), Does.Contain("Tracker mode"));
        }
    }
}
