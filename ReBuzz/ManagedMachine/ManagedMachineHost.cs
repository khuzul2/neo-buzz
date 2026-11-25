using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;

namespace ReBuzz.ManagedMachine
{
    internal class ManagedMachineHost : IBuzzMachineHost, IBuzzMachineHostRuntime
    {
        private ManagedMachineDLL dll;

        private delegate void ControlWorkDelegate();

        private delegate Sample GeneratorWorkDelegate();

        private delegate Sample EffectWorkDelegate(Sample s);

        private delegate bool GeneratorBlockWorkDelegate(Sample[] output, int n, WorkModes mode);

        private delegate bool GeneratorBlockWorkMultiDelegate(IList<Sample[]> output, int n, WorkModes mode);

        private delegate bool EffectBlockWorkDelegate(Sample[] output, Sample[] input, int n, WorkModes mode);

        private delegate bool EffectBlockWorkMultiDelegate(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode);

        private delegate void StopDelegate();

        private delegate void ImportFinishedDelegate(IDictionary<string, string> d);

        private delegate void MidiNoteDelegate(int channel, int value, int velocity);

        private delegate void MidiControlChangeDelegate(int ctrl, int channel, int value);

        private delegate int GetLatencyDelegate();

        private delegate string DescribeValueDelegate(IParameter parameter, int value);

        private delegate string GetChannelNameDelegate(bool input, int index);

        #region Pattern Editor
        // Fork (engine-split.md S3): to the engine a pattern editor's control is an object: a WPF
        // UserControl, or, for machines that do not support .NET 9.0 WPF (such as CLR), a .NET
        // Framework 4.x WinForms UserControl that the UI wraps (WrapWinFormsControl).
        private delegate object ClipEditorControlDelegate();

        /// <summary>Wraps a WinForms pattern editor control (NativeMachineFrameworkUI in the app).</summary>
        internal static Func<object, object> WrapWinFormsControl { get; set; }

        private delegate void SetEditorClipDelegate(IClip clip);

        private delegate void RecordControlChangeDelegate(IParameter parameter, int track, int value);

        private delegate void CreateClipCopyDelegate(IClip pnew, IClip p);

        private delegate void SetClipEditorDataDelegate(byte[] data);

        private delegate byte[] GetClipEditorDataDelegate();

        private delegate bool CanExecuteCommandDelegate(BuzzCommand cmd);

        private delegate void ExecuteCommandDelegate(BuzzCommand cmd);

        private delegate int[] GetClipEditorMachineMIDIEventsDelegate(IClip clip);

        private delegate void SetClipEditorMachineMIDIEventsDelegate(IClip clip, int[] data);

        private delegate IEnumerable<IClipEditorColumn> GetClipEditorEventsDelegate(IClip clip, int tbegin, int tend);

        private delegate void ActivateDelegate();

        // Fork (docs/specs/clips.md §6, CU3): a pattern editor that views the store (the song's clips are its data) is told when the song's clips are in it.
        private delegate void AttachClipStoreDelegate();

        private delegate void ReleaseDelegate();

        private delegate int GetTicksPerBeatDelegate(IClip clip, int playPosition);

        private delegate void UpdateWaveReferencesDelegate(IClip patten, IDictionary<int, int> remap);

        private delegate int GetEditorClipPositionDelegate();
        #endregion

        private ControlWorkDelegate ControlWork;

        private GeneratorWorkDelegate GeneratorWork;

        private EffectWorkDelegate EffectWork;

        private GeneratorBlockWorkDelegate GeneratorBlockWork;

        private GeneratorBlockWorkMultiDelegate GeneratorBlockWorkMulti;

        private EffectBlockWorkDelegate EffectBlockWork;

        private EffectBlockWorkMultiDelegate EffectBlockWorkMulti;

        private StopDelegate StopFunction;

        private ImportFinishedDelegate ImportFinishedFunction;

        private MidiNoteDelegate MidiNoteFunction;

        private MidiControlChangeDelegate MidiControlChangeFunction;

        private GetLatencyDelegate GetLatencyFunction;

        private DescribeValueDelegate DescribeValueFunction;

        private GetChannelNameDelegate GetChannelNameFunction;


        #region Pattern Editor

        private ClipEditorControlDelegate ClipEditorControlFunction;

        private SetEditorClipDelegate SetEditorClipFunction;

        private RecordControlChangeDelegate RecordControlChangeFunction;

        private CreateClipCopyDelegate CreateClipCopyFunction;

        private SetClipEditorDataDelegate SetClipEditorDataFunction;

        private GetClipEditorDataDelegate GetClipEditorDataFunction;

        private CanExecuteCommandDelegate CanExecuteCommandFunction;

        private ExecuteCommandDelegate ExecuteCommandFunction;

        private GetClipEditorMachineMIDIEventsDelegate GetClipEditorMachineMIDIEventsFunction;

        private SetClipEditorMachineMIDIEventsDelegate SetClipEditorMachineMIDIEventsFunction;

        private GetClipEditorEventsDelegate GetClipEditorEventsFunction;

        private ActivateDelegate ActivateFunction;

        private AttachClipStoreDelegate AttachClipStoreFunction;

        private ReleaseDelegate ReleaseFunction;

        private GetTicksPerBeatDelegate GetTicksPerBeatFunction;

        private UpdateWaveReferencesDelegate UpdateWaveReferencesFunction;

        private GetEditorClipPositionDelegate GetEditorClipPositionFunction;
        #endregion

        private IBuzzMachine machine;

        private readonly MachineParameter.Delegates[] parameterDelegates;

        private Sample[] inputBuffer;

        private Sample[] outputBuffer;

        public IMachine Machine { get; set; }

        public MasterInfo MasterInfo { get; private set; }

        public SubTickInfo SubTickInfo { get; private set; }

        public IBuzzMachine ManagedMachine => machine;

        // Fork (plugin-host.md §3.1): IBuzzMachineHostRuntime.
        public bool SongLoading => (Machine?.Graph?.Buzz as ReBuzzCore)?.SongLoading ?? false;
        public IntPtr AudioWorkgroup => ReBuzz.Platform.AudioWorkgroup.CurrentHandle;
        public double AudioWorkgroupPeriod => ReBuzz.Platform.AudioWorkgroup.CurrentPeriod;

        public void SetGlobalParameterInfo(int index, string name, string description, int min, int max, int def, bool visible)
        {
            if (Machine is not MachineCore mc || mc.ParameterGroupsList.Count < 2) return;
            var list = mc.ParameterGroupsList[1].ParametersList;
            if (index < 0 || index >= list.Count) return;
            list[index].SetRuntimeInfo(name, description, min, max, def, !visible);
        }

        public ManagedMachineHost(ManagedMachineDLL dll)
        {
            this.dll = dll;
            MasterInfo = new MasterInfo();
            SubTickInfo = new SubTickInfo();
            machine = dll.CreateMachine(this);
            CreateDelegates();
            parameterDelegates = dll.CreateParameterDelegates(machine);
        }

        private void CreateDelegates()
        {
            MethodInfo method = dll.machineType.GetMethod("Work");
            switch (dll.WorkFunctionType)
            {
                case ManagedMachineDLL.WorkFunctionTypes.Control:
                    ControlWork = Delegate.CreateDelegate(typeof(ControlWorkDelegate), machine, method) as ControlWorkDelegate;
                    break;
                case ManagedMachineDLL.WorkFunctionTypes.Generator:
                    GeneratorWork = Delegate.CreateDelegate(typeof(GeneratorWorkDelegate), machine, method) as GeneratorWorkDelegate;
                    break;
                case ManagedMachineDLL.WorkFunctionTypes.Effect:
                    EffectWork = Delegate.CreateDelegate(typeof(EffectWorkDelegate), machine, method) as EffectWorkDelegate;
                    break;
                case ManagedMachineDLL.WorkFunctionTypes.GeneratorBlock:
                    GeneratorBlockWork = Delegate.CreateDelegate(typeof(GeneratorBlockWorkDelegate), machine, method) as GeneratorBlockWorkDelegate;
                    outputBuffer = new Sample[256];
                    break;
                case ManagedMachineDLL.WorkFunctionTypes.GeneratorBlockMulti:
                    GeneratorBlockWorkMulti = Delegate.CreateDelegate(typeof(GeneratorBlockWorkMultiDelegate), machine, method) as GeneratorBlockWorkMultiDelegate;
                    outputBuffer = new Sample[256];
                    break;
                case ManagedMachineDLL.WorkFunctionTypes.EffectBlock:
                    EffectBlockWork = Delegate.CreateDelegate(typeof(EffectBlockWorkDelegate), machine, method) as EffectBlockWorkDelegate;
                    inputBuffer = new Sample[256];
                    outputBuffer = new Sample[256];
                    break;
                case ManagedMachineDLL.WorkFunctionTypes.EffectBlockMulti:
                    EffectBlockWorkMulti = Delegate.CreateDelegate(typeof(EffectBlockWorkMultiDelegate), machine, method) as EffectBlockWorkMultiDelegate;
                    inputBuffer = new Sample[256];
                    outputBuffer = new Sample[256];
                    break;
            }

            StopFunction = (StopDelegate)GetMethod(typeof(StopDelegate), "Stop");
            ImportFinishedFunction = (ImportFinishedDelegate)GetMethod(typeof(ImportFinishedDelegate), "ImportFinished");
            MidiNoteFunction = (MidiNoteDelegate)GetMethod(typeof(MidiNoteDelegate), "MidiNote");
            MidiControlChangeFunction = (MidiControlChangeDelegate)GetMethod(typeof(MidiControlChangeDelegate), "MidiControlChange");
            GetLatencyFunction = (GetLatencyDelegate)GetMethod(typeof(GetLatencyDelegate), "GetLatency");
            //Take into consideration that some more native machines may want to return System.Windows.Forms.UserControl.
            //In this situation, we can use NativeMachineFrameworkUI to wrap around the System.Windows.Forms.UserControl
            ClipEditorControlFunction = ClipEditorControlOf(dll.machineType.GetMethod("ClipEditorControl"));
            
            SetEditorClipFunction = (SetEditorClipDelegate)GetMethod(typeof(SetEditorClipDelegate), "SetEditorClip");
            RecordControlChangeFunction = (RecordControlChangeDelegate)GetMethod(typeof(RecordControlChangeDelegate), "RecordControlChange");
            CreateClipCopyFunction = (CreateClipCopyDelegate)GetMethod(typeof(CreateClipCopyDelegate), "CreateClipCopy");
            GetChannelNameFunction = (GetChannelNameDelegate)GetMethod(typeof(GetChannelNameDelegate), "GetChannelName");
            SetClipEditorDataFunction = (SetClipEditorDataDelegate)GetMethod(typeof(SetClipEditorDataDelegate), "SetClipEditorData");
            GetClipEditorDataFunction = (GetClipEditorDataDelegate)GetMethod(typeof(GetClipEditorDataDelegate), "GetClipEditorData");
            CanExecuteCommandFunction = (CanExecuteCommandDelegate)GetMethod(typeof(CanExecuteCommandDelegate), "CanExecuteCommand");
            ExecuteCommandFunction = (ExecuteCommandDelegate)GetMethod(typeof(ExecuteCommandDelegate), "ExecuteCommand");
            GetClipEditorMachineMIDIEventsFunction = (GetClipEditorMachineMIDIEventsDelegate)GetMethod(typeof(GetClipEditorMachineMIDIEventsDelegate), "GetClipEditorMachineMIDIEvents");
            SetClipEditorMachineMIDIEventsFunction = (SetClipEditorMachineMIDIEventsDelegate)GetMethod(typeof(SetClipEditorMachineMIDIEventsDelegate), "SetClipEditorMachineMIDIEvents");
            GetClipEditorEventsFunction = (GetClipEditorEventsDelegate)GetMethod(typeof(GetClipEditorEventsDelegate), "GetClipEditorEvents");
            DescribeValueFunction = (DescribeValueDelegate)GetMethod(typeof(DescribeValueDelegate), "DescribeValue");
            ActivateFunction = (ActivateDelegate)GetMethod(typeof(ActivateDelegate), "Activate");
            AttachClipStoreFunction = (AttachClipStoreDelegate)GetMethod(typeof(AttachClipStoreDelegate), "AttachClipStore");
            ReleaseFunction = (ReleaseDelegate)GetMethod(typeof(ReleaseDelegate), "Release");
            GetTicksPerBeatFunction = (GetTicksPerBeatDelegate)GetMethod(typeof(GetTicksPerBeatDelegate), "GetTicksPerBeat");
            UpdateWaveReferencesFunction = (UpdateWaveReferencesDelegate)GetMethod(typeof(UpdateWaveReferencesDelegate), "UpdateWaveReferences");
            GetEditorClipPositionFunction = (GetEditorClipPositionDelegate)GetMethod(typeof(GetEditorClipPositionDelegate), "GetEditorClipPosition");
        }

        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                PropertyInfo property = dll.machineType.GetProperty("Commands");
                if (property == null)
                {
                    return null;
                }
                MethodInfo getMethod = property.GetGetMethod();
                if (getMethod == null)
                {
                    return null;
                }
                object obj = getMethod.Invoke(machine, null);
                return obj as IEnumerable<IMenuItem>;
            }
        }

        public int Latency
        {
            get
            {
                if (GetLatencyFunction == null)
                {
                    return 0;
                }
                return GetLatencyFunction();
            }
        }

        public object ClipEditorControl
        {
            get
            {
                if (ClipEditorControlFunction == null)
                {
                    return null;
                }
                return ClipEditorControlFunction();
            }
        }

        public IClip SetEditorClip
        {
            set
            {
                if (SetEditorClipFunction != null)
                {
                    SetEditorClipFunction(value);
                }
            }
        }

        public byte[] MachineState
        {
            get
            {
                PropertyInfo property = dll.machineType.GetProperty("MachineState");
                if (property == null)
                {
                    return null;
                }
                MethodInfo getMethod = property.GetGetMethod();
                if (getMethod == null)
                {
                    return null;
                }

                object obj = getMethod.Invoke(machine, null);

                if (obj == null)
                    return null;

                using (var ms = new MemoryStream())
                {
                    XmlWriterSettings xmlWriterSettings = new XmlWriterSettings();
                    xmlWriterSettings.NamespaceHandling = NamespaceHandling.OmitDuplicates;
                    xmlWriterSettings.NewLineOnAttributes = false;
                    xmlWriterSettings.Indent = true;
                    XmlWriterSettings settings = xmlWriterSettings;
                    XmlWriter xmlWriter = XmlWriter.Create(ms, settings);
                    XmlSerializer xmlSerializer = new XmlSerializer(obj.GetType());
                    xmlSerializer.Serialize(xmlWriter, obj);
                    xmlWriter.Close();
                    return ms.ToArray();
                }
            }
            set
            {
                PropertyInfo property = dll.machineType.GetProperty("MachineState");
                if (property == null)
                {
                    return;
                }
                MethodInfo setMethod = property.GetSetMethod();
                if (setMethod == null)
                {
                    return;
                }

                if (value == null || value.Length == 0)
                {
                    // Create new instance and set it
                    //object machineStatePropertyInstance = Activator.CreateInstance(property.PropertyType);
                    //property.SetValue(machine, machineStatePropertyInstance, null);
                }
                else
                {
                    using (var ms = new MemoryStream(value))
                    {
                        XmlSerializer xmlSerializer = new XmlSerializer(property.PropertyType);
                        //MemoryStream input = new MemoryStream(value);
                        XmlReader xmlReader = XmlReader.Create(ms);
                        object obj = xmlSerializer.Deserialize(xmlReader);
                        xmlReader.Close();
                        setMethod.Invoke(machine, new object[1] { obj });
                    }
                }
            }
        }

        public bool IsControlMachine { get => ControlWork != null; }

        public void RecordControlChange(IParameter parameter, int track, int value)
        {
            if (RecordControlChangeFunction != null)
            {
                RecordControlChangeFunction(parameter, track, value);
            }
        }

        /// <summary>
        /// The machine's PatternEditorControl(): a WPF UserControl as it is, a WinForms UserControl
        /// wrapped by the UI, or, from a portable pattern editor (declared as returning object), what
        /// its UI makes: an Avalonia control in the studio, null without a UI (studio-mvp.md S6). Any
        /// other return type fails as binding the WPF delegate type did.
        /// </summary>
        private ClipEditorControlDelegate ClipEditorControlOf(MethodInfo method)
        {
            if (method == null)
            {
                return null;
            }
            if (IsOrDerivesFrom(method.ReturnType, "System.Windows.Controls.UserControl"))
            {
                return (ClipEditorControlDelegate)Delegate.CreateDelegate(typeof(ClipEditorControlDelegate), machine, method);
            }
            if (method.ReturnType == typeof(object))
            {
                return (ClipEditorControlDelegate)Delegate.CreateDelegate(typeof(ClipEditorControlDelegate), machine, method);
            }
            if (IsOrDerivesFrom(method.ReturnType, "System.Windows.Forms.UserControl"))
            {
                var winForms = (ClipEditorControlDelegate)Delegate.CreateDelegate(typeof(ClipEditorControlDelegate), machine, method);
                return () =>
                {
                    var control = winForms();
                    var wrap = WrapWinFormsControl;
                    return wrap != null ? wrap(control) : control;
                };
            }
            throw new ArgumentException("PatternEditorControl must return a WPF or WinForms UserControl: " + method.ReturnType.FullName);
        }

        static bool IsOrDerivesFrom(Type type, string fullName)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (t.FullName == fullName)
                    return true;
            }
            return false;
        }

        private Delegate GetMethod(Type t, string name)
        {
            MethodInfo method = dll.machineType.GetMethod(name);
            if (method == null)
            {
                return null;
            }
            return Delegate.CreateDelegate(t, machine, method);
        }


        public void SetParameterValue(int index, int track, int value)
        {
            if (index < dll.globalParameters.Length)
            {
                dll.globalParameters[index].SetValue(this, parameterDelegates[index], -1, value);
            }
            else
            {
                dll.trackParameters[index - dll.globalParameters.Length].SetValue(this, parameterDelegates[index], track, value);
            }
        }

        public unsafe void SetParameters(int* p, int n)
        {
            for (int i = 0; i < n; i++)
            {
                SetParameterValue(p[2 * i] & 0xFFFF, p[2 * i] >> 16, p[2 * i + 1]);
            }
        }

        public bool MultiWork(IList<Sample[]> samplesOut, IList<Sample[]> samplesIn, int n, WorkModes mode)
        {
            bool flag = false;
            if (EffectBlockWorkMulti != null)
            {
                flag = EffectBlockWorkMulti(samplesOut, samplesIn, n, mode);
            }
            else if (GeneratorBlockWorkMulti != null)
            {
                flag = GeneratorBlockWorkMulti(samplesOut, n, mode);
            }
            return flag;
        }

        public bool Work(Sample[] samples, int n, WorkModes mode)
        {
            if (ControlWork != null)
            {
                ControlWork();
                return false;
            }
            else if (GeneratorBlockWork != null)
            {
                Array.Clear(outputBuffer, 0, outputBuffer.Length);
                bool flag = GeneratorBlockWork(outputBuffer, n, mode);
                if (flag && (mode & WorkModes.WM_WRITE) != 0)
                {
                    Array.Copy(outputBuffer, samples, n);
                }
                return flag;
            }
            else if (EffectBlockWork != null)
            {
                if ((mode & WorkModes.WM_READ) != 0)
                {
                    Array.Copy(samples, inputBuffer, n);
                }
                bool flag2 = EffectBlockWork(outputBuffer, inputBuffer, n, mode);
                if (flag2 && (mode & WorkModes.WM_WRITE) != 0)
                {
                    Array.Copy(outputBuffer, samples, n);
                }
                return flag2;
            }
            else if (GeneratorWork != null)
            {
                for (int l = 0; l < n; l++)
                {
                    Sample sample = GeneratorWork();
                    samples[l] = sample;
                    MasterInfo.PosInTick++;
                    SubTickInfo.PosInSubTick++;
                }
                return true;
            }
            else if (EffectWork != null)
            {
                Sample s = default;
                if (samples != null)
                {
                    for (int m = 0; m < n; m++)
                    {
                        s = samples[m];
                        s = EffectWork(s);
                        samples[m] = s;
                        MasterInfo.PosInTick++;
                        SubTickInfo.PosInSubTick++;
                    }
                }
                else
                {
                    for (int num = 0; num < n; num++)
                    {
                        s.L = 0f;
                        s.R = 0f;
                        s = EffectWork(s);
                        samples[num] = s;
                        MasterInfo.PosInTick++;
                        SubTickInfo.PosInSubTick++;
                    }
                }
                return true;
            }
            return false;
        }

        public string DescribeValue(int index, int value)
        {
            if (index < dll.globalParameters.Length)
            {
                return dll.globalParameters[index].DescribeValue(this, parameterDelegates[index], value);
            }
            return dll.trackParameters[index - dll.globalParameters.Length].DescribeValue(this, parameterDelegates[index], value);
        }

        public int MsToSamples(float t)
        {
            return (int)(MasterInfo.SamplesPerSec * (t / 1000f));
        }

        public void Stop()
        {
            if (StopFunction != null)
            {
                StopFunction();
            }
        }

        public void MidiNote(int channel, int value, int velocity)
        {
            if (MidiNoteFunction != null)
            {
                MidiNoteFunction(channel, value, velocity);
            }
        }

        public void MidiControlChange(int ctrl, int channel, int value)
        {
            if (MidiControlChangeFunction != null)
            {
                MidiControlChangeFunction(ctrl, channel, value);
            }
        }

        public void ImportFinished(IDictionary<string, string> d)
        {
            if (ImportFinishedFunction != null)
            {
                ImportFinishedFunction(d);
            }
        }

        public bool IsValidAsciiChar(int index, char ch)
        {
            if (index < 0 || index >= dll.globalParameters.Length + dll.trackParameters.Length)
            {
                return false;
            }
            if (index < dll.globalParameters.Length)
            {
                return dll.globalParameters[index].IsValidAsciiChar(ch);
            }
            return dll.trackParameters[index - dll.globalParameters.Length].IsValidAsciiChar(ch);
        }

        internal void SetParameters()
        {
            int paramIndex = 0;
            var machineGlobaPG = Machine.ParameterGroups[1];
            for (int i = 0; i < machineGlobaPG.Parameters.Count; i++)
            {
                var val = machineGlobaPG.Parameters[i].GetValue(0);
                SetParameterValue(paramIndex, 0, val);

                if (!machineGlobaPG.Parameters[i].Flags.HasFlag(ParameterFlags.State))
                {
                    //machinepg.Parameters[i].SetValue(0, machinepg.Parameters[i].NoValue);
                }
                paramIndex++;
            }

            var machineTrackPG = Machine.ParameterGroups[2];
            for (int i = 0; i < machineTrackPG.Parameters.Count; i++)
            {
                for (int t = 0; t < machineTrackPG.TrackCount; t++)
                {
                    SetParameterValue(paramIndex, t, machineTrackPG.Parameters[i].GetValue(t));

                    if (!machineTrackPG.Parameters[i].Flags.HasFlag(ParameterFlags.State))
                    {
                        //trackpg.Parameters[i].SetValue(t, trackpg.Parameters[i].NoValue);
                    }
                }
                paramIndex++;
            }
        }

        internal void CreateClipCopy(IClip newp, IClip p)
        {
            if (CreateClipCopyFunction != null)
            {
                CreateClipCopyFunction(newp, p);
            }
        }

        internal string GetChannelName(bool input, int index)
        {
            if (GetChannelNameFunction != null)
            {
                return GetChannelNameFunction(input, index);
            }

            return null;
        }

        internal void SetClipEditorData(byte[] data)
        {
            if (SetClipEditorDataFunction != null)
            {
                SetClipEditorDataFunction(data);
            }
        }

        internal byte[] GetClipEditorData()
        {
            if (GetClipEditorDataFunction != null)
            {
                return GetClipEditorDataFunction();
            }

            return null;
        }

        public bool CanExecuteCommad(BuzzCommand cmd)
        {
            if (CanExecuteCommandFunction != null)
            {
                return CanExecuteCommandFunction(cmd);
            }

            return false;
        }

        public void ExecuteCommad(BuzzCommand cmd)
        {
            if (ExecuteCommandFunction != null)
            {
                ExecuteCommandFunction(cmd);
            }

            return;
        }

        internal int[] GetClipEditorMachineMIDIEvents(ClipCore clip)
        {
            if (GetClipEditorMachineMIDIEventsFunction != null)
            {
                return GetClipEditorMachineMIDIEventsFunction(clip);
            }
            else return new int[0];
        }

        internal void SetClipEditorMachineMIDIEvents(ClipCore clip, int[] data)
        {
            if (SetClipEditorMachineMIDIEventsFunction != null)
            {
                SetClipEditorMachineMIDIEventsFunction(clip, data);
            }
        }

        internal IEnumerable<IClipEditorColumn> GetClipCloumnEvents(ClipCore clip, int tbegin, int tend)
        {
            if (GetClipEditorEventsFunction != null)
            {
                return GetClipEditorEventsFunction(clip, tbegin, tend);
            }
            else return new List<IClipEditorColumn>();
        }

        internal string DescribeParameterValue(int index, int value)
        {
            if (DescribeValueFunction != null)
            {
                var parameter = Machine.AllNonInputParameters().ElementAt(index);
                string str = DescribeValueFunction(parameter, value);
                if (str != null)
                    return str;
                else
                    return DescribeValue(index, value);
            }
            else
            {
                return DescribeValue(index, value);
            }
        }

        internal int GetEditorClipPosition()
        {
            if (GetEditorClipPositionFunction != null)
                return GetEditorClipPositionFunction();
            return 0;
        }

        internal void Activate()
        {
            if (ActivateFunction != null)
            {
                ActivateFunction();
            }
        }

        /// <summary>Fork (clips.md §6, CU3): whether this pattern editor views the store, and tells it that the song's clips are there now.</summary>
        internal bool HasClipStore => AttachClipStoreFunction != null;

        internal void AttachClipStore() => AttachClipStoreFunction?.Invoke();

        internal void Release()
        {
            if (ReleaseFunction != null)
            {
                ReleaseFunction();
            }

            //For CLR machines, or machines that are Disposable, 
            //the Dispose() method must be called to properlly free up resources
            //used by that machine (such as native resources)
            if(machine is IDisposable)
            {
                (machine as IDisposable).Dispose();
            }
            machine = null;
            dll = null;
        }

        // Fork (docs/specs/machines.md 3.1): the latency the machine last reported.
        int lastLatency = int.MinValue;

        internal unsafe void Tick(MachineCore machine)
        {
            // Fork: a machine whose latency changes with its settings (a lookahead) reports it
            // here; ReBuzz used to read it only when the machine was created.
            if (GetLatencyFunction != null)
            {
                int latency = GetLatencyFunction();
                if (latency != lastLatency)
                {
                    lastLatency = latency;
                    machine.UpdateLatencyFromMachine(latency);    // fork (mastering-tools.md §4.1): applied before this block's work
                }
            }

            // Fork (engine hardening): the changes the machine's tick took (ParameterChanges), in
            // order, with no enumerator to allocate; and the list itself for the global count
            // (machine.ParameterGroups[1].Parameters built two new collections per track value).
            foreach (var (par, track) in machine.ParameterChanges)
            {
                if (par.Group.Type != ParameterGroupType.Input)
                {
                    int index = par.Group.Type == ParameterGroupType.Global ? par.IndexInGroup : par.IndexInGroup + machine.ParameterGroupsList[1].ParametersList.Count;
                    var val = par.GetPValue(track);

                    // Properties need to stay within min/max
                    if (val >= par.MinValue && val <= par.MaxValue)
                    {
                        SetParameterValue(index, track, val);
                    }
                    else if (par.Type == ParameterType.Note && val == BuzzNote.Off)
                    {
                        SetParameterValue(index, track, val);
                    }
                }
            }
        }

        internal void SetParameterDefaults(MachineCore mc)
        {
            ParameterGroup pg = mc.ParameterGroupsList[1];

            foreach (ParameterCore p in pg.Parameters)
            {
                for (int i = 0; i < pg.TrackCount; i++)
                {
                    if (p.Flags.HasFlag(ParameterFlags.State) && p.Type != ParameterType.Note)
                        p.SetValue(i, p.DefValue);
                }
            }

            pg = mc.ParameterGroupsList[2];

            foreach (ParameterCore p in pg.Parameters)
            {
                for (int i = 0; i < pg.TrackCount; i++)
                {
                    if (p.Flags.HasFlag(ParameterFlags.State) && p.Type != ParameterType.Note)
                        p.SetValue(i, p.DefValue);
                }
            }
        }

        internal int GetTicksPerBeat(MachineCore machine, IClip clip, int pp)
        {
            if (GetTicksPerBeatFunction != null)
            {
                GetTicksPerBeatFunction(clip, pp);
            }
            return 4; // Buzz ticks per beat
        }

        internal void UpdateWaveReferences(MachineCore machine, MachineCore editorTargetMachine, Dictionary<int, int> remappedWaveReferences)
        {
            if (UpdateWaveReferencesFunction != null)
            {
                foreach (var clip in editorTargetMachine.Clips)
                {
                    UpdateWaveReferencesFunction(clip, remappedWaveReferences);
                }
            }
        }

        public int OutputChannelCount
        {
            get
            {
                return Machine.OutputChannelCount;
            }
            set
            {
                int outCount = value;
                if (Machine.DLL.Info.Flags.HasFlag(MachineInfoFlags.MULTI_IO) &&
                    !Machine.DLL.Info.Flags.HasFlag(MachineInfoFlags.CONTROL_MACHINE))
                {
                    if (outCount < 1)
                    {
                        outCount = 1;
                    }
                    (Machine as MachineCore).OutputChannelCount = outCount;
                }
            }
        }

        public int InputChannelCount
        {
            get
            {
                return Machine.InputChannelCount;
            }
            set
            {
                int inCount = value;
                if (Machine.DLL.Info.Flags.HasFlag(MachineInfoFlags.MULTI_IO) &&
                    Machine.DLL.Info.Type == MachineType.Effect)
                {
                    if (inCount < 1)
                    {
                        inCount = 1;
                    }
                    (Machine as MachineCore).InputChannelCount = inCount;
                }
            }
        }
    }
}
