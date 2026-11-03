// MCP server core (docs/specs/mcp-server.md §3): the protocol. Handles one JSON-RPC message at a
// time for a session: the lifecycle (initialize, version negotiation), tools, prompts,
// resources, ping, logging level, cancellation and progress. Transports (stdio, Streamable
// HTTP) feed it messages and deliver what it returns or sends.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuzz.Agent.Mcp
{
    public sealed class ServerInfo
    {
        public required string Name { get; init; }
        public required string Title { get; init; }
        public required string Version { get; init; }
        /// <summary>Shown to the agent at the start of a session (how to begin, which tools first).</summary>
        public string Instructions { get; init; } = "";
    }

    /// <summary>One client connection's state.</summary>
    public sealed class McpSession
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string? ProtocolVersion { get; internal set; }
        public bool Initialized { get; internal set; }
        public JsonObject? ClientInfo { get; internal set; }
        public JsonObject? ClientCapabilities { get; internal set; }
        public string LogLevel { get; internal set; } = "info";
        internal readonly ConcurrentDictionary<string, CancellationTokenSource> InFlight = new();
    }

    /// <summary>One finished tool call, for the agent log.</summary>
    public sealed record ToolCallRecord(string Tool, JsonObject Arguments, bool IsError, string Summary, TimeSpan Duration);

    /// <summary>A binary or text resource produced on demand (renders, plots…).</summary>
    public sealed record ResourceContent(string Uri, string MimeType, string? Text = null, byte[]? Blob = null);

    public sealed class McpServer
    {
        /// <summary>Protocol versions this server speaks, newest first.</summary>
        public static readonly string[] SupportedVersions = { "2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26" };

        readonly ServerInfo info;
        readonly ToolRegistry tools;
        readonly List<PromptDef> prompts;
        readonly List<ResourceDef> resources;
        readonly List<Func<string, ResourceContent?>> resolvers = new();

        public McpServer(ServerInfo info, ToolRegistry tools, IEnumerable<PromptDef>? prompts = null, IEnumerable<ResourceDef>? resources = null)
        {
            this.info = info;
            this.tools = tools;
            this.prompts = prompts?.ToList() ?? new List<PromptDef>();
            this.resources = resources?.ToList() ?? new List<ResourceDef>();
        }

        public ServerInfo Info => info;
        public ToolRegistry Tools => tools;

        /// <summary>Raised after every tool call (the agent log).</summary>
        public event Action<ToolCallRecord>? ToolCalled;

        /// <summary>Adds a resolver for resources made on demand (e.g. rebuzz://renders/…).</summary>
        public void AddResolver(Func<string, ResourceContent?> resolver) => resolvers.Add(resolver);

        /// <summary>
        /// Handles one message. Returns the response for a request, null for a notification or a
        /// response. <paramref name="notify"/> sends notifications (progress) while a request runs.
        /// </summary>
        public async Task<JsonObject?> HandleAsync(JsonObject message, McpSession session, Func<JsonObject, Task> notify, CancellationToken cancellation = default)
        {
            if (JsonRpc.IsResponse(message)) return null;   // we send no requests yet
            if (message["jsonrpc"]?.GetValue<string>() != "2.0")
                return JsonRpc.Error(message["id"], JsonRpc.InvalidRequest, "Invalid request: \"jsonrpc\" must be \"2.0\".");
            string? method = message["method"] is JsonValue mv && mv.TryGetValue(out string? m) ? m : null;
            if (method == null) return JsonRpc.Error(message["id"], JsonRpc.InvalidRequest, "Invalid request: no method.");
            var p = message["params"] as JsonObject ?? new JsonObject();

            if (JsonRpc.IsNotification(message))
            {
                HandleNotification(method, p, session);
                return null;
            }

            var id = message["id"];
            string key = id!.ToJsonString();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            session.InFlight[key] = cts;
            try
            {
                var result = await Dispatch(method, p, session, notify, cts.Token).ConfigureAwait(false);
                return JsonRpc.Response(id, result);
            }
            catch (RpcException e)
            {
                return JsonRpc.Error(id, e.Code, e.Message, e.Data2);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return JsonRpc.Error(id, JsonRpc.RequestCancelled, "Request cancelled.");
            }
            catch (Exception e)
            {
                return JsonRpc.Error(id, JsonRpc.InternalError, "Internal error: " + e.Message);
            }
            finally
            {
                session.InFlight.TryRemove(key, out _);
            }
        }

        void HandleNotification(string method, JsonObject p, McpSession session)
        {
            switch (method)
            {
                case "notifications/initialized":
                    session.Initialized = true;
                    break;
                case "notifications/cancelled":
                    if (p["requestId"] is JsonNode rid && session.InFlight.TryGetValue(rid.ToJsonString(), out var cts))
                        cts.Cancel();
                    break;
            }
        }

        async Task<JsonNode> Dispatch(string method, JsonObject p, McpSession session, Func<JsonObject, Task> notify, CancellationToken ct)
        {
            switch (method)
            {
                case "initialize": return Initialize(p, session);
                case "ping": return new JsonObject();
                case "tools/list": return new JsonObject { ["tools"] = new JsonArray(tools.All.Select(t => (JsonNode)t.ToJson()).ToArray()) };
                case "tools/call": return await CallTool(p, notify, ct).ConfigureAwait(false);
                case "prompts/list": return ListPrompts();
                case "prompts/get": return GetPrompt(p);
                case "resources/list": return ListResources();
                case "resources/templates/list": return new JsonObject { ["resourceTemplates"] = new JsonArray() };
                case "resources/read": return ReadResource(p);
                case "logging/setLevel":
                    session.LogLevel = p["level"]?.GetValue<string>() ?? session.LogLevel;
                    return new JsonObject();
                default:
                    throw new RpcException(JsonRpc.MethodNotFound, $"Method not found: {method}");
            }
        }

        JsonNode Initialize(JsonObject p, McpSession session)
        {
            string requested = p["protocolVersion"]?.GetValue<string>() ?? SupportedVersions[0];
            // Answer with the client's version when we speak it, else our newest (the client decides).
            session.ProtocolVersion = SupportedVersions.Contains(requested) ? requested : SupportedVersions[0];
            session.ClientInfo = p["clientInfo"]?.DeepClone() as JsonObject;
            session.ClientCapabilities = p["capabilities"]?.DeepClone() as JsonObject;
            var result = new JsonObject
            {
                ["protocolVersion"] = session.ProtocolVersion,
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject { ["listChanged"] = false },
                    ["prompts"] = new JsonObject { ["listChanged"] = false },
                    ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false },
                    ["logging"] = new JsonObject(),
                },
                ["serverInfo"] = new JsonObject { ["name"] = info.Name, ["title"] = info.Title, ["version"] = info.Version },
            };
            if (info.Instructions.Length > 0) result["instructions"] = info.Instructions;
            return result;
        }

        async Task<JsonNode> CallTool(JsonObject p, Func<JsonObject, Task> notify, CancellationToken ct)
        {
            string name = p["name"] is JsonValue nv && nv.TryGetValue(out string? n) ? n : "";
            var tool = tools.Find(name);
            if (tool == null)
            {
                var similar = tools.Similar(name).ToList();
                string hint = similar.Count > 0 ? $" Did you mean {string.Join(" or ", similar.Select(s => $"\"{s}\""))}?" : "";
                throw new RpcException(JsonRpc.InvalidParams, $"Unknown tool \"{name}\".{hint} tools/list lists every tool.");
            }
            var args = p["arguments"] as JsonObject ?? new JsonObject();
            var problems = new List<string>();
            Schema.Validate(tool.Input, args, "", problems);
            var sw = Stopwatch.StartNew();
            ToolResult result;
            if (problems.Count > 0)
            {
                result = ToolResult.Fail($"Invalid arguments for {tool.Name}: {string.Join("; ", problems)}. See the tool's input schema.");
            }
            else
            {
                JsonNode? token = p["_meta"]?["progressToken"];
                Action<double, double?, string?>? progress = null;
                if (token != null)
                    progress = (done, total, msg) =>
                    {
                        var np = new JsonObject { ["progressToken"] = token.DeepClone(), ["progress"] = done };
                        if (total.HasValue) np["total"] = total.Value;
                        if (msg != null) np["message"] = msg;
                        _ = notify(JsonRpc.Notification("notifications/progress", np));
                    };
                try
                {
                    result = await tool.Handler(new ToolContext(tool.Name, args, ct, progress)).ConfigureAwait(false);
                }
                catch (ToolError e)
                {
                    result = ToolResult.Fail(e.Message);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    result = ToolResult.Fail($"{tool.Name} failed inside ReBuzz: {Describe(e)}. The song may be partly changed; studio_status shows what undo would revert.");
                }
            }
            ToolCalled?.Invoke(new ToolCallRecord(tool.Name, args, result.IsError, result.Summary, sw.Elapsed));
            return result.ToJson();
        }

        /// <summary>
        /// An exception as the agent reads it, through wrappers (a machine's constructor called by
        /// reflection throws TargetInvocationException) to the cause and where it came from, so a
        /// machine that needs a window says so (docs/specs/machine-safety.md §3.1: WPF types).
        /// </summary>
        internal static string Describe(Exception e)
        {
            string text = $"{e.GetType().Name}: {e.Message}";
            var inner = e;
            for (int depth = 0; depth < 6 && (inner is System.Reflection.TargetInvocationException || inner is AggregateException || inner is TypeInitializationException) && inner.InnerException != null; depth++)
            {
                inner = inner.InnerException!;
                text += $" → {inner.GetType().FullName}: {inner.Message}";
            }
            if (inner != e && inner.TargetSite?.DeclaringType is Type where)
                text += $" (in {where.FullName}.{inner.TargetSite.Name})";
            return text;
        }

        JsonNode ListPrompts() => new JsonObject
        {
            ["prompts"] = new JsonArray(prompts.Select(pr => (JsonNode)new JsonObject
            {
                ["name"] = pr.Name,
                ["title"] = pr.Title,
                ["description"] = pr.Description,
                ["arguments"] = new JsonArray(pr.Arguments.Select(a => (JsonNode)new JsonObject
                {
                    ["name"] = a.Name,
                    ["description"] = a.Description,
                    ["required"] = a.Required,
                }).ToArray()),
            }).ToArray()),
        };

        JsonNode GetPrompt(JsonObject p)
        {
            string name = p["name"]?.GetValue<string>() ?? "";
            var prompt = prompts.FirstOrDefault(x => x.Name == name)
                ?? throw new RpcException(JsonRpc.InvalidParams, $"Unknown prompt \"{name}\". prompts/list lists them.");
            var args = new Dictionary<string, string>();
            if (p["arguments"] is JsonObject a)
                foreach (var (k, v) in a) args[k] = v?.ToString() ?? "";
            foreach (var arg in prompt.Arguments)
                if (arg.Required && !args.ContainsKey(arg.Name))
                    throw new RpcException(JsonRpc.InvalidParams, $"Prompt \"{name}\" needs the argument \"{arg.Name}\": {arg.Description}");
            return new JsonObject
            {
                ["description"] = prompt.Description,
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt.Render(args) },
                }),
            };
        }

        JsonNode ListResources() => new JsonObject
        {
            ["resources"] = new JsonArray(resources.Select(r => (JsonNode)new JsonObject
            {
                ["uri"] = r.Uri,
                ["name"] = r.Name,
                ["title"] = r.Title,
                ["description"] = r.Description,
                ["mimeType"] = r.MimeType,
            }).ToArray()),
        };

        JsonNode ReadResource(JsonObject p)
        {
            string uri = p["uri"]?.GetValue<string>() ?? "";
            var r = resources.FirstOrDefault(x => x.Uri == uri);
            ResourceContent? content = r != null ? new ResourceContent(r.Uri, r.MimeType, r.Read()) : null;
            if (content == null)
                foreach (var resolve in resolvers)
                    if ((content = resolve(uri)) != null) break;
            if (content == null)
                throw new RpcException(-32002, $"Resource not found: {uri}. resources/list lists the guides; renders are named in render results.");
            var c = new JsonObject { ["uri"] = content.Uri, ["mimeType"] = content.MimeType };
            if (content.Blob != null) c["blob"] = Convert.ToBase64String(content.Blob);
            else c["text"] = content.Text ?? "";
            return new JsonObject { ["contents"] = new JsonArray(c) };
        }
    }
}
