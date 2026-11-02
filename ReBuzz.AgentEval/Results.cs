// Evaluation: what a run records (per check and per task: scores per criterion, total, tool calls,
// error calls, wall time), eval-results.json, the markdown summary, and the regression gate
// against eval/baseline.json.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReBuzz.AgentEval
{
    internal sealed class CheckResult
    {
        public string Name, Title;
        public bool Passed;
        public List<string> Failures = new();
        public List<string> Notes = new();
        public int Calls, Errors;
        public double Seconds;

        public JsonObject ToJson() => new()
        {
            ["name"] = Name,
            ["title"] = Title,
            ["passed"] = Passed,
            ["failures"] = J.Strings(Failures),
            ["notes"] = J.Strings(Notes),
            ["tool_calls"] = Calls,
            ["error_calls"] = Errors,
            ["seconds"] = Math.Round(Seconds, 2),
        };
    }

    internal sealed class Criterion
    {
        public Criterion(string name, double score, string detail)
        {
            Name = name;
            Score = double.IsNaN(score) ? 0 : Math.Clamp(score, 0, 1);
            Detail = detail;
        }

        public string Name { get; }
        /// <summary>0 … 1.</summary>
        public double Score { get; }
        /// <summary>What was measured, in numbers.</summary>
        public string Detail { get; }

        public JsonObject ToJson() => new() { ["name"] = Name, ["score"] = Math.Round(Score, 3), ["detail"] = Detail };
    }

    internal sealed class TaskResult
    {
        /// <summary>A task passes with a total of at least this and no criterion below <see cref="CriterionFloor"/>.</summary>
        public const double PassTotal = 0.85, CriterionFloor = 0.5;

        public string Name, Title;
        /// <summary>"reference" (the scripted solution), "agent" (a real agent's song) or "start" (the song before any work).</summary>
        public string Solver;
        public List<Criterion> Criteria = new();
        public double Total => Criteria.Count == 0 ? 0 : Criteria.Average(c => c.Score);
        public bool Passed => Error == null && Criteria.Count > 0 && Total >= PassTotal && Criteria.All(c => c.Score >= CriterionFloor);
        /// <summary>The same scorer on the starting song: it must not pass, or the task measures nothing.</summary>
        public double? StartTotal;
        public bool? StartPassed;
        public int Calls, Errors;
        public double Seconds;
        public string Error;
        public JsonObject Facts, StartFacts;
        public string Song;
        /// <summary>For real-agent runs: the agent's own record (turns, cost, how it stopped).</summary>
        public JsonObject Agent;

        public JsonObject ToJson()
        {
            var o = new JsonObject
            {
                ["name"] = Name,
                ["title"] = Title,
                ["solver"] = Solver,
                ["total"] = Math.Round(Total, 3),
                ["passed"] = Passed,
                ["criteria"] = new JsonArray(Criteria.Select(c => (JsonNode)c.ToJson()).ToArray()),
                ["tool_calls"] = Calls,
                ["error_calls"] = Errors,
                ["seconds"] = Math.Round(Seconds, 2),
            };
            if (StartTotal.HasValue) o["start_total"] = Math.Round(StartTotal.Value, 3);
            if (StartPassed.HasValue) o["start_passed"] = StartPassed.Value;
            if (Error != null) o["error"] = Error;
            if (Song != null) o["song"] = Song;
            if (Facts != null) o["facts"] = Facts.DeepClone();
            if (StartFacts != null) o["start_facts"] = StartFacts.DeepClone();
            if (Agent != null) o["agent"] = Agent.DeepClone();
            return o;
        }
    }

    internal sealed class EvalRun
    {
        public string Kind = "scripted";
        public string Server, Commit, Exe;
        public DateTime Started = DateTime.UtcNow;
        public double Seconds;
        public List<CheckResult> Checks = new();
        public List<TaskResult> Tasks = new();
        public List<string> Problems = new();

        public JsonObject ToJson() => new()
        {
            ["kind"] = Kind,
            ["server"] = Server,
            ["commit"] = Commit,
            ["exe"] = Exe,
            ["started"] = Started.ToString("o"),
            ["seconds"] = Math.Round(Seconds, 1),
            ["checks"] = new JsonArray(Checks.Select(c => (JsonNode)c.ToJson()).ToArray()),
            ["tasks"] = new JsonArray(Tasks.Select(t => (JsonNode)t.ToJson()).ToArray()),
            ["problems"] = J.Strings(Problems),
            ["totals"] = new JsonObject
            {
                ["checks_passed"] = Checks.Count(c => c.Passed),
                ["checks"] = Checks.Count,
                ["tasks_passed"] = Tasks.Count(t => t.Passed),
                ["tasks"] = Tasks.Count,
                ["tool_calls"] = Checks.Sum(c => c.Calls) + Tasks.Sum(t => t.Calls),
                ["error_calls"] = Checks.Sum(c => c.Errors) + Tasks.Sum(t => t.Errors),
            },
        };

        public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public void Write(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, ToJson().ToJsonString(Indented) + "\n");
        }

        /// <summary>The run as markdown: checks, tasks (with each criterion), the gate's verdicts.</summary>
        public string Markdown(string title, Gate gate = null)
        {
            var md = new StringBuilder();
            md.AppendLine($"## {title}").AppendLine();
            md.AppendLine($"Server {Server ?? "?"}; {Math.Round(Seconds)} s; " +
                          $"{Checks.Count(c => c.Passed)}/{Checks.Count} checks, {Tasks.Count(t => t.Passed)}/{Tasks.Count} tasks passed; " +
                          $"{Checks.Sum(c => c.Calls) + Tasks.Sum(t => t.Calls)} tool calls, {Checks.Sum(c => c.Errors) + Tasks.Sum(t => t.Errors)} with isError.").AppendLine();
            if (Checks.Count > 0)
            {
                md.AppendLine("| Check | Result | Calls | Errors | Seconds | Notes |").AppendLine("|---|---|---|---|---|---|");
                foreach (var c in Checks)
                    md.AppendLine($"| {c.Title ?? c.Name} | {(c.Passed ? "pass" : "**FAIL**")} | {c.Calls} | {c.Errors} | {c.Seconds:F1} | {Cell(string.Join("; ", c.Failures.Concat(c.Notes)))} |");
                md.AppendLine();
            }
            if (Tasks.Count > 0)
            {
                md.AppendLine("| Task | Solver | Total | Result | Start total | Calls | Errors | Seconds |").AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (var t in Tasks)
                    md.AppendLine($"| {t.Title ?? t.Name} | {t.Solver} | {t.Total:F2} | {(t.Passed ? "pass" : "**FAIL**")} | " +
                                  $"{(t.StartTotal.HasValue ? t.StartTotal.Value.ToString("F2", J.C) : "")} | {t.Calls} | {t.Errors} | {t.Seconds:F1} |");
                md.AppendLine();
                foreach (var t in Tasks)
                {
                    md.AppendLine($"<details><summary>{t.Title ?? t.Name}: {t.Total:F2}</summary>").AppendLine();
                    if (t.Error != null) md.AppendLine($"Error: {Cell(t.Error)}").AppendLine();
                    md.AppendLine("| Criterion | Score | Measured |").AppendLine("|---|---|---|");
                    foreach (var c in t.Criteria) md.AppendLine($"| {c.Name} | {c.Score:F2} | {Cell(c.Detail)} |");
                    md.AppendLine().AppendLine("</details>").AppendLine();
                }
            }
            if (gate != null)
            {
                if (gate.Blocking.Count > 0)
                {
                    md.AppendLine("**Blocking:**").AppendLine();
                    foreach (var b in gate.Blocking) md.AppendLine($"- {b}");
                    md.AppendLine();
                }
                if (gate.Info.Count > 0)
                {
                    foreach (var b in gate.Info) md.AppendLine($"- {b}");
                    md.AppendLine();
                }
                if (gate.Blocking.Count == 0) md.AppendLine(gate.HadBaseline ? "No regressions against eval/baseline.json." : "No baseline to compare with.").AppendLine();
            }
            foreach (var p in Problems) md.AppendLine($"- {p}");
            return md.ToString();
        }

        static string Cell(string s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }

    /// <summary>
    /// The regression gate. Blocking: a check that passed in the baseline fails; a task that passed
    /// fails, or its total drops by more than the tolerance; a check or task without a baseline entry
    /// fails; a starting song passes its task's scorer. Known failures (failing in the baseline too)
    /// are reported but don't block.
    /// </summary>
    internal sealed class Gate
    {
        public List<string> Blocking = new(), Info = new();
        public bool HadBaseline;

        public static Gate Check(EvalRun run, JsonObject baseline, double tolerance)
        {
            var g = new Gate { HadBaseline = baseline != null };
            var checks = baseline?["checks"] as JsonObject ?? new JsonObject();
            var tasks = baseline?["tasks"] as JsonObject ?? new JsonObject();
            foreach (var c in run.Checks)
            {
                bool? was = checks[c.Name] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
                if (c.Passed && was == false) g.Info.Add($"Check {c.Name} passes now (it failed in the baseline): update the baseline.");
                if (c.Passed) continue;
                if (was == true) g.Blocking.Add($"Regression: check {c.Name} passed in the baseline and fails now: {string.Join("; ", c.Failures)}");
                else if (was == false) g.Info.Add($"Known failure: check {c.Name} (fails in the baseline too).");
                else g.Blocking.Add($"Check {c.Name} fails and has no baseline entry: {string.Join("; ", c.Failures)}");
            }
            foreach (var t in run.Tasks)
            {
                if (t.StartPassed == true) g.Blocking.Add($"Task {t.Name}: the starting song already passes ({t.StartTotal:F2}), so the scorer measures nothing.");
                var was = tasks[t.Name] as JsonObject;
                double? total = was?["total"] is JsonNode n ? J.Num(n) : null;
                bool? passed = was?["passed"] is JsonValue pv && pv.TryGetValue(out bool pb) ? pb : null;
                if (was == null)
                {
                    if (!t.Passed) g.Blocking.Add($"Task {t.Name} fails ({t.Total:F2}) and has no baseline entry.");
                    continue;
                }
                if (passed == true && !t.Passed) g.Blocking.Add($"Regression: task {t.Name} passed in the baseline ({total:F2}) and fails now ({t.Total:F2}).");
                else if (total.HasValue && t.Total < total.Value - tolerance) g.Blocking.Add($"Regression: task {t.Name} scored {t.Total:F2}, the baseline {total:F2} (tolerance {tolerance:F2}).");
                else if (passed == false && !t.Passed) g.Info.Add($"Known failure: task {t.Name} ({t.Total:F2}).");
                if (total.HasValue && t.Total > total.Value + tolerance) g.Info.Add($"Task {t.Name} improved: {total:F2} → {t.Total:F2}; update the baseline.");
            }
            foreach (var p in run.Problems) g.Blocking.Add(p);
            return g;
        }

        public static JsonObject Baseline(EvalRun run, double tolerance) => new()
        {
            ["about"] = "Scores of the scripted evaluation (ReBuzz.AgentEval run); a change that makes a passing check fail or a task total drop by more than the tolerance blocks. Rewrite with --update-baseline.",
            ["server"] = run.Server,
            ["commit"] = run.Commit,
            ["tolerance"] = tolerance,
            ["checks"] = new JsonObject(run.Checks.Select(c => KeyValuePair.Create(c.Name, (JsonNode)c.Passed))),
            ["tasks"] = new JsonObject(run.Tasks.Select(t => KeyValuePair.Create(t.Name, (JsonNode)new JsonObject
            {
                ["total"] = Math.Round(t.Total, 3),
                ["passed"] = t.Passed,
                ["criteria"] = new JsonObject(t.Criteria.Select(c => KeyValuePair.Create(c.Name, (JsonNode)Math.Round(c.Score, 3)))),
            }))),
        };
    }
}
