using ReBuzz.Core.Session;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// Follow actions and launch undo in the scheduler (docs/specs/session-view.md 0.6), with
    /// Live's rules: a group is a run of filled slots, Next wraps to the group's first clip, a
    /// follow action happens exactly at its time unless the target has its own quantization,
    /// No Action ends the chain.
    /// </summary>
    [TestFixture]
    public class SessionFollowTests
    {
        SessionScheduler<Track, Clip> s = null!;
        Track drums = null!;
        readonly Dictionary<Track, SessionScheduler<Track, Clip>.Slot?[]> columns = new();

        [SetUp]
        public void SetUp()
        {
            s = new SessionScheduler<Track, Clip>(c => c.Length);
            drums = new Track("drums");
            columns.Clear();
            s.ColumnOf = t => columns.TryGetValue(t, out var c) ? c! : null!;
            s.QuantizeTicksOf = code => code * 4;            // test code n: n beats of 4 ticks
            s.Seed(1234);
        }

        static LaunchSpec Spec(int slot, FollowAction a, FollowAction b = FollowAction.None, int chanceA = 100,
                               bool linked = true, int multiplier = 1, int time = 16, int jumpA = -1,
                               ClipShape? shape = null, bool legato = false) =>
            new LaunchSpec(shape ?? ClipShape.Looped, legato, 0, 1.0, new FollowSpec(a, b, chanceA, linked, multiplier, time, jumpA), slot);

        /// <summary>A column of clips (null: empty slot), all with the same follow actions.</summary>
        Clip?[] Column(Track track, int length, Func<int, LaunchSpec> spec, params string?[] names)
        {
            var clips = names.Select(n => n == null ? null : new Clip(n, length)).ToArray();
            columns[track] = clips.Select((c, i) => c == null ? null : new SessionScheduler<Track, Clip>.Slot(c, spec(i))).ToArray();
            return clips;
        }

        void Launch(Track track, int slot)
        {
            var entry = columns[track][slot]!;
            s.Launch(track, entry.Clip, 0, s.Clock, entry.Spec, 0);
        }

        void RunTo(long clock)
        {
            while (s.Clock < clock) s.Advance();
        }

        string? Playing => s.GetPlaying(drums)?.Name;

        // ------------------------------------------------------------------ actions

        [Test]
        public void NextWrapsWithinTheGroupAndEmptySlotsSplitGroups()
        {
            Column(drums, 4, i => Spec(i, FollowAction.Next), "A", "B", "C", null, "D");
            s.Start(0);
            Launch(drums, 0);
            var seen = new List<string?>();
            for (long c = 1; c <= 13; c += 4) { RunTo(c); seen.Add(Playing); }
            Assert.That(seen, Is.EqualTo(new[] { "A", "B", "C", "A" }), "linked: at the end of each 4-tick clip; D is another group");
        }

        [Test]
        public void PreviousFirstAndLastStayInTheGroup()
        {
            Column(drums, 4, i => Spec(i, FollowAction.Previous), "A", "B", "C");
            s.Start(0);
            Launch(drums, 0);
            RunTo(5);
            Assert.That(Playing, Is.EqualTo("C"), "Previous wraps to the group's last clip");

            Column(drums, 4, i => Spec(i, i == 1 ? FollowAction.First : FollowAction.Last), "A", "B", "C");
            Launch(drums, 1);                                            // B at 6
            RunTo(9);
            Assert.That(Playing, Is.EqualTo("B"));
            RunTo(10);
            Assert.That(Playing, Is.EqualTo("A"), "B's First");
            RunTo(14);
            Assert.That(Playing, Is.EqualTo("C"), "A's Last");
        }

        [Test]
        public void LinkedWaitsForTheEndOfTheClipTimesTheMultiplierUnlinkedForItsTime()
        {
            var shape = new ClipShape(2, 4, 8, true);                    // a first pass of 6, then 4-tick loops
            Column(drums, 8, i => Spec(i, FollowAction.Next, multiplier: 2, shape: shape), "A", "B");
            s.Start(0);
            Launch(drums, 0);                                            // starts at 1
            RunTo(10);
            Assert.That(Playing, Is.EqualTo("A"));
            Assert.That(s.GetFollowAt(drums), Is.EqualTo(11), "1 + 6 + 4");
            RunTo(11);
            Assert.That(Playing, Is.EqualTo("B"));

            Column(drums, 8, i => Spec(i, FollowAction.Next, linked: false, time: 3), "A", "B");
            Launch(drums, 0);                                            // starts at 12
            RunTo(14);
            Assert.That(Playing, Is.EqualTo("A"));
            RunTo(15);
            Assert.That(Playing, Is.EqualTo("B"), "3 ticks after it started");
        }

        [Test]
        public void AFollowActionAtTheEndOfAOneShotClipIsASwitch()
        {
            Column(drums, 4, i => Spec(i, FollowAction.Next, shape: ClipShape.OneShot), "A", "B");
            s.Start(0);
            Launch(drums, 0);
            RunTo(4);
            s.Advance();                                                 // 5: A's end and its follow time
            Assert.That(s.Changes.Count, Is.EqualTo(1));
            Assert.That(s.Changes[0].Kind, Is.EqualTo(SessionChangeKind.Launched));
            Assert.That(s.Changes[0].Previous?.Name, Is.EqualTo("A"), "no Ended (note-offs) between the two");
            Assert.That(s.Changes[0].Current?.Name, Is.EqualTo("B"));
        }

        [Test]
        public void NoActionEndsTheChainAndStopStops()
        {
            Column(drums, 4, i => Spec(i, FollowAction.None), "A", "B");
            s.Start(0);
            Launch(drums, 0);
            RunTo(13);
            Assert.That(Playing, Is.EqualTo("A"));
            Assert.That(s.GetFollowAt(drums), Is.EqualTo(-1), "nothing more until it is launched again");

            Column(drums, 4, i => Spec(i, FollowAction.Stop), "A", "B");
            Launch(drums, 0);                                            // 14
            RunTo(17);
            Assert.That(Playing, Is.EqualTo("A"));
            s.Advance();                                                 // 18
            Assert.That(Playing, Is.Null);
            Assert.That(s.Changes.Single().Kind, Is.EqualTo(SessionChangeKind.Stopped));
        }

        [Test]
        public void JumpGoesToASlotAndToAnEmptyOneStops()
        {
            Column(drums, 4, i => Spec(i, FollowAction.Jump, jumpA: 3), "A", "B", null, "D");
            s.Start(0);
            Launch(drums, 0);
            RunTo(5);
            Assert.That(Playing, Is.EqualTo("D"), "any slot of the track, not only the group");

            Column(drums, 4, i => Spec(i, FollowAction.Jump, jumpA: 2), "A", "B", null, "D");
            Launch(drums, 0);                                            // 6
            RunTo(10);
            Assert.That(Playing, Is.Null);
        }

        [Test]
        public void OtherNeverRepeatsAndAnyCanAndChancePicksBetweenAAndB()
        {
            Column(drums, 1, i => Spec(i, FollowAction.Other), "A", "B", "C");
            s.Start(0);
            Launch(drums, 0);
            RunTo(1);
            string? previous = Playing;
            int repeats = 0;
            for (int n = 0; n < 300; n++)
            {
                s.Advance();
                if (Playing == previous) repeats++;
                previous = Playing;
            }
            Assert.That(repeats, Is.EqualTo(0));

            Column(drums, 1, i => Spec(i, FollowAction.Any), "A", "B", "C");
            Launch(drums, 0);
            s.Advance(); s.Advance();
            previous = Playing;
            for (int n = 0; n < 300; n++)
            {
                s.Advance();
                if (Playing == previous) repeats++;
                previous = Playing;
            }
            Assert.That(repeats, Is.InRange(60, 140), "Any repeats about a third of the time");

            Column(drums, 1, i => Spec(i, FollowAction.Stop, FollowAction.PlayAgain, chanceA: 0), "A");
            Launch(drums, 0);
            RunTo(s.Clock + 50);
            Assert.That(Playing, Is.EqualTo("A"), "0% A: always B");
        }

        [Test]
        public void AFollowActionWaitsForTheTargetsOwnQuantization()
        {
            Column(drums, 4, i => Spec(i, FollowAction.Next), "A", "B");
            columns[drums][1] = new SessionScheduler<Track, Clip>.Slot(columns[drums][1]!.Clip, columns[drums][1]!.Spec, quantization: 2);   // 8 ticks
            s.Start(0);
            Launch(drums, 0);                                            // starts at 1, follow due at 5
            RunTo(7);
            Assert.That(Playing, Is.EqualTo("A"));
            Assert.That(s.GetQueued(drums)?.Name, Is.EqualTo("B"));
            RunTo(8);
            Assert.That(Playing, Is.EqualTo("B"));
        }

        [Test]
        public void SwitchedOffAFollowActionWaitsForItsNextTurn()
        {
            Column(drums, 4, i => Spec(i, FollowAction.Next), "A", "B");
            s.Start(0);
            Launch(drums, 0);
            s.FollowActionsEnabled = false;
            RunTo(5);
            Assert.That(Playing, Is.EqualTo("A"));
            Assert.That(s.GetFollowAt(drums), Is.EqualTo(9), "the next pass");
            s.FollowActionsEnabled = true;
            RunTo(9);
            Assert.That(Playing, Is.EqualTo("B"));
        }

        [Test]
        public void ARequestOnTheFollowTickWins()
        {
            var clips = Column(drums, 4, i => Spec(i, FollowAction.Next), "A", "B", "C");
            s.Start(0);
            Launch(drums, 0);
            s.LaunchAt(drums, clips[2]!, 5, long.MinValue, columns[drums][2]!.Spec);
            RunTo(5);
            Assert.That(Playing, Is.EqualTo("C"));
        }

        [Test]
        public void ALegatoTargetPicksUpThePosition()
        {
            Column(drums, 8, i => Spec(i, FollowAction.Next, linked: false, time: 3, legato: i == 1), "A", "B");
            s.Start(0);
            Launch(drums, 0);                                            // starts at 1
            RunTo(4);
            Assert.That(Playing, Is.EqualTo("B"));
            Assert.That(s.GetPosition(drums), Is.EqualTo(3), "where A had got to");
        }

        /// <summary>
        /// The spec's acceptance test, at the scheduler: a 4-clip chain (Next 70%, Other 30%)
        /// runs for 20,000 follow actions. From each clip the next one should come 80% of the
        /// time and each of the two others 10%; never itself. Chi-square over the 4 x 3
        /// transitions (8 degrees of freedom) must stay under 15.51, the 95% critical value.
        /// </summary>
        [Test]
        public void AFourClipChainHasTheExpectedTransitions()
        {
            Column(drums, 2, i => Spec(i, FollowAction.Next, FollowAction.Other, chanceA: 70), "A", "B", "C", "D");
            s.Seed(20261030);
            s.Start(0);
            Launch(drums, 0);
            var counts = new int[4, 4];
            int transitions = 0;
            while (transitions < 20000)
            {
                s.Advance();
                foreach (var change in s.Changes)
                {
                    if (change.Kind != SessionChangeKind.Launched || change.Previous == null) continue;
                    counts[change.Previous.Name[0] - 'A', change.Current!.Name[0] - 'A']++;
                    transitions++;
                }
            }

            double chi = 0;
            for (int from = 0; from < 4; from++)
            {
                int total = 0;
                for (int to = 0; to < 4; to++) total += counts[from, to];
                Assert.That(counts[from, from], Is.Zero, "never itself");
                for (int to = 0; to < 4; to++)
                {
                    if (to == from) continue;
                    double expected = total * (to == (from + 1) % 4 ? 0.8 : 0.1);
                    chi += Math.Pow(counts[from, to] - expected, 2) / expected;
                }
            }
            TestContext.Out.WriteLine($"[notice] 4-clip chain, 20,000 transitions: chi-square {chi:F2} (95% critical 15.51)");
            Assert.That(chi, Is.LessThan(15.51));
        }

        // ------------------------------------------------------------------ launch undo

        [Test]
        public void UndoingAQueuedLaunchCancelsIt()
        {
            var a = new Clip("A", 16);
            var b = new Clip("B", 16);
            s.Start(0);
            s.Launch(drums, a, 0, loop: true);
            RunTo(5);
            var before = s.Capture(drums);
            s.Launch(drums, b, 16, loop: true);
            s.Revert(drums, before, b, 16, s.Clock);
            Assert.That(s.GetQueued(drums), Is.Null);
            RunTo(20);
            Assert.That(Playing, Is.EqualTo("A"));
        }

        [Test]
        public void UndoingAStartedLaunchBringsBackThePreviousClipInStep()
        {
            var a = new Clip("A", 16);
            var b = new Clip("B", 16);
            s.Start(0);
            s.Launch(drums, a, 0, loop: true);                           // starts at 1
            RunTo(10);
            var before = s.Capture(drums);
            s.Launch(drums, b, 0, loop: true);                           // 11
            RunTo(13);
            s.Revert(drums, before, b, 0, s.Clock);                      // back at 14
            RunTo(14);
            Assert.That(Playing, Is.EqualTo("A"));
            Assert.That(s.GetPosition(drums), Is.EqualTo(13), "as if it had never stopped");
        }

        [Test]
        public void UndoingTheFirstLaunchOfATrackStopsIt()
        {
            var a = new Clip("A", 16);
            s.Start(0);
            var before = s.Capture(drums);
            s.Launch(drums, a, 0, loop: true);
            RunTo(3);
            s.Revert(drums, before, a, 0, s.Clock);
            RunTo(4);
            Assert.That(Playing, Is.Null);
        }
    }
}
