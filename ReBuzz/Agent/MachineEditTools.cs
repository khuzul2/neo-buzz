// Agent server (agent-knowledge.md K3b, ux-overhaul.md §9 rows 4, 5, 12 and 14): what the studio's machine menu does that had no tool.
// machine_clone (Clone and Clone with patterns; Copy and Paste are the same steps), machine_group (the Group menu), machine_latency
// (the Latency window), machine_attributes and machine_command (the Attributes and Commands menus).
//
// Clone and group call ReBuzz.Core.MachineEditing, which the studio's MachineEdits calls too: one implementation.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class MachineEditTools
    {
        static List<MachineCore> Named(ReBuzzCore core, JsonArray names, string what)
        {
            var list = new List<MachineCore>();
            foreach (var n in names ?? new JsonArray())
            {
                var m = SongModel.Machine(core, n?.GetValue<string>() ?? "");
                if (SongModel.IsMaster(m)) throw new ToolError($"Master can't be {what}.");
                if (!list.Contains(m)) list.Add(m);
            }
            return list;
        }

        static MachineGroupCore GroupNamed(ReBuzzCore core, string name)
        {
            var groups = core.SongCore.MachineGroups.OfType<MachineGroupCore>().ToList();
            return groups.FirstOrDefault(g => g.Name == name) ?? groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ToolError(groups.Count == 0 ? $"There's no group \"{name}\": the song has none (machine_group(op=\"create\", machines=[…]) makes one)."
                       : $"There's no group \"{name}\". Groups: " + string.Join(", ", groups.Select(g => $"\"{g.Name}\"")) + ".");
        }

        /// <summary>Membership as an undo step (the engine's own AddMachineToGroup isn't one): machines into a group, or out of all, and back to where each was.</summary>
        static DelegateAction Membership(ReBuzzCore core, string group, List<string> machines, Dictionary<string, string> previous) => new(
            () => Place(core, machines.ToDictionary(n => n, _ => group)),
            () => Place(core, previous),
            "Group machines");

        static void Place(ReBuzzCore core, Dictionary<string, string> where)
        {
            foreach (var (machine, group) in where)
            {
                var m = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == machine);
                if (m == null) continue;
                var g = group == null ? null : core.SongCore.MachineGroups.OfType<MachineGroupCore>().FirstOrDefault(x => x.Name == group);
                if (g == null) MachineEditing.RemoveFromGroup(core, new[] { m });
                else MachineEditing.AddToGroup(core, new[] { m }, g);
            }
        }

        static Dictionary<string, string> GroupsOf(ReBuzzCore core, IEnumerable<MachineCore> machines) =>
            machines.ToDictionary(m => m.Name, m => core.SongCore.MachineToGroupDict.TryGetValue(m, out var g) ? g.Name : null);

        static JsonObject GroupJson(IMachineGroup g) => new()
        {
            ["group"] = g.Name,
            ["folded"] = g.IsGrouped,
            ["machines"] = new JsonArray(g.Machines.Select(m => (JsonNode)m.Name).ToArray()),
            ["position"] = new JsonArray(Math.Round(g.Position.Item1, 3), Math.Round(g.Position.Item2, 3)),
        };

        static JsonArray AllGroups(ReBuzzCore core) => new(core.SongCore.MachineGroups.Select(g => (JsonNode)GroupJson(g)).ToArray());

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "machine_clone",
                Title = "Clone machines",
                Description =
                    "Makes a copy of each machine with its settings, a little below and right of it (the machine menu's Clone). with_patterns=true is " +
                    "Clone with patterns, and what Copy then Paste does: the copy also gets the original's patterns and its rows in the arrangement, and the " +
                    "wires between the machines cloned together are cloned too. A cut is machine_clone then machine_remove. The Master can't be cloned. " +
                    "One undo step. Example: machine_clone(machines=[\"Lead\", \"Lead Delay\"], with_patterns=true).",
                Input = Object(null,
                    Req("machines", Array(String("A machine"), "The machines to clone", 1)),
                    Opt("with_patterns", Boolean("Also copy patterns, arrangement rows and the wires between the clones (default false)"))),
                Output = Object(null, Req("copies", Array(Object(null, Req("name", String("The copy")), Req("from", String("The original"))), "In the order given")),
                    Req("with_patterns", Boolean("Patterns were copied"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("machine_clone", true, _ =>
                    {
                        var sources = Named(core, c.List("machines"), "cloned");
                        if (sources.Count == 0) throw new ToolError("Name at least one machine.");
                        bool clips = c.Bool("with_patterns") == true;
                        var copies = clips ? MachineEditing.CloneWithClips(core, sources) : MachineEditing.Duplicate(core, sources);
                        if (copies.Count == 0) throw new ToolError($"{Product.Name} made no copy: a machine of this kind can't be cloned (machine_add makes a new one).");
                        var pairs = new JsonArray();
                        for (int i = 0; i < copies.Count && i < sources.Count; i++)
                            pairs.Add(new JsonObject { ["name"] = copies[i].Name, ["from"] = sources[i].Name });
                        return new JsonObject { ["copies"] = pairs, ["with_patterns"] = clips };
                    }, c.Cancellation);
                    return ToolResult.Ok("Cloned " + string.Join(", ", r["copies"]!.AsArray().Select(p => $"{p!["from"]} → {p["name"]}")) +
                                         (r["with_patterns"]!.GetValue<bool>() ? " (with patterns)." : "."), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "machine_group",
                Title = "Machine groups",
                Description =
                    "The machine view's groups: a named frame round machines that can be folded to one box. op: list, create (machines, name), add (machines " +
                    "into group), remove (machines out of their groups), delete (group; its machines stay), rename (group, new_name), fold, unfold. " +
                    "The Master can't be in a group. Each write is one undo step. Example: machine_group(op=\"create\", machines=[\"Kick\", \"Snare\"], name=\"Drums\").",
                Input = Object(null,
                    Req("op", String("What to do", "list", "create", "add", "remove", "delete", "rename", "fold", "unfold")),
                    Opt("group", String("The group (add, delete, rename, fold, unfold)")),
                    Opt("name", String("create: the new group's name (default New Group)")),
                    Opt("new_name", String("rename: the new name")),
                    Opt("machines", Array(String("A machine"), "create, add, remove: the machines", 1))),
                Output = Object(null, Req("groups", Array(Object(null, Req("group", String("Name")), Req("folded", Boolean("Folded to one box")),
                    Req("machines", Array(String("Machine"), "Members")), Req("position", Array(Number("Coordinate"), "Where the group sits"))), "The song's groups now"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string op = c.RequiredString("op");
                    var r = await host.Engine("machine_group", op != "list", _ =>
                    {
                        switch (op)
                        {
                            case "list": break;
                            case "create":
                            {
                                var members = Named(core, c.List("machines"), "grouped");
                                if (members.Count == 0) throw new ToolError("create needs machines.");
                                var previous = GroupsOf(core, members);
                                string name = core.SongCore.GetNewGroupName(c.String("name")?.Trim() is { Length: > 0 } n ? n : "New Group");
                                var g = MachineEditing.NewGroup(core, members, name);
                                if (g == null) throw new ToolError($"{Product.Name} made no group.");
                                // The engine's membership isn't an undo step: this one puts the machines in again on redo and out on undo.
                                AgentActions.Do(core, Membership(core, g.Name, members.Select(m => m.Name).ToList(), previous));
                                break;
                            }
                            case "add":
                            case "remove":
                            {
                                var members = Named(core, c.List("machines"), "grouped");
                                if (members.Count == 0) throw new ToolError($"{op} needs machines.");
                                string group = null;
                                if (op == "add") group = GroupNamed(core, c.String("group") ?? throw new ToolError("add needs group.")).Name;
                                AgentActions.Do(core, Membership(core, group, members.Select(m => m.Name).ToList(), GroupsOf(core, members)));
                                break;
                            }
                            case "delete": MachineEditing.DeleteGroup(core, GroupNamed(core, c.String("group") ?? throw new ToolError("delete needs group."))); break;
                            case "rename":
                            {
                                var g = GroupNamed(core, c.String("group") ?? throw new ToolError("rename needs group."));
                                if (!MachineEditing.RenameGroup(core, g, c.String("new_name") ?? throw new ToolError("rename needs new_name.")))
                                    throw new ToolError("Give a new name that differs from the current one.");
                                break;
                            }
                            case "fold": MachineEditing.Fold(core, GroupNamed(core, c.String("group") ?? throw new ToolError("fold needs group."))); break;
                            case "unfold": MachineEditing.Unfold(core, GroupNamed(core, c.String("group") ?? throw new ToolError("unfold needs group."))); break;
                            default: throw new ToolError("op: list, create, add, remove, delete, rename, fold or unfold.");
                        }
                        return new JsonObject { ["groups"] = AllGroups(core) };
                    }, c.Cancellation);
                    var gs = r["groups"]!.AsArray();
                    return ToolResult.Ok(gs.Count == 0 ? "The song has no groups." : string.Join("; ", gs.Select(g =>
                        $"\"{g!["group"]}\"{(g["folded"]!.GetValue<bool>() ? " (folded)" : "")}: " + string.Join(", ", g["machines"]!.AsArray().Select(m => m!.GetValue<string>())))), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "machine_latency",
                Title = "Machine latency",
                Description =
                    "The delay compensation's inputs (the Latency window): each machine's reported latency in samples and the override, if any. With " +
                    "machine and override_samples (0 or more) the engine compensates for that many samples instead of what the machine reports; " +
                    "clear=true takes the override off that machine, clear_all=true off every machine. Without arguments it lists the machines that " +
                    "report or override a latency. One undo step. Example: machine_latency(machine=\"Lookahead\", override_samples=512).",
                Input = Object(null,
                    Opt("machine", String("The machine (to change it, or to list it alone)")),
                    Opt("override_samples", Integer("Samples of latency to compensate for", 0, 1000000)),
                    Opt("clear", Boolean("Take the override off the machine")),
                    Opt("clear_all", Boolean("Take the override off every machine"))),
                Output = Object(null, Req("machines", Array(Object(null, Req("machine", String("Machine")),
                    Req("reported_samples", Integer("What the machine reports")), Opt("override_samples", Integer("The override; absent when none"))), "Machines with a latency"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    bool write = c.Int("override_samples") != null || c.Bool("clear") == true || c.Bool("clear_all") == true;
                    var r = await host.Engine("machine_latency", write, _ =>
                    {
                        var one = c.String("machine") is string n ? SongModel.Machine(core, n) : null;
                        void SetOverride(string machine, int value)
                        {
                            var m = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == machine);
                            if (m != null) m.OverrideLatency = value;
                        }
                        if (c.Bool("clear_all") == true)
                        {
                            var was = SongModel.Machines(core).Where(m => m.OverrideLatency >= 0).Select(m => (m.Name, m.OverrideLatency)).ToList();
                            if (was.Count == 0) throw new ToolError("No machine has an override.");
                            AgentActions.Do(core, new DelegateAction(() => was.ForEach(w => SetOverride(w.Name, -1)), () => was.ForEach(w => SetOverride(w.Name, w.OverrideLatency)), "Clear latency overrides"));
                        }
                        else if (write)
                        {
                            if (one == null) throw new ToolError("Name the machine (machine=…).");
                            int old = one.OverrideLatency;
                            int value = c.Bool("clear") == true ? -1 : c.Int("override_samples") ?? throw new ToolError("Give override_samples, or clear=true.");
                            string name = one.Name;
                            if (value == old) throw new ToolError(value < 0 ? $"{name} has no override." : $"{name} is already overridden to {value} samples.");
                            AgentActions.Do(core, new DelegateAction(() => SetOverride(name, value), () => SetOverride(name, old), "Machine latency"));
                        }
                        var list = new JsonArray();
                        foreach (var m in SongModel.Machines(core).Where(m => one == null ? (m.Latency != 0 || m.OverrideLatency >= 0) : m == one))
                        {
                            var o = new JsonObject { ["machine"] = m.Name, ["reported_samples"] = m.Latency };
                            if (m.OverrideLatency >= 0) o["override_samples"] = m.OverrideLatency;
                            list.Add(o);
                        }
                        return new JsonObject { ["machines"] = list };
                    }, c.Cancellation);
                    var ms = r["machines"]!.AsArray();
                    return ToolResult.Ok(ms.Count == 0 ? "No machine reports or overrides a latency." : string.Join("; ", ms.Select(m =>
                        $"{m!["machine"]}: {m["reported_samples"]} samples" + (m["override_samples"] != null ? $", overridden to {m["override_samples"]}" : ""))), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "machine_attributes",
                Title = "Machine attributes",
                Description =
                    "A machine's attributes (the machine menu's Attributes: settings classic machines keep outside their parameters, such as a tracker's " +
                    "interpolation or a filter's mode), with their range and default. set changes some by name. One undo step. " +
                    "Example: machine_attributes(machine=\"Tracker\", set={\"Volume Ramp\": 1}).",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Opt("set", Map("Attribute name → new value", Integer("The new value")))),
                Output = Object(null, Req("machine", String("Machine")), Req("attributes", Array(Object(null, Req("name", String("Attribute")),
                    Req("value", Integer("Now")), Req("min", Integer("Lowest")), Req("max", Integer("Highest")), Req("default", Integer("Default"))), "The machine's attributes"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("machine_attributes", c.Object("set") != null, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var attributes = m.Attributes.ToList();
                        if (c.Object("set") is JsonObject set)
                        {
                            var changes = new List<(string Name, int Old, int New)>();
                            foreach (var (key, node) in set)
                            {
                                var a = attributes.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase))
                                        ?? throw new ToolError($"{m.Name} has no attribute \"{key}\"." + (attributes.Count == 0 ? " It has none." : " Its attributes: " + string.Join(", ", attributes.Select(x => $"\"{x.Name}\"")) + "."));
                                if (!Schema.TryNumber(node, out double d)) throw new ToolError($"{a.Name}: give a whole number from {a.MinValue} to {a.MaxValue}.");
                                int v = (int)Math.Round(d);
                                if (v < a.MinValue || v > a.MaxValue) throw new ToolError($"{a.Name}: {v} is outside {a.MinValue} to {a.MaxValue}.");
                                if (v != a.Value) changes.Add((a.Name, a.Value, v));
                            }
                            string machine = m.Name;
                            void Apply(bool undo)
                            {
                                var mm = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == machine);
                                if (mm == null) return;
                                foreach (var ch in changes)
                                    foreach (var a in mm.Attributes.Where(x => x.Name == ch.Name))
                                        a.Value = undo ? ch.Old : ch.New;
                            }
                            if (changes.Count > 0) AgentActions.Do(core, new DelegateAction(() => Apply(false), () => Apply(true), "Machine attributes"));
                        }
                        return new JsonObject
                        {
                            ["machine"] = m.Name,
                            ["attributes"] = new JsonArray(m.Attributes.Select(a => (JsonNode)new JsonObject
                            {
                                ["name"] = a.Name, ["value"] = a.Value, ["min"] = a.MinValue, ["max"] = a.MaxValue, ["default"] = a.DefValue,
                            }).ToArray()),
                        };
                    }, c.Cancellation);
                    var attrs = r["attributes"]!.AsArray();
                    return ToolResult.Ok(attrs.Count == 0 ? $"{r["machine"]} has no attributes." : $"{r["machine"]}: " + string.Join(", ", attrs.Select(a => $"{a!["name"]} = {a["value"]} ({a["min"]}–{a["max"]})")), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "machine_command",
                Title = "Machine commands",
                Description =
                    "A machine's own commands (the machine menu's list a classic or native machine adds: About, Import, Randomize…). Without command it lists them; " +
                    "command runs one by its text (a submenu as \"Menu > Item\"). A command is the machine's own action: it may open a window or change its data, " +
                    "and undo does not take it back (snapshot_save first if it matters). Example: machine_command(machine=\"Matilde Tracker2\", command=\"About\").",
                Input = Object(null, Req("machine", String("The machine")), Opt("command", String("The command to run (its text from the list)"))),
                Output = Object(null, Req("machine", String("Machine")),
                    Req("commands", Array(Object(null, Req("command", String("Text, with its submenu")), Req("enabled", Boolean("Can run now"))), "The machine's commands")),
                    Opt("ran", String("The command that ran"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("machine_command", false, messages =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var all = new List<(string Path, IMenuItem Item)>();
                        void Walk(IEnumerable<IMenuItem> items, string prefix)
                        {
                            foreach (var it in items ?? Enumerable.Empty<IMenuItem>())
                            {
                                if (it.IsSeparator || it.IsLabel && it.Children == null) continue;
                                string path = prefix + it.Text;
                                if (it.Children != null && it.Children.Any()) Walk(it.Children, path + " > ");
                                else all.Add((path, it));
                            }
                        }
                        try { Walk(m.Commands?.ToList(), ""); } catch (Exception) { }
                        string ran = null;
                        if (c.String("command") is string want)
                        {
                            var hit = all.FirstOrDefault(x => string.Equals(x.Path, want, StringComparison.OrdinalIgnoreCase));
                            if (hit.Item == null)
                                throw new ToolError(all.Count == 0 ? $"{m.Name} has no commands." : $"{m.Name} has no command \"{want}\". Its commands: " + string.Join(", ", all.Select(x => $"\"{x.Path}\"")) + ".");
                            if (!hit.Item.IsEnabled || hit.Item.Command == null || !hit.Item.Command.CanExecute(hit.Item.CommandParameter))
                                throw new ToolError($"\"{hit.Path}\" can't run now (the machine has it greyed out).");
                            hit.Item.Command.Execute(hit.Item.CommandParameter);
                            ran = hit.Path;
                        }
                        var o = new JsonObject
                        {
                            ["machine"] = m.Name,
                            ["commands"] = new JsonArray(all.Select(x => (JsonNode)new JsonObject { ["command"] = x.Path, ["enabled"] = x.Item.IsEnabled }).ToArray()),
                        };
                        if (ran != null) o["ran"] = ran;
                        return o;
                    }, c.Cancellation);
                    var cmds = r["commands"]!.AsArray();
                    return ToolResult.Ok(r["ran"] != null ? $"Ran \"{r["ran"]}\" on {r["machine"]}." :
                        cmds.Count == 0 ? $"{r["machine"]} has no commands." : $"{r["machine"]}: " + string.Join(", ", cmds.Select(x => $"\"{x!["command"]}\"")), r);
                },
            });
        }
    }
}
