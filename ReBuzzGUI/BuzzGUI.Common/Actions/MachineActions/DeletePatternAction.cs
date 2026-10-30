using BuzzGUI.Interfaces;
using System.Linq;

namespace BuzzGUI.Common.Actions.MachineActions
{
    public class DeletePatternAction : MachineAction
    {
        string name;
        readonly int length;
        readonly PatternClip data;
        IPattern target;           // fork (0.6.2/0.6.3): the pattern deleted; after an undo, the one made again

        public DeletePatternAction(IPattern p)
            : base(p.Machine)
        {
            this.name = p.Name;
            this.length = p.Length;
            this.data = new PatternClip(p);
            this.target = p;
        }

        protected override void DoAction()
        {
            // Fork (0.6.3): the pattern itself (a redo deletes the one the undo made, under
            // whatever name it got); by name only when its machine was made again since.
            var pat = target != null && Machine.Patterns.Contains(target) ? target : Machine.Patterns.FirstOrDefault(p => p.Name == name);
            if (pat != null)
            {
                target = pat;
                name = pat.Name;
                Machine.DeletePattern(pat);
            }
        }

        protected override void UndoAction()
        {
            // Fork (0.6.3): under a free name if another pattern took it meanwhile; never a
            // second pattern of one name (pattern editors keep their data by name).
            var before = Machine.Patterns.ToList();
            Machine.CreatePattern(PatternNames.Free(Machine, name), length);
            var again = PatternNames.Added(Machine, before);
            if (again == null) return;
            data.CopyTo(again);
            Restorations.Report(target, again);
            target = again;
        }


    }
}
