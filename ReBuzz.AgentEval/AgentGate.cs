// Evaluation (docs/specs/agent-knowledge.md §3, K5): the regression gate for the real-agent workflow (agent-eval.yml).
// A real model varies from run to run, so the gate is looser than the scripted suite's (Gate in Results.cs, 0.05):
//  - a task blocks when its total fell by more than the task margin (default 0.34: on a three-criterion task, two
//    criteria lost; one lost criterion is noise), against eval/agent-baseline.json;
//  - the run blocks when the mean total over the tasks it ran, compared with the baseline's mean over the same
//    tasks, fell by more than the mean margin (default 0.05: about two criteria lost across 40 tasks, which one
//    flaky run rarely does and a worse guide, tool description or error text does).
// A task the baseline lacks is information (a new task); a baseline task the run did not run is ignored (a
// partial run is gated on what it ran). Rewrite the baseline from a run you accept: `gate --update-baseline`.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzz.AgentEval
{
    internal static class AgentGate
    {
        public const double TaskMargin = 0.34, MeanMargin = 0.05;

        public sealed class Verdict
        {
            public readonly List<string> Blocking = new(), Info = new();
            public double? Mean, BaselineMean;
            public int Compared;
        }

        public static Verdict Check(JsonObject results, JsonObject baseline, double taskMargin = TaskMargin, double meanMargin = MeanMargin)
        {
            var v = new Verdict();
            var was = baseline?["tasks"] as JsonObject;
            var now = new List<double>();
            var before = new List<double>();
            foreach (var t in J.Objects(results["tasks"]))
            {
                string name = J.Str(t["name"]);
                double total = J.Num(t["total"], 0);
                if (was == null || was[name] is not JsonObject b)
                {
                    if (baseline != null) v.Info.Add($"New task {name} ({total:F2}): no baseline entry yet.");
                    continue;
                }
                double baseTotal = J.Num(b["total"], 0);
                now.Add(total);
                before.Add(baseTotal);
                if (total < baseTotal - taskMargin)
                    v.Blocking.Add($"Regression: task {name} scored {total:F2}, the baseline {baseTotal:F2} (task margin {taskMargin:F2}).");
                else if (total > baseTotal + taskMargin)
                    v.Info.Add($"Task {name} improved: {baseTotal:F2} → {total:F2}.");
            }
            v.Compared = now.Count;
            if (now.Count > 0)
            {
                v.Mean = now.Average();
                v.BaselineMean = before.Average();
                if (v.Mean < v.BaselineMean - meanMargin)
                    v.Blocking.Add($"Regression: the mean total over {now.Count} tasks is {v.Mean:F3}, the baseline's over the same tasks {v.BaselineMean:F3} (mean margin {meanMargin:F2}).");
            }
            return v;
        }

        public static JsonObject Baseline(JsonObject results, string model) => new()
        {
            ["about"] = "Scores of the real-agent evaluation (agent-eval.yml, docs/specs/agent-knowledge.md §10). A task whose total falls by more than the task margin, " +
                        "or a mean over the tasks run that falls by more than the mean margin, fails the workflow. Rewrite from a run you accept: ReBuzz.AgentEval gate --update-baseline.",
            ["model"] = model,
            ["server"] = results["server"]?.DeepClone(),
            ["commit"] = results["commit"]?.DeepClone(),
            ["task_margin"] = TaskMargin,
            ["mean_margin"] = MeanMargin,
            ["tasks"] = new JsonObject(J.Objects(results["tasks"]).Select(t => KeyValuePair.Create(J.Str(t["name"]), (JsonNode)new JsonObject
            {
                ["total"] = Math.Round(J.Num(t["total"], 0), 3),
                ["passed"] = J.Bool(t["passed"]),
            }))),
        };

        public static string Markdown(JsonObject results, JsonObject baseline, Verdict v)
        {
            var md = new System.Text.StringBuilder();
            md.AppendLine("## Regression gate (real agent)").AppendLine();
            if (baseline == null) md.AppendLine("No eval/agent-baseline.json yet: nothing to compare with. The baseline this run would write is in the annotations.").AppendLine();
            else
            {
                md.AppendLine($"Mean total {(v.Mean.HasValue ? v.Mean.Value.ToString("F3", J.C) : "n/a")} over {v.Compared} tasks, baseline {(v.BaselineMean.HasValue ? v.BaselineMean.Value.ToString("F3", J.C) : "n/a")}; margins: task {J.Num(baseline["task_margin"], TaskMargin):F2}, mean {J.Num(baseline["mean_margin"], MeanMargin):F2}.").AppendLine();
                var was = baseline["tasks"] as JsonObject;
                md.AppendLine("| Task | Total | Baseline | Change |").AppendLine("|---|---|---|---|");
                foreach (var t in J.Objects(results["tasks"]))
                {
                    string name = J.Str(t["name"]);
                    double total = J.Num(t["total"], 0);
                    if (was?[name] is JsonObject b) md.AppendLine($"| {name} | {total:F2} | {J.Num(b["total"], 0):F2} | {total - J.Num(b["total"], 0):+0.00;-0.00;0.00} |");
                    else md.AppendLine($"| {name} | {total:F2} | new | |");
                }
                md.AppendLine();
            }
            foreach (var b in v.Blocking) md.AppendLine($"- **{b}**");
            foreach (var i in v.Info) md.AppendLine($"- {i}");
            if (v.Blocking.Count == 0) md.AppendLine(baseline == null ? "" : "No regression past the margins.");
            return md.ToString();
        }
    }
}
