using System.IO;
using ReBuzz.Common;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork: the windowed app checks its install before it opens a window (a package still being unzipped lacks Themes, which
    /// the first window reads). Pure file checks on temporary folders, plus the repository's RunTime folder (what the package ships).
    /// </summary>
    public class InstallCheckTests
    {
        static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "installcheck-" + Path.GetRandomFileName())).FullName;

        static void Touch(string root, string relative)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }

        [Test]
        public void APartlyUnzippedPackageIsIncomplete()
        {
            string root = Temp();
            try
            {
                foreach (var d in new[] { "bin32", "bin64", "Gear", "Help" }) Directory.CreateDirectory(Path.Combine(root, d));
                var missing = InstallCheck.Missing(root);
                Assert.That(missing, Has.Count.EqualTo(InstallCheck.Required.Length));
                Assert.That(missing, Does.Contain(@"Themes\Default\ParameterWindowShell.xaml"));
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void AWholeInstallPassesAndOneMissingFileIsNamed()
        {
            string root = Temp();
            try
            {
                foreach (var f in InstallCheck.Required) Touch(root, f);
                Assert.That(InstallCheck.Missing(root), Is.Empty);
                File.Delete(Path.Combine(root, @"Themes\Default\ToolBar.xaml"));
                Assert.That(InstallCheck.Missing(root), Is.EqualTo(new[] { @"Themes\Default\ToolBar.xaml" }));
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void TheListMatchesWhatThePackageShips()
        {
            string? dir = TestContext.CurrentContext.TestDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "RunTime", "Themes"))) dir = Path.GetDirectoryName(dir);
            if (dir == null) Assert.Ignore("no RunTime folder above the test folder");
            Assert.That(InstallCheck.Missing(Path.Combine(dir!, "RunTime")), Is.Empty, "every file the check requires is in RunTime");
            Assert.That(InstallCheck.Message, Does.StartWith("NeoBuzz Live's files are incomplete."));
        }
    }
}
