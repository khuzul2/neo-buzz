using AtmaFileSystem;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzzTests.Automation.TestMachines
{
    /// <summary>
    /// Fork addition: information about the Keys test machine (see SynthInfo).
    ///
    /// This information is awkward to put in the <see cref="Keys"/>
    /// class because it would require a reference to the types defined in this project.
    /// </summary>
    public class KeysInfo : IDynamicTestMachineInfo
    {
        public static IDynamicTestMachineInfo Instance { get; } = new KeysInfo();

        MachineDLL ITestMachineInfo.GetMachineDll(ReBuzzCore buzz, AbsoluteFilePath location)
        {
            var decl = Keys.GetMachineDecl();
            return new MachineDLL
            {
                Name = decl.Name,
                Buzz = buzz,
                Path = location.ToString(),
                Is64Bit = true,
                IsCrashed = false,
                IsManaged = true,
                IsLoaded = false,
                IsMissing = false,
                IsOutOfProcess = false,
                ManagedDLL = null,
                MachineInfo = new MachineInfo
                {
                    Flags = MachineInfoFlags.LOAD_DATA_RUNTIME,
                    Author = decl.Author,
                    InternalVersion = 0,
                    MaxTracks = decl.MaxTracks,
                    MinTracks = 1,
                    Name = decl.Name,
                    ShortName = decl.ShortName,
                    Type = MachineType.Generator,
                    Version = 66
                },
                Presets = null,
                SHA1Hash = "6B1E9C2A4D7F3E8B0C5A1D9E2F4B6C8A0D3E5F71",
                GUIFactoryDecl =
                    new MachineGUIFactoryDecl
                    {
                        IsGUIResizable = true,
                        PreferWindowedGUI = true,
                        UseThemeStyles = false
                    },
                ModuleHandle = 0
            };
        }

        public string DllName => Keys.GetMachineDecl().Name + ".dll";
        public string RealDllName => DllName;
        public string SourceCode => Keys.GetSourceCode();
    }
}