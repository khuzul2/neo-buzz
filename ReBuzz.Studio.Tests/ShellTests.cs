// Fork (docs/specs/studio-mvp.md S0): the main window, the transport and song files, headless.

using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class ShellTests
    {
        static MainWindow Open()
        {
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            return window;
        }

        [AvaloniaTest]
        public void TheEngineStartsWithTheNeoMachinesAndTheWindowShowsItsViews()
        {
            var host = Studio.Host;
            Assert.That(host.Core, Is.Not.Null);
            Assert.That(host.Core.MachineDLLs.Keys, Does.Contain("Neo Poly").And.Contain("Neo Verb"), "the portable Neo machines are installed");
            var window = Open();
            Assert.That(window.Tabs.Items.Cast<TabItem>().Select(t => (string)t.Header), Is.EqualTo(new[] { "Machines", "Sequence", "Session", "Pattern" }));
            Assert.That(window.StatusText, Does.Contain("no audio output"));
            Studio.Screenshot(window, "studio-main");
            window.Close();
        }

        [AvaloniaTest]
        public void PlayAndStopDriveTheTransport()
        {
            var host = Studio.Host;
            var window = Open();
            window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
            Assert.That(host.Core.PlayRequested, Is.True, "F5 plays");
            window.Transport.Stop();
            Assert.That(host.Core.PlayRequested, Is.False, "stop stops");
            window.Close();
        }

        [AvaloniaTest]
        public void SaveAsThenOpenGivesTheSongBack()
        {
            var host = Studio.Host;
            var core = host.Core;
            string dir = Path.Combine(Path.GetTempPath(), "rebuzz-studio-songs-" + System.Environment.ProcessId);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "round trip.bmxml");
            var window = Open();
            try
            {
                SongFiles.Picker = save => path;
                Dialogs.Responder = (m, c, b) => UserAnswer.No;     // never keep unsaved changes here
                core.BPM = 137;
                Assert.That(SongFiles.SaveAs().Result, Is.True);
                Assert.That(File.Exists(path), Is.True);
                Assert.That(core.SongCore.SongName, Is.EqualTo(path));
                Assert.That(core.Modified, Is.False);

                SongFiles.New().Wait();
                Assert.That(core.SongCore.SongName, Is.Null, "a new song has no file");
                Assert.That(core.BPM, Is.Not.EqualTo(137));

                Assert.That(SongFiles.Open(path).Result, Is.True);
                Assert.That(core.BPM, Is.EqualTo(137));
                Assert.That(core.SongCore.SongName, Is.EqualTo(path));
                Studio.Screenshot(window, "studio-song-open");
                Assert.That(window.Title, Does.StartWith("round trip.bmxml"));
            }
            finally
            {
                SongFiles.Picker = null;
                Dialogs.Responder = null;
                window.Close();
            }
        }
    }
}

namespace ReBuzz.Studio.Tests
{
    public class MachineWindowTests
    {
        [AvaloniaTest]
        public void AMachinesWindowsOpenThroughTheEngineAndCloseWithIt()
        {
            var core = Studio.Host.Core;
            core.SongCore.CreateMachine("Neo Poly", null, "Window Poly", null, null, null, -1, 0.2f, 0.2f);
            var machine = core.SongCore.MachinesList.First(m => m.Name == "Window Poly");
            // Neo Poly's GUI is its editor (S1, NeoEditorTests); its parameter window is its own.
            core.UiHost.OpenGui(machine, null);
            var window = ReBuzz.Studio.Platform.StudioMachineUiHost.GuiWindowOf(machine);
            Assert.That(window, Is.Not.Null);
            Assert.That(window!.IsVisible, Is.True);
            Assert.That(core.UiHost.GetWindowState(machine).GuiWindowVisible, Is.True, "songs save it as open");
            core.UiHost.ReleaseMachine(machine);
            Assert.That(window.IsVisible, Is.False);
            core.SongCore.DeleteMachines(new[] { machine });
        }
    }
}
