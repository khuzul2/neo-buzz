#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW1 as built): the one preference of auto-warp, Preferences > Audio > "Auto-warp long files".
// A small key=value file beside the streaming settings (not a registry setting, as those are not). Pure (ReBuzz.SessionTests links this file).

using System;
using System.IO;
using ReBuzz.Core.AudioLibrary;

namespace ReBuzz.Core.Clips
{
    /// <summary>What a long file gets on import (AW5): nothing, the beats followed, or the beats and then every transient on the grid.</summary>
    internal enum AutoWarpMode { Off, Beat, Grid }

    internal static class AutoWarpPolicy
    {
        /// <summary>A file longer than this many bars (with a tempo) is auto-warped on import.</summary>
        public const int LongBars = 8;

        static bool loaded;
        static AutoWarpMode mode = AutoWarpMode.Beat;

        /// <summary>What a long file gets when it is imported (default: Beat, as auto-warp has always been). <c>NEO_AUTOWARP_OFF=1</c> turns it off whatever the file says.</summary>
        public static AutoWarpMode Mode
        {
            get
            {
                if (Environment.GetEnvironmentVariable("NEO_AUTOWARP_OFF") == "1") return AutoWarpMode.Off;
                Load();
                return mode;
            }
            set { loaded = true; mode = value; }
        }

        /// <summary>Auto-warp a long file when it is imported (either mode).</summary>
        public static bool Enabled
        {
            get => Mode != AutoWarpMode.Off;
            set { loaded = true; mode = value ? (mode == AutoWarpMode.Off ? AutoWarpMode.Beat : mode) : AutoWarpMode.Off; }
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
                    if (line.Substring(0, eq).Trim() == "autoWarpLongFiles")
                    {
                        // 0 off, 1 beat (what the file has always said for on), 2 grid.
                        string v = line.Substring(eq + 1).Trim();
                        mode = v == "0" ? AutoWarpMode.Off : v == "2" ? AutoWarpMode.Grid : AutoWarpMode.Beat;
                    }
                }
            }
            catch (Exception) { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(AudioCache.DataFolder);
                File.WriteAllLines(SettingsFile, new[] { "autoWarpLongFiles=" + (int)mode });
            }
            catch (Exception) { }
        }

        /// <summary>Forces the next read to go to the file again (tests that change the data folder).</summary>
        internal static void Reload() { loaded = false; mode = AutoWarpMode.Beat; }
    }
}
