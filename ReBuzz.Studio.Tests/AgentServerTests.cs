// Fork (docs/specs/studio-mvp.md S8): the MCP server in studio mode, headless. Preferences ▸ Agent
// starts Streamable HTTP on a free port with a token and shows the `claude mcp add` line; a client
// then calls initialize, studio_status (mode "studio") and machine_add over HTTP, and the machine
// view shows the new machine. The UI thread is the engine's: the test pumps it while it waits.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Preferences;
using ReBuzz.Studio.Views.Machines;

namespace ReBuzz.Studio.Tests
{
    public class AgentServerTests
    {
        static StudioHost Host => Studio.Host;

        static void Settle(Window window = null)
        {
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Waits for an HTTP call, running the UI thread's work meanwhile (the agent's calls run there).</summary>
        static T Pump<T>(Task<T> task, int seconds = 60)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail("the agent didn't answer in time");
                Thread.Sleep(5);
            }
            return task.GetAwaiter().GetResult();
        }

        static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        [AvaloniaTest]
        public void TheStudioServesTheAgentOverHttpWithAToken()
        {
            var agent = Host.Agent;
            var core = Host.Core;
            Assert.That(agent, Is.Not.Null, "the studio starts the agent server");
            Assert.That(agent.Listening, Is.False, "off until allowed");
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            SongFiles.New().Wait();
            var window = new MainWindow(Host) { Width = 1280, Height = 800 };
            window.Show();
            window.ShowView("Machines");
            Settle(window);
            int port = FreePort();
            using var client = new HttpClient();
            try
            {
                // Preferences ▸ Agent: the port, then allow agents.
                var prefs = PreferencesWindow.Open(Host, PreferencesWindow.AgentPageName);
                var page = prefs.Page<AgentPage>();
                Settle(prefs);
                Assert.That(page.StateText, Does.StartWith("Off"));
                page.PortBox.Focus();
                page.PortBox.Text = port.ToString();
                prefs.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Assert.That(agent.Settings.Port, Is.EqualTo(port.ToString()));
                page.AllowBox.IsChecked = true;
                Settle(prefs);
                Assert.That(agent.Listening, Is.True);
                string url = $"http://127.0.0.1:{port}/mcp";
                Assert.That(agent.Url, Is.EqualTo(url));
                string token = agent.Token;
                Assert.That(token.Length, Is.GreaterThanOrEqualTo(24));
                Assert.That(page.CommandBox.Text, Is.EqualTo($"claude mcp add --transport http rebuzz {url} --header \"Authorization: Bearer {token}\""));
                Assert.That(page.StateText, Is.EqualTo($"Listening on {url}"));
                Assert.That(new AgentSettings().AllowAgents, Is.True, "saved");
                Studio.Screenshot(prefs, "prefs-agent");

                // Copy puts the line on the clipboard.
                var clipboard = prefs.Clipboard;
                Assert.That(clipboard, Is.Not.Null);
                page.CopyButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.That(Pump(Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard)), Is.EqualTo(page.CommandBox.Text));

                HttpRequestMessage Post(string body, string session = null, string bearer = null)
                {
                    var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    r.Headers.Accept.ParseAdd("application/json, text/event-stream");
                    r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? token);
                    if (session != null) r.Headers.Add("Mcp-Session-Id", session);
                    return r;
                }
                JsonNode Call(string session, int id, string tool, JsonObject arguments)
                {
                    var request = new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
                        ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
                    };
                    var response = Pump(client.SendAsync(Post(request.ToJsonString(), session)));
                    Assert.That((int)response.StatusCode, Is.EqualTo(200));
                    var body = JsonNode.Parse(Pump(response.Content.ReadAsStringAsync()))!;
                    Assert.That(body["result"]!["isError"]!.GetValue<bool>(), Is.False, body.ToJsonString());
                    return body["result"]!["structuredContent"]!;
                }

                var init = Pump(client.SendAsync(Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"studio-test\",\"version\":\"1\"}}}")));
                Assert.That((int)init.StatusCode, Is.EqualTo(200));
                string session = init.Headers.GetValues("Mcp-Session-Id").Single();
                var info = JsonNode.Parse(Pump(init.Content.ReadAsStringAsync()))!;
                Assert.That(info["result"]!["serverInfo"]!["name"]!.GetValue<string>(), Is.EqualTo("rebuzz"));
                Pump(client.SendAsync(Post("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", session)));

                var status = Call(session, 2, "studio_status", new JsonObject());
                Assert.That(status["mode"]!.GetValue<string>(), Is.EqualTo("studio"));

                var added = Call(session, 3, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Agent Poly" });
                Assert.That(added["machine"]!["name"]!.GetValue<string>(), Is.EqualTo("Agent Poly"));
                Settle(window);

                // The machine view follows the engine's events: the machine is there, drawn and hit.
                var machine = core.SongCore.MachinesList.Single(m => m.Name == "Agent Poly");
                Assert.That(machine.Outputs.Select(o => o.Destination.Name), Is.EqualTo(new[] { "Master" }));
                var view = (MachineView)((TabItem)window.Tabs.SelectedItem!).Content!;
                Assert.That(view.Canvas.MachineAt(view.Canvas.BoxOf(machine).Center), Is.SameAs(machine));
                Assert.That(core.SongCore.ActionStack.Actions.Any(), Is.True, "one undo step");
                window.RefreshNow();
                Assert.That(window.StatusText, Does.Contain($"Agent: listening on 127.0.0.1:{port}"));
                Studio.Screenshot(window, "agent-machine-added");

                // Without the token, or with an old one, the server refuses.
                var refused = Pump(client.SendAsync(Post("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/list\"}", session, bearer: "wrong")));
                Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                page.NewTokenButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle(prefs);
                Assert.That(agent.Token, Is.Not.EqualTo(token));
                Assert.That(page.CommandBox.Text, Does.Contain(agent.Token));
                var old = Pump(client.SendAsync(Post("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/list\"}", session, bearer: token)));
                Assert.That(old.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "a new token makes the old one stop working");

                // The agent's step undoes like the user's own.
                core.SongCore.ActionStack.Undo();
                Settle(window);
                Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Agent Poly"), Is.False);

                page.AllowBox.IsChecked = false;
                Assert.That(agent.Listening, Is.False);
                Assert.That(() => Pump(client.SendAsync(Post("{}"))), Throws.InstanceOf<HttpRequestException>(), "nothing listens once agents aren't allowed");
            }
            finally
            {
                agent.Set(false, AgentServer.DefaultPort.ToString());
                PreferencesWindow.Current?.Close();
                window.Close();
                Settle();
                Dialogs.Responder = null;
            }
        }
    }
}
