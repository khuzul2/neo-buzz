using NeoBuzz.Chord;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Chord (docs/specs/neo-chord.md): the acceptance criteria, measured on the engine (the machine adds only the host's note queues).</summary>
    [TestFixture]
    public class NeoChordTests
    {
        // ---------------------------------------------------------------------------------------------------------- harness

        /// <summary>The engine, its parameters and a clock: events are given at absolute samples, blocks of <see cref="B"/> samples are run, what was sent is logged at absolute samples.</summary>
        internal sealed class Rig
        {
            public readonly ParamValues V = new ParamValues(Params.Table);
            public readonly ChordEngine E;
            public readonly int B;
            public long Pos;
            public readonly List<(long T, ChordEvent Ev)> Sent = new();
            readonly List<(long T, ChordEvent Ev)> queue = new();

            public Rig(int block = 64, Action<ParamValues> setup = null)
            {
                B = block;
                setup?.Invoke(V);
                E = new ChordEngine(V);
                E.SetSampleRate(48000);
            }

            public void On(long t, int note, int vel = 100, int ch = 0) => queue.Add((t, ChordEvent.NoteOn(0, ch, note, vel)));
            public void Off(long t, int note, int ch = 0) => queue.Add((t, ChordEvent.NoteOff(0, ch, note)));
            public void Raw(long t, ChordEvent e) => queue.Add((t, e));

            /// <summary>Runs whole blocks until the clock has passed <paramref name="until"/>.</summary>
            public void Run(long until)
            {
                var sorted = queue.OrderBy(q => q.T).ToList();      // stable: events at one sample keep the order they were given in
                queue.Clear();
                queue.AddRange(sorted);
                while (Pos < until)
                {
                    long end = Pos + B;
                    int taken = 0;
                    while (taken < queue.Count && queue[taken].T < end)
                    {
                        var (t, e) = queue[taken++];
                        e.Offset = (int)Math.Max(0, t - Pos);
                        E.Input(e);
                    }
                    queue.RemoveRange(0, taken);
                    foreach (var o in E.Process(B)) Sent.Add((Pos + o.Offset, o));
                    Pos = end;
                }
            }

            public IEnumerable<(long T, int Note)> Ons => Sent.Where(s => s.Ev.Kind == ChordEvent.On).Select(s => (s.T, (int)s.Ev.Data1));
            public IEnumerable<(long T, int Note)> Offs => Sent.Where(s => s.Ev.Kind == ChordEvent.Off).Select(s => (s.T, (int)s.Ev.Data1));
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));
        static void Pick(ParamValues v, int p, string name) => v.Set(p, Array.IndexOf(Params.Table[p].Names, name));

        // ---------------------------------------------------------------------------------------------------------- theory

        /// <summary>Chord tones as formulas of the major scale (b flat, # sharp, 8 and above are the next octave): written from theory, not from the engine's tables.</summary>
        static readonly (int Kind, string Name, string Formula)[] Theory =
        {
            (ChordKind.Major, "Major", "1 3 5"),
            (ChordKind.Minor, "Minor", "1 b3 5"),
            (ChordKind.Power, "Power", "1 5 8"),
            (ChordKind.Sus2, "Sus2", "1 2 5"),
            (ChordKind.Sus4, "Sus4", "1 4 5"),
            (ChordKind.Diminished, "Diminished", "1 b3 b5"),
            (ChordKind.Augmented, "Augmented", "1 3 #5"),
            (ChordKind.Major6, "Major 6", "1 3 5 6"),
            (ChordKind.Minor6, "Minor 6", "1 b3 5 6"),
            (ChordKind.Dominant7, "Dominant 7", "1 3 5 b7"),
            (ChordKind.Major7, "Major 7", "1 3 5 7"),
            (ChordKind.Minor7, "Minor 7", "1 b3 5 b7"),
            (ChordKind.Minor7Flat5, "Minor 7 flat 5", "1 b3 b5 b7"),
            (ChordKind.Diminished7, "Diminished 7", "1 b3 b5 bb7"),
            (ChordKind.Add9, "Add 9", "1 3 5 9"),
            (ChordKind.MinorAdd9, "Minor add 9", "1 b3 5 9"),
            (ChordKind.Dominant9, "Dominant 9", "1 3 5 b7 9"),
            (ChordKind.Major9, "Major 9", "1 3 5 7 9"),
            (ChordKind.Minor9, "Minor 9", "1 b3 5 b7 9"),
            (ChordKind.StackedFourths, "Stacked fourths", "1 4 b7 b10"),
        };

        static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };

        /// <summary>Semitones above the root of a formula's tone ("b3" is 3, a flat: 3 in the major scale is 4, minus 1).</summary>
        static int Tone(string t)
        {
            int flat = 0;
            while (t.StartsWith("b")) { flat++; t = t.Substring(1); }
            if (t.StartsWith("#")) { flat--; t = t.Substring(1); }
            int n = int.Parse(t);
            return MajorScale[(n - 1) % 7] + 12 * ((n - 1) / 7) - flat;
        }

        static int[] Intervals(string formula) => formula.Split(' ').Select(Tone).ToArray();

        static int[] BuildNotes(ChordSettings s, int note, out int root)
        {
            var buf = new int[ChordBuilder.MaxNotes + 1];
            int n = ChordBuilder.Build(s, note, buf, out root);
            return buf.Take(n).ToArray();
        }

        [Test]
        public void EveryChordTypeHasTheRightPitchClassesOnAllTwelveRoots()
        {
            Assert.That(Theory.Length, Is.EqualTo(20), "every shape but Off and Custom");
            foreach (var (kind, name, formula) in Theory)
            {
                var intervals = Intervals(formula);
                for (int root = 0; root < 12; root++)
                {
                    int note = 48 + root;
                    var got = BuildNotes(ChordSettings.Plain(kind), note, out int r);
                    Assert.That(r, Is.EqualTo(note), name);
                    Assert.That(got, Is.EqualTo(intervals.Select(i => note + i).ToArray()), $"{name} on {root}: the notes, lowest first, from the root up");
                    var want = intervals.Select(i => (root + i) % 12).OrderBy(x => x).ToArray();
                    Assert.That(got.Select(p => p % 12).Distinct().OrderBy(x => x).ToArray(), Is.EqualTo(want.Distinct().ToArray()), $"{name} on {root}: pitch classes");
                }
            }
        }

        [Test]
        public void OffIsTheNoteItself()
        {
            var got = BuildNotes(ChordSettings.Plain(ChordKind.Off), 61, out int root);
            Assert.That(got, Is.EqualTo(new[] { 61 }));
            Assert.That(root, Is.EqualTo(61));
            // Off ignores the voicing: it is a pass-through.
            var s = ChordSettings.Plain(ChordKind.Off);
            s.Inversion = 2; s.Octave = 1; s.Bass = 2; s.Spread = 3;
            Assert.That(BuildNotes(s, 61, out _), Is.EqualTo(new[] { 61 }));
        }

        /// <summary>The seven scales' steps as whole and half steps (written out from theory): the modes are rotations of the major scale's pattern.</summary>
        static int[] ScaleFromSteps(int[] steps) { var o = new int[7]; int a = 0; for (int i = 0; i < 7; i++) { o[i] = a; a += steps[i]; } return o; }

        static readonly int[] MajorSteps = { 2, 2, 1, 2, 2, 2, 1 };
        static int[] Mode(int rotation) => ScaleFromSteps(Enumerable.Range(0, 7).Select(i => MajorSteps[(i + rotation) % 7]).ToArray());

        static readonly (string Name, int[] Offsets)[] TheoryScales =
        {
            ("Major", Mode(0)),
            ("Natural minor", Mode(5)),                                             // the sixth mode of the major scale
            ("Harmonic minor", ScaleFromSteps(new[] { 2, 1, 2, 2, 1, 3, 1 })),      // whole, half, whole, whole, half, an augmented second, half
            ("Melodic minor", ScaleFromSteps(new[] { 2, 1, 2, 2, 2, 2, 1 })),       // the rising form
            ("Dorian", Mode(1)),
            ("Phrygian", Mode(2)),
            ("Lydian", Mode(3)),
            ("Mixolydian", Mode(4)),
            ("Locrian", Mode(6)),
        };

        [Test]
        public void DiatonicGivesTheRightTriadAndSeventhOnEveryDegreeOfEveryScaleInEveryKey()
        {
            int checkedChords = 0;
            for (int scale = 0; scale < TheoryScales.Length; scale++)
            {
                var off = TheoryScales[scale].Offsets;
                Assert.That(off, Is.EqualTo(ChordTheory.Scales[scale]), $"{TheoryScales[scale].Name}: the steps");
                for (int key = 0; key < 12; key++)          // C and every sharp key among them
                    for (int degree = 0; degree < 7; degree++)
                        foreach (bool sevenths in new[] { false, true })
                        {
                            var s = ChordSettings.Plain(ChordKind.Major);
                            s.Diatonic = true; s.Key = key; s.Scale = scale; s.Sevenths = sevenths;
                            int keyNote = 48 + key;
                            int note = keyNote + off[degree] + (degree % 2 == 0 ? 0 : 12);     // played in two octaves
                            var got = BuildNotes(s, note, out int root);
                            // The chord is every other note of the scale from the degree played: 1 3 5 (7).
                            int count = sevenths ? 4 : 3;
                            var want = new int[count];
                            for (int k = 0; k < count; k++)
                            {
                                int step = degree + 2 * k;
                                want[k] = note - off[degree] + off[step % 7] + 12 * (step / 7);
                            }
                            Assert.That(got, Is.EqualTo(want), $"{TheoryScales[scale].Name} in {key}, degree {degree + 1}, sevenths {sevenths}");
                            Assert.That(root, Is.EqualTo(note));
                            // And the pitch classes are in the scale.
                            var inScale = off.Select(o => (key + o) % 12).ToHashSet();
                            Assert.That(got.All(p => inScale.Contains(p % 12)), Is.True, $"{TheoryScales[scale].Name} in {key}: every note is in the scale");
                            checkedChords++;
                        }
            }
            Assert.That(checkedChords, Is.EqualTo(9 * 12 * 7 * 2));
        }

        [Test]
        public void DiatonicChordsAreTheFamiliarOnes()
        {
            // C major's triads: C Em... by name: I major, ii minor, iii minor, IV major, V major, vi minor, vii diminished.
            string[] triads = { "1 3 5", "1 b3 5", "1 b3 5", "1 3 5", "1 3 5", "1 b3 5", "1 b3 b5" };
            string[] sevenths = { "1 3 5 7", "1 b3 5 b7", "1 b3 5 b7", "1 3 5 7", "1 3 5 b7", "1 b3 5 b7", "1 b3 b5 b7" };
            for (int key = 0; key < 12; key++)
                for (int d = 0; d < 7; d++)
                    foreach (bool seven in new[] { false, true })
                    {
                        var s = ChordSettings.Plain(ChordKind.Major);
                        s.Diatonic = true; s.Key = key; s.Scale = 0; s.Sevenths = seven;
                        int note = 60 + key + MajorScale[d];
                        var got = BuildNotes(s, note, out _);
                        var want = Intervals(seven ? sevenths[d] : triads[d]).Select(i => note + i).ToArray();
                        Assert.That(got, Is.EqualTo(want), $"key {key} degree {d + 1} sevenths {seven}");
                    }
        }

        [Test]
        public void ANoteOutsideTheScaleSnapsToTheScaleNoteBelow()
        {
            var s = ChordSettings.Plain(ChordKind.Major);
            s.Diatonic = true; s.Key = 0; s.Scale = 0;
            // C major: C# plays the C chord, D# the D chord, F# the F chord, G# the G chord, A# the A chord.
            int[] outside = { 61, 63, 66, 68, 70 }, snapTo = { 60, 62, 65, 67, 69 };
            for (int i = 0; i < outside.Length; i++)
            {
                var got = BuildNotes(s, outside[i], out int root);
                Assert.That(root, Is.EqualTo(snapTo[i]), $"{outside[i]}");
                Assert.That(got[0], Is.EqualTo(snapTo[i]));
                var inKey = BuildNotes(s, snapTo[i], out _);
                Assert.That(got, Is.EqualTo(inKey), "the same chord as the scale note itself");
            }
            // In G major (one sharp) F is outside: it plays the E chord; F# is the leading tone.
            s.Key = 7;
            Assert.That(BuildNotes(s, 65, out int r1)[0], Is.EqualTo(64));
            Assert.That(r1, Is.EqualTo(64));
        }

        [Test]
        public void InversionsAndSpreadsGiveTheRightOrder()
        {
            var s = ChordSettings.Plain(ChordKind.Major);
            Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 60, 64, 67 }));
            s.Inversion = 1; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 64, 67, 72 }), "first inversion: the third on the bottom");
            s.Inversion = 2; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 67, 72, 76 }), "second inversion: the fifth on the bottom");
            s.Inversion = 3; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 72, 76, 79 }), "third on a triad: root position an octave up");
            s.Inversion = 0;
            s.Spread = 1; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 60, 67, 76 }), "open: every second note an octave up");
            s.Spread = 2; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 52, 60, 67 }), "drop 2: the second from the top an octave down");
            s.Spread = 3; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 48, 67, 76 }), "wide: open, with the lowest an octave down");

            var m7 = ChordSettings.Plain(ChordKind.Major7);
            Assert.That(BuildNotes(m7, 60, out _), Is.EqualTo(new[] { 60, 64, 67, 71 }));
            m7.Inversion = 1; Assert.That(BuildNotes(m7, 60, out _), Is.EqualTo(new[] { 64, 67, 71, 72 }));
            m7.Inversion = 0; m7.Spread = 1; Assert.That(BuildNotes(m7, 60, out _), Is.EqualTo(new[] { 60, 67, 76, 83 }));
            m7.Spread = 2; Assert.That(BuildNotes(m7, 60, out _), Is.EqualTo(new[] { 55, 60, 64, 71 }), "drop 2 of a seventh chord: the fifth goes down");

            // Inversion and spread together: the inversion first.
            var both = ChordSettings.Plain(ChordKind.Major); both.Inversion = 1; both.Spread = 2;
            Assert.That(BuildNotes(both, 60, out _), Is.EqualTo(new[] { 55, 64, 72 }));
        }

        [Test]
        public void OctaveBassVoicesCustomAndRange()
        {
            var s = ChordSettings.Plain(ChordKind.Major);
            s.Octave = 1; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 72, 76, 79 }));
            s.Octave = -2; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 36, 40, 43 }));
            s.Octave = 0;
            s.Bass = 1; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 48, 60, 64, 67 }), "root an octave down");
            s.Bass = 2; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 36, 60, 64, 67 }), "root two octaves down");
            s.Inversion = 1; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 36, 64, 67, 72 }), "the bass stays on the root through inversions");
            s.Inversion = 0; s.Octave = 1; s.Bass = 1; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 60, 72, 76, 79 }), "the bass moves with the octave shift");
            s.Octave = 0;
            s.Voices = 2; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 48, 60 }), "the highest go first, the bass counts");
            s.Bass = 0; s.Voices = 2; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 60, 64 }));
            s.Voices = 7; Assert.That(BuildNotes(s, 60, out _), Is.EqualTo(new[] { 60, 64, 67 }));
            var nine = ChordSettings.Plain(ChordKind.Major9); nine.Voices = 4;
            Assert.That(BuildNotes(nine, 60, out _), Is.EqualTo(new[] { 60, 64, 67, 71 }), "a ninth chord limited to four voices loses its ninth");

            // Custom: root and up to four intervals, any of them negative, Off ones left out, duplicates one note.
            var c = ChordSettings.Plain(ChordKind.Custom);
            c.Custom1 = 4; c.Custom2 = 7;
            Assert.That(BuildNotes(c, 60, out _), Is.EqualTo(new[] { 60, 64, 67 }));
            c.Custom1 = -5; c.Custom2 = 7; c.Custom3 = 24; c.Custom4 = -24;
            Assert.That(BuildNotes(c, 60, out _), Is.EqualTo(new[] { 36, 55, 60, 67, 84 }));
            c.Custom3 = 7; c.Custom4 = ChordSettings.Off;
            Assert.That(BuildNotes(c, 60, out _), Is.EqualTo(new[] { 55, 60, 67 }), "the same pitch twice is one note");

            // Notes outside the keyboard are left out.
            Assert.That(BuildNotes(ChordSettings.Plain(ChordKind.Major), 125, out _), Is.EqualTo(new[] { 125 }));
            Assert.That(BuildNotes(ChordSettings.Plain(ChordKind.Major), 120, out _), Is.EqualTo(new[] { 120, 124, 127 }));
        }

        // ---------------------------------------------------------------------------------------------------------- timing

        [TestCase(16)]
        [TestCase(64)]
        [TestCase(256)]
        public void StrumOffsetsLandOnTheExactSample(int block)
        {
            // 100 ms at 48 kHz is 4800 samples: a triad strummed up starts at 0, 2400 and 4800 after the note.
            foreach (long t0 in new long[] { 0, 1, 15, 16, 63, 100, 255, 256, 1000, 4799 })
            {
                var rig = new Rig(block, v => { Pick(v, P.Chord, "Major"); Pick(v, P.Strum, "Up"); Set(v, P.StrumTime, 100); });
                rig.On(t0, 60);
                rig.Off(t0 + 9000, 60);
                rig.Run(t0 + 12000);
                Assert.That(rig.Ons.ToArray(), Is.EqualTo(new[] { (t0, 60), (t0 + 2400, 64), (t0 + 4800, 67) }), $"block {block}, note at {t0}");
                Assert.That(rig.Offs.Select(o => o.T).Distinct().ToArray(), Is.EqualTo(new[] { t0 + 9000 }), "every voice ends when its note ends");
                Assert.That(rig.Offs.Select(o => o.Note).OrderBy(x => x).ToArray(), Is.EqualTo(new[] { 60, 64, 67 }));
            }
        }

        [TestCase(16)]
        [TestCase(64)]
        [TestCase(256)]
        public void StrumDownAndCurveLandOnTheExactSample(int block)
        {
            // Down: the highest first. A five-note chord over 250 ms (12000 samples) with the curve at +100 % and −100 %: the delays are the curve's, rounded to the sample.
            foreach (double curve in new[] { 100.0, -100.0, 40.0, 0.0 })
            {
                var rig = new Rig(block, v => { Pick(v, P.Chord, "Minor 9"); Pick(v, P.Strum, "Down"); Set(v, P.StrumTime, 250); Set(v, P.StrumCurve, curve / 100); });
                rig.On(300, 60);
                rig.Run(20000);
                var ons = rig.Ons.ToArray();
                Assert.That(ons.Length, Is.EqualTo(5));
                Assert.That(ons.Select(o => o.Note).ToArray(), Is.EqualTo(new[] { 74, 70, 67, 63, 60 }), "highest first");
                for (int k = 0; k < 5; k++)
                {
                    double pos = k / 4.0;
                    double share = curve == 0 ? pos : curve > 0 ? 1 - Math.Pow(1 - pos, 4) : Math.Pow(pos, 4);       // 2^(2 * |curve|) = 4 at ±100 %
                    if (Math.Abs(curve) == 40) share = 1 - Math.Pow(1 - pos, Math.Pow(2, 2 * 0.4));
                    Assert.That(ons[k].T, Is.EqualTo(300 + (long)Math.Round(share * 12000)), $"curve {curve}, voice {k}");
                }
                if (curve > 0) { var gaps = Gaps(ons); Assert.That(gaps, Is.Ordered.Descending, "accelerating: the gaps shrink"); }
                if (curve < 0) { var gaps = Gaps(ons); Assert.That(gaps, Is.Ordered.Ascending, "slowing: the gaps grow"); }
            }
        }

        static long[] Gaps((long T, int Note)[] ons) => Enumerable.Range(1, ons.Length - 1).Select(i => ons[i].T - ons[i - 1].T).ToArray();

        [TestCase(16)]
        [TestCase(64)]
        [TestCase(256)]
        public void OffModePassesNotesThroughOnTheSameSample(int block)
        {
            var rig = new Rig(block, v => { Pick(v, P.Chord, "Off"); Pick(v, P.Strum, "Up"); Set(v, P.StrumTime, 200); Set(v, P.HumTime, 30); Set(v, P.HumVel, 30); Set(v, P.VelTop, 150); });
            var rnd = new Random(5);
            var want = new List<(long, int, int)>();
            long t = 3;
            for (int i = 0; i < 40; i++)
            {
                int note = 30 + i, vel = 1 + rnd.Next(127);
                rig.On(t, note, vel);
                want.Add((t, note, vel));
                rig.Off(t + 100 + rnd.Next(5000), note);
                t += 1 + rnd.Next(700);
            }
            rig.Run(t + 7000);
            var ons = rig.Sent.Where(s => s.Ev.Kind == ChordEvent.On).Select(s => (s.T, (int)s.Ev.Data1, s.Ev.Data2)).ToList();
            Assert.That(ons, Is.EqualTo(want), "every note on, on its own sample, with its own velocity, and nothing else");
            Assert.That(rig.Sent.Count(s => s.Ev.Kind == ChordEvent.Off), Is.EqualTo(40));
        }

        [Test]
        public void OtherEventsPassThroughAtTheirOffsets()
        {
            var rig = new Rig(64, v => Pick(v, P.Chord, "Major"));
            rig.Raw(70, new ChordEvent { Kind = 2, Channel = 3, Data1 = 1, Data2 = 99 });         // a controller
            rig.Raw(130, new ChordEvent { Kind = 3, Channel = 0, Data1 = 0, Data2 = 12000 });     // a pitch bend
            rig.Run(256);
            var other = rig.Sent.Where(s => s.Ev.Kind >= 2).ToList();
            Assert.That(other.Select(o => (o.T, (int)o.Ev.Kind, (int)o.Ev.Channel, o.Ev.Data2)).ToArray(), Is.EqualTo(new[] { (70L, 2, 3, 99), (130L, 3, 0, 12000) }));
        }

        [Test]
        public void StrumDirectionsAlternateAndRandom()
        {
            // Alternate: the first chord goes up, the next down, and so on, one chord per note on.
            var rig = new Rig(64, v => { Pick(v, P.Chord, "Major"); Pick(v, P.Strum, "Alternate"); Set(v, P.StrumTime, 60); });
            for (int c = 0; c < 4; c++) { rig.On(c * 6000, 60 + c); rig.Off(c * 6000 + 5000, 60 + c); }
            rig.Run(25000);
            var chords = rig.Ons.GroupBy(o => (o.T / 6000)).Select(g => g.OrderBy(x => x.T).Select(x => x.Note).ToArray()).ToArray();
            Assert.That(chords[0], Is.EqualTo(new[] { 60, 64, 67 }), "up");
            Assert.That(chords[1], Is.EqualTo(new[] { 68, 65, 61 }), "down");
            Assert.That(chords[2], Is.EqualTo(new[] { 62, 66, 69 }), "up");
            Assert.That(chords[3], Is.EqualTo(new[] { 70, 67, 63 }), "down");

            // Random: every voice once, the same order for the same seed, other orders for other seeds.
            string Order(int seed)
            {
                var r = new Rig(64, v => { Pick(v, P.Chord, "Dominant 9"); Pick(v, P.Strum, "Random"); Set(v, P.StrumTime, 100); v.Set(P.Seed, seed); });
                r.On(0, 60); r.Off(8000, 60);
                r.Run(9000);
                var ons = r.Ons.OrderBy(o => o.T).ToArray();
                Assert.That(ons.Select(o => o.Note).OrderBy(x => x).ToArray(), Is.EqualTo(new[] { 60, 64, 67, 70, 74 }));
                Assert.That(ons.Select(o => o.T).ToArray(), Is.EqualTo(new long[] { 0, 1200, 2400, 3600, 4800 }), "random order, even spacing");
                return string.Join(",", ons.Select(o => o.Note));
            }
            Assert.That(Order(3), Is.EqualTo(Order(3)));
            Assert.That(Enumerable.Range(0, 12).Select(Order).Distinct().Count(), Is.GreaterThan(3), "different seeds give different orders");
        }

        [Test]
        public void HumanizeIsSeededAndBounded()
        {
            List<(long, int, int)> Run(int seed)
            {
                var rig = new Rig(64, v => { Pick(v, P.Chord, "Minor 7"); Set(v, P.HumTime, 30); Set(v, P.HumVel, 30); v.Set(P.Seed, seed); });
                for (int i = 0; i < 20; i++) { rig.On(i * 5000, 60 + i % 5, 100); rig.Off(i * 5000 + 4000, 60 + i % 5); }
                rig.Run(105000);
                return rig.Sent.Where(s => s.Ev.Kind == ChordEvent.On).Select(s => (s.T, (int)s.Ev.Data1, s.Ev.Data2)).ToList();
            }
            var a = Run(0);
            Assert.That(Run(0), Is.EqualTo(a), "the same seed, the same render");
            Assert.That(Run(1), Is.Not.EqualTo(a));
            // Within 30 ms (1440 samples) late, within 30 % of the velocity.
            for (int i = 0; i < 20; i++)
            {
                var chord = a.Where(x => x.Item1 >= i * 5000 && x.Item1 < i * 5000 + 4000).ToList();
                Assert.That(chord.Count, Is.EqualTo(4));
                Assert.That(chord.All(x => x.Item1 - i * 5000 <= 1440), Is.True);
                Assert.That(chord.All(x => x.Item3 >= 70 && x.Item3 <= 130 && x.Item3 <= 127), Is.True);
            }
            Assert.That(a.Select(x => x.Item3).Distinct().Count(), Is.GreaterThan(5), "the velocities do vary");
        }

        [Test]
        public void VelocityShaping()
        {
            var rig = new Rig(64, v => { Pick(v, P.Chord, "Major 7"); Set(v, P.VelTop, 50); Set(v, P.VelBass, 120); });
            rig.On(0, 60, 100);
            rig.Run(64);
            var vel = rig.Sent.Where(s => s.Ev.Kind == ChordEvent.On).OrderBy(s => s.Ev.Data1).Select(s => s.Ev.Data2).ToArray();
            Assert.That(vel, Is.EqualTo(new[] { 120, 100, 100, 50 }), "bass 120 %, top 50 %, the inner notes as played");

            var tilt = new Rig(64, v => { Pick(v, P.Chord, "Major 7"); Set(v, P.VelSpread, 1.0); });
            tilt.On(0, 60, 80);
            tilt.Run(64);
            var t = tilt.Sent.Where(s => s.Ev.Kind == ChordEvent.On).OrderBy(s => s.Ev.Data1).Select(s => s.Ev.Data2).ToArray();
            Assert.That(t, Is.Ordered.Ascending, "plus tilts the velocity up toward the top");
            Assert.That(t[0], Is.EqualTo(40)); Assert.That(t[3], Is.EqualTo(120));
        }

        // ---------------------------------------------------------------------------------------------------------- notes off

        [Test]
        public void NotesOffGoToThePitchesActuallySentAndAFastReleaseCancelsTheRest()
        {
            var rig = new Rig(64, v => { Pick(v, P.Chord, "Major"); Pick(v, P.Strum, "Up"); Set(v, P.StrumTime, 250); });
            rig.On(0, 60);
            rig.Off(7000, 60);              // 7000 samples: after the second voice (6000), before the third (12000)
            rig.On(10000, 62);
            rig.Run(30000);
            var ons = rig.Ons.ToArray();
            Assert.That(ons.Where(o => o.T < 10000).Select(o => o.Note).ToArray(), Is.EqualTo(new[] { 60, 64 }), "the third voice was cancelled, never sent late");
            Assert.That(ons.Count(o => o.Note == 67), Is.EqualTo(0), "the first chord's fifth was never sent (the second chord, D major, has no G)");
            Assert.That(ons.Where(o => o.T >= 10000).Select(o => (o.T, o.Note)).ToArray(), Is.EqualTo(new[] { (10000L, 62), (16000L, 66), (22000L, 69) }));
            Assert.That(rig.Sent.Count(s => s.Ev.Kind == ChordEvent.On), Is.EqualTo(rig.Sent.Count(s => s.Ev.Kind == ChordEvent.Off) + 3), "the second chord is still held");
        }

        [Test]
        public void ChangingParametersWhileHeldNeverStrandsANote()
        {
            var rig = new Rig(64, v => Pick(v, P.Chord, "Major"));
            rig.On(0, 60);
            rig.Run(640);
            Pick(rig.V, P.Chord, "Minor 7"); Pick(rig.V, P.Mode, "Diatonic"); Pick(rig.V, P.Spread, "Wide"); Pick(rig.V, P.Bass, "Root 2 oct down"); rig.V.Set(P.Octave, 4);
            rig.Off(700, 60);
            rig.Run(1280);
            Assert.That(rig.Offs.Select(o => o.Note).OrderBy(x => x).ToArray(), Is.EqualTo(new[] { 60, 64, 67 }), "the offs are the major triad's, not the new settings'");
            Assert.That(rig.Ons.Select(o => o.Note).OrderBy(x => x).ToArray(), Is.EqualTo(new[] { 60, 64, 67 }));
        }

        [Test]
        public void PitchesTwoChordsShareKeepSoundingUntilTheLastOwnerEnds()
        {
            // C major (60 64 67) and E minor (64 67 71) share E and G.
            var rig = new Rig(64, v => Pick(v, P.Chord, "Major"));
            Pick(rig.V, P.Chord, "Major");
            rig.On(0, 60);
            rig.Run(128);
            Pick(rig.V, P.Chord, "Minor");
            rig.On(130, 64);
            rig.Run(256);
            Assert.That(rig.Ons.Select(o => o.Note).OrderBy(x => x).ToArray(), Is.EqualTo(new[] { 60, 64, 67, 71 }), "E and G are not struck twice");
            rig.Off(300, 60);
            rig.Run(384);
            Assert.That(rig.Offs.Select(o => o.Note).ToArray(), Is.EqualTo(new[] { 60 }), "only the C ends: E and G still belong to the second chord");
            rig.Off(400, 64);
            rig.Run(512);
            Assert.That(rig.Offs.Select(o => o.Note).OrderBy(x => x).ToArray(), Is.EqualTo(new[] { 60, 64, 67, 71 }), "then the rest, each once");
            Assert.That(rig.E.SentOn, Is.EqualTo(rig.E.SentOff));
        }

        [Test]
        public void TheSamePitchOnAnotherChannelIsCountedApart()
        {
            var rig = new Rig(64, v => Pick(v, P.Chord, "Major"));
            rig.On(0, 60, 100, 0);
            rig.On(10, 60, 100, 5);
            rig.Off(100, 60, 0);
            rig.Run(256);
            Assert.That(rig.Sent.Count(s => s.Ev.Kind == ChordEvent.On), Is.EqualTo(6));
            Assert.That(rig.Sent.Where(s => s.Ev.Kind == ChordEvent.Off).Select(s => (int)s.Ev.Channel).Distinct().ToArray(), Is.EqualTo(new[] { 0 }));
            Assert.That(rig.Sent.Count(s => s.Ev.Kind == ChordEvent.Off), Is.EqualTo(3));
        }

        [Test]
        public void PanicAndAllNotesOffReleaseEverythingAndCancelWhatHasNotStarted()
        {
            var rig = new Rig(64, v => { Pick(v, P.Chord, "Major 9"); Pick(v, P.Strum, "Up"); Set(v, P.StrumTime, 250); });
            rig.On(0, 60); rig.On(10, 62); rig.On(20, 64);
            rig.Run(3200);                                  // the strums are partly done
            rig.E.Panic();
            rig.Run(3300);
            long onsBefore = rig.E.SentOn;
            Assert.That(rig.E.SentOff, Is.EqualTo(onsBefore), "every note that went on has gone off");
            Assert.That(rig.E.HeldSources, Is.EqualTo(0));
            rig.Run(40000);
            Assert.That(rig.E.SentOn, Is.EqualTo(onsBefore), "no cancelled voice starts late");

            // Controller 123 (all notes off) at its own offset.
            var r2 = new Rig(64, v => Pick(v, P.Chord, "Minor"));
            r2.On(0, 50); r2.On(0, 55);
            r2.Raw(200, new ChordEvent { Kind = 2, Channel = 0, Data1 = 123, Data2 = 0 });
            r2.Run(512);
            Assert.That(r2.Offs.Select(o => o.T).Distinct().ToArray(), Is.EqualTo(new long[] { 200 }));
            Assert.That(r2.E.SentOn, Is.EqualTo(r2.E.SentOff));
            Assert.That(r2.E.HeldSources, Is.EqualTo(0));
        }

        [Test]
        public void ANoteOnForAHeldKeyEndsTheOldChordFirst()
        {
            var rig = new Rig(64, v => Pick(v, P.Chord, "Major"));
            rig.On(0, 60); rig.On(100, 60); rig.Off(200, 60);
            rig.Run(320);
            Assert.That(rig.Ons.Select(o => o.T).ToArray(), Is.EqualTo(new long[] { 0, 0, 0, 100, 100, 100 }));
            Assert.That(rig.E.SentOn, Is.EqualTo(rig.E.SentOff));
            Assert.That(rig.Offs.Select(o => o.T).Distinct().OrderBy(x => x).ToArray(), Is.EqualTo(new long[] { 100, 200 }));
        }

        // ---------------------------------------------------------------------------------------------------------- stress

        /// <summary>
        /// Ten thousand random events (overlapping notes on two channels, parameter changes in the middle of a hold, strums cut short by fast releases, the transport
        /// stopping, all notes off) at three block sizes: at the end every note on that went out has exactly one note off; a pitch is never started twice or ended twice;
        /// and no voice starts after its source ended (nor ends later than its source did).
        /// </summary>
        [TestCase(16, 1)]
        [TestCase(64, 2)]
        [TestCase(256, 3)]
        public void NothingSticksInATenThousandEventStress(int block, int seed)
        {
            var rnd = new Random(seed * 7919);
            var rig = new Rig(block);
            var e = rig.E;
            // The holds the test fed, numbered as the engine numbers its sources (one per note on): the key, when it began and when it ended.
            var spans = new List<(int Key, long On, long End)>();
            var held = new Dictionary<int, int>();                     // key (channel * 128 + note) -> the index of its open hold
            void Close(int key, long t) { if (held.TryGetValue(key, out int i)) { spans[i] = (key, spans[i].On, t); held.Remove(key); } }
            void CloseAll(long t) { foreach (var key in held.Keys.ToList()) Close(key, t); }
            int violations = 0;
            string first = null;
            e.Voice = (channel, source, pitch, started, t, seq) =>
            {
                bool ok = seq >= 0 && seq < spans.Count && spans[(int)seq].Key == channel * 128 + source;
                if (ok)
                {
                    var sp = spans[(int)seq];
                    // A voice starts at its note's own sample (no delay) or before the note ends; it ends exactly when the note does.
                    ok = started ? t >= sp.On && (t == sp.On || t < sp.End) : t == sp.End;
                }
                if (!ok) { violations++; first ??= $"{(started ? "start" : "end")} of {pitch} (source {source} on {channel}, #{seq}) at {t}, hold {(seq >= 0 && seq < spans.Count ? spans[(int)seq].ToString() : "?")}"; }
            };
            int events = 0;
            long now = 0, lastT = 0;
            var sounding = new HashSet<int>();                           // (channel, pitch) the output says is on
            int bad = 0, checkedUpTo = 0;
            void Check()
            {
                for (int i = checkedUpTo; i < rig.Sent.Count; i++)
                {
                    var ev = rig.Sent[i].Ev;
                    int k = ev.Channel * 128 + ev.Data1;
                    if (ev.Kind == ChordEvent.On) { if (!sounding.Add(k)) bad++; }
                    else if (ev.Kind == ChordEvent.Off) { if (!sounding.Remove(k)) bad++; }
                }
                checkedUpTo = rig.Sent.Count;
            }
            void Flush()      // everything fed so far is handled, and the clock stands at a block edge
            {
                rig.Run(lastT + 1);
                now = Math.Max(now, rig.Pos);
                lastT = Math.Max(lastT, rig.Pos);
            }
            while (events < 10000)
            {
                int action = rnd.Next(100);
                long t = Math.Max(lastT, now + rnd.Next(block));       // events are fed in time order
                if (action < 38)
                {
                    int ch = rnd.Next(3) == 0 ? 1 : 0, note = 36 + rnd.Next(48);
                    int key = ch * 128 + note;
                    Close(key, t);                                     // a second on for a held key ends the first chord at that sample
                    held[key] = spans.Count;
                    spans.Add((key, t, long.MaxValue));
                    rig.On(t, note, 1 + rnd.Next(127), ch);
                    lastT = t;
                    events++;
                }
                else if (action < 70)
                {
                    if (held.Count > 0)
                    {
                        int key = held.Keys.ElementAt(rnd.Next(held.Count));
                        Close(key, t);
                        rig.Off(t, key % 128, key / 128);
                    }
                    else rig.Off(t, 36 + rnd.Next(48), 0);             // an off for a note never on
                    lastT = t;
                    events++;
                }
                else if (action < 94)
                {
                    int p = rnd.Next(Params.Table.Length);
                    var d = Params.Table[p];
                    int value = rnd.Next(d.Min, d.Max + 1);
                    if (p == P.StrumTime && rnd.Next(2) == 0) value = d.Max;      // lots of long strums to cut short
                    if (p == P.Chord && rnd.Next(4) == 0) value = ChordKind.Custom;
                    Flush();                                           // the change falls between blocks, after the events given so far
                    rig.V.Set(p, value);
                    events++;
                }
                else if (action < 97)
                {
                    Flush();
                    CloseAll(rig.Pos);                                 // the transport stopping ends everything at the start of the next block
                    e.Panic();
                    events++;
                }
                else
                {
                    CloseAll(t);
                    rig.Raw(t, new ChordEvent { Kind = 2, Channel = 0, Data1 = 123 });      // all notes off ends every chord, on every channel
                    lastT = t;
                    events++;
                }
                // Move on by 0 to 3 blocks (sometimes many, so that long strums finish).
                int adv = rnd.Next(10) switch { < 4 => 0, < 8 => 1 + rnd.Next(3), _ => 20 + rnd.Next(300) };
                if (adv > 0) now = Math.Max(now, ((Math.Max(now, rig.Pos) + block - 1) / block + adv) * block);
                if (now > rig.Pos + 4096) { rig.Run(now); Check(); }
            }
            Flush();
            Check();
            // Everything still held ends (the transport stops), and a long wait shows that nothing cancelled starts later.
            CloseAll(rig.Pos);
            e.Panic();
            rig.Run(rig.Pos + 100000);
            Check();
            Assert.That(violations, Is.EqualTo(0), first);
            Assert.That(bad, Is.EqualTo(0), "a pitch was started twice or ended twice");
            Assert.That(sounding, Is.Empty, "a note is still sounding");
            Assert.That(e.SentOn, Is.EqualTo(e.SentOff));
            Assert.That(rig.Sent.Count(s => s.Ev.Kind == ChordEvent.On), Is.EqualTo(rig.Sent.Count(s => s.Ev.Kind == ChordEvent.Off)));
            Assert.That(e.Dropped, Is.EqualTo(0));
            Assert.That(e.HeldSources, Is.EqualTo(0));
            TestContext.Out.WriteLine($"block {block}: {events} events, {e.SentOn} chord notes sent and ended");
            Assert.That(e.SentOn, Is.GreaterThan(5000), "the stress did play notes");
        }

        // ---------------------------------------------------------------------------------------------------------- audio thread

        [Test]
        public void TheEngineAllocatesNothingPerBlock()
        {
            var v = new ParamValues(Params.Table);
            Pick(v, P.Chord, "Major 9"); Pick(v, P.Strum, "Random"); Set(v, P.StrumTime, 120); Set(v, P.HumTime, 10); Set(v, P.HumVel, 20); Pick(v, P.Bass, "Root 1 oct down");
            var e = new ChordEngine(v);
            e.SetSampleRate(48000);
            var rnd = new Random(1);
            const int Block = 64;
            // 64 note ons a block (the keys of the block before end first), their offsets spread over the block.
            var offsets = new int[64];
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                for (int i = 0; i < 64; i++)
                {
                    int note = 30 + i;
                    int off = (i * 7 + b) % Block;
                    e.Input(ChordEvent.NoteOff(off, 0, note));
                    e.Input(ChordEvent.NoteOn(off, 0, note, 90 + (i & 15)));
                }
                if (b % 50 == 49) e.Panic();
                e.Process(Block);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
            Assert.That(e.Dropped, Is.EqualTo(0));
            Assert.That(e.SentOn, Is.GreaterThan(10000), "the blocks did play chords");
        }

        // ---------------------------------------------------------------------------------------------------------- presets

        static readonly string[] ExpectedPresets =
        {
            "Chords — Major Triads", "Chords — Minor Sevenths", "Chords — Open Fifths", "Diatonic — Sevenths in C",
            "Strum — Slow Strum Up", "Strum — Down-Up Strum", "Voicing — Stacked Fourths", "Voicing — Wide Pad Voicing",
        };

        /// <summary>Plays a short progression through an engine with a preset's values and returns what went out (time, note, velocity).</summary>
        static List<(long T, int Note, int Vel, bool On)> Progression(IReadOnlyDictionary<int, int> preset)
        {
            var rig = new Rig(64, v => { foreach (var kv in preset) v.Set(kv.Key, kv.Value); });
            long t = 0;
            foreach (int note in new[] { 60, 65, 67, 62 })
            {
                rig.On(t, note, 100);
                rig.Off(t + 30000, note);
                t += 48000;
            }
            rig.Run(t + 20000);
            return rig.Sent.Select(s => (s.T, (int)s.Ev.Data1, s.Ev.Data2, s.Ev.Kind == ChordEvent.On)).ToList();
        }

        [Test]
        public void ThereAreEightClearlyDifferentPresets()
        {
            var presets = PresetTests.AllPresets().Where(p => p.Machine == "Neo Chord").Select(p => (Name: p.Preset, Values: (IReadOnlyDictionary<int, int>)p.Values.Where(kv => kv.Key >= 0).ToDictionary(kv => kv.Key, kv => kv.Value))).ToList();
            Assert.That(presets.Select(p => p.Name).ToArray(), Is.EqualTo(ExpectedPresets));
            var results = presets.Select(p => (p.Name, Notes: Progression(p.Values))).ToList();
            foreach (var (name, notes) in results)
            {
                Assert.That(notes.Count(n => n.On), Is.GreaterThanOrEqualTo(4 * 3), $"{name}: every note becomes at least three");
                Assert.That(notes.Count(n => n.On), Is.EqualTo(notes.Count(n => !n.On)), name);
            }
            // Different at first listen: no two share the same set of pitches over the progression, nor the same chord sizes and timing.
            for (int i = 0; i < results.Count; i++)
                for (int j = i + 1; j < results.Count; j++)
                {
                    var a = results[i].Notes.Where(n => n.On).Select(n => (n.T, n.Note)).ToList();
                    var b = results[j].Notes.Where(n => n.On).Select(n => (n.T, n.Note)).ToList();
                    Assert.That(a, Is.Not.EqualTo(b), $"{results[i].Name} and {results[j].Name} play the same notes");
                    // And more than a retuned neighbour: the chords' pitch-class sets differ for at least one chord, or the strum timing does.
                    var pa = a.Select(x => x.Note % 12).Distinct().OrderBy(x => x).ToArray();
                    var pb = b.Select(x => x.Note % 12).Distinct().OrderBy(x => x).ToArray();
                    var ta = a.Select(x => x.T % 48000).Distinct().Count();
                    var tb = b.Select(x => x.T % 48000).Distinct().Count();
                    Assert.That(pa.SequenceEqual(pb) && ta == tb && a.Count == b.Count, Is.False, $"{results[i].Name} and {results[j].Name} are too alike");
                }
            // The strum presets are spread in time, the block presets are not.
            foreach (var (name, notes) in results)
            {
                bool strummed = notes.Where(n => n.On).GroupBy(n => n.T / 48000).Any(g => g.Select(x => x.T).Distinct().Count() > 1);
                Assert.That(strummed, Is.EqualTo(name.StartsWith("Strum") || name.Contains("Wide Pad")), name);
            }
        }

        // ---------------------------------------------------------------------------------------------------------- through a synth

        /// <summary>The level of a frequency in x[from .. from + n) by correlation with a sine and a cosine (a Hann window).</summary>
        static double ToneLevel(float[] x, double hz, int from, int n)
        {
            double re = 0, im = 0, wsum = 0;
            for (int i = 0; i < n; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
                double ph = 2 * Math.PI * hz * (from + i) / Measure.Rate;
                re += w * x[from + i] * Math.Cos(ph);
                im += w * x[from + i] * Math.Sin(ph);
                wsum += w;
            }
            return 2 * Math.Sqrt(re * re + im * im) / wsum;
        }

        static double Hz(int note) => 440 * Math.Pow(2, (note - 69) / 12.0);

        /// <summary>Neo Chord's output into Neo Poly (the way a note insert or a note wire delivers it: the events of a block applied at its start).</summary>
        internal static float[] RenderThroughPoly(ParamValues chord, IEnumerable<(long T, bool On, int Note, int Vel)> notes, long length, int block = 64, ParamValues poly = null)
        {
            var rig = new Rig(block, v => { for (int i = 0; i < Params.Table.Length; i++) v.Set(i, chord[i]); });
            foreach (var (t, on, note, vel) in notes) { if (on) rig.On(t, note, vel); else rig.Off(t, note); }
            poly ??= new ParamValues(NeoBuzz.Poly.Params.Table);
            var synth = new NeoBuzz.Poly.PolyEngine(poly);
            synth.SetSampleRate(Measure.Rate);
            var y = new float[length];
            var l = new float[256];
            var r = new float[256];
            for (long pos = 0; pos < length; pos += block)
            {
                int before = rig.Sent.Count;
                rig.Run(pos + 1);
                for (int i = before; i < rig.Sent.Count; i++)
                {
                    var ev = rig.Sent[i].Ev;
                    if (ev.Kind == ChordEvent.On) synth.NoteOn(1000 + ev.Data1, ev.Data1, ev.Data2 / 127f);
                    else if (ev.Kind == ChordEvent.Off) synth.NoteOff(1000 + ev.Data1);
                }
                int n = (int)Math.Min(block, length - pos);
                synth.Process(l, r, n);
                for (int i = 0; i < n; i++) y[pos + i] = 0.5f * (l[i] + r[i]);
            }
            return y;
        }

        [Test]
        public void ThePolyReceivesTheChord()
        {
            var v = new ParamValues(Params.Table);
            Pick(v, P.Chord, "Major");
            var y = RenderThroughPoly(v, new[] { (0L, true, 60, 100), (36000L, false, 60, 0) }, 48000);
            int from = 12000, n = 16384;
            double c = ToneLevel(y, Hz(60), from, n), e = ToneLevel(y, Hz(64), from, n), g = ToneLevel(y, Hz(67), from, n), other = ToneLevel(y, Hz(62), from, n);
            TestContext.Out.WriteLine($"C {c:F4}  E {e:F4}  G {g:F4}  D (not in the chord) {other:F4}");
            Assert.That(c, Is.GreaterThan(0.002));
            Assert.That(e, Is.GreaterThan(0.002), "the third arrives");
            Assert.That(g, Is.GreaterThan(0.002), "the fifth arrives");
            Assert.That(other, Is.LessThan(Math.Min(c, Math.Min(e, g)) * 0.25), "and a note that is not in the chord does not");

            // Off: only the note played.
            Pick(v, P.Chord, "Off");
            var solo = RenderThroughPoly(v, new[] { (0L, true, 60, 100), (36000L, false, 60, 0) }, 48000);
            Assert.That(ToneLevel(solo, Hz(64), from, n), Is.LessThan(ToneLevel(solo, Hz(60), from, n) * 0.25));
        }
    }
}
