// Fork (docs/specs/studio-mvp.md S6): the portable editor's dispatcher, in place of WPF's. The shared
// sources call Editor.Dispatcher.Invoke/BeginInvoke to edit from other threads (recording). It runs
// work on the thread that made the editor, through that thread's SynchronizationContext (the studio's
// UI thread); without one (rebuzz-headless) the work runs where it is asked.

namespace WDE.ModernPatternEditor
{
    public sealed class EditorDispatcher
    {
        readonly SynchronizationContext context = SynchronizationContext.Current;
        readonly int thread = Environment.CurrentManagedThreadId;

        public bool CheckAccess() => context == null || Environment.CurrentManagedThreadId == thread;

        public void Invoke(Action action)
        {
            if (CheckAccess())
                action();
            else
                context.Send(_ => action(), null);
        }

        public void BeginInvoke(Action action)
        {
            if (context == null)
                action();
            else
                context.Post(_ => action(), null);
        }
    }
}
