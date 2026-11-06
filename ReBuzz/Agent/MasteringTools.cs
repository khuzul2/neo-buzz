// Agent server (docs/specs/mastering-tools.md §6): the assistant's mastering tools, deterministic
// measurements an agent can explain move by move. target_build averages the user's own reference
// recordings into a style target (a file, so one target serves every song); eq_fit turns the tonal
// difference between a master and its target (a style target, a reference, or a curve) into a few Neo
// EQ bands, and with a machine, writes them into that Neo EQ as one undo step. analyze(target=…)
// (RenderTools) measures against a target with the same resolution (Target).

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class MasteringTools
    {
        /// <summary>Where target_build saves targets unless given a file: beside the agent's log, in "targets".</summary>
        static string Folder(AgentHost host) =>
            Path.Combine(Path.GetDirectoryName(host.Log.FilePath) ?? Path.GetTempPath(), "targets");

        static string Slug(string name) => new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-');

        /// <summary>
        /// A target from what an agent gave: a target file (*.json), a target built in this session or saved by name, else a
        /// reference measured on the fly (a render id, a probe or a WAV path).
        /// </summary>
        internal static async System.Threading.Tasks.Task<TargetHandle> Target(AgentHost host, string spec, ToolContext c)
        {
            spec = spec.Trim().Trim('"');
            if (spec.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                string file = Path.GetFullPath(spec);
                if (!File.Exists(file)) throw new ToolError($"No target file {file}.");
                try { return MasteringReport.LoadTarget(file); }
                catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { throw new ToolError($"{file}: {e.Message}"); }
            }
            if (host.Targets.TryGetValue(spec, out var built)) return built;
            string saved = Path.Combine(Folder(host), Slug(spec) + MasteringReport.TargetExtension);
            if (File.Exists(saved)) return MasteringReport.LoadTarget(saved);
            bool audio = host.Renders.ContainsKey(spec) || spec.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(spec, @"^r\d+(:.+)?$");
            if (!audio)
                throw new ToolError($"No target \"{spec}\": give a target file (*.target.json), a name target_build saved, a render id or a WAV file." +
                                    (host.Targets.IsEmpty ? "" : $" Built in this session: {string.Join(", ", host.Targets.Keys)}."));
            var (label, _, a) = await RenderTools.Load(host, spec, c);
            return await System.Threading.Tasks.Task.Run(() => MasteringReport.TargetFrom(label, a), c.Cancellation);
        }

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "target_build",
                Title = "Build a style target",
                Description =
                    "Measures the user's own reference recordings (WAV files or renders) and averages them into a style target: the long-term tonal " +
                    "curve (1/6 octave, each moved to the same level), integrated loudness, loudness range, PLR, the side/mid ratio and the " +
                    "microdynamics (LDR) in four bands (split at 120 Hz, 2 kHz, 10 kHz), each with the references' spread. Saved as a file " +
                    "(targets/<name>.target.json beside the agent's log, or file), so every song can use it: analyze(target=name) and eq_fit(target=name). " +
                    "Use references in the style the user wants, mastered, a few minutes each. Example: target_build(name=\"deep house\", " +
                    "sources=[\"C:/Refs/a.wav\", \"C:/Refs/b.wav\"]).",
                Input = Object(null,
                    Req("name", String("The target's name")),
                    Req("sources", Array(String("A WAV file's full path, a render id or a probe"), "The references (1 to 20)", 1)),
                    Opt("file", String("Where to save it (a .target.json path); default: targets/<name>.target.json beside the agent's log"))),
                Output = Object(null,
                    Req("name", String("The target")),
                    Req("file", String("Where it was saved")),
                    Req("target", Object("The target as saved"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    string name = c.RequiredString("name").Trim();
                    if (name.Length == 0) throw new ToolError("name: give the target a name.");
                    var sources = c.List("sources")!.Select(x => x!.GetValue<string>()).ToList();
                    if (sources.Count > 20) throw new ToolError("sources: at most 20 references.");
                    var audio = new System.Collections.Generic.List<(string, StereoAudio)>();
                    foreach (var s in sources)
                    {
                        var (label, _, a) = await RenderTools.Load(host, s, c);
                        if (a.Seconds < 3) throw new ToolError($"{label} is {a.Seconds:F1} s long: a reference needs at least 3 s (better, a whole track).");
                        audio.Add((label, a));
                    }
                    var target = await System.Threading.Tasks.Task.Run(() => MasteringReport.Build(name, audio), c.Cancellation);
                    string file = c.String("file") is string f && f.Trim().Length > 0
                        ? Path.GetFullPath(f.Trim().Trim('"'))
                        : Path.Combine(Folder(host), Slug(name) + MasteringReport.TargetExtension);
                    if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) file += MasteringReport.TargetExtension;
                    target.Save(file);
                    host.Targets[name] = target;
                    var r = new JsonObject { ["name"] = name, ["file"] = file, ["target"] = target.ToJson() };
                    return ToolResult.Ok($"Target {MasteringReport.Describe(target)}. Saved to {file}. Measure against it with analyze(source=…, target=\"{name}\"); match its tone with eq_fit(source=…, target=\"{name}\").", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "eq_fit",
                Title = "Fit EQ bands to a target",
                Description =
                    "Turns a tonal difference into a few Neo EQ bands (bells and shelves, the machine's own response): the difference between a " +
                    "source (a render or WAV) and a target (a style target from target_build, a reference render or WAV, or a *.target.json), " +
                    "level-matched and smoothed over a third of an octave, each point clipped to ±limit_db; or a curve you give (hz and db " +
                    "wanted, positive boosts). Least squares on Neo EQ's design, a band at a time, then all together. Returns the bands, " +
                    "how much of the difference they take (RMS before and after) and the param_set changes for a Neo EQ; with machine, writes " +
                    "them into that Neo EQ (bands B2–B7, the leftovers at 0 dB; B1 and B8, the cuts, untouched) as one undo step. Render and " +
                    "compare after. Example: eq_fit(source=\"r4\", target=\"deep house\", bands=4, machine=\"M EQ\").",
                Input = Object(null,
                    Opt("source", String("The master to correct: a render id, probe or WAV path (with target)")),
                    Opt("target", String("A style target name, a *.target.json, or a reference render or WAV")),
                    Opt("curve", Array(Object("A point", Req("hz", Number("Frequency", 20, 20000)), Req("db", Number("The change wanted there, dB (positive: boost)"))), "Instead of source and target: the change wanted", 3)),
                    Opt("bands", Integer("Bands to use (1–6, default 4)", 1, 6)),
                    Opt("max_gain_db", Number("Largest gain per band, dB (default 4)", 0.5, 12)),
                    Opt("limit_db", Number("Clip the wanted change to ±this before fitting, dB (default 3: the mastering guide's rule)", 0.5, 12)),
                    Opt("machine", String("A Neo EQ to write the bands into (one undo step)"))),
                Output = Object(null,
                    Req("bands", Array(Object(null, Req("type", String("Bell, Low shelf or High shelf")), Req("freq_hz", Number("Frequency")), Req("gain_db", Number("Gain")), Req("q", Number("Q"))), "The fitted bands, low to high")),
                    Req("rms_before_db", Number("The wanted change's RMS, dB")),
                    Req("rms_after_db", Number("What the bands leave, dB RMS")),
                    Req("changes", Array(Object("A param_set change"), "For a Neo EQ's B2–B7")),
                    Opt("applied", String("The Neo EQ written into"))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    int bands = c.Int("bands") ?? 4;
                    double maxGain = c.Number("max_gain_db") ?? 4, limit = c.Number("limit_db") ?? 3;
                    double rate = ReBuzzCore.masterInfo?.SamplesPerSec > 0 ? ReBuzzCore.masterInfo.SamplesPerSec : 48000;
                    JsonObject fit;
                    string what;
                    if (c.List("curve") is JsonArray curve)
                    {
                        if (c.String("source") != null || c.String("target") != null) throw new ToolError("Give either curve, or source and target.");
                        var pts = curve.Select(p => (Hz: p!["hz"]!.GetValue<double>(), Db: p["db"]!.GetValue<double>())).OrderBy(p => p.Hz).ToList();
                        // The curve read at six points an octave (straight lines in log frequency between the points given).
                        var hz = Enumerable.Range(0, 60).Select(i => 20 * Math.Pow(2, i / 6.0)).Where(f => f <= 20000).ToArray();
                        var db = hz.Select(f => Interpolate(pts, f)).ToArray();
                        fit = await System.Threading.Tasks.Task.Run(() => MasteringReport.FitEq(hz, db, bands, maxGain, limit, rate), c.Cancellation);
                        what = "the curve given";
                    }
                    else
                    {
                        string source = c.String("source") ?? throw new ToolError("Give source and target (or a curve).");
                        string targetSpec = c.String("target") ?? throw new ToolError("Give a target: a style target name or file, or a reference render or WAV.");
                        var (label, _, audio) = await RenderTools.Load(host, source, c);
                        var target = await Target(host, targetSpec, c);
                        rate = audio.Rate > 0 ? audio.Rate : rate;
                        fit = await System.Threading.Tasks.Task.Run(() => MasteringReport.FitEq(audio, target, bands, maxGain, limit, rate), c.Cancellation);
                        what = $"{label} → {target.Name}";
                    }
                    var fitted = fit["bands"]!.AsArray();
                    var changes = MasteringReport.NeoEqChanges(fitted);
                    fit["changes"] = changes;
                    string line = fitted.Count == 0 ? "no band helps (the difference is already small or flat)" :
                        string.Join("; ", fitted.Select(b => $"{b!["type"]} {b["freq_hz"]} Hz {((double)b["gain_db"]!).ToString("+0.0;-0.0", System.Globalization.CultureInfo.InvariantCulture)} dB" +
                                                            ((string)b["type"] == "Bell" ? $" Q {b["q"]}" : "")));
                    string text = $"eq_fit ({what}, ±{limit} dB): {line}. Wanted {fit["rms_before_db"]} dB RMS, left {fit["rms_after_db"]} dB.";
                    if (c.String("machine") is string machine)
                    {
                        string gear = await host.Engine("eq_fit", false, _ => SongModel.Machine(core, machine).DLL?.Name, c.Cancellation);
                        if (gear != "Neo EQ") throw new ToolError($"{machine} is {gear ?? "not a machine"}: eq_fit writes into a Neo EQ (its bands B2–B7).");
                        var set = await host.Server.Tools.Find("param_set")!.Handler(new ToolContext("param_set",
                            new JsonObject { ["machine"] = machine, ["changes"] = changes.DeepClone() }, c.Cancellation));
                        if (set.IsError) return set;
                        fit["applied"] = machine;
                        text += $" Written into {machine} (B2–B{Math.Max(2, fitted.Count + 1)}; one undo step).";
                    }
                    else text += " To apply: param_set(machine=<a Neo EQ>, changes=<changes>), or call again with machine.";
                    return ToolResult.Ok(text, fit);
                },
            });
        }

        static double Interpolate(System.Collections.Generic.List<(double Hz, double Db)> pts, double hz)
        {
            if (hz <= pts[0].Hz) return pts[0].Db;
            if (hz >= pts[^1].Hz) return pts[^1].Db;
            int i = 0;
            while (pts[i + 1].Hz < hz) i++;
            double t = Math.Log(hz / pts[i].Hz) / Math.Log(pts[i + 1].Hz / pts[i].Hz);
            return pts[i].Db + (pts[i + 1].Db - pts[i].Db) * t;
        }
    }
}
