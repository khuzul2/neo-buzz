// Agent server (docs/specs/mcp-server.md §5.4–5.6, A3): full composition. Song markers (named
// sections), automation (curves the engine plays from a pattern), the Session View's grid,
// machine data imports (a wavetable for Neo Wave, a DX7 bank for Neo FM6) and renders turned
// into samples. arrangement_markers, automation_write, automation_read, session_get,
// session_clip_set, session_scene_set, machine_data_import, wave_from_render.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using static ReBuzz.Agent.Mcp.Schema;
using SongTime = ReBuzz.Agent.Model.SongTime;

namespace ReBuzz.Agent
{
    internal static class CompositionTools
    {
        static JsonObject Position(string description) =>
            AnyOf(description, Number("Beats (0 = the start)", 0), String("\"bar.beat\": \"5.1\" is bar 5's first beat"));

        /// <summary>A marker's range: from it to the next marker (or the song end), in ticks.</summary>
        internal static (int From, int To) Section(ReBuzzCore core, string name)
        {
            var all = core.MarkerStore?.All ?? System.Array.Empty<(string, int)>();
            int i = all.ToList().FindIndex(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
                throw new ToolError($"No marker \"{name}\"." + (all.Count > 0 ? $" Markers: {string.Join(", ", all.Select(m => m.Name))}." : " Set them with arrangement_markers."));
            int from = all[i].Tick;
            int to = i + 1 < all.Count ? all[i + 1].Tick : core.SongCore.SongEnd;
            if (to <= from) throw new ToolError($"Marker \"{all[i].Name}\" is at or after the song end; there's nothing to its right.");
            return (from, to);
        }

        static JsonArray MarkersJson(ReBuzzCore core)
        {
            var all = core.MarkerStore.All;
            var list = new JsonArray();
            for (int i = 0; i < all.Count; i++)
            {
                int end = i + 1 < all.Count ? all[i + 1].Tick : core.SongCore.SongEnd;
                double at = SongModel.Beats(core, all[i].Tick);
                list.Add(new JsonObject
                {
                    ["name"] = all[i].Name,
                    ["at"] = at,
                    ["bar"] = SongTime.BarBeat(at, SongModel.BeatsPerBar(core)),
                    ["length_beats"] = Math.Max(0, SongModel.Beats(core, end) - at),
                });
            }
            return list;
        }

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            // theory (pure) and notes_transform, whose engine is pure and whose notes live in the pattern.
            Model.CompositionTools.Register(tools, async c =>
            {
                var r = await host.Engine("notes_transform", true, _ =>
                {
                    var m = SongModel.Machine(core, c.RequiredString("machine"));
                    var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                    int tpb = SongModel.Tpb(core);
                    var input = MachineNotes.For(m);
                    var before = MachineNotes.Read(p, input);
                    var outcome = NotesTransform.Run(c.Arguments, before, tpb, p.Length, SongModel.BeatsPerBar(core));
                    var removed = before.Except(outcome.Notes).ToList();
                    var added = outcome.Notes.Except(before).ToList();
                    if (removed.Count > 0 || added.Count > 0) MachineNotes.Replace(core, p, input, removed, added, outcome.Problems);
                    outcome.Problems.AddRange(NoteList.Clashes(outcome.Notes.OrderBy(n => n.Time).ToList(), tpb));
                    return (Machine: m.Name, Pattern: p.Name, Outcome: outcome, Tpb: tpb);
                }, c.Cancellation);
                return NotesTransform.Result(r.Machine, r.Pattern, r.Outcome, r.Tpb);
            });

            tools.Add(new ToolDef
            {
                Name = "arrangement_markers",
                Title = "Song sections",
                Description =
                    "Lists, sets or removes named sections of the song (\"Intro\", \"Verse 1\", \"Drop\"). A section runs from its marker to the next. " +
                    "render(section=\"Drop\") renders one. Saved in the song. One undo step. " +
                    "Example: arrangement_markers(set=[{\"name\": \"Intro\", \"at\": 0}, {\"name\": \"Verse\", \"at\": \"9.1\"}]).",
                Input = Object(null,
                    Opt("set", Array(Object("A marker", Req("name", String("Section name")), Req("at", Position("Where it starts"))), "Markers to add or move")),
                    Opt("remove", Array(String("A marker's name"), "Markers to remove"))),
                Output = Object(null, Req("markers", Array(Object(null, Req("name", String("Name")), Req("at", Number("Beats")), Req("length_beats", Number("Up to the next marker"))), "All markers"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var set = c.List("set");
                    var remove = c.List("remove")?.Select(x => x!.GetValue<string>()).ToList();
                    bool changes = set?.Count > 0 || remove?.Count > 0;
                    var r = await host.Engine("arrangement_markers", changes, _ =>
                    {
                        var store = core.MarkerStore ?? throw new ToolError("This ReBuzz has no song markers.");
                        if (changes)
                        {
                            var before = store.All.ToList();
                            var edits = new List<(string Name, int Tick)>();
                            for (int i = 0; i < (set?.Count ?? 0); i++)
                            {
                                var o = set[i]!.AsObject();
                                string name = o["name"]?.GetValue<string>()?.Trim();
                                if (string.IsNullOrEmpty(name)) throw new ToolError($"set[{i}].name: give the section a name.");
                                edits.Add((name, SongModel.ParseTicks(core, o["at"], $"set[{i}].at")));
                            }
                            foreach (var name in remove ?? new List<string>())
                                if (!before.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) && !edits.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                                    throw new ToolError($"No marker \"{name}\" to remove." + (before.Count > 0 ? $" Markers: {string.Join(", ", before.Select(m => m.Name))}." : ""));
                            var after = before.ToList();
                            foreach (var (name, tick) in edits)
                            {
                                after.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                                after.Add((name, tick));
                            }
                            foreach (var name in remove ?? new List<string>())
                                after.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                            AgentActions.Do(core, new DelegateAction(() => store.Replace(after), () => store.Replace(before)));
                        }
                        return new JsonObject { ["markers"] = MarkersJson(core) };
                    }, c.Cancellation);
                    var ms = r["markers"]!.AsArray();
                    return ToolResult.Ok(ms.Count == 0 ? "No markers." :
                        string.Join("; ", ms.Select(m => $"{m!["name"]} at bar {m["bar"]} ({m["length_beats"]} beats)")), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "automation_write",
                Title = "Write automation",
                Description =
                    "Draws a parameter's movement inside a pattern: points (beat in the pattern, value in the parameter's units) joined as steps or " +
                    "linear ramps; ReBuzz plays it whenever the pattern plays. Replaces the parameter's automation between the first and last " +
                    "point (clear=true removes all of it). Values outside a parameter's safe_range (machine_describe) are refused, with the reason, " +
                    "unless allow_unsafe=true. One undo step. Example: automation_write(machine=\"Pad\", pattern=\"Build\", " +
                    "param=\"F1 cutoff\", points=[{\"at\": 0, \"value\": \"300 Hz\"}, {\"at\": 16, \"value\": \"4 kHz\"}]).",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Req("pattern", String("The pattern")),
                    Req("param", String("Parameter name")),
                    Opt("track", Integer("Track, for track parameters", 0)),
                    Opt("points", Array(Object("A point",
                        Req("at", Position("Beat in the pattern")),
                        Req("value", AnyOf("Value in units", String("\"800 Hz\", \"-6 dB\", a choice, raw:N"), Number("In the parameter's unit")))), "Points in time order")),
                    Opt("shape", String("linear (default) ramps between points; step jumps at each", "linear", "step")),
                    Opt("clear", Boolean("Remove all of this parameter's automation in the pattern first")),
                    Opt("allow_unsafe", Boolean("Write values outside the parameter's safe range anyway (default false: refused with the reason)"))),
                Output = Object(null, Req("events", Integer("Automation events in the pattern for it now"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("automation_write", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                        var param = SongModel.Param(m, SongModel.Params(m), c.RequiredString("param"), track: c.Int("track") != null);
                        int track = c.Int("track") ?? 0;
                        bool allowUnsafe = c.Bool("allow_unsafe") ?? false;
                        string unsafeWhy = null;
                        if (!param.IsTrack && track != 0) throw new ToolError($"{param.Name} is a global parameter; leave track out.");
                        if (param.IsTrack && track >= m.TrackCount) throw new ToolError($"{m.Name} has {m.TrackCount} track(s).");
                        var key = new ColumnKey(param.Group, param.Parameter.IndexInGroup, track);
                        int tpb = SongModel.Tpb(core), bpb = SongModel.BeatsPerBar(core), length = p.Length * NoteList.TimeBase;
                        var points = new List<(int Time, int Raw)>();
                        var list = c.List("points");
                        int current = param.Parameter.GetValue(track);
                        for (int i = 0; i < (list?.Count ?? 0); i++)
                        {
                            var o = list[i]!.AsObject();
                            int time = NoteList.Time(SongTime.ParseBeats(o["at"], bpb, $"points[{i}].at", "the pattern start"), tpb);
                            if (time > length) throw new ToolError($"points[{i}]: beat {NoteList.Fmt(NoteList.Beats(time, tpb))} is past the pattern's end ({NoteList.Fmt(NoteList.Beats(length, tpb))} beats).");
                            int raw;
                            try { raw = ValueResolver.Resolve(param.Info, o["value"], current); }
                            catch (ToolError e) { throw new ToolError($"points[{i}].value: {e.Message}"); }
                            // docs/specs/machine-safety.md §3.4: as param_set. A ramp between two safe points stays inside the range.
                            string refusal = SafeRange.Refusal(param.Info, raw);
                            if (refusal != null && !allowUnsafe) throw new ToolError($"points[{i}].value: {refusal}");
                            if (refusal != null) unsafeWhy ??= $"outside its safe range, {param.Info.SafeRange.Text(param.Info.Describe)}: {param.Info.SafeRange.Reason}";
                            points.Add((Math.Min(time, length - 1), raw));
                        }
                        points = points.OrderBy(x => x.Time).ToList();
                        bool clear = c.Bool("clear") == true;
                        if (points.Count == 0 && !clear) throw new ToolError("Give points (at least one), or clear=true to remove the automation.");
                        var existing = Events(p, key);
                        var removed = clear ? existing : points.Count == 0 ? new List<PatternEvent>()
                            : existing.Where(e => e.Time >= points[0].Time && e.Time <= points[^1].Time).ToList();
                        var added = new List<PatternEvent>();
                        bool linear = (c.String("shape") ?? "linear") == "linear";
                        int step = Math.Max(1, NoteList.TimeBase / 4);
                        for (int i = 0; i < points.Count; i++)
                        {
                            added.Add(new PatternEvent(points[i].Time, points[i].Raw));
                            if (!linear || i + 1 >= points.Count) continue;
                            var (t0, v0) = points[i];
                            var (t1, v1) = points[i + 1];
                            for (int t = t0 + step; t < t1; t += step)
                            {
                                int v = (int)Math.Round(v0 + (v1 - v0) * (t - t0) / (double)(t1 - t0));
                                if (v != added[^1].Value) added.Add(new PatternEvent(t, v));
                            }
                        }
                        // One value per time (the last point wins).
                        added = added.GroupBy(e => e.Time).Select(g => g.Last()).OrderBy(e => e.Time).ToList();
                        AgentActions.ReplaceColumnEvents(core, p, key, removed, added);
                        var now = Events(p, key);
                        var problems = new JsonArray();
                        if (SongModel.Placements(core, p).Count == 0) problems.Add($"\"{p.Name}\" isn't placed in the arrangement, so the automation doesn't play yet.");
                        if (unsafeWhy != null) problems.Add($"Unsafe (allow_unsafe): {param.Name} goes {unsafeWhy}.");
                        return new JsonObject
                        {
                            ["machine"] = m.Name, ["pattern"] = p.Name, ["param"] = param.Name, ["events"] = now.Count,
                            ["from"] = points.Count > 0 ? param.Info.Describe(points[0].Raw) : null,
                            ["to"] = points.Count > 0 ? param.Info.Describe(points[^1].Raw) : null,
                            ["problems"] = problems,
                        };
                    }, c.Cancellation);
                    var probs = r["problems"]!.AsArray();
                    return ToolResult.Ok($"{r["machine"]} \"{r["pattern"]}\": {r["param"]} automation now {r["events"]} events" +
                                         (r["from"] != null ? $", {r["from"]} → {r["to"]}" : "") + "." +
                                         (probs.Count > 0 ? " " + string.Join(" ", probs.Select(x => x!.GetValue<string>())) : ""), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "automation_read",
                Title = "Read automation",
                Description =
                    "The automation in a pattern: per parameter, its points (beat, value as the machine shows it). Example: automation_read(machine=\"Pad\", pattern=\"Build\").",
                Input = Object(null, Req("machine", String("The machine")), Req("pattern", String("The pattern")), Opt("param", String("Only this parameter"))),
                Output = Object(null, Req("automation", Array(Object(null, Req("param", String("Name")), Req("points", Array(Object(null, Req("at", Number("Beat")), Req("value", String("Shown value"))), "Points"))), "Per parameter"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("automation_read", false, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                        string only = c.String("param");
                        int tpb = SongModel.Tpb(core);
                        var list = new JsonArray();
                        // Track note columns (machines that take notes in tracks) are notes, read with notes_read.
                        foreach (var col in p.Columns.Where(x => x.Type == PatternColumnType.Parameter && x.Parameter != null && x.Metadata.ContainsKey(PatternColumnCore.SessionAutomationKey) && !MachineNotes.IsNoteColumn(x)))
                        {
                            if (only != null && !string.Equals(col.Parameter.Name, only, StringComparison.OrdinalIgnoreCase)) continue;
                            var events = col.GetEvents(int.MinValue, int.MaxValue).OrderBy(e => e.Time).ToList();
                            if (events.Count == 0) continue;
                            var o = new JsonObject { ["param"] = col.Parameter.Name };
                            if (col.Parameter.Group.Type == ParameterGroupType.Track) o["track"] = col.Track;
                            o["events"] = events.Count;
                            // Ramps are many events; show the corners (where the direction changes) as points.
                            var corners = new JsonArray();
                            for (int i = 0; i < events.Count; i++)
                            {
                                bool corner = i == 0 || i == events.Count - 1 ||
                                              Math.Sign(events[i].Value - events[i - 1].Value) != Math.Sign(events[i + 1].Value - events[i].Value);
                                if (corner) corners.Add(new JsonObject { ["at"] = NoteList.Beats(events[i].Time, tpb), ["value"] = SongModel.Describe(col.Parameter, events[i].Value) });
                            }
                            o["points"] = corners;
                            list.Add(o);
                        }
                        return new JsonObject { ["machine"] = m.Name, ["pattern"] = p.Name, ["automation"] = list };
                    }, c.Cancellation);
                    var a = r["automation"]!.AsArray();
                    return ToolResult.Ok(a.Count == 0 ? $"{r["machine"]} \"{r["pattern"]}\" has no automation." :
                        string.Join("; ", a.Select(x => $"{x!["param"]}: " + string.Join(" → ", x["points"]!.AsArray().Take(8).Select(pt => $"{pt!["value"]} at {pt["at"]}")))), r);
                },
            });

            SessionTools(tools, host);

            tools.Add(new ToolDef
            {
                Name = "machine_data_import",
                Title = "Import machine data",
                Description =
                    "Loads a file a machine uses as its own data: a WAV wavetable for Neo Wave (kind \"wavetable\"), a DX7 SysEx bank for Neo FM6 " +
                    "(kind \"dx7_bank\", item picks the voice to play, 1–32). machine_describe and gear_list's manifest say what each machine " +
                    "takes. One undo step. Example: machine_data_import(machine=\"Keys\", kind=\"dx7_bank\", path=\"C:/DX7/rom1a.syx\", item=11).",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Req("kind", String("What the file is (from the machine's manifest)")),
                    Req("path", String("Full path of the file")),
                    Opt("item", Integer("Which item of the file to use (a bank's voice), from 1", 1))),
                Output = Object(null, Req("summary", String("What was loaded"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string path = Path.GetFullPath(c.RequiredString("path").Trim().Trim('"'));
                    if (!File.Exists(path)) throw new ToolError($"No file at {path}.");
                    var r = await host.Engine("machine_data_import", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        string kind = c.RequiredString("kind");
                        var manifest = SongModel.Manifest(m.DLL);
                        var imports = manifest?.Imports ?? System.Array.Empty<ManifestImport>();
                        var import = imports.FirstOrDefault(i => string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase));
                        if (import == null)
                            throw new ToolError(imports.Count == 0 ? $"{m.Name} ({m.DLL?.Name}) takes no data files." :
                                $"{m.Name} doesn't take \"{kind}\". It takes: {string.Join("; ", imports.Select(i => $"{i.Kind} ({string.Join(", ", i.Extensions)}): {i.Description}"))}.");
                        if (import.Extensions.Count > 0 && !import.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                            throw new ToolError($"{kind} takes {string.Join(", ", import.Extensions)} files.");
                        var target = m.ManagedMachine;
                        var method = target?.GetType().GetMethod("ImportData", new[] { typeof(string), typeof(string), typeof(int), typeof(Action<int, int>) })
                                     ?? throw new ToolError($"{m.Name} has no data import in this build.");
                        byte[] before = m.Data;
                        var sets = new List<(int Index, int Value)>();
                        string summary;
                        try { summary = (string)method.Invoke(target, new object[] { import.Kind, path, c.Int("item") ?? 0, new Action<int, int>((i, v) => sets.Add((i, v))) }); }
                        catch (System.Reflection.TargetInvocationException e) { throw new ToolError($"{m.Name} couldn't load {Path.GetFileName(path)}: {e.InnerException?.Message}"); }
                        byte[] after = m.Data;
                        string name = m.Name;
                        bool first = true;
                        void SetData(byte[] d)
                        {
                            var x = core.SongCore.MachinesList.FirstOrDefault(y => y.Name == name);
                            if (x != null && d != null) x.Data = d;
                        }
                        AgentActions.Do(core, new DelegateAction(() => { if (first) first = false; else SetData(after); }, () => SetData(before)));
                        var globals = m.ParameterGroups[1].Parameters;
                        foreach (var (index, value) in sets)
                            if (index >= 0 && index < globals.Count && globals[index].GetValue(0) != value)
                                AgentActions.SetParameter(core, m, globals[index], 0, value);
                        return new JsonObject { ["machine"] = m.Name, ["kind"] = import.Kind, ["summary"] = summary ?? "Loaded.", ["parameters_set"] = sets.Count };
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]}: {r["summary"]}", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "wave_from_render",
                Title = "Render to a sample",
                Description =
                    "Puts a render (or a probe, or part of one) into a wavetable slot: resampling, or bouncing a synth to audio for a sampler. " +
                    "from_seconds/to_seconds trim it. One undo step. Example: wave_from_render(source=\"r4\", from_seconds=0, to_seconds=2, name=\"Stab\").",
                Input = Object(null,
                    Req("source", String("A render id (\"r4\", \"r4:Bass\")")),
                    Opt("from_seconds", Number("Start", 0)),
                    Opt("to_seconds", Number("End", 0)),
                    Opt("slot", Integer("Slot, from 1 (default: the first empty)", 1)),
                    Opt("name", String("Its name")),
                    Opt("root_note", AnyOf("Root pitch", Integer("MIDI", 0, 127), String("\"C4\"")))),
                Output = Object(null, Req("wave", Object(null, Req("slot", Integer("Slot")), Req("name", String("Name"))))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string source = c.RequiredString("source").Trim();
                    if (!host.Renders.TryGetValue(source, out var render))
                        throw new ToolError($"No render \"{source}\" in this session." + (host.Renders.IsEmpty ? " Make one with render()." : $" Renders: {string.Join(", ", host.Renders.Keys.OrderBy(k => k))}."));
                    var audio = AudioReport.Slice(Wav.Read(render.File), c.Number("from_seconds"), c.Number("to_seconds"));
                    if (audio.Frames < 16) throw new ToolError("That part is empty; check from_seconds and to_seconds.");
                    string file = Path.Combine(Path.GetDirectoryName(render.File)!, $"{Path.GetFileNameWithoutExtension(render.File)}-sample-{Guid.NewGuid().ToString("N")[..6]}.wav");
                    var interleaved = new float[audio.Frames * 2];
                    for (int i = 0; i < audio.Frames; i++) { interleaved[2 * i] = audio.Left[i]; interleaved[2 * i + 1] = audio.Right?[i] ?? audio.Left[i]; }
                    Wav.WriteStereoFloat(file, interleaved, audio.Frames, audio.Rate);
                    var args = new JsonObject { ["path"] = file, ["name"] = c.String("name") ?? source.Replace(':', ' ') };
                    if (c.Int("slot") is int slot) { args["slot"] = slot; args["replace"] = false; }
                    if (c.Arguments["root_note"] != null) args["root_note"] = c.Arguments["root_note"]!.DeepClone();
                    var import = host.Server.Tools.Find("wave_import")!;
                    return await import.Handler(new ToolContext("wave_import", args, c.Cancellation));
                },
            });
        }

        static List<PatternEvent> Events(IPattern p, ColumnKey key) =>
            SessionLayout.ColumnFor(p, key, create: false)?.GetEvents(int.MinValue, int.MaxValue).ToList() ?? new List<PatternEvent>();

        // ------------------------------------------------------------------ the Session View's grid

        static void SessionTools(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "session_get",
                Title = "Read the session grid",
                Description =
                    "The Session View: one track per machine lane, scenes (rows) with name and tempo, and which pattern sits in each slot, with the " +
                    "clips' launch settings when not the defaults. Scenes are numbered from 1. Example: session_get().",
                Input = Object(null),
                Output = Object(null,
                    Req("tracks", Array(Object(null, Req("machine", String("Machine")), Req("slots", Array(String("Pattern in the scene, or empty"), "One per scene"))), "Tracks")),
                    Req("scenes", Array(Object(null, Req("scene", Integer("Number")), Opt("name", String("Name"))), "Scenes"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("session_get", false, _ => SessionJson(core), c.Cancellation);
                    var tracks = r["tracks"]!.AsArray();
                    return ToolResult.Ok($"{tracks.Count} tracks, {r["scenes"]!.AsArray().Count} scenes. " +
                        string.Join("; ", tracks.Select(t => $"{t!["machine"]}: " + string.Join(" | ", t["slots"]!.AsArray().Select(s => s?.GetValue<string>() is { Length: > 0 } n ? n : "–")))), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "session_clip_set",
                Title = "Set a session slot",
                Description =
                    "Puts one of a machine's patterns in a scene's slot of the Session View (pattern=null empties it), optionally with launch settings: " +
                    "mode (Trigger, Gate, Toggle, Repeat), quantization (Global, None, Beat, Bar, TwoBars, FourBars, EightBars, HalfNote, Eighth, " +
                    "Sixteenth), loop, legato. The machine gets a track if it has none. One undo step. " +
                    "Example: session_clip_set(machine=\"Bass\", scene=2, pattern=\"Chorus\", quantization=\"Bar\").",
                Input = Object(null,
                    Req("machine", String("The machine (its track)")),
                    Req("scene", Integer("Scene, from 1 (new scenes are added up to it)", 1, 999)),
                    Opt("pattern", AnyOf("The pattern, or null to empty the slot", String("Pattern name"), new JsonObject { ["type"] = "null" })),
                    Opt("mode", String("Launch mode", "Trigger", "Gate", "Toggle", "Repeat")),
                    Opt("quantization", String("Launch quantization", "Global", "None", "Beat", "Bar", "TwoBars", "FourBars", "EightBars", "HalfNote", "Eighth", "Sixteenth")),
                    Opt("loop", Boolean("Loop the clip (default true)")),
                    Opt("legato", Boolean("Start where the previous clip of the track was"))),
                Output = Object(null, Req("tracks", Array(Object(null, Req("machine", String("Machine"))), "The grid now"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var r = await host.Engine("session_clip_set", true, _ =>
                    {
                        var layout = core.SessionLayout ?? throw new ToolError("This ReBuzz has no Session View.");
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        if (SongModel.IsMaster(m)) throw new ToolError("Master has no session track.");
                        IPattern pattern = c.Arguments["pattern"] is JsonValue v && v.TryGetValue(out string pn) ? SongModel.Pattern(m, pn) : null;
                        if (c.Arguments["pattern"] == null && !c.Arguments.ContainsKey("pattern")) throw new ToolError("pattern: give a pattern name, or null to empty the slot.");
                        if (!core.SongCore.Sequences.Any(s => s.Machine == m))
                            AgentActions.Do(core, new BuzzGUI.Common.Actions.MachineActions.AddSequenceAction(m));
                        var doc = layout.Ensure();
                        var before = doc.CloneLayout();
                        int track = TrackOf(layout, m);
                        if (track < 0) throw new ToolError($"{m.Name} has no session track (its lane wasn't picked up).");
                        int scene = c.Int("scene")!.Value - 1;
                        while (layout.SceneCount <= scene) layout.InsertScene(layout.SceneCount);
                        layout.SetSlot(track, scene, pattern);
                        if (pattern != null && (c.String("mode") != null || c.String("quantization") != null || c.Bool("loop") != null || c.Bool("legato") != null))
                        {
                            var s = layout.ClipOf(pattern)?.Clone() ?? new SessionDocument.ClipSettings();
                            if (c.String("mode") is string mode) s.Mode = mode;
                            if (c.String("quantization") is string q) s.Quantization = q;
                            if (c.Bool("loop") is bool loop) s.Loop = loop;
                            if (c.Bool("legato") is bool legato) s.Legato = legato;
                            layout.SetClipSettings(pattern, s);
                        }
                        var after = layout.Document.CloneLayout();
                        AgentActions.Do(core, new DelegateAction(() => layout.Restore(after), () => layout.Restore(before)));
                        return SessionJson(core);
                    }, c.Cancellation);
                    return ToolResult.Ok($"Slot set. {r["tracks"]!.AsArray().Count} tracks, {r["scenes"]!.AsArray().Count} scenes.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "session_scene_set",
                Title = "Set a session scene",
                Description =
                    "Names a scene, gives it a tempo or a time signature, or inserts a new empty scene before it. Scenes are numbered from 1. " +
                    "One undo step. Example: session_scene_set(scene=3, name=\"Drop\", tempo_bpm=128).",
                Input = Object(null,
                    Req("scene", Integer("Scene, from 1", 1, 999)),
                    Opt("insert", Boolean("Insert a new scene at this number (later scenes move down)")),
                    Opt("name", String("Its name")),
                    Opt("tempo_bpm", Integer("Tempo it launches with (0: none)", 0, 500)),
                    Opt("beats_per_bar", Integer("Beats per bar it launches with (0: none)", 0, 32))),
                Output = Object(null, Req("scenes", Array(Object(null, Req("scene", Integer("Number"))), "Scenes"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("session_scene_set", true, _ =>
                    {
                        var layout = core.SessionLayout ?? throw new ToolError("This ReBuzz has no Session View.");
                        var doc = layout.Ensure();
                        var before = doc.CloneLayout();
                        int scene = c.Int("scene")!.Value - 1;
                        if (c.Bool("insert") == true) layout.InsertScene(Math.Min(scene, layout.SceneCount));
                        while (layout.SceneCount <= scene) layout.InsertScene(layout.SceneCount);
                        if (c.String("name") is string name) layout.RenameScene(scene, name);
                        if (c.Int("tempo_bpm") is int bpm)
                        {
                            if (bpm != 0 && bpm < 16) throw new ToolError("tempo_bpm: 16–500, or 0 for none.");
                            layout.SetSceneTempo(scene, bpm);
                        }
                        if (c.Int("beats_per_bar") is int beats) layout.SetSceneBeatsPerBar(scene, beats);
                        var after = layout.Document.CloneLayout();
                        AgentActions.Do(core, new DelegateAction(() => layout.Restore(after), () => layout.Restore(before)));
                        return SessionJson(core);
                    }, c.Cancellation);
                    return ToolResult.Ok("Scenes: " + string.Join(", ", r["scenes"]!.AsArray().Select(s =>
                        $"{s!["scene"]}{(s["name"] != null ? " " + s["name"] : "")}{(s["tempo_bpm"] != null ? $" ({s["tempo_bpm"]} BPM)" : "")}")) + ".", r);
                },
            });
        }

        static int TrackOf(SessionLayout layout, MachineCore m)
        {
            for (int t = 0; t < layout.TrackCount; t++)
                if (layout.SequenceOf(t)?.Machine == m) return t;
            return -1;
        }

        static JsonObject SessionJson(ReBuzzCore core)
        {
            var layout = core.SessionLayout;
            var doc = layout?.Document;
            var tracks = new JsonArray();
            var scenes = new JsonArray();
            if (doc != null)
            {
                for (int s = 0; s < doc.Scenes.Count; s++)
                {
                    var sc = doc.Scenes[s];
                    var o = new JsonObject { ["scene"] = s + 1 };
                    if (!string.IsNullOrEmpty(sc.Name)) o["name"] = sc.Name;
                    if (sc.Tempo > 0) o["tempo_bpm"] = sc.Tempo;
                    if (sc.BeatsPerBar > 0) o["beats_per_bar"] = sc.BeatsPerBar;
                    scenes.Add(o);
                }
                for (int t = 0; t < layout.TrackCount; t++)
                {
                    var seq = layout.SequenceOf(t);
                    if (seq?.Machine == null) continue;
                    var slots = new JsonArray();
                    var clips = new JsonArray();
                    for (int s = 0; s < doc.Scenes.Count; s++)
                    {
                        var p = layout.PatternAt(t, s);
                        slots.Add(p?.Name);
                        if (p != null && layout.ClipOf(p) is { IsDefault: false } cs)
                            clips.Add(new JsonObject { ["scene"] = s + 1, ["pattern"] = p.Name, ["mode"] = cs.Mode, ["quantization"] = cs.Quantization, ["loop"] = cs.Loop, ["legato"] = cs.Legato });
                    }
                    var track = new JsonObject { ["machine"] = seq.Machine.Name, ["slots"] = slots };
                    if (clips.Count > 0) track["clip_settings"] = clips;
                    tracks.Add(track);
                }
            }
            return new JsonObject
            {
                ["tracks"] = tracks,
                ["scenes"] = scenes,
                ["beats_per_bar"] = SongModel.BeatsPerBar(core),
                ["launch_quantization"] = core.SessionCore?.LaunchQuantization.ToString() ?? "Bar",
            };
        }
    }
}
