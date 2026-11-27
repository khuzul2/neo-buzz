#nullable disable
// Neo Grain (docs/specs/neo-grain.md): the song's wave into the engine, as Neo Sampler's WaveSource does it (Machines/NeoSampler/WaveSource.cs),
// for the one slot the cloud reads. ReBuzz changes waves on the thread that owns the song, loads take the audio lock, and a wave's memory is freed
// when it is replaced; so a wave is read only where that cannot happen meanwhile: in Work (under the audio lock) or on the song's own thread
// (Busy, which renders ask first). A background timer (every 200 ms, and at once when ReBuzz says a wave changed) compares what the slot holds with
// what the engine has (the layer's buffer, length, format and rate) and makes room for a copy; Work copies up to 16 384 frames a block into it (no
// allocation); the timer then builds the source and hands it to the engine, which lets the grains that started on the old one finish on it.
// A slot that holds no wave (or none that can be read) gives the engine no source: the cloud is silent and Status says why.

using System;
using System.Linq;
using System.Threading;
using BuzzGUI.Interfaces;

namespace NeoBuzz.Grain
{
    internal sealed class GrainWaveSource : IDisposable
    {
        public const int FramesPerBlock = 16384;
        /// <summary>The longest wave read, in seconds (10 minutes); a longer one is cut.</summary>
        public const int MaxSeconds = 600;

        /// <summary>What the slot is: not asked for (a built-in source), ready, empty, or being copied.</summary>
        public const int NotUsed = 0, Ready = 1, Empty = 2, Loading = 3;

        sealed class Signature
        {
            public int Slot, Generation, Frames, Channels, Rate;
            public string Name;
            public IWave Wave;
            public IWaveLayer Layer;
            public IntPtr Raw;

            public static Signature Of(int slot, IWave w, int generation)
            {
                var layer = w?.Layers?.FirstOrDefault(l => l != null && l.SampleCount > 0);
                if (layer == null) return null;
                return new Signature
                {
                    Slot = slot, Generation = generation, Wave = w, Name = w.Name ?? "", Layer = layer, Raw = layer.RawSamples,
                    Frames = layer.SampleCount, Channels = Math.Clamp(layer.ChannelCount, 1, 2), Rate = layer.SampleRate,
                };
            }

            public bool Same(Signature o) =>
                o != null && o.Slot == Slot && o.Generation == Generation && o.Wave == Wave && o.Name == Name && o.Layer == Layer && o.Raw == Raw &&
                o.Frames == Frames && o.Channels == Channels && o.Rate == Rate;

            public bool Holds() => Layer.RawSamples == Raw && Layer.SampleCount == Frames && Layer.ChannelCount == Channels;
        }

        sealed class Load
        {
            public Signature Sig;
            public float[] Left, Right;
            public int Frames, Frame;
            public volatile bool Copied, Failed;
        }

        readonly Func<IWavetable> wavetable;
        readonly Func<int> slotOf;
        readonly Action<GrainSource> publish;
        readonly object gate = new object(), scan = new object();
        Signature published;
        Load pending;
        int generation, pumping;
        volatile int status;
        string name;
        IWavetable subscribed;
        Timer timer;
        volatile bool disposed;

        /// <param name="wavetable">The song's wavetable (null while there is none).</param>
        /// <param name="slotOf">The wave slot to read, 1 to 200, or 0 when the source is not a song wave.</param>
        /// <param name="publish">Where a built source goes (null: none).</param>
        public GrainWaveSource(Func<IWavetable> wavetable, Func<int> slotOf, Action<GrainSource> publish)
        {
            this.wavetable = wavetable;
            this.slotOf = slotOf;
            this.publish = publish;
            timer = new Timer(_ => Tick(), null, 50, 200);
        }

        /// <summary>One of <see cref="NotUsed"/>, <see cref="Ready"/>, <see cref="Empty"/>, <see cref="Loading"/>.</summary>
        public int Status => status;

        /// <summary>The name of the wave being read (UI thread reads it; may be a moment old).</summary>
        public string Name => Volatile.Read(ref name);

        /// <summary>A copy is waiting for Work (or for <see cref="Synchronise"/>).</summary>
        public bool Pending => Volatile.Read(ref pending) != null;

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
            Interlocked.Increment(ref generation);
            try { timer?.Change(0, 200); } catch (ObjectDisposedException) { }
        }

        void Scan()
        {
            var wt = wavetable();
            if (wt != subscribed)
            {
                if (subscribed != null) subscribed.WaveChanged -= OnWaveChanged;
                subscribed = wt;
                if (wt != null) wt.WaveChanged += OnWaveChanged;
            }
            int slot = slotOf();
            if (slot < 1)
            {
                Drop(NotUsed);
                return;
            }
            var waves = wt?.Waves;
            var w = waves != null && slot <= waves.Count ? waves[slot - 1] : null;
            var sig = Signature.Of(slot, w, Volatile.Read(ref generation));
            if (sig == null)
            {
                Drop(Empty);
                return;
            }
            Volatile.Write(ref name, sig.Name);
            lock (gate)
            {
                if (pending != null && pending.Sig.Same(sig) && !pending.Failed) return;
                if (pending == null && sig.Same(published)) { status = Ready; return; }
            }
            int frames = (int)Math.Min(sig.Frames, (long)MaxSeconds * Math.Max(1, sig.Rate));
            var load = new Load { Sig = sig, Frames = frames, Left = new float[frames], Right = sig.Channels > 1 ? new float[frames] : null };
            lock (gate) { Volatile.Write(ref pending, load); }
            status = Loading;
        }

        /// <summary>The slot is not used or holds nothing: the engine gets no source, and the memory of the last one is let go.</summary>
        void Drop(int why)
        {
            lock (gate)
            {
                pending = null;
                if (published != null) { published = null; publish(null); }
            }
            Volatile.Write(ref name, why == Empty ? NameOf(slotOf()) : null);
            status = why;
        }

        /// <summary>Copies the waiting wave, up to <paramref name="budget"/> frames: in Work (under the audio lock), or on the song's thread. Allocates nothing.</summary>
        public void Pump(int budget)
        {
            if (Volatile.Read(ref pending) == null) return;
            if (Interlocked.CompareExchange(ref pumping, 1, 0) != 0) return;
            try
            {
                var load = Volatile.Read(ref pending);
                if (load == null || load.Copied || load.Failed) return;
                var sig = load.Sig;
                if (!sig.Holds()) { load.Failed = true; return; }       // replaced meanwhile: the timer reads it again
                int n = Math.Min(budget, load.Frames - load.Frame);
                if (n > 0)
                {
                    sig.Layer.GetDataAsFloat(load.Left, load.Frame, 1, 0, load.Frame, n);
                    if (load.Right != null) sig.Layer.GetDataAsFloat(load.Right, load.Frame, 1, 1, load.Frame, n);
                    load.Frame += n;
                }
                if (load.Frame >= load.Frames) load.Copied = true;
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

        void BuildCopied()
        {
            var load = Volatile.Read(ref pending);
            if (load == null || !load.Copied) return;
            var sig = load.Sig;
            var source = GrainSource.FromWave(sig.Name, load.Left, load.Right, load.Frames, sig.Rate > 0 ? sig.Rate : 44100);
            lock (gate)
            {
                if (pending != load) return;
                published = sig;
                pending = null;
                publish(source);
                status = Ready;
            }
        }

        /// <summary>The song's own thread (a render asks first): reads the slot, copies and builds what is waiting, now. Whether anything is still waiting.</summary>
        public bool Synchronise()
        {
            if (disposed) return false;
            lock (scan)
            {
                try
                {
                    Scan();
                    for (int i = 0; i < 4 && Pending; i++)
                    {
                        Pump(int.MaxValue);
                        BuildCopied();
                        // A copy that failed (the wave replaced meanwhile) is read once more.
                        if (Volatile.Read(ref pending) is { Failed: true }) Scan();
                    }
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
