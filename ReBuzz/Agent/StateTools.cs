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

        /// <summary>
        /// A gear's preset names from its .prs.xml beside the DLL, without a machine of it (for its
        /// manual). Binary .prs files need a machine to read, so they show up in preset_list only.
        /// </summary>
        internal static IReadOnlyList<string> PresetNames(IMachineDLL dll)
        {
            string dir = Path.GetDirectoryName(dll?.Path ?? "");
            if (string.IsNullOrEmpty(dir)) return System.Array.Empty<string>();
            string xml = Path.Combine(dir, dll.Name + ".prs.xml");
            if (!File.Exists(xml)) return System.Array.Empty<string>();
            try
            {
                return System.Xml.Linq.XDocument.Load(xml).Root?.Elements("Item").Select(e => (string)e.Attribute("Key")).Where(k => !string.IsNullOrEmpty(k)).ToList()
                       ?? (IReadOnlyList<string>)System.Array.Empty<string>();
            }
            catch (Exception)
            {
                return System.Array.Empty<string>();
            }
        }

        /// <summary>A preset's category: the part of its name before " — " (or ": "); empty when it has none. The preset bars group by the same.</summary>
        internal static string PresetCategory(string name)
        {
            int i = name.IndexOf(" — ", StringComparison.Ordinal);
            if (i < 0) i = name.IndexOf(": ", StringComparison.Ordinal);
            return i > 0 ? name.Substring(0, i) : "";
        }

        /// <summary>Saves a machine's current sound as a preset of its gear (preset_save, and patch_save_preset for Neo Patch); on the engine's queue.</summary>
        internal static JsonObject SavePreset(MachineCore m, string name, string comment, bool overwrite)
        {
            if (SongModel.IsMaster(m)) throw new ToolError("Master has no presets.");
            if (name.Length == 0 || name == "<default>") throw new ToolError("name: give the preset a name.");
            var pd = Presets(m);
            if (pd.ContainsKey(name) && !overwrite)
                throw new ToolError($"{m.DLL.Name} already has a preset \"{name}\". Pass overwrite=true to replace it, or choose another name.");
            var preset = new Preset(m, false, true) { Comment = comment ?? "" };
            pd[name] = preset;
            try { pd.Save(); }
            catch (Exception e)
            {
                pd.Remove(name);
                throw new ToolError($"Couldn't write {pd.Filename}: {e.Message}. The gear folder may be read-only (install {Product.Name} somewhere writable).");
            }
            presetFiles.TryRemove(pd.Filename, out var stale);
            return new JsonObject { ["machine"] = m.Name, ["preset"] = name, ["file"] = pd.Filename };
        }

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "preset_list",
                Title = "List presets",
                Description =
                    "A machine's presets (its gear's factory presets and ones saved since), with their comments. Filter by text (name or comment) " +
                    "and by category (the part of the name before \" — \": Bass, Pad, Drum…); a long list comes in pages (limit, offset) and the " +
                    "categories with their counts come back with it, so a machine with hundreds of presets is browsed, not dumped. " +
                    "Example: preset_list(machine=\"Pad\", search=\"warm\"), preset_list(machine=\"Dye\", category=\"Bass\", limit=20).",
                Input = Object(null, Req("machine", String("The machine")), Opt("search", String("Text in the name or comment")),
                    Opt("category", String("Only this category: the text before \" — \" in a preset's name (Bass, Pad, Drum, Lo-Fi…; \"Arp/Seq\" takes both); the counts are in categories")),
                    Opt("limit", Integer("At most this many presets (default 60)", 1, 500)),
                    Opt("offset", Integer("Skip this many matches first, to read the next page", 0))),
                Output = Object(null,
                    Req("presets", Array(Object(null, Req("name", String("Pass to preset_load")), Opt("comment", String("What it is")), Opt("category", String("Its category"))), "Presets")),
                    Req("total", Integer("How many matched before limit and offset")),
                    Opt("offset", Integer("Where this page started")),
                    Opt("categories", Array(Object(null, Req("name", String("Category")), Req("count", Integer("Matches in it"))), "The matches' categories with their counts (before the category filter)"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    string search = c.String("search"), category = c.String("category")?.Trim();
                    int limit = c.Int("limit") ?? 60, offset = Math.Max(0, c.Int("offset") ?? 0);
                    var r = await host.Engine("preset_list", false, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var problems = new List<string>();
                        var matches = new List<(string Name, string Comment, string Category)>();
                        foreach (var kv in Presets(m, problems))
                        {
                            string comment = kv.Value?.Comment ?? "";
                            if (search != null && !kv.Key.Contains(search, StringComparison.OrdinalIgnoreCase) && !comment.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                            matches.Add((kv.Key, comment, PresetCategory(kv.Key)));
                        }
                        var categories = new JsonArray();
                        foreach (var g in matches.Where(x => x.Category.Length > 0).GroupBy(x => x.Category))
                            categories.Add(new JsonObject { ["name"] = g.Key, ["count"] = g.Count() });
                        if (!string.IsNullOrEmpty(category))
                        {
                            var wanted = category.Split('/').Select(x => x.Trim()).ToArray();
                            matches = matches.Where(x => wanted.Any(w => string.Equals(w, x.Category, StringComparison.OrdinalIgnoreCase))).ToList();
                        }
                        var list = new JsonArray();
                        foreach (var x in matches.Skip(offset).Take(limit))
                        {
                            var o = new JsonObject { ["name"] = x.Name };
                            if (x.Comment.Length > 0) o["comment"] = x.Comment;
                            if (x.Category.Length > 0) o["category"] = x.Category;
                            list.Add(o);
                        }
                        return new JsonObject
                        {
                            ["machine"] = m.Name, ["presets"] = list, ["total"] = matches.Count, ["offset"] = offset, ["categories"] = categories,
                            ["problems"] = new JsonArray(problems.Select(x => (JsonNode)x).ToArray()),
                        };
                    }, c.Cancellation);
                    var ps = r["presets"]!.AsArray();
                    int total = r["total"]!.GetValue<int>();
                    string filter = (category != null ? $" in category \"{category}\"" : "") + (search != null ? $" matching \"{search}\"" : "");
                    if (total == 0) return ToolResult.Ok($"{r["machine"]} has no presets{filter}.", r);
                    if (ps.Count == 0) return ToolResult.Ok($"{r["machine"]}: {total} presets{filter}; offset {offset} is past the end.", r);
                    string more = offset + ps.Count < total ? $" More: offset={offset + ps.Count}." : "";
                    var cats = r["categories"]!.AsArray();
                    string byCategory = category == null && cats.Count > 1 ? " Categories: " + string.Join(", ", cats.Select(x => $"{x!["name"]} {x["count"]}")) + "." : "";
                    return ToolResult.Ok($"{r["machine"]}: {total} presets{filter}, showing {offset + 1} to {offset + ps.Count}: " +
                        string.Join(", ", ps.Take(40).Select(p => p!["name"]!.GetValue<string>())) + (ps.Count > 40 ? ", …" : "") + "." + more + byCategory, r);
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
                        SavePreset(SongModel.Machine(core, c.RequiredString("machine")), c.RequiredString("name").Trim(), c.String("comment"), c.Bool("overwrite") == true), c.Cancellation);
                    return ToolResult.Ok($"Saved {r["machine"]}'s sound as \"{r["preset"]}\" in {r["file"]}.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "snapshot_save",
                Title = "Save a snapshot",
                Description =
                    "Remembers the whole song under a name: every machine's parameters, state (samples, wavetables, banks), patterns, arrangement, wires and Session layout, " +
                    "stored in a history folder beside the song (<song>.history) so it outlives this session. Save \"before\", change things, render both, and snapshot_restore the better one. " +
                    "kind=\"parameters\" keeps the old light snapshot instead (parameters, mutes and connection gains only, this session only). " +
                    "Snapshots are also taken automatically on save, every few minutes of editing and before big operations, labelled auto. Example: snapshot_save(name=\"mix A\").",
                Input = Object(null, Req("name", String("Snapshot name (replaces a manual one of the same name)")), Opt("kind", String("full: the whole song, kept on disk (default); parameters: parameters, mutes and gains for this session only", "full", "parameters"))),
                Output = Object(null, Req("name", String("Name")), Req("kind", String("full or parameters")), Req("machines", Integer("Machines captured")), Opt("id", String("The snapshot's id (full)")), Opt("megabytes", Number("The song's size before deduplication (full)"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    string name = c.RequiredString("name").Trim();
                    if (Kind(c) == "parameters")
                    {
                        var s = await host.Engine("snapshot_save", false, _ => Snapshot.Take(core, name), c.Cancellation);
                        host.Snapshots[name] = s;
                        var pr = new JsonObject { ["name"] = name, ["kind"] = "parameters", ["machines"] = s.Machines.Count, ["parameters"] = s.Machines.Values.Sum(m => m.Params.Count) };
                        return ToolResult.Ok($"Parameter snapshot \"{name}\": {pr["machines"]} machines, {pr["parameters"]} parameter values.", pr);
                    }
                    var m = await host.Engine("snapshot_save", false, _ =>
                    {
                        var old = core.Snapshots.List().Where(x => !x.IsAuto && x.Name == name).ToList();
                        var made = core.Snapshots.Capture(name) ?? throw new ToolError("The song is loading; try again in a moment.");
                        foreach (var o in old) core.Snapshots.Delete(o.Id);
                        return made;
                    }, c.Cancellation);
                    core.Snapshots.Flush();
                    if (core.Snapshots.LastError is string err && !core.Snapshots.List().Contains(m))
                        throw new ToolError(err);
                    var r = new JsonObject { ["name"] = m.Name, ["kind"] = "full", ["id"] = m.Id, ["machines"] = m.Machines, ["megabytes"] = Math.Round(m.Bytes / 1048576.0, 2) };
                    return ToolResult.Ok($"Snapshot \"{m.Name}\" ({m.Id}): {m.Machines} machines, kept in {core.Snapshots.Folder}.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "snapshot_list",
                Title = "List snapshots",
                Description =
                    "The song's snapshots, oldest first: id, name, time (UTC), trigger (manual, or auto: on save, every few minutes, before replace, restore, import or a big history jump) and kind. " +
                    "Full snapshots live in the song's history folder; parameter snapshots (kind=\"parameters\") last this session. Without kind both are listed. Example: snapshot_list(kind=\"full\").",
                Input = Object(null, Opt("kind", String("Which snapshots (default: both)", "full", "parameters"))),
                Output = Object(null, Req("snapshots", Array(Object(null, Req("name", String("Name")), Req("taken", String("Time"))), "Snapshots"))),
                Annotations = ToolAnnotations.Read,
                Handler = c =>
                {
                    string kind = c.String("kind");
                    var list = new JsonArray();
                    if (kind != "parameters")
                    {
                        core.Snapshots.Flush();
                        foreach (var s in core.Snapshots.List())
                            list.Add(new JsonObject { ["id"] = s.Id, ["name"] = s.Name, ["kind"] = "full", ["trigger"] = s.IsAuto ? "auto" : "manual", ["reason"] = s.Reason, ["taken"] = s.Time.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["machines"] = s.Machines, ["megabytes"] = Math.Round(s.Bytes / 1048576.0, 2) });
                    }
                    if (kind != "full")
                        foreach (var s in host.Snapshots.Values.OrderBy(s => s.Taken))
                            list.Add(new JsonObject { ["name"] = s.Name, ["kind"] = "parameters", ["trigger"] = "manual", ["taken"] = s.Taken.ToString("HH:mm:ss"), ["machines"] = s.Machines.Count });
                    return System.Threading.Tasks.Task.FromResult(ToolResult.Ok(list.Count == 0 ? "No snapshots yet; snapshot_save makes one."
                        : "Snapshots: " + string.Join(", ", list.Select(s => (s!["trigger"]!.GetValue<string>() == "auto" ? "(auto) " : "") + s["name"]!.GetValue<string>())) + ".", new JsonObject { ["snapshots"] = list }));
                },
            });

            tools.Add(new ToolDef
            {
                Name = "snapshot_restore",
                Title = "Restore a snapshot",
                Description =
                    "Puts the whole song back as a full snapshot had it (name or id from snapshot_list). Destructive: the song is replaced as opening a file does, so the undo history starts again; " +
                    "first it takes an auto snapshot of the song as it is (\"Auto: before restoring …\"), which is the way back. Only while the transport is stopped " +
                    "(glitch-free restore while playing comes next). kind=\"parameters\" instead puts every machine's parameters, mutes and connection gains back (machines added since are left alone; removed ones are reported; one undo step). " +
                    "Example: snapshot_restore(name=\"mix A\").",
                Input = Object(null, Req("name", String("Snapshot name, or id")), Opt("kind", String("full or parameters (default: the full snapshot of that name, else the parameter one)", "full", "parameters"))),
                Output = Object(null, Req("kind", String("full or parameters")), Req("changed", Integer("Differences put back (full: listed by snapshot_diff)")), Opt("missing", Array(String("Machine"), "Machines no longer in the song (parameters)"))),
                Annotations = new ToolAnnotations(Destructive: true, Idempotent: true),
                Handler = async c =>
                {
                    string name = c.RequiredString("name");
                    string kind = Kind(c);
                    if (kind == null)
                        kind = core.Snapshots.Find(name) != null || !host.Snapshots.ContainsKey(name) ? "full" : "parameters";
                    if (kind == "full")
                    {
                        var m = FindFull(host, name);
                        var r = await host.Engine("snapshot_restore", false, _ =>
                        {
                            int changes = core.Snapshots.Diff(m, null).Count;
                            try { core.Snapshots.Restore(m); }
                            catch (InvalidOperationException e) { throw new ToolError(e.Message); }
                            return new JsonObject { ["kind"] = "full", ["name"] = m.Name, ["changed"] = changes };
                        }, c.Cancellation);
                        return ToolResult.Ok($"Restored \"{m.Name}\" ({r["changed"]} differences from the song before). The undo history starts again; \"Auto: before restoring {m.Name}\" holds the song as it was.", r);
                    }
                    var s = Find(host, name);
                    var pr = await host.Engine("snapshot_restore", true, _ =>
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
                        return new JsonObject { ["kind"] = "parameters", ["name"] = s.Name, ["changed"] = changed, ["missing"] = missing };
                    }, c.Cancellation);
                    var miss = pr["missing"]!.AsArray();
                    return ToolResult.Ok($"Restored \"{pr["name"]}\": {pr["changed"]} values put back." +
                                         (miss.Count > 0 ? " Not in the song any more: " + string.Join(", ", miss.Select(x => x!.GetValue<string>())) + "." : ""), pr);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "snapshot_diff",
                Title = "Compare snapshots",
                Description =
                    "What differs between two snapshots, or between a snapshot and the song now. Full snapshots: machines added, removed or replaced, wires and gains, parameters (as the machines show them), " +
                    "patterns edited, arrangement and waves, tempo. Parameter snapshots: parameters, mutes and connection gains per machine. " +
                    "Example: snapshot_diff(a=\"mix A\") compares with now; snapshot_diff(a=\"mix A\", b=\"mix B\").",
                Input = Object(null, Req("a", String("A snapshot (name or id)")), Opt("b", String("Another snapshot (default: the song now)")), Opt("kind", String("full or parameters (default: the full snapshot of that name, else the parameter one)", "full", "parameters"))),
                Output = Object(null, Req("machines", Array(Object(null, Req("machine", String("Name")), Req("changes", Array(String("A change"), "Changes"))), "Machines that differ")),
                    Opt("changes", Array(Object(null, Req("area", String("machines, wires, parameters, patterns, sequence, waves or song")), Req("subject", String("What it is about")), Req("text", String("What changed"))), "Full snapshots: every change, going from a to b"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    string kind = Kind(c);
                    string aName = c.RequiredString("a");
                    if (kind == null)
                        kind = core.Snapshots.Find(aName) != null || !host.Snapshots.ContainsKey(aName) ? "full" : "parameters";
                    if (kind == "full")
                    {
                        var fa = FindFull(host, aName);
                        string fb = c.String("b");
                        var fbm = fb != null ? FindFull(host, fb) : null;
                        var fr = await host.Engine("snapshot_diff", false, _ =>
                        {
                            var changes = core.Snapshots.Diff(fa, fbm);
                            var machinesJson = new JsonArray(changes.GroupBy(x => x.Subject).Select(g => (JsonNode)new JsonObject
                            {
                                ["machine"] = g.Key,
                                ["changes"] = new JsonArray(g.Select(x => (JsonNode)(x.Area + ": " + x.Text)).ToArray()),
                            }).ToArray());
                            return new JsonObject
                            {
                                ["a"] = fa.Name, ["b"] = fbm?.Name ?? "now", ["machines"] = machinesJson,
                                ["changes"] = new JsonArray(changes.Select(x => (JsonNode)new JsonObject { ["area"] = x.Area, ["subject"] = x.Subject, ["text"] = x.Text }).ToArray()),
                            };
                        }, c.Cancellation);
                        var ch = fr["changes"]!.AsArray();
                        return ToolResult.Ok(ch.Count == 0 ? $"\"{fr["a"]}\" and {fr["b"]} are the same." :
                            $"{ch.Count} changes: " + string.Join("; ", ch.Take(12).Select(x => $"{x!["subject"]}: {x["text"]}")) + (ch.Count > 12 ? "; …" : ""), fr);
                    }
                    var a = Find(host, aName);
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

            tools.Add(new ToolDef
            {
                Name = "snapshot_delete",
                Title = "Delete a snapshot",
                Description =
                    "Deletes a snapshot for good (name or id from snapshot_list); chunks no other snapshot holds are removed from the history folder. " +
                    "Auto snapshots can be deleted too. kind=\"parameters\" deletes a session parameter snapshot. Example: snapshot_delete(name=\"mix A\").",
                Input = Object(null, Req("name", String("Snapshot name, or id")), Opt("kind", String("full or parameters (default: the full snapshot of that name, else the parameter one)", "full", "parameters"))),
                Output = Object(null, Req("deleted", String("The name deleted")), Req("kind", String("full or parameters"))),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    string name = c.RequiredString("name");
                    string kind = Kind(c);
                    if (kind == null)
                        kind = core.Snapshots.Find(name) != null || !host.Snapshots.ContainsKey(name) ? "full" : "parameters";
                    if (kind == "parameters")
                    {
                        Find(host, name);
                        host.Snapshots.TryRemove(name, out _);
                        return ToolResult.Ok($"Deleted the parameter snapshot \"{name}\".", new JsonObject { ["deleted"] = name, ["kind"] = "parameters" });
                    }
                    var m = FindFull(host, name);
                    await host.Engine("snapshot_delete", false, _ => core.Snapshots.Delete(m.Id), c.Cancellation);
                    return ToolResult.Ok($"Deleted the snapshot \"{m.Name}\" ({m.Id}).", new JsonObject { ["deleted"] = m.Name, ["kind"] = "full" });
                },
            });
        }

        /// <summary>kind: "full", "parameters" or null (not given).</summary>
        static string Kind(ToolContext c)
        {
            string k = c.String("kind");
            if (k == null) return null;
            if (k is "full" or "parameters") return k;
            throw new ToolError("kind is \"full\" or \"parameters\".");
        }

        static ReBuzz.Core.SongHistory.SnapshotManifest FindFull(AgentHost host, string name)
        {
            var snaps = host.Core.Snapshots;
            snaps.Flush();
            return snaps.Find(name) ?? throw new ToolError($"No snapshot \"{name}\"." + (snaps.List().Count == 0 ? " Make one with snapshot_save." : " snapshot_list shows them: " + string.Join(", ", snaps.List().Where(s => !s.IsAuto).Select(s => s.Name).Take(10)) + "."));
        }

        static Snapshot Find(AgentHost host, string name)
        {
            if (host.Snapshots.TryGetValue(name, out var s)) return s;
            throw new ToolError($"No snapshot \"{name}\"." + (host.Snapshots.IsEmpty ? " Make one with snapshot_save." : $" Snapshots: {string.Join(", ", host.Snapshots.Keys)}."));
        }
    }
}
