#nullable disable
// Neo Drive's saved-state versions (docs/specs/machines.md §3.2). Pure, so the tests can load an old saved state through it.

using NeoBuzz.Dsp;

namespace NeoBuzz.Drive
{
    internal static class DriveState
    {
        /// <summary>The keys songs saved at version 0, in order (the table as it was before EXT3: Mode 0 … 4).</summary>
        public static readonly string[] Version0Keys =
        {
            "Mode", "Drive", "Bias", "Character", "LowCut", "Tone", "HighCut", "Output", "AutoGain", "Mix", "Quality", "Amount",
        };

        /// <summary>
        /// Version 1 (EXT3): Mode grew from five choices to ten (Overdrive, Fuzz, Hard clip, Rectify, Transformer, appended at the end). Nothing
        /// a song saved moves: Tape to Fold keep their numbers, so every saved value means what it did.
        /// </summary>
        public static readonly StateMigrations Chain = new StateMigrations(Params.Table).Step(Version0Keys, s => { });
    }
}
