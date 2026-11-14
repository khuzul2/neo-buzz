using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Actions;
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
            public List<DynamicMachineController> Controllers { get; }

            public Rig(int count, bool hum = false, bool sequences = true)
            {
                var controllers = Enumerable.Range(0, count)
                    .Select(i => hum ? (DynamicMachineController)HumController.NewInstance("Hum" + i) : ToneController.NewInstance("Tone" + i))
                    .ToList();
                Controllers = controllers;
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

        /// <summary>
        /// Bytes allocated reading <paramref name="buffers"/> buffers (each after <paramref name="perBuffer"/>, if any: counted
        /// too), each measured by <paramref name="window"/>, which keeps what the thread did in it.
        /// </summary>
        static long AllocatedWhileReading(Rig rig, Driver.AudioReader reader, int buffers, int frames, List<string> log, AllocationWindow window, Action? perBuffer = null)
        {
            // GuardPolicy: a stretch that allocated is read again (up to 3 in all); 0 when any attempt allocated nothing.
            long total = 0;
            for (int attempt = 0; attempt < GuardPolicy.Attempts; attempt++)
            {
                total = AllocatedWhileReadingOnce(rig, reader, buffers, frames, log, window, perBuffer);
                if (total == 0) { log.Clear(); return 0; }
                log.Add($"(attempt {attempt + 1} of {GuardPolicy.Attempts}: {total} bytes)");
            }
            return total;
        }

        static long AllocatedWhileReadingOnce(Rig rig, Driver.AudioReader reader, int buffers, int frames, List<string> log, AllocationWindow window, Action? perBuffer)
        {
            long total = 0;
            for (int i = 0; i < buffers; i++)
            {
                int posBefore = ReBuzzCore.masterInfo.PosInTick;
                window.Begin();
                perBuffer?.Invoke();
                reader.Read(frames);
                long bytes = window.End();
                total += bytes;
                if (bytes > 0 && log.Count < 20)
                    log.Add($"buffer {i}: {bytes} bytes (PosInTick {posBefore} -> {ReBuzzCore.masterInfo.PosInTick}, clock {rig.Session.Clock})");
            }
            return total;
        }

        /// <summary>The rig's four Hums, two playing from the arrangement and two from the session, P then Q, looping over 4 bars: playing, warmed up.</summary>
        static Driver.AudioReader SteadyPlayback(Rig rig)
        {
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
            WarmUp(reader, "steady playback");
            return reader;
        }

        /// <summary>
        /// Steady playback allocates nothing on the audio thread. It met a one-off allocation after the warm-up now and then on
        /// CI (848, 1512 and 2048 bytes in one buffer, at no particular place in the song; runs 41916587037, 41966650618 and
        /// 41912541139), which reading on afterwards never met again: a failure says what the thread did in that buffer
        /// (AllocationWindow: the JIT, type loads, exceptions, waits and GCs, from the runtime's own events).
        /// </summary>
        [Test]
        public void SteadyPlaybackAllocatesNothing()
        {
            using var rig = new Rig(4, hum: true);
            var reader = SteadyPlayback(rig);
            using var window = new AllocationWindow(400);
            var log = new List<string>();
            long bytes = AllocatedWhileReading(rig, reader, 400, 256, log, window);   // ~2.3 s: several ticks and a loop wrap

            TestContext.Out.WriteLine($"[notice] steady playback allocated {bytes} bytes in 400 buffers of 256 frames");
            string what = bytes > 0 ? window.Describe() + AllocatedTypes(reader) : "";
            Assert.That(bytes, Is.EqualTo(0),
                "steady playback must not allocate on the audio thread (GC pauses are dropouts):\n" + string.Join("\n", log) + what);
        }

        /// <summary>
        /// Not a check (run by name): SteadyPlaybackAllocatesNothing's measurement on <see cref="SoakRuns"/> fresh rigs, each
        /// window that allocated reported with what the thread did in it ([notice] lines), to catch its rare one-off allocation.
        /// </summary>
        [Test, Explicit("a soak for SteadyPlaybackAllocatesNothing's rare one-off allocation; run by name")]
        public void SteadyPlaybackAllocationSoak()
        {
            int allocated = 0;
            for (int run = 0; run < SoakRuns; run++)
            {
                using var rig = new Rig(4, hum: true);
                var reader = SteadyPlayback(rig);
                using var window = new AllocationWindow(400);
                var log = new List<string>();
                long bytes = AllocatedWhileReading(rig, reader, 400, 256, log, window);
                if (bytes == 0) continue;
                allocated++;
                TestContext.Out.WriteLine($"[notice] soak run {run + 1}: {bytes} bytes in 400 buffers: " + string.Join("; ", log) + window.Describe().Replace("\n", " | "));
            }
            TestContext.Out.WriteLine($"[notice] soak: {allocated} of {SoakRuns} steady windows allocated");
        }

        const int SoakRuns = 150;

        /// <summary>Reads past one-time costs (JIT, first launches, tiered compilation), each buffer after <paramref name="perBuffer"/>.</summary>
        internal static void WarmUp(Driver.AudioReader reader, string what, Action? perBuffer = null)
        {
            for (int i = 0; i < 200; i++)
            {
                perBuffer?.Invoke();
                reader.Read(256);                                    // warm up: JIT, first launches, tiered compilation
            }
            // Fork (0.6.2): steady state comes after one-time costs: read on until a stretch of 100
            // buffers allocates nothing (or give up and measure anyway). 0.6.3: the 40-80 bytes per
            // buffer this test met now and then were LINQ's First() boxing the work set's
            // enumerator, unless profile-guided optimisation removed the box; fixed in WorkManager.
            for (int stretch = 0; stretch < 200; stretch++)
            {
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++)
                {
                    perBuffer?.Invoke();
                    reader.Read(256);
                }
                if (GC.GetAllocatedBytesForCurrentThread() == start)
                    break;
                if (stretch == 0)
                    TestContext.Out.WriteLine($"[notice] {what}: still allocating after the warm-up; reading on until it stops");
            }
        }

        /// <summary>What playing parameter changes is made of (ParameterChangesAllocateNothing).</summary>
        public enum ParameterChangeSource { AutomationSteps, AutomationRamps, PatternEditor, MidiController }

        /// <summary>
        /// Engine hardening (docs/specs/daw-modes.md §8.1): parameter changes reach their machines
        /// without allocating. Four machines (as steady playback above: two from the arrangement,
        /// two from the session) change a global parameter and a track parameter on four tracks:
        /// from engine automation columns, with steps on every tick or with ramps (events with a
        /// Duration, set on every tick and subtick); set as pattern editors set them, through the
        /// public interface (the Modern Pattern Editor's ControlChange:
        /// machine.ParameterGroups[group].Parameters[index].SetValue, for each value it plays);
        /// or from a MIDI controller mapped to the global. Upstream recorded each change in a
        /// ConcurrentDictionary: about 3.6 KB a buffer here, steps and ramps alike.
        /// </summary>
        [Test]
        public void ParameterChangesAllocateNothing([Values] ParameterChangeSource source)
        {
            using var rig = new Rig(4, hum: true);
            var heard = new int[1];
            for (int i = 0; i < 4; i++)
            {
                var m = rig.Machines[i];
                m.TrackCount = 4;
                rig.Driver.MachineGraph.ExecuteMachineCommand(((HumController)rig.Controllers[i]).Listen((parameter, track, value) => heard[0]++));
                var p = rig.Pattern(m, "P", Bar);
                var q = rig.Pattern(m, "Q", 2 * Bar);
                if (source is ParameterChangeSource.AutomationSteps or ParameterChangeSource.AutomationRamps)
                {
                    Automate(p, Bar, ramps: source == ParameterChangeSource.AutomationRamps);
                    Automate(q, 2 * Bar, ramps: source == ParameterChangeSource.AutomationRamps);
                }
                rig.Track(i).SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, p));
                rig.Track(i).SetEvent(Bar, new SequenceEvent(SequenceEventType.PlayPattern, q));
                if (i >= 2)
                    rig.Session.Launch(rig.Track(i), q);            // half from the arrangement, half from the session
            }
            rig.Song.LoopEnd = 4 * Bar;
            var perBuffer = source switch
            {
                ParameterChangeSource.PatternEditor => AsAPatternEditorPlays(rig.Machines),
                ParameterChangeSource.MidiController => FromAMappedMidiController(rig),
                _ => null,
            };
            var reader = rig.Driver.CreateAudioReader(256);
            rig.Core.Playing = true;
            WarmUp(reader, source.ToString(), perBuffer);

            var log = new List<string>();
            int heardBefore = heard[0];
            using var window = new AllocationWindow(400);
            long bytes = AllocatedWhileReading(rig, reader, 400, 256, log, window, perBuffer);
            int values = heard[0] - heardBefore;
            int ticks = 400 * 256 / rig.SamplesPerTick;

            TestContext.Out.WriteLine($"[notice] {source}: {bytes} bytes in 400 buffers of 256 frames; the machines were set {values} values in {ticks} ticks");
            Assert.That(values, Is.GreaterThanOrEqualTo(4 * ticks), "each machine was set values on every tick");
            string types = bytes > 0 ? window.Describe() + AllocatedTypes(reader, perBuffer) : "";
            Assert.That(bytes, Is.EqualTo(0),
                "parameter changes must not allocate on the audio thread (GC pauses are dropouts):\n" + string.Join("\n", log) + types);
        }

        /// <summary>Engine automation columns of a Hum's Level (a global, 0-127) and of its Gate on four tracks: steps on every tick, or ramps of 4 ticks.</summary>
        static void Automate(PatternCore pattern, int ticks, bool ramps)
        {
            const int TB = PatternEvent.TimeBase;
            var keys = new[] { new ColumnKey(1, 1, 0) }.Concat(Enumerable.Range(0, 4).Select(t => new ColumnKey(2, 0, t)));
            foreach (var key in keys)
            {
                var events = ramps
                    ? Enumerable.Range(0, ticks / 4).Select(r => new PatternEvent(4 * r * TB, (r + key.Track) % 2 == 0 ? 0 : 127, 4 * TB))
                    : Enumerable.Range(0, ticks).Select(t => new PatternEvent(t * TB, (t + key.Track) % 2 == 0 ? 0 : 100 + key.Track, 0));
                var column = SessionLayout.ColumnFor(pattern, key, create: true);
                column.SetEvents(events.ToList(), true);
                Assert.That(((PatternColumnCore)column).HasRamps, Is.EqualTo(ramps));
            }
        }

        /// <summary>
        /// Sets values before every buffer as the Modern Pattern Editor plays them, through the
        /// public interface (its ControlChange: machine.ParameterGroups[group].Parameters[index]
        /// .SetValue(track | 1 &lt;&lt; 16, value)): the Hums' Level and their Gate on four tracks.
        /// </summary>
        static Action AsAPatternEditorPlays(List<MachineCore> machines)
        {
            int n = 0;
            return () =>
            {
                n++;
                foreach (IMachine m in machines)
                {
                    m.ParameterGroups[1].Parameters[1].SetValue(1 << 16, n % 128);
                    for (int t = 0; t < 4; t++)
                        m.ParameterGroups[2].Parameters[0].SetValue(t | 1 << 16, (n + t) % 128);
                }
            };
        }

        /// <summary>
        /// Maps a controller (CC 20 + i, channel 1) to each Hum's Level, and moves them all before
        /// every buffer, as the UI thread passes a controller's messages on (MidiControllerAssignments).
        /// </summary>
        static Action FromAMappedMidiController(Rig rig)
        {
            for (int i = 0; i < rig.Machines.Count; i++)
                rig.Machines[i].ParameterGroupsList[1].ParametersList[1].BindToMIDIController(0, 0, 20 + i);
            var assignments = rig.Core.MidiControllerAssignments;
            int n = 0;
            return () =>
            {
                n++;
                for (int i = 0; i < 4; i++)
                    assignments.SendMidi(0xB0 | (20 + i) << 8 | (n % 2 == 0 ? 10 : 120) << 16);
            };
        }

        /// <summary>
        /// Engine hardening: what reaches a managed machine on a tick is what upstream's dictionary
        /// sent: one call for each parameter and track changed since the last tick, with the last
        /// value set; nothing again until the next change; the pvals cleared after the tick; value
        /// listeners told of each. Now in Buzz's order of a tick's values (globals by index, then
        /// the tracks, each by index); the dictionary's was by hash. And an input's amp reaches its
        /// connection, and a connection's amp its input (both set without building Inputs now).
        /// </summary>
        [Test]
        public void ATicksParameterChangesReachTheMachineOnceEachInOrder()
        {
            using var rig = new Rig(1, hum: true);
            var hum = rig.Machines[0];
            hum.TrackCount = 4;
            var heard = new List<(int Parameter, int Track, int Value)>();
            rig.Driver.MachineGraph.ExecuteMachineCommand(((HumController)rig.Controllers[0]).Listen((parameter, track, value) => heard.Add((parameter, track, value))));
            var bypass = hum.ParameterGroupsList[1].ParametersList[0];
            var level = hum.ParameterGroupsList[1].ParametersList[1];
            var gate = hum.ParameterGroupsList[2].ParametersList[0];
            var told = new List<(string, int)>();
            level.SubscribeEvents(0, (p, t) => told.Add((p.Name, t)), null);
            gate.SubscribeEvents(2, (p, t) => told.Add((p.Name, t)), null);
            rig.Align();
            rig.Ticks(1);
            heard.Clear();
            told.Clear();

            gate.SetValue(3, 30);
            level.SetValue(0, 10);
            gate.SetValue(1, 11);
            bypass.SetValue(0, 1);
            level.SetValue(0, 20);                               // twice: the last value is sent
            gate.SetValue(2, 22);
            Assert.That(heard, Is.Empty, "values wait for the machine's tick");
            Assert.That(level.GetPValue(0), Is.EqualTo(20));
            rig.Ticks(1);
            Assert.That(heard, Is.EqualTo(new[] { (0, -1, 1), (1, -1, 20), (2, 1, 11), (2, 2, 22), (2, 3, 30) }),
                "one call each, the last value, globals then tracks");
            Assert.That(told, Is.EqualTo(new[] { ("Level", 0), ("Gate", 2) }), "listeners told of each change");
            Assert.That(level.GetPValue(0), Is.EqualTo(level.NoValue), "pvals cleared after the tick");
            Assert.That(Enumerable.Range(0, 4).Select(gate.GetPValue), Is.All.EqualTo(gate.NoValue));
            Assert.That(level.GetValue(0), Is.EqualTo(20));
            Assert.That(hum.ParameterChanges.Pending(hum.ParameterGroupsList), Is.Empty);

            heard.Clear();
            rig.Ticks(2);
            Assert.That(heard, Is.Empty, "a change is sent once");
            gate.SetValue(0, 5);
            gate.SetValue(1 << 16 | 3, 6);                       // not recorded (as engine automation sets it): sent all the same
            rig.Ticks(1);
            Assert.That(heard, Is.EqualTo(new[] { (2, 0, 5), (2, 3, 6) }));

            var master = rig.Song.MachinesList[0];
            var connection = hum.AllOutputs.Single(c => c.Destination == master);
            var amp = master.ParameterGroupsList[0].ParametersList[0];
            int input = master.Inputs.IndexOf(connection);
            Assert.That(input, Is.GreaterThanOrEqualTo(0));
            amp.SetValue(input, 0x2000);
            Assert.That(connection.Amp, Is.EqualTo(0x2000), "an input's amp reaches its connection");
            connection.Amp = 0x3000;
            Assert.That(amp.GetValue(input), Is.EqualTo(0x3000), "a connection's amp reaches its input");
        }

        /// <summary>
        /// When steady playback allocates (it did now and then on CI), what: the runtime samples
        /// an allocation about every 100 KB (GCAllocationTick, with the type's name), so keep
        /// reading until there are a few samples.
        /// </summary>
        static string AllocatedTypes(Driver.AudioReader reader, Action? perBuffer = null)
        {
            using var listener = new AllocationListener();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000 && listener.Count < 5; i++)
            {
                perBuffer?.Invoke();
                reader.Read(256);
            }
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
            // The song loops over its first two bars (each track's pattern changes at the second, the session clips
            // are a bar long), so every pass of the loop is the same work: the guard's reps (Throughput.cs).
            rig.Song.LoopEnd = 2 * Bar;
            var reader = rig.Driver.CreateAudioReader(256);
            rig.Core.Playing = true;
            for (int i = 0; i < 100; i++)
                reader.Read(256);

            // Each buffer's time over a stretch, for the record.
            const int buffers = 2000;
            var times = new double[buffers];
            for (int i = 0; i < buffers; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                reader.Read(256);
                times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            double budgetMs = 256 * 1000.0 / ReBuzzCore.masterInfo.SamplesPerSec;
            var sorted = times.OrderBy(t => t).ToArray();
            TestContext.Out.WriteLine($"[notice] {machines} machines, {machines} tracks x 32 events, half playing session clips: " +
                $"per 256-frame buffer (budget {budgetMs:F2} ms): median {sorted[buffers / 2]:F3} ms, 99th {sorted[buffers * 99 / 100]:F3} ms, max {sorted[^1]:F3} ms");

            // The guard: a pass of the loop (less a buffer) in chunks of whole buffers, each rep from the loop's start,
            // timed against the reference workload in the same run.
            var mi = ReBuzzCore.masterInfo;
            int rate = mi.SamplesPerSec;
            int loop = 2 * Bar * mi.SamplesPerTick;
            void ReadFrames(int frames)
            {
                for (; frames > 0; frames -= 256) reader.Read(Math.Min(256, frames));
            }
            (int Tick, int InTick)? first = null;
            Throughput.Guard($"{machines} machines, {machines} tracks x 32 events, half playing session clips, the song's first two bars looping",
                Throughput.Floor(x64: 25, arm64: 25), (loop - 256) / (double)rate, rate, 256, () =>
            {
                // To the loop's start: the next tick boundary, then whole ticks until the song is back at it, so that
                // every rep starts in the same place.
                var now = ReBuzzCore.masterInfo;
                ReadFrames(now.PosInTick == 0 ? 0 : now.SamplesPerTick - now.PosInTick);
                for (int guard = 0; rig.Song.PlayPosition != rig.Song.LoopStart && guard < 4 * Bar; guard++)
                    ReadFrames(ReBuzzCore.masterInfo.SamplesPerTick);
                var here = (rig.Song.PlayPosition, ReBuzzCore.masterInfo.PosInTick);
                first ??= here;
                Assert.That(here, Is.EqualTo(first.Value), "every rep starts at the loop's start");
                return Throughput.InBlocks(256, (pos, n) => reader.Read(n));
            }, line => TestContext.Out.WriteLine("[notice] " + line));
        }

        /// <summary>
        /// History is live-safe (docs/specs/history-snapshots.md HS1b): while the heavy song above plays, 1,000 edits with undo,
        /// redo and history jumps between the buffers (the edits a person or an agent makes while it plays: tempo, pattern names,
        /// jumps over many steps) allocate nothing on the audio thread, and the worst buffer stays inside the real-time budget,
        /// the buffers' median within the throughput guard's own tolerance of the same song read without edits.
        /// </summary>
        [Test]
        public void ThousandEditsWithUndoRedoAndJumpsWhileTheBigSongPlaysAreLiveSafe()
        {
            const int machines = 32, buffers = 1200, edits = 1000;
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
            rig.Song.LoopEnd = 2 * Bar;
            var reader = rig.Driver.CreateAudioReader(256);
            rig.Core.Playing = true;
            var stack = rig.Song.ActionStack;
            var pattern = rig.Machines[0].PatternsList.First();
            int bpm = rig.Core.BPM;
            WarmUp(reader, "history live test");

            // The edits' own first run (their JIT and type loads) before anything is measured, then a clean history.
            int n = 0;
            void Edit()
            {
                switch (n++ % 5)
                {
                    case 0: TransportEdits.SetTempo(rig.Core, bpm + 1 + (n % 7)); break;
                    case 1: rig.Machines[0].RenamePattern(pattern, "P0 " + n); break;
                    case 2: if (stack.CanUndo) stack.Undo(); break;
                    case 3: if (stack.CanRedo) stack.Redo(); break;
                    default:
                        var steps = stack.Steps;
                        if (steps.Count > 3) stack.JumpTo(steps[(n * 7) % steps.Count].Id);   // undo or redo over many steps
                        break;
                }
            }
            for (int i = 0; i < 40; i++) { Edit(); reader.Read(256); }
            stack.Clear();
            n = 0;

            double[] Read(int count, Action? between)
            {
                var times = new double[count];
                for (int i = 0; i < count; i++)
                {
                    between?.Invoke();
                    long t0 = Stopwatch.GetTimestamp();
                    reader.Read(256);
                    times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                }
                return times;
            }
            double budgetMs = 256 * 1000.0 / ReBuzzCore.masterInfo.SamplesPerSec;
            var baseline = Read(buffers, null);

            using var window = new AllocationWindow(buffers);
            var log = new List<string>();
            long bytes = 0;
            var edited = new double[buffers];
            for (int i = 0; i < buffers; i++)
            {
                if (i < edits) Edit();                                  // the edit runs outside the timed read, as the UI thread does
                window.Begin();
                long t0 = Stopwatch.GetTimestamp();
                reader.Read(256);
                edited[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                long b = window.End();
                bytes += b;
                if (b > 0 && log.Count < 20) log.Add($"buffer {i}: {b} bytes");
            }

            double Median(double[] x) => x.OrderBy(v => v).ElementAt(x.Length / 2);
            double baseMedian = Median(baseline), editedMedian = Median(edited), editedWorst = edited.Max();
            TestContext.Out.WriteLine($"[notice] history live test: {stack.Steps.Count} steps kept; per 256-frame buffer (budget {budgetMs:F2} ms): " +
                $"baseline median {baseMedian:F3} ms, max {baseline.Max():F3} ms; with 1,000 edits median {editedMedian:F3} ms, max {editedWorst:F3} ms; " +
                $"{budgetMs / editedMedian:F1}× real time; allocated {bytes} bytes");

            Assert.That(bytes, Is.EqualTo(0), "history work must not allocate on the audio thread:\n" + string.Join("\n", log) + (bytes > 0 ? window.Describe() : ""));
            Assert.That(editedMedian, Is.LessThan(baseMedian * 1.5 + 0.05), "edits and undo must not make the audio thread slower");
            Assert.That(budgetMs / editedMedian, Is.GreaterThan(Throughput.Floor(x64: 25, arm64: 25)), "the throughput guard's floor, as the big song's guard has it");
            Assert.That(editedWorst, Is.LessThan(budgetMs), "the worst buffer stays inside its real-time budget");
        }

        /// <summary>
        /// Snapshots are live-safe (docs/specs/history-snapshots.md HS2): while the heavy song plays, a full snapshot is captured
        /// (UI thread) and stored (background) every 50 buffers. The audio thread allocates nothing, its median buffer stays within
        /// the throughput guard's tolerance of the same song without snapshots, and the UI-thread pause of a capture is reported.
        /// </summary>
        [Test]
        public void FullSnapshotsWhileTheBigSongPlaysAreLiveSafe()
        {
            const int machines = 32, buffers = 1200;
            string root = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "snapshots-" + Guid.NewGuid().ToString("N"));
            string oldRoot = ReBuzz.Core.SongHistory.SongSnapshots.UntitledRoot;
            ReBuzz.Core.SongHistory.SongSnapshots.UntitledRoot = root;
            try
            {
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
                rig.Song.LoopEnd = 2 * Bar;
                var reader = rig.Driver.CreateAudioReader(256);
                rig.Core.Playing = true;
                var snapshots = rig.Core.Snapshots;
                WarmUp(reader, "snapshot live test");
                snapshots.Capture("warm up");                           // the capture's own first run (JIT, type loads)
                snapshots.Flush();
                // The storing thread's allocations make the runtime collect now and then, and the first time a collection stops the reading
                // thread the runtime binds its own poll method there (a few hundred bytes that are the runtime's, not the audio path's):
                // let that happen here, with captures running beside the reads, before anything is measured.
                for (int round = 0; round < 8; round++)
                {
                    snapshots.Capture("warm up " + round);
                    for (int i = 0; i < 60; i++) { reader.Read(256); if (i % 20 == 0) GC.Collect(0); }
                }
                snapshots.Flush();
                for (int i = 0; i < 20; i++) reader.Read(256);

                double[] Read(int count, Action? between)
                {
                    var times = new double[count];
                    for (int i = 0; i < count; i++)
                    {
                        between?.Invoke();
                        long t0 = Stopwatch.GetTimestamp();
                        reader.Read(256);
                        times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    }
                    return times;
                }
                double budgetMs = 256 * 1000.0 / ReBuzzCore.masterInfo.SamplesPerSec;
                var baseline = Read(buffers, null);

                using var window = new AllocationWindow(buffers);
                var pauses = new List<double>();
                var edited = new double[buffers];
                long bytes = 0;
                var log = new List<string>();
                for (int i = 0; i < buffers; i++)
                {
                    if (i % 50 == 0)
                    {
                        long c0 = Stopwatch.GetTimestamp();
                        snapshots.Capture("live " + i);                  // the UI thread's part, between two buffers; the storing runs on its own thread
                        pauses.Add((Stopwatch.GetTimestamp() - c0) * 1000.0 / Stopwatch.Frequency);
                    }
                    window.Begin();
                    long t0 = Stopwatch.GetTimestamp();
                    reader.Read(256);
                    edited[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    long b = window.End();
                    bytes += b;
                    if (b > 0 && log.Count < 20) log.Add($"buffer {i}: {b} bytes");
                }
                snapshots.Flush();

                double Median(double[] x) => x.OrderBy(v => v).ElementAt(x.Length / 2);
                double baseMedian = Median(baseline), editedMedian = Median(edited), editedWorst = edited.Max();
                pauses.Sort();
                TestContext.Out.WriteLine($"[notice] snapshot live test: {pauses.Count} captures of the {machines}-machine song; UI-thread pause median {pauses[pauses.Count / 2]:F1} ms, max {pauses.Max():F1} ms; " +
                    $"per 256-frame buffer (budget {budgetMs:F2} ms): baseline median {baseMedian:F3} ms, with snapshots median {editedMedian:F3} ms, max {editedWorst:F3} ms; allocated {bytes} bytes");
                Assert.That(snapshots.List().Count(s => s.Name.StartsWith("live ")), Is.EqualTo(pauses.Count), "every capture was stored");

                Assert.That(bytes, Is.EqualTo(0), "snapshots must not allocate on the audio thread:\n" + string.Join("\n", log) + (bytes > 0 ? window.Describe() : ""));
                Assert.That(editedMedian, Is.LessThan(baseMedian * 1.5 + 0.05), "capturing and storing must not make the audio thread slower");
                // No absolute floor here: the runner's own baseline for this song is ~23x, and what matters is that snapshots add nothing to it.
            }
            finally
            {
                ReBuzz.Core.SongHistory.SongSnapshots.UntitledRoot = oldRoot;
                try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
            }
        }

        /// <summary>
        /// Fork (history-snapshots.md HS3): a snapshot restored while the 32-machine song plays. Some machines differ in shape (a pattern's length:
        /// they are built beside the playing ones and crossfaded in), some in their tracks and wire levels (swapped or ramped at the boundary).
        /// Asserts what the spec guards: the audio thread allocates nothing across the restore (the boundary included), the median and worst
        /// buffer stay as they were, and nothing clicks: the Hum machines output a constant, so the sum must stay constant through the swap
        /// (the old machine's fade-out and the new one's fade-in add up), measured as the largest step between consecutive samples.
        /// </summary>
        [Test]
        public void RestoringASnapshotWhileTheBigSongPlaysIsLiveSafeAndCrossfadesWithoutAClick()
        {
            const int machines = 32, swapped = 4, buffers = 700;
            string root = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "restore-" + Guid.NewGuid().ToString("N"));
            string oldRoot = ReBuzz.Core.SongHistory.SongSnapshots.UntitledRoot;
            ReBuzz.Core.SongHistory.SongSnapshots.UntitledRoot = root;
            try
            {
                using var rig = new Rig(machines, hum: true);
                for (int i = swapped; i < machines; i++)  // the first few have no patterns, so no pattern editor: they can be rebuilt live
                {
                    var m = rig.Machines[i];
                    var patterns = Enumerable.Range(0, 4).Select(k => rig.Pattern(m, "P" + k, Bar)).ToList();
                    for (int t = 0; t < 32; t++)
                        rig.Track(i).SetEvent(t * Bar, new SequenceEvent(SequenceEventType.PlayPattern, patterns[(t + i) % 4]));
                }
                rig.Song.LoopEnd = 2 * Bar;
                var reader = rig.Driver.CreateAudioReader(256);
                rig.Core.Playing = true;
                var snapshots = rig.Core.Snapshots;
                WarmUp(reader, "restore live test");
                var snap = snapshots.Capture("A");
                snapshots.Flush();

                // Moves the song away from A: shapes of some machines, tracks and wire levels of others.
                void Change()
                {
                    for (int i = 0; i < machines; i++)
                    {
                        var m = rig.Core.SongCore.MachinesList.First(x => x.Name == rig.Machines[i].Name);
                        if (i < swapped) rig.Core.SongCore.DeleteMachines(new[] { (BuzzGUI.Interfaces.IMachine)m });   // gone since A: built beside and faded in
                        else if (i < 12) rig.Core.SongCore.SequencesList.First(s => s.Machine == m).SetEvent(5 * Bar, null);
                        else if (i < 16) m.AllOutputs.First().Amp = 0x2000;
                    }
                }
                var windows = new List<(DateTime From, DateTime To, int Buffer)>();
                double[] Read(int count, Action? between, out double maxStep, out long allocated, bool tail = false)
                {
                    var times = new double[count];
                    maxStep = 0;
                    allocated = 0;
                    using var window = new AllocationWindow(count);
                    float prev = float.NaN;
                    for (int i = 0; i < count; i++)
                    {
                        between?.Invoke();
                        window.Begin();
                        var wall0 = DateTime.UtcNow;
                        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                        bool liveBefore = snapshots.LiveActive;
                        long t0 = Stopwatch.GetTimestamp();
                        reader.Read(256);
                        times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                        if (times[i] > 3.0)
                            TestContext.Out.WriteLine($"[notice] restore live test: slow buffer {i} of {count}: {times[i]:F2} ms; GCs during it gen0 +{GC.CollectionCount(0) - gc0} gen1 +{GC.CollectionCount(1) - gc1} gen2 +{GC.CollectionCount(2) - gc2}; restore live before/after: {liveBefore}/{snapshots.LiveActive}");
                        long got = window.End();
                        allocated += got;
                        if (got != 0) { TestContext.Out.WriteLine($"[notice] restore live test: buffer {i} allocated {got} bytes"); windows.Add((wall0, DateTime.UtcNow, i)); }
                        var b = reader.Buffer;
                        for (int k = 0; k < 256; k++)
                        {
                            float l = b[2 * k];
                            if (!float.IsNaN(prev)) maxStep = Math.Max(maxStep, Math.Abs(l - prev));
                            prev = l;
                        }
                    }
                    return times;
                }

                // Two rounds to warm the restore's code (JIT, first use), then the measured one.
                for (int round = 0; round < 2; round++)
                {
                    Change();
                    var warm = snapshots.Restore(snap, ReBuzz.Core.SongHistory.RestoreQuantize.Beat);
                    Assert.That(warm.Mode, Is.EqualTo("live"), warm.Reason);
                    for (int i = 0; i < 400 && snapshots.LiveActive; i++) { reader.Read(256); snapshots.Pump(); Thread.Sleep(1); }
                    Assert.That(snapshots.LiveActive, Is.False, "the warm-up restore finished");
                    for (int i = 0; i < 100; i++) reader.Read(256);
                }
                double budgetMs = 256 * 1000.0 / ReBuzzCore.masterInfo.SamplesPerSec;
                var baseline = Read(buffers, null, out double baseStep, out long baseBytes);

                Change();
                RestoreResultHolder holder = new();
                using var types = new AllocationTypes();
                bool started = false;
                var during = Read(buffers, () =>
                {
                    if (!started) { started = true; holder.Result = snapshots.Restore(snap, ReBuzz.Core.SongHistory.RestoreQuantize.Beat); }
                    snapshots.Pump();
                    Thread.Sleep(1);                         // the finish waits 100 ms of wall time for the fades; the reads alone run faster than real time
                }, out double restoreStep, out long bytes);
                Assert.That(holder.Result!.Mode, Is.EqualTo("live"), holder.Result.Reason);
                Assert.That(snapshots.LiveActive, Is.False, "it applied and finished within the stretch");

                double Median(double[] x) => x.OrderBy(v => v).ElementAt(x.Length / 2);
                double levelNow = Math.Abs(reader.Buffer[0]);
                TestContext.Out.WriteLine($"[notice] restore live test: {machines} machines, {swapped} swapped; per 256-frame buffer (budget {budgetMs:F2} ms): " +
                    $"baseline median {Median(baseline):F3} ms max {baseline.Max():F3} ms; restoring median {Median(during):F3} ms max {during.Max():F3} ms; allocated {bytes} bytes (baseline {baseBytes}); " +
                    $"largest sample-to-sample step: baseline {baseStep:G4}, across the restore {restoreStep:G4} (level {levelNow:G4})");

                if (bytes != 0) TestContext.Out.WriteLine("[notice] restore live test: allocation ticks inside the allocating buffers: " + string.Join("; ", types.Ticks.Where(k => windows.Any(w => k.Time >= w.From.AddMilliseconds(-2) && k.Time <= w.To.AddMilliseconds(2))).Select(k => k.Text)) + $" (of {types.Ticks.Count} ticks in all)");
                Assert.That(bytes, Is.EqualTo(0), "a restore must not allocate on the audio thread");
                Assert.That(snapshots.Diff(snap, null), Is.Empty, "the song is what the snapshot holds");
                Assert.That(rig.Core.SongCore.Machines.Count(m => m.DLL.Info.Type != BuzzGUI.Interfaces.MachineType.Master), Is.EqualTo(machines));
                Assert.That(Median(during), Is.LessThan(Median(baseline) * 1.5 + 0.05), "restoring must not make the audio thread slower");
                Assert.That(during.Max(), Is.LessThan(budgetMs), "no buffer misses its real-time budget");
                Assert.That(restoreStep, Is.LessThanOrEqualTo(baseStep + Math.Max(1e-3 * levelNow, 1e-6)), "no click: the swap's fade-out and fade-in add up to the level that was playing");
            }
            finally
            {
                ReBuzz.Core.SongHistory.SongSnapshots.UntitledRoot = oldRoot;
                try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
            }
        }

        sealed class RestoreResultHolder { public ReBuzz.Core.SongHistory.RestoreResult? Result; }

        /// <summary>Names the types behind an allocation (the runtime's GC allocation ticks, one per ~100 KB), for the live restore test's diagnosis.</summary>
        sealed class AllocationTypes : System.Diagnostics.Tracing.EventListener
        {
            public readonly System.Collections.Concurrent.ConcurrentQueue<(DateTime Time, string Text)> Ticks = new();
            protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
            {
                if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)1);
            }
            protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
            {
                if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick")) return;
                int t = e.PayloadNames?.IndexOf("TypeName") ?? -1, a = e.PayloadNames?.IndexOf("AllocationAmount64") ?? -1;
                Ticks.Enqueue((e.TimeStamp.ToUniversalTime(), $"{(t >= 0 ? e.Payload![t] : "?")} ({(a >= 0 ? e.Payload![a] : "?")} bytes)"));
            }
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
