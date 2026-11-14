using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using ReBuzz.Agent;
using ReBuzz.Agent.Mcp;
using ReBuzzTests.Automation;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/mcp-server.md §9, A0): the agent server inside ReBuzz. The
    /// protocol itself is tested on Linux (ReBuzz.AgentTests); here the tools run against a real
    /// ReBuzzCore: status, song properties, save and open, refusals, failures as errors (no
    /// dialogs), and the HTTP transport end to end.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class AgentServerTests
    {
        static readonly string LogDir = TestTemp.Path("ReBuzzAgentTestLogs");

        internal sealed class Rig : IDisposable
        {
            public readonly Driver Driver = new();
            public readonly AgentHost Host;
            readonly McpSession session = new();
            int id;

            /// <summary>A headless studio; <paramref name="gear"/>: Neo machines from the solution build, with their manifests.</summary>
            public Rig(params NeoMachineTests.NeoInfo[] gear) : this("headless", gear)
            {
            }

            /// <summary>As above, with the host in <paramref name="mode"/> ("studio": the live tools play; tests pump the audio).</summary>
            public Rig(string mode, params NeoMachineTests.NeoInfo[] gear)
            {
                foreach (var g in gear)
                {
                    if (!File.Exists(g.BuiltPath)) Assert.Inconclusive($"{g.Name} not built ({g.BuiltPath}); build the solution first");
                    Driver.Gear.AddBuiltMachine(g, AtmaFileSystem.AbsoluteFilePath.Value(g.BuiltPath), g.Generator);
                }
                Driver.Start();
                foreach (var g in gear)
                {
                    // The manifest goes beside the DLL, as the build puts it.
                    string dll = Driver.Core.MachineDLLs[g.Name].Path;
                    foreach (var sidecar in new[] { g.Name + ".machine.json", g.Name + ".prs.xml" })
                    {
                        string from = Path.Combine(Path.GetDirectoryName(g.BuiltPath)!, sidecar);
                        if (File.Exists(from)) File.Copy(from, Path.Combine(Path.GetDirectoryName(dll)!, sidecar), overwrite: true);
                    }
                }
                Host = new AgentHost(Driver.Core, Driver.Dispatcher, mode, LogDir);
                Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "initialize",
                    ["params"] = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "tests", ["version"] = "1" } },
                });
            }

            public JsonObject Send(JsonObject message) =>
                Host.Server.HandleAsync(message, session, _ => System.Threading.Tasks.Task.CompletedTask).GetAwaiter().GetResult();

            /// <summary>Calls a tool; returns (isError, text, structured).</summary>
            public (bool Error, string Text, JsonObject Data) Call(string tool, JsonObject args = null)
            {
                var r = Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
                });
                Assert.That(r["error"], Is.Null, $"{tool}: protocol error {r["error"]?.ToJsonString()}");
                var result = r["result"]!;
                return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>(), result["structuredContent"] as JsonObject);
            }

            public void Dispose()
            {
                Host.Dispose();
                Driver.Dispose();
            }
        }

        static string TempSong(string name) => TestTemp.Path("ReBuzzAgentTests", Guid.NewGuid().ToString("N"), name);

        [Test]
        public void StatusDescribesTheStudio()
        {
            using var rig = new Rig();
            var tools = rig.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 99, ["method"] = "tools/list" })["result"]!["tools"]!.AsArray();
            Assert.That(tools.Select(t => t!["name"]!.GetValue<string>()), Is.SupersetOf(new[] { "studio_status", "undo", "redo", "song_new", "song_open", "song_save", "song_set" }));
            foreach (var t in tools)
                Assert.That(t!["description"]!.GetValue<string>(), Does.Contain("Example:"), $"{t["name"]}: every description shows an example (§4 rule 2)");

            var (error, text, s) = rig.Call("studio_status");
            Assert.That(error, Is.False, text);
            Assert.That(s["mode"]!.GetValue<string>(), Is.EqualTo("headless"));
            Assert.That(s["counts"]!["machines"]!.GetValue<int>(), Is.GreaterThanOrEqualTo(1), "the Master");
            Assert.That(s["tempo_bpm"]!.GetValue<int>(), Is.GreaterThan(0));
            Assert.That(File.Exists(s["agent"]!["log"]!.GetValue<string>()), Is.True, "the call was logged");
            TestContext.Out.WriteLine(text);
        }

        [Test]
        public void SongSetChangesTempoAndLoopInBeats()
        {
            using var rig = new Rig();
            var (error, text, s) = rig.Call("song_set", new JsonObject { ["tempo_bpm"] = 92.4, ["ticks_per_beat"] = 4, ["song_end_beats"] = 64, ["loop_start_beats"] = 16, ["loop_end_beats"] = 32 });
            Assert.That(error, Is.False, text);
            Assert.That(s["tempo_bpm"]!.GetValue<int>(), Is.EqualTo(92), "Buzz tempos are whole numbers");
            Assert.That(s["length_beats"]!.GetValue<double>(), Is.EqualTo(64));
            Assert.That(s["loop"]!["start_beats"]!.GetValue<double>(), Is.EqualTo(16));
            Assert.That(s["loop"]!["end_beats"]!.GetValue<double>(), Is.EqualTo(32));
            Assert.That(rig.Driver.Core.BPM, Is.EqualTo(92));

            var bad = rig.Call("song_set", new JsonObject { ["loop_start_beats"] = 8, ["loop_end_beats"] = 4 });
            Assert.That(bad.Error, Is.True);
            Assert.That(bad.Text, Does.Contain("must be after"));

            var invalid = rig.Call("song_set", new JsonObject { ["tempo"] = 100 });
            Assert.That(invalid.Error, Is.True);
            Assert.That(invalid.Text, Does.Contain("tempo: unknown argument"));
        }

        [Test]
        public void SaveNewAndOpenRoundTripTheSong()
        {
            using var rig = new Rig();
            string path = TempSong("round trip.bmxml");
            rig.Call("song_set", new JsonObject { ["tempo_bpm"] = 101 });
            var saved = rig.Call("song_save", new JsonObject { ["path"] = path });
            Assert.That(saved.Error, Is.False, saved.Text);
            Assert.That(File.Exists(path), Is.True);

            var refused = rig.Call("song_save", new JsonObject { ["path"] = TempSong("other.txt") });
            Assert.That(refused.Error, Is.True);
            Assert.That(refused.Text, Does.Contain("isn't a song file"));

            rig.Call("song_set", new JsonObject { ["tempo_bpm"] = 140 });
            var unsaved = rig.Call("song_new");
            Assert.That(unsaved.Error, Is.True, "unsaved changes are protected");
            Assert.That(unsaved.Text, Does.Contain("discard_unsaved=true"));

            var fresh = rig.Call("song_new", new JsonObject { ["discard_unsaved"] = true, ["tempo_bpm"] = 120 });
            Assert.That(fresh.Error, Is.False, fresh.Text);
            Assert.That(fresh.Data["tempo_bpm"]!.GetValue<int>(), Is.EqualTo(120));

            var opened = rig.Call("song_open", new JsonObject { ["path"] = path });
            Assert.That(opened.Error, Is.False, opened.Text);
            Assert.That(opened.Data["tempo_bpm"]!.GetValue<int>(), Is.EqualTo(101));
            Assert.That(opened.Data["song"]!["file"]!.GetValue<string>(), Is.EqualTo(path));
        }

        [Test]
        public void AFailingOpenIsAnErrorTheAgentCanReadNotADialog()
        {
            using var rig = new Rig();
            var missing = rig.Call("song_open", new JsonObject { ["path"] = TempSong("nothing.bmxml") });
            Assert.That(missing.Error, Is.True);
            Assert.That(missing.Text, Does.StartWith("No file at"));

            string broken = TempSong("broken.bmxml");
            Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
            File.WriteAllText(broken, "<not a song");
            var failed = rig.Call("song_open", new JsonObject { ["path"] = broken });
            Assert.That(failed.Error, Is.True);
            Assert.That(failed.Text, Does.StartWith("Couldn't open"));

            var nothing = rig.Call("undo");
            Assert.That(nothing.Error, Is.True);
            Assert.That(nothing.Text, Does.StartWith("Nothing to undo"));

            // The studio still answers after the failures.
            Assert.That(rig.Call("studio_status").Error, Is.False);
        }

        [Test]
        public void StudioModeServesHttpWithAToken()
        {
            using var rig = new Rig();
            string token = HttpTransport.NewToken();
            var http = rig.Host.StartHttp(0, token);
            using var client = new HttpClient();
            HttpRequestMessage Post(string body, string session = null)
            {
                var r = new HttpRequestMessage(HttpMethod.Post, http.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                r.Headers.Accept.ParseAdd("application/json, text/event-stream");
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (session != null) r.Headers.Add("Mcp-Session-Id", session);
                return r;
            }
            var init = client.Send(Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}"));
            Assert.That((int)init.StatusCode, Is.EqualTo(200));
            string session = init.Headers.GetValues("Mcp-Session-Id").Single();
            var status = client.Send(Post("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"studio_status\",\"arguments\":{}}}", session));
            var body = JsonNode.Parse(status.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
            Assert.That(body["result"]!["isError"]!.GetValue<bool>(), Is.False, body.ToJsonString());
            Assert.That(body["result"]!["structuredContent"]!["counts"]!["machines"]!.GetValue<int>(), Is.GreaterThanOrEqualTo(1));
        }
    }
}
