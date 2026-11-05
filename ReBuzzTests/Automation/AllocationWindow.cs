// Fork (engine hardening, docs/specs/daw-modes.md §8.1): what the thread reading a stretch of audio buffers did in each
// of them, kept without allocating, so a zero-allocation test that fails can say what allocated. Reading on afterwards
// can't name a one-off: SteadyPlaybackAllocatesNothing met one now and then on CI, once in a buffer after its warm-up
// and never again (848, 1512 and 2048 bytes; runs 41916587037, 41966650618 and 41912541139: "then 0 bytes in up to
// 20000 buffers").
//
// Per buffer: the bytes the thread allocated, the methods the JIT compiled on it and how long that took, the GCs, and the
// buffer's time. And from a listener on the runtime's own events (the runtime writes them; the listener gets them on a
// thread of its own), what the runtime did on the reading thread through the stretch: JIT (each method and its tier),
// type loads, exceptions, lock contention, waits, assembly loads and the GC's allocation ticks (which name a type), each
// placed in the buffer it fell in; and the GCs, on whatever thread.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ReBuzzTests.Automation
{
    internal sealed class AllocationWindow : IDisposable
    {
        readonly long[] bytes, methods, jitTicks, starts, ends;
        readonly int[] gcs;
        int count;
        long lastBytes, lastMethods, lastJitTicks;
        int lastGcs;
        RuntimeEvents? events;
        readonly long thread;
        readonly DateTime anchorUtc;
        readonly long anchorTimestamp;

        /// <param name="buffers">The most buffers to keep (more are measured, not kept).</param>
        /// <param name="listen">Whether to listen to the runtime's events through the stretch.</param>
        public AllocationWindow(int buffers, bool listen = true)
        {
            bytes = new long[buffers];
            methods = new long[buffers];
            jitTicks = new long[buffers];
            starts = new long[buffers];
            ends = new long[buffers];
            gcs = new int[buffers];
            thread = OperatingSystem.IsWindows() ? GetCurrentThreadId() : OperatingSystem.IsLinux() ? gettid() : -1;
            if (listen) events = new RuntimeEvents(thread);
            // Once before the stretch: whatever these reads compile or load, here.
            Begin();
            End();
            count = 0;
            anchorUtc = DateTime.UtcNow;
            anchorTimestamp = Stopwatch.GetTimestamp();
        }

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("libc", EntryPoint = "gettid")]
        static extern int gettid();

        /// <summary>Buffers measured.</summary>
        public int Count => count;

        /// <summary>Just before a buffer (nothing allocated).</summary>
        public void Begin()
        {
            lastMethods = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
            lastJitTicks = System.Runtime.JitInfo.GetCompilationTime(currentThread: true).Ticks;
            lastGcs = GC.CollectionCount(0);
            if (count < starts.Length) starts[count] = Stopwatch.GetTimestamp();
            lastBytes = GC.GetAllocatedBytesForCurrentThread();          // last: the reads above aren't counted
        }

        /// <summary>Just after it: the bytes this thread allocated in it.</summary>
        public long End()
        {
            long b = GC.GetAllocatedBytesForCurrentThread() - lastBytes;   // first
            if (count < bytes.Length)
            {
                ends[count] = Stopwatch.GetTimestamp();
                bytes[count] = b;
                methods[count] = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - lastMethods;
                jitTicks[count] = System.Runtime.JitInfo.GetCompilationTime(currentThread: true).Ticks - lastJitTicks;
                gcs[count] = GC.CollectionCount(0) - lastGcs;
            }
            count++;
            return b;
        }

        static double Ms(long stopwatchTicks) => stopwatchTicks * 1000.0 / Stopwatch.Frequency;

        /// <summary>The buffer an event fell in (by its time); -1 between two, -2 before or after the stretch.</summary>
        int BufferAt(DateTime utc)
        {
            long at = anchorTimestamp + (long)((utc - anchorUtc).Ticks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond);
            int kept = Math.Min(count, starts.Length);
            if (kept == 0 || at < starts[0] || at > ends[kept - 1]) return -2;
            for (int i = 0; i < kept; i++)
                if (at >= starts[i] && at <= ends[i]) return i;
            return -1;
        }

        /// <summary>
        /// What happened in each buffer that allocated (call after the stretch: it waits for the listener's last events,
        /// then stops it). Empty when none did.
        /// </summary>
        public string Describe(int maxBuffers = 5)
        {
            int kept = Math.Min(count, bytes.Length);
            var allocating = Enumerable.Range(0, kept).Where(i => bytes[i] > 0).Take(maxBuffers).ToList();
            bool listened = events != null;
            var all = events?.Finish() ?? new List<RuntimeEvents.Entry>();
            events = null;
            if (allocating.Count == 0) return "";
            var durations = Enumerable.Range(0, kept).Select(i => Ms(ends[i] - starts[i])).OrderBy(d => d).ToList();
            double median = durations.Count > 0 ? durations[durations.Count / 2] : 0;
            var placed = all.Select(e => (Entry: e, Buffer: BufferAt(e.Time))).ToList();
            var text = new StringBuilder();
            text.Append($"\nwhat this thread did in the buffers that allocated (a buffer took {median:F3} ms at the median):");
            foreach (int i in allocating)
            {
                text.Append($"\n  buffer {i}: {bytes[i]} bytes in {Ms(ends[i] - starts[i]):F3} ms; the JIT compiled {methods[i]} methods on this thread " +
                            $"({TimeSpan.FromTicks(jitTicks[i]).TotalMilliseconds:F3} ms); GCs {gcs[i]}");
                var mine = placed.Where(p => p.Buffer == i).Select(p => p.Entry).ToList();
                if (!listened)
                    text.Append("; the runtime's events weren't listened to");
                else if (mine.Count == 0)
                    text.Append("; no runtime event on this thread or GC in it");
                foreach (var e in mine.Take(25))
                    text.Append($"\n    {(e.Thread == thread ? "" : $"[thread {e.Thread}] ")}{e.Name} {e.Payload}");
            }
            var elsewhere = placed.Where(p => p.Entry.Thread == thread && p.Buffer > -2 && !allocating.Contains(p.Buffer)).Select(p => p.Entry).ToList();
            text.Append($"\n  runtime events on this thread elsewhere in the stretch: {elsewhere.Count}");
            foreach (var e in elsewhere.Take(10))
                text.Append($"\n    {e.Name} {e.Payload}");
            if (listened)
                text.Append($"\n  ({all.Count} runtime events kept in all: this thread's and the GCs)");
            return text.ToString();
        }

        public void Dispose()
        {
            events?.Dispose();
            events = null;
        }

        /// <summary>
        /// The runtime's events (Microsoft-Windows-DotNETRuntime) on one thread, and its GCs: kept, at most 5000, with
        /// their time, thread, name and payload.
        /// </summary>
        internal sealed class RuntimeEvents : EventListener
        {
            internal readonly record struct Entry(DateTime Time, long Thread, string Name, string Payload);

            const EventKeywords Keywords = (EventKeywords)(
                0x1               // GC: starts, ends, allocation ticks
                | 0x8             // Loader: assembly and module loads
                | 0x10            // JIT: each method compiled, and its tier
                | 0x4000          // contention
                | 0x8000          // exceptions
                | 0x2000000000    // compilation diagnostics (tiering)
                | 0x8000000000    // type loads
                | 0x40000000000); // waits on wait handles

            static long target;
            readonly ConcurrentQueue<Entry> kept = new();
            int keptCount;
            long last = Stopwatch.GetTimestamp();

            public RuntimeEvents(long thread)
            {
                Volatile.Write(ref target, thread);
            }

            protected override void OnEventSourceCreated(EventSource source)
            {
                if (source.Name == "Microsoft-Windows-DotNETRuntime")
                    EnableEvents(source, EventLevel.Verbose, Keywords);
            }

            protected override void OnEventWritten(EventWrittenEventArgs e)
            {
                Interlocked.Exchange(ref last, Stopwatch.GetTimestamp());
                string name = e.EventName ?? $"event {e.EventId}";
                if (name == "R2RGetEntryPointStart" || name == "TypeLoadStart") return;      // their ends say what
                bool gc = name.StartsWith("GCStart", StringComparison.Ordinal) || name.StartsWith("GCEnd", StringComparison.Ordinal);
                if (e.OSThreadId != Volatile.Read(ref target) && !gc) return;
                if (Interlocked.Increment(ref keptCount) > 5000) return;
                var payload = new StringBuilder();
                if (e.PayloadNames != null && e.Payload != null)
                    for (int i = 0; i < e.PayloadNames.Count && i < e.Payload.Count; i++)
                    {
                        string n = e.PayloadNames[i];
                        if (n.EndsWith("ID", StringComparison.Ordinal) || n.EndsWith("Address", StringComparison.Ordinal) || n.EndsWith("Token", StringComparison.Ordinal)
                            || n == "EntryPoint" || n == "ExceptionEIP" || n == "MethodSize") continue;
                        if (payload.Length > 300) { payload.Append(" …"); break; }
                        payload.Append(payload.Length == 0 ? "" : ", ").Append(n).Append('=').Append(e.Payload[i]);
                    }
                kept.Enqueue(new Entry(e.TimeStamp.ToUniversalTime(), e.OSThreadId, name, payload.ToString()));
            }

            /// <summary>Waits until the events stop coming (0.5 s quiet, 5 s at most), stops listening, returns what was kept.</summary>
            public List<Entry> Finish()
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 5000 && Ms(Stopwatch.GetTimestamp() - Interlocked.Read(ref last)) < 500)
                    Thread.Sleep(50);
                Dispose();
                return kept.ToList();
            }
        }
    }
}
