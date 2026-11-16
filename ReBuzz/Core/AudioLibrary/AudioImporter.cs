#nullable disable
// Fork (docs/specs/neo-audio.md, NA1): audio files into assets. WAV, AIFF, FLAC, OGG (Vorbis) and MP3 are decoded by libsndfile (1.1 or newer
// reads MP3), which every build carries: Windows has libsndfile-1.dll beside the app, the macOS app bundles libsndfile.dylib with what it
// links (tools/mac-bundle.sh), Linux uses the system library. The first two channels are kept, and the audio is converted once, at import,
// to the song's sample rate (Resampler), so playback never converts. UI or import thread; never the audio thread.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ReBuzz.Core.AudioLibrary
{
    internal static class AudioImporter
    {
        /// <summary>The longest file an import takes.</summary>
        public const int MaxMinutes = 30;

        /// <summary>The extensions the import dialogs and the browser offer (what libsndfile 1.1 reads).</summary>
        public static readonly string[] Extensions = { ".wav", ".aif", ".aiff", ".flac", ".ogg", ".mp3" };

        public static bool IsAudioFile(string path) =>
            !string.IsNullOrEmpty(path) && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Decodes the file at <paramref name="songRate"/> Hz (0: its own rate). Throws with a reason a person can read when it can't
        /// (not audio, too long, no decoder for the format, silent of frames).
        /// </summary>
        public static AudioAsset Import(string path, int songRate, string name = null)
        {
            var (left, right, rate, channels) = Decode(path);
            if (left.Length == 0) throw new InvalidDataException("it holds no audio");
            int target = songRate > 0 ? songRate : rate;
            if (rate != target)
            {
                bool stereo = !ReferenceEquals(left, right);
                left = Resampler.Resample(left, rate, target);
                right = stereo ? Resampler.Resample(right, rate, target) : left;
                rate = target;
            }
            string sha;
            using (var f = File.OpenRead(path))
                sha = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
            var source = new Clips.AudioSource(left, right, rate);
            return new AudioAsset(name ?? Path.GetFileNameWithoutExtension(path), path, sha, source, channels > 1 ? 2 : 1);
        }

        /// <summary>The file's first two channels as floats at the file's rate (the right array is the left for a mono file).</summary>
        public static (float[] Left, float[] Right, int Rate, int Channels) Decode(string path)
        {
            using var file = libsndfile.SoundFile.OpenRead(path);
            int channels = file.ChannelCount;
            int rate = file.SampleRate;
            if (channels < 1 || rate <= 0) throw new InvalidDataException("not an audio file");
            long frames = file.FrameCount;
            long limit = (long)MaxMinutes * 60 * rate;
            if (frames > limit) throw new InvalidDataException($"it is longer than {MaxMinutes} minutes");
            // A stream whose length isn't known (frames 0) is read to its end, growing.
            var left = new float[frames > 0 ? frames : 1 << 16];
            var right = channels > 1 ? new float[left.Length] : left;
            const int Chunk = 4096;
            var buffer = new float[Chunk * channels];
            long at = 0;
            while (frames <= 0 || at < frames)
            {
                long want = frames > 0 ? Math.Min(Chunk, frames - at) : Chunk;
                long n = file.ReadFloat(buffer, want);
                if (n <= 0) break;
                if (at + n > left.Length)
                {
                    if (at + n > limit) throw new InvalidDataException($"it is longer than {MaxMinutes} minutes");
                    int size = (int)Math.Min(limit, Math.Max(at + n, left.LongLength * 2));
                    Array.Resize(ref left, size);
                    if (channels > 1) Array.Resize(ref right, size); else right = left;
                }
                for (int i = 0; i < n; i++)
                {
                    left[at + i] = buffer[i * channels];
                    if (channels > 1) right[at + i] = buffer[i * channels + 1];
                }
                at += n;
            }
            if (at < left.Length)
            {
                Array.Resize(ref left, (int)at);
                if (channels > 1) Array.Resize(ref right, (int)at); else right = left;
            }
            return (left, right, rate, channels > 1 ? 2 : 1);
        }

        /// <summary>The major formats this build's libsndfile reads (its own names: WAV, AIFF, FLAC, OGG, MPEG…), for diagnostics.</summary>
        public static IReadOnlyList<string> DecoderFormats()
        {
            try { return libsndfile.SoundFile.FormatsMajor.Select(f => f.Name).ToList(); }
            catch (Exception) { return Array.Empty<string>(); }
        }
    }
}
