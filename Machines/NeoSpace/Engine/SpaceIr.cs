#nullable disable
// Neo Space (docs/specs/neo-space.md): from a base response (a built-in room, or the song's wave resampled to the host's rate) to the
// response the convolver is built for. All of it runs on the builder's thread (it allocates), never the audio thread, and uses
// PortableMath, so the response is the same bits on every machine. The order:
//   1. a wave's start: a DC offset taken out and a lead of silence trimmed (a clip that starts late is not a pre-delay of its own);
//   2. Length: the response is cut to a fraction of its length, the end faded out with a raised cosine;
//   3. Reverse (after the trim, so the trimmed response is the one that plays backwards);
//   4. Low cut and High cut: two second-order Butterworth sections each (24 dB/oct), the cut off at its end of the range;
//   5. Width: the side signal scaled (0 mono … 200 %);
//   6. Pre-delay: that much silence in front;
//   7. at most 10 s long (the end fades), and the level set so that pink noise (equal energy per octave, 20 Hz to 20 kHz: like music)
//      comes out at its own level (see PinkLevel), the two channels together.
// The same steps with the same numbers on the same base always give the same bits.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Space
{
    /// <summary>What shapes a base response (physical values, from the parameters).</summary>
    internal struct SpaceSettings
    {
        public double PreDelayMs;
        /// <summary>5 … 100.</summary>
        public double LengthPercent;
        /// <summary>Hz; at or below <see cref="LowCutOff"/> it is off.</summary>
        public double LowCutHz;
        /// <summary>Hz; at or above <see cref="HighCutOff"/> it is off.</summary>
        public double HighCutHz;
        /// <summary>0 … 200.</summary>
        public double WidthPercent;
        public bool Reverse;

        public const double LowCutOff = 20.5, HighCutOff = 19500.0;

        public static SpaceSettings Plain => new SpaceSettings { PreDelayMs = 0, LengthPercent = 100, LowCutHz = 20, HighCutHz = 20000, WidthPercent = 100, Reverse = false };
    }

    internal static class SpaceIr
    {
        /// <summary>The longest response, in seconds.</summary>
        public const double MaxSeconds = SpaceRooms.MaxSeconds;

        /// <summary>The number of columns of the picture (<see cref="Preview"/>).</summary>
        public const int PreviewColumns = 240;

        /// <summary>
        /// The shaped response (new arrays; the base is left as it is). A null <paramref name="baseRight"/> is the left (a mono response).
        /// <paramref name="isWave"/>: the base is a user's wave (its DC and its lead of silence are taken out).
        /// </summary>
        public static void Shape(float[] baseLeft, float[] baseRight, double rate, in SpaceSettings s, bool isWave, out float[] outLeft, out float[] outRight)
            => Shape(baseLeft, baseRight, rate, s, isWave, out outLeft, out outRight, out _, out _);

        /// <summary>As above, and where the response the length trims starts in the base (a wave's lead of silence is skipped) and how many samples it has.</summary>
        public static void Shape(float[] baseLeft, float[] baseRight, double rate, in SpaceSettings s, bool isWave, out float[] outLeft, out float[] outRight, out int baseStart, out int baseCount)
        {
            baseRight ??= baseLeft;
            int total = Math.Min(baseLeft.Length, baseRight.Length);
            int start = 0;
            double dcL = 0, dcR = 0;
            if (isWave && total > 0)
            {
                for (int i = 0; i < total; i++) { dcL += baseLeft[i]; dcR += baseRight[i]; }
                dcL /= total; dcR /= total;
                // The lead of silence: up to the first sample 50 dB under the peak, less half a millisecond.
                double peak = 0;
                for (int i = 0; i < total; i++) peak = Math.Max(peak, Math.Max(Math.Abs(baseLeft[i] - dcL), Math.Abs(baseRight[i] - dcR)));
                double floor = peak * 0.00316;
                while (start < total && Math.Max(Math.Abs(baseLeft[start] - dcL), Math.Abs(baseRight[start] - dcR)) < floor) start++;
                start = Math.Max(0, start - (int)(0.0005 * rate));
            }
            int available = Math.Max(1, total - start);
            baseStart = start;
            baseCount = available;
            double pct = Math.Clamp(s.LengthPercent, 5.0, 100.0);
            int n = Math.Max(1, (int)Math.Round(available * pct / 100.0));
            var x = new float[n];
            var y = new float[n];
            for (int i = 0; i < n; i++)
            {
                x[i] = (float)(baseLeft[start + i] - dcL);
                y[i] = (float)(baseRight[start + i] - dcR);
            }
            // The trim's fade: a tenth of what is kept (5 ms … 500 ms); a whole response only gets the 10 ms that keeps its end from clicking.
            int fade = pct >= 99.99 ? (int)(0.01 * rate) : (int)Math.Clamp(0.1 * n, 0.005 * rate, 0.5 * rate);
            Fade(x, y, n, Math.Min(n / 2, fade));
            if (s.Reverse)
            {
                Array.Reverse(x);
                Array.Reverse(y);
            }
            if (s.LowCutHz > SpaceSettings.LowCutOff) Cut(x, y, rate, BiquadType.HighPass, s.LowCutHz);
            if (s.HighCutHz < SpaceSettings.HighCutOff) Cut(x, y, rate, BiquadType.LowPass, Math.Min(s.HighCutHz, rate * 0.45));
            double width = Math.Clamp(s.WidthPercent, 0.0, 200.0) / 100.0;
            if (Math.Abs(width - 1.0) > 1e-9)
            {
                for (int i = 0; i < n; i++)
                {
                    float m = 0.5f * (x[i] + y[i]), d = 0.5f * (x[i] - y[i]) * (float)width;
                    x[i] = m + d;
                    y[i] = m - d;
                }
            }
            int pre = (int)Math.Round(Math.Clamp(s.PreDelayMs, 0.0, 250.0) * 0.001 * rate);
            int max = (int)(MaxSeconds * rate);
            int length = Math.Min(max, n + pre);
            var l = new float[length];
            var r = new float[length];
            for (int i = 0; i < n && pre + i < length; i++)
            {
                l[pre + i] = x[i];
                r[pre + i] = y[i];
            }
            if (n + pre > length) Fade(l, r, length, Math.Min(length / 2, (int)(0.01 * rate)));
            float norm = Normaliser(l, r, rate);
            for (int i = 0; i < length; i++)
            {
                l[i] *= norm;
                r[i] *= norm;
                if (Math.Abs(l[i]) < 1e-20f) l[i] = 0;
                if (Math.Abs(r[i]) < 1e-20f) r[i] = 0;
            }
            outLeft = l;
            outRight = r;
        }

        /// <summary>The gain that gives the response a <see cref="PinkLevel"/> of 1.</summary>
        public static float Normaliser(float[] l, float[] r, double rate)
        {
            double e = PinkLevel(l, r, rate);
            return e > 1e-30 ? (float)(1.0 / Math.Sqrt(e)) : 1f;
        }

        /// <summary>
        /// The power gain a response has on pink noise (equal energy per octave from 20 Hz to 20 kHz, which is how music sits in the spectrum),
        /// the mean of the two channels: a single click is 1, and a reverb with this level at 1 comes out about as loud as what went in whatever
        /// its colour (a tail heavy in the lows is not louder than a bright one). Runs on the builder's thread (it allocates).
        /// </summary>
        public static double PinkLevel(float[] l, float[] r, double rate)
        {
            int n = Math.Max(l.Length, r.Length);
            int size = 1024;
            while (size < n) size <<= 1;
            double hi = Math.Min(20000.0, rate * 0.45);
            double sum = 0, weights = 0;
            var re = new double[size];
            var im = new double[size];
            for (int ch = 0; ch < 2; ch++)
            {
                var x = ch == 0 ? l : r;
                Array.Clear(re); Array.Clear(im);
                for (int i = 0; i < x.Length; i++) re[i] = x[i];
                Fft.Transform(re, im, false);
                double s = 0, w = 0;
                for (int k = 1; k <= size / 2; k++)
                {
                    double f = k * rate / size;
                    if (f < 20.0) continue;
                    if (f > hi) break;
                    double wk = 1.0 / f;
                    s += wk * (re[k] * re[k] + im[k] * im[k]);
                    w += wk;
                }
                sum += s;
                weights = w;
            }
            return weights > 0 ? sum / (2.0 * weights) : 0.0;
        }

        static void Fade(float[] x, float[] y, int n, int fade)
        {
            for (int i = 0; i < fade; i++)
            {
                float g = 0.5f + 0.5f * (float)PortableMath.Cos(Math.PI * (i + 1) / (fade + 1));
                x[n - fade + i] *= g;
                y[n - fade + i] *= g;
            }
        }

        static void Cut(float[] x, float[] y, double rate, BiquadType type, double hz)
        {
            // Two second-order Butterworth sections (Q 0.7071 twice: a 24 dB/oct slope close to a Linkwitz-Riley), a section pair per channel.
            var a1 = new Biquad(); a1.Set(type, hz, rate, 0.7071);
            var a2 = new Biquad(); a2.Set(type, hz, rate, 0.7071);
            var b1 = new Biquad(); b1.Set(type, hz, rate, 0.7071);
            var b2 = new Biquad(); b2.Set(type, hz, rate, 0.7071);
            for (int i = 0; i < x.Length; i++)
            {
                x[i] = a2.Process(a1.Process(x[i]));
                y[i] = b2.Process(b1.Process(y[i]));
            }
        }

        /// <summary>
        /// The picture of a response: its level in dB (peak of both channels over each of <see cref="PreviewColumns"/> equal spans of
        /// <paramref name="length"/> samples from <paramref name="offset"/>), -90 for silence.
        /// </summary>
        public static float[] Preview(float[] l, float[] r, int offset, int length)
        {
            var p = new float[PreviewColumns];
            if (r == null) r = l;
            length = Math.Min(length, Math.Min(l.Length, r.Length) - offset);
            if (length <= 0)
            {
                Array.Fill(p, -90f);
                return p;
            }
            for (int c = 0; c < PreviewColumns; c++)
            {
                int a = (int)((long)c * length / PreviewColumns), b = (int)((long)(c + 1) * length / PreviewColumns);
                float peak = 0;
                for (int i = a; i < Math.Max(b, a + 1) && i < length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[offset + i]), Math.Abs(r[offset + i])));
                p[c] = peak <= 1e-9f ? -90f : (float)Math.Max(-90.0, 20.0 * PortableMath.Log10(peak));
            }
            return p;
        }
    }
}
