#nullable disable
// Fork (docs/specs/mac-engine.md M0): the engine's UI-thread seam (IUiDispatcher) for hosts with
// no UI toolkit: one thread that runs queued work, highest priority first and in order within a
// priority, as WPF's dispatcher does, plus its timers. The headless host runs it on its main
// thread (Run); tests and embedders can give it a thread of its own (Start). An exception in a
// queued action goes to UnhandledException and the loop carries on. Pure .NET.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ReBuzz.Core;

namespace ReBuzz.Platform
{
    public sealed class LoopDispatcher : IUiDispatcher, IDisposable
    {
        readonly object gate = new object();
        readonly SortedDictionary<int, Queue<Action>> queues = new SortedDictionary<int, Queue<Action>>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        readonly List<LoopTimer> timers = new List<LoopTimer>();
        Thread thread;
        bool stopping;

        /// <summary>An action queued here threw; the loop goes on. Default: written to stderr.</summary>
        public event Action<Exception> UnhandledException;

        /// <summary>The loop's thread, once it runs.</summary>
        public Thread Thread => thread;

        public bool CheckAccess() => thread == null || Thread.CurrentThread == thread;

        public void BeginInvoke(Action action) => BeginInvoke(action, UiPriority.Normal);

        public void BeginInvoke(Action action, UiPriority priority)
        {
            if (action == null) return;
            lock (gate)
            {
                if (!queues.TryGetValue((int)priority, out var q)) queues[(int)priority] = q = new Queue<Action>();
                q.Enqueue(action);
                Monitor.PulseAll(gate);
            }
        }

        /// <summary>Runs the action on the loop's thread and waits for it; on that thread, runs it at once.</summary>
        public void Invoke(Action action)
        {
            if (action == null) return;
            if (CheckAccess())
            {
                action();
                return;
            }
            Exception failed = null;
            using var done = new ManualResetEventSlim();
            BeginInvoke(() =>
            {
                try { action(); }
                catch (Exception e) { failed = e; }
                finally { done.Set(); }
            }, UiPriority.Send);
            done.Wait();
            if (failed != null) throw new InvalidOperationException("An action invoked on the dispatcher failed: " + failed.Message, failed);
        }

        public IUiTimer CreateTimer(TimeSpan interval, Action tick)
        {
            var t = new LoopTimer(this, interval, tick);
            lock (gate) timers.Add(t);
            return t;
        }

        /// <summary>Runs the loop on the calling thread until <see cref="Stop"/> or the token.</summary>
        public void Run(CancellationToken cancellation = default)
        {
            lock (gate)
            {
                thread = Thread.CurrentThread;
                stopping = false;
            }
            using var registration = cancellation.Register(Stop);
            while (true)
            {
                Action next = null;
                lock (gate)
                {
                    while (!stopping && (next = Dequeue()) == null)
                    {
                        long wait = NextTimerWait();
                        Monitor.Wait(gate, wait < 0 ? Timeout.Infinite : (int)Math.Clamp(wait, 1, int.MaxValue));
                    }
                    if (stopping) return;
                }
                try { next(); }
                catch (Exception e) { Report(e); }
            }
        }

        /// <summary>Runs the loop on a thread of its own (background).</summary>
        public void Start(string name = "ReBuzz engine loop")
        {
            using var started = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                lock (gate) thread = Thread.CurrentThread;
                started.Set();
                Run();
            }) { IsBackground = true, Name = name };
            t.Start();
            started.Wait();
        }

        /// <summary>Ends the loop after the action running now; queued work is dropped.</summary>
        public void Stop()
        {
            lock (gate)
            {
                stopping = true;
                Monitor.PulseAll(gate);
            }
        }

        public void Dispose()
        {
            Stop();
            var t = thread;
            if (t != null && t != Thread.CurrentThread && t.IsAlive) t.Join(1000);
        }

        /// <summary>Under the gate: the next action, timers due first queued at their priority.</summary>
        Action Dequeue()
        {
            long now = Stopwatch.GetTimestamp();
            foreach (var t in timers)
                if (t.DueAt(now)) EnqueueTick(t);
            foreach (var q in queues.Values)
                if (q.Count > 0) return q.Dequeue();
            return null;
        }

        void EnqueueTick(LoopTimer t)
        {
            t.Queued = true;
            if (!queues.TryGetValue((int)UiPriority.Background, out var q)) queues[(int)UiPriority.Background] = q = new Queue<Action>();
            q.Enqueue(() =>
            {
                lock (gate) t.Queued = false;
                if (t.IsEnabled) t.Tick();
            });
        }

        /// <summary>Under the gate: milliseconds until the next timer is due, or −1 with none running.</summary>
        long NextTimerWait()
        {
            long now = Stopwatch.GetTimestamp(), soonest = long.MaxValue;
            foreach (var t in timers)
                if (t.IsEnabled && !t.Queued) soonest = Math.Min(soonest, t.Next - now);
            if (soonest == long.MaxValue) return -1;
            return Math.Max(0, soonest * 1000 / Stopwatch.Frequency);
        }

        void Report(Exception e)
        {
            var handler = UnhandledException;
            if (handler != null) handler(e);
            else Console.Error.WriteLine("[ReBuzz] " + e.GetType().Name + " on the engine loop: " + e.Message);
        }

        /// <summary>
        /// A timer whose ticks run on the loop, as a DispatcherTimer's do: it starts stopped,
        /// starting it again does nothing, and a tick still waiting to run is not queued twice.
        /// </summary>
        sealed class LoopTimer : IUiTimer
        {
            readonly LoopDispatcher owner;
            readonly Action tick;
            TimeSpan interval;
            bool enabled;

            public LoopTimer(LoopDispatcher owner, TimeSpan interval, Action tick)
            {
                this.owner = owner;
                this.interval = interval;
                this.tick = tick;
            }

            /// <summary>Stopwatch time of the next tick.</summary>
            public long Next;
            /// <summary>A tick is queued and hasn't run yet.</summary>
            public bool Queued;

            public TimeSpan Interval
            {
                get => interval;
                set
                {
                    lock (owner.gate)
                    {
                        interval = value;
                        if (enabled) Next = Stopwatch.GetTimestamp() + Ticks(value);
                        Monitor.PulseAll(owner.gate);
                    }
                }
            }

            public bool IsEnabled => enabled;

            public void Start()
            {
                lock (owner.gate)
                {
                    if (enabled) return;
                    enabled = true;
                    Next = Stopwatch.GetTimestamp() + Ticks(interval);
                    Monitor.PulseAll(owner.gate);
                }
            }

            public void Stop()
            {
                lock (owner.gate) enabled = false;
            }

            /// <summary>Under the gate: whether a tick should be queued now (and the next one scheduled).</summary>
            public bool DueAt(long now)
            {
                if (!enabled || Queued || now < Next) return false;
                Next = now + Ticks(interval);
                return true;
            }

            public void Tick() => tick();

            static long Ticks(TimeSpan t) => Math.Max(1, (long)(t.TotalSeconds * Stopwatch.Frequency));
        }
    }
}
