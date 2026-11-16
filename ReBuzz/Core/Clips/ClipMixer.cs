#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): what an audio track plays in a
// block, on the audio thread. Pure (ReBuzz.SessionTests links this file); no allocation after
// construction (for blocks up to the size it was made for).

using System;

namespace ReBuzz.Core.Clips
{
    /// <summary>
    /// A pattern's audio clip as the audio thread sees it: how it plays now (swapped whole by the
    /// UI thread when its settings or audio change; null when it is no audio clip), and its voice.
    /// </summary>
    internal sealed class ClipPlayback
    {
        volatile AudioClipParams current;

        /// <summary>How it plays; set on the UI thread (which makes what the voice needs first).</summary>
        public AudioClipParams Params
        {
            get => current;
            set
            {
                Voice.Prepare(value);
                current = value;
            }
        }

        /// <summary>Audio thread only.</summary>
        public ClipVoice Voice { get; } = new ClipVoice();

        volatile AudioCapture capture;

        /// <summary>A take recording its track's input while this pattern plays; null when none.</summary>
        public AudioCapture Capture
        {
            get => capture;
            set => capture = value;
        }
    }

    /// <summary>
    /// Mixes an audio track's clips for one block: each clip playing renders where its beat
    /// says, and one that played last block but not now fades out (a stop, or the transport
    /// stopping). Holds a few clips at once (a machine on several tracks).
    /// </summary>
    internal sealed class ClipMixer
    {
        public const int MaxClips = 16;

        ClipPlayback[] active = new ClipPlayback[MaxClips];
        ClipPlayback[] next = new ClipPlayback[MaxClips];
        int activeCount, nextCount;

        /// <summary>The longest block it mixes (the audio settings' largest buffer; the engine's blocks are 256 at most).</summary>
        public const int DefaultMaxBlock = 4096;

        public ClipMixer(int maxBlock = DefaultMaxBlock)
        {
            Left = new float[Math.Max(1, maxBlock)];
            Right = new float[Math.Max(1, maxBlock)];
        }

        public float[] Left { get; }
        public float[] Right { get; }

        /// <summary>The frames this block mixes: <c>min(n, capacity)</c>. A longer block is never allocated for; its end is silent.</summary>
        public int Frames { get; private set; }

        /// <summary>Starts a block of <paramref name="n"/> samples: the buffers cleared. Returns the frames it mixes.</summary>
        public int Begin(int n)
        {
            n = Math.Clamp(n, 0, Left.Length);
            Frames = n;
            Array.Clear(Left, 0, n);
            Array.Clear(Right, 0, n);
            nextCount = 0;
            return n;
        }

        /// <summary>A clip plays this block from <paramref name="beat"/>.</summary>
        public void Play(ClipPlayback clip, double beat, double beatsPerSample, int sampleRate, int n)
        {
            var p = clip?.Params;
            if (p == null || nextCount >= MaxClips || Array.IndexOf(next, clip, 0, nextCount) >= 0) return;
            clip.Voice.Render(p, beat, beatsPerSample, sampleRate, Left, Right, 0, Math.Min(n, Frames));
            next[nextCount++] = clip;
        }

        /// <summary>Ends the block: clips that played before and not now fade out. Whether anything sounds.</summary>
        public bool End(int n)
        {
            bool sound = nextCount > 0;
            for (int i = 0; i < activeCount; i++)
            {
                var clip = active[i];
                if (Array.IndexOf(next, clip, 0, nextCount) >= 0) continue;
                clip.Voice.Stop(Left, Right, 0, Math.Min(n, Frames));
                sound = true;
                if (clip.Voice.IsSounding && nextCount < MaxClips) next[nextCount++] = clip;
            }
            Array.Clear(active, 0, activeCount);
            (active, next) = (next, active);
            activeCount = nextCount;
            nextCount = 0;
            return sound;
        }

        /// <summary>Clips sounding (playing or fading out) after the last block.</summary>
        public int Sounding => activeCount;
    }
}
