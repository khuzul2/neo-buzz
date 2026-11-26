using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Interfaces;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor.Actions
{
    public class MPEUpDownAction : ClipAction
    {
        MPEClip mpeClip;
        private Selection selection;
        private int delta;

        public MPEUpDownAction(MPEClip mpeClip, Selection selection, int delta)
            : base(mpeClip.Clip)
        {
            this.mpeClip = mpeClip;
            this.selection = selection;
            this.delta = delta;
        }

        protected override void DoAction()
        {
            ShiftValue(delta);
        }

        private void ShiftValue(int shiftdelta)
        {
            var columniterator = selection.Bounds.Item1;
            var endDigit = selection.Bounds.Item2;

            while (true)
            {
                var digitIterator = columniterator;

                // Get last Beat & Row
                var columnEndDigit = digitIterator.SetBeat(endDigit.Beat);
                columnEndDigit = columnEndDigit.NearestRow(endDigit.TimeInBeat);

                while (true)
                {
                    int rowInBeat = digitIterator.RowInBeat;
                    var beat = digitIterator.ParameterColumn.FetchBeat(digitIterator.Beat);
                    var newValue = beat.Rows[rowInBeat].Type == ColumnRenderer.BeatValueType.NoValue ? -1 : beat.Rows[rowInBeat].Value;

                    if (newValue >= 0)
                    {
                        if (digitIterator.ParameterColumn.ClipColumn.Parameter.Type == ParameterType.Note)
                        {
                            if (newValue != BuzzNote.Off && newValue + shiftdelta > BuzzNote.Min && newValue + shiftdelta < BuzzNote.Max)
                            {
                                newValue = BuzzNote.FromMIDINote(BuzzNote.ToMIDINote(newValue) + shiftdelta);
                            }
                        }
                        else
                        {
                            newValue += shiftdelta;
                        }

                        foreach (var act in digitIterator.ParameterColumn.EditValue(digitIterator, newValue))
                            act.Do();
                    }

                    // Beats can have different number of rows, so use closest time
                    if (digitIterator.Beat * ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase + digitIterator.TimeInBeat >=
                        columnEndDigit.Beat * ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase + columnEndDigit.TimeInBeat)
                        break;

                    digitIterator = digitIterator.Down;
                }

                if (columniterator.ColumnSet >= endDigit.ColumnSet && columniterator.Column >= endDigit.Column)
                    break;

                columniterator = columniterator.RightColumn;
            }
        }

        protected override void UndoAction()
        {
            ShiftValue(-delta);
        }
    }
}
