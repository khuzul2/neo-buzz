// Agent server (docs/specs/mcp-server.md §5.4, A2): sound states. Presets (ReBuzz's own
// .prs.xml/.prs files beside each machine) and whole-song snapshots of every machine's parameters,
// connection gains and mutes, for comparing mixes and sounds. preset_list, preset_load,
// preset_save, snapshot_save, snapshot_restore, snapshot_diff, snapshot_list.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    /// <summary>Every machine's sound-defining state at one moment (by machine name).</summary>
    internal sealed class Snapshot
    {
        public sealed class Param
        {
            public int Group, Index, Track, Value;
            public string Name;
        }

        public sealed class Machine
        {
            public string Gear;
            public bool Muted, Bypassed;
            public List<Param> Params = new();
            /// <summary>Destination name → (amp, pan).</summary>
            public Dictionary<string, (int Amp, int Pan)> Outputs = new();
        }

        public string Name;
        public DateTime Taken;
        public Dictionary<string, Machine> Machines = new();

        public static Snapshot Take(ReBuzzCore core, string name)
        {
            var s = new Snapshot { Name = name, Taken = DateTime.Now };
            foreach (var m in SongModel.Machines(core).Where(m => !SongModel.IsMaster(m)))
            {
                var ms = new Machine { Gear = m.DLL?.Name ?? "", Muted = m.IsMuted, Bypassed = m.IsBypassed };
                for (int g = 1; g <= 2 && g < m.ParameterGroups.Count; g++)
                {
                    var group = m.ParameterGroups[g];
                    int tracks = g == 1 ? 1 : group.TrackCount;
                    for (int t = 0; t < tracks; t++)
                        foreach (var p in group.Parameters)
                            if ((p.Flags & ParameterFlags.State) != 0 && p.Type != ParameterType.Internal)
                                ms.Params.Add(new Param { Group = g, Index = p.IndexInGroup, Track = t, Name = p.Name, Value = p.GetValue(t) });
                }
                foreach (var o in m.Outputs.Where(o => !((MachineCore)o.Destination).Hidden))
                    ms.Outputs[o.Destination.Name] = (o.Amp, o.Pan);
                s.Machines[m.Name] = ms;
            }
            return s;
        }
    }

    internal static class StateTools
    {
        static readonly ConcurrentDictionary<string, (DateTime Written, PresetDictionary Presets)> presetFiles = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The machine's presets: its gear's .prs.xml and .prs beside the DLL, read without dialogs.</summary>
        static PresetDictionary Presets(MachineCore m, List<string> problems = null)
        {
            string dir = Path.GetDirectoryName(m.DLL?.Path ?? "");
            if (string.IsNullOrEmpty(dir)) return new PresetDictionary();
            string xml = Path.Combine(dir, m.DLL.Name + ".prs.xml"), prs = Path.Combine(dir, m.DLL.Name + ".prs");
            DateTime written = new[] { xml, prs }.Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            if (presetFiles.TryGetValue(xml, out var cached) && cached.Written == written) return cached.Presets;
            PresetDictionary pd = null;
            foreach (var file in new[] { xml, prs }.Where(File.Exists))
            {
                try
                {
                    var loaded = PresetDictionary.Load(m, file);
                    if (loaded == null) continue;
                    if (pd == null) pd = loaded;
                    else pd.Merge(loaded);
                }
                catch (Exception e) { problems?.Add($"Couldn't read {Path.GetFileName(file)}: {e.Message}"); }
            }
            pd ??= new PresetDictionary();
            pd.Filename = xml;
            presetFiles[xml] = (written, pd);
            return pd;
        }

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "preset_list",
                Title = "List presets",
                Description =
                    "A machine's presets (its gear's factory presets and ones saved since), with their comments. Optionally filtered by text. " +
                    "Example: preset_list(machine=\"Pad\", search=\"warm\").",
                Input = Object(null, Req("machine", String("The machine")), Opt("search", String("Text in the name or comment"))),
                Output = Object(null, Req("presets", Array(Object(null, Req("name", String("Pass to preset_load")), Opt("comment", String("What it is"))), "Presets"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    string search = c.String("search");
                    var r = await host.Engine("preset_list", false, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var problems = new List<string>();
                        var list = new JsonArray();
                        foreach (var kv in Presets(m, problems))
                        {
                            string comment = kv.Value?.Comment ?? "";
                            if (search != null && !kv.Key.Contains(search, StringComparison.OrdinalIgnoreCase) && !comment.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                            var o = new JsonObject { ["name"] = kv.Key };
                            if (comment.Length > 0) o["comment"] = comment;
                            list.Add(o);
                        }
                        return new JsonObject { ["machine"] = m.Name, ["presets"] = list, ["problems"] = new JsonArray(problems.Select(x => (JsonNode)x).ToArray()) };
                    }, c.Cancellation);
                    var ps = r["presets"]!.AsArray();
                    return ToolResult.Ok(ps.Count == 0 ? $"{r["machine"]} has no presets{(search != null ? $" matching \"{search}\"" : "")}."
                        : $"{r["machine"]}: {ps.Count} presets: " + string.Join(", ", ps.Take(40).Select(p => p!["name"]!.GetValue<string>())) + (ps.Count > 40 ? ", …" : "") + ".", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "preset_load",
                Title = "Load a preset",
                Description =
                    "Sets a machine to one of its presets (all its parameters, and its data such as a wavetable when the preset has it). " +
                    "A good start for sound design: load the closest preset, then param_set. One undo step. " +
                    "Example: preset_load(machine=\"Pad\", preset=\"Warm strings\").",
                Input = Object(null, Req("machine", String("The machine")), Req("preset", String("The preset's name (preset_list)"))),
                Output = Object(null, Req("preset", String("Loaded")), Req("changed", Integer("Parameters that changed"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("preset_load", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        string name = c.RequiredString("preset");
                        var pd = Presets(m);
                        string key = pd.Keys.FirstOrDefault(k => k == name) ?? pd.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
                        if (key == null)
                        {
                            var near = Text.Closest(name, pd.Keys);
                            throw new ToolError($"{m.Name} ({m.DLL.Name}) has no preset \"{name}\"." + (near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : "") +
                                                (pd.Count == 0 ? " It has no presets at all." : " preset_list shows them."));
                        }
                        var after = pd[key];
                        var before = new Preset(m, false, true);
                        string machine = m.Name;
                        void Apply(Preset p)
                        {
                            var target = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == machine);
                            if (target != null) p.Apply(target, true);
                        }
                        AgentActions.Do(core, new DelegateAction(() => Apply(after), () => Apply(before)));
                        var now = new Preset(m, false, false);
                        int changed = now.Parameters.Zip(before.Parameters, (a, b) => a.Value != b.Value).Count(x => x);
                        return new JsonObject { ["machine"] = m.Name, ["preset"] = key, ["changed"] = changed, ["comment"] = after.Comment ?? "" };
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]}: loaded \"{r["preset"]}\" ({r["changed"]} parameters changed)." +
                                         (r["comment"]!.GetValue<string>().Length > 0 ? $" {r["comment"]}" : ""), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "preset_save",
                Title = "Save a preset",
                Description =
                    "Saves a machine's current sound as a preset of its gear, in the gear's preset file beside the machine (shared by every song). " +
                    "Won't replace an existing preset unless overwrite is true. Not undoable (it writes a file). " +
                    "Example: preset_save(machine=\"Bass\", name=\"Agent acid bass\", comment=\"Resonant, short decay\").",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Req("name", String("Preset name")),
                    Opt("comment", String("What it sounds like")),
                    Opt("overwrite", Boolean("Replace a preset of that name"))),
                Output = Object(null, Req("preset", String("Saved")), Req("file", String("The preset file"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var r = await host.Engine("preset_save", false, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        if (SongModel.IsMaster(m)) throw new ToolError("Master has no presets.");
                        string name = c.RequiredString("name").Trim();
                        if (name.Length == 0 || name == "<default>") throw new ToolError("name: give the preset a name.");
                        var pd = Presets(m);
                        if (pd.ContainsKey(name) && c.Bool("overwrite") != true)
                            throw new ToolError($"{m.DLL.Name} already has a preset \"{name}\". Pass overwrite=true to replace it, or choose another name.");
                        var preset = new Preset(m, false, true) { Comment = c.String("comment") ?? "" };
                        pd[name] = preset;
                        try { pd.Save(); }
                        catch (Exception e)
                        {
                            pd.Remove(name);
                            throw new ToolError($"Couldn't write {pd.Filename}: {e.Message}. The gear folder may be read-only (install ReBuzz somewhere writable).");
                        }
                        presetFiles.TryRemove(pd.Filename, out var stale);
                        return new JsonObject { ["machine"] = m.Name, ["preset"] = name, ["file"] = pd.Filename };
                    }, c.Cancellation);
                    return ToolResult.Ok($"Saved {r["machine"]}'s sound as \"{r["preset"]}\" in {r["file"]}.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "snapshot_save",
                Title = "Save a snapshot",
                Description =
                    "Remembers every machine's parameters, mutes and connection gains under a name, for A/B comparison: save \"before\", change things, " +
                    "render both, and snapshot_restore the better one. Snapshots last for this session. Example: snapshot_save(name=\"mix A\").",
                Input = Object(null, Req("name", String("Snapshot name (replaces one of the same name)"))),
                Output = Object(null, Req("name", String("Name")), Req("machines", Integer("Machines captured"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    string name = c.RequiredString("name").Trim();
                    var s = await host.Engine("snapshot_save", false, _ => Snapshot.Take(core, name), c.Cancellation);
                    host.Snapshots[name] = s;
                    var r = new JsonObject { ["name"] = name, ["machines"] = s.Machines.Count, ["parameters"] = s.Machines.Values.Sum(m => m.Params.Count) };
                    return ToolResult.Ok($"Snapshot \"{name}\": {r["machines"]} machines, {r["parameters"]} parameter values.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "snapshot_list",
                Title = "List snapshots",
                Description = "This session's snapshots with when they were taken. Example: snapshot_list().",
                Input = Object(null),
                Output = Object(null, Req("snapshots", Array(Object(null, Req("name", String("Name")), Req("taken", String("Time"))), "Snapshots"))),
                Annotations = ToolAnnotations.Read,
                Handler = c =>
                {
                    var list = new JsonArray(host.Snapshots.Values.OrderBy(s => s.Taken)
                        .Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["taken"] = s.Taken.ToString("HH:mm:ss"), ["machines"] = s.Machines.Count }).ToArray());
                    return System.Threading.Tasks.Task.FromResult(ToolResult.Ok(list.Count == 0 ? "No snapshots yet; snapshot_save makes one."
                        : "Snapshots: " + string.Join(", ", list.Select(s => s!["name"]!.GetValue<string>())) + ".", new JsonObject { ["snapshots"] = list }));
                },
            });

            tools.Add(new ToolDef
            {
                Name = "snapshot_restore",
                Title = "Restore a snapshot",
                Description =
                    "Puts every machine in a snapshot back to its saved parameters, mutes and connection gains. Machines added since are left " +
                    "alone; machines removed since are reported. One undo step. Example: snapshot_restore(name=\"mix A\").",
                Input = Object(null, Req("name", String("Snapshot name"))),
                Output = Object(null, Req("changed", Integer("Values put back")), Req("missing", Array(String("Machine"), "Machines no longer in the song"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var s = Find(host, c.RequiredString("name"));
                    var r = await host.Engine("snapshot_restore", true, _ =>
                    {
                        int changed = 0;
                        var missing = new JsonArray();
                        foreach (var (name, ms) in s.Machines)
                        {
                            var m = SongModel.Machines(core).FirstOrDefault(x => x.Name == name);
                            if (m == null || m.DLL?.Name != ms.Gear) { missing.Add(name); continue; }
                            foreach (var p in ms.Params)
                            {
                                if (p.Group >= m.ParameterGroups.Count) continue;
                                var group = m.ParameterGroups[p.Group];
                                if (p.Index >= group.Parameters.Count || (p.Group == 2 && p.Track >= group.TrackCount)) continue;
                                var param = group.Parameters[p.Index];
                                if (param.GetValue(p.Track) == p.Value) continue;
                                AgentActions.SetParameter(core, m, param, p.Track, p.Value);
                                changed++;
                            }
                            if (m.IsMuted != ms.Muted) { AgentActions.SetFlag(core, m, AgentActions.Flag.Mute, ms.Muted); changed++; }
                            if (m.IsBypassed != ms.Bypassed) { AgentActions.SetFlag(core, m, AgentActions.Flag.Bypass, ms.Bypassed); changed++; }
                            foreach (var (to, (amp, pan)) in ms.Outputs)
                            {
                                var conn = m.Outputs.FirstOrDefault(o => o.Destination.Name == to);
                                if (conn == null || (conn.Amp == amp && conn.Pan == pan)) continue;
                                AgentActions.SetConnection(core, conn, amp, pan);
                                changed++;
                            }
                        }
                        return new JsonObject { ["name"] = s.Name, ["changed"] = changed, ["missing"] = missing };
                    }, c.Cancellation);
                    var miss = r["missing"]!.AsArray();
                    return ToolResult.Ok($"Restored \"{r["name"]}\": {r["changed"]} values put back." +
                                         (miss.Count > 0 ? " Not in the song any more: " + string.Join(", ", miss.Select(x => x!.GetValue<string>())) + "." : ""), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "snapshot_diff",
                Title = "Compare snapshots",
                Description =
                    "What differs between two snapshots, or between a snapshot and the song now: parameters (as the machines show them), mutes and " +
                    "connection gains, per machine. Example: snapshot_diff(a=\"mix A\") compares with now; snapshot_diff(a=\"mix A\", b=\"mix B\").",
                Input = Object(null, Req("a", String("A snapshot")), Opt("b", String("Another snapshot (default: the song now)"))),
                Output = Object(null, Req("machines", Array(Object(null, Req("machine", String("Name")), Req("changes", Array(String("A change"), "Changes"))), "Machines that differ"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var a = Find(host, c.RequiredString("a"));
                    string bName = c.String("b");
                    var b = bName != null ? Find(host, bName) : null;
                    var r = await host.Engine("snapshot_diff", false, _ =>
                    {
                        var other = b ?? Snapshot.Take(core, "now");
                        var machines = new JsonArray();
                        foreach (var name in a.Machines.Keys.Union(other.Machines.Keys).OrderBy(x => x))
                        {
                            var changes = new List<string>();
                            a.Machines.TryGetValue(name, out var ma);
                            other.Machines.TryGetValue(name, out var mb);
                            if (ma == null) changes.Add($"only in {(b == null ? "the song now" : b.Name)}");
                            else if (mb == null) changes.Add($"only in {a.Name}");
                            else
                            {
                                var live = SongModel.Machines(core).FirstOrDefault(x => x.Name == name);
                                string Show(Snapshot.Param p, int v) =>
                                    live != null && p.Group < live.ParameterGroups.Count && p.Index < live.ParameterGroups[p.Group].Parameters.Count
                                        ? SongModel.Describe(live.ParameterGroups[p.Group].Parameters[p.Index], v) : v.ToString();
                                var bParams = mb.Params.ToDictionary(p => (p.Group, p.Index, p.Track));
                                foreach (var p in ma.Params)
                                    if (bParams.TryGetValue((p.Group, p.Index, p.Track), out var q) && q.Value != p.Value)
                                        changes.Add($"{p.Name}{(p.Group == 2 ? $" [track {p.Track}]" : "")}: {Show(p, p.Value)} → {Show(q, q.Value)}");
                                if (ma.Muted != mb.Muted) changes.Add(mb.Muted ? "muted" : "unmuted");
                                if (ma.Bypassed != mb.Bypassed) changes.Add(mb.Bypassed ? "bypassed" : "not bypassed");
                                foreach (var (to, (amp, pan)) in ma.Outputs)
                                    if (mb.Outputs.TryGetValue(to, out var o2) && (o2.Amp != amp || o2.Pan != pan))
                                        changes.Add($"→ {to}: {Levels.Text(Levels.AmpToDb(amp))}, pan {Levels.PanToUnit(pan)} → {Levels.Text(Levels.AmpToDb(o2.Amp))}, pan {Levels.PanToUnit(o2.Pan)}");
                            }
                            if (changes.Count > 0)
                                machines.Add(new JsonObject { ["machine"] = name, ["changes"] = new JsonArray(changes.Select(x => (JsonNode)x).ToArray()) });
                        }
                        return new JsonObject { ["a"] = a.Name, ["b"] = b?.Name ?? "now", ["machines"] = machines };
                    }, c.Cancellation);
                    var ms = r["machines"]!.AsArray();
                    return ToolResult.Ok(ms.Count == 0 ? $"\"{r["a"]}\" and {r["b"]} are the same." :
                        string.Join("; ", ms.Select(m => $"{m!["machine"]}: " + string.Join(", ", m["changes"]!.AsArray().Take(8).Select(x => x!.GetValue<string>())) +
                                                         (m["changes"]!.AsArray().Count > 8 ? ", …" : ""))), r);
                },
            });
        }

        static Snapshot Find(AgentHost host, string name)
        {
            if (host.Snapshots.TryGetValue(name, out var s)) return s;
            throw new ToolError($"No snapshot \"{name}\"." + (host.Snapshots.IsEmpty ? " Make one with snapshot_save." : $" Snapshots: {string.Join(", ", host.Snapshots.Keys)}."));
        }
    }
}
