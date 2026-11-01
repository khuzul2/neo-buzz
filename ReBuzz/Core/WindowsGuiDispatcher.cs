using System;
using System.Windows;
using System.Windows.Threading;

namespace ReBuzz.Core
{
    public class WindowsGuiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action)
        {
            Application.Current.Dispatcher.Invoke(action);
        }

        public void BeginInvoke(Action action)
        {
            Application.Current.Dispatcher.BeginInvoke(action);
        }

        public void BeginInvoke(Action action, UiPriority priority)
        {
            Application.Current.Dispatcher.BeginInvoke(action, (DispatcherPriority)priority);
        }

        public bool CheckAccess() => Application.Current?.Dispatcher.CheckAccess() ?? true;

        /// <summary>
        /// Fork (engine-split.md S2): a DispatcherTimer, made as the engine made them before: on the
        /// calling thread's dispatcher, at Background priority.
        /// </summary>
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new WpfUiTimer(interval, tick);

        sealed class WpfUiTimer : IUiTimer
        {
            readonly DispatcherTimer timer;

            public WpfUiTimer(TimeSpan interval, Action tick)
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