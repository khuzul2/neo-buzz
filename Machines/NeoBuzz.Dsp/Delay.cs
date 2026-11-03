#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): fractional delay lines, allpass diffusers
// and level meters.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>Delay line with fractional, interpolated reads (power-of-two ring).</summary>
    internal sealed class FracDelay
    {
        readonly float[] data;
        readonly int mask;
        int pos;

        public FracDelay(int maxDelaySamples)
        {
            int size = 4;
            while (size < maxDelaySamples + 4) size <<= 1;
            data = new float[size];
            mask = size - 1;
        }

        public int MaxDelay => mask - 3;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(float x)
        {
            pos = (pos + 1) & mask;
            data[pos] = x;
        }

        /// <summary>Integer delay: 0 is the last sample written.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Read(int delay) => data[(pos - delay) & mask];

        /// <summary>Fractional delay ≥ 1, linear interpolation.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ReadLinear(float delay)
        {
            int i = (int)delay;
            float f = delay - i;
            float a = data[(pos - i) & mask];
            float b = data[(pos - i - 1) & mask];
            return a + (b - a) * f;
        }

        /// <summary>Fractional delay ≥ 1, 4-point cubic (Catmull-Rom) interpolation.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ReadCubic(float delay)
        {
            int i = (int)delay;
            float x = delay - i;
            float y0 = data[(pos - i + 1) & mask];
            float y1 = data[(pos - i) & mask];
            float y2 = data[(pos - i - 1) & mask];
            float y3 = data[(pos - i - 2) & mask];
            float c1 = 0.5f * (y2 - y0);
            float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            return ((c3 * x + c2) * x + c1) * x + y1;
        }

        public void Reset()
        {
            Array.Clear(data, 0, data.Length);
            pos = 0;
        }
    }

    /// <summary>Schroeder allpass with a (modulatable) fractional delay: diffusion without colour.</summary>
    internal sealed class AllpassDiffuser
    {
        readonly FracDelay line;

        public AllpassDiffuser(int maxDelay)
        {
            line = new FracDelay(maxDelay + 2);
        }

        public float Delay = 100;
        public float Gain = 0.6f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float x)
        {
            float d = line.ReadLinear(Math.Max(1f, Delay));
            float v = x + Gain * d;
            v = DspMath.Flush(v);
            line.Write(v);
            return d - Gain * v;
        }

        public void Reset() => line.Reset();
    }

    /// <summary>Peak meter with a held peak and a smooth fall, read by editors (any thread).</summary>
    internal sealed class LevelMeter
    {
        volatile float peak;
        float fall = 0.9995f;

        public void SetSampleRate(float sampleRate)
        {
            // About 20 dB per second.
            fall = PortableMathF.Pow(10f, -20f / 20f / sampleRate);
        }

        /// <summary>Audio thread: one block of samples.</summary>
        public void Feed(float[] x, int offset, int n)
        {
            float p = peak;
            float decay = PortableMathF.Pow(fall, n);
            p *= decay;
            for (int i = 0; i < n; i++)
            {
                float a = MathF.Abs(x[offset + i]);
                if (a > p) p = a;
            }
            peak = p;
        }

        /// <summary>Audio thread: one stereo block.</summary>
        public void Feed(float[] left, float[] right, int n)
        {
            float p = peak * PortableMathF.Pow(fall, n);
            for (int i = 0; i < n; i++)
            {
                float a = MathF.Max(MathF.Abs(left[i]), MathF.Abs(right[i]));
                if (a > p) p = a;
            }
            peak = p;
        }

        /// <summary>Audio thread: a value already in linear amplitude (gain reduction, levels).</summary>
        public void FeedValue(float a, int n)
        {
            float p = peak * PortableMathF.Pow(fall, n);
            peak = a > p ? a : p;
        }

        public void Set(float value) => peak = value;

        public float Peak => peak;

        public float PeakDb => DspMath.GainToDb(peak);
    }
}
