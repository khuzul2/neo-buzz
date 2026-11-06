// Fork (docs/specs/dye-essence.md §5.2): Dye Essence's G0 stress test in the studio, headless. A
// machine with 704 parameters and megabytes of state (Dye Stress, TestMachines/DyeStress, its table
// generated as Dye Essence's will be) through the parameter window, the pattern editor, a pattern
// playing every parameter, .bmx and .bmxml, MIDI learn on every parameter, a preset with its data,
// the manifest and machine_describe. Each test prints its figures ("Dye Stress: …") to the log.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Agent.Mcp;
using ReBuzz.Studio.Parameters;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class DyeStressTests
    {
        const string Gear = "Dye Stress";
        const int Globals = 704;
        const int Frames = 3 * 256;   // three tables of 256 frames of 2048 samples
        const int TB = PatternEvent.TimeBase;

        static ReBuzzCore Core => Studio.Host.Core;

        static void Report(string what) => TestContext.Out.WriteLine("Dye Stress: " + what);

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        static void NewSong()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.ExecuteCommand(BuzzCommand.Stop);
            Core.Modified = false;
            SongFiles.New().Wait();
            Pump();
        }

        static MachineCore Add(string name)
        {
            var sw = Stopwatch.StartNew();
            Core.SongCore.CreateMachine(Gear, null, name, null, null, null, -1, 0.3f, 0.3f);
            var machine = Core.SongCore.MachinesList.FirstOrDefault(m => m.Name == name);
            Assert.That(machine, Is.Not.Null, "Dye Stress loads (TestMachines/DyeStress)");
            Report($"machine_add equivalent: created in {sw.ElapsedMilliseconds} ms");
            return machine!;
        }

        static IReadOnlyList<IParameter> GlobalsOf(IMachine m) => m.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters;

        /// <summary>A value for global parameter i away from its default, spread over its range.</summary>
        static int ValueFor(IParameter p, int i, int salt = 0)
        {
            int span = p.MaxValue - p.MinValue + 1;
            int v = p.MinValue + (int)(((long)i * 7919 + 13 + salt * 104729) % span);
            if (v == p.DefValue) v = v == p.MaxValue ? p.MinValue : v + 1;
            return v;
        }

        static void SetEveryGlobal(MachineCore machine, int salt = 0)
        {
            var globals = GlobalsOf(machine);
            for (int i = 0; i < globals.Count; i++) globals[i].SetValue(0, ValueFor(globals[i], i, salt));
        }

        /// <summary>The machine's own raw values (DyeStressMachine.Raw), read through reflection.</summary>
        static int[] Raw(MachineCore machine) => (int[])machine.ManagedMachine.GetType().GetMethod("Raw")!.Invoke(machine.ManagedMachine, null)!;

        static object State(MachineCore machine) => machine.ManagedMachine.GetType().GetProperty("MachineState")!.GetValue(machine.ManagedMachine)!;

        static string StateText(MachineCore machine, string property) => (string)State(machine).GetType().GetProperty(property)!.GetValue(State(machine))!;

        /// <summary>Three 256-frame tables (as Neo Wave keeps a user table: floats, base64) and 64 routes: the large state.</summary>
        static (string Tables, string Routes) SetLargeState(MachineCore machine)
        {
            var samples = new float[Frames * 2048];
            for (int f = 0; f < Frames; f++)
                for (int i = 0; i < 2048; i++)
                    samples[f * 2048 + i] = (float)Math.Sin(2 * Math.PI * i / 2048 * (1 + f % 7)) * (1f - f / (float)Frames);
            var bytes = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            string tables = Convert.ToBase64String(bytes);
            string routes = string.Join("\n", Enumerable.Range(1, 64).Select(r => $"{r}: LFO {1 + r % 10} -> S{1 + r % 3} position, aux Macro {1 + r % 8}, curve {r % 5 * 0.2:F1}, {(r % 4 == 0 ? "audio" : "control")}"));
            var inst = machine.ManagedMachine;
            var prop = inst.GetType().GetProperty("MachineState")!;
            var state = Activator.CreateInstance(prop.PropertyType)!;
            state.GetType().GetProperty("Frames")!.SetValue(state, Frames);
            state.GetType().GetProperty("Tables")!.SetValue(state, tables);
            state.GetType().GetProperty("Routes")!.SetValue(state, routes);
            prop.SetValue(inst, state);
            return (tables, routes);
        }

        static string Folder(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"rebuzz-dye-stress-{Environment.ProcessId}", name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ------------------------------------------------------------------ loading, the parameter window

        [AvaloniaTest]
        public void ItLoadsAndTheParameterWindowShowsEveryParameter()
        {
            NewSong();
            var machine = Add("Stress Params");
            try
            {
                var globals = GlobalsOf(machine);
                Assert.That(globals.Count, Is.EqualTo(Globals), "the generated table's 704 parameters");
                Assert.That(machine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Track).Parameters.Select(p => p.Name), Is.EqualTo(new[] { "Note", "Velocity" }));
                string dll = machine.DLL.Path;
                string manifest = Path.Combine(Path.GetDirectoryName(dll)!, Gear + ".machine.json");
                Assert.That(File.Exists(manifest), Is.True, "the manifest beside the DLL");
                Report($"{globals.Count} global parameters; the manifest {new FileInfo(manifest).Length / 1024.0:F0} KB");

                var sw = Stopwatch.StartNew();
                Core.UiHost.OpenParameterWindow(machine, null);
                var window = StudioMachineUiHost.ParameterWindowOf(machine);
                Assert.That(window, Is.Not.Null);
                var view = (ParameterWindowView)StudioMachineUiHost.ContentOf(window!)!;   // inside the machine keyboard's frame (a generator that takes notes)
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                double openMs = sw.Elapsed.TotalMilliseconds;
                Assert.That(view.Rows.Count, Is.EqualTo(Globals), "a row per state parameter");
                var frames = new List<double>();
                for (int i = 0; i < 5; i++)
                {
                    sw.Restart();
                    window.InvalidateVisual();
                    window.CaptureRenderedFrame();
                    frames.Add(sw.Elapsed.TotalMilliseconds);
                }
                frames.Sort();
                // The last row, scrolled to and moved as a user would.
                var last = view.Rows.Single(r => r.ParameterName == "Arp scale");
                sw.Restart();
                last.Slider.BringIntoView();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                double scrollMs = sw.Elapsed.TotalMilliseconds;
                last.Slider.Commit(5);
                Dispatcher.UIThread.RunJobs();
                Assert.That(globals.Single(p => p.Name == "Arp scale").GetValue(0), Is.EqualTo(5), "the last parameter's slider moves it");
                Report($"parameter window: open and laid out in {openMs:F0} ms, a frame {frames[2]:F1} ms (median of 5), to the last row {scrollMs:F0} ms");
                Studio.Screenshot(window, "dye-stress-parameters");
                // Reported against the target of a second (dye-essence.md §5.2); asserted only as a bound
                // (shared runners vary): the rows are built eagerly, 704 of them.
                Assert.That(openMs, Is.LessThan(15000), "the window opens in seconds, not minutes");
            }
            finally
            {
                Core.UiHost.ReleaseMachine(machine);
                Core.SongCore.DeleteMachines(new[] { machine });
                Pump();
            }
        }

        // ------------------------------------------------------------------ the pattern editor

        [AvaloniaTest]
        public void ThePatternEditorShowsEveryColumnAndTypesIntoTheLast()
        {
            NewSong();
            var machine = Add("Stress PE");
            machine.CreatePattern("00", 16);
            var pattern = machine.Patterns.First(p => p.Name == "00");
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            try
            {
                window.Show();
                Pump();
                var sw = Stopwatch.StartNew();
                PatternView.Edit(pattern);
                Pump();
                double openMs = sw.Elapsed.TotalMilliseconds;
                var view = PatternView.Page?.EditorControl;
                Assert.That(view, Is.Not.Null, "the Modern Pattern Editor's view is in the tab");
                dynamic grid = ((dynamic)view!).TrackerGrid;
                var body = (Control)grid.Body;
                body.Focus();
                Pump();
                double Draw()
                {
                    var times = new List<double>();
                    for (int i = 0; i < 7; i++)
                    {
                        body.InvalidateVisual();
                        window.CaptureRenderedFrame();
                        times.Add(((TimeSpan)grid.LastRenderTime).TotalMilliseconds);
                    }
                    times.Sort();
                    return times[3];
                }
                double first = Draw();
                // End: the globals' last column (Arp scale); a digit there is its automation.
                var top = TopLevel.GetTopLevel(body)!;
                top.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
                top.KeyReleaseQwerty(PhysicalKey.End, RawInputModifiers.None);
                Pump();
                top.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.None);
                top.KeyReleaseQwerty(PhysicalKey.Digit3, RawInputModifiers.None);
                Pump();
                var column = pattern.PatternEditorMachineEvents.FirstOrDefault(c => c.Parameter.Name == "Arp scale");
                Assert.That(column, Is.Not.Null, "the last global parameter has a column");
                Assert.That(column!.Events.Count(), Is.EqualTo(1), "a digit typed at its end is its automation");
                double last = Draw();
                Report($"pattern editor: opened in {openMs:F0} ms; the grid draws in {first:F1} ms at the first columns, {last:F1} ms at the last (median of 7); a value typed into column {Globals} ('Arp scale')");
                Studio.Screenshot(window, "dye-stress-pattern-editor");
                Assert.That(Math.Max(first, last), Is.LessThan(250), "a frame of the grid draws in a quarter of a second at most");
            }
            finally
            {
                Core.Modified = false;
                window.Close();
                Pump();
                Core.SongCore.DeleteMachines(new[] { machine });
                Pump();
            }
        }

        // ------------------------------------------------------------------ a pattern playing every parameter

        /// <summary>An engine automation column for every global parameter: one value from tick 0, another from tick 8.</summary>
        static void AutomateEveryGlobal(MachineCore machine, IPattern pattern)
        {
            var globals = GlobalsOf(machine);
            for (int i = 0; i < globals.Count; i++)
            {
                var column = SessionLayout.ColumnFor(pattern, new ColumnKey(1, i, 0), create: true);
                column.SetEvents(new[] { new PatternEvent(0, ValueFor(globals[i], i, 1), 0), new PatternEvent(8 * TB, ValueFor(globals[i], i, 2), 0) }, true);
            }
        }

        /// <summary>
        /// Renders the song (the agent's offline render: a WorkManager on this thread, under the audio
        /// lock, as PatternEditorTests renders) from tick 0, or on from where it is, until it reaches a
        /// tick: the seconds it took.
        /// </summary>
        static double RenderUntil(int untilTick, bool fromStart)
        {
            var song = Core.SongCore;
            var buffer = new float[512];
            var sw = Stopwatch.StartNew();
            lock (ReBuzzCore.AudioLock)
            {
                var wm = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
                if (fromStart)
                {
                    Core.Playing = false;
                    Core.Looping = false;
                    song.LoopStart = 0;
                    song.LoopEnd = 64;
                    song.SongEnd = 64;
                    song.PlayPosition = 0;
                    ReBuzzCore.masterInfo.PosInTick = 0;
                    var sub = ReBuzzCore.subTickInfo;
                    sub.PosInSubTick = 0;
                    sub.CurrentSubTick = 0;
                    sub.SubTickReminderCounter = 0;
                    Core.Playing = true;
                    var info = ReBuzzCore.masterInfo;
                    for (int guard = 0; !Core.Playing && guard < 100000; guard++)
                    {
                        int pos = info.PosInTick;
                        int n = pos == 0 ? 1 : Math.Clamp(info.SamplesPerTick - pos, 1, 256);
                        wm.MainAudioFillBuffer(buffer, 0, n * 2);
                    }
                }
                Assert.That(Core.Playing, Is.True, "playing");
                for (int blocks = 0; blocks < 40000 && Core.Playing && song.PlayPosition < untilTick; blocks++)
                    wm.MainAudioFillBuffer(buffer, 0, 512);
            }
            return sw.Elapsed.TotalSeconds;
        }

        /// <summary>A few ticks of audio with the transport stopped: parameter changes reach the machine at a tick.</summary>
        static void RenderStopped(int ticks = 4)
        {
            var buffer = new float[512];
            lock (ReBuzzCore.AudioLock)
            {
                Core.Playing = false;
                var wm = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
                int blocks = ticks * ReBuzzCore.masterInfo.SamplesPerTick / 256 + 2;
                for (int b = 0; b < blocks; b++) wm.MainAudioFillBuffer(buffer, 0, 512);
            }
        }

        [AvaloniaTest]
        public void APatternPlaysEveryParameter()
        {
            NewSong();
            var machine = Add("Stress Play");
            var song = Core.SongCore;
            ISequence seq = null;
            try
            {
                var master = song.Machines.First(m => m.DLL.Info.Type == MachineType.Master);
                song.ConnectMachines(machine, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                machine.CreatePattern("P", 16);
                var pattern = machine.Patterns.First(p => p.Name == "P");
                var sw = Stopwatch.StartNew();
                AutomateEveryGlobal(machine, pattern);
                double writeMs = sw.Elapsed.TotalMilliseconds;
                song.AddSequence(machine, song.Sequences.Count);
                seq = song.Sequences.Last();
                seq.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, pattern));

                var globals = GlobalsOf(machine);
                RenderUntil(4, fromStart: true);
                Assert.That(Raw(machine), Is.EqualTo(Enumerable.Range(0, Globals).Select(i => ValueFor(globals[i], i, 1)).ToArray()), "tick 0's values reached the machine, all 704");
                RenderUntil(12, fromStart: false);
                Assert.That(Raw(machine), Is.EqualTo(Enumerable.Range(0, Globals).Select(i => ValueFor(globals[i], i, 2)).ToArray()), "tick 8's values too");

                // The cost: a song of 64 ticks with every parameter changing every tick, against the same
                // pattern with no events (its columns still there); after a warm-up, each rendered four
                // times in turn, the fastest kept (a shared runner is noisy).
                var every = new PatternEvent[globals.Count][];
                for (int i = 0; i < globals.Count; i++)
                    every[i] = Enumerable.Range(0, 16).Select(t => new PatternEvent(t * TB, ValueFor(globals[i], i, t), 0)).ToArray();
                double automated = double.MaxValue, plain = double.MaxValue;
                RenderUntil(64, fromStart: true);   // warm up
                for (int round = 0; round < 4; round++)
                {
                    for (int i = 0; i < globals.Count; i++)
                        SessionLayout.ColumnFor(pattern, new ColumnKey(1, i, 0), create: true).SetEvents(every[i], true);
                    automated = Math.Min(automated, RenderUntil(64, fromStart: true));
                    foreach (var c in pattern.Columns.ToList()) c.SetEvents(c.GetEvents(0, int.MaxValue).ToArray(), false);
                    plain = Math.Min(plain, RenderUntil(64, fromStart: true));
                }
                double tickMs = (automated - plain) * 1000 / 64;
                Report($"a pattern of {Globals} automation columns written in {writeMs:F0} ms; rendering 64 ticks with every parameter changing every tick {automated * 1000:F0} ms against {plain * 1000:F0} ms without (best of 4, after a warm-up): {tickMs:F2} ms a tick for {Globals} changes, {tickMs * 1000 / Globals:F1} µs a change");
                // A bound, not the target (dye-essence.md §5.2: well under an audio buffer, 5.3 ms at 256
                // samples): every parameter changing on every tick is the worst case, and runners vary.
                Assert.That(tickMs, Is.LessThan(15), "704 changes in a tick stay within a few buffers");
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                if (seq != null) song.RemoveSequence(seq);
                song.DeleteMachines(new[] { machine });
                Pump();
            }
        }

        // ------------------------------------------------------------------ MIDI learn

        [AvaloniaTest]
        public void MidiLearnBindsEveryParameter()
        {
            NewSong();
            var machine = Add("Stress MIDI");
            bool soft = Global.MIDISettings.ParameterSoftTakeover;
            try
            {
                Global.MIDISettings.ParameterSoftTakeover = false;
                var globals = GlobalsOf(machine);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < globals.Count; i++) globals[i].BindToMIDIController(0, i / 128, i % 128);
                double bindMs = sw.Elapsed.TotalMilliseconds;
                for (int i = 0; i < globals.Count; i++) Assert.That(globals[i].GetMIDIBinding(0), Is.EqualTo((i / 128, i % 128)), globals[i].Name);
                sw.Restart();
                for (int i = 0; i < globals.Count; i++) Core.MidiControllerAssignments.SendMidi(MIDI.Encode(MIDI.ControlChange | (i / 128), i % 128, 127));
                double sendUs = sw.Elapsed.TotalMilliseconds * 1000 / globals.Count;
                for (int i = 0; i < globals.Count; i++) Assert.That(globals[i].GetValue(0), Is.EqualTo(globals[i].MaxValue), $"{globals[i].Name}: CC {i % 128} on channel {i / 128 + 1} at 127");
                Report($"MIDI learn: {Globals} parameters bound in {bindMs:F0} ms; a controller message reaches its parameter in {sendUs:F1} µs among {Globals} bindings");
            }
            finally
            {
                Global.MIDISettings.ParameterSoftTakeover = soft;
                Core.MidiControllerAssignments.UnbindAllMIDIControllers(machine);
                Core.SongCore.DeleteMachines(new[] { machine });
                Pump();
            }
        }

        // ------------------------------------------------------------------ .bmx and .bmxml

        [AvaloniaTest]
        public void ASongWithItAllSavesAndOpens([Values(".bmx", ".bmxml")] string extension)
        {
            NewSong();
            var machine = Add("Stress Song");
            bool soft = Global.MIDISettings.ParameterSoftTakeover;
            try
            {
                SetEveryGlobal(machine);
                var (tables, routes) = SetLargeState(machine);
                machine.CreatePattern("P", 16);
                AutomateEveryGlobal(machine, machine.Patterns.First(p => p.Name == "P"));
                var globals = GlobalsOf(machine);
                for (int i = 0; i < globals.Count; i++) globals[i].BindToMIDIController(0, i / 128, i % 128);
                var expected = globals.Select(p => p.GetValue(0)).ToArray();

                string path = Path.Combine(Folder("song"), "stress" + extension);
                var sw = Stopwatch.StartNew();
                Core.SaveSongCopy(path);
                double saveMs = sw.Elapsed.TotalMilliseconds;
                long bytes = new FileInfo(path).Length;
                Core.Modified = false;
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                sw.Restart();
                Core.OpenSongFile(path);
                Pump();
                double openMs = sw.Elapsed.TotalMilliseconds;

                var opened = Core.SongCore.MachinesList.Single(m => m.Name == "Stress Song");
                Assert.That(opened, Is.Not.SameAs(machine), "the song was opened");
                machine = opened;
                var back = GlobalsOf(opened);
                Assert.That(back.Select(p => p.GetValue(0)).ToArray(), Is.EqualTo(expected), "every parameter's value");
                RenderStopped();
                Assert.That(Raw(opened), Is.EqualTo(expected), "the machine has them too");
                Assert.That(StateText(opened, "Tables"), Is.EqualTo(tables), "the tables, every byte");
                Assert.That(StateText(opened, "Routes"), Is.EqualTo(routes), "the routes");
                var p2 = opened.Patterns.First(p => p.Name == "P");
                for (int i = 0; i < back.Count; i += 37)
                {
                    var c = SessionLayout.ColumnFor(p2, new ColumnKey(1, i, 0), create: false);
                    Assert.That(c, Is.Not.Null, $"{back[i].Name}: its automation column");
                    Assert.That(c!.GetEvents(0, int.MaxValue).Select(e => e.Value), Is.EqualTo(new[] { ValueFor(back[i], i, 1), ValueFor(back[i], i, 2) }), back[i].Name);
                }
                for (int i = 0; i < back.Count; i++) Assert.That(back[i].GetMIDIBinding(0), Is.EqualTo((i / 128, i % 128)), $"{back[i].Name}: its MIDI binding");
                Report($"{extension}: {bytes / 1e6:F1} MB (state {tables.Length / 1e6:F1} MB of base64 tables), saved in {saveMs:F0} ms, opened in {openMs:F0} ms; values, state, {Globals} automation columns and {Globals} MIDI bindings back");
                Assert.That(openMs, Is.LessThan(30000), "it opens in seconds");
            }
            finally
            {
                Global.MIDISettings.ParameterSoftTakeover = soft;
                Core.MidiControllerAssignments.UnbindAllMIDIControllers(machine);
                NewSong();
            }
        }

        // ------------------------------------------------------------------ presets

        [AvaloniaTest]
        public void APresetCarriesItsValuesAndItsData()
        {
            NewSong();
            var machine = Add("Stress Preset");
            try
            {
                SetEveryGlobal(machine);
                var (tables, routes) = SetLargeState(machine);
                var expected = GlobalsOf(machine).Select(p => p.GetValue(0)).ToArray();
                var sw = Stopwatch.StartNew();
                var preset = new Preset(machine, false, true);   // the editor kit's Save: values and the machine's data
                string xml = preset.ToXml();
                double saveMs = sw.Elapsed.TotalMilliseconds;
                Assert.That(preset.Data, Is.Not.Null.And.Length.GreaterThan(tables.Length), "the preset holds the machine's data");

                foreach (var p in GlobalsOf(machine)) p.SetValue(0, p.DefValue);
                machine.Data = null;
                sw.Restart();
                var loaded = Preset.FromXml(xml);
                Assert.That(loaded, Is.Not.Null);
                loaded!.Apply(machine, true);
                double loadMs = sw.Elapsed.TotalMilliseconds;
                Assert.That(GlobalsOf(machine).Select(p => p.GetValue(0)).ToArray(), Is.EqualTo(expected), "every value");
                Assert.That(StateText(machine, "Tables"), Is.EqualTo(tables), "the tables");
                Assert.That(StateText(machine, "Routes"), Is.EqualTo(routes), "the routes");
                Report($"preset with its data: {xml.Length / 1e6:F1} MB of XML ({Globals} values, {preset.Data!.Length / 1e6:F1} MB of state), made in {saveMs:F0} ms, read and applied in {loadMs:F0} ms");
            }
            finally
            {
                Core.SongCore.DeleteMachines(new[] { machine });
                Pump();
            }
        }

        // ------------------------------------------------------------------ the agent

        static JsonObject Call(string tool, JsonObject args)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            var r = task.GetAwaiter().GetResult()!;
            Assert.That(r["error"], Is.Null, r.ToJsonString());
            var result = r["result"]!.AsObject();
            Assert.That(result["isError"]?.GetValue<bool>() == true, Is.False, result.ToJsonString());
            return result;
        }

        [AvaloniaTest]
        public void AgentsDescribeAndSetItInUnits()
        {
            NewSong();
            var machine = Add("Stress Agent");
            try
            {
                var sw = Stopwatch.StartNew();
                var all = Call("machine_describe", new JsonObject { ["machine"] = "Stress Agent" });
                double allMs = sw.Elapsed.TotalMilliseconds;
                int allChars = all.ToJsonString().Length;
                var slot = Call("machine_describe", new JsonObject { ["machine"] = "Stress Agent", ["group"] = "Slot 1" });
                int slotChars = slot.ToJsonString().Length;
                var data = slot["structuredContent"]!.AsObject();
                Assert.That(data["exact_units"]!.GetValue<bool>(), Is.True, "units from the generated manifest");
                Call("param_set", new JsonObject
                {
                    ["machine"] = "Stress Agent",
                    ["changes"] = new JsonArray(
                        new JsonObject { ["param"] = "F2 cutoff", ["value"] = "800 Hz" },
                        new JsonObject { ["param"] = "Arp scale", ["value"] = "Dorian" },
                        new JsonObject { ["param"] = "Route 64 amount", ["value"] = "-25 %" }),
                });
                var globals = GlobalsOf(machine);
                Assert.That(globals.Single(p => p.Name == "Arp scale").GetValue(0), Is.EqualTo(3), "an enum near the end, by its name");
                Assert.That(globals.Single(p => p.Name == "Route 64 amount").GetValue(0), Is.EqualTo(375), "a bipolar amount in %");
                Report($"machine_describe: all {Globals} parameters {allChars / 1024.0:F0} KB of JSON in {allMs:F0} ms; one group (Slot 1) {slotChars / 1024.0:F0} KB; param_set in units on parameters 600–704");
            }
            finally
            {
                Core.SongCore.DeleteMachines(new[] { machine });
                Pump();
            }
        }
    }
}
