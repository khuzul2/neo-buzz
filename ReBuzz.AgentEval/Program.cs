// Evaluation (docs/specs/mcp-server.md §10, A6): the command line.
//
//   run      --exe NeoBuzzLive.exe [--out eval-out] [--baseline eval/baseline.json] [--update-baseline]
//            [--tolerance 0.05] [--only name,name] [--no-checks] [--no-tasks] [--verbose]
//            The scripted suite: every check, then every task's setup, reference solution and score.
//            Writes eval-results.json and eval-summary.md (and appends to $GITHUB_STEP_SUMMARY);
//            exits 1 when the gate (Results.cs) finds a regression.
//   prepare  --exe NeoBuzzLive.exe --task NAME [--out agent-eval]
//            A task's starting song, brief.md and mcp.json (the MCP config a client runs NeoBuzz Live with).
//   score    --exe NeoBuzzLive.exe --task NAME [--out agent-eval] [--song result.bmxml] [--transcript t.jsonl]
//            [--results agent-results.json]: scores the song an agent saved; appends to the results.
//   report   --results agent-results.json [--title …]: the results as a markdown table.
//   rebaseline --results eval-results.json [--baseline eval/baseline.json]
//   gate     --results agent-results.json [--baseline eval/agent-baseline.json] [--task-margin 0.34] [--mean-margin 0.05] [--update-baseline] [--model NAME]
//            The real-agent regression gate (AgentGate.cs): exits 1 when a task or the mean fell past its margin.
//   calls    --exe NeoBuzzLive.exe --file calls.txt [--max 1500]: runs "tool {json}" lines in one session and prints each answer
//            (for finding out what a tool answers when writing a task)
//   tasks    the task names; brief NAME [--out dir] prints a brief
//   selftest the client and the scorers on synthetic signals (no NeoBuzz Live; runs on Linux)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReBuzz.AgentEval
{
    internal static class Program
    {
        static int Main(string[] argv)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return argv.Length == 0 ? 2 : 0;
            }
            var a = new Args(argv.Skip(1));
            try
            {
                return argv[0] switch
                {
                    "run" => Run(a),
                    "prepare" => Prepare(a),
                    "score" => Score(a),
                    "report" => Report(a),
                    "rebaseline" => Rebaseline(a),
                    "tasks" => Tasks(),
                    "brief" => Brief(a),
                    "calls" => Calls(a),
                    "gate" => GateCommand(a),
                    "selftest" => SelfTest.Run(),
                    _ => Fail($"Unknown command \"{argv[0]}\".\n{Usage}"),
                };
            }
            catch (ArgumentException e)
            {
                return Fail(e.Message);
            }
            catch (TaskSkipped e)
            {
                Console.Error.WriteLine("Task skipped: " + e.Message);
                return 3;
            }
        }

        const string Usage =
            "ReBuzz.AgentEval: the MCP server's evaluation (docs/specs/mcp-server.md §10).\n" +
            "  run --exe NeoBuzzLive.exe [--out eval-out] [--baseline eval/baseline.json] [--update-baseline] [--tolerance 0.05] [--only a,b] [--no-checks] [--no-tasks] [--verbose]\n" +
            "  prepare --exe NeoBuzzLive.exe --task NAME [--out agent-eval]\n" +
            "  score --exe NeoBuzzLive.exe --task NAME [--out agent-eval] [--song FILE] [--transcript FILE] [--results FILE]\n" +
            "  report --results FILE [--title TEXT]\n" +
            "  rebaseline --results eval-out/eval-results.json [--baseline eval/baseline.json]\n" +
            "  gate --results FILE [--baseline eval/agent-baseline.json] [--task-margin N] [--mean-margin N] [--update-baseline] [--model NAME]\n" +
            "  calls --exe NeoBuzzLive.exe --file calls.txt [--max N]\n" +
            "  tasks | brief NAME [--out DIR] | selftest";

        static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return 2;
        }

        sealed class Args
        {
            readonly Dictionary<string, string> values = new();
            public readonly List<string> Positional = new();

            public Args(IEnumerable<string> args)
            {
                var list = args.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    if (!list[i].StartsWith("--")) { Positional.Add(list[i]); continue; }
                    string key = list[i][2..];
                    if (i + 1 < list.Count && !list[i + 1].StartsWith("--")) values[key] = list[++i];
                    else values[key] = "true";
                }
            }

            public string this[string key] => values.TryGetValue(key, out var v) ? v : null;
            public bool Flag(string key) => this[key] == "true";
            public string Required(string key) => this[key] ?? throw new ArgumentException($"--{key} is required.\n{Usage}");
        }

        static string Exe(Args a)
        {
            string exe = Path.GetFullPath(a.Required("exe"));
            if (!File.Exists(exe)) throw new ArgumentException($"No {Product.Name} at {exe}.");
            return exe;
        }

        static string Commit() => Environment.GetEnvironmentVariable("GITHUB_SHA") is string sha && sha.Length >= 7 ? sha[..7] : null;

        static void Summary(string markdown)
        {
            Console.WriteLine(markdown);
            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is string file && file.Length > 0)
                File.AppendAllText(file, markdown + "\n");
        }

        // ---- run ----------------------------------------------------------------------------------

        static int Run(Args a)
        {
            string exe = Exe(a);
            string outDir = Path.GetFullPath(a["out"] ?? "eval-out");
            string baselineFile = a["baseline"] ?? Path.Combine("eval", "baseline.json");
            double tolerance = a["tolerance"] is string t ? double.Parse(t, J.C) : 0.05;
            var only = a["only"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Wanted(string name) => only == null || only.Contains(name);
            Directory.CreateDirectory(outDir);

            var run = new EvalRun { Exe = exe, Commit = Commit() };
            var clock = Stopwatch.StartNew();
            using var runner = new Runner(exe, a.Flag("verbose"));
            // The second process for the cross-run check: started on the song, initialized by the check.
            var env = new Checks.Env { Folder = outDir, StartReBuzz = song => McpClient.Start(exe, new[] { "--agent", Path.GetFullPath(song) }) };
            if (!a.Flag("no-checks"))
                foreach (var def in Checks.All.Where(d => Wanted(d.Name)))
                    run.Checks.Add(runner.RunCheck(def, env));
            if (!a.Flag("no-tasks"))
                foreach (var task in EvalTask.All.Where(t => Wanted(t.Name)))
                    { if (runner.RunTask(task, outDir) is { } done) run.Tasks.Add(done); }
            runner.Close();
            run.Server = runner.ServerVersion;
            run.Problems.AddRange(runner.Problems);
            run.Seconds = clock.Elapsed.TotalSeconds;

            JsonObject baseline = null;
            if (File.Exists(baselineFile)) baseline = JsonNode.Parse(File.ReadAllText(baselineFile)) as JsonObject;
            if (baseline?["tolerance"] is JsonNode tol && a["tolerance"] == null) tolerance = J.Num(tol, tolerance);
            // A partial run (--only) is gated on what it ran.
            var gate = Gate.Check(run, baseline, tolerance);
            run.Write(Path.Combine(outDir, "eval-results.json"));
            string md = run.Markdown("Agent server evaluation (scripted)", gate);
            File.WriteAllText(Path.Combine(outDir, "eval-summary.md"), md);
            Summary(md);
            Annotate(run, gate, Baseline(run.ToJson(), tolerance));
            if (a.Flag("update-baseline"))
            {
                var b = Baseline(run.ToJson(), tolerance);
                File.WriteAllText(baselineFile, b.ToJsonString(EvalRun.Indented) + "\n");
                Runner.Log($"baseline written to {Path.GetFullPath(baselineFile)}");
                return 0;
            }
            return gate.Blocking.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// On GitHub Actions, the results as annotations too (readable through the API, unlike the
        /// step summary): notices with every check and task, an error per blocking finding.
        /// </summary>
        static void Annotate(EvalRun run, Gate gate, JsonObject baseline)
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return;
            static string Esc(string s) => s.Replace("%", "%25").Replace("\r", "").Replace("\n", "%0A");
            void Emit(string kind, string title, IEnumerable<string> lines, int max)
            {
                var chunk = new List<string>();
                int n = 0, size = 0;
                foreach (var line in lines)
                {
                    if (size + line.Length > 3500 && chunk.Count > 0)
                    {
                        if (n++ < max) Console.WriteLine($"::{kind} title={title}::{Esc(string.Join("\n", chunk))}");
                        chunk.Clear();
                        size = 0;
                    }
                    chunk.Add(line);
                    size += line.Length + 1;
                }
                if (chunk.Count > 0 && n < max) Console.WriteLine($"::{kind} title={title}::{Esc(string.Join("\n", chunk))}");
            }
            Emit("notice", "Agent evaluation checks", run.Checks.Select(c =>
                $"{c.Name}: {(c.Passed ? "pass" : "FAIL")} ({c.Calls} calls, {c.Seconds:F1} s)" +
                (c.Failures.Count > 0 ? " — " + string.Join(" | ", c.Failures.Take(6)) : "") + (c.Notes.Count > 0 ? " — " + string.Join(" | ", c.Notes) : "")), 3);
            Emit("notice", "Agent evaluation tasks", run.Tasks.Select(t =>
                $"{t.Name}: {t.Total:F2} {(t.Passed ? "pass" : "FAIL")} (start {t.StartTotal:F2}; {t.Calls} calls, {t.Errors} errors, {t.Seconds:F1} s)" +
                (t.Error != null ? " ERROR " + t.Error : "") + " — " + string.Join(" | ", t.Criteria.Select(c => $"{c.Name} {c.Score:F2}: {c.Detail}"))), 4);
            Emit("notice", "Agent evaluation totals", new[] { $"{run.Seconds:F0} s, server {run.Server}" }.Concat(run.Problems), 1);
            // The baseline this run would write (artifacts may be out of reach): commit it as eval/baseline.json to accept the scores.
            Console.WriteLine($"::notice title=Agent evaluation baseline::{Esc(baseline.ToJsonString())}");
            foreach (var b in gate.Blocking.Take(8)) Console.WriteLine($"::error title=Agent evaluation::{Esc(b)}");
        }

        /// <summary>A baseline from results: each check passed or not; each task's total, pass and criteria.</summary>
        static JsonObject Baseline(JsonObject results, double tolerance) => new()
        {
            ["about"] = "Scores of the scripted evaluation (ReBuzz.AgentEval run, docs/specs/mcp-server.md §10). A change that makes a passing check fail, " +
                        "or a task fail or drop by more than the tolerance, blocks. Rewrite: ReBuzz.AgentEval rebaseline --results eval-results.json (or run --update-baseline).",
            ["server"] = results["server"]?.DeepClone(),
            ["commit"] = results["commit"]?.DeepClone(),
            ["tolerance"] = tolerance,
            ["checks"] = new JsonObject(J.Objects(results["checks"]).Select(c => KeyValuePair.Create(J.Str(c["name"]), (JsonNode)J.Bool(c["passed"])))),
            ["tasks"] = new JsonObject(J.Objects(results["tasks"]).Select(t => KeyValuePair.Create(J.Str(t["name"]), (JsonNode)new JsonObject
            {
                ["total"] = J.Num(t["total"]),
                ["passed"] = J.Bool(t["passed"]),
                ["criteria"] = new JsonObject(J.Objects(t["criteria"]).Select(c => KeyValuePair.Create(J.Str(c["name"]), (JsonNode)J.Num(c["score"])))),
            }))),
        };

        static int Rebaseline(Args a)
        {
            var results = JsonNode.Parse(File.ReadAllText(a.Required("results"))) as JsonObject ?? throw new ArgumentException("Not a results file.");
            string file = a["baseline"] ?? Path.Combine("eval", "baseline.json");
            double tolerance = a["tolerance"] is string t ? double.Parse(t, J.C) : 0.05;
            File.WriteAllText(file, Baseline(results, tolerance).ToJsonString(EvalRun.Indented) + "\n");
            Console.WriteLine($"Wrote {Path.GetFullPath(file)}");
            return 0;
        }

        // ---- real-agent runs: prepare, score, report -------------------------------------------------

        static TaskContext Context(Args a, EvalTask task) => new(task.Name, Path.Combine(a["out"] ?? "agent-eval", task.Name));

        static int Prepare(Args a)
        {
            string exe = Exe(a);
            var task = EvalTask.Find(a.Required("task"));
            var ctx = Context(a, task);
            using (var runner = new Runner(exe, a.Flag("verbose")))
            {
                var s = runner.Session("prepare");
                s.New();
                task.Setup(s, ctx);
                s.Save(ctx.StartSong);
                runner.Close();
            }
            File.WriteAllText(ctx.File("brief.md"), task.Brief(ctx) + "\n");
            var mcp = new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [Product.McpName] = new JsonObject { ["type"] = "stdio", ["command"] = exe, ["args"] = J.A("--agent", ctx.StartSong) },
                },
            };
            File.WriteAllText(ctx.File("mcp.json"), mcp.ToJsonString(EvalRun.Indented) + "\n");
            Console.WriteLine($"Prepared {task.Name} in {ctx.Folder}: start.bmxml, brief.md, mcp.json");
            return 0;
        }

        static int Score(Args a)
        {
            string exe = Exe(a);
            var task = EvalTask.Find(a.Required("task"));
            var ctx = Context(a, task);
            string song = Path.GetFullPath(a["song"] ?? ctx.ResultSong);
            var clock = Stopwatch.StartNew();
            TaskResult result;
            string server;
            using (var runner = new Runner(exe, a.Flag("verbose")))
            {
                result = runner.ScoreSong(task, ctx, song);
                runner.Close();
                server = runner.ServerVersion;
            }
            if (a["transcript"] is string transcript && File.Exists(transcript)) Transcript.Read(transcript, result);
            Runner.Log($"{task.Name}: {result.Total:F2} {(result.Passed ? "pass" : "FAIL")} in {clock.Elapsed.TotalSeconds:F0} s");
            string resultsFile = a["results"] ?? Path.Combine(a["out"] ?? "agent-eval", "agent-results.json");
            var run = File.Exists(resultsFile) ? JsonNode.Parse(File.ReadAllText(resultsFile)) as JsonObject : null;
            run ??= new EvalRun { Kind = "agent", Server = server, Commit = Commit(), Exe = exe }.ToJson();
            (run["tasks"] as JsonArray)!.Add(result.ToJson());
            File.WriteAllText(resultsFile, run.ToJsonString(EvalRun.Indented) + "\n");
            File.WriteAllText(ctx.File("score.json"), result.ToJson().ToJsonString(EvalRun.Indented) + "\n");
            return 0;
        }

        static int Report(Args a)
        {
            var results = JsonNode.Parse(File.ReadAllText(a.Required("results"))) as JsonObject ?? throw new ArgumentException("Not a results file.");
            var md = new System.Text.StringBuilder();
            md.AppendLine($"## {a["title"] ?? "Agent server evaluation (real agent)"}").AppendLine();
            md.AppendLine("| Task | Total | Result | Start | Tool calls | Errors | Turns | Cost (USD) | Stopped | Criteria |").AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var t in J.Objects(results["tasks"]))
            {
                var agent = t["agent"] as JsonObject;
                string criteria = string.Join(", ", J.Objects(t["criteria"]).Select(c => $"{J.Str(c["name"])} {J.F(J.Num(c["score"]))}"));
                md.AppendLine($"| {J.Str(t["title"])} | {J.F(J.Num(t["total"]))} | {(J.Bool(t["passed"]) ? "pass" : "**FAIL**")} | {J.F(J.Num(t["start_total"]))} | " +
                              $"{J.Num(t["tool_calls"])} | {J.Num(t["error_calls"])} | {J.Str(agent?["turns"]) ?? ""} | {J.Str(agent?["cost_usd"]) ?? ""} | " +
                              $"{J.Str(agent?["stopped"]) ?? ""} | {criteria}{(t["error"] != null ? " — " + J.Str(t["error"]) : "")} |");
            }
            var errors = J.Objects(results["tasks"]).SelectMany(t => J.Objects(t["agent"]?["tool_errors"]).Select(e => (Task: J.Str(t["name"]), Tool: J.Str(e["tool"]), Text: J.Str(e["text"])))).ToList();
            if (errors.Count > 0)
            {
                md.AppendLine().AppendLine("Tools whose errors the agent hit (the candidates for clearer errors, §10):").AppendLine();
                foreach (var g in errors.GroupBy(e => e.Tool).OrderByDescending(g => g.Count()))
                    md.AppendLine($"- `{g.Key}` ×{g.Count()}: {g.First().Text.Replace("\n", " ")[..Math.Min(200, g.First().Text.Length)]}");
            }
            Summary(md.ToString());
            return 0;
        }

        static int GateCommand(Args a)
        {
            var results = JsonNode.Parse(File.ReadAllText(a.Required("results"))) as JsonObject ?? throw new ArgumentException("Not a results file.");
            string file = a["baseline"] ?? Path.Combine("eval", "agent-baseline.json");
            double taskMargin = a["task-margin"] is string tm ? double.Parse(tm, J.C) : AgentGate.TaskMargin;
            double meanMargin = a["mean-margin"] is string mm ? double.Parse(mm, J.C) : AgentGate.MeanMargin;
            JsonObject baseline = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
            if (baseline != null && a["task-margin"] == null) taskMargin = J.Num(baseline["task_margin"], taskMargin);
            if (baseline != null && a["mean-margin"] == null) meanMargin = J.Num(baseline["mean_margin"], meanMargin);
            var verdict = AgentGate.Check(results, baseline, taskMargin, meanMargin);
            Summary(AgentGate.Markdown(results, baseline, verdict));
            var fresh = AgentGate.Baseline(results, a["model"]);
            if (a.Flag("update-baseline"))
            {
                File.WriteAllText(file, fresh.ToJsonString(EvalRun.Indented) + "\n");
                Console.WriteLine($"baseline written to {Path.GetFullPath(file)}");
                return 0;
            }
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                static string Esc(string s) => s.Replace("%", "%25").Replace("\r", "").Replace("\n", "%0A");
                // The baseline this run would write (artifacts may be out of reach): commit it as eval/agent-baseline.json to accept the scores.
                Console.WriteLine($"::notice title=Agent evaluation baseline (real agent)::{Esc(fresh.ToJsonString())}");
                foreach (var b in verdict.Blocking.Take(8)) Console.WriteLine($"::error title=Agent evaluation gate::{Esc(b)}");
            }
            return verdict.Blocking.Count == 0 ? 0 : 1;
        }

        static int Calls(Args a)
        {
            string exe = Exe(a);
            int max = a["max"] is string m ? int.Parse(m, J.C) : 1500;
            using var runner = new Runner(exe, false);
            var s = runner.Session("calls");
            foreach (var raw in File.ReadLines(a.Required("file")))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int sp = line.IndexOf(' ');
                string tool = sp < 0 ? line : line[..sp];
                var args = sp < 0 ? new JsonObject() : JsonNode.Parse(line[sp..]) as JsonObject;
                var call = s.C.Call(tool, args);
                string text = call.IsError ? call.Text : call.Data.ToJsonString();
                Console.WriteLine($"> {line}\n{(call.IsError ? "ERROR " : "")}{(text.Length > max ? text[..max] + "…" : text)}\n");
            }
            runner.Close();
            return 0;
        }

        static int Tasks()
        {
            foreach (var t in EvalTask.All) Console.WriteLine(t.Name);
            return 0;
        }

        static int Brief(Args a)
        {
            var task = EvalTask.Find(a.Positional.FirstOrDefault() ?? a.Required("task"));
            Console.WriteLine(task.Brief(Context(a, task)));
            return 0;
        }
    }
}
