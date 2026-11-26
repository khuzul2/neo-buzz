using BuzzGUI.Common;
using BuzzGUI.Common.Actions.ClipActions;
using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using WDE.ModernPatternEditor.MPEStructures;
using static WDE.ModernPatternEditor.MPEStructures.MPEClip;

namespace WDE.ModernPatternEditor
{
    public class ClipVM : INotifyPropertyChanged
    {
        IClip clip;
        public IClip Clip
        {
            get { return clip; }
            set
            {
                if (clip != null)
                {
                    clip.PropertyChanged -= clip_PropertyChanged;
                    clip.Machine.PropertyChanged -= Machine_PropertyChanged;

                    ReleaseColumnSets();
                    ReleaseNoteLanes();
                }

                clip = value;

                if (clip != null)
                {
                    clip.PropertyChanged += clip_PropertyChanged;
                    clip.Machine.PropertyChanged += Machine_PropertyChanged;

                    DefaultRPB = Editor.MPEClipsDB.GetMPEClip(clip).RowsPerBeat;
                    rowNumberColumnSet = new RowNumberColumnSet(DefaultRPB);
                    RowNumberColumnSet.BeatCount = BeatCount;
                    ClipData pd = new ClipData();
                    AttachNoteLanes();
                    CreateColumnSets();
                }

                PropertyChanged.Raise(this, "Clip");
            }
        }

        void Machine_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "TrackCount":
                    CreateColumnSets();
                    break;

                case "Name":
                    CreateColumnSets();
                    break;
            }
        }

        void clip_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "Length":
                    RowNumberColumnSet.BeatCount = BeatCount;
                    foreach (var cs in columnSets) cs.BeatCount = BeatCount;
                    CursorPosition = CursorPosition.Constrained;
                    Selection = Selection.Constrained;
                    Editor.MPEClipsDB.GetMPEClip(Clip).SetBeatCount(BeatCount);
                    PropertyChanged.Raise(this, "BeatCount");
                    PropertyChanged.Raise(this, "UndoableBeatCount");
                    break;

                case "Name":
                    PropertyChanged.Raise(this, e.PropertyName);
                    break;

            }


        }

        public ClipEditor Editor { get; }
        public MachineVM MachineVM { get; private set; }

        public ClipVM(MachineVM mvm, ClipEditor pe)
        {
            DefaultRPB = 4;

            this.Editor = pe;
            this.MachineVM = mvm;
            cursorPosition = new Digit(this);
            selection = new Selection(this);
        }

        void CreateColumnSets()
        {
            ReleaseColumnSets();
            columnSets = new List<ParameterColumnSet>();

            // Don't show anythign if machine has 0 parameters
            //if (clip.Machine.ParameterGroups[1].Parameters.Count + clip.Machine.ParameterGroups[2].Parameters.Count == 0)
            //    return;

            var parameterSets = Editor.MPEClipsDB.GetMPEClip(clip).GetParameterSets();
            // Fork (ux-overhaul.md P3c): the sets with notes first, the empty automation columns hidden.
            BeginPlan();
            parameterSets = ColumnPlan.OrderSets(parameterSets);
            AddNoteLaneSets();
            foreach (var pSet in parameterSets)
            {
                CreateColumnSet(pSet);
            }
            EndPlan();

            PropertyChanged.Raise(this, "ColumnSets");

            if (parameterSets.Count() > 0)
            {
                CursorPosition = CursorPosition.Constrained;
                Selection = Selection.Constrained;
            }
        }

        void CreateColumnSet(MPEParameterSet pSet)
        {
            var parameters = pSet.parameters;
            if (parameters.Count == 0) return;

            var mpeClip = Editor.MPEClipsDB.GetMPEClip(clip);
            parameters = PlanSet(pSet, mpeClip);
            if (parameters.Count == 0) return;

            var cl = new List<IClipColumn>();

            foreach (var p in parameters)
            {
                var ic = mpeClip.Columns.FirstOrDefault(c => c.Parameter == p && c.Track == pSet.track && c.Machine == p.Group.Machine);
                if (ic == null)
                {
                    if (p is MPEInternalParameter)
                    {
                        ic = new MPEClipColumnBuzz(ClipColumnType.MIDI, Clip, ((MPEInternalParameter)p).Machine, p, pSet.track);
                    }
                    else
                    {
                        mpeClip.InsertColumn(mpeClip.Columns.Count, p, pSet.track);
                        ic = mpeClip.Columns.Last();
                    }
                }

                cl.Add(ic);
            }

            columnSets.Add(new ParameterColumnSet(this, cl, pSet.track) { BeatCount = this.BeatCount, Label = pSet.name });
        }

        void CreateColumnSet(IEnumerable<IParameter> parameters, int track, string label)
        {
            if (parameters.Count() == 0) return;

            var cl = new List<IClipColumn>();

            foreach (var p in parameters)
            {
                var ic = clip.Columns.FirstOrDefault(c => c.Parameter == p && c.Track == track && c.Machine == p.Group.Machine);
                if (ic == null)
                {
                    if (p is MPEInternalParameter)
                    {
                        ic = new MPEClipColumnBuzz(ClipColumnType.MIDI, Clip, ((MPEInternalParameter)p).Machine, p, track);
                    }
                    else
                    {
                        clip.InsertColumn(clip.Columns.Count, p, track);
                        ic = clip.Columns.Last();
                    }
                }

                cl.Add(ic);
            }

            columnSets.Add(new ParameterColumnSet(this, cl, track) { BeatCount = this.BeatCount, Label = label });
        }

        void ReleaseColumnSets()
        {
            ReleaseHidden();
            if (columnSets == null) return;
            foreach (var cs in columnSets) cs.Release();
            columnSets = null;
        }

        // ------------------------------------------------------------------ Fork (ux-overhaul.md P3c): the columns shown

        /// <summary>Parameters the user asked for with + Automation (kept while the editor lives); a column that is shown for its data joins them.</summary>
        internal readonly HashSet<IParameter> RevealedParameters = new HashSet<IParameter>();

        HashSet<IParameter> withData = new HashSet<IParameter>();
        readonly List<HiddenColumn> hiddenColumns = new List<HiddenColumn>();
        readonly HashSet<IParameter> hiddenSeen = new HashSet<IParameter>();
        readonly List<(MPEClipColumn Column, Action<IEnumerable<ClipEvent>, bool> Handler)> hiddenWatches = new List<(MPEClipColumn, Action<IEnumerable<ClipEvent>, bool>)>();
        bool rebuildPending;

        /// <summary>The parameters whose columns are hidden because they hold nothing (what + Automation offers).</summary>
        public IReadOnlyList<HiddenColumn> HiddenColumns => hiddenColumns;

        void BeginPlan()
        {
            hiddenColumns.Clear();
            hiddenSeen.Clear();
            withData = new HashSet<IParameter>();
            if (clip == null) return;
            foreach (var c in Editor.MPEClipsDB.GetMPEClip(clip).MPEClipColumns)
                if (c.Parameter != null && (c.HasEvents || c.LaneOpen)) withData.Add(c.Parameter);        // T2: a column whose lane is open stays shown, empty or not
        }

        /// <summary>A set's parameters, in the order shown, without the empty automation the user hasn't asked for.</summary>
        IList<IParameter> PlanSet(MPEParameterSet pSet, MPEClip mpeClip)
        {
            var shown = new List<IParameter>();
            foreach (var (p, always) in ColumnPlan.Arrange(pSet.parameters))
            {
                // Fork (clips.md §10.0b, CU4): beside a Notes lane the editor's own MIDI Note and Velocity columns hold nothing (the notes are
                // the lane's), so they wait behind + Automation until something is written into them.
                bool besideNotes = noteSetCount > 0 && p is MPEInternalParameter;
                if ((always && !besideNotes) || RevealedParameters.Contains(p)) { shown.Add(p); continue; }
                if (withData.Contains(p))
                {
                    RevealedParameters.Add(p);
                    shown.Add(p);
                    continue;
                }
                if (hiddenSeen.Add(p))
                    hiddenColumns.Add(new HiddenColumn { Parameter = p, Name = p.Name, Group = pSet.groupType == ParameterGroupType.Global ? "Global" : "Track" });
                foreach (var c in mpeClip.MPEClipColumns.Where(c => c.Parameter == p && c.ParamTrack == pSet.track && c.Machine == pSet.machine))
                {
                    Action<IEnumerable<ClipEvent>, bool> h = OnHiddenEvents;
                    c.EventsChanged += h;
                    hiddenWatches.Add((c, h));
                }
            }
            return shown;
        }

        void EndPlan() { }

        void ReleaseHidden()
        {
            foreach (var (c, h) in hiddenWatches) c.EventsChanged -= h;
            hiddenWatches.Clear();
        }

        /// <summary>Something wrote into a hidden column (the agent, a paste, an undo): it has data now, so it shows.</summary>
        void OnHiddenEvents(IEnumerable<ClipEvent> events, bool setOrClear)
        {
            if (!setOrClear || rebuildPending || events == null || !events.Any()) return;
            rebuildPending = true;
            Editor.Dispatcher.BeginInvoke(() =>
            {
                rebuildPending = false;
                if (clip != null) RebuildColumns();
            });
        }

        /// <summary>Shows the columns of these parameters (+ Automation); the cursor stays on its column.</summary>
        public void RevealColumns(IEnumerable<IParameter> parameters)
        {
            bool any = false;
            foreach (var p in parameters) any |= RevealedParameters.Add(p);
            if (any && clip != null) RebuildColumns();
        }

        /// <summary>The columns made again (more or fewer shown), the cursor kept on its column when it is still there.</summary>
        internal void RebuildColumns()
        {
            IParameter parameter = null;
            int track = 0;
            bool onLane = false;
            var at = columnSets != null && cursorPosition.ColumnSet < columnSets.Count ? columnSets[cursorPosition.ColumnSet] : null;
            if (at != null && cursorPosition.Column < at.Columns.Count && at.Columns[cursorPosition.Column] is ParameterColumn pc)
            {
                parameter = pc.ClipColumn.Parameter;
                track = pc.ClipColumn.Track;
                onLane = pc is NoteLaneColumn;      // the Notes lanes share the editor's MIDI parameters and tracks
            }
            var old = cursorPosition;
            CreateColumnSets();
            if (parameter == null || columnSets == null) return;
            for (int s = 0; s < columnSets.Count; s++)
                for (int c = 0; c < columnSets[s].Columns.Count; c++)
                    if (columnSets[s].Columns[c] is ParameterColumn q && q.ClipColumn.Parameter == parameter && q.ClipColumn.Track == track && (q is NoteLaneColumn) == onLane)
                    {
                        CursorPosition = new Digit(this, s, c, old.Beat, old.RowInBeat, old.Index).Constrained;
                        return;
                    }
        }

        // ------------------------------------------------------------------ Fork (daw-modes.md AR3): the Notes lanes

        NoteLaneHost noteLanes;
        int noteSetCount;

        void AttachNoteLanes()
        {
            noteLanes = new NoteLaneHost(this, clip);
            noteLanes.Changed += OnNoteLanesChanged;
        }

        void ReleaseNoteLanes()
        {
            if (noteLanes == null) return;
            noteLanes.Changed -= OnNoteLanesChanged;
            noteLanes.Dispose();
            noteLanes = null;
            noteSetCount = 0;
        }

        /// <summary>The voices of the Notes lanes go first: the clip's notes are what the song plays.</summary>
        void AddNoteLaneSets()
        {
            noteSetCount = 0;
            if (noteLanes == null) return;
            int voices = noteLanes.ShownVoices;
            for (int v = 0; v < voices; v++)
                columnSets.Add(new NoteLaneColumnSet(this, noteLanes, v) { BeatCount = this.BeatCount });
            noteSetCount = voices;
        }

        /// <summary>The column of the MIDI column's notes is made again when the voices shown change, else drawn again.</summary>
        void OnNoteLanesChanged()
        {
            if (clip == null || columnSets == null || noteLanes == null) return;
            if (noteLanes.ShownVoices != noteSetCount)
                RebuildColumns();
            else
                foreach (var cs in columnSets.OfType<NoteLaneColumnSet>()) cs.InvalidateAll();
        }

        /// <summary>Whether the Notes lanes are shown.</summary>
        public bool HasNoteLanes => noteSetCount > 0;

        /// <summary>Shows the Notes lanes though the MIDI column holds no notes yet (the toolbar's Notes), or leaves it to the notes.</summary>
        public bool ShowNoteLanes
        {
            get => noteLanes?.Forced ?? false;
            set
            {
                if (noteLanes == null || noteLanes.Forced == value) return;
                noteLanes.Forced = value;
                OnNoteLanesChanged();
                PropertyChanged.Raise(this, "ShowNoteLanes");
            }
        }

        /// <summary>Whether the clip's MIDI column holds notes (so the lanes show by themselves).</summary>
        public bool HasMidiNotes => noteLanes != null && noteLanes.Lanes.NoteCount > 0;

        public int BeatCount
        {
            get { return (clip.Length + ClipControl.BUZZ_TICKS_PER_BEAT - 1) / ClipControl.BUZZ_TICKS_PER_BEAT; }
            internal set
            {
                if (clip.Length != value * ClipControl.BUZZ_TICKS_PER_BEAT)
                {
                    clip.Length = value * ClipControl.BUZZ_TICKS_PER_BEAT;
                    PropertyChanged.Raise(this, "BeatCount");
                }
            }
        }

        public int UndoableBeatCount
        {
            get { return BeatCount; }
            set
            {
                if (value != BeatCount)
                {
                    using (new ActionGroup(Editor.EditContext.ActionStack))
                    {
                        MachineVM.Editor.DoAction(new MPESetClipLengthAction(Editor.MPEClipsDB.GetMPEClip(Clip), BeatCount, value));
                        // Editor.MPEClipsDB.GetMPEClip(Clip).SetBeatCount(value); // Undoable set length?
                        //MachineVM.Editor.DoAction(new SetLengthAction(clip, value * Global.Buzz.TPB));

                    }
                    PropertyChanged.Raise(this, "UndoableBeatCount");
                }
            }
        }

        public string Name { get { return Clip.Name; } }

        RowNumberColumnSet rowNumberColumnSet;// = new RowNumberColumnSet();
        public RowNumberColumnSet RowNumberColumnSet { get { return rowNumberColumnSet; } }

        List<ParameterColumnSet> columnSets;
        public IList<ParameterColumnSet> ColumnSets { get { return columnSets; } }

        public ColumnRenderer.IColumnSet GetColumnSet(Digit p) { return ColumnSets[p.ColumnSet]; }
        public ColumnRenderer.IColumn GetColumn(Digit p) { return ColumnSets[p.ColumnSet].Columns[p.Column]; }
        public ColumnRenderer.IBeat GetBeat(Digit p) { return GetColumn(p).FetchBeat(p.Beat); }
        public ColumnRenderer.BeatValueType GetValueType(Digit p) { return GetBeat(p).Rows[p.RowInBeat].Type; }
        public int GetValue(Digit p) { return GetBeat(p).Rows[p.RowInBeat].Value; }
        public string GetValueString(Digit p) { return GetBeat(p).Rows[p.RowInBeat].ValueString; }

        public Digit GetColumnDigit(ColumnRenderer.IColumn c)
        {
            var d = new Digit(this, columnSets.FindIndex(cs => cs.Columns.Contains(c)), 0, 0, 0, 0);
            d = d.SetColumn(columnSets[d.ColumnSet].Columns.IndexOf(c));
            return d;
        }

        public Digit GetColumnSetDigit(ColumnRenderer.IColumnSet cs)
        {
            return new Digit(this, columnSets.IndexOf((ParameterColumnSet)cs), 0, 0, 0, 0);
        }


        Digit cursorPosition;
        public Digit CursorPosition
        {
            get { return cursorPosition; }
            set
            {
                //if (value != cursorPosition)
                {
                    cursorPosition = value;
                    PropertyChanged.Raise(this, "CursorPosition");
                }
            }
        }

        Selection selection;
        public Selection Selection
        {
            get { return selection; }
            set
            {
                //if (value != cursorPosition)
                {
                    selection = value;
                    PropertyChanged.Raise(this, "Selection");
                }
            }
        }

        public double ScrollPosition = 0;

        public ClipEditorSettings BindableSettings { get { return ClipEditor.Settings; } }

        public int DefaultRPB { get; internal set; }

        public event PropertyChangedEventHandler PropertyChanged;


    }
}
