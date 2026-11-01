// Agent server (docs/specs/mcp-server.md §5.5): composition. Patterns, their notes and the
// arrangement. pattern_list, pattern_create, pattern_delete, pattern_rename, pattern_duplicate,
// notes_write, notes_read, arrangement_get, arrangement_place, arrangement_clear.
//
// Notes live in each pattern's engine MIDI column (docs/specs/session-view.md 0.7): ReBuzz plays
// it to the machine itself, songs save it, the Session View's piano roll edits it, and its slot
// menu moves the notes to the Pattern View's note lanes and back.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Common.Actions.SequenceActions;
using BuzzGUI.Common.Actions.SongActions;
using BuzzGUI.SequenceEditor.Actions;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;
using SongTime = ReBuzz.Agent.Model.SongTime;

namespace ReBuzz.Agent
{
    internal static class PatternTools
    {
        static JsonObject Position(string description) =>
            AnyOf(description, Number("Beats from the start (0 = the start)", 0), String("\"bar.beat\": \"5.1\" is bar 5's first beat"));

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "pattern_list",
                Title = "List patterns",
                Description =
                    "Patterns of one machine or of all: name, length in beats, note count and where each is placed in the arrangement. " +
                    "Example: pattern_list(machine=\"Bass\").",
                Input = Object(null, Opt("machine", String("Only this machine's patterns"))),
                Output = Object(null, Req("machines", Array(Object(null, Req("machine", String("Name")), Req("patterns", Array(Object(null,
                    Req("name", String("Pattern name")), Req("length_beats", Number("Length")), Req("notes", Integer("Notes")),
                    Req("placed_at", Array(Number("Beat"), "Arrangement positions in beats"))), "Its patterns"))), "Per machine"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("pattern_list", false, _ =>
                    {
                        var ms = c.String("machine") is string name ? new List<MachineCore> { SongModel.Machine(core, name) }
                            : SongModel.Machines(core).Where(m => !SongModel.IsMaster(m)).ToList();
                        return new JsonObject
                        {
                            ["machines"] = new JsonArray(ms.Select(m => (JsonNode)new JsonObject
                            {
                                ["machine"] = m.Name,
                                ["patterns"] = new JsonArray(m.Patterns.Select(p => (JsonNode)PatternJson(core, p)).ToArray()),
                            }).ToArray()),
                        };
                    }, c.Cancellation);
                    var parts = r["machines"]!.AsArray().Select(m => $"{m!["machine"]}: " + (m["patterns"]!.AsArray().Count == 0 ? "no patterns"
                        : string.Join(", ", m["patterns"]!.AsArray().Select(p => $"{p!["name"]} ({p["length_beats"]} beats, {p["notes"]} notes)"))));
                    return ToolResult.Ok(string.Join("; ", parts), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "pattern_create",
                Title = "Create a pattern",
                Description =
                    "Makes an empty pattern for a machine, length in beats (default 4 bars). Then notes_write fills it and arrangement_place " +
                    "puts it on the timeline. One undo step. Example: pattern_create(machine=\"Bass\", name=\"Verse\", length_beats=16).",
                Input = Object(null,
                    Req("machine", String("The machine it plays")),
                    Opt("name", String("Its name (unique for the machine); default the next number")),
                    Opt("length_beats", Number("Length in beats (default 4 bars)", 0.0001, 4096))),
                Output = Object(null, Req("pattern", Object(null, Req("name", String("Name")), Req("length_beats", Number("Length"))))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("pattern_create", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        if (SongModel.IsMaster(m)) throw new ToolError("Master's patterns hold tempo changes; make patterns for the instruments.");
                        double beats = c.Number("length_beats") ?? SongModel.BeatsPerBar(core) * 4;
                        int ticks = SongModel.Ticks(core, beats);
                        if (ticks < 1) throw new ToolError($"length_beats {beats} is shorter than one tick (1/{SongModel.Tpb(core)} beat).");
                        string name = c.String("name")?.Trim();
                        if (name != null && m.Patterns.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                            throw new ToolError($"{m.Name} already has a pattern \"{name}\". Choose another name, or notes_write into it.");
                        name ??= NextName(m);
                        AgentActions.Do(core, new CreatePatternAction(m, name, ticks));
                        var p = m.Patterns.FirstOrDefault(x => x.Name == name) ?? throw new ToolError($"ReBuzz didn't create the pattern \"{name}\".");
                        return new JsonObject { ["machine"] = m.Name, ["pattern"] = PatternJson(core, p) };
                    }, c.Cancellation);
                    return ToolResult.Ok($"Created {r["machine"]}'s pattern \"{r["pattern"]!["name"]}\", {r["pattern"]!["length_beats"]} beats.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "pattern_delete",
                Title = "Delete a pattern",
                Description =
                    "Deletes a pattern with its notes and its places in the arrangement. One undo step (undo restores all three). " +
                    "Example: pattern_delete(machine=\"Bass\", pattern=\"Old\").",
                Input = Object(null, Req("machine", String("The machine")), Req("pattern", String("The pattern"))),
                Output = Object(null, Req("deleted", String("Its name")), Req("was_placed", Integer("Arrangement places removed"))),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    var r = await host.Engine("pattern_delete", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                        int placed = 0;
                        foreach (var seq in core.SongCore.Sequences.Where(s => s.Machine == m).ToList())
                            foreach (var t in seq.Events.Where(e => e.Value.Pattern == p).Select(e => e.Key).ToList())
                            {
                                AgentActions.Do(core, new ClearAction(seq, t, 1));
                                placed++;
                            }
                        var notes = SongModel.Notes(p);
                        string name = p.Name;
                        AgentActions.DeletePattern(core, p);
                        return new JsonObject { ["machine"] = m.Name, ["deleted"] = name, ["was_placed"] = placed, ["notes"] = notes.Count };
                    }, c.Cancellation);
                    return ToolResult.Ok($"Deleted {r["machine"]}'s pattern \"{r["deleted"]}\" ({r["notes"]} notes, {r["was_placed"]} places in the arrangement).", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "pattern_rename",
                Title = "Rename a pattern",
                Description = "Renames a pattern; its notes and arrangement places stay. One undo step. Example: pattern_rename(machine=\"Bass\", pattern=\"00\", new_name=\"Verse\").",
                Input = Object(null, Req("machine", String("The machine")), Req("pattern", String("The pattern")), Req("new_name", String("Its new name"))),
                Output = Object(null, Req("pattern", Object(null, Req("name", String("Name now"))))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var r = await host.Engine("pattern_rename", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                        string to = c.RequiredString("new_name").Trim();
                        if (to.Length == 0) throw new ToolError("new_name: give a name.");
                        if (to != p.Name)
                        {
                            if (m.Patterns.Any(x => x != p && string.Equals(x.Name, to, StringComparison.OrdinalIgnoreCase)))
                                throw new ToolError($"{m.Name} already has a pattern \"{to}\".");
                            AgentActions.RenamePattern(core, p, to);
                        }
                        return new JsonObject { ["machine"] = m.Name, ["pattern"] = PatternJson(core, p) };
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]}'s pattern is now \"{r["pattern"]!["name"]}\".", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "pattern_duplicate",
                Title = "Duplicate a pattern",
                Description =
                    "Copies a pattern under a new name, with its notes, automation and its Pattern View data, to make a variation (a fill, a second verse). One undo step. " +
                    "Example: pattern_duplicate(machine=\"Drums\", pattern=\"Beat\", new_name=\"Beat fill\").",
                Input = Object(null, Req("machine", String("The machine")), Req("pattern", String("The pattern to copy")), Opt("new_name", String("The copy's name (default: numbered)"))),
                Output = Object(null, Req("pattern", Object(null, Req("name", String("The copy's name"))))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("pattern_duplicate", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                        string name = c.String("new_name")?.Trim();
                        if (name != null && m.Patterns.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                            throw new ToolError($"{m.Name} already has a pattern \"{name}\".");
                        name ??= BuzzGUI.Common.Actions.PatternNames.Copy(m, p.Name);
                        AgentActions.DuplicatePattern(core, p, name);
                        var copy = m.Patterns.FirstOrDefault(x => x.Name == name) ?? throw new ToolError($"ReBuzz didn't create \"{name}\".");
                        return new JsonObject { ["machine"] = m.Name, ["from"] = p.Name, ["pattern"] = PatternJson(core, copy) };
                    }, c.Cancellation);
                    return ToolResult.Ok($"Copied {r["machine"]}'s \"{r["from"]}\" to \"{r["pattern"]!["name"]}\" ({r["pattern"]!["notes"]} notes).", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "notes_write",
                Title = "Write notes",
                Description =
                    "Writes notes into a pattern. Each note: pitch (MIDI number or name, 60 = \"C4\"), start (beats from the pattern start, or " +
                    "\"bar.beat\" in it), length in beats, velocity 1–127 (default 100). Chords are notes with the same start. mode: add (default) " +
                    "keeps what's there, replace clears the pattern first, replace_range clears only from range_start to range_end. One undo step. " +
                    "Returns a summary and problems (overlapping same pitches). Example: notes_write(machine=\"Bass\", pattern=\"Verse\", " +
                    "notes=[{\"pitch\": \"A1\", \"start\": 0, \"length\": 0.5, \"velocity\": 110}, {\"pitch\": \"A1\", \"start\": 1.5, \"length\": 0.5}]).",
                Input = Object(null,
                    Req("machine", String("The machine that plays them")),
                    Req("pattern", String("The pattern")),
                    Req("notes", Array(Object("A note",
                        Req("pitch", AnyOf("Pitch", Integer("MIDI number, 60 = C4", 0, 127), String("Name: \"C4\", \"F#3\", \"Bb2\""))),
                        Req("start", Position("Start, from the pattern start")),
                        Req("length", Number("Length in beats", 0)),
                        Opt("velocity", Integer("1–127 (default 100)", 1, 127))), "The notes")),
                    Opt("mode", String("add (default), replace or replace_range", "add", "replace", "replace_range")),
                    Opt("range_start", Position("replace_range: from here (in the pattern)")),
                    Opt("range_end", Position("replace_range: to here (exclusive)"))),
                Output = Object(null,
                    Req("written", Integer("Notes written")),
                    Req("removed", Integer("Notes cleared first")),
                    Req("total", Integer("Notes in the pattern now")),
                    Req("problems", Array(String("A problem"), "Overlaps, shortened notes"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string mode = c.String("mode") ?? "add";
                    var r = await host.Engine("notes_write", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        if (SongModel.IsMaster(m)) throw new ToolError("Master doesn't play notes.");
                        var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                        int tpb = SongModel.Tpb(core), bpb = SongModel.BeatsPerBar(core);
                        var problems = new List<string>();
                        var added = NoteList.Parse(c.List("notes")!, tpb, bpb, p.Length, problems);
                        var existing = SongModel.Notes(p);
                        List<NoteEvent> removed;
                        if (mode == "replace") removed = existing;
                        else if (mode == "replace_range")
                        {
                            if (c.Arguments["range_start"] == null || c.Arguments["range_end"] == null)
                                throw new ToolError("replace_range needs range_start and range_end (beats in the pattern).");
                            int from = NoteList.Time(SongTime.ParseBeats(c.Arguments["range_start"], bpb, "range_start", "the pattern start"), tpb);
                            int to = NoteList.Time(SongTime.ParseBeats(c.Arguments["range_end"], bpb, "range_end", "the pattern start"), tpb);
                            if (to <= from) throw new ToolError("range_end must be after range_start.");
                            removed = existing.Where(n => n.Time >= from && n.Time < to).ToList();
                            int outside = added.Count(n => n.Time < from || n.Time >= to);
                            if (outside > 0) problems.Add($"{outside} written note(s) start outside the replaced range; they were added anyway.");
                        }
                        else removed = new List<NoteEvent>();
                        var after = existing.Except(removed).Concat(added).ToList();
                        after.Sort(NoteList.Order);
                        // The same note twice is written once.
                        var unique = added.Distinct().Where(n => !existing.Except(removed).Contains(n)).ToList();
                        if (unique.Count < added.Count) problems.Add($"{added.Count - unique.Count} note(s) were already there and weren't doubled.");
                        problems.AddRange(NoteList.Clashes(existing.Except(removed).Concat(unique).OrderBy(n => n.Time).ToList(), tpb));
                        if (removed.Count > 0 || unique.Count > 0) AgentActions.ReplaceNotes(core, p, removed, unique);
                        var now = SongModel.Notes(p);
                        if (now.Count > 0 && SongModel.Placements(core, p).Count == 0)
                            problems.Add($"\"{p.Name}\" isn't placed in the arrangement yet, so the song doesn't play it: arrangement_place(machine=\"{m.Name}\", pattern=\"{p.Name}\", at=…).");
                        return new JsonObject
                        {
                            ["machine"] = m.Name,
                            ["pattern"] = p.Name,
                            ["written"] = unique.Count,
                            ["removed"] = removed.Count,
                            ["total"] = now.Count,
                            ["polyphony"] = NoteList.Polyphony(now),
                            ["summary"] = NoteList.Summary(now, tpb),
                            ["problems"] = new JsonArray(problems.Select(x => (JsonNode)x).ToArray()),
                        };
                    }, c.Cancellation);
                    var probs = r["problems"]!.AsArray();
                    return ToolResult.Ok($"{r["machine"]} \"{r["pattern"]}\": wrote {r["written"]}, cleared {r["removed"]}; now {r["summary"]}." +
                                         (probs.Count > 0 ? " Problems: " + string.Join(" ", probs.Select(x => x!.GetValue<string>())) : ""), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "notes_read",
                Title = "Read notes",
                Description =
                    "The notes of a pattern in the same shape notes_write takes (pitch, name, start and length in beats, velocity), optionally " +
                    "from range_start to range_end. Example: notes_read(machine=\"Bass\", pattern=\"Verse\").",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Req("pattern", String("The pattern")),
                    Opt("range_start", Position("From here (in the pattern)")),
                    Opt("range_end", Position("To here (exclusive)"))),
                Output = Object(null,
                    Req("notes", Array(Object(null, Req("pitch", Integer("MIDI")), Req("start", Number("Beats")), Req("length", Number("Beats")), Req("velocity", Integer("1–127"))), "Notes")),
                    Req("length_beats", Number("The pattern's length"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("notes_read", false, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                        int tpb = SongModel.Tpb(core), bpb = SongModel.BeatsPerBar(core);
                        var notes = SongModel.Notes(p);
                        if (c.Arguments["range_start"] != null)
                        {
                            int from = NoteList.Time(SongTime.ParseBeats(c.Arguments["range_start"], bpb, "range_start", "the pattern start"), tpb);
                            notes = notes.Where(n => n.Time >= from).ToList();
                        }
                        if (c.Arguments["range_end"] != null)
                        {
                            int to = NoteList.Time(SongTime.ParseBeats(c.Arguments["range_end"], bpb, "range_end", "the pattern start"), tpb);
                            notes = notes.Where(n => n.Time < to).ToList();
                        }
                        return new JsonObject
                        {
                            ["machine"] = m.Name,
                            ["pattern"] = p.Name,
                            ["length_beats"] = SongModel.Beats(core, p.Length),
                            ["summary"] = NoteList.Summary(notes, tpb),
                            ["notes"] = new JsonArray(notes.Select(n => (JsonNode)NoteList.ToJson(n, tpb)).ToArray()),
                        };
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]} \"{r["pattern"]}\" ({r["length_beats"]} beats): {r["summary"]}.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "arrangement_get",
                Title = "Read the arrangement",
                Description =
                    "The timeline: one lane per machine with its patterns' places (start and end in beats and bar.beat), plus the song end and " +
                    "loop. Optionally only from/to (beats). Example: arrangement_get().",
                Input = Object(null, Opt("from", Position("From here")), Opt("to", Position("To here (exclusive)"))),
                Output = Object(null,
                    Req("lanes", Array(Object(null, Req("machine", String("Machine")), Req("events", Array(Object(null,
                        Req("at", Number("Start, beats")), Req("pattern", String("Pattern")), Req("ends", Number("Where it stops, beats"))), "Placements"))), "Lanes")),
                    Req("song_end_beats", Number("Song end")),
                    Req("beats_per_bar", Integer("Beats per bar"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("arrangement_get", false, _ =>
                    {
                        int from = c.Arguments["from"] != null ? SongModel.ParseTicks(core, c.Arguments["from"], "from") : 0;
                        int to = c.Arguments["to"] != null ? SongModel.ParseTicks(core, c.Arguments["to"], "to") : int.MaxValue;
                        int bpb = SongModel.BeatsPerBar(core);
                        var lanes = new JsonArray();
                        foreach (var seq in core.SongCore.Sequences)
                        {
                            if (seq.Machine is MachineCore mc && mc.Hidden) continue;
                            var events = new JsonArray();
                            var ordered = seq.Events.OrderBy(e => e.Key).ToList();
                            for (int i = 0; i < ordered.Count; i++)
                            {
                                var (t, e) = (ordered[i].Key, ordered[i].Value);
                                int end = t + (e.Type == SequenceEventType.PlayPattern ? e.PlayedLength : 0);
                                if (i + 1 < ordered.Count) end = Math.Min(end, ordered[i + 1].Key);
                                if (t >= to || (end <= from && t < from)) continue;
                                var o = new JsonObject
                                {
                                    ["at"] = SongModel.Beats(core, t),
                                    ["bar"] = SongTime.BarBeat(SongModel.Beats(core, t), bpb),
                                };
                                if (e.Type == SequenceEventType.PlayPattern)
                                {
                                    o["pattern"] = e.Pattern.Name;
                                    o["ends"] = SongModel.Beats(core, end);
                                    if (end - t < e.PlayedLength) o["cut_by_next"] = true;
                                }
                                else o["event"] = e.Type.ToString().ToLowerInvariant();
                                events.Add(o);
                            }
                            lanes.Add(new JsonObject { ["machine"] = seq.Machine.Name, ["muted"] = seq.IsDisabled, ["events"] = events });
                        }
                        return new JsonObject
                        {
                            ["lanes"] = lanes,
                            ["song_end_beats"] = SongModel.Beats(core, core.SongCore.SongEnd),
                            ["loop"] = new JsonObject { ["start_beats"] = SongModel.Beats(core, core.SongCore.LoopStart), ["end_beats"] = SongModel.Beats(core, core.SongCore.LoopEnd) },
                            ["beats_per_bar"] = bpb,
                        };
                    }, c.Cancellation);
                    var lines = r["lanes"]!.AsArray().Select(l => $"{l!["machine"]}: " + (l["events"]!.AsArray().Count == 0 ? "empty"
                        : string.Join(", ", l["events"]!.AsArray().Select(e => $"{e!["pattern"] ?? e["event"]} at {e["at"]}"))));
                    return ToolResult.Ok($"Song end at beat {r["song_end_beats"]}. " + (r["lanes"]!.AsArray().Count == 0 ? "No lanes yet." : string.Join("; ", lines)), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "arrangement_place",
                Title = "Place a pattern",
                Description =
                    "Puts a pattern on its machine's lane in the arrangement at one or more positions (beats, or \"bar.beat\"), optionally repeated " +
                    "every N beats. A pattern plays until it ends or the lane's next pattern starts. Extends the song end to cover it. One undo step. " +
                    "Example: arrangement_place(machine=\"Bass\", pattern=\"Verse\", at=[0], repeat_every_beats=16, times=4).",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Req("pattern", String("Its pattern")),
                    Req("at", AnyOf("Where", Number("Beats", 0), String("\"bar.beat\""), Array(Position("A position"), "Several positions", 1))),
                    Opt("repeat_every_beats", Number("Also place it again this many beats after each position", 0.0001)),
                    Opt("times", Integer("How many places in all with repeat_every_beats (default 1)", 1, 1024))),
                Output = Object(null, Req("placed_at", Array(Number("Beats"), "Positions")), Req("song_end_beats", Number("Song end now"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("arrangement_place", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                        var starts = new List<int>();
                        if (c.Arguments["at"] is JsonArray list)
                            for (int i = 0; i < list.Count; i++) starts.Add(SongModel.ParseTicks(core, list[i], $"at[{i}]"));
                        else starts.Add(SongModel.ParseTicks(core, c.Arguments["at"], "at"));
                        int times = c.Int("times") ?? 1;
                        if (c.Number("repeat_every_beats") != null && c.Int("times") == null)
                            throw new ToolError("repeat_every_beats needs times: how many places in all (times=4 places it 4 times).");
                        if (times > 1)
                        {
                            if (c.Number("repeat_every_beats") is not double every) throw new ToolError("times needs repeat_every_beats.");
                            int step = SongModel.Ticks(core, every);
                            if (step < 1) throw new ToolError("repeat_every_beats is shorter than a tick.");
                            starts = starts.SelectMany(s => Enumerable.Range(0, times).Select(k => s + k * step)).ToList();
                        }
                        starts = starts.Distinct().OrderBy(t => t).ToList();
                        if (starts.Count > 4096) throw new ToolError("That's more than 4096 places; place fewer at once.");
                        var seq = core.SongCore.Sequences.FirstOrDefault(s => s.Machine == m);
                        if (seq == null)
                        {
                            AgentActions.Do(core, new AddSequenceAction(m));
                            seq = core.SongCore.Sequences.LastOrDefault(s => s.Machine == m) ?? throw new ToolError($"ReBuzz didn't make a lane for {m.Name}.");
                        }
                        var notes = new List<string>();
                        int loopEndBefore = core.SongCore.LoopEnd;
                        foreach (int t in starts)
                        {
                            if (seq.Events.TryGetValue(t, out var old) && old.Pattern != p)
                                notes.Add($"replaced {(old.Pattern?.Name ?? old.Type.ToString())} at {SongModel.Where(core, t)}");
                            AgentActions.Do(core, new SetEventAction(seq, t, new SequenceEvent(SequenceEventType.PlayPattern, p)));
                        }
                        int last = starts.Max() + p.Length;
                        if (core.SongCore.SongEnd < last) AgentActions.Do(core, new SetMarkerAction(core.SongCore, SongMarkers.SongEnd, last));
                        var ordered = seq.Events.OrderBy(e => e.Key).ToList();
                        foreach (int t in starts)
                        {
                            var next = ordered.FirstOrDefault(e => e.Key > t);
                            if (next.Value != null && next.Key < t + p.Length)
                                notes.Add($"the place at {SongModel.Where(core, t)} is cut at beat {NoteList.Fmt(SongModel.Beats(core, next.Key))} by the next pattern");
                        }
                        if (SongModel.Notes(p).Count == 0) notes.Add($"\"{p.Name}\" has no notes yet");
                        if (core.SongCore.LoopEnd != loopEndBefore)
                            notes.Add($"the loop end moved to beat {NoteList.Fmt(SongModel.Beats(core, core.SongCore.LoopEnd))} to cover it");
                        return new JsonObject
                        {
                            ["machine"] = m.Name,
                            ["pattern"] = p.Name,
                            ["placed_at"] = new JsonArray(starts.Select(t => (JsonNode)SongModel.Beats(core, t)).ToArray()),
                            ["song_end_beats"] = SongModel.Beats(core, core.SongCore.SongEnd),
                            ["notes"] = new JsonArray(notes.Select(x => (JsonNode)x).ToArray()),
                        };
                    }, c.Cancellation);
                    var placed = r["placed_at"]!.AsArray().Select(x => x!.GetValue<double>()).ToList();
                    var notesText = r["notes"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                    return ToolResult.Ok($"Placed {r["machine"]}'s \"{r["pattern"]}\" at beat{(placed.Count > 1 ? "s" : "")} " +
                                         string.Join(", ", placed.Take(16).Select(NoteList.Fmt)) + (placed.Count > 16 ? ", …" : "") +
                                         $". Song end: beat {r["song_end_beats"]}." + (notesText.Count > 0 ? " Note: " + string.Join("; ", notesText) + "." : ""), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "arrangement_clear",
                Title = "Clear part of the arrangement",
                Description =
                    "Removes pattern places that start from from to to (beats or \"bar.beat\"), on one machine's lane or on all. Patterns themselves stay. " +
                    "One undo step. Example: arrangement_clear(machine=\"Drums\", from=32, to=48).",
                Input = Object(null,
                    Opt("machine", String("Only this machine's lane (default all)")),
                    Req("from", Position("From here")),
                    Req("to", Position("To here (exclusive)"))),
                Output = Object(null, Req("removed", Integer("Places removed"))),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    var r = await host.Engine("arrangement_clear", true, _ =>
                    {
                        int from = SongModel.ParseTicks(core, c.Arguments["from"], "from");
                        int to = SongModel.ParseTicks(core, c.Arguments["to"], "to");
                        if (to <= from) throw new ToolError("to must be after from.");
                        var m = c.String("machine") is string name ? SongModel.Machine(core, name) : null;
                        int removed = 0;
                        foreach (var seq in core.SongCore.Sequences.Where(s => m == null || s.Machine == m).ToList())
                        {
                            int n = seq.Events.Keys.Count(t => t >= from && t < to);
                            if (n == 0) continue;
                            AgentActions.Do(core, new ClearAction(seq, from, to - from));
                            removed += n;
                        }
                        return new JsonObject { ["removed"] = removed };
                    }, c.Cancellation);
                    return ToolResult.Ok($"Removed {r["removed"]} place(s).", r);
                },
            });
        }

        static JsonObject PatternJson(ReBuzzCore core, IPattern p) => new()
        {
            ["name"] = p.Name,
            ["length_beats"] = SongModel.Beats(core, p.Length),
            ["notes"] = SongModel.Notes(p).Count,
            ["placed_at"] = new JsonArray(SongModel.Placements(core, p).Take(64).Select(t => (JsonNode)SongModel.Beats(core, t)).ToArray()),
        };

        /// <summary>"00", "01", … the next free two-digit name.</summary>
        static string NextName(MachineCore m)
        {
            for (int i = 0; ; i++)
            {
                string name = i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
                if (!m.Patterns.Any(p => p.Name == name)) return name;
            }
        }
    }
}
