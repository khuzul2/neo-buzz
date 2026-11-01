using ReBuzz.Core.Settings;

namespace ReBuzz.AgentTests
{
    /// <summary>Settings outside the registry (docs/specs/cross-platform.md P0).</summary>
    [TestFixture]
    public class SettingsStoreTests
    {
        [Test]
        public void ValuesPersistAcrossInstancesAndSurviveADamagedFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "rebuzz-settings-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "settings.json");
            var a = new JsonSettingsStore(path);
            Assert.That(a.Get("agent", "token"), Is.Null);
            a.Set("agent", "token", "abc");
            a.Set("agent", "port", "7481");
            var b = new JsonSettingsStore(path);
            Assert.That(b.Get("agent", "token"), Is.EqualTo("abc"));
            Assert.That(b.GetInt("agent", "port", 1), Is.EqualTo(7481));
            Assert.That(b.GetBool("agent", "allow", true), Is.True, "missing → fallback");
            b.Set("agent", "token", null);
            Assert.That(new JsonSettingsStore(path).Get("agent", "token"), Is.Null);

            File.WriteAllText(path, "{ damaged");
            var c = new JsonSettingsStore(path);
            Assert.That(c.Get("agent", "port"), Is.Null);
            c.Set("agent", "port", "9000");
            Assert.That(new JsonSettingsStore(path).Get("agent", "port"), Is.EqualTo("9000"), "a damaged file is replaced on the next write");
            Directory.Delete(dir, true);
        }
    }
}
