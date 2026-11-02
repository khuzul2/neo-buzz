using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common.Settings;
using ReBuzz.Audio;
using ReBuzz.Audio.Engine;
using ReBuzz.AppViews;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Midi;
using ReBuzzTests.Automation;

namespace ReBuzzTests
{
    /// <summary>
    /// docs/specs/engine-split.md S8: the engine reaches audio drivers through IAudioDriverFactory
    /// and MIDI devices through IMidiBackend. The app installs the Windows ones (ASIO, WASAPI,
    /// WinMM, Windows MIDI Services); a test can give the engine its own.
    /// </summary>
    public class BackendTests
    {
        [Test]
        public void TheAppInstallsTheWindowsBackends()
        {
            using var driver = new Driver();
            driver.Start();

            Assert.That(ReBuzzCore.DefaultAudioDriverFactory, Is.InstanceOf<WindowsAudioDriverFactory>());
            Assert.That(driver.Core.AudioDriverFactory, Is.SameAs(ReBuzzCore.DefaultAudioDriverFactory));
            Assert.That(driver.Core.MidiInOutEngine.Backend, Is.InstanceOf<WindowsMidiBackend>());
        }

        [Test]
        public void MidiDevicesGoThroughTheBackend()
        {
            var midi = new FakeMidiBackend(["Keys", "Pad"], ["Synth"]);
            using var driver = new Driver { MidiBackend = _ => midi };
            driver.Start();
            var engine = driver.Core.MidiInOutEngine;
            Assert.That(midi.Watching, Is.True, "the engine watches for hot-plug");

            engine.CreateMidiIn(1);
            engine.CreateMidiOut(0);
            driver.Core.SendMIDIOutput(0, 0x7F3C90);

            Assert.That(engine.GetMidiInputDevices(), Is.EqualTo(new[] { 1 }));
            Assert.That(engine.GetMidiOuts().Single().Item2, Is.EqualTo("Synth"));
            Assert.That(midi.Outputs[0].Sent, Is.EqualTo(new[] { 0x7F3C90 }));

            // The device watcher reports the synth and the pad gone: both close.
            midi.Raise(new MidiDeviceChange("Synth", false, 2, 1));
            midi.Raise(new MidiDeviceChange("Pad", false, 2, 1));

            Assert.That(engine.GetMidiOutputDevices(), Is.Empty);
            Assert.That(engine.GetMidiInputDevices(), Is.Empty);
            Assert.That(midi.Outputs[0].Disposed, Is.True);
            Assert.That(midi.Inputs[1].Closed, Is.True);

            engine.DisposeWatcher();
            Assert.That(midi.Watching, Is.False);
        }

        [Test]
        public void ControllersListTheBackendsPorts()
        {
            var midi = new FakeMidiBackend(["Keys"], ["Synth"]);
            using var driver = new Driver { MidiBackend = _ => midi };
            driver.Start();

            driver.Core.SessionControllers.Available();

            Assert.That(midi.NamesRead, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void NoBackendMeansNoDevices()
        {
            var none = new NoMidiBackend();
            Assert.That(none.InputCount, Is.Zero);
            Assert.That(none.OutputCount, Is.Zero);
            Assert.That(none.CreateMidi2Endpoint(), Is.False);
            Assert.Throws<NotSupportedException>(() => none.OpenOutput(0));
        }

        [Test]
        public void AudioDevicesListAsioDriversThenWasapi()
        {
            var service = new AudioDeviceService(new FakeUserMessages(), new FakeAudioDrivers(asio: true, "ASIO4ALL", "Focusrite"));

            var devices = service.GetOutputDevices();

            Assert.That(devices.Select(d => (d.Name, d.Type)), Is.EqualTo(new[]
            {
                ("ASIO4ALL", AudioEngine.AudioOutType.ASIO),
                ("Focusrite", AudioEngine.AudioOutType.ASIO),
                ("WASAPI", AudioEngine.AudioOutType.Wasapi),
            }));
            Assert.That(service.ResolveDevice("Focusrite").Type, Is.EqualTo(AudioEngine.AudioOutType.ASIO));
            Assert.That(service.ResolveDevice("gone").Name, Is.EqualTo("ASIO4ALL"), "an unknown name falls back to the first device");
        }

        [Test]
        public void AudioDeviceErrorsGoToTheUser()
        {
            var messages = new FakeUserMessages();
            var service = new AudioDeviceService(messages, new FakeAudioDrivers(asio: true) { Broken = true });

            var devices = service.GetOutputDevices();

            Assert.That(devices, Is.Empty);
            Assert.That(messages.AnyErrorReported, Is.True);
            Assert.That(messages.Caption, Is.EqualTo("Audio Devices Error"));
        }

        sealed class FakeAudioDrivers(bool asio, params string[] names) : IAudioDriverFactory
        {
            public bool Broken { get; init; }
            public bool AsioSupported => Broken ? throw new InvalidOperationException("no ASIO") : asio;
            public IEnumerable<string> AsioDriverNames() => names;
            public IAudioDriver Create(AudioEngine.AudioOutType type, string deviceName, ReBuzzCore buzzCore, EngineSettings settings, IRegistryEx registryEx) =>
                throw new NotSupportedException();
            public bool ShowConfig(AudioEngine.AudioOutDevice device, ReBuzzCore buzzCore, IRegistryEx registryEx, string buzzPath) => false;
            public int AsioPreferredBufferSize(string deviceName) => 256;
        }

        sealed class FakeMidiBackend(string[] inputs, string[] outputs) : IMidiBackend
        {
            Action<MidiDeviceChange>? changed;
            public int NamesRead;
            public bool Watching;
            public readonly Dictionary<int, FakeInput> Inputs = new();
            public readonly Dictionary<int, FakeOutput> Outputs = new();

            public int InputCount => inputs.Length;
            public string InputName(int index) { NamesRead++; return inputs[index]; }
            public int OutputCount => outputs.Length;
            public string OutputName(int index) { NamesRead++; return outputs[index]; }
            public IMidiInput OpenInput(int index) => Inputs[index] = new FakeInput(inputs[index]);
            public IMidiOutput OpenOutput(int index) => Outputs[index] = new FakeOutput(outputs[index]);
            public IControllerPort OpenControllerPort(int inputIndex, int outputIndex) => throw new NotSupportedException();
            public void StartWatching(Action<MidiDeviceChange> changed) { this.changed = changed; Watching = true; }
            public void StopWatching() { changed = null; Watching = false; }
            public bool CreateMidi2Endpoint() => false;
            public void ReleaseMidi2() { }
            public void Raise(MidiDeviceChange change) => changed?.Invoke(change);
        }

        sealed class FakeInput(string name) : IMidiInput
        {
            public bool Closed;
            public string ProductName => name;
            public void Close() => Closed = true;
        }

        sealed class FakeOutput(string name) : IMidiOutput
        {
            public bool Disposed;
            public readonly List<int> Sent = new();
            public string ProductName => name;
            public void Send(int message) => Sent.Add(message);
            public void Send(byte[] message) { }
            public void Dispose() => Disposed = true;
        }
    }
}
