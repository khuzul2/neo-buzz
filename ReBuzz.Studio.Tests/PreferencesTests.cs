// Fork (docs/specs/studio-mvp.md S7): Preferences, headless. Each page renders (a screenshot each)
// and its settings round-trip: chosen on the page, written to the settings, shown again when the
// window opens anew. The audio device choice writes the registry and reopens the device; MIDI
// devices open and follow plugging (a stand-in MIDI backend); sync and the generic [BuzzSetting]
// pages change the engine's settings.

using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Midi;
using ReBuzz.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Preferences;

namespace ReBuzz.Studio.Tests
{
    public class PreferencesTests
    {
        static StudioHost Host => Studio.Host;

        static void Settle(Window window = null)
        {
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        static PreferencesWindow Open(string page)
        {
            var prefs = PreferencesWindow.Open(Host, page);
            Assert.That(prefs, Is.Not.Null);
            Assert.That(prefs.Selected?.Title, Is.EqualTo(page));
            Settle(prefs);
            return prefs;
        }

        static void Close(PreferencesWindow prefs)
        {
            prefs.Close();
            Settle();
            Assert.That(PreferencesWindow.Current, Is.Null);
        }

        [TearDown]
        public void CloseWhatIsOpen()
        {
            PreferencesWindow.Current?.Close();
            AudioPage.OutputSource = null;
            AudioPage.InputSource = null;
            AudioPage.ReopenHook = null;
            Settle();
        }

        // ------------------------------------------------------------------ Audio

        [AvaloniaTest]
        public void AudioChoicesWriteTheRegistryAndReopenTheDevice()
        {
            var registry = Host.Registry;
            const string monitors = "PortAudio: Studio Monitors (Core Audio)", mic = "PortAudio: Studio Mic (Core Audio)";
            AudioPage.OutputSource = () => new[] { "PortAudio: default", monitors };
            AudioPage.InputSource = () => new[] { mic };
            var reopened = new List<string>();
            AudioPage.ReopenHook = reopened.Add;
            try
            {
                var prefs = Open("Audio");
                var page = prefs.Page<AudioPage>();
                Assert.That(page.OutputBox.ItemCount, Is.EqualTo(2));
                Assert.That(page.OutputBox.SelectedIndex, Is.EqualTo(0), "nothing saved: the first device, as the engine falls back to");
                Studio.Screenshot(prefs, "prefs-audio");

                page.OutputBox.SelectedIndex = 1;
                Assert.That(registry.Read("AudioDriver", "", "Settings"), Is.EqualTo(monitors));
                Assert.That(reopened, Is.EqualTo(new[] { monitors }), "the chosen device opens");
                page.InputBox.SelectedIndex = 2;            // None, Default input, then the devices
                Assert.That(registry.Read("Input", "", PortAudioDriver.Section), Is.EqualTo(mic));
                page.RateBox.SelectedIndex = 2;
                Assert.That(registry.Read("SampleRate", 0, PortAudioDriver.Section), Is.EqualTo(48000));
                page.BufferBox.SelectedIndex = 2;
                Assert.That(registry.Read("BufferSize", 0, PortAudioDriver.Section), Is.EqualTo(128));
                page.ThreadsBox.SelectedIndex = 1;
                Assert.That(registry.Read("AudioThreads", 0, "Settings"), Is.EqualTo(2));
                page.AlgorithmBox.SelectedIndex = 2;
                Assert.That(registry.Read("WorkAlgorithm", 0, "Settings"), Is.EqualTo(2));
                Assert.That(reopened.Count, Is.EqualTo(6), "each choice opens the device again, which then reads it");
                Assert.That(reopened.Distinct(), Is.EqualTo(new[] { monitors }));
                Close(prefs);

                // Opened again, the page shows what was saved.
                prefs = Open("Audio");
                page = prefs.Page<AudioPage>();
                Assert.That(page.OutputBox.SelectedIndex, Is.EqualTo(1));
                Assert.That(page.InputBox.SelectedItem, Is.EqualTo(mic));
                Assert.That(page.RateBox.SelectedItem, Is.EqualTo("48000 Hz"));
                Assert.That((string)page.BufferBox.SelectedItem, Does.StartWith("128 frames"));
                Assert.That(page.ThreadsBox.SelectedItem, Is.EqualTo("2"));
                Assert.That(page.AlgorithmBox.SelectedItem, Is.EqualTo("Threads"));
                Assert.That(reopened.Count, Is.EqualTo(6), "showing the page changes nothing");

                // A saved device that isn't there now is still shown, as not connected.
                registry.Write("AudioDriver", "PortAudio: Unplugged (Core Audio)", "Settings");
                page.Show();
                Assert.That(page.OutputBox.SelectedItem, Is.EqualTo("PortAudio: Unplugged (Core Audio) (not connected)"));
                Studio.Screenshot(prefs, "prefs-audio-chosen");
            }
            finally
            {
                foreach (var key in new[] { "AudioDriver", "AudioThreads", "WorkAlgorithm" }) registry.DeleteCurrentUserValue(key, "Settings");
                foreach (var key in new[] { "Input", "SampleRate", "BufferSize" }) registry.DeleteCurrentUserValue(key, PortAudioDriver.Section);
            }
        }

        /// <summary>Where PortAudio is (Linux CI has a null sink, the Mac job a device): the device really opens.</summary>
        [AvaloniaTest]
        public void RestartAudioOpensTheDeviceForReal()
        {
            Assume.That(PortAudio.Available, "PortAudio isn't installed here");
            var core = Host.Core;
            // What opening a device changes in the shared engine, put back after: the rate, and the
            // output latency launches and takes are timed by (the Session View's tests expect none).
            int rate = core.SelectedAudioDriverSampleRate;
            int latency = core.OutputLatencySamples;
            var window = new MainWindow(Host) { Width = 1280, Height = 800 };
            window.Show();
            try
            {
                var prefs = Open("Audio");
                var page = prefs.Page<AudioPage>();
                Assume.That(page.OutputBox.ItemCount, Is.GreaterThan(0), "no PortAudio output device here");
                page.Reopen();
                Settle(prefs);
                Assert.That(core.AudioEngine.SelectedOutDevice, Is.Not.Null, "the device opened");
                page.Show();
                Assert.That(page.StatusText, Does.StartWith("Playing through PortAudio: "));
                Studio.Screenshot(prefs, "prefs-audio-open");
                Close(prefs);

                // The status line names the device; the engine's load shows from the second measure.
                for (int i = 0; i < 2; i++)
                {
                    for (int t = 0; t < 10; t++) window.RefreshNow();
                    System.Threading.Thread.Sleep(150);
                }
                window.RefreshNow();
                Assert.That(window.StatusText, Does.StartWith("PortAudio: "));
                Assert.That(window.CpuLoad, Is.Not.Null);
                Assert.That(window.StatusText, Does.Contain("CPU "));
            }
            finally
            {
                core.AudioEngine.FinalStop();
                core.AudioEngine.ReleaseAudioDriver();
                core.SelectedAudioDriverSampleRate = rate;
                core.OutputLatencySamples = latency;
                Host.Registry.DeleteCurrentUserValue("AudioDriver", "Settings");
                window.Close();
                Settle();
            }
        }

        // ------------------------------------------------------------------ MIDI

        /// <summary>MIDI devices that come and go when a test says.</summary>
        sealed class StandInMidi : IMidiBackend
        {
            public readonly List<string> Ins = new List<string> { "Keys" }, Outs = new List<string> { "Synth" };
            public readonly HashSet<string> Opened = new HashSet<string>();
            System.Action<MidiDeviceChange> changed;

            sealed class Port : IMidiInput, IMidiOutput
            {
                readonly StandInMidi owner;
                public Port(StandInMidi owner, string name) { this.owner = owner; ProductName = name; owner.Opened.Add(name); }
                public string ProductName { get; }
                public void Close() => owner.Opened.Remove(ProductName);
                public void Dispose() => Close();
                public void Send(int message) { }
                public void Send(byte[] message) { }
            }

            public int InputCount => Ins.Count;
            public string InputName(int index) => Ins[index];
            public int OutputCount => Outs.Count;
            public string OutputName(int index) => Outs[index];
            public IMidiInput OpenInput(int index) => new Port(this, Ins[index]);
            public IMidiOutput OpenOutput(int index) => new Port(this, Outs[index]);
            public IControllerPort OpenControllerPort(int inputIndex, int outputIndex) => throw new System.NotSupportedException();
            public void StartWatching(System.Action<MidiDeviceChange> changed) => this.changed = changed;
            public void StopWatching() => changed = null;
            public bool CreateMidi2Endpoint() => false;
            public void ReleaseMidi2() { }

            public void Plug(string name)
            {
                Ins.Add(name);
                changed?.Invoke(new MidiDeviceChange(name, true, Ins.Count, Outs.Count));
            }
        }

        [AvaloniaTest]
        public void MidiDevicesOpenAndFollowPlugging()
        {
            var core = Host.Core;
            var registry = Host.Registry;
            var backend = new StandInMidi();
            var saved = ReBuzzCore.DefaultMidiBackend;
            ReBuzzCore.DefaultMidiBackend = c => backend;
            var midi = new MidiEngine(core, registry);
            ReBuzzCore.DefaultMidiBackend = saved;
            var window = new Window { Width = 760, Height = 600 };
            try
            {
                // Pads is remembered from an earlier session, and isn't plugged in.
                midi.SetMidiInputDevices(new Dictionary<string, int> { ["Pads"] = 1 });
                var page = new MidiPage(midi, registry);
                window.Content = page;
                window.Show();
                page.Show();
                Settle(window);
                Assert.That(page.Inputs.Select(r => (r.Name, r.Connected, r.Box.IsChecked)), Is.EqualTo(new[] { ("Keys", true, (bool?)false), ("Pads", false, (bool?)true) }));
                Assert.That(page.Outputs.Select(r => r.Name), Is.EqualTo(new[] { "Synth" }));

                page.Inputs[0].Box.IsChecked = true;
                Settle(window);
                Assert.That(registry.ReadDictionary("MIDI In List")["Keys"], Is.EqualTo(1), "kept by name");
                Assert.That(backend.Opened, Does.Contain("Keys"));
                Assert.That(page.Inputs.Single(r => r.Name == "Keys").Open, Is.True);
                page.Outputs[0].Box.IsChecked = true;
                Settle(window);
                Assert.That(registry.ReadDictionary("MIDI Out List")["Synth"], Is.EqualTo(1));
                Assert.That(midi.GetMidiOuts().Select(o => o.Item2), Is.EqualTo(new[] { "Synth" }));

                // Plugged in: the engine opens the remembered device, and the page follows.
                backend.Plug("Pads");
                page.Refresh();
                Assert.That(backend.Opened, Does.Contain("Pads"));
                var pads = page.Inputs.Single(r => r.Name == "Pads");
                Assert.That((pads.Connected, pads.Open), Is.EqualTo((true, true)));
                Studio.Screenshot(window, "prefs-midi-devices");

                page.Inputs.Single(r => r.Name == "Keys").Box.IsChecked = false;
                Settle(window);
                Assert.That(registry.ReadDictionary("MIDI In List")["Keys"], Is.EqualTo(0));
                Assert.That(backend.Opened, Does.Not.Contain("Keys"), "closed");
                Assert.That(backend.Opened, Does.Contain("Pads"), "the others open again");

                // A page anew shows what was saved.
                var again = new MidiPage(midi, registry);
                again.Show();
                Assert.That(again.Inputs.Select(r => (r.Name, r.Box.IsChecked)), Is.EqualTo(new[] { ("Keys", (bool?)false), ("Pads", (bool?)true) }));
                Assert.That(again.Outputs.Single().Box.IsChecked, Is.True);
            }
            finally
            {
                window.Close();
                midi.ReleaseAll();
                midi.DisposeWatcher();
                registry.DeleteCurrentUserSubKey(Global.RegistryRoot + "MIDI In List");
                registry.DeleteCurrentUserSubKey(Global.RegistryRoot + "MIDI Out List");
            }
        }

        [AvaloniaTest]
        public void MidiPageRendersWithTheEnginesDevicesAndItsSettings()
        {
            var prefs = Open("MIDI");
            var page = prefs.Page<MidiPage>();
            var core = Host.Core;
            Assert.That(page.Inputs.Count, Is.EqualTo(core.MidiInOutEngine.Backend.InputCount));
            Studio.Screenshot(prefs, "prefs-midi");

            // MIDISettings, generically: MIDI filtering off and on again.
            var filtering = (CheckBox)page.SettingControl("MIDISettings/MIDIFiltering");
            Assert.That(filtering.IsChecked, Is.EqualTo(Global.MIDISettings.MIDIFiltering));
            bool was = Global.MIDISettings.MIDIFiltering;
            try
            {
                filtering.IsChecked = !was;
                Assert.That(Global.MIDISettings.MIDIFiltering, Is.EqualTo(!was));
                Assert.That(new MIDISettings().MIDIFiltering, Is.EqualTo(!was), "saved: a new instance loads it");
            }
            finally
            {
                filtering.IsChecked = was;
            }
            Assert.That(new MIDISettings().MIDIFiltering, Is.EqualTo(was));
            Close(prefs);
        }

        // ------------------------------------------------------------------ Sync

        [AvaloniaTest]
        public void SyncChoicesRoundTrip()
        {
            var sync = Host.Core.SessionSync;
            var registry = Host.Registry;
            var prefs = Open("Sync");
            var page = prefs.Page<SyncPage>();
            Studio.Screenshot(prefs, "prefs-sync");
            try
            {
                page.ClockInBox.IsChecked = true;
                Assert.That(sync.FollowMidiClock, Is.True);
                Assert.That(registry.Read("MidiClockIn", 0, "Session"), Is.EqualTo(1), "kept in the settings");
                Assert.That(sync.Status, Is.Not.Empty);

                // Clock out to a device that isn't open: listed so, and off again.
                sync.SetClockOut("Ghost Port", true);
                Settle(prefs);
                page.Refresh();
                var ghost = page.ClockOutRows.Single(r => r.Name == "Ghost Port");
                Assert.That(ghost.Box.IsChecked, Is.True);
                Assert.That(ghost.Box.Content, Is.EqualTo("Ghost Port (not open)"));
                Studio.Screenshot(prefs, "prefs-sync-on");
                ghost.Box.IsChecked = false;
                Assert.That(sync.ClockOutNames, Does.Not.Contain("Ghost Port"));
                Assert.That(registry.Read("MidiClockOut", "x", "Session"), Is.EqualTo(""));

                // Link: on (with the helper beside the tests) or the reason why not; MIDI clock in goes off.
                page.LinkBox.IsChecked = true;
                Assert.That(sync.LinkEnabled || page.ProblemText.Length > 0, Is.True, "Link is on, or the page says why not");
                Assert.That(sync.FollowMidiClock, Is.False, "one or the other");
                page.LinkBox.IsChecked = false;
                Assert.That(sync.LinkEnabled, Is.False);
                Close(prefs);

                prefs = Open("Sync");
                page = prefs.Page<SyncPage>();
                Assert.That(page.ClockInBox.IsChecked, Is.False);
                Assert.That(page.LinkBox.IsChecked, Is.False);
            }
            finally
            {
                sync.SetLink(false);
                sync.SetFollowMidiClock(false);
                sync.SetClockOut("Ghost Port", false);
            }
        }

        // ------------------------------------------------------------------ General

        [AvaloniaTest]
        public void GeneralSettingsShowFromTheirAttributesAndRoundTrip()
        {
            var prefs = Open("General");
            var page = prefs.Page<GeneralPage>();
            Studio.Screenshot(prefs, "prefs-general");
            Assert.That(page.SettingControl("GeneralSettings/WPFSoftwareRendering"), Is.Null, "WPF's own aren't shown");
            Assert.That(page.SettingControl("EngineSettings/AudioBufferFillThread"), Is.Null);

            var engine = Global.EngineSettings;
            var general = Global.GeneralSettings;
            bool panning = engine.EqualPowerPanning;
            var wait = general.SongLoadWait;
            var panningBox = (CheckBox)page.SettingControl("EngineSettings/EqualPowerPanning");
            var waitBox = (ComboBox)page.SettingControl("GeneralSettings/SongLoadWait");
            Assert.That(panningBox.Content, Is.EqualTo("Equal power panning"));
            try
            {
                panningBox.IsChecked = !panning;
                Assert.That(engine.EqualPowerPanning, Is.EqualTo(!panning), "the engine's setting");
                Assert.That(new EngineSettings().EqualPowerPanning, Is.EqualTo(!panning), "saved");

                int index = System.Array.IndexOf(general.List.Single(s => s.Name == "SongLoadWait").Options, "Minutes_1");
                Assert.That(((ComboBoxItem)waitBox.Items[index]!).Content, Is.EqualTo("Minutes 1"));
                waitBox.SelectedIndex = index;
                Assert.That(general.SongLoadWait, Is.EqualTo(SongLoadWaitTime.Minutes_1));
                Assert.That(new GeneralSettings().SongLoadWait, Is.EqualTo(SongLoadWaitTime.Minutes_1));
                Close(prefs);

                prefs = Open("General");
                page = prefs.Page<GeneralPage>();
                Assert.That(((CheckBox)page.SettingControl("EngineSettings/EqualPowerPanning")).IsChecked, Is.EqualTo(!panning));
                Assert.That(((ComboBox)page.SettingControl("GeneralSettings/SongLoadWait")).SelectedIndex, Is.EqualTo(index));
            }
            finally
            {
                engine.Set("EqualPowerPanning", panning);
                general.Set("SongLoadWait", wait.ToString());
                engine.Save();
                general.Save();
            }
            Assert.That(PrefsPage.Pretty("LowLatencyGC"), Is.EqualTo("Low latency GC"));
            Assert.That(PrefsPage.Pretty("MIDIFiltering"), Is.EqualTo("MIDI filtering"));
        }
    }
}
