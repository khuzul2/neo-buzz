// Agent server (daw-modes.md EV1, agent-knowledge.md §11): clip_envelope, the envelopes of one clip as the Detail panel's lane shows them.
// A clip is a pattern, in a Session slot or placed in the arrangement: its envelope of a parameter is the points of the pattern's engine
// automation column (ramps and steps), which the Session View and every placement play. The tool calls the model code the lane uses
// (ClipEnvelopes, ClipEnvelopeEditor over AR4's AutomationEditor and ColumnEventsAction), so a point the agent writes is a point a person
// draws, with the same limits, and each call is one undo step (host.Engine groups it).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using static ReBuzz.Agent.Mcp.Schema;
using SongTime = ReBuzz.Agent.Model.SongTime;

namespace ReBuzz.Agent
{
    internal static class ClipEnvelopeTools
    {
        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "clip_envelope",
                Title = "Envelopes of a clip",
                Description =
                    "A clip's envelopes: one parameter's movement inside the clip, as the Detail panel's envelope lane draws it (points joined by ramps, or held as steps). " +
                    "A clip is a pattern of the machine, in a Session slot or placed in the arrangement (the same pattern is the same envelope in both; each repeat of a looped " +
                    "clip plays it again). Say which: pattern=<its name>, or scene=<Session scene, from 1>, or lane and at/clip as arrangement_clip does. " +
                    "op: read (every parameter with points in the clip, or only param; the Tracker's own columns come back as read-only `tracker` steps), " +
                    "write (points at beats from the clip's start, value as automation_write takes it; each point's ramp, or shape; replaces the clip's points between the first and last " +
                    "point, or all of them with replace=true), clear (all of param's points, or those from `from` to `to`). Values outside a parameter's safe range are refused " +
                    "unless allow_unsafe=true. Only state parameters of the clip's machine (machine_describe). One undo step. Example: " +
                    "clip_envelope(op=\"write\", machine=\"Pad\", pattern=\"Build\", param=\"F1 cutoff\", points=[{\"at\": 0, \"value\": \"300 Hz\"}, {\"at\": 8, \"value\": \"4 kHz\"}, {\"at\": 16, \"value\": \"300 Hz\", \"ramp\": false}]).",
                Input = Object(null,
                    Req("op", String("What to do", "read", "write", "clear")),
                    Req("machine", String("The machine")),
                    Opt("pattern", String("The clip's pattern")),
                    Opt("scene", Integer("A Session slot: the scene, from 1 (the clip in this machine's track)", 1, 999)),
                    Opt("lane", Integer("An arrangement clip: the machine's lane number (arrangement_get; default its first main lane)", 0)),
                    Opt("clip", Integer("An arrangement clip: its id")),
                    Opt("at", Position("An arrangement clip: a position inside it. write/clear don't use it for the points: see points")),
                    Opt("param", String("Parameter name (required for write and clear)")),
                    Opt("track", Integer("Track, for track parameters", 0)),
                    Opt("points", Array(Object("A point",
                        Req("at", Position("Beats from the clip's start (\"bar.beat\" counts from 1.1 at the clip's start)")),
                        Req("value", AnyOf("Value in units", String("\"800 Hz\", \"-6 dB\", a choice, raw:N"), Number("In the parameter's unit"))),
                        Opt("ramp", Boolean("Whether the line after this point is a ramp to the next (default per shape)"))), "Points in time order", 1)),
                    Opt("shape", String("linear (default) ramps between points; step holds each value", "linear", "step")),
                    Opt("replace", Boolean("write: replace all of the parameter's points in the clip (default: those from the first point to the last)")),
                    Opt("from", Position("clear: the start, beats from the clip's start")),
                    Opt("to", Position("clear: the end (exclusive)")),
                    Opt("allow_unsafe", Boolean("Write values outside the parameter's safe range anyway"))),
                Output = Object(null,
                    Req("machine", String("Machine")), Req("pattern", String("The clip's pattern")), Req("length_beats", Number("The clip's length")),
                    Req("envelopes", Array(Object(null,
                        Req("param", String("Parameter")), Opt("track", Integer("Track")),
                        Req("points", Array(Object(null, Req("at", Number("Beats from the clip's start")), Req("value", String("Shown value")), Req("ramp", Boolean("Ramps to the next point"))), "Its points")),
                        Opt("tracker", Array(Object(null, Req("at", Number("Beats")), Req("value", String("Shown value"))), "The Tracker's steps (read only)")),
                        Req("overlaps", Integer("Places the Tracker's column and the points both set it"))), "Per parameter")),
                    Opt("problems", Array(String("A note"), "Things to know"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string op = c.RequiredString("op");
                    var r = await host.Engine("clip_envelope", op != "read", _ => Run(core, c, op), c.Cancellation);
                    var list = r["envelopes"]!.AsArray();
                    string text = op switch
                    {
                        "read" => list.Count == 0 ? $"\"{r["pattern"]}\" has no envelopes." :
                            string.Join("; ", list.Select(e => $"{e!["param"]}: {e["points"]!.AsArray().Count} point(s)" + (e["tracker"] != null ? " + tracker" : ""))),
                        "write" => $"\"{r["pattern"]}\": {list[0]!["param"]} now {list[0]!["points"]!.AsArray().Count} point(s)." +
                                   (r["problems"]!.AsArray().Count > 0 ? " " + string.Join(" ", r["problems"]!.AsArray().Select(x => x!.GetValue<string>())) : ""),
                        _ => $"\"{r["pattern"]}\": {(list.Count > 0 ? list[0]!["param"] + " now " + list[0]["points"]!.AsArray().Count + " point(s)" : "cleared")}.",
                    };
                    return ToolResult.Ok(text, r);
                },
            });
        }

        static JsonObject Position(string description) =>
            AnyOf(description, Number("Beats (0 = the start)", 0), String("\"bar.beat\": \"1.3\" is the third beat of the first bar"));

        /// <summary>The clip the call names: a pattern by name, a Session slot, or an arrangement clip.</summary>
        internal static IPattern ClipOf(ReBuzzCore core, MachineCore m, ToolContext c)
        {
            if (c.String("pattern") is string name) return SongModel.Pattern(m, name);
            if (c.Int("scene") is int scene)
            {
                var layout = core.SessionLayout ?? throw new ToolError($"This {Product.Name} has no Session View.");
                int track = -1;
                for (int t = 0; t < layout.TrackCount && track < 0; t++)
                    if (layout.SequenceOf(t)?.Machine == m) track = t;
                if (track < 0) throw new ToolError($"{m.Name} has no session track.");
                var doc = layout.Document;
                if (doc == null || scene < 1 || scene > doc.Scenes.Count) throw new ToolError($"The session has {doc?.Scenes.Count ?? 0} scene(s), numbered from 1.");
                string slot = doc.GetSlot(track, scene - 1);
                if (string.IsNullOrEmpty(slot)) throw new ToolError($"Scene {scene} of {m.Name}'s track is empty (session_get shows the slots).");
                return SongModel.Pattern(m, slot);
            }
            if (c.Int("clip") != null || c.Arguments["at"] != null)
            {
                var lanes = core.SongCore.Sequences.Where(s => s.Machine == m).ToList();
                int index = c.Int("lane") ?? Math.Max(0, lanes.FindIndex(s => (core.Arrangement?.RoleOf(s) ?? LaneRole.Main) == LaneRole.Main));
                if (index < 0 || index >= lanes.Count) throw new ToolError($"{m.Name} has {lanes.Count} lane(s), numbered 0 to {Math.Max(0, lanes.Count - 1)}; arrangement_get lists them.");
                var items = new ClipEditor(core).Read(lanes[index]);
                ClipItem found = null;
                if (c.Int("clip") is int id) found = items.FirstOrDefault(i => i.Spec.Id == id);
                else
                {
                    int t = SongModel.ParseTicks(core, c.Arguments["at"], "at");
                    found = items.FirstOrDefault(i => i.Time <= t && t < i.End);
                }
                return found?.Pattern ?? throw new ToolError($"No such clip on {m.Name}'s lane {index}. arrangement_get lists the clips.");
            }
            throw new ToolError("Say which clip: pattern=<its name>, scene=<Session scene>, or lane with clip=<id> or at=<a position inside it>.");
        }

        static JsonObject Run(ReBuzzCore core, ToolContext c, string op)
        {
            var m = SongModel.Machine(core, c.RequiredString("machine"));
            var pattern = ClipOf(core, m, c);
            int tpb = SongModel.Tpb(core), bpb = SongModel.BeatsPerBar(core);
            int length = pattern.Length * NoteList.TimeBase;
            var editor = new ClipEnvelopeEditor(core);
            var problems = new JsonArray();
            if (op is not ("read" or "write" or "clear")) throw new ToolError("op: read, write or clear.");

            if (op == "read")
            {
                string only = c.String("param");
                int? onlyTrack = c.Int("track");
                var keys = ClipEnvelopes.KeysWithData(pattern);
                var shown = new JsonArray();
                foreach (var key in keys)
                {
                    var e = ClipEnvelopes.Read(pattern, key);
                    if (e == null) continue;
                    if (only != null && !e.Param.Parameter.Name.Equals(only, StringComparison.OrdinalIgnoreCase) && !e.Param.Name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                    if (onlyTrack != null && key.Track != onlyTrack) continue;
                    shown.Add(EnvelopeJson(e, tpb));
                }
                return Result(m, pattern, tpb, shown, problems);
            }

            string paramName = c.String("param") ?? throw new ToolError($"{op} needs param (machine_describe lists them).");
            var param = SongModel.Param(m, SongModel.Params(m), paramName, track: c.Int("track") != null);
            int track = c.Int("track") ?? 0;
            if (!param.IsTrack && track != 0) throw new ToolError($"{param.Name} is a global parameter; leave track out.");
            if (param.IsTrack && track >= m.TrackCount) throw new ToolError($"{m.Name} has {m.TrackCount} track(s).");
            var env = ClipEnvelopes.Read(pattern, new ColumnKey(param.Group, param.Parameter.IndexInGroup, track))
                      ?? throw new ToolError($"{param.Name} can't be automated in a clip (only state parameters the song keeps: machine_describe's parameters).");

            if (op == "clear")
            {
                int from = 0, to = int.MaxValue;
                if (c.Arguments["from"] != null) from = NoteList.Time(SongTime.ParseBeats(c.Arguments["from"], bpb, "from", "the clip start"), tpb);
                if (c.Arguments["to"] != null) to = NoteList.Time(SongTime.ParseBeats(c.Arguments["to"], bpb, "to", "the clip start"), tpb);
                if (to <= from) throw new ToolError("to must be after from.");
                if (!env.Points.Any(p => p.Time >= from && p.Time < to)) throw new ToolError($"{param.Name} has no points in \"{pattern.Name}\" there; clip_envelope(op=\"read\") shows where it moves.");
                Check(editor.Edit(env, pts => pts.Where(p => p.Time < from || p.Time >= to).ToList()));
                return Result(m, pattern, tpb, new JsonArray(Describe(core, pattern, env.Key, tpb)), problems);
            }

            // ---- write
            var list = c.List("points");
            if (list == null || list.Count == 0) throw new ToolError("write needs points (at least one): [{\"at\": 0, \"value\": \"300 Hz\"}, …].");
            bool allowUnsafe = c.Bool("allow_unsafe") ?? false;
            bool linear = (c.String("shape") ?? "linear") == "linear";
            string unsafeWhy = null;
            int current = param.Parameter.GetValue(track);
            var points = new List<AutoPoint>();
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i]!.AsObject();
                int time = NoteList.Time(SongTime.ParseBeats(o["at"], bpb, $"points[{i}].at", "the clip start"), tpb);
                if (time < 0 || time >= length)
                    throw new ToolError($"points[{i}]: beat {NoteList.Fmt(NoteList.Beats(time, tpb))} is outside the clip (0 to {NoteList.Fmt(NoteList.Beats(length, tpb))} beats, the end excluded).");
                int raw;
                try { raw = ValueResolver.Resolve(param.Info, o["value"], current); }
                catch (ToolError e) { throw new ToolError($"points[{i}].value: {e.Message}"); }
                string refusal = SafeRange.Refusal(param.Info, raw);
                if (refusal != null && !allowUnsafe) throw new ToolError($"points[{i}].value: {refusal}");
                if (refusal != null) unsafeWhy ??= $"outside its safe range, {param.Info.SafeRange.Text(param.Info.Describe)}: {param.Info.SafeRange.Reason}";
                points.Add(new AutoPoint(time, raw, o["ramp"] is JsonValue rv && rv.TryGetValue(out bool b) ? b : linear));
            }
            points = points.OrderBy(p => p.Time).ToList();
            int first = points[0].Time, last = points[^1].Time;
            bool all = c.Bool("replace") == true;
            Check(editor.Edit(env, old => (all ? new List<AutoPoint>() : old.Where(p => p.Time < first || p.Time > last).ToList()).Concat(points).ToList()));
            if (unsafeWhy != null) problems.Add($"Unsafe (allow_unsafe): {param.Name} goes {unsafeWhy}.");
            if (SongModel.Placements(core, pattern).Count == 0 && !InSession(core, pattern))
                problems.Add($"\"{pattern.Name}\" isn't in a Session slot or the arrangement, so the envelope doesn't play yet.");
            return Result(m, pattern, tpb, new JsonArray(Describe(core, pattern, env.Key, tpb)), problems);
        }

        static bool InSession(ReBuzzCore core, IPattern pattern)
        {
            var layout = core.SessionLayout;
            var doc = layout?.Document;
            if (doc == null) return false;
            for (int t = 0; t < layout.TrackCount; t++)
                if (layout.SequenceOf(t)?.Machine == pattern.Machine)
                    for (int s = 0; s < doc.Scenes.Count; s++)
                        if (doc.GetSlot(t, s) == pattern.Name) return true;
            return false;
        }

        static void Check(string why)
        {
            if (why != null) throw new ToolError(why);
        }

        static JsonNode Describe(ReBuzzCore core, IPattern pattern, ColumnKey key, int tpb) => EnvelopeJson(ClipEnvelopes.Read(pattern, key), tpb);

        static JsonObject Result(MachineCore m, IPattern pattern, int tpb, JsonArray envelopes, JsonArray problems) => new JsonObject
        {
            ["machine"] = m.Name,
            ["pattern"] = pattern.Name,
            ["length_beats"] = NoteList.Beats(pattern.Length * NoteList.TimeBase, tpb),
            ["envelopes"] = envelopes,
            ["problems"] = problems,
        };

        static JsonObject EnvelopeJson(ClipEnvelope e, int tpb)
        {
            var points = new JsonArray();
            foreach (var p in e.Points)
                points.Add(new JsonObject { ["at"] = NoteList.Beats(p.Time, tpb), ["value"] = e.Param.Describe(p.Value), ["ramp"] = p.Ramp });
            var o = new JsonObject { ["param"] = e.Param.Parameter.Name };
            if (e.Param.Parameter.Group.Type == ParameterGroupType.Track) o["track"] = e.Key.Track;
            o["points"] = points;
            if (e.Tracker != null)
            {
                var steps = new JsonArray();
                foreach (var p in e.Tracker.Points)
                    steps.Add(new JsonObject { ["at"] = NoteList.Beats(p.Time, tpb), ["value"] = e.Param.Describe(p.Value) });
                o["tracker"] = steps;
            }
            o["overlaps"] = e.Overlaps.Count;
            return o;
        }
    }
}
