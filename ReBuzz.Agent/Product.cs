// Fork: the app's name as people and agents see it. NeoBuzz Live is a fork of ReBuzz (MIT, WDE and its
// contributors); what it shows (window titles, menus, dialogs, the About box, the console's lines, the MCP
// server's name and its `claude mcp add` lines, the Mac app and the Windows executable) says NeoBuzz Live.
// The code keeps ReBuzz's names: namespaces, assemblies and projects, the settings, autosave and agent-log
// folders, registry keys, file formats (.bmx, .bmxml) and machine names saved in songs ("ReBuzz Audio In",
// "ReBuzz Groove", …), so songs, settings, machines and plugins carry over unchanged.
//
// In ReBuzz.Agent because every app and test project references it (the engine, ReBuzz.exe, the studio,
// rebuzz-headless, ReBuzz.AgentEval); ReBuzz.SessionTests links this file.

namespace ReBuzz
{
    public static class Product
    {
        /// <summary>The app's name: window titles, menus, dialogs, the About box, the MCP server's title.</summary>
        public const string Name = "NeoBuzz Live";

        /// <summary>The MCP server's name (serverInfo.name), and the name `claude mcp add` registers it under.</summary>
        public const string McpName = "neobuzz-live";

        /// <summary>
        /// The Windows app's executable: the apphost of ReBuzz.csproj under this name (its assembly stays ReBuzz.dll,
        /// which machines, plugins and tests load by name).
        /// </summary>
        public const string WindowsExe = "NeoBuzzLive.exe";

        /// <summary>What NeoBuzz Live is based on: ReBuzz, by WDE and its contributors (MIT licence).</summary>
        public const string Upstream = "ReBuzz";
        public const string UpstreamAuthors = "WDE and the ReBuzz contributors";
        public const string UpstreamUrl = "https://github.com/wasteddesign/ReBuzz";

        /// <summary>One line for the About boxes and READMEs: "Based on ReBuzz by WDE and the ReBuzz contributors (MIT): …".</summary>
        public const string BasedOn = "Based on " + Upstream + " by " + UpstreamAuthors + " (MIT licence): " + UpstreamUrl;

        /// <summary>The start of each line the apps write to the console (stderr): rebuzz-headless, --agent, the studio's log.</summary>
        public const string LogPrefix = "[" + Name + "] ";
    }
}
