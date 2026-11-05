#nullable disable
// Neo Drums (docs/specs/machines.md §5): the metallic voices' oscillators. Up to six square waves (the TR-808's six
// Schmitt-trigger oscillators for its hats and cymbals, two for its cowbell), each a naive ±1 square whose edges are
// corrected with the shared band-limited steps (NeoBuzz.Dsp BlepTable, a 48-sample windowed sinc: images under about
// −120 dB), summed into one correction buffer. The buffer runs BlepTable.Z samples late, so a hit starts the bank
// 2·Z + 1 samples early and runs it Z ahead: its first sample is the free-running bank's own, band-limited, without
// the start-up's step. Pure; no allocation after construction.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Drums
{
    internal sealed class MetalBank
    {
        public const int Max = 6;
        const int Z = BlepTable.Z;

        readonly double[] phase = new double[Max];
        readonly double[] inc = new double[Max];
        readonly float[] state = new float[Max];
        float amp;
        int count;
        BlepBuffer buf;

        public MetalBank()
        {
            buf.Init();
        }

        /// <summary>
        /// Starts <paramref name="n"/> squares at <paramref name="hz"/> (each below a fifth of the rate), each with phase
        /// <paramref name="phases"/>[i] at the hit's first sample, each at 1/n of full scale (the sum stays within ±1).
        /// </summary>
        public void Start(double[] hz, double[] phases, int n, double sampleRate)
        {
            count = Math.Min(n, Max);
            amp = 1f / count;
            buf.Reset();
            int lead = 2 * Z + 1;
            for (int i = 0; i < count; i++)
            {
                inc[i] = Math.Min(hz[i] / sampleRate, 0.2);
                // The phase lead + Z samples before the hit's first output sample… the bank then runs Z samples ahead.
                double p = phases[i] - (lead - Z) * inc[i];
                p -= Math.Floor(p);
                phase[i] = p;
                state[i] = p < 0.5 ? 1f : -1f;
            }
            for (int k = 0; k < lead - 1; k++) Step();
        }

        /// <summary>The next sample of the band-limited sum (the bank's sample Z steps back).</summary>
        public float Next() => Step();

        float Step()
        {
            float naive = 0;
            for (int i = 0; i < count; i++)
            {
                double p = phase[i] + inc[i];
                double d = inc[i];
                if (state[i] > 0 && p >= 0.5)
                {
                    // Falling edge, (p − 0.5) / inc samples ago.
                    buf.AddStep(-2f * amp, (float)((p - 0.5) / d));
                    state[i] = -1f;
                }
                if (p >= 1.0)
                {
                    p -= 1.0;
                    buf.AddStep(2f * amp, (float)(p / d));
                    state[i] = 1f;
                }
                phase[i] = p;
                naive += state[i] * amp;
            }
            return buf.Next(naive);
        }

        /// <summary>The TR-808's six metallic oscillators (Hz), for its hats and cymbals.</summary>
        public static readonly double[] Tr808 = { 205.3, 304.4, 369.6, 522.7, 540.0, 800.0 };

        /// <summary>Where each oscillator starts (fractions of a cycle): fixed, so every hit is the same.</summary>
        public static readonly double[] StartPhases = { 0.0, 0.37, 0.71, 0.13, 0.52, 0.89 };
    }
}
