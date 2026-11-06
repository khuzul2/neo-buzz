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
    }
}
