// Fork (docs/specs/studio-mvp.md S6): the portable editor's own part of ClipEditor, where the WPF
// build has its UserControl (ClipEditor.xaml.cs). The shared core (ClipEditor.Core.cs) holds the
// clips, their data and playback; this adds what the WPF control gave it: construction, a
// dispatcher, the toolkit-free ClipControl, the clip commands and properties, and the view, which
// a build with a UI makes (ViewFactory: the studio's Avalonia view). rebuzz-headless's build has none.

using BuzzGUI.Common;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Common.Actions.ClipActions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System.ComponentModel;
using WDE.ModernPatternEditor.Actions;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor
{
    public partial class ClipEditor
    {
        /// <summary>Makes the editor's view (the studio's build sets it); null: no view.</summary>
        public static Func<ClipEditor, object> ViewFactory { get; set; }

        internal readonly ClipControl clipControl;

        /// <summary>Runs edits on the editor's thread (WPF: the control's Dispatcher).</summary>
        internal EditorDispatcher Dispatcher { get; }

        /// <summary>The toolkit-free control: cursor, selection, keys and edit commands.</summary>
        public ClipControl Control => clipControl;

        object view;

        /// <summary>The editor's view, made on first use (on the UI thread); null without a UI.</summary>
        public object View => view ??= ViewFactory?.Invoke(this);

        /// <summary>The shown clip's length in beats (WPF: the toolbar's Beats box).</summary>
        internal int ShownBeatCount => SelectedMachine?.SelectedClip?.Clip != null ? SelectedMachine.SelectedClip.BeatCount : 1;

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
            get { return (ClipControl.Scale - 1.0) * 10.0; }
            set
            {
                ClipControl.Scale = 1.0 + value / 10.0;
                PropertyChanged.Raise(this, "ScaleFactor");
            }
        }

        public ClipEditor(IGUICallbacks cb)
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

            clipControl = new ClipControl(this);
            playRecordManager = new PlayRecordManager(this);
            MPEClipsDB = new MPEClipDatabase(this);

            var mappings = KeyboardMappingFile.Default;
            KeyboardMappings = mappings.Mappings;
            SelectedKeyboardMapping = mappings.DefaultMapping;
        }

        void Commands()
        {
            CreateClipCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => SelectedMachine != null,
                ExecuteDelegate = x =>
                {
                    DoAction(new CreateClipAction(SelectedMachine.Machine, SelectedMachine.Machine.GetNewClipName(),
                        SelectedMachine.SelectedClip != null ? SelectedMachine.SelectedClip.Clip.Length : Global.GeneralSettings.ClipLength));
                }
            };

            CloneClipCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => SelectedMachine != null && SelectedMachine.SelectedClip != null,
                ExecuteDelegate = x =>
                {
                    DoAction(new CreateClipAction(SelectedMachine.Machine, SelectedMachine.Machine.GetNewClipName(), 0, SelectedMachine.SelectedClip.Clip));
                }
            };

            DeleteClipCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => SelectedMachine != null && SelectedMachine.SelectedClip != null,
                ExecuteDelegate = x =>
                {
                    DoAction(new DeleteClipAction(SelectedMachine.SelectedClip.Clip));
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

            MPEClipsDB.Release();
            if (SelectedMachine != null)
                SelectedMachine.Machine = null;
            SelectedMachine = null;
            TargetMachine = null;
            Song = null;

            (view as IDisposable)?.Dispose();
            view = null;
            clipControl.Release();
            playRecordManager.Release();
        }

        public void Activate()
        {
            Activated?.Invoke();
        }

        /// <summary>
        /// The view's timer (WPF: every 20 ms while the editor is visible): follows the playing clip
        /// when the setting says so.
        /// </summary>
        public void TimerUpdate()
        {
            if (Settings.FollowPlayingClip && SelectedMachine?.Machine != null && SelectedMachine.SelectedClip != null)
            {
                var clip = playRecordManager.GetPlayingClip(SelectedMachine.Machine);
                if (clip != null && SelectedMachine.SelectedClip.Clip != clip)
                    SelectClip(clip);
            }
        }

        /// <summary>
        /// The clip properties, as WPF's dialog applies them (one undo step): length in beats, rows
        /// per beat, name, and the columns: the parameters given (null: as they are).
        /// </summary>
        public void ApplyClipProperties(int beats, int rowsPerBeat, string name, IEnumerable<IParameter> parameters)
        {
            if (SelectedMachine?.SelectedClip == null) return;
            lock (syncLock)
            {
                using (new ActionGroup(EditContext.ActionStack))
                {
                    IClip clip = SelectedMachine.SelectedClip.Clip;
                    MPEClip mpepat = MPEClipsDB.GetMPEClip(clip);

                    if (clip.Length != beats * ClipControl.BUZZ_TICKS_PER_BEAT)
                        DoAction(new MPESetClipLengthAction(MPEClipsDB.GetMPEClip(clip), clip.Length / ClipControl.BUZZ_TICKS_PER_BEAT, beats));

                    if (mpepat.RowsPerBeat != rowsPerBeat)
                        DoAction(new MPESetClipRowsPerBeatAction(MPEClipsDB.GetMPEClip(clip), SelectedMachine.SelectedClip, rowsPerBeat));

                    if (name != null && clip.Name != name)
                        DoAction(new RenameClipAction(MPEClipsDB.GetMPEClip(clip), cb, name));

                    if (parameters != null && parameters.Any())
                    {
                        foreach (var mpeClip in MPEClipsDB.GetClips())
                            DoAction(new MPEAddOrRemoveColumnsAction(mpeClip, SelectedMachine.clips.FirstOrDefault(x => x.Clip == mpeClip.Clip), parameters));
                    }
                }
            }
        }

        /// <summary>
        /// The parameters the clip properties offer for a machine: its own. (CU7: the editor's own MIDI lanes are not offered; a clip's MIDI notes are
        /// its Notes lane in the store, which the note lanes of the view show.)
        /// </summary>
        public IEnumerable<IParameter> ColumnChoices(IMachine machine) =>
            machine.ParameterGroups[1].Parameters.Concat(machine.ParameterGroups[2].Parameters);

        /// <summary>Whether the shown clip has a column for the parameter.</summary>
        public bool HasColumn(IParameter parameter) => SelectedMachine?.SelectedClip != null && MPEClipsDB.IsParameterEnabled(parameter);
    }
}
