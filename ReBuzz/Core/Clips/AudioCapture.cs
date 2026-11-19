#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 1.0): audio being recorded into a take,
// written on the audio thread into chunks the UI thread makes ahead of it. Pure
// (ReBuzz.SessionTests links this file); the audio thread never allocates or waits.

using System;
using System.Collections.Concurrent;
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
        public const int PeakShift = 10;
        public const int PeakFrames = 1 << PeakShift;

        readonly float[][] chunks;          // interleaved left, right
        readonly long capacity;
        long skip;
        long written;
        // A peak per PeakFrames frames of what was kept, for drawing the take while it grows (the audio thread keeps them; nothing is allocated).
        readonly float[] peaks;
        long length;
        long dropped;
        bool started;

        // Temp-file mode (docs/specs/neo-audio.md "Takes and comping"): a writer thread makes the chunks ahead of the audio thread and writes the full
        // ones to the take's file, giving them back, so a long take is a few chunks in RAM. The audio thread's side is the same lock-free chunk write.
        TakeFile file;
        Thread writer;
        volatile bool writerStop;
        readonly ConcurrentStack<float[]> pool = new ConcurrentStack<float[]>();
        int flushedChunks;
        int resident;
        int peakResident;
        bool completed;
        readonly object completeGate = new object();

        /// <param name="lengthFrames">Frames to keep; long.MaxValue until stopped.</param>
        /// <param name="maxFrames">The most it keeps, until stopped.</param>
        /// <param name="framesPerBeat">Frames a beat of the take lasts: where <see cref="StartAt"/> starts.</param>
        /// <param name="spill">The take's temp file: the audio goes there as it records instead of staying in RAM (null: it stays).</param>
        public AudioCapture(int sampleRate, long skipFrames, long lengthFrames, long maxFrames, double framesPerBeat = 0, TakeFile spill = null)
        {
            SampleRate = Math.Max(1, sampleRate);
            FramesPerBeat = Math.Max(0, framesPerBeat);
            skip = Math.Max(0, skipFrames);
            capacity = Math.Max(1, lengthFrames == long.MaxValue ? maxFrames : Math.Min(lengthFrames, Math.Max(1, maxFrames)));
            length = lengthFrames == long.MaxValue ? long.MaxValue : Math.Clamp(lengthFrames, 0, capacity);
            chunks = new float[(int)((capacity + ChunkFrames - 1) / ChunkFrames)][];
            peaks = new float[(int)((capacity >> PeakShift) + 2)];
            file = spill;
            if (file != null)
            {
                PrepareChunks(AheadFrames);
                writer = new Thread(WriterLoop) { IsBackground = true, Name = "Take writer", Priority = ThreadPriority.AboveNormal };
                writer.Start();
            }
            else
                Prepare(lengthFrames == long.MaxValue ? 8L * SampleRate : capacity);
        }

        /// <summary>Seconds of chunks the writer thread keeps ready ahead of the audio thread (tests that render far faster than real time raise it).</summary>
        public static int SpillAheadSeconds = 3;

        long AheadFrames => (long)Math.Max(1, SpillAheadSeconds) * SampleRate;

        /// <summary>The take's temp file, while it records into one.</summary>
        public TakeFile File => file;

        /// <summary>Chunks in memory now (the most is <see cref="PeakResidentChunks"/>): bounded when the take goes to a file.</summary>
        public int ResidentChunks => Volatile.Read(ref resident);

        public int PeakResidentChunks => Volatile.Read(ref peakResident);

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
            if (file != null) return;            // the writer thread makes them (two makers could replace a chunk the audio thread is writing)
            PrepareChunks(framesAhead);
        }

        void PrepareChunks(long framesAhead)
        {
            long upTo = Math.Min(capacity, Written + Math.Max(0, framesAhead));
            int first = file != null ? flushedChunks : 0;
            for (int c = first; (long)c * ChunkFrames < upTo && c < chunks.Length; c++)
                if (Volatile.Read(ref chunks[c]) == null)
                {
                    if (!pool.TryPop(out var chunk)) chunk = new float[ChunkFrames * 2];
                    Volatile.Write(ref chunks[c], chunk);
                    int now = Interlocked.Increment(ref resident);
                    if (now > peakResident) Volatile.Write(ref peakResident, now);
                }
        }

        void WriterLoop()
        {
            while (!writerStop)
            {
                try { Service(); }
                catch (Exception ex) when (ex is System.IO.IOException || ex is ObjectDisposedException || ex is UnauthorizedAccessException) { }
                Thread.Sleep(20);
            }
        }

        /// <summary>Writer thread: chunks ahead, full chunks to the file.</summary>
        void Service()
        {
            lock (completeGate)
            {
                if (completed) return;
                PrepareChunks(AheadFrames);
                FlushChunks(false, 0);
            }
        }

        /// <summary>Writes the chunks that are full (or, <paramref name="all"/>, every frame up to <paramref name="frames"/>) and gives them back.</summary>
        void FlushChunks(bool all, long frames)
        {
            bool wrote = false;
            while (flushedChunks < chunks.Length)
            {
                int c = flushedChunks;
                long start = (long)c * ChunkFrames;
                long end = all ? Math.Min(start + ChunkFrames, frames) : start + ChunkFrames;
                if (all ? start >= frames : Written < end) break;
                var chunk = Volatile.Read(ref chunks[c]);
                int count = (int)(end - start);
                if (chunk == null) file.AppendSilence(count);
                else file.Append(chunk, count);
                wrote = true;
                if (chunk != null)
                {
                    Volatile.Write(ref chunks[c], null);
                    Interlocked.Decrement(ref resident);
                    Array.Clear(chunk, 0, chunk.Length);       // a recycled chunk starts as silence (frames never written stay silent)
                    pool.Push(chunk);
                }
                flushedChunks++;
                if (!all && end < start + ChunkFrames) break;
            }
            if (wrote) file.Flush();
        }

        /// <summary>
        /// UI thread: the take is over (<see cref="Length"/> is known, or <paramref name="frames"/> are what it keeps): stops the writer
        /// and puts every frame in the file. The audio thread must be done with it.
        /// </summary>
        public void Complete(long frames)
        {
            if (file == null) return;
            lock (completeGate)
            {
                if (completed) return;
                completed = true;
            }
            writerStop = true;
            writer?.Join(2000);
            long keep = Math.Clamp(frames, 0, Math.Min(capacity, Written));
            lock (completeGate)
                FlushChunks(true, keep);
            // Frames the take keeps beyond what came in (a fixed length stopped early) are silence in the file too.
            long want = Math.Clamp(frames, 0, capacity);
            if (file.Frames < want && want < long.MaxValue / 8) file.AppendSilence((int)Math.Min(int.MaxValue, want - file.Frames));
            file.Flush();
        }

        /// <summary>The take is committed (or abandoned): its temp file goes (<paramref name="keepFile"/>: stays for recovery) and the writer stops.</summary>
        public void Close(bool keepFile = false)
        {
            writerStop = true;
            writer?.Join(2000);
            lock (completeGate) completed = true;
            var f = file;
            file = null;
            f?.Close(!keepFile);
            while (pool.TryPop(out _)) { }
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
            chunk[i] = float.IsFinite(l) ? l : 0f;       // an input's NaN or infinity must not reach the take's clip
            chunk[i + 1] = float.IsFinite(r) ? r : 0f;
            float m = Math.Max(Math.Abs(chunk[i]), Math.Abs(chunk[i + 1]));
            int b = (int)(frame >> PeakShift);
            if (b < peaks.Length && m > peaks[b]) peaks[b] = m;
        }

        /// <summary>The loudest frame of each <see cref="PeakFrames"/> frames kept so far (the last one still growing).</summary>
        public int PeakCount => (int)Math.Min(peaks.Length, (Written >> PeakShift) + 1);

        public float PeakAt(int index) => index >= 0 && index < peaks.Length ? Volatile.Read(ref peaks[index]) : 0f;

        /// <summary>UI thread: the first <paramref name="frames"/> frames as two channels (silence where none was kept).</summary>
        public (float[] Left, float[] Right) Collect(long frames)
        {
            if (file != null)
            {
                long keep = Math.Min(frames, Math.Min(capacity, int.MaxValue));
                Complete(keep);
                return file.ReadAll(keep);
            }
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
