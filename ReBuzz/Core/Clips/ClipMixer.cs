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

        /// <summary>
        /// A launch of this clip is queued (Session quantization gives lead time): a streamed clip's audio from <paramref name="beat"/> is read
        /// ahead now. Any thread but the audio thread's block.
        /// </summary>
        public void Anticipate(double beat, double beatsPerSample, int sampleRate) => Voice.Anticipate(current, beat, beatsPerSample, sampleRate);

        // Audio thread only (NA2): where the clip's beat goes on from, and how much of its crossfade tail is left (beats).
        internal double NextBeat;
        internal double TailLeft;

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
        readonly SeekBudget seeks = new SeekBudget();          // the Pro starts a block may do inline (neo-audio.md section 11)

        /// <summary>The longest block it mixes (the audio settings' largest buffer; the engine's blocks are 256 at most).</summary>
        public const int DefaultMaxBlock = 4096;

        public ClipMixer(int maxBlock = DefaultMaxBlock)
        {
            Left = new float[Math.Max(1, maxBlock)];
            Right = new float[Math.Max(1, maxBlock)];
            scratchLeft = new float[Left.Length];
            scratchRight = new float[Left.Length];
        }

        public float[] Left { get; }
        public float[] Right { get; }

        // A clip with fades renders here first, and is mixed in with its envelope (made once, with the mixer).
        readonly float[] scratchLeft, scratchRight;

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
            seeks.Left = SeekBudget.PerBlock;
            return n;
        }

        /// <summary>
        /// A clip plays this block from <paramref name="beat"/>. An arrangement clip says where it is in its clip: <paramref name="clipPos"/>
        /// beats since the clip started and <paramref name="clipLeft"/> beats to its end, both at the block's first sample (NaN for a clip
        /// that isn't in the arrangement, which has no fades).
        /// </summary>
        public void Play(ClipPlayback clip, double beat, double beatsPerSample, int sampleRate, int n, double clipPos = double.NaN, double clipLeft = double.NaN)
        {
            var p = clip?.Params;
            if (p == null || nextCount >= MaxClips || Array.IndexOf(next, clip, 0, nextCount) >= 0) return;
            int count = Math.Min(n, Frames);
            clip.Voice.Budget = seeks;
            var fades = p.Fades;
            if (fades.Active && !double.IsNaN(clipPos) && !double.IsNaN(clipLeft))
            {
                Array.Clear(scratchLeft, 0, count);
                Array.Clear(scratchRight, 0, count);
                clip.Voice.Render(p, beat, beatsPerSample, sampleRate, scratchLeft, scratchRight, 0, count);
                MixEnvelope(fades, clipPos, clipLeft, beatsPerSample, count);
            }
            else
                clip.Voice.Render(p, beat, beatsPerSample, sampleRate, Left, Right, 0, count);
            clip.NextBeat = beat + count * beatsPerSample;
            clip.TailLeft = fades.Tail;
            next[nextCount++] = clip;
        }

        /// <summary>The scratch buffers into the mix, each sample times the fades' gain where it falls.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        void MixEnvelope(ClipFades fades, double pos, double left, double beatsPerSample, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float g = fades.GainAt(pos + i * beatsPerSample, left - i * beatsPerSample);
                Left[i] += scratchLeft[i] * g;
                Right[i] += scratchRight[i] * g;
            }
        }

        /// <summary>The tail of a clip that has ended (its event was cut by the next): the audio goes on, falling as a cosine.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        void MixTail(ClipPlayback clip, AudioClipParams p, double beatsPerSample, int sampleRate, int count)
        {
            Array.Clear(scratchLeft, 0, count);
            Array.Clear(scratchRight, 0, count);
            clip.Voice.Render(p, clip.NextBeat, beatsPerSample, sampleRate, scratchLeft, scratchRight, 0, count);
            double tail = p.Fades.Tail, gone = tail - clip.TailLeft;
            for (int i = 0; i < count; i++)
            {
                float g = p.Fades.TailGainAt(gone + i * beatsPerSample);
                Left[i] += scratchLeft[i] * g;
                Right[i] += scratchRight[i] * g;
            }
            clip.NextBeat += count * beatsPerSample;
            clip.TailLeft -= count * beatsPerSample;
        }

        /// <summary>Ends the block: clips that played before and not now fade out. Whether anything sounds.</summary>
        public bool End(int n) => End(n, 0, 0);

        /// <summary>
        /// Ends the block. With <paramref name="beatsPerSample"/> (the transport playing), a clip that played before and not now but has a
        /// crossfade tail goes on under it for the tail's length; any other fades out in a few samples.
        /// </summary>
        public bool End(int n, double beatsPerSample, int sampleRate)
        {
            bool sound = nextCount > 0;
            int count = Math.Min(n, Frames);
            for (int i = 0; i < activeCount; i++)
            {
                var clip = active[i];
                if (Array.IndexOf(next, clip, 0, nextCount) >= 0) continue;
                var p = clip.Params;
                if (beatsPerSample > 0 && clip.TailLeft > 0 && p != null && p.Fades.Tail > 0 && clip.Voice.IsSounding && nextCount < MaxClips)
                {
                    MixTail(clip, p, beatsPerSample, sampleRate, count);
                    sound = true;
                    if (clip.TailLeft <= 0)
                    {
                        clip.TailLeft = 0;
                        clip.Voice.Silence();
                    }
                    else next[nextCount++] = clip;
                    continue;
                }
                clip.TailLeft = 0;
                clip.Voice.Stop(Left, Right, 0, count);
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
