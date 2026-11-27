#nullable disable
using NeoBuzz.Dsp;
using NeoBuzz.Notes;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Notes (docs/specs/neo-notes.md): the acceptance criteria, measured on the engine (the machine adds only the host's note queues).</summary>
    [TestFixture]
    public class NeoNotesTests
    {
        // ---------------------------------------------------------------------------------------------------------- helpers

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));
        static void Pick(ParamValues v, int p, string name)
        {
            int i = Array.IndexOf(Params.Table[p].Names, name);
            Assert.That(i, Is.GreaterThanOrEqualTo(0), $"{Params.Table[p].Name} has no choice {name}");
            v.Set(p, i);
        }
        static void On(ParamValues v, params int[] switches) { foreach (int s in switches) v.Set(s, 1); }

        static readonly int[] AllSwitches = { P.TransOn, P.ScaleOn, P.SpreadOn, P.VelOn, P.LenOn, P.EchoOn, P.HumOn };

        /// <summary>A note: its on and off sample times and the notes sent for it, run at a block size.</summary>
        static NotesRig RunNotes(int block, Action<ParamValues> setup, Action<NotesRig> feed, long until, double spb = 28800)
        {
            var rig = new NotesRig(block, setup, spb);
            feed(rig);
            rig.Run(until);
            return rig;
        }

        static List<(long T, int Kind, int Ch, int D1, int D2)> Plain(IEnumerable<(long T, NotesEvent Ev)> sent) =>
            sent.Select(s => (s.T, (int)s.Ev.Kind, (int)s.Ev.Channel, (int)s.Ev.Data1, s.Ev.Data2)).ToList();

        // ---------------------------------------------------------------------------------------------------------- all sections off

        /// <summary>Well-formed random input: notes on three channels (some of length 0), controllers, pitch bend and pressure, in time order.</summary>
        static List<(long T, NotesEvent Ev)> RandomInput(int seed, int notes, bool controllers = true)
        {
            var rnd = new Random(seed);
            var list = new List<(long T, NotesEvent Ev)>();
            var heldUntil = new Dictionary<int, long>();
            long t = 0;
            for (int i = 0; i < notes; i++)
            {
                t += rnd.Next(0, 3000);
                int ch = rnd.Next(3), note = rnd.Next(128), key = ch * 128 + note;
                if (heldUntil.TryGetValue(key, out long until) && until >= t) continue;      // never a second on for a held key
                long len = rnd.Next(0, 4) == 0 ? 0 : rnd.Next(1, 40000);
                heldUntil[key] = t + len;
                list.Add((t, NotesEvent.NoteOn(0, ch, note, rnd.Next(1, 128))));
                list.Add((t + len, NotesEvent.NoteOff(0, ch, note)));
                if (controllers && rnd.Next(5) == 0)
                {
                    int kind = 2 + rnd.Next(3);
                    list.Add((t + rnd.Next(0, 2000), new NotesEvent { Kind = (byte)kind, Channel = (byte)ch, Data1 = (byte)(kind == 2 ? 1 + rnd.Next(100) : 0), Data2 = rnd.Next(0, kind == 3 ? 16384 : 128) }));
                }
            }
            return list.Select((e, k) => (e, k)).OrderBy(x => x.e.T).ThenBy(x => x.k).Select(x => x.e).ToList();
        }

        [TestCase(1, false)]
        [TestCase(64, false)]
        [TestCase(512, false)]
        [TestCase(1, true)]
        [TestCase(64, true)]
        [TestCase(512, true)]
        public void AllSectionsOffIsEventIdenticalToTheInput(int block, bool neutral)
        {
            // neutral: every section ON but set to do nothing (0 st, Chromatic, no octaves, the identity curve, as played, no repeats, no humanize): the same.
            var input = RandomInput(12345, 4000);
            var rig = new NotesRig(block, v =>
            {
                if (!neutral) return;
                On(v, AllSwitches);
                Set(v, P.Trans, 0); Pick(v, P.Scale, "Chromatic"); Pick(v, P.ScaleMode, "Nearest"); Set(v, P.SpreadRange, 0);
                Set(v, P.VelCurve, 0); Set(v, P.OutMin, 1); Set(v, P.OutMax, 127); Set(v, P.VelRandom, 0);
                Pick(v, P.LenMode, "As played"); Set(v, P.Repeats, 0); Set(v, P.HumTime, 0); Set(v, P.HumVel, 0);
            });
            foreach (var (t, e) in input) rig.Raw(t, e);
            rig.Run(input.Last().T + 5000);
            var expected = input.Select(i => (i.T, (int)i.Ev.Kind, (int)i.Ev.Channel, (int)i.Ev.Data1, i.Ev.Data2)).ToList();
            Assert.That(Plain(rig.Sent), Is.EqualTo(expected), "the output is the input, event for event, on the same sample");
            Assert.That(rig.E.Dropped, Is.EqualTo(0));
            Assert.That(rig.E.ActiveSources, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------------------------------------------------- the fuzz

        /// <summary>
        /// Ten thousand random notes with every section on (pitch 0 to 127, two channels), every parameter automated at random between blocks and keys released at random:
        /// no pitch is started twice or ended twice, every velocity is 1 to 127, every pitch 0 to 127, a block holds at most the wire's 1024 events, the offsets are in order,
        /// and at the end nothing is left sounding: after the keys are released and the echoes have run out, and after a stop with keys still held.
        /// </summary>
        [TestCase(1, false, 1)]
        [TestCase(64, false, 2)]
        [TestCase(512, false, 3)]
        [TestCase(1, true, 4)]
        [TestCase(64, true, 5)]
        [TestCase(512, true, 6)]
        public void TenThousandRandomNotesWithEverySectionOnLeaveNothingHanging(int block, bool stopWhileHeld, int seed)
        {
            var rnd = new Random(seed * 104729);
            var rig = new NotesRig(block, v => On(v, AllSwitches));
            var e = rig.E;
            var held = new List<(int Ch, int Note)>();
            var sounding = new HashSet<int>();
            int bad = 0, badVel = 0, badPitch = 0, checkedUpTo = 0;
            void Check()
            {
                for (int i = checkedUpTo; i < rig.Sent.Count; i++)
                {
                    var ev = rig.Sent[i].Ev;
                    int k = ev.Channel * 128 + ev.Data1;
                    if (ev.Data1 > 127) badPitch++;
                    if (ev.Kind == NotesEvent.On)
                    {
                        if (ev.Data2 < 1 || ev.Data2 > 127) badVel++;
                        if (!sounding.Add(k)) bad++;
                    }
                    else if (ev.Kind == NotesEvent.Off) { if (!sounding.Remove(k)) bad++; }
                }
                checkedUpTo = rig.Sent.Count;
            }
            long t = 0;
            for (int n = 0; n < 10000; n++)
            {
                t += rnd.Next(0, 9600);                                  // up to 200 ms between events
                if (rnd.Next(3) == 0)
                {
                    // Automation: a parameter changes, before the next events reach the engine (at a block edge).
                    rig.Run(t);
                    for (int c = 0; c < 1 + rnd.Next(3); c++)
                    {
                        int p = rnd.Next(Params.Table.Length);
                        var d = Params.Table[p];
                        int value = rnd.Next(d.Min, d.Max + 1);
                        if (Array.IndexOf(AllSwitches, p) >= 0 && rnd.Next(5) != 0) value = d.Max;
                        rig.V.Set(p, value);
                    }
                }
                if (held.Count > 0 && rnd.Next(100) < 45)
                {
                    int i = rnd.Next(held.Count);
                    rig.Off(Math.Max(t, rig.Pos), held[i].Note, held[i].Ch);
                    held.RemoveAt(i);
                }
                else
                {
                    int ch = rnd.Next(4) == 0 ? 1 : 0, note = rnd.Next(128);
                    if (held.Contains((ch, note))) continue;
                    held.Add((ch, note));
                    rig.On(Math.Max(t, rig.Pos), note, rnd.Next(1, 128), ch);
                }
                if (rig.Pos < t - 200000) { rig.Run(t - 100000); Check(); }
            }
            rig.Run(t + 1);
            Check();
            if (stopWhileHeld)
            {
                // The transport stops with keys held, echoes running and notes waiting for a late start.
                e.Panic();
                rig.Run(rig.Pos + block);
                Check();
            }
            else
            {
                foreach (var (ch, note) in held) rig.Off(rig.Pos, note, ch);
                held.Clear();
                // Echoes of 16 repeats at 2 s each take 32 s; then a margin.
                long deadline = rig.Pos + 48000L * 90;
                while ((e.Pending > 0 || e.SoundingPitches > 0) && rig.Pos < deadline) { rig.Run(rig.Pos + 48000); Check(); }
            }
            rig.Run(rig.Pos + 4 * block);
            Check();
            Assert.That(bad, Is.EqualTo(0), "a pitch was started twice or ended twice");
            Assert.That(badVel, Is.EqualTo(0), "every velocity is 1 to 127");
            Assert.That(badPitch, Is.EqualTo(0), "every pitch is 0 to 127");
            Assert.That(rig.OrderViolations, Is.EqualTo(0), "the wire's rule: offsets never go backwards in a block");
            Assert.That(rig.MaxPerBlock, Is.LessThanOrEqualTo(NotesEngine.WireLimit));
            Assert.That(sounding, Is.Empty, "a note is still sounding");
            Assert.That(e.SoundingPitches, Is.EqualTo(0));
            Assert.That(e.SentOn, Is.EqualTo(e.SentOff));
            Assert.That(rig.Sent.Count(s => s.Ev.Kind == NotesEvent.On), Is.EqualTo(rig.Sent.Count(s => s.Ev.Kind == NotesEvent.Off)));
            Assert.That(e.Pending, Is.EqualTo(0), "nothing waits on the timeline");
            Assert.That(e.ActiveSources, Is.EqualTo(0), "every source is over");
            TestContext.Out.WriteLine($"block {block}, {(stopWhileHeld ? "stop while held" : "released")}: {e.SentOn} notes sent and ended, {e.Dropped} refused, peak timeline {e.PeakPending}, most in a block {rig.MaxPerBlock}");
            Assert.That(e.SentOn, Is.GreaterThan(3000), "the fuzz did play notes");
        }

        // ---------------------------------------------------------------------------------------------------------- scale

        /// <summary>The scales as step patterns (whole and half steps), written from theory and not from Neo Chord's table: Chromatic, then the nine scales in the Scale parameter's order.</summary>
        static int[] FromSteps(params int[] steps)
        {
            var s = new List<int> { 0 };
            for (int i = 0; i < steps.Length - 1; i++) s.Add(s[^1] + steps[i]);
            return s.ToArray();
        }

        static int[] Rotated(int[] steps, int by) => steps.Skip(by).Concat(steps.Take(by)).ToArray();

        static readonly int[] MajorSteps = { 2, 2, 1, 2, 2, 2, 1 };

        static readonly int[][] ScaleNotes =
        {
            Enumerable.Range(0, 12).ToArray(),                      // Chromatic
            FromSteps(MajorSteps),                                  // Major
            FromSteps(Rotated(MajorSteps, 5)),                      // Natural minor (Aeolian)
            FromSteps(2, 1, 2, 2, 1, 3, 1),                         // Harmonic minor
            FromSteps(2, 1, 2, 2, 2, 2, 1),                         // Melodic minor (rising)
            FromSteps(Rotated(MajorSteps, 1)),                      // Dorian
            FromSteps(Rotated(MajorSteps, 2)),                      // Phrygian
            FromSteps(Rotated(MajorSteps, 3)),                      // Lydian
            FromSteps(Rotated(MajorSteps, 4)),                      // Mixolydian
            FromSteps(Rotated(MajorSteps, 6)),                      // Locrian
        };

        static bool InScale(int pitch, int key, int scale) => ScaleNotes[scale].Contains(((pitch - key) % 12 + 12) % 12);

        /// <summary>What each mode should do, by brute force: -1 for a dropped note.</summary>
        static int Expected(int pitch, int key, int scale, int mode)
        {
            if (InScale(pitch, key, scale)) return pitch;
            switch (mode)
            {
                case 1: for (int q = pitch + 1; q <= 127; q++) if (InScale(q, key, scale)) return q; return -1;
                case 2: for (int q = pitch - 1; q >= 0; q--) if (InScale(q, key, scale)) return q; return -1;
                case 0:
                    for (int d = 1; d < 128; d++)
                    {
                        if (pitch - d >= 0 && InScale(pitch - d, key, scale)) return pitch - d;       // a tie goes down
                        if (pitch + d <= 127 && InScale(pitch + d, key, scale)) return pitch + d;
                    }
                    return -1;
                default: return -1;
            }
        }

        [Test]
        public void TheScaleTableIsNeoChordsAndMatchesTheTheory()
        {
            for (int s = 1; s <= 9; s++)
                Assert.That(NeoBuzz.Chord.ChordTheory.Scales[s - 1], Is.EqualTo(ScaleNotes[s]), $"scale {Params.Table[P.Scale].Names[s]}");
            Assert.That(Params.Table[P.Scale].Names.Length, Is.EqualTo(10));
        }

        [Test]
        public void EveryKeyAndScaleMovesEveryNoteIntoTheScaleAndFilterKeepsOnlyScaleNotes()
        {
            int checkedNotes = 0;
            for (int key = 0; key < 12; key++)
                for (int scale = 0; scale < 10; scale++)
                    for (int mode = 0; mode < 4; mode++)
                    {
                        var rig = new NotesRig(64, v =>
                        {
                            On(v, P.ScaleOn);
                            v.Set(P.Key, key); v.Set(P.Scale, scale); v.Set(P.ScaleMode, mode);
                        });
                        for (int p = 0; p < 128; p++) rig.Note(p * 2000L, 1000, p, 100);
                        rig.Run(128 * 2000L + 2000);
                        var ons = rig.Sent.Where(s => s.Ev.Kind == NotesEvent.On).ToList();
                        var wanted = Enumerable.Range(0, 128).Select(p => (Pitch: p, Out: Expected(p, key, scale, mode))).ToList();
                        // Each note on lands on its own 2000-sample slot, so the slot says which note it was.
                        foreach (var (p, outp) in wanted)
                        {
                            var hit = ons.Where(o => o.T / 2000 == p).ToList();
                            if (outp < 0) { Assert.That(hit, Is.Empty, $"key {key} scale {scale} mode {mode}: {p} is dropped"); continue; }
                            Assert.That(hit.Count, Is.EqualTo(1), $"key {key} scale {scale} mode {mode}: {p}");
                            Assert.That((int)hit[0].Ev.Data1, Is.EqualTo(outp), $"key {key} scale {scale} mode {mode}: {p} -> {outp}");
                            Assert.That(InScale(hit[0].Ev.Data1, key, scale), Is.True, $"key {key} scale {scale} mode {mode}: {p} came out out of the scale");
                            if (mode == 3) Assert.That(InScale(p, key, scale), Is.True, "Filter passes only notes that were in the scale");
                            checkedNotes++;
                        }
                        Assert.That(rig.Sent.Count(s => s.Ev.Kind == NotesEvent.Off), Is.EqualTo(ons.Count), "every note on has its note off");
                    }
            TestContext.Out.WriteLine($"{checkedNotes} notes checked in 12 keys, 10 scales, 4 modes");
            Assert.That(checkedNotes, Is.GreaterThan(30000));
        }

        [Test]
        public void ScaleModesDoWhatTheirNamesSay()
        {
            // C major: C# is a semitone from C and from D: Nearest goes down (tie), Up goes to D, Down to C. F# is between F and G.
            Assert.That(NotesEngine.Snap(61, 0, 1, 0), Is.EqualTo(60));
            Assert.That(NotesEngine.Snap(61, 0, 1, 1), Is.EqualTo(62));
            Assert.That(NotesEngine.Snap(61, 0, 1, 2), Is.EqualTo(60));
            Assert.That(NotesEngine.Snap(61, 0, 1, 3), Is.EqualTo(-1));
            Assert.That(NotesEngine.Snap(60, 0, 1, 3), Is.EqualTo(60), "a scale note passes Filter");
            // Harmonic minor in A has a gap of three semitones (F to G#): the note between them takes the nearest.
            Assert.That(NotesEngine.Snap(67, 9, 3, 0), Is.EqualTo(68), "G in A harmonic minor: G# is nearer than F");
            // The ends of the keyboard: a note with no scale note above it is dropped by Up, and Nearest never leaves the keyboard.
            Assert.That(NotesEngine.Snap(127, 1, 1, 1), Is.EqualTo(-1), "G is not in C# major and nothing above it is on the keyboard: Up drops it");
            Assert.That(NotesEngine.Snap(127, 1, 1, 0), Is.EqualTo(126), "Nearest never leaves the keyboard");
            Assert.That(NotesEngine.Snap(0, 2, 1, 2), Is.EqualTo(-1), "and Down drops a note with nothing below it");
            Assert.That(NotesEngine.Snap(61, 0, 0, 3), Is.EqualTo(61), "Chromatic: every note is in the scale");
        }

        // ---------------------------------------------------------------------------------------------------------- transpose and range

        [Test]
        public void TransposeMovesNotesAndDropsAnythingOutsideTheKeyboardWithItsNoteOff()
        {
            var rig = RunNotes(64, v => { On(v, P.TransOn); Set(v, P.Trans, 40); }, r =>
            {
                r.Note(0, 5000, 90);      // 130: out
                r.Note(10000, 5000, 80);  // 120: in
                r.Note(20000, 5000, 87);  // 127: in
                r.Note(30000, 5000, 88);  // 128: out
            }, 50000);
            Assert.That(rig.Ons.Select(o => o.Note).ToArray(), Is.EqualTo(new[] { 120, 127 }));
            Assert.That(rig.Offs.Select(o => o.Note).ToArray(), Is.EqualTo(new[] { 120, 127 }), "a dropped note has no note off either");
            Assert.That(rig.E.ActiveSources, Is.EqualTo(0));

            var down = RunNotes(64, v => { On(v, P.TransOn); Set(v, P.Trans, -48); }, r => { r.Note(0, 5000, 47); r.Note(10000, 5000, 48); }, 30000);
            Assert.That(down.Ons.Select(o => o.Note).ToArray(), Is.EqualTo(new[] { 0 }));
        }

        // ---------------------------------------------------------------------------------------------------------- spread

        static int[] SpreadSequence(string pattern, int range, int notes, int seed = 0)
        {
            var rig = RunNotes(64, v => { On(v, P.SpreadOn); Pick(v, P.SpreadPat, pattern); Set(v, P.SpreadRange, range); Set(v, P.Seed, seed); }, r =>
            {
                for (int i = 0; i < notes; i++) r.Note(i * 4000L, 1000, 60);
            }, notes * 4000L + 100);
            return rig.Ons.Select(o => (o.Note - 60) / 12).ToArray();
        }

        [Test]
        public void SpreadPatternsMoveNotesByWholeOctavesInOrder()
        {
            Assert.That(SpreadSequence("Up cycle", 2, 7), Is.EqualTo(new[] { 0, 1, 2, 0, 1, 2, 0 }));
            Assert.That(SpreadSequence("Down cycle", 3, 9), Is.EqualTo(new[] { 0, -1, -2, -3, 0, -1, -2, -3, 0 }));
            Assert.That(SpreadSequence("Up-down", 2, 9), Is.EqualTo(new[] { 0, 1, 2, 1, 0, 1, 2, 1, 0 }));
            Assert.That(SpreadSequence("Up-down", 1, 5), Is.EqualTo(new[] { 0, 1, 0, 1, 0 }));
            Assert.That(SpreadSequence("Up cycle", 0, 4), Is.EqualTo(new[] { 0, 0, 0, 0 }), "Range 0 moves nothing");
            var random = SpreadSequence("Random", 3, 300, 5);
            Assert.That(random.All(s => s >= -3 && s <= 3), Is.True);
            Assert.That(random.Distinct().Count(), Is.EqualTo(7), "Random uses every octave from -3 to +3");
            Assert.That(SpreadSequence("Random", 3, 300, 5), Is.EqualTo(random), "the same seed gives the same shifts");
            Assert.That(SpreadSequence("Random", 3, 300, 6), Is.Not.EqualTo(random), "another seed gives others");
        }

        [Test]
        public void SpreadStartsAgainAfterAStop()
        {
            var rig = new NotesRig(64, v => { On(v, P.SpreadOn); Pick(v, P.SpreadPat, "Up cycle"); Set(v, P.SpreadRange, 3); });
            rig.Note(0, 1000, 60); rig.Note(4000, 1000, 60);
            rig.Run(10000);
            rig.E.Panic();
            rig.Note(rig.Pos + 128, 1000, 60);
            rig.Run(rig.Pos + 4000);
            Assert.That(rig.Ons.Select(o => (o.Note - 60) / 12).ToArray(), Is.EqualTo(new[] { 0, 1, 0 }));
        }

        // ---------------------------------------------------------------------------------------------------------- velocity

        static int[] VelocitiesOf(Action<ParamValues> setup, params int[] velocities)
        {
            var rig = RunNotes(64, setup, r => { for (int i = 0; i < velocities.Length; i++) r.Note(i * 3000L, 1000, 60 + i % 12, velocities[i]); }, velocities.Length * 3000L + 100);
            return rig.Ons.Select(o => o.Vel).ToArray();
        }

        [Test]
        public void VelocityCurveRangeFixedAndRandom()
        {
            var all = Enumerable.Range(1, 127).ToArray();
            Assert.That(VelocitiesOf(v => On(v, P.VelOn), all), Is.EqualTo(all), "neutral settings leave every velocity as played");
            // The curve: monotone, the ends stay, plus lifts the middle and minus lowers it.
            foreach (double curve in new[] { -1.0, -0.6, -0.2, 0, 0.2, 0.6, 1.0 })
            {
                var o = VelocitiesOf(v => { On(v, P.VelOn); Set(v, P.VelCurve, curve); }, all);
                Assert.That(o[0], Is.EqualTo(1), $"curve {curve}: 1 stays 1");
                Assert.That(o[126], Is.EqualTo(127), $"curve {curve}: 127 stays 127");
                for (int i = 1; i < o.Length; i++) Assert.That(o[i], Is.GreaterThanOrEqualTo(o[i - 1]), $"curve {curve} is monotone");
            }
            Assert.That(VelocitiesOf(v => { On(v, P.VelOn); Set(v, P.VelCurve, 1.0); }, 64), Is.EqualTo(new[] { 96 }), "hard: the middle velocity rises (64 -> 96)");
            Assert.That(VelocitiesOf(v => { On(v, P.VelOn); Set(v, P.VelCurve, -1.0); }, 64), Is.EqualTo(new[] { 32 }), "soft: it falls (64 -> 32)");
            // The range maps the whole velocity span onto Out min … Out max.
            var ranged = VelocitiesOf(v => { On(v, P.VelOn); Set(v, P.OutMin, 40); Set(v, P.OutMax, 80); }, 1, 64, 127);
            Assert.That(ranged, Is.EqualTo(new[] { 40, 60, 80 }));
            Assert.That(VelocitiesOf(v => { On(v, P.VelOn); Set(v, P.OutMin, 90); Set(v, P.OutMax, 30); }, 1, 127), Is.EqualTo(new[] { 90, 90 }), "Out max is never below Out min");
            // Fixed: every note the same (the curve and range are skipped).
            Assert.That(VelocitiesOf(v => { On(v, P.VelOn, P.VelFixedOn); Set(v, P.VelFixed, 33); Set(v, P.VelCurve, 1.0); Set(v, P.OutMin, 90); }, 1, 50, 127), Is.EqualTo(new[] { 33, 33, 33 }));
            // Random: within the share, varied, seeded.
            var rnd = VelocitiesOf(v => { On(v, P.VelOn); Set(v, P.VelRandom, 20); Set(v, P.Seed, 4); }, Enumerable.Repeat(100, 400).ToArray());
            Assert.That(rnd.All(x => x >= 80 && x <= 120), Is.True, "±20 % of 100");
            Assert.That(rnd.Distinct().Count(), Is.GreaterThan(20));
            Assert.That(VelocitiesOf(v => { On(v, P.VelOn); Set(v, P.VelRandom, 20); Set(v, P.Seed, 4); }, Enumerable.Repeat(100, 400).ToArray()), Is.EqualTo(rnd));
            Assert.That(VelocitiesOf(v => { On(v, P.VelOn); Set(v, P.VelRandom, 50); }, Enumerable.Repeat(127, 200).ToArray()).All(x => x >= 1 && x <= 127), Is.True);
        }

        // ---------------------------------------------------------------------------------------------------------- length

        [TestCase(1)]
        [TestCase(64)]
        [TestCase(512)]
        public void FixedLengthEndsOnTheComputedSampleWhateverTheSourceDoes(int block)
        {
            // 1/16 triplet at 100 beats a minute and 48 kHz: 28800 / 6 = 4800 samples.
            foreach (long held in new long[] { 100, 4800, 4801, 100000 })
            {
                var rig = RunNotes(block, v => { On(v, P.LenOn); Pick(v, P.LenMode, "Fixed"); Pick(v, P.LenRate, "1/16"); Pick(v, P.LenFeel, "Triplet"); }, r => r.Note(777, held, 60), 120000);
                Assert.That(rig.Ons.ToArray(), Is.EqualTo(new[] { (777L, 60, 100) }), $"held {held}");
                Assert.That(rig.Offs.ToArray(), Is.EqualTo(new[] { (777L + 4800, 60) }), $"held {held}: the note off is a fixed time after the note on");
            }
            // Free time: 123 ms is 5904 samples.
            var ms = RunNotes(block, v => { On(v, P.LenOn); Pick(v, P.LenMode, "Fixed"); Pick(v, P.LenSync, "Free time"); Set(v, P.LenMs, 123); }, r => r.Note(30001, 20, 60), 60000);
            Assert.That(ms.Offs.ToArray(), Is.EqualTo(new[] { (30001L + 5904, 60) }));
            // Dotted and straight.
            var dot = RunNotes(block, v => { On(v, P.LenOn); Pick(v, P.LenMode, "Fixed"); Pick(v, P.LenRate, "1/8"); Pick(v, P.LenFeel, "Dotted"); }, r => r.Note(5, 10, 60), 60000, 33333);
            Assert.That(dot.Offs.ToArray(), Is.EqualTo(new[] { (5L + (long)Math.Round(0.75 * 33333), 60) }), "the song's tempo comes from the host");
        }

        [TestCase(1)]
        [TestCase(64)]
        [TestCase(512)]
        public void ClampHoldsShortNotesToTheMinimumAndEndsLongOnesAtTheMaximum(int block)
        {
            // Min 100 ms = 4800 samples, max 500 ms = 24000 samples. Every end is causal: never before the source's own, and never earlier than the minimum.
            void Setup(ParamValues v) { On(v, P.LenOn); Pick(v, P.LenMode, "Clamp"); Set(v, P.ClampMin, 100); Set(v, P.ClampMax, 500); }
            foreach (var (held, expectedLen) in new[] { (1000L, 4800L), (4800L, 4800L), (10000L, 10000L), (24000L, 24000L), (24001L, 24000L), (100000L, 24000L) })
            {
                var rig = RunNotes(block, Setup, r => r.Note(2000, held, 62), 150000);
                Assert.That(rig.Offs.ToArray(), Is.EqualTo(new[] { (2000L + expectedLen, 62) }), $"held {held}");
                Assert.That(rig.Ons.Count(), Is.EqualTo(1));
            }
            // A note held past the maximum ends at the maximum and the later source note off adds nothing.
            var late = RunNotes(block, Setup, r => r.Note(0, 90000, 62), 150000);
            Assert.That(late.Sent.Count, Is.EqualTo(2));
            // As played: the note off follows the source's.
            var asPlayed = RunNotes(block, v => On(v, P.LenOn), r => r.Note(2000, 777, 62), 20000);
            Assert.That(asPlayed.Offs.ToArray(), Is.EqualTo(new[] { (2777L, 62) }));
        }

        // ---------------------------------------------------------------------------------------------------------- echo

        [TestCase(1)]
        [TestCase(64)]
        [TestCase(512)]
        public void EchoRepeatsLandOnTheComputedSamples(int block)
        {
            // A dotted eighth at 100 beats a minute: 0.75 beat = 21600 samples. A note of 5000 samples, repeats half as long as it, each 25 % quieter.
            var rig = RunNotes(block, v =>
            {
                On(v, P.EchoOn); Set(v, P.Repeats, 4); Pick(v, P.EchoSync, "Song tempo"); Pick(v, P.EchoRate, "1/8"); Pick(v, P.EchoFeel, "Dotted");
                Set(v, P.EchoDrop, 25); Set(v, P.EchoStep, 0); Set(v, P.EchoLen, 50);
            }, r => r.Note(1000, 5000, 60, 100), 150000);
            var ons = rig.Ons.ToArray();
            var offs = rig.Offs.ToArray();
            var vel = new[] { 100, 75, 56, 42, 32 };
            Assert.That(ons, Is.EqualTo(Enumerable.Range(0, 5).Select(k => (1000L + k * 21600L, 60, vel[k])).ToArray()), "the repeats and their velocities (each 75 % of the one before)");
            Assert.That(offs, Is.EqualTo(new[] { (6000L, 60) }.Concat(Enumerable.Range(1, 4).Select(k => (1000L + k * 21600L + 2500, 60))).ToArray()), "each repeat is half as long as the note");
            Assert.That(rig.E.ActiveSources, Is.EqualTo(0));
        }

        [TestCase(1)]
        [TestCase(64)]
        [TestCase(512)]
        public void EchoInMillisecondsWithAPitchStepAndAHeldNote(int block)
        {
            // 137 ms is 6576 samples. The note is held 20000 samples, so repeats 1 and 2 start while it is still held: they last half the time between repeats (3288).
            // Pitch step +2: 60, 62, 64, 66.
            var rig = RunNotes(block, v =>
            {
                On(v, P.EchoOn); Set(v, P.Repeats, 3); Pick(v, P.EchoSync, "Free time"); Set(v, P.EchoMs, 137); Set(v, P.EchoDrop, 0); Set(v, P.EchoStep, 2); Set(v, P.EchoLen, 50);
            }, r => r.Note(500, 20000, 60, 90), 100000);
            Assert.That(rig.Ons.ToArray(), Is.EqualTo(new[] { (500L, 60, 90), (500L + 6576, 62, 90), (500L + 2 * 6576, 64, 90), (500L + 3 * 6576, 66, 90) }));
            Assert.That(rig.Offs.OrderBy(o => o.T).ToArray(), Is.EqualTo(new[] { (500L + 6576 + 3288, 62), (500L + 2 * 6576 + 3288, 64), (500L + 20000, 60), (500L + 3 * 6576 + 3288, 66) }.OrderBy(o => o.Item1).ToArray()));
        }

        [Test]
        public void EchoStopsWhereItWouldLeaveTheKeyboardOrFallSilent()
        {
            // Step +12 from 100: 112, 124, then 136 is off the keyboard, so the chain ends.
            var up = RunNotes(64, v => { On(v, P.EchoOn); Set(v, P.Repeats, 8); Set(v, P.EchoStep, 12); Set(v, P.EchoDrop, 0); }, r => r.Note(0, 1000, 100), 400000);
            Assert.That(up.Ons.Select(o => o.Note).ToArray(), Is.EqualTo(new[] { 100, 112, 124 }));
            Assert.That(up.Offs.Count(), Is.EqualTo(3));
            // A velocity drop of 100 % leaves nothing to repeat; a drop of 60 % keeps 40 % of the velocity each time: 100, 40, 16, 6.4, 2.6, 1.02, then 0.4 (which would round to 0).
            var gone = RunNotes(64, v => { On(v, P.EchoOn); Set(v, P.Repeats, 8); Set(v, P.EchoDrop, 100); }, r => r.Note(0, 1000, 60), 400000);
            Assert.That(gone.Ons.Count(), Is.EqualTo(1));
            var fade = RunNotes(64, v => { On(v, P.EchoOn); Set(v, P.Repeats, 12); Set(v, P.EchoDrop, 60); Set(v, P.EchoStep, 0); Set(v, P.EchoMs, 200); Pick(v, P.EchoSync, "Free time"); }, r => r.Note(0, 1000, 60, 100), 400000);
            Assert.That(fade.Ons.Select(o => o.Vel).ToArray(), Is.EqualTo(new[] { 100, 40, 16, 6, 3, 1 }), "each repeat keeps 40 % of the one before; the echo ends before a velocity would round to 0");
        }

        [Test]
        public void ARepeatOverItsOwnNoteStrikesThePitchAgainAndNothingSticks()
        {
            // The note is held (long Fixed length) when its repeats of the same pitch start. Each repeat is struck again (a note off and a note on on its sample);
            // the pitch is counted, so the one note off comes when the last owner has ended.
            var rig = RunNotes(64, v =>
            {
                On(v, P.EchoOn, P.LenOn); Pick(v, P.LenMode, "Fixed"); Pick(v, P.LenSync, "Free time"); Set(v, P.LenMs, 1000);
                Set(v, P.Repeats, 2); Pick(v, P.EchoSync, "Free time"); Set(v, P.EchoMs, 400); Set(v, P.EchoStep, 0); Set(v, P.EchoLen, 100);
            }, r => r.Note(0, 500, 60, 100), 200000);
            var s = rig.Sent.Select(x => (x.T, (int)x.Ev.Kind, (int)x.Ev.Data1)).ToArray();
            // The note ends at 48000, repeat 1 (19200 to 38400) and repeat 2 (38400 to 57600) are struck anew on their samples; the last owner ends at 57600.
            Assert.That(s, Is.EqualTo(new[] { (0L, 0, 60), (19200L, 1, 60), (19200L, 0, 60), (38400L, 1, 60), (38400L, 0, 60), (57600L, 1, 60) }));
            Assert.That(rig.E.SoundingPitches, Is.EqualTo(0));
        }

        [Test]
        public void ALateHumanizedNoteOnOverAnEarlyNoteOffIsStillHeard()
        {
            // The phrase's G4 ends and starts again on the same sample; humanize delays the first note off more than the second note on: the second is struck anyway.
            int struck = 0;
            for (int seed = 0; seed < 40; seed++)
            {
                var rig = RunNotes(64, v => { On(v, P.HumOn); Set(v, P.HumTime, 50); Set(v, P.Seed, seed); }, r => { r.Note(0, 10000, 67); r.Note(10000, 10000, 67); }, 60000);
                Assert.That(rig.Ons.Count(), Is.GreaterThanOrEqualTo(2), $"seed {seed}: two notes, two note ons (more is a strike that ended the first)");
                Assert.That(rig.E.SoundingPitches, Is.EqualTo(0));
                struck += rig.Ons.Count() - 2;
            }
            Assert.That(struck, Is.EqualTo(0), "a note on is never swallowed");
        }

        // ---------------------------------------------------------------------------------------------------------- humanize

        /// <summary>The engine's random stream, as written in the spec: xorshift32 from the seed (docs/specs/neo-notes.md §4).</summary>
        sealed class Mirror
        {
            uint x;
            public Mirror(int seed)
            {
                x = 0x9E3779B9u * (uint)(seed + 1) + 0x7F4A7C15u;
                if (x == 0) x = 1;
                for (int i = 0; i < 4; i++) Next();
            }
            public uint Next() { x ^= x << 13; x ^= x >> 17; x ^= x << 5; return x; }
        }

        [TestCase(1)]
        [TestCase(64)]
        [TestCase(512)]
        public void HumanizeDelaysEachNoteByTheComputedRandomTimeAndKeepsItsLength(int block)
        {
            const int Seed = 5;
            var pv = new ParamValues(Params.Table);
            Set(pv, P.HumTime, 37);
            double maxSamples = pv.Value(P.HumTime) * 0.001 * 48000;
            var mirror = new Mirror(Seed);
            var notes = new List<(long T, long Len, int Note)>();
            for (int i = 0; i < 40; i++) notes.Add((1000 + i * 7919L, 500 + i * 131, 40 + i));
            var rig = RunNotes(block, v => { On(v, P.HumOn); Set(v, P.HumTime, 37); Set(v, P.Seed, Seed); }, r => { foreach (var (t, len, n) in notes) r.Note(t, len, n); }, 400000);
            var ons = rig.Ons.ToDictionary(o => o.Note, o => o.T);
            var offs = rig.Offs.ToDictionary(o => o.Note, o => o.T);
            foreach (var (t, len, n) in notes)
            {
                mirror.Next(); mirror.Next();
                uint r3 = mirror.Next();
                mirror.Next();
                long delay = (long)(r3 * (1.0 / 4294967296.0) * maxSamples);
                Assert.That(ons[n], Is.EqualTo(t + delay), $"note {n}: on");
                Assert.That(offs[n], Is.EqualTo(t + len + delay), $"note {n}: the note off is as late as the note on, so the length is kept");
                Assert.That(delay, Is.InRange(0, (long)maxSamples), "only late, never early");
            }
            Assert.That(notes.Select(n => ons[n.Note] - n.T).Distinct().Count(), Is.GreaterThan(30), "the delays differ");
        }

        [Test]
        public void HumanizeVelocityIsBoundedAndSeeded()
        {
            var a = VelocitiesOf(v => { On(v, P.HumOn); Set(v, P.HumVel, 30); Set(v, P.Seed, 9); }, Enumerable.Repeat(100, 300).ToArray());
            Assert.That(a.All(x => x >= 70 && x <= 130 - 3), Is.True, "±30 % of 100 (127 at most)");
            Assert.That(a.Distinct().Count(), Is.GreaterThan(15));
            Assert.That(VelocitiesOf(v => { On(v, P.HumOn); Set(v, P.HumVel, 30); Set(v, P.Seed, 9); }, Enumerable.Repeat(100, 300).ToArray()), Is.EqualTo(a));
        }

        // ---------------------------------------------------------------------------------------------------------- determinism

        static List<(long, int, int, int, int)> Everything(int block, int seed)
        {
            var rig = new NotesRig(block, v =>
            {
                On(v, AllSwitches);
                Set(v, P.Trans, 3); Pick(v, P.Scale, "Dorian"); Pick(v, P.SpreadPat, "Random"); Set(v, P.SpreadRange, 2);
                Set(v, P.VelRandom, 25); Set(v, P.HumTime, 30); Set(v, P.HumVel, 20); Set(v, P.Seed, seed);
                Set(v, P.Repeats, 3); Pick(v, P.EchoSync, "Song tempo"); Set(v, P.EchoStep, 5);
            });
            NotesPhrase.Feed(rig);
            rig.Run(400000);
            return Plain(rig.Sent);
        }

        [Test]
        public void TheSameSeedGivesTheSameEventsAndAnotherSeedGivesOthers()
        {
            var a = Everything(64, 7);
            Assert.That(a.Count, Is.GreaterThan(100));
            Assert.That(Everything(64, 7), Is.EqualTo(a));
            Assert.That(Everything(1, 7), Is.EqualTo(a), "block size changes nothing");
            Assert.That(Everything(512, 7), Is.EqualTo(a));
            Assert.That(Everything(64, 8), Is.Not.EqualTo(a));
        }

        // ---------------------------------------------------------------------------------------------------------- note-offs and stuck notes

        [Test]
        public void ChangingParametersWhileHeldNeverStrandsANote()
        {
            var rig = new NotesRig(64, v => { On(v, P.TransOn); Set(v, P.Trans, 5); });
            rig.On(0, 60);
            rig.Run(1000);
            // Transpose changes, then is switched off, then the key is released: the note off goes to the pitch that was sent.
            rig.V.Set(P.Trans, Params.Table[P.Trans].Raw(7));
            rig.Run(2000);
            rig.V.Set(P.TransOn, 0);
            rig.V.Set(P.Scale, 3);
            rig.V.Set(P.LenOn, 1);
            rig.V.Set(P.LenMode, 1);
            rig.Off(2500, 60);
            rig.Run(10000);
            Assert.That(rig.Ons.Select(o => o.Note).ToArray(), Is.EqualTo(new[] { 65 }));
            Assert.That(rig.Offs.ToArray(), Is.EqualTo(new[] { (2500L, 65) }), "the note off goes to the pitch actually sent");
            Assert.That(rig.E.SoundingPitches, Is.EqualTo(0));
        }

        [Test]
        public void ANoteOnForAHeldKeyEndsTheOldNoteFirstAndChannelsAreCountedApart()
        {
            var rig = new NotesRig(64, v => On(v, P.TransOn));
            rig.On(0, 60, 100, 0);
            rig.On(100, 60, 90, 0);        // the same key again
            rig.On(200, 60, 80, 1);        // the same key on another channel is another note
            rig.Off(3000, 60, 0);
            rig.Off(3100, 60, 1);
            rig.Run(6000);
            var s = rig.Sent.Select(x => (x.T, (int)x.Ev.Kind, (int)x.Ev.Channel, (int)x.Ev.Data1)).ToArray();
            Assert.That(s, Is.EqualTo(new[] { (0L, 0, 0, 60), (100L, 1, 0, 60), (100L, 0, 0, 60), (200L, 0, 1, 60), (3000L, 1, 0, 60), (3100L, 1, 1, 60) }));
        }

        [Test]
        public void AStopCancelsEveryEchoAndNoteStillToCome()
        {
            var rig = new NotesRig(64, v => { On(v, P.EchoOn, P.HumOn); Set(v, P.Repeats, 8); Pick(v, P.EchoSync, "Free time"); Set(v, P.EchoMs, 500); Set(v, P.HumTime, 50); });
            rig.Note(0, 20000, 60);
            rig.Note(30000, 20000, 64);
            rig.On(45000, 67);
            rig.Run(48000);
            Assert.That(rig.E.SoundingPitches, Is.GreaterThan(0));
            rig.E.Panic();
            rig.Run(rig.Pos + 64);
            long stopAt = rig.Pos;
            Assert.That(rig.E.SoundingPitches, Is.EqualTo(0), "everything was released");
            Assert.That(rig.E.Pending, Is.EqualTo(0), "the echoes still to come are gone");
            rig.Run(rig.Pos + 500000);
            Assert.That(rig.Sent.Count(s => s.T > stopAt), Is.EqualTo(0), "nothing is sent after the stop");
            Assert.That(rig.E.ActiveSources, Is.EqualTo(0));
            Assert.That(rig.Sent.Count(s => s.Ev.Kind == NotesEvent.On), Is.EqualTo(rig.Sent.Count(s => s.Ev.Kind == NotesEvent.Off)));
        }

        [Test]
        public void AllNotesOffEndsEverythingOnItsSampleBeforeTheControllerPassesOn()
        {
            var rig = new NotesRig(64, v => { On(v, P.EchoOn); Set(v, P.Repeats, 4); Pick(v, P.EchoSync, "Free time"); Set(v, P.EchoMs, 300); });
            rig.On(0, 60);
            rig.On(100, 64);
            rig.Raw(5000, new NotesEvent { Kind = 2, Channel = 0, Data1 = 123, Data2 = 0 });
            rig.Run(300000);
            var after = rig.Sent.Where(s => s.T >= 5000).Select(s => (s.T, (int)s.Ev.Kind, (int)s.Ev.Data1)).ToArray();
            Assert.That(after, Is.EqualTo(new[] { (5000L, 1, 60), (5000L, 1, 64), (5000L, 2, 123) }), "the offs, then the controller, and nothing later");
        }

        [Test]
        public void ANoteOffAlwaysFindsRoomEvenWhenNoteOnsAreRefused()
        {
            var e = new NotesEngine(new ParamValues(Params.Table));
            int accepted = 0;
            for (int i = 0; i < NotesEngine.InputCapacity + 100; i++) { e.Input(NotesEvent.NoteOn(0, 0, i % 128, 100)); accepted++; }
            Assert.That(e.Dropped, Is.GreaterThan(0), "a flood of note ons is refused");
            long refused = e.Dropped;
            for (int i = 0; i < NotesEngine.InputReserve; i++) e.Input(NotesEvent.NoteOff(0, 0, i % 128));
            Assert.That(e.Dropped, Is.EqualTo(refused), "the note offs all found room");
        }

        [Test]
        public void ABlockWithMoreThanTheWireHoldsPassesTheRestOnInsteadOfDroppingIt()
        {
            // 2048 notes on one sample (16 channels, 128 pitches, none the same key): the wire takes 1024 events a block, so the rest goes out in the next block, in order.
            var rig = new NotesRig(64, v => { });
            var order = new List<(int Ch, int Note)>();
            for (int i = 0; i < 2048; i++) { int ch = i / 128, note = i % 128; order.Add((ch, note)); rig.On(0, note, 100, ch); }
            for (int i = 0; i < 2048; i++) rig.Off(1000, i % 128, i / 128);
            rig.Run(4000);
            Assert.That(rig.E.Dropped, Is.EqualTo(0));
            Assert.That(rig.MaxPerBlock, Is.LessThanOrEqualTo(NotesEngine.WireLimit));
            Assert.That(rig.OrderViolations, Is.EqualTo(0));
            var ons = rig.Sent.Where(s => s.Ev.Kind == NotesEvent.On).Select(s => (Ch: (int)s.Ev.Channel, Note: (int)s.Ev.Data1)).ToList();
            Assert.That(ons, Is.EqualTo(order), "every note went out, in the order it came in");
            Assert.That(rig.Sent.Count(s => s.Ev.Kind == NotesEvent.Off), Is.EqualTo(2048), "and every note off");
            Assert.That(rig.Sent.Where(s => s.Ev.Kind == NotesEvent.On).Select(s => s.T).Distinct().Count(), Is.InRange(2, 3), "over two or three blocks");
            Assert.That(rig.E.SoundingPitches, Is.EqualTo(0));
            Assert.That(rig.E.Pending, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------------------------------------------------- load

        [Test]
        public void ASixteenRepeatEchoOnATenNoteChordDropsNothingAndStaysInsideTheQueue()
        {
            // 10 notes, 16 repeats 10 ms apart (480 samples), the note itself 5 ms long (so each repeat is struck anew): 170 notes on and 170 off, at block 512.
            var rig = new NotesRig(512, v =>
            {
                On(v, P.EchoOn, P.LenOn); Pick(v, P.LenMode, "Fixed"); Pick(v, P.LenSync, "Free time"); Set(v, P.LenMs, 5);
                Set(v, P.Repeats, 16); Pick(v, P.EchoSync, "Free time"); Set(v, P.EchoMs, 10); Set(v, P.EchoDrop, 0); Set(v, P.EchoStep, 0); Set(v, P.EchoLen, 100);
            });
            for (int i = 0; i < 10; i++) rig.On(0, 48 + 3 * i, 100);
            rig.Run(20000);
            Assert.That(rig.E.Dropped, Is.EqualTo(0), "nothing refused");
            Assert.That(rig.Ons.Count(), Is.EqualTo(170));
            Assert.That(rig.Offs.Count(), Is.EqualTo(170));
            Assert.That(rig.MaxPerBlock, Is.LessThanOrEqualTo(NotesEngine.WireLimit));
            Assert.That(rig.E.PeakPending, Is.LessThan(NotesEngine.QueueCapacity - NotesEngine.QueueReserve), "the timeline stays out of its reserve");
            // Ten held keys of the same chord, released: all ends.
            for (int i = 0; i < 10; i++) rig.Off(rig.Pos, 48 + 3 * i);
            rig.Run(rig.Pos + 4000);
            Assert.That(rig.E.SoundingPitches, Is.EqualTo(0));
            Assert.That(rig.E.ActiveSources, Is.EqualTo(0));
            TestContext.Out.WriteLine($"peak timeline {rig.E.PeakPending} of {NotesEngine.QueueCapacity} (reserve {NotesEngine.QueueReserve}), most in a block {rig.MaxPerBlock}");
        }

        // ---------------------------------------------------------------------------------------------------------- audio thread

        [Test]
        public void TheEngineAllocatesNothingPerBlock()
        {
            var v = new ParamValues(Params.Table);
            On(v, AllSwitches);
            Set(v, P.Trans, 2); Set(v, P.SpreadRange, 2); Pick(v, P.SpreadPat, "Random"); Set(v, P.VelRandom, 20); Pick(v, P.LenMode, "Clamp");
            Set(v, P.Repeats, 6); Pick(v, P.EchoSync, "Free time"); Set(v, P.EchoMs, 40); Set(v, P.HumTime, 10); Set(v, P.HumVel, 20); Set(v, P.EchoStep, 3);
            var e = new NotesEngine(v);
            e.SetSampleRate(48000);
            const int Block = 64;
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                for (int i = 0; i < 16; i++)
                {
                    int note = 30 + (i * 5 + b) % 60;
                    int off = (i * 7 + b) % Block;
                    e.Input(NotesEvent.NoteOff(off, 0, note));
                    e.Input(NotesEvent.NoteOn(off, 0, note, 90 + (i & 15)));
                    if (i % 4 == 0) e.Input(new NotesEvent { Offset = off, Kind = 3, Channel = 0, Data1 = 0, Data2 = 8192 });
                }
                if (b % 200 == 199) e.Panic();
                e.Process(Block);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
            Assert.That(e.SentOn, Is.GreaterThan(1000), "the blocks did play notes");
        }

        // ---------------------------------------------------------------------------------------------------------- controllers

        [Test]
        public void ControllersBendAndPressurePassThroughInOrderBetweenTheNotes()
        {
            var rig = new NotesRig(64, v => On(v, AllSwitches));
            Set(rig.V, P.Trans, 0); Pick(rig.V, P.Scale, "Chromatic"); Set(rig.V, P.SpreadRange, 0); Set(rig.V, P.Repeats, 0); Set(rig.V, P.HumTime, 0); Set(rig.V, P.HumVel, 0);
            rig.On(100, 60);
            rig.Raw(100, new NotesEvent { Kind = 3, Channel = 0, Data1 = 0, Data2 = 9000 });
            rig.Raw(150, new NotesEvent { Kind = 4, Channel = 0, Data1 = 0, Data2 = 77 });
            rig.Raw(150, new NotesEvent { Kind = 2, Channel = 0, Data1 = 64, Data2 = 127 });
            rig.Off(200, 60);
            rig.Run(1000);
            Assert.That(Plain(rig.Sent), Is.EqualTo(new List<(long, int, int, int, int)> { (100, 0, 0, 60, 100), (100, 3, 0, 0, 9000), (150, 4, 0, 0, 77), (150, 2, 0, 64, 127), (200, 1, 0, 60, 0) }));
        }

        // ---------------------------------------------------------------------------------------------------------- presets

        static readonly string[] ExpectedPresets =
        {
            "Scale — Minor Snap", "Spread — Octave Cascade", "Echo — Dotted Eighth Echo", "Echo — Rising Fifths",
            "Velocity — Hard Hitter", "Velocity — Soft Touch", "Length — Staccato", "Humanize — Loose Player",
        };

        /// <summary>The phrase through a preset: what went out, as (time, note, velocity, on).</summary>
        internal static NotesRig PlayPhrase(IReadOnlyDictionary<int, int> preset, int block = 64)
        {
            var rig = new NotesRig(block, v => { foreach (var kv in preset) v.Set(kv.Key, kv.Value); }, NotesPhrase.SamplesPerBeat);
            NotesPhrase.Feed(rig);
            rig.Run((long)(NotesPhrase.Beats * NotesPhrase.SamplesPerBeat) + 48000 * 12);
            return rig;
        }

        /// <summary>Notes of the output paired with their note offs: (start, length, pitch, velocity).</summary>
        internal static List<(long Start, long Len, int Note, int Vel)> Spans(NotesRig rig)
        {
            var open = new Dictionary<int, (long T, int Vel)>();
            var list = new List<(long, long, int, int)>();
            foreach (var (t, e) in rig.Sent)
            {
                int k = e.Channel * 128 + e.Data1;
                if (e.Kind == NotesEvent.On) open[k] = (t, e.Data2);
                else if (e.Kind == NotesEvent.Off && open.TryGetValue(k, out var o)) { list.Add((o.T, t - o.T, e.Data1, o.Vel)); open.Remove(k); }
            }
            return list.OrderBy(s => s.Item1).ThenBy(s => s.Item3).ToList();
        }

        [Test]
        public void ThereAreEightClearlyDifferentAudiblePresets()
        {
            var presets = PresetTests.AllPresets().Where(p => p.Machine == "Neo Notes").Select(p => (Name: p.Preset, Values: (IReadOnlyDictionary<int, int>)p.Values.Where(kv => kv.Key >= 0).ToDictionary(kv => kv.Key, kv => kv.Value))).ToList();
            Assert.That(presets.Select(p => p.Name).ToArray(), Is.EqualTo(ExpectedPresets));

            var dry = Spans(PlayPhrase(new Dictionary<int, int>()));
            Assert.That(dry.Count, Is.EqualTo(NotesPhrase.Items.Sum(i => i.Notes.Length)));
            double dryVel = dry.Average(s => s.Vel), dryLen = dry.Average(s => s.Len);
            var results = presets.Select(p => (p.Name, Rig: PlayPhrase(p.Values), Spans: Spans(PlayPhrase(p.Values)))).ToList();
            foreach (var (name, rig, spans) in results)
            {
                Assert.That(rig.Sent.Count(s => s.Ev.Kind == NotesEvent.On), Is.EqualTo(rig.Sent.Count(s => s.Ev.Kind == NotesEvent.Off)), name);
                Assert.That(rig.E.SoundingPitches, Is.EqualTo(0), name);
                Assert.That(rig.E.Dropped, Is.EqualTo(0), name);
                Assert.That(spans, Is.Not.EqualTo(dry), $"{name} changes the phrase");
                TestContext.Out.WriteLine($"{name}: {spans.Count} notes, mean velocity {spans.Average(s => s.Vel):F1}, mean length {spans.Average(s => s.Len) / 48.0:F0} ms (dry: {dry.Count} notes, {dryVel:F1}, {dryLen / 48.0:F0} ms)");
            }
            // Pairwise different.
            for (int i = 0; i < results.Count; i++)
                for (int j = i + 1; j < results.Count; j++)
                    Assert.That(results[i].Spans, Is.Not.EqualTo(results[j].Spans), $"{results[i].Name} and {results[j].Name} are the same");

            List<(long Start, long Len, int Note, int Vel)> S(string name) => results.First(r => r.Name == name).Spans;
            // Each does what its name says, by a clear margin.
            var snap = S("Scale — Minor Snap");
            Assert.That(snap.Count, Is.EqualTo(dry.Count));
            Assert.That(snap.Zip(dry, (a, b) => a.Note != b.Note).Count(x => x), Is.GreaterThanOrEqualTo(6), "Minor Snap moves several notes");
            Assert.That(snap.All(s => new[] { 0, 2, 3, 5, 7, 8, 10 }.Contains(s.Note % 12)), Is.True, "all in C minor");
            var cascade = S("Spread — Octave Cascade");
            var dryClasses = dry.Select(s => s.Note % 12).ToHashSet();
            Assert.That(cascade.All(s => dryClasses.Contains(s.Note % 12)), Is.True, "octaves only: the pitch classes are the phrase's");
            Assert.That(cascade.Max(s => s.Note) - cascade.Min(s => s.Note), Is.GreaterThanOrEqualTo(dry.Max(s => s.Note) - dry.Min(s => s.Note) + 12));
            Assert.That(S("Echo — Dotted Eighth Echo").Count, Is.GreaterThanOrEqualTo(dry.Count * 3), "at least three times the notes");
            var fifths = S("Echo — Rising Fifths");
            Assert.That(fifths.Count, Is.GreaterThanOrEqualTo(dry.Count * 2));
            Assert.That(fifths.Count(s => s.Note > 90), Is.GreaterThanOrEqualTo(8), "the fifths climb high");
            Assert.That(S("Velocity — Hard Hitter").Average(s => s.Vel), Is.GreaterThanOrEqualTo(dryVel + 20));
            Assert.That(S("Velocity — Soft Touch").Average(s => s.Vel), Is.LessThanOrEqualTo(dryVel * 0.6));
            Assert.That(S("Length — Staccato").Average(s => s.Len), Is.LessThanOrEqualTo(dryLen * 0.5), "at most half as long");
            var loose = S("Humanize — Loose Player");
            var late = dry.Select(d => loose.Where(l => l.Note == d.Note && l.Start >= d.Start).Min(l => l.Start) - d.Start).ToList();
            Assert.That(late.Average(), Is.GreaterThanOrEqualTo(48000 * 0.010), "late by 10 ms or more on average");
            Assert.That(late.Max(), Is.LessThanOrEqualTo(48000 * 0.048), "never later than the setting plus the chord spread of 8 ms");
            Assert.That(loose.Select(s => s.Vel).Distinct().Count(), Is.GreaterThan(dry.Select(s => s.Vel).Distinct().Count()), "the velocities are uneven");
        }
    }
}
