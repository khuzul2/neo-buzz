using System;

namespace ReBuzz.MachineManagement
{
    /// <summary>
    /// Keyboard access hidden behind an interface to allow for easier testing,
    /// because the Keyboard class requires STA thread.
    /// </summary>
    public interface IKeyboard
    {
        /// <summary>
        /// Whether the modifier is held. Takes <see cref="KeyModifiers"/> or WPF's ModifierKeys.
        /// </summary>
        bool HasModifierKeyPressed(Enum modifierKey);
    }

    /// <summary>
    /// Portable modifier keys. Same names and values as WPF's System.Windows.Input.ModifierKeys,
    /// so the WPF implementation just casts it.
    /// </summary>
    [Flags]
    public enum KeyModifiers
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Windows = 8,
    }
}
