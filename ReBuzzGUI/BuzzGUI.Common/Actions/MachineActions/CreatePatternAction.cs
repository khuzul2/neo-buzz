using BuzzGUI.Interfaces;
using System.Linq;

namespace BuzzGUI.Common.Actions.MachineActions
{
    public class CreatePatternAction : MachineAction
    {
        readonly string name;
        readonly string cloneName;
        readonly int length;
        IPattern created;          // fork (0.6.2): the pattern made; a redo reports the new one standing for it

        public CreatePatternAction(IMachine machine, string name, int length, IPattern clone = null)
            : base(machine)
        {
            this.name = name;
            this.length = length;

            if (clone != null)
                cloneName = clone.Name;

        }

        protected override void DoAction()
        {
            if (cloneName != null)
            {
                var clone = Machine.Patterns.FirstOrDefault(p => p.Name == cloneName);
                if (clone != null) Machine.ClonePattern(name, clone);
            }
            else
            {
                Machine.CreatePattern(name, length);
            }
            var made = Machine.Patterns.FirstOrDefault(p => p.Name == name);
            if (created != null && made != null)
                Restorations.Report(created, made);
            if (made != null) created = made;
        }

        protected override void UndoAction()
        {
            // Fork (0.6.2): the pattern this action made, not any pattern that has its name now.
            var pat = created != null && Machine.Patterns.Contains(created) ? created : Machine.Patterns.FirstOrDefault(p => p.Name == name);
            if (pat != null) Machine.DeletePattern(pat);
        }


    }
}
