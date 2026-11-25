using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.ManagedMachine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace ReBuzz.MachineManagement
{
    public enum BuzzWorkMode
    {
        WM_NOIO = 0,
        WM_READ,
        WM_WRITE,
        WM_READWRITE
    }

    public class MachineManager
    {
        ReBuzzCore buzz;
        public ReBuzzCore Buzz
        {
            get => buzz;
            set
            {
                if (buzz != null)
                {
                }

                buzz = value;

                if (buzz != null)
                {
                }
            }
        }

        // Adjust these to support old machines
        public static readonly int BUZZ_MACHINE_INTERFACE_VERSION_12 = 12;
        public static readonly int BUZZ_MACHINE_INTERFACE_VERSION_15 = 15; //buzz v1.2
        public static readonly int BUZZ_MACHINE_INTERFACE_VERSION_27 = 27;
        public static readonly int BUZZ_MACHINE_INTERFACE_VERSION_42 = 42;

        readonly Dictionary<MachineCore, ManagedMachineHost> managedMachines = new Dictionary<MachineCore, ManagedMachineHost>();
        internal Dictionary<MachineCore, ManagedMachineHost> ManagedMachines { get => managedMachines; }

        readonly ConcurrentDictionary<MachineCore, MachineWorkInstance> workInstances = new ConcurrentDictionary<MachineCore, MachineWorkInstance>();

        private readonly SongCore song;

        internal MachineManager(SongCore song, EngineSettings settings, string buzzPath, IUiDispatcher dispatcher, IKeyboard keyboard)
        {
            this.song = song;
            engineSettings = settings;
            this.buzzPath = buzzPath;
            this.dispatcher = dispatcher;           
            this.keyboard = keyboard;
        }

        // instrumentPath == null or "" if instruments are not supported
        public MachineCore CreateMachine(string libName, string path, string instrument, byte[] data, int trackCount, float x, float y, bool hidden, string machineName = null, bool callInit = true)
        {
            // Removed this lock. Uncomment if ReBuzz goes weird
            //lock (ReBuzzCore.AudioLock)
            {
                MachineCore machine = new MachineCore(song, buzzPath, dispatcher, engineSettings);
                machine.InstrumentName = instrument;
                machine.Position = new Tuple<float, float>(x, y);
                machine.Hidden = hidden;

                // If missing
                if (!buzz.MachineDLLs.ContainsKey(libName))
                {
                    buzz.DCWriteLine("Missing machine: " + libName, DCLogLevel.Error);
                    var machineDLL = machine.MachineDLL;
                    machineDLL.IsMissing = true;
                    machineDLL.Name = libName;
                    machineDLL.Path = path;
                    machineDLL.MachineInfo.MinTracks = machineDLL.MachineInfo.MaxTracks = trackCount;
                    machine.MachineDLL = machineDLL;
                    machine.MachineDLL.Buzz = buzz;
                    machine.Name = GetNewMachineName(machineName);
                    machine.TrackCount = trackCount;
                    machine.Data = data;
                    
                    buzz.AddMachine(machine);
                    machine.Ready = true;
                    return machine;
                }

                var mDll = buzz.MachineDLLs[libName] as MachineDLL;
                machine.MachineDLL = mDll.Clone();
                machine.MachineDLL.Buzz = buzz;

                string name = machineName != null ? machineName : mDll.Info.ShortName;
                buzz.RenameMachine(machine, name);

                CreateManagedMachine(machine, trackCount, data, instrument);

                buzz.AddMachine(machine);

                machine.OversampleFactor = buzz.Gear.OversampleFactor(machine.DLL.Name);
                machine.updateWaveInfo = true;
                machine.IsSoloed = false;
                return machine;
            }
        }

        public void CreateManagedMachine(MachineCore machine, int trackcount, byte[] data, string instrument = null)
        {
            ManagedMachineDLL managedMachineDLL = new ManagedMachineDLL(dispatcher);
            managedMachineDLL.LoadManagedMachine(machine.MachineDLL.Path);
            ManagedMachineHost managedMachineHost = new ManagedMachineHost(managedMachineDLL);
            machine.ManagedMachine = managedMachineHost.ManagedMachine;
            UpdateMasterAndSubTickInfo(managedMachineHost); // Needs to be updated before AddMachine
            managedMachineHost.Machine = machine;

            managedMachineDLL.UpdateMachineDllInfo(machine);
            machine.IsControlMachine = managedMachineHost.IsControlMachine || machine.DLL.Info.Flags.HasFlag(MachineInfoFlags.CONTROL_MACHINE);

            // Create Parameters
            managedMachineDLL.CreateMachineDllParameters(machine);

            machine.TrackCount = trackcount;
            machine.MachineDLL.ManagedDLL = managedMachineDLL;

            // Fork (plugin-host.md §3.1): a machine that stands for many things is told which one, before its state is set.
            if (!string.IsNullOrEmpty(instrument) && machine.ManagedMachine is Buzz.MachineInterface.IBuzzMachineInstrument loader)
            {
                try { loader.SetInstrument(instrument); }
                catch (Exception ex) { buzz.DCWriteLine("Instrument " + instrument + " of " + machine.DLL.Name + ": " + (ex.InnerException ?? ex).Message, DCLogLevel.Error); }
            }

            lock (ReBuzzCore.AudioLock)
            {
                ManagedMachines.Add(machine, managedMachineHost);
            }

            machine.SetCommands();

            if (managedMachineDLL.machineInfo.OutputCount > 0 && machine.DLL.Info.Flags.HasFlag(MachineInfoFlags.MULTI_IO))
            {
                machine.OutputChannelCount = managedMachineDLL.machineInfo.OutputCount;
            }
            if (managedMachineDLL.machineInfo.InputCount > 0 && machine.DLL.Info.Flags.HasFlag(MachineInfoFlags.MULTI_IO) &&
                machine.DLL.Info.Type == MachineType.Effect)
            {
                machine.InputChannelCount = managedMachineDLL.machineInfo.InputCount;
            }

            if (machine.DLL.Info.Type == MachineType.Effect)
            {
                machine.MachineDLL.MachineInfo.Flags |= MachineInfoFlags.STEREO_EFFECT;
            }

            if (machine.DLL.Info.Flags.HasFlag(MachineInfoFlags.PATTERN_EDITOR))
            {
                machine.LoadedEditorData = data;        // fork (clips.md §7.1, CU3): what the song file gave the editor
                managedMachineHost.SetPatternEditorData(data);
            }
            else
            {
                machine.Data = data;
            }

            // Set default values
            managedMachineHost.SetParameterDefaults(machine);

            machine.Ready = true;
            machine.MachineDLL.IsLoaded = true;
        }

        private readonly EngineSettings engineSettings;
        private readonly string buzzPath;
        private readonly IUiDispatcher dispatcher;
        private readonly IKeyboard keyboard;
        internal int GetMachineLatency(MachineCore machine)
        {
            int latency = 0;
            if (machine.DLL.Info.Type == MachineType.Effect)
            {
                if (managedMachines.ContainsKey(machine))
                {
                    latency = managedMachines[machine].Latency;
                }
            }

            return latency;
        }

        private void ValidateMachineStructures(MachineCore machine, int trackCount)
        {
            if (machine.ParameterGroupsList.Count == 1)
            {
                machine.ParameterGroupsList.Add(new ParameterGroup(machine, ParameterGroupType.Global));
            }
            if (machine.ParameterGroupsList.Count == 2)
            {
                machine.ParameterGroupsList.Add(new ParameterGroup(machine, ParameterGroupType.Track));
            }
        }
        internal IMachineDLL GetPatternEditorDLL(MachineCore machine)
        {
            // ToDo: Check register for default machines.
            return buzz.MachineDLLs["Modern Pattern Editor"];
        }

        internal void GetChannels(MachineCore machine, out int inputChannels, out int outputChannels)
        {
            int inputs = 0, outputs = 0;
            var flags = machine.MachineDLL.Info.Flags;
            if (flags.HasFlag(MachineInfoFlags.MULTI_IO))
            {
                if (machine.MachineDLL.Info.Type == MachineType.Effect)
                {
                    inputs = outputs = 2; // stereo
                }
                else if (machine.MachineDLL.Info.Type == MachineType.Generator && !flags.HasFlag(MachineInfoFlags.NO_OUTPUT))
                {
                    outputs = 2;
                }
            }
            else if (flags.HasFlag(MachineInfoFlags.MONO_TO_STEREO) && !flags.HasFlag(MachineInfoFlags.DOES_INPUT_MIXING))
            {
                if (machine.MachineDLL.Info.Type == MachineType.Effect)
                {
                    inputs = 1; outputs = 2; // mono to stereo
                }
                else if (machine.MachineDLL.Info.Type == MachineType.Generator && !flags.HasFlag(MachineInfoFlags.NO_OUTPUT))
                {
                    outputs = 2;
                }
            }
            else if (flags.HasFlag(MachineInfoFlags.DOES_INPUT_MIXING) ||
                flags.HasFlag(MachineInfoFlags.STEREO_EFFECT))
            {
                if (machine.MachineDLL.Info.Type == MachineType.Effect)
                {
                    inputs = outputs = 2; // stereo
                }
                else if (machine.MachineDLL.Info.Type == MachineType.Generator && !flags.HasFlag(MachineInfoFlags.NO_OUTPUT))
                {
                    outputs = 2;
                }
            }
            else
            {
                if (machine.MachineDLL.Info.Type == MachineType.Effect)
                {
                    inputs = outputs = 1; // mono
                }
                else if (machine.MachineDLL.Info.Type == MachineType.Generator && !flags.HasFlag(MachineInfoFlags.NO_OUTPUT))
                {
                    outputs = 1;
                }
            }

            inputChannels = inputs;
            outputChannels = outputs;
        }

        public string GetNewMachineName(string name)
        {
            while (Buzz.SongCore.MachinesList.FirstOrDefault(m => m.Name == name) != null)
            {
                var result = string.Concat(name.ToArray().Reverse().TakeWhile(char.IsNumber).Reverse());
                if (result.Length > 0 &&
                    name.Length > result.Length &&
                    name.Substring(name.Length - result.Length - 3).StartsWith(" | "))
                {
                    int number = int.Parse(result);
                    number++;
                    name = name.Substring(0, name.Length - result.Length) + number;
                }
                else
                {
                    name += " | 2";
                }
            }

            return name;
        }

        public MachineCore GetMaster(IBuzz buzz)
        {
            MachineCore machine = new MachineCore(song, buzzPath, dispatcher, engineSettings);
            machine.Name = "Master";
            machine.InputChannelCount = 1;
            machine.OutputChannelCount = 0;
            //machine.TrackCount = 0;
            machine.Graph = song;
            machine.HasStereoOutput = true;
            machine.HasStereoInput = true;

            var dll = machine.MachineDLL;
            dll.IsCrashed = false;
            dll.IsLoaded = true;
            dll.IsManaged = true;
            dll.IsMissing = false;
            dll.IsOutOfProcess = false;
            dll.Is64Bit = true;
            dll.Buzz = buzz;
            dll.Path = Path.Combine(buzzPath, "Gear");
            dll.Name = "Master";

            //dll.TextColor = Global.Buzz.ThemeColors["MV Machine Text"] != null ? Global.Buzz.ThemeColors["MV Machine Text"] : Colors.GhostWhite;


            var mi = machine.MachineDLL.MachineInfo;
            mi.ShortName = "Master";
            mi.Author = "WDE";
            mi.MinTracks = 0;
            mi.MaxTracks = 0;
            mi.Version = 100;
            mi.Name = "Master";
            mi.InternalVersion = 100;
            mi.Flags = MachineInfoFlags.NO_OUTPUT;
            mi.Type = MachineType.Master;
            machine.MIDIInputChannel = -1;

            ParameterGroup pgGlobal = new ParameterGroup(machine, ParameterGroupType.Global);
            pgGlobal.TrackCount = 1;
            pgGlobal.Type = ParameterGroupType.Global;

            var parameter = new ParameterCore(dispatcher);
            parameter.Name = "Volume";
            parameter.Description = "Master Volume (0=0 dB, 4000=-80 dB)";
            parameter.MinValue = 0;
            parameter.MaxValue = 0x4000;
            parameter.DefValue = 0;
            parameter.NoValue = 65535;
            parameter.Type = ParameterType.Word;
            parameter.Flags = ParameterFlags.State;
            parameter.SetValue(0, parameter.DefValue);
            parameter.IndexInGroup = 0;
            pgGlobal.AddParameter(parameter);

            parameter = new ParameterCore(dispatcher);
            parameter.Name = "BPM";
            parameter.Description = "Beats Per Minute (10-200 hex)";
            parameter.MinValue = 10;
            parameter.MaxValue = 0x200;
            parameter.DefValue = 126;
            parameter.NoValue = 65535;
            parameter.Type = ParameterType.Word;
            parameter.Flags = ParameterFlags.State;
            parameter.SetValue(0, parameter.DefValue);
            parameter.IndexInGroup = 1;
            pgGlobal.AddParameter(parameter);

            parameter = new ParameterCore(dispatcher);
            parameter.Name = "TPB";
            parameter.Description = "Ticks Per Beat (1-20 hex)";
            parameter.MinValue = 1;
            parameter.MaxValue = 0x20;
            parameter.DefValue = 4;
            parameter.NoValue = 255;
            parameter.Type = ParameterType.Byte;
            parameter.Flags = ParameterFlags.State;
            parameter.SetValue(0, parameter.DefValue);
            parameter.IndexInGroup = 2;
            pgGlobal.AddParameter(parameter);

            machine.ParameterGroupsList.Add(pgGlobal);

            ParameterGroup pgTracks = new ParameterGroup(machine, ParameterGroupType.Track);
            pgTracks.TrackCount = 0;

            machine.ParameterGroupsList.Add(pgTracks);

            machine.Ready = true;
            machine.MachineDLL.IsLoaded = true;
            return machine;
        }

        internal static int PatternEditorNumber = 1;
        internal string GetPEName()
        {
            string name = System.Text.Encoding.UTF8.GetString(new byte[1] { 1 }, 0, 1);
            name += "pe" + PatternEditorNumber;
            PatternEditorNumber++;
            return name;
        }

        // Old machines expect this when pos in tick == 0
        internal void Tick(MachineCore machine)
        {
            var wi = GetMachineWorkInstance(machine);
            wi.Tick(true, true);
        }

        internal void AudioBeginFrame(MachineCore machine)
        {
        }

        internal string DescribeValue(MachineCore machine, int index, int value)
        {
            string str = value.ToString();

            if (managedMachines.TryGetValue(machine, out ManagedMachineHost hm))
            {
                str = hm.DescribeParameterValue(index, value);
            }

            return str;
        }

        internal void Command(MachineCore machine, int x)
        {
            if (managedMachines.ContainsKey(machine))
            {
                managedMachines[machine].ExecuteCommad((BuzzCommand)x);
            }
        }

        internal void AudioBeginBlock(MachineCore machine)
        {
        }

        internal void DeleteMachine(MachineCore machine)
        {
            if (machine == null)
                return;

            lock (ReBuzzCore.AudioLock)
            {
                machine.Ready = false;
                machine.MachineDLL.IsLoaded = false;
                machine.ClearEvents();

                var gui = machine.gui;
                if (gui != null)
                {
                    gui.Machine = null;
                    gui = null;
                }

                (machine.Graph?.Buzz as ReBuzzCore)?.UiHost?.ReleaseMachine(machine);   // fork (engine-split.md S3): the parameter window lets go

                if (managedMachines.ContainsKey(machine))
                {
                    var machineHost = managedMachines[machine];
                        
                    machineHost.Release();
                    managedMachines.Remove(machine);
                }

                if (workInstances.ContainsKey(machine))
                {
                    workInstances.TryRemove(machine, out var dummy);
                }
            }
        }

        internal void SendMidiInput(IMachine machine, int data, bool polacConversion)
        {
            if (machine == null)
                return;
            // Fork (machine keyboard): the keys of the keyboard under its window light up (Core/Keyboards/NoteMonitor.cs).
            (machine as MachineCore)?.NoteMonitor?.Midi(data);
            //lock (machine)
            //lock (ReBuzzCore.AudioLock)
            {
                int b = MIDI.DecodeStatus(data);
                int data1 = MIDI.DecodeData1(data);
                int data2 = MIDI.DecodeData2(data);
                int channel = 0;
                int commandCode = MIDI.ControlChange;

                if ((b & 0xF0) == 0xF0)
                {
                    // both bytes are used for command code in this case
                    commandCode = b;
                }
                else
                {
                    commandCode = (b & 0xF0);
                    channel = (b & 0x0F);
                }

                var mc = machine as MachineCore;
                // Fork (note-wires.md): a track with note inserts hands its clip notes, live MIDI and keys to the first insert.
                if (mc != null && Buzz?.NoteWires is { } nw && nw.RouteTrackInput(mc, data)) return;
                if (managedMachines.ContainsKey(mc))
                {
                    var mmh = managedMachines[mc];
                    if (commandCode == MIDI.NoteOn)
                    {
                        mmh.MidiNote(channel, data1, data2);
                    }
                    else if (commandCode == MIDI.NoteOff)
                    {
                        mmh.MidiNote(channel, data1, 0);
                    }
                    else if (commandCode == MIDI.ControlChange)
                    {
                        mmh.MidiControlChange(data1, channel, data2);
                    }
                    else if (commandCode == MIDI.PitchWheel)
                    {
                        int val = (data1 | (data2 << 7));
                        if (polacConversion)
                            mmh.MidiControlChange(0xff, channel, val);
                        else
                            mmh.MidiControlChange(commandCode, channel, val);
                    }
                    else if (commandCode == MIDI.ChannelAftertouch) // Aftertouch
                    {
                        if (polacConversion)
                            mmh.MidiControlChange(0xfc, channel, data1);
                        else
                            mmh.MidiControlChange(commandCode, channel, data1);
                    }
                }
            }
        }

        internal byte[] SendGUIMessage(MachineCore machine, byte[] message)
        {

            return null;
        }

        internal void SetNumTracks(MachineCore machine, int trackCount)
        {
        }

        internal string GetChannelName(MachineCore machine, bool input, int index)
        {
            string ret = "";

            if (managedMachines.ContainsKey(machine))
            {
                ret = managedMachines[machine].GetChannelName(input, index);
            }

            return ret;
        }

        /// <summary>The pattern editor machine's control (a WPF UserControl in the app), or null.</summary>
        internal object GetPatternEditorControl(MachineCore machine)
        {
            object ret = null;
            if (machine != null && machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                var machineHost = ManagedMachines[machine];
                ret = machineHost.PatternEditorControl;
            }
            return ret;
        }

        internal void SetPatternEditorPattern(MachineCore machine, IPattern p)
        {
            if (machine == null) return;                // fork (mac-engine.md M2): no editor machine
            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine) && p != null)
            {
                try
                {
                    var machineHost = ManagedMachines[machine];
                    machineHost.SetEditorPattern = p;
                }
                catch { }
            }
        }

        internal void RecordContolChange(MachineCore machine, ParameterCore parameter, int track, int value)
        {
            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                var machineHost = ManagedMachines[machine];
                machineHost.RecordControlChange(parameter, track, value);
            }
        }

        internal void CreatePatternCopy(MachineCore machine, IPattern newp, IPattern p)
        {
            if (machine == null) return;                // fork (mac-engine.md M2): no editor machine
            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                var machineHost = ManagedMachines[machine];
                machineHost.CreatePatternCopy(newp, p);
            }
        }

        internal byte[] GetMachineData(MachineCore machine)
        {
            // ToDo: Add checks to all machine calls and check if machine has crashed.
            if (machine.DLL.IsCrashed)
                return null;

            // Maybe fix: lock audio & midi so that there are no interruptions when getting vst data
            lock (ReBuzzCore.AudioLock)
            {
                if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
                {
                    var machineHost = ManagedMachines[machine];
                    if (machine.DLL.Info.Flags.HasFlag(MachineInfoFlags.PATTERN_EDITOR))
                    {
                        return machineHost.GetPatternEditorData();
                    }
                    else
                    {
                        var data = machineHost.MachineState;
                        // Fork (machines.md §3.4): a machine with a state version above 0 appends it (byte 3 envelope);
                        // version 0 machines save exactly what they always did (byte 2, size, content).
                        int stateVersion = (machineHost.ManagedMachine as IVersionedMachine)?.StateVersion ?? 0;
                        return StateEnvelope.Write(data, stateVersion);
                    }
                }
                return null;
            }
        }

        internal void SetMachineData(MachineCore machine, byte[] value)
        {
            if (machine.DLL.IsCrashed)
                return;

            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                var machineHost = ManagedMachines[machine];
                if (machine.DLL.Info.Flags.HasFlag(MachineInfoFlags.PATTERN_EDITOR))
                {
                    machine.LoadedEditorData = value;      // fork (clips.md §7.1, CU3): the migration on open reads what the file gave
                    machineHost.SetPatternEditorData(value);
                }
                else if (value != null)
                {
                    var (content, savedVersion) = StateEnvelope.Read(value);
                    machine.LoadedStateVersion = savedVersion;
                    // Fork (machines.md §3.4): data saved by an older layout is mapped to the current one, here on the loader thread, before the machine sees it.
                    if (machineHost.ManagedMachine is IVersionedMachine versioned && savedVersion != versioned.StateVersion)
                    {
                        byte[] migrated = savedVersion < versioned.StateVersion ? versioned.MigrateData(savedVersion, content) : null;
                        if (migrated == null)
                            buzz.DCWriteLine($"{machine.Name}: its data was saved at state version {savedVersion}, this build has {versioned.StateVersion}; " +
                                             (savedVersion > versioned.StateVersion ? "it comes from a newer build, so its own data is left out." : "the migration failed, so its own data is left out."), DCLogLevel.Error);
                        content = migrated ?? Array.Empty<byte>();
                    }
                    machineHost.MachineState = content;
                }
            }
        }

        internal void Stop(MachineCore machine)
        {
            if (machine.DLL.IsCrashed)
                return;

            machine.IsSeqThru = machine.IsSeqMute = false;

            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                var machineHost = ManagedMachines[machine];
                machineHost.Stop();
            }
        }

        // Alternative to Stop() that sends note offs to all note parameters and tracks, but does not call Stop() on the machine.
        internal void SendNoteOffs(MachineCore machine) 
        {
            // Send note offs to parameters and tracks
            for (int i = 1; i < machine.ParameterGroupsList.Count; i++)
            {
                var g = machine.ParameterGroupsList[i];
                for (int j = 0; j < g.ParametersList.Count; j++)
                {
                    var p = g.ParametersList[j];
                    for (int t = 0; t < g.TrackCount; t++)
                    {
                        if (p.Type == ParameterType.Note)
                            p.SetValue(t, BuzzNote.Off);
                    }
                }
            }
        }

        internal void UpdateMasterAndSubTickInfoToHost()
        {
            bool structDirty = ReBuzzCore.masterInfoStructDirty;
            if (structDirty)
                ReBuzzCore.masterInfoStructDirty = false;

            var masterInfo = ReBuzzCore.masterInfo;
            var subTickInfo = ReBuzzCore.subTickInfo;

            foreach (var machineHost in managedMachines.Values)
            {
                if (machineHost == null) continue;

                var hostInfo = machineHost.MasterInfo;
                var hostSubTick = machineHost.SubTickInfo;

                // Position fields - always copy (advance every chunk)
                hostInfo.PosInTick = masterInfo.PosInTick;
                hostSubTick.CurrentSubTick = subTickInfo.CurrentSubTick;
                hostSubTick.PosInSubTick = subTickInfo.PosInSubTick;

                if (structDirty)
                {
                    // Structural fields - only when changed
                    hostInfo.SamplesPerTick = masterInfo.SamplesPerTick;
                    hostInfo.BeatsPerMin = masterInfo.BeatsPerMin;
                    hostInfo.TicksPerBeat = masterInfo.TicksPerBeat;
                    hostInfo.SamplesPerSec = masterInfo.SamplesPerSec;
                    hostInfo.TicksPerSec = masterInfo.TicksPerSec;
                    hostSubTick.SubTicksPerTick = subTickInfo.SubTicksPerTick;
                    hostSubTick.SamplesPerSubTick = subTickInfo.SamplesPerSubTick;
                }
            }
        }

        internal void UpdateMasterAndSubTickInfo(ManagedMachineHost machineHost)
        {
            var masterInfo = ReBuzzCore.masterInfo;
            var hostInfo = machineHost.MasterInfo;
            hostInfo.PosInTick = masterInfo.PosInTick;
            hostInfo.SamplesPerTick = masterInfo.SamplesPerTick;
            hostInfo.BeatsPerMin = masterInfo.BeatsPerMin;
            hostInfo.TicksPerBeat = masterInfo.TicksPerBeat;
            hostInfo.SamplesPerSec = masterInfo.SamplesPerSec;
            hostInfo.TicksPerSec = masterInfo.TicksPerSec;

            var subTickInfo = ReBuzzCore.subTickInfo;
            var hostSubTick = machineHost.SubTickInfo;
            hostSubTick.CurrentSubTick = subTickInfo.CurrentSubTick;
            hostSubTick.SubTicksPerTick = subTickInfo.SubTicksPerTick;
            hostSubTick.SamplesPerSubTick = subTickInfo.SamplesPerSubTick;
            hostSubTick.PosInSubTick = subTickInfo.PosInSubTick;
        }
        /// <summary>
        /// Fork (history-snapshots.md HS3): makes the machine's work instance now (UI thread) if its host is
        /// registered (the instance keeps the host it finds), so the audio thread never makes it.
        /// </summary>
        internal void PrepareWorkInstance(MachineCore machine)
        {
            if (machine != null && managedMachines.ContainsKey(machine))
                GetMachineWorkInstance(machine);
        }

        internal MachineWorkInstance GetMachineWorkInstance(MachineCore machine)
        {
            if (workInstances.TryGetValue(machine, out MachineWorkInstance wi))
            {
                return wi;
            }
            else
            {
                var mwi = new MachineWorkInstance(machine, Buzz, engineSettings);
                workInstances[machine] = mwi;
                return mwi;
            }
        }

        internal string[] GetSubMenu(MachineCore machine, int i)
        {
            string[] res = null;
            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                // Implement Load if needed
            }

            return res;
        }

        internal void SendMIDIControlChange(MachineCore machine, int ctrl, int channel, int value)
        {
            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                var machineHost = ManagedMachines[machine];
                machineHost.MidiControlChange(ctrl, channel, value);
            }
        }

        /// <summary>Fork (note-wires.md): the clip player's note-off for a note it started: to the first insert of the track's note chain, else to the machine.</summary>
        internal void SendTrackNoteOff(MachineCore machine, int channel, int note)
        {
            if (Buzz?.NoteWires is { } nw && nw.RouteTrackNoteOff(machine, channel, note)) return;
            SendMIDINote(machine, channel, note, 0);
        }

        internal void SendMIDINote(MachineCore machine, int channel, int value, int velocity)
        {
            machine.NoteMonitor?.Note(channel, value, velocity);     // fork (machine keyboard)
            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                var machineHost = ManagedMachines[machine];
                machineHost.MidiNote(channel, value, velocity);
            }
        }

        internal void ImportFinished(MachineCore machine, IDictionary<string, string> importDictionary)
        {
            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                var machineHost = ManagedMachines[machine];
                machineHost.ImportFinished(importDictionary);
            }
        }

        internal IEnumerable<IMenuItem> GetCommands(MachineCore machine)
        {
            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                var machineHost = ManagedMachines[machine];
                return machineHost.Commands;
            }
            return null;
        }

        internal void SetValue(MachineCore machine, ParameterCore parameter, int track, int value)
        {
            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                if (parameter.Group.Type == ParameterGroupType.Input)
                {
                    return;
                }
                var machineHost = ManagedMachines[machine];
                int index = parameter.IndexInGroup;
                if (parameter.Group.Type == ParameterGroupType.Track)
                    index += machine.ParameterGroups[1].Parameters.Count;
                machineHost.SetParameterValue(index, track, value);
            }
        }

        internal int[] GetPatternEditorMachineMIDIEvents(MachineCore machine, PatternCore pattern)
        {
            var editor = machine.EditorMachine;
            if (managedMachines.ContainsKey(editor))
            {
                var machineHost = ManagedMachines[editor];
                return machineHost.GetPatternEditorMachineMIDIEvents(pattern);
            }
            return new int[0];
        }

        internal void SetPatternEditorMachineMIDIEvents(MachineCore machine, PatternCore pattern, int[] data)
        {
            var editor = machine.EditorMachine;
            if (managedMachines.ContainsKey(editor))
            {
                var machineHost = ManagedMachines[editor];
                machineHost.SetPatternEditorMachineMIDIEvents(pattern, data);
            }
        }

        internal IEnumerable<IPatternEditorColumn> PatternEditorMachineEvents(MachineCore machine, PatternCore patternCore)
        {
            var editor = machine.EditorMachine;
            if (managedMachines.ContainsKey(editor))
            {
                var machineHost = ManagedMachines[editor];
                return machineHost.GetPatternCloumnEvents(patternCore, 0, int.MaxValue);
            }
            return new List<IPatternEditorColumn>();
        }

        /// <summary>Fork (clips.md §6, CU3): tells the machine's pattern editor that the song's clips are in the store: it drops what it held and views the store. False when it has no such editor.</summary>
        internal bool AttachClipStore(MachineCore machine)
        {
            var em = machine?.EditorMachine;
            if (em == null || !managedMachines.TryGetValue(em, out var host) || !host.HasClipStore) return false;
            host.AttachClipStore();
            return true;
        }

        internal void ActivateEditor(MachineCore em)
        {
            if (em != null && managedMachines.ContainsKey(em))
            {
                var machineHost = ManagedMachines[em];
                machineHost.Activate();
            }
        }

        internal void LostMidiFocus(MachineCore machine)
        {
        }

        internal void GotMidiFocus(MachineCore machine)
        {
        }

        internal void SetInstrument(MachineCore machine, string newInstrument)
        {
        }

        internal void AddInput(MachineCore machine, IMachine source, bool stereo)
        {
            lock (ReBuzzCore.AudioLock)
            {
                // Check also if machine has MIF_DOES_INPUT_MIXING?
            }
        }
        internal void DeleteInput(MachineCore machine, IMachine source)
        {
        }

        internal void RenameInput(MachineCore machine, string oldName, string newName)
        {
        }

        internal void SetInputChannels(MachineCore machine, string name, bool stereo)
        {
        }

        internal int GetTicksPerBeat(MachineCore machine, IPattern pattern, int pp)
        {
            if (machine.EditorMachine != null)
            {
                var host = managedMachines[machine.EditorMachine];
                return host.GetTicksPerBeat(machine, pattern, pp);
            }
            return 4; // Buzz ticks per beat
        }

        internal void UpdateWaveReferences(MachineCore machine, MachineCore editorTargetMachine, Dictionary<int, int> remappedWaveReferences)
        {
            if (machine.DLL.IsManaged && managedMachines.ContainsKey(machine))
            {
                var machineHost = ManagedMachines[machine];
                machineHost.UpdateWaveReferences(machine, editorTargetMachine, remappedWaveReferences);
            }
        }

        internal int GetEditorPatternPosition(MachineCore machine)
        {
            if (machine.EditorMachine != null)
            {
                var host = managedMachines[machine.EditorMachine];
                return host.GetEditorPatternPosition();
            }
            return 0;
        }
    }
}