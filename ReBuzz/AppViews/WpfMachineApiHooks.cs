using BuzzGUI.Interfaces;
using ReBuzz.Common;
using ReBuzz.Core;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReBuzz.AppViews
{
    /// <summary>
    /// Fork (docs/specs/engine-split.md §4.3): in the Windows build of BuzzGUI.Interfaces the
    /// WPF-typed members of the machine API (IMachine.ParameterWindow, IBuzz.ThemeColors and the
    /// IMachineDLL skin members) have default bodies that ask WpfMachineApi. App sets the hooks
    /// before the first machine loads.
    /// - ParameterWindow and the machine windows (S3): WpfMachineUiHost, every core's default host.
    /// - ThemeColors and the skin members (S4): the engine keeps portable data (an ARGB theme table,
    ///   raw skin pixels); the hooks answer with WPF types, made as the engine made them before.
    /// </summary>
    internal static class WpfMachineApiHooks
    {
        internal static void Install()
        {
            WpfMachineApi.ParameterWindow = WpfMachineUiHost.ParameterWindowOf;
            ReBuzzCore.DefaultUiHost = new WpfMachineUiHost();
            ReBuzz.ManagedMachine.ManagedMachineHost.WrapWinFormsControl =
                c => new NativeMachineFrameworkUI.WinFormsControl((System.Windows.Forms.UserControl)c);
            WpfMachineApi.ThemeColors = b => UiUtils.GetThemeColors(b as ReBuzzCore);
            ReBuzzCore.ThemeColorsView = UiUtils.GetThemeColors;   // theme XAML binds Buzz.ThemeColors[...]
            WpfMachineApi.Skin = d => (d as MachineDLL)?.Skin as ImageSource;
            WpfMachineApi.SkinLED = d => (d as MachineDLL)?.SkinLED as ImageSource;
            WpfMachineApi.SkinLEDSize = d => d is MachineDLL m ? new Size(m.SkinLEDSize.Width, m.SkinLEDSize.Height) : default;
            WpfMachineApi.SkinLEDPosition = d => d is MachineDLL m ? new Point(m.SkinLedX, m.SkinLedY) : default;
            WpfMachineApi.TextColor = d => d is MachineDLL m ? UiUtils.ColorFromArgb(m.TextColorArgb) : default;
            RawImage.Viewer = SkinBitmap;

            // View state and UI settings the engine used to read or set itself (S6, S7).
            ReBuzzCore.SoftwareRendering = on => RenderOptions.ProcessRenderMode = on ? RenderMode.SoftwareOnly : RenderMode.Default;
            MachineViewState.ShowEngineThreads = () => BuzzGUI.MachineView.MachineView.StaticSettings.ShowEngineThreads;
            MachineViewState.EnableSkins = () => BuzzGUI.MachineView.MachineView.StaticSettings.EnableSkins;
            PatternColours.Source = SequenceEditorPatternColours.Current;
        }

        /// <summary>
        /// A skin image as WPF shows it: 96 dpi, opaque 32-bit BGR (the 4th byte ignored), as the
        /// GDI+ Format32bppRgb bitmap and its HBITMAP made it before. Frozen, so any thread can use it.
        /// </summary>
        internal static BitmapSource SkinBitmap(RawImage image)
        {
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Bgr32, image.Width * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
