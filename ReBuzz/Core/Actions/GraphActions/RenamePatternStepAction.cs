// Fork (docs/specs/history-snapshots.md HS1): a pattern rename from an editor as a step of the song's history.

using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System.Linq;

namespace ReBuzz.Core.Actions.GraphActions
{
    /// <summary>
    /// A pattern's rename, already made when it is recorded. It finds the pattern when it runs (the same object while it
    /// is on the machine, else the one of the name it had), as BuzzGUI's pattern actions do.
    /// </summary>
    [HistoryName("Rename pattern")]
    internal sealed class RenamePatternStepAction : IAction
    {
        readonly MachineCore machine;
        readonly IPattern pattern;
        readonly string oldName;
        readonly string newName;

        public RenamePatternStepAction(MachineCore machine, IPattern pattern, string oldName, string newName)
        {
            this.machine = machine;
            this.pattern = pattern;
            this.oldName = oldName;
            this.newName = newName;
        }

        public void Do() => Rename(newName, oldName);

        public void Undo() => Rename(oldName, newName);

        void Rename(string to, string from)
        {
            var p = machine.Patterns.Contains(pattern) ? pattern : machine.Patterns.FirstOrDefault(x => x.Name == from);
            if (p == null || p.Name == to || machine.Patterns.Any(x => !ReferenceEquals(x, p) && x.Name == to)) return;
            machine.SetPatternName(p, to);
        }
    }
}
