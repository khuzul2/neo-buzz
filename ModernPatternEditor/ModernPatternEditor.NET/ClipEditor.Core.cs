// Fork (docs/specs/studio-mvp.md S6): the Modern Pattern Editor's core, shared by the WPF editor
// and its portable builds (ModernPatternEditor/Portable, ModernPatternEditor/Avalonia). Moved as it
// was from PatternEditor.xaml.cs; each build adds its own part (the WPF control, or the portable one).
using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using WDE.ModernPatternEditor.Actions;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor
{
    public partial class ClipEditor : INotifyPropertyChanged
    {
        // The renderer's caches start over with each song (the WPF part clears them).
        partial void SongChanging();

        internal readonly string Version = "0.9.4";
        ISong song;
        internal MPEClipDatabase MPEClipsDB;
        internal static ClipClipboard clipboard = new ClipClipboard();
        internal PlayRecordManager playRecordManager;
        public bool MidiEdit { get; set; }

        public bool MidiVelocity { get; set; }

        int selectedStepsDown = 1;
        public int SelectedStepsDown { get => selectedStepsDown; set { selectedStepsDown = value; PropertyChanged.Raise(this, "SelectedStepsDown"); } }

        int selectedStepsRight = 0;
        public int SelectedStepsRight { get => selectedStepsRight; set { selectedStepsRight = value; PropertyChanged.Raise(this, "SelectedStepsRight"); } }

        public ISong Song
        {
            get { return song; }
            set
            {
                if (song != null)
                {
                    song.PropertyChanged -= song_PropertyChanged;
                    song.MachineAdded -= song_MachineAdded;
                    song.MachineRemoved -= song_MachineRemoved;
                    song.Buzz.PropertyChanged -= Buzz_PropertyChanged;
                    song.Wavetable.WaveChanged -= Wavetable_WaveChanged;

                    song.SequenceAdded -= Song_SequenceAdded;
                    song.SequenceRemoved -= Song_SequenceRemoved;
                    song.SequenceChanged -= Song_SequenceChanged;

                    foreach (var m in song.Machines)
                        song_MachineRemoved(m);

                    //machines.Clear();
                }

                SongChanging();

                song = value;

                if (song != null)
                {
                    song.PropertyChanged += song_PropertyChanged;
                    song.MachineAdded += song_MachineAdded;
                    song.MachineRemoved += song_MachineRemoved;
                    song.Buzz.PropertyChanged += Buzz_PropertyChanged;
                    song.Wavetable.WaveChanged += Wavetable_WaveChanged;

                    song.SequenceAdded += Song_SequenceAdded;
                    song.SequenceRemoved += Song_SequenceRemoved;
                    song.SequenceChanged += Song_SequenceChanged;

                    foreach (var m in song.Machines)
                    	song_MachineAdded(m);

                    for (int i = 0; i < song.Wavetable.Waves.Count; i++)
                        Wavetable_WaveChanged(i);
                }
            }
        }

        private void song_MachineRemoved(IMachine obj)
        {
            //machines.Remove(obj);
            PropertyChanged.Raise(this, "Machines");
            PropertyChanged.Raise(this, "ChangeMachine");
        }

        private void song_MachineAdded(IMachine obj)
        {
            //machines.Add(obj);
            PropertyChanged.Raise(this, "Machines");
            PropertyChanged.Raise(this, "ChangeMachine");
        }

        private void Song_SequenceChanged(int obj, ISequence seq)
        {
            playRecordManager.RefreshPlayPosData();
        }

        private void Song_SequenceRemoved(int obj, ISequence seq)
        {
            playRecordManager.RefreshPlayPosData();
        }

        private void Song_SequenceAdded(int obj, ISequence seq)
        {
            playRecordManager.RefreshPlayPosData();
        }
        void Wavetable_WaveChanged(int index)
        {
            var oldvm = waves.FirstOrDefault(w => w.Index == index);
            if (oldvm != null)
            {
                if (oldvm == SelectedWave)
                    SelectedWave = null;

                waves.Remove(oldvm);
            }

            if (song.Wavetable.Waves[index] != null)
            {
                var newvm = new WaveVM() { Wavetable = song.Wavetable, Index = index };
                var inspos = waves.FindIndex(w => w.Index > index);
                if (inspos < 0) inspos = waves.Count;
                waves.Insert(inspos, newvm);
                SelectedWave = newvm;
            }
            else
            {
                if (SelectedWave == null && waves.Count > 0)
                    SelectedWave = waves[0];
            }
        }
        void Buzz_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "ActiveView":
                    if (Global.Buzz.ActiveView != BuzzView.ClipView)
                        ClearEditContext();
                    break;
                case "Playing":
                    {
                        if (Global.Buzz.Recording)
                        {
                            // Init previous position etc
                            this.playRecordManager.Init();
                        }
                        if (!Global.Buzz.Playing)
                        {
                            playRecordManager.Stop();
                        }
                    }
                    break;
            }
        }

        void song_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
        }
        MachineVM selectedMachine;
        public MachineVM SelectedMachine
        {
            get { return selectedMachine; }
            set
            {
                selectedMachine = value;
                PropertyChanged.Raise(this, "SelectedMachine");
                playRecordManager.Init();

                if (selectedMachine != null)
                {
                    var m = selectedMachine.Machine;
                    if (m.DLL.Info.Type == MachineType.Generator || m.IsControlMachine)
                        m.Graph.Buzz.MIDIFocusMachine = m;

                    //changeMachine = selectedMachine.Machine;
                    //PropertyChanged.Raise(this, "ChangeMachine");
                }
            }
        }

        ObservableCollection<WaveVM> waves = new ObservableCollection<WaveVM>();
        public ObservableCollection<WaveVM> Waves { get { return waves; } }

        WaveVM selectedWave;
        public WaveVM SelectedWave
        {
            get { return selectedWave; }
            set
            {
                if (value != selectedWave)
                {
                    selectedWave = value;
                    PropertyChanged.Raise(this, "SelectedWave");
                    PropertyChanged.Raise(this, "SelectedWaveIndex");
                }
            }
        }

        public int SelectedWaveIndex
        {
            get
            {
                if (SelectedWave == null)
                    return -1;
                else
                    return Waves.IndexOf(SelectedWave);
            }

            set
            {
                if (waves.Count > 0)
                {
                    int newi = Math.Min(Math.Max(value, 0), Waves.Count - 1);
                    SelectedWave = Waves[newi];
                }
                else
                {
                    SelectedWave = null;
                }
            }
        }

        bool playNotes = true;
        public bool PlayNotes
        {
            get { return playNotes; }
            set
            {
                playNotes = value;
                PropertyChanged.Raise(this, "PlayNotes");
            }
        }
        bool isKeyboardMappingVisible = false;
        public bool IsKeyboardMappingVisible
        {
            get { return isKeyboardMappingVisible; }
            set
            {
                isKeyboardMappingVisible = value;
                PropertyChanged.Raise(this, "IsKeyboardMappingVisible");
                PropertyChanged.Raise(this, "KeyboardMappingVisibility");
            }
        }

        public IEnumerable<KeyboardMapping> KeyboardMappings { get; private set; }

        KeyboardMapping selectedKeyboardMapping;
        public KeyboardMapping SelectedKeyboardMapping
        {
            get { return selectedKeyboardMapping; }
            set
            {
                selectedKeyboardMapping = value;
                PropertyChanged.Raise(this, "SelectedKeyboardMapping");
            }
        }

        static readonly string[] rootNotes = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        public IEnumerable<string> RootNotes { get { return rootNotes; } }

        int selectedRootNote = 0;
        public int SelectedRootNote
        {
            get { return selectedRootNote; }
            set
            {
                selectedRootNote = value;
                PropertyChanged.Raise(this, "SelectedRootNote");
            }
        }
        public bool isSelectClipEditorVisible;
        public bool IsSelectClipEditorVisible
        {
            get { return isSelectClipEditorVisible; }
            set
            {
                isSelectClipEditorVisible = value;
                PropertyChanged.Raise(this, "IsSelectPatternEditorVisible");
                PropertyChanged.Raise(this, "SelectPatternEditorVisibility");
            }
        }
        public bool isImportExportClipVisible;
        public bool IsImportExportClipVisible
        {
            get { return isImportExportClipVisible; }
            set
            {
                isImportExportClipVisible = value;
                PropertyChanged.Raise(this, "IsImportExportPatternVisible");
                PropertyChanged.Raise(this, "SelectImportExportPatternVisibility");
            }
        }
        string statusBarItem1;
        public string StatusBarItem1
        {
            get { return statusBarItem1; }
            set
            {
                if (value != statusBarItem1)
                {
                    statusBarItem1 = value;
                    PropertyChanged.Raise(this, "StatusBarItem1");
                }

            }
        }

        string statusBarItem2;
        public string StatusBarItem2
        {
            get { return statusBarItem2; }
            set
            {
                if (value != statusBarItem2)
                {
                    statusBarItem2 = value;
                    PropertyChanged.Raise(this, "StatusBarItem2");
                }

            }
        }

        string statusBarItem3;
        public string StatusBarItem3
        {
            get { return statusBarItem3; }
            set
            {
                if (value != statusBarItem3)
                {
                    statusBarItem3 = value;
                    PropertyChanged.Raise(this, "StatusBarItem3");
                }

            }
        }
        public static ClipEditorSettings Settings = new ClipEditorSettings();
        public readonly IGUICallbacks cb;

        public EditContext EditContext;
        public ICommand CreateClipCommand { get; private set; }
        public ICommand CloneClipCommand { get; private set; }
        public ICommand DeleteClipCommand { get; private set; }
        public void SelectClip(IClip p)
        {
            SelectMachine(p.Machine);

            SelectedMachine.SelectedClip = SelectedMachine.clips.FirstOrDefault(pvm => pvm.Clip == p);

            //var sel = patternBox.Items.Cast<PatternVM>().Where(vm => vm.Pattern == p).FirstOrDefault();
            //if (sel != null) patternBox.SelectedItem = sel;
        }

        public void SelectMachine(IMachine m)
        {
        }

        public void ClearEditContext()
        {
            if (Global.Buzz.EditContext == EditContext)
            {
                Global.Buzz.EditContext = null;
            }
        }

        internal void DoAction(BuzzAction a)
        {
            EditContext.ManagedActionStack.Do(a);
        }
        IMachine targetMachine;
        public IMachine TargetMachine
        {
            get { return targetMachine; }
            set
            {
                if (targetMachine != null)
                {
                }

                targetMachine = value;
            }
        }
        
        public void InitMachine()
        {
        }

        // This is called always when pattern is added
        public void SetTargetMachine(IMachine machine)
        {
            lock (syncLock)
            {
                if (TargetMachine == null)
                {
                    TargetMachine = machine;

                    MPEClipsDB.Machine = TargetMachine;

                    if (clipEditorData != null)
                    {   
                        List<MPEClip> clips = ClipEditorUtils.ProcessEditorData(this, clipEditorData);
                        if (clips.Count != 0)
                        {
                            MPEClipsDB.SetClips(clips);
                        }
                    }

                    if (StoreAttached) ApplyStoreView();

                    if (MPEClipsDB.GetClips().Count() == 0 && TargetMachine.Clips.Count() > 0)
                    {
                        // Code never goes here?
                        MPEClipsDB.AddClip(TargetMachine.Clips[0]);
                    }

                    if (SelectedMachine == null)
                        SelectedMachine = new MachineVM(this) { Machine = TargetMachine };
                    else
                        SelectedMachine.Machine = machine;
                }
            }
        }

        internal void TargetMachine_ClipAdded(IClip clip)
        {
            //lock (syncLock)
            {
                MPEClipsDB.AddClip(clip);
            }
        }

        internal void TargetMachine_ClipRemoved(IClip clip)
        {
            //lock (syncLock)
            {
                MPEClipsDB.RemoveClip(clip);
            }
        }
        // ---- Fork (docs/specs/clips.md §6, CU3): the tracker views the store. After the song's tracker data is migrated into the pattern's
        // columns, the editor lets go of what it held and its columns read and write the lanes. The engine plays every lane, so the editor's
        // own player is not built for the Studio, and its note columns are the engine's Notes lane (shown by the note host).
        public bool StoreAttached { get; private set; }

        public void AttachClipStore()
        {
            lock (syncLock)
            {
                StoreAttached = true;
                ApplyStoreView();
            }
        }

        void ApplyStoreView()
        {
            foreach (var p in MPEClipsDB.GetClips())
            {
                p.MPEClipColumns.RemoveAll(c => c.Parameter is MPEInternalParameter);
                foreach (var c in p.MPEClipColumns)
                {
                    c.LetGoOfOwnEvents();
                    c.TakeViewFromStore();
                }
                p.MPEClipColumnsDict.Clear();
                for (int i = 0; i < p.MPEClipColumns.Count; i++) p.MPEClipColumnsDict[i] = p.MPEClipColumns[i];
            }
        }

        byte[] clipEditorData;
        // From native
        public void SetClipEditorData(byte[] data)
        {
            // Process later when all machines are loaded
            clipEditorData = data;
            /*
            List<MPEPattern> patterns = PatternEditorUtils.ProcessEditorData(this, data);
            if (patterns.Count != 0)
            {
                MPEPatternsDB.SetPatterns(patterns);
            }
            */
        }

        // From native
        public byte[] GetClipEditorData()
        {
            if (Settings.PXPDataFormat)
                return ClipEditorUtils.CreatePatternXPClipData(MPEClipsDB.GetClips());
            else
                return ClipEditorUtils.CreateModernPatternEditorData(MPEClipsDB.GetClips());

        }

        // From native
        public void CreateClip(string name, int numrows)
        {
        }


        public void SetEditorClip(IClip clip)
        {
            //lock (syncLock)
            {
                if (TargetMachine != null && SelectedMachine != null)
                {
                    if (clip != null && SelectedMachine.SelectedClip.Clip != clip)
                        SelectClip(clip);
                }
            }
        }

        // From native
        public void LostMidiFocus()
        {
            playRecordManager.StopNonEditorMidiNotes();
        }

        // From native
        public void AddTrack()
        {
            // This is also called when Ctrl+ is pressed. Commented out to avoid other issues.
            //Dispatcher.BeginInvoke(new Action(() =>
            //{
            //    SelectedMachine.UndoableTrackCount++;
            //}));
        }

        // From native
        public void DeleteLastTrack()
        {
            // This is also called when Ctrl- is pressed. Commented out to avoid other issues.
            //Dispatcher.BeginInvoke(new Action(() =>
            //{
            //    SelectedMachine.UndoableTrackCount--;
            //}));
        }

        // From native
        public void CreateClipCopy(string newName, string oldName)
        {
            MPEClipsDB.PrepareCopy(newName, oldName);
        }
        // From native
        public void RecordControlChange(IParameter param, int track, int value)
        {
            playRecordManager.RecordControlChange(param, track, value);
        }

        // From native
        public void MidiNote(int channel, int value, int velocity)
        {
            lock (syncLock)
            {
                if (Global.Buzz.Recording)
                {
                    // ToDo: Only record to MIDI columns.

                    //playRecordManager.RecordPlayingMidiNote(channel, value, velocity);
                }
                else if (MidiEdit && clipControl.Clip != null)
                {
                    var cursor = clipControl.Clip.CursorPosition;
                    if (cursor.ParameterColumn.Type == ColumnRenderer.ColumnType.Note && velocity != 0)
                    {
                        int buzzNote = velocity == 0 ? BuzzNote.Off : BuzzNote.FromMIDINote(value);
                        int time = cursor.TimeInBeat + cursor.Beat * ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase;
                        MPEClip mpeClip = MPEClipsDB.GetMPEClip(clipControl.Clip.Clip);
                        MPEClipColumn mpeColumn = mpeClip.GetColumn(cursor.ParameterColumn.ClipColumn);
                        MPEClipColumn midiVelocityColumn = MidiVelocity ? GetNoteVelocityColumn(mpeClip, cursor.ParameterColumn.ClipColumn.Track) : null;

                        Dispatcher.Invoke(() =>
                        {
                            if (clipControl.IsKeyboardFocused)
                            {
                                using (new ActionGroup(EditContext.ActionStack))
                                {
                                    DoAction(
                                        new MPESetOrClearEventsAction(
                                            clipControl.Clip.Clip,
                                            mpeColumn,
                                            new ClipEvent[] { new ClipEvent() { Time = time, Value = buzzNote } },
                                            true));

                                    if (midiVelocityColumn != null)
                                    {
                                        DoAction(
                                            new MPESetOrClearEventsAction(
                                                clipControl.Clip.Clip,
                                                midiVelocityColumn,
                                                new ClipEvent[]
                                                {
                                                    new ClipEvent() { Time = time, Value = velocity }
                                                },
                                                true));
                                    }
                                }

                                clipControl.MoveCursorDelta(SelectedStepsRight, SelectedStepsDown);
                                playRecordManager.UpdatePlayingNoteClip(
                                    mpeColumn.Parameter,
                                    mpeColumn.ParamTrack,
                                    buzzNote,
                                    velocity);
                            }
                        });
                    }
                }
                else
                {
                    playRecordManager.UpdatePlayingMidiNote(channel, value, velocity);
                }
                TargetMachine?.SendMIDINote(channel, value, velocity);
            }
        }

        // From native
        public void MidiControlChange(int ctrl, int channel, int value)
        {
            TargetMachine?.SendMIDIControlChange(ctrl, channel, value);
        }

        // From native
        public int[] ExportMidiEvents(IClip pat)
        {
            //lock (syncLock)
            {
                if (SelectedMachine != null)
                {
                    //IPattern pat = SelectedMachine.Machine.Patterns.FirstOrDefault(x => x.Name == pattern);
                    if (pat != null)
                    {
                        MPEClip mpeClip = MPEClipsDB.GetMPEClip(pat);
                        return ClipEditorUtils.ExportMidiEvents(mpeClip);
                    }
                }
                return new int[0];
            }
        }

        public bool ImportMidiEvents(IClip pat, int [] data)
        {
            lock (syncLock)
            {
                if (pat != null)
                {
                    MPEClip mpeClip = MPEClipsDB.GetMPEClip(pat);
                    return ClipEditorUtils.ImportMidiEvents(mpeClip, data);
                }
            }

            return false;
        }

        public bool CanExecuteCommand(BuzzCommand cmd)
        {
            if (EditContext == null)
                return false;

            if (clipControl.Clip.ColumnSets.Count == 0)
                return false;

            if (cmd == BuzzCommand.Undo)
            {
                return EditContext.ActionStack.CanUndo;
            }
            else if (cmd == BuzzCommand.Redo)
            {
                return EditContext.ActionStack.CanRedo;
            }
            else if (cmd == BuzzCommand.Copy)
            {
                return clipControl.CopyCommand.CanExecute(clipControl);
            }
            else if (cmd == BuzzCommand.Cut)
            {
                return clipControl.CutCommand.CanExecute(clipControl);
            }
            else if (cmd == BuzzCommand.Paste)
            {
                return clipControl.PasteCommand.CanExecute(clipControl);
            }
            return false;
        }

        public void ExecuteCommand(BuzzCommand cmd)
        {
            if (EditContext == null)
                return;

            if (cmd == BuzzCommand.Undo)
            {
                 EditContext.ActionStack.Undo();
            }
            else if (cmd == BuzzCommand.Redo)
            {
                EditContext.ActionStack.Redo();
            }
            else if (cmd == BuzzCommand.Copy)
            {
                clipControl.CopyCommand.Execute(clipControl);
            }
            else if (cmd == BuzzCommand.Cut)
            {
                clipControl.CutCommand.Execute(clipControl);
            }
            else if (cmd == BuzzCommand.Paste)
            {
                clipControl.PasteCommand.Execute(clipControl);
            }
        }

        // From native
        public bool CanUndo()
        {
            if (EditContext != null)
                return EditContext.ActionStack.CanUndo;
            else
                return false;
        }

        // From native
        public bool CanRedo()
        {
            if (EditContext != null)
                return EditContext.ActionStack.CanRedo;
            else
                return false;
        }

        // From native
        public void Undo()
        {
            if (EditContext != null)
            {
                EditContext.ActionStack.Undo();
            }
        }

        // From native
        public void Redo()
        {
            if (EditContext != null)
            {
                EditContext.ActionStack.Redo();
            }
        }

        // From native
        public void DoCopy()
        {
            clipControl.CopyCommand.Execute(clipControl);
        }

        // From native
        public void DoCut()
        {
            clipControl.CutCommand.Execute(clipControl);
        }

        // From native
        public void DoPaste()
        {
            clipControl.PasteCommand.Execute(clipControl);
        }

        public Lock syncLock = new();
        // Audio thread.

        public void Work(SongTime songTime)
        {
            lock (syncLock)
            {
                if (/*songTime.PosInTick != 0 &&*/ songTime.PosInSubTick == 0)
                {
                    if (Global.Buzz.Playing && TargetMachine != null)
                    {
                    }
                }

                // Notify patterns changed so seq editor can update the pattern block UI.
                // This limits the amount of messages.
                // Fork (neo-buzz audio hygiene): Keys made a list and its collection on every call
                // (this runs on the audio thread on every buffer), and IsEmpty takes every lock of an
                // empty dictionary: only when a pattern changed since, as a flag says.
                if (songTime.PosInTick == 0 && clipsChanged)
                {
                    clipsChanged = false;
                    foreach (var clip in changedClipsSinceLastTick.Keys)
                    {
                        clip.NotifyClipChanged();
                        changedClipsSinceLastTick.TryRemove(clip, out bool result);
                    }
                }
            }
        }
        internal void SetModifiedFlag()
        {
            cb.SetModifiedFlag();
        }

        internal IEnumerable<IClipEditorColumn> GetClipColumnEvents(IClip pat, int tbegin, int tend)
        {
            if (SelectedMachine != null)
            {
                if (pat != null)
                {
                    MPEClip mpeClip = MPEClipsDB.GetMPEClip(pat);

                    return ClipEditorUtils.GetClipEditorColumnEvents(mpeClip, tbegin, tend);
                }
            }
            return new List<IClipEditorColumn>();
        }

        ConcurrentDictionary<IClip, bool> changedClipsSinceLastTick = new ConcurrentDictionary<IClip, bool>();

        volatile bool clipsChanged;

        internal void ClipChanged(IClip clip)
        {
            changedClipsSinceLastTick.TryAdd(clip, true);
            clipsChanged = true;                     // after: Work's drain sees the pattern
        }

        private static MPEClipColumn? GetNoteVelocityColumn(MPEClip mpeClip, int track)
        {
            return ClipEditorUtils.GetColumn(mpeClip, "note velocity", track)
                ?? ClipEditorUtils.GetColumn(mpeClip, "volume", track)
                ?? ClipEditorUtils.GetColumn(mpeClip, "velocity", track);
        }

        #region INotifyPropertyChanged Members

        public event PropertyChangedEventHandler PropertyChanged;

        #endregion
    }
}
