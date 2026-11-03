// Fork (docs/specs/studio-mvp.md S2): WPF's MessageBox for the Pedal machines' portable builds,
// which have no WPF. Their About… and error boxes call MessageBox.Show; off Windows the text goes
// to ReBuzz's debug console and to stderr. PedalPortable.targets compiles this file into every
// portable Pedal machine; the Windows builds use WPF's own MessageBox.

namespace System.Windows
{
    internal enum MessageBoxButton { OK = 0, OKCancel = 1, YesNoCancel = 3, YesNo = 4 }

    internal enum MessageBoxImage { None = 0, Error = 16, Question = 32, Warning = 48, Information = 64 }

    internal enum MessageBoxResult { None = 0, OK = 1, Cancel = 2, Yes = 6, No = 7 }

    internal static class MessageBox
    {
        public static MessageBoxResult Show(string text) => Show(text, null);

        public static MessageBoxResult Show(string text, string caption) => Show(text, caption, MessageBoxButton.OK, MessageBoxImage.None);

        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
        {
            string line = caption == null ? text : caption + ": " + text;
            try { BuzzGUI.Common.Global.Buzz?.DCWriteLine(line); } catch { }
            Console.Error.WriteLine(line);
            return button == MessageBoxButton.OK || button == MessageBoxButton.OKCancel ? MessageBoxResult.OK : MessageBoxResult.Yes;
        }
    }
}
