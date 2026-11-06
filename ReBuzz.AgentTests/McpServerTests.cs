using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using static ReBuzz.AgentTests.TestTools;

namespace ReBuzz.AgentTests
{
    /// <summary>The protocol (docs/specs/mcp-server.md §3–4): lifecycle, tools, errors, prompts, resources, progress, cancellation.</summary>
    [TestFixture]
    public class McpServerTests
    {
        static Task<JsonObject?> Send(McpServer s, McpSession session, JsonObject m, List<JsonObject>? notes = null, CancellationToken ct = default) =>
            s.HandleAsync(m, session, n => { lock (notes ?? new()) notes?.Add(n); return Task.CompletedTask; }, ct);

        [Test]
        public async Task InitializeNegotiatesTheVersion()
        {
            var s = Server();
            var session = new McpSession();
            var r = await Send(s, session, Initialize(1, "2025-06-18"));
            Assert.That(r!["result"]!["protocolVersion"]!.GetValue<string>(), Is.EqualTo("2025-06-18"));
            Assert.That(r["result"]!["serverInfo"]!["name"]!.GetValue<string>(), Is.EqualTo("neobuzz-live"));
            Assert.That(r["result"]!["capabilities"]!["tools"], Is.Not.Null);
            Assert.That(r["result"]!["instructions"]!.GetValue<string>(), Does.Contain("studio_status"));

            var other = await Send(s, new McpSession(), Initialize(1, "1999-01-01"));
            Assert.That(other!["result"]!["protocolVersion"]!.GetValue<string>(), Is.EqualTo(McpServer.SupportedVersions[0]), "an unknown version gets our newest");

            Assert.That(await Send(s, session, JsonRpc.Notification("notifications/initialized")), Is.Null, "notifications get no answer");
            Assert.That(session.Initialized, Is.True);
        }

        [Test]
        public async Task ToolsAreListedWithSchemasAndAnnotations()
        {
            var r = await Send(Server(), new McpSession(), Request(2, "tools/list"));
            var tools = r!["result"]!["tools"]!.AsArray();
            var set = tools.Single(t => t!["name"]!.GetValue<string>() == "song_set")!;
            Assert.That(set["inputSchema"]!["type"]!.GetValue<string>(), Is.EqualTo("object"));
            Assert.That(set["outputSchema"], Is.Not.Null);
            Assert.That(set["annotations"]!["idempotentHint"]!.GetValue<bool>(), Is.True);
            Assert.That(set["annotations"]!["openWorldHint"]!.GetValue<bool>(), Is.False);
        }

        [Test]
        public async Task ACallReturnsStructuredContentAndASummary()
        {
            var r = await Send(Server(), new McpSession(), Call(3, "song_set", new JsonObject { ["tempo"] = 92 }));
            var result = r!["result"]!;
            Assert.That(result["isError"]!.GetValue<bool>(), Is.False);
            Assert.That(result["structuredContent"]!["tempo"]!.GetValue<double>(), Is.EqualTo(92));
            Assert.That(result["content"]![0]!["text"]!.GetValue<string>(), Is.EqualTo("Tempo 92 BPM."));
            Assert.That(result["content"]![1]!["text"]!.GetValue<string>(), Does.Contain("\"tempo\":92"), "the JSON too, for text-only clients");
        }

        [Test]
        public async Task BadArgumentsNameTheArgumentAndWhatWasExpected()
        {
            var s = Server();
            var r = await Send(s, new McpSession(), Call(4, "song_set", new JsonObject { ["tempo"] = "fast" }));
            Assert.That(r!["result"]!["isError"]!.GetValue<bool>(), Is.True);
            Assert.That(Text(r), Does.Contain("tempo: expected a number"));

            r = await Send(s, new McpSession(), Call(5, "song_set", new JsonObject { ["tempo"] = 1000 }));
            Assert.That(Text(r), Does.Contain("above the maximum 500"));

            r = await Send(s, new McpSession(), Call(6, "song_set", new JsonObject { ["bpm"] = 100 }));
            Assert.That(Text(r), Does.Contain("bpm: unknown argument (known: tempo, name)"));

            r = await Send(s, new McpSession(), Call(7, "notes_write", new JsonObject { ["notes"] = new JsonArray(new JsonObject { ["pitch"] = 60, ["start"] = 0 }, new JsonObject { ["pitch"] = 60.5 }) }));
            Assert.That(Text(r), Does.Contain("notes[1].pitch: expected a whole number"));
            Assert.That(Text(r), Does.Contain("notes[1].start: missing (required)"));
        }

        static string Text(JsonObject? r) => r!["result"]!["content"]![0]!["text"]!.GetValue<string>();

        [Test]
        public async Task ToolFailuresComeBackAsToolErrorsTheAgentCanReadNotAsCrashes()
        {
            var s = Server();
            var r = await Send(s, new McpSession(), Call(8, "refuse"));
            Assert.That(r!["result"]!["isError"]!.GetValue<bool>(), Is.True);
            Assert.That(Text(r), Is.EqualTo("No machine \"Bass\". Did you mean \"Bass 2\"?"));

            r = await Send(s, new McpSession(), Call(9, "boom"));
            Assert.That(r!["result"]!["isError"]!.GetValue<bool>(), Is.True);
            Assert.That(Text(r), Does.Contain("boom failed inside NeoBuzz Live: InvalidOperationException: collection was modified"));
        }

        [Test]
        public async Task UnknownToolsAndMethodsAreProtocolErrorsWithHints()
        {
            var s = Server();
            var r = await Send(s, new McpSession(), Call(10, "song_sett"));
            Assert.That(r!["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpc.InvalidParams));
            Assert.That(r["error"]!["message"]!.GetValue<string>(), Does.Contain("Did you mean \"song_set\""));

            r = await Send(s, new McpSession(), Request(11, "tools/destroy"));
            Assert.That(r!["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpc.MethodNotFound));

            r = await Send(s, new McpSession(), new JsonObject { ["jsonrpc"] = "1.0", ["id"] = 12, ["method"] = "ping" });
            Assert.That(r!["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpc.InvalidRequest));
        }

        [Test]
        public async Task ProgressIsReportedWhenAsked()
        {
            var notes = new List<JsonObject>();
            var r = await Send(Server(), new McpSession(), Call(13, "render", new JsonObject { ["steps"] = 3 }, "tok-1"), notes);
            Assert.That(r!["result"]!["isError"]!.GetValue<bool>(), Is.False);
            Assert.That(notes.Count, Is.EqualTo(3));
            Assert.That(notes[2]["method"]!.GetValue<string>(), Is.EqualTo("notifications/progress"));
            Assert.That(notes[2]["params"]!["progressToken"]!.GetValue<string>(), Is.EqualTo("tok-1"));
            Assert.That(notes[2]["params"]!["total"]!.GetValue<double>(), Is.EqualTo(3));

            var quiet = new List<JsonObject>();
            await Send(Server(), new McpSession(), Call(14, "render"), quiet);
            Assert.That(quiet.Count, Is.EqualTo(0), "no token, no progress");
        }

        [Test]
        public async Task ACancelledCallStops()
        {
            var s = Server();
            var session = new McpSession();
            var running = Send(s, session, Call(15, "render", new JsonObject { ["steps"] = 100 }));
            await Task.Delay(80);
            await Send(s, session, JsonRpc.Notification("notifications/cancelled", new JsonObject { ["requestId"] = 15, ["reason"] = "user" }));
            var r = await running.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(r!["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpc.RequestCancelled));
        }

        [Test]
        public async Task PromptsAndResources()
        {
            var s = Server();
            var list = await Send(s, new McpSession(), Request(16, "prompts/list"));
            Assert.That(list!["result"]!["prompts"]![0]!["arguments"]![0]!["required"]!.GetValue<bool>(), Is.True);
            var get = await Send(s, new McpSession(), Request(17, "prompts/get", new JsonObject { ["name"] = "compose", ["arguments"] = new JsonObject { ["genre"] = "synthwave" } }));
            Assert.That(get!["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>(), Is.EqualTo("Compose a synthwave track."));
            var missing = await Send(s, new McpSession(), Request(18, "prompts/get", new JsonObject { ["name"] = "compose" }));
            Assert.That(missing!["error"]!["message"]!.GetValue<string>(), Does.Contain("needs the argument \"genre\""));

            var guide = await Send(s, new McpSession(), Request(19, "resources/read", new JsonObject { ["uri"] = "rebuzz://guides/workflow" }));
            Assert.That(guide!["result"]!["contents"]![0]!["text"]!.GetValue<string>(), Is.EqualTo("# Workflow"));
            var blob = await Send(s, new McpSession(), Request(20, "resources/read", new JsonObject { ["uri"] = "rebuzz://renders/r1.wav" }));
            Assert.That(blob!["result"]!["contents"]![0]!["blob"]!.GetValue<string>(), Is.EqualTo("AQID"));
            var none = await Send(s, new McpSession(), Request(21, "resources/read", new JsonObject { ["uri"] = "rebuzz://nothing" }));
            Assert.That(none!["error"]!["code"]!.GetValue<int>(), Is.EqualTo(-32002));
        }

        [Test]
        public async Task EveryCallIsLogged()
        {
            var s = Server();
            var calls = new List<ToolCallRecord>();
            s.ToolCalled += calls.Add;
            await Send(s, new McpSession(), Call(22, "song_set", new JsonObject { ["tempo"] = 100 }));
            await Send(s, new McpSession(), Call(23, "refuse"));
            Assert.That(calls.Select(c => (c.Tool, c.IsError)).ToArray(), Is.EqualTo(new[] { ("song_set", false), ("refuse", true) }));

            string dir = Path.Combine(Path.GetTempPath(), "rebuzz-agent-log-" + Guid.NewGuid().ToString("N"));
            var log = new ReBuzz.Agent.AgentLog(dir, "test");
            foreach (var c in calls) log.Write(c);
            var lines = File.ReadAllLines(log.FilePath);
            Assert.That(lines.Length, Is.EqualTo(2));
            Assert.That(JsonNode.Parse(lines[1])!["tool"]!.GetValue<string>(), Is.EqualTo("refuse"));
            Directory.Delete(dir, true);
        }
    }
}
