// Agent server (clips.md §8, CU4): clip_get, one read of a clip: its lanes, its notes and its view. A clip is a pattern, in a Session slot or
// placed in the arrangement (addressed as clip_envelope does). The notes come from SongNotes (every lane of the note route), the lanes are
// the clip's columns in the store whoever wrote them, and the view is what the tracker keeps with the lanes (rows per beat, mutes).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class ClipTools
    {
        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "clip_get",
                Title = "Read a clip",
                Description =
                    "Everything a MIDI clip holds: its lanes (the Notes lane and one lane per parameter and track, each with its event count, who made it: " +
                    "pattern = the pattern editor's lane, automation = a take's or automation_write's, and its row grid and mute), its notes (as notes_read gives them) and its view " +
                    "(rows per beat, muted lanes, where the pattern is placed). Say which clip: pattern=<its name>, or scene=<Session scene, from 1>, or lane with clip=<id> or at=<a position inside it>. " +
                    "automation_read gives the points of the parameter lanes. Example: clip_get(machine=\"Bass\", pattern=\"Verse\").",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Opt("pattern", String("The clip's pattern")),
                    Opt("scene", Integer("A Session slot: the scene, from 1 (the clip in this machine's track)", 1, 999)),
                    Opt("lane", Integer("An arrangement clip: the machine's lane number (arrangement_get; default its first main lane)", 0)),
                    Opt("clip", Integer("An arrangement clip: its id")),
                    Opt("at", AnyOf("An arrangement clip: a position inside it", Number("Beats (0 = the start)", 0), String("\"bar.beat\"")))),
                Output = Object(null,
                    Req("machine", String("Machine")), Req("pattern", String("The clip's pattern")), Req("length_beats", Number("The clip's length")),
                    Opt("input", String("midi or tracks: where the machine takes its notes; absent when it takes none")),
                    Req("notes", Array(Object(null, Req("pitch", Integer("MIDI")), Req("start", Number("Beats")), Req("length", Number("Beats")), Req("velocity", Integer("1–127"))), "The notes")),
                    Req("lanes", Array(Object(null,
                        Req("kind", String("notes or parameter", "notes", "parameter")), Opt("param", String("Parameter")), Opt("machine", String("Another machine's parameter")),
                        Opt("track", Integer("Track")), Req("events", Integer("Events in the lane")),
                        Req("source", String("pattern, automation or notes", "pattern", "automation", "notes")),
                        Opt("grid", String("The lane's rows (rows per beat, and the beats whose rows differ)")), Opt("muted", Boolean("Muted in the tracker"))), "The lanes")),
                    Req("view", Object(null,
                        Req("rows_per_beat", Integer("Rows per beat the tracker shows (the lanes' grid, else the song's ticks per beat)")),
                        Req("muted_lanes", Integer("Lanes muted")),
                        Req("placements", Integer("Times the pattern is placed in the arrangement"))))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("clip_get", false, _ => Get(core, c), c.Cancellation);
                    int lanes = r["lanes"]!.AsArray().Count, notes = r["notes"]!.AsArray().Count;
                    return ToolResult.Ok($"{r["machine"]} \"{r["pattern"]}\" ({r["length_beats"]} beats): {notes} note(s), {lanes} lane(s).", r);
                },
            });
        }

        static JsonObject Get(ReBuzzCore core, ToolContext c)
        {
            var m = SongModel.Machine(core, c.RequiredString("machine"));
            var p = ClipEnvelopeTools.ClipOf(core, m, c);
            int tpb = SongModel.Tpb(core);
            var result = new JsonObject { ["machine"] = m.Name, ["pattern"] = p.Name, ["length_beats"] = SongModel.Beats(core, p.Length) };
            var notes = new JsonArray();
            if (!SongModel.IsMaster(m) && ReBuzz.Core.Notes.SongNotes.For(core, p) != null)
            {
                var input = MachineNotes.For(m);
                result["input"] = input.Mode;
                foreach (var n in MachineNotes.Read(p, input)) notes.Add(NoteList.ToJson(n, tpb));
            }
            result["notes"] = notes;

            var lanes = new JsonArray();
            int muted = 0, rpb = 0;
            foreach (var col in p.Columns)
            {
                var o = new JsonObject();
                bool marked = col.Metadata.ContainsKey(ClipColumnCore.SessionAutomationKey);
                if (col.Type == ClipColumnType.MIDI && (col.Parameter == null || col.Parameter.IndexInGroup < 0))
                {
                    o["kind"] = "notes";
                    o["source"] = "notes";
                }
                else if (col.Type == ClipColumnType.Parameter && col.Parameter != null)
                {
                    o["kind"] = "parameter";
                    o["param"] = col.Parameter.Name;
                    if (col.Machine != null && !ReferenceEquals(col.Machine, p.Machine)) o["machine"] = col.Machine.Name;
                    if (col.Parameter.Group.Type == ParameterGroupType.Track) o["track"] = col.Track;
                    o["source"] = marked ? "automation" : "pattern";
                }
                else continue;
                int count = col.GetEvents(int.MinValue, int.MaxValue).Count();
                o["events"] = count;
                if (col.Metadata.TryGetValue(ClipMeta.Grid, out var grid))
                {
                    o["grid"] = grid;
                    if (rpb == 0 && ClipGrid.TryParse(grid, out int g, out _)) rpb = g;
                }
                if (col.Metadata.TryGetValue(ClipMeta.Mute, out var mute) && mute == "1") { o["muted"] = true; muted++; }
                if (count == 0 && !o.ContainsKey("grid") && !o.ContainsKey("muted")) continue;       // an empty lane with no view of its own is not worth a line
                lanes.Add(o);
            }
            result["lanes"] = lanes;
            result["view"] = new JsonObject
            {
                ["rows_per_beat"] = rpb > 0 ? rpb : tpb,
                ["muted_lanes"] = muted,
                ["placements"] = SongModel.Placements(core, p).Count,
            };
            return result;
        }
    }
}
