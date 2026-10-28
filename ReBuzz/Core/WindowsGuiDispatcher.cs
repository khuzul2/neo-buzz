using System;
using System.Windows;
using System.Windows.Threading;

namespace ReBuzz.Core
{
    public interface IUiDispatcher
    {
        void Invoke(Action action);
        void BeginInvoke(Action action);
        void BeginInvoke(Action action, DispatcherPriority priority);

        /// <summary>Fork: whether the calling thread is the UI thread.</summary>
        bool CheckAccess() => true;
    }

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

        public void BeginInvoke(Action action, DispatcherPriority priority)
        {
            Application.Current.Dispatcher.BeginInvoke(action, priority);
        }

        public bool CheckAccess() => Application.Current?.Dispatcher.CheckAccess() ?? true;
    }
}