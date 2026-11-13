#nullable disable
// Neo machines' Buzz glue for the canvas modulators (docs/specs/neo-patch.md §3): finding a target's parameter and the range it may be
// modulated within. The safe range comes from the host's hook (the manifest's safe_range, machine-safety.md §3.4); the master's own
// rules are here: its volume never goes past 0 dB, and the tempo and ticks per beat are never modulated.

using System;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoBuzz.Machine
{
    internal static class ModResolver
    {
        /// <summary>The parameter of a machine by name (else by its index in the group), in the global or the track group.</summary>
        public static IParameter Find(IMachine machine, bool isTrack, string name, int index)
        {
            IParameter byIndex = null;
            foreach (var group in machine.ParameterGroups)
            {
                if (group.Type != (isTrack ? ParameterGroupType.Track : ParameterGroupType.Global)) continue;
                foreach (var p in group.Parameters)
                {
                    if (p.Type == ParameterType.Note) continue;
                    if (p.Name == name) return p;
                    if (p.IndexInGroup == index) byIndex = p;
                }
            }
            return byIndex;
        }

        public static bool IsMaster(IMachine machine) => machine.DLL?.Info?.Type == MachineType.Master;

        /// <summary>The raw range modulation may use for a parameter (the master's rules, then the host's hook); null: its own.</summary>
        public static (int Min, int Max)? SafeRange(IMachine machine, IParameter parameter)
        {
            if (IsMaster(machine) && parameter.IndexInGroup == 0 && parameter.Group?.Type == ParameterGroupType.Global)
                return (0, 0x4000);   // the volume: 0 raw is 0 dB; a modulator never takes it above
            try { return ModulationBoard.SafeRange?.Invoke(machine, parameter); }
            catch (Exception) { return null; }
        }

        /// <summary>A port for a parameter, or null (with the reason) when it is never modulated.</summary>
        public static ParamPort Port(IMachine machine, IParameter parameter, int track, out string refusal)
        {
            refusal = null;
            if (IsMaster(machine) && parameter.IndexInGroup != 0)
            {
                refusal = $"{parameter.Name} on the master is the song's timing; a modulator doesn't move it";
                return null;
            }
            var safe = SafeRange(machine, parameter);
            return new ParamPort(machine, parameter, track, safe?.Min ?? parameter.MinValue, safe?.Max ?? parameter.MaxValue);
        }
    }
}
