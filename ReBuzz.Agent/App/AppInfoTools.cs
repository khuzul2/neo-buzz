// Agent server (docs/specs/agent-knowledge.md §5, K3): the app itself. app_status (version, mode, CPU,
// memory) and song_recent (the recently opened songs), both read-only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.App
{
    internal static class AppInfoTools
    {
        /// <summary>How long app_status samples the CPU (ms).</summary>
        internal static int CpuSampleMs = 300;

        public static void Register(ToolRegistry tools, IAppServices app)
        {
            tools.Add(new ToolDef
            {
                Name = "app_status",
                Title = "App status",
                Description =
                    "The app itself: its version, mode (studio, live or headless), platform, how long it has run, the CPU it uses (percent of all cores, sampled over a third of a second) and memory " +
                    "(working set and managed heap), plus the song's name and whether it plays. studio_status has the song and the audio output; this one is for load and identity. Example: app_status().",
                Input = Object(null),
                Output = Object(null,
                    Req("product", String("The app's name")),
                    Req("version", String("The agent server's version")),
                    Req("mode", String("studio, live or headless", "studio", "live", "headless")),
                    Req("platform", String("Operating system and architecture")),
                    Req("uptime_seconds", Number("Since the process started")),
                    Req("cpu", Object(null, Req("process_percent", Number("Of all cores")), Req("cores", Integer("Logical cores")), Req("busy_cores", Number("The same as cores used")))),
                    Req("memory", Object(null, Req("working_set_mb", Number("Resident memory")), Req("gc_heap_mb", Number("The managed heap")), Req("threads", Integer("Threads")))),
                    Req("tools", Integer("How many tools this server has")),
                    Req("song", Object(null, Opt("file", String("The song's file")), Req("unsaved_changes", Boolean("Changes since the last save")), Req("tempo_bpm", Number("Tempo")), Req("playing", Boolean("The transport plays"))))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var p = Process.GetCurrentProcess();
                    var t0 = p.TotalProcessorTime;
                    var clock = Stopwatch.StartNew();
                    await Task.Delay(CpuSampleMs, c.Cancellation);
                    p.Refresh();
                    var t1 = p.TotalProcessorTime;
                    double wall = Math.Max(0.001, clock.Elapsed.TotalSeconds);
                    int cores = Math.Max(1, Environment.ProcessorCount);
                    double busy = Math.Round((t1 - t0).TotalSeconds / wall, 2);
                    var facts = await app.Engine("app_status", false, app.Facts, c.Cancellation);
                    double ws = Math.Round(p.WorkingSet64 / 1048576.0, 1), heap = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1);
                    var song = new JsonObject { ["unsaved_changes"] = facts.Unsaved, ["tempo_bpm"] = facts.TempoBpm, ["playing"] = facts.Playing };
                    if (facts.Song != null) song["file"] = facts.Song;
                    var result = new JsonObject
                    {
                        ["product"] = Product.Name,
                        ["version"] = facts.Version,
                        ["mode"] = facts.Mode,
                        ["platform"] = facts.Platform,
                        ["uptime_seconds"] = Math.Round((DateTime.Now - p.StartTime).TotalSeconds, 1),
                        ["cpu"] = new JsonObject { ["process_percent"] = Math.Round(busy / cores * 100, 1), ["cores"] = cores, ["busy_cores"] = busy },
                        ["memory"] = new JsonObject { ["working_set_mb"] = ws, ["gc_heap_mb"] = heap, ["threads"] = p.Threads.Count },
                        ["tools"] = facts.Tools,
                        ["song"] = song,
                    };
                    return ToolResult.Ok($"{Product.Name} {facts.Version}, {facts.Mode} mode on {facts.Platform}; CPU {result["cpu"]!["process_percent"]}% of {cores} cores, {ws} MB in memory; " +
                                         $"song {facts.Song ?? "untitled"}{(facts.Unsaved ? " (unsaved changes)" : "")}, {facts.TempoBpm} BPM{(facts.Playing ? ", playing" : "")}.", result);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "song_recent",
                Title = "Recent songs",
                Description =
                    "The songs opened lately, newest first, each with its full path, whether the file is still there and when it was last saved. Open one with song_open(path=…). Example: song_recent(limit=5).",
                Input = Object(null, Opt("limit", Integer("At most this many (default 10)", 1, 50))),
                Output = Object(null, Req("songs", Array(Object(null,
                    Req("path", String("Full path")), Req("name", String("File name")), Req("exists", Boolean("The file is there")),
                    Opt("modified", String("Last saved (UTC, ISO 8601)"))), "Newest first"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    int limit = c.Int("limit") ?? 10;
                    var recent = await app.Engine("song_recent", false, () => app.RecentSongs().Take(limit).ToList(), c.Cancellation);
                    var rows = new JsonArray();
                    foreach (var path in recent)
                    {
                        bool exists = File.Exists(path);
                        var o = new JsonObject { ["path"] = path, ["name"] = Path.GetFileName(path), ["exists"] = exists };
                        if (exists) o["modified"] = File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
                        rows.Add(o);
                    }
                    return ToolResult.Ok(recent.Count == 0 ? "No recent songs." : $"{recent.Count} recent song{(recent.Count == 1 ? "" : "s")}: " + string.Join("; ", recent.Take(5).Select(Path.GetFileName)) + (recent.Count > 5 ? "; …" : "") + ".",
                        new JsonObject { ["songs"] = rows });
                },
            });
        }
    }
}
