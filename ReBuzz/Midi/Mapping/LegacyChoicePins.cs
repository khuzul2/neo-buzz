// Fork (docs/sdk/tempo-sync.md, SYN1): a version 1 MIDI map has no raw range, so it would drive a choice list over everything the list holds
// today. The tempo-sync lists of the Neo machines grew at their end (1/64, bars, Quintuplet); a v1 map was made before they did, so it is held
// to the old last choice, as a v2 map is by its saved raw range. Keyed by the machine's name and the parameter's name, the same pairs as
// NeoBuzz.Dsp's LegacyChoiceSpans.SyncPins (a test in NeoBuzz.MachineTests keeps the two tables equal).

namespace ReBuzz.Midi.Mapping
{
    public static class LegacyChoicePins
    {
        public static readonly (string Machine, string Parameter, int OldMax)[] All =
        {
            ("Neo Delay", "Sync", 8),
            ("Neo Delay", "Feel", 2),
            ("Neo LFO", "Feel", 2),
            ("Neo Random", "Feel", 2),
            ("Neo Arp", "Rate", 6),
            ("Neo Arp", "Feel", 2),
            ("Neo Pulse", "Rate", 6),
            ("Neo Pulse", "Feel", 2),
            ("Neo Notes", "Fixed rate", 6),
            ("Neo Notes", "Fixed feel", 2),
            ("Neo Notes", "Echo rate", 6),
            ("Neo Notes", "Echo feel", 2),
            ("Neo Poly", "LFO1 sync", 11),
            ("Neo Poly", "LFO2 sync", 11),
            ("Neo Wave", "LFO1 sync", 11),
            ("Neo Wave", "LFO2 sync", 11),
            ("Neo Ensemble", "Sync", 13),
            ("Neo Filter", "LFO sync", 18),
        };

        /// <summary>The old last choice of a parameter whose list has grown since version 1 maps were made; null for every other.</summary>
        public static int? OldMax(string machineName, string parameterName)
        {
            if (machineName == null || parameterName == null) return null;
            foreach (var pin in All)
                if (pin.Machine == machineName && pin.Parameter == parameterName) return pin.OldMax;
            return null;
        }
    }
}
