// Fork (docs/specs/studio-mvp.md S7, ux-overhaul.md P3f): Preferences ▸ General: the general settings (BuzzGUI's GeneralSettings: note names,
// pattern length, song loading...), shown from the agent's list (section "general"), without the ones only the Windows app reads.

using System;
using Avalonia.Controls;
using ReBuzz.Studio.Platform;

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
            // Fork (ux-overhaul.md P6c): the portable package has no installer, so the default program for songs is set here (Windows only).
            associations = Associations();
            if (associations != null)
            {
                Heading("File types");
                Note("Open .nbl songs with NeoBuzz Live when they are double-clicked. Saved for this Windows user only; Restore gives back " +
                     "the program they opened with before.");
                assocButton.Click += (_, _) => ToggleAssociation();
                Row("Default program", assocButton, null, assocState);
                RefreshAssociation();
            }
        }

        /// <summary>Where the registry comes from: the real one on Windows (null elsewhere); tests put a store in memory.</summary>
        internal static Func<FileAssociations> Associations = FileAssociations.ForThisApp;

        readonly FileAssociations associations;
        readonly Button assocButton = new Button();
        readonly TextBlock assocState = new TextBlock { Foreground = Dim, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        internal Button AssociationButton => associations == null ? null : assocButton;
        internal string AssociationText => assocState.Text;

        void ToggleAssociation()
        {
            try
            {
                if (associations.IsDefault) associations.Remove(); else associations.Make();
            }
            catch (Exception e)
            {
                assocState.Text = "Could not change the file types: " + e.Message;
                return;
            }
            RefreshAssociation();
        }

        void RefreshAssociation()
        {
            bool isDefault = associations.IsDefault;
            assocButton.Content = isDefault ? "Restore the previous default for .nbl" : "Make NeoBuzz Live the default for .nbl";
            if (isDefault) { assocState.Text = "NeoBuzz Live opens .nbl songs."; return; }
            string now = associations.CurrentFor(".nbl");
            assocState.Text = now == null ? "No program is set for .nbl songs." : "Now opened by: " + now + ".";
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
