// Fork (docs/specs/ux-overhaul.md P3c, decision 50): which columns the studio's pattern editor shows, and in what
// order. Display only: the pattern's columns and every event in them are as they were (a hidden column is not removed from
// the pattern, as the Parameters dialog's removal does). The WPF build shows every column as before (PatternVM calls this
// under #if !WINDOWS).
//
//  - Order. A set (a track's, or the globals) that has a Note column comes before those that don't; inside a set the Note
//    column comes first, then the Wave column it fills (when it was right after the Note), then the velocity columns, then
//    the rest in the machine's order.
//  - Notes and velocity are always shown. Any other column is shown when it holds an event on any track, or when the user
//    asked for it (+ Automation); the rest wait behind that control.

using BuzzGUI.Interfaces;
using WDE.ModernPatternEditor.MPEStructures;
using static WDE.ModernPatternEditor.MPEStructures.MPEPattern;

namespace WDE.ModernPatternEditor
{
    /// <summary>A parameter whose columns the editor hides for now, as + Automation lists it.</summary>
    public sealed class HiddenColumn
    {
        public IParameter Parameter { get; init; }
        public string Name { get; init; }
        /// <summary>"Global" or "Track".</summary>
        public string Group { get; init; }
        public override string ToString() => Group + ": " + Name;
    }

    internal static class ColumnPlan
    {
        /// <summary>
        /// A velocity column: a track parameter that is not a note, has a range, and is named for velocity as the agent finds it
        /// (TrackerColumnNotes: "vel" in the name) or for its level as Buzz's trackers name theirs ("Volume", "Vol"). A global
        /// "Volume" is the machine's output level, not a note's.
        /// </summary>
        internal static bool IsVelocity(IParameter p) =>
            p.Type != ParameterType.Note && p.MaxValue > p.MinValue && p.Name != null && p.Group?.Type == ParameterGroupType.Track
            && (p.Name.Contains("vel", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Volume", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Vol", StringComparison.OrdinalIgnoreCase));

        /// <summary>The sets in the order they're shown: those with a Note column first, the others as they were.</summary>
        internal static IList<MPEParameterSet> OrderSets(IEnumerable<MPEParameterSet> sets) =>
            sets.OrderBy(s => s.parameters.Any(p => p.Type == ParameterType.Note) ? 0 : 1).ToList();

        /// <summary>
        /// A set's parameters in the shown order, with whether each is always shown (a note, the wave a note fills, a velocity).
        /// </summary>
        internal static List<(IParameter Parameter, bool Always)> Arrange(IList<IParameter> parameters)
        {
            var result = new List<(IParameter, bool)>(parameters.Count);
            var taken = new HashSet<IParameter>();
            for (int i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                if (p.Type != ParameterType.Note) continue;
                result.Add((p, true));
                taken.Add(p);
                if (i + 1 < parameters.Count && parameters[i + 1].Flags.HasFlag(ParameterFlags.Wave) && taken.Add(parameters[i + 1]))
                    result.Add((parameters[i + 1], true));
            }
            foreach (var p in parameters)
                if (!taken.Contains(p) && IsVelocity(p)) { result.Add((p, true)); taken.Add(p); }
            foreach (var p in parameters)
                if (!taken.Contains(p)) result.Add((p, false));
            return result;
        }
    }
}
