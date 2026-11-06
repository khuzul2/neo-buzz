// Fork (docs/specs/studio-mvp.md S7): Preferences ▸ General: the engine's and the general settings
// (BuzzGUI's EngineSettings and GeneralSettings), shown from their [BuzzSetting]s as ReBuzz.exe's
// Settings window shows them, without the ones only the Windows app reads.

using BuzzGUI.Common;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class GeneralPage : PrefsPage
    {
        /// <summary>Settings only ReBuzz.exe (WPF, Windows) reads.</summary>
        internal static readonly string[] WindowsOnlyEngine = { "AudioBufferFillThread" };

        internal static readonly string[] WindowsOnlyGeneral =
        {
            "WPFIdealFontMetrics", "WPFSoftwareRendering", "SequenceView", "PianoKeyboardTopmost", "DpiScaling",
        };

        public GeneralPage()
        {
            Heading("Engine");
            Note("These apply at once, except where they say restart.");
            AddSettings(Global.EngineSettings, WindowsOnlyEngine);
            Heading("General");
            AddSettings(Global.GeneralSettings, WindowsOnlyGeneral);
        }

        public override string Title => "General";
    }
}
