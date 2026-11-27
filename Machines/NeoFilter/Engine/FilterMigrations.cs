#nullable disable
// Neo Filter's versioned state (docs/specs/machines.md section 3.4), in the engine folder so the tests that load old songs can reach it.
// Version 1 (EXT1, the vowel mode): Type gained "Vowel" at the end of its list (a wider range: every value a song saved means what it did)
// and Vowel, Voice and Vowel mod were appended after Amount. A song saved before them loads with the table's defaults, which change nothing
// (they only act in the Vowel type, which no old song can have selected).

using NeoBuzz.Dsp;

namespace NeoBuzz.Filter
{
    internal static class FilterMigrations
    {
        /// <summary>The keys of version 0, in order (what a song saved before the chain contains; Amount was appended to it without a version).</summary>
        public static readonly string[] Version0 =
        {
            "Type", "Slope", "Cutoff", "Resonance", "Drive", "EnvAmount", "EnvSens", "EnvAttack", "EnvRelease",
            "LfoRate", "LfoSync", "LfoShape", "LfoDepth", "LfoSpread", "LfoRetrig", "LfoPhase", "Mix", "Output", "Amount",
        };

        public static readonly StateMigrations Chain = new StateMigrations(Params.Table)
            .Step(Version0, s => { });
    }
}
