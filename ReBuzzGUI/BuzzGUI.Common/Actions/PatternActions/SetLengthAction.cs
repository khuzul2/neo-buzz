using BuzzGUI.Interfaces;

namespace BuzzGUI.Common.Actions.ClipActions
{
    public class SetLengthAction : ClipAction
    {
        readonly int newLength;
        readonly int oldLength;

        public SetLengthAction(IClip clip, int length)
            : base(clip)
        {
            this.newLength = length;
            this.oldLength = clip.Length;
        }

        protected override void DoAction()
        {
            Clip.Length = newLength;
        }

        protected override void UndoAction()
        {
            Clip.Length = oldLength;
        }

    }
}
