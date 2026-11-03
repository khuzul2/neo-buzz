// Fork (docs/specs/studio-mvp.md S6): the Modern Pattern Editor's view on Avalonia, a port of
// PatternEditor.xaml: its toolbar (play the pattern, new/clone/delete pattern, beats, tracks, wave,
// base octave, the keyboard mapping's root note, mapping, steps down and right, MIDI edit and
// velocity, play notes, zoom, help), the pattern grid (PatternGrid) and the status bar. The engine
// gets it from the machine (PatternEditorControl) and the studio's Pattern tab shows it.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System.ComponentModel;
using Control = Avalonia.Controls.Control;

namespace WDE.ModernPatternEditor.AvaloniaView
{
    // Avalonia's, not the portable editor's stand-in (PortableShims.cs).
    using Color = Avalonia.Media.Color;

    public sealed class PatternEditorView : UserControl, IDisposable
    {
        readonly PatternEditor editor;
        readonly PatternGrid grid;
        readonly WrapPanel toolbar = new WrapPanel { Margin = new Thickness(4, 0) };
        readonly TextBlock status1 = StatusText(100), status2 = StatusText(150), status3 = StatusText(double.NaN);
        readonly Border help;

        readonly ToggleButton playButton = SmallToggle("▶", "Play/Stop Pattern (Space)");
        readonly NumericUpDown beatsBox = Number(1, 8192, "Beats");
        readonly NumericUpDown tracksBox = Number(0, 128, "Tracks");
        readonly ComboBox waveBox = new ComboBox { Width = 160, Margin = new Thickness(4, 4, 0, 4) };
        readonly ComboBox octaveBox = new ComboBox { Width = 56, Margin = new Thickness(4, 4, 0, 4), ItemsSource = Enumerable.Range(0, 10).ToArray() };
        readonly ToggleButton mappingToggle = SmallToggle("K", "Show Keyboard Mapping");
        readonly ComboBox rootBox = new ComboBox { Width = 64, Margin = new Thickness(4, 4, 0, 4) };
        readonly ComboBox mappingBox = new ComboBox { Width = 140, Margin = new Thickness(4, 4, 0, 4) };
        readonly NumericUpDown downBox = Number(0, 32, "Steps down when editing");
        readonly NumericUpDown rightBox = Number(0, 32, "Steps right when editing");
        readonly CheckBox midiEditBox = new CheckBox { Content = "Midi Edit", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly CheckBox midiVelocityBox = new CheckBox { Content = "Midi Velocity", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly ToggleButton playNotesToggle = SmallToggle("♪", "Play Notes (Alt+N)");
        readonly Slider zoom = new Slider { Width = 100, Minimum = 0, Maximum = 20, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly ToggleButton helpToggle = SmallToggle("Help", "Show Help (F1)");
        readonly Control[] mappingControls;

        MachineVM machine;
        PatternVM pattern;
        bool updating;

        /// <summary>The grid (for tests and for the studio's focus).</summary>
        public PatternGrid TrackerGrid => grid;

        public PatternEditor Editor => editor;

        /// <summary>The editor's undo stack and clipboard commands, for the studio's Edit menu.</summary>
        public IEditContext EditContext => editor.EditContext;

        public PatternEditorView(PatternEditor editor)
        {
            this.editor = editor;
            grid = new PatternGrid(editor);
            Background = PatternTheme.Background;
            Focusable = false;

            var create = SmallButton("+", "Create Pattern (Ctrl+Return)", editor.CreatePatternCommand);
            var clone = SmallButton("C", "Clone Pattern (Ctrl+Shift+Return)", editor.ClonePatternCommand);
            var delete = SmallButton("-", "Delete Pattern (Ctrl+Del)", editor.DeletePatternCommand);
            mappingControls = new Control[] { rootBox, mappingBox, Label("Down:"), downBox, Label("Right:"), rightBox, midiEditBox, midiVelocityBox };
            toolbar.Children.AddRange(new Control[]
            {
                playButton, create, clone, delete,
                Label("Beats:"), beatsBox, Label("Tracks:"), tracksBox, Label("Wave:"), waveBox,
                Label("Base Octave:"), octaveBox, mappingToggle,
            });
            toolbar.Children.AddRange(mappingControls);
            toolbar.Children.AddRange(new Control[] { playNotesToggle, zoom });

            var bar = new DockPanel { Background = PatternTheme.ToolbarBackground };
            DockPanel.SetDock(helpToggle, Dock.Right);
            helpToggle.Margin = new Thickness(8, 4);
            helpToggle.Width = 40;
            bar.Children.Add(helpToggle);
            bar.Children.Add(toolbar);

            var statusBar = new DockPanel { Height = 20, Background = PatternTheme.ToolbarBackground, LastChildFill = true };
            var s1 = new Border { Child = status1, BorderBrush = PatternTheme.ElementBorder, BorderThickness = new Thickness(0, 0, 1, 0) };
            var s2 = new Border { Child = status2, BorderBrush = PatternTheme.ElementBorder, BorderThickness = new Thickness(0, 0, 1, 0) };
            DockPanel.SetDock(s1, Dock.Left);
            DockPanel.SetDock(s2, Dock.Left);
            statusBar.Children.Add(s1);
            statusBar.Children.Add(s2);
            statusBar.Children.Add(status3);

            help = new Border
            {
                Width = 360,
                Margin = new Thickness(0, 4, 4, 4),
                Background = Brushes.White,
                BorderBrush = PatternTheme.ElementBorder,
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer { Content = HelpText() },
                IsVisible = false,
            };

            var main = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(statusBar, Dock.Bottom);
            DockPanel.SetDock(help, Dock.Right);
            main.Children.Add(bar);
            main.Children.Add(statusBar);
            main.Children.Add(help);
            main.Children.Add(grid);
            Content = main;

            Wire();
            editor.PropertyChanged += Editor_PropertyChanged;
            editor.Activated += () => Dispatcher.UIThread.Post(() => grid.FocusGrid());
            editor.Control.PropertiesRequested += () => _ = PatternPropertiesDialog.Show(this, editor);
            editor.Control.ViewRequest += request =>
            {
                switch (request)
                {
                    case "BaseOctave": octaveBox.IsDropDownOpen = true; break;
                    case "RootNote": rootBox.IsDropDownOpen = true; break;
                    case "KeyboardMapping": mappingBox.IsDropDownOpen = true; break;
                }
            };
            Follow(editor.SelectedMachine);
            UpdateAll();
        }

        public void Dispose()
        {
            editor.PropertyChanged -= Editor_PropertyChanged;
            Follow(null);
            grid.Release();
        }

        void Wire()
        {
            playButton.IsCheckedChanged += (s, e) =>
            {
                if (updating || pattern?.Pattern == null) return;
                pattern.Pattern.IsPlayingSolo = playButton.IsChecked == true;
            };
            beatsBox.ValueChanged += (s, e) =>
            {
                if (updating || pattern == null || e.NewValue == null) return;
                pattern.UndoableBeatCount = (int)e.NewValue.Value;
            };
            tracksBox.ValueChanged += (s, e) =>
            {
                if (updating || machine == null || e.NewValue == null) return;
                machine.UndoableTrackCount = (int)e.NewValue.Value;
            };
            waveBox.SelectionChanged += (s, e) =>
            {
                if (updating) return;
                editor.SelectedWave = waveBox.SelectedItem as WaveVM;
            };
            octaveBox.SelectionChanged += (s, e) =>
            {
                if (updating || machine?.Machine == null || octaveBox.SelectedItem is not int o) return;
                machine.Machine.BaseOctave = o;
            };
            mappingToggle.IsCheckedChanged += (s, e) => { if (!updating) editor.IsKeyboardMappingVisible = mappingToggle.IsChecked == true; };
            rootBox.ItemsSource = editor.RootNotes.ToArray();
            rootBox.SelectionChanged += (s, e) => { if (!updating && rootBox.SelectedIndex >= 0) editor.SelectedRootNote = rootBox.SelectedIndex; };
            mappingBox.ItemsSource = editor.KeyboardMappings?.Select(m => m.DisplayName).ToArray() ?? Array.Empty<string>();
            mappingBox.SelectionChanged += (s, e) =>
            {
                if (updating || editor.KeyboardMappings == null || mappingBox.SelectedIndex < 0) return;
                editor.SelectedKeyboardMapping = editor.KeyboardMappings.ElementAt(mappingBox.SelectedIndex);
            };
            downBox.ValueChanged += (s, e) => { if (!updating && e.NewValue != null) editor.SelectedStepsDown = (int)e.NewValue.Value; };
            rightBox.ValueChanged += (s, e) => { if (!updating && e.NewValue != null) editor.SelectedStepsRight = (int)e.NewValue.Value; };
            midiEditBox.IsCheckedChanged += (s, e) => { if (!updating) editor.MidiEdit = midiEditBox.IsChecked == true; };
            midiVelocityBox.IsCheckedChanged += (s, e) => { if (!updating) editor.MidiVelocity = midiVelocityBox.IsChecked == true; };
            playNotesToggle.IsCheckedChanged += (s, e) => { if (!updating) editor.PlayNotes = playNotesToggle.IsChecked == true; };
            zoom.ValueChanged += (s, e) =>
            {
                if (updating) return;
                editor.ScaleFactor = zoom.Value;
                grid.Relayout();
            };
            helpToggle.IsCheckedChanged += (s, e) => { if (!updating) editor.IsHelpVisible = helpToggle.IsChecked == true; };
        }

        void Editor_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "SelectedMachine")
                Follow(editor.SelectedMachine);
            Dispatcher.UIThread.Post(UpdateAll);
        }

        /// <summary>Follows the machine and the pattern shown (WPF binds to SelectedMachine.SelectedPattern).</summary>
        void Follow(MachineVM m)
        {
            if (machine != null)
            {
                machine.PropertyChanged -= Machine_PropertyChanged;
                if (machine.Machine != null) machine.Machine.PropertyChanged -= TargetMachine_PropertyChanged;
            }
            machine = m;
            if (machine != null)
            {
                machine.PropertyChanged += Machine_PropertyChanged;
                if (machine.Machine != null) machine.Machine.PropertyChanged += TargetMachine_PropertyChanged;
            }
            FollowPattern(machine?.SelectedPattern);
        }

        void FollowPattern(PatternVM p)
        {
            if (pattern != null)
            {
                pattern.PropertyChanged -= Pattern_PropertyChanged;
                if (pattern.Pattern != null) pattern.Pattern.PropertyChanged -= IPattern_PropertyChanged;
            }
            pattern = p;
            if (pattern != null)
            {
                pattern.PropertyChanged += Pattern_PropertyChanged;
                if (pattern.Pattern != null) pattern.Pattern.PropertyChanged += IPattern_PropertyChanged;
            }
        }

        void Machine_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "SelectedPattern" || e.PropertyName == "Machine")
            {
                if (e.PropertyName == "Machine") Follow(machine);
                else FollowPattern(machine.SelectedPattern);
            }
            Dispatcher.UIThread.Post(UpdateAll);
        }

        void TargetMachine_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "BaseOctave" || e.PropertyName == "TrackCount")
                Dispatcher.UIThread.Post(UpdateAll);
        }

        void Pattern_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Pattern") FollowPattern(pattern);
            if (e.PropertyName != "CursorPosition" && e.PropertyName != "Selection")
                Dispatcher.UIThread.Post(UpdateAll);
        }

        void IPattern_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsPlayingSolo" || e.PropertyName == "Length")
                Dispatcher.UIThread.Post(UpdateAll);
        }

        /// <summary>The toolbar and status bar from the editor's state (WPF: their bindings).</summary>
        void UpdateAll()
        {
            updating = true;
            try
            {
                bool hasPattern = pattern?.Pattern != null;
                playButton.IsEnabled = hasPattern;
                playButton.IsChecked = hasPattern && pattern.Pattern.IsPlayingSolo;
                beatsBox.IsEnabled = hasPattern;
                if (hasPattern) beatsBox.Value = pattern.BeatCount;
                var m = machine?.Machine;
                tracksBox.IsEnabled = machine != null && m?.DLL != null && machine.CanChangeTrackCount;
                if (m?.DLL != null)
                {
                    tracksBox.Minimum = machine.MinTracks;
                    tracksBox.Maximum = Math.Max(machine.MinTracks, machine.MaxTracks);
                    tracksBox.Value = m.TrackCount;
                    octaveBox.SelectedItem = m.BaseOctave;
                }
                var waves = editor.Waves.ToList();
                if (!waveBox.Items.Cast<object>().SequenceEqual(waves))
                    waveBox.ItemsSource = waves;
                waveBox.DisplayMemberBinding = new Avalonia.Data.Binding("Name");
                waveBox.SelectedItem = editor.SelectedWave;
                mappingToggle.IsChecked = editor.IsKeyboardMappingVisible;
                foreach (var c in mappingControls) c.IsVisible = editor.IsKeyboardMappingVisible;
                rootBox.SelectedIndex = editor.SelectedRootNote;
                if (editor.KeyboardMappings != null && editor.SelectedKeyboardMapping != null)
                    mappingBox.SelectedIndex = editor.KeyboardMappings.ToList().IndexOf(editor.SelectedKeyboardMapping);
                downBox.Value = editor.SelectedStepsDown;
                rightBox.Value = editor.SelectedStepsRight;
                downBox.IsEnabled = rightBox.IsEnabled = midiEditBox.IsEnabled = midiVelocityBox.IsEnabled = hasPattern;
                midiEditBox.IsChecked = editor.MidiEdit;
                midiVelocityBox.IsChecked = editor.MidiVelocity;
                playNotesToggle.IsChecked = editor.PlayNotes;
                zoom.Value = editor.ScaleFactor;
                helpToggle.IsChecked = editor.IsHelpVisible;
                help.IsVisible = editor.IsHelpVisible;
                status1.Text = editor.StatusBarItem1;
                status2.Text = editor.StatusBarItem2;
                status3.Text = editor.StatusBarItem3;
            }
            finally
            {
                updating = false;
            }
        }

        static TextBlock StatusText(double width) => new TextBlock
        {
            Width = width,
            Margin = new Thickness(4, 0),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = PatternTheme.StatusText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = PatternTheme.StatusText,
            FontSize = 12,
        };

        static NumericUpDown Number(int min, int max, string tip)
        {
            var n = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Increment = 1,
                FormatString = "0",
                Width = 96,
                Height = 26,
                Margin = new Thickness(4, 4, 0, 4),
                FontSize = 12,
            };
            ToolTip.SetTip(n, tip);
            return n;
        }

        static ToggleButton SmallToggle(string text, string tip)
        {
            var b = new ToggleButton { Content = text, FontSize = 11, Padding = new Thickness(6, 1), Margin = new Thickness(6, 4, 0, 4), Focusable = false, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(b, tip);
            return b;
        }

        static Button SmallButton(string text, string tip, System.Windows.Input.ICommand command)
        {
            var b = new Button { Content = text, FontSize = 11, Padding = new Thickness(6, 1), Margin = new Thickness(4, 4, 0, 4), Focusable = false, VerticalAlignment = VerticalAlignment.Center };
            b.Click += (s, e) =>
            {
                if (command.CanExecute(null)) command.Execute(null);
            };
            ToolTip.SetTip(b, tip);
            return b;
        }

        /// <summary>The keyboard commands (Help.xaml), as a table.</summary>
        static Control HelpText()
        {
            string[][] rows =
            {
                new[] { "F1", "Show/hide help" },
                new[] { "Ctrl+Z/Y", "Undo/Redo" },
                new[] { "Ctrl+X/C/V", "Cut/Copy/Paste" },
                new[] { "+/- (numpad)", "Select pattern" },
                new[] { "/ and * (numpad), Alt+B", "Select keyboard base octave" },
                new[] { "Alt+R", "Select keyboard root note" },
                new[] { "Alt+Y", "Select keyboard mapping" },
                new[] { "Alt+N", "Toggle Play Notes" },
                new[] { "Return", "Switch to sequence editor" },
                new[] { "Ctrl+Return", "Create pattern" },
                new[] { "Ctrl+Shift+Return", "Clone pattern" },
                new[] { "Ctrl+Del", "Delete pattern" },
                new[] { "Ctrl+Backspace", "Pattern properties" },
                new[] { "Arrows/PageUp/PageDown/Home/End/(Shift+)Tab", "Move cursor (+shift selects)" },
                new[] { ". (period)", "Clear field under cursor" },
                new[] { "Z,S,X,D,C,... (note column)", "Enter notes at base octave" },
                new[] { "Q,2,W,3,E,... (note column)", "Enter notes at base octave + 1" },
                new[] { "1 (note column)", "Enter note off" },
                new[] { "4 (note column)", "Play cursor row (current track)" },
                new[] { "8 (note column)", "Play cursor row (all tracks)" },
                new[] { "Insert/Delete", "Insert/Delete row" },
                new[] { "Ctrl+Shift+Insert/Delete", "Insert/Delete column" },
                new[] { "Ctrl+R", "Randomize" },
                new[] { "Ctrl+I", "Interpolate" },
                new[] { "Ctrl+Shift+I", "Interpolate logarithmic" },
                new[] { "Ctrl+W", "Rotate forward" },
                new[] { "Ctrl+Shift+W", "Rotate backwards" },
                new[] { "Ctrl+T", "Write machine state" },
                new[] { "Shift+Add/Subtract", "Shift values (transpose)" },
                new[] { "Ctrl+Shift+Add/Subtract", "Shift values by +12/-12" },
                new[] { "Ctrl+1,2,3...", "Beat subdivision" },
                new[] { "Ctrl+Shift+1,2,3...", "Beat subdivision + 10" },
                new[] { "Ctrl+1,2,3... (numpad)", "Step down" },
                new[] { "Ctrl+Alt+1,2,3... (numpad)", "Step right" },
            };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,2*"), Margin = new Thickness(4) };
            var title = new TextBlock { Text = "Keyboard Commands", FontWeight = FontWeight.Bold, Foreground = Brushes.Black, Background = Brushes.LightSteelBlue, Padding = new Thickness(2) };
            Grid.SetColumnSpan(title, 2);
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            g.Children.Add(title);
            int r = 1;
            foreach (var row in rows)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int c = 0; c < 2; c++)
                {
                    var t = new TextBlock { Text = row[c], FontSize = 12, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 1) };
                    Grid.SetRow(t, r);
                    Grid.SetColumn(t, c);
                    g.Children.Add(t);
                }
                r++;
            }
            return g;
        }
    }

    /// <summary>The grid's context menu (PatternControl.xaml): edits, undo, clipboard, selection, properties.</summary>
    internal static class PatternMenus
    {
        public static ContextMenu Create(PatternControl c)
        {
            var mod = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
            var items = new List<(MenuItem, System.Windows.Input.ICommand)>();
            MenuItem Item(string header, System.Windows.Input.ICommand command, string gesture)
            {
                var m = new MenuItem { Header = header, InputGesture = null };
                if (gesture != null) m.Header = header + "    " + gesture;
                m.Click += (s, e) =>
                {
                    if (command.CanExecute(null)) command.Execute(null);
                };
                items.Add((m, command));
                return m;
            }

            var edit = new MenuItem { Header = "Edit" };
            edit.Items.Add(Item("Decrease", c.DecreaseCommand, "- (numpad)"));
            edit.Items.Add(Item("Increase", c.IncreaseCommand, "+ (numpad)"));
            edit.Items.Add(Item("Interpolate (linear)", c.InterpolateLinearCommand, mod + "+I"));
            edit.Items.Add(Item("Interpolate (exponential)", c.InterpolateExpCommand, mod + "+Shift+I"));
            edit.Items.Add(Item("Randomize", c.RandomizeCommand, mod + "+R"));
            edit.Items.Add(Item("Rotate Forward", c.RotateCommandUp, mod + "+W"));
            edit.Items.Add(Item("Rotate Backward", c.RotateCommandDown, mod + "+Shift+W"));
            edit.Items.Add(Item("Write State", c.WriteStateCommand, mod + "+T"));
            edit.Items.Add(new Separator());
            edit.Items.Add(Item("Undo", c.UndoCommand, mod + "+Z"));
            edit.Items.Add(Item("Redo", c.RedoCommand, mod + "+Y"));
            edit.Items.Add(new Separator());
            edit.Items.Add(Item("Cut", c.CutCommand, mod + "+X"));
            edit.Items.Add(Item("Copy", c.CopyCommand, mod + "+C"));
            edit.Items.Add(Item("Paste", c.PasteCommand, mod + "+V"));

            var menu = new ContextMenu();
            menu.Items.Add(edit);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Select All", c.SelectAllCommand, mod + "+A"));
            menu.Items.Add(Item("Unselect", c.UnselectCommand, mod + "+U"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Pattern Properties...", c.PropertiesCommand, mod + "+Backspace"));
            // The commands don't say when they can run (SimpleCommand off Windows); ask when the menu opens.
            menu.Opening += (s, e) =>
            {
                foreach (var (m, command) in items) m.IsEnabled = command.CanExecute(null);
            };
            return menu;
        }
    }
}
