// Fork (docs/specs/clips.md §5.5, §6, T2): the vertical envelope lanes beside the tracker's parameter columns. The grid asks the studio's lane
// provider (BuzzGUI.Common.ClipLaneHost) for a control for each column whose lane is open, puts it in the scrolled content right of the column's
// digits (so it scrolls with the rows), and tells it where the rows are (RowAxis) and when the rows or the clip's events change. Which lanes are
// open is saved with the song: the Lane metadata of the column's lane in the clip store (MPEClipColumn.LaneOpen).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using WDE.ModernPatternEditor.ColumnRenderer;
using Control = Avalonia.Controls.Control;

namespace WDE.ModernPatternEditor.AvaloniaView
{
    public sealed partial class ClipGridView
    {
        /// <summary>One open lane: the control the studio made, and the axis it reads its rows from.</summary>
        sealed class LaneSlot
        {
            public Control Control;
            public RowAxis Axis;
        }

        readonly Dictionary<(IClip, IParameter, int), LaneSlot> lanes = new();

        /// <summary>The lane controls now open (for tests).</summary>
        public IReadOnlyList<Control> LaneControls => lanes.Values.Select(l => l.Control).ToList();

        /// <summary>Whether a column of this name (its nth of that name) has its lane open (for tests).</summary>
        public bool IsLaneOpen(int columnSet, int column) => Layout?.Clip.ColumnSets[columnSet].Columns[column] is ParameterColumn { LaneOpen: true };

        /// <summary>The lane control beside a column, or null (for tests).</summary>
        public Control LaneOf(int columnSet, int column)
        {
            if (Layout?.Clip.ColumnSets[columnSet].Columns[column] is not ParameterColumn { LaneSupported: true } pc) return null;
            return lanes.TryGetValue((pc.Set.Clip.Clip, pc.ClipColumn.Parameter, pc.LaneTrack), out var slot) ? slot.Control : null;
        }

        /// <summary>Where a column's lane strip is, in the beats' coordinates minus the sideways scroll (for tests).</summary>
        public Rect LaneStrip(int columnSet, int column) =>
            new Rect(Layout.LaneX(columnSet, column) - Offset.X, 0, Layout.LaneWidthAt(columnSet, column), Layout.ExtentHeight);

        /// <summary>The toggle of a column's lane, in the header's coordinates (for tests).</summary>
        public Rect LaneToggleRect(int columnSet, int column) => header.ToggleRect(Layout, columnSet, column).Translate(new Vector(-Offset.X, 0));

        /// <summary>The header strip (for tests: the toggles are clicked in it).</summary>
        public Control Header => header;

        /// <summary>Whether a column has a lane toggle: its parameter can have a lane (for tests).</summary>
        public bool HasLaneToggle(int columnSet, int column) => Layout?.Clip.ColumnSets[columnSet].Columns[column] is ParameterColumn { LaneSupported: true };

        /// <summary>Whether the header has its row of toggles (for tests).</summary>
        public bool HasLaneToggles => HasToggleRow;

        /// <summary>Shows or hides a column's lane (what its toggle does): saved with the song, not a step of the history.</summary>
        internal void ToggleLane(ParameterColumn column) => SetLane(column, !column.LaneOpen);

        public void SetLane(int columnSet, int column, bool open)
        {
            if (Layout?.Clip.ColumnSets[columnSet].Columns[column] is ParameterColumn pc) SetLane(pc, open);
        }

        void SetLane(ParameterColumn column, bool open)
        {
            if (!column.LaneSupported || column.LaneOpen == open) return;
            column.SetLaneOpen(open);
            Relayout();
        }

        /// <summary>Makes the lane controls match the columns whose lanes are open: new ones are asked for, ones already there are moved, others dropped.</summary>
        void SyncLanes()
        {
            var layout = Layout;
            var wanted = new HashSet<(IClip, IParameter, int)>();
            if (layout != null)
            {
                var sets = layout.Clip.ColumnSets;
                for (int s = 0; s < sets.Count; s++)
                    for (int c = 0; c < sets[s].Columns.Count; c++)
                    {
                        if (sets[s].Columns[c] is not ParameterColumn { LaneOpen: true, LaneSupported: true } pc) continue;
                        var key = (pc.Set.Clip.Clip, pc.ClipColumn.Parameter, pc.LaneTrack);
                        wanted.Add(key);
                        if (!lanes.TryGetValue(key, out var slot))
                        {
                            var axis = new RowAxis(this, pc);
                            if (ClipLaneHost.Provider?.Create(key.Item1, key.Item2, key.Item3, axis) is not Control control) continue;
                            control.HorizontalAlignment = HorizontalAlignment.Left;
                            control.VerticalAlignment = VerticalAlignment.Top;
                            slot = new LaneSlot { Control = control, Axis = axis };
                            lanes[key] = slot;
                            // Under the cursor and the play row (the overlay), over the beats.
                            content.Children.Insert(Math.Max(0, content.Children.IndexOf(overlay)), control);
                        }
                        slot.Axis.Column = pc;
                        slot.Control.Margin = new Thickness(layout.LaneX(s, c), 0, 0, 0);
                        slot.Control.Width = layout.LaneWidthAt(s, c);
                        slot.Control.Height = layout.ExtentHeight;
                    }
            }
            foreach (var key in lanes.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                content.Children.Remove(lanes[key].Control);
                lanes.Remove(key);
            }
            foreach (var slot in lanes.Values) slot.Axis.RaiseData();
        }

        /// <summary>The rows moved, scrolled or the play position changed: the lanes draw again.</summary>
        void RaiseLaneView()
        {
            foreach (var slot in lanes.Values) slot.Axis.RaiseView();
        }

        /// <summary>Events of the clip changed (typed, undone, written by the song's history or the agent): the lanes read them again.</summary>
        void RaiseLaneData()
        {
            foreach (var slot in lanes.Values) slot.Axis.RaiseData();
        }

        /// <summary>The rows of one column, as the grid draws them: a beat is as high as the grid's beats, and holds as many rows as this column's beat holds.</summary>
        sealed class RowAxis : IClipRowAxis
        {
            readonly ClipGridView grid;
            static readonly int Beat = ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase;

            public RowAxis(ClipGridView grid, ParameterColumn column)
            {
                this.grid = grid;
                Column = column;
            }

            public ParameterColumn Column { get; set; }

            public event Action Changed;

            public event Action DataChanged;

            public void RaiseView() => Changed?.Invoke();

            public void RaiseData() => DataChanged?.Invoke();

            GridLayout Layout => grid.Layout;

            public int Length => (grid.Controller.Clip?.Clip?.Length ?? 0) * ClipEvent.TimeBase;

            public double Extent => Layout?.ExtentHeight ?? 0;

            // The beat's height for rows: the grid places row r of n at beat * BeatHeight + r * LineHeight * RPB / n (GridLayout.GetFieldRect).
            double RowsHeight => Layout == null ? 0 : Layout.LineHeight * Layout.RPB;

            public double Y(int time)
            {
                if (Layout == null) return 0;
                time = Math.Clamp(time, 0, Math.Max(Length, 0));
                int beat = time / Beat;
                return beat * Layout.BeatHeight + (time % Beat) / (double)Beat * RowsHeight;
            }

            public int TimeAt(double y)
            {
                if (Layout == null || Layout.BeatHeight <= 0) return 0;
                int beat = Math.Clamp((int)Math.Floor(y / Layout.BeatHeight), 0, Math.Max(0, Layout.BeatCount - 1));
                double within = (y - beat * Layout.BeatHeight) / RowsHeight;
                int time = beat * Beat + (int)Math.Round(Math.Clamp(within, 0, 1) * Beat);
                return Math.Clamp(time, 0, Length);
            }

            int RowsIn(int beat) => Column.RowsInBeat(beat);

            static int RowTime(int beat, int row, int rows) => beat * Beat + ClipEvent.TimeBase * ClipControl.BUZZ_TICKS_PER_BEAT * row / rows;

            public int SnapToRow(int time)
            {
                int length = Length;
                if (length <= 0) return 0;
                time = Math.Clamp(time, 0, length);
                int beat = time / Beat;
                int rows = Math.Max(1, RowsIn(beat));
                int row = (int)Math.Round((time - beat * Beat) * rows / (double)Beat);
                int snapped = row >= rows ? (beat + 1) * Beat : RowTime(beat, row, rows);
                if (snapped < length) return snapped;
                // Past the last row of the clip: the last row there is.
                int lastBeat = (length - 1) / Beat;
                int lastRows = Math.Max(1, RowsIn(lastBeat));
                for (int r = lastRows - 1; r >= 0; r--)
                    if (RowTime(lastBeat, r, lastRows) < length) return RowTime(lastBeat, r, lastRows);
                return 0;
            }

            public void RowStarts(int from, int to, List<int> into)
            {
                into.Clear();
                if (to <= from) return;
                to = Math.Min(to, Length);
                for (int beat = Math.Max(0, from / Beat); beat * Beat < to; beat++)
                {
                    int rows = Math.Max(1, RowsIn(beat));
                    for (int r = 0; r < rows; r++)
                    {
                        int t = RowTime(beat, r, rows);
                        if (t >= from && t < to) into.Add(t);
                    }
                }
            }

            public (double Top, double Bottom) View => (grid.Offset.Y, grid.Offset.Y + Math.Max(grid.Viewport.Height, 0));

            public int PlayPosition => grid.PlayPosition == int.MinValue ? -1 : grid.PlayPosition;

            public int BeatUnits => Beat;

            public int BarUnits => grid.BarTicks * ClipEvent.TimeBase;
        }
    }
}
