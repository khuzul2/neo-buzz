// Fork (docs/specs/ux-overhaul.md P6): the studio's Windows preview, the parts a test can reach without a sound card or a
// MIDI device: the settings folder, the MIDI backend that lists devices without failing, and no native machine hosts
// where a folder doesn't have them (the test output has no bin32 or bin64).

using ReBuzz.Audio;
using ReBuzz.Host;
using ReBuzz.Platform;
using ReBuzz.Midi;
using ReBuzz.Studio.Shell;
using ReBuzzTests.Automation;

namespace ReBuzz.Studio.Tests
{
    public class WindowsPreviewTests
    {
        [Test]
        public void TheStudioKeepsItsSettingsInItsOwnFolderOnWindows()
        {
            string folder = StudioHost.DefaultSettingsFolder;
            if (OperatingSystem.IsWindows())
            {
                Assert.That(Path.GetFileName(folder), Is.EqualTo("NeoBuzz Live"));
                Assert.That(folder, Does.StartWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
                Assert.That(folder, Is.Not.EqualTo(ReBuzz.Platform.JsonRegistry.SettingsFolder), "not the WPF app's %APPDATA%\\ReBuzz");
                Assert.That(StudioHost.AppTitle, Is.EqualTo("NeoBuzz Live Studio (preview)"));
            }
            else
            {
                Assert.That(folder, Is.EqualTo(ReBuzz.Platform.JsonRegistry.SettingsFolder));
                Assert.That(StudioHost.AppTitle, Is.EqualTo("NeoBuzz Live"));
            }
        }

        [Test]
        public void TheWinMmBackendListsItsDevicesAndWatchesWithoutFailing()
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("WinMM is Windows' MIDI");
            var backend = new WinMmMidiBackend(_ => { }, null);
            for (int i = 0; i < backend.InputCount; i++) Assert.That(backend.InputName(i), Is.Not.Null);
            for (int i = 0; i < backend.OutputCount; i++) Assert.That(backend.OutputName(i), Is.Not.Null);
            Assert.That(backend.CreateMidi2Endpoint(), Is.False, "no Windows MIDI Services endpoint in the preview");
            backend.StartWatching(_ => { });
            backend.StopWatching();
        }

        [Test]
        public void NoNativeMachineHostsStartWhereTheFolderHasNone()
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("native machines are Windows DLLs");
            bool installed = File.Exists(Path.Combine(AppContext.BaseDirectory, "bin64", "ReBuzzEngine64.exe")) || File.Exists(Path.Combine(AppContext.BaseDirectory, "bin32", "ReBuzzEngine32.exe"));
            Assert.That(ReBuzz.NativeMachine.NativeMachineHost.Supported, Is.EqualTo(installed));
        }

        // ---- Windows audio (ux-overhaul.md P6, decision 3: the user's choice, 2026-11-13) ----------------------------------------

        static JsonRegistry Settings() => new JsonRegistry(TestTemp.Path("neobuzz-winaudio-" + Guid.NewGuid().ToString("N") + ".json"), @"Software\ReBuzz");

        static string Default(WindowsHostAudioDriverFactory factory, string saved = "WASAPI") =>
            new AudioDeviceService(new ConsoleUserMessages(), factory).ResolveDevice(saved).Name;

        [Test]
        public void WithAnAsioDriverInstalledTheDefaultIsAsioOtherwiseWasapiSharedWithLowLatency()
        {
            var registry = Settings();
            var withAsio = new WindowsHostAudioDriverFactory(registry, () => new[] { "Focusrite USB ASIO", "ASIO4ALL v2" });
            Assert.That(Default(withAsio), Is.EqualTo("Focusrite USB ASIO"), "ASIO present: the first driver");
            Assert.That(Default(withAsio, "gone device"), Is.EqualTo("Focusrite USB ASIO"));
            Assert.That(Default(withAsio, WasapiModes.Shared), Is.EqualTo(WasapiModes.Shared), "a saved choice stays");

            var without = new WindowsHostAudioDriverFactory(registry, () => Array.Empty<string>());
            Assert.That(Default(without), Is.EqualTo(WasapiModes.SharedLowLatency), "no ASIO: WASAPI shared, low latency");
            var names = without.OutputDevices().Select(d => d.Name).ToList();
            Assert.That(names, Is.EqualTo(new[] { WasapiModes.SharedLowLatency, WasapiModes.Shared }), "no exclusive, no DirectSound or MME, no WDM-KS until asked for");
        }

        [Test]
        public void ExclusiveModeIsOfferedOnlyBehindItsSwitch()
        {
            var registry = Settings();
            var factory = new WindowsHostAudioDriverFactory(registry, () => Array.Empty<string>());
            registry.Write(WindowsHostAudioDriverFactory.ExclusiveKey, 1, "Settings");
            Assert.That(factory.OutputDevices().Select(d => d.Name), Is.EqualTo(new[] { WasapiModes.Exclusive }));
            Assert.That(WasapiModes.Exclusive, Does.Contain("other apps go silent"));
        }
    }
}
