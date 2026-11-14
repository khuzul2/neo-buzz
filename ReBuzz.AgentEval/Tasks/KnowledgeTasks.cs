// Evaluation (docs/specs/agent-knowledge.md §3, K5): the knowledge tasks' definitions. See KnowledgeTask.cs for what a
// task is. Three groups, named by prefix so the workflow can give them their own turn and budget caps:
//   recipe_*  one per recipe in the recipes guide (the reference solution replays the recipe from the guide text);
//   guide_*   one per guide that no older task exercises (GuideCoverage names the older task for the others);
//   ask_*     questions: choose a machine for a job (the answer set comes from the catalog, machine_find), and set
//             parameters to values given in real units.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReBuzz.AgentEval
{
    internal static class KnowledgeTasks
    {
        /// <summary>The guides the older tasks exercise (a task per guide is required; these guides have theirs already).</summary>
        public static readonly Dictionary<string, string> GuideCoverage = new()
        {
            ["workflow"] = "beat",
            ["composition"] = "song",
            ["genres"] = "song",
            ["sound-design"] = "sound_match",
            ["mixing"] = "fix_the_mix",
            ["troubleshooting"] = "fix_harshness",
            ["mastering"] = "master",
            ["mastering-chain"] = "master_loud",
            ["recipes"] = "recipe_hear_a_bass",   // and the other nine recipe tasks
        };

        static readonly string[] Bass = { "Bass", "Neo Poly" };

        // ---- shared setups and checks -------------------------------------------------------------------

        /// <summary>Leaves the task out when this build lacks a classic machine (the quick package has no Pedal set).</summary>
        static void Need(Studio s, params string[] gear)
        {
            var have = J.Objects(s.C.Ok("gear_list", new JsonObject { ["limit"] = 1000 })["gear"]).Select(g => J.Str(g["name"])).ToHashSet();
            var missing = gear.Where(g => !have.Contains(g)).ToList();
            if (missing.Count > 0) throw new TaskSkipped("not installed in this build: " + string.Join(", ", missing));
        }

        static void BassSong(Studio s, TaskContext c) => K.Replay(s, "hear-a-bass");

        /// <summary>Bass still plays its two notes (C2 at 0, G2 at 2).</summary>
        static void NotesKept(Ck ck, Studio s)
        {
            var bass = K.Gear(s.Graph(), "Neo Poly", "Bass");
            var notes = K.Placed(s, bass?.Name);
            ck.Add("notes_kept", notes.Contains((K.Pitch("C2"), 0.0)) && notes.Contains((K.Pitch("G2"), 2.0)),
                bass == null ? "no Neo Poly" : string.Join(" ", notes.Select(n => $"{n.Pitch}@{n.Start}")));
        }

        /// <summary>The first number in a value text such as "2.99 kHz" or "300 Hz", in Hz.</summary>
        static double Hz(string text)
        {
            var m = Regex.Match(text ?? "", @"(-?[\d.]+)\s*(k?)Hz", RegexOptions.IgnoreCase);
            return m.Success ? double.Parse(m.Groups[1].Value, J.C) * (m.Groups[2].Value.Length > 0 ? 1000 : 1) : double.NaN;
        }

        static void Pattern(Studio s, string machine, string pattern, double beats, string pitch, double length = 2, double at = 0)
        {
            Recipes.Pattern(s, machine, pattern, beats, new[] { J.Note(pitch, 0, length, 100) });
            Recipes.Place(s, machine, pattern, at);
        }

        static IEnumerable<string> Effects(Graph g) => g.Machines.Where(m => m.Kind == "effect").Select(m => m.Gear);

        /// <summary>The machines machine_find answers for a tag (the catalog's own answer to "which machine for this").</summary>
        static HashSet<string> Found(Studio s, string tag, string kind = null)
        {
            var args = new JsonObject { ["tag"] = tag };
            if (kind != null) args["kind"] = kind;
            return J.Objects(s.C.Ok("machine_find", args)["matches"]).Select(m => J.Str(m["machine"])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // ---- the tasks ----------------------------------------------------------------------------------

        static KnowledgeTask Recipe(string id, string title, string goal, Action<Studio, TaskContext> setup, Action<Studio, TaskContext> solve, Action<Studio, TaskContext, Ck> check) =>
            new("recipe", "recipe_" + id.Replace('-', '_'), title, id, goal, setup, solve, check);

        static KnowledgeTask Guide(string guide, string title, string goal, Action<Studio, TaskContext> setup, Action<Studio, TaskContext> solve, Action<Studio, TaskContext, Ck> check) =>
            new("guide", "guide_" + guide.Replace('-', '_'), title, guide, goal, setup, solve, check);

        static KnowledgeTask Ask(string id, string title, string goal, Action<Studio, TaskContext> setup, Action<Studio, TaskContext> solve, Action<Studio, TaskContext, Ck> check) =>
            new("ask", "ask_" + id, title, "", goal, setup, solve, check);

        public static readonly KnowledgeTask[] All = Build();

        static KnowledgeTask[] Build() => new[]
        {
            // ======== recipes ==========================================================================
            Recipe("hear-a-bass", "Recipe: hear a bass",
                "Make a bass sound you can measure. Tempo 120 BPM; a Neo Poly called Bass loaded with its factory preset \"Bass — Classic Sub\"; a 4-beat pattern called Line with C2 at beat 0 and G2 at beat 2 (one beat each), placed at the start of the song. " +
                "Render it to confirm it is not silent and that its true peak is below 0 dBTP.",
                null, (s, c) => K.Replay(s, "hear-a-bass"),
                (s, c, ck) =>
                {
                    var bass = K.Gear(s.Graph(), "Neo Poly", "Bass");
                    ck.Add("tempo", Math.Abs(K.Tempo(s) - 120) < 0.5, $"{K.Tempo(s)} BPM");
                    ck.Add("synth", bass != null, bass?.Name ?? "no Neo Poly");
                    K.Value(ck, "preset", s, bass?.Name, "F1 cutoff", 401, 0.08, unit: " Hz (the preset's)");
                    var notes = K.Placed(s, bass?.Name);
                    ck.Add("notes_placed", notes.Contains((K.Pitch("C2"), 0.0)) && notes.Contains((K.Pitch("G2"), 2.0)), string.Join(" ", notes.Select(n => $"{n.Pitch}@{n.Start}")));
                    K.Sounds(ck, s);
                }),

            Recipe("movement-from-a-modulator", "Recipe: movement from a modulator",
                "The song has a Neo Poly called Bass playing a two-note line. Make its filter cutoff (F1 cutoff) move slowly with a modulator machine, not with automation: a Neo LFO called Wobble (a control machine, no audio output) at 30 percent depth. The notes stay as they are.",
                BassSong, (s, c) => K.Replay(s, "movement-from-a-modulator", skipLast: 1, withNeeds: false),
                (s, c, ck) =>
                {
                    var bass = K.Gear(s.Graph(), "Neo Poly", "Bass");
                    var targets = bass == null ? new List<JsonObject>() :
                        J.Objects(s.C.Ok("modulation_list", new JsonObject { ["machine"] = bass.Name })["modulators"]).SelectMany(m => J.Objects(m["targets"])).ToList();
                    var cutoff = targets.FirstOrDefault(t => string.Equals(J.Str(t["param"]), "F1 cutoff", StringComparison.OrdinalIgnoreCase));
                    ck.Add("modulated", cutoff != null, cutoff != null ? $"F1 cutoff modulated at depth {J.Num(cutoff["depth"])}" : "F1 cutoff has no modulator");
                    ck.Add("depth", cutoff != null && J.Num(cutoff["depth"]) is >= 20 and <= 40, cutoff != null ? $"depth {J.Num(cutoff["depth"])}" : "no modulation");
                    ck.Add("control_machine", s.Graph().Machines.Any(m => m.Gear == "Neo LFO"), "a Neo LFO in the song");
                    NotesKept(ck, s);
                    K.Sounds(ck, s);
                }),

            Recipe("safe-experiment", "Recipe: a safe experiment",
                "The song has a Neo Poly called Bass. Try raising its F1 cutoff to 3 kHz, in a way that lets you take the change back whole, and look at what changed. Then leave the song with the cutoff as it was, and write one line into the song's notes about what you tried.",
                BassSong,
                (s, c) =>
                {
                    K.Replay(s, "safe-experiment", withNeeds: false);
                    s.C.Ok("song_notes_write", new JsonObject { ["text"] = "Tried F1 cutoff at 3 kHz on Bass; put it back to the preset's value." });
                },
                (s, c, ck) =>
                {
                    var bass = K.Gear(s.Graph(), "Neo Poly", "Bass");
                    ck.Add("tried_noted", Regex.IsMatch(K.SongNotes(s), @"3\s*k\s*hz|3000", RegexOptions.IgnoreCase), "the song's notes name the 3 kHz that was tried");
                    K.Value(ck, "cutoff_restored", s, bass?.Name, "F1 cutoff", 401, 0.08, unit: " Hz (the preset's)");
                    NotesKept(ck, s);
                }),

            Recipe("arrange-a-section", "Recipe: arrange a section",
                "The song has a Neo Poly called Bass with a 4-beat pattern Line placed once. Arrange it: markers Intro at the start and Main at bar 3 (beat 8); Line repeated so it plays three times back to back (beats 0, 4 and 8); and inside the pattern an automation that opens the filter (F1 cutoff) from 300 Hz to 3 kHz across the 4 beats.",
                BassSong, (s, c) => K.Replay(s, "arrange-a-section", withNeeds: false),
                (s, c, ck) =>
                {
                    var markers = s.Markers();
                    ck.Add("markers", markers.Any(m => m.Name == "Intro" && m.At == 0) && markers.Any(m => m.Name == "Main" && Math.Abs(m.At - 8) < 0.01), string.Join(", ", markers.Select(m => $"{m.Name}@{m.At}")));
                    var lane = J.Objects(s.C.Ok("arrangement_get")["lanes"]).FirstOrDefault(l => J.Str(l["machine"]) == "Bass");
                    var ats = J.Objects(lane?["events"]).Where(e => J.Str(e["pattern"]) == "Line").Select(e => J.Num(e["at"])).OrderBy(x => x).ToList();
                    ck.Add("repeated", ats.SequenceEqual(new[] { 0.0, 4.0, 8.0 }), "Line at " + string.Join(", ", ats));
                    var auto = s.C.Call("automation_read", new JsonObject { ["machine"] = "Bass", ["pattern"] = "Line" });
                    var points = auto.IsError ? new List<JsonObject>() :
                        J.Objects(J.Objects(auto.Data["automation"]).FirstOrDefault(a => J.Str(a["param"]) == "F1 cutoff")?["points"]).ToList();
                    double first = points.Count > 0 ? Hz(J.Str(points[0]["value"])) : double.NaN, last = points.Count > 0 ? Hz(J.Str(points[^1]["value"])) : double.NaN;
                    ck.Add("sweep", first < 500 && last > 2000, $"cutoff from {J.F(first, 0)} Hz to {J.F(last, 0)} Hz");
                    K.Sounds(ck, s);
                }),

            Recipe("two-scenes", "Recipe: two scenes",
                "The song has a Neo Poly called Bass with a pattern Line. Set up the Session View for playing live: add a Neo Drums called Drums with a 4-beat pattern Beat (a kick, note C2, at beats 0 and 2). Scene 1 is named Intro and holds Bass's Line; scene 2 is named Groove and holds Drums' Beat; both clips launch quantized to the bar.",
                BassSong, (s, c) => K.Replay(s, "two-scenes", skipLast: 2, withNeeds: false),
                (s, c, ck) =>
                {
                    var grid = s.C.Ok("session_get");
                    var scenes = J.Objects(grid["scenes"]).ToList();
                    ck.Add("scene_names", scenes.Count >= 2 && J.Str(scenes[0]["name"]) == "Intro" && J.Str(scenes[1]["name"]) == "Groove", string.Join(", ", scenes.Take(2).Select(x => J.Str(x["name"]))));
                    string Slot(string machine, int scene) =>
                        (J.Objects(grid["tracks"]).FirstOrDefault(t => J.Str(t["machine"]) == machine)?["slots"] as JsonArray)?.ElementAtOrDefault(scene - 1) is JsonNode n ? J.Str(n) : null;
                    var drums = K.Gear(s.Graph(), "Neo Drums", "Drums")?.Name;
                    ck.Add("clip_intro", Slot("Bass", 1) == "Line", $"scene 1 holds {Slot("Bass", 1) ?? "nothing"} for Bass");
                    string beat = drums == null ? null : Slot(drums, 2);
                    ck.Add("clip_groove", beat != null, $"scene 2 holds {beat ?? "nothing"} for the drums");
                    var notes = drums != null && beat != null && s.Notes().TryGetValue(drums, out var p) && p.TryGetValue(beat, out var ns) ? ns : new List<JsonObject>();
                    ck.Add("kick_notes", notes.Any(n => J.Num(n["start"]) == 0) && notes.Any(n => J.Num(n["start"]) == 2) && notes.All(n => (int)J.Num(n["pitch"]) == K.Pitch("C2")), $"{notes.Count} notes");
                    var quant = J.Objects(grid["tracks"]).SelectMany(t => J.Objects(t["clip_settings"])).ToList();
                    ck.Add("bar_quantized", quant.Count >= 2 && quant.All(q => J.Str(q["quantization"]) == "Bar"), string.Join(", ", quant.Select(q => $"{J.Str(q["pattern"])} {J.Str(q["quantization"])}")));
                }),

            Recipe("controller-for-the-cutoff", "Recipe: a controller for the cutoff",
                "The song has a Neo Poly called Bass. Let a hardware knob move its filter cutoff: bind MIDI controller 74 on channel 1 to F1 cutoff, and keep the binding in the song.",
                BassSong, (s, c) => K.Replay(s, "controller-for-the-cutoff", skipLast: 2, withNeeds: false),
                (s, c, ck) =>
                {
                    var list = J.Objects(s.C.Ok("midi_bindings_list")["bindings"]).ToList();
                    var b = list.FirstOrDefault(x => string.Equals(J.Str(x["parameter"]), "F1 cutoff", StringComparison.OrdinalIgnoreCase));
                    ck.Add("bound", b != null, b != null ? "F1 cutoff is bound" : $"{list.Count} bindings, none on F1 cutoff");
                    ck.Add("controller_74", b != null && J.Num(b["controller"]) == 74, b != null ? $"controller {J.Num(b["controller"])}" : "no binding");
                    ck.Add("channel_1", b != null && J.Num(b["channel"]) == 1, b != null ? $"channel {J.Num(b["channel"])}" : "no binding");
                    NotesKept(ck, s);
                }),

            Recipe("explain-a-control", "Recipe: explain a control",
                "Add a Neo Learn FM called Teacher with its factory preset \"Keys — Glass Bell\". A beginner asks what its FM Depth control does. Set FM Depth to 60 percent and write the explanation into the song's notes, taken from what the machine itself says about the control: what it does and what you will hear.",
                null,
                (s, c) =>
                {
                    K.Replay(s, "explain-a-control");
                    var p = J.Objects(s.C.Ok("machine_describe", new JsonObject { ["machine"] = "Teacher" })["parameters"]).First(x => J.Str(x["name"]) == "FM Depth");
                    s.C.Ok("song_notes_write", new JsonObject { ["text"] = $"FM Depth: {J.Str(p["how"])} {J.Str(p["sound"])}" });
                },
                (s, c, ck) =>
                {
                    var teacher = K.Gear(s.Graph(), "Neo Learn FM", "Teacher");
                    ck.Add("machine", teacher != null, teacher?.Name ?? "no Neo Learn FM");
                    K.Value(ck, "depth", s, teacher?.Name, "FM Depth", 60, 0.02, 0.5, " %");
                    string text = "";
                    if (teacher != null)
                    {
                        var p = J.Objects(s.C.Ok("machine_describe", new JsonObject { ["machine"] = teacher.Name })["parameters"]).FirstOrDefault(x => J.Str(x["name"]) == "FM Depth");
                        text = $"{J.Str(p?["how"])} {J.Str(p?["sound"])}";
                    }
                    double overlap = K.WordOverlap(text, K.SongNotes(s));
                    ck.Add("explained", EvalTask.Linear(overlap, 0.5, 0.15), $"{overlap:P0} of the control's own words are in the song's notes");
                }),

            Recipe("engine-swap", "Recipe: swap an engine",
                "Add a Dye Essence called Dye. Make its slot A a struck bar: engine Modal, geometry Bar, exciter Struck, force 60 percent. Play A3 for two beats from the start of the song, and confirm it is not silent.",
                null, (s, c) => K.Replay(s, "engine-swap"),
                (s, c, ck) =>
                {
                    var dye = K.Gear(s.Graph(), "Dye Essence", "Dye")?.Name;
                    K.Choice(ck, "engine", s, dye, "A engine", "Modal");
                    K.Choice(ck, "geometry", s, dye, "A geometry", "Bar");
                    K.Choice(ck, "exciter", s, dye, "A exciter", "Struck");
                    K.Value(ck, "force", s, dye, "A force", 60, 0.02, 0.5, " %");
                    ck.Add("note_a3", K.Placed(s, dye).Any(n => n.Pitch == K.Pitch("A3")), string.Join(" ", K.Placed(s, dye).Select(n => $"{n.Pitch}@{n.Start}")));
                    K.Sounds(ck, s);
                }),

            Recipe("replace-a-classic", "Recipe: replace a classic",
                "The song has a Neo Poly called Bass and, after it, a classic Pedal Plate reverb called Room. Move Room onto its Neo successor: look at what the replacement would do first, then do it with the before-and-after comparison, and keep the new machine in the song in the same place in the chain.",
                (s, c) =>
                {
                    Need(s, "Pedal Plate");
                    K.Replay(s, "hear-a-bass");
                    Recipes.Add(s, "Pedal Plate", "Room", 0, insertAfter: "Bass");
                },
                (s, c) =>
                {
                    s.C.Ok("machine_replace", new JsonObject { ["machine"] = "Room", ["dry_run"] = true });
                    s.C.Ok("machine_replace", new JsonObject { ["machine"] = "Room", ["with"] = "Neo Verb", ["from"] = 0, ["to"] = 4 });
                },
                (s, c, ck) =>
                {
                    var g = s.Graph();
                    var room = g.Find("Room");
                    ck.Add("replaced", room?.Gear == "Neo Verb", room?.Gear ?? "no Room");
                    ck.Add("no_classic", !g.Machines.Any(m => m.Gear == "Pedal Plate"), "no Pedal Plate left");
                    ck.Add("same_place", room != null && room.Inputs.Any(i => i.From == "Bass") && room.Outputs.Any(o => o.To == "Master"), room == null ? "no Room" : $"in from {string.Join(",", room.Inputs.Select(i => i.From))}, out to {string.Join(",", room.Outputs.Select(o => o.To))}");
                    K.Sounds(ck, s);
                }),

            Recipe("check-the-load", "Recipe: check the load",
                "The song (a Neo Poly bass, a Dye Essence called Pad and a Neo Verb) feels heavy to play. Find out which machine costs the most CPU per second of audio, using a render's CPU figures, and write its name and its cost in milliseconds per second of audio into the song's notes. Do not remove anything.",
                (s, c) =>
                {
                    K.Replay(s, "hear-a-bass");
                    Recipes.Add(s, "Dye Essence", "Pad", 0);
                    Recipes.Add(s, "Neo Verb", "Space", 0, insertAfter: "Bass");
                },
                (s, c) =>
                {
                    var cpu = J.Objects(s.C.Ok("render", new JsonObject { ["from"] = 0, ["to"] = 4, ["cpu"] = true })["cpu"]).OrderByDescending(x => J.Num(x["ms_per_second"])).First();
                    s.C.Ok("song_notes_write", new JsonObject { ["text"] = $"Heaviest: {J.Str(cpu["machine"])}, {J.Num(cpu["ms_per_second"]):F1} ms per second of audio." });
                },
                (s, c, ck) =>
                {
                    var cpu = J.Objects(s.C.Ok("render", new JsonObject { ["from"] = 0, ["to"] = 4, ["cpu"] = true })["cpu"]).OrderByDescending(x => J.Num(x["ms_per_second"])).ToList();
                    // Pad and Space both cost far more than Bass, and which of the two is on top varies from run to run:
                    // naming any machine within half of the top cost is the right answer.
                    double top = J.Num(cpu[0]["ms_per_second"]);
                    string notes = K.SongNotes(s);
                    var named = cpu.Where(x => J.Num(x["ms_per_second"]) >= top / 2 && notes.Contains(J.Str(x["machine"]), StringComparison.OrdinalIgnoreCase)).ToList();
                    ck.Add("heaviest_named", named.Count > 0, $"the heaviest is {J.Str(cpu[0]["machine"])} ({J.F(top, 1)} ms/s); the notes name {(named.Count > 0 ? J.Str(named[0]["machine"]) : "none of the top ones")}");
                    // The figure varies with the host's load: anything within a factor of 4 is the right order of magnitude.
                    var numbers = Regex.Matches(notes, @"\d+(\.\d+)?").Select(m => double.Parse(m.Value, J.C)).ToList();
                    double ms = named.Count > 0 ? J.Num(named[0]["ms_per_second"]) : top;
                    ck.Add("figure", numbers.Any(n => n >= ms / 4 && n <= ms * 4), $"numbers in the notes: {string.Join(", ", numbers)} (the machine costs {J.F(ms, 1)})");
                    ck.Add("nothing_removed", s.Graph().Machines.Count(m => m.Kind != "master") >= 3, $"{s.Graph().Machines.Count - 1} machines");
                }),

            // ======== guides ===========================================================================
            Guide("app-control", "Guide: app control",
                "The song has a bass line. Export the whole song as a 24-bit WAV file to \"{dir}/bass.wav\" (no file of that name exists yet; do not overwrite anything), then write the exported file's sample rate and bit depth into the song's notes.",
                (s, c) =>
                {
                    K.Replay(s, "hear-a-bass");
                    File.Delete(c.File("bass.wav"));
                },
                (s, c) =>
                {
                    var r = s.C.Ok("export_audio", new JsonObject { ["path"] = c.File("bass.wav"), ["bits"] = 24 });
                    s.C.Ok("song_notes_write", new JsonObject { ["text"] = $"Exported bass.wav: {J.Num(r["sample_rate"])} Hz, {J.Num(r["bits"])} bit." });
                },
                (s, c, ck) =>
                {
                    string file = c.File("bass.wav");
                    ck.Add("file", File.Exists(file), file);
                    int bits = 0;
                    double rate = 0;
                    if (File.Exists(file))
                    {
                        var head = new byte[48];
                        using (var f = File.OpenRead(file)) f.Read(head, 0, head.Length);
                        bits = BitConverter.ToUInt16(head, 34);
                        rate = BitConverter.ToUInt32(head, 24);
                    }
                    ck.Add("bit_depth", bits == 24, $"{bits} bit");
                    ck.Add("audible", File.Exists(file) && !Meters.Silent(Meters.Read(file)), "the exported file has sound");
                    string notes = K.SongNotes(s);
                    ck.Add("noted", Regex.IsMatch(notes, @"\b24\b") && Regex.IsMatch(notes, @"44[.,]?100|44\.1"), notes.Length == 0 ? "no notes" : notes);
                }),

            Guide("build-a-synth", "Guide: build a synth",
                "The song has a Neo Patch called Patch holding the \"start\" template, which is silent on purpose, and a pattern Hold that plays C3 for four beats from the start. Make Patch sound as a basic voice: the key's pitch tunes the oscillator, the oscillator goes through the amplifier (VCA) to the output. Then make the oscillator a square wave.",
                (s, c) =>
                {
                    s.C.Ok("song_set", new JsonObject { ["tempo_bpm"] = 120 });
                    Recipes.Add(s, "Neo Patch", "Patch", 0);
                    s.C.Ok("patch_load_template", new JsonObject { ["template"] = "start" });
                    Pattern(s, "Patch", "Hold", 4, "C3", 4);
                },
                (s, c) =>
                {
                    var nodes = J.Objects(s.C.Ok("patch_describe")["nodes"]).ToList();
                    int Id(string kind) => (int)J.Num(nodes.First(n => J.Str(n["kind"]) == kind)["id"]);
                    s.C.Ok("patch_connect", new JsonObject { ["from_node"] = Id("Keys"), ["to_node"] = Id("Osc"), ["to_port"] = "Pitch" });
                    s.C.Ok("patch_connect", new JsonObject { ["from_node"] = Id("Osc"), ["to_node"] = Id("VCA"), ["to_port"] = "In" });
                    s.C.Ok("patch_connect", new JsonObject { ["from_node"] = Id("VCA"), ["to_node"] = Id("Out"), ["to_port"] = "In 1" });
                    s.C.Ok("patch_set", new JsonObject { ["node"] = Id("Osc"), ["control"] = "Shape", ["value"] = "square" });
                },
                (s, c, ck) =>
                {
                    var d = s.C.Call("patch_describe");
                    var nodes = d.IsError ? new List<JsonObject>() : J.Objects(d.Data["nodes"]).ToList();
                    var osc = nodes.FirstOrDefault(n => J.Str(n["kind"]) == "Osc");
                    var shape = J.Objects(osc?["controls"]).FirstOrDefault(x => J.Str(x["name"]) == "Shape");
                    ck.Add("square", J.Str(shape?["shown"]) == "square", $"Osc Shape shows {J.Str(shape?["shown"]) ?? "n/a"}");
                    int heard = nodes.Count(n => J.Bool(n["heard"]));
                    ck.Add("wired", nodes.Count > 0 && nodes.All(n => J.Bool(n["heard"]) || J.Str(n["kind"]) is "Envelope" or "Keys") && heard >= 3, $"{heard} of {nodes.Count} nodes reach the output");
                    K.Sounds(ck, s);
                }),

            Guide("concepts", "Guide: concepts",
                "The song is not empty. Write into the song's notes the facts a newcomer needs: the tempo in BPM, the ticks per beat, the beats per bar, and the name of every machine in the song except Master.",
                (s, c) =>
                {
                    s.C.Ok("song_set", new JsonObject { ["tempo_bpm"] = 97, ["ticks_per_beat"] = 8 });
                    Recipes.Add(s, "Neo Poly", "Bass", 0);
                    Recipes.Add(s, "Neo Drums", "Drums", 0);
                    Recipes.Add(s, "Neo Verb", "Space", 0, insertAfter: "Bass");
                },
                (s, c) =>
                {
                    var st = s.Status();
                    var names = s.Graph().Machines.Where(m => !m.IsMaster).Select(m => m.Name);
                    s.C.Ok("song_notes_write", new JsonObject { ["text"] = $"Tempo {J.Num(st["tempo_bpm"])} BPM, {J.Num(st["ticks_per_beat"])} ticks per beat, {J.Num(st["beats_per_bar"])} beats per bar. Machines: {string.Join(", ", names)}." });
                },
                (s, c, ck) =>
                {
                    string notes = K.SongNotes(s);
                    ck.Add("tempo", Regex.IsMatch(notes, @"\b97\b"), "97 BPM in the notes");
                    ck.Add("ticks", Regex.IsMatch(notes, @"\b8\b"), "8 ticks per beat in the notes");
                    ck.Add("beats_per_bar", Regex.IsMatch(notes, @"\b4\b"), "4 beats per bar in the notes");
                    var names = s.Graph().Machines.Where(m => !m.IsMaster).Select(m => m.Name).ToList();
                    ck.Add("machines", names.Count > 0 ? names.Count(n => notes.Contains(n, StringComparison.OrdinalIgnoreCase)) / (double)names.Count : 0, $"{names.Count(n => notes.Contains(n, StringComparison.OrdinalIgnoreCase))} of {names.Count}: {string.Join(", ", names)}");
                }),

            Guide("dye-essence-analysis", "Guide: Dye Essence analysis engines",
                "Add a Dye Essence called Dye. Slot A should use the engine that rebuilds a sound from partials taken from a sample: the Harmonic engine, with its partials import switched to take them from a sample, and the imported ratios at 40 percent.",
                null,
                (s, c) =>
                {
                    Recipes.Add(s, "Dye Essence", "Dye", 0);
                    Recipes.Set(s, "Dye", ("A engine", "Harmonic"), ("A import partials", "Sample"), ("A import ratios", "40%"));
                },
                (s, c, ck) =>
                {
                    var dye = K.Gear(s.Graph(), "Dye Essence", "Dye")?.Name;
                    K.Choice(ck, "engine", s, dye, "A engine", "Harmonic");
                    K.Choice(ck, "import_partials", s, dye, "A import partials", "Sample");
                    K.Value(ck, "import_ratios", s, dye, "A import ratios", 40, 0.02, 0.5, " %");
                }),

            Guide("dye-essence-effects", "Guide: Dye Essence effects rack",
                "Add a Dye Essence called Dye and give it a reverb in the first slot of its effects rack: type Verb, mixed in at 30 percent, with a decay of 60 percent, on effects bus A with the bus return at -6 dB.",
                null,
                (s, c) =>
                {
                    Recipes.Add(s, "Dye Essence", "Dye", 0);
                    Recipes.Set(s, "Dye", ("FX1 type", "Verb"), ("FX1 mix", "30%"), ("FX1 p4", "60%"), ("FX return A", "-6 dB"));
                },
                (s, c, ck) =>
                {
                    var dye = K.Gear(s.Graph(), "Dye Essence", "Dye")?.Name;
                    K.Choice(ck, "type", s, dye, "FX1 type", "Verb");
                    K.Value(ck, "mix", s, dye, "FX1 mix", 30, 0.02, 0.5, " %");
                    K.Value(ck, "decay", s, dye, "FX1 p4", 60, 0.02, 0.5, " %");
                    K.Value(ck, "return", s, dye, "FX return A", -6, 0.05, 0.3, " dB");
                }),

            Guide("dye-essence-in-depth", "Guide: Dye Essence in depth",
                "Add a Dye Essence called Dye that layers three engines: slot A Analog, slot B Modal, slot C Harmonic. Play A3 for two beats from the start of the song, and keep the true peak of the whole song below -1 dBTP.",
                null,
                (s, c) =>
                {
                    Recipes.Add(s, "Dye Essence", "Dye", 0);
                    Recipes.Set(s, "Dye", ("A engine", "Analog"), ("B engine", "Modal"), ("C engine", "Harmonic"), ("Volume", "-9 dB"));
                    Pattern(s, "Dye", "Hit", 4, "A3");
                },
                (s, c, ck) =>
                {
                    var dye = K.Gear(s.Graph(), "Dye Essence", "Dye")?.Name;
                    K.Choice(ck, "slot_a", s, dye, "A engine", "Analog");
                    K.Choice(ck, "slot_b", s, dye, "B engine", "Modal");
                    K.Choice(ck, "slot_c", s, dye, "C engine", "Harmonic");
                    ck.Add("note_a3", K.Placed(s, dye).Any(n => n.Pitch == K.Pitch("A3")), "A3 placed");
                    K.Sounds(ck, s, -1);
                }),

            Guide("dye-sound-design", "Guide: Dye Essence sound design",
                "Add a Dye Essence called Dye and start a marimba from its factory preset \"Keys — Glass Marimba\". Play A3 for two beats from the start of the song; the whole song's true peak must stay below -1 dBTP.",
                null,
                (s, c) =>
                {
                    Recipes.Add(s, "Dye Essence", "Dye", 0);
                    s.C.Ok("preset_load", new JsonObject { ["machine"] = "Dye", ["preset"] = "Keys — Glass Marimba" });
                    Pattern(s, "Dye", "Hit", 4, "A3");
                },
                (s, c, ck) =>
                {
                    var dye = K.Gear(s.Graph(), "Dye Essence", "Dye")?.Name;
                    K.Choice(ck, "preset_engine", s, dye, "A engine", "Modal");
                    K.Choice(ck, "preset_geometry", s, dye, "A geometry", "Bar");
                    ck.Add("note_a3", K.Placed(s, dye).Any(n => n.Pitch == K.Pitch("A3")), "A3 placed");
                    K.Sounds(ck, s, -1);
                }),

            Guide("fm-recipes", "Guide: FM recipes",
                "Make a two-operator FM bell on a Neo FM6 called Bell, starting from its factory preset \"Init — Sine\": operator 2 (the modulator) at coarse ratio 3 and fine +50 (ratio 3.5), level 72; operator 1 (the carrier) with its fourth envelope rate at 30 so it rings on after release. Play A4 for two beats from the start of the song.",
                null,
                (s, c) =>
                {
                    Recipes.Add(s, "Neo FM6", "Bell", 0);
                    s.C.Ok("preset_load", new JsonObject { ["machine"] = "Bell", ["preset"] = "Init — Sine" });
                    Recipes.Set(s, "Bell", ("Op2 coarse", 3), ("Op2 fine", 50), ("Op2 level", 72), ("Op1 rate 4", 30));
                    Pattern(s, "Bell", "Strike", 4, "A4");
                },
                (s, c, ck) =>
                {
                    var bell = K.Gear(s.Graph(), "Neo FM6", "Bell")?.Name;
                    K.Value(ck, "ratio_coarse", s, bell, "Op2 coarse", 3, 0, 0.01);
                    K.Value(ck, "ratio_fine", s, bell, "Op2 fine", 50, 0, 0.01);
                    K.Value(ck, "modulator_level", s, bell, "Op2 level", 72, 0, 0.01);
                    K.Value(ck, "carrier_rings", s, bell, "Op1 rate 4", 30, 0, 0.01);
                    ck.Add("note_a4", K.Placed(s, bell).Any(n => n.Pitch == K.Pitch("A4")), "A4 placed");
                    K.Sounds(ck, s);
                }),

            Guide("live-performance", "Guide: live performance",
                "The song has a Neo Poly called Bass with a pattern Line and a Neo Drums called Drums with a pattern Beat. Prepare the Session View for a live set: scene 1 named Verse holds Bass's Line; scene 2 named Chorus holds Drums' Beat, and that drum clip is a Toggle (the same launch starts and stops it) quantized to the bar. Bind MIDI controller 21 on channel 2 to Bass's F1 cutoff.",
                (s, c) =>
                {
                    K.Replay(s, "hear-a-bass");
                    Recipes.Add(s, "Neo Drums", "Drums", 0);
                    Recipes.Pattern(s, "Drums", "Beat", 4, new[] { J.Note("C2", 0, 0.25, 100), J.Note("C2", 2, 0.25, 100) });
                },
                (s, c) =>
                {
                    s.C.Ok("session_scene_set", new JsonObject { ["scene"] = 1, ["name"] = "Verse" });
                    s.C.Ok("session_scene_set", new JsonObject { ["scene"] = 2, ["name"] = "Chorus" });
                    s.C.Ok("session_clip_set", new JsonObject { ["machine"] = "Bass", ["scene"] = 1, ["pattern"] = "Line" });
                    s.C.Ok("session_clip_set", new JsonObject { ["machine"] = "Drums", ["scene"] = 2, ["pattern"] = "Beat", ["mode"] = "Toggle", ["quantization"] = "Bar" });
                    s.C.Ok("midi_bind", new JsonObject { ["machine"] = "Bass", ["parameter"] = "F1 cutoff", ["channel"] = 2, ["controller"] = 21 });
                },
                (s, c, ck) =>
                {
                    var grid = s.C.Ok("session_get");
                    var scenes = J.Objects(grid["scenes"]).ToList();
                    ck.Add("scene_names", scenes.Count >= 2 && J.Str(scenes[0]["name"]) == "Verse" && J.Str(scenes[1]["name"]) == "Chorus", string.Join(", ", scenes.Take(2).Select(x => J.Str(x["name"]))));
                    var tracks = J.Objects(grid["tracks"]).ToList();
                    string Slot(string machine, int scene) => (tracks.FirstOrDefault(t => J.Str(t["machine"]) == machine)?["slots"] as JsonArray)?.ElementAtOrDefault(scene - 1) is JsonNode n ? J.Str(n) : null;
                    ck.Add("clips", Slot("Bass", 1) == "Line" && Slot("Drums", 2) == "Beat", $"scene 1: {Slot("Bass", 1)}, scene 2: {Slot("Drums", 2)}");
                    var drums = J.Objects(tracks.FirstOrDefault(t => J.Str(t["machine"]) == "Drums")?["clip_settings"]).FirstOrDefault(x => J.Num(x["scene"]) == 2);
                    ck.Add("toggle", J.Str(drums?["mode"]) == "Toggle", $"drum clip mode {J.Str(drums?["mode"]) ?? "n/a"}");
                    ck.Add("bar", J.Str(drums?["quantization"]) == "Bar", $"drum clip quantization {J.Str(drums?["quantization"]) ?? "n/a"}");
                    var b = J.Objects(s.C.Ok("midi_bindings_list")["bindings"]).FirstOrDefault(x => string.Equals(J.Str(x["parameter"]), "F1 cutoff", StringComparison.OrdinalIgnoreCase));
                    ck.Add("controller", b != null && J.Num(b["controller"]) == 21 && J.Num(b["channel"]) == 2, b != null ? $"controller {J.Num(b["controller"])} on channel {J.Num(b["channel"])}" : "no binding");
                }),

            Guide("migrating-songs", "Guide: migrating songs",
                "An old song: a Neo Poly called Bass feeds a classic Pedal Comp called Squash, which feeds a classic Pedal Plate reverb called Room. Move every classic machine onto its Neo successor, one at a time, looking at each move first, so the chain keeps its order and the song still sounds.",
                (s, c) =>
                {
                    Need(s, "Pedal Comp", "Pedal Plate");
                    K.Replay(s, "hear-a-bass");
                    Recipes.Add(s, "Pedal Comp", "Squash", 0, insertAfter: "Bass");
                    Recipes.Add(s, "Pedal Plate", "Room", 0, insertAfter: "Squash");
                },
                (s, c) =>
                {
                    foreach (var m in new[] { "Squash", "Room" })
                    {
                        s.C.Ok("machine_replace", new JsonObject { ["machine"] = m, ["dry_run"] = true });
                        s.C.Ok("machine_replace", new JsonObject { ["machine"] = m, ["from"] = 0, ["to"] = 4 });
                    }
                },
                (s, c, ck) =>
                {
                    var g = s.Graph();
                    ck.Add("no_classics", !g.Machines.Any(m => m.Gear.StartsWith("Pedal ")), string.Join(", ", g.Machines.Select(m => m.Gear)));
                    var comp = g.Machines.FirstOrDefault(m => m.Gear == "Neo Comp");
                    var verb = g.Machines.FirstOrDefault(m => m.Gear == "Neo Verb");
                    ck.Add("successors", comp != null && verb != null, $"Neo Comp {(comp != null ? "yes" : "no")}, Neo Verb {(verb != null ? "yes" : "no")}");
                    ck.Add("order", comp != null && verb != null && comp.Inputs.Any(i => i.From == "Bass") && verb.Inputs.Any(i => i.From == comp.Name) && verb.Outputs.Any(o => o.To == "Master"), "Bass → comp → verb → Master");
                    K.Sounds(ck, s);
                }),

            Guide("mixing-dynamics", "Guide: mixing dynamics",
                "The song has a Neo Poly called Bass. Tame it with a Neo Comp called Squash placed right after it, with the settings for an aggressive bass: FET character, ratio 8:1, attack 1 ms, release 150 ms, knee 4 dB.",
                BassSong,
                (s, c) =>
                {
                    Recipes.Add(s, "Neo Comp", "Squash", 0, insertAfter: "Bass");
                    Recipes.Set(s, "Squash", ("Mode", "FET"), ("Ratio", "8:1"), ("Attack", "1 ms"), ("Release", "150 ms"), ("Knee", "4 dB"));
                },
                (s, c, ck) =>
                {
                    var g = s.Graph();
                    var comp = K.Gear(g, "Neo Comp", "Squash");
                    ck.Add("after_bass", comp != null && comp.Inputs.Any(i => i.From == "Bass"), comp == null ? "no Neo Comp" : $"fed by {string.Join(",", comp.Inputs.Select(i => i.From))}");
                    K.Choice(ck, "mode", s, comp?.Name, "Mode", "FET");
                    K.Value(ck, "ratio", s, comp?.Name, "Ratio", 8, 0.03, unit: ":1");
                    K.Value(ck, "attack", s, comp?.Name, "Attack", 0.001, 0.1, unit: " s");
                    K.Value(ck, "release", s, comp?.Name, "Release", 0.150, 0.05, unit: " s");
                    K.Value(ck, "knee", s, comp?.Name, "Knee", 4, 0.03, 0.2, " dB");
                }),

            Guide("neo-eq-in-depth", "Guide: Neo EQ in depth",
                "The song has a Neo Poly called Bass. Add a Neo EQ called Shape right after it, as a graphic equaliser with the factory preset that scoops the mids, and keep it in minimum-phase mode so it adds no latency.",
                BassSong,
                (s, c) =>
                {
                    Recipes.Add(s, "Neo EQ", "Shape", 0, insertAfter: "Bass");
                    s.C.Ok("preset_load", new JsonObject { ["machine"] = "Shape", ["preset"] = "Graphic — Mid Scoop" });
                },
                (s, c, ck) =>
                {
                    var eq = K.Gear(s.Graph(), "Neo EQ", "Shape");
                    ck.Add("after_bass", eq != null && eq.Inputs.Any(i => i.From == "Bass"), eq == null ? "no Neo EQ" : $"fed by {string.Join(",", eq.Inputs.Select(i => i.From))}");
                    K.Choice(ck, "graphic", s, eq?.Name, "Mode", "Graphic");
                    K.Value(ck, "scoop", s, eq?.Name, "G 630 Hz", -5, 0, 0.3, " dB");
                    K.Choice(ck, "minimum_phase", s, eq?.Name, "Phase", "Minimum");
                    K.Sounds(ck, s);
                }),

            Guide("performance", "Guide: performance and latency",
                "The song (a Neo Poly bass, a Dye Essence called Pad, a Neo Verb) is too heavy for a small laptop. Using a render's CPU figures, find the machine that costs by far the most and remove it, leaving the rest playing. Write into the song's notes the name of the setting that sets how many threads the audio engine renders machines on.",
                (s, c) =>
                {
                    K.Replay(s, "hear-a-bass");
                    Recipes.Add(s, "Dye Essence", "Pad", 0);
                    Recipes.Add(s, "Neo Verb", "Space", 0, insertAfter: "Bass");
                },
                (s, c) =>
                {
                    var heaviest = J.Objects(s.C.Ok("render", new JsonObject { ["from"] = 0, ["to"] = 4, ["cpu"] = true })["cpu"]).OrderByDescending(x => J.Num(x["ms_per_second"])).First();
                    s.C.Ok("machine_remove", new JsonObject { ["machines"] = J.A(J.Str(heaviest["machine"])) });
                    var key = J.Str(J.Objects(s.C.Ok("settings_get", new JsonObject { ["search"] = "thread" })["settings"]).First()["key"]);
                    s.C.Ok("song_notes_write", new JsonObject { ["text"] = $"The audio thread count is {key}." });
                },
                (s, c, ck) =>
                {
                    var g = s.Graph();
                    // Pad and Space both cost far more than Bass and swap places from run to run: removing exactly one of them, keeping Bass, is the answer.
                    int heavy = g.Machines.Count(m => m.Gear is "Dye Essence" or "Neo Verb");
                    ck.Add("heaviest_removed", heavy == 1, $"{heavy} of the two heavy machines (Dye Essence, Neo Verb) are left");
                    ck.Add("rest_kept", K.Gear(g, "Neo Poly") != null && g.Machines.Count(m => !m.IsMaster) == 2, string.Join(", ", g.Machines.Where(m => !m.IsMaster).Select(m => m.Gear)));
                    ck.Add("thread_setting", K.SongNotes(s).Contains("AudioThreads", StringComparison.OrdinalIgnoreCase), K.SongNotes(s));
                    K.Sounds(ck, s);
                }),

            Guide("synth-recipes", "Guide: synth recipes",
                "Make a sub bass on a Neo Poly called Sub: one voice at a time (mono), a pure sine for oscillator 1, a state-variable low-pass filter (SVF LP) at 2 kHz with no envelope amount, a 3 ms attack, full sustain, a 40 ms release, no drift, volume -3 dB. Play E1 for two beats from the start of the song.",
                null,
                (s, c) =>
                {
                    Recipes.Add(s, "Neo Poly", "Sub", 0);
                    Recipes.Set(s, "Sub", ("Voice mode", "Mono"), ("O1 shape", 0), ("F1 type", "SVF LP"), ("F1 cutoff", "2 kHz"), ("F1 env", "0%"), ("F1 key track", "0%"),
                        ("Amp attack", "3 ms"), ("Amp sustain", "100%"), ("Amp release", "40 ms"), ("Drift", "0%"), ("Volume", "-3 dB"));
                    Pattern(s, "Sub", "Low", 4, "E1");
                },
                (s, c, ck) =>
                {
                    var sub = K.Gear(s.Graph(), "Neo Poly", "Sub")?.Name;
                    K.Choice(ck, "mono", s, sub, "Voice mode", "Mono");
                    K.Value(ck, "sine", s, sub, "O1 shape", 0, 0, 0.01);
                    K.Choice(ck, "filter_type", s, sub, "F1 type", "SVF LP");
                    K.Value(ck, "cutoff", s, sub, "F1 cutoff", 2000, 0.03, unit: " Hz");
                    K.Value(ck, "release", s, sub, "Amp release", 0.040, 0.1, unit: " s");
                    K.Value(ck, "volume", s, sub, "Volume", -3, 0, 0.3, " dB");
                    ck.Add("note_e1", K.Placed(s, sub).Any(n => n.Pitch == K.Pitch("E1")), "E1 placed");
                }),

            Guide("teaching", "Guide: teaching",
                "The song has a Neo Learn FM called Teacher with a pattern Demo (not placed in the arrangement). A beginner asks what the machine's first control does. Write the lesson into the song's notes, from what the machine itself says about that control (what it does and what you will hear). You may change the control and place the pattern to demonstrate, but leave the song as you found it: the control at its original value and nothing placed in the arrangement.",
                (s, c) =>
                {
                    Recipes.Add(s, "Neo Learn FM", "Teacher", 0);
                    s.C.Ok("preset_load", new JsonObject { ["machine"] = "Teacher", ["preset"] = "Keys — Glass Bell" });
                    Recipes.Pattern(s, "Teacher", "Demo", 4, new[] { J.Note("A4", 0, 2, 100) });
                },
                (s, c) =>
                {
                    var first = J.Objects(s.C.Ok("machine_describe", new JsonObject { ["machine"] = "Teacher" })["parameters"]).First();
                    string name = J.Str(first["name"]);
                    s.C.Ok("song_notes_write", new JsonObject { ["text"] = $"{name}: {J.Str(first["how"])} {J.Str(first["sound"])}" });
                    Recipes.Place(s, "Teacher", "Demo", 0);
                    Recipes.Set(s, "Teacher", (name, "4.00×"));
                    s.C.Ok("arrangement_clear", new JsonObject { ["from"] = 0, ["to"] = 16 });
                    Recipes.Set(s, "Teacher", (name, J.Str(first["default"])));
                },
                (s, c, ck) =>
                {
                    var teacher = K.Gear(s.Graph(), "Neo Learn FM", "Teacher");
                    string text = "", name = null;
                    double was = double.NaN;
                    if (teacher != null)
                    {
                        var first = J.Objects(s.C.Ok("machine_describe", new JsonObject { ["machine"] = teacher.Name })["parameters"]).First();
                        name = J.Str(first["name"]);
                        was = J.Num(first["default_number"]);
                        text = $"{J.Str(first["how"])} {J.Str(first["sound"])}";
                    }
                    double overlap = K.WordOverlap(text, K.SongNotes(s));
                    ck.Add("lesson", EvalTask.Linear(overlap, 0.5, 0.15), $"{overlap:P0} of the control's own words are in the song's notes");
                    var now = K.Param(s, teacher?.Name, name);
                    ck.Add("control_restored", !double.IsNaN(was) && Math.Abs(K.Num(now) - was) <= Math.Abs(was) * 0.02 + 0.005, $"{name} reads {K.Text(now)}, it started at {was}");
                    int events = J.Objects(s.C.Ok("arrangement_get")["lanes"]).Sum(l => J.Objects(l["events"]).Count());
                    ck.Add("nothing_placed", events == 0, $"{events} placements in the arrangement");
                    ck.Add("notes_longer_than_a_line", K.SongNotes(s).Length >= 40, $"{K.SongNotes(s).Length} characters");
                }),

            // ======== questions ========================================================================
            Ask("choose_reverb", "Question: choose a reverb",
                "The song has a Neo Poly called Bass. It needs a reverb, a space around it. Pick the machine this app offers for that and add it right after Bass.",
                BassSong,
                (s, c) => s.C.Ok("machine_add", new JsonObject { ["gear"] = "Neo Verb", ["name"] = "Space", ["insert_after"] = "Bass" }),
                (s, c, ck) => Chosen(s, ck, "reverb", "effect")),

            Ask("choose_compressor", "Question: choose a compressor",
                "The song has a Neo Poly called Bass whose level jumps around. Pick the machine this app offers to even out its level and add it right after Bass.",
                BassSong,
                (s, c) => s.C.Ok("machine_add", new JsonObject { ["gear"] = "Neo Comp", ["name"] = "Level", ["insert_after"] = "Bass" }),
                (s, c, ck) => Chosen(s, ck, "compressor", "effect")),

            Ask("choose_limiter", "Question: choose a limiter",
                "The song has a Neo Poly called Bass. The final mix must never go over its peak ceiling. Pick the machine this app offers for limiting the master and add it between Bass and Master.",
                BassSong,
                (s, c) => s.C.Ok("machine_add", new JsonObject { ["gear"] = "Neo Limiter", ["name"] = "Ceiling", ["insert_after"] = "Bass" }),
                (s, c, ck) => Chosen(s, ck, "limiter", "effect")),

            Ask("choose_bass_synth", "Question: choose a bass synth",
                "The song is empty. Pick a synth this app offers that is made for bass lines, add it, and make it play a low note (below C3) for two beats from the start of the song.",
                null,
                (s, c) =>
                {
                    // A competent agent asks the catalog: the first answer that is one of ours (it runs everywhere).
                    string pick = Found(s, "bass", "generator").OrderBy(g => g).First(g => g.StartsWith("Neo "));
                    Recipes.Add(s, pick, "Low", 0);
                    Pattern(s, "Low", "Note", 4, "C2");
                },
                (s, c, ck) =>
                {
                    var allowed = Found(s, "bass", "generator");
                    var g = s.Graph();
                    var pick = g.Generators.FirstOrDefault(m => allowed.Contains(m.Gear));
                    ck.Add("fits_the_job", pick != null, pick != null ? $"{pick.Gear} is among: {string.Join(", ", allowed)}" : $"generators {string.Join(", ", g.Generators.Select(m => m.Gear))}; the catalog says: {string.Join(", ", allowed)}");
                    ck.Add("low_note", pick != null && K.Placed(s, pick.Name).Any(n => n.Pitch < K.Pitch("C3")), pick == null ? "no pick" : string.Join(" ", K.Placed(s, pick.Name).Select(n => $"{n.Pitch}@{n.Start}")));
                    K.Sounds(ck, s);
                }),

            Ask("units_synth", "Question: set a synth in real units",
                "The song has a Neo Poly called Bass. Set its filter cutoff (F1 cutoff) to 2.5 kHz, its amplitude envelope release (Amp release) to 350 ms and its Volume to -6 dB.",
                (s, c) => Recipes.Add(s, "Neo Poly", "Bass", 0),
                (s, c) => Recipes.Set(s, "Bass", ("F1 cutoff", "2.5 kHz"), ("Amp release", "350 ms"), ("Volume", "-6 dB")),
                (s, c, ck) =>
                {
                    K.Value(ck, "cutoff", s, "Bass", "F1 cutoff", 2500, 0.03, unit: " Hz");
                    K.Value(ck, "release", s, "Bass", "Amp release", 0.350, 0.03, unit: " s");
                    K.Value(ck, "volume", s, "Bass", "Volume", -6, 0, 0.3, " dB");
                }),

            Ask("units_compressor", "Question: set a compressor in real units",
                "The song has a Neo Comp called Comp. Set its threshold to -18 dB, its ratio to 4:1, its attack to 10 ms and its release to 120 ms.",
                (s, c) => Recipes.Add(s, "Neo Comp", "Comp", 0),
                (s, c) => Recipes.Set(s, "Comp", ("Threshold", "-18 dB"), ("Ratio", "4:1"), ("Attack", "10 ms"), ("Release", "120 ms")),
                (s, c, ck) =>
                {
                    K.Value(ck, "threshold", s, "Comp", "Threshold", -18, 0, 0.3, " dB");
                    K.Value(ck, "ratio", s, "Comp", "Ratio", 4, 0.03, unit: ":1");
                    K.Value(ck, "attack", s, "Comp", "Attack", 0.010, 0.05, unit: " s");
                    K.Value(ck, "release", s, "Comp", "Release", 0.120, 0.05, unit: " s");
                }),

            Ask("units_song", "Question: set the song's timing",
                "Set the song's tempo to 93 BPM, its resolution to 8 ticks per beat, and make it loop over its first four bars (16 beats), with looping on.",
                null,
                (s, c) => s.C.Ok("song_set", new JsonObject { ["tempo_bpm"] = 93, ["ticks_per_beat"] = 8, ["loop_start_beats"] = 0, ["loop_end_beats"] = 16, ["looping"] = true }),
                (s, c, ck) =>
                {
                    var st = s.Status();
                    ck.Add("tempo", Math.Abs(J.Num(st["tempo_bpm"]) - 93) < 0.05, $"{J.Num(st["tempo_bpm"])} BPM");
                    ck.Add("ticks", J.Num(st["ticks_per_beat"]) == 8, $"{J.Num(st["ticks_per_beat"])} ticks per beat");
                    ck.Add("loop_length", J.Num(st["loop"]?["start_beats"]) == 0 && J.Num(st["loop"]?["end_beats"]) == 16, $"loop {J.Num(st["loop"]?["start_beats"])} to {J.Num(st["loop"]?["end_beats"])} beats");
                    ck.Add("loop_on", J.Bool(st["loop"]?["on"]), "looping " + (J.Bool(st["loop"]?["on"]) ? "on" : "off"));
                }),

            Ask("units_dye_effects", "Question: set an effect rack in real units",
                "The song has a Dye Essence called Dye. Give its effects rack's first slot a delay (type Delay) mixed in at 25 percent, with the effects bus A return at -9 dB.",
                (s, c) => Recipes.Add(s, "Dye Essence", "Dye", 0),
                (s, c) => Recipes.Set(s, "Dye", ("FX1 type", "Delay"), ("FX1 mix", "25%"), ("FX return A", "-9 dB")),
                (s, c, ck) =>
                {
                    K.Choice(ck, "type", s, "Dye", "FX1 type", "Delay");
                    K.Value(ck, "mix", s, "Dye", "FX1 mix", 25, 0.02, 0.5, " %");
                    K.Value(ck, "return", s, "Dye", "FX return A", -9, 0.05, 0.3, " dB");
                }),
        };

        /// <summary>A machine of the catalog's answer for a tag was added after Bass, between Bass and Master, and the song still sounds.</summary>
        static void Chosen(Studio s, Ck ck, string tag, string kind)
        {
            var allowed = Found(s, tag, kind);
            var g = s.Graph();
            var pick = g.Machines.FirstOrDefault(m => m.Kind == kind && allowed.Contains(m.Gear));
            ck.Add("fits_the_job", pick != null, pick != null ? $"{pick.Gear} is among: {string.Join(", ", allowed)}" : $"effects {string.Join(", ", Effects(g))}; the catalog says: {string.Join(", ", allowed)}");
            ck.Add("in_the_chain", pick != null && g.SourcesOf(pick).Contains("Bass") && pick.Outputs.Any(o => o.To == "Master"), pick == null ? "no pick" : $"{pick.Name}: sources {string.Join(",", g.SourcesOf(pick))}");
            K.Sounds(ck, s);
        }
    }
}
