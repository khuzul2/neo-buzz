// Fork (docs/specs/studio-mvp.md): the studio's message boxes, built on Avalonia windows (it has
// none of its own). Async for the studio's own flows; Modal() runs one to its answer for the
// engine's synchronous questions (IUserMessages.Ask), in a nested dispatcher frame. Dangerous questions
// name their buttons and default to the safe answer (DialogChoices; UX review).

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

        public static Task<UserAnswer> Ask(string message, string caption, UserButtons buttons) => Ask(message, caption, buttons, null);

        /// <summary>
        /// A question with its own buttons (<see cref="DialogChoices"/>: verbs, the answer Return gives, the safe one Escape
        /// and closing give). Without them: Yes, OK first and the default; Escape and closing give Cancel, else No.
        /// </summary>
        public static async Task<UserAnswer> Ask(string message, string caption, UserButtons buttons, DialogChoices choices)
        {
            if (Responder?.Invoke(message, caption, buttons) is UserAnswer answered) return answered;
            var owner = Owner;
            var closed = new TaskCompletionSource<UserAnswer>();
            var window = Build(message, caption, buttons, choices, OperatingSystem.IsMacOS(), a => closed.TrySetResult(a));
            window.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            if (owner != null) await window.ShowDialog(owner);
            else window.Show();
            return await closed.Task;
        }

        /// <summary>The answers a kind of question offers, in the order given.</summary>
        internal static UserAnswer[] Answers(UserButtons buttons) => buttons switch
        {
            UserButtons.OkCancel => new[] { UserAnswer.Ok, UserAnswer.Cancel },
            UserButtons.YesNoCancel => new[] { UserAnswer.Yes, UserAnswer.No, UserAnswer.Cancel },
            UserButtons.YesNo => new[] { UserAnswer.Yes, UserAnswer.No },
            _ => new[] { UserAnswer.Ok },
        };

        /// <summary>
        /// The buttons' order, left to right: on a Mac the default rightmost and the safe one beside it, the others (Don't
        /// Save) to the left, as macOS's alerts have them; elsewhere the default first and the safe one last.
        /// </summary>
        internal static UserAnswer[] Order(UserAnswer[] answers, UserAnswer @default, UserAnswer safe, bool mac)
        {
            var others = answers.Where(a => a != @default && a != safe).ToList();
            var order = new System.Collections.Generic.List<UserAnswer>();
            if (mac)
            {
                order.AddRange(others);
                if (safe != @default) order.Add(safe);
                order.Add(@default);
            }
            else
            {
                order.Add(@default);
                order.AddRange(others);
                if (safe != @default) order.Add(safe);
            }
            return order.ToArray();
        }

        /// <summary>The question's window; <paramref name="answered"/> gets the answer as it closes (the safe one if closed otherwise).</summary>
        internal static Window Build(string message, string caption, UserButtons buttons, DialogChoices choices, bool mac, Action<UserAnswer> answered)
        {
            var answers = Answers(buttons);
            var safe = choices?.Safe is UserAnswer s0 && answers.Contains(s0) ? s0
                : answers.Contains(UserAnswer.Cancel) ? UserAnswer.Cancel : answers.Contains(UserAnswer.No) ? UserAnswer.No : answers.Last();
            var @default = choices?.Default is UserAnswer d0 && answers.Contains(d0) ? d0 : answers[0];
            var window = new Window
            {
                Title = caption,
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                ShowInTaskbar = false,
            };
            var answer = safe;
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            foreach (var choice in Order(answers, @default, safe, mac))
            {
                string label = choices?.Labels != null && choices.Labels.TryGetValue(choice, out var l) ? l : choice.ToString();
                var b = new Button { Content = label, MinWidth = 72, IsDefault = choice == @default, IsCancel = choice == safe, Tag = choice };
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
            window.Closed += (s, e) => answered(answer);
            return window;
        }

        public static Task Info(string message, string caption) => Ask(message, caption, UserButtons.Ok);

        /// <summary>Tests answer text questions here: (title, current) → the text, or null for cancel.</summary>
        public static Func<string, string, string> TextResponder { get; set; }

        /// <summary>A line of text (a machine's new name…); null when cancelled.</summary>
        public static async Task<string> AskText(string title, string current)
        {
            if (TextResponder != null) return TextResponder(title, current);
            var owner = Owner;
            var box = new TextBox { Text = current ?? "", MinWidth = 260 };
            string result = null;
            var window = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
            };
            var ok = new Button { Content = "OK", MinWidth = 72, IsDefault = true };
            var cancel = new Button { Content = "Cancel", MinWidth = 72, IsCancel = true };
            ok.Click += (s, e) => { result = box.Text; window.Close(); };
            cancel.Click += (s, e) => window.Close();
            window.Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children = { box, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } } },
            };
            window.Opened += (s, e) => { box.Focus(); box.SelectAll(); };
            if (owner != null) await window.ShowDialog(owner);
            else
            {
                var closed = new TaskCompletionSource();
                window.Closed += (s, e) => closed.TrySetResult();
                window.Show();
                await closed.Task;
            }
            return result;
        }

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
    /// <summary>
    /// Fork (UX review, mac-321): a question's own buttons. Every question made its first choice the default, so Return
    /// turned the safety limiter off; a dangerous question names its actions (Turn Off / Keep On), Return gives the safe
    /// one, and Escape (or closing the window) always gives the safe one.
    /// </summary>
    internal sealed class DialogChoices
    {
        /// <summary>A label for an answer ("Turn Off" for Yes).</summary>
        public System.Collections.Generic.Dictionary<UserAnswer, string> Labels { get; init; }

        /// <summary>The answer Return gives (the default button).</summary>
        public UserAnswer? Default { get; init; }

        /// <summary>The answer Escape and closing the window give.</summary>
        public UserAnswer? Safe { get; init; }

        /// <summary>Turning a safety off: Turn Off (Yes) or Keep On (No), Keep On on Return and Escape.</summary>
        public static DialogChoices TurnOff => new DialogChoices
        {
            Labels = new() { [UserAnswer.Yes] = "Turn Off", [UserAnswer.No] = "Keep On" },
            Default = UserAnswer.No,
            Safe = UserAnswer.No,
        };

        /// <summary>Unsaved changes: Save (Yes, on Return), Don't Save (No), Cancel (on Escape).</summary>
        public static DialogChoices SaveChanges => new DialogChoices
        {
            Labels = new() { [UserAnswer.Yes] = "Save", [UserAnswer.No] = "Don't Save", [UserAnswer.Cancel] = "Cancel" },
            Default = UserAnswer.Yes,
            Safe = UserAnswer.Cancel,
        };

        /// <summary>An autosave left over: Restore (Yes, on Return), Discard… (No, asked again), Decide Later (Cancel, on Escape).</summary>
        public static DialogChoices Recover => new DialogChoices
        {
            Labels = new() { [UserAnswer.Yes] = "Restore", [UserAnswer.No] = "Discard…", [UserAnswer.Cancel] = "Decide Later" },
            Default = UserAnswer.Yes,
            Safe = UserAnswer.Cancel,
        };

        /// <summary>Deleting: Delete (Yes) or Cancel (No), Cancel on Return and Escape.</summary>
        public static DialogChoices Delete => new DialogChoices
        {
            Labels = new() { [UserAnswer.Yes] = "Delete", [UserAnswer.No] = "Cancel" },
            Default = UserAnswer.No,
            Safe = UserAnswer.No,
        };
    }
}
