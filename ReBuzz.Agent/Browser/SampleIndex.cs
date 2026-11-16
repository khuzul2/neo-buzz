// Fork (docs/specs/browser.md, BR1): the index of the user's sample folders, in memory, kept in one compact binary file.
//
// Why a binary file and not SQLite (Microsoft.Data.Sqlite): the index is read whole and searched in memory (100,000 entries in a few
// milliseconds), written whole in a moment, and never joined or queried by anything but a name; SQLite would add a native library for
// each of the four platforms we ship (and its packaging, on macOS in the signed bundle) for no query this needs. The file is a
// version, a root table and one record per file, with a CRC-free "end" marker so a half-written file is refused and rebuilt by the
// next scan (it is written to a temporary name and moved over the old one, so it is never half-written in practice).
//
// Reads (Search, Entries) work on an immutable snapshot an update replaces, so the UI never waits for the indexer.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReBuzz.Agent.Browser
{
    public enum IndexKind : byte { Audio = 0, Midi = 1 }

    /// <summary>One indexed file.</summary>
    public sealed class SampleEntry
    {
        public SampleEntry(string path, string root, IndexKind kind, double seconds, int rate, int channels, double bpm, string key, long size, long modifiedTicks)
        {
            Path = path;
            Root = root;
            Kind = kind;
            Seconds = seconds;
            SampleRate = rate;
            Channels = channels;
            Bpm = bpm;
            Key = key ?? "";
            Size = size;
            Modified = modifiedTicks;
            Name = System.IO.Path.GetFileNameWithoutExtension(path);
            string dir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "");
            Haystack = (Name + " " + dir).ToLowerInvariant();
            NameLower = Name.ToLowerInvariant();
        }

        public string Path { get; }
        /// <summary>The added folder it was found under.</summary>
        public string Root { get; }
        public IndexKind Kind { get; }
        public double Seconds { get; }
        public int SampleRate { get; }
        public int Channels { get; }
        /// <summary>Beats per minute from the file or its name; 0 when unknown (the preview's analysis may fill it in later).</summary>
        public double Bpm { get; }
        public string Key { get; }
        public long Size { get; }
        public long Modified { get; }
        public string Name { get; }
        internal string Haystack { get; }
        internal string NameLower { get; }

        public SampleEntry WithBpm(double bpm) => new(Path, Root, Kind, Seconds, SampleRate, Channels, bpm, Key, Size, Modified);

        public string Describe() =>
            (Kind == IndexKind.Midi ? "MIDI" : $"{Seconds:0.0#} s, {SampleRate / 1000.0:0.#} kHz, {(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : Channels + " ch")}") +
            (Bpm > 0 ? $", {Bpm:0.#} BPM" : "") + (Key.Length > 0 ? ", " + Key : "");
    }

    public sealed class SampleIndex
    {
        const int Magic = 0x5842424E;       // "NBBX"
        const int FileVersion = 1;
        const int EndMarker = 0x444E4531;

        SampleEntry[] snapshot = Array.Empty<SampleEntry>();
        Dictionary<string, SampleEntry> byPath = new(StringComparer.Ordinal);
        readonly object gate = new();

        /// <summary>The index changed (the indexer's batch, a watcher's change); any thread.</summary>
        public event Action? Changed;

        public IReadOnlyList<SampleEntry> Entries => snapshot;

        public int Count => snapshot.Length;

        public SampleEntry? Find(string path)
        {
            lock (gate) return byPath.TryGetValue(path, out var e) ? e : null;
        }

        /// <summary>Adds or replaces entries and removes paths (and everything under a removed folder), as one change.</summary>
        public void Apply(IEnumerable<SampleEntry>? upsert, IEnumerable<string>? remove = null, IEnumerable<string>? removeFolders = null, bool notify = true)
        {
            bool changed = false;
            lock (gate)
            {
                bool copied = false;
                void Own()
                {
                    if (copied) return;
                    byPath = new Dictionary<string, SampleEntry>(byPath, StringComparer.Ordinal);
                    copied = true;
                }
                if (remove != null)
                    foreach (var p in remove) { Own(); changed |= byPath.Remove(p); }
                if (removeFolders != null)
                    foreach (var f in removeFolders)
                    {
                        string prefix = f.EndsWith(System.IO.Path.DirectorySeparatorChar) ? f : f + System.IO.Path.DirectorySeparatorChar;
                        var gone = byPath.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                        if (gone.Count == 0) continue;
                        Own();
                        foreach (var k in gone) byPath.Remove(k);
                        changed = true;
                    }
                if (upsert != null)
                    foreach (var e in upsert) { Own(); byPath[e.Path] = e; changed = true; }
                if (changed) snapshot = byPath.Values.ToArray();
            }
            if (changed && notify) Changed?.Invoke();
        }

        /// <summary>Drops everything found under a folder that is no longer one of the places.</summary>
        public void RemoveRoot(string root)
        {
            SampleEntry[] s = snapshot;
            Apply(null, s.Where(e => e.Root == root).Select(e => e.Path));
        }

        /// <summary>Remembers a tempo the preview's analysis found, so the next preview is as quick.</summary>
        public void SetBpm(string path, double bpm)
        {
            var e = Find(path);
            if (e != null && e.Bpm <= 0 && bpm > 0) Apply(new[] { e.WithBpm(bpm) }, notify: false);
        }

        // ------------------------------------------------------------------ search

        static readonly char[] Separators = { ' ', '_', '-', '.', ',', '/', '\\' };

        /// <summary>
        /// The files whose name or folder name has every word of the query (a part of a word counts), best first: a name that starts
        /// with the first word, then a name with the word starting a word, then the rest; shorter names before longer; then by name.
        /// <paramref name="root"/> narrows it to one place; <paramref name="bpm"/> and <paramref name="kind"/> filter. Fast: one pass
        /// over the snapshot with ordinal comparisons (100,000 entries in a few milliseconds).
        /// </summary>
        public List<SampleEntry> Search(string? query, int limit = 200, IndexKind? kind = null, string? root = null)
        {
            var words = (query ?? "").ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            var s = snapshot;
            var hits = new List<(SampleEntry E, int Score)>(Math.Min(s.Length, 1024));
            for (int i = 0; i < s.Length; i++)
            {
                var e = s[i];
                if (kind != null && e.Kind != kind) continue;
                if (root != null && e.Root != root) continue;
                int score = 0;
                bool ok = true;
                for (int w = 0; w < words.Length; w++)
                {
                    int at = e.Haystack.IndexOf(words[w], StringComparison.Ordinal);
                    if (at < 0) { ok = false; break; }
                    if (w == 0)
                    {
                        if (e.NameLower.StartsWith(words[0], StringComparison.Ordinal)) score += 3;
                        else if (at == 0 || Separators.Contains(e.Haystack[at - 1])) score += 2;
                        else score += 1;
                    }
                }
                if (!ok) continue;
                hits.Add((e, score));
            }
            return hits.OrderByDescending(h => h.Score).ThenBy(h => h.E.Name.Length).ThenBy(h => h.E.Name, StringComparer.OrdinalIgnoreCase)
                       .Take(limit <= 0 ? int.MaxValue : limit).Select(h => h.E).ToList();
        }

        // ------------------------------------------------------------------ the file

        /// <summary>Writes the index (to a temporary name, then over the old file).</summary>
        public void Save(string file)
        {
            var s = snapshot;
            string temp = file + ".tmp";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!);
            using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new BinaryWriter(f, System.Text.Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(FileVersion);
                var roots = s.Select(e => e.Root).Distinct().ToList();
                w.Write(roots.Count);
                foreach (var r in roots) w.Write(r);
                w.Write(s.Length);
                var index = roots.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i);
                foreach (var e in s)
                {
                    w.Write(e.Path);
                    w.Write(index[e.Root]);
                    w.Write((byte)e.Kind);
                    w.Write((float)e.Seconds);
                    w.Write(e.SampleRate);
                    w.Write((byte)Math.Min(255, e.Channels));
                    w.Write((float)e.Bpm);
                    w.Write(e.Key);
                    w.Write(e.Size);
                    w.Write(e.Modified);
                }
                w.Write(EndMarker);
            }
            File.Move(temp, file, overwrite: true);
        }

        /// <summary>Reads an index file; false (and an empty index) when it is missing, damaged or of another version.</summary>
        public bool Load(string file)
        {
            try
            {
                if (!File.Exists(file)) return false;
                using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                using var r = new BinaryReader(f, System.Text.Encoding.UTF8);
                if (r.ReadInt32() != Magic || r.ReadInt32() != FileVersion) return false;
                int rootCount = r.ReadInt32();
                if (rootCount < 0 || rootCount > 10000) return false;
                var roots = new string[rootCount];
                for (int i = 0; i < rootCount; i++) roots[i] = r.ReadString();
                int n = r.ReadInt32();
                if (n < 0 || n > 20_000_000) return false;
                var list = new SampleEntry[n];
                for (int i = 0; i < n; i++)
                {
                    string path = r.ReadString();
                    int root = r.ReadInt32();
                    var kind = (IndexKind)r.ReadByte();
                    double seconds = r.ReadSingle();
                    int rate = r.ReadInt32();
                    int ch = r.ReadByte();
                    double bpm = r.ReadSingle();
                    string key = r.ReadString();
                    long size = r.ReadInt64(), modified = r.ReadInt64();
                    if (root < 0 || root >= rootCount) return false;
                    list[i] = new SampleEntry(path, roots[root], kind, seconds, rate, ch, bpm, key, size, modified);
                }
                if (r.ReadInt32() != EndMarker) return false;
                lock (gate)
                {
                    byPath = list.ToDictionary(e => e.Path, StringComparer.Ordinal);
                    snapshot = byPath.Values.ToArray();
                }
                Changed?.Invoke();
                return true;
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }
    }
}
