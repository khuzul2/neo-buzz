// Fork (docs/specs/studio-mvp.md S8): Preferences ▸ Agent, ReBuzz.exe's Settings ▸ Agent and its
// connection window (Ctrl+Shift+A) in one page: allow agents, the port, and the ready-to-paste
// `claude mcp add` line with its token, a copy button and a new-token button.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class AgentPage : PrefsPage
    {
        readonly AgentServer agent;
        readonly CheckBox allow = new CheckBox { Content = "Allow agents" };
        readonly TextBox port = new TextBox { Width = 90 };
        readonly TextBlock state = new TextBlock { TextWrapping = TextWrapping.Wrap };
        readonly TextBox command = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"),
            FontSize = 12,
        };
        readonly Button copy = new Button { Content = "Copy" };
        readonly Button renew = new Button { Content = "New token" };
        readonly TextBlock log = new TextBlock { Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap };

        public AgentPage(AgentServer agent, PrefsModel model) : base(model)
        {
            this.agent = agent;
            Heading("Agents");
            Row("", allow, $"AI agents (Claude Code or any MCP client) can then work in this {Product.Name} over a local connection: 127.0.0.1 only, with a token. " +
                           "They see and change the open song as you do, and hear it through your audio output; each change is one undo step.");
            Row("Port", port, $"The local port agents connect to (default {AgentServer.DefaultPort}).");
            Row("Now", state);

            Heading("Connect Claude Code");
            Note("Run this in a terminal:");
            Body.Children.Add(command);
            Body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, renew } });
            Note($"The token is a password for this {Product.Name}: anyone with it can change your song while {Product.Name} runs. A new token makes the old one stop working.");
            Body.Children.Add(log);

            allow.IsCheckedChanged += (s, e) =>
            {
                if (Filling) return;
                string before = agent.Allowed ? "True" : "False";
                agent.Set(allow.IsChecked == true, port.Text);
                Model?.Record("agent.AllowAgents", before, agent.Settings.AllowAgents ? "True" : "False");
                Refresh();
            };
            port.LostFocus += (s, e) => CommitPort();
            port.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) CommitPort();
            };
            copy.Click += async (s, e) =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null) await clipboard.SetTextAsync(agent.ConnectCommand);
            };
            renew.Click += (s, e) =>
            {
                agent.NewToken();
                Refresh();
            };
            agent.Changed += Refresh;
        }

        public override string Title => "Agent";

        internal CheckBox AllowBox => allow;
        internal TextBox PortBox => port;
        internal TextBox CommandBox => command;
        internal Button CopyButton => copy;
        internal Button NewTokenButton => renew;
        internal string StateText => state.Text;

        void CommitPort()
        {
            if (Filling || port.Text?.Trim() == agent.PortNumber.ToString()) return;
            string before = agent.Settings.Port;
            agent.Set(agent.Allowed, port.Text);
            Model?.Record("agent.Port", before, agent.Settings.Port);
            Refresh();
        }

        public override void Closed() => agent.Changed -= Refresh;

        /// <summary>Restore Defaults leaves the switch and the port alone: changing them ends the connection the agent works through.</summary>
        public override void RestoreOwn() { }

        public override void Refresh()
        {
            Filling = true;
            try
            {
                allow.IsChecked = agent.Allowed;
                if (!port.IsFocused) port.Text = agent.PortNumber.ToString();
                state.Text = agent.Listening ? $"Listening on {agent.Url}" : agent.Allowed ? agent.Status.Replace("Agent: ", "") : "Off: agents can't connect.";
                if (agent.Allowed && !agent.Listening) state.Foreground = Problem;
                else state.ClearValue(TextBlock.ForegroundProperty);
                command.Text = agent.ConnectCommand;
                command.Opacity = agent.Listening ? 1 : 0.5;
                log.Text = $"Agent log: {agent.LogFolder}";
            }
            finally
            {
                Filling = false;
            }
        }
    }
}
