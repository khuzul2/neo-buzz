// Agent server (docs/specs/midi-mapping.md, MM1): MIDI mapping for any controller. controller_list reads the controllers and the selected track;
// midi_map_list, midi_map_set and midi_map_clear read and edit the maps (both scopes); track_page_get and track_page_set read and edit a track's
// pages. They call the same MidiMapping methods the studio's popover, map mode and Inspector page do, so the validation and the undo step are the
// same: one undo step each, and an error says what to change.

using System;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Midi.Mapping;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class MidiMapTools
    {
        static readonly string[] Kinds = { "cc", "note", "bend", "pc" };
        static readonly string[] Encoders = { "absolute", "offset64", "twos" };
        static readonly string[] Takeovers = { "pickup", "jump", "scale" };
        static readonly string[] Commands = { "play", "stop", "record", "loop", "previous_track", "next_track", "select_track", "arm", "mute", "solo" };

        static JsonObject ControlSchema() => Object(null,
            Opt("device", String("The controller's name as controller_list gives it (\"\" or none: any device)")),
            Opt("kind", String("The message: cc (default), note, bend or pc", Kinds)),
            Opt("channel", Integer("MIDI channel 1 to 16 (default 1)", 1, 16)),
            Opt("number", Integer("Controller or note number 0 to 127 (a bend has none)", 0, 127)));

        static JsonObject InfoSchema() => Object(null,
            Req("scope", String("track or global", "track", "global")),
            Opt("track_id", String("The track of a page (\"track:Bass\")")),
            Opt("page", Integer("The page, from 1")),
            Opt("knob", Integer("The knob's number from 1 when the entry is a slot of a page")),
            Req("control", String("The control as a badge shows it (\"K3\", \"CC 21 · Device\")")),
            Opt("device", String("The controller (none: any device)")),
            Opt("kind", String("cc, note, bend or pc")),
            Opt("channel", Integer("1 to 16")),
            Opt("number", Integer("The controller or note")),
            Req("target", String("\"Machine · Parameter\", or the command")),
            Opt("machine", String("The machine")),
            Opt("parameter", String("The parameter (a track parameter as \"T:Name\")")),
            Opt("command", String("The command")),
            Req("range", String("The part of the parameter's range the control covers")),
            Req("invert", Boolean("Reversed")),
            Req("takeover", String("pickup, jump or scale")),
            Req("encoder", String("absolute, offset64 or twos")),
            Opt("fallback", Boolean("The generator's pinned parameter of that number (the knob has nothing of its own)")),
            Opt("legacy", Boolean("An old Buzz binding (any device, a channel and a controller)")));

        static JsonObject Json(MapInfo i)
        {
            var o = new JsonObject
            {
                ["scope"] = i.Scope,
                ["control"] = i.Control,
                ["target"] = i.Target?.Text ?? "",
                ["range"] = i.Range,
                ["invert"] = i.Invert,
                ["takeover"] = i.Takeover.ToString().ToLowerInvariant(),
                ["encoder"] = i.Encoding == KnobEncoding.Absolute ? "absolute" : i.Encoding == KnobEncoding.Offset64 ? "offset64" : "twos",
            };
            if (i.TrackId != null) { o["track_id"] = i.TrackId; o["page"] = i.Page + 1; }
            if (i.Slot >= 0) o["knob"] = i.Slot + 1;
            if (i.Slot < 0 && i.Control.Length > 0)
            {
                if (!i.Source.AnyDevice) o["device"] = i.Source.Device;
                o["kind"] = MapSource.KindText(i.Source.Kind);
                o["channel"] = i.Source.Channel + 1;
                o["number"] = i.Source.Number;
            }
            if (i.Target is { IsCommand: true } c) o["command"] = CommandName(c.Cmd!.Value);
            else if (i.Target != null) { o["machine"] = i.Target.Machine; o["parameter"] = i.Target.Param; if (i.Target.IsTrackParam) o["track"] = i.Target.Track; }
            if (i.Fallback) o["fallback"] = true;
            if (i.Legacy) o["legacy"] = true;
            return o;
        }

        static string CommandName(MapCommand c) => c switch
        {
            MapCommand.PreviousTrack => "previous_track",
            MapCommand.NextTrack => "next_track",
            MapCommand.SelectTrack => "select_track",
            _ => c.ToString().ToLowerInvariant(),
        };

        static MapSource SourceOf(ToolContext c)
        {
            string kind = (c.String("kind") ?? "cc").ToLowerInvariant();
            if (!MapSource.TryKind(kind, out var k)) throw new ToolError($"kind: \"{kind}\" is not one of cc, note, bend, pc.");
            int number = c.Int("number") ?? (k == MapKind.PitchBend ? 0 : throw new ToolError("number: give the controller or note number (0 to 127)."));
            return new MapSource(c.String("device") ?? "", k, (c.Int("channel") ?? 1) - 1, number);
        }

        static KnobEncoding EncoderOf(ToolContext c) => (c.String("encoder") ?? "absolute").ToLowerInvariant() switch
        {
            "absolute" => KnobEncoding.Absolute,
            "offset64" => KnobEncoding.Offset64,
            "twos" => KnobEncoding.TwosComplement,
            var e => throw new ToolError($"encoder: \"{e}\" is not one of absolute, offset64, twos."),
        };

        static MapDrive DriveOf(ToolContext c) => new MapDrive
        {
            Min = (c.Number("min") ?? 0),
            Max = (c.Number("max") ?? 1),
            Invert = c.Bool("invert") ?? false,
            Takeover = (c.String("takeover") ?? "pickup").ToLowerInvariant() switch
            {
                "pickup" => Takeover.Pickup,
                "jump" => Takeover.Jump,
                "scale" => Takeover.Scale,
                var t => throw new ToolError($"takeover: \"{t}\" is not one of pickup, jump, scale."),
            },
        };

        static MapTarget TargetOf(ReBuzzCore core, JsonObject args, ToolContext? c = null)
        {
            string text(string n) => args[n] is JsonValue v && v.TryGetValue(out string? s) ? s! : "";
            string command = text("command");
            if (command.Length > 0)
            {
                int index = args["index"] is JsonValue iv && iv.TryGetValue(out double d) ? (int)d : 1;
                return command.ToLowerInvariant() switch
                {
                    "play" => MapTarget.Command(MapCommand.Play),
                    "stop" => MapTarget.Command(MapCommand.Stop),
                    "record" => MapTarget.Command(MapCommand.Record),
                    "loop" => MapTarget.Command(MapCommand.Loop),
                    "previous_track" => MapTarget.Command(MapCommand.PreviousTrack),
                    "next_track" => MapTarget.Command(MapCommand.NextTrack),
                    "select_track" => MapTarget.Command(MapCommand.SelectTrack, index),
                    "arm" => MapTarget.Command(MapCommand.Arm),
                    "mute" => MapTarget.Command(MapCommand.Mute),
                    "solo" => MapTarget.Command(MapCommand.Solo),
                    var other => throw new ToolError($"command: \"{other}\" is not one of {string.Join(", ", Commands)}."),
                };
            }
            string machine = text("machine"), parameter = text("parameter");
            if (machine.Length == 0 || parameter.Length == 0) throw new ToolError("Give a target: machine and parameter, or a command.");
            var m = SongModel.Machine(core, machine);
            int track = args["track"] is JsonValue tv && tv.TryGetValue(out double td) ? (int)td : 0;
            var p = SongModel.Param(m, SongModel.Params(m), parameter, track > 0);
            return MapTarget.Parameter(m.Name, p.IsTrack ? MapTarget.TrackPrefix + p.Name : p.Name, p.IsTrack ? track : 0);
        }

        static void Check(MapResult r)
        {
            if (!r.Ok) throw new ToolError(r.Error!);
        }

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "controller_list",
                Title = "MIDI controllers",
                Description =
                    "The MIDI controllers: every input that is open now and every device a map, a knob group or a role names. Each is named by its port (a second one of the same name is \"Name #2\"), " +
                    "with how many knobs its group has (0: not taught yet) and whether its keys follow the selected track. Also the selected track (what the keys and the knobs drive), whether " +
                    "selecting a track arms it (auto-arm) and which track each device is pinned to (a track whose MIDI From names it). Nothing is specific to a model: a device with no " +
                    "group works through direct mappings. Example: controller_list().",
                Input = Object(null),
                Output = Object(null,
                    Req("controllers", Array(Object(null,
                        Req("device", String("The controller's name")),
                        Req("connected", Boolean("Open now")),
                        Req("knobs", Integer("Knobs in its group (0: none taught)")),
                        Req("keys", Boolean("Its notes follow the selected track")),
                        Opt("pinned_to", Array(String("A track"), "The tracks whose MIDI From names it"))), "The controllers")),
                    Opt("selected_track", String("The selected track's id")),
                    Req("auto_arm", Boolean("Selecting a track arms it")),
                    Req("knob_count", Integer("Slots a page shows (the largest knob group, 8 at least)"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("controller_list", false, _ =>
                    {
                        var mapping = core.MidiMapping ?? throw new ToolError("MIDI mapping is not available here.");
                        var layout = core.SessionLayout;
                        var list = new JsonArray();
                        foreach (var (id, connected, knobs, keys) in mapping.Controllers())
                        {
                            var o = new JsonObject { ["device"] = id, ["connected"] = connected, ["knobs"] = knobs, ["keys"] = keys };
                            var pinned = core.SongCore.MachinesList.Where(m => ReBuzz.Core.Session.SessionLayout.CanArm(m) && layout != null && layout.TrackInputOf(m) is { Midi: ReBuzz.Core.Session.MidiFromKind.Device } t && string.Equals(t.Device, id, StringComparison.OrdinalIgnoreCase))
                                .Select(m => (JsonNode)("track:" + m.Name)).ToArray();
                            if (pinned.Length > 0) o["pinned_to"] = new JsonArray(pinned);
                            list.Add(o);
                        }
                        var result = new JsonObject { ["controllers"] = list, ["auto_arm"] = mapping.AutoArm, ["knob_count"] = mapping.KnobCount };
                        if (mapping.SelectedTrackId is { } id2) result["selected_track"] = id2;
                        return result;
                    }, c.Cancellation);
                    var cs = r["controllers"]!.AsArray();
                    string text = cs.Count == 0 ? "No MIDI controllers are open." : string.Join("; ", cs.Select(x => $"{x!["device"]}" + (x["connected"]!.GetValue<bool>() ? "" : " (not connected)") + $", {x["knobs"]} knob(s)"));
                    return ToolResult.Ok(text + (r["selected_track"] != null ? $". Selected: {r["selected_track"]}." : ". No track is selected."), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "midi_map_list",
                Title = "List MIDI mappings",
                Description =
                    "The MIDI mappings. A mapping is a control (device, message kind cc/note/bend/pc, channel, number) driving a target: a parameter (machine, parameter, track) with a range, invert and " +
                    "takeover (pickup waits until the control passes the value, jump goes there, scale makes both meet at the ends), or a command (play, stop, record, loop, previous_track, next_track, " +
                    "select_track n, arm, mute, solo of the selected track). Two scopes: track pages (a track's slots, \"knob\" 1..N of a device's knob group, and direct entries; saved in the song) and " +
                    "global maps (per computer, with an optional copy in the song). scope: all (default), track, global, or active (what is in force now: the selected track's current page, then global). " +
                    "Old Buzz bindings show as global entries on any device (legacy). Example: midi_map_list(scope=\"active\").",
                Input = Object(null,
                    Opt("scope", String("all (default), track, global or active", "all", "track", "global", "active")),
                    Opt("track_id", String("Only this track's pages (\"track:Bass\")"))),
                Output = Object(null,
                    Req("count", Integer("How many")),
                    Req("mappings", Array(InfoSchema(), "The mappings")),
                    Opt("song_global", Boolean("The song keeps its own copy of the global map"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    string scope = (c.String("scope") ?? "all").ToLowerInvariant();
                    if (scope is not ("all" or "track" or "global" or "active")) throw new ToolError($"scope: \"{scope}\" is not one of all, track, global, active.");
                    var r = await host.Engine("midi_map_list", false, _ =>
                    {
                        var mapping = core.MidiMapping ?? throw new ToolError("MIDI mapping is not available here.");
                        var list = scope == "active" ? mapping.Active() : mapping.All(scope, c.String("track_id"));
                        return new JsonObject { ["count"] = list.Count, ["mappings"] = new JsonArray(list.Select(i => (JsonNode)Json(i)).ToArray()), ["song_global"] = mapping.UsesSongGlobal };
                    }, c.Cancellation);
                    var items = r["mappings"]!.AsArray();
                    string text = items.Count == 0 ? "No MIDI mappings (" + scope + ")." : $"{items.Count} mapping(s): " + string.Join("; ", items.Take(12).Select(i => $"{i!["control"]} → {i["target"]}")) + (items.Count > 12 ? "; …" : "");
                    return ToolResult.Ok(text, r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "midi_map_set",
                Title = "Map a control",
                Description =
                    "Maps a control to a target (op \"map\", the default), or sets a device's knob group, its keys role, or the song's own copy of the global map. One undo step; the same checks as the studio. " +
                    "map: give the control (device, kind, channel, number; a device that is not named means any device) and the target (machine and parameter [and track], or command [and index]). " +
                    "scope \"track\" (the selected track, or track_id): a knob of the device's group fills the slot of that number on the track's current page, any other control becomes a direct entry of " +
                    "the page; the machine must lie on the track's chain. scope \"global\" (default): holds on every track. A control drives one target and a target has one control in a scope. " +
                    "min/max (0..1, the part of the parameter's range), invert, takeover (pickup default, jump, scale) and encoder (absolute, offset64, twos for a relative knob) shape it. " +
                    "knob_group: device and the numbers of its knobs in order (first_number and count for a run of controller numbers, or numbers=[...]); channel. keys_role: device and keys (false leaves its notes out of the selection). " +
                    "song_global: own=true gives the song its own copy of the global map, false goes back to the computer's. Example: midi_map_set(scope=\"global\", device=\"Pads\", kind=\"note\", number=36, command=\"play\").",
                Input = Object(null,
                    Opt("op", String("map (default), knob_group, keys_role or song_global", "map", "knob_group", "keys_role", "song_global")),
                    Opt("scope", String("track or global (default global)", "track", "global")),
                    Opt("track_id", String("The track for scope track (default: the selected one)")),
                    Opt("device", String("The controller (see controller_list); none for a control of any device")),
                    Opt("kind", String("cc (default), note, bend or pc", Kinds)),
                    Opt("channel", Integer("MIDI channel 1 to 16 (default 1)", 1, 16)),
                    Opt("number", Integer("Controller or note number", 0, 127)),
                    Opt("encoder", String("absolute (default), offset64 (64 ± n) or twos (1 up, 127 down)", Encoders)),
                    Opt("machine", String("The target machine")),
                    Opt("parameter", String("The target parameter (a name, or #index)")),
                    Opt("track", Integer("Track of a track parameter (default 0)", 0, 255)),
                    Opt("command", String("A command instead of a parameter", Commands)),
                    Opt("index", Integer("The track number of select_track (from 1)", 1, 255)),
                    Opt("min", Number("Low end of the part of the range, 0..1 (default 0)", 0, 1)),
                    Opt("max", Number("High end, 0..1 (default 1)", 0, 1)),
                    Opt("invert", Boolean("Reverse the control")),
                    Opt("takeover", String("pickup (default), jump or scale", Takeovers)),
                    Opt("first_number", Integer("knob_group: the first knob's controller number", 0, 127)),
                    Opt("count", Integer("knob_group: how many knobs", 1, 64)),
                    Opt("numbers", Array(Integer("A controller number", 0, 127), "knob_group: the knobs' controller numbers in order")),
                    Opt("keys", Boolean("keys_role: whether the device's notes follow the selected track")),
                    Opt("own", Boolean("song_global: true keeps the global map in the song"))),
                Output = Object(null,
                    Req("changed", Boolean("Whether this call changed the maps")),
                    Req("text", String("What it did")),
                    Opt("knob", Integer("The slot filled (from 1)"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string op = (c.String("op") ?? "map").ToLowerInvariant();
                    var r = await host.Engine("midi_map_set", true, _ =>
                    {
                        var mapping = core.MidiMapping ?? throw new ToolError("MIDI mapping is not available here.");
                        switch (op)
                        {
                            case "knob_group":
                            {
                                string device = c.String("device") ?? throw new ToolError("knob_group: give the device (controller_list names them).");
                                int ch = (c.Int("channel") ?? 1) - 1;
                                var numbers = c.List("numbers")?.Select(n => (int)n!.GetValue<double>()).ToList()
                                    ?? Enumerable.Range(c.Int("first_number") ?? throw new ToolError("knob_group: give numbers=[...] or first_number and count."), c.Int("count") ?? 8).ToList();
                                if (numbers.Any(n => n < 0 || n > 127)) throw new ToolError("knob_group: controller numbers run from 0 to 127.");
                                var enc = EncoderOf(c);
                                bool changed = mapping.SetKnobGroup(device, numbers.Select(n => new MapSource(device, MapKind.ControlChange, ch, n)), numbers.Select(_ => enc));
                                return new JsonObject { ["changed"] = changed, ["text"] = $"{device}: {numbers.Count} knob(s), controllers {string.Join(", ", numbers)}." };
                            }
                            case "keys_role":
                            {
                                string device = c.String("device") ?? throw new ToolError("keys_role: give the device.");
                                bool keys = c.Bool("keys") ?? throw new ToolError("keys_role: give keys=true or false.");
                                bool changed = mapping.SetKeysRole(device, keys);
                                return new JsonObject { ["changed"] = changed, ["text"] = $"{device}: its notes {(keys ? "follow" : "do not follow")} the selected track." };
                            }
                            case "song_global":
                            {
                                bool own = c.Bool("own") ?? throw new ToolError("song_global: give own=true or false.");
                                bool changed = mapping.SetSongGlobal(own);
                                return new JsonObject { ["changed"] = changed, ["text"] = own ? "The song keeps its own global map." : "The song uses the computer's global map." };
                            }
                            case "map":
                            {
                                var source = SourceOf(c);
                                var target = TargetOf(core, c.Arguments);
                                string scope = (c.String("scope") ?? "global").ToLowerInvariant();
                                if (scope is not ("track" or "global")) throw new ToolError($"scope: \"{scope}\" is not track or global.");
                                var result = mapping.MapControl(source, EncoderOf(c), target, scope == "track" ? MapScope.Track : MapScope.Global, DriveOf(c), c.String("track_id"));
                                Check(result);
                                var o = new JsonObject { ["changed"] = result.Changed, ["text"] = result.Text ?? "" };
                                if (result.Slot >= 0) o["knob"] = result.Slot + 1;
                                return o;
                            }
                            default:
                                throw new ToolError($"op: \"{op}\" is not one of map, knob_group, keys_role, song_global.");
                        }
                    }, c.Cancellation);
                    return ToolResult.Ok(r["text"]!.GetValue<string>() + (r["changed"]!.GetValue<bool>() ? "" : " (no change)"), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "midi_map_clear",
                Title = "Clear MIDI mappings",
                Description =
                    "Removes mappings, one undo step. Name a control (device, kind, channel, number) to remove what it drives; or a target (machine and parameter, or command) to remove what drives it " +
                    "(everywhere, with an old Buzz binding of the parameter too); or all=true with scope to empty a scope (global: the global map in use; track: every page of track_id, or of every track with track_id \"*\"). " +
                    "Example: midi_map_clear(machine=\"Bass\", parameter=\"Cutoff\").",
                Input = Object(null,
                    Opt("device", String("The control's controller (none: any)")),
                    Opt("kind", String("cc (default), note, bend or pc", Kinds)),
                    Opt("channel", Integer("1 to 16 (default 1)", 1, 16)),
                    Opt("number", Integer("The controller or note", 0, 127)),
                    Opt("machine", String("The target machine")),
                    Opt("parameter", String("The target parameter")),
                    Opt("track", Integer("Track of a track parameter", 0, 255)),
                    Opt("command", String("A command target", Commands)),
                    Opt("index", Integer("select_track's number", 1, 255)),
                    Opt("all", Boolean("Empty the scope")),
                    Opt("scope", String("track or global (with all)", "track", "global")),
                    Opt("track_id", String("With all and scope track: the track (\"*\": every track)"))),
                Output = Object(null, Req("changed", Boolean("Whether anything was removed")), Req("text", String("What it did"))),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    var r = await host.Engine("midi_map_clear", true, _ =>
                    {
                        var mapping = core.MidiMapping ?? throw new ToolError("MIDI mapping is not available here.");
                        bool changed;
                        string what;
                        if (c.Bool("all") == true)
                        {
                            string scope = (c.String("scope") ?? throw new ToolError("all: give scope (track or global).")).ToLowerInvariant();
                            string track = c.String("track_id");
                            changed = mapping.Edit((cm, sm) =>
                            {
                                if (scope == "global") (sm.Global ?? cm.Global).Clear();
                                else foreach (var t in sm.Tracks.Where(t => track == "*" || t.TrackId == (track ?? mapping.SelectedTrackId)))
                                    foreach (var p in t.Pages) { p.Slots.Clear(); p.Direct.Clear(); }
                            });
                            what = $"Cleared the {scope} mappings";
                        }
                        else if (c.Int("number") != null || c.String("kind") != null)
                        {
                            changed = mapping.Unmap(SourceOf(c));
                            what = "Cleared what the control drove";
                        }
                        else
                        {
                            var target = TargetOf(core, c.Arguments);
                            changed = mapping.UnmapTarget(target, everywhere: true);
                            what = $"Cleared what drove {target.Text}";
                        }
                        return new JsonObject { ["changed"] = changed, ["text"] = changed ? what + "." : "Nothing was mapped there." };
                    }, c.Cancellation);
                    return ToolResult.Ok(r["text"]!.GetValue<string>(), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "track_page_get",
                Title = "A track's control pages",
                Description =
                    "A track's control pages (what the Inspector shows for the selected track): each page's slots, laid out as a knob group (knob 1..N), with the machine and parameter each holds, its range " +
                    "and the value now, and which slots are free. A free slot falls back to the generator's pinned parameter of the same number (fallback). The knobs of every device with a group drive the " +
                    "selected track's current page, so selecting another track moves them to its page. Example: track_page_get(track_id=\"track:Bass\").",
                Input = Object(null, Opt("track_id", String("The track (default: the selected one)"))),
                Output = Object(null,
                    Req("track_id", String("The track")),
                    Req("selected", Boolean("It is the selected track")),
                    Req("current_page", Integer("The page in use, from 1")),
                    Req("pages", Array(Object(null,
                        Req("page", Integer("From 1")),
                        Req("name", String("The page's name")),
                        Req("slots", Array(Object(null,
                            Req("knob", Integer("From 1")),
                            Opt("machine", String("The machine")),
                            Opt("parameter", String("The parameter")),
                            Opt("value", String("Its value now")),
                            Opt("range", String("The part of the range")),
                            Opt("invert", Boolean("Reversed")),
                            Opt("takeover", String("pickup, jump or scale")),
                            Opt("fallback", Boolean("The generator's pin"))), "The knobs"))), "The pages")),
                    Req("chain", Array(String("A machine"), "The machines a slot may hold (the track's chain)"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("track_page_get", false, _ => Describe(core, c.String("track_id")), c.Cancellation);
                    var pages = r["pages"]!.AsArray();
                    int used = pages.Sum(p => p!["slots"]!.AsArray().Count(s => s!["machine"] != null && s["fallback"] == null));
                    return ToolResult.Ok($"{r["track_id"]}: {pages.Count} page(s), {used} slot(s) mapped; chain {string.Join(", ", r["chain"]!.AsArray().Select(x => x!.GetValue<string>()))}.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "track_page_set",
                Title = "Set a track's control page",
                Description =
                    "Fills slots of a track's page, adds a page, or changes the page in use. One undo step. slots: [{knob, machine, parameter [, track], min, max, invert, takeover}] (omit machine to free the knob); " +
                    "each machine must lie on the track's chain (track_page_get lists it) and be a parameter, not a note. page (from 1) picks the page (default: the current one). add_page appends one and makes it " +
                    "current; select_page makes page current; remove_page deletes it. Example: track_page_set(track_id=\"track:Bass\", slots=[{\"knob\": 1, \"machine\": \"Bass\", \"parameter\": \"Cutoff\"}, {\"knob\": 2, \"machine\": \"Echo\", \"parameter\": \"Feedback\"}]).",
                Input = Object(null,
                    Opt("track_id", String("The track (default: the selected one)")),
                    Opt("page", Integer("The page, from 1 (default: the current)", 1, TrackPages.MaxPages)),
                    Opt("slots", Array(Object("One slot",
                        Req("knob", Integer("From 1", 1, 64)),
                        Opt("machine", String("The machine (none: free the knob)")),
                        Opt("parameter", String("The parameter")),
                        Opt("track", Integer("Track of a track parameter", 0, 255)),
                        Opt("min", Number("0..1", 0, 1)), Opt("max", Number("0..1", 0, 1)),
                        Opt("invert", Boolean("Reversed")), Opt("takeover", String("pickup, jump or scale", Takeovers))), "Slots to set")),
                    Opt("add_page", Boolean("Append a page and make it current")),
                    Opt("select_page", Boolean("Make page the current one")),
                    Opt("remove_page", Boolean("Delete page (a track keeps at least one)"))),
                Output = Object(null,
                    Req("changed", Boolean("Whether this call changed the pages")),
                    Req("track_id", String("The track")),
                    Req("current_page", Integer("The page in use now, from 1"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("track_page_set", true, _ =>
                    {
                        var mapping = core.MidiMapping ?? throw new ToolError("MIDI mapping is not available here.");
                        string id = c.String("track_id") ?? mapping.SelectedTrackId ?? throw new ToolError("No track is selected: give track_id (machine_tracks lists them).");
                        if (mapping.EntryById(id) == null) throw new ToolError($"The song has no track \"{id}\" (machine_tracks lists them).");
                        bool changed = false;
                        if (c.Bool("add_page") == true) changed |= mapping.AddPage(id);
                        int pageCount() => mapping.PagesOf(id)?.Pages.Count ?? 1;
                        int current() => mapping.PagesOf(id)?.Current ?? 0;
                        int page = (c.Int("page") ?? current() + 1) - 1;
                        if (page < 0 || page >= pageCount()) throw new ToolError($"{id} has {pageCount()} page(s): there is no page {page + 1}.");
                        if (c.Bool("select_page") == true) changed |= mapping.SelectPage(id, page);
                        if (c.List("slots") is { } slots)
                            foreach (var s in slots.OfType<JsonObject>())
                            {
                                int knob = s["knob"] is JsonValue kv && kv.TryGetValue(out double kd) ? (int)kd : throw new ToolError("slots: each needs a knob number from 1.");
                                MapTarget target = s["machine"] == null ? null! : TargetOf(core, s);
                                var drive = new MapDrive
                                {
                                    Min = s["min"] is JsonValue a && a.TryGetValue(out double lo) ? lo : 0,
                                    Max = s["max"] is JsonValue b && b.TryGetValue(out double hi) ? hi : 1,
                                    Invert = s["invert"] is JsonValue iv && iv.TryGetValue(out bool inv) && inv,
                                    Takeover = s["takeover"] is JsonValue tv && tv.TryGetValue(out string? tk) ? (tk!.ToLowerInvariant() switch { "jump" => Takeover.Jump, "scale" => Takeover.Scale, _ => Takeover.Pickup }) : Takeover.Pickup,
                                };
                                var res = mapping.SetSlot(id, page, knob - 1, target, drive);
                                Check(res);
                                changed |= res.Changed;
                            }
                        if (c.Bool("remove_page") == true) changed |= mapping.RemovePage(id, page);
                        return new JsonObject { ["changed"] = changed, ["track_id"] = id, ["current_page"] = current() + 1 };
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["track_id"]}: page {r["current_page"]} in use" + (r["changed"]!.GetValue<bool>() ? "." : " (no change)."), r);
                },
            });
        }

        static JsonObject Describe(ReBuzzCore core, string? trackId)
        {
            var mapping = core.MidiMapping ?? throw new ToolError("MIDI mapping is not available here.");
            string id = trackId ?? mapping.SelectedTrackId ?? throw new ToolError("No track is selected: give track_id (machine_tracks lists them).");
            var entry = mapping.EntryById(id) ?? throw new ToolError($"The song has no track \"{id}\" (machine_tracks lists them).");
            var pages = mapping.PagesOf(id);
            int count = mapping.KnobCount;
            var generator = MidiMapping.GeneratorOf(entry);
            var pins = generator != null && core.PinStore != null ? core.PinStore.Get(generator.Name) : System.Array.Empty<string>();
            var list = new JsonArray();
            int n = pages?.Pages.Count ?? 1;
            for (int pi = 0; pi < n; pi++)
            {
                var page = pages?.Pages[pi];
                var slots = new JsonArray();
                for (int i = 0; i < count; i++)
                {
                    var o = new JsonObject { ["knob"] = i + 1 };
                    MapDrive? d = null;
                    bool fallback = false;
                    if (page != null && page.Slots.TryGetValue(i, out var have)) d = have;
                    else if (generator != null && i < pins.Count) { d = new MapDrive { Target = MapTarget.Parameter(generator.Name, pins[i]) }; fallback = true; }
                    if (d?.Target != null && !d.Target.IsCommand)
                    {
                        o["machine"] = d.Target.Machine;
                        o["parameter"] = d.Target.Param;
                        if (mapping.ParameterOf(d.Target) is { } p)
                        {
                            try { o["value"] = p.DescribeValue(p.GetValue(d.Target.Track)); } catch (Exception) { o["value"] = p.GetValue(d.Target.Track).ToString(); }
                        }
                        o["range"] = d.RangeText;
                        if (d.Invert) o["invert"] = true;
                        o["takeover"] = d.Takeover.ToString().ToLowerInvariant();
                        if (fallback) o["fallback"] = true;
                    }
                    slots.Add(o);
                }
                list.Add(new JsonObject { ["page"] = pi + 1, ["name"] = page?.Name ?? "Page 1", ["slots"] = slots });
            }
            return new JsonObject
            {
                ["track_id"] = id,
                ["selected"] = mapping.SelectedTrackId == id,
                ["current_page"] = (pages?.Current ?? 0) + 1,
                ["pages"] = list,
                ["chain"] = new JsonArray(mapping.ChainOf(entry).Select(m => (JsonNode)m.Name).ToArray()),
            };
        }
    }
}
