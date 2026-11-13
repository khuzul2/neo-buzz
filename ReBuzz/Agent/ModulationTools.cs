// Agent server (docs/specs/neo-patch.md §3, phase NM): the canvas modulators. modulate points a Neo LFO, Neo Envelope, Neo Step or Neo
// Macro at a parameter of any machine; modulation_list says what drives what; modulation_remove takes a target away (its parameter goes
// back to the value you set). machine_describe lists a machine's incoming modulations through Incoming. A target is a modulation: the
// modulator writes it smoothly, it is no undo step of its own; the call that adds or removes a target is one undo step.
//
// ModulationHost.Install (called when the engine starts) gives the modulators the safe ranges of the machines' manifests.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class ModulationHost
    {
        /// <summary>Gives the modulators (NeoBuzz.Machine/ModResolver.cs) the manifests' safe ranges: where a machine was found to diverge, a modulator stays inside.</summary>
        public static void Install() => ModulationBoard.SafeRange = Lookup;

        static (int Min, int Max)? Lookup(IMachine machine, IParameter parameter)
        {
            var manifest = SongModel.Manifest(machine.DLL);
            var entry = manifest?.FindAt(parameter.Group?.Type == ParameterGroupType.Track, parameter.IndexInGroup, parameter.Name);
            if (entry?.SafeRange is not { } range) return null;
            if (entry.Min != parameter.MinValue || entry.Max != parameter.MaxValue) return null;   // a stale manifest: trust the machine
            return (range.Min, range.Max);
        }
    }

    internal static class ModulationTools
    {
        static List<(MachineCore Machine, IModulator Modulator)> Modulators(ReBuzzCore core) =>
            core.SongCore.MachinesList.Select(m => (Machine: m, Modulator: m.ManagedMachine as IModulator)).Where(x => x.Modulator != null).ToList();

        static (MachineCore Machine, IModulator Modulator) Source(ReBuzzCore core, string name)
        {
            var m = SongModel.Machine(core, name);
            if (m.ManagedMachine is IModulator mod) return (m, mod);
            var all = Modulators(core);
            throw new ToolError($"{m.Name} is not a modulator (it is {m.DLL?.Name}). " + (all.Count == 0
                ? "Add one with machine_add(gear=\"Neo LFO\"), \"Neo Envelope\", \"Neo Step\" or \"Neo Macro\"."
                : "The modulators in this song: " + string.Join(", ", all.Select(x => $"{x.Machine.Name} ({x.Modulator.Kind})")) + "."));
        }

        static string PolarityText(ModulationInfo i) => i.Bipolar ? "bi" : "uni";

        static JsonObject InfoJson(MachineCore source, ModulationInfo i, MachineCore target)
        {
            var o = new JsonObject
            {
                ["source"] = source.Name,
                ["kind"] = i.Kind,
                ["slot"] = i.Slot,
                ["target_machine"] = i.Machine,
                ["param"] = i.Param,
                ["depth"] = i.Depth,
                ["polarity"] = PolarityText(i),
                ["state"] = i.State switch
                {
                    ModulationState.Active => "active",
                    ModulationState.MachineMissing => "machine missing",
                    ModulationState.ParamMissing => "parameter missing",
                    _ => "refused",
                },
            };
            if (i.IsTrack) o["track"] = i.Track;
            if (i.Curve != 0) o["curve"] = i.Curve;
            var p = target == null ? null : SongModel.Params(target).FirstOrDefault(x => x.Name == i.Param && x.IsTrack == i.IsTrack);
            string D(int raw) => p != null ? p.Info.Describe(raw) : raw.ToString();
            if (i.State == ModulationState.Active)
            {
                o["base"] = D(i.Base);
                o["base_raw"] = i.Base;
                o["reaches"] = $"{D(i.Low)} … {D(i.High)}";
                o["now"] = D(i.Current);
                if (i.RangeMin != int.MinValue && i.RangeMax != int.MinValue) o["range"] = $"{D(i.RangeMin)} … {D(i.RangeMax)}";
                if (i.SafeMin > i.Min || i.SafeMax < i.Max) o["safe_range"] = $"{D(i.SafeMin)} … {D(i.SafeMax)}";
            }
            return o;
        }

        /// <summary>The modulations driving <paramref name="m"/>'s parameters (null: none), for machine_describe.</summary>
        internal static JsonArray Incoming(ReBuzzCore core, MachineCore m)
        {
            var list = ModulationBoard.Incoming(m);
            if (list.Count == 0) return null;
            return new JsonArray(list.Select(i =>
            {
                var src = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == i.Source);
                return (JsonNode)InfoJson(src ?? m, i, m);
            }).ToArray());
        }

        /// <summary>What a modulator machine itself drives and listens to (null for any other machine), for machine_describe.</summary>
        internal static JsonObject Own(ReBuzzCore core, MachineCore m)
        {
            if (m.ManagedMachine is not IModulator mod) return null;
            var o = new JsonObject { ["kind"] = mod.Kind, ["max_targets"] = ModulationBoard.MaxTargets, ["output"] = Math.Round(mod.Output, 3) };
            if (!string.IsNullOrEmpty(mod.ListenMachine)) o["listens_to"] = mod.ListenMachine;
            o["targets"] = new JsonArray(mod.Targets.Select(i => (JsonNode)InfoJson(m, i, core.SongCore.MachinesList.FirstOrDefault(x => x.Name == i.Machine))).ToArray());
            return o;
        }

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "modulate",
                Title = "Modulate a parameter",
                Description =
                    "Points a modulator (a Neo LFO, Neo Envelope, Neo Step or Neo Macro machine) at a parameter of any machine, effects included: up to 8 targets per " +
                    "modulator. The parameter keeps the value you set (its base); the modulator adds depth × its output on top, smoothed, never past the parameter's " +
                    "range or its safe range, with no undo step per write (this call is one undo step). Removing the target puts the base back. depth is −100 … +100 % " +
                    "of the range (50 is a good start); polarity uni adds 0 … depth, bi swings ± depth around the base (default bi for an LFO, uni otherwise); range " +
                    "{min, max} in the parameter's unit confines the swing to part of its range; curve −100 … +100 bends the response. Call again for the same target to change it. " +
                    "listen_machine (and listen_track) says which machine's notes retrigger an LFO or start an envelope, or whose output level an envelope in Follower mode follows; " +
                    "give it with or without a target. Set the modulator's own rate, shape and so on with param_set. Example: modulate(source=\"LFO\", target_machine=\"Bass\", " +
                    "param=\"F1 cutoff\", depth=30).",
                Input = Object(null,
                    Req("source", String("The modulator machine (Neo LFO, Neo Envelope, Neo Step or Neo Macro)")),
                    Opt("target_machine", String("The machine whose parameter it moves")),
                    Opt("param", String("The parameter (its name, #index or id, as param_set takes it)")),
                    Opt("depth", Number("−100 … +100 % of the range (default 50)", -100, 100)),
                    Opt("polarity", String("uni: adds 0 … depth. bi: swings ± depth around the base", "uni", "bi")),
                    Opt("range", Object("Confine the swing to this part of the range, in the parameter's unit",
                        Req("min", AnyOf("Lowest value", String("With its unit"), Number("A number in the parameter's unit"))),
                        Req("max", AnyOf("Highest value", String("With its unit"), Number("A number in the parameter's unit"))))),
                    Opt("curve", Number("−100 … +100: bends the response (0 straight)", -100, 100)),
                    Opt("track", Integer("Track, for a track parameter (from 0)", 0)),
                    Opt("slot", Integer("The modulator's slot to put it in (0–7; default: the same target again, else the first free)", 0, 7)),
                    Opt("listen_machine", String("The machine whose notes or output level the modulator listens to (empty: none)")),
                    Opt("listen_track", Integer("Only this track's notes (default: every track)", -1))),
                Output = Object(null,
                    Req("source", String("The modulator")),
                    Opt("target", Object("The target as it is now",
                        Req("source", String("The modulator")), Req("slot", Integer("Its slot")), Req("target_machine", String("The machine")), Req("param", String("The parameter")),
                        Req("depth", Integer("Percent")), Req("polarity", String("uni or bi")), Req("state", String("active when it drives the parameter")))),
                    Opt("listens_to", String("The machine it listens to")),
                    Opt("warnings", Array(String("A warning"), "Things to know"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("modulate", true, _ =>
                    {
                        var (sm, mod) = Source(core, c.RequiredString("source"));
                        var o = new JsonObject { ["source"] = sm.Name };
                        var warnings = new JsonArray();
                        string listen = c.String("listen_machine");
                        string tname = c.String("target_machine");
                        if (listen != null)
                        {
                            if (listen.Length > 0) SongModel.Machine(core, listen);
                            string was = mod.ListenMachine; int wasTrack = mod.ListenTrack;
                            int track = c.Int("listen_track") ?? -1;
                            AgentActions.Do(core, new DelegateAction(
                                () => { mod.ListenMachine = listen; mod.ListenTrack = track; },
                                () => { mod.ListenMachine = was; mod.ListenTrack = wasTrack; }));
                            if (listen.Length > 0) o["listens_to"] = listen;
                        }
                        if (tname == null)
                        {
                            if (listen == null) throw new ToolError("modulate: give target_machine and param, or listen_machine.");
                            return o;
                        }
                        var tm = SongModel.Machine(core, tname);
                        var all = SongModel.Params(tm);
                        var p = SongModel.Param(tm, all, c.RequiredString("param"), track: c.Int("track") != null);
                        int track0 = p.IsTrack ? (c.Int("track") ?? 0) : 0;
                        if (p.IsTrack && track0 >= tm.TrackCount) throw new ToolError($"{tm.Name} has {tm.TrackCount} track(s) (0–{tm.TrackCount - 1}).");
                        if (!p.IsTrack && c.Int("track") != null) throw new ToolError($"{p.Name} is a global parameter; leave track out.");

                        // What is there already (the same target again changes it).
                        var existing = mod.Targets.FirstOrDefault(i => i.Machine == tm.Name && i.Param == p.Name && i.IsTrack == p.IsTrack && i.Track == track0
                                                                     && (c.Int("slot") is not int s0 || s0 == i.Slot));
                        var info = new ModulationInfo
                        {
                            Depth = (int)Math.Round(c.Number("depth") ?? existing?.Depth ?? 50),
                            Bipolar = c.String("polarity") is string pol ? pol == "bi" : existing?.Bipolar ?? mod.Kind == "LFO",
                            Curve = (int)Math.Round(c.Number("curve") ?? existing?.Curve ?? 0),
                            RangeMin = existing?.RangeMin ?? int.MinValue,
                            RangeMax = existing?.RangeMax ?? int.MinValue,
                        };
                        if (c.Object("range") is { } rng)
                        {
                            int current = p.Parameter.GetValue(track0);
                            info.RangeMin = ValueResolver.Resolve(p.Info, rng["min"], current);
                            info.RangeMax = ValueResolver.Resolve(p.Info, rng["max"], current);
                            if (info.RangeMin > info.RangeMax) (info.RangeMin, info.RangeMax) = (info.RangeMax, info.RangeMin);
                        }
                        if (Math.Abs(info.Depth) > 100) throw new ToolError("depth is −100 … +100.");
                        int slot = c.Int("slot") ?? existing?.Slot ?? -1;

                        // One undo step: the target as it was (or not there), and as it is.
                        string tmName = tm.Name, pName = p.Name;
                        bool isTrack = p.IsTrack;
                        void Apply(ModulationInfo what, int at)
                        {
                            var m2 = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == tmName);
                            var p2 = m2 == null ? null : ModulationTools.FindParam(m2, isTrack, pName);
                            if (m2 == null || p2 == null) return;
                            string refusal = mod.SetTarget(at, m2, p2, track0, what, out int _placed);
                            if (refusal != null) throw new ToolError(refusal);
                        }
                        // The call's own check, so a refusal is the agent's error, not an undo step.
                        string check = mod.SetTarget(slot, tm, p.Parameter, track0, info, out int placed);
                        if (check != null) throw new ToolError("modulate: " + check);
                        var before = existing;
                        bool first = true;   // the check above already did it
                        AgentActions.Do(core, new DelegateAction(
                            () => { if (first) first = false; else Apply(info, placed); },
                            () =>
                            {
                                if (before != null) Apply(before, placed); else mod.RemoveTarget(placed);
                            }));
                        var now = mod.Targets.First(i => i.Slot == placed);
                        o["target"] = InfoJson(sm, now, tm);
                        if (now.SafeMin > now.Min || now.SafeMax < now.Max)
                            warnings.Add($"{tm.Name} {p.Name} has a safe range ({p.Info.Describe(now.SafeMin)} … {p.Info.Describe(now.SafeMax)}): the modulation stays inside it.");
                        if (mod.Kind == "Envelope" && string.IsNullOrEmpty(mod.ListenMachine))
                            warnings.Add($"{sm.Name} has nothing to listen to: set listen_machine (its notes, or its output in Follower mode), or write its Gate switch.");
                        if (mod.Kind == "LFO" && info.Depth != 0 && !now.Bipolar && now.Base + (now.High - now.Low) > now.Max)
                            warnings.Add("The swing reaches the top of the range: lower the base or the depth to keep it from clipping.");
                        if (warnings.Count > 0) o["warnings"] = warnings;
                        return o;
                    }, c.Cancellation);
                    string text = r["target"] is JsonObject t
                        ? $"{r["source"]} → {t["target_machine"]} {t["param"]}: depth {t["depth"]} %, {t["polarity"]}, slot {t["slot"]}" +
                          (t["base"] != null ? $"; base {t["base"]}, reaches {t["reaches"]}" : "") +
                          (r["warnings"] is JsonArray w && w.Count > 0 ? " — " + string.Join(" ", w.Select(x => x!.GetValue<string>())) : "")
                        : $"{r["source"]} listens to {r["listens_to"]?.ToString() ?? "nothing"}.";
                    return ToolResult.Ok(text, r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "modulation_list",
                Title = "List modulations",
                Description =
                    "What drives what: every modulator (Neo LFO, Neo Envelope, Neo Step, Neo Macro) with its targets, each with its depth, polarity, the base value you set, " +
                    "where the modulation reaches, its value now, its range and safe range, and its state (a target whose machine was deleted shows as machine missing). " +
                    "Pass machine to see only the modulations that reach one machine's parameters, or one modulator's targets. machine_describe also lists a machine's incoming modulations.",
                Input = Object(null,
                    Opt("machine", String("A modulator, or a machine to list its incoming modulations (default: all)"))),
                Output = Object(null,
                    Req("modulators", Array(Object("A modulator",
                        Req("name", String("Its name")), Req("kind", String("LFO, Envelope, Step or Macro")),
                        Req("targets", Array(Object("A target", Req("slot", Integer("Slot")), Req("target_machine", String("Machine")), Req("param", String("Parameter")),
                            Req("depth", Integer("Percent")), Req("polarity", String("uni or bi")), Req("state", String("active, machine missing…"))), "Its targets"))), "Modulators"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    string filter = c.String("machine");
                    var r = await host.Engine("modulation_list", false, _ =>
                    {
                        var list = Modulators(core);
                        MachineCore only = filter == null ? null : SongModel.Machine(core, filter);
                        var arr = new JsonArray();
                        foreach (var (m, mod) in list)
                        {
                            bool isSource = only != null && ReferenceEquals(only, m);
                            var targets = mod.Targets.Where(i => only == null || isSource || i.Machine == only.Name).ToList();
                            if (only != null && !isSource && targets.Count == 0) continue;
                            var o = new JsonObject { ["name"] = m.Name, ["kind"] = mod.Kind, ["output"] = Math.Round(mod.Output, 3) };
                            if (!string.IsNullOrEmpty(mod.ListenMachine)) o["listens_to"] = mod.ListenMachine;
                            o["targets"] = new JsonArray(targets.Select(i => (JsonNode)InfoJson(m, i, core.SongCore.MachinesList.FirstOrDefault(x => x.Name == i.Machine))).ToArray());
                            arr.Add(o);
                        }
                        return new JsonObject { ["modulators"] = arr };
                    }, c.Cancellation);
                    var mods = r["modulators"]!.AsArray();
                    int n = mods.Sum(x => x!["targets"]!.AsArray().Count);
                    return ToolResult.Ok(mods.Count == 0
                        ? "No modulators" + (filter != null ? $" reach {filter}" : " in the song") + ". Add one with machine_add(gear=\"Neo LFO\") and modulate."
                        : $"{mods.Count} modulator(s), {n} target(s): " + string.Join("; ", mods.Select(x => $"{x!["name"]} ({x["kind"]}): " +
                            (x["targets"]!.AsArray().Count == 0 ? "no targets" : string.Join(", ", x["targets"]!.AsArray().Select(t => $"{t!["target_machine"]} {t["param"]} {t["depth"]}%"))))), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "modulation_remove",
                Title = "Remove a modulation",
                Description =
                    "Takes a target off a modulator, or all of them: the parameter goes back to the value you set (its base). Give slot, or target_machine (with param to take just that " +
                    "parameter), or all=true. One undo step; undo puts the modulation back.",
                Input = Object(null,
                    Req("source", String("The modulator machine")),
                    Opt("slot", Integer("The target's slot (0–7)", 0, 7)),
                    Opt("target_machine", String("Remove the targets on this machine")),
                    Opt("param", String("With target_machine: only this parameter")),
                    Opt("all", Boolean("Remove every target"))),
                Output = Object(null,
                    Req("source", String("The modulator")),
                    Req("removed", Array(Object("A removed target", Req("slot", Integer("Slot")), Req("target_machine", String("Machine")), Req("param", String("Parameter"))), "What was removed"))),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    var r = await host.Engine("modulation_remove", true, _ =>
                    {
                        var (sm, mod) = Source(core, c.RequiredString("source"));
                        var targets = mod.Targets.ToList();
                        List<ModulationInfo> pick;
                        if (c.Int("slot") is int slot)
                        {
                            pick = targets.Where(i => i.Slot == slot).ToList();
                            if (pick.Count == 0) throw new ToolError($"{sm.Name} has no target in slot {slot}.");
                        }
                        else if (c.String("target_machine") is string tn)
                        {
                            string param = c.String("param");
                            pick = targets.Where(i => string.Equals(i.Machine, tn, StringComparison.OrdinalIgnoreCase) && (param == null || string.Equals(i.Param, param, StringComparison.OrdinalIgnoreCase))).ToList();
                            if (pick.Count == 0) throw new ToolError($"{sm.Name} has no target on {tn}{(param != null ? " " + param : "")}. modulation_list shows its targets.");
                        }
                        else if (c.Bool("all") == true) pick = targets;
                        else throw new ToolError("modulation_remove: give slot, target_machine or all=true.");
                        var removed = new JsonArray();
                        foreach (var t in pick)
                        {
                            var before = t;
                            string tm = t.Machine;
                            int at = t.Slot;
                            AgentActions.Do(core, new DelegateAction(
                                () => mod.RemoveTarget(at),
                                () =>
                                {
                                    var m2 = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == tm);
                                    var p2 = m2 == null ? null : FindParam(m2, before.IsTrack, before.Param);
                                    if (m2 != null && p2 != null) mod.SetTarget(at, m2, p2, before.Track, before, out int _placed2);
                                }));
                            removed.Add(new JsonObject { ["slot"] = t.Slot, ["target_machine"] = t.Machine, ["param"] = t.Param });
                        }
                        return new JsonObject { ["source"] = sm.Name, ["removed"] = removed };
                    }, c.Cancellation);
                    var rem = r["removed"]!.AsArray();
                    return ToolResult.Ok($"{r["source"]}: removed {rem.Count} target(s)" + (rem.Count > 0 ? ": " + string.Join(", ", rem.Select(x => $"{x!["target_machine"]} {x["param"]}")) : "") +
                                         "; the parameters are back at the values you set.", r);
                },
            });
        }

        internal static IParameter FindParam(MachineCore m, bool isTrack, string name) =>
            SongModel.Params(m).FirstOrDefault(x => x.IsTrack == isTrack && x.Name == name)?.Parameter;
    }
}
