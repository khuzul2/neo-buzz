using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
#if WINDOWS
using System.Windows;
using System.Windows.Media;
#endif

namespace BuzzGUI.Interfaces
{
    public interface IMachineDLL : INotifyPropertyChanged
    {
        IBuzz Buzz { get; }
        string Name { get; }
        string Path { get; }
        string SHA1Hash { get; }        // computed in a background task so returns null first and sends a property changed notification later
        IntPtr ModuleHandle { get; }
        IMachineInfo Info { get; }
        ReadOnlyCollection<string> Presets { get; }

        bool IsLoaded { get; }
        bool IsMissing { get; }
        bool IsCrashed { get; }
        bool IsOutOfProcess { get; }
        bool IsManaged { get; }

#if WINDOWS
        // skin. WPF types, so the Windows build only; the default bodies ask the WPF app
        // (WpfMachineApi, docs/specs/engine-split.md §4.3). Implementations replace them.
        ImageSource Skin => WpfMachineApi.Skin?.Invoke(this)!;
        ImageSource SkinLED => WpfMachineApi.SkinLED?.Invoke(this)!;
        Size SkinLEDSize => WpfMachineApi.SkinLEDSize?.Invoke(this) ?? default;
        Point SkinLEDPosition => WpfMachineApi.SkinLEDPosition?.Invoke(this) ?? default;
        Color TextColor => WpfMachineApi.TextColor?.Invoke(this) ?? default;
#endif

        IMachineGUIFactory GUIFactory { get; }
        MachineGUIFactoryDecl GUIFactoryDecl { get; }

        void Load();
    }
}
