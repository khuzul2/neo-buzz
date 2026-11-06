// Evaluation: reads a real agent's transcript (Claude Code's `--output-format stream-json`, one
// JSON message per line) for the scores: the app's tool calls it made, the ones that came back
// as errors (and their texts: the tools whose errors agents hit get rewritten, §10), its turns,
// cost and how it stopped.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzz.AgentEval
{
    internal static class Transcript
    {
        /// <summary>The app's tools as Claude Code names them: mcp__neobuzz-live__&lt;tool&gt; (the server's name, Product.McpName).</summary>
        public const string ToolPrefix = "mcp__" + Product.McpName + "__";

        /// <summary>The server's name before the app was NeoBuzz Live, in transcripts recorded then.</summary>
        const string FormerServer = "rebuzz";
        const string FormerToolPrefix = "mcp__" + FormerServer + "__";

        /// <summary>The app's tool a call names (studio_status), or null for another tool.</summary>
        static string AppTool(string name) =>
            name.StartsWith(ToolPrefix) ? name[ToolPrefix.Length..] : name.StartsWith(FormerToolPrefix) ? name[FormerToolPrefix.Length..] : null;

        public static void Read(string file, TaskResult result)
        {
            var names = new Dictionary<string, string>();
            var tools = new Dictionary<string, int>();
            var errors = new JsonArray();
            int calls = 0, errorCalls = 0, otherTools = 0;
            var agent = new JsonObject();
            foreach (var line in File.ReadLines(file))
            {
                if (line.Trim().Length == 0) continue;
                JsonObject m;
                try { m = JsonNode.Parse(line) as JsonObject; }
                catch (Exception) { continue; }
                if (m == null) continue;
                string type = J.Str(m["type"]);
                if (type == "system" && J.Str(m["subtype"]) == "init")
                {
                    agent["model"] = m["model"]?.DeepClone();
                    agent["claude_code"] = m["claude_code_version"]?.DeepClone();
                    var server = J.Objects(m["mcp_servers"]).FirstOrDefault(x => J.Str(x["name"]) is Product.McpName or FormerServer);
                    agent["mcp_status"] = server?["status"]?.DeepClone() ?? "missing";
                }
                else if (type == "assistant")
                {
                    foreach (var block in J.Objects(m["message"]?["content"]).Where(b => J.Str(b["type"]) == "tool_use"))
                    {
                        string name = J.Str(block["name"]) ?? "";
                        names[J.Str(block["id"]) ?? ""] = name;
                        if (AppTool(name) is string tool)
                        {
                            calls++;
                            tools[tool] = tools.GetValueOrDefault(tool) + 1;
                        }
                        else otherTools++;
                    }
                }
                else if (type == "user")
                {
                    foreach (var block in J.Objects(m["message"]?["content"]).Where(b => J.Str(b["type"]) == "tool_result" && J.Bool(b["is_error"])))
                    {
                        string name = names.GetValueOrDefault(J.Str(block["tool_use_id"]) ?? "", "");
                        if (AppTool(name) is not string tool) continue;
                        errorCalls++;
                        string text = block["content"] is JsonArray parts
                            ? string.Join(" ", J.Objects(parts).Select(p => J.Str(p["text"])).Where(t => t != null))
                            : J.Str(block["content"]) ?? "";
                        if (errors.Count < 40) errors.Add(new JsonObject { ["tool"] = tool, ["text"] = text.Length > 400 ? text[..400] + "…" : text });
                    }
                }
                else if (type == "result")
                {
                    agent["stopped"] = m["subtype"]?.DeepClone();
                    agent["turns"] = m["num_turns"]?.DeepClone();
                    agent["cost_usd"] = m["total_cost_usd"] is JsonNode cost ? Math.Round(J.Num(cost), 2) : null;
                    agent["seconds"] = m["duration_ms"] is JsonNode ms ? Math.Round(J.Num(ms) / 1000, 1) : null;
                    agent["is_error"] = m["is_error"]?.DeepClone();
                    string final = J.Str(m["result"]);
                    if (final != null) agent["final_message"] = final.Length > 600 ? final[..600] + "…" : final;
                }
            }
            agent["tool_counts"] = new JsonObject(tools.OrderByDescending(kv => kv.Value).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)kv.Value)));
            agent["tool_errors"] = errors;
            agent["other_tool_calls"] = otherTools;
            result.Calls = calls;
            result.Errors = errorCalls;
            if (agent["seconds"] is JsonNode s) result.Seconds = J.Num(s);
            result.Agent = agent;
        }
    }
}
