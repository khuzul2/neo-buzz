#nullable disable
// Agent server: WAV files for renders (32-bit float, so nothing over 0 dBFS is lost before it is
// measured) and for reading audio the agent asks to analyse (PCM 16/24/32 and float, mono or
// more channels; the first two channels are used).

using System;
using System.IO;
using System.Text;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Audio
{
    public sealed class StereoAudio
    {
        public required float[] Left { get; init; }
        /// <summary>Null for mono.</summary>
        public float[] Right { get; init; }
        public int Rate { get; init; }
        public int Frames => Left.Length;
        public double Seconds => Rate > 0 ? Frames / (double)Rate : 0;
    }

    public static class Wav
    {
        const short PcmFormat = 1, FloatFormat = 3;
        const ushort Extensible = 0xFFFE;

        /// <summary>Writes stereo frames (interleaved L R) as 32-bit float.</summary>
        public static void WriteStereoFloat(string path, float[] interleaved, int frames, int rate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var w = new BinaryWriter(File.Create(path));
            int dataBytes = frames * 2 * 4;
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(4 + (8 + 16) + (8 + 4) + (8 + dataBytes));
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write(FloatFormat);
            w.Write((short)2);
            w.Write(rate);
            w.Write(rate * 2 * 4);
            w.Write((short)(2 * 4));
            w.Write((short)32);
            w.Write(Encoding.ASCII.GetBytes("fact"));
            w.Write(4);
            w.Write(frames);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(dataBytes);
            var bytes = new byte[frames * 2 * 4];
            Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
            w.Write(bytes);
        }

        public static StereoAudio Read(string path)
        {
            if (!File.Exists(path)) throw new ToolError($"No file at {path}.");
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.BaseStream.Length < 12 || Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF")
                throw new ToolError($"{path} isn't a WAV file (no RIFF header). Convert it to WAV first.");
            r.ReadInt32();
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new ToolError($"{path} isn't a WAV file.");
            int format = 0, channels = 0, rate = 0, bits = 0;
            byte[] data = null;
            while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
            {
                string id = Encoding.ASCII.GetString(r.ReadBytes(4));
                int size = r.ReadInt32();
                long next = r.BaseStream.Position + size + (size & 1);
                if (id == "fmt ")
                {
                    format = (ushort)r.ReadInt16();
                    channels = r.ReadInt16();
                    rate = r.ReadInt32();
                    r.ReadInt32();
                    r.ReadInt16();
                    bits = r.ReadInt16();
                    if (format == Extensible && size >= 40)
                    {
                        r.ReadInt16();
                        r.ReadInt16();
                        r.ReadInt32();
                        format = r.ReadInt16();   // the sub-format GUID starts with the format code
                    }
                }
                else if (id == "data")
                {
                    data = r.ReadBytes((int)Math.Min(size, r.BaseStream.Length - r.BaseStream.Position));
                }
                if (next > r.BaseStream.Length) break;
                r.BaseStream.Position = next;
            }
            if (data == null || channels < 1 || rate <= 0) throw new ToolError($"{path} has no audio data or no format chunk.");
            int bytesPer = bits / 8;
            if (!(format == PcmFormat && bits is 16 or 24 or 32) && !(format == FloatFormat && bits == 32))
                throw new ToolError($"{path} is a WAV ReBuzz can't read (format {format}, {bits} bits); use 16/24/32-bit PCM or 32-bit float.");
            int frames = data.Length / (bytesPer * channels);
            var left = new float[frames];
            var right = channels > 1 ? new float[frames] : null;
            for (int f = 0; f < frames; f++)
            {
                int at = f * bytesPer * channels;
                left[f] = Sample(data, at, format, bits);
                if (right != null) right[f] = Sample(data, at + bytesPer, format, bits);
            }
            return new StereoAudio { Left = left, Right = right, Rate = rate };
        }

        static float Sample(byte[] d, int at, int format, int bits)
        {
            if (format == FloatFormat) return BitConverter.ToSingle(d, at);
            return bits switch
            {
                16 => BitConverter.ToInt16(d, at) / 32768f,
                24 => ((d[at] | (d[at + 1] << 8) | ((sbyte)d[at + 2] << 16))) / 8388608f,
                _ => BitConverter.ToInt32(d, at) / 2147483648f,
            };
        }
    }
}
