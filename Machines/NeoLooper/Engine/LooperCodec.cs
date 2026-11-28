#nullable disable
// Neo Looper (docs/specs/neo-looper.md §5): how the loop's audio is kept in the song. Lossless float32, planar (left then right), the bytes of each
// float split into four planes (all the first bytes, then all the second, ...) so deflate finds the structure, deflated, then base64 for the
// machine's XML data. A SHA-256 of the raw samples is stored beside it and checked on load: a damaged loop loads as no loop, never as a bad one.

using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace NeoBuzz.Looper
{
    /// <summary>The machine's own data, kept with the song (the XML of its MachineState).</summary>
    public class LooperSaved
    {
        public int Version { get; set; }
        /// <summary>The sample rate of the loop.</summary>
        public int Rate { get; set; }
        /// <summary>Frames in the loop (0: none).</summary>
        public int Frames { get; set; }
        /// <summary>The loop's length in beats when it was taken in Sync (0: Free).</summary>
        public double LoopBeats { get; set; }
        /// <summary>The loop: shuffled, deflated, base64 (empty when there is no loop).</summary>
        public string Data { get; set; } = "";
        /// <summary>SHA-256 of the samples, hexadecimal.</summary>
        public string Hash { get; set; } = "";
    }

    internal static class LooperCodec
    {
        public const int Version = 1;

        public static byte[] Raw(float[] l, float[] r, int frames)
        {
            var b = new byte[frames * 8];
            int at = 0;
            for (int c = 0; c < 2; c++)
            {
                var x = c == 0 ? l : r;
                for (int i = 0; i < frames; i++)
                {
                    int bits = BitConverter.SingleToInt32Bits(x[i]);
                    b[at++] = (byte)bits;
                    b[at++] = (byte)(bits >> 8);
                    b[at++] = (byte)(bits >> 16);
                    b[at++] = (byte)(bits >> 24);
                }
            }
            return b;
        }

        public static string HashOf(byte[] raw)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(raw);
            var sb = new StringBuilder(h.Length * 2);
            foreach (var x in h) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static LooperSaved Encode(float[] l, float[] r, int frames, int rate, double loopBeats)
        {
            var s = new LooperSaved { Version = Version, Rate = rate, Frames = 0, LoopBeats = loopBeats };
            if (l == null || frames <= 0) return s;
            var raw = Raw(l, r ?? l, frames);
            int count = raw.Length / 4;
            var shuffled = new byte[raw.Length];
            for (int i = 0; i < count; i++)
                for (int k = 0; k < 4; k++) shuffled[k * count + i] = raw[i * 4 + k];
            using var ms = new MemoryStream();
            using (var d = new DeflateStream(ms, CompressionLevel.Optimal, true)) d.Write(shuffled, 0, shuffled.Length);
            s.Frames = frames;
            s.Data = Convert.ToBase64String(ms.ToArray());
            s.Hash = HashOf(raw);
            return s;
        }

        /// <summary>The loop of a saved state, or false when there is none or it does not check out.</summary>
        public static bool TryDecode(LooperSaved s, out float[] l, out float[] r)
        {
            l = r = null;
            try
            {
                if (s == null || s.Frames <= 0 || string.IsNullOrEmpty(s.Data)) return false;
                int count = s.Frames * 2;
                var shuffled = new byte[count * 4];
                using (var ms = new MemoryStream(Convert.FromBase64String(s.Data)))
                using (var d = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    int got = 0;
                    while (got < shuffled.Length)
                    {
                        int n = d.Read(shuffled, got, shuffled.Length - got);
                        if (n <= 0) return false;
                        got += n;
                    }
                }
                var raw = new byte[shuffled.Length];
                for (int i = 0; i < count; i++)
                    for (int k = 0; k < 4; k++) raw[i * 4 + k] = shuffled[k * count + i];
                if (!string.Equals(HashOf(raw), s.Hash, StringComparison.OrdinalIgnoreCase)) return false;
                l = new float[s.Frames];
                r = new float[s.Frames];
                for (int c = 0; c < 2; c++)
                {
                    var x = c == 0 ? l : r;
                    for (int i = 0; i < s.Frames; i++)
                    {
                        int at = (c * s.Frames + i) * 4;
                        int bits = raw[at] | raw[at + 1] << 8 | raw[at + 2] << 16 | raw[at + 3] << 24;
                        x[i] = BitConverter.Int32BitsToSingle(bits);
                    }
                }
                return true;
            }
            catch (Exception)
            {
                l = r = null;
                return false;
            }
        }
    }
}
