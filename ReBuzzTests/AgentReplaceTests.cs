using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzzTests.Automation;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/mcp-server.md §12.5, §12.7 U5): machine_replace on the native machines, which load only
    /// here. U5's exit check: Jeskola Reverb → Neo Verb, Jeskola Distortion → Neo Drive (and the other native pairs)
    /// convert within their stated tolerance on representative material, or say why not; Pedal EQ → Neo EQ and Pedal
    /// Gain → Neo Utility are checked on Linux (tools/headless-replace.py) and, in a full build, here too. The measurements
    /// go into the test log as "[notice]" lines (annotations: tools/ci-test-summary.ps1).
    /// </summary>
    public partial class AgentHeadlessTests
    {
        static Client StartAgent(out string bin)
        {
            bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
            string exe = Path.Combine(bin, "ReBuzz.exe");
            if (!File.Exists(exe)) Assert.Inconclusive($"ReBuzz.exe not built ({exe})");
            var client = new Client(exe, "--agent");
            client.Request("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "ci", ["version"] = "1" },
            }, seconds: 180);
            client.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            return client;
        }

        static bool Has(string bin, string kind, string gear) => File.Exists(Path.Combine(bin, "Gear", kind, gear + ".dll"));

        static JsonObject Args(params (string Key, JsonNode Value)[] pairs)
        {
            var o = new JsonObject();
            foreach (var (k, v) in pairs) o[k] = v;
            return o;
        }

        static JsonArray Changes(params (string Param, string Value)[] changes) =>
            new(changes.Select(c => (JsonNode)new JsonObject { ["param"] = c.Param, ["value"] = c.Value }).ToArray());

        /// <summary>A new song: Neo Poly playing chords and a bass line (8 beats, twice) into <paramref name="effect"/> → Master.</summary>
        static void EffectSong(Client client, string effect, JsonArray settings, bool noise = false)
        {
            client.Tool("song_new", Args(("discard_unsaved", true)));
            client.Tool("machine_add", Args(("gear", "Neo Poly"), ("name", "Synth")));
            var synth = noise
                ? Changes(("Noise", "100%"), ("O1 level", "0%"), ("F1 cutoff", "20 kHz"), ("F1 env", "0%"), ("Amp sustain", "100%"), ("Volume", "0 dB"))
                : Changes(("Volume", "0 dB"), ("Amp release", "80 ms"));
            client.Tool("param_set", Args(("machine", "Synth"), ("changes", synth)));
            client.Tool("machine_add", Args(("gear", effect), ("name", "FX"), ("insert_after", "Synth")));
            if (settings != null) client.Tool("param_set", Args(("machine", "FX"), ("changes", settings)));
            client.Tool("pattern_create", Args(("machine", "Synth"), ("name", "00"), ("length_beats", 8)));
            var notes = new JsonArray();
            foreach (var (start, pitches) in new[] { (0.0, new[] { 48, 60, 64, 67 }), (2.0, new[] { 53, 60, 65, 69 }), (4.0, new[] { 55, 62, 67, 71 }), (6.0, new[] { 43, 59, 62, 67 }) })
                foreach (int p in pitches)
                    notes.Add(new JsonObject { ["pitch"] = p, ["start"] = start, ["length"] = 1.0, ["velocity"] = 100 });
            client.Tool("notes_write", Args(("machine", "Synth"), ("pattern", "00"), ("notes", notes)));
            client.Tool("arrangement_place", Args(("machine", "Synth"), ("pattern", "00"), ("at", new JsonArray(0, 8))));
        }

        /// <summary>Replaces "FX" (or <paramref name="machine"/>) and reports the comparison as a [notice] line.</summary>
        static JsonObject Replace(Client client, string label, string machine = "FX", JsonObject extra = null)
        {
            var args = Args(("machine", machine));
            if (extra != null) foreach (var kv in extra) args[kv.Key] = kv.Value?.DeepClone();
            var r = client.Tool("machine_replace", args);
            var c = r["comparison"];
            var m = c?["machine"] ?? c?["song"];
            string bands = m?["bands_db"] is JsonObject b ? string.Join(" ", b.Select(kv => $"{kv.Key}:{kv.Value}")) : "";
            TestContext.Out.WriteLine($"[notice] {label}: {r["from"]} → {r["to"]}: within {c?["within_tolerance"]?.ToJsonString() ?? "-"}, " +
                                      $"loudness {m?["loudness_db"]?["difference"]?.ToJsonString() ?? "-"} dB ({m?["loudness_db"]?["by"]}), worst band {m?["worst_band"]?.ToJsonString() ?? "-"}; " +
                                      $"song {c?["song"]?["loudness_db"]?["difference"]?.ToJsonString() ?? "-"} dB; bands {bands}; notes {r["notes"]?.ToJsonString()}");
            return r;
        }

        static bool? Within(JsonObject r) => r["comparison"]?["within_tolerance"] is JsonValue v ? v.GetValue<bool>() : null;

        /// <summary>
        /// U5's exit check on Windows: each native pair, on a synth playing chords into it, converts within the tolerance its
        /// mapping states. Undo brings the original back exactly (its machine list, parameters and connections).
        /// </summary>
        [Test]
        public void NativeMachinesMoveOntoTheirSuccessorsWithinTolerance()
        {
            using var client = StartAgent(out string bin);
            if (!Has(bin, "Effects", "Jeskola Reverb") || !File.Exists(Path.Combine(bin, "Gear", "Effects", "Neo Verb.NET.dll")))
                Assert.Inconclusive("Jeskola Reverb or Neo Verb not in this build's Gear");
            var results = new List<(string Label, bool? Within)>();

            // Jeskola Reverb at its defaults (dry 0 dB, reverb −14.5 dB, 1 s), and as a send (wet only, 2.5 s).
            EffectSong(client, "Jeskola Reverb", null);
            var gear = client.Tool("gear_list", Args(("search", "Jeskola Reverb")))["gear"]!.AsArray();
            Assert.That(gear.First(g => g!["name"]!.GetValue<string>() == "Jeskola Reverb")!["successor"]!.GetValue<string>(), Does.Contain("Neo Verb"));
            var described = client.Tool("machine_describe", Args(("machine", "FX")));
            Assert.That(described["successors"]![0]!["gear"]!.GetValue<string>(), Is.EqualTo("Neo Verb"));
            var dry = client.Tool("machine_replace", Args(("machine", "FX"), ("dry_run", true)));
            Assert.That(dry["dry_run"]!.GetValue<bool>(), Is.True);
            Assert.That(client.Tool("machine_list")["machines"]!.AsArray().Any(m => m!["gear"]!.GetValue<string>() == "Jeskola Reverb"), Is.True, "a dry run changes nothing");
            var listBefore = client.Tool("machine_list")["machines"]!.ToJsonString();
            var paramsBefore = client.Tool("machine_describe", Args(("machine", "FX")))["parameters"]!.ToJsonString();
            var r = Replace(client, "Reverb defaults");
            results.Add(("Jeskola Reverb defaults", Within(r)));
            client.Tool("undo");
            Assert.That(client.Tool("machine_list")["machines"]!.ToJsonString(), Is.EqualTo(listBefore), "undo brings the original back");
            Assert.That(client.Tool("machine_describe", Args(("machine", "FX")))["parameters"]!.ToJsonString(), Is.EqualTo(paramsBefore));

            EffectSong(client, "Jeskola Reverb", Changes(("Dry Out", "raw:0"), ("Rev Out", "raw:4096"), ("Rev Time", "raw:25")));
            results.Add(("Jeskola Reverb send", Within(Replace(client, "Reverb wet only, 2.5 s"))));
            EffectSong(client, "Jeskola Reverb", Changes(("Dry Out", "raw:0"), ("Rev Out", "raw:0"), ("ER Out", "raw:4096")));
            Replace(client, "Reverb early reflections only");

            // Jeskola Distortion at its defaults (25 % threshold, 200 % clamp) and as the Oomek Aggressor demo sets it (all 2305).
            if (Has(bin, "Effects", "Jeskola Distortion"))
            {
                EffectSong(client, "Jeskola Distortion", null);
                results.Add(("Jeskola Distortion defaults", Within(Replace(client, "Distortion defaults"))));
                EffectSong(client, "Jeskola Distortion", Changes(("+threshold", "raw:2305"), ("+clamp", "raw:2305"), ("-threshold", "raw:2305"), ("-clamp", "raw:2305")));
                Replace(client, "Distortion as the Aggressor demo");
            }
            if (Has(bin, "Effects", "Jeskola EQ-3"))
            {
                EffectSong(client, "Jeskola EQ-3", Changes(("Low Gain", "raw:6110"), ("Mid Gain", "raw:7106"), ("High Gain", "raw:0"), ("Low Cutoff", "raw:452"), ("High Cutoff", "raw:8314")), noise: true);
                Replace(client, "EQ-3 as Masterizer_test.bmx sets it");
                EffectSong(client, "Jeskola EQ-3", Changes(("Low Gain", "+6 dB"), ("High Gain", "-6 dB")), noise: true);
                Replace(client, "EQ-3 low +6, high −6");
            }
            if (Has(bin, "Effects", "Jeskola Gain.NET"))
            {
                EffectSong(client, "Jeskola Gain", Changes(("Gain", "-6 dB")), noise: true);
                Replace(client, "Gain −6 dB");
            }
            // In a full build (the Pedal machines are external): the two portable exit-check pairs here too.
            if (File.Exists(Path.Combine(bin, "Gear", "Effects", "Pedal EQ.NET.dll")))
            {
                EffectSong(client, "Pedal EQ", Changes(("LM Gain", "+6 dB"), ("HS Gain", "-4 dB"), ("HM Q", "3"), ("HM Gain", "-9 dB")), noise: true);
                results.Add(("Pedal EQ", Within(Replace(client, "Pedal EQ"))));
                EffectSong(client, "Pedal Gain", Changes(("Gain", "50%")), noise: true);
                results.Add(("Pedal Gain", Within(Replace(client, "Pedal Gain"))));
            }
            Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
            Assert.That(results.Where(x => x.Within != true).Select(x => $"{x.Label}: {x.Within?.ToString() ?? "not compared"}"), Is.Empty,
                "the exit-check pairs convert within their stated tolerance");
        }

        /// <summary>
        /// The Jeskola Bass family onto Neo Poly (machine-safety.md §4, H4): tracker notes become Neo Poly's notes, the level is
        /// matched; and the repo's songs that use these machines move over (their patterns and arrangement with them).
        /// </summary>
        [Test]
        public void JeskolaBassesAndOldSongsMoveOntoTheirSuccessors()
        {
            using var client = StartAgent(out string bin);
            if (!Has(bin, "Generators", "Jeskola Bass 3") || !File.Exists(Path.Combine(bin, "Gear", "Generators", "Neo Poly.NET.dll")))
                Assert.Inconclusive("Jeskola Bass 3 or Neo Poly not in this build's Gear");
            foreach (string gear in new[] { "Jeskola Bass 3", "Jeskola Bass 2", "Jeskola Bass" })
            {
                if (!Has(bin, "Generators", gear)) continue;
                foreach (bool sine in new[] { true, false })
                {
                    client.Tool("song_new", Args(("discard_unsaved", true)));
                    client.Tool("machine_add", Args(("gear", gear), ("name", "Bass")));
                    if (sine && gear != "Jeskola Bass") client.Tool("param_set", Args(("machine", "Bass"), ("changes", Changes(("Waveform", "sine")))));
                    if (sine && gear == "Jeskola Bass") continue;
                    client.Tool("pattern_create", Args(("machine", "Bass"), ("name", "00"), ("length_beats", 8)));
                    var notes = new JsonArray();
                    int[] line = { 45, 45, 57, 45, 48, 50, 45, 52 };
                    for (int i = 0; i < line.Length; i++) notes.Add(new JsonObject { ["pitch"] = line[i], ["start"] = i, ["length"] = 0.75 });
                    client.Tool("notes_write", Args(("machine", "Bass"), ("pattern", "00"), ("notes", notes)));
                    client.Tool("arrangement_place", Args(("machine", "Bass"), ("pattern", "00"), ("at", 0)));
                    var before = client.Tool("render", Args(("tail_seconds", 0.5)));
                    var pitch = client.Request("tools/call", new JsonObject { ["name"] = "analyze", ["arguments"] = Args(("source", before["id"]!.GetValue<string>()), ("metrics", new JsonArray("pitch", "loudness"))) });
                    TestContext.Out.WriteLine($"[notice] {gear} {(sine ? "sine" : "default")} before: {pitch["result"]?["content"]?[0]?["text"]}");
                    var r = Replace(client, $"{gear} {(sine ? "sine" : "default")}", "Bass");
                    Assert.That(r["patterns"]![0]!["notes"]!.GetValue<int>(), Is.EqualTo(line.Length), $"{gear}: every note moves");
                    var after = client.Tool("render", Args(("tail_seconds", 0.5)));
                    pitch = client.Request("tools/call", new JsonObject { ["name"] = "analyze", ["arguments"] = Args(("source", after["id"]!.GetValue<string>()), ("metrics", new JsonArray("pitch", "loudness"))) });
                    TestContext.Out.WriteLine($"[notice] {gear} {(sine ? "sine" : "default")} after: {pitch["result"]?["content"]?[0]?["text"]}");
                    var read = client.Tool("notes_read", Args(("machine", "Bass"), ("pattern", "00")));
                    TestContext.Out.WriteLine($"[notice] {gear}: notes after: {read["notes"]?.ToJsonString()}");
                }
            }

            // The repo's songs (ExternalMachines/source/buzztrax-buzzmachines): their Jeskola machines move over.
            string root = Repo();
            foreach (var (song, machine) in new[]
                     {
                         ("Oomek/Masterizer/Masterizer_test.bmx", "Reverb"),
                         ("Oomek/Aggressor/Oomek_Aggressor_demo.bmx", "Dist"),
                         ("Elak/SVF/Elak_SVF.bmx", "Bass"),
                         ("CyanPhase/DTMF/CyanPhase_DTMF-1_Demo.bmx", "Bass 3"),
                     })
            {
                string path = Path.Combine(root, "ExternalMachines", "source", "buzztrax-buzzmachines", song.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;
                client.Tool("song_open", Args(("path", path), ("discard_unsaved", true)));
                var list = client.Tool("machine_list")["machines"]!.AsArray();
                if (!list.Any(m => m!["name"]!.GetValue<string>() == machine)) { TestContext.Out.WriteLine($"[notice] {song}: no machine {machine}"); continue; }
                var arrangementBefore = client.Tool("arrangement_get");
                var r = Replace(client, Path.GetFileName(song), machine);
                TestContext.Out.WriteLine($"[notice] {Path.GetFileName(song)}: patterns {r["patterns"]?.ToJsonString()}, sequences {r["sequences"]}");
                var arrangementAfter = client.Tool("arrangement_get");
                Assert.That(Placements(arrangementAfter, machine), Is.EqualTo(Placements(arrangementBefore, machine)), $"{song}: the arrangement keeps its patterns");
                client.Tool("undo");
            }
            Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
        }

        static string Placements(JsonObject arrangement, string machine) =>
            string.Join(";", arrangement["lanes"]!.AsArray().Where(t => t!["machine"]?.GetValue<string>() == machine).Select(t => t!["events"]!.ToJsonString()));

        static string Repo()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "Machines", "Manifests", "Successors"))) return dir.FullName;
            return Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? "";
        }
    }
}
