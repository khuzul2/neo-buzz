#nullable disable
// Neo Patch (docs/specs/neo-patch.md §4.5, §4.6): the song's history for patch edits, shared by the inner canvas (NeoPatchEditor.cs) and the agent's
// patch_* tools (the machine's IPatchAgent), so an edit is one step on the song's undo stack whoever made it.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace NeoBuzz.Patch
{
    /// <summary>The edits of the inner canvas and of the agent as steps of the song's history.</summary>
    internal sealed class SongHistory : IPatchHistory
    {
        readonly IMachine machine;
        public SongHistory(IMachine machine) { this.machine = machine; }

        public void Record(string name, string before, string after)
        {
            var graph = machine?.Graph;
            if (graph == null) return;
            graph.Do(new PatchEditAction(machine, name, before, after));
        }
    }

    /// <summary>
    /// An edit of the inner canvas as a step of the song's history: the patch text before and after. The patch is already in place when the step is made, so the
    /// first Do does nothing; Undo and a later Do put the text back through the machine, which compiles it off the audio thread. It finds its machine when it runs.
    /// </summary>
    internal sealed class PatchEditAction : IAction, IHistoryAction
    {
        readonly IMachine machine;
        readonly IMachineGraph graph;
        readonly string machineName, name, before, after;
        bool skipDo = true;

        public PatchEditAction(IMachine machine, string name, string before, string after)
        {
            this.machine = machine;
            graph = machine.Graph;
            machineName = machine.Name;
            this.name = name;
            this.before = before;
            this.after = after;
        }

        string IHistoryAction.HistoryName => name;
        long IHistoryAction.HistoryBytes => 2L * (before.Length + after.Length);
        IEnumerable<KeyValuePair<object, long>> IHistoryAction.HistoryBlobs => Array.Empty<KeyValuePair<object, long>>();

        public void Do()
        {
            if (skipDo) { skipDo = false; return; }
            Apply(after);
        }

        public void Undo() => Apply(before);

        void Apply(string text)
        {
            var machines = graph?.Machines;
            var m = machines == null || machines.Contains(machine) ? machine : machines.FirstOrDefault(x => x.Name == machineName);
            if (m?.ManagedMachine is NeoPatchMachine target) target.ApplyPatchText(text);
        }
    }
}
