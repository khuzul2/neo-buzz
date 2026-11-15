// Fork (docs/specs/ux-overhaul.md P6b, §9): the Windows-parity features of the studio, headless: Clone with patterns; Latency; Signal
// Analysis (the tap's ring, the FFT, the window's subscriptions); the CPU monitor; the Hard Disk Recorder (the wave writer, the
// recorder's loop rules, 0 bytes per block); the debug console; MIDI bindings and the MIDI focus; Song Properties.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Headless.NUnit;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Tools;
using ReBuzz.Studio.Views.Machines;
using Serilog.Events;
using static ReBuzz.Studio.Tests.MachineViewTests;
using static ReBuzz.Studio.Tests.UxP4aTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP6bTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static int Listeners(string eventName)
        {
            var f = typeof(ReBuzzCore).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return (f.GetValue(Core) as Delegate)?.GetInvocationList().Length ?? 0;
        }

        static IMachine Machine(string name) => Core.SongCore.Machines.First(m => m.Name == name);

        // ------------------------------------------------------------------ Clone with patterns

        [AvaloniaTest]
        public void CloneWithPatternsCopiesTheMachineItsPatternsItsSequenceAndTheWiresBetweenTheClones()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            Call("machine_add", Args(("gear", "Neo Delay"), ("name", "Echo")));
            Call("connect", Args(("from", "Voice"), ("to", "Echo")));
            var voice = Machine("Voice");
            int machines = Core.SongCore.Machines.Count, sequences = Core.SongCore.Sequences.Count;
            int events = Core.SongCore.Sequences.First(s => s.Machine == voice).Events.Count;
            var plain = MachineEdits.Duplicate(Core, new[] { voice });
            Assert.That(plain[0].Patterns.Count, Is.EqualTo(0), "Duplicate leaves the patterns behind");
            Core.SongCore.ActionStack.Undo();
            Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(machines));

            var copies = MachineEdits.CloneWithPatterns(Core, new[] { voice, Machine("Echo") });
            Assert.That(copies, Has.Count.EqualTo(2));
            Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(machines + 2));
            var v2 = copies[0];
            Assert.That(v2.Patterns.Select(p => p.Name), Is.EqualTo(new[] { "P" }), "the pattern came along");
            Assert.That(v2.Patterns[0].Length, Is.EqualTo(voice.Patterns[0].Length));
            Assert.That(Core.SongCore.Sequences.Count, Is.EqualTo(sequences + 1), "and its row in the sequence");
            var row = Core.SongCore.Sequences.First(s => s.Machine == v2);
            Assert.That(row.Events.Count, Is.EqualTo(events));
            Assert.That(row.Events.Values.Where(e => e.Pattern != null).All(e => e.Pattern == v2.Patterns[0]), "playing the copy's pattern, not the original's");
            Assert.That(copies[0].Outputs.Select(c => c.Destination), Contains.Item(copies[1]), "the wire between the two selected machines was cloned");
            Assert.That(voice.Outputs.Select(c => c.Destination), Does.Not.Contain(copies[1]), "and not crossed with the originals");
            Core.SongCore.ActionStack.Undo();
            Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(machines), "one undo step takes the lot back");
            Assert.That(Core.SongCore.Sequences.Count, Is.EqualTo(sequences));
        }

        [AvaloniaTest]
        public void TheMachineMenuHasClonePatternsLatencyAndSignalAnalysisAndTheMasterHasSignalAnalysis()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            var canvas = new MachineCanvas(Core);
            var menus = new MachineMenus(Core, canvas);
            var voice = menus.ForMachine(Machine("Voice"), new[] { Machine("Voice") });
            var headers = voice.Items.OfType<Avalonia.Controls.MenuItem>().Select(i => i.Header?.ToString()).ToList();
            Assert.That(headers, Is.SupersetOf(new[] { "Duplicate", "Clone with patterns", "Latency...", "Signal Analysis..." }));
            var master = menus.ForMachine(Machine("Master"), new[] { Machine("Master") });
            Assert.That(master.Items.OfType<Avalonia.Controls.MenuItem>().Select(i => i.Header?.ToString()), Contains.Item("Signal Analysis..."));
        }

        // ------------------------------------------------------------------ Latency

        [AvaloniaTest]
        public void TheLatencyDialogOverridesTheMachinesLatencyOrTakesTheOverrideOff()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            var voice = Machine("Voice");
            Assert.That(voice.OverrideLatency, Is.EqualTo(-1));
            var d = new LatencyDialog(voice);
            Assert.That(d.Override.IsChecked, Is.False);
            Assert.That(d.Title, Is.EqualTo("Latency - Voice"));
            d.Override.IsChecked = true;
            d.Samples.Text = "-3";
            Assert.That(d.Ok.IsEnabled, Is.False, "a negative number can't be accepted");
            d.Samples.Text = "abc";
            Assert.That(d.Ok.IsEnabled, Is.False);
            Assert.That(d.Problem.Text, Is.Not.Empty);
            d.Samples.Text = "128";
            Assert.That(d.Ok.IsEnabled, Is.True);
            Assert.That(d.Apply(), Is.True);
            Assert.That(voice.OverrideLatency, Is.EqualTo(128));
            var again = new LatencyDialog(voice);
            Assert.That((again.Override.IsChecked, again.Samples.Text), Is.EqualTo((true, "128")), "it shows what is set");
            again.Override.IsChecked = false;
            again.Apply();
            Assert.That(voice.OverrideLatency, Is.EqualTo(-1));
            voice.OverrideLatency = 64;
            Machine("Master").OverrideLatency = 5;
            again.ClearAll.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.That(Core.SongCore.Machines.All(m => m.OverrideLatency == -1), "Clear all overrides");
        }

        // ------------------------------------------------------------------ Song Properties

        [AvaloniaTest]
        public void SongPropertiesSetsTempoAndTicksPerBeatInOneUndoStepAndRefusesBadNumbers()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => { Core.Speed = 0; Restore(window, null); });
            int bpm = Core.BPM, tpb = Core.TPB;
            var d = new SongPropertiesDialog(Core);
            Assert.That((d.Bpm.Text, d.Tpb.Text), Is.EqualTo((bpm.ToString(), tpb.ToString())));
            d.Bpm.Text = "9";
            Assert.That(d.Ok.IsEnabled, Is.False);
            Assert.That(d.Problem.Text, Does.Contain("Tempo"));
            d.Bpm.Text = "140"; d.Tpb.Text = "33";
            Assert.That(d.Problem.Text, Does.Contain("Ticks per beat"));
            d.Tpb.Text = "8"; d.Speed.Text = "20";
            Assert.That(d.Apply(), Is.True);
            Settle();
            Assert.That((Core.BPM, Core.TPB, Core.Speed), Is.EqualTo((140, 8, 20)));
            Core.SongCore.ActionStack.Undo();
            Assert.That((Core.BPM, Core.TPB), Is.EqualTo((bpm, tpb)), "tempo and ticks per beat are one undo step");
        }

        // ------------------------------------------------------------------ Signal Analysis

        [Test]
        public void TheSpectrumReadsASineAtItsLevelOnItsBin()
        {
            var s = new Spectrum(2048);
            var x = new double[2048];
            int bin = 100;
            for (int i = 0; i < x.Length; i++) x[i] = 0.5 * Math.Sin(2 * Math.PI * bin * i / 2048.0);
            var db = new double[s.Bins];
            foreach (var window in Spectrum.Windows)
            {
                s.SetWindow(window);
                s.Magnitudes(x, db);
                int top = Array.IndexOf(db, db.Max());
                Assert.That(top, Is.EqualTo(bin).Within(1), window);
                Assert.That(db[top], Is.EqualTo(-6.02).Within(window == "Flat top" ? 0.2 : 1.5), window + ": half of full scale is -6 dB");
                Assert.That(db[10], Is.LessThan(db[top] - 40), window + ": far from the sine it is quiet");
            }
            s.Magnitudes(new double[2048], db);
            Assert.That(db.Max(), Is.EqualTo(-120), "silence");
        }

        [Test]
        public void TheTapRingKeepsTheLatestFramesAndAddsAllocateNothing()
        {
            var tap = new SignalTap(1024);
            var block = new float[512];
            for (int i = 0; i < block.Length; i += 2) { block[i] = 16384f; block[i + 1] = -8192f; }
            tap.Add(block, true);      // warm up
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) tap.Add(block, true);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0), "0 bytes per block");
            var l = new double[300]; var r = new double[300];
            Assert.That(tap.CopyLatest(l, r), Is.EqualTo(300));
            Assert.That((l[299], r[299]), Is.EqualTo((0.5, -0.25)));
            var mono = new float[100];
            Array.Fill(mono, 32768f);
            tap.Add(mono, false);
            tap.CopyLatest(l, r);
            Assert.That((l[299], r[299]), Is.EqualTo((1.0, 1.0)), "a mono block feeds both sides");
            Assert.That(tap.Written, Is.EqualTo(256 * 1001 + 100));
        }

        [AvaloniaTest]
        public void TheSignalAnalysisWindowListensWhileOpenDrawsWhatItHearsAndLetsGoWhenClosed()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            Call("machine_add", Args(("gear", "Neo Delay"), ("name", "Echo")));
            Call("connect", Args(("from", "Voice"), ("to", "Echo")));
            var master = Machine("Master");
            int onMaster = Listeners("MasterTap");
            var wm = SignalAnalysisWindow.Open(Studio.Host, master, window);
            Settle();
            Assert.That(Listeners("MasterTap"), Is.EqualTo(onMaster + 1), "open: it taps the Master");
            Assert.That(wm.Listening);
            // A 1 kHz-ish tone in: the window shows its peak.
            int rate = Math.Max(1, Core.SelectedAudioDriverSampleRate);
            var tone = new float[8192];
            double hz = 43.0664 * 40;      // 40 bins of 44.1 kHz / 1024
            for (int i = 0; i < tone.Length / 2; i++) tone[2 * i] = tone[2 * i + 1] = (float)(16384 * Math.Sin(2 * Math.PI * hz * i / rate));
            wm.Tap.Add(tone, true);
            wm.Tick();
            Assert.That(wm.InfoText, Does.StartWith("Peak"), wm.InfoText);
            Assert.That(wm.SpectrumLeft.Max(), Is.GreaterThan(-20));
            wm.Close();
            Settle();
            Assert.That(Listeners("MasterTap"), Is.EqualTo(onMaster), "closed: the tap is let go");
            Assert.That(SignalAnalysisWindow.Current, Is.Null);

            var echo = Machine("Voice");
            var connection = echo.Outputs.First();
            int onConnection = connection.GetType().GetField("Tap", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } f ? (f.GetValue(connection) as Delegate)?.GetInvocationList().Length ?? 0 : 0;
            var wv = SignalAnalysisWindow.Open(Studio.Host, echo, window);
            Settle();
            Assert.That(wv.OutputChoice.ItemCount, Is.EqualTo(echo.Outputs.Count));
            Assert.That(wv.OutputChoice.Items.Cast<string>(), Contains.Item("to Echo"));
            Assert.That(wv.OutputChoice.SelectedIndex, Is.EqualTo(0));
            wv.Close();
        }

        // ------------------------------------------------------------------ CPU monitor

        [Test]
        public void TheCpuMonitorModelReckonsEachMachinesShareTheTotalAndTheOverhead()
        {
            var m = new CpuMonitorModel();
            MachinePerformanceData Data(long perf, long cycles, long samples, long lockTime = 0) =>
                new MachinePerformanceData { PerformanceCount = perf, CycleCount = cycles, SampleCount = samples, MaxEngineLockTime = lockTime };
            var a = new object(); var b = new object();
            m.Update(new BuzzPerformanceData { PerformanceCount = 0, EnginePerformanceCount = 0 },
                new[] { new CpuMonitorModel.Entry(b, "Bravo", Data(0, 0, 0)), new CpuMonitorModel.Entry(a, "alpha", Data(0, 0, 0)) });
            Assert.That(m.HasReading, Is.False, "the first reading has nothing to compare");
            m.Update(new BuzzPerformanceData { PerformanceCount = 1000, EnginePerformanceCount = 400 },
                new[] { new CpuMonitorModel.Entry(b, "Bravo", Data(100, 5000, 50)), new CpuMonitorModel.Entry(a, "alpha", Data(250, 9000, 30, 7)) });
            Assert.That(m.Rows.Select(r => r.Name), Is.EqualTo(new[] { "alpha", "Bravo" }), "by name");
            Assert.That(m.Rows[0].Cpu, Is.EqualTo(25.0).Within(1e-9));
            Assert.That(m.Rows[1].Cpu, Is.EqualTo(10.0).Within(1e-9));
            Assert.That(m.Rows[0].CyclesPerSample, Is.EqualTo(300));
            Assert.That(m.Rows[1].CyclesPerSample, Is.EqualTo(100));
            Assert.That(m.Total, Is.EqualTo(40.0).Within(1e-9));
            Assert.That(m.Overhead, Is.EqualTo(5.0).Within(1e-9), "40 less 25 and 10");
            // The lock time is the one the previous snapshot held (the engine zeroes it at each snapshot).
            m.Update(new BuzzPerformanceData { PerformanceCount = 2000, EnginePerformanceCount = 500 },
                new[] { new CpuMonitorModel.Entry(b, "Bravo", Data(100, 5000, 50)), new CpuMonitorModel.Entry(a, "alpha", Data(250, 9000, 30)) });
            Assert.That(m.Rows[0].MaxLockTime, Is.EqualTo(7));
            Assert.That(m.Total, Is.EqualTo(10.0).Within(1e-9));
            m.ResetLocks();
            Assert.That(m.Rows[0].MaxLockTime, Is.EqualTo(0));
            m.Update(new BuzzPerformanceData { PerformanceCount = 3000, EnginePerformanceCount = 500 }, new[] { new CpuMonitorModel.Entry(a, "alpha", Data(300, 9500, 40)) });
            Assert.That(m.Rows.Select(r => r.Name), Is.EqualTo(new[] { "alpha" }), "a removed machine drops out");
        }

        [AvaloniaTest]
        public void TheCpuMonitorListsTheMachinesAndHoldsNothingOnceClosed()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            var w = CpuMonitorWindow.Open(Studio.Host, window);
            Settle();
            w.Tick();
            System.Threading.Thread.Sleep(30);
            w.Tick();
            Assert.That(w.RowsText, Does.Contain("Voice:").And.Contain("Master:"));
            Assert.That(CpuMonitorWindow.Open(Studio.Host, window), Is.SameAs(w), "one window");
            Call("machine_add", Args(("gear", "Neo Delay"), ("name", "Echo")));
            w.Tick();
            Assert.That(w.RowsText, Does.Contain("Echo:"), "a machine added while it is open");
            w.Close();
            Settle();
            Assert.That(CpuMonitorWindow.Current, Is.Null);
        }

        // ------------------------------------------------------------------ Hard Disk Recorder

        [Test]
        public void TheWaveWriterMakesAFileOfEachDepthWithItsHeaderFilledIn()
        {
            string folder = Path.Combine(Path.GetTempPath(), "p6b-wav-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                foreach (var (format, bytes) in new[] { (WavFormat.Pcm16, 2), (WavFormat.Pcm24, 3), (WavFormat.Pcm32, 4), (WavFormat.Float32, 4) })
                {
                    string path = Path.Combine(folder, format + ".wav");
                    using (var w = new WavStreamWriter(path, 48000, 2, format))
                        w.Write(new[] { 0.5f, -0.5f, 1.5f, -1.5f }, 0, 4);
                    var data = File.ReadAllBytes(path);
                    Assert.That(data.Length, Is.EqualTo(44 + 4 * bytes), format.ToString());
                    Assert.That(BitConverter.ToInt32(data, 4), Is.EqualTo(36 + 4 * bytes), "RIFF size");
                    Assert.That(BitConverter.ToInt32(data, 40), Is.EqualTo(4 * bytes), "data size");
                    Assert.That(BitConverter.ToInt32(data, 24), Is.EqualTo(48000));
                    Assert.That(BitConverter.ToInt16(data, 20), Is.EqualTo(format == WavFormat.Float32 ? 3 : 1));
                    var read = ReBuzz.Agent.Audio.Wav.Read(path);
                    Assert.That(read.Frames, Is.EqualTo(2));
                    Assert.That(read.Left[0], Is.EqualTo(0.5).Within(1e-4), format.ToString());
                    Assert.That(read.Right[0], Is.EqualTo(-0.5).Within(1e-4));
                    if (format != WavFormat.Float32) Assert.That(read.Left[1], Is.EqualTo(1.0).Within(1e-3), "integer formats clip at full scale");
                }
            }
            finally { Directory.Delete(folder, true); }
        }

        [AvaloniaTest]
        public void TheRecorderWritesWhatTheMasterTapHearsAndTheTapsThreadAllocatesNothing()
        {
            var window = SongWith("Neo Poly", 8);
            string folder = Path.Combine(Path.GetTempPath(), "p6b-rec-" + Guid.NewGuid().ToString("N"));
            using var restore = Studio.AfterTest(() => { Restore(window, null); try { Directory.Delete(folder, true); } catch (Exception) { } });
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "take.wav");
            int onMaster = Listeners("MasterTap");
            var rec = new MasterRecorder(Core, loop: false);
            rec.Start(path, WavFormat.Float32);
            Assert.That(Listeners("MasterTap"), Is.EqualTo(onMaster + 1));
            var block = new float[512];
            for (int i = 0; i < block.Length; i += 2) { block[i] = 16384f; block[i + 1] = -32768f; }
            var time = new SongTime { CurrentTick = 3 };
            rec.OnTap(block, true, time);        // warm up
            long allocated = 0;
            for (int i = 0; i < 200; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                rec.OnTap(block, true, time);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                if (i % 50 == 49) System.Threading.Thread.Sleep(20);      // let the writer drain
            }
            Assert.That(allocated, Is.EqualTo(0), "0 bytes per block on the tap's thread");
            rec.Stop();
            Assert.That(Listeners("MasterTap"), Is.EqualTo(onMaster), "stopped: let go");
            Assert.That(rec.Dropped, Is.EqualTo(0));
            var read = ReBuzz.Agent.Audio.Wav.Read(path);
            Assert.That(read.Frames, Is.EqualTo(201 * 256));
            Assert.That(read.Left[0], Is.EqualTo(0.5).Within(1e-6));
            Assert.That(read.Right[read.Frames - 1], Is.EqualTo(-1.0).Within(1e-6));
            Assert.That(MasterRecorder.NextFree(path), Is.EqualTo(Path.Combine(folder, "take-0001.wav")), "a take never overwrites another");
        }

        [AvaloniaTest]
        public void ALoopRecordingWaitsForTheLoopsFirstTickAndEndsAfterItsLast()
        {
            var window = SongWith("Neo Poly", 8);
            string folder = Path.Combine(Path.GetTempPath(), "p6b-loop-" + Guid.NewGuid().ToString("N"));
            int start = Core.Song.LoopStart, end = Core.Song.LoopEnd;
            using var restore = Studio.AfterTest(() =>
            {
                Core.Playing = false;
                Core.Song.LoopStart = start; Core.Song.LoopEnd = end;
                Restore(window, null);
                try { Directory.Delete(folder, true); } catch (Exception) { }
            });
            Directory.CreateDirectory(folder);
            Core.Song.LoopStart = 16; Core.Song.LoopEnd = 32;
            var rec = new MasterRecorder(Core, loop: true);
            rec.Start(Path.Combine(folder, "loop.wav"), WavFormat.Pcm16);
            Assert.That(rec.Now, Is.EqualTo(MasterRecorder.State.WaitingForLoop));
            var block = new float[256];
            Array.Fill(block, 1000f);
            rec.OnTap(block, true, new SongTime { CurrentTick = 4 });
            Assert.That(rec.Frames, Is.EqualTo(0), "before the loop: nothing");
            rec.OnTap(block, true, new SongTime { CurrentTick = 16 });
            Assert.That(rec.Now, Is.EqualTo(MasterRecorder.State.Recording));
            rec.OnTap(block, true, new SongTime { CurrentTick = 24 });
            Assert.That(rec.Progress, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(rec.LoopFinished, Is.False);
            rec.OnTap(block, true, new SongTime { CurrentTick = 32 });
            Assert.That(rec.LoopFinished, Is.True, "past the loop's last tick");
            rec.Stop();
            Assert.That(ReBuzz.Agent.Audio.Wav.Read(Path.Combine(folder, "loop.wav")).Frames, Is.EqualTo(2 * 128), "only the loop's two blocks");
        }

        [AvaloniaTest]
        public void TheRecorderWindowNamesTheTakeAndSaysWhyItCantRecordWithoutAnOutput()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            var w = HardDiskRecorderWindow.Open(Studio.Host, window);
            Settle();
            Assert.That(w.PathBox.Text, Does.EndWith(".wav"));
            Assert.That(HardDiskRecorderWindow.Open(Studio.Host, window), Is.SameAs(w));
            w.Tick();
            Assert.That(w.Stop.IsEnabled, Is.False);
            if (!w.HasOutput)
            {
                Assert.That(w.Record.IsEnabled, Is.False);
                Assert.That(w.Status.Text, Does.Contain("No audio output"));
                Assert.That(w.Begin(false), Is.False);
            }
            else Assert.That(w.Record.IsEnabled, Is.True);
            w.Close();
            Settle();
            Assert.That(HardDiskRecorderWindow.Current, Is.Null);
        }

        // ------------------------------------------------------------------ debug console

        [AvaloniaTest]
        public void TheDebugConsoleShowsTheEnginesLogFilteredByLevelAndTextAndClearsIt()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            Assert.That(DebugLog.Instance, Is.Not.Null, "installed at start");
            Core.DCWriteLine("p6b hello console");
            Core.DCWriteLine("p6b something is wrong", DCLogLevel.Error);
            Assert.That(DebugLog.Instance.Snapshot().Select(l => l.Text), Contains.Item("p6b hello console"), "DCWriteLine reaches the console's memory");
            var w = DebugConsoleWindow.Open(Studio.Host, window);
            Settle();
            Assert.That(w.Text.Text, Does.Contain("p6b hello console").And.Contain("ERR p6b something is wrong"));
            w.Level.SelectedIndex = 2;
            Assert.That(w.Text.Text, Does.Contain("something is wrong").And.Not.Contain("hello console"), "errors only");
            w.Level.SelectedIndex = 0;
            w.Filter.Text = "hello";
            Assert.That(w.Text.Text, Does.Contain("hello console").And.Not.Contain("something is wrong"));
            w.Filter.Text = "";
            Core.DCWriteLine("p6b a later line");
            w.Tick();
            Assert.That(w.Text.Text, Does.EndWith("p6b a later line"), "newest at the bottom");
            w.Clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.That(w.Text.Text, Is.Empty);
            w.Close();
            var log = DebugLog.Detached();
            for (int i = 0; i < DebugLog.Capacity + 10; i++) log.Add(LogEventLevel.Information, "line " + i);
            var all = log.Snapshot();
            Assert.That(all, Has.Count.EqualTo(DebugLog.Capacity));
            Assert.That(all[0].Text, Is.EqualTo("line 10"), "the oldest go first");
        }

        // ------------------------------------------------------------------ MIDI bindings and the MIDI focus

        [AvaloniaTest]
        public void MidiBindingsListsEveryBoundParameterAndClearsOneOrAllInUndoableSteps()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            var backend = new MidiBackend(Core);
            var param = Machine("Voice").ParameterGroups.SelectMany(g => g.Parameters).First(p => p.Type != ParameterType.Note && p.Type != ParameterType.Internal && p.Group.Type == ParameterGroupType.Global);
            var w = MidiBindingsWindow.Open(Studio.Host, window);
            Settle();
            var baseline = w.Lines.ToList();      // a machine can come with bindings of its own
            backend.Bind("Voice", param.Name, 0, 3, 74);
            w.Tick();
            string mine = $"Voice {param.Name} 0: Chan 3, Ctrl 74";
            Assert.That(w.Lines.Except(baseline), Is.EqualTo(new[] { mine }));
            Assert.That(w.ClearAll.IsEnabled, Is.True);
            w.Filter.Text = "nothing like it";
            Assert.That(w.Lines, Is.Empty, "filtered");
            w.Filter.Text = "";
            w.ClearRow(w.Lines.ToList().IndexOf(mine));
            Assert.That(param.GetMIDIBinding(0).Item1, Is.EqualTo(-1), "cleared");
            Assert.That(w.Lines, Does.Not.Contain(mine));
            Core.SongCore.ActionStack.Undo();
            w.Tick();
            Assert.That(w.Lines, Contains.Item(mine), "one undo step brings it back");
            w.Close();
            Assert.That(MidiBindingsWindow.Current, Is.Null);
        }

        [AvaloniaTest]
        public void TheMidiFocusBarListsTheMachinesMovesTheFocusAndKeepsTheLockOnWhenItDoes()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => { Core.MIDIFocusLocked = false; Restore(window, null); });
            Call("machine_add", Args(("gear", "Neo Delay"), ("name", "Echo")));
            var bar = window.MidiFocus;
            window.RefreshNow();
            Assert.That(bar.IsVisible);
            Assert.That(bar.Names, Is.EqualTo(new[] { "Echo", "Voice" }), "the machines by name, not the Master");
            bar.Machines.SelectedIndex = 0;
            Assert.That(Core.MIDIFocusMachine?.Name, Is.EqualTo("Echo"));
            Assert.That(bar.Lock.IsChecked, Is.False);
            Core.MIDIFocusLocked = true;
            window.RefreshNow();
            Assert.That(bar.Lock.IsChecked, Is.True, "the lock shows");
            bar.Machines.SelectedIndex = 1;
            Assert.That(Core.MIDIFocusMachine?.Name, Is.EqualTo("Voice"));
            Assert.That(Core.MIDIFocusLocked, Is.True, "choosing with the lock on keeps it on");
            bar.Lock.IsChecked = false;
            bar.Lock.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Primitives.ToggleButton.ClickEvent));
            Assert.That(Core.MIDIFocusLocked, Is.False);
        }

        // ------------------------------------------------------------------ machine groups

        [AvaloniaTest]
        public void MachineGroupsAreMadeFilledEmptiedAndDeletedFromTheMenusAndDrawnAsAFrame()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            Call("machine_add", Args(("gear", "Neo Delay"), ("name", "Echo")));
            var voice = Machine("Voice");
            var echo = Machine("Echo");
            var master = Machine("Master");
            Assert.That(MachineEdits.NewGroup(Core, new[] { master }), Is.Null, "the Master never joins a group");
            int groups = Core.SongCore.MachineGroups.Count;
            var g = MachineEdits.NewGroup(Core, new[] { voice, echo });
            Assert.That(Core.SongCore.MachineGroups.Count, Is.EqualTo(groups + 1));
            Assert.That(g.Name, Does.StartWith("New Group"));
            Assert.That(g.Machines, Is.EquivalentTo(new[] { voice, echo }));
            Assert.That(Core.SongCore.MachineToGroupDict[voice], Is.SameAs(g));

            var canvas = new MachineCanvas(Core);
            var menus = new MachineMenus(Core, canvas);
            canvas.Menus = menus;
            var menu = menus.ForMachine(voice, new[] { voice });
            var sub = menu.Items.OfType<Avalonia.Controls.MenuItem>().First(i => i.Header?.ToString() == "Group");
            var headers = sub.Items.OfType<Avalonia.Controls.MenuItem>().Select(i => i.Header?.ToString()).ToList();
            Assert.That(headers, Is.SupersetOf(new[] { "New Group From Selection", "Add To " + g.Name, "Remove From Group" }));
            Assert.That(sub.Items.OfType<Avalonia.Controls.MenuItem>().First(i => i.Header?.ToString() == "Remove From Group").IsEnabled, Is.True, "Voice is in a group");

            var shown = new Avalonia.Controls.Window { Content = canvas, Width = 700, Height = 500 };
            shown.Show();
            Settle();      // draws the frame (DrawGroups)
            MachineEdits.RemoveFromGroup(Core, new[] { voice });
            Assert.That(g.Machines, Is.EquivalentTo(new[] { echo }));
            Assert.That(Core.SongCore.MachineToGroupDict.ContainsKey(voice), Is.False);
            MachineEdits.AddToGroup(Core, new[] { voice }, g);
            Assert.That(g.Machines, Is.EquivalentTo(new[] { voice, echo }));
            canvas.Redraw();
            Settle();
            var canvasMenu = menus.ForCanvas(new Avalonia.Point(10, 10));
            Assert.That(canvasMenu.Items.OfType<Avalonia.Controls.MenuItem>().Select(i => i.Header?.ToString()), Contains.Item("Delete Group"));
            MachineEdits.DeleteGroup(Core, g);
            Assert.That(Core.SongCore.MachineGroups.Count, Is.EqualTo(groups), "deleted");
            Core.SongCore.ActionStack.Undo();
            Assert.That(Core.SongCore.MachineGroups.Count, Is.EqualTo(groups + 1), "an undo step brings the group back");
            shown.Close();
        }
    }
}
