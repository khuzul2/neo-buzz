using System;

namespace ReBuzz.Core
{
    public interface IUserMessages
    {
        void Error(string message, string caption, Exception exception);
    }
}
