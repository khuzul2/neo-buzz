// Fork (docs/specs/mcp-server.md §12.5, U5): the machine menu's "Replace with <successor>..." in the studio.
// A classic machine with a Neo successor offers it; choosing it runs machine_replace's code (settings in
// units, connections, patterns), shows the comparison, and is one undo step.

using System.Linq;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class ReplaceMenuTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        [AvaloniaTest]
        public void TheMachineMenuReplacesAClassicMachineWithItsSuccessorInOneUndoStep()
        {
            var (window, view) = OpenEmpty();
            string said = null;
            Dialogs.Responder = (m, c, b) => { if (c.StartsWith("Replaced")) said = m; return UserAnswer.No; };
            try
            {
                var song = Core.SongCore;
                song.CreateMachine("Pedal Gain", null, "Trim", null, null, null, -1, 0.1f, -0.2f);
                var trim = song.MachinesList.Last(m => m.Name == "Trim");
                song.ConnectMachines(trim, Machine("Master"), new[] { 0 }, new[] { 0 }, 0x2000, 0x4000);
                var gain = trim.ParameterGroups[1].Parameters.First(p => p.Name == "Gain");
                gain.SetValue(0, 50);
                Settle();

                Click(window, view.PointOf(trim, window), MouseButton.Right);
                var menu = view.Canvas.OpenMenu;
                Assert.That(menu?.IsOpen, Is.True);
                var item = menu!.Items.OfType<Avalonia.Controls.MenuItem>().FirstOrDefault(x => x.Header as string == "Replace with Neo Utility...");
                Assert.That(item, Is.Not.Null, "a machine with a successor offers it: " + string.Join(", ", menu.Items.OfType<Avalonia.Controls.MenuItem>().Select(x => x.Header)));
                Studio.Screenshot(Avalonia.Controls.TopLevel.GetTopLevel(item!)!, "replace-menu");
                ClickItem(item!);
                Settle();

                var replaced = (MachineCore)Machine("Trim");
                Assert.That(replaced.DLL.Name, Is.EqualTo("Neo Utility"), "the successor takes its place and name");
                Assert.That(Graph(), Is.EqualTo("Master; Trim; Trim→Master"));
                Assert.That(Wire("Trim", "Master").Amp, Is.EqualTo(0x2000), "its connection as it was");
                var newGain = replaced.ParameterGroups[1].Parameters.First(p => p.Name == "Gain");
                Assert.That(newGain.DescribeValue(newGain.GetValue(0)), Does.Contain("-6.0").Or.Contain("−6.0"), "50 % is −6.0 dB");
                Assert.That(said, Does.StartWith("Replaced Trim (Pedal Gain) with Trim (Neo Utility)"), "the comparison is shown");
                Assert.That(said, Does.Contain("within its tolerance").Or.Contain("not compared").Or.Contain("silent"));
                // One undo step brings the original back.
                song.ActionStack.Undo();
                Settle();
                var back = (MachineCore)Machine("Trim");
                Assert.That(back.DLL.Name, Is.EqualTo("Pedal Gain"), "undo brings the original back");
                Assert.That(back.ParameterGroups[1].Parameters.First(p => p.Name == "Gain").GetValue(0), Is.EqualTo(50));
                Assert.That(Graph(), Is.EqualTo("Master; Trim; Trim→Master"));
            }
            finally
            {
                Close(window);
            }
        }
    }
}
