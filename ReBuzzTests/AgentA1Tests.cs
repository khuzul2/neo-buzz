using ReBuzz.Core;
using ReBuzz.Core.Actions;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Rig = ReBuzzTests.AgentServerTests.Rig;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/mcp-server.md §9, A1 "Make and hear"): an agent builds a 4-bar
    /// piece with bass and chords from nothing through the tools alone, renders and measures it,
    /// reads its notes back, and every step undoes and redoes.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class AgentA1Tests
    {
        static (bool Error, string Text, JsonObject Data) Ok(Rig rig, string tool, JsonObject args = null)
        {
            var r = rig.Call(tool, args);
            Assert.That(r.Error, Is.False, $"{tool}: {r.Text}");
            TestContext.Out.WriteLine($"{tool}: {r.Text}");
            return r;
        }

        static JsonArray Notes(params (object Pitch, double Start, double Length)[] notes) =>
            new(notes.Select(n => (JsonNode)new JsonObject
            {
                ["pitch"] = n.Pitch is int p ? JsonValue.Create(p) : JsonValue.Create((string)n.Pitch),
                ["start"] = n.Start,
                ["length"] = n.Length,
            }).ToArray());

        static double Lufs(JsonObject render) => render["measure"]!["loudness"]!["integrated_lufs"]!.GetValue<double>();

        [Test]
        public void AnAgentBuildsHearsAndUndoesA4BarPiece()
        {
            using var rig = new Rig(NeoMachineTests.Poly, NeoMachineTests.Verb);
            int steps = 0;
            (bool Error, string Text, JsonObject Data) Change(string tool, JsonObject args)
            {
                steps++;
                return Ok(rig, tool, args);
            }

            int bpmBefore = rig.Driver.Core.BPM;
            Change("song_set", new JsonObject { ["tempo_bpm"] = 120 });

            var gear = Ok(rig, "gear_list", new JsonObject { ["kind"] = "generator", ["search"] = "neo" });
            var poly = gear.Data["gear"]!.AsArray().Single(g => g!["name"]!.GetValue<string>() == "Neo Poly")!;
            Assert.That(poly["exact_units"]!.GetValue<bool>(), Is.True, "Neo Poly's manifest is found beside its DLL");
            Assert.That(poly["summary"]!.GetValue<string>(), Does.Contain("Use it"));

            var bass = Change("machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Bass", ["gain_db"] = -6 });
            Assert.That(bass.Data["machine"]!["outputs"]![0]!["to"]!.GetValue<string>(), Is.EqualTo("Master"));
            Assert.That(bass.Data["machine"]!["outputs"]![0]!["gain_db"]!.GetValue<double>(), Is.EqualTo(-6).Within(0.01));
            Change("machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Chords" });
            var room = Change("machine_add", new JsonObject { ["gear"] = "neo verb", ["name"] = "Room", ["insert_after"] = "Chords" });
            Assert.That(room.Data["machine"]!["outputs"]![0]!["to"]!.GetValue<string>(), Is.EqualTo("Master"), "the effect took over Chords' output");
            var list = Ok(rig, "machine_list").Data["machines"]!.AsArray();
            var chords = list.Single(m => m!["name"]!.GetValue<string>() == "Chords")!;
            Assert.That(chords["outputs"]!.AsArray().Select(o => o!["to"]!.GetValue<string>()), Is.EqualTo(new[] { "Room" }));

            var dup = rig.Call("machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "bass" });
            Assert.That(dup.Error, Is.True);
            Assert.That(dup.Text, Does.Contain("already a machine named"));
            var typo = rig.Call("machine_add", new JsonObject { ["gear"] = "Neo Polly" });
            Assert.That(typo.Text, Does.Contain("Did you mean \"Neo Poly\""));

            // Sound design in units.
            var filter = Ok(rig, "machine_describe", new JsonObject { ["machine"] = "Bass", ["group"] = "Filter 1" });
            Assert.That(filter.Data["exact_units"]!.GetValue<bool>(), Is.True);
            var cutoff = filter.Data["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "F1 cutoff")!;
            Assert.That(cutoff["unit"]!.GetValue<string>(), Is.EqualTo("Hz"));
            var set = Change("param_set", new JsonObject
            {
                ["machine"] = "Bass",
                ["changes"] = new JsonArray(
                    new JsonObject { ["param"] = "F1 cutoff", ["value"] = "800 Hz" },
                    new JsonObject { ["param"] = "Voice mode", ["value"] = "mono" },
                    new JsonObject { ["param"] = "Volume", ["value"] = "-6 dB" }),
            });
            var got = Ok(rig, "param_get", new JsonObject { ["machine"] = "Bass", ["params"] = new JsonArray("F1 cutoff", "Voice mode", "Volume") }).Data["values"]!.AsArray();
            Assert.That(got[0]!["number"]!.GetValue<double>(), Is.EqualTo(800).Within(800 * 0.02), set.Text);
            Assert.That(got[1]!["value"]!.GetValue<string>(), Is.EqualTo("Mono"));
            Assert.That(got[2]!["number"]!.GetValue<double>(), Is.EqualTo(-6).Within(0.1));
            var badParam = rig.Call("param_set", new JsonObject
            {
                ["machine"] = "Bass",
                ["changes"] = new JsonArray(new JsonObject { ["param"] = "F1 cutoff", ["value"] = "2 kHz" }, new JsonObject { ["param"] = "Cutof", ["value"] = 1 }),
            });
            Assert.That(badParam.Error, Is.True);
            Assert.That(badParam.Text, Does.Contain("Did you mean"));
            Assert.That(Ok(rig, "param_get", new JsonObject { ["machine"] = "Bass", ["params"] = new JsonArray("F1 cutoff") }).Data["values"]![0]!["number"]!.GetValue<double>(),
                Is.EqualTo(800).Within(16), "a failed call changes nothing");

            // Notes and the arrangement.
            Change("pattern_create", new JsonObject { ["machine"] = "Bass", ["name"] = "Riff", ["length_beats"] = 16 });
            Change("pattern_create", new JsonObject { ["machine"] = "Chords", ["name"] = "Pads", ["length_beats"] = 16 });
            var riff = Notes(("A1", 0, 3), ("A1", 4, 3), ("F1", 8, 3), ("G1", 12, 3));
            Change("notes_write", new JsonObject { ["machine"] = "Bass", ["pattern"] = "Riff", ["notes"] = riff });
            var pads = Change("notes_write", new JsonObject
            {
                ["machine"] = "Chords", ["pattern"] = "Pads",
                ["notes"] = Notes(("A3", 0, 8), ("C4", 0, 8), ("E4", 0, 8), ("F3", 8, 8), ("A3", 8, 8), ("C4", 8, 8)),
            });
            Assert.That(pads.Data["total"]!.GetValue<int>(), Is.EqualTo(6));
            Assert.That(pads.Data["polyphony"]!.GetValue<int>(), Is.EqualTo(3));
            Assert.That(pads.Text, Does.Contain("isn't placed in the arrangement yet"));
            var read = Ok(rig, "notes_read", new JsonObject { ["machine"] = "Bass", ["pattern"] = "Riff" }).Data["notes"]!.AsArray();
            Assert.That(read.Select(n => n!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "A1", "A1", "F1", "G1" }));
            Assert.That(read.Select(n => n!["start"]!.GetValue<double>()), Is.EqualTo(new[] { 0.0, 4, 8, 12 }));
            Assert.That(read.Select(n => n!["length"]!.GetValue<double>()), Is.All.EqualTo(3.0));

            Change("arrangement_place", new JsonObject { ["machine"] = "Bass", ["pattern"] = "Riff", ["at"] = 0 });
            var placed = Change("arrangement_place", new JsonObject { ["machine"] = "Chords", ["pattern"] = "Pads", ["at"] = "1.1" });
            Assert.That(placed.Data["song_end_beats"]!.GetValue<double>(), Is.EqualTo(16));
            var arrangement = Ok(rig, "arrangement_get").Data;
            Assert.That(arrangement["lanes"]!.AsArray().Count(l => l!["events"]!.AsArray().Count > 0), Is.EqualTo(2));

            // Hear and measure.
            var mix = Ok(rig, "render").Data;
            Assert.That(mix["measure"]!["silent"]!.GetValue<bool>(), Is.False, "notes reach the synths and the mix reaches the render");
            Assert.That(mix["seconds"]!.GetValue<double>(), Is.EqualTo(8 + 2).Within(0.05), "16 beats at 120 BPM and a 2 s tail");
            Assert.That(File.Exists(mix["file"]!.GetValue<string>()), Is.True);
            double mixLufs = Lufs(mix);
            Assert.That(mixLufs, Is.InRange(-40, -3));
            Assert.That(mix["measure"]!["peaks"]!["clipped_samples"]!.GetValue<int>(), Is.EqualTo(0));
            var again = Ok(rig, "analyze", new JsonObject { ["source"] = mix["id"]!.GetValue<string>() }).Data;
            Assert.That(again["loudness"]!["integrated_lufs"]!.GetValue<double>(), Is.EqualTo(mixLufs).Within(0.01));
            var bassAlone = Ok(rig, "render", new JsonObject { ["solo"] = new JsonArray("Bass"), ["tail_seconds"] = 0 }).Data;
            Assert.That(bassAlone["seconds"]!.GetValue<double>(), Is.EqualTo(8).Within(0.05));
            Assert.That(Lufs(bassAlone), Is.LessThan(mixLufs), "the bass alone is quieter than the mix");
            Assert.That(rig.Driver.Core.SongCore.MachinesList.Any(m => m.IsMuted), Is.False, "solo leaves the song as it was");
            Assert.That(rig.Driver.Core.Modified, Is.True, "the song was changed before rendering");

            // Undo walks everything back, one call at a time; redo replays it.
            for (int i = 0; i < steps; i++)
                Assert.That(rig.Call("undo").Error, Is.False, $"undo {i + 1} of {steps}");
            var empty = Ok(rig, "machine_list").Data["machines"]!.AsArray();
            Assert.That(empty.Select(m => m!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "Master" }));
            Assert.That(rig.Driver.Core.BPM, Is.EqualTo(bpmBefore));
            for (int i = 0; i < steps; i++)
                Assert.That(rig.Call("redo").Error, Is.False, $"redo {i + 1} of {steps}");
            var back = Ok(rig, "notes_read", new JsonObject { ["machine"] = "Chords", ["pattern"] = "Pads" }).Data["notes"]!.AsArray();
            Assert.That(back.Count, Is.EqualTo(6));
            Assert.That(Ok(rig, "param_get", new JsonObject { ["machine"] = "Bass", ["params"] = new JsonArray("F1 cutoff") }).Data["values"]![0]!["number"]!.GetValue<double>(),
                Is.EqualTo(800).Within(16));
            var replay = Ok(rig, "render").Data;
            Assert.That(Lufs(replay), Is.EqualTo(mixLufs).Within(1.0), "the redone song sounds the same");
        }

        [Test]
        public void RepeatedNotesOfOnePitchEachSoundTheirFullLength()
        {
            // Engine fix: a note-off due where the next note of the same pitch starts went out a
            // block late, after the new note-on, and cut it to a few milliseconds.
            using var rig = new Rig(NeoMachineTests.Poly);
            Ok(rig, "song_set", new JsonObject { ["tempo_bpm"] = 120 });
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Bass" });
            Ok(rig, "pattern_create", new JsonObject { ["machine"] = "Bass", ["name"] = "Eighths", ["length_beats"] = 4 });
            Ok(rig, "pattern_create", new JsonObject { ["machine"] = "Bass", ["name"] = "Held", ["length_beats"] = 4 });
            Ok(rig, "notes_write", new JsonObject
            {
                ["machine"] = "Bass", ["pattern"] = "Eighths",
                ["notes"] = Notes(Enumerable.Range(0, 8).Select(i => ((object)"A2", i * 0.5, 0.5)).ToArray()),
            });
            Ok(rig, "notes_write", new JsonObject { ["machine"] = "Bass", ["pattern"] = "Held", ["notes"] = Notes(("A2", 0, 4)) });
            Ok(rig, "arrangement_place", new JsonObject { ["machine"] = "Bass", ["pattern"] = "Eighths", ["at"] = 0 });
            Ok(rig, "arrangement_place", new JsonObject { ["machine"] = "Bass", ["pattern"] = "Held", ["at"] = 4 });
            var r = Ok(rig, "render", new JsonObject { ["from"] = 0, ["to"] = 8, ["tail_seconds"] = 0 }).Data;
            string id = r["id"]!.GetValue<string>();
            // Beats 1–4 of each pattern (0.5–2 s and 2.5–4 s at 120 BPM): the repeated notes sound about as loud as the held one.
            double eighths = Ok(rig, "analyze", new JsonObject { ["source"] = id, ["from_seconds"] = 0.5, ["to_seconds"] = 2 }).Data["loudness"]!["integrated_lufs"]!.GetValue<double>();
            double held = Ok(rig, "analyze", new JsonObject { ["source"] = id, ["from_seconds"] = 2.5, ["to_seconds"] = 4 }).Data["loudness"]!["integrated_lufs"]!.GetValue<double>();
            Assert.That(eighths, Is.GreaterThan(held - 6), $"repeated notes {eighths:F1} LUFS vs held {held:F1} LUFS");
        }

        [Test]
        public void PatternsRenameDuplicateAndDeleteWithTheirNotesAndPlaces()
        {
            using var rig = new Rig(NeoMachineTests.Poly);
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Lead" });
            Ok(rig, "pattern_create", new JsonObject { ["machine"] = "Lead", ["length_beats"] = 8 });
            var made = Ok(rig, "pattern_list", new JsonObject { ["machine"] = "Lead" }).Data["machines"]![0]!["patterns"]!.AsArray();
            string first = made.Single()!["name"]!.GetValue<string>();
            Ok(rig, "notes_write", new JsonObject { ["machine"] = "Lead", ["pattern"] = first, ["notes"] = Notes((72, 0, 1), (74, 1, 1), (76, 2, 2)) });
            Ok(rig, "pattern_rename", new JsonObject { ["machine"] = "Lead", ["pattern"] = first, ["new_name"] = "Hook" });
            Ok(rig, "pattern_duplicate", new JsonObject { ["machine"] = "Lead", ["pattern"] = "Hook", ["new_name"] = "Hook 2" });
            Ok(rig, "arrangement_place", new JsonObject { ["machine"] = "Lead", ["pattern"] = "Hook", ["at"] = new JsonArray(0, 16), ["repeat_every_beats"] = 32, ["times"] = 2 });
            var patterns = Ok(rig, "pattern_list", new JsonObject { ["machine"] = "Lead" }).Data["machines"]![0]!["patterns"]!.AsArray();
            var hook = patterns.Single(p => p!["name"]!.GetValue<string>() == "Hook")!;
            Assert.That(hook["placed_at"]!.AsArray().Select(x => x!.GetValue<double>()), Is.EqualTo(new[] { 0.0, 16, 32, 48 }));
            Assert.That(patterns.Single(p => p!["name"]!.GetValue<string>() == "Hook 2")!["notes"]!.GetValue<int>(), Is.EqualTo(3));

            var deleted = Ok(rig, "pattern_delete", new JsonObject { ["machine"] = "Lead", ["pattern"] = "Hook" });
            Assert.That(deleted.Data["was_placed"]!.GetValue<int>(), Is.EqualTo(4));
            Assert.That(rig.Call("notes_read", new JsonObject { ["machine"] = "Lead", ["pattern"] = "Hook" }).Text, Does.Contain("Did you mean \"Hook 2\""));
            Ok(rig, "undo");
            var restored = Ok(rig, "pattern_list", new JsonObject { ["machine"] = "Lead" }).Data["machines"]![0]!["patterns"]!.AsArray()
                .Single(p => p!["name"]!.GetValue<string>() == "Hook")!;
            Assert.That(restored["notes"]!.GetValue<int>(), Is.EqualTo(3), "undo brings the notes back");
            Assert.That(restored["placed_at"]!.AsArray().Count, Is.EqualTo(4), "and the places");

            var cleared = Ok(rig, "arrangement_clear", new JsonObject { ["machine"] = "Lead", ["from"] = 16, ["to"] = "13.1" });
            Assert.That(cleared.Data["removed"]!.GetValue<int>(), Is.EqualTo(2));
        }

        /// <summary>history-snapshots.md HS1b: the shared history as a list, and a jump over several steps, the person's included.</summary>
        [Test]
        public void HistoryListAndJumpWalkTheSharedHistory()
        {
            using var rig = new Rig(NeoMachineTests.Poly);
            var core = rig.Driver.Core;
            Ok(rig, "song_set", new JsonObject { ["tempo_bpm"] = 100 });
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Bass" });
            TransportEdits.SetTempo(core, 111);                       // a person's edit in the window
            Ok(rig, "song_set", new JsonObject { ["tempo_bpm"] = 130 });

            var first = Ok(rig, "history_list").Data;
            var steps = first["steps"]!.AsArray();
            Assert.That(steps.Count, Is.GreaterThanOrEqualTo(4));
            Assert.That(steps[0]!["current"]!.GetValue<bool>(), Is.True, "newest first, and it is where we are");
            Assert.That(steps[0]!["source"]!.GetValue<string>(), Is.EqualTo("agent"));
            Assert.That(steps.Select(x => x!["source"]!.GetValue<string>()), Does.Contain("manual"), "the person's edit is a step too");
            Assert.That(Ok(rig, "history_list", new JsonObject { ["limit"] = 2 }).Data["steps"]!.AsArray().Count, Is.EqualTo(2));

            int manual = steps.First(x => x!["source"]!.GetValue<string>() == "manual")!["id"]!.GetValue<int>();
            var jumped = Ok(rig, "history_jump", new JsonObject { ["step"] = manual });
            Assert.That(jumped.Error, Is.False, jumped.Text);
            Assert.That(core.BPM, Is.EqualTo(111), "the song is right after the person's edit");
            Assert.That(jumped.Data["current"]!.GetValue<int>(), Is.EqualTo(manual));
            Assert.That(jumped.Data["can_redo"]!.GetValue<bool>(), Is.True);

            int newest = steps[0]!["id"]!.GetValue<int>();
            Ok(rig, "history_jump", new JsonObject { ["step"] = newest });
            Assert.That(core.BPM, Is.EqualTo(130), "forward again");
            Ok(rig, "history_jump", new JsonObject { ["step"] = 0 });
            Assert.That(core.BPM, Is.Not.EqualTo(130), "before the oldest step");
            var refused = rig.Call("history_jump", new JsonObject { ["step"] = 987654 });
            Assert.That(refused.Error, Is.True);
            Assert.That(refused.Text, Does.Contain("history_list"));

            // A failed call's taken-back step is not on the list.
            int before = Ok(rig, "history_list").Data["total"]!.GetValue<int>();
            Assert.That(rig.Call("machine_remove", new JsonObject { ["machines"] = new JsonArray("No such machine") }).Error, Is.True);
            Assert.That(Ok(rig, "history_list").Data["total"]!.GetValue<int>(), Is.EqualTo(before));
        }
    }
}
