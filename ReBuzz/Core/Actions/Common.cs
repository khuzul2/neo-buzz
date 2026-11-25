using BuzzGUI.Interfaces;
using ReBuzz.Core.Actions.GraphActions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Actions
{
    public class MachineInfoRef
    {
        public IMachineDLL ClipEditorDLL { get; internal set; }
        public byte[] ClipEditorData { get; internal set; }
        public string Name { get; set; }
        public Tuple<float, float> Pos { get; set; }
        public byte[] Data { get; set; }
        public string ClipEditorMachineName { get; }
        public string ClipEditorDllName { get; internal set; }
        public string MachineLib { get; internal set; }
        public string Instrument { get; internal set; }
        public int TrackCount { get; internal set; }
        internal List<AttributeCore> Attributes { get; set; }

        public Dictionary<int, IEnumerable<KeyValuePair<int, SequenceEventRef>>> sequences = new Dictionary<int, IEnumerable<KeyValuePair<int, SequenceEventRef>>>();
        public Dictionary<string, int> clipAssociations = new Dictionary<string, int>();
        public List<ConnectionInfoRef> connections = new List<ConnectionInfoRef>();
        internal List<ParameterGroup> parameterGroups = new List<ParameterGroup>();
        internal List<ClipInfoRef> clips = new List<ClipInfoRef>();
        internal List<DisconnectMachinesAction> disconnectActions = new List<DisconnectMachinesAction>();

        public MachineInfoRef(MachineCore machine)
        {
            Name = machine.Name;
            if (machine.ClipEditorDLL != null)
            {
                ClipEditorDllName = machine.ClipEditorDLL.Name;
            }
            if (machine.EditorMachine != null)
            {
                ClipEditorMachineName = machine.EditorMachine.Name;
            }
            Pos = new Tuple<float, float>(machine.Position.Item1, machine.Position.Item2);
            MachineLib = machine.DLL.Name;
            Instrument = machine.InstrumentName;
            TrackCount = machine.TrackCount;
            ClipEditorData = machine.ClipEditorData;
            Data = machine.Data;

            // Save Attributes
            Attributes = machine.AttributesList.ToList();

            // Save parameter values
            // Fork (fix/long-session-slowdown): the copies belong to no machine. A clone points at the machine (and, through
            // its parameters, at the original group, so at the machine again): the undo history kept every deleted machine
            // alive with its buffers. GetGroup gives a group its machine when an undo needs one.
            for (int g = 0; g < 3; g++)
            {
                ParameterGroup pg = machine.ParameterGroupsList[g].Clone();
                pg.Machine = null;
                foreach (var p in pg.ParametersList) p.Group = pg;
                parameterGroups.Add(pg);
            }

            // Save Patterns
            foreach (var clip in machine.ClipsList)
            {
                clips.Add(new ClipInfoRef(clip.Name, clip.Length));
            }
        }

        internal ParameterGroup GetGroup(MachineCore machine, int index)
        {
            var pg = parameterGroups[index];
            pg.Machine = machine;
            for (int i = 0; i < pg.ParametersList.Count; i++)
            {
                pg.ParametersList[i].Group = pg;
            }

            return pg;
        }

        internal void CopyParameters(MachineCore machine, int group, int track)
        {
            var parametersTo = machine.ParameterGroupsList[group].ParametersList;
            var parametersFrom = parameterGroups[group].ParametersList;

            for (int i = 0; i < parametersTo.Count; i++)
            {
                // Set the defaul/saved state of parameters. Skip non-state, notes and input group
                if (parametersTo[i].Flags.HasFlag(ParameterFlags.State) && parametersTo[i].Type != ParameterType.Note && group != 0)
                {
                    if (machine.DLL.IsManaged)
                    {
                        parametersTo[i].SetValue(track, parametersFrom[i].GetValue(track));
                    }
                    else
                    {
                        parametersTo[i].DirectSetValue(track, parametersFrom[i].GetValue(track));
                    }
                }
            }
        }

        internal void AddSequence(int index, ReadOnlyDictionary<int, SequenceEvent> sourceEvents)
        {
            Dictionary<int, SequenceEventRef> targetEvents = new Dictionary<int, SequenceEventRef>();
            foreach (var kvSource in sourceEvents)
            {
                var e = kvSource.Value;
                targetEvents.Add(kvSource.Key, new SequenceEventRef(e));
            }
            sequences.Add(index, targetEvents);
        }
    }

    public class ClipInfoRef
    {
        public string Name { get; }
        public int Lenght { get; }
        public ClipInfoRef(string name, int lenght)
        {
            Name = name;
            Lenght = lenght;
        }
    }

    public class SequenceEventRef
    {
        public SequenceEventType Type { get; private set; }

        public string ClipName { get; private set; }

        public int Span { get; set; }

        // Fork (0.5.1): the part of the pattern the event plays, and its volume.
        public int Offset { get; private set; }
        public int End { get; private set; }
        public double Gain { get; private set; } = 1.0;
        // Fork (daw-modes.md S2): the arrangement clip it belongs to (0: none).
        public int PlacementId { get; private set; }

        public SequenceEventRef(SequenceEventType type, string clip, int span)
        {
            Type = type;
            ClipName = clip;
            Span = span;
        }

        public SequenceEventRef(SequenceEvent e) : this(e.Type, e.Clip?.Name, e.Span)
        {
            Offset = e.Offset;
            End = e.End;
            Gain = e.Gain;
            PlacementId = e.PlacementId;
        }

        /// <summary>The event for a pattern (found by name by the caller).</summary>
        public SequenceEvent ToEvent(IClip clip) =>
            new SequenceEvent(Type, clip, Span) { Offset = Offset, End = End, Gain = Gain, PlacementId = PlacementId };
    }

    public class ConnectionInfoRef
    {

        public ConnectionInfoRef(IMachineConnection m)
        {
            Source = m.Source.Name;
            SourceChannels = m.SourceChannels.ToArray();
            Destination = m.Destination.Name;
            DestinationChannels = m.DestinationChannels.ToArray();
            Amp = m.Amp;
            Pan = m.Pan;
        }

        public string Source { get; internal set; }
        public IEnumerable<int> SourceChannels { get; internal set; }
        public string Destination { get; internal set; }
        public int Amp { get; internal set; }
        public int Pan { get; internal set; }
        public IEnumerable<int> DestinationChannels { get; internal set; }
    }

}
