// Fork (docs/specs/studio-mvp.md S7, ux-overhaul.md P3f): Preferences ▸ General: the general settings (BuzzGUI's GeneralSettings: note names,
// pattern length, song loading...), shown from the agent's list (section "general"), without the ones only the Windows app reads.

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class GeneralPage : PrefsPage
    {
        internal static readonly string[] WindowsOnlyGeneral =
        {
            "WPFIdealFontMetrics", "WPFSoftwareRendering", "SequenceView", "PianoKeyboardTopmost", "DpiScaling",
        };

        public GeneralPage(PrefsModel model) : base(model)
        {
            Heading("General");
            AddSection("general", WindowsOnlyGeneral);
        }

        public override string Title => "General";
    }

    /// <summary>
    /// Preferences ▸ Advanced: today's engine flags (BuzzGUI's EngineSettings), which change how audio is made: songs can sound different.
    /// </summary>
    internal sealed class AdvancedPage : PrefsPage
    {
        /// <summary>Settings only ReBuzz.exe (WPF, Windows) reads.</summary>
        internal static readonly string[] WindowsOnlyEngine = { "AudioBufferFillThread" };

        public AdvancedPage(PrefsModel model) : base(model)
        {
            Heading("Engine flags");
            Note("These change how audio is made, so a song can sound different. They apply at once, except where they say restart. " +
                 "Restore Defaults puts every flag back.");
            AddSection("advanced", WindowsOnlyEngine);
        }

        public override string Title => "Advanced";
        public override bool ConfirmRestore => true;
        public override string RestoreWarning => "Put every engine flag back to its default? Songs can sound different with the defaults.";
    }
}
