// Agent server (docs/specs/neo-patch.md §4.6, phase NP3): the patch_* tools, which build and change the patch inside a Neo Patch machine: its nodes,
// their wires, a node's controls in real units, the macros, the templates of the "build-a-synth" guide, and saving the patch as a preset.
// The tools are portable: they check the arguments and hand each call to IPatchServices as an operation and a JSON object (ReBuzz/Agent/PatchBackend.cs
// finds the machine and calls it on the engine's queue). The machine does the work through the edit model its inner canvas uses, with the song's history
// behind it, so each editing call is one undo step on the song's stack, shared with the person's own edits at the canvas, and a refusal changes nothing.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.App
{
    /// <summary>What the patch tools need from the host: one operation on a Neo Patch machine (on the engine's queue), and saving its preset.</summary>
    public interface IPatchServices
    {
        /// <summary>
        /// Runs an operation on the Neo Patch machine named <paramref name="machine"/> (null: the only one in the song). <paramref name="changes"/> says whether it edits the song
        /// (one undo step for the call). Returns the machine's JSON result; throws <see cref="ToolError"/> with the machine's own words when it refuses (nothing changed).
        /// </summary>
        Task<JsonObject> Run(string tool, bool changes, string? machine, string op, JsonObject args, CancellationToken ct);

        /// <summary>Names the machine's patch (one undo step) and saves the machine's sound as a preset of Neo Patch, as preset_save does. Returns {machine, preset, file}.</summary>
        Task<JsonObject> SavePreset(string? machine, string name, string? comment, bool overwrite, CancellationToken ct);
    }

    public static class PatchTools
    {
        /// <summary>The templates patch_load_template loads, by name (the "build-a-synth" guide walks through them in this order).</summary>
        public static readonly string[] Templates =
        {
            "start", "subtractive", "fm-2op", "fm-4op", "am-ring", "wavefolder", "sync-lead", "plucked-string", "vector-crossfade", "sample-loop",
        };

        static JsonObject Loose(string? description, params (string Name, JsonObject Schema, bool Required)[] properties)
        {
            var o = Object(description, properties);
            o["additionalProperties"] = true;
            return o;
        }

        static JsonObject NumberOrText(string description) =>
            AnyOf(description, Number("A number in the control's own units"), String("A number with a unit (\"1.5 kHz\", \"250 ms\"), or the name of a choice"));

        static JsonObject PortRef(string description) =>
            AnyOf(description, Integer("The port's number (patch_describe lists them)", 0, 15), String("The port's name (\"Pitch\", \"In\", \"CV\")"));

        static (string, JsonObject, bool) MachineArg => Opt("machine", String("The Neo Patch machine (default: the only one in the song)"));

        static JsonObject Args(ToolContext c)
        {
            var a = (JsonObject)(c.Arguments.DeepClone());
            a.Remove("machine");
            return a;
        }

        public static void Register(ToolRegistry tools, IPatchServices patch)
        {
            tools.Add(new ToolDef
            {
                Name = "patch_describe",
                Title = "Describe a Neo Patch patch",
                Description =
                    "Reads the patch inside a Neo Patch machine: every node (id, kind, per voice or global, whether it is heard, its controls with real values and ranges, its input and output ports), " +
                    "the wires (node and port at each end, and the amount), the macros and what each moves, and the patch's CPU. Node ids are what the other patch_* tools take. " +
                    "catalog=true adds every node kind with its ports and controls. Example: patch_describe(machine=\"Lead\").",
                Input = Object(null, MachineArg, Opt("catalog", Boolean("Also list every node kind with its ports and controls"))),
                Output = Loose(null,
                    Req("name", String("The patch's name")), Req("node_count", Integer("Nodes in the patch")), Req("node_cap", Integer("The most nodes a patch can have")),
                    Req("wire_count", Integer("Wires")), Req("nodes", Array(new JsonObject(), "The nodes")), Req("wires", Array(new JsonObject(), "The wires")),
                    Req("macros", Array(new JsonObject(), "The macros that are mapped or named")), Req("cpu", new JsonObject { ["type"] = "object", ["description"] = "The patch's CPU, percent of one core" })),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await patch.Run("patch_describe", false, c.String("machine"), "describe", Args(c), c.Cancellation);
                    var nodes = ((JsonArray)r["nodes"]!).Select(n => $"{n!["kind"]} {n["id"]}").ToList();
                    int unheard = (r["unheard"] as JsonArray)?.Count ?? 0;
                    string text = $"{r["machine"]}: patch \"{r["name"]}\", {r["node_count"]} of {r["node_cap"]} nodes, {r["wire_count"]} wires, {((JsonArray)r["macros"]!).Count} macros in use, CPU {r["cpu"]!["percent"]}%." +
                        (nodes.Count == 0 ? " The patch is empty: patch_load_template(template=\"start\") or patch_add_node builds one." : " Nodes: " + string.Join(", ", nodes.Take(16)) + (nodes.Count > 16 ? ", …" : "") + ".") +
                        (unheard > 0 ? $" {unheard} node{(unheard == 1 ? " is" : "s are")} not heard (no path to an Out node)." : "");
                    return ToolResult.Ok(text, r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "patch_add_node",
                Title = "Add a node to a patch",
                Description =
                    "Adds a node to the patch inside a Neo Patch machine: Keys, Osc, Wavetable Osc, Noise, Sample, LFO, Envelope, Slew, Sample & Hold, Random, Filter, Shaper, Delay, Mixer, Math, Crossfade, " +
                    "Feedback, Macro, VCA, Out, Global Keys or Voice sum (the kind is matched by name, any case). controls sets controls in real units as it adds (Cutoff \"1.5 kHz\", Time \"250 ms\", Shape \"saw\"); " +
                    "global=true makes a per-voice node run once for all notes. Returns the node with its id, controls and ports. The node is not wired: patch_connect does that. One undo step. " +
                    "Example: patch_add_node(kind=\"Filter\", controls={\"Mode\": \"ladder 24\", \"Cutoff\": \"1.5 kHz\"}).",
                Input = Object(null,
                    Req("kind", String("The node kind: Keys, Osc, Wavetable Osc, Noise, Sample, LFO, Envelope, Slew, Sample & Hold, Random, Filter, Shaper, Delay, Mixer, Math, Crossfade, Feedback, Macro, VCA, Out, Global Keys, Voice sum")),
                    MachineArg,
                    Opt("controls", Map("Controls to set, by name (or number): each a number in real units, a number with a unit, or a choice's name", AnyOf("The value", Number("A number"), String("Text")))),
                    Opt("global", Boolean("Run once for all notes instead of once for each")),
                    Opt("x", Number("Where to draw it on the canvas (default: the next free place in a grid)")),
                    Opt("y", Number("Where to draw it on the canvas"))),
                Output = Loose(null, Req("id", Integer("The node's id")), Req("kind", String("The kind")), Req("controls", Array(new JsonObject(), "Its controls")), Req("node_count", Integer("Nodes in the patch now"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await patch.Run("patch_add_node", true, c.String("machine"), "add_node", Args(c), c.Cancellation);
                    return ToolResult.Ok($"Added {r["kind"]} {r["id"]} to {r["machine"]}'s patch ({r["node_count"]} nodes). Wire it with patch_connect.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "patch_connect",
                Title = "Wire two nodes",
                Description =
                    "Wires a node's output to another node's input (ports by name or number; patch_describe lists them; from_port defaults to 0). Any output can feed any input; amount scales the signal where it arrives " +
                    "(1: as it is, negative flips it; connecting an existing wire again with a new amount changes the amount). Several wires into one input add. A wire that would make a loop is refused: the error names the " +
                    "loop; route it through a Feedback node, which hands the signal back one block (64 samples) later. One undo step. " +
                    "Example: patch_connect(from_node=1, from_port=\"Pitch\", to_node=2, to_port=\"Pitch\").",
                Input = Object(null,
                    Req("from_node", Integer("The node the signal comes from (its id)", 1)),
                    Req("to_node", Integer("The node it goes to (its id)", 1)),
                    Req("to_port", PortRef("The input port of to_node")),
                    Opt("from_port", PortRef("The output port of from_node (default 0)")),
                    Opt("amount", Number("The wire's amount, -16 to 16 (default 1)", -16, 16)),
                    MachineArg),
                Output = Loose(null, Req("connected", String("What was wired")), Req("amount", Number("The wire's amount")), Req("wire_count", Integer("Wires in the patch now"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await patch.Run("patch_connect", true, c.String("machine"), "connect", Args(c), c.Cancellation);
                    bool same = r["already_connected"] is JsonValue v && v.TryGetValue(out bool b) && b;
                    return ToolResult.Ok(same ? $"{r["connected"]} was already wired." : $"Wired {r["connected"]} (amount {r["amount"]}); {r["wire_count"]} wires.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "patch_disconnect",
                Title = "Remove a wire",
                Description =
                    "Removes one wire (give both ends), or every wire of a node (node with all=true). The error lists the wires between the two nodes when the one you named is not there. One undo step. " +
                    "Example: patch_disconnect(from_node=3, to_node=4, to_port=\"Cutoff\").",
                Input = Object(null,
                    Opt("from_node", Integer("The node the wire comes from", 1)),
                    Opt("to_node", Integer("The node it goes to", 1)),
                    Opt("to_port", PortRef("The input port of to_node")),
                    Opt("from_port", PortRef("The output port of from_node (default 0)")),
                    Opt("node", Integer("With all=true: the node whose wires all go", 1)),
                    Opt("all", Boolean("Remove every wire of node")),
                    MachineArg),
                Output = Loose(null, Req("removed", Integer("Wires removed")), Req("wire_count", Integer("Wires left"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    bool all = c.Bool("all") == true;
                    if (all && c.Arguments["node"] == null && c.Arguments["from_node"] == null) throw new ToolError("all=true needs node: the node whose wires all go.");
                    if (!all && (c.Arguments["from_node"] == null || c.Arguments["to_node"] == null || c.Arguments["to_port"] == null))
                        throw new ToolError("Give from_node, to_node and to_port (and from_port if it is not 0), or node with all=true.");
                    var r = await patch.Run("patch_disconnect", true, c.String("machine"), "disconnect", Args(c), c.Cancellation);
                    return ToolResult.Ok($"Removed {r["removed"]} wire{((int)r["removed"]! == 1 ? "" : "s")}; {r["wire_count"]} left.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "patch_set",
                Title = "Set a node's controls",
                Description =
                    "Sets a node's control in its own real units: Hz for a cutoff (\"1.5 kHz\" or 1500), seconds for envelope times (\"20 ms\" or 0.02), milliseconds for a delay time, semitones and cents for tuning, a choice by name " +
                    "(Shape \"saw\", Mode \"ladder 24\"); the control by name or number. Out of range is refused with the range. changes sets several in one call (all or none); global=true or false changes a node's scope. " +
                    "A control a macro is mapped to is overwritten the next time that macro moves. One undo step. Example: patch_set(node=4, control=\"Cutoff\", value=\"800 Hz\").",
                Input = Object(null,
                    Opt("node", Integer("The node's id", 1)),
                    Opt("control", AnyOf("The control", String("Its name (\"Cutoff\", \"Attack\")"), Integer("Its number", 0, 15))),
                    Opt("value", NumberOrText("The new value")),
                    Opt("global", Boolean("Make the node global (true) or per voice (false)")),
                    Opt("changes", Array(Object(null, Req("node", Integer("The node's id", 1)), Req("control", AnyOf("The control", String("Its name"), Integer("Its number", 0, 15))), Req("value", NumberOrText("The new value"))), "Several controls at once", 1)),
                    MachineArg),
                Output = Loose(null, Req("set", Array(new JsonObject(), "Each control set: node, kind, control, value, shown, was"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    if (c.Arguments["changes"] == null && c.Arguments["node"] == null) throw new ToolError("Give node and control and value, or changes.");
                    var r = await patch.Run("patch_set", true, c.String("machine"), "set", Args(c), c.Cancellation);
                    var set = (JsonArray)r["set"]!;
                    return ToolResult.Ok("Set " + string.Join("; ", set.Take(8).Select(s => $"{s!["kind"]} {s["node"]} {s["control"]} to {s["shown"]} (was {s["was"]})")) + (set.Count > 8 ? "; …" : "") + ".", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "patch_remove",
                Title = "Remove nodes",
                Description = "Removes a node (or several) with its wires and macro mappings. One undo step. Example: patch_remove(node=5).",
                Input = Object(null, Opt("node", Integer("The node's id", 1)), Opt("nodes", Array(Integer("A node's id", 1), "Several nodes", 1)), MachineArg),
                Output = Loose(null, Req("removed", Array(String("A node"), "The nodes removed")), Req("wires_removed", Integer("Wires that went with them")), Req("node_count", Integer("Nodes left"))),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    if (c.Arguments["node"] == null && c.Arguments["nodes"] == null) throw new ToolError("Give node (an id) or nodes (a list of ids).");
                    var r = await patch.Run("patch_remove", true, c.String("machine"), "remove", Args(c), c.Cancellation);
                    return ToolResult.Ok($"Removed {string.Join(", ", ((JsonArray)r["removed"]!).Select(x => x!.ToString()))} and {r["wires_removed"]} wire(s); {r["node_count"]} nodes left.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "patch_macro_map",
                Title = "Map a macro to a control",
                Description =
                    "Maps one of the machine's 16 macros (1 to 16) to a node control, so the macro sweeps the control from low to high in the control's real units (default: from where it is to the far end of its range), and names the macro (label). " +
                    "A control has one mapping; mapping it again moves it. unmap=true takes the mapping off. Without node, label alone names the macro. Macros are the machine's Macro 1 to Macro 16 parameters, so patterns, automation and param_set move them; " +
                    "they start at 50 %. One undo step. Example: patch_macro_map(macro=1, node=4, control=\"Cutoff\", low=\"200 Hz\", high=\"8 kHz\", label=\"Brightness\").",
                Input = Object(null,
                    Req("macro", Integer("The macro, 1 to 16", 1, 16)),
                    Opt("node", Integer("The node whose control the macro moves", 1)),
                    Opt("control", AnyOf("The control", String("Its name"), Integer("Its number", 0, 15))),
                    Opt("low", NumberOrText("The control's value with the macro at 0 %")),
                    Opt("high", NumberOrText("The control's value with the macro at 100 %")),
                    Opt("label", String("A name for the macro (up to 24 characters)")),
                    Opt("unmap", Boolean("Take the control's mapping off")),
                    MachineArg),
                Output = Loose(null),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var r = await patch.Run("patch_macro_map", true, c.String("machine"), "macro_map", Args(c), c.Cancellation);
                    string text = r["unmapped"] != null ? $"Took the mapping off {r["unmapped"]}." : r["mapped"] != null ? $"Macro {r["macro"]} \"{r["label"]}\" moves {r["mapped"]}." : $"Named macro {r["macro"]} \"{r["label"]}\".";
                    return ToolResult.Ok(text, r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "patch_load_template",
                Title = "Load a template patch",
                Description =
                    "Replaces the machine's patch with a template, one for each Neo Learn method the nodes can build: start (the empty patch with sound in three steps: Keys, Osc, Envelope, VCA and Out with three wires left to make), " +
                    "subtractive, fm-2op, fm-4op, am-ring, wavefolder, sync-lead, plucked-string, vector-crossfade and sample-loop. Each sounds at -22 dB with the macros at their 50 % default and is built to be taken apart one node at a time " +
                    "(the build-a-synth guide: guide(topic=\"build-a-synth\")). One undo step brings the old patch back. Example: patch_load_template(template=\"fm-2op\").",
                Input = Object(null, Req("template", String("The template", Templates)), MachineArg),
                Output = Loose(null, Req("template", String("The template")), Req("title", String("Its title")), Req("summary", String("What it is")), Req("node_count", Integer("Nodes")), Req("wire_count", Integer("Wires")), Req("note", String("What to do next"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await patch.Run("patch_load_template", true, c.String("machine"), "load_template", Args(c), c.Cancellation);
                    return ToolResult.Ok($"Loaded the \"{r["title"]}\" template into {r["machine"]}: {r["summary"]} {r["note"]}", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "patch_save_preset",
                Title = "Save the patch as a preset",
                Description =
                    "Names the machine's patch and saves the machine's sound (the patch, its macros and its parameters) as a Neo Patch preset, in the preset file beside the machine, shared by every song. " +
                    "Won't replace an existing preset unless overwrite is true. Naming the patch is one undo step; the file is not undoable. Load it later with preset_load. " +
                    "Example: patch_save_preset(name=\"Lead — Agent Fold\", comment=\"A sine folded by its envelope\").",
                Input = Object(null,
                    Req("name", String("The preset's name (\"Group — Name\" files it in a group)")),
                    Opt("comment", String("What it sounds like")),
                    Opt("overwrite", Boolean("Replace a preset of that name")),
                    MachineArg),
                Output = Loose(null, Req("preset", String("Saved")), Req("file", String("The preset file"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string name = c.RequiredString("name").Trim();
                    if (name.Length == 0) throw new ToolError("name: give the preset a name.");
                    var r = await patch.SavePreset(c.String("machine"), name, c.String("comment"), c.Bool("overwrite") == true, c.Cancellation);
                    return ToolResult.Ok($"Saved {r["machine"]}'s patch as the preset \"{r["preset"]}\" in {r["file"]}.", r);
                },
            });
        }
    }
}
