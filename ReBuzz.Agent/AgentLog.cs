// Agent server (docs/specs/mcp-server.md §3.4): a log of every tool call (time, tool, arguments,
// result, duration) as JSON lines, one file per session, for the user to review.

using System;
using System.IO;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent
{
    public sealed class AgentLog
    {
        readonly object gate = new();

        public AgentLog(string folder, string mode)
        {
            Directory.CreateDirectory(folder);
            FilePath = Path.Combine(folder, $"agent-{DateTime.Now:yyyyMMdd-HHmmss}-{mode}.jsonl");
        }

        public string FilePath { get; }

        public void Write(ToolCallRecord call)
        {
            var entry = new JsonObject
            {
                ["time"] = DateTimeOffset.Now.ToString("O"),
                ["tool"] = call.Tool,
                ["arguments"] = Cut(JsonRpc.Serialize(call.Arguments), 4000),
                ["error"] = call.IsError,
                ["result"] = Cut(call.Summary, 1000),
                ["ms"] = Math.Round(call.Duration.TotalMilliseconds, 1),
            };
            lock (gate)
            {
                try
                {
                    File.AppendAllText(FilePath, JsonRpc.Serialize(entry) + "\n");
                }
                catch (IOException)
                {
                    // The log must never fail a call.
                }
            }
        }

        static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
