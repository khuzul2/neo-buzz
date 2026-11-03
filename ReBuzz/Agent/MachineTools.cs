// Agent server (docs/specs/mcp-server.md §5.3): gear, machines and routing.
// gear_list, machine_list, machine_add, machine_remove, machine_set, connect, disconnect, connection_set.
// U2 (docs/specs/machine-safety.md §3.1, §3.4): gear_list shows the profile's categories (diverging,
// hot, needs a window) and safe ranges; machine_add warns only for diverging machines.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class MachineTools
    {
        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "gear_list",
                Title = "List gear",
                Description =
                    "The machines you can add: name, generator or effect, author, what it is (type, summary) when known, its manifest (generated, " +
                    "curated, or a profiled draft: its parameters take \"800 Hz\", \"-6 dB\"; exact_units when not a draft), its health and its parameters' " +
                    "safe ranges (param_set keeps to them). Machines with exact units come first, then drafts; machines that need a window, then unhealthy ones " +
                    "(diverging: NaN or a runaway level; crashes) last, flagged with why. Hot ones (loud but steady at some setting) are a warning. Filter by kind or text. " +
                    "Example: gear_list(kind=\"generator\", search=\"poly\").",
                Input = Object(null,
                    Opt("kind", String("Only generators (make sound) or effects (process sound)", "generator", "effect")),
                    Opt("search", String("Text to find in the name, author or summary")),
                    Opt("limit", Integer("At most this many (default 60)", 1, 1000))),
                Output = Object(null,
                    Req("gear", Array(Object(null,
                        Req("name", String("Pass this to machine_add")),
                        Req("kind", String("generator or effect")),
                        Opt("author", String("Author")),
                        Opt("type", String("What it is: synth, drums, filter, reverb… (from its manifest)")),
                        Opt("summary", String("What it is and when to use it")),
                        Req("exact_units", Boolean("Has a generated or curated manifest: values in units land exactly")),
                        Opt("manifest", String("generated, curated or draft (profiled, not yet curated)")),
                        Opt("presets", Integer("Factory presets")),
                        Opt("health", String("missing or crashed (not usable); unhealthy, warn or needs_window (from its profile)")),
                        Opt("diverging", Boolean("Its output runs away at some setting (NaN or a runaway level)")),
                        Opt("needs_window", String("It works in ReBuzz on Windows with its window, not headless")),
                        Opt("safe_ranges", Array(Object("A parameter's safe range", Req("param", String("Its name")), Req("range", String("The safe values"))), "Where param_set keeps agents")),
                        Opt("problems", Array(String("A problem"), "Why it is unhealthy, hot or warned about"))), "Matching gear")),
                    Req("total", Integer("How many matched before the limit"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    string kind = c.String("kind"), search = c.String("search")?.Trim();
                    int limit = c.Int("limit") ?? 60;
                    var r = await host.Engine("gear_list", false, _ =>
                    {
                        var rows = new List<(int Rank, string Name, JsonObject Row)>();
                        foreach (var dll in core.MachineDLLs.Values)
                        {
                            var info = dll.Info;
                            if (info == null || info.Type == MachineType.Master) continue;
                            if ((info.Flags & MachineInfoFlags.PATTERN_EDITOR) != 0) continue;
                            string k = info.Type == MachineType.Generator ? "generator" : "effect";
                            if (kind != null && k != kind) continue;
                            var manifest = SongModel.Manifest(dll);
                            string summary = manifest?.Summary ?? "", type = manifest?.Type ?? "";
                            if (!string.IsNullOrEmpty(search) &&
                                !(dll.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || (info.Author ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                  summary.Contains(search, StringComparison.OrdinalIgnoreCase) || type.Contains(search, StringComparison.OrdinalIgnoreCase)))
                                continue;
                            var row = new JsonObject { ["name"] = dll.Name, ["kind"] = k };
                            if (!string.IsNullOrEmpty(info.Author)) row["author"] = info.Author;
                            if (type.Length > 0) row["type"] = type;
                            if (summary.Length > 0) row["summary"] = summary;
                            bool exact = manifest is { IsDraft: false };
                            row["exact_units"] = exact;
                            if (manifest != null) row["manifest"] = manifest.Quality;
                            if (dll.Presets?.Count > 0) row["presets"] = dll.Presets.Count;
                            bool unhealthy = dll.IsMissing || dll.IsCrashed || manifest?.Health?.Unhealthy == true;
                            bool window = !unhealthy && manifest?.Health?.NeedsWindow == true;
                            if (dll.IsMissing || dll.IsCrashed) row["health"] = dll.IsMissing ? "missing" : "crashed";
                            else if (manifest?.Health is { Status: not "ok" } health)
                            {
                                row["health"] = health.Status;
                                if (health.Diverging) row["diverging"] = true;
                                if (health.NeedsWindow) row["needs_window"] = ParamTools.WindowNote(health);
                                if (health.Problems.Count > 0) row["problems"] = new JsonArray(health.Problems.Select(x => (JsonNode)x).ToArray());
                            }
                            var safe = manifest?.Parameters.Concat(manifest.TrackParameters).Where(p => p.SafeRange != null).ToList();
                            if (safe?.Count > 0)
                                row["safe_ranges"] = new JsonArray(safe.Select(p => (JsonNode)new JsonObject
                                {
                                    ["param"] = p.Name,
                                    ["range"] = p.SafeRange.Text(),
                                    ["min_raw"] = p.SafeRange.Min,
                                    ["max_raw"] = p.SafeRange.Max,
                                    ["reason"] = p.SafeRange.Reason,
                                }).ToArray());
                            rows.Add((unhealthy ? 4 : window ? 3 : exact ? 0 : manifest != null ? 1 : 2, dll.Name, row));
                        }
                        var sorted = rows.OrderBy(x => x.Rank).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                        return new JsonObject
                        {
                            ["gear"] = new JsonArray(sorted.Take(limit).Select(x => (JsonNode)x.Row).ToArray()),
                            ["total"] = sorted.Count,
                        };
                    }, c.Cancellation);
                    var gear = r["gear"]!.AsArray();
                    string names = string.Join(", ", gear.Take(12).Select(g => g!["name"]!.GetValue<string>()));
                    return ToolResult.Ok($"{r["total"]} {(kind ?? "machine")}s{(search != null ? $" matching \"{search}\"" : "")}" +
                                         (gear.Count > 0 ? $": {names}{(gear.Count > 12 ? ", …" : "")}." : ". Try without a filter."), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "machine_list",
                Title = "List the song's machines",
                Description =
                    "Every machine in the song with its gear, kind, connections in and out (gain in dB, pan −1…+1), mute/solo/bypass, " +
                    "tracks and pattern count. Use it to see the signal flow before changing it. Example: machine_list().",
                Input = Object(null),
                Output = Object(null, Req("machines", Array(Object("A machine", Req("name", String("Its name")), Req("gear", String("Its gear")), Req("kind", String("master, generator or effect"))), "The machines, Master first"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("machine_list", false, _ => new JsonObject
                    {
                        ["machines"] = new JsonArray(SongModel.Machines(core).Select(m => (JsonNode)SongModel.MachineJson(core, m)).ToArray()),
                    }, c.Cancellation);
                    var lines = r["machines"]!.AsArray().Select(m =>
                    {
                        var outs = m!["outputs"]!.AsArray().Select(o => $"{o!["to"]} ({o["gain_db"]} dB)");
                        return $"{m["name"]} [{m["gear"]}]" + (outs.Any() ? " → " + string.Join(", ", outs) : "");
                    });
                    return ToolResult.Ok(string.Join("; ", lines), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "machine_add",
                Title = "Add a machine",
                Description =
                    "Adds a machine from gear_list and connects it: to Master by default, to another machine with connect_to, or into a chain " +
                    "with insert_after (an effect goes between that machine and everything it fed). Name machines by role (\"Bass\", \"Drums\", \"Room\"). " +
                    "Returns its name and a parameter overview; machine_describe gives the details. One undo step. " +
                    "Example: machine_add(gear=\"Neo Poly\", name=\"Pad\", gain_db=-6).",
                Input = Object(null,
                    Req("gear", String("The gear name, from gear_list")),
                    Opt("name", String("Its name in the song (unique); default: from the gear")),
                    Opt("connect_to", String("Machine its output goes to (default \"Master\"; \"none\" leaves it unconnected)")),
                    Opt("insert_after", String("For effects: put it after this machine, between it and everything it fed")),
                    Opt("gain_db", Db("Gain of its output connection in dB (default 0)")),
                    Opt("tracks", Integer("Tracks (voices for tracker-played generators)", 1, 256))),
                Output = Object(null,
                    Req("machine", Object(null, Req("name", String("Its name")))),
                    Req("parameters", Integer("Global parameters")),
                    Req("groups", Array(String("A group"), "Parameter groups")),
                    Req("exact_units", Boolean("Values in units land exactly"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string gear = c.RequiredString("gear"), name = c.String("name")?.Trim(), connectTo = c.String("connect_to"), insertAfter = c.String("insert_after");
                    double? gainDb = c.Arguments["gain_db"] != null ? Levels.ParseDb(c.Arguments["gain_db"], "gain_db") : null;
                    var r = await host.Engine("machine_add", true, _ =>
                    {
                        var dll = core.MachineDLLs.Values.FirstOrDefault(d => d.Name == gear)
                                  ?? core.MachineDLLs.Values.FirstOrDefault(d => string.Equals(d.Name, gear, StringComparison.OrdinalIgnoreCase));
                        if (dll == null)
                        {
                            var near = Text.Closest(gear, core.MachineDLLs.Keys);
                            throw new ToolError($"No gear \"{gear}\"." + (near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : "") + " gear_list shows what's installed.");
                        }
                        if (dll.Info == null || dll.Info.Type == MachineType.Master || (dll.Info.Flags & MachineInfoFlags.PATTERN_EDITOR) != 0)
                            throw new ToolError($"{dll.Name} isn't a machine you can add to a song.");
                        if (dll.IsMissing || dll.IsCrashed) throw new ToolError($"{dll.Name} is {(dll.IsMissing ? "missing" : "crashed")} and can't be added.");
                        bool generator = dll.Info.Type == MachineType.Generator;
                        if (name != null && SongModel.Machines(core).Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
                            throw new ToolError($"There's already a machine named \"{name}\". Choose another name, or omit name for one made from the gear.");
                        if (insertAfter != null && generator) throw new ToolError("insert_after is for effects; a generator starts a chain. Use connect_to.");
                        MachineCore after = insertAfter != null ? SongModel.Machine(core, insertAfter, "insert_after") : null;
                        MachineCore target = null;
                        if (after == null && !string.Equals(connectTo, "none", StringComparison.OrdinalIgnoreCase))
                            target = SongModel.Machine(core, connectTo ?? "Master", "connect_to");
                        int? tracks = c.Int("tracks");
                        if (tracks is int t && (t < dll.Info.MinTracks || t > Math.Max(dll.Info.MinTracks, dll.Info.MaxTracks)))
                            throw new ToolError($"{dll.Name} takes {dll.Info.MinTracks}–{dll.Info.MaxTracks} tracks.");

                        var before = SongModel.Machines(core).ToHashSet();
                        var (x, y) = SongModel.NewPosition(core, generator);
                        var profiled = SongModel.Manifest(dll)?.Health;
                        try
                        {
                            core.SongCore.CreateMachine(dll.Name, null, name, null, null, null, tracks ?? -1, x, y);
                        }
                        catch (Exception e) when (profiled?.NeedsWindow == true && e is not ToolError)
                        {
                            // machine-safety.md §3.1: not an audio fault.
                            throw new ToolError($"{dll.Name} needs a window: {ParamTools.WindowNote(profiled)} ({e.GetType().Name}: {e.Message}) " +
                                                "studio_status shows what undo would revert.");
                        }
                        var made = SongModel.Machines(core).FirstOrDefault(m => !before.Contains(m))
                                   ?? throw new ToolError($"ReBuzz didn't create {dll.Name}." + (profiled?.NeedsWindow == true ? " " + ParamTools.WindowNote(profiled) : ""));
                        int amp = Levels.DbToAmp(gainDb ?? 0);
                        if (after != null)
                        {
                            var outs = after.Outputs.Where(o => !((MachineCore)o.Destination).Hidden).ToList();
                            foreach (var o in outs)
                            {
                                var dst = o.Destination;
                                int oAmp = o.Amp, oPan = o.Pan, oChannel = o.DestinationChannel;
                                core.SongCore.DisconnectMachines(o);
                                core.SongCore.ConnectMachines(made, dst, new[] { 0 }, new[] { oChannel }, oAmp, oPan);
                            }
                            core.SongCore.ConnectMachines(after, made, new[] { 0 }, new[] { 0 }, amp, Levels.PanCenter);
                        }
                        else if (target != null)
                        {
                            if (!core.SongCore.CanConnectMachines(made, target)) throw new ToolError($"{made.Name} can't connect to {target.Name}.");
                            core.SongCore.ConnectMachines(made, target, new[] { 0 }, new[] { 0 }, amp, Levels.PanCenter);
                        }
                        var ps = SongModel.Params(made);
                        var groups = ps.Where(p => p.Group == 1).Select(p => p.Info.Group).Where(g => g.Length > 0).Distinct().ToList();
                        var manifest = SongModel.Manifest(dll);
                        var added = new JsonObject
                        {
                            ["machine"] = SongModel.MachineJson(core, made),
                            ["parameters"] = ps.Count(p => p.Group == 1),
                            ["groups"] = new JsonArray(groups.Select(g => (JsonNode)g).ToArray()),
                            ["exact_units"] = manifest is { IsDraft: false } && ps.Any(p => p.Manifest != null),
                        };
                        if (manifest != null) added["manifest"] = manifest.Quality;
                        // machine-safety.md §3.4: a warning for machines that diverge; hot ones (loud but steady) don't get one.
                        if (manifest?.Health is { Diverging: true } health)
                        {
                            var ranges = manifest.Parameters.Concat(manifest.TrackParameters).Where(p => p.SafeRange != null).Select(p => $"{p.Name} {p.SafeRange.Text()}").ToList();
                            added["warning"] = $"{dll.Name} diverges (NaN or a runaway level) at some settings: {string.Join("; ", health.Problems.Take(4))}. " +
                                               (ranges.Count > 0 ? $"param_set keeps you inside its safe ranges ({string.Join(", ", ranges)}). " : "") +
                                               "Render and check it, or choose another machine.";
                        }
                        return added;
                    }, c.Cancellation);
                    var m = r["machine"]!;
                    var outs2 = m["outputs"]!.AsArray().Select(o => o!["to"]!.GetValue<string>()).ToList();
                    string groupsText = r["groups"]!.AsArray().Count > 0 ? " Groups: " + string.Join(", ", r["groups"]!.AsArray().Select(g => g!.GetValue<string>())) + "." : "";
                    string units = r["exact_units"]!.GetValue<bool>() ? " with exact units" : r["manifest"]?.GetValue<string>() == "draft" ? " with units from a profiled draft manifest" : "";
                    return ToolResult.Ok($"Added {m["name"]} ({m["gear"]}, {m["kind"]})" + (outs2.Count > 0 ? " → " + string.Join(", ", outs2) : ", not connected") +
                                         $". {r["parameters"]} parameters{units}.{groupsText}" + (r["warning"] != null ? " Warning: " + r["warning"] : ""), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "machine_remove",
                Title = "Remove machines",
                Description =
                    "Deletes machines with their patterns and arrangement lanes. For an effect in a chain, keep_chain (default true) connects " +
                    "what fed it to what it fed, so the sound still reaches Master. One undo step (undo brings everything back). " +
                    "Example: machine_remove(machines=[\"Old reverb\"]).",
                Input = Object(null,
                    Req("machines", Array(String("A machine's name"), "Machines to delete", 1)),
                    Opt("keep_chain", Boolean("Reconnect around removed effects (default true)"))),
                Output = Object(null, Req("removed", Array(String("Name"), "Deleted machines")), Req("reconnected", Array(String("from → to"), "Connections made around them"))),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    var names = c.List("machines")!.Select(n => n?.GetValue<string>()).ToList();
                    bool keep = c.Bool("keep_chain") ?? true;
                    var r = await host.Engine("machine_remove", true, _ =>
                    {
                        var ms = names.Select(n => SongModel.Machine(core, n)).Distinct().ToList();
                        if (ms.Any(SongModel.IsMaster)) throw new ToolError("Master can't be removed.");
                        var gone = ms.Cast<IMachine>().ToHashSet();
                        var reconnected = new JsonArray();
                        if (keep)
                        {
                            foreach (var m in ms.Where(m => SongModel.Kind(m) == "effect"))
                            {
                                var ins = m.Inputs.Where(i => !gone.Contains(i.Source) && !((MachineCore)i.Source).Hidden).ToList();
                                var outs = m.Outputs.Where(o => !gone.Contains(o.Destination) && !((MachineCore)o.Destination).Hidden).ToList();
                                foreach (var i in ins)
                                    foreach (var o in outs)
                                    {
                                        if (SongModel.Connection((MachineCore)i.Source, (MachineCore)o.Destination) != null) continue;
                                        if (!core.SongCore.CanConnectMachines(i.Source, o.Destination)) continue;
                                        core.SongCore.ConnectMachines(i.Source, o.Destination, new[] { 0 }, new[] { o.DestinationChannel }, i.Amp, i.Pan);
                                        reconnected.Add($"{i.Source.Name} → {o.Destination.Name}");
                                    }
                            }
                        }
                        core.SongCore.DeleteMachines(ms);
                        return new JsonObject { ["removed"] = new JsonArray(ms.Select(m => (JsonNode)m.Name).ToArray()), ["reconnected"] = reconnected };
                    }, c.Cancellation);
                    var rec = r["reconnected"]!.AsArray();
                    return ToolResult.Ok($"Removed {string.Join(", ", r["removed"]!.AsArray().Select(x => x!.GetValue<string>()))}." +
                                         (rec.Count > 0 ? " Reconnected " + string.Join(", ", rec.Select(x => x!.GetValue<string>())) + "." : ""), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "machine_set",
                Title = "Rename, mute, solo, bypass",
                Description =
                    "Changes a machine's name, mute, solo, bypass (effects pass the sound through untouched) or track count. One undo step. " +
                    "Mute and solo change the song (ReBuzz's solo: soloing an effect silences the generators). To hear parts alone without " +
                    "changing the song, use render(solo=[…]). reset=true is Reset and unmute for a machine whose guard tripped (machine_list " +
                    "and machine_describe show diverged: NaN, infinity or above +60 dBFS): it is stopped and plays again, and the guard trips " +
                    "again if it is still broken. " +
                    "Example: machine_set(machine=\"Pad\", rename=\"Strings\", mute=false).",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Opt("rename", String("New unique name")),
                    Opt("mute", Boolean("Silence it")),
                    Opt("reset", Boolean("Reset and unmute a diverged machine (its guard tripped)")),
                    Opt("solo", Boolean("Hear only soloed machines")),
                    Opt("bypass", Boolean("Effects: pass the input through")),
                    Opt("tracks", Integer("Number of tracks", 1, 256))),
                Output = Object(null, Req("machine", Object(null, Req("name", String("Its name now"))))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var r = await host.Engine("machine_set", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        string rename = c.String("rename")?.Trim();
                        if (SongModel.IsMaster(m) && (rename != null || c.Bool("mute") != null || c.Bool("solo") != null || c.Bool("bypass") != null || c.Int("tracks") != null))
                            throw new ToolError("Master can't be renamed, muted, soloed, bypassed or given tracks.");
                        // Fork (machine-safety.md §3.2): Reset and unmute is unmuting it.
                        if (c.Bool("reset") == true && m.IsDiverged) AgentActions.SetFlag(core, m, AgentActions.Flag.Mute, false);
                        if (c.Int("tracks") is int t)
                        {
                            int lo = m.DLL.Info.MinTracks, hi = Math.Max(lo, m.DLL.Info.MaxTracks);
                            if (t < lo || t > hi) throw new ToolError($"{m.Name} ({m.DLL.Name}) takes {lo}–{hi} tracks.");
                            if (t != m.TrackCount) AgentActions.Do(core, new SetTrackCountAction(m, t));
                        }
                        if (c.Bool("mute") is bool mute && mute != m.IsMuted) AgentActions.SetFlag(core, m, AgentActions.Flag.Mute, mute);
                        if (c.Bool("solo") is bool solo && solo != m.IsSoloed) AgentActions.SetFlag(core, m, AgentActions.Flag.Solo, solo);
                        if (c.Bool("bypass") is bool bypass && bypass != m.IsBypassed)
                        {
                            if (SongModel.Kind(m) != "effect") throw new ToolError($"{m.Name} is a generator; only effects bypass. Use mute to silence it.");
                            AgentActions.SetFlag(core, m, AgentActions.Flag.Bypass, bypass);
                        }
                        if (rename != null && rename != m.Name)
                        {
                            if (rename.Length == 0) throw new ToolError("rename: give a name.");
                            if (SongModel.Machines(core).Any(x => x != m && string.Equals(x.Name, rename, StringComparison.OrdinalIgnoreCase)))
                                throw new ToolError($"There's already a machine named \"{rename}\".");
                            core.SongCore.RenameMachineUndoable(m, rename);
                        }
                        return new JsonObject { ["machine"] = SongModel.MachineJson(core, m) };
                    }, c.Cancellation);
                    var m2 = r["machine"]!;
                    var flags = new[] { ("muted", "muted"), ("soloed", "soloed"), ("bypassed", "bypassed") }
                        .Where(f => m2[f.Item1]?.GetValue<bool>() == true).Select(f => f.Item2).ToList();
                    return ToolResult.Ok($"{m2["name"]}: {(flags.Count > 0 ? string.Join(", ", flags) : "playing normally")}, {m2["tracks"]} track(s).", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "connect",
                Title = "Connect machines",
                Description =
                    "Connects one machine's output to another's input, with a gain in dB and a pan. Several outputs into one input mix. " +
                    "input picks a second input by name or number (Neo Comp's \"Sidechain\"). One undo step. " +
                    "Example: connect(from=\"Pad\", to=\"Room\", gain_db=-8).",
                Input = Object(null,
                    Req("from", String("Source machine")),
                    Req("to", String("Destination machine")),
                    Opt("gain_db", Db("Gain in dB (default 0; up to +12)")),
                    Opt("pan", Number("Pan from -1 (left) to 1 (right), default 0", -1, 1)),
                    Opt("input", String("The destination's input by name or number (default its first)"))),
                Output = Object(null, Req("from", Object(null, Req("name", String("Name")))), Req("to", Object(null, Req("name", String("Name"))))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    double gain = c.Arguments["gain_db"] != null ? Levels.ParseDb(c.Arguments["gain_db"], "gain_db") : 0;
                    double pan = c.Number("pan") ?? 0;
                    var r = await host.Engine("connect", true, _ =>
                    {
                        var from = SongModel.Machine(core, c.RequiredString("from"), "from");
                        var to = SongModel.Machine(core, c.RequiredString("to"), "to");
                        if (from == to) throw new ToolError("A machine can't connect to itself.");
                        if (SongModel.IsMaster(from)) throw new ToolError("Master is the output; nothing comes out of it to connect.");
                        if (SongModel.Kind(to) == "generator") throw new ToolError($"{to.Name} is a generator and takes no audio input. Connect into an effect or Master.");
                        if (SongModel.Connection(from, to) != null)
                            throw new ToolError($"{from.Name} already feeds {to.Name}. Change its gain or pan with connection_set.");
                        int channel = InputChannel(to, c.String("input"));
                        if (!core.SongCore.CanConnectMachines(from, to))
                            throw new ToolError($"{from.Name} can't connect to {to.Name} (it would make a loop, or one of them has no audio there).");
                        core.SongCore.ConnectMachines(from, to, new[] { 0 }, new[] { channel }, Levels.DbToAmp(gain), Levels.UnitToPan(pan));
                        return new JsonObject { ["from"] = SongModel.MachineJson(core, from), ["to"] = SongModel.MachineJson(core, to) };
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["from"]!["name"]} → {r["to"]!["name"]} at {Levels.Text(gain)}, {Levels.PanText(pan)}.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "disconnect",
                Title = "Disconnect machines",
                Description = "Removes the connection from one machine to another. One undo step. Example: disconnect(from=\"Pad\", to=\"Master\").",
                Input = Object(null, Req("from", String("Source machine")), Req("to", String("Destination machine"))),
                Output = Object(null, Req("from", Object(null, Req("name", String("Name")))), Req("to", Object(null, Req("name", String("Name"))))),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    var r = await host.Engine("disconnect", true, _ =>
                    {
                        var from = SongModel.Machine(core, c.RequiredString("from"), "from");
                        var to = SongModel.Machine(core, c.RequiredString("to"), "to");
                        var conn = SongModel.Connection(from, to)
                                   ?? throw new ToolError($"{from.Name} isn't connected to {to.Name}. Its outputs: " +
                                                          (from.Outputs.Count == 0 ? "none" : string.Join(", ", from.Outputs.Select(o => o.Destination.Name))) + ".");
                        core.SongCore.DisconnectMachines(conn);
                        return new JsonObject { ["from"] = SongModel.MachineJson(core, from), ["to"] = SongModel.MachineJson(core, to) };
                    }, c.Cancellation);
                    bool silent = r["from"]!["outputs"]!.AsArray().Count == 0 && r["from"]!["kind"]!.GetValue<string>() != "master";
                    return ToolResult.Ok($"Disconnected {r["from"]!["name"]} from {r["to"]!["name"]}." + (silent ? $" {r["from"]!["name"]} now feeds nothing, so it isn't heard." : ""), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "connection_set",
                Title = "Set connection gains and pans",
                Description =
                    "Changes the gain (dB) and pan of existing connections, many at once, as one undo step. Faders of the mix. " +
                    "Example: connection_set(changes=[{\"from\": \"Bass\", \"to\": \"Master\", \"gain_db\": -3}, {\"from\": \"Pad\", \"to\": \"Master\", \"pan\": -0.3}]).",
                Input = Object(null, Req("changes", Array(Object("One connection",
                    Req("from", String("Source machine")),
                    Req("to", String("Destination machine")),
                    Opt("gain_db", Db("New gain in dB")),
                    Opt("pan", Number("New pan, -1 … 1", -1, 1))), "Connections to change", 1))),
                Output = Object(null, Req("connections", Array(Object(null, Req("from", String("Source")), Req("to", String("Destination")), Req("gain_db", Number("Gain"))), "As they are now"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var changes = c.List("changes")!;
                    var r = await host.Engine("connection_set", true, _ =>
                    {
                        var plan = new List<(IMachineConnection Conn, int Amp, int Pan)>();
                        for (int i = 0; i < changes.Count; i++)
                        {
                            var ch = changes[i]!.AsObject();
                            var from = SongModel.Machine(core, ch["from"]?.GetValue<string>(), $"changes[{i}].from");
                            var to = SongModel.Machine(core, ch["to"]?.GetValue<string>(), $"changes[{i}].to");
                            var conn = SongModel.Connection(from, to)
                                       ?? throw new ToolError($"changes[{i}]: {from.Name} isn't connected to {to.Name}. Use connect first.");
                            if (ch["gain_db"] == null && ch["pan"] == null) throw new ToolError($"changes[{i}]: give gain_db, pan or both.");
                            int amp = ch["gain_db"] != null ? Levels.DbToAmp(Levels.ParseDb(ch["gain_db"], $"changes[{i}].gain_db")) : conn.Amp;
                            int pan = conn.Pan;
                            if (ch["pan"] != null)
                            {
                                if (!conn.HasPan) throw new ToolError($"changes[{i}]: the connection {from.Name} → {to.Name} has no pan (a mono input).");
                                pan = Levels.UnitToPan(ch["pan"]!.GetValue<double>());
                            }
                            plan.Add((conn, amp, pan));
                        }
                        foreach (var (conn, amp, pan) in plan) AgentActions.SetConnection(core, conn, amp, pan);
                        return new JsonObject
                        {
                            ["connections"] = new JsonArray(plan.Select(p => (JsonNode)new JsonObject
                            {
                                ["from"] = p.Conn.Source.Name,
                                ["to"] = p.Conn.Destination.Name,
                                ["gain_db"] = Levels.Json(Levels.AmpToDb(p.Conn.Amp)),
                                ["pan"] = Levels.PanToUnit(p.Conn.Pan),
                            }).ToArray()),
                        };
                    }, c.Cancellation);
                    return ToolResult.Ok(string.Join("; ", r["connections"]!.AsArray().Select(x => $"{x!["from"]} → {x["to"]}: {x["gain_db"]} dB, pan {x["pan"]}")), r);
                },
            });
        }

        /// <summary>A gain: dB as a number, or text such as "-6 dB" or "-inf" (silent).</summary>
        internal static JsonObject Db(string description) =>
            AnyOf(description, Number("dB, up to +12", null, 12.04), String("Text: \"-6 dB\", or \"-inf\" for silence"));

        /// <summary>The destination input channel for "Sidechain", "2" or null (the first).</summary>
        static int InputChannel(MachineCore to, string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return 0;
            int count = Math.Max(1, to.InputChannelCount);
            if (int.TryParse(input, out int n))
            {
                if (n < 1 || n > count) throw new ToolError($"{to.Name} has {count} input(s); input {n} doesn't exist.");
                return n - 1;
            }
            var names = Enumerable.Range(0, count).Select(i => to.GetChannelName(true, i) ?? $"{i + 1}").ToList();
            int found = names.FindIndex(x => string.Equals(x, input, StringComparison.OrdinalIgnoreCase));
            if (found < 0) throw new ToolError($"{to.Name} has no input \"{input}\". Its inputs: {string.Join(", ", names)}.");
            return found;
        }
    }
}
