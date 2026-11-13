// Experiment: stands in for a plugin editor. A managed background thread allocates garbage at a set rate,
// optionally with a big live heap churning through gen2; a sampler thread records the longest GC pause seen.
using System;
using System.Diagnostics;
using System.Runtime;
using System.Text;
using System.Threading;

namespace NeoPluginGc
{
    internal static unsafe class LoadGen
    {
        sealed class Node { public Node Next; public byte[] Data; }

        static Thread loadThread, sampler;
        static volatile bool run;
        static object sink;
        static long maxPauseNs;
        public static long MaxPauseNs => Volatile.Read(ref maxPauseNs);

        public static void ResetMaxPause() => Volatile.Write(ref maxPauseNs, 0);

        public static void Start(int mode)
        {
            if (sampler == null)
            {
                sampler = new Thread(Sample) { IsBackground = true, Name = "gc-sampler" };
                sampler.Start();
            }
            Stop();
            if (mode <= 0) return;
            run = true;
            loadThread = new Thread(() => Load(mode)) { IsBackground = true, Name = "editor-load" };
            loadThread.Start();
        }

        public static void Stop()
        {
            run = false;
            var t = loadThread; loadThread = null;
            t?.Join();
        }

        static void Load(int mode)
        {
            double bytesPerSec = mode == 1 ? 1e6 : 20e6;
            const int RingSize = 2048;
            var ring = new object[RingSize];          // a UI keeps a little recent state alive
            int ringPos = 0;
            Node[] live = null;
            var rnd = new Random(12345);
            if (mode == 3)
            {
                live = new Node[1_000_000];           // ~256 MB: node + 200 byte payload
                for (int i = 0; i < live.Length; i++) live[i] = new Node { Data = new byte[200], Next = i > 0 ? live[i - 1] : null };
            }
            var sw = Stopwatch.StartNew();
            double allocated = 0, churned = 0, lohAt = 0;
            while (run)
            {
                double t = sw.Elapsed.TotalSeconds;
                double debt = t * bytesPerSec - allocated;
                while (debt > 0)
                {
                    int size = 64 + rnd.Next(0, 2048);   // small objects, 64 B to 2 KB
                    object o = (size & 1) == 0 ? new byte[size] : (object)new string('x', size / 2);
                    ring[ringPos] = o; ringPos = (ringPos + 1) % RingSize;
                    allocated += size + 24; debt -= size + 24;
                }
                sink = ring;
                if (live != null)
                {
                    // Replace ~1 MB/s of the live graph: old nodes die in gen2, new ones get promoted into it.
                    double due = t * 1e6 - churned;
                    while (due > 0)
                    {
                        int i = rnd.Next(live.Length);
                        live[i] = new Node { Data = new byte[200], Next = live[rnd.Next(live.Length)] };
                        churned += 256; due -= 256;
                    }
                    // A 2 MB large-object-heap buffer every second (an editor's bitmap): LOH growth triggers gen2 GCs.
                    if (t >= lohAt) { sink = new byte[2 << 20]; lohAt = t + 1; }
                }
                Thread.Sleep(1);
            }
            GC.KeepAlive(live);
        }

        static void Sample()
        {
            int seen = GC.CollectionCount(0);
            while (true)
            {
                Thread.Sleep(1);
                int c = GC.CollectionCount(0);
                if (c == seen) continue;
                seen = c;
                Take(GCKind.Any); Take(GCKind.FullBlocking); Take(GCKind.Background);
            }
        }

        static void Take(GCKind kind)
        {
            var info = GC.GetGCMemoryInfo(kind);
            foreach (var p in info.PauseDurations)
            {
                long ns = p.Ticks * 100;
                if (ns > Volatile.Read(ref maxPauseNs)) Volatile.Write(ref maxPauseNs, ns);
            }
        }

        public static int Describe(byte* buf, int cap)
        {
            var sb = new StringBuilder();
            sb.Append("server=").Append(GCSettings.IsServerGC).Append(" latency=").Append(GCSettings.LatencyMode);
            try
            {
                foreach (var kv in GC.GetConfigurationVariables())
                    if (kv.Key.Contains("Concurrent", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Gen0", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Server", StringComparison.OrdinalIgnoreCase))
                        sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            }
            catch { }
            var s = sb.ToString();
            int n = Math.Min(s.Length, cap - 1);
            for (int i = 0; i < n; i++) buf[i] = (byte)s[i];
            buf[n] = 0;
            return n;
        }
    }
}
