using System;
using System.Threading;

namespace ReBuzz.Core
{
    /// <summary>
    /// A timer whose ticks run on the UI thread (engine-split.md S2), made by
    /// <see cref="IUiDispatcher.CreateTimer"/>. It starts stopped. As WPF's DispatcherTimer:
    /// starting a running timer does nothing, and ticks don't pile up.
    /// </summary>
    public interface IUiTimer
    {
        TimeSpan Interval { get; set; }
        bool IsEnabled { get; }
        void Start();
        void Stop();
    }

    /// <summary>
    /// The default <see cref="IUiTimer"/>: a thread-pool timer that queues each tick with
    /// <see cref="IUiDispatcher.BeginInvoke(Action)"/>. For dispatchers without a timer of their own.
    /// </summary>
    internal sealed class ThreadPoolUiTimer : IUiTimer
    {
        readonly IUiDispatcher dispatcher;
        readonly Action tick;
        readonly Timer timer;
        readonly object gate = new object();
        TimeSpan interval;
        bool enabled;
        int queued;

        public ThreadPoolUiTimer(IUiDispatcher dispatcher, TimeSpan interval, Action tick)
        {
            this.dispatcher = dispatcher;
            this.tick = tick;
            this.interval = interval;
            timer = new Timer(_ => Elapsed(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public TimeSpan Interval
        {
            get => interval;
            set
            {
                lock (gate)
                {
                    interval = value;
                    if (enabled) timer.Change(value, value);
                }
            }
        }

        public bool IsEnabled => enabled;

        public void Start()
        {
            lock (gate)
            {
                if (enabled) return;
                enabled = true;
                timer.Change(interval, interval);
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                enabled = false;
                timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        void Elapsed()
        {
            if (Interlocked.Exchange(ref queued, 1) == 1) return;
            dispatcher.BeginInvoke(() =>
            {
                Volatile.Write(ref queued, 0);
                if (enabled) tick();
            });
        }
    }
}
