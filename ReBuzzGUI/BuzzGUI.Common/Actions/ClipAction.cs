using BuzzGUI.Interfaces;
using System.Linq;

namespace BuzzGUI.Common.Actions
{
    public abstract class ClipAction : MachineAction
    {
        readonly string clipName;

        protected IClip Clip
        {
            get
            {
                var p = Machine.Clips.FirstOrDefault(x => x.Name == clipName);
                if (p == null) throw new ActionException(this);
                return p;
            }
        }

        public ClipAction(IClip p)
            : base(p.Machine)
        {
            clipName = p.Name;
        }


    }
}
