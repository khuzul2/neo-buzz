#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.12): the Spectral engine's model of a sample. A sinusoidal
// model with a noise residual in the manner of the published sinusoids-plus-noise literature: a frame
// every hop of a short-time Fourier transform, in each frame up to 64 partials (frequency and amplitude,
// each kept in the slot of the track it continues) and the residual's level in 24 Bark-like bands.
// The model is what the song stores beside the sample: frequencies in steps of 1/4800 octave, amplitudes
// in steps of 0.05 dB and band levels in steps of 0.4 dB, deflated, so a reloaded song gives the same bits on every platform
// without analysing again (the analysis is in SpectralAnalysis.cs and runs off the audio thread).

using System;
using System.IO;
using System.IO.Compression;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    internal sealed class SpectralModel
    {
        /// <summary>Partial slots a frame has (a track keeps its slot from its birth to its death), bands of the residual, and the model's format version.</summary>
        public const int MaxPartials = 64, Bands = 24, Version = 1;

        /// <summary>Frequency steps an octave above <see cref="FMin"/>; amplitude and band-level steps in dB from <see cref="DbFloor"/>.</summary>
        public const int FreqSteps = 4800;
        public const double FMin = 8.0, DbStep = 0.4, AmpStep = 0.05, DbFloor = -100.0;

        /// <summary>The residual's band edges in Hz (a Bark-like scale); the top band runs to the Nyquist frequency.</summary>
        public static readonly double[] BandEdges =
        {
            0, 100, 200, 300, 400, 510, 630, 770, 920, 1080, 1270, 1480, 1720, 2000, 2320, 2700, 3150, 3700, 4400, 5300, 6400, 7700, 9500, 12000, 1e9,
        };

        public string Name = "";
        /// <summary>The sample's rate and length, the transform's size and hop (samples), and the frames (frame f is centred on sample f · hop).</summary>
        public int Rate, Length, Size, Hop, Frames;

        // Stored: per frame the first partial and the count (frames + 1 starts), and per partial its slot, frequency and amplitude steps.
        public int[] Start;
        public byte[] Slot;
        public ushort[] FreqQ;
        public ushort[] AmpQ;
        /// <summary>Per frame: the fundamental's frequency step (0: none found).</summary>
        public ushort[] F0Q;
        /// <summary>Band-major: band b of frame f at b · Frames + f; 0 is silence.</summary>
        public byte[] NoiseQ;

        // Derived from the steps (not stored), what the voices read: frequency (Hz), log2 of it, amplitude, ln of it; per frame the fundamental (Hz, log2) and
        // the energy-weighted centre of the partials (log2 Hz); the band levels as rms (frame-major: f · Bands + b).
        public float[] Freq, L2, Amp, LnA;
        public float[] F0, F0L2, Centroid, Noise;

        public double FrameSeconds => Hop / (double)Rate;
        public double Duration => Length / (double)Rate;
        public int PartialCount => Slot?.Length ?? 0;

        static readonly float[] AmpTable = MakeAmpTable();

        static float[] MakeAmpTable()
        {
            var t = new float[256];
            for (int q = 0; q < 256; q++) t[q] = q == 0 ? 0f : (float)PortableMath.Exp10((q * DbStep + DbFloor) / 20.0);
            return t;
        }

        /// <summary>The step of a level in dB (1 … 255; 0 for a level below the floor).</summary>
        public static byte QuantiseDb(double db) => db <= DbFloor + 0.5 * DbStep ? (byte)0 : (byte)Math.Clamp((int)Math.Round((db - DbFloor) / DbStep), 1, 255);

        /// <summary>The step of a linear band level (0.4 dB).</summary>
        public static byte QuantiseLevel(double a) => a <= 1e-12 ? (byte)0 : QuantiseDb(20.0 * PortableMath.Log10(a));

        /// <summary>The step of a partial's linear amplitude (0.05 dB, so the rounding error is under 0.02 dB rms: -55 dB against the partial); 0 below the floor.</summary>
        public static ushort QuantiseAmp(double a)
        {
            if (a <= 1e-12) return 0;
            double db = 20.0 * PortableMath.Log10(a);
            return db <= DbFloor + 0.5 * AmpStep ? (ushort)0 : (ushort)Math.Clamp((int)Math.Round((db - DbFloor) / AmpStep), 1, 4000);
        }

        public static ushort QuantiseFreq(double hz)
        {
            if (!(hz > FMin)) return 0;
            double q = Math.Round(PortableMath.Log2(hz / FMin) * FreqSteps);
            return (ushort)Math.Clamp(q, 1.0, 65535.0);
        }

        public static double FreqOf(int q) => q <= 0 ? 0.0 : FMin * PortableMath.Exp2(q / (double)FreqSteps);

        /// <summary>Works the read-side arrays out from the steps (once, off the audio thread).</summary>
        public void Finish()
        {
            int n = Slot.Length;
            Freq = new float[n]; L2 = new float[n]; Amp = new float[n]; LnA = new float[n];
            float l2min = (float)PortableMath.Log2(FMin);
            for (int i = 0; i < n; i++)
            {
                Freq[i] = (float)FreqOf(FreqQ[i]);
                L2[i] = l2min + FreqQ[i] / (float)FreqSteps;
                Amp[i] = AmpQ[i] == 0 ? 0f : (float)PortableMath.Exp10((AmpQ[i] * AmpStep + DbFloor) / 20.0);
                LnA[i] = (float)((AmpQ[i] * AmpStep + DbFloor) * 0.11512925464970229);
            }
            F0 = new float[Frames]; F0L2 = new float[Frames]; Centroid = new float[Frames];
            for (int f = 0; f < Frames; f++)
            {
                F0[f] = (float)FreqOf(F0Q[f]);
                F0L2[f] = F0Q[f] == 0 ? 0f : l2min + F0Q[f] / (float)FreqSteps;
                double e = 0, w = 0;
                for (int i = Start[f]; i < Start[f + 1]; i++)
                {
                    double a2 = (double)Amp[i] * Amp[i];
                    e += a2;
                    w += a2 * L2[i];
                }
                Centroid[f] = e > 0 ? (float)(w / e) : 10f;
            }
            Noise = new float[Frames * Bands];
            for (int f = 0; f < Frames; f++)
                for (int b = 0; b < Bands; b++) Noise[f * Bands + b] = AmpTable[NoiseQ[b * Frames + f]];
        }

        // ---- The song's text: a header and the deflated planes, in base64.

        /// <summary>The model as the song keeps it.</summary>
        public string ToBase64()
        {
            var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true))
            {
                var w = new BinaryWriter(z);
                for (int f = 0; f < Frames; f++) w.Write((byte)(Start[f + 1] - Start[f]));
                Plane(w, Frames, f => F0Q[f] & 0xFF);
                Plane(w, Frames, f => F0Q[f] >> 8);
                w.Write(Slot);
                // Frequencies and amplitudes as the change from the same slot's last entry (a track barely moves between frames).
                var lastF = new int[MaxPartials];
                var lastA = new int[MaxPartials];
                var df = new int[Slot.Length];
                var da = new int[Slot.Length];
                for (int i = 0; i < Slot.Length; i++)
                {
                    int s = Slot[i];
                    df[i] = Zig(FreqQ[i] - lastF[s]);
                    da[i] = Zig(AmpQ[i] - lastA[s]);
                    lastF[s] = FreqQ[i];
                    lastA[s] = AmpQ[i];
                }
                Plane(w, df.Length, i => df[i] & 0xFF);
                Plane(w, df.Length, i => (df[i] >> 8) & 0xFF);
                Plane(w, df.Length, i => df[i] >> 16);
                Plane(w, da.Length, i => da[i] & 0xFF);
                Plane(w, da.Length, i => da[i] >> 8);
                w.Write(NoiseQ);
                w.Flush();
            }
            var body = ms.ToArray();
            var all = new byte[24 + body.Length];
            all[0] = (byte)'D'; all[1] = (byte)'S'; all[2] = (byte)'M'; all[3] = Version;
            Put(all, 4, Rate); Put(all, 8, Length); Put(all, 12, Size); Put(all, 16, Hop); Put(all, 20, Slot.Length);
            Buffer.BlockCopy(body, 0, all, 24, body.Length);
            return Convert.ToBase64String(all);
        }

        static void Plane(BinaryWriter w, int n, Func<int, int> at)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)at(i);
            w.Write(b);
        }

        static int Zig(int v) => (v << 1) ^ (v >> 31);
        static int Unzig(int z) => (z >> 1) ^ -(z & 1);
        static void Put(byte[] a, int at, int v) { a[at] = (byte)v; a[at + 1] = (byte)(v >> 8); a[at + 2] = (byte)(v >> 16); a[at + 3] = (byte)(v >> 24); }
        static int Get(byte[] a, int at) => a[at] | (a[at + 1] << 8) | (a[at + 2] << 16) | (a[at + 3] << 24);

        /// <summary>Back from <see cref="ToBase64"/> (null if the text is damaged or from another version).</summary>
        public static SpectralModel FromBase64(string text, string name = "")
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return null;
                var all = Convert.FromBase64String(text);
                if (all.Length < 24 || all[0] != 'D' || all[1] != 'S' || all[2] != 'M' || all[3] != Version) return null;
                var m = new SpectralModel { Name = name ?? "", Rate = Get(all, 4), Length = Get(all, 8), Size = Get(all, 12), Hop = Get(all, 16) };
                int parts = Get(all, 20);
                if (m.Rate < 1000 || m.Length < 1 || m.Hop < 1 || m.Size < m.Hop || parts < 0 || parts > 64 * 200000) return null;
                m.Frames = (m.Length + m.Hop - 1) / m.Hop + 1;
                using var z = new DeflateStream(new MemoryStream(all, 24, all.Length - 24), CompressionMode.Decompress);
                var r = new BinaryReader(z);
                int frames = m.Frames;
                var counts = r.ReadBytes(frames);
                var f0lo = r.ReadBytes(frames);
                var f0hi = r.ReadBytes(frames);
                m.Slot = r.ReadBytes(parts);
                var dfl = r.ReadBytes(parts); var dfm = r.ReadBytes(parts); var dfh = r.ReadBytes(parts);
                var dal = r.ReadBytes(parts); var dah = r.ReadBytes(parts);
                m.NoiseQ = r.ReadBytes(Bands * frames);
                if (counts.Length != frames || f0hi.Length != frames || m.Slot.Length != parts || dah.Length != parts || m.NoiseQ.Length != Bands * frames) return null;
                m.Start = new int[frames + 1];
                for (int f = 0; f < frames; f++) m.Start[f + 1] = m.Start[f] + counts[f];
                if (m.Start[frames] != parts) return null;
                m.F0Q = new ushort[frames];
                for (int f = 0; f < frames; f++) m.F0Q[f] = (ushort)(f0lo[f] | (f0hi[f] << 8));
                m.FreqQ = new ushort[parts];
                m.AmpQ = new ushort[parts];
                var lastF = new int[MaxPartials];
                var lastA = new int[MaxPartials];
                for (int i = 0; i < parts; i++)
                {
                    int s = m.Slot[i];
                    if (s >= MaxPartials) return null;
                    int fq = lastF[s] + Unzig(dfl[i] | (dfm[i] << 8) | (dfh[i] << 16));
                    int aq = lastA[s] + Unzig(dal[i] | (dah[i] << 8));
                    if (fq < 0 || fq > 65535 || aq < 0 || aq > 4000) return null;
                    m.FreqQ[i] = (ushort)fq;
                    m.AmpQ[i] = (ushort)aq;
                    lastF[s] = fq;
                    lastA[s] = aq;
                }
                m.Finish();
                return m;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
