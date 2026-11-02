using NAudio.Midi;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using System;
using System.Threading;
using Windows.Devices.Enumeration;

namespace ReBuzz.Midi
{
    /// <summary>
    /// Fork (docs/specs/engine-split.md S8): Windows MIDI. WinMM devices through NAudio, the MIDI 2
    /// endpoint through Windows MIDI Services, hot-plug through a WinRT device watcher. Moved
    /// unchanged from MidiEngine and SessionControllers. Windows only, so it stays in ReBuzz.exe.
    /// </summary>
    internal sealed class WindowsMidiBackend : IMidiBackend
    {
        readonly ReBuzzCore buzz;
        readonly Midi2 midi2;
        DeviceWatcher _watcher;
        Action<MidiDeviceChange> changed;

        public WindowsMidiBackend(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            midi2 = new Midi2(buzz);
        }

        public int InputCount => MidiIn.NumberOfDevices;
        public string InputName(int index) => MidiIn.DeviceInfo(index).ProductName;
        public int OutputCount => MidiOut.NumberOfDevices;
        public string OutputName(int index) => MidiOut.DeviceInfo(index).ProductName;

        public IMidiInput OpenInput(int index)
        {
            var midiIn = new MidiInDevice(buzz);
            midiIn.CreateMidiIn(index);
            return midiIn;
        }

        public IMidiOutput OpenOutput(int index) => new MidiOutDevice(index);

        public IControllerPort OpenControllerPort(int inputIndex, int outputIndex) => new NAudioPort(inputIndex, outputIndex);

        public bool CreateMidi2Endpoint() => midi2.CreateMidi2Endpoint();

        public void ReleaseMidi2() => midi2.Release();

        public void StartWatching(Action<MidiDeviceChange> changed)
        {
            this.changed = changed;

            // Use empty selector to get all MIDI devices
            string selector = "";// "System.Devices.InterfaceClassGuid:=\"{6DC23320-AB33-4CE4-80D4-BBB3EBBF2814}\"";

            _watcher = DeviceInformation.CreateWatcher(selector);
            _watcher.Added += OnDeviceAdded;
            _watcher.Removed += OnDeviceRemoved;
            _watcher.Updated += OnDeviceUpdated;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Stopped += OnWatcherStopped;
            _watcher.Start();
        }

        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation args)
        {
            // New device spotted. Called a lot when watcher is started as it enumerates the installed (not connected) MIDI devices
            //buzz.DCWriteLine("OnDeviceAdded");
        }

        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            // A known device was removed (uninstalled)
            //buzz.DCWriteLine("OnDeviceRemoved");
        }

        private async void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            // A device that we already know about has changed somehow (connected, disconnected, other?)

            int midiInDevsCount = MidiIn.NumberOfDevices;
            int midiOutDevsCount = MidiOut.NumberOfDevices;

            try
            {
                var info = await DeviceInformation.CreateFromIdAsync(args.Id);
                changed?.Invoke(new MidiDeviceChange(info.Name, info.IsEnabled, midiInDevsCount, midiOutDevsCount));
            }
            catch (Exception ex)
            {
                buzz.DCWriteLine(ex.Message, BuzzGUI.Interfaces.DCLogLevel.Error);
            }
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        {
            // Watcher has finished enumerating devices
            //buzz.DCWriteLine("OnEnumerationCompleted");
        }

        private void OnWatcherStopped(DeviceWatcher sender, object args)
        {
            // The watcher has stopped watching
            //buzz.DCWriteLine("OnWatcherStopped");
        }

        public void StopWatching()
        {
            if (_watcher != null)
            {
                _watcher.Added -= OnDeviceAdded;
                _watcher.Removed -= OnDeviceRemoved;
                _watcher.Updated -= OnDeviceUpdated;
                _watcher.EnumerationCompleted -= OnEnumerationCompleted;
                _watcher.Stopped -= OnWatcherStopped;

                if (_watcher.Status == DeviceWatcherStatus.Started ||
                    _watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    _watcher.Stop();
                }

                _watcher = null;
            }
        }

        /// <summary>A controller's ports through NAudio (WinMM), opened for the controller alone.</summary>
        sealed class NAudioPort : IControllerPort
        {
            readonly MidiIn input;
            readonly MidiOut output;
            readonly Lock sendLock = new Lock();

            public event Action<int> Received;

            public NAudioPort(int inputIndex, int outputIndex)
            {
                output = new MidiOut(outputIndex);
                try
                {
                    input = new MidiIn(inputIndex);
                    input.MessageReceived += (s, e) => Received?.Invoke(e.RawMessage);
                    input.Start();
                }
                catch
                {
                    output.Dispose();
                    input?.Dispose();
                    throw;
                }
            }

            public void Send(int message)
            {
                lock (sendLock) output.Send(message);
            }

            public void Send(byte[] message)
            {
                lock (sendLock) output.SendBuffer(message);
            }

            public void Dispose()
            {
                try { input.Stop(); } catch (Exception) { }
                try { input.Dispose(); } catch (Exception) { }
                try { output.Dispose(); } catch (Exception) { }
            }
        }
    }
}
