using System;
using System.Collections.ObjectModel;
using System.ComponentModel;

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


        IMachineGUIFactory GUIFactory { get; }
        MachineGUIFactoryDecl GUIFactoryDecl { get; }

        void Load();
    }
}
