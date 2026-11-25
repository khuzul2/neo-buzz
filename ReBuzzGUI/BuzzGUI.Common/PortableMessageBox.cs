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
            System.Console.Error.WriteLine(caption == null ? message : caption + ": " + message);
        }
    }
}
