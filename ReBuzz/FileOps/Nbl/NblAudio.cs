// Fork (docs/specs/neo-audio.md, NA1; native-format.md section 12): the song's audio assets in the .nbl. The manifest's "audio" member lists
// them (id, name, source path, SHA-256 of the source, rate, channels, frames); the samples are blobs of the shared table: planar float32
// (little-endian), byte-shuffled (all first bytes, then all second bytes…, which is what makes deflate work on float audio), cut with the
// same content-defined chunker as every other blob, so audio held twice (a clip copied, a file imported twice, a snapshot) is stored once.
// Reading gives back the same bits.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                result.Add(asset);
            }
            return result;
        }

        /// <summary>Planar float32, byte-shuffled: byte k of every float together.</summary>
        internal static byte[] Pack(AudioSource s, int channels)
        {
            int frames = s.Length;
            var planes = channels > 1 ? new[] { s.Left, s.Right } : new[] { s.Left };
            var data = new byte[(long)frames * 4 * planes.Length];
            long n = (long)frames * planes.Length;
            long i = 0;
            foreach (var plane in planes)
                for (int f = 0; f < frames; f++, i++)
                {
                    uint bits = (uint)BitConverter.SingleToInt32Bits(plane[f]);
                    data[i] = (byte)bits;
                    data[n + i] = (byte)(bits >> 8);
                    data[2 * n + i] = (byte)(bits >> 16);
                    data[3 * n + i] = (byte)(bits >> 24);
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
}
