// Fork (audio hygiene, docs/specs/daw-modes.md §8.1): fork-join on the render threads with nothing
// allocated per dispatch. The work algorithms used to start a Task for each machine (Groups, the
// editors' and controls' lists) or each input (Recursive, the default), wait with Task.WaitAll,
// and sort inputs with OrderByDescending: a Task, its scheduler's list node and request closure,
// a wait object and LINQ's sorter for every dispatch, about 1.3 KB a 256-frame buffer with an
// empty song and 2 KB with one synth, on the audio thread, in every buffer, playing or not.
//
// Here the jobs are objects made once and reused, and the render threads (RenderThreads) take
// them from a ring made once. A job is claimed by whoever gets to it first, a render thread or
// the thread waiting for it, which runs its own jobs while it waits (the newest first, as the
// render threads take the oldest), as Task.Wait ran a task inline. A job the waiter claimed stays
// in the ring until a render thread passes it by; at the start of the next buffer every job has
// run, so the ring is emptied then, and never grows (the pool's queue grew a segment when its
// threads fell behind). Joins count their jobs down; the waiter spins briefly and then waits on
// a monitor.

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using ReBuzz.Core;

namespace ReBuzz.Audio
{
    /// <summary>A count of jobs to wait for, made once and reused (one waiter at a time).</summary>
    internal sealed class RenderJoin
    {
        int pending;
        Exception error;
        // A monitor's wait allocates nothing (ManualResetEventSlim made its lock the first time it
        // had to block, which could be any buffer).
        readonly object gate = new object();

        /// <summary>Before the jobs are queued: <paramref name="count"/> of them to wait for.</summary>
        internal void Begin(int count)
        {
            error = null;
            Volatile.Write(ref pending, count);
        }

        internal void Done(Exception e)
        {
            if (e != null) Interlocked.CompareExchange(ref error, e, null);
            if (Interlocked.Decrement(ref pending) == 0)
                lock (gate) Monitor.PulseAll(gate);
        }

        /// <summary>Waits for the jobs (spinning briefly first); an exception one of them threw is thrown here, as Task.WaitAll did.</summary>
        internal void Wait()
        {
            if (Volatile.Read(ref pending) > 0)
            {
                var spin = new SpinWait();
                while (Volatile.Read(ref pending) > 0 && !spin.NextSpinWillYield)
                    spin.SpinOnce();
                if (Volatile.Read(ref pending) > 0)
                {
                    lock (gate)
                    {
                        while (Volatile.Read(ref pending) > 0)
                            Monitor.Wait(gate);
                    }
                }
            }
            var e = error;
            if (e != null)
            {
                error = null;
                ExceptionDispatchInfo.Capture(e).Throw();
            }
        }
    }

    /// <summary>
    /// The render threads (as many as the Audio threads setting, AudioEngine.ThreadCount): they
    /// take jobs from a ring and run those nobody has claimed. One set for the process, as the
    /// engine renders one buffer at a time (under AudioLock).
    /// </summary>
    internal static class RenderThreads
    {
        static readonly object gate = new object();
        static readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        static RenderJobs.Job[] ring = new RenderJobs.Job[256];
        static int head, count;
        static int threads;

        /// <summary>Render threads running.</summary>
        internal static int Count => Volatile.Read(ref threads);

        /// <summary>At least <paramref name="count"/> render threads (more are never stopped). UI thread.</summary>
        internal static void Ensure(int count)
        {
            lock (gate)
            {
                while (threads < count)
                {
                    var t = new Thread(Work) { IsBackground = true, Name = "Render " + threads };
                    try { t.Priority = ProcessAndThreadProfile.DedicatedThreadPoolThread; } catch (Exception) { }
                    t.Start();
                    threads++;
                }
            }
        }

        /// <summary>
        /// Audio thread, before a buffer's dispatches, when every job queued before has run: the
        /// ring emptied (whatever is left was claimed by its waiter), with room for <paramref name="jobs"/>.
        /// </summary>
        internal static void Clear(int jobs)
        {
            lock (gate)
            {
                if (ring.Length < jobs)
                {
                    int size = ring.Length;
                    while (size < jobs) size *= 2;
                    ring = new RenderJobs.Job[size];    // only when the graph grew
                }
                else if (count > 0)
                {
                    Array.Clear(ring);
                }
                head = 0;
                count = 0;
            }
        }

        /// <summary>Queues a job (false when there are no render threads or no room: the caller runs it).</summary>
        internal static bool Enqueue(RenderJobs.Job job)
        {
            if (Volatile.Read(ref threads) == 0) return false;
            lock (gate)
            {
                if (count == ring.Length) return false;
                ring[(head + count) & (ring.Length - 1)] = job;
                count++;
            }
            signal.Release();
            return true;
        }

        // Optimized at once: its loop isn't recompiled (OSR) on a render thread while it plays.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        static void Work()
        {
            try
            {
                while (true)
                {
                    signal.Wait();
                    RenderJobs.Job job = null;
                    lock (gate)
                    {
                        if (count > 0)
                        {
                            job = ring[head];
                            ring[head] = null;
                            head = (head + 1) & (ring.Length - 1);
                            count--;
                        }
                    }
                    if (job == null) continue;
                    // macOS (mac-engine.md M7): render in the audio device's workgroup.
                    ReBuzz.Platform.AudioWorkgroup.Enter();
                    job.RunQueued();
                }
            }
            finally
            {
                ReBuzz.Platform.AudioWorkgroup.Leave();
            }
        }
    }

    internal sealed class RenderJobs
    {
        internal sealed class Job
        {
            int state;                  // 1: queued, not yet claimed
            Action<object> body;
            object arg;
            RenderJoin join;

            internal void Set(Action<object> body, object arg, RenderJoin join)
            {
                this.body = body;
                this.arg = arg;
                this.join = join;
                Volatile.Write(ref state, 1);   // the fields first: a claim sees them
            }

            /// <summary>Runs the job here if nobody has claimed it; false if somebody had.</summary>
            internal bool TryRun()
            {
                if (Interlocked.CompareExchange(ref state, 0, 1) != 1)
                    return false;
                var b = body;
                var a = arg;
                var j = join;
                Exception error = null;
                try
                {
                    b(a);
                }
                catch (Exception e)
                {
                    error = e;
                }
                j.Done(error);          // the last use of the job: the next buffer may reuse it
                return true;
            }

            /// <summary>A render thread took it from the ring: what it allocated and compiled is counted.</summary>
            internal void RunQueued()
            {
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long methods = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
                if (!TryRun()) return;
                CountWorker(bytes, methods);
            }
        }

        /// <summary>
        /// Bytes allocated and methods compiled by render threads in the jobs they ran (the audio
        /// thread's own are its thread's): what tests and diagnostics add to the audio thread's.
        /// </summary>
        internal static long WorkerBytes, WorkerMethods;

        /// <summary>A render thread finished a job it started at these counts.</summary>
        internal static void CountWorker(long bytesBefore, long methodsBefore)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
            long methods = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - methodsBefore;
            if (bytes != 0) Interlocked.Add(ref WorkerBytes, bytes);
            if (methods != 0) Interlocked.Add(ref WorkerMethods, methods);
        }

        Job[] jobs = new Job[64];
        int taken;

        public RenderJobs()
        {
            for (int i = 0; i < jobs.Length; i++)
                jobs[i] = new Job();
        }

        /// <summary>
        /// The audio thread, before a buffer's dispatches (every job of the last buffer has run):
        /// room for <paramref name="capacity"/> dispatches (more only when the graph grew).
        /// </summary>
        internal void BeginBuffer(int capacity)
        {
            if (jobs.Length < capacity)
            {
                int size = Math.Max(capacity, jobs.Length * 2);
                var grown = new Job[size];
                Array.Copy(jobs, grown, jobs.Length);
                for (int i = jobs.Length; i < size; i++)
                    grown[i] = new Job();
                jobs = grown;
            }
            Volatile.Write(ref taken, 0);
            RenderThreads.Clear(jobs.Length);
        }

        /// <summary>
        /// Queues <paramref name="body"/>(<paramref name="arg"/>) on the render threads, counted by
        /// <paramref name="join"/>, and returns its job for the waiter to help with (TryRun); with no
        /// render threads or no free job, runs it here and returns null. Any thread.
        /// </summary>
        internal Job Queue(Action<object> body, object arg, RenderJoin join)
        {
            int i = RenderThreads.Count == 0 ? int.MaxValue : Interlocked.Increment(ref taken) - 1;
            var all = jobs;
            if (i < all.Length)
            {
                var job = all[i];
                job.Set(body, arg, join);
                RenderThreads.Enqueue(job);     // if there's no room the waiter runs it (TryRun)
                return job;
            }
            Exception error = null;
            try { body(arg); }
            catch (Exception e) { error = e; }
            join.Done(error);
            return null;
        }
    }
}
