using BuzzGUI.Common.Actions;
using System.Linq;

namespace WDE.ModernPatternEditor.Actions
{
    abstract class ClipVMAction : MachineVMAction
    {
        string clipName;

        protected ClipVM ClipVM
        {
            get
            {
                var p = MachineVM.Clips.FirstOrDefault(x => x.Name == clipName);
                if (p == null) throw new ActionException(this);
                return p;
            }
        }

        public ClipVMAction(ClipVM pvm)
            : base(pvm.MachineVM)
        {
            clipName = pvm.Name;
        }

    }
}
