// Fork (NeoBuzz Live): the studio neither checks for updates nor offers upstream ReBuzz's installer, which would
// replace NeoBuzz Live. Nothing it ships is the updater (BuzzGUI.BuzzUpdate) or names upstream's releases, a saved
// "check for updates" doesn't turn anything on, and no menu item or Preferences page is about updates.
// ReBuzz.AgentTests/UpdateCheckTests.cs guards the sources; ReBuzzTests/UpdateCheckTests.cs the Windows app.

using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Headless.NUnit;
using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class UpdateCheckTests
    {
        /// <summary>Upstream's release feed and installer, as the updater named them (split, so no source here names them whole).</summary>
        internal static readonly string[] Upstream = { "wasteddesign/ReBuzz/" + "releases", "api.github.com/repos/" + "wasteddesign", "ReBuzzSetup" + "_", "BuzzGUI." + "BuzzUpdate" };

        static bool Holds(byte[] bytes, string text) =>
            bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 || bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;

        [AvaloniaTest]
        public void TheStudioNeitherChecksForUpdatesNorOffersUpstreams()
        {
            // What ships beside the studio: no updater, no reference to it, no upstream release URL.
            string dir = AppContext.BaseDirectory;
            var found = Directory.GetFiles(dir, "*.dll").Where(f => Path.GetFileName(f) != "ReBuzz.Studio.Tests.dll")
                .SelectMany(f => Upstream.Where(u => Holds(File.ReadAllBytes(f), u)).Select(u => $"{Path.GetFileName(f)}: {u}"))
                .ToList();
            Assert.That(found, Is.Empty, "the studio's assemblies");

            // A saved "check for updates" (upstream's setting) turns nothing on, and Preferences ▸ General doesn't list it.
            var host = Studio.Host;
            Assert.That(RegistryEx.Store, Is.Not.Null, "the studio's settings store");
            RegistryEx.Store.Set(@"BuzzGUI\GeneralSettings", "CheckForUpdates", "True");
#pragma warning disable CS0618 // obsolete on purpose: always false
            Assert.That(new GeneralSettings().CheckForUpdates, Is.False);
#pragma warning restore CS0618
            Assert.That(new GeneralSettings().List.Select(s => s.Name), Has.None.EqualTo("CheckForUpdates"));
            Assert.That(Global.GeneralSettings.List.Select(s => s.Description), Has.None.Contains("update"), "Preferences ▸ General");

            // No menu item is about updates (the window's menus, the macOS menu bar's and its app menu).
            var window = new MainWindow(host);
            try
            {
                var texts = window.MenuModel(mac: false).Concat(window.MenuModel(mac: true)).Concat(window.AppMenuModel())
                    .SelectMany(e => new[] { e }.Concat(e.Items?.Invoke() ?? Enumerable.Empty<MenuEntry>()))
                    .Select(e => e.Text).Where(t => t != null).ToList();
                Assert.That(texts, Is.Not.Empty);
                Assert.That(texts, Has.None.Contains("pdate"), "no Check for updates");
            }
            finally
            {
                window.Close();
            }
        }
    }
}
