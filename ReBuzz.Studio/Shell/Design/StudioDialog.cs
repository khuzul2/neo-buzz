// Fork (docs/specs/ux-overhaul.md P4a): one dialog. Every question, sheet and small form in the studio is made of these: the same
// window (fixed size from its content, no taskbar entry, over its owner), the same 20 pt margin and 12 pt rhythm, buttons 88 pt
// wide in the platform's order (the default at the right on a Mac, first elsewhere), the default answer in the one blue and given to
// Return, Cancel given to Escape.

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia;

namespace ReBuzz.Studio.Shell.Design
{
    using Control = Avalonia.Controls.Control;

    internal static class StudioDialog
    {
        public const double Margin = 20;
        public const double Spacing = 12;
        public const double ButtonSpacing = 8;

        /// <summary>The dialog's window: sized by its content, never resized, not in the taskbar.</summary>
        public static Window Create(string title) => new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
        };

        /// <summary>The content: the controls one under the other inside the dialog's margin.</summary>
        public static StackPanel Body(params Control[] children)
        {
            var panel = new StackPanel { Margin = new Thickness(Margin), Spacing = Spacing };
            foreach (var c in children) panel.Children.Add(c);
            return panel;
        }

        /// <summary>The default answer: Return gives it, drawn in the accent.</summary>
        public static Button Primary(string label = "OK")
        {
            var b = new Button { Content = label, IsDefault = true };
            b.Classes.Add("dialog");
            b.Classes.Add("accent");
            return b;
        }

        /// <summary>An answer that is not the default; <paramref name="cancel"/>: Escape gives it.</summary>
        public static Button Secondary(string label = "Cancel", bool cancel = true)
        {
            var b = new Button { Content = label, IsCancel = cancel };
            b.Classes.Add("dialog");
            return b;
        }

        /// <summary>The row of buttons at the bottom right, in the platform's order (<paramref name="buttons"/> as the elsewhere order).</summary>
        public static StackPanel Buttons(bool mac, params Button[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = ButtonSpacing };
            foreach (var b in mac ? buttons.Reverse() : buttons) row.Children.Add(b);
            return row;
        }

        public static StackPanel Buttons(params Button[] buttons) => Buttons(OperatingSystem.IsMacOS(), buttons);

        /// <summary>Shows the window over the active window (centred on the screen without one) and completes when it closes.</summary>
        public static async Task Run(Window window)
        {
            var owner = Dialogs.Owner;
            window.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            if (owner != null) await window.ShowDialog(owner);
            else
            {
                var closed = new TaskCompletionSource();
                window.Closed += (s, e) => closed.TrySetResult();
                window.Show();
                await closed.Task;
            }
        }
    }
}
