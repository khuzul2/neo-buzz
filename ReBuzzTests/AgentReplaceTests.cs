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
            string exe = Path.Combine(bin, ReBuzz.Product.WindowsExe);
            if (!File.Exists(exe)) Assert.Inconclusive($"{ReBuzz.Product.WindowsExe} not built ({exe})");
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

        static bool Has(string bin, string kind, string gear) =>
            File.Exists(Path.Combine(bin, "Gear", kind, gear + ".dll")) || File.Exists(Path.Combine(bin, "Gear", kind, gear + ".x64.dll"));

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
            // Neo Ensemble's pairs (machine-safety.md §4), on noise. Jeskola Flanger's default wet out (−100 %) has no counterpart:
            // the result says so rather than faking it.
            if (File.Exists(Path.Combine(bin, "Gear", "Effects", "Neo Ensemble.NET.dll")))
            {
                if (Has(bin, "Effects", "Jeskola Flanger"))
                {
                    EffectSong(client, "Jeskola Flanger", Raw(("wet out", 128), ("min. delay", 10), ("depth", 64), ("freq", 50)), noise: true);
                    results.Add(("Jeskola Flanger, wet +100 %", Within(Replace(client, "Jeskola Flanger, wet +100 %, 1–6 ms"))));
                    EffectSong(client, "Jeskola Flanger", null, noise: true);
                    var inverted = Replace(client, "Jeskola Flanger defaults (wet −100 %)");
                    Assert.That(inverted["notes"]?.ToJsonString() ?? "", Does.Contain("polarity"), "an inverted wet is reported, not faked");
                }
                if (Has(bin, "Effects", "Jeskola Chorus"))
                {
                    EffectSong(client, "Jeskola Chorus", null, noise: true);
                    results.Add(("Jeskola Chorus defaults", Within(Replace(client, "Jeskola Chorus defaults"))));
                }
                if (Has(bin, "Effects", "FSM Chorus2"))
                {
                    EffectSong(client, "FSM Chorus2", null, noise: true);
                    results.Add(("FSM Chorus2 defaults", Within(Replace(client, "FSM Chorus2 defaults"))));
                }
            }
            // In a full build (the Pedal machines are external): the two portable exit-check pairs here too.
            if (File.Exists(Path.Combine(bin, "Gear", "Effects", "Pedal EQ.NET.dll")))
            {
                EffectSong(client, "Pedal EQ", Changes(("LM Gain", "+6 dB"), ("HS Gain", "-4 dB"), ("HM Q", "3"), ("HM Gain", "-9 dB")), noise: true);
                results.Add(("Pedal EQ", Within(Replace(client, "Pedal EQ"))));
                EffectSong(client, "Pedal Gain", Changes(("Gain", "50%")), noise: true);
                results.Add(("Pedal Gain", Within(Replace(client, "Pedal Gain"))));
            }
            // Joachims Hyperion onto Neo EQ's graphic mode (machine-safety.md §4), on noise: its curve read at the ISO centres.
            // By index: #1 Input gain, #2 … #31 the bands from 20 kHz down to 20 Hz, #32 Highpass, #33 Bandwidth; raw 128 is 0 dB.
            if (Has(bin, "Effects", "Joachims Hyperion"))
            {
                var smile = new[] { (31, 149), (30, 149), (29, 149), (28, 149), (27, 149), (18, 114), (17, 114), (16, 114), (15, 114), (6, 149), (5, 149), (4, 149) };
                EffectSong(client, "Joachims Hyperion", Indexed(smile), noise: true);
                results.Add(("Joachims Hyperion, a smile", Within(Replace(client, "Joachims Hyperion, a smile (20–52 Hz and 7.7–12.4 kHz +3 dB, 440–900 Hz −2 dB)"))));
                EffectSong(client, "Joachims Hyperion", Indexed(smile.Append((32, 64)).ToArray()), noise: true);
                results.Add(("Joachims Hyperion, a smile and Highpass", Within(Replace(client, "Joachims Hyperion, the smile and Highpass 22 Hz (raw 64)"))));
                EffectSong(client, "Joachims Hyperion", Indexed(Enumerable.Range(2, 30).Select(i => (i, 149)).Append((1, 60)).ToArray()), noise: true);
                results.Add(("Joachims Hyperion, every band +3 dB", Within(Replace(client, "Joachims Hyperion, every band +3 dB (+21.5 dB summed), Input gain −12 dB"))));
                EffectSong(client, "Joachims Hyperion", Indexed(Enumerable.Range(2, 30).Select(i => (i, i % 2 == 0 ? 170 : 86)).Append((33, 2)).ToArray()), noise: true);
                results.Add(("Joachims Hyperion, a zigzag", Within(Replace(client, "Joachims Hyperion, zigzag ±6 dB at Bandwidth 0.3 (raw 2)"))));
                // Narrower than graphic mode can follow (0.37 octave between half-gain points): reported, not asserted.
                EffectSong(client, "Joachims Hyperion", Indexed((15, 213), (33, 0)), noise: true);
                Replace(client, "Joachims Hyperion, 900 Hz +12 dB at Bandwidth 0.2 (raw 0, the narrowest)");
            }
            // Neo Delay's pairs (mcp-server.md §12.10), on noise: each classic delay at its defaults and at another setting.
            if (File.Exists(Path.Combine(bin, "Gear", "Effects", "Neo Delay.NET.dll")))
            {
                foreach (var (delay, label, changes) in new (string Gear, string Label, JsonArray Changes)[]
                {
                    ("Jeskola Delay", "Jeskola Delay defaults (3 ticks, 50 %, wet 37.5 %)", null),
                    ("Jeskola Delay", "Jeskola Delay 120 ms, −60 %, wet only", Join(Raw(("Dry thru", 0)), OnTrack(Raw(("Length", 120), ("Length unit", 1), ("Feedback", 26), ("Wet out", 128))))),
                    ("Jeskola Cross Delay", "Jeskola Cross Delay defaults", null),
                    ("Jeskola Cross Delay", "Jeskola Cross Delay 2 and 3 ticks, 60 %", OnTrack(Raw(("L. Length", 2), ("R. Length", 3), ("Feedback", 102)))),
                    ("Jeskola NiNjA dElaY", "NiNjA dElaY defaults (high-pass 550 Hz, 75 %)", null),
                    ("Jeskola NiNjA dElaY", "NiNjA dElaY low-pass 2 kHz, 60 %", OnTrack(Raw(("f. type", 0), ("f. cutoff", 2000), ("Feedback", 102)))),
                    ("FSM PanzerDelay", "PanzerDelay defaults", null),
                    ("FSM PanzerDelay", "PanzerDelay ping-pong, 2 and 3 ticks, FB −3 dB", OnTrack(Raw(("FB PingPong", 1), ("L Delay Len", 2), ("R Delay Len", 3), ("FB Amp", 210)))),
                })
                {
                    if (!Has(bin, "Effects", delay)) continue;
                    EffectSong(client, delay, changes, noise: true);
                    results.Add((label, Within(Replace(client, label))));
                }
            }
            // Neo Filter's pairs (mcp-server.md §12.10), on noise: each classic filter at its defaults and in each type. The LFOs
            // run free in both (FSM WahMan's at its defaults, Pedal Filter's synced): where each is in its cycle when the render
            // starts differs, so those are reported, not asserted.
            if (File.Exists(Path.Combine(bin, "Gear", "Effects", "Neo Filter.NET.dll")))
            {
                foreach (var (filter, label, changes, assert) in new (string Gear, string Label, JsonArray Changes, bool Assert)[]
                {
                    ("Jeskola Filter", "Jeskola Filter defaults (low-pass, open)", null, true),
                    ("Jeskola Filter", "Jeskola Filter low-pass 64, resonance 64", Raw(("Cutoff", 64), ("Resonance", 64)), true),
                    ("Jeskola Filter", "Jeskola Filter high-pass 48", Raw(("Type", 1), ("Cutoff", 48)), true),
                    ("Jeskola Filter", "Jeskola Filter band-pass 32, resonance 96", Raw(("Type", 2), ("Cutoff", 32), ("Resonance", 96)), true),
                    ("Jeskola Filter 2", "Jeskola Filter 2 defaults (low-pass, open)", null, true),
                    ("Jeskola Filter 2", "Jeskola Filter 2 low-pass 64, resonance 64", Raw(("Cutoff", 64), ("Resonance", 64)), true),
                    ("Jeskola Filter 2", "Jeskola Filter 2 high-pass 48", Raw(("Type", 1), ("Cutoff", 48)), true),
                    ("Jeskola Filter 2", "Jeskola Filter 2 band-pass 96, resonance 110", Raw(("Type", 2), ("Cutoff", 96), ("Resonance", 110)), true),
                    ("FSM WahMan", "FSM WahMan cutoff 60, resonance 200, LFO still", OnTrack(Raw(("Cutoff", 60), ("Resonance", 200), ("Modulation", 0))), true),
                    ("FSM WahMan", "FSM WahMan cutoff 200, resonance 0, LFO still", OnTrack(Raw(("Cutoff", 200), ("Resonance", 0), ("Modulation", 0))), true),
                    ("FSM WahMan", "FSM WahMan defaults (its LFO at 0.11 Hz, ±0.6 octave)", null, false),
                    ("Pedal Filter", "Pedal Filter defaults (low pass 2 kHz)", null, true),
                    ("Pedal Filter", "Pedal Filter high pass 24 dB 500 Hz, resonance 60 %", Raw(("Mode", 1), ("Slope", 1), ("Cutoff", 500), ("Resonance", 60)), true),
                    ("Pedal Filter", "Pedal Filter band pass 1 kHz, resonance 80 %, its LFO on 1/4", Raw(("Mode", 2), ("Cutoff", 1000), ("Resonance", 80), ("Tempo Sync", 1), ("LFO Div", 4), ("LFO Depth", 25)), false),
                })
                {
                    if (!Has(bin, "Effects", filter) && !Has(bin, "Effects", filter + ".NET")) continue;
                    EffectSong(client, filter, changes, noise: true);
                    var within = Within(Replace(client, label));
                    if (assert) results.Add((label, within));
                }
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
                    report.Add($"{gear} {(sine ? "sine" : "default")} before: {Short(pitch)}");
                    var r = Replace(client, $"{gear} {(sine ? "sine" : "default")}", "Bass");
                    Assert.That(r["patterns"]![0]!["notes"]!.GetValue<int>(), Is.EqualTo(line.Length), $"{gear}: every note moves");
                    var after = client.Tool("render", Args(("tail_seconds", 0.5)));
                    pitch = client.Request("tools/call", new JsonObject { ["name"] = "analyze", ["arguments"] = Args(("source", after["id"]!.GetValue<string>()), ("metrics", new JsonArray("pitch", "loudness"))) });
                    report.Add($"{gear} {(sine ? "sine" : "default")} after: {Short(pitch)}");
                    report.Add($"{gear} {(sine ? "sine" : "default")}: within {Within(r)?.ToString() ?? "-"}, loudness {(r["comparison"]?["machine"] ?? r["comparison"]?["song"])?["loudness_db"]?["difference"]?.ToJsonString()} dB");
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
                report.Add($"{Path.GetFileName(song)}: within {Within(r)?.ToString() ?? "-"}, patterns {r["patterns"]?.ToJsonString()}, sequences {r["sequences"]}");
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

        /// <summary>Raw values by parameter index ("#15"), for machines whose names are padded (Joachims Hyperion's "  900 Hz").</summary>
        static JsonArray Indexed(params (int Index, int Raw)[] changes) =>
            new(changes.Select(c => (JsonNode)new JsonObject { ["param"] = $"#{c.Index}", ["value"] = $"raw:{c.Raw}" }).ToArray());

        /// <summary>Several change lists as one (global and track changes together).</summary>
        static JsonArray Join(params JsonArray[] parts) => new(parts.SelectMany(p => p).Select(n => n!.DeepClone()).ToArray());

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
                report.Add($"[notice] {label}: {left}; L/R {lr:0.00}; level {level:+0.0;-0.0} dB");
            }
            int Lag(double ms) => (int)(ms * 44.1);

            // Measured in package run 41762016880 (their numbers are in the mappings' rules): Neo Ensemble's own sweep as the
            // method's check, Jeskola Flanger and Chorus at min. delay 0, 2.5 and 5 ms (0, 10, 25 and 50 ms) and depth 0, 32, 64
            // and 127, their dry-only and dry-50 % levels and freq 0.25 and 1 Hz. Here: FSM Chorus2's against its source.
            if (Has(bin, "Effects", "FSM Chorus2"))
            {
                Sweep("FSM Chorus2 80/90, 1 tap (8.51–10.39 ms)", "FSM Chorus2", OnTrack(Raw(("Min Delay", 80), ("Mod Depth", 90), ("Mod Rate", 9), ("Fatness", 1), ("Stereo", 0))), Lag(1), Lag(40));
                Sweep("FSM Chorus2 40/240, 1 tap (4.28–9.28 ms)", "FSM Chorus2", OnTrack(Raw(("Min Delay", 40), ("Mod Depth", 240), ("Mod Rate", 9), ("Fatness", 1), ("Stereo", 0))), Lag(1), Lag(40));
            }
            if (Has(bin, "Effects", "Jeskola Flanger"))
                Sweep("Jeskola Flanger md 50 depth 0, lags to 100 ms", "Jeskola Flanger", Raw(("freq", 25), ("feedback", 64), ("dry out", 64), ("wet out", 128), ("min. delay", 50), ("depth", 0)), 1, Lag(100));

            // machine_replace's comparisons: noise and chords through each pair.
            void Compare(string label, string gear, JsonArray changes)
            {
                foreach (bool noise in new[] { true, false })
                {
                    EffectSong(client, gear, changes?.DeepClone().AsArray(), noise);
                    var r = Quiet(client, "machine_replace", Args(("machine", "FX")));
                    var c = r["comparison"];
                    var m = c?["machine"] ?? c?["song"];
                    report.Add($"[notice] {label}, {(noise ? "noise" : "chords")}: {c?["within_tolerance"]?.ToJsonString() ?? "-"}, {m?["loudness_db"]?["difference"]?.ToJsonString() ?? "-"} dB, " +
                               $"worst {m?["worst_band"]?["difference_db"]?.ToJsonString() ?? "-"} dB at {m?["worst_band"]?["hz"]?.ToJsonString() ?? "-"} Hz");
                }
            }
            if (Has(bin, "Effects", "Jeskola Flanger"))
            {
                Compare("JF defaults", "Jeskola Flanger", null);
                Compare("JF wet +100, md 1 ms, d 64", "Jeskola Flanger", Raw(("wet out", 128), ("min. delay", 10), ("depth", 64), ("freq", 50)));
                Compare("JF wet +100, md 2.5 ms, d 127, fb +50", "Jeskola Flanger", Raw(("wet out", 128), ("min. delay", 25), ("depth", 127), ("freq", 50), ("feedback", 96)));
                Compare("JF wet +50, md 0, d 32", "Jeskola Flanger", Raw(("wet out", 96), ("min. delay", 0), ("depth", 32), ("freq", 50)));
            }
            if (Has(bin, "Effects", "Jeskola Chorus"))
            {
                Compare("JC defaults", "Jeskola Chorus", null);
                Compare("JC wet 50, md 10, d 127", "Jeskola Chorus", Raw(("wet out", 96), ("min. delay", 10), ("depth", 127)));
                Compare("JC md 5, d 64, 2 Hz", "Jeskola Chorus", Raw(("min. delay", 5), ("depth", 64), ("freq", 400)));
            }
            if (Has(bin, "Effects", "FSM Chorus2"))
            {
                Compare("FSM defaults", "FSM Chorus2", null);
                Compare("FSM Fatness 1", "FSM Chorus2", OnTrack(Raw(("Fatness", 1))));
                Compare("FSM Fatness 6, Stereo 240", "FSM Chorus2", OnTrack(Raw(("Fatness", 6), ("Stereo", 240))));
                Compare("FSM Min 40, Depth 240, Rate 120", "FSM Chorus2", OnTrack(Raw(("Min Delay", 40), ("Mod Depth", 240), ("Mod Rate", 120))));
            }
            foreach (string line in report) TestContext.Out.WriteLine(line);
            Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
        }

        /// <summary>
        /// The measurements behind the Joachims Hyperion mapping (machine-safety.md §4): its response on noise, finer than third
        /// octaves (FineTransfer), at its defaults, one band at a few gains and at each end of Bandwidth, two neighbours, every
        /// band at once, the end bands, Highpass and Input gain. Its parameters by index: #1 Input gain, #2 … #31 the bands from
        /// 20 kHz down to 20 Hz, #32 Highpass, #33 Bandwidth.
        /// </summary>
        [Test, Explicit("measurements for the Hyperion mapping; run by name (test_filter) when it is recalibrated")]
        public void HyperionCalibration()
        {
            using var client = StartAgent(out string bin);
            if (!Has(bin, "Effects", "Joachims Hyperion")) Assert.Inconclusive("Joachims Hyperion not in this build's Gear");
            var report = new List<string>();
            WarmUp(client, "Joachims Hyperion");
            var hz = FineTransfer.Frequencies();
            void Measure(string label, params (int Index, int Raw)[] changes)
            {
                HeldNoise(client, "Joachims Hyperion", changes.Length == 0 ? null : Indexed(changes));
                Quiet(client, "param_set", Args(("machine", "Synth"), ("changes", Changes(("Volume", "-12 dB")))));
                var render = Quiet(client, "render", Args(("tail_seconds", 0), ("probes", new JsonArray("FX", "Synth"))));
                var probes = render["probes"]!.AsArray();
                string ProbeFile(string m) => probes.First(p => p!["machine"]!.GetValue<string>() == m)!["file"]!.GetValue<string>();
                var fx = ReBuzz.Agent.Audio.Wav.Read(ProbeFile("FX"));
                var src = ReBuzz.Agent.Audio.Wav.Read(ProbeFile("Synth"));
                var mono = src.Left.Zip(src.Right ?? src.Left, (l, r) => (l + r) / 2).ToArray();
                var db = FineTransfer.Db(mono, fx.Left, fx.Rate, hz);
                double lr = fx.Right == null ? 0 : FineTransfer.Db(fx.Left, fx.Right, fx.Rate, new[] { 1000.0 })[0];
                report.Add($"[notice] Hyperion {label} (R/L {lr:0.00} dB): {FineTransfer.Text(hz, db)}");
            }
            const int Bw = 33, Hp = 32, Gain = 1, B900 = 15, B1150 = 14, B6100 = 7, B20k = 2, B20 = 31;
            const int Plus12 = 213, Minus12 = 43, Plus6 = 170, Plus18 = 255;
            Measure("defaults");
            for (int bw = 0; bw <= 15; bw++) Measure($"900 Hz +12 dB, Bandwidth raw {bw}", (B900, Plus12), (Bw, bw));
            Measure("1150 Hz +12 dB, Bandwidth raw 8", (B1150, Plus12), (Bw, 8));
            Measure("900 Hz -12 dB, Bandwidth raw 8", (B900, Minus12), (Bw, 8));
            Measure("900 Hz +6 dB, Bandwidth raw 8", (B900, Plus6), (Bw, 8));
            Measure("900 Hz +18 dB, Bandwidth raw 8", (B900, Plus18), (Bw, 8));
            Measure("900 and 1150 Hz +12 dB, Bandwidth raw 8", (B900, Plus12), (B1150, Plus12), (Bw, 8));
            Measure("6100 Hz +12 dB, Bandwidth raw 8", (B6100, Plus12), (Bw, 8));
            Measure("20 kHz +12 dB, Bandwidth raw 8", (B20k, Plus12), (Bw, 8));
            Measure("20 Hz +12 dB, Bandwidth raw 8", (B20, Plus12), (Bw, 8));
            foreach (int bw in new[] { 0, 8, 15 })
                Measure($"every band +6 dB, Bandwidth raw {bw}", Enumerable.Range(2, 30).Select(i => (i, Plus6)).Append((Bw, bw)).ToArray());
            foreach (int bw in new[] { 2, 8 })
                Measure($"zigzag ±6 dB, Bandwidth raw {bw}", Enumerable.Range(2, 30).Select(i => (i, i % 2 == 0 ? Plus6 : 86)).Append((Bw, bw)).ToArray());
            Measure("every band -6 dB, Bandwidth raw 4", Enumerable.Range(2, 30).Select(i => (i, 86)).Append((Bw, 4)).ToArray());
            Measure("Highpass raw 64", (Hp, 64));
            Measure("Highpass raw 127", (Hp, 127));
            Measure("Input gain raw 60", (Gain, 60));
            foreach (string line in report) TestContext.Out.WriteLine(line);
            Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
        }

        /// <summary>
        /// A new song: a 20 ms burst of Neo Poly's white noise at the start (16 beats long) into <paramref name="gear"/> ("FX") →
        /// Master, the connection panned by <paramref name="pan"/>; rendered with probes on both.
        /// </summary>
        static (float[] Source, float[] L, float[] R, int Rate) Burst(Client client, string gear, JsonArray changes, double pan = 0, bool track = false)
        {
            Quiet(client, "song_new", Args(("discard_unsaved", true)));
            Quiet(client, "machine_add", Args(("gear", "Neo Poly"), ("name", "Synth")));
            Quiet(client, "param_set", Args(("machine", "Synth"), ("changes", Changes(("Noise", "100%"), ("O1 level", "0%"), ("F1 cutoff", "20 kHz"), ("F1 env", "0%"),
                ("Amp attack", "0.5 ms"), ("Amp sustain", "100%"), ("Amp release", "0.5 ms"), ("Drift", "0%"), ("Volume", "-6 dB")))));
            Quiet(client, "machine_add", Args(("gear", gear), ("name", "FX"), ("insert_after", "Synth")));
            if (changes != null) Quiet(client, "param_set", Args(("machine", "FX"), ("changes", track ? OnTrack(changes) : changes)));
            if (pan != 0) Quiet(client, "connection_set", Args(("changes", new JsonArray(new JsonObject { ["from"] = "Synth", ["to"] = "FX", ["pan"] = pan }))));
            Quiet(client, "pattern_create", Args(("machine", "Synth"), ("name", "00"), ("length_beats", 16)));
            Quiet(client, "notes_write", Args(("machine", "Synth"), ("pattern", "00"), ("notes", new JsonArray(new JsonObject { ["pitch"] = 60, ["start"] = 0, ["length"] = 0.04 }))));
            Quiet(client, "arrangement_place", Args(("machine", "Synth"), ("pattern", "00"), ("at", 0)));
            var render = Quiet(client, "render", Args(("tail_seconds", 0), ("probes", new JsonArray("FX", "Synth"))));
            var probes = render["probes"]!.AsArray();
            string ProbeFile(string m) => probes.First(p => p!["machine"]!.GetValue<string>() == m)!["file"]!.GetValue<string>();
            var fx = ReBuzz.Agent.Audio.Wav.Read(ProbeFile("FX"));
            var src = ReBuzz.Agent.Audio.Wav.Read(ProbeFile("Synth"));
            var mono = src.Left.Zip(src.Right ?? src.Left, (l, r) => (l + r) / 2).ToArray();
            return (mono, fx.Left, fx.Right ?? fx.Left, fx.Rate);
        }

        /// <summary>
        /// The echoes of a burst: the output's cross-correlation with the source over the lags (each lag's gain, the burst being
        /// white), its peaks above −40 dB in time order: "375.0 ms −8.5 dB" (a minus sign before the dB for an inverted echo).
        /// </summary>
        static string Taps(float[] source, float[] output, int rate, double maxSeconds = 6, int count = 8)
        {
            int first = Array.FindIndex(source, v => Math.Abs(v) > 1e-4f), last = Array.FindLastIndex(source, v => Math.Abs(v) > 1e-4f);
            if (first < 0) return "no burst";
            double energy = 0;
            for (int i = first; i <= last; i++) energy += source[i] * (double)source[i];
            int maxLag = Math.Min((int)(maxSeconds * rate), output.Length - last - 1);
            var xc = new double[maxLag + 1];
            for (int lag = 0; lag <= maxLag; lag++)
            {
                double sum = 0;
                for (int i = first; i <= last; i++) sum += source[i] * (double)output[i + lag];
                xc[lag] = sum / energy;
            }
            var peaks = new List<(int Lag, double Gain)>();
            for (int lag = 1; lag < maxLag; lag++)
            {
                double a = Math.Abs(xc[lag]);
                if (a > 0.01 && a >= Math.Abs(xc[lag - 1]) && a > Math.Abs(xc[lag + 1])) peaks.Add((lag, xc[lag]));
            }
            if (Math.Abs(xc[0]) > 0.01) peaks.Insert(0, (0, xc[0]));
            // The strongest few, in time order (a filtered echo's ripples are weaker than its peak).
            var top = peaks.OrderByDescending(p => Math.Abs(p.Gain)).Take(count).OrderBy(p => p.Lag);
            return string.Join(", ", top.Select(p => $"{p.Lag * 1000.0 / rate:0.0} ms {(p.Gain < 0 ? "inv " : "")}{20 * Math.Log10(Math.Abs(p.Gain)):0.0} dB"));
        }

        /// <summary>
        /// The measurements behind Neo Delay's mappings (mcp-server.md §12.10): each classic delay's echoes (when, on which side,
        /// how loud, from a burst of noise, centred and panned), its filter's response on noise (FineTransfer, the echo alone, a
        /// short line), and machine_replace's comparison of each pair on noise and chords at its defaults and a few settings.
        /// </summary>
        [Test, Explicit("measurements for the delay mappings; run by name (test_filter) when a mapping is recalibrated")]
        public void DelayCalibration()
        {
            using var client = StartAgent(out string bin);
            var report = new List<string>();
            void Report(string line)
            {
                report.Add(line);
                TestContext.Out.WriteLine(line);   // as it comes: a failure later keeps what was measured
            }
            WarmUp(client, "Jeskola Delay");
            var hz = FineTransfer.Frequencies(40, 16000, 3);
            void Echoes(string label, string gear, JsonArray changes, double pan = 0, bool track = false)
            {
                if (!Has(bin, "Effects", gear) && !Has(bin, "Effects", gear + ".NET")) { Report($"[notice] {gear} not in this build's Gear"); return; }
                var (src, l, r, rate) = Burst(client, gear, changes, pan, track);
                Report($"[notice] {label}{(pan != 0 ? $" (pan {pan:+0.0;-0.0})" : "")}: L {Taps(src, l, rate)} | R {Taps(src, r, rate)}");
            }
            void Response(string label, string gear, JsonArray changes, bool track = false)
            {
                if (!Has(bin, "Effects", gear) && !Has(bin, "Effects", gear + ".NET")) return;
                HeldNoise(client, gear, track ? OnTrack(changes) : changes);
                var render = Quiet(client, "render", Args(("tail_seconds", 0), ("probes", new JsonArray("FX", "Synth"))));
                var probes = render["probes"]!.AsArray();
                string ProbeFile(string m) => probes.First(p => p!["machine"]!.GetValue<string>() == m)!["file"]!.GetValue<string>();
                var fx = ReBuzz.Agent.Audio.Wav.Read(ProbeFile("FX"));
                var src = ReBuzz.Agent.Audio.Wav.Read(ProbeFile("Synth"));
                var mono = src.Left.Zip(src.Right ?? src.Left, (a, b) => (a + b) / 2).ToArray();
                Report($"[notice] {label}: {FineTransfer.Text(hz, FineTransfer.Db(mono, fx.Left, fx.Rate, hz))}");
            }
            void Compare(string label, string gear, JsonArray changes, bool track = false)
            {
                if (!Has(bin, "Effects", gear)) return;
                foreach (bool noise in new[] { true, false })
                {
                    EffectSong(client, gear, (track ? OnTrack(changes) : changes)?.DeepClone().AsArray(), noise);
                    var r = Quiet(client, "machine_replace", Args(("machine", "FX")));
                    var c = r["comparison"];
                    var m = c?["machine"] ?? c?["song"];
                    Report($"[notice] {label}, {(noise ? "noise" : "chords")}: {c?["within_tolerance"]?.ToJsonString() ?? "-"}, {m?["loudness_db"]?["difference"]?.ToJsonString() ?? "-"} dB, " +
                               $"worst {m?["worst_band"]?["difference_db"]?.ToJsonString() ?? "-"} dB at {m?["worst_band"]?["hz"]?.ToJsonString() ?? "-"} Hz; notes {r["notes"]?.ToJsonString() ?? "-"}");
                }
            }

            // Neo Delay itself: the method's check (3 ticks at 120 BPM = 375 ms; feedback 50 %, wet only).
            Echoes("Neo Delay 1/8 dotted, 50 %, wet only (the method)", "Neo Delay", Changes(("Mix", "100%"), ("Low cut", "20 Hz"), ("High cut", "20 kHz"), ("Feedback", "50%")));
            Echoes("Neo Delay cross, 1/4 and 1/4 dotted, 50 %, wet only", "Neo Delay", Changes(("Mode", "Cross"), ("Sync", "1/4"), ("Feel", "Straight"), ("Offset", "50%"), ("Mix", "100%"), ("Low cut", "20 Hz"), ("High cut", "20 kHz"), ("Feedback", "50%")), pan: -1);

            // Jeskola Delay: its defaults; one echo, dry off, at 10 ms and at 1000 samples; feedback −50 %.
            Echoes("Jeskola Delay defaults", "Jeskola Delay", null);
            Echoes("Jeskola Delay dry off, wet 100 %, fb 0, 10 ms", "Jeskola Delay", Join(Raw(("Dry thru", 0)), OnTrack(Raw(("Length", 10), ("Length unit", 1), ("Feedback", 64), ("Wet out", 128)))));
            Echoes("Jeskola Delay dry off, wet 100 %, fb −50 %, 2 ticks", "Jeskola Delay", Join(Raw(("Dry thru", 0)), OnTrack(Raw(("Length", 2), ("Feedback", 32), ("Wet out", 128)))));
            // Jeskola Cross Delay: its defaults; 2 and 3 ticks, fb 50 %, wet only, centred and panned left.
            Echoes("Jeskola Cross Delay defaults", "Jeskola Cross Delay", null);
            var cross = Join(Raw(("Dry thru", 0)), OnTrack(Raw(("L. Length", 2), ("R. Length", 3), ("Feedback", 96), ("Wet out", 128))));
            Echoes("Jeskola Cross Delay 2 and 3 ticks, fb 50 %, wet only", "Jeskola Cross Delay", cross);
            // NiNjA dElaY: its defaults (high-pass 550 Hz in the loop); its filter on one echo, 64 samples, each type at 1 kHz.
            Echoes("NiNjA dElaY defaults", "Jeskola NiNjA dElaY", null);
            Echoes("NiNjA dElaY lowpass 20 kHz, fb 50 %, wet only", "Jeskola NiNjA dElaY", Join(Raw(("Dry thru", 0)), OnTrack(Raw(("f. type", 0), ("f. cutoff", 20000), ("Feedback", 96), ("Wet out", 128)))));
            foreach (var (type, name) in new[] { (0, "lowpass"), (1, "highpass"), (2, "bandpass"), (3, "bandreject") })
                Response($"NiNjA dElaY {name} 1 kHz, one echo of 64 samples", "Jeskola NiNjA dElaY",
                    Join(Raw(("Dry thru", 0)), OnTrack(Raw(("Length", 64), ("Length unit", 2), ("Feedback", 64), ("Wet out", 128), ("f. type", type), ("f. cutoff", 1000)))));
            Response("Neo Delay low cut 1 kHz, one echo of 64 samples (the method)", "Neo Delay", Changes(("Sync", "Free"), ("Time", "1.45 ms"), ("Feedback", "0%"), ("Mix", "100%"), ("Low cut", "1 kHz"), ("High cut", "20 kHz")));
            Response("Neo Delay high cut 1 kHz, one echo of 64 samples", "Neo Delay", Changes(("Sync", "Free"), ("Time", "1.45 ms"), ("Feedback", "0%"), ("Mix", "100%"), ("Low cut", "20 Hz"), ("High cut", "1 kHz")));
            // FSM PanzerDelay: its defaults, centred and panned; ping-pong; its filter (LP, HP, BP at 1.88 kHz) on one echo.
            Echoes("PanzerDelay defaults", "FSM PanzerDelay", null);
            Echoes("PanzerDelay defaults", "FSM PanzerDelay", null, pan: -1);
            Echoes("PanzerDelay ping-pong, dry off, 2 and 3 ticks", "FSM PanzerDelay", Join(Raw(("Dry amp", 0)), OnTrack(Raw(("FB PingPong", 1), ("L Delay Len", 2), ("R Delay Len", 3)))), pan: -1);
            foreach (var (type, name) in new[] { (1, "LP"), (2, "HP"), (3, "BP") })
                Response($"PanzerDelay {name} 1876 Hz (raw 120), Q 0.5, one echo of 100 samples", "FSM PanzerDelay",
                    Join(Raw(("Dry amp", 0), ("Delay unit", 2), ("Filter type", type)), OnTrack(Raw(("L Delay Len", 100), ("R Delay Len", 100), ("FB Amp", 0), ("Wet L Pan", 0), ("Wet R Pan", 240)))));

            // machine_replace's comparisons.
            Compare("JD defaults", "Jeskola Delay", null);
            Compare("JD 5 ticks, fb +80 %, wet 75 %", "Jeskola Delay", Raw(("Length", 5), ("Feedback", 115), ("Wet out", 96)), track: true);
            Compare("JD 120 ms, fb −60 %, dry off", "Jeskola Delay", Join(Raw(("Dry thru", 0)), OnTrack(Raw(("Length", 120), ("Length unit", 1), ("Feedback", 26), ("Wet out", 128)))));
            Compare("JCD defaults", "Jeskola Cross Delay", null);
            Compare("JCD 2 and 3 ticks, fb 60 %", "Jeskola Cross Delay", Raw(("L. Length", 2), ("R. Length", 3), ("Feedback", 102)), track: true);
            Compare("NiNjA defaults", "Jeskola NiNjA dElaY", null);
            Compare("NiNjA lowpass 2 kHz, fb 60 %", "Jeskola NiNjA dElaY", Raw(("f. type", 0), ("f. cutoff", 2000), ("Feedback", 102)), track: true);
            Compare("NiNjA highpass 200 Hz, 4 ticks", "Jeskola NiNjA dElaY", Raw(("f. cutoff", 200), ("Length", 4)), track: true);
            Compare("Panzer defaults", "FSM PanzerDelay", null);
            Compare("Panzer ping-pong, 2 and 3 ticks, FB −3 dB", "FSM PanzerDelay", Raw(("FB PingPong", 1), ("L Delay Len", 2), ("R Delay Len", 3), ("FB Amp", 210)), track: true);
            Compare("Panzer LP 1876 Hz", "FSM PanzerDelay", Raw(("Filter type", 1)));
            Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
        }

        /// <summary>
        /// The measurements behind Neo Sampler's successor pairs (mcp-server.md §12.10): Pedal M1, Matilde Tracker2 and Jeskola
        /// Tracker each playing a line (A3 at the wave's root, A4, E3, A2) from a band-limited saw (16-bit) in the song's wavetable, against
        /// Neo Sampler at its defaults: each note's pitch, the line's loudness and brightness; then machine_replace's comparison of
        /// Pedal M1 → Neo Sampler at its defaults, with a key zone and both oscillators, its envelopes and filter, and resonance.
        /// </summary>
        [Test, Explicit("measurements for Neo Sampler's successor pairs; run by name (test_filter) when a mapping is recalibrated")]
        public void SamplerCalibration()
        {
            using var client = StartAgent(out string bin);
            var report = new List<string>();
            void Report(string line)
            {
                report.Add(line);
                TestContext.Out.WriteLine(line);   // as it comes: a failure later keeps what was measured
            }
            WarmUp(client, "Jeskola Delay");
            string Wave(string name, double hz, int harmonics, int step)
            {
                string path = Path.Combine(Path.GetTempPath(), $"neo-sampler-calibration-{name}-{Guid.NewGuid():N}.wav");
                var x = new float[96000];
                for (int i = 0; i < x.Length; i++)
                {
                    double v = 0;
                    for (int k = 1; k < harmonics; k += step) v += Math.Sin(2 * Math.PI * k * hz * i / 48000) / k;
                    x[i] = (float)(0.3 * v);
                }
                // 16-bit PCM, the format every tracker reads.
                using var w = new BinaryWriter(File.Create(path));
                w.Write("RIFF"u8.ToArray()); w.Write(36 + x.Length * 2); w.Write("WAVE"u8.ToArray());
                w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(48000); w.Write(48000 * 2); w.Write((short)2); w.Write((short)16);
                w.Write("data"u8.ToArray()); w.Write(x.Length * 2);
                foreach (var s in x) w.Write((short)Math.Round(s * 32767));
                return path;
            }
            string saw = Wave("saw", 220, 91, 1), square = Wave("square", 440, 45, 2);
            int[] line = { 57, 69, 52, 45 };
            bool Song(string gear, JsonArray changes)
            {
                Quiet(client, "song_new", Args(("discard_unsaved", true)));
                Quiet(client, "wave_import", Args(("path", saw), ("slot", 1), ("name", "Saw A3"), ("root_note", "A3")));
                Quiet(client, "wave_import", Args(("path", square), ("slot", 2), ("name", "Square A4"), ("root_note", "A4")));
                Quiet(client, "machine_add", Args(("gear", gear), ("name", "M")));
                if (changes != null) Quiet(client, "param_set", Args(("machine", "M"), ("changes", changes)));
                Quiet(client, "pattern_create", Args(("machine", "M"), ("name", "00"), ("length_beats", 8)));
                var notes = new JsonArray(line.Select((p, i) => (JsonNode)new JsonObject { ["pitch"] = p, ["start"] = 2 * i, ["length"] = 1, ["velocity"] = 127 }).ToArray());
                var r = client.Request("tools/call", new JsonObject { ["name"] = "notes_write", ["arguments"] = Args(("machine", "M"), ("pattern", "00"), ("notes", notes)) });
                if (r["result"]?["isError"]?.GetValue<bool>() != false)
                {
                    Report($"[notice] Sampler calibration, {gear}: notes_write refused: {r["result"]?["content"]?[0]?["text"] ?? r["error"]?.ToJsonString()}");
                    return false;
                }
                Quiet(client, "arrangement_place", Args(("machine", "M"), ("pattern", "00"), ("at", 0)));
                return true;
            }
            // A tool call that reports, rather than asserts, what goes wrong (a tracker that plays nothing).
            JsonObject Try(string name, JsonObject args)
            {
                var r = client.Request("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args });
                if (r["result"]?["isError"]?.GetValue<bool>() == false) return r["result"]!["structuredContent"]?.AsObject() ?? new JsonObject();
                Report($"[notice] Sampler calibration: {name} failed: {r["result"]?["content"]?[0]?["text"] ?? r["error"]?.ToJsonString()}");
                return null;
            }
            void Line(string gear, JsonArray changes = null, string label = null)
            {
                if (gear != "Neo Sampler" && !Has(bin, "Generators", gear) && !Has(bin, "Generators", gear + ".NET")) { Report($"[notice] {gear} not in this build's Gear"); return; }
                if (!Song(gear, changes)) return;
                var render = Try("render", Args(("tail_seconds", 0.3)));
                if (render?["id"] is not JsonValue idValue) return;
                string id = idValue.GetValue<string>();
                var pitches = new List<string>();
                for (int i = 0; i < line.Length; i++)
                {
                    var p = Try("analyze", Args(("source", id), ("metrics", new JsonArray("pitch", "loudness")), ("from_seconds", i + 0.05), ("to_seconds", i + 0.45)));
                    var n = p?["pitch"]?["notes"]?.AsArray().FirstOrDefault();
                    pitches.Add($"{n?["hz"]?.ToJsonString() ?? "-"} Hz {p?["loudness"]?["integrated_lufs"]?.ToJsonString() ?? "-"} LUFS");
                }
                var a = Try("analyze", Args(("source", id), ("metrics", new JsonArray("loudness", "spectrum", "peaks"))));
                if (a == null) return;
                Report($"[notice] Sampler calibration, {label ?? gear}: {a["loudness"]?["integrated_lufs"]} LUFS, peak {a["peaks"]?["sample_peak_dbfs"]} dBFS, centroid {a["spectrum"]?["centroid_hz"]} Hz; " +
                       $"notes (A3, A4, E3, A2): {string.Join(", ", pitches)}");
            }
            void Compare(string label, JsonArray changes)
            {
                if (!Has(bin, "Generators", "Pedal M1") && !Has(bin, "Generators", "Pedal M1.NET")) return;
                if (!Song("Pedal M1", changes)) return;
                var r = Quiet(client, "machine_replace", Args(("machine", "M")));
                var c = r["comparison"];
                var m = c?["machine"] ?? c?["song"];
                Report($"[notice] Sampler calibration, Pedal M1 → Neo Sampler {label}: {c?["within_tolerance"]?.ToJsonString() ?? "-"}, {m?["loudness_db"]?["difference"]?.ToJsonString() ?? "-"} dB, " +
                       $"worst {m?["worst_band"]?["difference_db"]?.ToJsonString() ?? "-"} dB at {m?["worst_band"]?["hz"]?.ToJsonString() ?? "-"} Hz; notes {r["patterns"]?[0]?["notes"]?.ToJsonString() ?? "-"}");
            }

            Line("Neo Sampler");
            Line("Neo Sampler", Changes(("Volume", "-10.2 dB"), ("Filter", "LP 12"), ("Cutoff", "18 kHz")), "Neo Sampler as Pedal M1's defaults convert");
            Line("Pedal M1");
            Compare("at its defaults", null);
            Compare("with a key zone (the square from F#3) and both oscillators", Raw(("OSC1 Wave 2", 2), ("OSC1 Split 2", 66), ("OSC Mode", 2), ("OSC2 Wave", 1), ("OSC2 Octave", 3), ("OSC2 Level", 64)));
            Compare("with its envelopes and filter", Raw(("Amp Attack", 60), ("Amp Sustain", 64), ("Amp Release", 70), ("Cutoff", 90), ("Filter EG Int", 40), ("Filter Decay", 60)));
            Compare("resonance 25 % at 1 kHz", Raw(("Cutoff", 73), ("Resonance", 32)));
            // The trackers last: what they do with notes from the tools is what's measured (Jeskola Tracker's profile heard nothing).
            Line("Matilde Tracker2");
            Line("Jeskola Tracker");
            foreach (var f in new[] { saw, square }) try { File.Delete(f); } catch (IOException) { }
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
