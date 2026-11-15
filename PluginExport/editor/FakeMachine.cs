// The machine a Neo editor sees in the plug-in: BuzzGUI's IMachine, IParameterGroup and IParameter over the Shared struct. The
// editor kit binds to global parameter i of the machine (EditorContext); here parameter i is the plug-in's parameter i. Reads come
// from a local copy kept in step with Shared.Param by Poll (host automation, state loads); writes go to the host through
// EditorInstance.UserSet (the edit ring, as gesture + value events).
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoExport
{
    internal sealed class FakeParameter : IParameter
    {
        readonly FakeGroup group;
        readonly ParamDef def;
        readonly EditorInstance owner;
        readonly List<Action<IParameter, int>> subscribers = new List<Action<IParameter, int>>();
        public int Value;

        public FakeParameter(FakeGroup group, ParamDef def, EditorInstance owner)
        {
            this.group = group; this.def = def; this.owner = owner; Value = def.Default;
        }

        public IParameterGroup Group => group;
        public int IndexInGroup => def.Index;
        public ParameterType Type => ParameterType.Word;
        public string Name => def.Name;
        public string Description => def.Description ?? "";
        public int MinValue => def.Min;
        public int MaxValue => def.Max;
        public int NoValue => def.Min - 1;
        public ParameterFlags Flags => ParameterFlags.State;
        public int DefValue => def.Default;
        public string GetDisplayName(int track) => def.Name;
        public int GetValue(int track) => Value;

        /// <summary>The editor writes: the local copy, every subscriber (the writer's own control too, as ReBuzz does), and the host.</summary>
        public void SetValue(int track, int value)
        {
            value = Math.Clamp(value, def.Min, def.Max);
            if (value == Value) return;
            Value = value;
            Notify();
            owner.UserSet(def.Index, value);
        }

        /// <summary>The host (automation, state) wrote: the local copy and the subscribers, not the host.</summary>
        public void Arrived(int value)
        {
            if (value == Value) return;
            Value = value;
            Notify();
        }

        void Notify()
        {
            foreach (var s in subscribers.ToArray())
            {
                try { s(this, 0); } catch (Exception) { }
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Value"));
        }

        public string DescribeValue(int value) => def.Describe(value);
        public void BindToMIDIController(int track, int mcindex) { }
        public void BindToMIDIController(int track, int channel, int controller) { }
        public bool IsValidAsciiChar(int ch) => false;
        public void SubscribeEvents(int track, Action<IParameter, int> valueChanged, Action<IParameter, int> valueDescriptionChanged) { if (valueChanged != null) subscribers.Add(valueChanged); }
        public void UnsubscribeEvents(int track, Action<IParameter, int> valueChanged, Action<IParameter, int> valueDescriptionChanged) { subscribers.Remove(valueChanged); }
        public (int, int) GetMIDIBinding(int track) => (-1, -1);
        public event Action<int> MIDIBindingChanged { add { } remove { } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed class FakeGroup : IParameterGroup
    {
        public FakeMachine MachineOwner;
        public readonly ParameterGroupType GroupType;
        public readonly List<FakeParameter> List = new List<FakeParameter>();
        ReadOnlyCollection<IParameter> view;

        public FakeGroup(FakeMachine m, ParameterGroupType type) { MachineOwner = m; GroupType = type; }
        public IMachine Machine => MachineOwner;
        public ParameterGroupType Type => GroupType;
        public ReadOnlyCollection<IParameter> Parameters => view ??= new ReadOnlyCollection<IParameter>(new List<IParameter>(List));
        public int TrackCount { get => GroupType == ParameterGroupType.Global ? 1 : 0; set { } }
        public void Seal() => view = null;
    }

    internal sealed class FakeMachine : IMachine
    {
        readonly FakeGroup global;
        readonly FakeGroup input, track;
        readonly ReadOnlyCollection<IParameterGroup> groups;
        readonly string name;

        public FakeMachine(string name, ParamDef[] table, EditorInstance owner, IBuzzMachine managed)
        {
            this.name = name; ManagedMachine = managed;
            input = new FakeGroup(this, ParameterGroupType.Input);
            global = new FakeGroup(this, ParameterGroupType.Global);
            track = new FakeGroup(this, ParameterGroupType.Track);
            foreach (var d in table) global.List.Add(new FakeParameter(global, d, owner));
            groups = new ReadOnlyCollection<IParameterGroup>(new IParameterGroup[] { input, global, track });
        }

        public int Count => global.List.Count;
        public FakeParameter P(int i) => global.List[i];

        public IMachineGraph Graph => null;
        public IMachineDLL DLL => null;
        public ReadOnlyCollection<IMachineConnection> Inputs { get; } = new ReadOnlyCollection<IMachineConnection>(new List<IMachineConnection>());
        public ReadOnlyCollection<IMachineConnection> Outputs { get; } = new ReadOnlyCollection<IMachineConnection>(new List<IMachineConnection>());
        public int InputChannelCount => 2;
        public int OutputChannelCount => 2;
        public string Name { get => name; set { } }
        public Tuple<float, float> Position => Tuple.Create(0f, 0f);
        public int OversampleFactor { get; set; } = 1;
        public int MIDIInputChannel { get; set; }
        public ReadOnlyCollection<IParameterGroup> ParameterGroups => groups;
        public ReadOnlyCollection<IAttribute> Attributes { get; } = new ReadOnlyCollection<IAttribute>(new List<IAttribute>());
        public IEnumerable<IMenuItem> Commands => Array.Empty<IMenuItem>();
        public ReadOnlyCollection<string> EnvelopeNames { get; } = new ReadOnlyCollection<string>(new List<string>());
        public ReadOnlyCollection<IPattern> Patterns { get; } = new ReadOnlyCollection<IPattern>(new List<IPattern>());
        public bool IsControlMachine => false;
        public bool IsActive => true;
        public bool IsMuted { get; set; }
        public bool IsSoloed { get; set; }
        public bool IsBypassed { get; set; }
        public bool IsWireless { get; set; }
        public bool HasStereoInput => true;
        public bool HasStereoOutput => true;
        public int LastEngineThread => 0;
        public int Latency => 0;
        public int OverrideLatency { get; set; } = -1;
        public IMachineDLL PatternEditorDLL => null;
        public int BaseOctave { get; set; } = 4;
        public byte[] Data { get; set; }
        public byte[] PatternEditorData => null;
        public IntPtr CMachinePtr => IntPtr.Zero;
        public MachinePerformanceData PerformanceData => null;
        public IBuzzMachine ManagedMachine { get; }
        public int TrackCount { get; set; }
        public void ShowPresetEditor() { }
        public void CopyParameters() { }
        public void ShowHelp() { }
        public void UnbindAllMIDIControllers() { }
        public void ShowContextMenu(int x, int y) { }
        public void DoubleClick() { }
        public void ShowDialog(MachineDialog d, int x, int y) { }
        public void ExecuteCommand(int id) { }
        public string GetChannelName(bool input, int index) => input ? "Input" : "Output";
        public byte[] SendGUIMessage(byte[] message) => null;
        public void SendControlChanges() { }
        public void SendMIDINote(int channel, int value, int velocity) { }
        public void SendMIDIControlChange(int ctrl, int channel, int value) { }
        public void CreatePattern(string name, int length) { }
        public void ClonePattern(string name, IPattern p) { }
        public void DeletePattern(IPattern p) { }
        public void RenamePattern(IPattern p, string name) { }
        public event Action<IPattern> PatternAdded { add { } remove { } }
        public event Action<IPattern> PatternRemoved { add { } remove { } }
        public event PropertyChangedEventHandler PropertyChanged { add { } remove { } }
    }
}
