#nullable disable
// Fork (docs/specs/neo-audio.md, NA1b): the decoded audio cache. A long asset is kept on disk as float32 in the engine's format (the song's
// rate, one or two channels), so the engine reads it from a file through a prefetch thread instead of holding it in memory.
//
// One file per asset, named by the asset's id (the SHA-256 of its rate, channels and samples) so it is shared by every song that holds the
// same audio and survives a reopen. Layout (little endian):
//   0   "NBDC0001"                 magic
//   8   u32 version (1), u32 channels, u32 sample rate, u32 page frames, u64 frames, u32 page count
//   40  32 bytes of the id (ASCII, zero padded), u64 data offset, u32 CRC of the table, u32 CRC of bytes 0..83
//   128 the CRC-32 of every piece (page, channel) as u32, page major
//   data offset (4096 aligned): the pages; a page is its channels' pieces one after the other, each PageFrames floats (the last page
//       padded with zeros), so one read fetches a page.
// A file is written as "name.part" and renamed when complete, so a file under its real name is whole; each piece's CRC is checked when it is
// read, so a file damaged since is found and rebuilt. Pure: ReBuzz.SessionTests links this file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ReBuzz.Core.AudioLibrary
{
    /// <summary>CRC-32 (the zlib polynomial), for the cache's pieces.</summary>
    internal static class Crc32
    {
        static readonly uint[] table = Make();

        static uint[] Make()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        public static uint Of(ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            var t = table;
            foreach (byte b in data) c = t[(c ^ b) & 0xFF] ^ (c >> 8);
            return ~c;
        }
    }

    /// <summary>The settings of disk streaming (neo-audio.md, NA1b). Read from the preferences file on first use.</summary>
    internal static class StreamingPolicy
    {
        /// <summary>Assets of at most this many bytes of floats stay in memory (8 MiB is about 22 seconds of stereo at 48 kHz).</summary>
        public static long ThresholdBytes { get; set; } = 8L * 1024 * 1024;

        /// <summary>The start of every streamed asset, and of each loop start and warp marker a clip uses, that is always resident.</summary>
        public static int HeadMilliseconds { get; set; } = 500;

        /// <summary>How far ahead of a voice the prefetch thread keeps audio, in seconds of the voice's own playing time.</summary>
        public static double ReadAheadSeconds { get; set; } = 2.0;

        /// <summary>The most memory the pages of all streamed assets keep once they are not needed (bytes); wanted pages are never evicted.</summary>
        public static long ResidentBytes { get; set; } = 96L * 1024 * 1024;

        /// <summary>The cache folder's size limit (bytes); files the open song does not use go oldest first.</summary>
        public static long CacheLimitBytes { get; set; } = 4L * 1024 * 1024 * 1024;

        /// <summary>Turns streaming off (every asset stays in memory): the safe switch.</summary>
        public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("NEO_STREAMING_OFF") != "1";

        public static bool ShouldStream(long frames, int channels) =>
            Enabled && frames * Math.Max(1, channels) * 4L > ThresholdBytes;

        static string SettingsFile => Path.Combine(AudioCache.DataFolder, "streaming.txt");

        /// <summary>Loads the preferences file (key=value lines); a missing or damaged one leaves the defaults.</summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (var line in File.ReadAllLines(SettingsFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d) || d < 0) continue;
                    switch (k)
                    {
                        case "thresholdMB": ThresholdBytes = (long)(d * 1024 * 1024); break;
                        case "headMs": HeadMilliseconds = (int)Math.Clamp(d, 0, 10000); break;
                        case "readAheadSeconds": ReadAheadSeconds = Math.Clamp(d, 0.25, 30); break;
                        case "residentMB": ResidentBytes = (long)(Math.Max(8, d) * 1024 * 1024); break;
                        case "cacheLimitGB": CacheLimitBytes = (long)(d * 1024 * 1024 * 1024); break;
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
                var c = System.Globalization.CultureInfo.InvariantCulture;
                File.WriteAllLines(SettingsFile, new[]
                {
                    "thresholdMB=" + (ThresholdBytes / 1048576.0).ToString(c),
                    "headMs=" + HeadMilliseconds.ToString(c),
                    "readAheadSeconds=" + ReadAheadSeconds.ToString(c),
                    "residentMB=" + (ResidentBytes / 1048576.0).ToString(c),
                    "cacheLimitGB=" + (CacheLimitBytes / 1073741824.0).ToString(c),
                });
            }
            catch (Exception) { }
        }
    }

    /// <summary>The cache folder: where the files are, what they hold, and the trimming of them.</summary>
    internal static class AudioCache
    {
        public const int PageShift = 13;
        public const int PageFrames = 1 << PageShift;
        public const int PageMask = PageFrames - 1;
        internal const int HeaderBytes = 128;
        internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("NBDC0001");

        /// <summary>Where the app keeps what it makes for the user (a test sets another).</summary>
        public static string DataFolder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "NeoBuzz", "AudioCache");

        public static string Folder => DataFolder;

        public static string PathFor(string id) => Path.Combine(Folder, Sanitise(id) + ".nbdc");

        static string Sanitise(string s) => new string((s ?? "").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

        public static int PagesOf(long frames) => (int)((frames + PageFrames - 1) >> PageShift);

        /// <summary>The bytes the cache files take.</summary>
        public static long SizeBytes()
        {
            try
            {
                if (!Directory.Exists(Folder)) return 0;
                return new DirectoryInfo(Folder).EnumerateFiles("*.nbdc").Sum(f => f.Length);
            }
            catch (Exception) { return 0; }
        }

        public static int FileCount()
        {
            try { return Directory.Exists(Folder) ? new DirectoryInfo(Folder).EnumerateFiles("*.nbdc").Count() : 0; }
            catch (Exception) { return 0; }
        }

        /// <summary>The ids of the files the open song streams from (<see cref="StreamedAudio"/>s are registered while open).</summary>
        static HashSet<string> InUse() => StreamPrefetcher.OpenIds();

        /// <summary>Removes every file the open song is not using (and partial files). Returns the bytes freed.</summary>
        public static long Clear()
        {
            long freed = 0;
            try
            {
                if (!Directory.Exists(Folder)) return 0;
                var keep = InUse();
                foreach (var f in new DirectoryInfo(Folder).EnumerateFiles().ToList())
                {
                    if (f.Extension == ".nbdc" && keep.Contains(System.IO.Path.GetFileNameWithoutExtension(f.Name))) continue;
                    if (f.Extension != ".nbdc" && f.Extension != ".part") continue;
                    try { long n = f.Length; f.Delete(); freed += n; } catch (IOException) { }
                }
            }
            catch (Exception) { }
            return freed;
        }

        /// <summary>Trims the folder to the size limit: files the open song does not use, least recently used first.</summary>
        public static void Trim(long limit = -1)
        {
            try
            {
                if (limit < 0) limit = StreamingPolicy.CacheLimitBytes;
                if (!Directory.Exists(Folder)) return;
                var keep = InUse();
                var files = new DirectoryInfo(Folder).EnumerateFiles("*.nbdc").ToList();
                long total = files.Sum(f => f.Length);
                foreach (var f in files.Where(f => !keep.Contains(System.IO.Path.GetFileNameWithoutExtension(f.Name))).OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= limit) break;
                    try { long n = f.Length; f.Delete(); total -= n; } catch (IOException) { }
                }
            }
            catch (Exception) { }
        }

        /// <summary>Marks a file as used now (the LRU order of the trimming).</summary>
        internal static void Touch(string path)
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (Exception) { }
        }

        /// <summary>Whether a file for this audio exists and is whole (header, sizes); it is opened by <see cref="StreamedAudio.Open"/>.</summary>
        public static bool Exists(string id, long frames, int channels, int rate) => StreamedAudio.IsWhole(id, frames, channels, rate);

        /// <summary>Writes a cache file from audio in memory (or any source of pieces): see <see cref="Writer"/>.</summary>
        public sealed class Writer : IDisposable
        {
            readonly string finalPath, partPath;
            readonly string id;
            readonly int channels, rate, pages;
            readonly long frames;
            readonly uint[] crcs;
            readonly bool[] done;
            SafeFileHandle handle;
            long dataOffset;
            readonly float[] pad = new float[PageFrames];
            bool completed;

            public Writer(string id, long frames, int channels, int rate)
            {
                this.id = id;
                this.frames = frames;
                this.channels = Math.Clamp(channels, 1, 2);
                this.rate = rate;
                pages = PagesOf(frames);
                crcs = new uint[pages * this.channels];
                done = new bool[pages * this.channels];
                Directory.CreateDirectory(Folder);
                finalPath = PathFor(id);
                partPath = Path.Combine(Folder, Sanitise(id) + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part");
                handle = File.OpenHandle(partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.None);
                dataOffset = (HeaderBytes + 4L * crcs.Length + 4095) & ~4095L;
                // Reserve the file's size so a full disk fails now.
                RandomAccess.SetLength(handle, dataOffset + (long)pages * this.channels * PageFrames * 4);
            }

            /// <summary>Writes the piece of a page of one channel: up to <see cref="PageFrames"/> floats (shorter, the last page, is padded with zeros).</summary>
            public void WritePiece(int channel, int page, ReadOnlySpan<float> data)
            {
                if (page < 0 || page >= pages || channel < 0 || channel >= channels) throw new ArgumentOutOfRangeException();
                ReadOnlySpan<float> piece = data;
                if (data.Length != PageFrames)
                {
                    if (data.Length > PageFrames) throw new ArgumentException("A piece is at most one page.");
                    data.CopyTo(pad);
                    pad.AsSpan(data.Length).Clear();
                    piece = pad;
                }
                var bytes = MemoryMarshal.AsBytes(piece);
                int index = page * channels + channel;
                crcs[index] = Crc32.Of(bytes);
                RandomAccess.Write(handle, bytes, dataOffset + ((long)page * channels + channel) * PageFrames * 4);
                done[index] = true;
            }

            /// <summary>Writes the whole audio from two arrays (the right one ignored for mono).</summary>
            public void WriteAll(float[] left, float[] right, IProgress<double> progress = null, CancellationToken cancel = default)
            {
                for (int p = 0; p < pages; p++)
                {
                    cancel.ThrowIfCancellationRequested();
                    int from = p << PageShift, n = (int)Math.Min(PageFrames, frames - from);
                    WritePiece(0, p, left.AsSpan(from, n));
                    if (channels > 1) WritePiece(1, p, right.AsSpan(from, n));
                    if ((p & 15) == 0) progress?.Report((p + 1) / (double)pages);
                }
            }

            /// <summary>Finishes the file and gives it its real name. False if a piece is missing.</summary>
            public bool Complete()
            {
                if (done.Any(d => !d)) return false;
                var head = new byte[HeaderBytes];
                Magic.CopyTo(head, 0);
                BitConverter.TryWriteBytes(head.AsSpan(8), 1u);
                BitConverter.TryWriteBytes(head.AsSpan(12), (uint)channels);
                BitConverter.TryWriteBytes(head.AsSpan(16), (uint)rate);
                BitConverter.TryWriteBytes(head.AsSpan(20), (uint)PageFrames);
                BitConverter.TryWriteBytes(head.AsSpan(24), (ulong)frames);
                BitConverter.TryWriteBytes(head.AsSpan(32), (uint)pages);
                var idBytes = Encoding.ASCII.GetBytes(id.Length > 32 ? id.Substring(0, 32) : id);
                idBytes.CopyTo(head, 40);
                BitConverter.TryWriteBytes(head.AsSpan(72), (ulong)dataOffset);
                var table = new byte[crcs.Length * 4];
                for (int i = 0; i < crcs.Length; i++) BitConverter.TryWriteBytes(table.AsSpan(i * 4), crcs[i]);
                BitConverter.TryWriteBytes(head.AsSpan(80), Crc32.Of(table));
                BitConverter.TryWriteBytes(head.AsSpan(84), Crc32.Of(head.AsSpan(0, 84)));
                RandomAccess.Write(handle, head, 0);
                RandomAccess.Write(handle, table, HeaderBytes);
                RandomAccess.FlushToDisk(handle);
                handle.Dispose();
                handle = null;
                File.Move(partPath, finalPath, true);
                completed = true;
                return true;
            }

            public void Dispose()
            {
                handle?.Dispose();
                handle = null;
                if (!completed)
                    try { File.Delete(partPath); } catch (Exception) { }
            }
        }
    }
}
