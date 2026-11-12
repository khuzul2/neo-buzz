#nullable disable
// Neo Sampler (docs/specs/machines.md §5): the waves it plays. A layer is one wave of a wavetable
// slot (ReBuzz's waves hold one or more layers, each with its own root note), copied once into
// float channels at the wave's own rate, padded with silence, with octave-down copies (levels) for
// playing it more than an octave up: each level is the one before through a 79-tap Kaiser halfband
// (passband to 0.21 of the rate, stopband from 0.29, about −100 dB), every other sample kept, so a
// level's sample j sits at the level before's sample 2j. A layer with a loop of its own also gets a
// playback copy in which the loop runs on past its end (forward, or unrolled back and forth for a
// ping-pong loop): reading near the loop's end then reads what plays next, and the position jumps
// back by whole periods where the copy and the loop are the same samples, so the loop has no seam
// at any pitch. Built off the audio thread; the audio thread only reads.

using System;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Sampler
{
    internal enum WaveLoop { None, Forward, PingPong }

    internal sealed class SampleLayer
    {
        /// <summary>Silent samples before and after every level (more than the kernel reads).</summary>
        public const int Pad = 32;
        public const int MaxLevels = 6;
        /// <summary>Columns of the editor's overview.</summary>
        public const int OverviewColumns = 512;
        const int HalfbandM = 39;

        public string Name { get; private set; }
        /// <summary>Frames of the wave.</summary>
        public int Length { get; private set; }
        public int Rate { get; private set; }
        /// <summary>MIDI note it plays unchanged at (60 = C4).</summary>
        public int Root { get; private set; }
        /// <summary>Its own loop (frames, end exclusive), and kind; None when it has none.</summary>
        public int LoopStart { get; private set; }
        public int LoopEnd { get; private set; }
        public WaveLoop Loop { get; private set; }
        public float Gain { get; private set; } = 1;
        public bool Stereo { get; private set; }

        /// <summary>Levels: [k] is the wave at a 2^k-th of its rate, padded by <see cref="Pad"/>. Right is Left for a mono wave.</summary>
        public float[][] Left { get; private set; }
        public float[][] Right { get; private set; }
        public int Levels => Left.Length;

        /// <summary>The playback copy with the loop running on (null without a loop of its own), and where the position jumps back, by how much (frames).</summary>
        public float[][] LoopLeft { get; private set; }
        public float[][] LoopRight { get; private set; }
        public double LoopJumpAt { get; private set; }
        public double LoopJumpBy { get; private set; }

        /// <summary>Min and max of the (mixed) wave per column, for the editor.</summary>
        public float[] Overview { get; private set; }
        public float Peak { get; private set; }

        static readonly double[] halfband = Halfband();

        static double[] Halfband()
        {
            var h = new double[2 * HalfbandM + 1];
            double beta = 10, i0 = Interpolation.BesselI0(beta), sum = 0;
            for (int m = -HalfbandM; m <= HalfbandM; m++)
            {
                double v;
                if (m == 0) v = 0.5;
                else if ((m & 1) == 0) v = 0;
                else
                {
                    double x = m / (double)(HalfbandM + 1);
                    v = PortableMath.Sin(Math.PI * m / 2) / (Math.PI * m) * Interpolation.BesselI0(beta * Math.Sqrt(1 - x * x)) / i0;
                }
                h[m + HalfbandM] = v;
                sum += v;
            }
            for (int i = 0; i < h.Length; i++) h[i] /= sum;
            return h;
        }

        /// <summary>
        /// A layer from its samples (<paramref name="right"/> null for a mono wave). Non-finite
        /// samples become silence. Loop points outside the wave, or a loop shorter than 2 frames, mean no loop.
        /// </summary>
        public static SampleLayer Build(string name, float[] left, float[] right, int rate, int root,
            int loopStart = 0, int loopEnd = 0, WaveLoop loop = WaveLoop.None, float gain = 1)
        {
            int n = left?.Length ?? 0;
            var layer = new SampleLayer
            {
                Name = name ?? "",
                Length = n,
                Rate = rate > 0 ? rate : 44100,
                Root = Math.Clamp(root, 0, 127),
                Gain = float.IsFinite(gain) ? Math.Clamp(gain, 0f, 16f) : 1f,
                Stereo = right != null,
            };
            var l = Sanitise(left, n);
            var r = right != null ? Sanitise(right, n) : null;
            int levels = 1;
            while (levels < MaxLevels && (n >> levels) >= 32) levels++;
            layer.Left = Mipmaps(l, levels);
            layer.Right = r != null ? Mipmaps(r, levels) : layer.Left;

            if (loop != WaveLoop.None && loopStart >= 0 && loopEnd <= n && loopEnd - loopStart >= 2)
            {
                layer.LoopStart = loopStart;
                layer.LoopEnd = loopEnd;
                layer.Loop = loop;
                // Where the jump back lands must be a period into the loop, with the levels' reach
                // (the kernel's and the halfbands', at the highest level) inside it on both sides.
                int support = (Interpolation.Half + 2 + HalfbandM) << (levels - 1);
                int period = loop == WaveLoop.PingPong ? 2 * (loopEnd - loopStart) : loopEnd - loopStart;
                double jumpAt = loopStart + support + period;
                int extension = (int)(jumpAt - loopEnd) + support + (64 << levels);
                layer.LoopJumpAt = jumpAt;
                layer.LoopJumpBy = period;
                layer.LoopLeft = Mipmaps(Extend(l, loopStart, loopEnd, loop, extension), levels);
                layer.LoopRight = r != null ? Mipmaps(Extend(r, loopStart, loopEnd, loop, extension), levels) : layer.LoopLeft;
            }

            var ov = new float[2 * OverviewColumns];
            float peak = 0;
            if (n > 0)
                for (int c = 0; c < OverviewColumns; c++)
                {
                    int a = (int)((long)c * n / OverviewColumns), b = Math.Max(a + 1, (int)((long)(c + 1) * n / OverviewColumns));
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int i = a; i < b && i < n; i++)
                    {
                        float v = r != null ? 0.5f * (l[i] + r[i]) : l[i];
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                        float m = Math.Max(Math.Abs(l[i]), r != null ? Math.Abs(r[i]) : 0);
                        if (m > peak) peak = m;
                    }
                    ov[2 * c] = lo == float.MaxValue ? 0 : lo;
                    ov[2 * c + 1] = hi == float.MinValue ? 0 : hi;
                }
            layer.Overview = ov;
            layer.Peak = peak;
            return layer;
        }

        static float[] Sanitise(float[] x, int n)
        {
            var y = new float[n];
            for (int i = 0; i < n; i++) y[i] = float.IsFinite(x[i]) ? x[i] : 0f;
            return y;
        }

        /// <summary>The wave up to its loop's end, then the loop running on for <paramref name="extension"/> frames.</summary>
        static float[] Extend(float[] x, int ls, int le, WaveLoop loop, int extension)
        {
            var y = new float[le + extension];
            Array.Copy(x, y, le);
            int len = le - ls;
            for (int m = 0; m < extension; m++)
            {
                int src;
                if (loop == WaveLoop.Forward) src = ls + m % len;
                else
                {
                    // Unrolled: back from the loop's end to its start, then forward again (each end sample twice).
                    int u = (len + m) % (2 * len);
                    src = u < len ? ls + u : le - 1 - (u - len);
                }
                y[le + m] = x[src];
            }
            return y;
        }

        static float[][] Mipmaps(float[] x, int levels)
        {
            var result = new float[levels][];
            result[0] = Padded(x, x.Length);
            int len = x.Length;
            for (int k = 1; k < levels; k++)
            {
                var src = result[k - 1];
                int outLen = (len + 1) / 2;
                var y = new float[outLen + 2 * Pad];
                for (int j = 0; j < outLen; j++)
                {
                    // Centred: level k's sample j is level k−1's sample 2j (silence outside the wave).
                    int c = 2 * j;
                    double acc = 0.5 * Sample(src, c, len);
                    for (int m = 1; m <= HalfbandM; m += 2)
                        acc += halfband[HalfbandM + m] * (Sample(src, c - m, len) + Sample(src, c + m, len));
                    y[Pad + j] = (float)acc;
                }
                result[k] = y;
                len = outLen;
            }
            return result;
        }

        static double Sample(float[] padded, int i, int len) => i < 0 || i >= len ? 0.0 : padded[Pad + i];

        static float[] Padded(float[] x, int n)
        {
            var y = new float[n + 2 * Pad];
            Array.Copy(x, 0, y, Pad, n);
            return y;
        }

        /// <summary>Frames of level <paramref name="k"/> (without padding).</summary>
        public int LevelLength(int k) => Left[k].Length - 2 * Pad;
    }
}
