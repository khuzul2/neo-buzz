using BuzzGUI.Interfaces;
using System.Linq;

namespace BuzzGUI.Common.Actions.MachineActions
{
    public class DeletePatternAction : MachineAction
    {
        readonly string name;
        readonly int length;
        readonly PatternClip data;
        IPattern deleted;          // fork (0.6.2): reported when the undo makes it again

        public DeletePatternAction(IPattern p)
            : base(p.Machine)
        {
            this.name = p.Name;
            this.length = p.Length;
            this.data = new PatternClip(p);
        }

        protected override void DoAction()
        {
            var pat = Machine.Patterns.FirstOrDefault(p => p.Name == name);
            if (pat != null)
            {
                deleted = pat;
                Machine.DeletePattern(pat);
            }
        }

        protected override void UndoAction()
        {
            Machine.CreatePattern(name, length);
            var again = Machine.Patterns.First(p => p.Name == name);
            data.CopyTo(again);
            Restorations.Report(deleted, again);
        }


    }
}
