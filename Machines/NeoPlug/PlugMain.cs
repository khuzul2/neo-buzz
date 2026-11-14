#nullable disable
// The plug-in host's "main thread" (docs/specs/plugin-host.md §3.4). Plug-ins want one thread for opening, closing, state
// and idling; ReBuzz's own threads come and go, so the host has one of its own (STA on Windows) that serves every Neo Plug
// machine: a queue of jobs, and a pump every ~20 ms for the machines' mirrors and the plug-ins' idle calls.

using System;
using System.Collections.Generic;
using System.Threading;

namespace NeoBuzz.Plug
{
    static class PlugMain
    {
        static readonly object gate = new();
        static readonly Queue<Action> jobs = new();
        static readonly List<Action> pumps = new();
        static Thread thread;
        static int threadId;
        static volatile bool quit;

        public static bool OnThread => Environment.CurrentManagedThreadId == threadId;

        static void Ensure()
        {
            lock (gate)
            {
                if (thread != null) return;
                thread = new Thread(Loop) { IsBackground = true, Name = "PlugMain" };
                if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                threadId = thread.ManagedThreadId;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            }
        }

        static void Loop()
        {
            while (!quit)
            {
                Action job = null;
                Action[] copy = null;
                lock (gate)
                {
                    if (jobs.Count == 0) Monitor.Wait(gate, 20);
                    if (jobs.Count > 0) job = jobs.Dequeue();
                    else lock (pumps) copy = pumps.ToArray();
                }
                if (job != null)
                {
                    try { job(); } catch (Exception) { }
                    continue;
                }
                if (copy != null)
                    foreach (var p in copy)
                        try { p(); } catch (Exception) { }
            }
        }

        public static void AddPump(Action pump) { Ensure(); lock (pumps) pumps.Add(pump); }
        public static void RemovePump(Action pump) { lock (pumps) pumps.Remove(pump); }

        /// <summary>Runs <paramref name="f"/> on the host thread and waits for it (at once when already there).</summary>
        public static T Invoke<T>(Func<T> f, int timeoutMs = 120000)
        {
            if (OnThread) return f();
            Ensure();
            T result = default;
            Exception error = null;
            using var done = new ManualResetEventSlim();
            lock (gate)
            {
                jobs.Enqueue(() =>
                {
                    try { result = f(); } catch (Exception e) { error = e; }
                    done.Set();
                });
                Monitor.Pulse(gate);
            }
            if (!done.Wait(timeoutMs)) throw new TimeoutException("the plug-in host thread did not answer");
            if (error != null) throw new InvalidOperationException(error.Message, error);
            return result;
        }

        public static void Invoke(Action a, int timeoutMs = 120000) => Invoke<bool>(() => { a(); return true; }, timeoutMs);

        /// <summary>Runs a job without waiting.</summary>
        public static void Post(Action a)
        {
            Ensure();
            lock (gate) { jobs.Enqueue(a); Monitor.Pulse(gate); }
        }

        static void Shutdown()
        {
            PlugCore[] all;
            lock (PlugCore.Live) all = new List<PlugCore>(PlugCore.Live).ToArray();
            if (thread == null) return;
            try
            {
                Invoke(() => { foreach (var c in all) c.CloseNow(); }, 5000);
            }
            catch (Exception) { }
            quit = true;
        }
    }
}
