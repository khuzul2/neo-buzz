using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Common.Actions.PatternActions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Templates;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WDE.ModernPatternEditor.Actions;
using WDE.ModernPatternEditor.Chords;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor
{
    public partial class PatternEditor : UserControl, INotifyPropertyChanged
    {
        internal ChordsWindow chordsWindow;

        // Fork (docs/specs/studio-mvp.md S6): the members without WPF moved to PatternEditor.Core.cs,
        // shared with the portable builds. This part keeps the control.
        partial void SongChanging()
        {
            ColumnRenderer.BeatVisual.InvalidateResources();
            ColumnRenderer.BeatVisualCache.Clear();
        }

        // The shown pattern's length in beats (the shared MPEPattern makes new columns this long).
        internal int ShownBeatCount => (int)lengthBox.Value;

        public double ScaleFactor
        {
            get { return (PatternControl.Scale - 1.0) * 10.0; }
            set
            {
                PatternControl.Scale = 1.0 + value / 10.0;
                PropertyChanged.Raise(this, "ScaleFactor");
                UpdateAll();
            }
        }

        bool isHelpVisible = false;
        public bool IsHelpVisible
        {
            get { return isHelpVisible; }
            set
            {
                isHelpVisible = value;
                PropertyChanged.Raise(this, "IsHelpVisible");
                helpControl.Visibility = isHelpVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility KeyboardMappingVisibility { get { return IsKeyboardMappingVisible ? Visibility.Visible : Visibility.Collapsed; } }

        public Visibility SelectPatternEditorVisibility { get { return IsSelectPatternEditorVisible ? Visibility.Visible : Visibility.Collapsed; } }

        public Visibility SelectImportExportPatternVisibility { get { return IsImportExportPatternVisible ? Visibility.Visible : Visibility.Collapsed; } }

        DispatcherTimer timer;

        void GeneralSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "WPFIdealFontMetrics":
                    PropertyChanged.Raise(this, "TextFormattingMode");
                    break;
            }
        }

        void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "ColorNote": UpdateAll(); break;
                case "CursorRowHighlight": patternControl.UpdateCursor(); break;
                case "CursorScrollMode": UpdateAll(); break;
                case "FontFamily": UpdateAll(); break;
                case "FontSize": UpdateAll(); break;
                case "FontStyle": UpdateAll(); break;
                case "FontWeight": UpdateAll(); break;
                case "FontStretch": UpdateAll(); break;
                case "FontClearType": UpdateAll(); break;
                case "TextDropShadow": UpdateAll(); break;
                case "TextFormattingMode": UpdateAll(); break;
                case "HexRowNumbers": UpdateAll(); break;
                case "ColumnLabels": patternControl.CreateHeaders(); break;
                case "ParameterKnobs": patternControl.CreateHeaders(); break;
            }
        }

        void UpdateAll()
        {
            ColumnRenderer.Font.Update();
            var m = SelectedMachine;
            SelectedMachine = null;
            SelectedMachine = m;
            patternControl.UpdateScrollMargins();
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
                    patternControl.Focus();
                }
            };

            ClonePatternCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => SelectedMachine != null && SelectedMachine.SelectedPattern != null,
                ExecuteDelegate = x =>
                {
                    DoAction(new CreatePatternAction(SelectedMachine.Machine, SelectedMachine.Machine.GetNewPatternName(), 0, SelectedMachine.SelectedPattern.Pattern));
                    patternControl.Focus();
                }
            };

            DeletePatternCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => SelectedMachine != null && SelectedMachine.SelectedPattern != null,
                ExecuteDelegate = x =>
                {
                    DoAction(new DeletePatternAction(SelectedMachine.SelectedPattern.Pattern));
                    patternControl.Focus();
                }
            };

            this.InputBindings.Add(new InputBinding(CreatePatternCommand, new KeyGesture(Key.Return, ModifierKeys.Control)));
            this.InputBindings.Add(new InputBinding(ClonePatternCommand, new KeyGesture(Key.Return, ModifierKeys.Control | ModifierKeys.Shift)));
            this.InputBindings.Add(new InputBinding(DeletePatternCommand, new KeyGesture(Key.Delete, ModifierKeys.Control)));
        }

        public PatternEditor(IGUICallbacks cb)
        {
            this.cb = cb;
            EditContext = new EditContext(this);
            SelectedStepsDown = 1;
            SelectedStepsRight = 0;
            Global.GeneralSettings.PropertyChanged += new PropertyChangedEventHandler(GeneralSettings_PropertyChanged);
            Settings.PropertyChanged += Settings_PropertyChanged;
            SettingsWindow.AddSettings("Modern Pattern Editor", Settings);
            DataContext = this;
            Commands();

            // Fork: the engine's song outlives its editors, so Release lets go of it: else a released editor took
            // the song again on the next machine added, and every editor made in a run stayed, listening.
            buzzSong = Global.Buzz.Song;
            buzzSong.MachineAdded += OnSongMachineAdded;

            ResourceDictionary rd = GetBuzzThemeResources();
            if (rd != null) this.Resources.MergedDictionaries.Add(rd);
            
            InitializeComponent();

            patternControl.Editor = this;
            patternControl.Resources.MergedDictionaries.Add(this.Resources);
            playRecordManager = new PlayRecordManager(this);

            MPEPatternsDB = new MPEPatternDatabase(this);

            this.ContextMenu = new ContextMenu();

            this.Loaded += (sender, e) =>
            {
                KeyboardMappings = KeyboardMappingFile.Default.Mappings;
                PropertyChanged.Raise(this, "KeyboardMappings");
                SelectedKeyboardMapping = KeyboardMappingFile.Default.DefaultMapping;
            };

            btExportPattern.Click += (sender, e) =>
            {
                PatternXMLImportExport.ExportPattern(MPEPatternsDB.GetMPEPattern(SelectedMachine.SelectedPattern.Pattern));
            };

            btExportPatterns.Click += (sender, e) =>
            {
                PatternXMLImportExport.ExportPatterns(MPEPatternsDB);
            };

            btImportPatterns.Click += (sender, e) =>
            {
                PatternXMLImportExport.ImportPatterns(this);
            };

            this.KeyDown += (sender, e) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    if (e.Key == Key.Add)
                    {
                        //patternBox.SelectedIndex++;
                        int index = SelectedMachine.patterns.IndexOf(SelectedMachine.SelectedPattern);
                        if (index != -1)
                        {
                            index++;
                            index = Math.Min(index, SelectedMachine.patterns.Count() - 1);
                            var p = SelectedMachine.Patterns[index];
                            Global.Buzz.SetPatternEditorPattern(p.Pattern);
                        }
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Subtract)
                    {
                        //if (patternBox.SelectedIndex > 0) patternBox.SelectedIndex--;
                        int index = SelectedMachine.patterns.IndexOf(SelectedMachine.SelectedPattern);
                        if (index != -1)
                        {
                            index--;
                            index = Math.Max(index, 0);
                            var p = SelectedMachine.Patterns[index];
                            Global.Buzz.SetPatternEditorPattern(p.Pattern);
                        }
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Return)
                    {
                        Global.Buzz.ActivateSequenceEditor();
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Space)
                    {
                        //if (SelectedMachine != null && SelectedMachine.SelectedPattern != null)
                        //    SelectedMachine.SelectedPattern.Pattern.IsPlayingSolo ^= true;
                        //e.Handled = true;
                    }
                    else if (e.Key == Key.Divide)
                    {
                        if (SelectedMachine != null && SelectedMachine.Machine.BaseOctave > 0)
                            SelectedMachine.Machine.BaseOctave--;

                        e.Handled = true;
                    }
                    else if (e.Key == Key.Multiply)
                    {
                        if (SelectedMachine != null && SelectedMachine.Machine.BaseOctave < 9)
                            SelectedMachine.Machine.BaseOctave++;

                        e.Handled = true;
                    }
                    else if (e.Key == Key.F1)
                    {
                        IsHelpVisible ^= true;
                        e.Handled = true;
                    }
                }
                else if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    if (e.Key == Key.Down)
                    {
                        // machineBox.SelectedIndex++;
                        //e.Handled = true;
                    }
                    else if (e.Key == Key.Up)
                    {
                        //if (machineBox.SelectedIndex > 0) machineBox.SelectedIndex--;
                        //e.Handled = true;
                    }
                    else if (e.Key == Key.Add)
                    {
                        SelectedMachine.UndoableTrackCount++;
                    }
                    else if (e.Key == Key.Subtract)
                    {
                        SelectedMachine.UndoableTrackCount--;
                    }
                    else if (e.Key == Key.Back)
                    {
                        ShowPatternProperties();
                    }
                }
                else if (Keyboard.Modifiers == ModifierKeys.Alt)
                {
                    if (e.Key == Key.System)
                    {
                        switch (e.SystemKey)
                        {
                            //case Key.M: machineBox.IsDropDownOpen = true; e.Handled = true; break;
                            //case Key.P: patternBox.IsDropDownOpen = true; e.Handled = true; break;
                            case Key.B: baseOctaveBox.IsDropDownOpen = true; e.Handled = true; break;
                            case Key.R: IsKeyboardMappingVisible = true; rootNoteBox.IsDropDownOpen = true; e.Handled = true; break;
                            case Key.Y: IsKeyboardMappingVisible = true; kbMappingBox.IsDropDownOpen = true; e.Handled = true; break;
                            case Key.N: PlayNotes ^= true; e.Handled = true; break;
                        }
                    }
                }

            };

            this.PreviewGotKeyboardFocus += (sender, e) =>
            {
                Global.Buzz.EditContext = EditContext;
            };

            InvalidateVisual(); // Force draw?

            //machineBox.DropDownClosed += (sender, e) => { patternControl.Focus(); };
            //patternBox.DropDownClosed += (sender, e) => { patternControl.Focus(); };
            baseOctaveBox.DropDownClosed += (sender, e) => { patternControl.Focus(); };
            rootNoteBox.DropDownClosed += (sender, e) => { patternControl.Focus(); };
            kbMappingBox.DropDownClosed += (sender, e) => { patternControl.Focus(); };

            this.IsVisibleChanged += (sender, e) =>
            {
                if (IsVisible && timer == null)
                {
                    SetTimer();
                }
                else if (!IsVisible && timer != null)
                {
                    timer.Stop();
                    timer = null;
                }
            };

            btChords.Click += (sender, e) =>
            {
                if (chordsWindow == null)
                {
                    chordsWindow = new ChordsWindow(this);
                    new WindowInteropHelper(chordsWindow).Owner = ((HwndSource)PresentationSource.FromVisual(this)).Handle;
                    chordsWindow.Show();
                    chordsWindow.Closed += (sender2, e2) =>
                    {
                        chordsWindow = null;
                    };
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

            if (timer != null)
                timer.Stop();

            if (chordsWindow != null)
                chordsWindow.Close();
            MPEPatternsDB.Release();
            if (SelectedMachine != null)
                SelectedMachine.Machine = null;
            SelectedMachine = null;
            TargetMachine = null;
            //machines.Clear();
            Song = null;
            Global.GeneralSettings.PropertyChanged -= GeneralSettings_PropertyChanged;
            Settings.PropertyChanged -= Settings_PropertyChanged;

            patternControl.Release();
            playRecordManager.Release();
        }

        void SetTimer()
        {
            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(20);
            timer.Tick += (sender, e) =>
            {
                if (Settings.FollowPlayingPattern)
                {
                    var pattern = playRecordManager.GetPlayingPattern(selectedMachine.Machine);
                    if (pattern != null && SelectedMachine.SelectedPattern.Pattern != pattern)
                    {
                        SelectPattern(pattern);
                    }
                }

                patternControl.TimerUpdate();
            };
            timer.Start();
        }

        public void Activate()
        {
            patternControl.Focus();
        }

        public TextFormattingMode TextFormattingMode { get { return Global.GeneralSettings.WPFIdealFontMetrics ? TextFormattingMode.Ideal : TextFormattingMode.Display; } }

        // From native
        public void ThemeChanged()
        {
            ResourceDictionary rd = GetBuzzThemeResources();
            if (rd != null) this.Resources.MergedDictionaries.Add(rd);
            InvalidateVisual();
            SelectedMachine.SelectedPattern.Pattern = SelectedMachine.SelectedPattern.Pattern;
        }

        PatternPropertiesWindow patternPropertiesWindow;
        // From native
        public void ShowPatternProperties()
        {
            patternPropertiesWindow = new PatternPropertiesWindow(this);

            new WindowInteropHelper(patternPropertiesWindow).Owner = ((HwndSource)PresentationSource.FromVisual(this)).Handle;

            if (patternPropertiesWindow.ShowDialog() == true)
            {
                lock (syncLock)
                {
                    using (new ActionGroup(EditContext.ActionStack))
                    {
                        IPattern pattern = SelectedMachine.SelectedPattern.Pattern;
                        MPEPattern mpepat = MPEPatternsDB.GetMPEPattern(pattern);

                        if (pattern.Length != patternPropertiesWindow.PatternLenghtInBeats * PatternControl.BUZZ_TICKS_PER_BEAT)
                            DoAction(new MPESetPatternLengthAction(MPEPatternsDB.GetMPEPattern(pattern), pattern.Length / PatternControl.BUZZ_TICKS_PER_BEAT, patternPropertiesWindow.PatternLenghtInBeats)); //pattern.Length = ppw.PatternLenghtInBeats * Global.Buzz.TPB; // ToDo: Undoable 

                        if (mpepat.RowsPerBeat != patternPropertiesWindow.RowsPerBeat)
                        {
                            DoAction(new MPESetPatternRowsPerBeatAction(MPEPatternsDB.GetMPEPattern(pattern), SelectedMachine.SelectedPattern, patternPropertiesWindow.RowsPerBeat));
                        }

                        if (pattern.Name != patternPropertiesWindow.PatternName)
                            DoAction(new RenamePatternAction(MPEPatternsDB.GetMPEPattern(pattern), cb, patternPropertiesWindow.PatternName));

                        var parameters = patternPropertiesWindow.GetSelectedParameters();
                        if (parameters.Count() > 0)
                        {
                            foreach (var mpePattern in MPEPatternsDB.GetPatterns())
                            {
                                DoAction(new MPEAddOrRemoveColumnsAction(mpePattern, selectedMachine.patterns.FirstOrDefault(x => x.Pattern == mpePattern.Pattern), parameters));
                            }
                        }
                        // PropertyChanged.Raise(this, "SelectedMachine");
                    }
                }
            }

            patternPropertiesWindow = null;
        }

        internal ResourceDictionary GetBuzzThemeResources()
        {
            ResourceDictionary skin = new ResourceDictionary();

            try
            {
                string selectedTheme = Global.Buzz.SelectedTheme == "<default>" ? "Default" : Global.Buzz.SelectedTheme;
                string skinPath = Global.BuzzPath + "\\Themes\\" + selectedTheme + "\\ModernPatternEditor\\ModernPatternEditor.xaml";
                
                skin = (ResourceDictionary)XamlReaderEx.LoadHack(skinPath);
            }
            catch (Exception)
            {
                string skinPath = Global.BuzzPath + "\\Themes\\Default\\ModernPatternEditor\\ModernPatternEditor.xaml";
                skin.Source = new Uri(skinPath, UriKind.Absolute);
            }

            return skin;
        }

    }
}
