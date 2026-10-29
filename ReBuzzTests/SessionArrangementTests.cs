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

        // ------------------------------------------------------------------ 0.5.1: regions and velocity in the arrangement

        static void Set(SessionLayout layout, IPattern pattern, Action<SessionDocument.ClipSettings> change)
        {
            var c = layout.ClipOf(pattern);
            change(c);
            layout.SetClipSettings(pattern, c);
        }

        [Test]
        public void APerformanceWithClipPropertiesPlaysBackIdenticallyFromTheArrangement()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8);
            var a2 = rig.Pattern(rig.A, "a2", 8);
            var b1 = rig.Pattern(rig.B, "b1", 16);
            rig.Core.Looping = false;
            var layout = rig.Layout;
            layout.Ensure();
            Set(layout, a1, c => { c.Start = 2; c.LoopStart = 4; c.LoopEnd = 7; });     // 2-6, then 4-6 over and over
            Set(layout, a2, c => c.Legato = true);
            Set(layout, b1, c => { c.Loop = false; c.Start = 3; c.LoopEnd = 13; c.Velocity = 100; });   // rows 3-12 once
            rig.Session.LaunchQuantization = LaunchQuantization.Beat;

            rig.Align();
            rig.Core.Recording = true;
            layout.LaunchClip(rig.TrackA, a1);
            layout.LaunchClip(rig.TrackB, b1, velocity: 64);
            var recorded = rig.Ticks(13);
            layout.LaunchClip(rig.TrackA, a2);                 // legato: picks up where a1 is
            recorded.AddRange(rig.Ticks(10));
            layout.LaunchClip(rig.TrackB, b1);                 // again, at full velocity
            recorded.AddRange(rig.Ticks(14));
            rig.Core.Recording = false;
            recorded.AddRange(rig.Ticks(1));
            rig.Core.Playing = false;

            var a = rig.TrackA.Events.OrderBy(e => e.Key).ToList();
            Assert.That((a[0].Key, a[0].Value.Pattern, a[0].Value.Offset, a[0].Value.End), Is.EqualTo((0, (IPattern)a1, 2, 7)), "from the start marker to the loop end");
            Assert.That((a[1].Key, a[1].Value.Offset, a[1].Value.End), Is.EqualTo((5, 4, 7)), "then the loop region");
            var legato = a.First(e => e.Value.Pattern == a2).Value;
            Assert.That(legato.Offset, Is.InRange(4, 6), "a2 starts where a1 was");
            var b = rig.TrackB.Events.OrderBy(e => e.Key).ToList();
            Assert.That((b[0].Value.Offset, b[0].Value.End), Is.EqualTo((3, 13)));
            Assert.That(b[0].Value.Gain, Is.EqualTo(64 / 127.0).Within(1e-6), "launch velocity");
            Assert.That(b[1].Key, Is.EqualTo(10));
            Assert.That(b[1].Value.Type, Is.EqualTo(SequenceEventType.Off), "the one-shot clip ended");
            Assert.That(b.Last(e => e.Value.Pattern == b1).Value.Gain, Is.EqualTo(1.0));

            rig.Core.SongCore.PlayPosition = 0;
            rig.Align();
            rig.Core.Playing = true;
            var played = rig.Ticks(recorded.Count / 2 / ReBuzzCore.masterInfo.SamplesPerTick);

            AssertSameAudio(recorded, played, ReBuzzCore.masterInfo.SamplesPerTick);
        }

        [Test]
        public void RecordingThatStartsMidClipWritesWhereTheClipIs()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8);
            rig.Core.Looping = false;
            var layout = rig.Layout;
            layout.Ensure();
            Set(layout, a1, c => { c.LoopStart = 2; c.LoopEnd = 6; });                // 0-5, then 2-5
            rig.Session.LaunchQuantization = LaunchQuantization.None;

            rig.Align();
            layout.LaunchClip(rig.TrackA, a1);
            rig.Ticks(8);                                      // a1 is in its loop region now
            rig.Core.Recording = true;
            rig.Ticks(9);
            rig.Core.Recording = false;
            rig.Ticks(1);
            rig.Core.Playing = false;

            var a = rig.TrackA.Events.OrderBy(e => e.Key).ToList();
            Assert.That(a.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(a.All(e => e.Value.Pattern == a1 && e.Value.End == 6), Is.True);
            Assert.That(a[0].Value.Offset, Is.InRange(3, 5), "written from where the clip was, not from its first row");
            for (int i = 1; i < a.Count; i++)
            {
                Assert.That(a[i].Value.Offset, Is.EqualTo(2), "loop wraps start at the loop start");
                Assert.That(a[i].Key - a[i - 1].Key, Is.EqualTo(6 - a[i - 1].Value.Offset), "each plays to the loop end");
            }
        }

        [Test]
        public void ArrangementEventsKeepTheirRegionAndVolumeInSongFiles()
        {
            foreach (var extension in new[] { ".bmx", ".bmxml" })
            {
                using var rig = new Rig();
                var a1 = rig.Pattern(rig.A, "a1", 8);
                var b1 = rig.Pattern(rig.B, "b1", 8);
                rig.TrackA.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, a1) { Offset = 2, End = 6, Gain = 0.25 });
                rig.TrackA.SetEvent(8, new SequenceEvent(SequenceEventType.PlayPattern, a1));
                rig.TrackB.SetEvent(4, new SequenceEvent(SequenceEventType.PlayPattern, b1) { Gain = 0.5 });
                Assert.That(rig.TrackA.Events[0].Span, Is.EqualTo(4), "plays rows 2-5");

                string path = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "regions-" + Guid.NewGuid().ToString("N") + extension);
                rig.Core.SaveSongCopy(path);
                rig.Core.Modified = false;
                rig.Core.OpenSongFile(path);

                var song = rig.Core.SongCore;
                var trackA = song.SequencesList.Single(s => s.Machine.Name == "ToneA");
                var trackB = song.SequencesList.Single(s => s.Machine.Name == "ToneB");
                var first = trackA.Events[0];
                Assert.That((first.Offset, first.End, first.Gain, first.Span), Is.EqualTo((2, 6, 0.25, 4)), extension);
                Assert.That(trackA.Events[8].IsPlain, Is.True, extension);
                Assert.That(trackB.Events[4].Gain, Is.EqualTo(0.5), extension);
            }
        }

        [Test]
        public void PlainArrangementsSaveNoForkData()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8);
            rig.TrackA.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, a1));
            Assert.That(ArrangementEventStore.ToXml(rig.Core.SongCore.Sequences), Is.Null, "upstream-identical files when nothing needs it");
        }

        [Test]
        public void SequenceEditorUndoKeepsTheRegion()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8);
            rig.TrackA.SetEvent(4, new SequenceEvent(SequenceEventType.PlayPattern, a1) { Offset = 3, End = 7, Gain = 0.5 });

            var clear = new BuzzGUI.Common.Actions.SequenceActions.ClearAction(rig.TrackA, 0, 16);
            clear.Do();
            Assert.That(rig.TrackA.Events, Is.Empty);
            clear.Undo();

            var e = rig.TrackA.Events[4];
            Assert.That((e.Offset, e.End, e.Gain, e.Span), Is.EqualTo((3, 7, 0.5, 4)));
            Assert.That(e.Describe(), Is.EqualTo("a1 3–7 50%"));
        }

        [Test]
        public void TheArrangementPlaysAnEventFromItsOffsetToItsEnd()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8);
            rig.TrackA.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, a1) { Offset = 3, End = 6 });
            rig.Core.Looping = false;

            rig.Align();
            rig.Core.Playing = true;
            var rows = new List<int?>();
            for (int i = 0; i < 5; i++)
            {
                rig.Ticks(1);
                rows.Add(a1.PlayPosition == int.MinValue ? null : a1.PlayPosition / PatternEvent.TimeBase);
            }
            Assert.That(rows, Is.EqualTo(new int?[] { 3, 4, 5, null, null }));
        }

        [Test]
        public void SceneToArrangementFollowsClipMarkers()
        {
            using var rig = new Rig();
            var a1 = rig.Pattern(rig.A, "a1", 8);
            var b1 = rig.Pattern(rig.B, "b1", 12);
            var layout = rig.Layout;
            layout.Ensure();
            Set(layout, a1, c => { c.Start = 2; c.LoopStart = 4; c.LoopEnd = 7; });
            Set(layout, b1, c => { c.Loop = false; c.Start = 3; c.LoopEnd = 9; });

            Assert.That(SceneToArrangement.SceneLength(layout, 0, 16), Is.EqualTo(16), "the first pass: 5 and 6 ticks");
            SceneToArrangement.Insert(rig.Core, layout, 0, 16, 16, insert: false);

            Assert.That(rig.TrackA.Events.OrderBy(e => e.Key).Select(e => (e.Key, e.Value.Type, e.Value.Offset, e.Value.End)), Is.EqualTo(new[]
            {
                (16, SequenceEventType.PlayPattern, 2, 7),
                (21, SequenceEventType.PlayPattern, 4, 7),
                (24, SequenceEventType.PlayPattern, 4, 7),
                (27, SequenceEventType.PlayPattern, 4, 7),
                (30, SequenceEventType.PlayPattern, 4, 7),
                (32, SequenceEventType.Break, 0, 0),
            }));
            Assert.That(rig.TrackB.Events.Select(e => (e.Key, e.Value.Offset, e.Value.End)), Is.EqualTo(new[] { (16, 3, 9) }), "a one-shot clip plays once");
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
