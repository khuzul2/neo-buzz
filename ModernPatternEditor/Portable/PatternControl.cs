// Fork (docs/specs/studio-mvp.md S6, decision 3.3): the WPF PatternControl's logic without WPF: the
// cursor, the selection, keyboard entry and the edit commands, ported from PatternControl.xaml.cs and
// the key handling of PatternEditor.xaml.cs (the WPF files keep their own code until P4). A view (the
// Avalonia one in the studio) draws what this says and gives it keys as EditorKey; the shared sources
// call it as they call the WPF control (InvalidateVisual, MoveCursorDelta, the commands).

using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.Actions.PatternActions;
using BuzzGUI.Interfaces;
using System.ComponentModel;
using System.Windows.Input;
using WDE.ModernPatternEditor.Actions;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor
{
    /// <summary>The keys the editor knows, named as WPF's Key (the view maps its toolkit's keys).</summary>
    public enum EditorKey
    {
        None, Up, Down, Left, Right, PageUp, PageDown, Home, End, Tab, Delete, Insert, Return, Back, Space, Escape,
        D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        NumPad0, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
        Add, Subtract, Multiply, Divide, OemPeriod, F1, Other
    }

    /// <summary>Modifier keys as the editor reads them (the view gives macOS's Command as Control).</summary>
    [Flags]
    public enum EditorModifiers { None = 0, Control = 1, Shift = 2, Alt = 4 }

    public class PatternControl
    {
        const int CursorStep = 1;
        public static readonly int BUZZ_TICKS_PER_BEAT = 4;

        /// <summary>The view's zoom (WPF: the editor's "Pattern Zoom" slider).</summary>
        public static double Scale { get; set; } = 1.0;

        static readonly EditorKey[] HexKeys = { EditorKey.D0, EditorKey.D1, EditorKey.D2, EditorKey.D3, EditorKey.D4, EditorKey.D5, EditorKey.D6, EditorKey.D7, EditorKey.D8, EditorKey.D9, EditorKey.A, EditorKey.B, EditorKey.C, EditorKey.D, EditorKey.E, EditorKey.F };
        static readonly EditorKey[] DecKeys = { EditorKey.D0, EditorKey.D1, EditorKey.D2, EditorKey.D3, EditorKey.D4, EditorKey.D5, EditorKey.D6, EditorKey.D7, EditorKey.D8, EditorKey.D9 };

        public PatternEditor Editor { get; }

        /// <summary>The view has the keyboard focus (MIDI edit writes only then, as in WPF).</summary>
        public bool IsKeyboardFocused { get; set; }

        /// <summary>A pattern is shown (or none), or its columns changed: the view lays out again.</summary>
        public event Action PatternShown;

        /// <summary>The cursor moved: the view redraws it and brings it into view.</summary>
        public event Action CursorMoved;

        public event Action SelectionChanged;

        /// <summary>The pattern's values changed: the view redraws.</summary>
        public event Action Invalidated;

        /// <summary>The editor asked for the pattern properties (Ctrl+Backspace, the context menu).</summary>
        public event Action PropertiesRequested;

        /// <summary>The editor asked to toggle its help (F1), or to open a toolbar list (Alt+B, R, Y).</summary>
        public event Action<string> ViewRequest;

        public ICommand PropertiesCommand { get; private set; }
        public ICommand CutCommand { get; private set; }
        public ICommand CopyCommand { get; private set; }
        public ICommand PasteCommand { get; private set; }
        public ICommand UndoCommand { get; private set; }
        public ICommand RedoCommand { get; private set; }
        public ICommand SelectAllCommand { get; private set; }
        public ICommand UnselectCommand { get; private set; }
        public ICommand RandomizeCommand { get; private set; }
        public ICommand DecreaseCommand { get; private set; }
        public ICommand IncreaseCommand { get; private set; }
        public ICommand RotateCommandDown { get; private set; }
        public ICommand RotateCommandUp { get; private set; }
        public ICommand WriteStateCommand { get; private set; }
        public ICommand InterpolateLinearCommand { get; private set; }
        public ICommand InterpolateExpCommand { get; private set; }

        PatternVM pattern;
        MachineVM machineVM;

        /// <summary>The pattern shown (null: none).</summary>
        public PatternVM CurrentPattern => pattern;

        internal PatternVM Pattern
        {
            get { return pattern; }
            set
            {
                if (pattern != null)
                    pattern.PropertyChanged -= pattern_PropertyChanged;

                pattern = value;

                if (pattern != null)
                    pattern.PropertyChanged += pattern_PropertyChanged;

                PatternShown?.Invoke();
                if (pattern != null && pattern.ColumnSets != null && pattern.ColumnSets.Count > 0)
                    UpdateCursor();
                SelectionChanged?.Invoke();
            }
        }

        public PatternControl(PatternEditor editor)
        {
            Editor = editor;
            // WPF binds the control's DataContext to SelectedMachine.SelectedPattern; this follows it.
            Editor.PropertyChanged += Editor_PropertyChanged;
            CreateCommands();
        }

        void Editor_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "SelectedMachine") return;
            if (machineVM != null) machineVM.PropertyChanged -= MachineVM_PropertyChanged;
            machineVM = Editor.SelectedMachine;
            if (machineVM != null) machineVM.PropertyChanged += MachineVM_PropertyChanged;
            Pattern = machineVM?.SelectedPattern;
        }

        void MachineVM_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "SelectedPattern" && machineVM.SelectedPattern != pattern)
                Pattern = machineVM.SelectedPattern;
        }

        void pattern_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "ColumnSets":
                    PatternShown?.Invoke();
                    break;

                case "BeatCount":
                case "CursorPosition":
                    UpdateCursor();
                    break;

                case "Selection":
                    SelectionChanged?.Invoke();
                    break;
            }
        }

        bool HasColumns => pattern != null && pattern.ColumnSets != null && pattern.ColumnSets.Count > 0;

        public void InvalidateVisual() => Invalidated?.Invoke();

        public void Release()
        {
            Editor.PropertyChanged -= Editor_PropertyChanged;
            if (machineVM != null) machineVM.PropertyChanged -= MachineVM_PropertyChanged;
            if (pattern != null) pattern.PropertyChanged -= pattern_PropertyChanged;
            machineVM = null;
            pattern = null;
        }

        void CreateCommands()
        {
            DecreaseCommand = new SimpleCommand { CanExecuteDelegate = x => HasColumns, ExecuteDelegate = x => ShiftValues(-1) };
            IncreaseCommand = new SimpleCommand { CanExecuteDelegate = x => HasColumns, ExecuteDelegate = x => ShiftValues(1) };
            WriteStateCommand = new SimpleCommand { CanExecuteDelegate = x => HasColumns, ExecuteDelegate = x => WriteState() };
            InterpolateLinearCommand = new SimpleCommand { CanExecuteDelegate = x => HasColumns, ExecuteDelegate = x => InterpolateSelection(InterpolationMethod.Linear) };
            InterpolateExpCommand = new SimpleCommand { CanExecuteDelegate = x => HasColumns, ExecuteDelegate = x => InterpolateSelection(InterpolationMethod.ExpIntp) };
            RotateCommandDown = new SimpleCommand { CanExecuteDelegate = x => HasColumns, ExecuteDelegate = x => RollSelection(true) };
            // WPF's "Rotate Forward" runs the same roll as "Rotate Backward" (as it is there).
            RotateCommandUp = new SimpleCommand { CanExecuteDelegate = x => HasColumns, ExecuteDelegate = x => RollSelection(true) };
            RandomizeCommand = new SimpleCommand { CanExecuteDelegate = x => HasColumns, ExecuteDelegate = x => RamdomizeSelection() };
            PropertiesCommand = new SimpleCommand { CanExecuteDelegate = x => pattern != null, ExecuteDelegate = x => PropertiesRequested?.Invoke() };

            CutCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => pattern != null && pattern.Selection.Active,
                ExecuteDelegate = x =>
                {
                    if (RefuseInNoteLanes("Cut")) return;
                    Selection selection = pattern.Selection;
                    if (!selection.Active)
                        selection = Selection.Start(pattern.CursorPosition).SetEnd(pattern.CursorPosition);
                    DoAction(new CutOrCopyPatternEventsAction(Editor.MPEPatternsDB.GetMPEPattern(pattern.Pattern), selection, PatternEditor.clipboard, true));
                    InvalidateVisual();
                }
            };

            CopyCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => pattern != null && pattern.Selection.Active,
                ExecuteDelegate = x =>
                {
                    if (RefuseInNoteLanes("Copy")) return;
                    Selection selection = pattern.Selection;
                    if (!selection.Active)
                        selection = Selection.Start(pattern.CursorPosition).SetEnd(pattern.CursorPosition);
                    DoAction(new CutOrCopyPatternEventsAction(Editor.MPEPatternsDB.GetMPEPattern(pattern.Pattern), selection, PatternEditor.clipboard, false));
                }
            };

            // WPF's paste command says it can't run (so its own Ctrl+V binding does nothing, and only
            // ReBuzz's Edit menu pastes); here it can whenever the clipboard holds something.
            PasteCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => HasColumns && PatternEditor.clipboard.ContainsData,
                ExecuteDelegate = x =>
                {
                    if (RefuseInNoteLanes("Paste")) return;
                    Selection selection = pattern.Selection;
                    if (!selection.Active)
                        selection = Selection.Start(pattern.CursorPosition).SetEnd(pattern.CursorPosition);
                    DoAction(new PastePatternEventsAction(Editor.MPEPatternsDB.GetMPEPattern(pattern.Pattern), selection, PatternEditor.clipboard));
                    InvalidateVisual();
                }
            };

            UndoCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => Editor.EditContext != null && Editor.EditContext.ActionStack.CanUndo,
                ExecuteDelegate = x => { Editor.EditContext.ActionStack.Undo(); InvalidateVisual(); }
            };

            RedoCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => Editor.EditContext != null && Editor.EditContext.ActionStack.CanRedo,
                ExecuteDelegate = x => { Editor.EditContext.ActionStack.Redo(); InvalidateVisual(); }
            };

            SelectAllCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => true,
                ExecuteDelegate = x =>
                {
                    if (!HasColumns)
                        return;

                    if (!pattern.Selection.Equals(Selection.ColumnSet(pattern.CursorPosition)))
                        pattern.Selection = Selection.ColumnSet(pattern.CursorPosition);
                    else
                        pattern.Selection = Selection.All(pattern);
                }
            };

            UnselectCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => pattern != null && pattern.Selection.Active,
                ExecuteDelegate = x => pattern.Selection = Selection.Empty(pattern)
            };
        }

        static void Run(ICommand command)
        {
            if (command.CanExecute(null))
                command.Execute(null);
        }

        // ------------------------------------------------------------------ keys

        /// <summary>
        /// A key pressed in the editor: the WPF control's PreviewKeyDown, its key bindings, then the
        /// editor's own KeyDown. <paramref name="scancode"/> is the key's PC scan code (the keyboard
        /// mappings name keys by position). True when the key was used.
        /// </summary>
        public bool KeyDown(EditorKey key, EditorModifiers modifiers, int scancode)
        {
            bool handled = PreviewKeyDown(key, modifiers, scancode);
            if (!handled)
                handled = KeyBindings(key, modifiers);
            if (!handled)
                handled = EditorKeyDown(key, modifiers);
            return handled;
        }

        bool PreviewKeyDown(EditorKey key, EditorModifiers modifiers, int scancode)
        {
            bool handled = false;
            if (modifiers == EditorModifiers.Control)
            {
                switch (key)
                {
                    case EditorKey.Left: handled = true; break;
                    case EditorKey.Right: handled = true; break;
                }
            }
            if (!HasColumns)
                return handled;

            if (modifiers == EditorModifiers.None)
            {
                switch (key)
                {
                    case EditorKey.Down: MoveCursorDelta(0, CursorStep); handled = true; break;
                    case EditorKey.Up: MoveCursorDelta(0, -CursorStep); handled = true; break;
                    case EditorKey.Right: MoveCursorDelta(1, 0); handled = true; break;
                    case EditorKey.Left: MoveCursorDelta(-1, 0); handled = true; break;
                    case EditorKey.PageDown: MoveCursorDelta(0, 16); handled = true; break;
                    case EditorKey.PageUp: MoveCursorDelta(0, -16); handled = true; break;
                    case EditorKey.Home: Home(); handled = true; break;
                    case EditorKey.End: End(); handled = true; break;
                    case EditorKey.Tab: Tab(); handled = true; break;
                    case EditorKey.Delete: InsertOrDelete(true, true); handled = true; break;
                    case EditorKey.Insert: InsertOrDelete(true, false); handled = true; break;
                }

                var col = pattern.GetColumn(pattern.CursorPosition);
                if (col is ParameterColumn)
                {
                    IEnumerable<BuzzAction> action = null;
                    bool movecursor = false;
                    bool play = false;

                    var pc = (ParameterColumn)col;

                    if (pc.Type == ColumnRenderer.ColumnType.Note)
                    {
                        if (pattern.CursorPosition.Index == 0)
                        {
                            var map = Editor.SelectedKeyboardMapping ?? KeyboardMappingFile.Default.DefaultMapping;
                            int i = scancode > 0 ? map.GetNoteByScancode(scancode) : -1;

                            if (i >= 0)
                            {
                                action = EditNote(pc, BuzzNote.FromMIDINote(Math.Min(i + Editor.SelectedRootNote + 12 * Editor.SelectedMachine.Machine.BaseOctave, BuzzNote.ToMIDINote(BuzzNote.Max))));
                                movecursor = true;
                                play = true;
                            }
                            else if (scancode > 0 && map.IsOffScancode(scancode))
                            {
                                action = EditNote(pc, BuzzNote.Off);
                                movecursor = true;
                                play = true;
                            }
                            else if (scancode > 0 && map.IsPlayScancode(scancode))
                            {
                                PlayColumnSet(pattern.CursorPosition);
                                movecursor = true;
                            }
                            else if (scancode > 0 && map.IsPlayAllScancode(scancode))
                            {
                                PlayAllColumnSets(pattern.CursorPosition);
                                movecursor = true;
                            }
                        }
                        else
                        {
                            int i = Array.IndexOf(DecKeys, key);
                            if (i >= 0)
                            {
                                action = pc.EditDigit(pattern.CursorPosition, i);
                                movecursor = true;
                            }
                        }
                    }
                    else if (col.Type == ColumnRenderer.ColumnType.HexValue)
                    {
                        // Fork (ux-overhaul.md P3c): a decimal column takes the digits 0-9 only.
                        int i = Array.IndexOf(pc.IsDecimal ? DecKeys : HexKeys, key);
                        if (i >= 0)
                        {
                            action = pc.EditDigit(pattern.CursorPosition, i);
                            movecursor = true;
                        }
                    }

                    if (key == EditorKey.OemPeriod)
                    {
                        if (pc.Type == ColumnRenderer.ColumnType.Note)
                            action = EditNote(pc, -1);
                        else
                            action = pc.EditDigit(pattern.CursorPosition, -1);

                        movecursor = true;
                    }

                    if (action != null)
                    {
                        var oldCursorPos = pattern.CursorPosition;

                        if (movecursor)
                            DoEditActions(action, key == EditorKey.OemPeriod);
                        else
                            DoActions(action);

                        if (Editor.PlayNotes && play)
                            PlayColumnSet(oldCursorPos);

                        handled = true;
                    }
                    else if (movecursor)
                    {
                        MoveCursor(GetMoveCursorNewPos());
                        handled = true;
                    }
                }
            }
            else if (modifiers == EditorModifiers.Shift)
            {
                switch (key)
                {
                    case EditorKey.Down: MoveCursorDelta(0, CursorStep, true); handled = true; break;
                    case EditorKey.Up: MoveCursorDelta(0, -CursorStep, true); handled = true; break;
                    case EditorKey.Right: MoveCursorDelta(1, 0, true); handled = true; break;
                    case EditorKey.Left: MoveCursorDelta(-1, 0, true); handled = true; break;
                    case EditorKey.PageDown: MoveCursorDelta(0, 16, true); handled = true; break;
                    case EditorKey.PageUp: MoveCursorDelta(0, -16, true); handled = true; break;
                    case EditorKey.Home: Home(true); handled = true; break;
                    case EditorKey.End: End(true); handled = true; break;
                    case EditorKey.Tab: ShiftTab(); handled = true; break;
                    case EditorKey.Add: ShiftValues(1); handled = true; break;
                    case EditorKey.Subtract: ShiftValues(-1); handled = true; break;
                }
            }
            else if (modifiers == EditorModifiers.Control)
            {
                if (key >= EditorKey.D0 && key <= EditorKey.D9)
                {
                    int n = key - EditorKey.D0;
                    if (n == 0) n = 10;
                    SetBeatSubdivision(n);
                    handled = true;
                }
                else if (key == EditorKey.R)
                {
                    RamdomizeSelection();
                    handled = true;
                }
                else if (key == EditorKey.I)
                {
                    InterpolateSelection(InterpolationMethod.Linear);
                    handled = true;
                }
                else if (key == EditorKey.W)
                {
                    RollSelection(false);
                    handled = true;
                }
                else if (key == EditorKey.T)
                {
                    WriteState();
                    handled = true;
                }
                else if (key >= EditorKey.NumPad0 && key <= EditorKey.NumPad9)
                {
                    Editor.SelectedStepsDown = key - EditorKey.NumPad0;
                    handled = true;
                }
            }
            else if (modifiers == (EditorModifiers.Control | EditorModifiers.Shift))
            {
                if (key >= EditorKey.D0 && key <= EditorKey.D9)
                {
                    int n = key - EditorKey.D0;
                    if (n == 0) n = 10;
                    SetBeatSubdivision(10 + n);
                    handled = true;
                }
                else if (key == EditorKey.Add)
                {
                    ShiftValues(12);
                    handled = true;
                }
                else if (key == EditorKey.Subtract)
                {
                    ShiftValues(-12);
                    handled = true;
                }
                else if (key == EditorKey.Delete)
                {
                    InsertOrDelete(false, true);
                    handled = true;
                }
                else if (key == EditorKey.Insert)
                {
                    InsertOrDelete(false, false);
                    handled = true;
                }
                else if (key == EditorKey.I)
                {
                    InterpolateSelection(InterpolationMethod.ExpIntp);
                    handled = true;
                }
                else if (key == EditorKey.W)
                {
                    RollSelection(true);
                    handled = true;
                }
            }
            else if (modifiers == (EditorModifiers.Control | EditorModifiers.Alt))
            {
                if (key >= EditorKey.NumPad0 && key <= EditorKey.NumPad9)
                {
                    Editor.SelectedStepsRight = key - EditorKey.NumPad0;
                    handled = true;
                }
            }
            return handled;
        }

        /// <summary>The WPF control's input bindings (Ctrl+X, C, V, Z, Y, A, U), and Ctrl+Shift+Z for redo as on macOS.</summary>
        bool KeyBindings(EditorKey key, EditorModifiers modifiers)
        {
            if (modifiers == EditorModifiers.Control)
            {
                switch (key)
                {
                    case EditorKey.X: Run(CutCommand); return true;
                    case EditorKey.C: Run(CopyCommand); return true;
                    case EditorKey.V: Run(PasteCommand); return true;
                    case EditorKey.Z: Run(UndoCommand); return true;
                    case EditorKey.Y: Run(RedoCommand); return true;
                    case EditorKey.A: Run(SelectAllCommand); return true;
                    case EditorKey.U: Run(UnselectCommand); return true;
                }
            }
            else if (modifiers == (EditorModifiers.Control | EditorModifiers.Shift) && key == EditorKey.Z)
            {
                Run(RedoCommand);
                return true;
            }
            return false;
        }

        /// <summary>The editor's keys (PatternEditor.xaml.cs: its KeyDown handler and its input bindings).</summary>
        bool EditorKeyDown(EditorKey key, EditorModifiers modifiers)
        {
            var machine = Editor.SelectedMachine;
            if (modifiers == EditorModifiers.None)
            {
                switch (key)
                {
                    case EditorKey.Add:
                    case EditorKey.Subtract:
                        if (machine != null)
                        {
                            int index = machine.patterns.IndexOf(machine.SelectedPattern);
                            if (index != -1)
                            {
                                index = key == EditorKey.Add ? Math.Min(index + 1, machine.patterns.Count - 1) : Math.Max(index - 1, 0);
                                Global.Buzz.SetPatternEditorPattern(machine.Patterns[index].Pattern);
                            }
                        }
                        return true;
                    case EditorKey.Return:
                        Global.Buzz.ActivateSequenceEditor();
                        return true;
                    case EditorKey.Divide:
                        if (machine != null && machine.Machine.BaseOctave > 0)
                            machine.Machine.BaseOctave--;
                        return true;
                    case EditorKey.Multiply:
                        if (machine != null && machine.Machine.BaseOctave < 9)
                            machine.Machine.BaseOctave++;
                        return true;
                    case EditorKey.F1:
                        Editor.IsHelpVisible ^= true;
                        return true;
                }
            }
            else if (modifiers == EditorModifiers.Control)
            {
                switch (key)
                {
                    case EditorKey.Add: if (machine != null) machine.UndoableTrackCount++; return true;
                    case EditorKey.Subtract: if (machine != null) machine.UndoableTrackCount--; return true;
                    case EditorKey.Back: PropertiesRequested?.Invoke(); return true;
                    case EditorKey.Return: Run(Editor.CreatePatternCommand); return true;
                    case EditorKey.Delete: Run(Editor.DeletePatternCommand); return true;
                }
            }
            else if (modifiers == (EditorModifiers.Control | EditorModifiers.Shift))
            {
                if (key == EditorKey.Return)
                {
                    Run(Editor.ClonePatternCommand);
                    return true;
                }
            }
            else if (modifiers == EditorModifiers.Alt)
            {
                switch (key)
                {
                    case EditorKey.B: ViewRequest?.Invoke("BaseOctave"); return true;
                    case EditorKey.R: Editor.IsKeyboardMappingVisible = true; ViewRequest?.Invoke("RootNote"); return true;
                    case EditorKey.Y: Editor.IsKeyboardMappingVisible = true; ViewRequest?.Invoke("KeyboardMapping"); return true;
                    case EditorKey.N: Editor.PlayNotes ^= true; return true;
                }
            }
            return false;
        }

        /// <summary>Typed text (WPF: PreviewTextInput): '&lt;' and '&gt;' pick the wave, and ASCII columns take characters.</summary>
        public bool TextInput(string text)
        {
            if (text == null || text.Length != 1) return false;
            char ch = text[0];
            if (ch == '<')
            {
                Editor.SelectedWaveIndex--;
                return true;
            }
            if (ch == '>')
            {
                Editor.SelectedWaveIndex++;
                return true;
            }
            if (ch == '.' || ch < 32 || ch >= 128 || !HasColumns) return false;

            var col = pattern.GetColumn(pattern.CursorPosition);
            if (col is ParameterColumn && col.Type == ColumnRenderer.ColumnType.Ascii)
            {
                var pc = (ParameterColumn)col;

                bool valid = pc.PatternColumn.Parameter.IsValidAsciiChar((int)ch);

                if (!valid && ch >= 'a' && ch <= 'z')
                {
                    ch = (char)(ch + 'A' - 'a');
                    valid = pc.PatternColumn.Parameter.IsValidAsciiChar((int)ch);
                }
                else if (!valid && ch >= 'A' && ch <= 'Z')
                {
                    ch = (char)(ch + 'a' - 'A');
                    valid = pc.PatternColumn.Parameter.IsValidAsciiChar((int)ch);
                }

                if (valid)
                {
                    var action = pc.EditDigit(pattern.CursorPosition, (int)ch);
                    DoEditActions(action, false);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ edit commands (as in WPF)

        private void WriteState()
        {
            if (RefuseInNoteLanes("Write state")) return;
            Selection s = this.pattern.Selection;

            if (!s.Active)
                s = Selection.Start(pattern.CursorPosition).SetEnd(pattern.CursorPosition);

            Digit columnIterator = s.Bounds.Item1;
            Digit lastColumn = s.Bounds.Item2;

            while (true)
            {
                var digitIterator = columnIterator;
                while (true)
                {
                    var column = digitIterator.ParameterColumn;
                    var par = column.PatternColumn.Parameter;
                    int value = par.GetValue(column.PatternColumn.Track);
                    int time = digitIterator.TimeInBeat + digitIterator.Beat * BUZZ_TICKS_PER_BEAT * PatternEvent.TimeBase;
                    MPEPattern mpePattern = Editor.MPEPatternsDB.GetMPEPattern(Pattern.Pattern);
                    MPEPatternColumn mpeColumn = mpePattern.GetColumn(column.PatternColumn);
                    DoAction(new MPESetOrClearEventsAction(Pattern.Pattern, mpeColumn, new[] { new PatternEvent(time, value) }, true));

                    if (digitIterator.Beat * BUZZ_TICKS_PER_BEAT * PatternEvent.TimeBase + digitIterator.TimeInBeat >=
                        lastColumn.Beat * BUZZ_TICKS_PER_BEAT * PatternEvent.TimeBase + lastColumn.TimeInBeat)
                        break;

                    digitIterator = digitIterator.Down;
                }
                if (columnIterator.Column == lastColumn.Column && columnIterator.ColumnSet == lastColumn.ColumnSet)
                    break;
                columnIterator = columnIterator.Right;
            }
        }

        private void RollSelection(bool down)
        {
            if (RefuseInNoteLanes("Rotate")) return;
            Selection s = this.pattern.Selection;

            if (!s.Active || s.Bounds.Item1.ParameterColumn.GetDigitTime(s.Bounds.Item1) >= s.Bounds.Item2.ParameterColumn.GetDigitTime(s.Bounds.Item2))
                return;

            MPEPattern mpePattern = Editor.MPEPatternsDB.GetMPEPattern(Pattern.Pattern);
            DoAction(new MPERotateAction(mpePattern, s, down));
        }

        /// <summary>Transposes notes, or adds to values, in the selection (or at the cursor).</summary>
        internal void ShiftValues(int delta)
        {
            if (InNoteLanes())
            {
                ShiftNoteLanes(delta);
                return;
            }
            Selection s = this.pattern.Selection;

            if (!s.Active)
                s = Selection.Start(pattern.CursorPosition).SetEnd(pattern.CursorPosition);

            MPEPattern mpePattern = Editor.MPEPatternsDB.GetMPEPattern(Pattern.Pattern);
            DoAction(new MPEUpDownAction(mpePattern, s, delta));
        }

        private void RamdomizeSelection()
        {
            if (RefuseInNoteLanes("Randomize")) return;
            Selection s = this.pattern.Selection;

            if (!s.Active)
                s = Selection.Start(pattern.CursorPosition).SetEnd(pattern.CursorPosition);

            var rnd = new Random();

            using (new ActionGroup(Editor.EditContext.ActionStack))
            {
                var columnIterator = s.Bounds.Item1;

                var lastColumn = s.Bounds.Item2;
                while (true)
                {
                    var digitIterator = columnIterator;
                    while (true)
                    {
                        var column = digitIterator.ParameterColumn;
                        var par = column.PatternColumn.Parameter;
                        int value = rnd.Next(par.MinValue, par.MaxValue + 1);
                        int time = digitIterator.TimeInBeat + digitIterator.Beat * BUZZ_TICKS_PER_BEAT * PatternEvent.TimeBase;
                        MPEPattern mpePattern = Editor.MPEPatternsDB.GetMPEPattern(Pattern.Pattern);
                        MPEPatternColumn mpeColumn = mpePattern.GetColumn(column.PatternColumn);
                        DoAction(new MPESetOrClearEventsAction(Pattern.Pattern, mpeColumn, new[] { new PatternEvent(time, value) }, true));

                        if (digitIterator.Beat * BUZZ_TICKS_PER_BEAT * PatternEvent.TimeBase + digitIterator.TimeInBeat >=
                            lastColumn.Beat * BUZZ_TICKS_PER_BEAT * PatternEvent.TimeBase + lastColumn.TimeInBeat)
                            break;
                        digitIterator = digitIterator.Down;
                    }
                    if (columnIterator.Column == lastColumn.Column && columnIterator.ColumnSet == lastColumn.ColumnSet)
                        break;
                    columnIterator = columnIterator.Right;
                }
            }
        }

        internal enum InterpolationMethod
        {
            Linear,
            ExpIntp
        }

        double ExpIntp(double x, double y, double a)
        {
            double lx = Math.Log(x);
            double ly = Math.Log(y);
            return Math.Exp(lx + a * (ly - lx));
        }

        double Interpolate(double a, double v1, double v2, bool expintp)
        {
            if (expintp && ((v1 > 0 && v2 > 0) || (v1 < 0 && v2 < 0)))
            {
                return (ExpIntp(v1, v2, a) - v1) / (v2 - v1);
            }
            else
            {
                return a;
            }
        }

        /// <summary>Fills the rows between the selection's first and last values of each column.</summary>
        internal void InterpolateSelection(InterpolationMethod method)
        {
            if (RefuseInNoteLanes("Interpolate")) return;
            Selection s = this.pattern.Selection;

            if (!s.Active)
                return;

            using (new ActionGroup(Editor.EditContext.ActionStack))
            {
                var columnIterator = s.Bounds.Item1;
                var lastColumn = s.Bounds.Item2;

                if (columnIterator.Beat == lastColumn.Beat && columnIterator.RowInBeat == lastColumn.RowInBeat)
                    return;

                while (true)
                {
                    var digitIterator = columnIterator;
                    var column = digitIterator.ParameterColumn;
                    var firstDigit = digitIterator.NearestRow(columnIterator.TimeInBeat);
                    var lastDigit = new Digit(firstDigit.PatternVM, firstDigit.ColumnSet, firstDigit.Column, lastColumn.Beat, lastColumn.RowInBeat, lastColumn.Index).NearestRow(lastColumn.TimeInBeat);
                    int firstValue = column.FetchBeat(firstDigit.Beat).Rows[firstDigit.RowInBeat].Value;
                    int lastValue = column.FetchBeat(lastDigit.Beat).Rows[lastDigit.RowInBeat].Value;
                    int firstDigitTime = column.GetDigitTime(firstDigit);
                    int lastDigitTime = column.GetDigitTime(lastDigit);

                    digitIterator = digitIterator.Down; // Start with next

                    if (firstValue != -1 && lastValue != -1)
                    {
                        while (true)
                        {
                            if (method == InterpolationMethod.ExpIntp && (firstValue == 0 || lastValue == 0))
                                break;

                            if (digitIterator.Beat * BUZZ_TICKS_PER_BEAT * PatternEvent.TimeBase + digitIterator.TimeInBeat >=
                                lastDigit.Beat * BUZZ_TICKS_PER_BEAT * PatternEvent.TimeBase + lastDigit.TimeInBeat)
                                break;

                            int time = digitIterator.TimeInBeat + digitIterator.Beat * BUZZ_TICKS_PER_BEAT * PatternEvent.TimeBase;
                            double a = (time - firstDigitTime) / (double)(lastDigitTime - firstDigitTime);
                            double iValue = Interpolate(a, firstValue, lastValue, method == InterpolationMethod.ExpIntp);
                            int value = (int)(firstValue + ((lastValue - firstValue) + 1) * iValue);

                            MPEPattern mpePattern = column.Set.Pattern.Editor.MPEPatternsDB.GetMPEPattern(column.Set.Pattern.Pattern);
                            MPEPatternColumn mpeColumn = mpePattern.GetColumn(column.PatternColumn);
                            DoAction(new MPESetOrClearEventsAction(Pattern.Pattern, mpeColumn, new[] { new PatternEvent(time, value) }, true));

                            digitIterator = digitIterator.Down;
                        }
                    }
                    if (columnIterator.Column == lastColumn.Column && columnIterator.ColumnSet == lastColumn.ColumnSet)
                        break;
                    columnIterator = columnIterator.Right;
                }
            }
        }

        private void InsertOrDelete(bool row, bool delete)
        {
            if (RefuseInNoteLanes(delete ? "Delete" : "Insert")) return;
            var cursorPos = pattern.CursorPosition;
            if (!row)
            {
                DoAction(new MPEInsertOrDeleteAction(pattern.Editor.MPEPatternsDB.GetMPEPattern(pattern.Pattern), cursorPos, delete));
            }
            else
            {
                using (new ActionGroup(Editor.EditContext.ActionStack))
                {
                    var fisrtColumn = cursorPos.FirstColumn.NearestRow(cursorPos.TimeInBeat);
                    int lastColumn = cursorPos.LastColumn.Column;
                    while (true)
                    {
                        DoAction(new MPEInsertOrDeleteAction(pattern.Editor.MPEPatternsDB.GetMPEPattern(pattern.Pattern), fisrtColumn, delete));
                        if (fisrtColumn.Column >= lastColumn)
                            break;
                        fisrtColumn = fisrtColumn.NextColumn.NearestRow(cursorPos.TimeInBeat);
                    }
                }
            }
            InvalidateVisual();
        }

        void SetBeatSubdivision(int n)
        {
            if (RefuseInNoteLanes("Beat subdivision")) return;
            var cb = pattern.Selection.ColumnsAndBeats;
            if (!cb.Any()) cb = LinqExtensions.Return(pattern.CursorPosition.ColumnAndBeat);

            using (new ActionGroup(Editor.EditContext.ActionStack))
            {
                DoAction(new MPESetBeatSubdivisionAction(pattern.Editor.MPEPatternsDB.GetMPEPattern(pattern.Pattern), cb.Select(i => Tuple.Create(i, n))));
                var sel = pattern.Selection.SetEnd(pattern.Selection.Bounds.Item2);
                if (!sel.Active)
                    sel = Selection.Start(pattern.CursorPosition).SetEnd(pattern.CursorPosition);
                DoAction(new MPEQuantizeBeatsAction(pattern.Editor.MPEPatternsDB.GetMPEPattern(pattern.Pattern), sel));
            }
            InvalidateVisual();
        }

        IEnumerable<BuzzAction> EditNote(ParameterColumn pc, int value)
        {
            var action = pc.EditDigit(pattern.CursorPosition, value);

            var nextColumnPos = pattern.CursorPosition.NextColumn;
            var npc = pattern.GetColumn(nextColumnPos) as ParameterColumn;
            var beat = pc.FetchBeat(pattern.CursorPosition.Beat);
            var nextBeat = npc.FetchBeat(nextColumnPos.Beat);

            if (npc.PatternColumn.Parameter.Flags.HasFlag(ParameterFlags.Wave) && beat.Rows.Count == nextBeat.Rows.Count)
            {
                if (value == BuzzNote.Off || value < 0)
                    return action.Concat(npc.EditValue(nextColumnPos, -1));
                else
                    return action.Concat(npc.EditValue(nextColumnPos, Editor.SelectedWave != null ? Editor.SelectedWave.Index + 1 : 1)); // Mtk crashes if selected wave == 0
            }
            else
            {
                return action;
            }
        }

        // ------------------------------------------------------------------ cursor and selection

        public void UpdateCursor()
        {
            if (!HasColumns) return;

            var p = Pattern.CursorPosition;
            if (p.ColumnSet >= Pattern.ColumnSets.Count)
            {
                p = p.LastColumnSet;
                Pattern.CursorPosition = p;
            }

            UpdateStatusBar();
            CursorMoved?.Invoke();
        }

        void UpdateStatusBar()
        {
            var p = Pattern.CursorPosition;

            var cs = pattern.GetColumnSet(p);
            Editor.StatusBarItem1 = cs.Label;

            var vt = pattern.GetValueType(p);
            if (vt != ColumnRenderer.BeatValueType.NoValue)
            {
                var v = pattern.GetValue(p);
                var vs = pattern.GetValueString(p);
                // Fork (ux-overhaul.md P3c): the value in the other base too, and a drum machine's name for a note.
                var pcol = p.ParameterColumn;
                string other = pcol.Type == ColumnRenderer.ColumnType.Note ? v.ToString() : pcol.IsDecimal ? "0x" + v.ToString("X") : v.ToString();
                string full = pcol.Type == ColumnRenderer.ColumnType.Note && v != BuzzNote.Off ? pcol.NoteFullName(v) : null;
                Editor.StatusBarItem2 = vs + " (" + other + ") " + (full ?? pcol.PatternColumn.Parameter.DescribeValue(v));
            }
            else
            {
                Editor.StatusBarItem2 = null;
            }

            var c = pattern.GetColumn(p);
            Editor.StatusBarItem3 = !string.IsNullOrEmpty(c.Description) ? c.Description : c.Label;
        }

        public void MoveCursor(Digit p, bool select = false)
        {
            if (!HasColumns) return;
            p = p.Constrained;

            pattern.CursorPosition = p;
            UpdateCursor();

            if (select)
                pattern.Selection = pattern.Selection.SetEnd(p);
            else
                pattern.Selection = Selection.Start(p);
        }

        public void MoveCursorDelta(int dx, int dy, bool select = false)
        {
            if (!HasColumns) return;
            MoveCursor(pattern.CursorPosition.Offset(dx, dy, select), select);
        }

        void Home(bool select = false)
        {
            MoveCursor(pattern.CursorPosition.Home(), select);
        }

        void End(bool select = false)
        {
            MoveCursor(pattern.CursorPosition.End(), select);
        }

        void Tab()
        {
            Digit p = pattern.CursorPosition;
            p = p.Tab(false);
            MoveCursor(p);
        }

        void ShiftTab()
        {
            Digit p = pattern.CursorPosition;
            p = p.Tab(true);
            MoveCursor(p);
        }

        public void GoToColumn(ColumnRenderer.IColumn c)
        {
            var d = pattern.CursorPosition;
            var x = pattern.GetColumnDigit(c);
            d = d.SetColumnSet(x.ColumnSet).SetColumn(x.Column);
            MoveCursor(d);
        }

        public void SelectColumn(ColumnRenderer.IColumn c)
        {
            pattern.Selection = Selection.Column(pattern.GetColumnDigit(c));
        }

        public void SelectColumnSet(ColumnRenderer.IColumnSet cs)
        {
            pattern.Selection = Selection.ColumnSet(pattern.GetColumnSetDigit(cs));
        }

        public void SelectRow(Digit d)
        {
            pattern.Selection = Selection.Row(d);
        }

        public void SelectAllColumnsOfBeat(Digit d)
        {
            pattern.Selection = Selection.AllColumnsOfBeat(d);
        }

        /// <summary>A mouse press on the pattern (WPF: the pattern grid's Dragger): clicks 1, 2 and 3.</summary>
        public void PressAt(Digit d, int clickCount, out bool dragSelects)
        {
            dragSelects = true;
            if (!HasColumns) return;
            if (PatternEditor.Settings.CursorScrollMode == CursorScrollMode.Standard)
            {
                MoveCursor(d);
                pattern.Selection = Selection.Start(d);
                if (clickCount == 2)
                    pattern.Selection = Selection.ColumnSetRow(pattern.CursorPosition);
                else if (clickCount >= 3)
                    pattern.Selection = Selection.ColumnSetBeat(pattern.CursorPosition);
            }
            else if (clickCount == 1)
            {
                pattern.Selection = Selection.Start(d);
            }
            else if (clickCount == 2)
            {
                MoveCursor(d);
                dragSelects = false;
            }
        }

        /// <summary>A mouse press on the row numbers: 1 moves the cursor's row, 2 selects the row, 3 the beat.</summary>
        public void PressRowAt(Digit d, int clickCount, out bool dragSelects)
        {
            dragSelects = false;
            if (!HasColumns) return;
            if (clickCount == 1)
            {
                pattern.CursorPosition = pattern.CursorPosition.SetBeat(d.Beat).SetRowInBeat(d.RowInBeat).Constrained;
                UpdateCursor();
            }
            else if (clickCount == 2)
            {
                SelectRow(d);
                dragSelects = true;
            }
            else if (clickCount == 3)
            {
                SelectAllColumnsOfBeat(d);
                dragSelects = true;
            }
        }

        /// <summary>The mouse dragged to a digit while selecting.</summary>
        public void DragTo(Digit d)
        {
            if (!HasColumns) return;
            pattern.Selection = pattern.Selection.SetEnd(d);
        }

        public void DoAction(BuzzAction action)
        {
            Editor.EditContext.ActionStack.Do(action);
        }

        public void DoActions(IEnumerable<BuzzAction> actions)
        {
            if (RunNoteLaneActions(actions)) return;
            using (new ActionGroup(Editor.EditContext.ActionStack))
            {
                foreach (var a in actions)
                    Editor.EditContext.ActionStack.Do(a);
            }
        }

        void DoEditActions(IEnumerable<BuzzAction> actions, bool editWasClear)
        {
            if (RunNoteLaneActions(actions))
            {
                // The edit went to the song's history; the cursor moves as it does after a typed value, without a step of the editor's own.
                var lanePc = pattern.GetColumn(pattern.CursorPosition) as ParameterColumn;
                if (PatternEditor.Settings.EditStayOnRow && lanePc != null && lanePc.Type != ColumnRenderer.ColumnType.Note && !editWasClear)
                    MoveCursor(!pattern.CursorPosition.IsLastIndex
                        ? pattern.CursorPosition.Offset(1, 0)
                        : pattern.CursorPosition.FirstIndex.Offset(Editor.SelectedStepsRight, Editor.SelectedStepsDown, true));
                else
                    MoveCursor(GetMoveCursorNewPos());
                return;
            }
            using (new ActionGroup(Editor.EditContext.ActionStack))
            {
                foreach (var a in actions)
                    Editor.EditContext.ActionStack.Do(a);

                var pc = pattern.GetColumn(pattern.CursorPosition) as ParameterColumn;

                if (PatternEditor.Settings.EditStayOnRow && pc.Type != ColumnRenderer.ColumnType.Note && !editWasClear)
                {
                    if (!pattern.CursorPosition.IsLastIndex)
                        Editor.EditContext.ActionStack.Do(new Actions.MoveCursorAction(pattern, pattern.CursorPosition.Offset(1, 0)));
                    else
                        Editor.EditContext.ActionStack.Do(new Actions.MoveCursorAction(pattern, pattern.CursorPosition.FirstIndex.Offset(Editor.SelectedStepsRight, Editor.SelectedStepsDown, true)));
                }
                else
                {
                    var pos = GetMoveCursorNewPos();
                    Editor.EditContext.ActionStack.Do(new Actions.MoveCursorAction(pattern, pos));
                }
            }
        }

        // ------------------------------------------------------------------ the Notes lanes (docs/specs/daw-modes.md AR3)

        /// <summary>
        /// Runs the actions that are edits of the Notes lanes on the song's history (not the editor's own), true when there were
        /// any. The others in the list, if any, go to the editor's stack as before.
        /// </summary>
        bool RunNoteLaneActions(IEnumerable<BuzzAction> actions)
        {
            if (actions == null) return false;
            var list = actions as IList<BuzzAction> ?? actions.ToList();
            if (!list.Any(a => a is NoteLaneAction)) return false;
            foreach (var a in list)
                if (a is NoteLaneAction lane) lane.RunOnSong();
            var others = list.Where(a => a is not NoteLaneAction).ToList();
            if (others.Count > 0)
                using (new ActionGroup(Editor.EditContext.ActionStack))
                    foreach (var a in others) Editor.EditContext.ActionStack.Do(a);
            return true;
        }

        /// <summary>Whether the cursor, or the selection, is in or reaches over a Notes lane.</summary>
        bool InNoteLanes()
        {
            if (pattern == null || !HasColumns) return false;
            int a, b;
            if (pattern.Selection.Active)
            {
                var bounds = pattern.Selection.Bounds;
                a = bounds.Item1.ColumnSet;
                b = bounds.Item2.ColumnSet;
            }
            else
                a = b = pattern.CursorPosition.ColumnSet;
            for (int i = Math.Max(0, a); i <= b && i < pattern.ColumnSets.Count; i++)
                if (pattern.ColumnSets[i] is NoteLaneColumnSet) return true;
            return false;
        }

        /// <summary>For the commands the lanes don't have yet (they act on the editor's own columns): says so, true when it did.</summary>
        bool RefuseInNoteLanes(string what)
        {
            if (!InNoteLanes()) return false;
            Global.Buzz.DCWriteLine("{statusbar}" + what + " isn't in the Notes lanes yet: they are the MIDI column's notes, which the piano roll edits too.");
            return true;
        }

        /// <summary>Transposes the notes in the selection (or at the cursor) of the Notes lanes: one step of the song's history.</summary>
        void ShiftNoteLanes(int delta)
        {
            Selection s = pattern.Selection;
            if (!s.Active)
                s = Selection.Start(pattern.CursorPosition).SetEnd(pattern.CursorPosition);
            var (tl, br) = s.Bounds;
            int rpb = Math.Max(1, pattern.DefaultRPB);
            NoteLaneHost host = null;
            var removed = new List<LaneEvent>();
            var added = new List<LaneEvent>();
            for (int set = tl.ColumnSet; set <= br.ColumnSet; set++)
            {
                if (pattern.ColumnSets[set] is not NoteLaneColumnSet lanes)
                {
                    Global.Buzz.DCWriteLine("{statusbar}Transpose: select Notes lanes only, or none.");
                    return;
                }
                host = lanes.Host;
                int firstColumn = set == tl.ColumnSet ? tl.Column : 0, lastColumn = set == br.ColumnSet ? br.Column : lanes.Columns.Count - 1;
                if (firstColumn > 0 || lastColumn < 0) continue;       // the Note column isn't in the selection
                int firstRow = tl.Beat * rpb + tl.RowInBeat, lastRow = br.Beat * rpb + br.RowInBeat;
                for (int row = firstRow; row <= lastRow; row++)
                {
                    var edit = host.Lanes.Transpose(lanes.Voice, row, delta);
                    removed.AddRange(edit.Removed);
                    added.AddRange(edit.Added);
                }
            }
            if (host == null) return;
            string why = host.Apply(new NoteLaneEdit(removed, added, null));
            if (why != null) Global.Buzz.DCWriteLine("{statusbar}" + why);
        }

        void PlayColumnSet(Digit d)
        {
            var col = pattern.GetColumn(d);
            if (col is ParameterColumn)
            {
                var pc = (ParameterColumn)col;
                pc.Set.SendCCsAtTime(pc.GetDigitTime(d));
            }
        }

        void PlayAllColumnSets(Digit d)
        {
            var col = pattern.GetColumn(d);
            if (!(col is ParameterColumn)) return;
            var pc = (ParameterColumn)col;
            var time = pc.GetDigitTime(d);

            foreach (var cs in pattern.ColumnSets)
                cs.SendCCsAtTime(time);
        }

        Digit GetMoveCursorNewPos()
        {
            int numStepsOverEnd = pattern.CursorPosition.StepsOverEnd(Editor.SelectedStepsDown);

            var pos = pattern.CursorPosition.Offset(Editor.SelectedStepsRight, Editor.SelectedStepsDown, true);

            if (numStepsOverEnd > 0)
            {
                if (PatternEditor.Settings.AutoMoveCursorToTop == AutoMoveCursorToTopMode.FirstRow)
                {
                    pos = pos.SetBeat(0).SetRowInBeat(0);
                }
                else if (PatternEditor.Settings.AutoMoveCursorToTop == AutoMoveCursorToTopMode.BasedOnStepLength)
                {
                    pos = pos.SetBeat(0).SetRowInBeat(0);
                    pos = pos.Offset(0, numStepsOverEnd - 1);
                }
            }

            return pos;
        }
    }
}
