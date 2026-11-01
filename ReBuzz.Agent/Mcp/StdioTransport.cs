// MCP server core (docs/specs/mcp-server.md §3.1, headless mode): newline-delimited JSON-RPC on
// stdin/stdout. Requests run concurrently (so ping and cancellation work during a long call);
// writes are serialised. Ends when stdin closes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuzz.Agent.Mcp
{
    public sealed class StdioTransport
    {
        readonly McpServer server;
        readonly TextReader input;
        readonly Stream output;
        readonly SemaphoreSlim writeLock = new(1, 1);
        readonly McpSession session = new();

        public StdioTransport(McpServer server, Stream input, Stream output)
        {
            this.server = server;
            this.input = new StreamReader(input, new UTF8Encoding(false));
            this.output = output;
        }

        public McpSession Session => session;

        /// <summary>Serves until stdin closes (or <paramref name="cancellation"/>); then waits for calls still running.</summary>
        public async Task RunAsync(CancellationToken cancellation = default)
        {
            var running = new List<Task>();
            while (!cancellation.IsCancellationRequested)
            {
                string? line = await input.ReadLineAsync(cancellation).ConfigureAwait(false);
                if (line == null) break;
                if (line.Trim().Length == 0) continue;
                running.RemoveAll(t => t.IsCompleted);
                running.Add(HandleLine(line, cancellation));
            }
            await Task.WhenAll(running).ConfigureAwait(false);
        }

        async Task HandleLine(string line, CancellationToken cancellation)
        {
            var node = JsonRpc.TryParse(line, out var parseError);
            if (node == null)
            {
                await Write(parseError!).ConfigureAwait(false);
                return;
            }
            if (node is JsonArray batch)
            {
                // Older protocol versions allow batches: answer with an array of the responses.
                var tasks = batch.OfType<JsonObject>().Select(m => Handle(m, cancellation)).ToList();
                var responses = (await Task.WhenAll(tasks).ConfigureAwait(false)).Where(r => r != null).ToList();
                if (responses.Count > 0) await Write(new JsonArray(responses.Select(r => (JsonNode)r!).ToArray())).ConfigureAwait(false);
                return;
            }
            if (node is not JsonObject message)
            {
                await Write(JsonRpc.Error(null, JsonRpc.InvalidRequest, "Invalid request: expected a JSON object.")).ConfigureAwait(false);
                return;
            }
            // Notifications (cancellation above all) are handled at once, not behind running calls.
            var response = await Handle(message, cancellation).ConfigureAwait(false);
            if (response != null) await Write(response).ConfigureAwait(false);
        }

        Task<JsonObject?> Handle(JsonObject message, CancellationToken cancellation) =>
            JsonRpc.IsRequest(message)
                ? Task.Run(() => server.HandleAsync(message, session, Write, cancellation), cancellation)
                : server.HandleAsync(message, session, Write, cancellation);

        async Task Write(JsonNode message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonRpc.Serialize(message) + "\n");
            await writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await output.WriteAsync(bytes).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        Task Write(JsonObject message) => Write((JsonNode)message);
    }
}
