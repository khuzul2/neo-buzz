using BespokeFusion;
using ReBuzz.Common;
using System;
using System.Windows;

namespace ReBuzz.Core
{
    public class UserMessagesViaMessageBox : IUserMessages
    {
        public void Error(string message, string caption, Exception exception)
        {
            // Fork (agent server): no dialogs during an agent call or in headless mode.
            if (ReBuzz.Agent.NonModal.TryCapture(message, caption)) return;
            Show(message, caption, MessageBoxButton.OK);
        }

        public void Info(string message, string caption)
        {
            if (ReBuzz.Agent.NonModal.TryCapture(message, caption)) return;
            Show(message, caption, MessageBoxButton.OK);
        }

        public UserAnswer Ask(string message, string caption, UserButtons buttons)
        {
            if (ReBuzz.Agent.NonModal.TryCapture(message, caption, buttons, out var captured)) return captured;
            return (UserAnswer)Show(message, caption, (MessageBoxButton)buttons);
        }

        public bool KeepWaiting(string message, string caption)
        {
            if (ReBuzz.Agent.NonModal.TryCapture(message, caption, UserButtons.YesNo, out var captured)) return captured == UserAnswer.Yes;
            return MessageBoxWindow.ShowYesNoWindow(caption, message) == MessageBoxResult.Yes;
        }

        /// <summary>
        /// A message box. As <see cref="UiUtils.MessageBox"/> did, the main window stays on top
        /// while it is open, when the calling thread owns the window; otherwise (start-up, other
        /// threads) a plain message box.
        /// </summary>
        static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons)
        {
            var window = UiUtils.AppWindow;
            if (window == null || !window.Dispatcher.CheckAccess())
                return MessageBox.Show(message, caption, buttons);

            window.Topmost = true;
            try
            {
                return MessageBox.Show(message, caption, buttons);
            }
            finally
            {
                window.Topmost = false;
            }
        }
    }
}
