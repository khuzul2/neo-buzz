// Fork: the lines that connect Claude Code to the app's MCP server, one source for ReBuzz.exe's agent
// connection window (Ctrl+Shift+A), the studio's Preferences ▸ Agent and rebuzz-headless's help. The server
// is registered under the app's MCP name (Product.McpName), so its tools show as mcp__neobuzz-live__<tool>.

namespace ReBuzz.Agent.Mcp
{
    public static class ClaudeCode
    {
        /// <summary>The app's server over Streamable HTTP, with its bearer token (studio mode).</summary>
        public static string HttpCommand(string url, string token) =>
            $"claude mcp add --transport http {Product.McpName} {url} --header \"Authorization: Bearer {token}\"";

        /// <summary>
        /// A server Claude Code starts itself, on stdio: <c>NeoBuzzLive.exe --agent</c> or <c>rebuzz-headless --agent</c>.
        /// A path is quoted (it may hold spaces, and backslashes a shell would eat); a bare command name isn't.
        /// </summary>
        public static string StdioCommand(string executable) =>
            $"claude mcp add {Product.McpName} -- {Quote(executable)} --agent";

        static string Quote(string path) => path.IndexOfAny(new[] { ' ', '\t', '\\', '/' }) >= 0 ? "\"" + path + "\"" : path;
    }
}
