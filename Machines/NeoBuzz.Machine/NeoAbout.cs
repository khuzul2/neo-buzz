// Fork (docs/specs/mac-engine.md M2): a Neo machine's "About" text. A message box on Windows; a
// line on the console where there is no UI toolkit (rebuzz-headless), until the portable studio
// (P3) shows it.

namespace NeoBuzz.Machine
{
    public static class NeoAbout
    {
        public static void Show(string text, string caption)
        {
#if WINDOWS
            System.Windows.MessageBox.Show(text, caption);
#else
            System.Console.Error.WriteLine(caption + ": " + text.Replace('\n', ' '));
#endif
        }
    }
}
