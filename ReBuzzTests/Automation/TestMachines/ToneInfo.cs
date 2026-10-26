using AtmaFileSystem;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzzTests.Automation.TestMachines
{
    /// <summary>
    /// Fork addition: information about the Tone test machine (see SynthInfo).
    ///
    /// This information is awkward to put in the <see cref="Tone"/>
    /// class because it would require a reference to the types defined in this project.
    /// </summary>
    public class ToneInfo : IDynamicTestMachineInfo
    {
        public static IDynamicTestMachineInfo Instance { get; } = new ToneInfo();

        MachineDLL ITestMachineInfo.GetMachineDll(ReBuzzCore buzz, AbsoluteFilePath location)
        {
            var decl = Tone.GetMachineDecl();
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
                SHA1Hash = "3C1F0A5D2B7E4C9A8F6D1E2B3A4C5D6E7F8A9B0C",
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

        public string DllName => Tone.GetMachineDecl().Name + ".dll";
        public string RealDllName => DllName;
        public string SourceCode => Tone.GetSourceCode();
    }
}