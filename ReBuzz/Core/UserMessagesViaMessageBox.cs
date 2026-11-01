using System;
using System.Windows;

namespace ReBuzz.Core
{
    public interface IUserMessages
    {
        void Error(string message, string caption, Exception exception);
    }

    public class UserMessagesViaMessageBox : IUserMessages
    {
        public void Error(string message, string caption, Exception exception)
        {
            // Fork (agent server): no dialogs during an agent call or in headless mode.
            if (ReBuzz.Agent.NonModal.TryCapture(message, caption)) return;
            MessageBox.Show(message, caption);
        }
    }
}