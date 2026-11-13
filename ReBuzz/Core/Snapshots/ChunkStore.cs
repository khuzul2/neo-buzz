// Fork (docs/specs/history-snapshots.md HS2): the content-addressed chunk store of a song's history folder.
// A chunk is named by the SHA-256 of its bytes and kept deflated, so a sample or wavetable that 50 snapshots
// hold is stored once. Nothing here runs on the audio thread; the snapshot service calls it from its
// background queue.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ReBuzz.Core.SongHistory
{
    internal sealed class ChunkStore
    {
        readonly string root;

        public ChunkStore(string root) { this.root = root; }

        public string Root => root;

        public static string HashOf(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        string PathOf(string hash) => Path.Combine(root, "chunks", hash.Substring(0, 2), hash);

        public bool Has(string hash) => File.Exists(PathOf(hash));

        /// <summary>Stores the chunk unless the store has it. Returns whether it was written.</summary>
        public bool Put(string hash, byte[] data, int offset, int length)
        {
            string path = PathOf(hash);
            if (File.Exists(path)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".part";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var z = new DeflateStream(fs, CompressionLevel.Fastest))
                z.Write(data, offset, length);
            File.Move(temp, path, overwrite: true);
            return true;
        }

        /// <summary>The chunk as stored (deflated), for embedding.</summary>
        public byte[] GetStored(string hash) => File.ReadAllBytes(PathOf(hash));

        /// <summary>Puts a chunk in as stored, after checking it inflates to its hash.</summary>
        public bool PutStored(string hash, byte[] stored)
        {
            string path = PathOf(hash);
            if (File.Exists(path)) return false;
            if (HashOf(Inflate(stored)) != hash) throw new InvalidDataException("A chunk doesn't match its name.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".part";
            File.WriteAllBytes(temp, stored);
            File.Move(temp, path, overwrite: true);
            return true;
        }

        public byte[] Get(string hash)
        {
            string path = PathOf(hash);
            if (!File.Exists(path)) throw new FileNotFoundException("A chunk of the snapshot is missing from the history folder: " + hash);
            return Inflate(File.ReadAllBytes(path));
        }

        static byte[] Inflate(byte[] stored)
        {
            using var z = new DeflateStream(new MemoryStream(stored), CompressionMode.Decompress);
            var o = new MemoryStream();
            z.CopyTo(o);
            return o.ToArray();
        }

        public IEnumerable<string> All()
        {
            string dir = Path.Combine(root, "chunks");
            if (!Directory.Exists(dir)) yield break;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string n = Path.GetFileName(f);
                if (n.Length == 64) yield return n;
            }
        }

        public void Delete(string hash)
        {
            try { File.Delete(PathOf(hash)); } catch (IOException) { }
        }

        public long Bytes()
        {
            long total = 0;
            foreach (var h in All())
                try { total += new FileInfo(PathOf(h)).Length; } catch (IOException) { }
            return total;
        }

        // Content-defined chunking (a gear hash, as FastCDC): cut points depend on the bytes around them, so
        // a section that changes in the middle or shifts keeps most of its chunks, and a sample inside a
        // machine's saved data is found again wherever it sits.
        const int Single = 64 * 1024, Min = 16 * 1024, Max = 256 * 1024, Mask = (1 << 16) - 1;
        static readonly ulong[] Gear = MakeGear();

        static ulong[] MakeGear()
        {
            var g = new ulong[256];
            ulong x = 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < 256; i++)
            {
                x += 0x9E3779B97F4A7C15UL;
                ulong z = x;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                g[i] = z ^ (z >> 31);
            }
            return g;
        }

        public static List<(int Offset, int Length)> Split(byte[] data)
        {
            var list = new List<(int, int)>();
            if (data.Length <= Single)
            {
                list.Add((0, data.Length));
                return list;
            }
            int start = 0;
            while (start < data.Length)
            {
                int remaining = data.Length - start;
                if (remaining <= Min) { list.Add((start, remaining)); break; }
                int limit = Math.Min(data.Length, start + Max);
                int i = start + Min;
                ulong h = 0;
                while (i < limit)
                {
                    h = (h << 1) + Gear[data[i]];
                    i++;
                    if ((h & Mask) == 0) break;
                }
                list.Add((start, i - start));
                start = i;
            }
            return list;
        }
    }
}
