using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ReBuzz.AppViews.Session
{
    /// <summary>
    /// Sets up the MIDI note grid (fork addition, docs/specs/session-view.md 0.2): which block of
    /// notes from a pad controller maps onto which block of slots.
    /// </summary>
    internal sealed class NoteGridWindow : Window
    {
        internal readonly record struct Result(NoteGrid Grid, bool Remove);

        readonly IBuzz buzz;
        readonly TextBox firstNote;
        readonly ComboBox channel;
        readonly ComboBox columns;
        readonly ComboBox rows;
        readonly CheckBox bottomUp;
        readonly TextBlock noteName;
        readonly Button learn;
        readonly int originTrack;
        readonly int originScene;
        bool learning;
        Result result;

        NoteGridWindow(IBuzz buzz, NoteGrid current, int originTrack, int originScene)
        {
            this.buzz = buzz;
            this.originTrack = current?.OriginTrack ?? originTrack;
            this.originScene = current?.OriginScene ?? originScene;
            Title = "MIDI note grid";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var g = current ?? new NoteGrid { OriginTrack = originTrack, OriginScene = originScene };

            firstNote = new TextBox { Width = 48, Text = g.FirstNote.ToString(CultureInfo.InvariantCulture) };
            firstNote.TextChanged += (s, e) => UpdateNoteName();
            noteName = new TextBlock { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 36 };
            learn = new Button { Content = "Learn", Padding = new Thickness(8, 1, 8, 1) };
            learn.Click += (s, e) => SetLearning(!learning);

            channel = new ComboBox { Width = 70 };
            channel.Items.Add("Any");
            for (int i = 1; i <= 16; i++) channel.Items.Add(i.ToString(CultureInfo.InvariantCulture));
            channel.SelectedIndex = g.Channel < 0 ? 0 : g.Channel + 1;

            columns = Numbers(1, 16, g.Columns);
            rows = Numbers(1, 16, g.Rows);
            bottomUp = new CheckBox { Content = "First note is the bottom-left pad (most pad controllers)", IsChecked = g.BottomUp };

            var form = new Grid { Margin = new Thickness(14, 14, 14, 8) };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddRow(form, 0, "First note", new StackPanel { Orientation = Orientation.Horizontal, Children = { firstNote, noteName, learn } });
            AddRow(form, 1, "MIDI channel", channel);
            AddRow(form, 2, "Columns (tracks)", columns);
            AddRow(form, 3, "Rows (scenes)", rows);
            var check = bottomUp;
            Grid.SetRow(check, 4);
            Grid.SetColumnSpan(check, 2);
            check.Margin = new Thickness(0, 8, 0, 0);
            form.RowDefinitions.Add(new RowDefinition());
            form.Children.Add(check);

            var origin = new TextBlock
            {
                Margin = new Thickness(14, 0, 14, 10),
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
                Text = $"The block starts at track {this.originTrack + 1}, scene {this.originScene + 1}" +
                       (current == null ? " (the selected slot)." : ". To move it, select another slot and set the grid up again.") +
                       " Notes in the grid control the session only; they don't reach machines."
            };

            var ok = new Button { Content = "OK", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (s, e) => { if (TryRead(out var grid)) { result = new Result(grid, false); DialogResult = true; } };
            var remove = new Button { Content = "Remove grid", Width = 96, Margin = new Thickness(0, 0, 8, 0), IsEnabled = current != null };
            remove.Click += (s, e) => { result = new Result(null, true); DialogResult = true; };
            var cancel = new Button { Content = "Cancel", Width = 76, IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 14, 14), Children = { remove, ok, cancel } };

            Content = new StackPanel { Children = { form, origin, buttons } };
            UpdateNoteName();
            Closed += (s, e) => SetLearning(false);
        }

        static ComboBox Numbers(int from, int to, int value)
        {
            var box = new ComboBox { Width = 70 };
            for (int i = from; i <= to; i++) box.Items.Add(i.ToString(CultureInfo.InvariantCulture));
            box.SelectedIndex = Math.Clamp(value, from, to) - from;
            return box;
        }

        static void AddRow(Grid form, int row, string label, UIElement control)
        {
            form.RowDefinitions.Add(new RowDefinition());
            var text = new TextBlock { Text = label, Margin = new Thickness(0, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            form.Children.Add(text);
            if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            form.Children.Add(control);
        }

        void UpdateNoteName()
        {
            noteName.Text = int.TryParse(firstNote.Text, out int n) && n >= 0 && n <= 127 ? MidiControl.NoteName(n) : "?";
        }

        bool TryRead(out NoteGrid grid)
        {
            grid = null;
            if (!int.TryParse(firstNote.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int note) || note < 0 || note > 127)
            {
                MessageBox.Show(this, "The first note must be a number from 0 to 127 (C-1 to G9); middle C is 60.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            grid = new NoteGrid
            {
                FirstNote = note,
                Channel = channel.SelectedIndex - 1,
                Columns = columns.SelectedIndex + 1,
                Rows = rows.SelectedIndex + 1,
                BottomUp = bottomUp.IsChecked == true,
                OriginTrack = originTrack,
                OriginScene = originScene,
            };
            return true;
        }

        void SetLearning(bool on)
        {
            if (on == learning) return;
            learning = on;
            if (on) buzz.MIDIInput += OnMidi; else buzz.MIDIInput -= OnMidi;
            learn.Content = on ? "Press a pad…" : "Learn";
        }

        void OnMidi(int data)
        {
            if (!MidiControl.TryDecode(data, out var control, out _, out bool press) || !press || control.Type != MidiControlType.Note) return;
            firstNote.Text = control.Number.ToString(CultureInfo.InvariantCulture);
            channel.SelectedIndex = control.Channel + 1;
            SetLearning(false);
        }

        /// <summary>Shows the dialog. Grid is null and Remove false when cancelled.</summary>
        internal static Result Ask(Window owner, IBuzz buzz, NoteGrid current, int originTrack, int originScene)
        {
            var w = new NoteGridWindow(buzz, current, originTrack, originScene) { Owner = owner };
            return w.ShowDialog() == true ? w.result : default;
        }
    }
}
