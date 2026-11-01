using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ReBuzz.Agent;
using ReBuzz.Agent.Mcp;
using static ReBuzz.AgentTests.TestTools;

namespace ReBuzz.AgentTests
{
    /// <summary>stdio (headless mode) and Streamable HTTP (studio mode), as Claude Code uses them.</summary>
    [TestFixture]
    public class TransportTests
    {
        [Test]
        public async Task StdioAnswersEachLineAndKeepsPingFastDuringALongCall()
        {
            var toServer = new Pipe();
            var fromServer = new Pipe();
            var transport = new StdioTransport(Server(), toServer.Reader.AsStream(), fromServer.Writer.AsStream());
            var run = transport.RunAsync();
            var input = toServer.Writer.AsStream();
            var output = new StreamReader(fromServer.Reader.AsStream(), Encoding.UTF8);

            async Task SendLine(JsonNode m)
            {
                await input.WriteAsync(Encoding.UTF8.GetBytes(JsonRpc.Serialize(m) + "\n"));
                await input.FlushAsync();
            }
            async Task<JsonObject> Next() => (JsonObject)JsonNode.Parse((await output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!)!;

            await SendLine(Initialize());
            Assert.That((await Next())["result"]!["protocolVersion"]!.GetValue<string>(), Is.EqualTo("2025-06-18"));
            await SendLine(JsonRpc.Notification("notifications/initialized"));
            await SendLine(Call(2, "render", new JsonObject { ["steps"] = 10 }));
            await SendLine(Request(3, "ping"));
            var first = await Next();
            Assert.That(first["id"]!.GetValue<int>(), Is.EqualTo(3), "ping answers while the render still runs");
            var second = await Next();
            Assert.That(second["id"]!.GetValue<int>(), Is.EqualTo(2));

            await input.WriteAsync(Encoding.UTF8.GetBytes("{not json\n"));
            Assert.That((await Next())["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpc.ParseError));

            toServer.Writer.Complete();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(transport.Session.Initialized, Is.True);
        }

        sealed class Http : IDisposable
        {
            public readonly HttpTransport Transport;
            public readonly HttpClient Client = new();
            public readonly string Token = HttpTransport.NewToken();

            public Http()
            {
                Transport = new HttpTransport(Server(), 0, Token);
                Transport.Start();
            }

            public HttpRequestMessage Post(JsonNode body, string? session = null, bool token = true, string accept = "application/json, text/event-stream")
            {
                var r = new HttpRequestMessage(HttpMethod.Post, Transport.Url) { Content = new StringContent(JsonRpc.Serialize(body), Encoding.UTF8, "application/json") };
                r.Headers.Accept.ParseAdd(accept);
                if (token) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
                if (session != null) r.Headers.Add("Mcp-Session-Id", session);
                return r;
            }

            public void Dispose()
            {
                Client.Dispose();
                Transport.Dispose();
            }
        }

        [Test]
        public async Task HttpRefusesWithoutTheTokenAndFromWebPages()
        {
            using var h = new Http();
            var noToken = await h.Client.SendAsync(h.Post(Initialize(), token: false));
            Assert.That(noToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(noToken.Headers.WwwAuthenticate.ToString(), Does.Contain("Bearer"));

            var page = h.Post(Initialize());
            page.Headers.Add("Origin", "https://evil.example");
            Assert.That((await h.Client.SendAsync(page)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            var local = h.Post(Initialize());
            local.Headers.Add("Origin", "http://localhost:3000");
            Assert.That((await h.Client.SendAsync(local)).StatusCode, Is.EqualTo(HttpStatusCode.OK), "a local origin is fine");

            var get = new HttpRequestMessage(HttpMethod.Get, h.Transport.Url);
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", h.Token);
            Assert.That((await h.Client.SendAsync(get)).StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));

            var elsewhere = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{h.Transport.Port}/other");
            elsewhere.Headers.Authorization = new AuthenticationHeaderValue("Bearer", h.Token);
            Assert.That((await h.Client.SendAsync(elsewhere)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        [Test]
        public async Task HttpSessionsFromInitializeToDelete()
        {
            using var h = new Http();
            var init = await h.Client.SendAsync(h.Post(Initialize()));
            Assert.That(init.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(init.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/json"));
            string session = init.Headers.GetValues("Mcp-Session-Id").Single();

            var initialized = await h.Client.SendAsync(h.Post(JsonRpc.Notification("notifications/initialized"), session));
            Assert.That(initialized.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

            var list = await h.Client.SendAsync(h.Post(Request(2, "tools/list"), session));
            var body = JsonNode.Parse(await list.Content.ReadAsStringAsync())!;
            Assert.That(body["result"]!["tools"]!.AsArray().Count, Is.EqualTo(5));

            var noSession = await h.Client.SendAsync(h.Post(Request(3, "tools/list")));
            Assert.That(noSession.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            var delete = new HttpRequestMessage(HttpMethod.Delete, h.Transport.Url);
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", h.Token);
            delete.Headers.Add("Mcp-Session-Id", session);
            Assert.That((await h.Client.SendAsync(delete)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var gone = await h.Client.SendAsync(h.Post(Request(4, "tools/list"), session));
            Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "a deleted session must initialize again");
        }

        [Test]
        public async Task HttpStreamsProgressAsServerSentEvents()
        {
            using var h = new Http();
            var init = await h.Client.SendAsync(h.Post(Initialize()));
            string session = init.Headers.GetValues("Mcp-Session-Id").Single();
            var r = await h.Client.SendAsync(h.Post(Call(5, "render", new JsonObject { ["steps"] = 3 }, 7), session));
            Assert.That(r.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/event-stream"));
            var events = (await r.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(e => JsonNode.Parse(e.Split('\n').Single(l => l.StartsWith("data: ")).Substring(6))!)
                .ToList();
            Assert.That(events.Count, Is.EqualTo(4));
            Assert.That(events.Take(3).All(e => e["method"]!.GetValue<string>() == "notifications/progress"), Is.True);
            Assert.That(events[3]["id"]!.GetValue<int>(), Is.EqualTo(5));

            var plain = await h.Client.SendAsync(h.Post(Call(6, "render", new JsonObject { ["steps"] = 2 }), session));
            Assert.That(plain.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/json"), "no progress token: plain JSON");
        }

        [Test]
        public async Task HttpKeepsTheConnectionAcrossRequests()
        {
            using var h = new Http();
            var init = await h.Client.SendAsync(h.Post(Initialize()));
            string session = init.Headers.GetValues("Mcp-Session-Id").Single();
            for (int i = 0; i < 20; i++)
            {
                var r = await h.Client.SendAsync(h.Post(Request(10 + i, "ping"), session));
                Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }
        }

        [Test]
        public async Task TheEngineQueueRunsOneCallAtATimeOnItsThread()
        {
            using var thread = new DedicatedEngineThread();
            var q = new EngineQueue(thread, TimeSpan.FromSeconds(2));
            int engineThreadId = await thread.InvokeAsync(() => Environment.CurrentManagedThreadId);
            int inside = 0, most = 0;
            var calls = Enumerable.Range(0, 8).Select(i => q.Run($"call {i}", () =>
            {
                int now = Interlocked.Increment(ref inside);
                most = Math.Max(most, now);
                Thread.Sleep(5);
                Interlocked.Decrement(ref inside);
                return Environment.CurrentManagedThreadId;
            })).ToList();
            var ids = await Task.WhenAll(calls);
            Assert.That(most, Is.EqualTo(1));
            Assert.That(ids.All(i => i == engineThreadId), Is.True);

            var slow = new EngineQueue(thread, TimeSpan.FromMilliseconds(100));
            var error = Assert.ThrowsAsync<ToolError>(() => slow.Run("render", () => { Thread.Sleep(400); return 0; }));
            Assert.That(error!.Message, Does.Contain("render took longer than"));
        }
    }
}
