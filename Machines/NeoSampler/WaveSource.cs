#nullable disable
// Neo Sampler (docs/specs/machines.md §5): the song's wavetable into the engine's bank, so songs
// carry their samples (ReBuzz saves the waves; the zones name their slots).
//
// ReBuzz changes waves on the thread that owns the song (its UI thread, which also runs the agent
// server's tools), loads take the audio lock, and a wave's memory is freed when it is replaced. So a
// wave is read only where that can't happen meanwhile: in Work (under the audio lock) or on the
// song's own thread (Busy, which renders ask first). A background timer (every 200 ms, and at once
// when ReBuzz says a wave changed) compares what the zones' slots hold with what the bank has, by
// each layer's buffer, length, format, rate, root and loop, and makes room for a copy; Work copies
// up to 16 384 frames a block into it (no allocation); the timer then builds the layer (levels,
// loop copy) and swaps the slot into the bank. A voice keeps the layer it started with, so a wave
// replaced or removed while it plays never pulls the samples from under it. The machine is Busy
// while copies are waiting, and its Busy does them all at once, so a render starts with every wave.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BuzzGUI.Interfaces;

namespace NeoBuzz.Sampler
{
    internal sealed class WaveSource : IDisposable
    {
        public const int FramesPerBlock = 16384;

        /// <summary>What a slot held when it was read: its wave and each layer's state.</summary>
        sealed class Signature
        {
            public IWave Wave;
            public string Name;
            public WaveFlags Flags;
            public float Volume;
            public int Generation;
            public IWaveLayer[] Layers;
            public IntPtr[] Raw;
            public int[] Frames, Channels, Rate, Root, LoopStart, LoopEnd;
            public WaveFormat[] Format;

            public static Signature Of(IWave w, int generation)
            {
                if (w == null) return null;
                var layers = w.Layers?.Where(l => l != null && l.SampleCount > 0).ToArray() ?? Array.Empty<IWaveLayer>();
                if (layers.Length == 0) return null;
                int n = layers.Length;
                var s = new Signature
                {
                    Wave = w, Name = w.Name ?? "", Flags = w.Flags, Volume = w.Volume, Generation = generation, Layers = layers,
                    Raw = new IntPtr[n], Frames = new int[n], Channels = new int[n], Rate = new int[n], Root = new int[n],
                    LoopStart = new int[n], LoopEnd = new int[n], Format = new WaveFormat[n],
                };
                for (int i = 0; i < n; i++)
                {
                    var l = layers[i];
                    s.Raw[i] = l.RawSamples;
                    s.Frames[i] = l.SampleCount;
                    s.Channels[i] = Math.Clamp(l.ChannelCount, 1, 2);
                    s.Rate[i] = l.SampleRate;
                    s.Root[i] = l.RootNote;
                    s.LoopStart[i] = l.LoopStart;
                    s.LoopEnd[i] = l.LoopEnd;
                    s.Format[i] = l.Format;
                }
                return s;
            }

            public bool Same(Signature o)
            {
                if (o == null || o.Wave != Wave || o.Name != Name || o.Flags != Flags || o.Volume != Volume || o.Generation != Generation || o.Layers.Length != Layers.Length) return false;
                for (int i = 0; i < Layers.Length; i++)
                    if (o.Layers[i] != Layers[i] || o.Raw[i] != Raw[i] || o.Frames[i] != Frames[i] || o.Channels[i] != Channels[i] || o.Rate[i] != Rate[i] ||
                        o.Root[i] != Root[i] || o.LoopStart[i] != LoopStart[i] || o.LoopEnd[i] != LoopEnd[i] || o.Format[i] != Format[i]) return false;
                return true;
            }

            /// <summary>Whether a layer still has the buffer it was read with (cheap property reads: no allocation).</summary>
            public bool Holds(int i)
            {
                var l = Layers[i];
                return l.RawSamples == Raw[i] && l.SampleCount == Frames[i] && l.ChannelCount == Channels[i];
            }
        }

        /// <summary>A slot being copied in: room for its layers' samples, filled block by block.</summary>
        sealed class Load
        {
            public Signature Sig;
            public float[][] Left, Right;
            public int Layer, Frame;
            public volatile bool Copied, Failed;
        }

        readonly Func<IWavetable> wavetable;
        readonly SampleBank bank;
        readonly Func<int, int> zoneWave;
        readonly object gate = new object(), scan = new object();
        readonly Signature[] published = new Signature[SampleBank.SongSlots];
        readonly Load[] pending = new Load[SampleBank.SongSlots];
        readonly int[] generation = new int[SampleBank.SongSlots];
        readonly bool[] wanted = new bool[SampleBank.SongSlots];
        int pumping, pendingCount;
        IWavetable subscribed;
        Timer timer;
        volatile bool disposed;

        /// <param name="wavetable">The song's wavetable (null while there is none).</param>
        /// <param name="zoneWave">Zone z's Wave (raw).</param>
        public WaveSource(Func<IWavetable> wavetable, SampleBank bank, Func<int, int> zoneWave)
        {
            this.wavetable = wavetable;
            this.bank = bank;
            this.zoneWave = zoneWave;
            timer = new Timer(_ => Tick(), null, 50, 200);
        }

        /// <summary>Copies waiting for Work (or for <see cref="Synchronise"/>).</summary>
        public bool Pending => Volatile.Read(ref pendingCount) > 0;

        /// <summary>The name of a song slot's wave, or null (the song's own thread).</summary>
        public string NameOf(int slot)
        {
            try
            {
                var waves = wavetable()?.Waves;
                return waves != null && slot >= 1 && slot <= waves.Count ? waves[slot - 1]?.Name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        void Tick()
        {
            if (disposed || !Monitor.TryEnter(scan)) return;
            try
            {
                Scan();
                BuildCopied();
            }
            catch (Exception)
            {
                // A wavetable changing under the read (a list being edited): the next tick reads it again.
            }
            finally
            {
                Monitor.Exit(scan);
            }
        }

        void OnWaveChanged(int index)
        {
            if (index >= 0 && index < SampleBank.SongSlots) Interlocked.Increment(ref generation[index]);
            try { timer?.Change(0, 200); } catch (ObjectDisposedException) { }
        }

        /// <summary>Compares the zones' slots with the bank and makes room for what changed.</summary>
        void Scan()
        {
            var wt = wavetable();
            if (wt != subscribed)
            {
                if (subscribed != null) subscribed.WaveChanged -= OnWaveChanged;
                subscribed = wt;
                if (wt != null) wt.WaveChanged += OnWaveChanged;
            }
            Array.Clear(wanted, 0, wanted.Length);
            for (int z = 0; z < SamplerEngine.ZoneCount; z++)
            {
                int w = zoneWave(z);
                if (w >= 1 && w <= SampleBank.SongSlots) wanted[w - 1] = true;
            }
            var waves = wt?.Waves;
            for (int i = 0; i < SampleBank.SongSlots; i++)
            {
                if (!wanted[i])
                {
                    // A slot no zone plays: let it go (a zone that comes back to it copies it again).
                    lock (gate)
                    {
                        if (published[i] == null && pending[i] == null) continue;
                        published[i] = null;
                        if (pending[i] != null) { pending[i] = null; Interlocked.Decrement(ref pendingCount); }
                        bank.Set(i + 1, null);
                    }
                    continue;
                }
                var w = waves != null && i < waves.Count ? waves[i] : null;
                var sig = Signature.Of(w, Volatile.Read(ref generation[i]));
                lock (gate)
                {
                    if (sig == null)
                    {
                        if (pending[i] != null) { pending[i] = null; Interlocked.Decrement(ref pendingCount); }
                        if (published[i] != null) { published[i] = null; bank.Set(i + 1, null); }
                        continue;
                    }
                    if (pending[i] != null && pending[i].Sig.Same(sig) && !pending[i].Failed) continue;
                    if (pending[i] == null && sig.Same(published[i])) continue;
                }
                var load = new Load { Sig = sig, Left = new float[sig.Layers.Length][], Right = new float[sig.Layers.Length][] };
                for (int l = 0; l < sig.Layers.Length; l++)
                {
                    load.Left[l] = new float[sig.Frames[l]];
                    load.Right[l] = sig.Channels[l] > 1 ? new float[sig.Frames[l]] : null;
                }
                lock (gate)
                {
                    if (pending[i] == null) Interlocked.Increment(ref pendingCount);
                    Volatile.Write(ref pending[i], load);
                }
            }
        }

        /// <summary>
        /// Copies waiting samples, up to <paramref name="budget"/> frames: in Work (under the audio
        /// lock), or on the song's thread. Allocates nothing.
        /// </summary>
        public void Pump(int budget)
        {
            if (Volatile.Read(ref pendingCount) == 0) return;
            if (Interlocked.CompareExchange(ref pumping, 1, 0) != 0) return;
            try
            {
                for (int i = 0; i < SampleBank.SongSlots && budget > 0; i++)
                {
                    var load = Volatile.Read(ref pending[i]);
                    if (load == null || load.Copied || load.Failed) continue;
                    var sig = load.Sig;
                    while (budget > 0 && load.Layer < sig.Layers.Length)
                    {
                        int l = load.Layer;
                        if (!sig.Holds(l))
                        {
                            load.Failed = true;   // replaced meanwhile: the timer reads it again
                            break;
                        }
                        int n = Math.Min(budget, sig.Frames[l] - load.Frame);
                        var layer = sig.Layers[l];
                        layer.GetDataAsFloat(load.Left[l], load.Frame, 1, 0, load.Frame, n);
                        if (load.Right[l] != null) layer.GetDataAsFloat(load.Right[l], load.Frame, 1, 1, load.Frame, n);
                        load.Frame += n;
                        budget -= n;
                        if (load.Frame >= sig.Frames[l])
                        {
                            load.Layer++;
                            load.Frame = 0;
                        }
                    }
                    if (load.Layer >= sig.Layers.Length) load.Copied = true;
                }
            }
            catch (Exception)
            {
                // A wave gone from under the copy: read again on the next tick.
            }
            finally
            {
                Volatile.Write(ref pumping, 0);
            }
        }

        /// <summary>Builds the layers of copied slots and swaps them into the bank.</summary>
        void BuildCopied()
        {
            for (int i = 0; i < SampleBank.SongSlots; i++)
            {
                var load = Volatile.Read(ref pending[i]);
                if (load == null || !load.Copied) continue;
                var sig = load.Sig;
                var layers = new SampleLayer[sig.Layers.Length];
                bool loop = (sig.Flags & WaveFlags.Loop) != 0, bidi = (sig.Flags & WaveFlags.BidirectionalLoop) != 0;
                for (int l = 0; l < layers.Length; l++)
                {
                    int ls = sig.LoopStart[l], le = sig.LoopEnd[l];
                    bool looped = loop && le > ls + 1 && ls >= 0 && le <= sig.Frames[l];
                    layers[l] = SampleLayer.Build(sig.Name, load.Left[l], load.Right[l], sig.Rate[l] > 0 ? sig.Rate[l] : 44100, Midi(sig.Root[l]),
                        ls, le, looped ? (bidi ? WaveLoop.PingPong : WaveLoop.Forward) : WaveLoop.None, sig.Volume);
                }
                var slot = new SampleSlot(sig.Name, layers);
                lock (gate)
                {
                    if (pending[i] != load) continue;
                    bank.Set(i + 1, slot);
                    published[i] = sig;
                    pending[i] = null;
                    Interlocked.Decrement(ref pendingCount);
                }
            }
        }

        /// <summary>A Buzz note byte (octave &lt;&lt; 4 | semitone + 1) as MIDI (Buzz C-5 = 60, as the agent server reads roots); 60 when it isn't one.</summary>
        internal static int Midi(int buzz)
        {
            int octave = buzz >> 4, semi = (buzz & 15) - 1;
            if (semi < 0 || semi > 11 || buzz <= 0 || buzz == 255) return 60;
            return Math.Clamp(octave * 12 + semi, 0, 127);
        }

        /// <summary>
        /// The song's own thread (a render asks first): reads the zones' slots, copies and builds
        /// everything waiting, now. Whether anything is still waiting afterwards.
        /// </summary>
        public bool Synchronise()
        {
            if (disposed) return false;
            lock (scan)
            {
                try
                {
                    Scan();
                    while (true)
                    {
                        Pump(int.MaxValue);
                        bool waiting = false;
                        for (int i = 0; i < SampleBank.SongSlots; i++)
                        {
                            var load = Volatile.Read(ref pending[i]);
                            if (load != null && !load.Copied) { waiting = true; break; }
                        }
                        if (!waiting || Volatile.Read(ref pumping) == 0) break;
                        Thread.Yield();
                    }
                    BuildCopied();
                    // A copy that failed (the wave replaced meanwhile) is read once more.
                    Scan();
                    Pump(int.MaxValue);
                    BuildCopied();
                }
                catch (Exception)
                {
                }
            }
            return Pending;
        }

        public void Dispose()
        {
            disposed = true;
            try { timer?.Dispose(); } catch (Exception) { }
            timer = null;
            try { if (subscribed != null) subscribed.WaveChanged -= OnWaveChanged; } catch (Exception) { }
            subscribed = null;
        }
    }
}
