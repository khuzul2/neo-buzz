using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/machines.md, Neo Drums; mcp-server.md §12.10): what a drum hit is, read from a render: its
    /// peak, its level over time, how long it takes to fall 20, 40 and 60 dB, its third-octave spectrum, its pitch over time
    /// and its brightness. And songs for the classic drum machines the tools can't play: Rout 909 plays only from trigger
    /// columns, so a copy of a repo song gets its pattern's triggers written here, in the song file.
    /// </summary>
    internal static class DrumHits
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        /// <summary>A WAV file (16-bit PCM or 32-bit float, any channels) as mono (the channels' mean) and its rate.</summary>
        public static (float[] Samples, int Rate) ReadMono(string path)
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
                throw new InvalidDataException($"{path}: not a WAV file");
            int format = 0, channels = 0, rate = 0, bits = 0, pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                int size = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(b, body);
                    channels = BitConverter.ToInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToInt16(b, body + 14);
                    if (format == -2 && size >= 26) format = BitConverter.ToInt16(b, body + 24);   // WAVE_FORMAT_EXTENSIBLE: its sub-format
                }
                else if (id == "data")
                {
                    int bytes = Math.Min(size, b.Length - body);
                    int frame = channels * bits / 8;
                    int frames = frame > 0 ? bytes / frame : 0;
                    var x = new float[frames];
                    for (int i = 0; i < frames; i++)
                    {
                        double sum = 0;
                        for (int c = 0; c < channels; c++)
                        {
                            int at = body + i * frame + c * bits / 8;
                            sum += format == 3 && bits == 32 ? BitConverter.ToSingle(b, at)
                                 : bits == 16 ? BitConverter.ToInt16(b, at) / 32768.0
                                 : bits == 24 ? ((b[at] | (b[at + 1] << 8) | ((sbyte)b[at + 2] << 16)) / 8388608.0)
                                 : BitConverter.ToInt32(b, at) / 2147483648.0;
                        }
                        x[i] = (float)(sum / Math.Max(1, channels));
                    }
                    return (x, rate);
                }
                pos = body + size + (size & 1);
            }
            throw new InvalidDataException($"{path}: no data");
        }

        /// <summary>In-place radix-2 FFT (re, im of a power-of-two length).</summary>
        public static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double a = -2 * Math.PI / len;
                double wr = Math.Cos(a), wi = Math.Sin(a);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int p = i + k, q = p + len / 2;
                        double tr = re[q] * cr - im[q] * ci, ti = re[q] * ci + im[q] * cr;
                        re[q] = re[p] - tr; im[q] = im[p] - ti;
                        re[p] += tr; im[p] += ti;
                        double nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
        }

        /// <summary>Power spectrum of x[from … from+count) (Hann window when asked), zero-padded to <paramref name="size"/>.</summary>
        public static double[] Power(float[] x, int from, int count, int size, bool hann)
        {
            var re = new double[size];
            var im = new double[size];
            count = Math.Min(count, Math.Min(size, x.Length - from));
            for (int i = 0; i < count; i++)
                re[i] = x[from + i] * (hann ? 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / count) : 1.0);
            Fft(re, im);
            var p = new double[size / 2 + 1];
            for (int k = 0; k < p.Length; k++) p[k] = re[k] * re[k] + im[k] * im[k];
            return p;
        }

        public static readonly double[] ThirdOctaves =
            { 20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000 };

        static double Db(double power) => power > 1e-30 ? 10 * Math.Log10(power) : -300;

        static double PeakOf(float[] x, int from, int count)
        {
            if (from < 0 || from + count > x.Length || count <= 0) return double.NaN;
            double p = 0;
            for (int i = 0; i < count; i++) p = Math.Max(p, Math.Abs(x[from + i]));
            return p;
        }

        static double Rms(float[] x, int from, int count)
        {
            if (from < 0 || from + count > x.Length || count <= 0) return double.NaN;
            double s = 0;
            for (int i = 0; i < count; i++) s += (double)x[from + i] * x[from + i];
            return Math.Sqrt(s / count);
        }

        /// <summary>
        /// One line about a hit that starts at the first sample above −80 dBFS: peak dBFS; peak in dB at times after the onset (from
        /// each time, over 2, 10 or 25 ms); when its 20 ms RMS has fallen 20, 40 and 60 dB from its highest; the third-octave spectrum (dB, the
        /// loudest band 0); the spectral centroid of the first 20 ms and of the whole; and with <paramref name="tonal"/> the
        /// strongest frequency (20 Hz – 3 kHz) in windows after the onset.
        /// </summary>
        public static string Describe(float[] x, int rate, bool tonal)
        {
            int on = Array.FindIndex(x, s => Math.Abs(s) > 1e-4f);
            if (on < 0) return "silent";
            double peak = 0;
            for (int i = on; i < x.Length; i++) peak = Math.Max(peak, Math.Abs(x[i]));
            var sb = new StringBuilder();
            sb.Append(C, $"onset {on} peak {20 * Math.Log10(peak):0.0}");
            // The level over time: the peak in a window from each time (2 ms early on, 10 ms to 100 ms, 25 ms after), so a low
            // tone's level doesn't depend on its phase.
            int[] ms = { 0, 1, 2, 5, 10, 20, 30, 50, 75, 100, 150, 200, 300, 400, 600, 800, 1000, 1500, 2000, 3000 };
            sb.Append(" env");
            foreach (int t in ms)
            {
                int w = (t < 10 ? 2 : t <= 100 ? 10 : 25) * rate / 1000;
                double r = PeakOf(x, on + (int)((long)t * rate / 1000), w);
                sb.Append(double.IsNaN(r) ? " -" : string.Format(C, " {0}:{1:0.0}", t, 20 * Math.Log10(Math.Max(1e-9, r))));
            }
            // Falls from the highest 20 ms RMS, in 5 ms steps.
            int w20 = Math.Max(1, rate / 50), step = Math.Max(1, rate / 200);
            double top = 0;
            int topAt = on;
            for (int i = on; i + w20 <= x.Length; i += step)
            {
                double r = Rms(x, i, w20);
                if (r > top) { top = r; topAt = i; }
            }
            sb.Append(" falls");
            foreach (double drop in new[] { 20.0, 40, 60 })
            {
                double limit = top * Math.Pow(10, -drop / 20);
                int at = -1;
                for (int i = topAt; i + w20 <= x.Length; i += step)
                    if (Rms(x, i, w20) < limit) { at = i + w20 / 2; break; }
                sb.Append(at < 0 ? string.Format(C, " {0}:-", drop) : string.Format(C, " {0}:{1:0}", drop, (at - on) * 1000.0 / rate));
            }
            // Third octaves of the whole hit (up to 2 s).
            int size = 1;
            int count = Math.Min(x.Length - on, 2 * rate);
            while (size < count) size <<= 1;
            var p = Power(x, on, count, size, false);
            var bands = new double[ThirdOctaves.Length];
            for (int k = 1; k < p.Length; k++)
            {
                double f = (double)k * rate / size;
                for (int b = 0; b < bands.Length; b++)
                {
                    double lo = ThirdOctaves[b] / Math.Pow(2, 1.0 / 6), hi = ThirdOctaves[b] * Math.Pow(2, 1.0 / 6);
                    if (f >= lo && f < hi) { bands[b] += p[k]; break; }
                }
            }
            double loud = bands.Max();
            sb.Append(" thirds");
            foreach (double band in bands) sb.Append(C, $" {Math.Max(-99, Db(band) - Db(loud)):0}");
            sb.Append(C, $" centroid {Centroid(x, on, Math.Min(count, rate / 50), rate):0}/{Centroid(x, on, count, rate):0}");
            if (tonal)
            {
                sb.Append(" pitch");
                foreach (var (a, b) in new[] { (0, 10), (10, 20), (20, 40), (40, 80), (80, 160), (160, 320), (320, 640) })
                {
                    int from = on + a * rate / 1000, n = (b - a) * rate / 1000;
                    if (from + n > x.Length) { sb.Append(" -"); continue; }
                    sb.Append(C, $" {a}-{b}:{StrongestHz(x, from, n, rate, 20, 3000):0.0}");
                }
            }
            return sb.ToString();
        }

        static double Centroid(float[] x, int from, int count, int rate)
        {
            int size = 1;
            while (size < count) size <<= 1;
            var p = Power(x, from, count, size, true);
            double num = 0, den = 0;
            for (int k = 1; k < p.Length; k++)
            {
                double f = (double)k * rate / size;
                if (f < 20 || f > 20000) continue;
                num += f * p[k];
                den += p[k];
            }
            return den > 0 ? num / den : 0;
        }

        /// <summary>The strongest frequency between <paramref name="lo"/> and <paramref name="hi"/> Hz (Hann window, padded, interpolated).</summary>
        public static double StrongestHz(float[] x, int from, int count, int rate, double lo, double hi)
        {
            const int size = 1 << 16;
            var p = Power(x, from, count, size, true);
            int kLo = Math.Max(1, (int)(lo * size / rate)), kHi = Math.Min(p.Length - 2, (int)(hi * size / rate));
            int best = kLo;
            for (int k = kLo; k <= kHi; k++) if (p[k] > p[best]) best = k;
            double a = Math.Log(p[best - 1] + 1e-300), b = Math.Log(p[best] + 1e-300), c = Math.Log(p[best + 1] + 1e-300);
            double d = a - 2 * b + c;
            double delta = Math.Abs(d) > 1e-12 ? 0.5 * (a - c) / d : 0;
            return (best + delta) * rate / size;
        }

        // ------------------------------------------------------------------ BMX songs

        sealed class Reader(byte[] d)
        {
            public int Pos;
            public byte U8() => d[Pos++];
            public ushort U16() { var v = BitConverter.ToUInt16(d, Pos); Pos += 2; return v; }
            public int I32() { var v = BitConverter.ToInt32(d, Pos); Pos += 4; return v; }
            public uint U32() { var v = BitConverter.ToUInt32(d, Pos); Pos += 4; return v; }
            public string Str()
            {
                int end = Array.IndexOf(d, (byte)0, Pos);
                var s = Encoding.Latin1.GetString(d, Pos, end - Pos);
                Pos = end + 1;
                return s;
            }
        }

        static int Size(byte type) => type == 3 ? 2 : 1;

        /// <summary>
        /// A copy of a BMX song in which machine <paramref name="machine"/>'s pattern <paramref name="pattern"/> has its track
        /// parameters (all tracks) set by <paramref name="value"/>(row, track, parameter index) (its no-value to leave a cell
        /// empty), and its global rows emptied. The pattern keeps its length; nothing else changes.
        /// </summary>
        public static byte[] WithTrackRows(byte[] bmx, string machine, string pattern, Func<int, int, int, int> value)
        {
            var r = new Reader(bmx);
            if (r.U32() != 0x7A7A7542) throw new InvalidDataException("not a Buzz file");
            var sections = new Dictionary<string, int>();
            uint count = r.U32();
            for (int i = 0; i < count; i++)
            {
                string magic = Encoding.ASCII.GetString(bmx, r.Pos, 4);
                r.Pos += 4;
                int offset = (int)r.U32();
                r.U32();
                sections[magic] = offset;
            }
            // Parameter types by machine name.
            var para = new Dictionary<string, (byte[] Globals, byte[] Tracks, int[] NoValue)>();
            r.Pos = sections["PARA"];
            uint machines = r.U32();
            for (int i = 0; i < machines; i++)
            {
                string name = r.Str();
                r.Str();
                int ng = (int)r.U32(), nt = (int)r.U32();
                var g = new byte[ng];
                var t = new byte[nt];
                var noValue = new int[nt];
                for (int k = 0; k < ng + nt; k++)
                {
                    byte type = r.U8();
                    r.Str();
                    r.I32(); r.I32();
                    int nv = r.I32();
                    r.I32(); r.I32();
                    if (k < ng) g[k] = type;
                    else { t[k - ng] = type; noValue[k - ng] = nv; }
                }
                para[name] = (g, t, noValue);
            }
            r.Pos = sections["MACH"];
            int machineCount = r.U16();
            var names = new List<string>();
            var types = new List<byte>();
            for (int i = 0; i < machineCount; i++)
            {
                string name = r.Str();
                byte type = r.U8();
                names.Add(name);
                types.Add(type);
                if (type != 0) r.Str();
                r.Pos += 8;                                 // x, y
                int data = r.I32();
                r.Pos += data;                              // the machine's data
                int attributes = r.U16();
                for (int a = 0; a < attributes; a++) { r.Str(); r.I32(); }
                if (type == 0)
                {
                    r.Pos += 2 + 2 + 1;                     // the Master's globals: Volume, BPM (words), TPB (byte)
                    r.U16();                                // and its track count (no track parameters)
                }
                else
                {
                    var (g, t, _) = para[name];
                    foreach (var gt in g) r.Pos += Size(gt);
                    int tracks = r.U16();
                    for (int k = 0; k < tracks; k++) foreach (var tt in t) r.Pos += Size(tt);
                }
            }
            var connections = new List<(int From, int To)>();
            if (sections.TryGetValue("CONN", out int conn))
            {
                r.Pos = conn;
                int n = r.U16();
                for (int i = 0; i < n; i++)
                {
                    int from = r.U16(), to = r.U16();
                    r.U16(); r.U16();
                    connections.Add((from, to));
                }
            }
            var copy = (byte[])bmx.Clone();
            r.Pos = sections["PATT"];
            for (int m = 0; m < machineCount; m++)
            {
                int inputs = types[m] == 0
                    ? connections.Count(c => c.To == 0 && !names[c.From].StartsWith('\x01'))
                    : connections.Where(c => c.To == m && !names[c.From].StartsWith('\x01')).Select(c => c.From).Distinct().Count();
                int patterns = r.U16(), tracks = r.U16();
                byte[] g = types[m] == 0 ? new byte[] { 3, 3, 2 } : para[names[m]].Globals;
                byte[] t = types[m] == 0 ? Array.Empty<byte>() : para[names[m]].Tracks;
                for (int p = 0; p < patterns; p++)
                {
                    string name = r.Str();
                    int rows = r.U16();
                    for (int i = 0; i < inputs; i++) { r.U16(); r.Pos += 4 * rows; }
                    bool target = names[m] == machine && name == pattern;
                    for (int row = 0; row < rows; row++)
                        foreach (var gt in g)
                        {
                            if (target) { copy[r.Pos] = 0xFF; if (Size(gt) == 2) copy[r.Pos + 1] = 0xFF; }
                            r.Pos += Size(gt);
                        }
                    for (int k = 0; k < tracks; k++)
                        for (int row = 0; row < rows; row++)
                            for (int q = 0; q < t.Length; q++)
                            {
                                if (target)
                                {
                                    int v = value(row, k, q);
                                    copy[r.Pos] = (byte)v;
                                    if (Size(t[q]) == 2) copy[r.Pos + 1] = (byte)(v >> 8);
                                }
                                r.Pos += Size(t[q]);
                            }
                    if (target) return copy;
                }
            }
            throw new InvalidDataException($"no pattern {pattern} of {machine}");
        }
    }
}
