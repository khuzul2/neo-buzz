// Fork (docs/specs/agent-knowledge.md §5, K3): the app-control tools against the real studio, headless: the settings are the
// ones Preferences shows, a MIDI binding is one undo step on a real parameter, the view tools move the real window,
// and export_audio renders the real song to files. The portable tests (ReBuzz.AgentTests) cover each tool over fakes.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Common;
using ReBuzz.Agent;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class AppControlStudioTests
    {
        static StudioHost Host => Studio.Host;

        static void Settle(Window window = null)
        {
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>A tool call on the studio's agent server (the UI thread runs it meanwhile); the whole result.</summary>
        static (bool Error, string Text, JsonObject Data) Try(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            var r = task.GetAwaiter().GetResult()!;
            if (r["error"] is JsonObject e) return (true, e["message"]!.GetValue<string>(), new JsonObject());
            var result = r["result"]!.AsObject();
            return (result["isError"]?.GetValue<bool>() == true, result["content"]![0]!["text"]!.GetValue<string>(), result["structuredContent"] as JsonObject ?? new JsonObject());
        }

        static JsonObject Call(string tool, JsonObject args = null)
        {
            var r = Try(tool, args);
            Assert.That(r.Error, Is.False, $"{tool}: {r.Text}");
            return r.Data;
        }

        static JsonObject Args(params (string Name, JsonNode Value)[] pairs)
        {
            var o = new JsonObject();
            foreach (var (n, v) in pairs) o[n] = v;
            return o;
        }

        static MainWindow NewSong()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            SongFiles.New().Wait();
            var window = new MainWindow(Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle(window);
            return window;
        }

        [AvaloniaTest]
        public void TheSettingsAreTheOnesPreferencesShowsAndEveryChangeIsLogged()
        {
            var window = NewSong();
            using var restore = Studio.AfterTest(() =>
            {
                Global.GeneralSettings.Set("PatternLength", 16);
                Global.GeneralSettings.Save();
                Dialogs.Responder = null;
                window.Close();
            });
            var all = Call("settings_get")["settings"]!.AsArray().Select(s => s["key"]!.GetValue<string>()).ToList();
            Assert.That(all, Does.Contain("general.PatternLength"));
            Assert.That(all, Does.Contain("advanced.Multithreading"));
            Assert.That(all, Does.Contain("midi.MasterKeyboardMode"));
            Assert.That(all, Does.Contain("appearance.Theme"), "the studio's own settings");
            Assert.That(all, Does.Contain("agent.AllowAgents"));
            Assert.That(all, Does.Contain("safety.SafetyLimiter"));
            Assert.That(all, Does.Contain("audio.AudioThreads"));
            Assert.That(Call("settings_get", Args(("section", "advanced")))["settings"]!.AsArray().Select(s => s["class"]!.GetValue<string>()), Is.All.EqualTo("engine"));
            var agentRow = Call("settings_get", Args(("key", "agent.Port")))["settings"]![0]!;
            Assert.That(agentRow["type"]!.GetValue<string>(), Is.EqualTo("int"));
            Assert.That(agentRow["note"]!.GetValue<string>(), Does.Contain("ends this agent connection"));

            var set = Call("settings_set", Args(("key", "general.PatternLength"), ("value", 32)));
            Assert.That(set["previous"]!.GetValue<string>(), Is.EqualTo("16"));
            Assert.That(Global.GeneralSettings.PatternLength, Is.EqualTo(32), "the setting itself");
            Assert.That(Try("settings_set", Args(("key", "general.PatternLength"), ("value", 5000))).Error, Is.True);
            var restartKey = Call("settings_get", Args(("search", "restart")))["settings"]!.AsArray().First(s => s["restart_required"]!.GetValue<bool>());
            Assert.That(restartKey["description"]!.GetValue<string>(), Does.Contain("estart"));
            var history = Call("settings_history", Args(("key", "general.PatternLength")))["entries"]!.AsArray();
            Assert.That(history[0]!["from"]!.GetValue<string>(), Is.EqualTo("16"));
            Assert.That(history[0]!["to"]!.GetValue<string>(), Is.EqualTo("32"));
            Call("settings_reset", Args(("key", "general.PatternLength")));
            Assert.That(Global.GeneralSettings.PatternLength, Is.EqualTo(16));

            // The safety limiter is the real one.
            var core = Host.Core;
            Assert.That(Call("settings_set", Args(("key", "safety.LimiterCeilingDb"), ("value", -3.0)))["class"]!.GetValue<string>(), Is.EqualTo("safety"));
            Assert.That(core.SafeMaster.CeilingDb, Is.EqualTo(-3));
            Call("settings_reset", Args(("key", "safety.LimiterCeilingDb")));
            Assert.That(core.SafeMaster.CeilingDb, Is.EqualTo(0));
            Assert.That(Try("settings_set", Args(("key", "audio.BufferSize"), ("value", 128))).Text, Does.Contain("audio_set"));
        }

        [AvaloniaTest]
        public void AMidiBindingIsOneUndoStepOnARealParameter()
        {
            var window = NewSong();
            using var restore = Studio.AfterTest(() =>
            {
                Dialogs.Responder = null;
                window.Close();
            });
            Call("machine_add", Args(("gear", "Neo Poly"), ("name", "Binder")));
            Settle(window);
            var core = Host.Core;
            var machine = core.SongCore.MachinesList.Single(m => m.Name == "Binder");
            var param = SongModel.Params(machine).First(p => !p.IsTrack);
            string name = param.Name;
            var stack = core.SongCore.ActionStack;
            int steps = stack.Actions.Count();

            var bound = Call("midi_bind", Args(("machine", "Binder"), ("parameter", name), ("channel", 3), ("controller", 21)));
            Assert.That(param.Parameter.GetMIDIBinding(0), Is.EqualTo((2, 21)), "channel 3 is 2 in the engine");
            Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 1), "one undo step");
            Assert.That(Call("midi_bindings_list", Args(("machine", "Binder")))["bindings"]![0]!["controller"]!.GetValue<int>(), Is.EqualTo(21));
            Assert.That(Call("midi_bindings_list")["count"]!.GetValue<int>(), Is.EqualTo(1));

            // A new controller replaces it, and the old one stops driving the parameter.
            var again = Call("midi_bind", Args(("machine", "Binder"), ("parameter", name), ("channel", 1), ("controller", 74)));
            Assert.That(again["replaced"]!["controller"]!.GetValue<int>(), Is.EqualTo(21));
            Assert.That(core.MidiControllerAssignments.ContollerBindings.Count(b => b.Parameter == param.Parameter), Is.EqualTo(1));

            stack.Undo();
            Assert.That(param.Parameter.GetMIDIBinding(0), Is.EqualTo((2, 21)), "undo gives back the one it replaced");
            stack.Undo();
            Assert.That(param.Parameter.GetMIDIBinding(0), Is.EqualTo((-1, -1)), "undo unbinds");
            stack.Redo();
            Assert.That(param.Parameter.GetMIDIBinding(0), Is.EqualTo((2, 21)));

            Assert.That(Call("midi_unbind", Args(("machine", "Binder"), ("parameter", name)))["removed"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(param.Parameter.GetMIDIBinding(0), Is.EqualTo((-1, -1)));
            stack.Undo();
            Assert.That(param.Parameter.GetMIDIBinding(0), Is.EqualTo((2, 21)), "undo binds it again");
            Assert.That(Call("midi_unbind", Args(("machine", "Binder"), ("all", true)))["removed"]!.GetValue<int>(), Is.EqualTo(1));

            // Learn: a control change from the MIDI input.
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "midi_learn", ["arguments"] = Args(("machine", "Binder"), ("parameter", name), ("timeout_seconds", 20)) },
            };
            var learning = Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            for (int i = 0; i < 1000 && !learning.IsCompleted; i++)
            {
                Dispatcher.UIThread.RunJobs();
                core.SendMIDIInput(0xB0 | 4 | (33 << 8) | (64 << 16));      // CC 33 on channel 5
                Thread.Sleep(5);
            }
            Assert.That(learning.IsCompleted, Is.True, "midi_learn answered");
            var result = learning.GetAwaiter().GetResult()["result"].AsObject();
            Assert.That(result["isError"].GetValue<bool>(), Is.False, result.ToJsonString());
            Assert.That(result["structuredContent"]["channel"].GetValue<int>(), Is.EqualTo(5));
            Assert.That(param.Parameter.GetMIDIBinding(0), Is.EqualTo((4, 33)));
            Assert.That(Try("midi_bind", Args(("machine", "Binder"), ("parameter", "No such parameter"), ("channel", 1), ("controller", 1))).Text, Does.Contain("Unknown parameter"));
        }

        [AvaloniaTest]
        public void TheViewToolsMoveTheRealWindowAndRefuseWhileRecording()
        {
            Studio.ResetPanels();
            var window = NewSong();
            var core = Host.Core;
            using var restore = Studio.AfterTest(() =>
            {
                core.Recording = false;
                Dialogs.Responder = null;
                // The Inspector's open flag, tab and width and the Browser's width persist in the registry, and the next
                // test's window (the Session view's clip panel lives in the Inspector) starts from them.
                Call("panel_set", Args(("panel", "inspector"), ("open", true), ("tab", "Selection"), ("size", 288)));
                Call("panel_set", Args(("panel", "browser"), ("open", false), ("size", 224)));
                window.Close();
                Studio.ResetPanels();
            });
            Call("machine_add", Args(("gear", "Neo Poly"), ("name", "Viewed")));
            Settle(window);
            Assert.That(Call("view_show", Args(("view", "Session")))["view"]!.GetValue<string>(), Is.EqualTo("Session"));
            Assert.That((window.Tabs.SelectedItem as TabItem)?.Header, Is.EqualTo("Session"));
            Call("view_show", Args(("view", "Machines")));
            Assert.That((window.Tabs.SelectedItem as TabItem)?.Header, Is.EqualTo("Machines"));

            var panels = Call("panel_set", Args(("panel", "browser"), ("open", true), ("size", 300)));
            Assert.That(window.Browser.IsOpen, Is.True);
            Assert.That(window.Browser.PanelWidth, Is.EqualTo(300));
            Assert.That(panels["browser"]!["open"]!.GetValue<bool>(), Is.True);
            Call("panel_set", Args(("panel", "browser"), ("open", false)));
            Assert.That(window.Browser.IsOpen, Is.False);
            Call("panel_set", Args(("panel", "inspector"), ("tab", "Song")));
            Assert.That(window.Inspector.CurrentTab, Is.EqualTo("Song"));
            Call("panel_set", Args(("panel", "inspector"), ("open", false)));
            Assert.That(window.Inspector.IsOpen, Is.False);

            var sel = Call("select", Args(("machine", "Viewed")));
            Assert.That(sel["selection"]!["machine"]!.GetValue<string>(), Is.EqualTo("Viewed"));
            Assert.That(ReBuzz.Studio.Views.StudioSelection.For(core).Machine?.Name, Is.EqualTo("Viewed"));
            Assert.That(Try("select", Args(("machine", "Viewd"))).Text, Does.Contain("Did you mean \"Viewed\""));
            Call("select", Args(("clear", true)));
            Assert.That(ReBuzz.Studio.Views.StudioSelection.For(core).Machine, Is.Null);

            var pattern = Call("pattern_create", Args(("machine", "Viewed"), ("name", "Shown"), ("length_beats", 4)));
            Settle(window);
            var roll = Call("view_show", Args(("machine", "Viewed"), ("pattern", "Shown"), ("editor", "piano_roll")));
            Assert.That(roll["detail"]!["page"]!.GetValue<string>(), Is.EqualTo("piano_roll"));
            Assert.That(window.Detail.IsOpen, Is.True);
            Assert.That(window.Detail.IsPianoRoll, Is.True);
            Call("view_show", Args(("machine", "Viewed"), ("pattern", "Shown")));
            Assert.That(window.Detail.IsPianoRoll, Is.False, "the Tracker");

            var layout = Call("window_layout", Args(("width", 1100), ("height", 700)));
            Settle(window);
            Assert.That(window.Width, Is.EqualTo(1100));
            Assert.That(Call("window_layout")["window"]!["width"]!.GetValue<double>(), Is.EqualTo(window.Bounds.Width));

            // Recording: no pattern editor and no machine window over the person playing.
            core.Recording = true;
            Assert.That(Call("window_layout")["recording"]!.GetValue<bool>(), Is.True);
            Assert.That(Try("view_show", Args(("machine", "Viewed"), ("pattern", "Shown"))).Text, Does.Contain("recording"));
            Assert.That(Try("machine_open_editor", Args(("machine", "Viewed"))).Text, Does.Contain("Stop recording"));
            Assert.That(Try("view_show", Args(("view", "Session"))).Error, Is.False, "a view switch takes nothing");
            core.Recording = false;
            Assert.That(Try("machine_open_editor", Args(("machine", "Viewed"), ("window", "parameters"))).Error, Is.False);
            Settle(window);
            Assert.That(ReBuzz.Studio.Platform.StudioMachineUiHost.ParameterWindowOf(core.SongCore.MachinesList.Single(m => m.Name == "Viewed")), Is.Not.Null, "the window opened");
        }

        [AvaloniaTest]
        public void ExportAudioRendersTheRealSongToFilesAndNeverOverwrites()
        {
            var window = NewSong();
            string folder = Path.Combine(Path.GetTempPath(), "app-control-export-" + Environment.ProcessId);
            using var restore = Studio.AfterTest(() =>
            {
                Dialogs.Responder = null;
                window.Close();
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            });
            Call("machine_add", Args(("gear", "Neo Poly"), ("name", "Exported")));
            Call("pattern_create", Args(("machine", "Exported"), ("name", "P"), ("length_beats", 4)));
            Call("notes_write", Args(("machine", "Exported"), ("pattern", "P"), ("notes", new JsonArray(new JsonObject { ["pitch"] = 60, ["start"] = 0.0, ["length"] = 1.0, ["velocity"] = 100 }))));
            Call("arrangement_place", Args(("machine", "Exported"), ("pattern", "P"), ("at", 0)));
            Settle(window);
            string path = Path.Combine(folder, "song.wav");
            var r = Call("export_audio", Args(("path", path), ("from", 0), ("to", 4), ("stems", "generators"), ("bits", 16), ("tail_seconds", 0.5)));
            var files = r["files"]!.AsArray().Select(f => f["path"]!.GetValue<string>()).ToList();
            Assert.That(files.Select(Path.GetFileName), Is.EqualTo(new[] { "song.wav", "song-Exported.wav" }));
            Assert.That(files.All(File.Exists), Is.True);
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(44 + 4 * 1000), "audio in it");
            Assert.That(r["bits"]!.GetValue<int>(), Is.EqualTo(16));
            Assert.That(r["measure"]!["silent"]!.GetValue<bool>(), Is.False, "the note is in the export");
            long size = new FileInfo(path).Length;
            var again = Try("export_audio", Args(("path", path), ("from", 0), ("to", 4)));
            Assert.That(again.Error, Is.True);
            Assert.That(again.Text, Does.Contain("overwrite=true"));
            Assert.That(new FileInfo(path).Length, Is.EqualTo(size));
            Assert.That(Try("export_audio", Args(("path", path), ("from", 0), ("to", 4), ("bits", 24), ("overwrite", true))).Error, Is.False);
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(size), "24 bit is bigger");
        }

        [AvaloniaTest]
        public void TheAppAndTheAudioOutputReportThemselves()
        {
            var window = NewSong();
            using var restore = Studio.AfterTest(() =>
            {
                Dialogs.Responder = null;
                window.Close();
            });
            var app = Call("app_status");
            Assert.That(app["mode"]!.GetValue<string>(), Is.EqualTo("studio"));
            Assert.That(app["tools"]!.GetValue<int>(), Is.GreaterThan(80));
            Assert.That(app["memory"]!["working_set_mb"]!.GetValue<double>(), Is.GreaterThan(10));
            var recent = Call("song_recent");
            Assert.That(recent["songs"], Is.Not.Null);

            var audio = Call("audio_status");
            Assert.That(audio["open"]!.GetValue<bool>(), Is.False, "the tests run without an audio device");
            Assert.That(Call("audio_devices")["devices"], Is.Not.Null);
            var failed = Try("audio_set", Args(("device", "PortAudio: no such device")));
            Assert.That(failed.Error, Is.True);
            Assert.That(failed.Text, Does.Contain("No output device"));
        }
    }
}
