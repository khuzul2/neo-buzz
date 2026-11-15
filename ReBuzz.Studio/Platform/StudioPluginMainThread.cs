// Fork (docs/specs/plugin-host.md H3): the studio's UI thread is the plug-in host's main thread, as in every DAW. A plug-in's editor
// window is made on the thread of its parent window, and the plug-ins (JUCE ones above all) make and use their windows, timers and
// message handling on the thread they treat as main; with one thread for both nothing crosses threads. The headless host and the
// tests have no window and keep the host's own thread (Machines/NeoPlug/PlugMain.cs).
//
// Calls from the UI thread run at once. Calls from other threads (the agent, a loader, a tool) are queued to the UI thread and
// wait for it, so the UI thread must never wait for such a thread while holding what it needs.

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Buzz.MachineInterface;

namespace ReBuzz.Studio.Platform
{
    internal sealed class StudioPluginMainThread : IPluginMainThread
    {
        public bool IsCurrent => Dispatcher.UIThread.CheckAccess();

        public void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Normal);

        public T Invoke<T>(Func<T> f, int timeoutMs)
        {
            if (IsCurrent) return f();
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
            {
                try { done.SetResult(f()); }
                catch (Exception e) { done.SetException(e); }
            }, DispatcherPriority.Normal);
            if (!done.Task.Wait(timeoutMs)) throw new TimeoutException("the UI thread did not answer");
            return done.Task.Result;
        }

        public IDisposable Every(int intervalMs, Action tick)
        {
            DispatcherTimer timer = null;
            void Make()
            {
                timer = new DispatcherTimer(TimeSpan.FromMilliseconds(intervalMs), DispatcherPriority.Normal, (_, _) => tick());
                timer.Start();
            }
            if (IsCurrent) Make();
            else Dispatcher.UIThread.Post(Make, DispatcherPriority.Normal);
            return new Stop(() => { if (IsCurrent) timer?.Stop(); else Dispatcher.UIThread.Post(() => timer?.Stop()); });
        }

        sealed class Stop : IDisposable
        {
            readonly Action stop;
            public Stop(Action stop) { this.stop = stop; }
            public void Dispose() => stop();
        }
    }
}
