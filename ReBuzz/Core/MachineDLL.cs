using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.ManagedMachine;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ReBuzz.Core
{
    internal class MachineDLL : IMachineDLL
    {
        readonly ManagedMachineDLL managed;

        public IBuzz Buzz { get; set; }

        public string Name { get; set; }

        public string Path { get; set; }

        public string SHA1Hash { get; set; }

        public IntPtr ModuleHandle { get; set; }

        public MachineInfo MachineInfo { get; set; }
        public IMachineInfo Info { get => MachineInfo; }

        public ReadOnlyCollection<string> Presets { get; set; }

        public bool IsLoaded { get; set; }

        public bool IsMissing { get; set; }

        bool isCrashed = false;

        // ToDo: IsCrashed should be moved to Machine.
        public bool IsCrashed
        {
            get => isCrashed;
            set
            {
                isCrashed = value;
                PropertyChanged.Raise(this, "IsCrashed");
            }
        }

        public bool IsOutOfProcess { get; set; }

        public bool IsManaged { get; set; }

        // Fork (engine-split.md S4): a native machine's skin as portable data. IMachineDLL's WPF
        // members (Skin, SkinLED, SkinLEDSize, SkinLEDPosition, TextColor) have default bodies in the
        // Windows build that the WPF app answers from these (WpfMachineApiHooks).
        public RawImage SkinImage { get; set; }
        public RawImage SkinLedImage { get; set; }
        public int SkinLedX { get; set; }
        public int SkinLedY { get; set; }

        // Theme XAML binds these by name ({Binding Machine.DLL.SkinLED}, SkinLEDSize.Width,
        // SkinLEDPosition.X), so they stay, with the UI's image and WPF's property names.
        public object Skin => SkinImage?.View;
        public object SkinLED => SkinLedImage?.View;
        public SkinSize SkinLEDSize => SkinLedImage == null ? default : new SkinSize(SkinLedImage.Width, SkinLedImage.Height);
        public SkinPoint SkinLEDPosition => new SkinPoint(SkinLedX, SkinLedY);

        public uint TextColorArgb
        {
            get
            {
                if (MachineViewState.SkinsEnabled && SkinImage != null)   // engine-split.md S7
                {
                    return SkinTextColorArgb;
                }
                else
                {
                    return unchecked((uint)Global.Buzz.GetThemeColour("MV Machine Text").ToArgb());
                }
            }
        }

        public ManagedMachineDLL ManagedDLL { get; set; }

        bool guiFactoryCached = false;
        IMachineGUIFactory guiFactory;
        private MachineGUIFactoryDecl guiFactoryDecl;

        public IMachineGUIFactory GUIFactory
        {
            get
            {
                if (guiFactoryCached)
                {
                    return guiFactory;
                }
                guiFactoryCached = true;
                string path = System.IO.Path.GetDirectoryName(Path);
                string text = ((Info.Type != MachineType.Generator) ? "Effects\\" : "Generators\\");
                // path = string.Concat(path, string.Concat(string.Concat("\\Gear\\" + text, Name), ".GUI.dll"));
                path = string.Concat(path, "\\" + Name, ".GUI.dll");
                try
                {
                    Assembly assembly = null;
                    ManagedMachineDLL machineDLL = ManagedDLL;
                    if (machineDLL != null)
                    {
                        assembly = machineDLL.Assembly;
                    }
                    else
                    {
                        if (!File.Exists(path))
                        {
                            return null;
                        }
                        assembly = Assembly.LoadFile(path);
                    }
                    Type[] exportedTypes = assembly.GetExportedTypes();
                    for (int i = 0; i < exportedTypes.Length; i++)
                    {
                        Type type = exportedTypes[i];
                        if (type.GetInterface("BuzzGUI.Interfaces.IMachineGUIFactory") != null)
                        {
                            guiFactoryDecl = type.GetCustomAttributes(inherit: false).OfType<MachineGUIFactoryDecl>().FirstOrDefault();
                            return guiFactory = Activator.CreateInstance(type) as IMachineGUIFactory;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ((Buzz ?? Global.Buzz) as ReBuzzCore)?.UserMessages.Error(ex.Message, path, ex);
                }
                return null;
            }
        }

        public MachineDLL()
        {
            MachineInfo = new MachineInfo();
        }

        public MachineGUIFactoryDecl GUIFactoryDecl
        {
            get
            {
                if (guiFactoryDecl == null)
                {
                    var a = GUIFactory;
                }
                return guiFactoryDecl;
            }
            set => guiFactoryDecl = value;
        }

        public uint SkinTextColorArgb { get; internal set; }
        public bool Is64Bit { get; internal set; }

        public event PropertyChangedEventHandler PropertyChanged;
        public void Load()
        {
        }

        internal MachineDLL Clone()
        {
            MachineDLL dll = new MachineDLL();
            dll.Buzz = Buzz;
            dll.SkinLedImage = SkinLedImage;
            dll.SkinImage = SkinImage;
            dll.SkinLedX = SkinLedX;
            dll.SkinLedY = SkinLedY;
            dll.Is64Bit = Is64Bit;
            dll.IsCrashed = IsCrashed;
            dll.IsLoaded = IsLoaded;
            dll.IsManaged = IsManaged;
            dll.IsMissing = IsMissing;
            dll.IsOutOfProcess = IsOutOfProcess;
            dll.MachineInfo = MachineInfo.Clone();

            dll.Name = Name;
            dll.Path = Path;
            dll.SHA1Hash = SHA1Hash;

            return dll;
        }
    }
}
