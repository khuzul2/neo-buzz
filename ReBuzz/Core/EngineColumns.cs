using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork addition (0.6.3): a copy of a pattern's engine columns (<see cref="PatternCore.Columns"/>:
    /// the MIDI columns the engine plays itself, and parameter columns), events and metadata
    /// included, to put into a pattern made again. Pattern editors keep their own data and copy
    /// it themselves; these columns were lost whenever an undo made a pattern again (upstream's
    /// PatternClip only fills columns that already exist, and a new pattern has none).
    /// Session takes (0.7) are recorded into a MIDI column.
    /// </summary>
    internal sealed class EngineColumns
    {
        sealed class Column
        {
            public PatternColumnType Type;
            public IMachine Target;          // null: the pattern's own machine
            public int Group;                // parameter group index, -1 for MIDI
            public int Index;                // parameter index in its group, -1 for MIDI
            public int Track;
            public PatternEvent[] Events;
            public KeyValuePair<string, string>[] Metadata;
        }

        readonly List<Column> columns;

        EngineColumns(List<Column> columns) => this.columns = columns;

        public bool IsEmpty => columns.Count == 0;

        /// <summary>The engine columns of <paramref name="pattern"/> (empty if it has none).</summary>
        public static EngineColumns Capture(IPattern pattern)
        {
            var list = new List<Column>();
            if (pattern != null)
            {
                foreach (var c in pattern.Columns)
                {
                    var parameter = c.Parameter;
                    var target = c.Machine;
                    bool midi = c.Type == PatternColumnType.MIDI || parameter == null || parameter.IndexInGroup < 0;
                    list.Add(new Column
                    {
                        Type = c.Type,
                        Target = ReferenceEquals(target, pattern.Machine) ? null : target,
                        Group = midi || parameter.Group == null || target == null ? -1 : target.ParameterGroups.IndexOf(parameter.Group),
                        Index = midi ? -1 : parameter.IndexInGroup,
                        Track = c.Track,
                        Events = c.GetEvents(int.MinValue, int.MaxValue).ToArray(),
                        Metadata = c.Metadata.ToArray()
                    });
                }
            }
            return new EngineColumns(list);
        }

        /// <summary>
        /// Puts the columns into <paramref name="pattern"/> (made again, without columns), after
        /// any it has. Columns of the pattern's own machine go to its machine now; columns of
        /// other machines only while those are in <paramref name="songMachines"/>.
        /// </summary>
        public void RestoreInto(IPattern pattern, ICollection<IMachine> songMachines)
        {
            if (pattern == null || columns.Count == 0) return;
            foreach (var c in columns)
            {
                var target = c.Target ?? pattern.Machine;
                if (target == null || (c.Target != null && !songMachines.Contains(c.Target))) continue;
                int at = pattern.Columns.Count;
                if (c.Group < 0 || c.Index < 0)
                {
                    if (c.Target == null)
                        pattern.InsertColumn(at, (IMachine)null);
                    else if (target is MachineCore mc && pattern is PatternCore pc)
                        pattern.InsertColumn(at, ParameterCore.GetMidiParameter(mc, pc.Dispatcher), c.Track);
                    else
                        continue;
                }
                else
                {
                    if (c.Group >= target.ParameterGroups.Count || c.Index >= target.ParameterGroups[c.Group].Parameters.Count) continue;
                    pattern.InsertColumn(at, target.ParameterGroups[c.Group].Parameters[c.Index], c.Track);
                }
                if (pattern.Columns.Count <= at) continue;
                var column = pattern.Columns[at];
                foreach (var m in c.Metadata)
                    column.Metadata[m.Key] = m.Value;
                if (c.Events.Length > 0)
                    column.SetEvents(c.Events, true);
            }
        }

        /// <summary>Copies the engine columns of <paramref name="source"/> into <paramref name="copy"/> (a clone just made).</summary>
        public static void Copy(IPattern source, IPattern copy, ICollection<IMachine> songMachines) =>
            Capture(source).RestoreInto(copy, songMachines);
    }
}
