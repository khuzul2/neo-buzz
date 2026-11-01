// MCP server core (docs/specs/mcp-server.md §3): JSON-RPC 2.0 message helpers. Messages are
// System.Text.Json nodes; nothing here knows about tools or ReBuzz.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReBuzz.Agent.Mcp
{
    public static class JsonRpc
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
        /// <summary>MCP: the request was cancelled by the client.</summary>
        public const int RequestCancelled = -32800;

        /// <summary>Compact, and without HTML escaping: text for agents reads as written ("don't", "→").</summary>
        public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public static JsonObject Response(JsonNode? id, JsonNode result) => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };

        public static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
        {
            var error = new JsonObject { ["code"] = code, ["message"] = message };
            if (data != null) error["data"] = data;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = error };
        }

        public static JsonObject Notification(string method, JsonObject? parameters = null)
        {
            var n = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (parameters != null) n["params"] = parameters;
            return n;
        }

        public static string Serialize(JsonNode node) => node.ToJsonString(Compact);

        /// <summary>Parses one message; null (and an error response) when it isn't JSON.</summary>
        public static JsonNode? TryParse(string text, out JsonObject? error)
        {
            error = null;
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException e)
            {
                error = Error(null, ParseError, "Parse error: " + e.Message);
                return null;
            }
        }

        public static bool IsRequest(JsonObject m) => m.ContainsKey("method") && m.ContainsKey("id") && m["id"] != null;
        public static bool IsNotification(JsonObject m) => m.ContainsKey("method") && (!m.ContainsKey("id") || m["id"] == null);
        public static bool IsResponse(JsonObject m) => !m.ContainsKey("method") && (m.ContainsKey("result") || m.ContainsKey("error"));
    }

    /// <summary>A JSON-RPC error raised inside a handler (becomes an error response).</summary>
    public sealed class RpcException : System.Exception
    {
        public RpcException(int code, string message, JsonNode? data = null) : base(message)
        {
            Code = code;
            Data2 = data;
        }

        public int Code { get; }
        public JsonNode? Data2 { get; }
    }
}
