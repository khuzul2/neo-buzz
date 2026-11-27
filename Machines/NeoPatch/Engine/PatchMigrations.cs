#nullable disable
// Neo Patch's versioned state (docs/specs/machines.md section 3.4), in the engine folder so the tests that load old songs can reach it.
// Version 1 (docs/specs/notes.md G2, CU6a): "Vel -> level" was appended. A song saved before it has no such control and sounded with the
// velocity the patch wires itself, so it loads with 0 %; a new machine has the table's default (70 %), so a played Neo Patch responds to velocity.

using NeoBuzz.Dsp;

namespace NeoBuzz.Patch
{
    internal static class PatchMigrations
    {
        /// <summary>The keys of version 0, in order (what a song saved before the chain contains).</summary>
        public static readonly string[] Version0 =
        {
            "volume", "voices", "glide", "audio_mod",
            "macro_1", "macro_2", "macro_3", "macro_4", "macro_5", "macro_6", "macro_7", "macro_8",
            "macro_9", "macro_10", "macro_11", "macro_12", "macro_13", "macro_14", "macro_15", "macro_16",
        };

        public static readonly StateMigrations Chain = new StateMigrations(Params.Table)
            .Step(Version0, s => s.Add("vel_level", 0));
    }
}
