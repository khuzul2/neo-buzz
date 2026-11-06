// Fork (rename): the app is NeoBuzz Live to whoever talks to it. The MCP server announces itself as
// neobuzz-live ("NeoBuzz Live"), the `claude mcp add` lines both apps show register it under that name,
// and what an agent reads (the instructions, guides, prompts, curated machine pages and manifests,
// successor notes, the tools' texts) names NeoBuzz Live, not ReBuzz. ReBuzz stays where it is a name
// that doesn't change (machines saved in songs, code paths, settings folders, resource URIs) or means
// upstream ReBuzz.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReBuzz.Agent.Guides;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using static ReBuzz.AgentTests.TestTools;

namespace ReBuzz.AgentTests
{
    [TestFixture]
    public class AppNameTests
    {
        [Test]
        public void TheAppIsNeoBuzzLive()
        {
            Assert.That(Product.Name, Is.EqualTo("NeoBuzz Live"));
            Assert.That(Product.McpName, Is.EqualTo("neobuzz-live"));
            Assert.That(Product.WindowsExe, Is.EqualTo("NeoBuzzLive.exe"));
            Assert.That(Product.LogPrefix, Is.EqualTo("[NeoBuzz Live] "));
            Assert.That(Product.BasedOn, Is.EqualTo("Based on ReBuzz by WDE and the ReBuzz contributors (MIT licence): https://github.com/wasteddesign/ReBuzz"));
        }

        [Test]
        public async Task TheServerAnnouncesItselfAsNeobuzzLive()
        {
            var info = GuideLibrary.AppServer("1.2");
            Assert.That((info.Name, info.Title, info.Version), Is.EqualTo(("neobuzz-live", "NeoBuzz Live", "1.2")));
            Assert.That(info.Instructions, Does.StartWith("NeoBuzz Live is a modular music studio (Buzz-style, a fork of ReBuzz): "));

            // As initialize answers a client (TestTools.Server is built on AppServer).
            var r = await Server().HandleAsync(Initialize(1, "2025-06-18"), new McpSession(), _ => Task.CompletedTask);
            var result = r!["result"]!;
            Assert.That(result["serverInfo"]!["name"]!.GetValue<string>(), Is.EqualTo("neobuzz-live"));
            Assert.That(result["serverInfo"]!["title"]!.GetValue<string>(), Is.EqualTo("NeoBuzz Live"));
            Assert.That(result["instructions"]!.GetValue<string>(), Does.StartWith("NeoBuzz Live is "));
        }

        [Test]
        public void TheClaudeCodeLinesRegisterNeobuzzLive()
        {
            // Settings → Agent (ReBuzz.exe, Ctrl+Shift+A) and Preferences ▸ Agent (the studio) show this line.
            Assert.That(ClaudeCode.HttpCommand("http://127.0.0.1:7481/mcp", "s3cret"),
                Is.EqualTo("claude mcp add --transport http neobuzz-live http://127.0.0.1:7481/mcp --header \"Authorization: Bearer s3cret\""));
            // ReBuzz.exe's window shows the headless one with its own path; rebuzz-headless's help with its name.
            Assert.That(ClaudeCode.StdioCommand(@"C:\Users\me\NeoBuzz-Live-0.9-x64\NeoBuzzLive.exe"),
                Is.EqualTo("claude mcp add neobuzz-live -- \"C:\\Users\\me\\NeoBuzz-Live-0.9-x64\\NeoBuzzLive.exe\" --agent"));
            Assert.That(ClaudeCode.StdioCommand("/Applications/NeoBuzz Live.app/Contents/MacOS/NeoBuzz Live"),
                Is.EqualTo("claude mcp add neobuzz-live -- \"/Applications/NeoBuzz Live.app/Contents/MacOS/NeoBuzz Live\" --agent"));
            Assert.That(ClaudeCode.StdioCommand("rebuzz-headless"), Is.EqualTo("claude mcp add neobuzz-live -- rebuzz-headless --agent"));
        }

        /// <summary>
        /// "ReBuzz" as the app's name: not a machine saved in songs ("ReBuzz Audio In"…), a code path (ReBuzz/…), a
        /// dotted name (ReBuzz.Agent), a URI (rebuzz://, lower case) or upstream (based on, a fork of ReBuzz).
        /// </summary>
        static readonly Regex AppCalledReBuzz = new(@"(?<![/\\\w-])(?<!based on |fork of )ReBuzz(?![\w/\\-])(?!\.\w)(?! Audio (In|Out)| Groove| Midi Output)");

        static IEnumerable<string> Hits(string where, string text) =>
            text.Split('\n').Where(line => AppCalledReBuzz.IsMatch(line)).Select(line => $"{where}: {line.Trim()}");

        [Test]
        public void WhatAgentsReadNamesNeoBuzzLive()
        {
            var hits = new List<string>();
            hits.AddRange(Hits("instructions", GuideLibrary.ServerInstructions));
            foreach (var topic in GuideLibrary.TopicNames)
            {
                hits.AddRange(Hits($"guide {topic} (title)", GuideLibrary.Title(topic)));
                hits.AddRange(Hits($"guide {topic}", GuideLibrary.Get(topic)));
            }
            foreach (var gear in MachineManual.CuratedGear)
                hits.AddRange(Hits($"machine page {gear}", MachineManual.Curated(gear)));
            foreach (var (prompt, args) in new (string, Dictionary<string, string>)[]
            {
                ("compose", new() { ["genre"] = "house" }), ("design-sound", new() { ["description"] = "a bell" }),
                ("mix", new()), ("master", new()), ("critique", new()),
            })
                hits.AddRange(Hits($"prompt {prompt}", GuideLibrary.Prompts().Single(p => p.Name == prompt).Render(args)));

            // The manifests and successor notes, as the manuals and machine_replace serve them.
            string machines = Path.Combine(RepoRoot(), "Machines");
            foreach (var file in Directory.GetFiles(machines, "*.json", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(MachineManifest.Suffix) || f.EndsWith(SuccessorMap.Suffix)))
            {
                foreach (var value in Strings(JsonNode.Parse(File.ReadAllText(file))))
                    if (value != "ReBuzz's example of a managed multi-input, multi-output effect: a clean input, a noise input that gets noise added, a delay input with feedback; a mix out and each part on its own output.")
                        hits.AddRange(Hits(Path.GetRelativePath(machines, file), value));
            }
            Assert.That(hits, Is.Empty, "these call the app ReBuzz");
        }

        /// <summary>
        /// The texts of the tools' descriptions, schemas and errors, in their source: ReBuzz/Agent (the tools, in the
        /// engine) and ReBuzz.Agent (the server). A string literal that calls the app ReBuzz is a finding; the
        /// settings and log folders keep the name (a literal "ReBuzz" beside GetFolderPath), and Product.cs names
        /// upstream ReBuzz on purpose.
        /// </summary>
        [Test]
        public void TheToolsTextsNameNeoBuzzLive()
        {
            var literal = new Regex("\"(?:[^\"\\\\\\n]|\\\\.)*\"");
            var hits = new List<string>();
            string root = RepoRoot();
            var files = Directory.GetFiles(Path.Combine(root, "ReBuzz", "Agent"), "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Path.Combine(root, "ReBuzz.Agent"), "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => Path.GetFileName(f) != "Product.cs");
            foreach (var file in files)
            {
                int n = 0;
                foreach (var line in File.ReadLines(file))
                {
                    n++;
                    string code = line.TrimStart();
                    if (code.StartsWith("//") || code.StartsWith("///") || line.Contains("GetFolderPath")) continue;
                    foreach (Match m in literal.Matches(line))
                        if (AppCalledReBuzz.IsMatch(m.Value))
                            hits.Add($"{Path.GetRelativePath(root, file)}:{n}: {m.Value}");
                }
            }
            Assert.That(hits, Is.Empty, "string literals that call the app ReBuzz (Product.Name says NeoBuzz Live)");
        }

        /// <summary>
        /// The same over the apps' own texts, people's this time: both apps' windows, menus, dialogs, tooltips and
        /// messages (ReBuzz, ReBuzz.Studio, ReBuzz.Host, ReBuzz.Headless, the WPF toolbar's menus, the editor kit's
        /// dialogs), in C# string literals and XAML. Allowed, each for its reason: the settings, autosave and log
        /// folders and registry keys, which keep ReBuzz's name so settings carry over; the virtual MIDI ports' former
        /// name and their unique-ID seed (CoreMidiBackend, CoreMidi.KeepId), kept so saved settings and other apps
        /// find them; the song format's name ("ReBuzz XML"); the About boxes' credits, which name upstream ReBuzz.
        /// </summary>
        [Test]
        public void TheAppsOwnTextsNameNeoBuzzLive()
        {
            var literal = new Regex("\"(?:[^\"\\\\\\n]|\\\\.)*\"");
            var attribute = new Regex("=\"([^\"]*)\"");
            var allowed = new Regex(@"GetFolderPath|SpecialFolder|Application Support|Software\\\\ReBuzz|AppDataPath|FormerVirtualName|""ReBuzz "" \+ key|ReBuzz XML|clr-namespace:ReBuzz");
            string root = RepoRoot();
            var hits = new List<string>();
            foreach (var dir in new[] { "ReBuzz", "ReBuzz.Studio", "ReBuzz.Host", "ReBuzz.Headless", Path.Combine("ReBuzzGUI", "BuzzGUI.ToolBar"), Path.Combine("Machines", "NeoBuzz.Editor.Avalonia") })
            {
                foreach (var file in Directory.GetFiles(Path.Combine(root, dir), "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(file);
                    if (ext is not (".cs" or ".xaml" or ".axaml")) continue;
                    string sep = Path.DirectorySeparatorChar.ToString();
                    if (file.Contains(sep + "obj" + sep) || file.Contains(sep + "bin" + sep) || Path.GetFileName(file).StartsWith("AboutWindow")) continue;
                    int n = 0;
                    foreach (var line in File.ReadLines(file))
                    {
                        n++;
                        string code = line.TrimStart();
                        if (code.StartsWith("//") || allowed.IsMatch(line)) continue;
                        var texts = ext == ".cs"
                            ? literal.Matches(line).Select(m => m.Value)
                            : attribute.Matches(line).Select(m => m.Groups[1].Value).Concat(code.StartsWith("<") ? Enumerable.Empty<string>() : new[] { line });
                        foreach (var text in texts.Where(t => AppCalledReBuzz.IsMatch(t)))
                            hits.Add($"{Path.GetRelativePath(root, file)}:{n}: {text.Trim()}");
                    }
                }
            }
            Assert.That(hits, Is.Empty, "texts that call the app ReBuzz (Product.Name says NeoBuzz Live)");
        }

        static IEnumerable<string> Strings(JsonNode? node) => node switch
        {
            JsonObject o => o.SelectMany(kv => Strings(kv.Value)),
            JsonArray a => a.SelectMany(Strings),
            JsonValue v when v.TryGetValue(out string? s) => new[] { s! },
            _ => Enumerable.Empty<string>(),
        };

        static string RepoRoot()
        {
            string? env = Environment.GetEnvironmentVariable("NEO_BUZZ_ROOT");
            if (!string.IsNullOrEmpty(env)) return env;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "Machines")) && Directory.Exists(Path.Combine(dir.FullName, "ReBuzz.Agent")))
                    return dir.FullName;
            Assert.Inconclusive("repository root not found");
            return "";
        }
    }
}
