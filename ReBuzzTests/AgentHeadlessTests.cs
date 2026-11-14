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
    /// <c>NeoBuzzLive.exe --agent</c>, with the package's own gear: it answers on stdio, makes a sound
    /// with a Neo synth through the tools, and exits when stdin closes.
    /// </summary>
    public partial class AgentHeadlessTests
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

            public string Errors => string.Join("\n", stderr.TakeLast(40)) + Windows();

            /// <summary>Whether a line on stderr has had <paramref name="text"/>, waiting up to 10 s for it (stderr is read as it comes).</summary>
            public bool Said(string text)
            {
                var clock = Stopwatch.StartNew();
                while (!stderr.Any(l => l.Contains(text)))
                {
                    if (clock.Elapsed.TotalSeconds > 10) return false;
                    System.Threading.Thread.Sleep(50);
                }
                return true;
            }

            /// <summary>The process's windows and their texts (a dialog nobody can answer shows up here).</summary>
            string Windows()
            {
                var lines = new StringBuilder();
                try
                {
                    uint pid = (uint)process.Id;
                    EnumWindows((h, _) =>
                    {
                        GetWindowThreadProcessId(h, out uint owner);
                        if (owner != pid || !IsWindowVisible(h)) return true;
                        lines.Append("\nwindow: \"").Append(Text(h)).Append('"');
                        EnumChildWindows(h, (c, __) => { string t = Text(c); if (t.Length > 0) lines.Append(" | ").Append(t); return true; }, IntPtr.Zero);
                        return true;
                    }, IntPtr.Zero);
                }
                catch (Exception e) { lines.Append("\n(windows: ").Append(e.Message).Append(')'); }
                return lines.Length == 0 ? "\n(no visible windows)" : lines.ToString();
            }

            static string Text(IntPtr h)
            {
                var sb = new StringBuilder(512);
                GetWindowText(h, sb, sb.Capacity);
                return sb.ToString();
            }

            delegate bool EnumProc(IntPtr hwnd, IntPtr param);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr param);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr param);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
            [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

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
                    if (line.Result == null) Assert.Fail($"NeoBuzz Live closed stdout. stderr:\n{Errors}");
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

        /// <summary>
        /// Fork (rename): the app people launch is NeoBuzzLive.exe, the apphost ReBuzz.csproj names so; the assembly it
        /// starts stays ReBuzz.dll (machines, plugins and these tests load it by name), and the file says NeoBuzz Live
        /// (Task Manager and the volume mixer show its description).
        /// </summary>
        [Test]
        public void TheAppIsNeoBuzzLiveExeAndItsAssemblyStaysReBuzz()
        {
            string bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
            string exe = Path.Combine(bin, "NeoBuzzLive.exe");
            Assert.That(File.Exists(exe), Is.True, $"{exe} is built");
            Assert.That(File.Exists(Path.Combine(bin, "ReBuzz.exe")), Is.False, "no ReBuzz.exe beside it");
            Assert.That(ReBuzz.Product.WindowsExe, Is.EqualTo("NeoBuzzLive.exe"));
            string dll = Path.Combine(bin, "ReBuzz.dll");
            Assert.That(System.Reflection.AssemblyName.GetAssemblyName(dll).Name, Is.EqualTo("ReBuzz"), "the assembly keeps its name");
            Assert.That(File.Exists(Path.Combine(bin, "ReBuzz.runtimeconfig.json")), Is.True);
            foreach (var file in new[] { exe, dll })
            {
                var info = FileVersionInfo.GetVersionInfo(file);
                Assert.That((info.FileDescription, info.ProductName), Is.EqualTo(("NeoBuzz Live", "NeoBuzz Live")), Path.GetFileName(file));
            }
        }

        [Test]
        public void HeadlessReBuzzServesMcpOnStdioAndMakesASound()
        {
            string bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
            string exe = Path.Combine(bin, ReBuzz.Product.WindowsExe);
            if (!File.Exists(exe)) Assert.Inconclusive($"{ReBuzz.Product.WindowsExe} not built ({exe})");
            if (!File.Exists(Path.Combine(bin, "Gear", "Generators", "Neo Poly.NET.dll"))) Assert.Inconclusive("Neo Poly not built");

            using var client = new Client(exe, "--agent");
            var init = client.Request("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "ci", ["version"] = "1" },
            }, seconds: 180);
            Assert.That(init["result"]!["serverInfo"]!["name"]!.GetValue<string>(), Is.EqualTo("neobuzz-live"), client.Errors);
            Assert.That(init["result"]!["serverInfo"]!["title"]!.GetValue<string>(), Is.EqualTo("NeoBuzz Live"));
            Assert.That(client.Said("[NeoBuzz Live] Headless agent server ready"), Is.True, "the console's banner names the app. stderr:\n" + client.Errors);
            client.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });

            var status = client.Tool("studio_status");
            Assert.That(status["mode"]!.GetValue<string>(), Is.EqualTo("headless"));
            var gear = client.Tool("gear_list", new JsonObject { ["search"] = "Neo", ["limit"] = 400 });
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

            string song = TestTemp.Path("ReBuzzAgentTests", Guid.NewGuid().ToString("N"), "headless.bmxml");
            client.Tool("song_save", new JsonObject { ["path"] = song });
            Assert.That(new FileInfo(song).Length, Is.GreaterThan(0));

            Assert.That(client.CloseAndWait(60), Is.True, "NeoBuzz Live exits when stdin closes. stderr:\n" + client.Errors);
        }

        /// <summary>
        /// Fork (docs/specs/mcp-server.md §12.9, U3): every machine in the package's Gear has a manifest (generated, curated
        /// or a profiled draft) and a manual; the curated ones (natives here, Pedal machines in a full build) say so, with
        /// exact units, plain names that param_set takes, roles and quirks.
        /// </summary>
        [Test]
        public void EveryMachineInTheGearHasAManifestAndAManual()
        {
            string bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
            string exe = Path.Combine(bin, ReBuzz.Product.WindowsExe);
            if (!File.Exists(exe)) Assert.Inconclusive($"{ReBuzz.Product.WindowsExe} not built ({exe})");
            if (!File.Exists(Path.Combine(bin, "Gear", "Effects", "Jeskola Delay.machine.json"))) Assert.Inconclusive("Machines/Manifests not copied into Gear (build.ps1 step 5b)");

            using var client = new Client(exe, "--agent");
            client.Request("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "ci", ["version"] = "1" },
            }, seconds: 180);
            client.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });

            var rows = client.Tool("gear_list", new JsonObject { ["limit"] = 1000, ["include_unhealthy"] = true })["gear"]!.AsArray().Select(g => g!.AsObject()).ToList();
            // U2 in depth (mcp-server.md §12.12): by default gear_list leaves unhealthy gear out and names it.
            var listed = client.Tool("gear_list", new JsonObject { ["limit"] = 1000 });
            var hidden = listed["hidden_unhealthy"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? new System.Collections.Generic.List<string>();
            var unhealthy = rows.Where(g => g["health"]?.GetValue<string>() is "unhealthy" or "missing" or "crashed").Select(g => g["name"]!.GetValue<string>()).ToList();
            Assert.That(hidden, Is.EquivalentTo(unhealthy), "the unhealthy gear is left out and named");
            Assert.That(listed["gear"]!.AsArray().Select(g => g!["name"]!.GetValue<string>()), Is.EquivalentTo(rows.Select(g => g["name"]!.GetValue<string>()).Except(unhealthy)));
            var missing = rows.Where(g => g["manifest"] == null && g["health"]?.GetValue<string>() is not ("missing" or "crashed"))
                .Select(g => g["name"]!.GetValue<string>()).ToList();
            Assert.That(missing, Is.Empty, $"every machine in the Gear has a manifest ({rows.Count} listed)");
            var curated = rows.Where(g => g["manifest"]?.GetValue<string>() == "curated").ToList();
            Assert.That(curated.Select(g => g["name"]!.GetValue<string>()), Is.SupersetOf(new[] { "Jeskola Delay", "FSM Infector", "Rout 909", "Oomek Exciter" }));
            Assert.That(curated.All(g => g["exact_units"]!.GetValue<bool>() && g["quirks"] is JsonArray { Count: > 0 }), Is.True, "curated: exact units and quirks");

            // A manual for every machine with a manifest: the resources and guide(machine=…).
            var uris = client.Request("resources/list", new JsonObject())["result"]!["resources"]!.AsArray().Select(r => r!["uri"]!.GetValue<string>()).ToHashSet();
            var noManual = rows.Where(g => g["manifest"] != null)
                .Select(g => g["name"]!.GetValue<string>())
                .Where(n => !uris.Contains("rebuzz://machines/" + System.Text.RegularExpressions.Regex.Replace(n.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') + "/manual")).ToList();
            Assert.That(noManual, Is.Empty, "a manual resource per machine with a manifest");
            var manual = client.Tool("guide", new JsonObject { ["machine"] = "Jeskola Delay" })["markdown"]!.GetValue<string>();
            Assert.That(manual, Does.Contain("## Quirks").And.Contain("**Echo level** (`mix.wet`)"));
            var draft = rows.FirstOrDefault(g => g["manifest"]?.GetValue<string>() == "draft");
            if (draft != null)
                Assert.That(client.Tool("guide", new JsonObject { ["machine"] = draft["name"]!.GetValue<string>() })["markdown"]!.GetValue<string>(), Does.Contain("profiled draft"));

            // A curated plain name works in param_set; the native's own texts come back.
            client.Tool("machine_add", new JsonObject { ["gear"] = "Jeskola Delay", ["name"] = "Echo" });
            var set = client.Tool("param_set", new JsonObject
            {
                ["machine"] = "Echo",
                ["changes"] = new JsonArray(new JsonObject { ["param"] = "Echo level", ["track"] = 0, ["value"] = "50%" }),
            });
            Assert.That(set["changed"]![0]!["param"]!.GetValue<string>(), Is.EqualTo("Wet out"));
            Assert.That(set["changed"]![0]!["raw"]!.GetValue<int>(), Is.EqualTo(64));
            var describe = client.Tool("machine_describe", new JsonObject { ["machine"] = "Echo" });
            Assert.That(describe["manifest"]!.GetValue<string>(), Is.EqualTo("curated"));
            Assert.That(describe["quirks"]!.AsArray().Count, Is.GreaterThan(0));
            Assert.That(describe["parameters"]!.AsArray().Any(p => p!["role"]?.GetValue<string>() == "delay.feedback"), Is.True);

            Assert.That(client.CloseAndWait(60), Is.True, "NeoBuzz Live exits when stdin closes. stderr:\n" + client.Errors);
        }
    }
}
