using System;
using System.Windows.Input;

namespace ReBuzz.MachineManagement
{
    internal class WindowsKeyboard : IKeyboard
    {
        public bool HasModifierKeyPressed(Enum modifierKey)
        {
            // KeyModifiers has ModifierKeys' values; HasFlag needs the same enum type.
            if (modifierKey is KeyModifiers portable)
                return Keyboard.Modifiers.HasFlag((ModifierKeys)portable);
            return Keyboard.Modifiers.HasFlag(modifierKey);
        }
    }
}