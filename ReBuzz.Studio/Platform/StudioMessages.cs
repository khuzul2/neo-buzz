// Fork (docs/specs/studio-mvp.md §2): the engine's messages (IUserMessages) as the studio's
// dialogs. Errors and notes don't wait; questions do (a nested frame), as WPF's message boxes do.

using System;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Platform
{
    internal sealed class StudioMessages : IUserMessages
    {
        readonly Action<string> log;

        public StudioMessages(Action<string> log) => this.log = log;

        public void Error(string message, string caption, Exception exception)
        {
            log($"{caption}: {message}" + (exception != null ? $" ({exception.GetType().Name}: {exception.Message})" : ""));
            _ = Dialogs.Info(message, caption);
        }

        public void Info(string message, string caption)
        {
            log($"{caption}: {message}");
            _ = Dialogs.Info(message, caption);
        }

        public UserAnswer Ask(string message, string caption, UserButtons buttons) =>
            Dialogs.Modal(() => Dialogs.Ask(message, caption, buttons));
    }
}
