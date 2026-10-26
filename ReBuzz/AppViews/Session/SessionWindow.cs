using ReBuzz.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReBuzz.AppViews.Session
{
    /// <summary>
    /// Window hosting the Session View (fork addition; spec 4.3, option A for 0.1–0.2).
    /// Opened with Ctrl+Tab or the toolbar's Session button; closing only hides it.
    /// </summary>
    internal sealed class SessionWindow : Window
    {
        internal SessionView View { get; }

        internal SessionWindow(ReBuzzCore buzz)
        {
            Title = "Session View";
            Width = 960;
            Height = 520;
            MinWidth = 420;
            MinHeight = 240;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = SessionView.Palette.Window;
            View = new SessionView(buzz);
            Content = View;

            PreviewKeyDown += (s, e) =>
            {
                // Ctrl+Tab toggles between the Session View and the main window.
                if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    Owner?.Activate();
                    e.Handled = true;
                }
            };
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Keep the instance (and its layout state); just hide.
            e.Cancel = true;
            Hide();
            Owner?.Activate();
        }
    }

    /// <summary>Minimal text prompt (WPF has no built-in input box).</summary>
    internal sealed class PromptWindow : Window
    {
        readonly TextBox box;

        PromptWindow(string title, string label, string initial)
        {
            Title = title;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            box = new TextBox { Text = initial ?? "", Width = 260, Margin = new Thickness(0, 4, 0, 10) };
            var ok = new Button { Content = "OK", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 76, IsCancel = true };
            ok.Click += (s, e) => { DialogResult = true; };

            Content = new StackPanel
            {
                Margin = new Thickness(14),
                Children =
                {
                    new TextBlock { Text = label },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
                }
            };
            Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
        }

        /// <summary>Returns the entered text, or null if cancelled.</summary>
        internal static string Ask(Window owner, string title, string label, string initial)
        {
            var w = new PromptWindow(title, label, initial) { Owner = owner };
            return w.ShowDialog() == true ? w.box.Text : null;
        }
    }
}
