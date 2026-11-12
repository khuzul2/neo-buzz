#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.6): what the Sample and Granular engines play. Five
// built-in samples made by code (recipes, no binary assets; each becomes a SampleLayer once per
// process on a background thread, as the built-in tables do), and a user's WAV file: read, kept in
// the machine state as 24-bit PCM (so a song carries exactly what played), built into a layer off
// the audio thread. The layer is Neo Sampler's (octave-down copies through a halfband, read by
// a windowed sinc whose cutoff follows the step), so a sample played up or down is band-limited.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeoBuzz.Dsp;
using NeoBuzz.Sampler;

namespace NeoBuzz.DyeEssence
{
    internal static class SampleSources
    {
        public static readonly string[] Names = { "Choir", "Strings", "Bell", "Pluck", "Texture" };
        public static int Count => Names.Length;

        /// <summary>The note the built-in samples sound at their own pitch.</summary>
        public const int Root = 60;
        /// <summary>The longest user sample kept, in seconds of the file's own rate (longer ones are cut).</summary>
        public const int MaxSeconds = 20;

        // The pitched recipes repeat every 183 samples; their layer's rate is set so that period is middle C.
        const int Period = 183;
        static readonly int PitchedRate = (int)Math.Round(Period * 261.6255653005986);

        static readonly SampleLayer[] built = new SampleLayer[5];
        static readonly int[] requested = new int[5];

        /// <summary>The sample if built; otherwise null, and a build is started.</summary>
        public static SampleLayer Get(int index)
        {
            if (index < 0 || index >= Count) return null;
            var t = Volatile.Read(ref built[index]);
            if (t != null) return t;
            if (Interlocked.Exchange(ref requested[index], 1) == 0) Build(index);
            return null;
        }

        static void Build(int index) => Task.Run(() => Volatile.Write(ref built[index], Make(index)));

        /// <summary>Whether every built-in sample is built (starting the builds not yet asked for). Any thread.</summary>
        public static bool AllBuilt()
        {
            bool all = true;
            for (int i = 0; i < Count; i++)
                if (Get(i) == null) all = false;
            return all;
        }

        /// <summary>Builds now (tests, editors); returns the sample.</summary>
        public static SampleLayer GetNow(int index)
        {
            var t = Volatile.Read(ref built[index]);
            if (t != null) return t;
            t = Make(index);
            Volatile.Write(ref built[index], t);
            Interlocked.Exchange(ref requested[index], 1);
            return t;
        }

        static SampleLayer Make(int index)
        {
            float[] x;
            int rate = PitchedRate;
            switch (index)
            {
                case 0: x = Choir(); break;
                case 1: x = Strings(); break;
                case 2: x = Bell(); rate = 48000; break;
                case 3: x = Pluck(); break;
                default: x = Texture(); rate = 48000; break;
            }
            float peak = 0;
            foreach (var v in x) peak = Math.Max(peak, Math.Abs(v));
            float g = peak > 0 ? 0.9f / peak : 1f;
            for (int i = 0; i < x.Length; i++) x[i] *= g;
            return SampleLayer.Build(Names[index], x, null, rate, Root);
        }

        // Additive recipes lock every harmonic to the period (sine tables indexed by whole steps) and
        // move them with whole numbers of slow cycles over the length, so the loop has no seam.
        static float[] Additive(int cycles, Func<int, double> weight, double depth, uint seed)
        {
            int n = cycles * Period;
            var sine = new double[Period];
            for (int i = 0; i < Period; i++) sine[i] = PortableMath.Sin(2 * Math.PI * i / Period);
            var slow = new double[n];
            for (int i = 0; i < n; i++) slow[i] = PortableMath.Sin(2 * Math.PI * i / n);
            var rng = new Rng(seed);
            int harmonics = Period / 2 - 1;
            var a = new double[harmonics + 1];
            var shift = new int[harmonics + 1];
            var rate = new int[harmonics + 1];
            var phase = new int[harmonics + 1];
            for (int k = 1; k <= harmonics; k++)
            {
                a[k] = weight(k);
                shift[k] = (int)(rng.NextUnipolar() * Period);
                rate[k] = 1 + (int)(rng.NextUInt() % 4);
                phase[k] = (int)(rng.NextUnipolar() * n);
            }
            var y = new float[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                for (int k = 1; k <= harmonics; k++)
                {
                    if (a[k] < 1e-4) continue;
                    long m = (long)rate[k] * i + phase[k];
                    sum += a[k] * (1 + depth * slow[(int)(m % n)]) * sine[(int)(((long)k * i + shift[k]) % Period)];
                }
                y[i] = (float)sum;
            }
            return y;
        }

        static double Bump(double f, double centre, double width) => PortableMath.Exp(-0.5 * ((f - centre) / width) * ((f - centre) / width));

        // A vowel: three formant peaks over the harmonics, each moving a little.
        static float[] Choir() => Additive(524, k =>
        {
            double f = k * (double)PitchedRate / Period;
            return Bump(f, 700, 90) + 0.7 * Bump(f, 1100, 110) + 0.35 * Bump(f, 2600, 250) + 0.02 / k;
        }, 0.25, 0xC001u);

        static float[] Strings() => Additive(524, k => (k % 2 == 0 ? 0.85 : 1.0) / k, 0.3, 0x57A1u);

        // Inharmonic partials of a struck bar, each dying at its own rate.
        static float[] Bell()
        {
            int n = 3 * 48000;
            double f0 = 261.6255653005986;
            double[] ratio = { 1, 2.76, 5.4, 8.93, 13.34, 18.64 };
            double[] amp = { 1, 0.6, 0.45, 0.3, 0.2, 0.1 };
            double[] tau = { 1.6, 1.0, 0.7, 0.45, 0.3, 0.2 };
            var y = new float[n];
            for (int j = 0; j < ratio.Length; j++)
            {
                double decay = PortableMath.Exp(-1.0 / (tau[j] * 48000)), env = amp[j];
                for (int i = 0; i < n; i++)
                {
                    y[i] += (float)(env * PortableMath.Sin(2 * Math.PI * f0 * ratio[j] * i / 48000));
                    env *= decay;
                }
            }
            for (int i = 0; i < 96; i++) y[i] *= i / 96f;
            return y;
        }

        // A plucked string: a burst of noise round a delay line, lowpassed on every pass.
        static float[] Pluck()
        {
            int n = (int)(2.5 * PitchedRate);
            var line = new float[Period];
            var rng = new Rng(0x91CCu);
            for (int i = 0; i < Period; i++) line[i] = rng.NextBipolar();
            var y = new float[n];
            for (int i = 0, at = 0; i < n; i++)
            {
                int next = at + 1 == Period ? 0 : at + 1;
                y[i] = line[at];
                line[at] = 0.5f * (line[at] + line[next]) * 0.9985f;
                at = next;
            }
            int fade = PitchedRate / 20;
            for (int i = 0; i < fade; i++) y[n - 1 - i] *= i / (float)fade;
            return y;
        }

        // Noise through two resonant band-passes drifting slowly (whole cycles over the length).
        static float[] Texture()
        {
            int n = 4 * 48000;
            var y = new float[n];
            var rng = new Rng(0x7E47u);
            double low1 = 0, band1 = 0, low2 = 0, band2 = 0, f1 = 0, f2 = 0;
            for (int i = 0; i < n; i++)
            {
                if ((i & 15) == 0)
                {
                    double a = 2 * Math.PI * i / n;
                    f1 = 2 * PortableMath.Sin(Math.PI * 600 * PortableMath.Pow(2.0, 1.5 * PortableMath.Sin(3 * a)) / 48000);
                    f2 = 2 * PortableMath.Sin(Math.PI * 2200 * PortableMath.Pow(2.0, 1.2 * PortableMath.Sin(5 * a + 1)) / 48000);
                }
                double noise = rng.NextBipolar();
                low1 += f1 * band1;
                band1 += f1 * (noise - low1 - 0.12 * band1);
                low2 += f2 * band2;
                band2 += f2 * (noise - low2 - 0.12 * band2);
                y[i] = (float)(band1 + 0.7 * band2);
            }
            return y;
        }

        // --- User samples: WAV in, 24-bit PCM in the machine state ---

        /// <summary>A WAV file's channels (the first two; right is null for mono) and rate. PCM 8/16/24/32-bit or 32/64-bit float.</summary>
        public static (float[] Left, float[] Right, int Rate) ReadWav(byte[] data)
        {
            using var br = new BinaryReader(new MemoryStream(data));
            if (data.Length < 12 || Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") throw new InvalidDataException("not a WAV file (no RIFF header)");
            br.ReadInt32();
            if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE") throw new InvalidDataException("not a WAV file (no WAVE tag)");
            int format = 0, channels = 0, bits = 0, rate = 0;
            float[] left = null, right = null;
            while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
            {
                string id = Encoding.ASCII.GetString(br.ReadBytes(4));
                long remaining = br.BaseStream.Length - br.BaseStream.Position - 4;
                uint declared = br.ReadUInt32();
                int size = (int)Math.Min(declared, (ulong)Math.Max(0, remaining));
                long next = br.BaseStream.Position + size + (size & 1);
                if (id == "fmt ")
                {
                    format = br.ReadInt16();
                    channels = br.ReadInt16();
                    rate = br.ReadInt32();
                    br.ReadInt32();
                    br.ReadInt16();
                    bits = br.ReadInt16();
                    if (format == unchecked((short)0xFFFE) && size >= 26)
                    {
                        br.ReadInt16();
                        br.ReadInt16();
                        br.ReadInt32();
                        format = br.ReadInt16();
                    }
                }
                else if (id == "data")
                {
                    if (channels <= 0 || bits <= 0 || rate <= 0) throw new InvalidDataException("data before format");
                    int bytesPer = bits / 8;
                    if (bytesPer <= 0 || (format != 1 && format != 3)) throw new InvalidDataException($"{bits}-bit samples (format {format}) aren't supported");
                    int count = size / (bytesPer * channels);
                    left = new float[count];
                    if (channels > 1) right = new float[count];
                    for (int i = 0; i < count; i++)
                        for (int c = 0; c < channels; c++)
                        {
                            float v = (float)ReadSample(br, format, bits);
                            if (c == 0) left[i] = v;
                            else if (c == 1) right[i] = v;
                        }
                }
                br.BaseStream.Position = Math.Min(next, br.BaseStream.Length);
            }
            if (left == null || left.Length < 16) throw new InvalidDataException("no audio in the file");
            return (left, right, rate);
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

        static int Quantise(float v)
        {
            if (!float.IsFinite(v)) return 0;
            double q = Math.Round(Math.Clamp(v, -1f, 1f) * 8388608.0);
            return (int)Math.Clamp(q, -8388608.0, 8388607.0);
        }

        /// <summary>The samples as the state keeps them: 24-bit little-endian PCM, channels interleaved, in base64.</summary>
        public static string Encode(float[] left, float[] right)
        {
            int ch = right != null ? 2 : 1, n = left.Length;
            var bytes = new byte[n * ch * 3];
            int at = 0;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < ch; c++)
                {
                    int q = Quantise(c == 0 ? left[i] : right[i]);
                    bytes[at++] = (byte)q;
                    bytes[at++] = (byte)(q >> 8);
                    bytes[at++] = (byte)(q >> 16);
                }
            return Convert.ToBase64String(bytes);
        }

        /// <summary>Back from <see cref="Encode"/> (what played when the sample was loaded).</summary>
        public static (float[] Left, float[] Right) Decode(string data, bool stereo)
        {
            var bytes = Convert.FromBase64String(data);
            int ch = stereo ? 2 : 1, n = bytes.Length / (3 * ch);
            var left = new float[n];
            var right = stereo ? new float[n] : null;
            int at = 0;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < ch; c++)
                {
                    int q = (bytes[at] | (bytes[at + 1] << 8) | ((sbyte)bytes[at + 2] << 16));
                    at += 3;
                    if (c == 0) left[i] = q / 8388608f;
                    else right[i] = q / 8388608f;
                }
            return (left, right);
        }

        /// <summary>A user sample's layer (the samples as the state keeps them).</summary>
        public static SampleLayer Layer(string name, float[] left, float[] right, int rate) =>
            SampleLayer.Build(name, left, right, rate, Root);
    }
}
