using System;
using System.Threading;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork (audio hygiene, docs/specs/daw-modes.md §8.1): wakes the UI thread to run one action
    /// made once (<see cref="IUiDispatcher.CreateSignal"/>). Any number of requests before it runs
    /// make one run; a request while it runs makes another. Requesting allocates nothing, so the
    /// audio thread can request.
    /// </summary>
    public interface IUiSignal
    {
        void Request();
    }

    /// <summary>
    /// The default <see cref="IUiSignal"/>: the action, made into a delegate once, queued with
    /// BeginInvoke when first requested. Allocation-free where BeginInvoke of a delegate is (the
    /// headless loop's queue, the tests' dispatchers).
    /// </summary>
    internal sealed class PostedUiSignal : IUiSignal
    {
        readonly IUiDispatcher dispatcher;
        readonly Action run;
        int requested;

        public PostedUiSignal(IUiDispatcher dispatcher, Action action)
        {
            this.dispatcher = dispatcher;
            run = () =>
            {
                Volatile.Write(ref requested, 0);   // before the action: a request it misses runs again
                action();
            };
        }

        public void Request()
        {
            if (Interlocked.Exchange(ref requested, 1) == 0)
                dispatcher.BeginInvoke(run, UiPriority.Normal);
        }
    }

    /// <summary>
    /// An <see cref="IUiSignal"/> for toolkits whose every post allocates (Avalonia's and WPF's
    /// dispatcher operations): a request from another thread (the audio thread) only sets a flag,
    /// which a timer on the UI thread looks at. A request on the UI thread itself (an offline render
    /// there, as tests make) is posted, so the action runs as soon as the UI thread's queue does.
    /// The timer runs from the start (made on the UI thread) until disposed.
    /// </summary>
    public sealed class PolledUiSignal : IUiSignal, IDisposable
    {
        readonly IUiDispatcher dispatcher;
        readonly IUiTimer timer;
        readonly Action action;
        readonly Action runPosted;
        int requested;
        int posted;

        /// <summary>Every 1/60 s: about a frame late at most.</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(1000 / 60.0);

        public PolledUiSignal(IUiDispatcher dispatcher, Action action, TimeSpan interval)
        {
            this.dispatcher = dispatcher;
            this.action = action;
            runPosted = () =>
            {
                Volatile.Write(ref posted, 0);
                RunIfRequested();
            };
            timer = dispatcher.CreateTimer(interval, RunIfRequested);
            timer.Start();
        }

        void RunIfRequested()
        {
            if (Volatile.Read(ref requested) != 0 && Interlocked.Exchange(ref requested, 0) != 0)
                action();
        }

        public void Request()
        {
            Volatile.Write(ref requested, 1);
            if (dispatcher.CheckAccess() && Interlocked.Exchange(ref posted, 1) == 0)
                dispatcher.BeginInvoke(runPosted, UiPriority.Normal);
        }

        /// <summary>Stops the timer (UI thread); a request after it runs only if made on the UI thread.</summary>
        public void Dispose() => timer.Stop();
    }
}
