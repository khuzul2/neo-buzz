using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System.Windows.Input;

namespace WDE.ModernPatternEditor
{
    public class EditContext : IEditContext
    {
        ClipEditor editor;
        ManagedActionStack actionStack = new ManagedActionStack();
        public ManagedActionStack ManagedActionStack { get { return actionStack; } }

        public EditContext(ClipEditor e)
        {
            editor = e;
        }

        public IActionStack ActionStack { get { return actionStack; } }
        public ICommand CutCommand { get { return editor.clipControl.CutCommand; } }
        public ICommand CopyCommand { get { return editor.clipControl.CopyCommand; } }
        public ICommand PasteCommand { get { return editor.clipControl.PasteCommand; } }

    }
}
