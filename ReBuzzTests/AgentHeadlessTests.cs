using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReBuzzTests.Automation;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/mcp-server.md §3.1, A0/A1): the real thing an MCP client runs,
    /// <c>ReBuzz.exe --agent</c>, with the package's own gear: it answers on stdio, makes a sound
    /// with a Neo synth through the tools, and exits when stdin closes.
    /// </summary>
    public class AgentHeadlessTests
    {
        sealed class Client : IDisposable
        {
            readonly Process process;
            readonly ConcurrentQueue<string> stderr = new();
            int id;

            public Client(string exe, string args)
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    StandardOutputEncoding = new UTF8Encoding(false),
                };
                process = Process.Start(psi)!;
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.Enqueue(e.Data); };
                process.BeginErrorReadLine();
            }

            public string Errors => string.Join("\n", stderr.TakeLast(40));

            public void Send(JsonObject message)
            {
                process.StandardInput.WriteLine(message.ToJsonString());
                process.StandardInput.Flush();
            }

            /// <summary>Sends a request and waits for its response (other lines are skipped).</summary>
            public JsonObject Request(string method, JsonObject p, int seconds = 120)
            {
                int my = ++id;
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = my, ["method"] = method, ["params"] = p });
                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < deadline)
                {
                    var line = process.StandardOutput.ReadLineAsync();
                    if (!line.Wait(TimeSpan.FromSeconds(Math.Max(1, (deadline - DateTime.UtcNow).TotalSeconds))))
                        break;
                    if (line.Result == null) Assert.Fail($"ReBuzz closed stdout. stderr:\n{Errors}");
                    JsonObject node = null;
                    try { node = JsonNode.Parse(line.Result) as JsonObject; }
                    catch (Exception) { Assert.Fail($"stdout carried a line that isn't the protocol: {line.Result}"); }
                    if (node?["id"]?.GetValue<int>() == my) return node;
                }
                Assert.Fail($"No answer to {method} in {seconds} s. stderr:\n{Errors}");
                return null;
            }

            public JsonObject Tool(string name, JsonObject args = null)
            {
                var r = Request("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args ?? new JsonObject() });
                Assert.That(r["error"], Is.Null, $"{name}: {r["error"]?.ToJsonString()}");
                Assert.That(r["result"]!["isError"]!.GetValue<bool>(), Is.False, $"{name}: {r["result"]!["content"]![0]!["text"]}\nstderr:\n{Errors}");
                TestContext.Out.WriteLine($"{name}: {r["result"]!["content"]![0]!["text"]}");
                return r["result"]!["structuredContent"]!.AsObject();
            }

            public bool CloseAndWait(int seconds)
            {
                process.StandardInput.Close();
                return process.WaitForExit(seconds * 1000);
            }

            public void Dispose()
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
                process.Dispose();
            }
        }

        [Test]
        public void HeadlessReBuzzServesMcpOnStdioAndMakesASound()
        {
            string bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
            string exe = Path.Combine(bin, "ReBuzz.exe");
            if (!File.Exists(exe)) Assert.Inconclusive($"ReBuzz.exe not built ({exe})");
            if (!File.Exists(Path.Combine(bin, "Gear", "Generators", "Neo Poly.NET.dll"))) Assert.Inconclusive("Neo Poly not built");

            using var client = new Client(exe, "--agent");
            var init = client.Request("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "ci", ["version"] = "1" },
            }, seconds: 180);
            Assert.That(init["result"]!["serverInfo"]!["name"]!.GetValue<string>(), Is.EqualTo("rebuzz"), client.Errors);
            client.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });

            var status = client.Tool("studio_status");
            Assert.That(status["mode"]!.GetValue<string>(), Is.EqualTo("headless"));
            var gear = client.Tool("gear_list", new JsonObject { ["search"] = "Neo" });
            Assert.That(gear["gear"]!.AsArray().Where(g => g!["exact_units"]!.GetValue<bool>()).Select(g => g!["name"]!.GetValue<string>()),
                Is.SupersetOf(new[] { "Neo Poly", "Neo Verb" }), "the package's Neo machines come with manifests");

            client.Tool("machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = "Keys" });
            client.Tool("machine_add", new JsonObject { ["gear"] = "Neo Verb", ["name"] = "Hall", ["insert_after"] = "Keys" });
            client.Tool("pattern_create", new JsonObject { ["machine"] = "Keys", ["name"] = "Chord", ["length_beats"] = 4 });
            client.Tool("notes_write", new JsonObject
            {
                ["machine"] = "Keys", ["pattern"] = "Chord",
                ["notes"] = new JsonArray(
                    new JsonObject { ["pitch"] = "C4", ["start"] = 0, ["length"] = 2 },
                    new JsonObject { ["pitch"] = "E4", ["start"] = 0, ["length"] = 2 },
                    new JsonObject { ["pitch"] = "G4", ["start"] = 0, ["length"] = 2 }),
            });
            client.Tool("arrangement_place", new JsonObject { ["machine"] = "Keys", ["pattern"] = "Chord", ["at"] = 0 });
            var render = client.Tool("render", new JsonObject { ["tail_seconds"] = 1 });
            Assert.That(render["measure"]!["silent"]!.GetValue<bool>(), Is.False, "the chord is heard");
            Assert.That(File.Exists(render["file"]!.GetValue<string>()), Is.True);

            string song = Path.Combine(Path.GetTempPath(), "ReBuzzAgentTests", Guid.NewGuid().ToString("N"), "headless.bmxml");
            client.Tool("song_save", new JsonObject { ["path"] = song });
            Assert.That(new FileInfo(song).Length, Is.GreaterThan(0));

            Assert.That(client.CloseAndWait(60), Is.True, "ReBuzz exits when stdin closes. stderr:\n" + client.Errors);
        }
    }
}
