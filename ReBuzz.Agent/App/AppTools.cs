// Agent server (docs/specs/agent-knowledge.md §5, K3): registers the app-control tools in one call, so the host's
// own registration is a single line. The tools are portable (they talk to IAppServices); see AppServices.cs.

using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.App
{
    public static class AppTools
    {
        public static void Register(ToolRegistry tools, IAppServices app)
        {
            SettingsTools.Register(tools, app);
            MidiTools.Register(tools, app);
            AudioTools.Register(tools, app);
            ViewTools.Register(tools, app);
            ExportTools.Register(tools, app);
            AppInfoTools.Register(tools, app);
        }
    }
}
