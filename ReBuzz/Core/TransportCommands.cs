namespace ReBuzz.Core
{
    /// <summary>
    /// Fork: the keyboard's transport commands, one path for both apps (ReBuzz.exe's main window and
    /// the studio's) and both Session Views. Space plays and stops the song in every view, as in most
    /// DAWs (F5 and F8 still do, as in Buzz); Shift+Space plays the pattern editor's pattern on its
    /// own from the editor's cursor, and stops it. The windows leave Space to a text box being typed in.
    /// </summary>
    internal static class TransportCommands
    {
        /// <summary>
        /// Space: plays when stopped, stops when playing. A start waiting for its tick counts as
        /// playing (<see cref="ReBuzzCore.PlayRequested"/>), as the Session View's Space always had it,
        /// so a second press before the tick stops it again.
        /// </summary>
        public static void TogglePlaying(ReBuzzCore core)
        {
            if (!core.PlayRequested)
            {
                core.Playing = true;
                return;
            }
            // A pattern played on its own (Shift+Space, the pattern editor's ▶) stops through its flag,
            // which stops the transport as Playing = false does and puts the editor's ▶ out. Playing =
            // false alone left the flag on, and the next Shift+Space only turned it off.
            if (core.SoloPattern is { IsPlayingSolo: true } solo) solo.IsPlayingSolo = false;
            else core.Playing = false;
        }

        /// <summary>
        /// Shift+Space in the pattern view: the pattern editor's pattern played on its own from the
        /// editor's cursor, or stopped when it is playing (ReBuzz.exe's Shift+Space, as upstream has it).
        /// False when the editor shows no pattern.
        /// </summary>
        public static bool TogglePatternPreview(ReBuzzCore core)
        {
            if (core.PatternEditorPattern is not PatternCore pattern) return false;
            if (pattern.IsPlayingSolo) pattern.IsPlayingSolo = false;
            else pattern.PlayFormPatternEditorPosition();
            return true;
        }
    }
}
