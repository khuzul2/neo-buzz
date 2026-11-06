// Fork (docs/specs/studio-mvp.md S6): the pattern properties (PatternPropertiesWindow on WPF): the
// pattern's name, length in beats and rows per beat, and its columns: for each machine in the song,
// the editor's MIDI lanes and the machine's parameters, ticked when the pattern has them. OK applies
// them as one undo step (PatternEditor.ApplyPatternProperties).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using Control = Avalonia.Controls.Control;

namespace WDE.ModernPatternEditor.AvaloniaView
{
    // Avalonia's, not the portable editor's stand-in (PortableShims.cs).
    using Color = Avalonia.Media.Color;

    internal sealed class PatternPropertiesDialog : Window
    {
        readonly PatternEditor editor;
        readonly TextBox nameBox = new TextBox { Width = 160 };
        readonly ComboBox beatsBox = new ComboBox { Width = 90, ItemsSource = Enumerable.Range(1, 256).ToArray() };
        readonly ComboBox rpbBox = new ComboBox { Width = 90, ItemsSource = Enumerable.Range(2, 15).ToArray() };
        readonly List<(CheckBox Box, IParameter Parameter)> parameters = new();

        PatternPropertiesDialog(PatternEditor editor)
        {
            this.editor = editor;
            Title = "Pattern Properties";
            Width = 420;
            Height = 520;
            CanResize = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var vm = editor.SelectedMachine.SelectedPattern;
            nameBox.Text = vm.Name;
            beatsBox.SelectedItem = Math.Min(256, vm.BeatCount);
            rpbBox.SelectedItem = editor.MPEPatternsDB.GetMPEPattern(vm.Pattern).RowsPerBeat;

            var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(8) };
            void Field(int row, string label, Control c)
            {
                var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
                Grid.SetRow(t, row);
                Grid.SetRow(c, row);
                Grid.SetColumn(c, 1);
                c.Margin = new Thickness(0, 4);
                c.HorizontalAlignment = HorizontalAlignment.Left;
                fields.Children.Add(t);
                fields.Children.Add(c);
            }
            Field(0, "Name", nameBox);
            Field(1, "Length (beats)", beatsBox);
            Field(2, "Rows per beat", rpbBox);

            var tree = new StackPanel { Margin = new Thickness(8, 0) };
            foreach (var mac in Global.Buzz.Song.Machines)
            {
                // The hidden pattern editors. Ordinal (fork, ui-gallery.md): the culture's comparison ignores control
                // characters, so every name "started with" U+0001 and the list was empty on macOS and Linux.
                if (mac.Name.StartsWith('\u0001')) continue;
                tree.Children.Add(new TextBlock { Text = mac.Name, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 2) });
                AddGroup(tree, "Pattern Editor", PatternEditorUtils.GetInternalParameters(mac));
                AddGroup(tree, "Parameters", mac.ParameterGroups[1].Parameters.Concat(mac.ParameterGroups[2].Parameters));
            }

            var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(4) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80, Margin = new Thickness(4) };
            ok.Click += (s, e) => Close(true);
            cancel.Click += (s, e) => Close(false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8), Children = { ok, cancel } };

            var root = new DockPanel();
            DockPanel.SetDock(fields, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(fields);
            root.Children.Add(buttons);
            root.Children.Add(new ScrollViewer { Content = tree });
            Content = root;
        }

        void AddGroup(StackPanel tree, string name, IEnumerable<IParameter> group)
        {
            var list = group.ToList();
            if (list.Count == 0) return;
            tree.Children.Add(new TextBlock { Text = name, Margin = new Thickness(12, 2, 0, 0), Foreground = Brushes.Gray });
            foreach (var p in list)
            {
                var box = new CheckBox { Content = p.Name, IsChecked = editor.HasColumn(p), Margin = new Thickness(24, 0, 0, 0) };
                tree.Children.Add(box);
                parameters.Add((box, p));
            }
        }

        /// <summary>Asks for the properties and applies them (WPF: PatternEditor.ShowPatternProperties).</summary>
        public static async Task Show(Control owner, PatternEditor editor)
        {
            if (editor.SelectedMachine?.SelectedPattern?.Pattern == null) return;
            var dialog = new PatternPropertiesDialog(editor);
            bool? result = TopLevel.GetTopLevel(owner) is Window window
                ? await dialog.ShowDialog<bool?>(window)
                : null;
            if (result != true) return;
            dialog.Apply();
        }

        void Apply()
        {
            int beats = beatsBox.SelectedItem is int b ? b : editor.SelectedMachine.SelectedPattern.BeatCount;
            int rpb = rpbBox.SelectedItem is int r ? r : 4;
            var chosen = parameters.Where(x => x.Box.IsChecked == true).Select(x => x.Parameter).ToList();
            editor.ApplyPatternProperties(beats, rpb, nameBox.Text, chosen);
        }
    }
}
