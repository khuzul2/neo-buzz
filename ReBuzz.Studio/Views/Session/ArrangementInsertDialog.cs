// Fork (docs/specs/studio-mvp.md S5): asks where and how long to put a scene into the arrangement
// (session-view.md 0.3), as the WPF view's ArrangementInsertWindow does.

using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views.Session
{
    using Control = Avalonia.Controls.Control;

    internal static class ArrangementInsertDialog
    {
        internal readonly record struct Answer(int Bar, int Bars, bool Insert);

        /// <summary>Tests answer here instead of a window: (scene, start bar, length in bars) → the answer, or null for cancel.</summary>
        public static Func<string, int, int, Answer?> Responder { get; set; }

        /// <summary>Shows the dialog; null when cancelled.</summary>
        public static async Task<Answer?> Ask(string sceneName, int startBar, int lengthBars)
        {
            startBar = Math.Max(1, startBar);
            lengthBars = Math.Max(1, lengthBars);
            if (Responder != null) return Responder(sceneName, startBar, lengthBars);

            var bar = new TextBox { Width = 72, Text = startBar.ToString(CultureInfo.InvariantCulture) };
            var bars = new TextBox { Width = 72, Text = lengthBars.ToString(CultureInfo.InvariantCulture) };
            var overwrite = new RadioButton { Content = "Overwrite: replace what the scene's tracks have there", IsChecked = true, GroupName = "mode" };
            var insert = new RadioButton { Content = "Insert: move everything from that bar on back to make room", GroupName = "mode" };
            var problem = new TextBlock { Foreground = ReBuzz.Studio.Shell.Design.StudioTokens.ErrorTextBrush, IsVisible = false, Text = "The bar and the length must be whole numbers of 1 or more." };
            Answer? answer = null;

            var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 6, ColumnSpacing = 12 };
            void Row(int row, string label, Control control)
            {
                var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(text, row);
                form.Children.Add(text);
                Grid.SetRow(control, row);
                Grid.SetColumn(control, 1);
                form.Children.Add(control);
            }
            Row(0, "At bar", bar);
            Row(1, "Length (bars)", bars);

            var window = ReBuzz.Studio.Shell.Design.StudioDialog.Create("Insert scene into arrangement");
            var ok = ReBuzz.Studio.Shell.Design.StudioDialog.Primary("Insert");
            var cancel = ReBuzz.Studio.Shell.Design.StudioDialog.Secondary();
            ok.Click += (s, e) =>
            {
                if (!int.TryParse(bar.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) || b < 1 ||
                    !int.TryParse(bars.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 4096)
                {
                    problem.IsVisible = true;
                    return;
                }
                answer = new Answer(b, n, insert.IsChecked == true);
                window.Close();
            };
            cancel.Click += (s, e) => window.Close();
            window.Content = ReBuzz.Studio.Shell.Design.StudioDialog.Body(
                new TextBlock
                {
                    Text = $"Writes the clips of {sceneName} into their tracks' sequences, repeated to fill the length. Undo with ↶ in the Session View.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 380
                },
                form,
                new StackPanel { Spacing = 4, Children = { overwrite, insert } },
                problem,
                ReBuzz.Studio.Shell.Design.StudioDialog.Buttons(ok, cancel));
            window.Opened += (s, e) => { bar.Focus(); bar.SelectAll(); };
            await ReBuzz.Studio.Shell.Design.StudioDialog.Run(window);
            return answer;
        }
    }
}
