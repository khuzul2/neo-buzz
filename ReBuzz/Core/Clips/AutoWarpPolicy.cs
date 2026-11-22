#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW1 as built): the one preference of auto-warp, Preferences > Audio > "Auto-warp long files".
// A small key=value file beside the streaming settings (not a registry setting, as those are not). Pure (ReBuzz.SessionTests links this file).

using System;
using System.IO;
using ReBuzz.Core.AudioLibrary;

namespace ReBuzz.Core.Clips
{
    internal static class AutoWarpPolicy
    {
        /// <summary>A file longer than this many bars (with a tempo) is auto-warped on import.</summary>
        public const int LongBars = 8;

        static bool loaded;
        static bool enabled = true;

        /// <summary>Auto-warp a long file when it is imported (default on). <c>NEO_AUTOWARP_OFF=1</c> turns it off whatever the file says.</summary>
        public static bool Enabled
        {
            get
            {
                if (Environment.GetEnvironmentVariable("NEO_AUTOWARP_OFF") == "1") return false;
                Load();
                return enabled;
            }
            set { loaded = true; enabled = value; }
        }

        static string SettingsFile => Path.Combine(AudioCache.DataFolder, "autowarp.txt");

        /// <summary>Reads the file once; a missing or damaged one leaves the default.</summary>
        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (var line in File.ReadAllLines(SettingsFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim() == "autoWarpLongFiles") enabled = line.Substring(eq + 1).Trim() != "0";
                }
            }
            catch (Exception) { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(AudioCache.DataFolder);
                File.WriteAllLines(SettingsFile, new[] { "autoWarpLongFiles=" + (enabled ? "1" : "0") });
            }
            catch (Exception) { }
        }

        /// <summary>Forces the next read to go to the file again (tests that change the data folder).</summary>
        internal static void Reload() { loaded = false; enabled = true; }
    }
}
