using ReBuzz.Core.Session;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// Regressions found by the 0.6.1 review (docs/specs/session-view.md 0.6.1): follow actions
    /// against queued requests, launch undo counting from where it is heard, stale requests,
    /// marker and length changes moving a linked follow action, one clip launched on two tracks
    /// at once, and clips or tracks that are gone.
    /// </summary>
    [TestFixture]
    public class SessionHardeningTests
    {
        SessionScheduler<Track, Clip> s = null!;
        Track drums = null!;
        Track bass = null!;
        readonly Dictionary<Track, SessionScheduler<Track, Clip>.Slot?[]> columns = new();

        [SetUp]
        public void SetUp()
        {
            s = new SessionScheduler<Track, Clip>(c => c.Length);
            drums = new Track("drums");
            bass = new Track("bass");
            columns.Clear();
            s.ColumnOf = t => columns.TryGetValue(t, out var c) ? c! : null!;
            s.QuantizeTicksOf = code => code * 4;
            s.Seed(99);
        }

        static LaunchSpec Next(int slot, ClipShape? shape = null) =>
            new LaunchSpec(shape ?? ClipShape.Looped, false, 0, 1.0, new FollowSpec(FollowAction.Next, FollowAction.None, 100, true, 1, 16), slot);

        Clip[] Column(Track track, int length, params string[] names)
        {
            var clips = names.Select(n => new Clip(n, length)).ToArray();
            columns[track] = clips.Select((c, i) => (SessionScheduler<Track, Clip>.Slot?)new SessionScheduler<Track, Clip>.Slot(c, Next(i))).ToArray();
            return clips;
        }

        void LaunchSlot(Track track, int slot, int quantize = 0)
        {
            var entry = columns[track][slot]!;
            s.Launch(track, entry.Clip, quantize, s.Clock, entry.Spec, 0);
        }

        void RunTo(long clock)
        {
            while (s.Clock < clock) s.Advance();
        }

        string? Playing(Track t) => s.GetPlaying(t)?.Name;

        // ------------------------------------------------------------------ follow actions and queued requests

        [Test]
        public void AFollowActionDoesNotReplaceAQueuedStop()
        {
            Column(drums, 4, "A", "B");
            s.Start(0);
            LaunchSlot(drums, 0);                           // A at 1, follow due at 5
            RunTo(2);
            s.StopAll(16, false);                           // stop at 16
            RunTo(15);
            Assert.That(Playing(drums), Is.EqualTo("A"), "the stop waits; the follow action doesn't jump over it");
            RunTo(16);
            Assert.That(Playing(drums), Is.Null);
        }

        [Test]
        public void AFollowActionDoesNotReplaceAQueuedLaunch()
        {
            var clips = Column(drums, 4, "A", "B", "C");
            s.Start(0);
            LaunchSlot(drums, 0);                           // A at 1
            RunTo(2);
            s.LaunchAt(drums, clips[2], 16, long.MinValue, columns[drums][2]!.Spec);   // the user's C, at 16
            RunTo(15);
            Assert.That(Playing(drums), Is.EqualTo("A"));
            RunTo(16);
            Assert.That(Playing(drums), Is.EqualTo("C"));
            RunTo(20);
            Assert.That(Playing(drums), Is.EqualTo("A"), "C's own follow action goes on (Next wraps)");
        }

        // ------------------------------------------------------------------ launch undo

        [Test]
        public void AnUndoneLaunchResumesInStepAndFollowsFromWhereItIsHeard()
        {
            var clips = Column(drums, 16, "A", "B");
            var x = new Clip("X", 16);
            s.Start(0);
            LaunchSlot(drums, 0, 16);                       // A at 16; linked follow at 32
            RunTo(20);
            var before = s.Capture(drums);
            s.Launch(drums, x, 16, loop: true);             // X at 32
            RunTo(40);
            Assert.That(Playing(drums), Is.EqualTo("X"));
            s.Revert(drums, before, x, 4, s.Clock);         // A again at 44, in step: position 12
            RunTo(44);
            Assert.That(Playing(drums), Is.EqualTo("A"));
            Assert.That(s.GetPosition(drums), Is.EqualTo(12));
            Assert.That(s.GetLaunch(drums), Is.EqualTo((44L, 0L)), "heard to start at 44: recorded there");
            Assert.That(s.GetFollowAt(drums), Is.EqualTo(48), "its follow action comes at the end of this pass");
            RunTo(47);
            Assert.That(Playing(drums), Is.EqualTo("A"));
            RunTo(48);
            Assert.That(Playing(drums), Is.EqualTo("B"));
        }

        [Test]
        public void AnUndoneRequestWhoseTimeHasPassedStartsOnTheGridInStep()
        {
            var a = new Clip("A", 12);
            var b = new Clip("B", 16);
            s.Start(0);
            s.Launch(drums, a, 4, loop: true);              // A queued for 4
            var before = s.Capture(drums);
            s.Launch(drums, b, 64, loop: true);             // replaced: B for 64
            RunTo(21);
            s.Revert(drums, before, b, 4, s.Clock);         // A's time (4) has passed: next beat, 24, in step from 4
            Assert.That(s.GetQueued(drums), Is.SameAs(a));
            RunTo(23);
            Assert.That(Playing(drums), Is.Null);
            RunTo(24);
            Assert.That(Playing(drums), Is.EqualTo("A"));
            Assert.That(s.GetPosition(drums), Is.EqualTo((24 - 4) % 12));
        }

        [Test]
        public void AStartOffsetCountsFollowActionsFromWhereTheClipIsHeard()
        {
            var a = new Clip("A", 16);
            s.Start(0);
            var spec = new LaunchSpec(ClipShape.Looped, false, 0, 1.0, new FollowSpec(FollowAction.Stop, FollowAction.None, 100, false, 1, 6));
            s.Launch(drums, a, 0, s.Clock, spec, 6);        // at 1, 6 ticks in; stops 6 ticks after it is heard
            RunTo(1);
            Assert.That(s.GetPosition(drums), Is.EqualTo(6));
            Assert.That(s.GetLaunch(drums), Is.EqualTo((1L, 0L)));
            RunTo(6);
            Assert.That(Playing(drums), Is.EqualTo("A"));
            RunTo(7);
            Assert.That(Playing(drums), Is.Null);
        }

        // ------------------------------------------------------------------ markers and length

        [Test]
        public void MovingTheLoopEndMovesALinkedFollowAction()
        {
            var clips = Column(drums, 16, "A", "B");
            s.Start(0);
            LaunchSlot(drums, 0);                           // A at 1; follow at 17
            RunTo(3);
            s.Reshape(drums, clips[0], new ClipShape(0, 0, 12, true));
            Assert.That(s.GetFollowAt(drums), Is.EqualTo(13));
            RunTo(13);
            Assert.That(Playing(drums), Is.EqualTo("B"));
        }

        [Test]
        public void TurningLoopOffFollowsWhereTheClipEndsInsteadOfFallingSilent()
        {
            var clips = Column(drums, 8, "A", "B");
            s.Start(0);
            columns[drums][0] = new SessionScheduler<Track, Clip>.Slot(clips[0],
                new LaunchSpec(ClipShape.Looped, false, 0, 1.0, new FollowSpec(FollowAction.Next, FollowAction.None, 100, true, 3, 16), 0));
            LaunchSlot(drums, 0);                           // A at 1, three passes: follow at 25
            RunTo(12);                                      // second pass, position 3
            s.Reshape(drums, clips[0], ClipShape.OneShot);  // its one pass is over: it ends at once
            RunTo(13);
            Assert.That(Playing(drums), Is.EqualTo("B"), "the follow action happens where the clip ends");
        }

        [Test]
        public void ALengthChangeMovesALinkedFollowAction()
        {
            var clips = Column(drums, 16, "A", "B");
            s.Start(0);
            LaunchSlot(drums, 0);                           // follow at 17
            RunTo(2);
            clips[0].Length = 8;
            s.ClipLengthChanged(clips[0]);
            Assert.That(s.GetFollowAt(drums), Is.EqualTo(9));
        }

        // ------------------------------------------------------------------ one clip, two tracks

        [Test]
        public void AClipLaunchedOnTwoTracksOnOneTickPlaysOnOne()
        {
            var x = new Clip("X", 16);
            var y = new Clip("Y", 4);
            s.Start(0);
            s.LaunchAt(drums, x, 6, long.MinValue, LaunchSpec.Looped);          // the user's X on drums, at 6
            // Bass plays Y, whose follow action (after 5 ticks) jumps to X: due at 6 as well.
            var jump = new LaunchSpec(ClipShape.Looped, false, 0, 1.0, new FollowSpec(FollowAction.Jump, FollowAction.None, 100, false, 1, 5, 1), 0);
            columns[bass] = new SessionScheduler<Track, Clip>.Slot?[] {
                new SessionScheduler<Track, Clip>.Slot(y, jump), new SessionScheduler<Track, Clip>.Slot(x, LaunchSpec.Looped) };
            s.Launch(bass, y, 0, s.Clock, jump, 0);                             // Y at 1
            RunTo(6);
            Assert.That(new[] { Playing(drums), Playing(bass) }.Count(p => p == "X"), Is.EqualTo(1), "X plays on exactly one track, not on neither");
            Assert.That(Playing(drums), Is.EqualTo("X"), "the first launch keeps it");
        }

        // ------------------------------------------------------------------ gone

        [Test]
        public void ClipsAndTracksThatAreGoneCannotBeLaunched()
        {
            var a = new Clip("A", 16);
            var dead = new Clip("dead", 16);
            var removed = new Track("removed");
            s.IsLiveClip = c => c != dead;
            s.IsLiveTrack = t => t != removed;
            s.Start(0);
            s.Launch(drums, dead, 0, loop: true);
            s.Launch(removed, a, 0, loop: true);
            RunTo(2);
            Assert.That(Playing(drums), Is.Null);
            Assert.That(s.GetPlaying(removed), Is.Null);
            Assert.That(removed.SessionState, Is.Null, "a removed track isn't registered again");
        }

        [Test]
        public void TheUndoHistoryKnowsWhichClipsItRefersTo()
        {
            var a = new Clip("A", 16);
            var b = new Clip("B", 16);
            s.Start(0);
            s.Launch(drums, a, 0, loop: true);
            RunTo(2);
            var before = s.Capture(drums);
            Assert.That(s.Refers(before, a), Is.True);
            Assert.That(s.Refers(before, b), Is.False);
        }
    }
}
