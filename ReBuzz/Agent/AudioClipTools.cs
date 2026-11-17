// Agent server (docs/specs/neo-audio.md, NA1): audio in the song. audio_import brings a WAV, AIFF, FLAC, OGG or MP3 file into the song's
// audio store and puts it on a Neo Audio track (in a Session slot, in the arrangement, or both); audio_clip_get and audio_clip_set read
// and change an audio clip's warp, tempo, transposition, gain, region and loop; audio_separate splits a clip or a file into stems on new tracks
// (docs/specs/stem-separation.md).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Common.Actions.SequenceActions;
using BuzzGUI.Common.Actions.SongActions;
using BuzzGUI.SequenceEditor.Actions;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Model;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.AudioLibrary.Stems;
using ReBuzz.Core.Session;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class AudioClipTools
    {
        static readonly string[] Warps = SessionDocument.AudioSettings.WarpModes;

        /// <summary>The warp mode's schema: the names, in any case ("pro" is "Pro").</summary>
        static JsonObject WarpSchema()
        {
            var s = String("Warp mode (any case)", Warps);
            s["x-ignoreCase"] = true;
            return s;
        }
        static readonly string[] Preserves = SessionDocument.AudioSettings.PreserveModes;

        static SessionLayout Layout(ReBuzzCore core) =>
            core.SessionLayout ?? throw new ToolError($"This {Product.Name} has no Session View, so no audio clips.");

        static int TrackOf(SessionLayout layout, MachineCore m)
        {
            for (int t = 0; t < layout.TrackCount; t++)
                if (layout.SequenceOf(t)?.Machine == m) return t;
            return -1;
        }

        static JsonObject ClipJson(ReBuzzCore core, SessionLayout layout, MachineCore m, IPattern p)
        {
            var settings = layout.ClipOf(p);
            var a = settings.Audio ?? throw new ToolError($"\"{p.Name}\" of {m.Name} is not an audio clip (audio_import makes one).");
            var o = new JsonObject
            {
                ["machine"] = m.Name,
                ["pattern"] = p.Name,
                ["warp"] = a.Warp,
                ["preserve"] = a.Preserve,
                ["tempo_bpm"] = Math.Round(a.Bpm, 3),
                ["transpose"] = a.Transpose,
                ["detune"] = a.Detune,
                ["gain_db"] = Math.Round(a.GainDb, 3),
                ["fade_in_beats"] = Math.Round(a.FadeIn, 4),
                ["fade_out_beats"] = Math.Round(a.FadeOut, 4),
                ["crossfade_beats"] = Math.Round(a.Tail, 4),
                ["reverse"] = a.Reverse,
                ["loop"] = settings.Loop,
                ["region"] = new JsonObject
                {
                    ["start_beats"] = SongModel.Beats(core, settings.Start),
                    ["loop_start_beats"] = SongModel.Beats(core, settings.LoopStart),
                    ["loop_end_beats"] = SongModel.Beats(core, settings.LoopEnd),
                },
                ["length_beats"] = SongModel.Beats(core, p.Length),
                ["warp_markers"] = new JsonArray(a.Markers.Select(x => (JsonNode)new JsonObject { ["sample"] = x.Sample, ["beat"] = x.Beat }).ToArray()),
            };
            // Pro needs the native neostretch library; without it the clip plays as Complex (neo-audio.md section 11).
            if (a.Warp == "Pro" && !ReBuzz.Core.Clips.NeoStretch.Available)
                o["warp_playing"] = "Complex";
            var info = layout.AudioInfo(p);
            if (info is { } i)
                o["audio"] = new JsonObject
                {
                    ["id"] = a.Asset,
                    ["name"] = i.Wave,
                    ["seconds"] = Math.Round(i.Seconds, 3),
                    ["sample_rate"] = i.Rate,
                    ["channels"] = i.Channels,
                    ["beats"] = Math.Round(i.Beats, 3),
                };
            else
                o["audio"] = JsonValue.Create((string)null);
            return o;
        }

        static void Check(string why)
        {
            if (why != null) throw new ToolError(why);
        }

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "audio_import",
                Title = "Import an audio file",
                Description =
                    "Brings an audio file (WAV, AIFF, FLAC, OGG or MP3) into the song as an audio clip on a Neo Audio track. The audio is decoded once and converted " +
                    "to the song's sample rate, kept in the song (embedded in the .nbl; not a wave of the wavetable), and its tempo is found from the audio " +
                    "(a loop is taken as whole bars; anything else by its transients, else 120). The clip is warped in Beats mode at that tempo. " +
                    "track = a Neo Audio machine (default: a new audio track named after the file); slot = the Session scene to put it in, from 1 (default: the " +
                    "first empty scene of the track unless at_beats is given); at_beats = also (or only) place it in the arrangement at that position. One undo step. " +
                    "Example: audio_import(path=\"/music/loop.wav\", track=\"Drums audio\", slot=1, at_beats=0).",
                Input = Object(null,
                    Req("path", String("The audio file")),
                    Opt("track", String("A Neo Audio machine (a new one when omitted)")),
                    Opt("slot", Integer("Session scene, from 1 (new scenes are added up to it)", 1, 999)),
                    Opt("at_beats", Number("Arrangement position, beats from the start", 0))),
                Output = Object(null,
                    Req("machine", String("The Neo Audio machine")), Req("pattern", String("The clip's pattern")),
                    Opt("slot", Integer("Its Session scene, from 1")), Opt("at_beats", Number("Its arrangement position")),
                    Req("clip", Object(null))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string path = c.RequiredString("path");
                    if (!File.Exists(path)) throw new ToolError($"No file at {path}.");
                    if (!AudioImporter.IsAudioFile(path))
                        throw new ToolError($"{Path.GetFileName(path)} isn't a type {Product.Name} imports: WAV, AIFF, FLAC, OGG or MP3.");
                    var r = await host.Engine("audio_import", true, _ =>
                    {
                        var layout = Layout(core);
                        if (!layout.CanMakeAudioTracks) throw new ToolError("The Neo Audio machine isn't installed, so there are no audio tracks.");
                        var doc = layout.Ensure();
                        var before = doc.CloneLayout();
                        MachineCore machine = null;
                        if (c.String("track") is string tn)
                        {
                            machine = SongModel.Machine(core, tn, "track");
                            if (!SessionLayout.IsClipPlayer(machine)) throw new ToolError($"{machine.Name} isn't a Neo Audio track.");
                            if (!core.SongCore.Sequences.Any(s => s.Machine == machine))
                                AgentActions.Do(core, new AddSequenceAction(machine));
                        }
                        int track = machine == null ? -1 : TrackOf(layout, machine);
                        if (machine != null && track < 0) throw new ToolError($"{machine.Name} has no session track (its lane wasn't picked up).");
                        int? slotArg = c.Int("slot");
                        double? at = c.Number("at_beats");
                        int scene;
                        if (slotArg is int sl) scene = sl - 1;
                        else if (at != null) scene = -1;
                        else
                        {
                            scene = 0;
                            while (track >= 0 && layout.PatternAt(track, scene) != null) scene++;
                        }
                        IPattern clip;
                        if (scene >= 0)
                        {
                            while (layout.SceneCount <= scene) layout.InsertScene(layout.SceneCount);
                            clip = layout.ImportAudio(track, scene, path, out string error) ?? throw new ToolError(error ?? "Couldn't import it.");
                        }
                        else
                        {
                            // Arrangement only: the clip is made in a scratch scene's slot, then the slot is emptied (the pattern stays, on its lane).
                            int last = layout.SceneCount;
                            layout.InsertScene(last);
                            clip = layout.ImportAudio(track, last, path, out string error) ?? throw new ToolError(error ?? "Couldn't import it.");
                            track = TrackOf(layout, (MachineCore)clip.Machine);
                            layout.SetSlot(track, last, null);
                            layout.RemoveScene(last);
                        }
                        var m = (MachineCore)clip.Machine;
                        int placedTrack = TrackOf(layout, m);
                        if (!core.SongCore.Sequences.Any(s => s.Machine == m))
                            AgentActions.Do(core, new AddSequenceAction(m));
                        double? placed = null;
                        if (at is double beats)
                        {
                            int t = SongModel.Ticks(core, beats);
                            var seq = core.SongCore.Sequences.First(s => s.Machine == m);
                            AgentActions.Do(core, new SetEventAction(seq, t, new SequenceEvent(SequenceEventType.PlayPattern, clip)));
                            int end = t + clip.Length;
                            if (core.SongCore.SongEnd < end) AgentActions.Do(core, new SetMarkerAction(core.SongCore, SongMarkers.SongEnd, end));
                            placed = SongModel.Beats(core, t);
                        }
                        var after = layout.Document.CloneLayout();
                        AgentActions.Do(core, new DelegateAction(() => layout.Restore(after), () => layout.Restore(before)));
                        var o = new JsonObject { ["machine"] = m.Name, ["pattern"] = clip.Name, ["clip"] = ClipJson(core, layout, m, clip) };
                        if (scene >= 0 && placedTrack >= 0) o["slot"] = scene + 1;
                        if (placed != null) o["at_beats"] = placed;
                        return o;
                    }, c.Cancellation);
                    var clipJson = (JsonObject)r["clip"]!;
                    return ToolResult.Ok($"Imported {Path.GetFileName(path)} as \"{r["pattern"]}\" on {r["machine"]}" +
                        (r["slot"] != null ? $", scene {r["slot"]}" : "") + (r["at_beats"] != null ? $", arrangement beat {r["at_beats"]}" : "") +
                        $": {clipJson["audio"]?["seconds"]} s, {clipJson["tempo_bpm"]} BPM, {clipJson["length_beats"]} beats, {clipJson["warp"]}.", r);
                },
            });

            static (string Name, System.Text.Json.Nodes.JsonObject Schema, bool Required)[] ClipTarget() => new[] { Req("machine", String("The Neo Audio machine (its track)")), Req("pattern", String("The audio clip's pattern")) };

            tools.Add(new ToolDef
            {
                Name = "audio_clip_get",
                Title = "Read an audio clip",
                Description =
                    "An audio clip's settings: warp mode (Off, Repitch, Beats, Complex, Pro), what Beats preserves, tempo, transposition (semitones) and detune (cents), gain (dB), " +
                    "loop and region (beats), warp markers, its length, and its audio (id, name, seconds, rate, channels, beats). " +
                    "Example: audio_clip_get(machine=\"Drums audio\", pattern=\"loop\").",
                Input = Object(null, ClipTarget()),
                Output = Object(null, Req("machine", String("Machine")), Req("pattern", String("Pattern")), Req("warp", String("Warp mode"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("audio_clip_get", false, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                        return ClipJson(core, Layout(core), m, p);
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]} \"{r["pattern"]}\": {r["warp"]}, {r["tempo_bpm"]} BPM, {r["transpose"]} st {r["detune"]} ct, {r["gain_db"]} dB, " +
                        $"loop {r["loop"]}, {r["length_beats"]} beats.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "audio_clip_set",
                Title = "Set an audio clip",
                Description =
                    "Changes an audio clip, one undo step. warp: Off (its own speed), Repitch (a turntable), Beats (slices on transients; drums, loops), Complex " +
                    "(a phase vocoder; any material), Pro (Signalsmith Stretch: the best quality for any material, at more CPU; it plays as Complex where its native library is missing). preserve (Beats): Transients, 1/4, 1/8, 1/16. tempo_bpm is the audio's tempo (without warp markers the clip's length " +
                    "follows it: 4 beats at 120 BPM are 8 at 60); detect_tempo=true finds it again. transpose -48..48 semitones, detune -50..50 cents, gain_db -70..24. " +
                    "loop on or off; region = {start_beats, loop_start_beats, loop_end_beats} inside the clip (loop_end 0 is its end). warp_markers = [{sample, beat}] " +
                    "(at least 2; [] clears them). Arrangement audio clips (NA2): fade_in_beats and fade_out_beats (the fades in the arrangement; 0 none), reverse " +
                    "(true or false: the audio plays backwards, the same part of it), stretch_to_beats (the clip's length in the arrangement: the audio is warped to fill it; " +
                    "the clip must be placed there, warp not Off, loop off). Setting tempo_bpm, or detect_tempo, on a clip whose warp is Off (a file with no tempo found) turns warp to Beats " +
                    "unless warp is given. Example: audio_clip_set(machine=\"Drums audio\", pattern=\"loop\", warp=\"Complex\", transpose=-2, gain_db=-3).",
                Input = Object(null, ClipTarget().Concat(new[]
                {
                    Opt("warp", WarpSchema()),
                    Opt("preserve", String("What Beats keeps", Preserves)),
                    Opt("tempo_bpm", Number("The audio's tempo", 1, 999)),
                    Opt("detect_tempo", Boolean("Find the tempo again from the audio")),
                    Opt("transpose", Integer("Semitones", -48, 48)),
                    Opt("detune", Integer("Cents", -50, 50)),
                    Opt("gain_db", Number("Gain, dB", -70, 24)),
                    Opt("loop", Boolean("Loop the clip")),
                    Opt("region", Object(null, Opt("start_beats", Number("Start marker", 0)), Opt("loop_start_beats", Number("Loop start", 0)), Opt("loop_end_beats", Number("Loop end; 0 is the clip's end", 0)))),
                    Opt("warp_markers", Array(Object(null, Req("sample", Number("Audio sample")), Req("beat", Number("Clip beat"))), "Markers; empty clears")),
                    Opt("fade_in_beats", Number("Fade in over this many beats from the clip's start in the arrangement; 0 none", 0)),
                    Opt("fade_out_beats", Number("Fade out over this many beats to the clip's end in the arrangement; 0 none", 0)),
                    Opt("reverse", Boolean("Play the audio backwards (the part the clip plays stays the same)")),
                    Opt("stretch_to_beats", Number("The clip's length in the arrangement, beats: the audio is warped to fill it", 0.0001)),
                }).ToArray()),
                Output = Object(null, Req("machine", String("Machine")), Req("pattern", String("Pattern")), Req("warp", String("Warp mode"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var r = await host.Engine("audio_clip_set", true, _ =>
                    {
                        var layout = Layout(core);
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var p = SongModel.Pattern(m, c.RequiredString("pattern"));
                        var settings = layout.ClipOf(p)?.Clone() ?? new SessionDocument.ClipSettings();
                        var a = settings.Audio ?? throw new ToolError($"\"{p.Name}\" of {m.Name} is not an audio clip (audio_import makes one).");
                        var doc = layout.Ensure();
                        var before = doc.CloneLayout();
                        if (c.String("warp") is string warp)
                            a.Warp = Warps.FirstOrDefault(w => string.Equals(w, warp, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolError($"warp: one of {string.Join(", ", Warps)}.");
                        if (c.String("preserve") is string pr)
                            a.Preserve = Preserves.FirstOrDefault(w => string.Equals(w, pr, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolError($"preserve: one of {string.Join(", ", Preserves)}.");
                        if (c.Number("tempo_bpm") is double bpm)
                        {
                            a.Bpm = Math.Clamp(bpm, 1, 999);
                            if (a.Warp == "Off" && c.String("warp") == null) a.Warp = "Beats";       // a tempo is what a file with none was waiting for
                        }
                        if (c.Bool("detect_tempo") == true && a.Warp == "Off" && c.String("warp") == null) a.Warp = "Beats";
                        if (c.Number("fade_in_beats") is double fi) { a.FadeIn = Math.Max(0, fi); if (fi <= 0) a.CrossIn = false; }
                        if (c.Number("fade_out_beats") is double fo) a.FadeOut = Math.Max(0, fo);
                        if (c.Int("transpose") is int tr) a.Transpose = Math.Clamp(tr, -48, 48);
                        if (c.Int("detune") is int dt) a.Detune = Math.Clamp(dt, -50, 50);
                        if (c.Number("gain_db") is double g) a.GainDb = Math.Clamp(g, -70, 24);
                        if (c.Arguments["warp_markers"] is JsonArray ms)
                        {
                            a.Markers = ms.Select(x => (x!["sample"]!.GetValue<double>(), x["beat"]!.GetValue<double>())).Where(x => double.IsFinite(x.Item1) && double.IsFinite(x.Item2)).ToList();
                            if (a.Markers.Count == 1) throw new ToolError("warp_markers needs at least 2 markers (or [] for none).");
                        }
                        if (c.Bool("loop") is bool loop) settings.Loop = loop;
                        if (c.Arguments["region"] is JsonObject region)
                        {
                            if (region["start_beats"] != null) settings.Start = Math.Max(0, SongModel.Ticks(core, region["start_beats"]!.GetValue<double>()));
                            if (region["loop_start_beats"] != null) settings.LoopStart = Math.Max(0, SongModel.Ticks(core, region["loop_start_beats"]!.GetValue<double>()));
                            if (region["loop_end_beats"] != null) settings.LoopEnd = Math.Max(0, SongModel.Ticks(core, region["loop_end_beats"]!.GetValue<double>()));
                        }
                        settings.Audio = a;
                        layout.SetAudio(p, a);
                        layout.SetClipSettings(p, settings);
                        if (c.Bool("detect_tempo") == true) layout.DetectTempo(p);
                        var editor = new ClipEditor(core);
                        if (c.Bool("reverse") is bool reverse && reverse != layout.AudioOf(p).Reverse)
                            Check(editor.ReversePattern(p));
                        if (c.Number("stretch_to_beats") is double stretch)
                        {
                            var use = editor.ClipsOf(p).FirstOrDefault() ?? throw new ToolError($"\"{p.Name}\" isn't placed in the arrangement: stretch_to_beats stretches the clip there (audio_import with at_beats, or arrangement_clip add).");
                            Check(editor.Stretch(use, use.Time + Math.Max(1, SongModel.Ticks(core, stretch))));
                        }
                        var after = layout.Document.CloneLayout();
                        AgentActions.Do(core, new DelegateAction(() => layout.Restore(after), () => layout.Restore(before)));
                        return ClipJson(core, layout, m, p);
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]} \"{r["pattern"]}\": {r["warp"]}, {r["tempo_bpm"]} BPM, {r["transpose"]} st {r["detune"]} ct, {r["gain_db"]} dB, {r["length_beats"]} beats.", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "audio_separate",
                Title = "Separate stems",
                Description =
                    "Splits an audio clip of the song (machine + pattern) or an audio file (path) into stems with a local model, and puts each stem on a new Neo Audio track named after it " +
                    "(\"Bass - <source>\"): one clip per stem, in the source clip's Session scene and at its arrangement positions, with its warp, tempo, gain and region (a file: scene slot " +
                    "from 1, default 1, and/or at_beats). The stems' audio goes into the song's audio store; one undo step. quality: Fast = Demucs htdemucs, Balanced = htdemucs_ft " +
                    "(four times the work), Best = BS-RoFormer (the slowest). A model must be installed (Gear/Models/Stems, or the folder in Preferences; Install models... takes the " +
                    "\"NeoBuzz Live Stem Models\" zip). stems: any of vocals, drums, bass, other, or all (default). Results are cached per audio and model, so a repeat is instant. " +
                    "It takes a while: about a minute per minute of audio on a fast CPU for Fast, several for Best; progress is reported, and cancelling stops it. " +
                    "Example: audio_separate(machine=\"Drums audio\", pattern=\"loop\", quality=\"Fast\", stems=[\"drums\", \"bass\"]).",
                Input = Object(null,
                    Opt("machine", String("The Neo Audio machine of the clip to separate")),
                    Opt("pattern", String("The audio clip's pattern")),
                    Opt("path", String("An audio file to separate instead of a clip (WAV, AIFF, FLAC, OGG, MP3)")),
                    Opt("quality", String("Fast, Balanced or Best (default Fast)", "Fast", "Balanced", "Best")),
                    Opt("stems", Array(String("A stem", "vocals", "drums", "bass", "other", "all"), "Which stems (default all four)")),
                    Opt("slot", Integer("A file: the Session scene for the new clips, from 1 (default 1)", 1, 999)),
                    Opt("at_beats", Number("A file: also place the new clips in the arrangement at this position, in beats", 0))),
                Output = Object(null,
                    Req("model", String("The model that ran")), Req("from_cache", Boolean("The result was cached")), Req("seconds", Number("Seconds it took")),
                    Req("tracks", Array(Object(null, Req("stem", String("The stem")), Req("machine", String("The new Neo Audio machine")), Req("pattern", String("Its clip"))), "The new tracks"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string machineName = c.String("machine"), patternName = c.String("pattern"), path = c.String("path");
                    if ((patternName != null) == (path != null))
                        throw new ToolError("Give a clip (machine and pattern) or a path to a file, one of the two.");
                    if (patternName != null && machineName == null) throw new ToolError("A clip needs its machine as well as its pattern.");
                    string qname = c.String("quality") ?? "Fast";
                    if (!Enum.TryParse<StemQuality>(qname, true, out var quality)) throw new ToolError("quality: Fast, Balanced or Best.");
                    string[] stems;
                    try { stems = StemSeparator.Normalise(c.List("stems")?.Select(x => x?.GetValue<string>())); }
                    catch (ArgumentException e) { throw new ToolError(e.Message); }
                    string missing = StemModels.Missing(quality);
                    if (missing != null) throw new ToolError(missing);
                    var source = await host.Engine("audio_separate", false, _ =>
                    {
                        var layout = Layout(core);
                        if (!layout.CanMakeAudioTracks) throw new ToolError("The Neo Audio machine isn't installed, so there are no audio tracks.");
                        StemSource s;
                        string error;
                        if (path != null)
                            s = layout.StemSourceOfFile(path, out error);
                        else
                        {
                            var m = SongModel.Machine(core, machineName);
                            s = layout.StemSourceOfClip(SongModel.Pattern(m, patternName), out error);
                        }
                        return s ?? throw new ToolError(error);
                    }, c.Cancellation);
                    StemResult result;
                    try
                    {
                        result = await Layout(core).SeparateAsync(source, quality, stems, f => c.Progress(f, 1, "Separating stems"), c.Cancellation);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException or InvalidDataException)
                    {
                        throw new ToolError("Couldn't separate: " + e.Message);
                    }
                    var r = await host.Engine("audio_separate", true, _ =>
                    {
                        var layout = Layout(core);
                        var doc = layout.Ensure();
                        var before = doc.CloneLayout();
                        var ticks = c.Number("at_beats") is double beats && path != null ? new[] { SongModel.Ticks(core, beats) } : null;
                        var outcomes = layout.ApplyStems(source, result, c.Int("slot") is int sl ? sl - 1 : null, ticks, out string error)
                            ?? throw new ToolError(error);
                        var after = layout.Document.CloneLayout();
                        AgentActions.Do(core, new DelegateAction(() => layout.Restore(after), () => layout.Restore(before)));
                        var list = new JsonArray();
                        foreach (var o in outcomes)
                        {
                            var t = new JsonObject
                            {
                                ["stem"] = o.Stem, ["machine"] = o.Machine.Name, ["pattern"] = o.Pattern.Name, ["asset"] = o.Asset.Id,
                                ["length_beats"] = SongModel.Beats(core, o.Pattern.Length),
                            };
                            if (o.Scene >= 0) t["slot"] = o.Scene + 1;
                            if (o.Ticks.Count > 0) t["at_beats"] = new JsonArray(o.Ticks.Select(x => (JsonNode)JsonValue.Create(SongModel.Beats(core, x))).ToArray());
                            list.Add(t);
                        }
                        return list;
                    }, c.Cancellation);
                    var model = StemModels.For(quality)!;
                    var output = new JsonObject
                    {
                        ["model"] = model.Id, ["from_cache"] = result.FromCache, ["seconds"] = Math.Round(result.Seconds, 1), ["tracks"] = r,
                    };
                    return ToolResult.Ok($"Separated \"{source.Name}\" with {model.Title} ({qname}) into {string.Join(", ", r.Select(t => t!["stem"]!.GetValue<string>()))}" +
                        (result.FromCache ? " (from the cache)" : $" in {result.Seconds:0.#} s") + ": " +
                        string.Join(", ", r.Select(t => $"{t!["machine"]}")) + ".", output);
                },
            });
        }
    }
}
