// Fork (docs/specs/studio-mvp.md §7, test reliability): no window outlives its test. A test that ended with
// a window open (a main window closed on a song with changes asks whether to save them, and nobody answers
// in a test) left it, and the question, open for the rest of the run: rendered with every input after, its
// timers ticking, its views following the engine. Here what a test leaves open is closed after it (changes
// dropped), and named in the log so that the test can be put right.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

[assembly: ReBuzz.Studio.Tests.ClosesWindowsLeftOpen]

namespace ReBuzz.Studio.Tests
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class ClosesWindowsLeftOpenAttribute : Attribute, ITestAction
    {
        static readonly List<WeakReference<Window>> opened = new();
        static bool watching;

        public ActionTargets Targets => ActionTargets.Test;

        public void BeforeTest(ITest test)
        {
            if (watching) return;
            watching = true;
            Window.WindowOpenedEvent.AddClassHandler<Window>((w, e) => opened.Add(new WeakReference<Window>(w)), RoutingStrategies.Direct, handledEventsToo: true);
        }

        public void AfterTest(ITest test)
        {
            if (!Dispatcher.UIThread.CheckAccess()) return;      // a plain [Test]: no windows
            var left = Open();
            if (left.Count == 0) return;
            TestContext.Progress.WriteLine($"[test windows] {test.Name} left {left.Count} open ({string.Join(", ", left.Select(w => $"{w.GetType().Name} \"{w.Title}\""))}): closed after it");
            var responder = Dialogs.Responder;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;      // a main window's song: its changes go
            try
            {
                // The questions first (they answer Cancel as they close), then the windows that asked them.
                foreach (var w in left.OrderBy(w => w is MainWindow ? 1 : 0))
                {
                    w.Close();
                    MachineViewTests.Settle();
                }
            }
            finally
            {
                Dialogs.Responder = responder;
            }
            var still = Open();
            if (still.Count > 0)
                TestContext.Progress.WriteLine($"[test windows] still open after {test.Name}: {string.Join(", ", still.Select(w => $"{w.GetType().Name} \"{w.Title}\""))}");
        }

        static List<Window> Open()
        {
            opened.RemoveAll(r => !r.TryGetTarget(out var w) || !w.IsVisible);
            return opened.Select(r => r.TryGetTarget(out var w) ? w : null).Where(w => w != null && w.IsVisible).ToList();
        }
    }
}
