#nullable disable
// Neo machines shared DSP (docs/specs/mastering-tools.md M3): FirOversampler's filters, faster. The same
// linear-phase halfband designs, stages, padding and latency, but each polyphase branch runs over the whole block
// at once: its outputs side by side in the lanes of a vector, each tap broadcast and multiplied into the window it
// meets, the taps added in time order into each output, four vectors of outputs at a time (so four independent
// sums are in flight). FirOversampler sums one output's tap pairs at a time, a chain of dependent additions.
// The sums are in a different order, so the bits differ from FirOversampler's: a separate type, used by the
// machines built after it (Neo Clipper, Neo Limiter v2's soft clip), so no existing machine's renders move.
// Each output is the same sum in the same order whether it falls in an 8-lane Vector256 (x64), a 4-lane Vector128
// (arm64, or x64 for the block's remainder) or the scalar tail: the same bits on every platform.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace NeoBuzz.Dsp
{
    /// <summary>
    /// One halfband stage, 2× up and down, a block at a time: the history in a straight buffer (the last window, then the
    /// block), each branch a convolution over the block (<see cref="Convolve"/>).
    /// </summary>
    internal sealed class FastHalfband
    {
        readonly float[] taps;          // the branch's 2K taps by ascending time (oldest first)
        readonly float centre;
        readonly int k, width;
        readonly float[] up, even, odd;   // [width of history | block]
        readonly float[] sum;             // the branch's outputs for a block

        public FastHalfband(FirHalfbandDesign d, int maxBlock)
        {
            k = d.K;
            width = 2 * k;
            // By delay m (0 newest) the odd-distance taps 2j+1 fall on delays K−1−j and K+j; stored by ascending time.
            var byDelay = new float[width];
            for (int j = 0; j < k; j++)
            {
                byDelay[k - 1 - j] = d.Odd[j];
                byDelay[k + j] = d.Odd[j];
            }
            taps = new float[width];
            for (int q = 0; q < width; q++) taps[q] = byDelay[width - 1 - q];
            centre = d.Centre;
            up = new float[width + maxBlock];
            even = new float[width + maxBlock];
            odd = new float[width + maxBlock];
            sum = new float[maxBlock];
            RoundTripLatency = d.CentreIndex;
        }

        public int RoundTripLatency { get; }

        public void Reset()
        {
            Array.Clear(up);
            Array.Clear(even);
            Array.Clear(odd);
        }

        /// <summary>
        /// y[j] = Σ t[q]·h[j + 1 + q] over q = 0 … t.Length − 1 in that order, for j &lt; n: whole vectors of outputs (four at a
        /// time, then one), then one output at a time, each the same sum.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Convolve(float[] h, float[] t, float[] y, int n)
        {
            int taps = t.Length;
            if (n + taps > h.Length || n > y.Length) throw new ArgumentOutOfRangeException(nameof(n));
            ref float rh = ref MemoryMarshal.GetArrayDataReference(h);
            ref float rt = ref MemoryMarshal.GetArrayDataReference(t);
            ref float ry = ref MemoryMarshal.GetArrayDataReference(y);
            int j = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                for (; j + 32 <= n; j += 32)
                {
                    Vector256<float> a0 = default, a1 = default, a2 = default, a3 = default;
                    for (int q = 0; q < taps; q++)
                    {
                        var c = Vector256.Create(Unsafe.Add(ref rt, q));
                        nuint at = (nuint)(j + 1 + q);
                        a0 += c * Vector256.LoadUnsafe(ref rh, at);
                        a1 += c * Vector256.LoadUnsafe(ref rh, at + 8);
                        a2 += c * Vector256.LoadUnsafe(ref rh, at + 16);
                        a3 += c * Vector256.LoadUnsafe(ref rh, at + 24);
                    }
                    a0.StoreUnsafe(ref ry, (nuint)j);
                    a1.StoreUnsafe(ref ry, (nuint)(j + 8));
                    a2.StoreUnsafe(ref ry, (nuint)(j + 16));
                    a3.StoreUnsafe(ref ry, (nuint)(j + 24));
                }
                for (; j + 8 <= n; j += 8)
                {
                    Vector256<float> a = default;
                    for (int q = 0; q < taps; q++)
                        a += Vector256.Create(Unsafe.Add(ref rt, q)) * Vector256.LoadUnsafe(ref rh, (nuint)(j + 1 + q));
                    a.StoreUnsafe(ref ry, (nuint)j);
                }
            }
            for (; j + 16 <= n; j += 16)
            {
                Vector128<float> a0 = default, a1 = default, a2 = default, a3 = default;
                for (int q = 0; q < taps; q++)
                {
                    var c = Vector128.Create(Unsafe.Add(ref rt, q));
                    nuint at = (nuint)(j + 1 + q);
                    a0 += c * Vector128.LoadUnsafe(ref rh, at);
                    a1 += c * Vector128.LoadUnsafe(ref rh, at + 4);
                    a2 += c * Vector128.LoadUnsafe(ref rh, at + 8);
                    a3 += c * Vector128.LoadUnsafe(ref rh, at + 12);
                }
                a0.StoreUnsafe(ref ry, (nuint)j);
                a1.StoreUnsafe(ref ry, (nuint)(j + 4));
                a2.StoreUnsafe(ref ry, (nuint)(j + 8));
                a3.StoreUnsafe(ref ry, (nuint)(j + 12));
            }
            for (; j + 4 <= n; j += 4)
            {
                Vector128<float> a = default;
                for (int q = 0; q < taps; q++)
                    a += Vector128.Create(Unsafe.Add(ref rt, q)) * Vector128.LoadUnsafe(ref rh, (nuint)(j + 1 + q));
                a.StoreUnsafe(ref ry, (nuint)j);
            }
            for (; j < n; j++)
            {
                float a = 0;
                for (int q = 0; q < taps; q++) a += t[q] * h[j + 1 + q];
                y[j] = a;
            }
        }

        /// <summary><paramref name="n"/> base-rate samples in, 2n high-rate samples out.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Up(float[] src, float[] dst, int n)
        {
            Array.Copy(src, 0, up, width, n);
            Convolve(up, taps, sum, n);
            float c2 = 2f * centre;
            for (int j = 0; j < n; j++)
            {
                dst[2 * j] = 2f * sum[j];
                dst[2 * j + 1] = c2 * up[width + j - (k - 1)];
            }
            Array.Copy(up, n, up, 0, width);
        }

        /// <summary><paramref name="n"/> base-rate samples out of 2n high-rate ones (the earlier of each pair first).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Down(float[] src, float[] dst, int n)
        {
            for (int j = 0; j < n; j++)
            {
                even[width + j] = src[2 * j];
                odd[width + j] = src[2 * j + 1];
            }
            Convolve(even, taps, sum, n);
            for (int j = 0; j < n; j++)
                dst[j] = centre * odd[width + j - k] + sum[j];
            Array.Copy(even, n, even, 0, width);
            Array.Copy(odd, n, odd, 0, width);
        }
    }

    /// <summary>
    /// One channel: up a block to Factor × the rate, process it there, come back down — FirOversampler's designs, latency
    /// and padding (see the file's comment for what differs).
    /// </summary>
    internal sealed class FastOversampler
    {
        static readonly FirHalfbandDesign[] Designs =
        {
            new FirHalfbandDesign(24, 120),
            new FirHalfbandDesign(11, 125),
            new FirHalfbandDesign(8, 125),
        };

        readonly FastHalfband[] stages;
        readonly DelayLineF[] pads;
        readonly int[] padLength;
        readonly float[][] buf;
        readonly int maxBlock;

        public FastOversampler(int maxBlock = 256)
        {
            this.maxBlock = maxBlock;
            stages = new FastHalfband[Designs.Length];
            pads = new DelayLineF[Designs.Length];
            padLength = new int[Designs.Length];
            buf = new float[Designs.Length + 1][];
            for (int s = 0; s < Designs.Length; s++)
            {
                stages[s] = new FastHalfband(Designs[s], maxBlock << s);
                int unit = 1 << s;
                int rt = stages[s].RoundTripLatency;
                padLength[s] = (unit - rt % unit) % unit;
                pads[s] = new DelayLineF(Math.Max(1, padLength[s]) + 1);
            }
            for (int s = 0; s <= Designs.Length; s++) buf[s] = new float[maxBlock << s];
        }

        /// <summary>1, 2, 4 or 8.</summary>
        public int Factor { get; private set; } = 1;

        int Stages => Factor switch { 2 => 1, 4 => 2, 8 => 3, _ => 0 };

        public void SetFactor(int factor)
        {
            int f = factor >= 8 ? 8 : factor >= 4 ? 4 : factor >= 2 ? 2 : 1;
            if (f == Factor) return;
            Factor = f;
            Reset();
        }

        /// <summary>Base-rate latency of the up-process-down path at a factor (FirOversampler's).</summary>
        public static int LatencyFor(int factor) => FirOversampler.LatencyFor(factor);

        public int Latency => LatencyFor(Factor);

        public static int MaxLatency => LatencyFor(8);

        public void Reset()
        {
            foreach (var s in stages) s.Reset();
            foreach (var p in pads) p.Reset();
        }

        /// <summary>Upsamples <paramref name="n"/> base samples; returns the high-rate buffer (n × Factor samples) to process in place before <see cref="Down"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public float[] Up(float[] input, int offset, int n)
        {
            if (n > maxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Array.Copy(input, offset, buf[0], 0, n);
            int count = n;
            int used = Stages;
            for (int s = 0; s < used; s++)
            {
                var src = buf[s];
                int pad = padLength[s];
                if (pad > 0)
                {
                    var pl = pads[s];
                    for (int i = 0; i < count; i++)
                    {
                        pl.Write(src[i]);
                        src[i] = pl.Read(pad);
                    }
                }
                stages[s].Up(src, buf[s + 1], count);
                count *= 2;
            }
            return buf[used];
        }

        /// <summary>Downsamples the high-rate buffer <see cref="Up"/> returned into <paramref name="output"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Down(float[] output, int offset, int n)
        {
            int used = Stages;
            int count = n << used;
            for (int s = used - 1; s >= 0; s--)
            {
                count /= 2;
                stages[s].Down(buf[s + 1], buf[s], count);
            }
            Array.Copy(buf[0], 0, output, offset, n);
        }
    }
}
