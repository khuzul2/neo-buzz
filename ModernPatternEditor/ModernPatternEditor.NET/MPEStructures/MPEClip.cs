using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using static WDE.ModernPatternEditor.ClipEditorUtils;
using System.Reflection;
using WDE.ModernPatternEditor.ColumnRenderer;

namespace WDE.ModernPatternEditor.MPEStructures
{
    public class MPEClip
    {
        private int rpb;
        public int RowsPerBeat
        {
            get { return rpb; }
            internal set
            {
                rpb = value;
                foreach (var c in MPEClipColumns)
                {
                    c.SetRPB(rpb);
                }
            }
        }


        // Key = Visual Index
        public Dictionary<int, MPEClipColumn> MPEClipColumnsDict = new Dictionary<int, MPEClipColumn>();

        public List<MPEClipColumn> MPEClipColumns = new List<MPEClipColumn> { };

        public ClipEditor Editor { get; }
        public string ClipName { get; internal set; }

        private IClip clip;
        public IClip Clip
        {
            get { return clip; }
            internal set
            {
                if (clip != null)
                {
                    clip.Machine.PropertyChanged -= Machine_PropertyChanged;
                    clip.PropertyChanged -= Clip_PropertyChanged;

                    foreach (var c in MPEClipColumns)
                    {
                        c.Machine = null;
                        c.Parameter = null;
                    }
                    MPEClipColumns.Clear();
                    MPEClipColumnsDict.Clear();
                }

                clip = value;

                if (clip != null)
                {
                    clip.Machine.PropertyChanged += Machine_PropertyChanged;
                    clip.PropertyChanged += Clip_PropertyChanged;
                    UpdateData();
                }
            }
        }

        internal List<IClipColumn> Columns = new List<IClipColumn>();

        private void Clip_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "Name":
                    IClip clip = (IClip)sender;
                    // Update mainDB
                    Editor.MPEClipsDB.ClipRenamed(clip);
                    break;
                case "Lenght":
                    foreach (var column in MPEClipColumns)
                        column.UpdateLength();
                    break;
            }
        }

        internal IList<MPEClipColumn> AddColumnToGroup(IParameter param)
        {
            List<MPEClipColumn> retList = new List<MPEClipColumn>();

            IMachine paramMachine = param.Group.Machine;
            int numTracks = paramMachine == Editor.SelectedMachine.Machine ?
                paramMachine.TrackCount : 1;

            if (param.Group.Type == ParameterGroupType.Track)
            {
                for (int i = 0; i < numTracks; i++)
                {
                    var t = CreateNewColumn(param, i);
                    retList.Add(t);
                }
            }
            else
            {
                var t = CreateNewColumn(param, 0);
                retList.Add(t);
            }

            UpdateData();

            return retList;
        }

        internal MPEClipColumn CreateNewColumn(IParameter param, int track)
        {
            int index = MPEClipColumns.Count;
            return CreateNewColumn(param, track, index);
        }

        internal MPEClipColumn CreateNewColumn(IParameter param, int track, int columnIndex)
        {
            MPEClipColumn column = new MPEClipColumn(this);
            column.Parameter = param;
            column.Machine = param.Group.Machine;
            column.Graphical = false;
            column.GroupType = param.Group.Type;
            column.ParamTrack = track;

            column.SetBeatCount(Editor.ShownBeatCount);
            column.SetRPB(rpb);
            column.UpdateLength();

            MPEClipColumns.Insert(columnIndex, column);
            MPEClipColumnsDict[columnIndex] = column;

            return column;
        }

        public MPEClipColumn? GetColumn(IClipColumn column)
        {
            int key = GetParamIndex(column.Parameter, column.Track);
            if (MPEClipColumnsDict.ContainsKey(key))
                return MPEClipColumnsDict[key];
            else
                return null;
        }

        public MPEClipColumn? GetColumn(int num)
        {
            if (MPEClipColumnsDict.ContainsKey(num))
                return MPEClipColumnsDict[num];
            else
                return null;
        }

        private void Machine_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "TrackCount":
                    UpdateData();
                    break;
            }
        }

        public void CreateDefaultColumns()
        {
            MPEClipColumnsDict = new Dictionary<int, MPEClipColumn>();
            MPEClipColumns = new List<MPEClipColumn>();

            var lastClip = Editor.MPEClipsDB.GetClips().LastOrDefault();
            if (lastClip != null)
            {
                foreach (var lastCol in lastClip.MPEClipColumns)
                {
                    MPEClipColumn column = lastCol.Clone(this);

                    column.SetBeatCount(lastClip.clip.Length / lastClip.RowsPerBeat);
                    column.SetRPB(lastClip.RowsPerBeat);
                    column.UpdateLength();

                    MPEClipColumns.Add(column);
                    MPEClipColumnsDict[MPEClipColumns.IndexOf(column)] = column;
                }
            }
            else
            {
                //int index = 0;
                int uiIndex = 0;

                // To be compatible with PXP, ignore input parameters
                foreach (var parAndTrack in clip.Machine.AllParametersAndTracks().Where( pt => pt.Item1.Group.Type != ParameterGroupType.Input) )
                {
                    MPEClipColumn column = new MPEClipColumn(this);
                    IParameter par = parAndTrack.Item1;
                    int track = parAndTrack.Item2;

                    column.Parameter = par;
                    //column.IndexInGroup = par.IndexInGroup;
                    //column.MachineName = par.Group.Machine.Name;
                    column.Machine = par.Group.Machine;
                    column.Graphical = false;
                    column.GroupType = par.Group.Type;
                    column.ParamTrack = track;
                    //column.ParamIndex = index;

                    column.SetBeatCount(Editor.ShownBeatCount);
                    column.SetRPB(rpb);
                    column.UpdateLength();

                    MPEClipColumns.Add(column);
                    MPEClipColumnsDict[uiIndex] = column;
                    //index++;
                    uiIndex++;
                }
            }
        }

        public void AddTrack(int trackNumber)
        {
            // Make a cooy of MPE track 0
            var trackColumns = MPEClipColumns.FindAll(column => column.GroupType == ParameterGroupType.Track && column.ParamTrack == 0).ToList();

            foreach (var trackColumn in trackColumns)
            {
                IParameter par = trackColumn.Parameter;

                MPEClipColumn column = new MPEClipColumn(this);
                column.Parameter = par;
                //column.MachineName = par.Group.Machine.Name;
                column.Machine = par.Group.Machine;
                column.Graphical = false;
                column.GroupType = par.Group.Type;
                column.ParamTrack = trackNumber;

                column.SetBeatCount(Editor.ShownBeatCount);
                column.SetRPB(rpb);
                column.UpdateLength();

                int index = MPEClipColumns.IndexOf(trackColumn) + trackColumns.Count;

                MPEClipColumns.Insert(index, column);
            }
        }

        public void UpdateData()
        {
            // Don't mess with these when audio thread access them
            lock (Editor.syncLock)
            {
                if (this.RowsPerBeat == 0)
                    this.RowsPerBeat = 4;

                if (MPEClipColumns.Count == 0)
                {
                    CreateDefaultColumns();
                    return;
                }

                MPEClipColumnsDict = new Dictionary<int, MPEClipColumn>();
                List<MPEClipColumn> newColumnList = new List<MPEClipColumn>();

                // Remove deleted tracks
                foreach (var col in MPEClipColumns)
                {
                    if (col.GroupType == ParameterGroupType.Track && col.ParamTrack >= clip.Machine.TrackCount)
                        continue;
                    else
                    {   
                        if (col.BeatRowsList.Count == 0)
                        {
                            col.SetRPB(rpb);
                        }
                        if (col.GetEvents(0, int.MaxValue).Count() > 0)
                        {

                        }
                        newColumnList.Add(col);
                    }   
                }
                MPEClipColumns = newColumnList;

                // Add added tracks
                int numTracks = MPEClipColumns.Max(x => x.ParamTrack);
                numTracks++;
                while (numTracks < clip.Machine.TrackCount)
                {
                    AddTrack(numTracks);
                    numTracks++;
                }

                MPEClipColumns = MPEClipColumns.OrderBy(x => x.Machine.ParameterGroups.IndexOf(x.Parameter.Group))
                    .ThenBy(x => x.Machine == this.Clip.Machine ? 0 : 1)
                    .ThenBy(x => x.Machine.Name)
                    .ThenBy(x => x.ParamTrack)
                    .ThenBy(x => x.VisualIndexInGroup).ToList();

                int visualIndex = 0;
                foreach (var mpecolumn in MPEClipColumns)
                {
                    MPEClipColumnsDict[visualIndex] = mpecolumn;
                    visualIndex++;
                }

                // Beat Rows
                foreach (var col in MPEClipColumns)
                {
                    col.UpdateLength();
                }
            }
        }

        internal void SetBeatCount(int beats)
        {
            foreach (var col in MPEClipColumns)
            {
                col.SetBeatCount(beats);
            }
        }

        internal MPEClipColumn GetColumnOrCreate(IClipColumn clipColumn)
        {
            MPEClipColumn ret;
            ret = MPEClipColumns.FirstOrDefault(x => x.Parameter == clipColumn.Parameter && x.ParamTrack == clipColumn.Track && x.Machine == clipColumn.Machine);
            return ret;
        }

        public int GetParamIndex(IParameter parameter, int track)
        {
            int ret = 0;

            foreach (var column in MPEClipColumns)
            {
                if (column.Parameter == parameter && column.ParamTrack == track)
                    break;
                ret++;
            }
            return ret;
        }

        public MPEClip(ClipEditor editor, string name)
        {
            this.Editor = editor;
            this.ClipName = name;
        }

        internal void Quantize()
        {
            foreach (var c in MPEClipColumns)
            {
                Dictionary<int, ClipEvent> dict = new Dictionary<int, ClipEvent>();
                var e = c.GetEvents(0, clip.Length * ClipEvent.TimeBase).ToArray();
                // Clear
                c.SetEvents(e, false);

                for (int i = 0; i < e.Length; i++)
                {
                    e[i].Time = c.GetTimeQuantized(e[i].Time);
                    dict[e[i].Time] = e[i];
                }

                // Set
                c.SetEvents(dict.Values.ToArray(), true);
            }
        }

        internal class MPEParameterSet
        {
            internal IList<IParameter> parameters;
            internal int track;
            internal string name;
            internal ParameterGroupType groupType;
            internal IMachine machine;

            internal MPEParameterSet()
            {
                parameters = new List<IParameter>();
                track = -1;
            }
        }

        internal IEnumerable<MPEParameterSet> GetParameterSets()
        {
            IList<MPEParameterSet> parameterSets = new List<MPEParameterSet>();

            MPEParameterSet set = new MPEParameterSet();

            foreach (var column in MPEClipColumns.Where(c => c.GroupType != ParameterGroupType.Input))
            {
                if (set.groupType != column.GroupType || set.track != column.ParamTrack || set.machine != column.Machine)
                {
                    if (set.parameters.Count > 0)
                    {
                        parameterSets.Add(set);
                    }

                    set = new MPEParameterSet();
                    set.track = column.ParamTrack;
                    set.name = column.Machine.Name;
                    set.groupType = column.GroupType;
                    set.machine = column.Machine;
                    if (column.GroupType == ParameterGroupType.Global)
                        set.name += "\nGlobal";
                    else if (column.GroupType == ParameterGroupType.Track)
                        set.name += "\nTrack " + set.track;
                    set.parameters.Add(column.Parameter);
                }
                else
                {
                    set.parameters.Add(column.Parameter);
                }
            }

            if (set.parameters.Count > 0)
            {
                parameterSets.Add(set);
            }

            return parameterSets.ToArray();
        }

        internal MPEClipColumn GetColumn(IMachine machine, int indexInGroup, int track)
        {
            // Fork (neo-buzz audio hygiene): a loop; the lambda's closure was made on every call
            // (for each MIDI note lane on every tick, on the audio thread).
            for (int i = 0; i < MPEClipColumns.Count; i++)
            {
                var x = MPEClipColumns[i];
                if (x.Machine == machine && x.Parameter.Group.Type == ParameterGroupType.Track && x.Parameter.IndexInGroup == indexInGroup && x.ParamTrack == track)
                    return x;
            }
            return null;
        }

        internal void InsertColumn(int index, IParameter p, int track)
        {
            ClipColumnType type = p.IndexInGroup == (int)InternalParameter.MidiNote || p.IndexInGroup == -1 ? ClipColumnType.MIDI : ClipColumnType.Parameter;
            ReBuzzClipColumn pcc = new ReBuzzClipColumn(this, type, clip, p.Group.Machine, p, track, null, null);
            Columns.Insert(index, pcc);
            //ColumnAdded?.Invoke(pcc);
        }
    }
}
