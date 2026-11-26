using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace BuzzGUI.Common.Actions.ClipActions
{
    public class SetOrClearEventsAction : ClipAction
    {
        readonly ClipEvent[] newevents;
        ClipEvent[] oldevents;
        readonly bool set;
        readonly int columnIndex;

        public SetOrClearEventsAction(IClipColumn column, IEnumerable<ClipEvent> events, bool set)
            : base(column.Clip)
        {
            this.newevents = events.ToArray();
            this.set = set;
            columnIndex = column.Clip.Columns.IndexOf(column);
        }

        protected override void DoAction()
        {
            var col = Clip.Columns[columnIndex];

            if (set)
            {
                var times = newevents.Select(e => e.Time).ToHashSet();
                oldevents = col.GetEvents(times.Min(), times.Max() + 1).Where(e => times.Contains(e.Time)).ToArray();
                col.SetEvents(oldevents, false);
                col.SetEvents(newevents, true);
            }
            else
            {
                col.SetEvents(newevents, false);
            }
        }

        protected override void UndoAction()
        {
            var col = Clip.Columns[columnIndex];

            if (set)
            {
                col.SetEvents(newevents, false);
                col.SetEvents(oldevents, true);
            }
            else
            {
                col.SetEvents(newevents, true);
            }
        }

    }
}
