using AtmaFileSystem;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzzTests.Automation.TestMachines
{
    /// <summary>
    /// Fork addition: information about the Hum test machine (see SynthInfo).
    ///
    /// This information is awkward to put in the <see cref="Hum"/>
    /// class because it would require a reference to the types defined in this project.
    /// </summary>
    public class HumInfo : IDynamicTestMachineInfo
    {
        public static IDynamicTestMachineInfo Instance { get; } = new HumInfo();

        MachineDLL ITestMachineInfo.GetMachineDll(ReBuzzCore buzz, AbsoluteFilePath location)
        {
            var decl = Hum.GetMachineDecl();
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
                SHA1Hash = "4D2A1B6E3C8F5D0A9E7B2C4D6F8A1B3C5E7D9F0A",
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

        public string DllName => Hum.GetMachineDecl().Name + ".dll";
        public string RealDllName => DllName;
        public string SourceCode => Hum.GetSourceCode();
    }
}