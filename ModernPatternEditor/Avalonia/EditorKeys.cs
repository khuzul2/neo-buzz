// Fork (docs/specs/studio-mvp.md S6): the view's hook into the editor, and Avalonia's keys as the
// editor reads them: EditorKey and its modifiers (Command on macOS acts as Control, as the studio's
// shortcuts do), and the PC scan code of the key's position, which the keyboard mappings
// (PEKeys.xml) name, so notes are entered by where keys are, as on Windows.

using Avalonia.Input;
using System.Runtime.CompilerServices;

namespace WDE.ModernPatternEditor.AvaloniaView
{
    // Avalonia's, not the portable editor's stand-in (PortableShims.cs).
    using Color = Avalonia.Media.Color;

    internal static class ViewRegistration
    {
        [ModuleInitializer]
        internal static void Register()
        {
            PatternEditor.ViewFactory = editor => new PatternEditorView(editor);
        }
    }

    internal static class EditorKeys
    {
        public static EditorModifiers Modifiers(KeyModifiers m)
        {
            var r = EditorModifiers.None;
            if (m.HasFlag(KeyModifiers.Control) || (OperatingSystem.IsMacOS() && m.HasFlag(KeyModifiers.Meta))) r |= EditorModifiers.Control;
            if (m.HasFlag(KeyModifiers.Shift)) r |= EditorModifiers.Shift;
            if (m.HasFlag(KeyModifiers.Alt)) r |= EditorModifiers.Alt;
            return r;
        }

        public static EditorKey Key(Key key)
        {
            if (key >= Avalonia.Input.Key.D0 && key <= Avalonia.Input.Key.D9) return EditorKey.D0 + (key - Avalonia.Input.Key.D0);
            if (key >= Avalonia.Input.Key.A && key <= Avalonia.Input.Key.Z) return EditorKey.A + (key - Avalonia.Input.Key.A);
            if (key >= Avalonia.Input.Key.NumPad0 && key <= Avalonia.Input.Key.NumPad9) return EditorKey.NumPad0 + (key - Avalonia.Input.Key.NumPad0);
            switch (key)
            {
                case Avalonia.Input.Key.Up: return EditorKey.Up;
                case Avalonia.Input.Key.Down: return EditorKey.Down;
                case Avalonia.Input.Key.Left: return EditorKey.Left;
                case Avalonia.Input.Key.Right: return EditorKey.Right;
                case Avalonia.Input.Key.PageUp: return EditorKey.PageUp;
                case Avalonia.Input.Key.PageDown: return EditorKey.PageDown;
                case Avalonia.Input.Key.Home: return EditorKey.Home;
                case Avalonia.Input.Key.End: return EditorKey.End;
                case Avalonia.Input.Key.Tab: return EditorKey.Tab;
                case Avalonia.Input.Key.Delete: return EditorKey.Delete;
                case Avalonia.Input.Key.Insert: return EditorKey.Insert;
                case Avalonia.Input.Key.Enter: return EditorKey.Return;
                case Avalonia.Input.Key.Back: return EditorKey.Back;
                case Avalonia.Input.Key.Space: return EditorKey.Space;
                case Avalonia.Input.Key.Escape: return EditorKey.Escape;
                // The numeric keypad's + and -, and (for keyboards without one, as on most Macs) the main ones.
                case Avalonia.Input.Key.Add:
                case Avalonia.Input.Key.OemPlus: return EditorKey.Add;
                case Avalonia.Input.Key.Subtract:
                case Avalonia.Input.Key.OemMinus: return EditorKey.Subtract;
                case Avalonia.Input.Key.Multiply: return EditorKey.Multiply;
                case Avalonia.Input.Key.Divide: return EditorKey.Divide;
                case Avalonia.Input.Key.OemPeriod:
                case Avalonia.Input.Key.Decimal: return EditorKey.OemPeriod;
                case Avalonia.Input.Key.F1: return EditorKey.F1;
                default: return EditorKey.Other;
            }
        }

        /// <summary>The PC (set 1) scan code of a key's position, as KeyboardMapping names keys; 0 if none.</summary>
        public static int ScanCode(PhysicalKey key)
        {
            switch (key)
            {
                case PhysicalKey.Digit1: return 2;
                case PhysicalKey.Digit2: return 3;
                case PhysicalKey.Digit3: return 4;
                case PhysicalKey.Digit4: return 5;
                case PhysicalKey.Digit5: return 6;
                case PhysicalKey.Digit6: return 7;
                case PhysicalKey.Digit7: return 8;
                case PhysicalKey.Digit8: return 9;
                case PhysicalKey.Digit9: return 10;
                case PhysicalKey.Digit0: return 11;
                case PhysicalKey.Q: return 16;
                case PhysicalKey.W: return 17;
                case PhysicalKey.E: return 18;
                case PhysicalKey.R: return 19;
                case PhysicalKey.T: return 20;
                case PhysicalKey.Y: return 21;
                case PhysicalKey.U: return 22;
                case PhysicalKey.I: return 23;
                case PhysicalKey.O: return 24;
                case PhysicalKey.P: return 25;
                case PhysicalKey.A: return 30;
                case PhysicalKey.S: return 31;
                case PhysicalKey.D: return 32;
                case PhysicalKey.F: return 33;
                case PhysicalKey.G: return 34;
                case PhysicalKey.H: return 35;
                case PhysicalKey.J: return 36;
                case PhysicalKey.K: return 37;
                case PhysicalKey.L: return 38;
                case PhysicalKey.Z: return 44;
                case PhysicalKey.X: return 45;
                case PhysicalKey.C: return 46;
                case PhysicalKey.V: return 47;
                case PhysicalKey.B: return 48;
                case PhysicalKey.N: return 49;
                case PhysicalKey.M: return 50;
                default: return 0;
            }
        }
    }
}
