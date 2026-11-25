// Agent server (docs/specs/drag-drop.md, DD1): drop_item does what dropping a browser item or a file on the Arrangement, the Session or the
// Machine View does, through the same router (ReBuzz.Core.Drop.DropRouter), as one undo step. The agent's other tools do each part of it
// (machine_add, machine_swap, audio_import, notes_write, arrangement_place, preset_load); drop_item is the one call with the drop's rules,
// and the only one that reads a MIDI file or inserts an effect after a track's last machine.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Browser;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Core.Drop;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class DropTools
    {
        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "drop_item",
                Title = "Drop an item on a view",
                Description =
                    "Does what dragging a browser item or a file onto the Arrangement, the Session or the Machine View does, as one undo step. kind: machine or plugin (id = " +
                    "the gear's name, browser_search shows it), preset (id = the gear, preset = its name), audio (path or paths; or id \"wave:N\", a wave of the song), " +
                    "midi (path to a .mid file), clip (id \"Machine/Clip\"). target.track = the machine of the track dropped on, or leave it out for empty space. " +
                    "The rules: a generator or instrument plug-in on empty space is a new machine and track wired to Master (in the arrangement with an empty clip at at_beats), " +
                    "on a track it replaces that track's generator (connections kept); an effect needs a track and goes between the track's last machine and what it fed " +
                    "(a track that branches needs target.branch: the machine to follow); audio on empty space is a new Neo Audio track, on an audio track clips from at_beats " +
                    "(one after another) or the slot (down the scenes), on Neo Sampler it loads the samples, on any other track a new audio track; a clip on its own " +
                    "machine's track is a clip; a MIDI file on empty space is a new track with the notes (target.instrument, default Neo Poly), on an instrument's track the notes " +
                    "in a new clip at at_beats or in slot (the file's tempo and meter are only reported); a preset on empty space is a new track with that machine and the " +
                    "preset, on a track of that machine it loads. plan=true says what it would do and changes nothing. " +
                    "Example: drop_item(view=\"arrangement\", kind=\"audio\", path=\"/music/loop.wav\", target={at_beats: 16}).",
                Input = Object(null,
                    Req("view", String("Where it is dropped", "arrangement", "session", "machine")),
                    Req("kind", String("What is dropped", "machine", "plugin", "preset", "audio", "midi", "clip")),
                    Opt("id", String("Gear name (machine, plugin, preset), \"Machine/Clip\" (clip) or \"wave:N\" (audio)")),
                    Opt("path", String("An audio or MIDI file")),
                    Opt("paths", Array(String("An audio file"), "Several audio files: one after another (arrangement) or down the scenes (session)")),
                    Opt("preset", String("A preset's name (kind preset)")),
                    Opt("target", Object("Where; leave out for empty space",
                        Opt("track", String("The machine of the track dropped on")),
                        Opt("at_beats", Number("Arrangement: the time, in beats", 0)),
                        Opt("slot", Integer("Session: the scene, from 1", 1, 999)),
                        Opt("x", Number("Machine view: the position of a new machine")),
                        Opt("y", Number("Machine view: the position of a new machine")),
                        Opt("branch", Array(String("A machine"), "An effect on a track that branches: the machines to follow, in order")),
                        Opt("instrument", String("A MIDI file on empty space: the gear for the notes")))),
                    Opt("plan", Boolean("Only say what would happen"))),
                Output = Object(null,
                    Req("action", String("What was (or would be) done")),
                    Req("message", String("In words")),
                    Opt("machine", String("The machine made, changed or loaded")),
                    Opt("clips", Array(String("A clip"), "The clips made or placed"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    var r = await host.Engine("drop_item", c.Bool("plan") != true, _ =>
                    {
                        var view = c.RequiredString("view") switch
                        {
                            "arrangement" => DropView.Arrangement,
                            "session" => DropView.Session,
                            _ => DropView.Machine,
                        };
                        if (!BrowserDragData.TryKind(c.RequiredString("kind"), out var kind)) throw new ToolError("kind: machine, plugin, preset, audio, midi or clip.");
                        var ids = new List<string>();
                        if (c.List("paths") is { } many) ids.AddRange(many.Select(n => n?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x))!);
                        if (c.String("path") is { Length: > 0 } one) ids.Insert(0, one);
                        if (c.String("id") is { Length: > 0 } id) ids.Insert(0, id);
                        if (ids.Count == 0) throw new ToolError("Give an id, a path or paths.");
                        if (kind != BrowserKind.Audio && ids.Count > 1) throw new ToolError("Only audio takes several files.");
                        var items = ids.Select(i => new BrowserDragData(kind, i, c.String("preset"))).ToList();
                        var to = c.Object("target");
                        IMachine machine = null;
                        if (to?["track"] is JsonValue tv && tv.TryGetValue(out string tn) && !string.IsNullOrWhiteSpace(tn)) machine = SongModel.Machine(core, tn, "track");
                        ISequence lane = machine == null ? null : core.SongCore.Sequences.FirstOrDefault(s => s.Machine == machine);
                        double? at = Num(to, "at_beats");
                        int? slot = Num(to, "slot") is double sl ? (int)Math.Round(sl) : null;
                        if (view == DropView.Session && machine != null && !SessionRows.HasTrack(core, machine) && kind != BrowserKind.Machine && kind != BrowserKind.Plugin)
                            throw new ToolError($"{machine.Name} has no Session track.");
                        var target = new DropTarget
                        {
                            View = view,
                            Machine = machine,
                            Lane = lane,
                            Time = at is double b ? SongModel.Ticks(core, b) : -1,
                            Scene = slot is int s ? s - 1 : -1,
                            X = (float)(Num(to, "x") ?? 0),
                            Y = (float)(Num(to, "y") ?? 0),
                            Branch = (to?["branch"] as JsonArray)?.Select(n => n?.GetValue<string>()).Where(x => x != null).ToList()!,
                            DefaultInstrument = to?["instrument"]?.GetValue<string>(),
                        };
                        var router = new DropRouter(core);
                        if (c.Bool("plan") == true)
                        {
                            var plan = router.Plan(items, target);
                            var po = new JsonObject { ["action"] = plan.Action.ToString(), ["message"] = plan.Allowed ? plan.Describe : plan.Choices != null ? plan.ChoiceTitle : plan.Refusal };
                            if (plan.Target != null) po["machine"] = plan.Target.Name;
                            if (plan.Choices != null) po["choices"] = new JsonArray(plan.Choices.Select(x => (JsonNode)x).ToArray());
                            return po;
                        }
                        host.AutoSnapshot("before dropping " + ids[0]);
                        var result = router.Drop(items, target);
                        if (!result.Ok)
                        {
                            string more = result.Choices != null ? " Pick one in target.branch: " + string.Join(", ", result.Choices) + "." : "";
                            throw new ToolError(result.Message + more);
                        }
                        var o = new JsonObject { ["action"] = result.Action.ToString(), ["message"] = result.Message };
                        if (result.Machine != null) o["machine"] = result.Machine.Name;
                        o["clips"] = new JsonArray(result.Clips.Select(p => (JsonNode)p.Name).ToArray());
                        return o;
                    }, c.Cancellation);
                    return ToolResult.Ok((string)r["message"]!, r);
                },
            });
        }

        static double? Num(JsonObject o, string name) => o?[name] is JsonValue v && Schema.TryNumber(v, out double d) ? d : null;
    }

    /// <summary>The Session's tracks, for the agent's checks.</summary>
    internal static class SessionRows
    {
        public static bool HasTrack(ReBuzzCore core, IMachine machine) => core.SessionLayout is { } layout && layout.FirstTrackOf(machine) >= 0;
    }
}
