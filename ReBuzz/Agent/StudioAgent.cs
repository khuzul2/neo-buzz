// Agent server (docs/specs/mcp-server.md §3.1): studio mode. Settings → Agent → Allow agents
// starts Streamable HTTP on 127.0.0.1 with a bearer token; a window shows the ready-to-paste
// `claude mcp add` line (Ctrl+Shift+A); the status bar shows the agent's current call.

using System;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Settings;

namespace ReBuzz.Agent
{
    public class AgentSettings : BuzzGUI.Common.Settings.Settings
    {
        [BuzzSetting(false, Description = "Let AI agents (Claude Code or any MCP client) work in this " + Product.Name + " over a local connection (127.0.0.1 only, with a token). Ctrl+Shift+A shows how to connect.")]
        public bool AllowAgents { get; set; }

        [BuzzSetting("7481", Description = "The local port agents connect to.")]
        public string Port { get; set; }
    }

    internal sealed class StudioAgent : IDisposable
    {
        readonly ReBuzzCore core;
        readonly IUiDispatcher dispatcher;
        readonly Action<string> status;
        readonly AgentSettings settings = new AgentSettings();
        readonly ISettingsStore store = new JsonSettingsStore(JsonSettingsStore.DefaultPath);
        readonly AgentHost host;
        HttpTransport transport;
        string listening = "";

        public StudioAgent(ReBuzzCore core, IUiDispatcher dispatcher, Action<string> status)
        {
            this.core = core;
            this.dispatcher = dispatcher;
            this.status = status;
            string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReBuzz", "agent-logs");
            host = new AgentHost(core, dispatcher, "studio", logs);
            host.Queue.CurrentChanged += name => dispatcher.BeginInvoke(() => status(name == null ? listening : $"Agent: {name}…"));
            SettingsWindow.AddSettings("Agent", settings);
            host.AppControl.AddSettings("agent", settings);     // K3: settings_get lists it
            settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(AgentSettings.AllowAgents) && e.PropertyName != nameof(AgentSettings.Port)) return;
                bool wasOn = transport != null;
                Apply();
                if (!wasOn && transport != null) ShowConnection();
            };
            Apply();
        }

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

        public string Url => transport?.Url ?? $"http://127.0.0.1:{PortNumber}/mcp";

        int PortNumber => int.TryParse(settings.Port, out int p) && p > 0 && p < 65536 ? p : 7481;

        void Apply()
        {
            host.StopHttp();
            transport = null;
            listening = "";
            if (settings.AllowAgents)
            {
                try
                {
                    transport = host.StartHttp(PortNumber, Token);
                    listening = $"Agent: listening on 127.0.0.1:{transport.Port}";
                }
                catch (SocketException e)
                {
                    listening = $"Agent: port {PortNumber} is in use ({e.SocketErrorCode}); choose another in Settings → Agent";
                }
            }
            status(listening);
        }

        public void NewToken()
        {
            store.Set("agent", "token", HttpTransport.NewToken());
            Apply();
        }

        /// <summary>The connection window (Ctrl+Shift+A).</summary>
        public void ShowConnection()
        {
            var window = new Window
            {
                Title = "Agent connection",
                Width = 640,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow,
                ResizeMode = ResizeMode.NoResize,
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            void Line(string text, double top = 0) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 4) });
            TextBox Command(string text)
            {
                var box = new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Padding = new Thickness(4) };
                panel.Children.Add(box);
                var copy = new Button { Content = "Copy", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 4, 0, 0) };
                copy.Click += (s, e) => Clipboard.SetText(box.Text);
                panel.Children.Add(copy);
                return box;
            }

            if (transport == null)
            {
                Line("Agents are off. Turn on Settings → Agent → AllowAgents, then open this window again (Ctrl+Shift+A).");
            }
            else
            {
                Line($"Connect Claude Code to this {Product.Name} (run in a terminal):");
                var command = Command(ClaudeCode.HttpCommand(Url, Token));
                Line($"The token is a password for this {Product.Name}: anyone with it can change your song while {Product.Name} runs. New token makes old ones stop working.", 10);
                var renew = new Button { Content = "New token", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2) };
                renew.Click += (s, e) =>
                {
                    NewToken();
                    command.Text = ClaudeCode.HttpCommand(Url, Token);
                };
                panel.Children.Add(renew);
            }
            Line($"Or run {Product.Name} headless (no window, for autonomous sessions) as an MCP server on stdio:", 14);
            Command(ClaudeCode.StdioCommand(Environment.ProcessPath));
            Line($"Agent log: {Path.GetDirectoryName(host.Log.FilePath)}", 10);
            window.Content = panel;
            window.Show();
        }

        public void Dispose() => host.Dispose();
    }

    /// <summary>A command from a delegate (key bindings).</summary>
    internal sealed class ActionCommand : ICommand
    {
        readonly Action action;
        public ActionCommand(Action action) => this.action = action;
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => action();
    }
}
