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
    /// Fork addition (docs/specs/mcp-server.md §9, A2): probes in renders, presets, snapshots for
    /// A/B comparison and the wavetable, through the tools; each change one undo step.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class AgentA2Tests
    {
        static JsonObject Ok(Rig rig, string tool, JsonObject args = null)
        {
            var r = rig.Call(tool, args);
            Assert.That(r.Error, Is.False, $"{tool}: {r.Text}");
            TestContext.Out.WriteLine($"{tool}: {r.Text}");
            return r.Data;
        }

        static JsonArray Chord(params string[] pitches) =>
            new(pitches.Select(p => (JsonNode)new JsonObject { ["pitch"] = p, ["start"] = 0, ["length"] = 4 }).ToArray());

        static double Lufs(JsonNode measure) => measure["loudness"]!["integrated_lufs"]!.GetValue<double>();

        [Test]
        public void ProbesPresetsAndSnapshots()
        {
            using var rig = new Rig(NeoMachineTests.Poly);
            Ok(rig, "song_set", new JsonObject { ["tempo_bpm"] = 120 });
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Pad" });
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Bass", ["gain_db"] = -6 });
            foreach (var (m, notes) in new[] { ("Pad", Chord("A3", "C4", "E4")), ("Bass", Chord("A1")) })
            {
                Ok(rig, "pattern_create", new JsonObject { ["machine"] = m, ["name"] = "P", ["length_beats"] = 4 });
                Ok(rig, "notes_write", new JsonObject { ["machine"] = m, ["pattern"] = "P", ["notes"] = notes });
                Ok(rig, "arrangement_place", new JsonObject { ["machine"] = m, ["pattern"] = "P", ["at"] = 0 });
            }

            // Probes: each machine's part of the mix, beside the master.
            var r = Ok(rig, "render", new JsonObject { ["probes"] = new JsonArray("Pad", "Bass"), ["tail_seconds"] = 0 });
            var probes = r["probes"]!.AsArray();
            Assert.That(probes.Select(p => p!["id"]!.GetValue<string>()), Is.EqualTo(new[] { r["id"] + ":Pad", r["id"] + ":Bass" }));
            double pad = Lufs(probes[0]!["measure"]!), bass = Lufs(probes[1]!["measure"]!), master = Lufs(r["measure"]!);
            Assert.That(pad, Is.LessThan(master + 0.5), "a part is no louder than the whole");
            Assert.That(bass, Is.LessThan(master + 0.5));
            var probed = Wav.Read(probes[1]!["file"]!.GetValue<string>());
            Assert.That(probed.Frames, Is.EqualTo(Wav.Read(r["file"]!.GetValue<string>()).Frames), "probes line up with the master");
            Ok(rig, "analyze", new JsonObject { ["source"] = probes[0]!["id"]!.GetValue<string>() });

            // Presets.
            var presets = Ok(rig, "preset_list", new JsonObject { ["machine"] = "Pad", ["search"] = "pad" })["presets"]!.AsArray();
            Assert.That(presets.Count, Is.GreaterThan(0), "Neo Poly's factory presets are read beside its DLL");
            string preset = presets[0]!["name"]!.GetValue<string>();
            Ok(rig, "snapshot_save", new JsonObject { ["name"] = "before", ["kind"] = "parameters" });
            var loaded = Ok(rig, "preset_load", new JsonObject { ["machine"] = "Pad", ["preset"] = preset });
            Assert.That(loaded["changed"]!.GetValue<int>(), Is.GreaterThan(0));
            var diff = Ok(rig, "snapshot_diff", new JsonObject { ["a"] = "before", ["kind"] = "parameters" })["machines"]!.AsArray();
            Assert.That(diff.Single()!["machine"]!.GetValue<string>(), Is.EqualTo("Pad"), "only the pad changed");

            // A/B: change the mix, then go back with one call (one undo step).
            Ok(rig, "connection_set", new JsonObject { ["changes"] = new JsonArray(new JsonObject { ["from"] = "Bass", ["to"] = "Master", ["gain_db"] = -20 }) });
            Ok(rig, "machine_set", new JsonObject { ["machine"] = "Pad", ["mute"] = true });
            var restored = Ok(rig, "snapshot_restore", new JsonObject { ["name"] = "before", ["kind"] = "parameters" });
            Assert.That(restored["changed"]!.GetValue<int>(), Is.GreaterThan(2));
            Assert.That(Ok(rig, "snapshot_diff", new JsonObject { ["a"] = "before", ["kind"] = "parameters" })["machines"]!.AsArray().Count, Is.EqualTo(0), "back exactly");
            Ok(rig, "undo");
            var undone = Ok(rig, "snapshot_diff", new JsonObject { ["a"] = "before", ["kind"] = "parameters" })["machines"]!.AsArray();
            Assert.That(undone.Count, Is.EqualTo(2), "undo takes the whole restore back");
            var bad = rig.Call("snapshot_restore", new JsonObject { ["name"] = "befor", ["kind"] = "parameters" });
            Assert.That(bad.Error, Is.True);
            Assert.That(bad.Text, Does.Contain("before"));
        }

        [Test]
        public void MixAnalysisComparisonAndPlots()
        {
            using var rig = new Rig(NeoMachineTests.Poly);
            Ok(rig, "song_set", new JsonObject { ["tempo_bpm"] = 120 });
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Bass" });
            Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Sub" });
            foreach (var m in new[] { "Bass", "Sub" })
            {
                Ok(rig, "pattern_create", new JsonObject { ["machine"] = m, ["name"] = "P", ["length_beats"] = 8 });
                Ok(rig, "notes_write", new JsonObject { ["machine"] = m, ["pattern"] = "P", ["notes"] = Chord(m == "Bass" ? "A1" : "A1") });
                Ok(rig, "arrangement_place", new JsonObject { ["machine"] = m, ["pattern"] = "P", ["at"] = 0 });
            }
            var mix = Ok(rig, "analyze_mix");
            Assert.That(mix["tracks"]!.AsArray().Count, Is.EqualTo(2));
            Assert.That(mix["masking"]!.AsArray().Count, Is.GreaterThan(0), "two basses on one note mask each other");
            string id = mix["render"]!.GetValue<string>();
            var full = Ok(rig, "analyze", new JsonObject { ["source"] = id, ["metrics"] = new JsonArray("spectrum", "stereo", "key") });
            Assert.That(full["spectrum"]!["bands_db"], Is.Not.Null);
            Assert.That(full["stereo"]!["correlation"], Is.Not.Null);

            Ok(rig, "connection_set", new JsonObject { ["changes"] = new JsonArray(new JsonObject { ["from"] = "Sub", ["to"] = "Master", ["gain_db"] = -12 }) });
            var after = Ok(rig, "render", new JsonObject { ["tail_seconds"] = 1 });
            var cmp = Ok(rig, "compare", new JsonObject { ["a"] = id, ["b"] = after["id"]!.GetValue<string>() });
            Assert.That(cmp["reads"]!.AsArray().Count, Is.GreaterThan(0), "turning a track down shows up");

            var plot = rig.Call("plot", new JsonObject { ["kind"] = "spectrum", ["sources"] = new JsonArray(id + ":Bass", id + ":Sub") });
            Assert.That(plot.Error, Is.False, plot.Text);
            var png = File.ReadAllBytes(plot.Data["file"]!.GetValue<string>());
            Assert.That(png.Take(8), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "a PNG");
        }

        [Test]
        public void WavesImportIntoSlotsAndUndo()
        {
            using var rig = new Rig();
            string file = Path.Combine(Path.GetTempPath(), "ReBuzzAgentTests", Guid.NewGuid().ToString("N"), "tone.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            int frames = 22050;
            var x = new float[frames * 2];
            for (int i = 0; i < frames; i++) x[2 * i] = x[2 * i + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * 220 * i / 44100.0));
            Wav.WriteStereoFloat(file, x, frames, 44100);

            var first = Ok(rig, "wave_import", new JsonObject { ["path"] = file, ["name"] = "Tone", ["root_note"] = "A3" });
            Assert.That(first["wave"]!["slot"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(first["wave"]!["seconds"]!.GetValue<double>(), Is.EqualTo(0.5).Within(0.001));
            Assert.That(first["wave"]!["root"]!["name"]!.GetValue<string>(), Is.EqualTo("A3"));
            var second = Ok(rig, "wave_import", new JsonObject { ["path"] = file, ["name"] = "Tone 2" });
            Assert.That(second["wave"]!["slot"]!.GetValue<int>(), Is.EqualTo(2), "the next empty slot");
            var refused = rig.Call("wave_import", new JsonObject { ["path"] = file, ["slot"] = 1 });
            Assert.That(refused.Error, Is.True);
            Assert.That(refused.Text, Does.Contain("replace=true"));
            Ok(rig, "wave_import", new JsonObject { ["path"] = file, ["slot"] = 1, ["name"] = "New", ["replace"] = true });
            Ok(rig, "undo");
            var list = Ok(rig, "wave_list")["waves"]!.AsArray();
            Assert.That(list.Select(w => w!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "Tone", "Tone 2" }), "undo put the replaced wave back");
            Ok(rig, "undo");
            Assert.That(Ok(rig, "wave_list")["waves"]!.AsArray().Count, Is.EqualTo(1), "undo of an import empties the slot");
        }
    }
}
