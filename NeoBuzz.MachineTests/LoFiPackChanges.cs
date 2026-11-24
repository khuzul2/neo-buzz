namespace NeoBuzz.MachineTests
{
    /// <summary>What changed per preset in the second listening pack (LF2), for its README.</summary>
    internal static class LoFiPackChanges
    {
        internal static readonly HashSet<string> Retuned = new()
        {
            "Crush — 8-bit", "Crush — 12-bit sampler", "Crush — Crushed drums", "Tape — Cassette", "Vinyl — Dusty vinyl", "Radio — Telephone", "Warm — Gentle warmth",
        };

        internal static readonly Dictionary<string, string> Text = new()
        {
            ["Crush — 8-bit"] = "still 8 bits, but the hold falls from 22 kHz to 6 kHz with the aliasing left in, so the top of the drums and the chords folds back as stepped grit (the 8-bit alone was too mild to hear).",
            ["Crush — 12-bit sampler"] = "still 12 bits; the hold falls from 26 kHz to 13 kHz behind the anti-alias filter (the filter's corner from 10.4 kHz to 5.2 kHz), clock jitter 0 to 12 %, hiss -78 to -62 dB, tone -15 to -20 %: a dull, grainy, metallic sampler.",
            ["Crush — Crushed drums"] = "6 bits at 14 kHz blended 60 % becomes 5 bits at 9.5 kHz, jitter 8 to 10 %, all wet: the dry no longer hides the crush.",
            ["Crush — Broken toy"] = "unchanged (you kept it).",
            ["Tape — Cassette"] = "hiss -52 to -43 dB, wow 12 to 18 ct (rate 0.8 to 1.1 Hz), flutter 6 to 9 ct, drift 3 to 5 ct, tone -35 to -42 % (the top starts to roll at 4.6 kHz, not 5.9 kHz).",
            ["Tape — Worn tape"] = "unchanged (you kept it).",
            ["Vinyl — Dusty vinyl"] = "crackle 14 to 24 pops a second and from -34 to -22 dB, hiss -62 to -54 dB, wow 3 to 7 ct, tone -20 to -25 %.",
            ["Radio — AM radio"] = "unchanged (you kept it).",
            ["Radio — Telephone"] = "8 bits dithered becomes 5 bits undithered (the distortion is the point), hiss -58 to -46 dB, crackle 3 to 8 a second and from -40 to -30 dB, tone -55 to -66 % (the top ends at 2.0 kHz, not 2.9 kHz).",
            ["Warm — Gentle warmth"] = "still subtle, now plain against the dry: tone -12 to -30 % (the -3 dB point from 13 kHz to 7 kHz), hiss -76 to -58 dB, wow 4 to 8 ct, drift 5 to 8 ct.",
        };
    }
}
