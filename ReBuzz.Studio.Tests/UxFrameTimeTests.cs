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
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Session;
using BuzzGUI.Interfaces;

namespace ReBuzz.Studio.Tests
{
    [Category("UxMeasure")]
    [Explicit("Frame times for the UX overhaul's reports (ux-overhaul.md §8): dotnet test --filter TestCategory=UxMeasure")]
    public class UxFrameTimeTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        /// <summary>The machine's 1-minute load average ("n/a" where /proc has none: macOS).</summary>
        static string Load()
        {
            try { return System.IO.File.ReadAllText("/proc/loadavg").Split(' ')[0]; }
            catch (System.Exception) { return "n/a"; }
        }

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

        /// <summary>
        /// P3g (decision 82): the Machine View on the 36-machine song (every Neo and Pedal machine, gallery 510), drawn whole with the meters off
        /// (what it cost before), on and silent, and on with a level through every wire; and what taking the peaks costs (it runs thirty times a second).
        /// </summary>
        [AvaloniaTest]
        public void ManyMachinesFrameTimes()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.Modified = false;
            Core.ExecuteCommand(BuzzGUI.Interfaces.BuzzCommand.NewFile);
            Settle();
            var gear = Core.MachineDLLs.Values.Where(d => d?.Info != null && d.Info.Type != MachineType.Master && (d.Name.StartsWith("Neo ") || d.Name == "Dye Essence" || d.Name.StartsWith("Pedal ")))
                .Select(d => d.Name).OrderBy(x => x).ToList();
            int cols = 6;
            for (int k = 0; k < gear.Count; k++)
            {
                float x = -0.9f + 1.6f * (k % cols) / (cols - 1), y = -0.85f + 1.6f * (k / cols) / System.Math.Max(1, (gear.Count - 1) / cols);
                var instrument = Core.Instruments.First(i => i.MachineDLL?.Name == gear[k]);
                MachineEdits.Create(Core, instrument, x, y);
            }
            // Every machine into the Master, so there is a wire to draw for each (the song a big project has).
            var master = Core.SongCore.Machines.First(m => m.DLL.Info.Type == MachineType.Master);
            foreach (var m in Core.SongCore.Machines.ToList())
                if (m != master) MachineEdits.Connect(Core, m, master);
            Core.Modified = false;
            var window = new MainWindow(Studio.Host) { Width = 1440, Height = 900 };
            window.Show();
            Settle();
            var lines = new List<string>();
            try
            {
                window.ShowView("Machines");
                Settle();
                var canvas = window.Tabs.Items.OfType<Avalonia.Controls.TabItem>().Select(t => t.Content).OfType<MachineView>().First().Canvas;
                int wires = canvas.Wires.Count();
                lines.Add($"machines {Core.SongCore.Machines.Count}, wires {wires}");
                using var target = new RenderTargetBitmap(new PixelSize(1440, 900));
                double Frame(string what)
                {
                    Settle();
                    double ms = Median(40, () =>
                    {
                        canvas.InvalidateVisual();
                        window.UpdateLayout();
                        target.Render(window);
                    });
                    lines.Add($"draw Machines, {what}: {ms:0.00} ms  (load {Load()})");
                    return ms;
                }
                var wireList = canvas.Wires.ToList();
                int n = 0;
                var level = new Dictionary<IMachineConnection, float>();
                foreach (var c in wireList) level[c] = 32768f * (float)System.Math.Pow(10, -(n++ % 12) * 4.0 / 20);   // 0 to -44 dB across the wires
                // Alternated, twice, so a drifting load or a warm-up doesn't fall on one of them.
                for (int round = 1; round <= 2; round++)
                {
                    canvas.MetersShown = false;
                    Frame($"meters off (round {round})");
                    canvas.MetersShown = true;
                    canvas.PeakSource = c => (0, 0);
                    canvas.TickMeters(1);
                    Frame($"meters on, silent (round {round})");
                    canvas.PeakSource = c => (level[c], level[c] * 0.8f);
                    canvas.TickMeters(1);
                    Frame($"meters on, a level on every wire (round {round})");
                }
                lines.Add($"taking the peaks (30 a second): {Median(300, () => canvas.TickMeters(1.0 / 30)):0.000} ms");
            }
            finally
            {
                Core.Modified = false;
                Dialogs.Responder = null;
                window.Close();
            }
            foreach (var l in lines) TestContext.Out.WriteLine("[ux-measure] " + l);
        }
    }
}
