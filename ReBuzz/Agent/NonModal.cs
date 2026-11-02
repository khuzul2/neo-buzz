// Agent server (docs/specs/mcp-server.md §3.2): no modal dialogs while an agent works. Messages
// and questions ReBuzz would show in a message box are captured instead: during an agent call
// (on the engine thread) they become the call's error text; in headless mode they always are
// (there is no one to click OK) and go to the log. Questions get the safe answer (No/Cancel).

using System;
using System.Collections.Generic;
using ReBuzz.Core;

namespace ReBuzz.Agent
{
    internal static class NonModal
    {
        [ThreadStatic] static List<string> scope;
        static volatile Action<string> always;

        /// <summary>Headless mode: capture every message on every thread, sending it to <paramref name="sink"/>.</summary>
        public static void CaptureAlways(Action<string> sink) => always = sink;

        public static bool IsCapturing => scope != null || always != null;

        /// <summary>Captures messages raised on this thread until disposed.</summary>
        public static Scope Capture() => new Scope();

        internal sealed class Scope : IDisposable
        {
            readonly List<string> outer;

            public Scope()
            {
                outer = scope;
                scope = new List<string>();
                Messages = scope;
            }

            public List<string> Messages { get; }

            public void Dispose() => scope = outer;
        }

        /// <summary>True when the message was captured (don't show it).</summary>
        public static bool TryCapture(string message, string caption)
        {
            string text = string.IsNullOrEmpty(caption) ? message : $"{caption}: {message}";
            if (scope != null)
            {
                scope.Add(text);
                always?.Invoke(text);
                return true;
            }
            if (always != null)
            {
                always(text);
                return true;
            }
            return false;
        }

        /// <summary>A question: captured with the safe answer (No, Cancel or OK).</summary>
        public static bool TryCapture(string message, string caption, UserButtons buttons, out UserAnswer answer)
        {
            answer = buttons switch
            {
                UserButtons.OkCancel => UserAnswer.Cancel,
                UserButtons.YesNo => UserAnswer.No,
                UserButtons.YesNoCancel => UserAnswer.Cancel,
                _ => UserAnswer.Ok,
            };
            return TryCapture(message, caption);
        }
    }
}
