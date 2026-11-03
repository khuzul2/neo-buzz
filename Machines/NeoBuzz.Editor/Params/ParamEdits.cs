// Fork (docs/specs/studio-mvp.md S1): undo steps for a Neo editor's edits, on the song's undo
// history (the machine graph's action stack), as the Machine View's edits are. Toolkit-free; the
// studio's kit (NeoBuzz.Editor.Avalonia) gives each editor one. WPF's editors have none, as before.

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
        readonly Action<Action> later;
        readonly Dictionary<int, int> before = new Dictionary<int, int>();
        int depth;

        /// <param name="later">Runs an action once the current input has been handled (the toolkit's dispatcher).</param>
        public ParamEdits(IMachine machine, ParamDef[] table, Action<Action> later)
        {
            this.machine = machine;
            this.table = table;
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
            if (!before.ContainsKey(index)) before[index] = Raw(machine, index, table[index].Default);
        }

        /// <summary>Notes every parameter (before a preset loads).</summary>
        public void TouchAll()
        {
            for (int i = 0; i < table.Length; i++) Touch(i);
        }

        void Commit()
        {
            var changes = before
                .Select(kv => new ParamChange(kv.Key, kv.Value, Raw(machine, kv.Key, kv.Value)))
                .Where(c => c.Old != c.New)
                .OrderBy(c => c.Index)
                .ToArray();
            before.Clear();
            if (changes.Length == 0 || machine?.Graph == null) return;
            machine.Graph.Do(new ParamEditAction(machine, changes));
        }

        static IParameterGroup Globals(IMachine m) => m?.ParameterGroups?.FirstOrDefault(g => g.Type == ParameterGroupType.Global);

        static int Raw(IMachine m, int index, int fallback)
        {
            try
            {
                var ps = Globals(m)?.Parameters;
                return ps != null && index < ps.Count ? ps[index].GetValue(0) : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        readonly record struct ParamChange(int Index, int Old, int New);

        /// <summary>
        /// One step: the parameters' values before and after. It finds its machine when it runs: the
        /// same one while it is in the song, else one of its name (deleted and made again by undo).
        /// </summary>
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
                var ps = Globals(m)?.Parameters;
                if (ps == null) return;
                foreach (var c in changes)
                {
                    // Not recorded into patterns or session automation while the song records.
                    if (c.Index < ps.Count) ps[c.Index].SetValue(1 << 16, after ? c.New : c.Old);
                }
            }
        }
    }
}
