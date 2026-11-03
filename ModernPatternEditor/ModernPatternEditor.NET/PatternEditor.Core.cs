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
    public partial class PatternEditor : INotifyPropertyChanged
    {
        // The renderer's caches start over with each song (the WPF part clears them).
        partial void SongChanging();

        internal readonly string Version = "0.9.4";
        ISong song;
        internal MPEPatternDatabase MPEPatternsDB;
        internal static PatternClipboard clipboard = new PatternClipboard();
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
                    if (Global.Buzz.ActiveView != BuzzView.PatternView)
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
        public bool isSelectPatternEditorVisible;
        public bool IsSelectPatternEditorVisible
        {
            get { return isSelectPatternEditorVisible; }
            set
            {
                isSelectPatternEditorVisible = value;
                PropertyChanged.Raise(this, "IsSelectPatternEditorVisible");
                PropertyChanged.Raise(this, "SelectPatternEditorVisibility");
            }
        }
        public bool isImportExportPatternVisible;
        public bool IsImportExportPatternVisible
        {
            get { return isImportExportPatternVisible; }
            set
            {
                isImportExportPatternVisible = value;
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
        public static PatternEditorSettings Settings = new PatternEditorSettings();
        public readonly IGUICallbacks cb;

        public EditContext EditContext;
        public ICommand CreatePatternCommand { get; private set; }
        public ICommand ClonePatternCommand { get; private set; }
        public ICommand DeletePatternCommand { get; private set; }
        public void SelectPattern(IPattern p)
        {
            SelectMachine(p.Machine);

            SelectedMachine.SelectedPattern = SelectedMachine.patterns.FirstOrDefault(pvm => pvm.Pattern == p);

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

                    MPEPatternsDB.Machine = TargetMachine;

                    if (patternEditorData != null)
                    {   
                        List<MPEPattern> patterns = PatternEditorUtils.ProcessEditorData(this, patternEditorData);
                        if (patterns.Count != 0)
                        {
                            MPEPatternsDB.SetPatterns(patterns);
                        }
                    }

                    if (MPEPatternsDB.GetPatterns().Count() == 0 && TargetMachine.Patterns.Count() > 0)
                    {
                        // Code never goes here?
                        MPEPatternsDB.AddPattern(TargetMachine.Patterns[0]);
                    }

                    if (SelectedMachine == null)
                        SelectedMachine = new MachineVM(this) { Machine = TargetMachine };
                    else
                        SelectedMachine.Machine = machine;
                }
            }
        }

        internal void TargetMachine_PatternAdded(IPattern pattern)
        {
            //lock (syncLock)
            {
                MPEPatternsDB.AddPattern(pattern);
            }
        }

        internal void TargetMachine_PatternRemoved(IPattern pattern)
        {
            //lock (syncLock)
            {
                MPEPatternsDB.RemovePattern(pattern);
            }
        }
        byte[] patternEditorData;
        // From native
        public void SetPatternEditorData(byte[] data)
        {
            // Process later when all machines are loaded
            patternEditorData = data;
            /*
            List<MPEPattern> patterns = PatternEditorUtils.ProcessEditorData(this, data);
            if (patterns.Count != 0)
            {
                MPEPatternsDB.SetPatterns(patterns);
            }
            */
        }

        // From native
        public byte[] GetPatternEditorData()
        {
            if (Settings.PXPDataFormat)
                return PatternEditorUtils.CreatePatternXPPatternData(MPEPatternsDB.GetPatterns());
            else
                return PatternEditorUtils.CreateModernPatternEditorData(MPEPatternsDB.GetPatterns());

        }

        // From native
        public void CreatePattern(string name, int numrows)
        {
        }


        public void SetEditorPattern(IPattern pattern)
        {
            //lock (syncLock)
            {
                if (TargetMachine != null && SelectedMachine != null)
                {
                    if (pattern != null && SelectedMachine.SelectedPattern.Pattern != pattern)
                        SelectPattern(pattern);
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
        public void CreatePatternCopy(string newName, string oldName)
        {
            MPEPatternsDB.PrepareCopy(newName, oldName);
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
                else if (MidiEdit && patternControl.Pattern != null)
                {
                    var cursor = patternControl.Pattern.CursorPosition;
                    if (cursor.ParameterColumn.Type == ColumnRenderer.ColumnType.Note && velocity != 0)
                    {
                        int buzzNote = velocity == 0 ? BuzzNote.Off : BuzzNote.FromMIDINote(value);
                        int time = cursor.TimeInBeat + cursor.Beat * PatternControl.BUZZ_TICKS_PER_BEAT * PatternEvent.TimeBase;
                        MPEPattern mpePattern = MPEPatternsDB.GetMPEPattern(patternControl.Pattern.Pattern);
                        MPEPatternColumn mpeColumn = mpePattern.GetColumn(cursor.ParameterColumn.PatternColumn);
                        MPEPatternColumn midiVelocityColumn = MidiVelocity ? GetNoteVelocityColumn(mpePattern, cursor.ParameterColumn.PatternColumn.Track) : null;

                        Dispatcher.Invoke(() =>
                        {
                            if (patternControl.IsKeyboardFocused)
                            {
                                using (new ActionGroup(EditContext.ActionStack))
                                {
                                    DoAction(
                                        new MPESetOrClearEventsAction(
                                            patternControl.Pattern.Pattern,
                                            mpeColumn,
                                            new PatternEvent[] { new PatternEvent() { Time = time, Value = buzzNote } },
                                            true));

                                    if (midiVelocityColumn != null)
                                    {
                                        DoAction(
                                            new MPESetOrClearEventsAction(
                                                patternControl.Pattern.Pattern,
                                                midiVelocityColumn,
                                                new PatternEvent[]
                                                {
                                                    new PatternEvent() { Time = time, Value = velocity }
                                                },
                                                true));
                                    }
                                }

                                patternControl.MoveCursorDelta(SelectedStepsRight, SelectedStepsDown);
                                playRecordManager.UpdatePlayingNotePattern(
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
        public int[] ExportMidiEvents(IPattern pat)
        {
            //lock (syncLock)
            {
                if (SelectedMachine != null)
                {
                    //IPattern pat = SelectedMachine.Machine.Patterns.FirstOrDefault(x => x.Name == pattern);
                    if (pat != null)
                    {
                        MPEPattern mpePattern = MPEPatternsDB.GetMPEPattern(pat);
                        return PatternEditorUtils.ExportMidiEvents(mpePattern);
                    }
                }
                return new int[0];
            }
        }

        // Fork (neo-buzz 0.7.1): a pattern's MIDI note lanes as notes, and back (the Session View).
        public int[] ExportClipNotes(IPattern pat)
        {
            lock (syncLock)
            {
                return pat == null ? new int[0] : PatternEditorUtils.ExportClipNotes(MPEPatternsDB.GetMPEPattern(pat));
            }
        }

        public bool ImportClipNotes(IPattern pat, int[] data)
        {
            if (pat == null) return false;
            var mpePattern = MPEPatternsDB.GetMPEPattern(pat);
            List<TrackerRow> rows;
            int tracks;
            lock (syncLock)
            {
                // Planned first: notes that don't fit change nothing.
                rows = PatternEditorUtils.PlanClipNotes(mpePattern, data, out tracks);
            }
            if (rows == null) return false;
            // Outside the lock: a native machine's track count takes the audio lock, and the audio
            // thread holds that while it runs this editor's Work, which takes ours.
            var machine = pat.Machine;
            if (machine != null && tracks > machine.TrackCount)
                machine.TrackCount = tracks;
            if (machine == null || machine.TrackCount < tracks) return false;
            lock (syncLock)
            {
                PatternEditorUtils.WriteClipNotes(mpePattern, rows, tracks);
            }
            bool done = true;
            // The pattern shown here shows its lanes as they are now.
            var shown = SelectedMachine?.SelectedPattern;
            if (shown != null && shown.Pattern == pat)
                shown.Pattern = pat;
            SetModifiedFlag();
            return done;
        }

        public bool ImportMidiEvents(IPattern pat, int [] data)
        {
            lock (syncLock)
            {
                if (pat != null)
                {
                    MPEPattern mpePattern = MPEPatternsDB.GetMPEPattern(pat);
                    return PatternEditorUtils.ImportMidiEvents(mpePattern, data);
                }
            }

            return false;
        }

        public bool CanExecuteCommand(BuzzCommand cmd)
        {
            if (EditContext == null)
                return false;

            if (patternControl.Pattern.ColumnSets.Count == 0)
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
                return patternControl.CopyCommand.CanExecute(patternControl);
            }
            else if (cmd == BuzzCommand.Cut)
            {
                return patternControl.CutCommand.CanExecute(patternControl);
            }
            else if (cmd == BuzzCommand.Paste)
            {
                return patternControl.PasteCommand.CanExecute(patternControl);
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
                patternControl.CopyCommand.Execute(patternControl);
            }
            else if (cmd == BuzzCommand.Cut)
            {
                patternControl.CutCommand.Execute(patternControl);
            }
            else if (cmd == BuzzCommand.Paste)
            {
                patternControl.PasteCommand.Execute(patternControl);
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
            patternControl.CopyCommand.Execute(patternControl);
        }

        // From native
        public void DoCut()
        {
            patternControl.CutCommand.Execute(patternControl);
        }

        // From native
        public void DoPaste()
        {
            patternControl.PasteCommand.Execute(patternControl);
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
                        playRecordManager.Play(songTime);
                    }
                }

                // Notify patterns changed so seq editor can update the pattern block UI.
                // This limits the amount of messages.
                // Fork (neo-buzz audio hygiene): Keys made a list and its collection on every call
                // (this runs on the audio thread on every buffer); only when something changed now.
                if (songTime.PosInTick == 0 && !changedPatternsSinceLastTick.IsEmpty)
                {
                    foreach (var pattern in changedPatternsSinceLastTick.Keys)
                    {
                        pattern.NotifyPatternChanged();
                        changedPatternsSinceLastTick.TryRemove(pattern, out bool result);
                    }
                }
            }
        }
        internal void SetModifiedFlag()
        {
            cb.SetModifiedFlag();
        }

        internal IEnumerable<IPatternEditorColumn> GetPatternColumnEvents(IPattern pat, int tbegin, int tend)
        {
            if (SelectedMachine != null)
            {
                if (pat != null)
                {
                    MPEPattern mpePattern = MPEPatternsDB.GetMPEPattern(pat);

                    return PatternEditorUtils.GetPatternEditorColumnEvents(mpePattern, tbegin, tend);
                }
            }
            return new List<IPatternEditorColumn>();
        }

        ConcurrentDictionary<IPattern, bool> changedPatternsSinceLastTick = new ConcurrentDictionary<IPattern, bool>();

        internal void PatternChanged(IPattern pattern)
        {
            changedPatternsSinceLastTick.TryAdd(pattern, true);
        }

        private static MPEPatternColumn? GetNoteVelocityColumn(MPEPattern mpePattern, int track)
        {
            return PatternEditorUtils.GetColumn(mpePattern, "note velocity", track)
                ?? PatternEditorUtils.GetColumn(mpePattern, "volume", track)
                ?? PatternEditorUtils.GetColumn(mpePattern, "velocity", track);
        }

        #region INotifyPropertyChanged Members

        public event PropertyChangedEventHandler PropertyChanged;

        #endregion
    }
}
