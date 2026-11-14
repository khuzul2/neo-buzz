// Fork (docs/specs/neo-patch.md §3, phase NM): one modulation, as the studio, the agent and the editors read it. Plain data, no host
// types, so the editor kit's toolkit-free core and its tests compile it too (Modulation.cs holds the machine-facing parts).
namespace BuzzGUI.Interfaces
{
    /// <summary>Whether a modulation target is being driven.</summary>
    public enum ModulationState
    {
        /// <summary>Driving its parameter.</summary>
        Active,
        /// <summary>The target machine is not in the song (deleted, or not loaded yet): nothing is written.</summary>
        MachineMissing,
        /// <summary>The machine is there but has no such parameter.</summary>
        ParamMissing,
        /// <summary>A parameter that is never modulated (the tempo, the ticks per beat).</summary>
        Refused,
    }

    /// <summary>One modulation: a modulator's target, as read by views and tools.</summary>
    public sealed class ModulationInfo
    {
        /// <summary>The modulator's slot (0 … 7).</summary>
        public int Slot { get; set; }
        /// <summary>The modulator machine's name and kind ("LFO", "Envelope", "Step", "Macro").</summary>
        public string Source { get; set; } = "";
        public string Kind { get; set; } = "";
        /// <summary>The target machine's current name, the parameter's name and its index in its group.</summary>
        public string Machine { get; set; } = "";
        public string Param { get; set; } = "";
        public int ParamIndex { get; set; } = -1;
        /// <summary>0 for a global parameter, else the track.</summary>
        public int Track { get; set; }
        public bool IsTrack { get; set; }
        /// <summary>−100 … +100 %.</summary>
        public int Depth { get; set; } = 100;
        public bool Bipolar { get; set; }
        /// <summary>The target's range the modulation spans, raw; <see cref="int.MinValue"/> when it spans the parameter's own.</summary>
        public int RangeMin { get; set; } = int.MinValue;
        public int RangeMax { get; set; } = int.MinValue;
        /// <summary>−100 … +100: bends the modulator's shape (0 is straight).</summary>
        public int Curve { get; set; }
        public ModulationState State { get; set; }
        /// <summary>The parameter's own range, raw, and the range the safe-range hook allows (the same when none).</summary>
        public int Min { get; set; }
        public int Max { get; set; }
        public int SafeMin { get; set; }
        public int SafeMax { get; set; }
        /// <summary>The value you set (raw), the value written last, and the extremes the modulation can reach from the base.</summary>
        public int Base { get; set; }
        public int Current { get; set; }
        public int Low { get; set; }
        public int High { get; set; }
    }
}
