using BuzzGUI.Interfaces;

namespace BuzzGUI.Common.Actions
{
    public struct ClipColumnBeatRef
    {
        readonly ClipColumnRef column;
        readonly int beat;

        public int Beat { get { return beat; } }
        public IClipColumn GetColumn(IClip p) { return column.GetColumn(p); }

        public ClipColumnBeatRef(ClipColumnRef pcr, int beat)
        {
            this.column = pcr;
            this.beat = beat;
        }
    }
}
