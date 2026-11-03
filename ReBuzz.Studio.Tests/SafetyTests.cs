// Fork (docs/specs/machine-safety.md §5): the guard on every machine and the safe master, headless.
// A test machine ("Guard Test", TestMachines/GuardTest.cs) diverges on purpose: a feedback loop at
// gain 1.01, and NaN after a few blocks. The guard trips, silences, stops and mutes it, the rest of
// the song plays on, the machine view draws it red with a tip, the status line and the agent say so,
// and Reset and unmute brings it back. Normal audio passes bit for bit, and the checks allocate
// nothing. A +12 dBFS sine reaches the device (CommonAudioProvider.Read) at no more than the ceiling,
// the clip indicator latches +12 dB (transport meter, Master, agent), and a render still measures
// +12 dBFS. Preferences ▸ Audio asks before the limiter or the guard goes off.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Audio;
using ReBuzz.Audio.Engine;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Preferences;
using ReBuzz.Studio.Views.Machines;
using BuzzCommand = BuzzGUI.Interfaces.BuzzCommand;

namespace ReBuzz.Studio.Tests
{
    public class SafetyTests
    {
        const string Gear = "Guard Test";
        const int Sine = 0, Runaway = 1, NaN = 2;

        static StudioHost Host => Studio.Host;
        static ReBuzzCore Core => Host.Core;

        static void Settle() => MachineViewTests.Settle();

        double masterVolume;

        [SetUp]
        public void Fresh()
        {
            masterVolume = Core.MasterVolume;
            Assert.That(Core.MachineDLLs.ContainsKey(Gear), Is.True, "the guard's test machine is in the tests' gear (TestMachines/GuardTest.csproj)");
            MachineGuard.Enabled = true;
            Core.SafeMaster.LimiterOn = true;
            Core.SafeMaster.CeilingDb = 0;
            Core.SafeMaster.ClipThresholdDb = 0;
            Core.SafeMaster.Reset();
        }

        [TearDown]
        public void PutBack()
        {
            Core.MasterVolume = masterVolume;
            Dialogs.Responder = null;
            MachineGuard.Enabled = true;
            Core.SafeMaster.LimiterOn = true;
            Core.SafeMaster.CeilingDb = 0;
            Core.SafeMaster.ClipThresholdDb = 0;
            Core.SafeMaster.Reset();
            PreferencesWindow.Current?.Close();
            Settle();
        }

        static MachineCore Master => Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);

        /// <summary>A Guard Test into the Master: a sine at <paramref name="dbfs"/>, or misbehaving.</summary>
        static MachineCore Add(string name, int mode, int dbfs = -12, int blocks = 10, float x = 0.3f, float y = -0.3f)
        {
            Core.MasterVolume = 1;      // levels as the machine makes them (other tests leave it lower)
            var song = Core.SongCore;
            song.CreateMachine(Gear, null, name, null, null, null, -1, x, y);
            var m = song.MachinesList.Last(m => m.Name == name);
            song.ConnectMachines(m, Master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            Set(m, "Mode", mode);
            Set(m, "Level", dbfs + 60);
            Set(m, "Blocks", blocks);
            return m;
        }

        static void Set(IMachine m, string name, int value) =>
            m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == name).SetValue(0, value);

        static void NewSong()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.ExecuteCommand(BuzzCommand.Stop);
            Core.Modified = false;
            Core.ExecuteCommand(BuzzCommand.NewFile);
            Settle();
        }

        /// <summary>
        /// Renders <paramref name="blocks"/> blocks of 256 frames offline, as render does (the true
        /// signal: no safe master), from a tick's start so parameter changes land first; the output
        /// at device scale (1.0 = 0 dBFS), interleaved.
        /// </summary>
        static float[] Render(int blocks)
        {
            var wm = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            var buffer = new float[512];
            var all = new List<float>();
            lock (ReBuzzCore.AudioLock)
            {
                ReBuzzCore.masterInfo.PosInTick = 0;
                for (int i = 0; i < blocks; i++)
                {
                    wm.MainAudioFillBuffer(buffer, 0, 512);
                    all.AddRange(buffer);
                }
            }
            return all.ToArray();
        }

        static float Peak(IEnumerable<float> samples) => samples.Max(MathF.Abs);

        static double Db(double peak) => 20 * Math.Log10(peak);

        /// <summary>Buzz's Stop on every machine (their state starts over), for renders that compare.</summary>
        static void StopAll()
        {
            foreach (var m in Core.SongCore.MachinesList) Core.MachineManager.Stop(m);
        }

        // ------------------------------------------------------------------ the guard

        [AvaloniaTest]
        public void AMachineThatRunsAwayIsSilencedStoppedAndMutedAndTheRestPlaysOn()
        {
            var (window, view) = MachineViewTests.OpenEmpty();
            var notices = new List<string>();
            Action<MachineCore> notice = m => notices.Add(m.Name);
            Core.MachineDiverged += notice;
            try
            {
                var steady = Add("Steady", Sine, dbfs: -12, x: -0.4f);
                var runaway = Add("Runaway", Runaway, x: 0.4f);
                Settle();

                // A gain of 1.01 a sample passes +60 dBFS in about 1,700 samples: the seventh block or so.
                var audio = Render(40);
                Assert.That(runaway.IsDiverged, Is.True, "the guard tripped on the feedback loop");
                Assert.That(steady.IsDiverged, Is.False);
                Assert.That(runaway.DivergedReason, Does.EndWith(" dBFS"));
                Assert.That(double.Parse(runaway.DivergedReason.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture), Is.GreaterThan(60));
                Assert.That(audio.All(float.IsFinite), Is.True);
                Assert.That(Db(Peak(audio)), Is.LessThan(60.1), "nothing above +60 dBFS reached the master (the blocks before the trip were under it)");
                var after = audio.Skip(audio.Length / 2).ToArray();
                Assert.That(Db(Peak(after)), Is.EqualTo(-12).Within(0.1), "the rest of the song plays on: the steady sine alone");

                Settle();       // the UI thread's part: Stop, the views, the notice
                Assert.That(runaway.IsMuted, Is.True, "a diverged machine reads as muted until it is reset");
                Assert.That(notices, Is.EqualTo(new[] { "Runaway" }));
                window.RefreshNow();
                Assert.That(window.StatusText, Does.Contain("Diverged: muted: Runaway (+"));
                window.MouseMove(view.PointOf(runaway, window));
                Settle();
                Assert.That(view.Canvas.HoverTip, Does.StartWith("Diverged: muted."));
                Studio.Screenshot(window, "safety-diverged");

                // Reset and unmute, from its menu: it plays again (Stop started its loop over), until it trips again.
                MachineViewTests.Click(window, view.PointOf(runaway, window), MouseButton.Right);
                var menu = view.Canvas.OpenMenu;
                Assert.That(menu?.IsOpen, Is.True);
                var reset = menu!.Items.OfType<MenuItem>().First();
                Assert.That(reset.Header, Is.EqualTo("Reset and unmute"), "first on a diverged machine's menu");
                Studio.Screenshot(TopLevel.GetTopLevel(reset)!, "safety-diverged-menu");
                MachineViewTests.ClickItem(reset);
                Assert.That(runaway.IsDiverged, Is.False);
                Assert.That(runaway.IsMuted, Is.False);
                var again = Render(2);
                Assert.That(Peak(again), Is.GreaterThan(1.0f), "the runaway plays again (from full scale, growing)");
                window.RefreshNow();
                Assert.That(window.StatusText, Does.Not.Contain("Diverged"));

                // One undo step: undone, it is muted (not diverged).
                Core.SongCore.ActionStack.Undo();
                Assert.That(runaway.IsMuted, Is.True);
                Assert.That(runaway.IsDiverged, Is.False);
            }
            finally
            {
                Core.MachineDiverged -= notice;
                MachineViewTests.Close(window);
            }
        }

        [AvaloniaTest]
        public void AMachineWritingNaNIsCaughtAndTheAgentSeesAndResetsIt()
        {
            NewSong();
            var steady = Add("Steady", Sine, dbfs: -12, x: -0.4f);
            // Blocks counts Work calls, which sub-ticks can make shorter than 256 frames.
            var broken = Add("Broken", NaN, blocks: 200, x: 0.4f);
            var audio = Render(400);
            Assert.That(broken.IsDiverged, Is.True);
            Assert.That(broken.DivergedReason, Is.EqualTo("NaN"));
            Assert.That(audio.All(float.IsFinite), Is.True, "no NaN reached the master");
            Assert.That(Db(Peak(audio.Skip(audio.Length / 2))), Is.EqualTo(-12).Within(0.1), "the steady sine plays on");
            Settle();

            var status = Call("studio_status");
            var safety = status["safety"]!;
            Assert.That(safety["guard"]!.GetValue<bool>(), Is.True);
            var diverged = safety["diverged"]!.AsArray();
            Assert.That(diverged.Count, Is.EqualTo(1));
            Assert.That(diverged[0]!["machine"]!.GetValue<string>(), Is.EqualTo("Broken"));
            Assert.That(diverged[0]!["reason"]!.GetValue<string>(), Is.EqualTo("NaN"));
            Assert.That(DateTime.Parse(diverged[0]!["at"]!.GetValue<string>()).ToUniversalTime(), Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(5)));
            Assert.That(safety["limiter"]!["on"]!.GetValue<bool>(), Is.True);

            var described = Call("machine_describe", new JsonObject { ["machine"] = "Broken" })["machine"]!;
            Assert.That(described["diverged"]!.GetValue<bool>(), Is.True);
            Assert.That(described["diverged_reason"]!.GetValue<string>(), Is.EqualTo("NaN"));
            Assert.That(described["muted"]!.GetValue<bool>(), Is.True);

            var set = Call("machine_set", new JsonObject { ["machine"] = "Broken", ["reset"] = true })["machine"]!;
            Assert.That(set["diverged"], Is.Null);
            Assert.That(set["muted"]!.GetValue<bool>(), Is.False);
            Assert.That(broken.IsDiverged, Is.False);
            // Stop started it over: its sine plays until the NaN comes back (both from phase 0 here: +6 dB).
            StopAll();
            var back = Render(2);
            Assert.That(Db(Peak(back)), Is.EqualTo(-6).Within(0.1), "both sines play");
            Assert.That(broken.IsDiverged, Is.False);
            Assert.That(Call("studio_status")["safety"]!["diverged"]!.AsArray().Count, Is.EqualTo(0));

            // A render that trips it says so (the profiler reads it), and leaves it muted.
            var rendered = Call("render", new JsonObject { ["from"] = 0, ["to"] = 4, ["tail_seconds"] = 0 });
            var tripped = rendered["diverged"]!.AsArray();
            Assert.That(tripped.Count, Is.EqualTo(1));
            Assert.That(tripped[0]!["machine"]!.GetValue<string>(), Is.EqualTo("Broken"));
            Assert.That(tripped[0]!["reason"]!.GetValue<string>(), Is.EqualTo("NaN"));
            Assert.That(rendered["warnings"]!.AsArray().Select(w => w!.GetValue<string>()), Has.Some.StartsWith("Broken diverged (NaN)"));
            Assert.That(rendered["measure"]!["silent"]!.GetValue<bool>(), Is.False, "the steady sine is in the render");
            Settle();
            Assert.That(broken.IsDiverged, Is.True);
            Assert.That(broken.IsMuted, Is.True);
        }

        [AvaloniaTest]
        public void NormalAudioPassesTheGuardBitForBit()
        {
            NewSong();
            Add("Hot", Sine, dbfs: 12, x: -0.4f);
            Add("Quiet", Sine, dbfs: -40, x: 0.4f);
            StopAll();
            var guarded = Render(30);
            MachineGuard.Enabled = false;
            StopAll();
            var unguarded = Render(30);
            MachineGuard.Enabled = true;
            Assert.That(guarded.Any(x => x != 0), Is.True);
            Assert.That(guarded, Is.EqualTo(unguarded), "a block that passes the guard is never touched");
        }

        [AvaloniaTest]
        public void TheGuardAndTheSafeMasterAllocateNothing()
        {
            NewSong();
            var m = Add("Steady", Sine);
            var block = new Sample[256];
            for (int i = 0; i < block.Length; i++) block[i] = new Sample(30000f * MathF.Sin(i * 0.1f), -20000f);
            var device = new float[512];
            var safe = new SafeMaster();
            for (int i = 0; i < 10; i++) { MachineGuard.Check(m, block, 256); safe.Process(device, 2, 48000); }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
                MachineGuard.Check(m, block, 256);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0), "the guard's check");

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                for (int k = 0; k < device.Length; k++) device[k] = 4f * MathF.Sin((i * 256 + k / 2) * 0.05f);     // +12 dBFS: limiting
                safe.Process(device, 2, 48000);
            }
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0), "the safe master, limiting");
            Assert.That(safe.LimitedFrames, Is.GreaterThan(0));
        }

        // ------------------------------------------------------------------ the safe master

        [AvaloniaTest]
        public void TheSafeMasterScrubsLimitsAndLeavesQuietAudioAlone()
        {
            var safe = new SafeMaster();
            var rng = new Random(7);
            var quiet = Enumerable.Range(0, 1024).Select(_ => (float)(rng.NextDouble() * 1.9 - 0.95)).ToArray();
            var copy = quiet.ToArray();
            safe.Process(quiet, 2, 48000);
            Assert.That(quiet, Is.EqualTo(copy), "below the ceiling: bit for bit");
            Assert.That(safe.LatchedOverDb, Is.Null);

            var bad = new float[] { 0.5f, float.NaN, float.PositiveInfinity, -0.25f, float.NegativeInfinity, 0.1f };
            safe.Process(bad, 2, 48000);
            Assert.That(bad, Is.EqualTo(new[] { 0.5f, 0, 0, -0.25f, 0, 0.1f }), "NaN and infinity become 0");
            Assert.That(safe.Scrubbed, Is.EqualTo(3));

            var hot = Enumerable.Range(0, 4800 * 2).Select(i => 2f * MathF.Sin(i / 2 * 0.06f)).ToArray();    // +6 dBFS
            safe.Process(hot, 2, 48000);
            Assert.That(hot.Max(MathF.Abs), Is.LessThanOrEqualTo(1f), "the hard ceiling");
            Assert.That(safe.LatchedOverDb, Is.EqualTo(6.0).Within(0.1));
            Assert.That(safe.TakeGain(), Is.EqualTo(0.5f).Within(0.01f));

            // Quiet again, the gain comes back (about 50 ms), then the audio is untouched once more.
            var after = Enumerable.Range(0, 48000 * 2).Select(i => 0.5f * MathF.Sin(i / 2 * 0.06f)).ToArray();
            var plain = after.ToArray();
            safe.Process(after, 2, 48000);
            Assert.That(after.Take(200), Is.Not.EqualTo(plain.Take(200)), "still releasing just after the peak");
            Assert.That(after.Skip(2 * 36000), Is.EqualTo(plain.Skip(2 * 36000)), "released (50 ms time constant): untouched again by 0.75 s");

            safe.ClearOver();
            Assert.That(safe.LatchedOverDb, Is.Null);
            Assert.That(safe.LimitedSinceClear, Is.False);
        }

        [AvaloniaTest]
        public void ASineAt12dBFSReachesTheDeviceAtTheCeilingAndTheClipIndicatorLatches()
        {
            var (window, view) = MachineViewTests.OpenEmpty();
            int rate = Core.SelectedAudioDriverSampleRate;
            var provider = new CommonAudioProvider(Core, Core.EngineSettings, rate, 2, 256, false, Host.Registry);
            try
            {
                var hot = Add("Hot", Sine, dbfs: 12);
                Settle();
                ReBuzzCore.masterInfo.PosInTick = 0;
                float[] Device(int buffers)
                {
                    var all = new List<float>();
                    var buffer = new float[512];
                    for (int i = 0; i < buffers; i++)
                    {
                        provider.Read(buffer);
                        all.AddRange(buffer);
                    }
                    return all.ToArray();
                }

                var heard = Device(60);
                Assert.That(Peak(heard), Is.LessThanOrEqualTo(1f), "the device gets no more than the ceiling (0 dBFS)");
                Assert.That(Peak(heard), Is.GreaterThan(0.99f));
                Assert.That(Core.SafeMaster.LatchedOverDb, Is.EqualTo(12.0).Within(0.1), "the clip indicator latched the over, before the limiter");
                Assert.That(Core.SafeMaster.LimitedSinceClear, Is.True);

                // The transport's meter and the Master show it; a click on the indicator clears it.
                window.RefreshNow();
                var meter = window.Transport.Meter;
                Assert.That(meter.OverText, Is.EqualTo("+12.0 dB"));
                Assert.That(meter.Limiting, Is.True);
                Assert.That(Call("studio_status")["safety"]!["clip"]!["latched_over_dbfs"]!.GetValue<double>(), Is.EqualTo(12.0).Within(0.1));
                Assert.That(Call("studio_status")["safety"]!["limiter"]!["acted"]!.GetValue<bool>(), Is.True);
                Settle();
                Device(2);          // limiting now, for the picture
                window.RefreshNow();
                Studio.Screenshot(window, "safety-clip");
                MachineViewTests.Click(window, meter.ClipButton.TranslatePoint(new Avalonia.Point(10, 8), window)!.Value);
                Assert.That(meter.OverText, Is.EqualTo(""));
                Assert.That(Core.SafeMaster.LatchedOverDb, Is.Null);

                // Latched again, cleared from the Master's menu.
                Device(4);
                window.RefreshNow();
                Assert.That(Core.MasterOver, Is.True);
                MachineViewTests.Menu(window, view, view.PointOf(Master, window), null, "Clear clip indicator (+12.0 dB)");
                Assert.That(Core.MasterOver, Is.False);

                // A lower ceiling; the limiter off lets the true signal through.
                Core.SafeMaster.CeilingDb = -3;
                Assert.That(Db(Peak(Device(20).Skip(512 * 10))), Is.LessThanOrEqualTo(-3.0 + 1e-4));
                Core.SafeMaster.LimiterOn = false;
                Assert.That(Db(Peak(Device(20))), Is.EqualTo(12).Within(0.1), "off: the device gets the true signal");
                Core.SafeMaster.LimiterOn = true;
                Core.SafeMaster.CeilingDb = 0;

                // A render (offline) gets the true signal too.
                StopAll();
                Assert.That(Db(Peak(Render(40))), Is.EqualTo(12).Within(0.1), "a render still measures +12 dBFS");
            }
            finally
            {
                provider.Stop();
                MachineViewTests.Close(window);
            }
        }

        // ------------------------------------------------------------------ preferences

        [AvaloniaTest]
        public void PreferencesAskBeforeTheLimiterOrTheGuardGoesOff()
        {
            var registry = Host.Registry;
            try
            {
                var prefs = PreferencesWindow.Open(Host, "Audio");
                Settle();
                var page = prefs.Page<AudioPage>();
                Assert.That(page.LimiterBox.IsChecked, Is.True, "on by default");
                Assert.That(page.GuardBox.IsChecked, Is.True);
                Assert.That(page.CeilingBox.SelectedItem, Is.EqualTo("0 dBFS"));
                Assert.That(page.ThresholdBox.SelectedItem, Is.EqualTo("0 dBFS"));
                prefs.Height = 960;     // the Safety section in the picture
                Settle();
                Studio.Screenshot(prefs, "prefs-audio-safety");

                var asked = new List<string>();
                Dialogs.Responder = (m, c, b) => { asked.Add(c); return UserAnswer.No; };
                page.LimiterBox.IsChecked = false;
                Settle();
                Assert.That(asked, Is.EqualTo(new[] { "Safety limiter" }));
                Assert.That(Core.SafeMaster.LimiterOn, Is.True, "not confirmed: still on");
                Assert.That(page.LimiterBox.IsChecked, Is.True);

                Dialogs.Responder = (m, c, b) => { asked.Add(c); return UserAnswer.Yes; };
                page.LimiterBox.IsChecked = false;
                Settle();
                Assert.That(Core.SafeMaster.LimiterOn, Is.False);
                Assert.That(registry.Read("SafeMasterLimiter", 1, SafeMaster.Section), Is.EqualTo(0), "saved");
                page.LimiterBox.IsChecked = true;
                Settle();
                Assert.That(asked.Count, Is.EqualTo(2), "turning it on asks nothing");
                Assert.That(Core.SafeMaster.LimiterOn, Is.True);

                page.CeilingBox.SelectedItem = "−1 dBFS";
                Assert.That(Core.SafeMaster.CeilingDb, Is.EqualTo(-1));
                Assert.That(registry.Read("SafeMasterCeiling", 0, SafeMaster.Section), Is.EqualTo(-10));
                page.ThresholdBox.SelectedItem = "+3 dBFS";
                Assert.That(Core.SafeMaster.ClipThresholdDb, Is.EqualTo(3));
                Assert.That(registry.Read("ClipThreshold", 0, SafeMaster.Section), Is.EqualTo(30));

                // The guard: off only for this song, after asking; a new song turns it on again.
                page.GuardBox.IsChecked = false;
                Settle();
                Assert.That(asked.Last(), Is.EqualTo("Machine guard"));
                Assert.That(MachineGuard.Enabled, Is.False);
                Assert.That(MainWindow.SafetyText(Core), Does.Contain("machine guard off"));
                prefs.Close();
                NewSong();
                Assert.That(MachineGuard.Enabled, Is.True, "a new song turns the guard on again");
            }
            finally
            {
                foreach (var key in new[] { "SafeMasterLimiter", "SafeMasterCeiling", "ClipThreshold" }) registry.DeleteCurrentUserValue(key, SafeMaster.Section);
            }
        }

        // ------------------------------------------------------------------ the agent, in process

        /// <summary>A tool call on the studio's agent server; its structured result (the UI thread runs it meanwhile).</summary>
        static JsonObject Call(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
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
            Assert.That(r["error"], Is.Null, r.ToJsonString());
            var result = r["result"]!.AsObject();
            Assert.That(result["isError"]?.GetValue<bool>() == true, Is.False, result.ToJsonString());
            return result["structuredContent"]!.AsObject();
        }
    }
}
