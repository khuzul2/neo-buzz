using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.AppViews;
using ReBuzz.Core;
using ReBuzzTests.Automation;

namespace ReBuzzTests
{
    /// <summary>
    /// docs/specs/engine-split.md S3: machine windows belong to the UI host (WpfMachineUiHost in the
    /// app). Without a host the engine remembers which windows a song wants open, so saving and
    /// loading the song keeps them.
    /// </summary>
    public class MachineUiHostTests
    {
        static MachineCore MasterOf(Driver driver) =>
            driver.Core.SongCore.MachinesList.Single(m => m.DLL.Info.Type == MachineType.Master);

        [TestCase("")]
        [TestCase("ml")]   // .bmxml
        public void SongKeepsMachineWindowsWithoutAHost(string extension)
        {
            using var driver = new Driver();
            driver.Start();
            driver.Core.UiHost = null;
            string path = driver.RandomSongPath().ToString() + extension;

            var master = MasterOf(driver);
            master.OpenParameterWindow(new WindowPlacement(10, 20, 300, 200));
            var wanted = new MachineWindowState(true, new WindowPlacement(10, 20, 300, 200), false, default);
            Assert.That(master.GetWindowState(), Is.EqualTo(wanted));

            driver.DawCommands.SaveCurrentSongForTheFirstTime(DialogChoices.Select(path));
            master.CloseWindows();
            Assert.That(master.GetWindowState().ParameterWindowVisible, Is.False);
            driver.DawCommands.LoadSong(DialogChoices.Select(path));

            Assert.That(MasterOf(driver).GetWindowState(), Is.EqualTo(wanted));
            driver.AssertNoErrorsReportedToUser();
        }

        [Test]
        public void ReopeningWithoutAPlacementKeepsTheLastOne()
        {
            using var driver = new Driver();
            driver.Start();
            driver.Core.UiHost = null;
            var master = MasterOf(driver);

            master.OpenParameterWindow(new WindowPlacement(1, 2, 3, 4));
            master.CloseWindows();
            master.OpenParameterWindow();

            Assert.That(master.GetWindowState(), Is.EqualTo(new MachineWindowState(true, new WindowPlacement(1, 2, 3, 4), false, default)));
        }

        [Test]
        public void TheAppHostIsEveryCoresDefault()
        {
            using var driver = new Driver();
            driver.Start();   // installs the hooks, as App does

            Assert.That(driver.Core.UiHost, Is.InstanceOf<WpfMachineUiHost>());
            var master = MasterOf(driver);
            Assert.That(((IMachine)master).ParameterWindow, Is.Null, "no window until opened");
            Assert.That(master.GetWindowState(), Is.EqualTo(default(MachineWindowState)));
            driver.Core.UiHost.ReleaseMachine(master);   // no windows: nothing to let go
        }
    }
}
