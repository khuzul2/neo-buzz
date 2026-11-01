// Fork (docs/specs/cross-platform.md P0): settings that aren't tied to the Windows registry. New
// fork settings go through ISettingsStore; JsonSettingsStore keeps them in one JSON file in the
// user's settings folder (%APPDATA%\ReBuzz on Windows; the Mac port picks its folder). Pure .NET.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ReBuzz.Core.Settings
{
    public interface ISettingsStore
    {
        string Get(string section, string key);
        void Set(string section, string key, string value);
    }

    public static class SettingsStoreExtensions
    {
        public static bool GetBool(this ISettingsStore s, string section, string key, bool fallback) =>
            bool.TryParse(s.Get(section, key), out bool b) ? b : fallback;

        public static int GetInt(this ISettingsStore s, string section, string key, int fallback) =>
            int.TryParse(s.Get(section, key), out int i) ? i : fallback;
    }

    public sealed class JsonSettingsStore : ISettingsStore
    {
        readonly object gate = new object();
        readonly string path;
        Dictionary<string, Dictionary<string, string>> data;

        public JsonSettingsStore(string path) => this.path = path;

        /// <summary>The user's settings file: settings.json in the per-user application data folder.</summary>
        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReBuzz", "settings.json");

        public string FilePath => path;

        Dictionary<string, Dictionary<string, string>> Data()
        {
            if (data != null) return data;
            try
            {
                data = File.Exists(path)
                    ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path))
                    : null;
            }
            catch (Exception)
            {
                data = null;   // a damaged file starts afresh (it is replaced on the next write)
            }
            return data ??= new Dictionary<string, Dictionary<string, string>>();
        }

        public string Get(string section, string key)
        {
            lock (gate)
                return Data().TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;
        }

        public void Set(string section, string key, string value)
        {
            lock (gate)
            {
                var d = Data();
                if (!d.TryGetValue(section, out var s)) d[section] = s = new Dictionary<string, string>();
                if (value == null) s.Remove(key);
                else s[key] = value;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // Write a temporary file, then replace: a crash never leaves half a file.
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, path, true);
            }
        }
    }
}
