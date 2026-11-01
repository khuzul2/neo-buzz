using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using ReBuzz.Agent.Audio;
using Rig = ReBuzzTests.AgentServerTests.Rig;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/mcp-server.md §9, A3): song markers saved in the song, automation
    /// the engine plays, the Session View's grid, machine data imports and renders turned into
    /// samples, through the tools.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class AgentA3Tests
    {
        static JsonObject Ok(Rig rig, string tool, JsonObject args = null)
        {
            var r = rig.Call(tool, args);
            Assert.That(r.Error, Is.False, $"{tool}: {r.Text}");
            TestContext.Out.WriteLine($"{tool}: {r.Text}");
            return r.Data;
        }

        static string TempFile(string name)
        {
            string path = Path.Combine(Path.GetTempPath(), "ReBuzzAgentTests", Guid.NewGuid().ToString("N"), name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        [Test]
        public void MarkersAutomationAndSections()
        {
            using var rig = new Rig(NeoMachineTests.Poly);
            Ok(rig, "song_set", new JsonObject { ["tempo_bpm"] = 120 });
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Pad" });
            Ok(rig, "pattern_create", new JsonObject { ["machine"] = "Pad", ["name"] = "Sweep", ["length_beats"] = 8 });
            Ok(rig, "notes_write", new JsonObject
            {
                ["machine"] = "Pad", ["pattern"] = "Sweep",
                ["notes"] = new JsonArray(new JsonObject { ["pitch"] = "C3", ["start"] = 0, ["length"] = 8 }),
            });
            Ok(rig, "arrangement_place", new JsonObject { ["machine"] = "Pad", ["pattern"] = "Sweep", ["at"] = new JsonArray(0, 8) });

            // Automation: a cutoff sweep up, read back as its two corners.
            var a = Ok(rig, "automation_write", new JsonObject
            {
                ["machine"] = "Pad", ["pattern"] = "Sweep", ["param"] = "F1 cutoff",
                ["points"] = new JsonArray(new JsonObject { ["at"] = 0, ["value"] = "200 Hz" }, new JsonObject { ["at"] = 8, ["value"] = "8 kHz" }),
            });
            Assert.That(a["events"]!.GetValue<int>(), Is.GreaterThan(10), "a linear ramp is many events");
            var read = Ok(rig, "automation_read", new JsonObject { ["machine"] = "Pad", ["pattern"] = "Sweep" })["automation"]!.AsArray().Single()!;
            Assert.That(read["param"]!.GetValue<string>(), Is.EqualTo("F1 cutoff"));
            Assert.That(read["points"]!.AsArray().Count, Is.EqualTo(2));
            // The engine plays it: the cutoff ends high after a render of the pattern.
            Ok(rig, "render", new JsonObject { ["from"] = 0, ["to"] = 8, ["tail_seconds"] = 0 });
            var cutoff = Ok(rig, "param_get", new JsonObject { ["machine"] = "Pad", ["params"] = new JsonArray("F1 cutoff") })["values"]![0]!["number"]!.GetValue<double>();
            Assert.That(cutoff, Is.GreaterThan(4000), "the automation moved the parameter while the pattern played");
            Ok(rig, "undo");
            Assert.That(Ok(rig, "automation_read", new JsonObject { ["machine"] = "Pad", ["pattern"] = "Sweep" })["automation"]!.AsArray().Count, Is.EqualTo(0));
            Ok(rig, "redo");

            // Markers: sections for renders, saved with the song.
            Ok(rig, "arrangement_markers", new JsonObject
            {
                ["set"] = new JsonArray(new JsonObject { ["name"] = "Intro", ["at"] = 0 }, new JsonObject { ["name"] = "Main", ["at"] = "3.1" }),
            });
            var section = Ok(rig, "render", new JsonObject { ["section"] = "main", ["tail_seconds"] = 0 });
            Assert.That(section["from_beats"]!.GetValue<double>(), Is.EqualTo(8));
            Assert.That(section["to_beats"]!.GetValue<double>(), Is.EqualTo(16));
            string song = TempFile("markers.bmxml");
            Ok(rig, "song_save", new JsonObject { ["path"] = song });
            Ok(rig, "song_new", new JsonObject { ["discard_unsaved"] = true });
            Assert.That(Ok(rig, "arrangement_markers")["markers"]!.AsArray().Count, Is.EqualTo(0), "a new song has no markers");
            Ok(rig, "song_open", new JsonObject { ["path"] = song });
            var markers = Ok(rig, "arrangement_markers")["markers"]!.AsArray();
            Assert.That(markers.Select(m => m!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "Intro", "Main" }));
            Assert.That(Ok(rig, "automation_read", new JsonObject { ["machine"] = "Pad", ["pattern"] = "Sweep" })["automation"]!.AsArray().Count, Is.EqualTo(1),
                "automation is saved with the song");
        }

        [Test]
        public void TheoryWritesAProgressionAndTransformsReshapeIt()
        {
            using var rig = new Rig(NeoMachineTests.Poly);
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Keys" });
            Ok(rig, "pattern_create", new JsonObject { ["machine"] = "Keys", ["name"] = "Chords", ["length_beats"] = 16 });
            var prog = Ok(rig, "theory", new JsonObject { ["op"] = "progression", ["progression"] = "i-VI-III-VII", ["key"] = "A minor", ["beats_per_chord"] = 4 });
            var notes = prog["notes"]!.AsArray();
            Assert.That(notes.Count, Is.GreaterThanOrEqualTo(12), "four chords of three notes or more");
            Ok(rig, "notes_write", new JsonObject { ["machine"] = "Keys", ["pattern"] = "Chords", ["notes"] = notes.DeepClone() });
            var before = Ok(rig, "notes_read", new JsonObject { ["machine"] = "Keys", ["pattern"] = "Chords" })["notes"]!.AsArray();
            Ok(rig, "notes_transform", new JsonObject
            {
                ["machine"] = "Keys", ["pattern"] = "Chords",
                ["transforms"] = new JsonArray(new JsonObject { ["op"] = "transpose", ["semitones"] = 12 }),
            });
            var after = Ok(rig, "notes_read", new JsonObject { ["machine"] = "Keys", ["pattern"] = "Chords" })["notes"]!.AsArray();
            Assert.That(after.Select(n => n!["pitch"]!.GetValue<int>()), Is.EqualTo(before.Select(n => n!["pitch"]!.GetValue<int>() + 12)));
            Ok(rig, "undo");
            var undone = Ok(rig, "notes_read", new JsonObject { ["machine"] = "Keys", ["pattern"] = "Chords" })["notes"]!.AsArray();
            Assert.That(undone.Select(n => n!["pitch"]!.GetValue<int>()), Is.EqualTo(before.Select(n => n!["pitch"]!.GetValue<int>())));
        }

        [Test]
        public void SessionGridAndRendersAsSamples()
        {
            using var rig = new Rig(NeoMachineTests.Poly);
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Lead" });
            Ok(rig, "pattern_create", new JsonObject { ["machine"] = "Lead", ["name"] = "A", ["length_beats"] = 4 });
            Ok(rig, "pattern_create", new JsonObject { ["machine"] = "Lead", ["name"] = "B", ["length_beats"] = 4 });
            Ok(rig, "session_clip_set", new JsonObject { ["machine"] = "Lead", ["scene"] = 1, ["pattern"] = "A" });
            var grid = Ok(rig, "session_clip_set", new JsonObject { ["machine"] = "Lead", ["scene"] = 2, ["pattern"] = "B", ["quantization"] = "Beat", ["mode"] = "Gate" });
            var lead = grid["tracks"]!.AsArray().Single(t => t!["machine"]!.GetValue<string>() == "Lead")!;
            Assert.That(lead["slots"]!.AsArray().Take(2).Select(s => s?.GetValue<string>()), Is.EqualTo(new[] { "A", "B" }));
            Assert.That(lead["clip_settings"]![0]!["mode"]!.GetValue<string>(), Is.EqualTo("Gate"));
            var scenes = Ok(rig, "session_scene_set", new JsonObject { ["scene"] = 2, ["name"] = "Chorus", ["tempo_bpm"] = 128 })["scenes"]!.AsArray();
            Assert.That(scenes[1]!["name"]!.GetValue<string>(), Is.EqualTo("Chorus"));
            Assert.That(scenes[1]!["tempo_bpm"]!.GetValue<int>(), Is.EqualTo(128));
            Ok(rig, "undo");
            Assert.That(Ok(rig, "session_get")["scenes"]!.AsArray()[1]!["name"], Is.Null, "undo takes the scene's name back");
            Ok(rig, "undo");
            var undone = Ok(rig, "session_get")["tracks"]!.AsArray().Single(t => t!["machine"]!.GetValue<string>() == "Lead")!;
            // (The grid filled itself from the song's patterns when first used, so the slot stays; its launch settings go.)
            Assert.That(undone["clip_settings"], Is.Null, "undo takes the clip's launch settings back");

            // A render becomes a sample.
            Ok(rig, "notes_write", new JsonObject { ["machine"] = "Lead", ["pattern"] = "A", ["notes"] = new JsonArray(new JsonObject { ["pitch"] = 64, ["start"] = 0, ["length"] = 2 }) });
            Ok(rig, "arrangement_place", new JsonObject { ["machine"] = "Lead", ["pattern"] = "A", ["at"] = 0 });
            var r = Ok(rig, "render", new JsonObject { ["tail_seconds"] = 0 });
            var wave = Ok(rig, "wave_from_render", new JsonObject { ["source"] = r["id"]!.GetValue<string>(), ["from_seconds"] = 0, ["to_seconds"] = 1, ["name"] = "Lead hit", ["root_note"] = 64 });
            Assert.That(wave["wave"]!["seconds"]!.GetValue<double>(), Is.EqualTo(1).Within(0.01));
            Assert.That(wave["wave"]!["root"]!["name"]!.GetValue<string>(), Is.EqualTo("E4"));
        }

        [Test]
        public void NeoWaveTakesAWavetableThroughDataImport()
        {
            using var rig = new Rig(NeoMachineTests.Wave);
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Wave", ["name"] = "Table" });
            var described = Ok(rig, "machine_describe", new JsonObject { ["machine"] = "Table", ["group"] = "Osc A" });
            Assert.That(described["data_imports"]![0]!["kind"]!.GetValue<string>(), Is.EqualTo("wavetable"));

            // Four frames of 2048 samples, sine to saw.
            string table = TempFile("morph.wav");
            int frame = 2048, frames = 4;
            var x = new float[frame * frames * 2];
            for (int f = 0; f < frames; f++)
                for (int i = 0; i < frame; i++)
                {
                    double ph = i / (double)frame, mix = f / (frames - 1.0);
                    float v = (float)((1 - mix) * Math.Sin(2 * Math.PI * ph) + mix * (2 * ph - 1));
                    x[2 * (f * frame + i)] = x[2 * (f * frame + i) + 1] = 0.8f * v;
                }
            Wav.WriteStereoFloat(table, x, frame * frames, 44100);
            var bad = rig.Call("machine_data_import", new JsonObject { ["machine"] = "Table", ["kind"] = "dx7_bank", ["path"] = table });
            Assert.That(bad.Error, Is.True);
            Assert.That(bad.Text, Does.Contain("wavetable"));
            var imported = Ok(rig, "machine_data_import", new JsonObject { ["machine"] = "Table", ["kind"] = "wavetable", ["path"] = table });
            Assert.That(imported["summary"]!.GetValue<string>(), Does.Contain("morph"));
            Ok(rig, "param_set", new JsonObject { ["machine"] = "Table", ["changes"] = new JsonArray(new JsonObject { ["param"] = "A table", ["value"] = "User" }) });
            Ok(rig, "pattern_create", new JsonObject { ["machine"] = "Table", ["name"] = "N", ["length_beats"] = 4 });
            Ok(rig, "notes_write", new JsonObject { ["machine"] = "Table", ["pattern"] = "N", ["notes"] = new JsonArray(new JsonObject { ["pitch"] = 57, ["start"] = 0, ["length"] = 4 }) });
            Ok(rig, "arrangement_place", new JsonObject { ["machine"] = "Table", ["pattern"] = "N", ["at"] = 0 });
            var r = Ok(rig, "render", new JsonObject { ["tail_seconds"] = 0 });
            Assert.That(r["measure"]!["silent"]!.GetValue<bool>(), Is.False, "the user table plays");
        }
    }
}
