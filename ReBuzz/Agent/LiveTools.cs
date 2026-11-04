// Agent server, live (docs/specs/mcp-server.md §5.2, §5.5, §5.8; A7): playing with the user, in
// studio mode. The transport, launching the Session View's scenes and clips in time, recording
// takes of what the user (or the agent) plays, phrases played into a machine, live meters of the
// master and chosen machines, and the readings of analysis machines.
//
// Headless ReBuzz has no audio output, so the playing tools answer there with the way to hear the
// song instead (render). These calls change what plays, not the song, so they are not undo steps;
// the takes recorded go on the Session View's own undo history (or pattern_delete removes one).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using static ReBuzz.Agent.Mcp.Schema;
using SongTime = ReBuzz.Agent.Model.SongTime;
using TapTime = BuzzGUI.Interfaces.SongTime;

namespace ReBuzz.Agent
{
    internal static class LiveTools
    {
        const int TimeBase = 240;     // session time is 1/240 tick

        static JsonObject Level(string description, string unit) => AnyOf(description, Number(unit), String("\"-inf\" for silence, \"inf\""));

        static JsonObject Position(string description) =>
            AnyOf(description, Number("Beats (0 = the start)", 0), String("\"bar.beat\": \"5.1\" is bar 5's first beat"));

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            Transport(tools, host);
            Launch(tools, host);
            Record(tools, host);
            MidiPlay(tools, host);
            Meters(tools, host);
            Readings(tools, host);
        }

        /// <summary>Playing tools need someone listening: studio or live mode (an audio device plays).</summary>
        static void RequireStudio(AgentHost host, string tool, string instead)
        {
            if (!host.Live)
                throw new ToolError($"{tool} plays live for the user to hear, which needs studio mode (ReBuzz open, Settings → Agent → Allow agents) " +
                                    $"or rebuzz-headless --audio. This is headless ReBuzz, with no audio output: {instead}");
        }

        // ------------------------------------------------------------------ what plays now

        /// <summary>The transport, and per session track what plays, waits to, is armed or records.</summary>
        internal static JsonObject LiveState(ReBuzzCore core)
        {
            var song = core.SongCore;
            int tpb = SongModel.Tpb(core), bpb = SongModel.BeatsPerBar(core);
            // A seek while playing lands on the next tick: report where it goes.
            int position = song.PlayPositionSetNextTick >= 0 ? song.PlayPositionSetNextTick : song.PlayPosition;
            double beats = SongModel.Beats(core, position);
            var o = new JsonObject
            {
                ["playing"] = core.PlayRequested,
                ["position_beats"] = Math.Round(beats, 4),
                ["position"] = SongTime.BarBeat(beats, bpb),
                ["tempo_bpm"] = core.BPM,
                ["looping"] = core.Looping,
                ["loop_start_beats"] = SongModel.Beats(core, song.LoopStart),
                ["loop_end_beats"] = SongModel.Beats(core, song.LoopEnd),
            };
            var layout = core.SessionLayout;
            var session = core.SessionCore;
            var tracks = new JsonArray();
            if (layout?.Document != null && session != null)
            {
                for (int t = 0; t < layout.TrackCount; t++)
                {
                    var seq = layout.SequenceOf(t);
                    if (seq?.Machine == null) continue;
                    var playing = session.GetPlaying(seq);
                    var queued = session.GetQueued(seq);
                    bool armed = layout.IsArmed(t), recording = layout.IsRecording(seq);
                    if (playing == null && queued == null && !armed && !recording) continue;
                    var track = new JsonObject { ["machine"] = seq.Machine.Name };
                    if (playing != null) track["playing"] = playing.Name;
                    if (queued != null) track["queued"] = queued.Name;
                    if (armed) track["armed"] = true;
                    if (recording) track["recording"] = true;
                    tracks.Add(track);
                }
                if (session.Clock >= 0 && core.PlayRequested)
                {
                    long grid = session.GridOrigin;
                    double sessionBeats = Math.Max(0, session.Clock - grid) / (double)tpb;
                    o["session_position"] = SongTime.BarBeat(sessionBeats, bpb);
                }
            }
            o["session"] = tracks;
            return o;
        }

        static string StateLine(JsonObject s)
        {
            var parts = new List<string> { s["playing"]!.GetValue<bool>() ? $"Playing at bar {s["position"]}" : $"Stopped at bar {s["position"]}" };
            parts[0] += $", {s["tempo_bpm"]} BPM" + (s["looping"]!.GetValue<bool>() ? ", looping" : "");
            foreach (var t in s["session"]!.AsArray())
            {
                string what = t!["playing"] is JsonNode p ? $"plays {p}" : "";
                if (t["queued"] is JsonNode q) what += (what.Length > 0 ? ", then " : "waits to play ") + q;
                if (t["recording"] != null) what += (what.Length > 0 ? ", " : "") + "records";
                else if (t["armed"] != null) what += (what.Length > 0 ? ", " : "") + "armed";
                parts.Add($"{t["machine"]} {what}");
            }
            return string.Join("; ", parts) + ".";
        }

        static JsonObject StateSchema() => Object(null,
            Req("playing", Boolean("Whether the transport runs")),
            Req("position", String("Song position, bar.beat")),
            Req("position_beats", Number("Song position in beats")),
            Req("tempo_bpm", Number("Tempo")),
            Req("looping", Boolean("Loop on")),
            Req("loop_start_beats", Number("Loop start, beats")),
            Req("loop_end_beats", Number("Loop end, beats")),
            Opt("session_position", String("Where the session's bar grid is, bar.beat (while it runs)")),
            Req("session", Array(Object(null, Req("machine", String("Track")), Opt("playing", String("Clip playing")), Opt("queued", String("Clip waiting to start")),
                Opt("armed", Boolean("Armed for recording")), Opt("recording", Boolean("Recording a take"))), "Session tracks with something going on")));

        // ------------------------------------------------------------------ transport

        static void Transport(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "transport",
                Title = "Play, stop, seek",
                Description =
                    "The transport, for the user to hear the song live: play (from at, or where it is), stop, seek (to at), loop on or off, " +
                    "or status. Returns what plays now: position, tempo, and the Session View clips playing or waiting. Studio mode only; " +
                    "in headless mode render the range instead. Not an undo step. Example: transport(action=\"play\", at=\"9.1\").",
                Input = Object(null,
                    Req("action", String("What to do", "play", "stop", "seek", "status")),
                    Opt("at", Position("play/seek: the song position")),
                    Opt("loop", Boolean("Turn the loop (song_set's loop range) on or off"))),
                Output = StateSchema(),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string action = c.RequiredString("action");
                    if (action != "status" || c.Bool("loop") != null)
                        RequireStudio(host, "transport", "render(from, to) plays a range into a file and analyze hears it.");
                    var s = await host.Engine("transport", false, _ =>
                    {
                        var song = core.SongCore;
                        int? at = c.Arguments["at"] != null ? SongModel.ParseTicks(core, c.Arguments["at"], "at") : null;
                        if (at is int a && a > song.SongEnd && !(core.SessionCore?.HasActivity ?? false))
                            throw new ToolError($"at: bar {SongTime.BarBeat(SongModel.Beats(core, a), SongModel.BeatsPerBar(core))} is past the song end " +
                                                $"(beat {SongModel.Beats(core, song.SongEnd)}); song_set(song_end_beats=…) moves it.");
                        if (c.Bool("loop") is bool loop) core.Looping = loop;
                        switch (action)
                        {
                            case "play":
                                if (at is int from) song.PlayPosition = from;
                                if (!core.PlayRequested) core.Playing = true;
                                break;
                            case "stop":
                                if (core.PlayRequested) core.Playing = false;
                                break;
                            case "seek":
                                if (at == null) throw new ToolError("seek needs at: a song position in beats or \"bar.beat\".");
                                song.PlayPosition = at.Value;
                                break;
                        }
                        return LiveState(core);
                    }, c.Cancellation);
                    return ToolResult.Ok(StateLine(s), s);
                },
            });
        }

        // ------------------------------------------------------------------ launching

        static int TrackOf(SessionLayout layout, IMachine m)
        {
            for (int t = 0; t < layout.TrackCount; t++)
                if (layout.SequenceOf(t)?.Machine == m) return t;
            return -1;
        }

        static void Launch(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "session_launch",
                Title = "Launch session clips",
                Description =
                    "Launches the Session View live, in time: a whole scene (scene), one slot (machine + scene), or any of a machine's patterns on " +
                    "its track (machine + pattern). Launches wait for the clip's quantization (session_clip_set) or the session's (default the next " +
                    "bar). stop=true stops a track (machine) or everything, at the quantization or immediate=true now; back_to_arrangement=true " +
                    "hands every track back to the arrangement. Launching starts the transport if it is stopped. Returns what plays and waits. " +
                    "Studio mode only. Not an undo step. Example: session_launch(scene=2).",
                Input = Object(null,
                    Opt("scene", Integer("Scene, from 1: the whole row, or with machine its slot", 1, 999)),
                    Opt("machine", String("A track's machine")),
                    Opt("pattern", String("With machine: launch this pattern on its track (in the grid or not)")),
                    Opt("stop", Boolean("Stop the machine's track, or every track")),
                    Opt("immediate", Boolean("With stop: now, not at the quantization")),
                    Opt("back_to_arrangement", Boolean("Every track follows the arrangement again"))),
                Output = StateSchema(),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    RequireStudio(host, "session_launch", "put the patterns in the arrangement (arrangement_place) and render the range.");
                    var s = await host.Engine("session_launch", false, _ =>
                    {
                        var layout = core.SessionLayout ?? throw new ToolError("This ReBuzz has no Session View.");
                        var session = core.SessionCore;
                        int? scene = c.Int("scene") is int sc ? sc - 1 : null;
                        string machineName = c.String("machine"), patternName = c.String("pattern");
                        MachineCore m = machineName != null ? SongModel.Machine(core, machineName) : null;
                        int track = -1;
                        if (m != null)
                        {
                            track = TrackOf(layout, m);
                            if (track < 0) throw new ToolError($"{m.Name} has no session track; session_clip_set(machine=\"{m.Name}\", scene=…, pattern=…) gives it one.");
                        }
                        if (c.Bool("back_to_arrangement") == true)
                        {
                            session.BackToArrangement();
                            return LiveState(core);
                        }
                        if (c.Bool("stop") == true)
                        {
                            bool now = c.Bool("immediate") == true;
                            if (m != null) session.Stop(layout.SequenceOf(track), now);
                            else session.StopAll(now);
                            return LiveState(core);
                        }
                        if (m == null)
                        {
                            if (scene == null) throw new ToolError("Say what to launch: scene=N for a scene, machine + scene for a slot, machine + pattern for a pattern, or stop=true.");
                            if (scene >= layout.SceneCount) throw new ToolError($"There are {layout.SceneCount} scenes; session_get shows them.");
                            layout.LaunchScene(scene.Value);
                            return LiveState(core);
                        }
                        var seq = layout.SequenceOf(track);
                        IPattern pattern;
                        if (patternName != null) pattern = SongModel.Pattern(m, patternName);
                        else if (scene != null)
                        {
                            if (scene >= layout.SceneCount) throw new ToolError($"There are {layout.SceneCount} scenes; session_get shows them.");
                            pattern = layout.PatternAt(track, scene.Value) ?? throw new ToolError($"{m.Name}'s slot in scene {scene + 1} is empty; session_get shows the grid.");
                        }
                        else throw new ToolError("With machine, say scene (its slot) or pattern.");
                        layout.LaunchClip(seq, pattern, 127, scene ?? -1);
                        return LiveState(core);
                    }, c.Cancellation);
                    return ToolResult.Ok(StateLine(s), s);
                },
            });
        }

        // ------------------------------------------------------------------ takes

        static void Record(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "session_record",
                Title = "Record session takes",
                Description =
                    "Records what is played on a machine's keyboard (the user's MIDI keyboard, or midi_play) into the Session View. " +
                    "arm: the machine's track takes the keyboard. start: a new take in its first empty slot from scene, bars long (0: until stop), " +
                    "counted in from a stopped transport; arms the track if needed. stop: ends the take (whole bars) and disarms a track start armed. " +
                    "capture: what armed tracks were played lately becomes clips, as Live's Capture MIDI. disarm, status. Studio mode only. " +
                    "Takes go on the Session View's undo history, not undo's. Example: session_record(action=\"start\", machine=\"Keys\", scene=1, bars=4).",
                Input = Object(null,
                    Req("action", String("What to do", "arm", "disarm", "start", "stop", "capture", "status")),
                    Opt("machine", String("The track's machine (arm, disarm, start, stop)")),
                    Opt("scene", Integer("start/capture: the first scene to look for an empty slot from (default 1)", 1, 999)),
                    Opt("bars", Integer("start: the take's length in bars; 0 records until stop (default: the Session View's setting)", 0, SessionDocument.MaxRecordBars))),
                Output = Object(null,
                    Opt("take", String("The take started or stopped")),
                    Opt("scene", Integer("start: the scene its slot is in")),
                    Opt("notes", Integer("Notes recorded in it so far")),
                    Opt("clips", Array(Object(null, Req("machine", String("Machine")), Req("pattern", String("Clip"))), "capture: the clips made")),
                    Req("state", StateSchema())),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    RequireStudio(host, "session_record", "write the notes with notes_write instead.");
                    string action = c.RequiredString("action");
                    var r = await host.Engine("session_record", true, _ =>
                    {
                        var layout = core.SessionLayout ?? throw new ToolError("This ReBuzz has no Session View.");
                        layout.Ensure();
                        var result = new JsonObject();
                        MachineCore m = c.String("machine") is string name ? SongModel.Machine(core, name) : null;
                        int Track()
                        {
                            if (m == null) throw new ToolError($"{action} needs machine.");
                            if (SongModel.IsMaster(m)) throw new ToolError("Master has no session track.");
                            if (!core.SongCore.Sequences.Any(s => s.Machine == m))
                                AgentActions.Do(core, new BuzzGUI.Common.Actions.MachineActions.AddSequenceAction(m));
                            int t = TrackOf(layout, m);
                            if (t < 0) throw new ToolError($"{m.Name} has no session track (its lane wasn't picked up).");
                            return t;
                        }
                        switch (action)
                        {
                            case "arm":
                                layout.SetArmed(Track(), true);
                                break;
                            case "disarm":
                                layout.SetArmed(Track(), false);
                                host.ArmedByAgent.Remove(m.Name);
                                break;
                            case "start":
                            {
                                int t = Track();
                                var seq = layout.SequenceOf(t);
                                if (layout.IsRecording(seq)) throw new ToolError($"{m.Name} is recording already; session_record(action=\"stop\", machine=\"{m.Name}\") ends it.");
                                if (!layout.IsArmed(t))
                                {
                                    layout.SetArmed(t, true);
                                    host.ArmedByAgent.Add(m.Name);
                                }
                                int s = (c.Int("scene") ?? 1) - 1;
                                while (s < layout.SceneCount && layout.PatternAt(t, s) != null) s++;
                                while (layout.SceneCount <= s) layout.InsertScene(layout.SceneCount);
                                var doc = layout.Document;
                                int bars = doc.RecordBars;
                                if (c.Int("bars") is int b) layout.SetRecordBars(b);
                                IPattern take;
                                try { take = layout.RecordTake(t, s); }
                                finally { if (c.Int("bars") != null) layout.SetRecordBars(bars); }
                                if (take == null) throw new ToolError($"{m.Name} couldn't start a take (its slot in scene {s + 1} isn't free, or the machine has no free pattern name).");
                                result["take"] = take.Name;
                                result["scene"] = s + 1;
                                break;
                            }
                            case "stop":
                            {
                                int t = Track();
                                var seq = layout.SequenceOf(t);
                                var take = FindTake(layout, t);
                                layout.StopRecording(seq);
                                if (host.ArmedByAgent.Remove(m.Name)) layout.SetArmed(t, false);
                                if (take != null)
                                {
                                    result["take"] = take.Name;
                                    result["notes"] = layout.RecordedNoteCount(take);
                                }
                                break;
                            }
                            case "capture":
                            {
                                var made = layout.CaptureMidi((c.Int("scene") ?? 1) - 1);
                                if (made.Count == 0)
                                    throw new ToolError("Nothing to capture: armed tracks keep what they're played while the transport runs. " +
                                                        "session_record(action=\"arm\", machine=…) first, then the user plays.");
                                result["clips"] = new JsonArray(made.Select(p => (JsonNode)new JsonObject { ["machine"] = p.Machine.Name, ["pattern"] = p.Name }).ToArray());
                                break;
                            }
                            case "status":
                                foreach (var t in Enumerable.Range(0, layout.TrackCount))
                                    if (m != null && layout.SequenceOf(t)?.Machine == m && FindTake(layout, t) is IPattern p)
                                    {
                                        result["take"] = p.Name;
                                        result["notes"] = layout.RecordedNoteCount(p);
                                    }
                                break;
                            default:
                                throw new ToolError("action: arm, disarm, start, stop, capture or status.");
                        }
                        result["state"] = LiveState(core);
                        return result;
                    }, c.Cancellation);
                    string line = action switch
                    {
                        "start" => $"Take {r["take"]} started in scene {r["scene"]}. ",
                        "stop" when r["take"] != null => $"Take {r["take"]} ended with {r["notes"]} notes. ",
                        "capture" => "Captured: " + string.Join(", ", r["clips"]!.AsArray().Select(x => $"{x!["pattern"]} on {x["machine"]}")) + ". ",
                        _ => "",
                    };
                    return ToolResult.Ok(line + StateLine(r["state"]!.AsObject()), r);
                },
            });
        }

        /// <summary>The take a track records into now, if any.</summary>
        static IPattern FindTake(SessionLayout layout, int track)
        {
            var seq = layout.SequenceOf(track);
            for (int s = 0; s < layout.SceneCount; s++)
                if (layout.PatternAt(track, s) is IPattern p && layout.IsRecordingInto(seq, p)) return p;
            return null;
        }

        // ------------------------------------------------------------------ playing into a machine

        static void MidiPlay(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "midi_play",
                Title = "Play notes live",
                Description =
                    "Plays notes into a machine live, as a keyboard player would, for the user to hear (auditioning a sound, playing along). " +
                    "Notes as notes_write takes them, start in beats from the phrase's start; the phrase starts now, on the next beat or on the next " +
                    "bar of the playing song (default: bar while playing, now while stopped). An armed track's take records them (session_record). " +
                    "stop=true lets go of everything playing. Returns at once. Studio mode only. " +
                    "Example: midi_play(machine=\"Keys\", notes=[{\"pitch\": \"C4\", \"start\": 0, \"length\": 1}, {\"pitch\": \"E4\", \"start\": 1, \"length\": 1}]).",
                Input = Object(null,
                    Opt("machine", String("The machine to play")),
                    Opt("notes", Array(Object("A note",
                        Req("pitch", AnyOf("Pitch", Integer("MIDI number, 60 = C4", 0, 127), String("Name: \"C4\", \"F#3\""))),
                        Req("start", Position("Start, from the phrase start")),
                        Req("length", Number("Length in beats", 0)),
                        Opt("velocity", Integer("1–127 (default 100)", 1, 127)),
                        Opt("name", String("The pitch's name (pitch decides)")),
                        Opt("midi_channel", Integer("MIDI channel 1–16 (default 1)", 1, 16))), "The notes")),
                    Opt("start", String("When the phrase starts", "now", "beat", "bar")),
                    Opt("stop", Boolean("Let go of every phrase playing"))),
                Output = Object(null,
                    Req("notes", Integer("Notes in the phrase")),
                    Req("beats", Number("Its length in beats")),
                    Req("starts", String("now, or the bar.beat of the session it starts on")),
                    Req("recorded", Boolean("Whether an armed track records it"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    RequireStudio(host, "midi_play", "write the notes with notes_write and render.");
                    if (c.Bool("stop") == true)
                    {
                        host.Player.StopAll();
                        return ToolResult.Ok("Stopped every phrase.", new JsonObject { ["notes"] = 0, ["beats"] = 0, ["starts"] = "now", ["recorded"] = false });
                    }
                    var r = await host.Engine("midi_play", false, _ =>
                    {
                        var m = SongModel.Machine(core, c.String("machine") ?? throw new ToolError("midi_play needs machine (or stop=true)."));
                        if (SongModel.IsMaster(m)) throw new ToolError("Master doesn't play notes.");
                        var list = c.List("notes") ?? throw new ToolError("midi_play needs notes.");
                        int tpb = SongModel.Tpb(core), bpb = SongModel.BeatsPerBar(core);
                        var problems = new List<string>();
                        var notes = NoteList.Parse(list, tpb, bpb, 1 << 20, problems);
                        if (notes.Count == 0) throw new ToolError("notes: none to play.");
                        // The written pitch plays (§12.2): the machine's pitch offset from its manifest.
                        var input = MachineNotes.For(m);
                        if (input.Tracks)
                            problems.Add($"{m.Name} takes notes only in its Note track columns (its manifest), so live MIDI may not reach it: write them with notes_write and render.");
                        if (input.Offset != 0)
                            notes = notes.Select(n => n with { Pitch = TrackNotes.Sent(n.Pitch, input.Offset, 127, m.Name) }).ToList();

                        var session = core.SessionCore;
                        long now = core.PlayRequested ? session.HeardTimeNow() : -1;
                        string start = c.String("start") ?? (now >= 0 ? "bar" : "now");
                        long origin = 0;
                        string starts = "now";
                        bool clocked = now >= 0;
                        if (clocked)
                        {
                            origin = now;
                            long beat = (long)tpb * TimeBase, bar = beat * bpb;
                            long grid = session.GridOrigin * TimeBase;
                            long step = start == "bar" ? bar : start == "beat" ? beat : 0;
                            if (step > 0) origin = grid + (long)Math.Ceiling((now - grid) / (double)step) * step;
                            if (step > 0) starts = SongTime.BarBeat(Math.Max(0, origin - grid) / (double)beat, bpb);
                        }
                        else if (start != "now") problems.Add("The transport is stopped, so the phrase starts now.");

                        var events = new List<(long At, int Data)>();
                        foreach (var n in notes)
                        {
                            int channel = Math.Clamp(n.Channel, 0, 15);
                            events.Add((origin + n.Time, 0x90 | channel | (n.Pitch << 8) | (n.Velocity << 16)));
                            events.Add((origin + n.End, 0x80 | channel | (n.Pitch << 8)));
                        }
                        // Note-offs first at the same time, so a repeated note sounds again.
                        var ordered = events.OrderBy(e => e.At).ThenBy(e => (e.Data & 0xF0) == 0x80 ? 0 : 1).ToArray();
                        var phrase = new LivePlayer.Phrase { Machine = m, Events = ordered, Clocked = clocked };
                        if (!clocked)
                        {
                            phrase.StartedAt = Stopwatch.GetTimestamp();
                            phrase.TimePerStopwatchTick = Math.Max(1, core.BPM) * (double)tpb * TimeBase / 60.0 / Stopwatch.Frequency;
                        }
                        host.Player.Play(phrase);
                        double beats = NoteList.Beats(notes.Max(n => n.End), tpb);
                        bool recorded = session.IsArmedMachine(m);
                        return (Notes: notes.Count, Beats: beats, Starts: starts, Recorded: recorded, Machine: m.Name, Problems: problems);
                    }, c.Cancellation);
                    string line = $"Playing {r.Notes} notes ({NoteList.Fmt(r.Beats)} beats) on {r.Machine}, " + (r.Starts == "now" ? "from now" : $"from bar {r.Starts}") +
                                  (r.Recorded ? "; its armed track records them." : ".") + (r.Problems.Count > 0 ? " " + string.Join(" ", r.Problems) : "");
                    return ToolResult.Ok(line, new JsonObject { ["notes"] = r.Notes, ["beats"] = r.Beats, ["starts"] = r.Starts, ["recorded"] = r.Recorded });
                },
            });
        }

        // ------------------------------------------------------------------ meters

        static void Meters(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "meters_read",
                Title = "Read live levels",
                Description =
                    "Listens to what plays for window_seconds (default 1) and reports the master's levels, and chosen machines' (each as its first " +
                    "connection carries it): loudness now (momentary, and short-term from a 3 s window), integrated over the window, loudest moment, " +
                    "true peak, RMS and clipped samples, measured as analyze measures renders (before the safety limiter on the audio output). " +
                    "safety tells whether the master's clip indicator latched an over and whether the safety limiter acted in the window; " +
                    "clear_clip clears the indicator first, as a click on it does. Returns after the window. Studio mode only; in " +
                    "headless mode render and analyze. Read-only. Example: meters_read(machines=[\"Bass\", \"Kick\"], window_seconds=2).",
                Input = Object(null,
                    Opt("machines", Array(String("A machine"), "Machines to meter too (up to 8)")),
                    Opt("window_seconds", Number("How long to listen, 0.1–30 (default 1)", 0.1, LiveMeter.MaxSeconds)),
                    Opt("clear_clip", Boolean("Clear the master's latched clip indicator before listening"))),
                Output = Object(null,
                    Req("playing", Boolean("Whether the transport ran")),
                    Opt("safety", Object("The master's clip indicator and the safety limiter (machine-safety.md §3.3)",
                        Opt("latched_over_dbfs", Level("The highest peak over the clip threshold since the indicator was cleared", "dBFS")),
                        Req("clip_threshold_dbfs", Number("Overs above this latch")),
                        Req("limiter_on", Boolean("The safety limiter is on")),
                        Req("limiter_acted", Boolean("It caught peaks over its ceiling during the window")))),
                    Req("meters", Array(Object(null,
                        Req("source", String("Master or the machine")),
                        Req("seconds", Number("Audio measured")),
                        Req("silent", Boolean("Nothing above -90 dBFS")),
                        Opt("momentary_lufs", Level("Loudness over the last 400 ms", "LUFS")),
                        Opt("short_term_lufs", Level("Over the last 3 s (windows of 3 s or more)", "LUFS")),
                        Opt("integrated_lufs", Level("Gated, over the window", "LUFS")),
                        Opt("momentary_max_lufs", Level("The loudest 400 ms in the window", "LUFS")),
                        Opt("true_peak_dbtp", Level("True peak (4× oversampled)", "dBTP")),
                        Opt("sample_peak_dbfs", Level("Sample peak", "dBFS")),
                        Opt("rms_dbfs", Level("RMS", "dBFS")),
                        Opt("clipped_samples", Integer("Samples at full scale"))), "One per source"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    RequireStudio(host, "meters_read", "render the range and analyze it (analyze_mix for every machine).");
                    double window = Math.Clamp(c.Number("window_seconds") ?? 1, 0.1, LiveMeter.MaxSeconds);
                    var names = c.List("machines")?.Select(n => n?.GetValue<string>()).Where(n => n != null).ToList() ?? new List<string>();
                    if (names.Count > 8) throw new ToolError("machines: 8 at most.");
                    int rate = ReBuzzCore.masterInfo.SamplesPerSec;
                    int frames = (int)Math.Round(window * rate);
                    var master = new LiveMeter("Master", frames, 1 / 32768f);
                    var taps = new List<(LiveMeter Meter, IMachineConnection Connection, Action<float[], bool, TapTime> Handler)>();
                    Action<float[], bool, TapTime> masterHandler = (samples, stereo, _) => master.Add(samples, stereo);
                    var safe = core.SafeMaster;
                    if (c.Bool("clear_clip") == true) safe.ClearOver();
                    long limitedBefore = safe.LimitedFrames;
                    bool playing = await host.Engine("meters_read", false, _ =>
                    {
                        foreach (var name in names)
                        {
                            var m = SongModel.Machine(core, name);
                            if (SongModel.IsMaster(m)) continue;
                            var conn = m.Outputs.FirstOrDefault(o => !((MachineCore)o.Destination).Hidden)
                                       ?? throw new ToolError($"{m.Name} feeds nothing, so there's nothing to meter; connect it (connect) first.");
                            float gain = conn.Amp / (float)Levels.UnityAmp;
                            var meter = new LiveMeter(m.Name, frames, 1 / 32768f);
                            Action<float[], bool, TapTime> handler = (samples, stereo, _) => meter.Add(samples, 0, samples.Length, stereo, gain);
                            taps.Add((meter, conn, handler));
                        }
                        core.MasterTap += masterHandler;
                        foreach (var t in taps) t.Connection.Tap += t.Handler;
                        return core.PlayRequested;
                    }, c.Cancellation);
                    try
                    {
                        // Real time: the window, plus room for the output to start.
                        var clock = Stopwatch.StartNew();
                        double limit = window * 1000 + 2000;
                        while ((!master.Full || taps.Any(t => !t.Meter.Full)) && clock.ElapsedMilliseconds < limit)
                            await Task.Delay(20, c.Cancellation);
                    }
                    finally
                    {
                        await host.Engine("meters_read", false, _ =>
                        {
                            core.MasterTap -= masterHandler;
                            foreach (var t in taps) t.Connection.Tap -= t.Handler;
                            return true;
                        }, System.Threading.CancellationToken.None);
                    }
                    if (master.Frames == 0)
                        throw new ToolError($"No audio came out in {Math.Round(window + 2, 1)} s: ReBuzz's audio output isn't running (Settings → Audio, or the device is in use).");
                    var meters = new JsonArray();
                    var lines = new List<string>();
                    foreach (var meter in new[] { master }.Concat(taps.Select(t => t.Meter)))
                    {
                        var report = LiveMeter.Report(meter.Audio(rate));
                        report["source"] = meter.Source;
                        meters.Add(report);
                        lines.Add(LiveMeter.Line(meter.Source, report));
                    }
                    string text = string.Join("; ", lines) + ".";
                    // Fork (machine-safety.md §3.3): the clip indicator and the safety limiter.
                    bool acted = safe.LimitedFrames > limitedBefore;
                    var safety = new JsonObject { ["clip_threshold_dbfs"] = safe.ClipThresholdDb, ["limiter_on"] = safe.LimiterOn, ["limiter_acted"] = acted };
                    if (safe.LatchedOverDb is double over)
                    {
                        safety["latched_over_dbfs"] = over;
                        text += $" The master's clip indicator latched {over:+0.0;-0.0} dBFS (meters_read(clear_clip=true) clears it).";
                    }
                    if (acted) text += " The safety limiter on the output caught peaks over its ceiling: lower the mix.";
                    if (!playing) text += " The transport is stopped: these are the levels of what sounds on its own; transport(action=\"play\") first.";
                    return ToolResult.Ok(text, new JsonObject { ["playing"] = playing, ["meters"] = meters, ["safety"] = safety });
                },
            });
        }

        // ------------------------------------------------------------------ analysis machines

        static void Readings(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;
            tools.Add(new ToolDef
            {
                Name = "machine_readings",
                Title = "Read an analysis machine",
                Description =
                    "The values an analysis machine shows (Neo Analyzer: loudness, peaks, correlation, spectrum bands; Neo Multiband: each band's gain change and level), from wherever the user put " +
                    "it in the graph. In studio mode they follow what plays; in headless mode they are what the last render played through it. " +
                    "Read-only. Example: machine_readings(machine=\"Analyzer\").",
                Input = Object(null, Req("machine", String("The analysis machine"))),
                Output = Object(null,
                    Req("machine", String("The machine")),
                    Req("readings", Map("Name to value", AnyOf("A reading", Number("Its value"), String("\"-inf\" (silence) or \"inf\""), new JsonObject { ["type"] = "null" })))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("machine_readings", false, _ =>
                    {
                        var m = SongModel.Machine(core, c.RequiredString("machine"));
                        var values = ReadingsOf(m) ?? throw new ToolError(
                            $"{m.Name} shows no readings: only analysis machines do (Neo Analyzer; Neo Multiband for its bands). Put one after what to measure " +
                            "(machine_add(gear=\"Neo Analyzer\"), connect), or render and analyze.");
                        var o = new JsonObject();
                        foreach (var (key, value) in values)
                            o[key] = double.IsNegativeInfinity(value) ? JsonValue.Create("-inf") : double.IsPositiveInfinity(value) ? JsonValue.Create("inf")
                                   : double.IsNaN(value) ? null : JsonValue.Create(Math.Round(value, 2));
                        return (Machine: m.Name, Readings: o);
                    }, c.Cancellation);
                    string line = r.Readings.Count == 0 ? $"{r.Machine} has nothing to show yet (nothing played through it)."
                        : $"{r.Machine}: " + string.Join(", ", r.Readings.Take(8).Select(kv => $"{kv.Key} {kv.Value?.ToJsonString().Trim('"') ?? "–"}")) +
                          (r.Readings.Count > 8 ? $", and {r.Readings.Count - 8} more" : "") + ".";
                    return ToolResult.Ok(line, new JsonObject { ["machine"] = r.Machine, ["readings"] = r.Readings });
                },
            });
        }

        /// <summary>A managed machine's <c>public IReadOnlyDictionary&lt;string, double&gt; Readings</c> (a snapshot), or null.</summary>
        static List<(string Key, double Value)> ReadingsOf(MachineCore m)
        {
            var target = m.ManagedMachine;
            if (target == null) return null;
            try
            {
                if (target.GetType().GetProperty("Readings")?.GetValue(target) is not IEnumerable<KeyValuePair<string, double>> values) return null;
                return values.Select(kv => (kv.Key, kv.Value)).ToList();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
