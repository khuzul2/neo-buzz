#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): filters. Zero-delay-feedback (topology-
// preserving transform) designs after Zavalishin, "The Art of VA Filter Design", and A. Simper's
// SVF; RBJ cookbook biquads for fixed EQ.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal enum SvfMode { LowPass, BandPass, HighPass, Notch, Peak, AllPass, Bell, LowShelf, HighShelf }

    /// <summary>
    /// State-variable filter, trapezoidal (TPT/ZDF): tuned exactly by prewarping, modulation-safe,
    /// every response from one structure. <see cref="Set"/> computes coefficients (call at control
    /// rate when modulated); <see cref="Process"/> runs one sample.
    /// </summary>
    internal struct Svf
    {
        float ic1, ic2;
        float a1, a2, a3, k;
        float m0, m1, m2;

        public SvfMode Mode;

        /// <summary>Coefficients for <paramref name="hz"/> at <paramref name="sampleRate"/>, resonance as Q (0.5 … 40), gain in dB for bell/shelves.</summary>
        public void Set(double hz, double sampleRate, double q, double gainDb = 0)
        {
            double f = Math.Clamp(hz, 1.0, sampleRate * 0.49);
            double g = Math.Tan(Math.PI * f / sampleRate);
            q = Math.Max(0.025, q);
            double kk = 1.0 / q;
            double A = Math.Pow(10.0, gainDb / 40.0);
            switch (Mode)
            {
                case SvfMode.Bell:
                    kk = 1.0 / (q * A);
                    break;
                case SvfMode.LowShelf:
                    g /= Math.Sqrt(A);
                    break;
                case SvfMode.HighShelf:
                    g *= Math.Sqrt(A);
                    break;
            }
            double aa1 = 1.0 / (1.0 + g * (g + kk));
            a1 = (float)aa1;
            a2 = (float)(g * aa1);
            a3 = (float)(g * g * aa1);
            k = (float)kk;
            switch (Mode)
            {
                case SvfMode.LowPass: m0 = 0; m1 = 0; m2 = 1; break;
                case SvfMode.BandPass: m0 = 0; m1 = 1; m2 = 0; break;
                case SvfMode.HighPass: m0 = 1; m1 = -k; m2 = -1; break;
                case SvfMode.Notch: m0 = 1; m1 = -k; m2 = 0; break;
                case SvfMode.Peak: m0 = 1; m1 = -k; m2 = -2; break;
                case SvfMode.AllPass: m0 = 1; m1 = -2 * k; m2 = 0; break;
                case SvfMode.Bell: m0 = 1; m1 = (float)(kk * (A * A - 1)); m2 = 0; break;
                case SvfMode.LowShelf: m0 = 1; m1 = (float)(kk * (A - 1)); m2 = (float)(A * A - 1); break;
                case SvfMode.HighShelf: m0 = (float)(A * A); m1 = (float)(kk * (1 - A) * A); m2 = (float)(1 - A * A); break;
            }
        }

        /// <summary>
        /// Fast coefficients for a modulated low/band/high-pass voice filter: <paramref name="g"/> is
        /// tan(π f / fs) (see <see cref="Prewarp"/>), <paramref name="kDamp"/> is 1/Q.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFast(float g, float kDamp)
        {
            float aa1 = 1f / (1f + g * (g + kDamp));
            a1 = aa1;
            a2 = g * aa1;
            a3 = g * g * aa1;
            k = kDamp;
            switch (Mode)
            {
                case SvfMode.LowPass: m0 = 0; m1 = 0; m2 = 1; break;
                case SvfMode.BandPass: m0 = 0; m1 = kDamp; m2 = 0; break;   // unity peak gain
                case SvfMode.HighPass: m0 = 1; m1 = -kDamp; m2 = -1; break;
                case SvfMode.Notch: m0 = 1; m1 = -kDamp; m2 = 0; break;
                case SvfMode.Peak: m0 = 1; m1 = -kDamp; m2 = -2; break;
                default: m0 = 1; m1 = -2 * kDamp; m2 = 0; break;
            }
        }

        /// <summary>tan(π f / fs), clamped below Nyquist.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Prewarp(float hz, float sampleRate)
        {
            float w = DspMath.Clamp(hz / sampleRate, 1e-6f, 0.497f);
            return MathF.Tan(MathF.PI * w);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float v0)
        {
            float v3 = v0 - ic2;
            float v1 = a1 * ic1 + a2 * v3;
            float v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2f * v1 - ic1;
            ic2 = 2f * v2 - ic2;
            return m0 * v0 + m1 * v1 + m2 * v2;
        }

        /// <summary>One sample, all outputs at once.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ProcessAll(float v0, out float low, out float band, out float high)
        {
            float v3 = v0 - ic2;
            float v1 = a1 * ic1 + a2 * v3;
            float v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2f * v1 - ic1;
            ic2 = 2f * v2 - ic2;
            low = v2;
            band = v1;
            high = v0 - k * v1 - v2;
        }

        public void Reset() { ic1 = ic2 = 0; }

        /// <summary>Flushes denormal state (call once per block).</summary>
        public void Flush()
        {
            ic1 = DspMath.Flush(ic1);
            ic2 = DspMath.Flush(ic2);
        }
    }

    /// <summary>
    /// Four-pole transistor ladder, zero-delay feedback, with the feedback path saturated so the
    /// resonance self-oscillates at a bounded level and exactly at the cutoff. LP24 is the fourth
    /// stage, LP12 the second.
    /// </summary>
    internal struct Ladder
    {
        float s1, s2, s3, s4;
        float G, k, gainComp;
        float c1, c2, c3, c4, g4, invDen;

        /// <summary><paramref name="g"/> = tan(π f / fs); <paramref name="resonance"/> 0 … 1 (1 self-oscillates).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(float g, float resonance)
        {
            G = g / (1f + g);
            k = 4.2f * DspMath.Clamp(resonance, 0f, 1f);
            // Passband loss of the ladder is 1/(1+k); give back about half of it, as most
            // ladder synths do, so resonance thins the sound without collapsing it.
            gainComp = 1f + 0.5f * k;
            // Each stage: y = G x + (1−G) s; the cascade's output in terms of its input u is
            // G⁴u + G³(1−G)s1 + G²(1−G)s2 + G(1−G)s3 + (1−G)s4.
            float gg = G, g2 = gg * gg, om = 1f - gg;
            g4 = g2 * g2;
            c1 = g2 * gg * om;
            c2 = g2 * om;
            c3 = gg * om;
            c4 = om;
            invDen = 1f / (1f + k * g4);
        }

        /// <summary>
        /// One sample. Inside, signals run at half scale into a tanh at the ladder's input (the
        /// differential pair), which bounds self-oscillation (about ±0.5 out at drive 1).
        /// <paramref name="drive"/> ≥ 1 pushes the signal into that saturation; the output is
        /// scaled back, so drive adds harmonics rather than level.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float x, float drive, bool lp12)
        {
            float g = G;
            float sigma = c1 * s1 + c2 * s2 + c3 * s3 + c4 * s4;
            float d = drive > 1f ? drive * 0.5f : 0.5f;
            float xin = x * gainComp * d;
            float y4est = (g4 * xin + sigma) * invDen;
            float u = DspMath.FastTanh(xin - k * y4est);

            float v = (u - s1) * g; float y1 = v + s1; s1 = y1 + v;
            v = (y1 - s2) * g; float y2 = v + s2; s2 = y2 + v;
            v = (y2 - s3) * g; float y3 = v + s3; s3 = y3 + v;
            v = (y3 - s4) * g; float y4 = v + s4; s4 = y4 + v;
            return (lp12 ? y2 : y4) * (drive > 1f ? 2f / drive : 2f);
        }

        public void Reset() { s1 = s2 = s3 = s4 = 0; }

        public void Flush()
        {
            s1 = DspMath.Flush(s1); s2 = DspMath.Flush(s2);
            s3 = DspMath.Flush(s3); s4 = DspMath.Flush(s4);
        }
    }

    /// <summary>One-pole low/high-pass, trapezoidal.</summary>
    internal struct OnePole
    {
        float s, g;

        public void Set(double hz, double sampleRate)
        {
            double w = Math.Tan(Math.PI * Math.Clamp(hz, 0.1, sampleRate * 0.49) / sampleRate);
            g = (float)(w / (1 + w));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float LowPass(float x)
        {
            float v = (x - s) * g;
            float y = v + s;
            s = y + v;
            return y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float HighPass(float x) => x - LowPass(x);

        public void Reset() { s = 0; }

        public void Flush() { s = DspMath.Flush(s); }
    }

    internal enum BiquadType { LowPass, HighPass, BandPass, Notch, Peak, LowShelf, HighShelf }

    /// <summary>RBJ cookbook biquad, transposed direct form II in double precision (accurate at low frequencies).</summary>
    internal struct Biquad
    {
        double b0, b1, b2, a1, a2, z1, z2;

        public void Set(BiquadType type, double hz, double sampleRate, double q, double gainDb = 0)
        {
            double w0 = 2 * Math.PI * Math.Clamp(hz, 1.0, sampleRate * 0.49) / sampleRate;
            double cs = Math.Cos(w0), sn = Math.Sin(w0);
            double alpha = sn / (2 * Math.Max(0.01, q));
            double A = Math.Pow(10, gainDb / 40);
            double nb0, nb1, nb2, na0, na1, na2;
            switch (type)
            {
                case BiquadType.LowPass:
                    nb0 = (1 - cs) / 2; nb1 = 1 - cs; nb2 = (1 - cs) / 2; na0 = 1 + alpha; na1 = -2 * cs; na2 = 1 - alpha; break;
                case BiquadType.HighPass:
                    nb0 = (1 + cs) / 2; nb1 = -(1 + cs); nb2 = (1 + cs) / 2; na0 = 1 + alpha; na1 = -2 * cs; na2 = 1 - alpha; break;
                case BiquadType.BandPass:
                    nb0 = alpha; nb1 = 0; nb2 = -alpha; na0 = 1 + alpha; na1 = -2 * cs; na2 = 1 - alpha; break;
                case BiquadType.Notch:
                    nb0 = 1; nb1 = -2 * cs; nb2 = 1; na0 = 1 + alpha; na1 = -2 * cs; na2 = 1 - alpha; break;
                case BiquadType.Peak:
                    nb0 = 1 + alpha * A; nb1 = -2 * cs; nb2 = 1 - alpha * A; na0 = 1 + alpha / A; na1 = -2 * cs; na2 = 1 - alpha / A; break;
                case BiquadType.LowShelf:
                {
                    double sq = 2 * Math.Sqrt(A) * alpha;
                    nb0 = A * ((A + 1) - (A - 1) * cs + sq);
                    nb1 = 2 * A * ((A - 1) - (A + 1) * cs);
                    nb2 = A * ((A + 1) - (A - 1) * cs - sq);
                    na0 = (A + 1) + (A - 1) * cs + sq;
                    na1 = -2 * ((A - 1) + (A + 1) * cs);
                    na2 = (A + 1) + (A - 1) * cs - sq;
                    break;
                }
                default:
                {
                    double sq = 2 * Math.Sqrt(A) * alpha;
                    nb0 = A * ((A + 1) + (A - 1) * cs + sq);
                    nb1 = -2 * A * ((A - 1) + (A + 1) * cs);
                    nb2 = A * ((A + 1) + (A - 1) * cs - sq);
                    na0 = (A + 1) - (A - 1) * cs + sq;
                    na1 = 2 * ((A - 1) - (A + 1) * cs);
                    na2 = (A + 1) - (A - 1) * cs - sq;
                    break;
                }
            }
            b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0; a1 = na1 / na0; a2 = na2 / na0;
        }

        /// <summary>Passes everything unchanged.</summary>
        public void SetIdentity() { b0 = 1; b1 = b2 = a1 = a2 = 0; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float x)
        {
            double y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return (float)y;
        }

        /// <summary>Magnitude response at <paramref name="hz"/> (for editors and tests).</summary>
        public double Magnitude(double hz, double sampleRate)
        {
            double w = 2 * Math.PI * hz / sampleRate;
            double cr = Math.Cos(w), ci = -Math.Sin(w), c2r = Math.Cos(2 * w), c2i = -Math.Sin(2 * w);
            double nr = b0 + b1 * cr + b2 * c2r, ni = b1 * ci + b2 * c2i;
            double dr = 1 + a1 * cr + a2 * c2r, di = a1 * ci + a2 * c2i;
            return Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
        }

        public void Reset() { z1 = z2 = 0; }

        public void Flush()
        {
            z1 = DspMath.Flush(z1);
            z2 = DspMath.Flush(z2);
        }
    }

    /// <summary>DC blocker: one zero at DC, one pole just inside (about 5 Hz).</summary>
    internal struct DcBlocker
    {
        float x1, y1, r;

        public void Set(double sampleRate, double hz = 5)
        {
            r = (float)(1 - 2 * Math.PI * hz / sampleRate);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float x)
        {
            float y = x - x1 + r * y1;
            x1 = x;
            y1 = DspMath.Flush(y);
            return y;
        }

        public void Reset() { x1 = y1 = 0; }
    }
}
