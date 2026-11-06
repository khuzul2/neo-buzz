using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReBuzz.Common
{
    /// <summary>
    /// Fork: an error with what happened in words and the exception under Details, closed at first
    /// (as the studio's Dialogs.Error). A song that can't be opened said only "Unable to read beyond
    /// the end of the stream." in a message box (gallery mac-338).
    /// </summary>
    internal sealed class ErrorDetailsWindow : Window
    {
        public ErrorDetailsWindow(string message, string caption, string details)
        {
            Title = caption;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            MessageText = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            DetailsBox = new TextBox
            {
                Text = details ?? "",
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Details = new Expander { Header = "Details", Content = DetailsBox, IsExpanded = false, Margin = new Thickness(0, 0, 0, 12) };
            var ok = new Button { Content = "OK", MinWidth = 72, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
            ok.Click += (s, e) => Close();
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Width = 480,
                Children = { MessageText, Details, ok },
            };
        }

        internal TextBlock MessageText { get; }
        internal Expander Details { get; }
        internal TextBox DetailsBox { get; }

        /// <summary>Shows it over the app's window (or alone at start-up), and waits.</summary>
        public static void Show(string message, string caption, string details)
        {
            var window = new ErrorDetailsWindow(message, caption, details);
            var owner = UiUtils.AppWindow;
            if (owner != null && owner.Dispatcher.CheckAccess() && owner.IsLoaded) window.Owner = owner;
            else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ShowDialog();
        }
    }
}
