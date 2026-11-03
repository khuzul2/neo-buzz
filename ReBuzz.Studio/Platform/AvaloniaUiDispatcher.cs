// Fork (docs/specs/studio-mvp.md §2): the engine's UI dispatcher (IUiDispatcher, engine-split.md
// S2) on Avalonia's UI thread.

using System;
using Avalonia.Threading;
using ReBuzz.Core;

namespace ReBuzz.Studio.Platform
{
    internal sealed class AvaloniaUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.Invoke(action);
        }

        public void BeginInvoke(Action action) => Dispatcher.UIThread.Post(action);

        public void BeginInvoke(Action action, UiPriority priority) => Dispatcher.UIThread.Post(action, Priority(priority));

        public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(interval, tick);

        static DispatcherPriority Priority(UiPriority priority) => priority switch
        {
            UiPriority.SystemIdle => DispatcherPriority.SystemIdle,
            UiPriority.ApplicationIdle => DispatcherPriority.ApplicationIdle,
            UiPriority.ContextIdle => DispatcherPriority.ContextIdle,
            UiPriority.Background => DispatcherPriority.Background,
            UiPriority.Input => DispatcherPriority.Input,
            UiPriority.Loaded => DispatcherPriority.Loaded,
            UiPriority.Render => DispatcherPriority.Render,
            UiPriority.DataBind => DispatcherPriority.DataBind,
            UiPriority.Send => DispatcherPriority.Send,
            _ => DispatcherPriority.Normal,
        };

        /// <summary>A DispatcherTimer: ticks on the UI thread, started stopped, never piling up.</summary>
        sealed class Timer : IUiTimer
        {
            readonly DispatcherTimer timer;

            public Timer(TimeSpan interval, Action tick)
            {
                timer = new DispatcherTimer { Interval = interval };
                timer.Tick += (s, e) => tick();
            }

            public TimeSpan Interval { get => timer.Interval; set => timer.Interval = value; }
            public bool IsEnabled => timer.IsEnabled;
            public void Start() => timer.Start();
            public void Stop() => timer.Stop();
        }
    }
}
