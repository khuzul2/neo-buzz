using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.AgentTests
{
    /// <summary>A small server with tools that exercise every path: success, bad input, errors, slow work.</summary>
    internal static class TestTools
    {
        public static McpServer Server()
        {
            var tools = new ToolRegistry();
            tools.Add(new ToolDef
            {
                Name = "song_set",
                Title = "Set song properties",
                Description = "Sets the tempo. Example: song_set(tempo=92).",
                Input = Object(null, Opt("tempo", Number("Beats per minute", 16, 500)), Opt("name", String("Song name"))),
                Output = Object(null, Req("tempo", Number("Beats per minute"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = c => Task.FromResult(ToolResult.Ok($"Tempo {c.Number("tempo") ?? 120} BPM.", new JsonObject { ["tempo"] = c.Number("tempo") ?? 120 })),
            });
            tools.Add(new ToolDef
            {
                Name = "notes_write",
                Title = "Write notes",
                Description = "Writes notes.",
                Input = Object(null, Req("notes", Array(Object(null, Req("pitch", Integer("MIDI note", 0, 127)), Req("start", Number("Beat"))), "Notes", 1))),
                Handler = c => Task.FromResult(ToolResult.Ok($"{c.List("notes")!.Count} notes.")),
            });
            tools.Add(new ToolDef
            {
                Name = "refuse",
                Title = "Refuse",
                Description = "Always fails with advice.",
                Input = Object(null),
                Handler = _ => throw new ToolError("No machine \"Bass\". Did you mean \"Bass 2\"?"),
            });
            tools.Add(new ToolDef
            {
                Name = "boom",
                Title = "Boom",
                Description = "Crashes.",
                Input = Object(null),
                Handler = _ => throw new InvalidOperationException("collection was modified"),
            });
            tools.Add(new ToolDef
            {
                Name = "render",
                Title = "Slow render",
                Description = "Takes a while and reports progress.",
                Input = Object(null, Opt("steps", Integer("Steps", 1, 100))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    int steps = c.Int("steps") ?? 3;
                    for (int i = 0; i < steps; i++)
                    {
                        c.Progress(i, steps, $"step {i + 1}");
                        await Task.Delay(30, c.Cancellation);
                    }
                    return ToolResult.Ok("Rendered.");
                },
            });
            var prompts = new[]
            {
                new PromptDef
                {
                    Name = "compose",
                    Title = "Compose",
                    Description = "Plan and build a track.",
                    Arguments = new[] { new PromptArgument { Name = "genre", Description = "e.g. synthwave", Required = true } },
                    Render = a => $"Compose a {a["genre"]} track.",
                },
            };
            var resources = new[]
            {
                new ResourceDef { Uri = "rebuzz://guides/workflow", Name = "workflow", Title = "Workflow", Description = "The loop", Read = () => "# Workflow" },
            };
            var server = new McpServer(new ServerInfo { Name = "rebuzz", Title = "ReBuzz", Version = "test", Instructions = "Start with studio_status." }, tools, prompts, resources);
            server.AddResolver(uri => uri == "rebuzz://renders/r1.wav" ? new ResourceContent(uri, "audio/wav", Blob: new byte[] { 1, 2, 3 }) : null);
            return server;
        }

        public static JsonObject Request(int id, string method, JsonObject? p = null)
        {
            var m = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (p != null) m["params"] = p;
            return m;
        }

        public static JsonObject Call(int id, string tool, JsonObject? args = null, JsonNode? progressToken = null)
        {
            var p = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() };
            if (progressToken != null) p["_meta"] = new JsonObject { ["progressToken"] = progressToken };
            return Request(id, "tools/call", p);
        }

        public static JsonObject Initialize(int id = 1, string version = "2025-06-18") => Request(id, "initialize", new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
        });
    }
}
