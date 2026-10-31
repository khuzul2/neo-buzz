#nullable disable
// Neo Wave (docs/specs/machines.md §5): reading wavetables from WAV files. PCM 8/16/24/32-bit or
// 32/64-bit float, any channel count (mixed to mono). Frames: from a "clm " chunk (Serum and
// others write "<!>2048 …"), else 2048 samples each when the length is a multiple of 2048, else
// the whole file is one cycle.

using System;
using System.IO;
using System.Text;
using NeoBuzz.Dsp;

namespace NeoBuzz.Wave
{
    internal static class WavReader
    {
        /// <summary>Mono samples and the frame size the file declares (0 when it doesn't).</summary>
        public static (float[] Samples, int FrameSize) Read(byte[] data)
        {
            using var br = new BinaryReader(new MemoryStream(data));
            if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") throw new InvalidDataException("not a WAV file (no RIFF header)");
            br.ReadInt32();
            if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE") throw new InvalidDataException("not a WAV file (no WAVE tag)");
            int format = 0, channels = 0, bits = 0, frameSize = 0;
            float[] samples = null;
            while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
            {
                string id = Encoding.ASCII.GetString(br.ReadBytes(4));
                // Streaming writers leave the size unset (0xFFFFFFFF) and files get cut short: trust only what is there.
                long remaining = br.BaseStream.Length - br.BaseStream.Position - 4;
                uint declared = br.ReadUInt32();
                int size = (int)Math.Min(declared, (ulong)Math.Max(0, remaining));
                long next = br.BaseStream.Position + size + (size & 1);
                if (id == "fmt ")
                {
                    format = br.ReadInt16();
                    channels = br.ReadInt16();
                    br.ReadInt32();   // sample rate
                    br.ReadInt32();
                    br.ReadInt16();
                    bits = br.ReadInt16();
                    if (format == unchecked((short)0xFFFE) && size >= 26)
                    {
                        br.ReadInt16();
                        br.ReadInt16();
                        br.ReadInt32();
                        format = br.ReadInt16();   // the sub-format's first field: 1 PCM, 3 float
                    }
                }
                else if (id == "clm ")
                {
                    string text = Encoding.ASCII.GetString(br.ReadBytes(Math.Min(size, 64)));
                    int start = text.IndexOf("<!>", StringComparison.Ordinal);
                    if (start >= 0)
                    {
                        var digits = new StringBuilder();
                        for (int i = start + 3; i < text.Length && char.IsDigit(text[i]); i++) digits.Append(text[i]);
                        if (int.TryParse(digits.ToString(), out int fs) && fs >= 16) frameSize = fs;
                    }
                }
                else if (id == "data")
                {
                    if (channels <= 0 || bits <= 0) throw new InvalidDataException("data before format");
                    int bytesPer = bits / 8;
                    if (bytesPer <= 0) throw new InvalidDataException($"{bits}-bit samples aren't supported");
                    int count = size / (bytesPer * channels);
                    samples = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        double sum = 0;
                        for (int c = 0; c < channels; c++) sum += ReadSample(br, format, bits);
                        samples[i] = (float)(sum / channels);
                    }
                }
                br.BaseStream.Position = Math.Min(next, br.BaseStream.Length);
            }
            if (samples == null || samples.Length < 16) throw new InvalidDataException("no audio in the file");
            return (samples, frameSize);
        }

        static double ReadSample(BinaryReader br, int format, int bits)
        {
            if (format == 3) return bits == 64 ? br.ReadDouble() : br.ReadSingle();
            switch (bits)
            {
                case 8: return (br.ReadByte() - 128) / 128.0;
                case 16: return br.ReadInt16() / 32768.0;
                case 24:
                {
                    int b0 = br.ReadByte(), b1 = br.ReadByte(), b2 = (sbyte)br.ReadByte();
                    return ((b2 << 16) | (b1 << 8) | b0) / 8388608.0;
                }
                case 32: return br.ReadInt32() / 2147483648.0;
                default: throw new InvalidDataException($"{bits}-bit samples aren't supported");
            }
        }

        /// <summary>Splits a file's audio into single-cycle frames and builds a table from them.</summary>
        public static Wavetable ToTable(string name, float[] samples, int frameSize)
        {
            int size = frameSize > 0 ? frameSize
                : samples.Length >= 2 * Wavetable.FrameSize && samples.Length % Wavetable.FrameSize == 0 ? Wavetable.FrameSize
                : samples.Length;
            int count = Math.Clamp(samples.Length / size, 1, Wavetable.MaxFrames);
            var frames = new float[count][];
            for (int f = 0; f < count; f++)
            {
                frames[f] = new float[size];
                Array.Copy(samples, f * size, frames[f], 0, Math.Min(size, samples.Length - f * size));
            }
            return Wavetable.FromFrames(name, frames);
        }

        /// <summary>A WAV file (16-bit-free: 32-bit float) of frames, for tests and for saving tables.</summary>
        public static byte[] Write(float[] samples, int sampleRate = 48000, int frameSize = 0)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            byte[] clm = frameSize > 0 ? Encoding.ASCII.GetBytes($"<!>{frameSize} 10000000 neo-buzz") : null;
            int clmChunk = clm != null ? 8 + clm.Length + (clm.Length & 1) : 0;
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(4 + 24 + clmChunk + 8 + samples.Length * 4);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)3);
            bw.Write((short)1);
            bw.Write(sampleRate);
            bw.Write(sampleRate * 4);
            bw.Write((short)4);
            bw.Write((short)32);
            if (clm != null)
            {
                bw.Write(Encoding.ASCII.GetBytes("clm "));
                bw.Write(clm.Length);
                bw.Write(clm);
                if ((clm.Length & 1) != 0) bw.Write((byte)0);
            }
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(samples.Length * 4);
            foreach (var s in samples) bw.Write(s);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
