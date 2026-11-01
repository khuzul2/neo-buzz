using System;
using ReBuzz.Core;

namespace ReBuzzTests.Automation
{
    /// <summary>
    /// A test-only dispatcher that does not depend on the System.Windows.
    /// More suitable for tests as there can only be one Application object in a process,
    /// and we create a new instance of ReBuzzCore (which closes the Application on exit) for each test.
    /// Also, not depending on the Application.Current.Dispatcher removes the necessity to declare
    /// the tests as [STAThread].
    /// </summary>
    public class FakeDispatcher : IUiDispatcher
    {
        readonly System.Collections.Generic.Queue<Action> pending = new();

        /// <summary>Fork: synchronous Invoke calls so far (engine threads must make none).</summary>
        public int InvokeCount { get; private set; }

        /// <summary>
        /// Fork: when true, BeginInvoke queues the action until <see cref="RunPending"/>, as the
        /// real dispatcher would run it later; when false (default) it runs at once.
        /// </summary>
        public bool Deferred { get; set; }

        public void Invoke(Action action)
        {
            InvokeCount++;
            action();
        }

        public void BeginInvoke(Action action)
        {
            if (Deferred)
                pending.Enqueue(action);
            else
                action();
        }

        public void BeginInvoke(Action action, UiPriority priority)
        {
            // this is a little stretch, but BeginInvoke does not guarantee when
            // the action will be executed, so might as well get executed immediately.
            BeginInvoke(action);
        }

        public bool CheckAccess() => true;

        /// <summary>Fork: runs the queued actions (and any they queue); returns how many ran.</summary>
        public int RunPending()
        {
            int count = 0;
            while (pending.Count > 0)
            {
                pending.Dequeue()();
                count++;
            }
            return count;
        }
    }
}