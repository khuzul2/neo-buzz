#nullable disable
// Neo Poly's versioned state (docs/specs/machines.md section 3.4), in the engine folder so the tests that load old songs can reach it.

using NeoBuzz.Dsp;

namespace NeoBuzz.Poly
{
    internal static class PolyMigrations
    {
        // State version 1 (machines.md §3.2): F1 type gained Diode 18 (a wider range; every value a song saved means what it did) and the acid
        // parameters were appended. Nothing a song saved moves: the new ones take their defaults, which change nothing.
        public static readonly StateMigrations Chain = new StateMigrations(Params.Table)
            .Step(new[]
            {
                "VoiceMode", "Polyphony", "Unison", "UniDetune", "UniSpread", "GlideMode", "Glide", "BendRange",
                "Drift", "Volume", "VelAmp", "O1Shape", "O1PW", "O1Oct", "O1Semi", "O1Fine",
                "O1Level", "O2Shape", "O2PW", "O2Oct", "O2Semi", "O2Fine", "O2Level", "O2Sync",
                "O2Fm", "O3Shape", "O3Oct", "O3Semi", "O3Fine", "O3Level", "RingLevel", "NoiseLevel",
                "NoiseColor", "F1Type", "F1Cutoff", "F1Reso", "F1Drive", "F1Env", "F1Key", "F1Vel",
                "F2Type", "F2Cutoff", "F2Reso", "F2Env", "Routing", "AmpA", "AmpD", "AmpS",
                "AmpR", "FltA", "FltD", "FltS", "FltR", "ModA", "ModD", "ModS",
                "ModR", "L1Shape", "L1Rate", "L1Sync", "L1Retrig", "L1Fade", "L2Shape", "L2Rate",
                "L2Sync", "L2Retrig", "L2Fade", "M1Src", "M1Dst", "M1Amt", "M2Src", "M2Dst",
                "M2Amt", "M3Src", "M3Dst", "M3Amt", "M4Src", "M4Dst", "M4Amt", "M5Src",
                "M5Dst", "M5Amt", "M6Src", "M6Dst", "M6Amt", "M7Src", "M7Dst", "M7Amt",
                "M8Src", "M8Dst", "M8Amt",
            }, s => { })
            // State version 2 (AC3): the acid bounds were appended (Filter range, Sweep limit, Filter quality, Soft start). A song saved before
            // them takes their defaults.
            .Step(new[]
            {
                "VoiceMode", "Polyphony", "Unison", "UniDetune", "UniSpread", "GlideMode", "Glide", "BendRange",
                "Drift", "Volume", "VelAmp", "O1Shape", "O1PW", "O1Oct", "O1Semi", "O1Fine",
                "O1Level", "O2Shape", "O2PW", "O2Oct", "O2Semi", "O2Fine", "O2Level", "O2Sync",
                "O2Fm", "O3Shape", "O3Oct", "O3Semi", "O3Fine", "O3Level", "RingLevel", "NoiseLevel",
                "NoiseColor", "F1Type", "F1Cutoff", "F1Reso", "F1Drive", "F1Env", "F1Key", "F1Vel",
                "F2Type", "F2Cutoff", "F2Reso", "F2Env", "Routing", "AmpA", "AmpD", "AmpS",
                "AmpR", "FltA", "FltD", "FltS", "FltR", "ModA", "ModD", "ModS",
                "ModR", "L1Shape", "L1Rate", "L1Sync", "L1Retrig", "L1Fade", "L2Shape", "L2Rate",
                "L2Sync", "L2Retrig", "L2Fade", "M1Src", "M1Dst", "M1Amt", "M2Src", "M2Dst",
                "M2Amt", "M3Src", "M3Dst", "M3Amt", "M4Src", "M4Dst", "M4Amt", "M5Src",
                "M5Dst", "M5Amt", "M6Src", "M6Dst", "M6Amt", "M7Src", "M7Dst", "M7Amt",
                "M8Src", "M8Dst", "M8Amt",
                "AccentThr", "Accent", "Slide", "SlideTime", "AccentDecay", "AccentSweep", "AccentLevel",
            }, s => s.Add("AcidRange", 797).Add("AcidSweepMax", 500).Add("AcidQuality", 1).Add("AcidSoft", 650))
            // State version 3 (SYN1, tempo sync): LFO feel was appended and LFO sync gained 1/64 and 8 bars; an old song loads Straight, as before.
            .Step(new[]
            {
                "VoiceMode", "Polyphony", "Unison", "UniDetune", "UniSpread", "GlideMode", "Glide", "BendRange", "Drift",
                "Volume", "VelAmp", "O1Shape", "O1PW", "O1Oct", "O1Semi", "O1Fine", "O1Level", "O2Shape", "O2PW", "O2Oct",
                "O2Semi", "O2Fine", "O2Level", "O2Sync", "O2Fm", "O3Shape", "O3Oct", "O3Semi", "O3Fine", "O3Level",
                "RingLevel", "NoiseLevel", "NoiseColor", "F1Type", "F1Cutoff", "F1Reso", "F1Drive", "F1Env", "F1Key",
                "F1Vel", "F2Type", "F2Cutoff", "F2Reso", "F2Env", "Routing", "AmpA", "AmpD", "AmpS", "AmpR", "FltA", "FltD",
                "FltS", "FltR", "ModA", "ModD", "ModS", "ModR", "L1Shape", "L1Rate", "L1Sync", "L1Retrig", "L1Fade",
                "L2Shape", "L2Rate", "L2Sync", "L2Retrig", "L2Fade", "M1Src", "M1Dst", "M1Amt", "M2Src", "M2Dst", "M2Amt",
                "M3Src", "M3Dst", "M3Amt", "M4Src", "M4Dst", "M4Amt", "M5Src", "M5Dst", "M5Amt", "M6Src", "M6Dst", "M6Amt",
                "M7Src", "M7Dst", "M7Amt", "M8Src", "M8Dst", "M8Amt", "AccentThr", "Accent", "Slide", "SlideTime",
                "AccentDecay", "AccentSweep", "AccentLevel", "AcidRange", "AcidSweepMax", "AcidQuality", "AcidSoft",
            }, s => s.Add("L1Feel", 0).Add("L2Feel", 0));
    }
}
