// Neo Arp's gates (docs/specs/neo-arp.md §9): the order of every pattern (written from the definition here, not from the engine), the steps' lengths and
// velocities, the timing at three tempos and three block sizes, tempo changes, loop jumps, Free time, no stuck notes under a seeded stress of 10,000
// events, the chain into Neo Poly, zero allocation, the saved state and the presets.
using NeoBuzz.Arp;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class NeoArpTests
    {
        // The chord of the order tests, played out of order: sorted it is 60 64 67 71.
        static readonly int[] Played = { 64, 71, 60, 67 };

        static ArpRig Make(int pattern, int octaves = 1, int octMode = 0, int repeats = 1, int rateIndex = 3)
        {
            var rig = new ArpRig();
            rig.Set(P.Pattern, pattern).Set(P.Octaves, octaves).Set(P.OctMode, octMode).Set(P.Repeats, repeats).Set(P.Rate, rateIndex).Set(P.Gate, 50);
            return rig;
        }

        static void Hold(ArpRig rig, int[] notes, long at = 0)
        {
            foreach (var n in notes) rig.NoteOn(at, n);
        }

        static int[] Repeat(int[] cycle, int times) => Enumerable.Range(0, times).SelectMany(_ => cycle).ToArray();

        // ============================================================================================================ order

        // Over two octaves (Octave mode Up: the figure, then the same figure an octave higher). Written out by hand from the definitions in docs/specs/neo-arp.md §3.
        static readonly (int Pattern, string Name, int[] Cycle)[] Orders =
        {
            (0, "Up", new[] { 60, 64, 67, 71, 72, 76, 79, 83 }),
            (1, "Down", new[] { 71, 67, 64, 60, 83, 79, 76, 72 }),
            (2, "Up-down (ends once)", new[] { 60, 64, 67, 71, 67, 64, 72, 76, 79, 83, 79, 76 }),
            (3, "Up-down (ends twice)", new[] { 60, 64, 67, 71, 71, 67, 64, 60, 72, 76, 79, 83, 83, 79, 76, 72 }),
            (4, "Down-up", new[] { 71, 67, 64, 60, 64, 67, 83, 79, 76, 72, 76, 79 }),
            (5, "Converge", new[] { 60, 71, 64, 67, 72, 83, 76, 79 }),
            (6, "Diverge", new[] { 64, 67, 60, 71, 76, 79, 72, 83 }),
            (7, "As played", new[] { 64, 71, 60, 67, 76, 83, 72, 79 }),
        };

        [Test]
        public void EveryPatternGivesTheRightOrderOverTwoOctaves()
        {
            foreach (var (pattern, name, cycle) in Orders)
            {
                var rig = Make(pattern, octaves: 2);
                Hold(rig, Played);
                rig.RunTo(4 * 4 * 5);                              // 40 steps of 1/8: two of the longest cycle
                var heard = rig.OnNotes();
                Assert.That(heard.Length, Is.GreaterThanOrEqualTo(cycle.Length * 2), name);
                Assert.That(heard.Take(cycle.Length * 2), Is.EqualTo(Repeat(cycle, 2)), $"{name}: the figure, then again");
            }
        }

        [Test]
        public void TheOctaveModesOrderThePasses()
        {
            // Up pattern, chord of one note, three octaves: Up climbs, Down falls, Up-down goes up and back (the ends once).
            var expect = new[] { (0, new[] { 60, 72, 84 }), (1, new[] { 84, 72, 60 }), (2, new[] { 60, 72, 84, 72 }) };
            foreach (var (mode, cycle) in expect)
            {
                var rig = Make(0, octaves: 3, octMode: mode);
                rig.NoteOn(0, 60);
                rig.RunTo(4 * 8);
                Assert.That(rig.OnNotes().Take(cycle.Length * 3), Is.EqualTo(Repeat(cycle, 3)), "octave mode " + mode);
            }
            // Down pattern with Octave mode Down is the whole range falling, as one would play it.
            var down = Make(1, octaves: 2, octMode: 1);
            Hold(down, Played);
            down.RunTo(4 * 8);
            Assert.That(down.OnNotes().Take(8), Is.EqualTo(new[] { 83, 79, 76, 72, 71, 67, 64, 60 }));
        }

        [Test]
        public void RepeatsAndTransposeAreApplied()
        {
            var rig = Make(0, repeats: 2).Set(P.Transpose, 5);
            Hold(rig, Played);
            rig.RunTo(4 * 4);
            Assert.That(rig.OnNotes().Take(8), Is.EqualTo(new[] { 65, 65, 69, 69, 72, 72, 76, 76 }));
            var low = Make(0).Set(P.Transpose, -24);
            low.NoteOn(0, 40);
            low.RunTo(8);
            Assert.That(low.OnNotes().First(), Is.EqualTo(16), "40 - 24");
            var high = Make(0).Set(P.Transpose, 24);
            high.NoteOn(0, 120);
            high.RunTo(8);
            Assert.That(high.OnNotes(), Is.Empty, "a note past 127 is not sent");
        }

        [Test]
        public void ChordPlaysEveryHeldNoteTogetherAndRepeats()
        {
            var rig = Make(10, octaves: 2).Set(P.Rate, 2);        // 1/4
            Hold(rig, Played);
            rig.RunTo(4 * 4);
            var steps = rig.Ons.GroupBy(r => r.At).OrderBy(g => g.Key).Take(4).Select(g => g.Select(r => r.Note).OrderBy(x => x).ToArray()).ToArray();
            Assert.That(steps[0], Is.EqualTo(new[] { 60, 64, 67, 71 }));
            Assert.That(steps[1], Is.EqualTo(new[] { 72, 76, 79, 83 }));
            Assert.That(steps[2], Is.EqualTo(steps[0]));
            Assert.That(steps[3], Is.EqualTo(steps[1]));
        }

        [Test]
        public void RandomIsSeededAndStaysInTheChord()
        {
            int[] Run()
            {
                var rig = Make(8, octaves: 2);
                Hold(rig, Played);
                rig.RunTo(4 * 16);
                return rig.OnNotes();
            }
            var a = Run();
            Assert.That(Run(), Is.EqualTo(a), "the same every time");
            var pool = new[] { 60, 64, 67, 71, 72, 76, 79, 83 };
            Assert.That(a.All(n => pool.Contains(n)), Is.True);
            Assert.That(a.Distinct().Count(), Is.GreaterThanOrEqualTo(6), "it uses the pool");
            Assert.That(a.Zip(a.Skip(1), (x, y) => x != y).Count(d => d), Is.GreaterThan(a.Length / 2), "it is not a repeating figure");
        }

        [Test]
        public void RandomWalkMovesOnePlaceAtATime()
        {
            var rig = Make(9, octaves: 2);
            Hold(rig, Played);
            rig.RunTo(4 * 40);
            var pool = new[] { 60, 64, 67, 71, 72, 76, 79, 83 };
            var heard = rig.OnNotes();
            Assert.That(heard.Length, Is.GreaterThan(40));
            Assert.That(heard[0], Is.EqualTo(pool[4]), "it starts in the middle of the range");
            for (int i = 1; i < heard.Length; i++)
                Assert.That(Math.Abs(Array.IndexOf(pool, heard[i]) - Array.IndexOf(pool, heard[i - 1])), Is.EqualTo(1), $"step {i}");
            Assert.That(heard.Distinct().Count(), Is.GreaterThan(3));
        }

        [Test]
        public void StepsWithTiesAndAccentsHaveTheRightLengthsAndVelocities()
        {
            // A row of eight steps at 1/8: On, Tie, Off, Accent(+7), On(-12), Tie, Tie, Off. Gate 80 %.
            var rig = Make(11, rateIndex: 3).Set(P.Gate, 80).Set(P.VelMode, 2).Set(P.FixedVel, 81).Set(P.AccentLevel, 50);
            rig.Row("NTOANTTO", new[] { 0, 0, 0, 7, -12, 0, 0, 0 });
            rig.NoteOn(0, 48);
            rig.RunTo(4 * 4 * 2 + 1);
            double L = 0.5;                                                        // beats a step
            var ons = rig.Ons.ToList();
            var offs = rig.Log.Where(r => r.Kind == ArpKind.NoteOff).ToList();
            var expectOn = new (double Step, int Note, int Vel)[] { (0, 48, 81), (3, 55, 104), (4, 36, 81), (8, 48, 81), (11, 55, 104), (12, 36, 81) };
            for (int i = 0; i < expectOn.Length; i++)
            {
                Assert.That(ons[i].At, Is.EqualTo(rig.BeatToAbs(expectOn[i].Step * L)).Within(1), $"on {i}: time");
                Assert.That(ons[i].Note, Is.EqualTo(expectOn[i].Note), $"on {i}: note");
                Assert.That(ons[i].Vel, Is.EqualTo(expectOn[i].Vel), $"on {i}: velocity");
            }
            // Lengths: the first note ties through step 1 (off at 1.8 steps), the accent is one step (3.8), the last ties through steps 5 and 6 (6.8).
            var expectOff = new[] { 1.8, 3.8, 6.8, 9.8, 11.8, 14.8 };
            for (int i = 0; i < expectOff.Length; i++)
                Assert.That(offs[i].At, Is.EqualTo(rig.BeatToAbs(expectOff[i] * L)).Within(1), $"off {i}");
            Assert.That(ons.Count(r => r.At < rig.BeatToAbs(8 * L) - 1), Is.EqualTo(3), "Off steps and Tie steps start no note");
        }

        [Test]
        public void StepsTransposeAndAccentLevelsFollowTheirSettings()
        {
            var rig = Make(11).Set(P.VelMode, 1).Set(P.FixedVel, 70);
            rig.Row("AN", new[] { 0, 0 });
            rig.NoteOn(0, 60);
            rig.RunTo(8);
            Assert.That(rig.Ons.Take(2).Select(r => r.Vel), Is.EqualTo(new[] { 70, 70 }), "Fixed ignores accents");
            var asPlayed = Make(11).Set(P.VelMode, 0);
            asPlayed.Row("AN");
            asPlayed.NoteOn(0, 60, 33);
            asPlayed.RunTo(8);
            Assert.That(asPlayed.Ons.Take(2).Select(r => r.Vel), Is.EqualTo(new[] { 33, 33 }), "As played keeps the played velocity");
            var full = Make(11).Set(P.VelMode, 2).Set(P.FixedVel, 60).Set(P.AccentLevel, 100);
            full.Row("AN");
            full.NoteOn(0, 60);
            full.RunTo(8);
            Assert.That(full.Ons.Take(2).Select(r => r.Vel), Is.EqualTo(new[] { 127, 60 }), "Accent 100 % reaches 127");
        }

        // ============================================================================================================ timing

        [TestCase(60.0, 16), TestCase(60.0, 64), TestCase(60.0, 256), TestCase(120.0, 16), TestCase(120.0, 64), TestCase(120.0, 256),
         TestCase(197.0, 16), TestCase(197.0, 64), TestCase(197.0, 256)]
        public void StepsLandWithinOneSampleOfTheGrid(double bpm, int block)
        {
            // 1/16, swing 50 %, gate 50 %; then 1/16 triplets with gate 150 %.
            foreach (var (feel, swing, gate) in new[] { (0, 0, 50), (0, 50, 50), (2, 0, 150), (1, 25, 100) })
            {
                var rig = Make(0, rateIndex: 4).Set(P.Feel, feel).Set(P.Swing, swing).Set(P.Gate, gate);
                rig.Bpm = _ => bpm;
                rig.Block = block;
                Hold(rig, Played);
                rig.RunTo(4 * 16);
                double stepBeats = 0.25 * (feel == 1 ? 1.5 : feel == 2 ? 2.0 / 3 : 1);
                AssertOnTheGrid(rig, stepBeats, swing / 100.0, gate / 100.0, $"{bpm} BPM, block {block}, feel {feel}, swing {swing}, gate {gate}");
            }
        }

        /// <summary>Every note-on of the Up pattern over a 4-note chord is where step k is (swing delays the odd ones), and its off is a gate after it.</summary>
        static void AssertOnTheGrid(ArpRig rig, double stepBeats, double swing, double gate, string what)
        {
            var ons = rig.Ons.ToList();
            Assert.That(ons.Count, Is.GreaterThan(40), what);
            int checkedOffs = 0;
            for (int k = 0; k < ons.Count; k++)
            {
                double beat = (k + ((k & 1) == 1 ? swing : 0)) * stepBeats;
                double expect = rig.BeatToAbs(beat);
                Assert.That(ons[k].At, Is.EqualTo(expect).Within(1.0), $"{what}: on {k}");
                // The matching off: the first off of that note after it.
                var off = rig.Log.FirstOrDefault(r => r.Kind == ArpKind.NoteOff && r.Note == ons[k].Note && r.At >= ons[k].At);
                if (off.Kind == ArpKind.NoteOff && off.At > 0)
                {
                    double expectOff = rig.BeatToAbs(beat + gate * stepBeats);
                    // A note of 4 steps ago would still be on only for a gate above 400 %; the retrigger of the same note can't happen here.
                    Assert.That(off.At, Is.EqualTo(expectOff).Within(1.0), $"{what}: off {k}");
                    checkedOffs++;
                }
            }
            Assert.That(checkedOffs, Is.GreaterThan(ons.Count - 4), what);
        }

        [Test]
        public void ATempoChangeInThePatternStaysOnTheGrid()
        {
            var rig = Make(0, rateIndex: 4).Set(P.Swing, 30).Set(P.Gate, 60);
            rig.Bpm = tick => tick < 64 ? 120 : tick < 128 ? 90 : tick < 160 ? 150 : 61;
            rig.Block = 64;
            Hold(rig, Played);
            rig.RunTo(4 * 56);
            AssertOnTheGrid(rig, 0.25, 0.30, 0.60, "tempo changes at ticks 64, 128, 160");
            Assert.That(ArpRig.Balance(rig.Log).Count(p => !p.Contains("never released")), Is.EqualTo(0));
        }

        [Test]
        public void ALoopJumpStaysOnTheGridAndLeavesNothingSounding()
        {
            var rig = Make(0, octaves: 2, rateIndex: 3).Set(P.Gate, 150);       // 1/8: a step is two ticks
            rig.Block = 64;
            Hold(rig, Played);
            rig.RunTo(100);
            long beforeJump = rig.Log.Count;
            long jumpAt = rig.Abs;
            rig.Jump(33);                                                         // an odd tick: the next step is at tick 34
            rig.RunTo(33 + 40);
            var after = rig.Log.Skip((int)beforeJump).ToList();
            // Everything sounding at the jump is released at the jump.
            Assert.That(after.TakeWhile(r => r.Kind == ArpKind.NoteOff).All(r => r.At == jumpAt), Is.True, "offs at the jump");
            var firstOn = after.First(r => r.Kind == ArpKind.NoteOn);
            Assert.That(firstOn.Beat, Is.EqualTo(34 / 4.0).Within(1e-6), "the next grid point after the jump");
            foreach (var r in rig.Ons)
            {
                double steps = r.Beat / 0.5;
                Assert.That(Math.Abs(steps - Math.Round(steps)) * 0.5 * 4 * rig.TickInfo[(long)Math.Floor(r.Beat * 4)].Length, Is.LessThanOrEqualTo(1.0), "on the grid in beats");
            }
            // The figure after the jump is the one the song position says: the pattern is a function of the step number.
            long stepNumber = (long)Math.Round(firstOn.Beat / 0.5);
            var pool = new[] { 60, 64, 67, 71, 72, 76, 79, 83 };
            Assert.That(firstOn.Note, Is.EqualTo(pool[stepNumber % 8]));
        }

        [Test]
        public void StartingMidBarIsOnTheGridAndThePatternFollowsTheSongPosition()
        {
            // The transport starts at tick 3 (a quarter of a beat is a tick): a 1/8 grid has its next step at tick 4.
            var rig = Make(0, rateIndex: 3);
            rig.Tick = 3;
            Hold(rig, Played);
            rig.RunTo(3 + 20);
            var first = rig.Ons.First();
            Assert.That(first.Beat, Is.EqualTo(1.0).Within(1e-6), "the first step is the next point of the grid, tick 4");
            // The step number there is 2: the third note of the cycle.
            Assert.That(first.Note, Is.EqualTo(67), "free running: the figure is where the song position puts it");
        }

        [Test]
        public void FreeTimeIsExactInMilliseconds()
        {
            foreach (int ms in new[] { 20, 137, 500, 2000 })
            {
                var rig = Make(0).Set(P.Sync, 1).Set(P.FreeMs, ms).Set(P.Gate, 50);
                rig.Block = 64;
                rig.NoteOn(0, 60);
                rig.Run(48000L * 12);
                double step = ms * 48.0;
                var ons = rig.Ons.ToList();
                Assert.That(ons.Count, Is.GreaterThan(5), $"{ms} ms");
                for (int k = 0; k < Math.Min(ons.Count, 400); k++)
                    Assert.That(ons[k].At, Is.EqualTo(k * step).Within(1.0), $"{ms} ms: on {k}");
                var offs = rig.Log.Where(r => r.Kind == ArpKind.NoteOff).ToList();
                for (int k = 0; k < Math.Min(offs.Count, 400); k++)
                    Assert.That(offs[k].At, Is.EqualTo(k * step + 0.5 * step).Within(1.0), $"{ms} ms: off {k}");
            }
        }

        [Test]
        public void SwingDelaysEverySecondStepAndTheGateIsMeasuredFromTheNote()
        {
            var rig = Make(0, rateIndex: 4).Set(P.Swing, 75).Set(P.Gate, 40);
            rig.NoteOn(0, 60);
            rig.NoteOn(0, 64);
            rig.Run(48000L * 4);
            var ons = rig.Ons.ToList();
            double step = 0.25 * 24000;
            for (int k = 0; k < 14; k++)
                Assert.That(ons[k].At, Is.EqualTo((k + ((k & 1) == 1 ? 0.75 : 0)) * step).Within(1.0), $"on {k}");
            var offs = rig.Log.Where(r => r.Kind == ArpKind.NoteOff).ToList();
            for (int k = 0; k < 14; k++)
                Assert.That(offs[k].At - ons[k].At, Is.EqualTo(0.4 * step).Within(1.0), $"length {k}");
        }

        [Test]
        public void ATiedChordAndALongGateOverlapOnlyOtherPitches()
        {
            var rig = Make(0, rateIndex: 4).Set(P.Gate, 200);
            Hold(rig, Played);
            rig.RunTo(4 * 8);
            Assert.That(ArpRig.Balance(rig.Log).Where(p => !p.Contains("never released")), Is.Empty);
            // Two steps long: the second note is on while the first still sounds.
            var ons = rig.Ons.Take(3).ToList();
            var off0 = rig.Log.First(r => r.Kind == ArpKind.NoteOff && r.Note == ons[0].Note);
            Assert.That(off0.At - ons[0].At, Is.EqualTo(2 * 6000).Within(1.0), "a gate of 200 % lasts two steps");
            Assert.That(ons[1].At, Is.LessThan(off0.At));
            // One held note: the same pitch again cuts the one before (an off, then the on, at the same sample).
            var one = Make(0, rateIndex: 4).Set(P.Gate, 200);
            one.NoteOn(0, 60);
            one.RunTo(4 * 2);
            Assert.That(ArpRig.Balance(one.Log).Where(p => !p.Contains("never released")), Is.Empty);
            var second = one.Ons.ElementAt(1);
            Assert.That(one.Log.Any(r => r.Kind == ArpKind.NoteOff && r.At == second.At), Is.True);
        }

        // ============================================================================================================ hold and retrigger

        [Test]
        public void LatchKeepsTheNotesUntilANewChordIsPlayed()
        {
            var rig = Make(0, rateIndex: 4).Set(P.Latch, 1);
            rig.Block = 64;
            rig.NoteOn(0, 60); rig.NoteOn(0, 64);
            rig.NoteOff(24000 * 2, 60); rig.NoteOff(24000 * 2, 64);              // released after one beat
            rig.Run(24000 * 6);
            Assert.That(rig.Ons.Any(r => r.At > 24000 * 3), Is.True, "still playing after the release");
            Assert.That(rig.Ons.Where(r => r.At > 24000 * 3).Select(r => r.Note).Distinct().OrderBy(x => x), Is.EqualTo(new[] { 60, 64 }));
            // A new chord replaces it.
            long at = rig.Abs + 100;
            rig.NoteOn(at, 55); rig.NoteOn(at, 59);
            rig.Run(24000 * 2);
            Assert.That(rig.Ons.Where(r => r.At > at + 3000).Select(r => r.Note).Distinct().OrderBy(x => x), Is.EqualTo(new[] { 55, 59 }));
            // Let go of the new chord: it goes on playing. Then Latch off with nothing held: silence and every note released.
            rig.NoteOff(rig.Abs + 10, 55); rig.NoteOff(rig.Abs + 10, 59);
            rig.Run(24000 * 2);
            Assert.That(rig.Ons.Any(r => r.At > rig.Abs - 12000), Is.True, "still playing after the second release");
            rig.Set(P.Latch, 0);
            rig.Run(24000);
            long quietFrom = rig.Abs;
            rig.Run(24000);
            Assert.That(rig.Ons.Any(r => r.At >= quietFrom), Is.False);
            Assert.That(ArpRig.Balance(rig.Log), Is.Empty);
        }

        [Test]
        public void RetriggerOnNewNoteStartsTheFigureAtTheNextGridPoint()
        {
            var rig = Make(0, rateIndex: 4).Set(P.Retrigger, 1);
            rig.Block = 64;
            rig.NoteOn(100, 60); rig.NoteOn(100, 64); rig.NoteOn(100, 67);
            rig.NoteOff(24000 * 2, 60); rig.NoteOff(24000 * 2, 64); rig.NoteOff(24000 * 2, 67);
            rig.NoteOn(24000 * 2 + 3000 + 17, 60); rig.NoteOn(24000 * 2 + 3000 + 17, 64); rig.NoteOn(24000 * 2 + 3000 + 17, 67);
            rig.Run(24000 * 5);
            var ons = rig.Ons.ToList();
            Assert.That(ons[0].Note, Is.EqualTo(60));
            Assert.That(ons[0].At, Is.EqualTo(6000).Within(1), "the first grid point after the key (a 16th is 6000 samples)");
            var second = ons.First(r => r.At > 24000 * 2 + 3000);
            Assert.That(second.Note, Is.EqualTo(60), "starts from the first note again");
            Assert.That(second.At, Is.EqualTo(54000).Within(1), "on the grid: the next 16th after the key");
        }

        [Test]
        public void RetriggerOnTheBarRestartsTheFigureAtEachBarLine()
        {
            var free = Make(0, rateIndex: 4);                                      // 16 steps a bar, a 3-note chord: 16 mod 3 is 1
            Hold(free, new[] { 60, 64, 67 });
            free.RunTo(4 * 4 * 3);
            var bar = Make(0, rateIndex: 4).Set(P.Retrigger, 2);
            Hold(bar, new[] { 60, 64, 67 });
            bar.RunTo(4 * 4 * 3);
            var freeBarStarts = free.Ons.Where(r => Math.Abs(r.Beat - Math.Round(r.Beat / 4) * 4) < 1e-6).Select(r => r.Note).ToArray();
            var barStarts = bar.Ons.Where(r => Math.Abs(r.Beat - Math.Round(r.Beat / 4) * 4) < 1e-6).Select(r => r.Note).ToArray();
            Assert.That(barStarts, Is.EqualTo(new[] { 60, 60, 60 }), "the figure starts from its first note on every bar line");
            Assert.That(freeBarStarts, Is.Not.EqualTo(barStarts), "free running goes on through the bar line");
        }

        [Test]
        public void AKeyPressedBetweenStepsWaitsForTheNextOneOnFreeRunning()
        {
            var rig = Make(0, rateIndex: 4);
            rig.Block = 64;
            rig.NoteOn(6000 * 5 + 1234, 60);
            rig.NoteOn(6000 * 5 + 1234, 64);
            rig.Run(48000);
            var first = rig.Ons.First();
            Assert.That(first.At, Is.EqualTo(6000 * 6).Within(1), "the next step of the grid");
            Assert.That(first.Note, Is.EqualTo(new[] { 60, 64 }[6 % 2]), "the figure at step 6");
        }

        [Test]
        public void HumanizeIsLateByAtMostTheSetTimeAndRepeats()
        {
            List<ArpRig.Rec> Run()
            {
                var rig = Make(0, rateIndex: 4).Set(P.HumTime, 12).Set(P.HumVel, 30);
                Hold(rig, Played);
                rig.RunTo(4 * 8);
                return rig.Log;
            }
            var a = Run();
            var b = Run();
            Assert.That(b.Select(r => (r.At, r.Note, r.Vel)), Is.EqualTo(a.Select(r => (r.At, r.Note, r.Vel))), "seeded: the same every run");
            var ons = a.Where(r => r.Kind == ArpKind.NoteOn).ToList();
            bool late = false;
            for (int k = 0; k < ons.Count; k++)
            {
                double late_ = ons[k].At - k * 6000.0;
                Assert.That(late_, Is.InRange(-1.0, 12 * 48.0 + 1), $"on {k}");
                if (late_ > 5) late = true;
            }
            Assert.That(late, Is.True);
            Assert.That(ons.Select(r => r.Vel).Distinct().Count(), Is.GreaterThan(3));
            Assert.That(ons.All(r => r.Vel is >= 1 and <= 127));
            foreach (var r in ArpRig.Balance(a)) Assert.That(r, Does.Contain("never released"));
        }

        [Test]
        public void TheTransportStopReleasesEverythingAndNothingIsSentAfterwards()
        {
            var rig = Make(0, rateIndex: 4).Set(P.Gate, 200);
            Hold(rig, Played);
            rig.Run(30000);
            Assert.That(rig.E.ActiveCount, Is.GreaterThan(0));
            rig.Playing = false;
            long stoppedAt = rig.Abs;
            rig.NoteOn(rig.Abs + 500, 72);                                         // played while stopped: held, not sent
            rig.Run(48000);
            Assert.That(rig.E.ActiveCount, Is.EqualTo(0));
            Assert.That(rig.OnsWhileStopped, Is.EqualTo(0));
            Assert.That(rig.Log.Where(r => r.At > stoppedAt).All(r => r.Kind == ArpKind.NoteOff && r.At == stoppedAt), Is.True);
            Assert.That(ArpRig.Balance(rig.Log), Is.Empty);
            // Started again, the held notes (the chord and the new one) play.
            rig.Playing = true;
            rig.Run(48000);
            Assert.That(rig.Ons.Where(r => r.At > stoppedAt).Select(r => r.Note).Distinct(), Does.Contain(72));
        }

        [Test]
        public void PanicAndAllNotesOffReleaseEverything()
        {
            var rig = Make(0, rateIndex: 4).Set(P.Gate, 200);
            Hold(rig, Played);
            rig.Run(20000);
            rig.Input(rig.Abs + 100, new ArpEvent { Kind = ArpKind.Controller, Data1 = 123, Data2 = 0 });
            rig.Run(24000);
            Assert.That(rig.E.ActiveCount, Is.EqualTo(0));
            Assert.That(rig.E.HeldCount, Is.EqualTo(0));
            long quiet = rig.Abs;
            rig.Run(24000);
            Assert.That(rig.Ons.Any(r => r.At >= quiet), Is.False);
            // The Panic command, from the UI thread: at the start of the next block.
            var other = Make(0, rateIndex: 4);
            Hold(other, Played);
            other.Run(20000);
            other.E.RequestPanic();
            other.Run(100);
            Assert.That(other.E.ActiveCount, Is.EqualTo(0));
            Assert.That(other.E.HeldCount, Is.EqualTo(0));
        }

        [Test]
        public void OtherEventsPassThroughWhilePlayingAndAreHeldBackWhileStopped()
        {
            var rig = Make(0, rateIndex: 4);
            Hold(rig, Played);
            rig.Input(1000, new ArpEvent { Kind = ArpKind.Controller, Channel = 2, Data1 = 1, Data2 = 99 });
            rig.Input(1100, new ArpEvent { Kind = ArpKind.PitchBend, Data2 = 12000 });
            rig.Run(5000);
            var cc = rig.Log.Single(r => r.Kind == ArpKind.Controller);
            Assert.That(cc.At, Is.EqualTo(1000));
            Assert.That(rig.Log.Single(r => r.Kind == ArpKind.PitchBend).At, Is.EqualTo(1100));
        }

        // ============================================================================================================ no stuck notes

        [Test]
        public void ASeededStressOfTenThousandEventsLeavesNoNoteStuck()
        {
            foreach (int seed in new[] { 1, 2, 3 })
            {
                var rng = new Random(seed);
                var rig = Make(0);
                rig.Rate = 48000;
                rig.Bpm = tick => 90 + (tick / 37 % 5) * 25;                        // tempo changes at ticks, deterministic
                var held = new HashSet<int>();
                int events = 0;
                int stops = 0;
                rig.BeforeBlock = () => { };
                while (events < 10000)
                {
                    int action = rng.Next(100);
                    long at = rig.Abs + rng.Next(0, 400);
                    if (action < 22)
                    {
                        int chord = rng.Next(1, 6);
                        for (int i = 0; i < chord; i++)
                        {
                            int note = rng.Next(24, 100);
                            rig.NoteOn(at + rng.Next(0, 50), note, rng.Next(1, 128));
                            held.Add(note);
                            events++;
                        }
                    }
                    else if (action < 50 && held.Count > 0)
                    {
                        int count = rng.Next(1, held.Count + 1);
                        foreach (int note in held.OrderBy(_ => rng.Next()).Take(count).ToList())
                        {
                            rig.NoteOff(at + rng.Next(0, 50), note);
                            held.Remove(note);
                            events++;
                        }
                    }
                    else if (action < 56) { rig.Set(P.Latch, rng.Next(2)); events++; }
                    else if (action < 66) { rig.Set(P.Pattern, rng.Next(12)); events++; }
                    else if (action < 72) { rig.Set(P.Rate, rng.Next(7)).Set(P.Feel, rng.Next(3)); events++; }
                    else if (action < 75) { rig.Set(P.Sync, rng.Next(2)).Set(P.FreeMs, rng.Next(20, 400)); events++; }
                    else if (action < 79) { rig.Set(P.Gate, rng.Next(5, 201)).Set(P.Swing, rng.Next(0, 76)); events++; }
                    else if (action < 82) { rig.Set(P.Octaves, rng.Next(1, 5)).Set(P.OctMode, rng.Next(3)).Set(P.Repeats, rng.Next(1, 5)); events++; }
                    else if (action < 85) { rig.Set(P.Retrigger, rng.Next(3)); events++; }
                    else if (action < 87) { rig.Set(P.HumTime, rng.Next(0, 21)).Set(P.HumVel, rng.Next(0, 31)); events++; }
                    else if (action < 89) { rig.Set(P.Transpose, rng.Next(-24, 25)); events++; }
                    else if (action < 92)
                    {
                        rig.Playing = !rig.Playing; stops++; events++;
                    }
                    else if (action < 95 && rig.Tick > 50)
                    {
                        rig.Jump(rng.Next(0, (int)rig.Tick)); events++;               // a loop or a locate
                    }
                    else if (action < 96) { rig.Input(at, new ArpEvent { Kind = ArpKind.Controller, Data1 = 123 }); held.Clear(); events++; }
                    else
                    {
                        rig.Row(new string(Enumerable.Range(0, 16).Select(_ => "ONTA"[rng.Next(4)]).ToArray()), Enumerable.Range(0, 16).Select(_ => rng.Next(-12, 13)).ToArray());
                        rig.Set(P.VelMode, rng.Next(3));
                        events++;
                    }
                    rig.Run(rng.Next(1, 3000));
                }
                // The end: the transport stops, and everything sounding is released.
                rig.Playing = false;
                rig.Run(2000);
                rig.Run(2000);
                var problems = ArpRig.Balance(rig.Log);
                Assert.That(problems, Is.Empty, $"seed {seed}: {problems.FirstOrDefault()}");
                Assert.That(rig.E.ActiveCount, Is.EqualTo(0), $"seed {seed}");
                Assert.That(rig.OnsWhileStopped, Is.EqualTo(0), $"seed {seed}: note-ons while stopped");
                Assert.That(rig.Ons.Count(), Is.GreaterThan(500), $"seed {seed}: it played");
                TestContext.Out.WriteLine($"seed {seed}: {events} events, {rig.Ons.Count()} notes, {stops} transport changes, nothing stuck");
            }
        }

        // ============================================================================================================ allocation

        [Test]
        public void TheAudioPathAllocatesNothing()
        {
            var rig = Make(0);
            rig.Block = 64;
            var v = rig.V;
            var input = new ArpEvent[8];
            int step = 0;
            // Everything the engine does in a block, with every pattern, humanize, latch and rate changes, and keys pressed and released.
            var e = rig.E;
            double beat = 0;
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                int n = 0;
                if (b % 40 == 0) { input[n++] = ArpEvent.On(3, 0, 48 + (b / 40 * 7) % 24, 100); input[n++] = ArpEvent.On(10, 0, 60 + (b / 40 * 5) % 12, 90); input[n++] = ArpEvent.On(20, 0, 67, 80); }
                if (b % 40 == 30) { input[n++] = ArpEvent.Off(1, 0, 48 + (b / 40 * 7) % 24); input[n++] = ArpEvent.Off(5, 0, 60 + (b / 40 * 5) % 12); }
                if (b % 50 == 49) input[n++] = new ArpEvent { Offset = 7, Kind = ArpKind.PitchBend, Data2 = 9000 };
                if (b % 25 == 0) { v.Set(P.Pattern, (step++) % 12); v.Set(P.Octaves, 1 + step % 4); v.Set(P.Repeats, 1 + step % 3); v.Set(P.Latch, step % 5 == 0 ? 1 : 0); v.Set(P.HumTime, step % 2 * 6); v.Set(P.HumVel, step % 3 * 10); v.Set(P.Rate, 2 + step % 4); v.Set(P.Swing, step % 4 * 20); v.Set(P.Gate, 30 + step % 6 * 30); }
                var block = new ArpBlock { Samples = 64, Playing = true, Beat = beat, SamplesPerBeat = 24000, BeatsPerBar = 4 };
                beat += 64 / 24000.0;
                e.Process(in block, new ReadOnlySpan<ArpEvent>(input, 0, n));
            });
            Assert.That(bytes, Is.EqualTo(0), why);
            Assert.That(e.OutCount, Is.GreaterThanOrEqualTo(0));
        }

        // ============================================================================================================ state and presets

        [Test]
        public void SavedStateKeepsEveryParameterAndTheStepRow()
        {
            var table = Params.Table;
            var rng = new Random(7);
            var v = new ParamValues(table);
            for (int i = 0; i < table.Length; i++) v.Set(i, rng.Next(table[i].Min, table[i].Max + 1));
            var saved = Enumerable.Range(0, table.Length).Select(i => v[i]).ToArray();
            // ReBuzz saves the globals by position and loads them through the machine's migrations (version 0: unchanged).
            var chain = new StateMigrations(table);
            Assert.That(chain.Current, Is.EqualTo(0));
            Assert.That(chain.Validate(), Is.Empty);
            var loaded = chain.MigrateGlobals(0, saved);
            var w = new ParamValues(table);
            for (int i = 0; i < table.Length; i++) w.Set(i, loaded[i]);
            for (int i = 0; i < table.Length; i++) Assert.That(w[i], Is.EqualTo(v[i]), table[i].Name);
            Assert.That(Enumerable.Range(0, 16).Select(i => w[P.S1 + i]), Is.EqualTo(Enumerable.Range(0, 16).Select(i => v[P.S1 + i])));
            Assert.That(Enumerable.Range(0, 16).Select(i => w[P.X1 + i]), Is.EqualTo(Enumerable.Range(0, 16).Select(i => v[P.X1 + i])));
            // And it plays the same.
            List<ArpRig.Rec> Play(ParamValues pv)
            {
                var r = new ArpRig();
                for (int i = 0; i < table.Length; i++) r.V.Set(i, pv[i]);
                r.Set(P.Latch, 0);
                Hold(r, Played);
                r.Run(48000 * 3);
                return r.Log;
            }
            Assert.That(Play(w).Select(r => (r.At, r.Note, r.Vel)), Is.EqualTo(Play(v).Select(r => (r.At, r.Note, r.Vel))));
        }

        [Test]
        public void TheTableHasTheDocumentedShape()
        {
            var t = Params.Table;
            Assert.That(t.Length, Is.EqualTo(51));
            Assert.That(t[P.Pattern].Names, Is.EqualTo(new[] { "Up", "Down", "Up-down", "Up-down ends twice", "Down-up", "Converge", "Diverge", "As played", "Random", "Random walk", "Chord", "Steps" }));
            Assert.That(t[P.Rate].Names, Is.EqualTo(new[] { "1/1", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64", "2 bars", "4 bars", "8 bars" }), "the old seven, then 2, 4 and 8 bars appended (SYN1)");
            Assert.That((t[P.Octaves].Min, t[P.Octaves].Max), Is.EqualTo((1, 4)));
            Assert.That((t[P.Repeats].Min, t[P.Repeats].Max), Is.EqualTo((1, 4)));
            Assert.That((t[P.FreeMs].Min, t[P.FreeMs].Max), Is.EqualTo((20, 2000)));
            Assert.That((t[P.Swing].Min, t[P.Swing].Max), Is.EqualTo((0, 75)));
            Assert.That((t[P.Gate].Min, t[P.Gate].Max), Is.EqualTo((5, 200)));
            Assert.That((t[P.FixedVel].Min, t[P.FixedVel].Max), Is.EqualTo((1, 127)));
            Assert.That((t[P.HumTime].Min, t[P.HumTime].Max), Is.EqualTo((0, 20)));
            Assert.That((t[P.HumVel].Min, t[P.HumVel].Max), Is.EqualTo((0, 30)));
            Assert.That(t[P.Transpose].Value(t[P.Transpose].Min), Is.EqualTo(-24));
            Assert.That(t[P.Transpose].Value(t[P.Transpose].Max), Is.EqualTo(24));
            Assert.That(t[P.X1].Value(t[P.X1].Min), Is.EqualTo(-12));
            Assert.That(t[P.X16].Value(t[P.X16].Max), Is.EqualTo(12));
            Assert.That(t[P.S1].Names, Is.EqualTo(new[] { "Off", "On", "Tie", "Accent" }));
            Assert.That((t[P.StepLen].Min, t[P.StepLen].Max), Is.EqualTo((1, 16)));
        }
    }
}
