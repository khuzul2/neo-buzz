// Agent server (docs/specs/neo-audio.md, NA1): audio in the song. audio_import brings a WAV, AIFF, FLAC, OGG or MP3 file into the song's
// audio store and puts it on a Neo Audio track (in a Session slot, in the arrangement, or both); audio_clip_get and audio_clip_set read
// and change an audio clip's warp, tempo, transposition, gain, region and loop; audio_separate splits a clip or a file into stems on new tracks
// (docs/specs/stem-separation.md); audio_convert_to_midi turns a clip's melody, harmony or drums into a MIDI clip on a new track (docs/specs/pitch.md section 9);
// audio_slice_to_midi cuts a clip into up to 16 slices played from a Neo Sampler on a new MIDI track (docs/specs/audio-to-midi.md, SL1).

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
using ReBuzz.Core.AudioLibrary.AudioToMidi;
using ReBuzz.Core.AudioLibrary.Stems;
using ReBuzz.Core.Clips;
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
                ["auto_warp_grid"] = new JsonObject
                {
                    ["grid"] = a.AutoGrid,
                    ["sensitivity"] = a.AutoSensitivity,
                    ["reach_percent"] = a.AutoReach,
                    ["strength_percent"] = layout.QuantizeStrength,
                },
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
                    // NA1b: whether the audio is read from the decoded cache file (long audio is) and how well that is going.
                    ["storage"] = Storage(layout.ViewOf(p)?.Asset),
                    ["streams"] = layout.ViewOf(p)?.Asset is { Storage: AssetStorage.Streaming or AssetStorage.Repairing },
                    ["cache_progress"] = Math.Round(layout.ViewOf(p)?.Asset?.CacheProgress ?? 0, 3),
                    ["underruns"] = layout.ViewOf(p)?.Asset?.StreamStats.Underruns ?? 0,
                    ["missed_samples"] = layout.ViewOf(p)?.Asset?.StreamStats.Missed ?? 0,
                    ["resident_mb"] = Math.Round((layout.ViewOf(p)?.Asset?.ResidentBytes ?? 0) / 1048576.0, 2),
                };
            else
                o["audio"] = JsonValue.Create((string)null);
            return o;
        }

        static string Storage(ReBuzz.Core.AudioLibrary.AudioAsset asset) => asset == null ? "none" : asset.Storage.ToString().ToLowerInvariant();

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
                    "(a loop is taken as whole bars; anything else by its transients, else 120). The clip is warped at that tempo in Pro mode (the default for a new clip; a file with no tempo found is left unwarped, Off). " +
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
                    "loop and region (beats), warp markers, its length, and its audio (id, name, seconds, rate, channels, beats; storage: memory, filling, streaming, repairing or failed, streams, cache_progress, " +
                    "underruns and missed_samples of a streamed asset, resident_mb: long audio is read from a decoded cache file, and an underrun is a block that played silence for audio the disk had not delivered in time). " +
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
                    "(a phase vocoder; any material), Pro (the default for a new clip; Signalsmith Stretch: the best quality for any material, at more CPU; it plays as Complex where its native library is missing). preserve (Beats): Transients, 1/4, 1/8, 1/16. tempo_bpm is the audio's tempo (without warp markers the clip's length " +
                    "follows it: 4 beats at 120 BPM are 8 at 60); detect_tempo=true finds the one steady tempo again from the audio (the markers are cleared). " +
                    "auto_warp=true finds the beats of the audio, follows a tempo that drifts or changes, and replaces the warp markers and the tempo (one undo step): a steady tempo gives one " +
                    "tempo and the first downbeat as bar 1 (a loop of whole bars is left as it is), a moving tempo gives a marker on every bar and on the beats that drift within a bar; " +
                    "mode=\"grid\" (with auto_warp; default \"beat\") is auto-warp to grid: after the beats, a warp marker on every transient, each pulled to the nearest line of grid (1/4, 1/8, 1/16 the default, 1/32, 1/4T, 1/8T, 1/16T) by strength_percent (0-100, default 100: the song's Quantize strength), a transient further than reach_percent (0-50, default 40) of a grid step from every line is left alone, and in quiet or sustained passages there are none; sensitivity 0-100 (50) is how readily transients are found; grid, sensitivity and reach_percent are saved with the clip, and given without auto_warp only set them; the answer's to_grid says how many markers were made and how many were out of reach or in quiet or sustained parts. " +
                    "from_seconds (with auto_warp) starts at the first downbeat at or after that second of the audio and keeps the markers before it; nothing changes, and the call says so, when no beats are found. transpose -48..48 semitones, detune -50..50 cents, gain_db -70..24. " +
                    "loop on or off; region = {start_beats, loop_start_beats, loop_end_beats} inside the clip (loop_end 0 is its end). warp_markers = [{sample, beat}] " +
                    "(at least 2; [] clears them). Arrangement audio clips (NA2): fade_in_beats and fade_out_beats (the fades in the arrangement; 0 none), reverse " +
                    "(true or false: the audio plays backwards, the same part of it), stretch_to_beats (the clip's length in the arrangement: the audio is warped to fill it; " +
                    "the clip must be placed there, warp not Off, loop off). Setting tempo_bpm, or detect_tempo, on a clip whose warp is Off (a file with no tempo found) turns warp to Pro (the default) " +
                    "unless warp is given. Example: audio_clip_set(machine=\"Drums audio\", pattern=\"loop\", warp=\"Complex\", transpose=-2, gain_db=-3).",
                Input = Object(null, ClipTarget().Concat(new[]
                {
                    Opt("warp", WarpSchema()),
                    Opt("preserve", String("What Beats keeps", Preserves)),
                    Opt("tempo_bpm", Number("The audio's tempo", 1, 999)),
                    Opt("detect_tempo", Boolean("Find the tempo again from the audio (one steady tempo; the markers are cleared)")),
                    Opt("auto_warp", Boolean("Find the beats, follow the tempo where it moves, and replace the warp markers and the tempo (one undo step)")),
                    Opt("mode", String("With auto_warp: beat (follow the beats, the default) or grid (then every transient pulled to the grid)", new[] { "beat", "grid" })),
                    Opt("grid", String("Auto-warp to grid: the grid transients are pulled to (default 1/16; saved with the clip)", SessionDocument.AudioSettings.AutoGrids.Select(g => g.Name).ToArray())),
                    Opt("sensitivity", Integer("Auto-warp to grid: transient sensitivity (default 50; saved with the clip)", 0, 100)),
                    Opt("reach_percent", Integer("Auto-warp to grid: a transient further than this share of a grid step from every line is left alone (default 40; saved with the clip)", 0, 50)),
                    Opt("strength_percent", Integer("Auto-warp to grid: how far transients are pulled to the grid (default: the song's Quantize strength, 100; kept with the song)", 0, 100)),
                    Opt("from_seconds", Number("With auto_warp: start at the first downbeat at or after this second of the audio; the markers before it stay", 0)),
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
                        if (layout.ClipOf(p)?.Audio == null) throw new ToolError($"\"{p.Name}\" of {m.Name} is not an audio clip (audio_import makes one).");
                        var doc = layout.Ensure();
                        var before = doc.CloneLayout();
                        // The change is the one the studio's Audio page makes (SessionLayout.EditAudio): both reach the model by the same calls.
                        var edit = new AudioClipEdit
                        {
                            Warp = c.String("warp"),
                            Preserve = c.String("preserve"),
                            TempoBpm = c.Number("tempo_bpm"),
                            DetectTempo = c.Bool("detect_tempo") == true,
                            AutoWarp = c.Bool("auto_warp") == true,
                            ToGrid = c.Bool("auto_warp") == true && string.Equals(c.String("mode"), "grid", StringComparison.OrdinalIgnoreCase),
                            AutoGrid = c.String("grid"),
                            AutoSensitivity = c.Int("sensitivity"),
                            AutoReach = c.Int("reach_percent"),
                            FromSeconds = c.Number("from_seconds"),
                            FadeIn = c.Number("fade_in_beats"),
                            FadeOut = c.Number("fade_out_beats"),
                            Transpose = c.Int("transpose"),
                            Detune = c.Int("detune"),
                            GainDb = c.Number("gain_db"),
                            Loop = c.Bool("loop"),
                            Reverse = c.Bool("reverse"),
                        };
                        if (c.Arguments["warp_markers"] is JsonArray ms)
                            edit.Markers = ms.Select(x => (x!["sample"]!.GetValue<double>(), x["beat"]!.GetValue<double>())).ToList();
                        if (c.Arguments["region"] is JsonObject region)
                        {
                            if (region["start_beats"] != null) edit.Start = Math.Max(0, SongModel.Ticks(core, region["start_beats"]!.GetValue<double>()));
                            if (region["loop_start_beats"] != null) edit.LoopStart = Math.Max(0, SongModel.Ticks(core, region["loop_start_beats"]!.GetValue<double>()));
                            if (region["loop_end_beats"] != null) edit.LoopEnd = Math.Max(0, SongModel.Ticks(core, region["loop_end_beats"]!.GetValue<double>()));
                        }
                        var editor = new ClipEditor(core);
                        if (c.String("mode") is string mode && !string.Equals(mode, "beat", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "grid", StringComparison.OrdinalIgnoreCase))
                            throw new ToolError("mode: beat or grid.");
                        if (c.String("mode") != null && c.Bool("auto_warp") != true) throw new ToolError("mode goes with auto_warp=true.");
                        if (c.Int("strength_percent") is int strengthPercent) layout.SetQuantizeStrength(Math.Clamp(strengthPercent, 0, 100));
                        Check(layout.EditAudio(p, edit, editor));
                        if (c.Number("stretch_to_beats") is double stretch)
                        {
                            var use = editor.ClipsOf(p).FirstOrDefault() ?? throw new ToolError($"\"{p.Name}\" isn't placed in the arrangement: stretch_to_beats stretches the clip there (audio_import with at_beats, or arrangement_clip add).");
                            Check(editor.Stretch(use, use.Time + Math.Max(1, SongModel.Ticks(core, stretch))));
                        }
                        var after = layout.Document.CloneLayout();
                        AgentActions.Do(core, new DelegateAction(() => layout.Restore(after), () => layout.Restore(before)));
                        var result = ClipJson(core, layout, m, p);
                        if (edit.ToGrid && layout.LastGridReport is { } g)
                            result["to_grid"] = new JsonObject { ["markers_made"] = g.Made, ["out_of_reach"] = g.OutOfReach, ["quiet_or_sustained"] = g.Gated, ["skipped"] = g.Skipped, ["beats_found"] = g.BeatsFound };
                        return result;
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]} \"{r["pattern"]}\": {r["warp"]}, {r["tempo_bpm"]} BPM, {r["transpose"]} st {r["detune"]} ct, {r["gain_db"]} dB, {r["length_beats"]} beats." +
                        (r["to_grid"] is JsonObject tg ? $" To grid: {tg["markers_made"]} markers on transients, {tg["out_of_reach"]} out of reach, {tg["quiet_or_sustained"]} in quiet or sustained parts." : ""), r);
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

            tools.Add(new ToolDef
            {
                Name = "audio_convert_to_midi",
                Title = "Convert audio to MIDI",
                Description =
                    "Finds the notes in an audio clip of the song (machine + pattern) and puts them on a new MIDI track: a clip of the same length, in the source clip's Session scene " +
                    "and at its arrangement positions, with an instrument on the track (Neo Poly for melody and harmony, Neo Drums for drums); one undo step. It runs on this computer and " +
                    "takes seconds (cancelling stops it). mode: melody = one note at a time (a voice, a lead, a bass line; tracked as pitch, with range any/voice/guitar/bass, min_note_ms, " +
                    "smooth_ms and pitch_bends: a slide or vibrato stays one note and its movement becomes pitch-bend events, the instrument's bend range being two semitones); " +
                    "harmony = several notes at once (chords, piano, guitar; the Basic Pitch model, with onset_threshold and frame_threshold, lower finds more); drums = kick, snare, closed " +
                    "and open hat, toms and cymbals as General MIDI notes (36 kick, 38 snare, 42 closed hat, 46 open hat, 43/47/50 toms, 49 crash; drum_threshold, lower finds more hits). " +
                    "quantize snaps the note starts to a grid (off, 1/4, 1/8, 1/16, 1/32) by strength 0 to 1. The audio is read as it is in the store: the clip's transpose is added to the pitches, " +
                    "its detune rounded, its warp markers place the notes in beats, a reversed clip puts them backwards; its gain and fades don't matter. Accuracy: melody finds most notes of " +
                    "a clean single line (see docs/specs/pitch.md section 9 for the measured numbers); drums are good at kick, snare, closed hat, toms and cymbals on an electronic kit and weaker at open hats, and less sure on an acoustic kit; " +
                    "harmony is Basic Pitch's own accuracy. apply=false only reports the notes (up to 200, in beats) and changes nothing. " +
                    "Example: audio_convert_to_midi(machine=\"Bass audio\", pattern=\"line\", mode=\"melody\", quantize=\"1/16\", strength=0.8).",
                Input = Object(null,
                    Req("machine", String("The Neo Audio machine of the clip")),
                    Req("pattern", String("The audio clip's pattern")),
                    Req("mode", String("What to find", "melody", "harmony", "drums")),
                    Opt("range", String("Melody: what the tracker listens for (default any)", "any", "voice", "guitar", "bass")),
                    Opt("min_note_ms", Number("Melody: shortest note in milliseconds (default 60)", 10, 2000)),
                    Opt("smooth_ms", Number("Melody: the median filter over the pitch curve, milliseconds (default 30; 0 for none)", 0, 200)),
                    Opt("pitch_bends", Boolean("Melody and harmony: keep slides and vibrato as pitch-bend events on one note (default false)")),
                    Opt("onset_threshold", Number("Harmony: how sure the model must be of a note's start, 0 to 1 (default 0.5)", 0.05, 0.95)),
                    Opt("frame_threshold", Number("Harmony: how sure it must be the note is sounding, 0 to 1 (default 0.3)", 0.05, 0.95)),
                    Opt("drum_threshold", Number("Drums: how sure the classifier must be of a hit (default 4, as fitted; lower finds more hits, higher fewer)", 1, 20)),
                    Opt("quantize", String("Snap note starts to a grid (default off)", "off", "1/4", "1/8", "1/16", "1/32")),
                    Opt("strength", Number("Quantize strength, 0 to 1 (default 1)", 0, 1)),
                    Opt("apply", Boolean("false: only report the notes (default true)"))),
                Output = Object(null,
                    Req("mode", String("The mode that ran")), Req("notes", Integer("How many notes (hits) were found")), Req("seconds", Number("Seconds it took")),
                    Req("summary", String("What was found, in a line")),
                    Opt("machine", String("The new MIDI machine")), Opt("pattern", String("Its clip")), Opt("instrument", String("The instrument on the track")),
                    Opt("slot", Integer("The Session scene of the new clip, from 1")), Opt("at_beats", Array(Number("A position"), "Positions of the clip in the arrangement, in beats")),
                    Opt("pitch_bends", Integer("Pitch-bend events written")),
                    Opt("found", Array(Object(null, Req("start_beats", Number("Where it starts")), Req("length_beats", Number("How long")), Req("note", Integer("MIDI note")), Req("velocity", Integer("1 to 127"))), "With apply=false: the notes"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string machineName = c.String("machine"), patternName = c.String("pattern");
                    if (!Enum.TryParse<ConvertMode>(c.String("mode") ?? "", true, out var mode)) throw new ToolError("mode: melody, harmony or drums.");
                    var options = new ConvertOptions { Mode = mode };
                    string range = (c.String("range") ?? "any").ToLowerInvariant();
                    options.Range = range == "any" ? "full" : range;
                    if (range is not ("any" or "voice" or "guitar" or "bass")) throw new ToolError("range: any, voice, guitar or bass.");
                    if (c.Number("min_note_ms") is double mn) options.MinNoteMs = mn;
                    if (c.Number("smooth_ms") is double sm) options.SmoothMs = sm;
                    if (c.Bool("pitch_bends") is bool pb) options.PitchBends = pb;
                    if (c.Number("onset_threshold") is double ot) options.OnsetThreshold = ot;
                    if (c.Number("frame_threshold") is double ft) options.FrameThreshold = ft;
                    if (c.Number("drum_threshold") is double dt) options.DrumThreshold = dt;
                    var apply = new ConvertApply();
                    string quantize = (c.String("quantize") ?? "off").ToLowerInvariant();
                    apply.QuantizeDivision = quantize switch { "off" => 0, "1/4" => 1, "1/8" => 2, "1/16" => 4, "1/32" => 8, _ => throw new ToolError("quantize: off, 1/4, 1/8, 1/16 or 1/32.") };
                    if (c.Number("strength") is double st) apply.QuantizeStrength = Math.Clamp(st, 0, 1);
                    bool write = c.Bool("apply") ?? true;

                    var source = await host.Engine("audio_convert_to_midi", false, _ =>
                    {
                        var layout = Layout(core);
                        var m = SongModel.Machine(core, machineName);
                        var s = layout.ConvertSourceOfClip(SongModel.Pattern(m, patternName), out string error);
                        if (s == null) throw new ToolError(error);
                        if (write && layout.ConvertBlocker(mode) is string blocker) throw new ToolError(blocker);
                        return s;
                    }, c.Cancellation);
                    ConvertResult result;
                    try
                    {
                        result = await Layout(core).ConvertAsync(source, options, f => c.Progress(f, 1, "Converting to MIDI"), c.Cancellation);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException or InvalidDataException or System.IO.FileNotFoundException)
                    {
                        throw new ToolError("Couldn't convert: " + e.Message);
                    }
                    var output = new JsonObject
                    {
                        ["mode"] = mode.ToString().ToLowerInvariant(), ["notes"] = result.Notes.Count, ["seconds"] = Math.Round(result.WorkSeconds, 2), ["summary"] = result.Summary,
                    };
                    if (!write)
                    {
                        var found = await host.Engine("audio_convert_to_midi", false, _ =>
                        {
                            var layout = Layout(core);
                            double bpm = Math.Max(1, layout.AudioOf(source.Clip)?.Bpm ?? 120);
                            var list = new JsonArray();
                            foreach (var n in result.Notes.Take(200))
                                list.Add(new JsonObject
                                {
                                    ["start_beats"] = Math.Round(n.Start * bpm / 60, 3), ["length_beats"] = Math.Round(Math.Max(0, n.End - n.Start) * bpm / 60, 3),
                                    ["note"] = n.Pitch, ["velocity"] = n.Velocity,
                                });
                            return list;
                        }, c.Cancellation);
                        output["found"] = found;
                        return ToolResult.Ok(result.Summary + " (nothing was changed; the times are in beats of the clip's own tempo).", output);
                    }
                    var r = await host.Engine("audio_convert_to_midi", true, _ =>
                    {
                        var layout = Layout(core);
                        var doc = layout.Ensure();
                        var before = doc.CloneLayout();
                        var outcome = layout.ApplyConversion(source, result, apply, out string error) ?? throw new ToolError(error);
                        var after = layout.Document.CloneLayout();
                        AgentActions.Do(core, new DelegateAction(() => layout.Restore(after), () => layout.Restore(before)));
                        return outcome;
                    }, c.Cancellation);
                    output["machine"] = r.Machine.Name;
                    output["pattern"] = r.Pattern.Name;
                    output["instrument"] = r.Instrument;
                    output["pitch_bends"] = r.Bends;
                    output["notes"] = r.Notes;
                    if (r.Scene >= 0) output["slot"] = r.Scene + 1;
                    if (r.Ticks.Count > 0) output["at_beats"] = new JsonArray(r.Ticks.Select(x => (JsonNode)JsonValue.Create(SongModel.Beats(core, x))).ToArray());
                    return ToolResult.Ok($"{result.Summary} {r.Instrument} plays {r.Notes} of them on the new track \"{r.Machine.Name}\" (clip \"{r.Pattern.Name}\").", output);
                },
            });
            tools.Add(new ToolDef
            {
                Name = "audio_slice_to_midi",
                Title = "Slice audio to a MIDI track",
                Description =
                    "Cuts an audio clip of the song (machine + pattern, or machine + slot: its Session scene, from 1) into at most 16 slices and plays them from a new MIDI track: each slice " +
                    "becomes a wave of the wavetable (the first free slots, named \"<clip> 01\"…, cut from the audio as the clip plays it: its gain, reversed if it is, not warped, with a 1 ms fade in " +
                    "unless it starts on a zero crossing and a 2 ms fade out), the track holds a Neo Sampler in Kit mode (zone n plays slice n on key 36 + n - 1, all in choke group 1 so " +
                    "it is monophonic like the loop; the clip's transpose and detune retune the zones), and a MIDI clip of one note per slice (velocity 100, as long as the slice as the clip is warped) sits " +
                    "where the source is (its Session scene, arrangement positions, region and loop). The source clip stays as it is. One undo step. by: transients (default: the 16 strongest " +
                    "attacks, the weaker merged into the slice before them), warp_markers, bar, 1/4, 1/8 or 1/16 (a beat is a quarter; refused when it would make more than 16 slices). " +
                    "Only the part the clip plays is cut: its loop, else its region. apply=false only reports the slices and changes nothing. " +
                    "Example: audio_slice_to_midi(machine=\"Drums audio\", pattern=\"loop\", by=\"transients\").",
                Input = Object(null,
                    Req("machine", String("The Neo Audio machine of the clip (its track)")),
                    Opt("pattern", String("The audio clip's pattern")),
                    Opt("slot", Integer("Or the clip in this Session scene of the track, from 1", 1, 999)),
                    Opt("by", String("Where to cut (default transients)", "transients", "warp_markers", "bar", "1/4", "1/8", "1/16")),
                    Opt("apply", Boolean("false: only report the slices (default true)"))),
                Output = Object(null,
                    Req("by", String("What cut it")), Req("count", Integer("How many slices")), Req("found", Integer("How many the choice found before the limit of 16")),
                    Req("capped", Boolean("Weaker transients were merged into the slice before them")), Req("summary", String("What was cut, in a line")),
                    Req("slices", Array(Object(null, Req("slot", Integer("The wavetable slot, from 1")), Req("start", Integer("Samples into the audio as the clip plays it")), Req("length", Integer("Samples")),
                        Req("key", Integer("MIDI note that plays it")), Req("beat", Number("Where its note is, beats of the clip"))), "The slices")),
                    Opt("machine", String("The new MIDI machine (Neo Sampler)")), Opt("pattern", String("Its clip")),
                    Opt("slot", Integer("The Session scene of the new clip, from 1")), Opt("at_beats", Array(Number("A position"), "Positions of the clip in the arrangement, in beats"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string machineName = c.RequiredString("machine"), patternName = c.String("pattern");
                    int? sceneArg = c.Int("slot");
                    if ((patternName != null) == (sceneArg != null)) throw new ToolError("Give the clip as pattern or as slot (its Session scene, from 1), one of the two.");
                    if (!SliceMath.TryParse(c.String("by") ?? "transients", out var by)) throw new ToolError("by: transients, warp_markers, bar, 1/4, 1/8 or 1/16.");
                    bool write = c.Bool("apply") ?? true;
                    var source = await host.Engine("audio_slice_to_midi", false, _ =>
                    {
                        var layout = Layout(core);
                        var m = SongModel.Machine(core, machineName);
                        IPattern clip;
                        if (patternName != null) clip = SongModel.Pattern(m, patternName);
                        else
                        {
                            int track = TrackOf(layout, m);
                            if (track < 0) throw new ToolError($"{m.Name} has no session track.");
                            clip = layout.PatternAt(track, sceneArg!.Value - 1) ?? throw new ToolError($"{m.Name} has no clip in scene {sceneArg}.");
                        }
                        var s = layout.SliceSourceOfClip(clip, out string error);
                        if (s == null) throw new ToolError(error);
                        return s;
                    }, c.Cancellation);
                    var layout0 = Layout(core);
                    SlicePlan plan;
                    try { plan = await layout0.PlanSlicesAsync(source, by, c.Cancellation); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.IO.InvalidDataException or System.IO.IOException)
                    {
                        throw new ToolError("Couldn't slice: " + e.Message);
                    }
                    if (!plan.Ok) throw new ToolError(plan.Why ?? "There is nothing to slice.");
                    string Line(int count) => $"Cut \"{source.Name}\" ({source.Region}) into {count} slices by {SliceMath.Label(by).ToLowerInvariant()}" +
                        (plan.Capped ? $" (the {plan.Found} transients were merged to the {count} strongest)" : "") + ".";
                    JsonObject Common(IEnumerable<SliceWave> list)
                    {
                        var arr = new JsonArray();
                        foreach (var s in list)
                            arr.Add(new JsonObject { ["slot"] = s.Slot, ["name"] = s.Name, ["start"] = s.Start, ["length"] = s.Length, ["key"] = s.Key, ["beat"] = Math.Round(s.Beat, 4) });
                        return new JsonObject
                        {
                            ["by"] = SliceMath.Key(by), ["count"] = plan.Slices.Count, ["found"] = plan.Found, ["capped"] = plan.Capped, ["slices"] = arr,
                        };
                    }
                    if (!write)
                    {
                        var preview = await host.Engine("audio_slice_to_midi", false, _ =>
                        {
                            var layout = Layout(core);
                            string blocker = layout.SliceBlocker(plan.Slices.Count);
                            var o = Common(plan.Slices.Select((s, i) => new SliceWave { Slot = 0, Name = $"{source.Name} {i + 1:00}", Start = s.Start, Length = s.Length, Key = SessionLayout.SliceKeyBase + i, Beat = s.Beat, Beats = s.Beats }));
                            o["summary"] = Line(plan.Slices.Count) + " (nothing was changed)" + (blocker != null ? " " + blocker : "");
                            return o;
                        }, c.Cancellation);
                        return ToolResult.Ok((string)preview["summary"]!, preview);
                    }
                    var r = await host.Engine("audio_slice_to_midi", true, _ =>
                    {
                        var layout = Layout(core);
                        var doc = layout.Ensure();
                        var before = doc.CloneLayout();
                        var outcome = layout.ApplySlices(source, plan, out string error) ?? throw new ToolError(error);
                        var after = layout.Document.CloneLayout();
                        AgentActions.Do(core, new DelegateAction(() => layout.Restore(after), () => layout.Restore(before)));
                        return outcome;
                    }, c.Cancellation);
                    var output = Common(r.Slices);
                    output["machine"] = r.Machine.Name;
                    output["pattern"] = r.Pattern.Name;
                    if (r.Scene >= 0) output["slot"] = r.Scene + 1;
                    if (r.Ticks.Count > 0) output["at_beats"] = new JsonArray(r.Ticks.Select(x => (JsonNode)JsonValue.Create(SongModel.Beats(core, x))).ToArray());
                    string summary = $"{Line(r.Slices.Count)} {r.Instrument} plays them on the new track \"{r.Machine.Name}\" (clip \"{r.Pattern.Name}\"; waves {r.Slices[0].Slot}–{r.Slices[^1].Slot}, keys {r.Slices[0].Key}–{r.Slices[^1].Key}).";
                    output["summary"] = summary;
                    return ToolResult.Ok(summary, output);
                },
            });
        }
    }
}
