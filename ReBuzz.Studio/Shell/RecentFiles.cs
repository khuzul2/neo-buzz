// Fork (docs/specs/studio-mvp.md S7): File ▸ Open Recent. The engine keeps the list in the
// registry ("Recent File List": File1…File10, newest first) when a song opens or saves, as
// ReBuzz.exe's File menu reads it.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReBuzz.Core;

namespace ReBuzz.Studio.Shell
{
    internal static class RecentFiles
    {
        const string Section = "Recent File List";

        /// <summary>The recent songs, newest first.</summary>
        public static IReadOnlyList<string> List(IRegistryEx registry) =>
            registry.ReadNumberedList<string>("File", Section).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();

        public static void Clear(IRegistryEx registry) => registry.DeleteCurrentUserSubKey(BuzzGUI.Common.Global.RegistryRoot + Section);

        /// <summary>The submenu: one entry per song (missing ones greyed), then Clear Menu.</summary>
        public static IEnumerable<MenuEntry> Menu(IRegistryEx registry)
        {
            var files = List(registry);
            foreach (var file in files)
            {
                string path = file;
                yield return new MenuEntry
                {
                    // Song name first, its folder after (as the Mac's Open Recent does for clashes).
                    Header = $"{Path.GetFileName(path)} — {Path.GetDirectoryName(path)}".Replace("_", "__"),
                    Run = () => _ = SongFiles.Open(path),
                    CanRun = () => File.Exists(path),
                };
            }
            if (files.Count == 0) yield return new MenuEntry { Header = "No recent songs", Run = () => { }, CanRun = () => false };
            yield return MenuEntry.Separator;
            yield return new MenuEntry { Header = "Clear Menu", Run = () => Clear(registry), CanRun = () => files.Count > 0 };
        }
    }
}
