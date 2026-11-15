#nullable disable
// Where a plug-in runs (docs/specs/plugin-host.md H2): in the app's process, or in a neoplug-server process of its own (a sandbox):
// one per plug-in, one per instance, or one shared by every sandboxed plug-in. The choice is saved with the song (per machine) and
// per plug-in (Preferences > Plug-ins), with a default for everything else. A blocklisted or flagged plug-in is forced into a sandbox.
//
// Defaults: sandbox per plug-in on Windows (a plug-in that crashes costs its own process, and editors embed across processes);
// in process on macOS and Linux (macOS can't embed another process's view: a sandboxed editor floats), with the sandbox on demand.
// NEOPLUG_PLACEMENT (inprocess, plugin, instance, shared) overrides everything but the forcing, for tests.
//
// The settings are the file placement.json in the plug-in data folder; the studio's Preferences page shares this code, and the
// machines read the file again when its time changes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NeoBuzz.Plug
{
    public sealed class PlacementFile
    {
        /// <summary>The default for plug-ins without a choice of their own: "" (the platform's), or one of <see cref="PlugPlacement"/>'s values.</summary>
        public string Default { get; set; } = "";
        /// <summary>A choice per plug-in: key "format|id" to a placement.</summary>
        public Dictionary<string, string> Plugins { get; set; } = new();
        /// <summary>Modules that are sandboxed whatever the choice: path to the reason (a plug-in that was blocked and then unblocked is flagged).</summary>
        public Dictionary<string, string> Flagged { get; set; } = new();
    }

    public static class PlugPlacement
    {
        public const string Auto = "", InProcess = "inprocess", PerPlugin = "plugin", PerInstance = "instance", Shared = "shared";
        public static readonly string[] Values = { InProcess, PerPlugin, PerInstance, Shared };

        public static string Label(string placement) => placement switch
        {
            InProcess => "In process",
            PerPlugin => "Sandbox per plug-in",
            PerInstance => "Sandbox per instance",
            Shared => "Sandbox, shared",
            _ => "Default",
        };

        public static bool IsSandbox(string placement) => placement is PerPlugin or PerInstance or Shared;

        /// <summary>The native library's number for a placement (np_open_ex).</summary>
        public static int Native(string placement) => placement switch { PerPlugin => 1, PerInstance => 2, Shared => 3, _ => 0 };

        public static string FromNative(int placement) => placement switch { 1 => PerPlugin, 2 => PerInstance, 3 => Shared, _ => InProcess };

        public static string Normalize(string value)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(Values, value) >= 0 ? value : Auto;
        }

        /// <summary>What a platform does when nothing says otherwise.</summary>
        public static string PlatformDefault => OperatingSystem.IsWindows() ? PerPlugin : InProcess;

        static readonly object gate = new();
        static readonly JsonSerializerOptions json = new() { WriteIndented = true };
        static PlacementFile file;
        static long fileStamp;

        static string FilePath => Path.Combine(PlugPaths.DataDir, "placement.json");

        static PlacementFile Load()
        {
            lock (gate)
            {
                long stamp = 0;
                try { if (File.Exists(FilePath)) stamp = File.GetLastWriteTimeUtc(FilePath).Ticks; } catch (Exception) { }
                if (file != null && stamp == fileStamp) return file;
                file = null;
                try { if (File.Exists(FilePath)) file = JsonSerializer.Deserialize<PlacementFile>(File.ReadAllText(FilePath), json); } catch (Exception) { }
                fileStamp = stamp;
                return file ??= new PlacementFile();
            }
        }

        static void Save()
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(PlugPaths.DataDir);
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(file, json));
                    File.Move(tmp, FilePath, true);
                    try { fileStamp = File.GetLastWriteTimeUtc(FilePath).Ticks; } catch (Exception) { }
                }
                catch (Exception) { }
            }
        }

        /// <summary>Forgets what was read (tests, and a change from another process).</summary>
        public static void Reload() { lock (gate) file = null; }

        public static string Key(string format, string id, string name) => (format ?? "").ToLowerInvariant() + "|" + (string.IsNullOrEmpty(id) ? name : id);

        /// <summary>The default for plug-ins without a choice of their own ("" = the platform's).</summary>
        public static string Default
        {
            get { var f = Load(); lock (gate) return Normalize(f.Default); }
            set { var f = Load(); lock (gate) { f.Default = Normalize(value); Save(); } }
        }

        /// <summary>One plug-in's own choice ("" = none).</summary>
        public static string ForPlugin(string format, string id, string name)
        {
            var f = Load();
            lock (gate) return f.Plugins.TryGetValue(Key(format, id, name), out var v) ? Normalize(v) : Auto;
        }

        public static void SetForPlugin(string format, string id, string name, string placement)
        {
            var f = Load();
            lock (gate)
            {
                string key = Key(format, id, name);
                placement = Normalize(placement);
                if (placement.Length == 0) f.Plugins.Remove(key); else f.Plugins[key] = placement;
                Save();
            }
        }

        public static IReadOnlyDictionary<string, string> Chosen()
        {
            var f = Load();
            lock (gate) return new Dictionary<string, string>(f.Plugins);
        }

        /// <summary>Why a module is sandboxed whatever the choice (on the blocklist, or flagged), or null.</summary>
        public static string Forced(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var blocked = PlugLibrary.Blocked().FirstOrDefault(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));
                if (blocked != null) return "it is on the blocklist (" + blocked.Reason + ")";
            }
            catch (Exception) { }
            var f = Load();
            lock (gate)
            {
                foreach (var kv in f.Flagged)
                    if (string.Equals(kv.Key, path, StringComparison.OrdinalIgnoreCase)) return "it is flagged (" + kv.Value + ")";
            }
            return null;
        }

        public static void Flag(string path, string reason)
        {
            if (string.IsNullOrEmpty(path)) return;
            var f = Load();
            lock (gate) { f.Flagged[path] = reason; Save(); }
        }

        public static bool Unflag(string path)
        {
            var f = Load();
            lock (gate)
            {
                bool had = f.Flagged.Remove(path);
                if (had) Save();
                return had;
            }
        }

        public static IReadOnlyDictionary<string, string> FlaggedModules()
        {
            var f = Load();
            lock (gate) return new Dictionary<string, string>(f.Flagged);
        }

        /// <summary>
        /// The placement a plug-in is opened with: the environment (tests), else the song's choice for this machine, else the plug-in's
        /// own choice, else the default, else the platform's. A forced module is never in process.
        /// <paramref name="why"/> says where the answer came from.
        /// </summary>
        public static string Resolve(string requested, string format, string id, string name, string path, out string why)
        {
            string chosen = Normalize(Environment.GetEnvironmentVariable("NEOPLUG_PLACEMENT"));
            why = "NEOPLUG_PLACEMENT";
            if (chosen.Length == 0) { chosen = Normalize(requested); why = "this machine's choice"; }
            if (chosen.Length == 0) { chosen = ForPlugin(format, id, name); why = "this plug-in's choice"; }
            if (chosen.Length == 0) { chosen = Default; why = "the default in Preferences"; }
            if (chosen.Length == 0) { chosen = PlatformDefault; why = OperatingSystem.IsWindows() ? "the Windows default" : "the platform's default"; }
            if (chosen == InProcess && Forced(path) is { } forced)
            {
                why = "forced into a sandbox: " + forced;
                return PerPlugin;
            }
            return chosen;
        }
    }
}
