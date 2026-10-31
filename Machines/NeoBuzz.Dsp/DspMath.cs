#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4). Pure: no Buzz or WPF references, linked
// as source into every Neo machine and into NeoBuzz.MachineTests.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>Small numeric helpers used across the library.</summary>
    internal static class DspMath
    {
        public const float Pi = MathF.PI;
        public const float TwoPi = 2f * MathF.PI;
        public const double TwoPiD = 2.0 * Math.PI;

        /// <summary>Buzz volumes are ±32768; the engines work in ±1.</summary>
        public const float BuzzScale = 32768f;

        public static float DbToGain(float db) => MathF.Pow(10f, db * 0.05f);
        public static double DbToGain(double db) => Math.Pow(10.0, db * 0.05);
        public static float GainToDb(float gain) => gain > 1e-12f ? 20f * MathF.Log10(gain) : -240f;
        public static double GainToDb(double gain) => gain > 1e-12 ? 20.0 * Math.Log10(gain) : -240.0;

        /// <summary>MIDI note (a Buzz note's octave × 12 + semitone) to Hz, A-5 = MIDI 69 = 440 Hz.</summary>
        public static double NoteToHz(double note) => 440.0 * Math.Pow(2.0, (note - 69.0) / 12.0);

        public static double HzToNote(double hz) => 69.0 + 12.0 * Math.Log2(hz / 440.0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp(float x, float lo, float hi) => x < lo ? lo : x > hi ? hi : x;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Clamp(double x, double lo, double hi) => x < lo ? lo : x > hi ? hi : x;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>Flushes tiny values to zero so feedback paths never run on denormals.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Flush(float x) => x > -1e-18f && x < 1e-18f ? 0f : x;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Flush(double x) => x > -1e-30 && x < 1e-30 ? 0.0 : x;

        /// <summary>tanh accurate to ~1e-4 (rational approximation, clamped), cheap enough per sample.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float FastTanh(float x)
        {
            if (x > 4.97f) return 1f;
            if (x < -4.97f) return -1f;
            float x2 = x * x;
            float a = x * (135135f + x2 * (17325f + x2 * (378f + x2)));
            float b = 135135f + x2 * (62370f + x2 * (3150f + x2 * 28f));
            return a / b;
        }

        /// <summary>2^x for |x| up to ~60, relative error ~1e-7.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Exp2(float x) => MathF.Pow(2f, x);

        /// <summary>One-pole coefficient for a time constant (time to 1 − 1/e) at a sample rate.</summary>
        public static float OnePoleCoeff(double seconds, double sampleRate) =>
            seconds <= 0 ? 1f : (float)(1.0 - Math.Exp(-1.0 / (seconds * sampleRate)));

        /// <summary>Equal-power pan: −1 left … +1 right.</summary>
        public static void PanGains(float pan, out float left, out float right)
        {
            float a = (Clamp(pan, -1f, 1f) + 1f) * 0.25f * Pi;
            left = MathF.Cos(a) * 1.41421356f;
            right = MathF.Sin(a) * 1.41421356f;
        }
    }

    /// <summary>xorshift32: fast, allocation-free noise source with a settable seed.</summary>
    internal struct Rng
    {
        uint state;

        public Rng(uint seed) { state = seed == 0 ? 0x9E3779B9u : seed; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint NextUInt()
        {
            uint x = state == 0 ? 0x9E3779B9u : state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }

        /// <summary>Uniform in [−1, 1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextBipolar() => (int)NextUInt() * (1f / 2147483648f);

        /// <summary>Uniform in [0, 1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextUnipolar() => (NextUInt() >> 8) * (1f / 16777216f);
    }

    /// <summary>Pink noise (Paul Kellet's refined filter on white noise), about −3 dB/octave.</summary>
    internal struct PinkNoise
    {
        float b0, b1, b2, b3, b4, b5, b6;

        public float Next(float white)
        {
            b0 = 0.99886f * b0 + white * 0.0555179f;
            b1 = 0.99332f * b1 + white * 0.0750759f;
            b2 = 0.96900f * b2 + white * 0.1538520f;
            b3 = 0.86650f * b3 + white * 0.3104856f;
            b4 = 0.55000f * b4 + white * 0.5329522f;
            b5 = -0.7616f * b5 - white * 0.0168980f;
            float pink = b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362f;
            b6 = white * 0.115926f;
            return pink * 0.11f;
        }
    }
}
