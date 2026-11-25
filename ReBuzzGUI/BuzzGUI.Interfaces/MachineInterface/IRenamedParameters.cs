#nullable disable
// Fork (docs/specs/machines.md §3.2, "Renamed parameters and user presets"): a machine whose global parameters were renamed says
// what they were called before. A user preset saved before presets stored each parameter's id names its parameters only by name, so
// a rename would send its value to whichever parameter has that name now (Neo Tune: "Amount" was the correction strength, and FX1's
// effect strength is called Amount now). BuzzGUI.Common's Preset matches a former name to its parameter while the preset doesn't
// know the parameter's current name. Plain data, read on the UI thread.

using System.Collections.Generic;

namespace Buzz.MachineInterface
{
    public interface IRenamedParameters
    {
        /// <summary>A former name of a global parameter → that parameter's key (<see cref="IVersionedMachine.ParameterKeys"/>). Empty when nothing was renamed.</summary>
        IReadOnlyDictionary<string, string> FormerParameterNames { get; }
    }
}
