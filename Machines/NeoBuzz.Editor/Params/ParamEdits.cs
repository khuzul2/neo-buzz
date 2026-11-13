// Fork (docs/specs/studio-mvp.md S1): undo steps for a Neo editor's edits, on the song's undo
// history (the machine graph's action stack), as the Machine View's edits are. Toolkit-free; the
// studio's kit (NeoBuzz.Editor.Avalonia) gives each editor one. WPF's editors have none, as before.
// Fork (docs/specs/mcp-server.md §12.11, U4): with slots (EditorContext), the manifest editor's track
// parameters too, each on its track.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    /// <summary>
    /// Collects the parameters a gesture changes (a knob drag, a click, a handle drag, a preset) and
    /// puts them on the song's undo history as one step when the gesture ends. A write outside a
    /// gesture is a step of its own, ended when the toolkit's dispatcher next runs, so writes made
    /// together (a DX7 voice's 150 parameters) stay one step.
    /// </summary>
    internal sealed class ParamEdits
    {
        readonly IMachine machine;
        readonly ParamDef[] table;
        readonly IReadOnlyList<ParamSlot> slots;
        readonly Action<Action> later;
        readonly Dictionary<int, int> before = new Dictionary<int, int>();
        int depth;

        /// <param name="later">Runs an action once the current input has been handled (the toolkit's dispatcher).</param>
        /// <param name="slots">Where each table entry is (EditorContext.Slots); null: global parameter i.</param>
        public ParamEdits(IMachine machine, ParamDef[] table, Action<Action> later, IReadOnlyList<ParamSlot> slots = null)
        {
            this.machine = machine;
            this.table = table;
            this.slots = slots;
            this.later = later;
        }

        /// <summary>True while a gesture is open.</summary>
        public bool Open => depth > 0;

        public void Begin() => depth++;

        public void End()
        {
            if (depth == 0) return;
            if (--depth == 0) Commit();
        }

        /// <summary>Notes a parameter's value before it is written.</summary>
        public void Touch(int index)
        {
            if (index < 0 || index >= table.Length) return;
            if (depth == 0)
            {
                depth++;
                later(End);
            }
            if (!before.ContainsKey(index)) before[index] = Raw(index, table[index].Default);
        }

        /// <summary>Notes every parameter (before a preset loads).</summary>
        public void TouchAll()
        {
            for (int i = 0; i < table.Length; i++) Touch(i);
        }

        void Commit()
        {
            var changes = before
                .Select(kv => new ParamChange(Where(kv.Key), kv.Value, Raw(kv.Key, kv.Value)))
                .Where(c => c.Old != c.New)
                .OrderBy(c => c.At.Group).ThenBy(c => c.At.Track).ThenBy(c => c.At.Index)
                .ToArray();
            before.Clear();
            if (changes.Length == 0 || machine?.Graph == null) return;
            machine.Graph.Do(new ParamEditAction(machine, changes));
        }

        static IParameterGroup Group(IMachine m, ParameterGroupType type) => m?.ParameterGroups?.FirstOrDefault(g => g.Type == type);

        /// <summary>A table entry's parameter by group, index in its group and track (the Neo editors': global i, track 0).</summary>
        ParamAt Where(int index)
        {
            if (slots == null || index < 0 || index >= slots.Count) return new ParamAt(ParameterGroupType.Global, index, 0);
            var s = slots[index];
            return new ParamAt(s.Parameter?.Group?.Type ?? ParameterGroupType.Global, s.Parameter?.IndexInGroup ?? index, s.Track);
        }

        int Raw(int index, int fallback)
        {
            try
            {
                return Find(machine, Where(index))?.GetValue(Where(index).Track) ?? fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        static IParameter Find(IMachine m, ParamAt at)
        {
            var ps = Group(m, at.Group)?.Parameters;
            return ps != null && at.Index >= 0 && at.Index < ps.Count ? ps[at.Index] : null;
        }

        readonly record struct ParamAt(ParameterGroupType Group, int Index, int Track);

        readonly record struct ParamChange(ParamAt At, int Old, int New);

        /// <summary>
        /// One step: the parameters' values before and after. It finds its machine when it runs: the
        /// same one while it is in the song, else one of its name (deleted and made again by undo).
        /// </summary>
        [BuzzGUI.Common.HistoryName("Edit parameters")]
        sealed class ParamEditAction : IAction
        {
            readonly IMachine machine;
            readonly IMachineGraph graph;
            readonly string name;
            readonly ParamChange[] changes;

            public ParamEditAction(IMachine machine, ParamChange[] changes)
            {
                this.machine = machine;
                graph = machine.Graph;
                name = machine.Name;
                this.changes = changes;
            }

            public void Do() => Apply(after: true);

            public void Undo() => Apply(after: false);

            void Apply(bool after)
            {
                var machines = graph?.Machines;
                var m = machines == null || machines.Contains(machine) ? machine : machines.FirstOrDefault(x => x.Name == name);
                if (m == null) return;
                foreach (var c in changes)
                {
                    // Not recorded into patterns or session automation while the song records.
                    var p = Find(m, c.At);
                    if (p != null && (c.At.Group != ParameterGroupType.Track || c.At.Track < m.TrackCount)) p.SetValue(c.At.Track | 1 << 16, after ? c.New : c.Old);
                }
            }
        }
    }
}
