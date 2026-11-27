#nullable disable
// Fork (docs/specs/machines.md §3.6, PRS1): where a machine's presets live.
//
//   factory   <Gear>/<Kind>/<DLL name>.prs.xml, generated from Presets.txt and shipped with the app. Read-only: nothing in the
//             app writes to Gear (on the Mac it is inside the signed app, and the next install replaces it).
//   user      <settings folder>/Presets/<DLL name>.prs.xml, the same file format (Import and Export still work), written atomically.
//
// A machine's presets are both together. A user preset with a factory preset's name shadows it; deleting that copy brings the
// factory preset back. Factory presets can't be deleted or overwritten. A missing or corrupt user file never stops the factory
// set loading (a corrupt one is kept aside as .bad).
//
// The one-time migration (Migrate) moves presets an older build wrote into the Gear file into the user file. It tells factory
// from user by the factory snapshot the build ships beside each Gear file, "<DLL name>.prs.xml.factory", a verbatim copy.
// While a snapshot exists the app reads the factory set from it, so entries left in the Gear file (read-only Gear) are never
// taken for factory presets.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuzzGUI.Interfaces;

namespace BuzzGUI.Common.Presets
{
    public static class PresetStore
    {
        internal static readonly object Gate = new object();
        static readonly Dictionary<string, MachinePresets> cache = new Dictionary<string, MachinePresets>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The settings folder the user presets live under (in its Presets folder). The studio sets it to its settings folder; null takes the OS default.</summary>
        public static string RootFolder { get; set; }

        /// <summary>The registry file's folder for this OS (ReBuzz/Platform/JsonRegistry.SettingsFolder): ~/Library/Application Support/ReBuzz, $XDG_CONFIG_HOME/rebuzz, %APPDATA%\ReBuzz.</summary>
        public static string DefaultSettingsFolder
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

        /// <summary>Tests: the Gear folder can't be written (the migration then leaves the Gear file as it is), as an OS keeps a normal user out of a read-only app folder. A root user can write anywhere, so a test can't count on the OS.</summary>
        public static bool GearWritesBlocked { get; set; }

        /// <summary>The folder of every user preset file.</summary>
        public static string UserFolder => Path.Combine(string.IsNullOrEmpty(RootFolder) ? DefaultSettingsFolder : RootFolder, "Presets");

        public static string UserFile(string dllName) => Path.Combine(UserFolder, FileName(dllName) + ".prs.xml");

        static string FileName(string dllName)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string(dllName.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray());
        }

        /// <summary>The factory snapshot beside a Gear preset file.</summary>
        public static string SnapshotFile(string gearDir, string dllName) => Path.Combine(gearDir, dllName + ".prs.xml.factory");

        /// <summary>A machine's presets: factory (its Gear folder) and the user's. Cached; reloaded when either file changed.</summary>
        public static MachinePresets For(string gearDir, string dllName, IMachine machine = null)
        {
            lock (Gate)
            {
                string key = Path.GetFullPath(gearDir ?? "") + "|" + dllName + "|" + UserFile(dllName);
                if (!cache.TryGetValue(key, out var mp))
                    cache[key] = mp = new MachinePresets(gearDir, dllName);
                mp.Refresh(machine);
                return mp;
            }
        }

        /// <summary>A machine's presets, from the folder its DLL sits in (or the Gear folder beside the app).</summary>
        public static MachinePresets For(IMachine machine) => For(GearFolderOf(machine), machine.DLL.Name, machine);

        /// <summary>The Gear folder a machine's presets are in: its DLL's folder when a preset file is there, else the Gear folder beside the app.</summary>
        public static string GearFolderOf(IMachine machine)
        {
            string dll = null;
            try { dll = string.IsNullOrEmpty(machine.DLL?.Path) ? null : Path.GetDirectoryName(machine.DLL.Path); } catch (Exception) { }
            string fallback = InterfaceExtensions.MachineExtensions.GetDirectory(machine);
            string name = machine.DLL.Name;
            foreach (var dir in new[] { dll, fallback })
                if (!string.IsNullOrEmpty(dir) && (File.Exists(Path.Combine(dir, name + ".prs.xml")) || File.Exists(SnapshotFile(dir, name)) || File.Exists(Path.Combine(dir, name + ".prs"))))
                    return dir;
            return dll ?? fallback;
        }

        /// <summary>"Pad" for "Pad — Lush" (or "Pad: Lush"), "" when the name has no group.</summary>
        public static string GroupOf(string name)
        {
            int i = name.IndexOf(" — ", StringComparison.Ordinal);
            if (i < 0) i = name.IndexOf(": ", StringComparison.Ordinal);
            return i > 0 ? name.Substring(0, i) : "";
        }

        /// <summary>The group the user's presets without one of their own are listed in.</summary>
        public const string UserGroup = "User";

        // ---------------------------------------------------------------- migration

        /// <summary>What <see cref="Migrate"/> did.</summary>
        public sealed class MigrationResult
        {
            /// <summary>The presets copied into the user file.</summary>
            public List<string> Moved { get; } = new List<string>();
            /// <summary>The Gear file was set back to the factory snapshot (it could be written).</summary>
            public bool GearRestored { get; set; }
            /// <summary>Why nothing was done: "", "done before", "no snapshot", "no gear file", or an error.</summary>
            public string Skipped { get; set; } = "";
        }

        static string MigratedFile => Path.Combine(UserFolder, "migrated.txt");

        static HashSet<string> Migrated()
        {
            try { return File.Exists(MigratedFile) ? new HashSet<string>(File.ReadAllLines(MigratedFile).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
            catch (Exception) { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        static void Record(HashSet<string> done, string dllName)
        {
            done.Add(dllName);
            Directory.CreateDirectory(UserFolder);
            string tmp = MigratedFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllLines(tmp, done.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                File.Move(tmp, MigratedFile, true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>
        /// Moves the presets an older build saved into a Gear .prs.xml (the ones its factory snapshot doesn't have, or has differently)
        /// into the user file, once per machine. Never overwrites or deletes a preset already in the user file. When Gear can be
        /// written the Gear file is then set back to the snapshot; when it can't, it is left as it is (the copy is done).
        /// Without a snapshot nothing can be told from the factory set, so nothing happens (and it is tried again later).
        /// </summary>
        public static MigrationResult Migrate(string gearDir, string dllName)
        {
            var result = new MigrationResult();
            lock (Gate)
            {
                try
                {
                    string gear = Path.Combine(gearDir, dllName + ".prs.xml"), snap = SnapshotFile(gearDir, dllName);
                    if (!File.Exists(snap)) { result.Skipped = "no snapshot"; return result; }
                    if (!File.Exists(gear)) { result.Skipped = "no gear file"; return result; }
                    // The usual start: the Gear file is the snapshot. Compared by bytes (Dye Essence's is 16 MB: parsing it costs seconds),
                    // and remembered for this process, so a machine's reloads don't read the files again.
                    string key = Path.GetFullPath(gear), seen = Stamp(gear, snap);
                    if (cleanStamps.TryGetValue(key, out var before) && before == seen) { result.Skipped = "clean"; return result; }
                    if (SameBytes(gear, snap)) { cleanStamps[key] = seen; result.Skipped = "clean"; return result; }
                    var done = Migrated();
                    if (done.Contains(dllName)) { result.Skipped = "done before"; return result; }

                    var factory = PresetDictionary.LoadXml(dllName, snap);
                    var found = PresetDictionary.LoadXml(dllName, gear);
                    var extra = new List<KeyValuePair<string, Preset>>();
                    foreach (var kv in found)
                    {
                        if (kv.Key == "<default>") continue;
                        var f = factory[kv.Key];
                        if (f == null || f.ToXml() != kv.Value.ToXml()) extra.Add(kv);
                    }

                    if (extra.Count > 0)
                    {
                        var user = LoadUser(dllName, out _);
                        foreach (var kv in extra)
                        {
                            if (user.ContainsKey(kv.Key)) continue;     // theirs already: never replaced
                            user[kv.Key] = kv.Value;
                            result.Moved.Add(kv.Key);
                        }
                        if (result.Moved.Count > 0)
                        {
                            user.Save(UserFile(dllName));
                            // Read it back: the Gear file is only touched once the copy is known to be whole.
                            var back = PresetDictionary.LoadXml(dllName, UserFile(dllName));
                            if (result.Moved.Any(k => back[k] == null)) throw new IOException("the user preset file didn't read back whole");
                        }
                        TryRestoreGear(gear, snap, result);
                    }
                    Record(done, dllName);
                    cleanStamps[key] = Stamp(gear, snap);
                }
                catch (Exception e)
                {
                    result.Skipped = "error: " + e.Message;
                }
            }
            return result;
        }

        static readonly Dictionary<string, string> cleanStamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static bool SameBytes(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using (var x = fa.OpenRead())
            using (var y = fb.OpenRead())
            {
                var bx = new byte[81920];
                var by = new byte[81920];
                int n;
                while ((n = x.Read(bx, 0, bx.Length)) > 0)
                {
                    int m = 0;
                    while (m < n) { int r = y.Read(by, m, n - m); if (r <= 0) return false; m += r; }
                    for (int i = 0; i < n; i++) if (bx[i] != by[i]) return false;
                }
            }
            return true;
        }

        static void TryRestoreGear(string gear, string snap, MigrationResult result)
        {
            string tmp = gear + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                if (GearWritesBlocked) throw new UnauthorizedAccessException("the Gear folder is read-only");
                File.Copy(snap, tmp, true);
                File.Move(tmp, gear, true);
                result.GearRestored = true;
            }
            catch (Exception)
            {
                try { File.Delete(tmp); } catch (Exception) { }
            }
        }

        /// <summary>Migrates every machine with a factory snapshot under a Gear folder (and its Generators and Effects), as the app starts. Returns how many presets moved.</summary>
        public static int MigrateAll(string gearRoot)
        {
            int moved = 0;
            if (string.IsNullOrEmpty(gearRoot)) return 0;
            foreach (var dir in new[] { gearRoot, Path.Combine(gearRoot, "Generators"), Path.Combine(gearRoot, "Effects") })
            {
                string[] snaps;
                try { snaps = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.prs.xml.factory") : Array.Empty<string>(); }
                catch (Exception) { continue; }
                foreach (var s in snaps)
                {
                    string name = Path.GetFileName(s);
                    moved += Migrate(dir, name.Substring(0, name.Length - ".prs.xml.factory".Length)).Moved.Count;
                }
            }
            return moved;
        }

        /// <summary>The user's file for a machine; a corrupt one is set aside as .bad and an empty set returned.</summary>
        internal static PresetDictionary LoadUser(string dllName, out string problem)
        {
            problem = null;
            string file = UserFile(dllName);
            PresetDictionary user = null;
            if (File.Exists(file))
            {
                try { user = PresetDictionary.LoadXml(dllName, file); }
                catch (Exception e)
                {
                    string aside = file + ".bad";
                    for (int n = 2; File.Exists(aside); n++) aside = file + ".bad" + n;
                    try
                    {
                        File.Move(file, aside);
                        problem = $"Couldn't read your presets for {dllName} ({e.Message}); the file is kept as {aside}.";
                    }
                    catch (Exception)
                    {
                        problem = $"Couldn't read your presets for {dllName} ({e.Message}).";
                    }
                }
            }
            user ??= new PresetDictionary();
            user.Filename = file;
            return user;
        }

        internal static string Stamp(params string[] files) =>
            string.Join("|", files.Select(f =>
            {
                try { return File.Exists(f) ? new FileInfo(f).Length + "@" + File.GetLastWriteTimeUtc(f).Ticks : "-"; }
                catch (Exception) { return "?"; }
            }));
    }

    /// <summary>One machine's presets: the factory set (read-only) and the user's, together.</summary>
    public sealed class MachinePresets
    {
        static object Gate => PresetStore.Gate;   // the store's own lock (For and Migrate hold it)

        readonly List<string> problems = new List<string>();
        PresetDictionary factory = new PresetDictionary();
        PresetDictionary user = new PresetDictionary();
        string stamp;

        public string DllName { get; }
        public string GearDir { get; }

        /// <summary>The Gear .prs.xml (the factory set; the app never writes it).</summary>
        public string FactoryFile => Path.Combine(GearDir, DllName + ".prs.xml");

        /// <summary>The user's file (the only one the app writes).</summary>
        public string UserFile => PresetStore.UserFile(DllName);

        internal MachinePresets(string gearDir, string dllName)
        {
            GearDir = gearDir ?? "";
            DllName = dllName;
        }

        string[] Files => new[] { FactoryFile, PresetStore.SnapshotFile(GearDir, DllName), Path.Combine(GearDir, DllName + ".prs"), UserFile };

        DateTime checkedAt = DateTime.MinValue;
        IReadOnlyList<string> names = Array.Empty<string>();

        internal void Refresh(IMachine machine)
        {
            // A machine's editors ask often (every repaint of a preset bar): look at the files at most four times a second.
            // Callers without a machine (the browser, the manual, tests) always look.
            if (machine != null && stamp != null && (DateTime.UtcNow - checkedAt).TotalMilliseconds < 250 && (binaryLoaded || !File.Exists(Path.Combine(GearDir, DllName + ".prs")))) return;
            checkedAt = DateTime.UtcNow;
            string now = PresetStore.Stamp(Files);
            if (now == stamp && (machine == null || binaryLoaded || !File.Exists(Path.Combine(GearDir, DllName + ".prs")))) return;
            Load(machine);
        }

        bool binaryLoaded;

        void Load(IMachine machine)
        {
            problems.Clear();
            binaryLoaded = false;
            if (Directory.Exists(GearDir)) PresetStore.Migrate(GearDir, DllName);

            factory = new PresetDictionary();
            string snap = PresetStore.SnapshotFile(GearDir, DllName);
            string source = File.Exists(snap) ? snap : FactoryFile;
            if (File.Exists(source))
            {
                try { factory = PresetDictionary.LoadXml(DllName, source) ?? new PresetDictionary(); }
                catch (Exception e) { problems.Add($"Couldn't read {Path.GetFileName(FactoryFile)} ({e.Message})."); factory = new PresetDictionary(); }
            }
            string prs = Path.Combine(GearDir, DllName + ".prs");
            if (machine != null && File.Exists(prs))
            {
                try
                {
                    var old = PresetDictionary.Load(machine, prs);
                    if (old != null) factory.Merge(old);
                    binaryLoaded = true;
                }
                catch (Exception e) { problems.Add($"Couldn't read {Path.GetFileName(prs)} ({e.Message})."); }
            }
            factory.Filename = null;
            factory.ReadOnly = true;

            user = PresetStore.LoadUser(DllName, out var problem);
            if (problem != null) problems.Add(problem);
            stamp = PresetStore.Stamp(Files);
            Renamed();
        }

        void Renamed()
        {
            var all = new SortedSet<string>(factory.Keys);
            all.UnionWith(user.Keys);
            names = all.ToList();
        }

        /// <summary>What went wrong reading the files (the sets are loaded as far as they could be).</summary>
        public IReadOnlyList<string> Problems { get { lock (Gate) return problems.ToList(); } }

        /// <summary>Every preset name, factory and user, once each.</summary>
        public IReadOnlyList<string> Names { get { lock (Gate) return names; } }

        public int Count => Names.Count;

        public bool Contains(string name) { lock (Gate) return user.ContainsKey(name) || factory.ContainsKey(name); }

        /// <summary>The user's own (a user copy of a factory name counts).</summary>
        public bool IsUser(string name) { lock (Gate) return user.ContainsKey(name); }

        public bool IsFactory(string name) { lock (Gate) return factory.ContainsKey(name); }

        /// <summary>A user preset with a factory preset's name: the factory one is hidden behind it until the copy is deleted.</summary>
        public bool Shadows(string name) { lock (Gate) return user.ContainsKey(name) && factory.ContainsKey(name); }

        /// <summary>The preset (the user's before the factory's), or null.</summary>
        public Preset this[string name] { get { lock (Gate) return user[name] ?? factory[name]; } }

        /// <summary>Its group: the part before " — " (or ": "); a user preset without one is in "User"; "" for a factory preset without.</summary>
        public string GroupOf(string name)
        {
            string g = PresetStore.GroupOf(name);
            return g.Length > 0 ? g : IsUser(name) ? PresetStore.UserGroup : "";
        }

        /// <summary>Saves a user preset (a copy that shadows a factory one of that name). Written atomically to the user's file only.</summary>
        public void Save(string name, Preset preset)
        {
            lock (Gate)
            {
                bool had = user.ContainsKey(name);
                var old = user[name];
                user[name] = preset;
                try { Write(); }
                catch
                {
                    if (had) user[name] = old; else user.Remove(name);
                    throw;
                }
            }
        }

        /// <summary>Deletes the user's preset of that name (a shadowed factory preset comes back). False when the user has none (factory presets can't be deleted).</summary>
        public bool Delete(string name)
        {
            lock (Gate)
            {
                if (!user.ContainsKey(name)) return false;
                var old = user[name];
                user.Remove(name);
                try { Write(); }
                catch
                {
                    user[name] = old;
                    throw;
                }
                return true;
            }
        }

        /// <summary>Adds presets from an imported file that this machine doesn't have yet (by name, user or factory). Returns how many.</summary>
        public int Import(PresetDictionary imported)
        {
            lock (Gate)
            {
                var added = new List<string>();
                foreach (var kv in imported)
                {
                    if (user.ContainsKey(kv.Key) || factory.ContainsKey(kv.Key)) continue;
                    user[kv.Key] = kv.Value;
                    added.Add(kv.Key);
                }
                if (added.Count == 0) return 0;
                try { Write(); }
                catch
                {
                    foreach (var k in added) user.Remove(k);
                    throw;
                }
                return added.Count;
            }
        }

        void Write()
        {
            user.Filename = UserFile;
            user.Save(UserFile);
            stamp = PresetStore.Stamp(Files);
            Renamed();
        }
    }
}
