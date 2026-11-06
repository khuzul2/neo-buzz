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
                Assert.That(window.RecordCommand.CanExecute(null), Is.False, "Record waits for a device");
                Assert.That(window.RenderLoopCommand.CanExecute(null), Is.False, "Render Loop too (it renders through the device's provider)");
            }
            finally
            {
                window.Close();
            }
        }
    }
}
