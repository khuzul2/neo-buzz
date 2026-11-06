// Fork (docs/specs/studio-mvp.md §2): the engine's messages (IUserMessages) as the studio's
// dialogs. Errors and notes don't wait; questions do (a nested frame), as WPF's message boxes do.
// During an agent call (S8) they become the call's error instead, as UserMessagesViaMessageBox's do.

using System;
using ReBuzz.Agent;
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
            if (NonModal.TryCapture(message, caption)) return;
            log($"{caption}: {message}" + (exception != null ? $" ({exception.GetType().Name}: {exception.Message})" : ""));
            if (exception != null) _ = Dialogs.Error(message, caption, ErrorDetails.Of(exception));     // the exception under Details
            else _ = Dialogs.Info(message, caption);
        }

        public void Info(string message, string caption)
        {
            if (NonModal.TryCapture(message, caption)) return;
            log($"{caption}: {message}");
            _ = Dialogs.Info(message, caption);
        }

        public UserAnswer Ask(string message, string caption, UserButtons buttons)
        {
            if (NonModal.TryCapture(message, caption, buttons, out var captured)) return captured;
            return Dialogs.Modal(() => Dialogs.Ask(message, caption, buttons));
        }
    }
}
