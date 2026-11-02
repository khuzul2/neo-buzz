#nullable disable
// Fork (docs/specs/mac-engine.md M0): the engine's registry seam (IRegistryEx) kept in a JSON
// file, for headless and macOS hosts where there is no Windows registry. Paths are the
// registry's: Write/Read take a path under the registry root ("Settings",
// "BuzzGUI\EngineSettings"); the subkey calls take a full path (root + "\" + name), as callers
// build them for the Windows registry. Values are stored as JSON numbers, booleans or strings,
// and read back through the type's converter with the invariant culture, so a file written on
// one machine reads the same on another. Pure .NET.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReBuzz.Core;

namespace ReBuzz.Platform
{
    public sealed class JsonRegistry : IRegistryEx, BuzzGUI.Common.IRegistryStore
    {
        readonly object gate = new object();
        readonly string path;
        readonly string root;
        Dictionary<string, Dictionary<string, JsonNode>> data;

        /// <param name="path">The JSON file (created on the first write).</param>
        /// <param name="root">The registry root callers prefix full paths with (Global.RegistryRoot, "Software\ReBuzz").</param>
        public JsonRegistry(string path, string root)
        {
            this.path = path;
            this.root = Normalize(root);
        }

        /// <summary>registry.json in the user's settings folder for this OS.</summary>
        public static string DefaultPath => Path.Combine(SettingsFolder, "registry.json");

        /// <summary>~/Library/Application Support/ReBuzz on macOS, $XDG_CONFIG_HOME/rebuzz (or ~/.config/rebuzz) on Linux, %APPDATA%\ReBuzz on Windows.</summary>
        public static string SettingsFolder
        {
            get
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", "ReBuzz");
                if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReBuzz");
                string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "rebuzz");
            }
        }

        public string FilePath => path;

        // ------------------------------------------------------------------ IRegistryEx

        public void Write<T>(string key, T x, string path = "BuzzGUI")
        {
            lock (gate)
            {
                Section(Normalize(path), create: true)[key] = ToNode(x);
                Save();
            }
        }

        public T Read<T>(string key, T def, string path = "BuzzGUI")
        {
            lock (gate)
            {
                var section = Section(Normalize(path), create: false);
                if (section == null || !section.TryGetValue(key, out var node) || node == null) return def;
                return Convert(node, def);
            }
        }

        public IEnumerable<T> ReadNumberedList<T>(string key, string path = "BuzzGUI")
        {
            var list = new List<T>();
            lock (gate)
            {
                var section = Section(Normalize(path), create: false);
                if (section == null) return list;
                for (int i = 1; section.TryGetValue(key + i.ToString(CultureInfo.InvariantCulture), out var node) && node != null; i++)
                {
                    if (!TryConvert(node, out T value)) break;
                    list.Add(value);
                }
            }
            return list;
        }

        public Dictionary<string, object> ReadDictionary(string path = "BuzzGUI")
        {
            lock (gate)
            {
                var section = Section(Normalize(path), create: false);
                return section == null
                    ? new Dictionary<string, object>()
                    : section.ToDictionary(kv => kv.Key, kv => Plain(kv.Value));
            }
        }

        public void DeleteCurrentUserSubKey(string key)
        {
            lock (gate)
            {
                if (Data().Remove(Relative(key))) Save();
            }
        }

        public IRegistryKey CreateCurrentUserSubKey(string subKey)
        {
            string relative = Relative(subKey);
            lock (gate)
            {
                Section(relative, create: true);
                Save();
            }
            return new Key(this, relative);
        }

        public void DeleteCurrentUserValue(string value, string path)
        {
            lock (gate)
            {
                var section = Section(Relative(path), create: false);
                if (section != null && section.Remove(value)) Save();
            }
        }

        // ------------------------------------------------------------------ BuzzGUI's RegistryEx and settings (IRegistryStore)

        string BuzzGUI.Common.IRegistryStore.Get(string path, string key) => Read<string>(key, null, path);

        void BuzzGUI.Common.IRegistryStore.Set(string path, string key, string value) => Write(key, value, path);

        IEnumerable<string> BuzzGUI.Common.IRegistryStore.Names(string path)
        {
            lock (gate) return Section(Normalize(path), create: false)?.Keys.ToList() ?? new List<string>();
        }

        IReadOnlyDictionary<string, object> BuzzGUI.Common.IRegistryStore.Values(string path) => ReadDictionary(path);

        sealed class Key : IRegistryKey
        {
            readonly JsonRegistry owner;
            readonly string section;

            public Key(JsonRegistry owner, string section)
            {
                this.owner = owner;
                this.section = section;
            }

            public void SetValue(string name, object value)
            {
                lock (owner.gate)
                {
                    owner.Section(section, create: true)[name] = ToNode(value);
                    owner.Save();
                }
            }
        }

        // ------------------------------------------------------------------ paths

        /// <summary>Backslashes, no slashes at either end; the registry is case-insensitive, so are we.</summary>
        static string Normalize(string p) => (p ?? "").Replace('/', '\\').Trim('\\');

        /// <summary>A full path (root + "\" + name) as a path under the root; one not under it stays whole.</summary>
        string Relative(string full)
        {
            string p = Normalize(full);
            if (root.Length > 0 && p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                string rest = p.Substring(root.Length);
                if (rest.Length == 0) return "";
                // "Software\ReBuzzSettings" (a root without its trailing backslash) is "Settings" too.
                return rest.TrimStart('\\');
            }
            return p;
        }

        // ------------------------------------------------------------------ values

        static JsonNode ToNode(object x) => x switch
        {
            null => null,
            bool b => JsonValue.Create(b),
            int or long or short or byte or sbyte or ushort or uint or ulong => JsonValue.Create(System.Convert.ToDecimal(x, CultureInfo.InvariantCulture)),
            float f => JsonValue.Create((double)f),
            double d => JsonValue.Create(d),
            decimal m => JsonValue.Create(m),
            string s => JsonValue.Create(s),
            _ => JsonValue.Create(TypeDescriptor.GetConverter(x.GetType()).ConvertToInvariantString(x)),
        };

        static string Text(JsonNode node) => node is JsonValue v && v.TryGetValue(out string s) ? s : node.ToJsonString();

        /// <summary>As the registry gives values to ReadDictionary: whole numbers as int (or long), the rest as text.</summary>
        static object Plain(JsonNode node)
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue(out string s)) return s;
                if (v.TryGetValue(out bool b)) return b;
                if (v.TryGetValue(out int i)) return i;
                if (v.TryGetValue(out long l)) return l;
                if (v.TryGetValue(out double d)) return d;
            }
            return node?.ToJsonString();
        }

        static T Convert<T>(JsonNode node, T def) => TryConvert(node, out T value) ? value : def;

        static bool TryConvert<T>(JsonNode node, out T value)
        {
            value = default;
            try
            {
                var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                if (type == typeof(string)) { value = (T)(object)Text(node); return true; }
                if (type == typeof(object)) { value = (T)Plain(node); return true; }
                string text = node is JsonValue v && v.TryGetValue(out bool b) ? (b ? "True" : "False") : Text(node);
                var converted = TypeDescriptor.GetConverter(type).ConvertFromInvariantString(text);
                if (converted == null) return false;
                value = (T)converted;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ the file

        Dictionary<string, Dictionary<string, JsonNode>> Data()
        {
            if (data != null) return data;
            data = new Dictionary<string, Dictionary<string, JsonNode>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject all)
                    foreach (var (name, section) in all)
                        if (section is JsonObject values)
                        {
                            var s = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
                            foreach (var (key, value) in values) s[key] = value?.DeepClone();
                            data[name] = s;
                        }
            }
            catch (Exception)
            {
                // A damaged file starts afresh; it is replaced on the next write.
            }
            return data;
        }

        Dictionary<string, JsonNode> Section(string name, bool create)
        {
            var all = Data();
            if (all.TryGetValue(name, out var s)) return s;
            if (!create) return null;
            return all[name] = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        }

        void Save()
        {
            try
            {
                var all = new JsonObject();
                foreach (var (name, section) in Data().OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var values = new JsonObject();
                    foreach (var (key, value) in section.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                        values[key] = value?.DeepClone();
                    all[name] = values;
                }
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // Written whole to a temporary file, then moved over: a crash never leaves half a file.
                string temp = path + ".tmp";
                File.WriteAllText(temp, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception)
            {
                // Settings are best-effort, as the registry's writes are (RegistryEx swallows them too).
            }
        }
    }
}
