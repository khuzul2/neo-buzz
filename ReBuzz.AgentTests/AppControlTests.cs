using System.Text.Json.Nodes;
using ReBuzz.Agent;
using ReBuzz.Agent.App;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Guides;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.AgentTests
{
    /// <summary>The app-control tools (docs/specs/agent-knowledge.md §5, K3), over fakes of what the app gives them.</summary>
    [TestFixture]
    public class AppControlTests
    {
        // ---- fakes -------------------------------------------------------------------------------

        sealed class FakeMidi : IMidiBindings
        {
            public List<MidiBindingInfo> Bindings = new() { new MidiBindingInfo("Bass", "Cutoff", 0, 1, 74) };
            public Action<int, int>? Seen;
            public bool Listening;

            public IReadOnlyList<MidiBindingInfo> List(string? machine) => Bindings.Where(b => machine == null || b.Machine == machine).ToList();

            void Check(string machine, string parameter)
            {
                if (machine != "Bass" && machine != "Lead") throw new ToolError($"No machine \"{machine}\". Did you mean \"Bass\"?");
                if (parameter != "Cutoff" && parameter != "Volume") throw new ToolError($"Unknown parameter \"{parameter}\".");
            }

            public MidiBindingInfo? Bind(string machine, string parameter, int track, int channel, int controller)
            {
                Check(machine, parameter);
                var old = Bindings.FirstOrDefault(b => b.Machine == machine && b.Parameter == parameter && b.Track == track);
                Bindings.Remove(old!);
                Bindings.Add(new MidiBindingInfo(machine, parameter, track, channel, controller));
                return old;
            }

            public bool Unbind(string machine, string parameter, int track)
            {
                Check(machine, parameter);
                return Bindings.RemoveAll(b => b.Machine == machine && b.Parameter == parameter && b.Track == track) > 0;
            }

            public int UnbindAll(string machine) => Bindings.RemoveAll(b => b.Machine == machine);

            public IDisposable? Listen(Action<int, int> seen)
            {
                Seen = seen;
                Listening = true;
                return new Stop(() => Listening = false);
            }
        }

        sealed class Stop : IDisposable
        {
            readonly Action stop;
            public Stop(Action stop) => this.stop = stop;
            public void Dispose() => stop();
        }

        /// <summary>A device that takes frames in real time when it is "flowing".</summary>
        sealed class FakeAudio : IAudioControl
        {
            public AudioConfig Config = new("PortAudio: default", 256, 0);
            public string[] Devices_ = { "PortAudio: default", "PortAudio: USB DAC", "PortAudio: Broken" };
            public bool Flows = true;
            public long Dropouts;
            public long DropoutsPerPoll;
            public long Played;
            public bool Settable = true;
            public List<AudioConfig> Applied = new();
            string Open = "PortAudio: default";

            public IReadOnlyList<int> Buffers { get; } = new[] { 32, 64, 128, 256, 512, 1024, 2048 };
            public IReadOnlyList<int> Rates { get; } = new[] { 0, 44100, 48000, 96000 };

            public AudioState Status()
            {
                if (Flows) Played += 12000;      // each look is a quarter of a second at 48 kHz
                Dropouts += DropoutsPerPoll;
                return new AudioState(true, Open, null, Config.BufferFrames, 48000, Config.BufferFrames * 1000.0 / 48000, Dropouts, 0, Played, "PortAudio", Settable);
            }

            public IReadOnlyList<AudioDeviceInfo> Devices() => Devices_.Select(d => new AudioDeviceInfo(d, "PortAudio", d == Open)).ToList();

            public AudioConfig Current() => Config;

            public void Apply(AudioConfig config)
            {
                Applied.Add(config);
                if (config.Device == "PortAudio: Broken") { Flows = false; }
                Config = config;
                Open = config.Device;
                if (config.Device != "PortAudio: Broken" && Applied.Count > 1) Flows = true;
            }
        }

        sealed class FakeView : IViewControl
        {
            public ViewState S = new("Arrangement", true, 224, true, 288, "Selection", false, 340, false, "tracker", null, null, null, 1200, 800, false, false);
            public bool Recording { get; set; }
            public List<string> Calls = new();

            public ViewState State() => S with { Recording = Recording };

            public void ShowView(string view) { Calls.Add("view " + view); S = S with { View = view }; }

            public void ShowPattern(string machine, string pattern, bool pianoRoll)
            {
                Calls.Add($"pattern {machine}/{pattern} {(pianoRoll ? "roll" : "tracker")}");
                S = S with { DetailOpen = true, DetailPage = pianoRoll ? "piano_roll" : "tracker", Machine = machine, Pattern = pattern };
            }

            public bool OpenMachineWindow(string machine, string window)
            {
                Calls.Add($"window {machine} {window}");
                return window != "editor";
            }

            public void Select(string? machine, string? pattern, int? scene) { Calls.Add($"select {machine}/{pattern}/{scene}"); S = S with { Machine = machine, Pattern = pattern, Scene = scene }; }

            public void Panel(string panel, bool? open, double? size, string? tab, bool? maximised)
            {
                Calls.Add($"panel {panel} {open} {size} {tab} {maximised}");
                if (panel == "browser") S = S with { BrowserOpen = open ?? S.BrowserOpen, BrowserWidth = size ?? S.BrowserWidth };
                if (panel == "inspector") S = S with { InspectorOpen = open ?? tab != null || S.InspectorOpen, InspectorTab = tab ?? S.InspectorTab };
                if (panel == "detail") S = S with { DetailOpen = open ?? S.DetailOpen, DetailMaximised = maximised ?? S.DetailMaximised };
            }

            public void Window(double? width, double? height, bool? maximised) { Calls.Add($"window {width} {height} {maximised}"); S = S with { WindowWidth = width ?? S.WindowWidth, WindowHeight = height ?? S.WindowHeight, Maximised = maximised ?? S.Maximised }; }
        }

        sealed class FakeApp : IAppServices
        {
            public string Folder = Path.Combine(Path.GetTempPath(), "app-control-" + Guid.NewGuid().ToString("N"));
            public Dictionary<string, string> Values = new() { ["general.PatternLength"] = "16", ["general.AutomaticBackups"] = "True", ["general.SequenceView"] = "ModernVertical",
                ["advanced.Multithreading"] = "True", ["advanced.AudioBufferFillThread"] = "False", ["agent.AllowAgents"] = "True", ["agent.Port"] = "7481",
                ["safety.SafetyLimiter"] = "True", ["audio.BufferSize"] = "256" };
            public List<(string What, bool Changes)> Calls = new();
            public FakeMidi MidiFake = new();
            public FakeAudio? AudioFake = new();
            public FakeView? ViewFake;
            public string Mode = "studio";
            public List<string> Recent = new();

            public FakeApp() => Directory.CreateDirectory(Folder);

            public Task<T> Engine<T>(string what, bool changes, Func<T> work, CancellationToken ct)
            {
                Calls.Add((what, changes));
                return Task.FromResult(work());
            }

            SettingEntry E(string key, SettingKind kind, string def, string description, Action<string>? set = null, Func<SettingEntry, SettingEntry>? tweak = null, string[]? options = null, double? min = null, double? max = null,
                bool restart = false, string cls = SettingClass.Normal, bool ends = false, string? use = null)
            {
                var (section, name) = (key.Split('.')[0], key.Split('.')[1]);
                return new SettingEntry
                {
                    Key = key, Section = section, Name = name, Kind = kind, Default = def, Description = description, Options = options, Min = min, Max = max, RestartRequired = restart,
                    Class = cls, EndsAgentConnection = ends, UseInstead = use,
                    Get = () => Values[key], Set = v => { if (set != null) set(v); Values[key] = v; },
                };
            }

            public IReadOnlyList<SettingEntry> Settings() => new[]
            {
                E("general.PatternLength", SettingKind.Int, "16", "Default pattern length.", min: 1, max: 512),
                E("general.AutomaticBackups", SettingKind.Bool, "True", "Rename the previous version when saving."),
                E("general.SequenceView", SettingKind.Choice, "ModernVertical", "Sequence view type.", options: new[] { "ModernVertical", "ModernHorizontal" }),
                E("advanced.Multithreading", SettingKind.Bool, "True", "Use several threads to render audio.", cls: SettingClass.Engine),
                E("advanced.AudioBufferFillThread", SettingKind.Bool, "False", "Use a background thread to fill the audio buffer (restart).", restart: true, cls: SettingClass.Engine),
                E("agent.AllowAgents", SettingKind.Bool, "False", "Let agents work in this app.", ends: true),
                E("agent.Port", SettingKind.Int, "7481", "The agent port.", min: 1, max: 65535, ends: true),
                E("safety.SafetyLimiter", SettingKind.Bool, "True", "The safety limiter.", cls: SettingClass.Safety),
                E("audio.BufferSize", SettingKind.Int, "256", "Frames per buffer.", use: "Use audio_set."),
            };

            public string SettingsHistoryFile => Path.Combine(Folder, "settings-history.jsonl");
            public IMidiBindings Midi => MidiFake;
            public IReadOnlyList<string> RecentSongs() => Recent;
            public AppFacts Facts() => new("0.8 (K3)", Mode, "Test OS x64", 99, "demo.bmxml", true, 120, false);
            public IViewControl? View => ViewFake;
            public IAudioControl? Audio => AudioFake;
        }

        static (McpServer Server, FakeApp App) Make(Action<ToolRegistry>? more = null)
        {
            var app = new FakeApp();
            var tools = new ToolRegistry();
            more?.Invoke(tools);
            AppTools.Register(tools, app);
            return (new McpServer(new ServerInfo { Name = "t", Title = "t", Version = "1" }, tools), app);
        }

        static (bool Error, string Text, JsonObject Data) Call(McpServer s, string tool, JsonObject? args = null)
        {
            var r = TestTools.CallTool(s, tool, args);
            var def = s.Tools.Find(tool)!;
            if (!r.Error && def.Output != null)
            {
                var problems = new List<string>();
                Schema.Validate(def.Output, r.Data, "", problems);
                Assert.That(problems, Is.Empty, $"{tool}'s result doesn't match its output schema");
            }
            return r;
        }

        static JsonObject Args(params (string Name, JsonNode? Value)[] pairs)
        {
            var o = new JsonObject();
            foreach (var (n, v) in pairs) o[n] = v;
            return o;
        }

        static readonly string[] AppToolNames =
        {
            "settings_get", "settings_set", "settings_reset", "settings_history", "midi_bindings_list", "midi_bind", "midi_unbind", "midi_learn", "audio_status", "audio_devices", "audio_set",
            "view_show", "machine_open_editor", "select", "panel_set", "window_layout", "export_audio", "app_status", "song_recent",
        };

        // ---- schemas -----------------------------------------------------------------------------

        [Test]
        public void EveryToolHasADescriptionSchemasAndAnnotations()
        {
            var (server, _) = Make();
            foreach (var name in AppToolNames)
            {
                var t = server.Tools.Find(name);
                Assert.That(t, Is.Not.Null, name);
                Assert.That(t!.Description.Length, Is.GreaterThan(80), name + " description");
                Assert.That(t.Description, Does.Contain("Example:"), name + " has an example");
                Assert.That(t.Input["type"]!.GetValue<string>(), Is.EqualTo("object"), name);
                Assert.That(t.Output, Is.Not.Null, name + " has an output schema");
                var json = t.ToJson();
                Assert.That(json["annotations"]!["title"]!.GetValue<string>(), Is.Not.Empty, name);
                foreach (var (prop, schema) in t.Input["properties"]!.AsObject())
                    Assert.That(schema!["description"]?.GetValue<string>() ?? "", Is.Not.Empty, $"{name}.{prop} is described");
            }
            Assert.That(AppToolNames.Where(n => ToolReference.AreaOf(n) == "Other"), Is.Empty, "every app tool is in an area of the tool reference");
        }

        [Test]
        public void AnnotationsSayWhichToolsReadAndWhichAreDestructive()
        {
            var (server, _) = Make();
            bool Ann(string tool, string hint) => server.Tools.Find(tool)!.ToJson()["annotations"]![hint]!.GetValue<bool>();
            foreach (var read in new[] { "settings_get", "settings_history", "midi_bindings_list", "audio_status", "audio_devices", "app_status", "song_recent" })
                Assert.That(Ann(read, "readOnlyHint"), Is.True, read);
            foreach (var destructive in new[] { "settings_set", "settings_reset", "audio_set", "midi_unbind" })
                Assert.That(Ann(destructive, "destructiveHint"), Is.True, destructive + " is destructive: clients ask the person");
            foreach (var plain in new[] { "midi_bind", "view_show", "select", "panel_set", "export_audio" })
                Assert.That(Ann(plain, "destructiveHint"), Is.False, plain);
            Assert.That(Ann("settings_set", "readOnlyHint"), Is.False);
        }

        // ---- settings ----------------------------------------------------------------------------

        [Test]
        public void SettingsGetListsFiltersAndExplains()
        {
            var (s, _) = Make();
            var all = Call(s, "settings_get");
            Assert.That(all.Data["total"]!.GetValue<int>(), Is.EqualTo(9));
            var one = Call(s, "settings_get", Args(("key", "general.PatternLength")));
            var row = one.Data["settings"]![0]!;
            Assert.That(row["value"]!.GetValue<string>(), Is.EqualTo("16"));
            Assert.That(row["type"]!.GetValue<string>(), Is.EqualTo("int"));
            Assert.That(row["min"]!.GetValue<double>(), Is.EqualTo(1));
            Assert.That(Call(s, "settings_get", Args(("key", "PatternLength"))).Data["count"]!.GetValue<int>(), Is.EqualTo(1), "a bare name works when unique");
            Assert.That(Call(s, "settings_get", Args(("section", "advanced"))).Data["settings"]!.AsArray().Select(x => x!["class"]!.GetValue<string>()), Is.All.EqualTo("engine"));
            Assert.That(Call(s, "settings_get", Args(("section", "safety"))).Data["settings"]![0]!["class"]!.GetValue<string>(), Is.EqualTo("safety"));
            Assert.That(Call(s, "settings_get", Args(("search", "background thread"))).Data["settings"]![0]!["restart_required"]!.GetValue<bool>(), Is.True);
            Assert.That(Call(s, "settings_get", Args(("changed_only", true))).Data["count"]!.GetValue<int>(), Is.EqualTo(1).Or.GreaterThanOrEqualTo(1), "agents on is a change from the default");
            var err = Call(s, "settings_get", Args(("key", "general.PatternLenght")));
            Assert.That(err.Error, Is.True);
            Assert.That(err.Text, Does.Contain("Did you mean \"general.PatternLength\""));
        }

        [Test]
        public void SettingsSetValidatesWritesLogsAndSaysWhenARestartIsNeeded()
        {
            var (s, app) = Make();
            var r = Call(s, "settings_set", Args(("key", "general.PatternLength"), ("value", 32)));
            Assert.That(r.Error, Is.False, r.Text);
            Assert.That(r.Data["previous"]!.GetValue<string>(), Is.EqualTo("16"));
            Assert.That(r.Data["value"]!.GetValue<long>(), Is.EqualTo(32));
            Assert.That(r.Data["restart_required"]!.GetValue<bool>(), Is.False);
            Assert.That(app.Values["general.PatternLength"], Is.EqualTo("32"));

            Assert.That(Call(s, "settings_set", Args(("key", "general.PatternLength"), ("value", 32))).Data["changed"]!.GetValue<bool>(), Is.False, "same value: no change, nothing logged");
            Assert.That(Call(s, "settings_set", Args(("key", "general.PatternLength"), ("value", 9999))).Text, Does.Contain("1 to 512"));
            Assert.That(Call(s, "settings_set", Args(("key", "general.PatternLength"), ("value", 3.5))).Text, Does.Contain("whole number"));
            Assert.That(Call(s, "settings_set", Args(("key", "general.AutomaticBackups"), ("value", "off"))).Data["value"]!.GetValue<bool>(), Is.False);
            Assert.That(app.Values["general.AutomaticBackups"], Is.EqualTo("False"));
            Assert.That(Call(s, "settings_set", Args(("key", "general.SequenceView"), ("value", "modernhorizontal"))).Data["value"]!.GetValue<string>(), Is.EqualTo("ModernHorizontal"));
            Assert.That(Call(s, "settings_set", Args(("key", "general.SequenceView"), ("value", "Sideways"))).Text, Does.Contain("ModernVertical, ModernHorizontal"));

            var restart = Call(s, "settings_set", Args(("key", "advanced.AudioBufferFillThread"), ("value", true)));
            Assert.That(restart.Data["restart_required"]!.GetValue<bool>(), Is.True);
            Assert.That(restart.Data["class"]!.GetValue<string>(), Is.EqualTo("engine"));
            Assert.That(restart.Text, Does.Contain("restart"));

            var audio = Call(s, "settings_set", Args(("key", "audio.BufferSize"), ("value", 128)));
            Assert.That(audio.Error, Is.True);
            Assert.That(audio.Text, Does.Contain("audio_set"));
            Assert.That(app.Values["audio.BufferSize"], Is.EqualTo("256"), "unchanged");
        }

        [Test]
        public void TheAuditLogKeepsEveryChangeNewestFirstWithTheValueItReplaced()
        {
            var (s, _) = Make();
            Call(s, "settings_set", Args(("key", "general.PatternLength"), ("value", 32)));
            Call(s, "settings_set", Args(("key", "advanced.Multithreading"), ("value", false)));
            Call(s, "settings_set", Args(("key", "general.PatternLength"), ("value", 64)));
            var h = Call(s, "settings_history");
            Assert.That(h.Data["count"]!.GetValue<int>(), Is.EqualTo(3));
            var first = h.Data["entries"]![0]!;
            Assert.That(first["key"]!.GetValue<string>(), Is.EqualTo("general.PatternLength"));
            Assert.That(first["from"]!.GetValue<string>(), Is.EqualTo("32"));
            Assert.That(first["to"]!.GetValue<string>(), Is.EqualTo("64"));
            Assert.That(h.Data["entries"]![1]!["class"]!.GetValue<string>(), Is.EqualTo("engine"));
            Assert.That(Call(s, "settings_history", Args(("key", "general.PatternLength"))).Data["count"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(Call(s, "settings_history", Args(("limit", 1))).Data["count"]!.GetValue<int>(), Is.EqualTo(1));
        }

        [Test]
        public void SettingsResetPutsBackOneKeyASectionOrEverythingExceptTheConnection()
        {
            var (s, app) = Make();
            Call(s, "settings_set", Args(("key", "general.PatternLength"), ("value", 32)));
            Call(s, "settings_set", Args(("key", "advanced.Multithreading"), ("value", false)));
            Call(s, "settings_set", Args(("key", "advanced.AudioBufferFillThread"), ("value", true)));
            Assert.That(Call(s, "settings_reset", new JsonObject()).Error, Is.True, "one of key, section, all");
            var one = Call(s, "settings_reset", Args(("key", "general.PatternLength")));
            Assert.That(one.Data["reset"]!.AsArray().Count, Is.EqualTo(1));
            Assert.That(app.Values["general.PatternLength"], Is.EqualTo("16"));
            var section = Call(s, "settings_reset", Args(("section", "advanced")));
            Assert.That(section.Data["reset"]!.AsArray().Count, Is.EqualTo(2));
            Assert.That(section.Data["restart_required"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { "advanced.AudioBufferFillThread" }));
            Assert.That(app.Values["advanced.Multithreading"], Is.EqualTo("True"));
            var all = Call(s, "settings_reset", Args(("all", true)));
            Assert.That(all.Data["skipped"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EquivalentTo(new[] { "agent.AllowAgents", "agent.Port", "audio.BufferSize" }), "the connection's own settings and the audio tool's are left alone");
            Assert.That(app.Values["agent.AllowAgents"], Is.EqualTo("True"), "still on: this call would have ended");
            Assert.That(Call(s, "settings_reset", Args(("key", "agent.AllowAgents"))).Data["reset"]!.AsArray().Count, Is.EqualTo(1), "by name it is reset");
            Assert.That(Call(s, "settings_reset", Args(("section", "nowhere"))).Error, Is.True);
            Assert.That(Call(s, "settings_history").Data["count"]!.GetValue<int>(), Is.EqualTo(3 + 1 + 2 + 1), "every reset is logged");
        }

        // ---- MIDI --------------------------------------------------------------------------------

        [Test]
        public void MidiBindingsListBindAndUnbindEachAsOneUndoStep()
        {
            var (s, app) = Make();
            Assert.That(Call(s, "midi_bindings_list").Data["count"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(app.Calls.Last(), Is.EqualTo(("midi_bindings_list", false)), "reading changes nothing");

            var bind = Call(s, "midi_bind", Args(("machine", "Bass"), ("parameter", "Volume"), ("channel", 2), ("controller", 7)));
            Assert.That(bind.Error, Is.False, bind.Text);
            Assert.That(bind.Data["binding"]!["controller"]!.GetValue<int>(), Is.EqualTo(7));
            Assert.That(app.Calls.Last(), Is.EqualTo(("midi_bind", true)), "one undo group");
            Assert.That(Call(s, "midi_bindings_list", Args(("machine", "Bass"))).Data["count"]!.GetValue<int>(), Is.EqualTo(2));

            var rebind = Call(s, "midi_bind", Args(("machine", "Bass"), ("parameter", "Cutoff"), ("channel", 1), ("controller", 71)));
            Assert.That(rebind.Data["replaced"]!["controller"]!.GetValue<int>(), Is.EqualTo(74), "says what it replaced");
            Assert.That(rebind.Text, Does.Contain("74"));

            Assert.That(Call(s, "midi_bind", Args(("machine", "Bass"), ("parameter", "Volume"), ("channel", 17), ("controller", 7))).Error, Is.True);
            Assert.That(Call(s, "midi_bind", Args(("machine", "Bass"), ("parameter", "Volume"), ("channel", 1), ("controller", 200))).Error, Is.True);
            var bad = Call(s, "midi_bind", Args(("machine", "Baas"), ("parameter", "Volume"), ("channel", 1), ("controller", 7)));
            Assert.That(bad.Error, Is.True);
            Assert.That(bad.Text, Does.Contain("Did you mean"));

            var un = Call(s, "midi_unbind", Args(("machine", "Bass"), ("parameter", "Volume")));
            Assert.That(un.Data["removed"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(app.Calls.Last(), Is.EqualTo(("midi_unbind", true)));
            Assert.That(Call(s, "midi_unbind", Args(("machine", "Bass"), ("parameter", "Volume"))).Data["removed"]!.GetValue<int>(), Is.EqualTo(0));
            Assert.That(Call(s, "midi_unbind", Args(("machine", "Bass"), ("all", true))).Data["removed"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(Call(s, "midi_unbind", Args(("machine", "Bass"))).Error, Is.True, "parameter or all");
        }

        [Test]
        public async Task MidiLearnWaitsForAControllerMoveAndBindsIt()
        {
            var (s, app) = Make();
            var task = Task.Run(() => Call(s, "midi_learn", Args(("machine", "Lead"), ("parameter", "Cutoff"), ("timeout_seconds", 5))));
            for (int i = 0; i < 100 && !app.MidiFake.Listening; i++) await Task.Delay(20);
            Assert.That(app.MidiFake.Listening, Is.True, "listening while it waits");
            app.MidiFake.Seen!(3, 21);
            var r = await task;
            Assert.That(r.Error, Is.False, r.Text);
            Assert.That(r.Data["channel"]!.GetValue<int>(), Is.EqualTo(3));
            Assert.That(r.Data["controller"]!.GetValue<int>(), Is.EqualTo(21));
            Assert.That(r.Data["binding"]!["machine"]!.GetValue<string>(), Is.EqualTo("Lead"));
            Assert.That(app.MidiFake.Bindings, Does.Contain(new MidiBindingInfo("Lead", "Cutoff", 0, 3, 21)));
            Assert.That(app.MidiFake.Listening, Is.False, "stopped listening");
            Assert.That(app.Calls.Last(), Is.EqualTo(("midi_learn", true)), "the binding is one undo step");
        }

        [Test]
        public async Task MidiLearnWithoutATargetOnlyReportsAndTimesOutWithoutChangingAnything()
        {
            var (s, app) = Make();
            var task = Task.Run(() => Call(s, "midi_learn", Args(("timeout_seconds", 5))));
            for (int i = 0; i < 100 && !app.MidiFake.Listening; i++) await Task.Delay(20);
            app.MidiFake.Seen!(1, 1);
            var r = await task;
            Assert.That(r.Data["controller"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(r.Data.ContainsKey("binding"), Is.False);
            Assert.That(app.MidiFake.Bindings.Count, Is.EqualTo(1));
            Assert.That(app.Calls.Where(c => c.What == "midi_learn" && c.Changes), Is.Empty, "nothing to undo");

            var half = Call(s, "midi_learn", Args(("machine", "Bass")));
            Assert.That(half.Error, Is.True, "machine and parameter go together");
            var timeout = await Task.Run(() => TestTools.CallTool(s, "midi_learn", Args(("timeout_seconds", 1), ("machine", "Bass"), ("parameter", "Volume"))));
            Assert.That(timeout.Error, Is.True);
            Assert.That(timeout.Text, Does.Contain("No controller moved"));
            Assert.That(app.MidiFake.Bindings.Count, Is.EqualTo(1), "nothing bound");
            Assert.That(app.MidiFake.Listening, Is.False);
        }

        // ---- export ------------------------------------------------------------------------------

        /// <summary>A render that writes a float WAV of a quiet sine, as the real one does, with a probe per machine.</summary>
        static void FakeRender(ToolRegistry tools, string folder, List<JsonObject> calls)
        {
            tools.Add(new ToolDef
            {
                Name = "machine_list", Title = "Machines", Description = "Lists.", Input = Object(null),
                Handler = _ => Task.FromResult(ToolResult.Ok("machines", new JsonObject
                {
                    ["machines"] = new JsonArray(
                        new JsonObject { ["name"] = "Master", ["kind"] = "master" },
                        new JsonObject { ["name"] = "Bass", ["kind"] = "generator" },
                        new JsonObject { ["name"] = "Lead 1", ["kind"] = "generator" },
                        new JsonObject { ["name"] = "Verb", ["kind"] = "effect" }),
                })),
            });
            tools.Add(new ToolDef
            {
                Name = "render", Title = "Render", Description = "Renders.", Input = Object(null),
                Handler = c =>
                {
                    calls.Add((JsonObject)c.Arguments.DeepClone());
                    string File(string name)
                    {
                        string path = Path.Combine(folder, name);
                        var data = new float[2 * 4800];
                        for (int i = 0; i < 4800; i++) data[2 * i] = data[2 * i + 1] = 0.25f * MathF.Sin(i * 0.05f);
                        Wav.WriteStereoFloat(path, data, 4800, 48000);
                        return path;
                    }
                    var probes = new JsonArray();
                    foreach (var p in c.Arguments["probes"] as JsonArray ?? new JsonArray())
                        probes.Add(new JsonObject { ["id"] = "r1:" + p, ["machine"] = p!.GetValue<string>(), ["file"] = File("probe-" + p!.GetValue<string>() + ".wav") });
                    return Task.FromResult(ToolResult.Ok("rendered", new JsonObject { ["id"] = "r1", ["file"] = File("master.wav"), ["probes"] = probes, ["warnings"] = new JsonArray() }));
                },
            });
        }

        [Test]
        public void ExportAudioWritesTheMasterAndStemsAtTheBitDepthAndNeverOverwritesUnlessAsked()
        {
            var calls = new List<JsonObject>();
            string work = Path.Combine(Path.GetTempPath(), "export-work-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var (s, _) = Make(t => FakeRender(t, work, calls));
                string path = Path.Combine(work, "out", "demo.wav");
                var r = Call(s, "export_audio", Args(("path", path), ("range", "loop"), ("stems", "generators"), ("tail_seconds", 1.0)));
                Assert.That(r.Error, Is.False, r.Text);
                Assert.That(r.Data["bits"]!.GetValue<int>(), Is.EqualTo(24), "24 bit by default");
                Assert.That(r.Data["dither"]!.GetValue<string>(), Is.EqualTo("tpdf"));
                var files = r.Data["files"]!.AsArray();
                Assert.That(files.Select(f => Path.GetFileName(f!["path"]!.GetValue<string>())), Is.EqualTo(new[] { "demo.wav", "demo-Bass.wav", "demo-Lead-1.wav" }), "master first; effects are not stems");
                Assert.That(files[0]!["role"]!.GetValue<string>(), Is.EqualTo("master"));
                foreach (var f in files) Assert.That(File.Exists(f!["path"]!.GetValue<string>()), Is.True);
                Assert.That(calls.Single()["probes"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { "Bass", "Lead 1" }));
                Assert.That(calls.Single()["range"]!.GetValue<string>(), Is.EqualTo("loop"));
                Assert.That(calls.Single()["tail_seconds"]!.GetValue<double>(), Is.EqualTo(1));
                var back = Wav.Read(path);
                Assert.That(back.Rate, Is.EqualTo(48000));
                Assert.That(back.Frames, Is.EqualTo(4800));
                Assert.That(new FileInfo(path).Length, Is.EqualTo(44 + 4800 * 2 * 3), "24 bit PCM stereo");
                Assert.That(r.Data["measure"]!["silent"]!.GetValue<bool>(), Is.False);

                long before = new FileInfo(path).Length;
                File.WriteAllText(Path.Combine(work, "out", "demo-Bass.wav"), "keep me");
                var refused = Call(s, "export_audio", Args(("path", path)));
                Assert.That(refused.Error, Is.True);
                Assert.That(refused.Text, Does.Contain("overwrite=true"));
                Assert.That(calls.Count, Is.EqualTo(1), "refused before rendering");
                var refusedStems = Call(s, "export_audio", Args(("path", Path.Combine(work, "out", "demo2.wav")), ("overwrite", false), ("machines", new JsonArray("Bass")), ("stems", "none")));
                Assert.That(refusedStems.Error, Is.False, "a new master name has no clash");
                Assert.That(Call(s, "export_audio", Args(("path", path), ("bits", 16), ("dither", "off"), ("machines", new JsonArray("Bass")))).Error, Is.True, "master and stem files exist");
                Assert.That(File.ReadAllText(Path.Combine(work, "out", "demo-Bass.wav")), Is.EqualTo("keep me"), "nothing was written over it");

                var again = Call(s, "export_audio", Args(("path", path), ("bits", 16), ("dither", "light"), ("overwrite", true)));
                Assert.That(again.Error, Is.False, again.Text);
                Assert.That(new FileInfo(path).Length, Is.EqualTo(44 + 4800 * 2 * 2), "16 bit now");
                var f32 = Call(s, "export_audio", Args(("path", Path.Combine(work, "f.wav")), ("bits", 32)));
                Assert.That(new FileInfo(Path.Combine(work, "f.wav")).Length, Is.GreaterThan(4800 * 2 * 4), "32-bit float");
                Assert.That(f32.Data.ContainsKey("dither"), Is.False);
            }
            finally
            {
                Directory.Delete(work, true);
            }
        }

        [Test]
        public void ExportAudioChecksItsArgumentsBeforeRendering()
        {
            var calls = new List<JsonObject>();
            string work = Path.Combine(Path.GetTempPath(), "export-args-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var (s, _) = Make(t => FakeRender(t, work, calls));
                Assert.That(Call(s, "export_audio", Args(("path", "demo.wav"))).Text, Does.Contain("full path"));
                Assert.That(Call(s, "export_audio", Args(("path", Path.Combine(work, "demo.mp3")))).Text, Does.Contain(".wav"));
                Assert.That(Call(s, "export_audio", Args(("path", work))).Text, Does.Contain("folder"));
                Assert.That(Call(s, "export_audio", Args(("path", Path.Combine(work, "a.wav")), ("bits", 20))).Error, Is.True);
                Assert.That(Call(s, "export_audio", Args(("path", Path.Combine(work, "a.wav")), ("bits", 32), ("dither", "tpdf"))).Text, Does.Contain("dither goes with"));
                Assert.That(Call(s, "export_audio", Args(("path", Path.Combine(work, "a.wav")), ("dither", "blue"))).Error, Is.True);
                Assert.That(calls, Is.Empty, "no render ran");
                Assert.That(Directory.GetFiles(work, "a*.wav"), Is.Empty);
            }
            finally
            {
                Directory.Delete(work, true);
            }
        }

        // ---- app ---------------------------------------------------------------------------------

        [Test]
        public void AppStatusReportsVersionModeCpuAndMemory()
        {
            AppInfoTools.CpuSampleMs = 50;
            var (s, _) = Make();
            var r = Call(s, "app_status");
            Assert.That(r.Error, Is.False, r.Text);
            Assert.That(r.Data["version"]!.GetValue<string>(), Is.EqualTo("0.8 (K3)"));
            Assert.That(r.Data["mode"]!.GetValue<string>(), Is.EqualTo("studio"));
            Assert.That(r.Data["memory"]!["working_set_mb"]!.GetValue<double>(), Is.GreaterThan(0));
            Assert.That(r.Data["cpu"]!["cores"]!.GetValue<int>(), Is.GreaterThanOrEqualTo(1));
            Assert.That(r.Data["cpu"]!["process_percent"]!.GetValue<double>(), Is.InRange(0, 100.5));
            Assert.That(r.Data["song"]!["unsaved_changes"]!.GetValue<bool>(), Is.True);
            Assert.That(r.Data["tools"]!.GetValue<int>(), Is.EqualTo(99));
        }

        [Test]
        public void SongRecentListsTheFilesNewestFirstAndSaysWhichAreGone()
        {
            var (s, app) = Make();
            Assert.That(Call(s, "song_recent").Text, Does.Contain("No recent"));
            string here = Path.Combine(app.Folder, "here.bmxml");
            File.WriteAllText(here, "x");
            app.Recent = new List<string> { here, Path.Combine(app.Folder, "gone.bmxml") };
            var r = Call(s, "song_recent");
            var songs = r.Data["songs"]!.AsArray();
            Assert.That(songs[0]!["exists"]!.GetValue<bool>(), Is.True);
            Assert.That(songs[0]!["modified"], Is.Not.Null);
            Assert.That(songs[1]!["exists"]!.GetValue<bool>(), Is.False);
            Assert.That(songs[1]!["name"]!.GetValue<string>(), Is.EqualTo("gone.bmxml"));
            Assert.That(Call(s, "song_recent", Args(("limit", 1))).Data["songs"]!.AsArray().Count, Is.EqualTo(1));
        }

        // ---- audio -------------------------------------------------------------------------------

        [SetUp]
        public void FastTimers()
        {
            AudioTools.FlowTimeout = TimeSpan.FromSeconds(1);
            AudioTools.Settle = TimeSpan.FromMilliseconds(300);
            AudioTools.Poll = TimeSpan.FromMilliseconds(20);
        }

        [Test]
        public void AudioStatusAndDevicesReportTheOutput()
        {
            var (s, _) = Make();
            var st = Call(s, "audio_status");
            Assert.That(st.Data["device"]!.GetValue<string>(), Is.EqualTo("PortAudio: default"));
            Assert.That(st.Data["buffer_frames"]!.GetValue<int>(), Is.EqualTo(256));
            Assert.That(st.Data["latency_ms"]!.GetValue<double>(), Is.EqualTo(5.33).Within(0.01));
            Assert.That(st.Text, Does.Contain("48000 Hz"));
            var d = Call(s, "audio_devices");
            Assert.That(d.Data["devices"]!.AsArray().Count, Is.EqualTo(3));
            Assert.That(d.Data["devices"]![0]!["current"]!.GetValue<bool>(), Is.True);
            Assert.That(d.Data["buffer_frames"]!.AsArray().Select(x => x!.GetValue<int>()), Does.Contain(128));
        }

        [Test]
        public void AudioSetChangesTheOutputAndKeepsItWhenAudioFlows()
        {
            var (s, app) = Make();
            var r = Call(s, "audio_set", Args(("buffer_frames", 128)));
            Assert.That(r.Error, Is.False, r.Text);
            Assert.That(r.Data["applied"]!.GetValue<bool>(), Is.True);
            Assert.That(r.Data["reverted"]!.GetValue<bool>(), Is.False);
            Assert.That(app.AudioFake!.Config.BufferFrames, Is.EqualTo(128));
            var dev = Call(s, "audio_set", Args(("device", "portaudio: usb dac"), ("sample_rate", 48000)));
            Assert.That(dev.Data["settings"]!["device"]!.GetValue<string>(), Is.EqualTo("PortAudio: USB DAC"));
            Assert.That(app.AudioFake.Config, Is.EqualTo(new AudioConfig("PortAudio: USB DAC", 128, 48000)));
            Assert.That(Call(s, "audio_set", Args(("buffer_frames", 128))).Text, Does.Contain("already"), "nothing to change");
        }

        [Test]
        public void AudioSetTakesTheChangeBackWhenNoAudioFlows()
        {
            var (s, app) = Make();
            var r = Call(s, "audio_set", Args(("device", "PortAudio: Broken")));
            Assert.That(r.Error, Is.True, "the call says it did not stick");
            Assert.That(r.Data["applied"]!.GetValue<bool>(), Is.False);
            Assert.That(r.Data["reverted"]!.GetValue<bool>(), Is.True);
            Assert.That(r.Data["reason"]!.GetValue<string>(), Does.Contain("No audio flowed"));
            Assert.That(app.AudioFake!.Config.Device, Is.EqualTo("PortAudio: default"), "the previous device is back");
            Assert.That(app.AudioFake.Applied.Select(a => a.Device), Is.EqualTo(new[] { "PortAudio: Broken", "PortAudio: default" }));
            Assert.That(r.Text, Does.Contain("previous settings"));
        }

        [Test]
        public void AudioSetTakesTheChangeBackWhenTheDropoutsSpike()
        {
            var (s, app) = Make();
            app.AudioFake!.DropoutsPerPoll = 2;
            var r = Call(s, "audio_set", Args(("buffer_frames", 32)));
            Assert.That(r.Data["applied"]!.GetValue<bool>(), Is.False);
            Assert.That(r.Data["reverted"]!.GetValue<bool>(), Is.True);
            Assert.That(r.Data["reason"]!.GetValue<string>(), Does.Contain("dropouts"));
            Assert.That(app.AudioFake.Config.BufferFrames, Is.EqualTo(256));
        }

        [Test]
        public void AudioSetChecksItsArguments()
        {
            var (s, app) = Make();
            Assert.That(Call(s, "audio_set").Error, Is.True);
            var near = Call(s, "audio_set", Args(("device", "PortAudio: USB DA")));
            Assert.That(near.Error, Is.True);
            Assert.That(near.Text, Does.Contain("Did you mean \"PortAudio: USB DAC\""));
            Assert.That(Call(s, "audio_set", Args(("sample_rate", 12345))).Error, Is.True);
            app.AudioFake!.Settable = false;
            Assert.That(Call(s, "audio_set", Args(("buffer_frames", 128))).Text, Does.Contain("control panel"));
            Assert.That(app.AudioFake.Applied, Is.Empty, "nothing was opened");
            app.AudioFake = null;
            app.Mode = "headless";
            var none = Call(s, "audio_status");
            Assert.That(none.Error, Is.True);
            Assert.That(none.Text, Does.Contain("headless"));
        }

        // ---- view --------------------------------------------------------------------------------

        [Test]
        public void ViewToolsSayStudioOnlyWhereThereIsNoWindow()
        {
            var (s, app) = Make();
            foreach (var (tool, args) in new[] { ("view_show", Args(("view", "Session"))), ("machine_open_editor", Args(("machine", "Bass"))), ("select", Args(("machine", "Bass"))),
                                                 ("panel_set", Args(("panel", "browser"), ("open", true))), ("window_layout", new JsonObject()) })
            {
                app.Mode = "headless";
                var r = Call(s, tool, args);
                Assert.That(r.Error, Is.True, tool);
                Assert.That(r.Text, Does.Contain("Studio only"), tool);
                Assert.That(r.Text, Does.Contain("headless"), tool);
            }
            app.Mode = "live";
            Assert.That(Call(s, "view_show").Text, Does.Contain("live mode"));
            app.Mode = "studio";
            Assert.That(Call(s, "view_show").Text, Does.Contain("Studio only"), "a studio without view control says so too");
        }

        [Test]
        public void ViewToolsDriveTheWindowAndReportItsState()
        {
            var (s, app) = Make();
            var v = app.ViewFake = new FakeView();
            Assert.That(Call(s, "view_show", Args(("view", "session"))).Data["view"]!.GetValue<string>(), Is.EqualTo("Session"));
            Assert.That(Call(s, "view_show", Args(("view", "Sequence"))).Data["view"]!.GetValue<string>(), Is.EqualTo("Arrangement"), "the old name works");
            Assert.That(Call(s, "view_show", Args(("view", "Mixer"))).Error, Is.True);
            var roll = Call(s, "view_show", Args(("machine", "Bass"), ("pattern", "Verse"), ("editor", "piano_roll")));
            Assert.That(roll.Data["detail"]!["page"]!.GetValue<string>(), Is.EqualTo("piano_roll"));
            Assert.That(roll.Data["selection"]!["pattern"]!.GetValue<string>(), Is.EqualTo("Verse"));
            Assert.That(Call(s, "view_show", Args(("machine", "Bass"))).Error, Is.True, "machine goes with pattern");
            Assert.That(Call(s, "view_show", Args(("view", "Session"), ("machine", "Bass"), ("pattern", "Verse"))).Error, Is.True);

            Assert.That(Call(s, "machine_open_editor", Args(("machine", "Bass"), ("window", "parameters"))).Error, Is.False);
            Assert.That(Call(s, "machine_open_editor", Args(("machine", "Bass"), ("window", "editor"))).Text, Does.Contain("no editor window"));

            var sel = Call(s, "select", Args(("machine", "Lead"), ("scene", 2)));
            Assert.That(sel.Data["selection"]!["scene"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(Call(s, "select", Args(("clear", true))).Data["selection"]!.AsObject().Count, Is.EqualTo(0));
            Assert.That(Call(s, "select", new JsonObject()).Error, Is.True);
            Assert.That(Call(s, "select", Args(("machine", "Bass"), ("clear", true))).Error, Is.True);

            var p = Call(s, "panel_set", Args(("panel", "browser"), ("open", false)));
            Assert.That(p.Data["browser"]!["open"]!.GetValue<bool>(), Is.False);
            Assert.That(Call(s, "panel_set", Args(("panel", "inspector"), ("tab", "Song"))).Data["inspector"]!["tab"]!.GetValue<string>(), Is.EqualTo("Song"));
            Assert.That(Call(s, "panel_set", Args(("panel", "detail"), ("maximised", true))).Data["detail"]!["maximised"]!.GetValue<bool>(), Is.True);
            Assert.That(Call(s, "panel_set", Args(("panel", "browser"), ("tab", "Song"))).Error, Is.True, "tab is the inspector's");
            Assert.That(Call(s, "panel_set", Args(("panel", "browser"))).Error, Is.True, "something to set");
            Assert.That(Call(s, "panel_set", Args(("panel", "console"), ("open", true))).Error, Is.True);

            var w = Call(s, "window_layout", Args(("width", 1400), ("height", 900)));
            Assert.That(w.Data["window"]!["width"]!.GetValue<double>(), Is.EqualTo(1400));
            Assert.That(Call(s, "window_layout").Data["window"]!["height"]!.GetValue<double>(), Is.EqualTo(900), "no arguments: just reads");
        }

        [Test]
        public void NothingTakesTheKeyboardOrOpensAWindowWhileRecording()
        {
            var (s, app) = Make();
            var v = app.ViewFake = new FakeView { Recording = true };
            var pattern = Call(s, "view_show", Args(("machine", "Bass"), ("pattern", "Verse")));
            Assert.That(pattern.Error, Is.True);
            Assert.That(pattern.Text, Does.Contain("recording"));
            var window = Call(s, "machine_open_editor", Args(("machine", "Bass")));
            Assert.That(window.Error, Is.True);
            Assert.That(window.Text, Does.Contain("Stop recording"));
            Assert.That(v.Calls, Is.Empty, "nothing happened in the window");
            Assert.That(Call(s, "view_show", Args(("view", "Session"))).Error, Is.False, "switching a view takes nothing");
            Assert.That(Call(s, "select", Args(("machine", "Bass"))).Data["recording"]!.GetValue<bool>(), Is.True);
        }
    }
}
