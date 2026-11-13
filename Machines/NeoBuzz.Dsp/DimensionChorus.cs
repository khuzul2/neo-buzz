#nullable disable
// Neo machines shared DSP (docs/specs/dye-essence.md section 4.10, P4b1): a chorus that widens, in the manner of the
// 1970s "dimension" boxes: two to four short modulated delays a channel, the two channels modulated half a cycle
// apart (so the left and the right drift in opposite directions), a high-pass and a low-pass on the wet signal and an
// optional feedback that turns it into a flanger. It returns the wet signal only: the blend with the dry is the
// caller's, and it has no latency. The planned Neo Learn synths use it as their chorus.
// Allocation-free after construction; the modulation is a polynomial sine and a filtered noise (IEEE basic operations
// only), so the same bits on every platform.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class DimensionChorus
    {
        public const int MaxVoices = 4;
        public const float MaxDelayMs = 25;

        const float MaxModFraction = 0.6f;
        const double DriftHz = 2.5;

        readonly float sr;
        readonly FracDelay dlL, dlR;
        readonly float[] driftState = new float[2 * MaxVoices];
        Biquad lpL, lpR, hpL, hpR;
        Rng rng = new Rng(0xD1E5D1E5u);
        float phase, driftCoeff, driftGain, fbL, fbR;
        double toneApplied = -1, lowApplied = -1;

        /// <summary>2, 3 or 4 delays a channel.</summary>
        public int Voices = 3;
        /// <summary>The modulation rate (Hz).</summary>
        public float RateHz = 0.6f;
        /// <summary>The modulation depth, 0 ... 1 (of the delay, at most 60 %).</summary>
        public float Depth = 0.5f;
        /// <summary>The delays' centre (ms), 1 ... 25.</summary>
        public float DelayMs = 6;
        /// <summary>0 (both channels the same, mono wet) ... 1 (full stereo).</summary>
        public float Width = 1;
        /// <summary>A triangle modulation instead of the sine.</summary>
        public bool Triangle;
        /// <summary>The wet low-pass (Hz).</summary>
        public double ToneHz = 12000;
        /// <summary>The wet high-pass (Hz).</summary>
        public double LowCutHz = 120;
        /// <summary>A slow random wander of the delays, 0 ... 1.</summary>
        public float Drift = 0.15f;
        /// <summary>The wet fed back into the delays, -0.9 ... 0.9.</summary>
        public float Feedback;

        public DimensionChorus(float sampleRate)
        {
            sr = sampleRate;
            int max = (int)(sampleRate * MaxDelayMs * 0.001f * (1 + MaxModFraction) * 1.3f) + 8;
            dlL = new FracDelay(max);
            dlR = new FracDelay(max);
            driftCoeff = (float)(1 - PortableMath.Exp(-2 * Math.PI * DriftHz / sampleRate));
            // The filtered noise has a standard deviation of sqrt(c / (2 - c)) / sqrt(3): scaled to about 1.
            driftGain = (float)(Math.Sqrt((2 - driftCoeff) / driftCoeff) * Math.Sqrt(3.0) * 0.5);
            Reset();
        }

        public int Latency => 0;

        public void Reset()
        {
            dlL.Reset();
            dlR.Reset();
            lpL.Reset(); lpR.Reset(); hpL.Reset(); hpR.Reset();
            Array.Clear(driftState);
            phase = 0;
            fbL = fbR = 0;
            toneApplied = lowApplied = -1;
        }

        /// <summary>A polynomial sine of a phase in cycles (any value): -1 ... 1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Sine(float cycles)
        {
            float x = cycles - MathF.Floor(cycles + 0.5f);   // -0.5 ... 0.5
            float y = 4 * x * (1 - 2 * MathF.Abs(x));
            return 0.225f * (y * MathF.Abs(y) - y) + y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Tri(float cycles)
        {
            float x = cycles - MathF.Floor(cycles);
            return x < 0.5f ? 4 * x - 1 : 3 - 4 * x;
        }

        void Setup()
        {
            double tone = Math.Clamp(ToneHz, 500, 20000), low = Math.Clamp(LowCutHz, 10, 2000);
            if (tone != toneApplied)
            {
                toneApplied = tone;
                lpL.Set(BiquadType.LowPass, tone, sr, 0.7071);
                lpR.Set(BiquadType.LowPass, tone, sr, 0.7071);
            }
            if (low != lowApplied)
            {
                lowApplied = low;
                hpL.Set(BiquadType.HighPass, low, sr, 0.7071);
                hpR.Set(BiquadType.HighPass, low, sr, 0.7071);
            }
        }

        /// <summary>Turns the stereo block into its wet signal, in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] l, float[] r, int n)
        {
            Setup();
            int voices = Math.Clamp(Voices, 2, MaxVoices);
            float baseSamples = Math.Clamp(DelayMs, 1f, MaxDelayMs) * 0.001f * sr;
            float depth = Math.Clamp(Depth, 0f, 1f) * MaxModFraction * baseSamples;
            float driftSamples = Math.Clamp(Drift, 0f, 1f) * 0.12f * baseSamples;
            float inc = Math.Clamp(RateHz, 0.01f, 20f) / sr;
            float fb = Math.Clamp(Feedback, -0.9f, 0.9f);
            float width = Math.Clamp(Width, 0f, 1f);
            float gain = 1f / MathF.Sqrt(voices);
            float direct = 0.5f * (1 + width), cross = 0.5f * (1 - width);
            bool tri = Triangle;
            float maxDelay = dlL.MaxDelay - 4;
            for (int i = 0; i < n; i++)
            {
                dlL.Write(l[i] + fb * fbL);
                dlR.Write(r[i] + fb * fbR);
                float sl = 0, sr2 = 0;
                for (int v = 0; v < voices; v++)
                {
                    float p = phase + (float)v / voices;
                    float ml = tri ? Tri(p) : Sine(p);
                    float mr = tri ? Tri(p + 0.5f) : Sine(p + 0.5f);
                    int dl = 2 * v, dr = 2 * v + 1;
                    driftState[dl] += (rng.NextBipolar() - driftState[dl]) * driftCoeff;
                    driftState[dr] += (rng.NextBipolar() - driftState[dr]) * driftCoeff;
                    float tl = baseSamples + depth * ml + driftSamples * driftGain * driftState[dl];
                    float tr = baseSamples + depth * mr + driftSamples * driftGain * driftState[dr];
                    sl += dlL.ReadCubic(Math.Clamp(tl, 2f, maxDelay));
                    sr2 += dlR.ReadCubic(Math.Clamp(tr, 2f, maxDelay));
                }
                sl *= gain;
                sr2 *= gain;
                float wl = direct * sl + cross * sr2, wr = direct * sr2 + cross * sl;
                wl = lpL.Process(hpL.Process(wl));
                wr = lpR.Process(hpR.Process(wr));
                fbL = DspMath.Flush(wl);
                fbR = DspMath.Flush(wr);
                l[i] = wl;
                r[i] = wr;
                phase += inc;
                if (phase >= 1f) phase -= 1f;
            }
        }
    }
}
