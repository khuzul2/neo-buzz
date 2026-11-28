#nullable disable
// Neo Ensemble's versioned state (docs/specs/machines.md section 3.4), in the engine folder so the tests that load old songs can reach it.
// Version 1 (SYN1, tempo sync): the Feel and timing parameters were appended. A song saved before them loads with the values that
// sound as before (Straight, Song tempo), not the table's defaults.

using NeoBuzz.Dsp;

namespace NeoBuzz.Ensemble
{
    internal static class EnsembleMigrations
    {
        /// <summary>The keys of version 0, in order (what a song saved before SYN1 contains).</summary>
        public static readonly string[] Version0 =
        {
            "Mode", "Rate", "Sync", "Shape", "Depth", "Spread", "Delay", "Voices", "Centre", "Stages", "Feedback",
            "Tone", "Mix", "Output", "Amount",
        };

        public static readonly StateMigrations Chain = new StateMigrations(Params.Table)
            .Step(Version0, s => s.Add("Feel", 0));
    }
}
