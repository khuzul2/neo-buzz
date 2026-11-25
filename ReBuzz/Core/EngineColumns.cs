using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork addition (0.6.3): a copy of a clip's engine columns (<see cref="ClipCore.Columns"/>:
    /// the MIDI columns the engine plays itself, and parameter columns), events and metadata
    /// included, to put into a clip made again. Clip editors keep their own data and copy
    /// it themselves; these columns were lost whenever an undo made a clip again (upstream's
    /// ClipClip only fills columns that already exist, and a new clip has none).
    /// Session takes (0.7) are recorded into a MIDI column.
    /// </summary>
    internal sealed class EngineColumns
    {
        sealed class Column
        {
            public ClipColumnType Type;
            public IMachine Target;          // null: the clip's own machine
            public int Group;                // parameter group index, -1 for MIDI
            public int Index;                // parameter index in its group, -1 for MIDI
            public int Track;
            public ClipEvent[] Events;
            public KeyValuePair<string, string>[] Metadata;
        }

        readonly List<Column> columns;

        EngineColumns(List<Column> columns) => this.columns = columns;

        public bool IsEmpty => columns.Count == 0;

        /// <summary>The engine columns of <paramref name="clip"/> (empty if it has none).</summary>
        public static EngineColumns Capture(IClip clip)
        {
            var list = new List<Column>();
            if (clip != null)
            {
                foreach (var c in clip.Columns)
                {
                    var parameter = c.Parameter;
                    var target = c.Machine;
                    bool midi = c.Type == ClipColumnType.MIDI || parameter == null || parameter.IndexInGroup < 0;
                    list.Add(new Column
                    {
                        Type = c.Type,
                        Target = ReferenceEquals(target, clip.Machine) ? null : target,
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
        /// Puts the columns into <paramref name="clip"/> (made again, without columns), after
        /// any it has. Columns of the clip's own machine go to its machine now; columns of
        /// other machines only while those are in <paramref name="songMachines"/>.
        /// </summary>
        public void RestoreInto(IClip clip, ICollection<IMachine> songMachines)
        {
            if (clip == null || columns.Count == 0) return;
            foreach (var c in columns)
            {
                var target = c.Target ?? clip.Machine;
                if (target == null || (c.Target != null && !songMachines.Contains(c.Target))) continue;
                int at = clip.Columns.Count;
                if (c.Group < 0 || c.Index < 0)
                {
                    if (c.Target == null)
                        clip.InsertColumn(at, (IMachine)null);
                    else if (target is MachineCore mc && clip is ClipCore pc)
                        clip.InsertColumn(at, ParameterCore.GetMidiParameter(mc, pc.Dispatcher), c.Track);
                    else
                        continue;
                }
                else
                {
                    if (c.Group >= target.ParameterGroups.Count || c.Index >= target.ParameterGroups[c.Group].Parameters.Count) continue;
                    clip.InsertColumn(at, target.ParameterGroups[c.Group].Parameters[c.Index], c.Track);
                }
                if (clip.Columns.Count <= at) continue;
                var column = clip.Columns[at];
                foreach (var m in c.Metadata)
                    column.Metadata[m.Key] = m.Value;
                if (c.Events.Length > 0)
                    column.SetEvents(c.Events, true);
            }
        }

        /// <summary>Copies the engine columns of <paramref name="source"/> into <paramref name="copy"/> (a clone just made).</summary>
        public static void Copy(IClip source, IClip copy, ICollection<IMachine> songMachines) =>
            Capture(source).RestoreInto(copy, songMachines);
    }
}
