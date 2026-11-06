using System;
using System.Collections.Generic;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork: what went wrong technically, for an error dialog's Details (gallery mac-338: the dialog
    /// showed only "Unable to read beyond the end of the stream."). The message says what happened in
    /// words; this is the exception and its inner ones, then where it was thrown.
    /// </summary>
    public static class ErrorDetails
    {
        public static string Of(Exception exception)
        {
            if (exception == null) return "";
            var lines = new List<string>();
            for (var e = exception; e != null; e = e.InnerException)
                lines.Add($"{e.GetType().FullName}: {e.Message}");
            string trace = (exception.InnerException ?? exception).StackTrace;
            if (!string.IsNullOrWhiteSpace(trace))
            {
                lines.Add("");
                lines.Add(trace.TrimEnd());
            }
            return string.Join(Environment.NewLine, lines);
        }
    }
}
