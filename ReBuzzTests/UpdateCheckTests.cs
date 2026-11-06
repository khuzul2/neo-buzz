using System;
using System.IO;
using System.Linq;
using System.Text;
using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using Microsoft.Win32;
using ReBuzzTests.Automation;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork (NeoBuzz Live): NeoBuzzLive.exe neither checks for updates nor offers upstream ReBuzz's installer, which would
    /// replace NeoBuzz Live. ReBuzz.dll doesn't reference the updater (BuzzGUI.BuzzUpdate), nothing beside it is the
    /// updater or names upstream's releases, and upstream's "check for updates" setting, saved True, turns nothing on.
    /// ReBuzz.AgentTests/UpdateCheckTests.cs guards the sources (the startup call is gone), ReBuzz.Studio.Tests the studio.
    /// </summary>
    public class UpdateCheckTests
    {
        /// <summary>Upstream's release feed and installer, as the updater named them (split, so no source here names them whole).</summary>
        static readonly string[] Upstream = { "wasteddesign/ReBuzz/" + "releases", "api.github.com/repos/" + "wasteddesign", "ReBuzzSetup" + "_", "BuzzGUI." + "BuzzUpdate" };

        static bool Holds(byte[] bytes, string text) =>
            bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 || bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;

        [Test]
        public void TheAppNeitherReferencesNorShipsTheUpdater()
        {
            Assert.That(typeof(ReBuzz.App).Assembly.GetReferencedAssemblies().Select(a => a.Name), Has.None.EqualTo("BuzzGUI.BuzzUpdate"));

            string bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
            Assert.That(File.Exists(Path.Combine(bin, ReBuzz.Product.WindowsExe)), Is.True, $"the app is built in {bin}");
            Assert.That(File.Exists(Path.Combine(bin, "BuzzGUI.BuzzUpdate.dll")), Is.False, "the updater isn't beside the app");
            var found = Directory.GetFiles(bin, "*.dll").Concat(Directory.GetFiles(bin, "*.exe"))
                .SelectMany(f => Upstream.Where(u => Holds(File.ReadAllBytes(f), u)).Select(u => $"{Path.GetFileName(f)}: {u}"))
                .ToList();
            Assert.That(found, Is.Empty, "the app's own files name neither the updater nor upstream's releases");
        }

        [Test]
        public void ASavedCheckForUpdatesTurnsNothingOn()
        {
            const string path = @"BuzzGUI\GeneralSettings";
            string key = Global.RegistryRoot + path;
            object before = RegistryEx.Portable ? RegistryEx.Store?.Get(path, "CheckForUpdates") : Registry.CurrentUser.OpenSubKey(key)?.GetValue("CheckForUpdates");
            try
            {
                if (RegistryEx.Portable) RegistryEx.Store?.Set(path, "CheckForUpdates", "True");
                else Registry.CurrentUser.CreateSubKey(key).SetValue("CheckForUpdates", "True");

                var settings = new GeneralSettings();
#pragma warning disable CS0618 // obsolete on purpose: always false
                Assert.That(settings.CheckForUpdates, Is.False, "upstream's setting, saved True, is ignored");
#pragma warning restore CS0618
                Assert.That(settings.List.Select(s => s.Name), Has.None.EqualTo("CheckForUpdates"), "and the Settings window doesn't list it");
                Assert.That(Global.GeneralSettings.List.Select(s => s.Description ?? ""), Has.None.Contains("update"));
            }
            finally
            {
                if (RegistryEx.Portable) { if (before is string saved) RegistryEx.Store?.Set(path, "CheckForUpdates", saved); }
                else if (before == null) Registry.CurrentUser.OpenSubKey(key, true)?.DeleteValue("CheckForUpdates", false);
                else Registry.CurrentUser.CreateSubKey(key).SetValue("CheckForUpdates", before);
            }
        }
    }
}
