#nullable disable
// Neo machines shared DSP (docs/specs/mastering-tools.md §5.9, M5): word-length reduction with dither, for Neo Dither
// and for the agent's render(bits, dither).
//  - The output is exactly on the grid k / 2^(bits−1), k an integer in [−2^(bits−1), 2^(bits−1) − 1] (a float holds every
//    such value for 24 bits or fewer), so writing it as PCM is exact.
//  - TPDF dither: the difference of two uniform numbers in [0, 1) from two xorshift32 generators, ±1 LSB (variance 1/6 LSB²);
//    with the rounding error (1/12) the total noise is LSB²/4 and independent of the signal. Seeded at Reset, so a render
//    gives the same bits every time.
//  - Noise shaping by error feedback: q = round(u + d), u = v + Σ b_k·e(n−k), e = q − u: the noise q − v is the
//    quantiser's error filtered by NTF(z) = 1 + Σ b_k z^−k. Shapes (designed for 44.1 and 48 kHz; at higher rates the same
//    curve sits higher in frequency): Light, a two-tap curve that takes the noise out of the low end (see <see cref="Shapes"/>); Psychoacoustic, an
//    ear-weighted five-tap curve that puts the noise away from 2 – 5 kHz.
//  - A clipped sample resets the feedback (the error would otherwise carry the clip into the next samples).
// Allocation-free after construction; double arithmetic with the same operations everywhere.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal enum DitherShape { Off, Light, Psychoacoustic }

    internal sealed class Dither
    {
        /// <summary>The b_k of NTF(z) = 1 + Σ b_k z^−k for each shape (k = 1 …).</summary>
        public static readonly double[][] Shapes =
        {
            new double[0],
            new[] { -0.8, 0.25 },
            new[] { -2.033, 2.165, -1.959, 1.590, -0.6149 },
        };

        readonly double[] err = new double[8];
        double[] b = Shapes[0];
        int pos;
        uint s1, s2;
        double scale = 32768, inv = 1.0 / 32768;
        double lo = -32768, hi = 32767;
        bool tpdf = true;
        readonly uint seed;

        public Dither(int bits, bool tpdf, DitherShape shape, uint seed = 1)
        {
            this.seed = seed;
            Configure(bits, tpdf, shape);
            Reset();
        }

        /// <summary>Changes the word length, dither and shape; the generators and the feedback continue.</summary>
        public void Configure(int bits, bool tpdf, DitherShape shape)
        {
            bits = Math.Clamp(bits, 2, 24);
            scale = Math.Pow(2, bits - 1);
            inv = 1.0 / scale;
            lo = -scale;
            hi = scale - 1;
            this.tpdf = tpdf;
            b = Shapes[(int)shape];
        }

        public void Reset()
        {
            s1 = 2463534242u ^ (seed * 2654435761u);
            s2 = 1013904223u ^ (seed * 40503u);
            if (s1 == 0) s1 = 1;
            if (s2 == 0) s2 = 1;
            Array.Clear(err);
            pos = 0;
        }

        /// <summary>Forgets the feedback (after digital silence).</summary>
        public void ClearError()
        {
            Array.Clear(err);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double Uniform(ref uint s)
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return (s >> 8) * (1.0 / 16777216.0);
        }

        /// <summary>One sample (±1 full scale) to the grid.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float x)
        {
            double v = (double)x * scale;
            double u = v;
            var bb = b;
            for (int k = 0; k < bb.Length; k++) u += bb[k] * err[(pos - 1 - k) & 7];
            double d = tpdf ? Uniform(ref s1) - Uniform(ref s2) : 0;
            double q = Math.Floor(u + d + 0.5);
            double e = q - u;
            if (q > hi) { q = hi; e = 0; }
            else if (q < lo) { q = lo; e = 0; }
            err[pos & 7] = e;
            pos++;
            return (float)(q * inv);
        }
    }
}
