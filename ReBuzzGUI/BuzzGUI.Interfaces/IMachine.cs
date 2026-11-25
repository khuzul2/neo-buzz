using Buzz.MachineInterface;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace BuzzGUI.Interfaces
{
    public enum MachineDialog { Parameters, Attributes, SignalAnalysis, Rename, Delay, Clips };

    public interface IMachine : INotifyPropertyChanged
    {
        IMachineGraph Graph { get; }
        IMachineDLL DLL { get; }

        ReadOnlyCollection<IMachineConnection> Inputs { get; }
        ReadOnlyCollection<IMachineConnection> Outputs { get; }

        int InputChannelCount { get; }
        int OutputChannelCount { get; }

        string Name { get; set; }
        Tuple<float, float> Position { get; }
        int OversampleFactor { get; set; }
        int MIDIInputChannel { get; set; }

        ReadOnlyCollection<IParameterGroup> ParameterGroups { get; }
        ReadOnlyCollection<IAttribute> Attributes { get; }
        IEnumerable<IMenuItem> Commands { get; }

        ReadOnlyCollection<string> EnvelopeNames { get; }

        ReadOnlyCollection<IClip> Clips { get; }

        bool IsControlMachine { get; }
        bool IsActive { get; }
        bool IsMuted { get; set; }
        bool IsSoloed { get; set; }
        bool IsBypassed { get; set; }
        bool IsWireless { get; set; }
        bool HasStereoInput { get; }
        bool HasStereoOutput { get; }

        int LastEngineThread { get; }

        int Latency { get; }                                                // latency given by the machine
        int OverrideLatency { get; set; }                                   // -1 = don't override

        IMachineDLL ClipEditorDLL { get; }

        int BaseOctave { get; set; }

        byte[] Data { get; set; }                                           // calls CMachineInterface::Save and CMachineInterfaceEx::Load
        byte[] ClipEditorData { get; }                                   // calls CMachineInterface::Save of the pattern editor machine

        IntPtr CMachinePtr { get; }                                         // native CMachine *


        MachinePerformanceData PerformanceData { get; }

        IBuzzMachine ManagedMachine { get; }

        int TrackCount { get; set; }                                        // an alias for ParameterGroups[2].TrackCount

        void ShowPresetEditor();
        void CopyParameters();
        void ShowHelp();
        void UnbindAllMIDIControllers();

        void ShowContextMenu(int x, int y);
        void DoubleClick();

        void ShowDialog(MachineDialog d, int x, int y);
        void ExecuteCommand(int id);

        string GetChannelName(bool input, int index);

        byte[] SendGUIMessage(byte[] message);

        void SendControlChanges();                                      // CMICallbacks::SendControlChanges
        void SendMIDINote(int channel, int value, int velocity);        // CMICallbacks::SendMidiNote
        void SendMIDIControlChange(int ctrl, int channel, int value);   // CMICallbacks::SendMidiControlChange

        void CreateClip(string name, int length);
        void CloneClip(string name, IClip p);
        void DeleteClip(IClip p);

        void RenameClip(IClip p, string name);

        event Action<IClip> ClipAdded;
        event Action<IClip> ClipRemoved;

    }
}
