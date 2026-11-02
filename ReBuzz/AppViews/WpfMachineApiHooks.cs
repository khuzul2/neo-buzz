using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.AppViews
{
    /// <summary>
    /// Fork (docs/specs/engine-split.md §4.3): in the Windows build of BuzzGUI.Interfaces the
    /// WPF-typed members of the machine API (IMachine.ParameterWindow, IBuzz.ThemeColors and the
    /// IMachineDLL skin members) have default bodies that ask WpfMachineApi. The engine's classes
    /// still implement those members, so for now the hooks only forward to them; the WPF host and
    /// theme service take them over when the engine stops implementing them (seams S3, S4).
    /// App sets them before the first machine loads.
    /// </summary>
    internal static class WpfMachineApiHooks
    {
        internal static void Install()
        {
            WpfMachineApi.ParameterWindow = m => (m as MachineCore)?.ParameterWindow;
            WpfMachineApi.ThemeColors = b => (b as ReBuzzCore)?.ThemeColors;
            WpfMachineApi.Skin = d => (d as MachineDLL)?.Skin;
            WpfMachineApi.SkinLED = d => (d as MachineDLL)?.SkinLED;
            WpfMachineApi.SkinLEDSize = d => (d as MachineDLL)?.SkinLEDSize ?? default;
            WpfMachineApi.SkinLEDPosition = d => (d as MachineDLL)?.SkinLEDPosition ?? default;
            WpfMachineApi.TextColor = d => (d as MachineDLL)?.TextColor ?? default;
        }
    }
}
