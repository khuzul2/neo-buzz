// Fork (machine keyboard, docs/specs/studio-mvp.md §3.12): whether a machine's windows show the
// keyboard under them. Preferences (General ▸ Machine keyboard, GeneralSettings.MachineKeyboard, on
// at first) sets it for every machine; a window's toggle (or Ctrl/⌘+K there) sets it for that machine
// type, kept per gear in the settings (RegistryEx: the Windows registry, or the studio's JSON) and
// never in songs, as the per-gear "double-click opens editor" is. A gear set back to the default
// follows the default again. The other open windows of the same gear follow a toggle (Changed).

using System;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core.Keyboards
{
    internal static class KeyboardSettings
    {
        /// <summary>The settings key (under the app's registry root): per gear, 1 shown, 0 hidden; none or -1, the default.</summary>
        public const string Key = "MachineKeyboard";

        /// <summary>Preferences: shown under every machine that plays notes unless its type says otherwise.</summary>
        public static bool Default => Global.GeneralSettings?.MachineKeyboard ?? true;

        /// <summary>A gear's own choice: true or false, null for the default.</summary>
        public static bool? Choice(IMachineDLL dll)
        {
            if (dll?.Name is not { Length: > 0 } name) return null;
            int v = RegistryEx.Read(name, -1, Key);
            return v < 0 ? null : v != 0;
        }

        /// <summary>Whether a machine of this gear shows its keyboard.</summary>
        public static bool Shown(IMachineDLL dll) => Choice(dll) ?? Default;

        /// <summary>Sets the gear's choice (the same as the default clears it), and tells the gear's other windows.</summary>
        public static void Set(IMachineDLL dll, bool shown)
        {
            if (dll?.Name is not { Length: > 0 } name) return;
            RegistryEx.Write(name, shown == Default ? -1 : shown ? 1 : 0, Key);
            Changed?.Invoke(dll.Name);
        }

        /// <summary>A gear's choice changed (its name), on the UI thread: its open keyboards follow.</summary>
        public static event Action<string> Changed;
    }
}
