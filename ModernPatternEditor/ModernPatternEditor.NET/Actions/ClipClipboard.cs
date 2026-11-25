using BuzzGUI.Interfaces;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor.Actions
{
    public class ClipClipboard
    {
        // Column <set, index>, Beat related data; <beat, rows>
        Dictionary<int, OrderedDictionary<int, int>> columnBeatData;
        // Column <set, index>, events
        Dictionary<int, List<ClipEvent>> eventData;
        private int regionLenght;
        private int numColumns;

        public bool ContainsData { get { return columnBeatData != null; } }

        public int RegionLenght { get => regionLenght; }
        public int NumColumns { get => numColumns; }

        public ClipClipboard()
        {
        }

        public ClipClipboard(IClip clip, ClipClipboard c)
        {
            Clone(clip, c);
        }

        public void Clone(IClip clip, ClipClipboard c)
        {
            if (c.columnBeatData != null)
            {
                columnBeatData = new Dictionary<int, OrderedDictionary<int, int>>();
                foreach (var key in c.columnBeatData.Keys)
                {
                    OrderedDictionary<int, int> dict = new OrderedDictionary<int, int>();
                    foreach (var e in c.columnBeatData[key])
                        dict[e.Key] = e.Value;
                    columnBeatData[key] = dict;
                }
            }
            if (c.eventData != null)
            {
                eventData = new Dictionary<int, List<ClipEvent>>();
                foreach (var key in c.eventData.Keys)
                {
                    List<ClipEvent> edata = new List<ClipEvent>();
                    foreach (var e in c.eventData[key])
                        edata.Add(e);
                    eventData[key] = edata;
                }

            }
            regionLenght = c.RegionLenght;
            numColumns = c.NumColumns;

        }

        public void Clear()
        {
            columnBeatData = null;
            eventData = null;
        }

        public void Cut(MPEClip clip, Selection r)
        {
            CutOrCopy(clip, r, true);
        }

        public void Copy(MPEClip clip, Selection r)
        {
            CutOrCopy(clip, r, false);
        }

        public void Paste(MPEClip clip, Selection r)
        {
            if (!ContainsData) return;

            Digit topLeftDigit = r.Bounds.Item1;
            int topTime = topLeftDigit.ParameterColumn.GetDigitTime(topLeftDigit);
            Digit columnIterator = topLeftDigit;

            foreach (int columnNumber in eventData.Keys)
            {
                var clipColumn = columnIterator.ParameterColumn.ClipColumn;
                var column = clip.GetColumn(clipColumn);
                var events = eventData[columnNumber];
                column.ClearRegion(RegionLenght, topTime, columnIterator);
                column.SetEvents(events.ToArray(), topTime, true);
                var beatData = columnBeatData[columnNumber];
                Digit beatIterator = new Digit(columnIterator.ClipVM, columnIterator.ColumnSet, columnIterator.Column, columnIterator.Beat, columnIterator.RowInBeat, columnIterator.Index);

                foreach (int beatRows in columnBeatData[columnNumber].Values)
                {
                    // Set rows in beat
                    clipColumn.SetBeatSubdivision(beatIterator.Beat, beatRows);
                    if (beatIterator.IsLastBeat)
                        break;
                    beatIterator = beatIterator.NextBeat;
                }

                var nextColumn = columnIterator.RightColumn;
                if (columnIterator.ColumnSet == nextColumn.ColumnSet && columnIterator.Column == nextColumn.Column)
                    break;
                columnIterator = nextColumn;
            }
        }

        void CutOrCopy(MPEClip clip, Selection r, bool cut)
        {
            var bounds = r.Bounds;
            Digit topLeftDigit = bounds.Item1;
            Digit bottomRightDigit = bounds.Item2;
            int start = topLeftDigit.ParameterColumn.GetDigitTime(topLeftDigit);
            int end = bottomRightDigit.ParameterColumn.GetDigitTime(bottomRightDigit.Down);
            if (bottomRightDigit.IsLastRowInBeat && bottomRightDigit.IsLastBeat)
                end = clip.Clip.Length * ClipEvent.TimeBase;
            regionLenght = end - start;
            numColumns = 1;
            Digit columnIterator = topLeftDigit;

            int firstBeat = topLeftDigit.Beat;
            int columnNumber = clip.GetParamIndex(columnIterator.ParameterColumn.ClipColumn.Parameter, columnIterator.ParameterColumn.ClipColumn.Track);

            eventData = new Dictionary<int, List<ClipEvent>>();
            columnBeatData = new Dictionary<int, OrderedDictionary<int, int>>();

            while (true)
            {
                // Save event data
                var column = clip.GetColumn(columnNumber);
                var events = column.GetEvents(start, end);
                List<ClipEvent> colEvents = new List<ClipEvent>();

                foreach (var e in events)
                {
                    ClipEvent clipEvent = new ClipEvent(e.Time - start, e.Value, e.Duration);
                    // Save to List
                    colEvents.Add(clipEvent);
                }
                eventData.Add(columnNumber, colEvents);

                if (cut)
                {
                    column.SetEvents(events.ToArray(), true);
                    column.ClearRegion(RegionLenght, start, topLeftDigit);
                }

                Digit beatIterator = new Digit(columnIterator.ClipVM, columnIterator.ColumnSet, columnIterator.Column, columnIterator.Beat, columnIterator.RowInBeat, columnIterator.Index);
                // Save beat data
                OrderedDictionary<int, int> beatData = new OrderedDictionary<int, int>();
                while (true)
                {
                    beatData[beatIterator.Beat - firstBeat] = columnIterator.ClipVM.GetBeat(beatIterator).Rows.Count;

                    if (beatIterator.Beat == bottomRightDigit.Beat)
                        break;
                    beatIterator = beatIterator.NextBeat;
                }
                columnBeatData.Add(columnNumber, beatData);

                if (columnIterator.Column == bottomRightDigit.Column && columnIterator.ColumnSet == bottomRightDigit.ColumnSet)
                    break;
                columnIterator = columnIterator.RightColumn;
                columnNumber++;
                numColumns++;
            }
        }
    }
}
