// Evaluation (docs/specs/mcp-server.md §10, scripted): fixed tool sequences through the real
// protocol, each asserting exact results.
//  - protocol: the handshake, the tool list against the design rules (§4), errors that say what next
//  - notes round-trip exactly (notes_read gives back what notes_write took, and takes it again)
//  - param_set "800 Hz" reads back "800 Hz" (and other units, relative changes, choices, raw)
//  - renders are bit-identical across runs (reopening the song; a second ReBuzz process)
//  - analyze meets reference values on known signals, and agrees with our own meters on a render
//  - undo restores the song exactly (the full tool-read state), redo replays it, a failed call changes nothing

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReBuzz.Agent.Audio;

namespace ReBuzz.AgentEval
{
    internal sealed class CheckRun
    {
        public CheckRun(CheckResult result) => Result = result;

        public CheckResult Result { get; }

        public void Expect(bool ok, string failure)
        {
            if (!ok) Result.Failures.Add(failure);
        }

        public void Note(string note) => Result.Notes.Add(note);
    }

    internal static class Checks
    {
        public sealed record Def(string Name, string Title, Action<Studio, CheckRun, Env> Run);

        /// <summary>What checks may need beyond the session: a folder, and a way to start another ReBuzz.</summary>
        public sealed class Env
        {
            public string Folder;
            public Func<string, McpClient> StartReBuzz;
        }

        public static readonly Def[] All =
        {
            new("protocol", "Protocol and tool design", Protocol),
            new("notes_roundtrip", "Notes round-trip exactly", NotesRoundTrip),
            new("param_units", "param_set \"800 Hz\" reads back \"800 Hz\"", ParamUnits),
            new("render_bit_identical", "Renders bit-identical across runs", RenderBitIdentical),
            new("analyze_reference", "analyze meets reference values", AnalyzeReference),
            new("undo_exact", "Undo restores the song exactly", UndoExact),
        };

        // ---- protocol ---------------------------------------------------------------------------

        static void Protocol(Studio s, CheckRun r, Env env)
        {
            var c = s.C;
            r.Expect(J.Str(c.ServerInfo?["name"]) == "rebuzz", $"serverInfo.name is {J.Str(c.ServerInfo?["name"])}, not rebuzz");
            r.Expect(c.ServerCapabilities?["tools"] != null, "the server doesn't declare the tools capability");
            r.Expect(!string.IsNullOrWhiteSpace(c.Instructions), "no instructions in initialize");
            var tools = c.ListTools();
            r.Note($"{tools.Count} tools");
            r.Expect(tools.Count >= 45, $"only {tools.Count} tools (A3 has 60 or so)");
            var name = new Regex("^[a-z]+(_[a-z0-9]+)*$");
            var problems = new List<string>();
            foreach (var t in J.Objects(tools))
            {
                string n = J.Str(t["name"]);
                if (!name.IsMatch(n)) problems.Add($"{n}: not area_verb");
                string d = J.Str(t["description"]) ?? "";
                if (!d.Contains("Example")) problems.Add($"{n}: no example in the description");
                if (J.Str(t["inputSchema"]?["type"]) != "object") problems.Add($"{n}: inputSchema isn't an object");
                if (t["outputSchema"] == null) problems.Add($"{n}: no outputSchema");
                var a = t["annotations"] as JsonObject;
                if (a == null) problems.Add($"{n}: no annotations");
                else if (a["openWorldHint"] is not JsonValue ow || ow.GetValue<bool>()) problems.Add($"{n}: openWorldHint isn't false");
            }
            foreach (var p in problems) r.Expect(false, p);
            // Errors say the next step.
            var unknown = c.RequestRaw("tools/call", new JsonObject { ["name"] = "machine_ad", ["arguments"] = new JsonObject() });
            r.Expect((J.Str(unknown["error"]?["message"]) ?? "").Contains("Did you mean \"machine_add\""), $"unknown tool: {unknown.ToJsonString()}");
            var badArg = c.Call("song_set", new JsonObject { ["tempo"] = 120 });
            r.Expect(badArg.IsError && badArg.Text.Contains("tempo_bpm"), $"an unknown argument: isError={badArg.IsError}, \"{badArg.Text}\"");
            s.New();
            var noMachine = c.Call("machine_describe", new JsonObject { ["machine"] = "Nothing" });
            r.Expect(noMachine.IsError && noMachine.Text.Length > 10, $"an unknown machine: isError={noMachine.IsError}");
            var status = s.Status();
            r.Expect(J.Str(status["mode"]) == "headless", $"mode is {J.Str(status["mode"])}");
            var guide = c.Ok("guide", new JsonObject { ["topic"] = "workflow" });
            r.Expect((J.Str(guide["markdown"]) ?? "").Length > 200, "the workflow guide is empty");
            r.Expect(c.ProtocolViolations.Count == 0, "stdout carried lines that aren't JSON-RPC: " + string.Join(" | ", c.ProtocolViolations.Take(3)));
        }

        // ---- notes ------------------------------------------------------------------------------

        static void NotesRoundTrip(Studio s, CheckRun r, Env env)
        {
            var c = s.C;
            s.New(120);
            Recipes.Add(s, Recipes.Synth, "Keys", 0);
            c.Ok("pattern_create", new JsonObject { ["machine"] = "Keys", ["name"] = "A", ["length_beats"] = 8 });
            // Names, sharps and flats, MIDI numbers, a chord, sixteenths, a triplet, the extremes of velocity, a note to the very end.
            var written = new (object Pitch, double Start, double Length, int Velocity, int Midi)[]
            {
                ("C4", 0, 1, 100, 60), ("E4", 0, 1, 90, 64), ("G4", 0, 1, 80, 67),
                ("F#3", 1.25, 0.25, 1, 54), ("Bb2", 1.5, 0.125, 127, 46), (72, 2, 0.5, 64, 72),
                ("A0", 1.0 / 3 + 2, 1.0 / 3, 50, 21), ("C8", 3.75, 0.0625, 100, 108), (60, 4, 4, 100, 60),
            };
            c.Ok("notes_write", new JsonObject
            {
                ["machine"] = "Keys", ["pattern"] = "A",
                ["notes"] = new JsonArray(written.Select(n => (JsonNode)J.Note(n.Pitch, Math.Round(n.Start, 6), Math.Round(n.Length, 6), n.Velocity)).ToArray()),
            });
            var read = J.Objects(c.Ok("notes_read", new JsonObject { ["machine"] = "Keys", ["pattern"] = "A" })["notes"]).ToList();
            var expected = written.Select(n => (n.Midi, Start: Math.Round(n.Start, 6), Length: Math.Round(n.Length, 6), n.Velocity)).OrderBy(n => n.Start).ThenBy(n => n.Midi).ToList();
            var got = read.Select(n => ((int)J.Num(n["pitch"]), Start: J.Num(n["start"]), Length: J.Num(n["length"]), (int)J.Num(n["velocity"]))).ToList();
            r.Expect(got.Count == expected.Count, $"wrote {expected.Count} notes, read {got.Count}");
            for (int i = 0; i < Math.Min(got.Count, expected.Count); i++)
                r.Expect(got[i] == expected[i], $"note {i}: wrote {expected[i]}, read {got[i]}");
            // What notes_read gives, notes_write takes, and reads back the same (rule 8).
            c.Ok("notes_write", new JsonObject { ["machine"] = "Keys", ["pattern"] = "A", ["notes"] = new JsonArray(read.Select(n => (JsonNode)n.DeepClone()).ToArray()), ["mode"] = "replace" });
            var again = c.Ok("notes_read", new JsonObject { ["machine"] = "Keys", ["pattern"] = "A" })["notes"];
            r.Expect(JsonNode.DeepEquals(again, new JsonArray(read.Select(n => (JsonNode)n.DeepClone()).ToArray())), "writing back what notes_read gave changes the notes: " + again?.ToJsonString());
            // A range reads the notes that start in it.
            var range = J.Objects(c.Ok("notes_read", new JsonObject { ["machine"] = "Keys", ["pattern"] = "A", ["range_start"] = 1, ["range_end"] = 2 })["notes"]).ToList();
            r.Expect(range.Count == 2, $"notes_read from beat 1 to 2 gave {range.Count} notes, not 2 (F#3 at 1.25, Bb2 at 1.5)");
            // And a copy of the pattern carries them all.
            c.Ok("pattern_duplicate", new JsonObject { ["machine"] = "Keys", ["pattern"] = "A", ["new_name"] = "B" });
            var copy = c.Ok("notes_read", new JsonObject { ["machine"] = "Keys", ["pattern"] = "B" })["notes"];
            r.Expect(JsonNode.DeepEquals(copy, again), "pattern_duplicate changed the notes");
            // They survive saving and opening the song.
            string song = Path.Combine(env.Folder, "notes.bmxml");
            s.Save(song);
            s.Open(song);
            var reopened = c.Ok("notes_read", new JsonObject { ["machine"] = "Keys", ["pattern"] = "A" })["notes"];
            r.Expect(JsonNode.DeepEquals(reopened, again), "the notes changed through song_save and song_open: " + reopened?.ToJsonString());
        }

        // ---- parameters in units ------------------------------------------------------------------

        static void ParamUnits(Studio s, CheckRun r, Env env)
        {
            var c = s.C;
            s.New(120);
            Recipes.Add(s, Recipes.Synth, "Synth", 0);
            Recipes.Add(s, "Neo Comp", "Comp", 0, insertAfter: "Synth");
            Recipes.Add(s, "Neo Verb", "Verb", 0, insertAfter: "Comp");
            JsonObject Get(string machine, string param) =>
                J.Objects(c.Ok("param_get", new JsonObject { ["machine"] = machine, ["params"] = J.A(param) })["values"]).First();
            void Set(string machine, string param, string form, JsonNode value) =>
                c.Ok("param_set", new JsonObject { ["machine"] = machine, ["changes"] = J.A(new JsonObject { ["param"] = param, [form] = value }) });

            // The spec's line: "800 Hz" reads back "800 Hz".
            Set("Synth", "F1 cutoff", "value", "800 Hz");
            var cutoff = Get("Synth", "F1 cutoff");
            r.Expect(J.Str(cutoff["value"]) == "800 Hz", $"F1 cutoff \"800 Hz\" reads back \"{J.Str(cutoff["value"])}\"");
            r.Expect(Math.Abs(J.Num(cutoff["number"]) - 800) <= 800 * 0.004 && J.Str(cutoff["unit"]) == "Hz", $"F1 cutoff number {cutoff["number"]} {cutoff["unit"]}");

            // Numbers within one step of the parameter's resolution, in every kind of unit.
            var cases = new (string Machine, string Param, string Form, JsonNode Value, double Want, double Tol, string Unit)[]
            {
                ("Synth", "Amp release", "value", "250 ms", 0.25, 0.25 * 0.011, "s"),
                ("Synth", "Volume", "value", "-6 dB", -6, 0.05, "dB"),
                ("Synth", "Volume", "by", "+3 dB", -3, 0.06, "dB"),
                ("Synth", "F1 cutoff", "times", 2, 1600, 1600 * 0.008, "Hz"),
                ("Synth", "F1 reso", "value", "35%", 35, 0.06, "%"),
                ("Synth", "F1 env", "value", "-40%", -40, 0.11, "%"),
                ("Comp", "Ratio", "value", "4:1", 4, 0.1, ":1"),
                ("Comp", "Threshold", "value", "-18 dB", -18, 0.035, "dB"),
                ("Comp", "Attack", "value", "5 ms", 0.005, 0.005 * 0.012, "s"),
                ("Verb", "Mix", "value", "25%", 25, 0.06, "%"),
                ("Verb", "Pre-delay", "value", "20 ms", 0.02, 0.00026, "s"),
                // A bare number is in the unit param_get reports (seconds here, though the knob shows ms).
                ("Verb", "Pre-delay", "value", 0.03, 0.03, 0.00026, "s"),
            };
            foreach (var (m, p, form, value, want, tol, unit) in cases)
            {
                var call = c.Call("param_set", new JsonObject { ["machine"] = m, ["changes"] = J.A(new JsonObject { ["param"] = p, [form] = value.DeepClone() }) });
                if (call.IsError) { r.Expect(false, $"{m} {p} {form} {value.ToJsonString()}: {call.Text}"); continue; }
                var got = Get(m, p);
                double n = J.Num(got["number"]);
                r.Expect(Math.Abs(n - want) <= tol && J.Str(got["unit"]) == unit, $"{m} {p} {form} {value.ToJsonString()}: reads {got["value"]} = {n} {got["unit"]}, want {want} {unit} (±{tol})");
            }
            // Out of range: recorded, not failed (values clamp by design, A1). An agent meaning 20 ms who
            // passes a bare 20 gets the top of the range; a warning or an error would serve it better.
            var clamp = c.Call("param_set", new JsonObject { ["machine"] = "Verb", ["changes"] = J.A(new JsonObject { ["param"] = "Pre-delay", ["value"] = 20 }) });
            if (!clamp.IsError)
                r.Note($"a bare 20 for Verb Pre-delay (read in seconds) lands at {J.Str(Get("Verb", "Pre-delay")["value"])} without a warning" +
                       (clamp.Text.Contains("range", StringComparison.OrdinalIgnoreCase) ? " (the summary mentions the range)" : ""));

            // Choices, min/max/default and raw.
            foreach (var (m, p, value, want) in new[] { ("Synth", "Voice mode", "Mono", "Mono"), ("Synth", "F1 type", "SVF HP", "SVF HP"), ("Synth", "LFO1 sync", "1/8T", "1/8T"), ("Comp", "Mode", "Opto", "Opto") })
            {
                Set(m, p, "value", value);
                r.Expect(J.Str(Get(m, p)["value"]) == want, $"{m} {p} \"{value}\" reads {Get(m, p)["value"]}");
            }
            Set("Verb", "Decay", "value", "default");
            r.Expect(J.Num(Get("Verb", "Decay")["raw"]) == 504, $"Verb Decay default reads raw {Get("Verb", "Decay")["raw"]} (504 in the manifest)");
            Set("Comp", "Release", "value", "raw:512");
            r.Expect(J.Num(Get("Comp", "Release")["raw"]) == 512, "raw:512 didn't land");
            Set("Synth", "Polyphony", "value", "max");
            r.Expect(J.Num(Get("Synth", "Polyphony")["number"]) == 16, $"Polyphony max reads {Get("Synth", "Polyphony")["value"]}");

            // Mistakes: errors that say what next, and nothing changes.
            var before = Get("Synth", "F1 cutoff");
            var typo = c.Call("param_set", new JsonObject { ["machine"] = "Synth", ["changes"] = J.A(new JsonObject { ["param"] = "F1 cutof", ["value"] = "1 kHz" }) });
            r.Expect(typo.IsError && typo.Text.Contains("Did you mean"), $"a misspelt parameter: \"{typo.Text}\"");
            var unit2 = c.Call("param_set", new JsonObject { ["machine"] = "Synth", ["changes"] = J.A(new JsonObject { ["param"] = "F1 cutoff", ["value"] = "800 dB" }) });
            r.Expect(unit2.IsError && unit2.Text.Contains("Hz"), $"a wrong unit: \"{unit2.Text}\"");
            var half = c.Call("param_set", new JsonObject
            {
                ["machine"] = "Synth",
                ["changes"] = J.A(new JsonObject { ["param"] = "F1 cutoff", ["value"] = "2 kHz" }, new JsonObject { ["param"] = "F1 type", ["value"] = "Wobble" }),
            });
            r.Expect(half.IsError, "a call with one bad change succeeded");
            r.Expect(JsonNode.DeepEquals(Get("Synth", "F1 cutoff"), before), "a failed call changed F1 cutoff");
        }

        // ---- renders ----------------------------------------------------------------------------

        /// <summary>A small song with LFOs, drift, a reverb, saturation and compression: what makes renders differ if anything does.</summary>
        static string DeterminismSong(Studio s, Env env)
        {
            s.New(110);
            Recipes.Add(s, Recipes.Synth, "Keys", -6);
            s.C.Ok("preset_load", new JsonObject { ["machine"] = "Keys", ["preset"] = "Pad — Warm Strings" });
            Recipes.Add(s, "Neo Verb", "Hall", 0, insertAfter: "Keys");
            Recipes.Set(s, "Hall", ("Mix", "30%"));
            Recipes.Add(s, Recipes.Synth, "Bass", -8);
            s.C.Ok("preset_load", new JsonObject { ["machine"] = "Bass", ["preset"] = "Bass — Acid" });
            Recipes.Add(s, "Neo Drive", "Grit", 0, insertAfter: "Bass");
            Recipes.Add(s, "Neo Comp", "Squash", 0, insertAfter: "Grit");
            Recipes.Pattern(s, "Keys", "Chords", 8, new[] { "A3", "C4", "E4" }.Select(p => J.Note(p, 0, 4, 90)).Concat(new[] { "F3", "A3", "C4" }.Select(p => J.Note(p, 4, 4, 90))));
            Recipes.Pattern(s, "Bass", "Line", 8, Enumerable.Range(0, 16).Select(i => J.Note(i % 3 == 0 ? "A1" : "A2", i * 0.5, 0.3, 80 + i * 3)));
            Recipes.Place(s, "Keys", "Chords", 0);
            Recipes.Place(s, "Bass", "Line", 0);
            string song = Path.Combine(env.Folder, "determinism.bmxml");
            s.Save(song);
            return song;
        }

        static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))[..16];

        static double MaxDifference(string a, string b)
        {
            var x = Wav.Read(a);
            var y = Wav.Read(b);
            if (x.Frames != y.Frames) return double.PositiveInfinity;
            double max = 0;
            for (int i = 0; i < x.Frames; i++) max = Math.Max(max, Math.Max(Math.Abs(x.Left[i] - y.Left[i]), Math.Abs((x.Right?[i] ?? 0) - (y.Right?[i] ?? 0))));
            return max;
        }

        static void RenderBitIdentical(Studio s, CheckRun r, Env env)
        {
            var c = s.C;
            string song = DeterminismSong(s, env);
            var args = new JsonObject { ["tail_seconds"] = 2, ["probes"] = J.A("Hall", "Squash") };
            s.Open(song);
            var first = c.Ok("render", args);
            string f1 = J.Str(first["file"]);
            r.Expect(!J.Bool(first["measure"]?["silent"]), "the song renders silent");
            s.Open(song);
            var second = c.Ok("render", args);
            string f2 = J.Str(second["file"]);
            r.Expect(Hash(f1) == Hash(f2), $"two renders of the song, opened afresh, differ: max sample difference {MaxDifference(f1, f2):E2}");
            var p1 = J.Objects(first["probes"]).ToList();
            var p2 = J.Objects(second["probes"]).ToList();
            for (int i = 0; i < Math.Min(p1.Count, p2.Count); i++)
                r.Expect(Hash(J.Str(p1[i]["file"])) == Hash(J.Str(p2[i]["file"])), $"probe {J.Str(p1[i]["machine"])} differs between the two renders");
            // Rendering again without reopening: the machines go on from where they were (voice state, random phases).
            var third = c.Ok("render", args);
            double diff = MaxDifference(f2, J.Str(third["file"]));
            r.Note(diff == 0 ? "a repeated render in one session is bit-identical too" : $"a repeated render without reopening differs by up to {diff:E2} (machine state carries over; reopen for bit-identical renders)");
            // Another ReBuzz process, started on the song: the same bits.
            if (env.StartReBuzz != null)
            {
                using var other = env.StartReBuzz(song);
                other.Initialize();
                var o = other.Ok("render", args);
                string f3 = J.Str(o["file"]);
                r.Expect(Hash(f1) == Hash(f3), $"a second ReBuzz process renders the song differently: max sample difference {MaxDifference(f1, f3):E2}");
                r.Result.Calls += other.Calls;
                r.Result.Errors += other.ErrorCalls;
                other.CloseAndWait(30);
            }
        }

        // ---- analysis ---------------------------------------------------------------------------

        static string WriteSignal(Env env, string name, int seconds, Func<int, int, (float L, float R)> sample)
        {
            const int rate = 48000;
            int frames = seconds * rate;
            var x = new float[2 * frames];
            for (int i = 0; i < frames; i++)
            {
                var (l, rr) = sample(i, rate);
                x[2 * i] = l;
                x[2 * i + 1] = rr;
            }
            string file = Path.Combine(env.Folder, "signals", name + ".wav");
            Wav.WriteStereoFloat(file, x, frames, rate);
            return file;
        }

        static void AnalyzeReference(Studio s, CheckRun r, Env env)
        {
            var c = s.C;
            JsonObject Analyze(string source, params string[] metrics) =>
                c.Ok("analyze", new JsonObject { ["source"] = source, ["metrics"] = J.Strings(metrics) });
            void Near(string what, JsonNode got, double want, double tol)
            {
                double n = J.Num(got);
                r.Expect(Math.Abs(n - want) <= tol, $"{what}: {J.F(n, 3)}, want {want} ±{tol}");
            }

            // EBU Tech 3341: a stereo 1 kHz sine at -23 dBFS reads -23 LUFS; its true and sample peaks -23 dB; crest 3.01 dB.
            double amp = Math.Pow(10, -23 / 20.0);
            string sine = WriteSignal(env, "sine-1k-23dBFS", 10, (i, rate) => { float v = (float)(amp * Math.Sin(2 * Math.PI * 1000 * i / rate)); return (v, v); });
            var a = Analyze(sine, "loudness", "peaks", "dynamics", "spectrum");
            Near("1 kHz −23 dBFS: integrated", a["loudness"]?["integrated_lufs"], -23, 0.1);
            Near("1 kHz −23 dBFS: true peak", a["peaks"]?["true_peak_dbtp"], -23, 0.1);
            Near("1 kHz −23 dBFS: sample peak", a["peaks"]?["sample_peak_dbfs"], -23, 0.05);
            Near("1 kHz sine: crest", a["dynamics"]?["crest_db"], 3.01, 0.1);
            Near("1 kHz sine: centroid", a["spectrum"]?["centroid_hz"], 1000, 15);
            var bands = a["spectrum"]?["bands_db"] as JsonObject;
            r.Expect(bands != null && bands.OrderByDescending(kv => J.Num(kv.Value)).First().Key == "mid", "1 kHz sine: the loudest band isn't mid");
            // Our own meters agree with the tool on the same file.
            var own = Meters.Loudness(Meters.Read(sine));
            Near("our meter against analyze (integrated)", a["loudness"]?["integrated_lufs"], own.Integrated, 0.051);

            // Anti-phase noise: correlation −1, it vanishes in mono.
            var rng = new Random(7);
            string anti = WriteSignal(env, "noise-antiphase", 4, (i, rate) => { float v = (float)(0.2 * (rng.NextDouble() * 2 - 1)); return (v, -v); });
            var st = Analyze(anti, "stereo");
            Near("anti-phase noise: correlation", st["stereo"]?["correlation"], -1, 0.01);

            // A click track at 120 BPM: 120 BPM.
            string clicks = WriteSignal(env, "clicks-120", 8, (i, rate) =>
            {
                int k = i % (rate / 2);
                float v = k < rate / 200 ? (float)(0.8 * Math.Exp(-k / (rate / 2000.0)) * Math.Sin(2 * Math.PI * 2000 * k / rate)) : 0;
                return (v, v);
            });
            var t = Analyze(clicks, "tempo");
            Near("120 BPM clicks: tempo", t["tempo"]?["bpm"], 120, 0.1);

            // A band-limited sawtooth on A3 (220 Hz): A3, 0 cents.
            string saw = WriteSignal(env, "saw-a3", 3, (i, rate) =>
            {
                double v = 0;
                for (int h = 1; h * 220 < rate / 2.5; h++) v += Math.Sin(2 * Math.PI * 220 * h * i / rate) / h;
                float f = (float)(0.3 * v);
                return (f, f);
            });
            var p = Analyze(saw, "pitch");
            r.Expect(J.Str(p["pitch"]?["median_note"]) == "A3", $"saw on A3: median note {J.Str(p["pitch"]?["median_note"])}");
            Near("saw on A3: cents", p["pitch"]?["median_cents"], 0, 2);

            // A C major scale, harmonic tones: C major.
            int[] scale = { 60, 62, 64, 65, 67, 69, 71, 72 };
            string cmaj = WriteSignal(env, "scale-c-major", 4, (i, rate) =>
            {
                int note = scale[Math.Min(scale.Length - 1, i / (rate / 2))];
                double hz = 440 * Math.Pow(2, (note - 69) / 12.0), v = 0;
                for (int h = 1; h <= 4; h++) v += Math.Sin(2 * Math.PI * hz * h * i / rate) / (h * h);
                float f = (float)(0.3 * v);
                return (f, f);
            });
            var k2 = Analyze(cmaj, "key");
            r.Expect(J.Str(k2["key"]?["name"]) == "C major", $"C major scale: key {J.Str(k2["key"]?["name"])}");

            // A render: the tool's numbers are our meters' numbers on its WAV.
            string song = Path.Combine(env.Folder, "determinism.bmxml");
            if (!File.Exists(song)) song = DeterminismSong(s, env);
            s.Open(song);
            var rendered = c.Ok("render", new JsonObject { ["tail_seconds"] = 1 });
            var m = Analyze(J.Str(rendered["id"]), "loudness", "peaks", "spectrum", "stereo");
            var audio = Meters.Read(J.Str(rendered["file"]));
            var l = Meters.Loudness(audio);
            Near("render: integrated, tool against our meter", m["loudness"]?["integrated_lufs"], l.Integrated, 0.051);
            Near("render: true peak, tool against our meter", m["peaks"]?["true_peak_dbtp"], l.TruePeakDb, 0.006);
            Near("render: tilt, tool against our meter", m["spectrum"]?["tilt_db_per_octave"], Meters.Spectrum(audio).TiltDbPerOctave, 0.006);
            Near("render: correlation, tool against our meter", m["stereo"]?["correlation"], Meters.Stereo(audio).Correlation, 0.006);
            Near("render: render's own loudness against analyze", rendered["measure"]?["loudness"]?["integrated_lufs"], J.Num(m["loudness"]?["integrated_lufs"]), 0.001);
        }

        // ---- undo -------------------------------------------------------------------------------

        /// <summary>Everything the tools can read about the song, minus what undo doesn't own (the file, undo itself, the transport).</summary>
        static JsonObject State(McpClient c)
        {
            var s = new JsonObject();
            var status = c.Ok("studio_status");
            foreach (var k in new[] { "song", "undo", "transport", "agent", "warnings", "mode" }) status.Remove(k);
            s["status"] = status;
            var machines = c.Ok("machine_list")["machines"]!.DeepClone();
            // Content compares by name; the order (of machines, connections, lanes, session tracks) is kept apart:
            // undo puts a restored machine or connection at the end of its list.
            var order = new JsonObject
            {
                ["machines"] = J.Strings(J.Objects(machines).Select(m => J.Str(m["name"]))),
                ["connections"] = new JsonObject(J.Objects(machines).Select(m => KeyValuePair.Create(J.Str(m["name"]),
                    (JsonNode)J.Strings(J.Objects(m["inputs"]).Select(i => "in:" + J.Str(i["from"])).Concat(J.Objects(m["outputs"]).Select(o => "out:" + J.Str(o["to"]))))))),
            };
            s["order"] = order;
            s["machines"] = ByName(machines, "name", m =>
            {
                foreach (var (k, key) in new[] { ("inputs", "from"), ("outputs", "to") })
                    if (m[k] is JsonArray list) m[k] = ByName(list, key);
            });
            var ps = new JsonObject();
            foreach (var m in J.Objects(machines))
            {
                string name = J.Str(m["name"]);
                if (J.Str(m["kind"]) == "master") continue;
                int tracks = Math.Max(1, (int)J.Num(m["tracks"], 1));
                var perTrack = new JsonArray();
                for (int t = 0; t < tracks; t++)
                    perTrack.Add(c.Ok("param_get", new JsonObject { ["machine"] = name, ["track"] = t })["values"]!.DeepClone());
                ps[name] = perTrack;
            }
            s["params"] = ps;
            var patterns = ByName(c.Ok("pattern_list")["machines"]!, "machine");
            s["patterns"] = patterns;
            var notes = new JsonObject();
            foreach (var m in J.Objects(patterns))
                foreach (var p in J.Objects(m["patterns"]))
                {
                    var args = new JsonObject { ["machine"] = J.Str(m["machine"]), ["pattern"] = J.Str(p["name"]) };
                    notes[$"{m["machine"]}/{p["name"]}"] = new JsonObject
                    {
                        ["notes"] = c.Ok("notes_read", args)["notes"]!.DeepClone(),
                        ["automation"] = c.Ok("automation_read", (JsonObject)args.DeepClone())["automation"]!.DeepClone(),
                    };
                }
            s["pattern_data"] = notes;
            var arrangement = c.Ok("arrangement_get");
            order["lanes"] = J.Strings(J.Objects(arrangement["lanes"]).Select(l => J.Str(l["machine"])));
            arrangement["lanes"] = ByName(arrangement["lanes"]!, "machine");
            s["arrangement"] = arrangement;
            s["markers"] = c.Ok("arrangement_markers")["markers"]!.DeepClone();
            var session = c.Ok("session_get");
            if (session["tracks"] is JsonArray sessionTracks)
            {
                order["session_tracks"] = J.Strings(J.Objects(sessionTracks).Select(t => J.Str(t["machine"])));
                session["tracks"] = ByName(sessionTracks, "machine");
            }
            s["session"] = session;
            s["waves"] = c.Ok("wave_list")["waves"]!.DeepClone();
            return s;
        }

        /// <summary>A copy of a list of objects sorted by one of their fields, each optionally adjusted.</summary>
        static JsonArray ByName(JsonNode list, string key, Action<JsonObject> adjust = null) =>
            new(J.Objects(list).Select(o => (JsonObject)o.DeepClone()).OrderBy(o => J.Str(o[key]), StringComparer.Ordinal)
                .Select(o => { adjust?.Invoke(o); return (JsonNode)o; }).ToArray());

        /// <summary>A state without its order record (content only).</summary>
        static JsonObject Content(JsonObject state)
        {
            var copy = (JsonObject)state.DeepClone();
            copy.Remove("order");
            return copy;
        }

        /// <summary>Paths where two JSON trees differ (the first few).</summary>
        static List<string> Diff(JsonNode a, JsonNode b, string path = "$", List<string> into = null)
        {
            into ??= new List<string>();
            if (into.Count >= 12) return into;
            if (a is JsonObject oa && b is JsonObject ob)
            {
                foreach (var k in oa.Select(kv => kv.Key).Union(ob.Select(kv => kv.Key)))
                    Diff(oa[k], ob[k], $"{path}.{k}", into);
            }
            else if (a is JsonArray xa && b is JsonArray xb)
            {
                if (xa.Count != xb.Count) into.Add($"{path}: {xa.Count} items → {xb.Count}");
                for (int i = 0; i < Math.Min(xa.Count, xb.Count); i++) Diff(xa[i], xb[i], $"{path}[{i}]", into);
            }
            else if (!JsonNode.DeepEquals(a, b))
                into.Add($"{path}: {Short(a)} → {Short(b)}");
            return into;
        }

        static string Short(JsonNode n)
        {
            string s = n?.ToJsonString() ?? "(absent)";
            return s.Length > 80 ? s[..80] + "…" : s;
        }

        static void UndoExact(Studio s, CheckRun r, Env env)
        {
            var c = s.C;
            // A song with something of everything.
            s.New(120);
            Recipes.Add(s, Recipes.Synth, "Bass", -6);
            Recipes.Add(s, "Neo Drive", "Drive", 0, insertAfter: "Bass");
            Recipes.Add(s, Recipes.Synth, "Pad", -8);
            Recipes.Pattern(s, "Bass", "Riff", 8, new[] { J.Note("A1", 0, 1), J.Note("C2", 2, 1), J.Note("G1", 4, 2) });
            Recipes.Pattern(s, "Pad", "P", 8, new[] { "A3", "C4", "E4" }.Select(p => J.Note(p, 0, 8, 90)));
            Recipes.Place(s, "Bass", "Riff", 0, 8, 2);
            Recipes.Place(s, "Pad", "P", 0, 8, 2);
            string tone = WriteSignal(env, "tone", 1, (i, rate) => { float v = (float)(0.4 * Math.Sin(2 * Math.PI * 330 * i / rate)); return (v, v); });
            c.Ok("snapshot_save", new JsonObject { ["name"] = "start" });
            var s0 = State(c);

            // Every kind of change, one call each.
            var steps = new List<(string Tool, JsonObject Args)>
            {
                ("song_set", new JsonObject { ["tempo_bpm"] = 133, ["loop_start_beats"] = 4, ["loop_end_beats"] = 12 }),
                ("machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Lead", ["gain_db"] = -9 }),
                ("param_set", new JsonObject { ["machine"] = "Lead", ["changes"] = J.Changes(("F1 cutoff", "1200 Hz"), ("Volume", "-8 dB"), ("Voice mode", "Legato")) }),
                ("machine_add", new JsonObject { ["gear"] = "Neo Verb", ["name"] = "Hall", ["insert_after"] = "Lead" }),
                ("param_set", new JsonObject { ["machine"] = "Hall", ["changes"] = J.Changes(("Mix", "40%")) }),
                ("connection_set", new JsonObject { ["changes"] = J.A(new JsonObject { ["from"] = "Pad", ["to"] = "Master", ["gain_db"] = -4, ["pan"] = 0.3 }) }),
                ("pattern_create", new JsonObject { ["machine"] = "Lead", ["name"] = "Hook", ["length_beats"] = 8 }),
                ("notes_write", new JsonObject { ["machine"] = "Lead", ["pattern"] = "Hook", ["notes"] = J.A(J.Note("E5", 0, 1), J.Note("D5", 1, 1), J.Note("C5", 2, 2)) }),
                ("arrangement_place", new JsonObject { ["machine"] = "Lead", ["pattern"] = "Hook", ["at"] = J.A(0, 8) }),
                ("notes_transform", new JsonObject { ["machine"] = "Bass", ["pattern"] = "Riff", ["transforms"] = J.A(new JsonObject { ["op"] = "transpose", ["semitones"] = 2 }) }),
                ("automation_write", new JsonObject { ["machine"] = "Pad", ["pattern"] = "P", ["param"] = "F1 cutoff", ["points"] = J.A(new JsonObject { ["at"] = 0, ["value"] = "300 Hz" }, new JsonObject { ["at"] = 8, ["value"] = "5000 Hz" }) }),
                ("arrangement_markers", new JsonObject { ["set"] = J.A(new JsonObject { ["name"] = "Intro", ["at"] = 0 }, new JsonObject { ["name"] = "Drop", ["at"] = "3.1" }) }),
                ("machine_set", new JsonObject { ["machine"] = "Pad", ["mute"] = true }),
                ("pattern_duplicate", new JsonObject { ["machine"] = "Bass", ["pattern"] = "Riff", ["new_name"] = "Riff 2" }),
                ("pattern_rename", new JsonObject { ["machine"] = "Bass", ["pattern"] = "Riff 2", ["new_name"] = "Fill" }),
                ("preset_load", new JsonObject { ["machine"] = "Pad", ["preset"] = "Pad — Glass" }),
                ("machine_remove", new JsonObject { ["machines"] = J.A("Drive") }),
                ("connect", new JsonObject { ["from"] = "Bass", ["to"] = "Hall", ["gain_db"] = -12 }),
                ("disconnect", new JsonObject { ["from"] = "Pad", ["to"] = "Master" }),
                ("session_clip_set", new JsonObject { ["machine"] = "Lead", ["scene"] = 2, ["pattern"] = "Hook" }),
                ("wave_import", new JsonObject { ["path"] = tone, ["name"] = "Tone", ["root_note"] = "E4" }),
                ("arrangement_clear", new JsonObject { ["machine"] = "Bass", ["from"] = 8, ["to"] = 16 }),
                ("pattern_delete", new JsonObject { ["machine"] = "Bass", ["pattern"] = "Fill" }),
                ("snapshot_restore", new JsonObject { ["name"] = "start" }),
            };
            int done = 0;
            foreach (var (tool, args) in steps)
            {
                var call = c.Call(tool, args);
                if (call.IsError) { r.Expect(false, $"{tool}: {call.Text}"); break; }
                done++;
            }
            var s1 = State(c);
            r.Expect(!JsonNode.DeepEquals(s0, s1), "the changes changed nothing the tools can read");
            for (int i = 0; i < done; i++)
            {
                var u = c.Call("undo");
                if (u.IsError) { r.Expect(false, $"undo {i + 1} of {done}: {u.Text}"); break; }
            }
            var back = State(c);
            var d0 = Diff(Content(s0), Content(back));
            r.Expect(d0.Count == 0, $"after {done} undos the song differs from before: " + string.Join("; ", d0));
            var o0 = Diff(s0["order"], back["order"]);
            if (o0.Count > 0) r.Note("after undo, restored machines and connections sit at the end of their lists: " + string.Join("; ", o0.Take(3)));
            for (int i = 0; i < done; i++)
            {
                var u = c.Call("redo");
                if (u.IsError) { r.Expect(false, $"redo {i + 1} of {done}: {u.Text}"); break; }
            }
            var again = State(c);
            var d1 = Diff(Content(s1), Content(again));
            r.Expect(d1.Count == 0, $"after {done} redos the song differs from after the changes: " + string.Join("; ", d1));
            // A call that fails changes nothing, and leaves undo where it was.
            string next = J.Str(s.Status()["undo"]?["next_undo"]);
            var failed = c.Call("param_set", new JsonObject
            {
                ["machine"] = "Lead",
                ["changes"] = J.A(new JsonObject { ["param"] = "F1 cutoff", ["value"] = "300 Hz" }, new JsonObject { ["param"] = "No such", ["value"] = 1 }),
            });
            r.Expect(failed.IsError, "a param_set with a bad change succeeded");
            var after = State(c);
            var d2 = Diff(again, after);
            r.Expect(d2.Count == 0, "a failed call changed the song: " + string.Join("; ", d2));
            r.Expect(J.Str(s.Status()["undo"]?["next_undo"]) == next, $"a failed call moved undo: {next} → {J.Str(s.Status()["undo"]?["next_undo"])}");
            r.Note($"{done} changing calls undone and redone");
        }
    }
}
