using Core.NullableReferenceTypesExtensions;
using ReBuzz.Core;
using System;
using System.Collections.Generic;

namespace ReBuzzTests.Automation
{
    public class FakeUserMessages : IUserMessages
    {
        public void Error(string message, string caption, Exception exception)
        {
            Caption = caption;
            Message = message;
            StackStrace = exception.StackTrace.OrThrow();
            AnyErrorReported = true;
        }

        // Fork (engine-split.md S1): messages and questions are recorded; questions get
        // NextAnswer, or the safe answer (No, Cancel or OK) when it is null.
        public void Info(string message, string caption)
        {
            Infos.Add((message, caption));
        }

        public UserAnswer Ask(string message, string caption, UserButtons buttons)
        {
            Questions.Add((message, caption, buttons));
            return NextAnswer ?? buttons switch
            {
                UserButtons.OkCancel => UserAnswer.Cancel,
                UserButtons.YesNo => UserAnswer.No,
                UserButtons.YesNoCancel => UserAnswer.Cancel,
                _ => UserAnswer.Ok,
            };
        }

        public string StackStrace { get; private set; } = string.Empty;
        public string Message { get; private set; } = string.Empty;
        public string Caption { get; private set; } = string.Empty;
        public bool AnyErrorReported { get; private set; } = false;

        public List<(string Message, string Caption)> Infos { get; } = new();
        public List<(string Message, string Caption, UserButtons Buttons)> Questions { get; } = new();
        public UserAnswer? NextAnswer { get; set; }
    }
}
