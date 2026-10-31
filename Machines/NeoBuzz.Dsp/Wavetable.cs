#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): wavetables. A table is a series of
// single-cycle frames; each frame is kept at 11 band-limited levels (harmonics up to 1024 >> level),
// built by FFT, so a read at any pitch can pick a level without aliasing. Reads interpolate within
// a frame (cubic) and between adjacent frames (linear), which makes frame morphing smooth.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class Wavetable
    {
        public const int FrameSize = 2048;
        public const int Levels = 11;
        public const int MaxFrames = 256;

        // data[frame][level]: LevelLength(level) samples plus 3 guard samples (index −1, len, len+1).
        readonly float[][][] data;
        readonly int[] lengths;

        Wavetable(string name, int frameCount)
        {
            Name = name;
            FrameCount = frameCount;
            data = new float[frameCount][][];
            lengths = new int[Levels];
            for (int l = 0; l < Levels; l++) lengths[l] = LevelLength(l);
        }

        public string Name { get; }
        public int FrameCount { get; }

        /// <summary>Highest harmonic kept at a level.</summary>
        public static int MaxHarmonic(int level) => 1024 >> level;

        /// <summary>Samples per cycle stored at a level: at least four per highest harmonic, at least 256.</summary>
        public static int LevelLength(int level) => level <= 1 ? 2048 : Math.Max(256, 4096 >> level);

        /// <summary>
        /// Builds a table from single-cycle frames of <see cref="FrameSize"/> samples (other lengths
        /// are resampled through the spectrum). DC is removed; the table is normalised so its
        /// loudest frame peaks at 1.
        /// </summary>
        public static Wavetable FromFrames(string name, float[][] frames, bool normalize = true)
        {
            int count = Math.Clamp(frames.Length, 1, MaxFrames);
            var table = new Wavetable(name, count);
            var spectra = new (double[] Re, double[] Im)[count];
            for (int f = 0; f < count; f++)
                spectra[f] = Spectrum(frames[Math.Min(f, frames.Length - 1)]);

            double peak = 0;
            for (int f = 0; f < count; f++)
            {
                table.data[f] = new float[Levels][];
                for (int l = 0; l < Levels; l++)
                {
                    table.data[f][l] = Synthesize(spectra[f], MaxHarmonic(l), LevelLength(l));
                    if (l == 0)
                        for (int i = 1; i <= LevelLength(0); i++) peak = Math.Max(peak, Math.Abs(table.data[f][0][i]));
                }
            }
            if (normalize && peak > 1e-9)
            {
                float g = (float)(1.0 / peak);
                foreach (var frame in table.data)
                    foreach (var level in frame)
                        for (int i = 0; i < level.Length; i++) level[i] *= g;
            }
            return table;
        }

        /// <summary>Builds a table from harmonic amplitudes and phases per frame (index 0 unused).</summary>
        public static Wavetable FromHarmonics(string name, int frameCount, Func<int, int, (double Amp, double Phase)> harmonic, int maxHarmonic = 1024)
        {
            var frames = new float[frameCount][];
            for (int f = 0; f < frameCount; f++)
            {
                var re = new double[FrameSize];
                var im = new double[FrameSize];
                for (int h = 1; h <= Math.Min(maxHarmonic, FrameSize / 2 - 1); h++)
                {
                    var (a, ph) = harmonic(f, h);
                    if (a == 0) continue;
                    // x = Σ a·sin(2π h t + ph)  →  bin h = a·(sin ph − i cos ph)·N/2
                    re[h] = a * Math.Sin(ph) * FrameSize / 2;
                    im[h] = -a * Math.Cos(ph) * FrameSize / 2;
                    re[FrameSize - h] = re[h];
                    im[FrameSize - h] = -im[h];
                }
                Fft.Transform(re, im, true);
                frames[f] = new float[FrameSize];
                for (int i = 0; i < FrameSize; i++) frames[f][i] = (float)re[i];
            }
            return FromFrames(name, frames);
        }

        static (double[] Re, double[] Im) Spectrum(float[] frame)
        {
            var re = new double[FrameSize];
            var im = new double[FrameSize];
            if (frame.Length == FrameSize)
            {
                for (int i = 0; i < FrameSize; i++) re[i] = frame[i];
                Fft.Transform(re, im, false);
                return (re, im);
            }
            // Another length: take its own spectrum (zero-padded to a power of two would smear),
            // via a DFT at the harmonic frequencies, up to what both lengths hold.
            int n = frame.Length;
            int maxH = Math.Min(FrameSize / 2 - 1, n / 2 - 1);
            for (int h = 1; h <= maxH; h++)
            {
                double sr = 0, si = 0;
                for (int i = 0; i < n; i++)
                {
                    double a = -2 * Math.PI * h * i / n;
                    sr += frame[i] * Math.Cos(a);
                    si += frame[i] * Math.Sin(a);
                }
                double scale = (double)FrameSize / n;
                re[h] = sr * scale;
                im[h] = si * scale;
                re[FrameSize - h] = re[h];
                im[FrameSize - h] = -im[h];
            }
            return (re, im);
        }

        static float[] Synthesize((double[] Re, double[] Im) spec, int maxHarmonic, int length)
        {
            var re = new double[length];
            var im = new double[length];
            double scale = (double)length / FrameSize;
            int top = Math.Min(maxHarmonic, length / 2 - 1);
            for (int h = 1; h <= top; h++)
            {
                re[h] = spec.Re[h] * scale;
                im[h] = spec.Im[h] * scale;
                re[length - h] = re[h];
                im[length - h] = -im[h];
            }
            Fft.Transform(re, im, true);
            var outp = new float[length + 3];
            for (int i = 0; i < length; i++) outp[i + 1] = (float)re[i];
            outp[0] = outp[length];
            outp[length + 1] = outp[1];
            outp[length + 2] = outp[2];
            return outp;
        }

        /// <summary>
        /// The level to read at a phase increment (cycles per sample at the rate being rendered),
        /// keeping every harmonic below <paramref name="limit"/> (cycles per sample).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LevelFor(double inc, double limit)
        {
            inc = Math.Abs(inc);
            if (inc <= 0) return 0;
            double maxH = limit / inc;
            int level = 0;
            while (level < Levels - 1 && MaxHarmonic(level) > maxH) level++;
            return level;
        }

        /// <summary>One sample: <paramref name="framePos"/> 0 … FrameCount−1, <paramref name="phase"/> 0 … 1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Read(float framePos, double phase, int level)
        {
            int f0 = (int)framePos;
            if (f0 < 0) f0 = 0;
            if (f0 >= FrameCount - 1)
                return ReadFrame(data[FrameCount - 1][level], lengths[level], phase);
            float ff = framePos - f0;
            float a = ReadFrame(data[f0][level], lengths[level], phase);
            if (ff <= 0) return a;
            float b = ReadFrame(data[f0 + 1][level], lengths[level], phase);
            return a + (b - a) * ff;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float ReadFrame(float[] t, int len, double phase)
        {
            double pos = (phase - Math.Floor(phase)) * len;
            int i = (int)pos;
            if (i >= len) i = len - 1;
            float x = (float)(pos - i);
            float y0 = t[i], y1 = t[i + 1], y2 = t[i + 2], y3 = t[i + 3];
            // Catmull-Rom (4-point, 3rd-order Hermite).
            float c1 = 0.5f * (y2 - y0);
            float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            return ((c3 * x + c2) * x + c1) * x + y1;
        }

        /// <summary>A frame's full-band samples (for editors): <see cref="FrameSize"/> values.</summary>
        public float[] FrameSamples(int frame, int level = 0)
        {
            var t = data[Math.Clamp(frame, 0, FrameCount - 1)][level];
            int len = lengths[level];
            var o = new float[len];
            Array.Copy(t, 1, o, 0, len);
            return o;
        }
    }
}
