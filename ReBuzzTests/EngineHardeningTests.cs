using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: engine hardening for live use (docs/specs/session-view.md, 0.4.1).
    /// <list type="bullet">
    /// <item>Engine, MIDI and native-host threads never wait for the UI thread.</item>
    /// <item>Edits raise their events after releasing AudioLock, so UI listeners never hold up the audio thread.</item>
    /// <item>Session MIDI launches without the UI thread.</item>
    /// <item>A pattern plays in one place; launches are quantized from what is heard.</item>
    /// <item>Pattern positions come from a sorted snapshot; steady playback allocates nothing.</item>
    /// <item>A randomised soak run keeps the engine's invariants.</item>
    /// </list>
    /// </summary>
    public class EngineHardeningTests
    {
        const int Bar = 16;

        static int NoteOn(int note, int channel = 0) => 0x90 | channel | (note << 8) | (100 << 16);

        /// <summary>Generators with sequences, on the master; Tone (shows what plays) or Hum (constant, allocation-free).</summary>
        sealed class Rig : IDisposable
        {
            public Driver Driver { get; } = new Driver();
            public ReBuzzCore Core => Driver.Core;
            public SongCore Song => Core.SongCore;
            public ISession Session => Core.Session;
            public List<MachineCore> Machines { get; } = new List<MachineCore>();

            public Rig(int count, bool hum = false, bool sequences = true)
            {
                var controllers = Enumerable.Range(0, count)
                    .Select(i => hum ? (DynamicMachineController)HumController.NewInstance("Hum" + i) : ToneController.NewInstance("Tone" + i))
                    .ToList();
                Driver.Gear.AddDynamicGenerator(hum ? HumController.Info : ToneController.Info);
                Driver.Start();
                foreach (var c in controllers)
                {
                    Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(c);
                    Machines.Add(Driver.MachineFor(c));
                }
                if (sequences)
                    foreach (var m in Machines)
                        if (!Song.SequencesList.Any(s => s.Machine == m))
                            Song.AddSequence(m, Song.SequencesList.Count);
                Core.Looping = true;
            }

            public SequenceCore Track(int i) => Song.SequencesList[i];

            public PatternCore Pattern(MachineCore machine, string name, int length)
            {
                machine.CreatePattern(name, length);
                return machine.PatternsList.Single(p => p.Name == name);
            }

            public int SamplesPerTick => ReBuzzCore.masterInfo.SamplesPerTick;

            /// <summary>Reads to the next tick boundary (a whole tick if on one).</summary>
            public void Align()
            {
                var mi = ReBuzzCore.masterInfo;
                Driver.ReadStereoSamples(mi.SamplesPerTick - mi.PosInTick);
            }

            public void Ticks(int count)
            {
                for (int i = 0; i < count; i++)
                    Driver.ReadStereoSamples(SamplesPerTick);
            }

            /// <summary>Starts the transport and plays whole ticks until the session clock reaches <paramref name="clock"/>.</summary>
            public void PlayTo(long clock)
            {
                if (!Core.PlayRequested)
                {
                    Align();
                    Core.Playing = true;
                }
                int guard = 0;
                while (Session.Clock < clock && guard++ < 10000)
                    Ticks(1);
                Assert.That(Session.Clock, Is.EqualTo(clock));
            }

            public void Dispose()
            {
                Driver.Dispatcher.Deferred = false;
                Core.Playing = false;
                Driver.Dispose();
            }
        }

        // ------------------------------------------------------------------ 1. no waiting for the UI

        [Test]
        public void TransportStartAndSongEndDoNotWaitForTheUi()
        {
            using var rig = new Rig(1);
            rig.Core.Looping = false;
            rig.Song.LoopEnd = 8;
            var raised = new List<bool>();
            rig.Core.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Playing")
                    raised.Add(ReBuzzCore.AudioLock.IsHeldByCurrentThread);
            };

            rig.Align();
            var dispatcher = rig.Driver.Dispatcher;
            int invokes = dispatcher.InvokeCount;
            dispatcher.Deferred = true;          // the UI thread is busy: nothing it queues runs
            rig.Core.Playing = true;
            rig.Ticks(12);                       // starts, then stops at the song end (tick 8)

            Assert.That(rig.Core.Playing, Is.False, "stopped at the song end");
            Assert.That(dispatcher.InvokeCount, Is.EqualTo(invokes), "the audio thread waited for the UI thread");
            Assert.That(raised, Is.Empty, "nothing is raised on the audio thread");

            dispatcher.RunPending();
            Assert.That(raised.Count, Is.GreaterThanOrEqualTo(2), "start and stop are announced on the UI thread");
            Assert.That(raised, Has.None.True, "never under AudioLock");
        }

        [Test]
        public void MidiLaunchesDoNotNeedTheUi()
        {
            using var rig = new SessionViewTests.Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            rig.Core.SessionMidi.BeginLearn(SessionTarget.Slot, 0, 0);
            rig.Core.SendMIDIInput(NoteOn(36));                      // learnt (UI thread)

            var dispatcher = rig.Driver.Dispatcher;
            int invokes = dispatcher.InvokeCount;
            dispatcher.Deferred = true;
            rig.Core.SendMIDIInput(NoteOn(36));

            Assert.That(rig.Core.Session.GetState(layout.SequenceOf(0)!, beat), Is.EqualTo(ClipState.Queued),
                "launched from the MIDI thread, with the UI thread busy");
            Assert.That(dispatcher.InvokeCount, Is.EqualTo(invokes));
            Assert.That(rig.Core.PlayRequested, Is.True);
            dispatcher.RunPending();
            dispatcher.Deferred = false;
        }

        // ------------------------------------------------------------------ 2. events outside the lock

        [Test]
        public void EditsRaiseTheirEventsOutsideTheAudioLock()
        {
            using var rig = new Rig(2, sequences: false);
            var song = rig.Song;
            var a = rig.Machines[0];
            var b = rig.Machines[1];
            var seen = new List<(string Name, bool Locked)>();
            void Spy(string name) => seen.Add((name, ReBuzzCore.AudioLock.IsHeldByCurrentThread));

            song.ConnectionAdded += c => Spy("ConnectionAdded");
            song.ConnectionRemoved += c => Spy("ConnectionRemoved");
            song.SequenceAdded += (i, s) => Spy("SequenceAdded");
            song.SequenceRemoved += (i, s) => Spy("SequenceRemoved");
            song.SequenceChanged += (i, s) => Spy("SequenceChanged");
            song.PropertyChanged += (s, e) => Spy("Song." + e.PropertyName);
            a.PatternAdded += p => Spy("PatternAdded");
            a.PatternRemoved += p => Spy("PatternRemoved");

            // Connections (the mixer's sends and cue), with undo
            Assert.That(song.CanConnectMachines(a, b), Is.False, "generators take no input");
            var master = song.MachinesList.First(m => m.Name == "Master");
            var toMaster = a.Outputs.Single(o => o.Destination == master);
            song.DisconnectMachines(toMaster);
            song.Undo();
            song.ConnectMachines(b, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);   // b is already connected: a second connection
            song.Undo();

            // Sequences and their events
            song.AddSequence(a, 0);
            song.AddSequence(b, 1);
            var seqA = song.SequencesList[0];
            seqA.EventChanged += t => Spy("EventChanged");
            seqA.SpanInserted += (t, n) => Spy("SpanInserted");
            seqA.SpanDeleted += (t, n) => Spy("SpanDeleted");
            seqA.SpanCleared += (t, n) => Spy("SpanCleared");
            seqA.PropertyChanged += (s, e) => Spy("Sequence." + e.PropertyName);
            var p = rig.Pattern(a, "P", 8);
            seqA.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, p));
            seqA.SetEvent(16, new SequenceEvent(SequenceEventType.PlayPattern, p));
            seqA.Insert(0, 4);
            seqA.Delete(0, 4);
            seqA.Clear(16, 4);
            song.SwapSequences(song.SequencesList[0], song.SequencesList[1]);
            song.LoopEnd = 32;
            a.DeletePattern(p);
            song.RemoveSequence(seqA);

            var names = seen.Select(e => e.Name).Distinct().ToList();
            Assert.That(names, Is.SupersetOf(new[]
            {
                "ConnectionAdded", "ConnectionRemoved", "SequenceAdded", "SequenceRemoved", "SequenceChanged",
                "PatternAdded", "PatternRemoved", "EventChanged", "SpanInserted", "SpanDeleted", "SpanCleared",
                "Sequence.Events", "Song.LoopEnd"
            }));
            Assert.That(seen.Where(e => e.Locked).Select(e => e.Name), Is.Empty, "raised while holding AudioLock");
        }

        // ------------------------------------------------------------------ 6. one pattern, one place

        [Test]
        public void ASessionClipTakesItsPatternFromAnotherTracksArrangement()
        {
            using var rig = new Rig(1);
            var tone = rig.Machines[0];
            rig.Song.AddSequence(tone, 1);                           // a second track for the same machine
            var arranged = rig.Track(0);
            var session = rig.Track(1);
            var p = rig.Pattern(tone, "P", 2 * Bar);
            arranged.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, p));
            rig.Song.LoopEnd = 4 * Bar;
            rig.Session.LaunchQuantization = LaunchQuantization.None;

            rig.PlayTo(6);
            Assert.That(arranged.PlayingPattern, Is.SameAs(p));
            Assert.That(p.PlayPosition / PatternEvent.TimeBase, Is.EqualTo(6), "the arrangement plays it from 0");

            rig.Session.Launch(session, p);
            rig.Ticks(3);
            int clipTick = rig.Session.GetClipPosition(session);
            Assert.That(clipTick, Is.InRange(1, 3));
            Assert.That(p.PlayPosition / PatternEvent.TimeBase, Is.EqualTo(clipTick), "the session clip's position wins");
            Assert.That(session.PlayingPattern, Is.SameAs(p));
            Assert.That(arranged.PlayingPattern, Is.Null, "the arrangement track no longer claims it");
        }

        [Test]
        public void LaunchingAPatternPlayingOnAnotherTrackMovesIt()
        {
            using var rig = new Rig(1);
            var tone = rig.Machines[0];
            rig.Song.AddSequence(tone, 1);
            var p = rig.Pattern(tone, "P", Bar);
            rig.Session.LaunchQuantization = LaunchQuantization.Beat;
            rig.Session.Launch(rig.Track(0), p);
            rig.PlayTo(5);
            rig.Session.Launch(rig.Track(1), p);
            Assert.That(rig.Session.GetState(rig.Track(0), p), Is.EqualTo(ClipState.Playing), "plays on until it starts there");
            rig.PlayTo(8);
            Assert.That(rig.Session.GetPlaying(rig.Track(0)), Is.Null);
            Assert.That(rig.Session.GetPlaying(rig.Track(1)), Is.SameAs(p));
        }

        // ------------------------------------------------------------------ 7. quantized from what is heard

        [Test]
        public void ALaunchHeardBeforeTheBarLineCatchesUp()
        {
            using var rig = new Rig(1);
            var p = rig.Pattern(rig.Machines[0], "P", Bar);
            rig.PlayTo(17);
            rig.Driver.ReadStereoSamples(rig.SamplesPerTick / 2);   // rendered: 17.5 ticks
            rig.Core.OutputLatencySamples = 3 * rig.SamplesPerTick;  // heard: about 14.5

            rig.Session.Launch(rig.Track(0), p);                     // one bar quantization
            rig.Driver.ReadStereoSamples(rig.SamplesPerTick - ReBuzzCore.masterInfo.PosInTick);

            Assert.That(rig.Session.GetClipStartClock(rig.Track(0)), Is.EqualTo(Bar), "in step with the bar line the listener was before");
            Assert.That(rig.Session.GetClipPosition(rig.Track(0)), Is.EqualTo(2), "starting where that bar is now");
        }

        [Test]
        public void WithoutLatencyALaunchWaitsForTheNextBarLine()
        {
            using var rig = new Rig(1);
            var p = rig.Pattern(rig.Machines[0], "P", Bar);
            rig.PlayTo(17);
            rig.Core.OutputLatencySamples = 0;
            rig.Session.Launch(rig.Track(0), p);
            rig.PlayTo(31);
            Assert.That(rig.Session.GetPlaying(rig.Track(0)), Is.Null);
            rig.PlayTo(32);
            Assert.That(rig.Session.GetClipStartClock(rig.Track(0)), Is.EqualTo(2 * Bar));
        }

        // ------------------------------------------------------------------ 8. arrangement lookup

        [Test]
        public void TheArrangementIsLookedUpInTimeOrderWhateverTheEditOrder()
        {
            using var rig = new Rig(1);
            var tone = rig.Machines[0];
            rig.Song.AddSequence(tone, 1);
            var p = rig.Pattern(tone, "P", 8);
            var q = rig.Pattern(tone, "Q", 8);
            var first = rig.Track(0);
            var second = rig.Track(1);
            first.SetEvent(16, new SequenceEvent(SequenceEventType.PlayPattern, q));   // set out of order
            first.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, p));
            second.SetEvent(24, new SequenceEvent(SequenceEventType.PlayPattern, p));
            rig.Song.LoopEnd = 32;

            rig.PlayTo(4);
            Assert.That(p.PlayPosition / PatternEvent.TimeBase, Is.EqualTo(4));
            Assert.That(first.PlayingPattern, Is.SameAs(p));
            rig.PlayTo(10);
            Assert.That(p.PlayPosition, Is.EqualTo(int.MinValue), "P (8 ticks) ended");
            Assert.That(first.PlayingPattern, Is.Null);
            rig.PlayTo(18);
            Assert.That(q.PlayPosition / PatternEvent.TimeBase, Is.EqualTo(2));
            rig.PlayTo(26);
            Assert.That(p.PlayPosition / PatternEvent.TimeBase, Is.EqualTo(2), "P again, on the second track");
            Assert.That(second.PlayingPattern, Is.SameAs(p));
            Assert.That(first.PlayingPattern, Is.Null, "the first track's P event is long over (upstream said P)");
        }

        // ------------------------------------------------------------------ 4. allocation and time per buffer

        static long AllocatedWhileReading(Rig rig, Driver.AudioReader reader, int buffers, int frames, List<string> log)
        {
            long total = 0;
            for (int i = 0; i < buffers; i++)
            {
                int posBefore = ReBuzzCore.masterInfo.PosInTick;
                long before = GC.GetAllocatedBytesForCurrentThread();
                reader.Read(frames);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                total += bytes;
                if (bytes > 0 && log.Count < 20)
                    log.Add($"buffer {i}: {bytes} bytes (PosInTick {posBefore} -> {ReBuzzCore.masterInfo.PosInTick}, clock {rig.Session.Clock})");
            }
            return total;
        }

        [Test]
        public void SteadyPlaybackAllocatesNothing()
        {
            using var rig = new Rig(4, hum: true);
            for (int i = 0; i < 4; i++)
            {
                var m = rig.Machines[i];
                var p = rig.Pattern(m, "P", Bar);
                var q = rig.Pattern(m, "Q", 2 * Bar);
                rig.Track(i).SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, p));
                rig.Track(i).SetEvent(Bar, new SequenceEvent(SequenceEventType.PlayPattern, q));
                if (i >= 2)
                    rig.Session.Launch(rig.Track(i), q);            // half from the arrangement, half from the session
            }
            rig.Song.LoopEnd = 4 * Bar;
            var reader = rig.Driver.CreateAudioReader(256);
            rig.Core.Playing = true;
            for (int i = 0; i < 200; i++)
                reader.Read(256);                                    // warm up: JIT, first launches, tiered compilation
            // Fork (0.6.2): tiered compilation can still be running methods as first compiled (where
            // a short-lived object isn't yet kept off the heap) when a busy CI machine gets here: it
            // failed now and then with 40-80 bytes per buffer. Steady state is after that: read on
            // until a stretch of 100 buffers allocates nothing (or give up and measure anyway).
            for (int stretch = 0; stretch < 200; stretch++)
            {
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++)
                    reader.Read(256);
                if (GC.GetAllocatedBytesForCurrentThread() == start)
                    break;
                if (stretch == 0)
                    TestContext.Out.WriteLine("[notice] steady playback: still allocating after the warm-up; reading on until it stops");
            }

            var log = new List<string>();
            long bytes = AllocatedWhileReading(rig, reader, 400, 256, log);   // ~2.3 s: several ticks and a loop wrap

            TestContext.Out.WriteLine($"[notice] steady playback allocated {bytes} bytes in 400 buffers of 256 frames");
            string types = bytes > 0 ? AllocatedTypes(reader) : "";
            Assert.That(bytes, Is.EqualTo(0),
                "steady playback must not allocate on the audio thread (GC pauses are dropouts):\n" + string.Join("\n", log) + types);
        }

        /// <summary>
        /// When steady playback allocates (it did now and then on CI), what: the runtime samples
        /// an allocation about every 100 KB (GCAllocationTick, with the type's name), so keep
        /// reading until there are a few samples.
        /// </summary>
        static string AllocatedTypes(Driver.AudioReader reader)
        {
            using var listener = new AllocationListener();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000 && listener.Count < 5; i++)
                reader.Read(256);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            return $"\nthen {bytes} bytes in up to 20000 buffers; sampled types: {listener.Describe()}";
        }

        sealed class AllocationListener : System.Diagnostics.Tracing.EventListener
        {
            readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> types = new();
            int count;
            readonly int thread = Environment.CurrentManagedThreadId;

            public int Count => Volatile.Read(ref count);

            protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
            {
                if (source.Name == "Microsoft-Windows-DotNETRuntime")
                    EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);   // GC
            }

            protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
            {
                if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick")) return;
                int i = e.PayloadNames?.IndexOf("TypeName") ?? -1;
                string name = i >= 0 ? e.Payload[i] as string ?? "?" : "?";
                types.AddOrUpdate(name, 1, (_, n) => n + 1);
                Interlocked.Increment(ref count);
            }

            public string Describe() => types.IsEmpty ? "none" : string.Join(", ", types.Select(t => $"{t.Key} x{t.Value}"));
        }

        /// <summary>Not a check: reports which engine stages allocate, per call (CI shows the [notice] lines).</summary>
        [Test]
        public void ReportAllocationsByEngineStage()
        {
            using var rig = new Rig(4, hum: true);
            for (int i = 0; i < 4; i++)
            {
                var m = rig.Machines[i];
                var p = rig.Pattern(m, "P", Bar);
                rig.Track(i).SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, p));
                if (i >= 2)
                    rig.Session.Launch(rig.Track(i), p);
            }
            var reader = rig.Driver.CreateAudioReader(256);
            rig.Core.Playing = true;
            for (int i = 0; i < 200; i++)
                reader.Read(256);

            var wm = reader.WorkManager;
            var buffer = new float[512];
            var report = new List<string>();
            void Measure(string stage, Action action)
            {
                action();                                           // first call may allocate lazily
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++)
                    action();
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                report.Add($"{stage} {bytes / 100.0:F0}");
            }

            lock (ReBuzzCore.AudioLock)
            {
                Measure("CopyMasterInfo", () => wm.CopyMasterInfo());
                Measure("CopySubTickInfo", () => wm.CopySubTickInfo());
                Measure("UpdateMasterParams", () => wm.UpdateMasterParams());
                Measure("UpdateSubTickLength", () => wm.UpdateSubTickLength());
                Measure("UpdatePatternColumnEvents", () => wm.UpdatePatternColumnEvents(256));
                Measure("UpdateMasterAndSubTickInfoToHost", () => rig.Core.MachineManager.UpdateMasterAndSubTickInfoToHost());
                Measure("CallTick(mid-tick)", () => { ReBuzzCore.masterInfo.PosInTick = 100; wm.CallTick(); });
                Measure("CallTick(on tick)", () => { ReBuzzCore.masterInfo.PosInTick = 0; wm.CallTick(); });
                Measure("ReadWork", () => wm.ReadWork(buffer, 0, 256));
                Measure("MasterTapSamples", () => rig.Core.MasterTapSamples(buffer, 0, 512));
                Measure("MasterTapSamplesMultiChannel", () => rig.Core.MasterTapSamplesMultiChannel(buffer, 0, 512));
                Measure("UpdatePatternPositions", () => wm.UpdatePatternPositions(256));
                Measure("SessionCore.OnTick", () => rig.Core.SessionCore.OnTick(false));
                Measure("SongCore.UpdatePlayPosition", () => rig.Song.UpdatePlayPosition(1));
                Measure("SessionCore.PublishRenderedPosition", () => rig.Core.SessionCore.PublishRenderedPosition());
                Measure("AudioEngine.ClearChannels", () => rig.Core.AudioEngine.ClearChannels());
                Measure("GetGrooveMul", () => wm.GetGrooveMul());
                Measure("IsPlayingWave", () => rig.Song.WavetableCore.IsPlayingWave());
            }
            Measure("whole buffer (256 frames)", () => reader.Read(256));
            TestContext.Out.WriteLine("[notice] bytes per call: " + string.Join(", ", report));
        }

        [Test]
        public void AHeavySongRendersFasterThanRealTime()
        {
            const int machines = 32;
            using var rig = new Rig(machines, hum: true);
            for (int i = 0; i < machines; i++)
            {
                var m = rig.Machines[i];
                var patterns = Enumerable.Range(0, 4).Select(k => rig.Pattern(m, "P" + k, Bar)).ToList();
                for (int t = 0; t < 32; t++)
                    rig.Track(i).SetEvent(t * Bar, new SequenceEvent(SequenceEventType.PlayPattern, patterns[(t + i) % 4]));
                if (i % 2 == 0)
                    rig.Session.Launch(rig.Track(i), patterns[0]);
            }
            rig.Song.LoopEnd = 32 * Bar;
            var reader = rig.Driver.CreateAudioReader(256);
            rig.Core.Playing = true;
            for (int i = 0; i < 100; i++)
                reader.Read(256);

            const int buffers = 2000;
            var times = new double[buffers];
            var total = Stopwatch.StartNew();
            for (int i = 0; i < buffers; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                reader.Read(256);
                times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            total.Stop();

            double audioMs = buffers * 256 * 1000.0 / ReBuzzCore.masterInfo.SamplesPerSec;
            double budgetMs = 256 * 1000.0 / ReBuzzCore.masterInfo.SamplesPerSec;
            var sorted = times.OrderBy(t => t).ToArray();
            TestContext.Out.WriteLine($"[notice] {machines} machines, {machines} tracks x 32 events, half playing session clips: " +
                $"{audioMs / total.Elapsed.TotalMilliseconds:F1}x real time; per 256-frame buffer (budget {budgetMs:F2} ms): " +
                $"median {sorted[buffers / 2]:F3} ms, 99th {sorted[buffers * 99 / 100]:F3} ms, max {sorted[^1]:F3} ms");
            Assert.That(total.Elapsed.TotalMilliseconds, Is.LessThan(audioMs), "slower than real time");
        }

        // ------------------------------------------------------------------ 10. autosave

        static string UseTemporaryAutosaveFolder()
        {
            string folder = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "autosave-" + Guid.NewGuid().ToString("N"));
            SongAutosave.Folder = folder;
            return folder;
        }

        [Test]
        public void AutosaveWritesACopyAndLeavesTheSongAlone()
        {
            string original = SongAutosave.Folder;
            UseTemporaryAutosaveFolder();
            try
            {
                using var rig = new Rig(1);
                rig.Pattern(rig.Machines[0], "P", Bar);
                rig.Core.Modified = true;
                var autosave = rig.Core.Autosave;
                var now = new DateTime(2026, 10, 28, 20, 0, 0);

                Assert.That(autosave.Tick(now), Is.True);
                string path = autosave.Current!;
                Assert.That(path, Is.EqualTo(SongAutosave.PathFor(null)));
                Assert.That(System.IO.File.Exists(path), Is.True);
                Assert.That(new System.IO.FileInfo(path).Length, Is.GreaterThan(0));
                Assert.That(rig.Core.SongCore.SongName, Is.Null, "the song keeps its (lack of a) name");
                Assert.That(rig.Core.Modified, Is.True, "and its unsaved changes");

                Assert.That(autosave.Tick(now.AddMinutes(1)), Is.False, "not again within two minutes");
                Assert.That(autosave.Tick(now.AddMinutes(3)), Is.True);
                rig.Core.Playing = true;
                Assert.That(autosave.Tick(now.AddMinutes(10)), Is.False, "not while playing");
                rig.Core.Playing = false;

                rig.Core.Modified = false;
                rig.Driver.NewFile();
                Assert.That(System.IO.File.Exists(path), Is.False, "a new song drops the old song's autosave");
            }
            finally
            {
                SongAutosave.Folder = original;
            }
        }

        [Test]
        public void AnAutosaveLeftByACrashIsRecoveredAsTheSongWithChanges()
        {
            string original = SongAutosave.Folder;
            string folder = UseTemporaryAutosaveFolder();
            string crashCopy = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "crash-" + Guid.NewGuid().ToString("N"));
            string songName = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "Live set.bmx");
            try
            {
                using (var rig = new Rig(1))
                {
                    rig.Pattern(rig.Machines[0], "Verse", Bar);
                    rig.Core.SongCore.SongName = songName;
                    rig.Core.Modified = true;
                    Assert.That(rig.Core.Autosave.SaveNow(DateTime.Now), Is.True);
                    // "Crash": keep the files that a normal exit would delete.
                    System.IO.Directory.CreateDirectory(crashCopy);
                    foreach (var f in System.IO.Directory.GetFiles(folder))
                        System.IO.File.Copy(f, System.IO.Path.Combine(crashCopy, System.IO.Path.GetFileName(f)));
                }
                foreach (var f in System.IO.Directory.GetFiles(crashCopy))
                    System.IO.File.Copy(f, System.IO.Path.Combine(folder, System.IO.Path.GetFileName(f)), overwrite: true);

                using var restarted = new Rig(0);
                var leftovers = SongAutosave.FindLeftovers();
                Assert.That(leftovers.Count, Is.EqualTo(1));
                Assert.That(leftovers[0].Song, Is.EqualTo(songName));

                restarted.Core.Autosave.Recover(leftovers[0]);

                Assert.That(restarted.Core.SongCore.SongName, Is.EqualTo(songName), "Save goes to the original file");
                Assert.That(restarted.Core.Modified, Is.True);
                Assert.That(restarted.Core.SongCore.MachinesList.SelectMany(m => m.PatternsList).Select(p => p.Name), Does.Contain("Verse"));
                Assert.That(SongAutosave.FindLeftovers(), Is.Empty);
            }
            finally
            {
                SongAutosave.Folder = original;
            }
        }

        // ------------------------------------------------------------------ soak

        [Test]
        public void RandomisedSessionPlayKeepsTheEngineConsistent()
        {
            const int machines = 6;
            using var rig = new Rig(machines);
            rig.Song.AddSequence(rig.Machines[0], machines);          // a 7th track: machine 0 again
            int tracks = rig.Song.SequencesList.Count;
            var random = new Random(4101);
            var clips = new List<PatternCore>[machines];
            for (int m = 0; m < machines; m++)
            {
                clips[m] = new List<PatternCore>();
                for (int k = 0; k < 4; k++)
                    clips[m].Add(rig.Pattern(rig.Machines[m], $"C{k}", 4 * (1 + random.Next(8))));
                rig.Track(m).SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, clips[m][0]));
            }
            List<PatternCore> ClipsOf(int track) => clips[rig.Machines.IndexOf((MachineCore)rig.Track(track).Machine)];

            rig.Song.LoopEnd = 8 * Bar;
            var quantizations = (LaunchQuantization[])Enum.GetValues(typeof(LaunchQuantization));
            var session = rig.Session;
            rig.PlayTo(0);

            int launches = 0, stops = 0, violations = 0;
            var problems = new List<string>();
            for (int step = 0; step < 2000; step++)
            {
                if (random.Next(3) == 0)
                {
                    int t = random.Next(tracks);
                    var options = new LaunchOptions { Quantization = quantizations[random.Next(quantizations.Length)], Loop = random.Next(5) != 0 };
                    switch (random.Next(10))
                    {
                        case 0: session.Stop(rig.Track(t), random.Next(4) == 0); stops++; break;
                        case 1: session.StopAll(); stops++; break;
                        case 2: session.LaunchScene(Enumerable.Range(0, tracks).Select(i => ((ISequence)rig.Track(i), (IPattern)ClipsOf(i)[random.Next(4)])), options); launches++; break;
                        case 3: if (random.Next(4) == 0) session.BackToArrangement(); break;
                        case 4: rig.Core.OutputLatencySamples = random.Next(3) * rig.SamplesPerTick; break;
                        default: session.Launch(rig.Track(t), ClipsOf(t)[random.Next(4)], options); launches++; break;
                    }
                }
                rig.Driver.ReadStereoSamples(rig.SamplesPerTick / (1 + random.Next(3)));

                // Invariants: a session clip belongs to its track's machine, plays in one place,
                // and has the scheduler's position (checked on tick boundaries).
                var owners = new Dictionary<PatternCore, SequenceCore>();
                for (int t = 0; t < tracks; t++)
                {
                    var seq = rig.Track(t);
                    if (session.GetPlaying(seq) is not PatternCore clip) continue;
                    if (clip.Machine != seq.Machine) { violations++; problems.Add($"step {step}: {clip.Name} on {seq.Machine.Name}'s track belongs to {clip.Machine.Name}"); }
                    if (owners.ContainsKey(clip)) { violations++; problems.Add($"step {step}: {clip.Machine.Name}/{clip.Name} plays on two tracks"); }
                    owners[clip] = seq;
                    if (ReBuzzCore.masterInfo.PosInTick == 0 && clip.PlayPosition != int.MinValue &&
                        clip.PlayPosition / PatternEvent.TimeBase != session.GetClipPosition(seq))
                    {
                        violations++;
                        problems.Add($"step {step}: {clip.Name} at {clip.PlayPosition / PatternEvent.TimeBase}, scheduler says {session.GetClipPosition(seq)}");
                    }
                }
            }
            foreach (var line in problems.Take(20)) TestContext.Out.WriteLine(line);
            TestContext.Out.WriteLine($"{launches} launches, {stops} stops, {violations} violations");
            Assert.That(violations, Is.EqualTo(0));

            session.StopAll(immediate: true);
            session.BackToArrangement();
            rig.Ticks(2);
            for (int t = 0; t < tracks; t++)
                Assert.That(session.GetPlaying(rig.Track(t)), Is.Null, $"track {t} still plays a clip");
            rig.Core.Playing = false;
            rig.Ticks(1);
            Assert.That(rig.Machines.SelectMany(m => m.PatternsList).Where(p => p.PlayPosition != int.MinValue), Is.Empty, "a pattern kept a play position after the transport stopped");
        }
    }
}
