// Fork (docs/specs/native-format.md): the .nbl container. One file: a JSON manifest and a table of content-addressed blobs
// (SHA-256 of the raw bytes, deflated when that is smaller), then a SHA-256 of everything before it. Chunks are cut by
// the history store's chunker (ChunkStore.Split), so a sample or a machine's state cut here has the same hash as the
// same bytes in the song's embedded history: the file holds them once.
//
//   8 bytes   89 'N' 'B' 'L' 0D 0A 1A 0A
//   u32       container version (1)        u32  flags (0)
//   u64       manifest length              u64  blob count
//   manifest  UTF-8 JSON
//   index     per blob, sorted by hash: 32 bytes hash, 1 byte codec (0 raw, 1 deflate), u64 stored length
//   blobs     in index order
//   32 bytes  SHA-256 of all of the above
//
// Integers are little-endian. The bytes depend only on the content: no time stamps, no ordering that a hash table picks.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ReBuzz.Core.SongHistory;

namespace ReBuzz.FileOps.Nbl
{
    /// <summary>A reference to a blob, as the manifest writes it.</summary>
    internal readonly record struct BlobRef(string Hash, long Size);

    /// <summary>The blobs of a file being written. Add returns the hash; the same bytes are held once.</summary>
    internal sealed class NblBlobSet
    {
        readonly SortedDictionary<string, (byte Codec, byte[] Stored)> blobs = new(StringComparer.Ordinal);

        public int Count => blobs.Count;

        public IEnumerable<KeyValuePair<string, (byte Codec, byte[] Stored)>> All => blobs;

        /// <summary>One blob.</summary>
        public BlobRef Add(byte[] data) => Add(data, 0, data.Length);

        public BlobRef Add(byte[] data, int offset, int length)
        {
            string hash = ChunkStore.HashOf(new ReadOnlySpan<byte>(data, offset, length));
            if (!blobs.ContainsKey(hash))
                blobs[hash] = Compress(data, offset, length);
            return new BlobRef(hash, length);
        }

        /// <summary>Data in pieces cut where the content says (the history store's chunker): one reference per piece.</summary>
        public List<BlobRef> AddChunked(byte[] data)
        {
            var list = new List<BlobRef>();
            if (data.Length == 0) return list;
            foreach (var (off, len) in ChunkStore.Split(data))
                list.Add(Add(data, off, len));
            return list;
        }

        /// <summary>A chunk the history store holds (deflated, named by the hash of what it inflates to): taken as it is.</summary>
        public void AddStoredDeflate(string hash, byte[] stored) => AddStored(hash, 1, stored);

        /// <summary>A blob as an opened file held it (carried over from another file): codec and stored bytes.</summary>
        public void AddStored(string hash, byte codec, byte[] stored)
        {
            if (!blobs.ContainsKey(hash))
                blobs[hash] = (codec, stored);
        }

        static (byte, byte[]) Compress(byte[] data, int offset, int length)
        {
            var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, true))
                z.Write(data, offset, length);
            if (ms.Length < length) return (1, ms.ToArray());
            var raw = new byte[length];
            Buffer.BlockCopy(data, offset, raw, 0, length);
            return (0, raw);
        }
    }

    internal static class NblContainer
    {
        public static readonly byte[] Magic = { 0x89, (byte)'N', (byte)'B', (byte)'L', 0x0D, 0x0A, 0x1A, 0x0A };
        public const int Version = 1;
        const int HeaderSize = 8 + 4 + 4 + 8 + 8;

        public static bool LooksLikeOne(string path)
        {
            try
            {
                using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var head = new byte[Magic.Length];
                return f.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(Magic);
            }
            catch (IOException) { return false; }
        }

        /// <summary>The file's bytes.</summary>
        public static byte[] Build(byte[] manifest, NblBlobSet blobs)
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                w.Write(Magic);
                w.Write((uint)Version);
                w.Write((uint)0);
                w.Write((ulong)manifest.Length);
                w.Write((ulong)blobs.Count);
                w.Write(manifest);
                foreach (var b in blobs.All)
                {
                    w.Write(Convert.FromHexString(b.Key));
                    w.Write(b.Value.Codec);
                    w.Write((ulong)b.Value.Stored.Length);
                }
                foreach (var b in blobs.All)
                    w.Write(b.Value.Stored);
            }
            ms.Write(SHA256.HashData(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
            return ms.ToArray();
        }

        /// <summary>
        /// Writes the file so that a crash or a full disk leaves the old file whole: a temporary file in the same folder,
        /// flushed to the disk, then renamed over the target.
        /// </summary>
        public static void WriteAtomic(string path, byte[] manifest, NblBlobSet blobs)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            string temp = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                byte[] bytes = Build(manifest, blobs);
                using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    f.Write(bytes, 0, bytes.Length);
                    f.Flush(true);
                }
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch (IOException) { }
                throw;
            }
        }

        /// <summary>An opened file: the manifest and the blobs, read on demand.</summary>
        public sealed class Reader : IDisposable
        {
            readonly FileStream file;
            readonly Dictionary<string, (byte Codec, long Offset, long Length)> index = new();

            public byte[] Manifest { get; }
            public int ContainerVersion { get; }

            public Reader(string path)
            {
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    if (file.Length < HeaderSize + 32) throw new InvalidDataException("Not a NeoBuzz Live song (too short).");
                    var head = new byte[HeaderSize];
                    file.ReadExactly(head);
                    if (!head.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("Not a NeoBuzz Live song.");
                    ContainerVersion = (int)BitConverter.ToUInt32(head, 8);
                    if (ContainerVersion > Version) throw new InvalidDataException($"This song needs a newer NeoBuzz Live (container version {ContainerVersion}).");
                    ulong manifestLength = BitConverter.ToUInt64(head, 16), count = BitConverter.ToUInt64(head, 24);
                    long indexBytes = checked((long)count * 41);
                    if (manifestLength > (ulong)file.Length || indexBytes > file.Length) throw new InvalidDataException("The song file is damaged (sizes).");
                    Manifest = new byte[manifestLength];
                    file.ReadExactly(Manifest);
                    var idx = new byte[indexBytes];
                    file.ReadExactly(idx);
                    long offset = HeaderSize + (long)manifestLength + indexBytes;
                    for (int i = 0; i < (long)count; i++)
                    {
                        string hash = Convert.ToHexString(idx.AsSpan(i * 41, 32)).ToLowerInvariant();
                        long len = checked((long)BitConverter.ToUInt64(idx, i * 41 + 33));
                        index[hash] = (idx[i * 41 + 32], offset, len);
                        offset = checked(offset + len);
                    }
                    if (offset + 32 != file.Length) throw new InvalidDataException("The song file is damaged (it is cut short or has extra bytes).");
                    VerifyTrailer();
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }

            void VerifyTrailer()
            {
                file.Position = 0;
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long left = file.Length - 32;
                while (left > 0)
                {
                    int n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0) throw new EndOfStreamException();
                    sha.AppendData(buffer, 0, n);
                    left -= n;
                }
                var expected = new byte[32];
                file.ReadExactly(expected);
                if (!sha.GetHashAndReset().AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException("The song file is damaged (its checksum doesn't match).");
            }

            public bool Has(string hash) => index.ContainsKey(hash);

            public IEnumerable<string> Hashes => index.Keys;

            /// <summary>The blob's bytes as stored (what the history store keeps for a chunk).</summary>
            public (byte Codec, byte[] Stored) GetStored(string hash)
            {
                if (!index.TryGetValue(hash, out var e)) throw new InvalidDataException("The song file is missing a part: " + hash);
                file.Position = e.Offset;
                var stored = new byte[e.Length];
                file.ReadExactly(stored);
                return (e.Codec, stored);
            }

            /// <summary>The blob's bytes, checked against its name.</summary>
            public byte[] Get(string hash)
            {
                var (codec, stored) = GetStored(hash);
                byte[] raw = stored;
                if (codec == 1)
                {
                    using var z = new DeflateStream(new MemoryStream(stored), CompressionMode.Decompress);
                    var o = new MemoryStream();
                    z.CopyTo(o);
                    raw = o.ToArray();
                }
                else if (codec != 0) throw new InvalidDataException("The song file uses a storage method this version doesn't know.");
                if (ChunkStore.HashOf(raw) != hash) throw new InvalidDataException("A part of the song file doesn't match its name: " + hash);
                return raw;
            }

            public void Dispose() => file.Dispose();
        }
    }
}
