#nullable disable
// Fork (docs/specs/stem-separation.md): separated stems kept on disk per (audio's content id, model), so separating the same audio with
// the same model again is a file read. One folder per key under StemModels.DataFolder/cache, one raw float file per stem (left, then
// right), trimmed to a size limit by age. A cache that can't be read or written is only a cache that misses.

using System;
using System.IO;
using System.Linq;

namespace ReBuzz.Core.AudioLibrary.Stems
{
    internal static class StemCache
    {
        /// <summary>The most the cache keeps (bytes); the oldest results go first.</summary>
        public static long LimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;

        public static string Root => Path.Combine(StemModels.DataFolder, "cache");

        public static string Folder(string key, StemModelManifest model) =>
            Path.Combine(Root, Sanitise(key) + "-" + Sanitise(model.Id) + "-r" + Sanitise(model.Revision));

        static string Sanitise(string s) => new string((s ?? "").Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());

        public static bool TryLoad(string key, StemModelManifest model, string[] stems, int frames, int rate, StemResult into)
        {
            try
            {
                string dir = Folder(key, model);
                foreach (var stem in stems)
                {
                    string file = Path.Combine(dir, stem + ".f32");
                    if (!File.Exists(file)) return false;
                    var info = new FileInfo(file);
                    long want = (long)frames * 2 * sizeof(float);
                    if (info.Length != want) return false;
                    var bytes = File.ReadAllBytes(file);
                    var l = new float[frames];
                    var r = new float[frames];
                    Buffer.BlockCopy(bytes, 0, l, 0, frames * sizeof(float));
                    Buffer.BlockCopy(bytes, frames * sizeof(float), r, 0, frames * sizeof(float));
                    into.Stems[stem] = (l, r);
                }
                foreach (var stem in stems) File.SetLastAccessTimeUtc(Path.Combine(dir, stem + ".f32"), DateTime.UtcNow);
                return true;
            }
            catch (Exception)
            {
                into.Stems.Clear();
                return false;
            }
        }

        public static void Save(string key, StemModelManifest model, StemResult result, int rate)
        {
            try
            {
                string dir = Folder(key, model);
                Directory.CreateDirectory(dir);
                foreach (var (stem, (l, r)) in result.Stems)
                {
                    var bytes = new byte[l.Length * 2 * sizeof(float)];
                    Buffer.BlockCopy(l, 0, bytes, 0, l.Length * sizeof(float));
                    Buffer.BlockCopy(r, 0, bytes, l.Length * sizeof(float), l.Length * sizeof(float));
                    string temp = Path.Combine(dir, stem + ".f32.tmp");
                    File.WriteAllBytes(temp, bytes);
                    File.Move(temp, Path.Combine(dir, stem + ".f32"), true);
                }
                Trim();
            }
            catch (Exception) { /* a cache that can't be written misses next time */ }
        }

        /// <summary>Deletes the oldest results until the cache is within its limit.</summary>
        public static void Trim()
        {
            if (!Directory.Exists(Root)) return;
            var dirs = new DirectoryInfo(Root).GetDirectories().Select(d => (Dir: d, Size: d.EnumerateFiles().Sum(f => f.Length),
                Time: d.EnumerateFiles().Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max())).OrderBy(x => x.Time).ToList();
            long total = dirs.Sum(x => x.Size);
            foreach (var x in dirs)
            {
                if (total <= LimitBytes) break;
                try { x.Dir.Delete(true); total -= x.Size; } catch (Exception) { }
            }
        }

        public static void Clear()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch (Exception) { }
        }
    }
}
