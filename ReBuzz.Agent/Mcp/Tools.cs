// MCP server core (docs/specs/mcp-server.md §4–5): tools, prompts and resources as data plus a
// handler. Tool results carry structured content (validated by the declared output schema in
// tests), a short text summary, and optional images or resource links.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuzz.Agent.Mcp
{
    /// <summary>MCP tool annotations (hints for clients; §4 rule 6).</summary>
    public sealed record ToolAnnotations(bool ReadOnly = false, bool Destructive = false, bool Idempotent = false, bool OpenWorld = false)
    {
        public static readonly ToolAnnotations Read = new(ReadOnly: true, Idempotent: true);
        public static readonly ToolAnnotations Write = new();
        public static readonly ToolAnnotations WriteIdempotent = new(Idempotent: true);
        public static readonly ToolAnnotations Delete = new(Destructive: true);

        public JsonObject ToJson(string title) => new()
        {
            ["title"] = title,
            ["readOnlyHint"] = ReadOnly,
            ["destructiveHint"] = Destructive,
            ["idempotentHint"] = Idempotent,
            ["openWorldHint"] = OpenWorld,
        };
    }

    /// <summary>What a tool handler gets: its arguments, cancellation, progress and logging.</summary>
    public sealed class ToolContext
    {
        public ToolContext(string tool, JsonObject arguments, CancellationToken cancellation, Action<double, double?, string?>? progress = null)
        {
            Tool = tool;
            Arguments = arguments;
            Cancellation = cancellation;
            this.progress = progress;
        }

        readonly Action<double, double?, string?>? progress;

        public string Tool { get; }
        public JsonObject Arguments { get; }
        public CancellationToken Cancellation { get; }

        /// <summary>
        /// Fork (ux-overhaul.md P4a): the caller keeps its own thread free (the studio's Export Audio sheet): a tool that can, runs its long
        /// work on a worker thread of its own instead of the engine thread. The song is then not safe to edit meanwhile: the caller blocks edits.
        /// </summary>
        public bool Background { get; init; }

        /// <summary>Reports progress when the client asked for it (a progress token); otherwise does nothing.</summary>
        public void Progress(double done, double? total = null, string? message = null) => progress?.Invoke(done, total, message);

        public string? String(string name) => Arguments[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

        public string RequiredString(string name) =>
            String(name) ?? throw new ToolError($"Missing \"{name}\".");

        public double? Number(string name) => Schema.TryNumber(Arguments[name], out double d) ? d : null;

        public int? Int(string name) => Number(name) is double d ? (int)Math.Round(d) : null;

        public bool? Bool(string name) => Arguments[name] is JsonValue v && v.TryGetValue(out bool b) ? b : null;

        public JsonArray? List(string name) => Arguments[name] as JsonArray;

        public JsonObject? Object(string name) => Arguments[name] as JsonObject;
    }

    /// <summary>A tool failure the agent can act on: becomes a result with isError and this message.</summary>
    public sealed class ToolError : Exception
    {
        public ToolError(string message) : base(message) { }
    }

    public sealed partial class ToolResult
    {
        public JsonObject? Structured { get; init; }
        public string Summary { get; init; } = "";
        public bool IsError { get; init; }
        /// <summary>Extra content blocks (images, resource links) after the text.</summary>
        public List<JsonObject> Extra { get; } = new();

        public static ToolResult Ok(string summary, JsonObject? structured = null) => new() { Summary = summary, Structured = structured };
        public static ToolResult Fail(string message) => new() { Summary = message, IsError = true };

        public ToolResult WithImage(byte[] png, string mime = "image/png")
        {
            Extra.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = mime });
            return this;
        }

        public ToolResult WithLink(string uri, string name, string mime, string? description = null)
        {
            var link = new JsonObject { ["type"] = "resource_link", ["uri"] = uri, ["name"] = name, ["mimeType"] = mime };
            if (description != null) link["description"] = description;
            Extra.Add(link);
            return this;
        }

        public JsonObject ToJson()
        {
            // JSON has no infinities or NaN; a result that holds one would fail to serialize (§12.8).
            if (Structured != null) Finite(Structured);
            var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = Summary } };
            if (Structured != null)
                content.Add(new JsonObject { ["type"] = "text", ["text"] = JsonRpc.Serialize(Structured) });
            foreach (var e in Extra) content.Add(e.DeepClone());
            var o = new JsonObject { ["content"] = content, ["isError"] = IsError };
            if (Structured != null) o["structuredContent"] = Structured.DeepClone();
            return o;
        }
    }

    public sealed partial class ToolResult
    {
        /// <summary>Replaces non-finite numbers in place: "inf" and "-inf" as text, NaN as null.</summary>
        static void Finite(JsonNode node)
        {
            if (node is JsonObject o)
            {
                foreach (var key in o.Select(kv => kv.Key).ToList())
                    if (o[key] is JsonNode child && !Replace(child, r => o[key] = r)) Finite(child);
            }
            else if (node is JsonArray a)
            {
                for (int i = 0; i < a.Count; i++)
                    if (a[i] is JsonNode child && !Replace(child, r => a[i] = r)) Finite(child);
            }
        }

        static bool Replace(JsonNode n, Action<JsonNode?> set)
        {
            if (n is not JsonValue v) return false;
            double d;
            if (v.TryGetValue(out double dv)) d = dv;
            else if (v.TryGetValue(out float fv)) d = fv;
            else return false;
            if (double.IsFinite(d)) return false;
            set(double.IsNaN(d) ? null : JsonValue.Create(d > 0 ? "inf" : "-inf"));
            return true;
        }
    }

    public sealed class ToolDef
    {
        public required string Name { get; init; }
        public required string Title { get; init; }
        /// <summary>What it does, when to use it, what it returns, one example (§4 rule 2).</summary>
        public required string Description { get; init; }
        public required JsonObject Input { get; init; }
        public JsonObject? Output { get; init; }
        public ToolAnnotations Annotations { get; init; } = ToolAnnotations.Write;
        public required Func<ToolContext, Task<ToolResult>> Handler { get; init; }

        public JsonObject ToJson()
        {
            var o = new JsonObject
            {
                ["name"] = Name,
                ["title"] = Title,
                ["description"] = Description,
                ["inputSchema"] = Input.DeepClone(),
                ["annotations"] = Annotations.ToJson(Title),
            };
            if (Output != null) o["outputSchema"] = Output.DeepClone();
            return o;
        }
    }

    public sealed class ToolRegistry
    {
        readonly List<ToolDef> tools = new();

        public IReadOnlyList<ToolDef> All => tools;

        public void Add(ToolDef tool)
        {
            if (tools.Any(t => t.Name == tool.Name)) throw new InvalidOperationException($"Tool {tool.Name} registered twice");
            tools.Add(tool);
        }

        public ToolDef? Find(string name) => tools.FirstOrDefault(t => t.Name == name);

        /// <summary>The closest tool names, for "did you mean" errors.</summary>
        public IEnumerable<string> Similar(string name) =>
            tools.Select(t => (t.Name, Score: Text.Distance(t.Name, name))).OrderBy(x => x.Score).Take(3).Where(x => x.Score <= Math.Max(3, name.Length / 2)).Select(x => x.Name);
    }

    public sealed class PromptArgument
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public bool Required { get; init; }
    }

    public sealed class PromptDef
    {
        public required string Name { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public IReadOnlyList<PromptArgument> Arguments { get; init; } = System.Array.Empty<PromptArgument>();
        /// <summary>Builds the prompt's user message from its arguments.</summary>
        public required Func<IReadOnlyDictionary<string, string>, string> Render { get; init; }
    }

    public sealed class ResourceDef
    {
        public required string Uri { get; init; }
        public required string Name { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public string MimeType { get; init; } = "text/markdown";
        public required Func<string> Read { get; init; }
    }

    /// <summary>Small text helpers shared by errors ("did you mean").</summary>
    public static class Text
    {
        /// <summary>Levenshtein distance, case-insensitive.</summary>
        public static int Distance(string a, string b)
        {
            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        /// <summary>The candidates closest to <paramref name="name"/> (substring matches first).</summary>
        public static List<string> Closest(string name, IEnumerable<string> candidates, int count = 3)
        {
            string n = name.ToLowerInvariant();
            return candidates
                .Distinct()
                .Select(c => (c, score: c.ToLowerInvariant().Contains(n) || n.Contains(c.ToLowerInvariant()) ? 0 : Distance(c, name)))
                .Where(x => x.score <= Math.Max(3, name.Length / 2))
                .OrderBy(x => x.score)
                .Take(count)
                .Select(x => x.c)
                .ToList();
        }
    }
}
