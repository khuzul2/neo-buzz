using System.Collections;
using System.IO;
using System.Resources;
using System.Text;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork (rename): ReBuzz.exe's own windows say NeoBuzz Live. The main window isn't built in the tests, so the
    /// compiled XAML the app ships (ReBuzz.g.resources) is read: the main window's title, About's and the splash
    /// screen's text. The executable and the MCP server's name are pinned in AgentHeadlessTests.
    /// </summary>
    public class AppNameTests
    {
        static string Baml(string name)
        {
            var assembly = typeof(ReBuzz.App).Assembly;
            using var resources = assembly.GetManifestResourceStream(assembly.GetName().Name + ".g.resources")!;
            Assert.That(resources, Is.Not.Null, "the app's compiled XAML");
            using var reader = new ResourceReader(resources);
            foreach (DictionaryEntry entry in reader)
            {
                if ((string)entry.Key != name) continue;
                using var baml = (Stream)entry.Value!;
                using var bytes = new MemoryStream();
                baml.CopyTo(bytes);
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            Assert.Fail($"{name} isn't in the app's resources");
            return "";
        }

        [Test]
        public void TheWindowsSayNeoBuzzLive()
        {
            Assert.That(ReBuzz.Product.Name, Is.EqualTo("NeoBuzz Live"));
            Assert.That(Baml("appviews/mainwindow.baml"), Does.Contain("NeoBuzz Live").And.Not.Contain("ReBuzz Digital Audio Workstation"), "the main window's title");
            Assert.That(Baml("common/aboutwindow.baml"), Does.Contain("About NeoBuzz Live").And.Not.Contain("About ReBuzz"));
            Assert.That(Baml("common/splashscreenwindow.baml"), Does.Contain("NeoBuzz Live...").And.Not.Contain("ReBuzz..."), "Loading NeoBuzz Live...");
        }
    }
}
