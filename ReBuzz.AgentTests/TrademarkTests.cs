// Fork (follow-ups): what people and agents read names no other company's product to say what NeoBuzz Live is or
// does: no "Serum-style" tables, no "Live's" legato. The texts say what a thing is in its own words (a wavetable
// WAV in the common 2048-samples-per-frame layout; legato). Specs, FORK.md and code comments may name the
// products a machine was measured against (benchmarks, design notes); people don't see them.
using System.Text;
using System.Text.RegularExpressions;

namespace ReBuzz.AgentTests
{
    [TestFixture]
    public class TrademarkTests
    {
        /// <summary>
        /// Other companies' audio products and the companies, as a name or a description. Words that are also ordinary
        /// English or our own terms go by their capitals or with their maker's name ("Massive", "Vital", "Waves Audio");
        /// Live (Ableton's) where a text means the product: "Live's", "in Live", "Ableton Live", "Live:".
        /// </summary>
        static readonly Regex Names = new Regex(
            @"\b(?:Serum|Xfer|Pigments|Arturia|Ozone|[iI][zZ]otope|Fab[Ff]ilter|Neutron|[sS]oothe2?|oeksound|Ableton|Bitwig|Massive|Vital|Kontakt|Omnisphere|Sylenth1?|" +
            @"Native Instruments|Komplete|Reaktor|u-he|Diva|Spire|Phase Plant|Kilohearts|Valhalla(?:DSP)?|Soundtoys|Waves Audio|Melodyne|Celemony|Auto-Tune|Antares|" +
            @"Logic Pro|FL Studio|Image-Line|Cubase|Steinberg|Nuendo|Reaper|REAPER|Cockos|Pro Tools|Studio One|PreSonus|Reason Studios|Propellerhead|Cakewalk|" +
            @"Spectrasonics|Keyscape|Eventide|Universal Audio|Softube|Plugin Alliance|Slate Digital|Toontrack|Superior Drummer|Addictive Drums|XLN Audio|Sonnox|" +
            @"Novation|Akai|Korg|Roland|Yamaha|Moog)\b" +
            @"|(?<!NeoBuzz )\bLive(?:'s|’s)|\b(?:in|like|as in|than|from|into) Live\b|(?<!NeoBuzz )\bLive:",
            RegexOptions.Compiled);

        /// <summary>
        /// Factual compatibility statements, each for its reason (path fragment, then what the line may say):
        ///  - "Ableton Link" anywhere: the name of the network sync protocol the apps speak (through ReBuzzLink, under
        ///    Link's own licence); a user finds the feature, and the other apps, by it.
        ///  - The control surfaces the Session View drives (BuiltInScripts.cs: their scripts' names, which people pick from,
        ///    and the MIDI port names the devices announce, which the scripts match): Novation Launchpad, Akai APC, Ableton Push 2.
        /// </summary>
        static readonly (string Path, Regex Allowed)[] Allowed =
        {
            ("", new Regex(@"Ableton Link")),
            ("BuiltInScripts.cs", new Regex(@"Novation Launchpad|Akai APC|Ableton Push 2|Novation|Akai|Ableton")),
        };

        /// <summary>
        /// Every user-facing text: both apps' windows, menus, dialogs, tooltips and messages, the machines' editors, About
        /// texts, parameter names and descriptions (C# string literals, XAML), what agents read (guides, prompts, the tools'
        /// texts, machine manifests, successor notes, factory presets), Buzz's machine templates, the README, and what the
        /// packages tell people (build.ps1's README-FIRST and notices, the Mac app's Info.plist and disk image). Comments,
        /// specs, FORK.md and tests aren't read by users; third-party sources (3rdParty, ExternalMachines/source,
        /// LinkHelper's licence notes for Link itself) are theirs.
        /// </summary>
        [Test]
        public void NoOtherCompanysProductNamesInUserFacingText()
        {
            string root = RepoRoot();
            var hits = new List<string>();
            int files = 0;
            foreach (var (file, texts) in UserFacingTexts(root))
            {
                files++;
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                foreach (var (line, text) in texts)
                    foreach (Match m in Names.Matches(text))
                    {
                        if (Allowed.Any(a => rel.Contains(a.Path) && a.Allowed.Matches(text).Any(x => x.Index <= m.Index && m.Index + m.Length <= x.Index + x.Length)))
                            continue;
                        hits.Add($"{rel}:{line}: \"{m.Value}\" in {Clip(text, m.Index)}");
                    }
            }
            TestContext.Out.WriteLine($"{files} files read");
            Assert.That(files, Is.GreaterThan(400), "the user-facing files were found");
            Assert.That(hits, Is.Empty, $"{hits.Count} other companies' product names in user-facing text (say it in our own words; " +
                                        "a factual compatibility statement goes in Allowed, with its reason):\n" + string.Join("\n", hits));
        }

        [Test]
        public void TheGuardFindsANameInEachKindOfText()
        {
            // The reader's own check: a name in a string literal is found, in a comment it isn't; in XAML, Markdown and JSON it is.
            Assert.That(CSharpStrings("var s = \"tables, Serum-style\"; // Serum in a comment").Select(t => t.Text), Is.EqualTo(new[] { "tables, Serum-style" }));
            Assert.That(CSharpStrings("/* \"Serum\" */ var s = @\"a \"\"Live's\"\" b\";").Select(t => t.Text), Is.EqualTo(new[] { "a \"\"Live's\"\" b" }));
            Assert.That(CSharpStrings("var s = $\"{x} in Live\";").Select(t => t.Text), Is.EqualTo(new[] { "{x} in Live" }));
            Assert.That(CSharpStrings("var s = \"https://example.com\"; var t = \"Kontakt\";").Select(t => t.Text), Is.EqualTo(new[] { "https://example.com", "Kontakt" }));
            Assert.That(Names.IsMatch("Live's legato"), Is.True);
            Assert.That(Names.IsMatch("NeoBuzz Live's list"), Is.False);
            Assert.That(Names.IsMatch("Live playing in studio mode"), Is.False);
            Assert.That(Names.IsMatch("Ableton-style"), Is.True);
            Assert.That(Names.IsMatch("a massive kick"), Is.False);
            Assert.That(Names.IsMatch("AbletonLinkConfig.cmake"), Is.False);
        }

        // ---- the texts -----------------------------------------------------------------------------------------------

        static readonly string[] CodeDirs =
        {
            "ReBuzz", "ReBuzz.Studio", "ReBuzz.Host", "ReBuzz.Headless", "ReBuzz.Agent", "ReBuzz.Engine", "ReBuzzGUI", "Machines", "ModernPatternEditor",
        };

        static IEnumerable<(string File, IEnumerable<(int Line, string Text)> Texts)> UserFacingTexts(string root)
        {
            string sep = Path.DirectorySeparatorChar.ToString();
            bool Skip(string f) => f.Contains(sep + "obj" + sep) || f.Contains(sep + "bin" + sep) || f.Contains("Tests" + sep) || f.Contains(".Tests" + sep);
            foreach (var dir in CodeDirs)
            {
                string d = Path.Combine(root, dir);
                if (!Directory.Exists(d)) continue;
                foreach (var file in Directory.GetFiles(d, "*.*", SearchOption.AllDirectories).Where(f => !Skip(f)).OrderBy(f => f, StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(file);
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".cs") yield return (file, CSharpStrings(File.ReadAllText(file)));
                    else if (ext is ".xaml" or ".axaml") yield return (file, Markup(File.ReadAllText(file)));
                    else if (ext == ".md" || name.EndsWith(".machine.json") || name.EndsWith(".successor.json") || name.EndsWith(".presets.txt") || name.EndsWith(".prs.xml") || name == "Params.def")
                        yield return (file, Lines(File.ReadAllText(file)));
                }
            }
            foreach (var file in Directory.GetFiles(Path.Combine(root, "Other", "Templates"), "*.xml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                yield return (file, Lines(File.ReadAllText(file)));
            foreach (var rel in new[] { "README.md", "build.ps1", Path.Combine("tools", "mac-bundle.sh"), Path.Combine("tools", "mac-dmg.sh") })
            {
                string file = Path.Combine(root, rel);
                if (!File.Exists(file)) continue;
                string text = File.ReadAllText(file);
                yield return (file, rel.EndsWith(".md") ? Lines(text) : Lines(text).Where(t => !t.Text.TrimStart().StartsWith("#")));
            }
        }

        static IEnumerable<(int Line, string Text)> Lines(string text) => text.Split('\n').Select((t, i) => (i + 1, t));

        /// <summary>XAML: everything but comments.</summary>
        static IEnumerable<(int Line, string Text)> Markup(string text) =>
            Lines(Regex.Replace(text, "<!--.*?-->", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline));

        /// <summary>The string literals of C# source (regular, verbatim, interpolated, raw), with the line each starts on; not comments.</summary>
        internal static List<(int Line, string Text)> CSharpStrings(string s)
        {
            var found = new List<(int, string)>();
            int line = 1, i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\n') { line++; i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) { if (s[i] == '\n') line++; i++; }
                    i += 2;
                    continue;
                }
                if (c == '\'')
                {
                    // A char literal: '"' mustn't open a string.
                    int j = i + 1;
                    if (j < s.Length && s[j] == '\\') j += 2; else j++;
                    while (j < s.Length && s[j] != '\'' && s[j] != '\n' && j - i < 12) j++;
                    i = j + 1;
                    continue;
                }
                if (c == '"')
                {
                    int start = line;
                    bool verbatim = i > 0 && (s[i - 1] == '@' || (s[i - 1] == '$' && i > 1 && s[i - 2] == '@'));
                    if (i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"')
                    {
                        // A raw string: from """ to the next """.
                        int end = s.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                        if (end < 0) end = s.Length;
                        string body = s.Substring(i + 3, end - i - 3);
                        found.Add((start, body));
                        line += body.Count(x => x == '\n');
                        i = end + 3;
                        continue;
                    }
                    var b = new StringBuilder();
                    i++;
                    while (i < s.Length)
                    {
                        char d = s[i];
                        if (verbatim)
                        {
                            if (d == '"' && i + 1 < s.Length && s[i + 1] == '"') { b.Append("\"\""); i += 2; continue; }
                            if (d == '"') { i++; break; }
                        }
                        else
                        {
                            if (d == '\\' && i + 1 < s.Length) { b.Append(d).Append(s[i + 1]); i += 2; continue; }
                            if (d == '"' || d == '\n') { i++; break; }
                        }
                        if (d == '\n') line++;
                        b.Append(d);
                        i++;
                    }
                    found.Add((start, b.ToString()));
                    continue;
                }
                i++;
            }
            return found;
        }

        static string Clip(string text, int at)
        {
            int from = Math.Max(0, at - 50), to = Math.Min(text.Length, at + 60);
            return (from > 0 ? "…" : "") + text.Substring(from, to - from).Trim() + (to < text.Length ? "…" : "");
        }

        static string RepoRoot()
        {
            string? env = Environment.GetEnvironmentVariable("NEO_BUZZ_ROOT");
            if (!string.IsNullOrEmpty(env)) return env;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "Machines")) && Directory.Exists(Path.Combine(dir.FullName, "ReBuzz.Agent")))
                    return dir.FullName;
            Assert.Inconclusive("repository root not found");
            return "";
        }
    }
}
