// Agent server (docs/specs/mcp-server.md §5.7): the agent's ears. render plays part of the song
// faster than real time through its own WorkManager (as ReBuzzTests' Driver does) and writes a
// 32-bit float WAV; analyze measures a render or a file (loudness, true peak, clipping).
//
// A render holds the audio lock for its whole length: in studio mode the user's output pauses
// while it runs. It changes nothing in the song: play position, loop, markers, mutes and the
// unsaved-changes flag are put back.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Audio;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal sealed record RenderInfo(string Id, string File, double FromBeats, double ToBeats, double Seconds);

    internal static class RenderTools
    {
        const double MaxSeconds = 600;

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "render",
                Title = "Render audio",
                Description =
                    "Plays part of the song faster than real time into a WAV file and measures it (loudness, true peak, clipping). Range: from/to " +
                    "in beats or \"bar.beat\" (default the whole song), or range=\"loop\". tail_seconds lets reverbs and releases ring out after " +
                    "the end (default 2). solo and mute hear parts without changing the song. Returns a render id for analyze, the file and the " +
                    "measurements. Changes nothing in the song. Example: render(from=0, to=32, solo=[\"Bass\", \"Drums\"]).",
                Input = Object(null,
                    Opt("from", AnyOf("Start", Number("Beats", 0), String("\"bar.beat\""))),
                    Opt("to", AnyOf("End (exclusive)", Number("Beats", 0), String("\"bar.beat\""))),
                    Opt("range", String("song (default) or loop", "song", "loop")),
                    Opt("tail_seconds", Number("Seconds rendered after the end, playback stopped (default 2)", 0, 30)),
                    Opt("solo", Array(String("A machine"), "Hear only these (and what feeds soloed effects)")),
                    Opt("mute", Array(String("A machine"), "Silence these")),
                    Opt("name", String("A label for the file name"))),
                Output = Object(null,
                    Req("id", String("Pass to analyze(source=…)")),
                    Req("file", String("The WAV file (32-bit float, stereo)")),
                    Req("seconds", Number("Length, with the tail")),
                    Req("from_beats", Number("Start")),
                    Req("to_beats", Number("End")),
                    Req("measure", Object("Loudness and peaks", Req("silent", Boolean("Nothing above -90 dBFS"))))),
                Annotations = new ToolAnnotations(ReadOnly: true),
                Handler = async c =>
                {
                    double tail = c.Number("tail_seconds") ?? 2;
                    var soloNames = c.List("solo")?.Select(x => x?.GetValue<string>()).ToList();
                    var muteNames = c.List("mute")?.Select(x => x?.GetValue<string>()).ToList();
                    string label = c.String("name");
                    var r = await host.Engine("render", false, _ =>
                    {
                        var song = core.SongCore;
                        int from, to;
                        if (c.String("range") == "loop")
                        {
                            from = song.LoopStart;
                            to = song.LoopEnd;
                        }
                        else
                        {
                            from = c.Arguments["from"] != null ? SongModel.ParseTicks(core, c.Arguments["from"], "from") : 0;
                            to = c.Arguments["to"] != null ? SongModel.ParseTicks(core, c.Arguments["to"], "to") : song.SongEnd;
                        }
                        if (to <= from) throw new ToolError($"to (beat {NoteList.Fmt(SongModel.Beats(core, to))}) must be after from (beat {NoteList.Fmt(SongModel.Beats(core, from))}).");
                        var solo = soloNames?.Select(n => SongModel.Machine(core, n, "solo")).ToHashSet();
                        var mute = muteNames?.Select(n => SongModel.Machine(core, n, "mute")).ToHashSet();
                        var warnings = new List<string>();
                        if (to > song.SongEnd)
                            warnings.Add($"The range runs past the song end (beat {NoteList.Fmt(SongModel.Beats(core, song.SongEnd))}); patterns placed there still play.");
                        int n = host.Renders.Count + 1;
                        string id = "r" + n.ToString(CultureInfo.InvariantCulture);
                        string folder = Path.Combine(Path.GetDirectoryName(host.Log.FilePath) ?? Path.GetTempPath(), "renders");
                        string file = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(host.Log.FilePath)}-{id}{SafeLabel(label)}.wav");
                        var audio = Render(core, from, to, tail, solo, mute, c, warnings);
                        var interleaved = new float[audio.Frames * 2];
                        for (int i = 0; i < audio.Frames; i++)
                        {
                            interleaved[2 * i] = audio.Left[i];
                            interleaved[2 * i + 1] = audio.Right[i];
                        }
                        Wav.WriteStereoFloat(file, interleaved, audio.Frames, audio.Rate);
                        var info = new RenderInfo(id, file, SongModel.Beats(core, from), SongModel.Beats(core, to), audio.Seconds);
                        host.Renders[id] = info;
                        var measure = AudioReport.Measure(audio, AudioReport.Metrics);
                        // The played part alone, for loudness without the tail's silence.
                        return new JsonObject
                        {
                            ["id"] = id,
                            ["file"] = file,
                            ["seconds"] = Math.Round(audio.Seconds, 3),
                            ["from_beats"] = info.FromBeats,
                            ["to_beats"] = info.ToBeats,
                            ["sample_rate"] = audio.Rate,
                            ["measure"] = measure,
                            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w).ToArray()),
                        };
                    }, c.Cancellation);
                    var m = r["measure"]!.AsObject();
                    var warn = r["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
                    return ToolResult.Ok($"Rendered {r["id"]}: beats {r["from_beats"]}–{r["to_beats"]}, {r["seconds"]} s → {r["file"]}. " +
                                         $"{AudioReport.Line(m)}. " + string.Join(" ", m["reads"]!.AsArray().Select(x => x!.GetValue<string>())) +
                                         (warn.Count > 0 ? " " + string.Join(" ", warn) : ""), r)
                        .WithLink(new Uri(r["file"]!.GetValue<string>()).AbsoluteUri, Path.GetFileName(r["file"]!.GetValue<string>()), "audio/wav", "The render");
                },
            });

            tools.Add(new ToolDef
            {
                Name = "analyze",
                Title = "Measure audio",
                Description =
                    "Measures a render (its id) or a WAV file (a full path, e.g. a reference track): loudness (integrated, short-term and momentary " +
                    "max in LUFS, loudness range in LU) and peaks (true peak dBTP, sample peak dBFS, clipped samples), each with a plain read. " +
                    "Optionally a part, from_seconds to to_seconds. Read-only. Example: analyze(source=\"r3\", metrics=[\"loudness\"]).",
                Input = Object(null,
                    Req("source", String("A render id (\"r1\") or a WAV file's full path")),
                    Opt("metrics", Array(String("A metric", AudioReport.Metrics), "Which measures (default all)")),
                    Opt("from_seconds", Number("From here", 0)),
                    Opt("to_seconds", Number("To here", 0))),
                Output = Object(null,
                    Req("source", String("What was measured")),
                    Req("seconds", Number("Length measured")),
                    Req("silent", Boolean("Nothing above -90 dBFS")),
                    Req("reads", Array(String("A plain-language read"), "What the numbers mean"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    string source = c.RequiredString("source").Trim();
                    var metrics = c.List("metrics")?.Select(x => x!.GetValue<string>()).ToList() ?? AudioReport.Metrics.ToList();
                    string file = host.Renders.TryGetValue(source, out var render) ? render.File : null;
                    if (file == null)
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(source, @"^r\d+$"))
                            throw new ToolError($"No render \"{source}\" in this session." + (host.Renders.Count > 0 ? $" Renders: {string.Join(", ", host.Renders.Keys)}." : " Make one with render()."));
                        file = Path.GetFullPath(source.Trim('"'));
                    }
                    var audio = await System.Threading.Tasks.Task.Run(() => Wav.Read(file), c.Cancellation);
                    double? fromS = c.Number("from_seconds"), toS = c.Number("to_seconds");
                    if (fromS.HasValue && toS.HasValue && toS <= fromS) throw new ToolError("to_seconds must be after from_seconds.");
                    var part = AudioReport.Slice(audio, fromS, toS);
                    var m = AudioReport.Measure(part, metrics);
                    m["source"] = render != null ? $"{source} ({file})" : file;
                    return ToolResult.Ok($"{(render != null ? source : Path.GetFileName(file))}: {m["seconds"]} s, {AudioReport.Line(m)}. " +
                                         string.Join(" ", m["reads"]!.AsArray().Select(x => x!.GetValue<string>())), m);
                },
            });
        }

        static string SafeLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";
            var chars = label.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-').Take(40).ToArray();
            return "-" + new string(chars);
        }

        /// <summary>Renders [from, to) ticks plus a tail on the engine thread; puts the song back as it was.</summary>
        static StereoAudio Render(ReBuzzCore core, int from, int to, double tailSeconds, HashSet<MachineCore> solo, HashSet<MachineCore> mute,
            ToolContext c, List<string> warnings)
        {
            var song = core.SongCore;
            var info = ReBuzzCore.masterInfo;
            int rate = info.SamplesPerSec;
            double samplesPerTick = 60.0 * rate / (core.BPM * (double)Math.Max(1, core.TPB));
            long playFrames = (long)Math.Round((to - from) * samplesPerTick);
            long tailFrames = (long)Math.Round(tailSeconds * rate);
            if ((playFrames + tailFrames) / (double)rate > MaxSeconds)
                throw new ToolError($"That's {Math.Round((playFrames + tailFrames) / (double)rate)} s; renders are limited to {MaxSeconds} s. Render a part (from/to).");
            var left = new float[playFrames + tailFrames];
            var right = new float[playFrames + tailFrames];

            // What to put back.
            bool wasPlaying = core.Playing, wasLooping = core.Looping, wasModified = core.Modified;
            int loopStart = song.LoopStart, loopEnd = song.LoopEnd, songEnd = song.SongEnd, position = song.PlayPosition;
            var machines = SongModel.Machines(core).Where(m => !SongModel.IsMaster(m)).ToList();
            var mutedBefore = machines.ToDictionary(m => m, m => m.IsMuted);
            if (wasPlaying) warnings.Add("Playback was running; the render stopped it.");

            lock (ReBuzzCore.AudioLock)
            {
                try
                {
                    core.Playing = false;
                    foreach (var m in machines.Where(m => Silenced(m, solo, mute) && !m.IsMuted)) m.IsMuted = true;
                    core.Looping = false;
                    song.LoopStart = 0;
                    song.LoopEnd = Math.Max(to, 1);
                    song.LoopStart = from;
                    song.PlayPosition = from;

                    var wm = new WorkManager(core, new WorkThreadEngine(1), 0, core.EngineSettings);
                    var buffer = new float[512];
                    // Playback starts at the end of the tick under way: run silently up to it.
                    core.Playing = true;
                    for (int guard = 0; !core.Playing && guard < 100000; guard++)
                    {
                        int pos = info.PosInTick;
                        int n = pos == 0 ? 1 : Math.Clamp(info.SamplesPerTick - pos, 1, 256);
                        wm.MainAudioFillBuffer(buffer, 0, n * 2);
                    }
                    if (!core.Playing) throw new ToolError("Playback didn't start (is ReBuzz following an external clock?).");

                    long done = 0, total = playFrames + tailFrames, nextReport = rate;
                    while (done < total)
                    {
                        c.Cancellation.ThrowIfCancellationRequested();
                        if (done >= playFrames && core.Playing) core.Playing = false;   // the tail rings out stopped
                        long limit = done < playFrames ? playFrames : total;
                        int n = (int)Math.Min(256, limit - done);
                        wm.MainAudioFillBuffer(buffer, 0, n * 2);
                        for (int i = 0; i < n; i++)
                        {
                            left[done + i] = buffer[2 * i];
                            right[done + i] = buffer[2 * i + 1];
                        }
                        done += n;
                        if (done >= nextReport)
                        {
                            c.Progress(done / (double)rate, total / (double)rate, "seconds rendered");
                            nextReport += rate * 5L;
                        }
                    }
                }
                finally
                {
                    core.Playing = false;
                    foreach (var m in machines) if (m.IsMuted != mutedBefore[m]) m.IsMuted = mutedBefore[m];
                    core.Looping = wasLooping;
                    song.LoopStart = 0;
                    song.LoopEnd = loopEnd;
                    song.LoopStart = loopStart;
                    song.SongEnd = songEnd;
                    song.PlayPosition = position;
                    if (!wasModified) core.Modified = false;
                }
            }
            return new StereoAudio { Left = left, Right = right, Rate = rate };
        }

        /// <summary>Whether a machine is kept quiet for a render's solo and mute lists.</summary>
        static bool Silenced(MachineCore m, HashSet<MachineCore> solo, HashSet<MachineCore> mute)
        {
            if (mute != null && mute.Contains(m)) return true;
            if (solo == null || solo.Count == 0 || SongModel.Kind(m) != "generator") return false;
            if (solo.Contains(m)) return false;
            // A generator feeding a soloed effect (directly or through other effects) stays.
            var seen = new HashSet<IMachine>();
            var stack = new Stack<IMachine>(m.Outputs.Select(o => o.Destination));
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (!seen.Add(d)) continue;
                if (d is MachineCore dc && solo.Contains(dc)) return false;
                foreach (var o in d.Outputs) stack.Push(o.Destination);
            }
            return true;
        }
    }
}
