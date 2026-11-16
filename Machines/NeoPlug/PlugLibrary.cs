#nullable disable
// The plug-in library (docs/specs/plugin-host.md §3.7): where plug-ins are, what a scan found, and the blocklist.
//
// Scanning runs in a process of its own (neoplug-scan, one module per run, with a timeout), so a plug-in that crashes or
// hangs while it loads costs the scanner and not the app: the module goes on the blocklist, with a way to retry. The
// results are cached by path, size and modification time, so a rescan only looks at what changed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace NeoBuzz.Plug
{
    /// <summary>One plug-in a scan found: a VST3 class or a CLAP plug-in in a module.</summary>
    public sealed class PlugEntry
    {
        public string Format { get; set; } = "";          // "vst3" or "clap"
        public string Path { get; set; } = "";            // the module
        public string Id { get; set; } = "";              // class UID (32 hex digits) or CLAP id
        public string Name { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string Version { get; set; } = "";
        public string Category { get; set; } = "";
        public bool Instrument { get; set; }
        public int Params { get; set; }
        public int Inputs { get; set; }
        public int Outputs { get; set; }
        public int Aux { get; set; }
        public int Latency { get; set; }
        public string Problem { get; set; } = "";         // set when the class did not open in the scan: it is not listed
        /// <summary>The name in the Gear menu and in songs: "Vendor/Name (VST3)".</summary>
        public string Display { get; set; } = "";
    }

    sealed class ModuleRecord
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public long Modified { get; set; }
        public List<PlugEntry> Classes { get; set; } = new();
    }

    public sealed class BlockedModule
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public long Modified { get; set; }
        public string Reason { get; set; } = "";
        public string When { get; set; } = "";
    }

    sealed class LibraryFile
    {
        public int Format { get; set; } = 1;
        public List<ModuleRecord> Modules { get; set; } = new();
        public List<BlockedModule> Blocked { get; set; } = new();
    }

    public static class PlugPaths
    {
        static string Env(string name) { var v = Environment.GetEnvironmentVariable(name); return string.IsNullOrWhiteSpace(v) ? null : v; }

        /// <summary>Where the library keeps its files (NEOBUZZ_PLUGIN_DIR overrides it).</summary>
        public static string DataDir
        {
            get
            {
                var env = Env("NEOBUZZ_PLUGIN_DIR");
                if (env != null) return env;
                string baseDir;
                if (OperatingSystem.IsWindows()) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                else if (OperatingSystem.IsMacOS()) baseDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
                else baseDir = Env("XDG_CONFIG_HOME") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return System.IO.Path.Combine(baseDir, OperatingSystem.IsLinux() ? "neobuzz-live" : "NeoBuzz Live", "Plugins");
            }
        }

        public static string LibraryFilePath => System.IO.Path.Combine(DataDir, "library.json");

        /// <summary>The folders to scan: NEOBUZZ_PLUGIN_PATH, the user's added ones (folders.txt in DataDir), and the standard ones (unless NEOBUZZ_PLUGIN_STANDARD=0).</summary>
        public static List<string> SearchDirs()
        {
            var dirs = new List<string>();
            void Add(string d) { if (!string.IsNullOrWhiteSpace(d) && !dirs.Contains(d)) dirs.Add(d); }
            foreach (var d in (Env("NEOBUZZ_PLUGIN_PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) Add(d.Trim());
            try
            {
                string extra = System.IO.Path.Combine(DataDir, "folders.txt");
                if (File.Exists(extra))
                    foreach (var line in File.ReadAllLines(extra)) if (!line.TrimStart().StartsWith("#")) Add(line.Trim());
            }
            catch (Exception) { }
            if (Env("NEOBUZZ_PLUGIN_STANDARD") != "0")
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var d in (Env("CLAP_PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) Add(d.Trim());
                if (OperatingSystem.IsWindows())
                {
                    string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    Add(System.IO.Path.Combine(common, "VST3"));
                    Add(System.IO.Path.Combine(common, "CLAP"));
                    Add(System.IO.Path.Combine(local, "Programs", "Common", "VST3"));
                    Add(System.IO.Path.Combine(local, "Programs", "Common", "CLAP"));
                }
                else if (OperatingSystem.IsMacOS())
                {
                    foreach (var kind in new[] { "VST3", "CLAP" })
                    {
                        Add($"/Library/Audio/Plug-Ins/{kind}");
                        Add(System.IO.Path.Combine(home, "Library", "Audio", "Plug-Ins", kind));
                    }
                }
                else
                {
                    Add(System.IO.Path.Combine(home, ".vst3"));
                    Add("/usr/lib/vst3");
                    Add("/usr/local/lib/vst3");
                    Add(System.IO.Path.Combine(home, ".clap"));
                    Add("/usr/lib/clap");
                    Add("/usr/local/lib/clap");
                }
            }
            return dirs;
        }

        /// <summary>The folders the user added (folders.txt in DataDir), as written.</summary>
        public static List<string> UserFolders()
        {
            var list = new List<string>();
            try
            {
                string extra = System.IO.Path.Combine(DataDir, "folders.txt");
                if (File.Exists(extra))
                    foreach (var line in File.ReadAllLines(extra))
                        if (!line.TrimStart().StartsWith("#") && line.Trim().Length > 0 && !list.Contains(line.Trim())) list.Add(line.Trim());
            }
            catch (Exception) { }
            return list;
        }

        static void WriteUserFolders(List<string> folders)
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllLines(System.IO.Path.Combine(DataDir, "folders.txt"), folders);
        }

        /// <summary>Adds a folder to scan; false when it is blank or there already.</summary>
        public static bool AddFolder(string folder)
        {
            folder = (folder ?? "").Trim();
            if (folder.Length == 0) return false;
            var list = UserFolders();
            if (list.Contains(folder)) return false;
            list.Add(folder);
            try { WriteUserFolders(list); } catch (Exception) { return false; }
            return true;
        }

        public static bool RemoveFolder(string folder)
        {
            var list = UserFolders();
            if (list.RemoveAll(f => f == folder) == 0) return false;
            try { WriteUserFolders(list); } catch (Exception) { return false; }
            return true;
        }

        static IEnumerable<string> BesideCandidates(string assemblyLocation, string file)
        {
            string dir = System.IO.Path.GetDirectoryName(assemblyLocation) ?? "";
            // An app that links this file (the studio) keeps the machines in Gear beside itself.
            yield return System.IO.Path.Combine(AppContext.BaseDirectory, "Gear", "NeoPlug", file);
            yield return System.IO.Path.Combine(dir, file);
            yield return System.IO.Path.Combine(dir, "..", "NeoPlug", file);
            yield return System.IO.Path.Combine(dir, "..", "..", "NeoPlug", file);
            yield return System.IO.Path.Combine(dir, "..", "..", "..", "NeoPlug", file);
        }

        static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

        public static IEnumerable<string> NativeLibraryCandidates(string assemblyLocation)
        {
            string env = Env("NEOPLUG_LIB");
            if (env != null) yield return env;
            string file = OperatingSystem.IsWindows() ? "neoplug.dll" : OperatingSystem.IsMacOS() ? "libneoplug.dylib" : "libneoplug.so";
            foreach (var c in BesideCandidates(assemblyLocation, file)) yield return c;
        }

        public static string ScannerPath()
        {
            string env = Env("NEOPLUG_SCAN");
            if (env != null) return env;
            foreach (var c in BesideCandidates(typeof(PlugPaths).Assembly.Location, Exe("neoplug-scan")))
                if (File.Exists(c)) return System.IO.Path.GetFullPath(c);
            return null;
        }
    }

    public static class PlugLibrary
    {
        static readonly object gate = new();
        static LibraryFile file;
        static long fileStamp;
        static int scanning;

        /// <summary>A scan is running now.</summary>
        public static bool Scanning => Volatile.Read(ref scanning) != 0;

        /// <summary>What the last scan of this process found.</summary>
        public static ScanReport LastReport { get; private set; }
        static readonly JsonSerializerOptions json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

        /// <summary>How long one module gets to be scanned.</summary>
        public static TimeSpan ScanTimeout = TimeSpan.FromSeconds(30);

        public static event Action Changed;

        static LibraryFile Load()
        {
            lock (gate)
            {
                // Another copy of this class (the studio's Preferences page links this file) may have rescanned or unblocked:
                // the file's time says so.
                long stamp = 0;
                try { if (File.Exists(PlugPaths.LibraryFilePath)) stamp = File.GetLastWriteTimeUtc(PlugPaths.LibraryFilePath).Ticks; } catch (Exception) { }
                if (file != null && stamp == fileStamp) return file;
                file = null;
                try
                {
                    if (File.Exists(PlugPaths.LibraryFilePath))
                        file = JsonSerializer.Deserialize<LibraryFile>(File.ReadAllText(PlugPaths.LibraryFilePath), json);
                }
                catch (Exception) { }
                fileStamp = stamp;
                return file ??= new LibraryFile();
            }
        }

        static void Save()
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(PlugPaths.DataDir);
                    string tmp = PlugPaths.LibraryFilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(file, json));
                    File.Move(tmp, PlugPaths.LibraryFilePath, true);
                    try { fileStamp = File.GetLastWriteTimeUtc(PlugPaths.LibraryFilePath).Ticks; } catch (Exception) { }
                }
                catch (Exception) { }
            }
        }

        /// <summary>Forgets what was read, so the next call reads the file again (tests, and a rescan from another process).</summary>
        public static void Reload() { lock (gate) file = null; }

        public static bool Exists => File.Exists(PlugPaths.LibraryFilePath);

        /// <summary>Everything the library knows that opens (classes that failed to open in the scan are left out).</summary>
        public static List<PlugEntry> All()
        {
            var f = Load();
            lock (gate)
                return f.Modules.SelectMany(m => m.Classes).Where(c => c.Problem.Length == 0).ToList();
        }

        public static List<BlockedModule> Blocked()
        {
            var f = Load();
            lock (gate) return f.Blocked.ToList();
        }

        /// <summary>The Gear menu's names for instruments (generators) or effects, unique.</summary>
        public static List<string> Names(bool effects) =>
            All().Where(e => e.Instrument == !effects).Select(e => e.Display).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        public static PlugEntry Find(string display)
        {
            if (string.IsNullOrEmpty(display)) return null;
            return All().FirstOrDefault(e => e.Display == display) ?? All().FirstOrDefault(e => string.Equals(e.Display, display, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The entry for a plug-in a song knows by path and id (its module may have moved: then by name and format).</summary>
        public static PlugEntry Find(string path, string id, string name, string format)
        {
            var all = All();
            return all.FirstOrDefault(e => e.Path == path && e.Id == id)
                   ?? all.FirstOrDefault(e => e.Format == format && e.Id == id && e.Id.Length > 0)
                   ?? all.FirstOrDefault(e => e.Format == format && e.Name == name);
        }

        /// <summary>Takes a module off the blocklist, so the next scan tries it again.</summary>
        public static bool Unblock(string path)
        {
            var f = Load();
            lock (gate)
            {
                var was = f.Blocked.Where(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
                int n = f.Blocked.RemoveAll(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));
                // H2: a plug-in that was blocked and is tried again runs in a sandbox, whatever else is chosen, until the flag is taken off.
                foreach (var b in was) PlugPlacement.Flag(b.Path, "it was blocked: " + b.Reason);
                if (n > 0) Save();
                return n > 0;
            }
        }

        public static void UnblockAll()
        {
            var f = Load();
            lock (gate)
            {
                foreach (var b in f.Blocked) PlugPlacement.Flag(b.Path, "it was blocked: " + b.Reason);
                f.Blocked.Clear();
                Save();
            }
        }

        // --- Scanning ------------------------------------------------------------------------------------

        static IEnumerable<string> Modules(string dir)
        {
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((dir, 0));
            while (stack.Count > 0)
            {
                var (d, depth) = stack.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(d); }
                catch (Exception) { continue; }
                Array.Sort(entries, StringComparer.Ordinal);
                foreach (var e in entries)
                {
                    string lower = e.ToLowerInvariant();
                    bool isDir = Directory.Exists(e);
                    if (lower.EndsWith(".vst3") || lower.EndsWith(".clap")) yield return e;      // a bundle is never walked into
                    else if (isDir && depth < 6) stack.Push((e, depth + 1));
                }
            }
        }

        static (long Size, long Modified) Stamp(string module)
        {
            try
            {
                if (File.Exists(module))
                {
                    var fi = new FileInfo(module);
                    return (fi.Length, fi.LastWriteTimeUtc.Ticks);
                }
                long size = 0, mod = Directory.GetLastWriteTimeUtc(module).Ticks;
                foreach (var f in new DirectoryInfo(module).EnumerateFiles("*", SearchOption.AllDirectories).Take(2000))
                {
                    size += f.Length;
                    mod = Math.Max(mod, f.LastWriteTimeUtc.Ticks);
                }
                return (size, mod);
            }
            catch (Exception) { return (0, 0); }
        }

        /// <summary>Makes the Gear menu's names ("Vendor/Name (VST3)"), unique across the library.</summary>
        static void Name(List<ModuleRecord> modules)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in modules.OrderBy(m => m.Path, StringComparer.Ordinal).SelectMany(m => m.Classes))
            {
                string vendor = (string.IsNullOrWhiteSpace(c.Vendor) ? "Other" : c.Vendor).Replace('/', '-').Trim();
                string name = (string.IsNullOrWhiteSpace(c.Name) ? System.IO.Path.GetFileNameWithoutExtension(c.Path) : c.Name).Replace('/', '-').Trim();
                string display = $"{vendor}/{name} ({c.Format.ToUpperInvariant()})";
                for (int n = 2; !used.Add(display); n++) display = $"{vendor}/{name} ({c.Format.ToUpperInvariant()}) #{n}";
                c.Display = display;
            }
        }

        public sealed class ScanReport
        {
            public int Scanned, Unchanged, Found, Blocked, Gone;
            public List<string> Messages = new();
            public override string ToString() => $"{Found} plug-ins; scanned {Scanned}, unchanged {Unchanged}, blocked {Blocked}, gone {Gone}";
        }

        /// <summary>
        /// Looks at every module in the search folders that is new or changed (all of them with <paramref name="force"/>),
        /// each in a process of its own, and updates the library. Safe to call from any thread, one scan at a time.
        /// </summary>
        public static ScanReport Scan(bool force = false, Action<string> log = null)
        {
            var report = new ScanReport();
            if (Interlocked.Exchange(ref scanning, 1) != 0)
            {
                report.Messages.Add("a scan is already running");
                return report;
            }
            try
            {
                string scanner = PlugPaths.ScannerPath();
                if (scanner == null)
                {
                    report.Messages.Add("neoplug-scan isn't installed beside the plug-in library");
                    log?.Invoke(report.Messages[^1]);
                    return report;
                }
                var f = Load();
                var found = new List<string>();
                foreach (var dir in PlugPaths.SearchDirs())
                    if (Directory.Exists(dir)) found.AddRange(Modules(dir));
                found = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                List<ModuleRecord> modules;
                List<BlockedModule> blocked;
                lock (gate) { modules = f.Modules.ToList(); blocked = f.Blocked.ToList(); }

                var result = new List<ModuleRecord>();
                foreach (var module in found)
                {
                    var (size, modified) = Stamp(module);
                    var known = modules.FirstOrDefault(m => string.Equals(m.Path, module, StringComparison.OrdinalIgnoreCase));
                    if (!force && known != null && known.Size == size && known.Modified == modified)
                    {
                        result.Add(known);
                        report.Unchanged++;
                        continue;
                    }
                    var block = blocked.FirstOrDefault(b => string.Equals(b.Path, module, StringComparison.OrdinalIgnoreCase));
                    if (block != null && block.Size == size && block.Modified == modified && !force) { report.Blocked++; continue; }
                    blocked.RemoveAll(b => string.Equals(b.Path, module, StringComparison.OrdinalIgnoreCase));
                    report.Scanned++;
                    log?.Invoke("scanning " + module);
                    var rec = ScanModule(scanner, module, size, modified, out string reason);
                    if (rec == null)
                    {
                        blocked.Add(new BlockedModule { Path = module, Size = size, Modified = modified, Reason = reason, When = DateTime.UtcNow.ToString("o") });
                        report.Blocked++;
                        report.Messages.Add($"{module}: {reason}");
                        log?.Invoke($"blocked {module}: {reason}");
                        continue;
                    }
                    result.Add(rec);
                }
                report.Gone = modules.Count(m => !found.Contains(m.Path, StringComparer.OrdinalIgnoreCase));
                blocked.RemoveAll(b => !found.Contains(b.Path, StringComparer.OrdinalIgnoreCase));
                Name(result);
                bool changed;
                lock (gate)
                {
                    changed = JsonSerializer.Serialize(result.Select(m => m.Classes), json) != JsonSerializer.Serialize(f.Modules.Select(m => m.Classes), json)
                              || blocked.Count != f.Blocked.Count || report.Gone > 0;
                    f.Modules = result;
                    f.Blocked = blocked;
                    report.Found = result.Sum(m => m.Classes.Count(c => c.Problem.Length == 0));
                    Save();
                }
                LastReport = report;
                if (changed) Changed?.Invoke();
                return report;
            }
            finally
            {
                Interlocked.Exchange(ref scanning, 0);
            }
        }

        /// <summary>Starts a scan on a thread of its own (the Gear menu shows what it finds the next time it is built).</summary>
        public static void ScanInBackground(bool force = false)
        {
            var t = new Thread(() => { try { Scan(force); } catch (Exception) { } }) { IsBackground = true, Name = "neoplug scan" };
            t.Start();
        }

        sealed class ScanJson
        {
            [JsonPropertyName("path")] public string Path { get; set; }
            [JsonPropertyName("error")] public string Error { get; set; }
            [JsonPropertyName("classes")] public List<ScanClass> Classes { get; set; } = new();
        }

        sealed class ScanClass
        {
            [JsonPropertyName("format")] public string Format { get; set; } = "";
            [JsonPropertyName("id")] public string Id { get; set; } = "";
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("vendor")] public string Vendor { get; set; } = "";
            [JsonPropertyName("version")] public string Version { get; set; } = "";
            [JsonPropertyName("category")] public string Category { get; set; } = "";
            [JsonPropertyName("instrument")] public bool Instrument { get; set; }
            [JsonPropertyName("params")] public int Params { get; set; }
            [JsonPropertyName("inputs")] public int Inputs { get; set; }
            [JsonPropertyName("outputs")] public int Outputs { get; set; }
            [JsonPropertyName("aux")] public int Aux { get; set; }
            [JsonPropertyName("latency")] public int Latency { get; set; }
            [JsonPropertyName("problem")] public string Problem { get; set; } = "";
        }

        static ModuleRecord ScanModule(string scanner, string module, long size, long modified, out string reason)
        {
            reason = null;
            try
            {
                var psi = new ProcessStartInfo(scanner)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add(module);
                using var p = Process.Start(psi);
                var stdout = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit((int)ScanTimeout.TotalMilliseconds))
                {
                    try { p.Kill(true); } catch (Exception) { }
                    reason = $"did not finish scanning in {ScanTimeout.TotalSeconds:0} s";
                    return null;
                }
                p.WaitForExit();   // flushes the output events
                string text;
                lock (stdout) text = stdout.ToString();
                if (p.ExitCode != 0 && p.ExitCode != 1)
                {
                    reason = $"crashed while scanning (exit code {p.ExitCode})";
                    return null;
                }
                ScanJson parsed = null;
                // A plug-in may print to stdout while it loads: the scanner's answer is the last line that is JSON.
                var jsonLine = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("{") && l.EndsWith("}"));
                try { if (jsonLine != null) parsed = JsonSerializer.Deserialize<ScanJson>(jsonLine); } catch (Exception) { }
                if (parsed == null)
                {
                    reason = "scanning gave no result";
                    return null;
                }
                if (!string.IsNullOrEmpty(parsed.Error))
                {
                    reason = "does not load: " + parsed.Error;
                    return null;
                }
                var rec = new ModuleRecord { Path = module, Size = size, Modified = modified };
                foreach (var c in parsed.Classes)
                    rec.Classes.Add(new PlugEntry
                    {
                        Format = c.Format, Path = module, Id = c.Id, Name = c.Name, Vendor = c.Vendor, Version = c.Version, Category = c.Category,
                        Instrument = c.Instrument, Params = c.Params, Inputs = c.Inputs, Outputs = c.Outputs, Aux = c.Aux, Latency = c.Latency, Problem = c.Problem ?? "",
                    });
                return rec;
            }
            catch (Exception e)
            {
                reason = "the scanner could not run: " + e.Message;
                return null;
            }
        }
    }
}
