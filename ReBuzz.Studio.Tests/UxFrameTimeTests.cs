// Fork (docs/specs/ux-overhaul.md §8): frame times of the main window, measured headless on the demo song, so each phase of
// the UX overhaul reports figures against the phase before it: the window drawn whole (Machines, Sequence, the Session View
// while it plays), the window's status refresh (ten a second), and the Session View's state refresh (twenty a second).
//   dotnet test ReBuzz.Studio.Tests -c Release --filter TestCategory=UxMeasure

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Tests
{
    [Category("UxMeasure")]
    [Explicit("Frame times for the UX overhaul's reports (ux-overhaul.md §8): dotnet test --filter TestCategory=UxMeasure")]
    public class UxFrameTimeTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>The median of <paramref name="n"/> runs of <paramref name="run"/>, in milliseconds (after 5 to warm up).</summary>
        static double Median(int n, System.Action run)
        {
            for (int i = 0; i < 5; i++) run();
            var times = new List<double>();
            var clock = new Stopwatch();
            for (int i = 0; i < n; i++)
            {
                clock.Restart();
                run();
                times.Add(clock.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            return times[times.Count / 2];
        }

        [AvaloniaTest]
        public void MainWindowFrameTimes()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.Modified = false;
            Core.ExecuteCommand(BuzzGUI.Interfaces.BuzzCommand.NewFile);
            Settle();
            ReBuzz.Gallery.GallerySong.Build(Core);
            Core.Modified = false;
            var window = new MainWindow(Studio.Host) { Width = 1440, Height = 900 };
            window.Show();
            Settle();
            var lines = new List<string>();
            try
            {
                using var target = new RenderTargetBitmap(new PixelSize(1440, 900));
                double Frame(string view)
                {
                    window.ShowView(view);
                    Settle();
                    return Median(30, () =>
                    {
                        window.UpdateLayout();
                        target.Render(window);          // the whole visual tree, as a frame draws it
                    });
                }
                lines.Add($"draw Machines: {Frame("Machines"):0.00} ms");
                lines.Add($"draw Sequence: {Frame("Sequence"):0.00} ms");
                window.ShowView("Session");
                Settle();
                var view = window.Tabs.Items.OfType<Avalonia.Controls.TabItem>().Select(t => t.Content).OfType<SessionView>().First();
                view.LaunchScene(2);
                Core.Playing = true;
                Settle();
                lines.Add($"draw Session (a scene launched): {Frame("Session"):0.00} ms");
                lines.Add($"Session state refresh: {Median(200, view.RefreshState):0.000} ms");
                lines.Add($"status refresh: {Median(200, window.RefreshNow):0.000} ms");
            }
            finally
            {
                Core.Playing = false;
                Core.Modified = false;
                Dialogs.Responder = null;
                window.Close();
            }
            foreach (var l in lines) TestContext.Out.WriteLine("[ux-measure] " + l);
        }
    }
}
