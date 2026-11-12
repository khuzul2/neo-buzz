// Fork (audio hygiene, docs/specs/daw-modes.md §8.1): nothing on the audio thread that can cause a
// dropout in normal use. The engine renders here as the device's thread does (a WorkManager with the
// device's work algorithm and render threads, on a thread of its own), and these tests count what
// that thread and the render threads allocate per buffer, and what they compile:
//  - an empty song, and a Modern Pattern Editor pattern playing notes (its Note column and its MIDI
//    note lanes) and automation, with its editor open, through a Neo Ensemble (feedback, a synced
//    LFO, its depth moved every buffer), a Neo EQ in graphic mode (a slider moved every buffer,
//    so its solver runs), a Neo Delay (ping-pong, feedback, wow, drive and ducking, its time moved
//    every buffer, so it glides and crossfades), a Neo Multiband (linear phase, a crossover moved
//    every buffer, so its FIRs are designed again and crossfade) and a Neo Scope (synced to the bar as
//    the song loops, its display switched every few buffers): 0 bytes a buffer, with each work
//    algorithm;
//  - automation of a parameter with listeners (a parameter window's rows, the master volume's):
//    0 bytes on the audio thread, and the listeners still see the final values;
//  - after the warm-up (AudioWarmUp), the transport's first play and stop compile nothing (a Neo Poly
//    through a Neo Ensemble, a Neo Delay, a Neo Multiband, a Neo Scope and a Neo Filter, all just created);
//  - a Neo Sampler playing the song's wave and a built-in one (looped, choked, filtered, its tune moved
//    every buffer), while the wave is loaded again into its slot and copied in on the audio thread:
//    0 bytes a buffer, with each work algorithm;
//  - a Neo Drums playing every voice from its keys and hits from its BD column, its kick's tune and its
//    snare's level moved every buffer: 0 bytes a buffer, with each work algorithm;
//  - a Neo Poly pattern through a Neo Filter (the ladder driven, its LFO following the song, the
//    envelope moving the cutoff, the cutoff moved every buffer and the type switched now and then):
//    0 bytes a buffer, with each work algorithm; it is in the first play's chain too;
//  - a pattern through a Neo Clipper (three bands, mid/side, its drive moved every buffer, Delta switched) and a Neo
//    Limiter with every v2 stage (its soft clip switched, so its latency moves): 0 bytes a buffer, each algorithm;
//  - a pattern through a Neo Transient (mid/side, Impact mode now and then, its gains moved every buffer) and a Neo Low End
//    (every control working, the phase mode switched now and then, so its latency moves): 0 bytes a buffer, each algorithm;
//  - a pattern through a Neo Imager (every band shaped, Recover on), a Neo Exciter (its curve switched, mid/side, auto gain) and a
//    Neo Dither (its shaping switched): 0 bytes a buffer, each algorithm.
//    Neo Strip (every stage on, a bell's gain moved a buffer): 0 bytes a buffer, each algorithm.
// The tests build in Debug, where the JIT doesn't optimize: no allocation here is one that only an
// optimizing compiler (tier 1) would remove.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Parameters;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class AudioHygieneTests
    {
        const int TB = PatternEvent.TimeBase;
        const int Frames = 256;

        static ReBuzzCore Core => Studio.Host.Core;

        // Looping isn't part of the song (a new song keeps it): the tests after these start as they would have. NewSong
        // puts it back, in every test's finally, not a TearDown: Avalonia.Headless.NUnit (12.1.4) skips a failed test's
        // TearDown, and a first play that failed here left Looping on for ExitTests' song from scratch (CI runs
        // 41866682998, 41926371493, 41937966540, 41938676130, 41953730788: its Loop click turned looping off).
        static bool looping;

        [SetUp]
        public void KeepLooping() => looping = Core.Looping;

        /// <summary>The device's default (Audio preferences): Recursive Tasks.</summary>
        const int DeviceAlgorithm = 1;

        /// <summary>What a stretch of buffers cost the audio thread and the render threads.</summary>
        sealed class Cost
        {
            public long Bytes;
            public long Methods;
            public readonly List<string> Log = new List<string>();
        }

        /// <summary>
        /// The engine rendering as the device's thread does: a WorkManager with the given work
        /// algorithm, the render threads it uses, Multithreading as set, on a thread of its own that
        /// lives as long as this (a device's thread does: its first reads pay its one-time costs).
        /// </summary>
        sealed class AudioThread : IDisposable
        {
            readonly WorkManager work;
            readonly WorkThreadEngine engine;
            readonly int algorithm;
            readonly float[] buffer = new float[Frames * 2];
            readonly Thread thread;
            readonly SemaphoreSlim go = new SemaphoreSlim(0), done = new SemaphoreSlim(0);
            Action job;
            Exception failed;
            bool quit;

            public AudioThread(int algorithm)
            {
                this.algorithm = algorithm;
                engine = new WorkThreadEngine(4);
                if (algorithm == 2) engine.Start();
                work = new WorkManager(Core, engine, algorithm, Core.EngineSettings);
                thread = new Thread(Loop) { Name = "test audio thread", IsBackground = true };
                thread.Start();
            }

            /// <summary>
            /// The thread's id as the runtime's events give it (Linux: /proc/thread-self; macOS:
            /// pthread_threadid_np), read once as the thread starts, before anything is measured; 0 elsewhere.
            /// </summary>
            public long OsThreadId { get; private set; }

            void Loop()
            {
                OsThreadId = CurrentOsThreadId();
                while (true)
                {
                    go.Wait();
                    if (quit) return;
                    try { job(); }
                    catch (Exception e) { failed = e; }
                    done.Release();
                }
            }

            void On(Action action)
            {
                job = action;
                failed = null;
                go.Release();
                done.Wait();
                if (failed != null) throw failed;
            }

            /// <summary>
            /// Reads <paramref name="buffers"/> buffers (each after <paramref name="perBuffer"/>, on that thread). With
            /// <paramref name="describe"/>, each buffer is measured by ReBuzzTests' AllocationWindow too, and a buffer that
            /// allocated is described in the log: the types the runtime's allocation ticks name, JIT, type loads, GCs.
            /// </summary>
            public Cost Read(int buffers, Action perBuffer = null, bool describe = false)
            {
                var cost = new Cost();
                On(() =>
                {
                    using var window = describe ? new ReBuzzTests.Automation.AllocationWindow(buffers) : null;
                    long workerMethods = Interlocked.Read(ref RenderJobs.WorkerMethods);
                    long methods = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
                    for (int i = 0; i < buffers; i++)
                    {
                        int position = Core.SongCore.PlayPosition, inTick = ReBuzzCore.masterInfo.PosInTick;
                        perBuffer?.Invoke();
                        window?.Begin();
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        long workers = Interlocked.Read(ref RenderJobs.WorkerBytes);
                        work.MainAudioFillBuffer(buffer, 0, Frames * 2);
                        long bytes = GC.GetAllocatedBytesForCurrentThread() - before + Interlocked.Read(ref RenderJobs.WorkerBytes) - workers;
                        window?.End();
                        cost.Bytes += bytes;
                        if (bytes != 0 && cost.Log.Count < 20)
                            cost.Log.Add($"buffer {i}: {bytes} bytes (tick {position}, {inTick} samples into it)");
                    }
                    cost.Methods = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - methods
                                 + Interlocked.Read(ref RenderJobs.WorkerMethods) - workerMethods;
                    if (window != null && cost.Bytes != 0) cost.Log.Add(window.Describe());
                });
                return cost;
            }

            public void Dispose()
            {
                quit = true;
                go.Release();
                thread.Join();
                work.Stop();
                if (algorithm == 2) engine.Stop();
            }
        }

        [System.Runtime.InteropServices.DllImport("libSystem.dylib")]
        static extern int pthread_threadid_np(IntPtr thread, out ulong id);

        /// <summary>This thread's OS id, as the runtime's events report it; 0 when it can't be read here.</summary>
        static long CurrentOsThreadId()
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    string target = new System.IO.DirectoryInfo("/proc/thread-self").LinkTarget;   // "<pid>/task/<tid>"
                    return target != null && long.TryParse(target[(target.LastIndexOf('/') + 1)..], out long tid) ? tid : 0;
                }
                if (OperatingSystem.IsMacOS()) return pthread_threadid_np(IntPtr.Zero, out ulong id) == 0 ? (long)id : 0;
            }
            catch (Exception)
            {
            }
            return 0;
        }

        /// <summary>Reads past one-time costs (the first loop of the song, first notes), then measures.</summary>
        static Cost Steady(AudioThread audio, int buffers = 400, Action perBuffer = null)
        {
            audio.Read(800, perBuffer);
            return audio.Read(buffers, perBuffer);
        }

        /// <summary>Methods compiled while it listens, by OS thread (diagnostics for a failed check).</summary>
        sealed class JitListener : System.Diagnostics.Tracing.EventListener
        {
            public readonly System.Collections.Concurrent.ConcurrentQueue<string> Methods = new();
            readonly System.Collections.Concurrent.ConcurrentQueue<(long Thread, string Method)> byThread = new();
            public volatile bool On;

            /// <summary>
            /// What a failed check reports: the methods compiled on the audio thread first (with their
            /// tiers: 5 is an on-stack replacement, 3 a first compile, 6 and 7 the profiling tiers), then
            /// the first of the rest of the process's.
            /// </summary>
            public string Report(long audioThread)
            {
                var all = byThread.ToArray();
                var mine = audioThread != 0 ? all.Where(x => x.Thread == audioThread).Select(x => x.Method).ToList() : new List<string>();
                return $"\non the audio thread ({(audioThread != 0 ? audioThread.ToString() : "its id unknown here")}): " +
                       (mine.Count > 0 ? "\n" + string.Join("\n", mine) : "none") +
                       "\nelsewhere in the process meanwhile (the first 40):\n" +
                       string.Join("\n", all.Where(x => x.Thread != audioThread).Take(40).Select(x => x.Method));
            }

            protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
            {
                if (source.Name == "Microsoft-Windows-DotNETRuntime")
                    EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x10);
            }

            protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
            {
                if (!On || e.EventName == null || !e.EventName.StartsWith("MethodLoadVerbose")) return;
                string Get(string name) => e.PayloadNames.IndexOf(name) is int i && i >= 0 ? e.Payload[i]?.ToString() : "?";
                int flags = int.TryParse(Get("MethodFlags"), out int f) ? f : -1;
                string method = $"thread {e.OSThreadId}: {Get("MethodNamespace")}::{Get("MethodName")} (flags {flags:x}, tier {(flags >> 7) & 7}, rejit {Get("ReJITID")}, id {Get("MethodID")})";
                Methods.Enqueue(method);
                byThread.Enqueue((e.OSThreadId, method));
            }
        }

        static void AssertNoAllocation(Cost cost, string what)
        {
            TestContext.Out.WriteLine($"[notice] {what}: {cost.Bytes} bytes");
            Assert.That(cost.Bytes, Is.EqualTo(0), what + " allocates on the audio thread or the render threads (a GC pause is a dropout):\n" + string.Join("\n", cost.Log));
        }

        static void NewSong()
        {
            lock (ReBuzzCore.AudioLock) Core.Playing = false;
            Core.ExecuteCommand(BuzzCommand.Stop);
            Core.Modified = false;
            Core.ExecuteCommand(BuzzCommand.NewFile);
            Core.Looping = looping;
            Pump();
        }

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        static void Key(Window w, PhysicalKey key, RawInputModifiers m = RawInputModifiers.None)
        {
            w.KeyPressQwerty(key, m);
            w.KeyReleaseQwerty(key, m);
            Pump();
        }

        static MachineCore AddPoly(string name)
        {
            var song = Core.SongCore;
            song.CreateMachine("Neo Poly", null, name, null, null, null, -1, 0.3f, 0.3f);
            var m = song.MachinesList.Last(x => x.Name == name);
            song.ConnectMachines(m, song.MachinesList[0], new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            return m;
        }

        /// <summary>
        /// Neo Ensemble between a machine and the master: a flanger with feedback, its LFO synced to the
        /// tempo, so its audio path (the loop, the LFO, the parameter smoothing) is measured too.
        /// </summary>
        static MachineCore InsertEnsemble(MachineCore source, string name)
        {
            var song = Core.SongCore;
            var master = song.MachinesList[0];
            song.CreateMachine("Neo Ensemble", null, name, null, null, null, -1, 0.5f, 0.3f);
            var fx = song.MachinesList.Last(x => x.Name == name);
            song.DisconnectMachines(source.Outputs.First(c => c.Destination == master));
            song.ConnectMachines(source, fx, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            song.ConnectMachines(fx, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            var globals = fx.ParameterGroupsList[1].ParametersList;
            globals.First(p => p.Name == "Mode").SetValue(0, 1);
            globals.First(p => p.Name == "Feedback").SetValue(0, 1700);
            globals.First(p => p.Name == "Sync").SetValue(0, 7);
            return fx;
        }

        /// <summary>
        /// Neo EQ in graphic mode after a machine (machine-safety.md §4): a loudness curve, so its 31 bands
        /// run; a slider moved every buffer makes its solver run on the audio thread.
        /// </summary>
        static MachineCore InsertGraphicEq(MachineCore source, string name)
        {
            var song = Core.SongCore;
            var master = song.MachinesList[0];
            song.CreateMachine("Neo EQ", null, name, null, null, null, -1, 0.7f, 0.3f);
            var fx = song.MachinesList.Last(x => x.Name == name);
            song.DisconnectMachines(source.Outputs.First(c => c.Destination == master));
            song.ConnectMachines(source, fx, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            song.ConnectMachines(fx, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            var globals = fx.ParameterGroupsList[1].ParametersList;
            globals.First(p => p.Name == "Mode").SetValue(0, 1);
            globals.First(p => p.Name == "G 50 Hz").SetValue(0, 230);
            globals.First(p => p.Name == "G 10 kHz").SetValue(0, 210);
            return fx;
        }

        /// <summary>
        /// Neo Delay after a machine (mcp-server.md §12.5): ping-pong with feedback, the wow, drive and ducking on and
        /// the time free, so moving it every buffer makes the lines glide and crossfade on the audio thread.
        /// </summary>
        static MachineCore InsertDelay(MachineCore source, string name)
        {
            var song = Core.SongCore;
            var master = song.MachinesList[0];
            song.CreateMachine("Neo Delay", null, name, null, null, null, -1, 0.85f, 0.3f);
            var fx = song.MachinesList.Last(x => x.Name == name);
            song.DisconnectMachines(source.Outputs.First(c => c.Destination == master));
            song.ConnectMachines(source, fx, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            song.ConnectMachines(fx, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            var globals = fx.ParameterGroupsList[1].ParametersList;
            globals.First(p => p.Name == "Mode").SetValue(0, 1);
            globals.First(p => p.Name == "Sync").SetValue(0, 0);
            globals.First(p => p.Name == "Feedback").SetValue(0, 1690);
            globals.First(p => p.Name == "Mod depth").SetValue(0, 300);
            globals.First(p => p.Name == "Drive").SetValue(0, 60);
            globals.First(p => p.Name == "Duck").SetValue(0, 120);
            return fx;
        }

        /// <summary>
        /// Neo Multiband after a machine (mcp-server.md §8.2): linear phase, every band compressing, so moving a crossover every
        /// buffer designs its FIRs again on the audio thread and crossfades to them.
        /// </summary>
        static MachineCore InsertMultiband(MachineCore source, string name)
        {
            var song = Core.SongCore;
            var master = song.MachinesList[0];
            song.CreateMachine("Neo Multiband", null, name, null, null, null, -1, 0.85f, -0.3f);
            var fx = song.MachinesList.Last(x => x.Name == name);
            song.DisconnectMachines(source.Outputs.First(c => c.Destination == master));
            song.ConnectMachines(source, fx, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            song.ConnectMachines(fx, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            var globals = fx.ParameterGroupsList[1].ParametersList;
            for (int b = 1; b <= 4; b++) globals.First(p => p.Name == $"B{b} threshold").SetValue(0, 200);
            globals.First(p => p.Name == "Stereo").SetValue(0, 1);
            return fx;
        }

        /// <summary>
        /// Neo Scope after a machine (mcp-server.md §8.1): synced to a bar, so as the song loops its sweeps start on the
        /// bar and jump back with the loop; left and right overlaid, the second trace filled too.
        /// </summary>
        static MachineCore InsertScope(MachineCore source, string name)
        {
            var song = Core.SongCore;
            var master = song.MachinesList[0];
            song.CreateMachine("Neo Scope", null, name, null, null, null, -1, 0.85f, 0.6f);
            var fx = song.MachinesList.Last(x => x.Name == name);
            song.DisconnectMachines(source.Outputs.First(c => c.Destination == master));
            song.ConnectMachines(source, fx, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            song.ConnectMachines(fx, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            var globals = fx.ParameterGroupsList[1].ParametersList;
            globals.First(p => p.Name == "Sync").SetValue(0, 5);
            globals.First(p => p.Name == "Display").SetValue(0, 4);
            return fx;
        }

        /// <summary>
        /// Neo Filter after a machine (mcp-server.md §12.5): the ladder driven, its LFO following the song (synced to a quarter),
        /// the envelope moving the cutoff, so every segment computes new coefficients on the audio thread.
        /// </summary>
        static MachineCore InsertFilter(MachineCore source, string name)
        {
            var song = Core.SongCore;
            var master = song.MachinesList[0];
            song.CreateMachine("Neo Filter", null, name, null, null, null, -1, 0.85f, 0.75f);
            var fx = song.MachinesList.Last(x => x.Name == name);
            song.DisconnectMachines(source.Outputs.First(c => c.Destination == master));
            song.ConnectMachines(source, fx, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            song.ConnectMachines(fx, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            var globals = fx.ParameterGroupsList[1].ParametersList;
            globals.First(p => p.Name == "Resonance").SetValue(0, 500);
            globals.First(p => p.Name == "Drive").SetValue(0, 120);
            globals.First(p => p.Name == "LFO sync").SetValue(0, 9);
            globals.First(p => p.Name == "LFO depth").SetValue(0, 150);
            globals.First(p => p.Name == "Env amount").SetValue(0, 900);
            globals.First(p => p.Name == "Env sensitivity").SetValue(0, 240);
            return fx;
        }

        static void Arrange(MachineCore m, IPattern p)
        {
            var song = Core.SongCore;
            song.AddSequence(m, song.Sequences.Count);
            song.Sequences.Last().SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, p));
            Core.Looping = true;
            song.LoopStart = 0;
            song.LoopEnd = p.Length;
            song.SongEnd = p.Length;
        }

        static bool Multithreading
        {
            get => Core.EngineSettings.Multithreading;
            set => Core.EngineSettings.Multithreading = value;
        }

        // ------------------------------------------------------------------ an empty song

        [AvaloniaTest]
        public void AnEmptySongAllocatesNothingPerBuffer([Values(0, 1, 2)] int algorithm, [Values] bool multithreading)
        {
            NewSong();
            bool was = Multithreading;
            Multithreading = multithreading;
            try
            {
                using var audio = new AudioThread(algorithm);
                AssertNoAllocation(Steady(audio), "an empty song, stopped");
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                AssertNoAllocation(Steady(audio), "an empty song, playing");
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                Multithreading = was;
                NewSong();
            }
        }

        // ------------------------------------------------------------------ the Modern Pattern Editor

        /// <summary>
        /// A Neo Poly's pattern in the Modern Pattern Editor, open in the Pattern tab: a note on every
        /// row of track 0's Note column, Uni detune interpolated over every row, and notes on the
        /// MIDI note lanes (as the Session View moves a clip's notes there).
        /// </summary>
        static (MachineCore Machine, IPattern Pattern, MainWindow Window) MpePattern(string name)
        {
            var m = AddPoly(name);
            m.CreatePattern("00", 16);
            var pattern = m.Patterns.First(p => p.Name == "00");
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Pump();
            PatternView.Edit(pattern);
            Pump();
            dynamic view = PatternView.Page.EditorControl;
            ((Control)view.TrackerGrid.Body).Focus();
            Pump();

            // The cursor starts on track 0's Note column (the notes come first, ux-overhaul.md P3c).
            var keys = new[] { PhysicalKey.Z, PhysicalKey.C, PhysicalKey.B, PhysicalKey.N };
            for (int i = 0; i < 16; i++) Key(window, keys[i % 4]);
            EditorNav.GoTo((object)view, "Uni detune", Pump);                   // its high digit, row 0
            Key(window, PhysicalKey.Digit0);
            for (int i = 0; i < 14; i++) Key(window, PhysicalKey.ArrowDown);
            Key(window, PhysicalKey.ArrowRight);
            Key(window, PhysicalKey.Digit3);
            Key(window, PhysicalKey.PageUp);
            for (int i = 0; i < 15; i++) Key(window, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
            Key(window, PhysicalKey.I, RawInputModifiers.Control);

            // (None ends on the last tick: the editor never sends a lane note's end there, and keeps
            // it queued for good, one more on every loop: upstream's, left as it is.)
            var lanes = TrackerNotes.Pack(Enumerable.Range(0, 7).Select(i => new TrackerNote(i * 2 * TB, TB, 48 + i, 100)));
            Assert.That(Core.SessionLayout.ImportToEditor(pattern, lanes), Is.True, "notes on the MIDI note lanes");
            Pump();

            var columns = pattern.PatternEditorMachineEvents.ToList();
            Assert.That(columns.Single(c => c.Parameter.Name == "Note").Events.Count(), Is.EqualTo(16), "a note on every row");
            Assert.That(columns.Single(c => c.Parameter.Name == "Uni detune").Events.Count(), Is.EqualTo(16), "automation on every row");
            Assert.That(columns.Where(c => c.Parameter.Name == "MIDI Note").Sum(c => c.Events.Count()), Is.GreaterThanOrEqualTo(7), "notes on the MIDI lanes");
            Arrange(m, pattern);
            return (m, pattern, window);
        }

        // One window and pattern for every work algorithm: a studio window closed in a test leaves
        // some of its views listening to the engine, slowing the tests after it a little each.
        [AvaloniaTest]
        public void AModernPatternEditorPatternPlayingNotesAndAutomationAllocatesNothing()
        {
            NewSong();
            var (m, pattern, window) = MpePattern("Hygiene MPE");
            var ensemble = InsertEnsemble(m, "Hygiene Ensemble");
            var eq = InsertGraphicEq(ensemble, "Hygiene EQ");
            var delay = InsertDelay(eq, "Hygiene Delay");
            var multiband = InsertMultiband(delay, "Hygiene Multiband");
            var scope = InsertScope(multiband, "Hygiene Scope");
            try
            {
                var detune = m.ParameterGroupsList[1].ParametersList.First(p => p.Name == "Uni detune");
                var display = scope.ParameterGroupsList[1].ParametersList.First(p => p.Name == "Display");
                var depth = ensemble.ParameterGroupsList[1].ParametersList.First(p => p.Name == "Depth");
                var slider = eq.ParameterGroupsList[1].ParametersList.First(p => p.Name == "G 1 kHz");
                var time = delay.ParameterGroupsList[1].ParametersList.First(p => p.Name == "Time");
                var crossover = multiband.ParameterGroupsList[1].ParametersList.First(p => p.Name == "Crossover 2");
                int n = 0;
                foreach (int algorithm in new[] { 0, 1, 2 })
                {
                    using var audio = new AudioThread(algorithm);
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    var heard = new HashSet<int>(1024);
                    var cost = Steady(audio, perBuffer: () =>
                    {
                        heard.Add(detune.GetValue(0));
                        depth.SetValue(1 << 16, 200 + (n % 8) * 100);   // as automation sets it, not recorded
                        slider.SetValue(1 << 16, 120 + (n % 12) * 10);
                        time.SetValue(1 << 16, (n % 5) * 600 + 3000);     // 5 ms … 110 ms: glides, and crossfades between the ends
                        crossover.SetValue(1 << 16, 5000 + (n % 9) * 100);     // 630 Hz … 1.6 kHz: the FIRs designed again
                        if (n++ % 50 == 0) display.SetValue(1 << 16, (n / 50) % 5);   // the scope's sweep starts again
                    });
                    AssertNoAllocation(cost, $"a pattern editor pattern playing through Neo Ensemble, a graphic Neo EQ, Neo Delay, Neo Multiband and Neo Scope (algorithm {algorithm})");
                    Assert.That(heard.Count, Is.GreaterThan(8), $"the automation reached the machine (algorithm {algorithm})");
                    Assert.That(m.IsActive, Is.True, $"the notes sound (algorithm {algorithm})");
                    Assert.That(ensemble.IsActive, Is.True, $"and pass through Neo Ensemble (algorithm {algorithm})");
                    Assert.That(eq.IsActive, Is.True, $"and the graphic Neo EQ (algorithm {algorithm})");
                    Assert.That(delay.IsActive, Is.True, $"and Neo Delay (algorithm {algorithm})");
                    Assert.That(multiband.IsActive, Is.True, $"and Neo Multiband (algorithm {algorithm})");
                    Assert.That(scope.IsActive, Is.True, $"and Neo Scope (algorithm {algorithm})");
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                Core.Modified = false;      // else closing asks to save, and the window stays open for the rest of the run
                window.Close();
                Pump();
                NewSong();
            }
        }

        // ------------------------------------------------------------------ listeners

        [AvaloniaTest]
        public void AutomationWithListenersAllocatesNothingAndTheListenersSeeTheFinalValues()
        {
            NewSong();
            var m = AddPoly("Hygiene Listened");
            m.CreatePattern("00", 16);
            var pattern = (PatternCore)m.Patterns.First();
            var cutoff = m.ParameterGroupsList[1].ParametersList.First(p => p.Name == "F1 cutoff");
            var column = SessionLayout.ColumnFor(pattern, new ColumnKey(1, cutoff.IndexInGroup, 0), create: true);
            column.SetEvents(Enumerable.Range(0, 16).Select(t => new PatternEvent(t * TB, cutoff.MinValue + (t * 37) % (cutoff.MaxValue - cutoff.MinValue), 0)).ToList(), true);
            Arrange(m, pattern);

            // The parameter window: a row per parameter, each listening to its parameter.
            Core.UiHost.OpenParameterWindow(m, null);
            var window = StudioMachineUiHost.ParameterWindowOf(m);
            Assert.That(window, Is.Not.Null);
            Pump();
            var row = ((ParameterWindowView)StudioMachineUiHost.ContentOf(window)!).Rows.Single(r => r.ParameterName == "F1 cutoff");
            var told = new List<int>();
            Action<IParameter, int> listener = (p, t) => told.Add(p.GetValue(t));
            cutoff.SubscribeEvents(0, listener, null);

            // The master volume (the engine's own listener sets ReBuzzCore.MasterVolume from it), as a
            // pattern editor or a mapped controller sets it: a new value before every buffer.
            var volume = Core.SongCore.MachinesList[0].ParameterGroupsList[1].ParametersList[0];
            int n = 0;
            Action setVolume = () => volume.SetValue(1 << 16, 0x1000 + (n++ % 64) * 16);
            try
            {
                using var audio = new AudioThread(DeviceAlgorithm);
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                audio.Read(800, setVolume);
                Core.UiEvents.Drain();
                told.Clear();
                var cost = audio.Read(400, setVolume);
                AssertNoAllocation(cost, "automation with listeners (a parameter window's row, the master volume's, one of the test's)");

                // Delivered on the UI thread (here at once; in the studio its timer does it).
                Core.UiEvents.Drain();
                Pump();
                Assert.That(told, Is.Not.Empty, "the listener was told");
                Assert.That(told.Last(), Is.EqualTo(cutoff.GetValue(0)), "and saw the final value");
                Assert.That(row.ValueText, Is.EqualTo(cutoff.DescribeValue(cutoff.GetValue(0))), "the window's row shows it");
                Assert.That(Core.MasterVolume, Is.EqualTo((volume.MaxValue - volume.GetValue(0)) / (double)volume.MaxValue), "the master volume follows its parameter");
            }
            finally
            {
                cutoff.UnsubscribeEvents(0, listener, null);
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                window.Close();
                Core.UiHost.ReleaseMachine(m);
                Pump();
                volume.SetValue(0, 0);
                NewSong();
            }
        }

        [AvaloniaTest]
        public void TheStudiosSignalDeliversEventsRaisedOnTheAudioThreadOnItsTimer()
        {
            int ran = 0;
            var signal = new AvaloniaUiDispatcher().CreateSignal(() => ran++);
            try
            {
                var thread = new Thread(() => { signal.Request(); signal.Request(); });
                thread.Start();
                thread.Join();
                Assert.That(ran, Is.EqualTo(0), "the audio thread only sets a flag");
                var until = DateTime.UtcNow.AddSeconds(5);
                while (ran == 0 && DateTime.UtcNow < until)
                {
                    Thread.Sleep(5);
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.That(ran, Is.EqualTo(1), "the timer ran it once for both requests");
            }
            finally
            {
                (signal as IDisposable)?.Dispose();         // its timer: not left running for the tests after
            }
        }

        // ------------------------------------------------------------------ the first play

        [AvaloniaTest]
        public void AfterTheWarmUpTheFirstPlayAndStopCompileNothing([Values] bool multithreading)
        {
            _ = Studio.Host;                                    // the engine starts its warm-up
            Assert.That(AudioWarmUp.Wait(), Is.True, "the warm-up finishes");
            Assert.That(AudioWarmUp.Methods, Is.GreaterThan(1000), "it compiled the audio path");
            NewSong();
            bool was = Multithreading;
            Multithreading = multithreading;
            try
            {
                var m = AddPoly("Hygiene First");
                var first = InsertEnsemble(m, "Hygiene First Ensemble");   // Neo effects after it, created just now too
                var firstDelay = InsertDelay(first, "Hygiene First Delay");
                var firstMultiband = InsertMultiband(firstDelay, "Hygiene First Multiband");
                var firstScope = InsertScope(firstMultiband, "Hygiene First Scope");
                InsertFilter(firstScope, "Hygiene First Filter");
                m.CreatePattern("00", 16);
                var p = (PatternCore)m.Patterns.First();
                Core.SessionLayout.EditNotes(p, null, Enumerable.Range(0, 16).Select(i => new ClipNote(i * TB, TB / 2, 60 + i % 5, 100)).ToArray());
                Arrange(m, p);
                Assert.That(AudioWarmUp.Wait(), Is.True, "the machine's assembly compiled");

                using var audio = new AudioThread(DeviceAlgorithm);
                audio.Read(100);                                // the device running, stopped
                int warmBefore = AudioWarmUp.Methods;
                using var jit = new JitListener();
                Thread.Sleep(100);
                jit.On = true;
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                var play = audio.Read(200);
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                var stop = audio.Read(50);
                Thread.Sleep(300);
                jit.On = false;
                TestContext.Out.WriteLine($"[notice] first play compiled {play.Methods} methods, its stop {stop.Methods} (warm-up: {AudioWarmUp.Methods} in {AudioWarmUp.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms; {AudioWarmUp.Methods - warmBefore} during the play)");
                // A failure names what compiled on the audio thread (CI run 41866682998, attempt 1: "its stop 1", and the
                // 60 methods it listed were all the tiering thread's, so the one compiled on the audio thread wasn't shown).
                string compiled = jit.Report(audio.OsThreadId);
                Assert.That(play.Methods, Is.EqualTo(0), "the first play compiles nothing on the audio or render threads; compiled meanwhile:" + compiled);
                Assert.That(stop.Methods, Is.EqualTo(0), "nor does its stop:" + compiled);
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                Multithreading = was;
                NewSong();
            }
        }

        /// <summary>
        /// Every Neo machine, all just created, through the transport's first play and stop: 0 bytes on the audio thread.
        /// The first play allocated about 340 KB there: Neo Wave a closure every voice block (Tables.Get), Neo Comp an
        /// enumerator every buffer (the multi-I/O paths' ElementAt over the channel sets), Neo FM6 its DX7 tables and its
        /// voices' carrier gains (static, made by the first note), the engine its pattern lists' first growth and the
        /// Playing event posted as a closure (216 bytes at every start, now on the UI event queue). The device's first
        /// buffers before the play, each machine's first Work since it was made, set it up for the rate (about 3 MB here:
        /// Neo Verb 1.5 MB, Analyzer 950 KB): reported, not asserted.
        /// </summary>
        [AvaloniaTest]
        public void TheFirstPlayOfEveryNeoMachineAllocatesNothing()
        {
            _ = Studio.Host;
            Assert.That(AudioWarmUp.Wait(), Is.True, "the warm-up finishes");
            NewSong();
            bool was = Multithreading;
            Multithreading = false;                             // every machine's work on the audio thread, which the window reads
            var song = Core.SongCore;
            var master = song.MachinesList[0];
            MachineCore Make(string gear, string name, float x, float y)
            {
                song.CreateMachine(gear, null, name, null, null, null, -1, x, y);
                return song.MachinesList.Last(m => m.Name == name);
            }
            void Chain(params MachineCore[] ms)
            {
                for (int i = 0; i < ms.Length; i++)
                    song.ConnectMachines(ms[i], i + 1 < ms.Length ? ms[i + 1] : master, new[] { 0 }, new[] { 0 }, 0x2000, 0x4000);
            }
            int length = 0;
            void Play(MachineCore m, params int[] keys)
            {
                m.CreatePattern("00", 16);
                var p = (PatternCore)m.Patterns.First();
                Core.SessionLayout.EditNotes(p, null, Enumerable.Range(0, 16).Select(i => new ClipNote(i * TB, TB / 2, keys[i % keys.Length], 100)).ToArray());
                song.AddSequence(m, song.Sequences.Count);
                song.Sequences.Last().SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, p));
                length = Math.Max(length, p.Length);
            }
            try
            {
                var poly = Make("Neo Poly", "First Poly", -0.8f, -0.6f);
                var wave = Make("Neo Wave", "First Wave", -0.8f, -0.2f);
                var fm6 = Make("Neo FM6", "First FM6", -0.8f, 0.2f);
                var drums = Make("Neo Drums", "First Drums", -0.8f, 0.6f);
                var sampler = Make("Neo Sampler", "First Sampler", -0.8f, 0.9f);
                Chain(poly, Make("Neo Drive", "First Drive", -0.4f, -0.6f), Make("Neo Comp", "First Comp", 0f, -0.6f), Make("Neo Verb", "First Verb", 0.4f, -0.6f));
                Chain(wave, Make("Neo EQ", "First EQ", -0.4f, -0.2f), Make("Neo Utility", "First Utility", 0f, -0.2f), Make("Neo Limiter", "First Limiter", 0.4f, -0.2f));
                Chain(fm6, Make("Neo Ensemble", "First Ensemble", -0.4f, 0.2f), Make("Neo Delay", "First Delay", -0.1f, 0.2f), Make("Neo Multiband", "First Multiband", 0.2f, 0.2f),
                      Make("Neo Filter", "First Filter", 0.4f, 0.2f), Make("Neo Analyzer", "First Analyzer", 0.6f, 0.2f), Make("Neo Scope", "First Scope", 0.8f, 0.2f));
                Chain(drums);
                Chain(sampler);
                Play(poly, 48, 55, 60, 63);
                Play(wave, 57, 60, 64, 69, 72);
                Play(fm6, 60, 67, 72, 76);
                Play(drums, 36, 42, 38, 42, 46, 39, 49, 51);
                Play(sampler, 60, 62, 64, 65, 67);
                Core.Looping = true;
                song.LoopStart = 0;
                song.LoopEnd = length;
                song.SongEnd = length;
                Assert.That(AudioWarmUp.Wait(), Is.True, "the machines' assemblies compiled");

                using var audio = new AudioThread(DeviceAlgorithm);
                var stopped = audio.Read(100, describe: true);  // the device running, stopped: each machine's first Work
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                var play = audio.Read(400, describe: true);     // the first play: every machine's first notes
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                var stop = audio.Read(50, describe: true);
                TestContext.Out.WriteLine($"[notice] every Neo machine's first buffers, stopped: {stopped.Bytes} bytes\n" + string.Join("\n", stopped.Log));
                Assert.Multiple(() =>
                {
                    AssertNoAllocation(play, "the first play of every Neo machine");
                    AssertNoAllocation(stop, "its stop");
                });
                foreach (var m in new[] { poly, wave, fm6, drums, sampler })
                    Assert.That(m.IsActive || m.Outputs.Count > 0, Is.True, $"{m.Name} is in the song");
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                Multithreading = was;
                NewSong();
            }
        }

        // ------------------------------------------------------------------ Neo Sampler

        static string SineWav(double hz, string name)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N") + ".wav");
            int n = 48000;
            using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
            w.Write("RIFF"u8.ToArray()); w.Write(36 + n * 4); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(48000); w.Write(48000 * 4); w.Write((short)4); w.Write((short)32);
            w.Write("data"u8.ToArray()); w.Write(n * 4);
            for (int i = 0; i < n; i++) w.Write((float)(0.4 * Math.Sin(2 * Math.PI * hz * i / 48000)));
            return path;
        }

        [AvaloniaTest]
        public void ANeoSamplerPlayingTheSongsWavesAllocatesNothing()
        {
            NewSong();
            var song = Core.SongCore;
            string a = SineWav(220, "hygiene-a"), b = SineWav(330, "hygiene-b");
            song.Wavetable.LoadWave(0, a, "Hygiene wave", false);
            song.CreateMachine("Neo Sampler", null, "Hygiene Sampler", null, null, null, -1, 0.3f, 0.3f);
            var m = song.MachinesList.Last(x => x.Name == "Hygiene Sampler");
            song.ConnectMachines(m, song.MachinesList[0], new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            var globals = m.ParameterGroupsList[1].ParametersList;
            void Set(string name, int raw) => globals.First(p => p.Name == name).SetValue(0, raw);
            Set("Z1 key high", 59);
            Set("Z1 loop", 2);                                   // Forward, crossfaded
            Set("Z1 loop start", 20000);
            Set("Z1 choke", 1);
            Set("Z2 wave", 221);                                 // the built-in strings, their own loop
            Set("Z2 key low", 60);
            Set("Z2 choke", 1);
            Set("Filter", 1);
            Set("Resonance", 400);
            m.CreatePattern("00", 16);
            var pattern = (PatternCore)m.Patterns.First();
            Core.SessionLayout.EditNotes(pattern, null, Enumerable.Range(0, 16).Select(i => new ClipNote(i * TB, TB / 2, i % 2 == 0 ? 52 + i % 5 : 64 + i % 3, 100)).ToArray());
            Arrange(m, pattern);
            var tune = globals.First(p => p.Name == "Z1 tune");
            var busy = m.ManagedMachine.GetType().GetProperty("Busy")!;
            try
            {
                // Busy reads the song's waves in on this, the song's thread; the built-ins are made in the background.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (busy.GetValue(m.ManagedMachine) is true && clock.ElapsedMilliseconds < 20000) Thread.Sleep(20);
                Assert.That(busy.GetValue(m.ManagedMachine), Is.False, "the waves read in and the built-ins made");
                int n = 0;
                foreach (int algorithm in new[] { 0, 1, 2 })
                {
                    using var audio = new AudioThread(algorithm);
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    var cost = Steady(audio, perBuffer: () =>
                    {
                        tune.SetValue(1 << 16, 44 + n % 9);
                        // Now and then the slot gets another wave: the machine copies it in on the audio thread.
                        if (++n % 300 == 150) song.Wavetable.LoadWave(0, n % 600 == 150 ? b : a, "Hygiene wave", false);
                    });
                    AssertNoAllocation(cost, $"a Neo Sampler playing the song's wave and a built-in one (algorithm {algorithm})");
                    Assert.That(m.IsActive, Is.True, $"the notes sound (algorithm {algorithm})");
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        // ------------------------------------------------------------------ Neo Drums

        [AvaloniaTest]
        public void NeoDrumsPlayingEveryVoiceAllocatesNothing()
        {
            NewSong();
            var song = Core.SongCore;
            song.CreateMachine("Neo Drums", null, "Hygiene Drums", null, null, null, -1, 0.3f, 0.3f);
            var m = song.MachinesList.Last(x => x.Name == "Hygiene Drums");
            song.ConnectMachines(m, song.MachinesList[0], new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            var globals = m.ParameterGroupsList[1].ParametersList;
            m.CreatePattern("00", 16);
            var pattern = (PatternCore)m.Patterns.First();
            // Every voice's key (General MIDI from C2), at rising velocities: the closed hats choke the open one.
            int[] keys = { 36, 42, 38, 42, 36, 46, 39, 42, 37, 44, 43, 47, 50, 56, 49, 51 };
            Core.SessionLayout.EditNotes(pattern, null, Enumerable.Range(0, 16).Select(i => new ClipNote(i * TB, TB / 2, keys[i], 40 + i * 5)).ToArray());
            Arrange(m, pattern);
            var tune = globals.First(p => p.Name == "Kick tune");
            var level = globals.First(p => p.Name == "Snare level");
            var bd = m.ParameterGroupsList[2].ParametersList.First(p => p.Name == "BD");
            try
            {
                int n = 0;
                foreach (int algorithm in new[] { 0, 1, 2 })
                {
                    using var audio = new AudioThread(algorithm);
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    var cost = Steady(audio, perBuffer: () =>
                    {
                        tune.SetValue(1 << 16, 300 + n % 200);
                        level.SetValue(1 << 16, 500 + n % 100);
                        if (++n % 7 == 0) bd.SetValue(0, 60 + n % 60);   // a kick from the hit column
                    });
                    AssertNoAllocation(cost, $"a Neo Drums playing every voice and hits from its BD column (algorithm {algorithm})");
                    Assert.That(m.IsActive, Is.True, $"the hits sound (algorithm {algorithm})");
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        // ------------------------------------------------------------------ Neo Filter

        [AvaloniaTest]
        public void ANeoFilterSweepingAPatternAllocatesNothing()
        {
            NewSong();
            var m = AddPoly("Hygiene Filter Poly");
            m.CreatePattern("00", 16);
            var pattern = (PatternCore)m.Patterns.First();
            Core.SessionLayout.EditNotes(pattern, null, Enumerable.Range(0, 16).Select(i => new ClipNote(i * TB, TB / 2, 48 + (i * 5) % 12, 60 + i * 4)).ToArray());
            Arrange(m, pattern);
            var filter = InsertFilter(m, "Hygiene Filter");
            var globals = filter.ParameterGroupsList[1].ParametersList;
            var cutoff = globals.First(p => p.Name == "Cutoff");
            var type = globals.First(p => p.Name == "Type");
            try
            {
                int n = 0;
                foreach (int algorithm in new[] { 0, 1, 2 })
                {
                    using var audio = new AudioThread(algorithm);
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    var cost = Steady(audio, perBuffer: () =>
                    {
                        cutoff.SetValue(1 << 16, 3000 + (n % 40) * 100);           // as automation sets it, not recorded
                        if (++n % 100 == 0) type.SetValue(1 << 16, (n / 100) % 7);  // a type change: the fade, the switch
                    });
                    AssertNoAllocation(cost, $"a pattern through a Neo Filter sweeping (algorithm {algorithm})");
                    Assert.That(filter.IsActive, Is.True, $"the filter sounds (algorithm {algorithm})");
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        // ------------------------------------------------------------------ Neo Clipper and Neo Limiter v2

        /// <summary>Inserts <paramref name="gear"/> between <paramref name="source"/> and whatever it fed.</summary>
        static MachineCore InsertAfter(MachineCore source, string gear, string name)
        {
            var song = Core.SongCore;
            var to = source.Outputs.First().Destination;
            song.CreateMachine(gear, null, name, null, null, null, -1, 0.85f, 0.75f);
            var fx = song.MachinesList.Last(x => x.Name == name);
            song.DisconnectMachines(source.Outputs.First(c => c.Destination == to));
            song.ConnectMachines(source, fx, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            song.ConnectMachines(fx, to, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            return fx;
        }

        [AvaloniaTest]
        public void ANeoTransientAndANeoLowEndWithEveryControlAllocateNothing()
        {
            // mastering-tools.md §5.4, §5.5: a pattern through Neo Transient (mid/side, Attack and Sustain moved every buffer,
            // Impact mode switched now and then) and Neo Low End (every control working, its punch moved every buffer, its
            // phase mode switched now and then: the engine's latency moves through its crossfade).
            NewSong();
            var m = AddPoly("Hygiene Shaper Poly");
            m.CreatePattern("00", 16);
            var pattern = (PatternCore)m.Patterns.First();
            Core.SessionLayout.EditNotes(pattern, null, Enumerable.Range(0, 16).Select(i => new ClipNote(i * TB, TB / 2, 36 + (i * 5) % 24, 100)).ToArray());
            Arrange(m, pattern);
            var transient = InsertAfter(m, "Neo Transient", "Hygiene Transient");
            var lowEnd = InsertAfter(transient, "Neo Low End", "Hygiene Low End");
            var tg = transient.ParameterGroupsList[1].ParametersList;
            tg.First(p => p.Name == "Stereo").SetValue(0, 1);
            tg.First(p => p.Name == "Impact 1").SetValue(0, 800);
            tg.First(p => p.Name == "Impact 3").SetValue(0, 200);
            var attack = tg.First(p => p.Name == "Attack");
            var mode = tg.First(p => p.Name == "Mode");
            var lg = lowEnd.ParameterGroupsList[1].ParametersList;
            lg.First(p => p.Name == "Sustain").SetValue(0, 100);
            lg.First(p => p.Name == "Contrast").SetValue(0, 800);
            lg.First(p => p.Name == "Balance").SetValue(0, 500);
            lg.First(p => p.Name == "Peak control").SetValue(0, 1);
            lg.First(p => p.Name == "Mono").SetValue(0, 1);
            var punch = lg.First(p => p.Name == "Punch");
            var phase = lg.First(p => p.Name == "Phase");
            try
            {
                int n = 0;
                foreach (int algorithm in new[] { 0, 1, 2 })
                {
                    using var audio = new AudioThread(algorithm);
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    var cost = Steady(audio, perBuffer: () =>
                    {
                        attack.SetValue(1 << 16, 120 + (n % 40) * 3);
                        punch.SetValue(1 << 16, 130 + (n % 30) * 3);
                        if (++n % 120 == 0) mode.SetValue(1 << 16, (n / 120) % 2);
                        if (n % 200 == 0) phase.SetValue(1 << 16, (n / 200) % 2);
                    });
                    AssertNoAllocation(cost, $"a pattern through a Neo Transient and a Neo Low End with every control (algorithm {algorithm})");
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        [AvaloniaTest]
        public void ANeoTapeANeoDeEsserAndANeoGateWithEveryControlAllocateNothing()
        {
            // mastering-tools.md §5.11 - §5.13: Neo Tape (speed and quality switched, wow, flutter, hiss, bias), Neo De-esser (every action,
            // lookahead moved) and Neo Gate (detection and sidechain switched, a lookahead moved) over a pattern.
            NewSong();
            var m = AddPoly("Hygiene Dynamics Poly");
            m.CreatePattern("00", 16);
            var pattern = (PatternCore)m.Patterns.First();
            Core.SessionLayout.EditNotes(pattern, null, Enumerable.Range(0, 16).Select(i => new ClipNote(i * TB, TB / 2, 36 + (i * 5) % 24, 100)).ToArray());
            Arrange(m, pattern);
            var tape = InsertAfter(m, "Neo Tape", "Hygiene Tape");
            var deesser = InsertAfter(tape, "Neo De-esser", "Hygiene De-esser");
            var gate = InsertAfter(deesser, "Neo Gate", "Hygiene Gate");
            var tg = tape.ParameterGroupsList.SelectMany(g => g.ParametersList).ToList();
            tg.First(p => p.Name == "Saturation").SetValue(0, 200);
            tg.First(p => p.Name == "Hiss").SetValue(0, 100);
            tg.First(p => p.Name == "Wow").SetValue(0, 120);
            tg.First(p => p.Name == "Flutter").SetValue(0, 60);
            var speed = tg.First(p => p.Name == "Speed");
            var quality = tg.First(p => p.Name == "Quality");
            var bias = tg.First(p => p.Name == "Bias");
            var dg = deesser.ParameterGroupsList.SelectMany(g => g.ParametersList).ToList();
            var action = dg.First(p => p.Name == "Action");
            var detector = dg.First(p => p.Name == "Detector");
            var dLook = dg.First(p => p.Name == "Lookahead");
            var gg = gate.ParameterGroupsList.SelectMany(g => g.ParametersList).ToList();
            var detection = gg.First(p => p.Name == "Detection");
            var gLook = gg.First(p => p.Name == "Lookahead");
            try
            {
                int n = 0;
                foreach (int algorithm in new[] { 0, 1, 2 })
                {
                    using var audio = new AudioThread(algorithm);
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    var cost = Steady(audio, perBuffer: () =>
                    {
                        n++;
                        if (n % 90 == 0) speed.SetValue(1 << 16, (n / 90) % 3);
                        if (n % 110 == 0) quality.SetValue(1 << 16, (n / 110) % 2);
                        bias.SetValue(1 << 16, 100 + n % 200);
                        if (n % 70 == 0) action.SetValue(1 << 16, (n / 70) % 4);
                        if (n % 150 == 0) detector.SetValue(1 << 16, (n / 150) % 2);
                        dLook.SetValue(1 << 16, n % 50);
                        if (n % 120 == 0) detection.SetValue(1 << 16, (n / 120) % 2);
                        gLook.SetValue(1 << 16, (n * 3) % 250);
                    });
                    AssertNoAllocation(cost, $"a pattern through a Neo Tape, a Neo De-esser and a Neo Gate with every control (algorithm {algorithm})");
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        [AvaloniaTest]
        public void ANeoImagerANeoExciterANeoDitherAndANeoStripWithEveryControlAllocateNothing()
        {
            // mastering-tools.md §5.7 - §5.9: a pattern through Neo Imager (every band shaped, Recover on, a width moved every
            // buffer), Neo Exciter (its curve switched now and then, mid/side, auto gain) and Neo Dither (shaping switched).
            NewSong();
            var m = AddPoly("Hygiene Finisher Poly");
            m.CreatePattern("00", 16);
            var pattern = (PatternCore)m.Patterns.First();
            Core.SessionLayout.EditNotes(pattern, null, Enumerable.Range(0, 16).Select(i => new ClipNote(i * TB, TB / 2, 36 + (i * 5) % 24, 100)).ToArray());
            Arrange(m, pattern);
            var imager = InsertAfter(m, "Neo Imager", "Hygiene Imager");
            var exciter = InsertAfter(imager, "Neo Exciter", "Hygiene Exciter");
            var dither = InsertAfter(exciter, "Neo Dither", "Hygiene Dither");
            var strip = InsertAfter(dither, "Neo Strip", "Hygiene Strip");
            var ig = imager.ParameterGroupsList.SelectMany(g => g.ParametersList).ToList();
            ig.First(p => p.Name == "Width 1").SetValue(0, 0);
            ig.First(p => p.Name == "Width 3").SetValue(0, 160);
            ig.First(p => p.Name == "Stereoize 2").SetValue(0, 400);
            ig.First(p => p.Name == "Recover sides").SetValue(0, 1);
            var width = ig.First(p => p.Name == "Width 4");
            var eg = exciter.ParameterGroupsList.SelectMany(g => g.ParametersList).ToList();
            eg.First(p => p.Name == "Amount 3").SetValue(0, 400);
            eg.First(p => p.Name == "Auto gain").SetValue(0, 1);
            eg.First(p => p.Name == "Stereo").SetValue(0, 1);
            var curve = eg.First(p => p.Name == "Mode");
            var dg = dither.ParameterGroupsList.SelectMany(g => g.ParametersList).ToList();
            var shaping = dg.First(p => p.Name == "Shaping");
            var sg = strip.ParameterGroupsList.SelectMany(g => g.ParametersList).ToList();
            foreach (var stageName in new[] { "Gate on", "EQ on", "Comp on", "Drive on", "Clip on", "Limit on" }) sg.First(p => p.Name == stageName).SetValue(0, 1);
            sg.First(p => p.Name == "Low mid gain").SetValue(0, 280);
            var lmGain = sg.First(p => p.Name == "Low mid gain");
            try
            {
                int n = 0;
                foreach (int algorithm in new[] { 0, 1, 2 })
                {
                    using var audio = new AudioThread(algorithm);
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    var cost = Steady(audio, perBuffer: () =>
                    {
                        width.SetValue(1 << 16, 20 + (n % 60) * 3);
                        if (++n % 100 == 0) curve.SetValue(1 << 16, (n / 100) % 5);
                        if (n % 130 == 0) shaping.SetValue(1 << 16, (n / 130) % 3);
                        lmGain.SetValue(1 << 16, 200 + (n % 80));
                    });
                    AssertNoAllocation(cost, $"a pattern through a Neo Imager, a Neo Exciter, a Neo Dither and a Neo Strip with every control (algorithm {algorithm})");
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        [AvaloniaTest]
        public void ANeoClipperAndANeoLimiterWithEveryStageAllocateNothing()
        {
            // mastering-tools.md §5.1, §5.2: a pattern through Neo Clipper (three bands, mid/side, its drive moved every buffer,
            // Delta switched now and then) and Neo Limiter with every v2 stage (the soft clip switched now and then: its
            // latency moves through the engine's crossfade).
            NewSong();
            var m = AddPoly("Hygiene Clip Poly");
            m.CreatePattern("00", 16);
            var pattern = (PatternCore)m.Patterns.First();
            Core.SessionLayout.EditNotes(pattern, null, Enumerable.Range(0, 16).Select(i => new ClipNote(i * TB, TB / 2, 40 + (i * 7) % 24, 100)).ToArray());
            Arrange(m, pattern);
            var clipper = InsertAfter(m, "Neo Clipper", "Hygiene Clipper");
            var limiter = InsertAfter(clipper, "Neo Limiter", "Hygiene Limiter");
            var cg = clipper.ParameterGroupsList[1].ParametersList;
            cg.First(p => p.Name == "Bands").SetValue(0, 2);
            cg.First(p => p.Name == "Stereo").SetValue(0, 1);
            var drive = cg.First(p => p.Name == "Drive");
            var delta = cg.First(p => p.Name == "Delta");
            var lg = limiter.ParameterGroupsList[1].ParametersList;
            lg.First(p => p.Name == "Input gain").SetValue(0, 220);
            lg.First(p => p.Name == "Upward").SetValue(0, 30);
            lg.First(p => p.Name == "Transients").SetValue(0, 30);
            lg.First(p => p.Name == "Slow stage").SetValue(0, 500);
            lg.First(p => p.Name == "Transient unlink").SetValue(0, 500);
            lg.First(p => p.Name == "Unity listen").SetValue(0, 1);
            var clip = lg.First(p => p.Name == "Soft clip");
            try
            {
                int n = 0;
                foreach (int algorithm in new[] { 0, 1, 2 })
                {
                    using var audio = new AudioThread(algorithm);
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    var cost = Steady(audio, perBuffer: () =>
                    {
                        drive.SetValue(1 << 16, 180 + (n % 40) * 3);
                        if (++n % 100 == 0) delta.SetValue(1 << 16, (n / 100) % 2);
                        if (n % 150 == 0) clip.SetValue(1 << 16, (n / 150) % 4);
                    });
                    AssertNoAllocation(cost, $"a pattern through a Neo Clipper and a Neo Limiter with every stage (algorithm {algorithm})");
                    Assert.That(limiter.IsActive, Is.True, $"the limiter sounds (algorithm {algorithm})");
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        [AvaloniaTest]
        public void ANeoEqWithDynamicBandsOnEveryChannelAllocatesNothing()
        {
            // mastering-tools.md §5.3: a pattern through Neo EQ v2, bands dynamic down and up on Stereo, Mid and Side, the
            // shapes, a dynamic band's frequency moved every buffer and Delta switched now and then.
            NewSong();
            var m = AddPoly("Hygiene EQ Poly");
            m.CreatePattern("00", 16);
            var pattern = (PatternCore)m.Patterns.First();
            Core.SessionLayout.EditNotes(pattern, null, Enumerable.Range(0, 16).Select(i => new ClipNote(i * TB, TB / 2, 40 + (i * 5) % 24, 100)).ToArray());
            Arrange(m, pattern);
            var eq = InsertAfter(m, "Neo EQ", "Hygiene EQ v2");
            var g = eq.ParameterGroupsList[1].ParametersList;
            IParameter P(string name) => g.First(p => p.Name == name);
            P("B2 dynamic").SetValue(0, 1);
            P("B2 threshold").SetValue(0, 150);
            P("B2 shape").SetValue(0, 3);
            P("B3 dynamic").SetValue(0, 2);
            P("B3 channel").SetValue(0, 3);
            P("B3 threshold").SetValue(0, 100);
            P("B5 dynamic").SetValue(0, 1);
            P("B5 channel").SetValue(0, 4);
            P("B5 threshold").SetValue(0, 100);
            P("B6 shape").SetValue(0, 1);
            P("B6 gain").SetValue(0, 330);
            P("B7 shape").SetValue(0, 2);
            var freq = P("B3 freq");
            var delta = P("Delta");
            try
            {
                int n = 0;
                foreach (int algorithm in new[] { 0, 1, 2 })
                {
                    using var audio = new AudioThread(algorithm);
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    var cost = Steady(audio, perBuffer: () =>
                    {
                        freq.SetValue(1 << 16, 3000 + (n % 40) * 20);
                        if (++n % 100 == 0) delta.SetValue(1 << 16, (n / 100) % 2);
                    });
                    AssertNoAllocation(cost, $"a pattern through a Neo EQ with dynamic bands on Stereo, Mid and Side (algorithm {algorithm})");
                    Assert.That(eq.IsActive, Is.True, $"the EQ sounds (algorithm {algorithm})");
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }
    }
}