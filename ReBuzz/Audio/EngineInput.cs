#nullable disable
// Fork (docs/specs/neo-audio.md "Inputs and arm"): the audio device's input, owned by the engine. Each driver pushes what the device
// captured, interleaved, into a fixed-size single-producer single-consumer ring (a full-duplex driver from the callback that is about
// to render, WASAPI from its capture thread); the engine takes the frames of each block it renders out of the ring into a per-block
// frame of planar channels (BeginBlock) that audio machines read. Lock-free, no allocation after Configure. An underrun (the device
// hasn't delivered what a block needs) is silence and a counted dropout; an overrun (the ring is full) drops the newest frames.
// No dependency on the rest of ReBuzz, so ReBuzz.SessionTests links this file.

using System;
using System.Threading;

namespace ReBuzz.Audio
{
    internal sealed class EngineInput
    {
        /// <summary>The most input channels (a big interface has 32 or 64).</summary>
        public const int MaxChannels = 64;
        /// <summary>The longest block taken at once (the engine's blocks are at most 256 frames; offline renders may ask more).</summary>
        public const int MaxBlock = 4096;
        /// <summary>The ring's length, in frames (a power of two): about 0.7 s at 48 kHz.</summary>
        public const int RingFrames = 1 << 15;
        /// <summary>The most the ring may hold ahead of the engine before the oldest frames are skipped (the engine ran behind), in frames.</summary>
        const int MaxBacklog = RingFrames / 2;
        /// <summary>What a skip leaves in the ring, in frames.</summary>
        const int KeepAfterSkip = RingFrames / 8;

        float[] ring = Array.Empty<float>();
        float[][] block = Array.Empty<float[]>();
        float[] peaks = Array.Empty<float>();
        string[] names = Array.Empty<string>();
        readonly float[] silence = new float[MaxBlock];
        long write;                 // frames pushed
        long read;                  // frames taken
        int channels;
        int sampleRate;
        int latencyFrames;
        int blockFrames;
        int started;                // the device has delivered something since Configure
        long underruns, overruns, pushed;

        /// <summary>Input channels the open device has (0: no input).</summary>
        public int Channels => Volatile.Read(ref channels);

        public bool IsOpen => Channels > 0;

        public int SampleRate => Volatile.Read(ref sampleRate);

        /// <summary>The device's input latency (frames from a sound reaching the converter to the engine seeing it), as the driver reports it.</summary>
        public int LatencyFrames => Volatile.Read(ref latencyFrames);

        /// <summary>The names of the input channels (the driver's when it has them, else "In 1", "In 2"...).</summary>
        public string[] ChannelNames => Volatile.Read(ref names);

        /// <summary>Frames of silence given for want of input (the device late), since the device opened.</summary>
        public long UnderrunFrames => Interlocked.Read(ref underruns);

        /// <summary>Frames dropped because the ring was full, and frames skipped because the engine ran behind.</summary>
        public long OverrunFrames => Interlocked.Read(ref overruns);

        public long PushedFrames => Interlocked.Read(ref pushed);

        /// <summary>Frames of the block <see cref="BeginBlock"/> took.</summary>
        public int BlockFrames => blockFrames;

        /// <summary>A device opened (or none: <paramref name="inputChannels"/> 0): the ring and the block's channels are made. Not while the device runs.</summary>
        public void Configure(int inputChannels, string[] channelNames, int rate, int latency)
        {
            int n = Math.Clamp(inputChannels, 0, MaxChannels);
            var shown = new string[n];
            for (int i = 0; i < n; i++)
                shown[i] = channelNames != null && i < channelNames.Length && !string.IsNullOrWhiteSpace(channelNames[i]) ? channelNames[i].Trim() : "In " + (i + 1);
            Volatile.Write(ref channels, 0);                       // pushes see no device while it is rebuilt
            ring = n == 0 ? Array.Empty<float>() : new float[RingFrames * n];
            block = new float[n][];
            for (int i = 0; i < n; i++) block[i] = new float[MaxBlock];
            peaks = new float[n];
            write = 0;
            read = 0;
            blockFrames = 0;
            Interlocked.Exchange(ref underruns, 0);
            Interlocked.Exchange(ref overruns, 0);
            Interlocked.Exchange(ref pushed, 0);
            Volatile.Write(ref started, 0);
            Volatile.Write(ref names, shown);
            Volatile.Write(ref sampleRate, Math.Max(1, rate));
            Volatile.Write(ref latencyFrames, Math.Max(0, latency));
            Volatile.Write(ref channels, n);
        }

        /// <summary>
        /// The engine is about to start rendering: what the device captured so far (a capture thread starts before the output does) is dropped, so
        /// the input starts near the render. Not while it renders.
        /// </summary>
        public void Flush() => Volatile.Write(ref read, Volatile.Read(ref write));

        /// <summary>The device closed.</summary>
        public void Close() => Configure(0, null, SampleRate, 0);

        /// <summary>
        /// The producer (the driver's thread): <paramref name="frames"/> interleaved frames of <paramref name="sourceChannels"/> channels from
        /// <paramref name="samples"/> (floats of ±1). Channels the device has beyond the source's are silent.
        /// </summary>
        public void Push(float[] samples, int frames, int sourceChannels)
        {
            int n = Volatile.Read(ref channels);
            if (n == 0 || frames <= 0 || sourceChannels <= 0 || samples == null) return;
            frames = Math.Min(frames, samples.Length / sourceChannels);
            long w = Volatile.Read(ref write);
            long r = Volatile.Read(ref read);
            long free = RingFrames - (w - r);
            int take = (int)Math.Min(frames, Math.Max(0, free));
            if (take < frames) Interlocked.Add(ref overruns, frames - take);
            var buffer = ring;
            int shared = Math.Min(n, sourceChannels);
            for (int i = 0; i < take; i++)
            {
                int at = (int)((w + i) & (RingFrames - 1)) * n;
                int from = i * sourceChannels;
                for (int c = 0; c < shared; c++) buffer[at + c] = samples[from + c];
                for (int c = shared; c < n; c++) buffer[at + c] = 0f;
            }
            Volatile.Write(ref write, w + take);
            Interlocked.Add(ref pushed, take);
            Volatile.Write(ref started, 1);
        }

        /// <summary>
        /// The consumer (the engine's render, once per block): takes <paramref name="frames"/> frames out of the ring into the block's
        /// channels. What isn't there is silence, counted as an underrun once the device has delivered anything.
        /// </summary>
        public void BeginBlock(int frames)
        {
            frames = Math.Clamp(frames, 0, MaxBlock);
            blockFrames = frames;
            int n = Volatile.Read(ref channels);
            if (n == 0) return;
            long w = Volatile.Read(ref write);
            long r = read;
            long have = w - r;
            if (have > MaxBacklog)
            {
                // The engine fell behind the device (it rendered nothing for a while): the oldest frames go, so the input stays near the render.
                long skip = have - KeepAfterSkip;
                r += skip;
                have -= skip;
                Interlocked.Add(ref overruns, skip);
            }
            int take = (int)Math.Min(frames, have);
            var buffer = ring;
            for (int c = 0; c < n; c++)
            {
                var channel = block[c];
                float peak = 0;
                for (int i = 0; i < take; i++)
                {
                    float v = buffer[(int)((r + i) & (RingFrames - 1)) * n + c];
                    channel[i] = v;
                    float a = v < 0 ? -v : v;
                    if (a > peak) peak = a;
                }
                if (take < frames) Array.Clear(channel, take, frames - take);
                if (peak > 0) AtomicMax(ref peaks[c], peak);
            }
            Volatile.Write(ref read, r + take);
            if (take < frames && Volatile.Read(ref started) != 0)
                Interlocked.Add(ref underruns, frames - take);
        }

        /// <summary>Channel <paramref name="c"/> of the current block (±1); silence for a channel the device doesn't have.</summary>
        public float[] Channel(int c) => c >= 0 && c < block.Length ? block[c] : silence;

        /// <summary>The loudest sample of input channel <paramref name="c"/> since the last call (±1 full scale), and forgets it.</summary>
        public float TakePeak(int c) => c >= 0 && c < peaks.Length ? Interlocked.Exchange(ref peaks[c], 0f) : 0f;

        static void AtomicMax(ref float target, float value)
        {
            float seen = Volatile.Read(ref target);
            while (value > seen)
            {
                float was = Interlocked.CompareExchange(ref target, value, seen);
                if (was == seen) return;
                seen = was;
            }
        }
    }
}
