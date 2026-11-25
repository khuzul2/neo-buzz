using BuzzGUI.Interfaces;
using System.Linq;

namespace BuzzGUI.Common.Actions.MachineActions
{
    public class DeleteClipAction : MachineAction
    {
        string name;
        readonly int length;
        readonly ClipSnapshot data;
        IClip target;           // fork (0.6.2/0.6.3): the clip deleted; after an undo, the one made again

        public DeleteClipAction(IClip p)
            : base(p.Machine)
        {
            this.name = p.Name;
            this.length = p.Length;
            this.data = new ClipSnapshot(p);
            this.target = p;
        }

        protected override void DoAction()
        {
            // Fork (0.6.3): the clip itself (a redo deletes the one the undo made, under
            // whatever name it got); by name only when its machine was made again since.
            var pat = target != null && Machine.Clips.Contains(target) ? target : Machine.Clips.FirstOrDefault(p => p.Name == name);
            if (pat != null)
            {
                target = pat;
                name = pat.Name;
                Machine.DeleteClip(pat);
            }
        }

        protected override void UndoAction()
        {
            // Fork (0.6.3): under a free name if another clip took it meanwhile; never a
            // second clip of one name (clip editors keep their data by name).
            var before = Machine.Clips.ToList();
            Machine.CreateClip(ClipNames.Free(Machine, name), length);
            var again = ClipNames.Added(Machine, before);
            if (again == null) return;
            data.CopyTo(again);
            Restorations.Report(target, again);
            target = again;
        }


    }
}
