using BuzzGUI.Interfaces;

namespace BuzzGUI.Common.Actions
{
    public struct ClipColumnRef
    {
        readonly int index;

        public IClipColumn GetColumn(IClip p) { return p.Columns[index]; }

        public ClipColumnRef(IClipColumn pc)
        {
            index = pc.Clip.Columns.IndexOf(pc);
        }
    }
}
