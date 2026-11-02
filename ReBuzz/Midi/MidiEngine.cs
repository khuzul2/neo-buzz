using BuzzGUI.Common;
using ReBuzz.Common;
using ReBuzz.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

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
        private readonly Dictionary<int, IMidiInput> midiIns = new Dictionary<int, IMidiInput>();
        private readonly Dictionary<int, IMidiOutput> midiOuts = new Dictionary<int, IMidiOutput>();
        private readonly Lock midiInLock = new Lock();

        // Fork (engine-split.md S8): the platform's MIDI devices (WinMM, Windows MIDI Services and
        // the WinRT device watcher on Windows); none without a backend.
        private readonly IMidiBackend backend;

        internal IMidiBackend Backend => backend;

        public MidiEngine(ReBuzzCore buzz, IRegistryEx registryEx)
        {
            this.registryEx = registryEx;
            this.buzz = buzz;

            backend = ReBuzzCore.DefaultMidiBackend?.Invoke(buzz) ?? new NoMidiBackend();

            CheckRegistryDataFormat();

            backend.StartWatching(OnDeviceUpdated);
        }

        /// <summary>The MIDI 2 (Windows MIDI Services) endpoint.</summary>
        internal bool CreateMidi2Endpoint() => backend.CreateMidi2Endpoint();

        private void OnDeviceUpdated(MidiDeviceChange info)
        {
            // A device that we already know about has changed somehow (connected, disconnected, other?).
            // The device counts were taken when the change was noticed; the backend logs what this throws.
            int midiInDevsCount = info.InputCount;
            int midiOutDevsCount = info.OutputCount;

            {
                if (info.Connected)
                {
                    buzz.DCWriteLine("Device connected: " + info.Name);

                    var inputsInfo = registryEx.ReadDictionary("MIDI In List");
                    if (inputsInfo.ContainsKey(info.Name))
                    {
                        if ((Int32)inputsInfo[info.Name] == 1)
                        {
                            for (int i = 0; i < midiInDevsCount; i++)
                            {
                                if (backend.InputName(i) == info.Name)
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
                                if (backend.OutputName(i) == info.Name)
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
                    IMidiInput goneIn = null;
                    lock (midiInLock)
                    {
                        var key = midiIns.FirstOrDefault(kv => kv.Value.ProductName == info.Name);
                        if (key.Value != null)
                        {
                            goneIn = key.Value;
                            midiIns.Remove(key.Key);
                        }
                    }
                    goneIn?.Close();

                    IMidiOutput goneOut = null;
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
        }

        public void DisposeWatcher()
        {
            backend.StopWatching();
        }

        public void CreateMidiIn(int selectedDeviceIndex)
        {
            try
            {
                // Fork: the old handle is closed first (opening a device twice fails).
                IMidiInput old;
                lock (midiInLock)
                {
                    midiIns.Remove(selectedDeviceIndex, out old);
                }
                old?.Close();

                var midiIn = backend.OpenInput(selectedDeviceIndex);
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
            List<IMidiInput> devices;
            lock (midiInLock)
            {
                devices = midiIns.Values.ToList();
                midiIns.Clear();
            }
            foreach (var md in devices)
            {
                md.Close();
            }
        }

        public void CreateMidiOut(int selectedDeviceIndex)
        {
            try
            {
                // Fork: the old handle is closed first (opening a device twice fails), and
                // outside the lock, so the audio thread's sends don't wait for the driver.
                IMidiOutput old;
                lock (midiOutLock)
                {
                    midiOuts.Remove(selectedDeviceIndex, out old);
                }
                old?.Dispose();

                var device = backend.OpenOutput(selectedDeviceIndex);
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
                    d.Send(message);
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
                    d.Send(message);
                }
                catch (Exception e)
                {
                    buzz.DCWriteLine(e.Message);
                }
            }
        }

        public void DisposeMidiOuts()
        {
            List<IMidiOutput> devices;
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

            backend.ReleaseMidi2();
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
            
            int midiInDevsCount = backend.InputCount;

            foreach (var item in inputsInfo)
            {
                if (((Int32)item.Value & 1) > 0) // Flags & 1 == enabled
                {
                    for (int i = 0; i < midiInDevsCount; i++)
                    {
                        if (backend.InputName(i) == item.Key)
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

            int midiOutDevsCount = backend.OutputCount;

            foreach (var item in outputsInfo)
            {
                if (((Int32)item.Value & 1) > 0) // Flags & 1 == enabled
                {
                    for (int i = 0; i < midiOutDevsCount; i++)
                    {
                        if (backend.OutputName(i) == item.Key)
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
