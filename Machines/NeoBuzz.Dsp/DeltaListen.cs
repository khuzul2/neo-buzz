#nullable disable
// Neo machines shared DSP (docs/specs/mastering-tools.md §2, the Delta convention, decided in M1): what the
// mixing and mastering machines from M2 on (Neo Clipper, Neo Limiter v2, Neo EQ v2, Neo Transient, …) do
// with their `Delta` switch. On, the output becomes wet − dry, the dry delayed by the machine's latency so
// the two line up: the user hears only what the machine changes (Ozone's Delta buttons). A machine with a
// Mix control applies Delta to its full wet signal, before the mix, so Delta always means "what the
// processing adds or takes away".
//  - The switch glides over 10 ms (the engine's latency crossfade, §4.1), linear, landing exactly: off is
//    exactly 0, so a machine with Delta off passes its wet signal bit for bit.
//  - The dry is written into the ring on every block, Delta on or off, so a switch to On has the delayed
//    dry ready at once.
//  - Process works sample by sample (each dry sample written before the delayed one is read), so any block
//    size and a latency of 0 work alike; the latency moves only between blocks.
// Allocation-free after construction; basic float operations only, the same bits on every platform.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal sealed class DeltaListen
    {
        /// <summary>The switch's glide, seconds (the engine's latency crossfade).</summary>
        public const double SwitchSeconds = 0.01;

        readonly float[] l, r;
        readonly int mask;
        int pos, latency;
        LinearRamp mix;

        /// <param name="maxLatency">The most latency the machine reports, samples.</param>
        /// <param name="sampleRate">For the switch's glide.</param>
        public DeltaListen(int maxLatency, double sampleRate)
        {
            int size = 1;
            while (size < maxLatency + 1) size <<= 1;
            l = new float[size];
            r = new float[size];
            mask = size - 1;
            MaxLatency = maxLatency;
            mix.Setup(SwitchSeconds, sampleRate);
            mix.Reset(0);
        }

        public int MaxLatency { get; }

        /// <summary>A new rate: the glide's length follows it (the switch keeps where it is).</summary>
        public void SetSampleRate(double sampleRate)
        {
            float v = mix.Value, t = mix.Target;
            mix.Setup(SwitchSeconds, sampleRate);
            mix.Reset(v);
            mix.SetTarget(t);
        }

        /// <summary>The machine's latency now, samples (0 … MaxLatency): how far the dry is delayed.</summary>
        public int Latency
        {
            get => latency;
            set => latency = Math.Clamp(value, 0, MaxLatency);
        }

        /// <summary>The switch: true glides to wet − dry, false back to wet.</summary>
        public bool On
        {
            get => mix.Target == 1;
            set => mix.SetTarget(value ? 1 : 0);
        }

        /// <summary>Whether the output differs from the wet signal (on, or gliding).</summary>
        public bool Active => !mix.At(0);

        /// <summary>On at once, no glide (a song loaded with Delta on).</summary>
        public void Set(bool on) => mix.Reset(on ? 1 : 0);

        public void Reset()
        {
            Array.Clear(l, 0, l.Length);
            Array.Clear(r, 0, r.Length);
            pos = 0;
        }

        /// <summary>
        /// <paramref name="n"/> samples: the machine's input (<paramref name="dryL"/>, <paramref name="dryR"/>) goes into the
        /// ring, and its output (<paramref name="wetL"/>, <paramref name="wetR"/>, in place) becomes wet − dry·m, m gliding to
        /// the switch. Right null: mono. Off and settled, the wet signal is untouched.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] dryL, float[] dryR, float[] wetL, float[] wetR, int n)
        {
            if (!Active)
            {
                for (int i = 0; i < n; i++)
                {
                    pos = (pos + 1) & mask;
                    l[pos] = dryL[i];
                    r[pos] = dryR != null ? dryR[i] : 0;
                }
                return;
            }
            for (int i = 0; i < n; i++)
            {
                pos = (pos + 1) & mask;
                l[pos] = dryL[i];
                if (dryR != null) r[pos] = dryR[i];
                int at = (pos - latency) & mask;
                float m = mix.Next();
                wetL[i] -= m * l[at];
                if (wetR != null) wetR[i] -= m * r[at];
            }
        }
    }
}
