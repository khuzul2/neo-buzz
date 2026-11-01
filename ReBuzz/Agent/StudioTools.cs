// Agent server (docs/specs/mcp-server.md §5.1–5.2): orientation, undo, and the song itself.
// studio_status, undo, redo, song_new, song_open, song_save, song_set.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class StudioTools
    {
        static readonly string[] SongExtensions = { ".bmxml", ".bmx", ".bmw" };

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "studio_status",
                Title = "Studio status",
                Description =
                    "The state of the studio: the song's file and unsaved changes, tempo, ticks per beat, length and loop in beats, " +
                    "sample rate, mode (studio with a window, or headless), transport, how many machines/patterns/waves, and what undo would revert. " +
                    "Call it first in every session and whenever you're unsure what the song looks like. Example: studio_status().",
                Input = Object(null),
                Output = StatusSchema(),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var s = await host.Engine("studio_status", false, _ => Status(host), c.Cancellation);
                    string file = s["song"]!["file"]?.GetValue<string>() ?? "untitled (not saved yet)";
                    return ToolResult.Ok(
                        $"{file}{(s["song"]!["unsaved_changes"]!.GetValue<bool>() ? ", unsaved changes" : "")}; " +
                        $"{s["tempo_bpm"]} BPM, {s["ticks_per_beat"]} ticks per beat, {s["length_beats"]} beats long; " +
                        $"{s["counts"]!["machines"]} machines, {s["counts"]!["patterns"]} patterns; {host.Mode} mode.", s);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "undo",
                Title = "Undo",
                Description =
                    "Reverts the last change to the song: one agent call is one step (a person's edit in the window is one step too). " +
                    "Use it to back out of a change that measured worse. Returns what is now on top of the undo stack. Example: undo().",
                Input = Object(null),
                Annotations = ToolAnnotations.Write,
                Handler = async c => await host.Engine("undo", false, _ =>
                {
                    var stack = core.SongCore.ActionStack;
                    if (!stack.CanUndo) throw new ToolError("Nothing to undo: the song has no changes on its undo stack (opening or creating a song clears it).");
                    stack.Undo();
                    if (host.FailedCallOnRedo) host.UndosSinceFailedCall++;
                    core.SetModifiedFlag();
                    return ToolResult.Ok("Undone. " + (stack.CanUndo ? "More steps can be undone." : "Nothing more to undo."),
                        new JsonObject { ["can_undo"] = stack.CanUndo, ["can_redo"] = stack.CanRedo });
                }, c.Cancellation),
            });

            tools.Add(new ToolDef
            {
                Name = "redo",
                Title = "Redo",
                Description = "Re-applies the last undone step. Example: redo().",
                Input = Object(null),
                Annotations = ToolAnnotations.Write,
                Handler = async c => await host.Engine("redo", false, _ =>
                {
                    var stack = core.SongCore.ActionStack;
                    if (!stack.CanRedo || (host.FailedCallOnRedo && host.UndosSinceFailedCall == 0))
                        throw new ToolError("Nothing to redo: nothing was undone since the last change.");
                    stack.Redo();
                    if (host.FailedCallOnRedo) host.UndosSinceFailedCall--;
                    core.SetModifiedFlag();
                    return ToolResult.Ok("Redone.", new JsonObject { ["can_undo"] = stack.CanUndo, ["can_redo"] = stack.CanRedo });
                }, c.Cancellation),
            });

            tools.Add(new ToolDef
            {
                Name = "song_new",
                Title = "New song",
                Description =
                    "Starts an empty song (just the Master), optionally with a tempo. Refuses when the current song has unsaved changes " +
                    "unless discard_unsaved is true: save first with song_save if they matter. Not undoable. Example: song_new(tempo_bpm=92).",
                Input = Object(null,
                    Opt("tempo_bpm", Number("Tempo in beats per minute (whole numbers; Buzz has no fractional BPM)", 16, 500)),
                    Opt("discard_unsaved", Boolean("Throw away unsaved changes in the current song"))),
                Output = StatusSchema(),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    var s = await host.Engine("song_new", false, messages =>
                    {
                        RefuseUnsaved(core, c.Bool("discard_unsaved") == true, "start a new song");
                        core.Modified = false;
                        core.ExecuteCommand(BuzzGUI.Interfaces.BuzzCommand.NewFile);
                        if (c.Number("tempo_bpm") is double bpm)
                        {
                            core.BPM = (int)Math.Round(bpm);
                            // The tempo marks the song modified from a deferred update; a fresh song isn't
                            // "unsaved work", so clear the flag after it (the dispatcher runs in order).
                            core.Modified = false;
                            host.Dispatcher.BeginInvoke(() => core.Modified = false);
                        }
                        return Status(host);
                    }, c.Cancellation);
                    return ToolResult.Ok($"New empty song at {s["tempo_bpm"]} BPM.", s);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "song_open",
                Title = "Open song",
                Description =
                    "Opens a song file (.bmxml, .bmx or .bmw) by its full path. Refuses when the current song has unsaved changes unless " +
                    "discard_unsaved is true. Returns the new studio status. Not undoable. Example: song_open(path=\"C:/Music/demo.bmxml\").",
                Input = Object(null,
                    Req("path", String("Full path of the song file")),
                    Opt("discard_unsaved", Boolean("Throw away unsaved changes in the current song"))),
                Output = StatusSchema(),
                Annotations = ToolAnnotations.Delete,
                Handler = async c =>
                {
                    string path = SongPath(c.RequiredString("path"));
                    if (!File.Exists(path)) throw new ToolError($"No file at {path}. Give the full path of an existing .bmxml, .bmx or .bmw song.");
                    var s = await host.Engine("song_open", false, messages =>
                    {
                        RefuseUnsaved(core, c.Bool("discard_unsaved") == true, "open another song");
                        core.Modified = false;
                        if (!core.OpenSongForAgent(path))
                            throw new ToolError($"Couldn't open {path}." + (messages.Count > 0 ? " ReBuzz said: " + string.Join(" | ", messages) : " The file may be damaged or from a newer ReBuzz.") +
                                                " The studio now has an empty song.");
                        return Status(host);
                    }, c.Cancellation);
                    var warnings = s["warnings"] as JsonArray;
                    return ToolResult.Ok($"Opened {path}: {s["counts"]!["machines"]} machines, {s["tempo_bpm"]} BPM." +
                                         (warnings?.Count > 0 ? $" {warnings.Count} warning(s) while loading." : ""), s);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "song_save",
                Title = "Save song",
                Description =
                    "Saves the song: to its own file, or to path (save as; .bmxml recommended, it keeps everything readable). " +
                    "Won't overwrite a different existing file unless overwrite is true. Example: song_save(path=\"C:/Music/sketch.bmxml\").",
                Input = Object(null,
                    Opt("path", String("Full path to save to (save as). Omit to save to the song's own file")),
                    Opt("overwrite", Boolean("Replace an existing file that isn't this song's own"))),
                Output = Object(null, Req("file", String("The file written")), Req("bytes", Integer("Its size"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    string requested = c.String("path");
                    var r = await host.Engine("song_save", false, messages =>
                    {
                        string current = core.SongCore.SongName;
                        string path = requested != null ? SongPath(requested) : current
                            ?? throw new ToolError("This song has no file yet: give a path, e.g. song_save(path=\"C:/Music/song.bmxml\").");
                        if (requested != null && File.Exists(path) && !string.Equals(Path.GetFullPath(path), current == null ? null : Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)
                            && c.Bool("overwrite") != true)
                            throw new ToolError($"{path} exists and isn't this song's file. Pass overwrite=true to replace it, or choose another name.");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        if (requested != null)
                        {
                            core.SongCore.SongName = path;
                        }
                        core.SaveSongFile(path);
                        if (messages.Count > 0) throw new ToolError($"Saving {path} failed. ReBuzz said: " + string.Join(" | ", messages));
                        return new JsonObject { ["file"] = path, ["bytes"] = new FileInfo(path).Length };
                    }, c.Cancellation);
                    return ToolResult.Ok($"Saved {r["file"]} ({r["bytes"]} bytes).", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "song_set",
                Title = "Set song properties",
                Description =
                    "Changes the tempo, ticks per beat (pattern resolution), song end and loop. Positions are in beats from the start " +
                    "(rounded to the nearest tick). Change ticks_per_beat before writing patterns: patterns keep their length in ticks, so it moves " +
                    "everything in beats. One undo step. Returns the values as they now are. Example: song_set(tempo_bpm=124, loop_start_beats=0, loop_end_beats=64).",
                Input = Object(null,
                    Opt("tempo_bpm", Number("Tempo in beats per minute (rounded to a whole number)", 16, 500)),
                    Opt("ticks_per_beat", Integer("Pattern rows per beat (4 = 16th notes)", 1, 32)),
                    Opt("song_end_beats", Number("Where the song ends, in beats", 0)),
                    Opt("loop_start_beats", Number("Loop start, in beats", 0)),
                    Opt("loop_end_beats", Number("Loop end, in beats", 0)),
                    Opt("looping", Boolean("Whether playback loops"))),
                Output = StatusSchema(),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var s = await host.Engine("song_set", true, _ =>
                    {
                        var song = core.SongCore;
                        double? ls = c.Number("loop_start_beats"), le = c.Number("loop_end_beats");
                        if (ls.HasValue && le.HasValue && le <= ls) throw new ToolError($"loop_end_beats ({le}) must be after loop_start_beats ({ls}).");
                        int tpbNow = c.Int("ticks_per_beat") ?? core.TPB;
                        int bpmNow = c.Number("tempo_bpm") is double bpm ? (int)Math.Round(bpm) : core.BPM;
                        bool loopingNow = c.Bool("looping") ?? core.Looping;
                        if (tpbNow != core.TPB || bpmNow != core.BPM || loopingNow != core.Looping)
                            AgentActions.SetTransport(core, bpmNow, tpbNow, loopingNow);
                        int Ticks(double beats) => (int)Math.Round(beats * tpbNow);
                        void Marker(BuzzGUI.Interfaces.SongMarkers marker, int ticks) =>
                            AgentActions.Do(core, new BuzzGUI.Common.Actions.SongActions.SetMarkerAction(song, marker, ticks));
                        if (c.Number("song_end_beats") is double end) Marker(BuzzGUI.Interfaces.SongMarkers.SongEnd, Math.Max(1, Ticks(end)));
                        if (le is double loopEnd)
                        {
                            int t = Math.Max(1, Ticks(loopEnd));
                            // The loop end pushes the song end out with it; as its own step, so undo brings it back.
                            if (song.SongEnd < t) Marker(BuzzGUI.Interfaces.SongMarkers.SongEnd, t);
                            Marker(BuzzGUI.Interfaces.SongMarkers.LoopEnd, t);
                        }
                        if (ls is double loopStart) Marker(BuzzGUI.Interfaces.SongMarkers.LoopStart, Math.Min(Ticks(loopStart), song.LoopEnd - 1));
                        core.SetModifiedFlag();
                        return Status(host);
                    }, c.Cancellation);
                    return ToolResult.Ok($"{s["tempo_bpm"]} BPM, {s["ticks_per_beat"]} ticks per beat, song end {s["length_beats"]} beats, " +
                                         $"loop {s["loop"]!["start_beats"]}–{s["loop"]!["end_beats"]} beats{(s["loop"]!["on"]!.GetValue<bool>() ? " (on)" : "")}.", s);
                },
            });
        }

        static void RefuseUnsaved(ReBuzzCore core, bool discard, string what)
        {
            if (core.Modified && !discard)
                throw new ToolError($"The song has unsaved changes. Save them with song_save first, or {what} with discard_unsaved=true.");
        }

        static string SongPath(string path)
        {
            path = Path.GetFullPath(path.Trim().Trim('"'));
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!SongExtensions.Contains(ext))
                throw new ToolError($"\"{path}\" isn't a song file: use .bmxml (recommended), .bmx or .bmw.");
            return path;
        }

        static double Beats(int ticks, int tpb) => Math.Round(ticks / (double)Math.Max(1, tpb), 4);

        static JsonObject StatusSchema() => Object("The studio's state",
            Req("song", Object(null, Opt("file", String("The song's file; absent when never saved")), Req("unsaved_changes", Boolean("Changes since the last save")))),
            Req("tempo_bpm", Number("Beats per minute")),
            Req("ticks_per_beat", Integer("Pattern rows per beat")),
            Req("beats_per_bar", Integer("Beats per bar, for \"bar.beat\" positions")),
            Req("length_beats", Number("Song end, in beats")),
            Req("loop", Object(null, Req("start_beats", Number("Loop start")), Req("end_beats", Number("Loop end")), Req("on", Boolean("Looping")))),
            Req("sample_rate", Integer("Samples per second")),
            Req("mode", String("studio (window open) or headless", "studio", "headless")),
            Req("transport", Object(null, Req("playing", Boolean("Playing")), Req("recording", Boolean("Recording")), Req("position_beats", Number("Play position")))),
            Req("counts", Object(null, Req("machines", Integer("Machines (with Master)")), Req("patterns", Integer("Patterns")), Req("waves", Integer("Waves in the wavetable")))),
            Req("undo", Object(null, Req("can_undo", Boolean("Something to undo")), Req("can_redo", Boolean("Something to redo")), Opt("next_undo", String("What undo would revert, when it's an agent call")))),
            Req("agent", Object(null, Req("version", String("Server version")), Req("log", String("The agent log file")))),
            Opt("warnings", Array(String("A message"), "Messages ReBuzz raised during the call")));

        /// <summary>The studio's state (engine thread).</summary>
        internal static JsonObject Status(AgentHost host)
        {
            var core = host.Core;
            var song = core.SongCore;
            int tpb = core.TPB;
            var stack = song.ActionStack;
            bool canRedo = stack.CanRedo && !(host.FailedCallOnRedo && host.UndosSinceFailedCall == 0);
            var undo = new JsonObject { ["can_undo"] = stack.CanUndo, ["can_redo"] = canRedo };
            if (stack.CanUndo && host.LastAgentUndo != null && stack.Actions.Count() == host.ActionsAfterLastAgentCall)
                undo["next_undo"] = host.LastAgentUndo;
            var songObject = new JsonObject { ["unsaved_changes"] = core.Modified };
            if (!string.IsNullOrEmpty(song.SongName)) songObject["file"] = song.SongName;
            return new JsonObject
            {
                ["song"] = songObject,
                ["tempo_bpm"] = core.BPM,
                ["ticks_per_beat"] = tpb,
                ["beats_per_bar"] = SongModel.BeatsPerBar(core),
                ["length_beats"] = Beats(song.SongEnd, tpb),
                ["loop"] = new JsonObject { ["start_beats"] = Beats(song.LoopStart, tpb), ["end_beats"] = Beats(song.LoopEnd, tpb), ["on"] = core.Looping },
                ["sample_rate"] = ReBuzzCore.masterInfo.SamplesPerSec,
                ["mode"] = host.Mode,
                ["transport"] = new JsonObject { ["playing"] = core.Playing, ["recording"] = core.Recording, ["position_beats"] = Beats(song.PlayPosition, tpb) },
                ["counts"] = new JsonObject
                {
                    ["machines"] = SongModel.Machines(core).Count(),
                    ["patterns"] = SongModel.Machines(core).Sum(m => m.Patterns.Count),
                    ["waves"] = song.Wavetable.Waves.Count(w => w != null),
                },
                ["undo"] = undo,
                ["agent"] = new JsonObject { ["version"] = AgentHost.Version, ["log"] = host.Log.FilePath },
            };
        }
    }
}
