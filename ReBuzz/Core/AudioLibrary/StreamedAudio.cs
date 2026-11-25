#nullable disable
// Fork (docs/specs/neo-audio.md, NA1b): audio read from a cache file through a prefetch thread. The audio thread only looks pages up in a table
// (a page is a few thousand frames of both channels, already in memory): no I/O, no locks, no allocation. A page that is not there plays as
// silence and is counted (an underrun); the voice goes on. The prefetch thread (one for the process, low priority) reads the pages the voices
// say they will need (a window ahead of each voice, published lock free), the pinned ones (the start of every asset, the starts of the loops and
// the warp markers clips use) and evicts what has not been wanted for longest when the memory budget is spent.
// Pure: ReBuzz.SessionTests links this file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ReBuzz.Core.AudioLibrary
{
    /// <summary>A cache file open for reading: its pages as the audio thread sees them, and the reads that fill them.</summary>
    internal sealed class StreamedAudio : IDisposable
    {
        public const int PageFrames = AudioCache.PageFrames;
        const int PageShift = AudioCache.PageShift;
        const int PageMask = AudioCache.PageMask;
        const int PageBytes = PageFrames * 4;

        /// <summary>A page: both channels' pieces, one after the other (a mono page has one).</summary>
        internal sealed class Page
        {
            public readonly float[] Data;
            public long Wanted;                 // prefetch thread: the tick it was last wanted at
            public long WantedMs;               // and when (a page is not evicted for a moment after: tails and crossfades read where a voice has just left)
            public long Freed;                  // prefetch thread: when it was evicted
            public Page(int floats) { Data = new float[floats]; }
        }

        readonly Page[] table;
        SafeFileHandle handle;
        uint[] crcs;
        long dataOffset;
        readonly object fileGate = new object();
        readonly string path;
        int disposed;
        long missed;
        long underruns;
        long residentPages;
        long loaded;
        volatile bool damaged;
        string damage;

        // Pins (UI thread writes; the prefetch thread reads a copy): ranges of frames that stay resident, by owner.
        readonly object pinGate = new object();
        readonly Dictionary<object, (long From, long To)[]> pins = new Dictionary<object, (long, long)[]>(ReferenceEqualityComparer.Instance);
        int pinVersion;
        int pinsSeen = -1;
        bool[] pinned;                          // prefetch thread

        // Diagnostics (RB1): when each page was last read in and last evicted, and the first few reads that missed, written where they happen with no
        // allocation (the audio thread notes a miss), so that an underrun a test or a user reports says whether the page was never asked for, was
        // evicted, or was asked for and late.
        readonly long[] loadedAtMs, evictedAtMs;
        readonly MissRecord[] missLog = new MissRecord[8];
        int missRecords;

        /// <summary>One read that found its page not in memory (see <see cref="MissReport"/>).</summary>
        internal struct MissRecord
        {
            public long AtMs, Frame, LoadedMs, EvictedMs, Pass, PassStartedMs;
            public int Page, Count, Resident;
            public bool Pinned;
        }

        // Prefetch thread state.
        internal long Settled;                  // ticks in a row in which every wanted page was resident
        internal long SettledSerial;
        internal long HintsSeen;                // hints set for this stream by voices (a voice publishes only once the stream is attached: PL1)
        internal bool Wants;

        StreamedAudio(string id, string path, SafeFileHandle handle, int channels, int rate, long frames, uint[] crcs, long dataOffset)
        {
            Id = id;
            this.path = path;
            this.handle = handle;
            Channels = channels;
            SampleRate = rate;
            Frames = frames;
            this.crcs = crcs;
            this.dataOffset = dataOffset;
            PageCount = AudioCache.PagesOf(frames);
            table = new Page[Math.Max(1, PageCount)];
            loadedAtMs = new long[table.Length];
            evictedAtMs = new long[table.Length];
            PinHead(StreamingPolicy.HeadMilliseconds);
        }

        public string Id { get; }
        public string Path => path;
        public int Channels { get; }
        public int SampleRate { get; }
        public long Frames { get; }
        public int PageCount { get; }

        /// <summary>Samples read that were not in memory (each is silence); the audio thread adds to it.</summary>
        public long MissedSamples => Interlocked.Read(ref missed);

        /// <summary>Blocks in which a voice missed samples (the voices count them, <see cref="NoteUnderrun"/>).</summary>
        public long Underruns => Interlocked.Read(ref underruns);

        /// <summary>Bytes of pages held in memory.</summary>
        public long ResidentBytes => Interlocked.Read(ref residentPages) * (long)PageBytes * Channels;

        public long PagesLoaded => Interlocked.Read(ref loaded);

        /// <summary>The file is gone, cut short or fails its checks: it is to be written again.</summary>
        public bool Damaged => damaged;
        public string DamageReason => damage;

        /// <summary>Raised on the prefetch thread when the file is found damaged or missing (once).</summary>
        public event Action<StreamedAudio> DamageFound;

        public void NoteUnderrun()
        {
            Interlocked.Increment(ref underruns);
            Interlocked.Increment(ref StreamStats.UnderrunBlocks);
        }

        /// <summary>Audio thread, only on a miss: notes the first few (no allocation, no lock).</summary>
        void NoteMiss(int index, long frame, int count)
        {
            int n = Interlocked.Increment(ref missRecords) - 1;
            if (n >= missLog.Length) return;
            var pins = pinned;
            missLog[n] = new MissRecord
            {
                AtMs = Environment.TickCount64, Frame = frame, Count = count, Page = index,
                LoadedMs = Volatile.Read(ref loadedAtMs[index]), EvictedMs = Volatile.Read(ref evictedAtMs[index]),
                Pass = StreamPrefetcher.Pass, PassStartedMs = Volatile.Read(ref StreamPrefetcher.PassStartedMs),
                Pinned = pins != null && index < pins.Length && pins[index], Resident = (int)Interlocked.Read(ref residentPages),
            };
        }

        /// <summary>Tests and diagnostics: the first reads that missed, one line each: where, and what the prefetch thread had done with that page.</summary>
        public string MissReport()
        {
            int n = Math.Min(Volatile.Read(ref missRecords), missLog.Length);
            if (n == 0) return "no misses";
            var sb = new StringBuilder();
            sb.Append(Volatile.Read(ref missRecords)).Append(" read(s) missed, the first ").Append(n).Append(" (ms are TickCount64, ago = before the miss):");
            for (int i = 0; i < n; i++)
            {
                var m = missLog[i];
                sb.Append("\n  frame ").Append(m.Frame).Append(" x").Append(m.Count).Append(" in page ").Append(m.Page).Append('/').Append(PageCount)
                  .Append(m.Pinned ? " (pinned)" : " (not pinned)")
                  .Append(m.LoadedMs == 0 ? ", never loaded" : ", loaded " + (m.AtMs - m.LoadedMs) + " ms ago")
                  .Append(m.EvictedMs == 0 ? ", never evicted" : ", evicted " + (m.AtMs - m.EvictedMs) + " ms ago")
                  .Append("; prefetch pass #").Append(m.Pass).Append(" began ").Append(m.AtMs - m.PassStartedMs).Append(" ms ago; ").Append(m.Resident).Append(" pages resident");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ opening

        /// <summary>Opens the cache file of this audio; null when it is missing or not a whole, matching file.</summary>
        public static StreamedAudio Open(string id, long frames, int channels, int rate, bool closeAtOnce = false)
        {
            string path = AudioCache.PathFor(id);
            var s = TryOpen(path, id, frames, channels, rate);
            if (s == null) return null;
            if (closeAtOnce)
            {
                s.Dispose();
                return null;
            }
            AudioCache.Touch(path);
            StreamPrefetcher.Add(s);
            return s;
        }

        /// <summary>Whether a whole file for this audio is in the cache.</summary>
        public static bool IsWhole(string id, long frames, int channels, int rate)
        {
            var s = TryOpen(AudioCache.PathFor(id), id, frames, channels, rate);
            if (s == null) return false;
            s.Dispose();
            return true;
        }

        static StreamedAudio TryOpen(string path, string id, long frames, int channels, int rate)
        {
            SafeFileHandle h = null;
            try
            {
                if (!File.Exists(path)) return null;
                h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
                long length = RandomAccess.GetLength(h);
                if (length < AudioCache.HeaderBytes) { h.Dispose(); return null; }
                var head = new byte[AudioCache.HeaderBytes];
                if (RandomAccess.Read(h, head, 0) != head.Length) { h.Dispose(); return null; }
                if (!head.AsSpan(0, 8).SequenceEqual(AudioCache.Magic)) { h.Dispose(); return null; }
                if (BitConverter.ToUInt32(head, 84) != Crc32.Of(head.AsSpan(0, 84))) { h.Dispose(); return null; }
                if (BitConverter.ToUInt32(head, 8) != 1u) { h.Dispose(); return null; }
                int ch = (int)BitConverter.ToUInt32(head, 12), rt = (int)BitConverter.ToUInt32(head, 16);
                long fr = (long)BitConverter.ToUInt64(head, 24);
                int pages = (int)BitConverter.ToUInt32(head, 32);
                long dataOffset = (long)BitConverter.ToUInt64(head, 72);
                string fid = Encoding.ASCII.GetString(head, 40, 32).TrimEnd('\0');
                if (BitConverter.ToUInt32(head, 20) != PageFrames || ch != channels || rt != rate || fr != frames || pages != AudioCache.PagesOf(frames)) { h.Dispose(); return null; }
                if (!string.Equals(fid, id.Length > 32 ? id.Substring(0, 32) : id, StringComparison.Ordinal)) { h.Dispose(); return null; }
                if (length != dataOffset + (long)pages * ch * PageBytes) { h.Dispose(); return null; }
                var table = new byte[(long)pages * ch * 4];
                if (RandomAccess.Read(h, table, AudioCache.HeaderBytes) != table.Length) { h.Dispose(); return null; }
                if (BitConverter.ToUInt32(head, 80) != Crc32.Of(table)) { h.Dispose(); return null; }
                var crcs = new uint[pages * ch];
                for (int i = 0; i < crcs.Length; i++) crcs[i] = BitConverter.ToUInt32(table, i * 4);
                return new StreamedAudio(id, path, h, ch, rt, fr, crcs, dataOffset);
            }
            catch (Exception)
            {
                h?.Dispose();
                return null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            StreamPrefetcher.Remove(this);
            SafeFileHandle h;
            lock (fileGate) { h = handle; handle = null; }
            try { h?.Dispose(); } catch (Exception) { }
        }

        public bool IsDisposed => disposed != 0;

        // ------------------------------------------------------------------ the audio thread's reads

        /// <summary>One sample (0 when its page is not in memory, which is counted). The caller has checked the range.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Sample(int channel, long frame)
        {
            var page = Volatile.Read(ref table[(int)(frame >> PageShift)]);
            if (page == null)
            {
                Interlocked.Increment(ref missed);
                NoteMiss((int)(frame >> PageShift), frame, 1);
                return 0f;
            }
            int at = (int)(frame & PageMask);
            if (channel != 0 && Channels > 1) at += PageFrames;
            return page.Data[at];
        }

        /// <summary>Copies samples starting at <paramref name="start"/> (silence outside the audio and for pages not in memory, which are counted).</summary>
        public void CopyTo(int channel, long start, Span<float> destination)
        {
            int n = destination.Length, done = 0;
            int chOffset = channel != 0 && Channels > 1 ? PageFrames : 0;
            while (done < n)
            {
                long p = start + done;
                if (p < 0)
                {
                    int z = (int)Math.Min(n - done, -p);
                    destination.Slice(done, z).Clear();
                    done += z;
                    continue;
                }
                if (p >= Frames)
                {
                    destination.Slice(done).Clear();
                    return;
                }
                int index = (int)(p >> PageShift), offset = (int)(p & PageMask);
                int take = (int)Math.Min(Math.Min(n - done, PageFrames - offset), Frames - p);
                var page = Volatile.Read(ref table[index]);
                if (page == null)
                {
                    destination.Slice(done, take).Clear();
                    Interlocked.Add(ref missed, take);
                    NoteMiss(index, p, take);
                }
                else
                    page.Data.AsSpan(chOffset + offset, take).CopyTo(destination.Slice(done, take));
                done += take;
            }
        }

        // ------------------------------------------------------------------ exact reads (not the audio thread)

        /// <summary>Copies samples from the file (the page in memory if there is one); silence outside the audio. Blocks on the disk. Throws if the file is bad.</summary>
        public void ReadExact(int channel, long start, Span<float> destination)
        {
            int n = destination.Length, done = 0;
            if (Channels == 1) channel = 0;
            var piece = new float[PageFrames];
            while (done < n)
            {
                long p = start + done;
                if (p < 0)
                {
                    int z = (int)Math.Min(n - done, -p);
                    destination.Slice(done, z).Clear();
                    done += z;
                    continue;
                }
                if (p >= Frames)
                {
                    destination.Slice(done).Clear();
                    return;
                }
                int index = (int)(p >> PageShift), offset = (int)(p & PageMask);
                int take = (int)Math.Min(Math.Min(n - done, PageFrames - offset), Frames - p);
                var page = Volatile.Read(ref table[index]);
                if (page != null)
                    page.Data.AsSpan((channel != 0 ? PageFrames : 0) + offset, take).CopyTo(destination.Slice(done, take));
                else
                {
                    ReadPiece(index, channel, piece);
                    piece.AsSpan(offset, take).CopyTo(destination.Slice(done, take));
                }
                done += take;
            }
        }

        /// <summary>One piece (page, channel) straight from the file, its CRC checked. Throws <see cref="InvalidDataException"/> when it fails.</summary>
        void ReadPiece(int page, int channel, Span<float> into)
        {
            SafeFileHandle h; uint[] table; long offset;
            lock (fileGate) { h = handle; table = crcs; offset = dataOffset; }
            if (h == null) throw new ObjectDisposedException(nameof(StreamedAudio));
            var bytes = MemoryMarshal.AsBytes(into.Slice(0, PageFrames));
            long at = offset + ((long)page * Channels + channel) * PageBytes;
            ReadFully(h, bytes, at);
            if (Crc32.Of(bytes) != table[page * Channels + channel]) throw new InvalidDataException("A piece of the audio cache fails its checksum.");
        }

        /// <summary>
        /// The file was written again (after it was removed or damaged): reads the new one, drops the pages (they are read again from it) and
        /// clears the damage. False if the new file is not whole. The pins stay.
        /// </summary>
        public bool Reload()
        {
            var fresh = TryOpen(AudioCache.PathFor(Id), Id, Frames, Channels, SampleRate);
            if (fresh == null) return false;
            lock (fileGate)
            {
                handle = fresh.handle;
                crcs = fresh.crcs;
                dataOffset = fresh.dataOffset;
                fresh.handle = null;
            }
            fresh.Dispose();
            for (int i = 0; i < table.Length; i++) if (Evict(i) != null) { }
            damage = null;
            damaged = false;
            AudioCache.Touch(path);
            return true;
        }

        static void ReadFully(SafeFileHandle h, Span<byte> bytes, long at)
        {
            int got = 0;
            while (got < bytes.Length)
            {
                int n = RandomAccess.Read(h, bytes.Slice(got), at + got);
                if (n <= 0) throw new EndOfStreamException("The audio cache file is cut short.");
                got += n;
            }
        }

        // ------------------------------------------------------------------ pins

        /// <summary>The frames of these ranges stay resident for as long as the owner holds them (replaces the owner's earlier ranges).</summary>
        public void SetPins(object owner, IReadOnlyList<(long From, long To)> ranges)
        {
            lock (pinGate)
            {
                if (ranges == null || ranges.Count == 0) pins.Remove(owner);
                else pins[owner] = ranges.Select(r => (Math.Max(0, r.From), Math.Min(Frames, r.To))).Where(r => r.Item2 > r.Item1).ToArray();
                pinVersion++;
            }
            Wants = true;
        }

        void PinHead(int milliseconds)
        {
            // The start of the audio belongs to no clip: the asset itself pins it (owner = the stream).
            long n = (long)(milliseconds / 1000.0 * SampleRate);
            SetPins(this, n > 0 ? new[] { (0L, n) } : null);
        }

        // ------------------------------------------------------------------ the prefetch thread's side

        internal bool PageResident(int index) => Volatile.Read(ref table[index]) != null;

        /// <summary>Prefetch thread: the pages that are pinned (recomputed when the pins change).</summary>
        internal bool[] PinnedPages()
        {
            if (pinsSeen == Volatile.Read(ref pinVersion) && pinned != null) return pinned;
            (long, long)[][] copy;
            int version;
            lock (pinGate)
            {
                copy = pins.Values.ToArray();
                version = pinVersion;
            }
            var set = new bool[PageCount];
            foreach (var list in copy)
                foreach (var (from, to) in list)
                {
                    int a = (int)(from >> PageShift), b = (int)((to - 1) >> PageShift);
                    for (int i = Math.Max(0, a); i <= b && i < PageCount; i++) set[i] = true;
                }
            pinned = set;
            pinsSeen = version;
            return set;
        }

        /// <summary>Prefetch thread: loads a page from the file. False if it could not be (the file is marked damaged when the data is bad).</summary>
        internal bool Load(int index, Func<Page> make, long tick)
        {
            if (index < 0 || index >= PageCount || Volatile.Read(ref table[index]) != null) return true;
            var page = make();
            try
            {
                SafeFileHandle h; uint[] table2; long offset;
                lock (fileGate) { h = handle; table2 = crcs; offset = dataOffset; }
                if (h == null) return false;
                // One read for the page's pieces (they are adjacent), then the checks.
                var bytes = MemoryMarshal.AsBytes(page.Data.AsSpan(0, PageFrames * Channels));
                ReadFully(h, bytes, offset + (long)index * Channels * PageBytes);
                for (int c = 0; c < Channels; c++)
                    if (Crc32.Of(bytes.Slice(c * PageBytes, PageBytes)) != table2[index * Channels + c])
                        throw new InvalidDataException("A piece of the audio cache fails its checksum.");
            }
            catch (Exception e)
            {
                MarkDamaged(e.Message);
                return false;
            }
            page.Wanted = tick;
            page.WantedMs = Environment.TickCount64;
            Volatile.Write(ref loadedAtMs[index], page.WantedMs);
            Volatile.Write(ref table[index], page);
            Interlocked.Increment(ref residentPages);
            Interlocked.Increment(ref loaded);
            return true;
        }

        /// <summary>Prefetch thread: takes a page out of the table (it is handed back for reuse some time later).</summary>
        internal Page Evict(int index)
        {
            var page = table[index];
            if (page == null) return null;
            Volatile.Write(ref evictedAtMs[index], Environment.TickCount64);
            Volatile.Write(ref table[index], null);
            Interlocked.Decrement(ref residentPages);
            return page;
        }

        internal Page PageAt(int index) => Volatile.Read(ref table[index]);

        internal void MarkDamaged(string why)
        {
            if (damaged) return;
            damage = why;
            damaged = true;
            try { DamageFound?.Invoke(this); } catch (Exception) { }
        }

        /// <summary>Prefetch thread, now and then: the file is still there.</summary>
        internal void CheckFile()
        {
            if (damaged || disposed != 0) return;
            try
            {
                // A file deleted while open is still readable on most systems, but the cache has lost it: it is written again.
                if (!File.Exists(path)) MarkDamaged("The cache file was removed.");
            }
            catch (Exception) { }
        }

        /// <summary>Copies the whole audio from this (open) file into a new cache writer: how a removed file is made again from the handle still held.</summary>
        public bool CopyTo(AudioCache.Writer writer)
        {
            try
            {
                var piece = new float[PageFrames];
                for (int p = 0; p < PageCount; p++)
                    for (int c = 0; c < Channels; c++)
                    {
                        ReadPiece(p, c, piece);
                        writer.WritePiece(c, p, piece);
                    }
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Counters the status shows (the agent's status, the dropout button).</summary>
    internal static class StreamStats
    {
        internal static long UnderrunBlocks;

        /// <summary>Blocks in which a streamed clip played silence for audio not read in time, since start or the last reset.</summary>
        public static long Underruns => Interlocked.Read(ref UnderrunBlocks);

        public static void Reset() => Interlocked.Exchange(ref UnderrunBlocks, 0);
    }

    /// <summary>
    /// What a voice tells the prefetch thread: the audio it reads now and the window it will read next, in frames of a stream. Written by the audio
    /// thread, read by the prefetch thread, with no lock (a sequence counter: a read that straddles a write is dropped and read again).
    /// </summary>
    internal sealed class StreamHint
    {
        int seq;
        StreamedAudio stream;
        long pos, aFrom, aTo, bFrom, bTo, stamp;

        /// <summary>Audio thread: now at <paramref name="at"/>; keep [aFrom, aTo) and [bFrom, bTo) (either may be empty) resident.</summary>
        public void Set(StreamedAudio s, long at, long aFrom, long aTo, long bFrom, long bTo)
        {
            Interlocked.Increment(ref seq);
            if (s != null) Interlocked.Increment(ref s.HintsSeen);
            stream = s;
            pos = at;
            this.aFrom = aFrom; this.aTo = aTo;
            this.bFrom = bFrom; this.bTo = bTo;
            stamp = Environment.TickCount64;
            Interlocked.Increment(ref seq);
        }

        /// <summary>Audio thread: the voice no longer needs anything.</summary>
        public void Clear()
        {
            Interlocked.Increment(ref seq);
            stream = null;
            Interlocked.Increment(ref seq);
        }

        public bool TryGet(out StreamedAudio s, out long at, out long af, out long at2, out long bf, out long bt, out long when)
        {
            for (int tries = 0; tries < 4; tries++)
            {
                int a = Volatile.Read(ref seq);
                if ((a & 1) != 0) continue;
                s = stream; at = pos; af = aFrom; at2 = aTo; bf = bFrom; bt = bTo; when = stamp;
                if (Interlocked.CompareExchange(ref seq, a, a) == a) return s != null;
            }
            s = null; at = af = at2 = bf = bt = when = 0;
            return false;
        }
    }

    /// <summary>The one low-priority thread that reads pages for every streamed asset (the clip of the Pro worker, neo-audio.md section 11).</summary>
    internal static class StreamPrefetcher
    {
        static readonly object gate = new object();
        static List<WeakReference<StreamHint>> hintList = new List<WeakReference<StreamHint>>();
        static StreamHint[] hints = Array.Empty<StreamHint>();
        static readonly List<StreamedAudio> streams = new List<StreamedAudio>();
        static StreamedAudio[] streamsSnapshot = Array.Empty<StreamedAudio>();
        static Thread thread;
        static long tick;
        static long lastRefresh, lastFileCheck, lastTrim;
        static readonly Stack<StreamedAudio.Page> pool1 = new Stack<StreamedAudio.Page>(), pool2 = new Stack<StreamedAudio.Page>();
        static readonly Queue<StreamedAudio.Page> graveyard = new Queue<StreamedAudio.Page>();
        static long pagesAllocated;
        /// <summary>Tests: the longest a pass took (milliseconds) and the most pages one pass read.</summary>
        internal static double WorstPassMs;
        internal static int Pass;
        /// <summary>Diagnostics: when the pass that is running (or the last) began (TickCount64).</summary>
        internal static long PassStartedMs;

        /// <summary>Bytes of pages held, those in the tables and those waiting to be reused.</summary>
        public static long ResidentBytes
        {
            get
            {
                long n = 0;
                foreach (var s in streamsSnapshot) n += s.ResidentBytes;
                lock (graveyard) n += graveyard.Count * (long)(StreamedAudio.PageFrames * 4) * 2;
                return n;
            }
        }

        public static StreamHint NewHint()
        {
            var h = new StreamHint();
            lock (gate)
            {
                hintList.RemoveAll(w => !w.TryGetTarget(out _));
                hintList.Add(new WeakReference<StreamHint>(h));
                hints = hintList.Select(w => w.TryGetTarget(out var t) ? t : null).Where(t => t != null).ToArray();
            }
            return h;
        }

        public static void Add(StreamedAudio s)
        {
            lock (gate)
            {
                streams.Add(s);
                streamsSnapshot = streams.ToArray();
                Start();
            }
        }

        public static void Remove(StreamedAudio s)
        {
            lock (gate)
            {
                streams.Remove(s);
                streamsSnapshot = streams.ToArray();
            }
        }

        /// <summary>The ids of the cache files that are open (the open song's).</summary>
        public static HashSet<string> OpenIds()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in streamsSnapshot)
                set.Add(System.IO.Path.GetFileNameWithoutExtension(s.Path));
            return set;
        }

        public static IReadOnlyList<StreamedAudio> OpenStreams => streamsSnapshot;

        static void Start()
        {
            if (thread != null) return;
            thread = new Thread(Run) { IsBackground = true, Name = "Neo Audio prefetch", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        static StreamedAudio.Page Make(int channels)
        {
            lock (graveyard)
            {
                var pool = channels > 1 ? pool2 : pool1;
                // Pages evicted a while ago (no read can still be in one: a block takes milliseconds) are reused, not left to the GC.
                while (graveyard.Count > 0 && Environment.TickCount64 - graveyard.Peek().Freed > 2000)
                {
                    var old = graveyard.Dequeue();
                    (old.Data.Length > StreamedAudio.PageFrames ? pool2 : pool1).Push(old);
                }
                if (pool.Count > 0) return pool.Pop();
            }
            Interlocked.Increment(ref pagesAllocated);
            return new StreamedAudio.Page(StreamedAudio.PageFrames * channels);
        }

        static void Run()
        {
            var ranges = new List<(StreamedAudio S, long Pos, long From, long To)>();
            var sw = new System.Diagnostics.Stopwatch();
            while (true)
            {
                bool did = false;
                try
                {
                    sw.Restart();
                    Volatile.Write(ref PassStartedMs, Environment.TickCount64);
                    did = Pass1(ranges);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    if (ms > WorstPassMs) WorstPassMs = ms;
                    Pass++;
                }
                catch (Exception) { }
                if (!did) Thread.Sleep(2);
            }
        }

        static bool Pass1(List<(StreamedAudio S, long Pos, long From, long To)> ranges)
        {
            long now = Environment.TickCount64;
            tick++;
            if (now - lastRefresh > 250)
            {
                lastRefresh = now;
                lock (gate)
                {
                    hintList.RemoveAll(w => !w.TryGetTarget(out _));
                    hints = hintList.Select(w => w.TryGetTarget(out var t) ? t : null).Where(t => t != null).ToArray();
                }
            }
            var all = streamsSnapshot;
            if (all.Length == 0) return false;

            // What the voices want now.
            ranges.Clear();
            foreach (var hint in hints)
            {
                if (!hint.TryGet(out var s, out long at, out long af, out long at2, out long bf, out long bt, out long when) || now - when > 3000) continue;
                if (s.IsDisposed) continue;
                if (at2 > af) ranges.Add((s, at, af, at2));
                if (bt > bf) ranges.Add((s, at, bf, bt));
            }

            bool did = false;
            int budget = 12;                    // pages per pass: the thread stays responsive to new hints and the file checks
            // The pages the voices' windows want, earliest deadline first across voices and files, then the pins (RB1: see Plan).
            plan.Clear();
            Plan(ranges, budget, plan);
            foreach (var d in plan)
            {
                if (budget <= 0) break;
                did |= Want(d.S, d.Page, ref budget);
            }
            foreach (var s in all)
            {
                if (s.IsDisposed || s.Damaged) continue;
                var pinned = s.PinnedPages();
                for (int p = 0; p < s.PageCount && budget > 0; p++)
                    if (pinned[p]) did |= Want(s, p, ref budget);
                // Everything wanted is in memory (a pass in which nothing was left to read): the stream is settled.
                bool missing = false;
                foreach (var r in ranges)
                {
                    if (!ReferenceEquals(r.S, s)) continue;
                    int p0 = (int)Math.Max(0, r.From >> AudioCache.PageShift), p1 = (int)Math.Min(s.PageCount - 1, (Math.Max(r.From, r.To - 1)) >> AudioCache.PageShift);
                    for (int p = p0; p <= p1; p++) if (!s.PageResident(p)) { missing = true; break; }
                    if (missing) break;
                }
                if (!missing) for (int p = 0; p < s.PageCount; p++) if (pinned[p] && !s.PageResident(p)) { missing = true; break; }
                if (missing) s.Settled = 0;
                else
                {
                    s.Settled++;
                    Volatile.Write(ref s.SettledSerial, s.SettledSerial + 1);
                }
                // Mark what is wanted, so the eviction knows what has not been.
                foreach (var r in ranges)
                {
                    if (!ReferenceEquals(r.S, s)) continue;
                    int p0 = (int)Math.Max(0, r.From >> AudioCache.PageShift), p1 = (int)Math.Min(s.PageCount - 1, (Math.Max(r.From, r.To - 1)) >> AudioCache.PageShift);
                    for (int p = p0; p <= p1; p++) { var pg = s.PageAt(p); if (pg != null) { pg.Wanted = tick; pg.WantedMs = now; } }
                }
                for (int p = 0; p < s.PageCount; p++) if (pinned[p]) { var pg = s.PageAt(p); if (pg != null) { pg.Wanted = tick; pg.WantedMs = now; } }
            }

            Evict(all, now);
            if (now - lastFileCheck > 1500)
            {
                lastFileCheck = now;
                foreach (var s in all) s.CheckFile();
            }
            return did;
        }

        [ThreadStatic] static List<(double Key, StreamedAudio S, int Page)> dueScratch;          // per thread: the prefetch thread's, and a test's
        [ThreadStatic] static HashSet<(StreamedAudio, int)> plannedScratch;
        static readonly Comparison<(double Key, StreamedAudio S, int Page)> DueOrder = (a, b) => a.Key.CompareTo(b.Key);
        static readonly List<(StreamedAudio S, int Page)> plan = new List<(StreamedAudio, int)>();

        /// <summary>
        /// The pages to read next, earliest deadline first across the voices and the files (RB1), at most <paramref name="count"/>: the pages of
        /// each range not in memory, each with the fraction of its window's reach it lies at from the voice (the pages ahead by their distance over
        /// the window ahead, so a fast voice, whose window is longer, has its pages nearer in time; the pages behind it, tails, after all of those
        /// ahead). The order before this went voice by voice, so a voice whose next page was the third ahead (the first past a clip's pinned head)
        /// waited for the whole window of every voice before it in the list. Prefetch thread; tests call it with ranges they made.
        /// </summary>
        internal static void Plan(IReadOnlyList<(StreamedAudio S, long Pos, long From, long To)> ranges, int count, List<(StreamedAudio S, int Page)> into)
        {
            var due = dueScratch ??= new List<(double, StreamedAudio, int)>();
            var planned = plannedScratch ??= new HashSet<(StreamedAudio, int)>();
            due.Clear();
            foreach (var r in ranges)
            {
                var rs = r.S;
                if (rs.IsDisposed || rs.Damaged) continue;
                int p0 = (int)Math.Max(0, r.From >> AudioCache.PageShift), p1 = (int)Math.Min(rs.PageCount - 1, (Math.Max(r.From, r.To - 1)) >> AudioCache.PageShift);
                if (p0 > p1) continue;                      // a window past the end of the file (a voice at a loop's wrap, a mirrored view): nothing to read; the old loops did not run for it either, this one would have gone out of the table and the pass thrown
                int at = (int)Math.Clamp(r.Pos >> AudioCache.PageShift, p0, Math.Max(p0, p1));
                // The voice goes the way its window reaches further: up the file, or down it for a reversed view (its frames are mirrored).
                int dir = at - p0 > p1 - at ? -1 : 1;
                int end = dir > 0 ? p1 : p0, back = dir > 0 ? p0 : p1;
                double ahead = Math.Max(1, Math.Abs(end - at) + 1), behind = Math.Max(1, Math.Abs(at - back) + 1);
                for (int p = at; p != end + dir; p += dir) if (!rs.PageResident(p)) due.Add((Math.Abs(p - at) / ahead, rs, p));
                for (int p = at - dir; p != back - dir; p -= dir) if (!rs.PageResident(p)) due.Add((1 + Math.Abs(at - p) / behind, rs, p));
            }
            due.Sort(DueOrder);
            planned.Clear();
            foreach (var d in due)
            {
                if (into.Count >= count) break;
                if (planned.Add((d.S, d.Page))) into.Add((d.S, d.Page));
            }
        }

        static bool Want(StreamedAudio s, int page, ref int budget)
        {
            if (s.PageResident(page)) return false;
            budget--;
            return s.Load(page, () => Make(s.Channels), tick);
        }

        /// <summary>Pages over the budget go, the ones not wanted for longest first (never the ones wanted this pass or within the last second: a slice's or a crossfade's tail reads where the voice's window has just left).</summary>
        internal static int EvictGraceMs = 1000;

        static void Evict(StreamedAudio[] all, long now)
        {
            long total = 0;
            foreach (var s in all) total += s.ResidentBytes;
            long limit = StreamingPolicy.ResidentBytes;
            if (total <= limit) return;
            var candidates = new List<(StreamedAudio S, int Page, long Wanted)>();
            foreach (var s in all)
                for (int p = 0; p < s.PageCount; p++)
                {
                    var pg = s.PageAt(p);
                    if (pg != null && pg.Wanted < tick && now - pg.WantedMs > EvictGraceMs) candidates.Add((s, p, pg.Wanted));
                }
            candidates.Sort((a, b) => a.Wanted.CompareTo(b.Wanted));
            foreach (var c in candidates)
            {
                if (total <= limit) break;
                var pg = c.S.Evict(c.Page);
                if (pg == null) continue;
                total -= StreamedAudio.PageFrames * 4L * c.S.Channels;
                pg.Freed = Environment.TickCount64;
                lock (graveyard) graveyard.Enqueue(pg);
            }
        }

        /// <summary>
        /// Before the floats of an asset that plays go: waits (at most <paramref name="milliseconds"/>) until a voice has published what it will read from the
        /// stream (a voice does so at the end of a block, and only once the stream is attached), then a moment more for the others. With no voice playing
        /// that is the whole wait. Without it the stream was "settled" with nothing wanted, the memory went, and the next frame of a voice read pages the
        /// prefetch thread had not been asked for: silence, counted, and a sample or more that differed (wave-45 Fork CI run 44652503440, PL1).
        /// </summary>
        internal static void WaitForVoices(StreamedAudio s, int milliseconds, int graceMilliseconds = 30)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds && Volatile.Read(ref s.HintsSeen) == 0) Thread.Sleep(2);
            if (Volatile.Read(ref s.HintsSeen) > 0) Thread.Sleep(graceMilliseconds);
        }

        /// <summary>Tests: waits until every open stream is settled (what its voices and pins want is in memory).</summary>
        internal static bool WaitSettled(StreamedAudio s, int milliseconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long start = Volatile.Read(ref s.SettledSerial);
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                if (s.Settled >= 3 && Volatile.Read(ref s.SettledSerial) - start >= 3) return true;
                Thread.Sleep(2);
            }
            return false;
        }
    }
}
