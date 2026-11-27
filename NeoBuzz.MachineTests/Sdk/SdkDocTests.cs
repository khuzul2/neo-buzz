using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NeoBuzz.Dsp;
using NeoBuzz.TutorialTremolo;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The machine SDK's documentation (docs/sdk, SDK1) is checked like code.
    ///  - The tutorial machine, Sdk/TutorialTremolo, is real code compiled with these tests: its engine is tested here (Mix 0 % and Amount 0 % are
    ///    bit-exact dry, 0 bytes per block, its state round-trips, its presets are audible and different).
    ///  - Every fenced code block of docs/sdk/tutorial.md is preceded by <c>&lt;!-- snippet: Name --&gt;</c> and equals the region Name of a file
    ///    under Sdk/, byte for byte after the line endings are normalised. A region is the lines between <c>// snippet Name</c> and
    ///    <c>// end snippet Name</c> (<c>#</c> instead of <c>//</c> in .def, .txt and .py files); the marker's own indentation is taken off every
    ///    line. A region may follow a line <c>// from: File</c> (path under Sdk/TutorialTremolo): it is an excerpt of a generated file, and must
    ///    appear in that file, line by line. A marked block on any other page is checked the same way.
    ///  - Every relative link in docs/sdk/*.md names a file that exists and, with a #anchor, a heading that exists; README links every page.
    /// </summary>
    [TestFixture]
    public class SdkDocTests
    {
        // ------------------------------------------------------------------------------------------------ where things are

        static string SdkDir([CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;
        static string Fixture => Path.Combine(SdkDir(), "TutorialTremolo");
        static string DocsDir => Path.GetFullPath(Path.Combine(SdkDir(), "..", "..", "docs", "sdk"));
        static string RepoRoot => Path.GetFullPath(Path.Combine(SdkDir(), "..", ".."));

        static string[] Pages() => Directory.GetFiles(DocsDir, "*.md").OrderBy(f => f, StringComparer.Ordinal).ToArray();

        static string Normalise(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

        // ------------------------------------------------------------------------------------------------ regions of the fixture

        sealed record Region(string Name, string File, string Text, string From);

        static readonly Regex StartMarker = new(@"^(?<indent>\s*)(?://|#)\s*snippet\s+(?<name>\w+)\s*$");
        static readonly Regex EndMarker = new(@"^\s*(?://|#)\s*end snippet\s+(?<name>\w+)\s*$");
        static readonly Regex FromMarker = new(@"^\s*(?://|#)\s*from:\s*(?<file>\S+)\s*$");

        /// <summary>Every region under Sdk/ (the folder of this file), by name. A name is used once.</summary>
        static Dictionary<string, Region> Regions()
        {
            var found = new Dictionary<string, Region>();
            foreach (var file in Directory.GetFiles(SdkDir(), "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(SdkDir(), file).Replace('\\', '/');
                if (rel.StartsWith("TutorialTremolo/obj/") || rel.StartsWith("TutorialTremolo/bin/")) continue;
                if (Path.GetExtension(file) is not (".cs" or ".def" or ".txt" or ".py")) continue;
                var lines = Normalise(File.ReadAllText(file)).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var m = StartMarker.Match(lines[i]);
                    if (!m.Success) continue;
                    string name = m.Groups["name"].Value, indent = m.Groups["indent"].Value;
                    string from = i > 0 && FromMarker.Match(lines[i - 1]) is { Success: true } f ? f.Groups["file"].Value : null;
                    var body = new List<string>();
                    int j = i + 1;
                    for (; j < lines.Length; j++)
                    {
                        var e = EndMarker.Match(lines[j]);
                        if (e.Success && e.Groups["name"].Value == name) break;
                        body.Add(lines[j].StartsWith(indent, StringComparison.Ordinal) ? lines[j][indent.Length..] : lines[j]);
                    }
                    if (j == lines.Length) throw new InvalidOperationException($"{rel}: snippet {name} has no end");
                    if (!found.TryAdd(name, new Region(name, rel, string.Join("\n", body).TrimEnd('\n', ' '), from)))
                        throw new InvalidOperationException($"snippet {name} is in {found[name].File} and in {rel}");
                    i = j;
                }
            }
            return found;
        }

        // ------------------------------------------------------------------------------------------------ the fenced blocks of a page

        sealed record Block(string Page, int Line, string Marker, string Language, string Text);

        static readonly Regex MarkerLine = new(@"^<!-- snippet: (?<name>\w+) -->$");

        static List<Block> Blocks(string page)
        {
            var lines = Normalise(File.ReadAllText(page)).Split('\n');
            var blocks = new List<Block>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("```")) continue;
                string lang = lines[i][3..].Trim();
                var body = new List<string>();
                int j = i + 1;
                for (; j < lines.Length && !lines[j].StartsWith("```"); j++) body.Add(lines[j]);
                if (j == lines.Length) throw new InvalidOperationException($"{Path.GetFileName(page)}:{i + 1}: a code block is never closed");
                string marker = i > 0 && MarkerLine.Match(lines[i - 1]) is { Success: true } m ? m.Groups["name"].Value : null;
                blocks.Add(new Block(Path.GetFileName(page), i + 1, marker, lang, string.Join("\n", body).TrimEnd('\n', ' ')));
                i = j;
            }
            return blocks;
        }

        static IEnumerable<TestCaseData> MarkedBlocks()
        {
            foreach (var page in Pages())
                foreach (var b in Blocks(page).Where(b => b.Marker != null))
                    yield return new TestCaseData(b.Page, b.Line, b.Marker).SetName($"{b.Page}:{b.Line} {b.Marker}");
        }

        static string FirstDifference(string want, string got)
        {
            var a = want.Split('\n');
            var b = got.Split('\n');
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                string x = i < a.Length ? a[i] : "(no line)", y = i < b.Length ? b[i] : "(no line)";
                if (x != y) return $"line {i + 1} of the block\n  source: {x}\n  page:   {y}";
            }
            return "";
        }

        [TestCaseSource(nameof(MarkedBlocks))]
        public void AMarkedBlockEqualsItsRegion(string page, int line, string marker)
        {
            var regions = Regions();
            Assert.That(regions.ContainsKey(marker), Is.True, $"{page}:{line}: no region named {marker} under NeoBuzz.MachineTests/Sdk");
            var block = Blocks(Path.Combine(DocsDir, page)).Single(b => b.Line == line);
            var region = regions[marker];
            Assert.That(block.Text, Is.EqualTo(region.Text),
                $"{page}:{line}: the block does not equal the region {marker} of {region.File}: " + FirstDifference(region.Text, block.Text));
        }

        [Test]
        public void EveryCodeBlockOfTheTutorialIsMarked()
        {
            var unmarked = Blocks(Path.Combine(DocsDir, "tutorial.md")).Where(b => b.Marker == null).Select(b => $"tutorial.md:{b.Line}").ToList();
            Assert.That(unmarked, Is.Empty, "a block with no <!-- snippet: Name --> on the line before its fence");
        }

        [Test]
        public void EveryRegionTheTutorialQuotesIsFromARealFile()
        {
            // An excerpt of a generated file ("from:") appears in that file, line by line (indentation aside).
            foreach (var region in Regions().Values.Where(r => r.From != null))
            {
                string path = Path.Combine(Fixture, region.From);
                Assert.That(File.Exists(path), Is.True, $"{region.Name}: from {region.From}, which does not exist");
                var file = Normalise(File.ReadAllText(path)).Split('\n').Select(l => l.Trim()).ToArray();
                var want = region.Text.Split('\n').Select(l => l.Trim()).ToArray();
                bool found = false;
                for (int i = 0; i + want.Length <= file.Length && !found; i++)
                    found = file.Skip(i).Take(want.Length).SequenceEqual(want);
                Assert.That(found, Is.True, $"{region.Name}: not found line by line in {region.From}:\n{region.Text}");
            }
        }

        // ------------------------------------------------------------------------------------------------ links

        static string Slug(string heading)
        {
            // GitHub's anchors: lower case, spaces to hyphens, everything but letters, digits, hyphens and underscores removed.
            var sb = new StringBuilder();
            foreach (char c in heading.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('-');
            }
            return sb.ToString();
        }

        static HashSet<string> Anchors(string file)
        {
            var anchors = new HashSet<string>();
            var seen = new Dictionary<string, int>();
            bool fence = false;
            foreach (var line in Normalise(File.ReadAllText(file)).Split('\n'))
            {
                if (line.StartsWith("```")) fence = !fence;
                if (fence || !line.StartsWith("#")) continue;
                string text = Regex.Replace(line.TrimStart('#').Trim(), @"[`*]", "");
                string slug = Slug(Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1"));
                anchors.Add(seen.TryGetValue(slug, out int n) ? $"{slug}-{n}" : slug);
                seen[slug] = seen.TryGetValue(slug, out int k) ? k + 1 : 1;
            }
            return anchors;
        }

        static IEnumerable<(string Page, int Line, string Target)> Links()
        {
            foreach (var page in Pages())
            {
                bool fence = false;
                var lines = Normalise(File.ReadAllText(page)).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("```")) fence = !fence;
                    if (fence) continue;
                    foreach (Match m in Regex.Matches(Regex.Replace(lines[i], @"`[^`]*`", ""), @"\[[^\]]*\]\((?<t>[^)\s]+)\)"))
                        yield return (page, i + 1, m.Groups["t"].Value);
                }
            }
        }

        [Test]
        public void EveryRelativeLinkResolvesToAFileAndAHeading()
        {
            var problems = new List<string>();
            int checkedLinks = 0;
            foreach (var (page, line, target) in Links())
            {
                if (Regex.IsMatch(target, @"^[a-z][a-z0-9+.-]*:")) continue;     // http:, https:, mailto:
                checkedLinks++;
                string path = target, anchor = null;
                int hash = target.IndexOf('#');
                if (hash >= 0) { path = target[..hash]; anchor = target[(hash + 1)..]; }
                string file = path.Length == 0 ? page : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, path));
                string where = $"{Path.GetFileName(page)}:{line} ({target})";
                if (!File.Exists(file) && !Directory.Exists(file)) { problems.Add(where + ": no such file"); continue; }
                if (anchor == null) continue;
                if (!file.EndsWith(".md") || !Anchors(file).Contains(anchor)) problems.Add(where + ": no such heading");
            }
            Assert.That(checkedLinks, Is.GreaterThan(30), "the pages are linked to the code and to each other");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [Test]
        public void TheReadmeLinksEveryPage()
        {
            var readme = Path.Combine(DocsDir, "README.md");
            var linked = Links().Where(l => l.Page == readme).Select(l => l.Target.Split('#')[0]).ToHashSet();
            var missing = Pages().Select(Path.GetFileName).Where(p => p != "README.md" && !linked.Contains(p)).ToList();
            Assert.That(missing, Is.Empty, "pages README.md does not link");
            Assert.That(Pages().Select(Path.GetFileName), Is.SupersetOf(new[]
            {
                "README.md", "tutorial.md", "params.md", "editor.md", "notes.md", "state.md", "audio-thread.md", "export.md", "rules.md",
            }));
        }

        [Test]
        public void TheSdkIsLinkedFromTheMachinesSpecAndFork()
        {
            foreach (var file in new[] { "docs/specs/machines.md", "FORK.md" })
                Assert.That(File.ReadAllText(Path.Combine(RepoRoot, file)).Contains("docs/sdk/README.md"), Is.True, file + " links docs/sdk/README.md");
        }

        // ------------------------------------------------------------------------------------------------ the tutorial machine

        // snippet TestHelpers
        static ParamValues Values(Action<ParamValues> set = null)
        {
            var v = new ParamValues(Params.Table);
            set?.Invoke(v);
            return v;
        }

        /// <summary>Sets a parameter in its real unit (Hz, %, degrees): the table maps it to the raw value ReBuzz stores.</summary>
        static void Set(ParamValues v, int index, double physical) => v.Set(index, v.Table[index].Raw(physical));

        /// <summary>The engine over the material in blocks of 256, as the machine's Work calls it. Values are set before the engine is made.</summary>
        static (float[] L, float[] R) Render(ParamValues v, float[] inL, float[] inR, Action<int> beforeBlock = null)
        {
            var engine = new TremoloEngine(v);
            engine.SetSampleRate(Measure.Rate);
            var l = (float[])inL.Clone();
            var r = (float[])inR.Clone();
            var bl = new float[TremoloEngine.MaxBlock];
            var br = new float[TremoloEngine.MaxBlock];
            for (int pos = 0, b = 0; pos < l.Length; pos += TremoloEngine.MaxBlock, b++)
            {
                beforeBlock?.Invoke(b);
                int n = Math.Min(TremoloEngine.MaxBlock, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                engine.Process(bl, br, n);
                Array.Copy(bl, 0, l, pos, n);
                Array.Copy(br, 0, r, pos, n);
            }
            return (l, r);
        }

        static void AssertSameBits(float[] want, float[] got, string what)
        {
            for (int i = 0; i < want.Length; i++)
                if (BitConverter.SingleToInt32Bits(want[i]) != BitConverter.SingleToInt32Bits(got[i]))
                    Assert.Fail($"{what}: sample {i} is {got[i]:R}, the input was {want[i]:R}");
        }

        static (float[] L, float[] R) Material() => (FxGoldenTests.DrumLoop(), FxGoldenTests.Chord());
        // end snippet TestHelpers

        // snippet TestBitExact
        [Test]
        public void AmountZeroIsTheInputBitForBit()
        {
            var v = Values(x => Set(x, P.Amount, 0));        // 0 %: the effect is out of the path
            var (inL, inR) = Material();
            var (l, r) = Render(v, inL, inR);
            AssertSameBits(inL, l, "left");
            AssertSameBits(inR, r, "right");
        }

        [Test]
        public void MixZeroIsTheInputBitForBit()
        {
            var v = Values(x => Set(x, P.Mix, 0));           // 0 %: all dry
            var (inL, inR) = Material();
            var (l, r) = Render(v, inL, inR);
            AssertSameBits(inL, l, "left");
            AssertSameBits(inR, r, "right");
        }
        // end snippet TestBitExact

        [Test]
        public void TheDefaultsAreAnEffectAtOnce()
        {
            // Mix 100 %, Amount 100 %: the tremolo as set. The level really moves (5 Hz, 50 % deep: the quietest 5 ms are at least 5 dB under the loudest).
            var v = Values();
            var tone = Measure.Sine(1000, Measure.Rate * 2, 0.5);
            var (l, _) = Render(v, tone, tone);
            double lo = double.MaxValue, hi = 0;
            for (int pos = 4800; pos + 240 <= l.Length; pos += 240)
            {
                double rms = Measure.Rms(l, pos, 240);
                lo = Math.Min(lo, rms);
                hi = Math.Max(hi, rms);
            }
            Assert.That(Measure.Db(hi / lo), Is.GreaterThan(5).And.LessThan(8), "50 % depth is a 6 dB swing");
        }

        [Test]
        public void AmountGrowsTheEffect()
        {
            var tone = Measure.Sine(1000, Measure.Rate * 2, 0.5);
            double Swing(double amount)
            {
                var (l, _) = Render(Values(x => Set(x, P.Amount, amount)), tone, tone);
                double lo = double.MaxValue, hi = 0;
                for (int pos = 4800; pos + 240 <= l.Length; pos += 240)
                {
                    double rms = Measure.Rms(l, pos, 240);
                    lo = Math.Min(lo, rms);
                    hi = Math.Max(hi, rms);
                }
                return Measure.Db(hi / Math.Max(lo, 1e-9));
            }
            double s50 = Swing(50), s100 = Swing(100), s150 = Swing(150);
            Assert.That(s50, Is.LessThan(s100));
            Assert.That(s100, Is.LessThan(s150));
        }

        // snippet TestAllocation
        [Test]
        public void ProcessAllocatesNothingWhileEveryParameterMoves()
        {
            var v = Values();
            var engine = new TremoloEngine(v);
            engine.SetSampleRate(Measure.Rate);
            var (inL, inR) = Material();
            var l = new float[TremoloEngine.MaxBlock];
            var r = new float[TremoloEngine.MaxBlock];
            int blocks = inL.Length / TremoloEngine.MaxBlock - 1;
            void Block(int b)
            {
                // Automation moves every parameter every block.
                v.Set(P.Rate, (b * 37) % 1001);
                v.Set(P.Depth, (b * 53) % 1001);
                v.Set(P.Shape, b / 7 % 2);
                v.Set(P.Phase, (b * 71) % 1001);
                v.Set(P.Mix, (b * 29) % 1001);
                v.Set(P.Amount, (b * 83) % 2001);
                Array.Copy(inL, b * TremoloEngine.MaxBlock, l, 0, TremoloEngine.MaxBlock);
                Array.Copy(inR, b * TremoloEngine.MaxBlock, r, 0, TremoloEngine.MaxBlock);
                engine.Process(l, r, TremoloEngine.MaxBlock);
            }
            var (bytes, why) = AllocationCheck.Run(blocks, Block);
            Assert.That(bytes, Is.EqualTo(0), why);
        }
        // end snippet TestAllocation

        // snippet TestNoClick
        [Test]
        public void MovingTheControlsMakesNoClick()
        {
            // A steady tone through the tremolo while Depth, Phase, Shape, Mix and Amount are all moved every few blocks:
            // the click detector (second difference against its local mean) finds nothing.
            var v = Values();
            var tone = Measure.Sine(1000, Measure.Rate * 4, 0.4);
            var (l, _) = Render(v, tone, tone, b =>
            {
                v.Set(P.Depth, (b * 211) % 1001);
                v.Set(P.Shape, b / 9 % 2);
                v.Set(P.Phase, (b * 97) % 1001);
                v.Set(P.Mix, 500 + (b * 131) % 501);
                v.Set(P.Amount, 800 + (b * 17) % 800);
            });
            Assert.That(ClickDetector.Find(l, floor: 0.02), Is.Empty);
        }
        // end snippet TestNoClick

        // snippet TestState
        [Test]
        public void StateRoundTrips()
        {
            // A song stores each parameter's raw number. Value and Raw are inverses over the whole range, so what a song stores
            // loads as the same sound; and a machine with no migration steps (version 0) loads the numbers it saved unchanged.
            foreach (var def in Params.Table)
                for (int raw = def.Min; raw <= def.Max; raw++)
                    Assert.That(def.Raw(def.Value(raw)), Is.EqualTo(raw), $"{def.Id} at {raw}");

            var saved = Params.Table.Select(d => d.Default).ToArray();
            saved[P.Rate] = 321; saved[P.Depth] = 777; saved[P.Shape] = 1; saved[P.Phase] = 250; saved[P.Mix] = 640; saved[P.Amount] = 1500;
            var chain = new StateMigrations(Params.Table);
            Assert.That(chain.Validate(), Is.Empty);
            Assert.That(chain.Current, Is.EqualTo(0));
            Assert.That(chain.MigrateGlobals(0, saved), Is.EqualTo(saved));
            Assert.That(chain.Keys, Is.EqualTo(new[] { "Rate", "Depth", "Shape", "Phase", "Mix", "Amount" }));
        }
        // end snippet TestState

        [Test]
        public void MixAndAmountAreTheLastParametersAndDefaultTo100Percent()
        {
            var t = Params.Table;
            Assert.That(t[^2].Id, Is.EqualTo("Mix"));
            Assert.That(t[^1].Id, Is.EqualTo("Amount"));
            Assert.That(t[^2].Value(t[^2].Default), Is.EqualTo(1.0));
            Assert.That(t[^1].Value(t[^1].Default), Is.EqualTo(100.0));
            Assert.That(t[^1].Value(t[^1].Max), Is.EqualTo(200.0));
        }

        // snippet TestPresets
        /// <summary>The factory presets as the generator wrote them: name and raw values by index.</summary>
        static List<(string Name, int[] Raw)> FactoryPresets()
        {
            var doc = XDocument.Load(Path.Combine(Fixture, "Tutorial Tremolo.prs.xml"));
            return doc.Root!.Elements("Item").Select(item => (
                (string)item.Attribute("Key")!,
                item.Element("Preset")!.Element("Parameters")!.Elements("Parameter").OrderBy(p => (int)p.Attribute("Index")!)
                    .Select(p => (int)p.Attribute("Value")!).ToArray())).ToList();
        }

        [Test]
        public void PresetsAreClearlyAudibleAndClearlyDifferent()
        {
            var presets = FactoryPresets();
            Assert.That(presets.Count, Is.GreaterThanOrEqualTo(4));
            var tone = Measure.Sine(220, Measure.Rate * 4, 0.4);
            var outputs = new List<float[]>();
            foreach (var (name, raw) in presets)
            {
                var v = Values(x => { for (int i = 0; i < raw.Length; i++) x.Set(i, raw[i]); });
                var (l, _) = Render(v, tone, tone);
                outputs.Add(l);
                // Audible: the output is not the input (at least 3 dB of level moving), at full-scale-ish material.
                double lo = double.MaxValue, hi = 0;
                for (int pos = 9600; pos + 480 <= l.Length; pos += 480)
                {
                    double rms = Measure.Rms(l, pos, 480);
                    lo = Math.Min(lo, rms);
                    hi = Math.Max(hi, rms);
                }
                Assert.That(Measure.Db(hi / Math.Max(lo, 1e-9)), Is.GreaterThan(3), name + " is subtle");
            }
            for (int a = 0; a < outputs.Count; a++)
                for (int b = a + 1; b < outputs.Count; b++)
                {
                    double diff = 0, energy = 0;
                    for (int i = 0; i < tone.Length; i++)
                    {
                        diff += Math.Pow(outputs[a][i] - outputs[b][i], 2);
                        energy += Math.Pow(outputs[a][i], 2);
                    }
                    Assert.That(Measure.Db(Math.Sqrt(diff / energy)), Is.GreaterThan(-12), $"{presets[a].Name} and {presets[b].Name} sound alike");
                }
        }
        // end snippet TestPresets

        [Test]
        public void TheGeneratedFilesAreTheOnesTheDefinitionMakes()
        {
            // The fixture's files come from regenerate.py; this catches a Params.def edited without regenerating (the shape of the table).
            var def = File.ReadAllLines(Path.Combine(Fixture, "Params.def"))
                .Where(l => Regex.IsMatch(l, @"^\w+\s+""[^""]+""\s+\d+\s+\d+\s+\d+\s+\w+")).ToList();
            Assert.That(Params.Table.Length, Is.EqualTo(def.Count));
            for (int i = 0; i < def.Count; i++)
            {
                var m = Regex.Match(def[i], @"^(?<id>\w+)\s+""(?<name>[^""]+)""\s+(?<min>\d+)\s+(?<max>\d+)\s+(?<def>\d+)");
                var d = Params.Table[i];
                Assert.That((d.Id, d.Name, d.Min, d.Max, d.Default), Is.EqualTo((m.Groups["id"].Value, m.Groups["name"].Value,
                    int.Parse(m.Groups["min"].Value), int.Parse(m.Groups["max"].Value), int.Parse(m.Groups["def"].Value))));
            }
            string manifest = File.ReadAllText(Path.Combine(Fixture, "Tutorial Tremolo.machine.json"));
            foreach (var d in Params.Table) Assert.That(manifest, Does.Contain($"\"id\": \"{d.Id}\""));
        }

        // ------------------------------------------------------------------------------------------------ the shared entries of step 9

        [TestCase("StudioProject", "Studio", "NeoStudio")]
        [TestCase("PortableProject", "Portable", "NeoPortable")]
        public void TheProjectFilesOfTheTutorialAreAnExistingMachinesWithTheNamesChanged(string region, string flavour, string targets)
        {
            // Step 9 shows the two project files a machine copies from a neighbour: they are Neo Utility's, with its name and folder replaced.
            string neighbour = Normalise(File.ReadAllText(Path.Combine(RepoRoot, "Machines", flavour, "NeoUtility", $"NeoUtility.{flavour}.csproj"))).TrimEnd('\n', ' ');
            string expected = neighbour.Replace("NeoUtility", "NeoTremolo").Replace("Neo Utility", "Neo Tremolo");
            Assert.That(Regions()[region].Text, Is.EqualTo(expected), "regenerate the region from " + $"Machines/{flavour}/NeoUtility");
            Assert.That(expected, Does.Contain($"../{targets}.targets"));
        }

        [Test]
        public void TheCatalogEntryOfTheTutorialIsValidAgainstTheRealCatalog()
        {
            // The entry step 9 adds to machine-catalog.json uses the catalogue's own categories and tags (tools/machine_catalog.py reports the same problems).
            using var catalog = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "Machines", "Manifests", "machine-catalog.json")));
            using var entry = System.Text.Json.JsonDocument.Parse(Regions()["Catalog"].Text);
            var categories = catalog.RootElement.GetProperty("categories").EnumerateArray().Select(e => e.GetString()).ToList();
            var tags = catalog.RootElement.GetProperty("tags").EnumerateObject().Select(p => p.Name).ToHashSet();
            string category = entry.RootElement.GetProperty("category").GetString();
            Assert.That(categories, Does.Contain(category), "category");
            var mine = entry.RootElement.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(mine, Is.Not.Empty);
            Assert.That(mine.Distinct().Count(), Is.EqualTo(mine.Count), "a tag twice");
            foreach (var tag in mine) Assert.That(tags.Contains(tag), Is.True, $"tag {tag} is not in the vocabulary");
            string useFor = entry.RootElement.GetProperty("use_for").GetString();
            Assert.That(string.IsNullOrWhiteSpace(useFor) || useFor.Contains('\n'), Is.False, "use_for is one non-empty line");
            // The generated manifest carries the same three fields.
            string manifest = File.ReadAllText(Path.Combine(Fixture, "Tutorial Tremolo.machine.json"));
            Assert.That(manifest.Contains($"\"category\": \"{category}\""), Is.True);
            Assert.That(manifest.Contains(useFor), Is.True);
        }
    }
}
