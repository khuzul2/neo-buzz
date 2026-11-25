// Neo Arp into Neo Poly (docs/specs/neo-arp.md §9): the chain gate (a note insert in front of the instrument, and a free note wire), the factory presets
// (audible, different from each other, free of clicks) and the progression the listening pack plays.
using System.Xml.Linq;
using NeoBuzz.Arp;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>The progression of the listening pack and the render of an arpeggio through Neo Poly on one fixed plain patch.</summary>
    internal static class ArpPack
    {
        public const int Rate = 48000;
        public const int Bar = 4 * 24000;                      // 120 BPM, 48 kHz

        // Four bars of our own: C major seventh, A minor seventh, F major seventh, G dominant seventh. Each chord is played out of order.
        public static readonly int[][] Chords =
        {
            new[] { 64, 71, 60, 67 },       // C E G B
            new[] { 60, 67, 57, 64 },       // A C E G
            new[] { 57, 64, 53, 60 },       // F A C E
            new[] { 62, 55, 65, 59 },       // G B D F
        };

        public static string FindRepo()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "ReBuzz.sln"))) d = d.Parent;
            return d!.FullName;
        }

        public static List<(string Name, string Comment, Dictionary<int, int> Values)> Presets()
        {
            var file = Path.Combine(FindRepo(), "Machines", "NeoArp", "Neo Arp.prs.xml");
            var list = new List<(string, string, Dictionary<int, int>)>();
            foreach (var item in XDocument.Load(file).Root!.Elements("Item"))
            {
                var d = new Dictionary<int, int>();
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter")) d[(int)p.Attribute("Index")!] = (int)p.Attribute("Value")!;
                list.Add(((string)item.Attribute("Key")!, (string)item.Element("Preset")!.Attribute("Comment") ?? "", d));
            }
            return list;
        }

        /// <summary>A preset (its values over the defaults) playing the progression: <paramref name="bars"/> bars, the four chords again from bar 5.</summary>
        public static ArpRig Play(Dictionary<int, int> preset, int bars, Action<ArpRig> setup = null)
        {
            var rig = new ArpRig(Rate);
            foreach (var kv in preset) rig.V.Set(kv.Key, kv.Value);
            setup?.Invoke(rig);
            for (int b = 0; b < bars; b++)
                foreach (int n in Chords[b % 4])
                {
                    rig.NoteOn((long)b * Bar, n, 100);
                    rig.NoteOff((long)(b + 1) * Bar - 1500, n);
                }
            rig.Run((long)bars * Bar);
            return rig;
        }

        /// <summary>One fixed plain patch: a saw through a low-pass, a short plucked envelope, no drift.</summary>
        public static ParamValues PlainPatch()
        {
            var v = new ParamValues(NeoBuzz.Poly.Params.Table);
            void S(int p, double x) => v.Set(p, NeoBuzz.Poly.Params.Table[p].Raw(x));
            S(NeoBuzz.Poly.P.O1Shape, 2.0);                    // saw
            S(NeoBuzz.Poly.P.Drift, 0);
            S(NeoBuzz.Poly.P.F1Cutoff, 3000);
            S(NeoBuzz.Poly.P.F1Reso, 0.1);
            S(NeoBuzz.Poly.P.AmpA, 0.003);
            S(NeoBuzz.Poly.P.AmpD, 0.35);
            S(NeoBuzz.Poly.P.AmpS, 0.45);
            S(NeoBuzz.Poly.P.AmpR, 0.15);
            S(NeoBuzz.Poly.P.Volume, -6);
            return v;
        }

        /// <summary>The events the instrument was sent, played through Neo Poly the way the host does it: a block's events are taken at its start (blocks of 64).</summary>
        public static (float[] L, float[] R) Render(IReadOnlyList<ArpRig.Rec> log, int total, ParamValues patch)
        {
            var e = new NeoBuzz.Poly.PolyEngine(patch);
            e.SetSampleRate(Rate);
            var l = new float[total];
            var r = new float[total];
            var bl = new float[NeoBuzz.Poly.PolyEngine.MaxBlock];
            var br = new float[NeoBuzz.Poly.PolyEngine.MaxBlock];
            int ei = 0;
            var events = log.Where(x => x.Kind == ArpKind.NoteOn || x.Kind == ArpKind.NoteOff).OrderBy(x => x.At).ToList();
            for (int pos = 0; pos < total; pos += 64)
            {
                int n = Math.Min(64, total - pos);
                while (ei < events.Count && events[ei].At < pos + n)
                {
                    var x = events[ei++];
                    if (x.Kind == ArpKind.NoteOn) e.NoteOn(1000 + x.Note, x.Note, x.Vel / 127f); else e.NoteOff(1000 + x.Note);
                }
                e.Process(bl, br, n);
                Array.Copy(bl, 0, l, pos, n);
                Array.Copy(br, 0, r, pos, n);
            }
            return (l, r);
        }

        public static double Hz(int note) => 440.0 * Math.Pow(2, (note - 69) / 12.0);

        /// <summary>The magnitude of one frequency in a stretch (Goertzel).</summary>
        public static double Goertzel(float[] x, int start, int length, double hz)
        {
            double w = 2 * Math.PI * hz / Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < length; i++)
            {
                double s = x[start + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (length - 1))) + c * s1 - s2;
                s2 = s1; s1 = s;
            }
            return Math.Sqrt(s1 * s1 + s2 * s2 - c * s1 * s2);
        }
    }

    [TestFixture]
    public class NeoArpChainTests
    {
        static ParamValues SinePatch()
        {
            var v = ArpPack.PlainPatch();
            v.Set(NeoBuzz.Poly.P.O1Shape, NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.O1Shape].Raw(0));   // a sine: the pitch is easy to tell
            v.Set(NeoBuzz.Poly.P.F1Cutoff, 1000);
            v.Set(NeoBuzz.Poly.P.AmpS, 1000);
            return v;
        }

        static string Heard(float[] x, long on, int[] pool)
        {
            int start = (int)on + 3000, len = 6000;
            var best = pool.OrderByDescending(n => ArpPack.Goertzel(x, start, len, ArpPack.Hz(n))).First();
            return best.ToString();
        }

        /// <summary>
        /// The chain gate: the same chord reaches the arpeggiator two ways, and Neo Poly plays the arpeggio both times. (a) As a note insert: the track's notes
        /// enter the first insert at the start of their block (offset 0, as NoteWires.RouteTrackInput queues them). (b) On a free note wire: a note source
        /// sends them at the samples it made them at. The engine's own delivery (queues, merge, order) is NW1's, tested with its test machine.
        /// </summary>
        [TestCase(true), TestCase(false)]
        public void TheInstrumentPlaysTheArpeggio(bool asInsert)
        {
            var rig = new ArpRig(ArpPack.Rate);
            rig.Set(P.Pattern, 0).Set(P.Octaves, 2).Set(P.Rate, 3).Set(P.Gate, 80);
            long at = asInsert ? 64 * 3 : 3000 + 17;                     // an insert gets it at the start of a block; a wire, at the sample
            foreach (int n in ArpPack.Chords[0]) rig.NoteOn(at, n, 100);
            long off = ArpPack.Bar * 2 - 5000;
            foreach (int n in ArpPack.Chords[0]) rig.NoteOff(off, n);
            rig.Run(ArpPack.Bar * 2);
            var ons = rig.Ons.ToList();
            var pool = new[] { 60, 64, 67, 71, 72, 76, 79, 83 };
            // Free running: the figure is where the song position puts it. The notes arrived during step 0, so the first step heard is step 1.
            var expect = Enumerable.Range(1, 8).Select(s => pool[s % 8]).ToArray();
            Assert.That(ons.Take(8).Select(r => r.Note), Is.EqualTo(expect), "what the arpeggiator sent");
            Assert.That(ons[0].At, Is.EqualTo(12000).Within(1), "on the grid after the notes arrived");
            var (l, _) = ArpPack.Render(rig.Log, (int)ArpPack.Bar * 2 + 24000, SinePatch());
            for (int k = 0; k < 8; k++)
            {
                Assert.That(Heard(l, ons[k].At, pool), Is.EqualTo(expect[k].ToString()), $"step {k}: the pitch the instrument sounds");
                int start = (int)ons[k].At + 3000;
                double rms = Math.Sqrt(Enumerable.Range(start, 6000).Sum(i => l[i] * (double)l[i]) / 6000);
                Assert.That(20 * Math.Log10(rms), Is.GreaterThan(-40), $"step {k}: it sounds");
            }
            // Between the notes of the held chord there is no chord: before the first step nothing sounded.
            double early = Math.Sqrt(Enumerable.Range(0, (int)ons[0].At).Sum(i => l[i] * (double)l[i]) / ons[0].At);
            double first = Math.Sqrt(Enumerable.Range((int)ons[0].At + 3000, 6000).Sum(i => l[i] * (double)l[i]) / 6000);
            Assert.That(early, Is.LessThan(first * 0.02), "nothing sounds before the first step (Neo Poly's own floor aside)");
        }

        [Test]
        public void EveryPresetIsAudibleThroughNeoPoly()
        {
            var presets = ArpPack.Presets();
            Assert.That(presets.Count, Is.InRange(6, 8));
            foreach (var (name, _, values) in presets)
            {
                var rig = ArpPack.Play(values, 4);
                int ons = rig.Ons.Count();
                Assert.That(ons, Is.GreaterThanOrEqualTo(8), $"{name}: it plays notes");
                Assert.That(ArpRig.Balance(rig.Log).Where(p => !p.Contains("never released")), Is.Empty, name);
                var (l, r) = ArpPack.Render(rig.Log, 4 * ArpPack.Bar + 24000, ArpPack.PlainPatch());
                double loudest = 0, peak = 0;
                for (int at = 0; at + 2400 <= l.Length; at += 1200)
                {
                    double s = 0;
                    for (int i = at; i < at + 2400; i++) s += l[i] * (double)l[i];
                    loudest = Math.Max(loudest, Math.Sqrt(s / 2400));
                }
                for (int i = 0; i < l.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
                TestContext.Out.WriteLine($"{name}: {ons} notes in 4 bars, loudest 50 ms {20 * Math.Log10(loudest):0.0} dB RMS, peak {20 * Math.Log10(peak):0.0} dBFS");
                Assert.That(20 * Math.Log10(loudest), Is.InRange(-30.0, -6.0), $"{name}: level");
                Assert.That(20 * Math.Log10(peak), Is.InRange(-20.0, -1.0), $"{name}: peak");
            }
        }

        /// <summary>A preset as a piano roll over the progression: on a 1/16 grid, the note names sounding, as a set of (cell, pitch relative to the chord's lowest).</summary>
        static HashSet<(int Cell, int Pitch)> Roll(ArpRig rig)
        {
            var set = new HashSet<(int, int)>();
            foreach (var on in rig.Ons)
            {
                int cell = (int)Math.Round(on.At / 6000.0);
                int bar = (int)(on.At / ArpPack.Bar);
                set.Add((cell, on.Note - ArpPack.Chords[bar % 4].Min()));
            }
            return set;
        }

        [Test]
        public void ThePresetsAreClearlyDifferentFromEachOther()
        {
            var presets = ArpPack.Presets();
            var rolls = presets.Select(p => (p.Name, Roll(ArpPack.Play(p.Values, 4)), ArpPack.Play(p.Values, 4))).ToList();
            for (int i = 0; i < rolls.Count; i++)
                for (int j = i + 1; j < rolls.Count; j++)
                {
                    var a = rolls[i].Item2;
                    var b = rolls[j].Item2;
                    int common = a.Intersect(b).Count();
                    int union = a.Union(b).Count();
                    double similarity = common / (double)Math.Max(1, union);
                    // Also different in feel: the number of notes, or how long they are.
                    double lenA = MeanLength(rolls[i].Item3), lenB = MeanLength(rolls[j].Item3);
                    TestContext.Out.WriteLine($"{rolls[i].Name} / {rolls[j].Name}: {similarity:P0} of the roll in common, mean length {lenA:0} / {lenB:0} samples, {a.Count} / {b.Count} notes");
                    bool otherFeel = Math.Max(lenA, lenB) >= 2 * Math.Min(lenA, lenB) || Math.Max(a.Count, b.Count) >= 2 * Math.Min(a.Count, b.Count);
                    Assert.That(similarity < 0.25 || otherFeel, Is.True, $"{rolls[i].Name} / {rolls[j].Name}: the same notes in the same places, as long and as many");
                }
        }

        static double MeanLength(ArpRig rig)
        {
            var lengths = new List<double>();
            foreach (var on in rig.Ons)
            {
                var off = rig.Log.FirstOrDefault(r => r.Kind == ArpKind.NoteOff && r.Note == on.Note && r.At >= on.At);
                if (off.Kind == ArpKind.NoteOff) lengths.Add(off.At - on.At);
            }
            return lengths.Count == 0 ? 0 : lengths.Average();
        }

        [Test]
        public void ThePresetsRenderWithoutClicks()
        {
            foreach (var (name, _, values) in ArpPack.Presets())
            {
                var rig = ArpPack.Play(values, 4);
                var (l, r) = ArpPack.Render(rig.Log, 4 * ArpPack.Bar + 24000, ArpPack.PlainPatch());
                int clicks = ClickDetector.Find(l).Count + ClickDetector.Find(r).Count;
                Assert.That(clicks, Is.EqualTo(0), $"{name}: clicks");
            }
        }

        [Test]
        public void TheFactoryPresetsNameRealParametersAndNoBrands()
        {
            var names = ArpPack.Presets().Select(p => p.Name).ToList();
            Assert.That(names.Count, Is.InRange(6, 8));
            Assert.That(names.Distinct().Count(), Is.EqualTo(names.Count));
            foreach (var want in new[] { "Straight 16ths Up", "Up-Down Two Octaves", "Triplet Cascade", "Random Sparkle", "Swing Eighths", "Gated Chord Pulse", "Step Bassline", "Slow Converge" })
                Assert.That(names.Any(n => n.EndsWith(want)), Is.True, want);
        }
    }
}
