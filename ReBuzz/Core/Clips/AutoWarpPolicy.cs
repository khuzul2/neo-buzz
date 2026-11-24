#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW1 as built): the one preference of auto-warp, Preferences > Audio > "Auto-warp long files".
// A small key=value file beside the streaming settings (not a registry setting, as those are not). Pure (ReBuzz.SessionTests links this file).

using System;
using System.IO;
using ReBuzz.Core.AudioLibrary;

namespace ReBuzz.Core.Clips
{
    /// <summary>Where auto-warp takes the beats from (AW4): the pretrained network when its file is here, else the AW2 network, else AW1's DSP (Auto); or one of them only (the evaluation and support).</summary>
    internal enum BeatSource { Auto, BeatThis, Aw2, Dsp }

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

        static BeatSource source = BeatSource.Auto;

        /// <summary>A hidden setting: <c>beatSource=auto|beatthis|aw2|dsp</c> in autowarp.txt, or <c>NEO_BEAT_SOURCE</c> (which wins). No page shows it; it is for the evaluation and for support.</summary>
        public static BeatSource Source
        {
            get
            {
                if (Enum.TryParse<BeatSource>(Environment.GetEnvironmentVariable("NEO_BEAT_SOURCE"), true, out var fromEnv)) return fromEnv;
                Load();
                return source;
            }
            set { loaded = true; source = value; }
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
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    if (key == "autoWarpLongFiles") enabled = value != "0";
                    else if (key == "beatSource" && Enum.TryParse<BeatSource>(value, true, out var s)) source = s;
                }
            }
            catch (Exception) { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(AudioCache.DataFolder);
                File.WriteAllLines(SettingsFile, new[] { "autoWarpLongFiles=" + (enabled ? "1" : "0"), "beatSource=" + source.ToString().ToLowerInvariant() });
            }
            catch (Exception) { }
        }

        /// <summary>Forces the next read to go to the file again (tests that change the data folder).</summary>
        internal static void Reload() { loaded = false; enabled = true; source = BeatSource.Auto; }
    }
}
