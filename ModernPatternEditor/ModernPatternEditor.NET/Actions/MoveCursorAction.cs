namespace WDE.ModernPatternEditor.Actions
{
    class MoveCursorAction : ClipVMAction
    {
        Digit oldPosition;
        Digit newPosition;

        public MoveCursorAction(ClipVM pvm, Digit p)
            : base(pvm)
        {
            // store PatternVM-independent digits
            oldPosition = pvm.CursorPosition.SetClipVM(null);
            newPosition = p.SetClipVM(null);
        }

        protected override void DoAction()
        {
            ClipVM.CursorPosition = newPosition.SetClipVM(ClipVM);
        }

        protected override void UndoAction()
        {
            ClipVM.CursorPosition = oldPosition.SetClipVM(ClipVM);
        }
    }
}
