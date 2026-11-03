// Fork (docs/specs/studio-mvp.md S8): the MCP server in studio mode, as ReBuzz.exe's StudioAgent
// (ReBuzz/Agent/StudioAgent.cs, docs/specs/mcp-server.md §3.1): Preferences ▸ Agent ▸ Allow agents
// serves Streamable HTTP on 127.0.0.1 with a bearer token; the page shows the `claude mcp add`
// line (Command/Ctrl+Shift+A opens it); the status line shows the agent's current call. The tools
// run on the UI thread (the engine's dispatcher) in "studio" mode, so the live tools play through
// the studio's audio device, and the views follow the engine's events as they do for the user.

using System;
using System.IO;
using System.Net.Sockets;
using BuzzGUI.Common.Settings;
using ReBuzz.Agent;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core.Settings;

namespace ReBuzz.Studio.Shell
{
    /// <summary>
    /// The agent settings, as ReBuzz.exe's (same class name, so the same place in the settings:
    /// BuzzGUI\AgentSettings).
    /// </summary>
    public class AgentSettings : BuzzGUI.Common.Settings.Settings
    {
        [BuzzSetting(false, Description = "Let AI agents (Claude Code or any MCP client) work in this ReBuzz over a local connection (127.0.0.1 only, with a token).")]
        public bool AllowAgents { get; set; }

        [BuzzSetting("7481", Description = "The local port agents connect to.")]
        public string Port { get; set; }
    }

    internal sealed class AgentServer : IDisposable
    {
        public const int DefaultPort = 7481;

        readonly StudioHost studio;
        readonly ISettingsStore store;
        HttpTransport transport;
        string listening = "";
        string current;

        public AgentServer(StudioHost studio)
        {
            this.studio = studio;
            store = new JsonSettingsStore(Path.Combine(studio.SettingsFolder, "settings.json"));
            Host = new AgentHost(studio.Core, studio.Services.Dispatcher, "studio", Path.Combine(studio.SettingsFolder, "agent-logs"));
            Host.Queue.CurrentChanged += name => studio.Services.Dispatcher.BeginInvoke(() =>
            {
                current = name;
                Changed?.Invoke();
            });
            Settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(AgentSettings.AllowAgents) or nameof(AgentSettings.Port)) Apply();
            };
            Apply();
        }

        public AgentHost Host { get; }

        public AgentSettings Settings { get; } = new AgentSettings();

        /// <summary>The listening state or the call running changed (UI thread).</summary>
        public event Action Changed;

        public bool Listening => transport != null;

        /// <summary>For the status line: the call running, else where it listens (or why it can't); "" when off.</summary>
        public string Status => current != null ? $"Agent: {current}…" : listening;

        public int PortNumber => int.TryParse(Settings.Port, out int p) && p > 0 && p < 65536 ? p : DefaultPort;

        public string Url => transport?.Url ?? $"http://127.0.0.1:{PortNumber}/mcp";

        /// <summary>The bearer token, made on first use and kept in the settings folder's settings.json.</summary>
        public string Token
        {
            get
            {
                string t = store.Get("agent", "token");
                if (string.IsNullOrEmpty(t))
                {
                    t = HttpTransport.NewToken();
                    store.Set("agent", "token", t);
                }
                return t;
            }
        }

        /// <summary>The line to paste into a terminal.</summary>
        public string ConnectCommand => $"claude mcp add --transport http rebuzz {Url} --header \"Authorization: Bearer {Token}\"";

        public string LogFolder => Path.GetDirectoryName(Host.Log.FilePath);

        /// <summary>Listens or stops as the settings say.</summary>
        void Apply()
        {
            Host.StopHttp();
            transport = null;
            listening = "";
            if (Settings.AllowAgents)
            {
                try
                {
                    transport = Host.StartHttp(PortNumber, Token);
                    listening = $"Agent: listening on 127.0.0.1:{transport.Port}";
                }
                catch (SocketException e)
                {
                    listening = $"Agent: port {PortNumber} is in use ({e.SocketErrorCode}); choose another in Preferences ▸ Agent";
                }
            }
            studio.Log(listening.Length > 0 ? listening : "Agent: off");
            Changed?.Invoke();
        }

        /// <summary>A new token: clients with the old one are refused from now on.</summary>
        public void NewToken()
        {
            store.Set("agent", "token", HttpTransport.NewToken());
            Apply();
        }

        /// <summary>Allow agents on or off, and the port, saved as the settings page saves them.</summary>
        public void Set(bool allow, string port = null)
        {
            if (port != null) Settings.Set(nameof(AgentSettings.Port), port.Trim());
            Settings.Set(nameof(AgentSettings.AllowAgents), allow);
            Settings.Save();
        }

        public void Dispose() => Host.Dispose();
    }
}
