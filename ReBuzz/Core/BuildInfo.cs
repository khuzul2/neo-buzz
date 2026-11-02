using System.IO;
using System.Text;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork (engine-split.md S10): the build number and date, from BuildNumber.txt and
    /// BuildDate.txt embedded in the assembly that compiles this file (ReBuzz.exe, ReBuzz.Engine).
    /// Read as the ResXFileRef resources were: the whole file, its encoding from its byte order mark.
    /// </summary>
    internal static class BuildInfo
    {
        public static string Number { get; } = Read("ReBuzz.BuildNumber.txt");
        public static string Date { get; } = Read("ReBuzz.BuildDate.txt");

        static string Read(string name)
        {
            using var stream = typeof(BuildInfo).Assembly.GetManifestResourceStream(name);
            if (stream == null)
                return "";
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }
}
