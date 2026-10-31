using BuzzGUI.Common;
using NAudio.Midi;
using ReBuzz.Common;
using ReBuzz.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Devices.Enumeration;

namespace ReBuzz.Midi
{
    internal class MidiEngine
    {
        private readonly IRegistryEx registryEx;
        private readonly ReBuzzCore buzz;
        // Fork (0.8 groundwork): the device watcher's thread, the UI and the audio thread (MIDI out
        // machines) all use these, so each is guarded: midiIns by midiInLock, midiOuts by
        // midiOutLock (taken after AudioLock, never before it). Devices are opened and closed
        // outside the locks, so a send never waits for a driver.
        private readonly Dictionary<int, MidiInDevice> midiIns = new Dictionary<int, MidiInDevice>();
        private readonly Dictionary<int, MidiOutDevice> midiOuts = new Dictionary<int, MidiOutDevice>();
        private readonly Lock midiInLock = new Lock();
        private DeviceWatcher _watcher;

        Midi2 midi2;

        internal Midi2 Midi2 { get => midi2; }

        public MidiEngine(ReBuzzCore buzz, IRegistryEx registryEx)
        {
            this.registryEx = registryEx;
            this.buzz = buzz;

            midi2 = new Midi2(buzz);

            CheckRegistryDataFormat();

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

                if (info.IsEnabled)
                {
                    buzz.DCWriteLine("Device connected: " + info.Name);

                    var inputsInfo = registryEx.ReadDictionary("MIDI In List");
                    if (inputsInfo.ContainsKey(info.Name))
                    {
                        if ((Int32)inputsInfo[info.Name] == 1)
                        {
                            for (int i = 0; i < midiInDevsCount; i++)
                            {
                                if (MidiIn.DeviceInfo(i).ProductName == info.Name)
                                {
                                    CreateMidiIn(i);
                                    break;
                                }
                            }
                        }
                    }

                    var outputsInfo = registryEx.ReadDictionary("MIDI Out List");
                    if (outputsInfo.ContainsKey(info.Name))
                    {
                        if ((Int32)outputsInfo[info.Name] == 1)
                        {
                            for (int i = 0; i < midiOutDevsCount; i++)
                            {
                                if (MidiOut.DeviceInfo(i).ProductName == info.Name)
                                {
                                    CreateMidiOut(i);
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                {
                    buzz.DCWriteLine("Device disconnected: " + info.Name);

                    // Fork: closed, not only forgotten, so the device opens again when it comes back.
                    MidiInDevice goneIn = null;
                    lock (midiInLock)
                    {
                        var key = midiIns.FirstOrDefault(kv => kv.Value.ProductName == info.Name);
                        if (key.Value != null)
                        {
                            goneIn = key.Value;
                            midiIns.Remove(key.Key);
                        }
                    }
                    goneIn?.DisposeMidiIn();

                    MidiOutDevice goneOut = null;
                    lock (midiOutLock)
                    {
                        var key = midiOuts.FirstOrDefault(kv => kv.Value.ProductName == info.Name);
                        if (key.Value != null)
                        {
                            goneOut = key.Value;
                            midiOuts.Remove(key.Key);
                        }
                    }
                    goneOut?.Dispose();
                }
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

        public void DisposeWatcher()
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

        public void CreateMidiIn(int selectedDeviceIndex)
        {
            try
            {
                // Fork: the old handle is closed first (opening a device twice fails).
                MidiInDevice old;
                lock (midiInLock)
                {
                    midiIns.Remove(selectedDeviceIndex, out old);
                }
                old?.DisposeMidiIn();

                var midiIn = new MidiInDevice(buzz);
                midiIn.CreateMidiIn(selectedDeviceIndex);
                lock (midiInLock)
                {
                    midiIns[selectedDeviceIndex] = midiIn;
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine(e.Message);
            }
        }

        public void DisposeMidiIn()
        {
            List<MidiInDevice> devices;
            lock (midiInLock)
            {
                devices = midiIns.Values.ToList();
                midiIns.Clear();
            }
            foreach (var md in devices)
            {
                md.DisposeMidiIn();
            }
        }

        public void CreateMidiOut(int selectedDeviceIndex)
        {
            try
            {
                // Fork: the old handle is closed first (opening a device twice fails), and
                // outside the lock, so the audio thread's sends don't wait for the driver.
                MidiOutDevice old;
                lock (midiOutLock)
                {
                    midiOuts.Remove(selectedDeviceIndex, out old);
                }
                old?.Dispose();

                var device = new MidiOutDevice(selectedDeviceIndex);
                lock (midiOutLock)
                {
                    midiOuts[selectedDeviceIndex] = device;
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine(e.Message);
            }
        }

        readonly Lock midiOutLock = new Lock();

        public void SendMidiOut(int device, int message)
        {
            lock (midiOutLock)
            {
                if (!midiOuts.TryGetValue(device, out var d)) return;
                try
                {
                    d.MidiOut.Send(message);
                }
                catch (Exception e)
                {
                    buzz.DCWriteLine(e.Message);
                }
            }
        }

        public void SendMidiOut(int device, byte[] message)
        {
            lock (midiOutLock)
            {
                if (!midiOuts.TryGetValue(device, out var d)) return;
                try
                {
                    d.MidiOut.SendBuffer(message);
                }
                catch (Exception e)
                {
                    buzz.DCWriteLine(e.Message);
                }
            }
        }

        public void DisposeMidiOuts()
        {
            List<MidiOutDevice> devices;
            lock (ReBuzzCore.AudioLock)
            {
                lock (midiOutLock)
                {
                    devices = midiOuts.Values.ToList();
                    midiOuts.Clear();
                }
            }
            foreach (var midiOut in devices)
            {
                try
                {
                    midiOut.Dispose();
                }
                catch (Exception e)
                {
                    buzz.DCWriteLine(e.Message);
                }
            }
        }

        public void ReleaseAll()
        {
            DisposeMidiIn();
            DisposeMidiOuts();

            Midi2.Release();
        }


        internal static IList<string> GetMidiControllers(IRegistryEx registryEx)
        {
            List<string> list = new List<string>();

            int numMidiControllers = registryEx.Read("numMidiControllers", 0, "Settings");
            for (int i = 0; i < numMidiControllers; i++)
            {
                list.Add(registryEx.Read("MidiController" + i, "", "Settings"));
            }

            return list;
        }

        internal void CheckRegistryDataFormat()
        {
            long version = registryEx.Read("MIDIDeviceFormat", 1, "Settings");

            if(version == 1)
            {
                // Convert MidiOut1 = SomeDevice to SomeDevice = flags
                // flags is just 1 for enabled now but may add more (eg. hidden)
                var inputsInfoOld = registryEx.ReadDictionary("MIDI In List");
                Dictionary<string, Int32> inputsInfo = new Dictionary<string, Int32>();
                foreach (var item in inputsInfoOld)
                {
                    if (item.Value is not int)
                        inputsInfo.Add((string)item.Value, 1);
                }

                registryEx.DeleteCurrentUserSubKey(buzz.registryRoot + "\\" + "MIDI In List");
                SetMidiInputDevices(inputsInfo);

                var outputsInfoOld = registryEx.ReadDictionary("MIDI Out List");
                Dictionary<string, Int32> outputsInfo = new Dictionary<string, Int32>();
                foreach (var item in outputsInfoOld)
                {
                    if (item.Value is not int)
                        outputsInfo.Add((string)item.Value, 1);
                }
                registryEx.DeleteCurrentUserSubKey(buzz.registryRoot + "\\" + "MIDI Out List");
                SetMidiOutputDevices(outputsInfo);
            }

            registryEx.Write("MIDIDeviceFormat", 2, "Settings");
        }

        internal IEnumerable<int> GetMidiInputDevices()
        {
            lock (midiInLock) return midiIns.Keys.ToList();
        }

        internal void OpenMidiInDevices()
        {
            var inputsInfo = registryEx.ReadDictionary("MIDI In List").KeyValuesToStringInt();
            
            int midiInDevsCount = MidiIn.NumberOfDevices;

            foreach (var item in inputsInfo)
            {
                if (((Int32)item.Value & 1) > 0) // Flags & 1 == enabled
                {
                    for (int i = 0; i < midiInDevsCount; i++)
                    {
                        if (MidiIn.DeviceInfo(i).ProductName == item.Key)
                        {
                            CreateMidiIn(i);
                            break;
                        }
                    }
                }
            }
        }

        internal void SetMidiInputDevices(Dictionary<string, Int32> midiInDevices)
        {
            var regKey = registryEx.CreateCurrentUserSubKey(buzz.registryRoot + "\\" + "MIDI In List");
            try
            {
                foreach (var item in midiInDevices)
                {
                    regKey.SetValue(item.Key, item.Value); // DeviceName = BitFlags
                }
            }
            catch { }
        }

        internal IEnumerable<int> GetMidiOutputDevices()
        {
            lock (midiOutLock) return midiOuts.Keys.ToList();
        }

        internal IEnumerable<Tuple<int, string>> GetMidiOuts()
        {
            List<Tuple<int, string>> moList = new List<Tuple<int, string>>();
            lock (midiOutLock)
            {
                foreach (var mo in midiOuts)
                    moList.Add(new Tuple<int, string>(mo.Key, mo.Value.ProductName));
            }
            return moList;
        }

        internal void SetMidiOutputDevices(Dictionary<string, Int32> midiOutDevices)
        {
            var regKey = registryEx.CreateCurrentUserSubKey(buzz.registryRoot + "\\" + "MIDI Out List");
            try
            {
                foreach (var item in midiOutDevices)
                {
                    regKey.SetValue(item.Key, item.Value); // DeviceName = BitFlags
                }
            }
            catch { }
        }

        internal void OpenMidiOutDevices()
        {
            var outputsInfo = registryEx.ReadDictionary("MIDI Out List").KeyValuesToStringInt();

            int midiOutDevsCount = MidiOut.NumberOfDevices;

            foreach (var item in outputsInfo)
            {
                if (((Int32)item.Value & 1) > 0) // Flags & 1 == enabled
                {
                    for (int i = 0; i < midiOutDevsCount; i++)
                    {
                        if (MidiOut.DeviceInfo(i).ProductName == item.Key)
                        {
                            CreateMidiOut(i);
                            break;
                        }
                    }
                }
            }
        }
    }
}
