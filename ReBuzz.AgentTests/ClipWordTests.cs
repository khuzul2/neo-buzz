// Fork (CU5, docs/specs/clips.md section 9): what people and agents read says "clip", not "pattern". The few texts
// that keep the word are in tools/clip-rename-allowlist.txt, each with its reason: a product or machine name that a
// song stores (Modern Pattern Editor), a file format's name (PatternXP), a name a saved file or a command id stores
// (view.pattern), a step or arpeggio pattern, a regular expression, the third-party sources we carry.
// Line format: path fragment TAB text fragment TAB reason. A string literal, guide or manifest line that has the word
// is allowed when its file path contains the first and the string contains the second.
using System.Text.RegularExpressions;

namespace ReBuzz.AgentTests
{
    [TestFixture]
    public class ClipWordTests
    {
        static readonly Regex Word = new Regex("pattern", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static string Root()
        {
            string d = TestContext.CurrentContext.TestDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "ReBuzz.sln"))) d = Path.GetDirectoryName(d);
            Assert.That(d, Is.Not.Null, "the repository root");
            return d!;
        }

        static List<(string Path, string Text, string Reason)> Allowlist(string root)
        {
            var list = new List<(string, string, string)>();
            foreach (var raw in File.ReadAllLines(Path.Combine(root, "tools", "clip-rename-allowlist.txt")))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                var p = raw.Split('\t');
                Assert.That(p.Length, Is.EqualTo(3), "path, text, reason: " + raw);
                Assert.That(p[2].Trim(), Is.Not.Empty, "every entry has its reason: " + raw);
                list.Add((p[0], p[1], p[2]));
            }
            return list;
        }

        /// <summary>The user-facing texts that still say pattern, minus the allowlist.</summary>
        static (List<string> Left, HashSet<int> Used, int Total) Scan()
        {
            string root = Root();
            var allow = Allowlist(root);
            var used = new HashSet<int>();
            var left = new List<string>();
            int total = 0;
            foreach (var (file, texts) in TrademarkTests.UserFacingTexts(root))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                foreach (var (line, text) in texts)
                {
                    if (!Word.IsMatch(text)) continue;
                    total++;
                    int hit = allow.FindIndex(a => rel.Contains(a.Path) && text.Contains(a.Text));
                    if (hit >= 0) used.Add(hit);
                    else left.Add($"{rel}:{line}: {text.Trim()[..Math.Min(text.Trim().Length, 140)]}");
                }
            }
            return (left, used, total);
        }

        [Test]
        public void UserFacingTextsSayClipUnlessTheAllowlistGivesAReason()
        {
            var (left, _, total) = Scan();
            TestContext.Out.WriteLine($"{total} texts with the word, {Allowlist(Root()).Count} allowlist entries");
            if (Environment.GetEnvironmentVariable("CLIP_WORD_DUMP") is string dump)
                File.WriteAllLines(dump, left);
            Assert.That(left, Is.Empty, $"{left.Count} texts say pattern for a clip (say clip, or add the text to tools/clip-rename-allowlist.txt with its reason):\n" + string.Join("\n", left));
        }

        [Test]
        public void EveryAllowlistEntryIsStillNeeded()
        {
            var (_, used, _) = Scan();
            var allow = Allowlist(Root());
            var stale = allow.Where((a, i) => !used.Contains(i)).Select(a => a.Path + " | " + a.Text).ToList();
            Assert.That(stale, Is.Empty, "allowlist entries that match nothing (delete them):\n" + string.Join("\n", stale));
        }

        [Test]
        public void NoToolOrArgumentNameSaysPatternExceptAsTheAllowlistSays()
        {
            // The tools' names and argument keys are string literals in ReBuzz/Agent, scanned above; this pins the names themselves.
            string src = string.Join("\n", Directory.GetFiles(Path.Combine(Root(), "ReBuzz", "Agent"), "*.cs").Select(File.ReadAllText));
            Assert.That(Regex.IsMatch(src, "\"pattern_[a-z_]+\""), Is.False, "no pattern_* tool");
            Assert.That(src, Does.Not.Contain("c.String(\"pattern\")"));
            Assert.That(src, Does.Contain("\"clip_create\""));
        }
    }
}
