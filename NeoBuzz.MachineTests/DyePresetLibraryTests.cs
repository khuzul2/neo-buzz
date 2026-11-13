using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Dye Essence P4c (docs/specs/dye-essence.md section 7): the factory library of 300 or more presets. What the script
    /// (tools/dye-preset-gen.py) makes and the hand-made ones are held to the same rules: named "Category — Name", spread over the
    /// engines, both layouts and the rack's twelve types, no two alike (parameter-space distance, else rendered spectrum), the
    /// generated ones levelled to -22 dB (loudest 50 ms RMS of the chord, the level tool's measure), in tune and not clipping.
    /// </summary>
    [TestFixture]
    public class DyePresetLibraryTests
    {
        static string PresetFile([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Machines", "DyeEssence", "Dye Essence.prs.xml"));

        sealed class Entry
        {
            public string Name, Comment, Category;
            public Dictionary<int, int> Values;
            public XElement State;
            /// <summary>Written by tools/dye-preset-gen.py (it gives every preset a comment; the hand-made ones have none).</summary>
            public bool Generated => Comment.Length > 0;
        }

        static readonly ParamDef[] Table = Params.Table;
        static int Index(string name) => Array.FindIndex(Table, d => d.Name == name);

        static readonly Lazy<List<Entry>> Library = new(() =>
        {
            var list = new List<Entry>();
            foreach (var item in XDocument.Load(PresetFile()).Root!.Elements("Item"))
            {
                string name = (string)item.Attribute("Key")!;
                var preset = item.Element("Preset")!;
                var values = new Dictionary<int, int>();
                foreach (var p in preset.Element("Parameters")!.Elements("Parameter")) values[(int)p.Attribute("Index")!] = (int)p.Attribute("Value")!;
                int cut = name.IndexOf(" — ", StringComparison.Ordinal);
                list.Add(new Entry
                {
                    Name = name,
                    Comment = (string)preset.Element("Comment") ?? "",
                    Category = cut > 0 ? name.Substring(0, cut) : "",
                    Values = values,
                    State = PresetTests.PresetState(PresetFile(), name),
                });
            }
            return list;
        });

        /// <summary>Categories of the library (the preset bar's submenus): the twelve groups of the spec, Arp/Seq and Texture/FX as two each.</summary>
        static readonly string[] LibraryCategories = { "Bass", "Lead", "Pad", "Keys", "Pluck", "Bell", "Arp", "Seq", "Texture", "FX", "Drum", "Vocal", "Cinematic", "Lo-Fi" };
        static readonly string[] AllCategories = LibraryCategories.Concat(new[] { "Init", "MPE", "Rack" }).ToArray();

        /// <summary>No product, maker, artist or trademark in a preset name or comment (docs/specs/dye-essence.md section 10).</summary>
        static readonly string[] Forbidden =
        {
            "roland", "yamaha", "moog", "korg", "oberheim", "sequential", "prophet", "juno", "jupiter", "minimoog", "dx7", "tb-303", "tb303", "tr-808", "tr808",
            "808", "909", "303", "rhodes", "wurlitzer", "hammond", "mellotron", "fairlight", "synclavier", "ppg", "waldorf", "serum", "massive", "omnisphere",
            "vital", "diva", "zimmer", "vangelis", "reese", "supersaw", "super saw", "hoover", "clavinet", "arp 2600", "cs-80", "cs80", "ob-xa", "sh-101", "sh101",
            "linn", "emu", "ensoniq", "casio", "novation", "arturia", "native instruments", "ableton", "fm8", "kontakt", "reaktor",
        };

        // ---- structure ----

        [Test]
        public void TheLibraryHasThreeHundredNamedPresets()
        {
            var all = Library.Value;
            TestContext.Out.WriteLine($"{all.Count} presets");
            Assert.That(all.Count, Is.GreaterThanOrEqualTo(300));
            Assert.That(all.Select(e => e.Name).Distinct().Count(), Is.EqualTo(all.Count), "unique names");
            foreach (var e in all)
            {
                Assert.That(e.Name, Does.Match(@"^[^—]+ — \S.*$"), $"{e.Name}: \"Category — Name\"");
                Assert.That(e.Name.Length, Is.LessThanOrEqualTo(44), $"{e.Name}: fits the preset bar");
                Assert.That(AllCategories, Does.Contain(e.Category), $"{e.Name}: a known category");
                string text = (e.Name + " " + e.Comment).ToLowerInvariant();
                foreach (string word in Forbidden)
                    Assert.That(Regex.IsMatch(text, @"(^|[^a-z0-9])" + Regex.Escape(word) + @"($|[^a-z0-9])"), Is.False, $"{e.Name}: names \"{word}\"");
            }
            foreach (var c in LibraryCategories)
                Assert.That(all.Count(e => e.Category == c), Is.GreaterThanOrEqualTo(10), $"{c} has presets to choose from");
            Assert.That(all.Where(e => e.Generated).All(e => e.Comment.Length is > 20 and < 400), Is.True, "every generated preset says what it is");
            // The preset bar's submenus: no category needs a scroll of more than a few screens.
            foreach (var g in all.GroupBy(e => e.Category)) Assert.That(g.Count(), Is.LessThanOrEqualTo(40), $"{g.Key}: a submenu of a usable size");
        }

        static readonly string[] EngineNames = { "Off", "Wavetable", "Analog", "Sample", "Granular", "Harmonic", "Modal" };
        static readonly string[] FxNames = { "Off", "EQ", "Filter", "Comp", "Clipper", "Delay", "Verb", "Dimension", "Bitcrusher", "Freq shift", "Pitch delay", "Convolution", "Vocoder" };

        static IEnumerable<string> EnginesOf(Entry e)
        {
            foreach (var s in new[] { "A", "B", "C" })
            {
                int raw = e.Values[Index(s + " engine")];
                if (raw != 0) yield return EngineNames[raw];
            }
        }

        static IEnumerable<string> FxOf(Entry e)
        {
            for (int n = 1; n <= 10; n++)
            {
                int raw = e.Values[Index($"FX{n} type")];
                if (raw != 0) yield return FxNames[raw];
            }
        }

        [Test]
        public void TheLibrarySpreadsEnginesLayoutsAndEffects()
        {
            var all = Library.Value;
            foreach (var engine in EngineNames.Skip(1))
            {
                int n = all.Count(e => EnginesOf(e).Contains(engine));
                TestContext.Out.WriteLine($"engine {engine}: {n}");
                Assert.That(n, Is.GreaterThanOrEqualTo(10), $"engine {engine}");
            }
            foreach (var fx in FxNames.Skip(1))
            {
                int n = all.Count(e => FxOf(e).Contains(fx));
                TestContext.Out.WriteLine($"rack type {fx}: {n}");
                Assert.That(n, Is.GreaterThanOrEqualTo(8), $"rack type {fx}");
            }
            int duo = all.Count(e => e.Values[Index("FX layout")] == 1);
            TestContext.Out.WriteLine($"Duo {duo}, Trio {all.Count - duo}");
            Assert.That(duo, Is.GreaterThanOrEqualTo(40), "Duo presets");
            Assert.That(all.Count - duo, Is.GreaterThanOrEqualTo(100), "Trio presets");
            foreach (var c in LibraryCategories.Except(new[] { "Drum" }))
            {
                var inCat = all.Where(e => e.Category == c).ToList();
                int engines = inCat.SelectMany(e => EnginesOf(e)).Distinct().Count();
                TestContext.Out.WriteLine($"{c}: {inCat.Count} presets, {engines} engines, {inCat.SelectMany(e => FxOf(e)).Distinct().Count()} rack types, Duo {inCat.Count(e => e.Values[Index("FX layout")] == 1)}");
                Assert.That(engines, Is.GreaterThanOrEqualTo(4), $"{c}: a spread of engines");
                Assert.That(inCat.SelectMany(e => FxOf(e)).Distinct().Count(), Is.GreaterThanOrEqualTo(3), $"{c}: a spread of rack types");
            }
            // A Duo preset uses the layout's two engine slots.
            foreach (var e in all.Where(e => e.Values[Index("FX layout")] == 1 && e.Generated))
                Assert.That(e.Values[Index("C engine")], Is.EqualTo(0), $"{e.Name}: Duo has two engine slots");
        }

        // ---- distinct ----

        /// <summary>
        /// The distance of two presets in parameter space (the generator's measure too): a choice (an enum, a switch or a small
        /// integer) that differs counts 1, any other parameter its difference over its range, and each machine-state property
        /// that differs counts 1. The level (Volume) does not count: two presets a gain apart are the same sound.
        /// </summary>
        static double Distance(Entry a, Entry b)
        {
            double d = 0;
            for (int i = 0; i < Table.Length; i++)
            {
                if (Table[i].Name == "Volume") continue;
                int x = a.Values[i], y = b.Values[i];
                if (x == y) continue;
                int span = Table[i].Max - Table[i].Min;
                d += Table[i].Kind is MapKind.Enum or MapKind.Switch || span <= 20 ? 1.0 : Math.Abs(x - y) / (double)span;
            }
            var sa = StateOf(a);
            var sb = StateOf(b);
            foreach (var k in sa.Keys.Union(sb.Keys)) if (sa.GetValueOrDefault(k) != sb.GetValueOrDefault(k)) d += 1;
            return d;
        }

        static Dictionary<string, string> StateOf(Entry e) =>
            e.State?.Elements().Where(x => !string.IsNullOrEmpty(x.Value)).ToDictionary(x => x.Name.LocalName, x => x.Value) ?? new();

        /// <summary>The chord's level in 1/3-octave bands from 63 Hz to 16 kHz, dB (0.1 to 0.43 s, while it is held).</summary>
        static double[] Bands(float[] y)
        {
            var mag = Fft.Magnitudes(y, 4800, 16384);
            var bands = new double[24];
            for (int b = 0; b < bands.Length; b++)
            {
                double lo = 63 * Math.Pow(2, b / 3.0 - 1 / 6.0), hi = 63 * Math.Pow(2, b / 3.0 + 1 / 6.0);
                double e = 1e-12;
                for (int k = (int)(lo * 16384 / Measure.Rate); k <= (int)(hi * 16384 / Measure.Rate) && k < mag.Length; k++) e += mag[k] * mag[k];
                bands[b] = 10 * Math.Log10(e);
            }
            return bands;
        }

        [Test]
        public void NoTwoPresetsAreAlike()
        {
            if (OperatingSystem.IsMacOS()) Assert.Ignore("renders the whole library; the same bits on Linux and Windows (PortableMath), and the Mac job is near its time limit");
            var all = Library.Value;
            const double MinDistance = 1.0, MinSpectrumDb = 1.5;
            var close = new List<(Entry A, Entry B, double D)>();
            double least = double.MaxValue;
            for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++)
                {
                    double d = Distance(all[i], all[j]);
                    least = Math.Min(least, d);
                    if (d < MinDistance) close.Add((all[i], all[j], d));
                }
            TestContext.Out.WriteLine($"{all.Count} presets, the closest pair is {least:F2} apart; {close.Count} pairs under {MinDistance}");
            var alike = new List<string>();
            foreach (var (a, b, d) in close)
            {
                // Close in parameter space: the sounds must still differ, in their spectra.
                var ya = PresetTests.RenderDyePreset(PresetFile(), a.Name, a.Values);
                var yb = PresetTests.RenderDyePreset(PresetFile(), b.Name, b.Values);
                var ba = Bands(ya);
                var bb = Bands(yb);
                double diff = 0;
                for (int k = 0; k < ba.Length; k++) diff += Math.Abs(ba[k] - bb[k]);
                diff /= ba.Length;
                if (diff < MinSpectrumDb) alike.Add($"{a.Name} / {b.Name}: distance {d:F2}, spectra {diff:F2} dB apart");
            }
            Assert.That(alike, Is.Empty, string.Join("\n", alike));
        }

        // ---- levelled, clean, in tune ----

        /// <summary>The chord's loudest 50 ms (the measure of the level tool and of EveryPresetSoundsSensible), the peak and the level of the held end.</summary>
        static (double Level, double Peak, double Held) Measured(Entry e)
        {
            var y = PresetTests.RenderDyePreset(PresetFile(), e.Name, e.Values);
            double loudest = 0;
            for (int at = 0; at + 2400 <= y.Length * 2 / 3; at += 1200) loudest = Math.Max(loudest, Measure.Rms(y, at, 2400));
            return (Measure.Db(loudest), Measure.Db(Measure.Peak(y, 0, y.Length)), Measure.Db(Measure.Rms(y, y.Length * 2 / 3 - 2400, 2400)));
        }

        /// <summary>Writes every Dye preset's level to the file named by DYE_LEVELS (name, loudest 50 ms dB, peak dB), for tools/dye-preset-gen.py --apply-levels.</summary>
        [Test, Explicit("a worker's aid: writes to $DYE_LEVELS")]
        public void MeasureLevels()
        {
            string path = Environment.GetEnvironmentVariable("DYE_LEVELS");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("DYE_LEVELS not set");
            var sb = new System.Text.StringBuilder();
            foreach (var e in Library.Value)
            {
                var (level, peak, held) = Measured(e);
                sb.Append(e.Name).Append('\t').Append(level.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                  .Append(peak.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                  .Append(held.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
        }

        [Test]
        public void GeneratedPresetsAreLevelledToMinus22AndDoNotClip()
        {
            if (OperatingSystem.IsMacOS()) Assert.Ignore("renders the whole library; the same bits on Linux and Windows (PortableMath), and the Mac job is near its time limit");
            var bad = new List<string>();
            int n = 0;
            foreach (var e in Library.Value.Where(e => e.Generated))
            {
                var (level, peak, held) = Measured(e);
                n++;
                if (!double.IsFinite(level) || !double.IsFinite(peak)) { bad.Add($"{e.Name}: not finite"); continue; }
                if (Math.Abs(level + 22.0) > 1.0) bad.Add($"{e.Name}: loudest 50 ms {level:F1} dB (want -22 +- 1)");
                if (peak > -3.0) bad.Add($"{e.Name}: peak {peak:F1} dBFS (clipping margin)");
            }
            TestContext.Out.WriteLine($"{n} generated presets measured, {bad.Count} problems");
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }

        /// <summary>
        /// Pitch: one held A3 (220 Hz) through the preset; where the sound has a clear period (autocorrelation over 0.1 to 0.4 s,
        /// peak above 0.85 of the zero lag) its fundamental sits within 30 cents of 220 Hz times a power of two. Presets that play
        /// other notes by themselves (arpeggiator, sequencer), drums, noise and comb textures have no pitch to test.
        /// </summary>
        [Test]
        public void PitchedPresetsAreInTune()
        {
            if (OperatingSystem.IsMacOS()) Assert.Ignore("renders the whole library; the same bits on Linux and Windows (PortableMath), and the Mac job is near its time limit");
            var skip = new HashSet<string> { "Arp", "Seq", "Drum", "Texture", "FX", "Init", "MPE" };
            var bad = new List<string>();
            int tested = 0;
            foreach (var e in Library.Value.Where(e => e.Generated && !skip.Contains(e.Category)))
            {
                if (e.Values[Index("Arp mode")] != 0 || e.Values[Index("Seq mode")] != 0) continue;
                var y = PresetTests.RenderDyePreset(PresetFile(), e.Name, e.Values, new[] { 57 });
                int start = 4800, n = 14400;
                double e0 = 0;
                for (int i = 0; i < n; i++) e0 += (double)y[start + i] * y[start + i];
                if (e0 < 1e-9) continue;
                if (e.Name.Contains("Sample Bell")) continue;   // the bell sample is inharmonic and not tuned to a note
                int minLag = 24, maxLag = 1100;   // 2 kHz down to 44 Hz
                double best = 0; int bestLag = 0;
                var acf = new double[maxLag + 2];
                for (int lag = minLag; lag <= maxLag + 1; lag++)
                {
                    double s = 0;
                    for (int i = 0; i < n - maxLag - 2; i++) s += (double)y[start + i] * y[start + i + lag];
                    acf[lag] = s;
                }
                double zero = 0;
                for (int i = 0; i < n - maxLag - 2; i++) zero += (double)y[start + i] * y[start + i];
                for (int lag = minLag + 1; lag <= maxLag; lag++)
                    if (acf[lag] > acf[lag - 1] && acf[lag] >= acf[lag + 1] && acf[lag] / zero > best) { best = acf[lag] / zero; bestLag = lag; }
                // The first peak above 0.85 of the highest is the period (not a multiple of it).
                int period = 0;
                for (int lag = minLag + 1; lag <= maxLag; lag++)
                    if (acf[lag] > acf[lag - 1] && acf[lag] >= acf[lag + 1] && acf[lag] / zero >= 0.85 * best) { period = lag; break; }
                if (best < 0.85 || period == 0) continue;
                double a = acf[period - 1], b = acf[period], c = acf[period + 1];
                double shift = 0.5 * (a - c) / (a - 2 * b + c);
                double f0 = Measure.Rate / (period + shift);
                double cents = 1200 * Math.Log2(f0 / 220.0);
                double fold = cents - 1200 * Math.Round(cents / 1200);
                tested++;
                TestContext.Out.WriteLine($"{e.Name}: {f0:F1} Hz, {fold:+0;-0} ct from an octave of A3 (clarity {best:F2})");
                if (Math.Abs(fold) > 30) bad.Add($"{e.Name}: {f0:F1} Hz is {fold:+0;-0} cents from an A");
            }
            TestContext.Out.WriteLine($"{tested} presets with a clear pitch tested");
            Assert.That(tested, Is.GreaterThanOrEqualTo(50), "enough presets have a measurable pitch");
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }
    }
}
