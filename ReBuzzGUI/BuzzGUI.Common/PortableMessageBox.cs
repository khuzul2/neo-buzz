namespace BuzzGUI.Common
{
    /// <summary>
    /// Fork (docs/specs/engine-split.md §4.4): System.Windows.MessageBox.Show in the Windows build.
    /// The portable build has no UI, so it writes the message to stderr.
    /// </summary>
    internal static class PortableMessageBox
    {
        internal static void Show(string message, string? caption = null)
        {
#if WINDOWS
            if (caption == null)
                System.Windows.MessageBox.Show(message);
            else
                System.Windows.MessageBox.Show(message, caption);
#else
            System.Console.Error.WriteLine(caption == null ? message : caption + ": " + message);
#endif
        }
    }
}
