#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4; first user: Dye Essence's convolution reverb, docs/specs/dye-essence.md §4.10):
// impulse responses made by code, and the shaping a convolution reverb gives them. All of it runs off the audio thread (it
// allocates), on the background builder, and uses PortableMath, so the responses are the same bits on every machine.
//  - Names / Generate: six built-in stereo responses of reverbs that never sat in a room (no recordings, so no licence): a
//    small room (a short one that suits a sampler's "room" send), a room, a chamber, a plate, a hall, a cathedral. Each is an
//    early-reflection pattern (a few discrete taps, different in the two channels) followed by a decaying noise tail whose
//    low band decays slower than its high band (the room's air and walls), a pre-delay and a build-up. Wet only: no direct sound.
//  - Resample: a windowed-sinc rate or time change (a user response at another rate; the Size stretch).
//  - Shape: size (stretch), pre-delay, a decay slope (shorter / longer tail), damping (the tail darker or thinner with time),
//    low and high cut, width, reverse, a fade-out of the end and a normalisation to unit energy (a white input comes out at its
//    own level), at most MaxSeconds long.

using System;

namespace NeoBuzz.Dsp
{
    internal static class ImpulseResponses
    {
        public static readonly string[] Names = { "Small room", "Room", "Chamber", "Plate", "Hall", "Cathedral" };

        /// <summary>The longest response a convolver is built for, in seconds.</summary>
        public const int MaxSeconds = 10;

        // rt60 low band, rt60 high band (s), early reflections (count, first, last in ms), pre-delay (ms), build-up (ms), tail start cutoff (Hz).
        static readonly double[][] Kinds =
        {
            new[] { 0.35, 0.20, 8, 2.5, 22, 1, 2, 9000 },
            new[] { 0.80, 0.45, 10, 4, 42, 2, 4, 10000 },
            new[] { 1.40, 0.85, 12, 5, 58, 4, 10, 9500 },
            new[] { 2.00, 1.60, 0, 0, 0, 0, 0, 14000 },
            new[] { 2.80, 1.70, 14, 8, 80, 14, 40, 9000 },
            new[] { 5.50, 2.80, 16, 18, 120, 24, 80, 8000 },
        };

        sealed class Rng
        {
            uint s;
            public Rng(uint seed) { s = seed; }
            public uint Next() { s = s * 1664525u + 1013904223u; return s; }
            public double Uniform() => (Next() >> 8) / 16777216.0;
            public double Signed() => Uniform() * 2 - 1;
        }

        /// <summary>A built-in response (<paramref name="kind"/> 0 … 5) at <paramref name="rate"/>: left and right, each as long as its tail needs.</summary>
        public static void Generate(int kind, double rate, out float[] left, out float[] right)
        {
            var k = Kinds[Math.Clamp(kind, 0, Kinds.Length - 1)];
            double rtLow = k[0], rtHigh = k[1];
            int early = (int)k[2];
            double e0 = k[3] * 0.001, e1 = k[4] * 0.001, pre = k[5] * 0.001, build = Math.Max(0.0005, k[6] * 0.001);
            double lengthSeconds = Math.Min(MaxSeconds, rtLow * 1.1 + pre);
            int n = (int)(lengthSeconds * rate);
            left = new float[n];
            right = new float[n];
            for (int ch = 0; ch < 2; ch++)
            {
                var y = ch == 0 ? left : right;
                var rng = new Rng(0x9E3779B9u ^ (uint)(kind * 7919 + ch * 104729 + 1));
                // The tail: noise split at 800 Hz, the bands decaying at their own rates (60 dB in rt60), built up over `build`.
                var lp = new Biquad(); lp.Set(BiquadType.LowPass, 800, rate, 0.7071);
                var tone = new Biquad(); tone.Set(BiquadType.LowPass, Math.Min(k[7], rate * 0.45), rate, 0.7071);
                double dLow = 6.907755 / (rtLow * rate), dHigh = 6.907755 / (rtHigh * rate);
                double gLow = 1, gHigh = 1;
                int start = (int)(pre * rate);
                int rise = Math.Max(1, (int)(build * rate));
                double eLow = PortableMath.Exp(-dLow), eHigh = PortableMath.Exp(-dHigh);
                for (int i = 0; i < n; i++)
                {
                    float w = (float)rng.Signed();
                    float low = lp.Process(w) * 2.4f;
                    float high = w - low * 0.4167f;
                    gLow *= eLow;
                    gHigh *= eHigh;
                    if (i < start) continue;
                    double ramp = Math.Min(1.0, (i - start) / (double)rise);
                    ramp = ramp * ramp * (3 - 2 * ramp);
                    y[i] = (float)((low * gLow + high * gHigh * 0.7) * ramp);
                }
                for (int i = 0; i < n; i++) y[i] = tone.Process(y[i]);
                // The early reflections: a few taps spread (denser and weaker later) from the first to the last.
                for (int e = 0; e < early; e++)
                {
                    double u = rng.Uniform();
                    double at = pre + e0 + (e1 - e0) * PortableMath.Pow(u, 1.4);
                    int idx = (int)(at * rate);
                    if (idx >= n) continue;
                    double amp = 0.55 / (1 + 22 * (at - pre));
                    y[idx] += (float)(amp * (rng.Uniform() < 0.5 ? -1 : 1) * (0.6 + 0.4 * rng.Uniform()));
                }
            }
        }

        /// <summary>
        /// <paramref name="x"/> read at steps of <paramref name="step"/> input samples per output sample (a rate change of 1 / step,
        /// a time stretch of 1 / step), by a windowed sinc (a low-pass at the new Nyquist when step is above one).
        /// </summary>
        public static float[] Resample(float[] x, double step)
        {
            if (step <= 0 || Math.Abs(step - 1) < 1e-9) return (float[])x.Clone();
            int n = Math.Max(1, (int)(x.Length / step));
            var y = new float[n];
            double cut = 1.0 / Math.Max(1.0, step);
            int half = (int)Math.Ceiling(10 / cut);
            const int Phases = 256;
            var table = new float[Phases * 2 * half];
            for (int p = 0; p < Phases; p++)
            {
                double frac = p / (double)Phases;
                double sum = 0;
                for (int k = 0; k < 2 * half; k++)
                {
                    double d = frac - (k - half + 1);
                    double a = Math.PI * d * cut;
                    double sinc = Math.Abs(a) < 1e-9 ? 1 : PortableMath.Sin(a) / a;
                    double win = 0.5 + 0.5 * PortableMath.Cos(Math.PI * d / half);
                    double w = Math.Abs(d) >= half ? 0 : cut * sinc * win;
                    table[p * 2 * half + k] = (float)w;
                    sum += w;
                }
                if (sum > 1e-9) for (int k = 0; k < 2 * half; k++) table[p * 2 * half + k] = (float)(table[p * 2 * half + k] / sum);
            }
            for (int i = 0; i < n; i++)
            {
                double pos = i * step;
                int c = (int)Math.Floor(pos);
                int p = Math.Min(Phases - 1, (int)((pos - c) * Phases + 0.5));
                double acc = 0;
                int row = p * 2 * half;
                for (int k = 0; k < 2 * half; k++)
                {
                    int idx = c + k - half + 1;
                    if (idx >= 0 && idx < x.Length) acc += table[row + k] * x[idx];
                }
                y[i] = (float)acc;
            }
            return y;
        }

        /// <summary>How a convolution reverb shapes a response.</summary>
        public struct ShapeSettings
        {
            /// <summary>Time stretch (0.25 … 1.5: 1 as it is).</summary>
            public double Size;
            /// <summary>Seconds of silence before the response.</summary>
            public double PreDelay;
            /// <summary>-1 … +1: the tail slopes down (shorter) or up (longer: at most 9 dB over its length).</summary>
            public double Decay;
            /// <summary>-1 … +1: negative, the tail gets darker with time (a low-pass from 1.5 kHz); positive, thinner (a high-pass from 800 Hz).</summary>
            public double Damping;
            public double LowCutHz, HighCutHz;
            /// <summary>0 (mono) … 2: the side channel's gain.</summary>
            public double Width;
            public bool Reverse;
        }

        /// <summary>
        /// The shaped response (new arrays: <paramref name="l"/> and <paramref name="r"/> are left as they are; a null right is the left),
        /// at most <see cref="MaxSeconds"/> long, normalised to unit energy.
        /// </summary>
        public static void Shape(float[] l, float[] r, double rate, in ShapeSettings s, out float[] outL, out float[] outR)
        {
            r ??= l;
            double size = Math.Clamp(s.Size <= 0 ? 1 : s.Size, 0.25, 1.5);
            int max = (int)(MaxSeconds * rate);
            var a = Resample(l, 1 / size);
            var b = ReferenceEquals(l, r) ? a : Resample(r, 1 / size);
            int pre = (int)Math.Round(Math.Clamp(s.PreDelay, 0, 1) * rate);
            int n = Math.Min(max, Math.Min(a.Length, b.Length) + pre);
            int body = Math.Max(1, n - pre);
            var x = new float[n];
            var y = new float[n];
            for (int i = 0; i < body; i++)
            {
                x[pre + i] = a[i];
                y[pre + i] = b[i];
            }
            // Decay slope: gain 10^(dB(t)/20), dB rising linearly to the end of the body.
            double slope = s.Decay < 0 ? s.Decay * 40 : s.Decay * 9;
            if (Math.Abs(slope) > 1e-6)
                for (int i = 0; i < body; i++)
                {
                    float g = (float)PortableMath.Pow(10, slope * i / body / 20);
                    x[pre + i] *= g;
                    y[pre + i] *= g;
                }
            // Damping: blend towards the filtered tail as time goes on.
            if (Math.Abs(s.Damping) > 1e-6)
            {
                bool dark = s.Damping < 0;
                double amount = Math.Min(1, Math.Abs(s.Damping));
                Damp(x, pre, body, rate, dark, amount);
                Damp(y, pre, body, rate, dark, amount);
            }
            if (s.LowCutHz > 21 || (s.HighCutHz > 0 && s.HighCutHz < 19900))
            {
                CutFilter(x, rate, s.LowCutHz, s.HighCutHz);
                CutFilter(y, rate, s.LowCutHz, s.HighCutHz);
            }
            double width = Math.Clamp(s.Width, 0, 2);
            if (Math.Abs(width - 1) > 1e-6)
                for (int i = 0; i < n; i++)
                {
                    float m = 0.5f * (x[i] + y[i]), d = 0.5f * (x[i] - y[i]) * (float)width;
                    x[i] = m + d;
                    y[i] = m - d;
                }
            if (s.Reverse)
            {
                Array.Reverse(x);
                Array.Reverse(y);
            }
            // The end fades out over 10 ms (a reversed response ends on what was its start: that fades too).
            int fade = Math.Min(n / 2, (int)(0.01 * rate));
            Fade(x, y, n, fade);
            double e = 0;
            for (int i = 0; i < n; i++) e += (double)x[i] * x[i] + (double)y[i] * y[i];
            e *= 0.5;
            float norm = e > 1e-20 ? (float)(1 / Math.Sqrt(e)) : 1f;
            for (int i = 0; i < n; i++) { x[i] *= norm; y[i] *= norm; }
            outL = x;
            outR = y;
        }

        static void Fade(float[] x, float[] y, int n, int fade)
        {
            for (int i = 0; i < fade; i++)
            {
                // From 1 at the start of the fade to 0 at the last sample.
                float g = 0.5f + 0.5f * (float)PortableMath.Cos(Math.PI * (i + 1) / (fade + 1));
                x[n - fade + i] *= g;
                y[n - fade + i] *= g;
            }
        }

        static void Damp(float[] x, int start, int body, double rate, bool dark, double amount)
        {
            var f = new Biquad();
            if (dark) f.Set(BiquadType.LowPass, 1500, rate, 0.7071);
            else f.Set(BiquadType.LowPass, 800, rate, 0.7071);
            for (int i = 0; i < body; i++)
            {
                float v = x[start + i];
                float lowPassed = f.Process(v);
                float w = (float)(amount * i / body);
                x[start + i] = dark ? v + (lowPassed - v) * w : v - lowPassed * w;
            }
        }

        static void CutFilter(float[] x, double rate, double lowCut, double highCut)
        {
            var hp = new Biquad();
            var lp = new Biquad();
            bool useHp = lowCut > 21, useLp = highCut > 0 && highCut < 19900;
            if (useHp) hp.Set(BiquadType.HighPass, lowCut, rate, 0.7071);
            if (useLp) lp.Set(BiquadType.LowPass, Math.Min(highCut, rate * 0.45), rate, 0.7071);
            for (int i = 0; i < x.Length; i++)
            {
                float v = x[i];
                if (useHp) v = hp.Process(v);
                if (useLp) v = lp.Process(v);
                x[i] = v;
            }
        }
    }
}
