// Fork (NeoBuzz Live): no update check. Upstream's (ReBuzzGUI/BuzzGUI.BuzzUpdate) polls ReBuzz's latest release on
// GitHub and offers upstream's installer, which would replace NeoBuzz Live. In the sources, where every CI job sees
// them and an upstream merge that brings the check back fails here: no project references BuzzGUI.BuzzUpdate, no code
// outside it calls it or names upstream's releases, the setting that turned it on is gone, and the project itself
// stays off (UpdateService.Enabled) should anything call it.
using System.Text.RegularExpressions;

namespace ReBuzz.AgentTests
{
    [TestFixture]
    public class UpdateCheckTests
    {
        static readonly string Sep = Path.DirectorySeparatorChar.ToString();

        static IEnumerable<string> Files(string root, params string[] patterns) =>
            patterns.SelectMany(p => Directory.GetFiles(root, p, SearchOption.AllDirectories))
                .Where(f => !f.Contains(Sep + "obj" + Sep) && !f.Contains(Sep + "bin" + Sep) && !f.Contains(Sep + ".git" + Sep)
                            && !f.Contains(Sep + "3rdParty" + Sep) && !f.Contains(Sep + "ExternalMachines" + Sep));

        static string Updater(string root) => Path.Combine(root, "ReBuzzGUI", "BuzzGUI.BuzzUpdate");

        [Test]
        public void NoProjectReferencesTheUpdater()
        {
            string root = RepoRoot();
            var referencing = Files(root, "*.csproj", "*.props", "*.targets")
                .Where(f => File.ReadAllText(f).Contains("BuzzGUI.BuzzUpdate.csproj"))
                .Select(f => Path.GetRelativePath(root, f));
            Assert.That(referencing, Is.Empty, "BuzzGUI.BuzzUpdate isn't referenced, so it isn't in the apps or their package");
        }

        [Test]
        public void NothingOutsideTheUpdaterCallsItOrNamesUpstreamsReleases()
        {
            string root = RepoRoot();
            var calls = new Regex(@"\bBuzzGUI\.BuzzUpdate\b|\bUpdateService\b|\bnew UpdateWindow\b|\bUpdateWindow\s*\.|\.CheckForUpdates\b");
            var upstreamReleases = new Regex(@"wasteddesign/ReBuzz/releases|api\.github\.com/repos/wasteddesign|ReBuzzSetup_", RegexOptions.IgnoreCase);
            var hits = new List<string>();
            foreach (var file in Files(root, "*.cs", "*.xaml", "*.axaml", "*.cpp", "*.h", "*.ps1", "*.py", "*.sh"))
            {
                if (file.StartsWith(Updater(root) + Sep) || Path.GetFileName(file) == "UpdateCheckTests.cs"
                    || Path.GetFileName(file) == "package-check.py") continue;   // checks the updater is NOT in the package
                bool settings = Path.GetFileName(file) == "GeneralSettings.cs";
                int n = 0;
                foreach (var line in File.ReadLines(file))
                {
                    n++;
                    string code = line.TrimStart();
                    if (code.StartsWith("//") || code.StartsWith("#")) continue;
                    if ((calls.IsMatch(line) && !settings) || upstreamReleases.IsMatch(line))
                        hits.Add($"{Path.GetRelativePath(root, file)}:{n}: {code}");
                }
            }
            Assert.That(hits, Is.Empty, "the update check, or upstream ReBuzz's releases, reached from the apps");
        }

        [Test]
        public void TheSettingThatTurnedItOnIsGoneAndAlwaysFalse()
        {
            var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "ReBuzzGUI", "BuzzGUI.Common", "Settings", "GeneralSettings.cs")).Select(l => l.Trim()).ToList();
            int at = lines.FindIndex(l => l.StartsWith("public bool CheckForUpdates"));
            Assert.That(at, Is.GreaterThan(0), "GeneralSettings keeps the property for code that reads it");
            Assert.That(lines[at], Is.EqualTo("public bool CheckForUpdates { get => false; set { } }"), "always false");
            var above = lines.Take(at).Reverse().TakeWhile(l => l.StartsWith("[") || l.StartsWith("//"));
            Assert.That(above.Where(l => l.StartsWith("[")), Has.None.Contains("BuzzSetting"), "not a setting: not listed, loaded or saved");
            // Settings skips what isn't a [BuzzSetting] (it used to fail on one).
            string settings = File.ReadAllText(Path.Combine(RepoRoot(), "ReBuzzGUI", "BuzzGUI.Common", "Settings", "Settings.cs"));
            Assert.That(settings, Does.Contain("if (decl == null) continue;"));
        }

        [Test]
        public void TheUpdaterItselfStaysOff()
        {
            string updater = Updater(RepoRoot());
            string service = File.ReadAllText(Path.Combine(updater, "UpdateService.cs"));
            Assert.That(service, Does.Contain("public static bool Enabled => false;"));
            Assert.That(Regex.IsMatch(service, @"CheckForUpdates\(IBuzz buzz\)\s*\{\s*if \(!Enabled\)"), Is.True, "the check returns first");
            string window = File.ReadAllText(Path.Combine(updater, "UpdateWindow.xaml.cs"));
            Assert.That(Regex.IsMatch(window, @"DownloadBuildCount\(IBuzz buzz\)\s*\{\s*if \(!UpdateService\.Enabled\) return;"), Is.True,
                "no request to upstream's releases");
            Assert.That(Regex.IsMatch(window, @"public UpdateWindow\(int latestBuild\)\s*\{\s*(//[^\n]*\n\s*)*if \(!UpdateService\.Enabled\) throw"), Is.True,
                "the offer of upstream's installer is never shown");
            Assert.That(Regex.Matches(window, @"new UpdateWindow\(").Count, Is.EqualTo(1), "one place makes the window, behind DownloadBuildCount's guard");
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
