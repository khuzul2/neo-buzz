using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: Session View 0.3 (docs/specs/session-view.md): recording the session into
    /// the arrangement and scene to arrangement. Uses the Tone test machine, whose output shows
    /// which pattern plays and where, so audio from the session and from the arrangement can be
    /// compared sample by sample.
    /// </summary>
    public class SessionArrangementTests
    {
        const int Beat = 4;    // default 4 ticks per beat

        sealed class Rig : IDisposable
        {
            public Driver Driver { get; } = new Driver();
            public ReBuzzCore Core => Driver.Core;
            public MachineCore A { get; }
            public MachineCore B { get; }
            public SequenceCore TrackA => Core.SongCore.SequencesList.Single(s => s.Machine == A);
            public SequenceCore TrackB => Core.SongCore.SequencesList.Single(s => s.Machine == B);
            public ISession Session => Core.Session;
            public SessionLayout Layout => Core.SessionLayout;

            public Rig()
            {
                var a = ToneController.NewInstance("ToneA");
                var b = ToneController.NewInstance("ToneB");
                Driver.Gear.AddDynamicGenerator(ToneController.Info);
                Driver.Start();
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(a);
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(b);
                A = Driver.MachineFor(a);
                B = Driver.MachineFor(b);
                foreach (var m in new[] { A, B })
                {
                    var song = Core.SongCore;
                    if (!song.SequencesList.Any(s => s.Machine == m))
                        song.AddSequence(m, song.SequencesList.Count);
                }
            }

            /// <summary>A pattern (the Tone machine's output shows which one plays, and where).</summary>
            public PatternCore Pattern(MachineCore machine, string name, int length, params (int Tick, int Level)[] unused)
            {
                machine.CreatePattern(name, length);
                return machine.PatternsList.Single(p => p.Name == name);
            }

            int SamplesPerTick => ReBuzzCore.masterInfo.SamplesPerTick;

            /// <summary>
            /// Reads up to the next tick boundary (a whole tick if already on one), so later reads
            /// cover whole ticks. With the transport stopped this also clears the pattern play
            /// positions, which are left over from the last tick played (or 0 in new patterns)
            /// until the engine runs once.
            /// </summary>
            public void Align()
            {
                var mi = ReBuzzCore.masterInfo;
                Driver.ReadStereoSamples(mi.SamplesPerTick - mi.PosInTick);
            }

            /// <summary>Renders whole ticks and returns their samples.</summary>
            public List<float> Ticks(int count)
            {
                var all = new List<float>();
                for (int i = 0; i < count; i++)
                    all.AddRange(Driver.ReadStereoSamples(SamplesPerTick).Values);
                return all;
            }

            public void Dispose()
            {
                Core.Recording = false;
                Core.Playing = false;
                Driver.Dispose();
            }
        }

        static void AssertSameAudio(List<float> expected, List<float> actual, int samplesPerTick)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count), "length");
            for (int i = 0; i < expected.Count; i++)
            {
                if (expected[i] != actual[i])
                    Assert.Fail($"Audio differs at tick {i / 2 / samplesPerTick} (sample {i / 2}): recorded {expected[i]}, played back {actual[i]}");
            }
        }

        static Dictionary<int, string> Events(ISequence seq) =>
            seq.Events.ToDictionary(e => e.Key, e => e.Value.Type == SequenceEventType.PlayPattern ? e.Value.Pattern.Name : e.Value.Type.ToString());

        /// <summary>
        /// Records a short performance (launch, switch, stop, relaunch, running past the song
        /// end) and returns its audio. The transport is stopped at the end.
        /// </summary>
        static List<float> RecordPerformance(Rig rig, PatternCore a1, PatternCore a2, PatternCore b1)
        {
            rig.Session.LaunchQuantization = LaunchQuantization.Beat;
            rig.Align();
            rig.Core.Recording = true;                 // arms and starts the transport on the next tick
            rig.Session.Launch(rig.TrackA, a1);
            rig.Session.Launch(rig.TrackB, b1);

            var audio = rig.Ticks(21);                 // first tick: silence before the start
            rig.Session.Launch(rig.TrackA, a2);        // next beat
            audio.AddRange(rig.Ticks(20));
            rig.Session.Stop(rig.TrackB);
            audio.AddRange(rig.Ticks(12));
            rig.Session.Launch(rig.TrackA, a1);
            audio.AddRange(rig.Ticks(30));             // well past the song end (16)

            rig.Core.Recording = false;
            audio.AddRange(rig.Ticks(1));              // the pass ends on this tick
            rig.Core.Playing = false;
            return audio;
        }

        [Test]
        public void ARecordedPerformancePlaysBackIdenticallyFromTheArrangement()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8, (0, 10), (4, 20));
            var a2 = rig.Pattern(rig.A, "a2", 6, (0, 30), (3, 40));
            var b1 = rig.Pattern(rig.B, "b1", 16, (0, 5), (8, 15), (12, 0));
            rig.Core.Looping = false;

            var recorded = RecordPerformance(rig, a1, a2, b1);

            Assert.That(recorded.Distinct().Count(), Is.GreaterThan(4), "the performance isn't silent");
            Assert.That(rig.Core.SongCore.LoopEnd, Is.GreaterThanOrEqualTo(84), "the song grew while recording");
            Assert.That(rig.TrackA.Events.Count, Is.GreaterThan(3));
            Assert.That(Events(rig.TrackB).Values, Does.Contain("Off"), "the stop is written");
            Assert.That(rig.Session.IsAnyArrangementOverridden, Is.False, "stopping the transport hands tracks back");

            rig.Core.SongCore.PlayPosition = 0;
            rig.Align();
            rig.Core.Playing = true;
            var played = rig.Ticks(recorded.Count / 2 / ReBuzzCore.masterInfo.SamplesPerTick);

            AssertSameAudio(recorded, played, ReBuzzCore.masterInfo.SamplesPerTick);
        }

        [Test]
        public void RecordingWritesStartsLoopsAndStopsAtTheirSongPositions()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8, (0, 10));
            var a2 = rig.Pattern(rig.A, "a2", 6, (0, 30));
            var b1 = rig.Pattern(rig.B, "b1", 16, (0, 5));
            rig.Core.Looping = false;

            RecordPerformance(rig, a1, a2, b1);

            // The a2 launch comes at clock 20 and starts on the next beat (24); B's stop at 40
            // takes effect at 44; the a1 relaunch at 52 starts at 56. Loops restart every
            // pattern length; the pass ends at 83.
            Assert.That(Events(rig.TrackA), Is.EqualTo(new Dictionary<int, string>
            {
                [0] = "a1", [8] = "a1", [16] = "a1",
                [24] = "a2", [30] = "a2", [36] = "a2", [42] = "a2", [48] = "a2", [54] = "a2",
                [56] = "a1", [64] = "a1", [72] = "a1", [80] = "a1",
            }));
            Assert.That(Events(rig.TrackB), Is.EqualTo(new Dictionary<int, string>
            {
                [0] = "b1", [16] = "b1", [32] = "b1", [44] = "Off",
            }));
        }

        [Test]
        public void RecordingReplacesTheArrangementBetweenItsWrites()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8, (0, 10));
            var old = rig.Pattern(rig.A, "old", 4, (0, 99));
            rig.TrackA.SetEvent(2, new SequenceEvent(SequenceEventType.PlayPattern, old));
            rig.TrackA.SetEvent(12, new SequenceEvent(SequenceEventType.PlayPattern, old));
            rig.TrackB.SetEvent(4, new SequenceEvent(SequenceEventType.PlayPattern, old.Machine == rig.B ? old : rig.Pattern(rig.B, "bOld", 4, (0, 1))));
            rig.Core.Looping = false;
            rig.Session.LaunchQuantization = LaunchQuantization.None;

            rig.Align();
            rig.Core.Recording = true;
            rig.Session.Launch(rig.TrackA, a1);
            rig.Ticks(19);
            rig.Core.Recording = false;
            rig.Ticks(1);
            rig.Core.Playing = false;

            var a = Events(rig.TrackA);
            Assert.That(a.Keys.OrderBy(k => k), Is.EqualTo(new[] { 0, 8, 16 }), "the old events on the recorded track are gone");
            Assert.That(Events(rig.TrackB)[4], Is.EqualTo("bOld"), "tracks the session didn't play keep their arrangement");
        }

        [Test]
        public void UndoTakesBackAWholeRecordingPass()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8, (0, 10));
            var a2 = rig.Pattern(rig.A, "a2", 6, (0, 30));
            var b1 = rig.Pattern(rig.B, "b1", 16, (0, 5));
            rig.Core.Looping = false;
            rig.Layout.Ensure();
            RecordPerformance(rig, a1, a2, b1);
            var recordedA = Events(rig.TrackA);
            int recordedEnd = rig.Core.SongCore.LoopEnd;

            rig.Layout.Undo();
            Assert.That(rig.TrackA.Events, Is.Empty);
            Assert.That(rig.TrackB.Events, Is.Empty);
            Assert.That(rig.Core.SongCore.LoopEnd, Is.EqualTo(16));

            rig.Layout.Redo();
            Assert.That(Events(rig.TrackA), Is.EqualTo(recordedA));
            Assert.That(rig.Core.SongCore.LoopEnd, Is.EqualTo(recordedEnd));
        }

        [Test]
        public void WithLoopingOnRecordingStaysInTheLoop()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8, (0, 10));
            rig.Core.Looping = true;
            rig.Session.LaunchQuantization = LaunchQuantization.None;

            rig.Align();
            rig.Core.Recording = true;
            rig.Session.Launch(rig.TrackA, a1);
            rig.Ticks(40);
            rig.Core.Recording = false;
            rig.Ticks(1);

            Assert.That(rig.Core.SongCore.LoopEnd, Is.EqualTo(16));
            Assert.That(rig.TrackA.Events.Keys.All(k => k < 16), Is.True);
        }

        // ------------------------------------------------------------------ scene to arrangement

        [Test]
        public void SceneOverwritesItsTracksForTheGivenLength()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8, (0, 10));
            var b1 = rig.Pattern(rig.B, "b1", 12, (0, 5));
            var other = rig.Pattern(rig.A, "other", 4, (0, 1));
            rig.TrackA.SetEvent(20, new SequenceEvent(SequenceEventType.PlayPattern, other));
            rig.TrackA.SetEvent(60, new SequenceEvent(SequenceEventType.PlayPattern, other));
            rig.Layout.Ensure();     // auto-fill: scene 1 = a1, b1

            int tracks = SceneToArrangement.Insert(rig.Core, rig.Layout, 0, 16, 32, insert: false);

            Assert.That(tracks, Is.EqualTo(2));
            Assert.That(Events(rig.TrackA), Is.EqualTo(new Dictionary<int, string>
            {
                [16] = "a1", [24] = "a1", [32] = "a1", [40] = "a1", [60] = "other"
            }));
            Assert.That(Events(rig.TrackB), Is.EqualTo(new Dictionary<int, string>
            {
                [16] = "b1", [28] = "b1", [40] = "b1", [48] = "Break"     // last repeat cut at the end
            }));
            Assert.That(rig.Core.SongCore.SongEnd, Is.EqualTo(48));
            Assert.That(rig.Core.SongCore.LoopEnd, Is.EqualTo(48), "a loop over the whole song grows with it");

            rig.Layout.Undo();
            Assert.That(Events(rig.TrackA), Is.EqualTo(new Dictionary<int, string> { [20] = "other", [60] = "other" }));
            Assert.That(rig.TrackB.Events, Is.Empty);
            Assert.That(rig.Core.SongCore.SongEnd, Is.EqualTo(16));
        }

        [Test]
        public void SceneInsertMovesLaterEventsOnEveryTrack()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8, (0, 10));
            var b1 = rig.Pattern(rig.B, "b1", 8, (0, 5));
            rig.TrackA.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, a1));
            rig.TrackB.SetEvent(8, new SequenceEvent(SequenceEventType.PlayPattern, b1));
            rig.Layout.Ensure();
            rig.Layout.ClearSlot(1, 0);              // scene 1 = a1 only

            SceneToArrangement.Insert(rig.Core, rig.Layout, 0, 8, 16, insert: true);

            Assert.That(Events(rig.TrackA), Is.EqualTo(new Dictionary<int, string> { [0] = "a1", [8] = "a1", [16] = "a1" }));
            Assert.That(Events(rig.TrackB), Is.EqualTo(new Dictionary<int, string> { [24] = "b1" }), "moved back by the inserted length");
            Assert.That(rig.Core.SongCore.SongEnd, Is.EqualTo(32));
        }

        [Test]
        public void SceneLengthIsTheLongestClipInWholeBars()
        {
            using var rig = new Rig();
            rig.Pattern(rig.A, "a1", 8, (0, 10));
            rig.Pattern(rig.B, "b1", 20, (0, 5));
            rig.Layout.Ensure();

            Assert.That(SceneToArrangement.SceneLength(rig.Layout, 0, 16), Is.EqualTo(32));
        }

        // ------------------------------------------------------------------ arrangement events on session tracks

        [Test]
        public void ATrackPlayedFromTheSessionIgnoresArrangementOffEvents()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8, (0, 10));
            rig.TrackA.SetEvent(4, new SequenceEvent(SequenceEventType.Mute));
            rig.Session.LaunchQuantization = LaunchQuantization.None;

            rig.Align();
            rig.Session.Launch(rig.TrackA, a1);
            rig.Ticks(8);

            Assert.That(rig.A.IsSeqMute, Is.False, "the session owns the track; its arrangement Mute doesn't apply");
        }
    }
}
