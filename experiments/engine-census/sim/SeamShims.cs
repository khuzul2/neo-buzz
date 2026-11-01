// Census stage 5 only (EngineCensus.csproj). Simulates the first P1 PRs without touching ReBuzz:
// the four seam interfaces that upstream extracted still live in the same files as their WPF or
// Windows implementations, so the engine can't compile them alone. These are those interfaces,
// moved to their own declarations, with the two WPF types in their signatures made portable:
//   IUiDispatcher.BeginInvoke(Action, DispatcherPriority)  ->  UiPriority (same member names)
//   IKeyboard.HasModifierKeyPressed(Enum)                  ->  unchanged; callers still pass the
//                                                             WPF ModifierKeys enum (measured)
// Source of truth: ReBuzz/Core/WindowsGuiDispatcher.cs, UserMessagesViaMessageBox.cs,
// WindowsRegistry.cs, WindowsRegistryKey.cs and ReBuzz/MachineManagement/WindowsKeyboard.cs.

using System;
using System.Collections.Generic;

namespace ReBuzz.Core
{
    /// <summary>Portable stand-in for System.Windows.Threading.DispatcherPriority (same names).</summary>
    public enum UiPriority { SystemIdle = 1, ApplicationIdle, ContextIdle, Background, Input, Loaded, Render, DataBind, Normal, Send }

    public interface IUiDispatcher
    {
        void Invoke(Action action);
        void BeginInvoke(Action action);
        void BeginInvoke(Action action, UiPriority priority);
        bool CheckAccess() => true;
    }

    public interface IUserMessages
    {
        void Error(string message, string caption, Exception exception);
    }

    public interface IRegistryEx
    {
        void Write<T>(string key, T x, string path = "BuzzGUI");
        T Read<T>(string key, T def, string path = "BuzzGUI");
        IEnumerable<T> ReadNumberedList<T>(string key, string path = "BuzzGUI");
        Dictionary<string, object> ReadDictionary(string path = "BuzzGUI");
        void DeleteCurrentUserSubKey(string key);
        IRegistryKey CreateCurrentUserSubKey(string subKey);
        void DeleteCurrentUserValue(string value, string path);
    }

    public interface IRegistryKey
    {
        void SetValue(string name, object value);
    }
}

namespace ReBuzz.MachineManagement
{
    public interface IKeyboard
    {
        bool HasModifierKeyPressed(Enum modifierKey);
    }
}
