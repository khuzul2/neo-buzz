// Fork (docs/specs/daw-modes.md AR4, §3.4, §4.3): the arrangement's parameter lanes. A lane shows what writes
// one state parameter along the timeline, stitched from the clips: automation clips (patterns on the machine's
// automation lanes), the engine automation columns of the clips on its other lanes (each repeat of a looped
// clip shows), and the tracker's parameter columns as steps. Curves are points with ramps (a straight line to
// the next point) or steps. Overlaps, where two writers set the parameter in the same place, are marked.
//
// Mouse: a click on a clip's curve adds a point (a drag places it); a drag on a point moves it; a double click
// deletes it; R makes the line after the selected point a ramp or a step; Delete deletes it. A drag on an empty
// place draws an automation clip from one beat to another, with a ramp from where it started to where it ended;
// a click there draws one bar with a point. Shift turns the beat grid off. Every edit is a step of the song's
// history (ReBuzz.Core.AutomationEditor).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Views.Sequence
{
    internal sealed partial class ArrangementCanvas
    {
        const double PointRadius = 4, PointHit = 7, RowPadding = 6;
        const int Unit = PatternEvent.TimeBase;

        readonly Dictionary<ArrangementRow, AutoLane> lanes = new();
        AutomationEditor autoEditor;

        enum PDrag { None, Point, Draw }
        PDrag pdrag;
        ArrangementRow pdragRow;
        AutoSegment pdragSeg;
        int pdragIndex = -1, pdragStartTime, pdragTime, pdragValue;
        bool pdragNew;
        Point pdragPress;
        AutoSegment selSeg;
        int selIndex = -1;
        // The selected point by what it is, not by where the last reading put it: an edit takes events out and puts them in
        // (two readings), and the selection finds its point again in the second.
        (IPattern Pattern, ISequence Sequence, int EventTime, int Time)? selId;

        void SelectPoint(AutoSegment seg, int index)
        {
            selSeg = seg;
            selIndex = index;
            selId = seg == null || index < 0 || index >= seg.Points.Count ? null : (seg.Pattern, seg.Sequence, seg.EventTime, seg.Points[index].Time);
        }

        void ResolveSelection(IEnumerable<AutoSegment> segments)
        {
            selSeg = null;
            selIndex = -1;
            if (selId is not { } id) return;
            selSeg = segments.FirstOrDefault(s => ReferenceEquals(s.Pattern, id.Pattern) && ReferenceEquals(s.Sequence, id.Sequence) && s.EventTime == id.EventTime);
            selIndex = selSeg == null ? -1 : selSeg.Points.ToList().FindIndex(p => p.Time == id.Time);
            if (selIndex < 0) selSeg = null;
        }

        public AutomationEditor Automation => autoEditor ??= new AutomationEditor(core, editor);

        /// <summary>What writes a parameter lane's parameter, as the last reading found it (null for a row that isn't a parameter lane).</summary>
        public AutoLane LaneOf(ArrangementRow row) => row != null && lanes.TryGetValue(row, out var lane) ? lane : null;

        public ArrangementRow ParameterRow(string machine, ColumnKey key) =>
            rows.FirstOrDefault(r => r.Kind == ArrangementRowKind.Parameter && r.Machine.Name == machine && r.Param.Key == key);

        /// <summary>The selected point of a parameter lane (null if none).</summary>
        public (AutoSegment Segment, int Index)? SelectedPoint => selSeg != null && selIndex >= 0 ? (selSeg, selIndex) : null;

        /// <summary>The rectangle of a lane's value area, in the canvas's pixels.</summary>
        public Rect ParameterArea(ArrangementRow row) => new Rect(SequenceGeometry.HeaderWidth, YOf(row) + RowPadding, Math.Max(0, Bounds.Width - SequenceGeometry.HeaderWidth), Math.Max(1, row.Height - 2 * RowPadding));

        double XOfUnits(int units) => geo.XOf(0) + units * geo.TickWidth / Unit;

        int UnitsAt(double x) => (int)Math.Max(0, Math.Floor((x - geo.XOf(0)) * Unit / geo.TickWidth));

        double YOfValue(ArrangementRow row, int value) { var a = ParameterArea(row); return a.Y + (1 - row.Param.Fraction(value)) * a.Height; }

        int ValueAtY(ArrangementRow row, double y) { var a = ParameterArea(row); return row.Param.ValueAt(1 - (y - a.Y) / a.Height); }

        /// <summary>The point's place in the canvas.</summary>
        public Point PointPosition(ArrangementRow row, AutoSegment seg, int index) => new Point(XOfUnits(seg.ToSong(seg.Points[index].Time)), YOfValue(row, seg.Points[index].Value));

        /// <summary>Reads the lanes of the parameter rows again (the clips changed).</summary>
        void ReadLanes()
        {
            lanes.Clear();
            var paramRows = rows.Where(r => r.Kind == ArrangementRowKind.Parameter).ToList();
            if (paramRows.Count == 0) return;
            // The patterns of the clips are watched whether or not they hold a curve yet: a curve put in later (a redo) redraws the lane.
            foreach (var item in items.Values.SelectMany(l => l))
            {
                Watch(item.Pattern);
                foreach (var column in item.Pattern.Columns) WatchColumn(column);
            }
            var read = AutomationReader.ReadAll(core, paramRows.Select(r => (r.Machine, r.Param.Key)));
            for (int i = 0; i < paramRows.Count; i++)
            {
                lanes[paramRows[i]] = read[i];
                foreach (var seg in read[i].Segments) Watch(seg.Pattern);
            }
            ResolveSelection(read.SelectMany(l => l.Segments));
        }

        readonly HashSet<PatternColumnCore> watchedColumns = new();

        /// <summary>A parameter column's events are listened to while the view shows its pattern: a point put in or taken out (by an undo, the recorder, the agent) redraws the lanes.</summary>
        void WatchColumn(IPatternColumn column)
        {
            if (column is PatternColumnCore { Type: PatternColumnType.Parameter } core && watchedColumns.Add(core)) core.EventsChanged += OnColumnEvents;
        }

        void UnwatchColumns()
        {
            foreach (var c in watchedColumns) c.EventsChanged -= OnColumnEvents;
            watchedColumns.Clear();
        }

        void OnColumnEvents(IEnumerable<PatternEvent> events, bool set) => OnUi(() =>
        {
            RefreshLanes();
            InvalidateVisual();
        });

        /// <summary>The clip curves changed: the lanes are read again and redrawn.</summary>
        void RefreshLanes()
        {
            if (!rows.Any(r => r.Kind == ArrangementRowKind.Parameter)) return;
            ReadLanes();
        }

        // ------------------------------------------------------------------ drawing

        void DrawParameter(DrawingContext ctx, Rect area, ArrangementRow row, double y)
        {
            var lane = LaneOf(row);
            var a = ParameterArea(row);
            var tint = Look.Ruler;
            using (ctx.PushOpacity(0.35)) ctx.FillRectangle(tint, new Rect(area.X, y, area.Width, row.Height));
            var mid = new Pen(Look.TrackLine, 1, DashStyle.Dot);
            ctx.DrawLine(mid, new Point(area.X, a.Y + a.Height / 2), new Point(area.Right, a.Y + a.Height / 2));
            if (lane == null) return;
            var curve = new Pen(Look.Note, 2);
            var steps = new Pen(Look.Off, 1.5);
            foreach (var seg in lane.Segments)
            {
                double x0 = XOfUnits(seg.Start), x1 = XOfUnits(seg.End);
                if (x1 < area.X || x0 > area.Right) continue;
                using var clip = ctx.PushClip(new Rect(Math.Max(area.X, x0), y, Math.Max(1, Math.Min(area.Right, x1) - Math.Max(area.X, x0)), row.Height));
                // The placement: a faint field in the clip's colour, with its start edge.
                var colour = ColourOf(seg.Pattern);
                using (ctx.PushOpacity(seg.Source == AutoSource.Tracker ? 0.08 : 0.2)) ctx.FillRectangle(new SolidColorBrush(colour), new Rect(x0, a.Y - 2, Math.Max(1, x1 - x0), a.Height + 4));
                ctx.FillRectangle(Look.BarLine, new Rect(Math.Floor(x0), a.Y - 2, 1, a.Height + 4));
                DrawCurve(ctx, row, seg, seg.Source == AutoSource.Tracker ? steps : curve);
            }
            foreach (var o in lane.Overlaps)
            {
                double x0 = XOfUnits(o.From), x1 = XOfUnits(o.To);
                if (x1 < area.X || x0 > area.Right) continue;
                var r = new Rect(x0, y + 1, Math.Max(3, x1 - x0), row.Height - 2);
                using (ctx.PushOpacity(0.28)) ctx.FillRectangle(Look.Param, r);
                ctx.FillRectangle(Look.Param, new Rect(r.X, r.Y, r.Width, 3));
                if (r.Width > 56) ctx.DrawText(Text("overlap", Look.BlockText ?? Brushes.Black, 10, true), new Point(r.X + 3, r.Y + 3));
            }
            // The draw ghost.
            if (pdrag == PDrag.Draw && ReferenceEquals(pdragRow, row))
            {
                int s = Math.Min(pdragStartTime, pdragTime), e = Math.Max(pdragStartTime, pdragTime);
                var g = new Rect(geo.XOf(s), a.Y - 2, Math.Max(2, (e - s) * geo.TickWidth), a.Height + 4);
                ctx.DrawRectangle(null, new Pen(Look.SelectionBorder.Brush, 1, DashStyle.Dash), g);
            }
        }

        /// <summary>One placement's curve: the line (ramps and steps as the engine plays them) and its points.</summary>
        void DrawCurve(DrawingContext ctx, ArrangementRow row, AutoSegment seg, Pen pen)
        {
            var pts = seg.Points;
            double endX = XOfUnits(seg.End);
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (!seg.Plays(p.Time)) continue;
                double x = XOfUnits(seg.ToSong(p.Time)), yv = YOfValue(row, p.Value);
                bool hasNext = i + 1 < pts.Count;
                if (p.Ramp && hasNext)
                {
                    var q = pts[i + 1];
                    if (seg.Plays(q.Time)) ctx.DrawLine(pen, new Point(x, yv), new Point(XOfUnits(seg.ToSong(q.Time)), YOfValue(row, q.Value)));
                    else
                    {
                        // The ramp is cut where the placement ends: the line to there.
                        int cut = seg.PatternTo;
                        ctx.DrawLine(pen, new Point(x, yv), new Point(endX, YOfValue(row, AutomationMath.Ramp(p.Time, p.Value, q.Time, q.Value, cut))));
                    }
                }
                else
                {
                    double nx = hasNext && seg.Plays(pts[i + 1].Time) ? XOfUnits(seg.ToSong(pts[i + 1].Time)) : endX;
                    ctx.DrawLine(pen, new Point(x, yv), new Point(nx, yv));
                    if (hasNext && seg.Plays(pts[i + 1].Time)) ctx.DrawLine(pen, new Point(nx, yv), new Point(nx, YOfValue(row, pts[i + 1].Value)));
                }
            }
            for (int i = 0; i < pts.Count; i++)
            {
                if (!seg.Plays(pts[i].Time)) continue;
                var c = new Point(XOfUnits(seg.ToSong(pts[i].Time)), YOfValue(row, pts[i].Value));
                bool sel = ReferenceEquals(seg, selSeg) && i == selIndex;
                var fill = sel ? Look.SelectionBorder.Brush : pen.Brush;
                if (seg.Source == AutoSource.Tracker) ctx.FillRectangle(fill, new Rect(c.X - 2.5, c.Y - 2.5, 5, 5));
                else if (pts[i].Ramp) ctx.DrawEllipse(fill, Look.EventBorder, c, PointRadius, PointRadius);
                else ctx.DrawRectangle(fill, Look.EventBorder, new Rect(c.X - PointRadius, c.Y - PointRadius, 2 * PointRadius, 2 * PointRadius));
            }
        }

        /// <summary>The line of an automation clip on its own lane: its first automation column, across the clip.</summary>
        void DrawAutomationLine(DrawingContext ctx, IPattern p, ClipSegment s, double sx, double sw, double h)
        {
            if (p.Length <= 0) return;
            var column = p.Columns.FirstOrDefault(c => c.Type == PatternColumnType.Parameter && c.Parameter != null && c is PatternColumnCore { EnginePlays: true });
            if (column == null) return;
            Watch(p);
            var pts = AutomationMath.PointsOf(column.GetEvents(int.MinValue, int.MaxValue), true);
            if (pts.Count == 0) return;
            var param = column.Parameter;
            double top = 21, height = Math.Max(4, h - 25);
            double X(int t) => sx + (t - s.Offset * Unit) * geo.TickWidth / Unit;
            double Y(int v) => top + (1 - (param.MaxValue > param.MinValue ? Math.Clamp((v - param.MinValue) / (double)(param.MaxValue - param.MinValue), 0, 1) : 0)) * height;
            double end = sx + sw;
            using var clip = ctx.PushClip(new Rect(sx + 1, 19, Math.Max(0, sw - 2), Math.Max(0, h - 21)));
            var pen = new Pen(Look.Param, 1.5);
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                bool next = i + 1 < pts.Count;
                double x = X(a.Time);
                if (a.Ramp && next) ctx.DrawLine(pen, new Point(x, Y(a.Value)), new Point(X(pts[i + 1].Time), Y(pts[i + 1].Value)));
                else
                {
                    double nx = next ? X(pts[i + 1].Time) : end;
                    ctx.DrawLine(pen, new Point(x, Y(a.Value)), new Point(nx, Y(a.Value)));
                    if (next) ctx.DrawLine(pen, new Point(nx, Y(a.Value)), new Point(nx, Y(pts[i + 1].Value)));
                }
            }
        }

        // ------------------------------------------------------------------ the mouse

        ArrangementRow ParameterRowAt(double y) => rows.FirstOrDefault(r => r.Kind == ArrangementRowKind.Parameter && y >= YOf(r) && y < YOf(r) + r.Height);

        /// <summary>The point under a position, nearest first: its segment and index.</summary>
        (AutoSegment Seg, int Index)? PointAt(ArrangementRow row, Point p)
        {
            var lane = LaneOf(row);
            if (lane == null) return null;
            (AutoSegment, int)? best = null;
            double bestD = PointHit * PointHit;
            foreach (var seg in lane.Segments)
                for (int i = 0; i < seg.Points.Count; i++)
                {
                    if (!seg.Plays(seg.Points[i].Time)) continue;
                    var c = PointPosition(row, seg, i);
                    double d = (c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y);
                    if (d <= bestD) { bestD = d; best = (seg, i); }
                }
            return best;
        }

        /// <summary>The editable curve a click at a song time adds a point to: an automation clip first, else a clip's own column.</summary>
        AutoSegment EditableAt(ArrangementRow row, int songTime) =>
            LaneOf(row)?.Segments.Where(s => s.Editable && s.Covers(songTime)).OrderBy(s => s.Source == AutoSource.AutomationClip ? 0 : 1).FirstOrDefault();

        string ParameterTip(ArrangementRow row, Point p)
        {
            var lane = LaneOf(row);
            if (lane == null) return null;
            int units = UnitsAt(p.X);
            var param = row.Param;
            if (PointAt(row, p) is { } hit)
            {
                var pt = hit.Seg.Points[hit.Index];
                string text = $"{param.Name} {param.Describe(pt.Value)} · {BarBeat(hit.Seg.ToSong(pt.Time) / Unit)} · {(pt.Ramp ? "ramp" : "step")}\n{hit.Seg.Describe}";
                return hit.Seg.Editable ? text + "\nDrag: move · Double click or Delete: remove · R: ramp or step" : text + "\nTracker steps: edit them in the Pattern tab";
            }
            string tip = $"{param.Name} · {BarBeat(units / Unit)}";
            foreach (var o in lane.Overlaps.Where(o => units >= o.From && units < o.To).Take(1))
                tip += $"\nOverlap: {o.A.Describe} and {o.B.Describe} both set this. The later lane's value wins; a tracker column lands after the engine's";
            var under = lane.Segments.Where(s => s.Covers(units)).Select(s => s.ValueAt(units) is { } v ? $"{s.Describe}: {param.Describe(v)}" : null).Where(t => t != null).ToList();
            if (under.Count > 0) tip += "\n" + string.Join("\n", under);
            tip += EditableAt(row, units) != null ? "\nClick: add a point" : "\nDrag: draw an automation clip · Click: a one-bar clip";
            return tip;
        }

        /// <summary>A press on a parameter lane. Returns true when it was handled.</summary>
        bool ParameterPressed(PointerPressedEventArgs e, ArrangementRow row, Point p, KeyModifiers mods)
        {
            bool fine = mods.HasFlag(KeyModifiers.Shift);
            var hit = PointAt(row, p);
            if (hit is { } h)
            {
                SelectPoint(h.Seg, h.Index);
                if (e.ClickCount == 2)
                {
                    SetStatus(Automation.DeletePoint(h.Seg, row.Param.Key, h.Index));
                    SelectPoint(null, -1);
                    Changed();
                    return true;
                }
                if (!h.Seg.Editable)
                {
                    SetStatus(h.Seg.Source == AutoSource.Tracker ? "These steps are the tracker's: edit them in the Pattern tab." : "This curve can't be edited from here.");
                    InvalidateVisual();
                    return true;
                }
                BeginPointDrag(e, row, h.Seg, h.Index, false, p);
                return true;
            }
            SelectPoint(null, -1);
            int units = UnitsAt(p.X);
            var seg = EditableAt(row, units);
            if (seg != null)
            {
                int snapped = fine ? units : SnapUnits(units);
                BeginPointDrag(e, row, seg, -1, true, p);
                pdragTime = Math.Clamp(snapped, seg.Start, seg.End - 1);
                pdragValue = ValueAtY(row, p.Y);
                return true;
            }
            // No clip here: a drag draws one (from a beat to a beat), a click makes one bar.
            pdrag = PDrag.Draw;
            pdragRow = row;
            pdragPress = p;
            pdragStartTime = Snap(units / Unit, fine);
            pdragTime = pdragStartTime;
            pdragValue = ValueAtY(row, p.Y);
            pdragEndValue = pdragValue;
            e.Pointer.Capture(this);
            InvalidateVisual();
            return true;
        }

        int SnapUnits(int units) => Snap((int)Math.Round(units / (double)Unit), false) * Unit;

        void BeginPointDrag(PointerPressedEventArgs e, ArrangementRow row, AutoSegment seg, int index, bool isNew, Point p)
        {
            pdrag = PDrag.Point;
            pdragRow = row;
            pdragSeg = seg;
            pdragIndex = index;
            pdragNew = isNew;
            pdragPress = p;
            if (!isNew)
            {
                pdragTime = seg.ToSong(seg.Points[index].Time);
                pdragValue = seg.Points[index].Value;
            }
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        /// <summary>A move while a parameter drag is on. Returns true when one is.</summary>
        bool ParameterMoved(Point p, KeyModifiers mods)
        {
            if (pdrag == PDrag.None) return false;
            bool fine = mods.HasFlag(KeyModifiers.Shift);
            if (pdrag == PDrag.Point)
            {
                int units = UnitsAt(p.X);
                pdragTime = Math.Clamp(fine ? units : SnapUnits(units), pdragSeg.Start, pdragSeg.End - 1);
                pdragValue = ValueAtY(pdragRow, p.Y);
            }
            else
            {
                pdragTime = Snap(geo.TimeAt(p.X), fine);
                pdragEndValue = ValueAtY(pdragRow, p.Y);
            }
            InvalidateVisual();
            return true;
        }

        int pdragEndValue;

        /// <summary>The release that ends a parameter drag. Returns true when one was on.</summary>
        bool ParameterReleased(bool commit)
        {
            if (pdrag == PDrag.None) return false;
            var d = pdrag;
            pdrag = PDrag.None;
            var row = pdragRow;
            if (commit && row != null)
            {
                string why = null;
                if (d == PDrag.Point)
                {
                    why = pdragNew
                        ? Automation.AddPoint(pdragSeg, row.Param.Key, pdragTime, pdragValue)
                        : Automation.MovePoint(pdragSeg, row.Param.Key, pdragIndex, pdragTime, pdragValue);
                    if (why == null && !pdragNew) SelectPoint(pdragSeg, pdragIndex);      // its time is the one it had; the next reading finds it moved
                    if (why == null && !pdragNew) selId = (pdragSeg.Pattern, pdragSeg.Sequence, pdragSeg.EventTime, pdragSeg.ToPattern(pdragTime));
                }
                else
                {
                    int a = Math.Min(pdragStartTime, pdragTime), b = Math.Max(pdragStartTime, pdragTime);
                    bool dragged = b - a >= TicksPerBeat;
                    int len = dragged ? b - a : core.SessionLayout?.NewClipLength ?? TicksPerBeat * 4;
                    int v0 = pdragValue, v1 = dragged ? pdragEndValue : pdragValue;
                    if (dragged && pdragTime < pdragStartTime) (v0, v1) = (pdragEndValue, pdragValue);
                    why = Automation.DrawClip(row.Machine, row.Param.Key, a, len, v0, v1, out _);
                }
                SetStatus(why);
            }
            pdragSeg = null;
            pdragRow = null;
            Changed();
            return true;
        }

        /// <summary>Keys for the selected point: Delete, and R for ramp or step. Returns true when handled.</summary>
        bool PointKey(Key key, KeyModifiers mods)
        {
            if (selSeg == null || selIndex < 0 || mods != KeyModifiers.None) return false;
            var row = rows.FirstOrDefault(r => r.Kind == ArrangementRowKind.Parameter && LaneOf(r)?.Segments.Contains(selSeg) == true);
            if (row == null) return false;
            string why;
            switch (key)
            {
                case Key.Delete:
                case Key.Back:
                    why = Automation.DeletePoint(selSeg, row.Param.Key, selIndex);
                    SelectPoint(null, -1);
                    break;
                case Key.R:
                    why = Automation.SetRamp(selSeg, row.Param.Key, selIndex, !selSeg.Points[selIndex].Ramp);
                    break;
                default: return false;
            }
            SetStatus(why);
            Changed();
            return true;
        }
    }
}
