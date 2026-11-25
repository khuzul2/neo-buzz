using BuzzGUI.Common.Actions;
using BuzzGUI.Interfaces;
using System.Collections.Generic;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor.Actions
{
    public class MPEQuantizeBeatsAction : ClipAction
    {
        Dictionary<int, List<ClipEvent>> oldData = new Dictionary<int, List<ClipEvent>>();
        MPEClip mpeClip;
        Selection selection;

        public MPEQuantizeBeatsAction(MPEClip clip, Selection selection)
            : base(clip.Clip)
        {
            this.selection = selection;
            this.mpeClip = clip;
        }

        protected override void DoAction()
        {
            Digit columnIterator = selection.Bounds.Item1;
            Digit columnEndDigit = selection.Bounds.Item2;

            while (true)
            {
                var buzzCol = columnIterator.ParameterColumn.ClipColumn;
                var mpeCol = mpeClip.GetColumn(buzzCol);
                int beatLength = ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase;
                int start = beatLength * columnIterator.Beat;
                int end = start + beatLength * (columnEndDigit.Beat - columnIterator.Beat + 1);
                List<ClipEvent> oldEvents = new List<ClipEvent>();
                foreach (var e in mpeCol.GetEvents(start, end))
                    oldEvents.Add(new ClipEvent(e.Time, e.Value, e.Duration));
                // Save
                oldData[columnIterator.Column] = oldEvents;

                // Snap to row
                mpeCol.Quantize(columnIterator, columnEndDigit.SetColumn(columnIterator.Column));
                columnIterator = columnIterator.SetColumn(columnIterator.Column + 1);
                if (columnIterator.Column > columnEndDigit.Column)
                    break;
            }
        }

        protected override void UndoAction()
        {
            Digit columnIterator = selection.Bounds.Item1;
            Digit columnEndDigit = selection.Bounds.Item2;

            while (true)
            {
                var buzzCol = columnIterator.ParameterColumn.ClipColumn;
                var mpeCol = mpeClip.GetColumn(buzzCol);
                int beatLength = ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase;
                int start = beatLength * columnIterator.Beat;
                int end = start + beatLength * (columnEndDigit.Beat - columnIterator.Beat + 1);
                List<ClipEvent> oldEvents = oldData[columnIterator.Column];
                mpeCol.ClearRegion(end - start, start, columnIterator);
                // Save
                mpeCol.SetEvents(oldEvents.ToArray(), true);

                columnIterator = columnIterator.SetColumn(columnIterator.Column + 1);
                if (columnIterator.Column > columnEndDigit.Column)
                    break;
            }
        }
    }
}
