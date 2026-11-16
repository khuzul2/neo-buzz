// Fork (docs/specs/browser.md, BR1): the browser's places and their index. The user adds folders (the browser's Places, kept in the
// settings); a background indexer reads every audio and MIDI file under them into a SampleIndex (a file in the settings folder),
// scans again only what changed (path, size and modification time), and a file watcher per folder keeps it current while the app
// runs. Progress is for the panel to show. Portable: no UI, the settings reached through two small delegates, so the studio, the
// agent's tools and the tests use the same thing.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuzz.Agent.Browser
{
    /// <summary>Where the indexer is: files looked at, of how many (0 while it still counts), and what it is doing.</summary>
    public sealed record IndexProgress(bool Scanning, int Done, int Total, string Folder)
    {
        public static readonly IndexProgress Idle = new(false, 0, 0, "");

        public string Text => !Scanning ? "" : Total > 0 ? $"Indexing {Done:N0} of {Total:N0}" : "Indexing…";
    }

    public sealed class BrowserLibrary : IDisposable
    {
        public const string PlacesKey = "BrowserPlaces";
        public const string RecentFilesKey = "BrowserRecentFiles";
        public const int MaxRecentFiles = 24;
        public const string IndexFileName = "browser-index.bin";

        readonly string indexFile;
        readonly Func<string, string> read;
        readonly Action<string, string> write;
        readonly object gate = new();
        readonly List<FileSystemWatcher> watchers = new();
        readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.Ordinal);
        Timer? settle;
        CancellationTokenSource? scanning;
        Task scan = Task.CompletedTask;
        List<string>? places;
        List<string>? recent;
        bool disposed, watching;

        /// <param name="settingsFolder">Where browser-index.bin lives (the user settings folder).</param>
        /// <param name="read">A setting by key ("" when none).</param>
        /// <param name="write">Saves a setting.</param>
        public BrowserLibrary(string settingsFolder, Func<string, string> read, Action<string, string> write)
        {
            indexFile = Path.Combine(settingsFolder, IndexFileName);
            this.read = read;
            this.write = write;
            Index = new SampleIndex();
            Index.Load(indexFile);
        }

        public SampleIndex Index { get; }

        public string IndexPath => indexFile;

        public IndexProgress Progress { get; private set; } = IndexProgress.Idle;

        /// <summary>The places changed (added, removed).</summary>
        public event Action? PlacesChanged;

        /// <summary>The indexer's progress changed (any thread).</summary>
        public event Action? ProgressChanged;

        // ------------------------------------------------------------------ places

        public IReadOnlyList<string> Places => places ??= Split(read(PlacesKey));

        public IReadOnlyList<string> RecentFiles => recent ??= Split(read(RecentFilesKey));

        static List<string> Split(string text) => (text ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

        static string Normal(string folder)
        {
            string full = Path.GetFullPath(folder);
            return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }

        /// <summary>Adds a folder as a place and indexes it; false when it is not a folder or is one already.</summary>
        public bool AddPlace(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            string full;
            try { full = Normal(folder); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            if (!Directory.Exists(full)) return false;
            if (Places.Any(p => string.Equals(p, full, StringComparison.Ordinal))) return false;
            var list = Places.Append(full).ToList();
            places = list;
            write(PlacesKey, string.Join("|", list));
            PlacesChanged?.Invoke();
            Watch(full);
            Rescan();
            return true;
        }

        /// <summary>Removes a place and what was indexed under it (the files stay where they are); false when it was not one.</summary>
        public bool RemovePlace(string folder)
        {
            string full;
            try { full = Normal(folder); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            var list = Places.ToList();
            if (list.RemoveAll(p => string.Equals(p, full, StringComparison.Ordinal)) == 0) return false;
            places = list;
            write(PlacesKey, string.Join("|", list));
            lock (gate)
            {
                var w = watchers.FirstOrDefault(x => string.Equals(x.Path, full, StringComparison.Ordinal));
                if (w != null) { watchers.Remove(w); w.Dispose(); }
            }
            Index.RemoveRoot(full);
            SaveIndex();
            PlacesChanged?.Invoke();
            return true;
        }

        /// <summary>Notes a file as recently used (previewed or dropped); the newest first, 24 kept.</summary>
        public void MarkRecent(string path)
        {
            var list = RecentFiles.Where(p => p != path).Prepend(path).Take(MaxRecentFiles).ToList();
            recent = list;
            write(RecentFilesKey, string.Join("|", list));
            PlacesChanged?.Invoke();
        }

        /// <summary>The recent files that are still there.</summary>
        public IReadOnlyList<string> ExistingRecents() => RecentFiles.Where(File.Exists).ToList();

        /// <summary>Forgets what was read, so the next read comes from the settings (tests; another window's change).</summary>
        public void Reload()
        {
            places = recent = null;
            PlacesChanged?.Invoke();
        }

        // ------------------------------------------------------------------ the indexer

        /// <summary>Whether a scan is running.</summary>
        public bool IsScanning => Progress.Scanning;

        /// <summary>Starts (or restarts) the incremental scan of every place, in the background.</summary>
        public void Rescan()
        {
            lock (gate)
            {
                if (disposed) return;
                scanning?.Cancel();
                var previous = scan;
                var cts = scanning = new CancellationTokenSource();
                var roots = Places.ToList();
                scan = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { /* the cancelled one */ }
                    ScanRoots(roots, cts.Token);
                });
            }
        }

        /// <summary>Waits for the scan that is running (tests, the agent).</summary>
        public bool WaitForScan(int milliseconds = 60000)
        {
            Task t;
            lock (gate) t = scan;
            try { return t.Wait(milliseconds); }
            catch (AggregateException) { return true; }
        }

        void SetProgress(IndexProgress p)
        {
            Progress = p;
            ProgressChanged?.Invoke();
        }

        /// <summary>One scan of the roots, on the calling thread: new and changed files read, vanished ones dropped, a batch at a time.</summary>
        internal void ScanRoots(IReadOnlyList<string> roots, CancellationToken ct)
        {
            try
            {
                SetProgress(new IndexProgress(true, 0, 0, ""));
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, ReturnSpecialDirectories = false };
                int done = 0, total = 0;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var batch = new List<SampleEntry>();
                foreach (var root in roots)
                {
                    if (ct.IsCancellationRequested) return;
                    if (!Directory.Exists(root)) { Index.RemoveRoot(root); continue; }
                    var files = new List<string>();
                    foreach (var f in Directory.EnumerateFiles(root, "*", options))
                    {
                        if (ct.IsCancellationRequested) return;
                        if (AudioFileInfo.IsAudio(f) || AudioFileInfo.IsMidi(f)) files.Add(f);
                    }
                    total += files.Count;
                    SetProgress(new IndexProgress(true, done, total, root));
                    foreach (var f in files)
                    {
                        if (ct.IsCancellationRequested) return;
                        seen.Add(f);
                        done++;
                        var entry = Make(f, root, Index.Find(f));
                        if (entry != null) batch.Add(entry);
                        if (batch.Count >= 500)
                        {
                            Index.Apply(batch);
                            batch = new List<SampleEntry>();
                            SetProgress(new IndexProgress(true, done, total, root));
                        }
                    }
                    if (batch.Count > 0) { Index.Apply(batch); batch = new List<SampleEntry>(); }
                    // What was indexed under this root and is no longer there.
                    var gone = Index.Entries.Where(e => e.Root == root && !seen.Contains(e.Path)).Select(e => e.Path).ToList();
                    if (gone.Count > 0) Index.Apply(null, gone);
                }
                // A root that is no longer a place: its files go.
                var stale = Index.Entries.Select(e => e.Root).Distinct().Where(r => !roots.Contains(r)).ToList();
                foreach (var r in stale) Index.RemoveRoot(r);
                SaveIndex();
            }
            finally
            {
                SetProgress(IndexProgress.Idle);
            }
        }

        /// <summary>The entry for a file: the old one when its size and time are unchanged, else a new one read from the header; null when it is not readable audio.</summary>
        internal SampleEntry? Make(string path, string root, SampleEntry? old)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                long modified = fi.LastWriteTimeUtc.Ticks;
                if (old != null && old.Size == fi.Length && old.Modified == modified && old.Root == root) return null;       // unchanged: nothing to apply
                if (AudioFileInfo.IsMidi(path))
                    return new SampleEntry(path, root, IndexKind.Midi, 0, 0, 0, 0, "", fi.Length, modified);
                if (!AudioFileInfo.TryRead(path, out var info)) return null;
                return new SampleEntry(path, root, IndexKind.Audio, info.Seconds, info.SampleRate, info.Channels, info.Bpm, info.Key, fi.Length, modified);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        void SaveIndex()
        {
            try { Index.Save(indexFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* an index that can't be saved is rebuilt next time */ }
        }

        // ------------------------------------------------------------------ watching

        /// <summary>Starts a watcher on every place (while the app runs), and scans them once.</summary>
        public void Start()
        {
            lock (gate)
            {
                if (watching || disposed) return;
                watching = true;
            }
            foreach (var p in Places) Watch(p);
            Rescan();
        }

        void Watch(string root)
        {
            lock (gate)
            {
                if (disposed || !watching) return;
                if (watchers.Any(w => string.Equals(w.Path, root, StringComparison.Ordinal))) return;
                try
                {
                    var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
                    w.Created += (s, e) => Touch(e.FullPath);
                    w.Changed += (s, e) => Touch(e.FullPath);
                    w.Deleted += (s, e) => Touch(e.FullPath);
                    w.Renamed += (s, e) => { Touch(e.OldFullPath); Touch(e.FullPath); };
                    w.Error += (s, e) => Rescan();             // the buffer overflowed: look at everything again
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                }
                catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException) { /* no watcher here: the next start's scan will see it */ }
            }
        }

        void Touch(string path)
        {
            pending[path] = 1;
            lock (gate)
            {
                if (disposed) return;
                settle ??= new Timer(_ => Settle(), null, Timeout.Infinite, Timeout.Infinite);
                settle.Change(400, Timeout.Infinite);
            }
        }

        /// <summary>The changes a watcher reported, applied once they stop coming (a copy of a folder is one batch).</summary>
        internal void Settle()
        {
            var paths = pending.Keys.ToList();
            foreach (var p in paths) pending.TryRemove(p, out _);
            if (paths.Count == 0) return;
            var roots = Places;
            var upsert = new List<SampleEntry>();
            var remove = new List<string>();
            var folders = new List<string>();
            foreach (var p in paths)
            {
                string? root = roots.FirstOrDefault(r => p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                if (root == null) continue;
                if (Directory.Exists(p))
                {
                    foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                        if (AudioFileInfo.IsAudio(f) || AudioFileInfo.IsMidi(f)) { var e = Make(f, root, Index.Find(f)); if (e != null) upsert.Add(e); }
                }
                else if (File.Exists(p))
                {
                    if (!AudioFileInfo.IsAudio(p) && !AudioFileInfo.IsMidi(p)) continue;
                    var e = Make(p, root, Index.Find(p));
                    if (e != null) upsert.Add(e);
                }
                else
                {
                    if (Index.Find(p) != null) remove.Add(p);
                    folders.Add(p);
                }
            }
            if (upsert.Count + remove.Count + folders.Count == 0) return;
            Index.Apply(upsert, remove, folders);
            SaveIndex();
        }

        /// <summary>Applies at once the changes the watchers have reported (tests; they otherwise wait 400 ms).</summary>
        public void FlushWatchers() => Settle();

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                scanning?.Cancel();
                foreach (var w in watchers) w.Dispose();
                watchers.Clear();
                settle?.Dispose();
            }
            try { scan.Wait(2000); } catch { /* cancelled */ }
        }
    }
}
