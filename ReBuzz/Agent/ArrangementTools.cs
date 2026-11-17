// Agent server (agent-knowledge.md K3b, daw-modes.md §7.3 and §8.2 to §8.6): the arrangement's clips, lanes, automation lanes and
// takes. arrangement_clip, arrangement_lane, arrangement_automation, arrangement_takes.
//
// Each tool calls the model code the studio's arrangement view uses (ClipEditor, AutomationEditor, ArrangementRecorder in
// ReBuzz/Core), so a clip the agent splits is the clip a person splits, and each call is one undo step (host.Engine groups it).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using static ReBuzz.Agent.Mcp.Schema;
using SongTime = ReBuzz.Agent.Model.SongTime;

namespace ReBuzz.Agent
{
    internal static class ArrangementTools
    {
        static JsonObject Position(string description) =>
            AnyOf(description, Number("Beats from the start (0 = the start)", 0), String("\"bar.beat\": \"5.1\" is bar 5's first beat"));

        static string Role(ReBuzzCore core, ISequence s) => (core.Arrangement?.RoleOf(s) ?? LaneRole.Main).ToString().ToLowerInvariant();

        static List<ISequence> LanesOf(ReBuzzCore core, IMachine m) => core.SongCore.Sequences.Where(s => s.Machine == m).ToList();

        /// <summary>The machine's lane: <paramref name="index"/> among its lanes, else its first lane of <paramref name="role"/>.</summary>
        static ISequence Lane(ReBuzzCore core, MachineCore m, int? index, LaneRole role = LaneRole.Main)
        {
            var lanes = LanesOf(core, m);
            if (index is int i)
            {
                if (i < 0 || i >= lanes.Count)
                    throw new ToolError(lanes.Count == 0
                        ? $"{m.Name} has no lanes yet: arrangement_lane(op=\"add\", machine=\"{m.Name}\") makes one, or arrangement_place does."
                        : $"{m.Name} has {lanes.Count} lane(s), numbered 0 to {lanes.Count - 1}; arrangement_get lists them with their roles.");
                return lanes[i];
            }
            var found = lanes.FirstOrDefault(s => (core.Arrangement?.RoleOf(s) ?? LaneRole.Main) == role);
            if (found == null)
                throw new ToolError(lanes.Count == 0
                    ? $"{m.Name} has no lanes yet: arrangement_lane(op=\"add\", machine=\"{m.Name}\") makes one, or arrangement_place does."
                    : $"{m.Name} has no {role.ToString().ToLowerInvariant()} lane; arrangement_lane(op=\"add\", machine=\"{m.Name}\", role=\"{role.ToString().ToLowerInvariant()}\") makes one.");
            return found;
        }

        static JsonObject ClipJson(ReBuzzCore core, ClipItem it)
        {
            var s = it.Spec;
            var o = new JsonObject
            {
                ["pattern"] = it.Pattern.Name,
                ["at"] = SongModel.Beats(core, it.Time),
                ["ends"] = SongModel.Beats(core, it.End),
                ["start_beats"] = SongModel.Beats(core, s.Start),
                ["loop"] = new JsonObject
                {
                    ["start_beats"] = SongModel.Beats(core, s.LoopStart),
                    ["end_beats"] = SongModel.Beats(core, s.RegionEnd),
                    ["on"] = s.Loop,
                },
            };
            if (s.Id != 0) o["clip"] = s.Id;
            if (s.Gain != 1.0) o["gain"] = Math.Round(s.Gain, 4);
            if (core.SessionLayout?.AudioOf(it.Pattern) is { } a)
            {
                // neo-audio.md (NA2): an audio clip's own settings (its pattern is its own; copies share the audio asset only).
                var audio = new JsonObject { ["warp"] = a.Warp, ["gain_db"] = Math.Round(a.GainDb, 3) };
                if (a.FadeIn > 0) audio["fade_in_beats"] = Math.Round(a.FadeIn, 4);
                if (a.FadeOut > 0) audio["fade_out_beats"] = Math.Round(a.FadeOut, 4);
                if (a.Tail > 0) audio["crossfade_beats"] = Math.Round(a.Tail, 4);
                if (a.Reverse) audio["reverse"] = true;
                o["audio"] = audio;
            }
            return o;
        }

        static JsonArray ClipsJson(ReBuzzCore core, ClipEditor editor, ISequence seq) =>
            new(editor.Read(seq).Take(64).Select(i => (JsonNode)ClipJson(core, i)).ToArray());

        static string ClipList(ReBuzzCore core, List<ClipItem> items) =>
            items.Count == 0 ? "The lane has no clips." : "Clips there: " + string.Join(", ", items.Take(12).Select(i =>
                $"\"{i.Pattern.Name}\" {NoteList.Fmt(SongModel.Beats(core, i.Time))}-{NoteList.Fmt(SongModel.Beats(core, i.End))}")) + (items.Count > 12 ? ", …" : "") + ".";

        static ClipItem FindClip(ReBuzzCore core, ClipEditor editor, ISequence seq, ToolContext c, string name)
        {
            var items = editor.Read(seq);
            if (c.Int("clip") is int id)
                return items.FirstOrDefault(i => i.Spec.Id == id) ?? throw new ToolError($"No clip {id} on {name}. " + ClipList(core, items));
            if (c.Arguments["at"] != null)
            {
                int t = SongModel.ParseTicks(core, c.Arguments["at"], "at");
                return items.FirstOrDefault(i => i.Time <= t && t < i.End)
                    ?? throw new ToolError($"No clip at beat {NoteList.Fmt(SongModel.Beats(core, t))} on {name}. " + ClipList(core, items));
            }
            throw new ToolError("Say which clip: clip=<its id, from arrangement_get> or at=<a position inside it>.");
        }

        static bool AudioMachine(IMachine m) => ReBuzz.Core.Session.SessionLayout.IsClipPlayer(m);

        static void Check(string why, string hint = null)
        {
            if (why != null) throw new ToolError(why + (hint != null ? " " + hint : ""));
        }

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            var clipSchema = Object(null,
                Opt("clip", Integer("The clip's id (arrangement_get), when it has one")),
                Req("pattern", String("Its pattern")), Req("at", Number("Start, beats")), Req("ends", Number("End, beats")),
                Req("start_beats", Number("Where its first play starts in the pattern, beats")),
                Req("loop", Object(null, Req("start_beats", Number("Loop start in the pattern")), Req("end_beats", Number("Loop end in the pattern")), Req("on", Boolean("Whether it loops")))),
                Opt("gain", Number("Volume, 0-1 (1 is not shown)")),
                Opt("audio", Object(null, Req("warp", String("Warp mode")), Req("gain_db", Number("Gain, dB")), Opt("fade_in_beats", Number("Fade in")), Opt("fade_out_beats", Number("Fade out")),
                    Opt("crossfade_beats", Number("The clip's audio goes on this long under the clip that follows (a crossfade)")), Opt("reverse", Boolean("Plays backwards")))));

            tools.Add(new ToolDef
            {
                Name = "arrangement_clip",
                Title = "Edit arrangement clips",
                Description =
                    "Edits the clips of a machine's lane in the arrangement, as the Arrange view's mouse and keys do. A clip is a pattern played from a place, " +
                    "trimmed, looped or repeated. op: add (an existing pattern as a clip at `at`, length_beats long, default the pattern's), draw (a new empty " +
                    "pattern as a clip, length_beats), move (to `to`, on lane to_lane), copy (to `to`, default right after it; an independent pattern, or " +
                    "link=true the same one), trim (start and/or end, beats), loop (on=true/false), split (at split_at), join (with the clip that starts " +
                    "where it ends), make_unique (a pattern of its own). Audio clips (Neo Audio tracks; each has a pattern of its own, copies share the audio only) " +
                    "also take: fade (fade_in_beats, fade_out_beats), gain (gain_db), crossfade (length_beats: with the clip that starts where it ends; 0 removes it), " +
                    "reverse (toggles), stretch (end: warps the audio to fill the clip up to there; not with warp Off or loop on) and consolidate (renders the clip, " +
                    "or the audio clips between start and end, into a new audio asset and one plain clip; one tempo, the song's now). move and copy may go to another " +
                    "Neo Audio machine (to_machine); split makes two clips with a pattern each. A clip moved or trimmed over an audio clip crossfades with it. " +
                    "The clip is clip=<id> or at=<a position inside it>; lane is the number " +
                    "arrangement_get shows (default the main lane). To delete a clip use arrangement_clear. One undo step. Example: " +
                    "arrangement_clip(op=\"trim\", machine=\"Bass\", at=\"2.1\", end=\"3.1\").",
                Input = Object(null,
                    Req("op", String("What to do", "add", "draw", "move", "copy", "trim", "loop", "split", "join", "make_unique", "fade", "gain", "crossfade", "reverse", "stretch", "consolidate")),
                    Req("machine", String("The machine")),
                    Opt("lane", Integer("The machine's lane number (arrangement_get; default its first main lane)", 0)),
                    Opt("clip", Integer("The clip's id")),
                    Opt("at", Position("add/draw: where it starts. Others: a position inside the clip")),
                    Opt("pattern", String("add: the pattern")),
                    Opt("length_beats", Number("add/draw: its length, beats", 0.0001)),
                    Opt("to", Position("move/copy: the new start")),
                    Opt("to_lane", Integer("move/copy: another lane of the machine", 0)),
                    Opt("link", Boolean("copy: play the same pattern (edits show in both) instead of a pattern of its own")),
                    Opt("start", Position("trim: the clip now starts here (what plays there goes on playing)")),
                    Opt("end", Position("trim: the clip now ends here (with a loop it repeats to there)")),
                    Opt("on", Boolean("loop: whether the clip loops")),
                    Opt("split_at", Position("split: where to cut")),
                    Opt("to_machine", String("move/copy of an audio clip: another Neo Audio machine (its lane given by to_lane, default its first)")),
                    Opt("fade_in_beats", Number("fade: the fade in, beats (0 none)", 0)),
                    Opt("fade_out_beats", Number("fade: the fade out, beats (0 none)", 0)),
                    Opt("gain_db", Number("gain: the clip's gain, dB", -70, 24)),
                    Opt("start_at", Position("consolidate: start of the range (default the clip's start)")),
                    Opt("end_at", Position("consolidate: end of the range (default the clip's end)"))),
                Output = Object(null, Req("machine", String("Machine")), Req("lane", Integer("The lane edited")), Req("op", String("What was done")), Req("done", String("What happened, in words")),
                    Req("clips", Array(clipSchema, "The lane's clips now (the first 64)"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("arrangement_clip", true, _ =>
                    {
                        string op = c.RequiredString("op");
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var editor = new ClipEditor(core);
                        ISequence seq;
                        if (op is "add" or "draw" && LanesOf(core, m).Count == 0 && c.Int("lane") == null)
                        {
                            AgentActions.Do(core, new AddSequenceAction(m));
                            seq = Lane(core, m, 0);
                        }
                        else seq = Lane(core, m, c.Int("lane"));
                        string name = $"{m.Name} lane {LanesOf(core, m).IndexOf(seq)}";
                        string done;
                        int Pos(string arg) => c.Arguments[arg] != null ? SongModel.ParseTicks(core, c.Arguments[arg], arg) : throw new ToolError($"{op} needs {arg}.");
                        switch (op)
                        {
                            case "add":
                            {
                                var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                                int len = c.Number("length_beats") is double lb ? Math.Max(1, SongModel.Ticks(core, lb)) : 0;
                                Check(editor.Place(seq, p, Pos("at"), len));
                                done = $"placed \"{p.Name}\"";
                                break;
                            }
                            case "draw":
                            {
                                if (c.Number("length_beats") is not double lb) throw new ToolError("draw needs length_beats.");
                                Check(editor.Draw(seq, Pos("at"), Math.Max(1, SongModel.Ticks(core, lb)), out var made));
                                done = $"drew a new pattern \"{made?.Name}\"";
                                break;
                            }
                            case "move":
                            case "copy":
                            {
                                var it = FindClip(core, editor, seq, c, name);
                                var target = c.Int("to_lane") != null ? Lane(core, m, c.Int("to_lane")) : seq;
                                if (c.String("to_machine") is string toName)
                                {
                                    var other = SongModel.Machine(core, toName, "to_machine");
                                    if (!AudioMachine(other)) throw new ToolError($"to_machine: {other.Name} isn't a Neo Audio machine (only audio clips go to another machine).");
                                    if (LanesOf(core, other).Count == 0) AgentActions.Do(core, new AddSequenceAction(other));
                                    target = Lane(core, other, c.Int("to_lane"));
                                }
                                int to = c.Arguments["to"] != null ? Pos("to") : op == "copy" ? it.End : throw new ToolError("move needs to.");
                                var mode = op == "move" ? ClipEditor.CopyMode.Move : c.Bool("link") == true ? ClipEditor.CopyMode.Linked : ClipEditor.CopyMode.Independent;
                                Check(editor.Move(it, target, to, mode));
                                done = op == "move" ? "moved" : mode == ClipEditor.CopyMode.Linked && !editor.IsAudio(it.Pattern) ? "copied (same pattern)" : "copied (a pattern of its own)";
                                if (target != seq) { seq = target; m = (MachineCore)target.Machine; }
                                break;
                            }
                            case "trim":
                            {
                                if (c.Arguments["start"] == null && c.Arguments["end"] == null) throw new ToolError("trim needs start and/or end.");
                                var it = FindClip(core, editor, seq, c, name);
                                int clipId = it.Spec.Id, firstTime = it.Time;
                                if (c.Arguments["start"] != null) Check(editor.TrimLeft(it, Pos("start")));
                                if (c.Arguments["end"] != null)
                                {
                                    // The left trim rewrote the clip: find it again, by its id or its (new) start.
                                    var again = editor.Read(seq).FirstOrDefault(i => clipId != 0 ? i.Spec.Id == clipId : i.Time == Math.Max(0, Math.Min(Pos("start"), it.End - 1)))
                                                ?? editor.Read(seq).FirstOrDefault(i => i.Time <= firstTime && firstTime < i.End) ?? it;
                                    Check(editor.TrimRight(again, Pos("end")));
                                }
                                done = "trimmed";
                                break;
                            }
                            case "loop":
                            {
                                if (c.Bool("on") is not bool on) throw new ToolError("loop needs on=true or on=false.");
                                Check(editor.SetLoop(FindClip(core, editor, seq, c, name), on));
                                done = on ? "loop on" : "loop off";
                                break;
                            }
                            case "split":
                            {
                                var it = FindClip(core, editor, seq, c, name);
                                int at = Pos("split_at");
                                Check(editor.Split(it, at), $"The clip runs from beat {NoteList.Fmt(SongModel.Beats(core, it.Time))} to {NoteList.Fmt(SongModel.Beats(core, it.End))}.");
                                done = "split";
                                break;
                            }
                            case "join":
                            {
                                var a = FindClip(core, editor, seq, c, name);
                                var b = editor.Read(seq).FirstOrDefault(i => i.Time == a.End)
                                        ?? throw new ToolError($"No clip starts where this one ends (beat {NoteList.Fmt(SongModel.Beats(core, a.End))}). " + ClipList(core, editor.Read(seq)));
                                Check(editor.Join(a, b));
                                done = "joined";
                                break;
                            }
                            case "make_unique":
                            {
                                var it = FindClip(core, editor, seq, c, name);
                                Check(editor.MakeUnique(it));
                                done = $"gave the clip a pattern of its own (a copy of \"{it.Pattern.Name}\")";
                                break;
                            }
                            case "fade":
                            {
                                if (c.Number("fade_in_beats") == null && c.Number("fade_out_beats") == null) throw new ToolError("fade needs fade_in_beats and/or fade_out_beats.");
                                var it = FindClip(core, editor, seq, c, name);
                                int? fi = c.Number("fade_in_beats") is double a ? SongModel.Ticks(core, a) : null;
                                int? fo = c.Number("fade_out_beats") is double b ? SongModel.Ticks(core, b) : null;
                                Check(editor.Fade(it, fi, fo));
                                done = "set the fades";
                                break;
                            }
                            case "gain":
                            {
                                if (c.Number("gain_db") is not double db) throw new ToolError("gain needs gain_db (-70 to 24).");
                                Check(editor.SetGain(FindClip(core, editor, seq, c, name), db));
                                done = $"gain {db:0.##} dB";
                                break;
                            }
                            case "crossfade":
                            {
                                if (c.Number("length_beats") is not double lb) throw new ToolError("crossfade needs length_beats (0 removes it).");
                                Check(editor.Crossfade(FindClip(core, editor, seq, c, name), lb <= 0 ? 0 : Math.Max(1, SongModel.Ticks(core, lb))));
                                done = lb <= 0 ? "removed the crossfade" : "crossfaded with the clip that follows";
                                break;
                            }
                            case "reverse":
                            {
                                Check(editor.Reverse(FindClip(core, editor, seq, c, name)));
                                done = "reversed (the same part of the audio, backwards)";
                                break;
                            }
                            case "stretch":
                            {
                                var it = FindClip(core, editor, seq, c, name);
                                Check(editor.Stretch(it, Pos("end")), "Say where the clip should now end (end), and use warp Beats, Repitch, Complex or Pro with the loop off.");
                                done = "stretched";
                                break;
                            }
                            case "consolidate":
                            {
                                var it = FindClip(core, editor, seq, c, name);
                                int from = c.Arguments["start_at"] != null ? Pos("start_at") : it.Time, to = c.Arguments["end_at"] != null ? Pos("end_at") : it.End;
                                Check(editor.Consolidate(seq, from, to, out var made));
                                done = $"consolidated into a new audio clip \"{made?.Name}\" (rendered at {core.BPM} BPM)";
                                break;
                            }
                            default:
                                throw new ToolError("op: add, draw, move, copy, trim, loop, split, join, make_unique, fade, gain, crossfade, reverse, stretch or consolidate.");
                        }
                        return new JsonObject
                        {
                            ["machine"] = m.Name, ["lane"] = LanesOf(core, m).IndexOf(seq), ["op"] = op, ["done"] = done,
                            ["clips"] = ClipsJson(core, editor, seq),
                        };
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]} lane {r["lane"]}: {r["done"]}. {r["clips"]!.AsArray().Count} clip(s) on the lane now.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "arrangement_lane",
                Title = "Add or remove an arrangement lane",
                Description =
                    "Adds a lane to a machine's track in the arrangement, or removes an empty one. role: main (the default: pattern clips), take (a lane " +
                    "recorded takes go to, for comping) or automation (a lane of automation clips). arrangement_get lists each machine's lanes with " +
                    "their numbers and roles. remove refuses a lane that still has clips unless remove_clips=true. One undo step. " +
                    "Example: arrangement_lane(op=\"add\", machine=\"Lead\", role=\"take\").",
                Input = Object(null,
                    Req("op", String("add or remove", "add", "remove")),
                    Req("machine", String("The machine")),
                    Opt("role", String("add: the lane's role (default main)", "main", "take", "automation")),
                    Opt("lane", Integer("remove: the lane number", 0)),
                    Opt("remove_clips", Boolean("remove: also remove the clips on it"))),
                Output = Object(null, Req("machine", String("Machine")), Req("done", String("What happened, in words")),
                    Req("lanes", Array(Object(null, Req("lane", Integer("Lane number")), Req("role", String("main, take or automation")), Req("events", Integer("Places on it"))), "The machine's lanes now"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("arrangement_lane", true, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        string op = c.RequiredString("op");
                        if (SongModel.IsMaster(m)) throw new ToolError("Master has no arrangement lanes of its own to add; it plays patterns on its own lane (arrangement_place).");
                        string done;
                        if (op == "add")
                        {
                            string role = c.String("role") ?? "main";
                            var before = LanesOf(core, m).Count;
                            switch (role)
                            {
                                case "main": AgentActions.Do(core, new AddSequenceAction(m)); break;
                                case "take":
                                    if (before == 0) throw new ToolError($"A take lane records into the machine's main lane: add that first (arrangement_lane(op=\"add\", machine=\"{m.Name}\")).");
                                    AgentActions.Do(core, new AddTakeLaneAction(core, m));
                                    break;
                                case "automation": AgentActions.Do(core, new AddAutomationLaneAction(core, m)); break;
                                default: throw new ToolError("role: main, take or automation.");
                            }
                            if (LanesOf(core, m).Count == before) throw new ToolError($"{Product.Name} didn't make a lane for {m.Name}.");
                            done = $"added a {role} lane";
                        }
                        else if (op == "remove")
                        {
                            var seq = Lane(core, m, c.Int("lane") ?? throw new ToolError("remove needs lane (the number arrangement_get shows)."));
                            if (seq.Events.Count > 0 && c.Bool("remove_clips") != true)
                                throw new ToolError($"Lane {LanesOf(core, m).IndexOf(seq)} still has {seq.Events.Count} place(s): arrangement_clear empties it, or pass remove_clips=true.");
                            AgentActions.Do(core, new DeleteTrackAction(seq));
                            done = "removed";
                        }
                        else throw new ToolError("op: add or remove.");
                        var lanes = LanesOf(core, m);
                        return new JsonObject
                        {
                            ["machine"] = m.Name, ["done"] = done,
                            ["lanes"] = new JsonArray(lanes.Select((s, i) => (JsonNode)new JsonObject { ["lane"] = i, ["role"] = Role(core, s), ["events"] = s.Events.Count }).ToArray()),
                        };
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]}: {r["done"]}. Lanes: " + string.Join(", ", r["lanes"]!.AsArray().Select(l => $"{l!["lane"]} {l["role"]}")) + ".", r);
                },
            });

            AutomationTool(tools, host);
            TakesTool(tools, host);

            tools.Add(new ToolDef
            {
                Name = "song_meter",
                Title = "Read or set the song's bars",
                Description =
                    "The song's bar list (what the sequence editor's ruler shows, and what \"bar.beat\" positions count in): each entry says from which beat " +
                    "the bars are that many beats long. With bar_beats, sets the bar length from a position on (from, default the start); a bar that long " +
                    "repeats until the next entry. Without it, reads. One undo step. Example: song_meter(from=\"9.1\", bar_beats=3).",
                Input = Object(null,
                    Opt("bar_beats", Number("The bar length in beats from `from` on (3 for 3/4, 4 for 4/4)", 0.25, 64)),
                    Opt("from", Position("Where the new bar length starts (default 0)"))),
                Output = Object(null, Req("meter", Array(Object(null, Req("from_beats", Number("From here")), Req("bar_beats", Number("Each bar's length, beats"))), "The bars"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("song_meter", c.Number("bar_beats") != null, _ =>
                    {
                        if (c.Number("bar_beats") is double beats)
                        {
                            int step = SongModel.Ticks(core, beats);
                            if (step < 1) throw new ToolError("bar_beats is shorter than a tick.");
                            int time = c.Arguments["from"] != null ? SongModel.ParseTicks(core, c.Arguments["from"], "from") : 0;
                            var meter = core.SongMeter;
                            var old = new SongMeter(meter);
                            AgentActions.Do(core, new DelegateAction(() => meter.Set(time, step), () => meter.Clone(old), "Set bar length"));
                        }
                        return new JsonObject
                        {
                            ["meter"] = new JsonArray(core.SongMeter.Entries.OrderBy(e => e.Key).Select(m => (JsonNode)new JsonObject
                            {
                                ["from_beats"] = SongModel.Beats(core, m.Key), ["bar_beats"] = SongModel.Beats(core, m.Value),
                            }).ToArray()),
                        };
                    }, c.Cancellation);
                    return ToolResult.Ok("Bars: " + string.Join(", ", r["meter"]!.AsArray().Select(b => $"{b!["bar_beats"]} beats from beat {b["from_beats"]}")) + ".", r);
                },
            });
        }

        // ------------------------------------------------------------------ automation lanes

        static void AutomationTool(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "arrangement_automation",
                Title = "Automation lanes in the arrangement",
                Description =
                    "A parameter's movement over the song, as the Arrange view's automation lanes show it: automation clips on an automation lane, " +
                    "the pattern columns of placed patterns, and tracker rows, with their overlaps. op: list (machine and param optional), " +
                    "add_lane (shows the parameter's lane under the track), write (an automation clip from `from` to `to` holding `points`: " +
                    "at = a position in the song, value as automation_write takes it; linear ramps between points, or shape=step; a clip already there " +
                    "is edited), clear (removes the points between from and to; a clip wholly inside goes). Values outside the parameter's safe range are " +
                    "refused unless allow_unsafe=true. To write inside one pattern use automation_write. One undo step. " +
                    "Example: arrangement_automation(op=\"write\", machine=\"Pad\", param=\"F1 cutoff\", from=0, to=16, points=[{\"at\": 0, \"value\": \"300 Hz\"}, {\"at\": 16, \"value\": \"4 kHz\"}]).",
                Input = Object(null,
                    Req("op", String("What to do", "list", "add_lane", "write", "clear")),
                    Opt("machine", String("The machine (required except for list)")),
                    Opt("param", String("Parameter name")),
                    Opt("track", Integer("Track, for track parameters", 0)),
                    Opt("from", Position("write/clear: the start")),
                    Opt("to", Position("write/clear: the end (exclusive); write's default is the last point rounded up to a beat")),
                    Opt("points", Array(Object("A point",
                        Req("at", Position("Where in the song")),
                        Req("value", AnyOf("Value in units", String("\"800 Hz\", \"-6 dB\", a choice, raw:N"), Number("In the parameter's unit"))),
                        Opt("ramp", Boolean("Whether the line after this point is a ramp (default per shape)"))), "Points in time order", 1)),
                    Opt("shape", String("linear (default) ramps between points; step holds", "linear", "step")),
                    Opt("allow_unsafe", Boolean("Write values outside the parameter's safe range anyway"))),
                Output = Object(null,
                    Opt("lanes", Array(Object(null,
                        Req("machine", String("Machine")), Req("param", String("Parameter")), Opt("track", Integer("Track")),
                        Req("shown", Boolean("Its lane is shown under the track")),
                        Req("segments", Array(Object(null,
                            Req("source", String("clip: an automation clip; pattern: a placed pattern's column; tracker: the Pattern tab's rows", "clip", "pattern", "tracker")),
                            Req("pattern", String("Its pattern")), Req("at", Number("Start, beats")), Req("ends", Number("End, beats")),
                            Req("points", Array(Object(null, Req("at", Number("Beats")), Req("value", String("Shown value")), Req("ramp", Boolean("Ramps to the next point"))), "Points it plays"))), "Where it moves")),
                        Req("overlaps", Integer("Places two writers set it at once"))), "Per parameter")),
                    Opt("clip", Object(null, Req("pattern", String("The clip's pattern")), Req("lane", Integer("Its automation lane")), Req("at", Number("Start, beats")), Req("ends", Number("End, beats")),
                        Req("points", Integer("Points in it")))),
                    Opt("removed", Integer("clear: points removed")),
                    Opt("problems", Array(String("A note"), "Things to know"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string op = c.RequiredString("op");
                    var r = await host.Engine("arrangement_automation", op != "list", _ => Automation(core, c, op), c.Cancellation);
                    string text = op switch
                    {
                        "list" => r["lanes"]!.AsArray().Count == 0 ? "No automation in the arrangement." :
                            string.Join("; ", r["lanes"]!.AsArray().Select(l => $"{l!["machine"]} {l["param"]}: " + l["segments"]!.AsArray().Count + " place(s)")),
                        "add_lane" => $"Showing the {r["lanes"]![0]!["param"]} lane under {r["lanes"]![0]!["machine"]}.",
                        "write" => $"Automation clip \"{r["clip"]!["pattern"]}\" from beat {r["clip"]!["at"]} to {r["clip"]!["ends"]} on lane {r["clip"]!["lane"]}, {r["clip"]!["points"]} point(s)." +
                                   (r["problems"]!.AsArray().Count > 0 ? " " + string.Join(" ", r["problems"]!.AsArray().Select(x => x!.GetValue<string>())) : ""),
                        _ => $"Removed {r["removed"]} point(s).",
                    };
                    return ToolResult.Ok(text, r);
                },
            });
        }

        static JsonObject Automation(ReBuzzCore core, ToolContext c, string op)
        {
            int tpb = SongModel.Tpb(core);
            const int unit = AutomationEditor.Unit;
            if (op == "list")
            {
                var wanted = new List<(IMachine Machine, ColumnKey Key)>();
                var machines = c.String("machine") is string name ? new List<MachineCore> { SongModel.Machine(core, name) } : SongModel.Machines(core).ToList();
                string only = c.String("param");
                foreach (var m in machines)
                    foreach (var p in AutomationParams.List(m))
                        if (only == null || p.Parameter.Name.Equals(only, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(only, StringComparison.OrdinalIgnoreCase))
                            wanted.Add((m, p.Key));
                var lanes = new JsonArray();
                var read = AutomationReader.ReadAll(core, wanted);
                for (int i = 0; i < read.Count; i++)
                {
                    var lane = read[i];
                    var (machine, key) = wanted[i];
                    bool shown = core.Arrangement?.HasParameterLane(machine.Name, key) == true;
                    if (lane.Segments.All(s => s.Points.Count == 0) && !shown) continue;
                    var segs = new JsonArray();
                    // The reader gives every parameter a place in an automation clip with no points of its own (a place a point can be put): not a movement.
                    foreach (var s in lane.Segments.Where(s => s.Points.Count > 0))
                    {
                        var pts = new JsonArray();
                        foreach (var p in s.Points.Where(p => s.Plays(p.Time)))
                            pts.Add(new JsonObject { ["at"] = NoteList.Beats(s.ToSong(p.Time), tpb), ["value"] = lane.Param.Describe(p.Value), ["ramp"] = p.Ramp });
                        segs.Add(new JsonObject
                        {
                            ["source"] = s.Source switch { AutoSource.AutomationClip => "clip", AutoSource.ClipColumn => "pattern", _ => "tracker" },
                            ["pattern"] = s.Pattern.Name,
                            ["at"] = NoteList.Beats(s.Start, tpb), ["ends"] = NoteList.Beats(s.End, tpb),
                            ["points"] = pts,
                        });
                    }
                    var o = new JsonObject { ["machine"] = machine.Name, ["param"] = lane.Param.Parameter.Name, ["shown"] = shown, ["segments"] = segs, ["overlaps"] = lane.Overlaps.Count };
                    if (key.Track != 0 || lane.Param.Parameter.Group.Type == ParameterGroupType.Track) o["track"] = key.Track;
                    lanes.Add(o);
                }
                return new JsonObject { ["lanes"] = lanes };
            }

            var mach = SongModel.Machine(core, c.String("machine") ?? throw new ToolError($"{op} needs machine."));
            AgentParam param;
            string paramName = c.String("param") ?? throw new ToolError($"{op} needs param (machine_describe lists them).");
            param = SongModel.Param(mach, SongModel.Params(mach), paramName, track: c.Int("track") != null);
            int track = c.Int("track") ?? 0;
            if (!param.IsTrack && track != 0) throw new ToolError($"{param.Name} is a global parameter; leave track out.");
            if (param.IsTrack && track >= mach.TrackCount) throw new ToolError($"{mach.Name} has {mach.TrackCount} track(s).");
            var key2 = new ColumnKey(param.Group, param.Parameter.IndexInGroup, track);
            var auto = AutomationParams.Find(mach, key2) ?? throw new ToolError($"{param.Name} can't be automated in the arrangement (only parameters the song keeps: machine_describe's parameters).");
            var editor = new ClipEditor(core);
            var automation = new AutomationEditor(core, editor);

            if (op == "add_lane")
            {
                string machineName = mach.Name;
                if (core.Arrangement == null) throw new ToolError("This host has no arrangement view state.");
                if (core.Arrangement.HasParameterLane(machineName, key2)) throw new ToolError($"The {param.Name} lane is shown already.");
                AgentActions.Do(core, new DelegateAction(() => core.Arrangement.AddParameterLane(machineName, key2), () => core.Arrangement.RemoveParameterLane(machineName, key2), "Show automation lane"));
                return new JsonObject { ["lanes"] = new JsonArray(new JsonObject { ["machine"] = mach.Name, ["param"] = param.Name, ["shown"] = true, ["segments"] = new JsonArray(), ["overlaps"] = 0 }) };
            }

            if (op == "clear")
            {
                if (c.Arguments["from"] == null || c.Arguments["to"] == null) throw new ToolError("clear needs from and to.");
                int from = SongModel.ParseTicks(core, c.Arguments["from"], "from") * unit, to = SongModel.ParseTicks(core, c.Arguments["to"], "to") * unit;
                if (to <= from) throw new ToolError("to must be after from.");
                int removed = 0;
                var segs = AutomationReader.Read(core, mach, key2).Segments.Where(s => s.Source == AutoSource.AutomationClip && s.Points.Count > 0 && s.Start < to && s.End > from).ToList();
                foreach (var s in segs)
                {
                    int count = s.Points.Count(p => s.Plays(p.Time) && s.ToSong(p.Time) >= from && s.ToSong(p.Time) < to);
                    if (count == 0) continue;
                    if (s.Start >= from && s.End <= to && !HoldsOthers(s.Pattern, auto.Parameter))
                    {
                        // A clip wholly inside the range goes (its pattern stays), unless it moves other parameters too.
                        var item = editor.Read(s.Sequence).FirstOrDefault(i => i.Time * unit == s.Start && ReferenceEquals(i.Pattern, s.Pattern));
                        if (item != null) { Check(editor.Delete(item)); removed += count; continue; }
                    }
                    Check(automation.Edit(s, key2, pts => pts.Where(p => !(s.ToSong(p.Time) >= from && s.ToSong(p.Time) < to)).ToList()));
                    removed += count;
                }
                if (removed == 0) throw new ToolError($"No automation clip of {param.Name} has points between those positions; arrangement_automation(op=\"list\") shows where it moves.");
                return new JsonObject { ["removed"] = removed };
            }

            if (op != "write") throw new ToolError("op: list, add_lane, write or clear.");

            // ---- write
            var list = c.List("points");
            if (list == null || list.Count == 0) throw new ToolError("write needs points (at least one): [{\"at\": 0, \"value\": \"300 Hz\"}, …].");
            bool allowUnsafe = c.Bool("allow_unsafe") ?? false;
            string unsafeWhy = null;
            int current = param.Parameter.GetValue(track);
            bool linear = (c.String("shape") ?? "linear") == "linear";
            var pts2 = new List<(int Tick, int Raw, bool? Ramp)>();
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i]!.AsObject();
                int tick = SongModel.ParseTicks(core, o["at"], $"points[{i}].at");
                int raw;
                try { raw = ValueResolver.Resolve(param.Info, o["value"], current); }
                catch (ToolError e) { throw new ToolError($"points[{i}].value: {e.Message}"); }
                string refusal = SafeRange.Refusal(param.Info, raw);
                if (refusal != null && !allowUnsafe) throw new ToolError($"points[{i}].value: {refusal}");
                if (refusal != null) unsafeWhy ??= $"outside its safe range, {param.Info.SafeRange.Text(param.Info.Describe)}: {param.Info.SafeRange.Reason}";
                pts2.Add((tick, raw, o["ramp"] is JsonValue rv && rv.TryGetValue(out bool b) ? b : null));
            }
            pts2 = pts2.OrderBy(p => p.Tick).ToList();
            int startTick = c.Arguments["from"] != null ? SongModel.ParseTicks(core, c.Arguments["from"], "from") : pts2[0].Tick;
            int endTick = c.Arguments["to"] != null ? SongModel.ParseTicks(core, c.Arguments["to"], "to")
                : Math.Max(startTick + tpb, (int)Math.Ceiling(pts2[^1].Tick / (double)tpb) * tpb);
            if (endTick <= startTick) throw new ToolError("to must be after from.");
            if (pts2[0].Tick < startTick || pts2[^1].Tick > endTick)
                throw new ToolError($"The points run from beat {NoteList.Fmt(SongModel.Beats(core, pts2[0].Tick))} to {NoteList.Fmt(SongModel.Beats(core, pts2[^1].Tick))}, outside from–to " +
                                    $"({NoteList.Fmt(SongModel.Beats(core, startTick))}–{NoteList.Fmt(SongModel.Beats(core, endTick))}). Widen from and to, or move the points.");

            var existing = AutomationReader.Read(core, mach, key2).Segments
                .FirstOrDefault(s => s.Source == AutoSource.AutomationClip && s.Editable && s.Start <= startTick * unit && s.End >= endTick * unit);
            IPattern pattern;
            AutoSegment seg;
            var problems = new JsonArray();
            if (existing != null)
            {
                seg = existing;
                pattern = existing.Pattern;
            }
            else
            {
                Check(automation.DrawClip(mach, key2, startTick, endTick - startTick, auto.Min, auto.Min, out pattern, withPoints: false));
                seg = AutomationReader.Read(core, mach, key2).Segments.FirstOrDefault(s => ReferenceEquals(s.Pattern, pattern))
                      ?? throw new ToolError("The automation clip was made but couldn't be read back.");
            }
            int last = seg.End - 1;
            var newPoints = pts2.Select(p => new AutoPoint(seg.ToPattern(Math.Min(p.Tick * unit, last)), p.Raw, p.Ramp ?? linear)).ToList();
            int firstT = newPoints[0].Time, lastT = newPoints[^1].Time;
            Check(automation.Edit(seg, key2, old => old.Where(p => p.Time < firstT || p.Time > lastT).Concat(newPoints).ToList()));
            if (unsafeWhy != null) problems.Add($"Unsafe (allow_unsafe): {param.Name} goes {unsafeWhy}.");
            var after = AutomationReader.Read(core, mach, key2).Segments.FirstOrDefault(s => ReferenceEquals(s.Pattern, pattern) && s.Start == seg.Start);
            if (SongModel.Placements(core, pattern).Count == 0) problems.Add("The clip isn't placed (unexpected).");
            return new JsonObject
            {
                ["clip"] = new JsonObject
                {
                    ["pattern"] = pattern.Name,
                    ["lane"] = LanesOf(core, mach).IndexOf(seg.Sequence),
                    ["at"] = SongModel.Beats(core, seg.Start / unit), ["ends"] = SongModel.Beats(core, seg.End / unit),
                    ["points"] = after?.Points.Count ?? newPoints.Count,
                },
                ["problems"] = problems,
            };
        }

        /// <summary>Whether the pattern has automation of a parameter other than <paramref name="parameter"/>.</summary>
        static bool HoldsOthers(IPattern pattern, IParameter parameter) =>
            pattern.Columns.Any(col => col.Type == PatternColumnType.Parameter && col.Parameter != null && !ReferenceEquals(col.Parameter, parameter) && col.GetEvents(int.MinValue, int.MaxValue).Any());

        // ------------------------------------------------------------------ takes and comping

        static void TakesTool(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "arrangement_takes",
                Title = "Record takes and comp",
                Description =
                    "Takes in the arrangement (the Arrange view's ● Rec and comping). op: arm / disarm (a machine's track takes the keyboard for recording), " +
                    "start (record the armed tracks from the play position; loop=true records each pass of the song loop as a take on a take lane), " +
                    "stop (writes what was played as clips and stops the transport), status, comp (puts the range from..to of a take lane's clip onto " +
                    "a main lane, trimmed, no notes copied: take_lane and to_lane are lane numbers from arrangement_get). arm/start/stop need studio mode " +
                    "(someone plays, or midi_play); comp works anywhere. comp is one undo step; recorded takes are on the song's history as one step too. " +
                    "Example: arrangement_takes(op=\"comp\", machine=\"Lead\", take_lane=1, from=0, to=8).",
                Input = Object(null,
                    Req("op", String("What to do", "arm", "disarm", "start", "stop", "status", "comp")),
                    Opt("machine", String("The machine (arm, disarm, comp)")),
                    Opt("loop", Boolean("start: record each loop pass as a take")),
                    Opt("take_lane", Integer("comp: the take lane's number", 0)),
                    Opt("to_lane", Integer("comp: the main lane's number (default its first main lane)", 0)),
                    Opt("from", Position("comp: the range's start")),
                    Opt("to", Position("comp: the range's end (exclusive)"))),
                Output = Object(null,
                    Req("recording", Boolean("Recording now")),
                    Opt("loop", Boolean("Recording each loop pass")),
                    Opt("armed", Array(String("Machine"), "Armed tracks")),
                    Opt("status", String("What happened last")),
                    Opt("lane", Integer("comp: the lane written to"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string op = c.RequiredString("op");
                    if (op != "comp") LiveTools.RequireStudio(host, "arrangement_takes", "write the notes with notes_write and place them with arrangement_place or arrangement_clip.");
                    var r = await host.Engine("arrangement_takes", op != "status", _ =>
                    {
                        var rec = core.Arrangement?.Takes ?? throw new ToolError($"This {Product.Name} has no arrangement recorder.");
                        int? written = null;
                        MachineCore Armable()
                        {
                            var m = SongModel.Machine(core, c.String("machine") ?? throw new ToolError($"{op} needs machine."));
                            if (rec.MainLaneOf(m) == null) throw new ToolError($"{m.Name} has no main lane to record into: arrangement_lane(op=\"add\", machine=\"{m.Name}\") makes one.");
                            return m;
                        }
                        switch (op)
                        {
                            case "arm": rec.SetArmed(Armable(), true); host.ArmedByAgent.Add(c.String("machine")); break;
                            case "disarm": { var m = Armable(); rec.SetArmed(m, false); host.ArmedByAgent.Remove(m.Name); break; }
                            case "start": Check(rec.Start(c.Bool("loop") == true), "arrangement_takes(op=\"arm\", machine=…) arms a track; with loop=true the song needs a loop (song_set)."); break;
                            case "stop": Check(rec.Stop()); break;
                            case "status": break;
                            case "comp":
                            {
                                var m = SongModel.Machine(core, c.String("machine") ?? throw new ToolError("comp needs machine."));
                                var take = Lane(core, m, c.Int("take_lane") ?? throw new ToolError("comp needs take_lane (the take lane's number)."), LaneRole.Take);
                                var main = Lane(core, m, c.Int("to_lane"));
                                if (c.Arguments["from"] == null || c.Arguments["to"] == null) throw new ToolError("comp needs from and to.");
                                int from = SongModel.ParseTicks(core, c.Arguments["from"], "from"), to = SongModel.ParseTicks(core, c.Arguments["to"], "to");
                                var editor = new ClipEditor(core);
                                var why = editor.Comp(take, from, to, main);
                                if (why != null)
                                    throw new ToolError(why + " " + ClipList(core, editor.Read(take)).Replace("Clips there", "The take lane's clips"));
                                written = LanesOf(core, m).IndexOf(main);
                                break;
                            }
                            default: throw new ToolError("op: arm, disarm, start, stop, status or comp.");
                        }
                        var armed = new JsonArray(SongModel.Machines(core).Where(m => rec.IsArmed(m)).Select(m => (JsonNode)m.Name).ToArray());
                        var o = new JsonObject { ["recording"] = rec.IsRecording, ["loop"] = rec.IsLoopRecording, ["armed"] = armed };
                        if (rec.Status != null) o["status"] = rec.Status;
                        if (written != null) o["lane"] = written;
                        return o;
                    }, c.Cancellation);
                    string armedText = r["armed"]!.AsArray().Count > 0 ? " Armed: " + string.Join(", ", r["armed"]!.AsArray().Select(x => x!.GetValue<string>())) + "." : "";
                    return ToolResult.Ok(op == "comp" ? $"Comped onto lane {r["lane"]}." : (r["recording"]!.GetValue<bool>() ? "Recording." : "Not recording.") + armedText + (r["status"] != null ? " " + r["status"] : ""), r);
                },
            });
        }
    }
}
