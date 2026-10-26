using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ReBuzz.AppViews.Session
{
    /// <summary>
    /// Asks where and how long to put a scene into the arrangement (fork addition,
    /// docs/specs/session-view.md 0.3).
    /// </summary>
    internal sealed class ArrangementInsertWindow : Window
    {
        internal readonly record struct Answer(int Bar, int Bars, bool Insert);

        readonly TextBox bar;
        readonly TextBox bars;
        readonly RadioButton insert;
        Answer? answer;

        ArrangementInsertWindow(string sceneName, int startBar, int lengthBars)
        {
            Title = "Insert scene into arrangement";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            bar = new TextBox { Width = 60, Text = startBar.ToString(CultureInfo.InvariantCulture) };
            bars = new TextBox { Width = 60, Text = lengthBars.ToString(CultureInfo.InvariantCulture) };
            var overwrite = new RadioButton { Content = "Overwrite: replace what the scene's tracks have there", IsChecked = true, Margin = new Thickness(0, 6, 0, 2) };
            insert = new RadioButton { Content = "Insert: move everything from that bar on back to make room", Margin = new Thickness(0, 2, 0, 0) };

            var form = new Grid { Margin = new Thickness(14, 14, 14, 6) };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddRow(form, 0, "At bar", bar);
            AddRow(form, 1, "Length (bars)", bars);

            var intro = new TextBlock
            {
                Text = $"Writes the clips of {sceneName} into their tracks' sequences, repeated to fill the length. Undo with ↶ in the Session View.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                Margin = new Thickness(14, 14, 14, 0)
            };
            var modes = new StackPanel { Margin = new Thickness(14, 0, 14, 12), Children = { overwrite, insert } };

            var ok = new Button { Content = "Insert", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (s, e) => { if (TryRead()) DialogResult = true; };
            var cancel = new Button { Content = "Cancel", Width = 76, IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 14, 14), Children = { ok, cancel } };

            Content = new StackPanel { Children = { intro, form, modes, buttons } };
            Loaded += (s, e) => { bar.Focus(); bar.SelectAll(); };
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

        bool TryRead()
        {
            if (!int.TryParse(bar.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) || b < 1 ||
                !int.TryParse(bars.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 4096)
            {
                MessageBox.Show(this, "The bar and the length must be whole numbers of 1 or more.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            answer = new Answer(b, n, insert.IsChecked == true);
            return true;
        }

        /// <summary>Shows the dialog; null when cancelled.</summary>
        internal static Answer? Ask(Window owner, string sceneName, int startBar, int lengthBars)
        {
            var w = new ArrangementInsertWindow(sceneName, Math.Max(1, startBar), Math.Max(1, lengthBars)) { Owner = owner };
            return w.ShowDialog() == true ? w.answer : null;
        }
    }
}
