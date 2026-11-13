// Fork (docs/specs/studio-mvp.md S6): the Pattern tab. As ReBuzz's WPF EditorView does, it shows the
// pattern editor machine's control for the machine and pattern chosen: the Modern Pattern Editor's
// Avalonia view, which the engine gets from the editor machine (PatternEditorControl). A machine
// chooser and a pattern chooser sit above it. Other views pick what it shows with Edit(pattern) or
// Edit(machine); the engine tells it when the pattern changes (SetPatternEditorPattern).

using ReBuzz.Studio.Shell.Design;
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views
{
    using Control = Avalonia.Controls.Control;

    internal sealed class PatternView : IStudioView
    {
        public string Title => "Pattern";

        public Control Create(StudioHost host) => new PatternPage(host);

        /// <summary>The page shown (the last one made), or null.</summary>
        public static PatternPage Page { get; internal set; }

        /// <summary>Shows a pattern in its machine's editor, on the Pattern tab (for the machine and sequence views).</summary>
        public static void Edit(IPattern pattern) => Page?.Edit(pattern);

        /// <summary>Shows a machine's patterns (the one its editor shows, else its first), on the Pattern tab.</summary>
        public static void Edit(IMachine machine) => Page?.Edit(machine);

        /// <summary>Brings the Pattern tab to the front, with the keyboard on the editor, showing what it showed.</summary>
        public static void Show() => Page?.Activate();

        /// <summary>The pattern shown changed (null: none).</summary>
        public static event Action<IPattern> PatternShown;

        internal static void RaisePatternShown(IPattern p) => PatternShown?.Invoke(p);
    }

    /// <summary>
    /// The Pattern tab's content: the choosers, and the editor's control below them. The shell's Edit
    /// menu and its Undo and Redo keys reach the editor's own undo stack and clipboard through it.
    /// </summary>
    internal sealed class PatternPage : DockPanel, IStudioEditTarget
    {
        readonly ReBuzzCore core;
        readonly ComboBox machineBox = new ComboBox { Width = 180, Margin = new Thickness(4, 4, 12, 4) };
        readonly ComboBox patternBox = new ComboBox { Width = 120, Margin = new Thickness(4) };
        readonly Button newPattern = new Button { Content = "New pattern", Margin = new Thickness(4) };
        readonly ContentControl editorHost = new ContentControl();
        readonly Detail.TrackerRoll rollView;
        readonly Shell.EmptyState empty;
        readonly List<IMachine> machines = new List<IMachine>();
        IMachine machine;
        IPattern pattern;
        bool updating;

        public PatternPage(StudioHost host)
        {
            core = host.Core;
            rollView = new Detail.TrackerRoll(core) { IsVisible = false };
            empty = new Shell.EmptyState(core);
            var bar = new WrapPanel { Orientation = Orientation.Horizontal, Background = StudioTokens.SurfacePanelBrush };      // wraps in a narrow window (decision 37)
            bar.Children.Add(Label("Machine:"));
            bar.Children.Add(machineBox);
            bar.Children.Add(Label("Pattern:"));
            bar.Children.Add(patternBox);
            bar.Children.Add(newPattern);
            SetDock(bar, Dock.Top);
            Children.Add(bar);
            Children.Add(new Panel { Children = { empty, editorHost, rollView } });

            machineBox.SelectionChanged += (s, e) =>
            {
                if (!updating && machineBox.SelectedItem is MachineItem item) Edit(item.Machine);
            };
            patternBox.SelectionChanged += (s, e) =>
            {
                if (!updating && patternBox.SelectedItem is PatternItem item) Edit(item.Pattern);
            };
            newPattern.Click += (s, e) => NewPattern();

            // A tab that isn't shown leaves the visual tree: undo goes back to the song (the editor set
            // the engine's EditContext when its grid took the keyboard). The page follows the engine while it
            // is in its window's logical tree, its tab shown or not, and lets go when the window closes (a
            // closed window leaves its logical tree).
            DetachedFromVisualTree += (s, e) => core.EditContext = null;
            AttachedToLogicalTree += (s, e) => Attach();
            DetachedFromLogicalTree += (s, e) => Detach();
        }

        bool attached;

        void Attach()
        {
            if (attached) return;
            attached = true;
            PatternView.Page = this;
            core.SetPatternEditorControl += OnEditorControl;
            core.SetPatternEditorPatternChanged += OnPatternChanged;
            core.SelectedMachineChanged += OnMachineChanged;
            core.SequenceEditorActivated += OnSequenceEditorActivated;
            core.PropertyChanged += OnCorePropertyChanged;
            core.Song.MachineAdded += OnMachinesChanged;
            core.Song.MachineRemoved += OnMachinesChanged;
            StudioSelection.For(core).Changed += FollowSelection;
            RefreshMachines();
            var start = core.PatternEditorPattern ?? machines.Select(m => m.Patterns.FirstOrDefault()).FirstOrDefault(p => p != null);
            if (start != null && !start.Machine.Patterns.Contains(start)) start = null;
            followingSelection = true;      // what the page starts on isn't something the user selected
            try
            {
                if (start != null) Edit(start, activate: false);
                else Show(machines.FirstOrDefault(m => m.DLL.Info.Type != MachineType.Master) ?? machines.FirstOrDefault(), null);
                FollowSelection(null);
            }
            finally
            {
                followingSelection = false;
            }
        }

        // Fork (ux-overhaul.md P2c): what the user chooses here is the studio's selection; the page shows what the others select.
        bool followingSelection;

        /// <summary>The machine and the pattern the page shows (for tests).</summary>
        internal IMachine ShownMachine => machine;
        internal IPattern ShownPattern => pattern;

        void FollowSelection(object source)
        {
            if (source == this || !attached) return;
            var selection = StudioSelection.For(core);
            followingSelection = true;
            try
            {
                if (selection.Pattern is { } p)
                {
                    if (p != pattern) Edit(p, activate: false);
                }
                else if (selection.Machine is { } m && m != machine)
                {
                    var own = (core.PatternEditorPattern != null && core.PatternEditorPattern.Machine == m ? core.PatternEditorPattern : null) ?? m.Patterns.FirstOrDefault();
                    if (own != null) Edit(own, activate: false);
                    else
                    {
                        core.SetPatternEditorMachine(m);
                        Show(m, null);
                    }
                }
            }
            finally
            {
                followingSelection = false;
            }
        }

        // A closed window lets go of the editor's control, so another page can show it.
        void Detach()
        {
            if (!attached) return;
            attached = false;
            core.SetPatternEditorControl -= OnEditorControl;
            core.SetPatternEditorPatternChanged -= OnPatternChanged;
            core.SelectedMachineChanged -= OnMachineChanged;
            core.SequenceEditorActivated -= OnSequenceEditorActivated;
            core.PropertyChanged -= OnCorePropertyChanged;
            core.Song.MachineAdded -= OnMachinesChanged;
            core.Song.MachineRemoved -= OnMachinesChanged;
            StudioSelection.For(core).Changed -= FollowSelection;
            Follow(null);
            editorHost.Content = null;
            if (PatternView.Page == this) PatternView.Page = null;
        }

        /// <summary>The editor control the page shows now (the Modern Pattern Editor's view), or null.</summary>
        public Control EditorControl => editorHost.Content as Control;

        // Fork (ux-overhaul.md P3b): the Tracker and the piano roll are two views of the pattern the choosers name.
        bool rollMode;

        /// <summary>Whether the piano roll is shown instead of the Tracker's grid.</summary>
        public bool IsRollMode => rollMode;

        /// <summary>The piano roll (tests, the window).</summary>
        internal Detail.TrackerRoll Roll => rollView;

        /// <summary>Shows the piano roll (true) or the Tracker (false) for the same pattern.</summary>
        public void SetRollMode(bool roll)
        {
            if (rollMode == roll) return;
            rollMode = roll;
            editorHost.IsVisible = !roll;
            rollView.IsVisible = roll;
            if (roll) rollView.Show(pattern);
            UpdateEmpty();
        }

        public IMachine Machine => machine;

        public IPattern Pattern => pattern;

        public void Edit(IPattern p) => Edit(p, activate: true);

        void Edit(IPattern p, bool activate)
        {
            if (p == null) return;
            core.SetPatternEditorPattern(p);      // the engine links the machine's editor and raises the events below
            Show(p.Machine, p);
            if (!followingSelection) StudioSelection.For(core).Set(p.Machine, p, this);
            if (activate) Activate();
        }

        public void Edit(IMachine m)
        {
            if (m == null) return;
            var p = (core.PatternEditorPattern != null && core.PatternEditorPattern.Machine == m ? core.PatternEditorPattern : null)
                ?? m.Patterns.FirstOrDefault();
            if (p != null)
            {
                Edit(p);
                return;
            }
            core.SetPatternEditorMachine(m);
            Show(m, null);
            if (!followingSelection) StudioSelection.For(core).SelectMachine(m, this);
            Activate();
        }

        // IStudioEditTarget: the editor's undo stack and clipboard (its IEditContext, from the editor
        // machine's view; the view comes from the editor's own assembly, so it is found by name). With
        // nothing to undo in the editor, the song's undo history takes the command.

        /// <summary>The shown editor's undo stack and clipboard commands, or null.</summary>
        public IEditContext EditContext => EditorControl?.GetType().GetProperty("EditContext")?.GetValue(EditorControl) as IEditContext;

        public bool CanUndo => EditContext?.ActionStack.CanUndo == true || (rollMode && rollView.CanUndoLoop);
        public bool CanRedo => EditContext?.ActionStack.CanRedo == true || (rollMode && rollView.CanRedoLoop);

        /// <summary>Undoes the editor's last edit; false when it has none (the song's undo can have it).</summary>
        public bool Undo()
        {
            if (!CanUndo) return false;
            if (rollMode && rollView.UndoLoop()) return true;          // a loop drag, the song's step, comes first when it was the last
            EditContext.ActionStack.Undo();
            if (rollMode) rollView.Poll();
            return true;
        }

        public bool Redo()
        {
            if (!CanRedo) return false;
            if (rollMode && rollView.RedoLoop()) return true;
            EditContext.ActionStack.Redo();
            if (rollMode) rollView.Poll();
            return true;
        }

        // The notes of the piano roll answer the Edit commands while it is shown (the tracker has its own keys for these).
        public bool Delete() => rollMode && rollView.Delete();
        public bool SelectAll() => rollMode && rollView.SelectAll();
        public bool DeselectAll() => rollMode && rollView.DeselectAll();
        public bool Duplicate() => rollMode && rollView.Duplicate();
        public bool Cut() => rollMode ? rollView.Cut() : Run(EditContext?.CutCommand);
        public bool Copy() => rollMode ? rollView.Copy() : Run(EditContext?.CopyCommand);
        public bool Paste() => rollMode ? rollView.Paste() : Run(EditContext?.PasteCommand);

        static bool Run(System.Windows.Input.ICommand command)
        {
            if (command == null || !command.CanExecute(null)) return false;
            command.Execute(null);
            return true;
        }

        /// <summary>Brings the Pattern tab to the front and gives the editor the keyboard (ReBuzz: ActivatePatternEditor).</summary>
        /// <summary>Shows the page: the window sets it to open the detail panel (P3a); without it the page is a tab's.</summary>
        internal Action Reveal { get; set; }

        /// <summary>The editor's Return key: to the arrangement; the window sets it, else the tab control's tab is used.</summary>
        internal Action ShowSequence { get; set; }

        internal void Activate()
        {
            // In the studio's window the Tracker is the detail panel's (Reveal opens it); elsewhere a tab.
            if (Reveal != null) Reveal();
            else
            {
                var tab = this.FindLogicalAncestorOfType<TabItem>();
                if (tab != null) tab.IsSelected = true;
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (rollMode) rollView.FocusRoll();
                else if (pattern != null) core.ActivatePatternEditor();
            });
        }

        void NewPattern()
        {
            if (machine == null) return;
            var name = BuzzGUI.Common.InterfaceExtensions.MachineExtensions.GetNewPatternName(machine);
            int length = pattern?.Length ?? 16;
            core.SongCore.ActionStack.Do(new BuzzGUI.Common.Actions.MachineActions.CreatePatternAction(machine, name, length));
            var made = machine.Patterns.FirstOrDefault(x => x.Name == name);
            if (made != null) Edit(made);
        }

        void OnEditorControl(object control)
        {
            var c = control as Control;
            if (c != null && c.Parent is ContentControl other && other != editorHost)
                other.Content = null;           // another page had it (a window that isn't closed yet)
            editorHost.Content = c;
            HookBars(c);
            UpdateEmpty();
        }

        /// <summary>
        /// The Tracker's bar rules follow the song's time signature where the pattern lies (ux-overhaul.md P3c, decision 55). The
        /// studio doesn't reference the editor, so its grid is found by name (as EditContext is).
        /// </summary>
        void HookBars(Control editor)
        {
            var grid = editor?.GetType().GetProperty("TrackerGrid")?.GetValue(editor);
            grid?.GetType().GetProperty("BarTicksSource")?.SetValue(grid, (Func<int>)(() => Detail.TrackerRoll.BarLengthOf(core, pattern)));
        }

        void OnPatternChanged(IPattern p)
        {
            if (p == null) return;
            // The engine's pattern when this runs (S9): a song's loader sets several in a row, and the
            // page shows the last, not each in turn.
            Dispatcher.UIThread.Post(() =>
            {
                var now = core.PatternEditorPattern;
                if (now?.Machine == null || !now.Machine.Patterns.Contains(now)) now = p;
                Show(now.Machine, now);
            });
        }

        void OnMachineChanged(IMachine m)
        {
            if (m != null && m != machine)
                Dispatcher.UIThread.Post(() => Show(m, m.Patterns.Contains(pattern) ? pattern : null));
        }

        // Another view asks for the pattern editor (ReBuzz: ActiveView = PatternView, after
        // SetPatternEditorPattern, as the Session View does for a double-clicked clip): to the front.
        void OnCorePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReBuzzCore.ActiveView) && core.ActiveView == BuzzView.PatternView)
                Dispatcher.UIThread.Post(() =>
                {
                    if (attached) Activate();
                });
        }

        void OnSequenceEditorActivated()
        {
            // The editor's Return key: to the sequence editor's tab, if the studio has one.
            if (ShowSequence != null)
            {
                ShowSequence();
                return;
            }
            var tabs = this.FindLogicalAncestorOfType<TabControl>();
            var sequence = tabs?.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == "Arrangement");
            if (sequence != null) sequence.IsSelected = true;
        }

        void OnMachinesChanged(IMachine m) => Dispatcher.UIThread.Post(() =>
        {
            RefreshMachines();
            if (machine != null && !machines.Contains(machine)) Show(machines.FirstOrDefault(), null);
            else Show(machine, pattern);
        });

        void RefreshMachines()
        {
            machines.Clear();
            machines.AddRange(core.Song.Machines.Where(m => !(m is MachineCore mc && mc.Hidden)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>The choosers show a machine and one of its patterns (or none).</summary>
        void Show(IMachine m, IPattern p)
        {
            if (m != machine) Follow(m);
            pattern = p != null && m != null && m.Patterns.Contains(p) ? p : null;
            updating = true;
            try
            {
                var items = machines.Select(x => new MachineItem(x)).ToList();
                machineBox.ItemsSource = items;
                machineBox.SelectedItem = items.FirstOrDefault(x => x.Machine == m);
                var patterns = m?.Patterns.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new PatternItem(x)).ToList() ?? new List<PatternItem>();
                patternBox.ItemsSource = patterns;
                patternBox.SelectedItem = patterns.FirstOrDefault(x => x.Pattern == pattern);
                newPattern.IsEnabled = m != null;
            }
            finally
            {
                updating = false;
            }
            if (pattern == null && m != null && m.Patterns.Count == 0)
                editorHost.Content = null;
            if (rollMode) rollView.Show(pattern);
            ShowPatternsEditor();
            UpdateEmpty();
            PatternView.RaisePatternShown(pattern);
        }

        bool requesting;

        /// <summary>
        /// The pattern the choosers name is the one whose editor shows (S9). The engine can hand the page
        /// another machine's editor, or none, after the pattern was chosen: a song's loader does, setting
        /// several patterns and machines as it opens. Then the tab named a pattern above an empty page
        /// until another pattern was picked. Only for the
        /// engine's pattern editor pattern, which the page follows (OnPatternChanged), and only by the
        /// newest page (two windows' pages would take the control from each other forever): it settles.
        /// </summary>
        void ShowPatternsEditor()
        {
            if (requesting || PatternView.Page != this || pattern == null || pattern != core.PatternEditorPattern
                || pattern.Machine is not MachineCore owner || owner.EditorMachine == null) return;
            var control = core.MachineManager.GetPatternEditorControl(owner.EditorMachine);
            if (control == null || editorHost.Content == control) return;
            requesting = true;
            try
            {
                core.SetPatternEditorPattern(pattern);      // links the editor and hands its control over (OnEditorControl)
            }
            finally
            {
                requesting = false;
            }
        }

        void Follow(IMachine m)
        {
            if (machine != null)
            {
                machine.PatternAdded -= OnPatternsChanged;
                machine.PatternRemoved -= OnPatternsChanged;
            }
            machine = m;
            if (machine != null)
            {
                machine.PatternAdded += OnPatternsChanged;
                machine.PatternRemoved += OnPatternsChanged;
            }
        }

        void OnPatternsChanged(IPattern p) => Dispatcher.UIThread.Post(() => Show(machine, machine != null && machine.Patterns.Contains(pattern) ? pattern : machine?.Patterns.LastOrDefault()));

        /// <summary>The empty state of the Tracker and the piano roll (P3i): no machine, no pattern.</summary>
        internal Shell.EmptyState EmptyState => empty;

        void UpdateEmpty()
        {
            // The same for both views of a pattern: nothing to show until a machine has a pattern chosen.
            bool none = pattern == null || (!rollMode && editorHost.Content == null);
            rollView.IsVisible = rollMode && pattern != null;
            editorHost.IsVisible = !rollMode && pattern != null;       // an editor left from another machine stays out of sight while none is chosen
            string what = rollMode ? "piano roll" : "Tracker";
            if (machine == null || machine.DLL?.Info?.Type == MachineType.Master)
            {
                bool bare = Shell.EmptyState.SongIsEmpty(core);
                empty.Update(none, bare ? "nomachine:bare" : "nomachine", "No machine to edit",
                    bare ? $"The {what} edits a machine's patterns. Start with a sound, or search every machine."
                         : $"The {what} edits a machine's patterns. Choose a machine above, or select one in the Machine View.",
                    Shell.EmptyState.AddMachineStep(), bare);
            }
            else if (machine.Patterns.Count == 0)
                empty.Update(none, "nopattern:" + machine.Name, $"{machine.Name} has no patterns",
                    $"A pattern holds the notes {machine.Name} plays. Make one and the {what} opens on it.",
                    new (string, Action)[] { ("New pattern", NewPattern) }, false);
            else
                empty.Update(none, "choose:" + machine.Name, "Choose a pattern",
                    $"Pick one of {machine.Name}'s {machine.Patterns.Count} patterns in the list above, or make another.",
                    new (string, Action)[] { ("New pattern", NewPattern) }, false);
        }

        static TextBlock Label(string text) => new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush };

        sealed class MachineItem
        {
            public MachineItem(IMachine m) => Machine = m;
            public IMachine Machine { get; }
            public override string ToString() => Machine.Name;
        }

        sealed class PatternItem
        {
            public PatternItem(IPattern p) => Pattern = p;
            public IPattern Pattern { get; }
            public override string ToString() => Pattern.Name;
        }
    }
}
