// Fork (docs/specs/ux-overhaul.md P6b, §9): the Hard Disk Recorder's engine, after BuzzGUI.MachineView's HDRecorderWindow: it listens to
// the Master's tap and writes what it hears to a wave file as it plays, in real time, in 16, 24 or 32 bit integer or 32 bit float.
// "Record" starts at once; "Record loop" waits for the loop's first tick and stops by itself after the loop's last (the song is
// started for it). The tap's thread only copies the block into a ring made up front (no allocation, no lock; a full ring drops the
// block and counts it); a worker thread converts and writes it, so a slow disk never blocks the audio. The clean one-shot offline
// render is File ▸ Export Audio, which this does not duplicate.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Studio.Shell.Tools
{
    internal enum WavFormat { Pcm16, Pcm24, Pcm32, Float32 }

    /// <summary>A wave file written block by block; the header's sizes are filled in when it closes.</summary>
    internal sealed class WavStreamWriter : IDisposable
    {
        readonly FileStream file;
        readonly WavFormat format;
        readonly int channels;
        readonly byte[] scratch = new byte[16384 * 4];
        long dataBytes;

        public WavStreamWriter(string path, int rate, int channels, WavFormat format)
        {
            this.format = format;
            this.channels = channels;
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteHeader(rate);
        }

        public int BytesPerSample => format switch { WavFormat.Pcm16 => 2, WavFormat.Pcm24 => 3, _ => 4 };
        public long DataBytes => Volatile.Read(ref dataBytes);

        void WriteHeader(int rate)
        {
            using var w = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true);
            int bytes = BytesPerSample;
            w.Write("RIFF"u8.ToArray()); w.Write(0);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16);
            w.Write((short)(format == WavFormat.Float32 ? 3 : 1)); w.Write((short)channels);
            w.Write(rate); w.Write(rate * channels * bytes); w.Write((short)(channels * bytes)); w.Write((short)(bytes * 8));
            w.Write("data"u8.ToArray()); w.Write(0);
        }

        /// <summary>Writes <paramref name="count"/> samples (fractions of full scale; interleaved), clipped to full scale for the integer formats.</summary>
        public void Write(float[] samples, int offset, int count)
        {
            int per = BytesPerSample;
            while (count > 0)
            {
                int n = Math.Min(count, scratch.Length / per);
                int p = 0;
                for (int i = 0; i < n; i++)
                {
                    float v = samples[offset + i];
                    switch (format)
                    {
                        case WavFormat.Float32:
                            BitConverter.TryWriteBytes(scratch.AsSpan(p), v); p += 4; break;
                        case WavFormat.Pcm16:
                            BitConverter.TryWriteBytes(scratch.AsSpan(p), (short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f)); p += 2; break;
                        case WavFormat.Pcm24:
                            int s24 = (int)Math.Round(Math.Clamp(v, -1f, 1f) * 8388607f);
                            scratch[p++] = (byte)s24; scratch[p++] = (byte)(s24 >> 8); scratch[p++] = (byte)(s24 >> 16); break;
                        default:
                            BitConverter.TryWriteBytes(scratch.AsSpan(p), (int)Math.Round(Math.Clamp(v, -1f, 1f) * 2147483647.0)); p += 4; break;
                    }
                }
                file.Write(scratch, 0, p);
                Interlocked.Add(ref dataBytes, p);
                offset += n;
                count -= n;
            }
        }

        public void Dispose()
        {
            if (!file.CanWrite) return;
            file.Position = 4;
            using (var w = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                w.Write((int)Math.Min(int.MaxValue, 36 + dataBytes));
                file.Position = 40;
                w.Write((int)Math.Min(int.MaxValue, dataBytes));
            }
            file.Dispose();
        }
    }

    internal sealed class MasterRecorder : IDisposable
    {
        public enum State { Idle, WaitingForLoop, Recording, Stopped }

        const int RingSamples = 1 << 21;      // 2M samples: about 20 s of stereo at 48 kHz

        readonly ReBuzzCore core;
        readonly float[] ring = new float[RingSamples];
        readonly bool loop;
        WavStreamWriter writer;
        Thread worker;
        long head, tail;                      // written by the tap, read by the worker
        volatile bool done;
        int dropped;
        int state;
        int started;
        double progress;
        bool listening;

        public MasterRecorder(ReBuzzCore core, bool loop)
        {
            this.core = core;
            this.loop = loop;
        }

        public string Path { get; private set; }
        public State Now => (State)Volatile.Read(ref state);
        public bool Active => Now == State.WaitingForLoop || Now == State.Recording;

        /// <summary>Blocks the ring had no room for (the disk can't keep up).</summary>
        public int Dropped => Volatile.Read(ref dropped);

        /// <summary>Frames recorded so far.</summary>
        public long Frames => Volatile.Read(ref head) / 2;

        public long Bytes => writer?.DataBytes ?? 0;

        /// <summary>0 to 1 across the loop while a loop records.</summary>
        public double Progress => Volatile.Read(ref progress);

        public TimeSpan Elapsed => TimeSpan.FromSeconds((double)Frames / Math.Max(1, core.SelectedAudioDriverSampleRate));

        /// <summary>Opens the file and starts listening. A loop recording starts the song at the loop's start.</summary>
        public void Start(string path, WavFormat format)
        {
            Path = path;
            writer = new WavStreamWriter(path, core.SelectedAudioDriverSampleRate, 2, format);
            worker = new Thread(Drain) { IsBackground = true, Name = "Hard Disk Recorder" };
            worker.Start();
            Volatile.Write(ref state, (int)(loop ? State.WaitingForLoop : State.Recording));
            listening = true;
            core.MasterTap += OnTap;
            if (loop)
            {
                core.Playing = false;
                core.Song.PlayPosition = core.Song.LoopStart;
                core.Playing = true;
            }
        }

        /// <summary>The tap's thread: copies the block (scaled to fractions of full scale) into the ring. Allocates nothing.</summary>
        internal void OnTap(float[] samples, bool stereo, SongTime time)
        {
            if (!stereo || Now == State.Stopped || Now == State.Idle) return;
            if (Now == State.WaitingForLoop)
            {
                // The loop's first tick: this block (it starts on the tick, as the song plays from the loop's start).
                if (time.CurrentTick < core.Song.LoopStart || time.CurrentTick >= core.Song.LoopEnd) return;
                Volatile.Write(ref state, (int)State.Recording);
            }
            if (loop && time.CurrentTick >= core.Song.LoopEnd)
            {
                Volatile.Write(ref state, (int)State.Stopped);     // the UI thread sees the end and finishes (Finish)
                return;
            }
            if (loop)
            {
                int length = Math.Max(1, core.Song.LoopEnd - core.Song.LoopStart);
                Volatile.Write(ref progress, Math.Clamp((time.CurrentTick - core.Song.LoopStart) / (double)length, 0, 1));
            }
            int n = samples.Length;
            long h = Volatile.Read(ref head);
            if (h + n - Volatile.Read(ref tail) > RingSamples)
            {
                Interlocked.Increment(ref dropped);
                return;
            }
            const float k = 1f / SignalTap.FullScale;
            for (int i = 0; i < n; i++) ring[(int)((h + i) & (RingSamples - 1))] = samples[i] * k;
            Volatile.Write(ref head, h + n);
        }

        void Drain()
        {
            var chunk = new float[16384];
            while (true)
            {
                long t = Volatile.Read(ref tail), h = Volatile.Read(ref head);
                int n = (int)Math.Min(chunk.Length, h - t);
                if (n <= 0)
                {
                    if (done) break;
                    Thread.Sleep(2);
                    continue;
                }
                for (int i = 0; i < n; i++) chunk[i] = ring[(int)((t + i) & (RingSamples - 1))];
                writer.Write(chunk, 0, n);
                Volatile.Write(ref tail, t + n);
            }
        }

        /// <summary>True once a loop recording has passed the loop's end (the window then calls <see cref="Stop"/>).</summary>
        public bool LoopFinished => loop && Now == State.Stopped && !done;

        /// <summary>Stops listening, lets the worker write what is left and closes the file. The song stops for a loop recording.</summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref started, 1) == 1) return;
            if (listening) core.MasterTap -= OnTap;
            listening = false;
            Volatile.Write(ref state, (int)State.Stopped);
            if (loop && core.Playing) core.Playing = false;
            done = true;
            worker?.Join();
            writer?.Dispose();
        }

        public void Dispose() => Stop();

        /// <summary>The next file name that doesn't exist: <paramref name="path"/> itself, else "name-0001.wav" and on.</summary>
        public static string NextFree(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = System.IO.Path.GetDirectoryName(path) ?? "", name = System.IO.Path.GetFileNameWithoutExtension(path), ext = System.IO.Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string next = System.IO.Path.Combine(dir, $"{name}-{i:D4}{ext}");
                if (!File.Exists(next)) return next;
            }
        }
    }
}
