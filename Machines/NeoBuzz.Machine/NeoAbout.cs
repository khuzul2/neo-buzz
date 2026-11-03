// Fork (docs/specs/mac-engine.md M2): a Neo machine's "About" text. A message box on Windows and
// in the studio (its Avalonia kit, studio-mvp.md S1); a line on the console where there is no UI
// toolkit (rebuzz-headless).

namespace NeoBuzz.Machine
{
    public static class NeoAbout
    {
        public static void Show(string text, string caption)
        {
#if WINDOWS
            System.Windows.MessageBox.Show(text, caption);
#elif AVALONIA
            NeoBuzz.Editor.MessageBox.Show(text, caption);
#else
            System.Console.Error.WriteLine(caption + ": " + text.Replace('\n', ' '));
#endif
        }
    }
}
