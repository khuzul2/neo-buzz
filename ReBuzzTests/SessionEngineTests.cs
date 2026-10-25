using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: Session View engine (docs/specs/session-view.md, 0.1) driven through the
    /// real audio loop (WorkManager), one tick at a time. The scheduling rules themselves are
    /// covered in detail by ReBuzz.SessionTests; these check the wiring into ReBuzz.
    /// </summary>
    public class SessionEngineTests
    {
        const int TimeBase = PatternEvent.TimeBase;
        const int Bar = 16;   // default 4 ticks per beat x 4 beats per bar

        sealed class Rig : IDisposable
        {
            public Driver Driver { get; } = new Driver();
            public ReBuzzCore Core => Driver.Core;
            public MachineCore Synth { get; private set; } = null!;
            public SequenceCore Track { get; private set; } = null!;
            public ISession Session => Core.Session;

            public Rig()
            {
                var synth = SynthController.NewInstance("synth");
                Driver.Gear.AddDynamicGenerator(SynthController.Info);
                Driver.Start();
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(synth);
                Synth = Driver.MachineFor(synth);

                var song = Core.SongCore;
                Track = song.SequencesList.FirstOrDefault(s => s.Machine == Synth)!;
                if (Track == null)
                {
                    song.AddSequence(Synth, song.SequencesList.Count);
                    Track = song.SequencesList.Last();
                }
                Core.Looping = true;
            }

            public PatternCore NewPattern(string name, int length)
            {
                Synth.CreatePattern(name, length);
                return Synth.PatternsList.Single(p => p.Name == name);
            }

            /// <summary>Runs the audio loop up to and through the next <paramref name="ticks"/> tick boundaries.</summary>
            public void RunTicks(int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    var mi = ReBuzzCore.masterInfo;
                    Driver.ReadStereoSamples(Math.Max(1, mi.SamplesPerTick - mi.PosInTick));
                }
            }

            /// <summary>Position of a pattern in whole ticks, or null when it isn't playing.</summary>
            public static int? TickOf(PatternCore p) =>
                p.PlayPosition == int.MinValue ? null : p.PlayPosition / TimeBase;

            public void Dispose()
            {
                Core.Playing = false;
                Driver.Dispose();
            }
        }

        [Test]
        public void LaunchWhileStoppedStartsTheTransportAndPlaysFromTheTop()
        {
            using var rig = new Rig();
            var clip = rig.NewPattern("clip", Bar);

            rig.Session.Launch(rig.Track, clip);
            Assert.That(rig.Session.GetState(rig.Track, clip), Is.EqualTo(ClipState.Queued));

            rig.RunTicks(1);
            Assert.That(rig.Core.Playing, Is.True);
            Assert.That(rig.Track.PlayingPattern, Is.SameAs(clip));
            Assert.That(Rig.TickOf(clip), Is.EqualTo(0));

            rig.RunTicks(5);
            Assert.That(Rig.TickOf(clip), Is.EqualTo(5));
            Assert.That(rig.Session.GetState(rig.Track, clip), Is.EqualTo(ClipState.Playing));
        }

        [Test]
        public void OddLengthClipLoopsFromItsLaunchPoint()
        {
            // G1: upstream positioned triggered patterns at songPosition % length.
            using var rig = new Rig();
            var clip = rig.NewPattern("odd", 12);

            rig.Session.Launch(rig.Track, clip, new LaunchOptions { Quantization = LaunchQuantization.None });
            rig.RunTicks(1);

            var ticks = new List<int?>();
            for (int i = 0; i < 26; i++)
            {
                ticks.Add(Rig.TickOf(clip));
                rig.RunTicks(1);
            }
            Assert.That(ticks, Is.EqualTo(Enumerable.Range(0, 26).Select(i => (int?)(i % 12)).ToList()));
        }

        [Test]
        public void LaunchWaitsForTheNextBarAndOverridesTheArrangement()
        {
            using var rig = new Rig();
            var arranged = rig.NewPattern("arranged", 64);
            var clip = rig.NewPattern("clip", Bar);
            rig.Core.SongCore.LoopEnd = 64;
            rig.Track.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, arranged));

            rig.Core.Playing = true;
            rig.RunTicks(5);                              // the first boundary starts the clock at 0: clock 4
            Assert.That(rig.Track.PlayingPattern, Is.SameAs(arranged), "arrangement plays before the launch");

            rig.Session.Launch(rig.Track, clip);         // bar quantization: starts at 16
            rig.RunTicks(11);                             // clock 15
            Assert.That(rig.Track.PlayingPattern, Is.SameAs(arranged));
            Assert.That(rig.Session.IsArrangementOverridden(rig.Track), Is.False);

            rig.RunTicks(1);                              // tick 16
            Assert.That(rig.Track.PlayingPattern, Is.SameAs(clip));
            Assert.That(Rig.TickOf(clip), Is.EqualTo(0));
            Assert.That(Rig.TickOf(arranged), Is.Null, "G3: the arranged pattern must not play alongside");
            Assert.That(rig.Session.IsArrangementOverridden(rig.Track), Is.True);
        }

        [Test]
        public void TransportKeepsRunningPastTheSongEndWhileAClipPlays()
        {
            // G2: a new song loops 0-16; with looping off upstream stops at tick 16.
            using var rig = new Rig();
            rig.Core.Looping = false;
            var clip = rig.NewPattern("clip", Bar);

            rig.Session.Launch(rig.Track, clip);
            rig.RunTicks(41);

            Assert.That(rig.Core.Playing, Is.True);
            Assert.That(rig.Session.Clock, Is.EqualTo(40));
            Assert.That(Rig.TickOf(clip), Is.EqualTo(40 % Bar));
        }

        [Test]
        public void StopIsQuantizedToTheNextBar()
        {
            using var rig = new Rig();
            var clip = rig.NewPattern("clip", 32);
            rig.Session.Launch(rig.Track, clip);
            rig.RunTicks(6);                              // clock 5

            rig.Session.Stop(rig.Track);
            Assert.That(rig.Session.GetState(rig.Track, clip), Is.EqualTo(ClipState.Stopping));
            rig.RunTicks(10);                             // clock 15
            Assert.That(rig.Track.PlayingPattern, Is.SameAs(clip));

            rig.RunTicks(1);                              // clock 16
            Assert.That(rig.Track.PlayingPattern, Is.Null);
            Assert.That(Rig.TickOf(clip), Is.Null);
            Assert.That(rig.Core.Playing, Is.True, "stopping a clip doesn't stop the transport");
        }

        [Test]
        public void SceneClipsStartOnTheSameTick()
        {
            using var rig = new Rig();
            var a = rig.NewPattern("a", Bar);
            var b = rig.NewPattern("b", Bar);
            rig.Core.SongCore.AddSequence(rig.Synth, rig.Core.SongCore.SequencesList.Count);
            var second = rig.Core.SongCore.SequencesList.Last();

            rig.Core.Playing = true;
            rig.RunTicks(3);                              // clock 2
            rig.Session.LaunchScene(new (ISequence, IPattern)[] { (rig.Track, a), (second, b) });
            rig.RunTicks(14);                             // clock 16

            Assert.That(rig.Session.GetClipStartClock(rig.Track), Is.EqualTo(16));
            Assert.That(rig.Session.GetClipStartClock(second), Is.EqualTo(16));
        }

        [Test]
        public void TriggerEventFromTheSequenceEditorGoesThroughTheSession()
        {
            using var rig = new Rig();
            var clip = rig.NewPattern("clip", Bar);
            rig.Core.SongCore.LoopEnd = 64;
            rig.Core.Playing = true;
            rig.RunTicks(4);                              // clock and song position 3

            int at = rig.Core.SongCore.PlayPosition + 2;
            rig.Track.TriggerEvent(at, new SequenceEvent(SequenceEventType.PlayPattern, clip), true);
            rig.RunTicks(2);

            Assert.That(rig.Track.PlayingPattern, Is.SameAs(clip));
            Assert.That(Rig.TickOf(clip), Is.EqualTo(0));

            rig.Track.TriggerEvent(rig.Core.SongCore.PlayPosition + 1, new SequenceEvent(SequenceEventType.Break), false);
            rig.RunTicks(1);
            Assert.That(rig.Track.PlayingPattern, Is.Null);
        }

        [Test]
        public void StoppingTheTransportClearsTheSession()
        {
            using var rig = new Rig();
            var clip = rig.NewPattern("clip", Bar);
            rig.Session.Launch(rig.Track, clip);
            rig.RunTicks(3);

            rig.Core.Playing = false;

            Assert.That(rig.Session.Clock, Is.EqualTo(-1));
            Assert.That(rig.Session.GetPlaying(rig.Track), Is.Null);
            Assert.That(rig.Session.IsArrangementOverridden(rig.Track), Is.False);
        }

        [Test]
        public void ArrangementPlaybackIsUnchanged()
        {
            using var rig = new Rig();
            var arranged = rig.NewPattern("arranged", Bar);
            rig.Track.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, arranged));

            rig.Core.Playing = true;
            rig.RunTicks(6);

            Assert.That(rig.Track.PlayingPattern, Is.SameAs(arranged));
            Assert.That(Rig.TickOf(arranged), Is.EqualTo(5));
            Assert.That(rig.Session.Clock, Is.EqualTo(5));
        }

        [Test]
        public void DeletingAPlayingClipStopsIt()
        {
            using var rig = new Rig();
            var clip = rig.NewPattern("clip", Bar);
            rig.Session.Launch(rig.Track, clip);
            rig.RunTicks(2);

            rig.Synth.DeletePattern(clip);
            rig.RunTicks(1);

            Assert.That(rig.Session.GetPlaying(rig.Track), Is.Null);
        }
    }
}
