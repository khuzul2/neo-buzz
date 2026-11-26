using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace BuzzGUI.Common.Actions
{
    public class ClipColumnSnapshot
    {
        readonly int columnIndex;
        readonly List<ClipEvent> events;

        public ClipColumnSnapshot(IClipColumn pc, int startTime, int endTime)
        {
            columnIndex = pc.Clip.Columns.IndexOf(pc);
            events = pc.GetEvents(startTime, endTime).ToList();
            // TODO: metadata
        }

        public void CopyTo(IClipColumn p)
        {
            var pc = p.Clip.Columns[columnIndex];
            pc.SetEvents(events, true);
        }

    }
}
