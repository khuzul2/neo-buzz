// Fork (docs/specs/history-snapshots.md HS1): an edit that replaces a machine's saved state (a sample, a wavetable, a
// bank) as a step of the song's history, in the same group as the parameter steps the same gesture makes. The machine's
// state classes are copy-on-write (a new object for each change), so a step holds two references; the big strings in
// them (base64 sample data) are counted once against the history's memory cap however many steps share them.
// As with ParamEdits, only the studio's kit (EditorContext.Edits set) records: WPF's editors make no steps, as before.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace NeoBuzz.Editor
{
    internal static class StateEdit
    {
        /// <summary>
        /// Runs <paramref name="edit"/> (which changes the machine's state, and may set parameters through <paramref name="ctx"/>) and
        /// puts the state's change on the song's history, in one step with those parameters. <paramref name="get"/> and
        /// <paramref name="set"/> are the machine's own state property.
        /// </summary>
        public static void Run<TMachine, TState>(EditorContext ctx, string name, TMachine machine, Func<TMachine, TState> get,
                                                 Action<TMachine, TState> set, Action edit)
            where TMachine : class
            where TState : class
        {
            var edits = ctx?.Edits;
            var graph = ctx?.Machine?.Graph;
            if (edits == null || graph == null || machine == null)
            {
                edit();
                return;
            }
            var before = get(machine);
            graph.BeginActionGroup();
            edits.Begin();
            try
            {
                edit();
                var after = get(machine);
                if (!ReferenceEquals(before, after))
                    graph.Do(new StateEditAction<TMachine, TState>(ctx.Machine, name, before, after, get, set));
            }
            finally
            {
                edits.End();
                graph.EndActionGroup();
            }
        }

        /// <summary>The big strings a state object holds, each with its size in bytes: what a step shares with the others.</summary>
        internal static IEnumerable<KeyValuePair<object, long>> Blobs(object state)
        {
            if (state == null) yield break;
            foreach (var p in state.GetType().GetProperties())
            {
                if (p.PropertyType != typeof(string) || !p.CanRead || p.GetIndexParameters().Length != 0) continue;
                var value = p.GetValue(state) as string;
                if (value != null && value.Length >= 4096) yield return new KeyValuePair<object, long>(value, 2L * value.Length);
            }
        }
    }

    /// <summary>
    /// A state change as a step: the machine's state before and after. It finds its machine when it runs (the same one while it
    /// is in the song, else one of its name). The state is already in place when the step is made, so the first Do does nothing.
    /// </summary>
    internal sealed class StateEditAction<TMachine, TState> : IAction, IHistoryAction
        where TMachine : class
        where TState : class
    {
        readonly IMachine machine;
        readonly IMachineGraph graph;
        readonly string machineName;
        readonly string name;
        readonly TState before, after;
        readonly Func<TMachine, TState> get;
        readonly Action<TMachine, TState> set;
        bool skipDo = true;

        public StateEditAction(IMachine machine, string name, TState before, TState after, Func<TMachine, TState> get, Action<TMachine, TState> set)
        {
            this.machine = machine;
            graph = machine.Graph;
            machineName = machine.Name;
            this.name = name;
            this.before = before;
            this.after = after;
            this.get = get;
            this.set = set;
        }

        string IHistoryAction.HistoryName => name;
        long IHistoryAction.HistoryBytes => 0;
        IEnumerable<KeyValuePair<object, long>> IHistoryAction.HistoryBlobs => StateEdit.Blobs(before).Concat(StateEdit.Blobs(after));

        public void Do()
        {
            if (skipDo) { skipDo = false; return; }
            Apply(after);
        }

        public void Undo() => Apply(before);

        void Apply(TState state)
        {
            var machines = graph?.Machines;
            var m = machines == null || machines.Contains(machine) ? machine : machines.FirstOrDefault(x => x.Name == machineName);
            if (m?.ManagedMachine is TMachine target) set(target, state);
        }
    }
}
