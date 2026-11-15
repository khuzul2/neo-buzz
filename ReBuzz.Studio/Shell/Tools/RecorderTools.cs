// Fork (docs/specs/agent-knowledge.md K3b, ux-overhaul.md §9 row 8): master_record, the Hard Disk Recorder for agents. It records
// the Master as it plays in real time to a WAV file, with the same MasterRecorder the window uses (so the same file format,
// ring and "blocks lost" count). Studio only, and only with an audio output open: offline, export_audio renders instead.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Studio.Shell.Tools
{
    internal sealed class RecorderTools
    {
        /// <summary>Whether an audio output is open (tests replace it: the headless studio has none).</summary>
        internal static Func<ReBuzzCore, bool> OutputProbe = core => core.AudioEngine?.SelectedOutDevice != null;

        readonly AgentHost host;
        MasterRecorder recorder;

        RecorderTools(AgentHost host) => this.host = host;

        public static void Register(AgentHost host) => host.Tools.Add(new RecorderTools(host).Tool());

        static string Size(long bytes) => bytes >= 1 << 20 ? (bytes / (double)(1 << 20)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB" : (bytes / 1024.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB";

        ToolDef Tool() => new()
        {
            Name = "master_record",
            Title = "Record the Master",
            Description =
                "The Hard Disk Recorder: records the Master as it plays, in real time, to a WAV file (what the person hears, including live playing). " +
                "op: start (path; bits 16, 24, 32 or float, default 24; loop=true waits for the song loop's first tick, starts the song and stops after the " +
                "loop's last tick), stop (closes the file), status. It never overwrites: an existing path is refused unless overwrite=true. Needs studio mode " +
                "with an audio output open; for a render without playing, use export_audio. Example: master_record(op=\"start\", path=\"/tmp/take.wav\", loop=true).",
            Input = Object(null,
                Req("op", String("What to do", "start", "stop", "status")),
                Opt("path", String("start: the WAV file to write")),
                Opt("bits", String("start: sample format (default 24)", "16", "24", "32", "float")),
                Opt("loop", Boolean("start: record one pass of the song loop")),
                Opt("overwrite", Boolean("start: replace the file if it exists"))),
            Output = Object(null, Req("recording", Boolean("Recording now")),
                Opt("state", String("waiting_for_loop, recording or stopped")),
                Opt("path", String("The file")), Opt("seconds", Number("Length recorded")), Opt("bytes", Integer("Audio bytes written")),
                Opt("dropped_blocks", Integer("Blocks lost because the disk was too slow"))),
            Annotations = ToolAnnotations.Write,
            Handler = async c =>
            {
                string op = c.RequiredString("op");
                var r = await host.Engine("master_record", false, _ => Run(c, op), c.Cancellation);
                return ToolResult.Ok(r["recording"]!.GetValue<bool>()
                    ? $"Recording to {r["path"]}: {r["seconds"]} s, {Size(r["bytes"]!.GetValue<long>())}."
                    : r["path"] != null ? $"Saved {r["path"]}: {r["seconds"]} s, {Size(r["bytes"]!.GetValue<long>())}." + (r["dropped_blocks"]!.GetValue<int>() > 0 ? $" {r["dropped_blocks"]} blocks were lost (the disk was too slow)." : "")
                    : "Not recording.", r);
            },
        };

        JsonObject Report(MasterRecorder r)
        {
            var o = new JsonObject { ["recording"] = r.Active };
            o["state"] = r.Now switch { MasterRecorder.State.WaitingForLoop => "waiting_for_loop", MasterRecorder.State.Recording => "recording", _ => "stopped" };
            o["path"] = r.Path;
            o["seconds"] = Math.Round(r.Elapsed.TotalSeconds, 2);
            o["bytes"] = r.Bytes;
            o["dropped_blocks"] = r.Dropped;
            return o;
        }

        JsonObject Run(ToolContext c, string op)
        {
            var core = host.Core;
            // A loop recording that has run its pass is closed here, as the window does on its timer.
            if (recorder?.LoopFinished == true)
            {
                var done = recorder;
                done.Stop();
                recorder = null;
                if (op == "status" || op == "stop") return Report(done);
            }
            switch (op)
            {
                case "status":
                    return recorder != null ? Report(recorder) : new JsonObject { ["recording"] = false };
                case "stop":
                {
                    if (recorder == null) throw new ToolError("Not recording: master_record(op=\"start\", path=…) starts.");
                    var r = recorder;
                    r.Stop();
                    recorder = null;
                    return Report(r);
                }
                case "start":
                {
                    if (recorder != null) throw new ToolError($"Already recording to {recorder.Path}: master_record(op=\"stop\") ends it first.");
                    if (!OutputProbe(core))
                        throw new ToolError("No audio output is open, so there is nothing to record as it plays: audio_status shows the device and audio_set opens one. " +
                                            "export_audio renders the song to a file without playing it.");
                    string path = Path.GetFullPath((c.String("path") ?? throw new ToolError("start needs path (a .wav file).")).Trim().Trim('"'));
                    if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) throw new ToolError($"\"{path}\" isn't a .wav file; the recorder writes WAV only (export_audio has more formats).");
                    if (File.Exists(path) && c.Bool("overwrite") != true)
                        throw new ToolError($"{path} exists. Pick another path (\"{MasterRecorder.NextFree(path)}\" is free), or pass overwrite=true.");
                    var format = (c.String("bits") ?? "24") switch
                    {
                        "16" => WavFormat.Pcm16, "24" => WavFormat.Pcm24, "32" => WavFormat.Pcm32, "float" => WavFormat.Float32,
                        _ => throw new ToolError("bits: 16, 24, 32 or float."),
                    };
                    bool loop = c.Bool("loop") == true;
                    if (loop && core.SongCore.LoopEnd <= core.SongCore.LoopStart) throw new ToolError("loop=true needs a song loop: song_set(loop_start_beats=…, loop_end_beats=…).");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var r = new MasterRecorder(core, loop);
                    try { r.Start(path, format); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        r.Stop();
                        throw new ToolError($"Can't record to {path}: {e.Message}");
                    }
                    recorder = r;
                    return Report(r);
                }
                default:
                    throw new ToolError("op: start, stop or status.");
            }
        }
    }
}
