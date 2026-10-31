#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): audio being recorded into a take,
// written on the audio thread into chunks the UI thread makes ahead of it. Pure
// (ReBuzz.SessionTests links this file); the audio thread never allocates or waits.

using System;
using System.Threading;

namespace ReBuzz.Core.Clips
{
    /// <summary>
    /// A recording's audio, stereo, from the first frame given: the first <c>skip</c> frames
    /// (the round trip the input took to come back, so the take starts where it was played) are
    /// dropped, then <see cref="Length"/> frames are kept (until stopped: as many as come, until
    /// the length is set). Chunks of <see cref="ChunkFrames"/> are made by the UI thread
    /// (<see cref="Prepare"/>); a frame with no chunk yet is kept as silence (counted in
    /// <see cref="Dropped"/>), so the take keeps its time.
    /// </summary>
    internal sealed class AudioCapture
    {
        public const int ChunkFrames = 1 << 16;

        readonly float[][] chunks;          // interleaved left, right
        readonly long capacity;
        long skip;
        long written;
        long length;
        long dropped;
        bool started;

        /// <param name="lengthFrames">Frames to keep; long.MaxValue until stopped.</param>
        /// <param name="maxFrames">The most it keeps, until stopped.</param>
        /// <param name="framesPerBeat">Frames a beat of the take lasts: where <see cref="StartAt"/> starts.</param>
        public AudioCapture(int sampleRate, long skipFrames, long lengthFrames, long maxFrames, double framesPerBeat = 0)
        {
            SampleRate = Math.Max(1, sampleRate);
            FramesPerBeat = Math.Max(0, framesPerBeat);
            skip = Math.Max(0, skipFrames);
            capacity = Math.Max(1, lengthFrames == long.MaxValue ? maxFrames : Math.Min(lengthFrames, Math.Max(1, maxFrames)));
            length = lengthFrames == long.MaxValue ? long.MaxValue : Math.Clamp(lengthFrames, 0, capacity);
            chunks = new float[(int)((capacity + ChunkFrames - 1) / ChunkFrames)][];
            Prepare(lengthFrames == long.MaxValue ? 8L * SampleRate : capacity);
        }

        public int SampleRate { get; }

        public double FramesPerBeat { get; }

        /// <summary>Audio thread: the block (engine frame count) last written, to keep time when the track's machine didn't work.</summary>
        public long LastBlock = long.MinValue;

        /// <summary>
        /// Audio thread, before the first frames: the take's beat then. A take whose launch came
        /// late starts into its pattern; the frames before stay silent, so the audio keeps the
        /// pattern's time.
        /// </summary>
        public void StartAt(double beat)
        {
            if (started) return;
            started = true;
            if (FramesPerBeat > 0 && beat > 0)
                Volatile.Write(ref written, Math.Min(Math.Min(Length, capacity), (long)Math.Round(beat * FramesPerBeat)));
        }

        /// <summary>Frames kept so far.</summary>
        public long Written => Volatile.Read(ref written);

        /// <summary>Frames it keeps; long.MaxValue until known.</summary>
        public long Length => Volatile.Read(ref length);

        /// <summary>Whether it has all its frames.</summary>
        public bool IsDone => Written >= Length;

        /// <summary>Frames kept as silence for want of a chunk (the UI thread was late).</summary>
        public long Dropped => Volatile.Read(ref dropped);

        /// <summary>UI thread: chunks for the next <paramref name="framesAhead"/> frames.</summary>
        public void Prepare(long framesAhead)
        {
            long upTo = Math.Min(capacity, Written + Math.Max(0, framesAhead));
            for (int c = 0; (long)c * ChunkFrames < upTo && c < chunks.Length; c++)
                if (Volatile.Read(ref chunks[c]) == null)
                    Volatile.Write(ref chunks[c], new float[ChunkFrames * 2]);
        }

        /// <summary>UI thread: how many frames it keeps (a recording until stopped, stopped).</summary>
        public void SetLength(long frames) => Volatile.Write(ref length, Math.Clamp(frames, 0, capacity));

        /// <summary>Audio thread: frames from separate channels (times <paramref name="scale"/>).</summary>
        public void Write(float[] left, float[] right, int count, float scale)
        {
            started = true;
            long w = written, end = Length;
            for (int i = 0; i < count && w < end; i++)
            {
                if (skip > 0)
                {
                    skip--;
                    continue;
                }
                Put(w++, left[i] * scale, right[i] * scale);
            }
            Volatile.Write(ref written, w);
        }

        /// <summary>Audio thread: interleaved frames (times <paramref name="scale"/>).</summary>
        public void WriteInterleaved(float[] buffer, int offset, int frames, float scale)
        {
            started = true;
            long w = written, end = Length;
            for (int i = 0; i < frames && w < end; i++)
            {
                if (skip > 0)
                {
                    skip--;
                    continue;
                }
                Put(w++, buffer[offset + 2 * i] * scale, buffer[offset + 2 * i + 1] * scale);
            }
            Volatile.Write(ref written, w);
        }

        /// <summary>Audio thread: silent frames (no input this block), keeping time.</summary>
        public void WriteSilence(int frames)
        {
            started = true;
            long w = written, end = Length;
            for (int i = 0; i < frames && w < end; i++)
            {
                if (skip > 0)
                {
                    skip--;
                    continue;
                }
                Put(w++, 0, 0);
            }
            Volatile.Write(ref written, w);
        }

        void Put(long frame, float l, float r)
        {
            int c = (int)(frame / ChunkFrames);
            var chunk = c < chunks.Length ? Volatile.Read(ref chunks[c]) : null;
            if (chunk == null)
            {
                Volatile.Write(ref dropped, dropped + 1);
                return;
            }
            int i = (int)(frame % ChunkFrames) * 2;
            chunk[i] = l;
            chunk[i + 1] = r;
        }

        /// <summary>UI thread: the first <paramref name="frames"/> frames as two channels (silence where none was kept).</summary>
        public (float[] Left, float[] Right) Collect(long frames)
        {
            int n = (int)Math.Clamp(frames, 0, Math.Min(capacity, int.MaxValue));
            var left = new float[n];
            var right = new float[n];
            long kept = Math.Min(n, Written);
            for (long f = 0; f < kept; f++)
            {
                var chunk = Volatile.Read(ref chunks[(int)(f / ChunkFrames)]);
                if (chunk == null) continue;
                int i = (int)(f % ChunkFrames) * 2;
                left[f] = chunk[i];
                right[f] = chunk[i + 1];
            }
            return (left, right);
        }
    }
}
