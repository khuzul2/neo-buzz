#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md "Takes and comping"): the temp file a take is written to while it records.
//
// A take's audio goes from the audio thread into chunks (AudioCapture), and a writer thread writes the full chunks here (stereo
// float32 WAV in the app's take folder) and gives them back, so a long take never sits in RAM. The header is brought up to date
// at every flush, so a file a crash left behind is a valid WAV of what was flushed; a small JSON beside it keeps the take's name
// and tempo for "Recover takes". Pure (no dependency on the rest of ReBuzz): ReBuzz.SessionTests links this file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ReBuzz.Core.Clips
{
    /// <summary>What a take left in the take folder: a recoverable recording.</summary>
    internal sealed record TakeLeftover(string Path, string Name, double Bpm, int SampleRate, long Frames, DateTime Started);

    internal sealed class TakeFile : IDisposable
    {
        const int HeaderBytes = 44;
        const string Extension = ".nbtake.wav";

        static readonly object gate = new object();
        static readonly HashSet<string> active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string directory;

        /// <summary>The take folder (tests point it at a folder of their own).</summary>
        public static string Directory
        {
            get => directory ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "NeoBuzz", "Takes");
            set => directory = value;
        }

        FileStream stream;
        readonly byte[] scratch = new byte[1 << 16];
        long frames;

        TakeFile(string path, int sampleRate)
        {
            Path = path;
            SampleRate = sampleRate;
        }

        public string Path { get; }
        public int SampleRate { get; }
        public string MetaPath => Path + ".json";

        /// <summary>Frames written to the file so far.</summary>
        public long Frames => frames;

        /// <summary>Opens a new take file; null when the folder can't be written (the take then stays in memory).</summary>
        public static TakeFile Create(string name, int sampleRate, double bpm)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string path = System.IO.Path.Combine(Directory, "take-" + Guid.NewGuid().ToString("N") + Extension);
                var file = new TakeFile(path, Math.Max(1, sampleRate));
                file.stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
                file.WriteHeader(0);
                File.WriteAllText(file.MetaPath, JsonSerializer.Serialize(new Meta { Name = name, Bpm = bpm, SampleRate = file.SampleRate, Pid = Environment.ProcessId, Started = DateTime.UtcNow }));
                lock (gate) active.Add(path);
                return file;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        sealed class Meta
        {
            public string Name { get; set; }
            public double Bpm { get; set; }
            public int SampleRate { get; set; }
            public int Pid { get; set; }
            public DateTime Started { get; set; }
        }

        /// <summary>Writer thread: appends interleaved stereo frames.</summary>
        public void Append(float[] interleaved, int frameCount)
        {
            if (stream == null || frameCount <= 0) return;
            int bytesPerFrame = 8;
            int at = 0;
            while (at < frameCount)
            {
                int n = Math.Min(frameCount - at, scratch.Length / bytesPerFrame);
                Buffer.BlockCopy(interleaved, at * bytesPerFrame, scratch, 0, n * bytesPerFrame);
                stream.Write(scratch, 0, n * bytesPerFrame);
                at += n;
            }
            frames += frameCount;
        }

        /// <summary>Writer thread: silent frames.</summary>
        public void AppendSilence(int frameCount)
        {
            if (stream == null || frameCount <= 0) return;
            Array.Clear(scratch, 0, scratch.Length);
            long left = (long)frameCount * 8;
            while (left > 0)
            {
                int n = (int)Math.Min(left, scratch.Length);
                stream.Write(scratch, 0, n);
                left -= n;
            }
            frames += frameCount;
        }

        /// <summary>Brings the header up to date and puts the written frames on disk, so a crash leaves a valid file.</summary>
        public void Flush()
        {
            if (stream == null) return;
            long end = stream.Position;
            WriteHeader(frames);
            stream.Position = end;
            stream.Flush();
        }

        void WriteHeader(long frameCount)
        {
            long data = frameCount * 8;
            stream.Position = 0;
            using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write((uint)Math.Min(uint.MaxValue, 36 + data));
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16u);
            w.Write((ushort)3);                       // IEEE float
            w.Write((ushort)2);
            w.Write((uint)SampleRate);
            w.Write((uint)(SampleRate * 8));
            w.Write((ushort)8);
            w.Write((ushort)32);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write((uint)Math.Min(uint.MaxValue, data));
            w.Flush();
        }

        /// <summary>Reads the first <paramref name="count"/> frames back as two channels (what the file holds, then silence).</summary>
        public (float[] Left, float[] Right) ReadAll(long count)
        {
            Flush();
            int n = (int)Math.Clamp(count, 0, int.MaxValue);
            var left = new float[n];
            var right = new float[n];
            long keep = Math.Min(n, frames);
            long end = stream.Position;
            stream.Position = HeaderBytes;
            var block = new float[2 * 8192];
            var bytes = new byte[block.Length * 4];
            long done = 0;
            while (done < keep)
            {
                int want = (int)Math.Min(8192, keep - done);
                int read = ReadFully(stream, bytes, want * 8);
                int got = read / 8;
                if (got == 0) break;
                Buffer.BlockCopy(bytes, 0, block, 0, got * 8);
                for (int i = 0; i < got; i++)
                {
                    left[done + i] = block[2 * i];
                    right[done + i] = block[2 * i + 1];
                }
                done += got;
            }
            stream.Position = end;
            return (left, right);
        }

        static int ReadFully(Stream s, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        /// <summary>Closes the file; the take is committed (<paramref name="delete"/>) or kept for recovery.</summary>
        public void Close(bool delete)
        {
            var s = stream;
            stream = null;
            if (s != null)
            {
                try
                {
                    if (!delete)
                    {
                        WriteHeader(frames);
                        s.Flush();
                    }
                }
                finally { s.Dispose(); }
            }
            lock (gate) active.Remove(Path);
            if (delete) Delete(Path);
        }

        public void Dispose() => Close(false);

        static void Delete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { File.Delete(path + ".json"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ------------------------------------------------------------------ recovery

        /// <summary>The takes a crash left in the take folder (not this run's, not another running instance's).</summary>
        public static List<TakeLeftover> Leftovers()
        {
            var found = new List<TakeLeftover>();
            if (!System.IO.Directory.Exists(Directory)) return found;
            HashSet<string> mine;
            lock (gate) mine = new HashSet<string>(active, StringComparer.OrdinalIgnoreCase);
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "take-*" + Extension).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (mine.Contains(path)) continue;
                Meta meta = null;
                try { meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(path + ".json")); } catch (Exception ex) when (ex is IOException || ex is JsonException) { }
                if (meta != null && meta.Pid != Environment.ProcessId && IsRunning(meta.Pid)) continue;
                try
                {
                    var info = Inspect(path, out int rate, out long frameCount);
                    if (!info || frameCount <= 0) { Delete(path); continue; }
                    found.Add(new TakeLeftover(path, meta?.Name ?? System.IO.Path.GetFileNameWithoutExtension(path), meta?.Bpm ?? 0, meta?.SampleRate ?? rate, frameCount, meta?.Started ?? File.GetCreationTimeUtc(path)));
                }
                catch (IOException) { }
            }
            return found;
        }

        static bool IsRunning(int pid)
        {
            try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        static bool Inspect(string path, out int rate, out long frameCount)
        {
            rate = 0;
            frameCount = 0;
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (s.Length < HeaderBytes) return false;
            var h = new byte[HeaderBytes];
            if (ReadFully(s, h, HeaderBytes) < HeaderBytes) return false;
            if (Encoding.ASCII.GetString(h, 0, 4) != "RIFF" || Encoding.ASCII.GetString(h, 36, 4) != "data") return false;
            rate = (int)BitConverter.ToUInt32(h, 24);
            long declared = BitConverter.ToUInt32(h, 40);
            // The file may hold more than the last header update said (a crash between flushes): the whole frames present count.
            frameCount = Math.Max(declared, s.Length - HeaderBytes) / 8;
            return rate > 0;
        }

        /// <summary>Reads a leftover take's audio (two channels).</summary>
        public static (float[] Left, float[] Right) Read(TakeLeftover take)
        {
            using var s = new FileStream(take.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            s.Position = HeaderBytes;
            int n = (int)Math.Min(int.MaxValue, take.Frames);
            var left = new float[n];
            var right = new float[n];
            var bytes = new byte[8192 * 8];
            var block = new float[8192 * 2];
            long done = 0;
            while (done < n)
            {
                int want = (int)Math.Min(8192, n - done);
                int got = ReadFully(s, bytes, want * 8) / 8;
                if (got == 0) break;
                Buffer.BlockCopy(bytes, 0, block, 0, got * 8);
                for (int i = 0; i < got; i++)
                {
                    left[done + i] = float.IsFinite(block[2 * i]) ? block[2 * i] : 0f;
                    right[done + i] = float.IsFinite(block[2 * i + 1]) ? block[2 * i + 1] : 0f;
                }
                done += got;
            }
            return (left, right);
        }

        /// <summary>Takes a recovered (or dismissed) take out of the folder.</summary>
        public static void Discard(TakeLeftover take) => Delete(take.Path);
    }
}
