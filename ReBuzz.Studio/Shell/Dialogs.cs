// Fork (docs/specs/studio-mvp.md): the studio's message boxes, built on Avalonia windows (it has
// none of its own). Async for the studio's own flows; Modal() runs one to its answer for the
// engine's synchronous questions (IUserMessages.Ask), in a nested dispatcher frame.

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReBuzz.Core;

namespace ReBuzz.Studio.Shell
{
    internal static class Dialogs
    {
        /// <summary>
        /// Tests answer here instead of a window: (message, caption, buttons) → the answer, or
        /// null to show the window after all.
        /// </summary>
        public static Func<string, string, UserButtons, UserAnswer?> Responder { get; set; }

        /// <summary>The window dialogs belong to: the active one, else the main window.</summary>
        public static Window Owner =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault(w => w.IsActive)
            ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        public static async Task<UserAnswer> Ask(string message, string caption, UserButtons buttons)
        {
            if (Responder?.Invoke(message, caption, buttons) is UserAnswer answered) return answered;
            var owner = Owner;
            var choices = buttons switch
            {
                UserButtons.OkCancel => new[] { UserAnswer.Ok, UserAnswer.Cancel },
                UserButtons.YesNoCancel => new[] { UserAnswer.Yes, UserAnswer.No, UserAnswer.Cancel },
                UserButtons.YesNo => new[] { UserAnswer.Yes, UserAnswer.No },
                _ => new[] { UserAnswer.Ok },
            };
            var cancel = choices.Contains(UserAnswer.Cancel) ? UserAnswer.Cancel : choices.Last();
            var window = new Window
            {
                Title = caption,
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
            };
            var answer = cancel;
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            foreach (var choice in choices)
            {
                var b = new Button { Content = choice.ToString(), MinWidth = 72, IsDefault = choice == choices[0], IsCancel = choice == cancel };
                b.Click += (s, e) => { answer = choice; window.Close(); };
                row.Children.Add(b);
            }
            window.Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                MaxWidth = 520,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, row },
            };
            if (owner != null) await window.ShowDialog(owner);
            else
            {
                var closed = new TaskCompletionSource();
                window.Closed += (s, e) => closed.TrySetResult();
                window.Show();
                await closed.Task;
            }
            return answer;
        }

        public static Task Info(string message, string caption) => Ask(message, caption, UserButtons.Ok);

        /// <summary>Runs a dialog to its end on the UI thread, for callers that can't await.</summary>
        public static T Modal<T>(Func<Task<T>> dialog)
        {
            var task = dialog();
            if (task.IsCompleted) return task.Result;
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.UIThread.PushFrame(frame);
            return task.Result;
        }
    }
}
