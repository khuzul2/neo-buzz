// Fork (docs/specs/history-snapshots.md HS1): a clip rename from an editor as a step of the song's history.

using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System.Linq;

namespace ReBuzz.Core.Actions.GraphActions
{
    /// <summary>
    /// A clip's rename, already made when it is recorded. It finds the clip when it runs (the same object while it
    /// is on the machine, else the one of the name it had), as BuzzGUI's clip actions do.
    /// </summary>
    [HistoryName("Rename clip")]
    internal sealed class RenameClipStepAction : IAction
    {
        readonly MachineCore machine;
        readonly IClip clip;
        readonly string oldName;
        readonly string newName;

        public RenameClipStepAction(MachineCore machine, IClip clip, string oldName, string newName)
        {
            this.machine = machine;
            this.clip = clip;
            this.oldName = oldName;
            this.newName = newName;
        }

        public void Do() => Rename(newName, oldName);

        public void Undo() => Rename(oldName, newName);

        void Rename(string to, string from)
        {
            var p = machine.Clips.Contains(clip) ? clip : machine.Clips.FirstOrDefault(x => x.Name == from);
            if (p == null || p.Name == to || machine.Clips.Any(x => !ReferenceEquals(x, p) && x.Name == to)) return;
            machine.SetClipName(p, to);
        }
    }
}
