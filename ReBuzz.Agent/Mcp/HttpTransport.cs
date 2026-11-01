// MCP server core (docs/specs/mcp-server.md §3.1, studio mode): Streamable HTTP on 127.0.0.1.
// A small HTTP/1.1 server over TcpListener (no http.sys URL reservations on Windows, no ASP.NET
// runtime, works on macOS): POST carries one JSON-RPC message; the answer comes back as JSON, or
// as a short SSE stream when the call reports progress. Every request needs the bearer token;
// Host and Origin must be local (no DNS rebinding); sessions are named by Mcp-Session-Id.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuzz.Agent.Mcp
{
    public sealed class HttpTransport : IDisposable
    {
        const int MaxHead = 64 * 1024;
        const int MaxBody = 32 * 1024 * 1024;

        readonly McpServer server;
        readonly string token;
        readonly string path;
        readonly int requestedPort;
        readonly ConcurrentDictionary<string, McpSession> sessions = new();
        TcpListener? listener;
        CancellationTokenSource? stopping;

        public HttpTransport(McpServer server, int port, string token, string path = "/mcp")
        {
            this.server = server;
            this.token = token;
            this.path = path;
            requestedPort = port;
        }

        /// <summary>The port in use (the requested one, or the one the OS chose for 0).</summary>
        public int Port { get; private set; }
        public string Url => $"http://127.0.0.1:{Port}{path}";
        public int SessionCount => sessions.Count;

        /// <summary>A new random bearer token (URL-safe).</summary>
        public static string NewToken() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, requestedPort);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            stopping = new CancellationTokenSource();
            _ = AcceptLoop(stopping.Token);
        }

        public void Dispose()
        {
            stopping?.Cancel();
            try { listener?.Stop(); } catch (SocketException) { }
            listener = null;
            sessions.Clear();
        }

        async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested || listener == null)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }
                _ = Serve(client, ct);
            }
        }

        sealed class Request
        {
            public string Method = "", Target = "", Version = "";
            public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = Array.Empty<byte>();
            public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        }

        async Task Serve(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var reader = new BufferedReader(stream);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        idle.CancelAfter(TimeSpan.FromMinutes(5));
                        var request = await ReadRequest(reader, idle.Token).ConfigureAwait(false);
                        if (request == null) return;
                        bool keepAlive = await Respond(request, stream, ct).ConfigureAwait(false);
                        if (!keepAlive || string.Equals(request.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase)) return;
                    }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // Client went away or idled out.
                }
                catch (HttpError e)
                {
                    try { await WriteText(stream, e.Status, e.Message, close: true).ConfigureAwait(false); } catch (IOException) { }
                }
            }
        }

        sealed class HttpError : Exception
        {
            public HttpError(int status, string message) : base(message) => Status = status;
            public int Status { get; }
        }

        static async Task<Request?> ReadRequest(BufferedReader reader, CancellationToken ct)
        {
            string? line = await reader.ReadLine(MaxHead, ct).ConfigureAwait(false);
            if (line == null) return null;
            while (line.Length == 0)   // tolerate stray CRLFs between requests
            {
                line = await reader.ReadLine(MaxHead, ct).ConfigureAwait(false);
                if (line == null) return null;
            }
            var parts = line.Split(' ');
            if (parts.Length != 3) throw new HttpError(400, "Bad request line");
            var r = new Request { Method = parts[0], Target = parts[1], Version = parts[2] };
            int headBytes = line.Length;
            while (true)
            {
                string? h = await reader.ReadLine(MaxHead, ct).ConfigureAwait(false);
                if (h == null) return null;
                if (h.Length == 0) break;
                headBytes += h.Length;
                if (headBytes > MaxHead) throw new HttpError(431, "Headers too large");
                int colon = h.IndexOf(':');
                if (colon <= 0) throw new HttpError(400, "Bad header");
                string name = h.Substring(0, colon).Trim(), value = h.Substring(colon + 1).Trim();
                r.Headers[name] = r.Headers.TryGetValue(name, out var prev) ? prev + ", " + value : value;
            }
            if (string.Equals(r.Header("Transfer-Encoding"), "chunked", StringComparison.OrdinalIgnoreCase))
            {
                var body = new MemoryStream();
                while (true)
                {
                    string? sizeLine = await reader.ReadLine(1024, ct).ConfigureAwait(false) ?? throw new HttpError(400, "Truncated chunk");
                    int semi = sizeLine.IndexOf(';');
                    int size = Convert.ToInt32((semi >= 0 ? sizeLine.Substring(0, semi) : sizeLine).Trim(), 16);
                    if (size == 0)
                    {
                        while ((await reader.ReadLine(MaxHead, ct).ConfigureAwait(false))?.Length > 0) { }
                        break;
                    }
                    if (body.Length + size > MaxBody) throw new HttpError(413, "Body too large");
                    body.Write(await reader.ReadBytes(size, ct).ConfigureAwait(false));
                    await reader.ReadLine(16, ct).ConfigureAwait(false);
                }
                r.Body = body.ToArray();
            }
            else if (r.Header("Content-Length") is string cl)
            {
                if (!int.TryParse(cl, out int length) || length < 0) throw new HttpError(400, "Bad Content-Length");
                if (length > MaxBody) throw new HttpError(413, "Body too large");
                r.Body = await reader.ReadBytes(length, ct).ConfigureAwait(false);
            }
            return r;
        }

        bool LocalHost(string? host)
        {
            if (host == null) return false;
            string h = host.ToLowerInvariant();
            foreach (var name in new[] { "127.0.0.1", "localhost", "[::1]" })
                if (h == name || h == $"{name}:{Port}") return true;
            return false;
        }

        static bool LocalOrigin(string? origin)
        {
            if (origin == null || origin == "null") return origin == null;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
            return u.Host == "127.0.0.1" || u.Host == "localhost" || u.Host == "[::1]" || u.Host == "::1";
        }

        bool Authorized(string? authorization)
        {
            if (authorization == null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
            var given = Encoding.UTF8.GetBytes(authorization.Substring(7).Trim());
            var expected = Encoding.UTF8.GetBytes(token);
            return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
        }

        /// <summary>Answers one request; false when the connection must close afterwards.</summary>
        async Task<bool> Respond(Request r, NetworkStream stream, CancellationToken ct)
        {
            string target = r.Target;
            int q = target.IndexOf('?');
            if (q >= 0) target = target.Substring(0, q);
            if (!LocalHost(r.Header("Host"))) return await WriteText(stream, 403, "Forbidden: this server only answers to localhost.", close: true).ConfigureAwait(false);
            if (!LocalOrigin(r.Header("Origin"))) return await WriteText(stream, 403, "Forbidden: requests from web pages are not allowed.", close: true).ConfigureAwait(false);
            if (target != path) return await WriteText(stream, 404, $"Not found. The MCP endpoint is {path}.").ConfigureAwait(false);
            if (!Authorized(r.Header("Authorization")))
                return await Write(stream, 401, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Unauthorized: send the token shown in ReBuzz (Settings → Agent) as a bearer token."),
                    new[] { ("WWW-Authenticate", "Bearer realm=\"ReBuzz\"") }).ConfigureAwait(false);

            switch (r.Method)
            {
                case "POST":
                    return await Post(r, stream, ct).ConfigureAwait(false);
                case "DELETE":
                    if (r.Header("Mcp-Session-Id") is string sid && sessions.TryRemove(sid, out _))
                        return await Write(stream, 200, null, Array.Empty<byte>()).ConfigureAwait(false);
                    return await WriteText(stream, 404, "Unknown session.").ConfigureAwait(false);
                case "GET":
                    // No server-initiated stream (yet): the spec lets a server answer 405 here.
                    return await Write(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("This server sends no standalone event stream."), new[] { ("Allow", "POST, DELETE") }).ConfigureAwait(false);
                default:
                    return await Write(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Method not allowed."), new[] { ("Allow", "POST, DELETE") }).ConfigureAwait(false);
            }
        }

        async Task<bool> Post(Request r, NetworkStream stream, CancellationToken ct)
        {
            var node = JsonRpc.TryParse(Encoding.UTF8.GetString(r.Body), out var parseError);
            if (node == null) return await WriteJson(stream, 400, parseError!).ConfigureAwait(false);
            var messages = node is JsonArray arr ? arr.OfType<JsonObject>().ToList() : node is JsonObject o ? new List<JsonObject> { o } : new List<JsonObject>();
            if (messages.Count == 0) return await WriteJson(stream, 400, JsonRpc.Error(null, JsonRpc.InvalidRequest, "Invalid request: expected a JSON-RPC message.")).ConfigureAwait(false);

            McpSession session;
            var extraHeaders = new List<(string, string)>();
            bool initialize = messages.Count == 1 && messages[0]["method"]?.GetValue<string>() == "initialize";
            if (initialize)
            {
                session = new McpSession();
                sessions[session.Id] = session;
                extraHeaders.Add(("Mcp-Session-Id", session.Id));
            }
            else
            {
                string? sid = r.Header("Mcp-Session-Id");
                if (sid == null) return await WriteJson(stream, 400, JsonRpc.Error(null, JsonRpc.InvalidRequest, "Missing Mcp-Session-Id header: send initialize first.")).ConfigureAwait(false);
                if (!sessions.TryGetValue(sid, out session!)) return await WriteJson(stream, 404, JsonRpc.Error(null, JsonRpc.InvalidRequest, "Unknown or expired session: initialize again.")).ConfigureAwait(false);
            }

            var requests = messages.Where(JsonRpc.IsRequest).ToList();
            if (requests.Count == 0)
            {
                foreach (var m in messages) await server.HandleAsync(m, session, _ => Task.CompletedTask, ct).ConfigureAwait(false);
                return await Write(stream, 202, null, Array.Empty<byte>()).ConfigureAwait(false);
            }

            bool sse = (r.Header("Accept") ?? "").Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
                       && requests.Any(m => m["params"]?["_meta"]?["progressToken"] != null);
            if (!sse)
            {
                var responses = new List<JsonObject>();
                foreach (var m in messages)
                {
                    var resp = await server.HandleAsync(m, session, _ => Task.CompletedTask, ct).ConfigureAwait(false);
                    if (resp != null) responses.Add(resp);
                }
                JsonNode body = node is JsonArray ? new JsonArray(responses.Select(x => (JsonNode)x).ToArray()) : responses[0];
                return await WriteJson(stream, 200, body, extraHeaders).ConfigureAwait(false);
            }

            // SSE: progress notifications as they come, then the response; then the stream ends.
            var head = new StringBuilder();
            head.Append("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n");
            foreach (var (k, v) in extraHeaders) head.Append(k).Append(": ").Append(v).Append("\r\n");
            head.Append("\r\n");
            var writeLock = new SemaphoreSlim(1, 1);
            async Task Event(JsonNode message)
            {
                byte[] bytes = Encoding.UTF8.GetBytes("event: message\ndata: " + JsonRpc.Serialize(message) + "\n\n");
                await writeLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await stream.WriteAsync(bytes).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                finally
                {
                    writeLock.Release();
                }
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
            foreach (var m in messages)
            {
                var resp = await server.HandleAsync(m, session, n => Event(n), ct).ConfigureAwait(false);
                if (resp != null) await Event(resp).ConfigureAwait(false);
            }
            return false;
        }

        static Task<bool> WriteJson(NetworkStream stream, int status, JsonNode body, IEnumerable<(string, string)>? headers = null) =>
            Write(stream, status, "application/json", Encoding.UTF8.GetBytes(JsonRpc.Serialize(body)), headers);

        static Task<bool> WriteText(NetworkStream stream, int status, string text, bool close = false) =>
            Write(stream, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), close ? new[] { ("Connection", "close") } : null);

        static async Task<bool> Write(NetworkStream stream, int status, string? contentType, byte[] body, IEnumerable<(string, string)>? headers = null)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
            if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            bool close = false;
            if (headers != null)
                foreach (var (k, v) in headers)
                {
                    sb.Append(k).Append(": ").Append(v).Append("\r\n");
                    if (k == "Connection" && v == "close") close = true;
                }
            sb.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString())).ConfigureAwait(false);
            if (body.Length > 0) await stream.WriteAsync(body).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            return !close;
        }

        static string Reason(int status) => status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            413 => "Payload Too Large",
            431 => "Request Header Fields Too Large",
            _ => "Error",
        };

        /// <summary>Reads lines and exact byte counts from a stream with its own buffer.</summary>
        sealed class BufferedReader
        {
            readonly Stream stream;
            readonly byte[] buffer = new byte[16 * 1024];
            int start, end;

            public BufferedReader(Stream stream) => this.stream = stream;

            async Task<bool> Fill(CancellationToken ct)
            {
                if (start > 0 && start == end) start = end = 0;
                if (end == buffer.Length)
                {
                    Array.Copy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }
                int n = await stream.ReadAsync(buffer.AsMemory(end, buffer.Length - end), ct).ConfigureAwait(false);
                if (n <= 0) return false;
                end += n;
                return true;
            }

            /// <summary>A line without its CRLF; null at end of stream.</summary>
            public async Task<string?> ReadLine(int limit, CancellationToken ct)
            {
                var line = new MemoryStream();
                while (true)
                {
                    for (int i = start; i < end; i++)
                    {
                        if (buffer[i] != (byte)'\n') continue;
                        line.Write(buffer, start, i - start);
                        start = i + 1;
                        var bytes = line.ToArray();
                        int len = bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
                        return Encoding.ASCII.GetString(bytes, 0, len);
                    }
                    line.Write(buffer, start, end - start);
                    start = end;
                    if (line.Length > limit) throw new HttpError(431, "Line too long");
                    if (!await Fill(ct).ConfigureAwait(false)) return line.Length == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
                }
            }

            public async Task<byte[]> ReadBytes(int count, CancellationToken ct)
            {
                var result = new byte[count];
                int got = 0;
                while (got < count)
                {
                    if (start == end && !await Fill(ct).ConfigureAwait(false)) throw new IOException("Connection closed mid-body");
                    int take = Math.Min(count - got, end - start);
                    Array.Copy(buffer, start, result, got, take);
                    start += take;
                    got += take;
                }
                return result;
            }
        }
    }
}
