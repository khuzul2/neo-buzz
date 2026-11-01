// Agent server (docs/specs/mcp-server.md §5.4): sound design. machine_describe, param_set, param_get.
// Values in units: exact for machines with a manifest (Neo), searched through the machine's own
// value texts for the others.

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
        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "machine_describe",
                Title = "Describe a machine",
                Description =
                    "Everything about one machine's sound: each parameter with its group, current value (text, number and unit), range, default, " +
                    "choices and meaning; track parameters per track; the gear's summary. Read it before param_set. Big machines (Neo FM6 has 172 " +
                    "parameters): pass group to read one section. Example: machine_describe(machine=\"Bass\", group=\"Filter 1\").",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Opt("group", String("Only this parameter group"))),
                Output = Object(null,
                    Req("machine", Object(null, Req("name", String("Name")))),
                    Opt("summary", String("What the gear is")),
                    Req("exact_units", Boolean("Values in units land exactly")),
                    Req("groups", Array(String("Group"), "All parameter groups")),
                    Req("parameters", Array(Object("A parameter",
                        Req("name", String("Pass this to param_set")),
                        Req("value", String("Current value as the machine shows it"))), "Parameters"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    string group = c.String("group");
                    var r = await host.Engine("machine_describe", false, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var all = SongModel.Params(m);
                        var groups = all.Select(p => p.Info.Group).Where(g => g.Length > 0).Distinct().ToList();
                        IEnumerable<AgentParam> shown = all;
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
                        o["exact_units"] = all.Any(p => p.Manifest != null);
                        o["groups"] = new JsonArray(groups.Select(g => (JsonNode)g).ToArray());
                        o["parameters"] = new JsonArray(shown.Select(p => (JsonNode)Describe(m, p)).ToArray());
                        if (all.Any(p => p.IsTrack)) o["tracks"] = m.TrackCount;
                        if (m.ParameterGroups.Count > 2 && m.ParameterGroups[2].Parameters.Any(p => p.Type == BuzzGUI.Interfaces.ParameterType.Note))
                            o["notes"] = "Plays notes: notes_write puts them in patterns, and ReBuzz sends them to it as MIDI. Neo machines and most " +
                                         "managed machines play MIDI; some older native machines ignore it (render and check).";
                        return o;
                    }, c.Cancellation);
                    var ps = r["parameters"]!.AsArray();
                    string line = string.Join("; ", ps.Take(40).Select(p => $"{p!["name"]} = {p["value"]}"));
                    return ToolResult.Ok($"{r["machine"]!["name"]} ({r["machine"]!["gear"]}): {ps.Count} parameters{(group != null ? $" in {group}" : "")}. " +
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
                    "Returns each value as the machine now shows it. " +
                    "Example: param_set(machine=\"Bass\", changes=[{\"param\": \"F1 cutoff\", \"value\": \"800 Hz\"}, {\"param\": \"F1 reso\", \"by\": \"+10%\"}]).",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Req("changes", Array(Object("One change",
                        Req("param", String("Parameter name (or #index for global parameters)")),
                        Opt("value", AnyOf("The new value", String("With its unit, a choice, min/max/default or raw:N"), Number("A number in the parameter's unit"))),
                        Opt("by", AnyOf("Change by this amount", String("With its unit: \"+3 dB\""), Number("In the parameter's unit"))),
                        Opt("times", Number("Multiply the value by this factor", 0)),
                        Opt("track", Integer("Track, for track parameters (from 0)", 0))), "Changes", 1))),
                Output = Object(null, Req("changed", Array(Object(null,
                    Req("param", String("Name")),
                    Req("value", String("As the machine shows it now")),
                    Req("was", String("Before"))), "Each change"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var changes = c.List("changes")!;
                    var r = await host.Engine("param_set", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var all = SongModel.Params(m);
                        var plan = new List<(AgentParam P, int Track, int Raw, string Was)>();
                        for (int i = 0; i < changes.Count; i++)
                        {
                            var ch = changes[i]!.AsObject();
                            string at = $"changes[{i}]";
                            var p = SongModel.Param(m, all, ch["param"]?.GetValue<string>());
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
                            plan.Add((p, track, raw, SongModel.Describe(p.Parameter, p.Parameter.GetValue(track))));
                        }
                        foreach (var (p, track, raw, _) in plan) AgentActions.SetParameter(core, m, p.Parameter, track, raw);
                        var changed = new JsonArray();
                        foreach (var (p, track, raw, was) in plan)
                        {
                            var o = new JsonObject { ["param"] = p.Name };
                            if (p.IsTrack) o["track"] = track;
                            foreach (var kv in SongModel.Value(p, track)) o[kv.Key] = kv.Value?.DeepClone();
                            o["was"] = was;
                            changed.Add(o);
                        }
                        return new JsonObject { ["machine"] = m.Name, ["changed"] = changed };
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]}: " + string.Join("; ", r["changed"]!.AsArray().Select(x =>
                        $"{x!["param"]}{(x["track"] != null ? $" [track {x["track"]}]" : "")} = {x["value"]} (was {x["was"]})")), r);
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
                Output = Object(null, Req("values", Array(Object(null, Req("param", String("Name")), Req("value", String("Shown value"))), "Values"))),
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
                        return new JsonObject { ["machine"] = m.Name, ["values"] = values };
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]}: " + string.Join("; ", r["values"]!.AsArray().Take(60).Select(x => $"{x!["param"]} = {x["value"]}")), r);
                },
            });
        }

        static JsonObject Describe(MachineCore m, AgentParam p)
        {
            var o = new JsonObject { ["name"] = p.Name };
            if (!p.IsTrack) o["index"] = p.Parameter.IndexInGroup;
            if (p.Info.Group.Length > 0) o["group"] = p.Info.Group;
            if (p.IsTrack)
            {
                o["per_track"] = true;
                foreach (var kv in SongModel.Value(p, 0)) o[kv.Key] = kv.Value?.DeepClone();
                if (m.TrackCount > 1)
                    o["track_values"] = new JsonArray(Enumerable.Range(0, m.TrackCount).Select(t => (JsonNode)SongModel.Describe(p.Parameter, p.Parameter.GetValue(t))).ToArray());
            }
            else
            {
                foreach (var kv in SongModel.Value(p, 0)) o[kv.Key] = kv.Value?.DeepClone();
            }
            o["range"] = $"{SongModel.Describe(p.Parameter, p.Info.Min)} … {SongModel.Describe(p.Parameter, p.Info.Max)}";
            o["default"] = SongModel.Describe(p.Parameter, p.Info.Default);
            if (p.Info.Mapping is { Kind: MappingKind.Enum or MappingKind.Switch })
                o["choices"] = new JsonArray(ValueResolver.Choices(p.Info).Select(x => (JsonNode)x).ToArray());
            else if (p.Info.Mapping == null && p.Info.Max - p.Info.Min <= 12 && p.Info.Max > p.Info.Min)
            {
                // A small range without a manifest: its texts are its choices.
                var texts = Enumerable.Range(p.Info.Min, p.Info.Max - p.Info.Min + 1).Select(v => SongModel.Describe(p.Parameter, v)).ToList();
                if (texts.Any(t => !int.TryParse(t, out _))) o["choices"] = new JsonArray(texts.Select(x => (JsonNode)x).ToArray());
            }
            if (!string.IsNullOrWhiteSpace(p.Info.Description) && p.Info.Description != p.Name) o["description"] = p.Info.Description;
            return o;
        }
    }
}
