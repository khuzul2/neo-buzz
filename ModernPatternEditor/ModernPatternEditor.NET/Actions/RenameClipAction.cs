using BuzzGUI.Common.Actions;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor.Actions
{
    public class RenameClipAction : ClipAction
    {
        MPEClip mPEClip;
        IGUICallbacks cb;
        string machineName;
        string oldName;
        string newName;

        public RenameClipAction(MPEClip clip, IGUICallbacks cb, string name)
            : base(clip.Clip)
        {
            this.mPEClip = clip;
            this.cb = cb;
            this.oldName = Clip.Name;
            this.newName = name;
            machineName = Clip.Machine.Name;
        }

        protected override void DoAction()
        {
            if (cb != null)
                cb.SetClipName(machineName, oldName, newName);
        }

        protected override void UndoAction()
        {
            if (cb != null)
                cb.SetClipName(machineName, newName, oldName);
        }
    }
}
