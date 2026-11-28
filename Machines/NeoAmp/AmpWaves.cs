#nullable disable
// Neo Amp (docs/specs/neo-amp.md §5): the song's wave as the cabinet (as Neo Space reads its wave: the same reader). The song saves its waves, so Neo Amp saves only the slot (the
// Wave parameter): the reference is the wave's id, never a copy of its samples. The machine reads that wave in the way Neo Sampler
// does (WaveSource.cs), because the rules are the host's:
//   ReBuzz changes waves on the thread that owns the song (its UI thread, which also runs the agent server's tools), loads take the
//   audio lock, and a wave's memory is freed when it is replaced. So a wave's samples are read only where that cannot happen
//   meanwhile: in Work (under the audio lock) or on the song's own thread (Busy, which renders ask first). A background timer (every
//   200 ms, and at once when ReBuzz says a wave changed or the engine wants another slot) compares what the slot holds with what
//   was read (by buffer, length, format, rate), makes room for a copy, and Work copies up to 16 384 frames a block into it (no
//   allocation); the timer then publishes the clip, with a version, to the engine, which builds the response in the background.
// One slot at a time: the one the engine says it wants (Cabinet = Wave); the first layer of a wave with layers, at most 250 ms of it.

using System;
using System.Linq;
using System.Threading;
using BuzzGUI.Interfaces;

namespace NeoBuzz.Amp
{
    internal sealed class AmpWaves : IAmpWaves, IDisposable
    {
        public const int FramesPerBlock = 16384;

        /// <summary>What the slot held when it was read.</summary>
        sealed class Signature
        {
            public IWave Wave;
            public string Name;
            public int Generation;
            public IWaveLayer Layer;
            public IntPtr Raw;
            public int Frames, Channels, Rate;
            public WaveFormat Format;
            public float Volume;

            public static Signature Of(IWave w, int generation)
            {
                if (w == null) return null;
                var layer = w.Layers?.FirstOrDefault(l => l != null && l.SampleCount > 0);
                if (layer == null) return null;
                return new Signature
                {
                    Wave = w, Name = w.Name ?? "", Generation = generation, Layer = layer, Raw = layer.RawSamples, Frames = layer.SampleCount,
                    Channels = Math.Clamp(layer.ChannelCount, 1, 2), Rate = layer.SampleRate > 0 ? layer.SampleRate : 44100, Format = layer.Format, Volume = w.Volume,
                };
            }

            public bool Same(Signature o) => o != null && o.Wave == Wave && o.Name == Name && o.Generation == Generation && o.Layer == Layer && o.Raw == Raw
                && o.Frames == Frames && o.Channels == Channels && o.Rate == Rate && o.Format == Format && o.Volume == Volume;

            public bool Holds() => Layer.RawSamples == Raw && Layer.SampleCount == Frames && Layer.ChannelCount == Channels;
        }

        /// <summary>A copy in progress: room for the samples, filled block by block.</summary>
        sealed class Load
        {
            public Signature Sig;
            public int Slot, Count;
            public float[] Left, Right;
            public int Frame;
            public volatile bool Copied, Failed;
        }

        readonly Func<IWavetable> wavetable;
        readonly object gate = new object(), scan = new object();
        volatile int wanted;
        int generation, version, pumping;
        IWavetable subscribed;
        Timer timer;
        volatile bool disposed;
        Load pending;
        Signature published;
        AmpClip clip;
        int clipSlot, missingSlot;

        /// <param name="wavetable">The song's wavetable (null while there is none).</param>
        public AmpWaves(Func<IWavetable> wavetable)
        {
            this.wavetable = wavetable;
            timer = new Timer(_ => Tick(), null, 50, 200);
        }

        /// <summary>Copies are waiting for Work (or <see cref="Synchronise"/>).</summary>
        public bool Pending => Volatile.Read(ref pending) != null;

        public void Want(int slot)
        {
            if (slot == wanted) return;
            wanted = slot;
            try { timer?.Change(0, 200); } catch (ObjectDisposedException) { }
        }

        public AmpWaveState State(int slot)
        {
            lock (gate)
            {
                if (clip != null && clipSlot == slot) return AmpWaveState.Ready;
                if (missingSlot == slot) return AmpWaveState.Missing;
            }
            return AmpWaveState.Loading;
        }

        public AmpClip Get(int slot)
        {
            lock (gate) return clip != null && clipSlot == slot ? clip : null;
        }

        /// <summary>The name of a slot's wave, or null (the song's own thread: the editor).</summary>
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
                Build();
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
            Interlocked.Increment(ref generation);
            try { timer?.Change(0, 200); } catch (ObjectDisposedException) { }
        }

        /// <summary>Compares the wanted slot with what was read and makes room for what changed.</summary>
        void Scan()
        {
            var wt = wavetable();
            if (wt != subscribed)
            {
                if (subscribed != null) subscribed.WaveChanged -= OnWaveChanged;
                subscribed = wt;
                if (wt != null) wt.WaveChanged += OnWaveChanged;
            }
            int slot = wanted;
            if (slot < 1)
            {
                // Not the cabinet: let the samples go (a Cabinet that comes back to Wave reads them again).
                lock (gate)
                {
                    clip = null; clipSlot = 0; missingSlot = 0;
                    published = null;
                    Volatile.Write(ref pending, null);
                }
                return;
            }
            var waves = wt?.Waves;
            var w = waves != null && slot <= waves.Count ? waves[slot - 1] : null;
            var sig = Signature.Of(w, Volatile.Read(ref generation));
            if (sig == null)
            {
                lock (gate)
                {
                    clip = null; clipSlot = 0; published = null;
                    Volatile.Write(ref pending, null);
                    missingSlot = slot;
                }
                return;
            }
            lock (gate)
            {
                missingSlot = 0;
                var p = Volatile.Read(ref pending);
                if (p != null && p.Slot == slot && p.Sig.Same(sig) && !p.Failed) return;
                if (p == null && clipSlot == slot && sig.Same(published)) return;
            }
            // At most 10 s of it, as the response will be (resampled to the host's rate later: a little more for the margin).
            int frames = (int)Math.Min((long)sig.Frames, (long)(AmpCabs.MaxWaveSeconds * sig.Rate) + 64);
            var load = new Load { Sig = sig, Slot = slot, Count = frames, Left = new float[frames], Right = sig.Channels > 1 ? new float[frames] : null };
            lock (gate) Volatile.Write(ref pending, load);
        }

        /// <summary>Copies waiting samples, up to <paramref name="budget"/> frames: in Work (under the audio lock) or on the song's thread. Allocates nothing.</summary>
        public void Pump(int budget)
        {
            var load = Volatile.Read(ref pending);
            if (load == null || load.Copied || load.Failed) return;
            if (Interlocked.CompareExchange(ref pumping, 1, 0) != 0) return;
            try
            {
                var sig = load.Sig;
                if (!sig.Holds())
                {
                    load.Failed = true;   // replaced meanwhile: the timer reads it again
                    return;
                }
                int n = Math.Min(budget, load.Count - load.Frame);
                if (n > 0)
                {
                    sig.Layer.GetDataAsFloat(load.Left, load.Frame, 1, 0, load.Frame, n);
                    if (load.Right != null) sig.Layer.GetDataAsFloat(load.Right, load.Frame, 1, 1, load.Frame, n);
                    load.Frame += n;
                }
                if (load.Frame >= load.Count) load.Copied = true;
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

        /// <summary>Publishes a copied wave to the engine.</summary>
        void Build()
        {
            var load = Volatile.Read(ref pending);
            if (load == null || !load.Copied) return;
            var made = new AmpClip(load.Sig.Name, load.Left, load.Right, load.Sig.Rate, Interlocked.Increment(ref version));
            lock (gate)
            {
                if (!ReferenceEquals(pending, load)) return;
                clip = made;
                clipSlot = load.Slot;
                published = load.Sig;
                missingSlot = 0;
                Volatile.Write(ref pending, null);
            }
        }

        /// <summary>
        /// The song's own thread (a render asks first): reads the wanted slot, copies and publishes it, now. Whether anything is
        /// still waiting afterwards.
        /// </summary>
        public bool Synchronise()
        {
            if (disposed) return false;
            lock (scan)
            {
                try
                {
                    Scan();
                    Pump(int.MaxValue);
                    Build();
                    // A copy that failed (the wave replaced meanwhile) is read once more.
                    Scan();
                    Pump(int.MaxValue);
                    Build();
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
