// Agent server (docs/specs/mcp-server.md §3.2): every tool call runs on the engine thread, one
// at a time, with a timeout. The thread is the WPF dispatcher in studio mode and in headless
// mode alike; tests use a plain thread.

using System;
using System.Threading;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent
{
    /// <summary>Runs work on the thread that owns the song (ReBuzz's UI/engine dispatcher).</summary>
    public interface IEngineThread
    {
        Task<T> InvokeAsync<T>(Func<T> work);
    }

    public sealed class EngineQueue
    {
        readonly IEngineThread thread;
        readonly SemaphoreSlim gate = new(1, 1);
        string? current;

        public EngineQueue(IEngineThread thread, TimeSpan timeout)
        {
            this.thread = thread;
            Timeout = timeout;
        }

        public TimeSpan Timeout { get; }

        /// <summary>What runs now ("machine_add"), or null.</summary>
        public string? Current => current;

        /// <summary>Raised (on a worker thread) when a call starts (its name) and ends (null): the studio's Agent badge.</summary>
        public event Action<string?>? CurrentChanged;

        /// <summary>
        /// Runs <paramref name="work"/> on the engine thread after any call before it. A call that
        /// waits or runs too long fails with a message saying so (the work itself can't be aborted).
        /// </summary>
        public async Task<T> Run<T>(string what, Func<T> work, CancellationToken cancellation = default, bool background = false)
        {
            if (!await gate.WaitAsync(Timeout, cancellation).ConfigureAwait(false))
                throw new ToolError($"{Product.Name} is still busy with {current ?? "another call"} after {Timeout.TotalSeconds:F0} s; try again shortly.");
            try
            {
                current = what;
                CurrentChanged?.Invoke(what);
                if (background)
                {
                    // Fork (ux-overhaul.md P4a): a long job (an export's render) on a worker thread of its own, below the UI's priority, still one
                    // at a time with every other call (the gate is held), and with no time limit but its cancellation.
                    var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var worker = new Thread(() =>
                    {
                        try { tcs.SetResult(work()); }
                        catch (Exception e) { tcs.SetException(e); }
                    }) { IsBackground = true, Name = Product.Name + " background render", Priority = ThreadPriority.BelowNormal };
                    worker.Start();
                    return await tcs.Task.ConfigureAwait(false);
                }
                var task = thread.InvokeAsync(work);
                var finished = await Task.WhenAny(task, Task.Delay(Timeout, cancellation)).ConfigureAwait(false);
                if (finished != task)
                {
                    cancellation.ThrowIfCancellationRequested();
                    throw new ToolError($"{what} took longer than {Timeout.TotalSeconds:F0} s and was abandoned; {Product.Name} may finish it later. Check studio_status before retrying.");
                }
                return await task.ConfigureAwait(false);
            }
            finally
            {
                current = null;
                CurrentChanged?.Invoke(null);
                gate.Release();
            }
        }

        public Task Run(string what, Action work, CancellationToken cancellation = default) =>
            Run<bool>(what, () => { work(); return true; }, cancellation);
    }

    /// <summary>An engine thread of our own: a dedicated thread running queued work in order (tests, tools without a dispatcher).</summary>
    public sealed class DedicatedEngineThread : IEngineThread, IDisposable
    {
        readonly System.Collections.Concurrent.BlockingCollection<Action> work = new();
        readonly Thread thread;

        public DedicatedEngineThread(string name = Product.Name + " agent engine")
        {
            thread = new Thread(() =>
            {
                foreach (var a in work.GetConsumingEnumerable()) a();
            }) { IsBackground = true, Name = name };
            thread.Start();
        }

        public Task<T> InvokeAsync<T>(Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            work.Add(() =>
            {
                try { tcs.SetResult(f()); }
                catch (Exception e) { tcs.SetException(e); }
            });
            return tcs.Task;
        }

        public void Dispose() => work.CompleteAdding();
    }
}
