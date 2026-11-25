// Agent server (docs/specs/neo-audio.md, WM1): audio_warp_markers, the Audio page's warp marker editing for the agent. Each action is one the page makes with its
// mouse, keys or context menu (add a marker, move markers with their spacing kept, move one along the audio, a segment's tempo, set bar 1 here, warp from here
// straight, warp as a loop, markers at the transients or on every bar, delete, remove all, quantize), through the same call (SessionLayout.WarpEdit): the same
// validation, one undo step. Selecting markers or a time range is the page's own (an agent names the markers it means).

using System;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Model;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class AudioWarpTools
    {
        static JsonObject State(ReBuzzCore core, SessionLayout layout, MachineCore m, IClip p, WarpResult result, string action)
        {
            var markers = result?.Markers ?? layout.WarpMarkersOf(p);
            var source = layout.AudioSourceOf(p);
            var audio = layout.AudioOf(p);
            double rate = source?.SampleRate ?? 44100;
            var list = new JsonArray();
            for (int i = 0; i < markers.Count; i++)
            {
                var o = new JsonObject { ["index"] = i, ["sample"] = markers[i].Sample, ["beat"] = Math.Round(markers[i].Beat, 6) };
                double tempo = WarpMath.SegmentTempo(markers, i, rate);
                if (!double.IsNaN(tempo)) o["segment_bpm"] = Math.Round(tempo, 3);
                if (i == 0 || i == markers.Count - 1) o["pinned"] = true;
                list.Add(o);
            }
            var r = new JsonObject
            {
                ["machine"] = m.Name,
                ["clip"] = p.Name,
                ["action"] = action,
                ["straight"] = (audio?.Markers?.Count ?? 0) < 2,
                ["markers"] = list,
                ["length_beats"] = SongModel.Beats(core, p.Length),
                ["quantize_strength"] = layout.QuantizeStrength,
            };
            if (result != null && action != WarpRequest.List)
            {
                r["count"] = result.Count;
                r["touched"] = new JsonArray(result.Touched.Where(i => i >= 0).Select(i => (JsonNode)JsonValue.Create(i)).ToArray());
            }
            return r;
        }

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "audio_warp_markers",
                Title = "Edit the warp markers of an audio clip",
                Description =
                    "Edits the warp markers of an audio clip, as the Audio page's waveform does, one undo step each. A marker pins a sample of the audio to a beat of the clip; the audio " +
                    "between two markers is stretched to fit. The first and last markers are the audio's pinned ends: they can't be deleted or moved along the audio, and no marker passes " +
                    "its neighbour (it stops one tick, a 64th of a beat, before it). A clip with no markers is straight at its tempo (the list shows its two ends). Markers are numbered " +
                    "from 0 in beat order (what action=list shows). Positions are a sample of the audio (sample) or a beat of the clip as the page shows it (beat). " +
                    "action: list (the markers, each with its segment's tempo, and the song's quantize strength; changes nothing) · add (a marker at sample or beat, where the audio is: nothing moves) · " +
                    "move (markers = [numbers], delta_beats: all move together, the spacing kept, the audio before and after stretches; for one marker, beat = where to) · " +
                    "move_along (one marker, sample = the audio it now sits on, or delta_samples: the marker keeps its beat) · segment_tempo (marker = where the segment starts, bpm: the next marker, " +
                    "and all after it, move so that only this segment is stretched), halve_segment, double_segment (marker) · set_bar_one (sample or beat: this moment becomes beat 0, the first downbeat; " +
                    "not for a reversed clip) · warp_straight (sample or beat, bpm optional: one tempo from there on, found from the audio when bpm is not given; the markers after it are removed) · warp_follow (sample or beat: the beats after it are found and followed through tempo changes; the markers before stay; the same as audio_clip_set auto_warp with from_seconds) · " +
                    "warp_loop (from and to as samples or beats, bars optional: the range, or the clip's region, becomes a whole number of bars, the nearest of 1, 2, 4, 8, 16) · " +
                    "insert_at_transients (from and to optional: a marker at each transient) · insert_every_bar (from and to optional) · delete (markers) · remove_all (back to straight at the clip's tempo) · " +
                    "quantize (markers, or from and to: markers inside, or made at the transients inside, move toward the nearest grid line by strength percent, default the song's, 100 at first; " +
                    "grid_beats, default 0.25) · quantize_strength (strength: the song's value, 0 to 100). Selecting is the page's own. " +
                    "Example: audio_warp_markers(machine=\"Drums audio\", clip=\"loop\", action=\"insert_at_transients\"), then action=\"quantize\" with markers=[1,2,3] and grid_beats=0.5.",
                Input = Object(null,
                    Req("machine", String("The Neo Audio machine (its track)")),
                    Req("clip", String("The audio clip's clip")),
                    Req("action", String("What to do", WarpRequest.Actions)),
                    Opt("markers", Array(Integer("A marker's number", 0, 100000), "The markers it is about, by number in beat order")),
                    Opt("marker", Integer("One marker, by number (segment actions, move_along)", 0, 100000)),
                    Opt("sample", Number("A position in the audio, in samples", 0)),
                    Opt("beat", Number("A position in the clip, in beats as the page shows them")),
                    Opt("from_sample", Number("Start of a range, samples", 0)), Opt("to_sample", Number("End of a range, samples", 0)),
                    Opt("from_beat", Number("Start of a range, beats of the clip")), Opt("to_beat", Number("End of a range, beats of the clip")),
                    Opt("delta_beats", Number("move: how far, in the audio's own beats (positive is later)")),
                    Opt("delta_samples", Number("move_along: how far along the audio")),
                    Opt("bpm", Number("segment_tempo: the segment's tempo; warp_straight: the tempo from there on", 1, 999)),
                    Opt("bars", Integer("warp_loop: 1, 2, 4, 8 or 16 bars (default: the nearest)", 1, 16)),
                    Opt("strength", Number("quantize: percent, 0 to 100 (default the song's); quantize_strength: the value to keep", 0, 100)),
                    Opt("grid_beats", Number("quantize: the grid in beats (default 0.25)", 0.015625, 64))),
                Output = Object(null,
                    Req("machine", String("Machine")), Req("clip", String("Clip")), Req("action", String("The action done")),
                    Req("markers", Array(Object(null), "The markers now: index, sample, beat, segment_bpm, pinned")),
                    Req("straight", Boolean("Whether the clip has no markers of its own")),
                    Opt("count", Integer("How many markers the action made, moved or removed")),
                    Opt("touched", Array(Integer("A marker's number"), "The markers it made or moved")),
                    Opt("length_beats", Number("The clip's length")), Opt("quantize_strength", Integer("The song's quantize strength, percent"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string action = c.RequiredString("action");
                    bool read = action == WarpRequest.List;
                    var r = await host.Engine("audio_warp_markers", !read, _ =>
                    {
                        var layout = core.SessionLayout ?? throw new ToolError($"This {Product.Name} has no Session View, so no audio clips.");
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var p = SongModel.Clip(m, c.RequiredString("clip"));
                        if (layout.ClipOf(p)?.Audio == null) throw new ToolError($"\"{p.Name}\" of {m.Name} is not an audio clip (audio_import makes one).");
                        if (!WarpRequest.Actions.Contains(action)) throw new ToolError($"action: one of {string.Join(", ", WarpRequest.Actions)}.");
                        var request = new WarpRequest
                        {
                            Action = action,
                            Markers = (c.Arguments["markers"] as JsonArray)?.Select(x => x!.GetValue<int>()).ToArray()
                                      ?? (c.Int("marker") is int one ? new[] { one } : null),
                            Sample = c.Number("sample"),
                            Beat = c.Number("beat"),
                            FromSample = c.Number("from_sample"), ToSample = c.Number("to_sample"),
                            FromBeat = c.Number("from_beat"), ToBeat = c.Number("to_beat"),
                            DeltaBeats = c.Number("delta_beats"),
                            DeltaSamples = c.Number("delta_samples"),
                            Bpm = c.Number("bpm"),
                            Bars = c.Int("bars"),
                            Strength = c.Number("strength"),
                            GridBeats = c.Number("grid_beats"),
                        };
                        if (read) { layout.WarpEdit(p, request, null, out var listed); return State(core, layout, m, p, listed, action); }
                        var doc = layout.Ensure();
                        var before = doc.CloneLayout();
                        // The change is the one the studio's Audio page makes (SessionLayout.WarpEdit): both reach the model by the same calls.
                        string why = layout.WarpEdit(p, request, new ClipEditor(core), out var result);
                        if (why != null) throw new ToolError(why);
                        var after = layout.Document.CloneLayout();
                        AgentActions.Do(core, new DelegateAction(() => layout.Restore(after), () => layout.Restore(before)));
                        var o = State(core, layout, m, p, request.Action == WarpRequest.RemoveAll ? null : result, action);
                        if (request.Action == WarpRequest.RemoveAll && result != null) o["count"] = result.Count;
                        return o;
                    }, c.Cancellation);
                    return ToolResult.Ok($"{r["machine"]} \"{r["pattern"]}\": {action}, {((JsonArray)r["markers"]!).Count} markers" + (r["straight"]!.GetValue<bool>() ? " (straight)" : "") + ".", r);
                },
            });
        }
    }
}
