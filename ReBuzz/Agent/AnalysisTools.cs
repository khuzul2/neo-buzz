// Agent server (docs/specs/mcp-server.md §5.7, A2): real ears. analyze_mix (a render with a probe
// on every machine feeding the mix, then levels, bands, masking and the biggest problems),
// compare (two renders, a render and a reference, before and after), plot (an image the agent
// looks at).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class AnalysisTools
    {
        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "analyze_mix",
                Title = "Analyze the mix",
                Description =
                    "Renders the song (or a range or section) with a probe on every machine feeding Master (or the machines named), then reports " +
                    "per track: loudness, peak, crest and bands; which pairs mask each other in which bands and how much; the master's headroom; " +
                    "and the biggest problems, worst first. Use it before and after mix moves. Example: analyze_mix(section=\"Chorus\").",
                Input = Object(null,
                    Opt("from", AnyOf("Start", Number("Beats", 0), String("\"bar.beat\""))),
                    Opt("to", AnyOf("End", Number("Beats", 0), String("\"bar.beat\""))),
                    Opt("section", String("A marker's name: its section")),
                    Opt("machines", Array(String("A machine"), "Tracks to probe (default: everything connected to Master)"))),
                Output = Object(null,
                    Req("render", String("The render id (its probes are \"id:Machine\")")),
                    Req("problems", Array(Object(null, Req("kind", String("What")), Req("text", String("The problem and a next step"))), "Worst first"))),
                Annotations = new ToolAnnotations(ReadOnly: true),
                Handler = async c =>
                {
                    var names = c.List("machines")?.Select(x => x!.GetValue<string>()).ToList();
                    if (names == null)
                        names = await host.Engine("analyze_mix", false, _ =>
                        {
                            var master = SongModel.Machines(core).First(SongModel.IsMaster);
                            return master.Inputs.Select(i => i.Source).OfType<MachineCore>().Where(m => !m.Hidden).Select(m => m.Name).Distinct().ToList();
                        }, c.Cancellation);
                    if (names.Count == 0) throw new ToolError("Nothing is connected to Master, so there's no mix to analyze.");
                    var args = new JsonObject { ["probes"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()), ["tail_seconds"] = 1, ["name"] = "mix" };
                    foreach (var k in new[] { "from", "to", "section" }) if (c.Arguments[k] != null) args[k] = c.Arguments[k]!.DeepClone();
                    var rendered = await host.Server.Tools.Find("render")!.Handler(new ToolContext("render", args, c.Cancellation));
                    if (rendered.IsError) return rendered;
                    var r = rendered.Structured!;
                    var master = Wav.Read(r["file"]!.GetValue<string>());
                    var tracks = r["probes"]!.AsArray().Select(p => (p!["machine"]!.GetValue<string>(), Wav.Read(p["file"]!.GetValue<string>()))).ToList();
                    var report = await System.Threading.Tasks.Task.Run(() => AudioReport.MixReport(tracks, master), c.Cancellation);
                    report["render"] = r["id"]!.GetValue<string>();
                    var problems = report["problems"] as JsonArray ?? new JsonArray();
                    string head = $"Mix of {tracks.Count} tracks ({r["id"]}): master {AudioReport.Line(report["master"]!.AsObject())}";
                    if (report["headroom_db"] != null) head += $", headroom {report["headroom_db"]} dB";
                    return ToolResult.Ok(head + ". " + (problems.Count == 0 ? "No problems found." :
                        "Problems: " + string.Join(" ", problems.Select((p, i) => $"{i + 1}. {p!["text"]}"))), report);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "compare",
                Title = "Compare two sounds",
                Description =
                    "Compares two renders, probes or WAV files (before/after a change, your mix against a reference track): loudness, peaks, " +
                    "dynamics, stereo and bands, each as a, b and the difference (b − a), with the bands also level-matched so tone differences show. " +
                    "Read-only. Example: compare(a=\"r2\", b=\"C:/Refs/reference.wav\").",
                Input = Object(null,
                    Req("a", String("A render id, probe id or WAV path")),
                    Req("b", String("Another")),
                    Opt("from_seconds", Number("Compare from here (both)", 0)),
                    Opt("to_seconds", Number("To here (both)", 0))),
                Output = Object(null, Req("reads", Array(String("A plain-language difference"), "What changed"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var (la, _, a) = await RenderTools.Load(host, c.RequiredString("a"), c);
                    var (lb, _, b) = await RenderTools.Load(host, c.RequiredString("b"), c);
                    double? from = c.Number("from_seconds"), to = c.Number("to_seconds");
                    a = AudioReport.Slice(a, from, to);
                    b = AudioReport.Slice(b, from, to);
                    var r = await System.Threading.Tasks.Task.Run(() => AudioReport.Compare(a, b, la, lb), c.Cancellation);
                    var reads = r["reads"] as JsonArray ?? new JsonArray();
                    return ToolResult.Ok($"{la} → {lb}: " + (reads.Count == 0 ? "no notable differences." : string.Join(" ", reads.Select(x => x!.GetValue<string>()))), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "plot",
                Title = "Plot audio",
                Description =
                    "Draws an image you can look at: spectrum (one or more sources overlaid), spectrogram, waveform, loudness over time, goniometer " +
                    "(the stereo field) or bands (per source). spectrum overlays several sources and bands groups them; the rest draw one. Sources are render ids, probe ids or WAV paths. Returns a PNG image and its file. " +
                    "Example: plot(kind=\"spectrum\", sources=[\"r3:Bass\", \"r3:Kick\"]).",
                Input = Object(null,
                    Req("kind", String("What to draw", AudioPlot.Kinds)),
                    Req("sources", Array(String("A render id, probe id or WAV path"), "What to draw it of", 1)),
                    Opt("title", String("A title")),
                    Opt("from_seconds", Number("From here", 0)),
                    Opt("to_seconds", Number("To here", 0))),
                Output = Object(null, Req("file", String("The PNG file")), Req("kind", String("What was drawn"))),
                Annotations = new ToolAnnotations(ReadOnly: true),
                Handler = async c =>
                {
                    string kind = c.RequiredString("kind");
                    var list = new List<(string, StereoAudio)>();
                    foreach (var s in c.List("sources")!)
                    {
                        var (label, _, audio) = await RenderTools.Load(host, s!.GetValue<string>(), c);
                        list.Add((label, AudioReport.Slice(audio, c.Number("from_seconds"), c.Number("to_seconds"))));
                    }
                    var notes = new List<string>();
                    if (kind is not ("spectrum" or "bands") && list.Count > 1)
                    {
                        notes.Add($"A {kind} plot shows one source; drew {list[0].Item1}.");
                        list = list.Take(1).ToList();
                    }
                    string title = c.String("title") ?? $"{kind}: {string.Join(", ", list.Select(x => x.Item1))}";
                    var png = await System.Threading.Tasks.Task.Run(() => AudioPlot.Draw(kind, list, title), c.Cancellation);
                    string folder = Path.Combine(Path.GetDirectoryName(host.Log.FilePath) ?? Path.GetTempPath(), "plots");
                    Directory.CreateDirectory(folder);
                    string file = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(host.Log.FilePath)}-{kind}-{Guid.NewGuid().ToString("N")[..6]}.png");
                    File.WriteAllBytes(file, png);
                    return ToolResult.Ok($"{title} → {file}" + (notes.Count > 0 ? " " + string.Join(" ", notes) : ""), new JsonObject { ["file"] = file, ["kind"] = kind, ["sources"] = new JsonArray(list.Select(x => (JsonNode)x.Item1).ToArray()) })
                        .WithImage(png);
                },
            });
        }
    }
}
