#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WDE.ModernPatternEditor.ColumnRenderer
{
    /// <summary>
    /// Fork (docs/specs/dye-essence.md §5.5): a row of the column set header, a cell per column (the
    /// column labels, the parameter knobs), laid out as the horizontal StackPanel it replaces (each cell
    /// as wide as its column, the row as wide as the set), but with only the cells of the columns in view
    /// and half a view either side made. A machine's globals are one set: with hundreds of parameters the
    /// header made, laid out and drew a label and a knob for every one of them, and each knob followed its
    /// parameter. A cell is made when its column comes near the view and let go when it leaves.
    /// </summary>
    sealed class ColumnStrip : Panel
    {
        readonly int[] widths;
        readonly int[] lefts;       // lefts[c] = x of column c; lefts[count] = the row's width
        readonly double height;
        readonly Func<int, UIElement?> create;
        readonly Action<UIElement>? release;
        readonly Func<Tuple<double, double>> view;
        readonly Dictionary<int, UIElement> cells = new Dictionary<int, UIElement>();
        int first, end;             // the columns whose cells are made: [first, end)

        /// <param name="widths">Each column's width.</param>
        /// <param name="height">The row's height (each cell's).</param>
        /// <param name="create">Makes column c's cell.</param>
        /// <param name="release">Lets a cell go (null: nothing to do).</param>
        /// <param name="view">The part of the row in view: its first and last x, in the row's coordinates.</param>
        public ColumnStrip(int[] widths, double height, Func<int, UIElement?> create, Action<UIElement>? release, Func<Tuple<double, double>> view)
        {
            this.widths = widths;
            this.height = height;
            this.create = create;
            this.release = release;
            this.view = view;
            lefts = new int[widths.Length + 1];
            for (int c = 0; c < widths.Length; c++) lefts[c + 1] = lefts[c] + widths[c];
        }

        /// <summary>The columns whose cells are made.</summary>
        public int CellCount => cells.Count;

        /// <summary>Column c's cell, or null when it isn't made.</summary>
        public UIElement? CellOf(int c) => cells.TryGetValue(c, out var cell) ? cell : null;

        /// <summary>The column at x, held to the row's columns.</summary>
        int ColumnAt(double x)
        {
            int lo = 0, hi = widths.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (lefts[mid] <= x) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Makes the cells of the columns in or near the view, and lets the others go.</summary>
        void Realize()
        {
            if (widths.Length == 0) return;

            var v = view();
            double from = v.Item1, to = v.Item2;
            double w = to - from;
            if (!(w > 0))
            {
                // Before the first layout of the scroll viewer: as wide as the screen.
                w = SystemParameters.PrimaryScreenWidth;
                to = from + w;
            }

            int v0 = ColumnAt(from), v1 = ColumnAt(to) + 1;
            if (end > first && v0 >= first && v1 <= end) return;

            double margin = w / 2;
            int newFirst = ColumnAt(from - margin), newEnd = ColumnAt(to + margin) + 1;

            foreach (var c in cells.Keys.Where(c => c < newFirst || c >= newEnd).ToList())
            {
                var cell = cells[c];
                cells.Remove(c);
                InternalChildren.Remove(cell);
                release?.Invoke(cell);
            }

            for (int c = newFirst; c < newEnd; c++)
            {
                if (cells.ContainsKey(c)) continue;
                var cell = create(c);
                if (cell == null) continue;
                cells[c] = cell;
                InternalChildren.Add(cell);
            }

            first = newFirst;
            end = newEnd;
        }

        /// <summary>Lets every cell go (the header is made again or the editor closes).</summary>
        public void ReleaseCells()
        {
            foreach (var cell in cells.Values.ToList())
            {
                InternalChildren.Remove(cell);
                release?.Invoke(cell);
            }
            cells.Clear();
            first = end = 0;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            Realize();
            foreach (var kv in cells)
                kv.Value.Measure(new Size(widths[kv.Key], height));
            return new Size(lefts[widths.Length], widths.Length > 0 ? height : 0);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (var kv in cells)
                kv.Value.Arrange(new Rect(lefts[kv.Key], 0, widths[kv.Key], height));
            return finalSize;
        }
    }
}
#endif
