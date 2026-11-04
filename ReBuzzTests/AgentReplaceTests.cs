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

        /// <summary>
        /// The first native machine of an agent process can render silent (or its host crash) the first time it plays; a throwaway
        /// one plays first so the measured ones are each machine's own sound.
        /// </summary>
        static void WarmUp(Client client, string effect)
        {
            Quiet(client, "song_new", Args(("discard_unsaved", true)));
            Quiet(client, "machine_add", Args(("gear", "Neo Poly"), ("name", "Warm")));
            Quiet(client, "machine_add", Args(("gear", effect), ("name", "Up"), ("insert_after", "Warm")));
            Quiet(client, "pattern_create", Args(("machine", "Warm"), ("name", "00"), ("length_beats", 4)));
            Quiet(client, "notes_write", Args(("machine", "Warm"), ("pattern", "00"), ("notes", new JsonArray(new JsonObject { ["pitch"] = 60, ["start"] = 0, ["length"] = 1 }))));
            Quiet(client, "arrangement_place", Args(("machine", "Warm"), ("pattern", "00"), ("at", 0)));
            Quiet(client, "render", Args(("tail_seconds", 0.5)));
            Quiet(client, "render", Args(("tail_seconds", 0.5)));
        }

        static bool Has(string bin, string kind, string gear) => File.Exists(Path.Combine(bin, "Gear", kind, gear + ".dll"));

        /// <summary>A tool call that doesn't echo its text into the test's output (long arrangements would crowd out the [notice] lines).</summary>
        static JsonObject Quiet(Client client, string name, JsonObject args = null)
        {
            var r = client.Request("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args ?? new JsonObject() });
            Assert.That(r["result"]?["isError"]?.GetValue<bool>(), Is.False, $"{name}: {r["result"]?["content"]?[0]?["text"] ?? r["error"]?.ToJsonString()}");
            return r["result"]!["structuredContent"]!.AsObject();
        }

        /// <summary>
        /// Third-octave levels of the effect's output over its input (dB), from one render with probes on both:
        /// "hz:db" pairs for bands within 40 dB of the input's loudest.
        /// </summary>
        static string Transfer(Client client, string effect = "FX", string source = "Synth")
        {
            var render = Quiet(client, "render", Args(("tail_seconds", 3), ("probes", new JsonArray(effect, source))));
            string id = render["id"]!.GetValue<string>();
            JsonArray Bands(string probe) => Quiet(client, "analyze", Args(("source", $"{id}:{probe}"), ("metrics", new JsonArray("spectrum"))))["spectrum"]!["third_octave"]!["db"]!.AsArray();
            var output = Bands(effect);
            var input = Bands(source);
            double Num(JsonNode n) => n is JsonValue v && v.TryGetValue(out double d) ? d : double.NegativeInfinity;
            double maxIn = input.Select(Num).Where(double.IsFinite).DefaultIfEmpty(0).Max();
            double[] hz = { 20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000 };
            var parts = new List<string>();
            for (int i = 0; i < Math.Min(hz.Length, output.Count); i++)
            {
                double o = Num(output[i]), n = Num(input[i]);
                if (double.IsFinite(o) && double.IsFinite(n) && n > maxIn - 40) parts.Add($"{hz[i]}:{o - n:0.0}");
            }
            return string.Join(" ", parts);
        }

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
            TestContext.Out.WriteLine($"[notice] {label}: within {c?["within_tolerance"]?.ToJsonString() ?? "-"}, " +
                                      $"loudness {m?["loudness_db"]?["difference"]?.ToJsonString() ?? "-"} dB, worst band {m?["worst_band"]?["difference_db"]?.ToJsonString() ?? "-"} dB " +
                                      $"at {m?["worst_band"]?["hz"]?.ToJsonString() ?? "-"} Hz");
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
            WarmUp(client, "Jeskola Reverb");
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

            EffectSong(client, "Jeskola Reverb", Changes(("Dry Out", "raw:0"), ("Rev Out", "raw:4096"), ("Rev Time", "raw:25")), noise: true);
            results.Add(("Jeskola Reverb send", Within(Replace(client, "Reverb wet only, 2.5 s"))));
            EffectSong(client, "Jeskola Reverb", Changes(("Dry Out", "raw:0"), ("Rev Out", "raw:4096"), ("Rev Time", "raw:25")));
            Replace(client, "Reverb wet only, 2.5 s, chords");
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
            var report = new List<string>();
            WarmUp(client, "Jeskola Reverb");
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
                    report.Add($"[notice] {gear} {(sine ? "sine" : "default")} before: {Short(pitch)}");
                    var r = Replace(client, $"{gear} {(sine ? "sine" : "default")}", "Bass");
                    Assert.That(r["patterns"]![0]!["notes"]!.GetValue<int>(), Is.EqualTo(line.Length), $"{gear}: every note moves");
                    var after = client.Tool("render", Args(("tail_seconds", 0.5)));
                    pitch = client.Request("tools/call", new JsonObject { ["name"] = "analyze", ["arguments"] = Args(("source", after["id"]!.GetValue<string>()), ("metrics", new JsonArray("pitch", "loudness"))) });
                    report.Add($"[notice] {gear} {(sine ? "sine" : "default")} after: {Short(pitch)}");
                    report.Add($"[notice] {gear} {(sine ? "sine" : "default")}: within {Within(r)?.ToString() ?? "-"}, loudness {(r["comparison"]?["machine"] ?? r["comparison"]?["song"])?["loudness_db"]?["difference"]?.ToJsonString()} dB");
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
                var arrangementBefore = Quiet(client, "arrangement_get");
                var r = Replace(client, Path.GetFileName(song), machine);
                report.Add($"[notice] {Path.GetFileName(song)}: within {Within(r)?.ToString() ?? "-"}, patterns {r["patterns"]?.ToJsonString()}, sequences {r["sequences"]}");
                var arrangementAfter = Quiet(client, "arrangement_get");
                Assert.That(Placements(arrangementAfter, machine), Is.EqualTo(Placements(arrangementBefore, machine)), $"{song}: the arrangement keeps its patterns");
                client.Tool("undo");
            }
            foreach (var line in report) TestContext.Out.WriteLine(line);
            Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
        }

        /// <summary>
        /// Measurements behind the native mappings (not assertions): each old effect's and its successor's response on noise
        /// (output over input per third octave), at settings that isolate one thing, and Jeskola Bass 3 and Neo Poly at a few
        /// cutoffs. The numbers go into the mappings and docs/specs/mcp-server.md §12.10.
        /// </summary>
        [Test, Explicit("measurements for the mappings; run by name (test_filter) when a mapping is recalibrated")]
        public void NativeSuccessorCalibration()
        {
            // The responses on noise behind the reverb and EQ-3 mappings were measured with Transfer (the history of this file);
            // here: the Jeskola Basses' cutoff against Neo Poly's, as the spectral centroid of a held A2.
            using var client = StartAgent(out string bin);
            if (!Has(bin, "Generators", "Jeskola Bass") || !Has(bin, "Generators", "Jeskola Bass 3"))
                Assert.Inconclusive("the Jeskola Basses are not in this build's Gear");
            var report = new List<string>();
            WarmUp(client, "Jeskola Reverb");
            string Centroid(string gear, string param, string value, bool track)
            {
                Quiet(client, "song_new", Args(("discard_unsaved", true)));
                Quiet(client, "machine_add", Args(("gear", gear), ("name", "B")));
                var change = new JsonObject { ["param"] = param, ["value"] = value };
                if (track) change["track"] = 0;
                Quiet(client, "param_set", Args(("machine", "B"), ("changes", new JsonArray(change))));
                if (gear == "Neo Poly")
                    Quiet(client, "param_set", Args(("machine", "B"), ("changes", Changes(("O2 level", "0%"), ("F1 env", "0%"), ("F1 key track", "0%"), ("F1 velocity", "0%"), ("Amp sustain", "100%")))));
                Quiet(client, "pattern_create", Args(("machine", "B"), ("name", "00"), ("length_beats", 4)));
                Quiet(client, "notes_write", Args(("machine", "B"), ("pattern", "00"), ("notes", new JsonArray(new JsonObject { ["pitch"] = 45, ["start"] = 0, ["length"] = 3.5 }))));
                Quiet(client, "arrangement_place", Args(("machine", "B"), ("pattern", "00"), ("at", 0)));
                var render = Quiet(client, "render", Args(("tail_seconds", 0.2)));
                var a = Quiet(client, "analyze", Args(("source", render["id"]!.GetValue<string>()), ("metrics", new JsonArray("spectrum", "loudness"))));
                return $"{a["spectrum"]?["centroid_hz"]}/{a["loudness"]?["integrated_lufs"]}";
            }
            var bass1 = new[] { "0", "10", "32", "64", "96", "128" }.Select(c => $"{c}:{Centroid("Jeskola Bass", "Cutoff", "raw:" + c, false)}");
            report.Add("[notice] calibration Jeskola Bass cutoff raw → centroid Hz/LUFS: " + string.Join(" ", bass1));
            var bass3 = new[] { "0", "32", "64", "96", "128" }.Select(c => $"{c}:{Centroid("Jeskola Bass 3", "Cutoff", "raw:" + c, true)}");
            report.Add("[notice] calibration Jeskola Bass 3 cutoff raw → centroid Hz/LUFS: " + string.Join(" ", bass3));
            var poly = new[] { "100 Hz", "200 Hz", "400 Hz", "800 Hz", "1600 Hz", "3200 Hz", "8000 Hz", "16000 Hz" }.Select(c => $"{c.Replace(" Hz", "")}:{Centroid("Neo Poly", "F1 cutoff", c, false)}");
            report.Add("[notice] calibration Neo Poly saw F1 cutoff → centroid Hz/LUFS: " + string.Join(" ", poly));
            foreach (string line in report) TestContext.Out.WriteLine(line);
            Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
        }

        /// <summary>A new song: Neo Poly's noise held for 32 beats into <paramref name="gear"/> ("FX") → Master.</summary>
        static void HeldNoise(Client client, string gear, JsonArray changes)
        {
            Quiet(client, "song_new", Args(("discard_unsaved", true)));
            Quiet(client, "machine_add", Args(("gear", "Neo Poly"), ("name", "Synth")));
            Quiet(client, "param_set", Args(("machine", "Synth"), ("changes", Changes(("Noise", "100%"), ("O1 level", "0%"), ("F1 cutoff", "20 kHz"), ("F1 env", "0%"), ("Amp sustain", "100%"), ("Volume", "0 dB")))));
            Quiet(client, "machine_add", Args(("gear", gear), ("name", "FX"), ("insert_after", "Synth")));
            if (changes != null) Quiet(client, "param_set", Args(("machine", "FX"), ("changes", changes)));
            Quiet(client, "pattern_create", Args(("machine", "Synth"), ("name", "00"), ("length_beats", 32)));
            Quiet(client, "notes_write", Args(("machine", "Synth"), ("pattern", "00"), ("notes", new JsonArray(new JsonObject { ["pitch"] = 48, ["start"] = 0, ["length"] = 31.5 }))));
            Quiet(client, "arrangement_place", Args(("machine", "Synth"), ("pattern", "00"), ("at", 0)));
        }

        static JsonArray Raw(params (string Param, int Raw)[] changes) =>
            new(changes.Select(c => (JsonNode)new JsonObject { ["param"] = c.Param, ["value"] = $"raw:{c.Raw}" }).ToArray());

        static JsonArray OnTrack(JsonArray changes)
        {
            foreach (var c in changes) c!["track"] = 0;
            return changes;
        }

        /// <summary>
        /// The measurements behind the chorus and flanger mappings (Neo Ensemble's): each closed machine's sweep, read from its
        /// input and output (DelaySweep), its levels, and machine_replace's comparison of each pair at a few settings.
        /// </summary>
        [Test, Explicit("measurements for the mappings; run by name (test_filter) when a mapping is recalibrated")]
        public void ChorusAndFlangerCalibration()
        {
            using var client = StartAgent(out string bin);
            var report = new List<string>();
            WarmUp(client, "Jeskola Reverb");
            void Sweep(string label, string gear, JsonArray changes, int minLag, int maxLag)
            {
                HeldNoise(client, gear, changes);
                var render = Quiet(client, "render", Args(("tail_seconds", 0), ("probes", new JsonArray("FX", "Synth"))));
                var probes = render["probes"]!.AsArray();
                string ProbeFile(string m) => probes.First(p => p!["machine"]!.GetValue<string>() == m)!["file"]!.GetValue<string>();
                var fx = ReBuzz.Agent.Audio.Wav.Read(ProbeFile("FX"));
                var src = ReBuzz.Agent.Audio.Wav.Read(ProbeFile("Synth"));
                var mono = src.Left.Zip(src.Right ?? src.Left, (l, r) => (l + r) / 2).ToArray();
                var left = DelaySweep.Measure(mono, fx.Left, fx.Rate, minLag, maxLag);
                double lr = DelaySweep.Correlation(fx.Left, fx.Right ?? fx.Left);
                double level = DelaySweep.GainDb(mono, fx.Left, fx.Rate / 2);
                report.Add($"[notice] sweep {label}: {left}; L/R {lr:0.00}; level {level:+0.0;-0.0} dB");
            }
            int Lag(double ms) => (int)(ms * 44.1);

            Sweep("Neo Ensemble flanger 2 ms ±1 oct (1–4 ms)", "Neo Ensemble", Changes(("Mode", "Flanger"), ("Rate", "0.25 Hz"), ("Voices", "1"), ("Feedback", "0%"), ("Tone", "20 kHz"), ("Spread", "0°"), ("Mix", "100%"), ("Delay", "2 ms"), ("Depth", "50%")), 1, Lag(10));
            if (Has(bin, "Effects", "Jeskola Flanger"))
            {
                (string, int)[] wet = { ("freq", 25), ("feedback", 64), ("dry out", 64), ("wet out", 128) };
                foreach (var (md, depth) in new[] { (0, 127), (0, 64), (25, 32), (50, 127), (50, 0) })
                    Sweep($"Jeskola Flanger md {md} depth {depth}", "Jeskola Flanger", Raw(wet.Concat(new[] { ("min. delay", md), ("depth", depth) }).ToArray()), 1, Lag(30));
                Sweep("Jeskola Flanger dry only", "Jeskola Flanger", Raw(("freq", 25), ("feedback", 64), ("dry out", 128), ("wet out", 64), ("min. delay", 50), ("depth", 0)), 0, Lag(2));
                Sweep("Jeskola Flanger dry 50 %", "Jeskola Flanger", Raw(("freq", 25), ("feedback", 64), ("dry out", 96), ("wet out", 64), ("min. delay", 50), ("depth", 0)), 0, Lag(2));
                Sweep("Jeskola Flanger freq 100, md 25 depth 64", "Jeskola Flanger", Raw(("freq", 100), ("feedback", 64), ("dry out", 64), ("wet out", 128), ("min. delay", 25), ("depth", 64)), 1, Lag(30));
            }
            if (Has(bin, "Effects", "Jeskola Chorus"))
            {
                (string, int)[] wet = { ("freq", 25), ("feedback", 64), ("dry out", 64), ("wet out", 128) };
                foreach (var (md, depth) in new[] { (25, 64), (0, 127), (10, 127), (50, 127), (25, 0) })
                    Sweep($"Jeskola Chorus md {md} depth {depth}", "Jeskola Chorus", Raw(wet.Concat(new[] { ("min. delay", md), ("depth", depth) }).ToArray()), 1, Lag(100));
                Sweep("Jeskola Chorus dry only", "Jeskola Chorus", Raw(("freq", 25), ("feedback", 64), ("dry out", 128), ("wet out", 64), ("min. delay", 25), ("depth", 0)), 0, Lag(2));
            }
            if (Has(bin, "Effects", "FSM Chorus2"))
            {
                Sweep("FSM Chorus2 80/90, 1 tap (8.51–10.39 ms)", "FSM Chorus2", OnTrack(Raw(("Min Delay", 80), ("Mod Depth", 90), ("Mod Rate", 9), ("Fatness", 1), ("Stereo", 0))), Lag(1), Lag(40));
                Sweep("FSM Chorus2 40/240, 1 tap (4.28–9.28 ms)", "FSM Chorus2", OnTrack(Raw(("Min Delay", 40), ("Mod Depth", 240), ("Mod Rate", 9), ("Fatness", 1), ("Stereo", 0))), Lag(1), Lag(40));
            }

            // machine_replace's comparisons: noise and chords through each pair.
            void Compare(string label, string gear, JsonArray changes)
            {
                foreach (bool noise in new[] { true, false })
                {
                    EffectSong(client, gear, changes?.DeepClone().AsArray(), noise);
                    var r = Quiet(client, "machine_replace", Args(("machine", "FX")));
                    var c = r["comparison"];
                    var m = c?["machine"] ?? c?["song"];
                    report.Add($"[notice] compare {label}, {(noise ? "noise" : "chords")}: within {c?["within_tolerance"]?.ToJsonString() ?? "-"}, loudness {m?["loudness_db"]?["difference"]?.ToJsonString() ?? "-"} dB, " +
                               $"bands {m?["bands_db"]?.ToJsonString() ?? "-"}");
                }
            }
            if (Has(bin, "Effects", "Jeskola Flanger"))
            {
                Compare("Jeskola Flanger defaults", "Jeskola Flanger", null);
                Compare("Jeskola Flanger wet +100 %, md 10, depth 64, 0.5 Hz", "Jeskola Flanger", Raw(("wet out", 128), ("min. delay", 10), ("depth", 64), ("freq", 50)));
            }
            if (Has(bin, "Effects", "Jeskola Chorus"))
            {
                Compare("Jeskola Chorus defaults", "Jeskola Chorus", null);
                Compare("Jeskola Chorus wet 50 %, md 10, depth 127", "Jeskola Chorus", Raw(("wet out", 96), ("min. delay", 10), ("depth", 127)));
            }
            if (Has(bin, "Effects", "FSM Chorus2"))
            {
                Compare("FSM Chorus2 defaults", "FSM Chorus2", null);
                Compare("FSM Chorus2 Fatness 1", "FSM Chorus2", OnTrack(Raw(("Fatness", 1))));
                Compare("FSM Chorus2 Fatness 6, Stereo 240", "FSM Chorus2", OnTrack(Raw(("Fatness", 6), ("Stereo", 240))));
            }
            foreach (string line in report) TestContext.Out.WriteLine(line);
            Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
        }

        static string Short(JsonObject analyzeResponse)
        {
            string text = analyzeResponse["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
            return text.Length > 240 ? text[..240] + "…" : text;
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
