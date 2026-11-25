using BuzzGUI.Common.Actions;
using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor.Actions
{
    public class MPESetOrClearEventsAction : ClipAction
    {
        ClipEvent[] newevents;
        ClipEvent[] oldevents;
        bool set;
        int columnIndex;

        MPEClip mpeClip;

        public MPESetOrClearEventsAction(IClip Clip, MPEClipColumn mpeClipColumn, IEnumerable<ClipEvent> events, bool set)
            : base(Clip)
        {
            this.newevents = events.ToArray();
            this.set = set;

            this.mpeClip = mpeClipColumn.MPEClip;
            columnIndex = mpeClip.MPEClipColumns.IndexOf(mpeClipColumn);
        }

        protected override void DoAction()
        {
            var mpeColumn = mpeClip.MPEClipColumns[columnIndex];

            if (set)
            {
                var times = newevents.Select(e => e.Time).ToHashSet();
                oldevents = mpeColumn.GetEvents(times.Min(), times.Max() + 1).Where(e => times.Contains(e.Time)).ToArray();
                mpeColumn.SetEvents(oldevents, false);
                mpeColumn.SetEvents(newevents, true);
            }
            else
            {
                mpeColumn.SetEvents(newevents, false);
            }


            var clip = this.mpeClip.Clip;
            if (clip != null)
            {
                mpeClip.Editor.ClipChanged(clip);
            }

            mpeClip.Editor.SetModifiedFlag();
        }

        protected override void UndoAction()
        {
            //var col = Pattern.Columns[columnIndex];
            var mpeColumn = mpeClip.MPEClipColumns[columnIndex];

            if (set)
            {
                mpeColumn.SetEvents(newevents, false);
                mpeColumn.SetEvents(oldevents, true);
            }
            else
            {
                mpeColumn.SetEvents(newevents, true);
            }

            mpeClip.Editor.SetModifiedFlag();
        }
    }
}
