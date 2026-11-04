// Fork (docs/specs/studio-mvp.md S6): the portable editor's own part of PatternEditor, where the WPF
// build has its UserControl (PatternEditor.xaml.cs). The shared core (PatternEditor.Core.cs) holds the
// patterns, their data and playback; this adds what the WPF control gave it: construction, a
// dispatcher, the toolkit-free PatternControl, the pattern commands and properties, and the view, which
// a build with a UI makes (ViewFactory: the studio's Avalonia view). rebuzz-headless's build has none.

using BuzzGUI.Common;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Common.Actions.PatternActions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System.ComponentModel;
using WDE.ModernPatternEditor.Actions;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor
{
    public partial class PatternEditor
    {
        /// <summary>Makes the editor's view (the studio's build sets it); null: no view.</summary>
        public static Func<PatternEditor, object> ViewFactory { get; set; }

        internal readonly PatternControl patternControl;

        /// <summary>Runs edits on the editor's thread (WPF: the control's Dispatcher).</summary>
        internal EditorDispatcher Dispatcher { get; }

        /// <summary>The toolkit-free control: cursor, selection, keys and edit commands.</summary>
        public PatternControl Control => patternControl;

        object view;

        /// <summary>The editor's view, made on first use (on the UI thread); null without a UI.</summary>
        public object View => view ??= ViewFactory?.Invoke(this);

        /// <summary>The shown pattern's length in beats (WPF: the toolbar's Beats box).</summary>
        internal int ShownBeatCount => SelectedMachine?.SelectedPattern?.Pattern != null ? SelectedMachine.SelectedPattern.BeatCount : 1;

        /// <summary>The view should take the keyboard (the engine's Activate).</summary>
        public event Action Activated;

        bool isHelpVisible;
        public bool IsHelpVisible
        {
            get { return isHelpVisible; }
            set
            {
                isHelpVisible = value;
                PropertyChanged.Raise(this, "IsHelpVisible");
            }
        }

        public double ScaleFactor
        {
            get { return (PatternControl.Scale - 1.0) * 10.0; }
            set
            {
                PatternControl.Scale = 1.0 + value / 10.0;
                PropertyChanged.Raise(this, "ScaleFactor");
            }
        }

        public PatternEditor(IGUICallbacks cb)
        {
            this.cb = cb;
            Dispatcher = new EditorDispatcher();
            EditContext = new EditContext(this);
            SelectedStepsDown = 1;
            SelectedStepsRight = 0;
            Commands();

            // The engine's song outlives its editors, so Release lets go of it: else a released editor took
            // the song again on the next machine added, and every editor made in a run stayed, listening.
            buzzSong = Global.Buzz.Song;
            buzzSong.MachineAdded += OnSongMachineAdded;

            patternControl = new PatternControl(this);
            playRecordManager = new PlayRecordManager(this);
            MPEPatternsDB = new MPEPatternDatabase(this);

            var mappings = KeyboardMappingFile.Default;
            KeyboardMappings = mappings.Mappings;
            SelectedKeyboardMapping = mappings.DefaultMapping;
        }

        void Commands()
        {
            CreatePatternCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => SelectedMachine != null,
                ExecuteDelegate = x =>
                {
                    DoAction(new CreatePatternAction(SelectedMachine.Machine, SelectedMachine.Machine.GetNewPatternName(),
                        SelectedMachine.SelectedPattern != null ? SelectedMachine.SelectedPattern.Pattern.Length : Global.GeneralSettings.PatternLength));
                }
            };

            ClonePatternCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => SelectedMachine != null && SelectedMachine.SelectedPattern != null,
                ExecuteDelegate = x =>
                {
                    DoAction(new CreatePatternAction(SelectedMachine.Machine, SelectedMachine.Machine.GetNewPatternName(), 0, SelectedMachine.SelectedPattern.Pattern));
                }
            };

            DeletePatternCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => SelectedMachine != null && SelectedMachine.SelectedPattern != null,
                ExecuteDelegate = x =>
                {
                    DoAction(new DeletePatternAction(SelectedMachine.SelectedPattern.Pattern));
                }
            };
        }

        /// <summary>The engine's song, whose first machine gives the editor its Song.</summary>
        readonly ISong buzzSong;

        void OnSongMachineAdded(IMachine obj)
        {
            if (Song == null)
                Song = Global.Buzz.Song;
        }

        public void Release()
        {
            buzzSong.MachineAdded -= OnSongMachineAdded;
            EditContext = null;

            MPEPatternsDB.Release();
            if (SelectedMachine != null)
                SelectedMachine.Machine = null;
            SelectedMachine = null;
            TargetMachine = null;
            Song = null;

            (view as IDisposable)?.Dispose();
            view = null;
            patternControl.Release();
            playRecordManager.Release();
        }

        public void Activate()
        {
            Activated?.Invoke();
        }

        /// <summary>
        /// The view's timer (WPF: every 20 ms while the editor is visible): follows the playing pattern
        /// when the setting says so.
        /// </summary>
        public void TimerUpdate()
        {
            if (Settings.FollowPlayingPattern && SelectedMachine?.Machine != null && SelectedMachine.SelectedPattern != null)
            {
                var pattern = playRecordManager.GetPlayingPattern(SelectedMachine.Machine);
                if (pattern != null && SelectedMachine.SelectedPattern.Pattern != pattern)
                    SelectPattern(pattern);
            }
        }

        /// <summary>
        /// The pattern properties, as WPF's dialog applies them (one undo step): length in beats, rows
        /// per beat, name, and the columns: the parameters given (null: as they are), the editor's MIDI
        /// lanes included.
        /// </summary>
        public void ApplyPatternProperties(int beats, int rowsPerBeat, string name, IEnumerable<IParameter> parameters)
        {
            if (SelectedMachine?.SelectedPattern == null) return;
            lock (syncLock)
            {
                using (new ActionGroup(EditContext.ActionStack))
                {
                    IPattern pattern = SelectedMachine.SelectedPattern.Pattern;
                    MPEPattern mpepat = MPEPatternsDB.GetMPEPattern(pattern);

                    if (pattern.Length != beats * PatternControl.BUZZ_TICKS_PER_BEAT)
                        DoAction(new MPESetPatternLengthAction(MPEPatternsDB.GetMPEPattern(pattern), pattern.Length / PatternControl.BUZZ_TICKS_PER_BEAT, beats));

                    if (mpepat.RowsPerBeat != rowsPerBeat)
                        DoAction(new MPESetPatternRowsPerBeatAction(MPEPatternsDB.GetMPEPattern(pattern), SelectedMachine.SelectedPattern, rowsPerBeat));

                    if (name != null && pattern.Name != name)
                        DoAction(new RenamePatternAction(MPEPatternsDB.GetMPEPattern(pattern), cb, name));

                    if (parameters != null && parameters.Any())
                    {
                        foreach (var mpePattern in MPEPatternsDB.GetPatterns())
                            DoAction(new MPEAddOrRemoveColumnsAction(mpePattern, SelectedMachine.patterns.FirstOrDefault(x => x.Pattern == mpePattern.Pattern), parameters));
                    }
                }
            }
        }

        /// <summary>The parameters the pattern properties offer for a machine: the editor's MIDI lanes, then the machine's own.</summary>
        public IEnumerable<IParameter> ColumnChoices(IMachine machine) =>
            PatternEditorUtils.GetInternalParameters(machine).Concat(machine.ParameterGroups[1].Parameters).Concat(machine.ParameterGroups[2].Parameters);

        /// <summary>Whether the shown pattern has a column for the parameter.</summary>
        public bool HasColumn(IParameter parameter) => SelectedMachine?.SelectedPattern != null && MPEPatternsDB.IsParameterEnabled(parameter);
    }
}
