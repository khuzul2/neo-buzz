// Fork (docs/specs/studio-mvp.md S6): the pattern grid's geometry, ported from the WPF editor's
// ColumnSetElement (GetFieldRect, GetDigitRect, GetDigitAtPoint) and PatternControl (column sets side
// by side, ColumnSetGap apart): where a beat, a field and a digit are, and which digit is at a point.

using Avalonia;
using WDE.ModernPatternEditor.ColumnRenderer;

namespace WDE.ModernPatternEditor.AvaloniaView
{
    // Avalonia's, not the portable editor's stand-in (PortableShims.cs).
    using Color = Avalonia.Media.Color;

    internal sealed class GridLayout
    {
        public const int ColumnSetGap = 4;

        public PatternVM Pattern { get; }
        public PatternFont Font { get; }

        /// <summary>Rows per beat the grid is drawn with (the pattern's default; other beats squeeze into it).</summary>
        public int RPB { get; }

        public double LineHeight => Font.LineHeight * PatternControl.Scale;
        public int BeatHeight => (int)Math.Ceiling(Font.LineHeight * RPB * PatternControl.Scale);
        public int BeatCount { get; }

        readonly int[][] columnWidths;          // [set][column]
        readonly double[] setX;                 // each set's left edge
        readonly double[] setWidth;

        public double ExtentWidth { get; }
        public double ExtentHeight => BeatCount * BeatHeight;
        public int SetCount => columnWidths.Length;

        public GridLayout(PatternVM pattern, PatternFont font)
        {
            Pattern = pattern;
            Font = font;
            RPB = Math.Max(1, pattern.DefaultRPB);
            BeatCount = pattern.BeatCount;
            var sets = pattern.ColumnSets ?? new List<ParameterColumnSet>();
            columnWidths = sets.Select(cs => cs.Columns.Select(ColumnWidth).ToArray()).ToArray();
            setX = new double[columnWidths.Length];
            setWidth = new double[columnWidths.Length];
            double x = 0;
            for (int i = 0; i < columnWidths.Length; i++)
            {
                setX[i] = x;
                setWidth[i] = columnWidths[i].Sum();
                x += setWidth[i] + ColumnSetGap;
            }
            ExtentWidth = Math.Max(1, x);
        }

        /// <summary>BeatVisual.GetColumnWidth: the column's digits (one less when tied to the next) plus one.</summary>
        public int ColumnWidth(IColumn column) =>
            (int)Math.Ceiling(Font.Width(column.TiedToNext ? column.DigitCount - 1 : column.DigitCount));

        public double SetX(int set) => setX[set];
        public double SetWidth(int set) => setWidth[set];
        public int ColumnWidthAt(int set, int column) => columnWidths[set][column];
        public double ColumnX(int set, int column) => setX[set] + columnWidths[set].Take(column).Sum();

        public Rect GetFieldRect(Digit d)
        {
            var cs = Pattern.ColumnSets[d.ColumnSet];
            var col = cs.Columns[d.Column];
            var beat = col.FetchBeat(d.Beat);

            double h = LineHeight * RPB / (double)beat.Rows.Count;
            double y = d.Beat * BeatHeight + d.RowInBeat * h;
            double x = ColumnX(d.ColumnSet, d.Column);
            if (col.TiedToNext) x += Font.Width(0) / 2;

            return new Rect(x, y, columnWidths[d.ColumnSet][d.Column], h);
        }

        public Rect GetDigitRect(Digit d)
        {
            var r = GetFieldRect(d);
            var col = Pattern.ColumnSets[d.ColumnSet].Columns[d.Column];
            var beat = col.FetchBeat(d.Beat);
            var text = beat.Rows[d.RowInBeat].ValueString;
            var widths = Font.AdvanceWidths(text);
            double cw = columnWidths[d.ColumnSet][d.Column];
            int index = Math.Min(d.Index, widths.Length - 1);
            double x = r.X + widths.Take(index).Sum() + cw / 2.0 - widths.Sum() / 2;
            return new Rect(x, r.Y, index >= 0 ? widths[index] : r.Width, r.Height);
        }

        /// <summary>The cursor's rectangle (PatternControl.UpdateCursor): a digit, or a note's whole field.</summary>
        public Rect GetCursorRect(Digit p)
        {
            var col = Pattern.GetColumn(p);
            if (col.Type == ColumnType.Note && p.Index == 0)
                return GetFieldRect(p).Inflate(new Thickness(-1, 0));
            return GetDigitRect(p).Inflate(new Thickness(1, 0));
        }

        /// <summary>The column set under an x (the last one past the end).</summary>
        public int SetAt(double x)
        {
            for (int i = 0; i < setX.Length; i++)
                if (x < setX[i] + setWidth[i] + ColumnSetGap) return i;
            return Math.Max(0, setX.Length - 1);
        }

        public Digit GetDigitAtPoint(Point p)
        {
            int set = SetAt(p.X);
            var cs = Pattern.ColumnSets[set];
            var cw = columnWidths[set];
            double px = p.X - setX[set];

            int newColumn = cw.Length - 1;
            double sum = 0;
            for (int i = 0; i < cw.Length; i++)
            {
                sum += cw[i];
                if (px < sum) { newColumn = i; break; }
            }
            var col = cs.Columns[newColumn];

            int newBeat, newRowInBeat;
            if (p.Y < 0)
            {
                newBeat = 0;
                newRowInBeat = 0;
            }
            else if (p.Y < ExtentHeight)
            {
                newBeat = (int)Math.Floor(p.Y / BeatHeight);
                var b = col.FetchBeat(newBeat);
                double h = LineHeight * RPB / (double)b.Rows.Count;
                newRowInBeat = Math.Min(b.Rows.Count - 1, (int)Math.Floor(p.Y % BeatHeight / h));
            }
            else
            {
                newBeat = BeatCount - 1;
                newRowInBeat = col.FetchBeat(newBeat).Rows.Count - 1;
            }

            var beat = col.FetchBeat(newBeat);
            var text = beat.Rows[newRowInBeat].ValueString;
            var widths = Font.AdvanceWidths(text);
            px -= cw.Take(newColumn).Sum();
            px -= cw[newColumn] / 2.0 - widths.Sum() / 2;
            if (col.TiedToNext) px -= Font.Width(0) / 2;
            int newIndex = col.DigitCount - 1;
            sum = 0;
            for (int i = 0; i < widths.Length; i++)
            {
                sum += widths[i];
                if (px < sum) { newIndex = i; break; }
            }
            newIndex = Math.Min(newIndex, col.DigitCount - 1);

            return new Digit(Pattern, set, newColumn, newBeat, newRowInBeat, newIndex);
        }

        /// <summary>The row (as a digit in the first column) at a y, for the row numbers.</summary>
        public Digit GetRowAtY(double y)
        {
            var d = GetDigitAtPoint(new Point(0, y));
            return Pattern.CursorPosition.SetBeat(d.Beat).SetRowInBeat(d.RowInBeat).Constrained;
        }
    }
}
