#nullable disable
// Fork (docs/specs/neo-audio.md, NA1): the song's audio store. Audio clips play assets, not waves of the wavetable: an asset is decoded
// float audio at one sample rate, with the file it came from (name, path, SHA-256), and is held once however many clips play it.
// The .nbl embeds them (NblAudio); a .bmx export writes them into waves while slots last. Pure: ReBuzz.SessionTests links this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ReBuzz.Core.Clips;

namespace ReBuzz.Core.AudioLibrary
{
    /// <summary>
    /// How an asset's samples are read. In NA1 the audio is in memory (<see cref="InMemoryAssetReader"/>); NA1b replaces it with a reader
    /// that streams from a decoded cache, so the voices never learn where the samples are.
    /// </summary>
    internal interface IAudioAssetReader
    {
        long Frames { get; }
        int Channels { get; }
        int SampleRate { get; }

        /// <summary>
        /// Copies samples of one channel (0 left, 1 right; a mono asset gives its one channel for both) from <paramref name="frame"/> into
        /// <paramref name="destination"/>; frames outside the audio are 0. Returns the frames written (all of the span).
        /// </summary>
        int Read(long frame, int channel, Span<float> destination);

        /// <summary>The audio as arrays when it is all in memory (the voices' fast path); null for a streaming reader.</summary>
        AudioSource InMemory { get; }
    }

    internal sealed class InMemoryAssetReader : IAudioAssetReader
    {
        readonly AudioSource source;
        readonly int channels;

        public InMemoryAssetReader(AudioSource source, int channels)
        {
            this.source = source;
            this.channels = Math.Clamp(channels, 1, 2);
        }

        public long Frames => source.Length;
        public int Channels => channels;
        public int SampleRate => source.SampleRate;
        public AudioSource InMemory => source;

        public int Read(long frame, int channel, Span<float> destination)
        {
            source.CopyExact(channel > 0 ? 1 : 0, frame, destination);
            return destination.Length;
        }
    }

    /// <summary>
    /// NA1b: reads an asset that is (or is becoming) streamed. It reads what the engine reads (<see cref="AudioSource.CopyTo"/>: pages in
    /// memory, silence and a count for a page not there yet), so it is for the audio thread and for tests of it; code that needs every sample
    /// exactly uses <see cref="AudioSource.CopyExact"/>. <see cref="InMemory"/> is null once the samples are only in the cache file.
    /// </summary>
    internal sealed class StreamingAssetReader : IAudioAssetReader
    {
        readonly AudioSource source;
        readonly int channels;

        public StreamingAssetReader(AudioSource source, int channels)
        {
            this.source = source;
            this.channels = Math.Clamp(channels, 1, 2);
        }

        public long Frames => source.Length;
        public int Channels => channels;
        public int SampleRate => source.SampleRate;
        public AudioSource InMemory => source.InMemory ? source : null;

        public int Read(long frame, int channel, Span<float> destination)
        {
            source.CopyTo(channel > 0 && channels > 1 ? 1 : 0, frame, destination);
            return destination.Length;
        }
    }

    /// <summary>Where an asset's audio is (neo-audio.md, NA1b).</summary>
    internal enum AssetStorage
    {
        /// <summary>Floats in memory only (short audio, or streaming has not started).</summary>
        Memory,
        /// <summary>In memory and being written to the cache; plays from memory until the file is ready.</summary>
        Filling,
        /// <summary>Read from the cache file through the prefetch thread.</summary>
        Streaming,
        /// <summary>The cache file is being written again (it was removed or damaged); what is in memory of it plays meanwhile.</summary>
        Repairing,
        /// <summary>The cache file could not be made and the audio is not in memory: the clips are silent.</summary>
        Failed,
    }

    /// <summary>How to make an asset's decoded cache file again: from the song file's blobs, or from the file it was imported from.</summary>
    internal abstract class AssetRecipe
    {
        public abstract string Describe { get; }

        /// <summary>Whether what it rebuilds from is there now.</summary>
        public virtual bool Available => true;

        /// <summary>Writes every piece of the audio into <paramref name="writer"/> (not completing it). False if it can't (the source is gone or does not match).</summary>
        public abstract bool Rebuild(AudioCache.Writer writer, AudioAsset asset, System.Threading.CancellationToken cancel);
    }

    /// <summary>Decodes the file again (the same decoder and resampler make the same audio; checked against the asset's id).</summary>
    internal sealed class FileRecipe : AssetRecipe
    {
        readonly string path;
        readonly int rate;
        public FileRecipe(string path, int rate) { this.path = path; this.rate = rate; }
        public override string Describe => "the file " + path;
        public override bool Available => !string.IsNullOrEmpty(path) && System.IO.File.Exists(path);

        public override bool Rebuild(AudioCache.Writer writer, AudioAsset asset, System.Threading.CancellationToken cancel)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return false;
                var again = AudioImporter.Import(path, rate);
                if (again.Id != asset.Id) return false;
                var (l, r) = again.Source.ToArrays();
                writer.WriteAll(l, r, null, cancel);
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>A piece of audio of the song: decoded floats (mono or stereo) at one sample rate, and where they came from.</summary>
    internal sealed class AudioAsset
    {
        public AudioAsset(string name, string sourcePath, string sourceSha256, AudioSource source, int channels, int originWave = -1)
        {
            Name = name ?? "";
            SourcePath = sourcePath ?? "";
            Source = source;
            Channels = Math.Clamp(channels, 1, 2);
            Id = ContentId(source, Channels);
            SourceSha256 = string.IsNullOrEmpty(sourceSha256) ? Id : sourceSha256;
            OriginWave = originWave;
            inMemoryReader = new InMemoryAssetReader(source, Channels);
            streamingReader = new StreamingAssetReader(source, Channels);
        }

        /// <summary>An asset whose audio is only in the cache file (a song opened with its cache present): the id is the song's own.</summary>
        public AudioAsset(string name, string sourcePath, string sourceSha256, string id, StreamedAudio stream, int channels, int originWave = -1)
        {
            Name = name ?? "";
            SourcePath = sourcePath ?? "";
            Source = AudioSource.FromStream(stream);
            Source.Attach(stream);
            Channels = Math.Clamp(channels, 1, 2);
            Id = id;
            SourceSha256 = string.IsNullOrEmpty(sourceSha256) ? Id : sourceSha256;
            OriginWave = originWave;
            inMemoryReader = new InMemoryAssetReader(Source, Channels);
            streamingReader = new StreamingAssetReader(Source, Channels);
            cache = stream;
            Storage = AssetStorage.Streaming;
            stream.DamageFound += OnDamage;
        }

        readonly InMemoryAssetReader inMemoryReader;
        readonly StreamingAssetReader streamingReader;

        /// <summary>Content-addressed: the same audio is the same asset (so a clip copied, or a file imported twice, shares one).</summary>
        public string Id { get; }
        public string Name { get; }
        public string SourcePath { get; }
        /// <summary>SHA-256 (hex) of the file it was imported from; of the decoded audio for a recording or a wave.</summary>
        public string SourceSha256 { get; }
        public int SampleRate => Source.SampleRate;
        public int Channels { get; }
        public long Frames => Source.Length;
        public double Seconds => Source.Seconds;
        /// <summary>The wave of the wavetable it was migrated from (kept untouched there), or -1.</summary>
        public int OriginWave { get; }
        public AudioSource Source { get; }
        public IAudioAssetReader Reader => Source.InMemory && Storage == AssetStorage.Memory ? inMemoryReader : streamingReader;


        // ------------------------------------------------------------------ streaming (NA1b)

        volatile AssetStorage storage = AssetStorage.Memory;
        StreamedAudio cache;
        int streamingStarted;
        double progress;
        System.Threading.CancellationTokenSource cancelFill;

        /// <summary>Where the audio is now (memory, filling the cache, streaming, being repaired).</summary>
        public AssetStorage Storage { get => storage; private set { storage = value; StorageChanged?.Invoke(this); } }

        /// <summary>Raised (on a background thread) when <see cref="Storage"/> or the progress changes.</summary>
        public event Action<AudioAsset> StorageChanged;

        /// <summary>0 to 1: how much of the cache file is written, while it is.</summary>
        public double CacheProgress => System.Threading.Volatile.Read(ref progress);

        /// <summary>The cache file the audio streams from, once it does.</summary>
        public StreamedAudio Cache => cache;

        /// <summary>How the cache file is made again when it is lost or damaged.</summary>
        public AssetRecipe Recipe { get; set; }

        /// <summary>Samples that were not in memory when a voice read them, and the blocks that had some (the streaming's quality).</summary>
        public (long Missed, long Underruns) StreamStats => cache == null ? (0, 0) : (cache.MissedSamples, cache.Underruns);

        /// <summary>Bytes of floats this asset holds in memory (arrays and pages).</summary>
        public long ResidentBytes => Source.MemoryBytes + (cache?.ResidentBytes ?? 0);

        /// <summary>The cache file is whole for this audio.</summary>
        public static bool CacheReady(string id, long frames, int channels, int rate) => AudioCache.Exists(id, frames, channels, rate);

        /// <summary>
        /// Starts making the cache file in the background (a short asset, or one already streaming, does nothing): it plays from memory until the
        /// file is whole, then reads from it, and the floats are let go. <paramref name="done"/> runs (on that thread) when it has finished.
        /// </summary>
        public System.Threading.Tasks.Task BeginStreaming(Action<AudioAsset> done = null)
        {
            if (!StreamingPolicy.ShouldStream(Frames, Channels) || !Source.InMemory || storage != AssetStorage.Memory ||
                System.Threading.Interlocked.Exchange(ref streamingStarted, 1) != 0)
                return System.Threading.Tasks.Task.CompletedTask;
            cancelFill = new System.Threading.CancellationTokenSource();
            var token = cancelFill.Token;
            Storage = AssetStorage.Filling;
            return System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string id = Id;
                    var s = StreamedAudio.Open(id, Frames, Channels, SampleRate);
                    if (s == null)
                    {
                        using (var w = new AudioCache.Writer(id, Frames, Channels, SampleRate))
                        {
                            var src = Source;
                            var piece = new float[AudioCache.PageFrames];
                            var piece2 = new float[AudioCache.PageFrames];
                            int pages = AudioCache.PagesOf(Frames);
                            for (int p = 0; p < pages; p++)
                            {
                                token.ThrowIfCancellationRequested();
                                long from = (long)p * AudioCache.PageFrames;
                                int n = (int)Math.Min(AudioCache.PageFrames, Frames - from);
                                src.CopyExact(0, from, piece.AsSpan(0, n));
                                w.WritePiece(0, p, piece.AsSpan(0, n));
                                if (Channels > 1)
                                {
                                    src.CopyExact(1, from, piece2.AsSpan(0, n));
                                    w.WritePiece(1, p, piece2.AsSpan(0, n));
                                }
                                if ((p & 7) == 0) { System.Threading.Volatile.Write(ref progress, (p + 1) / (double)pages); StorageChanged?.Invoke(this); }
                            }
                            if (!w.Complete()) throw new System.IO.IOException("The cache file is not whole.");
                        }
                        s = StreamedAudio.Open(id, Frames, Channels, SampleRate);
                    }
                    if (s == null) throw new System.IO.IOException("The cache file can't be opened.");
                    token.ThrowIfCancellationRequested();
                    Adopt(s);
                    // The pages the voices (and the pins) want are read before the floats go: what plays does not change.
                    StreamPrefetcher.WaitForVoices(s, 300);
                    StreamPrefetcher.WaitSettled(s, 5000);
                    Source.DropMemory();
                    System.Threading.Volatile.Write(ref progress, 1);
                    Storage = AssetStorage.Streaming;
                    AudioCache.Trim();
                }
                catch (Exception)
                {
                    // No cache (the disk is full, the folder is not writable): the audio stays in memory, as before streaming.
                    if (storage == AssetStorage.Filling) Storage = AssetStorage.Memory;
                    System.Threading.Interlocked.Exchange(ref streamingStarted, 0);
                }
                done?.Invoke(this);
            });
        }

        void Adopt(StreamedAudio s)
        {
            cache = s;
            s.DamageFound += OnDamage;
            Source.Attach(s);
            lock (clipPins)
                foreach (var (owner, pin) in clipPins) pin.Source.Pin(owner, pin.Ranges);      // the clips' starts are read before the floats go
        }

        readonly Dictionary<object, (AudioSource Source, IReadOnlyList<(long From, long To)> Ranges)> clipPins =
            new Dictionary<object, (AudioSource, IReadOnlyList<(long, long)>)>(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// A clip (<paramref name="owner"/>) plays this asset through <paramref name="source"/> (the asset's own, or its reversed view) and may start
        /// at these frames of it: they stay in memory, so a launch does not wait for the disk. Null ranges let go of them. UI thread.
        /// </summary>
        public void PinClip(object owner, AudioSource source, IReadOnlyList<(long From, long To)> ranges)
        {
            lock (clipPins)
            {
                if (ranges == null || ranges.Count == 0 || source == null)
                {
                    if (clipPins.Remove(owner, out var old)) old.Source.Pin(owner, null);
                    return;
                }
                clipPins[owner] = (source, ranges);
            }
            if (cache != null) source.Pin(owner, ranges);
        }

        int repairing;

        void OnDamage(StreamedAudio s)
        {
            if (System.Threading.Interlocked.Exchange(ref repairing, 1) != 0) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { Repair(s); }
                finally { System.Threading.Interlocked.Exchange(ref repairing, 0); }
            });
        }

        /// <summary>
        /// The cache file was removed or fails its checks: writes it again (from the open file if it is only gone, else from the recipe),
        /// then reads it again. The pages in memory play meanwhile; a page that is not there is silence, counted.
        /// </summary>
        public bool Repair(StreamedAudio s = null)
        {
            s ??= cache;
            if (s == null) return false;
            var previous = storage;
            Storage = AssetStorage.Repairing;
            bool ok = false;
            try
            {
                bool removedOnly = s.DamageReason == null || s.DamageReason.Contains("removed");
                ok = (removedOnly && WriteCache(w => s.CopyTo(w))) || (Recipe != null && WriteCache(w => Recipe.Rebuild(w, this, default)));
                if (ok) ok = s.Reload();
            }
            catch (Exception) { ok = false; }
            Storage = ok ? AssetStorage.Streaming : (Source.InMemory ? previous : AssetStorage.Failed);
            return ok;
        }

        bool WriteCache(Func<AudioCache.Writer, bool> fill)
        {
            using var w = new AudioCache.Writer(Id, Frames, Channels, SampleRate);
            return fill(w) && w.Complete();
        }

        /// <summary>Lets go of the cache file (the song closed or the asset was dropped from the store).</summary>
        public void ReleaseStream()
        {
            cancelFill?.Cancel();
            cache?.Dispose();
        }

        volatile PeakCache peaks;
        int peaksStarted;

        /// <summary>
        /// The peaks the views draw its waveform from (NA2), or null until the background build ends; asking starts the build once. Kept in
        /// memory beside the asset, never saved. <paramref name="ready"/> runs on a pool thread when they are made.
        /// </summary>
        public PeakCache PeaksOrBuild(Action ready = null)
        {
            var p = peaks;
            if (p != null) return p;
            if (System.Threading.Interlocked.Exchange(ref peaksStarted, 1) == 0)
                PeakCache.BuildAsync(Source, c =>
                {
                    peaks = c;
                    ready?.Invoke();
                });
            return null;
        }

        /// <summary>The peaks if they are made.</summary>
        public PeakCache Peaks => peaks;

        /// <summary>"a" and 16 hex digits of the SHA-256 of the audio's rate, channels and samples.</summary>
        public static string ContentId(AudioSource s, int channels)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            h.AppendData(BitConverter.GetBytes(s.SampleRate));
            h.AppendData(BitConverter.GetBytes(channels));
            h.AppendData(MemoryMarshal.AsBytes(s.Left.AsSpan()));
            if (channels > 1) h.AppendData(MemoryMarshal.AsBytes(s.Right.AsSpan()));
            return "a" + Convert.ToHexString(h.GetHashAndReset()).Substring(0, 16).ToLowerInvariant();
        }
    }

    /// <summary>The song's assets by id. UI thread (the audio thread only ever sees the arrays of an asset a clip has been given).</summary>
    internal sealed class AudioAssetStore
    {
        static AudioAssetStore() => StreamingPolicy.Load();       // the preferences file: threshold, head, read-ahead, limits (NA1b)

        readonly Dictionary<string, AudioAsset> assets = new(StringComparer.Ordinal);

        public int Count => assets.Count;
        public IEnumerable<AudioAsset> All => assets.Values.OrderBy(a => a.Id, StringComparer.Ordinal);

        /// <summary>Raised after assets were added or replaced.</summary>
        public event Action Changed;

        // A recording's clip names an id before the audio exists; the store aliases it to the asset the recording became.
        readonly Dictionary<string, string> aliases = new(StringComparer.Ordinal);

        public AudioAsset Get(string id)
        {
            if (id == null) return null;
            if (assets.TryGetValue(id, out var a)) return a;
            return aliases.TryGetValue(id, out var real) && assets.TryGetValue(real, out a) ? a : null;
        }

        /// <summary>The id the store holds for this one (the asset's own id; the id itself when it is unknown).</summary>
        public string Resolve(string id) => Get(id)?.Id ?? id;

        public void Alias(string from, string to)
        {
            if (from != to) aliases[from] = to;
        }

        /// <summary>Adds the asset (or returns the one already held with the same audio).</summary>
        public AudioAsset Add(AudioAsset asset)
        {
            if (assets.TryGetValue(asset.Id, out var known)) return known;
            assets[asset.Id] = asset;
            asset.BeginStreaming();                 // long audio goes to the cache in the background (NA1b)
            Changed?.Invoke();
            return asset;
        }

        /// <summary>The assets that are streaming or being written to the cache.</summary>
        public IEnumerable<AudioAsset> Streamed => assets.Values.Where(a => a.Storage != AssetStorage.Memory);

        public void Clear()
        {
            if (assets.Count == 0) return;
            foreach (var a in assets.Values) a.ReleaseStream();
            assets.Clear();
            aliases.Clear();
            Changed?.Invoke();
        }

        /// <summary>The store's content becomes these (a song opened).</summary>
        public void ReplaceWith(IEnumerable<AudioAsset> list)
        {
            var incoming = (list ?? Enumerable.Empty<AudioAsset>()).ToList();
            foreach (var old in assets.Values)
                if (!incoming.Contains(old)) old.ReleaseStream();
            assets.Clear();
            aliases.Clear();
            foreach (var a in incoming)
            {
                assets[a.Id] = a;
                a.BeginStreaming();
            }
            Changed?.Invoke();
        }
    }
}
