// Fork (docs/specs/browser.md, BR1): what the index needs from an audio file without decoding it: length, sample rate and channels
// from the header of WAV, AIFF, FLAC, Ogg (Vorbis, Opus) and MP3, plus a tempo where the file says one (a WAV's "acid" chunk) and a
// tempo and key where a sample pack's name does ("Loop 128 bpm Am"). Plain managed code, no native decoder, so the indexer runs
// the same on every platform and reads thousands of files a second.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.RegularExpressions;

namespace ReBuzz.Agent.Browser
{
    /// <summary>One audio file's facts. Bpm 0 and Key "" when unknown.</summary>
    public readonly record struct AudioFileInfo(double Seconds, int SampleRate, int Channels, double Bpm = 0, string Key = "")
    {
        public static readonly string[] AudioExtensions = { ".wav", ".aif", ".aiff", ".aifc", ".flac", ".ogg", ".oga", ".opus", ".mp3" };
        public static readonly string[] MidiExtensions = { ".mid", ".midi" };

        public static bool IsAudio(string path) => Has(path, AudioExtensions);
        public static bool IsMidi(string path) => Has(path, MidiExtensions);

        static bool Has(string path, string[] list)
        {
            string ext = Path.GetExtension(path);
            foreach (var e in list) if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Reads the header; false for a file that is not audio of these formats, or damaged.</summary>
        public static bool TryRead(string path, out AudioFileInfo info)
        {
            info = default;
            try
            {
                using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
                string ext = Path.GetExtension(path).ToLowerInvariant();
                AudioFileInfo? r = ext switch
                {
                    ".wav" => Wav(f),
                    ".aif" or ".aiff" or ".aifc" => Aiff(f),
                    ".flac" => Flac(f),
                    ".ogg" or ".oga" or ".opus" => Ogg(f),
                    ".mp3" => Mp3(f),
                    _ => null,
                };
                if (r is not { } v || v.SampleRate <= 0 || v.Channels <= 0 || !(v.Seconds >= 0) || double.IsInfinity(v.Seconds)) return false;
                var (bpm, key) = Hints(path);
                info = v with { Bpm = v.Bpm > 0 ? v.Bpm : bpm, Key = key };
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // ------------------------------------------------------------------ names

        static readonly Regex BpmWord = new(@"(?<![0-9])(\d{2,3}(?:[.,]\d)?)\s?bpm", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex BareNumber = new(@"(?:^|[ _\-\.\(\[])(\d{2,3})(?=$|[ _\-\.\)\]])", RegexOptions.Compiled);
        static readonly Regex KeyWord = new(@"(?:^|[ _\-\.\(\[])([A-G][#b]?)\s?(maj(?:or)?|min(?:or)?|m)?(?=$|[ _\-\.\)\]])", RegexOptions.Compiled);

        /// <summary>A tempo and a key from a sample pack's file name ("Dusty Loop 128 bpm Am.wav"): (0, "") where it says none.</summary>
        public static (double Bpm, string Key) Hints(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            double bpm = 0;
            var m = BpmWord.Match(name);
            if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) && b >= 40 && b <= 300) bpm = b;
            else if (name.Contains("loop", StringComparison.OrdinalIgnoreCase))
            {
                var n = BareNumber.Match(name);
                if (n.Success && int.TryParse(n.Groups[1].Value, out int v) && v >= 70 && v <= 190) bpm = v;
            }
            string key = "";
            foreach (Match k in KeyWord.Matches(name))
            {
                string note = k.Groups[1].Value, mode = k.Groups[2].Value.ToLowerInvariant();
                if (mode.Length == 0 && !(note.Length == 2)) continue;       // a lone "A" or "C" is a word, not a key
                if (mode.Length == 0 && note.Length == 2) { key = note.ToUpperInvariant()[0] + note[1].ToString() + " major"; break; }
                key = note + (mode.StartsWith("maj") ? " major" : " minor");
                break;
            }
            return (bpm, key);
        }

        // ------------------------------------------------------------------ formats

        static byte[] Read(Stream s, int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }
            return b;
        }

        static string Tag(byte[] b, int at) => System.Text.Encoding.ASCII.GetString(b, at, 4);

        static AudioFileInfo? Wav(Stream s)
        {
            var head = Read(s, 12);
            if (Tag(head, 0) is not ("RIFF" or "RF64") || Tag(head, 8) != "WAVE") return null;
            int rate = 0, channels = 0, align = 0;
            double bpm = 0;
            long data = -1;
            while (s.Position + 8 <= s.Length)
            {
                var ch = Read(s, 8);
                string id = Tag(ch, 0);
                long size = BinaryPrimitives.ReadUInt32LittleEndian(ch.AsSpan(4));
                long next = s.Position + size + (size & 1);
                if (id == "fmt " && size >= 16)
                {
                    var f = Read(s, 16);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(2));
                    rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(4));
                    align = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(12));
                }
                else if (id == "data")
                {
                    data = size == 0xFFFFFFFF ? s.Length - s.Position : Math.Min(size, s.Length - s.Position);
                }
                else if (id == "acid" && size >= 24)
                {
                    var a = Read(s, 24);
                    bool oneShot = (BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(0)) & 1) != 0;
                    float t = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(20)));
                    if (!oneShot && t >= 40 && t <= 300) bpm = t;
                }
                if (rate > 0 && data >= 0 && bpm > 0) break;
                s.Position = Math.Min(next, s.Length);
            }
            if (rate <= 0 || channels <= 0 || align <= 0 || data < 0) return null;
            return new AudioFileInfo(data / (double)align / rate, rate, channels, bpm);
        }

        static AudioFileInfo? Aiff(Stream s)
        {
            var head = Read(s, 12);
            if (Tag(head, 0) != "FORM" || Tag(head, 8) is not ("AIFF" or "AIFC")) return null;
            while (s.Position + 8 <= s.Length)
            {
                var ch = Read(s, 8);
                long size = BinaryPrimitives.ReadUInt32BigEndian(ch.AsSpan(4));
                long next = s.Position + size + (size & 1);
                if (Tag(ch, 0) == "COMM" && size >= 18)
                {
                    var c = Read(s, 18);
                    int channels = BinaryPrimitives.ReadInt16BigEndian(c.AsSpan(0));
                    long frames = BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(2));
                    // The rate is an 80-bit extended float: a 15-bit exponent and a 64-bit mantissa.
                    int exp = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(8)) & 0x7FFF;
                    ulong mant = BinaryPrimitives.ReadUInt64BigEndian(c.AsSpan(10));
                    double rate = mant * Math.Pow(2, exp - 16383 - 63);
                    return new AudioFileInfo(frames / rate, (int)Math.Round(rate), channels);
                }
                s.Position = Math.Min(next, s.Length);
            }
            return null;
        }

        static AudioFileInfo? Flac(Stream s)
        {
            var head = Read(s, 4);
            if (Tag(head, 0) != "fLaC") return null;
            var h = Read(s, 4);
            if ((h[0] & 0x7F) != 0) return null;                 // STREAMINFO comes first
            var si = Read(s, 18);
            int rate = (si[10] << 12) | (si[11] << 4) | (si[12] >> 4);
            int channels = ((si[12] >> 1) & 7) + 1;
            long total = ((long)(si[13] & 0xF) << 32) | ((long)si[14] << 24) | ((long)si[15] << 16) | ((long)si[16] << 8) | si[17];
            return rate > 0 ? new AudioFileInfo(total / (double)rate, rate, channels) : null;
        }

        static AudioFileInfo? Ogg(Stream s)
        {
            var first = Read(s, (int)Math.Min(s.Length, 512));
            if (Tag(first, 0) != "OggS") return null;
            int segs = first[26];
            int at = 27 + segs;
            int rate, channels, granuleRate;
            long preSkip = 0;
            if (at + 30 <= first.Length && System.Text.Encoding.ASCII.GetString(first, at + 1, 6) == "vorbis")
            {
                channels = first[at + 11];
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(at + 12));
                granuleRate = rate;
            }
            else if (at + 19 <= first.Length && System.Text.Encoding.ASCII.GetString(first, at, 8) == "OpusHead")
            {
                channels = first[at + 9];
                preSkip = BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(at + 10));
                rate = 48000;
                granuleRate = 48000;
            }
            else return null;
            // The last page's granule position is the length in samples.
            long tailLen = Math.Min(s.Length, 65536);
            s.Position = s.Length - tailLen;
            var tail = Read(s, (int)tailLen);
            for (int i = tail.Length - 27; i >= 0; i--)
            {
                if (tail[i] == 'O' && tail[i + 1] == 'g' && tail[i + 2] == 'g' && tail[i + 3] == 'S')
                {
                    long granule = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(i + 6));
                    if (granule > 0) return new AudioFileInfo(Math.Max(0, granule - preSkip) / (double)granuleRate, rate, channels);
                }
            }
            return null;
        }

        static readonly int[] Mp3Rates = { 44100, 48000, 32000 };
        static readonly int[,] Mp3Bitrate =
        {
            // MPEG1 layer 1, 2, 3; then MPEG2/2.5 layer 1, 2 and 3 (kbit/s, index 1..14)
            { 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },
            { 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },
            { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },
            { 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
            { 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
            { 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
        };

        static AudioFileInfo? Mp3(Stream s)
        {
            long start = 0;
            var id3 = Read(s, (int)Math.Min(10, s.Length));
            if (id3.Length == 10 && Tag(id3, 0).StartsWith("ID3"))
                start = 10 + ((id3[6] & 0x7F) << 21 | (id3[7] & 0x7F) << 14 | (id3[8] & 0x7F) << 7 | (id3[9] & 0x7F));
            s.Position = start;
            var buf = Read(s, (int)Math.Min(4096, s.Length - start));
            for (int i = 0; i + 4 <= buf.Length; i++)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0) continue;
                int version = (buf[i + 1] >> 3) & 3;                 // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
                int layer = (buf[i + 1] >> 1) & 3;                   // 3 = layer 1, 2 = layer 2, 1 = layer 3
                int bi = buf[i + 2] >> 4, ri = (buf[i + 2] >> 2) & 3;
                if (version == 1 || layer == 0 || bi == 0 || bi == 15 || ri == 3) continue;
                bool v1 = version == 3;
                int rate = Mp3Rates[ri] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
                int row = v1 ? 3 - layer : (layer == 3 ? 3 : 4 + (layer == 2 ? 0 : 1));
                int kbps = Mp3Bitrate[row, bi - 1];
                int channels = (buf[i + 3] >> 6) == 3 ? 1 : 2;
                int samplesPerFrame = layer == 3 ? 384 : (layer == 2 || v1 ? 1152 : 576);
                // A Xing or Info header says the frame count; else the file is taken as constant bitrate.
                int side = v1 ? (channels == 1 ? 17 : 32) : (channels == 1 ? 9 : 17);
                int x = i + 4 + side;
                if (x + 12 <= buf.Length && (Tag(buf, x) is "Xing" or "Info") && (buf[x + 7] & 1) != 0)
                {
                    long frames = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(x + 8));
                    return new AudioFileInfo(frames * samplesPerFrame / (double)rate, rate, channels);
                }
                double seconds = (s.Length - start - i) * 8.0 / (kbps * 1000.0);
                return new AudioFileInfo(seconds, rate, channels);
            }
            return null;
        }
    }
}
