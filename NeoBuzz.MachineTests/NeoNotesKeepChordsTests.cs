#nullable disable
using System.Security.Cryptography;
using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Notes;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Notes, Humanize: Keep chords (NOT1b, docs/specs/neo-notes.md §4 and §6). Appended after Seed; Off plays exactly as before it existed.</summary>
    [TestFixture]
    public class NeoNotesKeepChordsTests
    {
        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));
        static void Pick(ParamValues v, int p, string name)
        {
            int i = Array.IndexOf(Params.Table[p].Names, name);
            Assert.That(i, Is.GreaterThanOrEqualTo(0), name);
            v.Set(p, i);
        }
        const long Spacing = 12000;        // 250 ms between chords: more than the longest delay (40 + 8 ms)

        static List<int> ChordPitches = new() { 60, 64, 67, 71 };

        // ------------------------------------------------------------------------------------------------------ the gates

        [TestCase(1)]
        [TestCase(64)]
        [TestCase(512)]
        public void ChordNotesStayWithin8MsAndTheSharedDelayCoversTheWholeRange(int block)
        {
            int chords = block == 64 ? 1000 : 120;
            var rig = new NotesRig(block, v => { v.Set(P.HumOn, 1); v.Set(P.HumChord, 1); Set(v, P.HumTime, 40); Set(v, P.Seed, 11); });
            for (int c = 0; c < chords; c++)
                foreach (int n in ChordPitches) rig.Note(1000 + c * Spacing, 3000, n);
            rig.Run(1000 + (chords + 2) * Spacing);
            var ons = rig.Ons.ToList();
            Assert.That(ons.Count, Is.EqualTo(chords * 4));
            double maxGapMs = 0, minShared = 1e9, maxShared = -1;
            var shared = new List<double>();
            for (int c = 0; c < chords; c++)
            {
                long t0 = 1000 + c * Spacing;
                var mine = ons.Where(o => o.T >= t0 && o.T < t0 + Spacing).Select(o => o.T).ToList();
                Assert.That(mine.Count, Is.EqualTo(4), "chord " + c);
                Assert.That(mine.Min(), Is.GreaterThanOrEqualTo(t0), "only late, never early");
                double gap = (mine.Max() - mine.Min()) * 1000.0 / 48000;
                maxGapMs = Math.Max(maxGapMs, gap);
                double s = (mine.Min() - t0) * 1000.0 / 48000;       // the shared delay, plus the chord's smallest spread (0 to 8 ms)
                shared.Add(s);
                minShared = Math.Min(minShared, s); maxShared = Math.Max(maxShared, s);
            }
            Assert.That(maxGapMs, Is.LessThanOrEqualTo(8.0), "a chord's first and last note on are at most 8 ms apart");
            Assert.That(minShared, Is.LessThan(3.0), "the shared delay reaches down to about 0");
            Assert.That(maxShared, Is.GreaterThan(37.0), "and up to about Humanize time");
            if (block == 64)
            {
                // Spread over the range: eight bins of 5 ms, each holds a fair share of the 1000 chords (125 expected).
                var bins = new int[8];
                foreach (double s in shared) bins[Math.Min(7, (int)(s / 5))]++;
                Assert.That(bins.Min(), Is.GreaterThan(80), string.Join(",", bins));
                Assert.That(bins.Max(), Is.LessThan(175), string.Join(",", bins));
                TestContext.Out.WriteLine($"KEEPCHORDS gate: 1000 chords x 4 notes, Humanize time 40 ms: chord gap max {maxGapMs:F3} ms; shared delay (chord's earliest note) {minShared:F3} to {maxShared:F3} ms; bins of 5 ms {string.Join(",", bins)}");
            }
        }

        [Test]
        public void EachNoteOffIsAsLateAsItsOwnNoteOnSoLengthsAreKept()
        {
            var rig = new NotesRig(64, v => { v.Set(P.HumOn, 1); v.Set(P.HumChord, 1); Set(v, P.HumTime, 40); Set(v, P.Seed, 4); });
            for (int c = 0; c < 200; c++)
                for (int i = 0; i < 4; i++) rig.Note(1000 + c * Spacing, 2000 + 300 * i, ChordPitches[i]);
            rig.Run(1000 + 202 * Spacing);
            var ons = rig.Ons.ToList();
            var offs = rig.Offs.ToList();
            for (int c = 0; c < 200; c++)
                for (int i = 0; i < 4; i++)
                {
                    long t0 = 1000 + c * Spacing;
                    long on = ons.First(o => o.Note == ChordPitches[i] && o.T >= t0 && o.T < t0 + Spacing).T;
                    long off = offs.First(o => o.Note == ChordPitches[i] && o.T >= t0 && o.T < t0 + Spacing).T;
                    Assert.That(off - on, Is.EqualTo(2000 + 300 * i), $"chord {c} note {i}: length kept");
                }
        }

        [Test]
        public void ADeterministicDelayPerSeedAndHumanizeVelocityStaysPerNote()
        {
            List<long> Run(int seed, int humVel)
            {
                var rig = new NotesRig(64, v => { v.Set(P.HumOn, 1); v.Set(P.HumChord, 1); Set(v, P.HumTime, 40); Set(v, P.HumVel, humVel); Set(v, P.Seed, seed); });
                for (int c = 0; c < 50; c++) foreach (int n in ChordPitches) rig.Note(1000 + c * Spacing, 3000, n);
                rig.Run(1000 + 52 * Spacing);
                return rig.Ons.Select(o => o.T).ToList();
            }
            Assert.That(Run(3, 0), Is.EqualTo(Run(3, 0)), "same seed, same events");
            Assert.That(Run(3, 0), Is.Not.EqualTo(Run(4, 0)), "another seed, others");
            var rig2 = new NotesRig(64, v => { v.Set(P.HumOn, 1); v.Set(P.HumChord, 1); Set(v, P.HumTime, 40); Set(v, P.HumVel, 30); Set(v, P.Seed, 3); });
            for (int c = 0; c < 50; c++) foreach (int n in ChordPitches) rig2.Note(1000 + c * Spacing, 3000, n, 100);
            rig2.Run(1000 + 52 * Spacing);
            var vels = rig2.Ons.Select(o => o.Vel).ToList();
            Assert.That(vels.Distinct().Count(), Is.GreaterThan(8), "the velocity differs from note to note inside chords");
            Assert.That(vels.All(x => x >= 70 && x <= 127), Is.True);
        }

        // ------------------------------------------------------------------------------------------------------ Off is unchanged

        static string Hash(IEnumerable<(long T, NotesEvent Ev)> sent)
        {
            var sb = new StringBuilder();
            foreach (var (t, e) in sent) sb.Append(t).Append(',').Append(e.Kind).Append(',').Append(e.Channel).Append(',').Append(e.Data1).Append(',').Append(e.Data2).Append(';');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 16).ToLowerInvariant();
        }

        /// <summary>The phrase through a preset, Keep chords forced Off (the round 1 behaviour of every preset).</summary>
        static string PresetHash(IReadOnlyDictionary<int, int> values)
        {
            var rig = new NotesRig(64, v => { foreach (var kv in values) v.Set(kv.Key, kv.Value); v.Set(P.HumChord, 0); }, NotesPhrase.SamplesPerBeat);
            NotesPhrase.Feed(rig);
            rig.Run(48000 * 14);
            return Hash(rig.Sent);
        }

        /// <summary>Chords, singles and overlapping keys, every section on, Humanize on with a seed, Keep chords Off.</summary>
        static string BusyHash(int seed)
        {
            var rnd = new Random(seed);
            var rig = new NotesRig(64, v =>
            {
                foreach (int s in new[] { P.TransOn, P.SpreadOn, P.VelOn, P.LenOn, P.EchoOn, P.HumOn }) v.Set(s, 1);
                Set(v, P.Trans, 2); Set(v, P.SpreadRange, 2); Pick(v, P.SpreadPat, "Random"); Set(v, P.VelRandom, 20); Pick(v, P.LenMode, "Clamp");
                Set(v, P.Repeats, 3); Pick(v, P.EchoSync, "Free time"); Set(v, P.EchoMs, 60); Set(v, P.HumTime, 40); Set(v, P.HumVel, 20); Set(v, P.Seed, seed);
                v.Set(P.HumChord, 0);
            });
            long t = 0;
            var held = new Dictionary<int, long>();
            for (int i = 0; i < 400; i++)
            {
                t += rnd.Next(0, 3) == 0 ? 0 : rnd.Next(500, 9000);
                int size = rnd.Next(1, 5);
                for (int k = 0; k < size; k++)
                {
                    int n = 40 + rnd.Next(24);
                    if (held.TryGetValue(n, out long until) && until >= t) continue;
                    long len = rnd.Next(1, 20000);
                    held[n] = t + len;
                    rig.Note(t, len, n, rnd.Next(1, 128));
                }
            }
            rig.Run(t + 48000 * 3);
            return Hash(rig.Sent);
        }

        // Golden: the events of be1648d1c (before Keep chords existed), for the same input.
        static readonly Dictionary<string, string> Golden = new()
        {
            ["Scale — Minor Snap"] = "339678e49240fb16",
            ["Spread — Octave Cascade"] = "000453496abb63b4",
            ["Echo — Dotted Eighth Echo"] = "c9a51c3b9c6fe72e",
            ["Echo — Rising Fifths"] = "b1f683b254e0f204",
            ["Velocity — Hard Hitter"] = "7a736487e3be73b1",
            ["Velocity — Soft Touch"] = "07a012dbb82473c4",
            ["Length — Staccato"] = "e19579b66005fb47",
            ["Humanize — Loose Player"] = "5366febc2d5b478c",
            ["busy 1"] = "1b23825d7e6ba8a9",
            ["busy 2"] = "b1a509927aa13ad5",
            ["busy 3"] = "bfe0170b7974f423",
            ["busy 4"] = "95e396ed4d672e64",
        };

        [Test, Explicit("writes the golden hashes of the old engine")]
        public void WriteGolden()
        {
            var sb = new StringBuilder();
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Notes"))
                sb.Append("            [\"" + p.Preset + "\"] = \"" + PresetHash(p.Values.Where(kv => kv.Key >= 0).ToDictionary(kv => kv.Key, kv => kv.Value)) + "\",\n");
            for (int s = 1; s <= 4; s++) sb.Append("            [\"busy " + s + "\"] = \"" + BusyHash(s) + "\",\n");
            File.WriteAllText(Environment.GetEnvironmentVariable("NOTES_GOLDEN"), sb.ToString());
        }

        [Test]
        public void WithKeepChordsOffEveryPresetAndBusyInputPlaysBitIdenticalToTheBuildBefore()
        {
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Notes"))
                Assert.That(PresetHash(p.Values.Where(kv => kv.Key >= 0).ToDictionary(kv => kv.Key, kv => kv.Value)), Is.EqualTo(Golden[p.Preset]), p.Preset);
            for (int s = 1; s <= 4; s++) Assert.That(BusyHash(s), Is.EqualTo(Golden["busy " + s]), "busy " + s);
            Assert.That(Golden.Count, Is.EqualTo(8 + 4));
        }

        [Test]
        public void ASongSavedByTheBuildBeforeLoadsPlaysTheSameAndShowsKeepChordsOff()
        {
            // The old build saved 37 values by position; Keep chords is appended at 37, so an old song has no value for it and takes its default.
            Assert.That(P.HumChord, Is.EqualTo(37));
            Assert.That(Params.Table.Length, Is.EqualTo(38));
            Assert.That(Params.Table[P.HumChord].Default, Is.EqualTo(0));
            Assert.That(Params.Table[P.HumChord].Id, Is.EqualTo("keep_chords"));
            // Round 1's Loose Player, as it was saved: Humanize 40 ms, 25 %, seed 3, Velocity random 20 %.
            var saved = new Dictionary<int, int>
            {
                [P.HumOn] = 1, [P.HumTime] = Params.Table[P.HumTime].Raw(40), [P.HumVel] = Params.Table[P.HumVel].Raw(25), [P.Seed] = Params.Table[P.Seed].Raw(3),
                [P.VelOn] = 1, [P.VelRandom] = Params.Table[P.VelRandom].Raw(20),
            };
            var loaded = new ParamValues(Params.Table);
            foreach (var kv in saved) loaded.Set(kv.Key, kv.Value);
            Assert.That(loaded.Value(P.HumChord), Is.EqualTo(0), "shows Keep chords Off");
            var rig = new NotesRig(64, v => { foreach (var kv in saved) v.Set(kv.Key, kv.Value); }, NotesPhrase.SamplesPerBeat);
            NotesPhrase.Feed(rig);
            rig.Run(48000 * 14);
            Assert.That(Hash(rig.Sent), Is.EqualTo(Golden["Humanize — Loose Player"]), "plays identically to the old build");
        }

        // ------------------------------------------------------------------------------------------------------ no stuck notes

        [TestCase(false)]
        [TestCase(true)]
        public void TenThousandEventsWithKeepChordsEchoAndLengthLeaveNothingSounding(bool stopWhileHeld)
        {
            var rnd = new Random(stopWhileHeld ? 77 : 78);
            var rig = new NotesRig(64, v =>
            {
                v.Set(P.HumOn, 1); v.Set(P.HumChord, 1); Set(v, P.HumTime, 50); Set(v, P.HumVel, 20); Set(v, P.Seed, 6);
                v.Set(P.EchoOn, 1); Set(v, P.Repeats, 3); Pick(v, P.EchoSync, "Free time"); Set(v, P.EchoMs, 35); Set(v, P.EchoDrop, 20); Set(v, P.EchoLen, 80);
                v.Set(P.LenOn, 1); Pick(v, P.LenMode, "Clamp"); Set(v, P.ClampMin, 30); Set(v, P.ClampMax, 400);
            });
            var e = rig.E;
            var sounding = new HashSet<int>();
            int bad = 0, checkedUpTo = 0;
            void Check()
            {
                for (int i = checkedUpTo; i < rig.Sent.Count; i++)
                {
                    var ev = rig.Sent[i].Ev;
                    int k = ev.Channel * 128 + ev.Data1;
                    if (ev.Kind == NotesEvent.On) { if (!sounding.Add(k)) bad++; }
                    else if (ev.Kind == NotesEvent.Off) { if (!sounding.Remove(k)) bad++; }
                }
                checkedUpTo = rig.Sent.Count;
            }
            var held = new Dictionary<int, long>();
            long t = 0;
            int events = 0;
            while (events < 10000)
            {
                t += rnd.Next(0, 4) == 0 ? 0 : rnd.Next(200, 12000);
                int size = rnd.Next(1, 6);
                for (int k = 0; k < size && events < 10000; k++)
                {
                    int n = 48 + rnd.Next(12);                                   // few pitches: chords overlap each other and their own echoes
                    if (held.TryGetValue(n, out long until) && until >= t) continue;
                    long len = rnd.Next(0, 3) == 0 ? 0 : rnd.Next(1, 30000);
                    held[n] = t + len;
                    rig.Note(t, len, n, rnd.Next(1, 128));
                    events += 2;
                }
                if (rig.Pos < t - 100000) { rig.Run(t - 50000); Check(); }
            }
            rig.Run(t + 1);
            Check();
            if (stopWhileHeld)
            {
                e.Panic();
                rig.Run(rig.Pos + 64);
                Check();
            }
            else
            {
                long deadline = rig.Pos + 48000L * 30;
                while ((e.Pending > 0 || e.SoundingPitches > 0) && rig.Pos < deadline) { rig.Run(rig.Pos + 48000); Check(); }
            }
            rig.Run(rig.Pos + 256);
            Check();
            Assert.That(bad, Is.EqualTo(0), "a pitch was started twice or ended twice");
            Assert.That(sounding, Is.Empty);
            Assert.That(e.SentOn, Is.EqualTo(e.SentOff), "every note on has its note off");
            Assert.That(e.Pending, Is.EqualTo(0));
            Assert.That(e.ActiveSources, Is.EqualTo(0));
            Assert.That(e.SoundingPitches, Is.EqualTo(0));
            Assert.That(rig.OrderViolations, Is.EqualTo(0));
            Assert.That(e.SentOn, Is.GreaterThan(2000));
            TestContext.Out.WriteLine($"KEEPCHORDS stress ({(stopWhileHeld ? "stop while held" : "released")}): {events} input events, {e.SentOn} notes on, {e.SentOff} notes off, {e.Dropped} refused");
        }

        // ------------------------------------------------------------------------------------------------------ audio thread

        [Test]
        public void KeepChordsAllocatesNothingPerBlock()
        {
            var v = new ParamValues(Params.Table);
            foreach (int s in new[] { P.TransOn, P.SpreadOn, P.VelOn, P.LenOn, P.EchoOn, P.HumOn }) v.Set(s, 1);
            v.Set(P.HumChord, 1);
            Set(v, P.Repeats, 6); Pick(v, P.EchoSync, "Free time"); Set(v, P.EchoMs, 40); Set(v, P.HumTime, 40); Set(v, P.HumVel, 20); Pick(v, P.LenMode, "Clamp");
            var e = new NotesEngine(v);
            e.SetSampleRate(48000);
            const int Block = 64;
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                for (int i = 0; i < 16; i++)
                {
                    int note = 30 + (i / 4 * 5 + b) % 60 + i % 4 * 3;
                    int off = (i / 4 * 7 + b) % Block;                         // four notes a chord, on the same sample
                    e.Input(NotesEvent.NoteOff(off, 0, note));
                    e.Input(NotesEvent.NoteOn(off, 0, note, 90 + (i & 15)));
                }
                if (b % 200 == 199) e.Panic();
                e.Process(Block);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
            Assert.That(e.SentOn, Is.GreaterThan(1000));
        }

        [Test]
        public void TheRepeatsOfAChordShareTheirDelayToo()
        {
            var rig = new NotesRig(64, v =>
            {
                v.Set(P.HumOn, 1); v.Set(P.HumChord, 1); Set(v, P.HumTime, 40); Set(v, P.Seed, 2);
                v.Set(P.EchoOn, 1); Set(v, P.Repeats, 2); Pick(v, P.EchoSync, "Free time"); Set(v, P.EchoMs, 500);
            });
            for (int c = 0; c < 50; c++) foreach (int n in ChordPitches) rig.Note(1000 + c * 96000L, 5000, n);
            rig.Run(1000 + 52 * 96000L);
            var ons = rig.Ons.ToList();
            double worst = 0;
            for (int c = 0; c < 50; c++)
                for (int k = 0; k <= 2; k++)
                {
                    long from = 1000 + c * 96000L + k * 24000L;
                    var g = ons.Where(o => o.T >= from && o.T < from + 5000).Select(o => o.T).ToList();
                    Assert.That(g.Count, Is.EqualTo(4), $"chord {c} repeat {k}");
                    worst = Math.Max(worst, (g.Max() - g.Min()) * 1000.0 / 48000);
                }
            Assert.That(worst, Is.LessThanOrEqualTo(8.0));
        }
    }
}
