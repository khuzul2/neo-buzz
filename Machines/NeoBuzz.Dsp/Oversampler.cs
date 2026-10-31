#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): linear-phase oversampling, 1×/2×/4×/8×,
// for nonlinear effects. Integer latency at every factor, so a dry signal can be aligned with
// the wet one; MaxLatency is the latency to report when the factor can change.

using System;

namespace NeoBuzz.Dsp
{
    /// <summary>One channel: up a block to Factor × the rate, process it there, come back down.</summary>
    internal sealed class FirOversampler
    {
        // Stage s works between rate 2^(s−1) and 2^s. Designs are relative to the high rate of
        // their stage: passband to ~20 kHz at 48 kHz, stopband where its fold would land below it.
        static readonly FirHalfbandDesign[] Designs =
        {
            new FirHalfbandDesign(24, 120),
            new FirHalfbandDesign(11, 125),
            new FirHalfbandDesign(8, 125),
        };

        readonly FirHalfband[] stages;
        readonly DelayLineF[] pads;
        readonly int[] padLength;
        readonly float[][] buf;    // buf[s]: signal at rate 2^s
        readonly int maxBlock;

        public FirOversampler(int maxBlock = 256)
        {
            this.maxBlock = maxBlock;
            stages = new FirHalfband[Designs.Length];
            pads = new DelayLineF[Designs.Length];
            padLength = new int[Designs.Length];
            buf = new float[Designs.Length + 1][];
            for (int s = 0; s < Designs.Length; s++)
            {
                stages[s] = new FirHalfband(Designs[s]);
                // Round trip of stage s+1 is (2K−1) samples at rate 2^s; pad to a whole base sample.
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

        /// <summary>Base-rate latency of the up-process-down path at a factor.</summary>
        public static int LatencyFor(int factor)
        {
            int stagesUsed = factor >= 8 ? 3 : factor >= 4 ? 2 : factor >= 2 ? 1 : 0;
            int total = 0;
            for (int s = 0; s < stagesUsed; s++)
            {
                int unit = 1 << s;
                int rt = 2 * Designs[s].K - 1;
                int pad = (unit - rt % unit) % unit;
                total += (rt + pad) / unit;
            }
            return total;
        }

        public int Latency => LatencyFor(Factor);

        public static int MaxLatency => LatencyFor(8);

        public void Reset()
        {
            foreach (var s in stages) s.Reset();
            foreach (var p in pads) p.Reset();
        }

        /// <summary>
        /// Upsamples <paramref name="n"/> base samples; returns the high-rate buffer (n × Factor
        /// samples), which the caller processes in place before <see cref="Down"/>.
        /// </summary>
        public float[] Up(float[] input, int offset, int n)
        {
            if (n > maxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Array.Copy(input, offset, buf[0], 0, n);
            int count = n;
            int used = Stages;
            for (int s = 0; s < used; s++)
            {
                var src = buf[s];
                var dst = buf[s + 1];
                var st = stages[s];
                int pad = padLength[s];
                var pl = pads[s];
                for (int i = 0; i < count; i++)
                {
                    float x = src[i];
                    if (pad > 0)
                    {
                        pl.Write(x);
                        x = pl.Read(pad);
                    }
                    st.Up(x, out dst[2 * i], out dst[2 * i + 1]);
                }
                count *= 2;
            }
            return buf[used];
        }

        /// <summary>Downsamples the high-rate buffer <see cref="Up"/> returned into <paramref name="output"/>.</summary>
        public void Down(float[] output, int offset, int n)
        {
            int used = Stages;
            int count = n << used;
            for (int s = used - 1; s >= 0; s--)
            {
                var src = buf[s + 1];
                var dst = buf[s];
                var st = stages[s];
                count /= 2;
                for (int i = 0; i < count; i++) dst[i] = st.Down(src[2 * i], src[2 * i + 1]);
            }
            Array.Copy(buf[0], 0, output, offset, n);
        }
    }

    /// <summary>Plain integer delay line (power-of-two ring).</summary>
    internal sealed class DelayLineF
    {
        readonly float[] data;
        readonly int mask;
        int pos;

        public DelayLineF(int maxDelay)
        {
            int size = 1;
            while (size < maxDelay + 1) size <<= 1;
            data = new float[size];
            mask = size - 1;
        }

        public int Capacity => mask;

        public void Write(float x)
        {
            pos = (pos + 1) & mask;
            data[pos] = x;
        }

        /// <summary>The sample written <paramref name="delay"/> writes ago (0 = the last one).</summary>
        public float Read(int delay) => data[(pos - delay) & mask];

        public void Reset()
        {
            Array.Clear(data, 0, data.Length);
            pos = 0;
        }
    }
}
