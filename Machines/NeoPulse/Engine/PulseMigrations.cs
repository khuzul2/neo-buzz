#nullable disable
// Neo Pulse's versioned state (docs/specs/machines.md section 3.4), in the engine folder so the tests that load old songs can reach it.
// Version 1 (SYN1, tempo sync): the Feel and timing parameters were appended. A song saved before them loads with the values that
// sound as before (Straight, Song tempo), not the table's defaults.

using NeoBuzz.Dsp;

namespace NeoBuzz.Pulse
{
    internal static class PulseMigrations
    {
        /// <summary>The keys of version 0, in order (what a song saved before SYN1 contains).</summary>
        public static readonly string[] Version0 =
        {
            "rate", "feel", "swing", "humanize_time", "humanize_velocity", "fill_chance", "seed", "L1Steps", "L1Pulses",
            "L1Rot", "L1Chance", "L1AccPulses", "L1AccRot", "L1Note", "L1Vel", "L1AccVel", "L1Gate", "L1Ratchet",
            "L1RatchetCh", "L1Div", "L1Ch", "L1Mute", "L2Steps", "L2Pulses", "L2Rot", "L2Chance", "L2AccPulses",
            "L2AccRot", "L2Note", "L2Vel", "L2AccVel", "L2Gate", "L2Ratchet", "L2RatchetCh", "L2Div", "L2Ch", "L2Mute",
            "L3Steps", "L3Pulses", "L3Rot", "L3Chance", "L3AccPulses", "L3AccRot", "L3Note", "L3Vel", "L3AccVel",
            "L3Gate", "L3Ratchet", "L3RatchetCh", "L3Div", "L3Ch", "L3Mute", "L4Steps", "L4Pulses", "L4Rot", "L4Chance",
            "L4AccPulses", "L4AccRot", "L4Note", "L4Vel", "L4AccVel", "L4Gate", "L4Ratchet", "L4RatchetCh", "L4Div",
            "L4Ch", "L4Mute", "L5Steps", "L5Pulses", "L5Rot", "L5Chance", "L5AccPulses", "L5AccRot", "L5Note", "L5Vel",
            "L5AccVel", "L5Gate", "L5Ratchet", "L5RatchetCh", "L5Div", "L5Ch", "L5Mute", "L6Steps", "L6Pulses", "L6Rot",
            "L6Chance", "L6AccPulses", "L6AccRot", "L6Note", "L6Vel", "L6AccVel", "L6Gate", "L6Ratchet", "L6RatchetCh",
            "L6Div", "L6Ch", "L6Mute", "L7Steps", "L7Pulses", "L7Rot", "L7Chance", "L7AccPulses", "L7AccRot", "L7Note",
            "L7Vel", "L7AccVel", "L7Gate", "L7Ratchet", "L7RatchetCh", "L7Div", "L7Ch", "L7Mute", "L8Steps", "L8Pulses",
            "L8Rot", "L8Chance", "L8AccPulses", "L8AccRot", "L8Note", "L8Vel", "L8AccVel", "L8Gate", "L8Ratchet",
            "L8RatchetCh", "L8Div", "L8Ch", "L8Mute",
        };

        public static readonly StateMigrations Chain = new StateMigrations(Params.Table)
            .Step(Version0, s => s.Add("timing", 0).Add("free_ms", 125));
    }
}
