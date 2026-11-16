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
            var data = channel > 0 ? source.Right : source.Left;
            for (int i = 0; i < destination.Length; i++)
            {
                long f = frame + i;
                destination[i] = f >= 0 && f < data.Length ? data[f] : 0f;
            }
            return destination.Length;
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
            Reader = new InMemoryAssetReader(source, Channels);
        }

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
        public IAudioAssetReader Reader { get; }

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
            Changed?.Invoke();
            return asset;
        }

        public void Clear()
        {
            if (assets.Count == 0) return;
            assets.Clear();
            aliases.Clear();
            Changed?.Invoke();
        }

        /// <summary>The store's content becomes these (a song opened).</summary>
        public void ReplaceWith(IEnumerable<AudioAsset> list)
        {
            assets.Clear();
            aliases.Clear();
            foreach (var a in list ?? Enumerable.Empty<AudioAsset>()) assets[a.Id] = a;
            Changed?.Invoke();
        }
    }
}
