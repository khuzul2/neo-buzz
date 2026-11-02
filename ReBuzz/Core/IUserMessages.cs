using System;

namespace ReBuzz.Core
{
    /// <summary>
    /// Messages and questions for the user (engine-split.md S1). The engine never shows a dialog
    /// itself: the WPF app shows message boxes, the agent server captures them, tests record them.
    /// </summary>
    public interface IUserMessages
    {
        void Error(string message, string caption, Exception exception);

        /// <summary>A message with an OK button.</summary>
        void Info(string message, string caption);

        /// <summary>A question; returns the button the user chose.</summary>
        UserAnswer Ask(string message, string caption, UserButtons buttons);

        /// <summary>
        /// A long operation (loading machines) is still running: keep waiting? The WPF app shows
        /// its own Yes/No window here; the default asks a Yes/No question.
        /// </summary>
        bool KeepWaiting(string message, string caption) => Ask(message, caption, UserButtons.YesNo) == UserAnswer.Yes;
    }

    /// <summary>The buttons of a question. Same names and values as WPF's MessageBoxButton.</summary>
    public enum UserButtons
    {
        Ok = 0,
        OkCancel = 1,
        YesNoCancel = 3,
        YesNo = 4,
    }

    /// <summary>The user's answer. Same names and values as WPF's MessageBoxResult.</summary>
    public enum UserAnswer
    {
        Ok = 1,
        Cancel = 2,
        Yes = 6,
        No = 7,
    }
}
