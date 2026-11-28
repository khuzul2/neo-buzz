#nullable disable
// Neo Wave's versioned state (docs/specs/machines.md section 3.4), in the engine folder so the tests that load old songs can reach it.
// Version 1 (SYN1, tempo sync): the Feel and timing parameters were appended. A song saved before them loads with the values that
// sound as before (Straight, Song tempo), not the table's defaults.

using NeoBuzz.Dsp;

namespace NeoBuzz.Wave
{
    internal static class WaveMigrations
    {
        /// <summary>The keys of version 0, in order (what a song saved before SYN1 contains).</summary>
        public static readonly string[] Version0 =
        {
            "VoiceMode", "Polyphony", "Unison", "UniDetune", "UniSpread", "GlideMode", "Glide", "BendRange", "Volume",
            "VelAmp", "ATable", "APos", "AWarp", "AWarpAmt", "AOct", "ASemi", "AFine", "ALevel", "BTable", "BPos",
            "BWarp", "BWarpAmt", "BOct", "BSemi", "BFine", "BLevel", "SubShape", "SubOct", "SubLevel", "NoiseLevel",
            "FType", "FCutoff", "FReso", "FDrive", "FEnv", "FKey", "FVel", "AmpA", "AmpD", "AmpS", "AmpR", "FltA",
            "FltD", "FltS", "FltR", "ModA", "ModD", "ModS", "ModR", "L1Shape", "L1Rate", "L1Sync", "L1Retrig", "L1Fade",
            "L2Shape", "L2Rate", "L2Sync", "L2Retrig", "L2Fade", "M1Src", "M1Dst", "M1Amt", "M2Src", "M2Dst", "M2Amt",
            "M3Src", "M3Dst", "M3Amt", "M4Src", "M4Dst", "M4Amt", "M5Src", "M5Dst", "M5Amt", "M6Src", "M6Dst", "M6Amt",
            "M7Src", "M7Dst", "M7Amt", "M8Src", "M8Dst", "M8Amt",
        };

        public static readonly StateMigrations Chain = new StateMigrations(Params.Table)
            .Step(Version0, s => s.Add("L1Feel", 0).Add("L2Feel", 0));
    }
}
