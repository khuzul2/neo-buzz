using BuzzGUI.Common.Actions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using WDE.ModernPatternEditor.MPEStructures;
using System.Linq;

namespace WDE.ModernPatternEditor.Actions
{
    public class ImportClipAction : BuzzAction
    {
        XMLClip xClip;
        ClipEditor editor;
        IMachine machine;
        string name;

        public ImportClipAction(ClipEditor editor, XMLClip xClip)
        {
            this.xClip = xClip;
            this.editor = editor;
            machine = editor.SelectedMachine.Machine;
        }

        protected override void DoAction()
        {
            editor.MPEClipsDB.ClipImported(xClip);
            if(name == null)
                name = editor.SelectedMachine.Machine.GetNewClipName();
            machine.CreateClip(name, xClip.LenghtInBeats * ClipControl.BUZZ_TICKS_PER_BEAT);
        }

        protected override void UndoAction()
        {
            IClip clip = machine.Clips.FirstOrDefault(x => x.Name == name);
            if (clip != null)
                machine.DeleteClip(clip);
        }
    }
}
