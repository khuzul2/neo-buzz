// Evaluation (docs/specs/mcp-server.md §10): an MCP client over stdio, as a real client runs the
// server. Lifted from ReBuzzTests/AgentHeadlessTests.cs and made reusable: it starts
// `ReBuzz.exe --agent [song]` (or talks to any pair of streams), initializes, lists tools and
// calls them with structured results, isError and timeouts. It counts calls and errors, keeps a
// transcript, and when something goes wrong reports ReBuzz's stderr and, on Windows, the
// process's visible windows (a dialog nobody can answer shows up there).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuzz.AgentEval
{
    /// <summary>The protocol failed (no answer, a closed pipe, a JSON-RPC error): not a tool's isError.</summary>
    public sealed class McpException : Exception
    {
        public McpException(string message) : base(message) { }
    }

    /// <summary>One tools/call and what came back.</summary>
    public sealed class ToolCall
    {
        public string Tool { get; init; }
        public JsonObject Arguments { get; init; }
        /// <summary>The tool said isError (the agent would read Text and recover).</summary>
        public bool IsError { get; init; }
        /// <summary>The first text block: the one-paragraph summary, or the error.</summary>
        public string Text { get; init; } = "";
        /// <summary>structuredContent (an empty object when the tool returned none).</summary>
        public JsonObject Data { get; init; } = new();
        /// <summary>All content blocks.</summary>
        public JsonArray Content { get; init; } = new();
        public double Seconds { get; init; }
    }

    public sealed class McpClient : IDisposable
    {
        readonly Process process;
        readonly StreamReader reader;
        readonly Stream writer;
        readonly ConcurrentQueue<string> stderr = new();
        readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new();
        readonly List<string> protocolViolations = new();
        readonly object writeLock = new();
        readonly Task readLoop;
        int nextId;
        volatile bool closed;

        /// <summary>Starts <paramref name="exe"/> with <paramref name="args"/> and talks MCP over its stdio.</summary>
        public static McpClient Start(string exe, IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi) ?? throw new McpException($"Couldn't start {exe}.");
            return new McpClient(p);
        }

        McpClient(Process p)
        {
            process = p;
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data == null) return;
                stderr.Enqueue(e.Data);
                while (stderr.Count > 400) stderr.TryDequeue(out _);
                StderrLine?.Invoke(e.Data);
            };
            process.BeginErrorReadLine();
            reader = process.StandardOutput;
            writer = process.StandardInput.BaseStream;
            readLoop = Task.Run(ReadLoop);
        }

        /// <summary>A client over streams (an in-process server, for the self-test).</summary>
        public McpClient(Stream fromServer, Stream toServer)
        {
            reader = new StreamReader(fromServer, new UTF8Encoding(false));
            writer = toServer;
            readLoop = Task.Run(ReadLoop);
        }

        /// <summary>Each line ReBuzz writes to stderr (its start-up steps, captured dialogs).</summary>
        public event Action<string> StderrLine;

        /// <summary>Server notifications (progress, logging).</summary>
        public event Action<JsonObject> Notification;

        public JsonObject ServerInfo { get; private set; }
        public JsonObject ServerCapabilities { get; private set; }
        public string Instructions { get; private set; }

        /// <summary>Every tools/call so far, in order.</summary>
        public List<ToolCall> Transcript { get; } = new();
        public int Calls => Transcript.Count;
        public int ErrorCalls => Transcript.Count(c => c.IsError);

        /// <summary>stdout lines that weren't the protocol (stdout must carry only JSON-RPC).</summary>
        public IReadOnlyList<string> ProtocolViolations { get { lock (protocolViolations) return protocolViolations.ToList(); } }

        /// <summary>The last of ReBuzz's stderr and, on Windows, its visible windows: what to show when it fails.</summary>
        public string Diagnostics()
        {
            var sb = new StringBuilder();
            var lines = stderr.ToArray();
            sb.Append(string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 40))));
            if (process != null)
            {
                try { if (process.HasExited) sb.Append($"\n(process exited with {process.ExitCode})"); } catch (Exception) { }
                if (OperatingSystem.IsWindows()) sb.Append(Windows.Describe(process));
            }
            return sb.ToString();
        }

        async Task ReadLoop()
        {
            try
            {
                while (true)
                {
                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Trim().Length == 0) continue;
                    JsonNode node;
                    try { node = JsonNode.Parse(line); }
                    catch (Exception)
                    {
                        lock (protocolViolations) protocolViolations.Add(line.Length > 300 ? line[..300] + "…" : line);
                        continue;
                    }
                    if (node is JsonArray batch)
                    {
                        foreach (var m in batch.OfType<JsonObject>()) Route(m);
                        continue;
                    }
                    if (node is JsonObject o) Route(o);
                    else lock (protocolViolations) protocolViolations.Add(line);
                }
            }
            catch (Exception) { }
            finally
            {
                closed = true;
                foreach (var kv in pending) kv.Value.TrySetException(new McpException(Product.Name + " closed stdout."));
            }
        }

        void Route(JsonObject message)
        {
            if (message["id"] is JsonValue idv && idv.TryGetValue(out int id) && (message.ContainsKey("result") || message.ContainsKey("error")))
            {
                if (pending.TryRemove(id, out var tcs)) tcs.TrySetResult(message);
                return;
            }
            if (message["method"] != null && message["id"] == null) Notification?.Invoke(message);
        }

        void Send(JsonObject message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
            lock (writeLock)
            {
                writer.Write(bytes, 0, bytes.Length);
                writer.Flush();
            }
        }

        /// <summary>Sends a request and waits for its response; throws McpException on a timeout or a JSON-RPC error.</summary>
        public JsonObject Request(string method, JsonObject parameters, double seconds = 120)
        {
            if (closed) throw new McpException($"{method}: {Product.Name} has closed stdout.\n{Diagnostics()}");
            int id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() });
            JsonObject response;
            try
            {
                if (!tcs.Task.Wait(TimeSpan.FromSeconds(seconds)))
                {
                    pending.TryRemove(id, out _);
                    try { Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = id, ["reason"] = "timeout" } }); }
                    catch (Exception) { }
                    throw new McpException($"No answer to {method} in {seconds} s.\n{Diagnostics()}");
                }
                response = tcs.Task.Result;
            }
            catch (AggregateException e) when (e.InnerException is McpException m)
            {
                throw new McpException($"{method}: {m.Message}\n{Diagnostics()}");
            }
            if (response["error"] is JsonObject error)
                throw new McpException($"{method}: JSON-RPC error {error["code"]}: {error["message"]}");
            return response["result"] as JsonObject ?? new JsonObject();
        }

        /// <summary>Sends a request and returns the raw response, JSON-RPC errors included (for checks of error texts).</summary>
        public JsonObject RequestRaw(string method, JsonObject parameters, double seconds = 60)
        {
            int id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() });
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(seconds))) throw new McpException($"No answer to {method} in {seconds} s.\n{Diagnostics()}");
            return tcs.Task.Result;
        }

        /// <summary>initialize + notifications/initialized. The first answer waits for ReBuzz's start-up (gear scan).</summary>
        public JsonObject Initialize(double seconds = 240, string protocolVersion = "2025-06-18")
        {
            var r = Request("initialize", new JsonObject
            {
                ["protocolVersion"] = protocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "rebuzz-agent-eval", ["version"] = "1" },
            }, seconds);
            ServerInfo = r["serverInfo"] as JsonObject;
            ServerCapabilities = r["capabilities"] as JsonObject;
            Instructions = r["instructions"]?.GetValue<string>();
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            return r;
        }

        public JsonArray ListTools(double seconds = 60) => Request("tools/list", new JsonObject(), seconds)["tools"] as JsonArray ?? new JsonArray();

        /// <summary>Calls a tool; isError comes back in the result (it is the agent's to handle), protocol failures throw.</summary>
        public ToolCall Call(string tool, JsonObject args = null, double seconds = 300)
        {
            args ??= new JsonObject();
            var clock = Stopwatch.StartNew();
            var r = Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args.DeepClone() }, seconds);
            // Detached from the response, so callers can put the results into their own trees.
            var structured = r["structuredContent"] as JsonObject;
            r.Remove("structuredContent");
            var content = r["content"] as JsonArray ?? new JsonArray();
            var call = new ToolCall
            {
                Tool = tool,
                Arguments = args,
                IsError = r["isError"]?.GetValue<bool>() == true,
                Text = content.OfType<JsonObject>().FirstOrDefault(b => b["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>() ?? "",
                Data = structured ?? new JsonObject(),
                Content = content,
                Seconds = clock.Elapsed.TotalSeconds,
            };
            lock (Transcript) Transcript.Add(call);
            CallMade?.Invoke(call);
            return call;
        }

        /// <summary>Each finished tools/call (for progress output).</summary>
        public event Action<ToolCall> CallMade;

        /// <summary>Calls a tool that must succeed: an isError result throws with the tool's text.</summary>
        public JsonObject Ok(string tool, JsonObject args = null, double seconds = 300)
        {
            var c = Call(tool, args, seconds);
            if (c.IsError) throw new ToolFailed(tool, c.Text);
            return c.Data;
        }

        /// <summary>Closes stdin (the server's signal to exit) and waits.</summary>
        public bool CloseAndWait(int seconds)
        {
            try { writer.Close(); } catch (Exception) { }
            if (process == null) return true;   // a stream client: the server sees the end of its input
            return process.WaitForExit(seconds * 1000);
        }

        public void Dispose()
        {
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
                process.Dispose();
            }
            else
            {
                try { writer.Dispose(); } catch (Exception) { }
            }
        }

        /// <summary>The process's visible windows and their texts (Windows only).</summary>
        static class Windows
        {
            public static string Describe(Process process)
            {
                if (!OperatingSystem.IsWindows()) return "";
                var lines = new StringBuilder();
                try
                {
                    uint pid = (uint)process.Id;
                    EnumWindows((h, _) =>
                    {
                        GetWindowThreadProcessId(h, out uint owner);
                        if (owner != pid || !IsWindowVisible(h)) return true;
                        lines.Append("\nwindow: \"").Append(Text(h)).Append('"');
                        EnumChildWindows(h, (c, __) => { string t = Text(c); if (t.Length > 0) lines.Append(" | ").Append(t); return true; }, IntPtr.Zero);
                        return true;
                    }, IntPtr.Zero);
                }
                catch (Exception e) { lines.Append("\n(windows: ").Append(e.Message).Append(')'); }
                return lines.Length == 0 ? "\n(no visible windows)" : lines.ToString();
            }

            static string Text(IntPtr h)
            {
                var sb = new StringBuilder(512);
                GetWindowText(h, sb, sb.Capacity);
                return sb.ToString();
            }

            delegate bool EnumProc(IntPtr hwnd, IntPtr param);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr param);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr param);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
            [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
        }
    }

    /// <summary>A call that had to succeed returned isError.</summary>
    public sealed class ToolFailed : Exception
    {
        public ToolFailed(string tool, string text) : base($"{tool}: {text}") { Tool = tool; }
        public string Tool { get; }
    }
}
