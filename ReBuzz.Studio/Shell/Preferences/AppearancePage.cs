// Fork (docs/specs/ux-overhaul.md §3.3, P2b): Preferences ▸ Appearance: the theme. Dark is the default; Classic Buzz draws
// the machine view on beige and the sequence on brown, as Buzz always has. The choice is a studio setting (a new key,
// AppearanceSettings/Theme) and applies at once.

using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class AppearancePage : PrefsPage
    {
        public AppearancePage()
        {
            Heading("Theme");
            Note("Applies at once. Songs don't change: pattern, track and clip colours are saved in the song as they always were.");
            AddSettings(StudioTheme.Settings);
        }

        public override string Title => "Appearance";
    }
}
