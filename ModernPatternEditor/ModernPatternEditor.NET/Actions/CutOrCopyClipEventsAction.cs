using BuzzGUI.Common.Actions;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor.Actions
{
    public class CutOrCopyClipEventsAction : ClipAction
    {
        Selection r;
        ClipClipboard oldClipboard = new ClipClipboard();
        ClipClipboard oldEvents = new ClipClipboard();
        bool cut;
        MPEClip mPEClip;

        public CutOrCopyClipEventsAction(MPEClip clip, Selection r, ClipClipboard clipboard, bool cut)
            : base(clip.Clip)
        {
            this.mPEClip = clip;
            this.r = r;

            this.cut = cut;

            // Copy clip data to shared clipboard
            clipboard.Copy(mPEClip, r);

            oldClipboard.Clone(Clip, clipboard);

            if (cut)
            {
                oldEvents.Copy(mPEClip, r);
            }
        }

        protected override void DoAction()
        {
            if (cut)
            {
                oldClipboard.Cut(mPEClip, r);
            }
        }

        protected override void UndoAction()
        {
            if (cut) oldEvents.Paste(mPEClip, r);
        }
    }
}
