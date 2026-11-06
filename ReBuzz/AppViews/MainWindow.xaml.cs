using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using BuzzGUI.Interfaces;
using BuzzGUI.MachineView;
using BuzzGUI.MachineView.HDRecorder;
using BuzzGUI.PianoKeyboard;
using BuzzGUI.SequenceEditor;
using BuzzGUI.ToolBar;
using BuzzGUI.WavetableView;
using ReBuzz.AppViews;
using ReBuzz.Common;
using ReBuzz.Core;
using ReBuzz.FileOps;
using ReBuzz.MachineManagement;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ReBuzz
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged, IInitializationObserver
    {
        ReBuzzCore Buzz { get; set; }
        EditorView EditorView { get; set; }
        SequenceEditor seqenceEditor;
        MachineView MachineView { get; set; }
        WavetableVM WavetableVM { get; set; }
        UserControl WavetableControl { get; set; }
        InfoView InfoView { get; set; }

        PreferencesWindow preferencesWindow;
        AppViews.Session.SessionWindow sessionWindow;   // fork: Session View in its own window
        AppViews.Session.SessionView sessionDock;       // fork: docked Session View (BuzzView.SessionView)

        readonly DebugWindow debugWindow;

        StatusWindow statusWindow;
        HDRecorderWindow hDRecorderWindow;

        readonly SplashScreenWindow splashScreenWindow;
        KeyboardWindow keyboardWindow = null;

        WDE.ModernSequenceEditor.SequenceEditor ModernSequenceEditor { get; set; }
        WDE.ModernSequenceEditorHorizontal.SequenceEditor ModernSequenceEditorHorizontal { get; set; }

        MachineManager machineManager;
        ToolBarVM ToolBarVM { get; set; }

        IMachineDatabase MachineDB { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

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
        private WindowStyle mainWindowStyle;
        private UserControl ToolBarControl;
        private readonly IUiDispatcher windowsGuiDispatcher = new WindowsGuiDispatcher();
        private readonly IRegistryEx registryEx = new WindowsRegistry();
        private readonly string buzzPath = Global.BuzzPath;

        private ReBuzzCoreInitialization reBuzzCoreInitialization;
        private Agent.StudioAgent studioAgent;   // fork: agent server (studio mode)

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

        void IInitializationObserver.NotifyMachineManagerCreated(MachineManager machineManager)
        {
            this.machineManager = machineManager;
        }

        public MainWindow()
        {
            var generalSettings = Global.GeneralSettings;
            var engineSettings = Global.EngineSettings;
            var registryRoot = Global.RegistryRoot;
            debugWindow = DebugWindow.CreateAsync(buzzPath);

            DataContext = this;

            InitializeComponent();

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                ReBuzzCore.SafeMode = true;
            }

            Buzz = new ReBuzzCore(
                generalSettings,
                engineSettings,
                buzzPath,
                registryRoot,
                new MachineDLLScanner(windowsGuiDispatcher, engineSettings),
                windowsGuiDispatcher,
                registryEx,
                new FileNameToLoadChoiceThroughOpenFileDialog(),
                new FileNameToSaveChoiceThroughSaveFileDialog(),
                new UserMessagesViaMessageBox(), new WindowsKeyboard());

            reBuzzCoreInitialization = new ReBuzzCoreInitialization(Buzz, buzzPath, windowsGuiDispatcher, registryEx, new WindowsKeyboard(), Global.EngineSettings);
            reBuzzCoreInitialization.StartReBuzzEngineStep1(Buzz_PropertyChanged);

            if (ReBuzzCore.SafeMode)
            {
                debugWindow.Show();
            }

            BuzzGUIStartup.PreInit();

            splashScreenWindow = SplashScreenWindow.CreateAsync("Loading..", this, buzzPath);

            generalSettings.PropertyChanged += GeneralSettings_PropertyChanged;
            if (generalSettings.WPFIdealFontMetrics)
            {
                TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            }
            else
            {
                TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            }

            reBuzzCoreInitialization.StartReBuzzEngineStep2(new WindowInteropHelper(this).EnsureHandle());

            var buzzCore = Buzz;
            var song = buzzCore.SongCore;

            song.ShowMachineViewContextMenu += (x, y) =>
            {
                MachineView.ContextMenu.IsOpen = true;
            };

            if (MachineView.StaticSettings.ShowEngineThreads)
                Buzz.dtEngineThread.Start();

            MachineView.StaticSettings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "ShowEngineThreads")
                {
                    if (MachineView.StaticSettings.ShowEngineThreads)
                        Buzz.dtEngineThread.Start();
                    else
                        Buzz.dtEngineThread.Stop();
                }
            };

            reBuzzCoreInitialization.StartReBuzzEngineStep3(engineSettings, this);

            splashScreenWindow.UpdateText("Scan Plugin DLLs");
            MachineDB = new MachineDatabase(buzzCore, buzzPath, windowsGuiDispatcher, Global.EngineSettings);

            reBuzzCoreInitialization.StartReBuzzEngineStep4(MachineDB,
              machineDbDatabaseEvent: (str) =>
              {
                  splashScreenWindow.UpdateText(str);
              },
              onPatternEditorActivated: Buzz_PatternEditorActivated,
              onSequenceEditorActivated: Buzz_SequenceEditorActivated,
              onShowSettings: (activeTab) =>
              {
                  SettingsWindow.Show(MachineView,
              activeTab);
              },
              onSetPatternEditorControl: (control) =>
              {
                  EditorView.SetEditorControl(control as System.Windows.Controls.UserControl);
              },
              onFullScreenChanged: (fullScreen) =>
              {
                  if (fullScreen)
                  {
                      mainWindowStyle = mainWindow.WindowStyle;
                      mainWindow.WindowState = WindowState.Maximized;
                      mainWindow.WindowStyle = WindowStyle.None;
                  }
                  else
                  {
                      mainWindow.WindowStyle = mainWindowStyle;
                      mainWindow.WindowState = WindowState.Normal;
                  }
              },
              onThemeChanged: (theme) =>
              {
                  Buzz.ActiveView = BuzzView.MachineView; // ToDo: Machine View issue without changing the ActiveView
                  CreateThemedGUI();
              });

            SizeChanged += (sender, e) =>
            {
                ResizeModernSequenceEditor();
            };

            reBuzzCoreInitialization.StartReBuzzEngineStep5(onOpenFile: fileName =>
            {
                statusWindow = StatusWindow.CreateAsync(fileName, this, buzzPath);
                Buzz.FileEvent += Buzz_FileEvent;
            });

            this.Loaded += (sender, e) =>
            {
                splashScreenWindow.UpdateText("Init views");

                UiUtils.RestoreWindowStateFromRegistry(this, registryEx, "MainWindowWP");
                UiUtils.RestoreWindowStateFromRegistry(debugWindow, registryEx, "DebugWindowWP");

                UiUtils.InitUtils(this);

                Buzz.StartTimer();

                var rd = UiUtils.GetUserControlXAML<ResourceDictionary>(Buzz.Theme.MainWindow.Source, buzzPath);
                this.Style = rd["ThemeWindowStyle"] as Style;
                this.Resources.MergedDictionaries.Add(rd);

                // Machine View
                rd = UiUtils.GetUserControlXAML<ResourceDictionary>(Buzz.Theme.MachineView.Source, buzzPath);
                MachineView = new MachineView(buzzCore, rd);
                MachineView.MachineGraph = song;
                MachineView.Foreground = new SolidColorBrush(Global.Buzz.ThemeColors["MV Text"]);
                borderMachineView.Child = MachineView;

                Buzz.ActiveView = BuzzView.MachineView;

                // Wavetable
                WavetableVM = new WavetableVM();
                WavetableControl = UiUtils.GetUserControlXAML<UserControl>(Buzz.Theme.WavetableView.Source, buzzPath);
                WavetableControl.DataContext = WavetableVM;
                WavetableVM.Wavetable = song.Wavetable;

                // Info view
                InfoView = new InfoView(Buzz);
                rd = UiUtils.GetUserControlXAML<ResourceDictionary>(Buzz.Theme.InfoView.Source, buzzPath);
                InfoView.Resources.MergedDictionaries.Add(rd);
                borderInfo.Child = InfoView;

                // Editor view
                var res = UiUtils.GetBuzzThemeResources(Buzz.Theme.SequenceEditor.Source, buzzPath);
                UseThemeSessionColors(res);
                seqenceEditor = new BuzzGUI.SequenceEditor.SequenceEditor(Buzz, res);
                seqenceEditor.SetVisibility(true);
                EditorView = new EditorView(Buzz, registryEx);
                EditorView.SequenceEditor = seqenceEditor;
                borderEditor.Child = EditorView;

                // Load ToolBar
                ToolBarVM = new ToolBarVM();
                ToolBarVM.Buzz = Buzz;
                ToolBarVM.Song = song;
                ToolBarControl = Common.UiUtils.GetUserControlXAML<UserControl>(Buzz.Theme.ToolBar.Source, buzzPath);
                ToolBarControl.DataContext = ToolBarVM;
                mainGrid.Children.Add(ToolBarControl);
                Grid.SetRow(ToolBarControl, 0);

                // All set up
                BuzzGUIStartup.Startup();
                seqenceEditor.Song = song;
                SequenceEditorMeter.Attach(Buzz, seqenceEditor);       // fork (daw-modes.md S1): its bars mirror the engine's

                CreateSequenceView(song);

                reBuzzCoreInitialization.StartReBuzzEngineStep6();

                song.SongEnd = 16;
                song.LoopEnd = 16;

                // Fork (docs/specs/mcp-server.md §3.1): agents in studio mode (Settings → Agent; Ctrl+Shift+A).
                studioAgent = new Agent.StudioAgent(Buzz, windowsGuiDispatcher, text => SetStatusBarText(text, 1));
                InputBindings.Add(new KeyBinding(new Agent.ActionCommand(() => studioAgent.ShowConnection()), Key.A, ModifierKeys.Control | ModifierKeys.Shift));

                // Fork (docs/specs/mcp-server.md §12.5, U5): the machine menu's "Replace with <successor>...", as machine_replace does it.
                BuzzGUI.Common.MachineSuccessors.For = m => Agent.MachineReplace.Successors(Buzz, m?.DLL).Select(x => x.To).ToList();
                BuzzGUI.Common.MachineSuccessors.Replace = (m, gear) =>
                {
                    if (m is ReBuzz.Core.MachineCore classic && Agent.MachineReplace.FromMenu(Buzz, classic, gear) is string done)
                        SetStatusBarText(done, 0);
                };

                // Fork (docs/specs/mcp-server.md §12.11, U4): the machine menu's "Editor..." and "Double-click Opens Editor".
                BuzzGUI.Common.MachineEditors.Available = m => Agent.MachineFaces.Available(m);
                BuzzGUI.Common.MachineEditors.Open = m => Buzz.UiHost?.OpenEditor(m);
                BuzzGUI.Common.MachineEditors.OnDoubleClick = m => Agent.ManifestEditorSettings.OpensEditor(m?.DLL);
                BuzzGUI.Common.MachineEditors.SetOnDoubleClick = (m, on) => Agent.ManifestEditorSettings.Set(m?.DLL, on);

                // Fork (docs/specs/machine-safety.md §3.2): a machine whose guard tripped shows muted; say why.
                Buzz.MachineDiverged += m => SetStatusBarText($"{m.Name} diverged ({m.DivergedReason}): the guard silenced, stopped and muted it. Unmute it to reset it.", 0);

                UpdateTitle();

                SetStatusBarText("Ready!", 0);
                this.InvalidateMeasure();

                StartAutosave();
                ParseArgs();
                StartUiShots();     // fork (docs/specs/ui-gallery.md): --ui-shots <folder>

                splashScreenWindow.UpdateText("Done");
                splashScreenWindow.CloseWindow();
                Activate();
                Topmost = true;  // important
                Topmost = false; // important
                Focus();         // important

                if (generalSettings.CheckForUpdates && !UiShotsRequested)
                {
                    BuzzGUI.BuzzUpdate.UpdateService.CheckForUpdates(Buzz);
                }
            };

            mainGrid.Loaded += (s, e2) =>
            {
                UiUtils.UpdateDpi(mainGrid);
            };

            this.Closing += (sender, e) =>
            {
                if (!CloseReBuzz())
                { 
                    e.Cancel = true;
                    return;
                }
            };

            this.Closed += (sender, e) =>
            {
                // For some reason, Environment.Exit(0) does not always end the app process.
                Process.GetCurrentProcess().Kill();
                //Environment.Exit(0);
            };

            Buzz.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == "ActiveView")
                {
                    UpdateTitle();
                }
                else if (e.PropertyName == "Modified")
                {
                    UpdateTitle();
                }
                else if (e.PropertyName == "InfoText")
                {
                    InfoView.tbInfo.Text = Buzz.InfoText;
                }
                else if (e.PropertyName == "IsHardDiskRecorderWindowVisible")
                {
                    if (hDRecorderWindow == null && Buzz.IsHardDiskRecorderWindowVisible == true)
                    {
                        hDRecorderWindow = new HDRecorderWindow();
                        var rd = UiUtils.GetUserControlXAML<ResourceDictionary>("MachineView\\MachineView.xaml", buzzPath);
                        hDRecorderWindow.Resources.MergedDictionaries.Add(rd);

                        hDRecorderWindow.MachineGraph = Buzz.SongCore;

                        hDRecorderWindow.Topmost = true;
                        var interop = new WindowInteropHelper(hDRecorderWindow);
                        interop.Owner = Buzz.MachineViewHWND;

                        hDRecorderWindow.Closed += (sender, e) =>
                  {
                      hDRecorderWindow.MachineGraph = null;
                      hDRecorderWindow = null;
                      Buzz.IsHardDiskRecorderWindowVisible = false;
                  };
                    }

                    if (Buzz.IsHardDiskRecorderWindowVisible == true)
                    {
                        hDRecorderWindow.Show();
                    }
                    else if (hDRecorderWindow != null)
                    {
                        hDRecorderWindow.Close();
                    }
                }
                else if (e.PropertyName == "IsPianoKeyboardVisible")
                {
                    if (keyboardWindow == null)
                    {
                        keyboardWindow = new KeyboardWindow(Buzz);

                        if (Global.GeneralSettings.PianoKeyboardTopmost)
                            keyboardWindow.Topmost = true;

                        var interop = new WindowInteropHelper(keyboardWindow);
                        interop.Owner = Buzz.MachineViewHWND;

                        UiUtils.RestoreWindowStateFromRegistry(keyboardWindow, registryEx, "PianoKeyboardWP");
                    }

                    if (Buzz.IsPianoKeyboardVisible == true)
                    {
                        keyboardWindow.Show();
                    }
                    else
                    {   
                        keyboardWindow.Hide();
                    }
                }
            };

            Buzz.Song.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == "Name")
                {
                    UpdateTitle();
                }
            };

            // Fork: clip colours set in the Session View are pattern colours; redraw the Sequence View.
            Core.Session.PatternColors.Changed += p => seqenceEditor?.UpdatePatternBoxes();

            Buzz.BuzzCommandRaised += (cmd) =>
            {
                switch (Buzz.ActiveView)
                {
                    case BuzzView.PatternView:
                        {
                            if (seqenceEditor.IsKeyboardFocused)
                            {
                                if (cmd == BuzzCommand.Copy)
                                {
                                    if (seqenceEditor.CopyCommand.CanExecute(null))
                                        seqenceEditor.CopyCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Cut)
                                {
                                    if (seqenceEditor.CutCommand.CanExecute(null))
                                        seqenceEditor.CutCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Paste)
                                {
                                    if (seqenceEditor.PasteCommand.CanExecute(null))
                                        seqenceEditor.PasteCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Undo)
                                {
                                    if (seqenceEditor.UndoCommand.CanExecute(null))
                                        seqenceEditor.UndoCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Redo)
                                {
                                    if (seqenceEditor.RedoCommand.CanExecute(null))
                                        seqenceEditor.RedoCommand.Execute(null);
                                }
                            }
                            else
                            {
                                // Send commands to editor
                                if (cmd == BuzzCommand.Copy || cmd == BuzzCommand.Cut || cmd == BuzzCommand.Paste ||
                          cmd == BuzzCommand.Undo || cmd == BuzzCommand.Redo)
                                {
                                    Buzz.PatternEditorCommand(cmd);
                                }
                            }

                        }
                        break;
                    case BuzzView.MachineView:
                        {
                            if (cmd == BuzzCommand.Copy)
                            {
                                if (MachineView.CopyCommand.CanExecute(null))
                                    MachineView.CopyCommand.Execute(null);
                            }
                            else if (cmd == BuzzCommand.Cut)
                            {
                                if (MachineView.CutCommand.CanExecute(null))
                                    MachineView.CutCommand.Execute(null);
                            }
                            else if (cmd == BuzzCommand.Paste)
                            {
                                if (MachineView.PasteCommand.CanExecute(null))
                                    MachineView.PasteCommand.Execute(null);
                            }
                            else if (cmd == BuzzCommand.Undo)
                            {
                                if (song.CanUndo)
                                    song.Undo();
                            }
                            else if (cmd == BuzzCommand.Redo)
                            {
                                if (song.CanRedo)
                                    song.Redo();
                            }
                        }
                        break;
                    case BuzzView.SequenceView:
                        {
                            // FIXME
                            if (generalSettings.SequenceView == SequenceEditorType.ModernVertical)
                            {
                                if (cmd == BuzzCommand.Copy)
                                {
                                    if (ModernSequenceEditor.CopyCommand.CanExecute(null))
                                        ModernSequenceEditor.CopyCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Cut)
                                {
                                    if (ModernSequenceEditor.CutCommand.CanExecute(null))
                                        ModernSequenceEditor.CutCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Paste)
                                {
                                    if (ModernSequenceEditor.PasteCommand.CanExecute(null))
                                        ModernSequenceEditor.PasteCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Undo)
                                {
                                    if (ModernSequenceEditor.UndoCommand.CanExecute(null))
                                        ModernSequenceEditor.UndoCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Redo)
                                {
                                    if (ModernSequenceEditor.RedoCommand.CanExecute(null))
                                        ModernSequenceEditor.RedoCommand.Execute(null);
                                }
                            }
                            else
                            {
                                if (cmd == BuzzCommand.Copy)
                                {
                                    if (ModernSequenceEditorHorizontal.CopyCommand.CanExecute(null))
                                        ModernSequenceEditorHorizontal.CopyCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Cut)
                                {
                                    if (ModernSequenceEditorHorizontal.CutCommand.CanExecute(null))
                                        ModernSequenceEditorHorizontal.CutCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Paste)
                                {
                                    if (ModernSequenceEditorHorizontal.PasteCommand.CanExecute(null))
                                        ModernSequenceEditorHorizontal.PasteCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Undo)
                                {
                                    if (ModernSequenceEditorHorizontal.UndoCommand.CanExecute(null))
                                        ModernSequenceEditorHorizontal.UndoCommand.Execute(null);
                                }
                                else if (cmd == BuzzCommand.Redo)
                                {
                                    if (ModernSequenceEditorHorizontal.RedoCommand.CanExecute(null))
                                        ModernSequenceEditorHorizontal.RedoCommand.Execute(null);
                                }
                            }
                        }
                        break;
                    case BuzzView.WaveTableView:
                        {
                            if (cmd == BuzzCommand.Copy)
                            {
                                if (WavetableVM.CopyCommand.CanExecute(null))
                                    WavetableVM.CopyCommand.Execute(null);
                            }
                            else if (cmd == BuzzCommand.Cut)
                            {
                                if (WavetableVM.CutCommand.CanExecute(null))
                                    WavetableVM.CutCommand.Execute(null);
                            }
                            else if (cmd == BuzzCommand.Paste)
                            {
                                if (WavetableVM.PasteCommand.CanExecute(null))
                                    WavetableVM.PasteCommand.Execute(null);
                            }
                            else if (cmd == BuzzCommand.Undo)
                            {
                                if (WavetableVM.UndoCommand.CanExecute(null))
                                    WavetableVM.UndoCommand.Execute(null);
                            }
                            else if (cmd == BuzzCommand.Redo)
                            {
                                if (WavetableVM.RedoCommand.CanExecute(null))
                                    WavetableVM.RedoCommand.Execute(null);
                            }
                        }
                        break;
                    case BuzzView.SongInfoView:
                        {
                            // ToDo
                        }
                        break;
                }
                if (cmd == BuzzCommand.About)
                {
                    AboutWindow aboutWindow = new AboutWindow(buzzCore, "(build " + Buzz.BuildNumber + ")");
                    aboutWindow.Owner = this;
                    aboutWindow.Topmost = true;
                    var rdw = UiUtils.GetUserControlXAML<Window>("ParameterWindowShell.xaml", buzzPath);

                    aboutWindow.Resources.MergedDictionaries.Add(rdw.Resources);
                    aboutWindow.ShowDialog();
                }
                else if (cmd == BuzzCommand.NewFile)
                {
                    SequenceEditor.ViewSettings.TimeSignatureList.Set(0, 16);
                    song.Associations.Clear();
                    seqenceEditor.Song = song;
                    EditorView.Clear();
                }
                else if (cmd == BuzzCommand.OpenFile)
                {
                    EditorView.Clear();
                }
                else if (cmd == BuzzCommand.Exit)
                {
                    if (!CloseReBuzz())
                        return;

                    Process.GetCurrentProcess().Kill();
                    //Environment.Exit(0);
                }
                else if (cmd == BuzzCommand.SessionView)
                {
                    // Fork: Session <-> Sequence, like Live's Session/Arrangement switch.
                    Buzz.ActiveView = Buzz.ActiveView == BuzzView.SessionView ? BuzzView.SequenceView : BuzzView.SessionView;
                }
                else if (cmd == BuzzCommand.SessionWindow)
                {
                    ToggleSessionWindow();
                }
                else if (cmd == BuzzCommand.DebugConsole)
                {
                    debugWindow.Show();
                    debugWindow.BringToTop();
                }
                else if (cmd == BuzzCommand.Preferences)
                {
                    if (preferencesWindow == null)
                    {
                        preferencesWindow = new PreferencesWindow(Buzz, registryEx)
                        {
                            Owner = this
                        };

                        var rd = UiUtils.GetUserControlXAML<ResourceDictionary>("MachineView\\MVResources.xaml", buzzPath);
                        preferencesWindow.Resources.MergedDictionaries.Add(rd);
                        if (preferencesWindow.ShowDialog() == true)
                        {
                            Buzz.MidiInOutEngine.ReleaseAll();
                            Buzz.MidiInOutEngine.CreateMidi2Endpoint();

                            Dictionary<string, Int32> midiIns = new Dictionary<string, Int32>();
                            foreach (var item in preferencesWindow.MidiInControllerCheckboxes)
                            {
                                midiIns[item.Label] = item.Checked ? 1 : 0;
                            }
                            Buzz.MidiInOutEngine.SetMidiInputDevices(midiIns);
                            Buzz.MidiInOutEngine.OpenMidiInDevices();

                            Dictionary<string, Int32> midiOuts = new Dictionary<string, Int32>();
                            foreach (var item in preferencesWindow.MidiOutControllerCheckboxes)
                            {
                                midiOuts[item.Label] = item.Checked ? 1 : 0;
                            }
                            Buzz.MidiInOutEngine.SetMidiOutputDevices(midiOuts);
                            Buzz.MidiInOutEngine.OpenMidiOutDevices();

                            Buzz.MidiControllerAssignments.ClearAll();
                            foreach (PreferencesWindow.ControllerVM item in preferencesWindow.lvControllers.Items)
                            {
                                int controller = -1;
                                if (int.TryParse(item.Controller, out int c))
                                {
                                    controller = c;
                                }

                                int midiNote = -1;
                                try
                                {
                                    midiNote = BuzzNote.ToMIDINote(BuzzNote.Parse(item.Note));
                                }
                                catch { }
                                Buzz.MidiControllerAssignments.Add(item.Name, item.Channel - 1, controller, midiNote, item.Value);
                            }

                            foreach (PreferencesWindow.ControllerVM item in preferencesWindow.lvDAWControllers.Items)
                            {
                                int controller = -1;
                                if (int.TryParse(item.Controller, out int c))
                                {
                                    controller = c;
                                }

                                int midiNote = -1;
                                try
                                {
                                    midiNote = BuzzNote.ToMIDINote(BuzzNote.Parse(item.Note));
                                }
                                catch { }

                                Buzz.MidiControllerAssignments.AddDAWController(item.DAWControllerType, item.Channel - 1, controller, midiNote, item.Value);
                            }

                            Buzz.MIDIControllers = Buzz.MidiControllerAssignments.GetMidiControllerNames().ToReadOnlyCollection();

                            Buzz.AudioEngine.FinalStop();
                            Buzz.AudioEngine.ReleaseAudioDriver();

                            long processorAffinity = preferencesWindow.GetProcessorAffinity();
                            registryEx.Write("ProcessorAffinity", processorAffinity, "Settings");

                            int threadCount = preferencesWindow.cbAudioThreads.SelectedIndex + 1;
                            registryEx.Write("AudioThreads", threadCount, "Settings");

                            int algorithm = preferencesWindow.cbAlgorithms.SelectedIndex;
                            registryEx.Write("WorkAlgorithm", algorithm, "Settings");

                            if (Buzz.SelectedAudioDriver != null)
                            {
                                try
                                {
                                    Buzz.AudioEngine.CreateAudioOut(Buzz.SelectedAudioDriver);
                                    Buzz.AudioEngine.Play();
                                }
                                catch { }
                            }

                            Utils.SetProcessorAffinityMask(registryEx, Buzz.UserMessages, true);
                        }
                        preferencesWindow = null;
                    }
                }
            };

            this.PreviewKeyDown += (sender, e) =>
            {
                // Fork: Space plays and stops the song in every view, Shift+Space in the pattern view plays
                // the pattern from the editor's cursor (TransportKeys): before the views and the focused
                // control, so a focused button doesn't click too; not while typing in a box.
                switch (TransportKeys.For(e.Key, Keyboard.Modifiers, e.IsRepeat, Buzz.ActiveView == BuzzView.PatternView, e.OriginalSource as DependencyObject))
                {
                    case TransportKeys.Command.TogglePlaying:
                        TransportCommands.TogglePlaying(Buzz);
                        e.Handled = true;
                        return;
                    case TransportKeys.Command.PatternPreview:
                        TransportCommands.TogglePatternPreview(Buzz);
                        e.Handled = true;
                        return;
                    case TransportKeys.Command.Repeat:
                        e.Handled = true;
                        return;
                }

                // Fork: the docked Session View gets its keys (arrows, digits, Ctrl+Z...) before the
                // main window's shortcuts; it leaves F-keys and Ctrl+Tab alone.
                if (Buzz.ActiveView == BuzzView.SessionView && sessionDock != null && sessionDock.HandleKeyEvent(e))
                    return;

                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    if (e.Key == Key.F1)
                    {
                        // Help
                        var ps = new ProcessStartInfo(System.IO.Path.Combine(buzzPath, "Help/index.html"))
                        {
                            UseShellExecute = true,
                            Verb = "open"
                        };
                        Process.Start(ps);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.F2)
                    {
                        Buzz.ActiveView = BuzzView.PatternView;
                        e.Handled = true;
                    }
                    else if (e.Key == Key.F3)
                    {
                        Buzz.ActiveView = BuzzView.MachineView;
                        e.Handled = true;
                    }
                    else if (e.Key == Key.F4)
                    {
                        Buzz.ActiveView = BuzzView.SequenceView;
                        e.Handled = true;
                    }
                    else if (e.Key == Key.F5)
                    {
                        // Play
                        Buzz.Playing = true;
                        e.Handled = true;
                    }
                    else if (e.Key == Key.F6)
                    {
                        // Play from cursur pos
                        seqenceEditor.PlayCursor();
                        e.Handled = true;
                    }
                    else if (e.Key == Key.F7)
                    {
                        // Record
                        Buzz.Recording = !Buzz.Recording;
                        e.Handled = true;
                    }
                    else if (e.Key == Key.F8)
                    {
                        // Stop
                        Buzz.Playing = false;
                        e.Handled = true;
                    }
                    else if (e.Key == Key.F9)
                    {
                        // Wavetable
                        Buzz.ActiveView = BuzzView.WaveTableView;
                        e.Handled = true;
                    }
                    else if (e.SystemKey == Key.F10)
                    {
                        // Info
                        Buzz.ActiveView = BuzzView.SongInfoView;
                        e.Handled = true;
                    }
                    else if (e.Key == Key.F11)
                    {
                        // Dialogs
                        foreach (var m in song.Machines)
                        {
                            var mac = m as MachineCore;
                            var pw = WpfMachineUiHost.ParameterWindowOf(mac);
                            if (pw != null)
                            {
                                if (pw.Visibility == Visibility.Visible)
                                {
                                    pw.Visibility = Visibility.Collapsed;
                                }
                                else
                                {
                                    pw.Visibility = Visibility.Visible;
                                }
                            }

                            var mgw = WpfMachineUiHost.GuiWindowOf(mac);
                            if (mgw != null)
                            {
                                if (mgw.Visibility == Visibility.Visible)
                                {
                                    mgw.Visibility = Visibility.Collapsed;
                                }
                                else
                                {
                                    mgw.Visibility = Visibility.Visible;
                                }
                            }
                        }

                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            Focus();
                        });
                        e.Handled = true;
                    }
                    else if (e.Key == Key.F12)
                    {
                        // Reset Audio
                        buzzCore.AudioEngine.Reset();
                        e.Handled = true;
                    }
                    // Fork: Space and Shift+Space are handled above (TransportKeys).
                }

                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    if (Buzz.ActiveView != BuzzView.SongInfoView && Buzz.ActiveView != BuzzView.WaveTableView)
                    {
                        if (e.Key == Key.C)
                        {
                            Buzz.ExecuteCommand(BuzzCommand.Copy);
                            e.Handled = true;
                        }
                        else if (e.Key == Key.X)
                        {
                            Buzz.ExecuteCommand(BuzzCommand.Cut);
                            e.Handled = true;
                        }
                        else if (e.Key == Key.V)
                        {
                            Buzz.ExecuteCommand(BuzzCommand.Paste);
                            e.Handled = true;
                        }
                        else if (e.Key == Key.Z)
                        {
                            Buzz.ExecuteCommand(BuzzCommand.Undo);
                            e.Handled = true;
                        }
                    }
                    
                    if (e.Key == Key.Y)
                    {
                        Buzz.ExecuteCommand(BuzzCommand.Redo);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.S)
                    {
                        Buzz.ExecuteCommand(BuzzCommand.SaveFile);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.N)
                    {
                        Buzz.ExecuteCommand(BuzzCommand.NewFile);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.O)
                    {
                        Buzz.ExecuteCommand(BuzzCommand.OpenFile);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Tab)
                    {
                        // Fork: Ctrl+Tab switches to the Session View, as in Live.
                        Buzz.ExecuteCommand(BuzzCommand.SessionView);
                        e.Handled = true;
                    }
                }

                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    if (e.Key == Key.S)
                    {
                        Buzz.ExecuteCommand(BuzzCommand.SaveFileAs);
                        e.Handled = true;
                    }
                }
            };
        }

        /// <summary>
        /// Fork: shows the Session View window, or returns to the main window when the Session
        /// View is already in front (docs/specs/session-view.md).
        /// </summary>
        void ToggleSessionWindow()
        {
            if (sessionWindow == null)
            {
                sessionWindow = new AppViews.Session.SessionWindow(Buzz) { Owner = this };
            }
            else if (sessionWindow.IsVisible && sessionWindow.IsActive)
            {
                Activate();
                return;
            }

            sessionWindow.Show();
            sessionWindow.Activate();
            sessionWindow.View.Focus();
        }

        /// <summary>
        /// Fork: the Session View colours clips, tracks and scenes with the theme's pattern
        /// colours, the same list (and, for clips, the same per-pattern choice) as the Sequence View.
        /// </summary>
        static void UseThemeSessionColors(ResourceDictionary res)
        {
            try
            {
                if (res?["PatternColors"] is NamedColor[] colors)
                    AppViews.Session.SessionPalette.UseThemeColors(colors.Select(c => (c.Name, c.Color)));
            }
            catch (Exception)
            {
                // Keep the default colours if the theme has none or a broken list.
            }
        }

        private bool CloseReBuzz()
        {
            if (Buzz.Modified)
            {
                var song = Buzz.Song;
                var result = UiUtils.MessageBox("Save changes to " + (song.SongName == null ? "Untitled" : song.SongName), "ReBuzz", MessageBoxButton.YesNoCancel);
                if (result == MessageBoxResult.Yes)
                {
                    Buzz.SaveSongFile(song.SongName);
                }
                else if (result == MessageBoxResult.Cancel)
                {   
                    return false;
                }
            }
            UiUtils.SaveWindowStateToRegistry(this, registryEx, "MainWindowWP");
            UiUtils.SaveWindowStateToRegistry(debugWindow, registryEx, "DebugWindowWP");
            UiUtils.SaveWindowStateToRegistry(keyboardWindow, registryEx, "PianoKeyboardWP");

            debugWindow.CloseWindow();

            Buzz.Playing = false;

            studioAgent?.Dispose();   // fork: agent server
            
            reBuzzCoreInitialization.ShutDownReBuzzEngine();

            ShutDownSequenceEditor(seqenceEditor);

            ToolBarVM.Song = null;
            ToolBarVM.Buzz = null;

            Buzz.PropertyChanged -= Buzz_PropertyChanged;
            Global.GeneralSettings.PropertyChanged -= GeneralSettings_PropertyChanged;

            Buzz.Release();

            return true;
        }

        private static void ShutDownSequenceEditor(SequenceEditor sequenceEditor)
        {
            sequenceEditor.Song = null;
            sequenceEditor.Release();
        }

        private void CreateSequenceView(ISong song)
        {
            if (ModernSequenceEditor != null)
            {
                ModernSequenceEditor.SizeChanged -= ModernSequenceEditorHorizontal_SizeChanged;
                ModernSequenceEditor.Song = null;
                ModernSequenceEditor.Release();
            }
            if (ModernSequenceEditorHorizontal != null)
            {
                ModernSequenceEditorHorizontal.SizeChanged -= ModernSequenceEditorHorizontal_SizeChanged;
                ModernSequenceEditorHorizontal.Song = null;
                ModernSequenceEditorHorizontal.Release();
            }

            if (Global.GeneralSettings.SequenceView == SequenceEditorType.ModernVertical)
            {
                ModernSequenceEditor = new WDE.ModernSequenceEditor.SequenceEditor(Buzz, UiUtils.GetBuzzThemeResources(Buzz.Theme.SequenceEditor.Source, buzzPath), true);
                borderSequenceEditor.Child = ModernSequenceEditor;
                ModernSequenceEditor.SizeChanged += ModernSequenceEditorHorizontal_SizeChanged;

                ModernSequenceEditor.Song = song;
                ModernSequenceEditor.SetVisibility(true);
                ModernSequenceEditor.MinHeight = 400;
                ModernSequenceEditor.MinWidth = 600;
                ModernSequenceEditor.Foreground = new SolidColorBrush(Global.Buzz.ThemeColors["SE Text"]);
            }
            else
            {
                ModernSequenceEditorHorizontal = new WDE.ModernSequenceEditorHorizontal.SequenceEditor(Buzz, UiUtils.GetBuzzThemeResources(Buzz.Theme.SequenceEditor.Source, buzzPath), true);
                borderSequenceEditor.Child = ModernSequenceEditorHorizontal;
                ModernSequenceEditorHorizontal.SizeChanged += ModernSequenceEditorHorizontal_SizeChanged;

                ModernSequenceEditorHorizontal.Song = song;
                ModernSequenceEditorHorizontal.SetVisibility(true);
                ModernSequenceEditorHorizontal.MinHeight = 400;
                ModernSequenceEditorHorizontal.MinWidth = 600;
                ModernSequenceEditorHorizontal.Foreground = new SolidColorBrush(Global.Buzz.ThemeColors["SE Text"]);
            }
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            UiUtils.UpdateDpi(mainGrid);
        }

        private void ModernSequenceEditorHorizontal_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ResizeModernSequenceEditor();
        }

        System.Windows.Threading.DispatcherTimer autosaveTimer;

        /// <summary>
        /// Fork (0.4.1): offers to recover an autosave left by a session that didn't close
        /// normally, then autosaves unsaved changes every couple of minutes (SongAutosave).
        /// </summary>
        private void StartAutosave()
        {
            if (UiShotsRequested) return;   // fork (ui-gallery.md): no recovery question, no autosaves while the gallery draws
            try
            {
                var leftovers = SongAutosave.FindLeftovers();
                if (leftovers.Count > 0)
                {
                    var newest = leftovers[0];
                    string song = string.IsNullOrEmpty(newest.Song) ? "an untitled song" : "'" + Path.GetFileName(newest.Song) + "'";
                    var answer = MessageBox.Show(this,
                        $"ReBuzz didn't close normally last time. There are unsaved changes to {song}, autosaved at {newest.Saved:g}.\n\n" +
                        "Yes: open them (then save to keep them)\nNo: delete them\nCancel: keep them for next time",
                        "Recover unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Yes)
                    {
                        Buzz.Autosave.Recover(newest);
                        foreach (var older in leftovers.Skip(1))
                            SongAutosave.Delete(older.Path);
                    }
                    else if (answer == MessageBoxResult.No)
                    {
                        foreach (var leftover in leftovers)
                            SongAutosave.Delete(leftover.Path);
                    }
                }
            }
            catch (Exception e)
            {
                Buzz.DCWriteLine("Autosave recovery: " + e.Message, DCLogLevel.Warning);
            }

            autosaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            autosaveTimer.Tick += (s, e) => Buzz.Autosave.Tick(DateTime.Now);
            autosaveTimer.Start();
        }

        private void ParseArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (File.Exists(args[i]))
                {
                    // Open after all has been initialized
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        Buzz.OpenSongFile(args[i]);
                    });
                    break;
                }
            }
        }

        private void GeneralSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "WPFIdealFontMetrics")
            {
                if (Global.GeneralSettings.WPFIdealFontMetrics)
                {
                    TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
                }
                else
                {
                    TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
                }
            }
            else if (e.PropertyName == "SequenceView")
            {
                CreateSequenceView(Buzz.Song);
            }
            else if (e.PropertyName == "DpiScaling")
            {
                UiUtils.UpdateDpi(mainGrid);
            }
        }

        private void Buzz_FileEvent(FileEventType type, string text, object o)
        {
            if (type == FileEventType.StatusUpdate)
            {
                statusWindow?.UpdateText(text);
                UiUtils.AllowUIToUpdate();
            }
            else if (type == FileEventType.Close)
            {
                var machines = (IEnumerable<MachineCore>)o;

                if (machines != null)
                {
                    foreach (MachineCore machine in machines)
                    {
                        var mt = MachineView.Machines.FirstOrDefault(mc => mc.Machine == machine);
                        if (mt != null)
                        {
                            mt.IsSelected = true;
                        }
                    }
                }
                statusWindow?.CloseWindow();
                Buzz.FileEvent -= Buzz_FileEvent;
            }
        }

        private void ResizeModernSequenceEditor()
        {
            if (ModernSequenceEditor != null)
            {
                double h = mainGrid.ActualHeight - mainGrid.RowDefinitions[0].ActualHeight - mainGrid.RowDefinitions[2].ActualHeight;
                if (h != ModernSequenceEditor.ActualHeight)
                    ModernSequenceEditor.Height = h;
            }
            else if (ModernSequenceEditorHorizontal != null)
            {
                double h = mainGrid.ActualHeight - mainGrid.RowDefinitions[0].ActualHeight - mainGrid.RowDefinitions[2].ActualHeight;
                if (h != ModernSequenceEditorHorizontal.ActualHeight)
                    ModernSequenceEditorHorizontal.Height = h;
            }
        }

        private void CreateThemedGUI()
        {
            if (EditorView != null)
            {
                EditorView.SetEditorControl(null);
            }

            Buzz.LoadThemeColours();
            var song = Buzz.Song;

            // Main Window
            var rd = UiUtils.GetUserControlXAML<ResourceDictionary>(Buzz.Theme.MainWindow.Source, buzzPath);
            this.Style = rd["ThemeWindowStyle"] as Style;
            this.Resources.MergedDictionaries.Clear();
            this.Resources.MergedDictionaries.Add(rd);

            // Machine View
            borderMachineView.Child = null;
            if (MachineView != null)
            {
                MachineView.MachineGraph = null;
                MachineView.Release();

            }
            rd = UiUtils.GetUserControlXAML<ResourceDictionary>(Buzz.Theme.MachineView.Source, buzzPath);
            MachineView = new MachineView(Buzz, rd);
            MachineView.MachineGraph = song;
            MachineView.Foreground = new SolidColorBrush(Global.Buzz.ThemeColors["MV Text"]);
            borderMachineView.Child = MachineView;

            MachineView.Loaded += (s, e) =>
            {
                // Hack to force MachineView to update
                MachineView.Visibility = Visibility.Visible;
                this.Width--;
                this.Width++;
            };

            // Wavetable
            if (WavetableVM != null)
            {
                borderWavetable.Child = null;
                WavetableVM.Wavetable = null;
            }
            if (WavetableControl != null)
            {
                WavetableControl.DataContext = null;
            }
            WavetableVM = new WavetableVM();
            WavetableControl = UiUtils.GetUserControlXAML<UserControl>(Buzz.Theme.WavetableView.Source, buzzPath);
            WavetableControl.DataContext = WavetableVM;
            WavetableVM.Wavetable = song.Wavetable;

            // Info view
            InfoView = new InfoView(Buzz);
            rd = UiUtils.GetUserControlXAML<ResourceDictionary>(Buzz.Theme.InfoView.Source, buzzPath);
            InfoView.Resources.MergedDictionaries.Add(rd);
            borderInfo.Child = InfoView;

            // Editor view
            if (seqenceEditor != null)
            {
                seqenceEditor.Song = null;
                seqenceEditor.Release();
            }
            var res = UiUtils.GetBuzzThemeResources(Buzz.Theme.SequenceEditor.Source, buzzPath);
            UseThemeSessionColors(res);
            seqenceEditor = new SequenceEditor(Buzz, res);
            seqenceEditor.SetVisibility(true);
            EditorView.SequenceEditor = seqenceEditor;
            borderEditor.Child = EditorView;

            // Load ToolBar
            if (ToolBarControl != null)
            {
                mainGrid.Children.Remove(ToolBarControl);

                // ToDo: something is not released?
                //ToolBarVM.Buzz = null;
                //ToolBarVM.Song = null;
            }

            ToolBarVM = new ToolBarVM();
            ToolBarVM.Buzz = Buzz;
            ToolBarVM.Song = song;
            ToolBarControl = UiUtils.GetUserControlXAML<UserControl>(Buzz.Theme.ToolBar.Source, buzzPath);
            ToolBarControl.DataContext = ToolBarVM;
            mainGrid.Children.Add(ToolBarControl);
            Grid.SetRow(ToolBarControl, 0);

            // All set up
            seqenceEditor.Song = song;
            SequenceEditorMeter.Attach(Buzz, seqenceEditor);           // fork (daw-modes.md S1)
            CreateSequenceView(song);

            UpdateTitle();
            SetStatusBarText("Ready!", 0);
        }

        private void Buzz_PatternEditorActivated()
        {
            if (Buzz.ActiveView != BuzzView.PatternView)
            {
                Buzz.ActiveView = BuzzView.PatternView;
            }

            EditorView.PatternEditorActivated();
        }

        private void Buzz_SequenceEditorActivated()
        {
            seqenceEditor.Focus();
        }

        private void UpdateTitle()
        {
            string currentView = "[Machines]";
            if (Buzz.ActiveView == BuzzView.SongInfoView)
                currentView = "[Info]";
            else if (Buzz.ActiveView == BuzzView.PatternView)
                currentView = "[Patterns]";
            else if (Buzz.ActiveView == BuzzView.SequenceView)
                currentView = "[Sequences]";
            else if (Buzz.ActiveView == BuzzView.WaveTableView)
                currentView = "[Wavetable]";
            else if (Buzz.ActiveView == BuzzView.SessionView)
                currentView = "[Session]";

            this.Title = (Buzz.SongCore.SongName == null ? "Untitled" : Buzz.SongCore.SongName) + (Buzz.Modified == true ? "*" : "") + " | " + "ReBuzz Digital Audio Workstation | " + currentView;
        }

        internal void SetStatusBarText(string text, int item)
        {
            if (item == 0)
            {
                StatusBarItem1 = text;
            }
            else
            {
                StatusBarItem2 = text;
            }
        }

        private void Buzz_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsCPUMonitorWindowVisible")
            {
                MachineView.IsCPUMonitorVisible = Buzz.IsCPUMonitorWindowVisible;
            }
            else if (e.PropertyName == "ActiveView")
            {
                ChangeActiveview();
            }
        }

        private void ChangeActiveview()
        {
            borderEditor.Visibility = borderMachineView.Visibility = borderSequenceEditor.Visibility =
                borderWavetable.Visibility = borderInfo.Visibility = borderSession.Visibility = Visibility.Collapsed;

            foreach (var rd in contentGrid.RowDefinitions)
            {
                rd.Height = GridLength.Auto;
            }

            switch (Buzz.ActiveView)
            {
                case BuzzView.PatternView:
                    borderEditor.Visibility = Visibility.Visible;
                    contentGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
                    borderEditor.Focus();

                    // Force draw to that editor can get keyboard focus
                    UiUtils.Refresh(this);

                    if (Buzz.NewSequenceEditorActivate)
                    {
                        EditorView.FocusSequenceEditor();
                    }
                    else
                    {
                        EditorView.FocusEditor();
                    }

                    break;
                case BuzzView.MachineView:
                    borderMachineView.Visibility = Visibility.Visible;
                    // Main Grid behaves strange if row height is not "star"
                    contentGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                    borderMachineView.Focus();
                    MachineView.Focus();
                    break;
                case BuzzView.SequenceView:
                    borderSequenceEditor.Visibility = Visibility.Visible;
                    borderSequenceEditor.Focus();
                    if (Global.GeneralSettings.SequenceView == SequenceEditorType.ModernVertical)
                        ModernSequenceEditor.Focus();
                    else
                        ModernSequenceEditorHorizontal.Focus();
                    break;
                case BuzzView.WaveTableView:
                    // WT is a special case...
                    borderWavetable.Child = WavetableControl;
                    contentGrid.RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);
                    borderWavetable.Visibility = Visibility.Visible;
                    borderWavetable.Focus();
                    WavetableControl.Focus();
                    break;
                case BuzzView.SongInfoView:
                    contentGrid.RowDefinitions[4].Height = new GridLength(1, GridUnitType.Star);
                    borderInfo.Visibility = Visibility.Visible;
                    borderInfo.Focus();
                    InfoView.Focus();
                    break;
                case BuzzView.SessionView:
                    // Fork: docked Session View (docs/specs/session-view.md 0.3).
                    if (sessionDock == null)
                    {
                        sessionDock = new AppViews.Session.SessionView(Buzz);
                        borderSession.Child = sessionDock;
                    }
                    contentGrid.RowDefinitions[5].Height = new GridLength(1, GridUnitType.Star);
                    borderSession.Visibility = Visibility.Visible;
                    sessionDock.Focus();
                    break;
            }
            UpdateTitle();
        }

        internal UserControl GetActiveView()
        {
            switch (Buzz.ActiveView)
            {
                case BuzzView.PatternView:
                    return seqenceEditor;
                case BuzzView.MachineView:
                    return MachineView;
                case BuzzView.SequenceView:
                    if (Global.GeneralSettings.SequenceView == SequenceEditorType.ModernVertical)
                        return ModernSequenceEditor;
                    else
                        return ModernSequenceEditorHorizontal;
                case BuzzView.WaveTableView:
                    return WavetableControl;
                case BuzzView.SongInfoView:
                    return InfoView;
                case BuzzView.SessionView:
                    return sessionDock;
                default:
                    return null;
            }
        }

        internal IActionStack GetActiveActionStack()
        {
            var view = GetActiveView();
            if (view != null)
                return view as IActionStack;
            else
                return null;
        }
    }
}