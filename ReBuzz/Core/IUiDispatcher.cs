using System;

namespace ReBuzz.Core
{
    /// <summary>
    /// When a queued UI-thread action runs. Same names and values as WPF's
    /// System.Windows.Threading.DispatcherPriority, so the WPF dispatcher just casts it.
    /// </summary>
    public enum UiPriority
    {
        SystemIdle = 1,
        ApplicationIdle = 2,
        ContextIdle = 3,
        Background = 4,
        Input = 5,
        Loaded = 6,
        Render = 7,
        DataBind = 8,
        Normal = 9,
        Send = 10,
    }

    public interface IUiDispatcher
    {
        void Invoke(Action action);
        void BeginInvoke(Action action);
        void BeginInvoke(Action action, UiPriority priority);

        /// <summary>Fork: whether the calling thread is the UI thread.</summary>
        bool CheckAccess() => true;

        /// <summary>
        /// Fork (engine-split.md S2): a stopped timer that runs <paramref name="tick"/> on the UI
        /// thread every <paramref name="interval"/>. The default queues ticks from a thread-pool timer.
        /// </summary>
        IUiTimer CreateTimer(TimeSpan interval, Action tick) => new ThreadPoolUiTimer(this, interval, tick);
    }
}
