// Fork (docs/specs/neo-audio.md, NA1; native-format.md section 12): the song's audio assets in the .nbl. The manifest's "audio" member lists
// them (id, name, source path, SHA-256 of the source, rate, channels, frames); the samples are blobs of the shared table: planar float32
// (little-endian), byte-shuffled (all first bytes, then all second bytes…, which is what makes deflate work on float audio), cut with the
// same content-defined chunker as every other blob, so audio held twice (a clip copied, a file imported twice, a snapshot) is stored once.
// Reading gives back the same bits.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Clips;

namespace ReBuzz.FileOps.Nbl
{
    internal static class NblAudio
    {
        public const string Member = "audio";
        public const string Codec = "pcm-f32-shuffle-v1";

        /// <summary>The "audio" member for these assets, their samples added to <paramref name="blobs"/>; null when there are none.</summary>
        public static JsonObject Build(IEnumerable<AudioAsset> assets, NblBlobSet blobs)
        {
            var list = new JsonArray();
            foreach (var a in assets.OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                var data = Pack(a.Source, a.Channels);
                var chunks = new JsonArray();
                foreach (var r in blobs.AddChunked(data)) chunks.Add(new JsonObject { ["blob"] = r.Hash, ["size"] = r.Size });
                list.Add(new JsonObject
                {
                    ["id"] = a.Id,
                    ["name"] = a.Name,
                    ["path"] = a.SourcePath,
                    ["sha256"] = a.SourceSha256,
                    ["sampleRate"] = a.SampleRate,
                    ["channels"] = a.Channels,
                    ["frames"] = a.Frames,
                    ["chunks"] = chunks,
                });
            }
            return list.Count == 0 ? null : new JsonObject { ["codec"] = Codec, ["assets"] = list };
        }

        /// <summary>The assets of a manifest's "audio" member (none when it has none). Throws on damage.</summary>
        public static List<AudioAsset> Read(JsonObject manifest, NblReadContext ctx)
        {
            var result = new List<AudioAsset>();
            if (manifest["audio"] is not JsonObject audio) return result;
            if ((string)audio["codec"] != Codec) throw new InvalidDataException("The song's audio uses a storage this version doesn't know: " + audio["codec"]);
            foreach (var node in audio["assets"] as JsonArray ?? new JsonArray())
            {
                var e = (JsonObject)node;
                int channels = Math.Clamp((int?)e["channels"] ?? 1, 1, 2);
                int rate = (int?)e["sampleRate"] ?? 44100;
                long frames = (long?)e["frames"] ?? 0;
                string id = (string)e["id"];
                // Long audio whose decoded cache file is already there (it survives a reopen) is not decoded: it streams from the file.
                if (StreamingPolicy.ShouldStream(frames, channels) && frames > 0)
                {
                    var cached = StreamedAudio.Open(id, frames, channels, rate);
                    if (cached != null)
                    {
                        var streamed = new AudioAsset((string)e["name"], (string)e["path"], (string)e["sha256"], id, cached, channels);
                        if (ctx.Path != null) streamed.Recipe = new NblRecipe(ctx.Path, id);
                        result.Add(streamed);
                        continue;
                    }
                }
                var ms = new MemoryStream();
                foreach (var c in e["chunks"] as JsonArray ?? new JsonArray())
                {
                    var b = ctx.Get((string)c["blob"]);
                    ms.Write(b, 0, b.Length);
                }
                var (left, right) = Unpack(ms.ToArray(), frames, channels);
                var asset = new AudioAsset((string)e["name"], (string)e["path"], (string)e["sha256"], new AudioSource(left, right, rate), channels);
                if (asset.Id != (string)e["id"])
                    throw new InvalidDataException($"The audio '{(string)e["name"]}' of the song doesn't match its id (the file is damaged).");
                if (ctx.Path != null) asset.Recipe = new NblRecipe(ctx.Path, asset.Id);
                result.Add(asset);
            }
            return result;
        }

        /// <summary>Planar float32, byte-shuffled: byte k of every float together. Read exactly (from memory or the cache file), a piece at a time.</summary>
        internal static byte[] Pack(AudioSource s, int channels)
        {
            int frames = s.Length;
            var data = new byte[(long)frames * 4 * channels];
            long n = (long)frames * channels;
            long i = 0;
            var piece = new float[1 << 16];
            for (int c = 0; c < channels; c++)
                for (int from = 0; from < frames; from += piece.Length)
                {
                    int m = Math.Min(piece.Length, frames - from);
                    s.CopyExact(c, from, piece.AsSpan(0, m));
                    for (int f = 0; f < m; f++, i++)
                    {
                        uint bits = (uint)BitConverter.SingleToInt32Bits(piece[f]);
                        data[i] = (byte)bits;
                        data[n + i] = (byte)(bits >> 8);
                        data[2 * n + i] = (byte)(bits >> 16);
                        data[3 * n + i] = (byte)(bits >> 24);
                    }
                }
            return data;
        }

        internal static (float[] Left, float[] Right) Unpack(byte[] data, long frames, int channels)
        {
            long n = frames * channels;
            if (data.LongLength != n * 4) throw new InvalidDataException("An audio asset of the song has the wrong length.");
            var left = new float[frames];
            var right = channels > 1 ? new float[frames] : left;
            for (long i = 0; i < n; i++)
            {
                uint bits = data[i] | ((uint)data[n + i] << 8) | ((uint)data[2 * n + i] << 16) | ((uint)data[3 * n + i] << 24);
                float v = BitConverter.Int32BitsToSingle((int)bits);
                if (i < frames) left[i] = v; else right[i - frames] = v;
            }
            return (left, right);
        }
    }

    /// <summary>
    /// Makes an asset's decoded cache file again from the song file's blobs (neo-audio.md, NA1b), without holding the audio in memory: the
    /// shuffled bytes of the asset are read plane by plane, a page at a time, through a small cache of the blobs, and the id is checked on the way.
    /// </summary>
    internal sealed class NblRecipe : AssetRecipe
    {
        readonly string path, id;

        public NblRecipe(string path, string id) { this.path = path; this.id = id; }

        public string Path => path;
        public override string Describe => "the song file " + path;
        public override bool Available => File.Exists(path);

        public override bool Rebuild(AudioCache.Writer writer, AudioAsset asset, System.Threading.CancellationToken cancel)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using var reader = new NblContainer.Reader(path);
                var manifest = JsonNode.Parse(reader.Manifest) as JsonObject;
                var ctx = new NblReadContext(reader) { Path = path };
                NblMigrations.Default.Migrate(manifest, ctx);
                var entry = (manifest?["audio"]?["assets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(e => (string)e["id"] == id);
                if (entry == null) return false;
                int channels = Math.Clamp((int?)entry["channels"] ?? 1, 1, 2), rate = (int?)entry["sampleRate"] ?? 44100;
                long frames = (long?)entry["frames"] ?? 0;
                if (frames != asset.Frames || channels != asset.Channels || rate != asset.SampleRate) return false;
                var chunks = new ChunkedBytes(ctx, entry["chunks"] as JsonArray ?? new JsonArray());
                long n = frames * channels;
                if (chunks.Length != n * 4) return false;

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(BitConverter.GetBytes(rate));
                hash.AppendData(BitConverter.GetBytes(channels));
                var planes = new byte[4][];
                for (int b = 0; b < 4; b++) planes[b] = new byte[AudioCache.PageFrames];
                var piece = new float[AudioCache.PageFrames];
                int pages = AudioCache.PagesOf(frames);
                for (int c = 0; c < channels; c++)
                    for (int p = 0; p < pages; p++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        long from = (long)p * AudioCache.PageFrames;
                        int m = (int)Math.Min(AudioCache.PageFrames, frames - from);
                        long index = c * frames + from;                  // the float's index in the planar stream
                        for (int b = 0; b < 4; b++) chunks.Read(b * n + index, planes[b].AsSpan(0, m));
                        for (int f = 0; f < m; f++)
                        {
                            uint bits = planes[0][f] | ((uint)planes[1][f] << 8) | ((uint)planes[2][f] << 16) | ((uint)planes[3][f] << 24);
                            float v = BitConverter.Int32BitsToSingle((int)bits);
                            piece[f] = float.IsFinite(v) ? v : 0f;         // as AudioSource silences them
                        }
                        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(piece.AsSpan(0, m)));
                        writer.WritePiece(c, p, piece.AsSpan(0, m));
                    }
                string made = "a" + Convert.ToHexString(hash.GetHashAndReset()).Substring(0, 16).ToLowerInvariant();
                // A damaged float is silenced above, which changes the hash only for audio the store would not have accepted either.
                return made == id;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The bytes of an asset's chunks as one stream, read at any offset (a few decoded blobs are kept).</summary>
        sealed class ChunkedBytes
        {
            readonly NblReadContext ctx;
            readonly string[] hashes;
            readonly long[] starts;
            readonly Dictionary<int, byte[]> cache = new();
            readonly Queue<int> order = new();
            public long Length { get; }

            public ChunkedBytes(NblReadContext ctx, JsonArray chunks)
            {
                this.ctx = ctx;
                hashes = chunks.Select(c => (string)c["blob"]).ToArray();
                starts = new long[hashes.Length + 1];
                for (int i = 0; i < hashes.Length; i++) starts[i + 1] = starts[i] + (long)chunks[i]["size"];
                Length = starts[^1];
            }

            public void Read(long offset, Span<byte> into)
            {
                int done = 0;
                while (done < into.Length)
                {
                    long at = offset + done;
                    int k = Array.BinarySearch(starts, at);
                    if (k < 0) k = ~k - 1;
                    if (k >= hashes.Length) throw new InvalidDataException("An audio asset of the song has the wrong length.");
                    var blob = Blob(k);
                    int within = (int)(at - starts[k]);
                    int take = Math.Min(into.Length - done, blob.Length - within);
                    blob.AsSpan(within, take).CopyTo(into.Slice(done));
                    done += take;
                }
            }

            byte[] Blob(int k)
            {
                if (cache.TryGetValue(k, out var b)) return b;
                b = ctx.Get(hashes[k]);
                cache[k] = b;
                order.Enqueue(k);
                while (order.Count > 24) cache.Remove(order.Dequeue());
                return b;
            }
        }
    }
}
