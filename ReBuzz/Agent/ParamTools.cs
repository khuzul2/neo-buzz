// Agent server (docs/specs/mcp-server.md §5.4): sound design. machine_describe, param_set, param_get.
// Values in units: exact for machines with a manifest (Neo), from a profiled draft manifest where
// there is one (§12.2), searched through the machine's own value texts for the others.
// machine_describe(value_table=true) lists each parameter's texts across its range: what the
// profiler (tools/machine-profiler.py) infers units and mappings from. U2 (docs/specs/machine-safety.md
// §3.4): a parameter's safe range (from its manifest) keeps param_set inside it unless the agent
// passes allow_unsafe; machine_describe shows the ranges.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class ParamTools
    {
        /// <summary>machine_describe answers a machine with more parameters than this, without a group, with its groups only.</summary>
        internal const int LargeMachine = 200;

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "machine_describe",
                Title = "Describe a machine",
                Description =
                    "Everything about one machine's sound: each parameter with its group, current value (text, number and unit), range, default, " +
                    "choices, meaning and safe_range (where the machine was found to diverge: param_set keeps to it); track parameters per track; the gear's summary, its manifest (generated, curated or a profiled draft) and how it takes notes. " +
                    "Curated manifests add each parameter's plain name and role, and the gear's quirks; drafts a suggested_role measured by the profiler. " +
                    "played_parameters are track parameters that hold no value (a drum's trigger): automation_write writes them as events. " +
                    "Read it before param_set. Big machines (Neo FM6 has 172 parameters): pass group to read one section; a machine with more than " + LargeMachine + " parameters (Dye Essence) answers without a group with a short summary and its groups (name, parameter count), never the whole list: read it group by group (all=true reads everything at once, hundreds of KB). value_table=true adds each " +
                    "parameter's texts across its range (every value up to 256 values, else table_points of them), for machines whose texts are all you have. " +
                    "Example: machine_describe(machine=\"Bass\", group=\"Filter 1\").",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Opt("group", String("Only this parameter group")),
                    Opt("all", Boolean("A large machine without group: every parameter at once (default false: the summary and its groups)")),
                    Opt("value_table", Boolean("Also each parameter's value texts across its range: [raw, text] pairs")),
                    Opt("table_points", Integer("value_table: at most this many values per parameter, evenly spaced, ends included (default 1025)", 2, 65536))),
                Output = Object(null,
                    Req("machine", Object(null, Req("name", String("Name")))),
                    Opt("summary", String("What the gear is")),
                    Opt("manifest", String("generated, curated or draft (profiled, not yet curated: units approximate where marked)")),
                    Req("exact_units", Boolean("Values in units land exactly")),
                    Opt("modulations", Array(Object("A modulator driving one of its parameters", Req("source", String("The modulator")), Req("param", String("The parameter")), Req("depth", Integer("Percent")), Opt("reaches", String("Where the modulation reaches")), Opt("base", String("The value you set"))), "Incoming modulations (Neo LFO, Envelope, Step, Macro)")),
                    Opt("modulator", Object("For a Neo LFO, Envelope, Step or Macro: its targets", Req("kind", String("LFO, Envelope, Step or Macro")), Req("targets", Array(Object("A target", Req("target_machine", String("Machine")), Req("param", String("Parameter"))), "Its targets")))),
                    Opt("gear_info", Object("The gear: its file, track range, how it takes notes", Req("file", String("Its DLL, under Gear")))),
                    Opt("quirks", Array(String("A quirk"), "Curated: what to know before using it (track 0 only, needs a wave…)")),
                    Opt("successors", Array(Object("A Neo successor", Req("gear", String("Its gear")), Req("how", String("The call that moves the song onto it"))), "machine_replace moves the machine onto these")),
                    Req("groups", Array(String("Group"), "All parameter groups")),
                    Opt("group_sizes", Array(Object("A group of a large machine", Req("group", String("Its name")), Req("parameters", Integer("How many parameters it has"))), "A large machine without group: each group's parameter count; read one with group")),
                    Opt("parameter_count", Integer("A large machine without group: how many parameters it has in all")),
                    Req("parameters", Array(Object("A parameter",
                        Req("name", String("Pass this to param_set")),
                        Opt("plain_name", String("Curated: what it is in plain English; param_set takes it too")),
                        Opt("role", String("Curated: what it does (filter.cutoff, env.release, mix.wet, drive…)")),
                        Opt("id", String("Explain machines (Neo Learn): the snake_case id; param_set takes it too")),
                        Opt("how", String("Explain machines: how the control changes the sound")),
                        Opt("sound", String("Explain machines: what you will hear")),
                        Opt("curve", String("Explain machines: linear, logarithmic, bipolar, power or choice")),
                        Opt("min", Number("Explain machines: the lowest value, in unit")),
                        Opt("max", Number("Explain machines: the highest value, in unit")),
                        Opt("default_number", Number("Explain machines: the default, in unit")),
                        Opt("suggested_role", String("Draft: the role its profile suggests, from what moving it alone changes (no curated role)")),
                        Opt("role_evidence", String("Draft: what the profile measured behind suggested_role")),
                        Req("value", String("Current value as the machine shows it"))), "Parameters")),
                    Opt("played_parameters", Array(Object("A track parameter that holds no value (a trigger): automation_write writes it as events, each heard once",
                        Req("name", String("Pass this to automation_write (with track)")), Req("range", String("Its values"))), "Played track parameters"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    string group = c.String("group");
                    bool table = c.Bool("value_table") ?? false;
                    bool everything = c.Bool("all") ?? false;
                    int points = c.Int("table_points") ?? 1025;
                    var r = await host.Engine("machine_describe", false, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var all = SongModel.Params(m);
                        var groups = all.Select(p => p.Info.Group).Where(g => g.Length > 0).Distinct().ToList();
                        IEnumerable<AgentParam> shown = all;
                        // A large machine without a group: the groups with their sizes, not every parameter (Dye Essence at 704 was 326 KB).
                        bool overview = group == null && !everything && all.Count > LargeMachine;
                        if (overview) shown = Enumerable.Empty<AgentParam>();
                        if (group != null)
                        {
                            shown = all.Where(p => string.Equals(p.Info.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();
                            if (!shown.Any())
                            {
                                var near = Text.Closest(group, groups);
                                throw new ToolError($"{m.Name} has no group \"{group}\"." + (near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : "") +
                                                    (groups.Count > 0 ? $" Groups: {string.Join(", ", groups)}." : " Its parameters have no groups; omit group."));
                            }
                        }
                        var o = new JsonObject { ["machine"] = SongModel.MachineJson(core, m) };
                        var manifest = SongModel.Manifest(m.DLL);
                        if (!string.IsNullOrEmpty(manifest?.Summary)) o["summary"] = manifest.Summary;
                        if (manifest != null) o["manifest"] = manifest.Quality;
                        if (manifest?.Health is { Status: not "ok" } health)
                        {
                            var h = new JsonObject { ["status"] = health.Status, ["problems"] = new JsonArray(health.Problems.Select(x => (JsonNode)x).ToArray()) };
                            if (health.Diverging) h["diverging"] = true;
                            if (health.Hot) h["hot"] = true;
                            if (health.NeedsWindow) h["needs_window"] = WindowNote(health);
                            o["health"] = h;
                        }
                        o["gear_info"] = GearInfo(m);
                        if (ModulationTools.Incoming(core, m) is JsonArray incoming) o["modulations"] = incoming;
                        if (ModulationTools.Own(core, m) is JsonObject own) o["modulator"] = own;
                        if (manifest?.Quirks.Count > 0) o["quirks"] = new JsonArray(manifest.Quirks.Select(x => (JsonNode)x).ToArray());
                        // U5 (mcp-server.md §12.5): its Neo successors, which machine_replace moves the song onto.
                        if (MachineReplace.SuccessorsJson(core, m.DLL) is JsonArray successors) o["successors"] = successors;
                        if (manifest?.Imports.Count > 0)
                            o["data_imports"] = new JsonArray(manifest.Imports.Select(i => (JsonNode)new JsonObject
                            {
                                ["kind"] = i.Kind,
                                ["extensions"] = new JsonArray(i.Extensions.Select(e => (JsonNode)e).ToArray()),
                                ["about"] = i.Description,
                            }).ToArray());
                        o["exact_units"] = manifest is { IsDraft: false } && all.Any(p => p.Manifest != null);
                        o["groups"] = new JsonArray(groups.Select(g => (JsonNode)g).ToArray());
                        if (overview)
                        {
                            o["parameter_count"] = all.Count;
                            o["group_sizes"] = new JsonArray(groups.Select(g => (JsonNode)new JsonObject
                            {
                                ["group"] = g,
                                ["parameters"] = all.Count(p => p.Info.Group == g),
                            }).ToArray());
                            o["note"] = $"{m.Name} has {all.Count} parameters: pass group to read one group's parameters (machine_describe(machine=\"{m.Name}\", group=\"{(groups.Count > 0 ? groups[0] : "…")}\")), or all=true for every one at once.";
                        }
                        o["parameters"] = new JsonArray(shown.Select(p =>
                        {
                            var d = Describe(m, p);
                            if (table)
                            {
                                // Explain machines (Neo Learn) give min and max in the parameter's unit; the raw ones are min_raw and max_raw.
                                d[ExplainFields.Has(p.Manifest) ? "min_raw" : "min"] = p.Info.Min;
                                d[ExplainFields.Has(p.Manifest) ? "max_raw" : "max"] = p.Info.Max;
                                d["default_raw"] = p.Info.Default;
                                d["table"] = ValueTable(p, points);
                            }
                            return (JsonNode)d;
                        }).ToArray());
                        if (all.Any(p => p.IsTrack)) o["tracks"] = m.TrackCount;
                        // U2 in depth (mcp-server.md §12.12): track parameters that hold no value (a drum's trigger, a note's
                        // volume) are played: automation_write writes them as events, each heard once.
                        var played = SongModel.Params(m, played: true).Where(p => p.Played).ToList();
                        if (played.Count > 0 && group == null)
                        {
                            o["played_parameters"] = new JsonArray(played.Select(p =>
                            {
                                var q = new JsonObject
                                {
                                    ["name"] = p.Name,
                                    ["range"] = $"{p.Info.Describe(p.Info.Min)} … {p.Info.Describe(p.Info.Max)}",
                                    ["min_raw"] = p.Info.Min,
                                    ["max_raw"] = p.Info.Max,
                                };
                                if (!string.IsNullOrWhiteSpace(p.Info.Description) && p.Info.Description != p.Name) q["description"] = p.Info.Description;
                                return (JsonNode)q;
                            }).ToArray());
                            o["tracks"] = m.TrackCount;
                        }
                        if (m.ParameterGroups.Count > 2 && m.ParameterGroups[2].Parameters.Any(p => p.Type == BuzzGUI.Interfaces.ParameterType.Note))
                            o["notes"] = "Plays notes: notes_write puts them in patterns, and " + Product.Name + " sends them to it as MIDI. Neo machines and most " +
                                         "managed machines play MIDI; some older native machines ignore it (render and check).";
                        return o;
                    }, c.Cancellation);
                    var ps = r["parameters"]!.AsArray();
                    string line = string.Join("; ", ps.Take(40).Select(p => $"{p!["name"]} = {p["value"]}"));
                    string successor = r["successors"] is JsonArray su && su.Count > 0
                        ? $" Successor: {string.Join(", ", su.Select(x => x!["gear"]!.GetValue<string>()))} (machine_replace)." : "";
                    if (r["parameter_count"] != null)
                        return ToolResult.Ok($"{r["machine"]!["name"]} ({r["machine"]!["gear"]}): {r["parameter_count"]} parameters in {r["groups"]!.AsArray().Count} groups: " +
                                             string.Join(", ", r["group_sizes"]!.AsArray().Select(g => $"{g!["group"]} ({g["parameters"]})")) +
                                             ". Pass group to read one group's parameters.", r);
                    return ToolResult.Ok($"{r["machine"]!["name"]} ({r["machine"]!["gear"]}): {ps.Count} parameters{(group != null ? $" in {group}" : "")}" +
                                         (r["manifest"]?.GetValue<string>() == "draft" ? " (units from a profiled draft manifest)" : "") + "." + successor + " " +
                                         line + (ps.Count > 40 ? "; …" : ""), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "param_set",
                Title = "Set parameters",
                Description =
                    "Changes parameters, many in one call and one undo step. Each change names a param and gives exactly one of: value " +
                    "(in its unit: \"800 Hz\", \"-6 dB\", \"35%\", \"250 ms\", \"4:1\", a choice such as \"Saw\", min/max/default, or raw:N), " +
                    "by (an amount: \"+3 dB\", \"-200 Hz\", 2 steps of a choice) or times (a factor: 2, 0.5). Track parameters take track (from 0). " +
                    "A parameter with a safe_range (machine_describe shows it: where the machine was found to diverge) refuses values outside it, " +
                    "with the reason, unless you pass allow_unsafe=true. Returns each value as the machine now shows it. " +
                    "Example: param_set(machine=\"Bass\", changes=[{\"param\": \"F1 cutoff\", \"value\": \"800 Hz\"}, {\"param\": \"F1 reso\", \"by\": \"+10%\"}]).",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Req("changes", Array(Object("One change",
                        Req("param", String("Parameter name (a curated manifest's plain name works too, or #index for global parameters)")),
                        Opt("value", AnyOf("The new value", String("With its unit, a choice, min/max/default or raw:N"), Number("A number in the parameter's unit"))),
                        Opt("by", AnyOf("Change by this amount", String("With its unit: \"+3 dB\""), Number("In the parameter's unit"))),
                        Opt("times", Number("Multiply the value by this factor", 0)),
                        Opt("track", Integer("Track, for track parameters (from 0)", 0))), "Changes", 1)),
                    Opt("allow_unsafe", Boolean("Set values outside a parameter's safe range anyway (default false: refused with the reason). Render and check after"))),
                Output = Object(null,
                    Req("changed", Array(Object(null,
                        Req("param", String("Name")),
                        Req("value", String("As the machine shows it now")),
                        Req("was", String("Before")),
                        Opt("unsafe", String("Why the value is outside its safe range (set with allow_unsafe)"))), "Each change")),
                    Opt("manifest", String("draft: units came from a profiled manifest nobody has curated yet"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var changes = c.List("changes")!;
                    bool allowUnsafe = c.Bool("allow_unsafe") ?? false;
                    var r = await host.Engine("param_set", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var all = SongModel.Params(m);
                        var plan = new List<(AgentParam P, int Track, int Raw, string Was, string Unsafe)>();
                        for (int i = 0; i < changes.Count; i++)
                        {
                            var ch = changes[i]!.AsObject();
                            string at = $"changes[{i}]";
                            var p = SongModel.Param(m, all, ch["param"]?.GetValue<string>(), track: ch["track"] != null);
                            int track = 0;
                            if (ch["track"] != null)
                            {
                                track = ch["track"]!.GetValue<int>();
                                if (!p.IsTrack) throw new ToolError($"{at}: {p.Name} is a global parameter; leave track out.");
                            }
                            if (p.IsTrack && track >= m.TrackCount)
                                throw new ToolError($"{at}: {m.Name} has {m.TrackCount} track(s) (0–{m.TrackCount - 1}); add tracks with machine_set(tracks=…).");
                            var forms = new[] { "value", "by", "times" }.Where(k => ch[k] != null).ToList();
                            if (forms.Count != 1) throw new ToolError($"{at} ({p.Name}): give exactly one of value, by or times.");
                            var mode = forms[0] switch { "by" => ValueResolver.Mode.By, "times" => ValueResolver.Mode.Times, _ => ValueResolver.Mode.Value };
                            // Earlier changes in this call to the same parameter count.
                            var earlier = plan.LastOrDefault(x => x.P == p && x.Track == track);
                            int current = earlier.P != null ? earlier.Raw : p.Parameter.GetValue(track);
                            int raw;
                            try { raw = ValueResolver.Resolve(p.Info, ch[forms[0]], current, mode); }
                            catch (ToolError e) { throw new ToolError($"{at}: {e.Message}"); }
                            // machine-safety.md §3.4: outside the safe range only when the agent says so.
                            string refusal = SafeRange.Refusal(p.Info, raw);
                            if (refusal != null && !allowUnsafe) throw new ToolError($"{at}: {refusal}");
                            plan.Add((p, track, raw, p.Info.Describe(p.Parameter.GetValue(track)),
                                      refusal != null ? $"outside its safe range, {p.Info.SafeRange.Text(p.Info.Describe)}: {p.Info.SafeRange.Reason}" : null));
                        }
                        foreach (var (p, track, raw, _, _) in plan) AgentActions.SetParameter(core, m, p.Parameter, track, raw);
                        bool draft = SongModel.Manifest(m.DLL)?.IsDraft == true;
                        var changed = new JsonArray();
                        foreach (var (p, track, raw, was, unsafeWhy) in plan)
                        {
                            var o = new JsonObject { ["param"] = p.Name };
                            if (p.IsTrack) o["track"] = track;
                            foreach (var kv in SongModel.Value(p, track)) o[kv.Key] = kv.Value?.DeepClone();
                            o["was"] = was;
                            if (unsafeWhy != null) o["unsafe"] = unsafeWhy;
                            changed.Add(o);
                        }
                        var o2 = new JsonObject { ["machine"] = m.Name, ["changed"] = changed };
                        if (draft) o2["manifest"] = "draft";
                        return o2;
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]}: " + string.Join("; ", r["changed"]!.AsArray().Select(x =>
                        $"{x!["param"]}{(x["track"] != null ? $" [track {x["track"]}]" : "")} = {x["value"]} (was {x["was"]})" +
                        (x["unsafe"] != null ? $" — unsafe: {x["unsafe"]}" : ""))) + DraftNote(r), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "param_get",
                Title = "Read parameters",
                Description =
                    "Current values of some or all of a machine's parameters (text, number, unit, raw), lighter than machine_describe. " +
                    "Example: param_get(machine=\"Bass\", params=[\"F1 cutoff\", \"Volume\"]).",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Opt("params", Array(String("A parameter name"), "Only these (default all)")),
                    Opt("track", Integer("Track for track parameters (default 0)", 0))),
                Output = Object(null,
                    Req("values", Array(Object(null, Req("param", String("Name")), Req("value", String("Shown value"))), "Values")),
                    Opt("manifest", String("draft: numbers and units come from a profiled manifest nobody has curated yet"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    int track = c.Int("track") ?? 0;
                    var wanted = c.List("params")?.Select(x => x?.GetValue<string>()).ToList();
                    var r = await host.Engine("param_get", false, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var all = SongModel.Params(m);
                        var ps = wanted == null ? all : wanted.Select(n => SongModel.Param(m, all, n)).ToList();
                        if (track > 0 && track >= m.TrackCount) throw new ToolError($"{m.Name} has {m.TrackCount} track(s) (0–{m.TrackCount - 1}).");
                        var values = new JsonArray();
                        foreach (var p in ps)
                        {
                            var o = new JsonObject { ["param"] = p.Name };
                            int t = p.IsTrack ? track : 0;
                            if (p.IsTrack) o["track"] = t;
                            foreach (var kv in SongModel.Value(p, t)) o[kv.Key] = kv.Value?.DeepClone();
                            values.Add(o);
                        }
                        var o2 = new JsonObject { ["machine"] = m.Name, ["values"] = values };
                        if (SongModel.Manifest(m.DLL)?.IsDraft == true) o2["manifest"] = "draft";
                        return o2;
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]}: " + string.Join("; ", r["values"]!.AsArray().Take(60).Select(x => $"{x!["param"]} = {x["value"]}")) + DraftNote(r), r);
                },
            });
        }

        /// <summary>What gear_list and machine_describe say about a machine that needs a window (machine-safety.md §3.1).</summary>
        internal static string WindowNote(ManifestHealth health) =>
            "Works in " + Product.Name + " on Windows with its window" + (health.Window.Length > 0 ? $" ({health.Window})" : "") + "; it can't be built headless.";

        /// <summary>A parameter's safe range as tools show it: the machine's texts, raw values and why.</summary>
        internal static JsonObject SafeRangeJson(SafeRange s, Func<int, string> describe)
        {
            var o = new JsonObject { ["range"] = s.Text(describe), ["min_raw"] = s.Min, ["max_raw"] = s.Max };
            if (s.Reason.Length > 0) o["reason"] = s.Reason;
            return o;
        }

        static string DraftNote(JsonObject r) =>
            r["manifest"]?.GetValue<string>() == "draft" ? " (units from a profiled draft manifest; values marked approximate are read from its descriptions)" : "";

        /// <summary>[raw, text] across a parameter's range: every value up to 256 of them, else <paramref name="points"/> evenly spaced with both ends.</summary>
        static JsonArray ValueTable(AgentParam p, int points)
        {
            int min = p.Info.Min, max = p.Info.Max, span = max - min;
            IEnumerable<int> raws = span < 256 || span < points ? Enumerable.Range(min, span + 1)
                : Enumerable.Range(0, points).Select(i => min + (int)Math.Round(i * (double)span / (points - 1))).Distinct();
            return new JsonArray(raws.Select(raw => (JsonNode)new JsonArray(raw, SongModel.Describe(p.Parameter, raw))).ToArray());
        }

        /// <summary>The gear's file (under Gear), its tracks, and how it can take notes.</summary>
        internal static JsonObject GearInfo(MachineCore m)
        {
            var dll = m.DLL;
            string path = dll?.Path ?? "";
            string file = path.Length == 0 ? "" : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) + "/" + System.IO.Path.GetFileName(path);
            var o = new JsonObject { ["file"] = file, ["managed"] = dll?.IsManaged == true };
            if (dll?.Info != null)
            {
                o["min_tracks"] = dll.Info.MinTracks;
                o["max_tracks"] = Math.Max(dll.Info.MinTracks, dll.Info.MaxTracks);
                // Control machines (peer controllers, LFOs) move other machines' parameters; they make no sound.
                if ((dll.Info.Flags & BuzzGUI.Interfaces.MachineInfoFlags.CONTROL_MACHINE) != 0) o["control"] = true;
            }
            var note = m.ParameterGroups.Count > 2 ? m.ParameterGroups[2].Parameters.FirstOrDefault(x => x.Type == BuzzGUI.Interfaces.ParameterType.Note) : null;
            if (note != null) o["note_parameter"] = note.Name;
            // A managed machine takes MIDI notes when it has MidiNote; a native one can't be told without listening.
            if (m.ManagedMachine is object machine)
                o["takes_midi"] = machine.GetType().GetMethod("MidiNote", new[] { typeof(int), typeof(int), typeof(int) }) != null;
            var notes = SongModel.Manifest(dll)?.Notes;
            if (notes != null)
            {
                o["note_input"] = notes.Input;
                o["pitch_offset"] = notes.Pitched ? notes.PitchOffset : 0;
                if (notes.Polyphony > 0) o["polyphony"] = notes.Polyphony;
            }
            return o;
        }

        static JsonObject Describe(MachineCore m, AgentParam p)
        {
            var o = new JsonObject { ["name"] = p.Name };
            // A curated manifest (U3): what it is in plain English (param_set takes it too) and its role.
            if (p.Manifest is { PlainName.Length: > 0 } named) o["plain_name"] = named.PlainName;
            if (p.Manifest is { Role.Length: > 0 } roled) o["role"] = roled.Role;
            // U2 in depth (mcp-server.md §12.12): a draft's role as the profiler measured it, where nobody has curated one.
            else if (p.Manifest is { SuggestedRole.Length: > 0 } suggested)
            {
                o["suggested_role"] = suggested.SuggestedRole;
                if (suggested.RoleEvidence.Length > 0) o["role_evidence"] = suggested.RoleEvidence;
            }
            if (!p.IsTrack) o["index"] = p.Parameter.IndexInGroup;
            if (p.Info.Group.Length > 0) o["group"] = p.Info.Group;
            if (p.IsTrack)
            {
                o["per_track"] = true;
                foreach (var kv in SongModel.Value(p, 0)) o[kv.Key] = kv.Value?.DeepClone();
                if (m.TrackCount > 1)
                    o["track_values"] = new JsonArray(Enumerable.Range(0, m.TrackCount).Select(t => (JsonNode)p.Info.Describe(p.Parameter.GetValue(t))).ToArray());
            }
            else
            {
                foreach (var kv in SongModel.Value(p, 0)) o[kv.Key] = kv.Value?.DeepClone();
            }
            o["range"] = $"{p.Info.Describe(p.Info.Min)} … {p.Info.Describe(p.Info.Max)}";
            o["default"] = p.Info.Describe(p.Info.Default);
            // Explain (neo-learn.md §5.3): id, unit, min, max, default_number, curve, how and sound, for machines with Explain text.
            ExplainFields.Add(o, p.Manifest);
            if (p.Info.Mapping is { Kind: MappingKind.Enum or MappingKind.Switch })
                o["choices"] = new JsonArray(ValueResolver.Choices(p.Info).Select(x => (JsonNode)x).ToArray());
            else if (p.Info.Mapping == null && p.Info.Max - p.Info.Min <= 12 && p.Info.Max > p.Info.Min)
            {
                // A small range without a manifest: its texts are its choices.
                var texts = Enumerable.Range(p.Info.Min, p.Info.Max - p.Info.Min + 1).Select(v => p.Info.Describe(v)).ToList();
                if (texts.Any(t => !int.TryParse(t, out _))) o["choices"] = new JsonArray(texts.Select(x => (JsonNode)x).ToArray());
            }
            if (!string.IsNullOrWhiteSpace(p.Info.Description) && p.Info.Description != p.Name) o["description"] = p.Info.Description;
            if (p.Manifest?.Approximate == true) o["approximate"] = true;
            if (p.Info.SafeRange != null) o["safe_range"] = SafeRangeJson(p.Info.SafeRange, p.Info.Describe);
            return o;
        }
    }
}
