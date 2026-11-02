#if WINDOWS
using System.Windows;
using System.Windows.Media;

namespace BuzzGUI.Interfaces
{
    /// <summary>
    /// Windows build only (docs/specs/engine-split.md §4.3). The WPF-typed members of the machine
    /// API have default bodies in this build that ask these hooks, so an engine compiled against the
    /// portable build (where the members don't exist) still satisfies the interfaces on Windows.
    /// The WPF app sets the hooks before the first machine loads. An unset hook gives null (or the
    /// default value). A class that implements a member itself is not affected.
    /// </summary>
    public static class WpfMachineApi
    {
        public static Func<IMachine, Window?>? ParameterWindow { get; set; }
        public static Func<IBuzz, IIndex<string, Color>?>? ThemeColors { get; set; }
        public static Func<IMachineDLL, ImageSource?>? Skin { get; set; }
        public static Func<IMachineDLL, ImageSource?>? SkinLED { get; set; }
        public static Func<IMachineDLL, Size>? SkinLEDSize { get; set; }
        public static Func<IMachineDLL, Point>? SkinLEDPosition { get; set; }
        public static Func<IMachineDLL, Color>? TextColor { get; set; }
    }
}
#endif
