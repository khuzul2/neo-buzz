// Fork (docs/specs/mastering-tools.md §4.1): a delay of the engine's delay compensation that changes
// without a gap or a click. Each compensated connection has one (MachineConnectionCore), and each
// effect with latency one for its bypass (MachineCore): the audio goes in, a tap a set delay back
// comes out.
//  - A ring of Sample (a power of two above the delay plus the engine's largest block, 256), written
//    while the delay is above 0 or a change is under way; at delay 0 with nothing pending its owner
//    passes the audio directly, as before (Live is false).
//  - A changed delay (Request) crossfades from the old tap to the new over the fade's length (10 ms
//    for the compensation, 5 ms for a bypass): a smooth curve (3x² − 2x³: no libm, so the same bits
//    on every platform), the two taps of the same signal briefly mixed, never a gap. The new tap needs
//    its history: a ring that comes to life (from delay 0) starts empty and the crossfade waits until
//    it holds the new delay's worth.
//  - The change is instant and exact when nothing has passed through the ring's owner yet (a new
//    connection, or a graph built before any audio), and when everything in flight is silent (no
//    sample other than 0 written for longer than both delays).
//  - Capacity: grown on the UI thread under the audio lock (EnsureCapacity: what is in flight is kept);
//    a delay beyond it is clamped to it meanwhile (Wanted asks for more).
// Allocation-free on the audio thread. Callers hold ReBuzzCore.AudioLock for every call.

using Buzz.MachineInterface;
using System;
using System.Runtime.CompilerServices;

namespace ReBuzz.Core
{
    internal sealed class LatencyRing
    {
        /// <summary>The engine's largest block (samples): a ring holds its delay plus this.</summary>
        public const int Block = 256;

        Sample[] ring = Array.Empty<Sample>();
        int mask = -1;
        long written;             // samples written since the ring was made (positions are written & mask)
        int delay;                // the tap now
        int pending = -1;         // a delay asked for and not yet fading (−1: none)
        int fadeLength = 1;       // the fade asked for with it
        bool fading;
        int fadeFrom, fadeTo, fadeAt, fadeLen;
        int warm;                 // samples written since the ring came to life (capped)
        int quiet;                // samples since a sample other than 0 was written (capped)
        long blocks;              // blocks through the owner since it was made

        /// <summary>The ring is written (delay above 0, or a change under way); false: the owner passes audio directly.</summary>
        public bool Live { get; private set; }

        /// <summary>The delay now (samples): the tap, or the one a crossfade is going to.</summary>
        public int Delay => pending >= 0 ? pending : fading ? fadeTo : delay;

        /// <summary>The tap that plays now, once any crossfade is done (tests).</summary>
        public int Settled => fading || pending >= 0 ? -1 : delay;

        /// <summary>The ring's capacity: the largest delay it can hold.</summary>
        public int Capacity => ring.Length > Block ? ring.Length - Block : 0;

        /// <summary>A delay asked for that the ring can't hold yet (the UI thread grows it); 0 when none.</summary>
        public int Wanted { get; private set; }

        /// <summary>The ring (its owner writes the block it is processing at <see cref="Position"/> … + n, wrapping with <see cref="Mask"/>).</summary>
        public Sample[] Ring => ring;
        public int Mask => mask;
        public int Position => (int)(written & mask);

        /// <summary>
        /// Room for a delay of <paramref name="samples"/> (UI thread, audio lock held): grows the ring to a power of two above it
        /// plus a block, keeping what is in flight. Returns whether it grew.
        /// </summary>
        public bool EnsureCapacity(int samples)
        {
            if (samples <= Capacity) return false;
            int size = 1024;
            while (size < samples + Block) size <<= 1;
            var grown = new Sample[size];
            int keep = ring.Length;
            for (int k = 0; k < keep; k++)
            {
                long at = written - keep + k;
                if (at < 0) continue;
                grown[at & (size - 1)] = ring[at & mask];
            }
            // The block being written (ahead of written) is cleared by its owner before use.
            ring = grown;
            mask = size - 1;
            if (Wanted <= Capacity) Wanted = 0;
            return true;
        }

        /// <summary>
        /// Asks for a delay of <paramref name="samples"/>, reached by a crossfade of <paramref name="fade"/> samples (or at once when
        /// nothing is in flight). Beyond the capacity it is clamped to it, and <see cref="Wanted"/> says what was asked.
        /// </summary>
        public void Request(int samples, int fade)
        {
            samples = Math.Max(0, samples);
            if (samples > Capacity)
            {
                Wanted = samples;
                samples = Capacity;
            }
            else if (Wanted > 0 && samples <= Capacity) Wanted = 0;
            if (samples == Delay) return;
            if (blocks == 0)
            {
                // Nothing has passed yet: the ring is empty, so any tap is exact at once.
                Settle(samples);
                return;
            }
            pending = samples;
            fadeLength = Math.Max(1, fade);
            if (!Live) Wake();
        }

        void Wake()
        {
            Array.Clear(ring);
            Live = true;
            warm = 0;
            quiet = 0;
        }

        void Settle(int samples)
        {
            delay = samples;
            pending = -1;
            fading = false;
            if (samples > 0 && !Live) Wake();
            Live = samples > 0;
        }

        /// <summary>Clears the slots of the block about to be written (<paramref name="n"/> samples), when live.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear(int n)
        {
            if (!Live) return;
            int w = (int)(written & mask);
            for (int i = 0; i < n; i++)
            {
                ring[w] = default;
                w = (w + 1) & mask;
            }
        }

        /// <summary>Writes a block (live rings; the bypass's dry input) at the write position, replacing what was there.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Write(Sample[] samples, int n)
        {
            if (!Live) return;
            int w = (int)(written & mask);
            for (int i = 0; i < n; i++)
            {
                ring[w] = samples[i];
                w = (w + 1) & mask;
            }
        }

        /// <summary>
        /// The block just written (<paramref name="n"/> samples, by <see cref="Write"/> or the owner's own adds) out through the tap
        /// into <paramref name="output"/>, crossfading while a change is under way; the ring then moves on. Only for live rings.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Read(Sample[] output, int n)
        {
            blocks++;
            int capacity = ring.Length;
            warm = Math.Min(capacity, warm + n);
            // Silence in flight: was anything but 0 written in this block?
            int w0 = (int)(written & mask);
            bool sound = false;
            for (int i = 0, w = w0; i < n; i++, w = (w + 1) & mask)
                if (ring[w].L != 0 || ring[w].R != 0)
                {
                    sound = true;
                    break;
                }
            quiet = sound ? 0 : Math.Min(capacity, quiet + n);

            if (pending >= 0 && !fading)
            {
                int to = pending;
                if (quiet >= Math.Max(delay, to) + n)
                {
                    // All in flight is silent: the new tap at once.
                    delay = to;
                    pending = -1;
                }
                else if (warm >= to + n)
                {
                    fading = true;
                    fadeFrom = delay;
                    fadeTo = to;
                    fadeAt = 0;
                    fadeLen = fadeLength;
                    pending = -1;
                }
            }

            if (!fading)
            {
                for (int i = 0; i < n; i++)
                    output[i] = ring[(w0 + i - delay) & mask];
            }
            else
            {
                float step = 1f / fadeLen;
                for (int i = 0; i < n; i++)
                {
                    var a = ring[(w0 + i - fadeFrom) & mask];
                    var b = ring[(w0 + i - fadeTo) & mask];
                    if (fadeAt < fadeLen)
                    {
                        float x = (fadeAt + 1) * step;
                        float g = x * x * (3f - 2f * x);
                        output[i].L = a.L + (b.L - a.L) * g;
                        output[i].R = a.R + (b.R - a.R) * g;
                        fadeAt++;
                    }
                    else
                    {
                        output[i] = b;
                    }
                }
                if (fadeAt >= fadeLen)
                {
                    fading = false;
                    delay = fadeTo;
                }
            }
            written += n;
            if (!fading && pending < 0 && delay == 0) Live = false;     // back to the direct path from the next block
        }

        /// <summary>A block passed the owner directly (not live): counts for whether anything has passed yet.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Passed() => blocks++;
    }
}
