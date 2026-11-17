// A small WAV reader for the evaluation: PCM 8/16/24/32-bit and 32-bit float, any channel count (mixed down to mono).

using System.Text;

namespace NeoBuzz.PitchEval
{
    internal static class Wav
    {
        public static (float[] Samples, int Rate) Read(string path)
        {
            using var fs = File.OpenRead(path);
            return Read(fs);
        }

        public static (float[] Samples, int Rate) Read(Stream stream)
        {
            byte[] all;
            if (stream is MemoryStream ms0 && ms0.TryGetBuffer(out var seg)) all = seg.Array;
            else { var ms = new MemoryStream(); stream.CopyTo(ms); all = ms.ToArray(); }
            return Parse(all);
        }

        static (float[], int) Parse(byte[] b)
        {
            if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") throw new InvalidDataException("not a RIFF/WAVE file");
            int pos = 12, fmtTag = 1, channels = 1, rate = 44100, bits = 16, dataAt = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    fmtTag = BitConverter.ToUInt16(b, body);
                    channels = BitConverter.ToUInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToUInt16(b, body + 14);
                    if (fmtTag == 0xFFFE && len >= 26) fmtTag = BitConverter.ToUInt16(b, body + 24);
                }
                else if (id == "data")
                {
                    dataAt = body;
                    dataLen = len < 0 || body + len > b.Length ? b.Length - body : len;
                    break;
                }
                pos = body + len + (len & 1);
            }
            if (dataAt < 0) throw new InvalidDataException("no data chunk");
            int bytes = bits / 8;
            int frames = dataLen / (bytes * channels);
            var x = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                double acc = 0;
                for (int c = 0; c < channels; c++)
                {
                    int o = dataAt + (i * channels + c) * bytes;
                    double v;
                    if (fmtTag == 3 && bits == 32) v = BitConverter.ToSingle(b, o);
                    else if (bits == 16) v = BitConverter.ToInt16(b, o) / 32768.0;
                    else if (bits == 24) v = ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608.0;
                    else if (bits == 32) v = BitConverter.ToInt32(b, o) / 2147483648.0;
                    else v = (b[o] - 128) / 128.0;
                    acc += v;
                }
                x[i] = (float)(acc / channels);
            }
            return (x, rate);
        }
    }
}
