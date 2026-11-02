// Fork (docs/specs/mac-engine.md M0): the engine's message seam (IUserMessages) for hosts with no
// window: messages are written as lines (stderr by default, which keeps stdout for the MCP
// protocol), and questions get the answer that does the least: No, Cancel, or OK when that is
// the only button. A long load is waited for. During an agent call NonModal captures messages
// before they get here, so these are the ones raised outside calls (start-up, the audio thread).

using System;
using System.IO;
using ReBuzz.Core;

namespace ReBuzz.Platform
{
    public sealed class ConsoleUserMessages : IUserMessages
    {
        readonly TextWriter output;
        readonly object gate = new object();

        public ConsoleUserMessages(TextWriter output = null) => this.output = output ?? Console.Error;

        /// <summary>The answer a question gets when nobody is there to choose.</summary>
        public static UserAnswer DefaultAnswer(UserButtons buttons) => buttons switch
        {
            UserButtons.YesNo => UserAnswer.No,
            UserButtons.YesNoCancel => UserAnswer.Cancel,
            UserButtons.OkCancel => UserAnswer.Cancel,
            _ => UserAnswer.Ok,
        };

        public void Error(string message, string caption, Exception exception) =>
            Write("error", caption, message + (exception != null ? $" ({exception.GetType().Name}: {exception.Message})" : ""));

        public void Info(string message, string caption) => Write("info", caption, message);

        public UserAnswer Ask(string message, string caption, UserButtons buttons)
        {
            var answer = DefaultAnswer(buttons);
            Write("question", caption, $"{message} → {answer}");
            return answer;
        }

        public bool KeepWaiting(string message, string caption)
        {
            Write("question", caption, $"{message} → keep waiting");
            return true;
        }

        void Write(string kind, string caption, string message)
        {
            string text = $"[ReBuzz] {kind}: " + (string.IsNullOrEmpty(caption) ? "" : caption + ": ") + (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
            lock (gate) output.WriteLine(text);
        }
    }
}
