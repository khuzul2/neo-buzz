using BuzzGUI.Interfaces;
using System.Linq;

namespace BuzzGUI.Common.Actions.MachineActions
{
    public class CreateClipAction : MachineAction
    {
        readonly string name;
        readonly string cloneName;
        readonly int length;
        IClip created;          // fork (0.6.2): the clip made; a redo reports the new one standing for it

        public CreateClipAction(IMachine machine, string name, int length, IClip clone = null)
            : base(machine)
        {
            this.name = name;
            this.length = length;

            if (clone != null)
                cloneName = clone.Name;

        }

        protected override void DoAction()
        {
            // Fork (0.6.3): a redo makes it under a free name if its own was taken meanwhile, and
            // holds the clip it made (not an older one that has the name).
            var before = Machine.Clips.ToList();
            string free = ClipNames.Free(Machine, name);
            if (cloneName != null)
            {
                var clone = Machine.Clips.FirstOrDefault(p => p.Name == cloneName);
                if (clone != null) Machine.CloneClip(free, clone);
            }
            else
            {
                Machine.CreateClip(free, length);
            }
            var made = ClipNames.Added(Machine, before);
            if (created != null && made != null)
                Restorations.Report(created, made);
            if (made != null) created = made;
        }

        protected override void UndoAction()
        {
            // Fork (0.6.2): the clip this action made, not any clip that has its name now.
            var pat = created != null && Machine.Clips.Contains(created) ? created : Machine.Clips.FirstOrDefault(p => p.Name == name);
            if (pat != null) Machine.DeleteClip(pat);
        }


    }
}
