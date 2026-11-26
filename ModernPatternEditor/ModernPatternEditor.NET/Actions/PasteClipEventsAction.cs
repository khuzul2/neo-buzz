using BuzzGUI.Common.Actions;
using BuzzGUI.Interfaces;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor.Actions
{
    public class PasteClipEventsAction : ClipAction
    {
        Selection r;
        ClipClipboard oldClipboard;
        ClipClipboard oldEvents = new ClipClipboard();
        MPEClip mPEClip;

        public PasteClipEventsAction(MPEClip clip, Selection r, ClipClipboard clipboard)
            : base(clip.Clip)
        {
            this.mPEClip = clip;
            this.r = r;
            this.oldClipboard = new ClipClipboard(Clip, clipboard);
        }

        protected override void DoAction()
        {
            if (!oldClipboard.ContainsData) return;

            Selection copyRange = CreatePasteSelection(); // Create copy range bease on clipboard rect

            oldEvents.Copy(mPEClip, copyRange); // Save old events from 
            oldClipboard.Paste(mPEClip, copyRange);
        }

        protected override void UndoAction()
        {
            Selection copyRange = CreatePasteSelection(); // Create copy range bease on clipboard rect
            oldEvents.Paste(mPEClip, copyRange);
        }

        private Selection CreatePasteSelection()
        {
            //int rpb = r.Bounds.Item1.ClipVM.DefaultRPB;
            Digit rangeStart = r.Bounds.Item1.Offset(0, 0);
            Digit rangeEnd = rangeStart;//.SetColumn(rangeStart.Column + clipboard.NumColumns);
            for (int i = 0; i < oldClipboard.NumColumns; i++)
            {
                rangeEnd = rangeEnd.RightColumn;
            }

            int startTime = rangeStart.ParameterColumn.GetDigitTime(rangeStart);// rangeStart.Beat * (rpb * ClipEvent.TimeBase) + rangeStart.TimeInBeat;
            int endTime = startTime + oldClipboard.RegionLenght;
            int targetBeat = (endTime - 1) / (ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase);
            int timeInBeat = (endTime - 1) % (ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase);
            rangeEnd = rangeEnd.SetBeat(targetBeat);
            rangeEnd = rangeEnd.NearestRow(timeInBeat);
            if (endTime > Clip.Length * ClipEvent.TimeBase)
                rangeEnd = rangeEnd.LastRowInBeat;

            return Selection.Start(rangeStart).SetEnd(rangeEnd);
        }
    }
}
