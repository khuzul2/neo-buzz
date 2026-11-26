using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor
{
    [MachineDecl(Name = "Modern Pattern Editor", ShortName = "MPE", Author = "WDE", MaxTracks = 8, InputCount = 0, OutputCount = 0)]
    public class ModernPatternEditorMachine : IBuzzMachine, INotifyPropertyChanged, IGUICallbacks
    {
        public event PropertyChangedEventHandler PropertyChanged;

        IBuzzMachineHost host;
        ClipEditor ModernPatternEditor { get; set; }
        public ModernPatternEditorMachine(IBuzzMachineHost host)
        {
            ModernPatternEditor = new ClipEditor(this);
            ModernPatternEditor.Song = Global.Buzz.Song;
            this.host = host;
        }

        [ParameterDecl()]
        public bool Dummy { get; set; }

        // Fork (neo-buzz audio hygiene): one SongTime, reused (Work made one on every buffer, on the
        // audio thread); the editor reads it during the call only.
        readonly SongTime songTime = new SongTime();

        public void Work()
        {
            songTime.PosInSubTick = host.SubTickInfo.PosInSubTick;

            ModernPatternEditor.Work(songTime);
        }

        public void ImportFinished(IDictionary<string, string> machineNameMap)
        {
            ClipEditorUtils.MachineNameMap = machineNameMap;

            // Redo everyting for this machine in case machine names have been changed after import
            
            var currentMachine = ModernPatternEditor.TargetMachine;
            if (currentMachine != null)
            {
                ModernPatternEditor.TargetMachine = null;

                SetTargetMachine(currentMachine);
            }
        }

        // Add this if Clip Editor Machine
        // Fork (docs/specs/studio-mvp.md S6): off Windows the control is an object, so the machine
        // loads without a UI toolkit: the Avalonia view in the studio's build, null in rebuzz-headless's.
        public object ClipEditorControl()
        {
            return ModernPatternEditor.View;
        }

        public void SetEditorClip(IClip clip)
        {
            SetTargetMachine(clip.Machine);
            ModernPatternEditor.SetEditorClip(clip);
        }

        public void RecordControlChange(IParameter parameter, int track, int value)
        {
            if (ModernPatternEditor != null)
            {
                IMachine machine = parameter.Group.Machine;
                ModernPatternEditor.RecordControlChange(parameter, track, value);
            }
        }

        public void SetTargetMachine(IMachine machine)
        {
            ModernPatternEditor.SetTargetMachine(machine);
        }

        public void SetClipName(string machine, string oldName, string newName)
        {
            var mac = Global.Buzz.Song.Machines.FirstOrDefault(m => m.Name == machine);
            if (mac != null)
            {
                var clip = mac.Clips.FirstOrDefault(p => p.Name == oldName);
                if (clip != null)
                {
                    mac.RenameClip(clip, newName);
                }
            }
        }

        public int GetTicksPerBeatDelegate(IClip clip, int playPosition)
        {
            // We need column to get the specific RPB. Return the defaul for the clip
            var p = ModernPatternEditor.MPEClipsDB.GetMPEClip(clip);
            if (p != null)
            {
                return ModernPatternEditor.MPEClipsDB.GetMPEClip(clip).RowsPerBeat;
            }
            else
            {
                return MPEClipColumn.BUZZ_TICKS_PER_BEAT;
            }
        }

        public void ControlChange(IMachine machine, int group, int track, int param, int value)
        {
            group &= ~(16);
            machine.ParameterGroups[group].Parameters[param].SetValue(track | 1 << 16, value);
        }

        public void SetModifiedFlag()
        {
            Global.Buzz.SetModifiedFlag();
        }

        public class State : INotifyPropertyChanged
        {

            public event PropertyChangedEventHandler PropertyChanged;
        }

        public void SetClipEditorData(byte[] data)
        {
            ModernPatternEditor.SetClipEditorData(data);
        }

        // Fork (clips.md §6, CU3): the song's clips are in the store; the editor views them (found by name by the host).
        public void AttachClipStore()
        {
            ModernPatternEditor.AttachClipStore();
        }

        public byte[] GetClipEditorData()
        {
            return ModernPatternEditor.GetClipEditorData();
        }

        State machineState = new State();
        public State MachineState           // a property called 'MachineState' gets automatically saved in songs and presets
        {
            get { return machineState; }
            set
            {
                machineState = value;
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("MachineState"));
            }
        }

        public bool CanExecuteCommand(BuzzCommand cmd)
        {
            return ModernPatternEditor.CanExecuteCommand(cmd);
        }

        public void ExecuteCommand(BuzzCommand cmd)
        {
            ModernPatternEditor.ExecuteCommand(cmd);
        }

        public void MidiNote(int channel, int value, int velocity)
        {
            ModernPatternEditor.MidiNote(channel, value, velocity);
        }

        public void MidiControlChange(int ctrl, int channel, int value)
        {
            ModernPatternEditor.MidiControlChange(ctrl, channel, value);
        }

        public IEnumerable<IClipEditorColumn> GetClipEditorEvents(IClip clip, int tbegin, int tend)
        {
            return ModernPatternEditor.GetClipColumnEvents(clip, tbegin, tend);
        }

        public void Activate()
        {
            ModernPatternEditor.Activate();
        }

        public void Release()
        {
            ModernPatternEditor.Release();
        }

        public void CreateClipCopy(IClip pnew, IClip p)
        {
            ModernPatternEditor.CreateClipCopy(pnew.Name, p.Name);
        }

        // Update wave references if song/template was imported
        public void UpdateWaveReferences(IClip clip, IDictionary<int, int> remap)
        {
            var mpeClip = ModernPatternEditor.MPEClipsDB.GetMPEClip(clip);
            foreach (var column in mpeClip.MPEClipColumns)
            {
                if (column.Parameter.Flags.HasFlag(ParameterFlags.Wave))
                {
                    var events = column.GetEvents(0, int.MaxValue).ToArray();
                    List<ClipEvent> newEvents = new List<ClipEvent>();
                    for (int i = 0; i < events.Count(); i++)
                    {
                        var e = events[i];
                        int wave = e.Value - 1;
                        if (remap.ContainsKey(wave))
                        {   
                            e.Value = remap[wave] + 1;
                            newEvents.Add(e);
                        }
                    }

                    column.SetEvents(newEvents.ToArray(), false, false);
                    column.SetEvents(newEvents.ToArray(), true, false);
                }
            }
        }

        public int GetEditorClipPosition()
        {
            int pos = 0;
            var clip = ModernPatternEditor.clipControl.Clip;
            var mpeClip = ModernPatternEditor.MPEClipsDB.GetMPEClip(clip.Clip);

            if (clip != null && mpeClip != null)
            {
                pos = (clip.CursorPosition.Beat * mpeClip.RowsPerBeat + clip.CursorPosition.RowInBeat) * ClipControl.BUZZ_TICKS_PER_BEAT / mpeClip.RowsPerBeat;
            }
            return pos;
        }
    }
}
