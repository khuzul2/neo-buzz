#nullable disable
// Agent server (docs/specs/mcp-server.md §5.7 plot): a minimal PNG encoder, so plots need no
// System.Drawing (absent on Linux). 8-bit RGB, one IDAT chunk deflated by ZLibStream, each row
// with the Sub filter (flat plot colours and gradients compress well with it).

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ReBuzz.Agent.Audio
{
    internal static class Png
    {
        public static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        static readonly uint[] CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        /// <summary>The CRC-32 PNG chunks carry (over the chunk type and data).</summary>
        public static uint Crc(byte[] data, int offset, int count, uint crc = 0xFFFFFFFFu)
        {
            for (int i = offset; i < offset + count; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        /// <summary>Encodes <paramref name="rgb"/> (width × height × 3 bytes, rows top to bottom) as a PNG file.</summary>
        public static byte[] Encode(int width, int height, byte[] rgb)
        {
            if (rgb.Length != width * height * 3) throw new ArgumentException("width × height × 3 bytes expected", nameof(rgb));
            using var file = new MemoryStream();
            file.Write(Signature);
            var header = new byte[13];
            BigEndian(header, 0, (uint)width);
            BigEndian(header, 4, (uint)height);
            header[8] = 8;    // bits per channel
            header[9] = 2;    // RGB
            Chunk(file, "IHDR", header);

            int stride = width * 3;
            var raw = new byte[height * (stride + 1)];
            for (int y = 0; y < height; y++)
            {
                int row = y * (stride + 1), src = y * stride;
                raw[row] = 1;   // Sub: each byte minus the same channel of the pixel to its left
                for (int i = 0; i < stride; i++) raw[row + 1 + i] = (byte)(rgb[src + i] - (i >= 3 ? rgb[src + i - 3] : 0));
            }
            using (var compressed = new MemoryStream())
            {
                using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw, 0, raw.Length);
                Chunk(file, "IDAT", compressed.ToArray());
            }
            Chunk(file, "IEND", Array.Empty<byte>());
            return file.ToArray();
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var head = new byte[8];
            BigEndian(head, 0, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            s.Write(head);
            s.Write(data);
            uint crc = Crc(head, 4, 4);
            crc = Crc(data, 0, data.Length, crc) ^ 0xFFFFFFFFu;
            var tail = new byte[4];
            BigEndian(tail, 0, crc);
            s.Write(tail);
        }

        static void BigEndian(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }
    }
}
