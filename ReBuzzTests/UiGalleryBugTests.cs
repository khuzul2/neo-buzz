using BuzzGUI.Common;
using BuzzGUI.MachineView;
using BuzzGUI.MachineView.HDRecorder;
using ReBuzz.Common;
using ReBuzz.Core;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/ui-gallery.md): the Windows app's bugs the UI gallery's capture found, each pinned where
    /// it was: View ▸ Settings… with a free-text setting (Agent's Port), the Hard Disk Recorder with no audio device, and
    /// the Machine View made again (a theme switch) with a song open.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class UiGalleryBugTests
    {
        internal static string FindRunTime()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var runTime = Path.Combine(dir.FullName, "RunTime");
                if (File.Exists(Path.Combine(runTime, "Themes", "Default", "MachineView", "MachineView.xaml"))) return runTime;
            }
            throw new DirectoryNotFoundException("RunTime/Themes above " + TestContext.CurrentContext.TestDirectory);
        }

        [Test]
        public void TheSettingsWindowOpensWithAFreeTextSettingAndEditsIt()
        {
            // AddTab made a drop-down of every setting's Options; Agent's Port (a string) has none: View ▸ Settings… threw
            // a NullReferenceException once the Agent page was registered, as it is whenever the app runs.
            const string header = "Agent (test)";
            var agent = new ReBuzz.Agent.AgentSettings();
            string before = agent.Port;
            SettingsWindow.AddSettings(header, agent);
            try
            {
                var window = new SettingsWindow();
                var tabs = (TabControl)window.Content;
                var tab = tabs.Items.OfType<TabItem>().Single(t => (string)t.Header == header);
                var grid = (Grid)((ScrollViewer)tab.Content).Content;
                Assert.That(grid.Children.OfType<ComboBox>().Count(), Is.EqualTo(1), "AllowAgents: a choice of its options");
                var port = grid.Children.OfType<TextBox>().Single();
                Assert.That(port.Text, Is.EqualTo(before), "Port: a text box with its value");

                port.Text = "7500";
                port.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.That(agent.Port, Is.EqualTo("7500"), "what is typed is the setting's value");
                port.Text = before;
                port.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.That(agent.Port, Is.EqualTo(before));
            }
            finally
            {
                SettingsWindow.RemoveSettings(header);
            }
        }

        [Test]
        public void TheHardDiskRecorderOpensWithoutAnAudioDeviceAndSaysSo()
        {
            // ReBuzzCore.OutputChannels asked the audio provider of a device that isn't there (NullReferenceException), and
            // the window's constructor asks it: View ▸ Hard Disk Recorder threw on a machine with no audio output.
            using var driver = new Driver();
            driver.Start();
            Assert.That(driver.Core.AudioEngine.SelectedOutDevice, Is.Null, "the tests have no audio device");
            Assert.That(driver.Core.OutputChannels, Is.EqualTo(0), "no output channels without a device");

            var window = new HDRecorderWindow();
            try
            {
                Assert.That(window.HasOutput, Is.False);
                Assert.That(window.NoOutputText, Does.Contain("No audio output"), "it says why it can't record");
                Assert.That(window.RecordCommand.CanExecute(null!), Is.False, "Record waits for a device");
                Assert.That(window.RenderLoopCommand.CanExecute(null!), Is.False, "Render Loop too (it renders through the device's provider)");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void TheMachineViewMadeAgainWithASongOpenLeavesOutWiresItCannotDraw()
        {
            // A theme switch makes the Machine View again and adds every shown machine's inputs. An input from a machine
            // the view doesn't show (one that isn't ready: a machine whose start didn't finish in time is left so when a
            // song opens, and a deleted one on its way out) had no control: Connection.UpdateVisuals threw a
            // NullReferenceException, and the theme switch failed with the song open.
            using var driver = new Driver();
            var synth = SynthController.NewInstance("Wired");
            driver.Gear.AddDynamicGenerator(SynthController.Info);
            driver.Start();
            driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(synth);
            var machine = driver.MachineFor(synth);
            var song = driver.Core.SongCore;

            var rd = UiUtils.GetUserControlXAML<ResourceDictionary>("MachineView\\MachineView.xaml", FindRunTime());
            var view = new MachineView(driver.Core, rd);
            try
            {
                view.MachineGraph = song;
                Assert.That(view.Connections.Count(), Is.EqualTo(1), "the wire to the Master");
                view.MachineGraph = null!;

                machine.Ready = false;
                Assert.DoesNotThrow(() => view.MachineGraph = song, "the view made again with the song open");
                Assert.That(view.Machines.Select(m => m.Machine), Does.Not.Contain(machine), "a machine that isn't ready isn't shown");
                Assert.That(view.Connections, Is.Empty, "nor its wire");
                view.MachineGraph = null!;
                machine.Ready = true;
                view.MachineGraph = song;
                Assert.That(view.Connections.Count(), Is.EqualTo(1), "shown again once it is");
            }
            finally
            {
                machine.Ready = true;
                view.MachineGraph = null!;
                view.Release();
            }
        }

        [Test]
        public void TheMainWindowIsKeptWithinTheScreensWorkArea()
        {
            // Fork (UX review): the window's default 1500×800 (and a size saved on a larger screen) ran off a small screen
            // and under the taskbar. As it loads, it is moved and shrunk into its screen's work area.
            var area = new Rect(0, 0, 1024, 728);           // a 1024×768 screen above a 40-pixel taskbar
            Assert.That(UiUtils.FitToWorkArea(new Rect(0, 0, 1500, 800), area), Is.EqualTo(area), "the default size on a small screen");
            Assert.That(UiUtils.FitToWorkArea(new Rect(100, 50, 600, 400), area), Is.EqualTo(new Rect(100, 50, 600, 400)), "a window that fits stays");
            Assert.That(UiUtils.FitToWorkArea(new Rect(900, 500, 600, 400), area), Is.EqualTo(new Rect(424, 328, 600, 400)), "one that hangs off is moved in");

            var window = new Window { Width = 20000, Height = 20000, Left = 50, Top = 50, WindowStartupLocation = WindowStartupLocation.Manual };
            UiUtils.KeepOnWorkArea(window);
            var work = SystemParameters.WorkArea;           // the primary screen's (the window has no screen of its own yet)
            Assert.That(window.Width, Is.LessThanOrEqualTo(work.Width + 1));
            Assert.That(window.Height, Is.LessThanOrEqualTo(work.Height + 1));
            Assert.That(window.Left, Is.GreaterThanOrEqualTo(work.Left - 1).And.LessThanOrEqualTo(work.Right - window.Width + 1));
            Assert.That(window.Top, Is.GreaterThanOrEqualTo(work.Top - 1).And.LessThanOrEqualTo(work.Bottom - window.Height + 1));
        }
    }
}
