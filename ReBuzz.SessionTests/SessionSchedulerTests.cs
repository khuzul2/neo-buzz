using ReBuzz.Core.Session;

namespace ReBuzz.SessionTests
{
    sealed class Track : ISessionTrack
    {
        public Track(string name) { Name = name; }
        public string Name { get; }
        public object? SessionState { get; set; }
        public override string ToString() => Name;
    }

    sealed class Clip
    {
        public Clip(string name, int length) { Name = name; Length = length; }
        public string Name { get; }
        public int Length { get; set; }
        public override string ToString() => Name;
    }

    [TestFixture]
    public class SessionSchedulerTests
    {
        const int Bar = 16;    // 4 beats x 4 ticks per beat

        const int Stopped = 0, Queued = 1, Playing = 2, Stopping = 3;

        SessionScheduler<Track, Clip> s = null!;
        Track drums = null!, bass = null!;
        Clip loopA = null!, loopB = null!, odd = null!;

        [SetUp]
        public void SetUp()
        {
            s = new SessionScheduler<Track, Clip>(c => c.Length);
            drums = new Track("drums");
            bass = new Track("bass");
            loopA = new Clip("A", Bar);
            loopB = new Clip("B", Bar);
            odd = new Clip("odd", 12);
        }

        /// <summary>Advance until the clock reaches <paramref name="clock"/>.</summary>
        void RunTo(long clock)
        {
            Assert.That(s.IsRunning, Is.True, "transport must run");
            while (s.Clock < clock) s.Advance();
            Assert.That(s.Clock, Is.EqualTo(clock));
        }

        // ------------------------------------------------------------------ quantization

        [Test]
        public void LaunchWaitsForNextBarLine()
        {
            s.Start(0);
            RunTo(5);
            s.Launch(drums, loopA, Bar, loop: true);

            RunTo(15);
            Assert.That(s.GetPlaying(drums), Is.Null);
            Assert.That(s.GetQueued(drums), Is.SameAs(loopA));

            RunTo(16);
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopA));
            Assert.That(s.GetPosition(drums), Is.EqualTo(0));
            Assert.That(s.GetStartClock(drums), Is.EqualTo(16));
        }

        [Test]
        public void LaunchOnAGridPointGoesToTheNextOne()
        {
            s.Start(0);
            RunTo(16);
            s.Launch(drums, loopA, Bar, loop: true);

            RunTo(31);
            Assert.That(s.GetPlaying(drums), Is.Null);
            RunTo(32);
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopA));
        }

        [Test]
        public void ZeroQuantizationStartsOnTheNextTick()
        {
            s.Start(0);
            RunTo(7);
            s.Launch(drums, loopA, 0, loop: true);
            Assert.That(s.GetPlaying(drums), Is.Null);
            s.Advance();
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopA));
            Assert.That(s.GetPosition(drums), Is.EqualTo(0));
        }

        [TestCase(0, 16)]
        [TestCase(4, 16)]
        [TestCase(16, 32)]
        [TestCase(17, 32)]
        public void GridIsAlignedToTheSongPositionTheTransportStartedAt(int startPosition, int expectedStart)
        {
            // The clock starts at the song position, so bar lines match the arrangement's.
            s.Start(startPosition);
            s.Launch(drums, loopA, Bar, loop: true);
            RunTo(expectedStart - 1);
            Assert.That(s.GetPlaying(drums), Is.Null);
            RunTo(expectedStart);
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopA));
        }

        // ------------------------------------------------------------------ positions (G1)

        [Test]
        public void PositionCountsFromLaunchNotFromSongPosition()
        {
            // Upstream computed songPosition % patternLength, so a 12-tick pattern launched
            // on tick 16 started at row 4. It must start at 0 and loop every 12 ticks.
            s.Start(0);
            s.Launch(drums, odd, Bar, loop: true);
            RunTo(16);
            var positions = new List<int>();
            for (int i = 0; i < 26; i++)
            {
                positions.Add(s.GetPosition(drums));
                s.Advance();
            }
            Assert.That(positions, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 1 }));
        }

        [Test]
        public void LengthChangeWhilePlayingIsPickedUp()
        {
            s.Start(0);
            s.LaunchAt(drums, loopA, 0, loop: true);
            s.Start(0);
            RunTo(10);
            loopA.Length = 8;
            Assert.That(s.GetPosition(drums), Is.EqualTo(2));
        }

        [Test]
        public void ClockNeverWraps()
        {
            s.Start(0);
            for (int i = 0; i < 100_000; i++) s.Advance();
            Assert.That(s.Clock, Is.EqualTo(100_000));
        }

        // ------------------------------------------------------------------ one-shot, relaunch, switch

        [Test]
        public void OneShotPlaysOnceThenEnds()
        {
            s.Start(0);
            s.Launch(drums, odd, 0, loop: false);
            s.Advance();                      // starts at 1
            RunTo(12);
            Assert.That(s.GetPosition(drums), Is.EqualTo(11));
            s.Advance();                      // clock 13: 12 ticks played
            Assert.That(s.GetPlaying(drums), Is.Null);
            Assert.That(s.GetPosition(drums), Is.EqualTo(-1));
            Assert.That(s.Changes, Has.Count.EqualTo(1));
            Assert.That(s.Changes[0].Kind, Is.EqualTo(SessionChangeKind.Ended));
            Assert.That(s.Changes[0].Previous, Is.SameAs(odd));
        }

        [Test]
        public void RelaunchRestartsAtTheNextGridPoint()
        {
            s.Start(0);
            s.Launch(drums, loopA, Bar, loop: true);
            RunTo(20);
            Assert.That(s.GetPosition(drums), Is.EqualTo(4));
            s.Launch(drums, loopA, Bar, loop: true);
            Assert.That(s.GetState(drums, loopA), Is.EqualTo(Queued));
            RunTo(32);
            Assert.That(s.GetPosition(drums), Is.EqualTo(0));
            Assert.That(s.GetStartClock(drums), Is.EqualTo(32));
            Assert.That(s.GetState(drums, loopA), Is.EqualTo(Playing));
        }

        [Test]
        public void SwitchingClipsReportsThePreviousOne()
        {
            s.Start(0);
            s.Launch(drums, loopA, Bar, loop: true);
            RunTo(16);
            s.Launch(drums, loopB, Bar, loop: true);
            Assert.That(s.GetState(drums, loopA), Is.EqualTo(Playing));
            Assert.That(s.GetState(drums, loopB), Is.EqualTo(Queued));
            RunTo(32);
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopB));
            Assert.That(s.Changes, Has.Count.EqualTo(1));
            Assert.That(s.Changes[0].Kind, Is.EqualTo(SessionChangeKind.Launched));
            Assert.That(s.Changes[0].Previous, Is.SameAs(loopA));
            Assert.That(s.Changes[0].Current, Is.SameAs(loopB));
        }

        [Test]
        public void LaterRequestReplacesQueuedOne()
        {
            s.Start(0);
            s.Launch(drums, loopA, Bar, loop: true);
            s.Launch(drums, loopB, Bar, loop: true);
            RunTo(16);
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopB));
        }

        // ------------------------------------------------------------------ stops (G4)

        [Test]
        public void StopIsQuantized()
        {
            s.Start(0);
            s.Launch(drums, loopA, Bar, loop: true);
            RunTo(20);
            s.Stop(drums, Bar, immediate: false);
            Assert.That(s.GetState(drums, loopA), Is.EqualTo(Stopping));
            RunTo(31);
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopA));
            s.Advance();
            Assert.That(s.GetPlaying(drums), Is.Null);
            Assert.That(s.Changes, Has.Count.EqualTo(1));
            Assert.That(s.Changes[0].Kind, Is.EqualTo(SessionChangeKind.Stopped));
            Assert.That(s.GetState(drums, loopA), Is.EqualTo(Stopped));
        }

        [Test]
        public void ImmediateStopTakesEffectOnTheNextTick()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            RunTo(5);
            s.Stop(drums, Bar, immediate: true);
            s.Advance();
            Assert.That(s.GetPlaying(drums), Is.Null);
        }

        [Test]
        public void StopWithNothingPlayingCancelsAQueuedLaunch()
        {
            s.Start(0);
            s.Launch(drums, loopA, Bar, loop: true);
            s.Stop(drums, Bar, immediate: false);
            Assert.That(s.GetQueued(drums), Is.Null);
            RunTo(40);
            Assert.That(s.GetPlaying(drums), Is.Null);
        }

        [Test]
        public void StopAllStopsEveryTrackOnTheSameTick()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            s.Launch(bass, loopB, 0, loop: true);
            RunTo(3);
            s.StopAll(Bar, immediate: false);
            RunTo(16);
            Assert.That(s.GetPlaying(drums), Is.Null);
            Assert.That(s.GetPlaying(bass), Is.Null);
            Assert.That(s.Changes, Has.Count.EqualTo(2));
        }

        // ------------------------------------------------------------------ scenes (G7)

        [Test]
        public void SceneClipsStartOnTheSameTick()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            RunTo(9);
            s.LaunchScene(new[] { (drums, loopB), (bass, odd) }, Bar, loop: true);
            RunTo(16);
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopB));
            Assert.That(s.GetPlaying(bass), Is.SameAs(odd));
            Assert.That(s.GetStartClock(drums), Is.EqualTo(s.GetStartClock(bass)));
        }

        [Test]
        public void TracksNotInASceneKeepPlaying()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            s.LaunchScene(new[] { (bass, loopB) }, Bar, loop: true);
            RunTo(16);
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopA));
            Assert.That(s.GetPlaying(bass), Is.SameAs(loopB));
        }

        // ------------------------------------------------------------------ transport

        [Test]
        public void LaunchWhileStoppedStartsOnTheFirstTick()
        {
            s.Launch(drums, loopA, Bar, loop: true);
            Assert.That(s.GetQueued(drums), Is.SameAs(loopA));
            Assert.That(s.GetPlaying(drums), Is.Null);

            s.Start(8);
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopA));
            Assert.That(s.GetPosition(drums), Is.EqualTo(0));
            Assert.That(s.GetStartClock(drums), Is.EqualTo(8));
        }

        [Test]
        public void HaltStopsEverythingAndReportsIt()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            s.Launch(bass, loopB, Bar, loop: true);
            RunTo(3);
            s.Halt();
            Assert.That(s.Clock, Is.EqualTo(-1));
            Assert.That(s.GetPlaying(drums), Is.Null);
            Assert.That(s.GetQueued(bass), Is.Null);
            Assert.That(s.IsOverridden(drums), Is.False);
            Assert.That(s.Changes, Has.Count.EqualTo(1));
            Assert.That(s.Changes[0].Kind, Is.EqualTo(SessionChangeKind.Halted));
            Assert.That(s.HasActivity, Is.False);
        }

        [Test]
        public void AdvanceWhileStoppedDoesNothing()
        {
            s.Advance();
            Assert.That(s.Clock, Is.EqualTo(-1));
        }

        [Test]
        public void RestartWhileRunningKeepsClipPhase()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            RunTo(6);                                    // position 5
            s.Launch(bass, loopB, Bar, loop: true);       // queued for 16
            s.Start(100);
            Assert.That(s.Clock, Is.EqualTo(100));
            Assert.That(s.GetPosition(drums), Is.EqualTo(5));
            RunTo(109);
            Assert.That(s.GetPlaying(bass), Is.Null);
            RunTo(110);
            Assert.That(s.GetPlaying(bass), Is.SameAs(loopB));
        }

        [Test]
        public void LateActionsStillApply()
        {
            // G6: upstream started a trigger only when the position matched exactly.
            s.Start(50);
            s.LaunchAt(drums, loopA, 10, loop: true);
            s.Advance();
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopA));
            Assert.That(s.GetPosition(drums), Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ arrangement override (G3)

        [Test]
        public void LaunchOverridesTheArrangementUntilBackToArrangement()
        {
            s.Start(0);
            Assert.That(s.IsOverridden(drums), Is.False);
            s.Launch(drums, loopA, 0, loop: true);
            Assert.That(s.IsOverridden(drums), Is.False, "only once the clip actually starts");
            s.Advance();
            Assert.That(s.IsOverridden(drums), Is.True);
            Assert.That(s.IsAnyOverridden, Is.True);

            s.Stop(drums, 0, immediate: true);
            s.Advance();
            Assert.That(s.IsOverridden(drums), Is.True, "a stopped track stays silent, as in Live");

            s.BackToArrangement();
            Assert.That(s.IsOverridden(drums), Is.False);
        }

        [Test]
        public void BackToArrangementReleasesWhenTheClipStops()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            RunTo(4);
            s.BackToArrangement();
            Assert.That(s.IsOverridden(drums), Is.True, "clip still plays during the current tick");
            s.Advance();
            Assert.That(s.GetPlaying(drums), Is.Null);
            Assert.That(s.IsOverridden(drums), Is.False);
        }

        // ------------------------------------------------------------------ housekeeping

        [Test]
        public void ForgetClipStopsAndUnqueuesIt()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            s.Advance();
            s.Launch(bass, loopA, Bar, loop: true);      // queued here: drums hands it over at the bar
            var stopped = new List<Track>();
            s.ForgetClip(loopA, stopped);
            Assert.That(stopped, Is.EqualTo(new[] { drums }));
            Assert.That(s.GetPlaying(drums), Is.Null);
            Assert.That(s.GetQueued(bass), Is.Null);
        }

        [Test]
        public void ForgetTrackClearsItsSlot()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            s.Advance();
            Assert.That(s.Forget(drums), Is.SameAs(loopA));
            Assert.That(drums.SessionState, Is.Null);
            Assert.That(s.GetPlaying(drums), Is.Null);
            Assert.That(s.HasActivity, Is.False);
        }

        [Test]
        public void StateFromAnotherSchedulerIsIgnored()
        {
            var other = new SessionScheduler<Track, Clip>(c => c.Length);
            other.Start(0);
            other.Launch(drums, loopA, 0, loop: true);
            other.Advance();
            s.Start(0);
            Assert.That(s.GetPlaying(drums), Is.Null);
        }

        [Test]
        public void ResetForgetsEverything()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            s.Advance();
            s.Reset();
            Assert.That(s.Clock, Is.EqualTo(-1));
            Assert.That(drums.SessionState, Is.Null);
            Assert.That(s.HasActivity, Is.False);
        }

        // ------------------------------------------------------------------ 0.4.1: heard position, catch-up, offsets, one place

        [Test]
        public void ARequestHeardBeforeTheBarLineLandsOnItWithCatchUp()
        {
            s.Start(0);
            RunTo(17);                          // the engine has rendered into bar 2 ...
            s.Launch(drums, loopA, Bar, loop: true, heardClock: 15, startOffset: 0);   // ... the listener is still in bar 1

            Assert.That(s.GetState(drums, loopA), Is.EqualTo(Queued));
            s.Advance();                        // clock 18: starts now, in step with bar 2
            Assert.That(s.GetPlaying(drums), Is.SameAs(loopA));
            Assert.That(s.GetStartClock(drums), Is.EqualTo(Bar), "counts from the bar line");
            Assert.That(s.GetPosition(drums), Is.EqualTo(18 - Bar));
        }

        [Test]
        public void WithoutLatencyARequestWaitsForTheNextBarLine()
        {
            s.Start(0);
            RunTo(17);
            s.Launch(drums, loopA, Bar, loop: true, heardClock: 17, startOffset: 0);
            RunTo(31);
            Assert.That(s.GetPlaying(drums), Is.Null);
            s.Advance();
            Assert.That(s.GetPosition(drums), Is.EqualTo(0));
        }

        [Test]
        public void PlanUsesWhatIsHeard()
        {
            s.Start(0);
            RunTo(20);
            Assert.That(s.Plan(Bar, 20), Is.EqualTo((32L, long.MinValue)), "on time: next bar");
            Assert.That(s.Plan(Bar, 30), Is.EqualTo((32L, long.MinValue)), "heard can't be ahead of the render");
            Assert.That(s.Plan(Bar, 14), Is.EqualTo((21L, 16L)), "late: next tick, counted from the missed bar line");
            Assert.That(s.Plan(0, 14), Is.EqualTo((21L, long.MinValue)), "quantization None never catches up");
        }

        [Test]
        public void ALateStopHappensOnTheNextTick()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            RunTo(17);
            s.Stop(drums, Bar, immediate: false, heardClock: 15);
            s.Advance();
            Assert.That(s.GetPlaying(drums), Is.Null);
        }

        [Test]
        public void ScenesCatchUpTogether()
        {
            s.Start(0);
            RunTo(17);
            s.LaunchScene(new[] { (drums, loopA), (bass, loopB) }, Bar, loop: true, heardClock: 15);
            s.Advance();
            Assert.That(s.GetStartClock(drums), Is.EqualTo(Bar));
            Assert.That(s.GetStartClock(bass), Is.EqualTo(Bar));
        }

        [Test]
        public void AStartOffsetBeginsPartwayThrough()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true, heardClock: 0, startOffset: 5);
            s.Advance();
            Assert.That(s.GetPosition(drums), Is.EqualTo(5));
            RunTo(1 + Bar - 5);
            Assert.That(s.GetPosition(drums), Is.EqualTo(0), "loops from the top");
        }

        [Test]
        public void AnOffsetLaunchQueuedWhileStoppedKeepsItsOffset()
        {
            s.Launch(drums, loopA, 0, loop: true, heardClock: -1, startOffset: 3);
            s.Start(32);
            Assert.That(s.GetPosition(drums), Is.EqualTo(3));
        }

        [Test]
        public void AClipPlaysInOnePlaceAtATime()
        {
            s.Start(0);
            s.Launch(drums, loopA, 0, loop: true);
            s.Advance();
            s.Launch(bass, loopA, Bar, loop: true);       // same pattern on another track

            Assert.That(s.GetState(drums, loopA), Is.EqualTo(Playing), "plays on until it starts there");
            s.Launch(drums, loopB, 2 * Bar, loop: true);  // a later request on that track doesn't keep it there
            RunTo(Bar);
            Assert.That(s.GetPlaying(drums), Is.Null);
            Assert.That(s.GetPlaying(bass), Is.SameAs(loopA));
            Assert.That(s.Changes.Single(c => ReferenceEquals(c.Track, drums)).Kind, Is.EqualTo(SessionChangeKind.Moved), "handed over, not stopped");
            Assert.That(s.GetQueued(drums), Is.SameAs(loopB), "its own request stays");
        }

        [Test]
        public void AQueuedClipMovesToTheTrackLaunchedLast()
        {
            s.Start(0);
            s.Launch(drums, loopA, Bar, loop: true);
            s.Launch(bass, loopA, Bar, loop: true);
            Assert.That(s.GetQueued(drums), Is.Null);
            Assert.That(s.GetQueued(bass), Is.SameAs(loopA));
        }

        [Test]
        public void ZeroLengthClipsCountAsOneTick()
        {
            var empty = new Clip("empty", 0);
            s.Start(0);
            s.Launch(drums, empty, 0, loop: true);
            s.Advance();
            s.Advance();
            Assert.That(s.GetPosition(drums), Is.EqualTo(0));
        }
    }
}
