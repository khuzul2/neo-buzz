// Fork (docs/specs/studio-mvp.md §2): the engine's keyboard seam (IKeyboard): which modifier keys
// are held, as the studio's windows last saw them.

using System;
using ReBuzz.MachineManagement;
using UiModifiers = Avalonia.Input.KeyModifiers;

namespace ReBuzz.Studio.Platform
{
    internal sealed class StudioKeyboard : IKeyboard
    {
        /// <summary>The modifiers held, kept by the studio's windows from their key and pointer events.</summary>
        public static UiModifiers Held { get; set; }

        public bool HasModifierKeyPressed(Enum modifierKey)
        {
            // The engine asks with KeyModifiers or WPF's ModifierKeys: the same names (Alt, Control, Shift, Windows).
            // On macOS, Command does what Control does elsewhere.
            return modifierKey.ToString() switch
            {
                "Alt" => Held.HasFlag(UiModifiers.Alt),
                "Control" => Held.HasFlag(UiModifiers.Control) || (OperatingSystem.IsMacOS() && Held.HasFlag(UiModifiers.Meta)),
                "Shift" => Held.HasFlag(UiModifiers.Shift),
                "Windows" => !OperatingSystem.IsMacOS() && Held.HasFlag(UiModifiers.Meta),
                _ => false,
            };
        }
    }
}
