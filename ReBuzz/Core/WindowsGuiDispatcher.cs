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
    }
}