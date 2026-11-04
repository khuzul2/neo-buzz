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
                    "in beats or \"bar.beat\" (default the whole song), range=\"loop\", or section=\"Verse\" (a marker). tail_seconds lets reverbs and releases ring out after " +
                    "the end (default 2). solo and mute hear parts without changing the song (solo keeps what feeds a soloed effect). probes also " +
                    "capture chosen machines' outputs (after their connection gain), each with its own id \"r3:Bass\" for analyze. Follows the " +
                    "song's tempo changes. Returns a render id for analyze, the file and the measurements. Changes nothing in the song, but stops " +
                    "playback if it's running; in studio mode ReBuzz pauses while it renders. Example: render(from=0, to=32, solo=[\"Bass\", \"Drums\"]).",
                Input = Object(null,
                    Opt("from", AnyOf("Start", Number("Beats", 0), String("\"bar.beat\""))),
                    Opt("to", AnyOf("End (exclusive)", Number("Beats", 0), String("\"bar.beat\""))),
                    Opt("range", String("song (default) or loop", "song", "loop")),
                    Opt("section", String("A marker's name: from it to the next marker (arrangement_markers)")),
                    Opt("tail_seconds", Number("Seconds rendered after the end, playback stopped (default 2)", 0, 30)),
                    Opt("solo", Array(String("A machine"), "Hear only these (and what feeds soloed effects)")),
                    Opt("mute", Array(String("A machine"), "Silence these")),
                    Opt("probes", Array(String("A machine"), "Also capture these machines' outputs")),
                    Opt("name", String("A label for the file name"))),
                Output = Object(null,
                    Req("id", String("Pass to analyze(source=…)")),
                    Req("file", String("The WAV file (32-bit float, stereo)")),
                    Req("seconds", Number("Length, with the tail")),
                    Req("from_beats", Number("Start")),
                    Req("to_beats", Number("End")),
                    Req("measure", Object("Loudness and peaks", Req("silent", Boolean("Nothing above -90 dBFS")))),
                    Opt("diverged", Array(Object("A machine whose guard tripped in the render: silenced from there on and left muted (machine_set(reset=true))",
                        Req("machine", String("The machine")), Req("reason", String("NaN, infinity or its peak above +60 dBFS"))), "Machines that diverged"))),
                Annotations = new ToolAnnotations(ReadOnly: true),
                Handler = async c =>
                {
                    double tail = c.Number("tail_seconds") ?? 2;
                    var soloNames = c.List("solo")?.Select(x => x?.GetValue<string>()).ToList();
                    var muteNames = c.List("mute")?.Select(x => x?.GetValue<string>()).ToList();
                    var probeNames = c.List("probes")?.Select(x => x?.GetValue<string>()).ToList();
                    string label = c.String("name");
                    var r = await host.Engine("render", false, _ =>
                    {
                        var song = core.SongCore;
                        int from, to;
                        if (c.String("section") is string section)
                            (from, to) = CompositionTools.Section(core, section);
                        else if (c.String("range") == "loop")
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
                        var probes = probeNames?.Select(n => SongModel.Machine(core, n, "probes")).Distinct().ToList() ?? new List<MachineCore>();
                        if (probes.Any(SongModel.IsMaster)) throw new ToolError("probes: Master is the render itself; probe the machines feeding it.");
                        var warnings = new List<string>();
                        if (to > song.SongEnd)
                            warnings.Add($"The range runs past the song end (beat {NoteList.Fmt(SongModel.Beats(core, song.SongEnd))}); patterns placed there still play.");
                        int n = host.Renders.Count + 1;
                        string id = "r" + n.ToString(CultureInfo.InvariantCulture);
                        string folder = Path.Combine(Path.GetDirectoryName(host.Log.FilePath) ?? Path.GetTempPath(), "renders");
                        string file = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(host.Log.FilePath)}-{id}{SafeLabel(label)}.wav");
                        var divergedBefore = SongModel.Machines(core).Where(m => m.IsDiverged).ToHashSet();
                        var (audio, probed) = Render(host, from, to, tail, solo, mute, probes, c, warnings);
                        double fromBeats = SongModel.Beats(core, from), toBeats = SongModel.Beats(core, to);
                        Write(file, audio);
                        host.Renders[id] = new RenderInfo(id, file, fromBeats, toBeats, audio.Seconds);
                        var measure = AudioReport.Measure(audio, Summary);
                        var probeList = new JsonArray();
                        foreach (var (machine, pa) in probed)
                        {
                            string pid = id + ":" + machine;
                            string pfile = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(file)}-{SafeLabel(machine).TrimStart('-')}.wav");
                            Write(pfile, pa);
                            host.Renders[pid] = new RenderInfo(pid, pfile, fromBeats, toBeats, pa.Seconds);
                            probeList.Add(new JsonObject { ["id"] = pid, ["machine"] = machine, ["file"] = pfile, ["measure"] = AudioReport.Measure(pa, Summary) });
                        }
                        var info = host.Renders[id];
                        // Fork (machine-safety.md §3.2): machines whose guard tripped in this render.
                        var diverged = new JsonArray(SongModel.Machines(core).Where(m => m.IsDiverged && !divergedBefore.Contains(m))
                            .Select(m => (JsonNode)new JsonObject { ["machine"] = m.Name, ["reason"] = m.DivergedReason }).ToArray());
                        var result = new JsonObject
                        {
                            ["id"] = id,
                            ["file"] = file,
                            ["seconds"] = Math.Round(audio.Seconds, 3),
                            ["from_beats"] = info.FromBeats,
                            ["to_beats"] = info.ToBeats,
                            ["sample_rate"] = audio.Rate,
                            ["measure"] = measure,
                            ["probes"] = probeList,
                            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w).ToArray()),
                        };
                        if (diverged.Count > 0) result["diverged"] = diverged;
                        return result;
                    }, c.Cancellation);
                    var m = r["measure"]!.AsObject();
                    var warn = r["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
                    var probeLines = r["probes"]!.AsArray().Select(pr => $"{pr!["id"]}: {AudioReport.Line(pr["measure"]!.AsObject())}").ToList();
                    return ToolResult.Ok($"Rendered {r["id"]}: beats {r["from_beats"]}–{r["to_beats"]}, {r["seconds"]} s → {r["file"]}. " +
                                         $"{AudioReport.Line(m)}. " + string.Join(" ", m["reads"]!.AsArray().Select(x => x!.GetValue<string>())) +
                                         (probeLines.Count > 0 ? " Probes: " + string.Join("; ", probeLines) + "." : "") +
                                         (warn.Count > 0 ? " " + string.Join(" ", warn) : ""), r)
                        .WithLink(new Uri(r["file"]!.GetValue<string>()).AbsoluteUri, Path.GetFileName(r["file"]!.GetValue<string>()), "audio/wav", "The render");
                },
            });

            tools.Add(new ToolDef
            {
                Name = "analyze",
                Title = "Measure audio",
                Description =
                    "Measures a render, a probe (\"r3:Bass\") or a WAV file (a full path, e.g. a reference track), each number with a plain read: " +
                    "loudness (LUFS, range), peaks (true peak dBTP, clipping), dynamics (crest, PLR), spectrum (bands, third octaves, tilt, centroid), " +
                    "stereo (correlation, width per band, mono loss), pitch (monophonic notes and cents), key, tempo. Default: all; pick metrics to be " +
                    "quick. Optionally a part, from_seconds to to_seconds. Read-only. Example: analyze(source=\"r3\", metrics=[\"spectrum\", \"stereo\"]).",
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
                    var (label, file, audio) = await Load(host, source, c);
                    double? fromS = c.Number("from_seconds"), toS = c.Number("to_seconds");
                    if (fromS.HasValue && toS.HasValue && toS <= fromS) throw new ToolError("to_seconds must be after from_seconds.");
                    var part = AudioReport.Slice(audio, fromS, toS);
                    var m = await System.Threading.Tasks.Task.Run(() => AudioReport.Measure(part, metrics), c.Cancellation);
                    m["source"] = label == file ? file : $"{label} ({file})";
                    return ToolResult.Ok($"{label}: {m["seconds"]} s, {AudioReport.Line(m)}. " +
                                         string.Join(" ", m["reads"]!.AsArray().Select(x => x!.GetValue<string>())), m);
                },
            });
        }

        /// <summary>A render or probe by id, or a WAV file by path: (label, file, audio).</summary>
        internal static async System.Threading.Tasks.Task<(string Label, string File, StereoAudio Audio)> Load(AgentHost host, string source, ToolContext c)
        {
            source = source.Trim();
            string file = host.Renders.TryGetValue(source, out var render) ? render.File : null;
            if (file == null)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(source, @"^r\d+(:.+)?$"))
                    throw new ToolError($"No render \"{source}\" in this session." + (host.Renders.IsEmpty ? " Make one with render()." : $" Renders: {string.Join(", ", host.Renders.Keys.OrderBy(k => k))}."));
                file = Path.GetFullPath(source.Trim('"'));
            }
            var audio = await System.Threading.Tasks.Task.Run(() => Wav.Read(file), c.Cancellation);
            return (render != null ? source : Path.GetFileName(file), file, audio);
        }

        /// <summary>What render reports by itself; analyze has the rest.</summary>
        static readonly string[] Summary = { "loudness", "peaks" };

        static void Write(string file, StereoAudio audio)
        {
            var interleaved = new float[audio.Frames * 2];
            for (int i = 0; i < audio.Frames; i++)
            {
                interleaved[2 * i] = audio.Left[i];
                interleaved[2 * i + 1] = audio.Right?[i] ?? audio.Left[i];
            }
            Wav.WriteStereoFloat(file, interleaved, audio.Frames, audio.Rate);
        }

        static string SafeLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";
            var chars = label.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-').Take(40).ToArray();
            return "-" + new string(chars);
        }

        /// <summary>
        /// Renders [from, to) ticks plus a tail on the engine thread, and puts the song back as it
        /// was. Playback ends where the song's own playback would (tempo changes included): the
        /// loop is set to the range with looping off, so the engine stops at its end.
        /// </summary>
        static (StereoAudio Master, List<(string Machine, StereoAudio Audio)> Probes) Render(AgentHost host, int from, int to, double tailSeconds,
            HashSet<MachineCore> solo, HashSet<MachineCore> mute, IReadOnlyList<MachineCore> probes, ToolContext c, List<string> warnings) =>
            Render(host.Core, host.Dispatcher, from, to, tailSeconds, solo, mute, probes, c.Cancellation, (done, total, what) => c.Progress(done, total, what), warnings);

        /// <summary>
        /// The render itself, for the agent's tools and for machine_replace's comparison from the machine menu (U5), which
        /// has no agent host: <paramref name="progress"/> gets (seconds done, seconds in all, what).
        /// </summary>
        internal static (StereoAudio Master, List<(string Machine, StereoAudio Audio)> Probes) Render(ReBuzzCore core, IUiDispatcher dispatcher, int from, int to, double tailSeconds,
            HashSet<MachineCore> solo, HashSet<MachineCore> mute, IReadOnlyList<MachineCore> probes, System.Threading.CancellationToken cancellation,
            Action<double, double, string> progress, List<string> warnings, IReadOnlyList<(string Label, IMachineConnection Connection, double Amp)> connections = null)
        {
            var song = core.SongCore;
            var info = ReBuzzCore.masterInfo;
            int rate = info.SamplesPerSec;
            double samplesPerTick = 60.0 * rate / (core.BPM * (double)Math.Max(1, core.TPB));
            long expected = (long)Math.Round((to - from) * samplesPerTick);
            long tailFrames = (long)Math.Round(tailSeconds * rate);
            long cap = (long)(MaxSeconds * rate);
            if (expected + tailFrames > cap)
                throw new ToolError($"That's about {Math.Round((expected + tailFrames) / (double)rate)} s; renders are limited to {MaxSeconds} s. Render a part (from/to).");
            var left = new List<float>((int)Math.Min(cap, expected + tailFrames + rate));
            var right = new List<float>(left.Capacity);

            // What to put back.
            bool wasPlaying = core.Playing, wasLooping = core.Looping, wasModified = core.Modified;
            bool metronome = core.SessionCore.MetronomeOn;
            int bpm = core.BPM, tpb = core.TPB;
            int loopStart = song.LoopStart, loopEnd = song.LoopEnd, songEnd = song.SongEnd, position = song.PlayPosition;
            var machines = SongModel.Machines(core).Where(m => !SongModel.IsMaster(m)).ToList();
            var mutedBefore = machines.ToDictionary(m => m, m => m.IsMuted);
            var soloedBefore = machines.Where(m => m.IsSoloed).ToList();
            if (wasPlaying) warnings.Add("Playback was running; the render stopped it.");

            // Probes: a machine's output as its first connection carries it (taps run on the engine's thread, in order).
            var taps = new List<Probe>();
            foreach (var m in probes)
            {
                var conn = m.Outputs.FirstOrDefault(o => !((MachineCore)o.Destination).Hidden);
                if (conn == null) { warnings.Add($"{m.Name} feeds nothing, so there's nothing to probe."); continue; }
                taps.Add(new Probe(m.Name, conn));
            }
            // U5: chosen connections, at a gain given in Buzz amp units (machine_replace taps a machine's inputs).
            foreach (var (label, conn, amp) in connections ?? System.Array.Empty<(string Label, IMachineConnection Connection, double Amp)>())
                taps.Add(new Probe(label, conn, (float)(amp / Levels.UnityAmp / 32768.0)));

            lock (ReBuzzCore.AudioLock)
            {
                try
                {
                    core.Playing = false;
                    core.SessionCore.MetronomeOn = false;
                    foreach (var m in soloedBefore) m.IsSoloed = false;
                    foreach (var m in machines.Where(m => Silenced(m, solo, mute) && !m.IsMuted)) m.IsMuted = true;
                    core.Looping = false;
                    song.LoopStart = 0;
                    song.LoopEnd = Math.Max(to, 1);
                    song.LoopStart = from;
                    song.PlayPosition = from;

                    var wm = new WorkManager(core, new WorkThreadEngine(1), 0, core.EngineSettings);
                    var buffer = new float[512];
                    // A6: every render starts on a tick boundary, wherever the engine's tick phase was
                    // left, so its blocks (and so every machine's state when the first note plays) are
                    // the same each time: renders of a freshly opened song are bit-identical, in this
                    // process or another (docs/specs/mcp-server.md §10).
                    info.PosInTick = 0;
                    var sub = ReBuzzCore.subTickInfo;
                    sub.PosInSubTick = 0;
                    sub.CurrentSubTick = 0;
                    sub.SubTickReminderCounter = 0;
                    // Machines still preparing (Neo Wave building a table in the background) get up to
                    // 30 s. The wait runs no audio: had it run the machines, how long it took would move
                    // their free-running state (control grids, free LFOs) and so the render, from run to
                    // run (mac-engine.md §6, the null test).
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 30000 && machines.Any(Busy))
                        System.Threading.Thread.Sleep(5);
                    var preparing = machines.Where(Busy).Select(m => m.Name).ToList();
                    if (preparing.Count > 0)
                        warnings.Add($"{string.Join(", ", preparing)} still preparing after 30 s: the render may differ from run to run.");
                    // Tails of what played before (reverbs, delays) die away first, so renders compare.
                    for (long quiet = 0, run = 0; run < 5L * rate && quiet < rate / 10; run += 256)
                    {
                        wm.MainAudioFillBuffer(buffer, 0, 512);
                        quiet = buffer.All(x => Math.Abs(x) < 1e-5f) ? quiet + 256 : 0;
                    }
                    // Playback starts at the end of the tick under way: run silently up to it.
                    core.Playing = true;
                    for (int guard = 0; !core.Playing && guard < 100000; guard++)
                    {
                        int pos = info.PosInTick;
                        int n = pos == 0 ? 1 : Math.Clamp(info.SamplesPerTick - pos, 1, 256);
                        wm.MainAudioFillBuffer(buffer, 0, n * 2);
                    }
                    if (!core.Playing) throw new ToolError("Playback didn't start (is ReBuzz following an external clock?).");

                    foreach (var t in taps) t.Start();
                    void Take(int n)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            left.Add(buffer[2 * i]);
                            right.Add(buffer[2 * i + 1]);
                        }
                        foreach (var t in taps) t.Align(left.Count);
                    }
                    long nextReport = 5L * rate;
                    int last = song.PlayPosition;
                    // Until the engine stops at the range's end (or the position wraps, if something keeps it going).
                    while (core.Playing && left.Count < cap)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        wm.MainAudioFillBuffer(buffer, 0, 512);
                        Take(256);
                        int now = song.PlayPosition;
                        if (now < last) break;
                        last = now;
                        if (left.Count >= nextReport)
                        {
                            progress?.Invoke(left.Count / (double)rate, (expected + tailFrames) / (double)rate, "seconds rendered");
                            nextReport += 5L * rate;
                        }
                    }
                    core.Playing = false;   // the tail rings out stopped (note-offs go out in its first block)
                    long tailEnd = left.Count + tailFrames;
                    do
                    {
                        wm.MainAudioFillBuffer(buffer, 0, 512);
                        Take((int)Math.Clamp(tailEnd - left.Count, 0, 256));
                    }
                    while (left.Count < tailEnd);
                    if (Math.Abs(left.Count - tailFrames - expected) > rate / 20)
                        warnings.Add($"The range played {Math.Round((left.Count - tailFrames) / (double)rate, 2)} s (tempo changes in the song).");
                }
                finally
                {
                    foreach (var t in taps) t.Stop();
                    core.Playing = false;
                    // Fork (machine-safety.md §3.2): a machine whose guard tripped stays muted (unmuting resets it).
                    foreach (var m in machines.Where(m => m.IsDiverged && !mutedBefore[m]))
                        warnings.Add($"{m.Name} diverged ({m.DivergedReason}) at beat {Math.Round(m.DivergedPosition / (double)Math.Max(1, core.TPB), 2)}: " +
                                     $"the guard silenced and muted it. machine_set(machine=\"{m.Name}\", reset=true) brings it back.");
                    foreach (var m in machines) if (m.IsMuted != mutedBefore[m] && !m.IsDiverged) m.IsMuted = mutedBefore[m];
                    foreach (var m in soloedBefore) m.IsSoloed = true;
                    core.SessionCore.MetronomeOn = metronome;
                    core.Looping = wasLooping;
                    if (core.TPB != tpb) core.TPB = tpb;
                    if (core.BPM != bpm) core.BPM = bpm;
                    song.LoopStart = 0;
                    song.LoopEnd = loopEnd;
                    song.LoopStart = loopStart;
                    song.SongEnd = songEnd;
                    song.PlayPosition = position;
                    if (!wasModified)
                    {
                        core.Modified = false;
                        dispatcher?.BeginInvoke(() => core.Modified = false);   // after deferred updates
                    }
                }
            }
            var master = new StereoAudio { Left = left.ToArray(), Right = right.ToArray(), Rate = rate };
            return (master, taps.Select(t => (t.Machine, t.Audio(rate))).ToList());
        }

        /// <summary>A machine's output captured from its connection's tap, scaled by the connection's gain, aligned to the render.</summary>
        sealed class Probe
        {
            readonly IMachineConnection connection;
            readonly Action<float[], bool, BuzzGUI.Interfaces.SongTime> handler;
            readonly List<float> left = new(), right = new();
            readonly float gain;
            bool on;

            public Probe(string machine, IMachineConnection connection, float? scale = null)
            {
                Machine = machine;
                this.connection = connection;
                gain = scale ?? connection.Amp / (float)Levels.UnityAmp / 32768f;
                handler = (samples, stereo, _) =>
                {
                    if (!on) return;
                    for (int i = 0; i + 1 < samples.Length; i += 2)
                    {
                        left.Add(samples[i] * gain);
                        right.Add(samples[i + 1] * gain);
                    }
                };
                connection.Tap += handler;
            }

            public string Machine { get; }

            public void Start() { left.Clear(); right.Clear(); on = true; }

            /// <summary>Pads (a block the machine didn't work) or trims to the render's length.</summary>
            public void Align(int frames)
            {
                if (left.Count < frames) { left.AddRange(new float[frames - left.Count]); right.AddRange(new float[frames - right.Count]); }
                else if (left.Count > frames) { left.RemoveRange(frames, left.Count - frames); right.RemoveRange(frames, right.Count - frames); }
            }

            public void Stop() { on = false; connection.Tap -= handler; }

            public StereoAudio Audio(int rate) => new() { Left = left.ToArray(), Right = right.ToArray(), Rate = rate };
        }

        /// <summary>A machine's own "still preparing" flag (a public bool Busy on its managed machine), if it has one.</summary>
        static bool Busy(MachineCore m)
        {
            var target = m.ManagedMachine;
            if (target == null) return false;
            try { return target.GetType().GetProperty("Busy")?.GetValue(target) is true; }
            catch (Exception) { return false; }
        }

        /// <summary>Whether a machine is kept quiet for a render's solo and mute lists.</summary>
        static bool Silenced(MachineCore m, HashSet<MachineCore> solo, HashSet<MachineCore> mute)
        {
            if (mute != null && mute.Contains(m)) return true;
            if (solo == null || solo.Count == 0 || SongModel.Kind(m) != "generator") return false;
            // Control machines (peer controllers, LFOs) shape what they control; they stay.
            if (m.IsControlMachine || (m.DLL?.Info != null && (m.DLL.Info.Flags & MachineInfoFlags.CONTROL_MACHINE) != 0)) return false;
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
